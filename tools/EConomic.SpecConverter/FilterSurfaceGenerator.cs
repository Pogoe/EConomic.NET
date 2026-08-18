using System.Text;
using System.Text.Json.Nodes;

namespace EConomic.SpecConverter;

/// <summary>One filterable or sortable field, resolved to a C# property.</summary>
/// <param name="Path">The field name as e-conomic spells it, e.g. <c>customerGroup.customerGroupNumber</c>.</param>
/// <param name="PropertyName">The C# property name.</param>
/// <param name="FieldType">The field type that exposes the permitted operators.</param>
public sealed record SurfaceField(string Path, string PropertyName, string FieldType);

/// <summary>
/// Generates the filter and sort surfaces consumers write lambdas against.
/// </summary>
/// <remarks>
/// <para>
/// These are the one deliberate exception to "generated code is never public". The whole point of
/// the surface is that a consumer's <c>Where</c> lambda fails to compile when e-conomic would
/// reject it, which only works if the surface is public. It also means a property losing
/// <c>x-filterable</c> is a breaking change that shows up in <c>PublicAPI.Unshipped.txt</c> —
/// which is correct: it breaks callers either way, and better at build time than at run time.
/// </para>
/// <para>
/// The legacy schemas publish filterability as a boolean, unlike the newer services which publish
/// a per-property operator list. Operator sets here are therefore <em>inferred from the property
/// type</em>. That inference is a guess in the safe direction and is labelled as one in the
/// generated file; it must never be presented as coming from the spec.
/// </para>
/// </remarks>
public static class FilterSurfaceGenerator
{
    /// <summary>
    /// Entities whose surfaces are emitted. Every entry is public API, so the list grows as each
    /// facade lands rather than exposing types nothing can be used against. Emptying it emits
    /// everything. Shared with the facade generator, so the two stay in step.
    /// </summary>
    public static IReadOnlySet<string> PublishedEntities => SchemaRegistry.PublishedEntities;

    /// <summary>
    /// Fields the specifications mark filterable that the server refuses, keyed by the published
    /// entity name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The specifications were known to under-report — they omit <c>pNumber</c> on customers, which
    /// is what <c>WhereRaw</c> is for. They also over-report, which is worse: an over-reported field
    /// compiles and then fails at run time, which is precisely what the filter surface exists to
    /// prevent. e-conomic's own schema marks a customer group's <c>account.accountNumber</c>
    /// filterable; the server's list for that resource is <c>name</c> and <c>customerGroupNumber</c>.
    /// </para>
    /// <para>
    /// Nothing offline can find these, because the specification is the only offline authority and
    /// it is the thing that is wrong. Every entry below was read from a live agreement by
    /// <c>FilterSurfaceTests</c>, which is what keeps the list honest: an entry that becomes
    /// unnecessary makes this generator fail, and a field that starts being over-reported fails
    /// that test.
    /// </para>
    /// <para>
    /// "Refuses" covers two answers, and the second is the one the server's own list cannot reveal.
    /// Most of these are absent from <c>allowedFilteringFields</c> and answered <c>400</c>. The
    /// rest are listed there as filterable and answered <c>500</c> when actually used — a crash
    /// rather than a rejection, found only by sending each operator on each field.
    /// </para>
    /// </remarks>
    public static IReadOnlySet<string> UnfilterableFields { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // A nested account is filterable in its own right at /accounts, and not here.
        "CustomerGroup.account.accountNumber",
        "CustomerGroup.account.accountType",
        "CustomerGroup.account.balance",
        "CustomerGroup.account.blockDirectEntries",
        "CustomerGroup.account.debitCredit",
        "CustomerGroup.account.name",

        // The same field on the contact itself, and refused for the same reason: the server's list
        // for /customers/{n}/contacts is customerContactNumber, name, email, eInvoiceId, phone,
        // comments and sortKey. Found only once nested collections were swept — they were invisible
        // to this check until they gained WhereRaw, which is what it recognises a queryable by.
        "CustomerContact.customer.customerNumber",

        // The server filters invoices on customer.customerNumber, but not on the same number
        // reached through the customer contact.
        "BookedInvoice.references.customerContact.customer.customerNumber",
        "CustomerBookedInvoice.references.customerContact.customer.customerNumber",
        "CustomerDraftInvoice.references.customerContact.customer.customerNumber",
        "DraftInvoice.references.customerContact.customer.customerNumber",
        "NotDueInvoice.references.customerContact.customer.customerNumber",
        "OverdueInvoice.references.customerContact.customer.customerNumber",
        "PaidInvoice.references.customerContact.customer.customerNumber",
        "UnpaidInvoice.references.customerContact.customer.customerNumber",

        // The server's list for employees is employeeNumber, name and barred, and no more.
        "Employee.email",
        "Employee.phone",
        "Employee.employeeGroup.employeeGroupNumber",

        // Product filtering stops at the top level; the inventory block is not part of it.
        "Product.inventory.grossWeight",
        "Product.inventory.netWeight",
        "Product.inventory.packageVolume",
        "Product.inventory.recommendedCostPrice",

        // The rest of this list is fields the server lists as filterable and then answers with a
        // 500 rather than a result — reproduced on the demo agreement, so they are e-conomic's
        // bugs and not this agreement's data. A field that crashes the server is a worse promise
        // than one that is merely rejected, so they come out of the surface on the same terms.
        // WhereRaw still reaches them, for whenever they start working.

        // Filtering products on a departmental distribution is a 500 on every operator, though
        // /departmental-distributions filters on the same number perfectly well.
        "Product.departmentalDistribution.departmentalDistributionNumber",

        // Only $eq: survives here: products.barred$ne: and barred$eq:$null: are both 500s, where
        // customers.barred answers all three. Since the operators cannot be narrowed one at a
        // time, the field goes rather than the surface keeping two clauses that crash.
        "Product.barred",

        // Orders and quotes 500 on their payment terms number, in every view. Invoices carry the
        // same nested block and filter on it correctly, which is what makes this a bug rather than
        // a field that was never meant to be filterable.
        "ArchivedOrder.paymentTerms.paymentTermsNumber",
        "ArchivedQuote.paymentTerms.paymentTermsNumber",
        "DraftOrder.paymentTerms.paymentTermsNumber",
        "DraftQuote.paymentTerms.paymentTermsNumber",
        "SentOrder.paymentTerms.paymentTermsNumber",
        "SentQuote.paymentTerms.paymentTermsNumber",
    };

    /// <summary>
    /// Fields the specifications mark sortable that the server refuses.
    /// </summary>
    /// <remarks>
    /// Sortability is published and wrong independently of filterability, so this is a separate
    /// list rather than the same one. An unsortable field is answered with "Could not parse query
    /// string sort parameter" and no list of alternatives, so these were found by sorting on each
    /// field in turn against a live agreement.
    /// </remarks>
    public static IReadOnlySet<string> UnsortableFields { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "CustomerGroup.account.accountNumber",
        "CustomerGroup.account.accountType",
        "CustomerGroup.account.balance",
        "CustomerGroup.account.blockDirectEntries",
        "CustomerGroup.account.debitCredit",
        "CustomerGroup.account.name",

        "CustomerContact.customer.customerNumber",

        "BookedInvoice.references.customerContact.customer.customerNumber",
        "CustomerBookedInvoice.references.customerContact.customer.customerNumber",
        "CustomerDraftInvoice.references.customerContact.customer.customerNumber",
        "DraftInvoice.references.customerContact.customer.customerNumber",
        "NotDueInvoice.references.customerContact.customer.customerNumber",
        "OverdueInvoice.references.customerContact.customer.customerNumber",
        "PaidInvoice.references.customerContact.customer.customerNumber",
        "UnpaidInvoice.references.customerContact.customer.customerNumber",

        "Employee.email",
        "Employee.phone",
        "Employee.employeeGroup.employeeGroupNumber",

        // Filterable but not sortable, which is the pairing that makes these two separate flags.
        "Product.barred",
        "Product.inventory.grossWeight",
        "Product.inventory.netWeight",
        "Product.inventory.packageVolume",

        // Orders and quotes sort on their own columns, not on the payment terms they reference.
        "ArchivedOrder.paymentTerms.paymentTermsNumber",
        "DraftOrder.paymentTerms.paymentTermsNumber",
        "SentOrder.paymentTerms.paymentTermsNumber",
        "ArchivedQuote.paymentTerms.paymentTermsNumber",
        "DraftQuote.paymentTerms.paymentTermsNumber",
        "SentQuote.paymentTerms.paymentTermsNumber",
    };

    /// <summary>Generates the surfaces for every collection entity in a merged document.</summary>
    /// <param name="document">The merged OpenAPI document.</param>
    /// <param name="namespaceName">Namespace to emit into.</param>
    /// <returns>The C# source.</returns>
    public static string Generate(JsonObject document, string namespaceName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);

        var schemas = document["components"]!["schemas"]!.AsObject();
        var entities = CollectionEntities(document);
        var published = EndpointFields(document, schemas);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated>");
        builder.AppendLine("//     Generated by tools/EConomic.SpecConverter (filters).");
        builder.AppendLine("//     Regenerate after a spec refresh; do not edit by hand.");
        builder.AppendLine("// </auto-generated>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine("using EConomic.Querying;");
        builder.AppendLine();
        builder.AppendLine($"namespace {namespaceName};");

        foreach (var entity in entities)
        {
            if (PublishedEntities.Count > 0 && !PublishedEntities.Contains(entity))
            {
                continue;
            }

            var schema = schemas[entity]?.AsObject();
            if (schema is null)
            {
                continue;
            }

            var allowed = published.GetValueOrDefault(entity)
                ?? new EndpointSurface(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
            var filterable = new List<SurfaceField>();
            var sortable = new List<SurfaceField>();
            Collect(
                schema,
                schemas,
                prefix: string.Empty,
                csharpPrefix: string.Empty,
                allowed,
                filterable,
                sortable,
                depth: 0);

            var name = SchemaRegistry.PublicName(entity);
            emitted.Add(name);

            filterable.RemoveAll(f => Refused(UnfilterableFields, name, f, used));
            sortable.RemoveAll(f => Refused(UnsortableFields, name, f, used));

            // Both surfaces are emitted even when empty. An empty filter surface is not a gap in
            // the generator, it is the accurate statement that e-conomic will not filter this
            // resource on anything — and the facade needs the type to exist either way.
            AppendSurface(builder, $"{name}Filter", entity, filterable, isFilter: true);
            AppendSurface(builder, $"{name}Sort", entity, sortable, isFilter: false);
        }

        // The same discipline the curated names follow: an entry that no longer matches anything
        // fails the run rather than sitting there describing a field that has since changed. Only
        // entities that were actually emitted count, since the rest are gated out deliberately.
        var stale = UnfilterableFields.Concat(UnsortableFields)
            .Where(e => !used.Contains(e) && emitted.Contains(e[..e.IndexOf('.', StringComparison.Ordinal)]))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();

        if (stale.Count > 0)
        {
            throw new InvalidOperationException(
                "These fields are listed as refused by the server, but the surface no longer offers "
                + $"them: {string.Join(", ", stale)}. Remove them, or the list is describing a field "
                + "that has since changed.");
        }

        return builder.ToString();
    }

    /// <summary>Entity types that appear as the items of a collection response, in name order.</summary>
    /// <param name="document">The merged OpenAPI document.</param>
    /// <returns>The entity component names.</returns>
    public static IReadOnlyList<string> CollectionEntities(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var schemas = document["components"]!["schemas"]!.AsObject();
        var entities = new SortedSet<string>(StringComparer.Ordinal);

        // Only collection endpoints accept filter and sort, so only their item types get a surface.
        foreach (var (_, schema) in schemas)
        {
            if (schema?["properties"]?["collection"]?["items"]?["$ref"]?.GetValue<string>() is { } reference
                && reference.StartsWith(SchemaReference.Prefix, StringComparison.Ordinal))
            {
                entities.Add(reference[SchemaReference.Prefix.Length..]);
            }
        }

        return [.. entities];
    }

    private static void Collect(
        JsonObject schema,
        JsonObject schemas,
        string prefix,
        string csharpPrefix,
        EndpointSurface allowed,
        List<SurfaceField> filterable,
        List<SurfaceField> sortable,
        int depth)
    {
        // e-conomic nests at most one level in practice; the guard stops a malformed spec looping.
        if (depth > 3 || schema["properties"] is not JsonObject properties)
        {
            return;
        }

        foreach (var (name, node) in properties)
        {
            if (node is not JsonObject property)
            {
                continue;
            }

            var resolved = Resolve(property, schemas);
            var path = prefix.Length == 0 ? name : $"{prefix}.{name}";
            var propertyName = Combine(csharpPrefix, name);

            // What the endpoint published, not what the component carries: the component's flags are
            // the union across every endpoint sharing its shape.
            if (allowed.Filterable.Contains(path))
            {
                filterable.Add(new SurfaceField(path, propertyName, FieldTypeFor(resolved)));
            }

            if (allowed.Sortable.Contains(path))
            {
                sortable.Add(new SurfaceField(path, propertyName, "EconomicSortField"));
            }

            if (resolved["type"]?.GetValue<string>() == "object")
            {
                Collect(resolved, schemas, path, propertyName, allowed, filterable, sortable, depth + 1);
            }
        }
    }

    /// <summary>Whether the server refuses this field, recording that the entry was needed.</summary>
    private static bool Refused(IReadOnlySet<string> refused, string entity, SurfaceField field, HashSet<string> used)
    {
        var key = $"{entity}.{field.Path}";

        if (!refused.Contains(key))
        {
            return false;
        }

        used.Add(key);
        return true;
    }

    /// <summary>What one endpoint publishes as filterable and sortable.</summary>
    private sealed record EndpointSurface(HashSet<string> Filterable, HashSet<string> Sortable);

    /// <summary>
    /// The fields each entity's own collection endpoint publishes, keyed by entity.
    /// </summary>
    /// <remarks>
    /// An entity can be the item of more than one collection — an accounting year is listed both at
    /// <c>/accounting-years</c> and under an account — and the two do not accept the same filters.
    /// Only one surface exists per entity, so it takes the endpoint the facade exposes: the
    /// top-level one, preferred here by having no path parameters and then by being the shortest.
    /// </remarks>
    private static Dictionary<string, EndpointSurface> EndpointFields(JsonObject document, JsonObject schemas)
    {
        var surfaces = new Dictionary<string, EndpointSurface>(StringComparer.Ordinal);
        var chosen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (path, item) in document["paths"]?.AsObject() ?? [])
        {
            if (item?["get"] is not JsonObject get
                || SchemaReference.Name(get["responses"]?["200"]?["content"]?["application/json"]?["schema"]) is not { } envelope
                || schemas[envelope] is not JsonObject envelopeSchema
                || SchemaReference.Name(envelopeSchema["properties"]?["collection"]?["items"]) is not { } entity)
            {
                continue;
            }

            if (chosen.TryGetValue(entity, out var incumbent) && !Closer(path, incumbent))
            {
                continue;
            }

            chosen[entity] = path;
            surfaces[entity] = new EndpointSurface(Fields(get["x-filterable-fields"]), Fields(get["x-sortable-fields"]));
        }

        return surfaces;
    }

    /// <summary>Whether the first path is the more likely one for the facade to expose.</summary>
    private static bool Closer(string candidate, string incumbent)
    {
        var candidateDepth = candidate.Count(c => c == '{');
        var incumbentDepth = incumbent.Count(c => c == '{');

        return candidateDepth != incumbentDepth
            ? candidateDepth < incumbentDepth
            : candidate.Length < incumbent.Length;
    }

    private static HashSet<string> Fields(JsonNode? node) =>
        node is JsonArray array
            ? new HashSet<string>(array.Select(f => f?.GetValue<string>()).OfType<string>(), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);


    private static JsonObject Resolve(JsonObject schema, JsonObject schemas)
    {
        if (schema["$ref"]?.GetValue<string>() is { } reference
            && reference.StartsWith(SchemaReference.Prefix, StringComparison.Ordinal)
            && schemas[reference[SchemaReference.Prefix.Length..]] is JsonObject target)
        {
            return target;
        }

        return schema;
    }

    /// <summary>
    /// Picks the field type, and therefore the operators, from the property's JSON type. The
    /// legacy schemas publish only a boolean, so this is inference rather than fact.
    /// </summary>
    private static string FieldTypeFor(JsonObject property)
    {
        var type = property["type"]?.GetValue<string>();
        var format = property["format"]?.GetValue<string>();

        // An enumerated property is a closed set of names, and the server will compare them but not
        // match inside them: accounts.debitCredit accepts $eq: and $ne: and answers $like: with a
        // parse error. Matched on the enum array rather than on a string type, because e-conomic's
        // schemas give enums no type at all — which is why they landed in the TextField default.
        if (property["enum"] is not null)
        {
            return "EqualityField<string>";
        }

        return (type, format) switch
        {
            ("boolean", _) => "BooleanField",
            ("integer", _) => "NumericField<int>",
            ("number", _) => "NumericField<decimal>",
            ("string", "date") => "ComparableField<System.DateOnly>",
            ("string", "date-time") => "ComparableField<System.DateTimeOffset>",
            _ => "TextField",
        };
    }

    /// <summary>Joins a nested path into one property name, without stuttering.</summary>
    private static string Combine(string prefix, string name)
    {
        var pascal = Pascal(name);

        if (prefix.Length == 0)
        {
            return pascal;
        }

        // customerGroup + customerGroupNumber would otherwise be CustomerGroupCustomerGroupNumber.
        return pascal.StartsWith(prefix, StringComparison.Ordinal) ? pascal : prefix + pascal;
    }

    private static string Pascal(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    private static void AppendSurface(
        StringBuilder builder,
        string typeName,
        string entity,
        List<SurfaceField> fields,
        bool isFilter)
    {
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        // Plain <c> rather than <see cref>: the entity types are internal and in another
        // namespace, so a cref would not resolve.
        builder.AppendLine(isFilter
            ? $"/// The properties e-conomic will filter <c>{entity}</c> on, each typed to the"
            : $"/// The properties e-conomic will sort <c>{entity}</c> by.");

        if (isFilter)
        {
            builder.AppendLine("/// operators it accepts. Anything absent here is not filterable.");
            builder.AppendLine("/// </summary>");
            builder.AppendLine("/// <remarks>");
            builder.AppendLine("/// Operator sets are inferred from each property's type: the legacy schemas record only");
            builder.AppendLine("/// whether a property is filterable, not which operators it accepts. The inference errs");
            builder.AppendLine("/// toward fewer operators, so it can under-report but will not produce a request the");
            builder.AppendLine("/// server rejects.");
            builder.AppendLine("/// </remarks>");
        }
        else
        {
            builder.AppendLine("/// </summary>");
        }

        builder.AppendLine($"public sealed class {typeName}");
        builder.AppendLine("{");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (!seen.Add(field.PropertyName))
            {
                continue;
            }

            builder.AppendLine($"    /// <summary>Maps to <c>{field.Path}</c>.</summary>");
            builder.AppendLine($"    [EconomicField(\"{field.Path}\")]");
            builder.AppendLine($"    public {field.FieldType} {field.PropertyName} {{ get; }} = null!;");
            builder.AppendLine();
        }

        if (fields.Count > 0)
        {
            // Trim the trailing blank line inside the class body.
            builder.Length -= Environment.NewLine.Length;
        }

        builder.AppendLine("}");
    }
}
