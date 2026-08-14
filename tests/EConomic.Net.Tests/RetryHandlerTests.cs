using System.Net;
using System.Net.Http;
using System.Text;
using EConomic.Authentication;
using EConomic.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace EConomic.Tests;

public class RetryHandlerTests
{
    private static readonly EconomicRetryOptions Fast = new()
    {
        MaxAttempts = 3,
        BaseDelay = TimeSpan.FromMilliseconds(10),
        MaxDelay = TimeSpan.FromMilliseconds(50),
    };

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public void Only_throttling_and_transient_server_failures_are_transient(HttpStatusCode status, bool expected) =>
        Assert.Equal(expected, EconomicRetryHandler.IsTransient(status));

    [Fact]
    public void Idempotent_methods_are_always_safe_to_retry()
    {
        // DELETE is deliberately absent: e-conomic documents it as non-idempotent, so it is
        // covered by A_delete_without_an_idempotency_key_is_never_retried instead.
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Head })
        {
            using var request = new HttpRequestMessage(method, "https://restapi.e-conomic.com/customers");
            Assert.True(EconomicRetryHandler.IsSafeToRetry(request), $"{method} should be retryable.");
        }
    }

    [Fact]
    public void A_delete_without_an_idempotency_key_is_never_retried()
    {
        // HTTP says DELETE is idempotent; e-conomic says otherwise. Its drafts and sent endpoints
        // document that "on the consecutive calls it will be returning status code 404", so a
        // retry after a lost response would report failure for a delete that actually succeeded.
        using var request = new HttpRequestMessage(HttpMethod.Delete, "https://restapi.e-conomic.com/customers/1");

        Assert.False(EconomicRetryHandler.IsSafeToRetry(request));
    }

    [Fact]
    public void A_delete_with_an_idempotency_key_is_retryable()
    {
        // With a key the server replays the original result rather than deleting again.
        using var request = new HttpRequestMessage(HttpMethod.Delete, "https://restapi.e-conomic.com/customers/1");
        request.Headers.TryAddWithoutValidation(EconomicIdempotencyHandler.IdempotencyKeyHeader, "a-key");

        Assert.True(EconomicRetryHandler.IsSafeToRetry(request));
    }

    [Fact]
    public void A_post_without_an_idempotency_key_is_never_retried()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://restapi.e-conomic.com/customers");

        Assert.False(EconomicRetryHandler.IsSafeToRetry(request));
    }

    [Fact]
    public void A_post_with_an_idempotency_key_is_retryable()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://restapi.e-conomic.com/customers");
        request.Headers.TryAddWithoutValidation(EconomicIdempotencyHandler.IdempotencyKeyHeader, "key-1");

        Assert.True(EconomicRetryHandler.IsSafeToRetry(request));
    }

    [Fact]
    public async Task A_throttled_request_is_retried_until_it_succeeds()
    {
        var recorder = new SequenceHandler(
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.OK);

        using var response = await SendAsync(recorder, HttpMethod.Get);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, recorder.Attempts);
    }

    [Fact]
    public async Task Retries_stop_at_the_configured_attempt_limit()
    {
        var recorder = new SequenceHandler(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK);

        using var response = await SendAsync(recorder, HttpMethod.Get);

        // Three attempts total, so the fourth response is never reached.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, recorder.Attempts);
    }

    [Fact]
    public async Task A_non_transient_failure_is_returned_immediately()
    {
        var recorder = new SequenceHandler(HttpStatusCode.BadRequest, HttpStatusCode.OK);

        using var response = await SendAsync(recorder, HttpMethod.Get);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, recorder.Attempts);
    }

    [Fact]
    public async Task A_post_without_a_key_is_not_retried_even_when_throttled()
    {
        var recorder = new SequenceHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);

        using var response = await SendAsync(recorder, HttpMethod.Post);

        // Retrying could create the customer twice, so the 429 is surfaced instead.
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, recorder.Attempts);
    }

    [Fact]
    public async Task A_buffered_body_survives_being_resent()
    {
        var recorder = new SequenceHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK)
        {
            CaptureBodies = true,
        };

        using var invoker = CreateInvoker(recorder);
        using var request = new HttpRequestMessage(HttpMethod.Put, "https://restapi.e-conomic.com/customers/1")
        {
            Content = new StringContent("""{"name":"Acme"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(EconomicIdempotencyHandler.IdempotencyKeyHeader, "key-1");

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, recorder.Attempts);

        // The second attempt must carry the same body, not an empty one.
        Assert.Equal(2, recorder.Bodies.Count);
        Assert.All(recorder.Bodies, body => Assert.Equal("""{"name":"Acme"}""", body));
    }

    [Fact]
    public async Task The_same_idempotency_key_is_reused_across_retries()
    {
        var recorder = new SequenceHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        // Idempotency outside retry, as the DI registration wires them.
        var retry = new EconomicRetryHandler(Fast) { InnerHandler = recorder };
        using var invoker = new HttpMessageInvoker(
            new EconomicIdempotencyHandler(EconomicOptions.Demo()) { InnerHandler = retry });

        using var request = new HttpRequestMessage(HttpMethod.Put, "https://restapi.e-conomic.com/customers/1");
        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(2, recorder.Attempts);
        Assert.Equal(2, recorder.IdempotencyKeys.Count);
        Assert.Single(recorder.IdempotencyKeys.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Retry_after_is_preferred_over_the_backoff_curve()
    {
        var time = new FakeTimeProvider();
        var recorder = new SequenceHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK)
        {
            RetryAfter = TimeSpan.FromSeconds(7),
        };

        var options = new EconomicRetryOptions { MaxAttempts = 2, BaseDelay = TimeSpan.FromMilliseconds(1) };
        using var invoker = new HttpMessageInvoker(
            new EconomicRetryHandler(options, time) { InnerHandler = recorder });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://restapi.e-conomic.com/customers");
        var pending = invoker.SendAsync(request, TestContext.Current.CancellationToken);

        // The backoff would have been about a millisecond; the server asked for seven seconds.
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.False(pending.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));
        using var response = await pending;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void An_unusable_policy_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => new EconomicRetryHandler(new EconomicRetryOptions { MaxAttempts = 0 }));

        Assert.Throws<InvalidOperationException>(() => new EconomicRetryHandler(new EconomicRetryOptions
        {
            BaseDelay = TimeSpan.FromSeconds(10),
            MaxDelay = TimeSpan.FromSeconds(1),
        }));
    }

    private static async Task<HttpResponseMessage> SendAsync(SequenceHandler recorder, HttpMethod method)
    {
        using var invoker = CreateInvoker(recorder);
        using var request = new HttpRequestMessage(method, "https://restapi.e-conomic.com/customers");

        return await invoker.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static HttpMessageInvoker CreateInvoker(SequenceHandler recorder) =>
        new(new EconomicRetryHandler(Fast) { InnerHandler = recorder });

    /// <summary>Returns the given statuses in order, recording what each attempt carried.</summary>
    private sealed class SequenceHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        public bool CaptureBodies { get; init; }

        public TimeSpan? RetryAfter { get; init; }

        public List<string> Bodies { get; } = [];

        public List<string> IdempotencyKeys { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var status = statuses[Math.Min(Attempts, statuses.Length - 1)];
            Attempts++;

            if (CaptureBodies && request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            if (request.Headers.TryGetValues(EconomicIdempotencyHandler.IdempotencyKeyHeader, out var keys))
            {
                IdempotencyKeys.Add(keys.First());
            }

            var response = new HttpResponseMessage(status) { RequestMessage = request };
            if (RetryAfter is { } delay && status == HttpStatusCode.TooManyRequests)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
            }

            return response;
        }
    }
}
