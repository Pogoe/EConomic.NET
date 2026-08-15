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

    /// <summary>Converts the name e-conomic sends into the generated enum it belongs to.</summary>
    /// <typeparam name="TEnum">The generated enum.</typeparam>
    /// <param name="value">The value from the public model.</param>
    /// <returns>The parsed value.</returns>
    /// <remarks>
    /// The generated enums are internal, so the public models carry the name as text. Deliberately
    /// its own copy rather than borrowing the legacy surface's: the two surfaces share a transport
    /// and a query language, and nothing above that, and a shared helper is how that starts to slip.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not one of the values.</exception>
    public static TEnum ParseEnum<TEnum>(string? value)
        where TEnum : struct, Enum
    {
        if (value is null)
        {
            return default;
        }

        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        // Defaulting silently would send a value the caller never chose.
        throw new ArgumentException(
            $"'{value}' is not a value this property accepts. Expected one of: "
            + string.Join(", ", Enum.GetNames<TEnum>()) + ".",
            nameof(value));
    }

    /// <summary>Converts an optional name into the generated enum it belongs to.</summary>
    /// <typeparam name="TEnum">The generated enum.</typeparam>
    /// <param name="value">The value from the public model, or <see langword="null"/>.</param>
    /// <returns>The parsed value, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not one of the values.</exception>
    public static TEnum? ParseOptionalEnum<TEnum>(string? value)
        where TEnum : struct, Enum =>
        value is null ? null : ParseEnum<TEnum>(value);

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
