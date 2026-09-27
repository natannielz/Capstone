# Business permissions and role suggestions — local acceptance

Date: 28 September 2026. Scope: ACC02.1–.2 on top of `512c497`. This checkpoint supplies explicit permission contracts and reviewed grant/invitation selection; it does not complete ACC02 catalog/history adapters or production business workflows.

## Delivered behavior

- Buyer spending approval and store order acceptance have distinct permission names and allowed scopes. Staff substitution proposals and buyer consent are distinct, with buyer ownership/assignment or explicitly wider buying scope still required.
- Package conversion, reservation release and supplier-change proposal/approval have fulfillment permissions. Historical/report reads have explicit read-only entries; registering them grants no inactive-resource exception.
- Approval evidence requires the exact reviewed revision and immutable requester/proposer. Spending and supplier-change approval require an independent account and fresh MFA. Trusted same-person independence remains ACC03.
- Six existing v1 role templates and materialized grants are unchanged. The API exposes optional suggestions separately; no new power is selected or granted automatically, and buyer spending approval is never a default/suggestion.
- Grant administration labels scope-compatible suggestions on existing explicit controls. Invitation proposals accept checked suggestions, show exact requested permissions in Indonesian and invalidate review when selections change. Role, recipient, scope and account changes clear stale selections.
- Approval still passes server preview/proposal, current scoped/delegable authority and independent review. Unknown, repeated or scope-incompatible selected permissions fail validation instead of silently changing the requested grant.

## Verification

| Check | Evidence / outcome |
| --- | --- |
| Focused backend red/green | Missing definitions/suggestions and missing HTTP field reproduced before implementation; new domain/store/HTTP cases passed 7/7; affected policy/approval/store/catalog checks passed 44/44 |
| Full backend | .NET SDK 10.0.401; `dotnet test backend/UnitToko.slnx --no-restore`: **362 passed, 0 failed, 0 skipped**, 10m15s; `tmp/production/acc02-results/acc02-business-permissions-full.trx` |
| Contracts | Exported OpenAPI and generated client, checked drift, then built/tested; no stale copied-contract failure |
| Frontend red/green | Focused new selection/suggestion tests: 6 initial expected failures, then 12/12 pass |
| Full frontend | Pinned Node 24.19.0: 323/323 domain tests, 6/6 future-date repository tests, 7/7 legacy golden tests; no skips; TypeScript passed |
| Build / lint / boundaries | Production Next.js webpack build passed; module/import boundaries passed; full ESLint zero errors and nine existing warnings outside changed code |
| Browser | Production preview on loopback 3118; Edge headless: all 12 account-administration groups passed, including three new explicit-selection/reset groups; desktop 1365×1000 and mobile 390×844; no horizontal overflow or observed page errors |
| Visual inspection | Reviewed `tmp/production/accounts-acc02-desktop.png` and `accounts-acc02-mobile.png`; checked selections and exact permission review fit both layouts |
| Independent review | Separate read-only backend/domain and API/frontend reviews accepted; read-only fixture corrected to test an actual read-only grant before final verification |

Real Mongo tests used the existing isolated loopback replica set and disposable fixture databases. Browser API responses were synthetic; the browser results prove UI behavior, not connected provider/API acceptance. Browser reports: `tmp/production/accounts-browser-result.json` (12 groups) and `accounts-browser-acc02.json` (three focused groups).

## Remaining work and limits

ACC02.3–.6 remain open. The [producer contract map](../architecture/business-access-contracts.md) records catalog-entitlement ownership, complete authorization evidence, historical-only grant lifecycle, filtered read surfaces and transaction-fence requirements; those mappings are not implemented business adapters. Each future business endpoint must load immutable approval history and enforce its exact permission in the owning transaction.

Invitations expose optional server suggestions; arbitrary additional permissions and PIC designation still use the separate governed access workflow. This checkpoint does not claim a complete broad-branch PIC invitation flow. ACC03 person verification/finance conflicts/template migration and ACC04 delegation/access reviews remain open. Real Auth0 delivery, source-event coverage, verified owner recovery, deployment, restore and pilot gates remain unchanged.

## Report plan and active goal

Re-read the supplied report and independently checked traceability: all **97** report IDs exactly match the acceptance register, the source SHA-256 matches, every item maps to executable tasks/acceptance, and conditional items preserve their required baselines and explicit applicability decisions. Both original-checkout goal documents link to the authoritative managed-worktree report plan/register/progress. Keep the existing unbudgeted production goal active; no duplicate goal or completed-production claim is warranted.

See [report completion plan](../superpowers/plans/2026-09-28-report-completion.md), [97-item acceptance map](../requirements/unit-toko-report-2026-09-28.md) and [current progress](production-progress.md).
