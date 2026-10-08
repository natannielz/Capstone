# Audit v9 — Akun, pelanggan, dan administrasi

Tanggal: 22 September 2026. Acuan: rencana digitalisasi, rencana marketplace v7, dan rilis v8. Audit memeriksa hubungan antarmuka, proyeksi data per akun, dan tindakan server. Pemeriksaan kode bukan bukti bahwa seluruh skenario telah dijalankan melalui browser pada setiap akun.

## Cakupan akun

| Akun / peran | Alur yang tersedia | Hasil pemeriksaan ini |
| --- | --- | --- |
| Pelanggan demo | Katalog, kemasan, keranjang, checkout, pesanan sendiri, penggantian, penerimaan, komplain, invoice, bukti pembayaran, profil/alamat/password | Memperbaiki tautan kemasan; rincian serta histori penggantian; status penolakan keuangan; histori produk pengganti nonaktif; unggahan desain/spesifikasi pesanan |
| PIC Operasional dan PIC Teknologi | Pesanan, penggantian, penerimaan, tagihan, dan pembayaran milik divisi; profil | Pemisahan divisi tetap berlaku; perbaikan operasional/keuangan dicatat dalam audit terkait |
| Kepala Toko | Tinjau pesanan, pesanan divisi, harga dan master produk, pengadaan, pengawasan stok | Master produk dan perubahan harga sudah tersedia, bukan gap fitur |
| Staf Toko | Reservasi, Surat Jalan, penerimaan/finalisasi, komplain, pengadaan, retur, opname | Audit operasional terpisah memeriksa jalur gagal/batal dan keterkaitan pekerjaan |
| Kurir | Pengiriman yang ditugaskan, bukti, gagal kirim, biaya, profil | Akses tetap terbatas pada tugas kurir |
| Pengolah Laporan | Register, ekspor, laporan, pengajuan periode | Audit keuangan terpisah memeriksa pengembalian laporan untuk revisi |
| Penagihan | Invoice, dana masuk, verifikasi/alokasi, koreksi/refund, biaya dan pemasok | Penolakan pada audit keuangan diteruskan ke histori pelanggan dengan alasan yang sesuai |
| Pimpinan | Persetujuan pembiayaan, biaya, koreksi/refund, laporan | Audit keuangan memeriksa pilihan penolakan dan pemisahan pengaju–pemberi keputusan |
| Akuntansi | Jurnal, laporan, posting/tutup periode | Kontrol versi laporan dan periode tertutup tetap berlaku |
| Administrator | Peran, divisi, status aktif, data divisi/pemasok, profil | Menambahkan ubah data divisi/pemasok dan reset password manual akun lain dengan autentikasi ulang admin |

## Gap dan perbaikan

1. **Histori pembelian membuka kemasan dasar.** Tautan pelanggan sebelumnya memakai ID keluarga ditambah query `sku`, sedangkan detail produk membaca ID pada path. Tautan kini memakai ID varian lengkap; P3/P6 membuka SKU dan harga yang sesuai.
2. **Informasi keputusan pengganti kurang lengkap.** Pelanggan hanya melihat nama pengganti dan harga satuan. Kartu kini mencantumkan barang semula, pengganti, jumlah, total pengganti, nilai semula, tanggal dan status keputusan. Persetujuan, penolakan, dan pembatalan tetap terlihat sesudah keputusan. Nilai memakai snapshot usulan.
3. **Pengganti nonaktif hilang dari histori pelanggan.** Proyeksi produk kini mempertahankan produk dari usulan milik pelanggan tersebut. Usulan dan produk nonaktif milik pembeli lain tetap tidak disertakan.
4. **Data divisi/pemasok hanya bisa ditambah lewat UI.** Perintah pembaruan berdasarkan ID sudah ada di server. Tombol Ubah sekarang mengisi data sebelumnya, mempertahankan ID dan hubungan pengadaan, serta tidak mengganti alamat pada pesanan lama.
5. **Bantuan password belum memiliki tindakan admin.** Dialog reset menyebut target nama/email, meminta password admin saat ini, password baru beserta konfirmasi, dan alasan audit. Endpoint baru mencabut sesi target tanpa mengubah peran/email/status. Password hanya berada dalam isian sementara, tidak ditulis ke penyimpanan browser atau notifikasi. Respons yang belum pasti dapat diulang dengan ID dan payload sama. Kegagalan memuat ulang daftar setelah reset berhasil tidak dinyatakan sebagai kegagalan reset.
6. **Histori pelanggan perlu mengenali penolakan keuangan.** Pembayaran ditolak ditampilkan dengan alasan dan petunjuk membuat catatan pembayaran yang benar. Bukti sebelumnya menjadi histori; status tidak lagi terlihat sebagai terverifikasi. Penolakan koreksi/refund juga mempunyai label dan alasan yang sesuai.
7. **Lampiran desain/spesifikasi pesanan belum tersedia.** Pelanggan dapat mengunggah JPG, PNG, atau PDF maksimal 4 MB pada pesanan sendiri yang masih menunggu tinjauan. PIC dibatasi ke pesanan divisinya; Kepala Toko dapat melampirkan berkas sebelum tinjauan. Setelah disetujui, ditolak, atau dibatalkan, berkas menjadi histori baca saja. Kepala, Staf, dan Pengolah Laporan dapat membaca sesuai akses pesanan; kurir, administrator, dan peran keuangan tidak menerima dokumen desain. Identitas pembeli berasal dari pesanan di server. Izin diperiksa sebelum penyimpanan objek, sesudah penyimpanan, dan dalam transaksi metadata sehingga perubahan status selama unggahan tidak menambahkan lampiran pada pesanan yang sudah ditinjau.

## Bukti lokal

`tests/customer-account-v9.test.ts`: **6 kelompok lulus**, meliputi tautan P3/P6; nilai pengganti setelah persetujuan dan perubahan harga; histori penolakan/pembatalan; produk nonaktif dan isolasi pembeli; label pembayaran; pembaruan divisi/pemasok serta penolakan terhadap semua akun non-admin. Lint terarah tidak menemukan error; peringatan navigasi sesi yang sudah ada tetap muncul pada komponen pelanggan.

`tests/order-attachments-v9.test.ts` bersama regresi `attachment-upload-v7.test.ts`: **21 kelompok lulus** (6 baru dan 15 sebelumnya). Mencakup matriks pembaca/pengunggah, akses langsung serta metadata per akun, perubahan status sebelum/selama commit, histori setelah tinjauan, retry tanpa metadata ganda, snapshot periode tertutup, jenis/ukuran berkas, dan penyimpanan lokal atomik. Route serta pipeline unggahan yang sudah ada dipakai kembali; tidak ada migrasi atau penyimpanan publik baru.

Pemeriksaan sumber dialog reset mengonfirmasi isian password menggunakan kontrol password, hanya berada pada state komponen, tidak masuk log/toast/storage, dan dibersihkan saat dialog ditutup atau operasi berhasil. UI memakai komponen Dialog, Field, Input, Textarea, Alert, dan Button yang sudah tersedia. Pengujian endpoint reset, regresi gabungan, browser, dan publikasi dicatat pada rilis v9; audit ini tidak menyatakan ada reset password atau transaksi uji pada produksi.

## Batas demo

- Pembayaran, refund, dan data transaksi tetap simulasi. Integrasi bank, payment gateway, kurir eksternal, email otomatis, signup publik, dan multi-penjual berada di luar batas yang disepakati.
- Email akun demo tetap. Admin membantu reset manual akun lain; pengguna yang mengetahui password saat ini dapat menggantinya lewat profil. Tidak ada pengiriman password lewat email atau pesan otomatis.
- Desain/spesifikasi diunggah setelah pesanan dibuat dan sebelum toko meninjaunya. Perubahan spesifikasi setelah persetujuan membutuhkan pesanan/tinjauan baru; tidak ada penggantian berkas yang diam-diam mengubah acuan pesanan lama.
- Audit ini tidak menyamakan lolosnya tes demo dengan kesiapan operasi toko nyata.
