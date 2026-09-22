# Publikasi GitHub dan Vercel

Tanggal persiapan: 21 September 2026. Repositori tujuan: [natannielz/Capstone](https://github.com/natannielz/Capstone), branch `main`.

## Isi repositori

Publikasi awal menyimpan snapshot v6 dan rencana v7. Pembaruan v7 menambahkan etalase, akun pelanggan, alur pesanan, migrasi identitas pembeli, dan pengujian terkait. Aplikasi berada di `website/`, dokumentasi terpilih di `docs/`, dan panduan utama di root. Cakupan serta bukti rilis tersedia pada [rilis-v7.md](rilis-v7.md).

Hanya source, migrasi, tes, aset publik, lisensi font, contoh environment tanpa nilai rahasia, serta dokumen terpilih yang disertakan. Arsip referensi, database/unggahan lokal, `.env` asli, daftar password, log, metadata akun hosting, dan riwayat Git lama tidak masuk snapshot publik.

## Workspace sumber dan checkout publikasi

Pada workspace awal, kode pengembangan berada di `website/` dengan riwayat Git lokalnya sendiri. Repositori publik memakai struktur root Capstone. Checkout publikasi yang terpisah berada di `.tools/github-publish`; direktori ini diabaikan Git dan tidak menjadi sumber kode pengembangan.

Untuk pembaruan dari workspace awal, sinkronkan source dan dokumen terpilih melalui `.tools/export-github.py`, lalu periksa perubahan pada checkout publikasi, commit, dan push biasa. Script menolak file privat dan memeriksa nilai credential yang diketahui tanpa mencetaknya. Penghapusan atau penggantian source yang telah dipublikasikan harus diperiksa eksplisit; script tidak menghapus berkas lama secara otomatis. Jangan mengubah hanya checkout publikasi sehingga kode sumber tertinggal.

Clone baru dari GitHub dapat digunakan langsung sebagai workspace pengembangan; jalankan aplikasi dari `website/`. Tidak perlu membuat Git bersarang atau memakai script workspace awal untuk clone baru.

## Vercel

Project aplikasi yang sudah ada adalah `unit-toko-bni`; URL publik [unit-toko-bni.vercel.app](https://unit-toko-bni.vercel.app). Deployment final v7 `dpl_CS5rq8MVbegyMRUy7ax9EGr97Z7C` dibangun dari source kanonis `website/` dan dipromosikan ke URL publik tersebut pada 22 September 2026. Hasil verifikasi tersedia pada [rilis-v7.md](rilis-v7.md) dan [vercel-v7-results.json](vercel-v7-results.json).

Pengaturan bila menghubungkan atau mengimpor struktur repositori ini melalui integrasi Git:

| Pengaturan | Nilai |
| --- | --- |
| Root Directory | `website` |
| Framework | Next.js |
| Runtime | Node.js 24.x |
| Install | `npm ci` |
| Build | `npm run build` |
| Output | Default Next.js |

Deployment CLI existing dijalankan langsung dari direktori `website/`, sehingga Root Directory project existing tetap `.`. Jangan mengubahnya menjadi `website` ketika mengunggah hanya isi direktori aplikasi melalui CLI. Tabel di atas berlaku untuk checkout monorepo melalui integrasi Git.

Environment privat: `DEMO_PASSWORD_SEED`, `TURSO_DATABASE_URL`, `TURSO_AUTH_TOKEN`, dan `BLOB_READ_WRITE_TOKEN`. `NEXT_PUBLIC_APP_URL` berisi URL publik aplikasi. Gunakan database Turso remote dan Blob privat untuk Vercel; jangan mengunggah `.data` atau `.env.local`.

Push ke GitHub tidak dengan sendirinya membuktikan deployment baru atau koneksi Git otomatis. Rilis v7 menggunakan CLI pada project existing, build tanpa memindahkan domain terlebih dahulu, lalu promosi setelah build berhasil. Backup dan rehearsal migrasi dilakukan sebelum promosi; pemeriksaan data serta autentikasi dilakukan sesudahnya. Preview yang memakai database production bersama tidak boleh digunakan untuk uji transaksi.
