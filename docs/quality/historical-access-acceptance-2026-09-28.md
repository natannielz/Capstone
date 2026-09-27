# Historical-access producer and administration acceptance — 28 September 2026

This checkpoint implements ACC02.4–.6 locally after `a56036e`. It supplies R04/R14 producer and administration behavior and part of R05/U02–U04/U06. It does not close their production report acceptance or the full goal.

## Delivered behavior

- Explicit history/report grants use a specialized internal marker, not a seventh role. An active approved parent organization membership contains the named active/suspended branch or division. Existing role templates and ordinary active-only write policy remain unchanged.
- Preview, proposal, independent approval, rejection, withdrawal, revocation and same-key reconciliation reuse governed access lifecycle. One complete current approval grant must delegate all selected permissions; beneficiary/requester cannot approve. Stored proposal envelope and request must agree.
- A protected recipient-scoped target selector applies scope before count/page and exposes safe unit labels. The Indonesian administration form requires current server preview, clears changed or switched drafts and uses frozen same-body retries and body-free recovery references.
- Canonical historical metadata reads apply the same filter before search, count, page, internal/public ID and image/evidence/export/event reference lookup. Branch reads include valid child divisions; unrelated/malformed/admin records and confidential fields are excluded.
- Real Mongo transactions fence current actor/session, grant/member, active parent and exact historical nodes. Strict v7 business-read audit persists only with the read. The migration preflights data/options/index/retention/sequence and preserves earlier migration definitions.

## Review corrections

Independent review found an ordinary permission-selection carryover when switching via historical mode to another membership; both transitions now clear those selections. Pure and Mongo checks were aligned for malformed optional actor references. A final audit-write expiry regression verifies that grant/session expiry after insertion aborts the read and audit. These corrections are included in final verification below.

## Verification

| Check | Evidence/result |
| --- | --- |
| Full backend regression | **586/586 passed, zero failed/skipped**, 20m13s, .NET 10.0.401 and disposable loopback Mongo replica-set databases. `tmp/production/historical-access-results/historical-full.trx` is authoritative. |
| Pure historical policy | 26/26 passed; matcher parity regression first failed as expected, then passed. |
| Governed grant lifecycle | 32/32 passed in the final full run. |
| Historical API/selector | 6/6 passed with actual API host and disposable Mongo. Absent routes first returned expected RED failures; scoped selector total/page and invalid recipient membership are covered. |
| Store/migrations | 43 store + 18 new migration + 21 prior catalog migration + 20 prior provider migration cases passed in the full run. The initial focused run's invalid zero-revision identity fixture was corrected and rechecked. Post-audit grant/session expiry first failed as expected, then passed with audit rollback. The new audit schema permits zero actor revision without weakening the frozen identity validator. |
| Frontend | 329/329 main tests, 6/6 future-date cases, 7/7 golden scenarios; generated-contract typecheck, module boundaries and production build passed. Lint: zero errors, nine existing unrelated warnings. |
| Browser | 12/12 historical groups; prior ordinary-access and lifecycle/history groups passed at both desktop and mobile. Edge headless, production-built loopback frontend, synthetic API/CSRF fixtures. No horizontal overflow or observed page errors; screenshots visually inspected. |
| Contract/review | Compiled OpenAPI export check and generated client drift passed; independent backend/frontend/final-expiry reviews accepted the corrections. Staged diff check passed. |

Focused machine-readable artifacts: `tmp/production/historical-access-results/historical-policy-final-green.trx`, `historical-actor-parity-red.trx`, `historical-http-selector-green.trx`, and `tmp/production/historical-access-results/post-audit-{red,green-final}.trx`. Frontend commands/results: `tmp/production/historical-frontend-checks.md` and `historical-frontend-build.log`. Browser JSON/screenshots: `tmp/historical-access-browser/`, `tmp/production/historical-ordinary-access/`, and `tmp/production/historical-lifecycle/`. The owned preview server was stopped after verification.

New contracts were exported from the compiled API and generated frontend types checked. Read-only backend and frontend reviewers checked the final changes; no unresolved mandatory finding remains. Hosted CI and real deployment/provider acceptance were not run.

## Integration limits and release gates

Only administration has new browser/API routes in this checkpoint. Order, invoice and shipment history sources are metadata producers tested with disposable Mongo fixtures, not newly completed business modules. Reference helpers deliver no actual image, evidence, export or event payload. Future owning modules must enforce immutable buying ownership or serialize ownership changes, choose safe display metadata, build indexes and reauthorize actual deliveries. See [historical read contracts](../architecture/historical-reads.md).

Current maker/checker checks distinguish accounts. Trusted person independence remains ACC03. Named historical reviewers and any suspended-branch settlement policy remain business decisions; historical read grants do not permit settlement or any ordinary write. No real user, live grant, external message, paid resource or financial transfer was created.

The [97-item report register](../requirements/unit-toko-report-2026-09-28.md), complete business workflows, Auth0/recovery acceptance, operational/restore rehearsals, pilot and rollout remain open. Continue the existing production goal.
