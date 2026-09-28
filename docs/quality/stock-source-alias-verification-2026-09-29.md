# Governed stock-channel alias service verification — 29 September 2026

Base commit: 2b807ed452f92209c2f11dbbb0cf457e35b2a946.

This checkpoint adds private channel-to-pool configuration governance within the existing stock source service. It is one part of CAT02 and does not complete the production or report requirements.

## Implemented behavior

A mapping names one actual canonical pool and a different requesting channel at the same organization/shop/warehouse. It cannot move stock, change physical ownership or target another alias. The existing inventory repository continues to store balances under the physical pool and canonical location.

Proposals bind both the current pool settings revision and the preceding alias revision. Independent approval produces an append-only enabled/disabled version; rejection and maker withdrawal create no mapping. Original settings/creation and prior mapping records remain unchanged. Approval requires an inactive pool, fences that pool and leaves its settings revision unchanged. An older mapping becomes stale after a settings revision and needs a new explicit review.

Read/propose/review capability remains separately granted. Every operation requires current authority over both the requesting channel and canonical owner. Approvals and rejections reuse current distinct-person evidence and fresh MFA. Final transaction checks fence both grants/memberships/topology and the live session/time; receipt replay also passes current checks. Actual review history and audit records are loaded from storage.

Current reads validate the latest applied mapping, its preceding version, approval cardinality and canonical pool facts. The bindingCurrent result describes configuration only: it is not source acceptance, stock availability, a reservation or authorization to write stock. No production consumer currently treats it as any of those.

Receipt-only recovery reconstructs only the original server-owned request. It verifies exact receipt shape, actor, operation, result, request hash, chronology and retained review participants, then requires the same receipt inside a current-authority replay transaction. Both channel scopes remain mandatory; separate history-read permission is not required. Missing receipts return no confirmation, and a receipt disappearing between lookup and replay cannot create a replacement writer.

Three strict draft collection contracts use exact BSON Long identifiers/versions, immutable proposal/decision records, unique terminal decisions and unique pool/channel/alias versions. The registered migration remains version 16. Tests explicitly install the draft collections in generated disposable local databases; the service refuses missing, optional or unenforced storage.

## Verification

The six policy cases failed before the domain existed, then passed. The first real-Mongo suite passed 25 cases after two reproduced provenance failures were fixed: a deleted applied mapping could otherwise look new, and internally consistent mapping history with a foreign physical key could otherwise support a new proposal. Approval cardinality and comparison with actual canonical source facts now reject both.

The expanded source/inventory regression passed 277 executed tests with zero failures or skips in 16 minutes 46 seconds, including all 36 then-existing alias cases. Tests additionally exercise populated exact large-quantity balances, enable/disable/re-enable history, pool settings reapproval, both channel grants/memberships/scopes, current distinct person/MFA, simultaneous approvals, changed access after audit insertion, strict database validation, receipt replay, actual audit failure rollback and existing source/stock mechanics.

Twelve new recovery cases first failed because RecoverAliasAsync did not exist. After implementation, the final focused suite passed all 48 alias policy/service/recovery cases with zero failures or skips in 3 minutes 33 seconds. That suite reran every alias workflow after recovery was added; other existing source/inventory code was unchanged. Across the 277-case regression and final 48-case suite, there are 289 distinct passing tests and 325 executed passes, not 325 distinct cases. No frontend, API route or generated contract changed in this checkpoint.

One initial fixture compilation failed because a target-typed ScopeNode constructor was ambiguous with a BSON overload. The fixture now names ScopeNode explicitly. This was a test setup correction.

Artifacts are under tmp/production/stock-source-alias-results, including both final TRX files, intermediate failure logs, final-results.json, the exact owned-path manifest and source/artifact hashes. Regression TRX SHA-256: 93c9d128edff3dd77c5c8ad716f55e907908ae4beeda3c8a0eaff5eea754ae78. Final alias/recovery TRX SHA-256: 6ec04d8cd0377215c1be97cc35c1feba0282d29a2687f6566af849436470e567. Only trailing final newlines were normalized after compilation; executable behavior was unchanged.

## Remaining integration and acceptance

A forward migration must preflight the complete historical graph, receipts, approval participants, exact source settings references, indexes and allocation ranges before production installation. Runtime latest/predecessor checks do not replace that full preflight. Strict API/generated client, staff mapping review and real browser coverage remain to be implemented for aliases.

Source acceptance, live per-channel policies/observations, active-source suspension/drain/handoff/reactivation, canonical inventory consumption, lot authoring, valuation, operational migration and owning order/procurement/fulfillment workflows remain required. An inactive flag is not evidence of completed handoff. No D04 business facts, source overlap, acceptance, external access or live deployment is invented. Independent review remains pending reviewer capacity.
