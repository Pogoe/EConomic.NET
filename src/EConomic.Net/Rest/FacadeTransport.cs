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

    /// <summary>Converts a public string onto a generated enum property.</summary>
    /// <typeparam name="TEnum">The generated enum, inferred from <paramref name="inferenceOnly"/>.</typeparam>
    /// <param name="value">The caller's value, or <see langword="null"/> to leave it unset.</param>
    /// <param name="inferenceOnly">The target property, read solely so the compiler can infer
    /// <typeparamref name="TEnum"/>. Its value is returned unchanged when nothing is supplied.</param>
    /// <returns>The parsed value.</returns>
    /// <remarks>
    /// The generated enums are internal, so they cannot appear on the public models — a spec change
    /// adding a member would otherwise be a breaking change for consumers. The public surface takes
    /// the string e-conomic actually sends, and this converts it. The generated type is never named
    /// here because NSwag invents that name; passing the target property lets the compiler supply
    /// it instead.
    /// </remarks>
    /// <exception cref="ArgumentException">The value is not one this property accepts.</exception>
    public static TEnum ParseEnum<TEnum>(string? value, TEnum inferenceOnly)
        where TEnum : struct, Enum
    {
        if (value is null)
        {
            return inferenceOnly;
        }

        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        // Defaulting silently would send a value the caller never chose, on a property e-conomic
        // often requires.
        throw new ArgumentException(
            $"'{value}' is not a value this property accepts. Expected one of: "
            + string.Join(", ", Enum.GetNames<TEnum>()) + ".",
            nameof(value));
    }

    /// <summary>Projects a generated collection onto its public counterpart.</summary>
    /// <typeparam name="TSource">The generated element type, which NSwag names.</typeparam>
    /// <typeparam name="TResult">The public element type.</typeparam>
    /// <param name="source">The generated collection, which may be absent.</param>
    /// <param name="map">Projects one element.</param>
    /// <returns>The mapped elements, empty when the response carried none.</returns>
    /// <remarks>
    /// An absent array and an empty array are the same thing here — e-conomic omits <c>lines</c>
    /// from its collection listings entirely — so this never returns <see langword="null"/> and the
    /// public models expose a plain list rather than a nullable one.
    /// </remarks>
    public static IReadOnlyList<TResult> MapList<TSource, TResult>(
        IEnumerable<TSource>? source,
        Func<TSource, TResult> map)
    {
        ArgumentNullException.ThrowIfNull(map);

        if (source is null)
        {
            return [];
        }

        var mapped = new List<TResult>();
        foreach (var item in source)
        {
            mapped.Add(map(item));
        }

        return mapped;
    }

    /// <summary>Converts a public string onto a generated enum property that is itself optional.</summary>
    /// <typeparam name="TEnum">The generated enum, inferred from <paramref name="inferenceOnly"/>.</typeparam>
    /// <param name="value">The caller's value, or <see langword="null"/> to leave it unset.</param>
    /// <param name="inferenceOnly">The target property, read solely for type inference.</param>
    /// <returns>The parsed value, or <see langword="null"/> when nothing is supplied.</returns>
    /// <remarks>
    /// NSwag makes a nested class's optional enum nullable, so the same generated assignment needs
    /// both shapes. Overload resolution picks between them: the non-nullable one cannot infer
    /// <typeparamref name="TEnum"/> from a <see cref="Nullable{T}"/> argument, and this one cannot
    /// infer it from a bare enum.
    /// </remarks>
    /// <exception cref="ArgumentException">The value is not one this property accepts.</exception>
    public static TEnum? ParseEnum<TEnum>(string? value, TEnum? inferenceOnly)
        where TEnum : struct, Enum =>
        value is null ? inferenceOnly : ParseEnum(value, default(TEnum));

    /// <summary>Builds a generated collection from a public one.</summary>
    /// <typeparam name="TSource">The public element type.</typeparam>
    /// <typeparam name="TTarget">The generated element type, inferred from <paramref name="inferenceOnly"/>.</typeparam>
    /// <param name="source">The caller's elements, or <see langword="null"/> to send none.</param>
    /// <param name="inferenceOnly">The target property, read solely so the compiler can infer
    /// <typeparamref name="TTarget"/>. Its value is never used.</param>
    /// <param name="build">Copies one element onto a fresh generated instance.</param>
    /// <returns>The generated elements.</returns>
    /// <remarks>
    /// The counterpart to <see cref="MapList{TSource, TResult}"/>, and it exists for the same reason
    /// <see cref="ParseEnum{TEnum}(string?, TEnum)"/> does: NSwag invents the element type's name, so the generator
    /// cannot write it out. Passing the target property lets the compiler supply it, which also
    /// makes <paramref name="build"/>'s second parameter concrete enough to assign members on.
    /// </remarks>
    public static IReadOnlyList<TTarget> BuildList<TSource, TTarget>(
        IEnumerable<TSource>? source,
        IEnumerable<TTarget>? inferenceOnly,
        Action<TSource, TTarget> build)
        where TTarget : new()
    {
        ArgumentNullException.ThrowIfNull(build);

        if (source is null)
        {
            return [];
        }

        var built = new List<TTarget>();
        foreach (var item in source)
        {
            var target = new TTarget();
            build(item, target);
            built.Add(target);
        }

        return built;
    }

    /// <summary>Converts public strings onto a generated collection of enums.</summary>
    /// <typeparam name="TEnum">The generated enum, inferred from <paramref name="inferenceOnly"/>.</typeparam>
    /// <param name="values">The caller's values, or <see langword="null"/> to send none.</param>
    /// <param name="inferenceOnly">The target property, read solely for type inference.</param>
    /// <returns>The parsed values.</returns>
    /// <exception cref="ArgumentException">One of the values is not one this property accepts.</exception>
    public static IReadOnlyList<TEnum> ParseEnums<TEnum>(
        IEnumerable<string>? values,
        IEnumerable<TEnum>? inferenceOnly)
        where TEnum : struct, Enum
    {
        if (values is null)
        {
            return [];
        }

        var parsed = new List<TEnum>();
        foreach (var value in values)
        {
            parsed.Add(ParseEnum<TEnum>(value, default));
        }

        return parsed;
    }

    /// <summary>Issues a <c>DELETE</c>, which has no generated method.</summary>
    /// <param name="httpClient">The configured transport.</param>
    /// <param name="requestUri">The resource to delete, relative to the base address.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the resource is gone.</returns>
    /// <remarks>
    /// e-conomic publishes one JSON schema per request or response body, and <c>DELETE</c> has
    /// neither, so no schema exists and NSwag generates nothing. These calls are therefore issued
    /// directly rather than through the generated clients, and the endpoints themselves come from
    /// the published documentation rather than from <c>specs/</c>.
    /// </remarks>
    /// <exception cref="EconomicApiException">The request failed.</exception>
    public static async Task DeleteAsync(
        HttpClient httpClient,
        string requestUri,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        using var request = new HttpRequestMessage(HttpMethod.Delete, requestUri);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // A successful delete is 204 No Content, so there is nothing to deserialize.
        if (!response.IsSuccessStatusCode)
        {
            throw await EconomicApiException.FromResponseAsync(response, cancellationToken).ConfigureAwait(false);
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
