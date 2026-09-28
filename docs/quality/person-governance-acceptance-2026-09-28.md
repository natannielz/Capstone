# Governed person suspension service — 28 September 2026

Status: locally accepted service/storage checkpoint after focused compatibility and uncertain-commit verification. This implements PG02–PG04 of [the governed staff identity plan](../superpowers/plans/2026-09-28-person-governance.md). It does not complete ACC03 or production acceptance. Baseline before this change: 3715c47.

## Implemented boundary

Three private v9 collections retain versioned governance-policy references, immutable review requests and append-only transition history. Exact BSON types, chronology, bounded snapshots, unique proposal/transition records and complete cross-references are checked before migration DDL. Prior migration definitions and v8 approval provenance remain unchanged. The migration seeds no policy or reviewer authority.

A separate structural loader discovers the complete group, people, aliases, evidence, source and overlap closure. It accepts structurally complete expired/disputed targets without declaring them authoritative. New suspension targets must be active or disputed. It rejects incomplete, oversized, malformed or conflicting closure; transaction/client/database-bound proofs retain exact semantic heads. Supported writers must advance common group heads for membership changes and preserve append-only evidence.

The private service implements propose, independent approve, reject, withdraw, detail and actor-bound receipt reads. Approval rechecks the original requester's identity, epochs, permission, membership, appointed policy and person snapshot alongside current reviewer authority. It compares the entire reviewed target, enforces distinct reviewer/requester/affected people, then atomically suspends the group, invalidates all affected account/session revisions and writes terminal proposal/history/receipt records. Rejection and withdrawal do not invalidate accounts.

Policy, permission, person, MFA and session checks run through final awaited receipt/read operations. Exact-body retries return the original transition without repeating suspension, invalidation or history. Receipt links, history and safe result projections are validated; private source evidence does not enter results. Creating a proposal does not grant read visibility. Missing receipt is unconfirmed, not a declaration of failure.

Three explicit System-scope permission definitions were registered; no existing role template, suggestion or finance pack gains them. Application permission alone never appoints a reviewer. The public service constructor always uses unavailable trust. A future approved adapter must resolve immutable-manifest appointments under the durable policy head and advance that fenced head on changes or revocation.

## Verification and evidence

Environment: pinned .NET 10.0.401 and disposable GUID-named databases on the existing local Mongo replica set at 127.0.0.1:27028. Staff, policy and identity-provider fixtures are synthetic. No live Auth0 tenant or approved personnel source is represented.

- Structural loader: initial failing stub test, then 30/30 core cases and 4/4 bounds cases passed. Covers expired/disputed/inactive aliases, complete closure, semantic changes, common-head insertion races, transaction/database binding and immutable evidence mutation.
- v9 storage: initial failing stub, then 28/28 migration cases passed; isolated overflow/self-review and canonical-person regression checks passed. The latter first failed and was corrected in both schema/runtime validation. The integration run below rechecks the final suite and prior migrations together.
- Service: initial 31/31 service cases passed; a separate permission test had an assertion-overload error, corrected without changing permission behavior.
- Independent review found missing strict requester schema/provider validation. Five added regression cases first failed (three reached approval, two produced uncontrolled database errors), then passed after validating and pinning the exact requester record.
- Final service and permission run: **51/51 passed**, zero failures/skips, **3m04s**. Includes current trust/default denial, independent review, requester/policy/target drift, all-alias invalidation, exact replay, malformed private receipt/history, concurrent decisions, both business-approval/withdrawal orderings, and expiry after receipt persistence or final replay/read waits.
- Final migration/target/session integration: **213/213 passed**, zero failures/skips, **13m41s**. Includes all 28 final v9 migration cases, 34 target cases, prior migration suites, API migration-health checks, invalidation, session fences, step-up and existing business-permission checks. Three earlier migration tests now recognize only the three new v9 collections while retaining their majority-write assertions.
- Real service-level uncertain-commit composition plus shared runner regressions: **5/5 passed**, zero failures/skips, **26s**. A unique appName-scoped failpoint injects UnknownTransactionCommitResult into commit replies after execution. An unaffected client independently confirms the committed group/account/session/history/receipt state while the fault is active. Receipt recovery and subsequent exact retry preserve that state; cleanup disables the failpoint in finally. This is commit-reply fault injection, not a claim of physically dropped networking.
- Compiled API export and generated-client drift checks passed; no public API contract change.
- Independent migration, target-loader and service reviews accepted the final source; the requester finding was explicitly re-reviewed and closed.

Ignored artifacts: tmp/production/person-governance-results/governance-migration-*.trx, withdrawal-target-*.trx, withdrawal-requester-red.trx, withdrawal-service-initial.trx, withdrawal-service-green.trx, withdrawal-integration.trx and withdrawal-uncertain-commit.trx, with sibling logs. Test counters were read from terminal logs and TRX.

The prior full 715-case backend result remains baseline evidence. It is not a new full-suite result. This change is checked through the new service suite plus affected migration/identity/session compatibility; no new browser or hosted-CI acceptance is claimed.

## Open work

Subsequent PG05 work now locally delivers policy-scoped discovery, named consequence previews, private API/DTOs, review UI and operation-specific receipt recovery. [The workflow checkpoint](person-review-workflow-acceptance-2026-09-28.md) records 298 backend tests, frontend/browser verification and its remaining limits. Explicit permanent no-effect resolution after reload is the separate open PG05R task; live reviewer/source authority remains unconfigured. The service-only evidence above remains historical evidence for its original checkpoint.

PG06–PG07 still cover authority-establishing source/binding/renewal/merge operations, reviewed bootstrap, invitation admission, specialized administration, finance conflicts and template migration. Staff-source validity, named reviewer appointments and bootstrap authorization remain D01; catalog/business policy remains D02. Direct uncoordinated database mutations are not a supported writer protocol.

All 97 report rows remain open for production acceptance. Accounting/evidence/OCR, catalog/packages/stock, business workflows, recovery/provider deployment, operational rehearsal and the accepted branch pilot remain in the active goal. No source appointment, live user grant, external message, paid resource, transfer or production cutover was created.
