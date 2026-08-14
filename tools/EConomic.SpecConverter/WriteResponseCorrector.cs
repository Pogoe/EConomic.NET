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

                var isCollection = SchemaRegistry.CollectionWriteResponses.Contains(entity);

                foreach (var (_, response) in responses)
                {
                    if (response?["content"]?["application/json"]?["schema"] is not JsonObject schema)
                    {
                        continue;
                    }

                    schema.Clear();

                    // A voucher create answers with an array, because e-conomic may split the
                    // entries it was sent across several vouchers. Declaring the single shape there
                    // produced a client that posted successfully and then failed to read the reply.
                    if (isCollection)
                    {
                        schema["type"] = "array";
                        schema["items"] = new JsonObject { ["$ref"] = RefPrefix + entity };
                    }
                    else
                    {
                        schema["$ref"] = RefPrefix + entity;
                    }

                    corrected++;
                }
            }
        }

        return corrected;
    }

    /// <summary>
    /// Marks optional value-typed properties on write payloads as nullable.
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
    /// Dates have the same problem and a worse symptom. A non-nullable <c>DateOnly</c> defaults to
    /// year one, so an unset <c>dueDate</c> was sent as <c>0001-01-01</c> — a value the caller never
    /// supplied, on a property that decides when an invoice is overdue. It normally went unnoticed
    /// because e-conomic derives the due date from the payment terms and ignores what it was sent;
    /// with terms of type <c>dueDate</c>, which do not, the request was rejected outright.
    /// </para>
    /// <para>
    /// Enums are the third of the same kind, and the plainest. A C# enum defaults to its first
    /// member, so an unset <c>paymentTermsType</c> was sent as <c>"net"</c>, and e-conomic answered
    /// "Payment terms type does not match the type on the payment terms specified" for any terms
    /// that were not net — a value the caller never chose, failing a request that was correct.
    /// </para>
    /// <para>
    /// Only components used solely as request bodies are touched, so the read models keep their
    /// non-nullable numbers.
    /// </para>
    /// <para>
    /// Nested objects and array items are marked too. They have to be: an invoice line's
    /// <c>lineNumber</c> and <c>sortKey</c> both declare <c>minimum: 1</c>, so a draft invoice with
    /// a line the caller did not number was rejected outright — the same failure as
    /// <c>customerNumber</c>, one level further down. The dates repeat the pattern exactly: a line's
    /// <c>accrual</c> carries two of them.
    /// </para>
    /// </remarks>
    public static int MarkOptionalValuesNullable(JsonObject document)
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
                case "object":
                    marked += MarkObject(definition);
                    break;

                case "array" when definition["items"] is JsonObject item:
                    marked += MarkObject(item);
                    break;

                case var type when IsValueType(definition, type)
                    && !required.Contains(property)
                    && definition["nullable"] is null:
                    definition["nullable"] = true;
                    marked++;
                    break;

                default:
                    break;
            }
        }

        return marked;
    }

    /// <summary>
    /// Whether this property becomes a C# value type, and so carries a default the caller never
    /// chose unless it is nullable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An enum is recognised by its values rather than by its type, because e-conomic's own schemas
    /// declare no type for one — <c>paymentTermsType</c> is an <c>enum</c> with a <c>description</c>
    /// and nothing else. Matching on <c>"type": "string"</c> silently skipped every one of them.
    /// </para>
    /// <para>
    /// Booleans are deliberately left out, though they leak a <see langword="false"/> the same way.
    /// A <c>PUT</c> that omits <c>barred</c> clears it, so the two requests mean the same thing to
    /// e-conomic — verified against a live agreement — and making them nullable would only ask
    /// callers to distinguish an unset flag from a false one to no effect.
    /// </para>
    /// </remarks>
    private static bool IsValueType(JsonObject definition, string? type) =>
        type is "integer" or "number"
        || definition["enum"] is not null
        || (type is "string" && definition["format"]?.GetValue<string>() is "date" or "date-time");

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
