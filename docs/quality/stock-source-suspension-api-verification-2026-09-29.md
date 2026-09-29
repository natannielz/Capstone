# Source suspension API and client verification — 29 September 2026

Base commit: 38c2d6e1b21c63b2e6b41c8032ad16b77f2b5847. This checkpoint connects registered migration 18 and the audited suspension service to protected HTTP routes, paginated history and a typed frontend gateway. It advances CAT02; all original production work and all 97 report requirements remain binding.

## Implemented behavior

Six protected POST routes provide a minimal action preview, a suspension command, latest history, bounded history pages, historical detail and original-actor receipt lookup. Preview and receipt recovery require stock.source.suspend with fresh MFA; general history separately requires stock.source.read. Every canonical and historically mapped channel requires current scope authority, including disabled mappings. Page/detail reads check final authority before returning. A foreign suspension ID selected under another authorized pool returns the same null as an absent ID.

The command binds quoted If-Match to the pool settings version, plus an exact suspension version and a SHA-256 configuration hash from the reviewed preview. Session, same-origin CSRF, expected actor, idempotency key and strict input validation remain required. Unknown properties, numeric or ambiguous Int64 values, invalid purposes, malformed hashes/keys, unbounded paging and replacement commands in receipt lookup are rejected. Responses keep IDs and versions as exact decimal strings and omit participant identities, audit/receipt internals and private configuration references.

The existing transactional service performs the restrictive stop, immutable event, audit and receipt. A stop is not accepted drain, handoff or activation. No new role receives this permission automatically. Recovery returns the original actor's committed result or not_confirmed under current action authority; it never dispatches an absent command or turns an unknown outcome into success.

The client validates closed response shapes, exact numbers, affected warehouse/channel sets, canonical owner inclusion, immutable history, chronological and version consistency and bounded ordered pages. Confirmation copies and freezes the exact preview, actor, reason and preconditions. A submitted command is sent once. Changed or late views cannot be confirmed by an old result; lost responses do not trigger retries. Recovery supplies only the original actor/operation/key and a deliberately selected pool. The persistent journal adapter and staff screen are not yet connected.

## Verification

- Initial real HTTP checks failed twice with missing routes. After implementation, seven of eight API/access/contract cases passed; the remaining case reproduced missing closed OpenAPI choices. The schema transformer resolved it.
- The completed focused suite passed 14 cases. The final affected regression then passed **80 executed backend cases**, zero failures or skips, in approximately 3 minutes 16 seconds. This includes original source/setup/settings/mapping HTTP behavior, all 44 suspension service cases and all four source OpenAPI contracts. Thirteen new backend cases are included, not added again.
- HTTP fixtures exercise actual middleware, opaque sessions, CSRF and a real local Mongo replica set. Coverage includes action-only accounts, separate history permission, every linked channel even after disable, revoked grants, five-minute MFA expiry, altered settings/hash/version, replay, another actor's key, no-write malformed input, exact 9007199254740993 identifiers and private response projections. New service cases prove final read authorization after either linked grant is revoked, original history after settings change, filtered paging and foreign/missing detail equivalence.
- Thirteen new frontend workflow/client tests passed. The first attempted test bundle failed because the new workflow module did not exist; that is missing-module evidence, not a behavioral test failure. The final full frontend run passed **633 normal cases, six future-date cases and seven legacy golden scenarios: 646 executions**, zero failures/skips.
- Frontend type checking, module boundaries, lint and production build passed. Lint retains nine preexisting warnings. Only the build's two generated declaration-path changes were restored afterward; executable source did not change.
- Structural contract comparison confirms **six added paths and ten added schemas**, with no modified or removed existing paths, schemas or other contract facts. A review helper initially expected eleven schemas; inspection confirmed the ten declared DTOs and corrected that helper count. Server/client drift checks also pass. Generated declaration reordering does not change existing API behavior.

Raw logs, TRX files, contract comparison, source/commit manifests and hashes are retained in tmp/production/stock-source-suspension-api-results. This is affected local regression evidence, not a fresh whole-backend or new suspension-browser run. Root inspection is complete; independent review remains pending reviewer availability.

## Publication and remaining work

Migration commit 38c2d6e1b21c63b2e6b41c8032ad16b77f2b5847 was pushed and verified at origin codex/unit-toko-production after predecessor 34491dabf58d3bfa660e83569bee4d0f497f9169 completed Quality run 36514692611 successfully. That predecessor executed 2,188 backend passes, zero failures and 75 explicit skips; all four separately executed browsers, frontend/build/lint, Auth0 Action fixtures and contract checks passed. Its evidence does not certify this new API/client commit. Migration Quality run 36518386948 is in progress when this checkpoint is recorded; publication must avoid cancelling it.

Next connect a dedicated stock.source.suspend recovery adapter before generic source handling, preserving only actor/operation/key/time in the journal. Add accessible staff preview, frozen confirmation, historical detail and receipt-only recovery for command-only accounts, exact login/MFA return paths and actual HTTPS/Mongo browser coverage. No new screen or suspension browser acceptance is claimed here.

Accepted operational control, producer-complete drain, handoff, distinct reactivation, persisted policy/observations/reconciliation, external holds, canonical inventory consumers, lots/costs and all other original business/operations/pilot requirements remain open. Missing producers are never interpreted as zero obligations. D01/D02/D04 and other business/provider facts must not be invented.

Vercel publication is authorized but still requires owning-account access. An exact tracked source archive is only preparation, not deployment. No live Mongo, .NET, Auth0, financial transfer, production data migration or pilot acceptance is inferred.
