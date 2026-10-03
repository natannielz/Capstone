# Utang pemasok dan pemakaian uang muka

Status: alur browser, API, MongoDB dan migrasi telah diperiksa secara lokal dengan data serta kebijakan sintetis. Staging dan produksi belum diterima; kecepatan respons juga masih perlu diperbaiki sebelum peluncuran. Hasil pemeriksaan dan publikasi dicatat pada [ledger kemajuan](../quality/production-progress.md).

Alur ini memisahkan pengakuan utang pemasok dari pemakaian uang muka yang telah dibuktikan melalui pemeriksaan bank independen. Catatan pembayaran, hasil OCR dan persetujuan klaim pemasok sendiri tidak menyelesaikan utang.

## Sumber yang dapat dipakai

Pengakuan utang menggunakan alokasi penilaian barang diterima yang telah disetujui. Sistem menelusuri penerimaan, invoice asli yang telah diperiksa, hasil pencocokan, pesanan pembelian dan jurnal penilaiannya. Jumlah dan identitas pemasok berasal dari sumber tersebut; petugas tidak mengetik ulang nilai invoice atau mengubah pemasok untuk mengakui utang.

Pemakaian uang muka menggunakan penyelesaian pembayaran yang sudah diperiksa secara independen terhadap transaksi debit bank. Catatan transfer yang masih belum diverifikasi tidak dapat dipakai. Sumber uang muka dan utang harus sesuai dengan pesanan pembelian, pemasok dan lingkup yang diizinkan.

Perlakuan awal hanya mencakup nilai penilaian penerimaan yang didukung secara eksplisit oleh kebijakan Finance. SPH saja, invoice yang belum cocok, bukti tidak lengkap serta perlakuan pajak atau biaya yang belum disetujui harus ditolak. Jangan membuat jurnal pengganti atau mengubah catatan sumber agar transaksi dapat diterima.

## Dua tindakan yang terpisah

1. **Akui utang dari penilaian.** Pilih sumber penilaian yang disetujui, periksa nilai yang diturunkan dari sumber, kebijakan Finance dan tanggal pembukuan. Usulan diperiksa oleh orang berbeda. Persetujuan mengakui utang satu kali terhadap kapasitas sumber yang tepat; tindakan ini tidak membayar pemasok atau memindahkan barang.
2. **Terapkan uang muka terverifikasi.** Pilih utang dan uang muka yang sesuai, lalu masukkan jumlah positif yang akan dipakai. Nilainya tidak boleh melebihi sisa utang maupun sisa uang muka. Pemeriksa independen memutuskan usulan berdasarkan tinjauan terbaru. Pemakaian sebagian menyisakan saldo yang jelas pada kedua sumber.

Pesanan tanpa uang muka tetap dapat mempunyai utang yang belum dibayar. Pengakuan utang tidak boleh menciptakan uang muka atau transaksi bank buatan. Penolakan usulan tidak membukukan nilai baru.

## Kebijakan dan kewenangan

Kebijakan Finance yang berlaku harus menyetujui perlakuan dan pemetaan akun secara eksplisit, terikat pada pembukuan, lokasi dan periode yang benar. Kebijakan pembayaran atau penilaian lama tidak otomatis menyetujui pengakuan utang atau pemakaian uang muka. Akun penerimaan sementara, utang pemasok dan uang muka pemasok harus mempunyai bukti persetujuan yang sesuai.

Pengaturan pembukuan yang tersedia saat ini hanya mendukung konfigurasi awal yang disetujui. Jika konfigurasi tersebut belum mempunyai pemetaan yang dibutuhkan, alur ini belum dapat digunakan pada buku itu. Jangan menambahkan persetujuan kedua atau mengubah pemetaan langsung di database; perubahan bagan akun memerlukan alur revisi yang ditinjau tersendiri. Untuk buku baru, seluruh pemetaan harus disetujui sebelum transaksi sumber dibukukan.

Hak membaca dan hak mengajukan atau memutuskan dipisahkan. Tindakan tetap memerlukan kewenangan saat ini, MFA yang masih baru dan pemisahan orang pengaju/pemeriksa. Nama peran pada layar tidak memberikan kewenangan tambahan. Usulan lama tidak boleh diterima hanya karena pernah terlihat sah sebelum kebijakan, akses, periode atau saldo sumber berubah.

Gunakan tautan verifikasi pada halaman atau **Akun saya** untuk memperbarui verifikasi sebelum melanjutkan pekerjaan. Setelah kembali, buka lagi pengajuan yang sudah tersimpan dan tinjau ulang. Isian yang belum disimpan perlu diisi kembali; referensi pemeriksaan hasil tindakan yang belum pasti tetap dipertahankan.

## Hasil yang belum pasti

Jika balasan tindakan hilang, periksa hasil menggunakan akun, jenis operasi dan kunci asli. Jangan membuat kunci baru atau mengirim ulang tindakan untuk mencoba memastikan hasil. Catatan hasil harus cocok dengan sumber, nilai dan keputusan yang benar sebelum referensi pemulihan dibersihkan.

## Batas penerimaan

Alur ini tidak menjalankan transfer bank, mengaktifkan stok, melepas karantina, membuat bukti bank atau menyelesaikan klaim pemasok. Pengembalian uang muka yang tidak terpakai memerlukan bukti kredit bank serta pemilik transaksi dan kebijakan tersendiri. Sesuai keputusan pengguna, seluruh satu kredit pengembalian harus milik satu pemasok dan satu pesanan pembelian; kredit campuran dengan pembayaran pelanggan atau pesanan lain tidak boleh dipakai. Pengendalian pengembalian ini masih harus diselesaikan pada alur terpisah. Selisih invoice, nota kredit pemasok, pajak/biaya lain, pembatalan atau pembalikan dan penutupan periode tetap harus melalui alur yang disetujui.

Fixture lokal tidak menyetujui kebijakan perusahaan atau pemetaan akun produksi. Penerapan migrasi memerlukan prosedur penghentian penulis, cadangan dan pemulihan yang telah diterima; jangan memasukkan persetujuan, saldo atau jurnal buatan ke database produksi.
