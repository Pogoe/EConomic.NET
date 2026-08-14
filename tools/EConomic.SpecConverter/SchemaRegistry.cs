using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EConomic.SpecConverter;

/// <summary>
/// Collects converted schemas into a set of reusable components, deduplicating by structure.
/// </summary>
/// <remarks>
/// The legacy schemas contain no <c>$ref</c> at all — every file inlines every type — so the same
/// entity is redefined many times over. Worse, the same <c>title</c> does not imply the same shape:
/// across the 160 files, 28 titles cover more than one structure, and <c>Customer</c> alone appears
/// in 6 different forms (the full entity in one place, a two-field reference stub in another).
/// <para>
/// Deduplicating by title would therefore merge incompatible types. Identity here is the structure
/// itself: schemas are keyed by a canonical form with object keys sorted, so identical shapes
/// collapse to one component and different shapes keep separate names no matter what they are
/// called.
/// </para>
/// </remarks>
public sealed class SchemaRegistry
{
    /// <summary>
    /// Curated replacements for mechanically generated names, keyed by the generated name.
    /// The converter's collision report is the worklist: anything that came out as
    /// <c>Entry2</c> or similar deserves a real name here.
    /// </summary>
    public static readonly Dictionary<string, string> NameOverrides = new(StringComparer.Ordinal)
    {
        // Across the legacy files the same title is reused for a collection item and for the
        // richer single-resource GET, which adds `metaData`. The item keeps the plain entity name;
        // the single-resource shape takes a "Details" suffix.
        ["Account2"] = "AccountDetails",
        ["Customer2"] = "CustomerDetails",
        ["CustomerContact2"] = "CustomerContactDetails",
        ["Department2"] = "DepartmentDetails",
        ["Unit2"] = "UnitDetails",

        // Entities that appear both embedded in another resource and as their own endpoint. The
        // standalone one deserves the plain name, so the embedded copy is qualified.
        ["BookedInvoice"] = "CustomerBookedInvoice",
        ["BookedInvoice2"] = "BookedInvoice",
        ["DraftInvoice"] = "CustomerDraftInvoice",
        ["DraftInvoice2"] = "DraftInvoice",

        // /invoices/booked, /overdue, /paid and /unpaid return the same envelope over different
        // invoice shapes; name each after the endpoint that produces it.
        ["BookedInvoiceCollection2"] = "OverdueInvoiceCollection",
        ["BookedInvoiceCollection3"] = "PaidInvoiceCollection",
        ["BookedInvoiceCollection4"] = "UnpaidInvoiceCollection",

        // Titles that describe the endpoint rather than the type it returns.
        ["CurrenciesCollection"] = "CurrencyCollection",
        ["CurrenciesCollection2"] = "CurrencyDetails",
        ["Employees"] = "EmployeeCollection",
        ["Employees2"] = "EmployeeDetails",
        ["Journals"] = "JournalCollection",
        ["Journals2"] = "JournalDetails",
        ["CustomerGroupsCollection"] = "CustomerGroupCollection",
        ["CustomerGroupsCollection2"] = "CustomerGroupCustomerCollection",
        ["ProductsCollection"] = "ProductCollection",

        // The 5-property variant is a totals row rather than an entry.
        ["Entry2"] = "AccountTotalsEntry",
        ["EntriesCollection"] = "EntryCollection",

        // Four totals shapes under /invoices/totals/drafts, distinguished by what they group by.
        ["DraftInvoiceTotals2"] = "CustomerDraftInvoiceTotals",
        ["DraftInvoiceTotals3"] = "DraftInvoiceTotalsForAccountingYear",
        ["DraftInvoiceTotals4"] = "DraftInvoiceTotalsForPeriod",
        ["DraftInvoiceTotals5"] = "EmployeeDraftInvoiceTotalsSummary",
        ["DraftInvoiceTotals6"] = "EmployeeDraftInvoiceTotals",

        // The PUT payloads carry `lines` where the collection item carries `lastUpdated`.
        ["DraftOrder2"] = "DraftOrderPUT",
        ["DraftQuote2"] = "DraftQuotePUT",

        // The POST body, which drops `self`. Named for symmetry with DeliveryLocationPUT.
        ["CustomersDeliveryLocation"] = "DeliveryLocationPOST",

        // Reserving the unqualified name for the owning resource stops an earlier resource taking
        // it, but the owner's own shape then deduplicates into the already-registered qualified
        // one. These two are the resulting misnomers: nothing else holds the plain name.
        ["ProductGroupsProduct"] = "Product",
        ["ProductGroupsVatZone"] = "VatZone",

        // Two real shapes: /departmental-distributions omits `barred`, the per-type endpoints
        // include it. Each has its own collection envelope.
        ["DepartmentalDistribution"] = "DepartmentalDistributionSummary",
        ["DepartmentalDistribution2"] = "DepartmentalDistribution",
        ["DepartmentalDistribution3"] = "DepartmentalDistributionCollection",
        ["DepartmentalDistributionReference"] = "DepartmentalDistributionSummaryCollection",
    };

    /// <summary>
    /// Entities exposed publicly: filter surfaces, models and client properties are emitted only
    /// for these. Every entry is public API, so the set grows deliberately, one resource at a time.
    /// </summary>
    public static readonly IReadOnlySet<string> PublishedEntities = new HashSet<string>(StringComparer.Ordinal)
    {
        "Account", "AccountingYear", "AppRole", "Currency", "Customer", "CustomerGroup",
        "Department", "DepartmentalDistributionSummary", "Employee", "Journal", "Layout",
        "PaymentTerms", "PaymentType", "Product", "ProductGroup", "Supplier", "Unit",
        "VatAccount", "VatType", "VatZone",
    };

    /// <summary>
    /// Entities whose client property is a resource — exposing writes alongside the query — rather
    /// than a bare query. A resource type is hand-written per entity, so this set grows only as
    /// each one lands, and must stay a subset of <see cref="PublishedEntities"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> WriteEnabledEntities = new HashSet<string>(StringComparer.Ordinal)
    {
        // AccountingYear is absent: it is keyed by `year` rather than a `{Entity}Number` property,
        // which the key convention here does not express. Its create otherwise works — verified
        // live, returning `"year": "2027"` and a self link.
        "Customer", "CustomerGroup", "PaymentTerms", "Product", "Supplier", "Unit",
    };

    /// <summary>
    /// Entities whose resource supports <c>DELETE</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Derived from the published documentation, not from <c>specs/</c>.</strong> e-conomic
    /// publishes one JSON schema per request or response body and <c>DELETE</c> has neither, so no
    /// schema exists for any of these and nothing here can be checked against the specs.
    /// </para>
    /// <para>
    /// Each entry was read from <see href="https://restdocs.e-conomic.com/"/>. An entity absent
    /// here means the documentation does not describe a delete for it — <c>accounting-years</c> is
    /// the case in point — not that the generator failed to find one.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<string> DeletableEntities = new HashSet<string>(StringComparer.Ordinal)
    {
        "Customer", "CustomerGroup", "PaymentTerms", "Product", "Supplier", "Unit",
    };

    private readonly Dictionary<string, string> _namesByStructure = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _schemasByName = new(StringComparer.Ordinal);
    private readonly List<SchemaCollision> _collisions = [];

    /// <summary>Registered components, keyed by generated name.</summary>
    public IReadOnlyDictionary<string, JsonObject> Schemas => _schemasByName;

    /// <summary>
    /// Resource currently being converted. Used to name structural variants after where they occur
    /// — <c>InvoicesCustomer</c> rather than <c>Customer4</c> — which is far easier to review than
    /// a counter.
    /// </summary>
    public string? Context { get; set; }

    /// <summary>
    /// Base name to the resource that owns it, so the unqualified name goes to the entity's home.
    /// Without this the winner is whichever resource happens to sort first: <c>customer-groups</c>
    /// precedes <c>customers</c>, so its 27-property variant would take the name <c>Customer</c>
    /// and the real 36-property customer would end up as <c>Customer2</c>.
    /// </summary>
    public IDictionary<string, string> HomeResources { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Titles that resolved to more than one distinct structure. Each is a candidate for a curated
    /// name override, since the generated fallback names are only mechanically correct.
    /// </summary>
    public IReadOnlyList<SchemaCollision> Collisions => _collisions;

    /// <summary>
    /// Registers a schema and returns the component name to reference it by. Structurally identical
    /// schemas return the same name.
    /// </summary>
    /// <param name="schema">The converted schema.</param>
    /// <param name="fallbackName">Name to use when the schema has no usable title.</param>
    /// <returns>The component name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is <see langword="null"/>.</exception>
    public string Register(JsonObject schema, string fallbackName)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var structure = Canonicalize(schema);
        if (_namesByStructure.TryGetValue(structure, out var existing))
        {
            // The two shapes are the same type; keep whichever annotations either of them carried.
            MergeAnnotations(_schemasByName[existing], schema);
            return existing;
        }

        var title = schema["title"]?.GetValue<string>();
        var baseName = Identifier(string.IsNullOrWhiteSpace(title) ? fallbackName : title);
        // Only the owning resource may claim the unqualified name.
        var reserved = HomeResources.TryGetValue(baseName, out var home)
            && !string.Equals(Context is null ? null : Identifier(Context), home, StringComparison.Ordinal);

        // Curated names are applied as a rename once every document is built, not here: renaming
        // during registration would free up the base name and change the very names the override
        // table is keyed on.
        var name = Disambiguate(baseName, schema, reserved);

        _namesByStructure[structure] = name;
        _schemasByName[name] = schema;
        return name;
    }

    /// <summary>
    /// Replaces every titled nested object inside <paramref name="root"/> with a <c>$ref</c> to a
    /// registered component, returning the rewritten root.
    /// </summary>
    /// <remarks>
    /// Without this the entities stay inlined and every endpoint grows its own copy of Customer,
    /// Entry and friends. Extraction is bottom-up, so an entity nested inside another is itself
    /// shared rather than duplicated into its parent's component.
    /// </remarks>
    /// <param name="root">The converted root schema. It is not itself extracted.</param>
    /// <returns>The root with nested entities replaced by references.</returns>
    public JsonObject ExtractNested(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return (JsonObject)Rewrite(root, isRoot: true)!;
    }

    private JsonNode? Rewrite(JsonNode? node, bool isRoot)
    {
        switch (node)
        {
            case JsonArray array:
                var items = new JsonArray();
                foreach (var item in array)
                {
                    items.Add(Rewrite(item, isRoot: false));
                }

                return items;

            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    copy[key] = key switch
                    {
                        "properties" when value is JsonObject properties => RewriteProperties(properties),
                        "items" or "oneOf" or "allOf" or "anyOf" => Rewrite(value, isRoot: false),
                        _ => value?.DeepClone(),
                    };
                }

                if (!isRoot && IsNamedEntity(copy))
                {
                    var name = Register(copy, "Schema");
                    return new JsonObject { ["$ref"] = $"#/components/schemas/{name}" };
                }

                return copy;

            default:
                return node?.DeepClone();
        }
    }

    private JsonObject RewriteProperties(JsonObject properties)
    {
        var rewritten = new JsonObject();
        foreach (var (name, schema) in properties)
        {
            rewritten[name] = Rewrite(schema, isRoot: false);
        }

        return rewritten;
    }

    /// <summary>
    /// Annotations describing what an <em>endpoint</em> allows, not what the type is.
    /// </summary>
    /// <remarks>
    /// A collection endpoint marks its item's properties filterable and sortable; the
    /// single-resource GET of the very same entity does not, because there is nothing to filter.
    /// Treating these as part of the type's identity splits one entity into two — that is exactly
    /// how the departmental distributions ended up with duplicate shapes. They are excluded from
    /// identity and merged across occurrences instead, so the entity stays one type and keeps the
    /// filter metadata from whichever endpoint published it.
    /// </remarks>
    private static readonly HashSet<string> EndpointAnnotations = new(StringComparer.Ordinal)
    {
        "x-filterable", "x-sortable", "x-default-sorting",
    };

    /// <summary>Copies endpoint annotations from <paramref name="source"/> into an equivalent schema.</summary>
    private static void MergeAnnotations(JsonNode? target, JsonNode? source)
    {
        if (target is JsonArray targetItems && source is JsonArray sourceItems)
        {
            for (var i = 0; i < Math.Min(targetItems.Count, sourceItems.Count); i++)
            {
                MergeAnnotations(targetItems[i], sourceItems[i]);
            }

            return;
        }

        if (target is not JsonObject targetObject || source is not JsonObject sourceObject)
        {
            return;
        }

        foreach (var (key, value) in sourceObject)
        {
            if (EndpointAnnotations.Contains(key))
            {
                targetObject[key] ??= value?.DeepClone();
            }
            else if (targetObject[key] is { } existing)
            {
                MergeAnnotations(existing, value);
            }
        }
    }

    private static bool IsNamedEntity(JsonObject schema) =>
        schema["title"] is JsonValue && schema["properties"] is JsonObject;

    private string Disambiguate(string baseName, JsonObject schema, bool reserved = false)
    {
        if (!reserved && !_schemasByName.ContainsKey(baseName))
        {
            return baseName;
        }

        // A stub carrying little more than a `self` link is a reference to the entity, not the
        // entity, and that is by far the most common reason one title covers two shapes.
        if (LooksLikeReference(schema) && !_schemasByName.ContainsKey($"{baseName}Reference"))
        {
            _collisions.Add(new SchemaCollision(baseName, $"{baseName}Reference"));
            return $"{baseName}Reference";
        }

        if (!string.IsNullOrWhiteSpace(Context))
        {
            var context = Identifier(Context);

            // Skip the prefix when it would stutter: the resource `customers` must not turn
            // `Customer` into `CustomersCustomer`, nor `currencies` into `CurrenciesCurrenciesCollection`.
            var stutters = baseName.StartsWith(context, StringComparison.Ordinal)
                || string.Equals(context, baseName, StringComparison.Ordinal)
                || string.Equals(context, baseName + "s", StringComparison.Ordinal)
                || string.Equals(context, baseName + "es", StringComparison.Ordinal);

            var qualified = $"{context}{baseName}";
            if (!stutters && !_schemasByName.ContainsKey(qualified))
            {
                _collisions.Add(new SchemaCollision(baseName, qualified));
                return qualified;
            }
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseName}{suffix}";
            if (!_schemasByName.ContainsKey(candidate))
            {
                _collisions.Add(new SchemaCollision(baseName, candidate));
                return candidate;
            }
        }
    }

    private static bool LooksLikeReference(JsonObject schema) =>
        schema["properties"] is JsonObject properties
        && properties.Count <= 3
        && properties.ContainsKey("self");

    /// <summary>
    /// Produces a structural fingerprint: the same JSON with every object's keys sorted, so that
    /// two schemas differing only in property order compare equal. Descriptions are excluded —
    /// prose differences between endpoints do not make a different type.
    /// </summary>
    private static string Canonicalize(JsonNode? node)
    {
        const string Description = "description";
        const string Title = "title";

        var builder = new StringBuilder();
        Write(node, builder);
        return builder.ToString();

        static void Write(JsonNode? current, StringBuilder output)
        {
            switch (current)
            {
                case JsonObject obj:
                    output.Append('{');
                    foreach (var (key, value) in obj
                        .Where(p => !string.Equals(p.Key, Description, StringComparison.Ordinal))
                        .Where(p => !EndpointAnnotations.Contains(p.Key))
                        .OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        output.Append(JsonSerializer.Serialize(key)).Append(':');

                        // Compare titles in their identifier form. e-conomic titles the same type
                        // "Layout" in one file and "Layout GET schema" in another; both mean Layout,
                        // and treating them as different would emit two identical components.
                        if (string.Equals(key, Title, StringComparison.Ordinal)
                            && value is JsonValue titleValue
                            && titleValue.TryGetValue<string>(out var text)
                            && !string.IsNullOrWhiteSpace(text))
                        {
                            output.Append(JsonSerializer.Serialize(Identifier(text)));
                        }
                        else
                        {
                            Write(value, output);
                        }

                        output.Append(',');
                    }

                    output.Append('}');
                    break;

                case JsonArray array:
                    output.Append('[');
                    foreach (var item in array)
                    {
                        Write(item, output);
                        output.Append(',');
                    }

                    output.Append(']');
                    break;

                default:
                    output.Append(current?.ToJsonString() ?? "null");
                    break;
            }
        }
    }

    /// <summary>Turns a human title such as "Customer collection GET schema" into an identifier.</summary>
    /// <param name="text">The title.</param>
    /// <returns>A PascalCase identifier.</returns>
    public static string Identifier(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        // "schema" and "GET schema" are noise on almost every title.
        var words = text
            .Split([' ', '-', '_', '.', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.Equals("schema", StringComparison.OrdinalIgnoreCase))
            .Where(w => !w.Equals("get", StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Trim(',', ':', '(', ')'))
            .Where(w => w.Length > 0)
            .ToList();

        if (words.Count == 0)
        {
            words.Add("Schema");
        }

        var builder = new StringBuilder();
        foreach (var word in words)
        {
            var cleaned = new string([.. word.Where(char.IsLetterOrDigit)]);
            if (cleaned.Length == 0)
            {
                continue;
            }

            builder.Append(char.ToUpperInvariant(cleaned[0]));
            if (cleaned.Length > 1)
            {
                // Keep existing internal capitalisation: "customerNumber" -> "CustomerNumber",
                // but do not flatten an already-PascalCase word.
                builder.Append(cleaned[1..]);
            }
        }

        var identifier = builder.ToString();
        return char.IsDigit(identifier[0]) ? $"Schema{identifier}" : identifier;
    }
}

/// <summary>A title that resolved to more than one structure.</summary>
/// <param name="Title">The identifier the schema wanted.</param>
/// <param name="AssignedName">The name actually assigned to the second and later shapes.</param>
public sealed record SchemaCollision(string Title, string AssignedName);
