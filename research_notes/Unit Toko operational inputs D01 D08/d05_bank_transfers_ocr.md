# D05 — Indonesian bank transfer types and payment proofs (bukti transfer), for OCR-based payment-proof checking (as of Oct 2026)

Scope note: research done 2026-10-16. Primary-source coverage was uneven. Bank Indonesia's HTML pages for BI-FAST/SKNBI/RTGS returned 404 at the URLs tried, and BI's FAQ PDF could not be text-extracted, so several BI facts are cited through BI-quoting secondary sources or bank RIPLAY documents. No bank publishes official receipt-layout or statement-column specifications that I could find. Where facts below say "unverified", they come from general domain knowledge and **must be confirmed against real sample receipts or statements** before they become OCR rules. No real person's account numbers were collected.

## Q1. Transfer methods in Indonesia (2026): limits, fees, settlement, hours, narrative field

### Takeaway
For BNI-division-to-cooperative payments, four rails matter. **Intrabank BNI overbooking** is real-time, usually free, and runs 24/7. **BI-FAST** is real-time and 24/7, capped at Rp250 juta per transaction, with a fee of at most Rp2.500. **SKNBI/kliring** runs in batches, up to Rp1 miliar, during business hours. **RTGS** handles large amounts in real time during business hours. **BNI eCollection Virtual Account** is the strongest identification option because the payer is identified by the VA number itself, not by free text. The legacy online/RTO network (ATM Bersama/Prima/Alto) still exists but costs more (about Rp6.500) and has a lower cap (about Rp50 juta). The maximum length of the narrative field (berita/keterangan) is not published in any source I found.

### Cited Findings
**BI-FAST**
- BI-FAST is the national retail payment infrastructure. It is real-time and available "setiap saat (24/7)". BI's FAQ contrasts it with SKNBI and BI-RTGS, which have fixed operating hours of 06.30–16.30 WIB. — [BI FAQ BI-FAST (PDF), as quoted in search results](https://www.bi.go.id/id/fungsi-utama/sistem-pembayaran/ritel/infrastruktur/Documents/FAQ_BI-FAST.pdf); [PADG 17/2023](https://www.bi.go.id/id/publikasi/peraturan/Pages/PADG_172023.aspx) (24 jam sehari, 7 hari seminggu)
- BI charges participant banks Rp19 per transaction. The maximum fee a bank may charge its customer is Rp2.500 per transaction. The BI cap is Rp250 juta per transaction, and banks may set a lower cap based on their own risk appetite. — [detik.com](https://finance.detik.com/moneter/d-6817089/bi-fast-adalah-kenali-manfaat-serta-bedanya-dengan-rto-rtgs-dan-sknbi)
- A 2026 bank document confirms these values are still current: CIMB Niaga's RIPLAY for BI-FAST, printed 07/04/2026, lists "sampai dengan Rp 250.000.000,-/transaksi", 24/7 service on electronic channels, and a fee of "Rp. 2.500,-/transaksi". — [CIMB Niaga RIPLAY BI-FAST 2026](https://www.cimbniaga.co.id/content/dam/cimb/tb/2026/riplay/umum/RIPLAY%20Umum_Layanan%20Ver%202.0_BI%20FAST.pdf)
- Some banks waive the BI-FAST fee entirely (e.g. MNC Bank for a particular savings product). — [iNews, Feb 2025](https://www.inews.id/finance/bisnis/biar-makin-paham-simak-4-perbedaan-layanan-transfer-dana-bi-fast-rto-dan-rtgs/2)
- BI has said it will expand BI-FAST in phases to bulk credit, direct debit, and request-for-payment. The initial phase covers individual credit transfers. — [BI press release SP 23/27/2021](https://www.bi.go.id/id/publikasi/ruang-media/news-release/Pages/sp_2327021.aspx)
- BCA's BI-FAST flow (myBCA, BCA mobile, KlikBCA) asks the sender to fill in **"Berita"** (narrative) and **"Tujuan Transaksi"** (purpose of transaction), choose "BI FAST" under **"Layanan Transfer"**, and set "Waktu Transfer yang diinginkan" (immediate or scheduled). — [BCA Edukatips](https://bca.co.id/en/informasi/Edukatips/2021/12/23/09/28/transfer-antarbank-kini-hanya-rp2500-via-bi-fast)
- The maximum length of the BI-FAST narrative ("berita"/additional information) is not in any public BI or bank page I found. — see Gaps.

**Online/RTO (ATM Bersama / Prima / Alto)**
- RTO transfers cost about Rp6.500 per transaction and are capped at about Rp50 juta. — [iNews, Feb 2025](https://www.inews.id/finance/bisnis/biar-makin-paham-simak-4-perbedaan-layanan-transfer-dana-bi-fast-rto-dan-rtgs/2)

**SKNBI (kliring) and RTGS**
- SKNBI serves transactions up to Rp1 miliar each. RTGS serves transactions above Rp100 juta. — [detik.com](https://finance.detik.com/moneter/d-6817089/bi-fast-adalah-kenali-manfaat-serta-bedanya-dengan-rto-rtgs-dan-sknbi)
- Operating hours conflict between sources:
  - BI's BI-FAST FAQ (via search snippet) gives 06.30–16.30 WIB for both SKNBI and RTGS. — [BI FAQ BI-FAST](https://www.bi.go.id/id/fungsi-utama/sistem-pembayaran/ritel/infrastruktur/Documents/FAQ_BI-FAST.pdf)
  - IDX Channel cites BI as giving RTGS 06.30–19.00 and SKNBI 06.30–16.45. — [IDX Channel](https://www.idxchannel.com/banking/apa-itu-skn-dan-rtgs-simak-persamaan-perbedaan-dan-ketentuannya/all)
  - Bank customer cut-off times are earlier than BI's system hours.
- Customer fees vary by source and are dated:
  - SKN: roughly Rp2.500–Rp5.000 (one source says Rp2.900).
  - RTGS: roughly Rp25.000–Rp50.000.
  - Most of these sources date from 2021–2023. I found no BNI-specific 2026 fee table. — [IDX Channel](https://www.idxchannel.com/banking/apa-itu-skn-dan-rtgs-simak-persamaan-perbedaan-dan-ketentuannya/all); [Fortune IDN](https://www.fortuneidn.com/finance/suheriadi/ingin-transfer-bank-kenali-perbedaan-rtgs-sknbi-dan-bi-fast)

**Bank-specific limits (context)**
- From 12 Aug 2026, myBCA users can raise their daily transfer limit to Rp500 juta per BCA ID. — [Bisnis.com, Aug 2026](https://finansial.bisnis.com/read/20260814/90/1996151/bca-bbca-perluas-layanan-poket-valas-di-seluruh-cabang-limit-transfer-naik)
- Since 2023 the minimum BCA-to-BCA transfer has been Rp1. — [BCA](https://www.bca.co.id/en/informasi/news-and-features/2023/02/24/11/08/Informasi-Perubahan-Minimal-Transfer-Antar-Rekening-BCA)

**BNI Virtual Account (eCollection)**
- BNI's VA for corporates and institutions gives each payer a unique account number. Deposits are recorded automatically in the main (pooling) account. BNI markets this as making it easy to identify which partner or customer paid and as speeding up reconciliation. — [BNI Collection Management](https://bni.co.id/en-us/corporate/solusi-wholesale/collection-management)
- Payers can pay a BNI VA through ATM BNI, wondr by BNI, BNI Internet Banking, BNIDirect, SMS Banking, teller, Agen46, and other banks' channels.
  - In the ATM and IB flows the payer picks "Virtual Account Billing", enters a **16-digit** VA number, and confirms the billed amount shown.
  - A 2026 university payment guide says BI-FAST **cannot** be used to pay the VA. — [BNI VA payment guide (Poliupg 2026)](https://portalukt2026.poliupg.ac.id/Tata%20Cara%20Pembayaran%20BNI%20Virtual%20Account%20(eCollection)%20General_New_ChargeToBiller.pdf), per search summary
- Payment gateways offer both "static" VAs (one number, payable many times) and per-bill VAs. — [Faspay docs](https://docs.faspay.co.id/getting-started/faspay-bussiness/introduction/features/static-transaction/bni-va-static); [Xendit BNI VA](https://docs.xendit.co/docs/bni-virtual-account)
- BNIDirect's collection management includes autodebit, virtual account, cash collection, and autocollection. — [BNI news (BNIDirect)](https://bni.co.id/en-us/home/bni-news/news/articleid/21807), per search summary

**QRIS / QRIS Tuntas**
- The QRIS cap is Rp10 juta per transaction (PADG 24/1/PADG/2022). — [Kontan](https://keuangan.kontan.co.id/news/apakah-belanja-pakai-qris-ada-minimal-transaksi-simak-penjelasan-bank-indonesia)
- BCA set its QRIS payment limit to Rp10 juta from 3 Sep 2025. — [BCA](https://www.bca.co.id/en/informasi/news-and-features/2025/07/18/03/05/kenaikan-limit-transaksi-pembayaran-qris)
- QRIS Tuntas covers tarik tunai, transfer, and setor tunai between QRIS users. The 2023 fee figure is Rp6.500 per transaction for off-us transactions. — [BI QRIS Tuntas](https://www.bi.go.id/id/fungsi-utama/sistem-pembayaran/ritel/kanal-layanan/qris/qris-tuntas/default.aspx)
- From 1 Oct 2026 the MDR is 0% for transactions up to Rp100.000 for all merchant classes, and up to Rp500.000 for micro merchants (UMI). Above those thresholds it is 0.7% (0.3% for UMI). — [Infobanknews](https://infobanknews.com/bi-bebaskan-tarif-mdr-qris-transaksi-rp100-ribu-mulai-1-oktober-2026); [Kontan](https://keuangan.kontan.co.id/news/kebijakan-mdr-qris-0-berlaku-mulai-1-oktober-2026-btn-sesuaikan-tarif-merchant)

**E-wallet**
- DANA Premium can receive BI-FAST transfers up to Rp250 juta per transaction, but incoming funds from the DANA balance are capped at Rp40 juta per month under e-money regulation. — [DANA help](https://www.dana.id/help-center/article/what-is-the-maximum-transaction-limit-for-dana-premium-via-bi-fast)

**Bank codes**
- Interbank bank codes: BNI 009, BRI 002, Mandiri 008, BCA 014. — [Metro TV / CNN Indonesia list](https://www.cnnindonesia.com/ekonomi/20200702102114-83-519923/daftar-kode-bank-indonesia-untuk-transfer-online)

### Inferences
- Rail by amount for a typical division payment:
  - ≤ Rp250 juta: BI-FAST or intrabank.
  - Rp250 juta – Rp1 miliar: SKN.
  - Above that: RTGS.
  - Since payers are BNI divisions, **most payments are probably intrabank BNI overbooking**. On BNIDirect this would be "inhouse transfer" or similar — label unverified.
- SKN is batch-settled. A proof dated day D may appear in the statement later the same day or the next working day. Matching windows should be ≥ 1–2 working days for SKN and same-day for BI-FAST, RTGS, and intrabank.
- Scheduled ("Waktu Transfer yang diinginkan") and future-dated transfers mean a "success" screen may record an *instruction*, not a completed credit (see Q4).
- A VA per invoice, or per division, avoids the narrative problem entirely. But VA channel restrictions (e.g. no BI-FAST) need checking with BNI.

### Gaps
- **Maximum berita/keterangan length per rail and per app.** Not found in any official source. Unverified general knowledge, to be confirmed from BI-FAST and BNI technical specs or by testing in each app:
  - Bank apps commonly limit the narrative to roughly 30–50 characters.
  - SKN/RTGS remittance information fields in bank forms are often around 2×35 characters.
  - Receiving banks may **truncate** the narrative when they write it to the statement.
- BNI's own 2026 fee schedule for SKN, RTGS, BI-FAST, and online transfers: not found. Check bni.co.id tariff pages or the BNIDirect RIPLAY.
- BI's official current SKNBI/RTGS operating windows: the BI pages tried returned 404 and sources conflict. Confirm with BI PADG or with BNI cut-off times.
- Whether BI has changed the BI-FAST Rp250 juta cap or Rp2.500 fee in 2026: no evidence of a change. CIMB's RIPLAY dated April 2026 still shows both values.

## Q2. Fields on transfer receipts (wondr by BNI, BNI IB/BNIDirect, BRImo, Livin' by Mandiri, BCA, ATM)

### Takeaway
No bank publishes an official receipt-field specification. What sources confirm:
- A success screen or receipt appears after the transfer and can be shared.
- Users should check recipient name, bank, account, amount, date/time, status, and reference number.
- The narrative is entered *before* confirmation (BCA "Berita", BRImo "catatan").

Exact label wording and reference-number formats could not be verified from public sources. **Collect a labelled sample set of real receipts (with account numbers masked) from Koperasi finance before writing extraction regexes.**

### Cited Findings
- **BCA (myBCA / BCA mobile / KlikBCA)**
  - Input fields for BI-FAST: "Berita", "Tujuan Transaksi", "Layanan Transfer", "Waktu Transfer yang diinginkan".
  - KlikBCA also requires a KeyBCA Appli 2 response.
  - A success screen is displayed. — [BCA Edukatips](https://bca.co.id/en/informasi/Edukatips/2021/12/23/09/28/transfer-antarbank-kini-hanya-rp2500-via-bi-fast)
- **BRImo**
  - The user can enter a sending note ("catatan") on the confirmation page.
  - After success, a "bukti transaksi" appears in the app.
  - QRIS transfer produces a "receipt transaksi".
  - E-statements can be downloaded from the "Mutasi" menu. — [Antara](https://m.antaranews.com/amp/berita/4362647/cara-transfer-bri-ke-bca-lewat-m-banking-brimo-dengan-cepat); [IDX Channel QRIS transfer BRImo](https://www.idxchannel.com/banking/lengkap-ini-cara-mudah-transaksi-dengan-qris-transfer-lewat-brimo/all), per search summaries
- **Livin' by Mandiri**
  - The receipt ("resi") is viewed by swiping up after a successful transfer. It is also available under the transaction history.
  - Guidance says to check recipient name, destination bank, account number, amount, date and time, transaction status, and reference number "jika tersedia". — [Bank Mandiri transfer guide](https://www.bankmandiri.co.id/web/guest/livin/edukasi/cara-transfer-antar-bank); [ITERA blog](https://blog.itera.ac.id/?p=7872)
- **wondr by BNI**
  - wondr is BNI's current retail app; it had 6.8 juta users and Rp212 triliun in transactions by March 2025. — [Bisnis.com](https://finansial.bisnis.com/read/20250428/90/1872482/pengguna-wondr-by-bni-tembus-68-juta-transaksi-capai-rp212-triliun-per-maret-2025)
  - Legacy "BNI Mobile" is being wound down feature by feature. For example, the overseas-transfer feature in BNI Mobile closes from 16 Oct 2026. — [Bisnis.com, Sep 2026](https://finansial.bisnis.com/read/20260915/90/2004256/bni-tutup-fitur-transfer-luar-negeri-di-bni-mobile-mulai-16-oktober-2026)
  - **Expect receipts from both "BNI Mobile Banking" (old orange layout) and "wondr" layouts during 2026.**
  - Successful transfers also trigger in-app and SMS notifications. — [Metro TV](https://www.metrotvnews.com/read/KdZCj3r5-cara-transfer-dari-bni-ke-dana-cek-langkah-mudahnya)
- **BNIDirect (corporate)**
  - Transactions are authorized with dynamic PINs from physical or mobile tokens.
  - Payment management covers payroll, tax, inter-bank online, clearing, RTGS, and international transfers.
  - This is a maker/approver flow, so a division's "proof" may be a BNIDirect transaction report or status page, not a mobile screenshot. — [BNI news](https://bni.co.id/en-us/home/bni-news/news/articleid/21807); [IDX Channel BNIDirect](https://www.idxchannel.com/banking/cara-daftar-bni-direct-manfaat-dan-fiturnya/all), per search summary
- **ATM**
  - After an ATM transfer the customer takes the printed "bukti transaksi". — [IDX Channel (BRI ATM)](https://www.idxchannel.com/banking/cara-terbaru-transfer-bri-ke-bca-lewat-atm-hingga-brimo)

### Inferences
Candidate OCR field schema, based on common practice. **Labels are unverified and must be validated on samples.**

- `channel/app` — logo/header: wondr, BNI Mobile, BNIDirect, BRImo, Livin', myBCA, ATM.
- `status` — typical success words: "Berhasil", "Transaksi Berhasil", "Sukses", "Transfer Berhasil". Also detect "Diproses", "Pending", "Menunggu", "Terjadwal", "Gagal".
- `datetime` — dd/mm/yyyy or "16 Okt 2026", HH:MM:SS, WIB.
- `reference_no` — candidate labels: "No. Referensi", "No. Ref", "Ref No", "Nomor Transaksi", "ID Transaksi", "Journal/No. Jurnal". Formats differ per bank, so store as an opaque string. Do not validate length until samples are collected.
- `source_account` — often masked (e.g. `xxxx1234`). Store last 4 digits only.
- `beneficiary_name`, `beneficiary_bank`, `beneficiary_account` — the account should match the cooperative's receiving account. This is a strong check.
- `amount` — "Rp 1.234.567" or "IDR 1,234,567.00". Normalize Indonesian thousands "." and decimal ",".
- `fee` — "Biaya", "Biaya Admin".
- `total` — amount + fee. **Use `amount`, not `total`, for invoice matching.**
- `narrative` — "Berita", "Keterangan", "Catatan", "Deskripsi", "Remark".
- `transfer_method` — "BI-FAST", "Online", "SKN/Kliring", "RTGS", "Overbooking/Transfer sesama".
- `purpose` — "Tujuan Transaksi" (BI-FAST).
- ATM slips are thermal prints: low contrast, fading, and often photographed at an angle. Expect lower OCR confidence; deskew and binarize the image first.

### Gaps
- Exact Indonesian label strings for wondr, BNIDirect, BRImo, Livin', and BCA receipts: no official or reliable public description found. Requires a sample corpus.
- Reference-number formats and lengths (e.g. BI-FAST end-to-end ID, BNI journal number) are not published publicly. Unverified general knowledge: BI-FAST messages are ISO 20022-based and carry an end-to-end ID that is often around 30+ characters. Confirm with BNI or BI documentation.
- Whether wondr shows the narrative on the receipt, and how it masks account numbers: not verified.

## Q3. BNI account statement / mutasi rekening / e-statement: columns, sender/narrative display, export formats

### Takeaway
BNI mutasi is available in wondr / BNI Mobile ("Mutasi Rekening"), BNI Internet Banking ("Rekening → Mutasi Tabungan dan Giro"), SMS banking, ATM, branch rekening koran, and BNIDirect. Mobile mutasi shows sender name, date, and amount. BNIDirect offers downloadable or emailed balance/mutation reports, but **no public source confirms the export formats (PDF/CSV/MT940)**. The exact column layout and how the BI-FAST/SKN/RTGS narrative appears on BNI statements are not documented publicly.

### Cited Findings
- **BNI Mobile:** Rekeningku → Tabungan & Giro → Mutasi Rekening, choose a period. The mutasi list shows "nama pengirim, tanggal transaksi, dan jumlah transfer". — [IDX Channel](https://www.idxchannel.com/technology/begini-cara-mengetahui-siapa-yang-transfer-ke-rekening-kita-di-bni-mobile-banking)
- **BNI Internet Banking** (ib.bni.co.id): Rekening → Mutasi Tabungan dan Giro, then choose account number and period. — [IDX Channel](https://www.idxchannel.com/banking/cara-cek-mutasi-rekening-bni-bisa-online-dan-offline/all)
- **SMS:** send "HST" to 3346 (Rp1.000 charge). — same source
- **BNIDirect:** offers an information management feature covering balances, mutations, current accounts, and other reports, which can be downloaded or sent by email. It also offers API/open banking. — [BNI news](https://bni.co.id/en-us/home/bni-news/news/articleid/21807), per search summary
- **BNI Collection Management / ECOSmart:**
  - Provides a reporting dashboard and a mobile app showing billing status (unpaid, in process, history).
  - Payment confirmation is automatic and real-time for VA. — [BNI Collection Management](https://bni.co.id/en-us/corporate/solusi-wholesale/collection-management)
- **MT940 general structure** (not BNI-specific):
  - :20: reference, :25: account, :28C: statement number, :60F: opening balance, :61: transaction line, :86: narrative, :62F: closing balance. — [mBank MT940 spec](https://www.mbank.pl/pdf/msp-korporacje/bankowosc-elektroniczna/mbank-korpo-description-of-the-mt940-daily-statement-file-format.pdf)
  - BRI publishes an MT940 reconciliation spec for its API users (15-digit account, YYYYMMDDHHMMSS in :20:). This is BRI's implementation, not BNI's. — [BRI developer docs](https://developers.bri.co.id/en/docs/reconciliation-procedure), per search summary (page returned 403 on direct fetch)
- **Open-source parsers:**
  - DIRJEN-BOT/docai: "Extract structured transaction data from Indonesian bank e-statement PDFs (BCA, Mandiri, BNI, BRI) into clean JSON/CSV — deterministic… with built-in balance validation". Python, updated Aug 2026. — [GitHub topic bank-statement-parser](https://github.com/topics/bank-statement-parser)
  - kadekjayak/bca-parser scrapes KlikBCA mutasi rather than parsing PDFs. — [Packagist](https://packagist.org/packages/kadekjayak/bca-parser)
- **BCA:** e-Statement is available in myBCA for monthly periods. — [BCA e-Statement](https://www.bca.co.id/id/Individu/layanan/e-banking/mybca/e-statement)

### Inferences
- **For reconciliation, do not rely on OCR of the receipt. Rely on the cooperative's own statement data.** Preferred sources, in order:
  1. BNIDirect/ECOSmart download (CSV/Excel/MT940 if enabled).
  2. e-statement PDF parsed deterministically, e.g. with docai-style rules plus a running-balance check.
  3. Manual keying.
- Typical statement columns, **unverified for BNI** — confirm on a sample:
  - Posting date, value date, description/uraian (contains transfer type, sender name, and narrative, often concatenated and truncated), reference/journal no, debit, credit, balance.
- The sender name visible in mobile mutasi is a matching signal.
  - For interbank rails (BI-FAST/SKN/RTGS), the sender name comes from the originating bank's message.
  - For intrabank, it is the BNI account name, which is likely a division's account title rather than a person.
- Recommend requesting from BNI:
  1. A sample BNIDirect statement export in every available format.
  2. Confirmation of whether MT940 or CSV is enabled for the Koperasi's account.
  3. The max narrative length printed in mutasi for each rail.

### Gaps
- BNI statement column headers and description strings per rail (e.g. prefixes like "TRF", "BIFAST", "SKN", "RTGS", "INHOUSE"): no public source. Needs a sample statement.
- BNIDirect export formats: not confirmed publicly.
- How wondr e-statements are formatted (PDF layout): only a dubious third-party source was found, so it is not used.

## Q4. OCR / fraud risks with bukti transfer and verification practices

### Takeaway
Fake transfer proofs are widespread and now AI-generated. Both banks and media agree that **a receipt image is never proof of funds**: every payment must be confirmed against the recipient account's mutasi. Visual tamper cues (fonts, layout) are no longer reliable.

### Cited Findings
- Common scams:
  - Sending an edited screenshot.
  - Claiming "kelebihan transfer" (overpayment) and asking the victim to refund the difference. — [IDN Times](https://www.idntimes.com/business/economy/waspadai-taktik-penipuan-bukti-transfer-palsu-ini-ciri-cirinya-00-gshdq-hkmpf3); [Kompas TV, Sep 2025](https://www.kompas.tv/info-publik/619585/begini-cara-mendeteksi-bukti-transfer-palsu-agar-tidak-jadi-korban-penipuan)
- AI can produce convincing proofs complete with bank logo, recipient name, time, and amount. Polri issued an appeal about AI-made fake transfer proofs (April 2025). — [JatimTIMES](https://jatimtimes.com/baca/336043/20250425/174900/bukti-transfer-palsu-bermodus-ai-marak-beredar-ini-ciri-dan-cara-menghindarinya)
- Classic tamper cues: inconsistent or blurry fonts, untidy layout, odd text colour. But "with AI, the appearance can be perfect, so further verification is needed". — [Kompas TV](https://www.kompas.tv/info-publik/619585/begini-cara-mendeteksi-bukti-transfer-palsu-agar-tidak-jadi-korban-penipuan)
- Verification step: open the official bank app → Mutasi Rekening → check for the credit. If no matching credit exists, treat the proof as fake. Fraudsters use urgency, or "sudah transfer tapi belum masuk", to divert attention from verification. — [Kompas TV](https://www.kompas.tv/info-publik/619585/begini-cara-mendeteksi-bukti-transfer-palsu-agar-tidak-jadi-korban-penipuan); [IDN Times](https://www.idntimes.com/business/economy/waspadai-taktik-penipuan-bukti-transfer-palsu-ini-ciri-cirinya-00-gshdq-hkmpf3)
- Bank Mandiri runs a public warning page on fake-transfer-proof scams. — [Bank Mandiri](https://www.bankmandiri.co.id/waspada-modus-bukti-transfer-palsu-jangan-sampai-tertipu) (fetch failed; title only)
- Recent real cases (2026): a car was taken after a fake-proof transaction in Sidoarjo. — [Suara Surabaya, Jul 2026](https://www.suarasurabaya.net/kelanakota/2026/dugaan-modus-penipuan-bukti-transfer-palsu-mobil-suzuki-xl7-warga-sidoarjo-dibawa-kabur/)
- Bank apps let users pick a future "Waktu Transfer yang diinginkan" (scheduled transfer). — [BCA Edukatips](https://bca.co.id/en/informasi/Edukatips/2021/12/23/09/28/transfer-antarbank-kini-hanya-rp2500-via-bi-fast)
- Livin' guidance stresses confirming the status is actually successful, not "diproses" or "gagal". — [ITERA blog](https://blog.itera.ac.id/?p=7872)

### Inferences
Fraud risks are relatively low for intra-BNI divisions, but **error** risks (wrong amount, duplicate upload, pending or scheduled transfers) remain. Recommended rule set:

1. **The receipt is a claim, not settlement.** Invoice status goes `Klaim diunggah` → `Terverifikasi mutasi` only after a statement-line match. Never mark an invoice paid from OCR alone.
2. **Status gate.**
   - Reject or flag if OCR finds "Diproses/Pending/Menunggu/Terjadwal/Gagal/Dibatalkan".
   - Flag if the receipt date is in the future or the schedule date is later than the upload date.
3. **Duplicate detection.**
   - Hash the image (perceptual hash) and the tuple (reference_no, amount, date).
   - Reject a reused reference_no across invoices.
   - One statement line can settle only one claim, unless explicitly split by finance.
4. **Beneficiary check.** The beneficiary account and name must equal the cooperative's account. Mismatch means hard reject.
5. **Amount tolerance.**
   - Exact match by default.
   - Flag amount = invoice + fee (payer included the fee).
   - Flag amount = invoice − tax withholding (PPh 22/23 deductions are common for BUMN payers — unverified for this case; confirm with finance).
6. **Tamper signals** (supporting only):
   - EXIF/software tags, mismatched fonts, re-compressed regions.
   - PDF producer metadata for BNIDirect PDFs.
   - Never accept on visual checks alone.
7. **OCR quality.**
   - Indonesian number format: "." is thousands, "," is decimal.
   - Confuse-pairs: O/0, l/1, S/5, B/8.
   - Require amount to be confirmed both in digits and, where available, "terbilang".
   - Confidence threshold leads to manual review.

### Gaps
- No OJK/BI official guidance document on bukti-transfer verification was found; only bank and media articles.
- No data on fraud frequency in B2B or intra-group (BUMN) settings.

## Q5. Matching incoming transfers to invoices without narrative; suspense / unidentified-funds handling

### Takeaway
The robust fixes take the free-text narrative out of the critical path: (a) a **virtual account per division or per invoice** (BNI eCollection), or (b) a **unique amount (kode unik)** per open invoice. Both are proven Indonesian practice. Unmatched credits should go to a tracked suspense queue with aging and a resolution workflow, not sit indefinitely.

### Cited Findings
- **Kode unik:** random digits added to the end of the nominal to make each transfer distinct, so each one can be verified in detail. Used e.g. by Kitabisa.com. — [UIN SGD thesis abstract](https://digilib.uinsgd.ac.id/44102/2/2_abstrak.pdf)
- Customers are often asked to add a 3-digit kode unik to the last digits of the nominal for tracking. The drawback is confusion and "odd" amounts. — [ION Network blog](https://blog.ionnetwork.co.id/?p=7789)
- **Virtual account:** a pseudo-account created for transactions on behalf of a specific user. The number typically combines a company code (BIN/prefix) with a customer-unique code. — [ION Network blog](https://blog.ionnetwork.co.id/?p=7789)
- BNI eCollection VA: unique number per payer, automatic pooling, simpler identification and faster reconciliation. ECOSmart gives billing status and real-time payment confirmation. — [BNI Collection Management](https://bni.co.id/en-us/corporate/solusi-wholesale/collection-management)
- Static VA (reusable per customer) versus per-transaction VA. — [Faspay](https://docs.faspay.co.id/getting-started/faspay-bussiness/introduction/features/static-transaction/virtual-account-static)
- Deterministic statement parsing with running-balance validation is available as open source for BNI e-statements. — [GitHub docai](https://github.com/topics/bank-statement-parser)

### Inferences
Recommended matching ladder (highest confidence first):

1. **VA hit.** A credit to the VA assigned to a division or invoice matches automatically. Long-term target: request **static BNI eCollection VA per division**, or a billing VA per invoice. This removes dependence on "berita".
2. **Reference in narrative.** Regex for the invoice/receipt number pattern (e.g. `INV-YYYY-NNNNN`) in the statement description. Tolerate truncation, missing hyphens, and spaces; use fuzzy matching with Levenshtein distance ≤ 1–2.
3. **Uploaded claim + statement line.** OCR claim (amount, date, reference_no, beneficiary) matched to a statement credit on: exact amount, date within the window (D to D+1 working day; D+2 for SKN), and compatible sender name.
4. **Unique amount.**
   - Issue each open invoice with a small unique adder or reducer (e.g. Rp1–Rp999), kept unique among open invoices of similar size, and expire it when the invoice is settled.
   - For internal BNI divisions, finance must accept non-round amounts on the invoice.
   - Alternative: kode unik of a few rupiah, kept as "pendapatan lain-lain" or credited back.
5. **Sender-name + amount heuristic.** Match the division account name (a maintained alias table: account-name → division/cost-center) plus an amount equal to an open invoice or a sum of open invoices (subset-sum for bulk payments). Always route to **human confirmation**.
6. **Unmatched** → suspense ("dana belum teridentifikasi" / "titipan").

Suspense handling practices (general accounting practice; not sourced to an Indonesian cooperative standard):
- Record every unidentified credit as a line item with statement reference, date, amount, sender name, and raw description.
- Age the items (0–30, 31–90, >90 days).
- Each week, send an outreach list to division finance contacts and ask them to claim items by uploading a proof.
- Require a two-person approval to reclassify from suspense to receivable.
- Never net suspense against unrelated receivables.
- Report the suspense balance in monthly management reports.
- Write off or take to "pendapatan lain-lain" only under policy and with RAT/Pengurus approval.

Process fixes for the SOP pain point:
- Put the **invoice number and exact payment instructions (rekening, VA, required berita format)** on every invoice.
- Ask divisions, via BNI internal channels, to include the invoice number in BNIDirect "remark/berita".
- Track a per-division "missing narrative" rate.
- Since payments go through a centralized head-office transfer system, ask the head-office payment unit to send a **remittance advice** (list of invoices paid per transfer, e.g. an Excel attachment or email). This is the standard B2B practice and solves bulk payments.

### Gaps
- No Indonesian cooperative-specific (Kemenkop/SAK EP / SAK ETAP) guidance found on suspense-account aging or write-off. Confirm with the cooperative's accountant or auditor.
- Whether BNI will issue eCollection VAs to a Koperasi Unit Toko, the setup fees, and whether internal BNI divisions can pay VAs via BNIDirect (likely yes, since BNIDirect is a listed VA payment channel): needs BNI relationship-manager confirmation.
- Whether the head-office centralized payment system sends remittance advice or allows structured remarks: needs confirmation with the Koperasi or BNI.
