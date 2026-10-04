# Release 1 — katalog dan permintaan cabang

Immediate delivery: approved account → scoped catalog → persistent request → staff accepts/rejects → requester reads status/history. Acceptance means **accepted for processing only**: no stock reservation, binding price, payment, invoice or fulfillment commitment.

Authoritative checkout: `C:/Users/User/Documents/Capstone`, branch `main`. All original requirements and the 97-item register remain the later-release backlog; accounting, refunds, OCR, procurement, valuation and notifications are outside this release.

## Short release checklist

- [x] Stop unrelated implementation and preserve unfinished mixed-receipt costing.
- [x] Preserve tracked/untracked changes in `codex/preserved-mixed-costing-20261004`; byte-exact manifest and verified recovery bundle in `tmp/production/preserved-mixed-costing-20261004`.
- [x] Implement branch-scoped catalog, packaging and request submission without financial/inventory writes.
- [x] Implement staff review and persistent requester status/history; reuse catalog maintenance.
- [x] Restrict pilot pages/navigation/API; remove proven unused duplicates.
- [x] Verify service permission/isolation, validation, retry/concurrency and Mongo persistence checks.
- [x] Demonstrate actual browser → .NET → Mongo journey, desktop/mobile and restart persistence.
- [x] Production frontend build and relevant local checks; inspected GitHub rules permit normal main push.
- [x] Commit and push coherent release; publish verified frontend and verify deployment status honestly.
- [x] Provide [local startup and private synthetic access](../runbooks/local-pilot-release1.md) and [exact external deployment prerequisites](../runbooks/release1-deployment.md).

## Evidence and status

| Layer | Current state |
| --- | --- |
| Implemented | Price-free request workflow, staff decisions/history, catalog maintenance and bounded pilot routes. Existing verified-provider session controls retained. |
| Connected locally | Verified persistent synthetic pilot. Browser submitted PT-00000001 and PT-00000002 to .NET/Mongo; staff accepted the first and rejected the second; requester read both results. Lost-response recovery, cross-branch denial, catalog edit, mobile/desktop layout and actual application restart persistence passed. |
| Staging validated | Not validated. |
| Production accepted | Not accepted. |

No real Auth0 application/API/Mongo environment was supplied or found in the connected Railway account (no projects). Vercel `unit-toko-bni` exists but contains the old demo/Turso settings, not the required production API connection. User delegated preparation choices on 4 October; sample accounts/catalog must remain explicitly synthetic. No purchase, external message or real-data cutover is authorized by that delegation.

Focused evidence: native Mongo request/decision flow covers branch isolation, revoked permission, duplicate keys and competing decisions with no stock/money effects; default-pilot route tests pass; retained frontend regression, lint/module boundaries and production build pass. The local launcher has a real Windows TLS handshake test. Evidence logs are in ignored `tmp/production/release1-*`; these checks do not establish hosted acceptance.

Browser acceptance caught and corrected two integration defects: the synthetic provider suppressed the form Origin, and account action discovery omitted the new request permissions. Origin validation remains strict; actual API/Mongo discovery checks now verify only the buyer/staff/reader actions each scope permits. The browser driver also retains non-secret progress so completed requests are inspected on resumption rather than submitted again.

Integrated browser evidence, 4 October 2026: `tmp/production/release1-browser-results/attempt8` records actual submission, lost response, original-key lookup and staff decisions; `attempt10` resumes the same references and completes catalog maintenance; `after-restart` confirms both decisions and the catalog description after stopping/restarting the real API and Next processes. Pass logs: `release1-browser-before-10.log`, `release1-browser-after-restart.log`. The receipt is `tmp/production/release1-pilot-browser-receipt.json`. No requests were resubmitted during resumption/restart checks. Empty, loading, denied, invalid-quantity and uncertain-result states were inspected.

Published application checkpoint: [`5127a7da1eeebd71666a01b51d52bbf3a61d6557`](https://github.com/natannielz/Capstone/commit/5127a7da1eeebd71666a01b51d52bbf3a61d6557). Vercel built that exact commit as `dpl_EPvnwVREatVP6HJJckdX9HBN5oHK` and reports Ready. The public [Release 1 frontend](https://unit-toko-bni.vercel.app/) shows the inactive-preview banner. Actual browser sign-in reports the unavailable account service; live probes return no-store 503 for `/api/v1/identity/session` and no-store 404 for legacy `/api/products` and deferred `/billing/invoices`. This is frontend publication, **not hosted journey acceptance**. GitHub's [Focused quality run](https://github.com/natannielz/Capstone/actions/runs/37180570201) completed successfully: frontend types/lint/module boundaries and backend compile/contracts/shared-authority regression passed. Existing non-failing lint warnings remain recorded in that run.

Hosted operation still requires the external inputs listed in the deployment runbook. Private accounts, keys, database files and browser credentials are ignored by Git. The preserved 97-item project and mixed-receipt costing are not complete and have not resumed.

## Visual restoration — 4 October 2026

Restored the original demo's editorial landing composition, bundled photographs, typography, motion, and branded navigation/footer for Release 1. Catalog and sign-in now share that visual identity, including understandable unavailable/account states. Editorial illustrations remain labelled and are not presented as approved catalog inventory. Navigation uses only the supported request/account routes; API clients, session/CSRF handling, permissions, retry protection, and deferred-feature boundaries remain intact.

Validation: production frontend build, TypeScript, focused ESLint, import boundaries and the existing catalog/request/identity/release-scope checks passed. Actual browser checks cover landing/catalog/sign-in at desktop and 390px mobile width, images and navigation. The restarted real .NET/Mongo local pilot passed the existing read-only browser acceptance again, including both saved decisions, cross-branch denial and the persistent catalog description. Evidence: `tmp/production/release1-visual-build.log`, `release1-visual-browser.log` and `release1-browser-results/visual-restoration`. No new requests or financial/inventory writes were created by this check. Hosted-service activation remains subject to the external deployment inputs above; restoring the design does not imply production acceptance.

Published visual checkpoint: [`b0854bb74ea031e4db2f1022425d95ef89715a45`](https://github.com/natannielz/Capstone/commit/b0854bb74ea031e4db2f1022425d95ef89715a45), Vercel `dpl_DVbKrce3cxrvT4wdkEqfJtwgg75v`, Ready on the production frontend domain. The actual public browser verified `/`, `/shop`, and `/identity/login` with the restored design and honest inactive-service states. [GitHub frontend types, lint and boundary checks](https://github.com/natannielz/Capstone/actions/runs/37182214246) passed; unchanged backend checks were correctly not selected. Local is verified; staging and the hosted business journey remain unaccepted.

## Cleanup record

- Removed disconnected duplicate storefront and its sole carousel component after source/script/test reference checks: `frontend/src/components/shop/storefront.tsx`, `frontend/src/components/collection-carousel.tsx`.
- Removed seven unused UI wrappers (chart, calendar, command, drawer, resizable panels, OTP input and carousel) and their seven dedicated packages plus unused `date-fns`, after checking imports across source, tests and scripts. Native controls and existing used components remain.
- Pilot defaults to `release1`; deferred API endpoints are not mapped. Retained full-backlog regressions and contract export explicitly select `full`. Future work remains in source/recovery rather than being represented as pilot functionality.
- Legacy database dependencies still have retained callers and are not blindly deleted. Pilot routes block legacy login/cart/checkout/account/API entry points.

When resuming preserved costing, reconcile its migration 78 with the Release 1 request migration; do not apply both under the same sequence number.
