using System.Net.Http;
using EConomic.Authentication;
using Xunit;

namespace EConomic.IntegrationTests;

/// <summary>
/// Exercises resources beyond Customers, which the facade generator produced rather than a person.
/// </summary>
/// <remarks>
/// Customers was hand-written first and is covered in detail elsewhere. What matters here is that
/// the generated resources are wired correctly — right client, right method, right mapping — since
/// a mistake in the generator would repeat itself twenty times over.
/// </remarks>
public class ReplicatedResourceTests
{
    private const string OptInVariable = "ECONOMIC_RUN_INTEGRATION_TESTS";

    [Fact]
    public async Task Accounts_are_fetched_and_mapped()
    {
        SkipUnlessOptedIn();

        var accounts = await FirstPageAsync(CreateClient().Accounts);

        Assert.NotEmpty(accounts);
        Assert.All(accounts, a => Assert.True(a.AccountNumber > 0));
        Assert.All(accounts, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
    }

    [Fact]
    public async Task Suppliers_are_fetched_and_mapped()
    {
        SkipUnlessOptedIn();

        var suppliers = await FirstPageAsync(CreateClient().Suppliers.AsQuery());

        Assert.All(suppliers, s => Assert.True(s.SupplierNumber > 0));
    }

    [Fact]
    public async Task Products_are_fetched_and_mapped()
    {
        SkipUnlessOptedIn();

        var products = await FirstPageAsync(CreateClient().Products.AsQuery());

        Assert.All(products, p => Assert.False(string.IsNullOrWhiteSpace(p.ProductNumber)));
    }

    [Fact]
    public async Task A_typed_filter_works_on_a_generated_resource()
    {
        SkipUnlessOptedIn();

        // Accounts is a different client, method and filter surface than Customers, so this
        // confirms the generator wired the whole chain rather than just the one it was modelled on.
        var client = CreateClient();

        var page = await client.Accounts
            .Where(a => a.AccountNumber >= 1000)
            .OrderBy(a => a.AccountNumber)
            .GetPageAsync(0, TestContext.Current.CancellationToken);

        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, a => Assert.True(a.AccountNumber >= 1000));

        var numbers = page.Items.Select(a => a.AccountNumber).ToList();
        Assert.Equal(numbers.OrderBy(n => n).ToList(), numbers);
    }

    [Fact]
    public async Task Currencies_have_no_filterable_fields_and_still_enumerate()
    {
        SkipUnlessOptedIn();

        // An empty filter surface is a real answer, not a gap: this resource simply cannot be
        // filtered. Enumeration must still work.
        var currencies = await FirstPageAsync(CreateClient().Currencies);

        Assert.NotEmpty(currencies);
    }

    private static async Task<IReadOnlyList<T>> FirstPageAsync<T, TFilter, TSort>(
        Querying.EconomicQuery<T, TFilter, TSort> query)
    {
        var page = await query.WithPageSize(20).GetPageAsync(0, TestContext.Current.CancellationToken);
        return page.Items;
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
