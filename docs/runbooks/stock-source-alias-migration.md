# Stock channel mapping migration rehearsal

Migration 17 installs governed channel mapping history after the actual version-16 stock-source settings migration. It preserves applied migration records and business history. It does not activate a pool, accept a source handoff, create stock or authorize production cutover. Application readiness requires the completed current migration; application startup and requests do not run it.

## Before applying

1. Select the reviewed application commit and confirm the destination database through the deployment configuration.
2. Pause and drain application and integration writers throughout preflight and installation. The migration lease excludes other migrators, not external business writers.
3. Verify a coordinated backup and an applicable restore rehearsal. Retain migration history, original pool creation and registration, settings proposals/decisions/versions, channel mapping proposals/decisions/versions, audits, command receipts, historical identities, scope ancestry and counters together.
4. Capture the actual applied migrations 1–16 and baseline business rows/counts. Never remove or manufacture migration history to force an upgrade.
5. Check that pools are inactive and every source configuration version is explained by an approved snapshot. An inactive flag alone does not establish drained commitments or an accepted handoff.

## Apply and verify

From the backend directory, use the existing worker with approved Mongo configuration supplied through the deployment secret mechanism:

    dotnet run --project src/UnitToko.Worker -- --migrate

Before changing target schemas, indexes or counters, version 17 validates prerequisite storage, target collection options, duplicate/unsafe indexes, allocator ranges, original source creation and settings history, and all channel mapping records. It checks forward and reverse audit/receipt links, operation identity, exact request hashes and results, retained review participants, actual historical actor records, physical ownership, requested channel ancestry, bound settings versions, predecessor chronology and the complete mapping sequence.

Existing account suspension or removed current person bindings do not erase legitimate historical approvals. Retained participants are historical facts, not proof reconstructed from current identity bindings. Same-millisecond timestamps and allocated identifiers do not establish a transaction commit order.

The target collections are stockSourceAliasProposals, stockSourceAliasDecisions and stockSourceAliases. Installation uses strict/error validation, retains stronger compatible validators and equivalent unique index names, enforces one terminal decision per proposal and one mapping per pool/channel/version, and installs scoped history indexes. Relevant counters advance to at least stored maxima, never downward. Schema/index/counter writes use majority acknowledgement.

After success, verify the original 16 migration records are identical, step 17 is applied and readiness passes. Compare baseline business rows: source settings, mappings, audits, receipts, stock and accounting must remain unchanged. An immediate rerun must make no target schema/counter changes or business effects. Approved configuration remains separate from operational stock authority.

## Failure and recovery

Invalid history or unsafe storage stops before target schema/index/counter changes. Bookkeeping may retain step 17 as applying, which keeps readiness closed. Preserve the evidence and investigate through an independently reviewed repair or coordinated restore. Do not delete receipts, invent approvals, lower counters, remove uniqueness or mark the step applied manually.

MongoDB does not transact this collection/index installation as one unit. After interruption, keep writers paused and retry the same reviewed release after its lease is released or expires. Preflight runs again and retains compatible partial installation. A live competing lease must block all further target changes; never delete it to force progress.

Application rollback is not schema rollback. Verify schema/writer compatibility and reconcile records before resuming traffic, using coordinated restore or reviewed forward repair when needed. Synthetic tests do not establish production restore acceptance, actual D04 source decisions or pilot acceptance.

See [migration verification](../quality/stock-source-alias-migration-verification-2026-09-29.md), [source authority](../architecture/stock-authority-contract.md) and [local development](local-development.md).
