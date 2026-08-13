using System.Text.Json.Nodes;
using EConomic.SpecConverter;
using Xunit;

namespace EConomic.SpecConverter.Tests;

public class SchemaRegistryTests
{
    private static JsonObject Schema(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Identical_shapes_collapse_to_one_component()
    {
        var registry = new SchemaRegistry();

        var first = registry.Register(
            Schema("""{"title":"Customer","type":"object","properties":{"name":{"type":"string"}}}"""),
            "Fallback");
        var second = registry.Register(
            Schema("""{"title":"Customer","type":"object","properties":{"name":{"type":"string"}}}"""),
            "Fallback");

        Assert.Equal(first, second);
        Assert.Single(registry.Schemas);
    }

    [Fact]
    public void Property_order_does_not_create_a_second_component()
    {
        var registry = new SchemaRegistry();

        var first = registry.Register(
            Schema("""{"title":"Customer","properties":{"a":{"type":"string"},"b":{"type":"string"}}}"""),
            "Fallback");
        var second = registry.Register(
            Schema("""{"title":"Customer","properties":{"b":{"type":"string"},"a":{"type":"string"}}}"""),
            "Fallback");

        Assert.Equal(first, second);
        Assert.Single(registry.Schemas);
    }

    [Fact]
    public void Same_title_with_a_different_shape_gets_its_own_component()
    {
        // This is the case that makes deduplication by title unsafe: across the real files,
        // 'Customer' covers both the full entity and a bare reference stub.
        var registry = new SchemaRegistry();

        var full = registry.Register(
            Schema("""
                {"title":"Customer","properties":{"customerNumber":{"type":"integer"},
                 "name":{"type":"string"},"city":{"type":"string"},"zip":{"type":"string"}}}
                """),
            "Fallback");

        var stub = registry.Register(
            Schema("""{"title":"Customer","properties":{"customerNumber":{"type":"integer"},"self":{"type":"string"}}}"""),
            "Fallback");

        Assert.NotEqual(full, stub);
        Assert.Equal(2, registry.Schemas.Count);
        Assert.Equal("Customer", full);
        Assert.Equal("CustomerReference", stub);
    }

    [Fact]
    public void Further_collisions_fall_back_to_a_numeric_suffix()
    {
        var registry = new SchemaRegistry();

        registry.Register(Schema("""{"title":"Entry","properties":{"a":{"type":"string"}}}"""), "F");
        registry.Register(Schema("""{"title":"Entry","properties":{"b":{"type":"string"},"self":{"type":"string"}}}"""), "F");
        var third = registry.Register(Schema("""{"title":"Entry","properties":{"c":{"type":"integer"}}}"""), "F");

        Assert.Equal("Entry2", third);
        Assert.Equal(3, registry.Schemas.Count);
    }

    [Fact]
    public void Collisions_are_reported_for_curation()
    {
        var registry = new SchemaRegistry();

        registry.Register(Schema("""{"title":"Entry","properties":{"a":{"type":"string"}}}"""), "F");
        registry.Register(Schema("""{"title":"Entry","properties":{"b":{"type":"string"},"self":{"type":"string"}}}"""), "F");

        var collision = Assert.Single(registry.Collisions);
        Assert.Equal("Entry", collision.Title);
        Assert.Equal("EntryReference", collision.AssignedName);
    }

    [Fact]
    public void Fallback_name_is_used_when_a_schema_has_no_title()
    {
        var registry = new SchemaRegistry();

        var name = registry.Register(Schema("""{"properties":{"a":{"type":"string"}}}"""), "customers get response");

        Assert.Equal("CustomersResponse", name);
    }

    [Fact]
    public void The_owning_resource_gets_the_unqualified_name_regardless_of_processing_order()
    {
        // 'customer-groups' sorts before 'customers', so without ownership its 27-property variant
        // would take the name 'Customer' and the real customer entity would become 'Customer2'.
        var registry = new SchemaRegistry();
        registry.HomeResources["Customer"] = "Customers";

        registry.Context = "customer-groups";
        var fromGroups = registry.Register(
            Schema("""{"title":"Customer","properties":{"customerNumber":{"type":"integer"},"name":{"type":"string"}}}"""),
            "Fallback");

        registry.Context = "customers";
        var fromCustomers = registry.Register(
            Schema("""{"title":"Customer","properties":{"customerNumber":{"type":"integer"},"city":{"type":"string"}}}"""),
            "Fallback");

        Assert.Equal("Customer", fromCustomers);
        Assert.Equal("CustomerGroupsCustomer", fromGroups);
    }

    [Fact]
    public void Context_qualification_does_not_stutter_on_the_owning_resource()
    {
        var registry = new SchemaRegistry { Context = "currencies" };

        registry.Register(Schema("""{"title":"CurrenciesCollection","properties":{"a":{"type":"string"}}}"""), "F");
        var second = registry.Register(
            Schema("""{"title":"CurrenciesCollection","properties":{"b":{"type":"integer"}}}"""), "F");

        Assert.Equal("CurrenciesCollection2", second);
        Assert.DoesNotContain("CurrenciesCurrencies", second, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Customer collection GET schema", "CustomerCollection")]
    [InlineData("Customer", "Customer")]
    [InlineData("quote lines", "QuoteLines")]
    [InlineData("Departmental Distribution Schema", "DepartmentalDistribution")]
    [InlineData("Booked invoice collection schema", "BookedInvoiceCollection")]
    public void Titles_become_readable_identifiers(string title, string expected) =>
        Assert.Equal(expected, SchemaRegistry.Identifier(title));
}
