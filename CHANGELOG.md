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

[Unreleased]: https://github.com/Pogoe/EConomic.NET/commits/main
