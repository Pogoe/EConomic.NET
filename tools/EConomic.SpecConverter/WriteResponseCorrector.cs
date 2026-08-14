using System.Text.Json.Nodes;

namespace EConomic.SpecConverter;

/// <summary>
/// Points <c>POST</c> and <c>PUT</c> responses at the resource's read entity.
/// </summary>
/// <remarks>
/// <para>
/// e-conomic publishes one schema per endpoint body and uses the <em>request</em> schema to
/// describe the response as well. That is wrong, and provably so: a create returns the whole
/// resource. <c>POST /units</c> is described as returning only <c>name</c>, and actually returns
/// <c>unitNumber</c>, <c>name</c> and <c>self</c>. <c>POST /customers</c> is described with 26
/// properties and returns <c>contacts</c>, <c>templates</c>, <c>totals</c>, <c>deliveryLocations</c>
/// and <c>self</c> on top of them.
/// </para>
/// <para>
/// Left uncorrected the generated payload types silently discard everything the schema omits,
/// which is why several resources appeared to have no way to report the identifier they had just
/// assigned. Verified against a live agreement for customers, units, payment terms, suppliers,
/// products, customer groups and accounting years — every one returns the full resource.
/// </para>
/// <para>
/// Only the response is rewritten. The request body keeps its own schema, because what may be sent
/// really is narrower than what comes back.
/// </para>
/// </remarks>
public static class WriteResponseCorrector
{
    private const string RefPrefix = "#/components/schemas/";

    /// <summary>Rewrites the write responses in a document.</summary>
    /// <param name="document">An OpenAPI document, modified in place.</param>
    /// <returns>The number of responses corrected.</returns>
    public static int Apply(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document["paths"] is not JsonObject paths
            || document["components"]?["schemas"] is not JsonObject schemas)
        {
            return 0;
        }

        var entityByCollection = CollectionEntities(paths, schemas);
        var corrected = 0;

        foreach (var (path, item) in paths)
        {
            if (item is not JsonObject operations || Owner(path, entityByCollection) is not { } entity)
            {
                continue;
            }

            foreach (var method in (string[])["post", "put"])
            {
                if (operations[method] is not JsonObject operation
                    || operation["responses"] is not JsonObject responses)
                {
                    continue;
                }

                foreach (var (_, response) in responses)
                {
                    if (response?["content"]?["application/json"]?["schema"] is JsonObject schema)
                    {
                        schema.Clear();
                        schema["$ref"] = RefPrefix + entity;
                        corrected++;
                    }
                }
            }
        }

        return corrected;
    }

    /// <summary>
    /// Marks optional numeric properties on write payloads as nullable.
    /// </summary>
    /// <param name="document">An OpenAPI document, modified in place.</param>
    /// <returns>The number of properties marked.</returns>
    /// <remarks>
    /// A non-nullable <c>int</c> in the generated payload defaults to <c>0</c> and is serialized
    /// whether or not the caller set it. e-conomic rejects that: <c>customerNumber</c> declares
    /// <c>minimum: 1</c>, so an untouched create fails with "Integer 0 is less than minimum value
    /// of 1". Making the property nullable lets an unset value be omitted while an explicit zero is
    /// still sent, which is the distinction the API actually requires.
    /// <para>
    /// Only components used solely as request bodies are touched, so the read models keep their
    /// non-nullable numbers.
    /// </para>
    /// <para>
    /// Nested objects and array items are marked too. They have to be: an invoice line's
    /// <c>lineNumber</c> and <c>sortKey</c> both declare <c>minimum: 1</c>, so a draft invoice with
    /// a line the caller did not number was rejected outright — the same failure as
    /// <c>customerNumber</c>, one level further down.
    /// </para>
    /// </remarks>
    public static int MarkOptionalNumbersNullable(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document["paths"] is not JsonObject paths
            || document["components"]?["schemas"] is not JsonObject schemas)
        {
            return 0;
        }

        var requestBodies = new HashSet<string>(StringComparer.Ordinal);
        var otherUses = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (_, item) in paths)
        {
            if (item is not JsonObject operations)
            {
                continue;
            }

            foreach (var (_, node) in operations)
            {
                if (node is not JsonObject operation)
                {
                    continue;
                }

                if (Reference(operation["requestBody"]?["content"]?["application/json"]?["schema"]) is { } body)
                {
                    requestBodies.Add(body);
                }

                foreach (var (_, response) in operation["responses"]?.AsObject() ?? [])
                {
                    if (Reference(response?["content"]?["application/json"]?["schema"]) is { } used)
                    {
                        otherUses.Add(used);
                    }
                }
            }
        }

        var marked = 0;

        foreach (var name in requestBodies.Where(b => !otherUses.Contains(b)))
        {
            if (schemas[name] is JsonObject schema)
            {
                marked += MarkObject(schema);
            }
        }

        return marked;
    }

    /// <summary>Marks one object schema and everything nested inside it.</summary>
    private static int MarkObject(JsonObject schema)
    {
        if (schema["properties"] is not JsonObject properties)
        {
            return 0;
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in schema["required"]?.AsArray() ?? [])
        {
            if (entry?.GetValue<string>() is { } value)
            {
                required.Add(value);
            }
        }

        var marked = 0;

        foreach (var (property, node) in properties)
        {
            if (node is not JsonObject definition)
            {
                continue;
            }

            switch (definition["type"]?.GetValue<string>())
            {
                case "integer" or "number"
                    when !required.Contains(property) && definition["nullable"] is null:
                    definition["nullable"] = true;
                    marked++;
                    break;

                case "object":
                    marked += MarkObject(definition);
                    break;

                case "array" when definition["items"] is JsonObject item:
                    marked += MarkObject(item);
                    break;

                default:
                    break;
            }
        }

        return marked;
    }

    /// <summary>
    /// The entity a write path belongs to: either the collection itself, or one identifier below
    /// it. A nested collection such as <c>/customers/{n}/contacts</c> returns a contact rather than
    /// a customer, so it is deliberately excluded — its own entity is not described here.
    /// </summary>
    private static string? Owner(string path, Dictionary<string, string> entityByCollection)
    {
        if (entityByCollection.TryGetValue(path, out var direct))
        {
            return direct;
        }

        // One identifier below a collection addresses an item of it, at either nesting level:
        // /customers/{n} is a customer, /customers/{n}/contacts/{k} is a contact.
        var segments = path.Trim('/').Split('/');
        if (segments.Length < 2 || !segments[^1].StartsWith('{'))
        {
            return null;
        }

        var parent = "/" + string.Join('/', segments[..^1]);
        return entityByCollection.TryGetValue(parent, out var owner) ? owner : null;
    }

    /// <summary>
    /// Every path whose <c>GET</c> returns a collection envelope, keyed by path. Nested collections
    /// are included: a customer's contacts return contacts, not customers.
    /// </summary>
    private static Dictionary<string, string> CollectionEntities(JsonObject paths, JsonObject schemas)
    {
        var entities = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (path, item) in paths)
        {
            if (path.TrimEnd('/').EndsWith('}')
                || item?["get"] is not JsonObject get
                || Reference(get["responses"]?["200"]?["content"]?["application/json"]?["schema"]) is not { } envelope
                || schemas[envelope] is not JsonObject envelopeSchema
                || Reference(envelopeSchema["properties"]?["collection"]?["items"]) is not { } entity)
            {
                continue;
            }

            entities[path] = entity;
        }

        return entities;
    }

    private static string? Reference(JsonNode? node) =>
        node?["$ref"]?.GetValue<string>() is { } reference
        && reference.StartsWith(RefPrefix, StringComparison.Ordinal)
            ? reference[RefPrefix.Length..]
            : null;
}
