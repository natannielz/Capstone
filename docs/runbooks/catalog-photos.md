# Foto utama katalog

Fitur ini berada pada profil `full`. Pilot `release1` tetap memakai penanda gambar yang jujur dan tidak memanggil layanan foto baru. Implementasi, pemeriksaan lokal, staging dan penerimaan produksi dicatat terpisah di [ledger](../quality/production-progress.md).

## Alur petugas

Di `/administration/catalog`, petugas dengan kewenangan pengelolaan katalog pada pemilik SKU memilih **Foto** untuk SKU aktif. Muat SKU terbaru sebelum memilih PNG/JPEG, maksimal 10 MiB. Reservasi, unggahan dan foto utama adalah operasi terpisah; unggahan yang selesai masih berada dalam karantina.

Gunakan pemeriksaan status yang tersedia. Pemroses harus memindai dan mendekode original privat yang sama sebelum foto dapat ditinjau. Petugas membuka original yang diperiksa hash-nya, lalu mengonfirmasi pemasangan pada SKU dan definisi kemasan yang telah dibekukan. Perubahan SKU, definisi atau kepala foto selama tinjauan mengharuskan pemuatan ulang.

Jika jawaban operasi hilang, gunakan **Lanjutkan foto SKU** dan pulihkan operasi dengan identitas semula. Jurnal browser menyimpan identitas operasi; jurnal itu tidak menyimpan foto, referensi privat, token atau isi berkas. Hasil yang belum diketahui tidak boleh diperlakukan sebagai kegagalan pasti atau dikirim ulang dengan identitas baru. Foto pengganti menambah riwayat dan mengganti kepala aktif; original terdahulu tetap dipertahankan.

## Tampilan pembeli dan batas efek

Pembeli yang masih berhak pada katalog memperoleh foto melalui permintaan privat ke API menggunakan UUID publik SKU. API memilih foto aktif dari riwayat yang sah, memeriksa kewenangan serta kebijakan baca sesudah verifikasi berkas, lalu mengirim original PNG/JPEG. Tidak ada URL penyimpanan publik atau optimasi gambar pihak ketiga. Respons menggunakan `private, no-store`; browser memeriksa panjang, tipe dan SHA-256 sebelum membuat URL blob sementara.

SKU tanpa foto, definisi yang sudah berubah, validasi yang kedaluwarsa atau layanan yang tidak tersedia tidak boleh menampilkan foto seolah-olah masih sah. Antarmuka menyediakan keadaan memuat, belum tersedia, kesalahan dan coba lagi. Mengganti foto tidak membuat harga, pesanan, reservasi stok, pembayaran atau jurnal keuangan.

## Migrasi dan kesiapan operasional

M80 menambah pemilik, kepala, audit dan kuitansi operasi foto. Cabang validator bukti keuangan lama dipertahankan; lease `CatalogSku` memakai tujuan yang eksplisit dan bentuk yang ketat. Lease untuk pemeriksaan sebelum pemasangan berbeda dari lease untuk membaca foto yang sudah terpasang. Migrasi memeriksa penyimpanan dan indeks sebelum perubahan, mempertahankan penjagaan operator yang lebih kuat, serta menolak riwayat pendahulu yang sudah berisi pemilik foto yang tidak sah. Migrasi dijalankan melalui prosedur migrasi yang disetujui, bukan permintaan API.

Host Windows/NTFS, identitas layanan, penyimpanan privat, pemindai, kebijakan validasi dan kebijakan baca yang disetujui tetap wajib. Nilai konfigurasi atau pemindai sintetis dalam tes tidak membuktikan penerimaan layanan produksi. Lihat [penyimpanan privat](private-evidence-storage.md) dan [penerimaan unggahan](private-evidence-admission.md).

## Pemeriksaan ulang original yang sama

Di panel **Foto**, bagian **Pemeriksaan ulang keamanan foto** memakai original yang sudah terpasang pada SKU dan definisi kemasan saat ini. Petugas mengisi alasan, memberi persetujuan eksplisit, lalu memilih **Minta pemeriksaan ulang foto**. Operasi mengikat revisi SKU dan revisi pemeriksaan terbaru; perubahan sumber atau kepala pemeriksaan memerlukan pemuatan ulang. Hasil permintaan terdahulu berbeda dari status kepala terbaru.

M81 menyimpan permintaan, pekerjaan, hasil, audit, kepala dan kuitansi terpisah. Permintaan baru langsung membuat kepala menunggu; foto tidak dapat dibuka selama kepala terbaru menunggu atau ditolak. Worker memakai original privat yang sama, pemindai dan decoder nyata serta kebijakan operator aktif dengan batas waktu. Jika lulus, pembacaan tetap memeriksa kewenangan, kebijakan baca, hash, umur validasi dan umur definisi pemindai. Riwayat foto, hasil validasi pertama dan pekerjaan pertama tidak diubah. Pemeriksaan ulang tidak mengunggah, mengganti foto, mengubah harga/stok atau menulis transaksi keuangan.

Jika jawaban permintaan terputus, **Lanjutkan foto SKU** memulihkan kuitansi dengan akun dan identitas operasi semula tanpa pengiriman ulang. Gunakan **Periksa status pemeriksaan ulang** untuk membaca kepala terbaru. Worker hanya mengklaim pekerjaan untuk consumer, pipeline, penyimpanan dan revisi kebijakannya sendiri; lease kedaluwarsa dapat dipulihkan, maksimal lima percobaan. Ketidaktersediaan atau percobaan terakhir yang gagal tidak menghasilkan bukti lulus maupun menghidupkan kembali validasi lama.

Konfigurasi eksplisit `Evidence:ImageRevalidation` diperlukan pada API dan worker. Tidak ada penanaman kebijakan persetujuan otomatis. Konfigurasi belum tersedia menghasilkan keadaan layanan tidak tersedia, bukan izin baru. Bukti browser/API/Mongo dan pemindai sintetis lokal belum menerima host, pemindai, identitas layanan atau kebijakan produksi. Jangan memperpanjang umur kebijakan, mengubah waktu hasil, membuka ulang pekerjaan pertama atau memalsukan hasil agar foto tampil kembali. Penerimaan host dan kebijakan operator nyata tetap terbuka.

Pemulihan cadangan harus mempertahankan original privat beserta reservasi, versi dokumen, hasil validasi, pekerjaan/kuitansi worker, lease/audit baca, pemilik/kepala foto, audit, kuitansi operasi, seluruh pemilik pemeriksaan ulang M81 dan penghitung ID secara koheren. Penghapusan original atau pelepasan kapasitas tidak diotorisasi oleh panduan ini.
