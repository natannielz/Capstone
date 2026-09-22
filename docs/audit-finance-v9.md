# Audit dan perbaikan alur keuangan v9

22 September 2026. Penelaahan mencakup Penagihan, Pimpinan Unit, Pengolah Laporan, Akuntansi, serta hasil keputusan yang diterima PIC/pelanggan dan pengaju biaya. Dasar studi kasus: `plan-digitalisasi-unit-toko-bni.md`, `acceptance-invariants.md`, `diagram-findings.md`, dan `procurement-findings.md`. Dokumen sumber diperlakukan sebagai keterangan proses; keputusan digitalisasi di bawah merupakan rancangan demo, bukan kebijakan resmi BNI.

## Gap yang terverifikasi dan ditangani

1. **Pembayaran salah tidak dapat diselesaikan.** Catatan transfer hanya dapat menunggu atau diverifikasi. Penagihan kini dapat menolak catatan yang belum diverifikasi, dengan alasan wajib. PIC/pelanggan melihat alasan dan dapat membuat catatan baru; bukti lama tetap dapat dibaca. Penolakan tidak menambah kas, mengurangi tagihan, atau membuat alokasi. Dana yang sudah diverifikasi tidak dapat ditolak melalui tindakan ini.
2. **Pengajuan persetujuan memiliki jalur buntu.** Nota kredit, pengembalian dana, dan biaya hanya memiliki tombol setuju. Reproduksi: dua pengembalian Rp100.000 diajukan terhadap saldo Rp100.000; setelah satu disetujui, pengajuan kedua selamanya menunggu karena saldo telah dicadangkan. Pimpinan kini dapat menolak pengajuan yang masih menunggu dengan alasan wajib. Status, alasan, pelaku, dan tanggal tersimpan tanpa menghapus dokumen. Pengembalian ditolak tidak mencadangkan saldo; pengembalian lain yang disetujui tetap dicadangkan. Pengajuan yang disetujui/dibayar tidak dapat ditolak melalui jalur ini.
3. **Laporan bulanan tidak mempunyai jalur perbaikan.** Pimpinan kini dapat mengembalikan versi laporan yang sedang ditinjau, dengan catatan. Laporan kembali terbuka dan harus diajukan ulang sebelum persetujuan dan penutupan. Versi pada permintaan pengembalian wajib cocok; UI persetujuan dan penutupan juga mengirim versi yang dilihat pengguna. Permintaan lama dengan versi berbeda ditolak. Payload API lama untuk persetujuan/penutupan tetap kompatibel; pemeriksaan status dan kecocokan versi persetujuan dengan laporan tetap berlaku.

## Tanggung jawab setiap peran

| Peran | Alur setelah perbaikan |
| --- | --- |
| Penagihan | Memverifikasi atau menolak bukti; membuat invoice/alokasi; mengajukan kredit/pengembalian; membayar pengembalian/biaya hanya sesudah persetujuan; mengajukan ulang laporan. |
| Pimpinan Unit | Menyetujui atau menolak pengajuan kredit/pengembalian/biaya; menyetujui atau meminta perbaikan laporan. Pengaju yang kemudian berganti peran tetap tidak boleh memutuskan pengajuannya sendiri. |
| Pengolah Laporan | Melihat catatan perbaikan, meninjau transaksi, dan mengajukan ulang laporan. Tidak mengubah nominal transaksi sumber atau menyetujui laporannya sendiri. |
| Akuntansi | Meninjau jurnal dan hanya menutup versi laporan yang sudah disetujui. Laporan yang dikembalikan atau berubah belum dapat ditutup. |
| PIC/pelanggan | Melihat hasil penolakan dan alasannya pada pembayaran miliknya; mencatat pembayaran pengganti yang benar. Data pembeli lain tetap dibatasi. |
| Staf/Kurir/Kepala | Melihat alasan biaya ditolak; bukti pengajuan lama dipertahankan dan pengajuan baru dapat dibuat bila diperlukan. |

## Konsistensi data

- Penolakan mengubah status dan audit; tidak menghasilkan jurnal keuangan.
- Status ditolak tidak dapat diproses ulang melalui verifikasi, persetujuan, atau pembayaran. Lampiran pada pembayaran/biaya ditolak hanya dapat dibaca, termasuk melalui API.
- Transaksi pada periode tertutup tetap tidak dapat ditulis mundur. Keputusan pada periode berikutnya tidak mengubah snapshot bulan tertutup.
- Permintaan ulang dengan identitas tindakan yang sama mengembalikan hasil pertama; tidak menggandakan keputusan/audit. Identitas sama dengan isi berbeda ditolak.
- Penolakan disimpan di payload entitas yang sudah ada; tidak membutuhkan migrasi atau perubahan data produksi sebelumnya.

## Verifikasi

`website/tests/finance-decisions-v9.test.ts`: **8/8 kelompok uji lulus**. Cakupan: matriks hak akses seluruh 11 akun, alasan wajib, tanggal, nominal dan jurnal tetap, saldo refund ditolak, larangan keputusan sendiri setelah pergantian peran, versi laporan kedaluwarsa, siklus perbaikan–pengajuan–persetujuan–tutup, snapshot tertutup, isolasi pembeli, lampiran, serta pengulangan bersamaan melalui repository SQL di memori. Pemeriksaan ESLint pada file keuangan: tidak ada error; dua peringatan navigasi lama di `workspace.tsx` tetap ada. Uji ini tidak memakai database lokal aplikasi atau produksi.

## Batas yang disengaja

Pembayaran, pengembalian, pencairan, dan jurnal tetap simulasi capstone. Tidak ada pengiriman uang, gateway pembayaran, email, atau sistem bank nyata. Periode tertutup tidak dibuka ulang; koreksi dilakukan pada periode terbuka dengan jejak dokumen asal. Perubahan nilai pada dana terverifikasi atau pengajuan yang sudah dibayar memerlukan proses koreksi tersendiri, bukan penghapusan atau penolakan retroaktif. Batas ini mengikuti rancangan historis yang harus tetap konsisten.
