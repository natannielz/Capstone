# Stock-count staff screen implementation plan

This continues the approved production goal and the contract in ../../requirements/stock-count-staff-workflow.md. It needs no additional approval for local implementation. A working staff screen, browser verification, independent review and deployment remain open; API/client tests alone do not close this boundary.

## Current foundation

- Registered v14 stores immutable observations, their audit records and same-key receipts.
- Authenticated HTTP exposes six protected POST routes for named warehouses, countable packages, recording, history, detail and receipt lookup.
- The generated browser contract and modules/inventory/stock-count-client.ts provide these requests without automatic retries.
- modules/inventory/stock-count-workflow.ts validates public projections, explicit zero quantities, exact package contents, frozen reviewed commands and response binding.
- The existing operation journal persists actor/operation/key/time only and supplies a lookup-only inventory.stock-count adapter.
- These foundations do not implement balances, corrections, valuation, stock availability or an accounting source.

## Task 1: Implement the screen's view and submission lifecycle

Create frontend/src/modules/inventory/stock-count-editor-state.ts and frontend/tests/stock-count-editor-state.test.ts.

- [ ] Track actor, read purpose, canonical warehouse, search and selected package as explicit view context. Replacing any relevant context clears dependent selections and invalidates unfinished reads; a late response cannot repopulate the old choice.
- [ ] Use independent abortable read tickets for warehouse choices, package pages and history. Never treat aborting a dispatched write as proof of failure.
- [ ] Reuse server-time-based sessionTiming and actor crosschecks. Expiry, account replacement, unmount and BFCache restoration must invalidate display/approval state. A new session requires fresh verified current-access data.
- [ ] Review only a package selected from the current authorized choice response. Use prepareStockCount for a detached frozen command; blank quantity is invalid and explicit zero is valid.
- [ ] Freeze form edits during sending and uncertain outcomes. Retain the original command/key in memory for deliberate retry; persisted recovery contains no command contents.
- [ ] Test stale choice responses, changed warehouse/search/actor, expired session, double submit, zero conversion, immutable review, lost response then denied retry, and storage failure. Assert actual retained state and no unintended dispatch.

## Task 2: Build the protected staff experience

Create frontend/src/modules/inventory/StockCountWorkspace.tsx, a focused stylesheet and frontend/src/app/inventory/counts/page.tsx.

- [ ] Server page provides metadata/noindex and renders the client workspace. Follow frontend/AGENTS.md and the installed Next.js documentation.
- [ ] Load verified identity session then current access; require matching actor IDs. Display count and history areas independently according to stock.count and stock.read.
- [ ] Show actual organization, shop, warehouse and channel names in selection. Do not offer raw reference entry or infer names.
- [ ] Count workflow: select warehouse, find package, review SKU code/family/package kind/contents, enter observed packages and optional explicitly unverified lot label, give a reason, review and confirm.
- [ ] Show exact package-to-base-unit conversion and explain that this records one observation; it does not establish a warehouse balance or available stock.
- [ ] Provide bounded package/history pagination, clear empty/loading/error states and a deliberate refresh. Catalog conflicts require fresh choices and review.
- [ ] Supply the dedicated stock receipt adapter to OperationRecovery. Preserve original pending references when receipt lookup returns missing/denied/malformed/stale; do not offer close, abandon or automatic retry.
- [ ] History shows preserved package facts, reason, count and recording time. Count-only staff can recover their own receipt without history access.
- [ ] Use semantic form labels, grouped review controls, clear focus placement, live status/error announcements and responsive layouts.

## Task 3: Connect navigation and sign-in safely

Modify the existing AdministrationNavigation, OperationRecovery and identity-workflow modules, plus backend/src/UnitToko.Api/Identity/Auth0Options.cs.

- [ ] Add /inventory/counts only when the actual page exists. Show its destination for stock.count or stock.read; server checks remain authoritative.
- [ ] Add the exact path to both closed login-return allowlists and their tests.
- [ ] Route inventory.stock-count.record recovery to this page and give it a clear label. Preserve other recovery namespaces and their existing permissions.
- [ ] Revalidate browser/server actor handling and supported sign-in returns; do not activate a live Auth0 tenant without its required external configuration.

## Task 4: Verify the real browser boundary and publish accurately

- [ ] Run the new state/client tests and the full frontend regression suite, typecheck, import boundaries, lint and production build. Run only affected backend sign-in/contract checks after those edits.
- [ ] Exercise the actual rendered page with an isolated API and real local Mongo/session fixtures, using only synthetic accounts and data. Verify the existing same-origin API routing arrangement before claiming integration.
- [ ] Exercise keyboard and narrow-screen workflows: count zero, review explicit packaging, recover a lost response, expire/replace an account, switch warehouses during a read and fail local storage. Keep known outcomes distinct from unknown commits.
- [ ] Obtain independent source review when review agents are available. Their current usage-limit errors are an outstanding gate, not a completed review.
- [ ] Update evidence and progress docs with exact tested sources and limitations; preserve unrelated user changes.
- [ ] Push the verified work to codex/unit-toko-production and track the exact GitHub workflow.
- [ ] Prepare a clean committed-source Vercel release. Deploy to the authorized unit-toko-bni project when owning-account access is available. Do not equate frontend hosting with .NET/Mongo/Auth0 activation.

The full report, finance/payment evidence/OCR, roles, inventory ledger, operational readiness, hosting and one-branch pilot remain part of the active goal.
