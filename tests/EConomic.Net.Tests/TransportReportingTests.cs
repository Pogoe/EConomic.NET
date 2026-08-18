using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using EConomic.Authentication;
using EConomic.Exceptions;
using EConomic.Http;
using EConomic.Open;
using Xunit;

namespace EConomic.Tests;

/// <summary>
/// Covers what a failure from a <em>generated</em> call reports, beyond the rate-limit budget.
/// </summary>
/// <remarks>
/// The same class of defect as the budget: the facade transports read a header dictionary rather
/// than a response, and each thing read out of it was read slightly differently. Nothing downstream
/// can notice a correlation identifier that silently went missing, or an error code that was never
/// put in the message, so only a test that looks for them settles it.
/// </remarks>
public class TransportReportingTests
{
    private const string Problem =
        """{"title":"Bad Request","detail":"customerNumber is required","errorCode":"E12345"}""";

    [Theory]
    [InlineData(EconomicApiException.RequestIdHeader)]
    [InlineData("x-requestid")]
    [InlineData("X-REQUESTID")]
    public async Task The_request_identifier_survives_whatever_case_it_arrives_in(string header)
    {
        // This is the value e-conomic's support asks for. HTTP field names are case-insensitive and
        // HTTP/2 lowercases them, while the generated clients key a plain Dictionary by whatever
        // arrived — so an exact-match read loses it the day anything negotiates HTTP/2.
        var client = CreateClient(new StubHandler(HttpStatusCode.BadRequest, (header, "abc-123")));

        var exception = await Assert.ThrowsAsync<EconomicApiException>(
            () => client.Rest.Customers.GetPageAsync(0, TestContext.Current.CancellationToken));

        Assert.Equal("abc-123", exception.RequestId);
    }

    [Fact]
    public async Task The_open_surface_keeps_the_request_identifier_too()
    {
        var client = CreateClient(new StubHandler(HttpStatusCode.BadRequest, ("x-requestid", "abc-123")));

        var exception = await Assert.ThrowsAsync<EconomicApiException>(
            () => client.Open.Customers.AsQuery().GetPageAsync(0, TestContext.Current.CancellationToken));

        Assert.Equal("abc-123", exception.RequestId);
    }

    [Fact]
    public async Task A_generated_failure_names_the_error_code_in_its_message()
    {
        // e-conomic's documentation is indexed by this code, so a caller pasting the message into a
        // search finds the cause. Only the hand-written DELETE path ever included it.
        var client = CreateClient(new StubHandler(HttpStatusCode.BadRequest) { Body = Problem });

        var exception = await Assert.ThrowsAsync<EconomicApiException>(
            () => client.Rest.Customers.GetPageAsync(0, TestContext.Current.CancellationToken));

        Assert.Contains("(E12345)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("customerNumber is required", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_open_surface_names_the_error_code_too()
    {
        var client = CreateClient(new StubHandler(HttpStatusCode.BadRequest) { Body = Problem });

        var exception = await Assert.ThrowsAsync<EconomicApiException>(
            () => client.Open.Customers.AsQuery().GetPageAsync(0, TestContext.Current.CancellationToken));

        Assert.Contains("(E12345)", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task All_three_message_builders_agree_on_their_shape()
    {
        // There were three copies of this and they had drifted. Comparing the generated-call
        // message with the hand-written one is what keeps a fourth divergence from going unnoticed.
        var client = CreateClient(new StubHandler(HttpStatusCode.BadRequest) { Body = Problem });

        var generated = await Assert.ThrowsAsync<EconomicApiException>(
            () => client.Rest.Customers.GetPageAsync(0, TestContext.Current.CancellationToken));

        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(Problem, Encoding.UTF8, "application/problem+json"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://restapi.e-conomic.com/customers"),
        };

        var direct = await EconomicApiException.FromResponseAsync(response, TestContext.Current.CancellationToken);

        Assert.StartsWith("e-conomic returned 400 (E12345) for ", generated.Message, StringComparison.Ordinal);
        Assert.StartsWith("e-conomic returned 400 (E12345) for ", direct.Message, StringComparison.Ordinal);
        Assert.EndsWith(": customerNumber is required", generated.Message, StringComparison.Ordinal);
        Assert.EndsWith(": customerNumber is required", direct.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_retry_after_given_as_a_date_becomes_a_delay()
    {
        // RFC 9110 gives Retry-After two forms and .NET parses them into different properties.
        // Reading only Delta ignored the date form entirely: the server had said how long to wait
        // and the handler fell back to its own curve as though nothing had been sent.
        var now = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), HeaderReading.RetryAfter(response.Headers, now));
    }

    [Fact]
    public void A_retry_after_date_already_past_asks_for_no_wait()
    {
        var now = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(-30));

        // Zero rather than a negative delay, so no caller has to special-case it.
        Assert.Equal(TimeSpan.Zero, HeaderReading.RetryAfter(response.Headers, now));
    }

    [Fact]
    public void The_delta_form_of_retry_after_still_wins()
    {
        var now = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));

        Assert.Equal(TimeSpan.FromSeconds(7), HeaderReading.RetryAfter(response.Headers, now));
    }

    [Fact]
    public void No_retry_after_asks_for_nothing()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        Assert.Null(HeaderReading.RetryAfter(response.Headers, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_throttling_response_dated_rather_than_delayed_still_reports_a_wait()
    {
        var now = DateTimeOffset.UtcNow;
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/problem+json"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://restapi.e-conomic.com/customers"),
        };

        response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddMinutes(5));

        var exception = Assert.IsType<EconomicRateLimitException>(
            await EconomicApiException.FromResponseAsync(response, TestContext.Current.CancellationToken));

        // Bounded rather than exact: the header carries whole seconds and the clock moves between
        // building the response and reading it.
        Assert.NotNull(exception.RetryAfter);
        Assert.InRange(exception.RetryAfter!.Value, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void A_required_enum_refuses_to_default_when_nothing_was_supplied()
    {
        // The recurring write bug: `default` is the first member the generator happened to emit, so
        // this compiles, round-trips, and sends e-conomic a value the caller never chose.
        Assert.Throws<ArgumentNullException>(() => OpenTransport.ParseEnum<DayOfWeek>(null));

        // The optional shape is the one allowed to answer "unset".
        Assert.Null(OpenTransport.ParseOptionalEnum<DayOfWeek>(null));
        Assert.Equal(DayOfWeek.Friday, OpenTransport.ParseEnum<DayOfWeek>("Friday"));
    }

    [Fact]
    public void Each_surface_is_built_once_and_kept()
    {
        var client = CreateClient(new StubHandler(HttpStatusCode.OK));

        // Reached in a loop by anything that pages, and both are immutable wrappers over the
        // transport, so returning a fresh one per read was allocation for nothing.
        Assert.Same(client.Rest, client.Rest);
        Assert.Same(client.Open, client.Open);
    }

    private static EconomicClient CreateClient(StubHandler handler) =>
        new(new HttpClient(handler), EconomicOptions.Demo());

    private sealed class StubHandler(HttpStatusCode status, params (string Name, string Value)[] headers)
        : HttpMessageHandler
    {
        public string Body { get; init; } = "{}";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
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
