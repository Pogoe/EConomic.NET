# EConomic.NET

[![CI](https://github.com/Pogoe/EConomic.NET/actions/workflows/ci.yml/badge.svg)](https://github.com/Pogoe/EConomic.NET/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/EConomic.NET.svg)](https://www.nuget.org/packages/EConomic.NET)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A .NET client for the Visma **e-conomic** accounting APIs — covering both the legacy REST API and
the newer versioned OpenAPI services behind one package, one authentication setup, and one set of
conventions.

> **Status: pre-release.** The foundations (authentication, rate-limit handling, error mapping) are
> in place; endpoint coverage is being built out. The public API may change before 1.0.

> **Unofficial.** This project is not affiliated with, endorsed by, or supported by Visma or
> e-conomic. For API support, contact <api@e-conomic.com>.

## Install

```bash
dotnet add package EConomic.NET
```

Targets `net8.0` and `net10.0`.

## Getting tokens

Both e-conomic API surfaces authenticate with two tokens, neither of which expires:

| Token | Header | What it identifies |
| --- | --- | --- |
| App secret token | `X-AppSecretToken` | Your integration |
| Agreement grant token | `X-AgreementGrantToken` | The customer agreement you are acting on |

Create them by following [Connecting to the APIs using tokens](https://www.e-conomic.com/developer/connect).
For experiments, the literal value `demo` works for both and gives read-only access to e-conomic's
public demo agreement.

## Usage

```csharp
using EConomic.Authentication;

var options = new EconomicOptions
{
    AppSecretToken      = configuration["Economic:AppSecretToken"]!,
    AgreementGrantToken = configuration["Economic:AgreementGrantToken"]!,
};
```

Or, for a quick look at the demo agreement:

```csharp
var options = EconomicOptions.Demo();
```

`EconomicOptions.ToString()` deliberately redacts both tokens, so logging it is safe.

### Rate limiting

e-conomic charges each call a number of tokens against a bucket that refills on a fixed window,
rather than counting requests. Every response reports the budget, and this library parses it:

```csharp
var status = RateLimitStatus.FromResponse(response);
// status.Used, status.Remaining, status.Limit, status.Window, status.CallCost
```

Exhausting the bucket produces a `429`, surfaced as `EconomicRateLimitException` carrying both the
budget and the server's `Retry-After`.

### Errors

Failed requests throw `EconomicApiException`, which parses e-conomic's `problem+json` body:

```csharp
catch (EconomicApiException ex)
{
    // ex.StatusCode, ex.ErrorCode, ex.TraceId, ex.RequestId, ex.RateLimit
}
```

Quote `TraceId` or `RequestId` when contacting e-conomic support — those are what they ask for.

## The two API surfaces

| | Legacy REST API | OpenAPI services |
| --- | --- | --- |
| Base address | `https://restapi.e-conomic.com` | `https://apis.e-conomic.com/{service}api/v{version}/` |
| Coverage | Broadest | Growing |
| Paging | `skippages` + `pagesize` | `cursor` (preferred), or `skipPages` + `pageSize` on `/paged` |
| Docs | [restdocs.e-conomic.com](https://restdocs.e-conomic.com/) | `.../{service}api/redoc.html` |

Both share the same authentication and the same `filter`/`sort` query syntax, which is why they fit
in one package.

## Building

```bash
dotnet build EConomic.Net.slnx
dotnet test EConomic.Net.slnx
```

Integration tests hit the live demo agreement and are opt-in:

```bash
ECONOMIC_RUN_INTEGRATION_TESTS=1 dotnet test tests/EConomic.Net.IntegrationTests
```

## Contributing

Issues and pull requests are welcome. Please include a test for any behaviour change; the public
API surface is locked by `PublicAPI.*.txt` files, so intentional API changes need those updated in
the same commit.

## License

[MIT](LICENSE)
