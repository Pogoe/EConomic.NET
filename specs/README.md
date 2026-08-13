# API specifications

Committed on purpose: code generation must be reproducible offline, and a spec change should show
up as a reviewable diff in a pull request rather than as a surprise in generated code.

```
legacy/           e-conomic's published JSON Schema draft-03 files, one per endpoint (160).
                  Their originals, byte-for-byte. Never edit.
legacy-openapi/   OpenAPI 3.0 documents generated from legacy/ (24). Never edit.
*.json            OpenAPI specs for the apis.e-conomic.com services.
```

## Legacy: draft-03, one file per endpoint

The legacy REST API publishes JSON Schema **draft-03**, not OpenAPI, with a separate file per
endpoint and verb — `customers.customerNumber.contacts.get.schema.json`. There is no `$ref`
anywhere; every file inlines every type.

`tools/EConomic.SpecConverter` converts them into OpenAPI 3.0 so both API surfaces reach the same
generator:

```bash
dotnet run --project tools/EConomic.SpecConverter -c Release
```

It prints what it had to work around and exits non-zero on anything it cannot resolve. Two of
e-conomic's files contain trailing commas and are not strictly valid JSON; they are parsed
leniently and reported rather than edited, so a later re-export diffs cleanly.

Regenerate after replacing anything in `legacy/`, and commit the result.

## OpenAPI services

e-conomic does not serve these as standalone JSON — `openapi.json` and `swagger.json` both 404.
The spec is embedded in each service's ReDoc page at
`https://apis.e-conomic.com/{service}api/redoc.html`, downloadable from the link in its header.

Known services: `customersapi`, `suppliersapi`, `productsapi`, `accountsapi`, `journalsapi`,
`documentsapi`, `bookedentriesapi`, `subscriptionsapi`, `projectsapi`, `internationalizationapi`.

## Updating

1. Replace the file with the newer spec.
2. Re-run the converter if anything under `legacy/` changed.
3. Review the diff — a changed required field, a removed property, or a property losing
   `filterable` is a compatibility question, not a formality.
4. Regenerate the client and check the public API surface is unchanged: the facade is what
   consumers see, and `PublicAPI.Unshipped.txt` should only move when that was the intent.
