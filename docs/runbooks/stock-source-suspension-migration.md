# Stock source suspension migration rehearsal

Migration 18 installs immutable stock-source stop history and its lookup indexes. It preserves applied migrations 1–17 and business records. It neither stops a source itself nor accepts control, drain, handoff or reactivation. The suspension API and staff workflow are separate integration work.

## Before applying

1. Select the reviewed release and destination database using the deployment configuration. Keep credentials in the existing secret mechanism.
2. Pause and drain application and integration writers throughout preflight and installation. The deployment lease excludes other migrators; it does not fence external business writers.
3. Verify a coordinated backup and applicable restore rehearsal. Preserve original creation/registration, settings versions, channel mappings, suspension events, audits, receipts, historical identities and scope ancestry, counters and operational stock/financial records together.
4. Capture the original applied migration history and baseline business rows. Do not manufacture migration receipts, remove old steps, lower counters or fabricate missing evidence.

## Apply and verify

From the backend directory, using the approved deployment configuration:

    dotnet run --project src/UnitToko.Worker -- --migrate

The migration validates prerequisite source/settings/mapping storage and its full provenance, then all suspension records, sequential versions, immutable configuration bindings, exact audit copies, command bodies/results and reverse references. It checks chronology without inventing a commit order for equal millisecond timestamps. Historical account or membership suspension and removed current person bindings do not erase legitimate old records or prove historical person assurance.

No target schema/index/counter change occurs before complete preflight. The target collection is stockSourceSuspensions, with a unique poolId/suspensionRevision index. Dedicated nonunique indexes cover audit kind/record.poolId/record.suspensionRevision and receipt operation/result.poolId/result.suspensionId. Existing stronger validators and safe equivalent index names remain. Installation uses strict/error validation and majority acknowledgement. Allocators advance to at least stored maxima, never downward.

A legacy active flag without a recorded stop is preserved as historical state; this is not activation approval. A source with recorded suspension must remain inactive because this version contains no accepted reactivation lifecycle. Disabled mappings do not prove drained obligations and remain part of suspension history.

After success, verify migrations 1–17 are byte-for-byte unchanged, step 18 is applied and readiness passes. Compare baseline business rows, including stock and accounting. Rerunning the same release must make no target changes. Existing frontend/backend/provider cutover and business acceptance gates remain separate.

## Failure and recovery

Malformed or inconsistent history and unsafe storage fail before target changes. Migration bookkeeping may retain step 18 as applying; readiness remains closed. Preserve the failure evidence and use independently reviewed repair or coordinated restore. Do not bypass validators, discard receipts or mark a step applied manually.

Collection and index DDL is not one atomic transaction. After interrupted installation, keep writers paused and retry the same reviewed release after its lease is released or expires. Preflight runs again and preserves compatible partial installation. A live competing lease blocks the upgrade and must not be deleted to force progress.

Application rollback is not schema rollback. Review compatibility and reconcile records before returning traffic. Local synthetic upgrade tests do not establish production backup/restore, real source ownership, operations or pilot acceptance.

See [source authority](../architecture/stock-authority-contract.md), [local development](local-development.md) and the original production plan.