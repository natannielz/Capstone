# Audit operasional Unit Toko — v9

Tanggal: 22 September 2026. Lingkup: demo capstone, dengan pemeriksaan kode dan tes domain lokal. Tidak ada mutasi pada data produksi selama audit ini.

## Temuan yang diperbaiki

| Temuan | Dampak sebelum perbaikan | Perbaikan |
| --- | --- | --- |
| Cadangan batch kedaluwarsa tidak dapat dipindahkan | Dispatch ditolak karena kedaluwarsa, reservasi baru ditolak karena jumlah sudah dicadangkan, dan pembatalan Surat Jalan tetap mempertahankan cadangan. Pesanan produk yang sama terhenti meski batch layak tersedia. | Staf dapat **Lepas cadangan** per batch dengan jumlah dan alasan, kemudian mencadangkan batch layak. Kuantitas dalam Surat Jalan siap kirim dilindungi; batalkan Surat Jalan itu terlebih dahulu. Pesanan pembeli tetap aktif. |
| Produk yang sudah dipakai tidak dapat dihentikan penjualannya melalui UI | Flag `active` digunakan untuk membatasi pemesanan, tetapi pengaturan barang tidak mengekspos flag tersebut. Perubahan identitas lewat `master.product` juga ditolak untuk produk bersejarah. | Kepala Toko dapat memilih **Status penjualan** pada **Atur barang**. Produk nonaktif berlabel **Tidak dijual**, tidak menerima pesanan atau substitusi baru, dan dapat diaktifkan kembali. Pesanan yang sudah disetujui tetap dapat dipenuhi menggunakan harga sebelumnya. |
| Usulan pengganti lama dapat disetujui setelah produk tujuan nonaktif | Pemeriksaan aktif hanya dilakukan saat usulan dibuat. | Persetujuan juga memeriksa produk tujuan; usulan tetap bisa ditolak agar toko dapat menawarkan pilihan baru. |
| Pilihan keranjang internal hilang dari tampilan saat produk nonaktif | `Catalog` hanya merender produk aktif meski keranjang persisten masih menyimpan pilihan tersebut. Barang tersembunyi tidak dapat dihapus dan dapat muncul kembali saat produk diaktifkan. Keranjang campuran dapat melanjutkan pesanan tanpa memberi tahu ada barang yang dihilangkan. | Keranjang PIC/Kepala kini menampilkan baris barang tidak tersedia beserta jumlah tersimpan dan tombol hapus. Kelanjutan pesanan diblokir sampai pengguna menghapus pilihan yang tidak tersedia. Jika status berubah saat dialog pengiriman sudah terbuka, isian tetap tersimpan dan dialog mengarahkan kembali ke keranjang. |

Pelepasan cadangan tidak mengubah stok fisik atau jurnal. Server memeriksa peran staf, jumlah bulat, cadangan bebas pada batch yang dipilih, kronologi, periode terbuka, dan alasan. Riwayat aktivitas menyimpan jumlah, batch, dan alasan; kuantitas pesanan serta dokumen pengiriman lama tidak dihapus.

## Pemeriksaan peran dan alur yang sudah tersedia

| Peran | Kemampuan yang ditemukan dalam UI dan server |
| --- | --- |
| PIC divisi | Katalog dan pengajuan pesanan, pesanan divisi sendiri, persetujuan/penolakan substitusi, pembatalan sisa, konfirmasi penerimaan aktual, komplain sebelum finalisasi, invoice dan bukti pembayaran. |
| Kepala Toko | Tinjauan/penolakan pesanan, pembuatan barang baru, pengaturan harga dan stok minimum, pengadaan, pengajuan dana, tinjauan kelayakan retur, persetujuan selisih opname; kini status penjualan barang. |
| Staf Toko | Reservasi, Surat Jalan parsial, pembatalan Surat Jalan sebelum dispatch, penerimaan/pengembalian barang, penyelesaian komplain, finalisasi, pengadaan OMI/DDO, penerimaan pemasok bertahap, retur dan opname; kini pelepasan cadangan. |
| Kurir | Daftar tugas yang ditugaskan, keberangkatan, bukti serah terima, gagal kirim, pengajuan biaya terkait tugas sendiri. Harga dan data tagihan tidak diberikan pada proyeksi data kurir. |

Pembuatan produk baru sudah tersedia lewat **Persediaan → Tambah barang** dan tidak ditambahkan ulang. Empat jalur pengadaan memakai pemeriksaan penerimaan aktual; sisa pengadaan tetap terbuka. Retur ditolak tetap ditahan sampai Kepala Toko meninjau kelayakannya, dan barang kedaluwarsa tidak dapat dilepas sebagai layak jual.

Pemeriksaan ini membandingkan `plan-digitalisasi-unit-toko-bni.md`, `diagram-findings.md`, `procurement-findings.md`, dan `acceptance-invariants.md` dengan `engine.ts`, `selectors.ts`, `order-experience.tsx`, `workspace.tsx`, `operations.tsx`, serta `inventory-workspace.tsx`. Persetujuan keuangan dan pengelolaan akun ditelaah pada audit terpisah; tabel di atas bukan klaim bahwa seluruh kemungkinan operasional produksi sudah didukung.

## Verifikasi perubahan

`website/tests/operations-recovery-v9.test.ts`: **7 tes lulus**.

- Batch kedaluwarsa → pembatalan Surat Jalan → pelepasan cadangan → batch layak → dispatch pesanan yang sama.
- Pelepasan sebagian melindungi cadangan pada Surat Jalan siap kirim dan pesanan lain.
- Penolakan akses semua peran selain staf, alasan kosong, pecahan, batch tanpa cadangan, serta tanggal mendahului aktivitas.
- Periode tertutup menolak pelepasan cadangan.
- Produk bersejarah dapat dinonaktifkan; pesanan baru PIC/Kepala/pelanggan ditolak; pesanan sebelumnya tetap selesai dengan harga lama; aktivasi ulang menerima pesanan baru.
- Substitusi menuju barang nonaktif ditolak tanpa perubahan sebagian, dan usulan tetap dapat ditolak.
- Hanya Kepala Toko mengubah status penjualan; request pengaturan lama yang tidak mengirim `active` mempertahankan status saat ini.

Verifikasi browser, regresi gabungan, dan penerbitan dicatat oleh pemeriksaan rilis utama.

`website/tests/catalog-basket-v9.test.ts`: **2 tes lulus**. Barang nonaktif maupun yang tidak lagi ditemukan tetap terlihat dalam proyeksi keranjang tanpa mengubah data tersimpan; keranjang campuran tidak boleh dilanjutkan. Aktivasi kembali mempertahankan jumlah pilihan, sedangkan penghapusan eksplisit tersimpan lintas pemuatan dan mencegah barang muncul kembali setelah aktif. Pemeriksaan ketersediaan juga dilakukan kembali sebelum pengajuan dari dialog.
