# Rilis v10 — Katalog produk dan penyelesaian v9

Lingkup: demo capstone Unit Toko untuk studi kasus penjualan kebutuhan divisi BNI. Rilis ini menggabungkan perbaikan v9 dengan penambahan katalog v10.

## Katalog

200 produk tambahan memiliki nama, deskripsi Bahasa Indonesia, harga, satuan, dan foto masing-masing. Pembagian data terdiri dari 40 minuman, 60 pantry/konsumsi, 40 kebutuhan kantor, dan 60 merchandise. Fixture awal lengkap menjadi 236 SKU dalam 212 keluarga produk; instalasi yang sudah memiliki produk tambahan dapat menampilkan jumlah lebih besar.

Katalog pelanggan dan staf menampilkan 24 keluarga produk per halaman. Pencarian nama/SKU, filter kategori/koleksi/stok, serta pengurutan harga bekerja bersama pagination. Nomor halaman tersimpan pada URL, dan membuka rincian produk mempertahankan konteks kembali ke hasil pencarian.

Penambahan produk, stok awal, dan jurnal persediaan dilakukan atomik satu kali. Produk lama tidak diisi ulang, transaksi dan akun tidak direset. Periode tertutup dilindungi. Rincian mekanisme serta pengujiannya tersedia pada [implementasi katalog](implementation-v10-catalog.md).

Foto dibuat satu per produk menggunakan image generation bawaan. PNG sumber dipertahankan secara lokal; situs memakai WebP 1024 × 1024 agar ringan. Manifest prompt, deskripsi, dan pemetaan file dijelaskan pada [data dummy](data-dummy-v10.md).

## Perbaikan v9 yang disertakan

- Logo vektor U–T baru, konsisten pada beranda, toko, portal login, sidebar, dan favicon.
- Animasi pembukaan landing/login, transisi saat menggulir, dan gerak foto ringan pada desktop. Preferensi pengurangan gerakan dihormati; isian login tetap stabil.
- Reset password oleh admin dengan verifikasi ulang, alasan, pencabutan sesi target, dan perlindungan retry.
- Pelepasan cadangan stok bermasalah oleh staf serta penghentian penjualan SKU oleh Kepala Toko.
- Penolakan pembayaran, kredit, refund, dan biaya dengan alasan; pengembalian laporan untuk perbaikan.
- Lampiran desain/spesifikasi pesanan dan rincian pengganti barang pada akun pelanggan.

Lihat [catatan v9](rilis-v9.md) dan dokumen audit yang ditautkan di dalamnya.

## Verifikasi lokal

- 209/209 tes domain, akses peran, autentikasi, migrasi, dan transaksi lulus.
- 14/14 kelompok pemeriksaan HTTP akses mencakup 11 akun; 22/22 kelompok alur pelanggan lulus.
- Pemeriksaan TypeScript dan build produksi lulus.
- Lint: 0 error, 9 peringatan lama.
- Browser: foto dan deskripsi SKU baru termuat; pencarian kode menemukan produk yang tepat; tampilan rincian pada lebar ponsel tidak meluap horizontal; tombol tambah memasukkan produk ke keranjang.

Seluruh 200 gambar selesai, memiliki hash file yang berbeda, dan berhasil didekode sebagai WebP 1024 × 1024. Total ukuran aset pengiriman adalah 11.395.138 byte (sekitar 11,4 MB). PNG sumber dan metadata asal tersedia secara lokal. Hasil pemeriksaan tersimpan pada [verifikasi gambar](data-dummy-v10-results.json).

Katalog publik lokal juga lolos pemeriksaan 200 produk, 200 deskripsi berbeda, 200 jalur gambar berbeda, serta lima sampel gambar dari seluruh rentang. Pemeriksaan browser mencakup pencarian 200 produk dalam sembilan halaman, perpindahan halaman, filter kategori yang kembali ke halaman pertama, pengurutan harga, rincian produk, keranjang, dan pengalihan checkout tamu ke login pelanggan.

## Publikasi dan pemeriksaan produksi

Rilis diterbitkan pada 22 September 2026 di [unit-toko-bni.vercel.app](https://unit-toko-bni.vercel.app). Deployment `dpl_7eGpUNkxhTA339jysMqoFL9PRY9J` berstatus **Ready**, dipromosikan, dan tujuan domain utama diperiksa kembali. Build produksi Vercel lulus.

- **25/25** kelompok pemeriksaan halaman dan autentikasi lulus, mencakup 11 akun, pemisahan portal, penolakan portal yang salah, dan pencabutan sesi pengujian saat logout. Lihat [hasil akses produksi](vercel-v10-results.json).
- Katalog publik memuat seluruh **200 produk baru**, masing-masing dengan deskripsi dan jalur gambar unik yang cocok dengan manifest. Lima sampel aset dari seluruh rentang berhasil dimuat sebagai WebP. Lihat [hasil katalog produksi](public-catalog-v10-results.json).
- Browser produksi menampilkan logo baru, beranda, dan etalase dengan foto termuat. Etalase saat pemeriksaan berisi 212 keluarga produk, ditampilkan 24 per halaman dalam sembilan halaman.

## Batas demo

Harga, stok, foto, pengiriman, dan pembayaran adalah simulasi. Tidak ada integrasi bank, payment gateway, atau kurir eksternal. Pengujian transaksi dan perubahan password memakai database QA lokal/SQLite memori. Pemeriksaan produksi memakai halaman, login/logout, katalog publik, dan gambar publik; bukan ekspor atau perbandingan seluruh database. Provisioning 200 produk adalah perubahan data dummy yang disengaja dalam rilis ini.
