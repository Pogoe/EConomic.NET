using System.Net;
using EConomic.Http;

namespace EConomic.Exceptions;

/// <summary>
/// Thrown when e-conomic rejects a request with <c>429 Too Many Requests</c> because the
/// agreement's token budget for the current window is exhausted.
/// </summary>
/// <remarks>
/// Inspect <see cref="EconomicApiException.RateLimit"/> to see the budget and window, and
/// <see cref="RetryAfter"/> for how long the server asked you to wait.
/// </remarks>
public sealed class EconomicRateLimitException : EconomicApiException
{
    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">The message.</param>
    public EconomicRateLimitException(string message)
        : base(message, HttpStatusCode.TooManyRequests)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    /// <remarks>
    /// The status is supplied here rather than left to the two-argument base constructor, which does
    /// not set one. A throttling exception reporting no status contradicts its own type, and the
    /// other constructors on it both say <c>429</c>.
    /// </remarks>
    public EconomicRateLimitException(string message, Exception? innerException)
        : base(message, HttpStatusCode.TooManyRequests, innerException: innerException)
    {
    }

    /// <summary>Creates an exception describing a throttled response.</summary>
    /// <param name="message">The message.</param>
    /// <param name="problemDetails">The parsed problem+json body, if the OpenAPI services produced it.</param>
    /// <param name="legacyError">The parsed legacy error body, if the legacy REST API produced it.</param>
    /// <param name="requestId">The value of the <c>X-RequestId</c> response header, if any.</param>
    /// <param name="rateLimit">The rate-limit budget reported on the response, if any.</param>
    /// <param name="retryAfter">The <c>Retry-After</c> delay the server asked for, if any.</param>
    /// <param name="rawBody">The raw response body, if it was read.</param>
    /// <remarks>
    /// <paramref name="problemDetails"/> has no default so that this overload cannot be reached
    /// with a message alone — that call belongs to the single-argument constructor above, and two
    /// signatures competing for it means the default here would only ever apply when a later
    /// argument was named.
    /// </remarks>
    public EconomicRateLimitException(
        string message,
        EconomicProblemDetails? problemDetails,
        EconomicLegacyError? legacyError = null,
        string? requestId = null,
        RateLimitStatus? rateLimit = null,
        TimeSpan? retryAfter = null,
        string? rawBody = null)
        : base(message, HttpStatusCode.TooManyRequests, problemDetails, legacyError, requestId, rateLimit, rawBody)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>How long the server asked the caller to wait before retrying, if it said.</summary>
    public TimeSpan? RetryAfter { get; }
}
