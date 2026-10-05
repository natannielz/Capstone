# Pemrosesan seluruh halaman PDF privat

Dokumen pemasok mempertahankan format yang disimpan saat unggahan. Dokumen pemasok PDF menggunakan jalur unggahan PDF, validasi PDF tersendiri, dan pekerjaan OCR tersendiri. Unggahan gambar, hasil OCR gambar, dan izin keuangan tetap memakai pemilik yang sudah ada.

Bukti pembayaran utama tetap wajib berupa gambar yang memenuhi syarat (P01). PDF tidak dapat menggantikan gambar tersebut. Integrasi lampiran PDF pembayaran sedang menjalani validasi lokal; jangan mengaktifkannya pada lingkungan nyata sebelum penerimaan selesai. Lampiran menggunakan identitas, riwayat, reservasi, pin, dan pekerjaan tersendiri yang terikat pada pengajuan gambar utama yang tepat.

Worker membaca dokumen asli yang tepat melalui pegangan berkas privat yang telah diverifikasi. PDF dirender dengan runtime PDFium yang dipin; setiap halaman diproses dengan runtime Tesseract yang sama dengan OCR gambar. Pin kedua runtime ditahan selama seluruh komputasi. Hasil menyimpan metadata halaman, teks, koordinat halaman asli termasuk rotasi, prapemrosesan, kandidat bidang per halaman, dan alasan peninjauan. Gambar halaman tidak dipublikasikan sebagai bukti baru.

Tidak ada nilai pemenang otomatis antar halaman. Nilai bertentangan atau tidak diketahui tetap terlihat untuk pemeriksa. Hasil OCR tidak menyetujui pembayaran, penerimaan pemasok, stok, atau pencatatan jurnal.

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
