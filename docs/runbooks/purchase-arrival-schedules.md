# Jadwal kedatangan pemasok

Gunakan bagian **Jadwal kedatangan pemasok** pada `/procurement/purchase-orders` setelah memilih pesanan pembelian yang sudah disetujui. Jadwal berlaku untuk satu barang dan definisi kemasan pada PO tersebut. Tanggal jatuh tempo pembayaran dan tanggal pemberitahuan pengiriman tidak dianggap sebagai waktu kedatangan.

1. Pilih barang dan kemasan, lalu **Muat jadwal kedatangan**. “Belum dicatat” berarti belum ada waktu yang dapat dipakai. Kolom tanggal, referensi dan alasan dimulai kosong.
2. Petugas dengan izin menyiapkan pengadaan dan MFA terbaru memasukkan waktu yang dikonfirmasi pemasok dalam WIB, referensi konfirmasi, dan alasan. Jika pemasok belum dapat memastikan waktu, pilih penarikan jadwal; jangan mengisi tanggal perkiraan tanpa sumber.
3. Pilih **Periksa usulan jadwal**, periksa barang, waktu, referensi dan alasan, lalu konfirmasi. Simpan satu kali. Usulan baru langsung membuat waktu yang dapat digunakan menjadi belum diketahui sampai keputusan independen tersedia.
4. Pemeriksa dengan izin persetujuan pengadaan dan MFA terbaru memuat usulan. Pemeriksa harus merupakan orang yang berbeda dari pengusul, termasuk ketika orang yang sama memiliki dua akun. Masukkan alasan, tinjau persetujuan atau penolakan, lalu simpan keputusan.
5. Muat ulang halaman untuk melihat keputusan tersimpan. Penarikan yang disetujui, penolakan dan usulan yang belum diputuskan tetap tidak menyatakan waktu kedatangan yang dapat digunakan.

Jadwal tidak menambah stok, menyatakan penerimaan fisik, mengubah harga, atau mencatat pembayaran. Sisa pasokan pada [rekomendasi pengadaan](replenishment.md) berasal dari jumlah PO yang disetujui dikurangi penerimaan fisik yang dapat dibuktikan; waktu kedatangan hanya dipakai setelah persetujuan independen. Jadwal yang hilang, terlambat atau tidak dapat dibuktikan tidak boleh dianggap sebagai pasokan nol yang telah diverifikasi.

## Respons hilang dan pemulihan

Sebelum mengirim, browser menyimpan referensi akun, PO, SKU, jenis aksi dan kunci asli di penyimpanan lokal. Referensi setiap operasi memiliki key terpisah agar dua tab tidak menimpa hasil satu sama lain. Isi konfirmasi pemasok, alasan, token dan kredensial tidak disimpan di jurnal ini.

Jika respons hilang, jangan mengirim usulan atau keputusan baru. Buka kembali browser dengan profil yang sama, masuk dengan akun asli, lalu pilih **Pulihkan hasil jadwal**. Pemulihan hanya mencari hasil kunci asli. Bila hasil belum terkonfirmasi, referensi tetap disimpan dan perubahan baru ditahan. Bila beberapa tab menghasilkan lebih dari satu referensi, pulihkan masing-masing sampai seluruh hasil diketahui. Penyimpanan lokal yang rusak atau tidak tersedia menahan perintah baru; hubungi operator untuk rekonsiliasi referensi, jangan menghapusnya sebelum hasil di server diketahui.

## Kontrak penyimpanan dan verifikasi

Migrasi109 menambah `purchaseArrivalHeads`, `purchaseArrivalEvents` dan `purchaseArrivalAudits`, setelah migrasi108. Tidak ada tanggal, orang, policy atau grant operasional yang disisipkan otomatis. Setiap perubahan menyimpan versi, snapshot sumber PO, identitas orang, audit dan receipt asli. Review usang, riwayat tidak lengkap, receipt persetujuan salah, akun tidak berizin dan sumber berubah ditolak. Penyiapan dan penerimaan migrasi mengikuti prosedur rilis serta backup/restore yang sudah ada.

Build backend109-5, full frontend4 (74 halaman), 12 pengujian client/jurnal dan tiga pengujian Mongo telah lulus. Browser109-2 juga lulus dalam sekitar 1 menit 37 detik: dua respons write hilang, pemulihan kunci asli, browser benar-benar ditutup dan dibuka kembali, pemeriksa independen, akses baca/lingkup serta tampilan desktop/mobile. Bukti ada pada `tmp/production/full-migration-results/purchase-arrival-browser109-2.trx`; dua screenshot dan hash dipertahankan di `tmp/production/preserved-eta109-2-screenshots/`.

Saat tab lain memulihkan referensi, pratinjau di tab lama dibatalkan dan pilihan lokasi kembali tersedia setelah tidak ada operasi tertunda. Petugas memuat jadwal terkini sebelum meneruskan. Kedua keadaan ini diperiksa oleh browser109-2. Panduan ini belum merupakan penerimaan staging/produksi. Status terminal dan kekurangan tetap dicatat pada [checklist97](../quality/production-progress.md).
