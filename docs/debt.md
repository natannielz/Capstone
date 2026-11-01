# Utang teknis

- F-007: keuangan-tagihan melihat banner "Akses untuk tindakan ini tidak tersedia" di halaman pemeriksaan pembayaran karena frontend memanggil `workflow/query` tanpa `payment.review.manage`. Perbaikan: frontend tidak memanggil query itu untuk peran tanpa izin manage.

