# Klaim pemasok dan keputusan internal

Buka **Klaim pemasok** dari ruang kerja akun, atau `/procurement/supplier-claims`. Halaman ini mengajukan klaim berdasarkan pesanan pembelian dan hasil pemeriksaan barang yang benar-benar tercatat. Persetujuan di halaman ini adalah persetujuan internal untuk tindak lanjut; tanggapan pemasok, penggantian, kredit, pengembalian uang dan pelepasan barang belum dicatat.

## Akses

- `procurement.read` diperlukan untuk membaca lokasi, pesanan, sumber pemeriksaan dan riwayat klaim dalam penugasan yang berlaku.
- `supplier.claim` diperlukan untuk mengajukan klaim; `supplier.claim.approve` untuk menyetujui atau menolak. Keduanya memerlukan MFA dalam lima menit terakhir dan identitas orang yang terverifikasi saat tindakan dilakukan.
- Pemeriksa harus merupakan orang yang berbeda dari pengaju. Akun kedua milik orang yang sama tidak memenuhi pemisahan tersebut.
- Hak klaim tidak memberikan akses ke gambar atau video asli. Bukti privat tetap memerlukan izin tersendiri pada ruang kerja pemiliknya. Tidak ada pemberian izin otomatis berdasarkan nama peran.

## Mengajukan klaim

1. Pilih lokasi yang ditugaskan, **Ajukan klaim pemasok**, lalu pesanan pembelian dan baris pemeriksaan yang sesuai. Periksa definisi kemasan, kondisi teramati, lot, kedaluwarsa dan referensi lokasi karantina.
2. Pilih dasar jumlah dengan sengaja. **Barang diterima fisik** menggunakan kemasan yang benar-benar diterima. **Kekurangan teramati** menggunakan selisih kekurangan dan tidak menyatakan adanya stok fisik. Jumlah rusak, kedaluwarsa dan terbuka dapat tumpang tindih; jangan menjumlahkannya sebagai kemasan yang berbeda. Gunakan sisa cakupan klaim yang ditampilkan.
3. Masukkan jumlah kemasan dan masalah yang sesuai. Invoice bersifat opsional: barang rusak, kedaluwarsa atau terbuka dapat diajukan tanpa pencocokan invoice yang berhasil. Jika menautkan invoice, pilih dokumen, baris, dan pencocokan sumber yang nyata. Nilai seluruh baris pencocokan hanya nilai sumber; **bukan nominal hak klaim atau pembagian otomatis**. Nilai yang belum diketahui tetap dinyatakan belum diketahui.
4. Isi referensi, tanggal klaim, tanggal tanggapan yang diminta, dasar tenggat, alasan dan penyelesaian yang diminta: penggantian barang, kredit pemasok, pengembalian uang atau pemeriksaan lebih lanjut. Sistem tidak menentukan tenggat atau menyatakan permintaan tersebut sudah disetujui pemasok.
5. Pilih **Periksa klaim pemasok**, baca seluruh fakta pada tinjauan akhir, lalu konfirmasi dan kirim. Jika sumber atau sisa kapasitas berubah, muat data terbaru dan tinjau kembali.

## Keputusan dan tindak lanjut

Pemeriksa membuka klaim yang menunggu keputusan, memasukkan penjelasan serta alasan pencatatan, lalu meninjau persetujuan atau penolakan sebelum konfirmasi. Klaim yang disetujui menjadi **Disetujui internal · menunggu tindak lanjut pemasok**. Penolakan tetap mempertahankan barang dalam penahanan.

Pengajuan mencakup jumlah klaim terhadap sumbernya. Keputusan tidak mencakup jumlah itu untuk kedua kalinya dan tidak mengembalikan sisa kapasitas. Persetujuan maupun penolakan tidak menghasilkan barang layak jual, penempatan stok akhir, jurnal keuangan atau pembayaran. Penyelesaian pemasok dan keputusan karantina/putaway masih memerlukan alur lanjutan yang sesuai.

## Jika hasil belum diketahui

Gunakan **Periksa hasil** dengan akun pengaju tindakan yang sama. Referensi operasi asli tetap tersedia setelah halaman dimuat ulang; pemeriksaan ini hanya mencari hasil tersimpan, bukan mengirim ulang tindakan. Jangan membuat pengajuan atau keputusan pengganti untuk mengatasi balasan yang hilang. Jika hasil belum ditemukan, biarkan referensi tetap terbuka dan minta penanggung jawab memeriksa riwayat operasi. Setelah hasil dipastikan, muat kembali sumber dan sisa cakupan sebelum tindakan baru.

Status validasi dan penerimaan operasional dicatat dalam [ledger kemajuan](../quality/production-progress.md). Panduan ini menjelaskan perilaku implementasi; data sintetis dan pemeriksaan lokal tidak membuktikan penerimaan produksi.
