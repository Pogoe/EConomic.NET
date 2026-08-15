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
    /// <summary>
    /// Event types a webhook can be registered for, in the order this seed tries them.
    /// </summary>
    /// <remarks>
    /// e-conomic allows one webhook per event type per agreement, so a type already in use is
    /// skipped rather than failing the seed. Read from <c>/EventTypes</c> on a live agreement; the
    /// service publishes more, and three is enough for a probe.
    /// </remarks>
    private static readonly string[] WebhookEventTypes =
        ["CUSTOMER_UPDATED", "INVOICE_BOOKED", "JOURNAL_BOOKED"];

    private readonly List<Func<Task>> _cleanup = [];

    /// <summary>Creates a customer.</summary>
    /// <param name="name">Its name, which the caller can assert on.</param>
    /// <returns>The created customer.</returns>
    public async Task<Customer> CustomerAsync(string name = "ZZ Probe Customer")
    {
        var customer = await client.Rest.Customers.CreateAsync(
            new CustomerCreate
            {
                Name = name,
                Currency = "DKK",
                CustomerGroupNumber = await FirstCustomerGroupAsync(),
                PaymentTermsNumber = await FirstPaymentTermsAsync(),
                VatZoneNumber = await FirstVatZoneAsync(),
            },
            cancellationToken);

        _cleanup.Add(() => client.Rest.Customers.DeleteAsync(customer.CustomerNumber, cancellationToken));
        return customer;
    }

    /// <summary>Creates a product.</summary>
    /// <param name="productNumber">Its number, which is caller-supplied for products.</param>
    /// <returns>The created product.</returns>
    public async Task<Product> ProductAsync(string productNumber)
    {
        var product = await client.Rest.Products.CreateAsync(
            new ProductCreate
            {
                ProductNumber = productNumber,
                Name = "ZZ Probe Product",
                ProductGroupNumber = await FirstProductGroupAsync(),
            },
            cancellationToken);

        _cleanup.Add(() => client.Rest.Products.DeleteAsync(product.ProductNumber, cancellationToken));
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

        var invoice = await client.Rest.DraftInvoices.CreateAsync(
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

        _cleanup.Add(() => client.Rest.DraftInvoices.DeleteAsync(invoice.DraftInvoiceNumber, cancellationToken));
        return invoice;
    }

    /// <summary>Creates a supplier.</summary>
    /// <param name="name">Its name, which the caller can assert on.</param>
    /// <returns>The created supplier.</returns>
    public async Task<Supplier> SupplierAsync(string name = "ZZ Probe Supplier")
    {
        var supplier = await client.Rest.Suppliers.CreateAsync(
            new SupplierCreate
            {
                Name = name,
                Currency = "DKK",
                PaymentTermsNumber = await FirstPaymentTermsAsync(),
                VatZoneNumber = await FirstVatZoneAsync(),
                SupplierGroupNumber = await FirstSupplierGroupAsync(),
            },
            cancellationToken);

        _cleanup.Add(() => client.Rest.Suppliers.DeleteAsync(supplier.SupplierNumber, cancellationToken));
        return supplier;
    }

    /// <summary>Creates a draft order.</summary>
    /// <param name="customer">The customer to order for.</param>
    /// <returns>The created draft.</returns>
    public async Task<DraftOrder> DraftOrderAsync(Customer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);

        var order = await client.Rest.DraftOrders.CreateAsync(
            new DraftOrderCreate
            {
                Date = new DateOnly(2026, 8, 14),
                Currency = "DKK",
                LayoutNumber = await FirstLayoutAsync(),
                CustomerNumber = customer.CustomerNumber,
                PaymentTerms = new DraftOrderCreatePaymentTerms
                {
                    PaymentTermsNumber = await FirstPaymentTermsAsync(),
                },
                Recipient = new DraftOrderCreateRecipient
                {
                    Name = "ZZ Probe Recipient",
                    VatZoneNumber = await FirstVatZoneAsync(),
                    City = "Ringsted",
                },
            },
            cancellationToken);

        _cleanup.Add(() => client.Rest.DraftOrders.DeleteAsync(order.OrderNumber, cancellationToken));
        return order;
    }

    /// <summary>Creates a draft quote.</summary>
    /// <param name="customer">The customer to quote.</param>
    /// <returns>The created draft.</returns>
    public async Task<DraftQuote> DraftQuoteAsync(Customer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);

        var quote = await client.Rest.DraftQuotes.CreateAsync(
            new DraftQuoteCreate
            {
                Date = new DateOnly(2026, 8, 14),
                Currency = "DKK",
                LayoutNumber = await FirstLayoutAsync(),
                CustomerNumber = customer.CustomerNumber,
                PaymentTerms = new DraftQuoteCreatePaymentTerms
                {
                    PaymentTermsNumber = await FirstPaymentTermsAsync(),
                },
                Recipient = new DraftQuoteCreateRecipient
                {
                    Name = "ZZ Probe Recipient",
                    VatZoneNumber = await FirstVatZoneAsync(),
                    City = "Ringsted",
                },
            },
            cancellationToken);

        _cleanup.Add(() => client.Rest.DraftQuotes.DeleteAsync(quote.QuoteNumber, cancellationToken));
        return quote;
    }

    /// <summary>Creates a product price group on the OpenAPI surface.</summary>
    /// <returns>Its number.</returns>
    public async Task<int> ProductPriceGroupAsync()
    {
        var number = await NextNumberAsync(
            client.Open.ProductPriceGroups.AsAsyncEnumerable(cancellationToken),
            g => g.Number);

        var created = await client.Open.ProductPriceGroups.CreateAsync(
            new Open.ProductPriceGroup { Name = "ZZ Probe Price Group", Number = number },
            cancellationToken);

        var key = created ?? number;
        _cleanup.Add(() => client.Open.ProductPriceGroups.DeleteAsync(key, cancellationToken));
        return key;
    }

    /// <summary>Creates a project employee group.</summary>
    /// <returns>Its number.</returns>
    public async Task<int> ProjectEmployeeGroupAsync()
    {
        var number = await NextNumberAsync(
            client.Open.ProjectEmployeeGroups.AsAsyncEnumerable(cancellationToken),
            g => g.Number);

        var created = await client.Open.ProjectEmployeeGroups.CreateAsync(
            new Open.ProjectEmployeeGroup { Name = "ZZ Probe Employee Group", Number = number },
            cancellationToken);

        var key = created ?? number;
        _cleanup.Add(() => client.Open.ProjectEmployeeGroups.DeleteAsync(key, cancellationToken));
        return key;
    }

    /// <summary>Creates a project employee.</summary>
    /// <param name="groupNumber">The group it belongs to.</param>
    /// <returns>Its number.</returns>
    public async Task<int> ProjectEmployeeAsync(int groupNumber)
    {
        var number = await NextNumberAsync(
            client.Open.ProjectEmployees.AsAsyncEnumerable(cancellationToken),
            e => e.Number);

        var created = await client.Open.ProjectEmployees.CreateAsync(
            new Open.ProjectEmployee
            {
                Number = number,
                Name = "ZZ Probe Project Employee",
                GroupNumber = groupNumber,
                IsBarred = false,
            },
            cancellationToken);

        var key = created ?? number;
        _cleanup.Add(() => client.Open.ProjectEmployees.DeleteAsync(key, cancellationToken));
        return key;
    }

    /// <summary>
    /// Creates a webhook.
    /// </summary>
    /// <remarks>
    /// e-conomic allows one webhook per event type per agreement, and the event type is the key a
    /// delete takes, so the type is read from the service rather than hard-coded: an agreement that
    /// already has one on the type this picked would otherwise fail the seed.
    /// </remarks>
    /// <returns>The event type it was registered for.</returns>
    public async Task<string> WebhookAsync()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var hook in client.Open.Webhooks.AsAsyncEnumerable(cancellationToken))
        {
            if (hook.EventType is { } type)
            {
                taken.Add(type);
            }
        }

        var eventType = WebhookEventTypes.FirstOrDefault(t => !taken.Contains(t));

        Assert.SkipWhen(eventType is null, "Every event type this seed knows already has a webhook.");

        var created = await client.Open.Webhooks.CreateAsync(
            new Open.Webhook
            {
                Name = "ZZ Probe Webhook",
                EventType = eventType!,
                Url = "https://example.invalid/zz-probe",
                ContentType = "application/json",
            },
            cancellationToken);

        var key = created ?? eventType!;
        _cleanup.Add(() => client.Open.Webhooks.DeleteAsync(key, cancellationToken));
        return key;
    }

    /// <summary>
    /// Creates a subscription.
    /// </summary>
    /// <remarks>
    /// <c>interval</c> and <c>collection</c> are integer enumerations: 3 is a month and 0 collects a
    /// full period. e-conomic defines them in prose in the specification's description rather than as
    /// named values, which is why they are spelled out here.
    /// </remarks>
    /// <returns>Its number.</returns>
    public async Task<int> SubscriptionAsync()
    {
        var created = await client.Open.Subscriptions.CreateAsync(
            new Open.Subscription
            {
                Name = "ZZ Probe Subscription",
                Interval = 3,
                Collection = 0,
            },
            cancellationToken);

        var key = created ?? 0;
        _cleanup.Add(() => client.Open.Subscriptions.DeleteAsync(key, cancellationToken));
        return key;
    }

    /// <summary>Creates a budget figure.</summary>
    /// <returns>Its number.</returns>
    public async Task<int> BudgetFigureAsync()
    {
        var account = await FirstOpenAccountAsync();

        var created = await client.Open.BudgetFigures.CreateAsync(
            new Open.BudgetFigure
            {
                AccountNumber = account,
                AmountDefaultCurrency = 100,
                FromDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                ToDate = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero),
            },
            cancellationToken);

        var key = created ?? 0;
        _cleanup.Add(() => client.Open.BudgetFigures.DeleteAsync(key, cancellationToken));
        return key;
    }

    /// <summary>Creates a customer contact on the OpenAPI surface.</summary>
    /// <param name="customerNumber">The customer it belongs to.</param>
    /// <returns>Its number.</returns>
    public async Task<int> OpenCustomerContactAsync(int customerNumber)
    {
        var created = await client.Open.CustomerContacts.CreateAsync(
            new Open.CustomerContact
            {
                CustomerNumber = customerNumber,
                Name = "ZZ Probe Contact",
            },
            cancellationToken);

        var key = created ?? 0;
        _cleanup.Add(() => client.Open.CustomerContacts.DeleteAsync(key, cancellationToken));
        return key;
    }

    /// <summary>Creates a customer delivery location on the OpenAPI surface.</summary>
    /// <param name="customerNumber">The customer it belongs to.</param>
    /// <returns>Its number.</returns>
    public async Task<int> OpenDeliveryLocationAsync(int customerNumber)
    {
        var created = await client.Open.CustomerDeliveryLocations.CreateAsync(
            new Open.CustomerDeliveryLocation
            {
                CustomerNumber = customerNumber,
                Address = "ZZ Probe Address 1",
                City = "Ringsted",
                PostalCode = "4100",
            },
            cancellationToken);

        var key = created ?? 0;
        _cleanup.Add(() => client.Open.CustomerDeliveryLocations.DeleteAsync(key, cancellationToken));
        return key;
    }

    /// <summary>Creates a supplier contact on the OpenAPI surface.</summary>
    /// <param name="supplierNumber">The supplier it belongs to.</param>
    /// <returns>Its number.</returns>
    public async Task<int> OpenSupplierContactAsync(int supplierNumber)
    {
        var created = await client.Open.SupplierContacts.CreateAsync(
            new Open.SupplierContact
            {
                SupplierNumber = supplierNumber,
                Name = "ZZ Probe Supplier Contact",
            },
            cancellationToken);

        var key = created ?? 0;
        _cleanup.Add(() => client.Open.SupplierContacts.DeleteAsync(key, cancellationToken));
        return key;
    }

    /// <summary>Subscribes a customer to a subscription.</summary>
    /// <param name="subscriptionNumber">The subscription.</param>
    /// <param name="customerNumber">The customer subscribing.</param>
    /// <returns>Its number.</returns>
    public async Task<int> SubscriptionSubscriberAsync(int subscriptionNumber, int customerNumber)
    {
        var created = await client.Open.SubscriptionSubscribers.CreateAsync(
            new Open.SubscriptionSubscriber
            {
                SubscriptionNumber = subscriptionNumber,
                CustomerNumber = customerNumber,
                StartDate = new DateOnly(2026, 1, 1),
                EndDate = new DateOnly(2026, 12, 31),
            },
            cancellationToken);

        var key = created ?? 0;
        _cleanup.Add(() => client.Open.SubscriptionSubscribers.DeleteAsync(key, cancellationToken));
        return key;
    }

    /// <summary>
    /// Creates a journal draft entry.
    /// </summary>
    /// <remarks>
    /// <c>entryTypeNumber</c> 5 is a finance voucher, which balances against a contra account and so
    /// needs no customer or supplier. The specification defines the five types in prose rather than
    /// as named values.
    /// </remarks>
    /// <returns>The journal it was posted to, and the entry's number.</returns>
    public async Task<(int JournalNumber, int EntryNumber)> JournalDraftEntryAsync()
    {
        var journal = await FirstOpenJournalAsync();
        var accounts = await TwoPostableAccountsAsync();

        var created = await client.Open.JournalDraftEntries.CreateAsync(
            new Open.JournalDraftEntry
            {
                JournalNumber = journal,
                EntryTypeNumber = 5,
                Date = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero),
                Amount = 100,
                Currency = "DKK",
                AccountNumber = accounts.First,
                ContraAccountNumber = accounts.Second,
                Text = "ZZ Probe draft entry",
            },
            cancellationToken);

        var entry = created ?? 0;
        _cleanup.Add(() => client.Open.JournalDraftEntries.DeleteAsync(entry, cancellationToken));
        return (journal, entry);
    }

    /// <summary>Accrues a journal draft entry over a period.</summary>
    /// <param name="journalNumber">The journal the entry is in.</param>
    /// <param name="entryNumber">The entry to accrue.</param>
    public async Task JournalDraftEntryAccrualAsync(int journalNumber, int entryNumber)
    {
        var accounts = await TwoPostableAccountsAsync();

        await client.Open.JournalDraftEntryAccruals.CreateAsync(
            new Open.JournalDraftEntryAccrual
            {
                JournalNumber = journalNumber,
                EntryNumber = entryNumber,
                AccountNumber = accounts.First,
                StartDate = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
                EndDate = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero),
            },
            cancellationToken);

        // Deleting the entry takes its accrual with it, so nothing is registered here: a delete of
        // its own would run after the entry had already gone and answer 404.
    }

    /// <summary>
    /// Creates a value on the agreement's first dimension.
    /// </summary>
    /// <remarks>
    /// A dimension value is what everything else in this service attaches to: the five attachment
    /// collections are all keyed by <c>dimensionNumber</c> plus <c>dimensionKey</c>, and the key is
    /// this value's.
    /// </remarks>
    /// <returns>The dimension it belongs to, and its own key.</returns>
    public async Task<(int DimensionNumber, int Key)> DimensionValueAsync()
    {
        var dimension = await FirstDimensionAsync();

        // The key is caller-supplied and the specification does not say so: its `required` array is
        // empty, and the server answers "The field Key must be between 1 and 999999999". Nothing
        // offline could have found that. e-conomic also notes that values and distributions share
        // one key space, so a distribution's key would collide.
        var key = await NextNumberAsync(
            client.Open.DimensionValues.AsAsyncEnumerable(cancellationToken),
            v => v.Key);

        await client.Open.DimensionValues.CreateAsync(
            new Open.DimensionValue
            {
                DimensionNumber = dimension,
                Key = key,
                Name = "ZZ Probe Dimension Value",
                Active = true,
            },
            cancellationToken);

        var value = new Open.DimensionValue { DimensionNumber = dimension, Key = key };
        _cleanup.Add(() => client.Open.DimensionValues.DeleteAsync(value, cancellationToken));
        return (dimension, key);
    }

    /// <summary>Attaches a dimension value to an account.</summary>
    /// <param name="dimension">The dimension and value to attach.</param>
    public async Task DimensionAccountAsync((int DimensionNumber, int Key) dimension)
    {
        var item = new Open.DimensionAccount
        {
            DimensionNumber = dimension.DimensionNumber,
            DimensionKey = dimension.Key,
            AccountNumber = await FirstOpenAccountAsync(),
        };

        await client.Open.DimensionAccounts.CreateAsync(item, cancellationToken);
        _cleanup.Add(() => client.Open.DimensionAccounts.DeleteAsync(item, cancellationToken));
    }

    /// <summary>Attaches a dimension value to a booked entry.</summary>
    /// <param name="dimension">The dimension and value to attach.</param>
    public async Task DimensionBookedEntryAsync((int DimensionNumber, int Key) dimension)
    {
        var entry = 0;
        await foreach (var booked in client.Open.BookedEntries.AsAsyncEnumerable(cancellationToken))
        {
            entry = booked.EntryNumber;
            break;
        }

        Assert.SkipWhen(entry == 0, "The agreement has no booked entry to attach a dimension to.");

        var item = new Open.DimensionBookedEntry
        {
            DimensionNumber = dimension.DimensionNumber,
            DimensionKey = dimension.Key,
            EntryNumber = entry,
        };

        await client.Open.DimensionBookedEntries.CreateAsync(item, cancellationToken);
        _cleanup.Add(() => client.Open.DimensionBookedEntries.DeleteAsync(item, cancellationToken));
    }

    /// <summary>Attaches a dimension value to a budget figure.</summary>
    /// <param name="dimension">The dimension and value to attach.</param>
    /// <param name="budgetFigureNumber">The budget figure to attach it to.</param>
    public async Task DimensionBudgetFigureAsync((int DimensionNumber, int Key) dimension, int budgetFigureNumber)
    {
        var item = new Open.DimensionBudgetFigure
        {
            DimensionNumber = dimension.DimensionNumber,
            DimensionKey = dimension.Key,
            BudgetFigureNumber = budgetFigureNumber,
        };

        await client.Open.DimensionBudgetFigures.CreateAsync(item, cancellationToken);
        _cleanup.Add(() => client.Open.DimensionBudgetFigures.DeleteAsync(item, cancellationToken));
    }

    /// <summary>Attaches a dimension value to a journal draft entry.</summary>
    /// <param name="dimension">The dimension and value to attach.</param>
    /// <param name="entry">The journal and entry to attach it to.</param>
    public async Task DimensionDraftEntryAsync(
        (int DimensionNumber, int Key) dimension,
        (int JournalNumber, int EntryNumber) entry)
    {
        var item = new Open.DimensionDraftEntry
        {
            DimensionNumber = dimension.DimensionNumber,
            DimensionKey = dimension.Key,
            JournalNumber = entry.JournalNumber,
            EntryNumber = entry.EntryNumber,
        };

        await client.Open.DimensionDraftEntries.CreateAsync(item, cancellationToken);
        _cleanup.Add(() => client.Open.DimensionDraftEntries.DeleteAsync(item, cancellationToken));
    }

    /// <summary>
    /// Attaches a dimension value to a line of a sales document.
    /// </summary>
    /// <remarks>
    /// The line comes from a legacy draft invoice rather than from the quote-to-cash service, which
    /// publishes no way to create one: the two surfaces describe the same record, so a draft invoice
    /// created with a line is a sales document line here.
    /// </remarks>
    /// <param name="dimension">The dimension and value to attach.</param>
    /// <param name="draftInvoiceNumber">The legacy draft invoice carrying the line.</param>
    public async Task DimensionSalesDocumentLineAsync(
        (int DimensionNumber, int Key) dimension,
        int draftInvoiceNumber)
    {
        // The two surfaces number the same document differently. A draft invoice the legacy API
        // calls 3 is document 1 here, and the dimension service wants the quote-to-cash number —
        // passing the legacy one answers 400. Only a live response settles which is which.
        var documentNumber = 0;
        await foreach (var document in client.Open.SalesDraftInvoices.AsAsyncEnumerable(cancellationToken))
        {
            if (document.DraftInvoiceNumber == draftInvoiceNumber && document.Number is { } number)
            {
                documentNumber = number;
                break;
            }
        }

        Assert.SkipWhen(
            documentNumber == 0,
            $"Draft invoice {draftInvoiceNumber} does not appear as a quote-to-cash sales document.");

        var lineNumber = 0;
        await foreach (var line in client.Open.SalesDraftInvoiceLines.AsAsyncEnumerable(cancellationToken))
        {
            if (line.Number is { } number)
            {
                lineNumber = number;
                break;
            }
        }

        Assert.SkipWhen(lineNumber == 0, "The seeded draft invoice has no line to attach a dimension to.");

        var item = new Open.DimensionSalesDocumentLine
        {
            DimensionNumber = dimension.DimensionNumber,
            DimensionKey = dimension.Key,
            DocumentNumber = documentNumber,
            LineNumber = lineNumber,
        };

        await client.Open.DimensionSalesDocumentLines.CreateAsync(item, cancellationToken);
        _cleanup.Add(() => client.Open.DimensionSalesDocumentLines.DeleteAsync(item, cancellationToken));
    }

    /// <summary>
    /// Creates one record in every collection this library can create one in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the coverage sweep, which needs the agreement populated rather than any particular
    /// record. Everything is registered for deletion as usual, so the agreement is left as it was
    /// found.
    /// </para>
    /// <para>
    /// Ordered by dependency: a dimension attaches to a booked entry, a draft entry and a budget
    /// figure, and those have to exist first. Several collections are populated as a side effect
    /// rather than directly — a legacy draft invoice with a line is also a quote-to-cash sales
    /// document with a line, because the two surfaces describe one record.
    /// </para>
    /// </remarks>
    public async Task EverythingAsync()
    {
        var customer = await CustomerAsync();
        var product = await ProductAsync("ZZ-PROBE-COVERAGE");
        var invoice = await DraftInvoiceAsync(customer, product);

        await DraftOrderAsync(customer);
        await DraftQuoteAsync(customer);

        var supplier = await SupplierAsync();
        await OpenCustomerContactAsync(customer.CustomerNumber);
        await OpenDeliveryLocationAsync(customer.CustomerNumber);
        await OpenSupplierContactAsync(supplier.SupplierNumber);

        await ProductPriceGroupAsync();
        await ProjectEmployeeAsync(await ProjectEmployeeGroupAsync());
        await WebhookAsync();

        var subscription = await SubscriptionAsync();
        await SubscriptionSubscriberAsync(subscription, customer.CustomerNumber);

        var budgetFigure = await BudgetFigureAsync();
        var entry = await JournalDraftEntryAsync();
        await JournalDraftEntryAccrualAsync(entry.JournalNumber, entry.EntryNumber);

        var dimension = await DimensionValueAsync();
        await DimensionAccountAsync(dimension);
        await DimensionBookedEntryAsync(dimension);
        await DimensionBudgetFigureAsync(dimension, budgetFigure);
        await DimensionDraftEntryAsync(dimension, entry);
        await DimensionSalesDocumentLineAsync(dimension, invoice.DraftInvoiceNumber);
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
        (await FirstPageAsync(client.Rest.CustomerGroups.AsQuery())).CustomerGroupNumber;

    private async Task<int> FirstPaymentTermsAsync() =>
        (await FirstPageAsync(client.Rest.PaymentTerms.AsQuery())).PaymentTermsNumber;

    private async Task<int> FirstVatZoneAsync() =>
        (await FirstPageAsync(client.Rest.VatZones)).VatZoneNumber;

    private async Task<int> FirstProductGroupAsync() =>
        (await FirstPageAsync(client.Rest.ProductGroups)).ProductGroupNumber;

    private async Task<int> FirstLayoutAsync() =>
        (await FirstPageAsync(client.Rest.Layouts)).LayoutNumber;

    /// <summary>
    /// The first supplier group, read from the OpenAPI surface because the legacy one does not
    /// publish the collection at all.
    /// </summary>
    private async Task<int> FirstSupplierGroupAsync()
    {
        await foreach (var group in client.Open.SupplierGroups.AsAsyncEnumerable(cancellationToken))
        {
            return group.Number;
        }

        Assert.Skip("The agreement has no supplier group, which e-conomic normally seeds.");
        return 0;
    }

    /// <summary>The first dimension, which e-conomic seeds on every agreement.</summary>
    private async Task<int> FirstDimensionAsync()
    {
        await foreach (var dimension in client.Open.Dimensions.AsAsyncEnumerable(cancellationToken))
        {
            if (dimension.Number is { } number)
            {
                return number;
            }
        }

        Assert.Skip("The agreement has no dimension.");
        return 0;
    }

    /// <summary>The first journal, read from the OpenAPI surface.</summary>
    private async Task<int> FirstOpenJournalAsync()
    {
        await foreach (var journal in client.Open.Journals.AsAsyncEnumerable(cancellationToken))
        {
            return journal.Number ?? 0;
        }

        Assert.Skip("The agreement has no journal.");
        return 0;
    }

    /// <summary>
    /// Two accounts an entry can move an amount between.
    /// </summary>
    /// <remarks>
    /// A finance voucher posts to one and balances against the other, so both have to accept direct
    /// entries — the same requirement the legacy voucher round-trip has — and both have to be profit
    /// and loss accounts. Posting to a status account answers <c>400 JournalEntryWrongAccountType</c>.
    /// Type 1 is profit and loss; the service defines its seven types in prose rather than as named
    /// values.
    /// </remarks>
    private async Task<(int First, int Second)> TwoPostableAccountsAsync()
    {
        var usable = new List<int>();

        await foreach (var account in client.Open.Accounts.AsAsyncEnumerable(cancellationToken))
        {
            if (account is { IsBarred: false, IsBlockedForDirectEntries: false, Type: 1 })
            {
                usable.Add(account.Number);
            }

            if (usable.Count == 2)
            {
                return (usable[0], usable[1]);
            }
        }

        Assert.Skip("The agreement has fewer than two accounts that accept direct entries.");
        return (0, 0);
    }

    /// <summary>An account a budget figure can be posted against.</summary>
    private async Task<int> FirstOpenAccountAsync()
    {
        await foreach (var account in client.Open.Accounts.AsAsyncEnumerable(cancellationToken))
        {
            if (!account.IsBarred)
            {
                return account.Number;
            }
        }

        Assert.Skip("The agreement has no account that accepts entries.");
        return 0;
    }

    /// <summary>
    /// A number no record in a collection is using yet.
    /// </summary>
    /// <remarks>
    /// Several OpenAPI collections are keyed by a caller-supplied number rather than a server-assigned
    /// one, and answer a conflict when it is taken. Starting above the highest in use keeps a re-run
    /// from colliding with a record a previous run failed to delete.
    /// </remarks>
    private static async Task<int> NextNumberAsync<T>(IAsyncEnumerable<T> existing, Func<T, int?> number)
    {
        var highest = 0;

        await foreach (var item in existing)
        {
            highest = Math.Max(highest, number(item) ?? 0);
        }

        return highest + 1;
    }

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
