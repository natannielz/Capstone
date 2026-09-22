# Rilis v8 — Beranda, akses terpisah, dan identitas Unit Toko

Tanggal: 22 September 2026.

## Perubahan

- `/` kembali menjadi landing page publik dengan foto pilihan toko, penjelasan layanan, dan dua pintu masuk yang jelas.
- `/shop` menjadi etalase khusus pengunjung/pelanggan; pelanggan masuk melalui `/customer/login`.
- PIC divisi, admin, dan petugas menggunakan `/staff/login` menuju `/workspace`. Server mengarahkan akun internal dari halaman produk, keranjang, checkout, dan akun pelanggan ke ruang kerja sebelum etalase ditampilkan.
- Login memvalidasi kredensial sebelum memeriksa kecocokan portal. Akun yang salah portal mendapat petunjuk menuju akses yang benar tanpa sesi baru. Tautan `/login` lama tetap mengarahkan ke portal sesuai tujuan lokal yang diizinkan. Tujuan setelah login tetap mengikuti peran sebenarnya.
- Monogram UT dipakai konsisten pada beranda, toko, login, sidebar, dan favicon. Warna utama hijau petroleum `#073B45`, latar terang `#F4F7F7`, teks `#142D33`, dan aksen oranye `#C84513`.
- Navigasi kategori memiliki penanda aktif, tombol penting lebih kontras, target sentuh minimal 44 piksel pada navigasi utama, dan jarak antarelemen disesuaikan untuk ponsel.
- Filter katalog dan pilihan kemasan memperbarui tampilan bersama alamat halaman. Sebelumnya, perubahan riwayat browser membawa penanda internal router sehingga alamat berubah tetapi komponen tetap menampilkan pilihan sebelumnya. Panggilan riwayat kini memakai pola integrasi Next.js yang didokumentasikan.

## Validasi lokal

- 157/157 tes regresi domain, RBAC, autentikasi, transaksi, retry, dan penyimpanan lulus.
- 14/14 kelompok HTTP `scripts/smoke-access-v8.mjs` lulus pada database QA terisolasi. Mencakup 11 akun, semua 10 identitas internal, login salah portal, tujuan aman, serta pengalihan halaman pelanggan.
- 22/22 kelompok HTTP `scripts/smoke-customer.mjs` lulus. Alur pesanan hingga invoice/pembayaran simulasi, pemisahan pemilik, lampiran, dan perubahan kredensial tetap berjalan.
- TypeScript lulus. Lint: 0 error dan 9 warning yang sudah ada (7 navigasi penuh pada batas sesi, 2 variabel tes lama).
- Build produksi Next.js lulus; beranda dirender statis dan seluruh halaman yang bergantung akun diperiksa dinamis di server.
- Pemeriksaan browser pada lebar 320, 390, 768, 980, dan 1440 piksel tidak menemukan overflow horizontal pada beranda. Kedua login, katalog, navigasi kategori aktif, logo sidebar, serta pesan login akun internal diperiksa. Filter satu kolom pada 320 piksel dan opsi stok pada 380 piksel terlihat utuh.
- Browser mengonfirmasi login admin menuju `/workspace`, kunjungan admin ke `/shop` kembali ke `/workspace`, dan logout menuju `/staff/login`. Filter stok memperbarui nilai dan chip aktif; tombol Back mengembalikan nilai sebelumnya. Memilih Paket 3 pak kopi memperbarui SKU menjadi `OMI-003-P3` dan harga menjadi Rp85.500.

## Publikasi

Deployment `dpl_Cog7DZH6vniHnTJFzJRxTtxk9J8w` berstatus READY dan dipromosikan ke [unit-toko-bni.vercel.app](https://unit-toko-bni.vercel.app) pada 22 September 2026. Build cloud lulus. Inspect domain publik mengonfirmasi deployment tersebut; beranda baru juga telah diperiksa langsung di browser.

**25/25 kelompok pemeriksaan produksi lulus** untuk seluruh 11 akun. Login salah portal ditolak tanpa cookie, login pada portal yang benar berhasil, seluruh 10 akun internal diarahkan dari halaman pelanggan, dan pelanggan tidak dapat masuk workspace. Tautan login lama tetap berfungsi. Seluruh sesi pemeriksaan diakhiri dan pengalihan setelah logout diverifikasi. Hasil tersedia pada [vercel-v8-results.json](vercel-v8-results.json).

Validasi produksi berfokus pada halaman dan login/logout. Pemeriksaan ini bukan perbandingan isi database produksi sebelum/sesudah rilis, dan tidak membuat transaksi atau mengunggah dokumen uji ke produksi.

## Batas

Ini tetap demo capstone dengan transaksi dan pembayaran simulasi. Tidak ada akun baru, perubahan password, atau migrasi data bisnis dalam rilis v8. Foto ilustrasi yang sudah tersedia digunakan kembali; logo dibuat sebagai SVG agar tetap tajam.
