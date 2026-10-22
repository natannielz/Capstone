# Route × role rules matrix (Task 1.1, phases A and B)

Phase B persona results are in the section "Observed (phase B)" at the end of this file.

Author: GAP agent, 21 Okt 2026. Source: code on `main` (dc94c4b7 plus working tree) and signed-out probes of the running pilot
(`portal.unit-toko-pilot.home.arpa`). Findings referenced as `G-###` live in `findings-gap.md`.

Probe logs (signed-out only, no credentials, 35 + 53 API requests in total):
- `tmp/website-ready/logs/gap-guest-http.txt`: raw HTTP status, redirects and anonymous API answers.
- `tmp/website-ready/logs/gap-guest-rendered.txt`: Edge, fresh context, each page rendered as `tamu`.
- `tmp/website-ready/logs/gap-guest-stuck-pages.txt`: console and network capture for the pages that never finished loading.

## How to read this matrix

**The pilot runs the full release.** The running pilot answers `/cart`, `/ordering/orders` and `/billing/invoices` (HTTP 200, `gap-guest-http.txt`), so
`NEXT_PUBLIC_UNIT_TOKO_RELEASE=full` and `frontend/src/proxy.ts:5` lets every path through. Where a page behaves differently in a release-1 build
(`isPilotRelease`), the matrix says so.

**Server-side authentication.** `backend/src/UnitToko.Api/Program.cs:232-233` sets the fallback policy to `RequireAuthenticatedUser`. The only anonymous
endpoints are `/api/v1/public/catalog`, `/api/v1/public/catalog/{publicId}`, `/api/v1/identity/session`, `/api/v1/identity/csrf`, `/auth/login`,
`/auth/step-up`, `/auth/logout`, the provider security event hook and `/health/*` (`/health/*` is not exposed through the pilot edge: 404). The probe
confirms that every other API probed answers 401 `unauthenticated`, or 403 `untrusted_origin` for a cross-origin POST (`gap-guest-http.txt:39-55`).
Permissions are then enforced inside the module services (`ScopePolicy.Compile(actor, snapshot, Permissions.X, now)`), not by endpoint filters. The API
permission column names the permission family each service checks, read from the service code that the endpoint injects. Exact per-endpoint
permissions still need persona probing (see the end).

**Frames.**
- `S` is the storefront frame (`components/store/store-frame.tsx`).
- `W` is the workspace frame (`components/workspace-kit/workspace-frame.tsx`).
- `/ordering/*` switches between them by path (`components/store/ordering-frame.tsx:87`).
- No frame does any redirect. Both frames only read the session for display.

**Frontend guard.** No page has a server-side guard. Every page renders its shell to anyone, and the client module then decides what to show. Guard kinds:
- **SAS**, the shared `SessionAccessState` (`components/workspace-kit/session-access-state.tsx`):
  - Guest or expired session: "Sesi Anda perlu diperbarui", with "Masuk kembali" returning to the page path (the query is dropped).
  - 403: "Halaman ini di luar tugas akun Anda", with a link to `/workspace`.
  - Other failures: "Halaman belum dapat dibuka".
  - Most SAS pages also refuse on the client before reading data when the required action is missing (`AccessFailure(403,'scope_denied')`).
- **INV**, the inventory family's own guard. The heading is "Masuk untuk …", the body says "Sesi Anda telah berakhir. Masuk kembali untuk melanjutkan.", and "Masuk kembali" returns to the page path. Denial uses the module's own text.
- **ADM**, the administration family's own guard. The heading is "Sesi terverifikasi diperlukan", with module-specific body text. A 403 uses the module's own text ("Akses … belum disetujui").
- **ACC**, `IdentityAccount`. A guest sees "Masuk untuk melanjutkan", and the sign-in link is `/identity/login` **without** `returnPath`.
- **PUB**, the public catalog. No sign-in is needed. A signed-in member is moved to `/shop…` on the client.
- **BUY**, `BuyerCatalog`. A guest is moved to `/katalog…` in the full release, or sees a sign-in wall in release 1 (`BuyerCatalog.tsx:172`).

**Kind.**
- **canonical**: the one page for the task.
- **duplicate**: a second page rendering the same task.
- **redirect**: a server 307 to another route.
- **alias**: re-exports a redirecting page.

## API endpoint groups and the permission the server enforces

| Group | Endpoint file | Server permission family (service) |
|---|---|---|
| PUBLIC | `Catalog/CatalogPublicEndpoints.cs:36-55` | **anonymous**. Shows active SKUs listed in an assortment whose public head is `isPublic=true`. No price, owner or stock. `Cache-Control: public, max-age=60` |
| IDENTITY | `Identity/IdentityEndpoints.cs`, `IdentityRuntime.cs:111` | session and csrf anonymous; `/auth/*` anonymous form posts |
| ACCESS | `Authorization/AccessEndpoints.cs` (`/api/v1/access`) | `/current`: authenticated, returns `allowedActions` filtered by `ScopePolicy`. Proposals: `access.propose/approve/revoke`, `membership.propose/approve`, `access.audit.read`, `technical.configure`, `organization.approve` |
| DELEGATION | `Authorization/TemporaryDelegationEndpoints.cs` | `access.*` plus delegation participant (`TemporaryDelegationService`) |
| IDADMIN | `Identity/IdentityAdministrationEndpoints.cs` | `invite.manage`, `recovery.initiate`, `membership.*`, `access.*` |
| PERSON | `Identity/PersonReviewEndpoints.cs` | `person.review.read/propose/approve` |
| CONTACT | `Identity/AccountContactEndpoints.cs` | authenticated self only |
| ORG | `Authorization/OrganizationEndpoints.cs` | `technical.configure`, `organization.approve` (frontend also shows it for `access.audit.read`) |
| ADDRESS | `Organizations/OrganizationAddressEndpoints.cs` | `organization.address.read`, `technical.configure`, `organization.approve` |
| BOOKS | `Accounting/AccountingBookSetupEndpoints.cs` | `accounting.book.read/propose/approve` |
| REPORT | `Reporting/AccountingReportEndpoints.cs` | `report.read` |
| INVOICE | `Billing/InvoiceEndpoints.cs` | `invoice.read`, `invoice.issue` |
| INVREL | `Billing/InvoiceReleaseEndpoints.cs` | `invoice.read`, `invoice.issue` (frontend also requires `invoice.release.read`) |
| PAYDECL | `Billing/PaymentDraftEndpoints.cs`, `PaymentEvidenceEndpoints.cs`, `PaymentOcrEndpoints.cs` | `payment.evidence`, `invoice.read` |
| PAYREV | `Billing/PaymentReview*Endpoints.cs`, `PaymentVerificationEndpoints.cs`, `PaymentManualReviewEndpoints.cs`, `PaymentCorrectionEndpoints.cs`, `PaymentAllocation*Endpoints.cs` | `payment.review.read/manage/manual/correct`, `payment.verify`, `payment.allocate`, `journal.reverse`, `invoice.read` |
| RECVACC | `Billing/ReceivingAccountEndpoints.cs` | `receiving.account.read/details.read/propose/verify` |
| BANKSTMT | `Billing/BankStatementEndpoints.cs` | `bank.statement.read/stage` |
| BANKRCPT | `Billing/BankReceiptEndpoints.cs` | `bank.receipt.read/confirm` |
| REFUND | `Billing/Refund*Endpoints.cs`, `Return*Endpoints.cs` | `refund.request/approve`, `credit.request/approve`, `disbursement.record/verify`, `case.read` |
| CATADMIN | `Catalog/CatalogEndpoints.cs:16`, `CatalogIdentifierEndpoints.cs` | `catalog.manage` (`catalog.sku.*` commands), `catalog.read` |
| VARIANTS | `Catalog/CatalogEndpoints.cs:133`, `CatalogCartEndpoints.cs`, `CatalogBulkEndpoints.cs` | `catalog.read` through `CatalogVisibilityStore` scope |
| PHOTO | `Catalog/CatalogPhotoEndpoints.cs`, `CatalogPhotoRevalidationEndpoints.cs` | catalog visibility scope for reads, `catalog.manage` for writes |
| PRICE | `Catalog/CatalogPriceEndpoints.cs` | `pricing.manage`, `pricing.approve` |
| SHOWCASE | `Catalog/CatalogPublicEndpoints.cs:57-87` | `catalog.public.propose`, `catalog.public.approve` (per assortment) |
| SCAN | `Catalog/ScanEndpoints.cs` | authenticated; scope not statically traced |
| PREP / DISPATCH / COURIER | `Fulfillment/ShipmentPreparationEndpoints.cs`, `ShipmentDispatchEndpoints.cs`, `ShipmentCourierEndpoints.cs` | `shipment.prepare` / `shipment.dispatch` / `shipment.assign` |
| SHIPREAD | `Fulfillment/ShipmentReadEndpoints.cs`, `DeliveryAttemptEndpoints.cs`, `ShipmentDocumentEndpoints.cs` | `shipment.read`, `delivery.attempt`, `delivery.evidence`, `shipment.prepare` |
| RECEIPT | `Fulfillment/ShipmentReceiptEndpoints.cs`, `ShipmentReceiptProofEndpoints.cs` | `receipt.accept`, `shipment.read` |
| CASE | `Fulfillment/AfterSales*Endpoints.cs` | `case.read`, `case.request`, `case.manage` |
| STOCKCOUNT / REPLEN | `Inventory/StockCountEndpoints.cs`, `ReplenishmentEndpoints.cs` | `stock.count`, `stock.read` (replenishment also `procurement.prepare`) |
| SOURCES | `Inventory/StockSource*Endpoints.cs` | `stock.source.read/propose/approve/suspend`, `stock.source.policy.*`, `stock.source.reconciliation.*` |
| PACK | `Inventory/PackConversion*Endpoints.cs` | `stock.convert` |
| RELEASE | `Endpoints/StockReleaseEndpoints.cs` | `stock.release.propose/approve` |
| HEALTH / ANOMALY | `Operations/OperatorHealthEndpoints.cs`, `OperationalAnomalyEndpoints.cs` | `technical.configure` / `operations.anomaly.read/review/policy.configure` |
| REQUESTS | `Ordering/BranchOrderRequestEndpoints.cs` | `order.request.read/submit/decide` |
| DRAFT / QUOTE | `Ordering/OrderDraftEndpoints.cs`, `OrderQuoteEndpoints.cs` | `order.submit` (`order.read` for quotes of placed orders) |
| ORDERS | `Ordering/OrderSubmissionEndpoints.cs`, `OrderViewEndpoints.cs`, `OrderTimelineEndpoints.cs` | `order.read`, `order.submit`, `order.cancel` |
| REVIEWS | `Ordering/OrderDecisionEndpoints.cs` | `order.spend.approve`, `order.accept`, `order.cancel` |
| SUBST | `Ordering/OrderSubstitutionEndpoints.cs` | `substitution.propose`, `substitution.accept` |
| SPEND / COMM | `Ordering/OrderSpendingPolicyEndpoints.cs`, `OrderCommercialPolicyEndpoints.cs` | `order.spending.policy.*` / `order.commercial.policy.*` (the commercial service reads with `order.submit` on the buyer side) |
| PROC-* | `Procurement/*Endpoints.cs` | see each row: `procurement.read/prepare/approve`, `funding.*`, `supplier.*`, `procurement.<area>.*`, `journal.post` |
| WORK | `Work/WorkEndpoints.cs` | authenticated; per-actor summary (service permission checks not statically traced) |

## The 81 routes

| # | Route | Kind | Frame | Intended audience (navigation entry: permission) | Frontend guard: guest / wrong role / expired | API groups called | Gaps |
|---|---|---|---|---|---|---|---|
| 1 | `/` | canonical | own header | everyone (landing) | none needed; guest links "Masuk" have no `returnPath` | none | G-022 |
| 2 | `/account` | redirect → `/identity/account` | none | legacy | server 307 (probe) | none | K2 confirmed |
| 3 | `/account/orders` | redirect → `/ordering/orders` (release 1: `/ordering/requests`) | none | legacy | server 307 (probe) | none | K2 confirmed |
| 4 | `/account/orders/[id]` | alias of #3 | none | legacy | 307 to the list; **order id dropped** | none | G-009 |
| 5 | `/accounting/books` | canonical | W | "Buku akuntansi": `accounting.book.read/propose/approve` | SAS + client action check | BOOKS | — |
| 6 | `/administration/access` | canonical | W | "Akses dan keanggotaan": any `access.*` / `membership.*` | ADM; guest sees two stacked states plus trace id | ACCESS | G-012, G-016 |
| 7 | `/administration/accounts` | canonical | W | "Akun dan undangan": `invite.manage`, `recovery.initiate`, `membership.approve`, `access.approve`, `access.audit.read` | ADM | IDADMIN | G-012, G-014 |
| 8 | `/administration/catalog` | canonical | W | "Katalog toko": `catalog.manage` | ADM; guest is told "Sesi Anda telah berakhir" | CATADMIN, PHOTO | G-012, G-013 |
| 9 | `/administration/delegations` | canonical | W | "Delegasi sementara": **no permission**, listed for every visitor including guests | own guard "Sesi atau akses belum tersedia" | DELEGATION | G-028 |
| 10 | `/administration/history` | canonical | W | "Riwayat perubahan": `access.audit.read`, `access.approve`, `membership.approve`, `access.revoke`, `technical.configure`, `organization.approve` | ADM | ACCESS (history) | G-014 |
| 11 | `/administration/organizations/addresses` | canonical | W | "Alamat organisasi": `organization.address.read`, `technical.configure`, `organization.approve` | SAS | ADDRESS | — |
| 12 | `/administration/organizations` | canonical | W | "Organisasi dan unit": `technical.configure`, `organization.approve`, `access.audit.read` | ADM | ORG, ACCESS (scopes) | G-014 |
| 13 | `/administration/person-reviews` | canonical | W | "Keterkaitan akun": `person.review.*` | ADM | PERSON | G-014 |
| 14 | `/billing/bank-receipts` | canonical | W | "Penerimaan bank": `bank.receipt.read` | SAS | BANKRCPT | G-026 |
| 15 | `/billing/bank-statements` | canonical | W | "Mutasi bank": `bank.statement.read` | SAS | BANKSTMT | G-026 |
| 16 | `/billing/invoice-releases` | canonical | W | "Persetujuan penerbitan tagihan": `invoice.read` + `invoice.release.read` | SAS | INVREL | — |
| 17 | `/billing/invoices` | canonical | W | "Tagihan": `invoice.read` / `invoice.issue` (buyer template has `invoice.read`) | SAS; **pilot chunk 404: stuck on "Memeriksa akun…"** | INVOICE | G-006, G-018, G-026 |
| 18 | `/billing/payment-reviews` | canonical | W | "Pemeriksaan pembayaran": `payment.review.read` + `invoice.read` | SAS | PAYREV, PAYDECL | G-026 |
| 19 | `/billing/payments` | canonical | W | "Bukti pembayaran": `payment.evidence` + `invoice.read` (buyer task) | SAS | PAYDECL, INVOICE | G-018, G-026 |
| 20 | `/billing/receiving-accounts` | canonical | W | "Rekening penerimaan": `receiving.account.read` | SAS | RECVACC | G-026 |
| 21 | `/cart` | canonical (release 1: redirect → `/ordering/requests`) | S | store header "Keranjang" for members with `order.submit`; guest link goes through sign-in | SAS (returnPath `/cart`); a guest who opens it directly reads "Sesi Anda perlu diperbarui" | DRAFT/QUOTE, VARIANTS | G-013 |
| 22 | `/catalog/browse` | redirect → `/shop` | none | legacy | server 307 (probe); still linked in `/ordering/drafts` | none | K2 confirmed, G-023 |
| 23 | `/catalog/prices` | canonical | W | "Harga kemasan": `pricing.manage` / `pricing.approve` | own guard (not SAS) | PRICE | G-014 |
| 24 | `/catalog/public` | canonical | W | "Katalog publik": `catalog.public.propose` / `catalog.public.approve` | SAS | SHOWCASE (**whole assortments only**) | G-002 |
| 25 | `/checkout` | redirect → `/ordering/checkout` (release 1: `/ordering/requests`) | none | legacy | server 307 (probe) | none | K2 confirmed |
| 26 | `/customer/login` | alias of `/login` | none | legacy | 307 → `/identity/login?returnPath=%2Fidentity%2Faccount` | none | K2 confirmed, G-008 |
| 27 | `/fulfillment/cases` | canonical | W | "Keluhan barang": `case.read` / `case.request` (buyer task) | SAS | CASE, REFUND | G-018, G-026 |
| 28 | `/fulfillment/courier-assignments` | canonical | W | "Penugasan kurir": `shipment.assign` | SAS | COURIER | — |
| 29 | `/fulfillment/dispatches` | canonical | W | "Keberangkatan kiriman": `shipment.dispatch` + `shipment.assign` (the server needs only `shipment.dispatch`) | SAS | DISPATCH, PREP, SCAN | G-026, G-031 |
| 30 | `/fulfillment/preparations` | canonical | W | "Persiapan kiriman": `shipment.prepare` | SAS | PREP, SCAN | G-026 |
| 31 | `/fulfillment/receive` | canonical | W | "Penerimaan kiriman": `receipt.accept` (buyer task, reached by courier QR) | own guard; **a guest sees "Koneksi terputus. Hasil belum dapat dipastikan"** | RECEIPT, SHIPREAD | G-015, G-018, G-026 |
| 32 | `/fulfillment/shipments` | canonical | W | "Kiriman": `shipment.read` / `delivery.attempt` | SAS | SHIPREAD, COURIER, RECEIPT | G-018, G-026 |
| 33 | `/identity/account` | duplicate hub (with #81) | S | "Akun saya" (store menu and tab, workspace "Akun"): any signed-in user | ACC: guest sign-in link has no `returnPath` | IDENTITY, ACCESS, CONTACT, WORK, `/auth/logout`, `/auth/step-up` | G-010, G-020 |
| 34 | `/identity/login` | **canonical sign-in (D2)** | own header | everyone | already signed in: "Buka akun saya" ignores `returnPath` | IDENTITY, `/auth/login` | G-010 |
| 35 | `/inventory/counts` | canonical | W | "Hitung barang": `stock.count` / `stock.read` | INV | STOCKCOUNT, SCAN | G-013, G-014 |
| 36 | `/inventory/pack-conversion-executions` | canonical | W | "Pelaksanaan buka kemasan": `stock.convert` | INV | PACK, SCAN | G-013, G-014 |
| 37 | `/inventory/pack-conversions` | canonical | W | "Aturan buka kemasan": `stock.convert` | INV | PACK, SCAN | G-013, G-014 |
| 38 | `/inventory/replenishment` | canonical | W | "Kebutuhan pembelian": `stock.read` | own guard; **the server refuses its sign-in return path** | REPLEN, SCAN | G-007, G-014 |
| 39 | `/inventory/source-reconciliations` | canonical | W | "Rekonsiliasi jumlah fisik": `stock.source.reconciliation.read/propose/decide` | SAS | SOURCES, SCAN | G-026 |
| 40 | `/inventory/sources/activations` | canonical | W | "Aktivasi sumber stok": `stock.source.read/propose/approve` | INV | SOURCES | G-013, G-014 |
| 41 | `/inventory/sources/aliases` | canonical | W | "Pemetaan kanal": `stock.source.read/propose/approve` | INV | SOURCES, SCAN | G-013, G-014 |
| 42 | `/inventory/sources` | canonical | W | "Sumber stok": `stock.source.read/propose/approve` | INV (guest is told "Sesi Anda telah berakhir") | SOURCES | G-013, G-014 |
| 43 | `/inventory/sources/policies` | canonical | W | "Kebijakan operasional stok": `stock.source.read`, `stock.source.policy.propose/approve` | INV | SOURCES | G-013, G-014 |
| 44 | `/inventory/sources/revisions` | canonical | W | "Perubahan sumber": `stock.source.read/propose/approve` | INV | SOURCES | G-013, G-014 |
| 45 | `/inventory/sources/suspensions` | canonical | W | "Penghentian sumber": `stock.source.read` / `stock.source.suspend` | INV | SOURCES | G-013, G-014 |
| 46 | `/inventory/stock-releases` | canonical | W | "Pelepasan stok ditahan": `stock.release.propose/approve` | INV | RELEASE | G-013, G-014 |
| 47 | `/katalog/[publicId]` | canonical (guest) | S | guest product page; a member is moved to `/shop/[publicId]` | PUB; an unknown id answers 200 "Produk ini khusus untuk akun cabang" | PUBLIC | G-001, G-004, G-005, G-024 |
| 48 | `/katalog` | canonical (guest), pair of #80 | S | guest catalog (store "Katalog" for guests); a member is moved to `/shop` | PUB; **0 products on the pilot** | PUBLIC | G-001, G-003, G-004, G-005, G-025, G-027, G-029 |
| 49 | `/login` | redirect → `/identity/login` | none | legacy | maps `?next=` but **drops `/workspace` and the product id** | none | K2 confirmed, G-008 |
| 50 | `/operations/anomalies` | canonical | W | "Tinjauan operasional": `operations.anomaly.read` | own guard | ANOMALY | G-014 |
| 51 | `/operations/health` | canonical | W | "Kesehatan sistem": `technical.configure` | own guard | HEALTH | G-014 |
| 52 | `/ordering/checkout` | canonical | S | no navigation entry; reached from `/cart` with `?draft=`; `order.submit` | SAS, but `returnPath` drops `?draft`; **pilot chunk 404: stuck** | DRAFT/QUOTE, ORDERS | G-006, G-011 |
| 53 | `/ordering/commercial-policies` | canonical | W | "Aturan komersial": `order.commercial.policy.*` | SAS | COMM | — |
| 54 | `/ordering/drafts` | canonical | S | "Draf pesanan": `order.submit` | SAS; links to legacy `/catalog/browse`; **pilot chunk 404: stuck** | DRAFT/QUOTE, VARIANTS | G-006, G-023 |
| 55 | `/ordering/orders` | canonical | S | "Pesanan saya": `order.read` (store tab "Pesanan") | SAS; **pilot chunk 404: stuck** | ORDERS, DRAFT/QUOTE | G-006, G-026 |
| 56 | `/ordering/requests/incoming` | canonical | W | "Permintaan masuk": `order.request.decide` | SAS | REQUESTS, VARIANTS | — |
| 57 | `/ordering/requests` | canonical | S | "Permintaan saya": `order.request.read` / `order.request.submit` | SAS; a guest reads "Sesi Anda perlu diperbarui" | REQUESTS, VARIANTS | G-013, G-018 |
| 58 | `/ordering/reviews` | canonical | W | "Peninjauan pesanan": `order.spend.approve` / `order.accept` / `order.cancel`; store menu calls it "Persetujuan belanja" | SAS; **pilot chunk 404: stuck** | REVIEWS, ORDERS | G-006, G-018, G-019, G-026 |
| 59 | `/ordering/spending-policies` | canonical | W | "Persetujuan belanja": `order.spending.policy.*` | SAS | SPEND | G-019 |
| 60 | `/ordering/substitutions` | canonical | W | "Penggantian barang": `substitution.propose` / `substitution.accept` | SAS, but `returnPath` drops `?order` | SUBST | G-011, G-026 |
| 61 | `/procurement/advance-returns` | canonical | W | "Pengembalian uang muka": `procurement.advancereturn.*` | SAS | PROC (`procurement.advancereturn.*`, `journal.post`) | G-026 |
| 62 | `/procurement/disbursements` | canonical | W | "Bukti uang muka pemasok": `procurement.disbursement.read` | SAS | PROC (`procurement.disbursement.*`, `disbursement.record/verify`) | — |
| 63 | `/procurement/funding` | canonical | W | "Otorisasi uang muka": `funding.read/request/approve` | SAS | PROC (`funding.*`) | — |
| 64 | `/procurement/inspections` | canonical | W | "Pemeriksaan barang pengadaan": `procurement.read` | SAS | PROC (`procurement.read`, `procurement.inspection.*`) | — |
| 65 | `/procurement/purchase-orders` | canonical | W | "Pesanan pembelian": `procurement.read/prepare/approve` | SAS | PROC (`procurement.*`, `funding.*`), SCAN | — |
| 66 | `/procurement/quality-acceptances` | canonical | W | "Penerimaan mutu barang": `procurement.qualityacceptance.*` | SAS | PROC (`procurement.qualityacceptance.*`, `stock.read`) | G-026 |
| 67 | `/procurement/receiving` | canonical | W | "Penerimaan pengadaan": `procurement.read` | SAS | PROC (`procurement.container.receive`, `procurement.prepare`) | — |
| 68 | `/procurement/requests` | canonical | W | "Permintaan pengadaan": `procurement.read/prepare/approve` | SAS | PROC (`procurement.read/prepare/approve`), REPLEN | — |
| 69 | `/procurement/settlements` | canonical | W | "Verifikasi debit pemasok": `procurement.settlement.read` | SAS | PROC (`procurement.settlement.read`, `disbursement.verify`, bank reads) | — |
| 70 | `/procurement/stock-receipts` | canonical | W | "Penerimaan stok ditahan": `procurement.read` + `stock.read` | SAS | PROC (`stock.receive`, `procurement.read`, `stock.read`), SCAN | G-026 |
| 71 | `/procurement/stock-valuations` | canonical | W | "Penilaian stok ditahan": `procurement.stockvaluation.read` + `procurement.read` + `stock.read` | SAS | PROC (`procurement.stockvaluation.*`, `journal.post`) | G-026 |
| 72 | `/procurement/supplier-banks` | canonical | W | "Rekening pemasok": `supplier.bank.read` | SAS | PROC (`supplier.bank.*`) | — |
| 73 | `/procurement/supplier-claims` | canonical | W | "Klaim pemasok": `procurement.read` | SAS | PROC (`supplier.claim`, `supplier.claim.approve`) | G-026 |
| 74 | `/procurement/supplier-documents` | canonical | W | "Dokumen pemasok dan pencocokan": `procurement.read` | SAS | PROC (`procurement.supplier.document.*`) | G-026 |
| 75 | `/procurement/supplier-payables` | canonical | W | "Utang pemasok": `procurement.payable.*` | SAS | PROC (`procurement.payable.*`, `journal.post`) | G-026 |
| 76 | `/procurement/suppliers` | canonical | W | "Pemasok dan kode sumber": `supplier.change.propose/approve` | SAS | PROC (`supplier.change.*`, `procurement.*`) | — |
| 77 | `/reporting/accounts` | canonical | W | "Laporan akun dan jurnal": `report.read` | SAS | REPORT | — |
| 78 | `/shop/[product]` | canonical (member) | S | member product page (`catalog.read`) | BUY: guest → `/katalog/[id]` (full) or sign-in wall (release 1); sign-in link returns to `/shop`, **not the product** | VARIANTS, PHOTO | G-030 |
| 79 | `/shop` | canonical (member), pair of #48 | S | "Katalog kemasan": `catalog.read` (store "Katalog" for members) | BUY: guest → `/katalog` (full) or sign-in wall (release 1) | VARIANTS, PHOTO, REQUESTS | G-029, G-030 |
| 80 | `/staff/login` | alias of `/login` | none | legacy staff door | 307 → `/identity/login?returnPath=%2Fidentity%2Faccount` (D2 asks for `/workspace`) | none | K2 confirmed, G-008 |
| 81 | `/workspace` | duplicate hub (with #33) | W | "Pekerjaan saya" (staff door, sidebar "Beranda"): any signed-in user | ACC: guest sign-in link has no `returnPath`; pure buyers can open it | IDENTITY, ACCESS, CONTACT, WORK | G-010, G-020 |

Row numbering follows the alphabetical listing of `page.tsx` files. Count: 81 pages.
- canonical: 73 (including the two catalog pairs and the duplicate hubs #33 and #81)
- redirect: 5 (`/account`, `/account/orders`, `/catalog/browse`, `/checkout`, `/login`)
- alias: 3 (`/account/orders/[id]`, `/customer/login`, `/staff/login`)
- `/cart` redirects only in a release-1 build.

Frames: 61 routes use the workspace frame (`W`), 10 use the storefront frame (`S`), and 10 have no frame: the 8 redirects or aliases, plus `/` and `/identity/login`, which have their own header.

## Known duplicates (K2), confirmed

| Pair | Status today | Evidence |
|---|---|---|
| `/login`, `/customer/login`, `/staff/login`, `/identity/login` | The first three 307 to `/identity/login`. `/staff/login` and `/login?next=/workspace` land on `/identity/account`, not `/workspace`. `?next=/shop/<id>` loses the product. | `gap-guest-http.txt:9-13`, `app/login/page.tsx:10,16`; G-008 |
| `/account` vs `/identity/account` | 307 redirect, canonical `/identity/account`. A second hub `/workspace` renders the same `IdentityAccount` component. | `gap-guest-http.txt:16`; G-020 |
| `/account/orders` vs `/ordering/orders` | 307 redirect; `/account/orders/<id>` drops the id. | `gap-guest-http.txt:18-19`; G-009 |
| `/checkout` vs `/ordering/checkout` | 307 redirect. | `gap-guest-http.txt:21` |
| `/catalog/browse` vs `/shop` | 307 redirect, but still linked and kept as a return path and as a `BuyerCatalog` default. | `gap-guest-http.txt:8`; G-023 |
| `/katalog` (guest) vs `/shop` (member) | Both render. Guests are moved by client redirect `/shop` → `/katalog` (4 session reads per visit) and members the other way. Unknown ids answer 200. | `gap-guest-rendered.txt:30-46`; G-024, G-029 |

## Guest rule end to end (tamu), observed on the pilot

| Step | Observed | Verdict |
|---|---|---|
| Catalog `/katalog`, API `GET /api/v1/public/catalog` | 200, `total=0`, "Katalog publik belum dibuka". No price, no photo. The DTO carries no price or photo field. | No leak. **Rule unmet**: no sample products (G-001) |
| Search `?q=kopi`, categories | 200, 0 items, 0 categories. The input takes 128 characters, the API 64. | No leak (G-027 polish) |
| Product page `/katalog/<id>`, `/shop/<id>` as guest | Client redirect to `/katalog/<id>`, API 404. Page shows "Produk ini khusus untuk akun cabang" with no name, price or photo. | No leak; wording implies existence (G-024) |
| Cart `/cart` | Shell plus "Sesi Anda perlu diperbarui / Masuk kembali" (returnPath `/cart`). No API read beyond the session. | No leak; wrong wording for a guest (G-013) |
| Checkout `/ordering/checkout`, Orders `/ordering/orders`, Drafts | Page chunks 404 on the pilot, so the page stays on "Memeriksa akun…" for everyone. | No leak; dead page (G-006) |
| Staff pages (`/billing/invoices`, `/administration/catalog`, `/catalog/public`, `/inventory/sources`, `/administration/access`, `/fulfillment/receive`, `/ordering/reviews`) | The page shell renders: title, description and sidebar with "<page> · tidak tersedia" and "Delegasi sementara". No data. | No data leak; guests still see staff pages (G-012) |
| Anonymous API sweep (20 endpoints) | Everything outside public catalog, session and csrf answers 401 or 403. `/health/*` and `/openapi` are not exposed (404). | No leak |
| Prices, private photos, other organisations' data | None found in any guest page or anonymous response. | Pass (to be repeated once samples exist, Task 3.1) |

## Still needs persona probing (phase B, ENV review stack with synthetic sessions)

Each check below needs a signed-in persona and could not be done signed-out.
1. **pemohon (buyer template)**: does `/api/v1/access/current` include `order.cancel` and `receipt.accept`? If so, the store shows the staff door ("Ruang kerja", "Ruang kerja petugas"). Expected yes; G-017.
2. **pemohon**: reach `/billing/invoices`, `/billing/payments`, `/fulfillment/shipments`, `/fulfillment/receive` and `/fulfillment/cases` from the storefront. Today only the `/identity/account` directory links them (G-018). Also confirm the SAS 403 state on a staff page such as `/procurement/requests`.
3. **penyetuju-cabang**: store menu "Persetujuan belanja" → `/ordering/reviews` (frame switch, label collision with `/ordering/spending-policies`) (G-018, G-019).
4. **staf-pembeli**: one home or two (`/workspace` vs `/identity/account`); both doors visible; no page offered that the grants refuse (Review Focus 3, G-020).
5. **Every staff persona** × all 61 `W` routes, direct by URL and from the sidebar or palette:
   - Sidebar entries must match `allowedActions`.
   - A page whose client action check passes must not get a 403 from the API, and the reverse.
   - Suspect pairs:
     - `/billing/invoice-releases`: the frontend requires `invoice.release.read`; the server service checks `invoice.read` and `invoice.issue`.
     - `/ordering/commercial-policies`: the server reads with `order.submit`.
     - `/administration/organizations`: the frontend shows it for `access.audit.read`; the server service checks `technical.configure` and `organization.approve`.
     - `/procurement/supplier-claims`, `/procurement/supplier-documents`, `/procurement/inspections` and `/procurement/receiving`: shown for `procurement.read` only.
     - `/fulfillment/dispatches`: shown only with `shipment.dispatch` + `shipment.assign`, but `store-staff` has `shipment.dispatch` without `shipment.assign`, so the page is hidden from the role that dispatches (G-031).
     - `/procurement/stock-valuations`: shown only with three permissions.
6. **Wrong-role wording**: record the exact 403 text on every `W` route. About 25 modules (INV, ADM and own-guard pages) bypass SAS, and the texts differ (G-014).
7. **Session expiry mid-cart, mid-checkout and mid-staff-form**: does "Masuk kembali" return to the same page with work intact?
   - `?draft`, `?order` and `?package` are dropped (G-011).
   - `/inventory/replenishment` is refused by the server return-path list (G-007): confirm the real outcome at `/auth/callback`.
8. **petugas-katalog / peninjau-katalog on `/catalog/public`**: maker/checker and `ownProposal` are intact today per assortment. Repeat after Task 3.1 for per-product samples, and check that no proposer user id appears in responses.
9. **A signed-in staff member without `catalog.read`**, such as `keuangan-tagihan`, opening `/katalog` or the store "Katalog": the member redirect to `/shop`, then a 403, then possibly a dead end (G-029).
10. **`/api/v1/work/summary`** per persona: the counts must be limited to the persona's scopes (the service's permission checks were not traced statically).
11. **Guest rule after samples exist** (Task 3.1 plus ENV seed of 12 samples):
    - Only samples are listed.
    - A non-sample id answers "not available" through `/katalog/<id>`, `/shop/<id>` and the API.
    - The clean photo only.
    - Unpublish, hide or remove propagates immediately despite `max-age=60` (G-003).
12. **Dead-end sweep with navigation**: from every page, a way back (brand, sidebar or tabs) and forward (primary action) for each persona. Signed-out, every page had a way back.
13. **Release-1 build** (`isPilotRelease`), if it is still shipped: the guest `/shop` sign-in wall, and footer and menu links to proxied-404 pages (G-030).

## Observed (phase B, 21–22 Okt 2026)

Source: the loopback review stack `https://localhost:8443` at `main` 103bf357, which includes the FIX-FLOW merge of G-007 to G-031. Guest sample products and the UI system are not merged.
- Every route × 16 personas (`tamu` plus 15 synthetic sessions) at 1280 px: 1,296 page visits, paced at 170 API requests a minute or fewer.
- Logs: `tmp/website-ready/logs/gap-b-matrix.log` (one line per visit) and `gap-b-matrix.json` (final path, redirects, headings, notices, frame, API 4xx/5xx, sidebar and store links).
- Targeted checks: `gap-b-checks.txt`.
- The probe scripts are `gap-b-matrix.mjs` and `gap-b-checks.mjs` beside the logs.

**Caveat.** The review MongoDB (`127.0.0.1:27031`) stopped at about 01.06 WIB, after the matrix finished: its log ends without a shutdown message, and the port refuses connections. The matrix has no 5xx. The checks that need the API afterwards could not be completed: `access/current`, the store account menu, `work/summary`, the showcase query, and the problem codes behind the 409s. Rerun `gap-b-checks.mjs` once the review stack is restarted.

### Observed result per route (signed-in personas; `tamu` noted where relevant)

Legend:
- **ok**: every persona that should reach the page sees content, and every other persona sees a denial state.
- **409**: the page opens, but its first read (`…/targets/query` or similar) answers 409. The page shows "Data atau izin telah berubah…", or "Riwayat … belum dapat dipastikan lengkap", for every persona that should reach it (G-034).

| Route | tamu | Expected personas | Others | Gaps |
|---|---|---|---|---|
| `/` | content (catalog links now go to `/katalog`) | content | content | — |
| `/account`, `/account/orders`, `/checkout`, `/catalog/browse` | 307 to the canonical page, then sign-in with `returnPath` | 307 to the canonical page | same | K2 fixed |
| `/account/orders/[id]` | → `/identity/login?returnPath=/ordering/orders?order=1` | → `/ordering/orders?order=1`, notice "Filter belum dapat dimuat…" for an unknown id | same | G-009 fixed; G-045 |
| `/login`, `/customer/login`, `/staff/login` | → `/identity/login` (`/staff/login` adds `returnPath=/workspace`) | signed in: `/staff/login` → own home (`/shop` for pemohon, `/workspace` for staff) | — | G-008 fixed |
| `/identity/login` | sign-in page | signed in: "Lanjutkan" → `/workspace` for everyone, buyers included (one extra hop to `/shop`) | — | G-010 fixed; G-044 |
| `/workspace` | → sign-in `returnPath=/workspace` | staff: home with sidebar. pemohon: → `/shop` | — | G-020 fixed |
| `/identity/account` | → sign-in | content | content | — |
| `/katalog`, `/katalog/[id]` | **251 products** (the whole public assortment); no price, no photo | signed in → `/shop` | 12 non-buyer personas: `/shop`, then 403 "Katalog ini belum tersedia untuk akun Anda" | G-001 open; G-029 confirmed; G-032 |
| `/shop`, `/shop/[product]` | → `/katalog` | pemohon, penyetuju-cabang, staf-pembeli: priced catalog | 7 staff personas whose sidebar lists "Katalog kemasan" (`/shop`) get 403 | G-032 |
| `/cart`, `/ordering/checkout`, `/ordering/drafts`, `/ordering/orders` | → sign-in keeping the query | content (pemohon, staf-pembeli) | SAS denial; offered anyway in the store header to staff without `order.submit` | G-033 |
| `/ordering/requests`, `/ordering/requests/incoming`, `/ordering/reviews`, `/ordering/substitutions` | → sign-in | ok | ok | — |
| `/ordering/spending-policies`, `/ordering/commercial-policies` | → sign-in | **409** | ok | G-034 |
| `/billing/invoices`, `/billing/payments`, `/fulfillment/receive`, `/fulfillment/cases`, `/fulfillment/shipments` | → sign-in | content, but buyers open them in the workspace frame | `/fulfillment/receive`: store-staff (no `receipt.accept`) get the scan screen without a denial | G-018 partly fixed; G-037; G-038 |
| `/billing/invoice-releases`, `/billing/payment-reviews`, `/billing/receiving-accounts` | → sign-in | ok | ok | nav/API suspicion refuted |
| `/billing/bank-receipts`, `/billing/bank-statements` | → sign-in | **pemeriksa-pembayaran denied** (API 403 on `bank-statements/imports/query`, `bank-receipts/query`) although it holds `bank.receipt.read` and `bank.statement.read`, and the sidebar lists both | ok | G-035 |
| `/accounting/books`, `/reporting/accounts` | → sign-in | books: **409**; reports: ok | ok | G-034 |
| `/fulfillment/preparations`, `/fulfillment/courier-assignments` | → sign-in | ok | ok | — |
| `/fulfillment/dispatches` | → sign-in | petugas-kirim ok; now also open to every store-staff holder of `shipment.dispatch` | — | G-031 fixed |
| `/inventory/*` (11 routes) | → sign-in | ok | denial in INV wording ("… belum tersedia / Akses untuk tindakan ini tidak tersedia. Muat ulang…") | G-014 open |
| `/procurement/*` (16 routes) | → sign-in | **409** on 15 of the 16 routes for every expected persona (all except `/procurement/quality-acceptances`) | SAS denial; 10 routes also stack a second alert | G-034, G-042 |
| `/administration/access`, `/administration/organizations` | → sign-in | ok | ADM denial plus a trace id | G-014, G-016 |
| `/administration/accounts` | → sign-in | **admin-akses and penyetuju-akses: 400** on `directory/query` and `invitations/query` ("Permintaan belum dapat diterima") | ADM denial | G-036 |
| `/administration/person-reviews` | → sign-in | **403**: "Peninjauan identitas petugas belum diaktifkan" | ADM denial | G-039 |
| `/administration/history` | → sign-in | ok | **every persona sees the purpose form** and no denial | G-040 |
| `/administration/organizations/addresses` | → sign-in | **409** "Riwayat alamat belum dapat dipastikan lengkap" for all 11 expected personas | ok | G-034 |
| `/administration/delegations` | → sign-in | content | — (now listed only for staff) | G-028 fixed |
| `/administration/catalog`, `/catalog/prices`, `/catalog/public` | → sign-in | ok (the showcase is still per assortment) | `/catalog/prices` tells a buyer "Pemeriksa harus orang terverifikasi yang berbeda dari pengaju" | G-002 open; G-041 |
| `/operations/health`, `/operations/anomalies` | → sign-in | ok | denials word it as "Periksa sesi dan akses", although the session is fine | G-014 |

### Persona checks 1–13 (from phase A)

1. **G-017: pemohon is not staff.** Confirmed fixed: no staff door, `/workspace` → `/shop` (`gap-b-matrix.log:160`). penyetuju-cabang is classified as staff because of `order.spending.policy.read`, and gets the staff door plus `/workspace` (`gap-b-matrix.log:240`) (G-043).
2. **Buyer tasks.** `/billing/invoices`, `/billing/payments`, `/fulfillment/receive`, `/fulfillment/cases` and `/fulfillment/shipments` open for pemohon (`gap-b-matrix.log:97`), but inside the workspace frame (G-037). The account menu shows them per `buyerTasks` (code: `store-frame.tsx:136`); the click-through was not re-run (API down).
3. **Approval label.** "Peninjauan pesanan" (`/ordering/reviews`) and "Aturan persetujuan belanja" (`/ordering/spending-policies`) are now distinct (G-019 fixed in code; the sidebar shows both for penyetuju-cabang).
4. **staf-pembeli.** One home `/workspace`, with the staff door plus "Katalog kemasan". Priced `/shop` works. Buyer tasks are in the sidebar.
5. **Sidebar vs API.** Suspicions refuted: `invoice-releases`, `organizations`, `payment-reviews`.
   - Confirmed mismatches: `/shop` for 7 staff (G-032), and `bank-receipts`/`bank-statements` for pemeriksa-pembayaran (G-035).
   - Inconclusive because of the systemic 409 (G-034): `commercial-policies`, the procurement pages, `stock-valuations`.
   - `dispatches`: fixed.
6. **Wrong-role wording.** 33 routes use the SAS denial "Halaman ini di luar tugas akun Anda". 10 of those add a second alert, "Akses untuk tindakan ini tidak tersedia. Muat ulang…" (G-042). About 20 routes keep their own wording (`gap-b-matrix.json`). G-014 stays open.
7. **Expiry (simulated with an invalid session cookie).**
   - The return paths now keep `?draft`, `?order` and `?package`. `/workspace` and `/identity/account` links carry `returnPath`. The guest proxy keeps the query (`gap-b-checks.txt:115-133`). G-010, G-011 and G-012 are fixed.
   - The `/inventory/replenishment` return path is accepted by both lists (G-007 fixed in code; `/auth/callback` was not exercised).
   - `/fulfillment/receive` still shows "Koneksi terputus…" on expiry, but the API was degraded at that moment, so this is not re-verified (G-015).
8. **Catalog showcase.** The page lists whole assortments, as before (G-002). The `ownProposal` and proposer-id check is pending (API down).
9. **Staff without `catalog.read`.** `/katalog` → `/shop`, then 403, for kurir, keuangan-tagihan, pemeriksa-pembayaran, admin-akses and penyetuju-akses (`gap-b-matrix.log:682`). G-029 confirmed.
10. **`work/summary`.** Pending (API down).
11. **Guest rule.** The review stack shows guests all 251 products from the approved assortment (`gap-b-matrix.log:47`). Still the K1 behaviour (G-001 open; Task 3.1 not merged). No price, no photo, and the product page carries no price.
12. **Dead ends.** Every signed-in page rendered inside a frame with a way back (sidebar or store header). None was frameless.
13. **Release-1 build.** Not deployed on the review stack. Not re-checked (G-030).
