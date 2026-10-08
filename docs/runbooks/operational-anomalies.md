# Tinjauan operasional

Halaman `/operations/anomalies` membantu petugas meninjau harga, jumlah pesanan, dan pengembalian dana. Akses memerlukan `operations.anomaly.read` serta izin membaca sumber pada toko yang sama. Catatan tindak lanjut memerlukan `operations.anomaly.review`; konfigurasi kebijakan memerlukan `operations.anomaly.policy.configure`. Dua tindakan terakhir memerlukan verifikasi tambahan yang masih berlaku. Sumber pengembalian dana juga mengikuti izin dan verifikasi tambahan pemilik alur keuangan.

Status penerimaan mengikuti [checklist utama](../quality/production-progress.md), baris M07. Bagian ini masih parsial: sumber penyesuaian stok beserta pembanding yang disetujui belum tersedia, dan batas bisnis produksi tidak diisi otomatis. Hasil hitung fisik bukan pengganti sumber penyesuaian stok.

## Membuat pemeriksaan dan mencatat tindak lanjut

1. Pilih **Jenis sumber** dan **Sumber yang diizinkan**. Gunakan **Sumber berikutnya** atau **Sumber sebelumnya** untuk berpindah halaman sumber; hanya sumber dalam cakupan akses saat ini yang ditampilkan.
2. Isi alasan, lalu pilih **Buat pemeriksaan baru**. Hasil menyimpan sumber, versi aturan, versi kebijakan, waktu, nilai yang diperiksa, pembanding, dan penjelasan setiap tanda.
3. Tinjau sumber sebenarnya sebelum memilih hasil tinjauan dan **Simpan tinjauan**. Alasan serta petugas tercatat bersama hasil. Jika sumber atau kebijakan berubah, buat pemeriksaan baru sebelum melanjutkan.

Pemeriksaan harga membandingkan subtotal dengan harga paket dikalikan jumlah. Pemeriksaan pengembalian dana membandingkan jumlah yang tercatat dengan jumlah yang telah disetujui. Tanda batas harga, jumlah paket, dan pengembalian dana memakai kebijakan yang dicatat secara eksplisit. Nilai tepat pada batas tidak ditandai sebagai melebihi batas.

**Kebijakan belum tersedia** berarti kewajaran terhadap batas tersebut belum dapat disimpulkan, bukan berarti angka aman atau nol. Mata uang yang berbeda tidak dibandingkan menggunakan batas mata uang lain. **Sumber belum tersedia** pada penyesuaian stok menyatakan keterbatasan implementasi. Pemeriksaan dan tindak lanjut tidak menyetujui pesanan, mengubah stok, menyelesaikan kasus, atau memindahkan uang.

## Mencatat kebijakan yang telah ditetapkan

Petugas yang diberi izin konfigurasi dapat memakai bagian **Kebijakan tinjauan** pada sumber yang dipilih. Isi versi, referensi penetapan, alasan, serta hanya batas yang sudah ditetapkan oleh penanggung jawab untuk toko itu. Kolom yang belum diketahui tetap kosong. Batas uang memerlukan kode mata uang yang sesuai; jumlah paket memakai satuan kemasan pada sumber.

Pilih **Tinjau kebijakan**, periksa kembali sumber, versi, satuan, dan alasan, lalu konfirmasi penyimpanan. Server memeriksa revisi kebijakan saat ini, izin, dan verifikasi tambahan; perubahan yang bersamaan tidak ditimpa. Versi kebijakan dan bukti permintaan aslinya disimpan sebagai catatan yang tidak ditulis ulang. Setelah kebijakan tersimpan, buat pemeriksaan baru untuk memakai versi tersebut. Migration 107 menyediakan penyimpanan tinjauan tanpa membuat batas, kebijakan, atau izin bawaan.

## Pemulihan setelah balasan hilang

Sebelum mengirim konfigurasi kebijakan, pemeriksaan, atau tindak lanjut, browser menyimpan referensi permintaan asli dalam penyimpanan lokal. Referensi berisi akun, jenis operasi, sumber, kunci asli, waktu, dan bila diperlukan nomor pemeriksaan atau revisi yang diharapkan. Alasan, batas kebijakan, hasil pemeriksaan, dan isi formulir tidak disimpan di sana.

Jika hasil pengiriman belum pasti, buka kembali halaman dengan akun pemilik yang sama dan gunakan tombol **Periksa hasil asli kebijakan**, **Periksa hasil asli pemeriksaan**, atau **Periksa hasil asli tinjauan**. Pemulihan tersedia tanpa harus memilih ulang sumber. Menutup dan membuka kembali browser tetap mempertahankan referensi selama data situs tidak dihapus.

Setiap operasi memiliki entri sendiri. Dua tab tidak saling menimpa kunci; pemulihan satu hasil hanya menghapus referensi hasil itu. Referensi lain tetap ditampilkan dan harus diselesaikan sebelum pengiriman baru. Setelah tab lain menyelesaikan pemulihan, tampilan hasil atau rancangan lama dibatalkan agar tidak dianggap masih terkini. Referensi milik akun lain tidak dibuka.

Hasil belum ditemukan, penolakan sesi atau akses, perubahan sumber, serta kegagalan jaringan saat pemulihan tetap mempertahankan referensi asli. Membaca hasil terbaru bukan bukti bahwa permintaan asli sudah berhasil. Jangan membuat kunci pengganti atau menghapus data situs untuk melewati keadaan ini. Jika penyimpanan browser rusak atau tidak dapat diakses, pengiriman ditahan.

## Bukti pemeriksaan lokal

Pada backend 109 build 5 dan frontend full4 `aWI7PQZXT78AFix9dvLf4`, pengujian pemulihan asli lulus untuk akun, cakupan akses terkini, referensi tanpa bukti lengkap, serta perubahan catatan atau bukti transaksi. Browser nyata lulus dengan tiga pembukaan ulang browser, empat balasan hilang setelah server benar-benar menyimpan, dan dua tab dengan referensi terpisah. Pemulihan tidak menggandakan catatan atau mengubah efek bisnis. Batas waktu dan pemeriksaan penolakan tetap dipertahankan.

Bukti lokal: `tmp/production/full-migration-results/m07-durable-recovery-native109-5.trx`, `tmp/production/full-migration-results/m07-durable-browser109-5.trx`, dan tangkapan layar beserta hash di `tmp/production/preserved-m07-durable109-5-screenshots/`. Pengujian memakai sumber dan kebijakan sintetis melalui pemilik alur yang sebenarnya; hasil ini tidak menetapkan batas bisnis produksi dan tidak menutup kekurangan penyesuaian stok pada M07.
