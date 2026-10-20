# Indonesian Context and Accessibility for Unit Toko (UX writing, formatting, devices, WhatsApp, adoption, WCAG 2.2)

Scope: guidance for a Bahasa Indonesia, internal B2B Next.js web app for a Koperasi shop serving BNI divisions. Users: shop staff (low-to-moderate IT maturity, now on WhatsApp, handwritten notes, Excel), bank-division PICs (laptop + phone), cooperative finance staff. Research date: 19 Oct 2026.

## 1. Bahasa Indonesia UX writing: register (Anda vs kamu), terminology, buttons, errors, loanwords

### Takeaway
For a business or institutional app, the documented Indonesian convention is **"Anda" (capital A), formal but not stiff ("formal tapi tidak kaku")**. Use short, direct, verb-first labels, plain everyday words instead of technical or bureaucratic jargon, and error messages that say what happened and what to do next without blaming the user. "Kamu" and playful lines ("Oops, belum bisa nih") belong to consumer brands like Gojek, not to a cooperative/bank B2B tool. Public writing guides from Tokopedia Unify, GoTo Asphalt, Traveloka Momentum and BCA/BRI/Mandiri could not be found. The best available Indonesian sources are government ones (INA Digital / IDDS, Seragam by INA Digital Edu) and Telkom's Legion.

### Cited Findings
- **INA Digital Design System (IDDS, the national government design system)** has a "Penulisan" foundation that picks a voice through a persona ("sosok"). It gives contrasting reference personas:
  - INA Digital Edu's **"Tenaga Operasional"**: efficient, neutral, measured, solution-oriented. Style is "formal ringan, langsung, dan akurat". Examples: "Data belum bisa disimpan. Periksa kembali kolom yang belum diisi." and "Pengajuan Anda sudah diterima dan sedang diproses."
  - Gojek's **"Cerdikiawan"**: casual, uses "kamu". Examples: "Oops, belum bisa nih. Coba lagi nanti, ya."; "Pembayaran kamu aman…"
  — [INA Digital – Penulisan](https://design.inadigital.go.id/foundation/writing/)
- IDDS matches tone to the situation:
  - errors and system failures: empathetic and clear
  - warnings and deadlines: firm and neutral, e.g. "Koneksi sedang tidak stabil. Coba lagi setelah beberapa saat."
  - official announcements: formal and credible, with an explicit date, e.g. "…mulai 12 Desember 2025…"
  — [INA Digital – Penulisan](https://design.inadigital.go.id/foundation/writing/)
- IDDS inclusivity rules:
  - choose easy words and avoid technical or bureaucratic terms
  - make text work with screen readers
  - avoid phrases that assume ideal conditions, such as "klik saja" or "sangat mudah"
  - don't convey status by colour alone; e.g. add the text "Status: Aktif" next to a green indicator
  — [INA Digital – Penulisan](https://design.inadigital.go.id/foundation/writing/)
- Seragam (INA Digital Edu) sets two umbrella styles for all its products: **"Lugas"** (direct, no flowery wording) and **"Formal tapi tidak kaku"** (polite and professional, but not fully "baku" at the expense of clarity). Its top principle is "clarity above all". It defines three personas: Rekan (casual), **Tenaga Operasional** (neutral-formal, very direct, may use technical terms), and Atasan (very formal) — [Seragam – Gaya Bahasa](https://seragam.belajar.id/gaya-bahasa)
- Seragam's inclusive-writing table, as avoid → prefer pairs — [Seragam – Inklusivitas](https://seragam.belajar.id/inklusivitas):
  - "Modifikasi" → "Ubah" / "Edit"
  - "Balik" → "Kembali" / "Ke [nama halaman]"
  - "Singkirkan/Buang" → "Tutup/Batalkan/Sembunyikan"
  - "Pengguna" → "Anda/Kamu" (unless the text is about a role, e.g. an admin)
  - "Perbarui data" → "Perbarui data (1 dari 3 tahap)"
  - "Terjadi server error timeout… hubungi CS" → "Terjadi kendala sistem. Kunjungi Pusat Bantuan atau coba lagi dalam beberapa saat."
  - Spell out abbreviations on first use.
- Seragam on screen readers and visual cues — [Seragam – Inklusivitas](https://seragam.belajar.id/inklusivitas):
  - Screen-reader users often hear only the first sentence, and fast. Put the action first, e.g. "Muat ulang halaman. Sepertinya ada kendala internet." rather than leading with the problem.
  - Don't rely on position or colour cues ("Tekan tombol di kanan atas", "klik tombol biru"). Name the control instead, e.g. "tekan tombol Mulai".
  - Address people by name rather than by assumed title or gender.
- Telkom Indonesia's Legion design-system writing guideline:
  - principles: "Be clear" and "Speak like a human"
  - text scans best at about 40 characters or fewer per line and three lines or fewer
  - read the title, description and buttons aloud together to check they flow
  - check terminology consistency throughout the experience
  — [Legion (Telkom) Writing Guideline](https://legion.digitaltelkom.id/content/writing-guideline)
- Localization style guides:
  - Mozilla translates "you" as "Anda" for its formal products (Firefox, Thunderbird) and uses semi-formal only for marketing and social media — [Mozilla l10n Indonesian style guide](https://mozilla-l10n.github.io/styleguides/id/index.html)
  - Use "Anda" with a capital A, and drop the pronoun in instructions (e.g. "Buka file"). **Do not switch between Anda and kamu within a flow.** Use "kami" only when the company or support team really does something, not in every automated message — [FontLab Indonesian style guide](https://fontlab.dev/vexy-fontlab-writing-styleguide/fl1992mk/languages/id/)
  - Formal register uses saya/Anda/kami, keeps verb affixes (except in imperatives), avoids colloquial lexicon, and softens imperatives with "tolong", "coba" or "harap" — [Unbabel Indonesian guidelines](https://help.unbabel.com/hc/en-us/articles/360022961334-Language-Guidelines-Indonesian)
  - Microsoft's Indonesian style guide prefers a clear, friendly, conversational style over formal technical language, while matching technical terms to the audience — [Microsoft Indonesian Localization Style Guide (PDF)](https://download.microsoft.com/download/c/2/8/c2804555-f6ed-4db9-9e3b-ddf754159bd3/ind-idn-StyleGuide.pdf)
  - Note: Microsoft leans more casual than Mozilla and IDDS; both still avoid stiff bureaucratic language.
- An Indonesian UX-writing case study (e-commerce 500 error):
  - limits used: heading ≤45 characters, description ≤120, button ≤25
  - casual version: "Oops, terjadi kesalahan teknis… kamu…"
  - more neutral alternative: "Kendala pada sistem… Silakan coba lagi beberapa saat"
  — [Medium – Nurul Fayza Rahman](https://medium.com/@nurulfayza7/ux-writing-case-study-improving-error-500-message-on-e-commerce-app-0d00fcbb1264)

### Inferences
- **Recommended voice for Unit Toko: IDDS/Seragam "Tenaga Operasional".** Use "Anda", direct, formal-light, no slang, no "Oops/nih/yuk", little or no emoji.
  - Bank-division PICs at BNI and cooperative finance staff expect institutional politeness. Shop staff benefit from directness more than from playfulness.
  - In WhatsApp message templates and printed documents, use "Bapak/Ibu" as the salutation, e.g. "Yth. Bapak/Ibu [Nama]" (an inference from the formal-register conventions above).
- **Pronoun rules:**
  - Prefer no pronoun in labels and instructions: "Isi jumlah barang" rather than "Anda harus mengisi jumlah barang".
  - Use "Anda" only where needed, never "kamu".
  - Use "kami" only for real human actions by the shop or koperasi ("Pesanan Anda kami terima").
- **Buttons** — imperative verb + object, sentence case, 1–3 words, and the same verb used in the dialog title:

  | Do | Don't |
  |---|---|
  | "Simpan pesanan" | "Submit" |
  | "Kirim pesanan" | "OK" |
  | "Setujui" / "Tolak" | "Proses" (vague) |
  | "Cetak Surat Jalan" | "Klik di sini" |
  | "Unggah bukti transfer" | "Upload Bukti TF" |
  | "Batalkan pesanan" | "Hapus" (for a cancel action) |

  Dialog title "Batalkan pesanan ini?" pairs with the buttons "Batalkan pesanan" / "Kembali".
- **Error-message pattern** (what happened, then how to fix, no blame, the field named, real values):

  | Do | Don't |
  |---|---|
  | "Jumlah melebihi stok. Stok tersedia: 12 dus." | "Error: qty invalid" |
  | "Nomor HP belum lengkap. Contoh: 0812 3456 7890." | "Format salah!" |
  | "Koneksi terputus. Data Anda tersimpan di perangkat dan akan dikirim saat online." | "Network Error 503" |

  Use "belum bisa…", which implies recoverable, rather than "gagal…" where appropriate (an inference from IDDS's "Data belum bisa disimpan" example).
- **Loanwords and terminology:**
  - Keep established business and document terms as the user knows them: Surat Jalan, faktur pajak, bukti transfer, PIC, invoice/tagihan (pick one), stok, kasir.
  - Prefer Indonesian UI verbs (Simpan, Ubah, Hapus, Unggah, Unduh, Cetak, Bagikan, Cari, Saring/Filter).
  - Avoid developer jargon: "server", "timeout", "sync", "payload", "status 500".
  - Keep a **glossary** (one term per concept). For example, choose either "Pesanan" or "Order" app-wide, never both. Choose "Tagihan" or "Invoice" consistently.
  - The specific KBBI/Badan Bahasa equivalents (unggah, unduh, surel, daring, kata sandi) were not verified against a source in this session. Check them in KBBI before finalising.

### Gaps
- No public UX-writing guideline was found for Tokopedia Unify, GoTo Asphalt, Traveloka Momentum, Bukalapak, or the BCA/BRI/Mandiri apps. Searches returned generic Western guides. Their microcopy conventions are known only from product observation, which was not done here.
- No SPBE- or INApas-specific writing standard was found. IDDS (design.inadigital.go.id) appears to be the government's current reference.
- No Indonesian user-research study was found that compares "Anda" with "kamu" preference among B2B or office users. The recommendation rests on style-guide consensus, not experiments.

## 2. Formatting rules: IDR, numbers, dates, time zones, phone numbers, NIK/NPWP

### Takeaway
Under EYD V, money is written **"Rp" + amount with no space and no period after Rp**: dot (.) separates thousands, comma (,) marks decimals, so Rp1.250.000 or Rp1.250.000,00. Time uses a dot between hours and minutes (pukul 14.30 WIB). Day and month names are capitalised. The browser's `Intl` API with `id-ID` locale produces correct separators, month abbreviations and WIB/WITA/WIT labels. However, it **inserts a non-breaking space after "Rp"** ("Rp 1.250.000,00"), which differs from EYD, so the team must pick and enforce one convention. NPWP is moving from 15 to 16 digits (NIK for resident individuals); the 15-digit format is still legally valid.

### Cited Findings
- EYD V (Badan Bahasa, SK 16 Aug 2022):
  - Abbreviations of currencies take no period: "Rp" (rupiah) — [EYD V SK PDF](https://badanbahasa.kemendikdasmen.go.id/resource/doc/files/SK_EYD_Edisi_V_16082022.pdf); [EYD – Singkatan](https://ejaan.kemdikbud.go.id/eyd/penulisan-kata/singkatan-dan-akronim/)
  - Numbers are written as digits for money values, e.g. Rp5.000,00, US$3,50, 5% — [EYD – Angka dan Bilangan](https://ejaan.kemdikbud.go.id/eyd/penulisan-kata/angka-dan-bilangan/)
  - In legal documents, deeds and receipts ("kuitansi"), a number may be written in digits followed by words, e.g. "Rp900.500,50 (sembilan ratus ribu lima ratus rupiah lima puluh sen)" — [EYD – Angka dan Bilangan](https://ejaan.kemdikbud.go.id/eyd/penulisan-kata/angka-dan-bilangan/)
- EYD V on the dot — [EYD V SK PDF](https://badanbahasa.kemendikdasmen.go.id/resource/doc/files/SK_EYD_Edisi_V_16082022.pdf):
  - The dot separates thousands for quantities (13.000 pulau; Rp225.000.000.000,00).
  - It is **not** used in numbers that aren't quantities (years such as 1998, page numbers, account or ID numbers).
- Spacing after Rp:
  - The correct form is Rp with no period and no space before the amount, e.g. Rp2.000.000,00 — [Kompas.com (citing EYD and Balai Bahasa Kalsel)](https://www.kompas.com/skola/copy/2023/06/13/080000669/bagaimana-penulisan-rp-rupiah-yang-benar-)
  - Large amounts may be written with words for readability ("55 juta rupiah", "5 miliar rupiah"). House style may differ: Kompas itself puts a space after Rp — [Kompas.com 2024](https://www.kompas.com/skola/read/2024/01/22/070000069/bagaimana-cara-penulisan-bilangan-mata-uang-)
- EYD V on time: the dot separates hours, minutes and seconds, e.g. "pukul 01.35.20" — [EYD – Tanda Titik](https://ejaan.kemdikbud.go.id/eyd/penggunaan-tanda-baca/tanda-titik/). PUEBI says the same — [PUEBI PDF](http://digilib.upnb.ac.id/files/original/7/2755/Pedoman_Umum_Ejaan_Bahasa_Indonesia_[PUEBI].pdf)
- Capitalisation: names of years, months, days and holidays take a capital letter (bulan Agustus, hari Jumat) — [EYD/PUEBI (UGM copy)](https://luk.staff.ugm.ac.id/ta/Suwardjono/EYD.pdf)
- Time zones: a court-writing style guide writes times as "08.00 WIB, 23.00 WIT", **with a space between the number and WIB/WITA/WIT** — [PTA Ambon – Format Penulisan Putusan](https://www.pta-ambon.go.id/portal/MATERI%20BIMTEK/FORMAT%20PENULISAN%20PUTUSAN-%20ketua.pdf)
- **Verified `Intl` output** (Node v24.3.0, ICU 73.2, `id-ID` locale; run during this research):

  | Call | Output |
  |---|---|
  | `NumberFormat('id-ID',{style:'currency',currency:'IDR'})` | `Rp 1.250.000,00` (code points `52 70 a0 31` — the separator is **U+00A0 NBSP**, not a normal space) |
  | same, with `maximumFractionDigits:0` | `Rp 1.250.000` |
  | `NumberFormat('id-ID')` on 1234.5 | `1.234,5` |
  | percent | `12,5%` |
  | compact | `1,3 jt`; compact long → `1,3 miliar` |
  | `dateStyle:'medium'` | `17 Okt 2026` |
  | `dateStyle:'full'` | `Sabtu, 17 Oktober 2026` |
  | time | `14.30` |
  | with `timeZoneName:'short'` | `14.30 WIB` (Asia/Jakarta), `15.30 WITA` (Asia/Makassar), `16.30 WIT` (Asia/Jayapura) |
  | relative −1 day | `kemarin` |

  Short months: Jan, Feb, Mar, Apr, Mei, Jun, Jul, Agu, Sep, Okt, Nov, Des. Short weekdays: Min, Sen, Sel, Rab, Kam, Jum, Sab. (Source: local `Intl` execution against ICU/CLDR data. Output can vary slightly by browser and ICU version.)
- Phone numbers: WhatsApp click-to-chat needs the full international number with no "+", zeroes, brackets or dashes, e.g. `https://wa.me/1XXXXXXXXXX`, not `+001-(XXX)…` — [WhatsApp Help Center – click to chat](https://faq.whatsapp.com/5913398998672934/?locale=en_US)
- NPWP and related identifiers:
  - PMK 112/PMK.03/2022 sets three rules: NIK serves as the NPWP for resident individuals, a 16-digit NPWP applies to other taxpayers, and the **NITKU** (22 digits: the 16-digit NPWP plus a 6-digit sequence number) replaces branch NPWPs.
  - Full implementation moved to 1 July 2024 (PMK 136/2023).
  - Under PER-6/PJ/2024, documents carrying the 15-digit NPWP issued since 1 July 2024 have the same legal force.
  - DJP said NIK-as-NPWP would roll out gradually, pending Coretax.
  — [DDTC News – NIK, NPWP 16 digit, NITKU](https://news.ddtc.co.id/literasi/kamus/1803684/apa-beda-nik-sebagai-npwp-npwp-16-digit-dan-nitku); [DDTC – layanan wajib NIK/NPWP16](https://news.ddtc.co.id/berita/nasional/41787/catat-ini-layanan-yang-harus-pakai-nik-atau-npwp-16-digit-mulai-2024); [pajak.go.id](https://pajak.go.id/id/berita/gelar-instagram-live-kpp-sukoharjo-bahas-nik-jadi-npwp)

### Inferences
- **Currency rule for Unit Toko:**
  - Display "Rp1.250.000" (no space, no decimals) in UI lists, cards and totals. IDR in everyday commerce has no minor unit in practice. EYD's ",00" suffix is optional for UI and fits formal documents (kuitansi, faktur).
  - Implement as `new Intl.NumberFormat('id-ID',{style:'currency',currency:'IDR',maximumFractionDigits:0}).format(n).replace(/ /, '')`, or keep the NBSP if the team prefers screen-reader and wrap safety. Either way, **apply one helper everywhere** and never hand-concatenate.
  - Never use compact notation ("1,3 jt") for money in finance views, because it hides precision. It is acceptable only for dashboard KPI tiles with the full value in a tooltip.
  - Do: "Total: Rp1.250.000". Don't: "Rp. 1,250,000", "IDR 1250000", "Rp 1.250.000,-".
  - On printed Surat Jalan or kuitansi, add the amount in words ("terbilang"): "Rp1.250.000,00 (satu juta dua ratus lima puluh ribu rupiah)".
- **Number input:**
  - Accept digits and auto-format with dots as the user types (or on blur), and store an integer.
  - Never interpret "." as a decimal point. "1.250" typed by an Indonesian user means one thousand two hundred fifty.
  - Use `inputmode="numeric"` for quantities and money, plus a fixed "Rp" prefix outside the input.
  - Put units after quantities ("12 dus", "5 kg").
- **Dates and times:**
  - Lists: "17 Okt 2026".
  - Detail views and documents: "Sabtu, 17 Oktober 2026".
  - Time: "14.30 WIB". Always show the zone, because PICs or branches may be outside WIB.
  - Relative time for recent status ("kemarin", "2 jam lalu"), with the absolute time on hover or tap.
  - Avoid numeric-only dates like 10/11/2026, which are ambiguous between DD/MM and MM/DD.
  - Store UTC and render in Asia/Jakarta by default.
- **Phone numbers:**
  - Accept "0812-3456-7890", "+62 812 3456 7890" and "62812…".
  - Normalise to E.164 (+628123456789) for storage and to "628123456789" for wa.me links, by dropping the leading trunk "0" and adding 62.
  - Display "0812-3456-7890", or "+62 812-3456-7890" for external contacts.
- **NIK/NPWP:**
  - Accept 15-digit (legacy, display 00.000.000.0-000.000) and 16-digit NPWP, plus 22-digit NITKU for branches. Do not show thousand-separator dots in these IDs, consistent with EYD's no-dots rule for non-quantities.
  - Mask on screen by default (e.g. NIK "3174 **** **** 0012"), with reveal-on-tap for authorised finance roles. Show the full value only on tax documents (faktur pajak).

### Gaps
- The search for UU 27/2022 (Pelindungan Data Pribadi) display or masking obligations failed (tool error). Specific legal requirements for masking NIK/NPWP in UIs are unverified, so the masking advice above is best practice, not a cited legal requirement.
- Current (late 2026) Coretax status and whether 15-digit NPWP is still accepted for e-Faktur were not confirmed.
- Exact e-Faktur / faktur pajak number formatting rules were not researched.

## 3. Indonesian device and connectivity realities (2025–2026) and implications

### Takeaway
Indonesia is an Android-majority, mobile-first, price-sensitive market. About 80–85% of mobile web traffic is Android, and the most common mobile viewport is **360×800 CSS px**. Entry-level phones dominate sales (more than half sold under US$200; about 55% under US$150 in Q3 2025), and a long tail of devices still runs Android 10–12. Median mobile speeds improved to about 45 Mbps in 2025, but rural users and small businesses still name unstable internet as a top barrier. Build for 360px width, low CPU and RAM, flaky networks, and laptop use by office PICs. About 51% of internet users also use a laptop or desktop, and desktop is about 35% of web traffic.

### Cited Findings
- StatCounter mobile OS share in Indonesia:
  - February 2026: Android 85.23%, iOS 14.65% — [StatCounter](https://gs.statcounter.com/os-market-share/mobile/Indonesia); [Fortune Indonesia, Mar 2026](https://www.fortuneidn.com/tech/android-vs-iphone-mana-lebih-banyak-penggunanya-d7w03-00-fykp7-9h1kwc) (Android above 80% since 2020)
  - September 2026: Android 79.16%, iOS 20.79% — [StatCounter](https://gs.statcounter.com/os-market-share/mobile/indonesia)
  - The monthly snapshots vary. Treat Android as roughly 80–85%.
- StatCounter mobile screen resolutions in Indonesia, January 2026 — [StatCounter](https://gs.statcounter.com/screen-resolution-stats/mobile/indonesia):

  | Resolution | Share |
  |---|---|
  | 360×800 | 12.7% |
  | 393×873 | 5.68% |
  | 385×854 | 4.48% |
  | 360×806 | 4.08% |
  | 414×896 | 3.35% |
  | 384×832 | 3.12% |

- Android versions in Indonesia, January 2026: 15.0 19.79%, 13.0 15.7%, 14.0 15.09%, **11.0 14.02%**, 12.0 12.64%, **10.0 6.11%** — [StatCounter](https://gs.statcounter.com/os-version-market-share/android/mobile-tablet/indonesia/n%D0%9A%D0%B0%D0%BA)
- DataReportal Digital 2026 Indonesia (data to October 2025) — [DataReportal Digital 2026 Indonesia](https://datareportal.com/reports/digital-2026-indonesia); full deck mirror: [studylib copy of the report](https://studylib.net/doc/28964392/2026-gdr-indonesia):
  - 230M internet users (80.5% of the population)
  - 98.0% of internet users go online via mobile phones; 51.2% via laptop or desktop
  - web traffic share (StatCounter, August 2025): mobile 64.42%, laptop/desktop 35.02%, tablet 0.56%
  - Ookla medians (three months to August 2025): mobile download 45.01 Mbps (up 53.1% YoY), upload 16.02 Mbps, latency 22 ms; fixed download 39.88 Mbps
  - 331M cellular connections, 96.6% of them broadband (3G/4G/5G)
- Smartphone market:
  - Counterpoint Q2 2025: demand concentrated in the entry-level segment (<$150), which grew 3% YoY while mid and premium tiers declined. 5G reached 35% of shipments — [Counterpoint](https://counterpointresearch.com/en/insights/indonesia-smartphone-market-q2-2025)
  - Omdia Q3 2025: TRANSSION (Infinix/TECNO) led Indonesia with 21%, then Xiaomi 19%, OPPO 18%, vivo 16%, Samsung 15%. More than half of smartphones sold were priced below US$200 — [TelecomLead (Omdia data)](https://telecomlead.com/smart-phone/indonesia-smartphone-market-sees-strong-competition-in-q3-2025-as-transsion-leads-shipments-123517); [Omdia press release](https://omdia.tech.informa.com/pr/2025/nov/southeast-asia-smartphone-shipments-slip-1percent-in-3q25-as-vendors-face-mounting-cost-pressures)
  - A secondary report citing Counterpoint says sub-US$150 devices were 55% of Q3 2025 shipments (up 42% YoY), and that "entry-level" now means IDR 1.5–2.5 million — [time.news](https://time.news/hp-entry-level-shifts-price-idr-1-5-million/). This is a secondary aggregator; its vendor ranking (Samsung first at 20%) **conflicts** with Omdia's (TRANSSION first). Different trackers, different methods.
  - IDC: more than 60% of ASEAN smartphones are priced below US$200. Memory-chip shortages in 2026 are expected to raise prices and shrink the budget segment — [TelcoNews Asia (IDC)](https://telconews.asia/story/southeast-asia-smartphone-value-rises-as-shipments-stall)
- Rural MSME POS needs study (25 MSMEs): unstable internet and subscription cost were the main barriers. The most-wanted features were **offline mode (4.52/5)** and **simple interface (4.04/5)** — [Iwan, Urindo journal](https://ejournal.urindo.ac.id/index.php/administrasimanajemen/article/download/7016/2798)

### Inferences
- **Breakpoints:** design mobile-first at **360 CSS px** (test at 360×640 for small viewports with the keyboard open), with layouts up to about 414px. Use a proper desktop layout (≥1280px) for PICs and finance staff, who will do approval and reconciliation work on laptops.
- **Performance budget**, inferred from low-end Android prevalence and general web-performance practice, not from an Indonesian benchmark:
  - Initial JS for a route ≤ about 170 KB gzipped.
  - Target LCP < 2.5 s on a mid-tier Android over 4G.
  - Prefer server components / SSR in Next.js, avoid heavy client charting libraries on mobile routes, and lazy-load reports.
- **Offline tolerance:**
  - Keep form drafts locally (order entry, delivery confirmation) and retry submissions idempotently.
  - Show an explicit online/offline banner in plain Indonesian ("Anda sedang offline. Perubahan akan dikirim otomatis saat koneksi kembali.").
  - Never lose typed data on a network error.
- **Uploads** (bukti transfer, signed Surat Jalan photos): compress on the client (resize to about 1600px long edge, JPEG/WebP at about 0.7 quality, typically about 200–400 KB) and show progress. Allow a camera-capture input (`accept="image/*" capture`).
- Do not assume iOS-only or desktop-only features. Test on Chrome for Android and Samsung Internet, plus one older Android (10–11) device.

### Gaps
- No reliable 2025–2026 figure was found for Indonesian mobile data price per GB (cable.co.uk or similar), so data-cost claims are unquantified.
- No device RAM distribution for Indonesia was found.
- No StatCounter figure for Indonesian mobile browser share (Chrome vs Samsung Internet vs others) was gathered.

## 4. WhatsApp-native habits and appropriate WhatsApp integration

### Takeaway
WhatsApp is effectively universal in Indonesia: about 9 in 10 internet users use it monthly, and it is the most-used and favourite platform. Small businesses and cooperative shops already take orders through it. The research also documents its failure modes: slow admin replies, messages ignored, and no structured records. The right pattern is to **keep the system of record in the app and use WhatsApp as the notification and sharing channel**, via deep links (wa.me) with pre-filled text and links back to the app, not as the place where the order lives.

### Cited Findings
- WhatsApp usage:
  - DataReportal Digital 2026: WhatsApp is the most frequently used social platform, with about nine in ten users active monthly, and the favourite social app — [Campaign Brief Asia on Digital 2026 Indonesia](https://campaignbriefasia.com/2025/11/06/digital-2026-indonesia-reveals-social-media-user-identities-increased-26-to-180-million/); [Rilis.id](https://www.rilis.id/Ekonomi/Berita/Dominasi-WhatsApp-dan-TikTok-Pengguna-Medsos-Indonesia-Capai-180-Juta-vnnXgvq)
  - GWI Q2 2025 self-declared monthly use: WhatsApp 90.8% — [ardancakep.id (citing GWI)](https://ardancakep.id/tren-penggunaan-media-sosial-di-indonesia-2026/); secondary source
- An Indonesian MSME ERP/POS case (Dimsum Surga): records were manual and orders depended on WhatsApp. Customers waited for admin replies, messages were often not answered promptly or were ignored, and managers struggled to monitor stock and cash — [Jurnal Unpad (JANE)](https://jurnal.unpad.ac.id/jane/article/viewFile/69427/27376)
- WhatsApp click-to-chat format — [WhatsApp Help Center](https://faq.whatsapp.com/5913398998672934/?locale=en_US):
  - `https://wa.me/<number>`: works on phone and WhatsApp Web without saving the contact
  - `https://wa.me/<number>?text=<urlencoded>`: pre-filled message
  - `https://wa.me/?text=<urlencoded>`: opens a contact picker to share the text with anyone
- A "+" in wa.me links worked on mobile apps but failed on WhatsApp Web and desktop in one practitioner's test. Plain country code + number worked everywhere — [Technically Product](https://www.technicallyproduct.co.uk/messaging/international-number-formats-that-do-and-dont-work-in-whatsapp-click-to-chat/)

### Inferences
- **Recommended integration patterns** (no WhatsApp Business API needed for the MVP):
  - **"Bagikan ke WhatsApp"** button on order, Surat Jalan and invoice pages. It opens `wa.me/?text=` with a short, formal summary plus a link back to the app page, e.g. "Yth. Bapak/Ibu, pesanan PO-2026-0142 (Divisi XYZ) sudah dikirim pukul 14.30 WIB. Detail & Surat Jalan: https://…".
  - **"Hubungi via WhatsApp"** next to the shop's or PIC's contact, using `wa.me/62812…?text=Halo, saya ingin menanyakan pesanan PO-2026-0142`. Put this help link in a consistent position, which also satisfies WCAG 3.2.6 Consistent Help.
  - **Chat-like status timeline** in the app (Diterima → Diproses → Dikirim → Diterima PIC → Lunas), with timestamps and actor names. This mirrors the conversational status updates people are used to in WhatsApp, inside the system of record.
- **Don't:**
  - put prices, NIK/NPWP or bank details in pre-filled WhatsApp text, because it persists in chats
  - make WhatsApp the only notification channel
  - auto-send WhatsApp messages without the user tapping, since a wa.me link always needs a user action
  - imply WhatsApp is official proof. The app record and PDF are the evidence; WhatsApp is only the pointer.
- Automated WhatsApp notifications later (WhatsApp Business Platform templates) need Meta business verification and per-conversation costs. These were not researched here (see Gaps).
- Use the Web Share API (`navigator.share`) on mobile as an alternative that lets users choose WhatsApp, WhatsApp Business or email. This is a technical suggestion, not sourced.

### Gaps
- No Indonesia-specific B2B data was found on WhatsApp Business adoption (e.g. Meta SMB statistics), and no Indonesian B2B product case study of WhatsApp-notification design.
- WhatsApp Business Platform pricing and template-approval rules for Indonesia (2026) were not researched.

## 5. Adoption for low-IT-maturity staff (SME/cooperative ERP/POS research)

### Takeaway
Indonesian studies consistently find that cooperatives and SMEs still run on notebooks, Excel and WhatsApp. Adoption is driven by **low perceived complexity, clear relative advantage, top-management support, vendor/mentor support and government support**, and blocked by digital-skill gaps (cooperative staff readiness is only 30–40%), fear of errors and data loss, switching costs and unstable internet. Successful pilots paired a simple, mobile-friendly tool with hands-on training, written SOPs and continued mentoring.

### Cited Findings
- TOE study of 177 Indonesian cooperatives (2026, SEM-PLS):
  - Government Support, Organizational Innovativeness, **Perceived Complexity**, Relative Advantage, **Top Management Support** and **Vendor Support** affect use behaviour.
  - Citing 2024 Ministry of Cooperatives data: cooperative HR readiness for digitalisation is only **30–40%**, because most administrators are older and less tech-proficient.
  — [Putri et al., Cogent Business & Management 2026](https://www.tandfonline.com/doi/full/10.1080/23311975.2026.2700803)
- A review of cooperative digital transformation reports a 2023 Kemenkop UKM survey finding that only **15%** of active cooperatives fully use digital management systems. Barriers: HR, cost, network. It recommends gradual, contextual digitalisation with training and mentoring — [Nashoha et al., 2025](https://doi.org/10.60145/jcp.v2i7.485). The 15% is cited secondhand; the original survey was not retrieved.
- Interviews with 12 non-adopting SMEs found most record in notebooks or Excel. Barriers: digital skills, security risk, switching costs, provider image, worry about inaccurate information. SMEs prioritise ease of use and practicality, and want structured tutorials when problems occur — [Gurendrawati et al., JSMS](https://www.aasmr.org/jsms/Vol14/No.3/Vol.14.No.3.06.pdf)
- POS rollout to MSMEs in Purbalingga: **4 of 5** adopted and kept using the system independently. Training plus a mobile-friendly, accessible PWA (no app-store install) were key. One laggard showed the need for ongoing mentoring — [Prasetyo et al., JKAM 2023](https://doi.org/10.22437/jkam.v7i1.21453)
- **KSU Visiana Bhakti** (a cooperative with a "Unit Toko" serving TVRI employees and retirees, closely analogous to this project):
  - Problem: manual sales recording caused frequent errors in financial reports.
  - Intervention: socialisation for management, then a step from manual to Excel and labelling, then POS training, including migrating manual records into the software.
  — [Maryam et al., 2024](https://doi.org/10.32670/ecoopsday.v5i2.4696)
- Rural MSME POS needs study: 72% record manually, 20% use Excel, only 8% had used a POS app. Mean digital literacy was 3.7/5. Digital literacy correlated with operational efficiency (r = 0.48, p = 0.021) — [Iwan, Urindo](https://ejournal.urindo.ac.id/index.php/administrasimanajemen/article/download/7016/2798)
- Dimsum Surga ERP/POS case: employee training and **SOP creation** for the system were part of the successful rollout — [Jurnal Unpad](https://jurnal.unpad.ac.id/jane/article/viewFile/69427/27376)

### Inferences
- **Design to lower perceived complexity** (the strongest controllable TOE factor):
  - Use role-based home screens: shop staff see only "Pesanan masuk", "Siapkan barang", "Kirim", "Stok"; finance sees "Tagihan", "Bukti transfer", "Rekonsiliasi".
  - Hide advanced filters and settings behind "Lainnya" (progressive disclosure).
  - Prefer one primary action per screen.
- **Minimise typing:**
  - pickers and search-as-you-type for products and divisions
  - +/− steppers for quantity
  - defaults (today's date, the logged-in user's division)
  - "Ulangi pesanan" (repeat last order), which also supports WCAG 3.3.7
  - photo capture in place of typed delivery notes
- **Large targets:** use ≥44–48px touch targets for primary actions on mobile. This goes well beyond WCAG's 24px minimum and is an inference for low-dexterity, gloved, or on-the-move warehouse use.
- **Error prevention:**
  - confirm with a summary before irreversible actions ("Kirim 3 item senilai Rp1.250.000 ke Divisi Treasury?")
  - inline validation
  - undo for soft actions
  - show stock before submit
- **Bridge from current habits:**
  - Offer Excel/CSV export and printable layouts that look like the current handwritten Surat Jalan and printed reports.
  - Import existing Excel item lists, as the KSU case migrated manual records.
  - Keep WhatsApp share links (section 4).
- **Job aids:**
  - one-page laminated "Cara…" cards per role (e.g. "Cara mencatat pengiriman: 1) Buka pesanan 2) Foto Surat Jalan 3) Tekan Kirim")
  - a 2–3 minute Indonesian screen-recording per task
  - an in-app "Bantuan" link in a fixed position
  - a named internal champion (top-management support) and a short weekly check-in during the first month (mentoring)
- Run a parallel period in which paper/Excel and the app coexist, with a clear cut-over date. Measure adoption, e.g. the share of orders entered in-app.

### Gaps
- No Indonesian study specifically on onboarding UI patterns (tooltips, walkthroughs, empty states) for low-literacy business users was found. The guidance above transfers general UX practice onto the Indonesian adoption findings.
- No research on BNI-internal procurement or PIC behaviour was found (likely not public).

## 6. WCAG 2.2 AA requirements and Indonesian accessibility regulation

### Takeaway
WCAG 2.2 adds nine success criteria to 2.1 (six at A/AA) and removes 4.1.1 Parsing. The ones that matter most for Unit Toko are:
- 2.4.11 Focus Not Obscured (sticky headers/footers on mobile)
- 2.5.7 Dragging Movements (any drag-to-reorder or kanban)
- 2.5.8 Target Size Minimum (24×24 CSS px)
- 3.2.6 Consistent Help
- 3.3.7 Redundant Entry
- 3.3.8 Accessible Authentication (allow paste and password managers, no puzzle CAPTCHAs)

Indonesia has **no binding WCAG-based technical regulation** yet. UU 8/2016 and PP 42/2020 establish general accessibility rights for public services. A Komdigi ministerial regulation is in progress, and the government design system (IDDS) and Komdigi evaluations already use WCAG 2.2 AA as the reference. An internal koperasi app is therefore not legally bound, but WCAG 2.2 AA is the de-facto national benchmark.

### Cited Findings
- Changes from 2.1: WCAG 2.2 adds 9 success criteria; 4.1.1 Parsing is obsolete and removed; everything else from 2.0/2.1 is essentially the same — [W3C – What's New in WCAG 2.2](https://www.w3.org/WAI/standards-guidelines/wcag/new-in-22/)
- New criteria, per [W3C – What's New in WCAG 2.2](https://www.w3.org/WAI/standards-guidelines/wcag/new-in-22/):
  - **2.4.11 Focus Not Obscured (Minimum) (AA):** a focused component must not be entirely hidden by author-created content. W3C's example: a sticky bottom banner hiding focused items.
  - **2.5.7 Dragging Movements (AA):** anything done by dragging must also be possible with a single pointer without dragging, unless dragging is essential. Example: up/down arrow buttons to reorder a list.
  - **2.5.8 Target Size (Minimum) (AA):** pointer targets of at least 24×24 CSS px. Exceptions: spacing (a 24px-diameter circle around an undersized target doesn't intersect another target), an equivalent control elsewhere, inline targets in text, user-agent controls, and essential presentation.
  - **3.2.6 Consistent Help (A):** if human contact details, a contact mechanism, a self-help option or an automated contact mechanism repeat across pages, they appear in the same relative order.
  - **3.3.7 Redundant Entry (A):** information already entered or provided in the same process must be auto-populated or available to select. Exceptions: essential, security, or no longer valid.
  - **3.3.8 Accessible Authentication (Minimum) (AA):** no cognitive function test (such as remembering or transcribing a password, or solving a puzzle) in any login step, unless an alternative method, an assisting mechanism (e.g. paste or password manager), object recognition, or personal-content recognition is offered.
  - **3.3.9 (AAA, Enhanced):** removes the object-recognition and personal-content allowances.
  - **2.4.12 (Focus Not Obscured Enhanced) and 2.4.13 (Focus Appearance):** also AAA.
- Carried-over AA requirements still apply (unchanged in 2.2), including — [WCAG 2.2 Recommendation](https://www.w3.org/TR/WCAG22/):
  - 1.4.3 Contrast (Minimum) 4.5:1 for text (3:1 for large text)
  - 1.4.11 Non-text Contrast 3:1 for UI components and focus indicators
  - 2.4.7 Focus Visible
  - 1.4.1 Use of Color
  - 3.3.1 Error Identification, 3.3.2 Labels or Instructions, 3.3.3 Error Suggestion, 3.3.4 Error Prevention (legal/financial/data)
  - 1.4.10 Reflow at 320 CSS px
- IDDS states it follows WCAG 2.2 and WAI-ARIA and, per its own audit, sits at **Level AA**. Its audit's main findings were focus visibility, text colour contrast, colour as the only status indicator, and keyboard-navigation consistency — [INA Digital – Aksesibilitas](https://design.inadigital.go.id/foundation/accessibility/)
- Komdigi's October 2025 evaluation of government websites used UI/UX and WCAG 2.2 criteria (POUR) alongside SPBE compliance — [JDIH Komdigi](https://jdih.komdigi.go.id/berita/view/111)
- Regulation:
  - UU 8/2016 (Penyandang Disabilitas) Articles 122–124 oblige government to guarantee accessible information and communication for persons with disabilities. PP 42/2020 requires public services, including communication and information, to be accessible — [Amaliah et al., 2023](https://doi.org/10.24912/pr.v7i2.24456); [PP 42/2020 at JDIH BPK](https://peraturan.bpk.go.id/Read/133719/PP%20Nomor%2042%20Tahun%202020.pdf)
  - The Open Government Partnership commitment (ID0141) notes Indonesia had no government regulation on digital accessibility, and that UU 8/2016 addresses physical rather than digital infrastructure — [OGP ID0141](https://www.opengovpartnership.org/members/indonesia/commitments/ID0141/)
  - A 2025 normative study confirms that neither UU 8/2016 nor UU 1/2024 (second ITE amendment) gives WCAG-aligned technical provisions. It argues for a ministerial regulation or an SNI — [Universitas Terbuka conference paper](https://conference.ut.ac.id/index.php/osc/article/download/7872/2885/18585)
  - Kominfo (2024) said it was drafting digital information-service guidelines for persons with disabilities that reference ISO 40500 (WCAG 2.0) and WCAG — [Suarise press release](https://suarise.com/journal/siaran-pers/urgensi-regulasi-aksesibilitas-digital-dalam-membangun-lingkungan-digital-yang-inklusif/)
  - A 2025 report describes an upcoming Permenkomdigi on "LKIP Digital Penyandang Disabilitas", criticised for slow ratification and long compliance timelines — [Laporan Riset Situs Web Netra (Taratsa)](https://pustaka.taratsa.id/download/1050/pdf/1050.pdf)
- An axe audit of 34 provincial government websites found 2,088 WCAG 2.1 violations across 24 error types, showing low baseline practice in Indonesia — [Amaliah et al., 2023](https://doi.org/10.24912/pr.v7i2.24456)

### Inferences
- **Concrete Unit Toko rules:**
  - **2.4.11:** with a sticky mobile header or bottom action bar ("Kirim pesanan"), add `scroll-padding-top` / `scroll-padding-bottom` equal to the bar heights so focused fields are never fully hidden. Test by tabbing through long order forms at 360px.
  - **2.5.7:** any drag (reordering items, kanban columns for order status, drag-to-upload) needs a click or tap alternative: "Pindah ke…" menu, ↑/↓ buttons, and a "Pilih file" button next to the drop zone.
  - **2.5.8:** minimum 24×24 CSS px for icon buttons in dense tables (edit/delete icons, pagination, checkboxes), with spacing; recommended 44–48px for mobile primary actions. Do not place "Hapus" right next to "Simpan".
  - **3.2.6:** put "Bantuan" / "Hubungi Koperasi (WhatsApp)" in the same place on every page, e.g. header or user menu.
  - **3.3.7:** in multi-step order or delivery flows, carry forward the division, PIC, address and contact. Offer "Sama dengan alamat sebelumnya" and "Ulangi pesanan terakhir". Don't re-ask for the order number on the delivery step.
  - **3.3.8:**
    - Allow paste and password managers (`autocomplete="username"` / `"current-password"`), with no image puzzles.
    - If OTP is used, allow paste and `autocomplete="one-time-code"`.
    - Consider magic link or SSO for bank PICs.
  - **Forms and errors (3.3.1/3.3.3):**
    - Visible `<label>` for every field, not placeholder-only.
    - Error text in Indonesian placed next to the field and tied to it with `aria-describedby`.
    - An error summary at the top with links to the fields.
    - Focus moves to the summary on submit.
  - **3.3.4:** for financial records (payment confirmation, invoice issuance), provide review/confirm or reversal.
  - **Status colours:** use badge text plus colour ("Lunas", "Belum dibayar", "Terlambat"), never colour alone. IDDS's own audit flagged this, and it is common in order-status tables.
  - **Focus indicator:** at least 2px outline with 3:1 contrast against adjacent colours. Don't remove `outline` in Tailwind/shadcn resets.
  - **Language:** set `<html lang="id">` so screen readers (TalkBack on Android) pronounce Indonesian correctly. Mark English fragments with `lang="en"` if needed.
- **Legal posture:** cite WCAG 2.2 AA as the target "in line with IDDS (the national government design system) and Komdigi evaluation practice". Do not claim it is legally required for a private koperasi's internal app.

### Gaps
- No final, enacted Permenkomdigi on digital accessibility was confirmed as of October 2026. Its status and whether it extends to private-sector or BUMN-affiliated services are unknown.
- No SNI adopting WCAG 2.2 was found (ISO/IEC 40500 corresponds to WCAG 2.0).
- No data was found on assistive-technology use among Indonesian office or shop workers, such as TalkBack or screen-magnifier prevalence.
