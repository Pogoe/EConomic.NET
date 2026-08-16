using Xunit;

namespace EConomic.IntegrationTests;

/// <summary>
/// Exercises resources beyond Customers, which the facade generator produced rather than a person.
/// </summary>
/// <remarks>
/// Customers was hand-written first and is covered in detail elsewhere. What matters here is that
/// the generated resources are wired correctly — right client, right method, right mapping — since
/// a mistake in the generator would repeat itself thirty times over.
/// </remarks>
public class ReplicatedResourceTests
{
    [Fact]
    public async Task Accounts_are_fetched_and_mapped()
    {
        TestClients.SkipUnlessConfigured();

        // The chart of accounts is seeded by e-conomic, so this needs nothing of its own.
        var accounts = await FirstPageAsync(CreateClient().Rest.Accounts.AsQuery());

        Assert.NotEmpty(accounts);
        Assert.All(accounts, a => Assert.True(a.AccountNumber > 0));
        Assert.All(accounts, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
    }

    [Fact]
    public async Task Suppliers_are_fetched_and_mapped()
    {
        TestClients.SkipUnlessConfigured();

        var suppliers = await FirstPageAsync(CreateClient().Rest.Suppliers.AsQuery());

        Assert.All(suppliers, s => Assert.True(s.SupplierNumber > 0));
    }

    [Fact]
    public async Task A_typed_filter_works_on_a_generated_resource()
    {
        TestClients.SkipUnlessConfigured();

        // Accounts is a different client, method and filter surface than Customers, so this
        // confirms the generator wired the whole chain rather than just the one it was modelled on.
        var client = CreateClient();

        var page = await client.Rest.Accounts
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
        TestClients.SkipUnlessConfigured();

        // An empty filter surface is a real answer, not a gap: this resource simply cannot be
        // filtered. Enumeration must still work.
        var currencies = await FirstPageAsync(CreateClient().Rest.Currencies.AsQuery());

        Assert.NotEmpty(currencies);
    }

    [Fact]
    public async Task Invoices_orders_and_quotes_are_fetched_and_mapped()
    {
        TestClients.SkipUnlessConfigured();

        // These live one segment below a namespace — /invoices/drafts rather than /invoices — so
        // they were invisible to a discovery pass that only looked at single-segment collections.
        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;

        await using var seed = new AgreementSeed(client, token);
        var customer = await seed.CustomerAsync();
        var created = await seed.DraftInvoiceAsync(customer);

        var drafts = await FirstPageAsync(client.Rest.DraftInvoices.AsQuery());

        Assert.Contains(drafts, i => i.DraftInvoiceNumber == created.DraftInvoiceNumber);
        Assert.All(drafts, i => Assert.True(i.DraftInvoiceNumber > 0));
        Assert.All(drafts, i => Assert.NotNull(i.Date));

        // The order and quote collections come from the same templates, so an empty page still
        // proves the client, method and envelope were wired correctly.
        var orders = await FirstPageAsync(client.Rest.DraftOrders.AsQuery());
        Assert.All(orders, o => Assert.True(o.OrderNumber > 0));

        var quotes = await FirstPageAsync(client.Rest.DraftQuotes.AsQuery());
        Assert.All(quotes, q => Assert.True(q.QuoteNumber > 0));
    }

    [Fact]
    public async Task Invoice_amounts_carry_their_decimals()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;

        await using var seed = new AgreementSeed(client, token);
        var customer = await seed.CustomerAsync();
        var product = await seed.ProductAsync("ZZ-PROBE-DEC");

        // The schema types both amounts as a bare "number", so they land in decimal rather than
        // double on the public model. Asking for a price that is not a round binary fraction is the
        // point: floating-point drift would show up in this assertion and nowhere else.
        var invoice = await seed.DraftInvoiceAsync(customer, product, quantity: 3, unitNetPrice: 33.33m);

        Assert.Equal(99.99m, invoice.NetAmount);
        Assert.True(invoice.GrossAmount > invoice.NetAmount);
    }

    [Fact]
    public async Task Composite_properties_are_mapped_rather_than_dropped()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;

        await using var seed = new AgreementSeed(client, token);
        var customer = await seed.CustomerAsync();
        var created = await seed.DraftInvoiceAsync(customer, recipientName: "ZZ Probe Composite");

        // An invoice's recipient is a nested object, not a reference: it carries the delivery name
        // and address in full. Until the facade could express one, these properties were absent
        // from the public model altogether — the worst kind of gap, because nothing failed.
        var invoice = Assert.Single(
            await FirstPageAsync(client.Rest.DraftInvoices.AsQuery()),
            i => i.DraftInvoiceNumber == created.DraftInvoiceNumber);

        Assert.Equal("ZZ Probe Composite", invoice.Recipient?.Name);
        Assert.Equal("Ringsted", invoice.Recipient?.City);

        // A reference nested inside a nested object still flattens to a reference.
        Assert.NotNull(invoice.Recipient!.VatZone);
        Assert.True(invoice.Recipient.VatZone!.Number > 0);

        // paymentTerms has four properties, so it is not the {number, self} reference shape. It
        // carries the credit period inline, which a flattened reference would have thrown away.
        Assert.NotNull(invoice.PaymentTerms);
        Assert.True(invoice.PaymentTerms!.PaymentTermsNumber > 0);
    }

    [Fact]
    public async Task Arrays_of_objects_are_mapped()
    {
        TestClients.SkipUnlessConfigured();

        // A summing account is defined by the intervals it sums, so a model without them describes
        // almost nothing about it. These are part of the chart of accounts e-conomic seeds, which
        // is why this needs no fixture of its own.
        var accounts = await CreateClient().Rest.Accounts
            .Where(a => a.AccountNumber > 0)
            .WithPageSize(1000)
            .GetPageAsync(0, TestContext.Current.CancellationToken);

        var summing = Assert.Single(accounts.Items.Where(a => a.AccountsSummed.Count > 0).Take(1));

        Assert.All(summing.AccountsSummed, s => Assert.NotNull(s.FromAccount));
        Assert.All(summing.AccountsSummed, s => Assert.NotNull(s.ToAccount));
        Assert.All(summing.AccountsSummed, s => Assert.True(s.ToAccount!.Number >= s.FromAccount!.Number));
    }

    [Fact]
    public async Task Arrays_of_references_are_mapped()
    {
        TestClients.SkipUnlessConfigured();

        // An array whose items are the {number, self} reference shape collapses to a list of
        // references rather than to a record per element.
        var roles = await FirstPageAsync(CreateClient().Rest.AppRoles.AsQuery());

        var withModules = Assert.Single(roles.Where(r => r.RequiredModules.Count > 0).Take(1));

        Assert.All(withModules.RequiredModules, m => Assert.True(m.Number > 0));
        Assert.All(withModules.RequiredModules, m => Assert.NotNull(m.Self));
    }

    [Fact]
    public async Task A_product_carries_its_product_group()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;

        await using var seed = new AgreementSeed(client, token);
        var created = await seed.ProductAsync("ZZ-PROBE-GROUP");

        // productGroup has more than the three properties a reference has, so it was skipped
        // entirely rather than flattened. It is the property that says what a product *is*.
        var product = Assert.Single(
            await FirstPageAsync(client.Rest.Products.AsQuery()),
            p => p.ProductNumber == created.ProductNumber);

        Assert.NotNull(product.ProductGroup);
        Assert.True(product.ProductGroup!.ProductGroupNumber > 0);
        Assert.False(string.IsNullOrWhiteSpace(product.ProductGroup.Name));
    }

    private static async Task<IReadOnlyList<T>> FirstPageAsync<T, TFilter, TSort>(
        Querying.EconomicQuery<T, TFilter, TSort> query)
    {
        var page = await query.WithPageSize(50).GetPageAsync(0, TestContext.Current.CancellationToken);
        return page.Items;
    }

    private static EconomicClient CreateClient() => TestClients.Create();
}
