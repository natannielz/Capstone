# Kebutuhan pembelian

Halaman `/inventory/replenishment` menyimpan penilaian kebutuhan barang pada waktu pemeriksaan. Akses memerlukan `stock.read` untuk organisasi, toko, gudang, dan kanal yang dipilih. Status pengujian serta penerimaan produksi tetap mengikuti [checklist utama](../quality/production-progress.md), baris M08; tersedianya kode dan build tidak membuktikan kesiapan produksi.

## Memeriksa kebutuhan

1. Pilih gudang, sumber stok, lalu cari dan pilih SKU beserta kemasan yang tepat. Semua jumlah memakai kemasan tersebut, termasuk pemakaian harian, stok pengaman, batas peringatan, minimum, dan kelipatan pembelian.
2. Isi pemakaian harian dan waktu tunggu berdasarkan data yang telah diperiksa. Kosongkan angka yang belum diketahui. Nol berarti nol yang diketahui. Minimum dan kelipatan pembelian harus diisi secara eksplisit, beserta referensi dasar angka dan alasan pemeriksaan.
3. Pilih **Hitung dan simpan hasil**. Hasil memperlihatkan waktu pengamatan, stok layak, reservasi, komitmen tambahan, pasokan, batas peringatan, aturan, dan alasan bila jumlah belum dapat ditentukan.
4. Jika berwenang membaca dan menyiapkan pengadaan, pilih **Tinjau sebagai permintaan pengadaan**, kemudian **Gunakan saran dalam formulir**. Sistem memeriksa kembali lokasi dan definisi kemasan. Petugas memilih jalur, tanggal, alasan, dan meninjau jumlah sebelum mengirim permintaan untuk persetujuan independen.

Permintaan pembelian atau kebutuhan stok menyimpan referensi `replenishment:<nomor pemeriksaan>` dan mencantumkan pemeriksaan serta waktunya pada spesifikasi barang. Untuk jenis **pemberitahuan barang masuk OMI**, isi referensi pemberitahuan yang sebenarnya; nomor penilaian kebutuhan bukan nomor pemberitahuan OMI. Beralih kembali ke kebutuhan stok mengembalikan referensi penilaian. Membuka formulir tidak mengirim atau menyetujui permintaan.

Perhitungan memakai stok layak **sebelum reservasi**, mengurangi reservasi sekali dan komitmen tambahan yang terbukti belum termasuk reservasi, kemudian menambahkan sisa pasokan yang dijadwalkan tiba dalam waktu tunggu. Kebutuhan dasar adalah `ceil(pemakaian harian × hari tunggu) + stok pengaman`. Kekurangan positif dibulatkan mengikuti minimum dan kelipatan yang diisi; kekurangan nol tetap nol.

Stok harus memiliki sumber aktif, asal barang dan riwayat jumlah yang dapat diverifikasi. Saldo pengamatan hitung fisik bukan pengganti stok yang telah dibuktikan. Sisa pasokan berasal dari alokasi pesanan pembelian yang disetujui, dikurangi penerimaan barang yang sebenarnya; barang yang sudah diterima lalu digunakan tetap dihitung sudah diterima. Jadwal kedatangan harus berasal dari konfirmasi pemasok yang dicatat dan ditinjau secara independen pada pesanan serta SKU yang tepat. Tanggal kebutuhan, invoice, dan pemberitahuan tidak dipakai sebagai perkiraan kedatangan.

Pasokan tanpa jadwal yang disetujui, terlambat, atau cakupannya belum lengkap membuat jumlah pembelian belum diketahui. Jadwal setelah masa tunggu ditampilkan tetapi tidak ditambahkan ke posisi stok dalam masa tunggu. Batas peringatan masih dapat muncul saat pemakaian belum diketahui, selama stok dan komitmennya terbukti. Riwayat yang melampaui batas teknis pembacaan tidak boleh diam-diam dianggap lengkap.

## Hasil tersimpan dan pemulihan

Hasil menyimpan aturan, input, referensi, sumber, dan waktu pengamatan. Bagian **Dasar angka saat pemeriksaan** menampilkan pemakaian harian, waktu tunggu, stok pengaman, batas peringatan, minimum pembelian, dan kelipatan pembelian yang benar-benar dipakai. Angka yang tidak diketahui tetap ditandai demikian. Membuka ulang hasil menunjukkan **penilaian historis tersebut**. Perubahan stok atau jadwal setelahnya memerlukan pemeriksaan baru sebelum keputusan baru; hasil lama tidak menunjukkan stok atau jadwal terkini.

Untuk menemukan hasil tanpa tautan lama, pilih gudang lalu **Muat riwayat terbaru**. Pilih baris berdasarkan SKU, kemasan, dan waktu pemeriksaan untuk membuka hasilnya. Gunakan **Riwayat lebih lama** bila tersedia. Memuat riwayat atau memilih hasil tidak menghitung ulang kebutuhan, membuat pemeriksaan baru, atau mengubah stok. Riwayat hanya memuat gudang yang masih boleh dibaca oleh akun tersebut.

Referensi pengiriman disimpan dalam penyimpanan lokal browser sebelum dikirim. Isinya hanya akun pemilik, kunci permintaan asli, dan waktu; angka stok serta isi formulir tidak disimpan di sana. Jika respons hilang, buka kembali halaman dengan akun pemilik yang sama dan pilih **Periksa hasil tersimpan**. Browser dapat ditutup dan dibuka ulang tanpa mengganti kunci. Jangan menghapus data situs selama hasil belum pasti. Pergantian akun tidak membuka hasil atau referensi akun lain.

Tab lain dengan akun yang sama ikut terkunci saat ada hasil yang belum pasti, lalu terbuka kembali setelah referensi itu terkonfirmasi. Setiap pengiriman memakai entri referensi sendiri: dua tab tidak boleh saling menimpa kunci asli. Jika masih ada referensi lain yang belum pasti, selesaikan pemeriksaannya sebelum mengirim yang baru. Bila penyimpanan browser tidak dapat dibaca, pengiriman ditahan; gunakan **Periksa referensi browser** setelah penyimpanan dapat diakses kembali.

Jika hasil belum ditemukan, referensi tetap dipertahankan dan pengiriman pemeriksaan baru dikunci. Hasil belum ditemukan tidak membuktikan bahwa permintaan sebelumnya gagal. Pemulihan yang berhasil membuka hasil asli, menghapus referensi tertunda, dan tidak menghitung atau membeli ulang. Kegagalan sesi sesudah pengiriman tetap diperlakukan sebagai hasil belum pasti.

Pemeriksaan dan pemulihannya tidak memindahkan stok, membuat pesanan pembelian, membayar pemasok, atau menyetujui permintaan pengadaan. Migration 108 menyediakan penyimpanan penilaian; migration 109 menyediakan pemilik jadwal kedatangan. Keduanya tidak mengisi angka kebutuhan, stok, pemasok, jadwal, atau izin operasional bawaan.
