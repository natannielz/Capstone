# Stock-source setup API and migration verification — 29 September 2026

Source base: bfb79c39d78719991bf1e02e0c2ec468f34bfa7f. This checkpoint supersedes the previous preparation-only checkpoint's statement that source setup has no API or registered migration. Independent review, staff UI and operational activation remain open.

## Delivered boundary

Seven authenticated POST endpoints under /api/v1/inventory/sources expose proposal submission, approval, rejection, proposer withdrawal, exact-location history/detail and actor-owned receipt recovery. The API uses the existing opaque session, same-origin, CSRF and expected-actor protections. Mutations require idempotency keys; decisions require the proposal's version. Read permission is separate from submission and review permissions. Missing and foreign proposal IDs return the same denial to an unauthorized decision caller; permission and stored scope are checked before loading private history.

Requests reject unknown fields and ambiguous numeric forms. Long IDs, freshness milliseconds, safety buffers and response versions are exact decimal strings. Public projections omit private identity bindings, participant proofs, audit IDs and receipt hashes. The contract adds seven paths and nine source-setup schemas; semantic comparison found no changes or removals to existing paths or schemas.

Recovery first reads only the current actor's operation/key receipt under current scope, then reconstructs the committed command from server-owned history. The transaction's new requireReceipt option prohibits submitting work if that receipt disappears between lookup and replay. Replay still checks current grants, session, fresh MFA and independent person evidence where required. Missing receipts report not_confirmed; that is not evidence that an interrupted request failed or may safely be resubmitted with a new key.

Approval creates a uniquely registered inactive pool only. Neither the API nor its DTOs offer source activation or a direct stock write. No role defaults, real business source policy, accepted external protocol, inventory quantity, cost or financial posting is created.

## Migration 15

The deployment registry now appends StockSourceSetupMigration after the actual fourteen existing steps. It installs strict/error validation for stockSourceProposals, stockSourceDecisions, inventoryPoolRegistrations and inventoryPools; unique decision and physical-key indexes; and location query indexes. Startup and request handling still perform no schema DDL.

Before target DDL or allocator writes, the migration checks existing collection/index options, strict record shapes, duplicate keys, allocator range and the linked proposal/decision/audit/receipt/registration/pool graph. Receipt bodies are rehashed from immutable business facts. Unaccepted active, revised or fenced pools and orphaned records are rejected. Historical actors and person sources can legitimately be inactive now; current authorization is required on subsequent runtime use, not fabricated into historical approval.

Stronger existing validators and safe equivalent index names are retained. Majority high-water corrections never lower an existing counter. All prior applied migration timestamps remain unchanged. Partial DDL resumes through the real renewable deployment lease; competing runners fail before source storage changes. Writers must remain paused and drained throughout deployment because Mongo schema DDL is not transactional. This migration does not register balance/movement/lot/source-activation storage or authorize live cutover.

## Executed evidence

| Stage | Observed result |
| --- | --- |
| Migration before implementation | 14 expected failures |
| Initial migration and existing source/graph regression | 77 passed, no failures/skips |
| HTTP routes before implementation | 4 expected failures (routes absent) |
| Expanded migration and initial HTTP suite | 22 passed, no failures/skips |
| Proposal-enumeration regression before fix | 1 expected failure (missing ID returned 400 instead of the inaccessible ID's 403) |
| HTTP guards and receipt-only race | 9 passed, no failures/skips |
| Server and frontend contract generation/check | Passed |
| Frontend type check | Passed |
| Final affected regression | 250 passed, no failures/skips, 15m44s |
| Final HTTP and OpenAPI follow-up | 9 passed, no failures/skips, 44s; includes the added history-isolation case |

Test-only ambiguous type and nullable assertion declarations were corrected before their affected test runs. They were build failures, not passing checks. The new tests run against guarded generated local Mongo replica-set databases with synthetic people and source settings.

New coverage includes actual v14 upgrade, incompatible rows/indexes/counters, orphaned and altered provenance, high-water correction, historical actor/source suspension, stronger-validator preservation, partial-DDL interruption/resume, competing leases, real opaque sessions, CSRF/origin/actor guards, exact large IDs, strict input, separate read/write/review grants, stale revision/MFA/session, nonenumerating decisions, reject/withdraw terminal behavior, receipt-only recovery after the receipt is actually removed, and generated security/numeric/private-field contracts.

The published base bfb79c3 passed GitHub workflow [36464417066](https://github.com/natannielz/Capstone/actions/runs/36464417066): 1,891 backend passes, zero failures and 72 explicit skips. Of those skips, 71 are platform-specific and one is the ordinary job's opt-in browser case; the separate real HTTPS/browser/Mongo job executed that case and passed. Frontend, Auth0 Actions and both contract checks passed. This result belongs to the base commit, not this API/migration change; the latter requires its own GitHub workflow.

The saved full base-workflow log has SHA256 674ece30ee57c6b1a359469b3cb0a5b6074863532e2befb19d89f1b0ca23a81d under tmp/production/github-publication-results. Local candidate evidence is under tmp/production/stock-source-migration-results.

## Remaining work

Staff screens and authorized warehouse choices, source acceptance/activation, changes and aliases for existing pools, governed observations/lot authoring, complete inventory migrations, owning procurement/conversion/valuation workflows, concurrency/load/restore acceptance and independent review remain open. Synthetic D01/D04 facts do not resolve actual business acceptance. The overall production/report goal remains active.

GitHub publication and Vercel deployment are authorized. Vercel owning-account access remains unavailable; a prepared archive is not a deployment. No production migration, provider activation, financial transfer or data cutover is claimed.

## Final artifacts

The two final runs cover **251 distinct passing cases** (259 executions, eight repeated HTTP/contract cases). The second build adds only one HTTP history-isolation test; runtime source is unchanged from the 250-case regression. It confirms that inaccessible and missing detail return the same null result, history counts exclude a foreign warehouse before paging, and invalid paging/purpose/unknown fields are rejected.

| Artifact | SHA256 |
| --- | --- |
| stock-source-api-final.trx | b634498642d112545223b987cf8b199e31a0f46b325b2fb5b80e3bd89f825f2c |
| stock-source-http-final.trx | 4a174d61c0d679476d131ab6bd9a3c034715c559cad917f177d029751c4ddfde |

The final source hashes, owned-path manifest and machine-readable result counters are stored beside these TRX files. The affected coverage includes 78 source setup/policy/migration/HTTP/contract cases, 116 inventory graph/ledger/repository cases, 33 permission/person/session/receipt/recovery cases, 13 accounting/general migration review cases, six persistence HTTP cases and three prior stock-count migration cases. Existing Catalog and stock-count contract checks are included in the remaining two cases. These local synthetic checks establish the tested candidate boundary, not production acceptance.