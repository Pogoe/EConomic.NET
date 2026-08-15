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
    /// <summary>
    /// Properties e-conomic declares as a date and then answers with a timestamp, keyed by
    /// <c>{schema}.{property}</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same defect the legacy pipeline corrects, arriving by a different route. There the label
    /// said <c>full-date</c> while the pattern beside it said otherwise, so the pattern could settle
    /// it mechanically. Here there is no pattern — the document simply says <c>format: date</c>, and
    /// the server sends <c>2022-05-31T00:00:00</c>. NSwag maps <c>date</c> to <c>DateOnly</c>, which
    /// cannot parse that, so every page of project employees carrying a cut-off date failed to
    /// deserialize.
    /// </para>
    /// <para>
    /// Curated rather than inferred, because the only evidence is what the server actually sends.
    /// Note this is the single property in the whole projects document declared <c>date</c>: its
    /// eleven siblings, <c>cutoffDate</c> on an activity included, are already <c>date-time</c> and
    /// answer identically. An entry matching nothing fails the run, so a corrected specification
    /// cannot leave a stale override behind.
    /// </para>
    /// </remarks>
    public static IReadOnlySet<string> Timestamps { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "ProjectEmployee.cutOffDate",
    };

    /// <summary>
    /// Properties e-conomic lists in <c>required</c> and also declares <c>nullable</c>, keyed by
    /// <c>{schema}.{property}</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A contradiction in the specification, and the two halves disagree about the thing that
    /// matters. <see cref="MarkObject"/> leaves any property carrying an explicit <c>nullable</c>
    /// alone — that is the right default, since it is e-conomic stating the intent — so the property
    /// is emitted optional and the compiler never asks the caller for it. The server then refuses the
    /// create: <c>POST /Employees</c> without a number answers <c>400 InvalidInput</c>,
    /// "Required property is missing", naming <c>Number</c>.
    /// </para>
    /// <para>
    /// Dropping the <c>nullable</c> is safe in the reading direction too, and that was checked rather
    /// than assumed: every employee the projects service returns carries a number. A property that is
    /// genuinely absent sometimes must not be listed here, whatever the <c>required</c> array claims.
    /// </para>
    /// <para>
    /// One entry across all fourteen services, so this is a defect in one document rather than a
    /// shape worth inferring a rule from. An entry matching nothing fails the run.
    /// </para>
    /// </remarks>
    public static IReadOnlySet<string> RequiredNotNullable { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Employee.number",
    };

    /// <summary>Marks every optional value-typed property nullable, throughout a document.</summary>
    /// <param name="document">An OpenAPI document, modified in place.</param>
    /// <param name="corrected">Collects the <see cref="Timestamps"/> entries this document used.</param>
    /// <param name="flattened">Collects the paths whose path-parameter enumeration was dropped.</param>
    /// <param name="unnulled">Collects the <see cref="RequiredNotNullable"/> entries this document used.</param>
    /// <returns>The number of properties marked.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static int Prepare(
        JsonObject document,
        ICollection<string>? corrected = null,
        ICollection<string>? flattened = null,
        ICollection<string>? unnulled = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var marked = 0;

        // The schema corrections need schemas; the path ones do not, and gating them on a
        // `components` section would make a document without one silently skip work that has nothing
        // to do with it.
        if (document["components"]?["schemas"] is JsonObject schemas)
        {
            // Before MarkObject, which reads `nullable` to decide what to leave alone: resolving the
            // contradiction first means the property is seen as the required one it is.
            marked += DropContradictoryNullable(schemas, unnulled);

            foreach (var (_, schema) in schemas)
            {
                if (schema is JsonObject definition)
                {
                    marked += MarkObject(definition);
                }
            }

            marked += CorrectTimestamps(schemas, corrected);
        }

        marked += AllowEmptySuccess(document);
        marked += DropErrorResponses(document);
        marked += FlattenPathEnums(document, flattened);

        return marked;
    }

    /// <summary>
    /// Drops the inline enumeration from a path parameter that also references a component.
    /// </summary>
    /// <param name="document">An OpenAPI document, modified in place.</param>
    /// <param name="flattened">Collects the paths affected, for the caller's report.</param>
    /// <returns>The number of parameters flattened.</returns>
    /// <remarks>
    /// <para>
    /// The quote-to-cash service scopes eight of its listings by <c>{documentStatus}</c>, and
    /// declares that parameter with an inline <c>enum</c> of <c>drafts</c>, <c>sent</c> and
    /// <c>archived</c> sitting <em>beside</em> an <c>allOf</c> reference to
    /// <c>SalesDocumentStatusRoute</c> — which is itself nothing but <c>type: string</c>. The values
    /// are therefore in the wrong place: the component that should carry them does not, and each
    /// parameter carries its own copy.
    /// </para>
    /// <para>
    /// NSwag reads each copy as a distinct anonymous schema and mints an enum per operation —
    /// <c>DocumentStatus</c> through <c>DocumentStatus8</c>, eight mutually incompatible types for
    /// one path segment. Dropping the inline copy leaves the reference, so the parameter generates as
    /// the <c>string</c> it always was.
    /// </para>
    /// <para>
    /// Nothing is lost at the public surface. The facade does not ask callers for the status at all:
    /// it publishes one accessor per value — <c>SalesDraftOrderLines</c>, <c>SalesSentOrderLines</c>,
    /// <c>SalesArchivedOrderLines</c> — which is how the legacy surface already models the documents
    /// these lines belong to, and gives the caller the same compile-time choice an enum would.
    /// </para>
    /// </remarks>
    private static int FlattenPathEnums(JsonObject document, ICollection<string>? flattened)
    {
        var dropped = 0;

        foreach (var (path, item) in document["paths"]?.AsObject() ?? [])
        {
            foreach (var (_, node) in item?.AsObject() ?? [])
            {
                foreach (var parameter in (node as JsonObject)?["parameters"]?.AsArray() ?? [])
                {
                    // Only a path parameter, and only where the reference is already there to carry
                    // the type: an inline enum with nothing beside it is the parameter's whole
                    // definition and removing it would lose the type outright.
                    if (parameter is not JsonObject declared
                        || declared["in"]?.GetValue<string>() != "path"
                        || declared["schema"] is not JsonObject schema
                        || schema["enum"] is null
                        || schema["allOf"] is null)
                    {
                        continue;
                    }

                    schema.Remove("enum");
                    flattened?.Add(path);
                    dropped++;
                }
            }
        }

        return dropped;
    }

    /// <summary>Removes a <c>nullable</c> that contradicts the schema's own <c>required</c> array.</summary>
    /// <param name="schemas">The document's schemas, modified in place.</param>
    /// <param name="applied">Collects the entries corrected, for the caller's rot guard.</param>
    /// <returns>The number of properties corrected.</returns>
    private static int DropContradictoryNullable(JsonObject schemas, ICollection<string>? applied)
    {
        var corrected = 0;

        foreach (var entry in RequiredNotNullable)
        {
            var separator = entry.IndexOf('.', StringComparison.Ordinal);
            var schema = entry[..separator];
            var property = entry[(separator + 1)..];

            // Prepare runs over every service in turn, so finding nothing here is the norm. The rot
            // guard belongs to the caller, which sees every document.
            if (schemas[schema] is not JsonObject definition
                || definition["properties"]?[property] is not JsonObject declared
                || declared["nullable"] is null)
            {
                continue;
            }

            // Only where the schema really does contradict itself. A `nullable` on a property that
            // is not required is e-conomic stating the intent, and must be left alone.
            var required = definition["required"]?.AsArray()
                .Any(r => r?.GetValue<string>() == property) ?? false;

            if (!required)
            {
                continue;
            }

            declared.Remove("nullable");
            applied?.Add(entry);
            corrected++;
        }

        return corrected;
    }

    /// <summary>Promotes a mislabelled date to the timestamp the server actually sends.</summary>
    /// <param name="schemas">The document's schemas, modified in place.</param>
    /// <param name="applied">Collects the entries corrected, for the caller's rot guard.</param>
    /// <returns>The number of properties corrected.</returns>
    private static int CorrectTimestamps(JsonObject schemas, ICollection<string>? applied)
    {
        var corrected = 0;

        foreach (var entry in Timestamps)
        {
            var separator = entry.IndexOf('.', StringComparison.Ordinal);
            var schema = entry[..separator];
            var property = entry[(separator + 1)..];

            // Only the document that declares the schema can correct it, and Prepare runs over every
            // service in turn, so finding nothing here is the norm rather than a problem. The rot
            // guard belongs to the caller, which sees every document: an entry no document uses is
            // describing a specification that has since been fixed.
            if (schemas[schema]?["properties"]?[property] is not JsonObject declared
                || declared["format"]?.GetValue<string>() != "date")
            {
                continue;
            }

            declared["format"] = "date-time";
            applied?.Add(entry);
            corrected++;
        }

        return corrected;
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
