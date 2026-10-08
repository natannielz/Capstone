# Bukti pengujian pelanggan v7

Pengujian API lokal terakhir pada 22 September 2026: **22 kelompok lulus, 0 gagal**, termasuk pengulangan unggahan bukti bersamaan. Pengujian filter pelanggan dan CSV: **5 tes lulus**. Pemeriksaan TypeScript dan lint untuk artefak pengujian lulus. Bukti ini mencakup API/server dan logika data; pemeriksaan visual, build rilis, dan deployment dicatat terpisah di bawah.

## Lingkungan dan reproduksi

- Website lokal: `http://127.0.0.1:3007`.
- Database khusus pengujian: `file:.data/unit-toko-v7-qa.db`. Database demo biasa maupun produksi tidak dipakai.
- Konfigurasi privat: `.tools/v7-qa.env` pada root proyek; nilainya tidak disalin ke dokumen, source, atau keluaran pengujian.
- Skrip: `website/scripts/smoke-customer.mjs` dan `website/scripts/customer-qa-fixture.mjs`.
- Dari direktori `website`, jalankan `node --env-file=../.tools/v7-qa.env scripts/smoke-customer.mjs`.
- Skrip menolak URL database selain nama QA persis, alamat server selain port lokal 3007, mode produksi, serta credential remote database/blob. Produk penanda unik harus muncul di katalog server sebelum mutasi HTTP dilakukan, sehingga server terbukti membaca database QA yang sama.
- Fixture pelanggan kedua `customer-qa-b` hanya dibuat melalui helper lokal dengan hash kata sandi menggunakan kode keamanan aplikasi. Tidak ada fitur publik untuk membuat akun ini. Pengadaan dan stok fixture dibuat melalui repository/engine asli.
- Produk fixture dinonaktifkan melalui `product.update` dalam blok `finally` setelah tes, termasuk jika tes gagal. Sebelum tes berikutnya, helper juga membersihkan sisa proses yang terhenti. Pencocokan dibatasi ke SKU `QA-V7-<8 hex>-(full|partial|isolation)` dan nama, kategori, serta satuan fixture yang sesuai; 200 produk `DEMO` tetap aktif. Tidak ada penghapusan stok atau riwayat transaksi. Perintah domain mencatat audit dan menaikkan revisi periode terbuka seperti pembaruan produk biasa.
- Kata sandi dibentuk di memori dari seed privat. Keluaran skrip tidak mencetak kata sandi, token, atau cookie. Pengujian pergantian kata sandi mengembalikan kata sandi awal, termasuk melalui blok pemulihan. Mutasi role QA juga dikembalikan dalam blok pemulihan.

Rangkaian penuh menambah transaksi QA dan mengubah profil/foto pelanggan QA untuk pengujian. Jangan menjalankannya bersamaan dengan pemeriksaan browser akun pelanggan yang sama. Tidak ada penghapusan/reset database dalam skrip.

Pembersihan katalog pada 22 September 2026 menonaktifkan 18 produk sisa pengujian lokal. Pengulangan 22 kelompok tes kemudian lulus dan otomatis menonaktifkan ketiga produk fixture barunya. Pemeriksaan katalog setelah tes menemukan 236 SKU aktif, termasuk seluruh 200 produk bergambar baru, dan **0 produk QA aktif**. Pemanggilan ulang helper tanpa fixture aktif tidak mengubah data. Katalog produksi juga diperiksa dan tidak memuat produk QA.

## Cakupan dan hasil API

| Cakupan | Bukti yang diperiksa | Hasil |
|---|---|---|
| Katalog publik | Properti produk menggunakan daftar yang diizinkan; produk aktif saja; tanpa HPP, pemasok, identitas pembeli, credential, atau graph operasional | Lulus |
| Akses tanpa sesi | State, commands, profil, foto, lampiran, dokumen, dan laporan menolak dengan 401 | Lulus |
| Login | Email khusus pelanggan tidak bergantung pada ID; kapitalisasi/spasi ditangani; role/ID kiriman klien diabaikan; cookie HttpOnly dan SameSite Strict | Lulus |
| Login gagal | Email tidak dikenal dan kata sandi salah memiliki respons generik sama; percobaan kesembilan email acak terkena 429 | Lulus |
| Profil dan alamat | Penyisipan role ditolak 403; alamat dipangkas dan tersimpan; nama, kontak, dan alamat pesanan lama tetap berupa snapshot setelah profil berubah | Lulus |
| Penjualan penuh | Pesanan, persetujuan, reservasi, pengiriman, penerimaan, finalisasi, invoice, bukti pembayaran, verifikasi, dan alokasi dilakukan melalui HTTP | Lulus |
| Penerimaan parsial | Barang ditolak kembali tepat sekali ke stok, komplain diselesaikan, sisa dibatalkan, hanya jumlah diterima yang ditagih, kelebihan dana dikembalikan | Lulus |
| Kepemilikan | Pelanggan A, pelanggan B, dan PIC masing-masing memiliki transaksi nyata; akses langsung ke ID transaksi pihak lain ditolak | Lulus |
| Dokumen dan lampiran | Invoice/SJ pihak lain 404; unggahan pada target pihak lain 403; bukti pembayaran hanya terbaca oleh pemilik dan peran keuangan yang sesuai | Lulus |
| Data menurut peran | Pelanggan tidak mendapat HPP/direktori pelanggan/keuangan internal; staf tidak mendapat invoice/pembayaran/jurnal; admin tidak mendapat graph transaksi | Lulus |
| Kewenangan tindakan | Semua 10 role diuji pada state, invoice, laporan, dan batas admin; pelanggan ditolak pada persetujuan, reservasi, finalisasi, invoice, verifikasi, alokasi, pengadaan, dan tutup periode | Lulus |
| Identitas pembeli | Owner palsu pada checkout/pembayaran/lampiran diabaikan; transaksi mengikuti sesi dan relasi sumber; perpindahan akun pelanggan ↔ petugas ditolak | Lulus |
| Harga dan stok berubah | Harga palsu/harga lama dan stok yang sudah dicadangkan menghasilkan 409 secara atomik; input yang diperbaiki dapat dikirim ulang | Lulus |
| Tanggal pelanggan | Tanggal checkout klien tahun 1900 diabaikan; tanggal buku ditentukan server dan timestamp aktual tetap dicatat | Lulus |
| Saldo pelanggan | Nota kredit invoice yang sudah lunas menjadi saldo milik pelanggan tersebut; tidak tampil pada pelanggan kedua atau PIC | Lulus |
| Retry/idempotensi | Permintaan paralel dengan ID sama untuk pesanan, reservasi, pengiriman, penerimaan, finalisasi, invoice, pembayaran, verifikasi, alokasi, dan refund tidak menggandakan efek; ID sama untuk payload/aktor berbeda ditolak 409 | Lulus |
| Retry unggahan | Bukti pembayaran yang dikirim ulang mendapat ID sama; tiga unggahan penerimaan paralel dengan nama berbeda menghasilkan satu lampiran, isi file utuh, target berbeda tetap terpisah, pembeli asing ditolak | Lulus |
| Peran tidak dikenal | Sesi lama langsung ditolak 403 pada seluruh endpoint terlindungi; login baru ditolak generik 401 tanpa cookie sesi | Lulus setelah perbaikan |
| Foto dan sesi | Foto sendiri dapat diunggah/dibaca secara privat; parameter URL tidak dapat memilih foto pihak lain; perubahan kata sandi mencabut dua sesi; logout mencabut sesi dan data tetap tersedia setelah login ulang | Lulus |
| CSRF | Origin asing pada command dan logout ditolak 403 | Lulus |

Pembayaran pelanggan sebelum invoice ditolak. Pembayaran yang baru dicatat tetap berstatus `recorded` dan belum mengurangi piutang. Percobaan alokasi pembayaran pelanggan B ke invoice A atau PIC juga ditolak oleh engine meskipun pelakunya petugas keuangan.

## Rekonsiliasi numerik dan ID untuk pemeriksaan browser

Fixture yang dipakai dalam pemeriksaan browser 21 September: `019d3cec`. Pengulangan HTTP terakhir pada 22 September memakai fixture baru `5ac25c2e`; hasil rekonsiliasi tetap sama dan seluruh 22 kelompok lulus.

| Skenario | Rekonsiliasi | ID pesanan | ID invoice |
|---|---|---|---|
| Penuh | Stok awal 10; reservasi 3 → tersedia 7 dengan fisik 10; berangkat → fisik 7; diterima 3; invoice Rp30.000; alokasi Rp30.000; piutang 0 | `2d86bdd9-dfcf-4726-9dfb-26502ecb7613` | `6d6abd97-088e-4946-96b0-b395c86b793f` |
| Parsial | Stok awal 10; kirim 3; terima 2; retur layak 1 → fisik 8; sisa 1 dibatalkan; invoice Rp20.000; pembayaran Rp25.000; alokasi Rp20.000; refund Rp5.000; piutang 0 | `b0bd48da-fa89-4449-ad23-859176bb9553` | `0f1e353b-b8e7-40b4-9e8a-76cd4e7f1107` |

Sesudah pelunasan skenario penuh, pengujian tambahan menyetujui nota kredit Rp1.000. Piutang tetap nol dan Rp1.000 menjadi saldo pelanggan dari nota kredit. Ini menjelaskan saldo kredit yang terlihat pada pesanan penuh saat diperiksa melalui browser.

## Temuan yang telah diperbaiki

Putaran pertama menemukan login akun berperan tidak dikenal masih mengembalikan 200 dan membuat sesi. Endpoint terlindungi sudah menolak sesi tersebut dengan 403, sehingga tidak terbukti ada akses data, tetapi login belum gagal tertutup. Pelaksana utama menambahkan pemeriksaan role sebelum pembuatan sesi di `app/api/auth/login/route.ts` dengan respons generik 401. Pemeriksaan HTTP khusus sesudah perbaikan dan pengulangan penuh 21 kelompok sama-sama lulus.

## Uji filter pelanggan dan CSV

`website/tests/customer-views-v7.test.ts` berisi lima tes yang dijalankan tanpa database:

1. URL filter divisi v6 tetap kompatibel; dua pelanggan dengan `divisionId: null` memiliki kunci filter berbeda; identitas ambigu tidak menghasilkan kunci sah.
2. Pilihan pembeli menggunakan nama snapshot transaksi tanpa membutuhkan direktori atau kontak pelanggan, dan tidak menggandakan pilihan pembeli.
3. Ekspor pelanggan A sama dengan baris layar: hanya penjualan final pada rentang tanggal, menggunakan tanggal finalisasi dan harga historis; pelanggan B, PIC, HPP, alamat, dan bukti privat tidak ikut.
4. Total gabungan tiga pembeli Rp66.000; filter kategori tetap menghormati pembeli yang dipilih.
5. Nama pelanggan dengan awalan formula, tanda kutip, dan koma tetap menjadi sel inert yang benar, tanpa menambah kolom.

Validasi terakhir untuk file QA: ESLint lulus; `tsc --noEmit` lulus. Pengujian fixture/domain/database/billing v7 yang ditangani anggota lain dicatat oleh pelaksana utama pada laporan gabungan.

## Pemeriksaan visual dan rilis

Pemeriksaan browser oleh pelaksana utama, 21 September 2026, pada server QA lokal terisolasi:

- Beranda, pencarian kopi, detail kemasan 3 pak, harga Rp85.500, dan tautan kembali ke hasil pencarian berfungsi.
- Barang tamu dibawa sekali melalui login customer ke checkout. Pengajuan berhasil menampilkan nomor pesanan dan pesan sukses; tidak ada pembayaran otomatis.
- Dua tab: perubahan teh 1 menjadi 2 kotak di tab kedua langsung tampil pada tab pertama. Checkout hanya dua kotak teh Rp24.000; satu dus air tetap ada di keranjang kedua tab.
- Draft alamat tetap sama setelah reload. Nama penerima kosong melalui keyboard menampilkan kesalahan dan memfokuskan field yang perlu diperbaiki.
- Profil disimpan dan terlihat pada identitas akun. Navigasi saat profil belum disimpan membuka dialog; pilihan Tetap di halaman mempertahankan isian.
- Detail penerimaan sebagian menampilkan 3 dikirim, 2 diterima, 1 kembali; invoice Rp20.000 lunas, transfer Rp25.000, refund Rp5.000, dan saldo nol. Alamat order tetap snapshot lama sesudah nama profil berubah.
- Profil dan detail pesanan diperiksa pada lebar 320/390/768/980/1280/1440 px: satu elemen main, tidak ada overflow horizontal atau gambar gagal. Checkout juga tidak overflow pada enam ukuran tersebut. Screenshot desktop dan ponsel diperiksa; masalah lebar form profil dari gaya lama telah diperbaiki.
- Tindakan checkout bawah menggantikan navigasi ponsel. Accessible name ikon akun dan tautan gambar keranjang diperbaiki. Fokus input kesalahan dan dialog diuji dengan keyboard/semantik browser.

Regresi gabungan terakhir: **154/154 tes lulus**, TypeScript lulus, ESLint keluar 0 dengan 9 warning (7 penggunaan navigasi penuh pada pergantian sesi/halaman, 2 unused variable fixture lama). Empat berkas perbaikan unggahan terakhir juga lulus lint tanpa warning. Build dan bukti produksi tersedia pada [catatan rilis v7](rilis-v7.md). Pemeriksaan ini bukan audit WCAG menyeluruh; pengiriman dan pembayaran tetap simulasi.

Pemeriksaan terarah tambahan pada sumber pengembalian saldo menggunakan delapan pembayaran: pembeli divisi dan dua customer dikenali; pembayaran tanpa identitas, identitas ambigu, belum terverifikasi, sudah habis dialokasikan, atau habis dicadangkan untuk refund tidak ditawarkan. Label ringkasan dan keuangan menggunakan istilah pembeli agar mencakup kedua jenis akun. Pemeriksaan ini memakai ekspresi selector komponen dan helper asli; bukan skenario browser tambahan.

## Pemulihan tindakan akun setelah respons tidak pasti

Review komponen menemukan bahwa ID permintaan dialog hanya hidup selama modal terbuka. Bila pencatatan pembayaran sudah disimpan server tetapi respons hilang, membuka ulang modal dapat membuat ID baru dan mencatat transfer kedua. Ini diperbaiki di `components/customer-account.tsx` dan `lib/client/customer-action-draft.ts`.

Permintaan sekarang disimpan di `sessionStorage` sebelum dikirim, terpisah menurut akun, jenis tindakan, dan target. Payload/ID/form asli dikunci selama hasil masih ambigu. Membuka ulang atau memuat ulang halaman akun memulihkan permintaan yang sama setelah identitas akun berhasil dikonfirmasi. Gangguan jaringan, 5xx, respons 2xx tidak terbaca, atau hasil 2xx yang tidak memenuhi kontrak tidak menghapus draft. Penolakan 4xx yang jelas membuka isian untuk koreksi; 401 mengarah ke login dengan draft tetap tersedia. Bila penyimpanan browser gagal, permintaan belum dikirim.

Enam tes tambahan dalam `website/tests/customer-action-draft-v7.test.ts` lulus:

1. Identitas dan payload tepat dipertahankan setelah pembukaan ulang; tidak tertukar antar akun, target, atau halaman; draft tertunda tidak bisa ditimpa permintaan baru.
2. Klasifikasi hasil ambigu versus 4xx, termasuk pemulihan pemetaan baris penerimaan dan nilai numerik setelah reload.
3. Komponen asli dengan server tiruan yang menyimpan pembayaran lalu memutus respons: remount mengirim payload/ID identik dan jumlah penyimpanan tetap satu.
4. Respons 400 membuka koreksi; 503, JSON rusak dengan status 200, dan payload sukses tidak lengkap tetap mengunci permintaan yang sama.
5. Sesi 401 mengarahkan ke login internal dan mempertahankan draft hanya untuk akun asal.
6. Penyimpanan browser yang ditolak mencegah pengiriman pembayaran yang tidak dapat dipulihkan.

Pengujian kegagalan di atas menggunakan komponen asli dengan hook dan transport yang dikendalikan dalam proses Node, bukan simulasi gangguan jaringan di browser. Pemeriksaan TypeScript lulus; lint tidak memiliki error, dengan peringatan navigasi `window.location.assign` yang digunakan untuk batas sesi/autentikasi.

## Unggah ulang bukti tanpa duplikasi

Review akhir menemukan bahwa endpoint lampiran membuat ID baru pada setiap unggahan. Perbaikan memakai hash isi file, akun pengunggah, jenis bukti, serta target sebagai identitas yang stabil. Nama file atau pergantian tanggal tidak membuat salinan baru. Otorisasi diperiksa sebelum penyimpanan dan kembali sebelum metadata disimpan; permintaan bersamaan atau hasil commit yang tidak pasti direkonsiliasi terhadap metadata yang sudah ada.

Lima belas tes dalam `tests/attachment-upload-v7.test.ts` lulus, mencakup retry setelah respons hilang, nama/tanggal berbeda, dua pengunggah/target/isi berbeda, penolakan izin, pemulihan blob hilang, kegagalan penyimpanan, batas 4 MB, MIME aktual, periode tertutup, dan pembatalan stream. Dua tes menggunakan filesystem lokal asli: 16 penulis bersamaan tidak pernah menerbitkan isi parsial atau menimpa objek, dan 8 unggahan helper identik menghasilkan satu dokumen/metadata. Perilaku Blob privat dimodelkan sesuai SDK terpasang; produksi tidak diberi unggahan uji.

Kegagalan metadata yang belum dapat dipastikan tidak menghapus objek bersama. Satu objek tanpa metadata dapat tertinggal, maksimal 4 MB per akun/target/isi, dan dipakai kembali saat retry. Pembersihan objek semacam ini memerlukan rekonsiliasi terpisah setelah semua permintaan selesai.

## Pemeriksaan browser produksi

Pada 22 September, beranda dan grid 12 keluarga produk beserta foto diperiksa langsung di URL publik. Login akun pelanggan baru membuka profil dengan identitas, foto, kontak, dan alamat yang sesuai. Halaman Pesanan saya menampilkan keadaan kosong untuk akun baru; pesanan divisi tidak muncul. Logout kembali ke halaman masuk. Tidak ada pesanan, pembayaran, unggahan, atau perubahan profil uji yang dibuat pada database produksi.
