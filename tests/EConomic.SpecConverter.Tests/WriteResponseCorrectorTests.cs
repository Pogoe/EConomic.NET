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
                "required": ["name", "keptNumber", "keptDate"],
                "properties": {
                  "name": { "type": "string" },
                  "keptNumber": { "type": "integer" },
                  "keptDate": { "type": "string", "format": "date" },
                  "optionalNumber": { "type": "integer" },
                  "date": { "type": "string", "format": "date" },
                  "lastUpdated": { "type": "string", "format": "date-time" },
                  "note": { "type": "string" },
                  "kind": { "enum": ["net", "dueDate"], "description": "No type, as e-conomic writes them." },
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
                        "quantity": { "type": "number" },
                        "accrual": {
                          "type": "object",
                          "properties": { "startDate": { "type": "string", "format": "date" } }
                        }
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

        WriteResponseCorrector.MarkOptionalValuesNullable(document);

        var properties = document["components"]!["schemas"]!["ThingPost"]!["properties"]!;

        // A non-nullable int defaults to 0 and is serialized whether or not the caller set it, and
        // e-conomic rejects that for any identifier declaring a minimum of 1.
        Assert.True(properties["optionalNumber"]!["nullable"]!.GetValue<bool>());
        Assert.Null(properties["keptNumber"]!["nullable"]);
    }

    [Fact]
    public void Optional_dates_on_a_write_payload_become_nullable()
    {
        var document = WritePayloadDocument();

        WriteResponseCorrector.MarkOptionalValuesNullable(document);

        var properties = document["components"]!["schemas"]!["ThingPost"]!["properties"]!;

        // A date is a value type too, so an untouched one is serialized as 0001-01-01. e-conomic
        // accepts that rather than rejecting it, which makes it the more dangerous of the two: an
        // unset dueDate silently became a date in year one instead of failing the request.
        Assert.True(properties["date"]!["nullable"]!.GetValue<bool>());
        Assert.True(properties["lastUpdated"]!["nullable"]!.GetValue<bool>());

        // A required date is supplied by the caller, and a plain string is already absent when unset.
        Assert.Null(properties["keptDate"]!["nullable"]);
        Assert.Null(properties["note"]!["nullable"]);
    }

    [Fact]
    public void Optional_enums_on_a_write_payload_become_nullable()
    {
        var document = WritePayloadDocument();

        WriteResponseCorrector.MarkOptionalValuesNullable(document);

        // A C# enum defaults to its first member, so an unset one is sent as that member: e-conomic
        // rejected an invoice whose paymentTermsType said "net" while the payment terms it named
        // said otherwise. Note the schema declares no type for it — e-conomic writes enums with
        // nothing but their values — so matching on "type": "string" missed every one.
        var properties = document["components"]!["schemas"]!["ThingPost"]!["properties"]!;

        Assert.True(properties["kind"]!["nullable"]!.GetValue<bool>());
    }

    [Fact]
    public void Optional_dates_inside_array_items_become_nullable()
    {
        var document = WritePayloadDocument();

        WriteResponseCorrector.MarkOptionalValuesNullable(document);

        // Where the leak actually was: an invoice line's accrual dates, two levels down.
        var accrual = document["components"]!["schemas"]!["ThingPost"]!["properties"]!["lines"]!["items"]!
            ["properties"]!["accrual"]!;

        Assert.True(accrual["properties"]!["startDate"]!["nullable"]!.GetValue<bool>());
    }

    [Fact]
    public void Optional_numbers_nested_inside_an_object_become_nullable()
    {
        var document = WritePayloadDocument();

        WriteResponseCorrector.MarkOptionalValuesNullable(document);

        var customer = document["components"]!["schemas"]!["ThingPost"]!["properties"]!["customer"]!;

        Assert.True(customer["properties"]!["customerNumber"]!["nullable"]!.GetValue<bool>());
    }

    [Fact]
    public void Optional_numbers_inside_array_items_become_nullable()
    {
        var document = WritePayloadDocument();

        WriteResponseCorrector.MarkOptionalValuesNullable(document);

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

        WriteResponseCorrector.MarkOptionalValuesNullable(document);

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
