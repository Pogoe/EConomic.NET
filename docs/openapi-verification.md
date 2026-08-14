# Verifying the OpenAPI services

What `https://apis.e-conomic.com` actually does, as distinct from what its specifications say. The
companion to [legacy-api-verification.md](legacy-api-verification.md), which covers
`restapi.e-conomic.com`.

## Why this exists

The legacy surface taught the lesson twice over: the specification is a description, not the
contract. Every significant bug there was invisible to a green unit test suite and obvious on the
first live request. These services publish far better metadata — real OpenAPI 3.0, per-property
operator lists, `readOnly` as a keyword rather than prose — so the temptation to generate first and
ask later is stronger. These probes were run before generating anything.

Confirmed against a private test agreement on 2026-08-14, against `customersapi/v3.1.0`. Re-check
on a second service before treating any of it as universal: these services are independently
versioned and independently built.

## Verified against a live agreement

| Behaviour | Result |
| --- | --- |
| the version in the specification's `servers` URL | **serves** — `/customersapi/v3.1.0/Customers` answers |
| cursor listing | `GET /Customers` returns `{ "items": [...] }` with **`cursor` absent entirely** once there is nothing more to fetch, not an empty one. Absence is the end-of-enumeration signal |
| **`sort` under cursor pagination** | **silently ignored** — `?sort=-customerNumber` answers `200` with ascending data. No error, no warning. An `OrderBy` must move the request to `/paged` or it is a lie |
| `/{resource}/paged` | returns a **bare array**, not an envelope — exactly as declared |
| `/{resource}/count` | returns a **bare integer** — exactly as declared |
| errors | `problem+json` with `errors[]`, each carrying `message`, `errorCode` and `property`, plus a top-level `errorCode`, `traceId` and `traceTimeUtc` |
| **no `allowedFilteringFields`** | unlike the legacy API, a rejected filter does not name the alternatives. The one-request check of a whole filter surface does not work here |
| unfilterable property vs unknown property | **different messages**: `Filtering is not allowed on property 'zzzNoSuchField'` versus `Operator $eq: not supported on property name`. Both `errorCode: BadFilterRequest`. The distinction means the published operator lists can be checked per operator, not merely per field |
| **`customerNumber` is caller-supplied** | the server does not assign one. Omitting it fails with "The field CustomerNumber must be between 1 and 999999999" — the opposite of the legacy API, where the server numbers customers |
| create response | `201` with **only the identifier**, `{"customerNumber":5000}` — again the opposite of legacy, whose creates return the whole resource |
| unknown properties on write | **silently ignored**, as on the legacy API. Sending the legacy spelling `paymentTermsNumber` instead of this API's `paymentTermId` was dropped, and the request then failed as `CustomerInvalidPaymentTerm` — an error about a property that was never the problem |
| **`objectVersion` is optimistic concurrency** | every resource carries one. A `PUT` without it is rejected **`409 UpdateConflict`**, "The resource has been updated by another user"; the same `PUT` carrying the value from a fresh `GET` answers `200`. An update is therefore always read-modify-write |
| `/Setup` | returns agreement-level defaults — `defaultLayoutNumber` — and carries an `objectVersion` of its own |

## What this changes in the design

**`objectVersion` has to be on the public model and required by an update.** It is not an
implementation detail that the facade can hide: without it every update fails, and with a stale one
it fails for the right reason. A `409` here means "someone else changed this", which is worth its
own exception rather than a generic `EconomicApiException` — the caller's correct response is to
re-read and retry, and nothing else in either API surface behaves this way.

**Identifier assignment differs per surface, for the same entity.** A customer created through
`restapi.e-conomic.com` is numbered by the server; one created through `customersapi` is numbered by
the caller. The two facades cannot share a create model, and neither should pretend the other's
convention applies.

**The same entity is spelled differently on each surface.** `paymentTermsNumber` here is
`paymentTermId`. Since unknown properties are ignored rather than rejected, a name taken from the
wrong surface fails as a validation error somewhere else entirely. Nothing may be copied between
the two facades on the assumption that a field is "the same field".

**Sorting cannot be offered on a cursor query.** Verified twice now, on two different occasions.
`OrderBy` switches the request to `/paged`, which is capped at 10 000 items — a real limit that the
method has to document rather than let callers discover.

**Filterability is over-reported here too, and the failure is a `500`.** These services publish an
operator list per property, which is better information than the legacy schemas' boolean — and
still not the server. `Account.assetGroupNumber` answers `500` to every filter operator and to a
sort in both directions. `Account.isDepartmentMandatory` answers `500` to `$eq:` and `$ne:` while
`$eq:$null:` returns normally and the neighbouring `isUnitMandatory` works throughout. Both
reproduce on the demo agreement. There is no `allowedFilteringFields` to diff against on this
surface, so the only way to find them is to send every clause the surface offers.

**A conjunction hides a broken clause.** This is the finding that matters most for how the checks
are written. Batching clauses with `$and:` produces false passes: a twenty-clause filter containing
`assetGroupNumber$eq:1` returned `200` while the same clause alone returned `500`, because
e-conomic short-circuits once an earlier clause has excluded every row. Any check that batches
filter clauses is therefore unsound, and `FilterSurfaceTests` sends one clause per request. Sorting
does not have this property — ordering has to touch every field named — which is why the sort check
may still batch.

**The suppliers service publishes no suppliers.** `suppliersapi/v2.0.0` covers contacts and groups
only; suppliers themselves exist on the legacy API alone. Not a gap in the generator, and worth
stating because the service's name implies otherwise.

**Not every collection is the same shape.** `/KeyFigureCodes` is read-only. `/TotalIntervals` has a
delete addressed by two parameters — account number and starting account — which is not a shape the
facade expresses, so it offers none. `/products` has no `/count` at all, is keyed by a string, and
reaches its paged listing through `/productspaged/paged`. Reading sales prices in currency is a
collection of its own while writing them is scoped to a product and keyed by currency, so that
resource is read-only here.

**A collection scoped by a parent answers an empty listing for a parent that does not exist**, not
a `404`. Verified on price groups, of which the test agreement has none:
`/pricegroups/1/specialprices` returns `200` and no items. That is what makes it possible to probe
a scoped collection's filter surface on an agreement with no data in it — the question is whether
the server parses the filter, not what the filter matches.

## Still to verify

- [ ] Whether `objectVersion` behaves the same on the other services, and whether `POST` accepts one
- [ ] Whether `PUT` at a collection path — `PUT /Contacts`, which several services offer instead of
      `PUT /Contacts/{id}` — takes the identifier from the body
- [ ] Whether a cursor survives an intervening write, and what happens when it does not
- [ ] Whether `/count` respects `filter` in practice, as its parameter list claims
- [ ] Whether `429` carries the same `X-RateLimiting` budget as the legacy surface
- [ ] Whether the `like` operator behaves as "contains" without wildcards here too
- [ ] Whether the `500`s on `assetGroupNumber` and `isDepartmentMandatory` are fixed, which would
      make the curated exclusions fail the generator and prompt their removal
- [ ] Whether `$or:` short-circuits the way `$and:` does, which would matter if the operator check
      is ever batched again
