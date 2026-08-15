using System.Net;
using System.Net.Http;
using EConomic.Authentication;
using EConomic.Exceptions;
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

    /// <summary>
    /// How long a call may take before it is abandoned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not a per-request timeout. <see cref="HttpClient.Timeout"/> bounds the whole
    /// <c>SendAsync</c>, and the retry handler sits inside it — so one value has to cover the first
    /// attempt, every retry, and the backoff between them. At sixty seconds a single slow response
    /// left nothing for a retry, and a run of the live suite failed twice on
    /// <see cref="TaskCanceledException"/> at points unrelated to the code under test, then passed
    /// unchanged on a re-run.
    /// </para>
    /// <para>
    /// Three minutes is deliberately generous. A timeout here is a blunt instrument: it cannot tell
    /// a hung connection from e-conomic being slow, and the failure it produces names neither. The
    /// retry policy is what handles a genuinely failed call; this only stops a run from hanging for
    /// ever.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>A client for the configured agreement.</summary>
    /// <returns>A configured client.</returns>
    public static EconomicClient Create() =>
        new(
            new HttpClient(Pipeline(Options()))
            {
                BaseAddress = EconomicOptions.DefaultRestApiBaseAddress,
                Timeout = Timeout,
            },
            Options());

    /// <summary>A client for e-conomic's public demo agreement, read-only.</summary>
    /// <returns>A configured client.</returns>
    /// <remarks>
    /// <para>
    /// Everything else here runs against an agreement of your own, for the reasons above. This is
    /// the one exception, and it exists because e-conomic sells modules separately: the projects
    /// service answers <c>403</c> for every collection but employees on an agreement without the
    /// Project module, and the demo agreement has it. Without this, sixteen collections would ship
    /// filter and sort surfaces that had never been sent to a server — the exact gap these tests
    /// exist to close.
    /// </para>
    /// <para>
    /// Only the surface probes use it, and only to read. They assert that the server parses a query,
    /// never on what it returns, so the objection to a shared agreement — asserting on data nobody
    /// controls — does not apply. Its shared rate limit still does, which is why nothing else
    /// reaches for this.
    /// </para>
    /// </remarks>
    public static EconomicClient CreateDemo()
    {
        var demo = new EconomicOptions { AppSecretToken = "demo", AgreementGrantToken = "demo" };

        return new EconomicClient(
            new HttpClient(Pipeline(demo))
            {
                BaseAddress = EconomicOptions.DefaultRestApiBaseAddress,
                Timeout = Timeout,
            },
            demo);
    }

    /// <summary>A raw transport, for tests that inspect responses rather than mapped models.</summary>
    /// <returns>A configured transport, without a base address.</returns>
    public static HttpClient CreateTransport() =>
        new(Pipeline(Options())) { Timeout = Timeout };

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

    /// <summary>
    /// The module an agreement is missing, if that is why a call was refused.
    /// </summary>
    /// <param name="exception">The exception a call threw.</param>
    /// <returns>The missing module's name, or <see langword="null"/> if this is any other failure.</returns>
    /// <remarks>
    /// <para>
    /// e-conomic sells its modules separately, and a resource belonging to one the agreement has not
    /// bought answers <c>403</c> with <c>AccessDeniedAgreementMissingModules</c> — the projects
    /// service does this for every collection but employees and employee groups. That is a fact about
    /// the agreement, not about the client, so the sweeps record it and carry on rather than failing.
    /// </para>
    /// <para>
    /// Matched on the error code and the module named in the title, never on the status alone: a
    /// bare <c>403</c> is what a wrong token also produces, and swallowing that would turn an
    /// authentication bug into a green run.
    /// </para>
    /// </remarks>
    public static string? MissingModule(Exception? exception)
    {
        if (exception is not EconomicApiException
            {
                StatusCode: HttpStatusCode.Forbidden,
                ProblemDetails.ErrorCode: "AccessDeniedAgreementMissingModules",
            } refused)
        {
            return null;
        }

        var title = refused.ProblemDetails?.Title ?? string.Empty;
        var marker = title.IndexOf("Missing modules:", StringComparison.Ordinal);

        return marker < 0
            ? "unnamed"
            : title[(marker + "Missing modules:".Length)..].Trim().TrimEnd('.');
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
