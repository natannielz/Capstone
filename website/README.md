# Panduan demo Unit Toko — Divisi BNI

Website demo capstone ini menghubungkan pemesanan Divisi/Unit BNI, persediaan, pengadaan, pengiriman, penerimaan, penagihan, pembayaran simulasi, laporan, dan tutup periode dalam satu basis data. Rilis v7 menambahkan etalase dan akun pelanggan tanpa memisahkan basis data operasional. Lihat [cakupan v7](../docs/plan-customer-marketplace-v7.md), [hasil pengujian customer](../docs/testing-customer-v7.md), dan [baseline v6](../docs/rilis-v6.md).

## Membuka dan menjalankan lokal

Dari folder `website`, gunakan Node.js 24 dan npm:

```powershell
npm ci
npm run demo:accounts
npm run dev
```

Sebelum menjalankan perintah tersebut, salin `.env.example` menjadi `.env.local` dan isi `DEMO_PASSWORD_SEED` privat sedikitnya 32 karakter. Perintah `demo:accounts` menyimpan kredensial awal per akun secara privat; tidak perlu menampilkan isinya di terminal bersama. Buka alamat yang ditampilkan, biasanya `http://localhost:3000`. Skema serta data sintetis diinisialisasi otomatis pada akses pertama; langkah `db:local`/Wrangler dari versi lama tidak digunakan. Basis data libSQL lokal tersimpan di `website/.data/unit-toko.db`, sedangkan lampiran/avatar di `website/.data/files/`. Keduanya terpisah dari data layanan daring.

```powershell
npm run typecheck
npm test
npm run test:api
npm run build
```

Pengujian domain/SQL memakai basis data terisolasi. `test:api` membutuhkan server lokal berjalan dan menambahkan satu skenario demonstrasi ke data lokal. `npm start` membuka hasil build secara lokal, tanpa publikasi.

## Akun demo

Buka beranda `/` lalu pilih akses yang sesuai. Pelanggan berbelanja melalui `/shop` dan masuk melalui `/customer/login`. PIC divisi, admin, serta seluruh petugas masuk melalui `/staff/login` menuju `/workspace`. Akun internal yang membuka halaman toko, keranjang, checkout, atau akun pelanggan otomatis diarahkan ke ruang kerja oleh server. Form login menolak akun dari portal yang tidak sesuai setelah kredensial diperiksa; pilihan portal tidak mengubah peran akun.

Masukkan email dan password akun yang diinginkan. Password awal unik per akun; **lihat `website/.data/demo-accounts.json` secara privat**. Jangan menyalin daftar password ke dokumen publik. ID staf/PIC menggunakan email `ID@unit-toko.demo`, misalnya `pic-a@unit-toko.demo`. Akun `customer-demo` menggunakan email khusus `customer@unit-toko.demo`.

| Akun | Peran | Kegiatan utama |
| --- | --- | --- |
| `customer-demo` — Pelanggan Demo | Pelanggan | Etalase, checkout, pesanan sendiri, penerimaan, komplain, pembayaran invoice |
| `pic-a` — Nadia Putri | PIC Operasional | Pesanan, pengganti, penerimaan, komplain, bukti transfer |
| `pic-b` — Raka Pratama | PIC Teknologi | Alur yang sama, hanya data divisinya |
| `kepala` — Dewi Lestari | Kepala Toko | Tinjauan pesanan, pengadaan, harga, persetujuan opname |
| `staf` — Arif Setiawan | Staf Toko | Reservasi, Surat Jalan, finalisasi, penerimaan/retur pemasok |
| `kurir` — Bima Saputra | Kurir | Pengiriman yang ditugaskan, bukti, biaya |
| `laporan` — Maya Sari | Pengolah Laporan | Rekap gabungan dan pengajuan bulan |
| `penagihan` — Sinta Ayu | Penagihan | Invoice, verifikasi/alokasi dana, koreksi, pembayaran simulasi |
| `pimpinan` — Hendra Wijaya | Pimpinan Unit | Persetujuan laporan, dana, kredit, biaya/pengembalian |
| `akuntansi` — Laras Wulandari | Akuntansi | Laporan, posting, tutup periode |
| `admin` — Admin Demo | Administrator | Akun/peran, divisi, pemasok |

Terdapat 11 akun untuk 10 peran. Peran ditetapkan administrator dan tidak dapat dipilih melalui form login. Pergantian peran presentasi dilakukan dengan **Keluar akun**, lalu masuk menggunakan email/password akun berikutnya. Jika password telah diubah melalui profil, gunakan password terbaru; daftar seed awal tidak otomatis ikut berubah.

Menu **Profil saya** menyediakan nama, kontak, jabatan, foto PNG/JPG maksimal 2 MiB, serta penggantian password 12–128 karakter. Penggantian password mengakhiri seluruh sesi akun dan mengharuskan login ulang. Foto awal bersifat fiktif. Email/divisi/peran tidak dapat diubah melalui profil.

## Alur pelanggan

1. Jelajahi `/shop`, cari nama/SKU, pilih kemasan dan jumlah, lalu tambah ke keranjang atau **Beli sekarang**.
2. Pilih barang yang ingin dipesan. Checkout membawa pilihan pengunjung melalui login; barang lain tetap di keranjang.
3. Periksa penerima, kontak, dan alamat, lalu **Buat pesanan**. Tidak ada pembayaran saat checkout.
4. Pesanan masuk ke petugas yang sama. Customer dapat memantau pengiriman, menerima barang, dan mengajukan komplain melalui `/account/orders`.
5. Setelah penerimaan final, Penagihan menerbitkan invoice. Customer mencatat pembayaran simulasi untuk invoice sendiri; petugas memverifikasi dan mengalokasikan dana.
6. Kelola nama, foto, kontak, alamat bawaan, dan kata sandi di `/account`. Alamat pesanan lama tetap berupa snapshot.

Pelanggan hanya melihat transaksi miliknya. Tidak ada signup publik, marketplace multivendor, rating, diskon rekaan, atau pembayaran bank nyata. Satu akun demo tambahan diprovisikan secara idempotent pada database lama maupun baru tanpa mereset password/profil/status akun yang sudah ada.

Untuk pengujian customer API gunakan `scripts/smoke-customer.mjs` bersama fixture/database yang dijelaskan dalam [bukti uji v7](../docs/testing-customer-v7.md). Script menolak target selain lingkungan QA lokal yang ditentukan.

## Urutan presentasi divisi

1. PIC Operasional memilih katalog 236 SKU, mencari nama/SKU, kelompok atau koleksi, memasukkan barang ke keranjang, lalu mengajukan pesanan. Sebanyak 36 SKU lama (12 produk dasar dengan varian paket 3/6) tetap ada; 200 SKU tambahan memiliki deskripsi dan jalur foto WebP tersendiri. Katalog staf dan etalase pelanggan menampilkan 24 keluarga produk per halaman. Keranjang tersimpan per akun di perangkat. Data tambahan dipasang sekali secara atomik dengan jurnal persediaan/modal awal; data transaksi, akun, kata sandi dan produk lama dipertahankan. Rincian terdapat pada `../docs/implementation-v10-catalog.md`.
2. Kepala Toko membuka pesanan dan memilih Tinjau & setujui.
3. Staf mencadangkan stok tersedia dan membuat Surat Jalan. Sisa pesanan tetap tercatat untuk kiriman susulan. Pengganti memerlukan persetujuan PIC.
4. Kurir mengirim barang, mencatat penerima/bukti, atau mencatat gagal kirim.
5. PIC mengonfirmasi jumlah aktual. Isi alasan selisih atau ajukan komplain barang bermasalah.
6. Staf mencatat barang kembali bila ada, menyelesaikan komplain, lalu memfinalkan transaksi. Finalisasi tidak mengurangi stok dua kali.
7. Penagihan membuat invoice dari penerimaan final. Tombol Cetak / PDF menyediakan Surat Jalan dan invoice.
8. PIC mencatat transfer simulasi dan mengunggah bukti PNG/JPG/PDF maksimal 4 MiB. Penagihan memverifikasi lalu mengalokasikan dana. Satu pembayaran dapat dialokasikan bertahap ke beberapa invoice; sisanya tetap ada.
9. Pengolah Laporan/Penagihan meninjau rekap, filter divisi/OMI/Smart/tanggal, ekspor CSV, lalu mengajukan bulan. Pimpinan menyetujui versinya, Akuntansi menutup periode.
10. Setelah periode ditutup, transaksi baru menyarankan tanggal awal bulan terbuka berikutnya. Gunakan tanggal simulasi yang konsisten pada semua langkah. Pembayaran bulan berikutnya tidak mengubah snapshot bulan tertutup.

Gunakan **Keluar akun** lalu login akun berikutnya untuk setiap peran. Pesanan awal menyediakan contoh belum ditinjau, siap dikirim, dan invoice dibayar sebagian. Menu, angka ringkasan, dokumen, serta lampiran mengikuti kewenangan; administrator tidak mendapat transaksi bisnis, staf tidak mendapat keuangan pelanggan, dan kurir hanya melihat tugasnya tanpa harga/HPP.

## Kasus pendukung

Pengadaan OMI reguler/DDO dibuat Kepala atau Staf, sedangkan Smart/Pasar Kering oleh Kepala. Setelah dikonfirmasi, Staf mencatat penerimaan parsial. Stok bertambah hanya sebanyak yang diterima. Pembayaran sebelum penerimaan menjadi uang muka. Pasar Kering membutuhkan pengajuan Kepala → persetujuan Pimpinan → pencatatan dana tersedia oleh Penagihan. Pembayaran OMI/DDO dicatat Staf dengan referensi invoice/SPH; Smart/Pasar Kering melalui Kepala.

Persediaan menampilkan batch, lokasi, kedaluwarsa, jumlah ditahan, dan tersedia. Staf mengajukan retur/opname; opname disetujui Kepala. Retur ditolak tidak otomatis boleh dijual. Barang rusak dari divisi dapat diteruskan lewat pilihan sumber karantina. Retur diterima pemasok menimbulkan klaim pemasok; penyelesaian eksternal klaim berada di luar integrasi demo.

Penagihan mengajukan nota kredit, Pimpinan menyetujui. Invoice asal tetap tersimpan. Koreksi invoice lunas menghasilkan saldo divisi untuk alokasi atau pengembalian. Pengembalian saldo dan biaya operasional memerlukan persetujuan sebelum dibayar secara simulasi.

## Arsitektur dan kontrol

Next.js/React/TypeScript dengan libSQL: file SQLite persisten untuk pengembangan lokal, Turso remote untuk target Vercel. Lampiran memakai file lokal saat pengembangan dan Vercel Blob privat saat deployment. `lib/domain/engine.ts` mengatur aturan bisnis dan otorisasi; `lib/server/repository.ts` menyimpan transaksi secara atomik. Versi global mencegah perebutan stok/dana, identitas tindakan mencegah pencatatan ulang, dan batasan unik melindungi sumber invoice. `db/schema.ts` dan `drizzle/` berisi relasi/migrasi.

Server memeriksa peran, divisi, dan tugas kurir. Sesi memakai cookie HttpOnly/SameSite Strict; password PBKDF2; SQL memakai parameter terikat. Permintaan lintas origin ditolak. Berkas dibatasi ukuran/tipe, dan download memeriksa hak akses. Aplikasi tidak menyimpan data transaksi hanya di browser.

Website aktif di **[unit-toko-bni.vercel.app](https://unit-toko-bni.vercel.app)** dengan Turso dan Blob privat. Bukti pengujian login, RBAC, transaksi, lampiran, dan persistensi tersedia pada [catatan rilis v6](../docs/rilis-v6.md). Buat daftar akun awal lokal melalui `npm run demo:accounts`; hasilnya privat di `.data/demo-accounts.json` dan tidak disertakan dalam repositori. Lihat [panduan publikasi](../docs/publikasi-github.md).

## Batas demo

Nama, data barang, nilai uang, kendaraan, pembayaran, dan jurnal adalah sintetis. Pajak default nonaktif; simulasi pajak bukan faktur resmi. Jatuh tempo default 30 hari adalah asumsi demo. Tidak ada transfer uang, pesan WhatsApp, atau integrasi nyata bank/BNI, OMI, Smart, pajak, dan akuntansi eksternal. Formula HET tidak ditebak dari sumber yang ambigu.

Gunakan lampiran sintetis. Arsip BPP/diagram sumber berada di luar folder website dan tidak ikut publikasi. Pemakaian operasional nyata memerlukan validasi kebijakan, integrasi resmi, audit keamanan, kapasitas, dan pencadangan data.
