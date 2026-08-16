using System.Text.Json.Nodes;

namespace EConomic.SpecConverter;

/// <summary>
/// Converts a legacy e-conomic JSON Schema draft-03 document into an OpenAPI 3.0 Schema Object.
/// </summary>
/// <remarks>
/// Only the draft-03 constructs that actually occur in e-conomic's schemas are handled, and
/// anything unrecognised is reported rather than silently dropped. Across all 160 legacy files
/// that means two real differences from draft-04+:
/// <list type="bullet">
///   <item><description>
///     <c>required</c> is a boolean on each property; draft-04+ moves it to an array on the parent.
///   </description></item>
///   <item><description>
///     <c>format: "full-date"</c> is draft-03's name for what later drafts call <c>date</c>.
///   </description></item>
/// </list>
/// e-conomic's own <c>filterable</c> and <c>sortable</c> annotations are carried across as
/// <c>x-filterable</c> and <c>x-sortable</c>, matching the vendor extensions the newer OpenAPI
/// services already use, so downstream generation sees one shape for both API surfaces.
/// <para>
/// Note the two extensions do not carry the same information. The newer services publish a
/// per-property operator list (<c>"eq, ne, like"</c>); the legacy schemas only say whether a
/// property is filterable at all, so the operator set has to be inferred from the property type
/// downstream. That inference is a guess and must be documented as one.
/// </para>
/// </remarks>
public static class Draft03Converter
{
    /// <summary>Keywords carried across unchanged.</summary>
    /// <remarks>
    /// <c>exclusiveMinimum</c> is deliberately passed straight through: draft-03 spells it as a
    /// boolean modifier on <c>minimum</c>, and OpenAPI 3.0 follows draft-04, which spells it the
    /// same way. Targeting OpenAPI 3.1 later would require converting it to a number.
    /// </remarks>
    private static readonly HashSet<string> PassThrough = new(StringComparer.Ordinal)
    {
        "type", "title", "description", "enum", "readOnly", "default",
        "maxLength", "minLength", "minimum", "maximum", "pattern", "maxItems", "minItems",
        "exclusiveMinimum", "exclusiveMaximum", "uniqueItems",
    };

    /// <summary>
    /// Where a <c>oneOf</c>'s merged fields wait until the containing object can take them.
    /// </summary>
    /// <remarks>
    /// Internal to the conversion and removed before anything is written, so it never appears in a
    /// generated document. It exists because the fields belong one level above the schema that
    /// declares them, and only the object that owns that schema can put them there.
    /// </remarks>
    private const string OneOfUnionMarker = "x-oneof-union";

    /// <summary>Keywords dropped: they describe the document, not the type.</summary>
    private static readonly HashSet<string> Dropped = new(StringComparer.Ordinal)
    {
        "$schema", "restdocs",
    };

    /// <summary>
    /// Misspelled or inconsistently cased keywords in e-conomic's published files, mapped to what
    /// they were meant to be. <c>desciption</c> and <c>readonly</c> are typos that appear 8 and 86
    /// times respectively; correcting them here avoids losing the content.
    /// </summary>
    private static readonly Dictionary<string, string> Renamed = new(StringComparer.Ordinal)
    {
        ["readonly"] = "readOnly",
        ["desciption"] = "description",
        ["defaultSorting"] = "x-default-sorting",
        ["defaultsorting"] = "x-default-sorting",
        ["maxDecimal"] = "x-max-decimals",
        ["maxDecimals"] = "x-max-decimals",
    };

    /// <summary>
    /// Titles e-conomic got wrong, keyed by the file they appear in, then by the wrong title.
    /// </summary>
    /// <remarks>
    /// Scoped per file on purpose: "Customer" is a perfectly good title everywhere else. Correcting
    /// it here rather than renaming the generated type downstream means the shape deduplicates
    /// against the correctly titled copy of the same entity instead of becoming a second type.
    /// </remarks>
    public static readonly Dictionary<string, Dictionary<string, string>> TitleCorrections =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // The delivery-location collection titles its items "Customer"; they are delivery
            // locations, identical in shape to the single delivery-location GET.
            ["customers.customerNumber.delivery-locations.get.schema.json"] =
                new(StringComparer.Ordinal) { ["Customer"] = "Delivery location" },
        };

    /// <summary>
    /// Properties e-conomic declares as a string and then answers with a number, keyed by
    /// <c>{file}:{property}</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same class of defect as the mislabelled dates, and found the same way: only the server
    /// settles it. <c>Entry.invoiceNumber</c> is declared <c>"type": "string"</c> and the response
    /// carries <c>"invoiceNumber":1</c>, so <c>System.Text.Json</c> refuses the token and every page
    /// of <c>/accounting-years/{y}/entries</c> containing an invoice fails to deserialize.
    /// </para>
    /// <para>
    /// Unusually, e-conomic's own files corroborate the correction rather than only contradicting
    /// the server: four schemas describe this same entry, and
    /// <c>accounts.accountNumber.accounting-years.accountingYear.entries.get.schema.json</c> already
    /// declares <c>invoiceNumber</c> as <c>integer</c>. The three listed here are the ones that
    /// disagree with it, so this is closer to reconciling the specification with itself than to
    /// overriding it. Correcting them collapsed <c>AccountsEntry</c> and
    /// <c>AccountsEntriesCollection</c> into their accounting-year twins — 201 components to 199 —
    /// because the mistyped property was the only thing that made the shapes differ.
    /// </para>
    /// <para>
    /// Scoped per file, and listing only what a live agreement actually demonstrated. Note
    /// <c>supplierInvoiceNumber</c> sits beside it in these same schemas, declared a string in five
    /// entry shapes, and is deliberately <em>not</em> here: no response yet seen carries one, so
    /// there is no evidence either way and guessing would be inventing a specification rather than
    /// correcting one.
    /// </para>
    /// <para>
    /// An entry matching nothing fails the run, so a corrected specification cannot leave a stale
    /// override behind.
    /// </para>
    /// </remarks>
    public static IReadOnlySet<string> NumericStrings { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // GET /accounting-years/{accountingYear}/entries -> Entry
        "accounting-years.accountingYear.entries.get.schema.json:invoiceNumber",

        // GET /accounting-years/{accountingYear}/periods/{p}/entries and the same collection reached
        // through an account. Both files must be listed even though they dedup to one component:
        // identity here is structural, so correcting one and not the other splits the entity into
        // two near-identical types and reshuffles the names they were holding.
        "accounting-years.accountingYear.periods.accountingYearPeriod.entries.get.schema.json:invoiceNumber",
        "accounts.accountNumber.accounting-years.accountingYear.periods.accountingYearPeriod.entries.get.schema.json:invoiceNumber",
    };

    /// <summary>Retypes a property the server answers with a number, in place.</summary>
    /// <param name="source">The parsed source schema.</param>
    /// <param name="fileName">Name of the file it came from.</param>
    /// <param name="applied">Collects the <see cref="NumericStrings"/> entries used, for the rot guard.</param>
    /// <returns>The number of properties retyped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public static int CorrectNumericStrings(
        JsonObject source,
        string fileName,
        ICollection<string>? applied = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var wanted = NumericStrings
            .Where(e => e.StartsWith(fileName + ":", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(e => e[(fileName.Length + 1)..], StringComparer.Ordinal);

        if (wanted.Count == 0)
        {
            return 0;
        }

        var corrected = 0;
        Walk(source);
        return corrected;

        // The property sits inside the collection's item, so this walks rather than indexing: the
        // draft-03 nesting to reach it differs between a collection and a single-resource GET, and
        // hard-coding one path would silently skip the other.
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (obj["properties"] is JsonObject properties)
                    {
                        foreach (var (name, entry) in wanted)
                        {
                            if (properties[name] is JsonObject declared
                                && declared["type"]?.GetValue<string>() == "string")
                            {
                                declared["type"] = "integer";
                                applied?.Add(entry);
                                corrected++;
                            }
                        }
                    }

                    foreach (var (_, child) in obj)
                    {
                        Walk(child);
                    }

                    break;

                case JsonArray array:
                    foreach (var item in array)
                    {
                        Walk(item);
                    }

                    break;
            }
        }
    }

    /// <summary>Applies any known title corrections for a source file, in place.</summary>
    /// <param name="source">The parsed source schema.</param>
    /// <param name="fileName">Name of the file it came from.</param>
    /// <returns>The number of titles corrected.</returns>
    public static int CorrectTitles(JsonObject source, string fileName)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!TitleCorrections.TryGetValue(fileName, out var corrections))
        {
            return 0;
        }

        var corrected = 0;
        Walk(source);
        return corrected;

        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (obj["title"] is JsonValue value
                        && value.TryGetValue<string>(out var title)
                        && corrections.TryGetValue(title, out var replacement))
                    {
                        obj["title"] = replacement;
                        corrected++;
                    }

                    foreach (var (_, child) in obj)
                    {
                        Walk(child);
                    }

                    break;

                case JsonArray array:
                    foreach (var item in array)
                    {
                        Walk(item);
                    }

                    break;
            }
        }
    }

    /// <summary>Converts a draft-03 schema node into an OpenAPI 3.0 Schema Object.</summary>
    /// <param name="source">The draft-03 schema.</param>
    /// <param name="unhandled">Collects any keyword the converter does not understand.</param>
    /// <returns>The converted schema.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public static JsonObject Convert(JsonObject source, ISet<string>? unhandled = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var target = new JsonObject();
        var required = new JsonArray();
        var misplaced = new JsonObject();

        // Collected during the walk and merged after it: `oneOf` sits beside `properties` in these
        // files and comes first, so merging in place would be overwritten by the property pass.
        JsonObject? branches = null;

        foreach (var (key, value) in source)
        {
            if (Renamed.TryGetValue(key, out var corrected))
            {
                // Never let a typo overwrite the correctly spelled key when a file has both.
                target[corrected] ??= value?.DeepClone();
                continue;
            }

            switch (key)
            {
                case "properties" when value is JsonObject properties:
                    target["properties"] = ConvertProperties(properties, required, unhandled);
                    break;

                case "items" when value is JsonObject items:
                    target["items"] = Convert(items, unhandled);
                    break;

                case "oneOf" when value is JsonArray alternatives:
                    // Flattened into the union of its branches rather than carried across, and the
                    // reason is what NSwag does with it: given six alternatives it emits a class
                    // with the first branch's two properties and silently drops the other five, so
                    // five of e-conomic's six payment types become unrepresentable. Every oneOf in
                    // these schemas is the same shape — a payment type, whose branches are pairs of
                    // properties chosen by the payment type number — so the union is exactly the
                    // set of fields a caller might set, and the server validates the combination.
                    //
                    // Merged as optional, because required-ness here belongs to a branch and not to
                    // the type: `ocrLine` is required for a +71 payment and meaningless for an IBAN
                    // one. Marking any of them required would make five of the six impossible to
                    // express, which is the defect this exists to remove.
                    foreach (var alternative in alternatives.OfType<JsonObject>())
                    {
                        if (Convert(alternative, unhandled)["properties"] is not JsonObject branch)
                        {
                            continue;
                        }

                        foreach (var (name, definition) in branch)
                        {
                            // First branch wins: where two describe the same field they agree on
                            // its type, and differ only in the length limits one payment type
                            // imposes. Keeping the first is arbitrary but stable, and the server is
                            // the authority on the limits either way.
                            branches ??= [];
                            if (!branches.ContainsKey(name))
                            {
                                branches[name] = definition?.DeepClone();
                            }
                        }
                    }

                    break;

                case "format" when value is JsonValue format:
                    // draft-03 spells the date format "full-date". e-conomic labels 18 properties
                    // that way whose own `pattern` is a full ISO-8601 timestamp — lastUpdated is
                    // one, and it really does come back as 2022-06-02T08:53:29Z. The pattern is the
                    // authority; trusting the label alone yields a DateOnly that cannot parse the
                    // value the server sends.
                    var formatName = format.GetValue<string>();
                    if (formatName == "full-date")
                    {
                        var pattern = source["pattern"]?.GetValue<string>();
                        formatName = pattern?.Contains('T', StringComparison.Ordinal) == true
                            ? "date-time"
                            : "date";
                    }

                    target["format"] = formatName;
                    break;

                case "filterable" when value is JsonValue filterable:
                    target["x-filterable"] = filterable.GetValue<bool>();
                    break;

                case "sortable" when value is JsonValue sortable:
                    target["x-sortable"] = sortable.GetValue<bool>();
                    break;

                // Handled by the parent, which collects it into its own `required` array.
                case "required":
                    break;

                default:
                    if (PassThrough.Contains(key))
                    {
                        target[key] = value?.DeepClone();
                    }
                    else if (Dropped.Contains(key))
                    {
                        // Intentionally discarded.
                    }
                    else if (LooksLikeSchema(value))
                    {
                        // Some published files place a property beside `type` instead of inside
                        // `properties` - `self` on priceGroup, `pagination` and `metaData` on a
                        // delivery-locations collection. The intent is unambiguous, so put it where
                        // it belongs rather than dropping a documented field.
                        misplaced[key] = value!.DeepClone();
                    }
                    else
                    {
                        unhandled?.Add(key);
                        target[key] = value?.DeepClone();
                    }

                    break;
            }
        }

        if (misplaced.Count > 0)
        {
            var properties = target["properties"]?.AsObject();
            if (properties is null)
            {
                properties = [];
                target["properties"] = properties;
            }

            foreach (var (name, schema) in misplaced)
            {
                properties[name] ??= Convert(schema!.AsObject(), unhandled);
            }
        }

        // Handed to the containing object rather than merged here, under a marker the property pass
        // lifts and removes. The server is what decides that: e-conomic puts the `oneOf` on
        // `paymentDetails.paymentType`, and posting the pair there answers 400 "The folowing fields
        // need to be either all set or all not set", while posting it one level up on
        // `paymentDetails` answers 201 and reads back with the fields at that level. The schema
        // nests them one deeper than the API does.
        if (branches is { Count: > 0 })
        {
            target[OneOfUnionMarker] = branches;
        }

        if (required.Count > 0)
        {
            target["required"] = required;
        }

        return target;
    }

    /// <summary>A value is schema-shaped when it is an object declaring a <c>type</c>.</summary>
    private static bool LooksLikeSchema(JsonNode? value) =>
        value is JsonObject candidate
        && candidate.ContainsKey("type")
        && candidate["type"] is JsonValue;

    private static JsonObject ConvertProperties(JsonObject properties, JsonArray required, ISet<string>? unhandled)
    {
        var converted = new JsonObject();

        foreach (var (name, schema) in properties)
        {
            if (schema is not JsonObject property)
            {
                continue;
            }

            // draft-03 marks requiredness on the property; draft-04+ lists it on the parent.
            if (property.TryGetPropertyValue("required", out var isRequired)
                && isRequired is JsonValue value
                && value.TryGetValue<bool>(out var flag)
                && flag)
            {
                required.Add(name);
            }

            var result = Convert(property, unhandled);

            // A `oneOf` on this property describes fields the API carries on *this* object, not on
            // the property — verified against the server, which rejects them nested and accepts
            // them here. Lift them out and drop the marker so it never reaches the document.
            if (result[OneOfUnionMarker] is JsonObject union)
            {
                result.Remove(OneOfUnionMarker);

                foreach (var (field, definition) in union)
                {
                    converted[field] ??= definition?.DeepClone();
                }
            }

            // Every resource carries a `self` link and all but one label it `format: "uri"`.
            // Supplier omits it, which would otherwise surface `Supplier.Self` as a string while
            // every other entity exposes a Uri — an inconsistency in the public API caused purely
            // by a missing keyword in one file.
            if (name == "self"
                && result["type"]?.GetValue<string>() == "string"
                && result["format"] is null)
            {
                result["format"] = "uri";
            }

            converted[name] = result;
        }

        return converted;
    }
}
