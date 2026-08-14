using System.Net;
using System.Net.Http;
using EConomic.Http;
using Xunit;

namespace EConomic.IntegrationTests;

/// <summary>
/// Checks that e-conomic still reports its rate-limit budget the way the parser expects.
/// </summary>
/// <remarks>
/// Both API surfaces are covered because they are separate services that happen to agree on the
/// header format today. A unit test pins the parser against a recorded string; only these notice if
/// the server starts sending a different one.
/// </remarks>
public class RateLimitHeaderTests
{
    [Fact]
    public async Task Legacy_rest_api_answers_with_a_rate_limit_budget()
    {
        TestClients.SkipUnlessConfigured();

        using var client = CreateClient();
        using var response = await client.GetAsync(
            new Uri("https://restapi.e-conomic.com/customers?pagesize=1"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rateLimit = RateLimitStatus.FromResponse(response);
        Assert.NotNull(rateLimit);
        Assert.True(rateLimit.Limit > 0);
        Assert.NotNull(rateLimit.CallCost);
    }

    [Fact]
    public async Task Open_api_service_answers_with_a_rate_limit_budget()
    {
        TestClients.SkipUnlessConfigured();

        // The newer services authenticate with the same two headers, which is the main reason both
        // surfaces can share one handler pipeline. This is where that stops being an assumption.
        using var client = CreateClient();
        using var response = await client.GetAsync(
            new Uri("https://apis.e-conomic.com/customersapi/v3.1.0/customers?pageSize=1"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rateLimit = RateLimitStatus.FromResponse(response);
        Assert.NotNull(rateLimit);
        Assert.True(rateLimit.Limit > 0);
    }

    private static HttpClient CreateClient() => TestClients.CreateTransport();
}
