using System.Globalization;
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

        // TryGetValues matches without regard to case here, unlike the dictionary the generated
        // clients hand over, so this side needs no fallback of its own.
        var requestId = response.Headers.TryGetValues(RequestIdHeader, out var ids)
            ? ids.FirstOrDefault()
            : null;

        var message = BuildMessage(response, problem, legacy);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return new EconomicRateLimitException(
                message,
                problem,
                legacy,
                requestId,
                rateLimit,
                HeaderReading.RetryAfter(response.Headers, DateTimeOffset.UtcNow),
                body);
        }

        return new EconomicApiException(
            message, response.StatusCode, problem, legacy, requestId, rateLimit, body);
    }

    /// <summary>Composes the message a failure carries, wherever the failure was noticed.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="errorCode">e-conomic's own error code, when the body carried one.</param>
    /// <param name="request">What was requested, e.g. <c>GET /customers</c>.</param>
    /// <param name="reason">The server's explanation.</param>
    /// <param name="legacyErrors">The legacy API's per-property errors, when it sent any.</param>
    /// <returns>The message.</returns>
    /// <remarks>
    /// Shared with both facade transports. There were three copies of this and they had drifted:
    /// only this one appended the error code, so the same failure read differently depending on
    /// whether it was raised from a hand-written request or a generated one — and the code, which
    /// is the part e-conomic's own documentation is indexed by, was missing from the two paths that
    /// raise almost every exception this library produces.
    /// </remarks>
    internal static string BuildMessage(
        int status,
        string? errorCode,
        string request,
        string reason,
        IReadOnlyList<string>? legacyErrors)
    {
        var code = errorCode is { Length: > 0 } value ? $" ({value})" : string.Empty;

        // The legacy API names the offending property here, which is far more actionable than
        // the generic "Could not parse query string filter." message it leads with.
        var errors = legacyErrors is { Count: > 0 } list
            ? " " + string.Join(" ", list)
            : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"e-conomic returned {status}{code} for {request}: {reason}{errors}");
    }

    private static string BuildMessage(
        HttpResponseMessage response,
        EconomicProblemDetails? problem,
        EconomicLegacyError? legacy)
    {
        var reason =
            problem?.Detail
            ?? problem?.Title
            ?? legacy?.Message
            ?? response.ReasonPhrase
            ?? "Request failed";

        var method = response.RequestMessage?.Method.Method ?? "?";
        var uri = response.RequestMessage?.RequestUri?.ToString() ?? "?";

        return BuildMessage(
            (int)response.StatusCode,
            problem?.ErrorCode,
            $"{method} {uri}",
            reason,
            legacy?.Errors);
    }
}
