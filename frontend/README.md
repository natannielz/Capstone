# Frontend Unit Toko

Next.js/TypeScript menampilkan katalog, permintaan dan workspace operasional. API bisnis serta persistence berada di C#/.NET 10 dan MongoDB; identitas produksi memakai Auth0. Baca [panduan proyek](../README.md), [status 97 persyaratan](../docs/quality/production-progress.md), dan [setup lokal](../docs/runbooks/local-development.md).

## Pengembangan dan pengujian

Gunakan Node.js 24 dan dependency yang dipatok pada lockfile. Dari direktori ini jalankan `npm run typecheck`, `npm run lint`, `npm test`, dan `npm run build` sesuai perubahan. Regresi historis masih memerlukan dependency `backend/legacy`; clean install tanpa seluruh dependency lama belum diterima. `test:api:legacy` adalah runner historis untuk fixture versi lama, bukan pengujian API .NET sekarang.

Profil `release1` memakai `.next`. Profil `full` memakai `.next-full` dan hanya digunakan untuk validasi terisolasi. Jangan menimpa build pilot yang sedang berjalan. Build dan typecheck dijalankan berurutan karena berkas tipe Next dipakai bersama. Ikuti [runbook pilot](../docs/runbooks/local-pilot-release1.md) untuk akun sintetis dan jalur privat kredensial; tidak ada password yang dipublikasikan di repositori.

## Batas aplikasi saat ini

Halaman UI terpelihara menggunakan klien `src/api` dan modul `src/modules`. Tujuh handler HTTP demo lama telah dilepas dari Next dan dipertahankan hanya sebagai fixture regresi. `/api/state` dan `/api/commands` bukan API bisnis yang aktif. Konfigurasi `UNIT_TOKO_API_ORIGIN` harus menunjuk ingress HTTPS yang disetujui; konfigurasi contoh atau frontend READY tidak membuktikan backend hosted bekerja.

[Panduan demo lama](../docs/archive/legacy-ui/frontend-demo-readme.md) menyimpan konteks fitur dan desain historis. Instruksi akun, database Turso dan deployment di dalamnya tidak berlaku untuk setup terpelihara. Branding teal/oranye, DM Sans/Archivo, imagery dan navigasi yang telah dipulihkan tetap menjadi acuan.
