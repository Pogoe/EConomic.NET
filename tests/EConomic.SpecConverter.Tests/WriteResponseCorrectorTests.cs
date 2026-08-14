using System.Text.Json.Nodes;
using EConomic.SpecConverter;
using Xunit;

namespace EConomic.SpecConverter.Tests;

public class WriteResponseCorrectorTests
{
    private static JsonObject Document(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>
    /// A document with one write-only payload, shaped like the draft invoice one: optional numbers
    /// at the top level, inside a nested object, and inside an array's items.
    /// </summary>
    private static JsonObject WritePayloadDocument() => Document(
        """
        {
          "paths": {
            "/things": {
              "post": {
                "requestBody": {
                  "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ThingPost" } } }
                },
                "responses": {
                  "201": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Thing" } } } }
                }
              }
            }
          },
          "components": {
            "schemas": {
              "ThingPost": {
                "type": "object",
                "required": ["name", "keptNumber"],
                "properties": {
                  "name": { "type": "string" },
                  "keptNumber": { "type": "integer" },
                  "optionalNumber": { "type": "integer" },
                  "customer": {
                    "type": "object",
                    "properties": { "customerNumber": { "type": "integer" } }
                  },
                  "lines": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "required": ["quantity"],
                      "properties": {
                        "lineNumber": { "type": "integer" },
                        "quantity": { "type": "number" }
                      }
                    }
                  }
                }
              },
              "Thing": {
                "type": "object",
                "properties": { "thingNumber": { "type": "integer" } }
              }
            }
          }
        }
        """);

    [Fact]
    public void Optional_numbers_on_a_write_payload_become_nullable()
    {
        var document = WritePayloadDocument();

        WriteResponseCorrector.MarkOptionalNumbersNullable(document);

        var properties = document["components"]!["schemas"]!["ThingPost"]!["properties"]!;

        // A non-nullable int defaults to 0 and is serialized whether or not the caller set it, and
        // e-conomic rejects that for any identifier declaring a minimum of 1.
        Assert.True(properties["optionalNumber"]!["nullable"]!.GetValue<bool>());
        Assert.Null(properties["keptNumber"]!["nullable"]);
    }

    [Fact]
    public void Optional_numbers_nested_inside_an_object_become_nullable()
    {
        var document = WritePayloadDocument();

        WriteResponseCorrector.MarkOptionalNumbersNullable(document);

        var customer = document["components"]!["schemas"]!["ThingPost"]!["properties"]!["customer"]!;

        Assert.True(customer["properties"]!["customerNumber"]!["nullable"]!.GetValue<bool>());
    }

    [Fact]
    public void Optional_numbers_inside_array_items_become_nullable()
    {
        var document = WritePayloadDocument();

        WriteResponseCorrector.MarkOptionalNumbersNullable(document);

        // The case that reached the server: a draft invoice line the caller did not number was
        // rejected with "Integer 0 is less than minimum value of 1" for both lineNumber and sortKey.
        var item = document["components"]!["schemas"]!["ThingPost"]!["properties"]!["lines"]!["items"]!;

        Assert.True(item["properties"]!["lineNumber"]!["nullable"]!.GetValue<bool>());
        Assert.Null(item["properties"]!["quantity"]!["nullable"]);
    }

    [Fact]
    public void A_response_schema_keeps_its_non_nullable_numbers()
    {
        var document = WritePayloadDocument();

        WriteResponseCorrector.MarkOptionalNumbersNullable(document);

        // Only components used solely as request bodies are touched: a read model reporting a
        // number the server always sends should not become nullable.
        var thing = document["components"]!["schemas"]!["Thing"]!;

        Assert.Null(thing["properties"]!["thingNumber"]!["nullable"]);
    }

    [Fact]
    public void A_write_response_is_pointed_at_the_read_entity()
    {
        var document = Document(
            """
            {
              "paths": {
                "/things": {
                  "get": {
                    "responses": {
                      "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ThingCollection" } } } }
                    }
                  },
                  "post": {
                    "responses": {
                      "201": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ThingPost" } } } }
                    }
                  }
                }
              },
              "components": {
                "schemas": {
                  "ThingCollection": {
                    "type": "object",
                    "properties": { "collection": { "type": "array", "items": { "$ref": "#/components/schemas/Thing" } } }
                  },
                  "Thing": { "type": "object", "properties": { "thingNumber": { "type": "integer" } } },
                  "ThingPost": { "type": "object", "properties": { "name": { "type": "string" } } }
                }
              }
            }
            """);

        var corrected = WriteResponseCorrector.Apply(document);

        // A create returns the whole resource, not the payload it was sent.
        Assert.Equal(1, corrected);
        Assert.Equal(
            "#/components/schemas/Thing",
            document["paths"]!["/things"]!["post"]!["responses"]!["201"]!["content"]!["application/json"]!["schema"]!["$ref"]!
                .GetValue<string>());
    }
}
