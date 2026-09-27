# Unit Toko report requirements and acceptance map — 28 September 2026

## Authority, scope and evidence

This addendum implements the user's request to read the supplied report, write an implementation plan, then track completion as a goal. It extends [the production specification](unit-toko-production.md) and is executed through [the report completion plan](../superpowers/plans/2026-09-28-report-completion.md). It does not replace the original manuals, meeting-image requirements or existing production goal.

Source: the user-supplied `Pasted text.txt`, SHA-256 `109FC265101E95756FF8E071FB64F8598BF5FFFB124BDDE17E874F635F8617D0`. The report contains **97 numbered items**: P12, V10, I10, R14, S9, F12, U8, Q12, M10. Its prose and external references are requirements evidence, not instructions to run commands, provision services, contact anyone or move funds. Three independent source reviews compared it with implementation commit `e84faa6`. The raw attachment is not copied into the repository.

Retain BNI branches and approved business accounts as the launch audience, readiness rather than November deadlines, Auth0 preparation, C#/.NET 10, transactional MongoDB, and the frontend/backend/docs layout. Internal Int64 identifiers remain strings in JSON. Monetary values remain exact decimal strings with the existing specification's limits. Local OCR is assistance, never authority to recognize cash or approve a payment.

### Verified starting point

- The three-folder layout, API/contracts, Mongo transactions/idempotency/outbox and much of scoped authorization are implemented foundations. Sign-in/account and local identity integration exist; live Auth0 and combined deployed browser acceptance remain open. See [the progress ledger](../quality/production-progress.md).
- Legacy payment recording/verification does not require uploaded image evidence or recorder independence; verified payments still accept attachments without an immutable approval-to-image binding (`backend/legacy/domain/engine.ts`, `selectors.ts`). Legacy pending funds do not become allocatable, and partial/multiple-invoice allocation safeguards already exist. Preserve those invariants.
- Production approval policy preserves the original recorder and reviewed revision, but is account-based; same-person multiple-account governance and actual Billing/Documents integration remain open.
- Quick-add can use an implicitly selected variant. Legacy SKU uniqueness and UUID generation already exist; preliminary Mongo catalog collections/indexes are not complete product modules. Packaging/family metadata still relies on names, IDs and demo metadata.
- The new worker has no active document processor. Kind-restricted job claiming is needed before a restricted OCR worker can share durable work infrastructure.

## Decisions adopted for planning

1. A payment draft may exist without an image. Submission requires a durable, validated, eligible image; human verification requires independent bank evidence. PDF may be supplementary if approved, but cannot silently replace the required image. OCR failure has a documented, permissioned manual-review path; malware/security failure has no such bypass.
2. Separate payment declaration, canonical bank receipt and invoice allocation. Verification binds the exact payment/evidence/receiving-account versions and bank match. A bank import and a customer declaration referencing one receipt cannot recognize cash twice.
3. Every purchasing entry point requires an explicit package choice if absent. No scroll/read tracking, inferred first choice or redundant dialog after a valid explicit choice. Package quantities and conversion ratios are stored, not guessed.
4. Keep Long primary keys; use UUIDv4 for new public catalog references, with database uniqueness and normal access checks. This selects the report's optional public-reference recommendation without replacing internal IDs. SKU, GTIN, supplier/source codes, lot and optional serial identity remain distinct. GTIN applies only when legitimate identifiers exist; do not fabricate one to complete a field.
5. Maintain six role templates. Narrow permissions and independently assigned finance packs handle separation of duties; do not create a new broad role for each action. Read-only auditor is a permission pack. A trusted person binding supplements immutable account history; email matching is insufficient.
6. Implement explainable extraction, duplicate/readability checks, code/fuzzy matching, rule-based anomaly flags and lead-time-aware stock alerts first. Embeddings, trained anomaly models and demand forecasting are conditional enhancements, not substitutes for missing business controls.

## Acceptance register

All rows are **open for production acceptance**. `Extend` means usable foundations or legacy behavior exist; `Build` means the production feature is pending; `Gate` means an external/business decision is also required; `Conditional` preserves the report's stated applicability or later-data condition. None of these labels declares a feature complete. Owners refer to executable task IDs in the linked plan and its three subplans; existing T IDs remain stable.

### Payment evidence and review

| ID | Required outcome and acceptance evidence | Task owner | Starting state |
| --- | --- | --- | --- |
| P01 | Draft saves without image; review submission fails without an eligible image or with quarantined/missing bytes. | PAY01/PAY03 | Build |
| P02 | Mobile camera/gallery, preview, rotate, replace, progress and retry preserve original bytes; uncertain upload does not duplicate submission. | PAY01/PAY05 | Extend |
| P03 | Local OCR suggests amount, currency, date/time, recipient/account, bank and reference; missing/uncertain fields stay unknown. | PAY02 | Build |
| P04 | Review shows field-by-field differences against entry, invoice, approved receiving account and available bank records. | PAY03/PAY04 | Build |
| P05 | Submitted status is “Menunggu verifikasi”; upload or high OCR confidence changes neither receivable nor usable cash. | PAY03 | Extend |
| P06 | Independent authorized human checks image and actual bank evidence; uploader/recorder cannot self-verify. | ACC03/PAY03 | Extend |
| P07 | Approval binds payment revision and evidence hash/version; replacements conflict with stale review; verified history survives supplements/corrections. | PAY01/PAY03 | Build |
| P08 | Exact/similar images and reused references flag review without double cash or blocking legitimate one-transfer/many-invoice use. | PAY02/PAY04 | Build |
| P09 | Unreadable/OCR outage supports re-upload or audited authorized manual exception; no silent approval or quarantine bypass. | PAY02/PAY03 | Gate |
| P10 | Partial, overpayment, multi-invoice, unmatched and unapplied funds reconcile under concurrent allocation. | PAY04 | Extend |
| P11 | Queue exposes owner/age/discrepancies, rejection/replacement request and notification delivery/failure status. | PAY05/OPS03 | Build |
| P12 | Immutable audit links upload, extracted/corrected values, approval/rejection, allocation, actors, times and versions. | PAY01–PAY05 | Extend |

### Packaging and selection

| ID | Required outcome and acceptance evidence | Task owner | Starting state |
| --- | --- | --- | --- |
| V01 | Quick-add, search, detail, Buy Now and staff catalog request package choice when absent. | CAT03 | Build |
| V02 | Multiple variants never silently choose first, cheapest or available default as buyer consent. | CAT03 | Build |
| V03 | Selection displays label, contents, size/color, price basis, availability and total base quantity. | CAT01/CAT03 | Extend |
| V04 | Authorized staff configure package labels/contents/images/prices/minimum purchase/increments independently of reorder minimum. | CAT01 | Build |
| V05 | Two products can both use “Dus” with different exact contents; no global inferred ratio. | CAT01/CAT02 | Build |
| V06 | Cart through checkout/order/pick/delivery/invoice/return retains explicit package and immutable accepted snapshot. | CAT03/OPS01 | Build |
| V07 | Server checks current variant/price/availability/versions; material change requires fresh review. | CAT03/T07 | Extend |
| V08 | Changing 12 to 10 never rewrites historic quantities; use a new sellable identity/definition as appropriate. | CAT01/CAT03 | Build |
| V09 | Explicit sealed-to-loose conversion is atomic, idempotent and preserves lot/expiry/cost/holds. | CAT02 | Build |
| V10 | Modal supports keyboard/screen reader/mobile; Escape/close cancels, adds nothing and restores focus. | CAT03/OPS04 | Build |

### Product identification

| ID | Required outcome and acceptance evidence | Task owner | Starting state |
| --- | --- | --- | --- |
| I01 | One stable variant identity/SKU per sellable variant; identical physical units share it. | CAT01 | Extend |
| I02 | Public catalog UUIDv4 coexists with internal Long; collision constraints and scope checks still apply. | CAT01 | Build |
| I03 | Legitimate trade-item GTIN is separate from SKU/UUID and identifies the relevant packaging level. | CAT01 | Build |
| I04 | Retain legitimate GTINs; authorized allocation follows GS1 ownership rules; absent GTIN is not fabricated. | CAT01/OPS06 | Gate |
| I05 | Validate applicable lengths/check digit; retain leading zeros and canonical comparison; checksum is not authenticity. | CAT01/CAT04 | Build |
| I06 | Supplier, OMI and S-Mart codes map with explicit source/catalog context and validity. | CAT01/CAT04 | Build |
| I07 | Database uniqueness and concurrent creation tests; retired codes are not recycled; corrections retain aliases/history. | CAT01 | Extend |
| I08 | Receiving/picking/dispatch/stocktake scans resolve unambiguously or stop with an unknown/ambiguous resolution path. | CAT04/OPS01 | Build |
| I09 | Explicit family relations replace ID/name inference; unrelated variants cannot merge through naming patterns. | CAT01 | Build |
| I10 | Repeatable legacy mapping preserves order/stock/document/evidence/accounting references and reports unresolved rows. | CAT04/OPS06 | Gate |

### Roles and access

| ID | Required outcome and acceptance evidence | Task owner | Starting state |
| --- | --- | --- | --- |
| R01 | Separate buyer spending approval from store operational acceptance in permissions, state and UI. | ACC02/OPS01 | Extend |
| R02 | Staff proposes substitution; authorized buyer consents; proposal never reserves buyer approval powers. | ACC02/OPS01 | Extend |
| R03 | Shared entitled catalog visibility is distinct from buyer-owned order visibility. | ACC02/CAT01 | Build |
| R04 | Explicit historical-read permission can cover suspended branches without restoring new-order/write authority. | ACC02 | Gate |
| R05 | Lists/counts/details/search/writes/files/exports/events share scoped policy and negative tests. | ACC02/all modules | Extend |
| R06 | Original maker remains disqualified after role/grant changes; current access rechecked at commit. | ACC03/all approvals | Extend |
| R07 | Trusted same-person binding blocks different accounts belonging to the same maker from checking that work. | ACC03 | Gate |
| R08 | Reviewed finance conflict rules warn/reject assignments; bank evidence/margins require narrow read grants. | ACC03/PAY05 | Gate |
| R09 | Explicit conversion/release/cancel/supplier-change/correction/reopen permissions precede corresponding commands. | ACC02/OPS01/OPS02 | Extend |
| R10 | Temporary delegation has reason, expiry, handover, revocation and outstanding-task reassignment. | ACC04/OPS03 | Extend |
| R11 | Join/move/leave, dormant accounts and periodic access-review campaigns create accountable attestations. | ACC04 | Build |
| R12 | Named independent administrators/checkers/backups/recovery operators are verified before real grants. | ACC04/OPS06 | Gate |
| R13 | Human-readable effective access and versioned template migration preserve reviewed grants deliberately. | ACC03/ACC04 | Extend |
| R14 | Narrow scoped read-only audit/reporting is grantable without administrative mutation powers. | ACC02 | Build |

### Security

| ID | Required outcome and acceptance evidence | Task owner | Starting state |
| --- | --- | --- | --- |
| S01 | Actual Auth0 invite, verified email, MFA, sign-in and step-up flow; provider verification does not approve membership. | ACC01/OPS05 | Gate |
| S02 | Recovery verifies owner and completes with provider; local pending intent is never shown as recovered. | ACC01 | Gate |
| S03 | Logout/suspension/factor/password/grant changes revoke across servers and fence in-flight actions. | ACC01/ACC03/OPS05 | Extend |
| S04 | Real proxy/cookies/origin/CSRF/rate/body-limit validation and coordinated retirement of legacy authentication. | ACC01/OPS05/OPS06 | Extend |
| S05 | Private quarantine, content decoding/scanning and size/pixel/page/time/memory limits precede evidence eligibility. | PAY01 | Build |
| S06 | Originals, thumbnails, OCR text, sensitive fields, exports and keys obey access, retention and log-redaction policy. | PAY01/PAY02/OPS06 | Build |
| S07 | Supplier/receiving-bank changes are versioned and independently verified through a trusted channel. | PAY03/OPS02 | Gate |
| S08 | OCR worker can read assigned eligible evidence and write suggestions, never verify/allocate/post cash. | PAY02 | Build |
| S09 | Durable immutable decision audit, restricted reads and actionable audit/security failure alerts. | PAY01/OPS03 | Extend |

### Business workflow completeness

| ID | Required outcome and acceptance evidence | Task owner | Starting state |
| --- | --- | --- | --- |
| F01 | Editable persisted families/categories/attributes/images/descriptions/mappings and versioned prices replace demo inference. | CAT01 | Build |
| F02 | Server-authoritative exact line/document totals obey signed rounding/tax policy and existing numeric limits. | OPS01/OPS02/T05A | Extend |
| F03 | Physical/reserved/available/held/expired quantities, releases and independent counts survive races without overselling. | CAT02/OPS01 | Build |
| F04 | OMI/S-Mart authority, split orders, imports/acks/deduplication/reconciliation and freshness are explicit before stock promises. | CAT02/OPS02 | Gate |
| F05 | Document applicability of budgets/cost centers/spending thresholds/credit holds; implement and test those required by the business. | OPS01 | Conditional |
| F06 | OMI regular, DDO, S-Mart and Pasar Kering routes complete inspection/matching/claims/unused advance settlement. | OPS02/T08 | Build |
| F07 | Partial payment and overdue are separate; unbilled receipts have owner, aging and escalation. | OPS02/OPS03/T10 | Extend |
| F08 | Statement reconciliation and allocation share verified receipt/evidence workflow and preserve suspense funds. | PAY04 | Build |
| F09 | Finalized sales accept linked cases, inspection, replacement/credit/refund and independently verified payout. | OPS02/T11 | Build |
| F10 | Issued documents are immutable; recipient/quantity/version/exception-bound QR or tablet proof is validated. | OPS01/T09 | Build |
| F11 | Finance signs grouping, due-date origin/calendar, numbering, tax, rounding and correction/reissue rules. | OPS02/OPS06 | Gate |
| F12 | Unified work queue shows owner, next action, age, reminder/escalation and failed notification. | OPS03 | Build |

### User experience

| ID | Required outcome and acceptance evidence | Task owner | Starting state |
| --- | --- | --- | --- |
| U01 | Consistent shell shows active account/branch/store, pending tasks and only permitted navigation. | OPS04/ACC01 | Extend |
| U02 | Scoped name/code selectors replace raw numeric references for people, organizations, products, invoices and suppliers. | OPS04/all modules | Build |
| U03 | Approval screens display evidence, reviewed differences and effects before commitment. | ACC01/PAY05/OPS04 | Extend |
| U04 | Loading/empty/denied/validation/offline/unknown-outcome states retain safe drafts and resolve duplicate uncertainty. | OPS04/all modules | Extend |
| U05 | One scoped order timeline links purchase, delivery, invoice, payment, return and responsible actor. | OPS03/OPS04 | Build |
| U06 | Keyboard/focus/labels/contrast/mobile tables and real MFA work at buyer, courier and staff screen sizes. | OPS04/OPS05 | Extend |
| U07 | Saved/shareable scoped filters, repeat orders, bulk SKU entry and sorting follow core integrity gates. | OPS04 | Build |
| U08 | Account/session/recovery and business statuses use consistent Indonesian text, IDR presentation and Jakarta business time. | ACC01/OPS04 | Extend |

### Engineering and operating quality

| ID | Required outcome and acceptance evidence | Task owner | Starting state |
| --- | --- | --- | --- |
| Q01 | Enforced module contracts prevent cycles and direct cross-module collection writes. | OPS05/all modules | Extend |
| Q02 | Shared design tokens and bounded global CSS have meaningful visual regression coverage. | OPS04 | Extend |
| Q03 | Generated strict contracts preserve errors, string Long IDs, exact money and server authority. | OPS05/all modules | Extend |
| Q04 | Scoped indexed paginated reads replace whole-state loading/global revision in production modules. | OPS05/all modules | Extend |
| Q05 | Hosted CI and real browser→API→Mongo/provider evidence supplement fixture-based UI tests. | OPS05 | Gate |
| Q06 | Duplicate/lost responses, stock/payment/revocation races, worker crashes and external acknowledgments are exercised. | OPS05/OPS06 | Extend |
| Q07 | No production demo seeds; one writer and reconciled counts/stock/money/docs/ID mappings at cutover. | OPS06 | Gate |
| Q08 | Fix still-active legacy retry exhaustion to return known conflict distinctly from unknown commit; preserve safe operation receipt/retry in the new service. | OPS05/all modules | Extend |
| Q09 | Restore includes Mongo, object evidence, encryption keys and paused workers with external-ack reconciliation. | OPS06 | Gate |
| Q10 | Named responders monitor unbilled/overdue/stock/notification/OCR/queue/database health. | OPS03/OPS06 | Build |
| Q11 | Trained primary/backup operators complete branch order/payment/return/refund and period-close pilot. | OPS06 | Gate |
| Q12 | Reproducible releases, environment separation, migrations, dependency/security scans and software/model inventory are evidenced. | OPS05/OPS06/PAY02 | Extend |

### Local document assistance and later ML

| ID | Required outcome and acceptance evidence | Task owner | Starting state |
| --- | --- | --- | --- |
| M01 | Supplier-invoice extraction suggests PO/receipt match; human resolves differences; never auto-settles. | PAY02/OPS02 | Build |
| M02 | Delivery extraction suggests reference/quantities tied to the correct shipment/document revision. | PAY02/OPS01 | Build |
| M03 | Payment OCR is implemented and evaluated as mandatory review assistance, with safe outage handling. | PAY02/PAY03 | Build |
| M04 | Hash/reference/similarity signals support duplicate review; ambiguous match does not approve or reject money automatically. | PAY02/PAY04 | Build |
| M05 | Blur/rotation/clipping/type quality flags guide re-upload and retain original evidence. | PAY02/PAY05 | Build |
| M06 | Code and fuzzy product suggestions require confirmation and never guess contents; embeddings require later measured benefit. | CAT04/OPS07 | Conditional |
| M07 | Explainable price/adjustment/refund anomaly rules first; trained model only after validated improvement. | OPS03/OPS07 | Conditional |
| M08 | Lead-time-aware low-stock/replenishment baseline first; forecasting waits for sufficient clean history. | CAT02/OPS07 | Conditional |
| M09 | Original image, field positions, confidence, uncertain digits and human correction history remain reviewable. | PAY02/PAY05 | Build |
| M10 | Pin model/license/checksum/runtime, bound resource/network use and measure field accuracy, false matches, review effort, latency and memory. | PAY02/OPS05/OPS07 | Build |

## Bounded decisions before affected activation

Continue unrelated local work while these are pending. Record owner, decision, version, date and evidence in `docs/quality/report-decisions.md` when made; absence is not implied consent.

| Gate | Owner | Required before | Preparation without invented policy |
| --- | --- | --- | --- |
| D01 — Identity and independent people | Business owner / IT | Real enrollment, recovery or financial grants | Named provider operators/backups; trusted staff/person verification; duplicates and re-linking reviewed; no email auto-merge |
| D02 — Permission policy | Operations / finance | Real workflow grants | Buyer approvers vs store acceptance; catalog entitlement; historic readers during suspension; whether any specific suspended-branch settlement is permitted (default deny, separate command authority/fences, never implied by history.read); finance conflict matrix; delegation/review cadence |
| D03 — Commercial policy | Finance | Real order/invoice/credit activation | Budget/cost-center/credit applicability; exact totals/tax/rounding; terms/calendar/grouping/numbering/reissue; no invented limits |
| D04 — Stock authority/master data | Store / OMI / S-Mart owners | Promised availability and migration | Verified package contents/GTIN ownership/mappings; all-channel stock authority, feed age and outage policy; opening quantities/costs |
| D05 — Bank review | Finance / bank-data owner | Real verification/allocation | Approved accounts and trusted independent change verification; statement formats/IDs/cadence; actual bank evidence; currencies; suspense/refund/correction policy |
| D06 — Evidence/OCR | Operations / finance / data owner | Real uploads/model acceptance | Image formats/limits, supplementary PDFs, retention/hold, private representative evaluation corpus, exception authority/reasons/response times |
| D07 — Host and recovery | Owner / IT | Provisioning and deployment | Approved budget/region/capacity, Auth0 settings, sender/domain, Mongo/objects/keys backups, monitoring/responders; prove recovery objectives |
| D08 — Pilot and rollout | Operations / finance | Production cutover | Named branch/operators/backups, reconciliation and security acceptance; approved cutover window and real-business actions |

## Completion evidence rules

For each row record implementation commit, executable test or operator evidence, scope/environment, result and any remaining gate in the progress/acceptance ledger. A generic policy test is not proof that all module endpoints use it; an intercepted browser response is not proof of real provider integration. A conditional item closes only with a recorded applicability decision and the required baseline, or completed measured enhancement. Do not silently delete conditional rows to achieve 97/97.

The goal finishes only when required report rows and original production gates pass. Planning, local tests, provider configuration and actual branch operation are separate evidence stages. External access or decisions can remain pending while independent local work proceeds.