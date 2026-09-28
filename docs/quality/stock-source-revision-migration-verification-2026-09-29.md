# Stock-source revision migration verification — 29 September 2026

Base commit: 20ec4e20051d48c3ca9b27989cb0e9123a06eb4e. This checkpoint registers migration 16 for source-settings revision storage. It preserves applied migration 15 and does not complete CAT02 or activate production stock.

## Implemented boundary

The forward step validates existing source-setup storage, new revision collection options/validators/indexes, allocation ranges and the complete original/revision/audit/receipt graph before target DDL or counter writes. It follows every approved settings version from creation to the current pool head, verifies exact base settings and chronological links, and rejects missing, duplicate, orphaned or unexplained records. Valid pending, stale, rejected and withdrawn proposals remain historical records.

Retained receipts must match their original actor, operation, request hash, result and approval participants. Actual historical actor records and scope ancestry must exist. Current account or scope suspension and removal of current person bindings do not reconstruct or invalidate past approvals. PersonApprovalReceipt stores participants; it is not a historical proof of person assurance. No source operating policy or real-business acceptance is inferred.

After successful preflight, the step installs strict/error validation for stockSourceRevisionProposals, stockSourceRevisionDecisions and stockSourceRevisions. It preserves stronger validators and compatible alternative index names, enforces proposal-decision and pool-version uniqueness, and adds scoped query indexes. Missing or low counters advance above existing records; incompatible or exhausted counters fail. Explicit target DDL/index/counter writes use majority acknowledgement under the renewable deployment lease.

All original setup migration behavior stays unchanged. The service tests now use the actual registered migration instead of installing draft storage themselves. Three historical interruption tests admit the new collection names while retaining their majority-write, validator and index preservation assertions.

## Verification scope

The new migration cases exercise actual historical versions 1–15 through their real deployment steps before applying 16; they do not forge or delete applied migration history. Command-event observers assert that failed preflight issues no target create/collMod/createIndexes/dropIndexes or counter update. Business rows and counters must remain equal. Migration bookkeeping may retain an applying step and readiness remains closed.

Cases cover clean installation and idempotent rerun; existing approved history and allocator repair; 21 damaged-history/storage scenarios; corruption older than the runtime current/predecessor window; historical participants after current binding removal; pending/rejected/withdrawn history; stronger validators and alternate indexes; actual interruption immediately after a successful create command; competing live deployment leases; and partial, sparse or hidden required uniqueness. Historical traversal streams batches of 64; duplicate-link lookups stop at two results.

Observed development sequence: the initial two cases failed when version 16 was absent, then passed with the implementation. The expanded 25-case integrity run passed with no skips. The final combined regression passed 110 of 110 executed cases, with zero failures or skips, in approximately 8m56s. It includes all 32 new migration cases, 48 revision service/rule cases and 30 affected source/generic/historical migration cases. Earlier green runs are subsets of this final result and are not added again.

No API or frontend contract changed in this slice. The previously published frontend and browser checks are distinct evidence; they do not verify a revision screen that has not yet been implemented. Independent review remains pending the existing reviewer capacity limit.

## Remaining work

Add receipt-only revision recovery, bounded authorized pool/proposal queries, strict API/generated-client projections, and staff current-versus-proposed comparison/history with real browser tests. Approval still leaves an inactive pool and causes no stock quantity or financial effects. Active-source suspension/drain/handoff/reactivation, shared-channel aliases, actual accepted observations, lot authorship, full operational inventory migration and source/cost workflows remain required.

Production deployment requires the [migration rehearsal](../runbooks/stock-source-revision-migration.md), actual business/operations decisions, access and acceptance. GitHub publication is authorized. Vercel owning-account access remains pending; a prepared source archive is not deployment. The full 97-report production goal remains active.

## Final artifacts

Artifacts are retained in tmp/production/stock-source-revision-migration-results: revision-migration-regression.trx, regression.log, final-results.json, source-hashes.json and the explicit owned-paths.txt publication manifest. The final TRX SHA256 is c2a2b4611dbb76a96bd3ba34806bdfc0f82ce273bd0cf05e0be5f81fbf3b1d17.
After the final test run, only trailing blank lines at the end of the two new migration source files were normalized. No executable source changed.
