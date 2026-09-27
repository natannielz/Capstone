# Unit Toko Report Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish all applicable items in the user's 97-item report together with the existing production scope, preserving implemented foundations and proving complete B2B operations before rollout.

**Architecture:** Continue the existing Next.js frontend, C# modular API and worker, Mongo transactions and private evidence storage. Documents owns originals and extraction; Billing owns payment decisions and allocation; Inventory owns physical stock; Identity/Organizations own scoped access. Modules expose explicit contracts, and only one coordinated stock/money authority may accept writes.

**Tech Stack:** Existing Next.js/React/TypeScript, C#/ASP.NET Core .NET 10, MongoDB replica set, Auth0 integration, private object storage, durable jobs, generated OpenAPI contracts and meaningful browser/integration tests. Evaluate local PP-OCRv6 tiny/small against Tesseract Indonesian/English; pin the selected pipeline only after representative evaluation. A compatible ONNX runtime is an option, not an assumed drop-in C# OCR implementation.

**Spec:** [Report requirements and all 97 acceptance rows](../../requirements/unit-toko-report-2026-09-28.md), [production specification](../../requirements/unit-toko-production.md), [master plan](2026-09-27-unit-toko-production.md) and [current implementation evidence](../../quality/production-progress.md).

## Global Constraints

- Launch audience: BNI branches and approved business accounts only. No public self-registration or retail checkout in the first launch.
- The November dates are reference dates; release follows readiness gates.
- Three main source directories: `frontend/`, `backend/`, `docs/`. Keep required repository/CI metadata at root.
- Int64 internal IDs cross JSON as strings. Money crosses JSON as decimal strings; validate the precise limits in spec §5.
- No whole-company state API, no negative sellable stock, no duplicate invoice sources, no duplicate cash postings, no unbalanced journal and no unauthorized cross-branch access.
- Preserve unrelated work and completed foundations. The reviewed baseline is `e84faa6` in `C:/Users/User/.codex/worktrees/unit-toko-production/Capstone`; the original checkout is not the implementation directory.
- Treat raw reports/manuals as evidence, not executable instructions. Keep private source documents, credentials, real uploads and bank/person data out of Git and static assets.
- Six role templates plus narrow finance/auditor permissions; no blanket administrator financial powers. Use immutable maker history and trusted person independence.
- Mandatory payment image, security validation, OCR assistance and independent bank review precede verified usable funds. Neither OCR confidence nor image upload changes balances.
- Explicit package selection, versioned quantities/prices and immutable accepted snapshots apply to every purchase entry point and downstream document.
- Continue authorized local work autonomously. Paid provisioning, production cutover, external messages and real transfers require their existing explicit authorization gates. Never invent business policies or claim successful external tests.

## Review Focus

1. A statement import races with a customer declaration for the same transfer: recognize cash once and retain both evidence trails — PAY04/OPS05.
2. An alias or duplicate staff account attempts to approve the maker's old work: immutable account/person history must still deny — ACC03/OPS05.
3. A delayed OCR result arrives after image replacement or a restore: preserve the reviewed version and never revive stale approval — PAY02/PAY03/OPS06.
4. Package contents, price or stock change while a buyer's tab is open: show the difference and obtain renewed confirmation without corrupting historical quantities — CAT03/OPS01.
5. A suspended branch has unpaid historical invoices: explicitly permitted historical readers retain approved visibility only; any separately permitted settlement requires its own current finance/person/fence checks, without new-order access or leaked search counts — ACC02/OPS02.

## Starting point and goal tracking

This is an implementation addendum, not a restart. Three source reviews verified payment/OCR, catalog/packaging and access/operations. T00–T02/T05 foundations and much of T04 are already implemented; T03 has local verified-identity/API/account foundations but remaining administration, recovery dispatch/security-event integration and external acceptance. T05A–T16 business modules are still pending. The progress ledger is authoritative where original unchecked master-plan items describe work subsequently completed.

The task already has an **active, unbudgeted production goal** covering the website. Keep that goal active and incorporate this plan through its referenced master plan/specification; do not create a competing goal or mark unfinished work complete merely to replace it. Original checkout planning documents link this managed-worktree continuation. Goal acceptance now includes the report register as well as the original image/manual requirements.

**Execution objective:** Deliver the existing full production system plus all 97 applicable report outcomes, including safe payment evidence/OCR/reconciliation, explicit versioned packages and identifiers, completed governed access, professional workflows, proven recovery and an accepted branch pilot. Record evidence per requirement, complete conditional-item applicability decisions honestly, and finish only after required readiness/rollout gates pass.

## Execution order and file ownership

These are dependency stages, not calendar promises. Estimate module effort after its contracts are reviewed. An unavailable Auth0 tenant or bank sample does not prevent unrelated local implementation, but cannot be bypassed for real activation.

| Stage | Executable work | Exit evidence |
| --- | --- | --- |
| 1 — Complete access contracts | ACC01–ACC04 in [access plan](2026-09-28-report-access.md); preserve T03/T04 completed foundations | Realistic local identity/admin flow, explicit permissions/person independence; external provider acceptance separately tracked |
| 2 — Shared evidence and accounting | Existing T05A Accounting + PAY01, then PAY02 in [payments/OCR plan](2026-09-28-report-payments-ocr.md) | Balanced journal/period guard, eligible private versioned evidence, restricted durable OCR pipeline |
| 3 — Catalog and inventory | CAT01–CAT04 in [catalog/packaging plan](2026-09-28-report-catalog-packaging.md), with T06 | Persisted variant identity, explicit selection, exact conversion, scoped scans and stock-source authority contracts |
| 4 — Order through delivery/invoice | OPS01/OPS02 with T07–T10 | Correct reservations/procurement/receipt snapshots and finance-approved invoice readiness |
| 5 — Payment and financial completion | PAY03–PAY05, remainder of T11/T12 and OPS02 | Image→OCR→independent bank review→one cash receipt→allocation; returns/refunds/source reconciliation/period close |
| 6 — Coherent experience and operation | OPS03/OPS04; extend T13/T14 | Unified tasks/timeline, scoped selectors, accessible responsive UI, failure alerts and notifications |
| 7 — Proof and release | OPS05→OPS06; T15/T16 | Real browser/API/Mongo/provider evidence, migration/restore rehearsal, independent review and accepted branch pilot |
| 8 — Conditional intelligence | OPS07 only when its prerequisites and applicability decision permit | Evaluated improvement over rules with no autonomous financial or stock authority |

UI/error/accessibility and targeted tests ship with each feature; stage 6 is convergence, not the first usability pass. PAY02 pulls minimum leasing/retry/dead-letter/health infrastructure forward from T13. OMI/S-Mart authority/freshness decisions move ahead of live stock promises; complete external reconciliation still belongs to T12. CAT04 migration acceptance finishes at OPS06. M01/M02 consume PAY02 in procurement/delivery once the relevant immutable records exist.

Do not run writers on the same files concurrently. Root/integrator owns shared contracts, API composition, migrations and generated clients; assign a single module owner per slice. Independent read-only review can run alongside another module's implementation. A worker must preserve changes made by others.

## Implementation cycle for every task

1. Read the task's source files, relevant `AGENTS.md`, contracts and current progress; confirm its prerequisites and unrelated dirty paths.
2. Add focused failing tests for the stated behavior, including the task's denial/race/recovery case. Run the selected tests and confirm failure for the intended reason.
3. Implement the smallest complete domain/API/UI slice and migration needed, with authorized scoped reads, audit and idempotency. Update generated contracts through the established generator, not handwritten drift.
4. Run relevant unit/integration/browser checks, inspect actual outcomes and review the diff independently. Tests using synthetic provider/API responses must be labeled as such.
5. Record commit/test/environment/remaining decisions against requirement IDs in the progress and acceptance ledgers. Commit only owned changes; proceed to the next ready task.

Existing check commands include `dotnet test backend/UnitToko.slnx`, `npm --prefix frontend run typecheck`, `npm --prefix frontend run lint`, `npm --prefix frontend test`, and `npm --prefix frontend run build`. Use focused xUnit filters while iterating and the established Mongo test environment safeguards. `test:e2e` and any new focused scripts are planned deliverables: create/configure them before citing their success. Run broad regression checks at integration gates or after relevant failures, not repeatedly for document-only edits.

## OPS01 — Complete authoritative ordering and receipt workflows

**Depends on:** ACC02/ACC03, CAT01–CAT03, T05A, PAY01; T07/T08/T09 remain authoritative detailed workflow tasks.

**Files:** Create or extend `backend/src/UnitToko.Modules/Ordering/{Order,SubmitOrder,ReviewOrder,SpendingPolicy,Substitution,CancelRemainder}.cs`, `Fulfillment/{Shipment,DispatchShipment,ConfirmReceipt,DeliveryException}.cs`; consume Catalog/Inventory/Documents contracts. Own `frontend/src/modules/ordering/`, `frontend/src/modules/fulfillment/`, `backend/tests/UnitToko.Tests/Ordering/ReportOrderingTests.cs`, `backend/tests/UnitToko.Tests/Fulfillment/ReportReceiptTests.cs`, and `docs/quality/report-decisions.md` commercial/receipt entries.

**Interfaces:** `SubmitOrder` consumes actor/scope, idempotency key, reviewed quote/variant/price revisions, package quantities and policy revision; returns order receipt or structured changes requiring reconfirmation. `BuyerSpendingDecision` and `StoreAcceptanceDecision` are separate records/permissions. `OrderLineSkuSnapshot` from CAT03 is the immutable downstream quantity/identity contract. `ConfirmReceipt` consumes authorized recipient, shipment revision, actual accepted/rejected quantities and eligible evidence; extracted text is a suggestion, not receipt authority.

- [ ] Test competing orders for final stock, price/pack change after review, two source stores in one purchase, and same person trying both buyer approval and an incompatible decision.
- [ ] Implement separate spending/store review and staff proposal/buyer consent; preserve source-separated suborders, reservation/release/expiry and auditable cancellation remainder.
- [ ] Record F05 policy applicability with finance. Implement required cost centers, limits and credit holds as versioned policies with server checks/reapproval; if not applicable, record the signed decision rather than invent limits.
- [ ] Implement actual pick/pack/partial shipment and recipient-bound single-use tablet/QR proof with all T09 exceptions. Link M02 extraction to the correct immutable shipment/document revision.
- [ ] Verify changed master data never changes issued documents; test wrong/replayed QR, partial/rejected delivery, uncertain response and failed/quarantined evidence.
- [ ] Run scoped module/browser tests, review, record V06/F02/F03/F05/F10/M02 evidence and commit.

**Exit:** An approved branch can buy an explicitly selected package, staff can fulfill the correct contents, and partial/wrong delivery remains accurate and actionable. No signature image alone grants authority.

## OPS02 — Finish procurement, invoices, corrections and accounting

**Depends on:** T05A, PAY01, CAT02, OPS01; PAY04 before final payment/reconciliation acceptance. Extends T08/T10/T11/T12, with payment lifecycle owned by PAY03/PAY04 to avoid duplicate implementations.

**Files:** Existing planned `backend/src/UnitToko.Modules/Procurement/{PurchaseOrder,GoodsReceipt,SupplierInvoice,SupplierClaim,SupplierSettlement}.cs`; add `SupplierPayoutAccountVersion.cs` through PAY04's shared supplier-account contract; Billing `{Invoice,IssueInvoice,InvoiceReadyQueue,PaymentTerms,CreditNote,Refund}.cs`; Accounting `{SourceImport,Reconciliation,PeriodClose}.cs`; `Platform/Integrations/`; finance/procurement frontend modules; tests under `backend/tests/UnitToko.Tests/{Procurement,Billing,Accounting}/`; `docs/runbooks/source-reconciliation.md`.

**Interfaces:** `ApproveSupplierBankChange(proposalRevision, trustedVerificationEvidence)` produces an immutable payee version only after independent approval. `IssueInvoice(receiptLineVersions, termsPolicyRevision)` consumes accepted uninvoiced sources once. `PostJournal`/`PeriodGuard` from T05A remain the only accounting write boundary. Reconciliation links external source+transaction+line and originating web record; it does not reuse image hashes as transaction identity.

- [ ] Add regressions for all four procurement routes, DDO 30 ordered/10 received/20 closed, excess advance return, partly credited claim and no double settlement.
- [ ] Implement trusted supplier-bank change control and T08 inspection/matching/settlement; consume M01 OCR as proposed fields/PO-receipt matches requiring human confirmation.
- [ ] Obtain/version applicable invoice grouping, calendar, numbering, term-start, tax, rounding and correction rules before real issuance. Test missing policy blocks issuance; staged fixtures may use explicitly synthetic policies.
- [ ] Implement ready/unbilled queue and overdue independently from payment state. Test total 100000, allocation 40000, credit 10000 gives outstanding 50000 and overdue when past due.
- [ ] Port linked after-sales intake on finalized sales, physical inspection/quarantine, replacement/credit, refund approval and independently verified payout. Never treat a payout screenshot or request as a completed transfer.
- [ ] Complete source import preview/quarantine/deduplication/acks and AR/AP/stock/unapplied-cash reconciliation; one web sale exported/reimported produces one movement/revenue entry.
- [ ] Implement distinct report preparer/approver/closer, snapshot revision and controlled reopening/reversal. Test role/person changes and concurrent period close. For suspended branches, `history.read` grants no mutation: test permitted historical visibility and denied settlement/new orders by default. Only an approved D02 settlement policy may enable named settlement commands, each with its own current financial permissions, trusted person independence and transactional authorization fences; test those controls separately.
- [ ] Run module integration tests and seven preserved golden scenarios, review finance/stock invariants, record requirement evidence and commit.

**Exit:** Source registers, evidence and journals reconcile; no silent supplier debt/advance write-off, duplicated invoice/cash/credit or mutable issued record. Real finance policy remains an explicit gate.

## OPS03 — Unified work, timeline and operational signals

**Depends on:** Stable module events from OPS01/OPS02/PAY03; reusable worker restrictions/retries from PAY02. Extends T13.

**Files:** Create `backend/src/UnitToko.Modules/Operations/{WorkItem,OrderTimeline,OperationalRule}.cs`, `backend/src/UnitToko.Platform/Operations/ScopedWorkQueries.cs`, `backend/src/UnitToko.Worker/{ReminderJobs,EvidenceHealthJob}.cs`, `frontend/src/modules/work/`, `frontend/src/shared/live/`; tests `backend/tests/UnitToko.Tests/Operations/WorkQueueTests.cs`; `docs/runbooks/{alerts,worker-recovery}.md`.

**Interfaces:** `WorkItem` contains stable source/action reference, scope, owner, created/due times, next permitted action, source revision and delivery status. `OrderTimeline` projects authorized events and links to source records; projection is not financial authority. Notifications use durable deduplication key, kind-scoped lease/retry and provider acknowledgment; user events invalidate/refetch scoped data.

- [ ] Test cross-branch queue counts/events, stale owner delegation, duplicated jobs and worker crash after acknowledged external delivery.
- [ ] Build shared queue/timeline queries with scoped paging, next-action/aging, assignment/handover, reminder/escalation and notification failure visibility.
- [ ] Add unbilled/overdue/source-stock age/OCR failure/backlog/audit failure/database/backup alerts with named responder and remediation link.
- [ ] Implement explainable M07 anomaly rules for exceptional prices, adjustments and refunds with policy version, explanation and human disposition; no auto-rejection/posting. Keep M08 replenishment baseline in CAT02.
- [ ] Verify authorized live updates/reconnect/bounded polling and worker recovery without duplicate send; test stale-state labels and existing proposed convergence target.
- [ ] Review, record F12/U05/Q10/M07 baseline evidence and commit. Configure real outbound delivery only with authorized sender/access.

**Exit:** Each pending exception has an owner and next action; automation failures are visible and recoverable. Event payloads and duplicate alerts reveal no unauthorized branch details.

## OPS04 — Complete consistent, accessible workspaces

**Depends on:** Each module's working contracts; implement incrementally, converge after core integrity. Extends T14.

**Files:** `frontend/src/modules/{catalog,ordering,fulfillment,finance,work}/`, `frontend/src/shared/{ui,live}/`, design-token/global-style files located before editing, `frontend/tests/e2e/report-workflows.spec.ts`, `frontend/tests/accessibility/report-workspaces.spec.ts`, and `docs/design/production-experience.md`.

**Interfaces:** Shared scoped search request uses query text, context and cursor; each domain returns authorized label/code/reference/allowed-action DTOs. UI consumes generated contracts, immutable snapshots, operation receipts and current account/branch/store context. Saved views store only supported filters; URLs and filters cannot widen access.

- [ ] Inventory routes/components into buyer, store, courier, finance and administrator workspaces; list missing pages against U01–U08. Preserve useful branding and safe cart drafts.
- [ ] Add scoped selectors instead of numeric-ID entry, consistent queue/detail/approval patterns and the linked order timeline. Display evidence/differences/consequences before confirmation.
- [ ] Standardize Indonesian statuses, IDR and Jakarta business-time presentation, forms/tables, loading/empty/denied/offline/unknown-outcome recovery and session expiry with safe draft handling.
- [ ] Verify keyboard, labels, focus, contrast, mobile tables, reduced motion, camera upload and MFA at 360px/tablet/desktop. Include actual package modal cancellation and payment review flows.
- [ ] Once core checks pass, implement saved/shareable filters, sorting, repeat orders and bulk SKU entry with server validation of current variants/prices/stock.
- [ ] Run meaningful visual/accessibility/browser regressions and build/type/lint checks. Record synthetic versus connected test evidence explicitly, review and commit.

**Exit:** Every listed business workflow can be completed using understandable scoped controls; no raw internal-reference knowledge is required for routine work.

## OPS05 — Prove contracts, concurrency and deployed integration

**Depends on:** Completed local vertical slices; external identity acceptance may finish here. Extends T02/T14/T15 quality gates.

**Files:** Extend `backend/tests/UnitToko.Tests/`, `frontend/tests/e2e/`, `backend/legacy/server/repository.ts` and `frontend/tests/repository.test.ts` for the still-active legacy conflict fix, existing CI workflow files after inspecting ownership, `backend/contracts/`, `docs/quality/{production-acceptance,report-verification}.md`; create `docs/quality/software-model-inventory.md`.

**Interfaces:** Every acceptance result names requirement IDs, commit, environment identity, setup, assertion, result and artifact. Test environment attestation precedes destructive fixture setup. Unknown operation results resolve via persisted operation receipt/idempotency; known exhausted conflicts return a typed recoverable conflict.

- [ ] Add a focused legacy repository regression for final recognized write-conflict exhaustion and a separate ambiguous outcome. Correct the active legacy path to return its intended recoverable conflict without mislabeling an unknown commit or duplicating a command; new Mongo tests alone do not close Q08. Bring this bounded fix forward while legacy is still serving writes, then retain its regression until retirement.
- [ ] Enforce module import/collection ownership, generated-contract drift and strict input/error/numeric boundaries. Inspect indexes/query plans and realistic pagination; prohibit whole-state/global revision ports.
- [ ] Run real Mongo stock/payment allocation/period/revocation races including the five Review Focus cases. Separate deterministic conflict exhaustion from unknown commit/retry; prove no partial state or double mutation.
- [ ] Add connected browser→actual API→Mongo flow with isolated provider, then actual approved Auth0 staging/proxy acceptance including invitation, verification, MFA/step-up, logout and recovery/security events. Keep fixture UI tests as a separate suite.
- [ ] Run hosted CI, dependency/security review, reproducible build checks, environment separation and migration compatibility; record licenses/checksums for OCR software/models.
- [ ] Measure realistic concurrency, scoped-query latency, UI performance, OCR field accuracy/false matches/review effort/latency/memory and worker backlog on documented hardware/data; distinguish proposed targets from measured passes.
- [ ] Independently review authorization/evidence/stock/money paths; remediate launch-blocking findings and update all 97 rows with evidence or explicit open gate. Commit the evidence and fixes separately where appropriate.

**Exit:** No unresolved critical/high security or stock/money integrity issue; no fixture-only claim of actual provider or deployed workflow success.

## OPS06 — Rehearse migration, restore and accepted pilot

**Depends on:** OPS05 and signed affected D01–D08 decisions; preserves T15→T16.

**Files:** `backend/migrations/{ExportLegacy,ImportBusinessData,ReconcileCutover}/`, `backend/deploy/`, `docs/runbooks/{deployment,cutover,rollback,backup-restore,incident-response,operator-guide}.md`, `docs/quality/{migration-rehearsal,pilot-acceptance,report-verification}.md`.

**Interfaces:** Migration manifest records source snapshot/checksum, entity/legacy/new ID mapping, unresolved/rejected rows, counts, stock/cost/AR/AP/journal/document/evidence reconciliation and high-water sequences. Release manifest records build/configuration/model versions, accountable operators, rollback point and recoverability evidence.

- [ ] Rehearse deterministic CAT04 mapping/import with actual authorized inventory of real versus demo records. Preserve historic units/prices/documents and unresolved rows; never infer missing package contents or fabricate evidence/person bindings.
- [ ] Restore Mongo, originals/derived artifacts and identity encryption keys in isolation; pause workers and reconcile external acknowledgments before resuming. Test old OCR result, expired lease and verified evidence retrieval after restore.
- [ ] Measure existing proposed RPO ≤15 minutes/RTO ≤4 hours against the approved host/backup tier. Resolve unsupported targets with owner before launch; do not claim a paper runbook proves recovery.
- [ ] Rehearse one-writer freeze/import/reconcile/switch and pre-write rollback versus post-write recovery. Disable all corresponding legacy stock/money/auth writers together.
- [ ] Train named operators/backups with person-level independence; obtain remaining commercial, receiving-account, evidence retention, recovery and source-authority decisions.
- [ ] With authorized real pilot actions, complete branch order→partial receipt→invoice→image/OCR/bank review→allocation/reconcile, wrong delivery/return/refund, supplier exception and period close.
- [ ] Record operational acceptance and approved production cutover; monitor pilot before further branches. Close goal only after required evidence and rollout outcomes, never solely after local tests.

**Exit:** A reviewed recoverable release and functioning accepted branch pilot, with every required report row and original requirement accounted for.

## OPS07 — Conditional advanced assistance

**Depends on:** Required rules/extraction baselines, governed clean data and documented applicability/benefit decision. Does not delay launch merely because a trained model lacks data; it cannot erase required M01–M10 baselines.

**Files:** Create only when justified `backend/src/UnitToko.Modules/Operations/AssistanceProposal.cs`, `backend/src/UnitToko.Platform/DocumentProcessing/` matching/forecast adapters, `backend/tests/UnitToko.Tests/Assistance/`, `docs/quality/assistance-evaluation.md`; update software/model inventory.

**Interfaces:** Suggestions expose model/rule/data versions, confidence/uncertainty, evidence and explicit human disposition. They have no approval, payment, stock-write or automatic package-conversion authority.

- [ ] Record whether embeddings, trained anomaly detection or forecasting are applicable now; state data sufficiency and rule baseline. If not ready, record deferred conditional scope and revisit trigger without claiming a deployed model.
- [ ] If applicable, split evaluation data by source/time to avoid leakage, compare field/product matching errors, anomaly false alerts and lead-time forecast/replenishment outcomes against deterministic baselines.
- [ ] Deploy only after measured benefit with pinned artifacts, resource bounds, monitoring and rollback; otherwise retain baseline and document failed experiment.
- [ ] Verify no guessed pack quantity, automatic financial decision or hidden customer-data transfer; record M06–M10 conditional decisions/evidence.

## Primary sources and limits of research

- [OWASP transaction authorization](https://cheatsheetseries.owasp.org/cheatsheets/Transaction_Authorization_Cheat_Sheet.html) supports server-enforced transaction state and authorization bound to the reviewed transaction. The exact payment/bank lifecycle here is our design for this report.
- [OWASP file upload guidance](https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html) informs private storage, validation, scanning and size/resource limits; malware scanning does not establish payment authenticity.
- [W3C modal dialog pattern](https://www.w3.org/WAI/ARIA/apg/patterns/dialog-modal/) informs focus containment, Escape and focus return in explicit package selection.
- [RFC 9562](https://www.rfc-editor.org/rfc/rfc9562.html) defines UUIDv4/v7; choosing v4 for opaque catalog references is a project decision, not an access-control mechanism.
- [GS1 GTIN Management Standard](https://ref.gs1.org/standards/gtin-management/) separates trade-item/packaging identification changes from internal SKU policy. Legitimate allocation and business ownership must be verified.
- [PaddleOCR PP-OCRv6 documentation](https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version3.x/algorithm/PP-OCRv6/PP-OCRv6.en.md) confirms tiny/small variants and discusses ONNX-backed execution. Its published benchmarks do not prove Indonesian bank-image accuracy or our C# pipeline compatibility.
- [Tesseract data files](https://tesseract-ocr.github.io/tessdoc/Data-Files.html) supplies the Indonesian/English baseline; [ONNX Runtime C# documentation](https://onnxruntime.ai/docs/get-started/with-csharp.html) documents .NET inference, not the missing OCR preprocessing/decoding/model parity. PAY02 must measure those before selection.

Sources checked 28 September 2026. No model benchmark, live provider test, business approval or production action occurred merely by writing this plan.