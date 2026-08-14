using Xunit;

namespace EConomic.IntegrationTests;

/// <summary>
/// The whole slice against a live agreement: query composition, transport, paging, mapping.
/// </summary>
/// <remarks>
/// Each test creates the customers it asserts on. Reading whatever the agreement happened to
/// contain meant asserting on data the test did not control, which is how "the demo agreement has
/// five customers, numbered 1 to 5" ended up compiled into a test.
/// </remarks>
public class CustomerQueryTests
{
    [Fact]
    public async Task Customers_can_be_enumerated_and_mapped()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;

        await using var seed = new AgreementSeed(client, token);
        var created = await seed.CustomerAsync("ZZ Probe Enumerated");

        var customers = await ToListAsync(client.Rest.Customers.AsQuery());

        Assert.Contains(customers, c => c.CustomerNumber == created.CustomerNumber);
        Assert.All(customers, c => Assert.True(c.CustomerNumber > 0));
        Assert.All(customers, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
    }

    [Fact]
    public async Task A_typed_filter_restricts_what_the_server_returns()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;

        await using var seed = new AgreementSeed(client, token);
        var first = await seed.CustomerAsync("ZZ Probe Filter A");
        var second = await seed.CustomerAsync("ZZ Probe Filter B");
        await seed.CustomerAsync("ZZ Probe Filter C");

        // Filtering on the two the test created, rather than on numbers it hopes exist. Customer
        // numbers are server-assigned, so they cannot be predicted — only read back.
        var all = await CountAsync(client.Rest.Customers.AsQuery());
        var filtered = await ToListAsync(
            client.Rest.Customers.Where(c => c.CustomerNumber.In(first.CustomerNumber, second.CustomerNumber)));

        Assert.True(all > filtered.Count, $"Expected the filter to narrow the result set; got {all} and {filtered.Count}.");
        Assert.Equal(2, filtered.Count);
        Assert.All(filtered, c => Assert.Contains(c.CustomerNumber, new[] { first.CustomerNumber, second.CustomerNumber }));
    }

    [Fact]
    public async Task Ordering_is_applied_by_the_server()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;

        await using var seed = new AgreementSeed(client, token);
        await seed.CustomerAsync("ZZ Probe Order A");
        await seed.CustomerAsync("ZZ Probe Order B");

        var descending = (await ToListAsync(client.Rest.Customers.OrderByDescending(c => c.CustomerNumber)))
            .Select(c => c.CustomerNumber)
            .ToList();

        Assert.NotEmpty(descending);
        Assert.Equal(descending.OrderByDescending(n => n).ToList(), descending);
    }

    [Fact]
    public async Task Paging_is_transparent_across_page_boundaries()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;

        await using var seed = new AgreementSeed(client, token);
        await seed.CustomerAsync("ZZ Probe Paging A");
        await seed.CustomerAsync("ZZ Probe Paging B");
        await seed.CustomerAsync("ZZ Probe Paging C");

        // One customer per request forces several round trips over the same collection, which is
        // only a real test if the collection has more than one page in it.
        var paged = await CountAsync(client.Rest.Customers.WithPageSize(1));
        var single = await CountAsync(client.Rest.Customers.WithPageSize(1000));

        Assert.True(single >= 3, $"Expected the seeded customers to be present; found {single}.");
        Assert.Equal(single, paged);
    }

    [Fact]
    public async Task The_raw_escape_hatch_reaches_fields_the_schema_omits()
    {
        TestClients.SkipUnlessConfigured();

        // pNumber is filterable on the server but absent from CustomerFilter, because the
        // published schema does not mark it. This is the case WhereRaw exists for: a 400 here would
        // mean the server does not accept the field after all.
        var client = CreateClient();

        var count = await CountAsync(client.Rest.Customers.WhereRaw("pNumber$eq:1234567890"));

        Assert.Equal(0, count);
    }

    private static async Task<int> CountAsync<TFilter, TSort>(
        Querying.EconomicQuery<Rest.Customer, TFilter, TSort> query) =>
        (await ToListAsync(query)).Count;

    private static async Task<List<Rest.Customer>> ToListAsync<TFilter, TSort>(
        Querying.EconomicQuery<Rest.Customer, TFilter, TSort> query)
    {
        var items = new List<Rest.Customer>();
        await foreach (var customer in query.AsAsyncEnumerable(TestContext.Current.CancellationToken))
        {
            items.Add(customer);
        }

        return items;
    }

    private static EconomicClient CreateClient() => TestClients.Create();
}
