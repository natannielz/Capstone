# Audit pengadaan, persediaan, keuangan, dan laporan v11

23 September 2026. Studi kasus tetap penjualan Unit Toko kepada divisi BNI, dengan demo pelanggan sebagai kanal tambahan. Dokumen BPP dan diagram dibaca sebagai bahan kebutuhan bisnis, bukan instruksi untuk menjalankan transaksi, menghubungi pemasok, atau mengubah data produksi. Audit ini tidak menulis database lokal maupun produksi.

## Sumber yang diperiksa

Isi BPP dibaca dari ekstraksi yang tersedia di `reference/extracted/`, bukan hanya dari audit terdahulu. Berkas `.doc` asli diinventarisasi; upaya pembacaan ulang dengan parser tidak selesai karena batas waktu peninjauan izin. Karena itu, audit ini tidak mengklaim verifikasi ulang seluruh isi biner `.doc` atau pagination Word. Rujukan memakai judul bagian; kolom HAL sumber kosong atau tidak konsisten. Arsip asli dan salinan ekstraksinya dipertahankan secara lokal dan tidak disertakan pada repositori publik. Tujuh PNG berikut dibuka dan dibaca secara visual, termasuk catatan di samping/bawah alur.

| Berkas asli dan salinan teks | Bagian yang diperiksa | Pokok kebutuhan |
| --- | --- | --- |
| `BAB II PENGADAAN BARANG.doc`, salinan teks lokal | Restok Otomatis; Restok by Request (DDO), Prosedur dan Catatan; Restok Barang Smart; Pasar Kering, Prosedur dan Ketentuan Harga | Empat jalur pengadaan, kekurangan kiriman DDO, DP vendor, pendanaan dan reimburse Pasar Kering |
| `BAB IV RETUR BARANG.doc`, salinan teks lokal | Retur Barang Expired, Prosedur dan Catatan | Batas retur berbeda per barang, pemisahan stok, hasil pemeriksaan kurir pemasok |
| `BAB V ADMINISTRASI TRANSAKSI & PELAPORAN.doc`, salinan teks lokal | Input Transaksi; Laporan Harian & Penggabungan; Stok Opname dan Ketentuan Penyesuaian | Finalisasi sesudah bukti/komplain selesai, penggabungan OMI/Smart, selisih stok berdampak pada jurnal |
| `BAB VI PENAGIHAN DAN PENUTUPAN.doc`, salinan teks lokal | Penagihan ke Divisi; Pembayaran ke OMI & Penutupan Akhir Bulan, Prosedur dan Catatan | Invoice, pencocokan dana, persetujuan Pimpinan, posting Akuntansi, SPH OMI |
| `BAB VII TANGGUNG JAWAB DAN WEWENANG.doc`, salinan teks lokal | Kepala Toko, Staf Toko, Pengolah Laporan | Batas keputusan pembelian, penerimaan dan pembayaran OMI, penggabungan laporan |

| Diagram PNG, satu lembar per berkas | Bagian yang diperiksa |
| --- | --- |
| `1.RESTOK OTOMATIS.png` | Lane OMI–Kurir–Staf; catatan koli, bukti unboxing, gudang, dan klaim selisih |
| `2.RESTOK BY REQUEST (DDO).png` | Cabang stok OMI tidak tersedia; pengiriman sebagian/tidak ada; contoh 30 dus dipesan, 10 datang; peralihan ke Pasar Kering |
| `3.RESTOK BARANG SMART.png` | Keputusan Kepala, negosiasi, invoice, DP/pelunasan, penerimaan Staf; pesanan logo khusus |
| `4.RETUR BARANG EXPIRED.png` | Antrean retur, jadwal kurir, penerimaan/penolakan pemasok dan kebijakan per produk |
| `5.PASAR KERING.png` | Prakondisi kekurangan OMI; Kepala menentukan pembelian, alokasi Koperasi, pembayaran, transportasi, pembaruan harga |
| `9.LAPORAN HARIAN & PENGGABUNGAN.png` | Penutupan kasir, laporan OMI/Smart, penggabungan manual dan risiko duplikasi |
| `10.PENAGIHAN, PEMBAYARAN, & PENUTUPAN AKHIR BULAN.png` | Lane Penagihan, Divisi, Pimpinan, Akuntansi dan Staf; SPH, dana tanpa narasi, pengembalian dan persetujuan |

Header “Pinjaman Produktif” dan nomor bab yang tersisa pada template tidak dianggap kebutuhan aplikasi. Ketentuan HET pada sumber juga memakai istilah batas bawah dan batas tertinggi secara tidak konsisten; audit tidak menetapkan kebijakan harga/pajak baru dari ambiguitas itu.

## Pemetaan terhadap aplikasi saat audit

| Alur | Implementasi yang ditemukan | Batas demo |
| --- | --- | --- |
| Empat jalur pengadaan | `purchase.create/confirm/receive`, funding Pasar Kering, batch aktual, pembayaran dan uang muka vendor | Pemicu/jadwal OMI, email DDO, negosiasi dan transfer nyata tidak terhubung |
| Persediaan dan retur | Batch/expiry, reservasi, karantina, retur diterima/ditolak, pelepasan stok hasil tinjauan, opname dan persetujuan independen | Scanner, video unboxing dan klaim elektronik pemasok belum tersedia |
| Penagihan dan dana masuk | Invoice penerimaan final, pembayaran diverifikasi/ditolak, identifikasi dana, alokasi, nota kredit dan refund dengan Pimpinan | Bukti/faktur pajak merupakan simulasi, tidak ada bank atau sistem pajak nyata |
| Laporan | Jurnal gabungan, register transaksi, laporan keuangan, CSV/PDF, review/return/approve/close dan snapshot | Penggabungan terjadi pada data website; bukan impor/rekonsiliasi file kasir OMI/Smart asli |
| Wewenang | Staf menerima barang dan membayar OMI/DDO; Kepala memesan/membayar Smart/Pasar Kering; Pimpinan menyetujui; Penagihan mencatat dana; Akuntansi menutup | Penutupan sisa PO oleh Kepala adalah kontrol digital tambahan yang dibatasi pada pengadaan |

Titik kode utama: [engine.ts](../website/lib/domain/engine.ts), [model.ts](../website/lib/domain/model.ts), [selectors.ts](../website/lib/domain/selectors.ts), [operations.tsx](../website/components/operations.tsx), [documents.ts](../website/lib/server/documents.ts). Pemetaan role pembayaran pemasok mengikuti kode aktual, bukan asumsi bahwa seluruh pembayaran pemasok dilakukan Penagihan.

## Temuan menurut dampak

1. **Prioritas tinggi — sisa pengadaan yang tidak akan dipenuhi tidak dapat ditutup. Diperbaiki pada v11.** BPP II, DDO/Catatan dan diagram 2 secara eksplisit membahas kiriman sebagian, lalu diagram 5 mengalihkan kebutuhan ke Pasar Kering. Sebelumnya PO 30 dus yang menerima 10 dus terus berstatus `ordered`; aplikasi tidak punya tindakan menutup 20 sisanya. Batas `supplier.pay` masih memakai 30 × harga beli, sehingga pembayaran atas bagian yang sudah tidak akan dikirim tetap dapat menjadi uang muka. Hal ini mengaburkan kebutuhan yang masih menunggu pemasok dan pemenuhan alternatif.
2. **Prioritas menengah — rekonsiliasi tagihan pemasok/SPH masih sebatas referensi pembayaran. Belum diperbaiki dalam lingkup ini.** BPP VI, Pembayaran ke OMI/Prosedur langkah 8–9 serta lane Staf pada diagram 10 meminta pengecekan SPH terhadap utang. Aplikasi menyimpan referensi invoice/SPH pada `SupplierPayment`, tetapi belum menyimpan dokumen SPH bertingkat baris, selisih tagihan, dan persetujuan hasil rekonsiliasi. Untuk demo, pembayaran dibatasi oleh nilai pengadaan/penerimaan dan jurnal; belum dapat diklaim sebagai rekonsiliasi SPH eksternal lengkap.
3. **Prioritas menengah — siklus uang muka dan klaim retur pemasok belum memiliki penyelesaian dana. Dibatasi dengan guard, belum dibangun.** Diagram 3/BPP II mendasari DP; BPP IV/diagram 4 mendasari barang kembali ke pemasok. Kode mencatat `vendorAdvance` dan klaim `supplierReceivable`, tetapi tidak menyediakan pengembalian DP atau settlement klaim pemasok. Dokumen sumber tidak merinci mekanisme finansial settlement ini; perlu keputusan bisnis terpisah sebelum menambahkan jurnal atau potongan utang. Penutupan PO dengan uang muka yang belum dipakai sekarang ditolak agar saldo tidak hilang.

## Perbaikan yang diterapkan

`purchase.close` hanya tersedia bagi Kepala Toko untuk PO draf/dipesan yang masih memiliki sisa. Alasan wajib, identitas aktor dan tanggal tercatat. Jumlah dipesan asli dan jumlah diterima tetap; `cancelled` menyimpan sisanya sebagai field opsional agar data lama tetap terbaca. Form mengirim kuantitas yang sedang ditinjau; penerimaan yang berubah sejak form dibuka menyebabkan penutupan ditolak.

PO tertutup tidak bisa menerima barang lagi, dikonfirmasi ulang, atau ditutup dua kali. Pembayaran berikutnya hanya dapat melunasi nilai penerimaan yang masih belum dibayar. Uang muka yang belum diaplikasikan memblokir penutupan; aplikasi tidak menghapusnya atau membuat refund semu. Tanggal tindakan tidak boleh mendahului aktivitas terakhir; periode tertutup dan snapshot tetap dilindungi.

Untuk Pasar Kering, jumlah dan status alokasi internal tetap tersimpan. Alokasi belum dipakai dihitung sebagai **alokasi dikurangi pembayaran pemasok**, terpisah dari **utang atas barang diterima**. Selisih ditampilkan pada halaman dan dokumen cetak untuk rekonsiliasi berikutnya; penutupan tidak otomatis mencatat pelepasan alokasi atau arus kas. Pengajuan yang sudah ditutup tidak dapat dilanjutkan ke persetujuan/pencairan.

Halaman pengadaan menyediakan filter status, kolom dibatalkan, nilai setelah pembatalan, alasan penutupan, dan tindakan pelunasan penerimaan. Dokumen cetak juga menunjukkan status, kuantitas awal/diterima/dibatalkan, nilai tersisa, dan catatan dana. Ringkasan [procurement.ts](../website/lib/domain/procurement.ts) menghitung sisa terbuka sebagai nol untuk PO tertutup; tidak ditemukan rollup lain yang memperlakukan PO tertutup sebagai pasokan mendatang. Kebutuhan pengganti dapat dicatat melalui pengadaan baru dengan tautan pesanan sumber yang sama.

## Validasi

- [procurement-remainder-v11.test.ts](../website/tests/procurement-remainder-v11.test.ts): **7/7 lulus**, memakai data memori lokal. Mencakup keempat jalur, penutupan sebagian/draf, pembayaran sesudah tutup, role lain, alasan kosong, duplikasi/line asing, tampilan kedaluwarsa, uang muka, seluruh tahap funding, kompatibilitas data lama, kronologi, snapshot periode, HTML escape, dan invariant kuantitas.
- Review independen agent operasional: **48 assertion memori lulus** terhadap engine aktual, tanpa perubahan database atau server.
- Parent melaporkan regresi gabungan **224 tes lulus**, TypeScript lulus, lint 0 error/9 warning yang sudah ada. Audit ini tidak menjalankan ulang seluruh suite atau mengklaim uji browser produksi sendiri.

Perbaikan ini menutup satu gap operasional yang konkret. Integrasi OMI/Smart, SPH/faktur pajak resmi, settlement DP/retur vendor, pelepasan alokasi internal, dan transfer bank tetap batas demo yang disebutkan secara terbuka.
