# Catalog & pricing clerk workspace: back-office UI/UX and workflow patterns (Indonesian e-commerce, B2B seller tools, ERP/PIM approval flows)

Scope: staff-side screens only (list page, product editor, bulk actions, price change review, publication/visibility, photo management) for Unit Toko's "Katalog toko", "Harga kemasan", "Katalog publik" and "Aturan buka kemasan" pages. Research date 21 Oct 2026. Sources dated where the page shows a date; undated or pre-2024 material is flagged "older".

---

## 1. How do Shopee Seller Centre, Tokopedia/TikTok Shop Seller Center, Jubelio, Ginee and Accurate structure the product list (status tabs, filters, search, columns, bulk edit) and the create/edit form?

### Takeaway
All the Indonesian marketplace tools work the same way. The product list is split into status tabs, each tab allows only certain actions, and every product that needs fixing shows a reason, a deadline and a fix button. One search box finds a product by name, parent SKU, variant code or ID. Bulk changes go out as a downloadable template: only some columns can be edited, the file is uploaded back, and failed rows come back as a file with a reason per row. The create form always has the same sections (product info → sales info with a variant/price/stock table → shipping → media), and it can save a draft that is not published.

### Cited Findings

**Shopee Seller Centre: Produk Saya (list page)**
- From Oct 2025, the "Diarsipkan" (archived) tab sits under the "Belum Ditampilkan" (not yet shown) tab. The product menu is split in two: "Pengaturan Produk" (Manajemen Merek, Panduan Ukuran, Sertifikasi Halal, Optimasi Produk) and "Pengaturan Massal" (Mass Upload, Mass Update, Edit Atribut). — [Shopee edu 20515, 05-10-2025](https://seller.shopee.co.id/edu/article/20515)
- One search box finds a product by Nama Produk, SKU Induk, Kode Variasi or ID Produk. Sellers no longer pick the search type first. — [Shopee edu 20515](https://seller.shopee.co.id/edu/article/20515)
- Low-stock products get a "Stok Menipis" tag in the Stok column. "Atur Pengingat Stok Menipis" sets a per-product threshold alert. — [Shopee edu 20515](https://seller.shopee.co.id/edu/article/20515); [Shopee edu 26134, 30-09-2025](https://seller.shopee.co.id/edu/article/26134)
- The Sep 2025 update added Kategori and Performa Produk filters, quick filters ("Berpotensi Harga Terbaik", "Perlu Diiklankan", "Stok Sedikit"), a "Urutkan Berdasarkan Terbaru" sort, a Performa column (Total Penjualan, Penjualan 30 Hari Terakhir, Jumlah Produk Dilihat) and an "Analisis Produk" column. That column shows performance tags and suggested fixes, with a "Riwayat Optimasi Produk 30 Hari Terakhir" log. — [Shopee edu 26134](https://seller.shopee.co.id/edu/article/26134)
- **Pending price changes are flagged in the list.** If a product has a running or upcoming promotion, its price is underlined. Hovering shows the promotion type, the promo price and the duration. — [Shopee edu 26134](https://seller.shopee.co.id/edu/article/26134)
- The Live tab has a sub-tab "Tinjau Rincian Produk" (To Review Listing Detail) for listing-quality issues. — [Shopee edu 26134](https://seller.shopee.co.id/edu/article/26134); [Shopee MY 20465, 26-10-2025](https://seller.shopee.com.my/edu/article/20465/where-to-optimise-listings)
- Older status set (2022, per Ginee): Live, Habis, Perlu Tindakan, Diarsipkan, Belum Diupload. Archived and not-uploaded products do not appear in search or on the shop page. "Tampilkan" and "Arsipkan" work on one product or many. — [Ginee insights, 2022 (older)](https://ginee.com/id/insights/cara-melihat-produk-saya-di-shopee/)
- **"Perlu Tindakan" / Pelanggaran Produk tab columns:**
  - Riwayat Pelanggaran: status changes over the last 3 months, with the action taken and the reason.
  - Status: Diblokir or Sedang Ditinjau Shopee.
  - Waktu diubah.
  - Batas waktu: the last date the seller can fix the product.
  - Tipe Pelanggaran: sortable.
  - Alasan pelanggaran.
  - Saran: the suggested fix.
  - Aksi: Ubah / Arsipkan / Hapus / Upload dokumen pendukung, up to 10 documents.

  A separate tab, "Dihapus oleh Shopee", only offers "Konfirmasi". — [Shopee edu 1741, 05-12-2025](https://seller.shopee.co.id/edu/article/1741/Memeriksa-dan-Memperbaiki-Produk-yang-Diblokir)
- Products that are Diturunkan or Diblokir can go live again once fixed before the deadline. Products that are Dihapus cannot. — [Shopee edu 15618, 02-09-2025](https://seller.shopee.co.id/edu/article/15618)
- After any upload or update, Shopee runs quality control (QC). A product may take up to 1x24 hours to appear. — [Shopee edu 5427, 24-07-2025](https://seller.shopee.co.id/edu/article/5427)

**Shopee: create/edit form ("Tambah Produk", 2026)**
- **Informasi Produk:** Foto Produk (cover required), Foto Produk Promosi (required, but only for selected sellers), Video, Nama, Deskripsi, Kategori. — [Shopee edu 16726, 2026-06-02](https://seller.shopee.co.id/edu/article/16726)
- **Informasi Penjualan:**
  - Variasi (optional, up to 2 variation types).
  - Harga (required) and Stok (required).
  - Min. Jumlah Pembelian and Maks. Jumlah Pembelian (selected sellers only). The maximum can be "Tanpa Batas", "Per Pesanan" or "Per Periode" with a start date.
  - Grosir (optional) and Panduan Ukuran.
  - Each variation option has Harga, Stok, an optional Kode Variasi and an optional image. "Terapkan Ke Semua" and "Edit Secara Massal" copy values across all options.

  — [Shopee edu 16726](https://seller.shopee.co.id/edu/article/16726)
- Shopee blocks a product automatically ("spam harga") if its price does not match the market. — [Shopee edu 16726](https://seller.shopee.co.id/edu/article/16726)
- Price, stock, minimum purchase and variations are locked while a product is in a promotion or the shop is in Mode Libur. — [Shopee edu 5431, 2026-03-16](https://seller.shopee.co.id/edu/article/5431)

**Shopee: Mass Update / Mass Upload**
- Template-based. Only the highlighted columns can be edited. The ID article says green and six template types; the SG article says yellow and five template types, so the versions conflict.
- Sellers can upload at most 3 files at the same time. A "Telah diproses" column shows how many rows went through, and "Coba Lagi" retries a failed upload.
- Failed rows download as a template with an "Alasan Gagal" column. Upload status is Berhasil, Berhasil Sebagian or Gagal. Template columns are labelled "Wajib" or "Wajib Bersyarat".

  — [Shopee edu 6953](https://seller.shopee.co.id/edu/article/6953); [Shopee SG 7029](https://seller.shopee.sg/edu/article/7029/about-mass-update)

**Tokopedia / TikTok Shop by Tokopedia: integrated Seller Center ("Manage Products", 2026)**
- Search by Seller SKU, category or price. Actions (edit, delete, copy, deactivate, change price and stock) work on one product or in batch. — [Tokopedia Seller University 10000818, 2026-08-27](https://seller-id.tokopedia.com/university/essay?knowledge_id=10000818&lang=en)
- Each status tab allows a fixed set of actions:

  | Tab | What it holds | Actions |
  | --- | --- | --- |
  | All | Every product | Edit, Deactivate, Copy, Change Price & Stock, Delete |
  | Live | Visible and buyable | Edit, Deactivate, Copy, Change Price & Stock, Delete |
  | Deactivated | Taken offline by the seller | Activate, Edit, Delete |
  | Suspended | Two kinds: "Failed" (breaks guidelines, can be resubmitted after fixing) and "Frozen" (illegal or counterfeit, cannot be modified) | — |
  | Draft | Filled in but not submitted | Edit, Delete |
  | Deleted | Removed by the seller | "Recover" sends it back to Deactivated |

  — [Tokopedia SU 10000818](https://seller-id.tokopedia.com/university/essay?knowledge_id=10000818&lang=en)
- **Batch Edit:**
  - Filter by status (Active/Inactive/Draft), category, and name, ID or Seller SKU.
  - Select products one by one, or all filtered products (up to 50,000).
  - Pick a template type: Sales information (price, quantity, SKU), Basic information, Shipping, Images, Properties, or All.
  - Each template file holds up to 5,000 products.
  - "Upload history" shows completed and failed counts, with "download failed entries" for the failures.

  — [Tokopedia SU 10000818](https://seller-id.tokopedia.com/university/essay?knowledge_id=10000818&lang=en)
- "Save as draft" does not send the product to review. "Publish" sends it to review, and it goes live once approved. A side preview shows how the product page will look. — [Tokopedia SU 10000818](https://seller-id.tokopedia.com/university/essay?knowledge_id=10000818&lang=en)
- Older (pre-merger) Tokopedia Daftar Produk:
  - Price and stock could be edited in place in the list (click the cell, type, press Enter).
  - Status switched with one click.
  - Stock 0 turned the product inactive automatically.

  — [Tokopedia Care (older)](https://www.tokopedia.com/help/article/cara-memaksimalkan-pengaturan-stok-produk-dengan-lebih-cepat)
- After shops are integrated, the old Tokopedia dashboard can still edit stock, price and status, but not attributes. — [Tokopedia SU 2405512621639440](https://seller-id.tokopedia.com/university/essay?knowledge_id=2405512621639440)
- TikTok Shop statuses, as shown by the BigSeller integrator:
  - Live, Sold Out, Delisted, Deleted.
  - Pending: the product can still be edited, but editing restarts QC.
  - Failed: shows the failure reason.
  - Frozen: cannot be edited.

  The "Content Quality" diagnosis links straight to the field that needs fixing. — [BigSeller help](https://help.bigseller.com/en_US/detailPage/introduction-to-product-page-tiktok-shop.7653)

**Omnichannel/ERP tools**
- **Ginee "Edit Master Produk Secara Massal"** (2021, older):
  - Up to 100 master products at once.
  - A pencil icon on a column header sets that field for all selected rows, and a trash icon clears it.
  - Editable fields: gambar, kategori master, harga default, gambar varian, ukuran paket, berat, status produk, jumlah pembelian min.
  - Name and description support add-prefix, add-suffix and find-and-replace.
  - Stock is edited separately in "Manajemen Stok", using stok tersedia = gudang − cadangan − terkunci − promosi.

  — [Ginee help](https://ginee.com/id/help/edit-master-produk-secara-massal/)
- Ginee "Tampilkan ke Toko": pick the target stores, fill the required channel fields (listed), up to 100 products at a time. A store that already has a product with the same name rejects it. — [Ginee help (2021, older)](https://ginee.com/id/help/publish-master-produk-secara-massal/)
- Jubelio's Katalog module covers product creation, SKUs, per-channel prices, publishing to channels, variants, bundles, monitoring channel issues, and archiving products no longer sold. — [Jubelio edu](https://edu.jubelio.com/kelas-online/courses/jo01-memulai-dengan-jubelio-omnichannel/pelajaran/pengenalan-konsep-jubelio-omnichannel-2/)
- Jubelio Enterprise:
  - Bulk CSV product edit validates data before import.
  - "Pantauan Perbedaan Informasi" flags fields that differ between Jubelio and a marketplace.
  - "Buku Harga" supports tiered pricing by quantity, customer type, channel, store and day/hour.

  — [Jubelio Enterprise](https://jubelio.com/enterprise/)

**General admin-table UX**
- NN/g: tables must support four tasks: find records by criteria, compare data, view/edit/add a single row, and act on records. For deep editing, use a non-modal side panel rather than a modal, because users look at neighbouring rows while editing. — [NN/g, Data Tables, 2022](https://www.nngroup.com/articles/data-tables/)
- NN/g bulk actions: offer Select All, use a contextual action bar, and give clear feedback with undo. — [NN/g video, 2025-03-17](https://www.nngroup.com/videos/bulk-actions-design-guidelines/)
- HashiCorp Helios:
  - Show the selected count against the total.
  - Distinguish three selection scopes: global (whole dataset), page (header checkbox) and row.
  - Confirm destructive bulk actions in a critical modal that states the count and the consequences.
  - Use a modal for bulk edits of five or fewer fields. For anything bigger, use a separate page.

  — [Helios table multi-select](https://helios.hashicorp.design/patterns/table-multi-select)
- BASIS design system: show a change preview before applying complex bulk edits. — [BASIS bulk editing](https://design.basis.com/patterns/bulk-editing)
- ELMO EDS: let users review the change in a confirmation modal before a bulk action runs, then show a success or failure toast. — [ELMO EDS tables](https://eds.docs.elmotalent.com.au/patterns/tables)

### Inferences
- Every marketplace splits the list by **lifecycle state, not by category**, and each tab has its own action set. For Unit Toko that maps to: Semua / Tayang / Draf / Menunggu persetujuan / Perlu tindakan / Diarsipkan. Category belongs in a filter, not a tab.
- The Shopee "Perlu Tindakan" row (reason + suggestion + deadline + fix button) suits Unit Toko's "rejected proposal" and "blocked photo" cases well. "Batas waktu" may not apply internally, but "Alasan" plus "Saran"/catatan peninjau does.
- The underlined price with hover detail is a cheap, proven way to show "a price change is pending or scheduled for this SKU" in the list without adding a column.
- The two-step template bulk flow (download only selected or filtered rows → edit allowed columns → upload → per-row errors file) is the norm Indonesian sellers already know. Unit Toko can adopt it for bulk price proposals and bulk package-rule edits.

### Gaps
- I could not confirm the complete, current (2026) set of Indonesian top-level tab labels on Shopee Produk Saya. Confirmed so far: Live (with "Tinjau Rincian Produk"), "Belum Ditampilkan" (contains Diarsipkan), "Perlu Tindakan"/Pelanggaran. Shopee edu pages are JS-rendered and the "Melihat dan Mengelola Daftar Produk" article (6937) returned no body.
- Indonesian UI labels for the merged Tokopedia/TikTok Seller Center statuses were only found in English (Live/Deactivated/Suspended/Draft/Deleted).
- No primary Accurate Online documentation for the Barang & Jasa list screen columns. help.accurate.id pages returned 301 redirects to the scraper.

---

## 2. How do these tools model variants vs packaging/unit of measure (satuan, isi per kemasan, minimum pembelian/kelipatan) and SKU codes? How do B2B tools (Amazon Business, Alibaba/1688, Mbiz) show MOQ and tiered/contract pricing?

### Takeaway
Consumer marketplaces treat "pack of 24" as either a variation option or a separate listing, and only offer a minimum quantity per product plus up to 5 wholesale tiers (Shopee). They have no real unit-of-measure model. Real packaging hierarchies live in ERPs: Accurate's ordered satuan levels with conversion ratios, and Odoo's per-packaging price rules. B2B commerce platforms add the remaining piece: **per-SKU minimum, maximum and order increment** (Shopify B2B) and **customer-specific catalogs/price lists**. Each platform has a strict rule that higher tiers must be cheaper (Shopee, Amazon) or that a more specific rule wins (Odoo).

### Cited Findings

**Variants and SKU codes**
- Shopee allows up to 2 variation types (e.g. colour × size). Each option has Harga, Stok, an optional Kode Variasi and an optional image. The parent product has an "SKU Induk". — [Shopee edu 16726](https://seller.shopee.co.id/edu/article/16726); [Shopee edu 20515](https://seller.shopee.co.id/edu/article/20515)
- The Tokopedia/TikTok Batch Edit "Sales information" template edits price, quantity and SKU per variant. Search uses "Seller SKU". — [Tokopedia SU 10000818](https://seller-id.tokopedia.com/university/essay?knowledge_id=10000818&lang=en)
- Shopify B2B catalogs identify a product or variant by SKU, falling back to Handle plus option columns. Option1 Name/Value is required even for products without variants ("Title"/"Default Title"). — [Shopify Help: creating catalogs](https://help.shopify.com/en/manual/b2b/catalogs/creating-catalogs)
- Bundles: Ginee's Bundle Master SKU ties existing master SKUs together with quantities. Bundle stock is computed, not entered. The tied SKUs cannot be changed after creation; the bundle must be deleted and recreated. In Jubelio, a bundle has its own SKU and its stock equals that of its scarcest component. — [Ginee bundle (2021)](https://ginee.com/id/help/fitur-bundle-master-produk/); [Jubelio bundling, 2026-01-26](https://jubelio.com/solusi/bundling-produk/)

**Unit of measure / packaging**
- Accurate Online multi-satuan:
  - Up to 5 unit levels per item (e.g. PCS > Pak > Lusin > Dus > Palet).
  - Satuan #1 must be the smallest. Each further unit has a conversion ratio, e.g. 1 Lusin = 12 PCS.
  - The Penjualan/Pembelian tab has "Def. Hrg. Jual Satuan #1, #2, …" price fields, one per unit.
  - Choosing a unit on an invoice switches the price automatically.
  - Quantity-based price breaks inside one unit need "Syarat & Ketentuan Diskon"; multi-satuan does not do them.
  - Role permissions can stop cashiers editing the price.

  — [penjualanonline.id (Accurate reseller), 2026-06-06](https://penjualanonline.id/cara-mengatur-default-harga-jual-multi-satuan-di-accurate-online/). Secondary source; the official help URL 301-redirected.
- Odoo 20 pricelist rules have a Min Qty plus an unlabelled packaging selector (from the product's "Packagings"). That allows separate prices per packaging option, defaulting to the product's UoM. The selector only appears for product-level rules. — [Odoo 20 docs: Pricelists](https://www.odoo.com/documentation/20.0/applications/sales/sales/products_prices/prices/pricing.html)
- Odoo picks between rules in this order: most specific first (variant > product > subcategory > parent category > all), then larger min qty, then category sequence, then newest rule. — [Odoo 20 docs](https://www.odoo.com/documentation/20.0/applications/sales/sales/products_prices/prices/pricing.html)
- SAP Ariba catalog items carry Packaging Quantity, Quantity Intervals, Effective Date, Expiration Date and a Price Configuration block (start/end date, amount, currency, LowerBound quantity for price breaks). — [SAP Community blog, 2023](https://community.sap.com/t5/spend-management-blog-posts-by-members/ariba-catalog-solution/ba-p/13549568)
- Amazon's B2B APIs support "package hierarchies for single units, case packs, and pallets". — [Sell on Amazon: Amazon Business](https://sell.amazon.com/programs/amazon-business)

**Minimum order / increment**
- Shopee "Min. Jumlah Pembelian":
  - Applies across all variations of a product within one order, so the buyer can mix variants to reach it.
  - Defaults to 1 and cannot be 0.
  - If stock is below the minimum, the product cannot be bought.
  - It is locked while the product is in a promotion.
  - Shopee advises using Grosir instead of a large minimum.

  — [Shopee edu 13251, 2025-03-18](https://seller.shopee.co.id/edu/article/13251)
- Shopify B2B quantity rules:
  - Increment (default 1, must be a whole number of 1 or more), Minimum, and an optional Maximum.
  - Rules are **per variant**: variants cannot be combined to reach an increment or minimum.
  - Set in Markets > Catalogs > "Manage products and pricing" > Quantity rules column, through an "Edit quantity rules and volume pricing" dialog.
  - Shopify notes the theme must display the minimum on the product page.

  — [Shopify Help: quantity rules](https://help.shopify.com/en/manual/b2b/catalogs/quantity-pricing)
- Shopify catalog CSV columns: Quantity Increment, Minimum Order Quantity (both at least 1 to define a rule), Maximum Order Quantity (optional). Leaving all three blank removes the rule. — [Shopify Help: creating catalogs](https://help.shopify.com/en/manual/b2b/catalogs/creating-catalogs)

**Tiered / contract pricing**
- **Shopee Grosir:**
  - Each tier has Min. Pesanan, Maks. Pesanan and Harga Satuan, up to 5 tiers.
  - Each tier's unit price must be lower than the previous one.
  - All variations must share one price before wholesale can be set.
  - A light-blue "Grosir" label appears on the product page.
  - Wholesale prices hide temporarily while a discount runs (SG doc).

  — [Shopee edu 16726](https://seller.shopee.co.id/edu/article/16726); [Shopee SG 14209](https://seller.shopee.sg/edu/article/14209/what-is-the-wholesale-feature)
- **Amazon Business:**
  - The business price is a per-unit price shown only to registered business customers.
  - Quantity discounts are either "% off" or "Fixed Price" per unit, up to 5 thresholds.
  - Each tier must give a lower unit price; a tier that is not cheaper is removed automatically.
  - Business customers see a quantity-discount table on the product page.
  - Prices are set in Manage All Inventory > "Business Price" action panel. That panel also compares competitors and gives recommendations.
  - Bulk updates use a feed file; changes show in Seller Central immediately but may take up to 15 min to reach buyers.

  — [Sell on Amazon blog, 2025-04-15](https://sell.amazon.com/blog/amazon-b2b-prices); [Seller Central: business pricing rule](https://sellercentral.amazon.com/help/hub/reference/external/GD29LN5JBS94EL4L)
- **Shopify B2B:**
  - Catalogs are assigned to companies or company locations; if several catalogs apply, the **lowest price wins**.
  - Volume pricing allows up to 10 price breaks per product, applied per variant.
  - Once volume pricing is set, the price becomes fixed and catalog-wide adjustments no longer apply.
  - Direct company/location catalogs need the Plus plan.

  — [Shopify Help: catalogs](https://help.shopify.com/en/manual/b2b/catalogs); [quantity pricing](https://help.shopify.com/en/manual/b2b/catalogs/quantity-pricing); [plan features](https://help.shopify.com/en/manual/b2b/getting-started/plan-features)
- **Accurate "Kategori Penjualan"/Kategori Harga:**
  - Named price levels such as Harga Umum, Harga Agen or Harga Member. A level can also carry a single customer's name to give that customer its own price.
  - Assigned on the customer form (Pelanggan > tab Penjualan > Kategori Harga).
  - Filled through "Penyesuaian Harga/Diskon".

  — [FAC Institute, 2025-02-25](https://facinstitute.id/mengatur-level-harga-jual-barang-per-pelanggan-di-accurate-online/); [programakuntansi.net, 2021 (older)](https://programakuntansi.net/setting-level-harga-jual-barang-per-pelanggan-di-accurate-online/)
- Jubelio Buku Harga example: Retail Rp500.000, Shopee Rp485.000, Reseller Rp450.000 (min 10 pcs), Distributor Rp420.000 (min 50 pcs). A price book applies to all customers, to a category, or to a specific contact. — [Jubelio Buku Harga, 2026-03-03](https://jubelio.com/solusi/buku-harga-dalam-sistem-inventory-omnichannel/)
- **Alibaba:**
  - A listing has a quantity price ladder, e.g. 5–2999 / 3000–4999 / 5000–9999 / 10000+; the top tier is open-ended.
  - The search card shows only the min–max range.
  - MOQ is a number plus a unit the supplier types freely (meters, pieces, sets, bags), so it is not normalised.

  — [ReefAPI Alibaba docs, measured 2026-08-27 (third-party)](https://reefapi.com/docs/alibaba)
- Alibaba posting guidance: price can be a ladder or a range. Packaging details include form, size and units per container. — [AMZ123, 2025-01-16 (Chinese, secondary)](https://m.amz123.com/ask/bZR5QNwA)
- **Mbizmarket** (B2B/B2G):
  - Catalog cards show "Mulai dari Rp…" plus a warranty tag ("Tanpa Garansi" / "Garansi Distributor").
  - Buying runs cart → "Membuat Permintaan RFQ" → seller quotation → buyer PO → invoice. Payment is CBD or TOP, with tax settings (PKP, WAPU) per transaction.
  - The seller can adjust TOP, delivery estimate and taxes when answering the RFQ.

  — [Mbizmarket catalog search](https://www.mbizmarket.co.id/catalog/search); [Mbizmarket cara transaksi](https://www.mbizmarket.co.id/cara-transaksi); [ITB/Mbiz training PDF, 2024](https://eproc.itb.ac.id/wp-content/uploads/sites/78/2024/05/Mbizmarket-Alur-Transaksi.pdf)
- Mbizmarket (PT Brilliant Ecommerce Berjaya, Lippo affiliate) is an official partner of LKPP's Toko Daring. Its process includes a role-based online approval step. — [Mbizmarket S&K](https://www.mbizmarket.co.id/id/syarat-dan-ketentuan); [Mbiz Insight, 2017 (older)](https://insight.mbiz.co.id/2017/10/18/solusi-total-mbiz-co-id-e-commerce-e-procurement/)

### Inferences
- Unit Toko's model (family → SKU → one package definition with "isi", minimum and increment in packages) is closest to Shopify B2B quantity rules combined with an Accurate-style satuan ratio. Neither marketplace offers it.
- Following Shopify, the editor should state plainly whether the minimum and increment are **per SKU** (variants cannot be combined) or across the family (Shopee mixes variants). The two platforms differ on this, and buyers notice.
- Following Accurate, show the package as a readable hierarchy, smallest unit first: "1 Karton = 24 Botol". The "isi" should feed the base-unit price display (Rp per botol) used in review.
- If tiered prices are ever added, enforce the shared rule (higher tier = cheaper unit price; invalid tiers are rejected) when the proposal is created, not when it is reviewed. Shopee and Amazon both enforce this rule.
- Contract-price visibility ("prices only after sign-in") matches Amazon Business ("visible only to registered business customers") and Shopify B2B catalogs per company. Both treat price visibility as part of the catalog assignment, not as a field on the product.

### Gaps
- No public Mbiz **merchant-side** catalog management docs (product upload form, MOQ field, catalog curation/approval) were found. Seller help appears to sit behind login. MOQ display on Mbiz cards was not confirmed.
- No official Alibaba.com seller help page was retrieved. Ladder and MOQ details come from a third-party API doc and a Chinese aggregator.
- Jubelio's unit-of-measure / "satuan konversi" handling was not found in public docs.
- No Indonesian marketplace was found with a true packaging hierarchy (carton → inner → piece) in the seller form.

---

## 3. What are good patterns for price-change workflows with approval (history, effective dates, pending changes, old-vs-new diff, reason, reviewer queue) in ERP/B2B tools?

### Takeaway
Mature tools treat a price change as a **dated document**, not a field edit. The document is a form or batch with a price category and a "Mulai Berlaku" date, lines of old and new values, and approval before it takes effect. Prices are stored as time-based records so history is kept. The best reviewer views compare the proposed version with the active one and filter on large price deltas (SAP Ariba "price change of more than X %"). They allow approving or rejecting the whole batch or individual lines, with a comment (Akeneo, Ariba), and they enforce that the same person cannot approve twice (Odoo "Exclusive Approval"; Accurate "minimal dua pengguna").

### Cited Findings

**Accurate Online**
- "Penyesuaian Harga Jual" form: Kategori Harga, Mulai Berlaku and Mata Uang in the header. Items are picked one by one or with "Ambil", which filters by category and lets the user tick items. Each line has Harga Jual Baru and Harga Minimum. — [account.co.id (Accurate reseller), 2024-03-20](https://account.co.id/membuat-penyesuaian-harga-jual/)
- Price import:
  1. Open Penjualan > Penyesuaian Harga/Diskon.
  2. Choose the Kategori Penjualan and the "Tanggal Mulai Berlaku".
  3. Choose "Impor Harga dari Excel" and download the official template.
  4. Upload the file and check the success/failed counts.
  5. Fix failed rows, then save the form (Simpan) so the changes are recorded.

  A "Tipe Penyesuaian" option switches between price and discount. — [Accurate help, 2026-09-28](https://help.accurate.id/product/accurate-online/fitur-aol/penjualan/penyesuaian-harga-diskon/impor-penyesuaian-harga-diskon)
- Accurate "Persetujuan" (approval) is switched on per transaction type in Pengaturan > Preferensi > tab Persetujuan. The Penjualan group includes **Penyesuaian Harga/Diskon**; the Pembelian group includes Harga Pemasok. — [Accurate help, 2025-08-06](https://help.accurate.id/product/accurate-online/fitur-aol/pengaturan/preferensi/cara-mengaktifkan-pengaturan-persetujuan/)
- "Penyetuju Transaksi" fields:
  - Tipe Transaksi.
  - Syarat min. Nilai: an approval threshold; left empty, every transaction needs approval.
  - Pembuatan Transaksi: which users may create it.
  - Disetujui Oleh: the approvers.
  - Dengan Syarat: one approver enough, at least two approvers, or all approvers must agree.

  — [akuntansionline.co.id, 2024-03-20](https://akuntansionline.co.id/fitur-penyetuju-transaksi-di-accurate-online/)
- Approval run:
  1. The operator saves the transaction as a draft.
  2. Approvers get an in-app and email notification.
  3. In the Persetujuan list they tick "Setuju" or "Tolak" per row and click "Proses". This works as a mass action.
  4. Rejected documents show status "Ditolak" and can be edited or deleted. Approved ones are saved and marked "Disetujui".

  — [triwibowo.id, 2024-08-20](https://triwibowo.id/fitur-persetujuan-accurate-online-tutorial-accurate-online/)

**Odoo**
- Pricelist rules carry Min Qty and a Validity Period (start/end dates). — [Odoo 19 docs](https://www.odoo.com/documentation/19.0/applications/sales/sales/products_prices/prices/pricing.html)
- Recommended practice: change prices by adding new dated pricelist lines rather than overwriting the product's sales price. Future-dated lines give stores lead time; history is kept; old rules are archived periodically. — [Odoo forum, 2026-01-04 (community opinion)](https://www.odoo.com/forum/help-1/best-practice-for-changing-product-sales-prices-in-odoo-18-295322)
- Odoo has no native report showing "old price → new price, variance %". Audit teams need a custom "Price Change Journal" or a list of proposed changes for sign-off. — [Odoo forum, 2026-01-04 (community opinion)](https://www.odoo.com/forum/help-1/best-practice-for-changing-product-sales-prices-in-odoo-18-295322)
- Odoo Studio approval rules:
  - An approval step is attached to a button, with Approvers or an Approver Group, optional "Users to Notify", an optional condition filter and a description.
  - Multiple steps can be ordered with "Approval Order".
  - "Exclusive Approval" stops a user who approved one step from approving another step on the same record.

  — [Odoo 19 docs: Approval rules](https://www.odoo.com/documentation/19.0/applications/studio/approval_rules.html)
- Third-party Odoo module "Pricelist Approvals": a pricelist is created or made active only after approval. — [Odoo Apps Store](https://apps.odoo.com/apps/modules/19.0/pricelist_approvals)

**SAP Ariba (catalog approval)**
- Compare Versions: choose the new version and either an activated older version or "All active items". "Separately List Items with" offers criteria such as "Price Change of More Than __ %". — [Ariba help (2004, older)](https://ep.tce.adquira.com/AribaBuyer/ariba/resource/en_US/help/content/admin/catalog/catalog_workspace/compare_versions.htm)
- Content Management System statuses: New → Parsed → (Fatal Error | Validation Error | Revalidating) → Validated → Completed → Generating Comparison → Activated / Deactivated.
  - Fields with errors are boxed in red and can be edited in place, then "Revalidate".
  - "Notify Valid Items" sends the content for approval.

  — [SAP Learning, 2024-10-04](https://learning.sap.com/learning-journeys/managing-sap-ariba-procurement/content-management-system_cf59562b-89d9-40e7-81bf-f10c497995ff); [Ariba support 186176](https://support.ariba.com/item/view/186176)
- Ariba approver view:
  - "View comparison" lists new, modified, unmodified, deleted and price-modified items.
  - Approve/Deny applies to the whole request; denying individual items is possible by setting a CSV "Cleanse" column to Yes/No.
  - Denied items are removed when the request is approved.

  — [SAP Community blog, 2023](https://community.sap.com/t5/spend-management-blog-posts-by-members/ariba-catalog-solution/ba-p/13549568)

**Akeneo PIM (product-data maker/checker)**
- A contributor edits a draft and clicks "Send for approval", with an optional comment. The product shows "Waiting for approval" and owners are notified. — [Akeneo: Drafts & proposals](https://help.akeneo.com/serenity-collaborative-work/serenity-workflow-proposals)
- Proposal grid fields: author, changes per attribute, date/time sent, status. Status values:
  - "In progress".
  - "Waiting for approval".
  - "Can be partially reviewed".
  - "Can't be reviewed".

  — [Akeneo: Review product proposals](https://help.akeneo.com/serenity-collaborative-work/review-product-proposals)
- Reviewers can approve or reject all of a proposal, or **each change one by one**, each with an optional comment and also as mass actions.
  - An approved change creates a new product version, visible in the product's History.
  - A rejected change goes back into the contributor's draft for rework.

  — [Akeneo: Review product proposals](https://help.akeneo.com/serenity-collaborative-work/review-product-proposals); [Akeneo v7](https://help.akeneo.com/v7-collaborative-work/v7-review-product-proposals)
- Akeneo builds its permission model on category ownership: editors who are not owners can only propose; owners approve. — [Akeneo blog, 2022 (older)](https://www.akeneo.com/blog/how-to-make-collaboration-flow-in-akeneo-pim/)

**Timing / scheduled-change cues in marketplaces**
- Shopee underlines a list price that has an upcoming or running promotion; hovering shows the type, price and duration. — [Shopee edu 26134](https://seller.shopee.co.id/edu/article/26134)
- Jubelio "harga coret" (struck-through promo prices):
  - Become active 5 minutes after creation.
  - A running promotion should not be edited; create a new one instead.
  - The price reverts when the promotion ends.

  — [Jubelio edu](https://edu.jubelio.com/kelas-online/courses/jubelio-online-training/pelajaran/cara-membuat-promosi-harga-coret/)
- Amazon: a business price shows in Seller Central immediately but may take up to 15 minutes to reach buyers. — [Sell on Amazon blog](https://sell.amazon.com/blog/amazon-b2b-prices)

### Inferences
- "Harga kemasan" should be built as an **Accurate-style penyesuaian document**: one proposal holding many SKU lines, with a header for catalog/price category, "Mulai berlaku" and reason. It is reviewed in an **Ariba-style comparison**: old vs new price per package, Δ Rp, Δ %, base-unit price, and a "show only changes > X %" filter. The checker should be able to **approve or reject per line** with a comment, as Akeneo allows.
- Unit Toko's "different, freshly verified person" rule is stricter than any tool found. Odoo's Exclusive Approval and Accurate's "minimal dua pengguna" are the nearest equivalents. The UI should **hide or disable "Setujui" for the proposer** and say why, not just fail on the server.
- Approved prices should become **new dated price rows** (Odoo practice), never overwrite the old one. That gives a per-SKU "Riwayat harga" tab that Odoo itself lacks and auditors explicitly ask for.
- Pending state must show up in the catalog list, as with Shopee's underlined price, so the clerk does not file a second proposal for the same SKU.

### Gaps
- No public screenshots or docs of Mbiz/Pengadaan.com catalog or price approval for merchants.
- The current SAP Ariba Compare Versions screen was documented only in a 2004-era help page and a 2023 community blog. Current UI labels may differ.
- No Indonesian marketplace exposes a price history or audit view to sellers that I could cite.

---

## 4. How do marketplaces show product quality/completeness ("kualitas produk", "Perlu dilengkapi"), photo requirements, and publication/visibility toggles?

### Takeaway
Completeness is shown as a **list of named problems with a fix link**, not a vague score. Examples: "Foto produk terlalu sedikit (min. 3)", "Atribut terlalu sedikit (min. 3)", "Nama 25–100 karakter", "Deskripsi ≥100 karakter". Users can filter by problem type and jump to the field. Photo rules have converged: square 1:1, about 500–600 px minimum, 2–5 MB, up to 9 images, first image = cover on a white background. Visibility is a small state machine with clear consequences: tayang, nonaktif/diarsipkan, menunggu QC, ditolak/diblokir. Publishing often goes through review before the product goes live.

### Cited Findings

**Quality/completeness**
- Shopee "Tinjau Rincian Produk" filters by "Kendala Informasi Produk":

  | Problem type | Shopee rule |
  | --- | --- |
  | Nilai/Kategori Salah | Wrong category, inaccurate weight |
  | Foto | Fewer than 3 photos |
  | Informasi kurang lengkap | No size chart or brand, no video, fewer than 3 attributes (fix in bulk with "Edit Atribut") |
  | Lainnya | Name outside 25–100 characters, description under 100 characters, non-standard variation names (fashion only) |

  — [Shopee edu 6915, 2026-08-20](https://seller.shopee.co.id/edu/article/6915)
- "Optimalkan Produk" shows missions for products in their first 90 days. Older products get an "Info Perlu Diperbaiki" checklist: Merek tidak ada, Atribut utama tidak lengkap, Atribut terlalu sedikit (min. 3), Panduan Ukuran tidak ada, non-standard variations, Kategori belum sesuai. — [Shopee edu 6915](https://seller.shopee.co.id/edu/article/6915)
- The "Perbaiki Produk Baru" tab covers products uploaded in the last 30 days, with three missions (Harga, Stok, Kualitas Informasi Produk). A "Perlu Ditingkatkan" link sits in the "Kualitas Informasi Produk" column, and a green check shows "Selesai". — [Shopee edu 20729](https://seller.shopee.co.id/edu/article/20729)
- Tokopedia "Analisis Wawasan Produk" (2022-era, older) scores 4 elements:
  - At least 3 photos, 300–2048 px (700×700 optimal), plain/white background.
  - Title of at least 40 characters.
  - Description of at least 260 characters, avoiding certain symbols.
  - A competitive price.

  Products can be edited directly from the analysis page, and the edit form shows information labels. — [Tokopedia seller-education PDF (via depok.go.id), older](https://umkmdkerens.depok.go.id/cms/api/Master/application/pdf/i1afu0ob2v6utt8k1712045048.pdf)
- Tokopedia "Skor Produk" is a 0–100 score with a colour indicator, built from content quality and sales performance. It was only shown to Power Merchant PRO and Official Store. — [Everpro blog, 2024-02-04 (secondary)](https://everpro.id/blog/marketplace-management/tokopedia-marketplace-management/ukuran-foto-produk-tokopedia/)
- TikTok Shop "Content Quality" diagnosis links straight to the field that needs editing. — [BigSeller help](https://help.bigseller.com/en_US/detailPage/introduction-to-product-page-tiktok-shop.7653)
- SAP Ariba validation rules mark each field mandatory or optional, and a failure can be set to be an error or a warning. — [SAP Learning](https://learning.sap.com/learning-journeys/managing-sap-ariba-procurement/content-management-system_cf59562b-89d9-40e7-81bf-f10c497995ff)

**Photo requirements**
- Shopee upload limits:
  - Up to 9 photos including the cover; the first photo becomes the cover.
  - Photos: max 2 MB, JPG/PNG/JPEG.
  - Video: max 30 MB, 10–60 s, max 1280×1280.
  - Shopee suggests 3:4 photos as a tip. Choosing 1:1 makes the first photo the Foto Produk Promosi.

  — [Shopee edu 16726](https://seller.shopee.co.id/edu/article/16726)
- Shopee cover-photo guide: minimum 500×500 px, 72 dpi, product filling at least 70% of the frame, white background preferred, no added text or graphics, front or 30° angle. — [Shopee edu 350 (undated)](https://seller.shopee.co.id/edu/article/350)
- Shopee Ads guidance: at least 1024×1024 px, 1:1, white background on the cover, no watermark. — [Shopee Iklan FAQ](https://iklan.shopee.co.id/learn/faq/572/5)
- TikTok Shop by Tokopedia:
  - Images at least 600×600 px, in colour, sharp, nothing covering the product.
  - Up to 9 square images.
  - Main image: solid background (white recommended), product filling at least 60% of the frame, not cropped.
  - 1 video, max 5 MB.

  — [Tokopedia SU 7065663894374161](https://seller-id.tokopedia.com/university/essay?knowledge_id=7065663894374161&lang=id-ID)
- TikTok Shop pre-upload checklist: max 9 images, 1:1, at least 600×600 px, max 5 MB per image, no watermark, logo or text. — [Tokopedia SU course, 2025-06-23](https://seller-id.tokopedia.com/university/course?content_id=7569069167347458&learning_id=6992591971944209)
- TikTok Seller Center (SG) lets sellers edit images in place: remove or change the background, adjust the ratio. — [TikTok Shop SG University](https://seller-sg.tiktok.com/university/essay?knowledge_id=4897339370735376&lang=en)
- TikTok enforced these image rules from 25 Aug 2023 and removed non-compliant images or videos. — [Radar Tasik, 2023 (news, older)](https://radartasik.id/2023/08/28/warning-tiktok-keluarkan-aturan-baru-untuk-penjual/)

**Publication/visibility**
- Shopee: "Tampilkan"/"Arsipkan" work on one product or in bulk. Archived products disappear from search and the shop. — [Ginee insights, 2022 (older)](https://ginee.com/id/insights/cara-melihat-produk-saya-di-shopee/)
- Shopee runs QC on every upload or update, with up to 1x24 hours before the product is visible. — [Shopee edu 5427](https://seller.shopee.co.id/edu/article/5427)
- Tokopedia/TikTok:
  - Draft = not submitted. Publish = goes into review, then live once approved.
  - When publishing, the seller picks the platform: Tokopedia, TikTok Shop, or both.
  - Delisting asks which platform to delist from.

  — [Tokopedia SU 10000818](https://seller-id.tokopedia.com/university/essay?knowledge_id=10000818&lang=en); [BigSeller ID publishing](https://help.bigseller.com/en_US/detailPage/publishing-products-on-tokopedia-tiktok-shop-in-indonesia.7660)
- Legacy Tokopedia: "Nonaktif" products cannot be found in search. Setting stock 0 + Nonaktif on a product with at least one past sale shows it in the shop as "Stok Habis", so buyers can wishlist it. — [Tokopedia Care (older)](https://www.tokopedia.com/help/article/cara-memaksimalkan-pengaturan-stok-produk-dengan-lebih-cepat)
- Shopify B2B catalog membership:
  - A "Published" TRUE/FALSE flag per product per catalog. It works at product level; variants cannot be included or excluded.
  - Catalog status changes can be scheduled through a "rollout" for market catalogs.

  — [Shopify Help: creating catalogs](https://help.shopify.com/en/manual/b2b/catalogs/creating-catalogs); [Shopify Help: catalogs](https://help.shopify.com/en/manual/b2b/catalogs)
- SAP Ariba restricts catalog visibility with a company-code field and "catalog constraints" (by supplier, catalog, kit or commodity code). — [SAP Community blog, 2023](https://community.sap.com/t5/spend-management-blog-posts-by-members/ariba-catalog-solution/ba-p/13549568)

### Inferences
- "Kelengkapan" in Katalog toko should be a **named checklist per SKU** with a link to each field, and the list should filter by checklist item, as Shopee's Kendala filter does. Unit Toko-relevant items:
  - Belum ada foto, or foto belum lolos pemindaian.
  - Kemasan belum diisi (isi / minimum / kelipatan).
  - Belum ada harga kemasan disetujui.
  - Atribut wajib kosong.
  - Belum masuk katalog mana pun.

  A 0–100 score adds little for a cooperative clerk.
- Photo rules for Unit Toko can borrow the marketplace consensus: 1:1, at least 600 px (TikTok minimum, above Shopee's 500), at most 2–5 MB, at most 9, first = cover, white background recommended. Each photo then carries its own status from Unit Toko's virus scan (e.g. "Dipindai…", "Lolos", "Ditolak – alasan"). No source covers scan-state UI, so that part is Unit Toko's own design.
- "Katalog publik" maps directly to Shopify's "Published per catalog" plus Tokopedia's publish-to-platform choice. A per-shop-catalog toggle that itself goes through maker/checker, and a "lihat sebagai tamu" preview (Tokopedia's live preview pane), would make the effect of the change clear before approval.

### Gaps
- No marketplace source describes UI for **virus or malware scanning** of uploaded images. No equivalent was found.
- Shopee's current Indonesian minimum resolution for listing photos (as opposed to the cover-photo guide's 500×500) was not restated in the 2026 upload article. The 1024 px figure is ads guidance only.
- Tokopedia "Skor Produk" criteria come from a secondary blog. Current Tokopedia/TikTok quality scoring in the merged Seller Center was not found in a primary source.

---

## 5. Which patterns fit a small cooperative staff with maker/checker controls, and which don't?

### Takeaway
Adopt the **structural** patterns:
- Status tabs with action-specific rows.
- A single search box covering name and SKU codes.
- Named completeness problems with fix links.
- Template-based bulk edit with per-row error files.
- Dated price documents with an old-vs-new comparison.
- Per-line approve/reject with comments.
- Per-SKU minimum/increment rules.
- Per-catalog publish flags.

Skip the **growth/marketing** layers (performance tags, ads nudges, AI optimisation, competitor or "market price" checks, gamified missions) and anything that edits a live price in one step (inline price cells). Those conflict with maker/checker or with contract pricing.

### Cited Findings
- Approval with a value threshold and multi-approver conditions ("Syarat min. Nilai"; one, two or all approvers) ships in an Indonesian SME accounting tool already used by koperasi-sized businesses. — [akuntansionline.co.id](https://akuntansionline.co.id/fitur-penyetuju-transaksi-di-accurate-online/); [Accurate help](https://help.accurate.id/product/accurate-online/fitur-aol/pengaturan/preferensi/cara-mengaktifkan-pengaturan-persetujuan/)
- Accurate's approver queue processes many rows at once by ticking Setuju/Tolak per row and clicking Proses. — [triwibowo.id](https://triwibowo.id/fitur-persetujuan-accurate-online-tutorial-accurate-online/)
- Odoo's "Exclusive Approval" separates approvers across steps on one record. — [Odoo 19 docs](https://www.odoo.com/documentation/19.0/applications/studio/approval_rules.html)
- In Akeneo, a rejected change returns to the contributor's draft rather than being lost, and each approval creates a version in History. — [Akeneo review proposals](https://help.akeneo.com/serenity-collaborative-work/review-product-proposals)
- Legacy Tokopedia's inline price-cell edit applied immediately on Enter. — [Tokopedia Care (older)](https://www.tokopedia.com/help/article/cara-memaksimalkan-pengaturan-stok-produk-dengan-lebih-cepat)
- Shopee's list adds marketing layers: performance tags ("Penjualan Teratas", "Perlu Diiklankan"), ad nudges and a "Berpotensi Harga Terbaik" programme. — [Shopee edu 26134](https://seller.shopee.co.id/edu/article/26134)
- Shopee and Amazon both check prices against the market or competitors: Shopee auto-blocks a "spam harga" price, and Amazon's Business Price panel shows competitor comparisons and recommendations. — [Shopee edu 16726](https://seller.shopee.co.id/edu/article/16726); [Sell on Amazon blog](https://sell.amazon.com/blog/amazon-b2b-prices)
- Tokopedia batch edit is built for up to 50,000 selected products and 5,000 per file. — [Tokopedia SU 10000818](https://seller-id.tokopedia.com/university/essay?knowledge_id=10000818&lang=en)
- NN/g recommends a non-modal side panel for editing a record from a table. — [NN/g Data Tables](https://www.nngroup.com/articles/data-tables/)
- Helios/BASIS recommend a change preview and a count-stating confirmation before bulk edits. — [Helios](https://helios.hashicorp.design/patterns/table-multi-select); [BASIS](https://design.basis.com/patterns/bulk-editing)
- Shopee locks price and quantity fields while a promotion runs. — [Shopee edu 5431](https://seller.shopee.co.id/edu/article/5431)
- Jubelio advises creating a new promotion rather than editing a running one. — [Jubelio edu](https://edu.jubelio.com/kelas-online/courses/jubelio-online-training/pelajaran/cara-membuat-promosi-harga-coret/)

### Inferences

#### Fits (adopt)
- **Status tabs + per-tab actions** (Shopee/Tokopedia). They give a small team a work queue: "Perlu tindakan" and "Menunggu persetujuan" are the clerk's and checker's inboxes.
- **One search box over name + kode SKU + kode keluarga** (Shopee 2025). The clerk usually has a code from a branch order.
- **Named "Perlu dilengkapi" checklist with fix links** (Shopee Kendala, TikTok diagnosis). This is cheap to build and teaches the data rules.
- **Accurate penyesuaian-style price proposal**: a batch with "Mulai berlaku", lines, a reason and Excel import with a success/failed preview. It mirrors what Indonesian bookkeeping staff already know from Accurate.
- **Ariba-style comparison for the checker**: Δ %, a "only > X %" filter, and new/changed/removed counts.
- **Akeneo-style per-line approve/reject with comment**. A rejected line returns to the maker's draft.
- **Odoo dated price rows → Riwayat harga** per SKU.
- **Disable "Setujui" for the proposer with an explanation.** This matches Odoo Exclusive Approval and Accurate's two-approver condition.
- **Shopify-style per-SKU Minimum / Kelipatan / (Maks)** with the base-unit hierarchy shown as text (Accurate).
- **Shopify-style "Published per catalog"** for Katalog publik, plus a guest preview.
- **Lock editing of a SKU's price while a proposal is pending.** Shopee locks price during promotions and Jubelio says not to edit a running promotion. In Unit Toko terms: one open proposal per SKU, with the pending badge in the list.
- **Template bulk with per-row error file** (Shopee/Tokopedia/Accurate). Use it for bulk price proposals and package rules, sized to Unit Toko's catalog (hundreds, not 50,000).
- **Non-modal side panel** for SKU editing from the list (NN/g), so the clerk can copy values from neighbouring SKUs.

#### Does not fit (skip or adapt)
- **Inline price editing that applies immediately** (legacy Tokopedia). It conflicts with maker/checker. Adapt it to "Usulkan harga" from the row, which adds the line to a draft proposal.
- **Market or competitor price checks / "spam harga" auto-block / price recommendations** (Shopee, Amazon). Unit Toko prices are contract prices for BNI branches, so the relevant check is Δ % against the current approved price, not the market.
- **Performance tags, ads nudges, AI optimisation, gamified missions and boosts** (Shopee). This is noise for a back office with no ad spend. Keep only a plain completeness checklist.
- **Two-axis variation matrix** (Shopee's 2 variation types). Unit Toko already models a family → SKUs with one package definition each. A colour × size matrix adds complexity the office/pantry catalog likely does not need.
- **Multi-channel publish / per-platform delist** (Ginee, Jubelio, Tokopedia+TikTok). Not applicable. Unit Toko's "channels" are its own shop/warehouse assortments, so reuse the pattern as "publish to assortments" only.
- **0–100 quality score** (Tokopedia Skor Produk). It is opaque. Prefer the checklist.
- **Very large batch limits (50,000 / 5,000 per file)**. Over-engineered here. The per-row error-file mechanism is what matters.
- **Wholesale tiers (Shopee Grosir / Amazon quantity discounts)**. Not needed for single contract prices per package today. If added later, enforce the "cheaper per higher tier" rule when the tier is entered.

#### Screen-by-screen pattern map (synthesised recommendations; labels are suggestions in Bahasa Indonesia)
1. **Katalog toko – daftar (list page)**
   - Tabs: Semua · Tayang · Draf · Menunggu persetujuan · Perlu tindakan · Diarsipkan.
   - One search box (nama / kode SKU / kode keluarga).
   - Filters: Kategori, Katalog/gudang, Kelengkapan ("Tanpa foto", "Foto belum lolos", "Tanpa harga disetujui", "Kemasan belum lengkap", "Belum di katalog mana pun").
   - Columns: foto sampul; nama keluarga + jumlah SKU; kode SKU; kemasan ("Karton isi 24 botol"); min/kelipatan; harga berlaku per kemasan (underlined with hover "Usulan Rp… mulai …, oleh …" when pending, as in Shopee 26134); katalog chips; kelengkapan; diperbarui (oleh/kapan).
   - "Perlu tindakan" rows show Alasan + catatan peninjau + Aksi (Ubah / Usulkan ulang), following Shopee's Pelanggaran tab.
2. **Product editor (family + SKU)**
   - Sections in Shopee order: Informasi produk → Kemasan & pembelian (per-SKU table: kode SKU, nama kemasan, isi, satuan dasar, min, kelipatan; "Terapkan ke semua") → Atribut → Foto → Katalog → Harga (read-only, with "Usulkan perubahan harga").
   - Open from the list in a side panel (NN/g).
   - Inline validation with Wajib / Wajib bersyarat markers (Shopee template labels), e.g. minimum must be a whole number of packages, and kelipatan of at least 1 (Shopify rule). Whether the minimum must be a multiple of kelipatan is Unit Toko's decision.
   - "Simpan draf" vs "Ajukan" (Tokopedia draft/publish).
3. **Bulk actions**
   - Contextual bar showing "n dipilih dari N", with select-all scope stated (Helios).
   - Actions: Usulkan harga (opens a proposal with these SKUs), Masukkan ke katalog…, Arsipkan.
   - Preview + count-stating confirmation (BASIS/Helios); toast with Batalkan where reversible (NN/g).
   - Excel route: unduh template (only selected/filtered SKUs; only highlighted columns editable) → unggah → "Berhasil / Berhasil sebagian / Gagal" + unduh baris gagal with "Alasan gagal" (Shopee/Tokopedia/Accurate).
4. **Harga kemasan – usulan & tinjauan (price change review)**
   - Proposal header: katalog/kategori harga, Mulai berlaku, Alasan. Lines: SKU, kemasan, harga lama, harga baru, Δ Rp, Δ %, harga per satuan dasar (Accurate penyesuaian).
   - Reviewer queue: tab "Menunggu persetujuan" with pengusul, tanggal diajukan, jumlah baris, Δ % terbesar.
   - Comparison view: filter "perubahan > X %" and counts baru/berubah/dihapus (Ariba).
   - Per-line Setujui/Tolak + catatan, and Setujui semua (Akeneo/Accurate). Setujui is disabled for the pengusul, with the reason shown (Odoo Exclusive Approval). Fresh re-verification happens before the final submit.
   - Each SKU gets a "Riwayat harga" tab of dated rows: harga, berlaku sejak, diusulkan oleh, disetujui oleh, alasan (Odoo practice).
5. **Katalog publik (publication/visibility)**
   - Per shop catalog: status (Publik / Hanya untuk pembeli masuk), number of products guests can see, and "Lihat sebagai tamu" preview (Tokopedia preview).
   - Changes go through the same proposal → review flow, showing the products that will become visible or hidden (Shopify "Published" per catalog).
6. **Foto (photo management)**
   - Grid with drag-to-reorder; first = sampul (Shopee/TikTok). Rules shown before upload: 1:1, ≥600 px, ≤2–5 MB, ≤9 photos, white background recommended.
   - Per-photo status chip from the virus scan (Dipindai / Lolos / Ditolak + alasan), with "Ganti" on rejected photos.
   - A photo that has not passed the scan counts as "Perlu dilengkapi" and should block the product from appearing in a public catalog (Unit Toko rule; no source pattern).
7. **Aturan buka kemasan (pack conversion rules)**
   - Show conversions as sentences in Accurate's smallest-first order ("1 Karton = 24 Botol").
   - Use the same draft/ajukan/tinjau flow if rules affect sellable quantities. No direct marketplace analogue was found; Accurate multi-satuan and Odoo packagings are the closest.

### Gaps
- No source evaluates maker/checker UX specifically for *small* teams. For example, nothing covers what happens when only two staff exist and one is absent (delegation or escalation). Accurate's "one / two / all approvers" and Odoo's approver groups are the only evidence.
- No usability data was found (e.g. NN/g or Baymard studies) on price-diff review screens specifically. The Ariba and Akeneo patterns are vendor documentation, not tested guidance.
- Baymard was not consulted; its published research focuses on consumer storefronts rather than seller back offices.
