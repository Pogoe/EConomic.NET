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

> **Status: pre-release, read-only.** Twenty collection resources on the legacy REST API are
> covered, with filtering, sorting and transparent paging. Creating and updating records is not
> implemented yet, and neither are the newer OpenAPI services. The public API may change before 1.0.

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

## Available resources

`AccountingYears`, `Accounts`, `AppRoles`, `Currencies`, `CustomerGroups`, `Customers`,
`DepartmentalDistributions`, `Departments`, `Employees`, `Journals`, `Layouts`, `PaymentTerms`,
`PaymentTypes`, `ProductGroups`, `Products`, `Suppliers`, `Units`, `VatAccounts`, `VatTypes`,
`VatZones`.

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

Integration tests run against the live demo agreement and are opt-in, so an e-conomic outage never
fails an unrelated build:

```bash
ECONOMIC_RUN_INTEGRATION_TESTS=1 dotnet test tests/EConomic.Net.IntegrationTests
```

Much of the library is generated from the API specifications in `specs/`. Regeneration is a
deliberate, reviewed step rather than part of the build — see [CONTRIBUTING.md](CONTRIBUTING.md).

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for the layout, the
code generation pipeline, and what a change needs before it can be merged.

## License

[MIT](LICENSE)
