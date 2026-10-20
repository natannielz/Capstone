# D08 — Pilot and Cutover Playbook for Unit Toko (small retail cooperative, 2026)

Scope note: most practitioner sources on cutover are vendor/consultant blogs (Ecosire, Digital Adaption, Acumatica, Softomate, ERP Research, E2B Teknologies, RSM). They agree with each other, but none is a formal standard; tolerances and durations below are guidance, not norms. Indonesian evidence is mostly small qualitative case studies (abstracts). One fetched source (gigabit.agency "ERP Cutover Plan + Hypercare Checklist") now redirects to an unrelated AI-services page and is NOT cited.

## 1. Pilot strategy, parallel run duration, success metrics

### Takeaway
For a small, single-site organization, sources favour a limited pilot (a slice of business run through both systems) plus a short parallel period (1–4 weeks, ideally spanning one month-end close and one tax period) rather than a full long parallel run, followed by a single cutover and 2–4 weeks of hypercare that includes the first month-end close.

### Cited Findings
- Parallel running of 1–4 weeks is suggested; it should cover one payroll cycle, one VAT period and one month-end close, and adds 2–4 weeks to the overall timeline — [Softomate, Odoo implementation timeline UK](https://www.softomatesolutions.com/blog/odoo-implementation-timeline-uk/) (via search snippet; page returned 403 on fetch)
- Vendor guidance: parallel running demands the most effort because users are expected to do their jobs twice, which can leave both systems under-attended; a pilot (running a slice of the business, e.g. re-entering sales orders from the old system into the new one, and comparing results) is much easier than running 100% of the business in parallel — [Acumatica, "Going live with your new ERP system" (PDF)](https://cdn.acumatica.com/media/2016/08/Acumatica-Going-live-with-your-new-ERP-system.pdf)
- Practitioner counterpoint: an ERP practitioner for a ~100-employee firm reported never doing a parallel run and relying on thorough UAT; another noted that when two systems work differently, results are hard to compare — [DynamicsUser forum, "Running parallel test before going live"](https://www.dynamicsuser.net/t/running-parallel-test-before-going-live/17978)
- Big bang is described as more viable for smaller, simpler deployments; phased rollouts suit larger or multi-entity businesses — [ERP Research glossary: go-live](https://erpresearch.com/glossary/go-live)
- Hypercare: most organizations plan 2–4 weeks of dedicated post-go-live support; one guide recommends 2 weeks covering configuration tweaks, extra training and hands-on support through month-end close; most issues surface in the first 2 weeks — [Genetiq, how long does ERP implementation take](https://www.genetiqsoftware.com/en-us/genetiq-blog/how-long-does-erp-implementation-take); [ERP Research](https://erpresearch.com/glossary/go-live)
- A Tally→Odoo guide recommends a 30-day stabilization window with weekly comparison of the new trial balance against the last known-good balance from the old system, and claims the first month-end close runs 25–40% slower than steady state (figure unsupported by evidence) — [Ecosire, Tally to Odoo migration guide](https://ecosire.com/es/blog/tally-to-odoo-migration-step-by-step-guide)
- Timeline: cutover planning ~6 weeks out, go/no-go criteria drafted early, full cutover rehearsal ~2 weeks before go-live; teams that skip rehearsal and use go-live as the test turn small issues into disruption — [Ecosire, ERP Go-Live Checklist](https://ecosire.com/blog/erp-go-live-checklist)

### Inferences
- Unit Toko's natural "pilot slice" is one BNI division (one PIC) ordering through the new system while other divisions stay on WhatsApp/Surat Jalan — a division-scoped pilot, not a branch pilot (there is only one shop). Phase-by-process also fits: (a) ordering→approval→dispatch with sequential delivery records first; (b) invoicing + payment-proof matching second; (c) month-end close last, once a full month of transactions exists in the new system.
- Because OMI stays as the supplier's system, the "parallel run" for staples is permanent reconciliation, not a temporary dual entry. The temporary parallel run applies to what is being replaced: manual Surat Jalan, the Smart merchandise system (if retired), the manual journal merge, and manual transfer matching.
- Recommended sizing: pilot 2–4 weeks with 1 division; parallel run of the replaced processes across one full calendar month so it spans one month-end close and one PPN tax period (Masa PPN); hypercare 4 weeks overlapping the first fully-live month-end.
- Suggested success metrics (derived, not sourced): 100% of pilot deliveries carry a system-generated sequential delivery number; zero unexplained variance between new-system and OMI daily sales/stock for pilot SKUs; % of incoming transfers auto-matched to an invoice/payment reference; days to month-end close vs baseline; count of orders still arriving via WhatsApp only; new suspense additions = 0 for pilot divisions.

### Gaps
- No academic/benchmark source gives typical parallel-run lengths specifically for cooperatives or retail units in Indonesia.
- No source quantified pilot success metrics for small retail; metrics above are inferred.

## 2. Cutover checklist (opening stock, AR/AP, master data, users, freeze, go/no-go, rollback, customer communication)

### Takeaway
Cutover should happen at a month-end, with a physical count, reconciled opening balances (trial balance, AR aging, bank) signed off by named owners, a pre-agreed go/no-go list and a rollback decision rule — all decided before cutover weekend, not during it.

### Cited Findings
- Opening balances are loaded as of a cutover date, usually a month-end, covering GL and subledger amounts (AR/AP aging, bank, inventory value) — [Cleverence, opening balances in Sage ERP](https://www.cleverence.com/articles/sage-documentation/entering-account-balances-historical-data-and-quantities-4827/)
- Acceptance bar: new trial balance matches old as of cutoff with zero tolerance on balance-sheet accounts (≤ $1 on P&L); AR/AP aging matches source; bank balances agree with bank statements; physical/latest cycle counts match on-hand quantities for critical items — [Ecosire, ERP Go-Live Checklist 50-point](https://ecosire.com/blog/erp-go-live-checklist-launch-guide)
- Final delta (transactions entered in the old system after the main load) should be loaded fast, ideally < 2 hours — [Ecosire](https://ecosire.com/blog/erp-go-live-checklist-launch-guide)
- After loading stock, compare valuation reports to the legacy valuation; non-balance data (reorder points, min/max, bin locations, default vendors) must also be loaded or operations stall — [Cleverence](https://www.cleverence.com/articles/sage-documentation/entering-account-balances-historical-data-and-quantities-4827/)
- Open AR: carry each open invoice with original invoice number, date, due date and tax breakup (not a single lump balance) to preserve aging continuity; reconcile every open document — even ₹1 differences show in reports; fix during the reconciliation window, not after go-live — [Ecosire, Tally to Odoo](https://ecosire.com/es/blog/tally-to-odoo-migration-step-by-step-guide)
- Weekend cutover pattern: freeze source Friday evening, load final changes Saturday, smoke test and rollback test Sunday, go live Monday; old system becomes a read-only archive — [Ecosire, Tally to Odoo](https://ecosire.com/es/blog/tally-to-odoo-migration-step-by-step-guide)
- Opening stock in a typical ERP (Odoo) is entered as an inventory adjustment: record counted quantities per location, review, then apply — [Odoo documentation repo (inventory adjustments)](https://github.com/odoo/documentation/pull/7421/files); [Cleverence, opening inventory in Odoo](https://cleverence.com/articles/odoo-documentation/set-your-initial-stock-inventory-4821)
- Master data cleansing before the first meaningful test load: merge duplicate customers/suppliers, remove inactive records per archive rules, standardise units of measure and categories, resolve missing mandatory fields, lock down who can change master data during the migration window; decide what should NOT migrate (read-only archive instead) — [Digital Adaption, ERP Data Migration Checklist for SMEs (Jun 2026)](https://digitaladaption.co.uk/blog/2026-06-05-erp-data-migration-checklist-sme/)
- Build a source-system inventory including spreadsheets, CSV exports and manual trackers; decide "which source wins when two systems disagree" — [Digital Adaption](https://digitaladaption.co.uk/blog/2026-06-05-erp-data-migration-checklist-sme/)
- Named business owners sign off by area (finance: balances/open transactions; operations: products/stock; leadership: known risks and go/no-go). Decide before cutover weekend which defects block go-live, which are accepted with a workaround, and which require rollback — [Digital Adaption](https://digitaladaption.co.uk/blog/2026-06-05-erp-data-migration-checklist-sme/)
- Go/no-go is documented with formal sign-off from sponsor, PM and department heads; go-live is postponed if any critical item is open — [Ecosire 50-point checklist](https://ecosire.com/blog/erp-go-live-checklist-launch-guide)
- Finance go/no-go questions include whether every open finance exception has an owner and whether month-end reports still produce trusted numbers; run checks after every meaningful test load, not only on cutover weekend — [Digital Adaption, Finance Data Migration Sign-Off Pack](https://digitaladaption.co.uk/downloads/finance-data-migration-sign-off-pack/)
- High-risk indicators: limited SME (subject-matter expert) availability and poor data quality — [Hyperbots, cutover planning glossary](https://www.hyperbots.com/glossary/cutover-planning)

### Inferences (Unit Toko-specific checklist)
- Timing: cut over on the 1st of a month after a month-end stock opname (count date = cutover date). Avoid the last week of a month and the PPN filing window of the following month for the go-live itself; avoid December/January (fiscal year-end close + RAT preparation) — see §5.
- Opening stock: full stock opname of merchandise (Smart) and the staples held for division orders; for OMI-managed staples, take OMI's on-hand as the reference and record count vs OMI variance as a reconciling item rather than overwriting OMI.
- Open AR: load every unpaid cooperative-billing invoice individually (invoice no., faktur pajak no., division, due date, amount, PPN), so new payment proofs can match to invoice references from day one.
- Suspense (~Rp475M unidentified transfers): do NOT net or write off at cutover. Load as an itemised "unidentified receipts" opening list (one line per bank credit: date, amount, sender text, bank ref) under a separate suspense account, with an owner and an aging; clear lines over time by matching. Accounting/Pimpinan Unit should sign the opening suspense list as-is. This keeps the old problem visible but quarantined.
- Delivery note numbering: start the new sequential Surat Jalan series on cutover day; record the last handwritten notes outstanding (delivered-not-invoiced) as an opening list so they are invoiced through the new system.
- Master data: product codes mapped across OMI SKU / Smart code / new item code (one crosswalk table); division and PIC master (who may order/approve); price lists; chart of accounts aligned to SAK EP.
- User provisioning: create named accounts only (no shared logins), roles mapped to segregation of duties (orderer ≠ approver ≠ payment verifier); disable test accounts before go-live.
- Customer (division PIC) communication: at least 2 weeks before go-live send a one-page notice + short demo: new ordering channel, approval step, that deliveries now carry a numbered delivery record, and the mandatory payment reference rule (e.g., invoice number in transfer description) — with a firm date after which WhatsApp orders are not accepted (or are re-keyed by staff only during hypercare).
- Rollback: because the old process is manual/paper, rollback = revert to WhatsApp + handwritten Surat Jalan + manual journal using pre-printed numbered forms kept ready; define the trigger (e.g., new system unavailable > 1 business day, or opening balances fail reconciliation) and keep new-system data as the record to re-key later.

### Gaps
- No source found describing OMI back-office export formats or whether OMI franchise partners can export daily sales/stock files; this must be confirmed with OMI/Indogrosir directly. General OMI info: franchise with software lending and SOP-based training — [Pancabudi journal on OMI](https://journal.pancabudi.ac.id/index.php/jurnalfasosa/article/download/2614/2399)
- No authoritative source on treatment of long-standing unidentified receipts under SAK EP at transition; accountant judgement needed.

## 3. Training approach for low-IT-maturity staff and primary/backup operators

### Takeaway
Use a train-the-trainer / super-user model (1–2 internal champions who learn early, write short role-specific job aids from the test scripts, and support colleagues on the floor), with hands-on sandbox practice; Indonesian case evidence shows weak training and low motivation as recurring failure causes in small-retail ERP.

### Cited Findings
- Indonesian case: ERP failed at a supermarket (swalayan) MSME in Malang despite top-management support, due to limited employee competence, weak project management, no balanced project team, resistance to change, lack of effective training and low motivation — so ERP was seen "as a burden, not a solution" — [Azaro et al., JAMA Polibatam Vol 9 No 2 (2025)](https://jurnal.polibatam.ac.id/index.php/JAMA/article/view/11704)
- Indonesian case (PT XYZ finance/accounting division): obstacles were resistance to change, high workload and uneven competence; recommended better communication, more intensive training and HR management during transition — [Indikator, Mercu Buana (2025)](https://publikasi.mercubuana.ac.id/index.php/indikator/article/view/32915)
- Indonesian SAP case: training identified as the biggest problem; authors built a training website — [G-Tech, Uniramalang](https://ejournal.uniramalang.ac.id/g-tech/en/article/view/2488); contradicted in part by a study of 84 village enterprises (BUMDes) where training showed no significant effect on ERP success — [Undana JEM](https://ejurnal.undana.ac.id/index.php/JEM/article/download/13290/6862)
- Super users: choose the employee co-workers already turn to for tech help; mix group sessions and one-to-one; train them on all modules — [E2B Teknologies](https://e2btek.com/identifying-training-erp-super-user/); [2W Tech](https://2wtech.com/do-you-have-an-erp-super-user/)
- Super users help write training materials and quick reference guides; documentation stays valuable as staff turn over and reduces reliance on the implementer — [RSM technology blog](https://technologyblog.rsmus.com/insights/5-training-your-super-users-5-important-things-to-keep-in-mind)
- Train-the-trainer builds a permanent internal training capability and limits consultant dependence; base materials on test scripts; role-specific training (a clerk and a floor operator don't need the same course) — [ERP Research, ERP training 2026](https://erpresearch.com/en-us/erp-training); [Velosio](https://www.velosio.com/blog/functionality-user-training-how-to-mitigate/)
- Workload warning: "all-star" employees get overloaded during implementation; a super user may need 20–30% of time — [ERP Research](https://erpresearch.com/en-us/erp-training)
- Cooperatives' baseline: a West Java study found most cooperatives still compile financial statements manually in Excel — [IKOPIN J. Coop](https://ojs.ikopin.ac.id/index.php/jcoop/article/download/21/21)

### Inferences
- Unit Toko plan: (1) sandbox/training environment loaded with copied master data and fictitious orders; (2) Kepala Toko + one Staf Toko as super users for ordering/stock/dispatch; Pengolah Laporan + one accounting staff as super users for invoicing/payment matching/close; (3) one-page laminated job aids in Bahasa Indonesia per task (buat pesanan, setujui, siapkan & kirim + Surat Jalan, catat bukti bayar, rekonsiliasi harian OMI, tutup bulan) with screenshots; (4) short practice sessions during pilot, not one long class.
- Bus factor: the single Pengolah Laporan is a single point of failure for the journal and month-end. Require a named backup (e.g., one accounting staff) who performs the daily reconciliation at least once a week during pilot and executes one month-end close end-to-end under supervision before go-live sign-off. Document each recurring report procedure as a checklist inside the system or job aid. Make "backup operator has done the task unaided" an exit criterion.
- Keep super-user load in mind: Unit Toko staff are few; schedule training outside peak delivery hours and protect super-user time.

### Gaps
- No rigorous source on job-aid formats for low-literacy/low-IT retail staff in Indonesia; guidance above is practitioner-level.
- Indonesian studies cited are abstracts/small samples; training-effect evidence is mixed.

## 4. Reconciliation, acceptance evidence, sign-off roles, security checks before go-live

### Takeaway
Acceptance evidence should be a signed reconciliation pack (trial balance, AR aging, bank, stock valuation, new-system vs OMI daily totals) with named sign-offs by area and leadership, plus pre-go-live security evidence (ASVS 5.0 L1 baseline, L2 for authentication/MFA on finance roles, tested backup restore).

### Cited Findings
- Finance must explain every reconciling difference between old and new; each open transaction is deliberately included, excluded or archived; stock value must agree between operations and finance — [Digital Adaption Sign-Off Pack](https://digitaladaption.co.uk/downloads/finance-data-migration-sign-off-pack/)
- Reconciliation targets: trial balance, AR/AP aging, bank vs statements, stock counts — [Ecosire 50-point](https://ecosire.com/blog/erp-go-live-checklist-launch-guide)
- Key reports used by finance and operations should be shown to still agree after go-live; keep a report catalogue (needed / retired / redefined) — [Digital Adaption](https://digitaladaption.co.uk/blog/2026-06-05-erp-data-migration-checklist-sme/)
- Sign-off roles: finance → balances/open transactions; operations → stock/fulfilment; leadership → known risks and go/no-go — [Digital Adaption](https://digitaladaption.co.uk/blog/2026-06-05-erp-data-migration-checklist-sme/)
- Weekly trial-balance comparison during the first 30 days — [Ecosire Tally→Odoo](https://ecosire.com/es/blog/tally-to-odoo-migration-step-by-step-guide)
- OWASP ASVS 5.0.0 is the latest stable version (released May 2025, launched at Global AppSec EU Barcelona); requirement lists are published in CSV/JSON — [OWASP ASVS project](https://owasp.org/www-project-asvs/); [SoftwareMill, what's new in ASVS 5.0](https://softwaremill.com/whats-new-in-asvs-5-0/)
- ASVS 5.0 simplified Level 1 to lower the barrier to entry; L1 is the first-layer defence (~20% of requirements); most applications should target Level 2 — [SoftwareMill](https://softwaremill.com/whats-new-in-asvs-5-0/); [OWASP Developer Guide, ASVS](https://devguide.owasp.org/03-requirements/05-asvs)
- ASVS 5.0 requirements were fully renumbered and reworded vs 4.0.3; a mapping document exists. In v4, L2 mapped to NIST AAL2, which requires MFA — [OWASP ASVS v4.0.2 V2 Authentication](https://github.com/OWASP/ASVS/blob/v4.0.2/4.0/en/0x11-V2-Authentication.md); [SoftwareMill](https://softwaremill.com/whats-new-in-asvs-5-0/)
- Rollback plan should be documented before go-live; Sunday cutover includes a rollback test — [Ecosire Tally→Odoo](https://ecosire.com/es/blog/tally-to-odoo-migration-step-by-step-guide); [Hyperbots](https://www.hyperbots.com/glossary/cutover-planning)

### Inferences
- Daily reconciliation (new system vs OMI): per day, compare (a) quantities shipped to divisions for OMI-supplied SKUs vs OMI outbound/sales records, (b) stock movements for those SKUs, (c) value totals; every variance gets a reason code and owner. During pilot this is daily; after hypercare, weekly + month-end.
- Cash/bank: daily bank-mutation import or statement entry vs payment proofs (OCR-checked) vs invoices; output = matched / partially matched / unidentified list. Track "new unidentified receipts added" separately from the legacy Rp475M opening suspense so improvement is measurable.
- Sign-off matrix suggestion: Kepala Toko → stock opname and delivery-record continuity; Pengolah Laporan + accounting staff → AR opening list, suspense list, trial balance; cooperative billing → open invoices/faktur list; Pimpinan Unit → go/no-go and accepted risks. Ideally the cooperative's Pengawas or external accountant reviews the opening-balance pack.
- Security evidence before go-live: ASVS 5.0 L1 checklist completed for the whole app; L2-level authentication controls (MFA) at least for approver, finance and admin roles; least-privilege roles; audit log of approvals and payment matching; a full backup + restore test to a separate environment with restore time recorded; secrets not in code; HTTPS only. (The ASVS-to-role mapping is an inference; exact ASVS 5.0 requirement IDs must be pulled from the official CSV.)

### Gaps
- Exact ASVS 5.0 chapter/ID text for MFA and backup requirements not retrieved; use the official CSV/JSON at the OWASP GitHub v5.0.0 tag.
- No source on standard reconciliation formats between a franchisor POS (OMI) and a third-party system.

## 5. Indonesian cooperative digitalization and regulatory timing (SAK EP, PPN/faktur, Kemenkop)

### Takeaway
SAK EP replaces SAK ETAP for periods beginning on/after 1 Jan 2025 and Permenkop 2/2024 requires cooperatives to align accounting policies to it; faktur pajak now runs through Coretax (since 1 Jan 2025) with monthly PPN periods — so cutover should be aligned to a month start (ideally fiscal-year start or a quarter start), not mid-period, and the first new-system invoices must be valid Coretax faktur.

### Cited Findings
- SAK EP (approved by DSAK IAI 30 June 2021) is effective for periods beginning on/after 1 January 2025, replacing SAK ETAP; early adoption allowed from 2022 — [Unram journal](https://jurnal.fe.unram.ac.id/index.php/prakbis/article/download/3356/1432/13763); [DDTCNews](https://news.ddtc.co.id/berita/nasional/1802565/sak-ep-bakal-gantikan-sak-etap-tak-boleh-turun-kelas-pakai-sak-emkm)
- Permenkop UKM No. 2/2024 requires cooperatives to adjust accounting policies to SAK EP — [Unmul ABDIMU](https://e-journals2.unmul.ac.id/index.php/ABDIMU/article/download/5039/2507/17662)
- SAK ETAP users cannot "step down" to SAK EMKM — [DDTCNews](https://news.ddtc.co.id/berita/nasional/1802565/sak-ep-bakal-gantikan-sak-etap-tak-boleh-turun-kelas-pakai-sak-emkm)
- PMK 131/2024: for non-luxury goods, PPN at 12% is computed on "DPP nilai lain" = 11/12 of selling price (effective burden 11%); faktur uses transaction code 04 in Coretax per PER-1/PJ/2025 — [DDTC Coretax consultation](https://news.ddtc.co.id/review/konsultasi-coretax/1808042/bagaimana-cara-membuat-faktur-pajak-dengan-dpp-nilai-lain-di-coretax); [IKPI](https://ikpi.or.id/?p=13685); [Pajakku](https://pajakku.com/artikel/simak-bedanya-dpp-nilai-lain-sebelum-dan-sesudah-coretax)
- PMK 11/2025 (omnibus) and PER-11/PJ/2025 subsequently revised faktur rules; most DPP-nilai-lain formulas still use 11/12 — [DDTC](https://news.ddtc.co.id/review/konsultasi-coretax/1808042/bagaimana-cara-membuat-faktur-pajak-dengan-dpp-nilai-lain-di-coretax); [Pajak.com on PER-11/2025](https://www.pajak.com/pajak/pkp-wajib-tahu-praktisi-ini-ungkap-perubahan-penghitungan-ppn-dan-faktur-pajak-di-per-11-2025/amp/)
- Since 1 Jan 2025 PPN reporting moved to Coretax; SPT Masa PPN due by end of the following month — [OnlinePajak](https://www.online-pajak.com/id/spt-masa-ppn); some sources say payment/filing deadlines were unified to the 15th — conflicting, verify against PMK 81/2024 — [Pajak.com tax calendar](https://www.pajak.com/pajak/kalender-pajak-februari-2025-awasi-tenggat-waktu-hindari-surat-cinta-dari-petugas-pajak/amp/)
- DJP clarified PMK 81/2024 does not explicitly restrict input-tax crediting to the same period — [DJP KT-08/2025 (PDF)](https://www.pajak.go.id/sites/default/files/2025-02/KT-08_2025%20Pengkreditan%20Pajak%20Masukan%20Pasca%20Implementasi%20Coretax...pdf); [IKPI](https://ikpi.or.id/posisi-kedudukan-hukum-pmk-81-2024-tentang-pengkreditan-faktur-pajak-masukan-dan-kepastian-hukum/)
- Kemenkop digitalization in 2025 centres on Koperasi Desa/Kelurahan Merah Putih (Inpres 9/2025): SIMKOPDES microsite/super-app, a formation dashboard, and Kemenkop–Komdigi cooperation (connectivity, data centre, platform, digital literacy) — [Investortrust](https://investortrust.id/national/76926/kemenkop-dan-komdigi-perkuat-kerja-sama-untuk-digitalisasi-koperasi-desa-merah-putih); [RMOL](https://rmol.id/bisnis/read/2025/09/19/680508/digitalisasi-kopdes-merah-putih-diperkuat-guna-optimalkan-pelayanan); [SIMKOPDES app](https://apps.apple.com/id/app/simkopdes/id6749216676)
- Minister Ferry Juliantono (Dec 2025): digitalization yields little multiplier effect without real-sector progress — [Bisnis.com](https://ekonomi.bisnis.com/read/20251202/12/1933569/ekosistem-kopdes-merah-putih-butuh-dukungan-sekolah-digital)

### Inferences
- Best cutover windows: 1 January (aligns with fiscal year, SAK EP comparative periods and closing of the RAT books) is cleanest but collides with year-end close workload; a practical alternative is the first day of a quarter/month after a clean month-end close, keeping Dec–Feb (year-end + RAT preparation) and the PPN filing deadline days out of the cutover/hypercare peak.
- Faktur pajak: the new system will likely prepare invoice data while faktur is still issued in Coretax (via import/manual entry); go-live acceptance should include a test that one full PPN period's invoices map 1:1 to Coretax faktur (correct DPP 11/12, code 04 where applicable) — confirm with the cooperative's tax adviser as rules changed repeatedly in 2025.
- Kemenkop programmes (SIMKOPDES) target village cooperatives, not employee cooperatives like Unit Toko; no direct mandate affects this system, but alignment of the chart of accounts to SAK EP is mandated via Permenkop 2/2024.

### Gaps
- Exact current faktur issuance deadline and whether PPN deadline is the 15th or end of following month in 2026 not confirmed from primary text.
- Whether Unit Toko's cooperative is a PKP and which transaction codes (e.g., code 04 vs 01, or BUMN/government-related collector codes for BNI) apply — unverified; BNI is a state-owned bank and may have special PPN collection arrangements; needs tax adviser confirmation.
- No Kemenkop guidance found specific to employee (karyawan) cooperative store system cutover.
