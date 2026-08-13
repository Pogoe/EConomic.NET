using System.Net;
using System.Net.Http;
using EConomic.Exceptions;
using Xunit;

namespace EConomic.Tests;

public class EconomicApiExceptionTests
{
    // Captured verbatim from a live 404 on 2026-08-12.
    private const string LiveProblemJson = """
        {
          "type": "https://tools.ietf.org/html/rfc9110#section-15.5.5",
          "title": "Resource not found.",
          "status": 404,
          "detail": "The resource you have been looking for does not exist.",
          "errors": [],
          "traceId": "a2a259bbe959d8d5-CPH",
          "traceTimeUtc": "2026-08-12T20:50:07",
          "errorCode": "ResourceNotFound"
        }
        """;

    [Fact]
    public async Task FromResponseAsync_maps_a_problem_json_body()
    {
        using var response = CreateResponse(HttpStatusCode.NotFound, LiveProblemJson);
        response.Headers.TryAddWithoutValidation(EconomicApiException.RequestIdHeader, "a2a25bfb88b1f4bd-CPH");

        var exception = await EconomicApiException.FromResponseAsync(response, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal("ResourceNotFound", exception.ErrorCode);
        Assert.Equal("a2a259bbe959d8d5-CPH", exception.TraceId);
        Assert.Equal("a2a25bfb88b1f4bd-CPH", exception.RequestId);
        Assert.Contains("404", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ResourceNotFound", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FromResponseAsync_returns_a_rate_limit_exception_for_429()
    {
        using var response = CreateResponse(HttpStatusCode.TooManyRequests, "{}");
        response.Headers.TryAddWithoutValidation(
            Http.RateLimitStatus.RateLimitingHeader,
            "token-limit-10000-per-60-seconds: 10000/10000");
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));

        var exception = await EconomicApiException.FromResponseAsync(response, TestContext.Current.CancellationToken);

        var rateLimited = Assert.IsType<EconomicRateLimitException>(exception);
        Assert.Equal(TimeSpan.FromSeconds(30), rateLimited.RetryAfter);
        Assert.Equal(0, rateLimited.RateLimit!.Remaining);
    }

    [Fact]
    public async Task FromResponseAsync_survives_a_body_that_is_not_problem_json()
    {
        using var response = CreateResponse(HttpStatusCode.BadGateway, "<html>gateway exploded</html>");

        var exception = await EconomicApiException.FromResponseAsync(response, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Null(exception.ProblemDetails);
        Assert.Equal("<html>gateway exploded</html>", exception.RawBody);
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string body) =>
        new(statusCode)
        {
            Content = new StringContent(body),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://restapi.e-conomic.com/customers"),
        };
}
