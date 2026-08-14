# Contributing to EConomic.NET

Thanks for taking an interest. Issues and pull requests are both welcome — a bug report describing
what e-conomic actually returned is often more valuable than a patch, because most of the difficult
decisions in this repository come from the API behaving differently than its documentation says.

## Getting set up

You need the **.NET 10 SDK** (pinned in `global.json`) and the **.NET 8** runtime or SDK, since the
library multi-targets both.

```bash
dotnet tool restore                    # NSwag, used only when regenerating
dotnet build EConomic.Net.slnx
dotnet test tests/EConomic.Net.Tests
```

The solution is in `.slnx` format. Add projects with
`dotnet sln EConomic.Net.slnx add <path>` rather than editing it by hand.

## Repository layout

```
specs/                        API specifications. The source of truth for all generated code.
  legacy/                     160 JSON Schema draft-03 files, e-conomic's originals. Never edit.
  legacy-openapi/             OpenAPI 3.0 documents converted from them. Generated. Never edit.
src/EConomic.Net/
  Authentication/             EconomicOptions, EconomicAuthenticationHandler
  Http/                       Retry, idempotency, rate-limit parsing
  Exceptions/                 Unified error mapping across both API surfaces
  Querying/                   Filter and sort translation, value escaping
  Pagination/                 Page models and transparent paging
  Rest/                       The legacy REST API surface
    Generated/                NSwag output. Internal, analyzer-exempt, never edited by hand.
tools/EConomic.SpecConverter/ Spec conversion and all code generation
tools/nswag/                  NSwag configuration
tests/                        Unit, integration and AOT smoke tests
```

## The rule that matters most

**Generated client code is never public.** NSwag's output in `Rest/Generated/` is `internal`; the
hand-written facade is what consumers see. This is the whole reason the facade exists: a
specification refresh must not become a breaking change for the package.

If you find yourself returning a generated type from a public member, stop and add a facade type.

There is exactly one deliberate exception. The generated **filter and sort surfaces** are public,
because they only work as a compile-time guard if consumers can write lambdas against them. A
consequence worth understanding before you change them: a property losing `x-filterable` moves the
public API baseline, and that is correct — it breaks callers either way, and a build error beats a
runtime `400`.

## Design rules for the public surface

- **Async only.** Every I/O method returns `Task`/`ValueTask`, ends in `Async`, and takes a
  `CancellationToken` as its last parameter. No sync-over-async, no `.Result`.
- **Collections are `IAsyncEnumerable<T>`** that page transparently, plus an explicit
  page-at-a-time method. Never silently fetch every page into a `Task<List<T>>`.
- **Built on `IHttpClientFactory`.** Never `new HttpClient()` internally.
- **Tokens never leak.** They are not logged, not in exception messages, and `ToString()` redacts
  them. There is a test for this.
- **`System.Text.Json` with a source-generated context.** No Newtonsoft, and no reflection-based
  serialization — it would break the trimming and AOT guarantees.
- **Expression trees are inspected, never compiled.** `Expression.Compile()` emits IL at runtime
  and would break native AOT. Translation is a pure tree walk.
- **Danish domain terms keep their e-conomic names** — `vatZone`, `paymentTerms`, `bookedEntries`.
  Do not invent English improvements; they break the mapping back to the official docs.
- **Dependencies are close to zero, deliberately.** Adding one needs a reason why no BCL type will
  do.

## Code generation

The specifications in `specs/` are the source of truth. They are committed so that builds work
offline and a specification change is reviewable as a diff. Nothing in the build scrapes the
network, and nothing regenerates during `dotnet build` — regeneration is always a deliberate,
reviewed step.

The legacy API has no published OpenAPI document, only per-endpoint JSON Schema draft-03 files, so
`tools/EConomic.SpecConverter` converts them first. That converter reports anomalies rather than
hiding them and **exits non-zero on anything it cannot resolve** — including a curated type name
that no longer matches anything, or a path segment it does not recognise. If a specification
refresh makes it fail, the fix is to teach it the new case, not to loosen the check.

Run the steps **in this order**, from the repository root:

```bash
dotnet run --project tools/EConomic.SpecConverter -c Release                  # specs -> legacy-openapi + _all.json
(cd tools/nswag && dotnet nswag run legacy.nswag)                             # -> Rest/Generated/LegacyClients.g.cs
dotnet run --project tools/EConomic.SpecConverter -c Release -- enum-names    # enum members -> the values e-conomic sends
dotnet run --project tools/EConomic.SpecConverter -c Release -- json-context  # serialization metadata
dotnet run --project tools/EConomic.SpecConverter -c Release -- filters       # public filter/sort surfaces
dotnet run --project tools/EConomic.SpecConverter -c Release -- facade        # public models and client properties
```

`enum-names` edits the NSwag output in place, and must run before the JSON context is generated.
NSwag names an enum member `Net` for the value `"net"`, recording the original on an `[EnumMember]`
attribute that `System.Text.Json` ignores. Reading tolerated that, because enum deserialization is
case-insensitive; writing did not, and e-conomic rejects `"Net"` outright. Renaming the member is
the fix that keeps the source-generated converter — a `JsonStringEnumConverter` with a camel-case
policy is reflection-based and would break the trimming and AOT guarantees.

Skipping a step leaves generated code referring to names that no longer exist, and the failure
surfaces much later than the mistake. Two ordering details are easy to get wrong:

- NSwag generates from the **merged** `_all.json`, not the per-resource documents. Generating each
  separately would emit a copy of every shared type per document, and they would collide.
- The JSON context is derived from the **generated code**, not from the specifications, so it has
  to be regenerated after every NSwag run to match what was actually emitted.

Afterwards, confirm the public API baseline did not move. Generated churn should produce **zero**
diff in `PublicAPI.Unshipped.txt` unless you deliberately changed the facade. That is the whole
point of the split.

### Adding a resource

New resources are gated by `SchemaRegistry.PublishedEntities`, which the filter, sort and facade
generators all read. It grows one entity at a time as each facade lands, because every emitted
surface is public API — ungated, the 38 collection entities would add roughly 5 400 public symbols
for types nothing can yet be used against.

## Testing

| Project | What it covers |
| --- | --- |
| `tests/EConomic.Net.Tests` | Unit tests. No network. Handler pipeline, query translation, paging, error mapping and serialization, against recorded fixtures. |
| `tests/EConomic.SpecConverter.Tests` | The conversion and generation pipeline. |
| `tests/EConomic.Net.IntegrationTests` | A live agreement of your own. Opt-in, scheduled in CI. |
| `tests/EConomic.Net.AotSmokeTest` | Published with `PublishAot` and run in CI, so the trimming claim is tested rather than asserted. |

```bash
dotnet test tests/EConomic.Net.Tests

export ECONOMIC_APP_SECRET_TOKEN=…
export ECONOMIC_AGREEMENT_GRANT_TOKEN=…
ECONOMIC_RUN_INTEGRATION_TESTS=1 dotnet test tests/EConomic.Net.IntegrationTests
```

Integration tests are opt-in and run on a schedule rather than on every pull request, because they
break when e-conomic changes and that must not block unrelated work. Without the two tokens every
one of them skips.

**Point them at a throwaway agreement — they write to it.** They used to read from the public
`demo` agreement, and both reasons for moving off it are worth knowing:

- **It is shared.** Sampling `X-RateLimiting` there showed the budget moving between 58 and 352 of
  10 000 with no calls of our own in between. A burst from someone else produced a `429` that
  outlived any retry policy, at a moment with nothing to do with the code under test.
- **Its data is not ours.** Reading a shared agreement means asserting on records nobody controls;
  "five customers, numbered 1 to 5" is a fact about someone else's books, and it drifts.

Each test now creates what it asserts on through `AgreementSeed`, which deletes it again in reverse
order — an invoice has to go before the customer and product it references. Everything is prefixed
`ZZ Probe`, and a failed run can still leave a record behind.

Booking is the exception and has its own opt-in, `ECONOMIC_RUN_BOOKING_TESTS=1`: a booked invoice
cannot be deleted, and neither can the customer and product it references, so every run of that one
leaves three records behind permanently. The scheduled workflow deliberately does not set it.

Set the tokens through the environment rather than on the command line, or they end up in shell
history. For CI they are repository secrets of the same names.

Three tests are structural rather than per-resource. Each enumerates the client by reflection rather
than from a list, so a resource added later is covered without anyone remembering to add it, and
each carries a guard asserting it found something — reflection that matches nothing otherwise
reports a pass while testing nothing.

| Test | What it pins |
| --- | --- |
| `EveryResourceTests` (integration) | Fetches a page from every resource. An empty page passes: the claim is that the call round-trips and maps. |
| `FacadeMappingTests` (unit) | Fills a generated response in property by property and asserts every property the public record declares comes out populated. This is the only cover the resources with no live data have — sent and archived orders and quotes cannot be created through this API at all, since e-conomic publishes no endpoint that promotes a draft into them. |
| `WriteRequestTests` (unit) | Sends every write and asserts nothing appears in the body that the caller did not put there. |
| `FilterSurfaceTests` (integration) | Checks every filterable and sortable property against the server. A filter naming a field e-conomic does not accept comes back with `allowedFilteringFields`, its own list for that resource, so one bad filter per resource checks a whole surface. Sorting has no such list, so the entire sort surface goes into one request and a rejection is bisected to name the field. |

That last one earns its keep. A live test can only report that e-conomic accepted the request, not
what was in it, and unset value types kept leaking their defaults into requests: first numbers as
`0`, then dates as `0001-01-01`, then enums as their first member. Each was rejected by the server
in some configuration, and each was invisible to a green test suite.

**The specifications are wrong in both directions, and only one of them is safe.** They
under-report filterability — `pNumber` on customers is filterable and unannotated, which is what
`WhereRaw` is for. They also over-report it, which is not safe: a property that compiles and then
returns `400` defeats the entire purpose of the filter surface. Those are listed in
`FilterSurfaceGenerator.UnfilterableFields` and `UnsortableFields`, every entry read from a live
agreement. Follow the same discipline the curated type names do: an entry that stops matching
anything fails the generator rather than quietly describing a field that has since changed.

**Filterability belongs to an endpoint, not to a type.** Two endpoints can return the same shape
and accept different filters, so the flags are recorded per endpoint as `x-filterable-fields` on
the path rather than read back off the deduplicated component — which carries the union of every
endpoint that happens to share it.

**Assert on what a value type does when nobody sets it.** In C# it has a value regardless, and
`System.Text.Json` writes it. Nullability on the generated payload is what makes "unset" and
"explicitly zero" different things on the wire, and the corrector in the spec converter is what
applies it — including inside nested objects and array items, which is where two of the three
escaped.

**Assert on results, not just status codes.** More than one bug here survived a green unit test
suite because the test pinned an assumption the server did not share — filter syntax that parsed
locally and returned `400` live, and a date format that deserialized in tests but not against real
responses. A test that asserts a query returned the *right rows* would have caught both.

Every fixed bug gets a regression test. Every new endpoint gets at least a serialization
round-trip test. Fixtures must be anonymized: never commit real tokens or a real agreement's data.
The `demo` tokens are the only credentials allowed in this repository.

## Public API surface

The public surface is locked by `Microsoft.CodeAnalysis.PublicApiAnalyzers`. An unintended public
API change fails the build — this is the guardrail that makes the generated/facade split hold.

After an intentional API change, add the new symbols to `PublicAPI.Unshipped.txt`. The analyzer
names every undeclared symbol, so the list can be rebuilt from the build output rather than by
hand:

```bash
dotnet build EConomic.Net.slnx -c Release --nologo 2>&1 \
  | grep -o "Symbol '[^']*' is not part" \
  | sed "s/^Symbol '//; s/' is not part$//" \
  | LC_ALL=C sort -u >> src/EConomic.Net/PublicAPI.Unshipped.txt
```

Run the build once more afterwards. The pattern above stops at the first `'`, so it truncates any
symbol whose signature contains a quote — a `const char`, for instance — and those have to be added
by hand. The second build reports exactly which, and is worth doing regardless to confirm the
baseline is complete.

Entries move from `Unshipped` to `Shipped` at release time, not before.

## Style

Warnings are errors, and `nullable` is enabled everywhere. Line endings are LF, enforced by
`.gitattributes` and `.editorconfig`. CI runs:

```bash
dotnet format EConomic.Net.slnx --verify-no-changes
```

Write comments that explain *why*, especially where the code works around API behaviour that
contradicts the documentation. Several of the stranger-looking decisions in this repository are
load-bearing, and a comment is what stops the next person from "simplifying" them back into a bug.

## Submitting a change

- Include a test for any behaviour change.
- Update `CHANGELOG.md` under `## [Unreleased]`, following
  [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
- Update `PublicAPI.Unshipped.txt` in the same commit as an intentional API change.
- Keep generated files as generated — if the output is wrong, fix the generator.

## Releasing

Maintainers only. Versioning is [SemVer](https://semver.org/) via MinVer, driven by git tags, and
the package version is independent of the e-conomic API versions: a service moving from v3 to v4 is
a mapping change inside the facade, not automatically a major release here.

Pushing a `v*` tag publishes to NuGet, so it is never done casually:

```bash
git tag v0.1.0
git push origin v0.1.0
```

Publishing uses nuget.org trusted publishing over GitHub OIDC — there is no long-lived API key
stored anywhere. Before tagging, move the accumulated `PublicAPI.Unshipped.txt` entries into
`PublicAPI.Shipped.txt` and promote the `## [Unreleased]` changelog section.
