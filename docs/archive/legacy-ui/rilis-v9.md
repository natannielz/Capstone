# Rilis v9 — Identitas, gerak, dan penyelesaian alur

Tanggal: 22 September 2026. Lingkup: demo capstone Unit Toko untuk studi kasus divisi BNI.

## Perubahan

- Monogram baru menyatukan T di dalam U dengan ketebalan dan jarak yang konsisten. Satu warna petroleum pada latar terang, putih pada latar gelap. Logo digunakan pada beranda, toko, dua login, sidebar, dan favicon; tersedia sebagai SVG agar tetap tajam.
- Landing page mendapat pembukaan teks/foto, transisi bagian saat digulir, dan pergeseran foto ringan pada desktop. Dua portal login mendapat pembukaan foto dan judul; kolom isian tidak ikut digerakkan. Animasi berhenti mengikuti preferensi reduced motion, dibersihkan saat pindah halaman, dan diselesaikan ketika pengguna menavigasi dengan keyboard. Isi halaman tetap dirender tanpa bergantung pada animasi.
- Cadangan stok dari batch bermasalah dapat dilepas oleh staf dengan alasan, kemudian diganti batch layak. Jumlah yang sudah ada pada Surat Jalan siap kirim dilindungi. Kepala Toko dapat menghentikan/menjalankan kembali penjualan SKU tanpa mengubah transaksi lama.
- Penagihan dapat menolak bukti pembayaran yang belum diverifikasi. Pimpinan dapat menolak pengajuan kredit, refund, dan biaya serta mengembalikan laporan untuk perbaikan. Alasan dan riwayat disimpan; keputusan tidak membuat jurnal atau transfer dana.
- Admin dapat mereset password akun lain dengan verifikasi password admin saat ini dan alasan. Penggantian kredensial, pencabutan sesi target, audit, serta pencatatan retry dilakukan atomik. Password tidak disimpan dalam log; retry tidak mengulang reset setelah pemilik mengganti password lagi.
- Riwayat pelanggan membuka SKU kemasan yang benar dan menampilkan nilai serta keputusan pengganti. Data divisi/pemasok kini dapat diubah melalui UI. Pemisahan akses pelanggan, PIC divisi, dan petugas tetap berlaku.
- Lampiran desain/spesifikasi pesanan menghubungkan kebutuhan merchandise dengan pemeriksaan toko. Rincian kontrol dan hasil tes dicatat setelah verifikasi gabungan.
- Pratinjau pengembangan mengizinkan alamat loopback `127.0.0.1` agar sumber daya development Next.js dapat dimuat; konfigurasi ini hanya berlaku untuk development.

## Dasar desain dan audit

Logo digambar khusus untuk Unit Toko. Prinsip kontras, ruang bebas, dan penerapan satu warna merujuk pada [IBM Design Language](https://www.ibm.com/design/language/ibm-logos/8-bar/); bentuk logo IBM tidak disalin. Tipografi DM Sans dan Archivo serta palet yang ada dipertahankan. Foto ilustrasi yang tersedia digunakan kembali.

- [Audit akun dan pelanggan](audit-accounts-v9.md)
- [Audit operasional](audit-operasional-v9.md)
- [Audit keuangan](audit-finance-v9.md)
- [Rancangan gerak halaman](design-motion-v9.md)

## Validasi dan publikasi

Validasi v9 lulus 199 tes domain/RBAC/auth; setelah katalog v10 ditambahkan, suite gabungan lulus 209 tes. Pemeriksaan HTTP 14 kelompok akses dan 22 kelompok pelanggan, TypeScript, serta build produksi juga lulus. Lint tidak memiliki error; terdapat 9 peringatan lama.

Pemeriksaan browser mencakup logo dan landing pada lebar desktop serta ponsel, gerak foto saat menggulir, navigasi keyboard, login pelanggan, login admin, dan dialog reset password. Tidak ada password yang direset pada pemeriksaan browser. Dukungan reduced motion diperiksa pada implementasi; preferensi tersebut tidak diemulasikan dalam browser pengujian.

Perubahan ini diterbitkan bersama katalog v10. Status publikasi dan pemeriksaan akhir dicatat pada [rilis v10](rilis-v10.md).

## Batas

Pembayaran, pengiriman, refund, dan data tetap simulasi. Tidak ada gateway pembayaran, integrasi bank/kurir eksternal, signup publik, atau pesan otomatis. Pengujian alur yang mengubah data dilakukan pada database QA lokal atau SQLite di memori. Pemeriksaan produksi dibatasi pada halaman, login/logout, katalog publik dan gambar; bukan perbandingan seluruh isi database sebelum dan sesudah rilis. Tidak ada reset password pengguna atau transaksi uji pada produksi. Katalog v10 menambahkan stok simulasi dan jurnal pembukanya satu kali.
