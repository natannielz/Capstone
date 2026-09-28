# Inventory provenance verification — 28 September 2026

Status: local verification passed; independent review and full production inventory acceptance remain pending. Implementation is based on published storage commit eb674bf18bf9201165fc9f088e15a75253fbe4d7. No production migration or application route is activated.

## Implemented behavior

The private balance repository now checks server-loaded physical-pool ownership and immutable batch ancestry on create, read and change. A lot binds its actual organization/SKU/definition; the pool owns one exact location. Missing, malformed or mismatched records fail and abort the entire caller transaction.

Conversion ancestry must reach the declared root through at most64 real records, with unchanged organization and expiry and nondecreasing recorded times. Cycles, missing/self parents and forged roots fail. Null expiry remains null for products without expiry. Count observations cannot serve as a recognized root receipt origin.

Stock writes require an active pool and conditionally increment its metadata fence, so a suspension committed after a transaction's earlier snapshot prevents its stock change. A failed quantity transition or later operation rolls that fence back with the stock and previous caller work. Historical reads can inspect suspended/exhausted pools without changing their fence.

## Executed checks

| Run | Result |
| --- | --- |
| Initial real-Mongo fault tests before implementation |12 failed as expected: invalid/missing graph did not throw |
| First implementation check |12 passed,0failed,0skipped |
| Expanded graph and existing inventory regression |121 passed,0failed,0skipped;2m51s |
| Final graph and existing inventory regression |127 passed/executed,0failed,0skipped;2m18s |

The final127 include31 graph cases and the previous96 storage/quantity/source-authority cases. Tests use generated disposable databases on the guarded local Mongo replica set, with actual transactions rather than a mocked database. Fixtures supply synthetic pools and batches; they do not establish approved real source data.

Covered cases include missing or wrong pools/lots; every missing lot/pool field; duplicate/extra fields and incorrectly typed IDs; invalid dates/source kinds; cross-organization/SKU/definition bindings; multigeneration expiry preservation including null expiry; actual64/65-record traversal boundaries; transaction rollback after quantity failure; historical reads of suspended or fence-exhausted pools; and an actual Mongo write conflict after an externally committed pool suspension. Existing simultaneous last-package reservations and balance rollback checks still pass.

## Publication context

The preceding domain commit6e7d2274bd17824445712d14f6476c1e76d0d19e passed the complete [GitHub workflow36447001255](https://github.com/natannielz/Capstone/actions/runs/36447001255):1722 backend passes,0fail,72explicit skips,1794total; separate real-browser/HTTPS/Mongo check, frontend491+6+7,Auth0Actions5 and both contracts passed. Full-log SHA256: f36adbd3ac7cab874a6e2923c07c617cd33b4fd5aff26301a086134d38281d84.

The subsequent storage commit eb674bf was pushed and verified on origin. Its workflow36451369823 was still running at this checkpoint; frontend and separate real-browser jobs had succeeded. No full-workflow success for that commit is claimed yet. Its clean Vercel package contains616tracked files; archive SHA2569272f406f40ce0dcbd57dc624a98d36a74f1059d5abc46384d805e8d291e7f8a. Deployment remains pending owning-account access.

## Limits and remaining work

This validates structural references, not source approval or quantity/cost provenance. Governed pool/lot creation, source-event uniqueness, actual receipt/conversion/catalog validation, staff/source authorization, immutable movements and costs, forward migration/preflight, command receipts/audit/outbox and usable API/UI still remain. The existing migration registry stays at14. Pool fencing serializes writers within a pool; production contention/load checks remain necessary. Three independent reviewers remain unavailable under the previously reported account limit, so root inspection/tests are not presented as independent approval.

Local artifact hashes below describe the exact files tested; these raw artifacts remain under ignored local output directories. Hashes are evidence identifiers, not a claim of external review or deployment.

| Artifact | SHA256 |
| --- | --- |
| tmp/production/inventory-graph-results/inventory-graph-red.trx | 04ee7b44fa737e9355fec6e07646148f8e9592107fcf933f22ba548ee1a03f95 |
| tmp/production/inventory-graph-results/inventory-graph-first-green.trx | 15a22571f962c54b67457238fac1a5bff5c53663f66d96ffe220e9f164975d89 |
| tmp/production/inventory-graph-results/inventory-graph-integrity.trx | a6e4bb7931e87fd6a5b1f33ae0064ed210c802ab888b05c07c0293cfd07cafce |
| tmp/production/inventory-graph-results/inventory-graph-final.trx | 97d13ef97dde964d74d46a89a5d68d92aaba1de4a89efd6f3c32a4cea1cbe5ed |
| backend/src/UnitToko.Platform/Inventory/InventoryGraph.cs | a2e9afc1634ace0d6e2065732e89a09c2b1aaf62ed1f7da0b4111003f8475ed5 |
| backend/src/UnitToko.Platform/Inventory/InventoryRepository.cs | b973fde1257d13c12aa5e480465fdd16d9039e625536b9e7bc954f751bc54abb |
| backend/tests/UnitToko.Tests/Inventory/InventoryGraphTests.cs | f6a73d25dc01ad1fb8f22d359486ba44d64d95066b18aafcc8b616c2769a3496 |
| backend/tests/UnitToko.Tests/Inventory/InventoryRepositoryTests.cs | 18695b174fb0939efa82ab654aa2ff68d6dd41f3ae2e56d081c8e05b3f6572be |
