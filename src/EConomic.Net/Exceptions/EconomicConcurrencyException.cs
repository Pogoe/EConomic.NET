using System.Net;

namespace EConomic.Exceptions;

/// <summary>
/// Thrown when an update is rejected because the record changed since it was read.
/// </summary>
/// <remarks>
/// <para>
/// The OpenAPI services carry an <c>objectVersion</c> on every record and use it for optimistic
/// concurrency. An update sending a stale one — or none at all — is answered <c>409 Conflict</c>
/// with "The resource has been updated by another user", and nothing is written. Verified against a
/// live agreement: the same update carrying the value from a fresh read succeeds.
/// </para>
/// <para>
/// This is separated from <see cref="EconomicApiException"/> because the caller's correct response
/// is specific and unlike any other failure: re-read the record, re-apply the change, and send it
/// again. Retrying the identical request cannot succeed, which is also why the retry handler leaves
/// a <c>409</c> alone.
/// </para>
/// <para>
/// The legacy REST API has no equivalent. There the last write wins, so nothing on that surface
/// throws this.
/// </para>
/// </remarks>
public sealed class EconomicConcurrencyException : EconomicApiException
{
    /// <summary>Creates a concurrency failure.</summary>
    /// <param name="message">The message.</param>
    /// <param name="problemDetails">The parsed <c>problem+json</c> body.</param>
    /// <param name="requestId">The request identifier, when the response carried one.</param>
    /// <param name="rawBody">The unparsed response body.</param>
    public EconomicConcurrencyException(
        string message,
        EconomicProblemDetails? problemDetails = null,
        string? requestId = null,
        string? rawBody = null)
        : base(message, HttpStatusCode.Conflict, problemDetails, legacyError: null, requestId,
            rateLimit: null, rawBody)
    {
    }

    /// <summary>Creates a concurrency failure.</summary>
    public EconomicConcurrencyException()
        : this("The record changed since it was read.")
    {
    }

    /// <summary>Creates a concurrency failure.</summary>
    /// <param name="message">The message.</param>
    public EconomicConcurrencyException(string message)
        : this(message, problemDetails: null)
    {
    }

    /// <summary>Creates a concurrency failure.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public EconomicConcurrencyException(string message, Exception innerException)
        : base(message, HttpStatusCode.Conflict, problemDetails: null, legacyError: null,
            requestId: null, rateLimit: null, rawBody: null, innerException: innerException)
    {
    }
}
