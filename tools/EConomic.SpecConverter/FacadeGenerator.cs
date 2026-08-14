using System.Text;
using System.Text.Json.Nodes;

namespace EConomic.SpecConverter;

/// <summary>A top-level collection endpoint that can be exposed as a query.</summary>
/// <param name="Path">The URL path, e.g. <c>/customers</c>.</param>
/// <param name="Entity">The component name of the collection's item type.</param>
/// <param name="ClientClass">The generated NSwag client class.</param>
/// <param name="Method">The generated method that fetches the collection.</param>
/// <param name="PropertyName">The property to expose on the client, e.g. <c>Customers</c>.</param>
public sealed record FacadeResource(
    string Path,
    string Entity,
    string ClientClass,
    string Method,
    string PropertyName);

/// <summary>A property carried across from a generated entity to its public model.</summary>
/// <param name="Name">The C# property name.</param>
/// <param name="PublicType">Type on the public model.</param>
/// <param name="Mapping">Expression mapping from <c>source</c>.</param>
public sealed record FacadeProperty(string Name, string PublicType, string Mapping);

/// <summary>A property a caller may set when creating or updating a resource.</summary>
/// <param name="Name">The C# property name on the public write model.</param>
/// <param name="PublicType">Type on the public write model.</param>
/// <param name="GeneratedName">The property on the generated payload type.</param>
/// <param name="ReferenceNumber">When the payload nests a reference, the number property inside it.</param>
/// <param name="IsRequired">Whether e-conomic requires it.</param>
/// <param name="IsEnum">Whether the generated property is an enum the public model exposes as text.</param>
public sealed record WriteProperty(
    string Name,
    string PublicType,
    string GeneratedName,
    string? ReferenceNumber,
    bool IsRequired,
    bool IsEnum = false);

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
    FacadeWrite? Write);

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
                PropertyNameFor(segments)));
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

        foreach (var resource in resources)
        {
            var properties = MapProperties(schemas[resource.Entity]!.AsObject(), resource.Entity, schemas, skipped);

            AppendModel(builder, resource, properties);
            AppendPageSource(builder, resource, properties);

            if (SchemaRegistry.WriteEnabledEntities.Contains(resource.Entity)
                && WriteFor(document, resource) is { } write)
            {
                var children = nested.Where(n => n.ParentEntity == resource.Entity).ToList();
                AppendResource(builder, resource, write, properties, schemas, children, skipped);
            }
        }

        foreach (var child in nested)
        {
            AppendNested(builder, child, schemas, skipped);
        }

        builder.AppendLine("}");
        AppendClientProperties(builder, resources);
        return builder.ToString();
    }

    private static List<FacadeProperty> MapProperties(
        JsonObject entity,
        string entityName,
        JsonObject schemas,
        IList<string> skipped)
    {
        var mapped = new List<FacadeProperty>();

        // The schema says which properties are always present. Honouring that keeps the key and
        // name non-nullable instead of making every consumer null-check what cannot be null.
        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in entity["required"]?.AsArray() ?? [])
        {
            if (name?.GetValue<string>() is { } value)
            {
                required.Add(value);
            }
        }

        foreach (var (name, node) in entity["properties"]?.AsObject() ?? [])
        {
            if (node is not JsonObject property)
            {
                continue;
            }

            var propertyName = Pascal(name);
            var resolved = Resolve(property, schemas);
            var type = resolved["type"]?.GetValue<string>();
            var format = resolved["format"]?.GetValue<string>();

            // A constrained string becomes a generated enum, which is internal and so cannot appear
            // on a public model. The value is carried across as its name: adding the enum to the
            // public surface would tie it to the spec, and a new member would then be a breaking
            // change rather than just a new string.
            if (resolved["enum"] is JsonArray)
            {
                mapped.Add(new FacadeProperty(propertyName, "string?", $"source.{propertyName}.ToString()"));
                continue;
            }

            var scalar = (type, format) switch
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

            if (scalar is not null)
            {
                // Numbers and booleans are non-nullable in the generated layer regardless, so a
                // nullable public property would promise an absence it can never report. Dates are
                // the exception: a default timestamp is meaningless, so null carries real
                // information there.
                var alwaysPresent = scalar is "int?" or "decimal?" or "bool";
                var isRequired = alwaysPresent || required.Contains(name);

                // A JSON number is a double in the generated layer because the schema says only
                // "number"; money belongs in decimal on the public model.
                var mapping = scalar switch
                {
                    "decimal?" when isRequired => $"(decimal)source.{propertyName}",
                    "decimal?" => $"(decimal?)source.{propertyName}",
                    "string?" when isRequired => $"source.{propertyName} ?? string.Empty",
                    "DateTimeOffset?" or "DateOnly?" when !isRequired =>
                        $"source.{propertyName} == default ? null : source.{propertyName}",
                    _ => $"source.{propertyName}",
                };

                mapped.Add(new FacadeProperty(propertyName, isRequired ? Required(scalar) : scalar, mapping));
                continue;
            }

            if (ReferenceNumber(resolved) is { } numberProperty)
            {
                mapped.Add(new FacadeProperty(
                    propertyName,
                    "EconomicReference?",
                    $"Reference(source.{propertyName}?.{Pascal(numberProperty)}, source.{propertyName}?.Self)"));

                continue;
            }

            skipped.Add($"{entityName}.{name} ({type ?? "object"})");
        }

        return mapped;
    }

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
        builder.AppendLine($"public sealed record {resource.Entity}");
        builder.AppendLine("{");

        foreach (var property in properties)
        {
            // A non-nullable reference type has no sensible default, so the compiler needs to know
            // the mapper always supplies it.
            var modifier = !property.PublicType.EndsWith('?')
                && property.PublicType is "string" or "System.Uri" or "EconomicReference"
                    ? "required "
                    : string.Empty;

            builder.AppendLine($"    /// <summary>The <c>{Camel(property.Name)}</c> field.</summary>");
            builder.AppendLine($"    public {modifier}{property.PublicType} {property.Name} {{ get; init; }}");
            builder.AppendLine();
        }

        builder.Length -= Environment.NewLine.Length;
        builder.AppendLine("}");
    }

    private static void AppendPageSource(StringBuilder builder, FacadeResource resource, IReadOnlyList<FacadeProperty> properties)
    {
        builder.AppendLine();
        builder.AppendLine($"/// <summary>Fetches pages of <c>{resource.Path}</c> and maps them to <see cref=\"{resource.Entity}\"/>.</summary>");
        builder.AppendLine($"internal sealed class {resource.Entity}PageSource(Generated.{resource.ClientClass} client)");
        builder.AppendLine($"    : IEconomicPageSource<{resource.Entity}>");
        builder.AppendLine("{");
        builder.AppendLine($"    public async Task<EconomicPage<{resource.Entity}>> GetPageAsync(");
        builder.AppendLine("        EconomicPageRequest request,");
        builder.AppendLine("        CancellationToken cancellationToken)");
        builder.AppendLine("    {");
        builder.AppendLine("        var response = await FacadeTransport.SendAsync(");
        builder.AppendLine($"            () => client.{resource.Method}(");
        builder.AppendLine("                request.Filter, request.Sort, request.PageIndex, request.PageSize, cancellationToken),");
        builder.AppendLine($"            \"GET {resource.Path}\").ConfigureAwait(false);");
        builder.AppendLine();
        builder.AppendLine($"        var items = new List<{resource.Entity}>(response.Collection?.Count ?? 0);");
        builder.AppendLine("        foreach (var item in response.Collection ?? [])");
        builder.AppendLine("        {");
        builder.AppendLine("            items.Add(Map(item));");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine($"        return new EconomicPage<{resource.Entity}>(items, request.PageIndex, request.PageSize);");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine($"    private static {resource.Entity} Map(Generated.{resource.Entity} source) => new()");
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

    private static void AppendClientProperties(StringBuilder builder, IReadOnlyList<FacadeResource> resources)
    {
        builder.AppendLine();
        builder.AppendLine("namespace EConomic");
        builder.AppendLine("{");
        builder.AppendLine("    using EConomic.Rest;");
        builder.AppendLine();
        builder.AppendLine("    public sealed partial class EconomicClient");
        builder.AppendLine("    {");

        foreach (var resource in resources)
        {
            // A write-enabled entity gets its resource type, which composes the same query and adds
            // the write methods. The query itself never gains them: a filtered query is not a
            // sensible place to create from.
            if (SchemaRegistry.WriteEnabledEntities.Contains(resource.Entity))
            {
                builder.AppendLine($"        /// <summary><c>{resource.Path}</c>, as a queryable and writable resource.</summary>");
                // The resource takes the HttpClient rather than the generated client: DELETE has no
                // schema and therefore no generated method, so it is issued directly.
                builder.AppendLine($"        public {resource.Entity}Resource {resource.PropertyName} =>");
                builder.AppendLine("            new(HttpClient);");
                builder.AppendLine();
                continue;
            }

            builder.AppendLine($"        /// <summary><c>{resource.Path}</c>, as a composable query.</summary>");
            builder.AppendLine(
                $"        public EconomicQuery<{resource.Entity}, {resource.Entity}Filter, {resource.Entity}Sort> {resource.PropertyName} =>");
            builder.AppendLine(
                $"            new(new {resource.Entity}PageSource(new Generated.{resource.ClientClass}(HttpClient)));");
            builder.AppendLine();
        }

        builder.Length -= Environment.NewLine.Length;
        builder.AppendLine("    }");
        builder.AppendLine("}");
    }

    private static void AppendResource(
        StringBuilder builder,
        FacadeResource resource,
        FacadeWrite write,
        IReadOnlyList<FacadeProperty> readProperties,
        JsonObject schemas,
        IReadOnlyList<FacadeNested> children,
        IList<string> skipped)
    {
        var entity = resource.Entity;
        var collection = resource.Path.Trim('/');

        // Both writes return the whole resource — WriteResponseCorrector points their responses at
        // the read entity — so the response always carries the identifier and `self`, whatever the
        // request schema happens to describe.
        var createPayload = write.CreateBody is null ? null : schemas[write.CreateBody] as JsonObject;
        var canCreate = createPayload is not null;
        var updatePayload = write.UpdateBody is null ? null : schemas[write.UpdateBody] as JsonObject;

        List<WriteProperty>? createProperties = null;
        List<WriteProperty>? updateProperties = null;

        if (canCreate)
        {
            // The key stays on the create model: some resources assign it (customers get the next
            // free number) and others require the caller to choose it (customer groups reject a
            // create without one, products are keyed by a caller-chosen string). Whether it is
            // required follows the schema, which distinguishes the two correctly.
            createProperties = MapWriteProperties(
                createPayload!, write.CreateBody!, schemas, write.KeyProperty, includeKey: true, skipped);
            AppendWriteModel(builder, $"{entity}Create", entity, "create", createProperties);
        }

        if (updatePayload is not null)
        {
            updateProperties = MapWriteProperties(
                updatePayload, write.UpdateBody!, schemas, write.KeyProperty, includeKey: false, skipped);
            AppendWriteModel(builder, $"{entity}Update", entity, "replace", updateProperties);
        }

        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine($"/// The <c>{resource.Path}</c> resource: a composable query, plus the writes e-conomic supports.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine("/// <remarks>");
        builder.AppendLine("/// The query-building methods each return a query and composition continues from there.");
        builder.AppendLine("/// Writes live on the resource rather than on the query: a query describes a filtered view,");
        builder.AppendLine("/// which is not a meaningful thing to create from.");
        builder.AppendLine("/// </remarks>");
        builder.AppendLine($"public sealed class {entity}Resource");
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
            builder.AppendLine($"            () => _client.{write.CreateMethod}(ToGenerated(item), cancellationToken),");
            builder.AppendLine($"            \"POST {resource.Path}\").ConfigureAwait(false);");
            builder.AppendLine();
            builder.AppendLine("        return FromGenerated(response);");
            builder.AppendLine("    }");
        }

        if (updateProperties is not null)
        {
            builder.AppendLine();
            builder.AppendLine($"    /// <summary>Replaces an existing {Camel(entity)}.</summary>");
            builder.AppendLine($"    /// <param name=\"{write.KeyName}\">The item to replace.</param>");
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

        if (write.SupportsDelete)
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
            AppendToGenerated(builder, write.CreateBody!, $"{entity}Create", createProperties!, keyProperty: null, keyType: null);
        }

        if (updateProperties is not null)
        {
            var updateDeclaresKey =
                updatePayload!["properties"]?.AsObject().Any(p => Pascal(p.Key) == write.KeyProperty) ?? false;

            AppendToGenerated(
                builder, write.UpdateBody!, $"{entity}Update", updateProperties,
                write.KeyProperty, write.KeyType, updateDeclaresKey);
        }

        foreach (var child in children)
        {
            builder.AppendLine();
            builder.AppendLine($"    /// <summary>The <c>{child.Collection}</c> belonging to one {Camel(entity)}.</summary>");
            builder.AppendLine($"    /// <param name=\"{child.ParentKeyName}\">The owning {Camel(entity)}.</param>");
            builder.AppendLine($"    /// <returns>The nested collection, scoped to that {Camel(entity)}.</returns>");
            builder.AppendLine(
                $"    public {child.Entity}Resource {child.AccessorName}({child.ParentKeyType} {child.ParentKeyName}) =>");
            builder.AppendLine($"        new(_httpClient, {child.ParentKeyName});");
        }

        // One mapper serves both writes: each returns the whole resource, so the response is the
        // same generated type the read path already maps.
        builder.AppendLine();
        builder.AppendLine($"    private static {entity} FromGenerated(Generated.{entity} source) => new()");
        builder.AppendLine("    {");

        foreach (var property in readProperties)
        {
            builder.AppendLine($"        {property.Name} = {property.Mapping},");
        }

        builder.AppendLine("    };");
        builder.AppendLine();
        builder.AppendLine("    private static EconomicReference? Reference(int? number, System.Uri? self) =>");
        builder.AppendLine("        number is { } value ? new EconomicReference(value, self) : null;");
        builder.AppendLine("}");
    }

    private static void AppendNested(
        StringBuilder builder,
        FacadeNested nested,
        JsonObject schemas,
        IList<string> skipped)
    {
        var entity = nested.Entity;
        var write = nested.Write!;
        var properties = MapProperties(schemas[entity]!.AsObject(), entity, schemas, skipped);

        // The model, mirroring a top-level one.
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine($"/// A resource from <c>{nested.Path}</c>.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine($"public sealed record {entity}");
        builder.AppendLine("{");

        foreach (var property in properties)
        {
            var modifier = !property.PublicType.EndsWith('?')
                && property.PublicType is "string" or "System.Uri" or "EconomicReference"
                    ? "required "
                    : string.Empty;

            builder.AppendLine($"    /// <summary>The <c>{Camel(property.Name)}</c> field.</summary>");
            builder.AppendLine($"    public {modifier}{property.PublicType} {property.Name} {{ get; init; }}");
            builder.AppendLine();
        }

        builder.Length -= Environment.NewLine.Length;
        builder.AppendLine("}");

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
        builder.AppendLine($"    private static {entity} Map(Generated.{entity} source) => new()");
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

        var createPayload = schemas[write.CreateBody!] as JsonObject;
        var updatePayload = write.UpdateBody is null ? null : schemas[write.UpdateBody] as JsonObject;

        var createProperties = MapWriteProperties(
            createPayload!, write.CreateBody!, schemas, write.KeyProperty, includeKey: true, skipped);
        AppendWriteModel(builder, $"{entity}Create", entity, "create", createProperties);

        List<WriteProperty>? updateProperties = null;
        if (updatePayload is not null)
        {
            updateProperties = MapWriteProperties(
                updatePayload, write.UpdateBody!, schemas, write.KeyProperty, includeKey: false, skipped);
            AppendWriteModel(builder, $"{entity}Update", entity, "replace", updateProperties);
        }

        // The resource itself, scoped to one parent.
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine($"/// The <c>{nested.Path}</c> collection, scoped to one {Camel(nested.ParentEntity)}.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine($"public sealed class {entity}Resource");
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
        builder.AppendLine($"            () => _client.{write.CreateMethod}(_{nested.ParentKeyName}, ToGenerated(item), cancellationToken),");
        builder.AppendLine($"            \"POST {nested.Path}\").ConfigureAwait(false);");
        builder.AppendLine();
        builder.AppendLine("        return FromGenerated(response);");
        builder.AppendLine("    }");

        if (updateProperties is not null)
        {
            builder.AppendLine();
            builder.AppendLine($"    /// <summary>Replaces an existing {Camel(entity)}.</summary>");
            builder.AppendLine($"    /// <param name=\"{write.KeyName}\">The item to replace.</param>");
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

        if (write.SupportsDelete)
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

        AppendToGenerated(builder, write.CreateBody!, $"{entity}Create", createProperties, keyProperty: null, keyType: null);

        if (updateProperties is not null)
        {
            var declaresKey = updatePayload!["properties"]?.AsObject().Any(p => Pascal(p.Key) == write.KeyProperty) ?? false;
            AppendToGenerated(
                builder, write.UpdateBody!, $"{entity}Update", updateProperties,
                write.KeyProperty, write.KeyType, declaresKey);
        }

        builder.AppendLine();
        builder.AppendLine($"    private static {entity} FromGenerated(Generated.{entity} source) => new()");
        builder.AppendLine("    {");

        foreach (var property in properties)
        {
            builder.AppendLine($"        {property.Name} = {property.Mapping},");
        }

        builder.AppendLine("    };");
        builder.AppendLine();
        builder.AppendLine("    private static EconomicReference? Reference(int? number, System.Uri? self) =>");
        builder.AppendLine("        number is { } value ? new EconomicReference(value, self) : null;");
        builder.AppendLine("}");
    }

    /// <summary>Finds the collections that hang off a write-enabled resource.</summary>
    /// <param name="document">The merged OpenAPI document.</param>
    /// <param name="resources">The top-level resources already discovered.</param>
    /// <returns>The nested collections, in path order.</returns>
    /// <remarks>
    /// Only collections whose parent is itself exposed as a resource are emitted, because the
    /// accessor has to hang off something. That defers the journal vouchers, whose parent is not
    /// write-enabled and whose item is addressed by a composite key.
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
                || !byCollection.TryGetValue(segments[0], out var parent)
                || !SchemaRegistry.WriteEnabledEntities.Contains(parent.Entity))
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

            // Nothing to add for a read-only nested collection: the parent's own query already
            // reaches its contents through the link on the model.
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
                write));
        }

        return nested;
    }

    private static FacadeWrite? NestedWriteFor(JsonObject paths, string path, string entity)
    {
        if (paths[path]?["post"] is not JsonObject post
            || Reference(post["requestBody"]?["content"]?["application/json"]?["schema"]) is not { } createBody)
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

        var keyProperty = $"{entity}Number";

        return new FacadeWrite(
            createBody,
            MethodName(post),
            updateBody,
            updateMethod,
            Camel(keyProperty),
            keyType,
            keyProperty,
            SchemaRegistry.DeletableEntities.Contains(entity));
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
        var keyProperty = $"{resource.Entity}Number";
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
            if (!path.StartsWith(resource.Path + "/", StringComparison.Ordinal)
                || path.Count(c => c == '/') != 2
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
    private static List<WriteProperty> MapWriteProperties(
        JsonObject payload,
        string payloadName,
        JsonObject schemas,
        string keyProperty,
        bool includeKey,
        IList<string> skipped)
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
        builder.AppendLine($"public sealed record {typeName}");
        builder.AppendLine("{");

        foreach (var property in properties.OrderByDescending(p => p.IsRequired).ThenBy(p => p.Name, StringComparer.Ordinal))
        {
            var modifier = property.IsRequired ? "required " : string.Empty;
            builder.AppendLine($"    /// <summary>The <c>{Camel(property.GeneratedName)}</c> field.</summary>");
            builder.AppendLine($"    public {modifier}{property.PublicType} {property.Name} {{ get; init; }}");
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
        builder.AppendLine($"        var target = new Generated.{payloadType}");
        builder.AppendLine("        {");

        // The key goes in the body only when the payload declares it; several update schemas
        // identify the resource solely by the path.
        if (keyProperty is not null && assignKey)
        {
            builder.AppendLine($"            {keyProperty} = {Camel(keyProperty)},");
        }

        foreach (var property in properties)
        {
            var line = property switch
            {
                // Assigned after the initializer, where the target property can be read for type
                // inference.
                { IsEnum: true } => null,
                { ReferenceNumber: { } number, IsRequired: true } =>
                    $"            {property.GeneratedName} = new() {{ {number} = source.{property.Name} }},",
                { ReferenceNumber: not null } => null,
                { PublicType: "bool" } => $"            {property.GeneratedName} = source.{property.Name},",
                { PublicType: "string" } => $"            {property.GeneratedName} = source.{property.Name},",
                { PublicType: "string?" } => $"            {property.GeneratedName} = source.{property.Name}!,",
                { PublicType: "decimal" } => $"            {property.GeneratedName} = (double)source.{property.Name},",
                { IsRequired: true } => $"            {property.GeneratedName} = source.{property.Name},",
                _ => null,
            };

            if (line is not null)
            {
                builder.AppendLine(line);
            }
        }

        builder.AppendLine("        };");

        // An enum is set afterwards so the target property can be passed for type inference: the
        // generated enum type is internal and named by NSwag, so it cannot be written out here.
        foreach (var property in properties.Where(p => p.IsEnum))
        {
            builder.AppendLine();
            builder.AppendLine(
                $"        target.{property.GeneratedName} = "
                + $"FacadeTransport.ParseEnum(source.{property.Name}, target.{property.GeneratedName});");
        }

        // Optional values are assigned only when supplied: the generated properties are
        // non-nullable, so there is no way to express "absent" in the initializer.
        foreach (var property in properties.Where(p => !p.IsEnum && !p.IsRequired && p.PublicType is not ("bool" or "string?" or "string")))
        {
            builder.AppendLine();
            builder.AppendLine($"        if (source.{property.Name} is {{ }} {Camel(property.Name)})");
            builder.AppendLine("        {");

            builder.AppendLine(property.ReferenceNumber is { } number
                ? $"            target.{property.GeneratedName} = new() {{ {number} = {Camel(property.Name)} }};"
                : property.PublicType == "decimal?"
                    ? $"            target.{property.GeneratedName} = (double){Camel(property.Name)};"
                    : $"            target.{property.GeneratedName} = {Camel(property.Name)};");

            builder.AppendLine("        }");
        }

        builder.AppendLine();
        builder.AppendLine("        return target;");
        builder.AppendLine("    }");
    }

    private static void AppendFromGenerated(
        StringBuilder builder,
        string methodName,
        string payloadType,
        string entity,
        IReadOnlyList<FacadeProperty> readProperties,
        JsonObject payload,
        JsonObject schemas,
        string keyProperty,
        string keyType,
        bool keyFromParameter)
    {
        var available = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, node) in payload["properties"]?.AsObject() ?? [])
        {
            if (node is JsonObject property && PublicKind(Resolve(property, schemas)) is { } kind)
            {
                available[Pascal(name)] = kind;
            }
        }

        builder.AppendLine();
        builder.AppendLine(keyFromParameter
            ? $"    private {entity} {methodName}(Generated.{payloadType} source, {keyType} {Camel(keyProperty)}) => new()"
            : $"    private {entity} {methodName}(Generated.{payloadType} source) => new()");
        builder.AppendLine("    {");

        foreach (var property in readProperties)
        {
            // A property present in both schemas can still be typed differently in each — the read
            // and write schemas are written independently — so the read mapping is only reused when
            // the write payload actually carries the same kind of value.
            var value = property.Name switch
            {
                "Self" when keyFromParameter => $"SelfFor({Camel(keyProperty)})",
                "Self" => $"SelfFor(source.{keyProperty})",
                _ when property.Name == keyProperty && keyFromParameter => Camel(keyProperty),
                _ when available.TryGetValue(property.Name, out var kind)
                    && kind == Required(property.PublicType) => property.Mapping,
                _ => Absent(property.PublicType),
            };

            builder.AppendLine($"        {property.Name} = {value},");
        }

        builder.AppendLine("    };");
    }

    /// <summary>The public type a schema property maps to, without its nullable marker.</summary>
    private static string? PublicKind(JsonObject resolved)
    {
        if (resolved["enum"] is JsonArray)
        {
            return "string";
        }

        var kind = (resolved["type"]?.GetValue<string>(), resolved["format"]?.GetValue<string>()) switch
        {
            ("string", "uri") => "System.Uri",
            ("string", "date") => "DateOnly",
            ("string", "date-time") => "DateTimeOffset",
            ("string", _) => "string",
            ("integer", _) => "int",
            ("number", _) => "decimal",
            ("boolean", _) => "bool",
            _ => null,
        };

        return kind ?? (ReferenceNumber(resolved) is not null ? "EconomicReference" : null);
    }

    /// <summary>
    /// The value for a property the write response does not carry. e-conomic's write schemas
    /// describe far less than its read schemas, so several properties simply are not returned.
    /// </summary>
    private static string Absent(string publicType) => publicType switch
    {
        "string" => "string.Empty",
        "bool" => "false",
        "int" => "0",
        "decimal" => "0m",
        _ when publicType.EndsWith('?') => "null",
        _ => "default!",
    };

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
