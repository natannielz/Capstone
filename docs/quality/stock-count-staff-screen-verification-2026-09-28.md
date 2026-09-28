# Stock-count staff screen verification — 28 September 2026

Status: locally implemented and verified screen. The same-origin browser/API/Mongo rehearsal, independent implementation review and deployment acceptance remain open. This record supplements the [API/client acceptance](stock-count-staff-api-acceptance-2026-09-28.md) and [screen plan](../superpowers/plans/2026-09-28-stock-count-staff-screen.md); it does not close the full inventory or production milestone.

## Delivered behavior

The actual /inventory/counts page loads verified identity and current access, requires matching account references, and presents count and history work separately for stock.count and stock.read. It offers named, scoped warehouse/channel choices and package search; staff do not type raw organization or product references.

A count requires an explicitly selected current package, a whole-package quantity (including deliberate zero) and a reason. The page displays the package contents and exact conversion, then freezes a detached review and requires confirmation. It records a single immutable observation, not a stock balance, availability decision, correction or accounting source. History preserves the original packaging facts and marks optional observed lot labels as unverified.

The controller cancels replaced reads, fences late responses by view/account/session, hides private display state on expiry, and rechecks identity/access after browser page restoration. A new session does not inherit the old review. Double submit is blocked. Unknown saves retain the original key and command in memory for deliberate retry; persistent recovery contains actor, operation, key and time only. A denied retry after a lost response cannot discard the unresolved original. Receipt checks have no close/abandon operation; missing, invalid and stale results retain the reference.

Navigation and recovery now point to the real page. Frontend and backend accept only its exact sign-in return path; slash, query, fragment and encoded alternatives are rejected. No provider configuration or live tenant was enabled.

## Verification

| Check | Result and boundary |
| --- | --- |
| Frontend regression | 491 domain cases, six future-date cases and seven preserved golden cases passed, zero failures/skips |
| New lifecycle/navigation regressions | Ten editor cases and three navigation/return/recovery cases included in that suite |
| Affected backend identity checks | 41 passed, zero failures/skips; Auth0 claims/options and identity endpoint classes |
| Types, boundaries and lint | Production build typechecking and module boundaries passed; lint zero errors, nine preexisting warnings |
| Production build | Passed with the new /inventory/counts route |
| Actual rendered browser | Eleven scenario groups passed using Edge and the local production Next.js build, with intercepted synthetic API/session responses |
| Visual and keyboard check | Desktop and 390px screenshots inspected; review focus, cancellation focus, labeled controls and 320/390px no-horizontal-overflow assertions passed |

Browser groups cover explicit zero and frozen review/history, lost response then denied same-key retry and receipt recovery, separate permissions, stale warehouse responses, expiry before and during a save, blocked storage, catalog conflict and fresh selection, restored-page account mismatch, malformed receipt preservation, and mobile keyboard/layout behavior. Every fixture checks the expected actor/CSRF headers and the stock-count namespace. These assertions verify browser request composition; actual server enforcement is covered by the separate API tests, not by intercepted browser responses.

The first browser pass exposed an unstable accessible name on the warehouse select. The screen now uses an explicit label reference. Cancelling review restores quantity focus after React enables the input. Both were verified in the final production-mode run. The initial typecheck exposed a package-kind label indexing error, corrected before the successful build. The new backend allowlist test first failed against the old defaults, then passed after adding only the exact route.

Local production verification included the preserved user layout file. Its unrelated Figma script is conditional on development mode and is excluded from publication; it is inactive in this production-mode run. No unrelated layout, mentor mockup or ignore-file changes are part of this screen.

## Evidence

Files are retained under tmp/production/stock-count-results (synthetic fixtures, not production user data).

| Artifact | SHA-256 |
| --- | --- |
| staff-screen-frontend-tests.log | 85a5b9d96941db79fbea75e44ee3af3c8f2703092e6094dc5781d1e7765855f1 |
| stock-count-signin-green.trx | 4532868944dc5663b8111ae7a9e04cd7e0767eb1ba1e5fa1c09dcc02a739cd20 |
| staff-screen-build.log | 167f2ed026b4601c3966563b151711314855bab24c78dbe79434ba47b2125c45 |
| check-stock-count-browser.mjs | e18e0116b4875f8c04328baa55817c21dced342d282d45c4356056bfac60031e |
| staff-screen-browser-results.json | e504dfe6b2ba38df163b791a3a9651f4fb9a45f4b9ab75ca330b2e0d1119bf97 |
| stock-count-desktop-synthetic.png | c70d11c269e726a87a74b1b103c1f51e10fb37b2d2da778957a3b93697eb8ab3 |
| stock-count-mobile-synthetic.png | 811c78afceb5d29a547b14b0721f00db08d8d39cf68c689bb51bbe13f7098a1c |

The browser harness is local evidence; its responses are simulated and its runtime path is specific to this verification machine. It must not be described as a real Mongo, Auth0, TLS/proxy, deployed-host or branch-pilot test.

## Published foundation and remaining gates

The preceding commit 2dc0b9c82035f1eb729f383108d1b79f9ccffcc5 passed [full GitHub workflow 36432695309](https://github.com/natannielz/Capstone/actions/runs/36432695309): 1,657 backend passes, zero failures, 71 explicit Windows-only method skips on Linux (1,728 total, 31m42s), five Auth0 Action cases, both contract checks, frontend 478+6+7 cases, types/lint and builds. Log SHA-256: 8cd695f22e8a7fdfcab4020748887ed5336c327c7a01826d090064fdaca72e14. This resolves that commit's earlier pending-CI entry; it does not cover the later screen source.

The next published screen commit requires its own full workflow. Independent review of the new choice/API/client/screen source remains pending because all three review agents reported account usage limits; earlier reviewed persistence and journal work does not cover it.

Next integration work must use a real isolated API, Mongo replica set and synthetic sessions through the documented single HTTPS origin, and verify actual cookies, CSRF, proxy boundaries and persisted observations from the browser. Auth0 provider acceptance remains separate.

Vercel owning-account access remains unavailable. A clean committed-source release can be prepared for unit-toko-bni, but deployment and .NET/Mongo/Auth0 activation have not occurred. All 97 report requirements, remaining inventory ledger, evidence/OCR/payment/accounting work, operations and one-branch pilot remain in the active goal.
