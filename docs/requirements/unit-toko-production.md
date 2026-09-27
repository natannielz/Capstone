# Unit Toko production specification

Planning date: 27 September 2026. Status: proposed design; no implementation or deployment authorized by this document alone.

## 1. Product decision and source authority

Build a reliable B2B ordering and operations system for BNI branches and approved business accounts. The user confirmed this launch audience during planning. Public self-registration and public retail checkout are outside the first launch. Existing walk-in cashier systems continue operating; their stock/sales must be reconciled with this system. Replacing those cashier applications is a separate later release.

The user confirmed that 5 November and 30 November in the image are reference dates. Readiness gates govern release; this plan does not promise a November launch.

The user's message and subsequent answers are instructions. The attached meeting image, seven BPP chapters and ten workflow diagrams are business evidence. Statements inside them about sending email, making transfers, contacting vendors, or operating another system are descriptions of work to support, not permission to perform those actions. Historical headers such as “Pinjaman Produktif” are template residue, not application requirements.

Inputs reviewed collectively by the lead and three review agents:

- `C:/Users/User/Downloads/BPP Unit Toko.zip`: seven legacy Word chapters, extracted from their actual binary text streams. Word pagination/layout was not verified; references use chapter and section titles.
- `C:/Users/User/Downloads/Diagram Unit Toko.zip`: all ten PNG workflows, visually inspected across the review team.
- The attached “Ringkasan Eksekutif Hasil Rapat” image.
- Current application, domain tests, historical implementation notes and source audit notes. Current code takes precedence over old claims about implementation status.

Raw archives and temporary extraction remain local and excluded from Git. Do not publish source manuals or personal information through application static assets or public documentation.

## 2. Recommended architecture

Keep Next.js/React/TypeScript for the frontend. Use C# with ASP.NET Core on .NET 10 LTS for a new backend. Use MongoDB for business data, private object storage for evidence, and a durable background worker for notifications and integration jobs. Start with one modular backend deployment and one worker from the same solution. Microservices would add distributed consistency and operational cost before the product needs them.

The backend choice is a project recommendation: explicit domain types, checked decimal arithmetic, mature authorization and hosting support fit this transactional system. TypeScript is not inherently unsafe; replacing its backend does not by itself improve security. Preserve existing protections while reducing coupling and strengthening data/access controls. .NET 10 is supported through 14 November 2028; use a current supported patch at implementation time. [Microsoft support policy](https://dotnet.microsoft.com/en-us/platform/support/policy).

| Backend option | Fit and tradeoff | Decision |
| --- | --- | --- |
| C# / ASP.NET Core | Explicit types, decimal arithmetic, established authorization and background-service facilities; rewrite and team learning cost | Recommended |
| Existing TypeScript backend | Lowest migration effort; can be secure with the same controls; retains much existing logic | Sensible fallback if C# cannot be maintained by the operating team |
| Go | Small runtime and explicit code; more authentication/accounting conventions to assemble | No clear project advantage over C# |
| Rust | Strong memory guarantees; substantial implementation and maintenance learning cost | Not justified for this application |

Use three main source directories. Repository metadata and platform entry points may remain at root; forcing `.gitignore`, CI workflows or README into documentation would break their purpose.

```text
frontend/
  src/app/                       # Thin Next.js routes and layouts
  src/modules/
    identity/ organizations/ catalog/ ordering/
    inventory/ procurement/ fulfillment/ finance/ administration/
  src/shared/                    # UI components, formatting, design tokens
  src/api/generated/             # Types/client generated from backend contracts
  public/                        # Public, licensed assets only
  tests/                         # Frontend unit, browser and accessibility tests
  package.json
  package-lock.json
backend/
  UnitToko.slnx
  src/UnitToko.Api/               # HTTP composition, versioned endpoints
  src/UnitToko.Worker/            # Durable outbox jobs and scheduled work
  src/UnitToko.Modules/
    Identity/ Organizations/ Catalog/ Inventory/ Ordering/
    Procurement/ Fulfillment/ Billing/ Accounting/ Documents/
  src/UnitToko.Platform/          # MongoDB, object storage, telemetry, clock
  contracts/                     # OpenAPI and versioned integration schemas
  migrations/                    # Import tools and reconciliation manifests
  tests/                         # Domain, Mongo integration, security, load
  deploy/                        # Containers, local replica set, infrastructure
  legacy/                        # Temporary TypeScript service; remove after cutover
docs/
  requirements/ architecture/ decisions/ quality/ runbooks/
  superpowers/plans/ design/ archive/
  source-material/README.md      # Source inventory; private originals excluded
README.md
.gitignore
.github/workflows/               # Required root-level CI entry points
```

Tests and executable infrastructure belong beside their runtime owners. “Documentation and everything else” includes plans, designs, business references, release notes and runbooks; it does not mean placing backend deployment code in prose folders.

### Module boundaries

| Module | Owns | Public operations and boundaries |
| --- | --- | --- |
| Identity | Provider identities, app sessions, invitations, grants | Resolve actor, invite, suspend, revoke; never decides financial policy |
| Organizations | Cooperative, buying organization, branch, division, fulfillment shop, warehouse, membership, addresses | Resolve authorized scope; no organization selected by arbitrary client ID alone |
| Catalog | Product families, SKUs, images, pack contents, source-system mapping, versioned prices | Search and price snapshots; does not own stock counts |
| Inventory | Lots, bins, expiry, quantities, reservations, stock movement journal, quarantine, stocktakes | Reserve/release/dispatch/receive/convert; only owner of physical stock writes |
| Ordering | Draft/submitted orders, buyer approvals, substitutions, cancellations, quote/custom-logo request | Owns requested quantities and price agreements, not shipment receipt |
| Procurement | OMI automatic records, DDO, Smart orders, Pasar Kering, vendor quotes, PO/receipt/SPH match, supplier claims/advances | Records external facts and procurement approvals; invokes Inventory for actual receipts |
| Fulfillment | Pick/pack, dispatch, courier assignments, shipments, delivery exceptions, receipt, after-sales cases | Owns actual delivery quantities and recipient evidence; invokes stock/financial interfaces |
| Billing | Invoice-ready queue, issued invoices, customer payments, allocations, credits, refunds, reminders | Invoices accepted eligible quantities; never directly edits finalized journals |
| Accounting | Chart mappings, immutable postings, source reconciliation, periods, review/approval/close | Balanced postings, reversals, controlled period close and exports |
| Documents | Immutable document versions, evidence metadata, QR challenges, email jobs, delivery logs | Private storage and durable notification delivery; business eligibility stays with owning module |

Each module exposes an application interface and owns its collections. No cross-module direct writes or shared global `State` object. Coordinated inventory/order/billing/accounting changes use a shared transaction context within the modular monolith. Notifications run from an outbox after commit. Browser responses contain purpose-specific, scoped data rather than the entire company state.

## 3. Roles: six launch templates with explicit grants

Roles are understandable starting templates, not a substitute for permissions. Every endpoint, list, search, export, file download and event subscription checks permission, organization/branch/shop scope, ownership or assignment, record state and independent-approval rules. Default is deny. [OWASP authorization guidance](https://cheatsheetseries.owasp.org/cheatsheets/Authorization_Cheat_Sheet.html).

| Template | Allowed duties | Explicit restriction |
| --- | --- | --- |
| Buyer / PIC | Approved business purchasing; own authorized orders, substitutions, receipt, invoices, cases and payment evidence | Branch-wide visibility and recipient authority require a named `branch.pic` grant; ordinary business buyer sees own permitted organization records only |
| Staf Toko / Kasir | Receive stock, pick/pack, create Surat Jalan, stock count, source-system sale entry and import preparation | Assigned shop/warehouse/channel only; no own adjustment approval or independent finance verification |
| Kepala Toko | Operational review, catalog/pricing, procurement decision, stock adjustment review, funding requests | Cannot approve own financial requests; no global identity administration |
| Kurir | Assigned deliveries, failed delivery, evidence, returned goods and own expense requests | No unrelated routes, complete buyer financial history or acceptance on behalf of an unverified recipient |
| Finance | Finance workspace with separately assigned packs below | No blanket finance superuser grant |
| System Admin | Invitations, memberships, role configuration, recovery administration, technical configuration | No default stock/payment/journal editing; no automatic access to transaction content |

Customer and PIC share a buying interface because the launch is B2B-only. Their scope is not silently merged: former public demo customers are not migrated as approved organizations. Ordinary membership defaults to self-created or explicitly assigned records. Organization-wide visibility/purchasing requires a separate scoped grant, and PIC designation names the exact branch. A business owner must approve organization membership and PIC designation. Keep separate personal/branch ownership rules even if they use one workspace.

BPP VII gives cashier activity to Staf Toko, so do not introduce another cashier role. Finance consolidates four workspaces while preserving four distinct authorization packs:

| Pack | Migration | Powers |
| --- | --- | --- |
| Reporting | `laporan` | Reconcile daily OMI/Smart sources; prepare/submit reports |
| Billing | `penagihan` | Issue invoices; record/identify/allocate incoming funds; request credits/refunds; record an approved disbursement |
| Approval | `pimpinan` | Review/approve/reject financial requests and submitted periods |
| Accounting | `akuntansi` | Verify independent reconciliations, post/reverse journals, close approved periods |

Payment verification is a separately granted `payment.verify` permission, normally assigned to an independent finance reconciler. A finance template alone grants no permissions. `laporan` and `penagihan` may be combined where the person performs both similar preparation duties; approval and accounting packs do not silently follow. Store role assignments as explicit versioned grants with approver, reason, effective time and scope.

### Independence rules

- Payment recorder cannot verify that payment, even after receiving another role.
- Refund/credit/funding/expense requester cannot approve their request. Refund approver cannot record its disbursement. The operator entering a manual bank payout reference requires an independent verifier before paid status is final.
- Stocktake counter/proposer cannot approve their own adjustment.
- A period submitter, approver and closer are three different actors. Reopening requires an independent approved action and a new revision.
- No user grants privileged permissions to themself. Privileged grants require a second designated access approver; technical admin alone cannot bypass business policy.
- Dual membership does not bypass actor checks. Small staffing levels require a separately assigned reviewer, not disabling the checks.

## 4. Identity and application security

Selected identity implementation: Auth0 through a managed OpenID Connect boundary. On 28 September 2026 the user confirmed no existing approved provider and requested the planned Auth0 integration. Keep organizational authorization in MongoDB and credentials/factor secrets in the provider. Validate provider region, MFA entitlement, organizational policy and operating cost before selecting a paid plan; no claim is made that every feature is free. A future approved enterprise provider may use the same boundary after its own acceptance review. Do not assume an official BNI SSO connection exists.

This avoids inventing a password/MFA platform or assuming ASP.NET Core's default relational Identity stores work with MongoDB without an adapter. Auth0 documents [email verification](https://auth0.com/docs/manage-users/user-accounts/verify-emails) and [organization invitations](https://auth0.com/docs/manage-users/organizations/configure-organizations/invite-members). Provider login does not itself authorize branch access.

Required behavior:

1. Invite/approve accounts; disable open signup. Verify email before business access. Normalize identifiers; never auto-link accounts solely by matching email.
2. Require authenticator-app MFA for staff, PICs, finance, admin and business buyers with transaction permissions. Offer passkeys where supported and validated; never treat an unverified `mfaEnabled` flag as proof of a completed challenge. Email verification is not the second factor.
3. Use authorization code + PKCE, state and nonce checks, exact redirect allowlists and provider signature/issuer/audience validation. Backend holds tokens; browser gets an opaque HttpOnly, Secure cookie. Same-origin `/api` routing is the preferred deployment. Use a callback-compatible SameSite policy plus antiforgery protection, not a cookie setting that breaks the OIDC return flow.
4. Default session policy proposal: 30-minute idle timeout, 8-hour absolute lifetime; fresh MFA within 5 minutes for privileged grants, factor recovery and refund authorization. Validate provider authentication time and assurance/factor evidence on the server; a new application cookie or silent SSO using old authentication does not satisfy freshness. Force provider step-up when needed. Configure these values centrally; do not duplicate them in UI code.
5. Owner-controlled single-use recovery; expire invitations/recovery links; revoke sessions after password/factor changes, disablement or privilege removal. Provider back-channel/events plus application grant-version checks enforce revocation across API instances. Authenticate and deduplicate provider events. Sensitive mutations conditionally write the actor's authorization fence in the same transaction as the business decision; suspension/grant changes update that fence too, causing stale in-flight operations to conflict and reauthorize. A read of a cached grant version alone is insufficient.
6. Recovery codes and helpdesk verification follow provider controls. Admin initiates recovery but cannot choose/read a user's new permanent password. Emergency access is named, time-limited, separately logged and does not bypass transaction independence.
7. Enforce account and IP/risk throttles; generic account responses; strict request schemas and streaming body limits; allowlisted Mongo filters rather than accepting arbitrary query objects; protect against object-level authorization errors.
8. HTTPS, HSTS, CSP, restrictive CORS, CSRF protection, secure forwarded-header configuration, safe output encoding and secure dependency/secret scanning.
9. Upload limits by media type, private quarantine, malware/content validation, safe filenames and object keys, signed short-lived authorized downloads, quotas and retention. Supplier unboxing video is a separate evidence policy with larger limits and asynchronous scanning; do not force it through the existing small image/PDF limit.
10. Audit login, factor/recovery events, permissions, privileged reads/exports, financial decisions and evidence actions. Include actor, resource, branch, timestamp, reason, correlation ID and safe before/after changes. Redact secrets and minimize personal data.

## 5. MongoDB and data integrity

The current app already uses persistent libSQL/Turso. Migration changes the storage architecture; it is not the first introduction of a database.

Use MongoDB Atlas or an equivalently operated replica set with backups, private/restricted networking and a least-privilege application identity. Local development/CI must also use a replica set: standalone MongoDB cannot run the required multi-document transactions. [MongoDB production transaction considerations](https://www.mongodb.com/docs/manual/core/transactions-production-consideration/).

Collections are bounded business aggregates: identities, memberships, organizations, branches, divisions, shops, warehouses, productFamilies, skus, priceVersions, lots, stockBalances, movements, reservations, orders, shipments, receipts, cases, purchaseOrders, goodsReceipts, supplierInvoices, supplierClaims, invoices, payments, allocations, credits, refunds, journalEntries, periods, documentVersions, auditEvents, idempotencyRecords, outboxEvents and importBatches. Split large lines/event histories into bounded child collections. Never store the whole company in one document.

Data contracts:

- Enforce validation in both application commands and Mongo collection validators; schema versions and reviewed migrations are mandatory. MongoDB supports [schema validation](https://www.mongodb.com/docs/manual/core/schema-validation/).
- Honor the image's Integer/Long request using BSON Int64 internal domain IDs allocated by atomic per-entity counters. Gaps are acceptable; never derive a new ID from record count or reuse IDs. Use random public resource IDs and non-guessable QR tokens separately. Serialize Int64 IDs as decimal strings in JSON so JavaScript cannot round them above `2^53 - 1`.
- Human invoice/Surat Jalan numbers have separate unique sequences scoped to entity/channel/year. Do not claim gapless numbering unless finance mandates it; preserve cancelled-number records and reasons.
- Use C# `decimal` / BSON Decimal128 and explicit currency/rounding policy for money; serialize amounts as decimal strings. Define a shared application subset: nonnegative input unit amounts and document totals up to `999999999999999.99`, at most two fractional digits; signed journal amounts use the same absolute bound. Integer quantities are at most `1000000` per line, with checked totals still inside the money limit. Parse invariant decimal strings and reject excess precision/range at API, import and storage boundaries rather than silently rounding. Percentage/rate values use a separately bounded decimal contract (0–100, up to four fractional digits); round calculated tax using the finance-approved document policy. Decimal128 has a wider range/precision than C# decimal, so storage validity alone is insufficient. Never use JavaScript floating point for authoritative tax/total calculations. Decimal128 avoids binary floating approximation. [MongoDB monetary data guidance](https://www.mongodb.com/docs/v7.3/tutorial/model-monetary-data/).
- Validate quantity, unit value, line total, tax and document total before accepting a command. Define one supported range, use checked arithmetic, and test its boundary through fulfillment/invoicing, not only checkout.
- Store timestamps in UTC and business dates in `Asia/Jakarta`; inject a clock in tests. Expiry and month-close logic use the agreed business date.
- Important unique keys: provider issuer+subject; normalized business account identifier; organization+SKU; document number scope; sourceSystem+externalTransactionId+lineId; invoice source receipt-line allocation; accounting source event; actor+operation+idempotency key; document+version.
- Important query indexes include scoped status/date, branch+order date, SKU+warehouse+lot expiry, outstanding invoice due date, assigned courier+delivery date, source reconciliation status, worker due time. Use explain plans against representative data. [MongoDB C# index guide](https://www.mongodb.com/docs/drivers/csharp/current/indexes/).

Stock reservation, dispatch, receipt, invoice issuance, cash allocation, refunds and journal posting use conditional updates/version checks and transactions. Store idempotency request hash, actor/scope and result in the same transaction. A repeated identical request returns the original result; reuse with a different body returns 409. Handle transient transaction and uncertain-commit results through the official driver's transaction mechanisms, without duplicating external side effects.

Money and stock cannot depend on background delivery. Persist the business write plus outbox entry atomically; workers claim leases, retry with backoff and deduplicate messages. An email outage does not roll back an invoice or trigger another stock reservation. An accounting failure in a synchronous financial transaction rolls back the complete business operation.

## 6. Workflows and image requirement mapping

| Requirement | Required production behavior | Implementation task |
| --- | --- | --- |
| Three clear folders; well-separated modules | Structure in §2, module ownership/contracts, separate frontend/backend checks | T01–T02 |
| Three-agent search for bugs/features/security | Completed audit with evidence; findings become acceptance gates | T00, audit document |
| Defined and simplified roles | Six templates, scoped grants and independent finance permissions | T03–T04 |
| MongoDB and backend language | C# API/worker; Mongo collections, transactions, migration/reconciliation | T02, T05, T15 |
| MFA and email verification | Approved account lifecycle and provider-backed factors/recovery | T03 |
| Branch procurement replacing WhatsApp | Named PICs, catalog, saved drafts, submitted orders, approval history and in-app exceptions | T04, T07 |
| OMI vs S-Mart cashiers | Explicit source channel/register mapping; separate suborders/posting/export queues; consolidated reconciliation | T06–T07, T12 |
| Real-time stock | Server-authoritative reservations; push invalidation plus refetch and recovery | T06, T13 |
| SKU unit/carton grouping | Explicit family/variant/UOM/barcode/pack ratio; audited conversion | T06 |
| Tablet/QR signature for Surat Jalan | Recipient-authenticated, shipment-version-bound proof, immutable document | T09 |
| Wrong deliveries and complicated refunds | Actual picked/delivered/accepted quantities, substitution consent, after-sales case, controlled credit/refund | T07–T11 |
| Missing Surat Jalan | Private immutable copy, searchable document register, proof/acknowledgement trail | T09 |
| Late invoices and >30-day collections | Invoice-ready SLA queue, sending/retry tracking, aging, reminders, ownership/escalation | T10, T13 |
| Integer/Long IDs | Int64 internal ID plus lossless string API representation | T05 |
| November milestones | Reference dates only, superseded by user clarification | Release gates |

### Catalog, packaging and two cashier channels

OMI/S-Mart is an operational source dimension, distinct from product family/category or user role. BPP II/III includes galon/water in Smart, so do not hardcode “Smart = merchandise only.” Each SKU has a validated source mapping and external SKU where one exists.

A mixed basket remains one buyer experience but creates source-separated suborders/fulfillment/posting records. Default invoice grouping is buying organization + billing period + source channel, with one consolidated statement linking those invoices. Finance can approve another grouping policy before launch; source traceability must remain intact.

Product family, SKU, unit of measure, pack contents, barcode, image, description and price are editable master data. Default: sealed cartons and loose units have separate physical balances; breaking/repacking a carton requires an audited conversion that decreases one balance and increases the other by the defined ratio in one transaction. Preserve lot/expiry/cost provenance. No inferred conversion from product names and no double-counting the same stock.

### Ordering, inventory and procurement

Keep draft/reorder, branch addresses, optional purchase reference and explicit submission. Snapshot agreed prices, tax policy, buyer/seller details and delivery address. Require buyer confirmation for substitutions or price-changing revisions. Support partial fulfillment, shortages, remainder cancellation and linked alternative purchases. Pending exceptions are work queues with an owner and timestamp.

Inventory owns on-hand, reserved, available and held/expired quantities. Lot/expiry selection, quarantine and supplier-return policies are explicit. Record physical movement immediately even when accounting finalization awaits dispute resolution; delayed paperwork must not leave stock available for sale again.

Preserve all four procurement routes: OMI regular replenishment, DDO requests and partial deliveries, Smart/vendor/custom-logo orders, and Pasar Kering fallback/funding/transport costs. External email/phone requests remain recorded references until an approved connector exists. Add supplier PO-receipt-SPH line matching, discrepancies/evidence, unused advance refund, supplier-return credit/replacement/cash settlement. No unexplained residual supplier receivable or advance can be marked reconciled.

Receiving supports counted containers, pending item inspection, unboxing evidence, damaged/missing quantities and put-away location. Item-level inspection and claim deadlines can be configured per supplier. Rejected expired returns remain quarantined until a documented disposition decision; do not automatically put expired goods back for sale merely because a source diagram depicts that historical practice.

### Delivery proof and after-sales

Surat Jalan derives from actual dispatch quantities. Courier sees only assigned work, with mobile/tablet controls, delivery exceptions and a document register. A QR challenge is random, stored hashed, expires (default 15 minutes), binds shipment+version+intended recipient scope, and is single-use. Scanning opens a confirmation flow; scanning alone is not acceptance.

The recipient authenticates as an authorized Buyer/PIC. On a courier tablet use a separate recipient session or a restricted delivery confirmation challenge sent only to a preverified contact of an authorized organization member, never a courier-entered address/number. Bind it to recipient and shipment/version, apply expiry and attempt limits, and isolate it from the courier session. Delivery OTP authorizes only that receipt and is not a substitute for required account MFA. The courier's logged-in identity cannot stand in for the recipient. Record recipient, acceptance text/consent, actual received quantities, discrepancy, signature capture where used, server time, evidence hash and document version. Do not claim a drawn signature is a certified electronic signature. Offline work may save a clearly marked draft, but final receipt requires server validation. Lost responses can be retried without duplicate receipts.

Allow a buyer to open an after-sales case after finalization. Preserve original accepted quantities/documents and resolve through linked return authorization, received-return inspection/quarantine, credit and refund/replacement records. Customer return windows/conditions need a business policy; BPP IV governs supplier expiry returns, not customer entitlement. Until a policy is approved, permit case submission/manual review but do not auto-promise returns/refunds.

### Billing, payment, reporting and close

Eligible accepted quantities enter an invoice-ready queue after required checks. Proposed operational target: raise an alert when still unbilled after one business day; finance owns the queue. This is a proposed SLA, not an assertion from the manuals. Capture received/finalized/ready/issued/sent/delivered-or-failed/acknowledged timestamps.

Payment terms are configurable by contract. The source's 30-day concern does not establish whether the clock starts at delivery, issue or receipt; finance must choose before real invoices. Until then, default is a visible draft setting of 30 days from issue for staging only. Separate payment state (unpaid/partial/paid/credited) from aging (not due/due/overdue). Credit/payment with a remaining overdue balance stays overdue.

Use actual immutable invoice templates/numbering, official seller information and finance-approved tax configuration. Record/link approved tax documents; do not relabel the current simulation as a valid tax invoice. No tax rate or legal certification is inferred by this plan.

Keep B2B bank transfer/invoice terms. A consumer gateway is not required. Import bank statements with preview and deduplication, preserve unmatched/suspense funds, suggest allocations but require authorized confirmation. Partial/multiple-invoice payments, overpayment, credits and unallocated cash must reconcile. Recording a bank reference does not execute a transfer. Mark refunds paid only after evidence and independent verification; outbound transfers remain with authorized banking operators unless a separately approved integration is added.

OMI/S-Mart import/export uses staged files, schema validation, external source references, preview, totals and deduplication. Web-originated sales exported to a cashier must be tagged and linked so a later import does not decrement inventory or post revenue twice. Opening stock + receipts - all channel sales ± approved adjustments must reconcile to physical stock. Define the authority and handoff for each channel before enabling order promises; if external stock is stale, show its age and restrict availability/reservations using the agreed safety policy.

Accounting preserves balanced journals, unique source events, explicit reversals and locked periods. Close requires reconciled inventory, AR/AP, credits, unallocated cash, documents and source totals or specifically approved exceptions. Supplier debt and advances are included. The reporting, approval and accounting sequence in BPP VI remains visible and independently controlled.

## 7. Quality and operations gates

These are proposed measurable launch targets, not current capabilities or contractual guarantees:

- No unresolved critical/high security or money/stock integrity defect. Test all allow/deny cells, cross-branch ID substitutions, exports, file access and event subscriptions.
- Zero negative sellable balances, duplicate invoice sources, duplicate verified cash entries or unbalanced journals under concurrency/retry tests.
- Staging reference load: 100 concurrent users, 10,000 SKUs and 100,000 historical orders; p95 scoped reads ≤500 ms and ordinary writes ≤1 second excluding upload/provider latency, <1% unexpected server errors. Tune after actual volume measurements.
- Authorized online stock/order views converge within 5 seconds after commit; disconnected views clearly show stale state and refetch on reconnect. Server validation always wins over the displayed number.
- Responsive buyer/staff/courier tasks at 360px, tablet and desktop sizes; complete keyboard operation; meaningful labels/error summaries; reduced motion; contrast and focus reviewed against WCAG 2.2 AA. Proposed field targets: LCP ≤2.5s, INP ≤200ms, CLS ≤0.1 at the 75th percentile.
- Draft recovery, loading/empty/error states, clear pending/retry status, no duplicate submission, accessible tables, search/filter/pagination and actionable dashboards. Add repeat orders, bulk SKU entry and barcode picking after core flow reliability; avoid decorative complexity that slows work.
- Separate development/staging/production databases, identity tenants/configuration and storage. Production has no demo seed, credentials or sample balances. Explicit migrations run outside user requests.
- Log correlation IDs, latency, failures, auth events, worker lag, failed invoices, stock discrepancies, overdue amounts and backup health. Alerts have a named operational owner and response runbook.
- Proposed initial recovery objectives: RPO ≤15 minutes and RTO ≤4 hours. Choose a hosting/backup tier that can meet them, and demonstrate a restore including object evidence plus database reconciliation. Restore with workers/outbound integrations paused; reconcile restored outbox/idempotency records against external acknowledgements before resuming, so previously sent invoices/exports are not blindly replayed. If the selected tier cannot meet the recovery targets, revise the service agreement before launch.
- Encrypted secrets outside source control, rotation procedure, dependency patch process, CI security checks, immutable deployment artifacts and staged rollouts.
- Pilot one selected branch, process a complete order→delivery→invoice→payment→reconciliation cycle, exercise a refund/return, and rehearse period close. Add remaining branches only after signoff and monitored pilot stability.

## 8. Migration and deployment policy

The current Vercel deployment is not assumed to host a .NET process. Keep Next.js on a compatible host (Vercel is an option), run API+worker on a container-capable host, and route a single public origin through an explicit gateway/reverse proxy. Validate cookie/CSRF and long-lived event transport behavior through that actual topology. If the gateway cannot reliably carry push events, use bounded polling initially or host frontend/API behind the same suitable proxy.

Do not migrate by renaming files and declaring completion. First preserve existing backend code under `backend/legacy`, with thin Next server adapters for route compatibility until the new service takes over. This is a temporary shared runtime, not the final architecture. Move UI calls behind a generated versioned API contract. New identity/catalog reads can be tested in shadow mode while legacy remains authoritative. An early authority switch requires disabling the old domain's mutations and a tested bridge that supplies current grants, SKU/price versions and ID mappings to all legacy commands; mappings alone are insufficient. Prefer switching transactional identity/catalog authorization with the full business cohort if the bridge would add complexity. Inventory/order/procurement/fulfillment/billing/accounting and every other stock/money writer switch as one consistent business data cohort unless a separately reviewed protocol demonstrates equivalent consistency. Never let two systems independently write the same stock or money records.

Inventory source data before importing. Demo rows stay demo. If real records are discovered, preserve them and obtain a reviewed mapping/opening balance sheet. Use deterministic legacy→new ID mapping, idempotent importer versions, rejected-row reports and checksums. Reconcile counts, stock buckets/lots, open orders, actual deliveries, outstanding AR/AP, advances, credits, journal balance, sequence high-water marks and attachment hashes.

Rehearse cutover in staging. During production cutover freeze legacy writes, take a verified backup, run final import/delta, reconcile and switch traffic. Preserve old data read-only. Before new production writes, traffic can safely revert to the frozen old system. After new writes, do not blindly switch back to a stale database: enter maintenance, preserve/export new writes and reconcile a reviewed reverse migration or fix forward. Assign a recovery owner and rehearse this distinction.

## 9. Decisions needed before affected implementation or launch

These are bounded business gates, not reasons to stop planning or the structural work:

| Decision | Recommended preparation/default | Owner and required point |
| --- | --- | --- |
| Real branch/division/shop/account list and PICs | Model multi-membership; stage synthetic Tebet/Pancoran/Cawang records | Operations, before real master-data import |
| Finance staffing and independent approvers | Six templates plus four packs; actor independence always enforced | Business owner, before real permission grants |
| Identity provider/hosting/budget/region | Managed OIDC + .NET host + Mongo replica set + private storage; price against workload | Owner/IT, before external provisioning or paid plan selection |
| OMI/S-Mart stock authority and file/API access | Source-linked staged imports and acknowledged exports; live shared stock only when reconciled | Store/source-system owners, before multi-channel selling |
| Due-date origin, invoice grouping and tax treatment | Staging 30-days-from-issue; source-separated invoices + consolidated statement | Finance, before real invoices |
| Customer return conditions and refund SLA | Case intake and manual decisions; no automated entitlement promise | Operations/finance, before real after-sales decisions |
| HET rule | No inferred floor/ceiling; explicit price approval | Operations/finance, before pricing automation |
| Signature acceptance and retention | Authenticated evidence + immutable version; retention configurable | Organization owner, before replacing paper evidence |
| Existing real data and document retention | Inventory first; never seed over it; opening balances reviewed | Operations/accounting, before migration |

No credentials, subscription purchases, vendor contacts, transfers or production changes occurred during this planning task.

## 10. Consolidated report requirements — 28 September 2026

The [97-item report requirements and acceptance map](unit-toko-report-2026-09-28.md) is an authoritative addendum to this specification. Execute it through the [report completion plan](../superpowers/plans/2026-09-28-report-completion.md) alongside the original plan. It makes payment image evidence/local OCR/human bank verification, explicit package selection, immutable versions and remaining access/workflow/quality controls concrete while preserving completed foundations and conditional advanced-ML applicability. Its bounded decision gates extend §9 without supplying unapproved business policies.

The active production goal includes this scope through its referenced specification and master plan. Completion requires evidence for both original requirements and applicable report rows, not only a passing build or local provider simulation.