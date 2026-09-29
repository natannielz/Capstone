# Stock channel mapping migration verification — 29 September 2026

Base commit: 8c8583c579a3ebc504b3c7b05c527b0adc166009.

This checkpoint registers migration 17 for the existing governed channel mapping service. It advances one portion of CAT02; the complete production plan and all 97 report requirements remain binding.

## Behavior

The upgrade requires the actual applied migration history through version 16. Earlier migration implementations remain unchanged. A version-17 copy of the source-history validation rules accommodates the new alias namespace and strengthens exact receipt-operation identity without changing an already applied step.

Before any target schema, index or counter changes, preflight checks prerequisite creation/settings storage, target collection options and validators, unsafe/extra unique/TTL indexes, duplicates, counters, original/current source provenance, every alias proposal/decision/version and both directions of audit and receipt references. It verifies exact hashes/results, original operation and retained participants, historical actor existence, canonical physical ownership, requested channel ancestry, bound settings versions and consecutive mapping history. It processes the complete cursor with bounded batches rather than a fixed total row cap.

Historical proposals may remain pending or become stale; rejection/withdrawal and mappings bound to an older approved settings version remain legitimate facts. Actual chronology must be coherent. Current actor suspension or deleted present-day person bindings do not retrospectively erase valid history, and retained approval participants are not reconstructed proof of historical person assurance. Equal millisecond timestamps and allocated IDs do not establish commit order.

The migration installs strict/error collection validation, preserves stronger compatible validation and equivalent index names, uses majority writes, advances counters without lowering them and resumes compatible interrupted DDL under the deployment lease. Invalid preflight leaves business rows, target schema/indexes and counters untouched; migration bookkeeping may retain an applying step that keeps readiness closed. Writers must be paused throughout the operation because the deployment lease does not fence external business writers.

Normal alias service tests now use the registered schema instead of privately installing draft storage. Public routes, frontend and generated contracts are unchanged. Mapping approval remains configuration only: it grants no accepted stock source, quantity effect, financial effect or permission for a downstream stock writer.

## Verification

The initial two upgrade tests reproduced the missing registration and then passed. A first 43-case integrity/lifecycle run passed 40 cases; three fixture failures were caused by a misspelled alias collection name. That fixture was corrected, and index-preservation coverage now explicitly requires the expected old unique index to exist.

Three additional tests reproduced a real provenance flaw: an extra receipt labelled as rejection could borrow an existing approval decision while the actual approval receipt remained valid. Version 17 now requires the reverse receipt operation to equal the linked decision outcome for original source, settings revision and alias workflows. All three failed before this change and passed afterward.

The final affected regression passed all 180 executed tests with zero failures or skips, including all 50 new alias migration cases. It covers registered migration installation and preservation, malformed/absent/orphan/duplicate history, mismatched receipt operations, exact source and mapping chronology, a 65-proposal cursor boundary, retained stale and suspended histories, weak indexes, stronger validation, majority writes, actual interrupted installation and competing leases. Existing alias service/recovery, source creation/settings migrations, older deployment upgrades and persistence readiness checks also passed. No frontend/API/generated contract changed, so no new frontend or browser result is claimed for this backend-only checkpoint.

Artifacts are retained under tmp/production/stock-source-alias-migration-results, including original failed runs, final TRX/log, owned-file manifest and source/artifact hashes. Synthetic Mongo databases use the isolated local replica set and contain no actual BNI/Koperasi data.

## Remaining work

Strict alias API/generated clients, an independent journal adapter and accessible staff mapping review/history with browser checks are next. Actual canonical inventory consumers, active-source suspension/drain/handoff/reactivation, accepted per-channel policy/observations, lots/costs and downstream workflows remain required. D01/D02/D04 and other business/provider/operations/pilot acceptance remain external gates.

Independent review is still pending reviewer availability. This local rehearsal does not establish production migration, backup/restore, source acceptance or launch readiness. GitHub publication and a prepared Vercel archive must be distinguished from an actual deployment; Vercel owning-account access remains pending.

See [the migration runbook](../runbooks/stock-source-alias-migration.md) and [source authority](../architecture/stock-authority-contract.md).
