# Utang teknis

- F-007: keuangan-tagihan melihat banner "Akses untuk tindakan ini tidak tersedia" di halaman pemeriksaan pembayaran karena frontend memanggil `workflow/query` tanpa `payment.review.manage`. Perbaikan: frontend tidak memanggil query itu untuk peran tanpa izin manage.

- Pembacaan halaman pemeriksaan pembayaran (panel verifikasi dan "Lihat bukti asli") kadang mendapat 409 `version_conflict` pada percobaan pertama, kemungkinan karena beberapa baca berlomba atas pengajuan yang sama. Golden A2 mengulang baca itu (paling banyak 10 dan 3 kali) tanpa melonggarkan batas waktu. Perbaikan: operasi baca tidak boleh menghasilkan 409; setelah itu hapus retry di `verify-golden-journey-browser.mjs` (A13a/A13b).
