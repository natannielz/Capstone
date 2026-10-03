# Penerimaan stok ditahan

Gunakan `/procurement/stock-receipts` untuk mencatat kemasan dari pemeriksaan pengadaan yang telah diajukan. Hasilnya adalah lot, saldo dan pergerakan penerimaan yang seluruhnya ditahan: jumlah fisik tercatat sama dengan jumlah ditahan, sedangkan dipesan dan tersedia untuk dijual tetap nol. Angka tersebut menggunakan kemasan, bukan koli atau hasil perkalian satuan dasar. Nilai persediaan belum ditetapkan; tidak ada jurnal, pelepasan barang atau pengaktifan alokasi otomatis.

## Akses dan sumber

Pembacaan memerlukan `procurement.read` dan `stock.read` dalam lokasi yang ditugaskan. Pencatatan juga memerlukan `stock.receive`, identitas orang terverifikasi dan MFA dalam lima menit terakhir. Hak membaca stok tidak memberi akses ke video asli pemeriksaan.

Pilih pesanan pembelian, baris pemeriksaan dan alokasi stok dari pilihan yang disediakan. Sumber alokasi harus mempunyai pengaturan yang telah disetujui, kebijakan operasional Unit Toko yang berlaku dan pemetaan lokasi yang diaktifkan. Alokasi yang belum aktif boleh menerima stok ditahan apabila memenuhi persyaratan tersebut; pencatatan tidak menjadikannya aktif. Sumber dengan pencatat eksternal tidak diperlakukan sebagai buku stok Unit Toko.

## Mencatat penerimaan

1. Pilih **Catat stok ditahan** dan pesanan acuan. Tinjau kemasan asli, kondisi, lot, kedaluwarsa dan catatan karantina. Kondisi rusak, kedaluwarsa dan terbuka dapat tumpang tindih; jangan menyimpulkan jumlah sehat dengan menjumlahkan atau mengurangkan kondisi tersebut.
2. Periksa jumlah fisik, jumlah yang sudah dicakup klaim atau penerimaan stok, serta sisa yang dapat dicatat. Pilih alokasi dan tinjau revisi pengaturan, kebijakan, prosedur rekonsiliasi dan prosedur gangguannya.
3. Masukkan jumlah kemasan, referensi penerimaan, referensi lot dan alasan. Lot atau kedaluwarsa yang sudah diketahui harus sesuai pemeriksaan. Jika kedaluwarsa belum diketahui, pilih opsi tersebut secara eksplisit. Nilai kosong tidak berarti barang tanpa kedaluwarsa atau layak dijual.
4. Pilih **Periksa penerimaan stok**, baca seluruh tinjauan, centang konfirmasi dan simpan. Jika sumber atau kapasitas berubah, muat ulang dan tinjau kembali.

Riwayat menampilkan fakta pada saat penerimaan dicatat, termasuk sisa setelah penerimaan itu. Angka riwayat bukan pernyataan saldo terkini setelah kemungkinan pergerakan berikutnya. Harga pesanan tidak otomatis menjadi biaya persediaan. Penilaian, keputusan pelepasan dan penempatan akhir tetap memerlukan tindak lanjut yang sesuai.

Penerimaan yang masih ditahan tetap menjadi kewajiban sumber dan persediaan yang belum selesai. Pengaktifan berikutnya memerlukan pemeriksaan kewajiban nyata pada ketujuh bidang: pesanan, reservasi, pemenuhan, pengadaan, purnajual, rekonsiliasi sumber dan operasi persediaan. Penilaian, pelepasan dan pengendalian juga harus disetujui. Pencatatan penerimaan bukan persetujuan pemindahan kewenangan pencatatan stok. Alur klaim setelah stok ditahan dan pelaksanaan keputusan fisik atas barang masih menunggu implementasi; penerimaan tidak menghapus kebutuhan penanganan klaim tersebut.

## Balasan hilang

Gunakan **Periksa hasil** dengan akun yang mengirim tindakan. Halaman mencari hasil berdasarkan kunci operasi asli, termasuk setelah dimuat ulang; tindakan tidak dikirim ulang. Jangan membuat penerimaan pengganti saat hasil belum pasti. Setelah dipastikan, muat sumber terbaru sebelum pencatatan berikutnya.

Status pemeriksaan lokal, staging dan penerimaan produksi dicatat dalam [ledger kemajuan](../quality/production-progress.md). Data sintetis tidak menetapkan kesiapan operasional atau kebijakan produksi.

## Penerimaan deployment

Penguncian bukti kebijakan sumber memakai penghapusan bersyarat dan penyisipan kembali BSON yang identik di dalam satu transaksi MongoDB. Data yang terkomit tidak berubah; operasi tersebut memberi konflik tulis nyata terhadap penghapusan atau penggantian bersamaan. Pembaruan tanpa perubahan nilai tidak cukup untuk melindungi pembacaan snapshot. Lihat [pertimbangan transaksi MongoDB](https://www.mongodb.com/docs/manual/core/transactions-production-consideration/).

Uji peran database produksi untuk hak `find`, `insert` dan `remove` yang diperlukan oleh penguncian ini pada `stockSourcePolicyProposals`, `stockSourcePolicyDecisions` dan `stockSourcePolicies`, tanpa memberi hak hapus umum pada aplikasi. Uji juga konflik, rollback dan pemantauan oplog/change stream: pasangan penghapusan/penyisipan ini bukan pencabutan atau penerbitan ulang kebijakan bisnis. Hak serta perilaku provider yang nyata belum diterima melalui pengujian lokal.
