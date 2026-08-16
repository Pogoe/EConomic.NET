using System.Globalization;
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
/// Two cannot be undone and share an opt-in of their own — booking a draft invoice, and creating
/// an accounting year, neither of which e-conomic lets you delete. Everything else in this class
/// cleans up after itself.
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
            created = await client.Rest.Customers.CreateAsync(
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

            var updated = await client.Rest.Customers.UpdateAsync(
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
                await client.Rest.Customers.DeleteAsync(created.CustomerNumber, token);
            }
        }

        // Deleting twice is what reports the record as gone. Updating it would not: PUT is an
        // upsert, covered separately below.
        var gone = await Assert.ThrowsAsync<EconomicApiException>(
            () => client.Rest.Customers.DeleteAsync(created!.CustomerNumber, token));

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

        var upserted = await client.Rest.Customers.UpdateAsync(
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
            await client.Rest.Customers.DeleteAsync(number, token);
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
            created = await client.Rest.Units.CreateAsync(new UnitCreate { Name = "ZZ Probe Unit" }, token);

            Assert.True(created.UnitNumber > 0);
            Assert.NotNull(created.Self);
            Assert.Equal("ZZ Probe Unit", created.Name);

            var renamed = await client.Rest.Units.UpdateAsync(
                created.UnitNumber,
                new UnitUpdate { Name = "ZZ Probe Unit (updated)" },
                token);

            Assert.Equal(created.UnitNumber, renamed.UnitNumber);
            Assert.Equal("ZZ Probe Unit (updated)", renamed.Name);
        }
        finally
        {
            if (created is not null)
            {
                await client.Rest.Units.DeleteAsync(created.UnitNumber, token);
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
            var product = await client.Rest.Products.CreateAsync(
                new ProductCreate { ProductNumber = number, Name = "ZZ Probe Product", ProductGroupNumber = 1 },
                token);

            created = true;
            Assert.Equal(number, product.ProductNumber);

            var updated = await client.Rest.Products.UpdateAsync(
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
                await client.Rest.Products.DeleteAsync(number, token);
            }
        }
    }

    [Fact]
    public async Task Deleting_twice_reports_the_second_attempt_as_missing()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;

        var created = await client.Rest.Units.CreateAsync(new UnitCreate { Name = "ZZ Probe Delete Twice" }, token);
        await client.Rest.Units.DeleteAsync(created.UnitNumber, token);

        // e-conomic documents delete as non-idempotent, and identifiers are reused, which together
        // are why the retry handler refuses to repeat a delete that carries no idempotency key.
        var second = await Assert.ThrowsAsync<EconomicApiException>(
            () => client.Rest.Units.DeleteAsync(created.UnitNumber, token));

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
            created = await client.Rest.PaymentTerms.CreateAsync(
                new PaymentTermsCreate { Name = "ZZ Probe Terms", PaymentTermsType = "net", DaysOfCredit = 14 },
                token);

            Assert.True(created.PaymentTermsNumber > 0);
            Assert.Equal("ZZ Probe Terms", created.Name);
            Assert.Equal("net", created.PaymentTermsType, ignoreCase: true);

            // The update carries the enum too, so this is the second place the string has to
            // survive conversion — in the opposite direction from the create's response. Both it
            // and daysOfCredit have to be sent back unchanged: the payload accepts them, but the
            // server rejects the whole request with E06151 if either differs from what it stored.
            var updated = await client.Rest.PaymentTerms.UpdateAsync(
                created.PaymentTermsNumber,
                new PaymentTermsUpdate
                {
                    Name = "ZZ Probe Terms (updated)",
                    PaymentTermsType = "net",
                    DaysOfCredit = 14,
                },
                token);

            Assert.Equal("ZZ Probe Terms (updated)", updated.Name);
            Assert.Equal(14, updated.DaysOfCredit);

            var rejected = await Assert.ThrowsAsync<EconomicApiException>(
                () => client.Rest.PaymentTerms.UpdateAsync(
                    created.PaymentTermsNumber,
                    new PaymentTermsUpdate
                    {
                        Name = "ZZ Probe Terms",
                        PaymentTermsType = "net",
                        DaysOfCredit = 30,
                    },
                    token));

            Assert.Equal("E06151", rejected.ErrorCode);
        }
        finally
        {
            if (created is not null)
            {
                await client.Rest.PaymentTerms.DeleteAsync(created.PaymentTermsNumber, token);
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
        var created = await client.Rest.CustomerGroups.CreateAsync(
            new CustomerGroupCreate { CustomerGroupNumber = number, Name = "ZZ Probe Group", AccountNumber = 5600 },
            token);

        try
        {
            Assert.Equal(number, created.CustomerGroupNumber);
            Assert.Equal("ZZ Probe Group", created.Name);

            var updated = await client.Rest.CustomerGroups.UpdateAsync(
                number,
                new CustomerGroupUpdate { Name = "ZZ Probe Group (updated)", AccountNumber = 5600 },
                token);

            Assert.Equal(number, updated.CustomerGroupNumber);
            Assert.Equal("ZZ Probe Group (updated)", updated.Name);
        }
        finally
        {
            await client.Rest.CustomerGroups.DeleteAsync(number, token);
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
            created = await client.Rest.Suppliers.CreateAsync(
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

            var updated = await client.Rest.Suppliers.UpdateAsync(
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
                await client.Rest.Suppliers.DeleteAsync(created.SupplierNumber, token);
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
            customer = await client.Rest.Customers.CreateAsync(
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
            var contacts = client.Rest.Customers.Contacts(customer.CustomerNumber);

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

            var locations = client.Rest.Customers.DeliveryLocations(customer.CustomerNumber);
            var location = await locations.CreateAsync(
                new DeliveryLocationCreate { Address = "Odinsparken 4", City = "Ringsted", PostalCode = "4100" },
                token);

            Assert.True(location.DeliveryLocationNumber > 0);

            var moved = await locations.UpdateAsync(
                location.DeliveryLocationNumber,
                new DeliveryLocationUpdate { Address = "Odinsparken 5", City = "Ringsted", PostalCode = "4100" },
                token);

            Assert.Equal(location.DeliveryLocationNumber, moved.DeliveryLocationNumber);
            Assert.Equal("Odinsparken 5", moved.Address);

            await locations.DeleteAsync(location.DeliveryLocationNumber, token);
        }
        finally
        {
            if (customer is not null)
            {
                await client.Rest.Customers.DeleteAsync(customer.CustomerNumber, token);
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
            var layouts = await client.Rest.Layouts.GetPageAsync(0, token);
            var layout = layouts.Items[0].LayoutNumber;

            customer = await client.Rest.Customers.CreateAsync(
                new CustomerCreate
                {
                    Name = "ZZ Probe Invoice Customer",
                    Currency = "DKK",
                    CustomerGroupNumber = 1,
                    PaymentTermsNumber = 1,
                    VatZoneNumber = 1,
                },
                token);

            product = await client.Rest.Products.CreateAsync(
                new ProductCreate
                {
                    ProductNumber = "ZZ-PROBE-INV",
                    Name = "ZZ Probe product",
                    ProductGroupNumber = 1,
                },
                token);

            created = await client.Rest.DraftInvoices.CreateAsync(
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

            var updated = await client.Rest.DraftInvoices.UpdateAsync(
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
                await client.Rest.DraftInvoices.DeleteAsync(created.DraftInvoiceNumber, token);
            }

            if (product is not null)
            {
                await client.Rest.Products.DeleteAsync(product.ProductNumber, token);
            }

            if (customer is not null)
            {
                await client.Rest.Customers.DeleteAsync(customer.CustomerNumber, token);
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
            var layout = (await client.Rest.Layouts.GetPageAsync(0, token)).Items[0].LayoutNumber;
            var vatZone = (await client.Rest.VatZones.GetPageAsync(0, token)).Items[0].VatZoneNumber;

            customer = await client.Rest.Customers.CreateAsync(
                new CustomerCreate
                {
                    Name = "ZZ Probe Documents Customer",
                    Currency = "DKK",
                    CustomerGroupNumber = 1,
                    PaymentTermsNumber = 1,
                    VatZoneNumber = vatZone,
                },
                token);

            // Orders and quotes are generated from the same templates as invoices, which is exactly
            // why they are worth sending: a mistake in those templates repeats itself three times.
            order = await client.Rest.DraftOrders.CreateAsync(
                new DraftOrderCreate
                {
                    Date = new DateOnly(2026, 8, 14),
                    Currency = "DKK",
                    LayoutNumber = layout,
                    CustomerNumber = customer.CustomerNumber,
                    PaymentTerms = new DraftOrderCreatePaymentTerms { PaymentTermsNumber = 1 },
                    Recipient = new DraftOrderCreateRecipient { Name = "ZZ Probe Order", VatZoneNumber = vatZone },
                },
                token);

            Assert.True(order.OrderNumber > 0);
            Assert.Equal("ZZ Probe Order", order.Recipient?.Name);

            quote = await client.Rest.DraftQuotes.CreateAsync(
                new DraftQuoteCreate
                {
                    Date = new DateOnly(2026, 8, 14),
                    Currency = "DKK",
                    LayoutNumber = layout,
                    CustomerNumber = customer.CustomerNumber,
                    PaymentTerms = new DraftQuoteCreatePaymentTerms { PaymentTermsNumber = 1 },
                    Recipient = new DraftQuoteCreateRecipient { Name = "ZZ Probe Quote", VatZoneNumber = vatZone },
                },
                token);

            Assert.True(quote.QuoteNumber > 0);
            Assert.Equal("ZZ Probe Quote", quote.Recipient?.Name);

            // The updates are a separate generated template from the creates — different payload
            // type, different required set — so sending them is what confirms they work.
            var updatedOrder = await client.Rest.DraftOrders.UpdateAsync(
                order.OrderNumber,
                new DraftOrderUpdate
                {
                    Date = new DateOnly(2026, 8, 15),
                    Currency = "DKK",
                    CustomerNumber = customer.CustomerNumber,
                    Recipient = new DraftOrderUpdateRecipient
                    {
                        Name = "ZZ Probe Order (updated)",
                        VatZoneNumber = vatZone,
                    },
                },
                token);

            Assert.Equal(order.OrderNumber, updatedOrder.OrderNumber);
            Assert.Equal("ZZ Probe Order (updated)", updatedOrder.Recipient?.Name);

            var updatedQuote = await client.Rest.DraftQuotes.UpdateAsync(
                quote.QuoteNumber,
                new DraftQuoteUpdate
                {
                    Date = new DateOnly(2026, 8, 15),
                    Currency = "DKK",
                    CustomerNumber = customer.CustomerNumber,
                    Recipient = new DraftQuoteUpdateRecipient
                    {
                        Name = "ZZ Probe Quote (updated)",
                        VatZoneNumber = vatZone,
                    },
                },
                token);

            Assert.Equal(quote.QuoteNumber, updatedQuote.QuoteNumber);
            Assert.Equal("ZZ Probe Quote (updated)", updatedQuote.Recipient?.Name);
        }
        finally
        {
            if (quote is not null)
            {
                await client.Rest.DraftQuotes.DeleteAsync(quote.QuoteNumber, token);
            }

            if (order is not null)
            {
                await client.Rest.DraftOrders.DeleteAsync(order.OrderNumber, token);
            }

            if (customer is not null)
            {
                await client.Rest.Customers.DeleteAsync(customer.CustomerNumber, token);
            }
        }
    }

    [Fact]
    public async Task Deleting_every_draft_invoice_empties_the_collection()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;

        // This one deletes drafts it did not create, so it insists on starting from an empty
        // collection. On a throwaway agreement that is true; anywhere else the skip is the point.
        var before = await client.Rest.DraftInvoices.GetPageAsync(0, token);
        Assert.SkipWhen(
            before.Items.Count > 0,
            "The agreement already has draft invoices, and this test would delete them.");

        await using var seed = new AgreementSeed(client, token);
        var customer = await seed.CustomerAsync("ZZ Probe Bulk Delete");
        await seed.DraftInvoiceAsync(customer, recipientName: "ZZ Probe Bulk A");
        await seed.DraftInvoiceAsync(customer, recipientName: "ZZ Probe Bulk B");

        Assert.Equal(2, (await client.Rest.DraftInvoices.GetPageAsync(0, token)).Items.Count);

        await client.Rest.DraftInvoices.DeleteEveryDraftAsync(DraftInvoiceBulkDelete.EveryDraft, token);

        Assert.Empty((await client.Rest.DraftInvoices.GetPageAsync(0, token)).Items);

        // Both drafts are already gone, so the seed must not try to delete them again.
        seed.Forget();
        seed.Forget();
    }

    [Fact]
    public async Task A_journal_voucher_is_posted_with_its_entries()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;

        var journal = (await client.Rest.Journals.GetPageAsync(0, token)).Items[0];
        var year = (await client.Rest.AccountingYears.AsQuery().GetPageAsync(0, token)).Items
            .First(y => y.Closed == false);

        // Two accounts to move an amount between. A finance voucher entry posts to `account` and
        // balances against `contraAccount`, so both have to accept direct entries.
        var accounts = await client.Rest.Accounts
            .Where(a => a.AccountType == "profitAndLoss")
            .WithPageSize(50)
            .GetPageAsync(0, token);

        var usable = accounts.Items.Where(a => !a.BlockDirectEntries).Take(2).ToList();
        Assert.SkipWhen(usable.Count < 2, "The agreement has fewer than two accounts that accept direct entries.");

        var created = await client.Rest.Journals.Vouchers(journal.JournalNumber).CreateAsync(
            new JournalVoucherCreate
            {
                AccountingYear = new JournalVoucherCreateAccountingYear { Year = year.Year },
                Entries = new JournalVoucherCreateEntries
                {
                    FinanceVouchers =
                    [
                        new JournalVoucherCreateEntriesFinanceVoucher
                        {
                            Date = new DateOnly(2026, 8, 14),
                            Amount = 100m,
                            AccountNumber = usable[0].AccountNumber,
                            ContraAccountNumber = usable[1].AccountNumber,
                            Text = "ZZ Probe voucher",
                        },
                    ],
                },
            },
            token);

        // A voucher create answers with an array, not a single voucher: e-conomic may split the
        // entries it was sent across several. The specification describes one object, and a client
        // built from it posted successfully and then failed to read the reply.
        var voucher = Assert.Single(created);

        // The voucher is also the one shape whose entries are an object of five arrays, one per
        // entry kind. Nothing else in the API nests that deeply.
        Assert.True(voucher.VoucherNumber > 0);
        Assert.NotNull(voucher.Entries);

        // Reading vouchers back, which is a different generated method from the create.
        var listed = await client.Rest.Journals.Vouchers(journal.JournalNumber).GetPageAsync(0, token);
        Assert.Contains(listed.Items, v => v.VoucherNumber == voucher.VoucherNumber);

        var posted = Assert.Single(voucher.Entries!.FinanceVouchers);
        Assert.Equal(100m, posted.Amount);
        Assert.Equal("ZZ Probe voucher", posted.Text);
        Assert.Equal(usable[0].AccountNumber, posted.Account?.Number);
        Assert.Equal(usable[1].AccountNumber, posted.ContraAccount?.Number);

        // Unposting it again. e-conomic publishes no delete for a voucher, but every entry carries
        // a metaData.delete link to /journals/{n}/entries/{k} — hypermedia about its own records,
        // which is better evidence than the documentation, and the only way this test does not
        // leave a voucher behind on every run.
        //
        // The entry number is read back from the entries collection rather than from the voucher:
        // the server sends `journalEntryNumber` on a voucher's entries, but the schema does not
        // declare it, so the mapped model does not carry it.
        var entries = client.Rest.Journals.Entries(journal.JournalNumber);
        var mine = (await entries.GetPageAsync(0, token)).Items
            .Where(e => e.Voucher?.VoucherNumber == voucher.VoucherNumber)
            .ToList();

        var entry = Assert.Single(mine);
        Assert.Equal(100m, entry.Amount);

        await entries.DeleteAsync(entry.JournalEntryNumber, token);

        var remaining = await entries.GetPageAsync(0, token);
        Assert.DoesNotContain(remaining.Items, e => e.JournalEntryNumber == entry.JournalEntryNumber);
    }

    /// <summary>
    /// Posts a supplier invoice carrying payment details, and reads them back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// e-conomic describes the payment type as a <c>oneOf</c> over six alternatives — one pair of
    /// fields per payment type — and NSwag generates from the first alternative and silently drops
    /// the other five. Five of the six were therefore unrepresentable in the <em>generated</em>
    /// layer, so no amount of facade work could have reached them; the alternatives are merged into
    /// the union of their fields before generation now.
    /// </para>
    /// <para>
    /// This deliberately sends a bank transfer, which is the fourth alternative. The first —
    /// <c>fiSupplierNo</c> with <c>ocrLine</c> — is the one that survived the drop, so testing it
    /// would have passed before the fix and proved nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_supplier_invoice_voucher_carries_its_payment_details()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;

        await using var seed = new AgreementSeed(client, token);
        var supplier = await seed.SupplierAsync("ZZ Probe Payment Supplier");

        var journal = (await client.Rest.Journals.GetPageAsync(0, token)).Items[0];
        var year = (await client.Rest.AccountingYears.AsQuery().GetPageAsync(0, token)).Items
            .First(y => y.Closed == false);

        // A supplier invoice posts to the supplier's own creditor account and balances against an
        // expense account, so only the contra account is chosen here.
        var accounts = await client.Rest.Accounts
            .Where(a => a.AccountType == "profitAndLoss")
            .WithPageSize(50)
            .GetPageAsync(0, token);

        var expense = accounts.Items.FirstOrDefault(a => !a.BlockDirectEntries);
        Assert.SkipWhen(expense is null, "The agreement has no account that accepts direct entries.");

        // 7 is the bank transfer type, which takes an account number and a message. The numbers are
        // read from the agreement rather than hard-coded: e-conomic seeds them, but nothing
        // guarantees the numbering.
        var bankTransfer = (await client.Rest.PaymentTypes.AsQuery().GetPageAsync(0, token)).Items
            .FirstOrDefault(t => t.Name == "Bank transfer");

        Assert.SkipWhen(bankTransfer is null, "The agreement publishes no bank transfer payment type.");

        var created = await client.Rest.Journals.Vouchers(journal.JournalNumber).CreateAsync(
            new JournalVoucherCreate
            {
                AccountingYear = new JournalVoucherCreateAccountingYear { Year = year.Year },
                Entries = new JournalVoucherCreateEntries
                {
                    SupplierInvoices =
                    [
                        new JournalVoucherCreateEntriesSupplierInvoice
                        {
                            Date = new DateOnly(2026, 8, 14),
                            DueDate = new DateOnly(2026, 9, 14),
                            Amount = -250m,
                            Currency = new JournalVoucherCreateEntriesSupplierInvoiceCurrency { Code = "DKK" },
                            SupplierNumber = supplier.SupplierNumber,
                            ContraAccountNumber = expense!.AccountNumber,
                            SupplierInvoiceNumber = "ZZ-PROBE-1",
                            Text = "ZZ Probe supplier invoice",
                            PaymentDetails = new JournalVoucherCreateEntriesSupplierInvoicePaymentDetails
                            {
                                PaymentTypeNumber = bankTransfer!.PaymentTypeNumber,
                                AccountNo = "12345678",
                                Message = "ZZ Probe payment message",
                            },
                        },
                    ],
                },
            },
            token);

        var voucher = Assert.Single(created);
        var posted = Assert.Single(voucher.Entries!.SupplierInvoices);

        // The point of the test: the fields of the alternative that was dropped survive the round
        // trip. Asserting on the number alone would pass with the payment details missing entirely.
        Assert.Equal(bankTransfer.PaymentTypeNumber, posted.PaymentDetails?.PaymentType?.Number);
        Assert.Equal("12345678", posted.PaymentDetails?.AccountNo);
        Assert.Equal("ZZ Probe payment message", posted.PaymentDetails?.Message);

        // Unpost it, so a run leaves nothing behind. A voucher has no delete of its own; its
        // entries carry one, which is how the finance voucher round-trip cleans up too.
        var entries = client.Rest.Journals.Entries(journal.JournalNumber);
        var mine = (await entries.GetPageAsync(0, token)).Items
            .Where(e => e.Voucher?.VoucherNumber == voucher.VoucherNumber)
            .ToList();

        foreach (var entry in mine)
        {
            await entries.DeleteAsync(entry.JournalEntryNumber, token);
        }
    }

    [Fact]
    public async Task An_accounting_year_can_be_created()
    {
        TestClients.SkipUnlessConfigured();

        // Like booking, this cannot be undone: e-conomic publishes no delete for an accounting
        // year, so a run of this leaves one behind permanently. It shares the same opt-in.
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable(BookingOptInVariable) is not "1",
            $"Set {BookingOptInVariable}=1 to create an accounting year, which cannot be undone.");

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;

        var existing = (await client.Rest.AccountingYears.AsQuery().GetPageAsync(0, token)).Items
            .Select(y => y.Year)
            .ToHashSet(StringComparer.Ordinal);

        var year = Enumerable.Range(2030, 20).First(y => !existing.Contains(y.ToString(CultureInfo.InvariantCulture)));

        var created = await client.Rest.AccountingYears.CreateAsync(
            new AccountingYearCreate
            {
                FromDate = new DateOnly(year, 1, 1),
                ToDate = new DateOnly(year, 12, 31),
            },
            token);

        // The payload carries two dates and no identifier; the server answers with the year it
        // assigned, as a string.
        Assert.Equal(year.ToString(CultureInfo.InvariantCulture), created.Year);
        Assert.NotNull(created.Self);
        Assert.False(created.Closed);
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
            var layouts = await client.Rest.Layouts.GetPageAsync(0, token);

            customer = await client.Rest.Customers.CreateAsync(
                new CustomerCreate
                {
                    Name = "ZZ Probe Booking Customer",
                    Currency = "DKK",
                    CustomerGroupNumber = 1,
                    PaymentTermsNumber = 1,
                    VatZoneNumber = 1,
                },
                token);

            // Unique per run, and it has to be: a product on a booked invoice cannot be deleted, so
            // a fixed number makes this test pass exactly once per agreement and fail with "already
            // exists" forever after. Everything else here is numbered by the server.
            product = await client.Rest.Products.CreateAsync(
                new ProductCreate
                {
                    ProductNumber = "ZZ-BOOK-" + DateTime.UtcNow.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture),
                    Name = "ZZ Probe booking product",
                    ProductGroupNumber = 1,
                },
                token);

            draft = await client.Rest.DraftInvoices.CreateAsync(
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

            var invoice = await client.Rest.DraftInvoices.BookAsync(draft.DraftInvoiceNumber, cancellationToken: token);
            booked = true;

            // Booking answers with the booked invoice, which has its own number and carries the
            // totals across. There is no way back: the draft no longer exists.
            Assert.True(invoice.BookedInvoiceNumber > 0);
            Assert.Equal(50m, invoice.NetAmount);
            Assert.Equal("ZZ Probe Recipient", invoice.Recipient?.Name);

            var remaining = await client.Rest.DraftInvoices
                .Where(i => i.DraftInvoiceNumber == draft.DraftInvoiceNumber)
                .GetPageAsync(0, token);

            Assert.Empty(remaining.Items);

            // Booking is the only way this library can put anything into the derived views, and
            // this is the only test that reaches them with data. They are separate endpoints with
            // separate models, so an empty page from each says nothing about whether they map.
            var booking = await client.Rest.BookedInvoices
                .Where(i => i.BookedInvoiceNumber == invoice.BookedInvoiceNumber)
                .GetPageAsync(0, token);

            Assert.Equal(50m, Assert.Single(booking.Items).NetAmount);

            var unpaid = await client.Rest.UnpaidInvoices
                .Where(i => i.BookedInvoiceNumber == invoice.BookedInvoiceNumber)
                .GetPageAsync(0, token);

            // An invoice nobody has paid is outstanding for its full gross amount, and the view
            // reports what is left rather than what it was worth.
            var outstanding = Assert.Single(unpaid.Items);
            Assert.Equal(customer.CustomerNumber, outstanding.Customer?.Number);
            Assert.Equal(outstanding.GrossAmount, outstanding.Remainder);

            // Which of not-due and overdue it lands in depends on the due date against today, so
            // the assertion is that it is in exactly one of them, whichever that is.
            var notDue = await client.Rest.NotDueInvoices
                .Where(i => i.BookedInvoiceNumber == invoice.BookedInvoiceNumber)
                .GetPageAsync(0, token);

            var overdue = await client.Rest.OverdueInvoices
                .Where(i => i.BookedInvoiceNumber == invoice.BookedInvoiceNumber)
                .GetPageAsync(0, token);

            Assert.Equal(1, notDue.Items.Count + overdue.Items.Count);
        }
        finally
        {
            // Only unwind what booking did not consume. Once the invoice is booked none of this can
            // be removed, so the cleanup exists for the run that fails before that point.
            if (!booked)
            {
                if (draft is not null)
                {
                    await client.Rest.DraftInvoices.DeleteAsync(draft.DraftInvoiceNumber, token);
                }

                if (product is not null)
                {
                    await client.Rest.Products.DeleteAsync(product.ProductNumber, token);
                }

                if (customer is not null)
                {
                    await client.Rest.Customers.DeleteAsync(customer.CustomerNumber, token);
                }
            }
        }
    }

    /// <summary>
    /// A date the caller never set must not be sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Optional numbers on the write payloads were made nullable so an unset one would be left out
    /// rather than sent as <c>0</c>. Dates are value types too and were missed, so every draft
    /// invoice, order and quote carried <c>dueDate: 0001-01-01</c> unless the caller supplied one.
    /// </para>
    /// <para>
    /// It went unnoticed because e-conomic normally derives the due date from the payment terms and
    /// ignores what it was sent. Payment terms of type <c>dueDate</c> are the exception: there the
    /// invoice carries its own due date, and the difference becomes visible. Sending year one was
    /// rejected as "may not be set to an earlier date than date"; omitting it is rejected as
    /// "missing a value", which is the error the caller can act on.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_unset_due_date_is_omitted_rather_than_sent_as_year_one()
    {
        TestClients.SkipUnlessConfigured();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;

        await using var seed = new AgreementSeed(client, token);
        var customer = await seed.CustomerAsync("ZZ Probe Due Date");
        var layouts = await client.Rest.Layouts.GetPageAsync(0, token);

        PaymentTerms? terms = null;
        DraftInvoice? created = null;

        try
        {
            terms = await client.Rest.PaymentTerms.CreateAsync(
                new PaymentTermsCreate { Name = "ZZ Probe Due Date Terms", PaymentTermsType = "dueDate" },
                token);

            DraftInvoiceCreate Draft(DateOnly? dueDate) => new()
            {
                Date = new DateOnly(2026, 8, 14),
                DueDate = dueDate,
                Currency = "DKK",
                LayoutNumber = layouts.Items[0].LayoutNumber,
                CustomerNumber = customer.CustomerNumber,
                PaymentTerms = new DraftInvoiceCreatePaymentTerms { PaymentTermsNumber = terms.PaymentTermsNumber },
                Recipient = new DraftInvoiceCreateRecipient
                {
                    Name = "ZZ Probe Recipient",
                    VatZoneNumber = customer.VatZone!.Number,
                },
            };

            var rejected = await Assert.ThrowsAsync<EconomicApiException>(
                () => client.Rest.DraftInvoices.CreateAsync(Draft(dueDate: null), token));

            // E04042 is "dueDate is missing a value", which is only reachable if the property was
            // left out of the request. The year-one value produced E04760 instead — a complaint
            // about a date the caller never chose.
            Assert.NotNull(rejected.RawBody);
            Assert.Contains("E04042", rejected.RawBody, StringComparison.Ordinal);
            Assert.DoesNotContain("0001-01-01", rejected.RawBody, StringComparison.Ordinal);

            // And a date that was supplied still arrives, so nothing was lost making it optional.
            var due = new DateOnly(2026, 12, 24);
            created = await client.Rest.DraftInvoices.CreateAsync(Draft(due), token);

            Assert.Equal(due, created.DueDate);
        }
        finally
        {
            if (created is not null)
            {
                await client.Rest.DraftInvoices.DeleteAsync(created.DraftInvoiceNumber, token);
            }

            if (terms is not null)
            {
                await client.Rest.PaymentTerms.DeleteAsync(terms.PaymentTermsNumber, token);
            }
        }
    }

    private static EconomicClient CreateClient() => TestClients.Create();

}
