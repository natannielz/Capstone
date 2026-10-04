# Unggahan bukti PDF

Status bagian ini: jalur API, MongoDB, penyimpanan privat dan renderer terpin sudah terhubung serta diverifikasi secara lokal, termasuk HTTP nyata. Pemeriksaan native memakai pemindai loopback sintetis yang dinyatakan jelas; penerimaan ClamAV, host produksi dan operasi nyata belum dibuktikan. Antarmuka unggahan PDF dan OCR bisnis seluruh halaman masih perlu dihubungkan.

PDF pembayaran dan dokumen pemasok memakai jalur unggahan tersendiri. Nomor reservasi, tujuan bukti, kunci pemulihan, pemilik transaksi, cabang, dan versi sumber tersimpan di MongoDB. Mengirim MIME PDF ke jalur gambar tidak mengubah tujuan unggahannya.

Pemeriksaan keamanan PDF membaca berkas asli privat yang sudah diverifikasi. Pemindai lokal dan renderer PDF yang dikunci ke versi serta hash konfigurasi memeriksa seluruh halaman dalam batas yang ditentukan. PDF terenkripsi, rusak, berisi fitur yang tidak didukung, atau melebihi batas tidak memperoleh akses baca. Kegagalan runtime tidak menjadi hasil lolos dan tidak menghasilkan bukti halaman parsial.

Setelah pemeriksaan lengkap lolos, pemilik yang masih berwenang dapat membaca berkas PDF asli melalui unduhan privat. Bukti pemeriksaan, audit, penerimaan pekerjaan, persetujuan pemroses, versi sumber dan izin pengguna diperiksa kembali. Tautan penyimpanan publik tidak tersedia.

**Batas saat ini:** pemeriksaan keamanan bukan pengajuan pembayaran, peninjauan faktur pemasok, hasil OCR bisnis, persetujuan uang, atau perubahan stok. Pengajuan PDF ke pemrosesan gambar tetap ditolak dengan kode `evidence_pdf_processing_not_connected`. Pengolahan dan peninjauan seluruh halaman perlu dihubungkan ke pemilik bisnis tersendiri sebelum batas ini dibuka.

## Sambungan aplikasi

Hanya profil aplikasi `full` yang memetakan jalur ini. Profil `release1` tetap menyediakan katalog dan permintaan pesanan cabang.

- Pembayaran: `/api/v1/billing/payment-declarations/{id}/pdf-evidence`.
- Dokumen pemasok: `/api/v1/procurement/supplier-documents/drafts/{id}/pdf-evidence`.
- Reservasi, daftar, status, pemulihan dan byte asli memakai batas sesi, Origin, CSRF, aktor yang diharapkan, serta versi yang sama dengan unggahan privat yang sudah ada.
- Byte unggahan memakai `application/octet-stream`; format sebenarnya ditentukan oleh tujuan tersimpan dan pemeriksaan berkas asli.
- Unduhan memakai `application/pdf`, nama berakhiran `.pdf`, `attachment`, `no-store`, `nosniff`, dan sandbox; pemrosesan Range tidak tersedia.

## Konfigurasi operator

Worker membaca bagian opsional `Evidence:PdfValidation`. Jika tidak dikonfigurasi, worker tidak mengambil pekerjaan PDF. Bagian yang dikonfigurasi harus lengkap dan tidak boleh mengandung kunci yang tidak dikenal.

Konfigurasi memerlukan root penyimpanan privat Windows, identitas store, SID operator tepercaya, consumer, referensi penerimaan host dan konfigurasi ClamAV, konfigurasi scanner, serta konfigurasi renderer. Renderer memerlukan jalur Python/helper/package/Pillow/wheel, versi Pillow, daftar pin file dengan hash, batas halaman/pixel/output/memori, dan timeout. Pin serta referensi penerimaan berasal dari operator yang menyetujui rilis; runtime tidak menyetujui file yang terpasang dengan menghitung hash sendiri.

MongoDB harus memakai migrasi M83 dan M85 beserta urutan predecessor yang sah. Persetujuan consumer/pipeline/store PDF tersendiri harus aktif dan belum kedaluwarsa. Menyalakan worker atau menjalankan migrasi tidak membuat persetujuan ini secara otomatis.

M85 menambah tujuan PDF dan identitas operasi melalui validator baru. Migrasi memeriksa definisi, indeks, data dan riwayat yang dipertahankan sebelum perubahan pertama; tidak mengubah bukti gambar/video lama menjadi PDF.
