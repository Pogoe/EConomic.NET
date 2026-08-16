using System.Linq.Expressions;
using System.Net;
using System.Text.Json;
using EConomic.Querying;
using Xunit;

namespace EConomic.IntegrationTests;

/// <summary>A filter surface for customers, shaped like a generated one.</summary>
public sealed class DemoCustomerFilter
{
    [EconomicField("customerNumber")]
    public NumericField<int> CustomerNumber { get; } = null!;

    [EconomicField("name")]
    public TextField Name { get; } = null!;

    [EconomicField("barred")]
    public BooleanField Barred { get; } = null!;
}

/// <summary>
/// Checks that what the translator produces is what e-conomic actually accepts.
/// </summary>
/// <remarks>
/// Unit tests pin the translation to an expected string, which only proves it matches what we
/// believe the syntax to be. These send it to the server: a filter that parses but means something
/// else — a mis-escaped wildcard, say — shows up here and nowhere else.
/// </remarks>
public class FilterSyntaxTests
{
    [Fact]
    public async Task Every_supported_operator_is_accepted_by_the_server()
    {
        TestClients.SkipUnlessConfigured();

        // A 400 here means the translator emits syntax e-conomic does not accept.
        Expression<Func<DemoCustomerFilter, bool>>[] filters =
        [
            c => c.Name == "Decathlon",
            c => c.Name != "Decathlon",
            c => c.Name.Like("*a*"),
            c => c.CustomerNumber >= 1 && c.CustomerNumber <= 5,
            c => c.CustomerNumber.In(1, 2, 3),
            c => c.CustomerNumber.NotIn(1),
            c => c.Name == "Decathlon" && (c.Name.Like("*a*") || c.CustomerNumber < 40),
            c => c.Barred == false,
            c => c.Name == null,
        ];

        foreach (var filter in filters)
        {
            var expression = FilterTranslator.Translate(filter);
            var (status, _) = await QueryAsync(expression);

            Assert.True(status == HttpStatusCode.OK, $"Server rejected '{expression}' with {(int)status}.");
        }
    }

    [Fact]
    public async Task In_restricts_the_result_set_rather_than_merely_parsing()
    {
        TestClients.SkipUnlessConfigured();

        var client = TestClients.Create();
        await using var seed = new AgreementSeed(client, TestContext.Current.CancellationToken);

        // Two of the three are asked for, so a filter that merely parses and returns everything is
        // distinguishable from one the server actually applied.
        var first = await seed.CustomerAsync("ZZ Probe In A");
        var second = await seed.CustomerAsync("ZZ Probe In B");
        await seed.CustomerAsync("ZZ Probe In C");

        var expression = FilterTranslator.Translate<DemoCustomerFilter>(
            c => c.CustomerNumber.In(first.CustomerNumber, second.CustomerNumber));

        var (status, count) = await QueryAsync(expression);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task An_escaped_wildcard_matches_literally_instead_of_matching_everything()
    {
        TestClients.SkipUnlessConfigured();

        // If `*` leaked through unescaped this would match every customer whose name starts with
        // "NoSuch", rather than looking for a literal asterisk.
        var expression = FilterTranslator.Translate<DemoCustomerFilter>(c => c.Name == "NoSuch*Name");
        var (status, count) = await QueryAsync(expression);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task A_bracket_in_a_value_finds_the_record_it_names()
    {
        TestClients.SkipUnlessConfigured();

        var client = TestClients.Create();
        await using var seed = new AgreementSeed(client, TestContext.Current.CancellationToken);

        // e-conomic publishes `$[` and `$]` in the escape table it returns on a filter-parse
        // failure, and does not honour them: the escaped form parses cleanly and matches nothing.
        // A unit test cannot see this — it agreed with the published table, which is why the bug
        // survived. Only a record that exists settles it.
        const string Name = "ZZ Probe Br[X] Tail";
        await seed.CustomerAsync(Name);

        var expression = FilterTranslator.Translate<DemoCustomerFilter>(c => c.Name == Name);
        var (status, count) = await QueryAsync(expression);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task A_bracket_in_a_like_pattern_is_literal_rather_than_a_character_class()
    {
        TestClients.SkipUnlessConfigured();

        var client = TestClients.Create();
        await using var seed = new AgreementSeed(client, TestContext.Current.CancellationToken);

        // Under `$like:` a bracket opens a SQL character class, so an unescaped `Br[X]` matches
        // `BrX` and not `Br[X]` — the exact inverse of what the caller asked for. The two records
        // below are what tells those apart: the pattern must find the first and not the second.
        await seed.CustomerAsync("ZZ Probe Cls Br[X] Tail");
        await seed.CustomerAsync("ZZ Probe Cls BrX Tail");

        var expression = FilterTranslator.Translate<DemoCustomerFilter>(c => c.Name.Like("Cls Br[X]"));
        var (status, count) = await QueryAsync(expression);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task A_filter_on_a_property_the_schema_under_reports_still_works()
    {
        TestClients.SkipUnlessConfigured();

        // pNumber is absent from the published schema's filterable set but the server accepts it.
        // This is the case the raw escape hatch exists for; if it ever starts failing, the escape
        // hatch is no longer needed and the generated surface can be trusted alone.
        var (status, _) = await QueryAsync("pNumber$eq:1234567890");

        Assert.Equal(HttpStatusCode.OK, status);
    }

    private static async Task<(HttpStatusCode Status, int Count)> QueryAsync(string filter)
    {
        using var client = TestClients.CreateTransport();

        var uri = new Uri($"https://restapi.e-conomic.com/customers?pagesize=1000&filter={Uri.EscapeDataString(filter)}");
        using var response = await client.GetAsync(uri, TestContext.Current.CancellationToken);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return (response.StatusCode, 0);
        }

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);

        return (response.StatusCode, document.RootElement.GetProperty("collection").GetArrayLength());
    }
}
