# Penerimaan rilis dan cutover

Dokumen ini menetapkan bukti yang diperlukan sebelum menjalankan alur bisnis pada lingkungan tujuan. Status aktual, persentase, hasil uji terminal dan pemetaan seluruh 97 persyaratan hanya dicatat dalam [ledger kemajuan utama](production-progress.md). Keberadaan layar, source, commit, atau deployment frontend tidak menutup penerimaan suatu alur.

Checkpoint T00–T05 dan implementasi fondasi September 2026 dipertahankan dalam [arsip fondasi penerimaan](../archive/production-acceptance-foundation-2026-10-08.md). Jumlah tes, worktree, runtime legacy dan kekurangan pada arsip adalah kondisi saat checkpoint tersebut dibuat. Gunakan ledger untuk kondisi terbaru.

## Bukti wajib sebelum rilis

| Bagian | Bukti yang harus tersedia |
| --- | --- |
| Cakupan lengkap | Seluruh [kebutuhan asli](../requirements/unit-toko-production.md), manual/rapat dan [97 item laporan](../requirements/unit-toko-report-2026-09-28.md) terpetakan ke layar, operasi backend, data persisten, izin, hasil pemeriksaan dan sisa pekerjaan. Fitur conditional mengikuti syarat data/evaluasinya; baseline wajib tetap harus selesai. |
| Instalasi dan pemeriksaan | Instalasi dari lockfile dan build bersih pada Node 24/.NET 10 yang dipatok; kontrak API sesuai; seluruh job [CI terfokus](../../.github/workflows/quality.yml) dan [validasi rilis](../../.github/workflows/release-validation.yml) benar-benar dijalankan pada perubahan terintegrasi. Tidak menaikkan timeout, mengurangi assertion, melewati required check atau membuat status pengganti. |
| Penghentian legacy | Runtime produksi tidak memuat backend bisnis legacy, Turso/libSQL atau Drizzle yang telah digantikan. Ketergantungan `/api/state` dan `/api/commands` dihapus setelah pengganti teruji; bukti regresi lama dan konteks bisnisnya tetap dapat dipulihkan. Pemasangan paket untuk regresi historis tidak membuktikan penghentian dependensi runtime. |
| Jalur pengguna | Browser → API → MongoDB → hasil → refresh/restart/recovery terbukti dengan sumber dan build yang dicatat. Meliputi desktop/mobile, keyboard, keadaan kosong/loading/denied/offline, versi basi, perubahan akun dan respons commit hilang. Screenshot berasal dari aplikasi yang benar-benar dijalankan. |
| Integritas bisnis | Bukti permission/scope, pemisahan orang, exact money/identifier, duplikasi, persaingan transaksi, rollback dan original-key recovery untuk setiap alur terdampak. Reservasi, konversi, dispatch, invoice, pembayaran, retur dan supplier advance harus mempertahankan sejarah sumber serta saldo yang benar. Kredit pengembalian advance terikat supplier dan purchase order masing-masing; uang bank yang sama tidak digunakan dua kali. |
| Bukti privat dan OCR | Bukti asli tetap privat dan terotorisasi; upload, admission, versi immutable, batas sumber daya dan pemulihan worker teruji. Tesseract 5.5.3/tesserocr 2.11.0 dengan `ind+eng` diuji pada dokumen representatif, termasuk hasil tidak terbaca/ambigu dan koreksi manusia. Hasil ekstraksi tidak memberi persetujuan pembayaran atau penerimaan barang. |
| Identity dan hosting | Domain HTTPS, origin/proxy/cookie, API/worker, replica set, private storage/keys dan tenant Auth0 tujuan benar-benar tersambung. Masuk, email verification, MFA, perubahan/revocation akses serta recovery diuji pada provider sebenarnya. Sesi sintetis lokal tidak menggantikan bukti ini. |
| Migrasi | Release tujuan, sumber predecessor yang sebenarnya, urutan migrasi, validator/index dan bukti history diverifikasi. Data tak dikenal, efek orphan atau provenance yang hilang menghentikan migrasi untuk ditinjau; jangan menghapusnya atau membuat receipt palsu agar preflight lulus. API startup tidak melakukan migrasi operasional diam-diam. |
| Backup dan restore | Backup MongoDB, bukti privat dan kunci yang saling sesuai berhasil dipulihkan ke lingkungan terisolasi. Verifikasi dokumen/hash, audit, receipt, outbox, saldo, read/recovery dan ketidakadaan efek ganda; backup tersimpan saja belum merupakan uji restore. |
| Worker dan notifikasi | Proses worker sebenarnya, restart, retry, kegagalan, outbox dan rekonsiliasi acknowledgment terbukti. Setelah restore, efek keluar tetap tertahan sampai status acknowledgment dapat dipastikan. Pengiriman pesan nyata membutuhkan otorisasi yang sesuai. |
| Aktivasi operasional | Cabang pilot, PIC/operator, pemberi persetujuan independen dan prosedur/policy operasional disetujui. Keputusan yang belum tersedia tetap tercatat dalam [ledger keputusan](report-decisions.md); contoh konfigurasi atau fixture tidak menjadi persetujuan, harga, limit kredit atau data operasional. |

## Kasus regresi fondasi yang tetap wajib

Kasus berikut mempertahankan batas penerimaan checkpoint awal. Angka uang merupakan contoh uji, bukan policy atau data operasional.

| Kasus | Hasil yang harus dibuktikan pada owner sebenarnya |
| --- | --- |
| Independensi pembayaran | Orang A yang mencatat pembayaran tetap tidak boleh memverifikasinya setelah perubahan role/grant; orang B yang independen memverifikasi sekali dan replay tidak menggandakan efek. |
| Aging pembayaran parsial | Invoice100.000, alokasi40.000 dan credit yang disetujui10.000 menyisakan50.000. Jika jatuh tempo sebelum tanggal bisnis, status parsial tetap menampilkan keterlambatan. |
| Konflik berulang | Empat konflik penulisan menghasilkan penolakan409 bertipe tanpa mutasi parsial; percobaan baru dengan sumber yang ditinjau kembali dapat berhasil. Hasil yang belum pasti tetap memakai pemulihan kunci asli. |
| Paritas skenario bisnis | Materialisasikan kondisi awal, urutan command dan referensi setiap golden scenario; bandingkan saldo serta hasil yang dinormalisasi dengan owner C# pengganti. Jangan mempertahankan cacat self-verification hanya demi menyamai legacy. |
| Dokumen immutable | Perubahan nama/alamat pembeli setelah penerbitan tidak mengubah isi atau hash dokumen lama. |
| Isolasi cabang | List, count, detail, write, export, file dan event lintas lingkup ditolak, termasuk manipulasi ID langsung; halaman kosong tidak menggantikan pemeriksaan akses. |
| Persaingan stok/uang | Reservasi/alokasi yang bersaing dan respons commit yang hilang mempertahankan saldo persis dan satu hasil sah. |
| Duplikasi sumber eksternal | Penjualan web yang diimpor kembali dari kasir mengubah stok dan pendapatan sekali saja melalui identitas sumber yang benar. |
| Replay setelah restore | Outbox hasil restore tetap ditahan sampai acknowledgment eksternal direkonsiliasi; melanjutkan worker tidak menggandakan efek keluar. |

## Urutan cutover

1. Tetapkan release yang telah diperiksa, lingkungan tujuan, PIC, cabang pilot dan persetujuan operasional. Cocokkan commit, source manifest, konfigurasi non-secret dan hasil pemeriksaan; tuntaskan required checks yang benar-benar berlaku.
2. Hentikan dan kuras seluruh writer aplikasi, worker dan integrasi yang terdampak. Lease migrasi hanya mengoordinasikan deployer; operator tetap harus memastikan transaksi bisnis tidak berjalan.
3. Ambil backup terkoordinasi dan buktikan restore terisolasi sebelum mengubah data operasional. Pertahankan bukti, kunci, receipt migrasi dan riwayat sumber yang diperlukan.
4. Jalankan entry point migrasi yang dipertahankan menggunakan konfigurasi lingkungan yang disetujui. Verifikasi urutan dan hasil preflight setiap owner; jangan mengganti kegagalan dengan seed atau prefix migrasi buatan.
5. Pasang frontend/API/worker yang sesuai, buktikan hanya writer yang disetujui aktif, lalu jalankan walkthrough cabang pilot dengan akun serta kewenangan sebenarnya. Rekonsiliasi saldo/stock, dokumen, outbox dan hasil operasi sebelum membuka cakupan lebih luas.
6. Catat bukti dan penerimaan per persyaratan pada ledger utama. Jika perlu rollback, hentikan writer terlebih dahulu dan gunakan prosedur pemulihan yang telah direhearsal; jangan mencampur backup database, berkas bukti dan kunci dari waktu yang tidak cocok.

Release 1 tetap pilot terbatas untuk katalog dan permintaan cabang. Penerimaannya tidak menggantikan cakupan sistem lengkap. Pembelian layanan, pesan nyata, transfer finansial dan migrasi data operasional tetap memerlukan otorisasi serta input operasional yang sesuai.

Panduan terkait: [pengembangan lokal](../runbooks/local-development.md), [pilot Release 1](../runbooks/local-pilot-release1.md), [hosting identity](../runbooks/identity-hosting.md), [penyimpanan privat](../runbooks/private-evidence-storage.md), [pemrosesan dokumen](../runbooks/document-processing.md), [outbox](../runbooks/outbox-consumers.md), dan [deployment Release 1](../runbooks/release1-deployment.md).
