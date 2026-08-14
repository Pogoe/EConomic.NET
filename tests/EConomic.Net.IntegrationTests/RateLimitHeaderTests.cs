using System.Net;
using System.Net.Http;
using EConomic.Authentication;
using EConomic.Http;
using Xunit;

namespace EConomic.IntegrationTests;

/// <summary>
/// Read-only smoke tests against e-conomic's public demo agreement.
/// </summary>
/// <remarks>
/// These hit the live service, so they are opt-in: set <c>ECONOMIC_RUN_INTEGRATION_TESTS=1</c>.
/// They exist to catch e-conomic changing something under us, and are scheduled in CI rather than
/// run on every pull request. The demo agreement rejects writes, so never add a non-GET test here.
/// </remarks>
public class DemoAgreementTests
{
    private const string OptInVariable = "ECONOMIC_RUN_INTEGRATION_TESTS";

    [Fact]
    public async Task Legacy_rest_api_answers_with_a_rate_limit_budget()
    {
        SkipUnlessOptedIn();

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
        SkipUnlessOptedIn();

        using var client = CreateClient();
        using var response = await client.GetAsync(
            new Uri("https://apis.e-conomic.com/customersapi/v3.1.0/customers?pageSize=1"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rateLimit = RateLimitStatus.FromResponse(response);
        Assert.NotNull(rateLimit);
        Assert.True(rateLimit.Limit > 0);
    }

    private static void SkipUnlessOptedIn() =>
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable(OptInVariable) is not "1",
            $"Set {OptInVariable}=1 to run tests against the live demo agreement.");

    private static HttpClient CreateClient() =>
        new(new EconomicAuthenticationHandler(EconomicOptions.Demo())
        {
            InnerHandler = new HttpClientHandler(),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
}
