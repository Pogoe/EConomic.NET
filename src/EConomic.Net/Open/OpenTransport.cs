using System.Globalization;
using System.Net;
using EConomic.Exceptions;

namespace EConomic.Open;

/// <summary>
/// The single point where a generated OpenAPI-service call becomes a facade call.
/// </summary>
/// <remarks>
/// The counterpart to the legacy surface's transport, and separate from it for one reason: these
/// services answer a rejected update with <c>409</c> and an optimistic-concurrency failure, which
/// has no equivalent on the legacy API and deserves an exception the caller can act on.
/// </remarks>
internal static class OpenTransport
{
    /// <summary>Runs a generated call that returns a value.</summary>
    /// <typeparam name="T">The generated response type.</typeparam>
    /// <param name="call">The generated call.</param>
    /// <param name="description">The request, for the message, e.g. <c>GET /Customers</c>.</param>
    /// <returns>The response.</returns>
    /// <exception cref="EconomicApiException">The request failed.</exception>
    public static async Task<T> SendAsync<T>(Func<Task<T>> call, string description)
    {
        ArgumentNullException.ThrowIfNull(call);

        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (EconomicGeneratedApiException exception)
        {
            throw Translate(exception, description);
        }
    }

    /// <summary>Runs a generated call that returns nothing.</summary>
    /// <param name="call">The generated call.</param>
    /// <param name="description">The request, for the message.</param>
    /// <returns>A task that completes when the call does.</returns>
    /// <exception cref="EconomicApiException">The request failed.</exception>
    public static async Task SendAsync(Func<Task> call, string description)
    {
        ArgumentNullException.ThrowIfNull(call);

        try
        {
            await call().ConfigureAwait(false);
        }
        catch (EconomicGeneratedApiException exception)
        {
            throw Translate(exception, description);
        }
    }

    private static EconomicApiException Translate(EconomicGeneratedApiException exception, string description)
    {
        var status = (HttpStatusCode)exception.StatusCode;
        var problem = EconomicProblemDetails.TryParse(exception.Response);
        var reason = problem?.Detail ?? problem?.Title ?? exception.Message;

        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"e-conomic returned {exception.StatusCode} for {description}: {reason}");

        var requestId = exception.Headers.TryGetValue(EconomicApiException.RequestIdHeader, out var ids)
            ? ids.FirstOrDefault()
            : null;

        // A 409 here is always the objectVersion check: the record changed since it was read, and
        // retrying the same request cannot succeed. Verified against a live agreement.
        if (status == HttpStatusCode.Conflict)
        {
            return new EconomicConcurrencyException(message, problem, requestId, rawBody: exception.Response);
        }

        if (status == HttpStatusCode.TooManyRequests)
        {
            return new EconomicRateLimitException(
                message, problem, legacyError: null, requestId, rawBody: exception.Response);
        }

        return new EconomicApiException(
            message, status, problem, legacyError: null, requestId, rateLimit: null,
            rawBody: exception.Response, innerException: exception);
    }
}
