using System.Net;
using System.Net.Http;
using System.Text;
using EConomic.Authentication;
using EConomic.Exceptions;
using EConomic.Http;
using Xunit;

namespace EConomic.Tests;

/// <summary>
/// Covers the budget reaching the exception from a <em>generated</em> call.
/// </summary>
/// <remarks>
/// Every call but the hand-written legacy <c>DELETE</c> goes through a generated client, and those
/// hand the facade a header dictionary rather than the response. The budget was dropped on all of
/// them: the only path that ever populated it was
/// <see cref="EconomicApiException.FromResponseAsync"/>, which the delete alone calls. A green
/// suite said nothing, because <see cref="EconomicApiException.RateLimit"/> being null is exactly
/// what a response carrying no header looks like.
/// </remarks>
public class RateLimitReportingTests
{
    private const string Budget = "token-limit-10000-per-60-seconds: 9997/10000";

    [Fact]
    public async Task A_failure_from_a_generated_call_carries_the_budget()
    {
        var client = CreateClient(new StubHandler(HttpStatusCode.BadRequest, (RateLimitStatus.RateLimitingHeader, Budget)));

        var exception = await Assert.ThrowsAsync<EconomicApiException>(
            () => client.Rest.Customers.GetPageAsync(0, TestContext.Current.CancellationToken));

        Assert.Equal(9997, exception.RateLimit!.Used);
        Assert.Equal(3, exception.RateLimit.Remaining);
    }

    [Fact]
    public async Task A_throttled_generated_call_carries_the_budget()
    {
        var client = CreateClient(new StubHandler(
            HttpStatusCode.TooManyRequests,
            (RateLimitStatus.RateLimitingHeader, "token-limit-10000-per-60-seconds: 10000/10000")));

        var exception = await Assert.ThrowsAsync<EconomicRateLimitException>(
            () => client.Rest.Customers.GetPageAsync(0, TestContext.Current.CancellationToken));

        // The distinct exception exists to expose the budget; without one it reports nothing the
        // caller cannot already see from the status code.
        Assert.Equal(0, exception.RateLimit!.Remaining);
    }

    [Fact]
    public async Task The_open_surface_carries_the_budget_too()
    {
        var client = CreateClient(new StubHandler(HttpStatusCode.BadRequest, (RateLimitStatus.RateLimitingHeader, Budget)));

        var exception = await Assert.ThrowsAsync<EconomicApiException>(
            () => client.Open.Customers.AsQuery().GetPageAsync(0, TestContext.Current.CancellationToken));

        // One agreement-wide bucket serves both hosts, so both transports have to report it.
        Assert.Equal(9997, exception.RateLimit!.Used);
    }

    [Fact]
    public async Task The_call_cost_is_carried_alongside_the_budget()
    {
        var client = CreateClient(new StubHandler(
            HttpStatusCode.BadRequest,
            (RateLimitStatus.RateLimitingHeader, Budget),
            (RateLimitStatus.CallCostHeader, "3")));

        var exception = await Assert.ThrowsAsync<EconomicApiException>(
            () => client.Rest.Customers.GetPageAsync(0, TestContext.Current.CancellationToken));

        Assert.Equal(3, exception.RateLimit!.CallCost);
    }

    [Fact]
    public async Task The_header_is_matched_whatever_case_it_arrives_in()
    {
        // HTTP field names are case-insensitive and HTTP/2 lowercases them on the wire, while the
        // generated clients key an ordinary Dictionary by whatever arrived. An exact-match lookup
        // would report no budget rather than fail, which nothing downstream could notice.
        var client = CreateClient(new StubHandler(HttpStatusCode.BadRequest, ("x-ratelimiting", Budget)));

        var exception = await Assert.ThrowsAsync<EconomicApiException>(
            () => client.Rest.Customers.GetPageAsync(0, TestContext.Current.CancellationToken));

        Assert.Equal(9997, exception.RateLimit!.Used);
    }

    [Fact]
    public async Task A_response_carrying_no_budget_reports_none()
    {
        var client = CreateClient(new StubHandler(HttpStatusCode.BadRequest));

        var exception = await Assert.ThrowsAsync<EconomicApiException>(
            () => client.Rest.Customers.GetPageAsync(0, TestContext.Current.CancellationToken));

        Assert.Null(exception.RateLimit);
    }

    [Fact]
    public void A_rate_limit_exception_built_from_an_inner_exception_still_reports_429()
    {
        // The two-argument base constructor sets no status, so this one has to supply it. A
        // throttling exception reporting no status contradicts its own type.
        var exception = new EconomicRateLimitException("throttled", new InvalidOperationException("cause"));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public void Every_rate_limit_constructor_agrees_on_the_status() =>
        Assert.All(
            new EconomicRateLimitException[]
            {
                new("throttled"),
                new("throttled", new InvalidOperationException()),
                new("throttled", problemDetails: null),
            },
            exception => Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode));

    private static EconomicClient CreateClient(StubHandler handler) =>
        new(new HttpClient(handler), EconomicOptions.Demo());

    private sealed class StubHandler(HttpStatusCode status, params (string Name, string Value)[] headers)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };

            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return Task.FromResult(response);
        }
    }
}
