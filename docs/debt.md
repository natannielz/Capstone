# Utang teknis

- Rehearsal `verify-catalog-identifier-browser.mjs:246`, `verify-stock-release-browser.mjs:116` dan `verify-accounting-books-browser.mjs:67` masih menunggu judul "Masuk untuk melanjutkan" di halaman. Sejak G-012, proxy mengalihkan pengunjung tanpa sesi ke `/identity/login?returnPath=…`, jadi langkah itu akan timeout. Perbaikannya sama dengan `verify-stock-count-browser.mjs` (ea6a958e). Ini mengubah tes, jadi perlu izin pemilik dulu.
