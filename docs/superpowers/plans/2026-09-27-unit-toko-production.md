# Unit Toko Production Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the existing Unit Toko demo into a reliable B2B production system for BNI branches and approved business accounts, with explicit roles, MongoDB, secure authentication and complete operational workflows.

**Architecture:** Keep the Next.js frontend and build a C# modular backend with one API and a worker. Modules own scoped collections and expose explicit interfaces; stock/money changes remain transactional, with durable jobs after commit. Migrate through a temporary legacy adapter and one authoritative writer per domain.

**Tech Stack:** Next.js/React/TypeScript; C#/ASP.NET Core .NET 10 LTS; official MongoDB C# driver with a replica set; managed OIDC identity (Auth0 integration preparation selected by the user on 28 September 2026); private object storage; OpenAPI-generated frontend contracts; xUnit, real Mongo integration tests, Playwright and accessibility/load checks.

**Spec:** [Unit Toko production specification](../../requirements/unit-toko-production.md). Read this together with the [three-agent review](../../quality/production-readiness-2026-09-27.md).

## Global Constraints

- Launch audience: BNI branches and approved business accounts only. No public self-registration or retail checkout in the first launch.
- The November dates are reference dates; release follows readiness gates.
- Three main source directories: `frontend/`, `backend/`, `docs/`. Keep required repository/CI metadata at root.
- Preserve existing uncommitted work; review the diff before moving files or committing. Do not commit another task's changes.
- Preserve `website/AGENTS.md` as `frontend/AGENTS.md`; read the relevant installed Next.js documentation before changing routing/build behavior, as that project instruction requires.
- Raw manuals, credentials, production data, uploads and extracted source text stay out of public Git/static assets.
- Six launch role templates with independently assigned finance packs. No template grants a blanket union of financial powers.
- C#/.NET 10 current supported patch; MongoDB must support multi-document transactions. No production demo seeding.
- Int64 internal IDs cross JSON as strings. Money crosses JSON as decimal strings; validate the precise limits in spec §5.
- No whole-company state API, no negative sellable stock, no duplicate invoice sources, no duplicate cash postings, no unbalanced journal and no unauthorized cross-branch access.
- Keep one authoritative stock/money writer. Switch all inventory, order, procurement, fulfillment, billing and accounting writes consistently.
- External production changes, paid service provisioning and data cutover are separate actions; this deliverable is a plan.

## Review Focus

1. A lost response or two simultaneous checkouts must not double-reserve stock, invoice the same receipt twice or duplicate payment allocation — T05–T07 and T10–T11.
2. A removed permission or changed role must not authorize a pending approval, a cross-branch request or approval of the actor's earlier work — T03–T04 and T11–T12.
3. Wrong/partial delivery, stale/replayed QR or missing evidence must preserve accurate quantities and a recoverable, auditable workflow — T08–T09.
4. Partially paid overdue invoices, excessive numeric inputs, month boundaries and expired stock must produce correct balances/statuses — T05–T06, T10–T12.
5. Legacy cashier imports and restored background jobs must not repeat stock/accounting changes or resend acknowledged external actions — T12–T13 and T15.

---

## Recommended sequence

The three initial review assignments are complete. During implementation, use parallel agents on independent modules after shared contracts are stable, then an independent reviewer at each gate. Do not have multiple agents rewrite the same domain engine or data model concurrently.

| Stage | Tasks | User-visible result / exit gate |
| --- | --- | --- |
| 0. Baseline and cleanup | T00–T01 | Existing behavior works from the three-folder layout |
| 1. Production foundation | T02 → T05 → T04 → T03 → T05A | Backend/contracts, Mongo primitives, permissions, identity and shared accounting/evidence |
| 2. Real business data and purchasing | T06–T08 | Editable products/variants, consistent stock, branch orders and complete procurement |
| 3. Delivery and financial completion | T09–T12 | Verified delivery, timely invoices, controlled returns/refunds and reconciled reporting |
| 4. Reliable experience | T13–T14 | Live updates, notifications, accessible polished workflows and operational visibility |
| 5. Readiness and release | T15–T16 | Rehearsed migration/restore, tested pilot, controlled production rollout |

Task IDs are stable references; follow the explicit dependency order in the table, not numerical order within stage 1. These are dependency stages, not promised calendar durations. After T01, estimate from actual porting complexity and staff availability; do not trade away stock, finance or access controls to meet a presentation date.

## T00 — Preserve the verified baseline and turn findings into acceptance cases

**Status:** Audit and initial baseline complete in this planning task. Further steps below belong to implementation.

**Files:** Existing `website/tests/`, `website/scripts/test-domain.mjs`, `website/lib/domain/engine.ts`, `website/lib/server/repository.ts`; create `docs/quality/production-acceptance.md` and `backend/tests/Fixtures/legacy-scenarios.json` during implementation.

**Interfaces:** Consumes current role/action and domain behavior. Produces versioned input/output scenarios with fixed clock, actor, initial balances, commands and expected final balances; do not encode incidental object IDs or array ordering.

- [x] Run the existing isolated regression suite: 266/266 passed on 27 September 2026.
- [x] Run TypeScript checking: passed. No claim is made that build/lint/browser/security/load checks ran during planning.
- [x] Review current behavior with three agents and map findings to the specification.
- [ ] Record source commit and existing dirty paths. Capture golden scenarios for partial delivery, substitution, invoice uniqueness, payment allocation, return, stocktake and period close.
- [ ] Replace moving-date fixture assumptions with a frozen test clock; declare `esbuild` directly. Add failing cases for A02 payment self-verification, A11 overdue-partial balance and A15 conflict exhaustion before their owning implementation task.
- [ ] Verify test fixtures cannot use a real connection string. Gate any HTTP smoke execution on server environment/database identity, not hostname alone.

Verification while the original layout remains:

```powershell
Set-Location website
npm run typecheck
npm run lint
npm test
npm run build
```

**Acceptance:** Results are recorded honestly; failures discovered here become a bounded fix or an explicit gate before the affected workflow is enabled. Commit only baseline/test changes belonging to this task.

## T01 — Restructure without rewriting business behavior

**Files:** Move `website/app`, `components`, UI-only `lib`, `public`, frontend configuration and package files into `frontend/`; move server/domain persistence and backend fixtures into `backend/legacy/`; update root README, `.gitignore`, frontend aliases, deployment root configuration and documentation links. Preserve the modified layout and mentor mockups. Create `docs/architecture/module-boundaries.md`.

**Interfaces:** Legacy public route URLs and command/result contracts remain stable temporarily. Next route adapters call server-only legacy exports. Browser modules may not import backend persistence/domain internals.

- [ ] Make a file ownership manifest classifying each test/helper as UI, domain, HTTP or infrastructure. Keep shared UI validation separate from authoritative backend rules.
- [ ] Move source using checked explicit paths; move no `.data`, environment secrets, cache or generated build folders. Preserve Git history and uncommitted content.
- [ ] Leave thin compatibility handlers for existing Next API routes; update test fixture paths to derive from the test file location rather than assuming `website/` as the current directory.
- [ ] Introduce the module directories from the spec; add import-boundary rules. Keep public UI components cohesive, and break `workspace.tsx`/`operations.tsx` into feature views as their modules migrate.
- [ ] Update clean install/test/build commands and Vercel's planned root to `frontend`. Verify locally; do not alter the live deployment during this task.
- [ ] Run the baseline suite from the new paths and inspect key buyer/staff flows. Commit the structural change independently from the new backend.

**Acceptance:** Three main directories; same visible behavior; no new demo/production data; current modified UI survives; no secret/artifact accidentally added. A structure-only commit can be reviewed independently of functional changes.

## T02 — Establish the C# service, contract and test foundation

**Files:** Create `backend/UnitToko.slnx`, `src/UnitToko.Api/Program.cs`, `src/UnitToko.Worker/Program.cs`, `src/UnitToko.Modules/UnitToko.Modules.csproj`, `src/UnitToko.Modules/Identity/Contracts/ActorContext.cs`, `src/UnitToko.Platform/Clock.cs`, `contracts/openapi.json`, `deploy/compose.yaml`, `tests/UnitToko.Tests/`, `frontend/src/api/generated/`, `.github/workflows/quality.yml`.

**Interfaces:** Versioned `/api/v1` endpoints; RFC-style problem responses with stable `code` and `traceId`; request idempotency and expected version headers; OpenAPI generated from server definitions. `IClock.UtcNow` supplies time. Domain IDs and money are strings at the HTTP boundary. Define the shared `ActorContext` contract described in T03 here so policy tests in T04 can use synthetic actors before provider login is implemented; production must never enable the test actor adapter.

- [ ] Install/pin a supported .NET 10 SDK and official Mongo driver in development/CI; the current environment has a dotnet executable but no SDK was reported by the planning check.
- [ ] Create API/worker/module/platform/test projects. Implement liveness independent of external services and readiness that checks required dependencies without exposing secrets.
- [ ] Configure a local Mongo replica set and isolated test databases; refuse standalone mode for transaction-required tests. Pin actual package versions after compatibility review.
- [ ] Add centralized validation, exception→problem mapping, request correlation, cancellation and bounded body reading. Require authentication by default; mark only deliberate health/login routes public.
- [ ] Generate a TypeScript client and add a CI drift check. Add real backend integration fixtures with fake clock/provider/object store and an actual replica-set database.
- [ ] Verify boot with valid configuration, fail-closed production configuration, invalid JSON/oversized streaming input and unavailable database. Commit the working skeleton with contract tests.

Example contract to implement and test:

```json
{
  "type": "https://unit-toko.invalid/problems/version-conflict",
  "title": "Data changed; refresh and try again",
  "status": 409,
  "code": "version_conflict",
  "traceId": "request-correlation-id"
}
```

The URI above is a contract example, not a deployed endpoint. Use an owned stable documentation URI in the live configuration.

```powershell
dotnet build backend/UnitToko.slnx
dotnet test backend/UnitToko.slnx
```

## T03 — Production account lifecycle, email verification and MFA

**Files:** Create `backend/src/UnitToko.Modules/Identity/{OidcEndpoints,SessionService,InvitationService,RecoveryService,AuthorizationFence}.cs`, provider adapter under `UnitToko.Platform/Identity/`, `frontend/src/modules/identity/`, `backend/tests/UnitToko.Tests/Identity/`; create `docs/runbooks/account-recovery.md`.

**Interfaces:** `ActorContext` contains immutable user ID, provider issuer/subject, current grant version, actual second-factor completion time and validated factor assurance. Production actors also carry the persistent session hash/version and identity security version. Provider primary-authentication time is stored separately and never substitutes for fresh MFA. Invitation binds intended email, organization and grants approved by an authorized administrator; membership approval is separate from provider login.

- [ ] Verify the managed provider can meet required MFA, region, recovery and cost constraints; configure a development tenant only after its access is available. Keep provider credentials outside source.
- [ ] Implement OIDC code+PKCE login and server-held tokens/opaque cookie, callback validation, logout and revocation. Test through the actual planned frontend/API origin arrangement.
- [ ] Build invitation and verified-email gates; deny open signup and unauthorized organization membership. Require completion of MFA before issuing a transaction-capable session.
- [ ] Implement session limits, fresh-MFA step-up, owner-controlled recovery and safe factor recovery. Remove permanent-password-setting UI from the production flow.
- [ ] Authenticate/deduplicate provider events and fence sensitive business writes against concurrent suspension/grant changes. Log recovery/privilege events without tokens.
- [ ] Run allow/deny identity cases and commit.

Local foundation implementation is recorded in `docs/quality/production-progress.md`. Prepared provider/session/onboarding libraries do not activate production sign-in. Keep the task open until origin/CSRF/API/frontend integration, provider event coverage, owner recovery and actual tenant acceptance are complete.

Required fixture matrix:

| Condition | Expected |
| --- | --- |
| Correct password, unverified email or incomplete required MFA | No business session |
| Valid provider login without approved membership | No organization access |
| Replayed/expired invitation or wrong invitee | Rejected without creating grants |
| Silent provider SSO from old auth; sensitive refund action | Fresh factor challenge required |
| Permission revoked while an approval is pending | No stale authorization commit |
| Admin initiates recovery | Owner chooses secret; old sessions revoked; admin never sees new password |

## T04 — Organization model and the six role templates

**Files:** Create `backend/src/UnitToko.Modules/Organizations/{Organization,Membership,ScopePolicy}.cs`, `Identity/{Permissions,RoleTemplates,ApprovalPolicy}.cs`, `frontend/src/modules/organizations/`, `frontend/src/modules/administration/`, `backend/tests/UnitToko.Tests/Authorization/`; create `docs/architecture/permissions.md`.

**Interfaces:** `ScopePolicy` checks buying organization, exact branch/division, fulfillment shop/warehouse and channel. `ApprovalPolicy` compares immutable actor IDs, not current role strings. UI uses allowed actions returned for a resource but server reevaluates every command.

- [ ] Implement six templates and finance packs exactly as spec §3. Migrate each old finance role to its corresponding pack, never their union.
- [ ] Create explicit BNI branch/division and approved business organization membership. Default Buyer visibility to self/assigned records; named PIC grant adds only its stated branch scope.
- [ ] Add server policies to queries, detail routes, writes, exports, attachments and event subscriptions. Test every grant's denied counterpart and direct ID substitution.
- [ ] Add independent maker/checker rules for payment verification, stock adjustment, credits/refunds, disbursement, access grants and period submit/approve/close.
- [ ] Build admin scope/grant forms with a preview of effective permissions and audit reason; T03 supplies account invitation screens. Require a separate approver for privileged self-impacting grants.
- [ ] Test multiple memberships, suspended account, permission change mid-session, changed roles on old requests and unavailable branch. Commit the permission catalog and its matrix tests.

**Acceptance example:** PIC Tebet cannot read or mutate Pancoran orders by replacing an ID; a courier sees assigned shipments only; a System Admin cannot verify payments without a separately authorized business assignment and still cannot verify their own recording.

## T05 — Mongo persistence and transactional primitives

**Files:** Create `backend/src/UnitToko.Platform/Mongo/{MongoUnitOfWork,CollectionMigrations,IdAllocator,IdempotencyStore,OutboxStore}.cs`, `UnitToko.Modules/Accounting/Money.cs`, `backend/tests/UnitToko.Tests/Persistence/`, `backend/migrations/`; modify API serialization and validation from T02.

**Interfaces:** A unit of work supplies the same Mongo session to participating module operations. IDs are Int64 internally, string externally; `Money` is decimal internally, decimal string externally. Idempotency scope is actor + operation + key with body hash/result persisted atomically.

- [ ] Create validators and indexes from spec §5, including identity uniqueness, source-event uniqueness and document sequence scopes. Bootstrap migrations explicitly; remove runtime seeding from production.
- [ ] Implement atomic ID allocation and JSON converters; reject lossy browser numeric IDs and out-of-contract decimal values/imports.
- [ ] Implement bounded driver-supported transaction retries, expected-version checks, idempotent result storage and outbox insertion. On exhausted business conflicts return typed 409, not raw database errors.
- [ ] Write integration tests that race writes on the actual replica set, kill/restart a worker, retry after a lost response and inject a downstream transaction failure.
- [ ] Compare golden legacy scenarios where invariants should remain the same. Commit only after rollback leaves no partial stock/money/document records.

Concrete numeric vectors:

```json
{
  "idRoundTrip": "9007199254740993",
  "acceptedMoney": "999999999999999.99",
  "rejectedMoney": ["1000000000000000.00", "12.345", "NaN", "1e999"],
  "rejectedInputQuantity": [0, -1, 1.5, 1000001],
  "idempotency": {"sameKeySameBody": "originalResult", "sameKeyDifferentBody": 409}
}
```

The command-specific quantity policy may allow zero for a recorded discrepancy, but order/reservation quantities must be positive integers. Test document-total overflow even when each individual field is valid.

## T05A — Shared accounting and private evidence foundations

**Dependencies:** T05 persistence, T04 policies and T03 authenticated actors. Complete before procurement, fulfillment or financial workflows.

**Files:** Create `backend/src/UnitToko.Modules/Accounting/{JournalEntry,PostJournal,PeriodGuard}.cs`, `Documents/{EvidenceService,EvidenceMetadata}.cs`, object-storage/scanner adapters in `UnitToko.Platform/Storage/`, and backend accounting/evidence integration tests. T09 extends this evidence service; T12 extends these journal foundations.

**Interfaces:** `PostJournal` consumes a unique source event, actor/scope, business date, currency and bounded debit/credit lines in the caller's Mongo transaction. `PeriodGuard` rejects writes into a closed accounting period. `EvidenceService` stages bytes privately, validates type/size/content and scan outcome, records a checksum/version, then makes evidence eligible for the owning workflow; download reevaluates scope.

- [ ] Port balanced posting and closed-period checks before porting procurement settlement; enforce source-event uniqueness and immutable posted entries.
- [ ] Add per-period transaction fencing so a concurrent close and late posting cannot both commit against incompatible snapshots. A simple preflight read is insufficient.
- [ ] Implement private upload/quarantine/content-validation and authorized download, including different limits for images/PDFs and supplier inspection video.
- [ ] Coordinate object-store completion with metadata eligibility using staged/pending/validated states and orphan cleanup; never claim object storage participates in a Mongo transaction.
- [ ] Test unequal debits/credits, duplicate source events, closed-period race, absent/replaced object bytes, failed scan, cross-branch download and cleanup after interrupted upload.
- [ ] Commit only when procurement can call real transactional accounting/evidence interfaces; no placeholder financial success or fabricated attachment metadata.

**Acceptance:** A supplier receipt that cannot post its required journal rolls back its stock mutation. A failed or pending evidence upload cannot satisfy a receipt/claim proof requirement.

## T06 — Real catalog, packaging and inventory

**Files:** Create `backend/src/UnitToko.Modules/Catalog/{ProductFamily,Sku,PriceVersion,CatalogQueries}.cs`, `Inventory/{StockBalance,StockMovement,ReservationService,PackConversionService,StocktakeService}.cs`, corresponding repositories, `frontend/src/modules/catalog/` and `inventory/`, tests in `backend/tests/UnitToko.Tests/{Catalog,Inventory}/`.

**Interfaces:** Catalog produces price/SKU snapshots; Inventory owns reserve/release/dispatch/receive/convert operations. Availability is derived from actual stock buckets, never trusted from client payloads. A conversion carries ratio, source/destination SKU, lot, cost basis, reason and actor.

- [ ] Port editable family/SKU/category/image/description/barcode/source mapping; delete dependence on ID prefixes, product-name parsing and seed-only metadata in the production catalog.
- [ ] Implement explicitly separate sealed-carton/loose-unit balances and audited conversion. Make expiry/lot/cost propagation deterministic.
- [ ] Port reservations, quarantine, expiry, stock count and independent adjustments with per-balance concurrency control and immutable movements.
- [ ] Add indexed scoped catalog/inventory APIs with server pagination; catalog queries do not load users/invoices/journals.
- [ ] Test two buyers competing for the last units, failed conversion rollback, stale prices, inactive SKU, expiry across Jakarta midnight, allocation release and stocktake approval.
- [ ] Verify a new ATK family with unit/carton variants can be created entirely through UI and displays correct category/image/stock. Commit.

**Acceptance scenario:** One carton containing 12 units is converted once: carton decreases by 1, loose balance increases by 12, same lot/expiry/cost total preserved. Replaying the command changes neither balance again.

## T07 — B2B ordering, approvals and source-separated suborders

**Files:** Create `backend/src/UnitToko.Modules/Ordering/{Order,SubmitOrder,ReviewOrder,Substitution,CancelRemainder}.cs`, `frontend/src/modules/ordering/`, update generated client; port domain/customer/cart tests to backend/frontend owners.

**Interfaces:** Submit consumes approved buyer scope, versioned SKU/price snapshots and Inventory reservations in one transaction. Produces a parent order plus source-separated suborders and an audit event. Optional buyer purchase-approval grant is distinct from operations review.

- [ ] Preserve cart/draft recovery, branch address selection, purchase references and clear submission confirmation. Block unapproved organization/account checkout.
- [ ] Implement submission with server pricing, source mapping, checked totals and authoritative stock validation. Require expected price version and show changed-price confirmation.
- [ ] Keep mixed OMI/Smart shopping convenient while creating separate operational suborders/posting references. Do not duplicate the parent demand in source imports.
- [ ] Port partial allocation/fulfillment, buyer-confirmed substitution, operational review and cancellation of unfulfilled remainders.
- [ ] Add reusable branch orders and bulk SKU input after normal entry works; validate every imported row and show preview errors.
- [ ] Test stale cart, unauthorized address/branch, lost response, duplicate submit, partial stock, declined substitution and cancelled remainder. Commit.

**Acceptance:** Request 10 units, ship 8, cancel 2: reservation and eventual invoice use actual quantities. No quiet substitution or price change reaches the buyer.

## T08 — Procurement, receiving and supplier settlement

**Files:** Create `backend/src/UnitToko.Modules/Procurement/{PurchaseOrder,GoodsReceipt,SupplierInvoice,MatchSupplierInvoice,SupplierClaim,SupplierSettlement}.cs`, procurement finance interfaces to Accounting, `frontend/src/modules/procurement/`, integration tests and supplier source contracts.

**Interfaces:** PO commitment is separate from counted receipt. Receiving invokes Inventory. Supplier advance/refund/claim settlement invokes Accounting in the same unit of work. External instructions have durable status/reference but are not represented as completed transfers by merely sending a request.

- [ ] Port OMI regular/DDO, Smart and Pasar Kering paths with vendor/source identity, negotiated prices, partial receipt, funding/transport and custom-logo requirements.
- [ ] Record container receipt separately from item inspection/put-away; upload private inspection/unboxing evidence using appropriate media limits and scanning status.
- [ ] Add PO/receipt/SPH line matching with mismatch queue and independent approvals for exceptions.
- [ ] Add unused advance refunds and supplier return outcomes: accepted credit, replacement or cash settlement. Keep rejected/expired stock quarantined pending disposition.
- [ ] Port stocktake/expiry policy links and reminders for supplier-return deadlines; no silent write-off or disappearance of supplier obligations.
- [ ] Test DDO 30 cartons ordered/10 received/20 closed, excess advance refund, claim partly credited, duplicate settlement, rejected return and evidence failure. Commit.

**Acceptance:** The PO can close only when remainder and financial balance have a documented resolution. Supplier statements, inventory movements and accounting totals agree.

## T09 — Delivery, tablet/QR proof and immutable documents

**Files:** Create `backend/src/UnitToko.Modules/Fulfillment/{Shipment,DispatchShipment,ConfirmReceipt,DeliveryChallenge,DeliveryException}.cs`, `Documents/DocumentVersion.cs`, `frontend/src/modules/fulfillment/`, `frontend/src/app/receive/[challenge]/page.tsx`, backend security/integration and frontend browser tests; extend `Documents/EvidenceService.cs` from T05A.

**Interfaces:** Dispatch records actual picked quantities. Receipt confirmation consumes shipment version, authorized recipient, challenge/evidence and accepted/rejected quantities; publishes one immutable receipt and document version. Object-store bytes must exist and pass validation before evidence is finalized.

- [ ] Port picking/packing, courier assignment, partial delivery, failed attempt and returned-undelivered goods; add a mobile daily task list.
- [ ] Generate immutable Surat Jalan with dispatched quantities, seller/buyer/address snapshots, version/hash and searchable document register.
- [ ] Implement tablet signature and QR flow with expiring hashed challenge, recipient-bound verification, attempt limits and single-use consumption in the receipt transaction.
- [ ] Keep courier session separate from recipient confirmation; never use courier-entered contact details for OTP. Mark offline drafts as unsent and require server checks before final receipt.
- [ ] Save actual quantities, consent, timestamp and discrepancies. A wrong shipment/version or partial receipt opens the appropriate exception, not a fabricated successful delivery.
- [ ] Test copied/replayed/expired QR, wrong branch, unauthorized recipient, lost response, missing evidence, upload rejection and document rendering after master address edits. Commit.

**Acceptance:** Changing a division's name tomorrow cannot change yesterday's issued Surat Jalan. A signature image alone never grants delivery acceptance authority.

## T10 — Timely invoices, exact balances and aging

**Files:** Create `backend/src/UnitToko.Modules/Billing/{Invoice,IssueInvoice,InvoiceReadyQueue,ReceivablesQuery,PaymentTerms}.cs`, `Documents/InvoiceRenderer.cs`, `frontend/src/modules/finance/{invoices,receivables}/`, backend billing tests.

**Interfaces:** An invoice consumes eligible finalized accepted receipt lines, snapshots and approved terms/tax policy. Unique source allocations prevent duplicate invoicing. Payment state and aging are independent fields. Document delivery is an outbox job, not part of the database transaction.

- [ ] Confirm invoice grouping, term start event, approved seller/template and tax-document process at the spec's business gate before enabling real issuance.
- [ ] Port unique invoice sources and create the ready/unbilled work queue with owner, timestamps and proposed one-business-day escalation.
- [ ] Add exact decimal totals, immutable invoice snapshot/version/number, official tax references and draft preview. No simulated invoice enters the real document register.
- [ ] Fix partial-payment aging and show outstanding balance, due date, days overdue and payment status separately.
- [ ] Track issued/sent/failed/delivered-or-provider-accepted/acknowledged statuses accurately; do not equate an email-provider acceptance with recipient reading.
- [ ] Test partial payment/credit past due, 30-day term date boundaries, duplicate issuance race, changed master data and missing tax-policy configuration. Commit.

Required balance case:

```json
{
  "invoiceTotal": "100000.00",
  "verifiedAllocatedPayment": "40000.00",
  "approvedCredit": "10000.00",
  "dueDate": "2026-09-01",
  "businessDate": "2026-09-27",
  "expectedOutstanding": "50000.00",
  "expectedPaymentState": "partial",
  "expectedAging": "overdue"
}
```

## T11 — Payment reconciliation, after-sales and verified refunds

**Files:** Create `backend/src/UnitToko.Modules/Billing/{RecordPayment,VerifyPayment,AllocatePayment,CreditNote,Refund}.cs`, `Fulfillment/{AfterSalesCase,ReturnAuthorization,InspectCustomerReturn}.cs`, corresponding finance/buyer UI and integration tests.

**Interfaces:** Bank/source evidence identifies the payment; independent verification precedes usable allocation. Cases reference original immutable order/receipt/invoice. Credit/refund commands reference approved case/credit balances and unique accounting events.

- [ ] Separate recording, verification and allocation; add statement import preview, deduplication, unidentified/suspense funds and explicit allocation confirmation.
- [ ] Enforce actor independence and authorization fences at commit time; retain reversible draft states and immutable verified financial facts.
- [ ] Allow buyer cases after finalization without editing historic sales. Add return authorization and physical inspection/quarantine; policy determines replacement/credit/refund eligibility.
- [ ] Implement refund approval and manual bank payout reference/evidence entry, followed by independent verification before final paid status. No transfer API is assumed.
- [ ] Test same-actor verification, changed role, concurrent allocation to two invoices, overpayment, partial refunds, failed payout, restored retry, returned goods condition and no-double-credit.
- [ ] Verify balances through customer, finance and accounting views; commit.

**Acceptance:** A verified Rp100,000 receipt cannot allocate Rp100,000 twice during simultaneous requests. Recording a refund request or uploading an unverified transfer screenshot never silently reduces the bank balance as a completed payout.

## T12 — OMI/S-Mart reconciliation, accounting and period close

**Files:** Create `backend/src/UnitToko.Modules/Accounting/{SourceImport,Reconciliation,PeriodClose}.cs`, source adapters under `UnitToko.Platform/Integrations/`, `backend/contracts/imports/`, `frontend/src/modules/finance/{reconciliation,reports,periods}/`, accounting tests and `docs/runbooks/source-reconciliation.md`; extend `JournalEntry.cs`, `PostJournal.cs` and `PeriodGuard.cs` from T05A.

**Interfaces:** External transaction identity includes source system, external transaction/line ID and mapped originating web record where applicable. Journal sources are unique; posted entries are immutable and corrected by linked reversal. Close consumes a reviewed reconciliation snapshot/version.

- [ ] Agree source authority and import/export acknowledgement with OMI/S-Mart owners. Keep walk-in POS operation while preventing its sales from being invisible to available stock.
- [ ] Implement staged import validation/preview/quarantine, source reference deduplication, channel totals and acknowledgment. Reimporting web-originated sales must not create another movement or revenue entry.
- [ ] Port balanced journals, financial reports, supplier advances/claims and source-specific dimensions. Add complete AR/AP/unallocated cash/stock reconciliation.
- [ ] Implement report preparation→approval→accounting close with three distinct actors, locked snapshot revision, closed-period enforcement and controlled reopening/reversal.
- [ ] Test duplicate/corrupt/out-of-order imports, conflicting source totals, closed-month transactions, changed role, revised report after approval and supplier residuals.
- [ ] Verify consolidated totals equal source totals after documented adjustments, then commit.

**Acceptance:** A web sale exported to OMI and later reimported as the same source sale produces one stock decrement and one revenue posting. A stale external inventory feed cannot be presented as verified live availability.

## T13 — Live updates, notifications and operational monitoring

**Files:** Create `backend/src/UnitToko.Worker/{OutboxDispatcher,ReminderJobs,EvidenceHealthJob}.cs`, `UnitToko.Api/Events/`, provider adapters, `frontend/src/shared/live/`, task/notification UI; create `docs/runbooks/{alerts,worker-recovery}.md`.

**Interfaces:** Committed module events carry scope and revision. Browser events invalidate scoped data, then clients refetch authoritative views. Notification jobs have stable deduplication keys, lease/retry state and external delivery acknowledgements.

- [ ] Implement authorized event subscription using SSE/SignalR or bounded polling appropriate to the actual host; keep a reconnect/refetch fallback and never expose another branch's event payload.
- [ ] Implement invoice-ready escalation, overdue reminders, order/substitution/delivery alerts and supplier expiry queues using durable jobs.
- [ ] Configure production email delivery only after sender/domain access is available; store send result/retry history and honor business notification preferences.
- [ ] Add structured logs, metrics and alerts for failed invoices, outbox lag, stock discrepancies, authorization abuse, database health and backup status.
- [ ] Test two sessions, disconnect/reconnect, duplicate event, provider timeout, lease expiry, worker crash after external acknowledgment and recovery with outbound jobs paused.
- [ ] Verify proposed ≤5-second online stock convergence and clear stale-state handling; commit.

## T14 — Professional frontend and real browser quality

**Files:** Refactor `frontend/src/modules/*` views, `src/shared/ui/`, design tokens, `tests/e2e/`, `tests/accessibility/`, Playwright configuration; update `docs/design/production-experience.md`.

**Interfaces:** Frontend consumes generated DTOs and resource allowed-actions, not database entities. Server business errors map to understandable Indonesian UI messages with recovery actions and retained safe drafts.

- [ ] Keep useful existing branding, responsive layout, cart/draft recovery and role-aware dashboards. Remove public-demo onboarding and simulation wording only where the associated production capability exists.
- [ ] Create coherent navigation for six workspaces; each module starts with actionable work, clear status and a primary action. Split oversized components by feature ownership.
- [ ] Standardize forms, filters/pagination, amount/date formatting, loading/empty/error states, confirmations and accessible mobile tables. Make courier/tablet flows usable one-handed where practical.
- [ ] Test real browser back/forward, expired session with unsaved work, slow network, repeated clicks, keyboard-only flow, screen-reader labels, reduced motion and 360px/tablet/desktop layouts.
- [ ] Measure against the spec's accessibility/performance goals with realistic data. Address failures that block core tasks before decorative motion or advanced dashboards.
- [ ] Complete end-to-end B2B order, partial receipt, invoice/payment, after-sales and reporting scenarios; commit.

```powershell
npm --prefix frontend run typecheck
npm --prefix frontend run lint
npm --prefix frontend test
npm --prefix frontend run test:e2e
npm --prefix frontend run build
dotnet test backend/UnitToko.slnx
```

`test:e2e` is a planned script created in this task; it must target an isolated staging/test environment whose identity is asserted before mutation.

## T15 — Migration, restore and production deployment rehearsal

**Files:** Create `backend/migrations/{ExportLegacy,ImportBusinessData,ReconcileCutover}/`, `backend/deploy/` infrastructure/container definitions, `docs/runbooks/{deployment,cutover,rollback,backup-restore,incident-response}.md`, `docs/quality/migration-rehearsal.md`.

**Interfaces:** Import manifest records source snapshot, version, ID mappings, input checksums, rejected rows and reconciliation totals. Only one selected authority accepts writes. Workers are paused after restore until external acknowledgements/idempotency are reconciled.

- [ ] Inventory real versus simulated records and approved opening balances. Preserve original files/credentials privately; never migrate demo identities into production grants.
- [ ] Implement repeatable import with validation and reconciliation of counts, references, stock buckets/lots, open orders/deliveries, AR/AP, claims/advances, credits, journals, document sequences and evidence hashes.
- [ ] Rehearse on a restored staging copy, including repeated import and intentionally corrupt references. No dropped/rejected row is silently treated as success.
- [ ] Provision the approved frontend/API/worker/Mongo/storage/identity topology; verify same-origin auth, secrets, TLS, environment separation and readiness/alerts. Confirm actual paid features and capacity before relying on them.
- [ ] Test restore to the proposed RPO/RTO, with outbound workers paused and external acknowledgements reconciled. Verify usable evidence as well as restored database counts.
- [ ] Rehearse freeze→final snapshot/import→reconcile→switch and both pre-write rollback and post-write recovery. Disable all corresponding legacy writers together.
- [ ] Run representative concurrency/load tests and an independent security review. Document measured results and remaining launch blockers; commit runbooks/results.

**Acceptance:** A restore produces coherent money/stock/evidence and does not blindly resend acknowledged invoices/exports. The team can execute recovery from the written runbook. No production cutover occurs merely because code builds.

## T16 — Branch pilot, acceptance and controlled rollout

**Files:** Create `docs/quality/pilot-acceptance.md`, `docs/runbooks/operator-guide.md`, `docs/archive/legacy-retirement.md`; remove temporary legacy adapters only after retention/rollback conditions are met.

**Interfaces:** Operations/finance acceptance consumes the permission matrix, reconciliations, documents and measured reliability results. Release record identifies exact build, data snapshot, configuration and recovery owner.

- [ ] Train named operators using approved role-specific guides and recovery paths. Verify backup personnel can perform reporting and that independence rules are staffed.
- [ ] Pilot one selected branch with approved product/account/stock data. Complete order→partial delivery→proof→invoice→verified payment→reconciliation; exercise wrong delivery, refund and supplier exception.
- [ ] Rehearse period close with distinct submitter, approver and closer. Reconcile evidence, source registers and accounting output with finance.
- [ ] Obtain operational acceptance of pending business gates from spec §9 and confirm no critical/high launch blockers remain.
- [ ] Perform the approved production cutover using T15's runbook, monitor the pilot, then enable additional branches once pilot reconciliation is stable.
- [ ] Retire obsolete legacy writes/routes and demo-only public assets after the audit/rollback retention window; retain an authorized read-only archive rather than deleting required history.

**Done means:** All image/user requirements mapped to active tested functionality, six scoped role templates, verified account lifecycle, real Mongo-backed operations, correct stock and financial balances, traceable documents, timely billing controls, recoverable infrastructure and an accepted branch pilot.

## Scope after launch

Consider public retail ordering, full replacement POS/cash-drawer hardware, route optimization, direct banking/tax APIs, supplier portals and advanced analytics only after the B2B core is operating reliably. Barcode support, saved/repeat orders and bulk entry can be included earlier where their dependencies pass. Existing walk-in sales must still be reconciled before launch if they share inventory; deferring a POS replacement does not defer that integrity requirement.

## Handoff

This plan, the specification and audit are the planning deliverables. No application refactor, database migration, production account creation or deployment has been performed. Recommended execution method is module-by-module subagent implementation with independent review at the gates above. The first implementation action is T00's baseline preservation followed by the reversible structure change in T01.
