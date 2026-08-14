using System.Net.Http;
using EConomic.Authentication;
using EConomic.Exceptions;
using EConomic.Rest;
using Xunit;

namespace EConomic.IntegrationTests;

/// <summary>
/// Exercises create, update and delete against a live agreement.
/// </summary>
/// <remarks>
/// <para>
/// Everything these create is prefixed <c>ZZ Probe</c> and deleted again, but a failed run can
/// leave a record behind — which is why the whole suite wants a throwaway agreement.
/// </para>
/// <para>
/// The one exception is booking, which cannot be undone and so needs its own opt-in. Everything
/// else in this class cleans up after itself.
/// </para>
/// </remarks>
public class WriteRoundTripTests
{
    private const string BookingOptInVariable = "ECONOMIC_RUN_BOOKING_TESTS";

    [Fact]
    public async Task A_customer_survives_a_create_update_delete_round_trip()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;
        Customer? created = null;

        try
        {
            created = await client.Customers.CreateAsync(
                new CustomerCreate
                {
                    Name = "ZZ Probe Round Trip",
                    Currency = "DKK",
                    CustomerGroupNumber = 1,
                    PaymentTermsNumber = 1,
                    VatZoneNumber = 1,
                    City = "Ringsted",
                },
                token);

            // The create schema declares neither of these; the server sends both.
            Assert.True(created.CustomerNumber > 0);
            Assert.NotNull(created.Self);
            Assert.Equal("ZZ Probe Round Trip", created.Name);
            Assert.Equal("Ringsted", created.City);

            var updated = await client.Customers.UpdateAsync(
                created.CustomerNumber,
                new CustomerUpdate
                {
                    Name = "ZZ Probe Round Trip (updated)",
                    Currency = "DKK",
                    CustomerGroupNumber = 1,
                    PaymentTermsNumber = 1,
                    VatZoneNumber = 1,
                },
                token);

            Assert.Equal("ZZ Probe Round Trip (updated)", updated.Name);

            // PUT replaces rather than patches, so the city omitted above must now be gone.
            Assert.True(string.IsNullOrEmpty(updated.City), $"Expected city to be cleared, got '{updated.City}'.");
        }
        finally
        {
            if (created is not null)
            {
                await client.Customers.DeleteAsync(created.CustomerNumber, token);
            }
        }

        // Deleting twice is what reports the record as gone. Updating it would not: PUT is an
        // upsert, covered separately below.
        var gone = await Assert.ThrowsAsync<EconomicApiException>(
            () => client.Customers.DeleteAsync(created!.CustomerNumber, token));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Updating_a_customer_that_does_not_exist_creates_it()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;

        // PUT is an upsert, which neither the schemas nor the documentation mention: sending one
        // for an identifier that does not exist answers 201 Created and the resource appears.
        // Anything relying on PUT to fail for a missing record is therefore wrong.
        const int number = 999;

        var upserted = await client.Customers.UpdateAsync(
            number,
            new CustomerUpdate
            {
                Name = "ZZ Probe Upsert",
                Currency = "DKK",
                CustomerGroupNumber = 1,
                PaymentTermsNumber = 1,
                VatZoneNumber = 1,
            },
            token);

        try
        {
            Assert.Equal(number, upserted.CustomerNumber);
            Assert.Equal("ZZ Probe Upsert", upserted.Name);
        }
        finally
        {
            await client.Customers.DeleteAsync(number, token);
        }
    }

    [Fact]
    public async Task A_unit_can_be_created_even_though_its_schema_describes_no_identifier()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;
        Unit? created = null;

        try
        {
            // UnitsCreate declares only `name`. The server returns unitNumber and self as well,
            // which is why this method exists at all.
            created = await client.Units.CreateAsync(new UnitCreate { Name = "ZZ Probe Unit" }, token);

            Assert.True(created.UnitNumber > 0);
            Assert.NotNull(created.Self);
            Assert.Equal("ZZ Probe Unit", created.Name);
        }
        finally
        {
            if (created is not null)
            {
                await client.Units.DeleteAsync(created.UnitNumber, token);
            }
        }
    }

    [Fact]
    public async Task A_product_keeps_an_alphanumeric_number_through_the_round_trip()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;
        const string number = "ZZ-PROBE-1";
        var created = false;

        try
        {
            // The path parameter used to be typed as an integer, which would have made this
            // unrepresentable. Product numbers are strings, and the server accepts them in the path.
            var product = await client.Products.CreateAsync(
                new ProductCreate { ProductNumber = number, Name = "ZZ Probe Product", ProductGroupNumber = 1 },
                token);

            created = true;
            Assert.Equal(number, product.ProductNumber);

            var updated = await client.Products.UpdateAsync(
                number,
                new ProductUpdate { Name = "ZZ Probe Product (updated)", ProductGroupNumber = 1 },
                token);

            Assert.Equal(number, updated.ProductNumber);
            Assert.Equal("ZZ Probe Product (updated)", updated.Name);
        }
        finally
        {
            if (created)
            {
                await client.Products.DeleteAsync(number, token);
            }
        }
    }

    [Fact]
    public async Task Deleting_twice_reports_the_second_attempt_as_missing()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;

        var created = await client.Units.CreateAsync(new UnitCreate { Name = "ZZ Probe Delete Twice" }, token);
        await client.Units.DeleteAsync(created.UnitNumber, token);

        // e-conomic documents delete as non-idempotent, and identifiers are reused, which together
        // are why the retry handler refuses to repeat a delete that carries no idempotency key.
        var second = await Assert.ThrowsAsync<EconomicApiException>(
            () => client.Units.DeleteAsync(created.UnitNumber, token));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task A_payment_term_round_trips_with_its_enum_typed_property()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;
        PaymentTerms? created = null;

        try
        {
            // paymentTermsType is an enum in the generated layer and required by e-conomic, so a
            // create is impossible without it. The public model takes the string the API uses.
            created = await client.PaymentTerms.CreateAsync(
                new PaymentTermsCreate { Name = "ZZ Probe Terms", PaymentTermsType = "net", DaysOfCredit = 14 },
                token);

            Assert.True(created.PaymentTermsNumber > 0);
            Assert.Equal("ZZ Probe Terms", created.Name);
            Assert.Equal("net", created.PaymentTermsType, ignoreCase: true);
        }
        finally
        {
            if (created is not null)
            {
                await client.PaymentTerms.DeleteAsync(created.PaymentTermsNumber, token);
            }
        }
    }

    [Fact]
    public async Task A_customer_group_requires_a_caller_supplied_number()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;
        const int number = 90;

        // Unlike customers, whose number the server assigns, a customer group create is rejected
        // without one — which is why the number is required on the create model.
        var created = await client.CustomerGroups.CreateAsync(
            new CustomerGroupCreate { CustomerGroupNumber = number, Name = "ZZ Probe Group", AccountNumber = 5600 },
            token);

        try
        {
            Assert.Equal(number, created.CustomerGroupNumber);
            Assert.Equal("ZZ Probe Group", created.Name);
        }
        finally
        {
            await client.CustomerGroups.DeleteAsync(number, token);
        }
    }

    [Fact]
    public async Task A_supplier_round_trips()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;
        Supplier? created = null;

        try
        {
            created = await client.Suppliers.CreateAsync(
                new SupplierCreate
                {
                    Name = "ZZ Probe Supplier",
                    Currency = "DKK",
                    SupplierGroupNumber = 1,
                    PaymentTermsNumber = 1,
                    VatZoneNumber = 1,
                },
                token);

            Assert.True(created.SupplierNumber > 0);
            Assert.NotNull(created.Self);

            var updated = await client.Suppliers.UpdateAsync(
                created.SupplierNumber,
                new SupplierUpdate
                {
                    Name = "ZZ Probe Supplier (updated)",
                    Currency = "DKK",
                    SupplierGroupNumber = 1,
                    PaymentTermsNumber = 1,
                    VatZoneNumber = 1,
                },
                token);

            Assert.Equal("ZZ Probe Supplier (updated)", updated.Name);
        }
        finally
        {
            if (created is not null)
            {
                await client.Suppliers.DeleteAsync(created.SupplierNumber, token);
            }
        }
    }

    [Fact]
    public async Task A_customers_contacts_and_delivery_locations_round_trip()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;
        Customer? customer = null;

        try
        {
            customer = await client.Customers.CreateAsync(
                new CustomerCreate
                {
                    Name = "ZZ Probe Nested Host",
                    Currency = "DKK",
                    CustomerGroupNumber = 1,
                    PaymentTermsNumber = 1,
                    VatZoneNumber = 1,
                },
                token);

            // A nested collection is reached through its parent rather than off the client, because
            // it cannot be addressed without the parent's identifier.
            var contacts = client.Customers.Contacts(customer.CustomerNumber);

            var contact = await contacts.CreateAsync(
                new CustomerContactCreate { Name = "ZZ Probe Contact", Email = "contact@example.com" },
                token);

            Assert.True(contact.CustomerContactNumber > 0);
            Assert.Equal("ZZ Probe Contact", contact.Name);

            var renamed = await contacts.UpdateAsync(
                contact.CustomerContactNumber,
                new CustomerContactUpdate { Name = "ZZ Probe Contact (updated)" },
                token);

            Assert.Equal("ZZ Probe Contact (updated)", renamed.Name);

            var page = await contacts.GetPageAsync(0, token);
            Assert.Contains(page.Items, c => c.CustomerContactNumber == contact.CustomerContactNumber);

            await contacts.DeleteAsync(contact.CustomerContactNumber, token);

            var locations = client.Customers.DeliveryLocations(customer.CustomerNumber);
            var location = await locations.CreateAsync(
                new DeliveryLocationCreate { Address = "Odinsparken 4", City = "Ringsted", PostalCode = "4100" },
                token);

            Assert.True(location.DeliveryLocationNumber > 0);
            await locations.DeleteAsync(location.DeliveryLocationNumber, token);
        }
        finally
        {
            if (customer is not null)
            {
                await client.Customers.DeleteAsync(customer.CustomerNumber, token);
            }
        }
    }

    [Fact]
    public async Task A_draft_invoice_survives_a_create_update_delete_round_trip()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;
        DraftInvoice? created = null;
        Customer? customer = null;
        Product? product = null;

        try
        {
            // A draft invoice needs a customer, a product and a layout to point at, and a fresh
            // agreement has none of the first two. Creating them here keeps the test independent of
            // whatever happens to be in the agreement.
            var layouts = await client.Layouts.GetPageAsync(0, token);
            var layout = layouts.Items[0].LayoutNumber;

            customer = await client.Customers.CreateAsync(
                new CustomerCreate
                {
                    Name = "ZZ Probe Invoice Customer",
                    Currency = "DKK",
                    CustomerGroupNumber = 1,
                    PaymentTermsNumber = 1,
                    VatZoneNumber = 1,
                },
                token);

            product = await client.Products.CreateAsync(
                new ProductCreate
                {
                    ProductNumber = "ZZ-PROBE-INV",
                    Name = "ZZ Probe product",
                    ProductGroupNumber = 1,
                },
                token);

            created = await client.DraftInvoices.CreateAsync(
                new DraftInvoiceCreate
                {
                    Date = new DateOnly(2026, 8, 14),
                    Currency = "DKK",
                    LayoutNumber = layout,
                    CustomerNumber = customer.CustomerNumber,
                    PaymentTerms = new DraftInvoiceCreatePaymentTerms { PaymentTermsNumber = 1 },
                    Recipient = new DraftInvoiceCreateRecipient
                    {
                        Name = "ZZ Probe Recipient",
                        VatZoneNumber = 1,
                        City = "Ringsted",
                    },
                    Notes = new DraftInvoiceCreateNotes { Heading = "ZZ Probe" },
                    Lines =
                    [
                        new DraftInvoiceCreateLine
                        {
                            Description = "ZZ Probe line",
                            Product = new DraftInvoiceCreateLineProduct { ProductNumber = product.ProductNumber },
                            Quantity = 2,
                            UnitNetPrice = 100,
                        },
                    ],
                },
                token);

            Assert.True(created.DraftInvoiceNumber > 0);
            Assert.NotNull(created.Self);
            Assert.Equal("ZZ Probe Recipient", created.Recipient?.Name);

            // The line was accepted, so the server priced the invoice from it. Nothing in the
            // request said what the totals should be.
            Assert.Equal(200m, created.NetAmount);
            Assert.True(created.GrossAmount > created.NetAmount);

            var updated = await client.DraftInvoices.UpdateAsync(
                created.DraftInvoiceNumber,
                new DraftInvoiceUpdate
                {
                    Date = new DateOnly(2026, 8, 15),
                    Currency = "DKK",
                    CustomerNumber = customer.CustomerNumber,
                    PaymentTerms = new DraftInvoiceUpdatePaymentTerms { PaymentTermsNumber = 1 },
                    Recipient = new DraftInvoiceUpdateRecipient
                    {
                        Name = "ZZ Probe Recipient Updated",
                        VatZoneNumber = 1,
                    },
                    Lines =
                    [
                        new DraftInvoiceUpdateLine
                        {
                            Description = "ZZ Probe line",
                            Product = new DraftInvoiceUpdateLineProduct { ProductNumber = product.ProductNumber },
                            Quantity = 3,
                            UnitNetPrice = 100,
                        },
                    ],
                },
                token);

            Assert.Equal(created.DraftInvoiceNumber, updated.DraftInvoiceNumber);
            Assert.Equal("ZZ Probe Recipient Updated", updated.Recipient?.Name);
            Assert.Equal(300m, updated.NetAmount);
        }
        finally
        {
            // The invoice first: a customer or product still referenced by one cannot be deleted.
            if (created is not null)
            {
                await client.DraftInvoices.DeleteAsync(created.DraftInvoiceNumber, token);
            }

            if (product is not null)
            {
                await client.Products.DeleteAsync(product.ProductNumber, token);
            }

            if (customer is not null)
            {
                await client.Customers.DeleteAsync(customer.CustomerNumber, token);
            }
        }
    }

    [Fact]
    public async Task Draft_orders_and_quotes_round_trip_like_invoices()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;
        Customer? customer = null;
        DraftOrder? order = null;
        DraftQuote? quote = null;

        try
        {
            var layout = (await client.Layouts.GetPageAsync(0, token)).Items[0].LayoutNumber;

            customer = await client.Customers.CreateAsync(
                new CustomerCreate
                {
                    Name = "ZZ Probe Documents Customer",
                    Currency = "DKK",
                    CustomerGroupNumber = 1,
                    PaymentTermsNumber = 1,
                    VatZoneNumber = 1,
                },
                token);

            // Orders and quotes are generated from the same templates as invoices, which is exactly
            // why they are worth sending: a mistake in those templates repeats itself three times.
            order = await client.DraftOrders.CreateAsync(
                new DraftOrderCreate
                {
                    Date = new DateOnly(2026, 8, 14),
                    Currency = "DKK",
                    LayoutNumber = layout,
                    CustomerNumber = customer.CustomerNumber,
                    PaymentTerms = new DraftOrderCreatePaymentTerms { PaymentTermsNumber = 1 },
                    Recipient = new DraftOrderCreateRecipient { Name = "ZZ Probe Order", VatZoneNumber = 1 },
                },
                token);

            Assert.True(order.OrderNumber > 0);
            Assert.Equal("ZZ Probe Order", order.Recipient?.Name);

            quote = await client.DraftQuotes.CreateAsync(
                new DraftQuoteCreate
                {
                    Date = new DateOnly(2026, 8, 14),
                    Currency = "DKK",
                    LayoutNumber = layout,
                    CustomerNumber = customer.CustomerNumber,
                    PaymentTerms = new DraftQuoteCreatePaymentTerms { PaymentTermsNumber = 1 },
                    Recipient = new DraftQuoteCreateRecipient { Name = "ZZ Probe Quote", VatZoneNumber = 1 },
                },
                token);

            Assert.True(quote.QuoteNumber > 0);
            Assert.Equal("ZZ Probe Quote", quote.Recipient?.Name);
        }
        finally
        {
            if (quote is not null)
            {
                await client.DraftQuotes.DeleteAsync(quote.QuoteNumber, token);
            }

            if (order is not null)
            {
                await client.DraftOrders.DeleteAsync(order.OrderNumber, token);
            }

            if (customer is not null)
            {
                await client.Customers.DeleteAsync(customer.CustomerNumber, token);
            }
        }
    }

    [Fact]
    public async Task A_draft_invoice_can_be_booked()
    {
        TestClients.SkipUnlessConfigured();

        // Booking is the one operation here that cannot be undone, so it is opted into separately.
        // A booked invoice is part of the accounting record: it cannot be deleted, and neither can
        // the customer and product it references. Every run therefore leaves three records behind.
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable(BookingOptInVariable) is not "1",
            $"Set {BookingOptInVariable}=1 to book an invoice, which cannot be undone.");

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;
        Customer? customer = null;
        Product? product = null;
        DraftInvoice? draft = null;
        var booked = false;

        try
        {
            var layouts = await client.Layouts.GetPageAsync(0, token);

            customer = await client.Customers.CreateAsync(
                new CustomerCreate
                {
                    Name = "ZZ Probe Booking Customer",
                    Currency = "DKK",
                    CustomerGroupNumber = 1,
                    PaymentTermsNumber = 1,
                    VatZoneNumber = 1,
                },
                token);

            product = await client.Products.CreateAsync(
                new ProductCreate
                {
                    ProductNumber = "ZZ-PROBE-BOOK",
                    Name = "ZZ Probe booking product",
                    ProductGroupNumber = 1,
                },
                token);

            draft = await client.DraftInvoices.CreateAsync(
                new DraftInvoiceCreate
                {
                    Date = new DateOnly(2026, 8, 14),
                    Currency = "DKK",
                    LayoutNumber = layouts.Items[0].LayoutNumber,
                    CustomerNumber = customer.CustomerNumber,
                    PaymentTerms = new DraftInvoiceCreatePaymentTerms { PaymentTermsNumber = 1 },
                    Recipient = new DraftInvoiceCreateRecipient
                    {
                        Name = "ZZ Probe Recipient",
                        VatZoneNumber = 1,
                    },
                    Lines =
                    [
                        new DraftInvoiceCreateLine
                        {
                            Description = "ZZ Probe line",
                            Product = new DraftInvoiceCreateLineProduct { ProductNumber = product.ProductNumber },
                            Quantity = 1,
                            UnitNetPrice = 50,
                        },
                    ],
                },
                token);

            var invoice = await client.DraftInvoices.BookAsync(draft.DraftInvoiceNumber, cancellationToken: token);
            booked = true;

            // Booking answers with the booked invoice, which has its own number and carries the
            // totals across. There is no way back: the draft no longer exists.
            Assert.True(invoice.BookedInvoiceNumber > 0);
            Assert.Equal(50m, invoice.NetAmount);
            Assert.Equal("ZZ Probe Recipient", invoice.Recipient?.Name);

            var remaining = await client.DraftInvoices
                .Where(i => i.DraftInvoiceNumber == draft.DraftInvoiceNumber)
                .GetPageAsync(0, token);

            Assert.Empty(remaining.Items);
        }
        finally
        {
            // Only unwind what booking did not consume. Once the invoice is booked none of this can
            // be removed, so the cleanup exists for the run that fails before that point.
            if (!booked)
            {
                if (draft is not null)
                {
                    await client.DraftInvoices.DeleteAsync(draft.DraftInvoiceNumber, token);
                }

                if (product is not null)
                {
                    await client.Products.DeleteAsync(product.ProductNumber, token);
                }

                if (customer is not null)
                {
                    await client.Customers.DeleteAsync(customer.CustomerNumber, token);
                }
            }
        }
    }

    private static EconomicClient CreateClient() => TestClients.Create();

}
