# Finance & invoicing back office for an Indonesian koperasi selling to BNI: tax rules, document fields, Indonesian tool patterns, screen improvements

Scope: staff/back-office screens only. Current as of October 2026. Items marked **[2025 change]** changed in 2025. Items marked **[uncertain]** could not be confirmed from a primary or near-primary source. Research was cut short when web search hit its session limit, so a few vendor-UI details stay unverified (see Gaps).

---

## 1. What Indonesian tax rules shape a B2B invoice to a BUMN buyer in 2025–2026?

### Takeaway
For non-luxury goods, PPN is "12%" applied to a DPP nilai lain of 11/12 of the price (PMK 131/2024), so it works out to 11% of the price. The Faktur Pajak is made in Coretax under PER-11/PJ/2025. It has a 17-digit code+NSFP, gets its number automatically when DJP approves it, must be uploaded by the 20th of the next month, and needs the buyer's 22-digit NITKU when the buyer is identified by NPWP. BNI, as a BUMN, is a pemungut PPN under PMK 81/2024 Pasal 292, which revoked PMK 8/2021. For payments above Rp10 juta (PPN included), BNI collects the PPN itself and also collects PPh 22 at 1.5% of the price excluding PPN (PMK 51/2025), so the transfer Unit Toko receives is smaller than the invoice total.

### Cited Findings

**PPN rate and DPP nilai lain (PMK 131/2024) [2025 change]**
- PMK 131/2024 limits the full 12% rate to luxury goods (barang mewah, the PPnBM goods). For all other goods and services, PPN = 12% × DPP nilai lain, where DPP nilai lain is 11/12 of the selling price, import value or replacement value (Pasal 3), giving an effective 11% — [MUC Consulting](https://muc.co.id/id/article/resmi-berlaku-tarif-ppn-untuk-barang-mewah-12-non-barang-mewah-11); [Kontan](https://nasional.kontan.co.id/news/aturan-terbit-ppn-12-resmi-hanya-untuk-barang-mewah)
- Luxury goods had a one-month transition in January 2025 (12% × 11/12). From 1 February 2025 they pay 12% × the full price — [Pajak.com](https://www.pajak.com/pajak/ppn-12-persen-barang-mewah-berlaku-1-februari-2025-dirjen-pajak-ada-masa-transisi-bagi-perusahaan-retail/). Pajak.com also cites PER-01/PJ/2025 for a 3-month transition (1 Jan to 31 Mar 2025). This looks like a faktur-relaxation window rather than a rate rule. The two sources describe different scopes, so this is flagged rather than reconciled.
- PER-1/PJ/2025 (set and in force 3 January 2025) is the technical guide for faktur under PMK 131/2024. Luxury goods use kode transaksi 01. Non-luxury BKP/JKP with DPP nilai lain 11/12 use kode 04. Deliveries to an instansi pemerintah pemungut use kode 02, even when DPP nilai lain applies — [DDTCNews](https://news.ddtc.co.id/berita/nasional/1808116/ramai-bahas-kode-faktur-simak-lagi-hierarki-penggunaannya)
- Goods with their own special DPP or fixed-rate rules are excluded and follow their own PMK (for example LPG 3 kg, gold jewellery, used vehicles, crypto) — [IKPI](https://ikpi.or.id/?p=12994) / [MUC](https://muc.co.id/id/article/resmi-berlaku-tarif-ppn-untuk-barang-mewah-12-non-barang-mewah-11)

**When a koperasi must be PKP**
- The Rp4,8 miliar gross-turnover threshold comes from PMK 197/PMK.03/2013, in force since 1 January 2014. A business below it may still register as PKP voluntarily — [search summary of Online Pajak / izin.co.id](https://izin.co.id/blog/pkp-adalah/); also [DDTCNews](https://news.ddtc.co.id/berita/nasional/1809782/omzet-rp48-miliar-wajib-pajak-dikukuhkan-jadi-pkp-di-kpp-terdaftar)
- PMK 164/2023 Pasal 17 ayat (3): a business that passes Rp4,8 miliar must report for PKP registration by the end of that financial year. It does not have to report by the end of the following month, as the old rule required. It then collects PPN from the first tax period of the next financial year, unless it notifies an earlier start date — [DDTCNews](https://news.ddtc.co.id/berita/nasional/1813945/omzet-lebihi-rp48-m-pemungutan-ppn-dilaksanakan-tahun-berikutnya)
- A proposal to lower the threshold to Rp3,6 miliar was reported as not in force. Rp4,8 miliar still applies — [search summary, DDTC/pajak.go.id results](https://news.ddtc.co.id/berita/nasional/1809782/omzet-rp48-miliar-wajib-pajak-dikukuhkan-jadi-pkp-di-kpp-terdaftar)
- No special koperasi threshold was found. A koperasi is a badan and follows the same rule (see Gaps).

**Faktur Pajak in Coretax (PER-11/PJ/2025) [2025 change]**
- Coretax DJP replaced the old e-Faktur desktop app and e-Faktur VAT Refund. All faktur creation now runs in the Coretax e-Faktur module — [Pajakku](https://pajakku.com/artikel/per-11pj2025-pokok-perubahan-faktur-pajak-dalam-coretax)
- Pasal 37 ayat (1) PER-11/PJ/2025: the code + NSFP is **17 digits**: 2-digit kode transaksi + 2-digit kode status + 13-digit NSFP. The NSFP is the 2-digit year followed by an 11-digit sequence. The old format had 16 digits with a 1-digit status. The NSFP is assigned automatically when the e-Faktur is uploaded and DJP approves it. e-NoFa number requests no longer exist — [DDTCNews](https://news.ddtc.co.id/berita/nasional/1811127/per-11pj2025-pertegas-ketentuan-kode-dan-nomor-seri-faktur-pajak); [Pajakku](https://pajakku.com/artikel/per-11pj2025-pokok-perubahan-faktur-pajak-dalam-coretax)
- Kode transaksi under PER-11/PJ/2025:
  - 01: normal delivery, PKP collects the PPN.
  - 02: to an instansi pemerintah pemungut.
  - **03: to "pemungut PPN lainnya selain instansi pemerintah"** (e.g. BUMN), or to a buyer whose PPN a third party collects under Pasal 32A UU KUP.
  - 04: DPP nilai lain under Pasal 8A(1) UU PPN.
  - 05: besaran tertentu.
  - 06: tourist VAT refund.
  - 07: tidak dipungut/DTP.
  - 08: dibebaskan.
  - 09: aktiva Pasal 16D.
  - 10 (new): other deliveries where the PKP collects the PPN.
  - Sources: [DDTCNews](https://news.ddtc.co.id/berita/nasional/1811127/per-11pj2025-pertegas-ketentuan-kode-dan-nomor-seri-faktur-pajak); [Pajakku](https://pajakku.com/artikel/per-11pj2025-pokok-perubahan-faktur-pajak-dalam-coretax)
- When more than one code fits, DJP's priority order decides (Lampiran PER-03/PJ/2022 huruf B, the predecessor rule). Example: a DPP-nilai-lain delivery to a government pemungut uses 02, not 04 — [DDTCNews](https://news.ddtc.co.id/berita/nasional/1808116/ramai-bahas-kode-faktur-simak-lagi-hierarki-penggunaannya)
- **Upload deadline:** e-Faktur must be uploaded by the 20th of the month after the faktur date. The old deadline was the 15th — [Pajakku](https://pajakku.com/artikel/per-11pj2025-pokok-perubahan-faktur-pajak-dalam-coretax)
- **Seller/buyer TKU address:** filling the place-of-business (TKU) address can now be optional or mandatory. Before, it was mandatory only in facility areas — [Pajakku](https://pajakku.com/artikel/per-11pj2025-pokok-perubahan-faktur-pajak-dalam-coretax)
- **Wrong buyer identity cannot be fixed with a faktur pengganti.** The faktur must be cancelled and a new one issued. Kring Pajak confirmed this specifically for a wrong buyer NITKU, even though Pasal 48(1) otherwise allows a pengganti for wrong content — [DDTCNews (24 Jun 2025)](https://news.ddtc.co.id/berita/nasional/1811670/keliru-cantumkan-nitku-pembeli-tak-bisa-bikin-faktur-pajak-pengganti); [Pajakku](https://pajakku.com/artikel/per-11pj2025-pokok-perubahan-faktur-pajak-dalam-coretax)
- A faktur pengganti issued after a return or cancellation is now based on the net value. The retur/pembatalan is treated as not having occurred — [Pajakku](https://pajakku.com/artikel/per-11pj2025-pokok-perubahan-faktur-pajak-dalam-coretax)
- **Termin / down payments:** each termin payment needs its own faktur. For DP 1, tick "uang muka", fill the full unit price, tick DPP Nilai Lain (11/12), and enter the DP amount. Settlement fakturs reference the earlier ones (Kring Pajak, 13 Jul 2025) — [IKPI](https://ikpi.or.id/bayar-bertahap-begini-cara-isi-faktur-pajak-termin-sesuai-per-11-pj-2025/)

**When the faktur must be made (timing)**
- Pasal 13 ayat (1a) UU PPN sets the time of faktur creation:
  - (a) at delivery of the BKP/JKP;
  - (b) at receipt of payment, if payment comes before delivery;
  - (c) at receipt of a termin payment for a staged delivery;
  - (d) at another time set by the Minister.
  - Sources: [search summary of Enforcea / Online Pajak](https://enforcea.com/Blog/kapan-seharusnya-faktur-pajak-dibuat); [Online Pajak](https://www.online-pajak.com/tentang-efaktur-ppn/kapan-faktur-pajak-diterbitkan/)
- PER-11/PJ/2025 Pasal 3 ayat (2) jo. Pasal 13 UU PPN, as tabulated by Online Pajak:
  - deliveries to a bendaharawan pemerintah: faktur is made when the PKP submits the bill;
  - faktur gabungan: one faktur covers all deliveries to one buyer in a calendar month and must be made by the end of that month — [Online Pajak](https://www.online-pajak.com/tentang-efaktur-ppn/kapan-faktur-pajak-diterbitkan/)
- A faktur can be backdated by at most 3 months from when it should have been made. A later faktur counts as never made. A late faktur is fined 1% of DPP under Pasal 14(4) UU KUP as amended by HPP — [Online Pajak](https://www.online-pajak.com/tentang-efaktur-ppn/kapan-faktur-pajak-diterbitkan/) **[uncertain: commercial blog, not checked against the PER text]**

**Minimum contents of a Faktur Pajak (Pasal 13 ayat 5 UU PPN)**
- At minimum:
  - name, address and NPWP of the seller;
  - buyer identity: name, address and NPWP, or NIK, or passport number for a foreign individual; name and address only for a foreign entity or non-subject;
  - type of goods/services, price or replacement value, and discount;
  - PPN collected;
  - PPnBM collected;
  - code, serial number and date of the faktur;
  - name and signature of the authorised signer.
  - A faktur missing these items fails the formal requirements, and the buyer cannot credit its input tax — [DDTCNews](https://news.ddtc.co.id/berita/nasional/1804950/faktur-pajak-harus-penuhi-syarat-formal-dan-material-seperti-apa); [DDTCNews on UU Cipta Kerja amendment](https://news.ddtc.co.id/berita/nasional/24557/ketentuan-identitas-pembeli-dalam-faktur-pajak-diperjelas)

**NPWP 16 digit / NIK / NITKU**
- PMK 112/2022, as amended by PMK 136/2023, moved administration to three identifiers: NIK as NPWP for resident individuals, a **16-digit NPWP** for entities (badan) and government agencies, and **NITKU** for each place of business. PER-06/PJ/2024 applied this from 1 July 2024 — [DDTCNews](https://news.ddtc.co.id/literasi/kamus/1803684/apa-beda-nik-sebagai-npwp-npwp-16-digit-dan-nitku)
- A NITKU has 22 digits: the 16-digit NPWP/NIK plus a 6-digit place sequence. The head office is 000000 and the first branch 000001. The format is in the annex to PER-6/PJ/2024. The duty to obtain a NITKU is in Pasal 16 PMK 81/2024 — [Online Pajak](https://www.online-pajak.com/bisnis/nitku-adalah/); [Ortax](https://ortax.org/penggunaan-nitku-sebagai-pengganti-npwp-cabang)
- In Coretax: if the buyer ID type is TIN/NPWP, the buyer "ID TKU" must be the **22-digit NITKU**. For other ID types (NIK, passport, other ID), the TKU field is "0000". This comes from a DDTC consultation answer, not regulation text — [DDTCNews Konsultasi Coretax](https://news.ddtc.co.id/review/konsultasi-coretax/1808368/muncul-error-nitku-pembeli-tidak-valid-apa-yang-harus-dilakukan)

**BUMN as wajib pungut PPN [2025 change: legal basis moved]**
- **PMK 8/PMK.03/2021 was revoked ("Dicabut dengan") by PMK 81/2024**, the Coretax omnibus regulation — [JDIH BPK](https://peraturan.bpk.go.id/Details/160062/pmk-no-8pmk032021). Hukumonline reported that PMK 81/2024 cancelled 42 earlier tax rules — [Hukumonline](https://www.hukumonline.com/berita/a/menkeu-terbitkan-pmk-omnibus-perpajakan--42-regulasi-dibatalkan-lt6729c24ae06a2)
- PMK 81/2024 Pasal 291 ayat (1): PPN on deliveries by a rekanan to a pemungut is collected, paid and reported by the pemungut. Pasal 292 ayat (1) huruf a names BUMN as pemungut, alongside restructured BUMN and certain companies directly owned by a BUMN — [DDTCNews (Apr 2026)](https://news.ddtc.co.id/berita/nasional/1818728/kondisi-yang-bikin-pkp-setor-sendiri-ppn-saat-bertransaksi-dengan-bumn)
- PMK 81/2024 Pasal 294 ayat (1) lists six cases where the BUMN does **not** collect, so the seller (rekanan) collects and pays the PPN itself:
  1. payments **≤ Rp10 juta including PPN** that are not split from a larger transaction;
  2. supplies with a PPN not-collected or exemption facility;
  3. BBM/BBBM from Pertamina;
  4. telecom services;
  5. airline tickets;
  6. other supplies not subject to PPN.
  - Sources: [DDTCNews](https://news.ddtc.co.id/berita/nasional/1818728/kondisi-yang-bikin-pkp-setor-sendiri-ppn-saat-bertransaksi-dengan-bumn); same list under PMK 8/2021: [DDTCNews](https://news.ddtc.co.id/berita/nasional/1801939/kondisi-yang-membuat-ppn-atau-ppnbm-tidak-dipotong-oleh-wapu-bumn)
- Under the old rules, Kring Pajak advised kode "030" (code 03, status 0) for transactions with a BUMN wapu that meet the criteria — [DDTCNews](https://news.ddtc.co.id/berita/nasional/1801939/kondisi-yang-membuat-ppn-atau-ppnbm-tidak-dipotong-oleh-wapu-bumn). Under the 17-digit format the status is 2 digits ("00" normal) — [Pajakku](https://pajakku.com/artikel/per-11pj2025-pokok-perubahan-faktur-pajak-dalam-coretax)

**PPh Pasal 22 collected by BUMN [2025 change]**
- PMK 51/2025 now defines the "badan usaha tertentu" that collect PPh 22: BUMN, BUMN restructured by transfer of state shares to another BUMN, and certain companies directly owned by a BUMN — [Ortax (22 Dec 2025)](https://ortax.org/ketentuan-pemungutan-pph-pasal-22-oleh-bumn). The earlier basis, PMK 34/2017 as last amended by PMK 41/2022, is cited by [DJP](https://pajak.go.id/en/node/111332).
- Rate: **1.5% × purchase price excluding PPN**, not final — [Ortax](https://ortax.org/ketentuan-pemungutan-pph-pasal-22-oleh-bumn); [Binus Accounting](https://accounting.binus.ac.id/?p=12498)
- Exempt when the payment is **≤ Rp10 juta excluding PPN** and not split. Also exempt: BBM/gas/lubricants/postal items, water and electricity, upstream oil and gas output, geothermal — [Ortax](https://ortax.org/ketentuan-pemungutan-pph-pasal-22-oleh-bumn)
- UMKM taxpayers under Rp4,8 miliar turnover on the 0.5% final scheme (PP 23/2018, now PP 55/2022) are treated differently, per a DJP article — [DJP](https://www.pajak.go.id/index.php/en/node/35009). This does not apply to a PKP above the threshold (see Gaps).

**BUMN status after the 2025 BUMN law changes [uncertain]**
- UU 1/2025 (signed 24 February 2025) created BPI Danantara. The Minister holds the Dwiwarna series A share and Danantara holds series B shares in the holdings — [DDTCNews](https://news.ddtc.co.id/berita/nasional/1809104/uu-12025-resmi-diteken-atur-pembentukan-dan-wewenang-bpi-danantara); [Investortrust](https://investortrust.id/business/57903/ini-poin-utama-uu-bumn-terbaru-terkait-danantara). UU 16/2025 amended it further, and an October 2025 amendment turned the ministry into BP BUMN — [DDTCNews](https://news.ddtc.co.id/berita/nasional/1820926/bpk-sebut-belum-ada-dasar-hukum-5-bumn-kemenkeu-di-luar-danantara)
- As of 15 October 2026, DJP said a PP on the taxation of BUMN restructuring under Danantara was still awaited. PER-12/PJ/2026 already lists six new 0% final-PPh object codes for restructuring transactions — [DDTCNews](https://news.ddtc.co.id/berita/nasional/1823309/djp-pp-pajak-untuk-restrukturisasi-bumn-danantara-masih-digodok). No source found says BUMN like BNI lost pemungut status.

### Inferences
- **Worked example (non-luxury goods, invoice above both thresholds):**
  - harga jual Rp20.000.000
  - DPP nilai lain = 11/12 × 20.000.000 = Rp18.333.333
  - PPN = 12% × 18.333.333 = Rp2.200.000
  - invoice total Rp22.200.000
  - BNI withholds PPN Rp2.200.000 (it pays this to the state itself) and PPh 22 = 1.5% × 20.000.000 = Rp300.000
  - **expected bank receipt = Rp19.700.000**
  - The payment-review screen must expect this net figure. Otherwise every BNI payment looks like an underpayment. (Computed from PMK 131/2024 Pasal 3, PMK 81/2024 Pasal 294, and the PMK 51/2025 rate above.)
- **The two thresholds have different bases.** The PPN wapu threshold is Rp10 juta *including* PPN; the PPh 22 threshold is Rp10 juta *excluding* PPN. Example: harga jual Rp9.200.000 + PPN Rp1.012.000 = Rp10.212.000. BNI collects the PPN (above Rp10 juta incl. PPN) but no PPh 22 (at or below Rp10 juta excl. PPN). The invoice engine should evaluate the two flags separately, per payment, and guard against splitting.
- Likely kode transaksi for Unit Toko:
  - ordinary non-luxury sales where Unit Toko collects the PPN (e.g. BNI orders ≤ Rp10 juta incl. PPN, or non-pemungut buyers): **04** (DPP nilai lain);
  - BNI orders above Rp10 juta incl. PPN: **03**, with the DPP Nilai Lain 11/12 box ticked, by analogy with the PER-1/PJ/2025 rule that 02 takes priority over 04;
  - an older DDTC article said "detail 1" for sub-Rp10 juta BUMN purchases, but that predates PMK 131/2024. Confirm with a tax adviser.
- The buyer is BNI's single 16-digit NPWP (badan). Each receiving branch should carry its own 22-digit NITKU. A wrong NITKU forces a cancel-and-reissue, not a pengganti. So **the BNI branch master data (NPWP + NITKU + branch address) is the riskiest data on the whole finance side** and needs its own checker step.
- Every bulk invoicing of a delivered order must reach Coretax upload by the 20th of the next month. A "faktur pajak" status column and a deadline counter on the invoice list are justified.

### Gaps
- Exact article in PMK 81/2024 that now carries the Rp4,8 miliar pengusaha-kecil threshold (sources still cite PMK 197/2013). Not verified.
- Whether a cooperative supplier gets any PPh 22 exemption certificate path (e.g. under PP 55/2022 for small taxpayers) was not researched in depth. It does not apply to Unit Toko if its turnover is above Rp4,8 miliar.
- Whether BNI (PT Bank Negara Indonesia (Persero) Tbk) is still listed or treated as a pemungut after the Danantara restructuring. No regulation was found removing it. Practical check: BNI's PO/contract and its procurement/tax unit.
- The Coretax bulk-import format (XML/Excel template) for fakturs was not sourced.
- The name of the Coretax-era PPh 22 withholding slip (bukti pemotongan/pemungutan unifikasi) was not confirmed in this session.
- Whether the 3-month backdate limit and the 1% sanction wording match the PER-11/PJ/2025 text was not checked. Only a commercial blog was seen.

---

## 2. Which fields belong on the commercial invoice (tagihan/faktur penjualan) and the kuitansi, and when does e-Meterai apply?

### Takeaway
No regulation prescribes the layout of a commercial invoice. Indonesian practice converges on a common field set: number, date, due date, PO number, payment terms, seller and buyer identity with NPWP, line items, PPN, total, and payment destination. Terbilang and signatures are convention. Bea meterai of Rp10.000 (UU 10/2020) applies to a document stating more than Rp5.000.000 *only if it states receipt of money or acknowledges payment*. A kuitansi or "LUNAS" receipt above Rp5 juta needs e-Meterai; a pure billing invoice does not.

### Cited Findings
- Credit-invoice fields per Mekari Jurnal:
  - Nomor Faktur (unique, sequential);
  - Tanggal Faktur (starts the credit period);
  - Tanggal Jatuh Tempo;
  - Nomor PO (optional, links to the buyer's order);
  - Syarat Pembayaran (e.g. Net 30, 2/10 Net 30, instalments);
  - delivery date;
  - delivery/waybill number;
  - plus seller and buyer details and line items.
  - Source: [Jurnal.id](https://www.jurnal.id/id/blog/arti-faktur-penjualan-kredit-dan-komponen-pentingnya/)
- "8 komponen wajib":
  1. unique sequential invoice number (e.g. INV/2026/06/001) for an audit trail;
  2. issue date;
  3. due date (commonly 7/14/30 days);
  4. seller data incl. NPWP if PKP;
  5. buyer data incl. buyer NPWP for B2B;
  6. item detail (description, qty, unit, unit price);
  7. PPN if the seller is PKP;
  8. total.
  - Source: [Online Pajak](https://www.online-pajak.com/bisnis/cara-membuat-invoice-yang-benar/). (The "wajib" label is the vendor's, not a legal citation.)
- PKPs still need a separate Faktur Pajak; the commercial invoice does not replace it — [search summary, Online Pajak](https://www.online-pajak.com/bisnis/cara-membuat-invoice/)
- Accurate Online has a "Tukar Faktur (tanda terima faktur)" module "to accommodate payment-collection procedures". It has its own numbering and its own approval type in "Penyetuju Transaksi" — [Accurate release 31 Oct 2017](https://accurate.id/release-notes-aol/1004194/)
- **UU 10/2020 Pasal 3 ayat (2) huruf g:** a document stating a nominal amount above Rp5.000.000 that (1) mentions receipt of money, or (2) acknowledges a debt paid or offset in full or in part, is subject to bea meterai. **Pasal 5:** fixed rate Rp10.000 — [UU 10/2020 text (Pajakku)](https://pajakku.com/tax-guide/uu/10-tahun-2020); [JDIH Setkab PDF](https://jdih.setkab.go.id/PUUdoc/176264/UU_Nomor_10_Tahun_2020.pdf)
- When the duty arises: for Pasal 3(2)(g) money documents, when the document is handed to the party it is made for — [UU 10/2020 (Pajakku)](https://pajakku.com/tax-guide/uu/10-tahun-2020)
- Pasal 7 non-objects include documents for the movement of goods: "bukti untuk pengiriman dan penerimaan barang", surat angkutan barang, konosemen, and similar — [UU 10/2020 (Pajakku)](https://pajakku.com/tax-guide/uu/10-tahun-2020)
- If meterai is unpaid, it is settled by Pemeteraian Kemudian plus an administrative sanction of **100%** of the duty (Pasal 17–18) — [UU 10/2020 (Pajakku)](https://pajakku.com/tax-guide/uu/10-tahun-2020)
- DJP article: a nota pembelian, faktur penjualan or invoice owes bea meterai **if it is used as proof of receipt of money**; a document that only evidences receipt of goods does not — [DJP](https://pajak.go.id/en/node/49921)
- An invoice used only to request payment is not a meterai object. It becomes one when it doubles as a kuitansi — [Online Pajak (updated Oct 2026)](https://www.online-pajak.com/tips-e-meterai/apakah-invoice-harus-pakai-meterai-memahami-pentingnya-e-meterai-pada-invoice); [Hukumonline](https://www.hukumonline.com/klinik/a/fungsi-meterai-dan-dokumen-dokumen-yang-wajib-bermeterai-lt51426f0a4f0ee)
- Tax payment receipts (kuitansi pajak) are listed as non-objects in DJP's e-Meterai briefing — [Pajakku webinar recap](https://pajakku.com/artikel/webinar-pajakku-sosialisasi-e-meterai-masa-pajak)

### Inferences
- Recommended "Tagihan" print fields:
  - **Header:** koperasi legal name, address, NPWP 16 digit, NITKU, PKP status.
  - **Buyer block:** "PT Bank Negara Indonesia (Persero) Tbk – Kantor Cabang …", branch address, NPWP 16 + NITKU 22 digit of the branch.
  - **Document facts:** nomor tagihan, tanggal, jatuh tempo, syarat pembayaran, nomor PO/SPK BNI, nomor surat jalan/BAST.
  - **Lines:** qty, satuan, harga satuan, jumlah.
  - **Totals:** Subtotal (Harga Jual), DPP Nilai Lain (11/12), PPN 12%, Total Tagihan, Terbilang.
  - **Wapu note:** "PPN Rp… dipungut oleh BNI selaku Pemungut PPN" and "PPh 22 1,5% Rp…", then **"Jumlah yang ditransfer: Rp…"**.
  - **Payment:** bank, nomor rekening, atas nama koperasi, and the instruction "cantumkan nomor tagihan di berita transfer".
  - **Signature:** name and title of the signer, with a reference to the Faktur Pajak NSFP once approved.
- Keep the **kuitansi / tanda terima pembayaran** a separate document from the tagihan. Flag "perlu e-Meterai Rp10.000" automatically when the amount stated as received is above Rp5.000.000. Never stamp "LUNAS" on the tagihan PDF itself, because that turns it into a meterai object.
- Surat jalan and BAST (goods receipt) are not meterai objects under Pasal 7, so the UI should not suggest e-Meterai there.

### Gaps
- No legal source requires "terbilang", company stamp or wet signature on a commercial invoice. These are convention, likely also required by BNI's own procurement and payment checklist, which was not found publicly.
- Which party is the "Pihak Yang Terutang" for a one-sided kuitansi under UU 10/2020 Pasal 9 was not confirmed from the text in this session.
- BNI vendor/procurement guidance (required invoice attachments such as tanda terima faktur, BAST, faktur pajak, SPK) was not researched; no public source was found.

---

## 3. How do Indonesian tools (Accurate, Jurnal, Kledo, Paper.id, Xendit) structure invoice status, aging, payment recording, bank import and reconciliation?

### Takeaway
Indonesian SMB accounting tools use one shared bank-reconciliation model: bring in the bank statement (manual import, or bank "direct feed"/API for BCA, Mandiri, BNI and others), let the system suggest matches on equal amount and date, allow one-to-many and bulk matching, create a new transaction from an unmatched line, and use bank rules for recurring items. Invoicing tools add WhatsApp/SMS/email delivery, custom reminders, recurring invoices and VA payment. Payment-link providers separate "Paid" from "Settled". Exact status-tab labels in Jurnal, Kledo and Accurate could not be confirmed this session.

### Cited Findings
- **Mekari Jurnal: Cash Link vs Cash Mapping.**
  - Cash Link reconciles one statement line at a time against transactions already recorded.
  - Cash Mapping books many unrecorded bank lines at once (e.g. utilities, rent).
  - Both need an imported statement or Direct Feeds.
  - When a bank line with the same amount and date arrives, the system suggests the match.
  - Bank Rules auto-record lines, e.g. a transfer above Rp500.000 whose description contains "brosur" goes to advertising expense.
  - Source: [Jurnal.id blog (search summary)](https://www.jurnal.id/id/blog/PenerapanFiturCashlink)
- Jurnal "Cashlink & Bank": reconciled data is journalised automatically, and statement data can arrive via API — [Jurnal.id](https://www.jurnal.id/id/fitur/kas-transaksi/cashlink-bank)
- **Jurnal Direct Feeds:**
  - pulls statement lines automatically;
  - bank list includes BCA Corporate/Personal, Mandiri, CIMB Niaga, **BNI**, BNI Syariah and Danamon;
  - for BCA Corporate: Kas & Bank → account → Action → Install Direct Feeds, then a token; the "Bank Statements" tab shows pulled lines;
  - some bank links must be refreshed manually and only fetch the last 30 days.
  - Source: [Jurnal.id](https://www.jurnal.id/id/blog/direct-feeds-bni-jurnal)
- **Accurate Online:**
  - 2017: internet-banking integration (BCA, Mandiri) for statements and bank reconciliation; "Buat Transaksi Baru Dari Data Impor Rekening Koran"; a changed matching-check mechanism in Rekonsiliasi Bank — [Accurate release v1.0.0#4194](https://accurate.id/release-notes-aol/1004194/)
  - Oct 2024: "Rekonsiliasi Bank: Ditambahkan fitur untuk mencocokkan mutasi sekaligus" (multi-match) and bulk delete of bank lines. Same release: a "Jurnal Umum" error when debit ≠ credit, and fixes to neraca percobaan and faktur-belum-lunas vs balance-sheet differences — [Accurate release v1.0.1#2083](https://accurate.id/release-notes-aol/1012083/)
  - Approval ("Penyetuju Transaksi") exists per transaction type, including Tukar Faktur and Uang Muka Penjualan approvals — [Accurate release v1.0.0#4194](https://accurate.id/release-notes-aol/1004194/); [Accurate release v1.0.1#2083](https://accurate.id/release-notes-aol/1012083/)
- **Kledo:** dozens of customisable invoice templates; send by WhatsApp/SMS/email; "Timely Invoice Reminders… custom reminders"; recurring invoices — [Kledo](https://kledo.com/en/invoices/). Third-party reviews describe automatic bank-transaction matching and 50+ reports incl. neraca, laba rugi, arus kas, buku besar — [HashMicro review](https://www.hashmicro.com/id/blog/aplikasi-kledo/)
- **Paper.id:** sends invoices via WhatsApp, SMS or email with automatic reminders; the vendor dashboard shows payment status; VA payment without fee via Tokopedia — [Tokopedia help](https://www.tokopedia.com/help/article/t-int75-paper)
- **Xendit payment links/invoices:**
  - statuses: Unpaid, Paid (customer paid, funds not yet in balance), Settled (funds withdrawable), Expired (cannot be paid or reactivated), plus Active/Stopped for reusable links;
  - QRIS payments end at PAID rather than SETTLED.
  - Source: [Xendit docs (search index; page returned 404 on direct fetch)](https://docs.xendit.co/xeninvoice/statuses)
- General accounts-receivable aging convention, from non-Indonesian tools: aging counts only approved or partially paid invoices, shows only the remaining balance, and buckets by an "as of" date — [Qoyod](https://www.qoyod.com/en/knowledge-base/aging-report/); [Avaza](https://intercom.help/avaza/en/articles/6791383-invoice-aging-details-report)

### Inferences
- **Status model for Unit Toko invoices** (standard across tools, Indonesian labels):
  - lifecycle: Draf → Menunggu persetujuan → Terbit/Belum dibayar → Dibayar sebagian → Lunas; side states Dibatalkan (Void) and Retur/Nota kredit;
  - "Jatuh tempo / Terlambat" should be a computed overlay (due date passed and balance > 0), not a stored lifecycle state;
  - add a separate **Faktur Pajak status**: Belum dibuat → Draf Coretax → Disetujui (NSFP) → Diganti → Dibatalkan.
  - Wapu adds one more state Indonesian SMB tools rarely show: **"Lunas (netto pungutan)"**. The bank receipt plus the withheld PPN/PPh 22 equals the invoice total, pending receipt of BNI's withholding slip.
- **Aging (umur piutang)** buckets: Belum jatuh tempo, 1–30, 31–60, 61–90, >90 days. Group by BNI region/branch, because collection is chased per branch.
- **Reconciliation UI pattern:**
  - left pane: imported mutasi lines; right pane: candidate receipts/invoices;
  - auto-suggest when amount = expected net and date is within a window;
  - one-to-many and many-to-one match;
  - "buat transaksi dari mutasi" for bank fees and interest;
  - bank rules for recurring lines;
  - running "selisih" (statement balance vs book balance) that must reach 0 before closing a period.

### Gaps
- Exact status-tab labels and aging-report layouts in Jurnal, Kledo and Accurate help centres were not retrieved; the help pages did not appear in search. No BNI-specific statement file format (CSV/MT940 columns) was found.
- Xendit fixed-VA partial or overpayment behaviour was not found.
- Midtrans invoicing was not researched; the session limit was hit.

---

## 4. What are the best patterns for payment-proof review (OCR, duplicates, partial and overpayment) and for maker/checker on invoice issue and void?

### Takeaway
Indonesian banks and OJK warn that transfer screenshots are easily forged, including with AI. A screenshot should only ever be a *pre-screen*. Confirmation must come from the bank mutation. Academic Indonesian work uses OCR (e.g. Google ML Kit) to extract amount, date and time, and image forensics to flag edits. Accurate shows Indonesian SMB software uses per-document-type approval. PER-11/PJ/2025 makes faktur errors costly (buyer-identity errors need cancel-and-reissue), which justifies a checker step before release.

### Cited Findings
- OJK warns of AI-forged transfer proofs — [Metro TV News](https://www.metrotvnews.com/read/N4EC4Jqr-hati-hati-pemalsuan-bukti-transfer-pakai-ai-ini-yang-harus-dilakukan). Bank Mandiri advises checking the bank's own app or account mutation instead of trusting the screenshot — [Bank Mandiri (search summary; direct fetch failed)](https://www.bankmandiri.co.id/waspada-modus-bukti-transfer-palsu-jangan-sampai-tertipu)
- An Indonesian study combines image forensics with K-Nearest Neighbor classification to detect manipulated digital payment proofs — [Sistemasi journal](https://sistemasi.ftik.unisi.ac.id/index.php/stmsi/article/download/5728/1125)
- A ticketing-app study uses Google ML Kit OCR to extract transfer amount, date and time from proof images to speed validation — [Amikom Purwokerto](https://eprints.amikompurwokerto.ac.id/id/eprint/2920/3/ABSTRAK.pdf)
- Document-verification vendors pre-screen for inconsistent fonts, misaligned text and altered dates — [Didit](https://didit.me/id/blog/developers-guide-proof-of-funds-verification-didit-ocr-database-validation-id/)
- Accurate has approval workflows per transaction type ("Penyetuju Transaksi"), including Tukar Faktur and Uang Muka Penjualan approval — [Accurate 2017](https://accurate.id/release-notes-aol/1004194/); [Accurate 2024](https://accurate.id/release-notes-aol/1012083/)
- A wrong buyer identity or NITKU on a Faktur Pajak cannot be corrected with a pengganti; it requires cancellation and a new faktur — [DDTCNews](https://news.ddtc.co.id/berita/nasional/1811670/keliru-cantumkan-nitku-pembeli-tak-bisa-bikin-faktur-pajak-pengganti)
- A faktur pengganti after a retur or cancellation is based on net value — [Pajakku](https://pajakku.com/artikel/per-11pj2025-pokok-perubahan-faktur-pajak-dalam-coretax)

### Inferences
- **Payment-proof intake (Bukti pembayaran):**
  - store a perceptual hash and a SHA-256 of every upload;
  - flag duplicates on (a) the same file or hash, or (b) the same bank + reference number + amount + date seen before on any invoice;
  - OCR fields: bank asal, nama pengirim, rekening tujuan, nominal, tanggal/jam, nomor referensi, berita;
  - show per-field confidence and highlight mismatches against the invoice: wrong receiving account, amount ≠ expected net, date before the invoice date.
- **Review rule:** "Terverifikasi" only when matched to a bank mutation line (Mutasi bank) or a confirmed Penerimaan bank. OCR alone can never mark an invoice paid. This follows the bank and OJK guidance above.
- **Allocation:**
  - support partial payments (status Dibayar sebagian; remaining balance keeps the original due date);
  - support one transfer across several invoices, since BNI branches often pay several invoices in one transfer (inference);
  - park overpayments as "Kelebihan bayar / uang muka pelanggan", to be refunded or applied to the next invoice, never as revenue;
  - explain expected underpayment: PPN wapu + PPh 22 lines pre-filled and marked "menunggu bukti pungut/potong".
- **Maker/checker:**
  - Invoice release: maker ≠ checker.
  - Checker checklist: buyer NPWP + NITKU (validated format, 22 digits); kode transaksi (03 vs 04) and the wapu/PPh 22 threshold flags; DPP Nilai Lain amount; PO/SPK number and BAST attached; receiving account active.
  - Void: always needs a reason and a second approver. If a Faktur Pajak is already approved, the void path must first record the Coretax cancellation (or retur/nota) before the commercial invoice can be voided.
  - Changing a receiving account: also dual-control. Changing bank-account details is a classic fraud vector (inference; no Indonesian source retrieved).

### Gaps
- No source on duplicate-receipt detection practice in Indonesian tools was found.
- Real BNI-to-vendor payment behaviour (batching, berita transfer content, timing after tukar faktur) is not public.
- The exact Coretax cancellation flow and deadlines for cancelling an approved faktur were not retrieved.

---

## 5. Which accounting screens does a small koperasi need (journals, ledger, trial balance, period close; SAK EP vs SAK ETAP)?

### Takeaway
Permenkop UKM 2/2024 (Kebijakan Akuntansi Koperasi) requires koperasi to use an IAI standard (SAK Indonesia, SAK EP or SAK EMKM). SAK ETAP is replaced by SAK EP from 1 January 2025. Koperasi financial statements comprise laporan posisi keuangan, laporan perhitungan hasil usaha, laporan perubahan ekuitas, laporan arus kas and CALK. The statements must separate member and non-member revenue, and annual reports go to the Ministry electronically.

### Cited Findings
- Permenkop UKM No. 2 Tahun 2024 tentang Kebijakan Akuntansi Koperasi was set on 10 January 2024 and promulgated on 16 January 2024 (BN 2024 No. 35). It replaces the 2015 accounting guidelines (PermenkopUKM 12, 13, 14/Per/M.KUKM/IX/2015). Koperasi must use IAI-issued SAK: SAK Indonesia, SAK EP or SAK EMKM — [peraturan.go.id PDF](https://peraturan.go.id/files/permenkop-kukm-no-2-tahun-2024.pdf); [IAI (Permenkop text)](https://web.iaiglobal.or.id/assets/files/file_publikasi/Permenkop%20No%202%20Tahun%202024.pdf) (from search summary; PDF text was not machine-readable here)
- SAK ETAP is revoked and replaced by SAK EP from January 2025. Kemenkop's slides say SAK EP fits koperasi better because it covers consolidation and separating member and non-member revenue — [Kemenkop UKM slides via IAI](https://web.iaiglobal.or.id/assets/files/file_publikasi/Materi_Permenkop%202%20thn%202024_Kemenkop%20dan%20UKM.pdf) (search summary)
- Statement set under SAK EP for KSP/USP and real-sector koperasi: laporan posisi keuangan, laporan perhitungan hasil usaha, laporan perubahan ekuitas, laporan arus kas, catatan atas laporan keuangan (Pasal 8). Annual statements are submitted electronically to Kemenkop UKM within a deadline — [search summary of IAI/Kemenkop materials](https://web.iaiglobal.or.id/assets/files/file_publikasi/Materi_Permenkop%202%20thn%202024_Kemenkop%20dan%20UKM.pdf)
- KSP/USP must apply SAK-EP-based policy by financial year 2025 at the latest (Pasal 5(2), Lampiran I) — [search summary citing DDTC](https://news.ddtc.co.id/berita/nasional/1802464/ddtc.co.id) **[uncertain: applies to savings-and-loan units; real-sector timing not confirmed]**
- Indonesian tools treat neraca percobaan (trial balance), rincian buku besar (GL detail with drill-down) and a balanced-journal check as core. Accurate errors when "jurnal yang anda masukkan tidak seimbang antara debit dengan kredit" and lets users drill from GL detail into journals — [Accurate 2024](https://accurate.id/release-notes-aol/1012083/); [Accurate 2017](https://accurate.id/release-notes-aol/1004194/)

### Inferences
- Minimum accounting screens:
  - **Jurnal list:** date, number, source document link (tagihan/penerimaan/mutasi), memo, debit = credit badge, posted/locked flag.
  - **Buku besar:** per akun, running balance, drill-down to the source.
  - **Neraca percobaan:** per period, with an unbalanced-amount alarm.
  - Koperasi statements named exactly as Permenkop 2/2024 names them: "Laporan Posisi Keuangan", "Laporan Perhitungan Hasil Usaha" (not "Laba Rugi"), "Laporan Perubahan Ekuitas", "Laporan Arus Kas", CALK.
  - **Period close / tutup buku:** a monthly lock after bank reconciliation reaches selisih 0 and PPN for the masa is reconciled to approved fakturs.
- Accounts the billing flow needs:
  - Piutang usaha – BNI;
  - PPN Keluaran;
  - **PPN dipungut oleh pemungut** (contra/settlement account, so wapu-collected PPN does not appear as an unpaid receivable);
  - **Uang muka PPh 22 / PPh 22 dibayar dimuka** (a creditable prepaid tax once the bukti pungut arrives);
  - Kelebihan bayar pelanggan;
  - Beban administrasi bank;
  - one cash/bank account per receiving account.
- Revenue should be tagged member vs non-member. Sales to BNI are presumably non-member, to be confirmed by the koperasi. SAK EP and Permenkop expect the split in the perhitungan hasil usaha.

### Gaps
- The exact Permenkop 2/2024 article numbers for real-sector (non-KSP) koperasi deadlines and the reporting deadline were not verified from the regulation text (the PDF was not machine-readable here).
- Whether Unit Toko's koperasi qualifies for SAK EMKM instead of SAK EP was not determined.

---

## 6. Screen-by-screen improvements for Unit Toko finance pages

### Takeaway
Most of the value sits in making BUMN-wapu arithmetic, Coretax faktur status and buyer NITKU first-class data. Then every list, check and match screen can expect the *net* BNI transfer and not raise false underpayments, and errors that need a faktur cancel-and-reissue are caught before release.

### Cited Findings
- (All grounded in Sections 1–5.) The facts below drive the recommendations:
  - PPN = 12% × 11/12 for non-luxury goods — [MUC](https://muc.co.id/id/article/resmi-berlaku-tarif-ppn-untuk-barang-mewah-12-non-barang-mewah-11)
  - BUMN collects PPN above Rp10 juta incl. PPN (PMK 81/2024 Pasal 294) — [DDTCNews](https://news.ddtc.co.id/berita/nasional/1818728/kondisi-yang-bikin-pkp-setor-sendiri-ppn-saat-bertransaksi-dengan-bumn)
  - PPh 22 at 1.5% above Rp10 juta excl. PPN (PMK 51/2025) — [Ortax](https://ortax.org/ketentuan-pemungutan-pph-pasal-22-oleh-bumn)
  - 17-digit NSFP, upload by the 20th, cancel-and-reissue for buyer-identity errors (PER-11/PJ/2025) — [Pajakku](https://pajakku.com/artikel/per-11pj2025-pokok-perubahan-faktur-pajak-dalam-coretax); [DDTCNews](https://news.ddtc.co.id/berita/nasional/1811670/keliru-cantumkan-nitku-pembeli-tak-bisa-bikin-faktur-pajak-pengganti)
  - Buyer TKU = 22-digit NITKU — [DDTCNews](https://news.ddtc.co.id/review/konsultasi-coretax/1808368/muncul-error-nitku-pembeli-tidak-valid-apa-yang-harus-dilakukan)
  - Meterai only on receipt-type documents above Rp5 juta — [UU 10/2020](https://pajakku.com/tax-guide/uu/10-tahun-2020); [DJP](https://pajak.go.id/en/node/49921)
  - Indonesian reconciliation patterns: auto-suggest on amount and date, multi-match, bank rules, create from unmatched line — [Jurnal](https://www.jurnal.id/id/blog/PenerapanFiturCashlink); [Accurate](https://accurate.id/release-notes-aol/1012083/)
  - Screenshots are not proof of payment — [Metro TV/OJK](https://www.metrotvnews.com/read/N4EC4Jqr-hati-hati-pemalsuan-bukti-transfer-pakai-ai-ini-yang-harus-dilakukan)

### Inferences

**Tagihan (/billing/invoices)**
- Tabs:
  - Draf, Menunggu persetujuan, Belum dibayar, Dibayar sebagian, Lunas, Dibatalkan, each with count and Rupiah sum;
  - a "Terlambat" chip/filter computed from the due date;
  - a "Faktur pajak belum terbit" filter with a countdown "Batas unggah 20 {bulan}".
- Columns:
  - No. tagihan; Cabang BNI (name + last 6 digits of NITKU); Tgl; Jatuh tempo (+"lewat N hari"); Total tagihan;
  - "Dipungut BNI" (PPN + PPh 22); **"Diharapkan diterima"** (net); Diterima; Sisa;
  - Status; Faktur pajak (NSFP or status).
- Above the table: an aging strip (Belum jatuh tempo / 1–30 / 31–60 / 61–90 / >90) that filters on click; group-by Cabang/Wilayah.
- Bulk actions: kirim pengingat (WhatsApp/email, as Kledo and Paper.id do), cetak tanda terima faktur, ekspor untuk Coretax.

**Tagihan detail / print**
- Show the calculation ladder explicitly: Harga jual → DPP Nilai Lain 11/12 → PPN 12% → Total → minus PPN dipungut BNI → minus PPh 22 → Jumlah ditransfer.
- Show the kode transaksi chosen (03/04) and why, e.g. "> Rp10 juta termasuk PPN, BNI pemungut".
- Print variant per Section 2 fields, with terbilang and the receiving account.
- Separate "Kuitansi" output that flags e-Meterai when the amount stated as received is above Rp5.000.000.
- Timeline: dibuat → disetujui → dikirim → tukar faktur diterima BNI → faktur pajak disetujui DJP → pembayaran → bukti pungut diterima.

**Persetujuan penerbitan tagihan (/billing/invoice-releases)**
- Enforce maker ≠ checker.
- Checklist, with each item a pass/fail chip:
  - NPWP 16 digit valid;
  - NITKU 22 digit present and matching the delivery branch;
  - kode transaksi consistent with the thresholds;
  - DPP nilai lain = 11/12;
  - PO/SPK number and BAST/surat jalan attached;
  - receiving account active;
  - totals recomputed server-side.
- Show a diff against the order or delivery (qty, price).
- Void/cancel needs a reason and a second approver. If a faktur pajak is already approved, block until the Coretax cancellation or retur is recorded. Warn that buyer-identity errors need cancel-and-reissue, not a pengganti.

**Bukti pembayaran (/billing/payments)**
- Inbox of buyer uploads with OCR-extracted amount, date, sender and reference, plus a duplicate badge (file hash, or same bank + ref + amount + date).
- Status: Baru → Diperiksa → Cocok dengan mutasi / Ditolak (with reason sent to the buyer).
- Never label as "Lunas" here.

**Pemeriksaan pembayaran (/billing/payment-reviews)**
- Split view:
  - left: image with zoom and an EXIF/edit-anomaly hint;
  - centre: OCR fields with confidence, with mismatches in red (destination account not one of Rekening penerimaan, amount ≠ expected net, date before invoice);
  - right: candidate mutasi lines (amount within tolerance, ±3 days) and suggested invoice allocations.
- Allocation grid: multi-invoice, partial, an overpayment row to "Kelebihan bayar", and pre-filled withholding rows "PPN dipungut BNI" and "PPh 22 (menunggu bukti potong)".
- Approve requires a matched mutasi line, or an explicit override with a second approver.

**Penerimaan bank (/billing/bank-receipts)**
- Each receipt shows its source (matched mutasi / manual), allocations, and an unallocated remainder.
- Filter "belum dialokasikan".
- Link to the journal.

**Mutasi bank (/billing/bank-statements)**
- Import (CSV/Excel per bank template), with a preview and dedupe of re-imported lines (date + amount + description + balance).
- Auto-match suggestions (Jurnal-style, amount + date), multi-match (Accurate 2024), bulk delete of a wrong import.
- "Buat transaksi dari mutasi" for admin fees and interest; bank rules, e.g. description contains "BIAYA ADM" → Beban administrasi bank.
- Header: statement balance vs book balance vs selisih, with a "Tutup rekonsiliasi periode" button enabled only at selisih 0.

**Rekening penerimaan (/billing/receiving-accounts)**
- Show bank, nomor rekening and atas nama, which must equal the koperasi legal name.
- Usage count on open invoices; active/inactive.
- Changes require dual approval and are audit-logged.
- Warn when deactivating an account that appears on unpaid invoices.

**Buku akuntansi & Laporan akun dan jurnal**
- Journal list with a source-document link and a balanced badge; GL with drill-down; trial balance.
- Koperasi statement names per Permenkop 2/2024; member vs non-member revenue split.
- Period lock after bank reconciliation and the PPN masa check (fakturs approved = PPN Keluaran ledger).
- Dedicated accounts for PPN dipungut pemungut and prepaid PPh 22, so wapu payments close receivables correctly.

### Gaps
- None of these recommendations were checked against the current Unit Toko implementation in this research pass. They describe a target state, not a confirmed delta.
