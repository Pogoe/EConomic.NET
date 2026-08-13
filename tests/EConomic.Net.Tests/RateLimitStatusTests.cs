using System.Net;
using System.Net.Http;
using EConomic.Http;
using Xunit;

namespace EConomic.Tests;

public class RateLimitStatusTests
{
    // Captured verbatim from a live response on 2026-08-12.
    private const string LiveHeaderValue = "token-limit-10000-per-60-seconds: 147/10000";

    [Fact]
    public void TryParse_reads_a_live_header_value()
    {
        Assert.True(RateLimitStatus.TryParse(LiveHeaderValue, out var status));

        Assert.Equal(10_000, status.Limit);
        Assert.Equal(TimeSpan.FromSeconds(60), status.Window);
        Assert.Equal(147, status.Used);
        Assert.Equal(9_853, status.Remaining);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("token-limit-per-seconds: /")]
    public void TryParse_rejects_unusable_values(string? headerValue)
    {
        Assert.False(RateLimitStatus.TryParse(headerValue, out var status));
        Assert.Null(status);
    }

    [Fact]
    public void Remaining_never_goes_negative_when_the_bucket_is_overdrawn()
    {
        Assert.True(RateLimitStatus.TryParse("token-limit-2000-per-60-seconds: 2500/2000", out var status));

        Assert.Equal(0, status.Remaining);
        Assert.Equal(1d, status.UsedFraction);
    }

    [Fact]
    public void FromResponse_combines_the_budget_with_the_call_cost()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation(RateLimitStatus.RateLimitingHeader, LiveHeaderValue);
        response.Headers.TryAddWithoutValidation(RateLimitStatus.CallCostHeader, "3");

        var status = RateLimitStatus.FromResponse(response);

        Assert.NotNull(status);
        Assert.Equal(3, status.CallCost);
        Assert.Equal(147, status.Used);
    }

    [Fact]
    public void FromResponse_returns_null_when_the_response_carries_no_budget()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);

        Assert.Null(RateLimitStatus.FromResponse(response));
    }
}
