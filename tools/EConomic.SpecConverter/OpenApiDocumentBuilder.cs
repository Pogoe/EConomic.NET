using System.Text;
using System.Text.Json.Nodes;

namespace EConomic.SpecConverter;

/// <summary>One converted endpoint, ready to be placed into a document.</summary>
/// <param name="Endpoint">The resolved path and method.</param>
/// <param name="Schema">The converted payload schema.</param>
/// <param name="RestDocsUrl">Link to the endpoint's page in e-conomic's REST docs, if the file had one.</param>
public sealed record ConvertedEndpoint(LegacyEndpoint Endpoint, JsonObject Schema, string? RestDocsUrl);

/// <summary>
/// Assembles converted endpoints into one OpenAPI 3.0 document per top-level resource.
/// </summary>
public sealed class OpenApiDocumentBuilder(SchemaRegistry registry)
{
    private readonly SchemaRegistry _registry = registry
        ?? throw new ArgumentNullException(nameof(registry));

    /// <summary>Builds the document for a single resource.</summary>
    /// <param name="resource">Resource name, e.g. <c>customers</c>.</param>
    /// <param name="endpoints">Every converted endpoint belonging to that resource.</param>
    /// <returns>The OpenAPI 3.0 document.</returns>
    public JsonObject Build(string resource, IReadOnlyList<ConvertedEndpoint> endpoints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentNullException.ThrowIfNull(endpoints);

        var paths = new JsonObject();

        foreach (var converted in endpoints.OrderBy(e => e.Endpoint.Path, StringComparer.Ordinal))
        {
            var path = converted.Endpoint.Path;
            if (paths[path] is not JsonObject item)
            {
                item = [];
                paths[path] = item;
            }

            item[converted.Endpoint.Method] = BuildOperation(converted);
        }

        return new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject
            {
                ["title"] = $"e-conomic legacy REST API - {resource}",
                ["version"] = "1.0.0",
                ["description"] =
                    "Generated from e-conomic's published JSON Schema draft-03 files by "
                    + "tools/EConomic.SpecConverter. Do not edit by hand.",
            },
            ["servers"] = new JsonArray { new JsonObject { ["url"] = "https://restapi.e-conomic.com" } },
            ["security"] = new JsonArray
            {
                new JsonObject
                {
                    ["AppSecretToken"] = new JsonArray(),
                    ["AgreementGrantToken"] = new JsonArray(),
                },
            },
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = new JsonObject
                {
                    ["AppSecretToken"] = ApiKeyHeader("X-AppSecretToken", "Identifies the integration."),
                    ["AgreementGrantToken"] = ApiKeyHeader("X-AgreementGrantToken", "Identifies the customer agreement."),
                },
                ["schemas"] = new JsonObject(),
            },
        };
    }

    /// <summary>
    /// Fills a document's <c>components/schemas</c> from the registry, following references
    /// transitively so a component that refers to another brings it along.
    /// </summary>
    /// <param name="document">The document to complete.</param>
    /// <param name="names">Component names referenced directly by that document's operations.</param>
    public void AddComponents(JsonObject document, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(names);

        var required = new HashSet<string>(names, StringComparer.Ordinal);
        var queue = new Queue<string>(required);

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            foreach (var nested in References(_registry.Schemas[name]))
            {
                if (required.Add(nested))
                {
                    queue.Enqueue(nested);
                }
            }
        }

        var schemas = document["components"]!["schemas"]!.AsObject();
        foreach (var name in required.OrderBy(n => n, StringComparer.Ordinal))
        {
            schemas[name] = _registry.Schemas[name].DeepClone();
        }
    }

    /// <summary>Component names referenced by a node, at any depth.</summary>
    /// <param name="node">The node to scan.</param>
    /// <returns>The referenced component names.</returns>
    public static IEnumerable<string> References(JsonNode? node)
    {
        const string Prefix = "#/components/schemas/";

        switch (node)
        {
            case JsonObject obj:
                if (obj["$ref"]?.GetValue<string>() is { } reference
                    && reference.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    yield return reference[Prefix.Length..];
                }

                foreach (var (_, value) in obj)
                {
                    foreach (var found in References(value))
                    {
                        yield return found;
                    }
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var found in References(item))
                    {
                        yield return found;
                    }
                }

                break;
        }
    }

    private JsonObject BuildOperation(ConvertedEndpoint converted)
    {
        var endpoint = converted.Endpoint;
        _registry.Context = endpoint.Resource;
        var extracted = _registry.ExtractNested(converted.Schema);
        var componentName = _registry.Register(extracted, FallbackName(endpoint));
        var reference = new JsonObject { ["$ref"] = $"#/components/schemas/{componentName}" };

        var parameters = new JsonArray();
        foreach (var name in endpoint.Parameters)
        {
            parameters.Add(PathParameter(name));
        }

        // Collection responses carry a `collection` array alongside `pagination`, and only those
        // endpoints accept the filtering, sorting and paging query string.
        if (endpoint.Method == "get" && IsCollection(converted.Schema))
        {
            foreach (var parameter in CollectionQueryParameters())
            {
                parameters.Add(parameter);
            }
        }

        var responses = new JsonObject
        {
            ["200"] = new JsonObject
            {
                ["description"] = "Success",
                ["content"] = new JsonObject
                {
                    ["application/json"] = new JsonObject { ["schema"] = reference.DeepClone() },
                },
            },
        };

        // The schema files describe bodies, never status codes, so these have to come from the
        // documentation. Its status table is explicit: "201 Created — When you create resources,
        // this is what you get. This will be accompanied by the created resource in the body."
        // Declaring only 200 makes the generated client reject every successful create.
        //
        // PUT needs it too, which the documentation does not mention: PUT is an upsert. Sending one
        // for an identifier that does not exist creates the resource and answers 201, verified
        // against a live agreement. Without this a perfectly successful upsert throws.
        if (endpoint.Method is "post" or "put")
        {
            responses["201"] = new JsonObject
            {
                ["description"] = "Created",
                ["content"] = new JsonObject
                {
                    ["application/json"] = new JsonObject { ["schema"] = reference.DeepClone() },
                },
            };
        }

        var operation = new JsonObject
        {
            ["operationId"] = OperationId(endpoint),
            ["tags"] = new JsonArray { endpoint.Resource },
            ["responses"] = responses,
            ["x-economic-source"] = endpoint.SourceFile,
        };

        if (parameters.Count > 0)
        {
            operation["parameters"] = parameters;
        }

        if (endpoint.Method is "post" or "put")
        {
            // The legacy files publish one schema per endpoint without distinguishing request from
            // response. For a write that schema describes the payload being sent, and e-conomic
            // echoes the stored resource back, so the same schema serves both here.
            operation["requestBody"] = new JsonObject
            {
                ["required"] = true,
                ["content"] = new JsonObject
                {
                    ["application/json"] = new JsonObject { ["schema"] = reference.DeepClone() },
                },
            };
        }

        if (converted.RestDocsUrl is { Length: > 0 } docs)
        {
            operation["externalDocs"] = new JsonObject { ["url"] = docs };
        }

        return operation;
    }

    private static bool IsCollection(JsonObject schema) =>
        schema["properties"] is JsonObject properties && properties.ContainsKey("collection");

    private static JsonObject ApiKeyHeader(string header, string description) => new()
    {
        ["type"] = "apiKey",
        ["in"] = "header",
        ["name"] = header,
        ["description"] = description,
    };

    private static JsonObject PathParameter(string name) => new()
    {
        ["name"] = name,
        ["in"] = "path",
        ["required"] = true,
        ["schema"] = new JsonObject { ["type"] = ParameterType(name) },
    };

    /// <summary>
    /// Identifiers ending in "Number" that are nonetheless strings.
    /// </summary>
    /// <remarks>
    /// <c>productNumber</c> is a string in the read schema and a string on the wire: a live
    /// agreement accepts <c>ZZ-TEST-1</c> for create, update and delete, and returns
    /// <c>"productNumber": "1"</c> quoted. Typing the path parameter as an integer would make a
    /// perfectly valid product number unrepresentable.
    /// </remarks>
    private static readonly HashSet<string> StringIdentifiers = new(StringComparer.Ordinal)
    {
        "productNumber",
    };

    /// <summary>
    /// The schemas do not type their path parameters, so this is inferred from the name:
    /// anything ending in "Number", plus "id", is numeric unless listed as a string identifier.
    /// </summary>
    private static string ParameterType(string name) =>
        !StringIdentifiers.Contains(name)
        && (name.EndsWith("Number", StringComparison.Ordinal)
            || name.Equals("id", StringComparison.Ordinal)
            || name.Equals("customergroupnumber", StringComparison.Ordinal)
            || name.Equals("customerNo", StringComparison.Ordinal))
            ? "integer"
            : "string";

    private static IEnumerable<JsonObject> CollectionQueryParameters()
    {
        yield return QueryParameter(
            "filter",
            "string",
            "Filter expression, e.g. `name$eq:Joe$and:city$like:*port`. Values must escape "
            + "`$` `(` `)` `*` `[` `]` `,` by prefixing them with `$`.");

        yield return QueryParameter(
            "sort",
            "string",
            "Sort field; prefix with `-` for descending, `~` to sort numerics alphabetically.");

        yield return QueryParameter("skippages", "integer", "Zero-indexed page offset.");
        yield return QueryParameter("pagesize", "integer", "Items per page. Defaults to 20, maximum 1000.");
    }

    private static JsonObject QueryParameter(string name, string type, string description) => new()
    {
        ["name"] = name,
        ["in"] = "query",
        ["required"] = false,
        ["description"] = description,
        ["schema"] = new JsonObject { ["type"] = type },
    };

    private static string FallbackName(LegacyEndpoint endpoint) =>
        SchemaRegistry.Identifier($"{endpoint.Resource} {endpoint.Method} response");

    private static string OperationId(LegacyEndpoint endpoint)
    {
        var builder = new StringBuilder(endpoint.Method);

        foreach (var segment in endpoint.Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = segment.Replace("{", "By", StringComparison.Ordinal)
                .Replace("}", string.Empty, StringComparison.Ordinal);

            foreach (var word in clean.Split('-', StringSplitOptions.RemoveEmptyEntries))
            {
                builder.Append(char.ToUpperInvariant(word[0])).Append(word[1..]);
            }
        }

        return builder.ToString();
    }
}
