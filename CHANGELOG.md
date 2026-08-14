# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The package version is independent of the e-conomic API versions: a service moving from v3 to v4
is a mapping change inside the facade, not automatically a major release here.

## [Unreleased]

### Added

- Project scaffolding: multi-targeted `net8.0`/`net10.0` library, xUnit unit and integration test
  projects, central package management, public API surface lock, GitHub Actions CI and release.
- `EconomicOptions` — tokens and base addresses for both API surfaces, with validation and a
  redacting `ToString()`.
- `EconomicAuthenticationHandler` — attaches `X-AppSecretToken` and `X-AgreementGrantToken`, and
  leaves caller-supplied headers alone so the agreement can be overridden per request.
- `RateLimitStatus` — parses e-conomic's token-bucket headers (`X-CallCost`, `X-RateLimiting`).
- `EconomicApiException` and `EconomicRateLimitException` — error mapping for both API surfaces
  (`problem+json` from the OpenAPI services, the legacy shape from the REST API) with error code,
  trace id, request id and the reported rate-limit budget.
- `EconomicLegacyError` — the legacy REST API's error shape, including the `allowedFilteringFields`
  list it returns when a filter is rejected, surfaced as
  `EconomicApiException.AllowedFilteringFields`.
- `EconomicQuery` — a LINQ-style query API with `Where`, `WhereRaw`, `OrderBy`,
  `OrderByDescending`, `ThenBy`, `ThenByDescending` and `WithPageSize`. Queries are immutable, so a
  base query can be reused as the starting point for several others.
- Generated filter and sort surfaces for every published resource, derived from the specifications'
  `x-filterable` and `x-sortable` annotations. Filtering on a property e-conomic will not filter, or
  applying an operator it does not accept, is a compile error rather than a runtime `400`. Filter
  values are escaped using the table the server itself publishes.
- Twenty collection resources on the legacy REST API, exposed as properties on `EconomicClient`:
  accounting years, accounts, app roles, currencies, customer groups, customers, departmental
  distributions, departments, employees, journals, layouts, payment terms, payment types, product
  groups, products, suppliers, units, VAT accounts, VAT types and VAT zones. Thirteen more, and
  writes, are listed below.
- Transparent paging through `AsAsyncEnumerable`, which fetches a page at a time as results are
  consumed, plus `GetPageAsync` for callers who want to manage paging themselves. There is
  deliberately no method that loads every page into a list.
- `EconomicRetryHandler` — retries `429` and transient `5xx` responses with exponential backoff and
  full jitter, honouring `Retry-After`. Configurable through `EconomicOptions.Retry`. Driven by
  `TimeProvider`, so the backoff is tested without sleeping.
- `EconomicIdempotencyHandler` — attaches an `Idempotency-Key` to non-`GET` requests. The key is
  assigned outside the retry loop so every attempt reuses it, and a request without one is never
  retried.
- `AddEconomicClient` — dependency injection registration over `IHttpClientFactory`, with the
  handler order that makes idempotency and retry work together.
- Native AOT smoke test, published and executed in CI, so the `IsTrimmable` and `IsAotCompatible`
  claims are tested rather than asserted.

- Write support for customers, customer groups, payment terms, products, suppliers, units, a
  customer's contacts and delivery locations, and the invoice, order and quote drafts. Each is now
  a `{Entity}Resource` exposing the writes e-conomic supports alongside the query methods, which
  continue to return a query, so existing read code is unaffected apart from the property's type.
  Twelve resources, each offering exactly the operations e-conomic documents for it — accounting
  years get a create and nothing else, and no booked invoice can be deleted.
- `{Entity}Create` and `{Entity}Update` write models, carrying only the properties e-conomic
  accepts. Server-maintained values such as `balance` are absent, and references to other resources
  are flattened to their numbers.
- The invoice, order and quote families as queryable collections: `DraftInvoices`,
  `BookedInvoices`, `SentInvoices`, `PaidInvoices`, `UnpaidInvoices`, `OverdueInvoices`,
  `NotDueInvoices`, `DraftOrders`, `SentOrders`, `ArchivedOrders`, `DraftQuotes`, `SentQuotes` and
  `ArchivedQuotes`. These sit one segment below a namespace — `/invoices/drafts` rather than
  `/invoices` — and were invisible to a discovery pass that only considered single-segment paths.
  Thirty-three resources in total, up from twenty. The two whose generated component name is shaped
  by what else the specifications contain are published under the name that describes the type:
  `DraftInvoice` and `BookedInvoice`, rather than `…Summary`.
- Nested collections: a customer's contacts and delivery locations, reached through the parent
  because they cannot be addressed without its identifier —
  `client.Customers.Contacts(customerNumber).CreateAsync(...)`. Each supports the same querying,
  paging and writes as a top-level resource.
- Composite properties on the read models. An entity's nested objects and arrays each get their own
  public record — an invoice's `Recipient`, `Delivery`, `Notes` and `References`, a departmental
  distribution's `Distributions`, a product's `ProductGroup`. These were previously absent from the
  public models altogether, which is the worst kind of gap: nothing failed, the data simply was not
  there. The count of properties the facade could not express went from 117 to 13, and every one
  that remains is a server-assigned link.
- Write support for draft invoices, orders and quotes, including their lines:
  `client.DraftInvoices.CreateAsync(new DraftInvoiceCreate { … Lines = [new DraftInvoiceCreateLine { … }] })`.
  Composite write models are generated the same way as the read ones, so a nested object is a
  record and an array of them is a list. Verified end to end against a live agreement: the server
  prices the invoice from the lines it is sent.
- `DraftInvoices.BookAsync` — books a draft invoice and returns the booked one. e-conomic models
  this as a `POST` to `/invoices/booked` carrying a reference to the draft, which is an action on a
  draft rather than the creation of a booked invoice, so it is hand-written rather than generated.
  Booking is not reversible, and the documentation says so.
- `AccountingYears.CreateAsync` — an accounting year is created from two dates and nothing else,
  and e-conomic publishes neither an update nor a delete for one, so the resource offers only a
  create. It is identified by the year as a string rather than by a number, which is why it had
  been held back.
- Journal vouchers — `client.Journals.Vouchers(journalNumber)`, with querying, paging and a create.
  This is how entries are posted, and it needed two things the facade did not have: a resource type
  for a parent that is itself read-only, and a create whose response is a collection. Its payload is
  also the deepest in the API, an object of five arrays of entries, one array per entry kind.
- Journal entries — `client.Journals.Entries(journalNumber)`, with a delete. That endpoint appears
  in no schema and on no documentation page; every entry carries it as a `metaData.delete` link,
  which is the server describing its own records. Deleting an entry is how a mis-posted voucher is
  undone, since a voucher itself has no delete.
- `AsQuery()` on each resource, for obtaining an unfiltered query.
- `DELETE` support, from the 21 endpoints described in the published documentation. e-conomic
  publishes no schema for `DELETE` — it has neither request nor response body — so these are issued
  directly rather than through the generated clients, and are documented as documentation-derived.

### Fixed

- Unset optional properties were serialized as explicit `null`, which e-conomic rejects outright
  with "Expected String but got Null". Every write would have failed. The generated clients now
  omit them: the ignore condition has to be set on the options the clients serialize through, not
  only through `JsonSourceGenerationOptions`, which applies to the context's own instance.
- Unset optional numbers were serialized as `0`, which e-conomic rejects for identifiers —
  `customerNumber` declares a minimum of 1. They are nullable on the write payloads now, so an
  unset value is omitted while an explicit zero is still sent.
- Update payload types were missing from the serialization metadata. The root scanner only matched
  a request body in first position, so anything taking an identifier first — every `PUT` — was
  skipped, and would have failed at run time once writes stopped doubling as their own response.
- Write responses were typed as the request payload, so everything the request did not describe was
  discarded — including the identifier and `self` that e-conomic returns for every create. They now
  reference the read entity, which is what the server actually sends.
- Every `POST` was declared as returning `200` only, so the generated clients rejected the `201
  Created` that e-conomic documents for resource creation — every create would have failed. `PUT`
  needed it too: it is an upsert, and answers `201` when the identifier does not exist.
- `productNumber` path parameters were typed as integers while product numbers are strings, making
  an alphanumeric product number unrepresentable.
- `DELETE` was always considered safe to retry, on the HTTP convention that it is idempotent.
  e-conomic documents the opposite on its drafts and sent endpoints, where a repeated delete
  answers `404`, so a retry after a lost response reported failure for a delete that had succeeded.
  It now requires an `Idempotency-Key`, exactly as `POST` does.
- `lastUpdated` and 17 other properties are labelled `full-date` in the legacy schemas but carry a
  pattern — and real values — that include a time component. They now convert to `date-time`
  instead of `DateOnly`, which no live response could parse.
- Enum-valued properties failed to deserialize, because e-conomic sends enums as strings while
  `System.Text.Json` defaults to numbers. The generated context now applies a string enum converter.
- `$null:` is a value rather than an operator, so an absent property is filtered with
  `field$eq:$null:`. The previously generated `field$null:` was a syntax error the server rejected.
- A property described by `oneOf` was mapped from its own `properties`, which produced code
  referring to members the generated type did not have. NSwag picks one branch to generate from,
  and there is no way to know which, so these are now reported rather than guessed —
  `paymentDetails.paymentType`, whose six alternatives cover different payment forms, is the case.
- Posting a journal voucher succeeded and then failed to read the reply: e-conomic answers with an
  array of vouchers, while the specification describes a single one.
- Unset optional numbers were only made nullable at the top level of a write payload, so one nested
  inside an object or an array item was still serialized as `0`. A draft invoice whose line did not
  set `lineNumber` and `sortKey` — neither of which a caller has any reason to set — was rejected
  outright, both declaring a minimum of 1.

[Unreleased]: https://github.com/Pogoe/EConomic.NET/commits/main
