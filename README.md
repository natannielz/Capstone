# Unit Toko — Demo Capstone BNI

Website demo untuk pemesanan divisi, persediaan, pengadaan, pengiriman, penerimaan, penagihan, pembayaran simulasi, laporan, dan tutup periode.

- Website: [unit-toko-bni.vercel.app](https://unit-toko-bni.vercel.app)
- Versi aplikasi yang selesai: **v6**, diverifikasi pada 17 September 2026. Lihat [catatan rilis dan hasil pengujian](docs/rilis-v6.md).
- Tahap berikutnya: **v7 — etalase dan akun pelanggan**, masih direncanakan. Lihat [rencana implementasi](docs/plan-customer-marketplace-v7.md).

## Struktur

| Direktori | Isi |
| --- | --- |
| `website/` | Aplikasi Next.js/React/TypeScript, komponen, aturan bisnis, migrasi libSQL, aset dan tes |
| `docs/` | Rencana, catatan implementasi, hasil validasi, dan panduan publikasi |

## Menjalankan lokal

Gunakan Node.js 24. Dari direktori repositori:

```powershell
cd website
npm ci
Copy-Item .env.example .env.local
```

Isi `DEMO_PASSWORD_SEED` pada `.env.local` dengan nilai acak privat minimal 32 karakter. Untuk pengembangan lokal, gunakan `TURSO_DATABASE_URL=file:.data/unit-toko.db` dari contoh konfigurasi. Lalu:

```powershell
npm run demo:accounts
npm run dev
```

Akun awal disimpan privat di `website/.data/demo-accounts.json`. Database dan berkas unggahan lokal berada di `.data/` dan tidak masuk Git. Skema serta data demo dibuat otomatis pada akses pertama. [Panduan aplikasi](website/README.md) menjelaskan peran dan alur presentasi.

## Pemeriksaan

Jalankan dari `website/`:

```powershell
npm run typecheck
npm run lint
npm test
npm run build
```

`npm run test:api` memerlukan server lokal dan menambahkan skenario pada database demo lokal. Gunakan database terisolasi untuk pengujian mutasi. Hasil v6 yang dicatat sebelumnya: 86 tes regresi, 65 pemeriksaan HTTP lokal, dan 73 pemeriksaan publik lulus; satu pemeriksaan publik dilewati. Rincian, tanggal, serta keterbatasannya tersedia dalam [catatan rilis](docs/rilis-v6.md).

## Deployment

Untuk Vercel, pilih root directory **`website`**, build `npm run build`, dan Node.js 24. Aplikasi memakai Turso remote dan Vercel Blob privat; konfigurasi lokal tidak menjadi penyimpanan production. Lihat [panduan publikasi](docs/publikasi-github.md).

## Cakupan demo

Data orang, foto ilustrasi, produk, transaksi, pembayaran, dan jurnal bersifat simulasi. Aplikasi tidak melakukan transfer bank atau integrasi operasional resmi BNI. Rilis v6 memiliki 10 akun untuk 9 peran, dengan pemeriksaan akses pada server. Akun customer tambahan baru termasuk rencana v7.

Arsip studi kasus, kredensial, database, unggahan privat, dan log tidak disertakan dalam repositori ini.
