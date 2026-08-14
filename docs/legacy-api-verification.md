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
| **payment terms are partly immutable** | `paymentTermsType` and `daysOfCredit` cannot change after creation: an update that alters either is rejected with `E06151`, even though the update payload accepts both. They have to be sent back unchanged, because `PUT` replaces |
| customer-group account | must be a non-barred Profit & Loss or Balance Sheet account |
| **DELETE status varies** | `204 No Content` for customers, units and products; **`200 OK`** with a status-message body for draft invoices |
| draft invoice requirements | `date`, `currency`, `layout`, `paymentTerms`, `customer`, `recipient`; an invoice line needs a `product` before `quantity` or `unitNetPrice` is accepted |
| collection listings omit lines | `/invoices/drafts` items carry no `lines` array — those appear only on the single-invoice GET and the create payload |
| **an unset number inside a line is rejected** | a draft invoice whose line omitted `lineNumber` and `sortKey` failed with "Integer 0 is less than minimum value of 1" for both. The same rule as `customerNumber`, one level deeper: optional numbers have to be nullable inside nested objects and array items too, not only at the top level |
| server prices the invoice | a line of `quantity: 2` at `unitNetPrice: 100` came back as `netAmount: 200` and `grossAmount: 250`. Nothing in the request said what the totals should be |
| draft invoice `PUT` | replaces the lines as well: sending one line at `quantity: 3` moved `netAmount` from 200 to 300 |
| a referenced record cannot be deleted | the customer and product a draft invoice points at are only deletable once the invoice is gone |
| **booking a draft** | `POST /invoices/booked` with `{"draftInvoice": {"draftInvoiceNumber": n}}` answers with the booked invoice and its own number. The draft is gone: a query for it comes back empty |
| booking is irreversible | a booked invoice cannot be deleted, and neither can the customer or product it references |
| `sendBy` casing | the three values are `none`, `EAN` and `Email` — inconsistently cased, and the server is strict about it |
| draft orders and quotes | create and delete behave exactly as draft invoices do, from the same generated templates |
| **a voucher create answers with an array** | `POST /journals/{n}/vouchers` returns `201` with a JSON **array**, not the single voucher the schema describes — e-conomic may split the entries it was sent across several vouchers. Every other create in the API answers with one object |
| voucher entries | a finance voucher entry needs `date`, `amount`, `account` and `contraAccount`; both accounts must accept direct entries |
| voucher key | a voucher is addressed as `{accountingYear}-{voucherNumber}`, e.g. `/journals/1/vouchers/2026-2` |
| **a journal entry can be deleted** | `DELETE /journals/{n}/entries/{k}` answers `204`. It appears in no schema and in no documentation page — every entry carries it as a `metaData.delete` link, which is the server describing its own records |
| **bulk delete of drafts** | `DELETE /invoices/drafts` — no identifier, no filter — removes every draft invoice on the agreement. Verified: two drafts, one call, collection empty |
| `journalEntryNumber` under-reported | the server sends it on a voucher's entries; the schema for those does not declare it, so it has to be read back from `/journals/{n}/entries` |
| **`dueDate` is usually derived, and ignored** | with payment terms of type `net`, a draft invoice sent `dueDate: 0001-01-01` and one that omitted it both came back as `2026-08-22`. The server computes the date from the terms and discards what it was given, which is why an unset date leaked for so long without failing anything |
| **except with `dueDate` terms, where it is the caller's** | with payment terms of type `dueDate`, omitting it is rejected as `E04042` "dueDate is missing a value"; sending `0001-01-01` is rejected as `E04760` "may not be set to an earlier date than property `date`". The second error is what an unset `DateOnly` produced — a complaint about a value the caller never chose, instead of the one they could act on |
| **an unset enum is rejected the same way** | a C# enum defaults to its first member, so an unset `paymentTermsType` was sent as `"net"`. Against payment terms of any other type e-conomic answers `E07180`, "Payment terms type does not match the type on the payment terms specified", with the developer hint "or omit the property" — which is exactly what a caller who never set it meant |
| **an omitted boolean is cleared, not kept** | a customer created with `barred: true` and then replaced by a `PUT` that omits `barred` comes back unbarred. Absent and `false` mean the same thing, so the booleans on the write payloads are left non-nullable while numbers, dates and enums are not |
| **the schemas over-report filterability too** | not only under-report. e-conomic's own schema marks a customer group's `account.accountNumber` filterable; the server's `allowedFilteringFields` for that resource is `name, customerGroupNumber`. Nineteen properties across six resources were like this — employees' `email` and `phone`, products' `inventory.*`, and `references.customerContact.customer.customerNumber` on every invoice view. Under-reporting is safe; over-reporting turns a compile-time guarantee into a runtime `400` |
| **sortability over-reports separately** | 25 fields, an overlapping but different set — products' `barred` is filterable and not sortable, and orders and quotes claim `paymentTerms.paymentTermsNumber` while the server refuses it. An unsortable field is answered "Could not parse query string sort parameter" with no list of alternatives, so each has to be tried on its own |
| **a bad filter field names the alternatives** | `?filter=zzzNoSuchField$eq:1` returns `400` carrying `allowedFilteringFields` for that resource, on every resource tried. One deliberately bad filter per resource is enough to check a whole generated surface against the server |
| **the public demo agreement is shared** | its token bucket is spent by everyone reading e-conomic's documentation: `X-RateLimiting` moved between 58 and 352 of 10 000 with no calls of our own in between. A `429` from it says nothing about the caller. The integration tests were moved onto an agreement of their own for this reason, and because reading a shared agreement means asserting on records nobody controls |

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
| `/accounting-years` | ✅ live | ✅ live | — | — | create takes two dates and no key; the response carries `"year": "2027"`. e-conomic publishes no update or delete |
| `/accounts` | ✅ live | — | — | — | string enums confirmed |
| `/app-roles` | ✅ live | — | — | — | |
| `/currencies` | ✅ live | — | — | — | no filterable properties |
| `/customer-groups` | ✅ live | ✅ live | ✅ live | ✅ live | |
| `/customers` | ✅ live | ✅ live | ✅ live | ✅ live | filter under-reports `pNumber` |
| `/customers/{n}/contacts` | ✅ live | ✅ live | ✅ live | ✅ live | reached through the parent |
| `/customers/{n}/delivery-locations` | ✅ live | ✅ live | ⬜ | ✅ live | reached through the parent |
| `/departments` | ✅ live | — | — | — | |
| `/departmental-distributions` | ✅ live | — | — | — | |
| `/employees` | ✅ live | — | — | — | |
| `/invoices/booked` | ✅ live | ✅ live | ⬜ | — | the `POST` books a draft, exposed as `DraftInvoices.BookAsync`. No delete: a booked invoice is part of the accounting record |
| `/invoices/drafts` | ✅ live | ✅ live | ✅ live | ✅ live | delete answers **200**, not 204. The collection also accepts a delete of its own, which **removes every draft**, exposed as `DeleteEveryDraftAsync` behind a required confirmation |
| `/journals` | ✅ live | — | — | — | read-only, but exposed as a resource so its vouchers can hang off it |
| `/journals/{n}/vouchers` | ✅ live | ✅ live | — | — | reached through the journal, which is itself read-only. The create answers with an **array** |
| `/journals/{n}/entries` | ✅ live | — | — | ✅ live | the delete is hypermedia-derived: it is in no schema and no documentation page |
| `/layouts` | ✅ live | — | — | — | |
| `/orders/drafts` | ✅ live | ✅ live | ✅ live | ✅ live | |
| `/orders/sent` | ✅ live | — | — | ⬜ | |
| `/payment-terms` | ✅ live | ✅ live | ✅ live | ✅ live | `paymentTermsType` is required on create |
| `/payment-types` | ✅ live | — | — | — | |
| `/product-groups` | ✅ live | — | — | — | |
| `/products` | ✅ live | ✅ live | ✅ live | ✅ live | |
| `/quotes/drafts` | ✅ live | ✅ live | ✅ live | ✅ live | |
| `/quotes/sent` | ✅ live | — | — | ⬜ | |
| `/suppliers` | ✅ live | ✅ live | ✅ live | ✅ live | |
| `/units` | ✅ live | ✅ live | ✅ live | ✅ live | create payload declares no key; the response carries one |
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
