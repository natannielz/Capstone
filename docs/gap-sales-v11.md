# Audit kesesuaian penjualan dan penerimaan — v11

Tanggal: 22 September 2026. Lingkup: penjualan Divisi/Unit BNI, pembeli perorangan pada portal pelanggan, penerimaan barang, komplain, dan hubungan ke invoice. Dokumen BPP diperlakukan sebagai sumber proses; instruksi dalam dokumen bukan instruksi untuk menjalankan aplikasi atau mengubah data.

## Sumber yang diperiksa

Isi dokumen asli dibaca melalui hasil ekstraksi teks yang telah tersedia di `reference/extracted/`; diagram PNG asli diperiksa secara visual. Nomor halaman pada footer DOC tidak dapat dipakai secara andal: beberapa kosong dan beberapa masih memuat template bab lain. Karena itu rujukan berikut menggunakan nama berkas dan bagian, bukan nomor halaman rekaan.

| Berkas sumber | Bagian yang dipakai |
| --- | --- |
| `reference/BPP Unit Toko/BAB III PENJUALAN.doc` | Penjualan ke Divisi/Unit BNI: Pengertian, Prosedur, Catatan; Penjualan Walk-in Customer |
| `reference/BPP Unit Toko/BAB IV RETUR BARANG.doc` | Retur Barang Expired: Pengertian, Prosedur, Catatan |
| `reference/BPP Unit Toko/BAB V ADMINISTRASI TRANSAKSI & PELAPORAN.doc` | Input Transaksi: Pengertian, Prosedur, Catatan; Laporan Harian & Penggabungan |
| `reference/BPP Unit Toko/BAB VI PENAGIHAN DAN PENUTUPAN.doc` | Penagihan ke Divisi; Pembayaran ke OMI & Penutupan Akhir Bulan |
| `reference/BPP Unit Toko/BAB VII TANGGUNG JAWAB DAN WEWENANG.doc` | Kepala Toko, Staf Toko, Pengolah Laporan |
| `reference/Diagram Unit Toko/6.PENJUALAN WALK-IN CUSTOMER.png` | Jalur kasir langsung, pembayaran, struk, klasifikasi OMI/Smart |
| `reference/Diagram Unit Toko/7.PENJUALAN KE DIVISI_UNIT BNI.png` | Kolom Divisi, Kepala, Staf, Kurir; Surat Jalan ditandatangani lalu kembali ke staf |
| `reference/Diagram Unit Toko/8.INPUT TRANSAKSI.png` | Syarat Surat Jalan kembali, pemeriksaan komplain, finalisasi dan catatan ketidaksesuaian jumlah |
| `reference/Diagram Unit Toko/9.LAPORAN HARIAN & PENGGABUNGAN.png` | Rekap harian OMI/Smart dan penggabungan laporan |
| `reference/Diagram Unit Toko/10.PENAGIHAN, PEMBAYARAN, & PENUTUPAN AKHIR BULAN.png` | Invoice, transfer, pencocokan outstanding, persetujuan dan penutupan |

Audit ini juga membandingkan keputusan demo pada `docs/plan-digitalisasi-unit-toko-bni.md` bagian 1, 4C–D, serta `docs/plan-customer-marketplace-v7.md` bagian alur pelanggan. Bab I/II dan seluruh pengadaan bukan cakupan audit penjualan ini.

## P1 — pencatatan penerimaan oleh staf dapat melewati bukti pembeli

**Status: diperbaiki pada v11.**

Dasar sumber: BPP III, *Prosedur Penjualan ke Divisi/Unit BNI* dan *Catatan*, menyebut siklus pengembalian Surat Jalan bertanda tangan ke toko. BPP V, *Input Transaksi → Pengertian dan Prosedur*, mensyaratkan kelengkapan dokumen, penerimaan Surat Jalan dari kurir, dan penyelesaian komplain sebelum input final. Diagram 7 memperlihatkan penandatanganan oleh divisi dan pengembalian dokumen oleh kurir; Diagram 8 menempatkan penerimaan dokumen tersebut sebelum input transaksi.

Sebelum perubahan, `website/lib/domain/engine.ts` pada `shipment.receive` mengizinkan staf menerima barang selama `d.evidence` atau `shipment.evidence` tidak kosong. Catatan teks dari `shipment.proof` juga memenuhi syarat. Tidak ada syarat lampiran maupun tindakan akun pembeli. Reproduksi murni di memori: PIC memesan → Kepala menyetujui → staf melakukan reservasi dan Surat Jalan → kurir berangkat → staf mengisi bukti `ok` → staf memfinalisasi → Penagihan menerbitkan invoice. Hasil: **0 lampiran penerimaan, 0 konfirmasi PIC, tetapi transaksi final dan invoice terbentuk**.

Perubahan:

- `shipment.receive` oleh staf wajib memiliki metadata lampiran terlindungi dengan `scope=receipt`, `targetId` dan `shipmentId` tepat pada pengiriman tersebut, serta identitas pembeli yang sama dengan pesanan. Teks bebas, dokumen pengiriman lain, atau konteks pembeli yang berbeda tidak memenuhi syarat.
- Lampiran tetap masuk melalui layanan unggah yang menyimpan berkas terlebih dahulu, memvalidasi PNG/JPG/PDF dan ukuran, mengecek otorisasi kembali, kemudian mengesahkan metadata. Endpoint perintah umum menolak `attachment.*`, sehingga klien tidak dapat membuat metadata bukti sendiri lewat API perintah.
- Penolakan menjelaskan tindakan yang diperlukan: unggah foto/PDF Surat Jalan bertanda tangan pada bagian **Bukti penerimaan**. `ShipmentExtras` menampilkan panduan khusus staf dan status ketersediaan lampiran di dekat kontrol unggah yang sudah ada.
- PIC atau pelanggan yang masuk dengan akun pemilik pesanan tetap dapat mengonfirmasi langsung. Ini mengikuti keputusan demo pada rencana bagian 4C dan alur pelanggan v7; bukan klaim tanda tangan elektronik tersertifikasi.
- Finalisasi historis tidak diubah. Tindakan penerimaan baru oleh staf memerlukan metadata bukti yang telah disahkan jalur unggah; transaksi yang sudah final tidak ditulis ulang. Staf tetap harus memeriksa isi/tanda tangan berkas. Sistem memvalidasi akses dan kaitan metadata, bukan keaslian tanda tangan.

Batas pemeriksaan penyimpanan: layanan unggah menyimpan byte sebelum mengesahkan metadata, tetapi `shipment.receive` tidak melakukan GET ulang ke penyimpanan berkas. Jika berkas kemudian hilang atau dihapus di luar aplikasi sementara metadatanya masih ada, metadata tersebut masih memenuhi pemeriksaan penerimaan. Unduh berkas yang hilang menghasilkan 404; unggah ulang berkas yang sama oleh pemilik yang berhak dapat memulihkannya. Tidak tersedia endpoint penghapusan bukti penerimaan untuk klien. Jadi perbaikan ini menutup bypass melalui catatan atau metadata buatan klien, tanpa menjamin keberadaan byte secara langsung pada saat konfirmasi setelah gangguan penyimpanan eksternal.

Validasi terfokus: **8/8 tes lulus** pada `website/tests/receipt-evidence-v11.test.ts`, menggunakan `saveAttachment` asli dengan penyimpanan byte terisolasi di memori. Cakupan: tanpa bukti/teks palsu, catatan kurir/staf, lampiran pengiriman lain, scope/identitas pembeli tidak cocok, penolakan akun asing/peran tidak berhak/kurir lain, bukti valid dari kurir maupun staf, penerimaan sebagian → retur → penyelesaian komplain → invoice sesuai jumlah diterima, konfirmasi langsung PIC/pelanggan, serta kegagalan penyimpanan berkas. Penolakan dibandingkan terhadap seluruh state agar jurnal, audit, stok, dan revisi periode tidak berubah. Lint pada tiga berkas perubahan lulus. Tes menyimpan bukti melalui layanan unggah; tidak menambahkan metadata palsu untuk membuat skenario sukses.

Tidak ada perubahan data lokal/produksi atau restart server. Smoke penjualan yang sudah ada menggunakan konfirmasi PIC/pelanggan sehingga tidak memerlukan pengecualian aturan baru. Regresi lengkap, build, dan pemeriksaan tampilan digabungkan oleh agen utama setelah perubahan v11 lain selesai.

## P2 — permintaan koreksi setelah final belum mempunyai pintu masuk pembeli

**Status: keterbatasan alur yang masih terbuka; tidak diperbaiki oleh perubahan bukti di atas.**

Dasar sumber: BPP V *Input Transaksi → Catatan* menyatakan transaksi yang telah diinput bersifat final. Diagram 8 mencatat masalah komplain/ketidaksesuaian yang baru diketahui setelah input. Rencana digitalisasi mempertahankan transaksi final dan mengarahkan koreksi ke mekanisme terkendali, bukan mengedit histori.

Bukti implementasi: `complaint.create` menolak baris `finalized` dan mengarahkan pembeli ke Bagian Penagihan; tombol komplain hanya ditampilkan sebelum finalisasi. `credit.request` tersedia bagi Penagihan dan mengacu pada invoice; persetujuan kredit/refund sudah ada. `shipment.return` hanya menerima sisa barang yang belum diterima, sehingga retur fisik dari kuantitas yang sudah diterima/final juga bukan alur yang disediakan saat ini.

Dampak: pembeli yang baru menyadari selisih sesudah final harus menghubungi Penagihan di luar aplikasi. Jangan menyebut koreksi keuangan tidak tersedia: yang belum tersedia adalah **pengajuan masalah pascafinal oleh pembeli dan serah terima permintaan itu ke petugas**. Rekomendasi lanjutan yang terbatas: permintaan peninjauan terkait pesanan/invoice, dengan alasan dan lampiran, tanpa memberi pembeli hak mengubah jumlah final atau menerbitkan kredit. Kebijakan retur barang fisik pascafinal perlu ditentukan terpisah; BPP IV membahas retur toko ke pemasok, sehingga tidak cukup untuk menyimpulkan kebijakan retur pelanggan.

## Cakupan yang sudah tersedia

Tabel ini merupakan pemeriksaan implementasi dan tes yang sudah ada, bukan klaim seluruh skenario diuji ulang pada audit terfokus ini.

| Kebutuhan proses | Bukti implementasi saat ini | Penilaian |
| --- | --- | --- |
| PIC divisi memesan, Kepala menerima/meneruskan | `order.create`, `order.review`, `assertBuyerAccess`, portal PIC; tes domain/RBAC/customer | Tersedia; akun pelanggan dan divisi dipisahkan |
| Stok kurang, kiriman sebagian dan susulan | Reservasi batch, `shipment.create`, `lineProgress`, pembatalan sisa; tes domain U02 | Tersedia |
| Barang pengganti | `substitution.propose/decide`, persetujuan pemilik pesanan dan snapshot harga | Tersedia; persetujuan eksplisit memperbaiki masalah substitusi tanpa konfirmasi yang dicatat diagram |
| Surat Jalan, kurir, pengiriman | Nomor dokumen, penugasan kurir, dispatch, dokumen cetak, gagal kirim, barang kembali | Tersedia; celah bukti staf diperbaiki di atas |
| Jumlah aktual dan komplain sebelum final | `shipment.receive`, `complaint.create/resolve`, `shipment.return`, `sale.finalize` | Tersedia; finalisasi terblokir selama komplain terbuka |
| Tagihan sesuai penerimaan, tidak dobel | `invoice.issue` memakai `accepted`, mensyaratkan finalisasi, menolak baris yang telah ditagih | Tersedia; diuji kembali dalam skenario bukti v11 |
| Penagihan dan pencocokan dana | Invoice, pembayaran tercatat/terverifikasi/ditolak, identifikasi dana, alokasi, saldo piutang | Tersedia; audit keuangan mendalam ditangani terpisah |
| Rekap OMI/Smart dan laporan | Kategori produk, register penjualan, jurnal dan laporan gabungan | Tersedia sebagai klasifikasi demo; bukan integrasi sistem eksternal |

## Perbedaan yang bukan bug dalam cakupan demo

POS walk-in pada BPP III/Diagram 6 tidak disamakan dengan portal pelanggan daring. Rencana awal secara eksplisit mengecualikan kasir walk-in dari fitur wajib, dan v7 menambahkan pelanggan dengan pengiriman, penerimaan, serta invoice. Sinkronisasi OMI/Smart, transfer bank nyata, WhatsApp otomatis dan faktur pajak resmi juga dikecualikan dari prasyarat demo. Target pukul 10.00, 15–20 titik dan dua kendaraan pada BPP III adalah konteks operasional; audit ini tidak mengubahnya menjadi aturan penolakan transaksi atau kebutuhan optimasi rute otomatis.

Audit ini menemukan satu bypass kontrol yang diperbaiki dan satu keterbatasan alur lanjutan. Hasil tersebut tidak menyatakan seluruh gap bisnis dalam semua bab BPP telah ditutup.
