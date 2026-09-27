# Unit Toko: production readiness review

Date: 27 September 2026. Scope: planning audit, current local checkout, attached BPP/diagram archives and meeting image. Three review agents worked independently on product/workflows, bugs/architecture, and security/roles. The lead consolidated their findings, read the original source documents, researched primary technical documentation and ran baseline checks.

## Verification and limits

- Current regression suite: **266 tests passed, zero failures** using `node scripts/test-domain.mjs` from `website/`.
- TypeScript: **passed** using `node node_modules/typescript/bin/tsc --noEmit`.
- Initial regression execution was blocked by sandbox file resolution; the same existing isolated test command passed outside that restriction. Tests use temporary/in-memory fixtures, not the live website.
- No lint, production build, live browser audit, penetration test, load test, actual Mongo migration or restore drill was performed for this planning task. Passing existing tests does not prove the missing controls below exist.
- Findings describe static code behavior unless marked as a risk/proposal. Referenced paths/lines describe the checkout at review time and will move during refactoring.
- Existing unrelated edits in `.gitignore`, `website/app/layout.tsx`, `docs/mentor-mockup/` and `website/public/mentor-mockup.html` were left intact. Generated test/typecheck artifacts are ignored.

## Existing foundations to keep

Persistent libSQL/Turso storage, migrations, transactional write batches, command idempotency, optimistic revision checks, server-side role restrictions, buyer ownership, courier assignment, private attachments, stock reservations, partial shipments, buyer-approved substitutions, invoices from accepted quantities, credit/refund workflows, balanced journals and period locks already exist. The project should be described as a substantial demo with production gaps, not as a UI without a backend or database.

## Consolidated findings

P0 = blocks real production use. P1 = resolve before the affected real workflow goes live. P2 = reliability/maintainability improvement with explicit acceptance. These are project priorities, not CVSS severity scores.

| ID | Priority | Evidence | Finding and required outcome |
| --- | --- | --- | --- |
| A01 | P0 | `website/lib/server/repository.ts:19`, `:24`, `:38`, `:82`; `lib/server/demo-catalog.ts:37` | Initialization and read paths can provision demo users/catalog/stock/journals. Separate migrations, controlled production bootstrap and explicit demo fixtures; no business seeding from GET requests. |
| A02 | P1 | `website/lib/domain/engine.ts:157`, `:163` | Payment recording and verification allow the same billing actor. Add independent verifier rule across role changes, with transaction/replay tests. |
| A03 | P1 | `website/lib/domain/accounts.ts:4`; `model.ts:4`; `selectors.ts:31` | Simple division identity and broad internal staff projections do not express branch/shop scope. Model organization/branch/division memberships and scope every read/write/export/file/event. This is a requirement gap, not a claim that existing global staff scope violates an earlier requirement. |
| A04 | P1 | `website/app/api/auth/login/route.ts:15`; `website/db/schema.ts:5`; `website/app/api/admin/password-reset/route.ts:17` | Password-only authentication and administrator-chosen reset passwords need invitation, verified email, MFA, owner-controlled recovery and revocation. Existing hashing, reset throttling and audit are useful foundations. |
| A05 | P1 | `website/lib/server/repository.ts:44`, `:49`; `database.ts:51`; `app/api/catalog/route.ts:9` | Whole-company state is loaded from 32 business tables for catalog/state/commands, with one global revision. Replace with scoped indexed queries and aggregate transactions. Additional read-batch contention is a risk to measure, not a measured performance result. |
| A06 | P1 | `website/lib/domain/catalog.ts:45`, `:65`; `model.ts:5`; `engine.ts:277` | Product family/pack/category/image rely on demo IDs/names/static metadata. Real editable master data and explicit unit/carton stock conversion are required. |
| A07 | P1 | `website/components/workspace.tsx:111`; `components/shop/shop-shell.tsx:177` | Views update at mount/local action/manual refresh, not automatically across users. Add authorized push invalidation and reconnect/refetch; server remains authoritative. |
| A08 | P1 | `website/lib/domain/model.ts:5`; `engine.ts:149`; `components/sales-register.tsx:19` | OMI/Smart is a classification/filter, not complete source-system posting/reconciliation. Add source mappings, separate records, external reference deduplication and explicit stock authority. |
| A09 | P1 | `website/lib/domain/engine.ts:124`; `website/lib/server/documents.ts:10` | Existing receipt evidence/account confirmation does not implement the requested tablet/QR workflow. Add shipment-bound recipient verification, consent, actual quantities, one-use challenge, immutable proof/version and expiry/replay tests. |
| A10 | P1 | `website/components/invoice-composer.tsx:43`; `lib/domain/invoice-selection.ts:13`; `model.ts:15` | Manual invoice creation and due dates alone do not control delayed billing. Add invoice-ready SLA work queue, delivery history/retries, aging/reminders and accountable owners. |
| A11 | P1 bug | `website/lib/domain/selectors.ts:22`; `components/workspace.tsx:200`; `components/customer-account.tsx:219` | Partial-payment status is returned before overdue evaluation. Keep payment state and aging independent whenever an overdue balance remains. |
| A12 | P1 | `website/lib/domain/engine.ts:141`, `:144`, `:191`; `components/customer-account.tsx:206` | No buyer case for finalized sales; accepted/finalized physical returns need a controlled route. Refund payout marking lacks real bank evidence/verification. Add after-sales cases, approved returns, linked credits/refunds and payout proof while preserving original history. |
| A13 | P1 before supplier finance | `website/lib/domain/engine.ts:212`, `:224`, `:228`, `:235` | SPH is reference text; unused supplier advances and return receivables lack complete settlement. Add PO/receipt/SPH matching, advance refund, supplier claim credit/replacement/cash outcomes and reconciliation. |
| A14 | P1 | `website/lib/server/documents.ts:10`, `:15`; `components/invoice-composer.tsx:50` | Operational invoices/tax handling are demo, and historical rendering uses mutable master descriptions. Create approved immutable issuance snapshots; retain official external tax references; never just remove a demo label. |
| A15 | P2 bug | `website/lib/server/repository.ts:53`, `:55`; `lib/server/http.ts:7` | Fourth concurrency conflict throws raw SQL error, bypassing intended 409 and surfacing 503. Add a bounded typed retryable conflict response with unchanged state. |
| A16 | P2 | `website/lib/domain/engine.ts:6`, `:37`, `:65`, `:150`, `:198`, `:222` | Individual quantity/unit values pass a limit that their product can exceed later during finalization. Validate checked exact line/document totals before accepting an order/PO. |
| A17 | P2 | `website/tests/repository.test.ts:41`; `lib/domain/seed.ts:13`, `:23` | Fixed invoice due date 2026-10-30 and seed expiry 2027-06-30 create future date-dependent failures. Use frozen clocks and explicit boundary scenarios. |
| A18 | P2 | `website/scripts/test-domain.mjs:1`; `website/package.json` | `esbuild` test dependency is transitive, not explicitly declared. Pin direct test tools and exercise clean CI installs. |
| A19 | P1 policy gate | `website/lib/domain/engine.ts:289`, `:290` | Period approval checks independence, but closure lacks actor separation from earlier submission/approval after role changes. Enforce the proposed three-actor close policy explicitly. |
| A20 | P2 | `website/app/api/auth/login/route.ts:11`; `lib/server/http.ts:9` | Email-only throttling and post-read JSON size validation need layered abuse limits and bounded streaming bodies. Test spraying, targeted lockout recovery and oversized chunked input. |
| A21 | P2 | `website/lib/server/attachment-upload.ts:65`; `lib/domain/engine.ts:282`, `:294` | Header/size checks and generic audits need quarantine/content scanning, structured security audit and scoped retention. Preserve existing private storage and upload retry safety. |
| A22 | P2 | `website/lib/domain/accounts.ts:18`; command branches in `engine.ts` | Role labels depend on demo identities and checks are scattered. Formal permission catalog and migration mapping must be independent of sample accounts. |

## Source interpretation

| Source | Requirement or important distinction |
| --- | --- |
| Meeting image | B2B branch procurement, wrong delivery/refund problems, two cashiers, live stock, unit/carton variants, signature/QR, roles and Long IDs. November dates are reference only after the user's clarification. |
| BPP I and VII | Store, manager and report-preparation responsibilities. Cashier belongs to shop staff; finance and technical identity administration should not be conflated. |
| BPP II; diagrams 1, 2, 3, 5 | Four procurement routes, container/item inspection, evidence, partial DDO, vendor down payments, custom logos, emergency buying and transport reimbursement. HET floor/ceiling wording is inconsistent and needs a business decision. |
| BPP III; diagrams 6, 7 | Walk-in and branch orders are different channels. Preserve partial delivery and explicit substitution consent. Legacy walk-in POS can continue, but shared stock/source reconciliation is a launch concern. |
| BPP IV; diagram 4 | Supplier expiry returns and per-product policies. Does not establish customer return rights. Rejected expired stock must not automatically become sellable. |
| BPP V; diagrams 8, 9 | Delayed transaction entry causes stale stock/reporting; manual OMI/Smart consolidation risks duplicates and one-person dependency. Capture stock movement promptly and correct finalized finances through controlled adjustments. |
| BPP VI; diagram 10 | Invoice/tax-document delivery, centralized transfer payment, unidentified funds, independent management approval, accounting close and OMI SPH reconciliation. A consumer payment gateway is not a prerequisite. |
| BPP II/III and diagram 3 | Smart includes galon/water as well as merchandise. Use configurable SKU-to-source mapping instead of a hardcoded product-category shortcut. |

## Recommended launch cut

Approve B2B accounts only; preserve the useful frontend and transactional behavior; migrate to modular C# + MongoDB behind explicit contracts; reduce ten current role names to six launch templates without merging independent finance grants; use managed identity for verified email/MFA/recovery; complete stock, receipt, invoice, return and reconciliation loops; release through rehearsal and a one-branch pilot.

Full specification: [unit-toko-production.md](../requirements/unit-toko-production.md). Sequenced work: [implementation plan](../superpowers/plans/2026-09-27-unit-toko-production.md).
