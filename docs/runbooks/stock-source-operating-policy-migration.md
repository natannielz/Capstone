# Stock operating-policy migration rehearsal

Migration 19 installs immutable operating-policy definitions for an existing physical stock source. It preserves applied migrations 1–18 and existing business records. A policy definition does not activate a source, accept external control, complete handoff or drain, or establish stock availability.

## Before applying

1. Select the reviewed release and destination database using the deployment configuration; retain credentials in the existing secret mechanism.
2. Pause and drain application, worker and integration writers for the whole preflight and installation. The renewable migration lease excludes competing migrators; it does not fence business writers.
3. Verify a coordinated backup and applicable restore rehearsal. Preserve source creation/registration, settings revisions, channel mappings, suspension events, policy proposals/decisions/versions, exact audits and whole-command receipts, historical identities/scope ancestry, allocator counters and business stock/accounting data together.
4. Capture the existing migration history and business baseline. Do not manufacture missing receipts, remove historical steps, lower counters, or substitute current person bindings for original approval evidence.

## Apply and verify

From the backend directory, using the approved deployment configuration:

    dotnet run --project src/UnitToko.Worker -- --migrate

Preflight validates prerequisite source/settings/mapping/suspension storage and complete provenance before policy records. Every policy proposal, decision and accepted version is checked against actual source revisions, mappings, suspension history and prior accepted policy. Exact audit copies and complete command receipts are checked in both directions, including original independently reviewed participants. Current account suspension or loss of a current person binding does not erase legitimate historical records.

The preflight checks both proposal and approval chronology. Equal millisecond timestamps alone cannot establish commit order, but sequential accepted policy versions do establish order: a later version cannot lose a previously accepted channel, regress its mapping revision, or repeat an unchanged definition.

Three collections are installed with strict/error validation: stockSourcePolicyProposals, stockSourcePolicyDecisions and stockSourcePolicies. The latter two receive unique proposalId and poolId/policyRevision keys respectively. Six supporting lookup indexes cover pool history and audit/receipt provenance. Compatible stronger validators, unrelated safe indexes and equivalent index names are preserved. Unsafe collection options, retention indexes, hidden/sparse/partial/incompatible required indexes, duplicate versions and invalid allocator values reject the upgrade before any target schema, index or counter write. Writes require majority acknowledgement; allocators advance to at least existing maxima and never move backwards.

After success, verify migrations 1–18 are byte-for-byte unchanged, step 19 is applied and readiness passes. Compare the captured business baseline, including nonempty stock, lots and accounting records. Repeat the same release and verify no target changes. Policy API, staff workflow, external services and production acceptance remain separate gates.

## Failure and interrupted installation

On invalid history or storage, preserve the diagnostics and original data. Migration bookkeeping may retain step 19 as applying, with readiness closed. Use independently reviewed repair or coordinated restore; do not bypass validation, discard evidence or mark a step applied manually.

Collection/index changes are not a single transaction. If installation is interrupted, keep writers paused and retry the same reviewed release after the lease is released or expires. Complete preflight runs again and retains compatible partial installation and the original migration start record. Do not delete a live competing lease.

Application rollback is not a database downgrade. Review schema compatibility and reconcile records before reopening traffic. Local synthetic rehearsals do not establish production backup/restore, real source ownership, operating procedures or pilot acceptance.

See [source authority](../architecture/stock-authority-contract.md), [local development](local-development.md) and the complete production/report plan.
