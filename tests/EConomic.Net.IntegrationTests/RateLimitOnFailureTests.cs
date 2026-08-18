using EConomic.Exceptions;
using Xunit;

namespace EConomic.IntegrationTests;

/// <summary>
/// Checks that the rate-limit budget reaches the exception when a <em>generated</em> call fails.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RateLimitHeaderTests"/> covers the header on a success, read off the response by hand
/// through a raw transport. This covers the path every call in the library actually takes: a
/// generated client raises its own failure type carrying a header dictionary, the response object is
/// gone by the time the facade translates it, and the budget has to come out of that dictionary or
/// not at all. It did not — every exception but the hand-written <c>DELETE</c> reported none, and
/// nothing noticed, because a null budget is also what a response carrying no header looks like.
/// </para>
/// <para>
/// Live rather than stubbed because a unit test can only prove the parser reads a header the stub
/// invented. Whether e-conomic sends <c>X-RateLimiting</c> on a rejected request at all is a fact
/// about the server, and that is the fact the fix depends on.
/// </para>
/// </remarks>
public class RateLimitOnFailureTests
{
    [Fact]
    public async Task A_rejected_legacy_filter_still_reports_the_budget()
    {
        TestClients.SkipUnlessConfigured();

        var client = TestClients.Create();

        var exception = await Assert.ThrowsAnyAsync<EconomicApiException>(
            () => client.Rest.Customers
                .WhereRaw("bogusProp$eq:1")
                .GetPageAsync(0, TestContext.Current.CancellationToken));

        // The 400 itself is incidental; that it arrives carrying a readable budget is the claim.
        Assert.NotEmpty(exception.AllowedFilteringFields);
        Assert.NotNull(exception.RateLimit);
        Assert.True(exception.RateLimit.Limit > 0, "A failure should still report the window's limit.");
        Assert.True(exception.RateLimit.Used > 0, "A rejected call still spends budget.");
    }

    [Fact]
    public async Task A_rejected_open_filter_still_reports_the_budget()
    {
        TestClients.SkipUnlessConfigured();

        // Both surfaces spend one agreement-wide bucket, so both transports have to read it back.
        var client = TestClients.Create();

        var exception = await Assert.ThrowsAnyAsync<EconomicApiException>(
            () => client.Open.Customers
                .WhereRaw("bogusProp$eq:1")
                .GetPageAsync(0, TestContext.Current.CancellationToken));

        Assert.NotNull(exception.RateLimit);
        Assert.True(exception.RateLimit.Limit > 0, "A failure should still report the window's limit.");
    }

    [Fact]
    public async Task A_rejected_legacy_call_reports_what_it_cost()
    {
        TestClients.SkipUnlessConfigured();

        var client = TestClients.Create();

        var exception = await Assert.ThrowsAnyAsync<EconomicApiException>(
            () => client.Rest.Customers
                .WhereRaw("bogusProp$eq:1")
                .GetPageAsync(0, TestContext.Current.CancellationToken));

        // Separated from the budget because it is a second, weaker claim: X-CallCost is a different
        // header, and a server may report the window without pricing a request it refused to run.
        Assert.NotNull(exception.RateLimit?.CallCost);
    }
}
