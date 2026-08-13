using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using EConomic.Authentication;
using EConomic.Querying;
using Xunit;

namespace EConomic.IntegrationTests;

/// <summary>A filter surface for the demo agreement's customers, shaped like a generated one.</summary>
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
    private const string OptInVariable = "ECONOMIC_RUN_INTEGRATION_TESTS";

    [Fact]
    public async Task Every_supported_operator_is_accepted_by_the_server()
    {
        SkipUnlessOptedIn();

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

    [Theory]
    // Values are compared against the live demo agreement, which has five customers.
    [InlineData(3)]
    public async Task In_restricts_the_result_set_rather_than_merely_parsing(int expected)
    {
        SkipUnlessOptedIn();

        var expression = FilterTranslator.Translate<DemoCustomerFilter>(c => c.CustomerNumber.In(1, 2, 3));
        var (status, count) = await QueryAsync(expression);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(expected, count);
    }

    [Fact]
    public async Task An_escaped_wildcard_matches_literally_instead_of_matching_everything()
    {
        SkipUnlessOptedIn();

        // If `*` leaked through unescaped this would match every customer whose name starts with
        // "NoSuch", rather than looking for a literal asterisk.
        var expression = FilterTranslator.Translate<DemoCustomerFilter>(c => c.Name == "NoSuch*Name");
        var (status, count) = await QueryAsync(expression);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task A_filter_on_a_property_the_schema_under_reports_still_works()
    {
        SkipUnlessOptedIn();

        // pNumber is absent from the published schema's filterable set but the server accepts it.
        // This is the case the raw escape hatch exists for; if it ever starts failing, the escape
        // hatch is no longer needed and the generated surface can be trusted alone.
        var (status, _) = await QueryAsync("pNumber$eq:1234567890");

        Assert.Equal(HttpStatusCode.OK, status);
    }

    private static async Task<(HttpStatusCode Status, int Count)> QueryAsync(string filter)
    {
        using var client = new HttpClient(new EconomicAuthenticationHandler(EconomicOptions.Demo())
        {
            InnerHandler = new HttpClientHandler(),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

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

    private static void SkipUnlessOptedIn() =>
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable(OptInVariable) is not "1",
            $"Set {OptInVariable}=1 to run tests against the live demo agreement.");
}
