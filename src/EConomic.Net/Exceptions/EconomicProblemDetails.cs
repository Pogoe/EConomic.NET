using System.Text.Json;
using System.Text.Json.Serialization;

namespace EConomic.Exceptions;

/// <summary>
/// The <c>application/problem+json</c> body the OpenAPI services at <c>apis.e-conomic.com</c>
/// return for a failed request.
/// </summary>
/// <remarks>
/// The legacy REST API uses a different shape entirely — see <see cref="EconomicLegacyError"/>.
/// </remarks>
public sealed class EconomicProblemDetails
{
    /// <summary>URI identifying the problem type, usually a link to the relevant HTTP status definition.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Short, human-readable summary of the problem.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>HTTP status code echoed in the body.</summary>
    [JsonPropertyName("status")]
    public int? Status { get; init; }

    /// <summary>Human-readable explanation specific to this occurrence.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    /// <summary>e-conomic's own error code, e.g. <c>ResourceNotFound</c>.</summary>
    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }

    /// <summary>Correlation id. Quote this when contacting e-conomic support.</summary>
    [JsonPropertyName("traceId")]
    public string? TraceId { get; init; }

    /// <summary>Server-side UTC timestamp of the failure.</summary>
    [JsonPropertyName("traceTimeUtc")]
    public string? TraceTimeUtc { get; init; }

    /// <summary>Parses a problem+json payload, returning <see langword="null"/> if it is absent or unparseable.</summary>
    /// <param name="json">The raw response body.</param>
    /// <returns>The parsed problem details, or <see langword="null"/>.</returns>
    public static EconomicProblemDetails? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var problem = JsonSerializer.Deserialize(json, EconomicJsonContext.Default.EconomicProblemDetails);

            // A legacy error body deserializes into this type without error but leaves it empty,
            // so require a field only the problem+json shape has.
            return problem is { Type: null, Title: null, ErrorCode: null } ? null : problem;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
