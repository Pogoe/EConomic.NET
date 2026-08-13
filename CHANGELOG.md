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

### Fixed

- `lastUpdated` and 17 other properties are labelled `full-date` in the legacy schemas but carry a
  pattern — and real values — that include a time component. They now convert to `date-time`
  instead of `DateOnly`, which no live response could parse.
- Enum-valued properties failed to deserialize, because e-conomic sends enums as strings while
  `System.Text.Json` defaults to numbers. The generated context now applies a string enum converter.
- `$null:` is a value rather than an operator, so an absent property is filtered with
  `field$eq:$null:`. The previously generated `field$null:` was a syntax error the server rejected.

[Unreleased]: https://github.com/Pogoe/EConomic.NET/commits/main
