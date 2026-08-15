# EConomic.NET

[![CI](https://github.com/Pogoe/EConomic.NET/actions/workflows/ci.yml/badge.svg)](https://github.com/Pogoe/EConomic.NET/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Pogoe.EConomic.svg)](https://www.nuget.org/packages/Pogoe.EConomic)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A .NET client for the Visma **e-conomic** accounting APIs, with queries written as C# lambdas that
only compile if e-conomic will actually accept them.

```csharp
await foreach (var customer in client.Rest.Customers
    .Where(c => c.Country == "Denmark" && c.Balance > 0)
    .OrderByDescending(c => c.Balance)
    .AsAsyncEnumerable(cancellationToken))
{
    Console.WriteLine($"{customer.CustomerNumber}: {customer.Name} owes {customer.Balance}");
}
```

> **Status: pre-release.** Thirty-three collection resources on the legacy REST API are covered,
> with filtering, sorting and transparent paging; twelve of them also support writing. All
> fourteen of the newer OpenAPI services — accounting years, accounts, booked entries, budgets,
> customers, dimensions, documents, journals, products, projects, quote-to-cash, subscriptions,
> suppliers and webhooks, fifty-seven collections in all — are covered under `client.Open`. The
> public API may change before 1.0.

> **Unofficial.** This project is not affiliated with, endorsed by, or supported by Visma or
> e-conomic. For API support, contact <api@e-conomic.com>.

## Install

```bash
dotnet add package Pogoe.EConomic
```

The package id is owner-prefixed; the project, assembly and namespaces are all `EConomic`, so
nothing in your code refers to the prefix.

Targets `net8.0` and `net10.0`. The only dependency is `Microsoft.Extensions.Http`.

## Getting tokens

Both e-conomic API surfaces authenticate with two tokens, neither of which expires:

| Token | Header | What it identifies |
| --- | --- | --- |
| App secret token | `X-AppSecretToken` | Your integration |
| Agreement grant token | `X-AgreementGrantToken` | The customer agreement you are acting on |

Create them by following [Connecting to the APIs using tokens](https://www.e-conomic.com/developer/connect).
For experiments, the literal value `demo` works for both and gives read-only access to e-conomic's
public demo agreement — that is what `EconomicOptions.Demo()` returns.

## Getting started

With dependency injection, which is the intended path:

```csharp
using EConomic.DependencyInjection;

builder.Services.AddEconomicClient(options =>
{
    options.AppSecretToken      = builder.Configuration["Economic:AppSecretToken"]!;
    options.AgreementGrantToken = builder.Configuration["Economic:AgreementGrantToken"]!;
});
```

That registers `EconomicClient` as a typed client over `IHttpClientFactory` and wires up
authentication, retries and idempotency keys in the right order. Inject `EconomicClient` wherever
you need it. `AddEconomicClient` returns the `IHttpClientBuilder`, so you can keep configuring the
underlying pipeline.

Without dependency injection, pass options directly and the client configures the `HttpClient` for
you:

```csharp
using var http = new HttpClient();
var client = new EconomicClient(http, EconomicOptions.Demo());
```

`EconomicOptions.ToString()` deliberately redacts both tokens, so logging it is safe.

## Querying

Every resource on the client exposes `Where`, `OrderBy`, `ThenBy` and async enumeration:

```csharp
var query = client.Rest.Customers
    .Where(c => c.Barred == false && c.Balance >= 1000m)
    .OrderBy(c => c.Name)
    .ThenByDescending(c => c.Balance);
```

Queries are immutable — each call returns a new query, so a base query can be safely reused as the
starting point for several others. Nothing is sent until you enumerate.

### Why it is not `IQueryable`

Because most of LINQ would be a lie. A `Customer` has 33 properties, of which e-conomic will filter
on 20 and sort by 18. An `IQueryable` provider advertises that the whole of LINQ works, then turns
everything outside those subsets into a runtime `400`.

Instead, `Where` takes a lambda over a **generated filter type** containing only the properties
e-conomic will filter on, each typed to expose only the operators it accepts. So neither of these
compiles:

```csharp
client.Rest.Customers.Where(c => c.DueAmount > 0)     // CS1061: dueAmount is not filterable
client.Rest.Customers.Where(c => c.Balance.Like("*")) // CS1061: Like is for text fields
```

Sorting works the same way, from a separate generated type. Filterability and sortability are
independent flags in the specification, and `barred` is a real case of a property you can filter on
but not sort by — which is why they are two types rather than one.

### Operators

| C# | Sent as | Available on |
| --- | --- | --- |
| `==` `!=` | `$eq:` `$ne:` | all fields |
| `<` `<=` `>` `>=` | `$lt:` `$lte:` `$gt:` `$gte:` | numeric, date |
| `.Like("Acme*")` | `$like:` | text fields |
| `.In(1, 2, 3)` / `.NotIn(...)` | `$in:` `$nin:` | numeric fields, max 200 values |
| `== null` | `$eq:$null:` | all fields |
| `&&` `\|\|` | `$and:` `$or:`, parenthesised | — |

Values are escaped for you. e-conomic treats `$ ( ) * [ ] ,` as syntax, and a customer named
`Ø & Sønner (A/S)` would otherwise produce a filter the server rejects — or worse, one it
misinterprets.

`Like` without a wildcard means *contains*: `Like("Acme")` matches `Acme Ltd` and `The Acme Co`.
Anchor it with `Like("Acme*")` if you want a prefix match.

### Paging

`AsAsyncEnumerable` fetches pages as you consume them, so a `foreach` that stops early stops
fetching:

```csharp
await foreach (var product in client.Rest.Products.AsAsyncEnumerable(cancellationToken))
{
    // one request per page, transparently
}
```

There is no method that silently loads everything into a list. If you want to control paging
yourself, ask for a page at a time:

```csharp
var page = await client.Rest.Products.WithPageSize(100).GetPageAsync(0, cancellationToken);
// page.Items, page.PageIndex, page.PageSize, page.HasMore
```

Page size defaults to 20 and the API caps it at 1000.

### When the generated filter is missing something

The legacy schemas under-report what the server accepts — on Customers they mark 20 properties
filterable while the server accepts 21, omitting `pNumber`. They never claim something the server
rejects, so the error is always in the safe direction, but it means the generated type is not the
whole story. For those cases there is a raw escape hatch:

```csharp
client.Rest.Customers.WhereRaw("pNumber$eq:1234567890");
```

If a raw filter is wrong, e-conomic replies with the fields it *would* have accepted, and those
come back on the exception as `AllowedFilteringFields`.

You can always inspect what a query will send:

```csharp
query.GetFilterExpression(); // "barred$eq:false$and:balance$gte:1000"
query.GetSortExpression();   // "name,-balance"
```

## Creating, updating and deleting

Writable resources take a purpose-built model carrying only what e-conomic accepts. Server-
maintained values such as `balance` are absent, and references to other resources are flattened to
their numbers:

```csharp
var customer = await client.Rest.Customers.CreateAsync(
    new CustomerCreate
    {
        Name = "Acme A/S",
        Currency = "DKK",
        CustomerGroupNumber = 1,
        PaymentTermsNumber = 1,
        VatZoneNumber = 1,
    },
    cancellationToken);

await client.Rest.Customers.DeleteAsync(customer.CustomerNumber, cancellationToken);
```

A create returns the whole resource as the server stored it, including the identifier it assigned
and everything the request never mentioned.

`UpdateAsync` **replaces** rather than patches — a property left unset is cleared — and e-conomic
treats it as an upsert, answering `201 Created` when the identifier does not exist.

Composite properties are records of their own, and repeating groups are lists:

```csharp
var invoice = await client.Rest.DraftInvoices.CreateAsync(
    new DraftInvoiceCreate
    {
        Date = DateOnly.FromDateTime(DateTime.Today),
        Currency = "DKK",
        LayoutNumber = 21,
        CustomerNumber = customer.CustomerNumber,
        PaymentTerms = new DraftInvoiceCreatePaymentTerms { PaymentTermsNumber = 1 },
        Recipient = new DraftInvoiceCreateRecipient { Name = "Acme A/S", VatZoneNumber = 1 },
        Lines =
        [
            new DraftInvoiceCreateLine
            {
                Description = "Consulting",
                Product = new DraftInvoiceCreateLineProduct { ProductNumber = "CONS-1" },
                Quantity = 2,
                UnitNetPrice = 100,
            },
        ],
    },
    cancellationToken);

// The server prices it: NetAmount is 200, GrossAmount includes VAT.
```

Booking a draft turns it into a booked invoice:

```csharp
var booked = await client.Rest.DraftInvoices.BookAsync(invoice.DraftInvoiceNumber, cancellationToken);
```

**Booking cannot be undone.** A booked invoice is part of the accounting record — it cannot be
edited or deleted, only corrected with a credit note — and the draft no longer exists afterwards.
Pass `bookWithNumber` to choose the invoice number, or `sendBy` to have e-conomic send it.

Collections that cannot be addressed without their parent are reached through it:

```csharp
var contacts = client.Rest.Customers.Contacts(customer.CustomerNumber);
await contacts.CreateAsync(new CustomerContactCreate { Name = "Jane Doe" }, cancellationToken);
```

Journal vouchers work the same way, and are how entries are posted:

```csharp
var vouchers = await client.Rest.Journals.Vouchers(journalNumber).CreateAsync(
    new JournalVoucherCreate
    {
        AccountingYear = new JournalVoucherCreateAccountingYear { Year = "2026" },
        Entries = new JournalVoucherCreateEntries
        {
            FinanceVouchers =
            [
                new JournalVoucherCreateEntriesFinanceVoucher
                {
                    Date = DateOnly.FromDateTime(DateTime.Today),
                    Amount = 100m,
                    AccountNumber = 1010,
                    ContraAccountNumber = 1020,
                    Text = "Consulting",
                },
            ],
        },
    },
    cancellationToken);
```

That one returns a **list**: e-conomic may split the entries it was sent across several vouchers.

A voucher has no delete, but the entries it produced do, which is how a mis-posted one is undone:

```csharp
await client.Rest.Journals.Entries(journalNumber).DeleteAsync(journalEntryNumber, cancellationToken);
```

One delete removes a whole collection. e-conomic exposes `DELETE /invoices/drafts` with no
identifier and no filter, so it is named apart from `DeleteAsync` and needs an argument you have to
mean:

```csharp
await client.Rest.DraftInvoices.DeleteEveryDraftAsync(DraftInvoiceBulkDelete.EveryDraft, cancellationToken);
```

Deletes are **not** retried without an `Idempotency-Key`. e-conomic reuses identifiers, so a
repeated delete can land on a different record than the one you meant — see
[Retries and idempotency](#retries-and-idempotency).

## Available resources

Queryable: `AccountingYears`, `Accounts`, `AppRoles`, `ArchivedOrders`, `ArchivedQuotes`,
`BookedInvoices`, `Currencies`, `CustomerGroups`, `Customers`, `DepartmentalDistributions`,
`Departments`, `DraftInvoices`, `DraftOrders`, `DraftQuotes`, `Employees`, `Journals`, `Layouts`,
`NotDueInvoices`, `OverdueInvoices`, `PaidInvoices`, `PaymentTerms`, `PaymentTypes`,
`ProductGroups`, `Products`, `SentInvoices`, `SentOrders`, `SentQuotes`, `Suppliers`,
`UnpaidInvoices`, `Units`, `VatAccounts`, `VatTypes`, `VatZones`.

Also writable: `AccountingYears`, `Customers` (and its `Contacts` and `DeliveryLocations`),
`CustomerGroups`, `DraftInvoices`, `DraftOrders`, `DraftQuotes`, `Journals.Vouchers`,
`PaymentTerms`, `Products`, `Suppliers`, `Units`.

Each offers exactly the operations e-conomic documents for it, no more: an accounting year can be
created but never updated or deleted, and a booked invoice cannot be deleted at all.

Danish domain terms keep their e-conomic names — `vatZone`, `paymentTerms`, `bookedEntries` — so
that everything maps back to the official docs without a translation step.

## Rate limiting

e-conomic charges each call a number of tokens against a bucket that refills on a fixed window,
rather than counting requests. Every response reports the budget:

```csharp
var status = RateLimitStatus.FromResponse(response);
// status.Used, status.Remaining, status.Limit, status.Window, status.CallCost
```

Exhausting the bucket produces a `429`, surfaced as `EconomicRateLimitException` carrying both the
parsed budget and the server's `Retry-After`.

## Retries and idempotency

`429` and transient `5xx` responses are retried with exponential backoff and full jitter. Tune it
through the options:

```csharp
options.Retry.MaxAttempts = 5;
options.Retry.BaseDelay   = TimeSpan.FromMilliseconds(500);
options.Retry.MaxDelay    = TimeSpan.FromSeconds(30);
```

A request that is not idempotent is **never** retried unless it carries an `Idempotency-Key`, which
the client attaches to non-`GET` requests by default. The key is generated once, outside the retry
loop, so every attempt reuses it and e-conomic can recognise the replay — a retried invoice is not
a second invoice. Set `options.SendIdempotencyKeys = false` to opt out, at the cost of those
requests no longer being retried.

## Errors

Failed requests throw `EconomicApiException`. The two API surfaces return entirely different error
shapes — `problem+json` from the OpenAPI services, a bespoke shape from the legacy REST API — and
both are parsed into the same members:

```csharp
catch (EconomicApiException ex)
{
    // ex.StatusCode, ex.ErrorCode, ex.Detail, ex.DeveloperHint
    // ex.TraceId, ex.RequestId, ex.Errors, ex.AllowedFilteringFields, ex.RateLimit
}
```

Quote `RequestId` when contacting e-conomic support — that is what they ask for.

## Trimming and native AOT

The package is annotated `IsTrimmable` and `IsAotCompatible`, serializes through a source-generated
`JsonSerializerContext`, and has no reflection-based serialization path. That claim is tested
rather than asserted: CI publishes a console app with `PublishAot`, runs the native binary, and
exercises the real deserialization path on every commit.

## The two API surfaces

| | Legacy REST API | OpenAPI services |
| --- | --- | --- |
| Reached through | `client.Rest` | `client.Open` |
| Base address | `https://restapi.e-conomic.com` | `https://apis.e-conomic.com/{service}api/v{version}/` |
| Status here | Implemented, reads and writes | 14 of 14 services |
| Coverage | Broadest | Newer, per-service versioned |
| Paging | `skippages` + `pagesize` | `cursor` (preferred), or `skipPages` + `pageSize` |
| Identifiers | assigned by the server | supplied by the caller |
| Concurrency | last write wins | `objectVersion` — an update without it is rejected `409` |
| Docs | [restdocs.e-conomic.com](https://restdocs.e-conomic.com/) | `.../{service}api/redoc.html` |

They belong in one package because everything underneath the models is shared: one pair of tokens,
one transport, the same `filter`/`sort` syntax, and — measured against a live agreement — a single
rate-limit budget, whose `X-RateLimiting` header moves together whichever host answers.

Everything above that is separate, and the call site says which surface it means. The two are not
interchangeable even where they look it: both publish a `Customer`, but this one spells a payment
terms reference `paymentTermsNumber` and lets the server assign the customer number, while the
OpenAPI services spell it `paymentTermId` and expect you to supply the number yourself.

## The OpenAPI services

The newer services sit under `client.Open` and behave differently enough that the two are never
mixed up:

```csharp
// Cursor paging by default — no page numbers, no total, no limit on how far it reaches.
await foreach (var customer in client.Open.Customers.AsAsyncEnumerable(cancellationToken))
{
    Console.WriteLine(customer.Name);
}

var total = await client.Open.Customers.CountAsync(cancellationToken);
```

Three things differ from the legacy surface, all of them verified against a live agreement:

**You supply the identifier, and a create returns only that.** Omitting `CustomerNumber` fails with
"The field CustomerNumber must be between 1 and 999999999", and the response carries the number and
nothing else — so read the record back if you want it.

**Updates are read-modify-write.** Every record carries an `objectVersion`, and an update that does
not send the current one is rejected with `409` and `EconomicConcurrencyException`. Nothing is
written, and retrying the same request cannot help:

```csharp
var customer = await client.Open.Customers.GetAsync(number, cancellationToken);

await client.Open.Customers.UpdateAsync(
    number,
    customer with { Name = "Acme A/S" },   // objectVersion comes along with it
    cancellationToken);
```

**Sorting moves the query to the classic endpoint.** e-conomic ignores `sort` on a cursor request —
it answers `200` with unordered data — so `OrderBy` switches endpoints rather than letting that
happen. The classic endpoint stops after 10 000 items, so filter a large collection down before
sorting it. Asking for a cursor page explicitly on a sorted query throws instead.

Filtering is far more restricted here than on the legacy API: of a customer's 55 properties, the
service will filter on exactly one. That is published per property, so the filter surface is
generated from it and the restriction is a compile error rather than a surprise.

**Names carry their service.** Customers and suppliers both publish a `Contact`, and they are
different shapes, so they are `CustomerContact` and `SupplierContact` — as are the collections they
hang off:

```csharp
await foreach (var contact in client.Open.CustomerContacts
    .Where(c => c.CustomerNumber == number)
    .AsAsyncEnumerable(cancellationToken))
{
    Console.WriteLine(contact.Name);
}

var groups = await client.Open.SupplierGroups.GetPageAsync(0, cancellationToken);
```

Qualifying every name, rather than only the ones that clash today, is what keeps adding a service
from renaming types you already compile against. The prefix is dropped where it would stutter, so
`Customer` and `Account` stay as they are.

**Some collections are scoped by a parent**, because e-conomic addresses them that way. Those are
methods rather than properties, taking the identifier they need:

```csharp
var zones = await client.Open.ProductZones(productGroupNumber).GetPageAsync(0, cancellationToken);
```

**Not every collection publishes both listings**, and the surface reflects that rather than failing
when you use it. `AccountingYears` has only the classic paged one, so it pages throughout and asking
it for a cursor page throws. `MatchedBookedEntriesPairs` has only the cursor, so it offers no
`OrderBy`, no `GetPageAsync` and no `CountAsync` — sorting exists only on the classic listing.

Several collections offer less than the pattern suggests, because the service does.
`AccountKeyFigureCodes` is read-only, and so is `ProductSalesPricesInCurrency` — e-conomic publishes
its writes under a product, keyed by currency. `AccountTotalIntervals` has no delete: that one is
addressed by account number and starting account together. `Products` has no `CountAsync`, because
it is the one collection here that publishes no `/count`, and neither `ProjectMileagePrices` nor
`ProjectTimeEntryPrices` does either. `SalesDraftInvoiceLines` is read-only for the same reason
`ProductSalesPricesInCurrency` is — reading them is a collection of its own, writing them is
published under the invoice they belong to — and all the order and quote lines are read-only
outright. The suppliers service publishes no suppliers at all; those remain at
`client.Rest.Suppliers`.

**The quote-to-cash service is named `Sales` here.** e-conomic calls it `q2capi`, for the
quote-to-cash process it covers, and `Q2C` in a type name tells a reader nothing. `Sales` is the
service's own word for the same material — its status type is `SalesDocumentStatusRoute`, and the
dimensions service publishes `/dimension-data/sales-document-lines` for these very records.

Its order and quote lines are scoped by a document status in the path, and appear once per status
rather than as a method taking one: `SalesDraftOrderLines`, `SalesSentOrderLines`,
`SalesArchivedOrderLines`, and the same three for quotes. That is how the legacy surface already
models the documents these lines belong to — `client.Rest.DraftOrders`, `SentOrders`,
`ArchivedOrders`. All three carry the same `SalesOrderLine`.

`SalesDraftInvoices` is the counterpart to `client.Rest.DraftInvoices` rather than a replacement:
booking a draft is published only on the legacy surface.

**The projects service needs the Project module.** e-conomic sells its modules separately, and an
agreement without this one answers `403` to every projects collection except `ProjectEmployees` and
`ProjectEmployeeGroups`. That surfaces as an ordinary `EconomicApiException` whose `ErrorCode` is
`AccessDeniedAgreementMissingModules`; there is nothing the client can do about it but report it.

Two of its collections carry longer names than e-conomic's own, because e-conomic gives two
different things the same one. `ProjectActivities` is the catalogue of activities — what a time
entry's `activityNumber` points at — while `ProjectActivityAssignments` (`/project-activities`) is
one of those attached to a project, with its own number, a date range and a responsible employee.
Likewise `ProjectEmployees` carries an employee's phone and email, and `ProjectEmployeeDetails`
(`/project-employees`) the same employees' rates and approval rights; neither is a superset of the
other.

## Building

```bash
dotnet build EConomic.Net.slnx
dotnet test tests/EConomic.Net.Tests
```

Integration tests run against a live agreement and are opt-in, so an e-conomic outage never fails
an unrelated build. They create the records they assert on and delete them again, so point them at
a **throwaway agreement**:

```bash
export ECONOMIC_APP_SECRET_TOKEN=…
export ECONOMIC_AGREEMENT_GRANT_TOKEN=…
ECONOMIC_RUN_INTEGRATION_TESTS=1 dotnet test tests/EConomic.Net.IntegrationTests
```

Much of the library is generated from the API specifications in `specs/`. Regeneration is a
deliberate, reviewed step rather than part of the build — see [CONTRIBUTING.md](CONTRIBUTING.md).

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for the layout, the
code generation pipeline, and what a change needs before it can be merged.

## License

[MIT](LICENSE)
