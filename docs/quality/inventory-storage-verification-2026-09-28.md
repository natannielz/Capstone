# Inventory balance storage verification — 28 September 2026

Status: private transactional storage component verified on the guarded local Mongo replica set. Production migration registry remains 14; no API, source authority, balance writer or business cohort was activated.

## Verified behavior

The final focused run passed 96 tests, zero failures/skips, in 1 minute 8 seconds: 22 real-Mongo storage cases, 15 strict-codec cases, and the existing 59 quantity/source-authority cases. All .NET projects built before execution. The original missing-implementation run failed all 12 initial storage cases; the first implementation passed those 12, then the expanded integrity run passed 95 before the final concurrency/batch strengthening.

- Actual two-balance changes commit with exact persisted counts and revisions.
- A caught stale second leg, cancellation between legs, a stale later entry inside one batch, or an owning-command failure rolls back earlier balance changes and a caller-owned test record.
- Two separate real transactions both read revision one and the same last package before either can reserve it. Exactly one succeeds; the other returns a bounded revision conflict. Final on-hand/reserved remain one and the revision advances once.
- The physical-pool/SKU/definition/lot unique key rejects a second copy under another channel.
- Storage requires an active transaction from the configured client, exact pool/location/key/version references, bounded distinct batches and revision-one creation.
- Strict decoding rejects missing/extra/duplicate fields, unknown kind, numeric coercion, invalid IDs/owners/revisions and invalid bucket relationships. Direct Mongo writes with malformed fields/buckets are rejected by the draft validator.
- A malformed preexisting row is refused without repair; the boundary aborts the caller's previous work.
- Disabled or optional schema, missing/sparse/partial/nonunique canonical indexes, additional unique constraints and TTL indexes prevent use of the storage component.
- Large IDs and quantities, including values beyond JavaScript's safe integer range and Int64.MaxValue, survive actual BSON round trips.

The tests use the existing guarded local fixture at 127.0.0.1:27028 with generated databases. Each test applies existing registered migrations, then installs the draft balance schema/index in its own disposable database. One explicit validation-bypass insertion supplies a malformed historical fixture only; runtime code has no bypass path. Test setup is not a deployable migration.

## Component boundary

[The inventory contract](../architecture/stock-authority-contract.md) describes the full current boundary and remaining work. InventoryRepository is internal, accepts only the caller's active transaction and performs no schema DDL. It implements persistence mechanics, not authentication or business approval.

No actual physical pool, source policy, lot, product ownership or costing is established by constructing a record. The eventual service must load and fence that authoritative graph, enforce the actor's current permission and expiry, and save movements, cost provenance, audit, outbox and a whole-command receipt atomically. A forward migration must preflight existing data/graph/history before registration. Actual external source capability, operational readiness and a branch pilot remain open.

Independent implementation review remains pending while the review agents are account-limited. This result does not replace that gate or complete CAT02.

## Published predecessor evidence

The browser commit 2e6b6727090c4c5a7930746338be71ce1a032527 passed [workflow 36442542166](https://github.com/natannielz/Capstone/actions/runs/36442542166): 1,663 backend passes, zero failures, 72 explicit skips (71 Windows-only methods and one browser opt-in case); 1,735 total, 32 minutes 20 seconds. Its separate browser job actually executed/passed one real HTTPS/API/Mongo rehearsal and its no-skip result guard. Frontend, Auth0 Action fixtures, contracts and builds passed.

The full log is tmp/production/github-publication-results/run-36442542166.log, SHA-256 9c4da7c73685a5062083b7f752827ebcbe2f27949eb64c8348bc17b186331ff6. The following quantity/source-authority commit 6e7d2274bd17824445712d14f6476c1e76d0d19e is pushed; its separate workflow 36447001255 is still running at this record, with frontend and browser jobs successful. This new storage component requires its own subsequent exact-commit workflow.

## Evidence

Paths below are relative to tmp/production/inventory-storage-results unless marked source.

| Artifact | SHA-256 |
| --- | --- |
| inventory-storage-red.trx | af5302a845241d95881897a3bdb7d038ff672a21a7f59d25850dab952b4f14c7 |
| inventory-storage-first-green.trx | d113eea6dd18eb00290f18383d78b29f444760f8a94512b63d68f6ec3ad6afeb |
| inventory-storage-integrity.trx | c5fd3102256acc6049f46b6be1f1b54b33ca75399d2e871e429619b26a1e49c8 |
| inventory-storage-final.trx | 68f57b3288781ea7d08c54ca1c7bb081bf805ec57dbb6fc2f38eac58e88c39b1 |
| source: backend/src/UnitToko.Platform/Inventory/InventoryRepository.cs | f493c9e5b4923dfcac48eaf96b2afea06c69a4df65bc4c920a6ace0c5c110825 |
| source: backend/src/UnitToko.Platform/Inventory/InventoryBson.cs | 57aace5b470ad0ce39576abf6a4bc33d6ecd2eb30a0821ab9a6307958039c6ae |
| source: backend/src/UnitToko.Platform/Mongo/Migrations/InventorySchemas.cs | 609de6e251c08f4509163e572216fad3b91ce846f85a840883b44106baec6846 |
| source: backend/tests/UnitToko.Tests/Inventory/InventoryRepositoryTests.cs | 38a7f7181f2bce6c40be3692feb93c4619e3077a3356f829f811d1de581e6e80 |
