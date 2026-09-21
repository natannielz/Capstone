# Publikasi GitHub dan Vercel

Tanggal persiapan: 21 September 2026. Repositori tujuan: [natannielz/Capstone](https://github.com/natannielz/Capstone), branch `main`.

## Isi repositori

Publikasi awal memakai snapshot source v6 yang saat ini berjalan, termasuk berkas baru yang belum berada dalam riwayat Git lokal lama. Aplikasi berada di `website/`, dokumentasi terpilih di `docs/`, dan panduan utama di root. Rencana v7 tersedia pada [plan-customer-marketplace-v7.md](plan-customer-marketplace-v7.md); publikasi rencana tidak berarti fitur customer telah dibuat.

Hanya source, migrasi, tes, aset publik, lisensi font, contoh environment tanpa nilai rahasia, serta dokumen terpilih yang disertakan. Arsip referensi, database/unggahan lokal, `.env` asli, daftar password, log, metadata akun hosting, dan riwayat Git lama tidak masuk snapshot publik.

## Workspace sumber dan checkout publikasi

Pada workspace awal, kode pengembangan berada di `website/` dengan riwayat Git lokalnya sendiri. Repositori publik memakai struktur root Capstone. Checkout publikasi yang terpisah berada di `.tools/github-publish`; direktori ini diabaikan Git dan tidak menjadi sumber kode pengembangan.

Untuk pembaruan dari workspace awal, sinkronkan source dan dokumen terpilih melalui `.tools/export-github.py`, lalu periksa perubahan pada checkout publikasi, commit, dan push biasa. Script menolak file privat dan memeriksa nilai credential yang diketahui tanpa mencetaknya. Penghapusan atau penggantian source yang telah dipublikasikan harus diperiksa eksplisit; script tidak menghapus berkas lama secara otomatis. Jangan mengubah hanya checkout publikasi sehingga kode sumber tertinggal.

Clone baru dari GitHub dapat digunakan langsung sebagai workspace pengembangan; jalankan aplikasi dari `website/`. Tidak perlu membuat Git bersarang atau memakai script workspace awal untuk clone baru.

## Vercel

Project aplikasi yang sudah ada adalah `unit-toko-bni`; URL publik [unit-toko-bni.vercel.app](https://unit-toko-bni.vercel.app). Rilis v6 dan batas verifikasi dicatat pada [rilis-v6.md](rilis-v6.md).

Pengaturan untuk struktur repositori ini:

| Pengaturan | Nilai |
| --- | --- |
| Root Directory | `website` |
| Framework | Next.js |
| Runtime | Node.js 24.x |
| Install | `npm ci` |
| Build | `npm run build` |
| Output | Default Next.js |

Environment privat: `DEMO_PASSWORD_SEED`, `TURSO_DATABASE_URL`, `TURSO_AUTH_TOKEN`, dan `BLOB_READ_WRITE_TOKEN`. `NEXT_PUBLIC_APP_URL` berisi URL publik aplikasi. Gunakan database Turso remote dan Blob privat untuk Vercel; jangan mengunggah `.data` atau `.env.local`.

Push ke GitHub tidak dengan sendirinya membuktikan deployment baru atau koneksi Git otomatis. Publikasi awal ini menyimpan source dan rencana; deployment v7 dilakukan sesudah implementasi, pengujian, dan migrasi yang terverifikasi. Gunakan project Vercel existing dan hindari membuat layanan duplikat. Preview yang memakai database production bersama tidak boleh digunakan untuk uji mutasi/migrasi.
