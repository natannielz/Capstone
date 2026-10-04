# Pemeriksaan kelayakan barang yang masih ditahan

Status implementasi: terhubung dan terverifikasi secara lokal melalui browser, API dan MongoDB dengan kebijakan serta identitas sintetis. Catatan hasil pemeriksaan ada pada [ledger produksi](../quality/production-progress.md). Alur ini belum diterima untuk staging maupun produksi.

Gunakan ruang kerja **Penerimaan mutu barang** untuk mencatat bagian barang dari satu penerimaan yang telah dipisahkan dan diperiksa secara fisik. Persetujuan menghasilkan status **Disetujui — tetap ditahan**. Kuantitas tersedia, reservasi, nilai persediaan dan jurnal tidak berubah. Pelepasan menjadi stok tersedia tetap memerlukan proses tersendiri dengan sumber stok yang berwenang dan nilai yang telah disetujui.

## Sebelum pemeriksaan

- Pilih penerimaan yang benar melalui daftar lokasi dan penerimaan. Cocokkan pesanan, barang, satuan kemasan, lot dan jumlah yang ditahan dengan barang fisik.
- Gunakan kebijakan pemeriksaan yang masih berlaku dan telah disetujui. Kebijakan mengatur prosedur mutu, putaway, batas usia pengamatan dan penggunaan alasan tanpa kedaluwarsa.
- Pengaju dan pemeriksa harus merupakan dua orang terverifikasi yang berbeda dengan kewenangan pada lokasi tersebut. Tindakan pengajuan dan keputusan memerlukan verifikasi masuk terbaru. Hak melihat riwayat tidak memberikan hak menyetujui.

## Pengajuan dan keputusan

1. Pisahkan barang layak dari barang bermasalah. Masukkan jumlah yang benar-benar diperiksa, waktu pengamatan, referensi pemeriksaan, identitas pemisahan barang dan lokasi putaway. Jumlah kerusakan, kedaluwarsa dan kemasan terbuka pada pemeriksaan awal dapat saling tumpang tindih; jangan menjumlahkan atau menguranginya untuk menebak jumlah layak.
2. Cocokkan lot serta kondisi kemasan dengan definisi barang yang tercatat. Pemeriksaan tidak boleh mengubah kemasan tersegel menjadi satuan lepas. Barang bertanggal harus memakai tanggal kedaluwarsa asli yang masih berlaku. Untuk barang tanpa tanggal, kebijakan harus mengizinkan kondisi tersebut dan alasannya harus dicatat; jangan membuat tanggal perkiraan.
3. Periksa ringkasan, kebijakan, jumlah yang telah disetujui dan sisa jumlah sebelum mengajukan. Perubahan sumber atau jumlah setelah peninjauan mengharuskan peninjauan ulang.
4. Pemeriksa lain mengamati barang secara langsung dan memasukkan hasilnya sendiri. Persetujuan mensyaratkan kesesuaian jumlah, lot, kondisi, pemisahan, putaway dan informasi kedaluwarsa. Referensi pemeriksaan masing-masing tetap dicatat. Penolakan memerlukan alasan dan tidak menggunakan kapasitas penerimaan.
5. Setelah disetujui, barang tetap ditahan. Riwayat menunjukkan keputusan dan bagian kuantitas yang sudah memiliki persetujuan. Jangan menganggap persetujuan ini sebagai izin penjualan, pembukuan biaya atau aktivasi sumber stok.

## Sambungan terputus atau riwayat tidak cocok

Jika hasil pengiriman tidak diketahui, gunakan pemeriksaan hasil operasi dengan kunci semula. Jangan mengirim ulang keputusan dengan kunci baru. Perubahan akun harus diselesaikan sebelum memeriksa operasi milik akun sebelumnya.

Penolakan tetap dapat menutup pengajuan yang pengamatan atau kebijakannya sudah kedaluwarsa, maupun setelah izin pengaju dicabut. Identitas asli dan bukti orang terverifikasi tetap harus dapat dibuktikan. Penanganan pengajuan milik identitas yang sudah dinonaktifkan masih menunggu alur siklus akun dan penugasan ulang pada M5; jangan mengganti identitas pengaju pada catatan lama.

Jika penerimaan, kebijakan atau riwayat persetujuan tidak dapat dibuktikan, hentikan keputusan pada penerimaan tersebut dan periksa catatan sumber bersama petugas berwenang. Jangan menghapus keputusan, alokasi atau penanda kapasitas untuk membuka kembali jumlah yang telah digunakan. Pemeriksaan lokal dengan orang dan kebijakan sintetis tidak menggantikan persetujuan operasional nyata.
