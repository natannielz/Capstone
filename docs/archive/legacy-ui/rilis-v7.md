# Rilis v7 — Etalase dan akun pelanggan

22 September 2026. Situs: [unit-toko-bni.vercel.app](https://unit-toko-bni.vercel.app). Source: [natannielz/Capstone](https://github.com/natannielz/Capstone).

## Perubahan

Etalase publik memakai pola marketplace: pencarian, kategori, pilihan kemasan, harga, foto, keranjang terpilih, dan Beli sekarang. Checkout terhubung ke operasional toko yang sudah ada. Pelanggan mengelola pesanan, pengiriman, penerimaan, komplain, tagihan, bukti simulasi, profil, foto, alamat bawaan, serta kata sandi dari halaman akun sendiri.

Satu akun tambahan disediakan: `customer@unit-toko.demo`, role pelanggan. Total 11 akun dan 10 peran. Password awal unik disampaikan privat; tidak ada password atau seed dalam repositori. Akun tidak dapat mendaftar atau mengganti role sendiri.

Pesanan baru menyimpan penerima dan alamat pada saat checkout. Pembayaran customer hanya untuk invoice sendiri yang sudah diterbitkan setelah penerimaan final. Checkout tidak membuat pesanan lunas. Penagihan memverifikasi dan mengalokasikan dana; saldo, nota kredit, dan pengembalian mengikuti identitas pembeli yang sama.

## Kendali data

Referensi pembeli membedakan divisi dengan customer, termasuk dua customer yang sama-sama tidak memiliki divisi. Server membatasi state, command, dokumen, ekspor, bukti, serta profil. Katalog publik hanya berisi data jual. Role tidak dikenal ditolak saat login maupun saat mengakses data.

Keranjang disimpan per akun, perubahan tab disinkronkan, dan impor tamu hanya membawa barang/jumlah. Checkout hanya mengurangi barang yang dibeli. Draft checkout dan dialog transaksi mempertahankan identitas permintaan ketika respons hilang agar retry tidak menggandakan transaksi. Jika browser menolak penyimpanan, keranjang memiliki fallback memori yang diberi keterangan; tindakan pembayaran yang tidak bisa menyimpan draft pemulihan tidak dikirim.

Unggahan bukti identik untuk akun/jenis/target yang sama memakai satu ID, termasuk saat nama file berubah atau permintaan dikirim bersamaan. Otorisasi tetap diperiksa pada retry. Penyimpanan lokal menerbitkan file lengkap secara atomik dan Blob privat tidak menimpa objek. Objek dari kegagalan metadata yang belum pasti dipertahankan untuk pemulihan; batas dan pengujiannya dicatat pada dokumen pengujian.

## Validasi

Bukti rinci ada di [pengujian customer](testing-customer-v7.md) dan [implementasi etalase](implementation-v7-storefront.md). Pengujian transaksi dilakukan pada database QA lokal yang terpisah; tidak menambahkan pesanan uji ke data publik.

- 154/154 tes regresi lulus, termasuk 15 tes tambahan unggahan dan penyimpanan.
- 22/22 kelompok pemeriksaan HTTP customer lulus pada database QA terisolasi, termasuk penjualan penuh, parsial, refund, isolasi pembeli, dan retry bukti.
- TypeScript, lint, dan build lulus. Lint penuh memiliki 9 warning yang sudah dicatat; berkas perbaikan unggahan terakhir lulus tanpa warning.
- Browser mencakup alur tamu sampai checkout, dua tab, profil, serta detail transaksi pada QA. Pemeriksaan responsif 320–1440 px dan batas cakupannya dicatat pada dokumen pengujian. Login/profil/pesanan kosong/logout customer juga diperiksa langsung di produksi.

Deployment final `dpl_CS5rq8MVbegyMRUy7ax9EGr97Z7C` dibangun dengan status READY lalu dipromosikan ke URL publik existing. [Deployment Vercel](https://unit-toko-k6z3n1kmg-natannielzs-projects.vercel.app). Hasil pemeriksaan produksi terakhir tercatat pada [vercel-v7-results.json](vercel-v7-results.json).

Pemeriksaan produksi **84/84 lulus**: katalog publik, halaman toko, 11 login, pembatasan data tiap role, dokumen/laporan, pengalihan halaman, logout, dan pencabutan sesi. Snapshot sebelum serta sesudah pengujian membuktikan 239 record lama dan 10 credential tetap identik; satu record sesi pada backup telah kedaluwarsa dan dibersihkan normal saat login. Data bisnis, payload historis, dan password tidak berubah. Revisi tetap 34→35 dari provisioning satu customer.

## Migrasi dan pemulihan

Backup produksi dibuat privat sebelum migrasi melalui sinkronisasi replica libSQL, kemudian diperiksa secara lokal tanpa koneksi remote. Salinan backup digunakan untuk menjalankan migrasi dan provisioning dua kali. Sebanyak 240 baris lama dibandingkan per kolom: 10 credential, 1 sesi, 6 pesanan, 2 invoice, dan 36 produk tetap. Provisioning hanya menambah customer, credential, email login, serta satu revision. Database sumber tidak direset.

Tidak ada periode tertutup pada data produksi saat backup. Pelestarian snapshot periode tertutup diuji dengan fixture, bukan diklaim berasal dari snapshot produksi. Detail backup, hash, seed, dan data akun tetap berada dalam artefak privat workspace.

Pemeriksaan awal berhenti ketika menemukan satu sesi PIC baru yang dibuat setelah backup, sebelum verifier dijalankan. Diagnosis hanya-baca memastikan 240 record asli tetap identik; pengecualian kemudian dibatasi pada fingerprint sesi tersebut. Pembacaan terakhir pada run berikutnya mengalami kegagalan layanan yang penyebab pastinya tidak terekam. Pembacaan ulang lulus; verifier memakai batch dalam satu transaksi baca dan maksimal tiga retry khusus gangguan sementara. Run final 84/84 menyelesaikan kedua snapshot pada percobaan pertama. Tidak ada aturan preservasi bisnis atau credential yang dilonggarkan.

Jika pemulihan diperlukan, hentikan penerimaan transaksi baru, ambil backup keadaan terbaru, dan periksa perubahan sejak backup awal. Pulihkan database dari salinan yang telah diverifikasi ke resource terpisah, lalu cocokkan aplikasi serta environment sebelum memindahkan traffic. Jangan mengganti database aktif tanpa rekonsiliasi transaksi baru. Setelah ada transaksi customer, rollback ke v6 saja tidak kompatibel dengan data pembeli baru. Resource Blob privat tetap dipertahankan untuk lampiran lama.

## Batas

Satu toko, satu akun customer demo tambahan, 12 keluarga produk dan 36 SKU pada data awal. Foto, data, pengiriman, uang, dan pembayaran adalah simulasi. Tidak ada multivendor, signup publik, rating/diskon rekaan, gateway pembayaran, atau integrasi bank/kurir nyata. Pemeriksaan responsif dan aksesibilitas dasar bukan audit WCAG menyeluruh.
