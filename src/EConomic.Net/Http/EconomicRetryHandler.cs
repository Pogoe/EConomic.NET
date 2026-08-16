using System.Net;

namespace EConomic.Http;

/// <summary>
/// Retries throttled and transient failures with exponential backoff and jitter.
/// </summary>
/// <remarks>
/// <para>
/// A request is retried only when repeating it is safe. Methods that are idempotent by HTTP
/// semantics always qualify; <c>POST</c> qualifies only when it carries an
/// <c>Idempotency-Key</c>, because without one a retry after an ambiguous failure can create the
/// resource twice. That is why <see cref="EconomicIdempotencyHandler"/> belongs outside this
/// handler — the key must already be attached, and identical across attempts.
/// </para>
/// <para>
/// Replace this handler wholesale if you would rather use Polly or your own policy: it is an
/// ordinary <see cref="DelegatingHandler"/> and nothing else depends on it.
/// </para>
/// </remarks>
public sealed class EconomicRetryHandler : DelegatingHandler
{
    private readonly EconomicRetryOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a handler using the supplied policy.</summary>
    /// <param name="options">The retry policy.</param>
    /// <param name="timeProvider">Clock used for delays. Defaults to the system clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The policy is not usable.</exception>
    public EconomicRetryHandler(EconomicRetryOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Whether a failed request may be repeated.</summary>
    /// <param name="request">The request.</param>
    /// <returns><see langword="true"/> when repeating it cannot duplicate an effect.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public static bool IsSafeToRetry(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // DELETE is idempotent by HTTP semantics but not at e-conomic, which documents on the
        // drafts and sent endpoints that "on the consecutive calls it will be returning status
        // code 404". Retrying after a lost response would therefore report failure for a delete
        // that actually succeeded. It needs the same key POST does: with one the server replays the
        // original result instead of running the operation again.
        if (request.Method == HttpMethod.Post
            || request.Method == HttpMethod.Patch
            || request.Method == HttpMethod.Delete)
        {
            return request.Headers.Contains(EconomicIdempotencyHandler.IdempotencyKeyHeader);
        }

        return true;
    }

    /// <summary>Whether a response is worth retrying.</summary>
    /// <param name="statusCode">The status returned.</param>
    /// <returns><see langword="true"/> for throttling and transient server failures.</returns>
    public static bool IsTransient(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.TooManyRequests => true,
        HttpStatusCode.InternalServerError => true,
        HttpStatusCode.BadGateway => true,
        HttpStatusCode.ServiceUnavailable => true,
        HttpStatusCode.GatewayTimeout => true,
        _ => false,
    };

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        bool retryable = IsSafeToRetry(request);

        // A request body is a forward-only stream by default, so a second attempt would send an
        // empty one. Buffering first makes the request repeatable.
        if (retryable && request.Content is not null)
        {
#if NET9_0_OR_GREATER
            await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
#else
            // The overload taking a cancellation token does not exist on net8.0.
            await request.Content.LoadIntoBufferAsync().ConfigureAwait(false);
#endif
        }

        for (int attempt = 1; ; attempt++)
        {
            bool isLastAttempt = attempt >= _options.MaxAttempts || !retryable;

            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (!isLastAttempt)
            {
                // The request may or may not have reached the server. Retrying is only correct
                // because IsSafeToRetry already established that repeating it is harmless.
                await DelayAsync(attempt, retryAfter: null, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (isLastAttempt || !IsTransient(response.StatusCode))
            {
                return response;
            }

            var retryAfter = _options.RespectRetryAfter ? response.Headers.RetryAfter?.Delta : null;
            response.Dispose();

            await DelayAsync(attempt, retryAfter, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task DelayAsync(int attempt, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        // The server knows better than the backoff curve when it says how long to wait.
        var delay = retryAfter ?? Backoff(attempt);

        return delay <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(delay, _timeProvider, cancellationToken);
    }

    private TimeSpan Backoff(int attempt)
    {
        var exponential = _options.BaseDelay * Math.Pow(2, attempt - 1);
        var capped = exponential > _options.MaxDelay ? _options.MaxDelay : exponential;

        // Full jitter. Without it, clients that were throttled together retry together and
        // throttle each other again.
        return capped * Random.Shared.NextDouble();
    }
}
