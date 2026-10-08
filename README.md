# Unit Toko — platform operasional koperasi BNI

Unit Toko melayani pemesanan cabang BNI dan akun bisnis yang disetujui. Cakupan lengkapnya mencakup katalog/kemasan, persediaan, pengadaan, pengiriman dan bukti penerimaan, invoice, bukti pembayaran/OCR, peninjauan bank, alokasi, retur/refund, peran, notifikasi, laporan dan tutup periode. Migrasi penuh masih berjalan; hasil rapat/manual dan seluruh [97 butir laporan](docs/requirements/unit-toko-report-2026-09-28.md) tetap berlaku.

Status dan pekerjaan tersisa dicatat dalam satu [ledger kemajuan, checklist dan pemetaan persyaratan](docs/quality/production-progress.md). [Urutan pelaksanaan M0–M6](docs/superpowers/plans/2026-09-29-accelerated-completion.md) tetap menjadi acuan; checklist Release 1 yang selesai tidak berarti proyek lengkap selesai.

## Status yang dapat digunakan

| Lingkungan | Batas yang sudah diketahui |
| --- | --- |
| Pilot lokal Release 1 | Browser → API .NET → MongoDB telah diverifikasi untuk akun cabang, katalog/kemasan, permintaan, keputusan petugas dan riwayat. Bukti lokal juga mencakup isolasi cabang, pemulihan respons hilang, pemeliharaan katalog serta persistensi setelah restart. Identitas dan data contoh bersifat sintetis. |
| [Frontend Vercel](https://unit-toko-bni.vercel.app/) | Frontend dipublikasikan sebagai pratinjau dengan pemberitahuan layanan belum tersedia. Publikasi dan tampilan publik tercatat dalam ledger; koneksi akun/API bisnis hosted belum diterima. |
| Sistem produksi lengkap | Belum diterima. Hosted API/worker, MongoDB, Auth0, penyimpanan privat, kebijakan bisnis, migrasi/restore dan pilot pengguna sebenarnya masih memerlukan koneksi atau penerimaan yang sesuai. |

Release 1 mencakup katalog dan permintaan cabang. **“Diterima untuk diproses” tidak mereservasi stok, menetapkan harga, membuat invoice, menerima pembayaran atau menjanjikan pengiriman.** Foto editorial bukan foto produk katalog yang telah disetujui. Lihat [cakupan dan bukti Release 1](docs/releases/release-1.md).

Pilot yang dipertahankan tetap memakai Release 1 dengan skema migrasi **78**. Pekerjaan sistem penuh, termasuk mixed-receipt costing dan migrasi berikutnya, berjalan pada validasi terisolasi; tidak dipasang ke pilot hanya karena build berhasil. [Ledger utama](docs/quality/production-progress.md) membedakan source kandidat, hasil terminal dan penerimaan per persyaratan.

## Struktur dan teknologi

| Direktori | Tanggung jawab |
| --- | --- |
| [frontend/](frontend/) | Next.js 16, React dan TypeScript; halaman, komponen, [modul UI](frontend/src/modules/), klien API dan pemeriksaan frontend |
| [backend/](backend/) | C#/.NET 10 API dan worker, modul bisnis, MongoDB transaksional, migrasi, bukti privat, kontrak API dan alat pilot |
| [docs/](docs/) | Persyaratan, arsitektur, runbook operator, bukti penerimaan, rencana pelaksanaan dan deployment |

Identitas produksi disiapkan untuk **Auth0**, verifikasi email, MFA, sesi server dan izin sesuai lingkup; penerimaan tenant sebenarnya masih terbuka. Dokumen keuangan memakai penyimpanan privat dengan akses terotorisasi. OCR memakai satu mesin lokal yang dipatok: **Tesseract 5.5.3 / tesserocr 2.11.0**, model `ind+eng`. [OCR gambar dan PDF pemasok](docs/pdf-document-ocr.md) memiliki checkpoint lokal; pembayaran tetap memerlukan bukti gambar, dan OCR dokumen pengiriman belum diterima. Hasil OCR adalah saran untuk pemeriksaan manusia, bukan persetujuan atau bukti uang diterima.

**Penghentian runtime dan build legacy belum selesai.** `backend/legacy`, dependensi libSQL/Turso/SQLite, adapter dan pemeriksaan regresinya masih dipertahankan sampai pengganti serta penghapusannya diverifikasi. Legacy bukan penulis data pilot Release 1. [Peta transisi modul](docs/architecture/module-boundaries.md) menyimpan konteks migrasi; status aktual tiap alur mengikuti ledger.

Profil bawaan tetap `release1`: frontend `NEXT_PUBLIC_UNIT_TOKO_RELEASE=release1`, API `Release__Profile=release1`. Halaman dan API di luar cakupan pilot dibatasi. Profil `full` hanya untuk validasi terisolasi sampai prasyaratnya diterima; jangan mengaktifkannya pada pilot/deployment untuk melewati batas tersebut. Build frontend penuh memakai `.next-full`, sedangkan pilot memakai `.next`. Build dan pemeriksaan tipe tetap dijalankan berurutan karena Next memperbarui berkas tipe bersama.

## Membuka pilot lokal

Pilot persisten ini khusus Windows: PowerShell 7, Node.js 24, SDK sesuai [backend/global.json](backend/global.json), MongoDB yang telah diperiksa dan Microsoft Edge. Ikuti [penyiapan dependensi, build dan jalur alat](docs/runbooks/local-pilot-release1.md) terlebih dahulu. Setelah build Release 1 tersedia, jalankan dari root repositori:

```powershell
./backend/tools/UnitToko.LocalPilot/start-local-pilot.ps1 -MongoPath 'C:\lokasi\mongod.exe'
```

Ganti jalur tersebut dengan executable sebenarnya; parameter `-DotnetPath` dan `-NodePath` tersedia jika diperlukan. [Launcher](backend/tools/UnitToko.LocalPilot/start-local-pilot.ps1) tidak mengunduh alat atau membuat build diam-diam. Jangan membangun ulang `.next` yang sedang dipakai pilot.

Setelah muncul **SYNTHETIC LOCAL PILOT**, jalankan `tmp/pilot/release1/open-browser.ps1` di jendela PowerShell 7 lain. Pintasan memakai Edge khusus pilot, loopback dan pin sertifikat. Buka `tmp/pilot/release1/ACCOUNTS-PRIVATE.txt` **secara privat** untuk akun `pemohon-a`, `pemohon-b` atau `petugas`, password acak dan kode faktor simulasi masing-masing. Keluar sebelum berganti akun; faktor simulasi ini bukan enrollment MFA/Auth0 sebenarnya.

Permintaan dan perubahan katalog tersimpan di MongoDB lokal terpisah. `Ctrl+C` menghentikan API/Next; MongoDB dan state privat tetap dipertahankan. Jalankan launcher yang sama untuk melanjutkan. Jangan membagikan, memasukkan ke Git, menghapus atau memisahkan database dari `tmp/pilot/release1` dan berkas kuncinya. Prosedur lengkap serta batas akun sintetis tersedia dalam panduan pilot.

## Panduan pengembangan dan operasi

| Kebutuhan | Mulai dari sini |
| --- | --- |
| Kebutuhan dan sumber asli | [Spesifikasi](docs/requirements/unit-toko-production.md), [manual/diagram/rapat dan riwayat](docs/source-material/README.md), [keputusan bisnis](docs/quality/report-decisions.md) |
| Setup dan kontrak API | [Pengembangan lokal](docs/runbooks/local-development.md), [skrip frontend](frontend/package.json), [kontrak API](backend/contracts/) |
| Modul dan alur terpelihara | [Identitas](frontend/src/modules/identity/README.md), [katalog](frontend/src/modules/catalog/README.md), [ordering](frontend/src/modules/ordering/README.md), [fulfillment](frontend/src/modules/fulfillment/README.md), [purchase order](docs/runbooks/purchase-orders.md), [alokasi pembayaran](docs/runbooks/payment-allocation.md) |
| Keamanan dan bukti privat | [Izin](docs/architecture/permissions.md), [otoritas orang](docs/architecture/person-authority.md), [hosting Auth0/sesi](docs/runbooks/identity-hosting.md), [admission bukti](docs/runbooks/private-evidence-admission.md), [penyimpanan privat](docs/runbooks/private-evidence-storage.md) |
| OCR lokal | [Engine, preprocessing dan kebijakan worker](docs/runbooks/document-processing.md), [konfigurasi lokal](docs/runbooks/local-document-processing.md), [invoice/SPH pemasok](docs/runbooks/supplier-documents.md) |
| Pengujian | [Browser/API/Mongo terisolasi](docs/runbooks/stock-count-browser-rehearsal.md), [CI terfokus](.github/workflows/quality.yml), [validasi rilis lengkap](.github/workflows/release-validation.yml), [bukti dan batas hasil](docs/quality/production-progress.md) |
| Deployment, migrasi dan restore | [Deployment Release 1](docs/runbooks/release1-deployment.md), [penerimaan produksi dan backup/restore](docs/quality/production-acceptance.md), [pemulihan akses](docs/runbooks/account-recovery.md) |
| Seluruh backlog | [97 persyaratan asli](docs/requirements/unit-toko-report-2026-09-28.md), [traceability dan pekerjaan tersisa](docs/quality/production-progress.md#current-traceability--all-97-report-items) |

Skrip frontend yang tersedia mencakup `typecheck`, `lint`, `test` dan `build`; sebagian regresi masih menguji legacy. Pemeriksaan yang menulis data memakai database terisolasi, bukan database pilot. Selama implementasi, **Focused quality** dan **Release validation** dijalankan manual; job dan pemeriksaan sebenarnya tetap dipertahankan. Jalankan CI lengkap setelah integrasi siap dan patuhi required checks sebelum merge. Hasil fixture lokal tidak membuktikan penerimaan hosted.

Vercel memakai root `frontend`, Node.js 24 dan [konfigurasi deployment](frontend/vercel.json) dengan Git otomatis hanya dari `main`. Publikasi frontend tidak menjalankan API/worker .NET. Peluncuran sebenarnya memerlukan ingress HTTPS/API yang disetujui, MongoDB replica set, Auth0, private storage dan operator/kebijakan bisnis, lalu verifikasi browser, izin, persistensi, worker dan restore pada koneksi sebenarnya. Push GitHub atau status Ready Vercel saja belum memenuhi gerbang itu.

## Arsip yang tetap dipertahankan

[README demo lama](frontend/README.md), [rilis visual v15](docs/rilis-v15.md), [landing editorial v13](docs/rilis-v13.md), [panduan publikasi lama](docs/publikasi-github.md) dan [kronologi progres](docs/archive/production-progress-before-consolidation-2026-10-08.md) menyimpan konteks desain/regresi. Instruksi akun, database, status proses dan deployment historis tidak menggantikan ledger serta panduan pilot saat ini. Sumber, bukti bisnis, data privat dan pekerjaan belum selesai dipertahankan; hasil build obsolete yang dihapus dicatat beserta backup dalam ledger.
