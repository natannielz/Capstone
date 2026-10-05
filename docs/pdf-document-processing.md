# Pemrosesan seluruh halaman PDF privat

Dokumen pemasok mempertahankan format yang disimpan saat unggahan. Dokumen pemasok PDF menggunakan jalur unggahan PDF, validasi PDF tersendiri, dan pekerjaan OCR tersendiri. Unggahan gambar, hasil OCR gambar, dan izin keuangan tetap memakai pemilik yang sudah ada.

Bukti pembayaran utama tetap wajib berupa gambar yang memenuhi syarat (P01). PDF tidak dapat menggantikan gambar tersebut. Integrasi lampiran PDF pembayaran sedang menjalani validasi lokal; jangan mengaktifkannya pada lingkungan nyata sebelum penerimaan selesai. Lampiran menggunakan identitas, riwayat, reservasi, pin, dan pekerjaan tersendiri yang terikat pada pengajuan gambar utama yang tepat.

Worker membaca dokumen asli yang tepat melalui pegangan berkas privat yang telah diverifikasi. PDF dirender dengan runtime PDFium yang dipin; setiap halaman diproses dengan runtime Tesseract yang sama dengan OCR gambar. Pin kedua runtime ditahan selama seluruh komputasi. Hasil menyimpan metadata halaman, teks, koordinat halaman asli termasuk rotasi, prapemrosesan, kandidat bidang per halaman, dan alasan peninjauan. Gambar halaman tidak dipublikasikan sebagai bukti baru.

Tidak ada nilai pemenang otomatis antar halaman. Nilai bertentangan atau tidak diketahui tetap terlihat untuk pemeriksa. Hasil OCR tidak menyetujui pembayaran, penerimaan pemasok, stok, atau pencatatan jurnal.

## Referensi halaman pada koreksi pemeriksa (M88)

Pemeriksa dapat memilih pengamatan dari halaman lampiran PDF, menyatakan bahwa asli telah ditinjau, lalu menyimpan referensinya bersama perubahan bidang yang dimasukkan secara manual. Referensi ini opsional dan menyimpan lampiran, hasil/hash OCR, halaman, serta bidang yang tepat. Referensi saja tidak membuat koreksi; koreksi tetap memerlukan perubahan manual dan pengikatan pada gambar pembayaran utama beserta hasil OCR gambarnya. PDF tidak mengisi nilai secara otomatis, mengganti pin gambar utama, memverifikasi bank, atau mengubah uang maupun stok.

Penyimpanan memeriksa sumber dan asli yang telah divalidasi dan dipin, hasil seluruh halaman, izin pemeriksa saat ini, independensi orang, serta persetujuan D06 yang terpisah. Batas waktu asli dan persetujuan tetap berlaku sampai transaksi selesai dan tidak diperpanjang ketika mencoba ulang. Satu audit baca OCR yang tepat disimpan dalam transaksi yang sama dengan koreksi, riwayat, dan receipt; kegagalan audit membatalkan semuanya. Pemeriksaan kandidat memakai pengikatan asli yang sah tanpa membaca ulang berkas asli saat menyimpan koreksi. Koreksi lama tanpa referensi PDF tetap mempertahankan format dan pengikatan gambar yang sama.

M88 terhubung dan diverifikasi secara lokal melalui browser, API .NET, MongoDB serta runtime OCR terpin. Riwayat koreksi manual dan referensi halaman bertahan setelah muat ulang; balasan tersimpan yang sengaja diputus dipulihkan tanpa pengiriman ulang. Pengujian izin, masa berlaku, pembatalan audit, format HTTP dan preservasi migrasi terhubung dalam ledger. Data pengujian sintetis serta verifikasi lokal ini belum merupakan penerimaan staging atau produksi.

## Referensi halaman pada pencocokan dokumen pemasok (M89)

Petugas dapat meninjau PDF asli dan memilih kandidat bidang dari halaman tertentu sebagai referensi opsional di samping alokasi pencocokan yang dimasukkan manual. `pdfObservations` menyimpan hasil/hash OCR, referensi/hash bukti, halaman, bidang, dan pernyataan pemeriksaan asli. Nilai PDF tidak mengisi jumlah kemasan, nominal, atau pilihan sumber secara otomatis. Dokumen PDF tetap memakai `ocrResultId` gambar yang kosong; jalur gambar dan pencocokan lama tanpa referensi tetap dipertahankan. Pencocokan ini tidak melepaskan stok yang ditahan, membayar pemasok, atau membuat jurnal.

Pratinjau dan penyimpanan memeriksa lingkup dan izin saat ini, orang yang terverifikasi, asli privat dan pin yang tepat, hasil seluruh halaman serta kandidat yang benar. Audit baca kandidat dan pencocokan disimpan dalam transaksi yang sama; kegagalan audit membatalkan keduanya. Tenggat baca yang ditangkap tetap berlaku hingga pemeriksaan akhir. Penggunaan ulang bukti kualitas hanya berlaku dalam percobaan transaksi yang sama dan tetap memeriksa baris induk PO/permintaan yang autentik, pemilik inspeksi privat, kepala kapasitas dan saldo saat ini.

M89 terhubung dan diverifikasi lokal melalui build produksi Next.js, HTTPS API .NET, MongoDB dan runtime PDF/OCR terpin. Browser membuktikan unggahan dan pemrosesan seluruh halaman, pembacaan asli privat, pemilihan halaman kedua, alokasi manual, pratinjau, penyimpanan, pemulihan satu commit dengan kunci asli setelah balasan diputus, serta riwayat setelah muat ulang pada desktop dan seluler. Pengujian native juga membuktikan pencocokan normal dan kualitas, penolakan sumber/halaman/hash/izin yang salah, masa berlaku, pembatalan audit, dan preservasi migrasi beserta riwayat lama.

Bukti tersimpan di `tmp/production/full-migration-results/`:

- `supplier-document-pdf-matching-integrated-release1.trx`: lima lulus, satu kegagalan awal browser tetap disimpan.
- `supplier-document-pdf-matching-retained-integrity-native-release1.trx`: lima lulus, satu kegagalan penyiapan pengujian M88 yang masih meminta versi 88 setelah database mencapai 89 tetap disimpan.
- `supplier-document-pdf-matching-corrected-browsers-and-m88-migration-release3.trx`: ketiga pengujian ulang lulus tanpa dilewati; mencakup migrasi M88, browser koreksi pembayaran M88 dan browser pencocokan pemasok M89. Pengujian ulang tidak mengubah status bukti awal yang gagal.

Seluruh penerimaan di atas memakai akun, persetujuan dan masukan sintetis lokal. Profil Release 1 tetap terbatas pada katalog dan permintaan pesanan cabang. Penerimaan staging/produksi, sambungan Auth0 dan layanan API/MongoDB/penyimpanan privat nyata, serta persetujuan operator dan kebijakan host/scanner/OCR yang berlaku masih harus dibuktikan; hasil lokal bukan izin untuk mengaktifkan layanan atau memigrasikan data nyata.

## Konfigurasi worker

Bagian opsional `Evidence:SupplierDocumentPdfOcr` mengaktifkan runtime khusus dokumen pemasok. Konfigurasi tidak menebak format atau sumber dari MIME, nama berkas, atau pekerjaan gambar. Bagian yang tidak diisi tidak mengklaim pekerjaan PDF pemasok. Lampiran pembayaran memiliki bagian terpisah `Evidence:PaymentSupplementPdfOcr` dengan kunci dan pin runtime yang sama di bawah ini; hanya pekerjaan `payment.supplement-document-ocr.requested` dapat memakai jalur tersebut. Konfigurasi lama `Evidence:PaymentPdfOcr` tetap ditolak saat startup worker. Menjalankan migrasi tidak mengaktifkan worker.

Bagian pemasok memerlukan persis:

- `Root`, `StoreId`, `TrustedOperatorSids`, `ConsumerId`.
- `HostAcceptanceReference`, `ModelSelectionReference`, `RasterAcceptanceReference`.
- `Raster`: `PythonExecutable`, `HelperPath`, `PackageRoot`, `PillowRoot`, `WheelPath`, `PillowVersion`, `Files`, `MaximumPages`, `MaximumPagePixels`, `MaximumTotalPixels`, `MaximumOutputBytes`, `MaximumMemoryBytes`, `Timeout`.
- `Engine`: `PythonExecutable`, `HelperPath`, `PackageRoot`, `PillowRoot`, `ModelRoot`, `ModelVersion`, `Files`, `Limits`, `MaximumMemoryBytes`, `Timeout`.

`Files` berupa daftar berurutan dengan `Path` dan `Sha256`. `Engine:Limits` memerlukan `MaximumBytes`, `MaximumWidth`, `MaximumHeight`, `MaximumPixels`. Nilai atau kunci tambahan dan daftar pin berlubang ditolak. Jalur privat dan hash runtime harus berasal dari inventaris runtime yang telah diterima operator; dokumen ini tidak memberikan inventaris atau persetujuan produksi.

Konfigurasi worker saja belum memberikan izin pemrosesan. Pemilik `pdfDocumentProcessingPolicies` harus berisi persetujuan terpisah yang masih aktif untuk konsumen, sumber, store, pipeline, dan provenance raster/OCR yang tepat, beserta ketiga referensi persetujuan. Persetujuan terbatas waktunya dan tidak dibuat otomatis oleh migrasi atau worker. Persetujuan penyimpanan, scanner dan validasi PDF, serta kebijakan baca asli juga tetap diperlukan.

Untuk lampiran pembayaran, kebijakan D06 pada `paymentSupplementPolicies` juga wajib disetujui secara eksplisit oleh Operations, Finance, dan pemilik data untuk unit organisasi/toko/channel yang tepat. Masa persetujuan paling lama 90 hari dan batas lampiran harus 1–64 sesuai keputusan operator. Tidak ada persetujuan bawaan. Pengunggah, penerima reservasi, worker, dan pemeriksa tidak dapat mengubah pengajuan gambar utama atau memakai ulang buktinya sebagai PDF. Pemeriksa tetap memerlukan izin pada lingkup saat ini dan orang yang independen; OCR lampiran tidak mengisi koreksi keuangan secara otomatis. Berakhirnya D06 menutup kemampuan baca asli/OCR saat ini, tetapi pemulihan metadata operasi tetap tersedia untuk pemilik yang masih berwenang.

## Batas integritas

Satu percobaan mempertahankan satu tenggat privat yang ditangkap dan disimpan, paling lama satu menit. Tenggat juga dibatasi klaim pekerjaan, durasi permintaan yang disetujui, usia bukti validasi, usia definisi scanner, dan masa persetujuan. Membaca ulang kebijakan atau mencoba ulang klaim yang sama tidak memperbarui tenggat yang sudah disimpan.

Worker memeriksa kembali sumber, asosiasi pengajuan, pin, publikasi asli, bukti validasi, kebijakan saat ini, dan klaim sebelum membaca byte dan sebelum menyimpan hasil. Hanya keberhasilan seluruh halaman yang dapat menyimpan hasil lengkap, audit yang cocok, dan receipt pekerjaan dalam transaksi yang sama. Penolakan audit membatalkan hasil dan receipt. Kegagalan halaman terakhir tidak menerbitkan halaman sebelumnya. Metadata kegagalan PDF yang terbatas disimpan terpisah; pekerjaan tetap memakai kode kegagalan umum yang aman. Setelah tenggat privat berakhir, worker tidak memulai jendela baru untuk menerbitkan kegagalan terlambat.

Hasil yang sudah tersimpan dapat dipulihkan tanpa membuka asli atau menjalankan OCR lagi hanya jika hasil, audit, receipt, dan riwayat sumber cocok. Pembacaan bisnis tetap memerlukan aktor, lingkup, revisi sumber, referensi/hash, dan kebijakan asli saat ini. Riwayat asli yang tidak berubah tetap dapat dicocokkan setelah persetujuan berakhir; kecocokan riwayat tidak memberikan izin baca saat ini.

Jalur ini hanya tersedia pada profil penuh. Profil Release 1 tetap berisi katalog dan permintaan pesanan cabang. Persetujuan dan scanner sintetis dalam pengujian lokal bukan penerimaan host atau penyedia produksi.
