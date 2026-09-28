# Catalog authoring acceptance — 28 September 2026

Status: locally accepted for CAT01.2. This is not production release approval.

This is the CAT01.2 staff workflow in the [catalog plan](../superpowers/plans/2026-09-28-report-catalog-packaging.md). The broader catalog, inventory, purchasing and production requirements remain open.

## Accepted scope

Staff select a currently permitted shop/channel or warehouse/channel, create a category and product family, enter explicit package contents, and reload the persisted variants. The 12-unit and 24-unit examples are test inputs; no unit count or package choice is inferred. Cosmetic corrections preserve immutable earlier definitions. Material changes require a different SKU, public UUID and permanently reserved primary code; replacing a SKU does not retire its predecessor automatically.

Publication and retirement are explicit transactions. The server loads the immutable authoring owner and every affected assortment owner, checks current management authority, and changes the SKU and assortment revisions together. Category codes and authoring ownership remain immutable. A family can move to another active category under the exact same owner; its recorded history retains the original category. No family-wide deactivation route is exposed.

Buyer definition reads reuse the existing entitlement transaction and return only the published descriptive projection. They supply no price, availability, document/image capability or stock promise. Image inputs remain unavailable until PAY01. Staff authoring creates no entitlements, assortments, role assignments or business seed data.

## Persistence and recovery

Migration v11 adds strict catalog masters, immutable definitions and permanent identifier history. It preserves the foundation organization/SKU uniqueness index, including its original partial predicate, which covers every complete v11 SKU. Incomplete legacy rows, malformed relationships, conflicting indexes and unsafe counters stop the migration before catalog DDL or sequence repair. Historical snapshots are checked against immutable identities without comparing them to later mutable names or revisions. Definition and linked assignment dates preserve the domain's nondefault-time requirement. Migration preflight and transactional writers establish the latest-definition pointer; reads validate the exact referenced definition and fence the current SKU head. They do not rescan all historical definitions on every read.

The administration API requires the verified session, exact expected actor, same-origin/anti-forgery checks, explicit revisions and bounded idempotency keys. Int64 identifiers and versions remain JSON strings. Receipts are bound to the original actor, operation and body and require current access. A missing receipt remains unconfirmed; this catalog slice exposes no permanent no-effect closure. The browser retains only actor/operation/key across reload and keeps an unchanged retry body only in the originating tab.

Legacy malformed-metadata/duplicate-public-ID regression fixtures remain explicitly pinned to schema v6. Current positive reader fixtures use complete records from the real authoring writer. Production validators and uniqueness constraints are not relaxed to accommodate old test data.

## Verification record

- Initial behavior failures: two real-cookie HTTP cases reached the absent catalog routes; 38 service cases reached the unimplemented service; initial migration cases exposed the absent schema and missing legacy preflight.
- Root API build passed with zero warnings/errors; compiled export adds 18 routes and 33 schemas, with all previous paths/schemas semantically unchanged. The real buyer HTTP case reproduced an empty response for hidden products; returning the nullable public DTO directly fixed it, and the case now passes. The generated detail response explicitly permits JSON null while the shared public DTO and page items remain non-null.
- Shared frontend recovery: 16 new cases plus 46 existing cases passed.
- Browser: 11 scenarios passed on the actual Next.js UI with synthetic identity/catalog responses. These are not live Auth0 or Mongo browser evidence. Desktop/mobile screenshots were inspected.
- Migration: 66 unique cases passed across the complete 64-case run (6.1189 minutes) and two final date-parity cases (17.4348 seconds). The earlier index/provenance failures and corrections are retained in `catalog-storage-verification.md`; no single 66-case run is claimed. The additional same-owner category-reclassification fixture passed in the combined run, bringing selected migration coverage to 67 cases.
- Frontend: 453 main, six future-date and seven legacy golden cases passed, including 33 dedicated catalog cases and the 16 shared recovery cases. Type checking, production build, focused lint and module boundaries passed. Full lint reported zero errors and nine pre-existing warnings.
- Combined backend workflow: 113/113 passed, zero failures/skips, 2.8691 minutes; clean compilation. This includes 49 service cases, seven real-cookie HTTP cases, all 55 existing catalog reader cases and two targeted migration cases. Evidence: `catalog-authoring-focused.trx` and `.log`.
- Earlier upgrade/foundation/contract run: 203 total, 202 passed and one historical-fixture mismatch. Its v6-to-v7 test had applied the latest migration chain while expecting all old catalog validators unchanged. The test now explicitly targets v7, retains every equality/history/high-water assertion, and passed in the combined run. Evidence: `catalog-prior-migration-compatibility.trx`.
- Review regressions: 15 selected cases produced four existing guard passes and 11 expected failures before corrections. These cover strict audit versions, final-session expiry, filtering before counts, field-order-independent owners, bounded audit correlation and category reclassification. The two foreign-category cases already rejected the request but returned the wrong controlled exception type; they were not authorization bypasses. Evidence: `catalog-review-red.trx`.
- Six tightened controlled-exception cases passed (10.6976 seconds), checking exact safe validation/conflict types and codes; they are already included in the 113-case total.
- A final contract review found four catalog ID arrays without item schemas. The new contract test reproduced that omission. The catalog-only schema helper now reuses the existing canonical LongId string schema. All four item contracts, positive Int64 bounds, overflow/newline rejection and nullable-detail/non-null-page rules pass. One intermediate test assumed an inline schema instead of a valid shared reference; resolving that exact reference fixed the test without a further production change. The three final HTTP/schema drift checks passed alongside it; no single clean four-case run is claimed.
- Compiled export drift check, generated client check and final frontend type check passed. These checks followed the last schema correction. `catalog-id-array-red.trx`, `catalog-final-generated-contract.trx` and `catalog-id-array-green.trx` preserve the complete result history.
- Across the affected backend runs, 381 distinct cases passed after the documented corrections. This is selected regression coverage, not a new run of the entire backend suite.
- Independent API and storage/runtime reviews accepted the final corrections and legacy-fixture boundaries. The nullable response passed real HTTP verification; the final generated-contract source review also passed. Independent comparison confirms all 58 prior paths, 59 operations and 84 schemas remain semantically unchanged; the catalog adds 18 paths and 33 schemas.

## Remaining production work

Full CAT01 still requires governed identifier correction/mapping, pricing, eligible images, buyer entry-point integration and downstream snapshots. Inventory/ordering, OMI/S-Mart source authority, fulfillment/documents, payment evidence/OCR/bank allocation, accounting, operations and pilot gates remain in the umbrella plan. D01/D02 remain pending; no live source, reviewer appointment, provider configuration or commercial policy has been invented. All 97 report production acceptance rows remain open and the full goal stays active.

No live credentials, external messages, paid provisioning, real transfer, production import or cutover is covered by these local checks. Unrelated user changes are preserved.
