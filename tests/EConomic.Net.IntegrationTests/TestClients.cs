using System.Net.Http;
using EConomic.Authentication;
using EConomic.Http;
using Xunit;

namespace EConomic.IntegrationTests;

/// <summary>
/// Builds the client the integration tests run against, and gates them on having an agreement.
/// </summary>
/// <remarks>
/// <para>
/// Every test here — reads included — runs against one agreement of your own, configured through
/// <c>ECONOMIC_APP_SECRET_TOKEN</c> and <c>ECONOMIC_AGREEMENT_GRANT_TOKEN</c>. They used to read
/// from the public <c>demo</c> agreement, which is one shared agreement addressed by everyone
/// reading e-conomic's documentation. That had two problems, and isolation fixes both.
/// </para>
/// <para>
/// The rate limit is the visible one: sampling <c>X-RateLimiting</c> there showed the budget moving
/// between 58 and 352 of 10 000 with no calls of our own in between, so a burst from elsewhere
/// produced a <c>429</c> that outlived any retry policy, at a moment unrelated to the code under
/// test. The subtler one is that reading a shared agreement means asserting on data nobody controls
/// — "five customers, numbered 1 to 5" is a fact about someone else's records. Tests now create
/// what they need and delete it again, so they assert on data they put there.
/// </para>
/// <para>
/// Point this at a throwaway agreement. The tests write to it.
/// </para>
/// </remarks>
internal static class TestClients
{
    /// <summary>Environment variable holding the application's token.</summary>
    public const string AppSecretVariable = "ECONOMIC_APP_SECRET_TOKEN";

    /// <summary>Environment variable holding the agreement's token.</summary>
    public const string AgreementGrantVariable = "ECONOMIC_AGREEMENT_GRANT_TOKEN";

    /// <summary>Environment variable that opts into running against a live agreement.</summary>
    public const string OptInVariable = "ECONOMIC_RUN_INTEGRATION_TESTS";

    /// <summary>A client for the configured agreement.</summary>
    /// <returns>A configured client.</returns>
    public static EconomicClient Create() =>
        new(
            new HttpClient(Pipeline(Options()))
            {
                BaseAddress = EconomicOptions.DefaultRestApiBaseAddress,
                Timeout = TimeSpan.FromSeconds(60),
            },
            Options());

    /// <summary>A raw transport, for tests that inspect responses rather than mapped models.</summary>
    /// <returns>A configured transport, without a base address.</returns>
    public static HttpClient CreateTransport() =>
        new(Pipeline(Options())) { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Skips unless an agreement is configured and the tests are opted into.</summary>
    public static void SkipUnlessConfigured()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable(OptInVariable) is not "1",
            $"Set {OptInVariable}=1 to run against a live agreement.");

        // Falling back to the demo tokens would be worse than skipping: the demo agreement rejects
        // every write, so the seeding each test starts with would fail and report itself as a bug
        // in the library.
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppSecretVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AgreementGrantVariable)),
            $"Set {AppSecretVariable} and {AgreementGrantVariable} to a throwaway agreement's tokens.");
    }

    private static EconomicOptions Options() => new()
    {
        AppSecretToken = Environment.GetEnvironmentVariable(AppSecretVariable)!,
        AgreementGrantToken = Environment.GetEnvironmentVariable(AgreementGrantVariable)!,
    };

    /// <summary>
    /// Idempotency outside retry, so a key is assigned once and every attempt reuses it. The order
    /// is the same one <c>AddEconomicClient</c> depends on, which is the point: a bug in that order
    /// should show up here rather than only in production.
    /// </summary>
    private static EconomicIdempotencyHandler Pipeline(EconomicOptions options) =>
        new(options)
        {
            InnerHandler = new EconomicRetryHandler(options.Retry)
            {
                InnerHandler = new EconomicAuthenticationHandler(options)
                {
                    InnerHandler = new HttpClientHandler(),
                },
            },
        };
}
