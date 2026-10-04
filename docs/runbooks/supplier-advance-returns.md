# Pengembalian uang muka pemasok

Status: alur M76 telah terhubung dari build produksi frontend yang dijalankan secara lokal ke API dan MongoDB lokal. Pengembalian satu kredit untuk dua uang muka, MFA, persetujuan independen, pemulihan balasan hilang, persaingan saldo, kepemilikan pembayaran pelanggan, pemeriksaan penyimpanan/akses, migrasi dan kedua alur browser terdampak telah lulus. Bukti dan batas penerimaan dicatat pada [ledger kemajuan](../quality/production-progress.md). Waktu respons masih memerlukan perbaikan dan uji beban. Staging, produksi dan kebijakan Finance yang sebenarnya belum diterima.

## Sumber dan batas transaksi

Gunakan halaman **Pengembalian uang muka** untuk membukukan kredit bank yang benar-benar sudah diterima dan dikonfirmasi secara independen. Seluruh satu kredit harus milik satu pemasok dan satu pesanan pembelian. Kredit tersebut dapat dibagi ke beberapa uang muka terverifikasi dari pesanan yang sama. Jumlah rincian harus tepat sama dengan seluruh kredit, dan setiap rincian tidak boleh melebihi sisa uang muka yang belum diterapkan atau dikembalikan.

Catatan transfer yang belum diverifikasi, hasil OCR saja, persetujuan klaim, atau saldo yang diketik sendiri tidak menjadi sumber uang muka. Kredit campuran dengan pembayaran pelanggan atau pesanan lain tidak dapat digunakan. Jika nilainya tidak cocok, selesaikan perbedaan sumber melalui prosedur Finance yang disetujui; jangan mengubah transaksi bank atau membuat bukti pengganti agar jumlahnya cocok.

## Kebijakan dan akses

Sebelum penggunaan nyata, Finance harus menyetujui perlakuan pengembalian, penanggung jawab, masa berlaku, buku, versi bagan akun, serta pemetaan akun penerimaan belum dialokasikan dan uang muka pemasok. Pemetaan harus sesuai dengan jurnal bank dan uang muka asal. Periode pembukuan harus terbuka. Konfigurasi buku yang tersedia saat ini hanya mendukung persetujuan awal; perubahan bagan akun memerlukan alur revisi tersendiri, bukan perubahan langsung pada database atau persetujuan awal kedua.

Izin baca pengembalian dan pengadaan mengatur riwayat. Memilih kredit baru memerlukan kewenangan bank dan rincian rekening pada lingkup sebenarnya. Pengajuan dan keputusan mempunyai izin terpisah serta memerlukan MFA dalam lima menit terakhir. Pemeriksa harus orang berbeda dari pengaju; persetujuan pembukuan juga memerlukan izin jurnal. Nama peran tidak menambahkan izin ini secara otomatis.

## Pengajuan dan pemeriksaan

1. Pilih lokasi pengadaan, kredit bank terkonfirmasi dan kebijakan Finance yang berlaku.
2. Pilih uang muka terverifikasi dari pemasok dan pesanan yang sama. Masukkan nilai pengembalian setiap uang muka, tanggal pembukuan dan alasan. Periksa jumlah keseluruhan sebelum meninjau pengajuan.
3. Periksa sumber, sisa nilai dan akun pembukuan yang ditampilkan, lalu simpan pengajuan. Pengajuan belum mencadangkan uang muka dan belum mencatat jurnal.
4. Pemeriksa independen membuka pengajuan, meninjau kembali sumber, kebijakan, periode dan sisa nilai, lalu menyetujui atau menolak dengan alasan. Jika sumber atau saldo berubah, pengajuan tidak boleh dipaksakan melalui tinjauan lama.

Persetujuan mencatat debit penerimaan belum dialokasikan dan kredit uang muka pemasok. Sistem tidak mengirim uang, tidak mengurangi utang pemasok, tidak membuat kas kedua kali dan tidak melepas barang. Sisa uang muka berkurang bersama pemakaian yang sudah tercatat pada [utang pemasok](supplier-payables.md). Penolakan tidak mencatat jurnal pengembalian.

Riwayat menampilkan saldo pada saat pengajuan; saldo tersebut bukan janji bahwa kapasitas yang sama masih tersedia sekarang. Gunakan sumber terbaru sebelum pekerjaan berikutnya. Untuk memperbarui MFA, gunakan tautan verifikasi pada halaman atau **Akun saya**, lalu buka kembali pengajuan yang sudah tersimpan.

## Balasan hilang atau bukti tidak lengkap

Jika hasil tindakan belum pasti, gunakan **Periksa hasil** dengan akun dan referensi permintaan semula. Jangan mengirim ulang atau membuat kunci baru untuk mencari kepastian. Pemulihan harus menemukan hasil asli dan pemilik yang tepat sebelum referensi lokal dibersihkan.

Kepemilikan tujuan kredit bersifat tetap: kredit pengembalian pemasok tidak dapat menjadi pembayaran pelanggan meskipun saldo lain berubah. Jika keputusan, rincian, jurnal atau bukti pemilik hilang atau tidak cocok, sistem menolak tindakan lanjutan. Jurnal menyimpan referensi pemiliknya; jika pemilik itu hilang, penerimaan baru dalam buku yang sama dapat ditahan sampai insiden diperbaiki. Jangan menghapus catatan untuk membuka saldo atau menandai hasil sebagai berhasil tanpa bukti.

## Penerimaan operasional

Fixture lokal tidak menyetujui transaksi, rekening, orang atau kebijakan produksi. Migrasi memerlukan penghentian penulis yang terkoordinasi, cadangan dan pembuktian pemulihan. Penerimaan produksi masih memerlukan sumber bank nyata, kewenangan operator, pemeriksaan provider, uji beban dan pilot yang disetujui. Pembalikan transaksi atau koreksi pengembalian yang telah disetujui harus melalui alur tersendiri yang ditinjau; belum ada izin untuk mengubah catatan langsung.
