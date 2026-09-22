# Unit Toko — Demo Capstone BNI

Website demo dengan etalase pelanggan dan pemesanan divisi, persediaan, pengadaan, pengiriman, penerimaan, penagihan, pembayaran simulasi, laporan, dan tutup periode.

- Website: [unit-toko-bni.vercel.app](https://unit-toko-bni.vercel.app)
- **v11 — pengalaman belanja dan audit proses**: etalase bergambar, komposisi baru beranda/login, animasi yang mengikuti preferensi gerakan, serta kontrol bukti penerimaan dan penutupan sisa pengadaan. Lihat [catatan rilis](docs/rilis-v11.md), [audit penjualan](docs/gap-sales-v11.md), dan [audit pengadaan](docs/gap-operations-v11.md).
- **v10 — 200 produk tambahan**, foto dan deskripsi unik, stok simulasi, serta katalog dengan pagination. Rilis ini juga menyertakan logo U–T baru, animasi landing/login, dan penyelesaian alur akun, stok, serta keuangan dari v9. Lihat [catatan rilis](docs/rilis-v10.md) dan [data katalog](docs/data-dummy-v10.md).
- **v8 — beranda dan akses terpisah**: landing page publik, [toko pelanggan](https://unit-toko-bni.vercel.app/shop), [login pelanggan](https://unit-toko-bni.vercel.app/customer/login), dan [portal divisi/staf](https://unit-toko-bni.vercel.app/staff/login). Logo UT, warna, kontras, dan tampilan responsif diperbarui. Lihat [catatan rilis](docs/rilis-v8.md).
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

`npm run test:api` memerlukan server lokal dan menambahkan skenario pada database demo lokal. Gunakan database terisolasi untuk pengujian mutasi. Validasi v11 lulus **224 tes regresi**, **14 kelompok pemeriksaan HTTP pemisahan akses**, dan **22 kelompok pemeriksaan HTTP customer**. Reproduksi pengujian customer tersedia pada [bukti v7](docs/testing-customer-v7.md); hasil pemeriksaan terbaru tercatat pada [rilis v11](docs/rilis-v11.md).

## Deployment

Untuk Vercel, pilih root directory **`website`**, build `npm run build`, dan Node.js 24. Aplikasi memakai Turso remote dan Vercel Blob privat; konfigurasi lokal tidak menjadi penyimpanan production. Lihat [panduan publikasi](docs/publikasi-github.md).

## Cakupan demo

Data orang, foto ilustrasi, produk, transaksi, pembayaran, dan jurnal bersifat simulasi. Aplikasi tidak melakukan transfer bank atau integrasi operasional resmi BNI. Rilis v7 memiliki 11 akun untuk 10 peran, termasuk pelanggan demo khusus dengan email `customer@unit-toko.demo`. Hak akses dan kepemilikan transaksi diperiksa di server. Password dibuat dari seed privat; tidak disertakan dalam repositori.

Arsip studi kasus, kredensial, database, unggahan privat, dan log tidak disertakan dalam repositori ini.
