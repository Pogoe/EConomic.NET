using EConomic.Rest;
using Xunit;

namespace EConomic.IntegrationTests;

/// <summary>
/// Creates the records a test needs and removes them again.
/// </summary>
/// <remarks>
/// <para>
/// Reading a shared agreement let the read tests assert on data nobody controlled — "five
/// customers, numbered 1 to 5" is a fact about someone else's records, and it drifts. Against an
/// agreement of one's own the tests have to bring their own data, which is better anyway: an
/// assertion about a record the test created is an assertion about the library.
/// </para>
/// <para>
/// Everything is prefixed <c>ZZ Probe</c> and deleted on disposal, in reverse order of creation —
/// a customer or product still referenced by an invoice cannot be deleted.
/// </para>
/// </remarks>
internal sealed class AgreementSeed(EconomicClient client, CancellationToken cancellationToken)
    : IAsyncDisposable
{
    private readonly List<Func<Task>> _cleanup = [];

    /// <summary>Creates a customer.</summary>
    /// <param name="name">Its name, which the caller can assert on.</param>
    /// <returns>The created customer.</returns>
    public async Task<Customer> CustomerAsync(string name = "ZZ Probe Customer")
    {
        var customer = await client.Customers.CreateAsync(
            new CustomerCreate
            {
                Name = name,
                Currency = "DKK",
                CustomerGroupNumber = await FirstCustomerGroupAsync(),
                PaymentTermsNumber = await FirstPaymentTermsAsync(),
                VatZoneNumber = await FirstVatZoneAsync(),
            },
            cancellationToken);

        _cleanup.Add(() => client.Customers.DeleteAsync(customer.CustomerNumber, cancellationToken));
        return customer;
    }

    /// <summary>Creates a product.</summary>
    /// <param name="productNumber">Its number, which is caller-supplied for products.</param>
    /// <returns>The created product.</returns>
    public async Task<Product> ProductAsync(string productNumber)
    {
        var product = await client.Products.CreateAsync(
            new ProductCreate
            {
                ProductNumber = productNumber,
                Name = "ZZ Probe Product",
                ProductGroupNumber = await FirstProductGroupAsync(),
            },
            cancellationToken);

        _cleanup.Add(() => client.Products.DeleteAsync(product.ProductNumber, cancellationToken));
        return product;
    }

    /// <summary>Creates a draft invoice, with one line when a product is supplied.</summary>
    /// <param name="customer">The customer to invoice.</param>
    /// <param name="product">A product to put on a line, or <see langword="null"/> for no lines.</param>
    /// <param name="recipientName">The recipient's name, which the caller can assert on.</param>
    /// <param name="quantity">How many of the product.</param>
    /// <param name="unitNetPrice">The unit price before VAT.</param>
    /// <returns>The created draft.</returns>
    public async Task<DraftInvoice> DraftInvoiceAsync(
        Customer customer,
        Product? product = null,
        string recipientName = "ZZ Probe Recipient",
        decimal quantity = 2,
        decimal unitNetPrice = 100)
    {
        ArgumentNullException.ThrowIfNull(customer);

        var invoice = await client.DraftInvoices.CreateAsync(
            new DraftInvoiceCreate
            {
                Date = new DateOnly(2026, 8, 14),
                Currency = "DKK",
                LayoutNumber = await FirstLayoutAsync(),
                CustomerNumber = customer.CustomerNumber,
                PaymentTerms = new DraftInvoiceCreatePaymentTerms
                {
                    PaymentTermsNumber = await FirstPaymentTermsAsync(),
                },
                Recipient = new DraftInvoiceCreateRecipient
                {
                    Name = recipientName,
                    VatZoneNumber = await FirstVatZoneAsync(),
                    City = "Ringsted",
                },
                Notes = new DraftInvoiceCreateNotes { Heading = "ZZ Probe" },
                Lines = product is null
                    ? []
                    : [
                        new DraftInvoiceCreateLine
                        {
                            Description = "ZZ Probe line",
                            Product = new DraftInvoiceCreateLineProduct { ProductNumber = product.ProductNumber },
                            Quantity = quantity,
                            UnitNetPrice = unitNetPrice,
                        },
                    ],
            },
            cancellationToken);

        _cleanup.Add(() => client.DraftInvoices.DeleteAsync(invoice.DraftInvoiceNumber, cancellationToken));
        return invoice;
    }

    /// <summary>Forgets a record, for one the test has already removed or deliberately kept.</summary>
    /// <remarks>Booking consumes a draft, so trying to delete it afterwards would fail the teardown.</remarks>
    public void Forget() => _cleanup.RemoveAt(_cleanup.Count - 1);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Reverse order: an invoice has to go before the customer and product it references.
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            await _cleanup[i]().ConfigureAwait(false);
        }
    }

    private async Task<int> FirstCustomerGroupAsync() =>
        (await FirstPageAsync(client.CustomerGroups.AsQuery())).CustomerGroupNumber;

    private async Task<int> FirstPaymentTermsAsync() =>
        (await FirstPageAsync(client.PaymentTerms.AsQuery())).PaymentTermsNumber;

    private async Task<int> FirstVatZoneAsync() =>
        (await FirstPageAsync(client.VatZones)).VatZoneNumber;

    private async Task<int> FirstProductGroupAsync() =>
        (await FirstPageAsync(client.ProductGroups)).ProductGroupNumber;

    private async Task<int> FirstLayoutAsync() =>
        (await FirstPageAsync(client.Layouts)).LayoutNumber;

    /// <summary>
    /// The first item of a collection e-conomic seeds itself. Which numbers those carry differs per
    /// agreement — a fresh one does not necessarily have a customer group 1 — so nothing here is
    /// hard-coded.
    /// </summary>
    private static async Task<T> FirstPageAsync<T, TFilter, TSort>(
        Querying.EconomicQuery<T, TFilter, TSort> query)
    {
        var page = await query.WithPageSize(1).GetPageAsync(0, TestContext.Current.CancellationToken);

        Assert.SkipWhen(
            page.Items.Count == 0,
            $"The agreement has no {typeof(T).Name}, which e-conomic normally seeds. Nothing to test against.");

        return page.Items[0];
    }
}
