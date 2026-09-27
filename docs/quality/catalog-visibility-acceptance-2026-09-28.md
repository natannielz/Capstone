# Shared catalog visibility — local acceptance

Date: 28 September 2026. Scope: ACC02.3 producer boundary on top of `c7f4027`. Local integration verification passed. This checkpoint does not complete product screens, governed entitlement issuance, pricing, delivery adapters or the production report requirements.

## Delivered behavior

- Approved buyers can share explicitly published assortments without sharing order ownership. A catalog entitlement must join the same current actor, membership and eligible catalog-read grant; unrelated grant alternatives cannot supply missing authority.
- Explicit shop/warehouse audiences also require containment of the assortment owner. No technical administrator, courier, product owner or role template receives implicit access.
- Persisted entitlement provenance includes requester, independent approving account, approval reference/version/time and effectivity. The issuer workflow and trusted-person independence remain separate gates.
- One canonical Mongo filter enforces complete assortment/owner pairs before count, page, search and internal/public lookup. Duplicate listings do not duplicate SKUs. Hidden and malformed products stay out of counts; ambiguous eligible public UUIDs fail closed.
- Responses contain only internal ID, public ID and display name. Image/export/event methods return metadata references, not file contents or reusable capabilities. Actual delivery must authorize again through the owning document policy.
- Snapshot reads fence current actor/session, grants, memberships, entitlements, assortments and hierarchy, then recheck effectivity. Catalog publication must atomically increment affected assortment versions; the owning CAT01 writer remains to be implemented.
- Migration v6 adds strict catalog entitlement/assortment schemas and indexes without seeding authority, rewriting previous migrations or changing existing SKUs. Preflight rejects incompatible collection/index/retention settings and preserves stronger validation.
- Organization reactivation impact reviews ignore operational read-fence counters while continuing to invalidate on semantic access revisions. Out-of-range BSON dates deny access with a controlled error.

## Verification

| Check | Evidence / outcome |
| --- | --- |
| Pure policy red/green | Initial shared-catalog failures reproduced before implementation; the fulfillment extension reproduced 3 expected failures, then all 21 policy cases passed |
| Catalog persistence | Final focused suite: 55 passed, 0 failed, 0 skipped, 2m18s; includes four out-of-range BSON date cases; console evidence in `tmp/production/catalog-visibility-results/catalog-store-focused-result.log` |
| Migration | All 21 v6 catalog migration cases and 20 previous provider lifecycle migration cases passed; existing definitions/history/SKUs preserved, strict types/options/indexes and resumability checked |
| Organization review red/green | Counter-only change failed as expected before the fix; semantic-version invalidation already passed; all 11 organization service cases passed after the fix; `catalog-organization-red-confirmed.trx` and `catalog-organization-green.trx` |
| Full backend | .NET 10.0.401, dotnet test backend/UnitToko.slnx --no-restore: **461 passed, 0 failed, 0 skipped**, **14m31s**; tmp/production/catalog-visibility-results/catalog-full.trx and catalog-full.log |
| Contracts | Compiled API OpenAPI export check and generated client drift check passed; no endpoint/client schema change |
| Independent review | Separate read-only review accepted domain/schema/query/fences/fulfillment and organization review changes; the BSON date-range finding was corrected and re-reviewed |

Tests use the isolated loopback replica set with disposable databases. Publication-removal races use the required atomic SKU/assortment-version transaction in a fixture; CAT01's production writer is not yet implemented. Metadata-reference tests prove the service policy, not nonexistent file/export/event routes. The focused console run has no TRX; full-run TRX provides durable combined evidence.

No frontend source changed in this checkpoint, so frontend/browser testing was not rerun. Prior frontend acceptance remains the baseline; hosted CI and connected real-provider acceptance have not run.

## Remaining integration and release gates

ACC02.4–.6 historical-only grant lifecycle, privileged-read audit and full read-surface integration remain open. CAT01 must supply product publication/versioning/indexes, governed entitlement issuance, actual product endpoints/screens and authorized pricing. PAY01/owning modules must supply real file/export/event delivery. Every consumer needs its own endpoint-specific isolation and revocation cases.

D02 catalog/pricing audience and approval-owner choices remain pending in [report decisions](report-decisions.md). No test fixture establishes a business rule or real entitlement. ACC03 person equivalence/finance conflicts, actual Auth0 coverage/recovery, complete commerce, operations/restore and accepted pilot/rollout remain required.

This is backend-only work with local disposable Mongo data. No frontend behavior or real provider/browser/public-gateway acceptance is claimed. The full production goal and all 97 report acceptance rows remain open.

See [publication contract](../architecture/catalog-visibility.md), [business access contracts](../architecture/business-access-contracts.md), [access plan](../superpowers/plans/2026-09-28-report-access.md) and [report completion plan](../superpowers/plans/2026-09-28-report-completion.md).
