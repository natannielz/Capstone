# Accelerated Unit Toko Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan natively, task by task. Steps use checkbox syntax. Resume accepted implementation; do not restart planning, repeat the completed three-agent audit, or require a new agent/review conversation for every change.

**Goal:** Complete the original production goal, meeting-image/manual outcomes and all 97 report requirements, integrate reviewed work into main early, and deliver the verified site through GitHub, Vercel and its actual backend services.

**Architecture:** Preserve the existing Next.js frontend, C#/.NET modular backend and worker, MongoDB transactions, Auth0 boundary and private evidence storage. Integrate complete user workflows over these foundations. Keep one authoritative development checkout and one writer for each stock/money domain; switching source branches is separate from switching real production data.

**Tech Stack:** Existing pinned Next.js/React/TypeScript, C#/.NET 10, MongoDB replica set, Auth0, private object storage; Vercel frontend plus a suitable approved API/worker host. This plan does not introduce a Go rewrite, microservices, or a new framework.

**Spec:** [Original production specification](../../requirements/unit-toko-production.md), [97-item report](../../requirements/unit-toko-report-2026-09-28.md), [three-agent audit](../../quality/production-readiness-2026-09-27.md), and [business decision ledger](../../quality/report-decisions.md).

**Prepared:** 29 September 2026, from read-only source and main-task review. This document changes execution order and verification cadence; it does not claim any merge, deployment, acceptance, or implementation occurred in this side conversation.

## 1. Authority, preserved scope and starting evidence

The user should stop the old executor before handing this plan to the main task. Resume the existing goal; do not start a competing executor. User instructions outrank descriptions in uploaded documents. Manuals describe supported business work, not permission to contact vendors, send messages, transfer money, purchase services or operate external systems.

This is a replacement delivery sequence, not another specification rewrite. Keep the original task plans as implementation references: [original T00–T16 plan](2026-09-27-unit-toko-production.md), [report completion](2026-09-28-report-completion.md), [access](2026-09-28-report-access.md), [payment/OCR](2026-09-28-report-payments-ocr.md), [catalog/packaging](2026-09-28-report-catalog-packaging.md), and [current pricing work](2026-09-29-catalog-pricing.md). Existing source-control and inventory plans retain their required invariants. Resolve sequencing conflicts using this plan; do not weaken accepted integrity controls.

The three-agent audit is already recorded as completed on 27 September. Reuse its findings. A fresh broad audit or a new plan for every validator would consume time without completing another customer journey.

| Observation at review | Consequence |
| --- | --- |
| Main development checkout: [production worktree](C:/Users/User/.codex/worktrees/unit-toko-production/Capstone), branch codex/unit-toko-production; reviewed committed checkpoint c6a5b4ecf282ca9a71a794dd4d1673e51ea69875 | Re-inventory after stopping the old executor. Newer commits or unfinished work may exist. Do not hardcode a reset to this checkpoint. |
| Original [workspace](C:/Users/User/Documents/Capstone) is on main, observed at 09265cc65b47f26ce3bec8c652fde380dcdcce86 | It contains separate edits and untracked documents; preserve these before integrating the renamed layout. |
| Both checkouts contain local edits; pricing persistence is being developed | Preserve in-flight code separately if it is not yet a coherent, verified checkpoint. Resume it in M1. |
| Six role templates, identity/access administration, Mongo foundations, catalog authoring and substantial stock/source safeguards exist | Extend accepted implementation; do not rebuild these modules from scratch. |
| Checkout, legacy login and state/catalog routes still use backend/legacy; new ordering, procurement and billing consumers are incomplete | The highest-value next output is a real browser order through the new API and MongoDB, not another isolated foundation-only milestone. |
| A recorded pricing-focused run passed 231 tests; this is not whole-system acceptance | Reuse valid evidence for unchanged code; identify its scope and source revision. Never equate a test count with percentage finished. |
| Full backend CI can take about an hour; repeated waits and queued checkpoints consumed main-task time | Change cadence in M0. Continue useful implementation while necessary hosted checks run. |
| Worker currently reports that no outbox processors are registered | Real notification/OCR processing must be implemented; worker scaffolding is not completion. |
| Actual Auth0 tenant acceptance and Vercel owning-account access were not demonstrated | Build locally and stage integrations, then resolve these specific gates at M6. A ZIP or GitHub push is not deployment evidence. |

## 2. Global constraints

- First launch: BNI branches and approved business accounts. Open public signup is disabled. November dates are reference dates; readiness controls release.
- Retain three main directories: [frontend](C:/Users/User/Documents/Capstone/frontend), [backend](C:/Users/User/Documents/Capstone/backend), [docs](C:/Users/User/Documents/Capstone/docs). Root metadata, CI, README and platform entry points stay where their tools require them.
- Six templates: Buyer/PIC, Staf Toko/Kasir, Kepala Toko, Kurir, Finance, System Admin. Finance packs remain separately assigned. Consolidating names never merges incompatible approval powers.
- Business authorization belongs to the application. Provider email verification/MFA does not approve branch membership. Scope lists, counts, detail, search, commands, files, exports and events consistently.
- Preserve original-maker and verified-person independence across account/role changes. Recheck actor, scope, grants, session and applicable fresh MFA in the final transaction. Technical administration grants no automatic financial powers.
- Default proposed session limits remain 30-minute idle and 8-hour absolute, with 5-minute fresh MFA for relevant sensitive actions. Confirm actual provider support and policy before live activation.
- Internal IDs are Int64 with lossless JSON strings; catalog public UUIDv4 and legitimate GTIN/SKU/source codes have separate meanings. Never infer packaging or family membership from names or fabricate GTINs.
- Money uses the existing checked decimal contract and BSON Decimal128, serialized as decimal strings. Nonnegative money inputs/document totals are at most 999999999999999.99 with two fractional digits; signed journals have the same absolute limit. Integer quantity is at most 1000000 per line; percentages are 0–100 with at most four fractional digits. Checked totals still obey the money limit.
- Stock movements, orders, cash, journals, audit, operation receipts and outbox effects commit through existing owner interfaces and a shared transaction where required. No direct cross-module collection writes, duplicate canonical writers or browser-authoritative balances.
- Applied migrations are immutable. Discover the next free migration version at execution time; version 19 was the observed current version, not a permanent assumption.
- Unknown source obligations, stale authority or absent adapters are not zero obligations. Production stock activation remains closed until the required operational readers and reconciliation are complete.
- Historical accepted package, price, recipient, quantity, evidence and issued-document versions remain immutable. Corrections create linked versions/reversals.
- Upload/OCR never verifies payment or posts usable cash. Actual independent bank review is required. Recording a refund payout does not execute a bank transfer.
- Keep production free of demo accounts, stock and balances. Isolated synthetic local fixtures are allowed and labelled; they never count as live provider/source/business acceptance.
- User-facing text is consistent Indonesian, IDR and Jakarta business time. Current local Next.js documentation and applicable AGENTS.md govern frontend changes.
- Preserve existing security and transactional regressions. Optimize test frequency and setup cost, not assertions or required business controls.

## 3. Delivery and verification cadence

### A. Daily implementation loop

1. Work on one coherent user-facing slice inside the current milestone. Reuse existing contracts and detailed plans. Read only the dependency files needed for that slice.
2. Add or update meaningful tests for changed permissions, exact totals, source transactions, retries and state transitions. Avoid tests that merely repeat reversible UI implementation.
3. Connect backend, generated contract, screen and actual Mongo-backed flow before calling the feature implemented. A DTO, permission or isolated endpoint alone is an intermediate checkpoint.
4. Run the smallest meaningful local checks. Review the diff, commit a coherent checkpoint and push it to the agreed main workflow. Do not wait for unrelated previous full CI runs before starting independent work.
5. Update the existing progress ledger once per coherent checkpoint with what users can now do, what was checked and what remains. No separate planning document for every helper, repeated source-only ZIP, repeated full test inventory or broad refactor.
6. If a local obstacle prevents the slice, record the concrete dependency and continue another ready slice in the same milestone. Ask only for unavailable business decisions/credentials/access at the point they matter, with a concrete result ready to review.

### B. Three levels of checking

| Level | When | What |
| --- | --- | --- |
| Focused local | During implementation | Compile changed backend; affected domain/real-Mongo tests; frontend typecheck/lint for frontend edits; contract checks for API changes; actual browser happy path plus denied/retry path for the delivered feature. |
| Integrated checkpoint | At completion of M1–M5 | Connected role journeys for the milestone, affected concurrency/rollback tests, current frontend production build when relevant; review remaining limitations. These runs need not include every unrelated module. |
| Full release validation | Once the website workflows are integrated at M6, and again if material fixes change that release candidate | All existing suites, real browser/API/Mongo tests, genuine provider and deployment checks, scans, migration/restore rehearsal, load/accessibility checks and pilot evidence. All evidence must identify the exact release revision/environment. |

A documentation-only change does not justify a full build. A changed payment allocation does justify its real-Mongo race and independent-verifier tests. A shared authorization change justifies the affected cross-module security regressions.

Existing usable local commands after M0, run from the authoritative root:

```powershell
& .\.tools\dotnet10\dotnet.exe build .\backend\UnitToko.slnx --nologo
& .\.tools\dotnet10\dotnet.exe test .\backend\tests\UnitToko.Tests\UnitToko.Tests.csproj --no-restore --filter 'FullyQualifiedName~CatalogPrice'
npm --prefix frontend run typecheck
npm --prefix frontend run lint
npm --prefix frontend run build
npm --prefix frontend test
& .\backend\contracts\export-openapi.ps1 -Check
npm --prefix backend/contracts run check
```

The CatalogPrice filter is an example for the current pricing slice, not coverage for all modules. Use exact existing class names for each later slice and verify a nonzero expected test count. The frontend test command is currently a custom full harness; do not invent a filter or nonexistent test:e2e script. Use the actual browser harness after reading its configuration. Tests use an isolated local replica set, never production data. Reuse the already-installed pinned SDK/runtime; do not download them afresh per checkpoint.

### C. Hosted CI changes in M0

Inspect current branch protection and required status names before modifying workflows. Keep every existing full-suite job available. Move lengthy whole-system validation to an explicit release workflow/manual dispatch once the application is integrated; retain fast compilation/contracts/security-sensitive checks on meaningful source pushes where compatible with existing rules. Use path-aware jobs that report honest outcomes. Do not disguise skipped validation as a successful full regression.

If the initial merge requires the existing full checks, let those run once on the correct merge candidate. Do not weaken branch protection, rename a trivial job to satisfy a required check, or merge with a known failing required status. Report only an actual protection/access blocker.

Keep at most one useful full regression for a candidate; cancel only verified obsolete task-owned runs, and stop repeated polling of unchanged runs. Continue independent work during a necessary run. Reuse dependency caches and unchanged build outputs safely. Measure expensive Mongo fixture setup before optimizing it; retain full-chain migration tests, isolate databases and do not parallelize shared collections blindly.

GitHub documents [manual workflows](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow) and [required-check behavior](https://docs.github.com/en/pull-requests/how-tos/merge-and-close-pull-requests/troubleshooting-required-status-checks). A skipped workflow can leave a required check pending; a manually dispatched run is not automatically a substitute for the required PR event/check.

## 4. File ownership and interfaces

Paths below describe the unified repository after M0. Existing accepted names win; do not rename working modules merely to match a proposed new filename.

| Area | Existing files/directories to extend | Required boundary |
| --- | --- | --- |
| Identity and scope | backend/src/UnitToko.Modules/Identity; backend/src/UnitToko.Platform/Authorization; backend/src/UnitToko.Platform/Identity; frontend/src/modules/identity; frontend/src/app/administration | Existing actor/person/scope/session checks, explicit permissions, authorization fences and action-specific operation recovery. |
| Catalog and ordering | backend/src/UnitToko.Modules/Catalog; backend/src/UnitToko.Platform/Catalog; backend/src/UnitToko.Api/Catalog; frontend/src/modules/catalog; frontend/src/components/shop; new Ordering owner directories under Modules/Platform/Api and frontend/src/modules/ordering | Catalog supplies immutable SKU/definition/approved price facts; Ordering owns quote/order/approval/substitution and invokes Inventory. |
| Inventory | backend/src/UnitToko.Platform/Inventory/InventoryLedger.cs; existing inventory/source/count modules and frontend/src/app/inventory | Inventory owns stock effects. Business adapters supply real source facts and obligation lifecycles, never a fake zero-reader. |
| Fulfillment and Billing | New owner directories under Modules/Platform/Api for Fulfillment and Billing; frontend/src/modules/fulfillment and finance | Fulfillment owns shipment/recipient acceptance; Billing consumes eligible accepted lines and owns invoices, receipts, allocation and credits. |
| Procurement | New Procurement owner directories under Modules/Platform/Api; frontend/src/modules/procurement | Owns PO/inspection/SPH/claims/advances; invokes inventory and journal interfaces atomically. |
| Accounting | backend/src/UnitToko.Platform/Accounting/PostJournal.cs; existing Accounting domain/platform; frontend/src/modules/finance | Balanced immutable postings, unique source events, independent close, reversals and source reconciliation. |
| Evidence and worker | backend/src/UnitToko.Platform/Documents/EvidenceService.cs and its Upload/Transitions files; backend/src/UnitToko.Worker/Program.cs; new owner-specific worker processors | Evidence eligibility before review; durable scoped worker leases, idempotent receipts and outbox; no OCR financial authority. |
| API composition/contracts | backend/src/UnitToko.Api/Program.cs; backend/contracts; frontend/src/modules/contracts | Existing generated OpenAPI/client conventions, exact numeric strings and typed errors/recovery. |
| Quality/release | backend/tests/UnitToko.Tests; frontend/tests and actual browser harness; .github/workflows/quality.yml; backend/deploy; docs/runbooks; docs/quality/production-progress.md | Add tests beside the owner. One progress ledger; release evidence tied to source and environment. |

New modules should follow the neighboring existing command/transaction patterns. Their detailed method signatures are implemented with generated contracts inside the slice; do not invent duplicate abstractions in advance. Keep public business operations in their owner and avoid routing everything through a new giant universal command switch.

## 5. Milestones

### M0 — Preserve work, merge into main and remove execution overhead

**Files:** Both checkouts, their Git state, .github/workflows/quality.yml, README, docs/quality/production-progress.md; preserve this plan and the prompt.
**Consumes:** Current local edits, remote branch state, existing tests and branch rules.
**Produces:** Verified integrated main, one authoritative checkout, preserved WIP, safe CI cadence and one accurate progress ledger.

- [x] Confirm the previous executor has stopped. Inventory tracked and untracked changes, branch ancestry, local/remote commits, actual PR if one exists, task-owned processes and Vercel auto-deploy linkage. Do not assume the user-described branch is already an open PR.
- [x] Create recoverable local checkpoints/backups with a manifest for BOTH checkouts, including untracked files and the new plan. Keep secrets/private uploads out of commits and public backups. Incomplete pricing changes can remain a separate recoverable WIP checkpoint; never discard them or label them tested.
- [x] Reconcile original .gitignore and layout edits with the renamed branch. Map website/app/layout.tsx to frontend/src/app/layout.tsx and website/public/mentor-mockup.html to frontend/public/mentor-mockup.html. Compare docs/mentor-mockup and older untracked requirements/plans before merging; do not overwrite newer accepted branch documents with old copies.
- [ ] Identify the latest coherent reviewed checkpoint. Fetch remote state; fast-forward main only if ancestry permits, otherwise perform a normal merge and resolve conflicts preserving both sides. Run required initial merge checks and focused checks for conflicts. Push main without force. If protection requires a PR, merge the actual appropriate PR and verify remote main contains the intended changes.
- [x] Reconcile the resulting root checkout and safely restore useful WIP there. Use C:/Users/User/Documents/Capstone as the authoritative checkout on main, subject to actual repository rules. If rules require a short branch, merge each coherent slice back promptly instead of accumulating another long-lived branch.
- [x] Apply section 3 CI cadence without bypassing required statuses. Review test setup timings only far enough to eliminate measured repeat costs.
- [x] Remove only verified obsolete, clean, fully integrated task branches/worktrees after backups, remote verification and checking no process uses them. Resolve absolute Windows paths before any removal. Do not run broad clean/reset/delete commands or erase tool caches, databases, evidence, manuals, unreviewed edits or test results to make the tree look tidy.
- [x] Replace stale progress claims with observed states. Record accepted foundations, partial pricing, remaining business workflows and real external gates. Maintain the 97-row register; no resetting everything to “not started.”

**Acceptance:** Remote main contains the intended integrated checkpoint; root runs the preserved application; no unique edit is lost; WIP recovery is demonstrated; one checkout is authoritative. No real stock/money/authentication writer switches merely because source merged. Inspect auto-deploy behavior so an unaccepted backend/provider or migration is not activated accidentally.

### M1 — Real catalog and branch ordering through the new backend

**Files:** Catalog pricing files already in progress; existing catalog authoring; new Ordering owner module/API/platform; frontend shop/cart/checkout and ordering module; generated contracts; owner tests.
**Consumes:** Approved-account identity, organizations, scoped catalog entitlement, price versions, InventoryLedger, Mongo command receipts.
**Produces:** A branch buyer can deliberately choose packaging, receive an authoritative quote and submit an order stored by the new backend.

- [ ] Finish and verify the current pricing work across persistence, forward migration, API/recovery and staff price desk. Preserve independent proposal/approval, missing-vs-zero price and immutable SKU/definition/head bindings. Reuse its existing detailed plan.
- [ ] Complete editable families/categories/attributes/descriptions/images, legitimate package-level identifiers, explicit relationships and source mappings. Validate GTIN length/check digit/leading zeros without claiming authenticity. Enforce immutable package meaning, code uniqueness/retirement and per-SKU minimum/increment distinct from replenishment minimum.
- [ ] Require explicit packaging selection from quick-add, search, product detail, Buy Now and staff entry. Display unit basis, exact contents, price and availability. Modal dismissal adds nothing and restores focus. Persist accepted package/price snapshots through downstream documents; changed price/package requires fresh buyer review.
- [ ] Implement scoped quotes, safe drafts, checkout, source-separated OMI/S-Mart suborders, buyer spending approval distinct from store acceptance, consented substitutions, reservation/release, partial fulfillment and cancellation of remainder. Implement applicable budgets/cost centers/thresholds/credit holds under recorded D03/D05 decisions; pending production decisions remain gated.
- [ ] Register genuine ordering/reservation source and obligation adapters as these consumers become real. Do not spend another cycle finishing every future source-governance screen before building the order flow. For local integrated demonstrations use isolated labelled fixtures; production source activation still requires M4 completeness.
- [ ] Replace the relevant frontend legacy calls with typed new-backend clients within the isolated new-system environment. Keep legacy and new production writes coordinated until M6; no unsynchronized dual-write bridge. Correct still-active legacy exhausted-retry behavior to distinguish a known conflict from an unknown commit.
- [ ] Add connected browser tests and relevant Mongo tests, then commit/push the complete slice.

**Acceptance examples:** A carton of 12 at its reviewed price produces exactly 12 base units; a distinct “Dus” of 10 stays 10. No default first variant becomes consent. A quote stale after price approval requires review. Two buyers competing for the last available stock cannot both reserve it. Different-branch users cannot retrieve the order through list/detail/file/export/event paths. A lost submit response resolves the original receipt and creates one order. Ordinary membership grants neither branch-wide order visibility nor store acceptance. Scoped buyer catalog reads do not expose staff price-history/margin authority.

### M2 — Pick, deliver, prove receipt and issue timely invoices

**Files:** New Fulfillment/Billing owners and API endpoints, private Documents extensions, courier/buyer/finance screens, owner tests.
**Consumes:** Accepted order/suborder snapshots, inventory reservations, scoped recipient membership, existing evidence/audit/transaction foundations.
**Produces:** Accepted actual delivery lines, immutable Surat Jalan and invoice sources, invoice-ready work queue and separate payment/aging state.

- [ ] Implement scan-assisted pick/pack, courier assignment, dispatch, attempts/failure, partial/wrong delivery and returned goods. Unknown/ambiguous barcode stops with a resolution path; never guess a package.
- [ ] Issue immutable Surat Jalan from actual dispatched quantities. Implement authenticated recipient-bound tablet/QR consent: single use, hashed challenge, default 15-minute expiry, shipment/document revision binding and explicit accepted/exception quantities. Courier cannot accept for a recipient; scanning alone is not acceptance. Do not trust an unverified courier-supplied contact for identity.
- [ ] Support mobile/tablet draft and offline-unsent states without pretending unsubmitted signatures are accepted. Bind original evidence and document snapshots, allow authorized search/download through the private register.
- [ ] Build invoice-ready selection from eligible accepted quantities exactly once. Apply reviewed grouping, numbering, tax/rounding, due-date origin/calendar and correction/reissue rules. Keep immutable address/quantity/price snapshots and links to external official tax documents.
- [ ] Add unbilled owner/age/escalation and independent aging for unpaid balance. Build durable invoice/document notification processors with lease, retry and delivery/failure records; local tests use controlled recipients/sinks.
- [ ] Commit source adapters and journal effects with business decisions through existing interfaces; connect the real buyer/courier/finance screens and run the focused integrated flow.

**Acceptance examples:** An eight-unit dispatch with six accepted units cannot be invoiced for eight accepted units. Replayed or stale QR proof cannot accept a second shipment/version; an unrelated courier/recipient is denied. Duplicate invoice submission cannot bill a source line twice. Invoice 100000 minus verified payment 40000 minus valid credit 10000 leaves 50000 outstanding and remains overdue after its due date. A worker crash/retry cannot create a second invoice. No document send is claimed delivered without recorded provider evidence.

### M3 — Mandatory payment evidence, local OCR, reconciliation and after-sales

**Files:** Documents upload/eligibility, Billing payment/receipt/allocation/case flows, Fulfillment after-sales, worker OCR processors, finance/buyer evidence UI, real-Mongo and OCR evaluation tests.
**Consumes:** Issued invoices, private object storage, approved receiving-account versions, verified-person authorization, job/outbox and journal foundations.
**Produces:** Reviewed canonical bank receipts and exact allocations, human OCR assistance and independently verified after-sales financial outcomes.

- [ ] Draft payment can exist without an image; review submission requires eligible actual bytes. Implement camera/gallery, preview/rotate/replace/progress/retry while preserving original bytes, hash and evidence version. Quarantine and decoding/scanning limits precede eligibility, including bytes/pixels/pages/time/memory; neither OCR exceptions nor admin convenience bypass security quarantine.
- [ ] Register scoped durable OCR workers. Extract amount/currency/time/account/bank/reference with field positions/confidence/uncertain digits and retain human corrections. Quality flags guide re-upload. Pin runtime/model/license/checksum; bound memory/time/network; evaluate field accuracy, false matches, reviewer effort and latency. Missing data remains unknown.
- [ ] Show typed entry vs OCR vs invoice vs approved receiving account vs actual bank evidence. Bind decision to exact payment/evidence/account revisions. Require a currently authorized independent verified person; role changes or a second account for the same person cannot bypass maker exclusion.
- [ ] Implement image/reference/similarity duplicate warnings, canonical receipt identity, bank statement preview/dedup and concurrent multi-invoice/partial/overpayment/unapplied allocation. Imported bank receipt and customer declaration can reference one transfer without posting cash twice.
- [ ] Support rejection/replacement, safe unreadable/OCR-outage re-upload and permissioned audited manual exceptions. Preserve “Menunggu verifikasi”; upload/high confidence never reduces AR or creates usable cash.
- [ ] Build linked finalized-sale cases, actual inspection/quarantine, replacement, credits and refunds. Requester cannot approve; refund approver cannot record the payout; manual payout evidence requires an independent verifier before paid status. Preserve original history and period rules.
- [ ] Extend the shared OCR pipeline to delivery-reference/quantity suggestions bound to the right shipment/document revision. Keep human receipt consent separate. Add owner/age/discrepancies/notification state and the immutable audit chain.
- [ ] Complete the browser payment→bank-review→allocation and return→refund flows with duplicate/revocation/stale-evidence/race tests; commit/push.

**Acceptance examples:** Missing/quarantined evidence cannot submit. Concurrent evidence replacement invalidates an old review. Same maker through another verified-linked account is denied. One transfer imported and uploaded creates one canonical cash event; concurrent allocation cannot overspend it. Bank-account changes require independent verification through a trusted channel. OCR outage supports the authorized manual path but no automatic settlement. A refund request or uploaded reference is not a completed transfer.

### M4 — Complete procurement, real stock authority and accounting

**Files:** Procurement owner modules/endpoints/UI; existing Inventory source/count/conversion code; Accounting posting/period/reconciliation; OCR supplier adapter; owner tests and import schemas.
**Consumes:** Inventory/journal/evidence interfaces, actual ordering/delivery/return obligations, approved source/finance policies, supplier identities and account versions.
**Produces:** All four procurement routes, complete operational source readers, reconciled OMI/S-Mart activity, supplier settlements and independent period close.

- [ ] Implement OMI regular, DDO, S-Mart and Pasar Kering routes, including partial orders, vendor quotations/negotiated prices/custom logos, funding/advances, emergency buying and transport expense/reimbursement. Do not infer an HET floor/ceiling.
- [ ] Separate container receipt from item inspection and stock putaway. Support private unboxing video under its appropriate larger bounded policy, supplier invoice OCR suggestions, PO/receipt/SPH matching and human discrepancy resolution.
- [ ] Complete supplier claims, unused advance refunds, replacements/cash/credit settlements, deadlines and expiry-specific returns. Independently verify supplier/bank changes. Quarantined/expired/rejected goods cannot silently return to sellable stock.
- [ ] Complete lot/expiry/cost-aware receive/reserve/hold/dispatch/release/count/adjustment and explicit sealed-to-loose conversion. Checked conversion is atomic and idempotent and preserves applicable holds; identical names never supply an implicit ratio.
- [ ] Finish OMI/S-Mart code mappings, authority/freshness/outage policy, opening quantities/cost, staged import/export, acknowledgments, idempotent external reference handling and reconciliation. A web sale exported then imported cannot post stock/revenue twice. Source/channel is configurable per SKU, including water/galon where appropriate.
- [ ] Wire and verify all seven real obligation domains before production activation/control/handoff: uncompleted accepted orders; reservations/external holds; picking/dispatch/delivery; pending receiving; returns/replacements; import/export/reconciliation exceptions and unacknowledged source work; conversions/adjustments/count reviews/unsettled inventory movements. Missing domain/reader/schema or unknown coverage stays closed. Preserve reviewed drain/control/handoff and shared-pool race guarantees.
- [ ] Complete balanced immutable postings, unique source events/reversals, AR/AP/supplier advances/unapplied cash/stock/source reports and reconciliation. Implement submit→approve→close with three distinct verified people, locked period versions and independently approved reopen/correction.
- [ ] Run the integrated procurement→inspection→stock→supplier settlement and source→reconciliation→close flows, including adapter rollback/races, then commit/push.

**Acceptance examples:** Receipt may be partial without accepting unchecked items. Unused advance remains recoverable until actual reviewed settlement. Conversion retains cost/expiry/holds. A new obligation racing activation invalidates the activation transaction. Stale external stock blocks or restricts promises under the approved policy. Duplicate cashier acknowledgments cause one effect. Submitter/approver/closer remain different people after role changes. Closed-period mutation cannot win a concurrent close.

### M5 — Finish professional role workspaces and operations

**Files:** Existing account/person/access administration; module screens/shared UI tokens; worker alerts/notifications; scoped read models/events; operational dashboards and tests.
**Consumes:** Complete business workflows and scoped action/read permissions.
**Produces:** Consistent accessible workspaces, full account lifecycle, actionable queues/timeline, supportable real-time behavior and measured assistance.

- [ ] Finish join/move/leave, dormant-account review, periodic access campaigns, temporary delegation with expiry/reason/handover/revocation/reassignment, named independent admins/checkers/backups, reviewed effective grants and versioned role-template migration. Historical read for suspended branches does not restore order or settlement rights. Provide narrow read-only audit/report grants.
- [ ] Finish consistent Indonesian shell with active account/branch/store and permitted navigation. Use human-readable scoped selectors instead of raw IDs; approval screens show evidence, differences and effects before commitment.
- [ ] Unify work queues, owner/next action/age/reminders/escalation and failed notifications. Add a scoped order timeline across purchase/delivery/invoice/payment/return. Register actual notification processors with durable idempotent delivery; no unauthorized messages to real people during testing.
- [ ] Complete authorized stock/order invalidation plus refetch/reconnect (or bounded polling if topology requires it). Expose stale/offline status; retain safe drafts and original operation keys for unknown outcomes.
- [ ] Add saved/shareable scoped filters, repeat orders, bulk SKU entry, sorting and scan resolution. Complete mobile tables, keyboard/focus/labels/errors/contrast/reduced motion at 360px, tablet and desktop, consistent IDR/Jakarta formatting and shared design tokens.
- [ ] Add actionable audit/security/queue/OCR/database/backup/unbilled/overdue/stock alerts with named owners. Include suspicious successful access as well as failures, with explainable versioned rules and restricted sensitive-log access.
- [ ] Deliver confirmed code/fuzzy matching, explainable price/quantity/adjustment/refund anomaly baselines and lead-time-aware replenishment. Keep model/embedding/forecast extensions conditional on sufficient clean data and measured improvement. Record explicit applicability/evaluation decisions; no silent scope deletion or unvalidated “AI complete” claim.
- [ ] Review each role's complete browser journey and high-value visual regressions, fix inconsistencies and commit/push.

**Acceptance examples:** Delegation expiry/revocation removes access and reassigns outstanding tasks. A suspended-branch historical reader cannot mutate or settle by inference. Shared filters cannot leak another branch. A disconnected courier sees unsent/stale state. Keyboard package/evidence dialogs return focus correctly. Notifications expose failed delivery and responsible owner. Replenishment suggestions never become unattended purchase/payment decisions.

### M6 — Full release validation, GitHub/Vercel deployment and pilot

**Files:** CI/release workflows, backend/deploy and actual Vercel configuration, provider configuration/runbooks, import/reconciliation tools, tests and final progress/acceptance records.
**Consumes:** Integrated M1–M5 application, main candidate revision, D01–D08 business inputs and owner access.
**Produces:** Reproducible deployed release with verified frontend/API/worker/storage/identity, reconciled migration and operational pilot acceptance.

- [ ] Resolve remaining genuine business/provider/hosting inputs using the prepared decision ledger. Verify named operators and actual independently verified identities, Auth0 email/MFA/invite/recovery/step-up, sender/region/entitlement/cost, receiving accounts, catalog/source/finance/retention policies and pilot branch. Unknown policy stays inactive; local implementation continues independently.
- [ ] Validate the actual deployment topology: Vercel frontend, approved .NET API and worker host, MongoDB replica set, private objects and secrets. Test same-origin proxy/cookies/CSRF/body/rate limits, provider redirects/events and revocation across instances. Do not claim Vercel frontend deploys the .NET backend automatically.
- [ ] Run full CI on the exact candidate: preserved legacy regressions, all backend/real-Mongo suites, generated contracts, frontend tests/lint/typecheck/build, actual browser→API→Mongo paths and actual provider staging acceptance. Extend coverage beyond existing stock browser paths to order/delivery/invoice/payment/refund/close. Run dependency/secret scans and record software/model inventory.
- [ ] Exercise duplicate/lost responses, authorization/stock/payment/close races, worker crashes and external acknowledgments. Run future-date/frozen-clock tests. Verify no unresolved critical/high security or money/stock integrity defect.
- [ ] Measure proposed staging targets: 100 concurrent users, 10,000 SKUs, 100,000 historical orders; p95 scoped reads ≤500 ms and ordinary writes ≤1 second excluding provider/upload delay; <1% unexpected errors; authorized online views converge within 5 seconds. Review WCAG 2.2 AA and proposed field LCP ≤2.5s, INP ≤200ms, CLS ≤0.1. Report measured environment and limits; do not invent production measurements.
- [ ] Inventory real vs demo data; dry-run deterministic legacy ID/package/price/document/evidence mappings, rejected-row reports, counts, stock/lots/holds, orders/deliveries, AR/AP/advances/credits/journals and sequence bounds. Do not reinterpret historical package quantities or manufacture missing approvals.
- [ ] Demonstrate restore of MongoDB, private evidence and keys with workers/outbound integrations paused, then reconcile external acknowledgments before resuming. Measure proposed RPO ≤15 minutes/RTO ≤4 hours against the actual approved tier or obtain an explicit revised service objective.
- [ ] Rehearse one-writer cutover for the transactional/authentication cohort: freeze old writers, verified backup/import/reconcile, switch traffic and keep old data read-only. Before new writes, rollback may use the frozen legacy system; after new writes, maintenance plus reconciled recovery/fix-forward is required. Do not switch to stale legacy data.
- [ ] Push the accepted release to GitHub main and deploy the tested frontend to the authorized Vercel project, plus its API/worker migrations and approved infrastructure. Verify commit/deployment IDs, URLs and actual journeys. If access is missing, record the precise access blocker; a prepared archive is not a completed deployment.
- [ ] With approved real cutover timing/operators, run one-branch pilot including order→receipt→invoice→payment→reconciliation, a return/refund and period close with primary/backup staff. Record acceptance, monitoring owners, runbooks and controlled rollout.
- [ ] Reconcile original T00–T16, audit A01–A22 and every report row. Mark the goal complete only when all applicable implementation and required production gates are evidenced. Keep unavailable real acceptance explicitly pending; do not call the site production-ready based on local mocks.

**Acceptance:** No lost work; remote main is the verified source; deployed URLs serve the intended revision; actual provider/API/Mongo/private evidence and worker paths function; all applicable requirements have evidence; real-data and operator acceptance gates are met. Material fixes require affected regression and fresh final evidence for the changed candidate.

## 6. Review focus and where it is tested

1. A mutation commits but the response disappears: M1 submit and M3 allocation receipt recovery must find the original result without a second stock/cash effect.
2. Same person changes roles/accounts during approval: M3/M4 final authorization/person fences reject self-review and stale authority.
3. A carton definition changes after quote, picking or issuance: M1 quote refresh and M2 immutable downstream snapshots preserve the original accepted quantities.
4. A worker retries after crash or restore while the bank/cashier already acknowledged: M2/M3/M4 idempotency and M6 paused-worker reconciliation prevent duplicate external/business effects.
5. A missing reader or concurrent new obligation looks like an empty stock source: M4 denies activation until all genuine producers prove complete coverage and the shared fence still holds.

## 7. Progress reporting and decisions

Use one record per milestone with states: preserved/implemented, connected locally, validated in staging, accepted for production. Record exact demonstration, revision, test outcome and concrete dependency. A written plan, number of files, passing validator or number of commits is not a finished user workflow.

Show progress as “M1 connected locally; M2 in progress; 31/97 rows have local evidence, 4/97 have production acceptance” only when the register actually supports those numbers. Do not publish an unweighted percentage as an objective measure. Cross-cutting rows close only after all affected modules, not the first example.

Collect open D01–D08 inputs once in a concise decision list when relevant. D01 verified staff/person source; D02 catalog/approval/grant rules; D03 commercial policy; D04 source/stock authority; D05 bank/settlement policy; D06 evidence/retention/OCR examples; D07 infrastructure/provider/backup owners and costs; D08 pilot/cutover operators. Do not repeatedly poll an unchanged external blocker or ask again for already-authorized routine coding, checks, merge and publication.

The user has asked for faster completion, not a guaranteed number of hours. Report the next demonstrable result and concrete blockers. No additional cleanup, architecture rewrite or optional model work takes priority over a missing required business journey.

## 8. Original scope coverage

| Original plan | Delivery owner |
| --- | --- |
| T00 baseline / three-agent findings; T01 layout | Preserve completed audit; M0 integration, M6 final reconciliation |
| T02 backend/contracts; T05 Mongo; T05A journal/evidence foundations | Reuse now; extend per M1–M4 owner; M6 full verification |
| T03 identity; T04 organizations/roles | Reuse now, M1 business wiring, M3 independence, M5 lifecycle, M6 actual provider |
| T06 catalog/packaging/inventory | M1 catalog/selection, M4 full inventory/source authority |
| T07 B2B orders | M1 |
| T08 procurement | M4 |
| T09 delivery/proof/documents | M2, M3 OCR assistance |
| T10 invoices/aging | M2; approved live finance policy M6 |
| T11 payment/after-sales/refunds | M3 |
| T12 cashier reconciliation/accounting/close | M4 |
| T13 real-time/notification/monitoring | M2 processors, M5 complete operations |
| T14 professional UI/browser quality | Usable UI in each slice, M5 consistency, M6 full validation |
| T15 migration/restore/deployment; T16 pilot/rollout | M6 |

| Original audit findings | Owning milestones |
| --- | --- |
| A01 demo seeding | M6 production bootstrap/migration; isolate fixtures throughout |
| A02 self-verification, A12 after-sales/refunds | M3 |
| A03 scope, A04 identity, A20 request abuse, A22 role labels | M1/M3/M5, M6 actual topology |
| A05 whole-state loading | M1–M5 scoped owner reads, M6 performance evidence |
| A06 catalog inference | M1/M4 |
| A07 cross-user freshness | M5 |
| A08 source reconciliation | M4 |
| A09 delivery proof | M2 |
| A10 late billing, A11 overdue partial balance, A14 mutable/demo documents | M2, M6 live finance policy |
| A13 supplier settlement, A19 period independence | M4 |
| A15 exhausted retry | M1 |
| A16 numeric totals | M1 ordering, M4 procurement, M6 full boundary evidence |
| A17 clock-sensitive tests, A18 pinned test tools | M0 reuse pinned tools; M6 reproducible full validation |
| A21 evidence scanning/audit | M3/M5 |

## 9. All 97 report requirements

The owner is responsible for implementation and evidence, including the row's existing detailed task references. Acceptance always includes M6 where actual provider, production policy, migration or pilot evidence is required. “Conditional” rows require recorded applicability and their required baseline, not silent removal. Existing partial implementations remain credited; this table does not reset status.

| ID | Required outcome (preserved from report) | Owner | Existing detailed tasks |
| --- | --- | --- | --- |
| P01 | Draft saves without image; review submission fails without an eligible image or with quarantined/missing bytes. | M3 | PAY01/PAY03 |
| P02 | Mobile camera/gallery, preview, rotate, replace, progress and retry preserve original bytes; uncertain upload does not duplicate submission. | M3 | PAY01/PAY05 |
| P03 | Local OCR suggests amount, currency, date/time, recipient/account, bank and reference; missing/uncertain fields stay unknown. | M3 | PAY02 |
| P04 | Review shows field-by-field differences against entry, invoice, approved receiving account and available bank records. | M3 | PAY03/PAY04 |
| P05 | Submitted status is “Menunggu verifikasi”; upload or high OCR confidence changes neither receivable nor usable cash. | M3 | PAY03 |
| P06 | Independent authorized human checks image and actual bank evidence; uploader/recorder cannot self-verify. | M3 | ACC03/PAY03 |
| P07 | Approval binds payment revision and evidence hash/version; replacements conflict with stale review; verified history survives supplements/corrections. | M3 | PAY01/PAY03 |
| P08 | Exact/similar images and reused references flag review without double cash or blocking legitimate one-transfer/many-invoice use. | M3 | PAY02/PAY04 |
| P09 | Unreadable/OCR outage supports re-upload or audited authorized manual exception; no silent approval or quarantine bypass. | M3 | PAY02/PAY03 |
| P10 | Partial, overpayment, multi-invoice, unmatched and unapplied funds reconcile under concurrent allocation. | M3 | PAY04 |
| P11 | Queue exposes owner/age/discrepancies, rejection/replacement request and notification delivery/failure status. | M3 | PAY05/OPS03 |
| P12 | Immutable audit links upload, extracted/corrected values, approval/rejection, allocation, actors, times and versions. | M3 | PAY01–PAY05 |
| V01 | Quick-add, search, detail, Buy Now and staff catalog request package choice when absent. | M1 | CAT03 |
| V02 | Multiple variants never silently choose first, cheapest or available default as buyer consent. | M1 | CAT03 |
| V03 | Selection displays label, contents, size/color, price basis, availability and total base quantity. | M1 | CAT01/CAT03 |
| V04 | Authorized staff configure package labels/contents/images/prices/minimum purchase/increments independently of reorder minimum. | M1 | CAT01 |
| V05 | Two products can both use “Dus” with different exact contents; no global inferred ratio. | M1 | CAT01/CAT02 |
| V06 | Cart through checkout/order/pick/delivery/invoice/return retains explicit package and immutable accepted snapshot. | M1 | CAT03/OPS01 |
| V07 | Server checks current variant/price/availability/versions; material change requires fresh review. | M1 | CAT03/T07 |
| V08 | Changing 12 to 10 never rewrites historic quantities; use a new sellable identity/definition as appropriate. | M1 | CAT01/CAT03 |
| V09 | Explicit sealed-to-loose conversion is atomic, idempotent and preserves lot/expiry/cost/holds. | M4 | CAT02 |
| V10 | Modal supports keyboard/screen reader/mobile; Escape/close cancels, adds nothing and restores focus. | M1 | CAT03/OPS04 |
| I01 | One stable variant identity/SKU per sellable variant; identical physical units share it. | M1 | CAT01 |
| I02 | Public catalog UUIDv4 coexists with internal Long; collision constraints and scope checks still apply. | M1 | CAT01 |
| I03 | Legitimate trade-item GTIN is separate from SKU/UUID and identifies the relevant packaging level. | M1 | CAT01 |
| I04 | Retain legitimate GTINs; authorized allocation follows GS1 ownership rules; absent GTIN is not fabricated. | M1 | CAT01/OPS06 |
| I05 | Validate applicable lengths/check digit; retain leading zeros and canonical comparison; checksum is not authenticity. | M1 | CAT01/CAT04 |
| I06 | Supplier, OMI and S-Mart codes map with explicit source/catalog context and validity. | M1 | CAT01/CAT04 |
| I07 | Database uniqueness and concurrent creation tests; retired codes are not recycled; corrections retain aliases/history. | M1 | CAT01 |
| I08 | Receiving/picking/dispatch/stocktake scans resolve unambiguously or stop with an unknown/ambiguous resolution path. | M4 | CAT04/OPS01 |
| I09 | Explicit family relations replace ID/name inference; unrelated variants cannot merge through naming patterns. | M1 | CAT01 |
| I10 | Repeatable legacy mapping preserves order/stock/document/evidence/accounting references and reports unresolved rows. | M6 | CAT04/OPS06 |
| R01 | Separate buyer spending approval from store operational acceptance in permissions, state and UI. | M1 | ACC02/OPS01 |
| R02 | Staff proposes substitution; authorized buyer consents; proposal never reserves buyer approval powers. | M1 | ACC02/OPS01 |
| R03 | Shared entitled catalog visibility is distinct from buyer-owned order visibility. | M1 | ACC02/CAT01 |
| R04 | Explicit historical-read permission can cover suspended branches without restoring new-order/write authority. | M5 | ACC02 |
| R05 | Lists/counts/details/search/writes/files/exports/events share scoped policy and negative tests. | M6 | ACC02/all modules |
| R06 | Original maker remains disqualified after role/grant changes; current access rechecked at commit. | M3 | ACC03/all approvals |
| R07 | Trusted same-person binding blocks different accounts belonging to the same maker from checking that work. | M3 | ACC03 |
| R08 | Reviewed finance conflict rules warn/reject assignments; bank evidence/margins require narrow read grants. | M3 | ACC03/PAY05 |
| R09 | Explicit conversion/release/cancel/supplier-change/correction/reopen permissions precede corresponding commands. | M4 | ACC02/OPS01/OPS02 |
| R10 | Temporary delegation has reason, expiry, handover, revocation and outstanding-task reassignment. | M5 | ACC04/OPS03 |
| R11 | Join/move/leave, dormant accounts and periodic access-review campaigns create accountable attestations. | M5 | ACC04 |
| R12 | Named independent administrators/checkers/backups/recovery operators are verified before real grants. | M5 | ACC04/OPS06 |
| R13 | Human-readable effective access and versioned template migration preserve reviewed grants deliberately. | M5 | ACC03/ACC04 |
| R14 | Narrow scoped read-only audit/reporting is grantable without administrative mutation powers. | M5 | ACC02 |
| S01 | Actual Auth0 invite, verified email, MFA, sign-in and step-up flow; provider verification does not approve membership. | M6 | ACC01/OPS05 |
| S02 | Recovery verifies owner and completes with provider; local pending intent is never shown as recovered. | M6 | ACC01 |
| S03 | Logout/suspension/factor/password/grant changes revoke across servers and fence in-flight actions. | M6 | ACC01/ACC03/OPS05 |
| S04 | Real proxy/cookies/origin/CSRF/rate/body-limit validation and coordinated retirement of legacy authentication. | M6 | ACC01/OPS05/OPS06 |
| S05 | Private quarantine, content decoding/scanning and size/pixel/page/time/memory limits precede evidence eligibility. | M3 | PAY01 |
| S06 | Originals, thumbnails, OCR text, sensitive fields, exports and keys obey access, retention and log-redaction policy. | M3 | PAY01/PAY02/OPS06 |
| S07 | Supplier/receiving-bank changes are versioned and independently verified through a trusted channel. | M4 | PAY03/OPS02 |
| S08 | OCR worker can read assigned eligible evidence and write suggestions, never verify/allocate/post cash. | M3 | PAY02 |
| S09 | Durable immutable decision audit, restricted reads and actionable audit/security failure and suspicious-access alerts. | M5 | PAY01/OPS03 |
| F01 | Editable persisted families/categories/attributes/images/descriptions/mappings and versioned prices replace demo inference. | M1 | CAT01 |
| F02 | Server-authoritative exact line/document totals obey signed rounding/tax policy and existing numeric limits. | M4 | OPS01/OPS02/T05A |
| F03 | Physical/reserved/available/held/expired quantities, releases and independent counts survive races without overselling. | M4 | CAT02/OPS01 |
| F04 | OMI/S-Mart authority, split orders, imports/acks/deduplication/reconciliation and freshness are explicit before stock promises. | M4 | CAT02/OPS02 |
| F05 | Document applicability of budgets/cost centers/spending thresholds/credit holds; implement and test those required by the business. | M1 | OPS01 |
| F06 | OMI regular, DDO, S-Mart and Pasar Kering routes complete inspection/matching/claims/unused advance settlement. | M4 | OPS02/T08 |
| F07 | Partial payment and overdue are separate; unbilled receipts have owner, aging and escalation. | M2 | OPS02/OPS03/T10 |
| F08 | Statement reconciliation and allocation share verified receipt/evidence workflow and preserve suspense funds. | M3 | PAY04 |
| F09 | Finalized sales accept linked cases, inspection, replacement/credit/refund and independently verified payout. | M3 | OPS02/T11 |
| F10 | Issued documents are immutable; recipient/quantity/version/exception-bound QR or tablet proof is validated. | M2 | OPS01/T09 |
| F11 | Finance signs grouping, due-date origin/calendar, numbering, tax, rounding and correction/reissue rules. | M6 | OPS02/OPS06 |
| F12 | Unified work queue shows owner, next action, age, reminder/escalation and failed notification. | M5 | OPS03 |
| U01 | Consistent shell shows active account/branch/store, pending tasks and only permitted navigation. | M5 | OPS04/ACC01 |
| U02 | Scoped name/code selectors replace raw numeric references for people, organizations, products, invoices and suppliers. | M5 | OPS04/all modules |
| U03 | Approval screens display evidence, reviewed differences and effects before commitment. | M5 | ACC01/PAY05/OPS04 |
| U04 | Loading/empty/denied/validation/offline/unknown-outcome states retain safe drafts and resolve duplicate uncertainty. | M5 | OPS04/all modules |
| U05 | One scoped order timeline links purchase, delivery, invoice, payment, return and responsible actor. | M5 | OPS03/OPS04 |
| U06 | Keyboard/focus/labels/contrast/mobile tables and real MFA work at buyer, courier and staff screen sizes. | M5 | OPS04/OPS05 |
| U07 | Saved/shareable scoped filters, repeat orders, bulk SKU entry and sorting follow core integrity gates. | M5 | OPS04 |
| U08 | Account/session/recovery and business statuses use consistent Indonesian text, IDR presentation and Jakarta business time. | M5 | ACC01/OPS04 |
| Q01 | Enforced module contracts prevent cycles and direct cross-module collection writes. | M6 | OPS05/all modules |
| Q02 | Shared design tokens and bounded global CSS have meaningful visual regression coverage. | M5 | OPS04 |
| Q03 | Generated strict contracts preserve errors, string Long IDs, exact money and server authority. | M6 | OPS05/all modules |
| Q04 | Scoped indexed paginated reads replace whole-state loading/global revision in production modules. | M6 | OPS05/all modules |
| Q05 | Hosted CI and real browser→API→Mongo/provider evidence supplement fixture-based UI tests. | M6 | OPS05 |
| Q06 | Duplicate/lost responses, stock/payment/revocation races, worker crashes and external acknowledgments are exercised. | M6 | OPS05/OPS06 |
| Q07 | No production demo seeds; one writer and reconciled counts/stock/money/docs/ID mappings at cutover. | M6 | OPS06 |
| Q08 | Fix still-active legacy retry exhaustion to return known conflict distinctly from unknown commit; preserve safe operation receipt/retry in the new service. | M1 | OPS05/all modules |
| Q09 | Restore includes Mongo, object evidence, encryption keys and paused workers with external-ack reconciliation. | M6 | OPS06 |
| Q10 | Named responders monitor unbilled/overdue/stock/notification/OCR/queue/database health. | M5 | OPS03/OPS06 |
| Q11 | Trained primary/backup operators complete branch order/payment/return/refund and period-close pilot. | M6 | OPS06 |
| Q12 | Reproducible releases, environment separation, migrations, dependency/security scans and software/model inventory are evidenced. | M6 | OPS05/OPS06/PAY02 |
| M01 | Supplier-invoice extraction suggests PO/receipt match; human resolves differences; never auto-settles. | M4 | PAY02/OPS02 |
| M02 | Delivery extraction suggests reference/quantities tied to the correct shipment/document revision. | M3 | PAY02/OPS01 |
| M03 | Payment OCR is implemented and evaluated as mandatory review assistance, with safe outage handling. | M3 | PAY02/PAY03 |
| M04 | Hash/reference/similarity signals support duplicate review; ambiguous match does not approve or reject money automatically. | M3 | PAY02/PAY04 |
| M05 | Blur/rotation/clipping/type quality flags guide re-upload and retain original evidence. | M3 | PAY02/PAY05 |
| M06 | Code and fuzzy product suggestions require confirmation and never guess contents; embeddings require later measured benefit. | M5 | CAT04/OPS07 |
| M07 | Explainable price/quantity/adjustment/refund anomaly rules first; trained model only after validated improvement. | M5 | OPS03/OPS07 |
| M08 | Lead-time-aware low-stock/replenishment baseline first; forecasting waits for sufficient clean history. | M5 | CAT02/OPS07 |
| M09 | Original image, field positions, confidence, uncertain digits and human correction history remain reviewable. | M3 | PAY02/PAY05 |
| M10 | Pin model/license/checksum/runtime, bound resource/network use and measure field accuracy, false matches, review effort, latency and memory. | M3 | PAY02/OPS05/OPS07 |

**Coverage self-review:** 97 distinct report IDs mapped exactly once, all original T00–T16/T05A tasks and A01–A22 audit findings mapped; five review risks assigned to concrete milestone acceptance tests. Scope and live-acceptance gates are preserved. This is a planning coverage check, not proof of implementation.
