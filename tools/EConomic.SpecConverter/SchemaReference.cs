using System.Text.Json.Nodes;

namespace EConomic.SpecConverter;

/// <summary>
/// Reading and writing the <c>$ref</c> pointers that tie an OpenAPI document's schemas together.
/// </summary>
/// <remarks>
/// Shared because it was previously copied verbatim into four generators. That is mechanical
/// knowledge about the document's own shape rather than anything curated from live probes, so
/// unlike the lookup tables elsewhere in this tool there is no reason for four copies of it — and a
/// change to how a reference is recognised should not have to be found in four places.
/// </remarks>
internal static class SchemaReference
{
    /// <summary>The prefix every schema reference in these documents carries.</summary>
    public const string Prefix = "#/components/schemas/";

    /// <summary>A reference to the named schema.</summary>
    /// <param name="entity">The schema name.</param>
    /// <returns>The <c>$ref</c> string.</returns>
    public static string To(string entity) => Prefix + entity;

    /// <summary>The schema a node refers to, if it is a reference to one.</summary>
    /// <param name="node">The node to inspect.</param>
    /// <returns>The schema name, or <see langword="null"/> if this is not a schema reference.</returns>
    public static string? Name(JsonNode? node) =>
        node?["$ref"]?.GetValue<string>() is { } reference
        && reference.StartsWith(Prefix, StringComparison.Ordinal)
            ? reference[Prefix.Length..]
            : null;

    /// <summary>Follows a reference to the schema it names, or returns the schema unchanged.</summary>
    /// <param name="schema">A schema, which may be a reference.</param>
    /// <param name="schemas">The document's schema components.</param>
    /// <returns>The referenced schema if this was a resolvable reference, otherwise <paramref name="schema"/>.</returns>
    public static JsonObject Resolve(JsonObject schema, JsonObject schemas) =>
        Name(schema) is { } name && schemas[name] is JsonObject target ? target : schema;
}
