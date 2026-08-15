using System.Text.Json;
using System.Text.Json.Nodes;
using EConomic.SpecConverter;

// Subcommand: emit the public facade for one OpenAPI service.
if (args.Length > 0 && args[0].Equals("open-facade", StringComparison.Ordinal))
{
    var service = args.Length > 1 ? args[1] : "Customers";
    var specFile = Path.Combine("specs", "openapi-prepared", $"openapi-{service}.json");
    var generatedName = OpenFacadeGenerator.GeneratedNamespace(service);
    var clientFile = Path.Combine("src", "EConomic.Net", "Open", "Generated", $"{generatedName}Service.g.cs");
    var outputFile = Path.Combine("src", "EConomic.Net", "Open", $"{generatedName}.g.cs");

    foreach (var required in (string[])[specFile, clientFile])
    {
        if (!File.Exists(required))
        {
            Console.Error.WriteLine($"Not found: {Path.GetFullPath(required)}");
            return 1;
        }
    }

    if (JsonNode.Parse(File.ReadAllText(specFile)) is not JsonObject openDocument)
    {
        Console.Error.WriteLine($"Not an object: {specFile}");
        return 1;
    }

    var openSkipped = new List<string>();
    var openSource = OpenFacadeGenerator.Generate(
        openDocument, File.ReadAllText(clientFile), "EConomic.Open", service, openSkipped);
    File.WriteAllText(outputFile, openSource.ReplaceLineEndings("\n"));

    var qualifier = OpenFacadeGenerator.ServiceNames[service];
    var openSchemas = openDocument["components"]!["schemas"]!.AsObject();
    var collections = OpenFacadeGenerator
        .Deduplicated(
            OpenFacadeGenerator.Collections(openDocument["paths"]!.AsObject(), openSchemas),
            openSchemas)
        .Select(c => c.QualifiedBy(qualifier))
        .ToList();

    Console.WriteLine(
        $"Wrote {outputFile}: {collections.Count} collections "
        + $"({string.Join(", ", collections.Select(c => c.PublicName))})");

    foreach (var scoped in collections.Where(c => c.Parent is not null))
    {
        Console.WriteLine($"  {scoped.PublicName} hangs off {scoped.Parent!.Name} ({scoped.Path})");
    }

    foreach (var odd in collections.Where(c => c.PagedPath is not null && c.PagedPath != $"{c.Path}/paged"))
    {
        Console.WriteLine($"  {odd.PublicName} pages through {odd.PagedPath}, not {odd.Path}/paged");
    }

    foreach (var uncounted in collections.Where(c => c.Count is null))
    {
        Console.WriteLine($"  {uncounted.PublicName} publishes no count endpoint");
    }

    foreach (var (omitted, kept) in OpenFacadeGenerator.DuplicateCollections
        .Where(d => openDocument["paths"]![d.Key] is not null))
    {
        Console.WriteLine($"  {omitted} omitted: it answers with the same records as {kept}");
    }

    // Everything the document declares that no emitted resource reaches. Reported rather than
    // dropped: an operation the facade cannot express is a gap someone has to decide about, and
    // the legacy facade generator reports its own the same way.
    var reached = collections
        .SelectMany(c => new[] { c.Cursor, c.Paged, c.Count, c.Get, c.Create, c.Update, c.Delete })
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal);

    var missed = openDocument["paths"]!.AsObject()
        .SelectMany(path => path.Value!.AsObject()
            .Where(operation => operation.Value?["operationId"] is not null)
            .Select(operation => (
                Path: path.Key,
                Method: operation.Key.ToUpperInvariant(),
                Id: operation.Value!["operationId"]!.GetValue<string>())))
        .Where(o => !reached.Contains(o.Id))
        // A collection omitted as a duplicate is already reported, once, with the reason. Listing
        // its seven operations again as gaps would bury the operations that genuinely are gaps.
        .Where(o => !OpenFacadeGenerator.DuplicateCollections.Keys.Any(d =>
            o.Path == d || o.Path.StartsWith($"{d}/", StringComparison.Ordinal)))
        .OrderBy(o => o.Path, StringComparer.Ordinal)
        .ToList();

    foreach (var property in openSkipped)
    {
        Console.WriteLine($"  not expressed: {property}");
    }

    foreach (var (path, method, id) in missed)
    {
        Console.WriteLine($"  not expressed: {method} {path} ({id})");
    }

    return 0;
}

// Subcommand: prepare the OpenAPI service specifications for generation.
if (args.Length > 0 && args[0].Equals("open-specs", StringComparison.Ordinal))
{
    var openInput = args.Length > 1 ? args[1] : Path.Combine("specs", "openapi");
    var openOutput = args.Length > 2 ? args[2] : Path.Combine("specs", "openapi-prepared");

    if (!Directory.Exists(openInput))
    {
        Console.Error.WriteLine($"Input directory not found: {Path.GetFullPath(openInput)}");
        return 1;
    }

    Directory.CreateDirectory(openOutput);
    var writeSettings = new JsonSerializerOptions { WriteIndented = true };
    var preparedCount = 0;
    var markedCount = 0;
    var correctedTimestamps = new List<string>();
    var flattenedEnums = new List<string>();
    var unnulledRequired = new List<string>();

    foreach (var file in Directory.GetFiles(openInput, "*.json").OrderBy(f => f, StringComparer.Ordinal))
    {
        if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject document)
        {
            Console.Error.WriteLine($"Not an object: {file}");
            return 1;
        }

        markedCount += OpenSpecPreparer.Prepare(
            document, correctedTimestamps, flattenedEnums, unnulledRequired);
        File.WriteAllText(
            Path.Combine(openOutput, Path.GetFileName(file)),
            document.ToJsonString(writeSettings).ReplaceLineEndings("\n") + "\n");
        preparedCount++;
    }

    // Every correction describes something the specification gets wrong. One that no longer applies
    // is describing a specification that has changed, and this is the only moment anyone would see it.
    var fixedUp = OpenSpecPreparer.Timestamps
        .Except(correctedTimestamps, StringComparer.Ordinal)
        .OrderBy(t => t, StringComparer.Ordinal)
        .ToList();

    if (fixedUp.Count > 0)
    {
        Console.Error.WriteLine(
            $"These properties are corrected from a date to a timestamp, but no service declares "
            + $"them as a date any more: {string.Join(", ", fixedUp)}. Remove them from "
            + $"{nameof(OpenSpecPreparer)}.{nameof(OpenSpecPreparer.Timestamps)}.");
        return 1;
    }

    var stillNullable = OpenSpecPreparer.RequiredNotNullable
        .Except(unnulledRequired, StringComparer.Ordinal)
        .OrderBy(e => e, StringComparer.Ordinal)
        .ToList();

    if (stillNullable.Count > 0)
    {
        Console.Error.WriteLine(
            "These properties are required and were also declared nullable, but no service "
            + $"contradicts itself about them any more: {string.Join(", ", stillNullable)}. Remove "
            + $"them from {nameof(OpenSpecPreparer)}.{nameof(OpenSpecPreparer.RequiredNotNullable)}.");
        return 1;
    }

    // Dropping an inline path enumeration is only right while a reference is there to carry the
    // type. If no service does this any more, the rule is describing nothing and should go rather
    // than sit waiting to fire on something it was never written for.
    if (flattenedEnums.Count == 0)
    {
        Console.Error.WriteLine(
            "No path parameter declares an inline enumeration beside a reference any more. Remove "
            + $"{nameof(OpenSpecPreparer)}.FlattenPathEnums — it is describing a shape no "
            + "specification has.");
        return 1;
    }

    Console.WriteLine(
        $"Prepared {preparedCount} service specifications into {openOutput}: "
        + $"{markedCount} optional value-typed properties marked nullable, "
        + $"{correctedTimestamps.Count} mislabelled dates corrected, "
        + $"{unnulledRequired.Count} required properties un-nulled, "
        + $"{flattenedEnums.Count} inline path enumerations flattened");

    return 0;
}

// Subcommand: emit the public models, transports and client properties.
if (args.Length > 0 && args[0].Equals("facade", StringComparison.Ordinal))
{
    var facadeSource = args.Length > 1 ? args[1] : Path.Combine("specs", "legacy-openapi", "_all.json");
    var facadeFile = args.Length > 2 ? args[2] : Path.Combine("src", "EConomic.Net", "Rest", "Facade.g.cs");

    if (!File.Exists(facadeSource))
    {
        Console.Error.WriteLine($"Merged document not found: {Path.GetFullPath(facadeSource)}");
        return 1;
    }

    var facadeDocument = JsonNode.Parse(File.ReadAllText(facadeSource))!.AsObject();
    var skippedProperties = new List<string>();
    var facade = FacadeGenerator.Generate(facadeDocument, "EConomic.Rest", skippedProperties);
    File.WriteAllText(facadeFile, facade.ReplaceLineEndings("\n"));

    var resourceCount = FacadeGenerator.Resources(facadeDocument)
        .Count(r => SchemaRegistry.PublishedEntities.Contains(r.Entity));

    Console.WriteLine($"Wrote {facadeFile}: {resourceCount} resources");

    if (skippedProperties.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"{skippedProperties.Count} propert(ies) the facade cannot express yet:");
        foreach (var skipped in skippedProperties)
        {
            Console.WriteLine($"  {skipped}");
        }
    }

    return 0;
}

// Subcommand: emit the public filter and sort surfaces from the merged document.
if (args.Length > 0 && args[0].Equals("filters", StringComparison.Ordinal))
{
    var surfaceSource = args.Length > 1
        ? args[1]
        : Path.Combine("specs", "legacy-openapi", "_all.json");

    var surfaceFile = args.Length > 2
        ? args[2]
        : Path.Combine("src", "EConomic.Net", "Rest", "Filters.g.cs");

    if (!File.Exists(surfaceSource))
    {
        Console.Error.WriteLine($"Merged document not found: {Path.GetFullPath(surfaceSource)}");
        Console.Error.WriteLine("Run the converter with no arguments first.");
        return 1;
    }

    var surfaceDocument = JsonNode.Parse(File.ReadAllText(surfaceSource))!.AsObject();
    var surfaces = FilterSurfaceGenerator.Generate(surfaceDocument, "EConomic.Rest");
    File.WriteAllText(surfaceFile, surfaces.ReplaceLineEndings("\n"));

    Console.WriteLine(
        $"Wrote {surfaceFile}: surfaces for {FilterSurfaceGenerator.CollectionEntities(surfaceDocument).Count} collection entities");

    return 0;
}

// Subcommand: rename generated enum members to the values e-conomic sends, so the
// source-generated converter writes them correctly without a reflection-based naming policy.
if (args.Length > 0 && args[0].Equals("enum-names", StringComparison.Ordinal))
{
    var enumFile = args.Length > 1
        ? args[1]
        : Path.Combine("src", "EConomic.Net", "Rest", "Generated", "LegacyClients.g.cs");

    if (!File.Exists(enumFile))
    {
        Console.Error.WriteLine($"Generated file not found: {Path.GetFullPath(enumFile)}");
        Console.Error.WriteLine("Run NSwag first: cd tools/nswag && dotnet nswag run legacy.nswag");
        return 1;
    }

    var unusable = new List<string>();
    var (rewritten, renamed) = EnumNameRewriter.Rewrite(File.ReadAllText(enumFile), unusable);
    File.WriteAllText(enumFile, rewritten.ReplaceLineEndings("\n"));

    Console.WriteLine($"Renamed {renamed} enum members in {enumFile}");

    if (unusable.Count > 0)
    {
        Console.WriteLine("Values that are not valid identifiers, left as generated:");
        foreach (var entry in unusable)
        {
            Console.WriteLine($"  {entry}");
        }
    }

    return 0;
}

// Subcommand: emit the source-generated JSON context for an NSwag-generated file.
if (args.Length > 0 && args[0].Equals("json-context", StringComparison.Ordinal))
{
    var generatedFile = args.Length > 1
        ? args[1]
        : Path.Combine("src", "EConomic.Net", "Rest", "Generated", "LegacyClients.g.cs");

    var contextFile = args.Length > 2
        ? args[2]
        : Path.Combine("src", "EConomic.Net", "Rest", "Generated", "EconomicRestJsonContext.g.cs");

    if (!File.Exists(generatedFile))
    {
        Console.Error.WriteLine($"Generated file not found: {Path.GetFullPath(generatedFile)}");
        Console.Error.WriteLine("Run NSwag first: cd tools/nswag && dotnet nswag run legacy.nswag");
        return 1;
    }

    var generated = File.ReadAllText(generatedFile);
    var contextNamespace = args.Length > 3 ? args[3] : "EConomic.Rest.Generated";
    var contextName = args.Length > 4 ? args[4] : "EconomicRestJsonContext";
    // The OpenAPI services number their enums where the legacy API names them, and the converter
    // that is right for one is wrong for the other.
    var stringEnums = !contextNamespace.StartsWith("EConomic.Open", StringComparison.Ordinal);
    var source = JsonContextGenerator.Generate(generated, contextNamespace, contextName, stringEnums);
    File.WriteAllText(contextFile, source.ReplaceLineEndings("\n"));

    Console.WriteLine(
        $"Wrote {contextFile}: "
        + $"{JsonContextGenerator.SerializationRoots(generated).Count} serialization roots, "
        + $"{JsonContextGenerator.ClientClasses(generated).Count} client hooks");

    return 0;
}

var inputDirectory = args.Length > 0 ? args[0] : Path.Combine("specs", "legacy");
var outputDirectory = args.Length > 1 ? args[1] : Path.Combine("specs", "legacy-openapi");

if (!Directory.Exists(inputDirectory))
{
    Console.Error.WriteLine($"Input directory not found: {Path.GetFullPath(inputDirectory)}");
    return 1;
}

var files = Directory.GetFiles(inputDirectory, "*.schema.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
Console.WriteLine($"Reading {files.Count} legacy schema files from {inputDirectory}");

// Two of e-conomic's published files contain trailing commas and are not valid JSON. Tolerating
// them here keeps specs/legacy byte-identical to what e-conomic published, so a later re-export
// diffs cleanly instead of fighting a local fix.
var parseOptions = new JsonDocumentOptions
{
    AllowTrailingCommas = true,
    CommentHandling = JsonCommentHandling.Skip,
};

var unknownSegments = EndpointResolver.UnknownCamelCaseSegments(files);
if (unknownSegments.Count > 0)
{
    Console.Error.WriteLine(
        "Unrecognised path segments - add each to EndpointResolver.ParameterSegments or "
        + "LiteralOverrides before continuing, since guessing would mis-route the endpoint:");

    foreach (var segment in unknownSegments)
    {
        Console.Error.WriteLine($"  {segment}");
    }

    return 1;
}

var registry = new SchemaRegistry();
var builder = new OpenApiDocumentBuilder(registry);
var unhandledKeywords = new SortedSet<string>(StringComparer.Ordinal);
var toleratedFiles = new List<string>();
var correctedTitles = 0;
var correctedNumericStrings = new List<string>();
var byResource = new SortedDictionary<string, List<ConvertedEndpoint>>(StringComparer.Ordinal);
var failures = new List<string>();

foreach (var file in files)
{
    var name = Path.GetFileName(file);
    var text = File.ReadAllText(file);

    JsonObject? source;
    try
    {
        source = JsonNode.Parse(text) as JsonObject;
    }
    catch (JsonException)
    {
        // Not strictly valid JSON. Retry leniently and report rather than fail.
        try
        {
            source = JsonNode.Parse(text, documentOptions: parseOptions) as JsonObject;
            toleratedFiles.Add(name);
        }
        catch (JsonException ex)
        {
            failures.Add($"{name}: {ex.Message}");
            continue;
        }
    }

    if (source is null)
    {
        failures.Add($"{name}: root is not a JSON object");
        continue;
    }

    LegacyEndpoint endpoint;
    try
    {
        endpoint = EndpointResolver.Resolve(name);
    }
    catch (ArgumentException ex)
    {
        failures.Add($"{name}: {ex.Message}");
        continue;
    }

    correctedTitles += Draft03Converter.CorrectTitles(source, name);
    Draft03Converter.CorrectNumericStrings(source, name, correctedNumericStrings);
    var schema = Draft03Converter.Convert(source, unhandledKeywords);
    var restDocs = source["restdocs"]?.GetValue<string>();

    if (!byResource.TryGetValue(endpoint.Resource, out var list))
    {
        list = [];
        byResource[endpoint.Resource] = list;
    }

    list.Add(new ConvertedEndpoint(endpoint, schema, restDocs));
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} file(s) could not be converted:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"  {failure}");
    }

    return 1;
}

// Every correction describes something the specification gets wrong. One that no longer applies is
// describing a specification that has changed, and this is the only moment anyone would see it.
var staleNumericStrings = Draft03Converter.NumericStrings
    .Except(correctedNumericStrings, StringComparer.Ordinal)
    .OrderBy(e => e, StringComparer.Ordinal)
    .ToList();

if (staleNumericStrings.Count > 0)
{
    Console.Error.WriteLine(
        "These properties are retyped from a string to a number, but no schema declares them as a "
        + $"string any more: {string.Join(", ", staleNumericStrings)}. Remove them from "
        + $"{nameof(Draft03Converter)}.{nameof(Draft03Converter.NumericStrings)}.");
    return 1;
}

// Discovery pass: work out which resource each entity belongs to before any name is handed out,
// so the unqualified name goes to the entity's home rather than to whichever resource sorts first.
foreach (var (title, resources) in DiscoverTitles(byResource))
{
    var identifier = SchemaRegistry.Identifier(title);
    var home = resources.FirstOrDefault(r => IsHomeResource(r, identifier));
    if (home is not null)
    {
        registry.HomeResources[identifier] = SchemaRegistry.Identifier(home);
    }
}

Directory.CreateDirectory(outputDirectory);

// LF explicitly, not Environment.NewLine: the generated documents are committed, so running the
// converter on Windows and on Linux CI must produce byte-identical output.
var writeOptions = new JsonSerializerOptions { WriteIndented = true, NewLine = "\n" };
var endpointCount = 0;
var usedOverrides = new HashSet<string>(StringComparer.Ordinal);
var overrideConflicts = new List<string>();
var mergeConflicts = new List<string>();
const string MergedDocumentName = "_all.json";

// Two passes on purpose. Building a document registers its schemas, and registering a schema that
// already exists merges the endpoint annotations into the stored copy. Embedding components during
// the first pass would therefore freeze an early document's copy before a later one enriched it,
// leaving the same component with different content in different files.
var documents = new List<(string Resource, JsonObject Document)>();

foreach (var (resource, endpoints) in byResource)
{
    documents.Add((resource, builder.Build(resource, endpoints)));
    endpointCount += endpoints.Count;
}

var correctedResponses = 0;

foreach (var (resource, document) in documents)
{
    builder.AddComponents(document, OpenApiDocumentBuilder.References(document["paths"]));

    // Must run after the components are embedded: it rewrites a response to reference the read
    // entity, which has to already be present in the document for the reference to resolve.
    correctedResponses += WriteResponseCorrector.Apply(document);
    WriteResponseCorrector.MarkOptionalValuesNullable(document);

    foreach (var applied in ApplyNameOverrides(document, resource, overrideConflicts))
    {
        usedOverrides.Add(applied);
    }

    var path = Path.Combine(outputDirectory, $"{resource}.json");
    File.WriteAllText(path, document.ToJsonString(writeOptions) + "\n");
}

if (overrideConflicts.Count > 0)
{
    Console.Error.WriteLine("NameOverrides entries that would collide with an existing component:");
    foreach (var conflict in overrideConflicts)
    {
        Console.Error.WriteLine($"  {conflict}");
    }

    return 1;
}

var staleOverrides = SchemaRegistry.NameOverrides.Keys
    .Where(k => !usedOverrides.Contains(k))
    .OrderBy(k => k, StringComparer.Ordinal)
    .ToList();

if (staleOverrides.Count > 0)
{
    Console.Error.WriteLine(
        "NameOverrides entries that matched nothing - the generated name they key on has changed:");

    foreach (var stale in staleOverrides)
    {
        Console.Error.WriteLine($"  {stale}");
    }

    return 1;
}

// A single merged document as well as the per-resource ones. The per-resource files are the
// reviewable artifact; code generation needs one document, because generating each separately
// would emit a copy of every shared type per document and they would collide in one namespace.
var merged = BuildMergedDocument(outputDirectory, mergeConflicts);
if (mergeConflicts.Count > 0)
{
    Console.Error.WriteLine("Components with the same name but different content across documents:");
    foreach (var conflict in mergeConflicts)
    {
        Console.Error.WriteLine($"  {conflict}");
    }

    return 1;
}

var mergedPath = Path.Combine(outputDirectory, MergedDocumentName);
File.WriteAllText(mergedPath, merged.ToJsonString(writeOptions) + "\n");

Console.WriteLine();
Console.WriteLine($"Wrote {byResource.Count} documents covering {endpointCount} endpoints to {outputDirectory}");
Console.WriteLine(
    $"Merged into {MergedDocumentName}: "
    + $"{merged["paths"]!.AsObject().Count} paths, "
    + $"{merged["components"]!["schemas"]!.AsObject().Count} components");
Console.WriteLine($"Components: {registry.Schemas.Count} distinct schemas after structural dedup");

if (toleratedFiles.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"Tolerated {toleratedFiles.Count} file(s) that are not strictly valid JSON:");
    foreach (var file in toleratedFiles)
    {
        Console.WriteLine($"  {file}");
    }
}

if (registry.Collisions.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"{registry.Collisions.Count} name collision(s) resolved mechanically - review these:");
    foreach (var collision in registry.Collisions)
    {
        Console.WriteLine($"  {collision.Title} -> {collision.AssignedName}");
    }
}

if (unhandledKeywords.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Keywords passed through without explicit handling:");
    foreach (var keyword in unhandledKeywords)
    {
        Console.WriteLine($"  {keyword}");
    }
}

return 0;

// Combines the per-resource documents into one. Component names are globally unique because a
// single registry assigns them, so identical names must carry identical content; anything else is
// a bug worth failing on rather than silently picking a winner.
static JsonObject BuildMergedDocument(string directory, List<string> conflicts)
{
    JsonObject? merged = null;
    var paths = new JsonObject();
    var schemas = new JsonObject();

    var files = Directory.GetFiles(directory, "*.json")
        .Where(f => !Path.GetFileName(f).StartsWith('_'))
        .OrderBy(f => f, StringComparer.Ordinal);

    foreach (var file in files)
    {
        var document = JsonNode.Parse(File.ReadAllText(file))!.AsObject();

        merged ??= new JsonObject
        {
            ["openapi"] = document["openapi"]!.DeepClone(),
            ["info"] = new JsonObject
            {
                ["title"] = "e-conomic legacy REST API",
                ["version"] = "1.0.0",
                ["description"] =
                    "Every legacy endpoint in one document, merged from the per-resource files by "
                    + "tools/EConomic.SpecConverter. This is the code generation input. Do not edit by hand.",
            },
            ["servers"] = document["servers"]!.DeepClone(),
            ["security"] = document["security"]!.DeepClone(),
        };

        foreach (var (path, item) in document["paths"]!.AsObject())
        {
            paths[path] = item!.DeepClone();
        }

        foreach (var (name, schema) in document["components"]!["schemas"]!.AsObject())
        {
            var incoming = schema!.DeepClone();
            if (schemas[name] is { } existing)
            {
                if (!JsonNode.DeepEquals(existing, incoming))
                {
                    conflicts.Add($"{name} (differs in {Path.GetFileName(file)})");
                }

                continue;
            }

            schemas[name] = incoming;
        }
    }

    merged!["paths"] = paths;
    merged["components"] = new JsonObject
    {
        ["securitySchemes"] = JsonNode
            .Parse(File.ReadAllText(Directory.GetFiles(directory, "*.json")
                .First(f => !Path.GetFileName(f).StartsWith('_'))))!["components"]!["securitySchemes"]!
            .DeepClone(),
        ["schemas"] = schemas,
    };

    return merged;
}

// Applies the curated names to a finished document, rewriting both the component keys and every
// reference to them. Done after the fact so the table keys stay stable: renaming during
// registration would free up a base name and change what the next schema is called.
static IEnumerable<string> ApplyNameOverrides(JsonObject document, string resource, List<string> conflicts)
{
    const string Prefix = "#/components/schemas/";

    var schemas = document["components"]!["schemas"]!.AsObject();
    var applied = new List<string>();
    var renamed = new JsonObject();

    foreach (var (name, schema) in schemas.ToList())
    {
        var target = name;
        if (SchemaRegistry.NameOverrides.TryGetValue(name, out var preferred))
        {
            target = preferred;
            applied.Add(name);
        }

        if (renamed.ContainsKey(target))
        {
            conflicts.Add($"{resource}.json: '{name}' -> '{target}' collides with an existing component");
            target = name;
        }

        renamed[target] = schema!.DeepClone();
    }

    document["components"]!["schemas"] = renamed;

    Rewrite(document);
    return applied;

    static void Rewrite(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["$ref"]?.GetValue<string>() is { } reference
                    && reference.StartsWith(Prefix, StringComparison.Ordinal)
                    && SchemaRegistry.NameOverrides.TryGetValue(reference[Prefix.Length..], out var preferred))
                {
                    obj["$ref"] = Prefix + preferred;
                }

                foreach (var (_, value) in obj)
                {
                    Rewrite(value);
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    Rewrite(item);
                }

                break;
        }
    }
}

// Titles of every named entity, mapped to the resources whose documents contain them.
static SortedDictionary<string, SortedSet<string>> DiscoverTitles(
    IDictionary<string, List<ConvertedEndpoint>> byResource)
{
    var titles = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

    foreach (var (resource, endpoints) in byResource)
    {
        foreach (var endpoint in endpoints)
        {
            Walk(endpoint.Schema, resource, titles);
        }
    }

    return titles;

    static void Walk(JsonNode? node, string resource, SortedDictionary<string, SortedSet<string>> found)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["title"]?.GetValue<string>() is { Length: > 0 } title
                    && obj["properties"] is JsonObject)
                {
                    if (!found.TryGetValue(title, out var resources))
                    {
                        resources = new SortedSet<string>(StringComparer.Ordinal);
                        found[title] = resources;
                    }

                    resources.Add(resource);
                }

                foreach (var (_, value) in obj)
                {
                    Walk(value, resource, found);
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    Walk(item, resource, found);
                }

                break;
        }
    }
}

// `customers` is home to `Customer`; `customer-groups` is not. Compared on the identifier form so
// that hyphenated resources line up, allowing for the resource being the plural.
static bool IsHomeResource(string resource, string identifier)
{
    var name = SchemaRegistry.Identifier(resource);
    return string.Equals(name, identifier, StringComparison.Ordinal)
        || string.Equals(name, identifier + "s", StringComparison.Ordinal)
        || string.Equals(name, identifier + "es", StringComparison.Ordinal);
}
