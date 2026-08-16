using System.Net;
using EConomic.Http;

namespace EConomic.Exceptions;

/// <summary>
/// Thrown when e-conomic returns an unsuccessful response.
/// </summary>
/// <remarks>
/// The two API surfaces report errors in different shapes — <see cref="ProblemDetails"/> for the
/// OpenAPI services, <see cref="LegacyError"/> for the legacy REST API. The members on this
/// exception read from whichever one is present, so callers rarely need to care which surface
/// produced the failure.
/// <para>
/// Never carries the request's tokens. When reporting a problem to e-conomic support, quote
/// <see cref="RequestId"/> or <see cref="TraceId"/>.
/// </para>
/// </remarks>
public class EconomicApiException : Exception
{
    private static readonly string[] NoStrings = [];

    /// <summary>Name of the legacy REST API's per-request correlation header.</summary>
    public const string RequestIdHeader = "X-RequestId";

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">The message.</param>
    public EconomicApiException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public EconomicApiException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an exception describing a failed response.</summary>
    /// <param name="message">The message.</param>
    /// <param name="statusCode">The HTTP status code returned.</param>
    /// <param name="problemDetails">The parsed problem+json body, if the OpenAPI services produced it.</param>
    /// <param name="legacyError">The parsed legacy error body, if the legacy REST API produced it.</param>
    /// <param name="requestId">The value of the <c>X-RequestId</c> response header, if any.</param>
    /// <param name="rateLimit">The rate-limit budget reported on the response, if any.</param>
    /// <param name="rawBody">The raw response body, if it was read.</param>
    /// <param name="innerException">The underlying cause, such as a deserialization failure.</param>
    public EconomicApiException(
        string message,
        HttpStatusCode statusCode,
        EconomicProblemDetails? problemDetails = null,
        EconomicLegacyError? legacyError = null,
        string? requestId = null,
        RateLimitStatus? rateLimit = null,
        string? rawBody = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ProblemDetails = problemDetails;
        LegacyError = legacyError;
        RequestId = requestId;
        RateLimit = rateLimit;
        RawBody = rawBody;
    }

    /// <summary>The HTTP status code returned, if the failure reached the server.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>The parsed <c>problem+json</c> body, when the OpenAPI services produced the failure.</summary>
    public EconomicProblemDetails? ProblemDetails { get; }

    /// <summary>The parsed error body, when the legacy REST API produced the failure.</summary>
    public EconomicLegacyError? LegacyError { get; }

    /// <summary>e-conomic's own error code, e.g. <c>ResourceNotFound</c>. OpenAPI services only.</summary>
    public string? ErrorCode => ProblemDetails?.ErrorCode;

    /// <summary>Correlation id, from whichever error shape the response used.</summary>
    public string? TraceId => ProblemDetails?.TraceId ?? LegacyError?.LogId;

    /// <summary>Human-readable explanation, from whichever error shape the response used.</summary>
    public string? Detail => ProblemDetails?.Detail ?? ProblemDetails?.Title ?? LegacyError?.Message;

    /// <summary>Longer integrator-facing guidance. Legacy REST API only.</summary>
    public string? DeveloperHint => LegacyError?.DeveloperHint;

    /// <summary>Specific problems listed by the server; empty when none were listed.</summary>
    public IReadOnlyList<string> Errors => LegacyError?.Errors ?? NoStrings;

    /// <summary>
    /// Fields the resource allows filtering on, when the failure was a rejected filter on the
    /// legacy REST API. Empty otherwise.
    /// </summary>
    public IReadOnlyList<string> AllowedFilteringFields => LegacyError?.AllowedFilteringFields ?? NoStrings;

    /// <summary>Correlation id from the <c>X-RequestId</c> response header.</summary>
    public string? RequestId { get; }

    /// <summary>The rate-limit budget reported alongside the failure, if any.</summary>
    public RateLimitStatus? RateLimit { get; }

    /// <summary>The raw response body, if it was read before throwing.</summary>
    public string? RawBody { get; }

    /// <summary>
    /// Builds the exception matching an unsuccessful response, reading and parsing its body in
    /// whichever of the two error shapes it used.
    /// </summary>
    /// <param name="response">The unsuccessful response.</param>
    /// <param name="cancellationToken">Token to cancel reading the body.</param>
    /// <returns>An <see cref="EconomicRateLimitException"/> for <c>429</c>, otherwise an <see cref="EconomicApiException"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is <see langword="null"/>.</exception>
    public static async Task<EconomicApiException> FromResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        string? body = null;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // A failure response with an unreadable body is still worth reporting.
        }

        var problem = EconomicProblemDetails.TryParse(body);
        var legacy = problem is null ? EconomicLegacyError.TryParse(body) : null;
        var rateLimit = RateLimitStatus.FromResponse(response);
        string? requestId = response.Headers.TryGetValues(RequestIdHeader, out var ids)
            ? ids.FirstOrDefault()
            : null;

        string message = BuildMessage(response, problem, legacy);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return new EconomicRateLimitException(
                message,
                problem,
                legacy,
                requestId,
                rateLimit,
                response.Headers.RetryAfter?.Delta,
                body);
        }

        return new EconomicApiException(
            message, response.StatusCode, problem, legacy, requestId, rateLimit, body);
    }

    private static string BuildMessage(
        HttpResponseMessage response,
        EconomicProblemDetails? problem,
        EconomicLegacyError? legacy)
    {
        int status = (int)response.StatusCode;
        string reason =
            problem?.Detail
            ?? problem?.Title
            ?? legacy?.Message
            ?? response.ReasonPhrase
            ?? "Request failed";

        string code = problem?.ErrorCode is { Length: > 0 } errorCode ? $" ({errorCode})" : string.Empty;
        string method = response.RequestMessage?.Method.Method ?? "?";
        string uri = response.RequestMessage?.RequestUri?.ToString() ?? "?";

        string message = $"e-conomic returned {status}{code} for {method} {uri}: {reason}";

        // The legacy API names the offending property here, which is far more actionable than
        // the generic "Could not parse query string filter." message it leads with.
        if (legacy?.Errors is { Count: > 0 } errors)
        {
            message += $" {string.Join(" ", errors)}";
        }

        return message;
    }
}
