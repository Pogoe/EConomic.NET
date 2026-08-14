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
- Twenty read-only resources on the legacy REST API, exposed as properties on `EconomicClient`:
  accounting years, accounts, app roles, currencies, customer groups, customers, departmental
  distributions, departments, employees, journals, layouts, payment terms, payment types, product
  groups, products, suppliers, units, VAT accounts, VAT types and VAT zones.
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

- Write support for customers, customer groups, payment terms, suppliers and units. Each is now a
  `{Entity}Resource` exposing the writes e-conomic supports alongside the query methods, which
  continue to return a query, so existing read code is unaffected apart from the property's type.
  Three creates, five updates and five deletes in total.
- `{Entity}Create` and `{Entity}Update` write models, carrying only the properties e-conomic
  accepts. Server-maintained values such as `balance` are absent, and references to other resources
  are flattened to their numbers.
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

[Unreleased]: https://github.com/Pogoe/EConomic.NET/commits/main
