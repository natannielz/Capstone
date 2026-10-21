# Journey matrix and golden journey (FUNC, Task 1.2 phase A)

Owner question: "I do not see the functions actually working." This file lists, for every persona, every function its navigation offers, which existing browser rehearsal exercises it, and its status. Phase B tests the `uncovered` rows by hand on the review stack and writes the golden journey designed in section 5.

Sources read (21 Okt 2026, base `dc94c4b7`): `frontend/src/modules/identity/identity-workflow.ts` (navigation destinations and the permission each needs), `workspace-navigation.ts`, `workspace-directory.ts`, `frontend/src/components/store/store-navigation.ts` + `store-frame.tsx`, `frontend/src/lib/release-scope.ts` + `frontend/src/proxy.ts`, all 81 `frontend/src/app/**/page.tsx` and their module components, the module READMEs, `backend/src/UnitToko.Modules/Identity/RoleTemplates.cs`, `Permissions.cs`, `backend/src/UnitToko.Api/Authorization/AccessEndpoints.cs` (`CurrentActions`, the `allowedActions` list the browser receives), all 80 `frontend/scripts/verify-*-browser.mjs` / `*-harness.mjs` and all 67 `backend/tests/UnitToko.Tests/**/*BrowserTests*.cs`.

Legend
- **Status**: `covered` = an existing rehearsal drives this action in a real browser (often with a fixture actor that holds the same permission, not this persona's exact grant set); `uncovered` = no rehearsal drives it (includes rehearsals that are always skipped); `failing` = the orchestrator pastes it from `tmp/website-ready/logs/rehearsals-*.log` into the **Run** column.
- **R-ID**: see section 3 for script → xUnit class → test method.
- Release: rows describe the `full` release (`NEXT_PUBLIC_UNIT_TOKO_RELEASE=full`), which is what every xUnit browser driver runs. The pilot release is section 6.
- Navigation is presentation only: a row exists when `identityDestinations(allowedActions)` shows the entry. `allowedActions` comes from `GET /api/v1/access/current` = `CurrentActions(...)`, which only ever emits a fixed list; permissions outside it never reach the browser (see F-001).

---

## 1. Personas

Grants are FUNC's proposal of the minimum a persona needs to exercise its functions, built from the existing role templates plus named additions. ENV's `PilotPersonas` (Task 0.3, `backend/tools/UnitToko.LocalPilot.Tests/PilotSessionsTests.cs` in `Capstone-wt/env`) is authoritative once merged; its test currently asserts one key permission per persona, all consistent with this table. Additions marked **(!)** are permissions no role template or template suggestion contains (F-006); without them the persona's main function has no navigation entry or no working button.

| Persona | Template (+packs) | Additions | What the persona is for | Permissions that never reach the browser |
|---|---|---|---|---|
| `tamu` | none (no session) | none | Guest: sample catalog without prices | n/a |
| `pemohon` | `buyer` | `order.request.read`, `order.request.submit`, `substitution.accept`, `organization.address.read` (template suggestions) | Branch buyer: cart, checkout, receive, pay, complain | `order.substitute` |
| `penyetuju-cabang` | `buyer` | `order.spend.approve` **(!)** | Branch spending approver; different verified person from the requester, fresh MFA | `order.substitute` |
| `peninjau-toko` | `store-head` | `order.accept`, `order.request.read`, `order.request.decide` (suggestions) | Store acceptance of branch orders and branch requests | `stock.adjust.approve`, `expense.request` |
| `petugas-katalog` | `store-head` | `catalog.public.propose` **(!)** | Catalog maker: families, packages, codes, photos, prices, guest samples | same as store-head |
| `peninjau-katalog` | `store-head` | `pricing.approve` **(!)**, `catalog.public.approve` **(!)** | Price and guest-sample checker | same as store-head |
| `petugas-gudang` | `store-staff` | `stock.release.propose`, `stock.convert`, `substitution.propose` (suggestions), `procurement.container.receive` **(!)**, `procurement.inspection.record` **(!)**, `procurement.qualityacceptance.propose` **(!)** | Warehouse: preparation, receiving, inspection, held stock | `stock.adjust.propose`, `source.prepare`, `expense.request` |
| `petugas-kirim` | `store-staff` | `shipment.assign` **(!)** (dispatch page needs `shipment.dispatch` AND `shipment.assign`) | Dispatch with a courier, courier handover | same as store-staff |
| `kurir` | `courier` | none; needs an approved courier roster assignment | Delivery attempts, receipt QR, proof | `delivery.return`, `expense.request` |
| `keuangan-tagihan` | `finance` + `billing` | `invoice.release.read` **(!)**, `invoice.release.propose` **(!)**, `receiving.account.read`, `procurement.stockvaluation.read`, `procurement.stockvaluation.propose` (suggestions), `procurement.read`, `stock.read` | Invoice release proposal, invoice issue, stock valuation proposal | `payment.record` |
| `pemeriksa-pembayaran` | `finance` + `billing` | `payment.review.read`, `payment.review.correct` (suggestions), `payment.verify` **(!)**, `invoice.release.read` **(!)**, `invoice.release.approve` **(!)**, `bank.receipt.read`, `bank.receipt.confirm`, `procurement.stockvaluation.read`, `procurement.stockvaluation.decide`, `stock.release.approve` (suggestions), `procurement.read`, `stock.read` | Payment verification, invoice release checker, valuation and stock-release checker | `payment.record` |
| `pengadaan` | `store-staff` | `supplier.change.propose` (suggestion), `procurement.supplier.document.record` **(!)**, `procurement.supplier.document.match` **(!)** | Procurement maker: request, PO, supplier, supplier invoice | same as store-staff |
| `penyetuju-pengadaan` | `store-head` | `supplier.change.approve` (suggestion), `procurement.qualityacceptance.decide` **(!)** | Procurement checker: request and PO approval, supplier approval, quality decision | same as store-head |
| `admin-akses` | `system-admin` | none (a second admin holds `access.approve`/`membership.approve` for maker/checker) | Accounts, invitations, access, organizations, health | `invite.manage`, `recovery.initiate` (F-001) |
| `staf-pembeli` | `store-staff` | `order.submit` | Store staff who also buys | same as store-staff |

Navigation each persona sees (full release, computed by evaluating `destinationEntries` with the emitted grant set):

| Persona | Workspace navigation entries (besides Beranda and Akun saya) |
|---|---|
| tamu | none (storefront only: `/`, `/katalog`, sign-in links) |
| pemohon | Delegasi sementara, Permintaan saya, Keluhan barang, Kiriman, Penerimaan kiriman, Tagihan, Bukti pembayaran, Draf pesanan, Pesanan saya, Penggantian barang, Peninjauan pesanan (cancel), Katalog kemasan, Alamat organisasi |
| penyetuju-cabang | Delegasi, Keluhan barang, Kiriman, Penerimaan kiriman, Tagihan, Bukti pembayaran, Draf pesanan, Pesanan saya, Peninjauan pesanan, Katalog kemasan |
| peninjau-toko | Delegasi, Permintaan saya, Permintaan masuk, Keluhan barang, Kiriman, Penugasan kurir, Penerimaan stok ditahan, Klaim pemasok, Dokumen pemasok, Pemeriksaan barang pengadaan, Penerimaan pengadaan, Otorisasi uang muka, Pesanan pembelian, Permintaan pengadaan, Pesanan saya, Peninjauan pesanan, Katalog kemasan, Harga kemasan, Hitung barang, Kebutuhan pembelian, Katalog toko |
| petugas-katalog | as peninjau-toko without Permintaan saya / Permintaan masuk / Peninjauan pesanan, plus Katalog publik |
| peninjau-katalog | same as petugas-katalog |
| petugas-gudang | Delegasi, Persiapan kiriman, Kiriman, Penerimaan mutu barang, Penerimaan stok ditahan, Klaim pemasok, Dokumen pemasok, Pemeriksaan barang pengadaan, Penerimaan pengadaan, Pesanan pembelian, Permintaan pengadaan, Pesanan saya, Penggantian barang, Katalog kemasan, Hitung barang, Kebutuhan pembelian, Pelepasan stok ditahan, Aturan buka kemasan, Pelaksanaan buka kemasan |
| petugas-kirim | Delegasi, Persiapan kiriman, Keberangkatan kiriman, Kiriman, Penugasan kurir, the store-staff procurement pages, Pesanan saya, Katalog kemasan, Hitung barang, Kebutuhan pembelian |
| kurir | Delegasi sementara, Kiriman |
| keuangan-tagihan | Delegasi, Tagihan, Persetujuan penerbitan tagihan, Rekening penerimaan, Penilaian stok ditahan, procurement read pages, Hitung barang, Kebutuhan pembelian |
| pemeriksa-pembayaran | Delegasi, Tagihan, Persetujuan penerbitan tagihan, Penerimaan bank, Penilaian stok ditahan, procurement read pages, Pemeriksaan pembayaran, Hitung barang, Kebutuhan pembelian, Pelepasan stok ditahan |
| pengadaan | Delegasi, Persiapan kiriman, Kiriman, Pemasok dan kode sumber, store-staff procurement pages, Pesanan saya, Katalog kemasan, Hitung barang, Kebutuhan pembelian |
| penyetuju-pengadaan | store-head list plus Pemasok dan kode sumber and Penerimaan mutu barang (no ordering review entries) |
| admin-akses | Delegasi, Akun dan undangan, Akses dan keanggotaan, Alamat organisasi, Organisasi dan unit, Kesehatan sistem, Riwayat perubahan |
| staf-pembeli | store-staff list plus Draf pesanan |

Storefront header for a signed-in member (`store-frame.tsx`): Katalog (`/shop`), Keranjang (`/cart`), Pesanan (`/ordering/orders`), account menu (Pesanan saya, Persetujuan belanja when `order.spend.approve`, Ruang kerja petugas when `staff`). `staff` is true for every `buyer`-template holder because `order.cancel`, `receipt.accept`, `substitution.accept` and `organization.address.read` are not in `buyerActions` (F-002).

---

## 2. Function matrix per persona

Counting rule: a persona's functions are its own rows plus the shared blocks it lists. Shared blocks are written once.

### 2.0 Shared block ALL (every signed-in persona)

| ID | Page | Action | Permission | Rehearsal | Status | Run |
|---|---|---|---|---|---|---|
| ALL-01 | `/identity/login` | Masuk through the identity provider and return to the page | session | R62, R66, R69, R70 (sign-in at `/identity/login`) | covered | |
| ALL-02 | `/identity/account` | Session state, "Verifikasi dua langkah" step-up for listed operations | session | R04, R46, R51 (step-up before decisions) | covered | |
| ALL-03 | `/identity/account` | "Simpan kontak pribadi", contact history | session | R01 | covered | |
| ALL-04 | `/workspace` | "Pekerjaan saya": directory of allowed destinations, WorkSummary counts | session | R76 (runs inside R47 for the procurement maker only) | uncovered (only one persona's directory is asserted) | |
| ALL-05 | `/administration/delegations` | See temporary delegations involving the account | session (always listed) | R75 (finance maker/approver create one) | uncovered for every persona except the delegation maker/approver | |
| ALL-06 | any workspace page | "Pastikan hasil dan tutup" / "Periksa hasil" recovery of an unconfirmed command | session | most rehearsals (lost-reply recovery is in nearly every script) | covered | |

### 2.1 Shared block BUY (`buyer` template: pemohon, penyetuju-cabang)

| ID | Page | Action | Permission | Rehearsal | Status | Run |
|---|---|---|---|---|---|---|
| BUY-01 | `/` | Landing, "Lihat katalog" | none | R11 | covered | |
| BUY-02 | `/shop` | Browse, search, page, open package details with price and photo | `catalog.read` | R11, R09 (buyer reads current photo), R07 | covered | |
| BUY-03 | `/shop/[product]` | "Tambah ke keranjang" (branch, quantity stepper, subtotal) | `order.submit` | R11 | covered | |
| BUY-04 | `/cart` | "Lihat keranjang": lines and "Ringkasan belanja" | `order.submit` | R11 | covered | |
| BUY-05 | `/cart` | Change quantity, "Hapus" a line, empty the cart | `order.submit` | none | uncovered | |
| BUY-06 | `/cart` | "Pakai harga terbaru" / "Pakai kemasan terbaru" after a price or package change | `order.submit` | none | uncovered | |
| BUY-07 | `/cart` → `/ordering/checkout?draft=` | "Lanjut ke pengajuan", delivery, approved branch address, "Tinjau pesanan dan biaya", "Ajukan untuk persetujuan"; result heading "Pesanan PSN-… tercatat" | `order.submit` | R27, R04 (R04 currently fails at dispatch, F-003) | covered | |
| BUY-08 | `/ordering/drafts` | Reopen draft, edit quantities, "Cari kemasan", approved address | `order.submit` | R11, R24, R07 | covered | |
| BUY-09 | `/ordering/drafts` | "Terapkan daftar ke draf" (bulk entry by code) | `order.submit` | R24, R07 | covered | |
| BUY-10 | `/ordering/drafts` | Discard a draft (restorable 30 days) | `order.submit` | none | uncovered | |
| BUY-11 | `/ordering/orders` | List and detail of own orders by PSN | `order.read` | R04, R25 | covered | |
| BUY-12 | `/ordering/orders` | Saved views: save, share, rename, remove filters | `order.read` | R30 | covered | |
| BUY-13 | `/ordering/orders` | "Pesan lagi" into the branch cart | `order.read` + `order.submit` | R25 | covered | |
| BUY-14 | `/ordering/orders` | Order timeline: delivery, invoice, payment, return outcomes | `order.read` (+ shipment/invoice/payment/case reads) | R29 — always skipped `Pending.TransactionLatency` | uncovered (F-005) | |
| BUY-15 | `/ordering/reviews` | "Pembatalan pesanan": cancel own outstanding order | `order.cancel` | none | uncovered | |
| BUY-16 | `/fulfillment/shipments` | See own shipments ("Lihat kiriman E/C/Y/N") | `shipment.read` | R04, R61 | covered | |
| BUY-17 | `/fulfillment/receive` | Open courier QR link, "Hitung barang yang tiba", "Tinjau jumlah", "Konfirmasi penerimaan" | `receipt.accept` | R61, R13 | covered | |
| BUY-18 | `/billing/invoices` | Open invoice, "Unduh invoice" | `invoice.read` | R15a, R07 | covered | |
| BUY-19 | `/billing/payments` | "Simpan draf pembayaran", "Unggah bukti asli", "Ajukan untuk verifikasi" | `payment.evidence` + `invoice.read` | R35, R39 | covered | |
| BUY-20 | `/billing/payments` | Supplementary PDF pages for a payment | `payment.evidence` | R37, R38 | covered | |
| BUY-21 | `/billing/payments` | Follow-up from reviewer, "Siapkan bukti pengganti", "Buat draf pengganti" | `payment.evidence` | R39 | covered | |
| BUY-22 | `/fulfillment/cases` | "Laporkan masalah barang", "Kirim keluhan" (KLH-…) | `case.request` | R15b | covered | |
| BUY-23 | `/fulfillment/cases` | Follow own complaint and the private decision explanation | `case.read` | R15c | covered | |
| BUY-24 | storefront | Header "Ruang kerja petugas" / account menu links | display | none | uncovered (F-002) | |

### 2.2 Shared block SH (`store-head` template: peninjau-toko, petugas-katalog, peninjau-katalog, penyetuju-pengadaan)

| ID | Page | Action | Permission | Rehearsal | Status | Run |
|---|---|---|---|---|---|---|
| SH-01 | `/shop` | Browse catalog with prices | `catalog.read` | R11 | covered | |
| SH-02 | `/administration/catalog` | Categories, families, packaging variants: create, "Ubah deskripsi", "Buat pengganti", "Publikasi", "Hentikan" | `catalog.manage` | none (only the identifier and photo panels are rehearsed) | uncovered | |
| SH-03 | `/administration/catalog` | "Kode dan GTIN": assign/retire identifiers | `catalog.manage` | R08 | covered | |
| SH-04 | `/administration/catalog` | "Foto": upload, review, replace private photo | `catalog.manage` | R09 | covered | |
| SH-05 | `/catalog/prices` | "Tinjau pengajuan harga": propose package price | `pricing.manage` | R10 | covered | |
| SH-06 | `/fulfillment/courier-assignments` | "Tinjau penyerahan tugas": courier handover | `shipment.assign` | R13 | covered | |
| SH-07 | `/fulfillment/shipments` | Store shipments, "Tinjau Surat Jalan" | `shipment.read` | R60 | covered | |
| SH-08 | `/fulfillment/cases` | "Tinjau keputusan keluhan", "Izinkan pengembalian barang", return receipt | `case.manage` + `case.read` | R15c, R15d | covered | |
| SH-09 | `/procurement/requests` | "Tinjau persetujuan", "Setujui permintaan" (PP-…) | `procurement.approve` | R48 | covered | |
| SH-10 | `/procurement/requests` | Reject a procurement request | `procurement.approve` | none | uncovered | |
| SH-11 | `/procurement/purchase-orders` | "Tinjau persetujuan pesanan", "Setujui pesanan secara internal" (SP-…) | `procurement.approve` | R54, R56 | covered | |
| SH-12 | `/procurement/purchase-orders` | Review supplier arrival schedule | `procurement.approve` | R55 | covered | |
| SH-13 | `/procurement/funding` | "Periksa permintaan uang muka": request advance | `funding.request` | R44 | covered | |
| SH-14 | `/procurement/supplier-claims` | "Periksa klaim pemasok": claim damaged goods | `supplier.claim` | R72 | covered | |
| SH-15 | `/procurement/receiving`, `/procurement/inspections`, `/procurement/supplier-documents`, `/procurement/stock-receipts` | Read-only review of procurement records | `procurement.read` (+ `stock.read`) | R47, R45, R73, R50 | covered | |
| SH-16 | `/inventory/counts` | See count observations | `stock.read` | R62 | covered | |
| SH-17 | `/inventory/replenishment` | "Hitung dan simpan hasil" | `stock.read` | R58 | covered | |
| SH-18 | `/ordering/orders` | "Pesanan saya": lists only orders the account created (empty for store staff) | `order.read` | none | uncovered (F-010) | |

### 2.3 Shared block SS (`store-staff` template: petugas-gudang, petugas-kirim, pengadaan, staf-pembeli)

| ID | Page | Action | Permission | Rehearsal | Status | Run |
|---|---|---|---|---|---|---|
| SS-01 | `/shop` | Browse catalog with prices | `catalog.read` | R11 | covered | |
| SS-02 | `/fulfillment/preparations` | Choose packages and quantities, "Tinjau persiapan", "Konfirmasi persiapan" | `shipment.prepare` | R04, R22, R27, R28 | covered | |
| SS-03 | `/fulfillment/shipments` | Shipments, "Tinjau Surat Jalan" | `shipment.read` + `shipment.prepare` | R60 | covered | |
| SS-04 | `/procurement/requests` | "Buat permintaan", "Tambahkan kemasan", "Kirim permintaan" (PP-…) | `procurement.prepare` | R48, R58 | covered | |
| SS-05 | `/procurement/purchase-orders` | "Buat pesanan pembelian", "Tambahkan barang", "Kirim usulan pesanan" (SP-…) | `procurement.prepare` | R54 | covered | |
| SS-06 | `/procurement/purchase-orders` | Arrival schedule proposal, inbound receiving pool plan | `procurement.prepare` | R55, R56 | covered | |
| SS-07 | `/procurement/receiving`, `/procurement/inspections`, `/procurement/supplier-documents`, `/procurement/supplier-claims` | Read-only review | `procurement.read` | R47, R45, R73, R72 | covered | |
| SS-08 | `/procurement/stock-receipts` | "Catat stok ditahan", "Simpan penerimaan stok ditahan" | `procurement.read` + `stock.read` + `stock.receive` | R50, R23 | covered | |
| SS-09 | `/inventory/counts` | "Tinjau hitungan", "Simpan pengamatan" (scan or type) | `stock.count` | R62, R08 | covered | |
| SS-10 | `/inventory/replenishment` | "Hitung dan simpan hasil", create procurement work | `stock.read` + `procurement.prepare` | R58 | covered | |
| SS-11 | `/ordering/orders` | "Pesanan saya" (empty for staff) | `order.read` | none | uncovered (F-010) | |

### 2.4 Persona rows

#### tamu

| ID | Page | Action | Permission | Rehearsal | Status | Run |
|---|---|---|---|---|---|---|
| TAM-01 | `/` | Landing, "Lihat katalog" | none | R11 | covered | |
| TAM-02 | `/katalog` | Sample products without prices, search | none | none | uncovered (being rebuilt by Task 3.1) | |
| TAM-03 | `/katalog/[publicId]` | Sample product page, "Masuk" call to action | none | none | uncovered | |
| TAM-04 | storefront | Keranjang / Pesanan / Akun / Ruang kerja petugas links go to sign-in with the right `returnPath` | none | none | uncovered | |
| TAM-05 | `/shop`, staff pages | Guest is sent to sign-in; never sees prices, private photos, staff pages | none | none (GAP probes) | uncovered | |

#### pemohon — ALL + BUY + rows below

| ID | Page | Action | Permission | Rehearsal | Status | Run |
|---|---|---|---|---|---|---|
| PMH-01 | `/ordering/requests` | Compose and send a branch request ("Cari kemasan", review, send) | `order.request.submit` | R20 only (home.arpa pilot script, no xUnit driver, off-limits) | uncovered | |
| PMH-02 | `/ordering/requests` | Follow store decisions on own requests | `order.request.read` | none | uncovered | |
| PMH-03 | `/ordering/substitutions` | Review replacement, decline or consent | `substitution.accept` | R28 | covered | |
| PMH-04 | `/administration/organizations/addresses` | Read approved organization addresses | `organization.address.read` | R31 (reader scope) | covered | |

#### penyetuju-cabang — ALL + BUY + rows below

| ID | Page | Action | Permission | Rehearsal | Status | Run |
|---|---|---|---|---|---|---|
| PYC-01 | `/ordering/reviews` | "Tinjau pesanan PSN-…", "Setujui belanja", "Konfirmasi persetujuan belanja" (fresh MFA, different person) | `order.spend.approve` | R04, R27 | covered | |
| PYC-02 | `/ordering/reviews` | "Tolak belanja" | `order.spend.approve` | R27 | covered | |
| PYC-03 | storefront account menu | "Persetujuan belanja" link | display | none | uncovered | |

#### peninjau-toko — ALL + SH + rows below

| ID | Page | Action | Permission | Rehearsal | Status | Run |
|---|---|---|---|---|---|---|
| PTK-01 | `/ordering/reviews` | "Terima pesanan toko", "Konfirmasi penerimaan toko" | `order.accept` | R04 | covered | |
| PTK-02 | `/ordering/reviews` | Store rejection of its part of an order | `order.accept` | none | uncovered | |
| PTK-03 | `/ordering/requests/incoming` | Inspect a branch request and give the store decision | `order.request.decide` | R20 only (no driver) | uncovered | |
| PTK-04 | `/ordering/requests` | Read branch requests | `order.request.read` | none | uncovered | |

#### petugas-katalog — ALL + SH + rows below

| ID | Page | Action | Permission | Rehearsal | Status | Run |
|---|---|---|---|---|---|---|
| KAT-01 | `/catalog/public` | Propose which products guests may see (one proposal with reason) | `catalog.public.propose` | none | uncovered (Task 3.1 rewrites the page) | |

#### peninjau-katalog — ALL + SH + rows below

| ID | Page | Action | Permission | Rehearsal | Status | Run |
|---|---|---|---|---|---|---|
| PKT-01 | `/catalog/prices` | Independent price decision (approve/reject), maker cannot approve own | `pricing.approve` | R10 | covered | |
| PKT-02 | `/catalog/public` | Approve or reject the guest-sample proposal | `catalog.public.approve` | none | uncovered | |

#### petugas-gudang — ALL + SS + rows below

| ID | Page | Action | Permission | Rehearsal | Status | Run |
|---|---|---|---|---|---|---|
| GDG-01 | `/procurement/receiving` | "Catat pemberitahuan pemasok", "Catat penerimaan fisik koli" | `procurement.container.receive` | R47 | covered | |
| GDG-02 | `/procurement/inspections` | "Buat draf pemeriksaan", "Unggah video asli", "Ajukan hasil dalam karantina" | `procurement.inspection.record` | R45 | covered | |
| GDG-03 | `/procurement/quality-acceptances` | "Ajukan penerimaan mutu" | `procurement.qualityacceptance.propose` | R46 | covered | |
| GDG-04 | `/inventory/stock-releases` | Propose release of held stock | `stock.release.propose` | R63 | covered | |
| GDG-05 | `/inventory/pack-conversions` | "Minta pratinjau aturan", propose rule | `stock.convert` | R32 | covered | |
| GDG-06 | `/inventory/pack-conversion-executions` | "Minta pratinjau pelaksanaan", "Catat konversi fisik" | `stock.convert` | R33 | covered | |
| GDG-07 | `/ordering/substitutions` | "Cari kemasan pengganti", propose replacement | `substitution.propose` | R28 | covered | |
| GDG-08 | `/procurement/stock-receipts` | Scan supplier label to confirm held receipt | `stock.receive` | R23 | covered | |

#### petugas-kirim — ALL + SS + rows below

| ID | Page | Action | Permission | Rehearsal | Status | Run |
|---|---|---|---|---|---|---|
| KRM-01 | `/fulfillment/dispatches` | "Periksa keberangkatan pesanan PSN-…", choose lot and courier, "Tinjau keberangkatan", "Konfirmasi keberangkatan" | `shipment.dispatch` + `shipment.assign` | R60, R22, R28; R04 fails here (F-003) | covered | |
| KRM-02 | `/fulfillment/dispatches` | Scan-assisted picking and dispatch | `shipment.dispatch` + `shipment.assign` | R22 | covered | |
| KRM-03 | `/fulfillment/courier-assignments` | "Tinjau penyerahan tugas" | `shipment.assign` | R13 | covered | |

#### kurir — ALL + rows below

| ID | Page | Action | Permission | Rehearsal | Status | Run |
|---|---|---|---|---|---|---|
| KUR-01 | `/fulfillment/shipments` | Assigned shipments only | `shipment.read` | R61, R13 | covered | |
| KUR-02 | `/fulfillment/shipments` | "Upaya pengiriman": "Tinjau upaya pengiriman", "Simpan upaya pengiriman" | `delivery.attempt` | R61 | covered | |
| KUR-03 | `/fulfillment/shipments` | "Tinjau penerima", "Buat QR penerimaan", "Buka tautan penerimaan" | `delivery.evidence` | R61, R13 | covered | |
| KUR-04 | `/fulfillment/shipments` | "Tinjau bukti penerimaan" | `delivery.evidence` | R61 | covered | |
| KUR-05 | none | Record goods returned (`delivery.return`) | `delivery.return` | none; permission never emitted and no page offers it | uncovered | |

#### keuangan-tagihan — ALL + rows below

| ID | Page | Action | Permission | Rehearsal | Status | Run |
|---|---|---|---|---|---|---|
| KEU-01 | `/billing/invoice-releases` | Propose issuing approval (source, fees, format, books) | `invoice.release.propose` | R19 — always skipped `Pending.BrowserLatency` | uncovered (F-004) | |
| KEU-02 | `/billing/invoices` | "Tinjau invoice", "Tinjau penerbitan invoice", "Terbitkan invoice" | `invoice.issue` | R15a (issues through a fixture admission, not a real release) | covered (fixture path only, F-004) | |
| KEU-03 | `/billing/invoices` | Open and "Unduh invoice" | `invoice.read` | R15a | covered | |
| KEU-04 | `/billing/receiving-accounts` | Read receiving accounts | `receiving.account.read` | R57 | covered | |
| KEU-05 | `/procurement/stock-valuations` | "Periksa penilaian stok": propose valuation of held stock | `procurement.stockvaluation.propose` | R51, R52 | covered | |
| KEU-06 | `/procurement/*` read pages, `/inventory/counts`, `/inventory/replenishment` | Read-only | `procurement.read`, `stock.read` | R47, R45, R73, R50, R62, R58 | covered | |
| KEU-07 | (no page reachable) | `credit.request`, `refund.request` (live on `/fulfillment/cases`, needs `case.read`), `payment.allocate` (on `/billing/payment-reviews`, needs `payment.review.read`), `disbursement.record` (on `/procurement/disbursements`, needs `procurement.disbursement.read`) from the billing pack | billing pack | R16, R17, R34, R43 with extra fixture grants | uncovered for this persona (F-007) | |

#### pemeriksa-pembayaran — ALL + rows below

| ID | Page | Action | Permission | Rehearsal | Status | Run |
|---|---|---|---|---|---|---|
| PMB-01 | `/billing/payment-reviews` | "Periksa pengajuan …", "Lihat bukti asli", "Verifikasi pembayaran" | `payment.review.read` + `payment.verify` | R39 | covered | |
| PMB-02 | `/billing/payment-reviews` | Follow-up, manual OCR exception, "Muat hasil OCR" | `payment.review.read` (+ manage/manual) | R39 | covered | |
| PMB-03 | `/billing/payment-reviews` | "Periksa koreksi" of payment reading | `payment.review.correct` | R37, R38 | covered | |
| PMB-04 | `/billing/payment-reviews` | "Periksa alokasi", "Periksa koreksi alokasi" | `payment.allocate` | R34 | covered | |
| PMB-05 | `/billing/payment-reviews` | Claim a delegated review after handover | `payment.review.read` | R36 | covered | |
| PMB-06 | `/billing/invoice-releases` | Review and approve the issuing approval | `invoice.release.approve` | R19 — always skipped | uncovered (F-004) | |
| PMB-07 | `/billing/bank-receipts` | "Tinjau keputusan bank": confirm one cash event | `bank.receipt.confirm` | R05 | covered | |
| PMB-08 | `/procurement/stock-valuations` | Decide valuation (step-up) | `procurement.stockvaluation.decide` | R51, R52 | covered | |
| PMB-09 | `/inventory/stock-releases` | "Tinjau keputusan": approve release (step-up) | `stock.release.approve` | R63 | covered | |
| PMB-10 | `/billing/invoices`, procurement read pages, counts, replenishment | Read-only | `invoice.read`, `procurement.read`, `stock.read` | R15a, R47, R62, R58 | covered | |

#### pengadaan — ALL + SS + rows below

| ID | Page | Action | Permission | Rehearsal | Status | Run |
|---|---|---|---|---|---|---|
| PGD-01 | `/procurement/suppliers` | "Tambahkan pemetaan", "Periksa usulan pemasok": propose supplier and source mapping | `supplier.change.propose` | R74 | covered | |
| PGD-02 | `/procurement/supplier-documents` | "Periksa draf dokumen": record supplier invoice/SPH with original image | `procurement.supplier.document.record` | R73, R40 | covered | |
| PGD-03 | `/procurement/supplier-documents` | "Periksa alokasi pencocokan", "Gunakan rujukan PDF untuk pencocokan" | `procurement.supplier.document.match` | R73, R41, R52 | covered | |

#### penyetuju-pengadaan — ALL + SH + rows below

| ID | Page | Action | Permission | Rehearsal | Status | Run |
|---|---|---|---|---|---|---|
| PPG-01 | `/procurement/suppliers` | "Periksa hasil pengamatan", approve supplier | `supplier.change.approve` | R74 | covered | |
| PPG-02 | `/procurement/quality-acceptances` | "Tinjau persetujuan mutu", "Simpan persetujuan mutu" | `procurement.qualityacceptance.decide` | R46 | covered | |

#### admin-akses — ALL + rows below

| ID | Page | Action | Permission | Rehearsal | Status | Run |
|---|---|---|---|---|---|---|
| ADM-01 | `/administration/accounts` | Search accounts, open an account | `access.audit.read` | none | uncovered | |
| ADM-02 | `/administration/accounts` | "Kirim usulan undangan" (invitation proposal) | `invite.manage` + `membership.propose` | none — form never renders (F-001) | uncovered | |
| ADM-03 | `/administration/accounts` | Initiate account recovery | `recovery.initiate` | none — never offered (F-001) | uncovered | |
| ADM-04 | `/administration/access` | "Tampilkan akun", compose grant/membership, server preview, "Ajukan untuk persetujuan" | `access.propose` / `membership.propose` | none | uncovered | |
| ADM-05 | `/administration/access` | Second admin: "Tinjau dan setujui", reject, requester withdraw, revoke | `access.approve` / `membership.approve` / `access.revoke` | none | uncovered | |
| ADM-06 | `/administration/access` | Suspend an identity and deny its sessions | `access.*` | R14 | covered | |
| ADM-07 | `/administration/history` | "Tampilkan riwayat" with review purpose | `access.audit.read` | R14 | covered | |
| ADM-08 | `/administration/organizations` | Propose unit, update profile, suspend, reactivate ("Kirim untuk pemeriksaan") | `technical.configure` / `organization.approve` | none | uncovered | |
| ADM-09 | `/administration/organizations/addresses` | Propose and review billing/branch/warehouse addresses | `technical.configure` / `organization.approve` | R31 | covered | |
| ADM-10 | `/operations/health` | Background queues, OCR, draft cleanup, schema version, replication | `technical.configure` | none | uncovered | |

#### staf-pembeli — ALL + SS + rows below

| ID | Page | Action | Permission | Rehearsal | Status | Run |
|---|---|---|---|---|---|---|
| SPB-01 | `/ordering/drafts`, `/ordering/checkout` | Staff saves a draft and submits an order | `order.submit` | R27 (generic buyer) | covered | |
| SPB-02 | storefront | Staff-and-buyer header: "Ruang kerja" link plus cart | display | none | uncovered | |

### 2.5 Functions no persona in this program reaches

These pages exist and have rehearsals, but none of the 15 personas holds their permission. They stay out of the per-persona counts; phase B does not hand-test them unless a persona is added.

| Page | Permission | Rehearsal | Status |
|---|---|---|---|
| `/accounting/books` | `accounting.book.*` | R02 | covered |
| `/reporting/accounts` | `report.read` (fulfillment-shop scope) | R03 | covered |
| `/operations/anomalies` | `operations.anomaly.*` | R21 | covered |
| `/ordering/spending-policies` | `order.spending.policy.*` | R26 | covered |
| `/ordering/commercial-policies` | `order.commercial.policy.*` | R12 | covered |
| `/billing/bank-statements` | `bank.statement.*` | R06 | covered |
| `/billing/receiving-accounts` propose/verify/approve | `receiving.account.propose/verify/approve` | R57 | covered |
| `/procurement/supplier-banks` | `supplier.bank.*` | R71 | covered |
| `/procurement/disbursements` | `procurement.disbursement.read` | R43 | covered |
| `/procurement/settlements` | `procurement.settlement.read` | R49 | covered |
| `/procurement/advance-returns` | `procurement.advancereturn.*` | R42 | covered |
| `/procurement/supplier-payables` | `procurement.payable.*` | R53 | covered |
| `/procurement/funding` approve | `funding.approve` | R44 | covered |
| `/inventory/sources`, `/aliases`, `/revisions`, `/policies`, `/suspensions`, `/activations` | `stock.source.*` | R64–R67, R69, R70 | covered |
| `/inventory/source-reconciliations` | `stock.source.reconciliation.*` | R68 | covered |
| `/administration/person-reviews` | `person.review.*` | none | uncovered |
| `/fulfillment/cases` return entitlement, financial approval, refund payout, refund settlement, return lot | `credit.*`, `refund.*`, `case.manage`, `stock.count` | R15e, R16, R17, R18, R59 | covered |

### 2.6 Counts (full release)

Unique functions per persona = own rows + shared blocks (ALL = 6, BUY = 24, SH = 18, SS = 11).

| Persona | Functions | covered | uncovered |
|---|---|---|---|
| tamu | 5 | 1 | 4 |
| pemohon | 34 | 24 | 10 |
| penyetuju-cabang | 33 | 24 | 9 |
| peninjau-toko | 28 | 20 | 8 |
| petugas-katalog | 25 | 19 | 6 |
| peninjau-katalog | 26 | 20 | 6 |
| petugas-gudang | 25 | 22 | 3 |
| petugas-kirim | 20 | 17 | 3 |
| kurir | 11 | 8 | 3 |
| keuangan-tagihan | 13 | 9 | 4 |
| pemeriksa-pembayaran | 16 | 13 | 3 |
| pengadaan | 20 | 17 | 3 |
| penyetuju-pengadaan | 26 | 21 | 5 |
| admin-akses | 16 | 7 | 9 |
| staf-pembeli | 19 | 15 | 4 |
| **Total (rows as counted per persona)** | **317** | **237** | **80** |

Distinct functions (each shared row counted once): 128 rows, 92 covered, 36 uncovered. ALL contributes 4 covered / 2 uncovered to every signed-in persona. "covered" means a rehearsal exists; until the orchestrator's run results are pasted, a covered row can still turn out `failing` (chunk 1 already shows R04 and R05 failing, and R14).

---

## 3. Rehearsal map (script → xUnit driver)

All drivers use `[LocalStockBrowserFact]` (opt-in, `UNITTOKO_STOCK_BROWSER=1`) or `[WindowsPaymentBrowserFact]` (Windows + the same opt-in) and set `NEXT_PUBLIC_UNIT_TOKO_RELEASE=full`. "Hub" means the driver runs `verify-invoice-browser.mjs` with a config flag that selects the sub-script.

| R-ID | Script(s) | xUnit class | Test method(s) | Notes |
|---|---|---|---|---|
| R01 | verify-account-contact-browser | Identity/AccountContactBrowserTests | Browser_recovers_one_committed_contact_change_with_own_persistent_history_and_mobile_foreign_isolation | |
| R02 | verify-accounting-books-browser | Accounting/AccountingBookSetupBrowserTests | Browser_independently_sets_up_book_chart_period_and_recovers_lost_replies_without_financial_effects | hub `accountingBooks` |
| R03 | verify-accounting-report-browser | Reporting/AccountingReportBrowserTests | Browser_reads_scoped_journals_downloads_whole_snapshot_and_preserves_mobile_foreign_isolation | |
| R04 | verify-approved-order-dispatch-browser | Ordering/ApprovedOrderDispatchBrowserTests | Browser_first_order_independent_spending_store_preparation_dispatch_and_buyer_history_use_actual_approved_owners_once | script line 207 waits for a raw order id (F-003) |
| R05 | verify-bank-receipt-browser | Ordering/BankReceiptBrowserTests | Browser_independently_confirms_and_recovers_one_cash_event_across_overlapping_sources | |
| R06 | verify-bank-statement-browser | Ordering/BankStatementBrowserTests | Browser_previews_stages_and_recovers_private_bank_source_without_cash | |
| R07 | verify-buyer-identifiers-harness → verify-buyer-identifiers-browser | Catalog/CatalogBuyerIdentifierBrowserTests | Browser_searches_reviewed_current_codes_and_keeps_one_stable_sku_draft_after_reply_loss_and_restart | |
| R08 | verify-catalog-identifier-browser | Catalog/CatalogIdentifierBrowserTests | Browser_authors_identifier_then_scans_exact_package_and_recovers_one_count_after_restart | |
| R09 | verify-catalog-photo-browser | Catalog/CatalogPhotoBrowserTests | Staff_uploads_reviews_recovers_and_replaces_real_private_photo_while_buyer_reads_current_bytes_and_foreign_scope_is_denied | |
| R10 | verify-catalog-price-browser | Catalog/CatalogPriceBrowserTests | Browser_price_selection_independent_decisions_four_recoveries_and_stale_package_use_real_Mongo | |
| R11 | verify-primary-shop-browser (imported by verify-catalog-price-browser) | Catalog/CatalogPriceBrowserTests | Primary_shop_reviews_exact_package_quantity_and_real_subtotal_and_recovers_one_saved_draft_without_resend_or_foreign_disclosure | landing → shop → cart → drafts |
| R12 | verify-commercial-policy-browser | Ordering/OrderCommercialPolicyBrowserTests | Browser_explicit_commercial_bundle_independent_approval_rejection_original_key_loss_recovery_and_two_versions_have_no_business_effects | |
| R13 | verify-courier-assignment-harness → verify-courier-assignment-browser | Fulfillment/ShipmentCourierBrowserTests | Browser_handover_recovers_original_key_after_restart_and_current_courier_receives_without_rewriting_departure | |
| R14 | verify-identity-suspension-browser | Authorization/AccessIdentitySuspensionBrowserTests | Browser_recovers_exact_account_suspension_once_and_denies_existing_and_new_business_sessions | |
| R15a | verify-invoice-browser (default flow) | Ordering/InvoiceBrowserTests | Browser_issues_received_goods_recovers_lost_response_and_downloads_private_invoice | issues through `InvoiceData.FixtureAdmission` (legacy release anchor), not the real release |
| R15b | verify-invoice-browser (after-sales) | Ordering/InvoiceBrowserTests | Browser_links_finalized_sale_case_recovers_original_request_and_preserves_sale | |
| R15c | verify-invoice-browser (case decision) | Ordering/InvoiceBrowserTests | Browser_independently_decides_return_recovers_lost_decision_and_shows_private_buyer_explanation | |
| R15d | verify-invoice-browser (case return) | Ordering/InvoiceBrowserTests | Browser_receives_inspects_and_requests_return_resolution_recovers_lost_replies_and_preserves_pack_stock_and_money | |
| R15e | verify-invoice-browser (entitlement) | Ordering/InvoiceBrowserTests | Browser_requests_exact_return_entitlement_recovers_original_and_preserves_invoice_cash_and_quarantine | |
| R16 | verify-return-financial-approval-browser | Ordering/InvoiceBrowserTests | Browser_independent_Finance_credits_and_reserves_original_funding_recovers_exact_lost_approval_without_resend_or_cash_transfer | hub `financialApproval` |
| R17 | verify-refund-payout-browser | Ordering/InvoiceBrowserTests | Browser_records_exact_refund_private_evidence_recovers_lost_reply_without_resend_or_money_transfer | hub `refundPayout` |
| R18 | verify-refund-settlement-browser | Ordering/InvoiceBrowserTests (InvoiceBrowserTests.RefundSettlement.cs) | Browser_independently_reviews_private_refund_matches_bank_debit_and_recovers_exact_lost_settlement_without_resend | hub `refundSettlement` |
| R19 | verify-invoice-release-harness → verify-invoice-release-browser | Billing/InvoiceReleaseBrowserTests | Browser_independently_approves_actual_received_goods_issues_once_and_recovers_three_committed_commands_after_process_restarts | **always skipped**: `Skip = Pending.BrowserLatency` (a command exceeds the 30 s browser bound) |
| R20 | verify-local-pilot-browser | none | none | runs against the `home.arpa` pilot with private credentials; no driver; not allowed in this program |
| R21 | verify-operational-anomalies-browser | Operations/OperationalAnomalyBrowserTests | Browser_real_source_policy_evaluation_review_and_lost_response_recover_without_business_effect | hub `operationalAnomalies` |
| R22 | verify-operational-scan-fulfillment-browser | Catalog/OperationalScanFulfillmentBrowserTests | Browser_explicit_scans_bind_one_picking_and_one_finite_dispatch_and_recover_both_original_keys | |
| R23 | verify-operational-scan-receiving-harness → verify-operational-scan-receiving-browser | Procurement/OperationalScanReceivingBrowserTests | Browser_confirms_exact_approved_supplier_scan_and_recovers_one_held_receipt_without_resend | |
| R24 | verify-order-bulk-browser | Ordering/OrderBulkEntryBrowserTests | Browser_applies_explicit_bulk_packages_to_one_private_draft_and_recovers_original_key_after_browser_restart | hub `orderBulk` |
| R25 | verify-order-reorder-browser | Ordering/OrderReorderBrowserTests | Browser_reorders_original_selections_as_one_private_draft_and_recovers_original_key_after_browser_restart | hub `orderReorder` |
| R26 | verify-order-spending-policy-browser | Ordering/OrderSpendingPolicyBrowserTests | Browser_proposes_independently_approves_and_recovers_lost_spending_policy_replies_without_business_effects | hub `orderSpendingPolicy` |
| R27 | verify-order-submission-browser | Ordering/OrderSubmissionBrowserTests | Browser_saved_draft_checkout_reserves_Mongo_stock_and_recovers_a_lost_submit_without_repeating_it | |
| R28 | verify-order-substitution-browser | Ordering/OrderSubstitutionBrowserTests | Browser_staff_proposal_buyer_decline_and_exact_consent_recover_original_keys_then_dispatch_real_replacement | hub `orderSubstitution` |
| R29 | verify-order-timeline-browser | Ordering/OrderTimelineBrowserTests | Browser_reads_actual_grouped_delivery_invoice_bank_allocation_and_return_outcomes_after_restart_without_business_writes | **always skipped**: `Skip = Pending.TransactionLatency` |
| R30 | verify-order-views-browser | Ordering/OrderViewBrowserTests | Browser_saves_shares_renames_removes_private_filters_and_recovers_each_original_key_after_restart | hub `orderViews` |
| R31 | verify-organization-address-browser | Organizations/OrganizationAddressBrowserTests | Browser_proposes_independently_approves_exact_postal_address_recovers_committed_reply_and_limits_reader_scope | hub `organizationAddress` |
| R32 | verify-pack-conversion-browser | Inventory/PackConversionRuleBrowserTests | Browser_rule_preparation_independent_approval_original_key_recovery_and_unchanged_stock_money_use_real_https_and_Mongo | |
| R33 | verify-pack-conversion-execution-browser | Inventory/PackConversionBrowserTests | Browser_real_lot_conversion_committed_reply_loss_original_key_recovery_and_stock_value_are_durable | |
| R34 | verify-payment-allocation-browser | Ordering/PaymentAllocationBrowserTests | Browser_allocates_and_independently_corrects_linked_funds_recovers_lost_replies_and_preserves_original_cash; Browser_chooses_a_genuine_non_declaration_invoice_and_independently_corrects_its_original_allocation | |
| R35 | verify-payment-browser | Ordering/PaymentBrowserTests | Browser_recovers_draft_reservation_and_original_upload_without_duplicate_financial_effects | `[WindowsPaymentBrowserFact]` |
| R36 | verify-payment-handover-browser | Ordering/PaymentHandoverBrowserTests | Browser_claims_real_delegated_review_after_revocation_and_recovers_original_key_after_actual_restart | hub `paymentHandover` |
| R37 | verify-payment-supplement-browser | Billing/PaymentSupplementBrowserTests | Actual_browser_recovers_separate_supplement_and_reads_all_native_pages_without_replacing_primary_image_or_money | |
| R38 | verify-payment-supplement-correction-browser | Billing/PaymentSupplementBrowserTests | Actual_browser_manually_corrects_image_with_persisted_supplementary_page_reference_and_recovers_one_commit | |
| R39 | verify-payment-verification-browser | Ordering/PaymentVerificationBrowserTests | Browser_verifies_exact_evidence_recovers_a_lost_response_and_retains_one_cash_event; Browser_records_manual_OCR_exception_recovers_each_lost_response_and_verifies_real_bank_receipt; Browser_explicitly_selects_recovered_OCR_after_manual_policy_expires_and_preserves_both_histories; Browser_claims_releases_and_requests_replacement_recovers_a_lost_response_and_blocks_stale_verification; Browser_creates_linked_replacement_recovers_without_resending_and_submits_new_checked_original | |
| R40 | verify-pdf-document-ocr-browser | Documents/PdfDocumentOcrBrowserTests | Actual_supplier_browser_uploads_PDF_and_scoped_staff_read_every_page_without_a_financial_or_stock_effect | |
| R41 | verify-supplier-pdf-match-browser | Documents/PdfDocumentOcrBrowserTests | Actual_supplier_browser_manually_matches_with_persisted_native_page_reference_and_recovers_one_commit | |
| R42 | verify-procurement-advance-return-browser | Procurement/ProcurementAdvanceReturnBrowserTests | Browser_returns_whole_genuine_credit_across_two_verified_advances_with_original_key_recovery | hub |
| R43 | verify-procurement-disbursement-browser | Procurement/ProcurementDisbursementBrowserTests | Browser_records_real_private_original_with_exact_key_recovery_and_no_financial_effects | hub |
| R44 | verify-procurement-funding-browser | Procurement/ProcurementFundingBrowserTests | Browser_requests_partial_advance_and_independently_authorizes_once_with_both_original_key_recoveries | hub |
| R45 | verify-procurement-inspection-browser | Procurement/ProcurementInspectionBrowserTests | Browser_records_actual_unboxing_video_and_quarantined_observations_with_original_key_recovery | hub |
| R46 | verify-procurement-quality-acceptance-browser | Procurement/ProcurementQualityAcceptanceBrowserTests | Browser_independently_accepts_mixed_receipt_subset_and_keeps_stock_held_with_original_key_recovery | hub |
| R47 | verify-procurement-receiving-browser | Procurement/ProcurementReceivingBrowserTests | Browser_records_actual_supplier_notice_and_partial_container_custody_with_separate_key_recovery | hub |
| R48 | verify-procurement-requests-browser | Procurement/ProcurementRequestBrowserTests | Browser_submits_real_catalog_packages_and_independently_decides_with_original_key_recovery | hub |
| R49 | verify-procurement-settlement-browser | Procurement/ProcurementSettlementBrowserTests | Browser_independently_verifies_actual_debit_and_recovers_one_claim_and_journal | hub |
| R50 | verify-procurement-stock-receipt-browser | Procurement/ProcurementStockReceiptBrowserTests | Browser_records_inspected_packages_fully_held_and_recovers_original_key_without_resend | hub |
| R51 | verify-procurement-stock-valuation-browser | Procurement/ProcurementStockValuationBrowserTests | Browser_independently_values_real_held_receipt_and_recovers_both_original_keys_without_duplicate_posting | hub |
| R52 | verify-quality-backed-invoice-browser | Procurement/ProcurementStockValuationBrowserTests | Browser_matches_explicit_quality_subset_then_values_it_once_with_real_step_up_and_original_key_recovery | hub `procurementStockValuation` + `mixedQuality` |
| R53 | verify-procurement-supplier-payable-browser | Procurement/ProcurementSupplierPayableBrowserTests | Browser_recognizes_genuine_supplier_payable_and_applies_verified_advance_with_original_key_recovery | hub |
| R54 | verify-purchase-orders-browser | Procurement/PurchaseOrderBrowserTests | Browser_proposes_explicit_commercial_order_and_independently_approves_real_request_quantity_with_original_key_recovery | hub `purchaseOrders` |
| R55 | verify-purchase-arrivals-browser | Procurement/PurchaseOrderBrowserTests | Browser_reviews_supplier_arrival_independently_and_recovers_original_write_after_reply_loss | hub `purchaseArrivals` |
| R56 | verify-purchase-order-inbound-browser | Procurement/PurchaseOrderInboundBrowserTests | Browser_plans_real_inactive_receiving_pool_and_preserves_exact_independent_approval_after_original_key_recovery_and_reload | hub |
| R57 | verify-receiving-account-browser | Ordering/ReceivingAccountBrowserTests | Browser_proposes_independently_observes_approves_and_recovers_without_duplicate_versions | |
| R58 | verify-replenishment-browser | Inventory/ReplenishmentBrowserTests | Browser_replenishment_uses_genuine_stock_persists_recovers_refreshes_and_explicitly_creates_procurement_work | hub |
| R59 | verify-return-lot-browser | Ordering/ReturnLotBrowserTests | Browser_records_only_manual_whole_package_lot_finding_recovers_original_reply_loss_and_preserves_quarantine_stock_and_money | hub |
| R60 | verify-shipment-dispatch-browser | Ordering/ShipmentDispatchBrowserTests | Browser_confirms_physical_lot_and_courier_and_recovers_one_committed_dispatch_without_resending | |
| R61 | verify-shipment-receipt-browser | Ordering/ShipmentReceiptBrowserTests | Browser_requires_separate_recipient_exact_counts_and_recovers_lost_confirmation_without_resending | |
| R62 | verify-stock-count-browser | Inventory/StockCountBrowserTests | Browser_signin_count_recovery_and_logout_use_real_cookies_API_and_Mongo | |
| R63 | verify-stock-release-browser | Inventory/StockReleaseBrowserTests | Browser_explicit_selection_independent_release_lost_reply_original_key_recovery_and_exact_stock_value_are_durable | |
| R64 | verify-stock-source-activation-browser | Inventory/StockSourceActivationBrowserTests | Browser_empty_handoff_independent_activation_history_original_key_recovery_and_unchanged_stock_money_are_durable | |
| R65 | verify-stock-source-alias-browser | Inventory/StockSourceAliasBrowserTests | Browser_alias_channels_stale_review_recovery_MFA_and_revocation_use_real_API_and_Mongo | |
| R66 | verify-stock-source-browser | Inventory/StockSourceBrowserTests | Browser_source_proposal_independent_review_recovery_and_logout_use_real_API_and_Mongo | |
| R67 | verify-stock-source-policy-browser | Inventory/StockSourcePolicyBrowserTests | Browser_policy_scoped_tasks_four_recoveries_frozen_decisions_and_private_history_use_real_API_and_Mongo | |
| R68 | verify-stock-source-reconciliation-browser | Inventory/StockSourceReconciliationBrowserTests | Browser_records_complete_independent_counts_and_recovers_original_keys_without_changing_stock_or_money | hub |
| R69 | verify-stock-source-revision-browser | Inventory/StockSourceRevisionBrowserTests | Browser_revision_versions_stale_review_recovery_MFA_and_revocation_use_real_API_and_Mongo | |
| R70 | verify-stock-source-suspension-browser | Inventory/StockSourceSuspensionBrowserTests | Browser_suspension_command_only_confirmation_recovery_history_and_MFA_use_real_API_and_Mongo | |
| R71 | verify-supplier-bank-browser | Procurement/SupplierBankBrowserTests | Browser_proposes_bank_details_independently_observes_and_approves_with_original_key_recovery_and_private_reads | hub `supplierBanks` |
| R72 | verify-supplier-claim-browser | Procurement/SupplierClaimBrowserTests | Browser_claims_actual_damaged_goods_and_independently_reviews_with_original_key_recovery | hub |
| R73 | verify-supplier-document-browser | Procurement/SupplierDocumentBrowserTests | Browser_records_private_invoice_runs_native_ocr_and_reviews_partial_match_with_original_key_recovery | hub |
| R74 | verify-suppliers-browser | Procurement/SupplierBrowserTests | Browser_proposes_real_supplier_mapping_and_independently_observes_and_approves_with_original_key_recovery | hub `suppliers` |
| R75 | verify-temporary-delegation-browser | Authorization/TemporaryDelegationBrowserTests | Browser_independently_approves_finite_delegation_and_recovers_all_original_operations_after_real_restart | hub `temporaryDelegation` |
| R76 | verify-workspace-directory-browser (imported by verify-procurement-receiving-browser) | Procurement/ProcurementReceivingBrowserTests | Browser_records_actual_supplier_notice_and_partial_container_custody_with_separate_key_recovery | asserts the directory for one procurement actor |
| R77 | verify-golden-journey-browser (stage `order-to-receipt`) | Journeys/GoldenJourneyBrowserTests | Golden_order_to_receipt_across_personas_shows_each_result_and_order_number | phase B; one persona per step (A0–A9); each staff persona first finds its page in its own workspace directory, buyers in the storefront account menu |
| R78 | verify-golden-journey-browser (stage `invoice-to-payment`) | Journeys/GoldenJourneyBrowserTests | Golden_invoice_release_to_verified_payment_across_personas_shows_each_result_and_invoice_number | phase B; **always skipped** `Pending.BrowserLatency` (F-004) |
| R79 | verify-golden-journey-browser (stage `procurement-to-receipt`) | Journeys/GoldenJourneyBrowserTests | Golden_procurement_request_to_received_goods_across_personas_shows_each_result_and_number | phase B; B1–B6 |
| R80 | verify-golden-journey-browser (stage `cart-and-cancellation`) | Journeys/FunctionGapBrowserTests | Cart_changes_draft_discard_buyer_cancellation_and_store_rejection_work_in_the_browser | phase B; F-018, F-017 (order parts) |
| R81 | verify-golden-journey-browser (stage `branch-requests`) | Journeys/FunctionGapBrowserTests | Branch_request_is_sent_by_the_buyer_and_accepted_or_rejected_by_the_store_in_the_browser | phase B; F-013, full release |

Script accounting: 80 files. Each R-ID is one file, except R07, R13, R19 and R23 (harness plus its browser module, two files each) and R15a–e (five test methods sharing the hub `verify-invoice-browser.mjs`). Every file has a driver except `verify-local-pilot-browser.mjs` (R20). Two drivers are permanently skipped (R19, R29). 67 driver files, 66 classes, 83 test methods.

---

## 4. Pilot release (`isPilotRelease`, the default build)

`proxy.ts` answers 404 "Halaman belum tersedia pada pilot ini" for every page outside `pilotPages` and `release_unavailable` for every API outside `isPilotApiPathAllowed`. Navigation only lists pilot pages. No xUnit driver runs the pilot build (all set `NEXT_PUBLIC_UNIT_TOKO_RELEASE=full`); the only pilot script is R20 (`home.arpa`, off-limits). So every pilot row below is `uncovered` (F-009).

| Page (pilot) | Who | Function | API in allowlist? | Status |
|---|---|---|---|---|
| `/`, `/katalog`, `/katalog/[publicId]` | tamu | Landing, sample catalog | yes (`/api/v1/public/catalog`) | uncovered |
| `/shop`, `/shop/[product]` | pemohon, staff | Browse packages without price; photo always "Foto belum tersedia" (F-012); "Pilih untuk permintaan" → `/ordering/requests?package=` | yes (`/api/v1/catalog/variants`) | uncovered |
| `/ordering/requests` | pemohon | Compose and send branch request | yes | uncovered |
| `/ordering/requests/incoming` | peninjau-toko | Store decision | yes | uncovered |
| `/administration/catalog` | petugas-katalog | Families, packages, codes | yes (`/api/v1/catalog/administration`) | uncovered |
| `/administration/catalog` → "Foto …" | petugas-katalog | Photo panel | **no**: `/api/v1/catalog/skus/*`, `/catalog/primary-photos/*`, `/catalog/photo-revalidations/*` → 404 (F-008) | uncovered |
| `/identity/account`, `/workspace` | all | Session, step-up limited to catalog/request operations; WorkSummary and contact panel hidden | yes | uncovered |
| `/operations/health` | admin-akses | Health report | yes | uncovered |
| `/cart`, `/checkout`, `/account/orders` | pemohon | Redirect to `/ordering/requests` | n/a | uncovered |

---

## 5. Golden journey design

Deliverables (phase B, after ENV Task 0.3 merges): `frontend/scripts/verify-golden-journey-browser.mjs` and `backend/tests/UnitToko.Tests/Journeys/GoldenJourneyBrowserTests.cs`.

### 5.1 Shape

- One stack, one Mongo database, one Next production build (`.next-full`), one HTTPS origin. Two stages in one test method: **A. order to cash** and **B. procurement to stock**. Each step runs in its own Playwright context with that persona's session; no persona ever acts outside its grants (the step fails if the needed navigation entry is missing, which catches F-001/F-006-type defects).
- Driver: `GoldenJourneyBrowserTests.Golden_order_to_cash_then_procurement_to_stock_across_personas_shows_each_result_and_number` with `[LocalStockBrowserFact]` (same opt-in and runtimes as every rehearsal; heavy job under the program lock). It self-hosts exactly like `ApprovedOrderDispatchBrowserTests`: `IdentityRuntimeTests.Factory(settings)` with `Release:Profile=full`, Kestrel on loopback, self-signed TLS for `https://portal.unit-toko.co.id`, the `FixtureControl` startup filter for session issue, and passes `{ apiAddress, origin, pfx, password, playwrightPackage, buildId, tokens: { <persona>: token }, actorIds, ids… }` to the script on stdin. It asserts `REAL_GOLDEN_JOURNEY_BROWSER_PASS` and then re-reads Mongo for the end state (one order, one shipment, one invoice, one verified payment, one held→released receipt, no duplicate command receipts).
- Second mode for phase B hand runs: the script also accepts `{ mode: 'review-stack', origin: 'https://localhost:8443', sessions: 'tmp/website-ready/sessions' }` and loads `<persona>.json` storage states that ENV writes. Same steps, same assertions; no xUnit, no database checks.
- Personas are minted from the same grant table ENV uses (`PilotPersonas`), so the journey proves the real role templates plus additions, not ad-hoc fixture grants. Until ENV merges, use section 1's table.
- Pace with `paceApi` (copy from `verify-catalog-photo-browser.mjs`) so the whole run stays under 240 requests/min from one IP; never raise the limiter. Each command must finish inside the 30 s browser bound; do not relax it.
- Separate warehouses for stage A and stage B: an order fixture cannot reuse a warehouse the procurement fixture has already stocked (`Pending.SharedWarehouseFixture`). Stage B's released stock lands in the procurement warehouse; stage B asserts its own on-hand change, not stage A's.
- Expected RED on first run: step A10 (invoice release) is known to exceed the 30 s bound (F-004); the test is written to fail there with that reason, and the remaining steps are still useful when A10 is temporarily seeded through `InvoiceReleaseService` directly (flagged `seededRelease` in the output, never in the PASS line).

### 5.2 Stage A — order to cash

| # | Persona | Page | Action (exact control names) | Visible result to assert | Typed number to assert | Data / fixture needed |
|---|---|---|---|---|---|---|
| A1 | tamu | `/` → `/katalog` → `/katalog/[publicId]` | "Lihat katalog"; open a sample | h1 of the catalog; ≥1 sample card; product page shows name and package contents; **no** `Rp` text and no price element anywhere; sign-in link carries `returnPath` | none | 12 approved public samples (Task 3.1 + ENV 0.3b seed); one of them is the SKU used in A2 |
| A2 | pemohon | `/shop` → `/shop/[product]` | choose branch (explicit), quantity stepper, "Tambah ke keranjang"; "Lihat keranjang" | `/cart` shows the line, "Ringkasan belanja", subtotal `Rp…` formatted `Rp1.250.000` (no space); header badge = 1 | none (draft has no number) | approved price for the SKU; available stock in warehouse A; branch membership; approved branch address |
| A3 | pemohon | `/cart` → `/ordering/checkout?draft=` | "Lanjut ke pengajuan"; delivery from approved address; "Tinjau pesanan dan biaya"; "Ajukan untuk persetujuan" | heading "Pesanan PSN-xxxxxx tercatat"; `/ordering/orders` row shows the order waiting for spending approval | `PSN-` + 6-digit order id (`recordNumber('order', id)`) | commercial policy (approved), spending policy that requires branch approval, approved order admission readers |
| A4 | penyetuju-cabang | `/ordering/reviews` | "Tinjau pesanan PSN-xxxxxx"; "Setujui belanja"; "Konfirmasi persetujuan belanja" | state changes to waiting for store acceptance; the requester cannot approve (different person) | same PSN | fresh MFA on the session (step-up); distinct person records for pemohon and penyetuju |
| A5 | peninjau-toko | `/ordering/reviews` | "Tinjau pesanan PSN-xxxxxx"; "Terima pesanan toko"; "Konfirmasi penerimaan toko" | store part accepted; buyer list shows the new state | same PSN | store scope over warehouse A |
| A6 | petugas-gudang | `/fulfillment/preparations` | open the accepted order; choose complete packages; "Tinjau persiapan"; "Konfirmasi persiapan" | preparation saved with packages and quantities; no price shown | PSN on the preparation row | reservation from A3 |
| A7 | petugas-kirim | `/fulfillment/dispatches` | "Periksa keberangkatan pesanan PSN-xxxxxx"; choose physical lot and kurir; "Tinjau keberangkatan"; "Konfirmasi keberangkatan" | shipment created; "Lihat kiriman E/C/Y/N" appears in `/fulfillment/shipments` | shipment number `entity/channel/year/seq` (e.g. `100/103/2026/1`) | stock lot with value in warehouse A; kurir with approved roster assignment, `shipment.read` + `delivery.attempt` in scope |
| A8 | kurir | `/fulfillment/shipments` | "Lihat kiriman …"; "Upaya pengiriman" → "Simpan upaya pengiriman"; "Tinjau penerima" → "Buat QR penerimaan" | QR "QR penerimaan untuk akun yang dipilih" shown; link available | shipment number | courier session |
| A9 | pemohon | `/fulfillment/receive#<challenge>` | "Hitung barang yang tiba"; "Tinjau jumlah"; "Konfirmasi penerimaan" | receipt confirmed; shipment shows received counts | shipment number | recipient is a different person from the courier |
| A10 | keuangan-tagihan → pemeriksa-pembayaran | `/billing/invoice-releases` | keuangan: "Periksa sumber penerbitan", "Tinjau usulan penerbitan", submit; pemeriksa: "Tinjau persetujuan penerbitan", "Konfirmasi persetujuan invoice" | release approved; proposer sees "Anda yang mengajukan…" and cannot approve | PSN / receipt reference on the release | accounting book + invoice format + fees policy as `InvoiceReleaseBrowserTests` sets them; **known to exceed 30 s (F-004)** |
| A11 | keuangan-tagihan | `/billing/invoices` | "Penerimaan untuk invoice" → "Tinjau invoice" → "Tinjau penerbitan invoice" → "Terbitkan invoice" | "Buka invoice E/C/Y/N" listed; download works | invoice number `entity/channel/year/seq` | approved release from A10 |
| A12 | pemohon | `/billing/invoices`, `/billing/payments` | "Unduh invoice"; "Simpan draf pembayaran" (invoice chosen, amount `Rp…`); "Unggah bukti asli"; "Periksa pengajuan pembayaran"; "Ajukan untuk verifikasi" | "Pengajuan <id>" submitted; original evidence visible to the buyer | invoice number on the draft; payment shown as "Pengajuan <id>" (payments have no typed prefix) | approved receiving account; evidence admission (private-file adapter + scanner, Windows) as `PaymentDraftData` + `EvidenceAdmissionTestData` |
| A13 | pemeriksa-pembayaran | `/billing/payment-reviews` | "Periksa pengajuan <id>"; "Lihat bukti asli"; "Pilih penerimaan bank"; "Verifikasi pembayaran" | verified; buyer's `/billing/payments` shows verified state; one cash event | invoice number + payment id | confirmed bank receipt matching the amount (as `PaymentVerificationTestData` + `BankReceiptTestData`) |

### 5.3 Stage B — procurement to stock

| # | Persona | Page | Action (exact control names) | Visible result to assert | Typed number to assert | Data / fixture needed |
|---|---|---|---|---|---|---|
| B1 | pengadaan | `/procurement/requests` | "Buat permintaan"; "Cari kemasan"; "Periksa kemasan pilihan"; "Tambahkan kemasan"; "Periksa rincian permintaan"; "Kirim permintaan" | "Menunggu pemeriksaan" | `PP-` + 6 digits | catalog SKU, warehouse B location, procurement route |
| B2 | penyetuju-pengadaan | `/procurement/requests` | "Tinjau persetujuan"; "Setujui permintaan" | "Disetujui"; maker cannot approve own | same PP | distinct person |
| B3 | pengadaan | `/procurement/purchase-orders` | "Buat pesanan pembelian"; "Tambah barang pesanan" → "Tambahkan barang"; "Periksa pesanan pembelian"; "Kirim usulan pesanan" | waiting for internal approval | `SP-` + 6 digits; links PP | approved supplier with source mapping (`SupplierTestData`); route DDO (pay on delivery, so no funding/disbursement) |
| B4 | penyetuju-pengadaan | `/procurement/purchase-orders` | "Tinjau persetujuan pesanan"; "Setujui pesanan secara internal" | "Disetujui secara internal" | same SP | stock source bound before the order (post-7f8541b fixture order) |
| B5 | petugas-gudang | `/procurement/receiving` | "Catat pemberitahuan pemasok" → "Tinjau pemberitahuan pemasok" → "Simpan pemberitahuan pemasok"; "Catat penerimaan fisik koli" → "Tinjau penerimaan fisik koli" → save | "Riwayat penerimaan fisik" shows the koli | SP on the notice line | inbound receiving pool (PurchaseOrderInbound plan) |
| B6 | petugas-gudang | `/procurement/inspections` | "Buat draf pemeriksaan"; "Simpan draf pemeriksaan"; "Unggah video asli"; "Ajukan hasil dalam karantina" | inspection in quarantine | SP | video evidence admission (as `ProcurementInspectionTestData`) |
| B7 | petugas-gudang | `/procurement/stock-receipts` | "Catat stok ditahan"; "Periksa penerimaan stok"; "Simpan penerimaan stok ditahan" | "stok ditahan dan belum dinilai" | SP | inspection from B6 |
| B8 | petugas-gudang → penyetuju-pengadaan | `/procurement/quality-acceptances` | "Ajukan penerimaan mutu" → "Simpan pengajuan"; checker "Tinjau persetujuan mutu" → "Simpan persetujuan mutu" | "Mutu disetujui · barang tetap ditahan" | SP | step-up on both sessions |
| B9 | pengadaan → keuangan-tagihan → pemeriksa-pembayaran | `/procurement/supplier-documents`, `/procurement/stock-valuations` | record supplier invoice + match; "Periksa penilaian stok" (propose); checker decides | valuation decided; goods still held | SP | supplier invoice PDF + OCR runtime (`SupplierDocumentTestData`), accounting book for journal |
| B10 | petugas-gudang → pemeriksa-pembayaran | `/inventory/stock-releases` | "Tinjau pengajuan" → "Konfirmasi pengajuan pelepasan"; checker "Tinjau keputusan" → "Konfirmasi keputusan pelepasan" | "Hasil pelepasan tercatat"; `/inventory/replenishment` on-hand for the SKU in warehouse B rises by the received packages | release "Pengajuan #<id>"; SP in the source | valued, quality-accepted receipt from B8–B9 |

Minimum acceptable stage B if B9/B10 block: B1–B8 (received and held stock). The PASS line must say which.

### 5.4 Reusable fixture code

| Need | Reuse |
|---|---|
| Kestrel + TLS + session issue + restart control | `Ordering/ApprovedOrderDispatchBrowserTests.cs` (`FixtureControl`, `IdentityRuntimeTests.Factory/Settings`, `data.IssueExistingSessionAsync`, `data.Catalog.Issued`) |
| Stage A world (buyer, spender, store, warehouse stock with value, prices, commercial + spending policy, admission readers, courier roster) | `Ordering/GenuineFiniteDispatchTestData.cs` (`CreateAsync(fixture, converted:false, cogs:true, createOrder:false, useSystemClock:true)`) — split its combined `Dispatcher` into peninjau-toko / petugas-gudang / petugas-kirim |
| Receipt QR flow | `Ordering/ShipmentReceiptBrowserTests.cs` + `ShipmentDispatchTestData` |
| Real invoice release (not `FixtureAdmission`) | `Billing/InvoiceReleaseBrowserTests.cs` data setup; `InvoiceService(..., InvoiceReleaseService)` constructor |
| Payment draft + evidence admission | `Ordering/PaymentDraftTests.cs` (`PaymentDraftData`), `Documents` `EvidenceAdmissionTestData`, `ReceivingAccountTestData` |
| Verification with bank receipt | `Ordering/PaymentVerificationTestData.cs`, `BankReceiptTestData` |
| Stage B world | `Procurement/ProcurementRequestTestData`, `SupplierTestData`, `PurchaseOrderTestData`, `PurchaseOrderInboundBindingTestData`, `ProcurementReceivingTestData`, `ProcurementInspectionTestData`, `ProcurementStockReceiptTestData`, `ProcurementQualityAcceptanceTestData`, `ProcurementStockValuationTestData`, `SupplierDocumentTestData`; release data from `Inventory/StockReleaseBrowserTests` (`ReleaseData`) |
| Distinct people for maker/checker | `PersonTestData.SeedDistinctPeopleAsync` |
| Script plumbing | `verify-approved-order-dispatch-browser.mjs` (proxy, Next production server, contexts), `paceApi` from `verify-catalog-photo-browser.mjs`, `orderNumber`/`recordNumber` from `src/lib/document-numbers.ts` |

Each builder today creates its own world; composing them onto one database (shared organization tree, one catalog, two warehouses) is the main work. The review stack (ENV) needs the same seed: personas per section 1, samples + prices, warehouse A stock, approved supplier, receiving account, bank receipt source, accounting book and invoice format, evidence scanner.
