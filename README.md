# Unit Toko — Demo Capstone BNI

Website demo dengan etalase pelanggan dan pemesanan divisi, persediaan, pengadaan, pengiriman, penerimaan, penagihan, pembayaran simulasi, laporan, dan tutup periode.

- Website: [unit-toko-bni.vercel.app](https://unit-toko-bni.vercel.app)
- **v7 — etalase dan akun pelanggan**, dengan katalog publik, keranjang, checkout, pesanan pribadi, dan profil. Lihat [catatan rilis](docs/rilis-v7.md), [rencana dan cakupan](docs/plan-customer-marketplace-v7.md), serta [hasil pengujian customer](docs/testing-customer-v7.md).
- Alur staf dan divisi v6 tetap tersedia. [Catatan rilis v6](docs/rilis-v6.md) menjadi baseline regresi.

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

`npm run test:api` memerlukan server lokal dan menambahkan skenario pada database demo lokal. Gunakan database terisolasi untuk pengujian mutasi. Rilis v7 lulus **154 tes regresi** dan **22 kelompok pemeriksaan HTTP customer**; TypeScript dan build lulus, lint tidak memiliki error dengan 9 warning yang dicatat. Reproduksi dan batas pemeriksaan tersedia pada [hasil pengujian customer](docs/testing-customer-v7.md); hasil produksi tercatat pada [catatan rilis v7](docs/rilis-v7.md).

## Deployment

Untuk Vercel, pilih root directory **`website`**, build `npm run build`, dan Node.js 24. Aplikasi memakai Turso remote dan Vercel Blob privat; konfigurasi lokal tidak menjadi penyimpanan production. Lihat [panduan publikasi](docs/publikasi-github.md).

## Cakupan demo

Data orang, foto ilustrasi, produk, transaksi, pembayaran, dan jurnal bersifat simulasi. Aplikasi tidak melakukan transfer bank atau integrasi operasional resmi BNI. Rilis v7 memiliki 11 akun untuk 10 peran, termasuk pelanggan demo khusus dengan email `customer@unit-toko.demo`. Hak akses dan kepemilikan transaksi diperiksa di server. Password dibuat dari seed privat; tidak disertakan dalam repositori.

Arsip studi kasus, kredensial, database, unggahan privat, dan log tidak disertakan dalam repositori ini.
