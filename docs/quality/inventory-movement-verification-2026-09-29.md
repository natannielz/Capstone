# Inventory movement verification — 29 September 2026

Status: local verification passed; independent review and production acceptance remain open. Source base:309888df2f3b4f44702a40c23c1f50372a6517c8. No migration, source adapter or runtime API is activated.

## Implemented result

InventoryLedger accepts a source reference/revision and joins the caller's active same-client transaction. Only configured same-database source readers can load and fence approved source facts and provide scoped stock effects. The ledger checks the returned source/pool revisions, captures real prior balances, applies all stock legs and appends one movement containing exact before/after images and actor/source metadata.

An already-applied event always aborts, including an identical duplicate. This preserves earlier caller work from accidentally committing as a second stock effect; whole-command receipt recovery remains the owning workflow's responsibility. All source/pool fences, balances and movement records roll back on a later failure. The failure of the real PostJournal boundary after a successful ledger call is included in the tests.

Strict draft movement constraints require exact types and the canonical unique source key. They also enforce internally consistent balance/pool/package/lot/location references, unique legs, valid stock buckets, exact revisions and all eight supported quantity transitions. They reject arithmetic overflow and fabricated resulting quantities. These constraints check record consistency, not actual source approval or financial/cost conservation.

## Executed evidence

| Run | Result |
| --- | --- |
| Before ledger implementation |9 expected failures; NotImplementedException |
| First schema candidate |9 setup failures; Mongo JSON Schema minimum/maximum do not accept BSON dates |
| Corrected schema and transaction/storage/graph regression |94 passed,0failed,0skipped;5m35s |
| New direct-database semantic cases before constraint implementation |8 expected failures; malformed effects were accepted by the shape-only validator |
| Movement-specific semantic and concurrency checks |46 passed,0failed,0skipped;2m35s |
| Final combined inventory regression |175 executed/passed,0failed,0skipped;about5m54s |

Final coverage comprises48 new ledger cases,37 storage/codec cases,31 graph cases and59 quantity/source-authority cases. The final TRX counters were checked directly. Real Mongo tests use guarded generated local replica-set databases; setup cleanup now disposes the new test factory's database if configuration fails. Synthetic source approvals remain test fixtures, not actual business approval.

The cases include source/actor/approval/revision rejection; malformed or mismatched reader snapshots; absent readers and cross-client/database rejection; missing/weakened validation/indexes; identical and changed duplicates; a two-transaction barrier forcing both to read the same source before fencing; source approval revoked after snapshot read; cancellation after source fencing; database rejection of the movement after stock writes; downstream accounting failure; two-leg success and stale-second-leg rollback; all eight stock actions; direct malformed effects/headers; exact Int64 quantity endpoints; and the longest169-character source key with Int64-max source identifiers. Existing final-package reservation races and pool/lot integrity checks still pass.

## Current publication evidence

The preceding exact commit309888df2f3b4f44702a40c23c1f50372a6517c8 passed the complete [GitHub workflow36453112905](https://github.com/natannielz/Capstone/actions/runs/36453112905). Backend:1790 passed,0failed,72explicit skips,1862total,32m45s. The72skips are the previously identified71 Windows-specific methods plus the opt-in browser case in the ordinary backend job; the separate real-browser/HTTPS/Mongo job actually executed and passed. Frontend491+6+7,Auth0Actions5, both contracts and production builds passed. Full-log SHA256:3f61fbd5f81aeb03de2face60f09a1f620e9a5bdc972179aae8f655c721c4d4d.

The earlier storage-only workflow36451369823 was cancelled when the substantive309888d update was published. It is not a full-green proof. A new full workflow is required for this movement implementation after its push. Vercel deployment remains pending access to the project's owning account; no deployment or production activation is claimed.

## Remaining acceptance

No genuine procurement/conversion/source adapter, pool/lot authoring, valuation policy, cost layers, source-echo mapping, final authorization/clock fences, whole-command receipts/audit/outbox or usable ledger API/UI is supplied by these tests. The owning workflows must implement those requirements and the forward migration/preflight before activation. The generic accounting failure test does not complete procurement acceptance or T05A. Pool contention/load, independent review, real source acceptance, operations and pilot gates remain open. The migration registry stays at14.

See the [source-owned transaction contract](../architecture/stock-authority-contract.md) for obligations and the official Mongo query-validation references used to implement the draft constraints. Local hashes identify tested source snapshots and artifacts; they are not external approval or deployment evidence.

| Artifact | SHA256 |
| --- | --- |
| tmp/production/inventory-ledger-results/inventory-ledger-red.trx | 857d39e6927b639a16471ce2a9f4616bf98f4d2c637002f0b62582e33c53a7ae |
| tmp/production/inventory-ledger-results/inventory-ledger-first-green.trx | 991a6a5cbca9436a03d9209ebc12d1a0ee66618ba68491f9919b95470732f4b0 |
| tmp/production/inventory-ledger-results/inventory-ledger-integrity.trx | 09f8cfd15321447586d45e7514be3f51dd4af807120387fc326901dae610db17 |
| tmp/production/inventory-ledger-results/inventory-movement-semantics-red.trx | 40c1b3878ed9215c8b119e0cd8d8fd4dbf6cc018eb10f6770e10a73e193db4de |
| tmp/production/inventory-ledger-results/inventory-movement-semantics-green.trx | c45326dc731b808cada8d79d6fd14e7eb61541276b08bacad0779e679a462117 |
| tmp/production/inventory-ledger-results/inventory-ledger-final.trx | 7a351fb80d586a17c8c7af50bf4b290fc5427d6a1727c4dd98b90911300d69dc |
| backend/src/UnitToko.Platform/Inventory/InventoryLedger.cs | 4f94ae020f2d971a32910bc1191a3918bf3dd62c0b0f2e2afe73c7e4677e52d7 |
| backend/src/UnitToko.Platform/Inventory/InventoryMovementBson.cs | 20fe7da0b785a0fa380a0f6b2c7ff5524401ce8e4e412aabf6abf66b702066dd |
| backend/src/UnitToko.Platform/Mongo/Migrations/InventoryMovementSchemas.cs | 129583693a1e4a1dbc3c98e4cf10a416a3050a0607badabbf27ca3f209d5673e |
| backend/src/UnitToko.Platform/Inventory/InventoryRepository.cs | a0cc064de975a237b742a546827b73e8e740332c07b8f06426e0ca2b792c69b5 |
| backend/src/UnitToko.Platform/Inventory/InventoryGraph.cs | c92b1cb8ce03591b6b5b65d36b9a3fc32bd000e9a047970252a8af17679941b9 |
| backend/tests/UnitToko.Tests/Inventory/InventoryLedgerTests.cs | 821f7d5b80f7c4c50f1906961c8419bbd0c722db80352deb50ba74099eff1365 |
