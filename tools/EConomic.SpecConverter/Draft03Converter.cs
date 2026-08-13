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
                    var converted = new JsonArray();
                    foreach (var alternative in alternatives)
                    {
                        converted.Add(alternative is JsonObject option
                            ? Convert(option, unhandled)
                            : alternative?.DeepClone());
                    }

                    target["oneOf"] = converted;
                    break;

                case "format" when value is JsonValue format:
                    // draft-03 spells the date format "full-date".
                    var formatName = format.GetValue<string>();
                    target["format"] = formatName == "full-date" ? "date" : formatName;
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

            converted[name] = Convert(property, unhandled);
        }

        return converted;
    }
}
