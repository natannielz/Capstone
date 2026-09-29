# Catalog price and approval contract verification — 29 September 2026

Status: locally verified prerequisite; complete catalog pricing remains open.

Plan: docs/superpowers/plans/2026-09-29-catalog-pricing.md, Task 1. This checkpoint fills part of CAT01 from the report packaging plan. It preserves the original production scope and all 97 report requirements.

## Implemented

- Immutable price facts belong to one SKU and represent the price of one counted package. Approval binds the exact reviewed SKU revision, current package-definition ID/version and previous price revision.
- Missing price input cannot become an implicit free price. Explicit zero and the existing bounded Money contract preserve exact cents; existing price facts and package definitions remain unchanged.
- New versions require a new positive identity, an active SKU, canonical currency data and UTC millisecond approval time no earlier than package/history facts. Stale expectations, cross-SKU history, no-op changes and exhausted revisions fail.
- Explicit pricing.approve applies only to fulfillment shop/warehouse scopes, requires MFA within five minutes and uses both account-level and verified-person ProposedBy independence. No role template receives it automatically. pricing.manage remains separate.
- Eleven real-Mongo tests exercise the new permission through existing command, person-proof, receipt and rollback machinery using an isolated synthetic probe. They do not install a production price writer.

Currency shape validation is not approval of a supported currency, exchange conversion, tax, HET, margin or customer-tier policy.

## Evidence

| Check | Result |
| --- | --- |
| Initial contract test build | Expected missing CatalogPriceExpectation, CatalogPriceInput and PriceVersion contracts |
| First executable price-policy slice | 44 passed, zero failures/skips |
| Expanded price and transactional-authority slice | 70 passed: 59 pure contract/person cases and 11 real-Mongo cases |
| Final affected regression | 231 passed, zero failures/skips, including all 70 new cases and 161 existing cases |
| Server OpenAPI export check | Passed; generated contract unchanged |
| Unrelated tracked working files | Raw SHA-256 matches previous preservation manifest |
| Independent review | Pending capacity; author review is not independent acceptance |

Two incidental compile errors (the required Money validation argument and the fixture enum name) were corrected before executable tests. No test expectation was weakened. The initial missing-contract evidence is retained.

Final regression includes existing catalog package transitions, numeric contracts, scoped authorization, business grant administration, stock-source permission rules, person independence, transactional approval/replay and final-session expiry tests.

Artifacts are local under tmp/production/catalog-price-results:

- policy-red.log: SHA-256 997cc23d6da45b749af2fc19bc3868e22c9cb13fe3f10d07c7c3e05d654c027d
- price-authority.trx: SHA-256 f7e59007192bdf331266f826603e01e790562934121d60e13c91c27516a02dfa
- affected.trx: SHA-256 2c183193693b7a2009a3675a7b9dfa9a1cf33659d36696e01d4ae6d267ab1709
- test-evidence.json records the individual runs; repeated runs are not counted as distinct tests.

## Remaining implementation and publication

Task 2 must implement actual scoped proposal/decision persistence, immutable price history/current heads, graph/provenance validation, concurrency fencing and rollback. Task 3 must install the forward migration and complete action-specific API/recovery. Task 4 must deliver staff screens, entitled buyer quotes and real browser workflows. The numeric domain record is not an API DTO; future API revisions/IDs must use the existing numeric-string wire contract.

No production price schema, service route, staff editor, buyer price entitlement, checkout switch, stock activation or financial posting is delivered by this checkpoint. No frontend application changed, so no fresh frontend build or browser result is attributed to these edits.

The checkpoint will be pushed to the authorized codex/unit-toko-production branch with exact publication evidence kept locally. Prior CI run 36547963275 for 1a78f63b29caaef4f61a6485eeae4c61e57b0f3e has passed frontend and real-browser jobs, with full backend validation still running at this review. It does not validate this new checkpoint.

Vercel owning-account access remains unavailable. The previously prepared application archive remains available, and the application source trees are unchanged. Preparation is not deployment. The complete goal remains active.
