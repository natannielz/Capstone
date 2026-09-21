# Panduan demo Unit Toko — Divisi BNI

Website demo capstone ini menghubungkan pemesanan Divisi/Unit BNI, persediaan, pengadaan, pengiriman, penerimaan, penagihan, pembayaran simulasi, laporan, dan tutup periode dalam satu basis data. Aplikasi saat ini adalah v6; lihat [catatan rilis dan validasi 17 September 2026](../docs/rilis-v6.md). [Etalase dan akun pelanggan v7](../docs/plan-customer-marketplace-v7.md) masih dalam tahap rencana.

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

Buka landing page lalu pilih **Masuk portal**. Masukkan email dan password akun yang diinginkan. Password awal unik per akun; **lihat `website/.data/demo-accounts.json` secara privat**. Jangan menyalin daftar password ke dokumen publik. Setiap ID dalam tabel menggunakan email `ID@unit-toko.demo`, misalnya `pic-a@unit-toko.demo`.

| Akun | Peran | Kegiatan utama |
| --- | --- | --- |
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

Terdapat 10 akun untuk 9 peran. Peran ditetapkan administrator dan tidak dapat dipilih melalui form login. Pergantian peran presentasi dilakukan dengan **Keluar akun**, lalu masuk menggunakan email/password akun berikutnya. Jika password telah diubah melalui profil, gunakan password terbaru; daftar seed awal tidak otomatis ikut berubah.

Menu **Profil saya** menyediakan nama, kontak, jabatan, foto PNG/JPG maksimal 2 MiB, serta penggantian password 12–128 karakter. Penggantian password mengakhiri seluruh sesi akun dan mengharuskan login ulang. Foto awal bersifat fiktif. Email/divisi/peran tidak dapat diubah melalui profil.

## Urutan presentasi

1. PIC Operasional memilih katalog 36 SKU, mencari nama/SKU atau kategori, memasukkan barang ke keranjang, lalu mengajukan pesanan. Katalog berisi 12 produk dasar dan varian paket 3/6 dengan harga/satuan masing-masing; 12 foto produk dipakai bersama variannya. Keranjang tersimpan per akun di perangkat.
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
