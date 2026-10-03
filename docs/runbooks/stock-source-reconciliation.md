# Rekonsiliasi jumlah stok

Status: alur browser→API→Mongo dan peningkatan skema telah diperiksa secara lokal dengan identitas, kebijakan dan pengamatan sintetis. Staging dan produksi belum diterima. Lihat [ledger kemajuan](../quality/production-progress.md) untuk bukti dan batas pemeriksaannya.

Halaman `/inventory/source-reconciliations` membandingkan pengamatan fisik manual dengan seluruh saldo yang dapat dibuktikan pada satu alokasi stok. Jalur awal ini melayani alokasi yang dihentikan, memakai buku stok Unit Toko dan mempunyai pengaturan serta kebijakan operasional yang telah disetujui. Referensi pengamatan diisi oleh petugas; referensi tersebut bukan balasan dari OMI, S-Mart atau penyedia eksternal.

Jalur awal mempunyai batas teknis 1.000 baris saldo/penerimaan per alokasi. Jika cakupan lebih besar atau riwayat pergerakan belum didukung, hasil tidak tersedia. Sistem tidak memotong daftar lalu menyatakan seluruh alokasi cocok; dukungan rekonsiliasi bertahap untuk cakupan yang lebih besar tetap diperlukan.

## Akses dan satuan

Hak membaca, mengusulkan dan memutuskan rekonsiliasi diberikan terpisah: `stock.source.reconciliation.read`, `.propose` dan `.decide`. Tindakan memerlukan MFA dalam lima menit terakhir dan identitas orang yang terverifikasi. Pemeriksa harus orang yang berbeda dari pengusul, termasuk ketika satu orang mempunyai beberapa akun. Akses mencakup pemilik alokasi dan seluruh kanal yang terkait dengan riwayat pemetaannya.

Pemilihan sumber, dasar perbandingan dan riwayat memerlukan hak membaca. Untuk membuat usulan atau keputusan melalui layar, gabungkan hak tersebut dengan hak tindakan yang sesuai. Akun yang hanya mempunyai hak tindakan tetap dapat memeriksa hasil operasinya sendiri menggunakan kunci asli, tanpa memperoleh akses ke riwayat umum.

Hitung **kemasan dari definisi yang ditampilkan** untuk setiap saldo/lot. Nama kemasan, isi, satuan dasar dan kedaluwarsa adalah acuan; jangan mengubah dus menjadi satuan berdasarkan nama barang saja. Stok ditahan tetap termasuk jumlah fisik. Angka ditahan, dipesan dan kedaluwarsa tidak ditambahkan lagi ke jumlah fisik.

## Pengamatan dan pemeriksaan

1. Pilih alokasi dan muat dasar perbandingan terkini. Periksa lokasi, pengaturan, kebijakan, prosedur rekonsiliasi, kemasan dan lot. Saldo yang tidak dapat dibuktikan membuat dasar perbandingan tidak tersedia; catatan yang hilang tidak dianggap sebagai saldo nol.
2. Masukkan waktu pengamatan fisik yang sebenarnya, referensi sumber, jumlah kemasan untuk setiap baris dan alasan. Nol harus diisi secara eksplisit. Seluruh baris wajib dicakup.
3. Periksa hasil dan selisih sebelum menyimpan usulan. Selisih tetap menjadi fakta yang memerlukan tindak lanjut; usulan tidak mengubah stok atau mencatat penyesuaian.
4. Pemeriksa memuat usulan, melakukan pemeriksaan sendiri dan mengisi waktu, referensi serta jumlah pada isian yang kosong. Angka pengusul bukan isian otomatis untuk pemeriksa.
5. Konfirmasi hanya dapat dilakukan jika kedua pengamatan cocok dengan seluruh dasar perbandingan yang masih berlaku. Jika ada selisih, sumber berubah atau pengamatan telah kedaluwarsa, jangan menyatakan rekonsiliasi cocok. Penolakan memerlukan alasan; pengamatan baru memerlukan peninjauan dasar perbandingan terbaru.

Waktu konfirmasi tidak memperbarui waktu pengamatan pengusul. Batas usia berasal dari pengaturan sumber yang disetujui; data sintetis pemeriksaan tidak menetapkan kebijakan produksi. Perubahan stok, pemetaan atau kebijakan setelah tinjauan membuat tindakan lama tidak berlaku.

## Hasil yang belum pasti

Jika balasan hilang, gunakan pemulihan hasil dengan akun, jenis operasi dan kunci asli. Jangan membuat kunci pengganti atau mengirim ulang tindakan untuk mencoba memastikan hasil. Riwayat dan pemulihan tunduk pada kewenangan akun saat ini.

## Batas hasil dan tindak lanjut

Konfirmasi menyimpan hasil pemeriksaan jumlah dan jejaknya. Konfirmasi tidak mengaktifkan alokasi, melepas karantina, membuat stok tersedia, menetapkan biaya, membuat jurnal atau menyelesaikan pengambilalihan kewenangan pencatatan. Saldo yang diterima tetap ditahan sampai ada keputusan fisik melalui alur yang sesuai.

Rekonsiliasi nilai, sumber eksternal, impor/ekspor dan balasan sumber, stok awal, penyesuaian, konversi, pelepasan serta pemeriksaan kewajiban tujuh bidang tetap mempunyai pekerjaan dan penerimaan tersendiri. Konfirmasi jumlah manual bukan penerimaan operasional terhadap seluruh alur tersebut.

## Deployment

Migrasi baru harus diterapkan melalui proses migrasi resmi dengan penghentian penulis dan cadangan terkoordinasi. Jangan mengedit migrasi yang sudah diterapkan, memasukkan hasil rekonsiliasi buatan atau mengaktifkan alokasi langsung di database. Pemeriksaan peningkatan versi, pelestarian penerimaan/penilaian sebelumnya, pemulihan cadangan dan peran database produksi harus mempunyai bukti masing-masing sebelum cutover.

Migrasi 74 menambahkan `stockSourceReconciliationProposals` dan `stockSourceReconciliationDecisions`. Preflight memeriksa riwayat penerimaan dan penilaian yang dipertahankan, catatan rekonsiliasi parsial, batas penghitung dan keunikan sebelum mengubah skema tujuan. Validasi yang lebih kuat dipertahankan. Pengulangan migrasi menggunakan lease resmi; jangan menghapus riwayat versi untuk memaksakan pengulangan pada data nyata.
