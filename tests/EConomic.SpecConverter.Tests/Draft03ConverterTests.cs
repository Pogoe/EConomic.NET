using System.Text.Json.Nodes;
using EConomic.SpecConverter;
using Xunit;

namespace EConomic.SpecConverter.Tests;

public class Draft03ConverterTests
{
    // Shaped after customers.get.schema.json, trimmed to the constructs that matter.
    private const string LegacySchema = """
        {
          "$schema": "http://json-schema.org/draft-03/schema#",
          "title": "Customer collection GET schema",
          "type": "object",
          "restdocs": "http://restdocs.e-conomic.com/#get-customers",
          "properties": {
            "collection": {
              "type": "array",
              "items": {
                "title": "Customer",
                "type": "object",
                "properties": {
                  "customerNumber": {
                    "type": "integer",
                    "required": true,
                    "sortable": true,
                    "filterable": true
                  },
                  "name": { "type": "string", "maxLength": 255, "filterable": true },
                  "lastUpdated": { "type": "string", "format": "full-date", "filterable": true },
                  "pNumber": { "type": "string", "minLength": 10 }
                }
              }
            },
            "self": { "type": "string", "format": "uri", "required": true }
          }
        }
        """;

    private static JsonObject Convert(out ISet<string> unhandled)
    {
        unhandled = new HashSet<string>(StringComparer.Ordinal);
        return Draft03Converter.Convert(JsonNode.Parse(LegacySchema)!.AsObject(), unhandled);
    }

    [Fact]
    public void Boolean_required_moves_onto_the_parent_as_an_array()
    {
        var result = Convert(out _);

        var root = result["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["self"], root);

        var customer = result["properties"]!["collection"]!["items"]!.AsObject();
        var required = customer["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["customerNumber"], required);

        // The boolean must not survive on the property itself.
        Assert.Null(customer["properties"]!["customerNumber"]!["required"]);
    }

    [Fact]
    public void Properties_that_are_not_required_are_left_out_of_the_array()
    {
        var result = Convert(out _);

        var customer = result["properties"]!["collection"]!["items"]!.AsObject();
        var required = customer["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

        Assert.DoesNotContain("name", required);
        Assert.DoesNotContain("pNumber", required);
    }

    [Fact]
    public void Draft03_full_date_becomes_the_modern_date_format()
    {
        var result = Convert(out _);
        var customer = result["properties"]!["collection"]!["items"]!.AsObject();

        Assert.Equal("date", customer["properties"]!["lastUpdated"]!["format"]!.GetValue<string>());
    }

    [Fact]
    public void Other_formats_are_left_alone()
    {
        var result = Convert(out _);

        Assert.Equal("uri", result["properties"]!["self"]!["format"]!.GetValue<string>());
    }

    [Fact]
    public void Filterable_and_sortable_become_the_same_vendor_extensions_the_openapi_services_use()
    {
        var result = Convert(out _);
        var properties = result["properties"]!["collection"]!["items"]!["properties"]!;

        Assert.True(properties["customerNumber"]!["x-filterable"]!.GetValue<bool>());
        Assert.True(properties["customerNumber"]!["x-sortable"]!.GetValue<bool>());
        Assert.True(properties["name"]!["x-filterable"]!.GetValue<bool>());

        // Absence means not filterable - the legacy schemas only ever mark the positive case.
        Assert.Null(properties["pNumber"]!["x-filterable"]);
        Assert.Null(properties["name"]!["x-sortable"]);
    }

    [Fact]
    public void Document_level_keywords_are_dropped()
    {
        var result = Convert(out _);

        Assert.Null(result["$schema"]);
        Assert.Null(result["restdocs"]);
    }

    [Fact]
    public void Validation_keywords_survive()
    {
        var result = Convert(out _);
        var properties = result["properties"]!["collection"]!["items"]!["properties"]!;

        Assert.Equal(255, properties["name"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(10, properties["pNumber"]!["minLength"]!.GetValue<int>());
    }

    [Fact]
    public void Nothing_in_the_real_schemas_goes_unhandled()
    {
        Convert(out var unhandled);

        Assert.Empty(unhandled);
    }

    [Fact]
    public void Unrecognised_keywords_are_reported_and_still_carried_across()
    {
        var source = JsonNode.Parse("""{"type":"object","somethingNew":42}""")!.AsObject();
        var unhandled = new HashSet<string>(StringComparer.Ordinal);

        var result = Draft03Converter.Convert(source, unhandled);

        Assert.Equal("somethingNew", Assert.Single(unhandled));
        Assert.Equal(42, result["somethingNew"]!.GetValue<int>());
    }
}
