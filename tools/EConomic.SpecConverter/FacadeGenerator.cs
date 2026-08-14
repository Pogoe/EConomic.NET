using System.Text;
using System.Text.Json.Nodes;

namespace EConomic.SpecConverter;

/// <summary>A top-level collection endpoint that can be exposed as a query.</summary>
/// <param name="Path">The URL path, e.g. <c>/customers</c>.</param>
/// <param name="Entity">The component name of the collection's item type.</param>
/// <param name="ClientClass">The generated NSwag client class.</param>
/// <param name="Method">The generated method that fetches the collection.</param>
/// <param name="PropertyName">The property to expose on the client, e.g. <c>Customers</c>.</param>
/// <param name="PublicName">The name the entity is published under, usually <paramref name="Entity"/>.</param>
public sealed record FacadeResource(
    string Path,
    string Entity,
    string ClientClass,
    string Method,
    string PropertyName,
    string PublicName);

/// <summary>A property carried across from a generated entity to its public model.</summary>
/// <param name="Name">The C# property name.</param>
/// <param name="PublicType">Type on the public model.</param>
/// <param name="Mapping">Expression mapping from <c>source</c>.</param>
/// <remarks>
/// <paramref name="Mapping"/> may span several lines. It is laid out for an assignment indented by
/// eight spaces, which is where every caller emits it.
/// </remarks>
public sealed record FacadeProperty(string Name, string PublicType, string Mapping);

/// <summary>A public record generated for a composite property, such as an invoice's recipient.</summary>
/// <param name="Name">The C# type name, e.g. <c>DraftInvoiceSummaryRecipient</c>.</param>
/// <param name="Owner">The type the property belongs to, for the documentation comment.</param>
/// <param name="Property">The property's name in the specification.</param>
/// <param name="Properties">Its own properties, mapped the same way as an entity's.</param>
/// <param name="IsElement">Whether the record is one element of an array rather than the whole property.</param>
/// <remarks>
/// Named by prefixing the owner rather than by sharing one type per property name. The same name
/// covers genuinely different shapes across resources — <c>recipient</c> has six of them and
/// <c>lines</c> five — so a shared type would have to be a curated compromise, and a shape change on
/// one resource would silently move another's public surface.
/// </remarks>
public sealed record FacadeNestedType(
    string Name,
    string Owner,
    string Property,
    IReadOnlyList<FacadeProperty> Properties,
    bool IsElement = false);

/// <summary>A property a caller may set when creating or updating a resource.</summary>
/// <param name="Name">The C# property name on the public write model.</param>
/// <param name="PublicType">Type on the public write model.</param>
/// <param name="GeneratedName">The property on the generated payload type.</param>
/// <param name="ReferenceNumber">When the payload nests a reference, the number property inside it.</param>
/// <param name="IsRequired">Whether e-conomic requires it.</param>
/// <param name="IsEnum">Whether the generated property is an enum the public model exposes as text.</param>
/// <param name="Nested">The properties of a composite value: an object, or one element of an array.</param>
/// <param name="Kind">How the value is carried.</param>
public sealed record WriteProperty(
    string Name,
    string PublicType,
    string GeneratedName,
    string? ReferenceNumber,
    bool IsRequired,
    bool IsEnum = false,
    IReadOnlyList<WriteProperty>? Nested = null,
    WriteKind Kind = WriteKind.Scalar);

/// <summary>How a settable property's value is carried onto the generated payload.</summary>
public enum WriteKind
{
    /// <summary>A single value, or a reference flattened to its number.</summary>
    Scalar,

    /// <summary>A composite value with its own public record.</summary>
    Composite,

    /// <summary>An array whose elements have their own public record.</summary>
    CompositeList,

    /// <summary>An array of enum values, carried as strings.</summary>
    EnumList,
}

/// <summary>A public record generated for a composite property of a write model.</summary>
/// <param name="Name">The C# type name, e.g. <c>DraftInvoiceCreateLine</c>.</param>
/// <param name="Owner">The model the property belongs to.</param>
/// <param name="Property">The property's name in the specification.</param>
/// <param name="Properties">Its own settable properties.</param>
/// <param name="IsElement">Whether the record is one element of an array.</param>
public sealed record FacadeNestedWriteType(
    string Name,
    string Owner,
    string Property,
    IReadOnlyList<WriteProperty> Properties,
    bool IsElement = false);

/// <summary>The write operations a resource supports.</summary>
/// <param name="CreateBody">Generated payload type for <c>POST</c>.</param>
/// <param name="CreateMethod">Generated method for <c>POST</c>.</param>
/// <param name="UpdateBody">Generated payload type for <c>PUT</c>, when there is one.</param>
/// <param name="UpdateMethod">Generated method for <c>PUT</c>.</param>
/// <param name="KeyName">C# parameter name for the resource's identifier.</param>
/// <param name="KeyType">C# type of that identifier.</param>
/// <param name="KeyProperty">The identifier's property name on the payload types.</param>
/// <param name="SupportsDelete">Whether the documentation describes a delete.</param>
public sealed record FacadeWrite(
    string? CreateBody,
    string? CreateMethod,
    string? UpdateBody,
    string? UpdateMethod,
    string KeyName,
    string KeyType,
    string KeyProperty,
    bool SupportsDelete);

/// <summary>A collection that hangs off another resource, such as a customer's contacts.</summary>
/// <param name="Path">The URL path, e.g. <c>/customers/{customerNumber}/contacts</c>.</param>
/// <param name="ParentEntity">The owning entity, e.g. <c>Customer</c>.</param>
/// <param name="ParentKeyName">C# parameter name for the parent's identifier.</param>
/// <param name="ParentKeyType">C# type of the parent's identifier.</param>
/// <param name="ParentCollection">The parent's collection segment, e.g. <c>customers</c>.</param>
/// <param name="Collection">This collection's segment, e.g. <c>contacts</c>.</param>
/// <param name="Entity">The component name of the item type.</param>
/// <param name="ClientClass">The generated NSwag client class.</param>
/// <param name="ListMethod">The generated method that fetches the collection.</param>
/// <param name="AccessorName">The method exposed on the parent resource, e.g. <c>Contacts</c>.</param>
/// <param name="Write">The write operations, when the collection accepts any.</param>
/// <param name="PublicName">The name the entity is published under, usually <paramref name="Entity"/>.</param>
public sealed record FacadeNested(
    string Path,
    string ParentEntity,
    string ParentKeyName,
    string ParentKeyType,
    string ParentCollection,
    string Collection,
    string Entity,
    string ClientClass,
    string ListMethod,
    string AccessorName,
    FacadeWrite? Write,
    string PublicName);

/// <summary>
/// Generates the public models, transports and client properties for whole resources.
/// </summary>
/// <remarks>
/// Written after the Customers facade was built by hand: one resource is a reasonable thing to
/// hand-write, twenty-three is a reliable way to introduce typos into mapping code that no test
/// would catch. Anything this generator cannot express is reported rather than skipped silently,
/// so the gap is visible instead of appearing as a property that is always null.
/// </remarks>
public static class FacadeGenerator
{
    private const string RefPrefix = "#/components/schemas/";

    /// <summary>
    /// How deep composite properties are followed. The deepest real nesting in the specifications is
    /// three (<c>product.productGroup.accrual.accountsSummed</c>); the cap is a guard against a
    /// self-referential schema rather than a limit anything currently reaches.
    /// </summary>
    private const int MaxNestingDepth = 5;

    /// <summary>Finds the collection endpoints that can be exposed without path parameters.</summary>
    /// <param name="document">The merged OpenAPI document.</param>
    /// <returns>The resources, in path order.</returns>
    public static IReadOnlyList<FacadeResource> Resources(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var schemas = document["components"]!["schemas"]!.AsObject();
        var resources = new List<FacadeResource>();

        foreach (var (path, item) in document["paths"]!.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            // A collection that needs an identifier cannot hang off the client as a bare property;
            // those are exposed through their parent instead. Two static segments are fine, though
            // — /invoices/drafts is a collection in its own right, not a nested one, and the
            // invoices, orders and quotes families are all shaped that way.
            var segments = path.Trim('/').Split('/');
            if (path.Contains('{', StringComparison.Ordinal) || segments.Length is < 1 or > 2)
            {
                continue;
            }

            if (item?["get"] is not JsonObject operation)
            {
                continue;
            }

            var envelope = Reference(operation["responses"]?["200"]?["content"]?["application/json"]?["schema"]);
            if (envelope is null || schemas[envelope] is not JsonObject envelopeSchema)
            {
                continue;
            }

            var entity = Reference(envelopeSchema["properties"]?["collection"]?["items"]);
            if (entity is null)
            {
                continue;
            }

            var tag = operation["tags"]?[0]?.GetValue<string>() ?? path.Trim('/');
            var operationId = operation["operationId"]!.GetValue<string>();

            resources.Add(new FacadeResource(
                path,
                entity,
                $"{SchemaRegistry.Identifier(tag)}Client",
                $"{char.ToUpperInvariant(operationId[0])}{operationId[1..]}Async",
                PropertyNameFor(segments),
                SchemaRegistry.PublicName(entity)));
        }

        return resources;
    }

    /// <summary>Generates the facade for the published resources.</summary>
    /// <param name="document">The merged OpenAPI document.</param>
    /// <param name="namespaceName">Namespace for the public models.</param>
    /// <param name="skipped">Collects properties that could not be mapped.</param>
    /// <returns>The C# source.</returns>
    public static string Generate(JsonObject document, string namespaceName, IList<string> skipped)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(skipped);

        var schemas = document["components"]!["schemas"]!.AsObject();
        var resources = Resources(document)
            .Where(r => FilterSurfaceGenerator.PublishedEntities.Count == 0
                || FilterSurfaceGenerator.PublishedEntities.Contains(r.Entity))
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated>");
        builder.AppendLine("//     Generated by tools/EConomic.SpecConverter (facade).");
        builder.AppendLine("//     Regenerate after a spec refresh; do not edit by hand.");
        builder.AppendLine("// </auto-generated>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine("using EConomic.Pagination;");
        builder.AppendLine("using EConomic.Querying;");
        builder.AppendLine("using Generated = EConomic.Rest.Generated;");
        builder.AppendLine();
        builder.AppendLine($"namespace {namespaceName}");
        builder.AppendLine("{");

        var nested = NestedResources(document, resources);

        // A resource type is emitted for anything that offers more than a query — writes, nested
        // collections, or both. Journals are the case that forced the distinction: they are
        // read-only, but their vouchers hang off them and need something to hang from.
        var withResourceType = new HashSet<string>(StringComparer.Ordinal);

        foreach (var resource in resources)
        {
            var nestedTypes = new List<FacadeNestedType>();
            var properties = MapProperties(
                schemas[resource.Entity]!.AsObject(), resource.PublicName, resource.Entity,
                schemas, skipped, nestedTypes);

            AppendNestedTypes(builder, nestedTypes);
            AppendModel(builder, resource, properties);
            AppendPageSource(builder, resource, properties);

            var write = SchemaRegistry.WriteEnabledEntities.Contains(resource.Entity)
                ? WriteFor(document, resource)
                : null;

            var children = nested.Where(n => n.ParentEntity == resource.Entity).ToList();

            if (write is not null || children.Count > 0)
            {
                withResourceType.Add(resource.Entity);
                AppendResource(builder, resource, write, properties, schemas, children, skipped);
            }
        }

        foreach (var child in nested)
        {
            AppendNested(builder, child, schemas, skipped);
        }

        builder.AppendLine("}");
        AppendClientProperties(builder, resources, withResourceType);
        return builder.ToString();
    }

    private static List<FacadeProperty> MapProperties(
        JsonObject entity,
        string typeName,
        string componentName,
        JsonObject schemas,
        IList<string> skipped,
        List<FacadeNestedType> nested) =>
        MapObject(entity, typeName, componentName, "source", schemas, skipped, nested, depth: 0);

    /// <summary>Maps one object schema, generating a public record for each composite property.</summary>
    /// <param name="schema">The schema to map.</param>
    /// <param name="typeName">The public type being built, which also prefixes its nested types.</param>
    /// <param name="reportName">Dotted path used when reporting a property that cannot be mapped.</param>
    /// <param name="accessor">The generated expression this object is read from, e.g. <c>source.Recipient</c>.</param>
    /// <param name="schemas">Every registered component, for resolving <c>$ref</c>s.</param>
    /// <param name="skipped">Collects what could not be mapped.</param>
    /// <param name="nested">Collects the records to emit.</param>
    /// <param name="depth">Nesting depth, which sets the indentation of the generated initializer.</param>
    private static List<FacadeProperty> MapObject(
        JsonObject schema,
        string typeName,
        string reportName,
        string accessor,
        JsonObject schemas,
        IList<string> skipped,
        List<FacadeNestedType> nested,
        int depth)
    {
        var mapped = new List<FacadeProperty>();

        // The schema says which properties are always present. Honouring that keeps the key and
        // name non-nullable instead of making every consumer null-check what cannot be null.
        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in schema["required"]?.AsArray() ?? [])
        {
            if (name?.GetValue<string>() is { } value)
            {
                required.Add(value);
            }
        }

        foreach (var (name, node) in schema["properties"]?.AsObject() ?? [])
        {
            if (node is not JsonObject property)
            {
                continue;
            }

            var propertyName = Pascal(name);
            var member = $"{accessor}.{propertyName}";
            var resolved = Resolve(property, schemas);

            if (Composition(resolved) is { } composition)
            {
                skipped.Add($"{reportName}.{name} ({composition}, no single shape)");
                continue;
            }

            var type = resolved["type"]?.GetValue<string>();
            var format = resolved["format"]?.GetValue<string>();

            // A constrained string becomes a generated enum, which is internal and so cannot appear
            // on a public model. The value is carried across as its name: adding the enum to the
            // public surface would tie it to the spec, and a new member would then be a breaking
            // change rather than just a new string.
            if (resolved["enum"] is JsonArray)
            {
                mapped.Add(new FacadeProperty(propertyName, "string?", $"{member}.ToString()"));
                continue;
            }

            var scalar = ScalarType(type, format);

            if (scalar is not null)
            {
                // Numbers and booleans are non-nullable in the generated layer regardless, so a
                // nullable public property would promise an absence it can never report. Dates are
                // the exception: a default timestamp is meaningless, so null carries real
                // information there.
                var alwaysPresent = scalar is "int?" or "decimal?" or "bool";
                var isRequired = alwaysPresent || required.Contains(name);

                mapped.Add(new FacadeProperty(
                    propertyName,
                    isRequired ? Required(scalar) : scalar,
                    ScalarMapping(scalar, member, isRequired)));

                continue;
            }

            if (ReferenceNumber(resolved) is { } numberProperty)
            {
                mapped.Add(new FacadeProperty(
                    propertyName,
                    "EconomicReference?",
                    $"Reference({member}?.{Pascal(numberProperty)}, {member}?.Self)"));

                continue;
            }

            if (type == "array" && resolved["items"] is JsonObject itemNode)
            {
                if (MapArray(
                        Resolve(itemNode, schemas), typeName, name, $"{reportName}.{name}",
                        member, schemas, skipped, nested, depth) is { } array)
                {
                    mapped.Add(new FacadeProperty(propertyName, array.Type, array.Mapping));
                    continue;
                }

                skipped.Add($"{reportName}.{name} (array)");
                continue;
            }

            if (type == "object" && depth < MaxNestingDepth)
            {
                var nestedName = typeName + propertyName;
                var properties = MapObject(
                    resolved, nestedName, $"{reportName}.{name}", member, schemas, skipped, nested, depth + 1);

                // Two of e-conomic's schemas declare a property as an object with no properties at
                // all — `metaData` and `pagination` on a journal, both envelope fields that ended up
                // on the item. An empty public record would be API nobody can use.
                if (properties.Count == 0)
                {
                    skipped.Add($"{reportName}.{name} (object, no properties)");
                    continue;
                }

                nested.Add(new FacadeNestedType(nestedName, typeName, name, properties));
                mapped.Add(new FacadeProperty(
                    propertyName,
                    $"{nestedName}?",
                    $"{member} is null ? null : {Initializer(nestedName, properties, Indent(depth))}"));

                continue;
            }

            skipped.Add($"{reportName}.{name} ({type ?? "object"})");
        }

        return mapped;
    }

    /// <summary>Maps an array property, whose items may be scalars, references or objects.</summary>
    /// <returns>The public type and mapping, or <see langword="null"/> when the items cannot be mapped.</returns>
    private static (string Type, string Mapping)? MapArray(
        JsonObject item,
        string ownerTypeName,
        string propertyName,
        string reportName,
        string member,
        JsonObject schemas,
        IList<string> skipped,
        List<FacadeNestedType> nested,
        int depth)
    {
        // An absent array maps to an empty list rather than to null: "no lines" and "the server did
        // not say" are the same thing here, and a nullable collection makes every caller guard.
        if (item["enum"] is JsonArray)
        {
            return ("IReadOnlyList<string>", $"FacadeTransport.MapList({member}, value => value.ToString())");
        }

        var itemType = item["type"]?.GetValue<string>();

        if (ScalarType(itemType, item["format"]?.GetValue<string>()) is { } scalar)
        {
            var element = Required(scalar);
            var projection = element == "decimal" ? "value => (decimal)value" : "value => value";
            return ($"IReadOnlyList<{element}>", $"FacadeTransport.MapList({member}, {projection})");
        }

        if (ReferenceNumber(item) is { } numberProperty)
        {
            return (
                "IReadOnlyList<EconomicReference>",
                $"FacadeTransport.MapList({member}, value => new EconomicReference(value.{Pascal(numberProperty)}, value.Self))");
        }

        if (itemType != "object" || depth >= MaxNestingDepth)
        {
            return null;
        }

        var elementName = Singular(ownerTypeName + Pascal(propertyName));
        var variable = $"item{depth}";
        var properties = MapObject(
            item, elementName, reportName, variable, schemas, skipped, nested, depth + 1);

        if (properties.Count == 0)
        {
            return null;
        }

        nested.Add(new FacadeNestedType(elementName, ownerTypeName, propertyName, properties, IsElement: true));

        return (
            $"IReadOnlyList<{elementName}>",
            $"FacadeTransport.MapList({member}, {variable} => {Initializer(elementName, properties, Indent(depth))})");
    }

    /// <summary>
    /// The composition keyword a schema uses, when it has one.
    /// </summary>
    /// <remarks>
    /// A property described by <c>oneOf</c> has no single shape, and NSwag picks one branch to
    /// generate from. Mapping the property's own <c>properties</c> instead would produce code
    /// referring to members the generated type does not have — which is exactly what
    /// <c>paymentDetails.paymentType</c> did: its six alternatives cover different payment forms,
    /// and NSwag emitted the first. Reporting it is the honest answer; guessing compiled to nothing.
    /// </remarks>
    private static string? Composition(JsonObject schema) =>
        schema["oneOf"] is not null ? "oneOf"
        : schema["anyOf"] is not null ? "anyOf"
        : schema["allOf"] is not null ? "allOf"
        : null;

    private static string? ScalarType(string? type, string? format) => (type, format) switch
    {
        ("string", "uri") => "System.Uri?",
        ("string", "date") => "DateOnly?",
        ("string", "date-time") => "DateTimeOffset?",
        ("string", _) => "string?",
        ("integer", _) => "int?",
        ("number", _) => "decimal?",
        ("boolean", _) => "bool",
        _ => null,
    };

    /// <summary>The expression reading one scalar out of the generated layer.</summary>
    /// <remarks>
    /// A JSON number is a <c>double</c> in the generated layer because the schema says only
    /// "number"; money belongs in <c>decimal</c> on the public model.
    /// </remarks>
    private static string ScalarMapping(string scalar, string member, bool isRequired) => scalar switch
    {
        "decimal?" when isRequired => $"(decimal){member}",
        "decimal?" => $"(decimal?){member}",
        "string?" when isRequired => $"{member} ?? string.Empty",
        "DateTimeOffset?" or "DateOnly?" when !isRequired => $"{member} == default ? null : {member}",
        _ => member,
    };

    /// <summary>Renders an object initializer, laid out for an assignment at the given indent.</summary>
    private static string Initializer(string typeName, IReadOnlyList<FacadeProperty> properties, int indent)
    {
        var pad = new string(' ', indent);
        var inner = new string(' ', indent + 4);

        var builder = new StringBuilder();
        builder.AppendLine($"new {typeName}");
        builder.AppendLine($"{pad}{{");

        foreach (var property in properties)
        {
            builder.AppendLine($"{inner}{property.Name} = {property.Mapping},");
        }

        builder.Append($"{pad}}}");
        return builder.ToString();
    }

    /// <summary>Where a nested initializer's braces sit. Every caller emits properties at eight.</summary>
    private static int Indent(int depth) => 8 + (depth * 4);

    /// <summary>
    /// The element type name for an array property. A name that is already plural loses its "s";
    /// anything else — <c>accountsSummed</c> — gets a suffix instead, since there is no singular
    /// form to take.
    /// </summary>
    private static string Singular(string name) =>
        name.EndsWith('s') && name.Length > 1 ? name[..^1] : $"{name}Item";

    /// <summary>The number property of an embedded reference, when the shape is one.</summary>
    private static string? ReferenceNumber(JsonObject schema)
    {
        if (schema["type"]?.GetValue<string>() != "object" || schema["properties"] is not JsonObject properties)
        {
            return null;
        }

        if (!properties.ContainsKey("self") || properties.Count > 3)
        {
            return null;
        }

        return properties
            .FirstOrDefault(p => p.Key.EndsWith("Number", StringComparison.Ordinal)
                && p.Value?["type"]?.GetValue<string>() == "integer")
            .Key;
    }

    private static void AppendModel(StringBuilder builder, FacadeResource resource, IReadOnlyList<FacadeProperty> properties)
    {
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine($"/// A resource from <c>{resource.Path}</c>.");
        builder.AppendLine("/// </summary>");
        AppendRecord(builder, resource.PublicName, properties);
    }

    /// <summary>Emits the public records for an entity's composite properties.</summary>
    /// <remarks>
    /// Deepest first, so a record is declared before the one that mentions it. Ordering is
    /// irrelevant to the compiler; it just makes the generated file readable.
    /// </remarks>
    private static void AppendNestedTypes(StringBuilder builder, IReadOnlyList<FacadeNestedType> nestedTypes)
    {
        foreach (var nested in nestedTypes)
        {
            builder.AppendLine();
            builder.AppendLine("/// <summary>");
            builder.AppendLine(nested.IsElement
                ? $"/// One entry in the <c>{nested.Property}</c> of a <see cref=\"{nested.Owner}\"/>."
                : $"/// The <c>{nested.Property}</c> of a <see cref=\"{nested.Owner}\"/>.");
            builder.AppendLine("/// </summary>");
            AppendRecord(builder, nested.Name, nested.Properties);
        }
    }

    private static void AppendRecord(StringBuilder builder, string typeName, IReadOnlyList<FacadeProperty> properties)
    {
        builder.AppendLine($"public sealed record {typeName}");
        builder.AppendLine("{");

        foreach (var property in properties)
        {
            // A non-nullable reference type has no sensible default, so the compiler needs to know
            // the mapper always supplies it. A collection does have one — an absent array and an
            // empty array mean the same thing — so it is initialized rather than made required.
            var isList = property.PublicType.StartsWith("IReadOnlyList<", StringComparison.Ordinal);
            var modifier = !isList
                && !property.PublicType.EndsWith('?')
                && property.PublicType is "string" or "System.Uri" or "EconomicReference"
                    ? "required "
                    : string.Empty;

            var initializer = isList ? " = [];" : string.Empty;

            builder.AppendLine($"    /// <summary>The <c>{Camel(property.Name)}</c> field.</summary>");
            builder.AppendLine($"    public {modifier}{property.PublicType} {property.Name} {{ get; init; }}{initializer}");
            builder.AppendLine();
        }

        builder.Length -= Environment.NewLine.Length;
        builder.AppendLine("}");
    }

    private static void AppendPageSource(StringBuilder builder, FacadeResource resource, IReadOnlyList<FacadeProperty> properties)
    {
        var name = resource.PublicName;

        builder.AppendLine();
        builder.AppendLine($"/// <summary>Fetches pages of <c>{resource.Path}</c> and maps them to <see cref=\"{name}\"/>.</summary>");
        builder.AppendLine($"internal sealed class {name}PageSource(Generated.{resource.ClientClass} client)");
        builder.AppendLine($"    : IEconomicPageSource<{name}>");
        builder.AppendLine("{");
        builder.AppendLine($"    public async Task<EconomicPage<{name}>> GetPageAsync(");
        builder.AppendLine("        EconomicPageRequest request,");
        builder.AppendLine("        CancellationToken cancellationToken)");
        builder.AppendLine("    {");
        builder.AppendLine("        var response = await FacadeTransport.SendAsync(");
        builder.AppendLine($"            () => client.{resource.Method}(");
        builder.AppendLine("                request.Filter, request.Sort, request.PageIndex, request.PageSize, cancellationToken),");
        builder.AppendLine($"            \"GET {resource.Path}\").ConfigureAwait(false);");
        builder.AppendLine();
        builder.AppendLine($"        var items = new List<{name}>(response.Collection?.Count ?? 0);");
        builder.AppendLine("        foreach (var item in response.Collection ?? [])");
        builder.AppendLine("        {");
        builder.AppendLine("            items.Add(Map(item));");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine($"        return new EconomicPage<{name}>(items, request.PageIndex, request.PageSize);");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine($"    internal static {name} Map(Generated.{resource.Entity} source) => new()");
        builder.AppendLine("    {");

        foreach (var property in properties)
        {
            builder.AppendLine($"        {property.Name} = {property.Mapping},");
        }

        builder.AppendLine("    };");
        builder.AppendLine();
        builder.AppendLine("    private static EconomicReference? Reference(int? number, System.Uri? self) =>");
        builder.AppendLine("        number is null ? null : new EconomicReference(number.Value, self);");
        builder.AppendLine("}");
    }

    private static void AppendClientProperties(
        StringBuilder builder,
        IReadOnlyList<FacadeResource> resources,
        HashSet<string> withResourceType)
    {
        builder.AppendLine();
        builder.AppendLine("namespace EConomic.Rest");
        builder.AppendLine("{");
        builder.AppendLine("    /// <summary>");
        builder.AppendLine("    /// The legacy REST API, reached through <see cref=\"EConomic.EconomicClient.Rest\"/>.");
        builder.AppendLine("    /// </summary>");
        builder.AppendLine("    /// <remarks>");
        builder.AppendLine("    /// The two API surfaces are addressed separately and deliberately. Both publish an entity");
        builder.AppendLine("    /// called <c>Customer</c>, and they disagree: this one spells a reference");
        builder.AppendLine("    /// <c>paymentTermsNumber</c> where the OpenAPI services spell it <c>paymentTermId</c>, and");
        builder.AppendLine("    /// here the server assigns a customer number where there the caller supplies one. Naming the");
        builder.AppendLine("    /// surface at the call site is what keeps the two from being confused for each other.");
        builder.AppendLine("    /// </remarks>");
        builder.AppendLine("    /// <param name=\"httpClient\">The configured transport.</param>");
        builder.AppendLine("    public sealed partial class EconomicRestApi(System.Net.Http.HttpClient httpClient)");
        builder.AppendLine("    {");
        builder.AppendLine("        private System.Net.Http.HttpClient HttpClient { get; } = httpClient;");
        builder.AppendLine();

        foreach (var resource in resources)
        {
            // A resource type composes the same query and adds whatever else the resource offers.
            // The query itself never gains those: a filtered query is not a sensible place to
            // create from, nor to reach a nested collection through.
            if (withResourceType.Contains(resource.Entity))
            {
                builder.AppendLine(SchemaRegistry.WriteEnabledEntities.Contains(resource.Entity)
                    ? $"        /// <summary><c>{resource.Path}</c>, as a queryable and writable resource.</summary>"
                    : $"        /// <summary><c>{resource.Path}</c>, as a query plus its nested collections.</summary>");
                // The resource takes the HttpClient rather than the generated client: DELETE has no
                // schema and therefore no generated method, so it is issued directly.
                builder.AppendLine($"        public {resource.PublicName}Resource {resource.PropertyName} =>");
                builder.AppendLine("            new(HttpClient);");
                builder.AppendLine();
                continue;
            }

            builder.AppendLine($"        /// <summary><c>{resource.Path}</c>, as a composable query.</summary>");
            builder.AppendLine(
                $"        public EconomicQuery<{resource.PublicName}, {resource.PublicName}Filter, {resource.PublicName}Sort> {resource.PropertyName} =>");
            builder.AppendLine(
                $"            new(new {resource.PublicName}PageSource(new Generated.{resource.ClientClass}(HttpClient)));");
            builder.AppendLine();
        }

        builder.Length -= Environment.NewLine.Length;
        builder.AppendLine("    }");
        builder.AppendLine("}");
    }

    private static void AppendResource(
        StringBuilder builder,
        FacadeResource resource,
        FacadeWrite? write,
        IReadOnlyList<FacadeProperty> readProperties,
        JsonObject schemas,
        IReadOnlyList<FacadeNested> children,
        IList<string> skipped)
    {
        var entity = resource.PublicName;
        var collection = resource.Path.Trim('/');

        // Both writes return the whole resource — WriteResponseCorrector points their responses at
        // the read entity — so the response always carries the identifier and `self`, whatever the
        // request schema happens to describe.
        var createPayload = write?.CreateBody is null ? null : schemas[write.CreateBody] as JsonObject;
        var canCreate = createPayload is not null;
        var updatePayload = write?.UpdateBody is null ? null : schemas[write.UpdateBody] as JsonObject;

        List<WriteProperty>? createProperties = null;
        List<WriteProperty>? updateProperties = null;

        if (canCreate)
        {
            // The key stays on the create model: some resources assign it (customers get the next
            // free number) and others require the caller to choose it (customer groups reject a
            // create without one, products are keyed by a caller-chosen string). Whether it is
            // required follows the schema, which distinguishes the two correctly.
            var nestedWrites = new List<FacadeNestedWriteType>();
            createProperties = MapWriteProperties(
                createPayload!, write!.CreateBody!, $"{entity}Create", schemas,
                write.KeyProperty, includeKey: true, skipped, nestedWrites);

            AppendNestedWriteTypes(builder, nestedWrites);
            AppendWriteModel(builder, $"{entity}Create", entity, "create", createProperties);
        }

        if (updatePayload is not null)
        {
            var nestedWrites = new List<FacadeNestedWriteType>();
            updateProperties = MapWriteProperties(
                updatePayload, write!.UpdateBody!, $"{entity}Update", schemas,
                write.KeyProperty, includeKey: false, skipped, nestedWrites);

            AppendNestedWriteTypes(builder, nestedWrites);
            AppendWriteModel(builder, $"{entity}Update", entity, "replace", updateProperties);
        }

        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine(write is null
            ? $"/// The <c>{resource.Path}</c> resource: a composable query, plus its nested collections."
            : $"/// The <c>{resource.Path}</c> resource: a composable query, plus the writes e-conomic supports.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine("/// <remarks>");
        builder.AppendLine("/// The query-building methods each return a query and composition continues from there.");
        builder.AppendLine("/// Writes live on the resource rather than on the query: a query describes a filtered view,");
        builder.AppendLine("/// which is not a meaningful thing to create from.");
        builder.AppendLine("/// </remarks>");
        builder.AppendLine($"public sealed partial class {entity}Resource");
        builder.AppendLine("{");
        builder.AppendLine("    private readonly System.Net.Http.HttpClient _httpClient;");
        builder.AppendLine($"    private readonly Generated.{resource.ClientClass} _client;");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Creates the resource over a configured transport.</summary>");
        builder.AppendLine("    /// <param name=\"httpClient\">A client carrying the base address and authentication.</param>");
        builder.AppendLine($"    public {entity}Resource(System.Net.Http.HttpClient httpClient)");
        builder.AppendLine("    {");
        builder.AppendLine("        System.ArgumentNullException.ThrowIfNull(httpClient);");
        builder.AppendLine("        _httpClient = httpClient;");
        builder.AppendLine($"        _client = new Generated.{resource.ClientClass}(httpClient);");
        builder.AppendLine("    }");
        builder.AppendLine();

        var queryType = $"EconomicQuery<{entity}, {entity}Filter, {entity}Sort>";
        builder.AppendLine($"    private {queryType} Query => new(new {entity}PageSource(_client));");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>The resource as an unfiltered, unsorted query.</summary>");
        builder.AppendLine("    /// <returns>A query over every item.</returns>");
        builder.AppendLine($"    public {queryType} AsQuery() => Query;");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Restricts what is returned.</summary>");
        builder.AppendLine("    /// <param name=\"predicate\">A filter over the filterable properties.</param>");
        builder.AppendLine("    /// <returns>A query carrying the filter.</returns>");
        builder.AppendLine($"    public {queryType} Where(System.Linq.Expressions.Expression<System.Func<{entity}Filter, bool>> predicate) => Query.Where(predicate);");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Restricts what is returned, using e-conomic's filter syntax directly.</summary>");
        builder.AppendLine("    /// <param name=\"filter\">A filter expression.</param>");
        builder.AppendLine("    /// <returns>A query carrying the filter.</returns>");
        builder.AppendLine($"    public {queryType} WhereRaw(string filter) => Query.WhereRaw(filter);");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Orders ascending.</summary>");
        builder.AppendLine("    /// <param name=\"selector\">The property to sort by.</param>");
        builder.AppendLine("    /// <returns>A query carrying the sort.</returns>");
        builder.AppendLine($"    public {queryType} OrderBy(System.Linq.Expressions.Expression<System.Func<{entity}Sort, EconomicSortField>> selector) => Query.OrderBy(selector);");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Orders descending.</summary>");
        builder.AppendLine("    /// <param name=\"selector\">The property to sort by.</param>");
        builder.AppendLine("    /// <returns>A query carrying the sort.</returns>");
        builder.AppendLine($"    public {queryType} OrderByDescending(System.Linq.Expressions.Expression<System.Func<{entity}Sort, EconomicSortField>> selector) => Query.OrderByDescending(selector);");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Sets how many items are fetched per request.</summary>");
        builder.AppendLine("    /// <param name=\"pageSize\">Items per page, up to 1000.</param>");
        builder.AppendLine("    /// <returns>A query using that page size.</returns>");
        builder.AppendLine($"    public {queryType} WithPageSize(int pageSize) => Query.WithPageSize(pageSize);");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Enumerates everything, fetching pages as they are consumed.</summary>");
        builder.AppendLine("    /// <param name=\"cancellationToken\">Cancels the enumeration.</param>");
        builder.AppendLine("    /// <returns>The items.</returns>");
        builder.AppendLine($"    public System.Collections.Generic.IAsyncEnumerable<{entity}> AsAsyncEnumerable(System.Threading.CancellationToken cancellationToken = default) => Query.AsAsyncEnumerable(cancellationToken);");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Fetches a single page.</summary>");
        builder.AppendLine("    /// <param name=\"pageIndex\">Zero-based page index.</param>");
        builder.AppendLine("    /// <param name=\"cancellationToken\">Cancels the request.</param>");
        builder.AppendLine("    /// <returns>The page.</returns>");
        builder.AppendLine($"    public System.Threading.Tasks.Task<EconomicPage<{entity}>> GetPageAsync(int pageIndex, System.Threading.CancellationToken cancellationToken = default) => Query.GetPageAsync(pageIndex, cancellationToken);");

        if (canCreate)
        {
            builder.AppendLine();
            builder.AppendLine($"    /// <summary>Creates a {Camel(entity)}.</summary>");
            builder.AppendLine("    /// <param name=\"item\">The item to create.</param>");
            builder.AppendLine("    /// <param name=\"cancellationToken\">Cancels the request.</param>");
            builder.AppendLine("    /// <returns>The created item, as e-conomic stored it.</returns>");
            builder.AppendLine($"    public async System.Threading.Tasks.Task<{entity}> CreateAsync({entity}Create item, System.Threading.CancellationToken cancellationToken = default)");
            builder.AppendLine("    {");
            builder.AppendLine("        System.ArgumentNullException.ThrowIfNull(item);");
            builder.AppendLine();
            builder.AppendLine("        var response = await FacadeTransport.SendAsync(");
            builder.AppendLine($"            () => _client.{write!.CreateMethod}(ToGenerated(item), cancellationToken),");
            builder.AppendLine($"            \"POST {resource.Path}\").ConfigureAwait(false);");
            builder.AppendLine();
            builder.AppendLine("        return FromGenerated(response);");
            builder.AppendLine("    }");
        }

        if (updateProperties is not null)
        {
            builder.AppendLine();
            builder.AppendLine($"    /// <summary>Replaces an existing {Camel(entity)}.</summary>");
            builder.AppendLine($"    /// <param name=\"{write!.KeyName}\">The item to replace.</param>");
            builder.AppendLine("    /// <param name=\"item\">The replacement state.</param>");
            builder.AppendLine("    /// <param name=\"cancellationToken\">Cancels the request.</param>");
            builder.AppendLine("    /// <returns>The updated item, as e-conomic stored it.</returns>");
            builder.AppendLine("    /// <remarks>");
            builder.AppendLine("    /// This replaces rather than patches: a property left unset is cleared.");
            builder.AppendLine("    /// <para>");
            builder.AppendLine("    /// It is also an upsert. e-conomic answers <c>201 Created</c> and creates the resource");
            builder.AppendLine("    /// when the identifier does not exist, so this never reports a missing record as an");
            builder.AppendLine("    /// error. Verified against a live agreement.");
            builder.AppendLine("    /// </para>");
            builder.AppendLine("    /// </remarks>");
            builder.AppendLine($"    public async System.Threading.Tasks.Task<{entity}> UpdateAsync({write.KeyType} {write.KeyName}, {entity}Update item, System.Threading.CancellationToken cancellationToken = default)");
            builder.AppendLine("    {");
            builder.AppendLine("        System.ArgumentNullException.ThrowIfNull(item);");
            builder.AppendLine();
            builder.AppendLine("        var response = await FacadeTransport.SendAsync(");
            builder.AppendLine($"            () => _client.{write.UpdateMethod}({write.KeyName}, ToGenerated(item, {write.KeyName}), cancellationToken),");
            builder.AppendLine($"            \"PUT {resource.Path}/{{{write.KeyName}}}\").ConfigureAwait(false);");
            builder.AppendLine();
            builder.AppendLine("        return FromGenerated(response);");
            builder.AppendLine("    }");
        }

        if (write is { SupportsDelete: true })
        {
            builder.AppendLine();
            builder.AppendLine($"    /// <summary>Deletes a {Camel(entity)}.</summary>");
            builder.AppendLine($"    /// <param name=\"{write.KeyName}\">The item to delete.</param>");
            builder.AppendLine("    /// <param name=\"cancellationToken\">Cancels the request.</param>");
            builder.AppendLine("    /// <returns>A task that completes once the item is deleted.</returns>");
            builder.AppendLine("    /// <remarks>");
            builder.AppendLine("    /// e-conomic answers <c>204 No Content</c>. This endpoint comes from the published");
            builder.AppendLine("    /// documentation rather than a schema: <c>DELETE</c> has no body, so none is published.");
            builder.AppendLine("    /// </remarks>");
            builder.AppendLine($"    public System.Threading.Tasks.Task DeleteAsync({write.KeyType} {write.KeyName}, System.Threading.CancellationToken cancellationToken = default) =>");
            builder.AppendLine("        FacadeTransport.DeleteAsync(");
            builder.AppendLine("            _httpClient,");
            builder.AppendLine($"            string.Create(System.Globalization.CultureInfo.InvariantCulture, $\"{collection}/{{{write.KeyName}}}\"),");
            builder.AppendLine("            cancellationToken);");
        }

        if (canCreate)
        {
            AppendToGenerated(builder, write!.CreateBody!, $"{entity}Create", createProperties!, keyProperty: null, keyType: null);
        }

        if (updateProperties is not null)
        {
            var updateDeclaresKey =
                updatePayload!["properties"]?.AsObject().Any(p => Pascal(p.Key) == write!.KeyProperty) ?? false;

            AppendToGenerated(
                builder, write!.UpdateBody!, $"{entity}Update", updateProperties,
                write.KeyProperty, write.KeyType, updateDeclaresKey);
        }

        foreach (var child in children)
        {
            builder.AppendLine();
            builder.AppendLine($"    /// <summary>The <c>{child.Collection}</c> belonging to one {Camel(entity)}.</summary>");
            builder.AppendLine($"    /// <param name=\"{child.ParentKeyName}\">The owning {Camel(entity)}.</param>");
            builder.AppendLine($"    /// <returns>The nested collection, scoped to that {Camel(entity)}.</returns>");
            builder.AppendLine(
                $"    public {child.PublicName}Resource {child.AccessorName}({child.ParentKeyType} {child.ParentKeyName}) =>");
            builder.AppendLine($"        new(_httpClient, {child.ParentKeyName});");
        }

        // One mapper serves both writes: each returns the whole resource, so the response is the
        // same generated type the read path already maps. A resource with no writes has nothing to
        // map, and emitting it unused would not compile cleanly.
        if (write is not null)
        {
            builder.AppendLine();
            builder.AppendLine($"    private static {entity} FromGenerated(Generated.{resource.Entity} source) => new()");
            builder.AppendLine("    {");

            foreach (var property in readProperties)
            {
                builder.AppendLine($"        {property.Name} = {property.Mapping},");
            }

            builder.AppendLine("    };");
            builder.AppendLine();
            builder.AppendLine("    private static EconomicReference? Reference(int? number, System.Uri? self) =>");
            builder.AppendLine("        number is { } value ? new EconomicReference(value, self) : null;");
        }

        builder.AppendLine("}");
    }

    private static void AppendNested(
        StringBuilder builder,
        FacadeNested nested,
        JsonObject schemas,
        IList<string> skipped)
    {
        var entity = nested.PublicName;
        var write = nested.Write!;
        var nestedTypes = new List<FacadeNestedType>();
        var properties = MapProperties(
            schemas[nested.Entity]!.AsObject(), entity, nested.Entity, schemas, skipped, nestedTypes);

        AppendNestedTypes(builder, nestedTypes);

        // The model, mirroring a top-level one.
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine($"/// A resource from <c>{nested.Path}</c>.");
        builder.AppendLine("/// </summary>");
        AppendRecord(builder, entity, properties);

        // The page source, which differs from a top-level one only by carrying the parent's key.
        builder.AppendLine();
        builder.AppendLine($"/// <summary>Fetches pages of <c>{nested.Path}</c> and maps them to <see cref=\"{entity}\"/>.</summary>");
        builder.AppendLine(
            $"internal sealed class {entity}PageSource(Generated.{nested.ClientClass} client, {nested.ParentKeyType} {nested.ParentKeyName})");
        builder.AppendLine($"    : IEconomicPageSource<{entity}>");
        builder.AppendLine("{");
        builder.AppendLine($"    public async Task<EconomicPage<{entity}>> GetPageAsync(");
        builder.AppendLine("        EconomicPageRequest request,");
        builder.AppendLine("        CancellationToken cancellationToken)");
        builder.AppendLine("    {");
        builder.AppendLine("        var response = await FacadeTransport.SendAsync(");
        builder.AppendLine($"            () => client.{nested.ListMethod}(");
        builder.AppendLine($"                {nested.ParentKeyName}, request.Filter, request.Sort, request.PageIndex, request.PageSize, cancellationToken),");
        builder.AppendLine($"            \"GET {nested.Path}\").ConfigureAwait(false);");
        builder.AppendLine();
        builder.AppendLine($"        var items = new List<{entity}>(response.Collection?.Count ?? 0);");
        builder.AppendLine("        foreach (var item in response.Collection ?? [])");
        builder.AppendLine("        {");
        builder.AppendLine("            items.Add(Map(item));");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine($"        return new EconomicPage<{entity}>(items, request.PageIndex, request.PageSize);");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine($"    internal static {entity} Map(Generated.{nested.Entity} source) => new()");
        builder.AppendLine("    {");

        foreach (var property in properties)
        {
            builder.AppendLine($"        {property.Name} = {property.Mapping},");
        }

        builder.AppendLine("    };");
        builder.AppendLine();
        builder.AppendLine("    private static EconomicReference? Reference(int? number, System.Uri? self) =>");
        builder.AppendLine("        number is null ? null : new EconomicReference(number.Value, self);");
        builder.AppendLine("}");

        var createPayload = write.CreateBody is null ? null : schemas[write.CreateBody] as JsonObject;
        var updatePayload = write.UpdateBody is null ? null : schemas[write.UpdateBody] as JsonObject;

        List<WriteProperty>? createProperties = null;
        if (createPayload is not null)
        {
            var nestedCreateTypes = new List<FacadeNestedWriteType>();
            createProperties = MapWriteProperties(
                createPayload, write.CreateBody!, $"{entity}Create", schemas,
                write.KeyProperty, includeKey: true, skipped, nestedCreateTypes);

            AppendNestedWriteTypes(builder, nestedCreateTypes);
            AppendWriteModel(builder, $"{entity}Create", entity, "create", createProperties);
        }

        List<WriteProperty>? updateProperties = null;
        if (updatePayload is not null)
        {
            var nestedUpdateTypes = new List<FacadeNestedWriteType>();
            updateProperties = MapWriteProperties(
                updatePayload, write.UpdateBody!, $"{entity}Update", schemas,
                write.KeyProperty, includeKey: false, skipped, nestedUpdateTypes);

            AppendNestedWriteTypes(builder, nestedUpdateTypes);
            AppendWriteModel(builder, $"{entity}Update", entity, "replace", updateProperties);
        }

        // The resource itself, scoped to one parent.
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine($"/// The <c>{nested.Path}</c> collection, scoped to one {Camel(nested.ParentEntity)}.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine($"public sealed partial class {entity}Resource");
        builder.AppendLine("{");
        builder.AppendLine("    private readonly System.Net.Http.HttpClient _httpClient;");
        builder.AppendLine($"    private readonly Generated.{nested.ClientClass} _client;");
        builder.AppendLine($"    private readonly {nested.ParentKeyType} _{nested.ParentKeyName};");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Creates the resource over a configured transport.</summary>");
        builder.AppendLine("    /// <param name=\"httpClient\">A client carrying the base address and authentication.</param>");
        builder.AppendLine($"    /// <param name=\"{nested.ParentKeyName}\">The owning {Camel(nested.ParentEntity)}.</param>");
        builder.AppendLine($"    public {entity}Resource(System.Net.Http.HttpClient httpClient, {nested.ParentKeyType} {nested.ParentKeyName})");
        builder.AppendLine("    {");
        builder.AppendLine("        System.ArgumentNullException.ThrowIfNull(httpClient);");
        builder.AppendLine("        _httpClient = httpClient;");
        builder.AppendLine($"        _client = new Generated.{nested.ClientClass}(httpClient);");
        builder.AppendLine($"        _{nested.ParentKeyName} = {nested.ParentKeyName};");
        builder.AppendLine("    }");
        builder.AppendLine();

        var queryType = $"EconomicQuery<{entity}, {entity}Filter, {entity}Sort>";
        builder.AppendLine($"    private {queryType} Query => new(new {entity}PageSource(_client, _{nested.ParentKeyName}));");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>The collection as an unfiltered, unsorted query.</summary>");
        builder.AppendLine("    /// <returns>A query over every item.</returns>");
        builder.AppendLine($"    public {queryType} AsQuery() => Query;");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Restricts what is returned.</summary>");
        builder.AppendLine("    /// <param name=\"predicate\">A filter over the filterable properties.</param>");
        builder.AppendLine("    /// <returns>A query carrying the filter.</returns>");
        builder.AppendLine($"    public {queryType} Where(System.Linq.Expressions.Expression<System.Func<{entity}Filter, bool>> predicate) => Query.Where(predicate);");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Enumerates everything, fetching pages as they are consumed.</summary>");
        builder.AppendLine("    /// <param name=\"cancellationToken\">Cancels the enumeration.</param>");
        builder.AppendLine("    /// <returns>The items.</returns>");
        builder.AppendLine($"    public System.Collections.Generic.IAsyncEnumerable<{entity}> AsAsyncEnumerable(System.Threading.CancellationToken cancellationToken = default) => Query.AsAsyncEnumerable(cancellationToken);");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Fetches a single page.</summary>");
        builder.AppendLine("    /// <param name=\"pageIndex\">Zero-based page index.</param>");
        builder.AppendLine("    /// <param name=\"cancellationToken\">Cancels the request.</param>");
        builder.AppendLine("    /// <returns>The page.</returns>");
        builder.AppendLine($"    public System.Threading.Tasks.Task<EconomicPage<{entity}>> GetPageAsync(int pageIndex, System.Threading.CancellationToken cancellationToken = default) => Query.GetPageAsync(pageIndex, cancellationToken);");
        if (createProperties is not null)
        {
            builder.AppendLine();
            builder.AppendLine($"    /// <summary>Creates a {Camel(entity)}.</summary>");
            builder.AppendLine("    /// <param name=\"item\">The item to create.</param>");
            builder.AppendLine("    /// <param name=\"cancellationToken\">Cancels the request.</param>");

            var createReturnsCollection = SchemaRegistry.CollectionWriteResponses.Contains(nested.Entity);
            var createReturns = createReturnsCollection
                ? $"System.Collections.Generic.IReadOnlyList<{entity}>"
                : entity;

            builder.AppendLine(createReturnsCollection
                ? "    /// <returns>The created items, as e-conomic stored them.</returns>"
                : "    /// <returns>The created item, as e-conomic stored it.</returns>");

            if (createReturnsCollection)
            {
                builder.AppendLine("    /// <remarks>");
                builder.AppendLine("    /// This answers with a collection because e-conomic may split the entries it was sent");
                builder.AppendLine("    /// across more than one record. Verified against a live agreement.");
                builder.AppendLine("    /// </remarks>");
            }

            builder.AppendLine($"    public async System.Threading.Tasks.Task<{createReturns}> CreateAsync({entity}Create item, System.Threading.CancellationToken cancellationToken = default)");
            builder.AppendLine("    {");
            builder.AppendLine("        System.ArgumentNullException.ThrowIfNull(item);");
            builder.AppendLine();
            builder.AppendLine("        var response = await FacadeTransport.SendAsync(");
            builder.AppendLine($"            () => _client.{write.CreateMethod}(_{nested.ParentKeyName}, ToGenerated(item), cancellationToken),");
            builder.AppendLine($"            \"POST {nested.Path}\").ConfigureAwait(false);");
            builder.AppendLine();
            builder.AppendLine(createReturnsCollection
                ? "        return FacadeTransport.MapList(response, FromGenerated);"
                : "        return FromGenerated(response);");
            builder.AppendLine("    }");
        }

        if (updateProperties is not null)
        {
            builder.AppendLine();
            builder.AppendLine($"    /// <summary>Replaces an existing {Camel(entity)}.</summary>");
            builder.AppendLine($"    /// <param name=\"{write!.KeyName}\">The item to replace.</param>");
            builder.AppendLine("    /// <param name=\"item\">The replacement state.</param>");
            builder.AppendLine("    /// <param name=\"cancellationToken\">Cancels the request.</param>");
            builder.AppendLine("    /// <returns>The updated item, as e-conomic stored it.</returns>");
            builder.AppendLine("    /// <remarks>This replaces rather than patches: a property left unset is cleared.</remarks>");
            builder.AppendLine($"    public async System.Threading.Tasks.Task<{entity}> UpdateAsync({write.KeyType} {write.KeyName}, {entity}Update item, System.Threading.CancellationToken cancellationToken = default)");
            builder.AppendLine("    {");
            builder.AppendLine("        System.ArgumentNullException.ThrowIfNull(item);");
            builder.AppendLine();
            builder.AppendLine("        var response = await FacadeTransport.SendAsync(");
            builder.AppendLine($"            () => _client.{write.UpdateMethod}(_{nested.ParentKeyName}, {write.KeyName}, ToGenerated(item, {write.KeyName}), cancellationToken),");
            builder.AppendLine($"            \"PUT {nested.Path}/{{{write.KeyName}}}\").ConfigureAwait(false);");
            builder.AppendLine();
            builder.AppendLine("        return FromGenerated(response);");
            builder.AppendLine("    }");
        }

        if (write is { SupportsDelete: true })
        {
            builder.AppendLine();
            builder.AppendLine($"    /// <summary>Deletes a {Camel(entity)}.</summary>");
            builder.AppendLine($"    /// <param name=\"{write.KeyName}\">The item to delete.</param>");
            builder.AppendLine("    /// <param name=\"cancellationToken\">Cancels the request.</param>");
            builder.AppendLine("    /// <returns>A task that completes once the item is deleted.</returns>");
            builder.AppendLine($"    public System.Threading.Tasks.Task DeleteAsync({write.KeyType} {write.KeyName}, System.Threading.CancellationToken cancellationToken = default) =>");
            builder.AppendLine("        FacadeTransport.DeleteAsync(");
            builder.AppendLine("            _httpClient,");
            builder.AppendLine(
                $"            string.Create(System.Globalization.CultureInfo.InvariantCulture, "
                + $"$\"{nested.ParentCollection}/{{_{nested.ParentKeyName}}}/{nested.Collection}/{{{write.KeyName}}}\"),");
            builder.AppendLine("            cancellationToken);");
        }

        if (createProperties is not null)
        {
            AppendToGenerated(
                builder, write.CreateBody!, $"{entity}Create", createProperties,
                keyProperty: null, keyType: null);
        }

        if (updateProperties is not null)
        {
            var declaresKey = updatePayload!["properties"]?.AsObject().Any(p => Pascal(p.Key) == write.KeyProperty) ?? false;
            AppendToGenerated(
                builder, write.UpdateBody!, $"{entity}Update", updateProperties,
                write.KeyProperty, write.KeyType, declaresKey);
        }

        if (createProperties is not null || updateProperties is not null)
        {
            builder.AppendLine();
            builder.AppendLine($"    private static {entity} FromGenerated(Generated.{nested.Entity} source) => new()");
            builder.AppendLine("    {");

            foreach (var property in properties)
            {
                builder.AppendLine($"        {property.Name} = {property.Mapping},");
            }

            builder.AppendLine("    };");
            builder.AppendLine();
            builder.AppendLine("    private static EconomicReference? Reference(int? number, System.Uri? self) =>");
            builder.AppendLine("        number is { } value ? new EconomicReference(value, self) : null;");
        }

        builder.AppendLine("}");
    }

    /// <summary>Finds the collections that hang off another resource.</summary>
    /// <param name="document">The merged OpenAPI document.</param>
    /// <param name="resources">The top-level resources already discovered.</param>
    /// <returns>The nested collections, in path order.</returns>
    /// <remarks>
    /// The parent has to be a published collection, because the accessor hangs off it — but it need
    /// not be writable itself. Journals are the case: they are read-only, and their vouchers are
    /// how entries are posted.
    /// </remarks>
    public static IReadOnlyList<FacadeNested> NestedResources(
        JsonObject document,
        IReadOnlyList<FacadeResource> resources)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(resources);

        var schemas = document["components"]!["schemas"]!.AsObject();
        var paths = document["paths"]!.AsObject();
        var byCollection = resources.ToDictionary(r => r.Path.Trim('/'), StringComparer.Ordinal);
        var nested = new List<FacadeNested>();

        foreach (var (path, item) in paths.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var segments = path.Trim('/').Split('/');

            if (segments.Length != 3
                || !segments[1].StartsWith('{')
                || segments[2].StartsWith('{')
                || item?["get"] is not JsonObject get
                || !byCollection.TryGetValue(segments[0], out var parent))
            {
                continue;
            }

            var envelope = Reference(get["responses"]?["200"]?["content"]?["application/json"]?["schema"]);
            if (envelope is null
                || schemas[envelope] is not JsonObject envelopeSchema
                || Reference(envelopeSchema["properties"]?["collection"]?["items"]) is not { } entity)
            {
                continue;
            }

            // A collection whose item is already a top-level resource is a view of it, not a
            // resource of its own: /product-groups/{n}/products returns products, which
            // client.Products already creates, deletes and filters. Emitting it again would
            // redeclare every one of that entity's records.
            if (resources.Any(r => r.Entity == entity))
            {
                continue;
            }

            // Nothing to add for a nested collection with neither a create nor a delete: the
            // parent's own query already reaches its contents through the link on the model.
            var write = NestedWriteFor(paths, path, entity);
            if (write is null)
            {
                continue;
            }

            var parentKey = segments[1].Trim('{', '}');
            var parentKeyType = get["parameters"]?[0]?["schema"]?["type"]?.GetValue<string>() == "string"
                ? "string"
                : "int";

            nested.Add(new FacadeNested(
                path,
                parent.Entity,
                parentKey,
                parentKeyType,
                segments[0],
                segments[2],
                entity,
                parent.ClientClass,
                MethodName(get),
                SchemaRegistry.Identifier(segments[2]),
                write,
                SchemaRegistry.PublicName(entity)));
        }

        return nested;
    }

    /// <summary>The write operations a nested collection supports, if any.</summary>
    /// <remarks>
    /// A collection with neither a create nor a delete is left out: its contents are already
    /// reachable through the link on the parent's model, so a resource type would add nothing.
    /// A delete on its own is enough, though — journal entries have no create of their own, and
    /// deleting one is how a mis-posted voucher is undone.
    /// </remarks>
    private static FacadeWrite? NestedWriteFor(JsonObject paths, string path, string entity)
    {
        var post = paths[path]?["post"] as JsonObject;
        var createBody = post is null
            ? null
            : Reference(post["requestBody"]?["content"]?["application/json"]?["schema"]);

        var canDelete = SchemaRegistry.DeletableEntities.Contains(entity);

        if (createBody is null && !canDelete)
        {
            return null;
        }

        string? updateBody = null;
        string? updateMethod = null;
        var keyType = "int";

        foreach (var (candidate, item) in paths)
        {
            if (!candidate.StartsWith(path + "/{", StringComparison.Ordinal)
                || item?["put"] is not JsonObject put
                || Reference(put["requestBody"]?["content"]?["application/json"]?["schema"]) is not { } schema)
            {
                continue;
            }

            updateBody = schema;
            updateMethod = MethodName(put);
            keyType = put["parameters"]?[1]?["schema"]?["type"]?.GetValue<string>() == "string" ? "string" : "int";
            break;
        }

        var keyProperty = SchemaRegistry.KeyProperty(entity);

        return new FacadeWrite(
            createBody,
            post is null ? null : MethodName(post),
            updateBody,
            updateMethod,
            Camel(keyProperty),
            keyType,
            keyProperty,
            canDelete);
    }

    /// <summary>Finds the write operations a resource supports, when it is write-enabled.</summary>
    /// <param name="document">The merged OpenAPI document.</param>
    /// <param name="resource">The resource.</param>
    /// <returns>The write surface, or <see langword="null"/> when there is none.</returns>
    public static FacadeWrite? WriteFor(JsonObject document, FacadeResource resource)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(resource);

        var paths = document["paths"]!.AsObject();
        var keyProperty = SchemaRegistry.KeyProperty(resource.Entity);
        var keyName = Camel(keyProperty);

        string? createBody = null;
        string? createMethod = null;

        if (paths[resource.Path]?["post"] is JsonObject post
            && Reference(post["requestBody"]?["content"]?["application/json"]?["schema"]) is { } body)
        {
            createBody = body;
            createMethod = MethodName(post);
        }

        string? updateBody = null;
        string? updateMethod = null;
        var keyType = "int";

        foreach (var (path, item) in paths.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            // The update lives one segment below the collection, on its identifier.
            // The update lives on the collection's own identifier, so it is exactly one segment
            // deeper — which is two slashes for /customers, three for /invoices/drafts.
            if (!path.StartsWith(resource.Path + "/", StringComparison.Ordinal)
                || path.Count(c => c == '/') != resource.Path.Count(c => c == '/') + 1
                || item?["put"] is not JsonObject put
                || Reference(put["requestBody"]?["content"]?["application/json"]?["schema"]) is not { } updateSchema)
            {
                continue;
            }

            updateBody = updateSchema;
            updateMethod = MethodName(put);
            keyType = put["parameters"]?[0]?["schema"]?["type"]?.GetValue<string>() == "string" ? "string" : "int";
            break;
        }

        if (createBody is null && updateBody is null)
        {
            return null;
        }

        return new FacadeWrite(
            createBody,
            createMethod,
            updateBody,
            updateMethod,
            keyName,
            keyType,
            keyProperty,
            SchemaRegistry.DeletableEntities.Contains(resource.Entity));
    }

    private static string MethodName(JsonObject operation)
    {
        var operationId = operation["operationId"]!.GetValue<string>();
        return $"{char.ToUpperInvariant(operationId[0])}{operationId[1..]}Async";
    }

    /// <summary>The properties a caller may set on a write payload.</summary>
    /// <param name="payload">The request-body schema.</param>
    /// <param name="payloadName">Component name, for reporting what cannot be mapped.</param>
    /// <param name="modelName">The public write model, which also prefixes its nested records.</param>
    /// <param name="schemas">Every registered component.</param>
    /// <param name="keyProperty">The resource's identifier property.</param>
    /// <param name="includeKey">Whether the caller supplies the identifier.</param>
    /// <param name="skipped">Collects what could not be mapped.</param>
    /// <param name="nested">Collects the records to emit for composite properties.</param>
    /// <param name="depth">Nesting depth, guarding against a self-referential schema.</param>
    private static List<WriteProperty> MapWriteProperties(
        JsonObject payload,
        string payloadName,
        string modelName,
        JsonObject schemas,
        string keyProperty,
        bool includeKey,
        IList<string> skipped,
        List<FacadeNestedWriteType> nested,
        int depth = 0)
    {
        var mapped = new List<WriteProperty>();

        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in payload["required"]?.AsArray() ?? [])
        {
            if (name?.GetValue<string>() is { } value)
            {
                required.Add(value);
            }
        }

        foreach (var (name, node) in payload["properties"]?.AsObject() ?? [])
        {
            if (node is not JsonObject property)
            {
                continue;
            }

            var propertyName = Pascal(name);
            var resolved = Resolve(property, schemas);

            if (Composition(resolved) is { } composition)
            {
                skipped.Add($"{payloadName}.{name} ({composition}, no single shape)");
                continue;
            }

            // A server-maintained value cannot be set, so offering it would be a lie.
            if (resolved["readOnly"]?.GetValue<bool>() == true)
            {
                continue;
            }

            if (propertyName == keyProperty && !includeKey)
            {
                continue;
            }

            var isRequired = required.Contains(name);

            // The generated enum is internal, so the public model takes the string e-conomic sends
            // and FacadeTransport.ParseEnum converts it. Skipping these was wrong: paymentTermsType
            // is required, so a payment term could never be created without one.
            if (resolved["enum"] is JsonArray)
            {
                mapped.Add(new WriteProperty(
                    propertyName,
                    isRequired ? "string" : "string?",
                    propertyName,
                    ReferenceNumber: null,
                    isRequired,
                    IsEnum: true));

                continue;
            }
            var type = resolved["type"]?.GetValue<string>();
            var format = resolved["format"]?.GetValue<string>();

            var scalar = (type, format) switch
            {
                ("string", "date") => "DateOnly?",
                ("string", "date-time") => "DateTimeOffset?",
                ("string", "uri") => null, // a link is server-assigned
                ("string", _) => "string?",
                ("integer", _) => "int?",
                ("number", _) => "decimal?",
                ("boolean", _) => "bool",
                _ => null,
            };

            if (scalar is not null)
            {
                mapped.Add(new WriteProperty(
                    propertyName,
                    isRequired ? Required(scalar) : scalar,
                    propertyName,
                    ReferenceNumber: null,
                    isRequired));

                continue;
            }

            if (ReferenceNumber(resolved) is { } numberProperty)
            {
                mapped.Add(new WriteProperty(
                    $"{propertyName}{(propertyName.EndsWith("Number", StringComparison.Ordinal) ? string.Empty : "Number")}",
                    isRequired ? "int" : "int?",
                    propertyName,
                    Pascal(numberProperty),
                    isRequired));

                continue;
            }

            if (type == "array" && resolved["items"] is JsonObject itemNode)
            {
                var item = Resolve(itemNode, schemas);

                if (item["enum"] is JsonArray)
                {
                    mapped.Add(new WriteProperty(
                        propertyName, "IReadOnlyList<string>", propertyName,
                        ReferenceNumber: null, IsRequired: false, Kind: WriteKind.EnumList));

                    continue;
                }

                if (item["type"]?.GetValue<string>() == "object" && depth < MaxNestingDepth)
                {
                    var elementName = Singular(modelName + propertyName);
                    var elementProperties = MapWriteProperties(
                        item, $"{payloadName}.{name}", elementName, schemas,
                        keyProperty: string.Empty, includeKey: true, skipped, nested, depth + 1);

                    if (elementProperties.Count > 0)
                    {
                        nested.Add(new FacadeNestedWriteType(
                            elementName, modelName, name, elementProperties, IsElement: true));

                        mapped.Add(new WriteProperty(
                            propertyName, $"IReadOnlyList<{elementName}>", propertyName,
                            ReferenceNumber: null, IsRequired: false,
                            Nested: elementProperties, Kind: WriteKind.CompositeList));

                        continue;
                    }
                }

                skipped.Add($"{payloadName}.{name} (array, not settable)");
                continue;
            }

            if (type == "object" && depth < MaxNestingDepth)
            {
                var nestedName = modelName + propertyName;
                var nestedProperties = MapWriteProperties(
                    resolved, $"{payloadName}.{name}", nestedName, schemas,
                    keyProperty: string.Empty, includeKey: true, skipped, nested, depth + 1);

                if (nestedProperties.Count > 0)
                {
                    nested.Add(new FacadeNestedWriteType(nestedName, modelName, name, nestedProperties));

                    mapped.Add(new WriteProperty(
                        propertyName, isRequired ? nestedName : $"{nestedName}?", propertyName,
                        ReferenceNumber: null, isRequired,
                        Nested: nestedProperties, Kind: WriteKind.Composite));

                    continue;
                }
            }

            skipped.Add($"{payloadName}.{name} ({type ?? "object"}, not settable)");
        }

        return mapped;
    }

    private static void AppendWriteModel(
        StringBuilder builder,
        string typeName,
        string entity,
        string verb,
        IReadOnlyList<WriteProperty> properties)
    {
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine($"/// The <c>{entity}</c> to {verb}.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine("/// <remarks>");
        builder.AppendLine("/// Only the properties e-conomic accepts appear here. Server-maintained values are absent,");
        builder.AppendLine("/// and references to other resources are flattened to their numbers.");
        builder.AppendLine("/// </remarks>");
        AppendWriteRecord(builder, typeName, properties);
    }

    /// <summary>Emits the public records for a write model's composite properties.</summary>
    private static void AppendNestedWriteTypes(
        StringBuilder builder,
        IReadOnlyList<FacadeNestedWriteType> nestedTypes)
    {
        foreach (var nested in nestedTypes)
        {
            builder.AppendLine();
            builder.AppendLine("/// <summary>");
            builder.AppendLine(nested.IsElement
                ? $"/// One entry in the <c>{nested.Property}</c> of a <see cref=\"{nested.Owner}\"/>."
                : $"/// The <c>{nested.Property}</c> of a <see cref=\"{nested.Owner}\"/>.");
            builder.AppendLine("/// </summary>");
            AppendWriteRecord(builder, nested.Name, nested.Properties);
        }
    }

    private static void AppendWriteRecord(
        StringBuilder builder,
        string typeName,
        IReadOnlyList<WriteProperty> properties)
    {
        builder.AppendLine($"public sealed record {typeName}");
        builder.AppendLine("{");

        foreach (var property in properties.OrderByDescending(p => p.IsRequired).ThenBy(p => p.Name, StringComparer.Ordinal))
        {
            var isList = property.PublicType.StartsWith("IReadOnlyList<", StringComparison.Ordinal);
            var modifier = property.IsRequired && !isList ? "required " : string.Empty;
            var initializer = isList ? " = [];" : string.Empty;

            builder.AppendLine($"    /// <summary>The <c>{Camel(property.GeneratedName)}</c> field.</summary>");
            builder.AppendLine($"    public {modifier}{property.PublicType} {property.Name} {{ get; init; }}{initializer}");
            builder.AppendLine();
        }

        builder.Length -= Environment.NewLine.Length;
        builder.AppendLine("}");
    }

    private static void AppendToGenerated(
        StringBuilder builder,
        string payloadType,
        string modelType,
        IReadOnlyList<WriteProperty> properties,
        string? keyProperty,
        string? keyType,
        bool assignKey = false)
    {
        var signature = keyProperty is null
            ? $"    private static Generated.{payloadType} ToGenerated({modelType} source)"
            : $"    private static Generated.{payloadType} ToGenerated({modelType} source, {keyType} {Camel(keyProperty)})";

        builder.AppendLine();
        builder.AppendLine(signature);
        builder.AppendLine("    {");
        builder.AppendLine($"        var target = new Generated.{payloadType}();");

        // The key goes in the body only when the payload declares it; several update schemas
        // identify the resource solely by the path.
        if (keyProperty is not null && assignKey)
        {
            builder.AppendLine($"        target.{keyProperty} = {Camel(keyProperty)};");
        }

        AppendAssignments(builder, indent: 8, target: "target", source: "source", properties, depth: 0);

        builder.AppendLine();
        builder.AppendLine("        return target;");
        builder.AppendLine("    }");
    }

    /// <summary>
    /// Emits the statements that copy a public write model onto its generated payload.
    /// </summary>
    /// <remarks>
    /// Statements rather than an object initializer, because none of the generated types can be
    /// named here — NSwag invents the names of every nested class and enum. Building the payload a
    /// property at a time keeps target-typed <c>new()</c> and inference-by-argument available at
    /// every level, which an initializer would not: a nested object's own enum cannot be converted
    /// until the object it belongs to exists.
    /// </remarks>
    private static void AppendAssignments(
        StringBuilder builder,
        int indent,
        string target,
        string source,
        IReadOnlyList<WriteProperty> properties,
        int depth)
    {
        var pad = new string(' ', indent);

        foreach (var property in properties)
        {
            var member = $"{target}.{property.GeneratedName}";
            var value = $"{source}.{property.Name}";

            switch (property)
            {
                // ParseEnum answers with the property's current value when nothing is supplied, so
                // this is safe to write unconditionally.
                case { IsEnum: true }:
                    builder.AppendLine($"{pad}{member} = FacadeTransport.ParseEnum({value}, {member});");
                    break;

                case { Kind: WriteKind.EnumList }:
                    builder.AppendLine($"{pad}{member} = FacadeTransport.ParseEnums({value}, {member});");
                    break;

                case { Kind: WriteKind.CompositeList }:
                    {
                        var element = $"element{depth}";
                        var item = $"item{depth}";
                        builder.AppendLine(
                            $"{pad}{member} = FacadeTransport.BuildList({value}, {member}, ({item}, {element}) =>");
                        builder.AppendLine($"{pad}{{");
                        AppendAssignments(builder, indent + 4, element, item, property.Nested!, depth + 1);
                        builder.AppendLine($"{pad}}});");
                        break;
                    }

                case { Kind: WriteKind.Composite, IsRequired: true }:
                    builder.AppendLine($"{pad}{member} = new();");
                    AppendAssignments(builder, indent, member, value, property.Nested!, depth + 1);
                    break;

                case { Kind: WriteKind.Composite }:
                    {
                        var local = $"{Camel(property.Name)}{depth}";
                        builder.AppendLine($"{pad}if ({value} is {{ }} {local})");
                        builder.AppendLine($"{pad}{{");
                        builder.AppendLine($"{pad}    {member} = new();");
                        AppendAssignments(builder, indent + 4, member, local, property.Nested!, depth + 1);
                        builder.AppendLine($"{pad}}}");
                        break;
                    }

                case { ReferenceNumber: { } number, IsRequired: true }:
                    builder.AppendLine($"{pad}{member} = new() {{ {number} = {value} }};");
                    break;

                // A null string leaves the generated property null, which the serializer omits, so
                // these need no guard.
                case { PublicType: "bool" or "string" }:
                    builder.AppendLine($"{pad}{member} = {value};");
                    break;

                case { PublicType: "string?" }:
                    builder.AppendLine($"{pad}{member} = {value}!;");
                    break;

                case { PublicType: "decimal" }:
                    builder.AppendLine($"{pad}{member} = (double){value};");
                    break;

                case { IsRequired: true }:
                    builder.AppendLine($"{pad}{member} = {value};");
                    break;

                // Everything else is optional and lands on a non-nullable generated property, so
                // "absent" can only be expressed by not assigning at all.
                default:
                    {
                        var local = $"{Camel(property.Name)}{depth}";
                        builder.AppendLine($"{pad}if ({value} is {{ }} {local})");
                        builder.AppendLine($"{pad}{{");

                        builder.AppendLine(property switch
                        {
                            { ReferenceNumber: { } number } => $"{pad}    {member} = new() {{ {number} = {local} }};",
                            { PublicType: "decimal?" } => $"{pad}    {member} = (double){local};",
                            _ => $"{pad}    {member} = {local};",
                        });

                        builder.AppendLine($"{pad}}}");
                        break;
                    }
            }
        }
    }

    private static JsonObject Resolve(JsonObject schema, JsonObject schemas)
    {
        if (Reference(schema) is { } name && schemas[name] is JsonObject target)
        {
            return target;
        }

        return schema;
    }

    private static string? Reference(JsonNode? node) =>
        node?["$ref"]?.GetValue<string>() is { } reference
        && reference.StartsWith(RefPrefix, StringComparison.Ordinal)
            ? reference[RefPrefix.Length..]
            : null;

    /// <summary>Drops the nullable marker for a property the schema guarantees is present.</summary>
    private static string Required(string type) =>
        type.EndsWith('?') ? type[..^1] : type;

    /// <summary>
    /// The client property for a collection path. A second segment qualifies the first rather than
    /// nesting under it — <c>/invoices/drafts</c> is the draft invoices — so the two read most
    /// naturally in the opposite order to the path.
    /// </summary>
    private static string PropertyNameFor(string[] segments)
    {
        if (segments.Length == 1)
        {
            return SchemaRegistry.Identifier(segments[0]);
        }

        // "drafts" qualifies "invoices" as "DraftInvoices", not "DraftsInvoices".
        var qualifier = segments[1].EndsWith('s') && segments[1].Length > 1
            ? segments[1][..^1]
            : segments[1];

        return SchemaRegistry.Identifier(qualifier) + SchemaRegistry.Identifier(segments[0]);
    }

    private static string Pascal(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    private static string Camel(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
