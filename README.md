# EConomic.NET

[![CI](https://github.com/Pogoe/EConomic.NET/actions/workflows/ci.yml/badge.svg)](https://github.com/Pogoe/EConomic.NET/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Pogoe.EConomic.svg)](https://www.nuget.org/packages/Pogoe.EConomic)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A .NET client for the Visma **e-conomic** accounting APIs, with queries written as C# lambdas that
only compile if e-conomic will actually accept them.

```csharp
await foreach (var customer in client.Customers
    .Where(c => c.Country == "Denmark" && c.Balance > 0)
    .OrderByDescending(c => c.Balance)
    .AsAsyncEnumerable(cancellationToken))
{
    Console.WriteLine($"{customer.CustomerNumber}: {customer.Name} owes {customer.Balance}");
}
```

> **Status: pre-release.** Thirty-three collection resources on the legacy REST API are covered,
> with filtering, sorting and transparent paging. Eleven of them also support creating, updating
> and deleting. The newer OpenAPI services are not implemented yet, and the public API may change
> before 1.0.

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
var query = client.Customers
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
client.Customers.Where(c => c.DueAmount > 0)     // CS1061: dueAmount is not filterable
client.Customers.Where(c => c.Balance.Like("*")) // CS1061: Like is for text fields
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
await foreach (var product in client.Products.AsAsyncEnumerable(cancellationToken))
{
    // one request per page, transparently
}
```

There is no method that silently loads everything into a list. If you want to control paging
yourself, ask for a page at a time:

```csharp
var page = await client.Products.WithPageSize(100).GetPageAsync(0, cancellationToken);
// page.Items, page.PageIndex, page.PageSize, page.HasMore
```

Page size defaults to 20 and the API caps it at 1000.

### When the generated filter is missing something

The legacy schemas under-report what the server accepts — on Customers they mark 20 properties
filterable while the server accepts 21, omitting `pNumber`. They never claim something the server
rejects, so the error is always in the safe direction, but it means the generated type is not the
whole story. For those cases there is a raw escape hatch:

```csharp
client.Customers.WhereRaw("pNumber$eq:1234567890");
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
var customer = await client.Customers.CreateAsync(
    new CustomerCreate
    {
        Name = "Acme A/S",
        Currency = "DKK",
        CustomerGroupNumber = 1,
        PaymentTermsNumber = 1,
        VatZoneNumber = 1,
    },
    cancellationToken);

await client.Customers.DeleteAsync(customer.CustomerNumber, cancellationToken);
```

A create returns the whole resource as the server stored it, including the identifier it assigned
and everything the request never mentioned.

`UpdateAsync` **replaces** rather than patches — a property left unset is cleared — and e-conomic
treats it as an upsert, answering `201 Created` when the identifier does not exist.

Composite properties are records of their own, and repeating groups are lists:

```csharp
var invoice = await client.DraftInvoices.CreateAsync(
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
var booked = await client.DraftInvoices.BookAsync(invoice.DraftInvoiceNumber, cancellationToken);
```

**Booking cannot be undone.** A booked invoice is part of the accounting record — it cannot be
edited or deleted, only corrected with a credit note — and the draft no longer exists afterwards.
Pass `bookWithNumber` to choose the invoice number, or `sendBy` to have e-conomic send it.

Collections that cannot be addressed without their parent are reached through it:

```csharp
var contacts = client.Customers.Contacts(customer.CustomerNumber);
await contacts.CreateAsync(new CustomerContactCreate { Name = "Jane Doe" }, cancellationToken);
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

Also writable: `Customers` (and its `Contacts` and `DeliveryLocations`), `CustomerGroups`,
`DraftInvoices`, `DraftOrders`, `DraftQuotes`, `PaymentTerms`, `Products`, `Suppliers`, `Units`.

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
| Base address | `https://restapi.e-conomic.com` | `https://apis.e-conomic.com/{service}api/v{version}/` |
| Status here | Implemented, read-only | Not implemented yet |
| Coverage | Broadest | Newer, per-service versioned |
| Paging | `skippages` + `pagesize` | `cursor` (preferred), or `skipPages` + `pageSize` |
| Docs | [restdocs.e-conomic.com](https://restdocs.e-conomic.com/) | `.../{service}api/redoc.html` |

Both share the same authentication and the same `filter`/`sort` syntax, which is why they belong in
one package. When the OpenAPI services land they will sit under `EConomic.Open`, alongside the
existing `EConomic.Rest`, on the same client.

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
