# Website Ready: Multi-Agent Program Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Unit Toko website ready for real use. The rules are enforced (guests see only staff-chosen sample products), every flow has one clear path, every function is proven to work by tests per account, and every page looks professionally finished on desktop and phone.

**Architecture:** One orchestrator runs the program in this session and coordinates eight specialist agents in four phases: set up, audit, fix, verify. Auditors work read-only and feed one findings register. Fixers take findings by ID, work test-first in their own git worktrees, and never push. The orchestrator merges, runs every heavy test itself one at a time (16 GB machine), commits and pushes.

**Tech Stack:** .NET 10 API and modules, MongoDB 8.2, Next.js 16 (webpack, read `node_modules/next/dist/docs` before writing Next code), React 19, Playwright browser rehearsals driven by xUnit, node:test unit suite.

**Spec:** the owner's request of 21 October 2026, restated in "Requirements" below, plus the earlier approved decisions it builds on: a public catalog without prices, staff choose what is public under maker/checker, and photos shown publicly only as a clean copy.

## Requirements (owner, 21 October 2026)

- R1 **Rules.** A visitor who is not signed in can browse the catalog, but only some items chosen as examples, never all of them. Today this cannot be done. Rules like this must be finished and actually enforced, for every account type.
- R2 **Flow.** The website flow is messy and must become one clear path per task.
- R3 **Functions.** Functions must be seen working. Test every function, per account.
- R4 **UI.** The UI looks like amateur work: placement, heights, spacing and more. Bring every page to a professional finish.
- R5 **Agents.** Run multiple agents: one for function, one for gaps, one for fixes, one for UI/UX, plus any other agent the website needs.
- R6 **Models.** Every agent runs on Opus 5.5. The orchestrator runs at extra-high effort; every other agent runs at high.

## What is already known (baseline facts, 21 October 2026)

- K1 The guest catalog (`/katalog`, API `GET /api/v1/public/catalog`) shows whole **assortments** that staff made public. It cannot show a chosen subset of products. The running pilot shows guests **0 products**, and approving its single assortment would expose all 200.
- K2 There are 81 pages with duplicates:
  - four sign-in pages (`/login`, `/customer/login`, `/staff/login`, `/identity/login`);
  - two account pages (`/account`, `/identity/account`);
  - two order lists (`/account/orders`, `/ordering/orders`);
  - two checkouts (`/checkout`, `/ordering/checkout`);
  - two catalogs (`/catalog/browse`, `/shop`).
- K3 The pilot has four synthetic accounts: pemohon-a, pemohon-b, petugas (catalog) and peninjau. No account exists for finance, warehouse, courier, procurement, branch approval or access administration, so those functions cannot be tried in the pilot at all.
- K4 Work in flight:
  - Typed numbers (`KLH-`, `RTR-`, `PP-`, `SP-`) are uncommitted.
  - A payment-supplement fixture fix is not yet built.
  - One refund-settlement evidence test is timing-sensitive.
  - The evidence batch passes 36 of 40.
- K5 The API limit is 240 requests per minute per IP. BNI branches behind shared proxies would share it. This is still open for the owner.

## Decisions for the owner before execution

| # | Decision | Recommendation | Alternative |
|---|---|---|---|
| D1 | What staff pick for guests | **Products** (a product shows all its packages, without prices) | Individual packages |
| D2 | Sign-in pages | **One page, `/identity/login`, "Masuk"**, for everyone. The staff door links to it with `returnPath=/workspace`. Old URLs redirect. | Separate customer and staff pages |
| D3 | Pilot accounts | **Add one synthetic account per role** (14 accounts, list in Task 0.3) so you can try every function yourself | Keep 4 accounts; other roles are tested only on the local review stack |
| D4 | Orchestrator | **This session**, switched to Opus 5.5 at extra-high effort | A separate orchestrator agent |
| D5 | Rate limit (from earlier) | Per-person limit of 240/min after sign-in, plus a configurable higher per-IP ceiling | Unchanged until BNI's network is known |

## Agent roster

All agents use model **Opus 5.5** (`model: "opus"`). Effort: orchestrator **xhigh**; every other agent **high** (`effort: "high"`).

| Agent | Effort | Owner request | Mandate | Writes |
|---|---|---|---|---|
| ORCH: Orchestrator (this session) | **xhigh** | R5, R6 | Runs the plan, triage, worktrees, the heavy-job queue, merges, all commits and pushes, owner reports | Ledger, register triage, merges |
| ENV: Review environment | high | "agent for what the website needs" | Local review stack with every persona, screenshot and axe harness, pilot seed (samples, personas per D3) | `backend/tools/UnitToko.LocalPilot`, `frontend/scripts/review/` |
| GAP: Rules and flow auditor | high | "agent for the gap", R1, R2 | Route × role rule matrix; finds unenforced rules, duplicate routes, dead ends, guest leaks | `docs/quality/website-ready/route-rules.md`, findings G-### |
| FUNC: Function tester | high | "agent for the function", R3 | Journey matrix per persona; runs and writes browser journeys; golden end-to-end journey | `docs/quality/website-ready/journeys.md`, new `verify-*-journey-browser.mjs`, findings F-### |
| UX: UI/UX auditor | high | "agent for the UI and UX", R4 | Writes the UI standard; audits every page at 1280 px and 390 px | `docs/quality/website-ready/ui-standard.md`, findings U-### |
| FIX-RULES: Rules fixer | high | "agent for the fix", R1 | Guest sample products (backend and staff page), API rule enforcement | backend modules and tests, `frontend/src/modules/catalog` |
| FIX-FLOW: Flow and function fixer | high | "agent for the fix", R2, R3 | Canonical routes, redirects, guards, navigation, frontend function bugs | `frontend/src/app`, `identity`, `ordering`, store frame |
| UI-SYSTEM then UI-PAGES: UI builder | high | R4 | Shared layout primitives and tokens first, then page groups A, B, C | `frontend/src/components/workspace-kit`, page modules' CSS and markup |
| QA: Independent verifier | high | R3, R4 | Re-checks every closed finding, runs regressions through ORCH, final whole-branch review | `docs/quality/website-ready/verification.md` |

## Global Constraints

- Every agent: model Opus 5.5; ORCH effort xhigh, all others high.
- Only ORCH commits, merges and pushes. Stage explicit paths only; never `git add -A` or `git add docs`. `docs/proposal-completion-plan.md` stays untracked.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Heavy jobs run one at a time, all through ORCH: `dotnet build`/`test`, `next build`, browser rehearsals, the full frontend suite. Agents take the lock with `mkdir tmp/website-ready/heavy.lock` and remove it when done. Light work runs freely: typecheck, eslint, one unit test file.
- Resource budget: at most 3 implementation agents at once, at most 2 `next dev` servers, and backend builds only in the FIX-RULES worktree.
- Never weaken the 60 s transaction deadline, the 30 s browser bound or the 5 s evidence lease policy. Never disable the rate limiter in tests; pace instead (`paceApi` pattern).
- Never type passwords on the `home.arpa` pilot. Signed-in testing runs on the loopback review stack with synthetic sessions minted into its own database, never printed in chat.
- Never change system security settings or folder ACLs. Never drop Mongo test databases. Never delete `tmp/production` evidence or the release1 `.next` build. Ask before any download.
- Guests never see prices, private photos, other organisations' data or staff pages. Maker/checker and proposer privacy (`ownProposal`) stay intact.
- Copy is in Bahasa Indonesia (EYD V): `Rp1.250.000`, `21 Okt 2026`, times with WIB, typed record numbers (`PSN-`, `KLH-`, `RTR-`, `PP-`, `SP-`).
- Each fix names its finding ID and starts with a test that fails for the reason in the finding.

## Review Focus

1. A guest opens a non-sample product directly (`/katalog/<publicId>`, `/shop/<publicId>`, or the API). Expect "not available", with no name, price or photo leaked. Test: Task 3.1, Step 1.
2. A session expires mid-cart, mid-checkout or mid-staff-form. Expect sign-in, then a return to the same page with saved work intact. Test: Task 3.3, Step 1.
3. A staff member who also buys for a branch. Expect one home, both doors visible, and no page offered that their grants refuse. Test: Task 3.3, Step 1.
4. A staff table or form on a 390 px phone. Expect no horizontal page scroll and every action reachable. Test: Task 4.2 screenshot check.
5. A sample product is unpublished, its photo is hidden, or its assortment changes. Expect it to leave the guest catalog immediately and never fall back to the private photo. Test: Task 3.1, Step 1.

---

## Phase 0: Set up (ORCH, ENV)

### Task 0.1: Close in-flight work and freeze a baseline (ORCH)

- [ ] Build the test project. Rerun `PaymentSupplementEvidenceTests|PaymentSupplementTests|RefundSettlementEvidenceTests`. Expected: payment supplement green. A refund-settlement timeout is analysed from its timings, never fixed by raising the lease.
- [ ] Run the full frontend suite, then the browser rehearsals touched by the typed numbers: refund payout and settlement, return financial approval, return lot, order timeline, invoice, buyer identifiers, op-scan receiving, procurement requests, purchase arrivals. Expected: all pass.
- [ ] Commit the typed numbers and fixture fix as two commits, push, and tag `website-ready-base`.

### Task 0.2: Coordination scaffold (ORCH)

- [ ] Create `docs/quality/website-ready/findings.md` with one row per finding: `ID | type (rule, flow, function, ui) | persona | page | expected | actual | evidence path | severity P0–P3 | owner | status | fix commit`.
- [ ] Create the ledger `docs/quality/website-ready/ledger.md`, and `tmp/website-ready/` for screenshots, logs and the heavy lock.
- [ ] Write `tmp/website-ready/new-worktree.ps1`. It creates a worktree on branch `wr/<agent>`, junctions `frontend/node_modules` and `.tools` into it, and prints the path.

### Task 0.3: Review stack with every persona (ENV)

**Files:** `backend/tools/UnitToko.LocalPilot/PilotState.cs`, `PilotBootstrap.cs`, `start-local-pilot.ps1` (new profile `review`), new `PilotSessions.cs`.

**Interfaces:**
- Produces: `start-local-pilot.ps1 -Profile review`, which serves loopback-only `https://localhost:8443` from its own database and writes `tmp/website-ready/sessions/<persona>.json` (Playwright storage state).
- Personas: `tamu` (no session), `pemohon` (buyer), `penyetuju-cabang` (spending approver), `peninjau-toko` (store acceptance), `petugas-katalog`, `peninjau-katalog` (price and public-catalog checker), `petugas-gudang` (preparation), `petugas-kirim`, `kurir`, `keuangan-tagihan`, `pemeriksa-pembayaran`, `pengadaan`, `penyetuju-pengadaan`, `admin-akses`, `staf-pembeli` (staff who also buys).

- [ ] Write the failing xUnit test `Review_profile_refuses_non_loopback_and_mints_one_session_per_persona`. It asserts the profile refuses any bind address other than 127.0.0.1 or ::1, that each persona's grants match its role template, and that no session file is written outside `tmp/website-ready/sessions`.
- [ ] Implement it. Grants come from the existing role templates, and sessions are inserted the way the browser fixtures insert them. Run the test; expected PASS.
- [ ] Seed through real services: the 200-photo catalog, prices, and 12 approved sample products (after Task 3.1 lands).
- [ ] Under D3, the same personas join the `full` pilot profile, with credentials written only to `ACCOUNTS-PRIVATE.txt`.

### Task 0.4: Screenshot and accessibility harness (ENV)

**Files:** create `frontend/scripts/review/capture.mjs` and `frontend/scripts/review/routes.json`.

- [ ] `capture.mjs --persona <name> --routes <group> --run <label>` writes `tmp/website-ready/screens/<label>/<persona>/<route>@{1280,390}.png`, plus `axe.json` and `metrics.json`. The metrics record page scroll width, the heights of every button, input and select, and the heading outline.
- [ ] `routes.json` lists all 81 routes with the personas that should reach each one, following GAP's matrix once it exists.
- [ ] Run a `before` capture of every route for every persona. ORCH runs it as one heavy job.

## Phase 1: Audit (parallel, read-only: GAP, FUNC, UX)

### Task 1.1: Rules and flow audit (GAP)

- [ ] For each of the 81 routes, record: intended audience (navigation entry plus permission), the actual frontend guard, the API permissions of every endpoint the page calls, and whether it is canonical, a duplicate or a redirect. Output: `route-rules.md`.
- [ ] Probe every route as every persona and as `tamu` through the harness, directly by URL and through navigation. Record any page a persona reaches but should not, any page it should reach but cannot, dead ends with no way back or forward, and wording that differs for the same state.
- [ ] Check the guest rule end to end: catalog, product page, search, categories, cart, checkout, orders and API responses. Confirm no price, private photo or non-sample product is exposed.
- [ ] Record every gap as G-### in the register, with evidence. Expected: G entries for K1 and K2 at minimum.

### Task 1.2: Function test matrix (FUNC)

- [ ] Build `journeys.md`: for each persona, every function its navigation offers, the existing rehearsal that covers it (map the ~60 `verify-*-browser.mjs` scripts), and status `covered`, `uncovered` or `failing`.
- [ ] Ask ORCH to queue every existing rehearsal (one heavy job, about 4 hours, run while the auditors work). Record each failure as F-### with its log.
- [ ] Write `frontend/scripts/verify-golden-journey-browser.mjs` and its xUnit driver `GoldenJourneyBrowserTests`. It runs, on one stack:
  - order to cash: guest browses samples; pemohon signs in, adds to the cart and checks out; penyetuju-cabang approves; peninjau-toko accepts; gudang prepares; kirim dispatches with a kurir; pemohon receives; keuangan issues the invoice; pemohon pays; pemeriksa verifies;
  - procurement: request, PO, receive, stock.
  It asserts each step's visible result and the record's typed number.
- [ ] Explore every `uncovered` function by hand on the review stack and record F-### findings with screenshots.

### Task 1.3: UI standard and visual audit (UX)

- [ ] Write `ui-standard.md`, with values measured from the current tokens and `reports/Unit Toko UI UX improvements.md`:
  - spacing scale 4, 8, 12, 16, 24, 32, 48;
  - control height 44 px (36 px compact in dense tables);
  - type scale and weights;
  - page header pattern (title, one-line purpose, primary action on the right);
  - section, card, list/table, form-field and button-hierarchy patterns;
  - empty, loading, error and denied states;
  - content widths 720, 1080 and 1280;
  - alignment to a single left edge;
  - mobile rules.
- [ ] Score every `before` screenshot against the standard. Record U-### per issue with page, viewport, rule broken, screenshot crop and severity, grouping repeated issues by shared component.
- [ ] List the 20 most visible "amateur tells" across the site, each with the shared component that causes it.

## Phase 2: Triage (ORCH)

- [ ] Merge duplicate findings, set severity (P0: broken rule or data leak; P1: broken function or dead end; P2: inconsistent flow or visible UI defect; P3: polish) and assign owners and batches.
- [ ] Send the owner a one-page triage summary: counts by type and severity, the top items, and anything that needs a decision. Execution continues unless a decision is needed.

## Phase 3: Fix (worktrees, at most 3 agents at once)

### Task 3.1: Guest sample products (FIX-RULES), with R1 and Review Focus 1 and 5

**Files:**
- Create:
  - `backend/src/UnitToko.Platform/Catalog/CatalogPublicSampleService.cs`;
  - `backend/src/UnitToko.Platform/Mongo/Migrations/CatalogPublicSampleMigration.cs` (Version 122);
  - `backend/src/UnitToko.Api/Catalog/CatalogPublicSampleEndpoints.cs`;
  - `backend/tests/UnitToko.Tests/Catalog/CatalogPublicSampleTests.cs`.
- Modify:
  - `backend/src/UnitToko.Platform/Catalog/CatalogDefinitionPublicQueries.cs` (the filter reads samples, not assortment heads);
  - `frontend/src/modules/catalog/public-showcase-client.ts` (rename to `public-sample-client.ts`);
  - `PublicShowcaseWorkspace.tsx` (becomes `PublicSampleWorkspace.tsx`);
  - `PublicCatalog.tsx` (banner and empty state);
  - `backend/contracts/openapi.json` and the generated client.
- Retire: the assortment showcase endpoints and tests, under `Retired.*` with the ledger in `docs/quality/retired-tests.md`.

**Interfaces:**
- Collections: `catalogPublicSamples` (`_id` = familyId, owner scope, revision, addedAt, approvedProposalId) and `catalogPublicSampleProposals` (adds[], removes[], reason, proposedBy, state, decision).
- `POST /api/v1/catalog/public-samples/query` returns `{ items: [{ familyId, familyName, ownerLabel, packages, isSample, pending: { proposalId, adding, ownProposal } | null }], total }`.
- `POST /api/v1/catalog/public-samples/proposals` takes `{ adds: string[], removes: string[], reason }` with an Idempotency-Key, and requires `catalog.public.propose`.
- `POST /api/v1/catalog/public-samples/proposals/{id}/approve|reject` takes `{ reason }`. It requires `catalog.public.approve` and fresh MFA, and refuses the proposer.
- Public read: a family is visible to guests only if it is a sample **and** currently published **and** its photo has a clean copy (photo optional).

- [ ] **Step 1: Write the failing tests** in `CatalogPublicSampleTests`:
  - `Guest_sees_only_approved_samples_without_prices`: total equals the sample count; no `price` key on any item.
  - `Non_sample_product_by_direct_reference_is_unavailable`: 404 with no name.
  - `Proposer_cannot_approve_own_proposal`: 403 `self_approval`.
  - `Unpublished_sample_disappears_immediately`: the next read excludes it.
  - `Hidden_photo_never_falls_back_to_private_original`.
  - `Migration_122_converts_public_assortments_to_samples_of_their_published_products`.
- [ ] **Step 2:** Run `-Filter FullyQualifiedName~CatalogPublicSampleTests` through ORCH. Expected: FAIL (types missing).
- [ ] **Step 3:** Implement the service, migration, endpoints and query filter. Export OpenAPI with `-Configuration Release -ArtifactsPath tmp/production/fix118-artifacts` and regenerate the client.
- [ ] **Step 4:** Rerun the step 2 filter plus `CatalogPublic|CatalogBuyer|AccessEndpoints`. Expected: PASS.
- [ ] **Step 5:** Frontend:
  - `/catalog/public` becomes "Contoh katalog untuk tamu": a searchable product list with "Tampilkan untuk tamu" toggles collected into one proposal with a reason. The reviewer sees old against new, and "Setujui" is disabled with "Anda yang mengajukan perubahan ini" for the proposer.
  - `/katalog` shows the banner "Contoh barang. Masuk untuk melihat seluruh katalog dan harga cabang Anda."
  - The empty state reads "Contoh katalog belum dipilih" with a sign-in link.
  - Unit tests in `tests/public-sample-*.test.ts` fail first, then pass.

### Task 3.2: API rule enforcement (FIX-RULES)

- [ ] For each backend G-### finding: write a failing HTTP test in the owning module's `*HttpTests.cs` that reproduces the leak or missing guard, fix it in the owning module only, then run that module's batch through ORCH.

### Task 3.3: One clear flow (FIX-FLOW), with R2 and Review Focus 2 and 3

**Files:**
- `frontend/src/app/**/page.tsx` for the duplicate routes;
- `frontend/src/modules/identity/identity-workflow.ts`;
- `workspace-navigation.ts`;
- `frontend/src/components/store/store-navigation.ts`;
- `backend/src/UnitToko.Api/Identity/Auth0Options.cs` (allowed return paths).

**Interfaces:**
- Canonical routes:
  - `/identity/login`;
  - `/identity/account`;
  - `/ordering/orders`;
  - `/ordering/checkout`;
  - `/shop` (signed-in) and `/katalog` (guest).
  `/login`, `/customer/login`, `/staff/login`, `/account`, `/account/orders`, `/checkout` and `/catalog/browse` redirect, keeping `returnPath` and query.

- [ ] **Step 1: Write the failing tests:**
  - `tests/route-canonical.test.ts`: every legacy path maps to its canonical path with query preserved.
  - `tests/return-after-signin.test.ts`: an expired session on `/cart`, `/ordering/checkout?draft=7` and a staff form returns to the same URL after sign-in.
  - `tests/multi-role-home.test.ts`: `staf-pembeli` lands on `/workspace` with a "Belanja untuk cabang" door, and navigation lists only granted pages.
- [ ] **Step 2:** Implement the redirects as Next route handlers or redirects (check the Next 16 docs), the guards, and the single sign-in page per D2. Every page gets a way back (breadcrumb or back link) and one primary next step. Run the tests; expected PASS.
- [ ] **Step 3:** For each frontend G-### finding, write a failing test, fix, and record the commit in the register.

### Task 3.4: Function fixes (FIX-FLOW, with FIX-RULES for backend causes)

- [ ] For each F-### in priority order: reproduce it with the failing rehearsal or a new focused test, fix the cause in its owning module, and rerun the reproduction through ORCH. QA closes it.

### Task 3.5: UI system (UI-SYSTEM), with R4

**Files:**
- Create in `frontend/src/components/workspace-kit/`: `page-header.tsx`, `section.tsx`, `toolbar.tsx`, `field.tsx`, `data-list.tsx`, `data-table.tsx`, `badge.tsx`, `button.tsx`, `layout.module.css`.
- Migrate users of `modules/administration/access.module.css` (`common`) to these primitives.

- [ ] **Step 1:** Write the failing unit tests `tests/workspace-kit-*.test.ts`:
  - `PageHeader` renders one h1, the purpose line and at most one primary action.
  - `Field` ties label, hint and error with `aria-describedby`.
  - `Button` sizes map to 44 px and 36 px classes.
- [ ] **Step 2:** Implement the primitives with the tokens from `ui-standard.md`, then run the tests; expected PASS.
- [ ] **Step 3:** Swap `common.*` usage page group by page group (no visual change yet), then typecheck and lint.

### Task 3.6–3.8: Page polish (UI-PAGES, at most 2 groups at once)

- [ ] 3.6, group A (storefront and buyer):
  - `/shop`, `/shop/[product]`, `/katalog`, `/cart`, `/ordering/checkout`, `/ordering/orders`, `/ordering/requests`, `/identity/account`, `/identity/login`;
  - the landing page only for U-### defects (its design stays).
- [ ] 3.7, group B (catalog, ordering desk, fulfilment, inventory pages).
- [ ] 3.8, group C (billing, procurement, administration, accounting, reporting, operations).
- [ ] For every group: fix its U-### findings with the primitives, capture `after` screenshots for the group, and attach before/after pairs to each finding. Expected: no page scroll wider than the viewport, every control at the standard height, axe with no serious or critical issues.

## Phase 4: Verify (QA, ORCH)

### Task 4.1: Close findings (QA, per merged batch)

- [ ] Rerun each finding's reproduction on the review stack. Mark it `closed` with evidence or `reopened` with the new symptom. P0 and P1 can only be closed by QA.

### Task 4.2: Full regression (ORCH heavy queue)

- [ ] Run, one at a time:
  - the full frontend suite;
  - the backend batches (Catalog, Access, Ordering, Fulfillment, Inventory, Procurement, Billing, Documents, Mongo);
  - every browser rehearsal;
  - `GoldenJourneyBrowserTests`;
  - an `after` capture of all routes for all personas.
- [ ] Expected: all green or documented under `Retired.*` or `Pending.*`, every page passing the `metrics.json` checks, and axe with no serious or critical issues.

### Task 4.3: Final review (QA)

- [ ] Do a whole-branch review from `website-ready-base` to HEAD with superpowers' `code-reviewer.md`, given this plan's Review Focus. ORCH does one fix pass for Critical and Important findings, each test-first. Minor findings are listed for the owner.

### Task 4.4: Ship and hand over (ORCH)

- [ ] Redeploy the pilot with samples and personas (D3), probe every page, and push.
- [ ] Publish a before/after report page and a walkthrough: which account to use for each function, and what to expect.

## Definition of done

- R1: guests see only approved sample products, never prices; every buy, cart and order action leads to sign-in and back; all Review Focus tests pass.
- R2: one canonical URL per page; legacy URLs redirect; no dead ends; each persona's navigation matches its grants exactly.
- R3: every journey in `journeys.md` is covered and green, including the golden journey; no P0 or P1 open.
- R4: every page meets `ui-standard.md` at 1280 px and 390 px; before/after evidence exists for every U-### finding.
- Everything is committed and pushed, and the pilot is redeployed. The owner has a walkthrough and the list of deferred minor items.
