using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EConomic.SpecConverter;

/// <summary>One property of a generated class, as NSwag emitted it.</summary>
/// <param name="JsonName">The name on the wire.</param>
/// <param name="Name">The C# property name.</param>
/// <param name="Type">The C# type, verbatim.</param>
public sealed record GeneratedProperty(string JsonName, string Name, string Type);

/// <summary>
/// Generates the public facade for one OpenAPI service.
/// </summary>
/// <remarks>
/// <para>
/// The models mirror the generated classes rather than being derived from the specification a
/// second time: the property names and types are read back out of NSwag's own output, so the
/// mapping is a straight copy and no naming rule has to be guessed at. Only the filter and sort
/// surfaces come from the specification, because only it publishes the operator lists.
/// </para>
/// <para>
/// These services are far more regular than the legacy API — every collection publishes the same
/// six operations over a flat entity — which is why this generator is a fraction of the size of the
/// legacy one and needs none of its curated name tables.
/// </para>
/// </remarks>
public static partial class OpenFacadeGenerator
{
    private const string RefPrefix = "#/components/schemas/";

    /// <summary>
    /// The name each service contributes to the types it publishes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The services overlap heavily — customers and suppliers both publish a <c>Contact</c>, and
    /// <c>Group</c>, <c>Employee</c> and <c>EmployeeGroup</c> recur — while the public surface is
    /// one flat namespace, matching the legacy one. So every type carries its service, dropped only
    /// where it would stutter: <c>Contact</c> becomes <c>CustomerContact</c> and <c>Customer</c>
    /// stays <c>Customer</c>.
    /// </para>
    /// <para>
    /// Qualifying always, rather than only on collision, is what makes adding a service additive: a
    /// name is decided by its own service and nothing else, so a service landing later cannot rename
    /// a type that already compiles against an earlier one.
    /// </para>
    /// <para>
    /// An explicit table rather than a singulariser, and unknown services fail the run. Guessing
    /// here would mint a public type name from a rule nobody checked, and public names are the one
    /// thing this package cannot take back.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, string> ServiceNames { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Accounts"] = "Account",
            ["Customers"] = "Customer",
            ["Products"] = "Product",
            ["Suppliers"] = "Supplier",
        };

    /// <summary>
    /// Properties these services publish as filterable that the server will not filter on, keyed by
    /// the public type name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The legacy surface needed a list like this because its metadata over-reports. These services
    /// publish a per-property operator list, which is better information — and still not the server.
    /// Both entries below are marked filterable and answer <c>500</c>, on every operator, on the
    /// demo agreement as well as a real one.
    /// </para>
    /// <para>
    /// Unlike the legacy API there is no list to compare against: these services name the offending
    /// property and stop, publishing no <c>allowedFilteringFields</c>. So every entry here was found
    /// by <c>FilterSurfaceTests</c> sending each clause the surface offers at a live agreement, one
    /// per request. An entry that stops being needed fails the run rather than lingering.
    /// </para>
    /// </remarks>
    public static IReadOnlySet<string> UnfilterableFields { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // Every operator, and the sort too.
        "Account.assetGroupNumber",

        // $eq: and $ne: both crash, while $eq:$null: answers normally and the neighbouring
        // isUnitMandatory is fine throughout. Nothing usable is left, so the property goes.
        "Account.isDepartmentMandatory",
    };

    /// <summary>
    /// Properties these services publish as sortable that the server will not sort on.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="UnfilterableFields"/> because the two flags are independent and so
    /// are the failures: <c>isDepartmentMandatory</c> cannot be filtered on and was never offered
    /// for sorting, and a property could as easily fail the other way round. One list would force a
    /// working filter to be dropped to fix a broken sort.
    /// </remarks>
    public static IReadOnlySet<string> UnsortableFields { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // A 500 in both directions, ascending and descending.
        "Account.assetGroupNumber",
    };

    /// <summary>Generates the facade for one service.</summary>
    /// <param name="document">The prepared service specification.</param>
    /// <param name="generatedSource">NSwag's output for that service.</param>
    /// <param name="namespaceName">Namespace to emit into.</param>
    /// <param name="service">The service's file name, e.g. <c>Customers</c>.</param>
    /// <returns>The C# source.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The service's shape is not one this understands.</exception>
    public static string Generate(
        JsonObject document,
        string generatedSource,
        string namespaceName,
        string service)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(generatedSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        if (!ServiceNames.TryGetValue(service, out var qualifier))
        {
            throw new InvalidOperationException(
                $"No name is recorded for the '{service}' service. Add it to "
                + $"{nameof(OpenFacadeGenerator)}.{nameof(ServiceNames)} — every public type this "
                + "emits is prefixed with it, so it cannot be guessed.");
        }

        var schemas = document["components"]?["schemas"]?.AsObject()
            ?? throw new InvalidOperationException("The document declares no schemas.");
        var paths = document["paths"]?.AsObject()
            ?? throw new InvalidOperationException("The document declares no paths.");

        var emitted = Classes(generatedSource);
        var builder = new StringBuilder();

        builder.AppendLine("// <auto-generated>");
        builder.AppendLine("//     Generated by tools/EConomic.SpecConverter (open-facade).");
        builder.AppendLine("//     Regenerate after a spec refresh; do not edit by hand.");
        builder.AppendLine("// </auto-generated>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine("using EConomic.Querying;");
        // Aliased as Raw, not Generated: inside namespace EConomic.Open the identifier
        // `Generated` binds to the nested EConomic.Open.Generated namespace, which beats a
        // file-level using alias of the same name and hides the per-service one.
        builder.AppendLine($"using Raw = EConomic.Open.Generated.{service};");
        builder.AppendLine();
        builder.AppendLine($"namespace {namespaceName};");

        var resources = Collections(paths, schemas).Select(r => r.QualifiedBy(qualifier)).ToList();
        if (resources.Count == 0)
        {
            throw new InvalidOperationException("No cursor collections found; this service has an unfamiliar shape.");
        }

        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var resource in resources)
        {
            if (!emitted.TryGetValue(resource.Entity, out var properties))
            {
                throw new InvalidOperationException($"NSwag emitted no class for '{resource.Entity}'.");
            }

            AppendModel(builder, resource, properties);
            AppendSurfaces(builder, resource, schemas, properties, used);
            AppendSource(builder, resource, properties);
            AppendResource(builder, resource);
        }

        // The same discipline the legacy generator follows: an entry describing a property this
        // service no longer offers is describing something that has changed, and saying so at
        // generation time is the only moment anyone will notice.
        var names = resources.Select(r => r.PublicName).ToHashSet(StringComparer.Ordinal);
        var stale = UnfilterableFields
            .Concat(UnsortableFields)
            .Distinct(StringComparer.Ordinal)
            .Where(f => !used.Contains(f) && names.Contains(f[..f.IndexOf('.', StringComparison.Ordinal)]))
            .OrderBy(f => f, StringComparer.Ordinal)
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

    /// <summary>The collections a service exposes, in name order.</summary>
    /// <param name="paths">The document's paths.</param>
    /// <param name="schemas">The document's schemas.</param>
    /// <returns>One entry per collection.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IEnumerable<OpenResource> Collections(JsonObject paths, JsonObject schemas)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(schemas);

        var found = new List<OpenResource>();
        var claimedPaged = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (path, item) in paths)
        {
            // A cursor listing is what marks a collection: /Customers returns items plus a cursor,
            // while /Customers/paged and /Customers/count hang off it.
            if (item?["get"] is not JsonObject get
                || Reference(get["responses"]?["200"]?["content"]?["application/json"]?["schema"]) is not { } envelope
                || !envelope.EndsWith("CursorResults", StringComparison.Ordinal)
                // `items` is the array property on the envelope; its own `items` is the element.
                || Reference(schemas[envelope]?["properties"]?["items"]?["items"]) is not { } entity)
            {
                continue;
            }

            var tag = get["tags"]?.AsArray().FirstOrDefault()?.GetValue<string>() ?? entity;

            // A collection can itself sit under a parent — /pricegroups/{priceGroupNumber}/specialprices
            // — in which case every one of its calls needs that identifier.
            var parent = Parameters(get)
                .FirstOrDefault(p => path.Contains($"{{{p.Name}}}", StringComparison.Ordinal));

            // The item path is the collection plus exactly one templated segment. Matching on shape
            // rather than on the literal {number} is what finds /products/{productNumber}, whose
            // identifier is a string, and keeps /pricegroups/{n}/specialprices from being mistaken
            // for a price group's item path.
            var itemPath = paths
                .Where(p => ItemPath(path, p.Key))
                .Select(p => (Path: p.Key, Item: p.Value as JsonObject))
                .FirstOrDefault();

            var byId = itemPath.Item;
            var key = byId is null
                ? null
                : Parameters(byId["get"] ?? byId["delete"])
                    .FirstOrDefault(p => !path.Contains($"{{{p.Name}}}", StringComparison.Ordinal));

            var pagedPath = paths.ContainsKey($"{path}/paged")
                ? $"{path}/paged"
                : Elsewhere(paths, tag, claimedPaged);

            if (pagedPath is not null)
            {
                claimedPaged.Add(pagedPath);
            }

            found.Add(new OpenResource(
                Entity: entity,
                Path: path,
                Client: $"{tag}Client",
                Cursor: OperationId(get),
                Paged: OperationId(paths[pagedPath ?? $"{path}/paged"]?["get"]),
                PagedPath: pagedPath,
                Count: OperationId(paths[$"{path}/count"]?["get"]),
                Get: OperationId(byId?["get"]),
                Create: OperationId(item?["post"]),
                // Several services update through the collection, taking the identifier from the
                // body, and others through the item. Both shapes appear in one service.
                Update: OperationId(item?["put"]) ?? OperationId(byId?["put"]),
                UpdateTakesId: item?["put"] is null && byId?["put"] is not null,
                Delete: OperationId(byId?["delete"]),
                CreatedProperty: CreatedProperty(item?["post"], schemas),
                Parent: parent,
                Key: key));
        }

        return found.OrderBy(r => r.Entity, StringComparer.Ordinal);
    }

    /// <summary>Whether <paramref name="candidate"/> is the item path of <paramref name="collection"/>.</summary>
    private static bool ItemPath(string collection, string candidate) =>
        candidate.Length > collection.Length + 3
        && candidate.StartsWith($"{collection}/{{", StringComparison.Ordinal)
        && candidate.EndsWith('}')
        && candidate.IndexOf('/', collection.Length + 1) < 0;

    /// <summary>
    /// A paged listing that does not sit beside its collection.
    /// </summary>
    /// <remarks>
    /// The products service reaches the paged listing of <c>/products</c> through
    /// <c>/productspaged/paged</c>. Rather than encode that one path, this looks for a paged
    /// listing carrying the same tag that no other collection has claimed — and the caller reports
    /// what it found, so an odd match is seen rather than assumed.
    /// </remarks>
    private static string? Elsewhere(JsonObject paths, string tag, HashSet<string> claimed) =>
        paths
            .Where(p => p.Key.EndsWith("/paged", StringComparison.Ordinal)
                && !claimed.Contains(p.Key)
                && p.Value?["get"]?["tags"]?.AsArray().FirstOrDefault()?.GetValue<string>() == tag)
            .Select(p => p.Key)
            .OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>The path parameters an operation declares, in the order the specification lists them.</summary>
    private static IEnumerable<OpenParameter> Parameters(JsonNode? operation) =>
        operation?["parameters"]?.AsArray()
            .OfType<JsonObject>()
            .Where(p => p["in"]?.GetValue<string>() == "path")
            .Select(p => new OpenParameter(
                p["name"]!.GetValue<string>(),
                p["schema"]?["type"]?.GetValue<string>() == "integer" ? "int" : "string"))
        ?? [];

    /// <summary>Every class NSwag emitted, with its properties.</summary>
    /// <param name="generatedSource">NSwag's output.</param>
    /// <returns>Properties by class name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="generatedSource"/> is <see langword="null"/>.</exception>
    public static Dictionary<string, IReadOnlyList<GeneratedProperty>> Classes(string generatedSource)
    {
        ArgumentNullException.ThrowIfNull(generatedSource);

        var classes = new Dictionary<string, IReadOnlyList<GeneratedProperty>>(StringComparer.Ordinal);

        foreach (Match type in ClassPattern().Matches(generatedSource))
        {
            var properties = PropertyPattern().Matches(type.Groups["body"].Value)
                .Select(m => new GeneratedProperty(
                    m.Groups["json"].Value,
                    m.Groups["name"].Value,
                    m.Groups["type"].Value.Trim()))
                .ToList();

            classes[type.Groups["name"].Value] = properties;
        }

        return classes;
    }

    private static void AppendModel(StringBuilder builder, OpenResource resource, IReadOnlyList<GeneratedProperty> properties)
    {
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine($"/// A resource from <c>{resource.Path}</c>.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine("/// <remarks>");
        builder.AppendLine("/// The same shape is used to read, create and update: these services describe a resource once");
        builder.AppendLine("/// and accept it back unchanged. Properties the server maintains are simply left unset.");
        builder.AppendLine("/// </remarks>");
        builder.AppendLine($"public sealed record {resource.PublicName}");
        builder.AppendLine("{");

        foreach (var property in properties)
        {
            // A non-nullable property is one the specification marked required — that is why NSwag
            // emitted it non-nullable — so the caller has to supply it. Without `required` an
            // untouched one is sent as its default, and a required identifier defaulting to 0 is
            // rejected outright: the same failure the legacy write models use `required` to prevent.
            var isRequired = !property.Type.EndsWith('?');

            builder.AppendLine($"    /// <summary>The <c>{property.JsonName}</c> field.</summary>");
            builder.AppendLine(
                $"    public {(isRequired ? "required " : string.Empty)}{property.Type} {property.Name} {{ get; init; }}");
            builder.AppendLine();
        }

        builder.Length -= Environment.NewLine.Length;
        builder.AppendLine("}");
    }

    private static void AppendSurfaces(
        StringBuilder builder,
        OpenResource resource,
        JsonObject schemas,
        IReadOnlyList<GeneratedProperty> properties,
        HashSet<string> used)
    {
        var declared = schemas[resource.Entity]?["properties"]?.AsObject();
        var filterable = new List<(GeneratedProperty Property, string Field)>();
        var sortable = new List<GeneratedProperty>();

        foreach (var property in properties)
        {
            var declaration = declared?[property.JsonName] as JsonObject;
            var operators = declaration?["x-filterable"]?.GetValue<string>();

            var key = $"{resource.PublicName}.{property.JsonName}";

            if (operators is not null and not "not filterable")
            {
                if (UnfilterableFields.Contains(key))
                {
                    used.Add(key);
                }
                else
                {
                    filterable.Add((property, FieldType(operators, property.Type)));
                }
            }

            if (declaration?["x-sortable"]?.GetValue<bool>() == true)
            {
                if (UnsortableFields.Contains(key))
                {
                    used.Add(key);
                }
                else
                {
                    sortable.Add(property);
                }
            }
        }

        AppendSurface(builder, $"{resource.PublicName}Filter", resource,
            filterable.Select(f => (f.Property, f.Field)).ToList());
        AppendSurface(builder, $"{resource.PublicName}Sort", resource,
            sortable.Select(p => (p, "EconomicSortField")).ToList());
    }

    private static void AppendSurface(
        StringBuilder builder,
        string name,
        OpenResource resource,
        IReadOnlyList<(GeneratedProperty Property, string Field)> fields)
    {
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine($"/// What <c>{resource.Path}</c> can be {(name.EndsWith("Filter", StringComparison.Ordinal) ? "filtered" : "sorted")} on.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine("/// <remarks>");
        builder.AppendLine("/// Each property is typed to the operators the service publishes for it, narrowed to those the");
        builder.AppendLine("/// value's type can carry: e-conomic restricts <c>$in:</c> to numbers and <c>$like:</c> to text,");
        builder.AppendLine("/// whatever an individual property claims. Anything absent here is not filterable at all, which");
        builder.AppendLine("/// is most of them.");
        builder.AppendLine("/// </remarks>");
        builder.AppendLine($"public sealed class {name}");
        builder.AppendLine("{");

        if (fields.Count == 0)
        {
            builder.AppendLine("}");
            return;
        }

        foreach (var (property, field) in fields)
        {
            builder.AppendLine($"    /// <summary>Maps to <c>{property.JsonName}</c>.</summary>");
            builder.AppendLine($"    [EconomicField(\"{property.JsonName}\")]");
            builder.AppendLine($"    public {field} {property.Name} {{ get; }} = null!;");
            builder.AppendLine();
        }

        builder.Length -= Environment.NewLine.Length;
        builder.AppendLine("}");
    }

    private static void AppendSource(StringBuilder builder, OpenResource resource, IReadOnlyList<GeneratedProperty> properties)
    {
        var entity = resource.PublicName;
        var generated = resource.Entity;
        var parent = resource.Parent;
        var scope = parent is null ? string.Empty : $", {parent.Type} {parent.Name}";
        var scoped = parent is null ? string.Empty : $"{parent.Name}: {parent.Name}, ";

        builder.AppendLine();
        builder.AppendLine($"/// <summary>Fetches <c>{resource.Path}</c> and maps it to <see cref=\"{entity}\"/>.</summary>");
        builder.AppendLine($"internal sealed class {entity}Source(Raw.{resource.Client} client{scope})");
        builder.AppendLine($"    : IEconomicOpenSource<{entity}>");
        builder.AppendLine("{");

        builder.AppendLine($"    public async Task<EconomicCursorPage<{entity}>> GetCursorPageAsync(");
        builder.AppendLine("        string? cursor, string? filter, CancellationToken cancellationToken)");
        builder.AppendLine("    {");
        builder.AppendLine("        var response = await OpenTransport.SendAsync(");
        builder.AppendLine(
            $"            () => client.{resource.Cursor}Async({scoped}cursor: cursor, filter: filter, cancellationToken: cancellationToken),");
        builder.AppendLine($"            \"GET {resource.Path}\").ConfigureAwait(false);");
        builder.AppendLine();
        builder.AppendLine($"        var items = new List<{entity}>(response.Items?.Count ?? 0);");
        builder.AppendLine("        foreach (var item in response.Items ?? [])");
        builder.AppendLine("        {");
        builder.AppendLine("            items.Add(Map(item));");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine($"        return new EconomicCursorPage<{entity}>(items, response.Cursor);");
        builder.AppendLine("    }");
        builder.AppendLine();

        builder.AppendLine($"    public async Task<IReadOnlyList<{entity}>> GetPageAsync(");
        builder.AppendLine("        string? filter, string? sort, int pageSize, int skipPages, CancellationToken cancellationToken)");
        builder.AppendLine("    {");
        builder.AppendLine("        var response = await OpenTransport.SendAsync(");
        builder.AppendLine(
            $"            () => client.{resource.Paged}Async({scoped}filter: filter, sort: sort, pageSize: pageSize, skipPages: skipPages, cancellationToken: cancellationToken),");
        builder.AppendLine($"            \"GET {resource.PagedPath}\").ConfigureAwait(false);");
        builder.AppendLine();
        builder.AppendLine($"        var items = new List<{entity}>(response?.Count ?? 0);");
        builder.AppendLine($"        foreach (var item in response ?? Array.Empty<Raw.{generated}>())");
        builder.AppendLine("        {");
        builder.AppendLine("            items.Add(Map(item));");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        return items;");
        builder.AppendLine("    }");
        builder.AppendLine();

        if (resource.Count is null)
        {
            // The interface is the query's whole view of a collection, so the method has to exist.
            // The resource does not offer it, which is where a caller would reach for it, and the
            // message names the collection rather than leaving a bare NotSupportedException.
            builder.AppendLine("    public Task<int> CountAsync(string? filter, CancellationToken cancellationToken) =>");
            builder.AppendLine("        throw new NotSupportedException(");
            builder.AppendLine($"            \"e-conomic publishes no count endpoint for {resource.Path}, so this collection \"");
            builder.AppendLine("            + \"cannot be counted. Enumerate it, or count a collection that publishes one.\");");
        }
        else
        {
            builder.AppendLine("    public Task<int> CountAsync(string? filter, CancellationToken cancellationToken) =>");
            builder.AppendLine("        OpenTransport.SendAsync(");
            builder.AppendLine(
                $"            () => client.{resource.Count}Async({scoped}filter: filter, cancellationToken: cancellationToken),");
            builder.AppendLine($"            \"GET {resource.Path}/count\");");
        }

        builder.AppendLine();

        builder.AppendLine($"    internal static {entity} Map(Raw.{generated} source) => new()");
        builder.AppendLine("    {");
        foreach (var property in properties)
        {
            builder.AppendLine($"        {property.Name} = source.{property.Name},");
        }

        builder.AppendLine("    };");
        builder.AppendLine();

        builder.AppendLine($"    internal static Raw.{generated} ToGenerated({entity} source) => new()");
        builder.AppendLine("    {");
        foreach (var property in properties)
        {
            builder.AppendLine($"        {property.Name} = source.{property.Name},");
        }

        builder.AppendLine("    };");
        builder.AppendLine("}");
    }

    private static void AppendResource(StringBuilder builder, OpenResource resource)
    {
        var entity = resource.PublicName;
        var parent = resource.Parent;
        var key = resource.Identifier;
        var scope = parent is null ? string.Empty : $", {parent.Type} {parent.Name}";
        var scoped = parent is null ? string.Empty : $"{parent.Name}: _{parent.Name}, ";
        var query = $"EconomicOpenQuery<{entity}, {entity}Filter, {entity}Sort>";

        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine($"/// <c>{resource.Path}</c>, as a query and the writes the service supports.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine($"public sealed class {entity}Resource");
        builder.AppendLine("{");
        builder.AppendLine($"    private readonly Raw.{resource.Client} _client;");

        if (parent is not null)
        {
            builder.AppendLine($"    private readonly {parent.Type} _{parent.Name};");
        }

        builder.AppendLine();
        builder.AppendLine(
            $"    internal {entity}Resource(System.Net.Http.HttpClient httpClient, System.Uri baseAddress{scope})");
        builder.AppendLine("    {");
        builder.AppendLine($"        _client = new Raw.{resource.Client}(httpClient) {{ BaseUrl = baseAddress.ToString() }};");

        if (parent is not null)
        {
            builder.AppendLine($"        _{parent.Name} = {parent.Name};");
        }

        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>This collection as a composable query.</summary>");
        builder.AppendLine("    /// <returns>A query with no filter or ordering.</returns>");
        builder.AppendLine(
            $"    public {query} AsQuery() => new(new {entity}Source(_client{(parent is null ? string.Empty : $", _{parent.Name}")}));");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Restricts what is returned.</summary>");
        builder.AppendLine("    /// <param name=\"predicate\">A filter over the filterable properties.</param>");
        builder.AppendLine("    /// <returns>A query carrying the filter.</returns>");
        builder.AppendLine($"    public {query} Where(System.Linq.Expressions.Expression<System.Func<{entity}Filter, bool>> predicate) => AsQuery().Where(predicate);");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Restricts what is returned, using e-conomic's filter syntax directly.</summary>");
        builder.AppendLine("    /// <param name=\"filter\">A filter expression.</param>");
        builder.AppendLine("    /// <returns>A query carrying the filter.</returns>");
        builder.AppendLine($"    public {query} WhereRaw(string filter) => AsQuery().WhereRaw(filter);");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Orders ascending, which moves the query onto the paged endpoint.</summary>");
        builder.AppendLine("    /// <param name=\"selector\">The property to sort by.</param>");
        builder.AppendLine("    /// <returns>A query carrying the ordering.</returns>");
        builder.AppendLine($"    public {query} OrderBy(System.Linq.Expressions.Expression<System.Func<{entity}Sort, EconomicSortField>> selector) => AsQuery().OrderBy(selector);");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Orders descending, which moves the query onto the paged endpoint.</summary>");
        builder.AppendLine("    /// <param name=\"selector\">The property to sort by.</param>");
        builder.AppendLine("    /// <returns>A query carrying the ordering.</returns>");
        builder.AppendLine($"    public {query} OrderByDescending(System.Linq.Expressions.Expression<System.Func<{entity}Sort, EconomicSortField>> selector) => AsQuery().OrderByDescending(selector);");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Sets how many items are fetched per request.</summary>");
        builder.AppendLine("    /// <param name=\"pageSize\">Items per page, up to 1000.</param>");
        builder.AppendLine("    /// <returns>A query using that page size.</returns>");
        builder.AppendLine($"    public {query} WithPageSize(int pageSize) => AsQuery().WithPageSize(pageSize);");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Enumerates everything, following the cursor.</summary>");
        builder.AppendLine("    /// <param name=\"cancellationToken\">Cancels the enumeration.</param>");
        builder.AppendLine("    /// <returns>The items.</returns>");
        builder.AppendLine($"    public System.Collections.Generic.IAsyncEnumerable<{entity}> AsAsyncEnumerable(CancellationToken cancellationToken = default) => AsQuery().AsAsyncEnumerable(cancellationToken);");
        // Offered only where the service publishes a count. /products has none, and a method that
        // could only ever throw is worse than one that is not there to reach for.
        if (resource.Count is not null)
        {
            builder.AppendLine();
            builder.AppendLine("    /// <summary>Counts everything in the collection.</summary>");
            builder.AppendLine("    /// <param name=\"cancellationToken\">Cancels the request.</param>");
            builder.AppendLine("    /// <returns>The number of records.</returns>");
            builder.AppendLine("    public Task<int> CountAsync(CancellationToken cancellationToken = default) => AsQuery().CountAsync(cancellationToken);");
        }

        builder.AppendLine();
        builder.AppendLine("    /// <summary>Fetches one numbered page.</summary>");
        builder.AppendLine("    /// <param name=\"pageIndex\">Zero-based page index.</param>");
        builder.AppendLine("    /// <param name=\"cancellationToken\">Cancels the request.</param>");
        builder.AppendLine("    /// <returns>The page.</returns>");
        builder.AppendLine($"    public Task<IReadOnlyList<{entity}>> GetPageAsync(int pageIndex, CancellationToken cancellationToken = default) => AsQuery().GetPageAsync(pageIndex, cancellationToken);");

        if (resource.Get is not null)
        {
            builder.AppendLine();
            builder.AppendLine("    /// <summary>Fetches one record.</summary>");
            builder.AppendLine($"    /// <param name=\"{key.Name}\">Its identifier.</param>");
            builder.AppendLine("    /// <param name=\"cancellationToken\">Cancels the request.</param>");
            builder.AppendLine("    /// <returns>The record.</returns>");
            builder.AppendLine($"    public async Task<{entity}> GetAsync({key.Type} {key.Name}, CancellationToken cancellationToken = default)");
            builder.AppendLine("    {");
            builder.AppendLine("        var response = await OpenTransport.SendAsync(");
            builder.AppendLine($"            () => _client.{resource.Get}Async({scoped}{key.Name}: {key.Name}, cancellationToken: cancellationToken),");
            builder.AppendLine($"            \"GET {resource.Path}/{{{key.Name}}}\").ConfigureAwait(false);");
            builder.AppendLine();
            builder.AppendLine($"        return {entity}Source.Map(response);");
            builder.AppendLine("    }");
        }

        if (resource.Create is not null)
        {
            builder.AppendLine();
            builder.AppendLine("    /// <summary>Creates a record.</summary>");
            builder.AppendLine("    /// <param name=\"item\">The record to create, whose identifier you supply.</param>");
            builder.AppendLine("    /// <param name=\"cancellationToken\">Cancels the request.</param>");
            builder.AppendLine("    /// <returns>The identifier e-conomic stored it under.</returns>");
            builder.AppendLine("    /// <remarks>");
            builder.AppendLine("    /// The response carries the identifier and nothing else, so that is what this returns —");
            builder.AppendLine("    /// unlike the legacy REST API, whose creates answer with the whole resource. Read it back");
            builder.AppendLine("    /// with <c>GetAsync</c> if you need the stored record, which also gives you the");
            builder.AppendLine("    /// <c>objectVersion</c> an update requires.");
            builder.AppendLine("    /// </remarks>");
            builder.AppendLine("    /// <exception cref=\"System.ArgumentNullException\"><paramref name=\"item\"/> is <see langword=\"null\"/>.</exception>");
            builder.AppendLine($"    public async Task<{resource.CreatedProperty.Type}> CreateAsync({entity} item, CancellationToken cancellationToken = default)");
            builder.AppendLine("    {");
            builder.AppendLine("        System.ArgumentNullException.ThrowIfNull(item);");
            builder.AppendLine();
            builder.AppendLine("        var response = await OpenTransport.SendAsync(");
            builder.AppendLine($"            () => _client.{resource.Create}Async({scoped}body: {entity}Source.ToGenerated(item), cancellationToken: cancellationToken),");
            builder.AppendLine($"            \"POST {resource.Path}\").ConfigureAwait(false);");
            builder.AppendLine();
            builder.AppendLine($"        return response.{resource.CreatedProperty.Name};");
            builder.AppendLine("    }");
        }

        if (resource.Update is not null)
        {
            builder.AppendLine();
            builder.AppendLine("    /// <summary>Replaces a record.</summary>");
            if (resource.UpdateTakesId)
            {
                builder.AppendLine($"    /// <param name=\"{key.Name}\">The record to replace.</param>");
            }

            builder.AppendLine("    /// <param name=\"item\">The replacement state, carrying the <c>objectVersion</c> that was read.</param>");
            builder.AppendLine("    /// <param name=\"cancellationToken\">Cancels the request.</param>");
            builder.AppendLine("    /// <returns>A task that completes once the record is replaced.</returns>");
            builder.AppendLine("    /// <remarks>");
            builder.AppendLine("    /// Updates are read-modify-write on this surface. The <c>objectVersion</c> from the record");
            builder.AppendLine("    /// you read has to come back with it; a stale one, or none, is rejected with");
            builder.AppendLine("    /// <see cref=\"Exceptions.EconomicConcurrencyException\"/> and nothing is written.");
            builder.AppendLine("    /// </remarks>");
            builder.AppendLine("    /// <exception cref=\"System.ArgumentNullException\"><paramref name=\"item\"/> is <see langword=\"null\"/>.</exception>");
            builder.AppendLine("    /// <exception cref=\"Exceptions.EconomicConcurrencyException\">The record changed since it was read.</exception>");

            var parameters = resource.UpdateTakesId
                ? $"{key.Type} {key.Name}, {entity} item, CancellationToken cancellationToken = default"
                : $"{entity} item, CancellationToken cancellationToken = default";
            var arguments = resource.UpdateTakesId
                ? $"{scoped}{key.Name}: {key.Name}, body: {entity}Source.ToGenerated(item), cancellationToken: cancellationToken"
                : $"{scoped}body: {entity}Source.ToGenerated(item), cancellationToken: cancellationToken";

            builder.AppendLine($"    public Task UpdateAsync({parameters})");
            builder.AppendLine("    {");
            builder.AppendLine("        System.ArgumentNullException.ThrowIfNull(item);");
            builder.AppendLine();
            builder.AppendLine("        return OpenTransport.SendAsync(");
            builder.AppendLine($"            () => _client.{resource.Update}Async({arguments}),");
            builder.AppendLine($"            \"PUT {resource.Path}\");");
            builder.AppendLine("    }");
        }

        if (resource.Delete is not null)
        {
            builder.AppendLine();
            builder.AppendLine("    /// <summary>Deletes a record.</summary>");
            builder.AppendLine($"    /// <param name=\"{key.Name}\">Its identifier.</param>");
            builder.AppendLine("    /// <param name=\"cancellationToken\">Cancels the request.</param>");
            builder.AppendLine("    /// <returns>A task that completes once the record is gone.</returns>");
            builder.AppendLine($"    public Task DeleteAsync({key.Type} {key.Name}, CancellationToken cancellationToken = default) =>");
            builder.AppendLine("        OpenTransport.SendAsync(");
            builder.AppendLine($"            () => _client.{resource.Delete}Async({scoped}{key.Name}: {key.Name}, cancellationToken: cancellationToken),");
            builder.AppendLine($"            \"DELETE {resource.Path}/{{{key.Name}}}\");");
        }

        builder.AppendLine("}");
    }

    /// <summary>
    /// The field type for a published operator list, narrowed to what the value can carry.
    /// </summary>
    /// <remarks>
    /// The lists are an upper bound rather than gospel: a contact's <c>name</c> claims <c>in</c> and
    /// <c>nin</c>, which e-conomic documents as numeric-only, and a boolean claims the ordering
    /// operators. Offering those would put the same over-claim into a compile-time surface that the
    /// legacy one had to be corrected for, so each is intersected with what its type supports.
    /// </remarks>
    private static string FieldType(string operators, string csharpType)
    {
        var bare = csharpType.TrimEnd('?');
        var text = bare == "string";
        var numeric = bare is "int" or "long" or "double" or "decimal" or "float";
        var ordered = numeric || bare.Contains("Date", StringComparison.Ordinal) || bare == "System.TimeSpan";

        if (text)
        {
            return operators.Contains("like", StringComparison.Ordinal) ? "TextField" : "EqualityField<string>";
        }

        if (numeric && operators.Contains("in", StringComparison.Ordinal))
        {
            return $"NumericField<{bare}>";
        }

        return ordered ? $"ComparableField<{bare}>" : $"EqualityField<{bare}>";
    }

    private static string? OperationId(JsonNode? operation) =>
        operation?["operationId"]?.GetValue<string>();

    /// <summary>
    /// The property a create response carries the new identifier in, and its type.
    /// </summary>
    /// <remarks>
    /// Not always a number and not always called <c>number</c>: a product answers with a string
    /// <c>productNumber</c> and a sales price with a <c>currency</c>. Reading both from the schema
    /// is what keeps the create signature honest about what comes back.
    /// </remarks>
    private static OpenParameter CreatedProperty(JsonNode? post, JsonObject schemas)
    {
        var created = Reference(post?["responses"]?["201"]?["content"]?["application/json"]?["schema"]);
        var property = created is null
            ? null
            : schemas[created]?["properties"]?.AsObject().FirstOrDefault();

        if (property is null or { Key: null })
        {
            return new OpenParameter("Number", "int?");
        }

        var name = property.Value.Key;
        var type = property.Value.Value?["type"]?.GetValue<string>() == "integer" ? "int?" : "string?";

        return new OpenParameter(char.ToUpperInvariant(name[0]) + name[1..], type);
    }

    private static string? Reference(JsonNode? node) =>
        node?["$ref"]?.GetValue<string>() is { } reference
        && reference.StartsWith(RefPrefix, StringComparison.Ordinal)
            ? reference[RefPrefix.Length..]
            : null;

    [GeneratedRegex(@"internal partial class (?<name>\w+)\s*\r?\n\s*\{(?<body>(?:[^{}]|\{[^{}]*\})*)\}", RegexOptions.ExplicitCapture)]
    private static partial Regex ClassPattern();

    // Attributes can sit between the two: a property with a minimum picks up a Range, which is how
    // customerNumber went missing from the model the first time this ran.
    [GeneratedRegex(
        @"JsonPropertyName\(""(?<json>[^""]+)""\)\](?:\s*\[[^\]]*\])*\s*\r?\n\s*public (?<type>[\w<>.?]+) (?<name>\w+) \{ get; set; \}",
        RegexOptions.ExplicitCapture)]
    private static partial Regex PropertyPattern();
}

/// <summary>One path parameter, as the specification declares it.</summary>
/// <param name="Name">The parameter name, which is also what NSwag names its argument.</param>
/// <param name="Type">The C# type: <c>int</c> or <c>string</c>.</param>
public sealed record OpenParameter(string Name, string Type);

/// <summary>One collection of an OpenAPI service, and the operations it publishes.</summary>
/// <param name="Entity">The entity type name.</param>
/// <param name="Path">The collection path, e.g. <c>/Customers</c>.</param>
/// <param name="Client">The generated client class.</param>
/// <param name="Cursor">Operation id of the cursor listing.</param>
/// <param name="Paged">Operation id of the paged listing.</param>
/// <param name="PagedPath">Where the paged listing lives, which is not always beside the collection.</param>
/// <param name="Count">Operation id of the count, if published.</param>
/// <param name="Get">Operation id of the single fetch, if published.</param>
/// <param name="Create">Operation id of the create, if published.</param>
/// <param name="Update">Operation id of the update, if published.</param>
/// <param name="UpdateTakesId">Whether the update addresses the item rather than the collection.</param>
/// <param name="Delete">Operation id of the delete, if published.</param>
/// <param name="CreatedProperty">The property a create response carries the identifier in, and its type.</param>
/// <param name="Parent">The identifier this whole collection hangs off, if any.</param>
/// <param name="Key">The identifier of a single item, if the service publishes an item path.</param>
/// <param name="Name">The public type name, which carries the service. Defaults to the entity.</param>
public sealed record OpenResource(
    string Entity,
    string Path,
    string Client,
    string? Cursor,
    string? Paged,
    string? PagedPath,
    string? Count,
    string? Get,
    string? Create,
    string? Update,
    bool UpdateTakesId,
    string? Delete,
    OpenParameter CreatedProperty,
    OpenParameter? Parent = null,
    OpenParameter? Key = null,
    string? Name = null)
{
    /// <summary>The public type name: the entity, prefixed by the service.</summary>
    public string PublicName => Name ?? Entity;

    /// <summary>The identifier of a single item, defaulting to an integer <c>number</c>.</summary>
    public OpenParameter Identifier => Key ?? new OpenParameter("number", "int");

    /// <summary>The same resource, named for the service that publishes it.</summary>
    /// <param name="qualifier">The service's contribution to the name, e.g. <c>Customer</c>.</param>
    /// <returns>The resource with its public name set.</returns>
    /// <remarks>
    /// The prefix is dropped when the entity already opens with it, so the customers service's
    /// <c>Customer</c> does not become <c>CustomerCustomer</c> — the same rule the legacy registry
    /// applies when qualifying a type by its owning resource.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="qualifier"/> is blank.</exception>
    public OpenResource QualifiedBy(string qualifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifier);

        return this with
        {
            Name = Entity.StartsWith(qualifier, StringComparison.Ordinal)
                ? Entity
                : qualifier + Entity,
        };
    }
}
