using EConomic.Querying;
using Xunit;

namespace EConomic.Tests;

/// <summary>
/// A stand-in for a generated filter surface, shaped like the real Customers one: only filterable
/// properties, each typed to the operators e-conomic allows for it.
/// </summary>
public sealed class TestCustomerFilter
{
    [EconomicField("customerNumber")]
    public NumericField<int> CustomerNumber { get; } = null!;

    [EconomicField("name")]
    public TextField Name { get; } = null!;

    [EconomicField("city")]
    public TextField City { get; } = null!;

    [EconomicField("barred")]
    public BooleanField Barred { get; } = null!;

    [EconomicField("lastUpdated")]
    public ComparableField<DateOnly> LastUpdated { get; } = null!;

    [EconomicField("balance")]
    public NumericField<decimal> Balance { get; } = null!;

    // Nested paths are legal filter fields and have no C# equivalent, which is why the name is
    // carried by an attribute rather than derived from the property.
    [EconomicField("customerGroup.customerGroupNumber")]
    public NumericField<int> CustomerGroupNumber { get; } = null!;
}

public class FilterTranslatorTests
{
    private static string Translate(System.Linq.Expressions.Expression<Func<TestCustomerFilter, bool>> filter) =>
        FilterTranslator.Translate(filter);

    [Fact]
    public void Equality_uses_eq()
    {
        Assert.Equal("name$eq:Joe", Translate(c => c.Name == "Joe"));
        Assert.Equal("name$ne:Joe", Translate(c => c.Name != "Joe"));
    }

    [Theory]
    [InlineData("lt")]
    [InlineData("lte")]
    [InlineData("gt")]
    [InlineData("gte")]
    public void Comparisons_map_to_their_operators(string op)
    {
        var translated = op switch
        {
            "lt" => Translate(c => c.CustomerNumber < 10),
            "lte" => Translate(c => c.CustomerNumber <= 10),
            "gt" => Translate(c => c.CustomerNumber > 10),
            _ => Translate(c => c.CustomerNumber >= 10),
        };

        Assert.Equal($"customerNumber${op}:10", translated);
    }

    [Fact]
    public void And_chains_without_parentheses_at_the_top_level()
    {
        // Matches the shape e-conomic documents: name$eq:Joe$and:city$like:*port
        Assert.Equal(
            "name$eq:Joe$and:city$like:*port",
            Translate(c => c.Name == "Joe" && c.City.Like("*port")));
    }

    [Fact]
    public void Nested_groups_are_parenthesised()
    {
        Assert.Equal(
            "name$eq:Joe$and:(city$like:*port$or:customerNumber$lt:40)",
            Translate(c => c.Name == "Joe" && (c.City.Like("*port") || c.CustomerNumber < 40)));
    }

    [Fact]
    public void Or_of_two_groups_keeps_both_sides_grouped()
    {
        Assert.Equal(
            "(name$eq:a$and:city$eq:b)$or:(name$eq:c$and:city$eq:d)",
            Translate(c => (c.Name == "a" && c.City == "b") || (c.Name == "c" && c.City == "d")));
    }

    [Fact]
    public void Like_keeps_wildcards_but_escapes_everything_else() =>
        Assert.Equal("name$like:*Acme$,Ltd*", Translate(c => c.Name.Like("*Acme,Ltd*")));

    [Fact]
    public void In_and_not_in_render_bracketed_lists()
    {
        Assert.Equal("customerNumber$in:[1,2,3]", Translate(c => c.CustomerNumber.In(1, 2, 3)));
        Assert.Equal("customerNumber$nin:[4,5]", Translate(c => c.CustomerNumber.NotIn(4, 5)));
    }

    [Fact]
    public void In_rejects_more_values_than_the_server_accepts()
    {
        var tooMany = Enumerable.Range(1, NumericField<int>.MaxInValues + 1).ToArray();

        var exception = Assert.Throws<NotSupportedException>(() => Translate(c => c.CustomerNumber.In(tooMany)));

        Assert.Contains("200", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void In_rejects_an_empty_list()
    {
        var none = Array.Empty<int>();

        Assert.Throws<NotSupportedException>(() => Translate(c => c.CustomerNumber.In(none)));
    }

    [Fact]
    public void Null_is_a_value_that_follows_an_operator()
    {
        // Verified live: `name$null:` is a syntax error, and the server replies listing the
        // operators it expected. $null: has to sit where a value would.
        Assert.Equal("name$eq:$null:", Translate(c => c.Name == null));
        Assert.Equal("name$eq:$null:", Translate(c => c.Name.IsNull()));
        Assert.Equal("name$ne:$null:", Translate(c => !c.Name.IsNull()));
    }

    [Fact]
    public void Captured_variables_are_read_without_compiling_the_expression()
    {
        var wanted = "Acme";
        var minimum = 100;

        Assert.Equal("name$eq:Acme", Translate(c => c.Name == wanted));
        Assert.Equal("customerNumber$gt:100", Translate(c => c.CustomerNumber > minimum));
    }

    [Fact]
    public void Nested_field_paths_are_preserved()
    {
        Assert.Equal(
            "customerGroup.customerGroupNumber$eq:1",
            Translate(c => c.CustomerGroupNumber == 1));
    }

    [Fact]
    public void Booleans_render_lower_case() =>
        Assert.Equal("barred$eq:false", Translate(c => c.Barred == false));

    [Fact]
    public void Dates_render_as_iso_days()
    {
        var since = new DateOnly(2026, 8, 13);

        Assert.Equal("lastUpdated$gte:2026-08-13", Translate(c => c.LastUpdated >= since));
    }

    [Fact]
    // A Danish machine formats 1234.5 as "1234,5"; the comma would be parsed as a list separator.
    public void Decimals_use_invariant_formatting() =>
        Assert.Equal("balance$gt:1234.5", Translate(c => c.Balance > 1234.5m));

    [Fact]
    public void Values_are_escaped_using_the_servers_own_table()
    {
        Assert.Equal("name$eq:a$$b", Translate(c => c.Name == "a$b"));
        Assert.Equal("name$eq:a$(b$)c", Translate(c => c.Name == "a(b)c"));
        Assert.Equal("name$eq:a$,b", Translate(c => c.Name == "a,b"));
        Assert.Equal("name$eq:a$[b$]c", Translate(c => c.Name == "a[b]c"));

        // A literal asterisk in an equality value must not become a wildcard.
        Assert.Equal("name$eq:a$*b", Translate(c => c.Name == "a*b"));
    }

    [Fact]
    public void Is_null_and_is_not_null_do_not_collapse_to_the_same_query()
    {
        var isNull = Translate(c => c.Name == null);
        var isNotNull = Translate(c => c.Name != null);

        Assert.Equal("name$eq:$null:", isNull);
        Assert.Equal("name$ne:$null:", isNotNull);
        Assert.NotEqual(isNull, isNotNull);
    }

    [Fact]
    public void A_null_captured_variable_is_treated_as_a_null_comparison()
    {
        string? nothing = null;

        Assert.Equal("name$eq:$null:$and:city$ne:$null:", Translate(c => c.Name == nothing && c.City != null));
    }
}
