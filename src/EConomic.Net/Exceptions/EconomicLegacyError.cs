using System.Text.Json;
using System.Text.Json.Serialization;

namespace EConomic.Exceptions;

/// <summary>
/// The error body returned by the legacy REST API at <c>restapi.e-conomic.com</c>.
/// </summary>
/// <remarks>
/// The legacy API predates RFC 9457 and does not return <c>problem+json</c>; it uses its own shape.
/// See <see cref="EconomicProblemDetails"/> for the newer OpenAPI services.
/// <para>
/// Failed filter queries are unusually helpful here: the response lists every field the resource
/// actually allows filtering on, which <see cref="AllowedFilteringFields"/> surfaces verbatim.
/// </para>
/// </remarks>
public sealed class EconomicLegacyError
{
    /// <summary>Short summary of what went wrong.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>Longer guidance aimed at the integrator, often including a worked example.</summary>
    [JsonPropertyName("developerHint")]
    public string? DeveloperHint { get; init; }

    /// <summary>Correlation id. Quote this when contacting e-conomic support.</summary>
    [JsonPropertyName("logId")]
    public string? LogId { get; init; }

    /// <summary>HTTP status code echoed in the body.</summary>
    [JsonPropertyName("httpStatusCode")]
    public int? HttpStatusCode { get; init; }

    /// <summary>Server-side timestamp of the failure.</summary>
    [JsonPropertyName("logTime")]
    public string? LogTime { get; init; }

    /// <summary>Specific problems found, e.g. <c>Filtering is not allowed on property 'bogusProp'.</c></summary>
    [JsonPropertyName("errors")]
    public IReadOnlyList<string>? Errors { get; init; }

    /// <summary>
    /// Every field this resource allows filtering on, returned when a filter fails to parse.
    /// The legacy API publishes no machine-readable filter metadata, so this response is the only
    /// authoritative source — surface it rather than guessing.
    /// </summary>
    [JsonPropertyName("allowedFilteringFields")]
    public IReadOnlyList<string>? AllowedFilteringFields { get; init; }

    /// <summary>Parses a legacy error payload, returning <see langword="null"/> if it is absent or not this shape.</summary>
    /// <param name="json">The raw response body.</param>
    /// <returns>The parsed error, or <see langword="null"/>.</returns>
    public static EconomicLegacyError? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var error = JsonSerializer.Deserialize(json, EconomicJsonContext.Default.EconomicLegacyError);

            // A problem+json body deserializes into this type without error but leaves it empty,
            // so require a field only the legacy shape has.
            return error is { Message: null, DeveloperHint: null, LogId: null } ? null : error;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
