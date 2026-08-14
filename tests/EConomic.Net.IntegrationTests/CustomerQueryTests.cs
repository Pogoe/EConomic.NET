using System.Net.Http;
using EConomic.Authentication;
using Xunit;

namespace EConomic.IntegrationTests;

/// <summary>
/// The whole slice against the live demo agreement: query composition, transport, paging, mapping.
/// </summary>
public class CustomerQueryTests
{
    private const string OptInVariable = "ECONOMIC_RUN_INTEGRATION_TESTS";

    [Fact]
    public async Task Customers_can_be_enumerated_and_mapped()
    {
        SkipUnlessOptedIn();

        var client = CreateClient();

        var customers = new List<Rest.Customer>();
        await foreach (var customer in client.Customers.AsAsyncEnumerable(TestContext.Current.CancellationToken))
        {
            customers.Add(customer);
        }

        Assert.NotEmpty(customers);
        Assert.All(customers, c => Assert.True(c.CustomerNumber > 0));
        Assert.All(customers, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
    }

    [Fact]
    public async Task A_typed_filter_restricts_what_the_server_returns()
    {
        SkipUnlessOptedIn();

        var client = CreateClient();

        var all = await CountAsync(client.Customers.AsQuery());
        var filtered = await CountAsync(client.Customers.Where(c => c.CustomerNumber.In(1, 2, 3)));

        Assert.True(all > filtered, $"Expected the filter to narrow the result set; got {all} and {filtered}.");
        Assert.Equal(3, filtered);
    }

    [Fact]
    public async Task Ordering_is_applied_by_the_server()
    {
        SkipUnlessOptedIn();

        var client = CreateClient();

        var descending = new List<int>();
        await foreach (var customer in client.Customers
            .OrderByDescending(c => c.CustomerNumber)
            .AsAsyncEnumerable(TestContext.Current.CancellationToken))
        {
            descending.Add(customer.CustomerNumber);
        }

        Assert.NotEmpty(descending);
        Assert.Equal(descending.OrderByDescending(n => n).ToList(), descending);
    }

    [Fact]
    public async Task Paging_is_transparent_across_page_boundaries()
    {
        SkipUnlessOptedIn();

        var client = CreateClient();

        // One customer per request forces several round trips over the same collection.
        var paged = await CountAsync(client.Customers.WithPageSize(1));
        var single = await CountAsync(client.Customers.WithPageSize(1000));

        Assert.Equal(single, paged);
    }

    [Fact]
    public async Task The_raw_escape_hatch_reaches_fields_the_schema_omits()
    {
        SkipUnlessOptedIn();

        // pNumber is filterable on the server but absent from CustomerFilter, because the
        // published schema does not mark it. This is the case WhereRaw exists for.
        var client = CreateClient();

        var count = await CountAsync(client.Customers.WhereRaw("pNumber$eq:1234567890"));

        Assert.Equal(0, count);
    }

    private static async Task<int> CountAsync<TFilter, TSort>(
        Querying.EconomicQuery<Rest.Customer, TFilter, TSort> query)
    {
        var count = 0;
        await foreach (var _ in query.AsAsyncEnumerable(TestContext.Current.CancellationToken))
        {
            count++;
        }

        return count;
    }

    private static EconomicClient CreateClient() =>
        new(
            new HttpClient(new EconomicAuthenticationHandler(EconomicOptions.Demo())
            {
                InnerHandler = new HttpClientHandler(),
            })
            {
                BaseAddress = EconomicOptions.DefaultRestApiBaseAddress,
                Timeout = TimeSpan.FromSeconds(30),
            });

    private static void SkipUnlessOptedIn() =>
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable(OptInVariable) is not "1",
            $"Set {OptInVariable}=1 to run tests against the live demo agreement.");
}
