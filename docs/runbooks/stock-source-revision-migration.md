# Stock-source revision migration rehearsal

Migration 16 installs the three settings-history collections after the existing version-15 pool-creation migration. It does not activate stock authority, create opening quantities or authorize production cutover. The API requires a completed current migration; it never installs schemas during startup or a request.

## Before applying

1. Use the exact reviewed application commit and deployment configuration. Confirm the destination database explicitly; the automated tests use disposable synthetic databases only.
2. Pause and drain all application and integration writers. The deployment lease excludes another migrator, not every external database writer. Keep writers stopped throughout preflight and DDL.
3. Verify a coordinated backup and an applicable restore rehearsal. Retain original migration history, creation proposals/decisions/registrations, pools, revision proposals/decisions/applied versions, audit records, command receipts, actor records, scope ancestry and ID counters together.
4. Capture the completed migration history and baseline business counts. Version 15 must remain its original applied step. Do not delete its history or rerun its initial-pool rules over later revisions.
5. Check that current pools are inactive and each configuration version is explained by an approved settings snapshot. Inactive status alone does not establish that commitments were drained or a source handoff accepted.

## Apply and verify

Use the existing worker migration command from the backend directory with the approved Mongo configuration supplied through the deployment secret mechanism:

    dotnet run --project src/UnitToko.Worker -- --migrate

Before any target collection/index/counter changes, version 16 checks old and new storage contracts, unsafe indexes, counter ranges, the original creation graph, every approved version and all owned audit/receipt links. It validates exact original request hashes, results and retained approval participants. Current account suspension or removed person bindings do not invalidate historical records; retained participants are not a reconstruction of historical person assurance.

The migration installs strict/error validators for stockSourceRevisionProposals, stockSourceRevisionDecisions and stockSourceRevisions, retaining stronger existing validation. It enforces one decision per proposal and one settings snapshot per pool/version, installs scoped query indexes and advances allocation counters above existing IDs. It does not replace the original source-setup schemas. Schema/index/counter writes use majority acknowledgement.

After success, verify that migrations 1–15 retain their prior records, version 16 is applied and readiness passes. Compare business rows/counts with the baseline; only new schema/index definitions, required allocation high-water marks and migration bookkeeping may change. Rerunning the completed command must not replay business work or rewrite completed history. The protected revision API, typed client and staff comparison/recovery screen have separate integration evidence. Their real browser rehearsal does not establish operational activation or production migration acceptance.

## Failure and recovery

An invalid historical graph stops before target DDL/counter changes. Migration bookkeeping can record an unfinished applying step; this is expected and keeps readiness closed. Preserve the error and evidence, investigate the actual inconsistent records and use an independently reviewed repair or restore procedure. Do not delete receipts, invent approvals, lower counters, remove uniqueness or mark the migration applied by hand.

DDL is resumable, not one all-or-nothing Mongo transaction. If execution stops after a collection is created, keep writers paused and retry the same reviewed release after its lease is released or expires. Preflight runs again; compatible partial collections and stronger validators are retained. A live competing deployment lease must block further target writes. Never delete a live lease to force progress.

Application rollback is not a schema rollback. Do not resume an older writer merely because its binary starts. Restore the coordinated snapshot or use a reviewed forward repair, verify schema/application compatibility and reconcile affected records before resuming traffic. Production backup/restore acceptance remains an outstanding operations gate.

See [the migration evidence](../quality/stock-source-revision-migration-verification-2026-09-29.md) and [local development](local-development.md). Synthetic rehearsals are not acceptance of actual BNI/Koperasi sources, operating policies or production recovery.
