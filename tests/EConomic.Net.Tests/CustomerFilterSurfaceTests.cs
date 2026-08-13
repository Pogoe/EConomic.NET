using System.Reflection;
using EConomic.Querying;
using EConomic.Rest;
using Xunit;

namespace EConomic.Tests;

/// <summary>
/// Tests the generated surface, not a stand-in: these assert what consumers can and cannot write.
/// </summary>
public class CustomerFilterSurfaceTests
{
    [Fact]
    public void The_generated_surface_translates()
    {
        var filter = FilterTranslator.Translate<CustomerFilter>(
            c => c.CustomerNumber > 1000 && c.Name.Like("Acme*"));

        Assert.Equal("customerNumber$gt:1000$and:name$like:Acme*", filter);
    }

    [Fact]
    public void A_nested_field_keeps_its_dotted_path()
    {
        var filter = FilterTranslator.Translate<CustomerFilter>(c => c.CustomerGroupNumber == 1);

        Assert.Equal("customerGroup.customerGroupNumber$eq:1", filter);
    }

    [Fact]
    public void Non_filterable_properties_are_absent_from_the_surface()
    {
        // Customer has 36 properties; only the filterable ones may appear here. A property that is
        // present on the entity but not on the filter is the compile-time guard doing its job.
        Assert.NotNull(typeof(CustomerFilter).GetProperty("Name"));
        Assert.Null(typeof(CustomerFilter).GetProperty("Attention"));
        Assert.Null(typeof(CustomerFilter).GetProperty("PaymentTerms"));

        // pNumber is the known gap: the schema does not mark it filterable but the server accepts
        // it, which is exactly why a raw escape hatch has to exist alongside this surface.
        Assert.Null(typeof(CustomerFilter).GetProperty("PNumber"));
    }

    [Fact]
    public void Operator_sets_match_the_property_type()
    {
        Assert.Equal(typeof(NumericField<int>), typeof(CustomerFilter).GetProperty("CustomerNumber")!.PropertyType);
        Assert.Equal(typeof(NumericField<decimal>), typeof(CustomerFilter).GetProperty("Balance")!.PropertyType);
        Assert.Equal(typeof(TextField), typeof(CustomerFilter).GetProperty("Name")!.PropertyType);
        Assert.Equal(typeof(BooleanField), typeof(CustomerFilter).GetProperty("Barred")!.PropertyType);
        // A timestamp, not a date: e-conomic labels lastUpdated "full-date" but its own pattern
        // and its responses are full ISO-8601, and it filters to the second.
        Assert.Equal(
            typeof(ComparableField<DateTimeOffset>),
            typeof(CustomerFilter).GetProperty("LastUpdated")!.PropertyType);
    }

    [Fact]
    public void Every_property_carries_the_name_e_conomic_expects()
    {
        foreach (var property in typeof(CustomerFilter).GetProperties())
        {
            var attribute = property.GetCustomAttribute<EconomicFieldAttribute>();

            Assert.True(attribute is not null, $"{property.Name} has no {nameof(EconomicFieldAttribute)}.");
            Assert.False(string.IsNullOrWhiteSpace(attribute!.Name));
        }
    }

    [Fact]
    public void Sorting_uses_its_own_surface_because_sortability_is_a_separate_flag()
    {
        Assert.Equal("name", SortTranslator.FieldName<CustomerSort>(c => c.Name));

        // Barred is filterable but not sortable, so it is absent from the sort surface.
        Assert.NotNull(typeof(CustomerFilter).GetProperty("Barred"));
        Assert.Null(typeof(CustomerSort).GetProperty("Barred"));
    }

    [Fact]
    public void Sort_clauses_render_in_e_conomic_syntax()
    {
        var clauses = new[]
        {
            new SortClause(SortTranslator.FieldName<CustomerSort>(c => c.Name), SortDirection.Descending),
            new SortClause(SortTranslator.FieldName<CustomerSort>(c => c.City), SortDirection.Ascending),
        };

        Assert.Equal("-name,city", SortTranslator.Render(clauses));
    }
}
