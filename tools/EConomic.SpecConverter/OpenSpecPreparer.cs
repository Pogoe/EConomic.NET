using System.Text.Json.Nodes;

namespace EConomic.SpecConverter;

/// <summary>
/// Prepares the OpenAPI service specifications for code generation.
/// </summary>
/// <remarks>
/// <para>
/// The originals in <c>specs/openapi/</c> are e-conomic's own and are never edited, exactly as the
/// draft-03 files are not. This writes a prepared copy alongside them, so what NSwag consumes is
/// reviewable as a diff and the pipeline stays offline.
/// </para>
/// <para>
/// One correction, and it is the same one the legacy surface needed three times over: a property
/// that is optional in the schema and a value type in C# carries a default the caller never chose,
/// and <c>System.Text.Json</c> writes it. An untouched <c>customerNumber</c> would be sent as
/// <c>0</c>, which the server rejects — verified live: "The field CustomerNumber must be between 1
/// and 999999999".
/// </para>
/// <para>
/// Unlike the legacy pipeline this cannot be limited to request-only schemas, because there are
/// none: these services describe a resource once and use that one schema for reading, creating and
/// updating alike. Marking every optional value type nullable is therefore right for both
/// directions, and honest about reads too — the server omits a property rather than sending its
/// default, so an absent value really is absent rather than zero.
/// </para>
/// </remarks>
public static class OpenSpecPreparer
{
    /// <summary>Marks every optional value-typed property nullable, throughout a document.</summary>
    /// <param name="document">An OpenAPI document, modified in place.</param>
    /// <returns>The number of properties marked.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static int Prepare(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document["components"]?["schemas"] is not JsonObject schemas)
        {
            return 0;
        }

        var marked = 0;

        foreach (var (_, schema) in schemas)
        {
            if (schema is JsonObject definition)
            {
                marked += MarkObject(definition);
            }
        }

        marked += AllowEmptySuccess(document);
        marked += DropErrorResponses(document);

        return marked;
    }

    /// <summary>
    /// Removes the declared failure responses, leaving only the successful ones.
    /// </summary>
    /// <param name="document">An OpenAPI document, modified in place.</param>
    /// <returns>The number of responses removed.</returns>
    /// <remarks>
    /// <para>
    /// Nothing is lost: the library parses <c>problem+json</c> itself and exposes it through
    /// <c>EconomicApiException</c>, so the generated per-status handling is dead weight. It is also
    /// actively harmful — NSwag deserializes a declared error straight off the stream and leaves the
    /// raw text empty, so the body never reaches the exception. A <c>409</c> arrived carrying
    /// nothing at all, which is precisely the response worth reading.
    /// </para>
    /// <para>
    /// Removing them puts every failure on NSwag's unexpected-status path, which reads the body as a
    /// string — the same path the legacy clients already take, since those documents declare no
    /// error responses to begin with. One error shape, one place that parses it.
    /// </para>
    /// </remarks>
    private static int DropErrorResponses(JsonObject document)
    {
        var removed = 0;

        foreach (var (_, item) in document["paths"]?.AsObject() ?? [])
        {
            foreach (var (_, node) in item?.AsObject() ?? [])
            {
                if (node is not JsonObject operation || operation["responses"] is not JsonObject responses)
                {
                    continue;
                }

                foreach (var status in responses.Select(r => r.Key).ToList())
                {
                    if (!status.StartsWith('2'))
                    {
                        responses.Remove(status);
                        removed++;
                    }
                }
            }
        }

        return removed;
    }

    /// <summary>
    /// Accepts <c>200</c> wherever an operation declares only <c>204</c>.
    /// </summary>
    /// <param name="document">An OpenAPI document, modified in place.</param>
    /// <returns>The number of operations corrected.</returns>
    /// <remarks>
    /// <c>DELETE /Customers/{number}</c> is documented as answering <c>204 No Content</c> and
    /// actually answers <c>200 OK</c> with an empty body. The generated client rejects any status it
    /// was not told about, so a perfectly successful delete threw. The same disagreement appears on
    /// the legacy surface in the other direction, where creates answer <c>201</c> against a document
    /// that promises <c>200</c>: either way the document describes the intent and the server decides.
    /// </remarks>
    private static int AllowEmptySuccess(JsonObject document)
    {
        var corrected = 0;

        foreach (var (_, item) in document["paths"]?.AsObject() ?? [])
        {
            foreach (var (_, node) in item?.AsObject() ?? [])
            {
                if (node is not JsonObject operation
                    || operation["responses"] is not JsonObject responses
                    || responses["204"] is null
                    || responses["200"] is not null)
                {
                    continue;
                }

                responses["200"] = new JsonObject { ["description"] = "Success" };
                corrected++;
            }
        }

        return corrected;
    }

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
            if (node is not JsonObject definition || required.Contains(property) || definition["nullable"] is not null)
            {
                continue;
            }

            switch (definition["type"]?.GetValue<string>())
            {
                case "integer" or "number" or "boolean":
                    definition["nullable"] = true;
                    marked++;
                    break;

                // A date is a string in the schema and a value type in C#.
                case "string" when definition["format"]?.GetValue<string>() is "date" or "date-time":
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
}
