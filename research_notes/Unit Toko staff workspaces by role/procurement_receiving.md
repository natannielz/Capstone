# Procurement and goods receiving back-office patterns (Indonesia): PR, PO, advance, receive, inspect, release, match, payable, pay, claims

Scope: staff/back-office screens only. Reference tools: Accurate Online, Mekari Jurnal / Mekari Expense, Odoo 17/18 (Indonesian strings), SAP Business One / S/4HANA, Mbiz, Pengadaan.com / iProc. Indonesian documents and tax rules: Coretax DJP (2025+), BAST / berita acara, surat jalan, tukar faktur. Notes are mapped to Unit Toko pages where useful (in Inferences).

## Q1. How do Indonesian platforms and ERPs used in Indonesia structure PR and PO screens, statuses, approval chains and PO documents?

### Takeaway
Indonesian tools split "document state" (Draf / Diajukan / Disetujui / Ditolak) from "fulfilment state" (Menunggu diproses / Sebagian diproses / Terproses / Ditutup). They show both in lists, approve with sequential value-band (berjenjang) rules, and let one PR feed several POs while showing only the remaining quantity. Accurate's real labels are the best ready-made Indonesian vocabulary. Odoo's official Indonesian strings and Jurnal's PR statuses are the next best.

### Cited Findings
**Accurate Online (most widely used Indonesian SME ERP)**
- PO fulfilment statuses are "Menunggu Diproses" (not yet passed to a next transaction), "Sebagian diproses" and "Terproses". Only "Menunggu Diproses" POs can be deleted. Deleting a processed or partly processed PO fails with "Data Gagal Diproses". — [Ultima Solusindo (Accurate partner), Oct 2025](https://ultimasolusindo.com/menghapus-transaksi-pesanan-pembelian-secara-massal/)
- A PO is closed instead of deleted, so history is kept. Per line: tick "Tutup pesanan" on unprocessed items. Whole PO: Info lainnya, then "Tutup". Both require an "alasan" (reason). A preference can auto-close unprocessed POs after N days from the PO date. — [Solusi Akuntansi Indonesia (Accurate trainer), Oct 2025](https://solusiakuntansiindonesia.com/menutup-pesanan-pembelian-pada-accurate-online/)
- Approval ("Persetujuan") is enabled per transaction type. On the purchasing side: Pesanan Pembelian, Penerimaan Barang, Faktur Pembelian, Pembayaran pembelian, Retur Pembelian, Harga Pemasok, Klaim Pemasok. On the inventory side: Permintaan Barang, Penyesuaian Persediaan, Pemindahan Barang, and others. — [Solusi Akuntansi Indonesia, Jul 2025](https://solusiakuntansiindonesia.com/fitur-persetujuan/)
- Saving a transaction under approval offers two choices: "draf" or "diajukan" (submitted). The approver filters the list by status "diajukan" and decides "disetujui", "ditolak" or "dibatalkan pengajuannya". — [Acis Indonesia](https://www.acisindonesia.com/penggunaan-menu-approval-di-accurate-online/)
- Approval is "berjenjang berurutan dengan rentang nilai": sequential, with value ranges. The next approver sees the request only after the previous one approves. Approval also works from a phone (Accurate Lite). — [doniirvan.id (Accurate consultant), Oct 2026](https://www.doniirvan.id/accurate-online/fitur-accurate-online/pembelian/)
- Internal demand is captured as "Permintaan Barang" and then processed into a Pesanan Pembelian. A purchase invoice can be created five ways: direct (no PO or receipt), from a PO, from a receipt, before goods arrive, or in advance (di muka). — [doniirvan.id](https://www.doniirvan.id/accurate-online/fitur-accurate-online/pembelian/)
- Draft-status pitfall: with the preference "Dapat memproses transaksi yang sedang diproses oleh transaksi berstatus Draf/Pengajuan" turned on, one PO can be pulled into two draft purchase invoices. Approving the second then fails with "Barang ABC atas Pesanan XYZ terdeteksi terproses dua kali". The PO's "Informasi Pesanan" tab lists which transactions consumed it. — [Ultima Solusindo, Jul 2025](https://ultimasolusindo.com/error-barang-abc-atas-pesanan-xyz-terdeteksi-terproses-dua-kali-mohon-cek-transaksi-yang-memproses-pesanan-tersebut-saat-menyetujui-transaksi-faktur-pembelian/)
- Accurate trainers describe PO status in English-style terms ("Open, Partially Received, Closed"). They also teach the PR approval flow, and PO creation from a PR, as standard. — [FAC Institute blog, Dec 2025](https://blogs.fac-institute.com/kuasai-fitur-pembelian-accurate-online-dengan-training-accurate-di-fac-institute/). (The "Open / Partially Received / Closed" labels are the trainer's paraphrase. The in-app labels are the Indonesian ones above.)
- Report "Pesanan per Barang (Belum Proses)" lists outstanding PO lines. A custom column "Kts. Blm Proses" = quantity − shipQuantity can be added. — [Mitraku, Aug 2024](https://mitraku.id/menampilkan-kuantitas-belum-terproses-pada-laporan-pesanan-pembelian-per-barang-belum-proses-accurate-online/)

**Mekari Jurnal**
- The Pembelian menu has 7 tabs: Faktur Pembelian; Tukar Faktur Pembelian (Join Purchase Invoice); Pengiriman Pembelian (Purchase Delivery); Pemesanan Pembelian (PO); Penawaran Pembelian (Quote); Permintaan Pembelian (Purchase Request); "Membutuhkan persetujuan" (approve draft purchase transactions). PO actions: Lihat & Cetak, Email, Buat Pengiriman, Buat Faktur, Tambah Deposit, Atur Transaksi Berulang. — [Mekari Jurnal Help Center](https://help-center.jurnal.id/hc/id/articles/16822555057689-Sekilas-Mengenai-Menu-Pembelian)
- Header KPI tiles above the list: "Pembelian Belum Dibayar", "Pembelian Jatuh Tempo", "Pelunasan Dibayar 30 Hari Terakhir". Each has a count badge and filters the list when clicked. Status filter values: Open, Overdue, Paid, Partial, Unpaid. (The help text says "4 status" but lists five, an inconsistency in the source.) — [Mekari Jurnal Help Center](https://help-center.jurnal.id/hc/id/articles/16822555057689-Sekilas-Mengenai-Menu-Pembelian)
- Permintaan Pembelian statuses: "Dalam proses" (nothing fulfilled yet), "Terpenuhi sebagian", and "Selesai" (set manually with the "Permintaan selesai" button once quantities are fulfilled). The form has a required "Urgensi" field (Rendah / Sedang / Tinggi), product lines, message, memo and attachment. Each new PR emails the "Staff Pengadaan". PRs in Open or Partial status cannot be edited or deleted. — [Mekari Jurnal Help Center](https://help-center.jurnal.id/hc/id/articles/17245524160537-Bagaimana-Cara-Mengelola-Permintaan-Pembelian-pada-Jurnal)
- One PR can become several POs (e.g., to compare suppliers). On "Tindakan → Buat pemesanan" the user selects lines ("Proses [n] produk"). Only unprocessed lines and quantities are offered. Zero quantity, or more than the remaining quantity, is blocked. The vendor is required. — [Mekari Jurnal Help Center](https://help-center.jurnal.id/hc/id/articles/23999859514009-Bagaimana-Cara-Mengelola-Pemesanan-Pembelian-dari-Menu-Permintaan-Pembelian)
- Roles: a "Roles Permintaan" user sees a reduced Pembelian menu. Converting a PR into a PO needs Ultimate or "Pemesanan & Penawaran" roles. — [Mekari Jurnal Help Center](https://help-center.jurnal.id/hc/id/articles/17245524160537-Bagaimana-Cara-Mengelola-Permintaan-Pembelian-pada-Jurnal)

**Mekari Expense (Mekari's procurement/spend product)**
- PO fields: Vendor, Transaction date & Due date, Organization, Reference no., Requester, Warehouse, Term of payment (Cash / Net 15 / Net 30 / Net 60 / Custom), Currency, Price include tax, Product, Description, Qty, Unit, Unit price, VAT, WHT, Amount, Note, Attachment. Buttons: "Submit for review" / "Save as draft". Statuses: Awaiting review → (Approve) Awaiting invoice → Closed once invoiced. Approver actions: Approve / Reject / Void. A workflow must exist before POs can be created. — [Mekari Expense Help Center](https://help-center.expense.mekari.com/hc/en-us/articles/58013433892377-How-to-Create-a-Purchase-Order-in-Mekari-Expense)
- A PO can request an advance payment (amount and terms) directly from the PO. Deposits are recorded automatically and reconciled with the invoice. — [Mekari Expense feature page](https://expense.mekari.com/en-sg/feature/purchase-order)

**Odoo (official Indonesian translation, 17.0 purchase module)**
- PO state labels in Indonesian: RFQ = "PPw" (Permintaan Penawaran); state sent = "PPw Dikirim"; to_approve = "Akan Disetujui"; done = "Dikunci" (Locked). Billing status: "Belum Ada Tagihan" (Nothing to Bill), "Menunggu Tagihan" (Waiting Bills), "Sepenuhnya Ditagih" (Fully Billed). Line fields: "Jml Diterima" (received qty), "Kuantitas Tertagih" (billed qty). Settings: "Penyetujuan Bertahap" and "2 level penyetujuan untuk mengkonfirmasi order pembelian" (two-step approval). Portal labels: "Dibayar" and "Menunggu Pembayaran". — [Odoo 17.0 purchase/i18n/id.po (GitHub)](https://raw.githubusercontent.com/odoo/odoo/17.0/addons/purchase/i18n/id.po)

**SAP**
- In S/4HANA, if an approval workflow exists, PRs and POs need approval, and no GR or invoice can be posted against a PO until it is approved. — [Espresso Tutorials (SAP book)](https://books.espresso-tutorials.com/invoice-verification-and-the-gr-ir-in-sap-s-4hana); [SAP Learning](https://learning.sap.com/courses/detailing-procurement-of-direct-materials/carrying-out-procurement-of-direct-materials-solution-process-steps_df81d8a6-5c63-4a5e-8074-1b50d0223d2d)
- SAP B1 document types in the purchasing chain: Purchase Request, Purchase Quotation, Purchase Order, Goods Receipt PO, Goods Return Request, Goods Return, A/P Down Payment, A/P Invoice, A/P Credit Memo, A/P Correction Invoice, Outgoing Payments. — [SAP B1 SDK table POR21](https://help.sap.com/doc/089315d8d0f8475a9fc84fb919b501a3/10.0/en-US/SDKhelp/POR21.html)
- In B1 a PO causes no value-based accounting postings. It only shows quantities "on order" in inventory reports. — [SAP Learning, B1 Purchasing Items](https://learning.sap.com/courses/managing-logistics-in-sap-business-one/purchasing-items)

**Indonesian procurement platforms**
- Mbiz (Lippo group, founded 2015) offers an end-to-end e-procurement platform (quote requests, PO, payment, logging every step) and a separate marketplace (Mbizmarket). Per its CEO, no client used the entire end-to-end stack as of 2020. — [CompassList interview, 2020](https://www.compasslist.com/insights/mbiz-working-toward-the-tipping-point-of-e-procurement-mainstreaming)
- Mbiz's "Manajemen Persetujuan" lets a company account invite internal approvers (finance, procurement, own team) and approve online from anywhere. — [Mbiz Insight](https://insight.mbiz.co.id/2020/04/21/bagaimana-karyawan-saya-bisa-menggunakan-e-procurement-mbiz/); [Mbizmarket Play Store listing](https://play.google.com/store/apps/details?id=com.mbizmarket.app&hl=en_US)
- iProc (the Pengadaan.com group's eProcurement) offers mobile approval of PR, goods, vendors and RFQ ("iProc 2 Go"), and "Smart Invoice & Payment" with integrated three-way matching. — [iproc.id](https://iproc.id/)
- A third-party Mbiz explainer describes value-threshold routing (e.g., ≤ Rp 50 juta to manager, > Rp 50 juta to director), a 24-hour reminder, and auto-delegation when an approver is away. This is a marketing blog with illustrative numbers, not Mbiz documentation. — [Kana Komputer, Mar 2025](https://www.kanakomputer.com/mbiz-solusi-e-procurement/)

### Inferences
- For "Permintaan pengadaan" and "Pesanan pembelian", use two status axes in the list:
  - Persetujuan: Draf → Diajukan / Menunggu persetujuan → Disetujui / Ditolak / Dibatalkan.
  - Pemenuhan: Menunggu diproses → Sebagian diproses → Terproses, or Ditutup with an alasan.
  These words are already familiar to Accurate-trained Indonesian staff. For a PO, add a supplier-facing step "Dikirim ke pemasok" (Odoo's "PPw Dikirim" analogue).
- Show the remaining quantity on each PR/PO line (dipesan / diterima / ditagih / sisa). Block over-conversion the way Jurnal does, and reserve quantities consumed by drafts, avoiding Accurate's double-processing error.
- Use "tutup dengan alasan" instead of delete once anything downstream exists. This matches Accurate and keeps the audit trail.
- Show sequential maker/checker approval as a stepper: who approved, when, and which value band applied. The next checker sees the item only after the prior step.

### Gaps
- No official Mbiz, Pengadaan.com/iProc or Bizzy help pages with exact PR/PO status names were found. Their sites are marketing-level. Bizzy: no current (2025–2026) procurement product information found.
- No BUMN eProc (PLN, Pertamina, Telkom) PO status list was found.
- Accurate's own help-center page listing every status label was not retrieved. The labels above come from Accurate partner/trainer sites quoting the UI.

## Q2. Goods receipt patterns: GRN against a PO, partial receipts, quantity vs quality inspection, rejection/retur, putaway; Indonesian documents

### Takeaway
Indonesian receiving runs in this order:
1. Check the supplier's surat jalan.
2. Count koli/qty (photo or video evidence).
3. QC incoming.
4. Split accepted from rejected goods.
5. Retur or replacement for rejects.
6. Putaway and system entry.

The receipt record carries the supplier's surat jalan number. Partial receipts leave the PO open. Inspection outcomes are recorded in a berita acara (BAPB / BAST) with a per-line Sesuai/Tidak Sesuai and an overall conclusion. ERPs model "held" stock as a separate stock type or location (SAP quality inspection / blocked stock; Odoo Input → Quality Control → Stock) that is not available until released.

### Cited Findings
**Receiving SOP and documents**
- Typical warehouse SOP:
  1. Match goods against the supplier's surat jalan.
  2. Open the truck seal, then check quantity and condition, documented with photo/video.
  3. If the koli count differs from the surat jalan, inform and correct it together with the supplier.
  4. QC Incoming inspects.
  5. Rejected goods go to retur or another supplier-instructed procedure.
  6. Accepted goods are unloaded and stored at the prepared location.
  7. System entry and handover are done, and the receipt is recorded on the stock card with surat jalan details.
  — [Logistics Plus Indonesia SOP, May 2024](https://logisticsplus.co.id/prosedur-sop-penerimaan-barang-di-gudang-warehouse/)
- Perum BULOG SOP (state logistics; a background convention):
  - Fields: a surat jalan must carry nomor surat jalan, nomor polisi kendaraan, jumlah barang (koli and berat), asal barang, tanggal masuk.
  - Order: quality inspection comes before weighing. "Bukti Timbang Penerimaan Barang" is made separately per condition (baik kemasan utuh / baik kemasan tidak utuh / rusak) and signed by the weigher and the deliverer.
  - Recap: the receipt document (GD1M) recaps the weighing slips, is signed by the Kepala Gudang and the supplier in 3 copies, and is the basis for the payment order (SPP). A warehouse may not receive goods without a valid receipt order (PO/SPK).
  — [BULOG SOP-19/DO100/09/2018 (studylibid copy)](https://studylibid.com/doc/4331317/prosedur-penerimaan-dan-pengeluaran-barang)

**ERP receipt behaviour**
- Accurate Online Penerimaan Barang:
  - Fields: "Terima dari" (supplier), "Tanggal" (date goods actually arrived), "No Form #" (auto), "No Terima #" (the supplier's surat jalan number, so the receipt can be matched to supplier documents for checks and returns).
  - Lines are pulled via "Ambil → Pesanan", and the quantity received can be adjusted. Saving updates stock and marks the PO as processed to prevent double entry.
  — [Solusi Akuntansi Indonesia, Oct 2025](https://solusiakuntansiindonesia.com/penerimaan-barang-dari-pesanan-pembelian/)
- In Accurate, a short delivery is recorded by lowering the qty, and the PO stays outstanding for the remainder. Several POs from the same supplier can be received on one receipt. The receipt journal debits Persediaan and credits "Hutang Belum Tertagih" (unbilled payable). Real AP appears only when the receipt is pulled into a Faktur Pembelian. One practitioner tip: restrict warehouse roles so they record physical quantity without seeing purchase prices. — [penjualanonline.id (Accurate reseller), Jun 2026](https://penjualanonline.id/cara-membuat-penerimaan-barang-dari-pesanan-di-accurate-online/)
- SAP B1 GRPO:
  - Typically entered by the warehouse person taking delivery.
  - Can copy from one or several POs; quantities and prices can be adjusted before posting.
  - The GRPO should match the vendor's packing slip, which SAP calls legally binding. Discrepancies are resolved with the vendor before posting.
  - The GRPO credits "Goods Received Not Invoiced".
  — [SAP Learning](https://learning.sap.com/courses/managing-logistics-in-sap-business-one/purchasing-items); [SAP B1 training solutions PDF](https://help.sap.com/doc/saphelpiis_hc_b1_image_repository_consultant_training_basic_b1_90_tb1000_02_02_sol_pdf/9.0/en-US/B1_90_TB1000_02_02_Sol.pdf)
- SAP S/4HANA Post Goods Receipt:
  - Entering the supplier's Delivery Note number lets AP later reference it instead of the PO.
  - Received qty can be split across storage locations.
  - A GR/issue slip can be printed.
  - "Goods receipt blocked stock" records a conditionally accepted delivery that is not yet valuated.
  — [SAP Learning](https://learning.sap.com/courses/detailing-procurement-of-direct-materials/carrying-out-procurement-of-direct-materials-solution-process-steps_df81d8a6-5c63-4a5e-8074-1b50d0223d2d)

**Held stock and inspection**
- SAP stock types: unrestricted-use, quality inspection, blocked. The stock type is chosen at GR. Quality-inspection and blocked stock are not available for withdrawal by default. — [SAP Help, Stock Types](https://help.sap.com/doc/a6a8c7536e8e2a4be10000000a174cb4/700_SFIN3E%20006/en-US/299bc7536e8e2a4be10000000a174cb4.html)
- Non-valuated GR blocked stock: movement 103 receives conditionally, 105 releases (valuation happens only at release), 124 returns to vendor. A Fiori app lists overdue GR-blocked materials by "Days since Posting Date". — [SAP Learning, GR with reference](https://learning.sap.com/courses/inventory-management-and-physical-inventory-in-sap-s-4hana/posting-a-goods-receipt-with-reference)
- Inspection at GR:
  1. The GR creates an inspection lot, and the quantity posts to inspection stock, unusable by other processes.
  2. The technician records results or defects.
  3. The quality engineer posts a "usage decision" (pass/fail). Positive moves stock to unrestricted. Negative moves it to blocked stock for return to the supplier, with the complaint handled as a quality notification.
  Missing supplier quality certificates can block the usage decision. — [SAP Learning QM](https://learning.sap.com/courses/applying-sap-s-4hana-quality-management/processing-inspections-at-good-receipt_bf84141a-43bd-4744-a9f8-988655750611); [SAP Learning, certificates](https://learning.sap.com/learning-journeys/configuring-sap-s-4hana-quality-management/processing-quality-certificates-at-goods-receipt)
- Odoo three-step receipt: goods are received into WH/Input, moved to WH/Quality Control, then to stock. Products are unavailable until they leave QC. Each hop is a separate transfer validated from Inventory task cards ("# To Process"). A "Receipt" smart button on the PO opens the receipt. — [Odoo 17 docs, three-step receipt](https://simbiozems.com/documentation/17.0/applications/inventory_and_mrp/inventory/shipping_receiving/daily_operations/receipts_three_steps.html)

**Berita acara conventions**
- Berita Acara Pemeriksaan Barang template, made on the PPK's request:
  - I. Dasar Pemeriksaan: Kontrak/SPK nomor and tanggal, nama pengadaan, nama penyedia.
  - II. Tim Pemeriksa.
  - III. Hasil pemeriksaan: table with Uraian Barang, Spesifikasi, Jumlah, Hasil (Sesuai / Tidak Sesuai), Keterangan. Checks cover jenis and jumlah, spesifikasi, kondisi/kualitas, kelengkapan, fungsi, identitas (merek, tipe, nomor seri, kode).
  - IV. Kesimpulan, three options: "dapat diterima"; "perlu perbaikan/pelengkapan/penggantian oleh Penyedia"; "perlu pemeriksaan ulang".
  - V. Dokumentasi: photos, serial numbers.
  - Signed by the Tim Teknis, with the PPK and the Penyedia acknowledging.
  — [LPSK template, 2026](https://e-ppid.lpsk.go.id/storage/content-files/BERITA%20ACARA%20PEMERIKSAAN%20BARANG.pdf)
- Under Perpres 16/2018 Arts. 57–58: at 100% completion the supplier requests handover in writing, the PPK inspects, and the PPK and supplier sign the BAST. The PA/KPA then has the PjPHP/PPHP perform an administrative check, recorded in a berita acara. — [LPSE Lombok Utara](https://spse.inaproc.id/lombokutarakab/pengumuman/24487)
- Government e-Katalog BAST (2026 example):
  - Per-shipment ("Termin 1") summary: "Permintaan Tiba", "Pesanan Tiba", "Selesai Pemeriksaan".
  - Item list with "Barang PDN" flag.
  - Clauses: the buyer has inspected every shipment, and the BAST is the primary reference for supplier billing. It is valid without wet signatures, and responsibility transfers on signing.
  — [Kesbangpol Banten BAST PDF, Apr 2026](https://kesbangpol.bantenprov.go.id/storage/kesbangpol/files/866/Upload/Tahun%202026/Paket%20Pengadaan%20Telah%20Selesai%202026.pdf)
- BAST structure (government format): numbered, dated and placed. PIHAK PERTAMA hands over, PIHAK KEDUA receives, with an itemised value. Responsibility transfers on signing. Two stamped (bermeterai) copies. — [PMK 265/PMK.05/2014 annex](https://jdih.kemenkeu.go.id/api/download/fulltext/2014/265~PMK.05~2014PerLamp.pdf)

**Retur**
- Purchase return flow:
  1. Warehouse/QC identifies nonconforming, damaged or over-shipped goods and makes an initial return report.
  2. Purchasing confirms with the supplier, then a retur pembelian document is created.
  3. Goods are packed and shipped with the retur document, and the vendor confirms receipt.
  4. Outcome: either a replacement (shipped without a nota retur) or a financial return. The system reduces stock, and finance reconciles.
  — [Netvision Knowledge Base](https://kb.netvisionsoftware.com/?epkb_post_type_1=alur-sprtn)
- Accurate "Klaim Pemasok" handles sending damaged goods to the supplier and receiving replacements. It is integrated with inventory and auto-journals, and is separate from Retur Pembelian (the financial return). — [doniirvan.id](https://www.doniirvan.id/accurate-online/fitur-accurate-online/pembelian/)

### Inferences
- "Penerimaan pengadaan" (container receipt) should require the supplier's surat jalan number, vehicle plate, koli count and photos at the dock. It should allow partial receipt against a PO and keep the PO "Sebagian diterima".
- "Pemeriksaan barang pengadaan" and "Penerimaan mutu barang" map well to a BAPB-style screen:
  - Dasar (PO/SPK no.).
  - Tim pemeriksa (maker) and approver (checker).
  - Per-line table: Sesuai / Tidak sesuai, qty baik / qty rusak, catatan, foto.
  - Kesimpulan with the three LPSK options.
- "Penerimaan / Penilaian / Pelepasan stok ditahan" mirrors SAP's GR-blocked or quality-inspection stock and the usage decision. Held quantity stays non-sellable. Valuation is set at release (as in SAP movement 105). A rejected portion goes to Klaim pemasok / retur. An aging list ("hari sejak diterima") of held stock is a proven pattern (SAP Fiori overdue GR blocked stock).
- Keep replacement claims (barang pengganti) separate from financial returns, as Accurate separates Klaim Pemasok from Retur Pembelian. Tax treatment differs: a nota retur is only needed when goods are returned for credit (see Q3).

### Gaps
- No Indonesian ERP help page was found on a dedicated "barang karantina / stok ditahan" status. Accurate and Jurnal appear to rely on separate warehouses for this. Not confirmed.
- No source specifically covers koperasi receiving practice.

## Q3. Three-way matching, price/quantity variances, faktur pajak masukan, payables aging, uang muka and settlement/return

### Takeaway
Three-way match (PO, penerimaan, faktur pemasok) is standard and usually enforced by "bill on received quantities". Indonesian buyers add a fourth and fifth check before booking AP:
- the supplier's e-Faktur (Coretax prepopulated Pajak Masukan, with "Dilaporkan oleh Penjual" YES/NO), and
- the "tukar faktur" paper bundle: invoice bermeterai, signed surat jalan or GRN, PO copy, faktur pajak.

The due date runs from the date the complete documents are accepted. Down payments are a separate document type, applied on the final invoice ("Ambil → Uang Muka") and refunded through a dedicated return-of-DP transaction.

### Cited Findings
**Three-way match in ERPs**
- Odoo:
  - Bill Control: "Ordered quantities" creates a draft bill on PO confirmation. "Received quantities" bills only what has been received; "Create Bill" before any receipt raises an error.
  - "3-way matching" is a setting meant only for received-quantities control.
  - The vendor bill has a "Bill Reference" for matching. Banners show Paid / In Payment.
  — [Odoo 18 docs](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/purchase/manage_deals/manage.html); [Odoo 17 docs](https://documentation.erptoancau.com/17.0/applications/inventory_and_mrp/purchase/manage_deals/manage.html)
- Odoo's 3-way match sets a "Should Be Paid" value on the bill: Yes (received, so payable), No (not yet received), Exception (ordered, received and billed quantities disagree). — [Cybrosys Odoo 17 book](https://www.cybrosys.com/odoo/odoo-books/v17/purchase/3way-matching/)
- SAP S/4HANA supplier invoice:
  - Simulate/Post compares the quantity and amount of each invoice item with the PO item. The balance must be within tolerance limits.
  - "Unplanned Delivery Costs" can be added.
  - The invoice can be "Parked" for someone else to complete, which works as a maker/checker split.
  - PO history and document flow show the related GRs.
  — [SAP Learning](https://learning.sap.com/courses/detailing-procurement-of-direct-materials/carrying-out-procurement-of-direct-materials-solution-process-steps_df81d8a6-5c63-4a5e-8074-1b50d0223d2d)
- Typical discrepancies caught: quantity differences, price changes after the PO, unagreed freight or surcharges, and missing discounts. Payment is held until resolved, with tolerances and exceptions in practice. — [Pegadaian article](https://pegadaian.co.id/artikel/wirausaha/three-way-matching-adalah); [KantorKu](https://kantorku.id/blog/three-way-matching); [HashMicro ID](https://www.hashmicro.com/id/blog/three-way-matching/)

**Indonesian buyer billing requirements (corporate practice)**
- Invoice rules:
  - Invoice bermeterai, carrying the PO/Kontrak/SPK number; 1 invoice = 1 PO.
  - Name, address and NPWP exactly as on the PO.
  - Duplicate invoice numbers are rejected.
  - DPP and PPN shown separately (and goods vs services DPP separated).
  - If a DP was paid, the invoice must show the total transaction value and the DP already paid.
  - Item names and UoM must match the PO, or a procurement-acknowledged explanation letter is attached.
- Faktur pajak rules:
  - QR code or e-Nofa present; FP date = invoice date; 1 FP per invoice.
  - For a DP, the FP date is the earlier of goods delivery (per surat jalan) or funds received.
  - A late FP is uncreditable, so the vendor is paid DPP only.
- Attachments: original surat jalan signed by sender and receiver, plus a "Rekap Surat Jalan" if there are several. Services need a BAPP/BAST.
- Invoice status notifications: Accepted / In-progress / Rejected. The due date is counted from receipt of complete and correct documents.
— [PT SMART Tbk billing procedure (2020)](https://www.smart-tbk.com/wp-content/uploads/2020/07/Prosedur-dan-Persyaratan-Penagihan.pdf). (Meterai amounts in this 2020 PDF, Rp3.000/Rp6.000, are outdated. The Monotaro page below uses the current Rp10.000 for ≥ Rp5.000.000.)
- "Tukar faktur" at a buyer (PT Monotaro Indonesia):
  - Exchange days: Tuesday and Thursday, 13:00–16:00.
  - Bundle, in order: (a) 2 copies of a tanda terima with No PO and invoice value incl. PPN; (b) original invoice bermeterai Rp10.000 if ≥ Rp5.000.000; (c) original GRN or surat jalan signed with the receiver's name; (d) PO copy; (e) faktur pajak.
  - The FP date must equal the GRN date. Incomplete documents are returned unprocessed.
  — [Monotaro tukar faktur guide](https://www.monotaro.id/panduan-tukar-faktur-dan-pembayaran)
- Generic tukar faktur flow: the supplier submits the bundle, the buyer issues a tanda terima with a reference number, then three-way verification, internal approval (one level for small, several for large amounts), payment scheduling by termin, and archive/reconcile. — [adminca.sch.id, Oct 2025](https://adminca.sch.id/tukar-faktur/) (secondary source)
- Accurate's Tukar Faktur form (sales side, mirrors the practice): Nomor #, Tanggal Dokumen, "Tanggal Tukar", "Jatuh Tempo", Faktur selection. Jurnal has "Tukar Faktur Pembelian (Join Purchase Invoice)" on the purchase side. — [Accurate Help](https://help.accurate.id/product/accurate-online/fitur-aol/penjualan/tukar-faktur/mengenal-fitur-tukar-faktur/); [Mekari Jurnal Help Center](https://help-center.jurnal.id/hc/id/articles/16822555057689-Sekilas-Mengenai-Menu-Pembelian)

**Faktur pajak masukan in Coretax (2025–2026)**
- Supplier output invoices are prepopulated as the buyer's "Pajak Masukan" list in Coretax (e-Faktur → Pajak Masukan). The buyer ticks invoices and clicks "Credit Invoice", and the status becomes "Credited". — [Panduan Ringkas Coretax (Jan 2025)](https://ptpsi.com/wp-content/uploads/2025/01/Panduan-Ringkas-Coretax-14.01.2025.pdf)
- Validation routes: scan the faktur's QR code (shows seller/buyer NPWP, nomor faktur, tanggal, DPP, PPN, status Approved / Tidak Sah); a bell notification; or search the Pajak Masukan list by number or date. — [Pajakku, Feb 2025](https://pajakku.com/artikel/cara-cek-validitas-faktur-pajak-dengan-qr-code-di-coretax)
- PER-11/PJ/2025 Art. 122(3): the buyer's crediting does not depend on whether the seller reported the faktur in its SPT. The Pajak Masukan list has a "Dilaporkan oleh Penjual" column (YES/NO) for self-checking. — [IKPI, Aug 2025](https://ikpi.or.id/per-11-pj-2025-tegaskan-hak-pkp-kreditkan-pajak-masukan-tanpa-tunggu-pelaporan-penjual/); [Ortax, Aug 2025](https://ortax.org/pengkreditan-pajak-masukan-kini-tidak-bergantung-pada-pelaporan-pkp-penjual)
- Timing:
  - Coretax was updated so e-Faktur input tax can be credited up to 3 tax periods after the invoice period. — [DJP KT-08/2025, 20 Feb 2025](https://www.pajak.go.id/sites/default/files/2025-02/KT-08_2025%20Pengkreditan%20Pajak%20Masukan%20Pasca-Implementasi%20Coretax...pdf)
  - Sellers have until the 15th of the following month to upload, so buyers should check completeness before filing. — [DJP article, Dec 2024](https://pajak.go.id/id/artikel/pengkreditan-pajak-masukan-1-januari-2025-begini-penjelasannya)
  - Note: DJP's Dec 2024 article stated same-period-only crediting for Jan 2025 fakturs. KT-08/2025 superseded that in practice.
- Returns (nota retur):
  - Under PMK-81/2024 Art. 288 the buyer creates an electronic nota retur in Coretax ("Retur Pajak Masukan → Buat Retur", or the retur icon on a Credited/Uncredited faktur). It is dated on the day goods are returned and carries the faktur number, item, qty, and PPN/PPnBM returned.
  - The seller gets a notification and approves or rejects under "Retur Pajak Keluaran".
  - The value flows to the buyer's SPT Masa PPN B.2/B.3 and the seller's A.2 in the retur period.
  - If input tax was already credited, the retur reduces Pajak Masukan; otherwise it reduces cost or asset.
  — [DJP Coretaxpedia, Sep 2025](https://www.pajak.go.id/coretaxpedia/retur-barang); [FAQ Coretax](https://t.me/s/FAQcoretax?before=216); [Ortax, Feb 2025](https://ortax.org/mekanisme-pembuatan-nota-retur-dan-nota-pembatalan-pada-aplikasi-coretax)

**Uang muka pembelian (supplier advances)**
- Accurate Online "Uang Muka Pembelian":
  - Fields: Pemasok, Tanggal, Nilai Uang Muka, Kena Pajak; plus, under Info lainnya, the supplier's Nomor Faktur for the DP.
  - Then "Proses → Pembayaran" to pay it.
  - Applied later on the Faktur Pembelian via "Ambil → Uang Muka", ticking the DP transactions to deduct.
  — [Accurate Help, Jul 2026](https://help.accurate.id/product/accurate-online/fitur-aol/pembelian/uang-muka-pembelian/membuat-uang-muka-pembelian/); [Mitraku, Aug 2024](https://mitraku.id/membuat-faktur-pembelian-dipotong-uang-muka-pembelian-accurate-online/)
- Accurate journals:
  - DP invoice: Uang Muka Pembelian (D) / Hutang Dagang (K).
  - Final invoice from a receipt: Unbilled Goods (D) / Hutang Dagang (K) and Uang Muka (K).
  - The supplier's DP balance is visible on the supplier list.
  — [account.co.id (Accurate desktop)](https://account.co.id/uang-muka-pembelian-di-accurate/); [Szeto Accurate, Apr 2025](https://www.szetoaccurate.com/retur-uang-muka-pembelian-di-accurate-5/)
- Return of a DP (Accurate 5): Pembelian → Pembayaran Pembelian → Tambah → "Retur Pembayaran Uang Muka". Pick the supplier and the DP document, enter the refunded amount and the Kas/Bank account. Partial returns leave the remaining DP balance. Use cases: cancelled order, goods not delivered, DP exceeds the final invoice. — [trainingaccurate.com, Jul 2025](https://trainingaccurate.com/blog/retur-uang-muka-pembelian-accurate-5/)
- SAP B1 has a distinct "A/P Down Payment" object in the purchasing chain. — [SAP B1 SDK POR21](https://help.sap.com/doc/089315d8d0f8475a9fc84fb919b501a3/10.0/en-US/SDKhelp/POR21.html)

**Payables and payment (maker/checker)**
- Accurate "Perintah Pembayaran + Transfer Pemasok", a two-step payment:
  1. Select unpaid invoices (filter by supplier or due date) and set a transfer deadline, method (transfer / VA / cek-giro) and destination account. Status: "Belum Ditransfer", via a clearing account.
  2. When finance actually transfers, record "Transfer Pemasok". Status: "Sudah Diproses", and the journal reverses to the real bank.
  This is described as segregating "yang menyetujui" from "yang mengeksekusi". — [doniirvan.id](https://www.doniirvan.id/accurate-online/fitur-accurate-online/pembelian/)
- Jurnal's AP overview tiles: Belum Dibayar, Jatuh Tempo, Dibayar 30 hari terakhir. Accurate has "umur utang" (AP aging) and "pesanan belum diproses" reports. — [Mekari Jurnal Help Center](https://help-center.jurnal.id/hc/id/articles/16822555057689-Sekilas-Mengenai-Menu-Pembelian); [doniirvan.id](https://www.doniirvan.id/accurate-online/fitur-accurate-online/pembelian/)

### Inferences
- "Dokumen pemasok dan pencocokan" should show, per supplier invoice:
  - a 3-column match grid (Dipesan / Diterima (lolos mutu) / Ditagih) per line, with price and qty variance chips;
  - a tax panel: NSFP/nomor faktur, tanggal FP vs tanggal penerimaan, DPP/PPN, Coretax status (Approved / Credited / Dilaporkan oleh Penjual);
  - a document checklist mirroring tukar faktur: invoice bermeterai, surat jalan bertanda tangan, PO copy, faktur pajak, BAST if any.
  A match outcome like Odoo's "Harus dibayar: Ya / Belum / Pengecualian" is a compact status.
- "Utang pemasok" should count the due date from "tanggal dokumen lengkap diterima" (Indonesian corporate practice). Show aging buckets plus a "menunggu dokumen" state.
- "Otorisasi uang muka" → "Bukti uang muka pemasok" → "Pengembalian uang muka" mirror Accurate's DP lifecycle:
  - DP document (with the supplier's DP invoice number and Kena Pajak flag), then payment evidence.
  - Application on the final invoice, with the remaining DP saldo visible.
  - Refund via a dedicated return-of-DP record with a bank account.
- The final invoice should show "Total transaksi − uang muka terpakai = sisa tagihan", as SMART requires on supplier invoices.
- "Verifikasi debit pemasok" and "Klaim pemasok": rejected goods that are physically returned need a Coretax nota retur by the buyer within the retur period. Replacement-only claims need none. Show the nota retur number and status (diajukan / disetujui penjual / ditolak) on the claim.

### Gaps
- No source specifies standard price/qty tolerance percentages used in Indonesia.
- Unit Toko's own PKP status is unknown. If the koperasi is not PKP, input-tax crediting does not apply, but the nota retur still applies to non-PKP buyers per Coretaxpedia.
- No Indonesian-specific AP aging bucket convention was found beyond generic "umur utang" reports.

## Q4. Supplier master data: NPWP, rekening verification, PKP status, documents, and fraud controls

### Takeaway
Indonesian vendor registration (Bank Indonesia, Pengadaan.com, PETRONAS PePS) asks for a document set:
- Legal and licensing: Akta pendirian/perubahan + SK Kemenkumham, NIB (risk-based, with KBLI).
- Tax: NPWP (16 digits since the 2024 NIK/NPWP integration), SPPKP or a non-PKP statement, SPT.
- Bank: a bank book cover or rekening koran, or a surat referensi bank.
- People: KTP/NPWP of directors and shareholders.

Documents often carry expiry tracking, and a separate review/verification step precedes approval. Bank-account controls in practice:
- account name must match the supplier name on the PO;
- changes need a signed, stamped (bermeterai) statement from an authorised official;
- APIs (bank name-inquiry) return the account holder name for automated verification.

### Cited Findings
**Registration documents**
- Bank Indonesia vendor registration:
  - Pre-registration: company info incl. NPWP; contact; address; commodity classification (aligned to anggaran dasar, NIB); "Informasi Banking" (nomor rekening, nama pemilik rekening, nama bank, cabang, mata uang).
  - Uploads: Akta Pendirian + SK Menkumham; latest Akta Perubahan (name, shareholders, management); shareholder and management KTP/NPWP; NPWP + SKT + SPPKP (if PKP) + Surat Keterangan Fiskal; NIB berbasis risiko; Sertifikat Standar per KBLI; financial statements; "Buku Rekening Bank" (bank book cover or rekening koran header showing account number, account name, bank); Pakta Integritas bermeterai.
  - Original documents are verified in person on a quarterly schedule.
  — [BI requirements PDF](https://www.bi.go.id/id/layanan/lelang-jasa-barang/Documents/Persyaratan-Dokumen-Registrasi-Rekanan-Badan-Usaha.pdf); [BI older version](https://www.bi.go.id/id/layanan/lelang-jasa-barang/Documents/Syarat-Syarat-Menjadi-Rekanan-Badan-Usaha.pdf)
- Pengadaan.com, non-individual vendors: Akta pendirian/perubahan; NIB/NIB RBA with KBLI attachment; certificates; NPWP; "PKP/Non PKP"; SPT 1771 and filing proof (2 years); financial statements (2 years); 3 months of bank statements or a bank reference letter; e-KTP of shareholders, directors and commissioners; "NPWP 16 digit" for individuals and domestic companies. Domisili / SIUP / TDP / API are no longer needed since NIB covers them. — [Pengadaan.com Helpdesk](https://helpdesk.pengadaan.com/portal/id/kb/articles/faq-syarat-kelengkapan-dokumen-vendor-non-perorangan)
- Pengadaan.com vendor profile sections (as required by PT TIMAH): Data Utama, Legal, Pajak (NPWP, PKP/SPPKP, SPT Tahunan), Keuangan (Data Bank, Rekening Koran, Surat Referensi Bank, Modal, Laporan Keuangan), Saham, Pengurus, Personil, Pengalaman, Fasilitas, Klasifikasi, Catatan. "Simpan & Selesai" submits the profile for review by the Pengadaan.com team. — [PT TIMAH vendor guide](https://timah.com/userfiles/post/240710668DEEF29A884.pdf)
- PETRONAS Indonesia PePS: mandatory documents include NPWP, a "Surat Referensi Bank Sesuai No Rekening", and directors' terms of office. Every document must be valid with an expiry date more than 90 days away, or the vendor becomes ineligible for sourcing. — [PETRONAS PePS FAQ](https://peps.petronas.com/FAQ.aspx)
- Accurate's supplier master stores contacts, NPWP, bank account(s) for transfers, and more than one AP account per supplier. — [doniirvan.id](https://www.doniirvan.id/accurate-online/fitur-accurate-online/pembelian/)

**Bank-account controls and verification**
- Bank-account change control (buyer side): if the registered payment account changes, the vendor must attach a "Surat Pernyataan Perubahan Rekening" bermeterai, signed by an authorised official and stamped. The name on the account must match the name on the PO. If the invoice lacks the account number, a stamped statement letter is required. — [PT SMART Tbk billing procedure](https://www.smart-tbk.com/wp-content/uploads/2020/07/Prosedur-dan-Persyaratan-Penagihan.pdf)
- BRI API "Account Name Validation" (POST /v1.0/validation-account/name-validate, v2.0 July 2024):
  - Takes bankCode and accountNumber and returns accountName ("Nama Pemilik Rekening").
  - Error codes distinguish closed (0618), passive (0619), dormant (0621), VA debit (0622), loan (0623) and deposito (0624) accounts, plus inquiry timeout (0102).
  - Does not work for virtual accounts.
  — [BRIAPI docs](https://developers.bri.co.id/id/docs/api-docs-account-name-validation)
- Flip "Bank Account Inquiry" (/v2/disbursement/bank-account-inquiry) returns the holder name. It is instant if cached, otherwise asynchronous via callback (status PENDING until resolved). — [Flip docs, updated Oct 2026](https://docs.flip.id/docs/api/account-inquiry/); [laravel-flip README](https://github.com/reefki/laravel-flip/blob/main/README.md)
- iFortepay SNAP Account Inquiry returns beneficiaryAccountName, inquiryStatus (PENDING / SUCCESS / FAILED) and beneficiaryAccountStatus (ACTIVE / INVALID). — [iFortepay docs](https://developer.ifortepay.id/snap/payment-gateway/disbursement/account-inquiry)
- Xendit does not validate account names by default on payouts. Validation needs the separate Iluma "Name Validator" (v1.2 returns the holder name; v2.1 returns a match result). Iluma also offers an "NPWP Validator" (NPWP plus holder name). — [Xendit Help](https://help.xendit.co/hc/en-us/articles/360027727392-Can-I-send-disbursement-into-Virtual-Account); [Xendit Data Services](https://help.xendit.co/hc/id/articles/14142333152793-Produk-apa-saja-yang-disediakan-Layanan-Data-Xendit)

### Inferences
- "Pemasok dan kode sumber" should hold:
  - identity: NPWP 16 digit, status PKP / Non-PKP with SPPKP upload, NIB + KBLI, Akta + SK Kemenkumham;
  - document expiry dates, with a warning before expiry (PETRONAS uses a 90-day rule);
  - a verification state (Draf → Menunggu verifikasi → Terverifikasi / Ditolak).
- "Rekening pemasok" (maker/checker), proven controls:
  1. Evidence upload: buku tabungan cover, rekening koran header, or surat referensi bank.
  2. Name-inquiry result: nama pemilik rekening from the bank/API with timestamp; flag closed or dormant accounts.
  3. An exact or fuzzy match of the inquiry name to the legal name of the supplier.
  4. A signed change statement (surat pernyataan perubahan rekening) for any change.
  5. Checker approval by someone other than the maker. A new account should not be payable until approved.
  Show old vs new account side by side in the approval view.
- Because BRI and Flip inquiries can time out or return asynchronously, the UI needs a "Menunggu hasil cek rekening" state, not just pass/fail.

### Gaps
- No Indonesian source quantifies vendor bank-change fraud (BEC) incidents. No official OJK/BI guidance on supplier-account change controls was found.
- BNI-specific vendor requirements and any BNI account-inquiry API documentation were not retrieved. These are relevant since Unit Toko sells to BNI branches and may bank with BNI.
- Whether Coretax offers a public PKP-status check usable by buyers was not confirmed in this search.

## Q5. How to show a whole procurement case as one timeline (SAP Document Flow, Odoo smart buttons, Accurate "Proses")

### Takeaway
Every reference tool links documents base-to-target and gives a "chain" view from any document:
- SAP: Document Flow / Process Flow per PO item; B1 Relationship Map.
- Odoo: smart buttons (Receipt, Bills) with counts, plus banners.
- Accurate: "Informasi Pesanan" tab, and the "Ambil" (pull from) / "Proses" (push to) buttons.

Indonesian e-Katalog BASTs show per-termin milestones (Permintaan Tiba / Pesanan Tiba / Selesai Pemeriksaan). The pattern for Unit Toko: one case page keyed by the PR/PO. It shows a horizontal stepper of the stages, with per-stage status, quantity progress, owner (maker/checker), and links to the stage documents.

### Cited Findings
- SAP Document Flow shows all preceding and subsequent documents tied to a business transaction, with their status, from whichever document is opened. In MM, POs nest under their PR and GRs under their PO. Deleted items are highlighted in colour. — [SAP Help, Document Flow](https://help.sap.com/doc/b183ce53118d4308e10000000a174cb4/700_SFIN3E%20006/en-US/b4dfb65334e6b54ce10000000a174cb4.html)
- SAP S/4HANA Manage Purchase Orders (F0842A) has a "Process Flow" tab at line-item level, from purchase requisition through supplier invoice. Material and accounting documents are reachable from the process flow. — [Espresso Tutorials](https://books.espresso-tutorials.com/invoice-verification-and-the-gr-ir-in-sap-s-4hana); [SAP Learning](https://learning.sap.com/courses/detailing-procurement-of-direct-materials/carrying-out-procurement-of-direct-materials-solution-process-steps_df81d8a6-5c63-4a5e-8074-1b50d0223d2d)
- SAP B1 Relationship Map visualises chains of purchasing documents (PO → GRPO → A/P invoice → outgoing payment), related journal entries, internal reconciliations and payments. Its "Document Tree" view adds the outgoing payment once posted. — [SAP Help, Relationship Maps PDF](https://help.sap.com/doc/011000358700000787192011e/8.82/en-US/RelationshipMaps_881.pdf); [SAP B1 training solutions](https://help.sap.com/doc/saphelpiis_hc_b1_image_repository_consultant_training_basic_b1_90_tb1000_02_02_sol_pdf/9.0/en-US/B1_90_TB1000_02_02_Sol.pdf)
- SAP SNC document flow shows the direct predecessor and successor of the chosen document, with navigation between them (PO, ASN, invoice). — [SAP Help, SNC Document Flow](https://help.sap.com/doc/saphelp_snc70/7.0/en-US/49/dc4a53548f6bf2e10000000a42189b/content.htm?no_cache=true)
- Odoo: after PO confirmation a "Receipt" smart button appears on the PO, opening the linked receipt. The user returns via breadcrumb to "Create Bill". The PO shows a green "Paid" or "In Payment" banner once the bill is paid. — [Odoo 18 docs](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/purchase/manage_deals/manage.html); [Odoo 17 three-step receipt](https://simbiozems.com/documentation/17.0/applications/inventory_and_mrp/inventory/shipping_receiving/daily_operations/receipts_three_steps.html)
- Accurate: a PO's "Informasi Pesanan" tab lists the transactions that processed it. Downstream documents are created by "Ambil → Pesanan / Uang Muka" on the target form, or "Proses → Pembayaran" on the source form. — [Ultima Solusindo](https://ultimasolusindo.com/error-barang-abc-atas-pesanan-xyz-terdeteksi-terproses-dua-kali-mohon-cek-transaksi-yang-memproses-pesanan-tersebut-saat-menyetujui-transaksi-faktur-pembelian/); [Accurate Help](https://help.accurate.id/product/accurate-online/fitur-aol/pembelian/uang-muka-pembelian/membuat-uang-muka-pembelian/)
- Accurate positions purchasing as "Procure-to-Pay berantai: setiap rupiah keluar punya jejak". Documents flow from order to settlement without re-entry, and stages can be skipped. — [doniirvan.id](https://www.doniirvan.id/accurate-online/fitur-accurate-online/pembelian/)
- Government e-Katalog BAST shows per-termin delivery milestones ("Permintaan Tiba", "Pesanan Tiba", "Selesai Pemeriksaan") and references the Surat Pesanan number. — [Kesbangpol Banten BAST](https://kesbangpol.bantenprov.go.id/storage/kesbangpol/files/866/Upload/Tahun%202026/Paket%20Pengadaan%20Telah%20Selesai%202026.pdf)
- Mekari Expense PO statuses chain to the invoice (Awaiting review → Awaiting invoice → Closed). Jurnal's PO page exposes next-step actions (Buat Pengiriman, Buat Faktur, Tambah Deposit), which act as a "what's next" bar. — [Mekari Expense Help](https://help-center.expense.mekari.com/hc/en-us/articles/58013433892377-How-to-Create-a-Purchase-Order-in-Mekari-Expense); [Mekari Jurnal Help](https://help-center.jurnal.id/hc/id/articles/16822555057689-Sekilas-Mengenai-Menu-Pembelian)

### Inferences
- **Case header.** One "Kasus pengadaan" page per PO (PR as parent) with a horizontal stepper:
  1. Permintaan
  2. Pesanan
  3. Uang muka (otorisasi → bukti)
  4. Penerimaan
  5. Pemeriksaan
  6. Mutu
  7. Stok ditahan (penilaian → pelepasan)
  8. Dokumen & pencocokan
  9. Utang
  10. Pembayaran / Klaim / Pengembalian uang muka / Verifikasi debit
  Each step shows: status chip (Indonesian), qty progress (e.g., "Diterima 80/100, lolos mutu 75, ditahan 5"), amount progress (DP terpakai / sisa), who is maker / who is checker, and the date. Skipped steps (e.g., no DP) are greyed "Tidak berlaku".
- **Body.** Below the stepper, an SAP-style document tree or table of every document in the case: nomor, jenis, status, nilai, tanggal, tautan. Add an audit timeline of approvals and evidence uploads. Line-level drill-down (per item, as in SAP's Process Flow per PO item) is needed because partial receipts and partial quality acceptance diverge by line.
- **Existing pages.** Keep the 15 role pages as work queues. Every row and every document links back to the one case page, the way Odoo smart buttons with counts link back. Each queue page can show a compact mini-stepper per row.
- **Next action.** A "Langkah berikutnya" panel on the case page (Jurnal-style action buttons) should show only the actions the current user's role can take at the current step. This preserves maker/checker separation.

### Gaps
- The SAP Fiori help page for the Process Flow control could not be fetched (empty response). Node visuals (lanes, status colours) are described from secondary sources only.
- No Indonesian ERP was found that offers a single cross-stage timeline page. Accurate's tab and Jurnal's action buttons are per-document, not per-case.
