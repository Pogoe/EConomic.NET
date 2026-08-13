using System.Globalization;
using System.Net;
using EConomic.Exceptions;
using Generated = EConomic.Rest.Generated;

namespace EConomic.Rest;

/// <summary>
/// A link to another resource, as e-conomic embeds them.
/// </summary>
/// <param name="Number">The referenced resource's number.</param>
/// <param name="Self">The absolute URL of the referenced resource.</param>
/// <remarks>
/// e-conomic spells the number differently in every context — <c>customerGroupNumber</c>,
/// <c>paymentTermsNumber</c>, <c>employeeNumber</c> — but the shape is always the same, so the
/// facade presents one type instead of one per resource.
/// </remarks>
public sealed record EconomicReference(int Number, Uri? Self);

/// <summary>
/// The single point where a generated call becomes a facade call.
/// </summary>
/// <remarks>
/// Shared by every generated page source so the translation exists once. Without it, each of the
/// resources would carry its own copy of this logic and they would drift.
/// </remarks>
internal static class FacadeTransport
{
    /// <summary>Runs a generated call, rewriting its failure into the library's own exception.</summary>
    /// <typeparam name="T">The generated response type.</typeparam>
    /// <param name="call">The generated call.</param>
    /// <param name="description">The request, for the message, e.g. <c>GET /customers</c>.</param>
    /// <returns>The response.</returns>
    /// <exception cref="EconomicApiException">The request failed.</exception>
    public static async Task<T> SendAsync<T>(Func<Task<T>> call, string description)
    {
        ArgumentNullException.ThrowIfNull(call);

        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Generated.EconomicGeneratedApiException exception)
        {
            throw Translate(exception, description);
        }
    }

    private static EconomicApiException Translate(
        Generated.EconomicGeneratedApiException exception,
        string description)
    {
        var status = (HttpStatusCode)exception.StatusCode;
        var problem = EconomicProblemDetails.TryParse(exception.Response);
        var legacy = problem is null ? EconomicLegacyError.TryParse(exception.Response) : null;

        // Falling back to the generated message matters: it also reports a failure to deserialize
        // a *successful* response, which has no error body to parse at all.
        var reason = problem?.Detail ?? problem?.Title ?? legacy?.Message ?? exception.Message;
        var errors = legacy?.Errors is { Count: > 0 } list ? " " + string.Join(" ", list) : string.Empty;

        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"e-conomic returned {exception.StatusCode} for {description}: {reason}{errors}");

        var requestId = exception.Headers.TryGetValue(EconomicApiException.RequestIdHeader, out var ids)
            ? ids.FirstOrDefault()
            : null;

        if (status == HttpStatusCode.TooManyRequests)
        {
            return new EconomicRateLimitException(message, problem, legacy, requestId, rawBody: exception.Response);
        }

        return new EconomicApiException(
            message, status, problem, legacy, requestId, rawBody: exception.Response, innerException: exception);
    }
}
