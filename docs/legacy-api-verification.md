# Verifying the legacy REST API

The schemas in `specs/legacy/` describe e-conomic's REST API, and they are wrong often enough that
they cannot be trusted on their own. This document tracks what has been **verified against a live
agreement** and what is still only an assumption.

Every entry marked *unverified* is a claim the library currently relies on. Some are certainly
right. The point is that we do not yet know which.

## Why this exists

Each of these was found by contradicting the schema, not by reading it:

| Claim in the spec | What the server does |
| --- | --- |
| `lastUpdated` is `format: full-date` | returns `2022-06-24T08:25:00Z` |
| enums are numbers | sends `"heading"`, `"debit"` |
| `POST` responses are `200` | returns `201 Created` |
| 20 customer properties are filterable | accepts 21 — `pNumber` is missing from the schema |
| `Supplier.self` has no format | is a URI, like every other `self` |
| `productNumber` path parameter is an integer | the value is a JSON string, `"1"` |
| `$null:` is an operator | it is a value: `ean$eq:$null:` |

## The probe

`tools/EConomic.Probe` sends one request and prints exactly what comes back — no mapping, no
deserialization, no interpretation.

```bash
dotnet run --project tools/EConomic.Probe -- GET "/products?pagesize=3"
dotnet run --project tools/EConomic.Probe -- POST /customers --data @customer.json
dotnet run --project tools/EConomic.Probe -- GET /customers --save customers-page
```

Credentials come from `ECONOMIC_APP_SECRET_TOKEN` and `ECONOMIC_AGREEMENT_GRANT_TOKEN`, defaulting
to the public read-only demo agreement. `--save` writes a fixture **only** when running against the
public demo — a real agreement's data must be anonymized by hand before it goes anywhere near git.

Set the tokens through the environment, never on the command line, or they end up in shell history.

## Verified against a live agreement

Confirmed on agreement 2452861 (a trial agreement) on 2026-08-14, against `/customers`. Each of
these should be re-checked on at least one other resource before being treated as universal.

| Behaviour | Result |
| --- | --- |
| `POST` status | **`201 Created`** |
| `POST` response headers | carries **`Location: https://restapi.e-conomic.com/customers/{n}`** — undocumented |
| `POST` response body | includes top-level **`self`**, which the write schemas omit entirely |
| **`self` in place of a number in a reference** | **accepted and resolved** — undocumented |
| `readOnly` properties sent on write | **ignored** — `balance: 999.99` came back as `0` |
| unknown properties sent on write | **ignored**, not rejected |
| `PUT` status | **`200 OK`** |
| `PUT` semantics | **true replace** — omitting `city` and `email` cleared both |
| `DELETE` status | **`204 No Content`** |
| repeated `DELETE`, no key | **`404`**, `errorCode: E06000` — confirms non-idempotent |
| repeated `DELETE`, same `Idempotency-Key` | **`204` with `X-ResultFromCache: true`** — replayed |
| identifier reuse | **numbers are reused** — a customer created after deleting number 3 was itself given number 3 |
| `productNumber` on the wire | a JSON **string**, `"1"`; `ZZ-TEST-1` works for create, update and delete |
| **`PUT` is an upsert** | a `PUT` to an identifier that does not exist answers **`201 Created`** and creates it |
| **explicit `null` is rejected** | `"address": null` fails validation with "Expected String but got Null" — omit instead |
| **`0` is rejected for identifiers** | `customerNumber: 0` fails with "Integer 0 is less than minimum value of 1" |
| create responses | **every** resource returns the whole entity with `self` — customers, units, payment terms, suppliers, products, customer groups, accounting years |
| identifier assignment | server-assigned for customers, units, payment terms and suppliers; **caller-supplied and required** for customer groups and products |
| `accounting-years` key | `"year": "2027"` — a string, not a number |
| **enum casing is strict** | `"paymentTermsType": "Net"` is rejected; the value must be `"net"` exactly |
| `paymentTermsType` | **required** on create, so a payment term cannot be created without it |
| customer-group account | must be a non-barred Profit & Loss or Balance Sheet account |
| **DELETE status varies** | `204 No Content` for customers, units and products; **`200 OK`** with a status-message body for draft invoices |
| draft invoice requirements | `date`, `currency`, `layout`, `paymentTerms`, `customer`, `recipient`; an invoice line needs a `product` before `quantity` or `unitNetPrice` is accepted |
| collection listings omit lines | `/invoices/drafts` items carry no `lines` array — those appear only on the single-invoice GET and the create payload |

Two of these change the design:

**`self` is interchangeable with the identifier.** The write models flatten every reference to a
number and discard `self`, which is now known to be narrower than the API. It is also the obvious
route for resources whose key is not an integer.

**Identifier reuse makes a retried `DELETE` dangerous.** It is not merely that a repeat returns
`404`; the number may by then belong to a different record. Requiring an `Idempotency-Key` before
retrying a `DELETE` is not caution, it is necessary — and the server does replay the original
result when one is supplied.

## Assumptions still to verify

### Identity and references

- [ ] Whether a reference carrying *both* a number and a `self` that disagree is rejected or
      silently resolved, and which wins.
- [ ] Whether `self` is accepted on `PUT` bodies and in path position, not just in nested
      references on `POST`.
- [ ] Whether the server accepts an alphanumeric `productNumber`, given the path parameter declares
      an integer while the value is a string.

### Status codes

- [x] `POST` returns `201 Created`, with a `Location` header.
- [x] `PUT` returns `200 OK` and the updated resource.
- [x] `DELETE` returns `204 No Content`.
- [ ] Whether these hold for every resource, or only for customers.

### Write payloads

- [x] `readOnly` properties are ignored, so sending `balance: 0` is harmless.
- [x] `PUT` is a true replace: an omitted property is cleared.
- [x] Unknown properties are ignored.
- [ ] What a create response actually contains for `UnitsCreate`, `PaymentTermPOST` and
      `AccountingYearsPOST`, whose schemas carry no identifier — which is the only reason those
      resources have no `CreateAsync`. Customers proved the response carries far more than the
      schema describes, so these very likely do too.
- [ ] Whether `customerNumber` may be supplied on create, or is always server-assigned.

### Idempotency and retries

- [x] `DELETE` honours `Idempotency-Key` and replays with `X-ResultFromCache: true`.
- [ ] The same for `POST` — whether a replayed create returns the original resource rather than
      making a second one.
- [ ] How long a key is remembered.

### Querying

- [ ] Which properties are filterable per resource, against the schema's list. Customers is known
      to under-report by one (`pNumber`).
- [ ] Whether `$like:` without wildcards really means *contains*.
- [ ] `$in:`/`$nin:` are numeric-only with a 200-value maximum.
- [ ] Sorting on a property the schema marks unsortable — rejected, or silently ignored.
- [ ] The escape table, by sending values containing `$ ( ) * [ ] ,`.

### Pagination

- [ ] `pagesize` maximum is 1000, and what happens above it.
- [ ] Whether `skippages` past the end returns an empty collection or an error.

## Endpoint checklist

One row per endpoint. Fill in as each is exercised.

| Resource | GET | POST | PUT | DELETE | Notes |
| --- | --- | --- | --- | --- | --- |
| `/accounting-years` | ✅ live | ⬜ | — | — | create payload has no key |
| `/accounts` | ✅ live | — | — | — | string enums confirmed |
| `/app-roles` | ✅ live | — | — | — | |
| `/currencies` | ✅ live | — | — | — | no filterable properties |
| `/customer-groups` | ✅ live | ⬜ | ⬜ | ⬜ | |
| `/customers` | ✅ live | ⬜ | ⬜ | ⬜ | filter under-reports `pNumber` |
| `/customers/{n}/contacts` | ⬜ | ⬜ | ⬜ | ⬜ | nested, not in the facade |
| `/customers/{n}/delivery-locations` | ⬜ | ⬜ | ⬜ | ⬜ | nested, not in the facade |
| `/departments` | ✅ live | — | — | — | |
| `/departmental-distributions` | ✅ live | — | — | — | |
| `/employees` | ✅ live | — | — | — | |
| `/invoices/booked` | ⬜ | ⬜ | ⬜ | — | not in the facade |
| `/invoices/drafts` | ⬜ | ⬜ | ⬜ | ⬜ | **bulk delete removes every draft** |
| `/journals` | ✅ live | — | — | — | |
| `/journals/{n}/vouchers` | ⬜ | ⬜ | — | — | composite key |
| `/layouts` | ✅ live | — | — | — | |
| `/orders/drafts` | ⬜ | ⬜ | ⬜ | ⬜ | delete documented non-idempotent |
| `/orders/sent` | ⬜ | — | — | ⬜ | delete documented non-idempotent |
| `/payment-terms` | ✅ live | ⬜ | ⬜ | ⬜ | create payload has no key |
| `/payment-types` | ✅ live | — | — | — | |
| `/product-groups` | ✅ live | — | — | — | |
| `/products` | ✅ live | ⬜ | ⬜ | ⬜ | `productGroup` missing from the model |
| `/quotes/drafts` | ⬜ | ⬜ | ⬜ | ⬜ | delete documented non-idempotent |
| `/quotes/sent` | ⬜ | — | — | ⬜ | |
| `/suppliers` | ✅ live | ⬜ | ⬜ | ⬜ | |
| `/units` | ✅ live | ⬜ | ⬜ | ⬜ | create payload has no key |
| `/vat-accounts` | ✅ live | — | — | — | |
| `/vat-types` | ✅ live | — | — | — | |
| `/vat-zones` | ✅ live | — | — | — | |

Legend: ✅ verified against a live agreement · ⬜ unverified · — no such endpoint in the specs.

## Recording what is learned

A finding is only useful once it is pinned. For each one:

1. Save the response as a fixture, anonymized if it came from a real agreement.
2. Add a unit test asserting the behaviour, so a regression fails the build.
3. If the schema was wrong, correct it in `tools/EConomic.SpecConverter` — never by editing
   `specs/legacy/`, which stays byte-identical to what e-conomic published — and say why in a
   comment.

Assert on results rather than status codes. More than one bug here survived a green test suite
because the test pinned an assumption the server did not share.
