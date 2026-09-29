# Stock source suspension migration verification — 29 September 2026

Base commit: 34491dabf58d3bfa660e83569bee4d0f497f9169. Migration 18 advances CAT02 by making the audited suspension storage installable through the real deployment chain. The complete production plan and all 97 report requirements remain binding. Suspension API/client/staff integration remains next.

## Implemented behavior

The registry now requires migration 18. Its preflight checks prerequisite source/settings/mapping storage, complete original approval and mapping provenance, draft suspension records, exact typed identifiers, immutable configuration bindings, sequential versions, chronology, audits, whole-command receipts and allocator ranges before target DDL or counter changes. Original applied steps 1–17 are unchanged. A version-18 snapshot partitions the new suspension operation from the older source graph; the existing version-17 alias graph is reused unchanged.

Reverse validation follows suspension references as well as operation names. An audit or receipt copied under a foreign operation cannot evade the checks. Missing middle/tail records, changed source snapshots, already replaced settings or mappings, omitted historical channels, unknown actions, wrong bodies/results/actors, unsafe storage/indexes and exhausted or malformed counters are rejected without target mutation. Equal millisecond timestamps do not establish a transaction commit order.

The strict/error collection has unique poolId/suspensionRevision and dedicated nonunique audit and receipt lookup indexes. Existing stronger compatible validators and equivalent index names remain. Relevant counters advance above existing IDs without decreasing. Writes use majority acknowledgement; migration leases exclude competing migrators. Partial collection/index installation can resume after lease release or expiry. Application and integration writers must be quiesced separately.

Current suspended accounts/scopes or removed current person bindings do not erase legitimate historical actions; retained history does not reconstruct historical person assurance. Legacy active flags without a stop are preserved as historical data, with no activation approval inferred. A pool with a recorded stop must be inactive because this version contains no accepted reactivation lifecycle. Business source settings, mappings, quantities, lot ownership and financial records are not rewritten.

Service fixtures now install the registered schema. Upgrade fixtures run the actual historical chain through version 17; they never manufacture or remove migration receipts. Existing interrupted version-5, 6 and 7 checks explicitly allow and positively require the new suspension collection while retaining their other collection/majority-write assertions. Older mapping-success checks follow the current registry while preserving all original version-16 history.

## Verification evidence

- The initial test compilation found an ambiguous test-fixture constructor; it was made explicit. The first executable upgrade tests then failed twice because version 17 was still considered current and no version 18 was applied.
- After registration, the first combined suite executed 67 cases: 66 passed and one fixture failed before migration because the existing counter validator correctly rejected a deliberately malformed number. That disposable corruption fixture now explicitly bypasses document validation solely to exercise migration rejection. Application code and validators were not weakened.
- The broad affected regression passed **180 executed tests**, zero failures or skips, in approximately 7 minutes 12 seconds. This includes all 39 suspension-service cases, 38 initial migration cases, original source/settings/mapping migrations and the three interrupted historical upgrades.
- Nine review cases added obsolete-review, weakened-index and foreign-operation checks. Seven passed and two reproduced a real gap: relabelled audit/receipt copies escaped the reverse scan. The scan now also follows suspension references.
- Against that final implementation, **all 47 migration cases passed**, zero failures or skips, in approximately 2 minutes 4 seconds. They include interrupted actual collection creation/index creation, live competing leases, exact unchanged historical records, stronger validators/equivalent indexes, allocator recovery, same-timestamp legitimate histories and a corrupted record beyond the first 64-item cursor batch. The latter creates 65 actual approved pools and stops through the services; it does not fabricate approval evidence.
- An additional populated-stock upgrade case passed. An actual lot and balance containing 9007199254740993 base units plus reserved/held/expired buckets remain exactly unchanged. This is synthetic local preservation evidence, not live stock acceptance or production valuation.
- The union contains **190 distinct passing backend cases**, including **48 new migration cases**. This is not a new whole-backend run. All final tests and helper processes completed; retained intermediate failures are not relabelled as successes.
- Compiled server OpenAPI and generated-client drift checks passed with unchanged contracts. No frontend application code changed; no fresh frontend or browser execution is claimed. Independent agent review remains pending reviewer capacity.

Artifacts are in tmp/production/stock-source-suspension-migration-results, including initial/intermediate failures, final logs/TRX, manifests, checksums and final-results.json. SHA-256 values:

- Broad final-regression.trx: d2af6b3e9012963401e0ba6955966f2670abd2d48f7abf6b6cc6a7aaeebdb526.
- Final migration-final.trx: e7f7bde6f7a2303f86d28dbadcec680d73609265e00878fe46ba0b32db2e7d2c.
- Populated-stock.trx: 39707256d569ca403da54c8a210ec31e1be19d7ac982bf3b25b739127e77f2d6.

## Remaining work

The [migration runbook](../runbooks/stock-source-suspension-migration.md) records deployment, recovery and verification boundaries. Next implement the strict private-safe suspension API/client, separate operation-journal adapter, staff frozen confirmation/history/receipt recovery and real browser verification.

Accepted operational control, producer-complete drain/handoff, reactivation, persisted policy/observations, canonical inventory/cost consumers and the wider business workflows remain required. No missing producer is treated as zero obligations. Actual D01/D02/D04 and other business/provider inputs, production backup/restore/load/operations/pilot acceptance and Vercel account access remain unresolved. This local upgrade does not deploy the website, activate a writer or complete the production goal.