# Unit Toko — platform operasional koperasi BNI

Unit Toko dikembangkan untuk pemesanan barang oleh cabang BNI dan akun bisnis yang disetujui, beserta persediaan, pengadaan, pengiriman, penerimaan, penagihan, pembayaran, retur/refund, laporan dan tutup periode. Migrasi penuh masih berjalan; persyaratan asli, hasil rapat/manual dan seluruh [97 butir laporan](docs/requirements/unit-toko-report-2026-09-28.md) tetap berlaku.

Acuan pekerjaan saat ini adalah [rencana M0–M6](docs/superpowers/plans/2026-09-29-accelerated-completion.md) dan [ledger kemajuan serta pemetaan persyaratan](docs/quality/production-progress.md). Ledger membedakan implementasi, verifikasi lokal, staging dan penerimaan produksi. Checklist Release 1 yang sudah selesai tidak berarti seluruh proyek selesai.

## Yang dapat digunakan sekarang

| Lingkungan | Status dan cakupan |
| --- | --- |
| Pilot lokal Release 1 | Alur browser → API .NET → MongoDB telah diverifikasi: akun cabang → katalog/kemasan → permintaan tersimpan → keputusan petugas → riwayat pemohon. Pemisahan cabang, pemulihan respons yang hilang, pengelolaan katalog dan persistensi setelah restart juga diperiksa. Akun dan katalog bersifat sintetis. |
| [Frontend Vercel](https://unit-toko-bni.vercel.app/) | Frontend telah dipublikasikan dengan tampilan awal, katalog dan halaman masuk yang dipulihkan. Koneksi layanan akun/API sebenarnya belum tersedia; halaman menyatakan keterbatasan tersebut. |
| Sistem produksi lengkap | Belum diterima. Koneksi hosted API/worker, MongoDB, Auth0, penyimpanan privat, migrasi/restore dan pilot operasional dengan pengguna sebenarnya masih memerlukan implementasi atau validasi. |

Dalam Release 1, **“Diterima untuk diproses” hanya keputusan atas permintaan**. Keputusan ini tidak mereservasi stok, menetapkan harga, membuat invoice, menerima pembayaran atau menjanjikan pengiriman. Foto editorial bukan foto barang katalog yang telah disetujui; foto produk yang belum tersedia ditampilkan secara jujur. Rincian bukti dan batasnya ada pada [catatan Release 1](docs/releases/release-1.md).

## Struktur dan teknologi

| Direktori | Isi |
| --- | --- |
| `frontend/` | Next.js, React dan TypeScript; halaman pelanggan/staf, komponen, aset, klien API serta pemeriksaan frontend |
| `backend/` | API dan worker C#/.NET 10, modul domain, MongoDB transaksional, migrasi, bukti privat dan alat pilot lokal; `backend/legacy/` menyimpan kode TypeScript lama yang masih dalam migrasi |
| `docs/` | Persyaratan, arsitektur, rencana pelaksanaan, bukti penerimaan, panduan operator dan deployment |

API baru mengelola data bisnis di MongoDB. Identitas produksi disiapkan untuk Auth0 dengan sesi server, verifikasi email, MFA dan izin sesuai lingkup. Bukti keuangan/dokumen menggunakan penyimpanan privat dan pemeriksaan akses; OCR memakai satu mesin lokal Tesseract/tesserocr yang versinya dipatok dan tidak menjadi otoritas persetujuan keuangan. Integrasi layanan sebenarnya tetap perlu diterima sebelum peluncuran.

Dependensi Turso/SQLite dan backend lama masih memiliki pemanggil yang harus dimigrasikan dan diverifikasi sebelum dihapus. Keduanya **bukan penulis data pilot Release 1**. Lihat [batas modul selama transisi](docs/architecture/module-boundaries.md).

Profil bawaan adalah `release1`: `NEXT_PUBLIC_UNIT_TOKO_RELEASE=release1` pada frontend dan `Release__Profile=release1` pada API. Halaman serta API fitur lanjutan dibatasi. Profil `full` digunakan secara eksplisit untuk pengembangan/pemeriksaan terisolasi; jangan mengaktifkannya pada pilot atau deployment sebelum alur, izin dan dependensi terkait selesai divalidasi.

## Menjalankan pilot lokal

Pilot persisten ini khusus Windows. Siapkan PowerShell 7, Node.js 24, SDK .NET 10 sesuai `backend/global.json`, `mongod.exe` yang telah diperiksa, serta Microsoft Edge. Dari direktori repositori:

```powershell
npm ci --prefix backend/legacy
npm ci --prefix frontend
$env:NEXT_PUBLIC_UNIT_TOKO_RELEASE = 'release1'
npm run build --prefix frontend
dotnet build backend/tools/UnitToko.LocalPilot/UnitToko.LocalPilot.csproj -c Release
./backend/tools/UnitToko.LocalPilot/start-local-pilot.ps1 -MongoPath 'C:\path\to\mongod.exe'
```

Ganti jalur MongoDB dengan lokasi sebenarnya. Instalasi dependensi lama masih diperlukan oleh build selama migrasi; perintah di atas tidak menjalankan server demo lama. Launcher tidak mengunduh alat atau membuat build secara diam-diam. Jalur alat yang telah diperiksa pada komputer penyerahan dan prosedur lengkap ada di [panduan pilot lokal](docs/runbooks/local-pilot-release1.md).

Setelah launcher menampilkan **SYNTHETIC LOCAL PILOT**, jalankan `tmp/pilot/release1/open-browser.ps1` dari jendela PowerShell 7 lain. Pintasan membuka Edge khusus pilot dengan pemetaan loopback dan pin sertifikat; gunakan pintasan tersebut agar batas HTTPS lokal tetap benar.

Buka `tmp/pilot/release1/ACCOUNTS-PRIVATE.txt` secara privat. Gunakan `pemohon-a`, `pemohon-b` atau `petugas` dengan password acak dan kode faktor **simulasi** masing-masing; keluar sebelum berganti akun. Ini bukan enrollment MFA/Auth0 sebenarnya. Jangan membagikan atau memasukkan berkas kredensial, kunci, profil browser maupun data MongoDB ke Git.

Permintaan dan perubahan katalog tersimpan di MongoDB lokal terpisah. `Ctrl+C` menghentikan API dan Next; MongoDB serta berkasnya tetap dipertahankan. Jalankan launcher yang sama untuk melanjutkan. Jangan menghapus `tmp/pilot/release1`: database dan state privatnya harus tetap berpasangan. Alur langkah demi langkah dan batas kedaluwarsa akun sintetis tersedia dalam panduan pilot.

## Pemeriksaan dan publikasi

CI push menjalankan pemeriksaan terfokus sesuai sumber yang berubah. Suite lengkap tetap tersedia melalui workflow manual **Release validation** untuk kandidat rilis terintegrasi. Gunakan database terisolasi untuk pemeriksaan yang mengubah data; skrip HTTP demo lama bukan penerimaan pilot baru. [Ledger](docs/quality/production-progress.md) mencatat hasil dan batas pemeriksaan yang berlaku.

Vercel memakai root `frontend` dan Node.js 24. Konfigurasi [frontend/vercel.json](frontend/vercel.json) mengaktifkan deployment Git otomatis hanya dari `main`; dependensi/build mengikuti berkas tersebut. Publikasi frontend sudah terjadi, tetapi push GitHub atau status Ready Vercel belum membuktikan layanan bisnis berjalan.

Aktivasi hosted memerlukan host .NET/worker, MongoDB replica set, tenant Auth0 dan identitas yang disetujui, penyimpanan privat serta `UNIT_TOKO_API_ORIGIN` menuju ingress HTTPS yang benar. Verifikasi browser, cookie/origin, izin, persistensi, worker dan restore harus menggunakan koneksi sebenarnya. Ikuti [prasyarat deployment Release 1](docs/runbooks/release1-deployment.md), [panduan identitas/hosting](docs/runbooks/identity-hosting.md) dan [gerbang penerimaan produksi](docs/quality/production-acceptance.md).

## Arsip desain dan demo

Catatan berikut mempertahankan konteks desain asli dan perilaku lama untuk migrasi/regresi. Daftar fitur, akun, rute, database dan instruksi deployment di dalam arsip tidak menggantikan cakupan atau cara menjalankan pilot saat ini.

| Versi | Dokumen historis |
| --- | --- |
| v15 — daftar operasional dan profil | [Rencana](docs/plan-ui-ux-v15.md), [hasil dan batas](docs/rilis-v15.md) |
| v14 — autoplay, footer, belanja dan ruang kerja | [Rencana/riset](docs/plan-ui-ux-v14.md), [hasil dan batas](docs/rilis-v14.md) |
| v13 — landing editorial dan portal masuk | [Rilis](docs/rilis-v13.md), [desain/gerakan](docs/research-motion-v13.md) |
| v12 — desain dan animasi | [Rilis](docs/rilis-v12.md), [riset](docs/research-experience-v12.md) |
| v11 — pengalaman belanja dan audit proses | [Rilis](docs/rilis-v11.md), [audit penjualan](docs/gap-sales-v11.md), [audit pengadaan](docs/gap-operations-v11.md) |
| v10 — katalog ilustrasi dan akun | [Rilis](docs/rilis-v10.md), [data dummy](docs/data-dummy-v10.md) |
| v8 — beranda dan akses terpisah | [Rilis](docs/rilis-v8.md) |
| v7 — etalase dan akun pelanggan | [Rilis](docs/rilis-v7.md), [rencana/cakupan](docs/plan-customer-marketplace-v7.md), [pengujian](docs/testing-customer-v7.md) |
| v6 — alur staf dan divisi | [Baseline regresi](docs/rilis-v6.md) |

[README frontend](frontend/README.md) dan [panduan publikasi lama](docs/publikasi-github.md) juga menyimpan konteks transisi; untuk menjalankan atau menerbitkan kandidat saat ini, gunakan panduan pilot/deployment di atas. Data demo, transaksi dan pembayaran sintetis bukan operasi resmi BNI. Arsip studi kasus, kredensial, database, unggahan privat dan log tidak disertakan dalam repositori.
