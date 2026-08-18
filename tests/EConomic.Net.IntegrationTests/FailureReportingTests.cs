using EConomic.Exceptions;
using Xunit;

namespace EConomic.IntegrationTests;

/// <summary>
/// Checks what a rejected request reports beyond the rate-limit budget.
/// </summary>
/// <remarks>
/// Live for the same reason <see cref="RateLimitOnFailureTests"/> is: a unit test can only prove
/// the transport reads back a header or a field that a stub invented. Whether e-conomic sends an
/// <c>errorCode</c> or an <c>X-RequestId</c> on a rejected call is a fact about the server, and it
/// is what decides whether putting them in the message is worth anything.
/// </remarks>
public class FailureReportingTests
{
    [Fact]
    public async Task A_rejected_open_call_names_its_error_code_in_the_message()
    {
        TestClients.SkipUnlessConfigured();

        var client = TestClients.Create();

        var exception = await Assert.ThrowsAnyAsync<EconomicApiException>(
            () => client.Open.Customers
                .WhereRaw("bogusProp$eq:1")
                .GetPageAsync(0, TestContext.Current.CancellationToken));

        // These services publish an errorCode and index their documentation by it, so the message
        // is where it earns its keep — a caller pastes the message into a search, not the exception.
        Assert.NotNull(exception.ErrorCode);
        Assert.Contains($"({exception.ErrorCode})", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_legacy_call_still_reports_the_request_identifier()
    {
        TestClients.SkipUnlessConfigured();

        var client = TestClients.Create();

        var exception = await Assert.ThrowsAnyAsync<EconomicApiException>(
            () => client.Rest.Customers
                .WhereRaw("bogusProp$eq:1")
                .GetPageAsync(0, TestContext.Current.CancellationToken));

        // This is the identifier e-conomic's support asks for, and it comes out of the same header
        // dictionary the budget does. The transport negotiates HTTP/1.1 today, so the casing here
        // is whatever e-conomic sent; the reader is case-insensitive so that stays true if it moves.
        Assert.False(string.IsNullOrWhiteSpace(exception.RequestId));
    }

    [Fact]
    public async Task A_rejected_legacy_call_names_the_offending_property()
    {
        TestClients.SkipUnlessConfigured();

        var client = TestClients.Create();

        var exception = await Assert.ThrowsAnyAsync<EconomicApiException>(
            () => client.Rest.Customers
                .WhereRaw("bogusProp$eq:1")
                .GetPageAsync(0, TestContext.Current.CancellationToken));

        // The legacy per-property errors survived the message builders being consolidated. The
        // leading "Could not parse query string filter." says nothing on its own.
        Assert.Contains("bogusProp", exception.Message, StringComparison.Ordinal);
    }
}
