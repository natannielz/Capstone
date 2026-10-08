# Rilis v11 — Pengalaman belanja dan kontrol proses

Terbit 23 September 2026 di [unit-toko-bni.vercel.app](https://unit-toko-bni.vercel.app).

## Tampilan dan interaksi

Beranda memakai komposisi foto melengkung, kartu produk bertumpuk, judul yang terbuka per baris, serta gerak foto ringan saat menggulir atau mengarahkan pointer desktop. Kartu koleksi mengarah ke filter katalog yang sesuai. Login pelanggan dan petugas menggunakan komposisi produk yang sama, dengan isian dan tombol tetap stabil.

Etalase pelanggan memperoleh pembuka bergambar, kategori yang lebih mudah dikenali, serta penekanan foto, harga dan tindakan pada kartu barang. Pencarian dan penyaringan tetap menjadi prioritas ketika pembeli sedang mencari produk. Filter dan pagination membawa layar ke hasil baru; kembali dari rincian memulihkan posisi sebelumnya. Konfirmasi dan animasi keranjang hanya muncul sesudah perubahan berhasil. Aset memakai katalog 200 gambar dan deskripsi unik yang sudah tersedia; tidak ada angka penjualan, ulasan, rating atau diskon buatan.

Gerak menggunakan scroll native, animasi masuk sekali, dan respons tindakan yang singkat. Preferensi pengurangan gerakan dihormati; konten tetap terlihat tanpa JavaScript animasi. Rincian arah desain terdapat pada [rencana pengalaman](plan-experience-v11.md).

## Audit dokumen

Sumber kajian terdiri dari tujuh bab BPP dan sepuluh diagram asli. Dokumen diperlakukan sebagai referensi proses studi kasus. Alur pelanggan daring dan pencatatan keuangan simulasi tetap dibedakan dari integrasi operasional OMI/Smart, bank, kasir walk-in, dan faktur pajak resmi.

- [Audit penjualan dan penerimaan](gap-sales-v11.md): penerimaan yang dicatat staf wajib memiliki bukti unggahan untuk pengiriman dan pembeli yang tepat. Konfirmasi langsung oleh akun pembeli pemilik pesanan tetap tersedia. Teks bebas tidak lagi cukup untuk melewati kontrol bukti.
- [Audit pengadaan dan keuangan](gap-operations-v11.md): Kepala Toko dapat menutup sisa pengadaan pemasok dengan alasan wajib. Jumlah awal, diterima, dan dibatalkan tetap terpisah; pembayaran berikutnya dibatasi pada nilai barang diterima. Uang muka yang belum dipakai memblokir penutupan. Alokasi dana internal tetap tercatat, dengan selisih yang terlihat untuk rekonsiliasi.

Transaksi historis tidak ditulis ulang. Lampiran penerimaan diperiksa berdasarkan metadata yang disahkan layanan unggah; tanda tangan pada berkas tetap memerlukan pemeriksaan petugas. Kehilangan berkas akibat penghapusan di luar aplikasi masih menjadi batas ketahanan penyimpanan.

Permintaan peninjauan pascafinal belum dapat diajukan langsung oleh pembeli di dalam aplikasi. Penagihan sudah memiliki nota kredit dan pengembalian saldo dengan persetujuan, tetapi serah terima masalah pascafinal dari pembeli masih dilakukan di luar aplikasi. Kebijakan retur fisik pelanggan tidak disimpulkan dari BPP retur toko ke pemasok.

Rekonsiliasi SPH pemasok secara rinci, penyelesaian pengembalian uang muka/klaim pemasok, dan pelepasan alokasi internal juga belum tersedia. Saldo tersebut tetap terlihat dan tidak dihapus oleh penutupan pengadaan. Dua audit di atas memisahkan fitur yang tersedia, kontrol yang diperbaiki, dan batas demo yang masih terbuka.

## Validasi dan publikasi

- **224/224 tes** lulus, termasuk 8 skenario bukti penerimaan dan 7 skenario penutupan pengadaan baru. Cakupan meliputi aturan bisnis, stok, jurnal, batas peran, kepemilikan data, autentikasi, retry dan kompatibilitas data lama.
- **14/14** kelompok HTTP pemisahan akses mencakup 11 akun; **22/22** kelompok HTTP alur pelanggan lulus pada database QA lokal. Ketiga produk fixture QA otomatis dinonaktifkan setelah pengujian.
- TypeScript dan build produksi Vercel lulus. Lint: 0 error, 9 peringatan lama.
- Browser lokal: landing/login desktop dan ponsel; etalase pada lebar 320/390 px dan desktop; pencarian, kategori, pengurutan, pagination, tambah barang, rincian, keranjang kosong/terisi dan login saat checkout tamu. Tidak ada overflow horizontal pada lebar yang diperiksa. Pencarian “Barang QA” menghasilkan 0 produk.
- Perilaku reduced motion dan pembersihan animasi diperiksa pada kode. Preferensi sistem reduced motion tidak diemulasikan pada pemeriksaan browser ini. Siklus context animasi yang ditemukan saat pengujian browser telah diperbaiki sebelum build produksi.
- Deployment **`dpl_DS8tgEemXo3Qj56sqR6y1aKy5M4s`** berstatus Ready, dipromosikan, dan tujuan domain utama diverifikasi.
- **25/25** kelompok pemeriksaan halaman/autentikasi produksi lulus untuk 11 akun, termasuk penolakan portal salah dan pencabutan sesi pengujian. [Hasil pemeriksaan](vercel-v11-results.json).
- Katalog produksi memuat 200 produk tambahan dengan deskripsi dan jalur gambar unik sesuai manifest, tanpa barang QA aktif. Lima sampel gambar berhasil dimuat. [Hasil katalog](public-catalog-v11-results.json).

Pengujian transaksi menggunakan database QA lokal atau state terisolasi. Pemeriksaan produksi memakai halaman, autentikasi, katalog dan aset publik; tidak mengekspor atau membandingkan keseluruhan database.
