# Indonesian business document numbering and formatting conventions for a B2B back office (Unit Toko)

Scope: human-facing numbers and formats for Unit Toko, an Indonesian koperasi shop that sells supplies to BNI branches. UI language is Bahasa Indonesia. Research as of 21 October 2026. Every question below separates **LEGAL** (set by statute or regulation) from **CUSTOM** (common practice) where that applies. Examples marked "illustrative" were made up for this note.

---

## 1. Marketplace order and invoice numbers (Shopee, Tokopedia, Bukalapak, Blibli)

### Takeaway
Tokopedia's buyer invoice number has four parts: `INV/{YYYYMMDD}/MPL/{10-digit id}`. The date is visible in the number and the id is globally unique. None of the marketplaces publishes an official spec for these numbers. Shopee's format could not be confirmed, and no sources were found for Bukalapak or Blibli. All marketplace formats are CUSTOM. No law requires any of them.

### Cited Findings
- A real Tokopedia invoice shows the number `INV/20230720/MPL/3354884307` with purchase date 20 July 2023. The middle segment is the transaction date (YYYYMMDD) and the last segment is a 10-digit numeric id. — [Scribd copy of a Tokopedia invoice](https://www.scribd.com/document/691768676/Invoice-Tokopedia)
- No source explains what "MPL" means. Reading it as "marketplace" is unconfirmed. — [Scribd copy of a Tokopedia invoice](https://www.scribd.com/document/691768676/Invoice-Tokopedia)
- In April 2024 Tokopedia's partner API changed the invoice field of GetSingleOrder. It used to return a PDF URL that needed a login. It now returns a Tokopedia page URL that is valid for only 1 hour, so partners must request it again. The number format itself did not change. — [Tokopedia/TikTok Shop developer announcement](https://developer.tokopedia.com/news/invoice-format-5-april)
- Shopee: BigSeller, an aggregator, says the Shopee order number matches the one in Seller Center. It does not describe the format. — [BigSeller help](https://help.bigseller.pro/en_US/detail/2218)
- Shopee: sources that are not official disagree. One blog says order numbers are numeric (9–10 digits). Another says they are a country code followed by digits (e.g., "MY1234567890"). Neither matches the user's example `250101ABCDEF12`. — [imagetotable.ai Shopee screenshot guide](https://imagetotable.ai/blog/shopee-order-screenshot)
- A general Indonesian invoice guide gives the "typical" pattern as `INV/[TAHUN]/[BULAN]/[NOMOR URUT]`, e.g., `INV/2026/06/001`, and says the number must be "unik dan berurutan" for the audit trail. — [OnlinePajak: Cara Membuat Invoice yang Benar](https://www.online-pajak.com/bisnis/cara-membuat-invoice-yang-benar/)

### Inferences
- Marketplace numbers share three design traits. A **document-type prefix** says what the number is (INV). A **date or period segment** lets staff place the document in time without opening it. A **long opaque id** makes the number globally unique across millions of sellers. Unit Toko has one issuer and low volume, so it needs the first two traits and only a **short per-period counter** instead of a 10-digit id.
- Indonesian buyers and BNI branch staff already know the `INV/...` shape from Tokopedia. That supports keeping "INV" as the invoice prefix even in an Indonesian-language UI.
- Tokopedia uses slashes. They are fine in print but a problem in file names and URLs. A Windows file name cannot contain `/`, and in a URL path the slash must be encoded. For a system-generated number, hyphens are safer (see §7).

### Gaps
- No official Shopee Open Platform documentation of the `order_sn` structure was found. The shape the user described (YYMMDD followed by about 8 alphanumerics, e.g., `250101ABCDEF12`) is plausible but **unverified**. Do not parse dates out of it.
- No sources were found for Bukalapak or Blibli order or invoice number formats. Both remain open questions.
- No marketplace publishes why it chose its format. The design reasoning above is inference.

---

## 2. Internal document numbering customs (faktur penjualan, PO, surat jalan, BAST, kuitansi, nota retur, surat pesanan) and accounting-software auto-numbering

### Takeaway
Indonesian office documents conventionally use slash-separated numbers in the order `{urut}/{kode surat}/{kode instansi}/{bulan Romawi}/{tahun}`, e.g., `001/INV/UT/X/2026`. The counter usually restarts at 1 every year. This is CUSTOM, inherited from letter (surat) numbering. No law fixes the format of commercial invoices, POs, surat jalan, BAST, kuitansi or nota retur. Accounting software lets each user set a prefix, the counter and a period reset per document type.

### Cited Findings
- A formal letter number (nomor surat) usually has these parts: kode surat, nomor urut, nama/kode lembaga, **bulan dalam angka Romawi**, and tahun berjalan, separated by slashes. — [Gramedia: Cara Membuat Nomor Surat](https://www.gramedia.com/literasi/cara-membuat-nomor-surat/)
- The counter is counted within a calendar year and **restarts at 1 at the start of the year**. The month is written in Roman numerals (e.g., IX = September) and the year in ordinary digits. — [Gramedia: Cara Membuat Nomor Surat](https://www.gramedia.com/literasi/cara-membuat-nomor-surat/); [detikBali: cara membuat nomor surat](https://www.detik.com/bali/berita/d-7489660/cara-membuat-nomor-surat-yang-benar-dan-efektif-lengkap-fungsi-dan-contoh)
- Simpler variants are also used: `002/2025` (counter/year), `001/03/2025` (counter/Arabic month/year), and `001/HRD/I/2024` (counter/department code/Roman month/year). Each organisation sets its own format. — [Popbela: cara membuat nomor surat](https://www.popbela.com/career/working-life/cara-membuat-nomor-surat-00-rxg23-7ppbg1); [Deepublish: cara membuat nomor surat](https://penerbitdeepublish.com/panduan-menulis/cara-membuat-nomor-surat/)
- An invoice has 8 required parts: a unique, sequential number (e.g., `INV/2026/06/001`); issue date; due date (typically 7, 14 or 30 days); seller data (including NPWP if the seller is a PKP); buyer data (including the buyer's NPWP in B2B); line items (description, quantity, unit, unit price); PPN (required if the seller is a PKP, 11% for non-luxury goods); and the total. This is guidance from a tax-software vendor, not a regulation. — [OnlinePajak](https://www.online-pajak.com/bisnis/cara-membuat-invoice-yang-benar/)
- Accurate Online (release v1.0.0, July 2022) added an option to switch numbering off and a setting for automatic numbering of purchase invoice numbers in the Supplier menu. This shows that numbering is configured separately for each module. — [Accurate release notes](https://accurate.id/release-notes-aol/1011899/)
- MASERP, an Indonesian ERP, builds number formats from up to four column types: text, nomor urut (counter), bulan (month), and tahun (year). The month is filled in automatically from the system date. This is a typical design for Indonesian business software. — [MASERP help: cara setting nomor otomatis](https://intercom.help/maserp/id/articles/5396310-cara-setting-nomor-otomatis)
- General accounting-software pattern: a yearly reset needs a year placeholder in the prefix or suffix. Otherwise numbers would repeat after the reset. — [Chargebee invoice numbering](https://chargebee.com/docs/invoice-numbering.html); [Odoo: reset invoice number per financial year or month](https://odoobooks.readthedocs.io/en/12.0/accounting/invoice/invoice_numbers_by_financial_year.html)

### Inferences
- **Customary forms for each document**, as staff would recognise them. These are illustrative, using the counter/code/org/Roman-month/year style:
  - Faktur penjualan / invoice: `0042/INV/UT/X/2026`
  - Surat pesanan (Unit Toko's order to a supplier): `0007/SP/UT/X/2026`
  - Purchase order *from* a BNI branch: this is the **buyer's** number. Store it verbatim as "No. PO Pembeli" and never renumber it.
  - Surat jalan (delivery note): `0031/SJ/UT/X/2026`
  - BAST (Berita Acara Serah Terima): `0005/BAST/UT/X/2026`
  - Kuitansi (receipt): `0018/KWT/UT/X/2026`
  - Nota retur: `0002/RTR/UT/X/2026`
- Roman-numeral months read as "official" on paper. They are poor for software: `X` < `IX` sorts wrong as text, users cannot type them quickly, and parsing them adds code. Use Arabic `YYMM` in the stored canonical number. If the koperasi's letter conventions need the Roman form, show it only in the printed "Nomor:" line of letter-style documents (BAST, surat jalan). See §7.
- Assign the number when the document is **issued**, e.g., when a draft is submitted as an order or an invoice is posted. Do not assign it when a draft is created. Otherwise abandoned drafts leave holes in the sequence that auditors will ask about.
- Cancelled documents should keep their number and be shown with the status "Batal". The number must never be reused or deleted. This keeps the "unik dan berurutan" property that auditors expect.

### Gaps
- No official help pages could be retrieved for **Jurnal by Mekari, Kledo or Paper.id** auto-numbering: available tokens, reset options, default formats. Their behaviour is unverified. Known only from general product knowledge: they offer per-transaction-type prefixes with auto-increment, and Jurnal's default invoice numbers look like `10001`. That needs checking in the apps.
- No specific regulation for **koperasi** document numbering was found.
- No source was found that says whether BNI branches' procurement or accounts-payable process requires a particular vendor invoice format. Ask the pilot branches.

---

## 3. Legally regulated numbers: Faktur Pajak (Coretax), NPWP/NIK, NITKU, e-Meterai; gap rules

### Takeaway
These identifiers are issued by DJP or Peruri. Unit Toko should **store and display them exactly as issued and never generate them**. Since PER-11/PJ/2025 the Faktur Pajak number is 17 digits: transaction code (2) + status code (2) + year (2) + DJP sequence (11). DJP assigns it automatically when the e-Faktur is approved. The NPWP is 16 digits (NIK for resident individuals). NITKU is 22 digits (NPWP + 6). An e-Meterai carries a 22-digit serial. DJP states there is no sanction for out-of-order Faktur Pajak serials. No law requiring gap-free numbering of ordinary commercial invoices was found.

### Cited Findings
- **LEGAL, NSFP structure:** PER-11/PJ/2025 Pasal 37(1): "Kode dan nomor seri faktur pajak terdiri atas 17 digit, yaitu: a. 2 digit kode transaksi; b. 2 digit kode status; dan c. 13 digit nomor seri faktur pajak yang diberikan oleh Direktorat Jenderal Pajak." The previous format had 16 digits. — [IKPI](https://ikpi.or.id/format-baru-nsfp-era-coretax-kini-17-digit-dan-diberikan-otomatis/); [DDTCNews](https://news.ddtc.co.id/berita/nasional/1811127/per-11pj2025-pertegas-ketentuan-kode-dan-nomor-seri-faktur-pajak)
- The 13-digit serial is the **year the e-Faktur was created (2 digits)** followed by an **11-digit sequence set by the DJP system**. The NSFP is now assigned **automatically when the e-Faktur is uploaded through Coretax and approved by DJP**. — [IKPI](https://ikpi.or.id/format-baru-nsfp-era-coretax-kini-17-digit-dan-diberikan-otomatis/); [DDTCNews](https://news.ddtc.co.id/berita/nasional/1811127/per-11pj2025-pertegas-ketentuan-kode-dan-nomor-seri-faktur-pajak)
- Klikpajak writes the format as `AA.BB.CC.XXXXXXXXX`: AA transaction code, BB status (normal or pengganti), CC year, then the 11-digit serial. — [Klikpajak](https://klikpajak.id/blog/cara-mengatasi-nomor-faktur-pajak-tidak-urut-dan-ketentuannya/)
  - **Conflict:** OnlinePajak gives a different split: 2 transaction + 1 status + 2 branch + 4 year-month + 8 sequence. This does not match the text of the regulation as quoted by IKPI and DDTC. Treat the regulation text as authoritative. — [OnlinePajak kode faktur](https://www.online-pajak.com/kode-faktur-pajak)
- Transaction code examples: **01** = normal supply where the PKP collects PPN itself (including luxury goods). **02** = supply to a government-agency PPN collector. **04** = supply using the "DPP nilai lain" (11/12 × price) under Article 8A of the VAT Law. — [DDTCNews: hierarki kode faktur](https://news.ddtc.co.id/berita/nasional/1808116/ramai-bahas-kode-faktur-simak-lagi-hierarki-penggunaannya)
- **Gaps in NSFP:** DJP states there is **no administrative sanction** when a PKP uses Faktur Pajak serial numbers out of order, provided they are valid and used correctly. Unused serials should be deleted to avoid technical errors in e-Faktur or Coretax. That is a technical requirement, not a tax penalty. — [Klikpajak](https://klikpajak.id/blog/cara-mengatasi-nomor-faktur-pajak-tidak-urut-dan-ketentuannya/)
- **LEGAL, NPWP/NIK:** PMK 136/2023 amended PMK 112/2022 and moved full implementation of NIK-as-NPWP, the 16-digit NPWP and NITKU to **1 July 2024**. Rollout followed Coretax readiness. — [DDTCNews: NPWP cabang bakal dihapus](https://news.ddtc.co.id/npwp-cabang-bakal-dihapus-djp-gantinya-bukan-npwp-16-digit-1796580); [DDTCNews: beda NIK, NPWP 16 digit, NITKU](https://news.ddtc.co.id/literasi/kamus/1803684/apa-beda-nik-sebagai-npwp-npwp-16-digit-dan-nitku)
- **LEGAL, NITKU:** NITKU has 22 digits: the 16-digit NPWP or NIK followed by a 6-digit sequence number for the place of business. The format is set in the annex to PER-6/PJ/2024. DJP issues it automatically. Under Pasal 16 PMK 81/2024, taxpayers must report each place of business within 1 month to get a NITKU. NITKU replaces the branch NPWP. — [Ortax](https://ortax.org/penggunaan-nitku-sebagai-pengganti-npwp-cabang); [DDTCNews: NPWP cabang diganti NITKU](https://news.ddtc.co.id/berita/nasional/1796814/npwp-cabang-diganti-nitku-begini-cara-ceknya)
- NITKU contains digits only (search-result summary of DJP guidance). Head offices also get a NITKU. — [DDTCNews: pusat juga dapat NITKU](https://news.ddtc.co.id/berita/nasional/1803709/tak-hanya-cabang-wajib-pajak-pusat-juga-bakal-dapat-nitku)
- **LEGAL, Bea Meterai:** UU 10/2020 took effect 1 January 2021. A document stating receipt of money, or acknowledging that a debt has been fully or partly paid, with a **nominal value above Rp5.000.000** is subject to a flat duty of **Rp10.000** (Pasal 3(2)(g); Pasal 5). The threshold and rate may be changed by Government Regulation (Pasal 6). — [UU 10/2020 (JDIH Setkab)](https://jdih.setkab.go.id/PUUdoc/176264/UU_Nomor_10_Tahun_2020.pdf); [DJP article](https://pajak.go.id/en/node/49921)
- DJP's position: a nota, faktur penjualan or invoice owes bea meterai **if it is used as proof of receipt of money** above the threshold. Documents about the movement of goods, such as proof of shipment or receipt of goods, are **not** subject (Pasal 7 huruf a). — [DJP: Bea Meterai atas Bukti/Nota Pembelian](https://pajak.go.id/en/node/49921)
- An **e-Meterai** carries a unique **22-digit serial number** generated by the electronic meterai system. Peruri produces it under PP 86/2021. Authenticity is verified using that serial on the official verification site. Old Rp3.000 and Rp6.000 meterai stopped being valid for new documents on 1 January 2021. — [Bisnis.com](https://ekonomi.bisnis.com/read/20211004/9/1450103/resmi-diluncurkan-ini-kelebihan-meterai-elektronik-atau-e-meterai); [Antara](https://m.antaranews.com/amp/berita/4352007/apa-itu-e-meterai-dan-manfaatnya); [OnlinePajak: meterai 10000](https://www.online-pajak.com/tentang-pajak/meterai-10000/)
- A missing meterai does not make an agreement invalid. It is a tax obligation that can be settled later through "pemeteraian kemudian", with an administrative sanction. — [OnlinePajak: meterai 10000](https://www.online-pajak.com/tentang-pajak/meterai-10000/)

### Inferences
- **For Unit Toko:** keep the internal invoice number (`INV-...`) separate from the Faktur Pajak number. The Faktur Pajak number exists only if Unit Toko is a PKP, and Coretax assigns it after approval. Example NSFP (illustrative): `04.00.26.00000012345`, stored as the 17-digit string `04002600000012345`. Store it as a **string** so leading zeros survive. Display the grouping DJP uses. Never compute it.
- Store NPWP, NIK and NITKU as digit strings (16 or 22 characters) and validate only the length and digits-only rule. For readability you can group them in blocks of 4 on screen, e.g., `0123 4567 8901 2345`. The copied value must be digits only.
- Kuitansi or invoices marked "LUNAS" above Rp5.000.000 sent to BNI branches need an e-Meterai or meterai tempel. The UI could warn when a kuitansi total is over Rp5.000.000. A surat jalan or BAST for goods does not need one.
- No law was found that requires gap-free numbering of **commercial** invoices or internal documents. Gap-free, sequential numbering is audit best practice (CUSTOM). If DJP tolerates gaps in the regulated NSFP, ordinary invoices certainly have no stricter legal rule. What auditors expect is that every gap is explained, for example by a voided document that still exists.

### Gaps
- The exact **status code values** in the BB position (e.g., `00` normal vs `01` pengganti) and the full list of 10 transaction codes in PER-11/PJ/2025 were not retrieved. In particular, it is unknown whether supplies to a BUMN like BNI use code 03 ("pemungut PPN selain instansi pemerintah").
- Unit Toko's PKP status was not verified. A koperasi below the PKP threshold does not issue Faktur Pajak at all.
- The internal structure of the 22-digit e-Meterai serial was not found in an official Peruri or DJP spec.
- The rule for converting an old 15-digit corporate NPWP to 16 digits (prefixing "0") and the `000000` suffix for a head-office NITKU were not confirmed from a primary source here.

---

## 4. SKU and item codes, GS1 barcodes

### Takeaway
Retail barcodes registered through GS1 Indonesia start with **899** (GTIN-13). GS1 reserves the prefixes **020–029** (and the 2xx range) for **Restricted Circulation Numbers**: internal, in-store or variable-measure codes that are not globally unique. These are a natural fit for barcodes Unit Toko prints itself. Human-readable SKUs are CUSTOM and no Indonesian standard governs them.

### Cited Findings
- 899 is the GS1 prefix for **Indonesia**. — [Wikipedia: List of GS1 country codes](https://en.wikipedia.org/wiki/List_of_GS1_country_codes); [GS1 Prefixes](https://www.gs1.org/prefixes)
- GS1 prefixes 020–029 are used to issue **Restricted Circulation Numbers within a geographic region** (MO-defined). They are for internal use, such as in-store or variable-weight items, and are not globally unique product identifiers. Wikipedia's list marks the 2xx ranges as "defined by GS1 Member Organisations". — [GS1 Prefixes](https://www.gs1.org/prefixes) (via search summary; direct fetch returned 403); [Wikipedia](https://en.wikipedia.org/wiki/List_of_GS1_country_codes)
- A GS1 prefix shows **which GS1 member organisation issued the number, not where the product was made**. — [GS1 Prefixes](https://www.gs1.org/prefixes)
- A GTIN-13 consists of the GS1 prefix, the company prefix, the item reference ("running no.") and a check digit. — [GS1 Malaysia: How to Assign GS1 Barcode Numbers (PDF)](https://www.gs1my.org/sites/default/files/Topic%202%20-%20GS1%20DIY%20Self-Learning%20-%20How%20to%20Assign%20GS1%20Barcode%20Numbers_1.pdf)

### Inferences
- **Recommended SKU (CUSTOM / best practice):** `{KATEGORI}-{JENIS}-{ATRIBUT}`, uppercase, hyphen-separated, 3–4 letter Indonesian abbreviations, at most about 16 characters. Illustrative examples:
  - `ATK-KRT-A4-80` (alat tulis kantor, kertas, A4, 80 gsm)
  - `ATK-PLP-HTM` (pulpen, hitam)
  - `KBR-TSU-250` (kebersihan, tisu, 250 lembar)
  - `MKN-AIR-600` (makanan/minuman, air mineral, 600 ml)
- Keep the SKU **meaningful but stable**. Do not encode price, supplier or stock location, because those change. Put the brand in a separate field.
- If a product carries a manufacturer barcode (GTIN, often `899…` for Indonesian-registered brands), store it as a separate `GTIN` field and scan by it. Never invent `899…` numbers: they belong to licensed GS1 Indonesia members.
- For items without a barcode that Unit Toko labels itself, generate **EAN-13 in the 20–29 restricted range** with a correct mod-10 check digit, e.g., `2000000000428` (illustrative, check digit computed by the standard GTIN algorithm). These will never collide with real GTINs at a scanner.
- Avoid the ambiguous characters `O/0` and `I/1/L` inside SKU attribute codes. Use digits for sizes and numbers and letters for words. See the Crockford findings in §7.

### Gaps
- GS1 Indonesia's own rules or fees for restricted-circulation allocations were not retrieved (gs1.org returned 403, and no GS1 Indonesia page was fetched).
- No Indonesian distribution-industry SKU standard was found. The SKU pattern above is common practice, not sourced.

---

## 5. Courier waybill (resi / AWB) formats

### Takeaway
Resi formats differ by courier and service and are not officially published. JNE: about 12–15 characters, numeric or alphanumeric. J&T: about 12 characters, possibly all digits. No reliable data was found for SiCepat, AnterAja or Pos Indonesia. The UI should treat a resi as an **opaque string**: store it exactly, show it in monospace with a copy button, and never reformat it.

### Cited Findings
- JNE's tracking reference goes by several names: resi JNE, nomor resi, AWB JNE, JNE Airwaybill Number. It appears on the receipt, barcode label, airwaybill, e-connote, marketplace order page or shipping email. "Some references contain only numbers, while others may include letters depending on how the shipment was created." — [Ship24: JNE tracking](https://www.ship24.com/couriers/jne-express-tracking)
- Ship24 advises entering the number exactly as shown. A missing digit or **an extra space** can stop tracking from working. — [Ship24: JNE tracking](https://www.ship24.com/couriers/jne-express-tracking)
- JNE resi length is reported as 15 characters, mixed letters and digits ([Biteship](https://biteship.com/en/tracking/jne)), or as 12–15 characters, sometimes starting with "JNE", e.g., `JNE1234567890` ([Parcel Monitor](https://www.parcelmonitor.com/id-cek-resi-jne/)). Both pages are aggregators and were seen only as search snippets, since direct fetches failed.
- J&T Express: AfterShip says the number has 12 digits, e.g., `600000000001`. 15TRACK says J&T numbers can mix letters and digits and that the format varies. A blog example starting with "JP" is not authoritative. — [AfterShip J&T](https://carriers.aftership.com/jtexpress); [15TRACK J&T](https://15track.com/jet-tracking)

### Inferences
- Store the resi **verbatim** as an uppercase, trimmed string. Validate only that it is not empty, uses an allowed character set (`A–Z 0–9`), and has a sensible length (about 8–30 characters). Do not enforce per-courier length rules, because formats change.
- Display the courier name next to the resi, e.g., "JNE · `CGK1234567890123`" (illustrative). Use a monospace font and a one-tap copy button whose clipboard value has **no spaces**. Add a link that opens the courier's tracking page.
- Unit Toko's own surat jalan number (`SJ-…`) is separate from the courier resi. Show both on the order. In-house deliveries to branches may have no resi at all.

### Gaps
- No official format or length was found for SiCepat, AnterAja or Pos Indonesia resi. No official JNE or J&T specification was found either.

---

## 6. Formatting rules: Rupiah, terbilang, dates, time zones, phone numbers, addresses, postal codes

### Takeaway
Under EYD V, Rupiah is written `Rp1.250.000`: no space and no period after "Rp", a dot as the thousands separator, and a comma for decimals (`,00`). Times use a dot (`pukul 14.05`). Dates are written `21 Oktober 2026` or `21/10/2026`. The legal time zones are WIB (UTC+7), WITA (UTC+8) and WIT (UTC+9). Postal codes have 5 digits and are hierarchical. Mobile numbers start with `08xx` locally and `+62 8xx` internationally. JavaScript's `Intl` for `id-ID` inserts a space after "Rp", which does not match EYD.

### Cited Findings
- **EYD V** (the official spelling standard published by Badan Bahasa): the **dot** separates thousands **only in numbers that express a quantity**. Example from the standard: "Anggaran lembaga itu mencapai **Rp225.000.000.000,00**". Note: no space after Rp, a dot every three digits, and a comma before the decimals. — [EYD V: Tanda Titik, rule 8](https://ejaan.kemendikdasmen.go.id/eyd/penggunaan-tanda-baca/tanda-titik/)
- EYD V: the dot is **not** used in numbers that do not express a quantity. Its examples are a year (1998), a page number, a **bank account number (0015645678)** and an **NIP (199701112015041002)**. — [EYD V: Tanda Titik, rule 9](https://ejaan.kemendikdasmen.go.id/eyd/penggunaan-tanda-baca/tanda-titik/)
- EYD V: the dot separates **hours, minutes and seconds**, e.g., "pukul 01.35.20". — [EYD V: Tanda Titik, rule 7](https://ejaan.kemendikdasmen.go.id/eyd/penggunaan-tanda-baca/tanda-titik/)
- Rupiah spelling (secondary sources citing PUEBI II.H.5, now EYD V): no period after the currency symbol (`Rp100.000`, not `Rp.100.000`). No space (`Rp100.000`, not `Rp 100.000`). Capital R with lowercase p. A dot for thousands (`Rp1.000`, not `Rp1,000`). A comma for decimals (`Rp10.000,00`, not `Rp10.000.00`). The ISO code is followed by a space (`IDR 5.000`). — [Deepublish](https://penerbitdeepublish.com/aturan-penulisan-rupiah/); [detikFinance](https://finance.detik.com/berita-ekonomi-bisnis/d-6430345/begini-cara-penulisan-rupiah-yang-benar-jangan-sampai-salah)
  - Note: the search tool's summary claimed "EYD V was replaced by PUEBI". That is backwards. The official Badan Bahasa site now publishes **EYD V** as the current standard, and EYD V superseded PUEBI (2015).
- **Local runtime test** (Node v24.3.0 with ICU, `Intl` `id-ID`, run 21 Oct 2026): currency gives `"Rp 1.250.000,00"` (with a space) or `"Rp 1.250.000"` when fraction digits are 0. Plain numbers give `1.250.000`. `dateStyle:'long'` gives `21 Oktober 2026`. `dateStyle:'full'` + `timeStyle:'short'` gives `Rabu, 21 Oktober 2026 pukul 14.05`. The 2-digit numeric date is `21/10/2026`. The short month is `21 Okt 2026`. Time is `14.05`. The time-zone name for Asia/Makassar is `15.05 WITA`. (Own test. No URL.)
- **LEGAL, time zones:** Indonesia has three zones: WIB (Waktu Indonesia Barat, UTC+07:00: Sumatra, Java, West and Central Kalimantan), WITA (Waktu Indonesia Tengah, UTC+08:00: South, East and North Kalimantan, Sulawesi, Bali, Nusa Tenggara) and WIT (Waktu Indonesia Timur, UTC+09:00: Maluku, Papua). They are set by Keputusan Presiden No. 41 Tahun 1987. — [Wikipedia: Time in Indonesia](https://en.wikipedia.org/wiki/Time_in_Indonesia); [Keppres 41/1987 (archived BAPPENAS PDF)](https://web.archive.org/web/20160420185920/http://dapp.bappenas.go.id/upload/pdf/KEPPRES_1987_041.pdf)
- **Postal codes:** kode pos have 5 digits. Digit 1 is the region, digits 2–3 the kabupaten or kota, digit 4 the kecamatan, and digit 5 the kelurahan or desa. Jakarta is an exception: digit 3 is the kecamatan, digit 4 the kelurahan, and digit 5 is always "0". — [Wikipedia: Postal codes in Indonesia](https://en.wikipedia.org/wiki/Postal_codes_in_Indonesia)
- **Phone numbers:** mobile numbers use `08xx` prefixes. Telkomsel uses 0811–0813, 0821–0823 and 0851–0853. Indosat/IM3 uses 0814–0816 and 0855–0858, and 3 uses 0895–0899. XL uses 0817–0819, 0859, 0877 and 0878. AXIS uses 0831–0833 and 0838. Smartfren uses 0881–0889. The country code is +62, and the leading 0 (the trunk prefix) is dropped in international format. — [Wikipedia: Telephone numbers in Indonesia](https://en.wikipedia.org/wiki/Telephone_numbers_in_Indonesia)

### Inferences
- **Rupiah in the UI:** `Rp1.250.000`. Build it with `Intl.NumberFormat('id-ID')` on the number and prepend the literal `Rp`, rather than using `style:'currency'`, or strip the space (likely U+00A0) that ICU inserts. Leave out `,00` in lists, cards and carts, since practical prices have no sen. Use `,00` on formal printed documents (invoice totals, kuitansi) to match the EYD example and accounting habit: `Rp1.250.000,00`. Negative amounts in retur or credit: `−Rp125.000` or `(Rp125.000)`. Choose one and use it consistently.
- **Terbilang** (CUSTOM, expected on kuitansi and common on invoices and BAST): write the amount in words, e.g., "Terbilang: *satu juta dua ratus lima puluh ribu rupiah*". Printed kuitansi often frame it with symbols like `# … #` to stop tampering. That is an observed custom with no source found. Generate it with a tested function: no "satu ratus" (use "seratus"), "seribu", "sebelas", and so on.
- **Dates:** in prose and on documents, `21 Oktober 2026`. On dense tables or mobile, `21 Okt 2026`, or `21/10/2026` where space is tight. Never use US `MM/DD`. Weekday when useful: `Rabu, 21 Oktober 2026`. Time: `14.05` with a dot, per EYD, plus the time zone, e.g., `21 Okt 2026, 14.05 WIB`. Unit Toko's BNI branches can be in any zone. Store UTC, render in the **viewer's or branch's zone**, and always print the zone label on documents.
- **Phone numbers:** store in E.164 (`+6281234567890`). Display in local style (`0812-3456-7890`), the form Indonesians write. Accept input as `08…`, `628…` or `+62 8…`, with spaces or hyphens, and normalise it. `wa.me` links need `6281234567890` with no plus sign.
- **Address structure** (customary order on KTP and letters; illustrative): `Jl. Contoh No. 12, RT 003/RW 005, Kel. Gambir, Kec. Gambir, Kota Jakarta Pusat, DKI Jakarta 10110`. Use structured fields for jalan + nomor, RT/RW, kelurahan or desa, kecamatan, kota or kabupaten, provinsi and kode pos (exactly 5 digits). Validate kode pos as `^\d{5}$` and store it as a string.
- **Never put thousands dots in identifiers** (EYD V rule 9). `Pesanan 5.201` would be wrong. Document numbers, NPWP, account numbers and resi carry no dots.

### Gaps
- No authoritative source (Badan Bahasa or a regulation) was found that *requires* terbilang on kuitansi. It is custom. Its legal weight, i.e., which wins when figures and words disagree, was not researched.
- No Bank Indonesia publication on Rupiah notation was retrieved. The EYD V example is the authoritative formatting source used here.
- No source was retrieved for the RT/RW notation convention (`RT 003/RW 005` vs `RT 03 RW 05`). Both forms appear in practice.

---

## 7. Display patterns for IDs in the UI; recommended scheme for Unit Toko

### Takeaway
Show one **canonical, human-assigned number per document**: type prefix + period + zero-padded counter, hyphen-separated (e.g., `PSN-2610-0042`). Keep database IDs out of the UI. Render numbers in monospace with tabular figures and a copy button. Accept case-insensitive search with or without separators. Use an ambiguity-free alphabet (Crockford Base32) for any non-sequential reference.

### Cited Findings
- Crockford Base32 uses 10 digits and 22 letters. It **excludes I, L, O and U**: I and L look like 1, O looks like 0, and U is excluded to avoid accidental obscenity. Decoding accepts upper and lower case and treats `i`/`l` as `1` and `o` as `0`. Encoding uses only upper case. **Hyphens can be inserted for readability and are ignored when decoding.** — [Crockford: Base 32](https://www.crockford.com/base32.html)
- Crockford Base32 also defines an optional **check symbol** (mod 37) for detecting transcription errors. — [Crockford: Base 32](https://www.crockford.com/base32.html)
- EYD V: no thousands dots in identifier-like numbers (account numbers, NIP). — [EYD V: Tanda Titik, rule 9](https://ejaan.kemendikdasmen.go.id/eyd/penggunaan-tanda-baca/tanda-titik/)
- Couriers and trackers warn that extra spaces or missing characters break a lookup. Copied identifiers must be exact. — [Ship24](https://www.ship24.com/couriers/jne-express-tracking)
- Invoice numbers should be unique and sequential for the audit trail. — [OnlinePajak](https://www.online-pajak.com/bisnis/cara-membuat-invoice-yang-benar/)

### Inferences

#### Recommended canonical format: `{JENIS}-{YYMM}-{NNNN}`
| Record (UI label) | Prefix | Example | Counter reset | Notes |
|---|---|---|---|---|
| Pesanan (customer order) | `PSN` | `PSN-2610-0042` | monthly | Assigned when the draft is **submitted**, not when it is created |
| Draf pesanan | — | (no number) | — | Show "Draf · BNI KCU Jakarta Kota · 21 Okt 2026, 14.05 WIB". If a reference is needed, `DRF-7K3Q9M` (random Crockford, **non-sequential**) |
| Faktur penjualan / Tagihan | `INV` | `INV-2610-0042` | monthly | Separate from the NSFP. Show the NSFP in its own field if PKP |
| Surat jalan | `SJ` | `SJ-2610-0031` | monthly | Courier resi stored separately |
| BAST | `BAST` | `BAST-2610-0005` | monthly | Optional printed letter form: `Nomor: 0005/BAST/UT/X/2026` |
| Kuitansi | `KWT` | `KWT-2610-0018` | monthly | Terbilang + meterai warning above Rp5.000.000 |
| Nota retur | `RTR` | `RTR-2610-0002` | monthly | Always references the original `INV-`/`PSN-` |
| Surat pesanan to a supplier | `SP` | `SP-2610-0007` | monthly | |
| PO from a BNI branch | — | as given, e.g., "No. PO Pembeli: 123/PBJ/KC-JKK/X/2026" (illustrative) | — | Buyer's number, verbatim |
| Toko / pelanggan (BNI branch outlet) | `TK` | `TK-005` | never | Lead with the **branch name**. Use BNI's own branch code if one is available |
| Barang (SKU) | category code | `ATK-KRT-A4-80` | — | GTIN in a separate field (§4) |

Why this format:
- **Unique:** the type prefix plus period plus counter is unique per series. Enforce it with a unique index on `(type, period, seq)` and allocate the counter atomically, e.g., a counters collection with an atomic increment inside the issuing transaction.
- **Sortable:** `YYMM` with a zero-padded counter sorts correctly as text within a type. Sorting in the UI should still use the issued timestamp.
- **Readable on phone or WhatsApp:** 13 characters, read aloud as "PSN, dua-enam-sepuluh, nol-nol-empat-dua". Small monthly counters are easier to say than a yearly counter in the thousands.
- **Print and file safe:** hyphens instead of slashes, so the number works in PDF file names (`INV-2610-0042.pdf`) and in URLs. Roman months are kept only for optional letter-style printing (CUSTOM, §2).
- **Audit:** no reuse, voids kept with the status "Batal", and the number assigned at issuance, so gaps are explainable. This matches the "unik dan berurutan" expectation, although no law mandates gap-free commercial numbering (§3).
- **Alternative:** yearly reset (`INV-2026-00042`) follows the letter custom that "nomor urut dimulai dari 1 pada awal tahun" ([Gramedia](https://www.gramedia.com/literasi/cara-membuat-nomor-surat/)). Choose yearly if auditors want one continuous series per fiscal year. Choose monthly for shorter numbers and month-end closing.
- **Organisation code:** leave "UT" out of the number. The kop surat or letterhead already identifies Unit Toko, and a shorter number is easier to dictate. If BNI's vendor-invoice register needs it, add the code to *every* document type (`UT-INV-2610-0042`) rather than mixing forms.

#### Display rules
- Label plus number: "No. Pesanan **PSN-2610-0042**". The type prefix makes the number self-describing when it is pasted into chat.
- Use a monospace font or `font-variant-numeric: tabular-nums`. The copy button copies the exact canonical string and shows a "Disalin" toast.
- Search: case-insensitive. Strip spaces, hyphens and slashes before matching, so `psn26100042` finds `PSN-2610-0042`. Also accept the counter alone within the current month, e.g., "42".
- Never show database IDs, including in URLs where possible. Use the human number or an opaque slug in routes.
- Check digits: not needed for type+period+counter numbers that staff look up by search. Add a Crockford mod-37 check symbol, or Luhn for digits only, only for codes that outsiders must **type** blind. Example: a payment reference (illustrative `DRF-7K3Q9M-X`).
- Random references use the Crockford alphabet only (no I, L, O, U), upper case, grouped with hyphens. Normalise `O→0` and `I/L→1` on input.
- Regulated numbers (NSFP, NPWP, NITKU, e-Meterai serial, resi) are shown exactly as issued, without dots per EYD rule 9 except where DJP's own display groups them, and copied without spaces.

### Gaps
- No usability study was found on Indonesian users reading or dictating document numbers, so monthly versus yearly reset is a judgement call.
- No source was found on how BNI branches record vendor document numbers, or on whether they need a vendor code inside the number. Confirm this with pilot branches.
