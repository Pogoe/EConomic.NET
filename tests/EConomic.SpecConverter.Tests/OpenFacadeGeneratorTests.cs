using System.Text.Json.Nodes;
using EConomic.SpecConverter;
using Xunit;

namespace EConomic.SpecConverter.Tests;

/// <summary>
/// The guards that keep two collections from quietly becoming one public type.
/// </summary>
/// <remarks>
/// The projects service is where this stopped being hypothetical: e-conomic prefixes half its own
/// entities with <c>Project</c>, which is also the name that service contributes, so <c>Activity</c>
/// and <c>ProjectActivity</c> both arrive at <c>ProjectActivity</c>. Emitting both would produce two
/// records, two filter surfaces and two resources under one name.
/// </remarks>
public class OpenFacadeGeneratorTests
{
    private static JsonObject Document(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>Two collections whose items are the named schemas, cursor-listed.</summary>
    private static JsonObject TwoCollections(string first, string second, string secondSchema) => Document(
        $$"""
        {
          "paths": {
            "/{{first}}": {
              "get": {
                "operationId": "GetAll{{first}}",
                "tags": ["{{first}}"],
                "responses": {
                  "200": {
                    "content": {
                      "application/json": {
                        "schema": { "$ref": "#/components/schemas/FirstCursorResults" }
                      }
                    }
                  }
                }
              }
            },
            "/{{second}}": {
              "get": {
                "operationId": "GetAll{{second}}",
                "tags": ["{{second}}"],
                "responses": {
                  "200": {
                    "content": {
                      "application/json": {
                        "schema": { "$ref": "#/components/schemas/SecondCursorResults" }
                      }
                    }
                  }
                }
              }
            }
          },
          "components": {
            "schemas": {
              "FirstCursorResults": {
                "properties": {
                  "items": { "type": "array", "items": { "$ref": "#/components/schemas/Widget" } }
                }
              },
              "SecondCursorResults": {
                "properties": {
                  "items": { "type": "array", "items": { "$ref": "#/components/schemas/{{secondSchema}}" } }
                }
              },
              "Widget": { "properties": { "number": { "type": "integer" } } },
              "{{secondSchema}}": { "properties": { "number": { "type": "integer" } } }
            }
          }
        }
        """);

    [Fact]
    public void A_prefixed_entity_keeps_the_prefix_it_already_has()
    {
        var resource = OpenFacadeGenerator
            .Collections(
                TwoCollections("Widgets", "gadget-widgets", "GadgetWidget")["paths"]!.AsObject(),
                TwoCollections("Widgets", "gadget-widgets", "GadgetWidget")["components"]!["schemas"]!.AsObject())
            .Single(r => r.Entity == "GadgetWidget")
            .QualifiedBy("Gadget");

        Assert.Equal("GadgetWidget", resource.PublicName);
    }

    [Fact]
    public void An_unprefixed_entity_is_qualified_by_its_service()
    {
        var document = TwoCollections("Widgets", "gadget-widgets", "GadgetWidget");
        var resource = OpenFacadeGenerator
            .Collections(document["paths"]!.AsObject(), document["components"]!["schemas"]!.AsObject())
            .Single(r => r.Entity == "Widget")
            .QualifiedBy("Gadget");

        // Which is exactly the collision: both of these are now GadgetWidget.
        Assert.Equal("GadgetWidget", resource.PublicName);
    }

    [Fact]
    public void A_curated_entity_name_wins_over_the_rule()
    {
        // Keyed by what e-conomic calls the entity rather than by what the rule produces, which is
        // the only key that can tell the two halves of a collision apart.
        Assert.Equal("ProjectActivityAssignment", OpenFacadeGenerator.EntityNames["Project.ProjectActivity"]);
        Assert.Equal("ProjectActivity", OpenFacadeGenerator.EntityNames["Project.Activity"]);
    }

    [Fact]
    public void A_recorded_duplicate_is_dropped_when_the_two_shapes_still_match()
    {
        var document = TwoCollections("EmployeeGroups", "project-employeegroups", "ProjectEmployeeGroup");
        var schemas = document["components"]!["schemas"]!.AsObject();

        var kept = OpenFacadeGenerator.Deduplicated(
            OpenFacadeGenerator.Collections(document["paths"]!.AsObject(), schemas), schemas);

        Assert.Equal(["/EmployeeGroups"], kept.Select(r => r.Path));
    }

    [Fact]
    public void A_recorded_duplicate_that_has_diverged_fails_the_run()
    {
        var document = TwoCollections("EmployeeGroups", "project-employeegroups", "ProjectEmployeeGroup");
        var schemas = document["components"]!["schemas"]!.AsObject();

        // One property apart is enough: the entry asserts the two carry the same type, and once they
        // do not, dropping one starts losing information.
        schemas["ProjectEmployeeGroup"]!["properties"]!["extra"] = new JsonObject { ["type"] = "string" };

        var thrown = Assert.Throws<InvalidOperationException>(() => OpenFacadeGenerator.Deduplicated(
            OpenFacadeGenerator.Collections(document["paths"]!.AsObject(), schemas), schemas));

        Assert.Contains("no longer have the same shape", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Differently_worded_prose_does_not_make_a_duplicate_diverge()
    {
        var document = TwoCollections("EmployeeGroups", "project-employeegroups", "ProjectEmployeeGroup");
        var schemas = document["components"]!["schemas"]!.AsObject();

        schemas["ProjectEmployeeGroup"]!["description"] = "The employee groups, described differently.";

        var kept = OpenFacadeGenerator.Deduplicated(
            OpenFacadeGenerator.Collections(document["paths"]!.AsObject(), schemas), schemas);

        Assert.Equal(["/EmployeeGroups"], kept.Select(r => r.Path));
    }

    [Fact]
    public void An_inline_path_enumeration_beside_a_reference_is_dropped()
    {
        var document = Document(
            """
            {
              "paths": {
                "/orders/{documentStatus}/lines": {
                  "get": {
                    "parameters": [
                      {
                        "name": "documentStatus",
                        "in": "path",
                        "schema": {
                          "enum": ["drafts", "sent", "archived"],
                          "allOf": [{ "$ref": "#/components/schemas/SalesDocumentStatusRoute" }]
                        }
                      },
                      {
                        "name": "status",
                        "in": "query",
                        "schema": {
                          "enum": ["open", "closed"],
                          "allOf": [{ "$ref": "#/components/schemas/Whatever" }]
                        }
                      },
                      {
                        "name": "kind",
                        "in": "path",
                        "schema": { "enum": ["a", "b"] }
                      }
                    ]
                  }
                }
              }
            }
            """);

        var flattened = new List<string>();
        OpenSpecPreparer.Prepare(document, flattened: flattened);

        var parameters = document["paths"]!["/orders/{documentStatus}/lines"]!["get"]!["parameters"]!.AsArray();

        // The one NSwag would mint an anonymous enum from, on every operation that takes it.
        Assert.Null(parameters[0]!["schema"]!["enum"]);

        // Left alone: a query parameter is not a path segment, and the facade's one-accessor-per-value
        // answer does not apply to it.
        Assert.NotNull(parameters[1]!["schema"]!["enum"]);

        // Left alone: with no reference beside it the inline enum is the whole definition, and
        // dropping it would leave the parameter with no type at all.
        Assert.NotNull(parameters[2]!["schema"]!["enum"]);

        Assert.Equal(["/orders/{documentStatus}/lines"], flattened);
    }

    [Fact]
    public void A_mislabelled_date_becomes_the_timestamp_the_server_sends()
    {
        var document = Document(
            """
            {
              "components": {
                "schemas": {
                  "ProjectEmployee": {
                    "properties": {
                      "cutOffDate": { "type": "string", "format": "date" },
                      "hiredOn": { "type": "string", "format": "date" }
                    }
                  }
                }
              }
            }
            """);

        var corrected = new List<string>();
        OpenSpecPreparer.Prepare(document, corrected);

        var properties = document["components"]!["schemas"]!["ProjectEmployee"]!["properties"]!;

        Assert.Equal("date-time", properties["cutOffDate"]!["format"]!.GetValue<string>());

        // Only the property that was verified against the server, never every date in the document.
        Assert.Equal("date", properties["hiredOn"]!["format"]!.GetValue<string>());
        Assert.Equal(["ProjectEmployee.cutOffDate"], corrected);
    }
}
