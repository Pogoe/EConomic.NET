using System.Text.Json.Serialization;

namespace EConomic.Exceptions;

/// <summary>
/// Source-generated serialization context. Keeps the library reflection-free so it stays
/// trim- and AOT-compatible.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EconomicProblemDetails))]
[JsonSerializable(typeof(EconomicLegacyError))]
internal sealed partial class EconomicJsonContext : JsonSerializerContext;
