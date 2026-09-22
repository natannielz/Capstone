# Integrasi katalog simulasi v10

Implementasi menambah 200 SKU berbeda (`DEMO-001`–`DEMO-200`) ke katalog staf dan etalase pelanggan. Sebanyak 36 SKU lama dalam 12 keluarga produk tetap tersedia. Katalog lengkap berisi 236 SKU dalam 212 keluarga produk; tidak ada penggantian nama, harga, atau stok produk lama.

## Data dan penampilan

- Sumber terkurasi: `website/lib/domain/demo-products-v10.json`. Setiap produk memiliki nama, deskripsi, harga, satuan, kelompok, koleksi, instruksi gambar dan jalur gambar yang berbeda.
- Distribusi: 40 minuman, 60 pantry/konsumsi, 40 kebutuhan kantor, 60 merchandise.
- Berkas pendamping `demo-catalog-public-v10.json` hanya memuat ID, gambar, deskripsi, kelompok dan koleksi. Komponen katalog tidak mengimpor instruksi pembuatan gambar.
- Gambar ditayangkan dari `/images/products/generated/demo-NNN.webp`. Pembuatan dan pemeriksaan kelengkapan gambar menjadi langkah rilis terpisah; kelulusan tes kode tidak menyatakan bahwa 200 aset sudah selesai.
- API publik menambahkan `group` dan `collections` melalui daftar field eksplisit. Harga pokok, pemasok, data pembeli, kredensial, dan `imagePrompt` tidak ditayangkan.
- Pencarian nama/SKU, filter kelompok, koleksi, ketersediaan dan pengurutan tetap berlaku. Koleksi lama yang memuat satu produk pada beberapa kebutuhan tetap dipertahankan.
- Kedua katalog menayangkan maksimal 24 keluarga produk per halaman. Halaman disimpan dalam URL; perubahan filter kembali ke halaman pertama dan tautan halaman yang terlalu besar dibatasi ke halaman terakhir. Foto dimuat secara malas, kecuali dua foto pertama katalog internal.

## Penambahan data tanpa reset

`ensureSeed()` memanggil `provisionDemoCatalog()` setelah inisialisasi lama. `seedState()` tetap menjadi fixture awal yang sudah ada sehingga data lama dan tes historis tidak berubah.

Importer menggunakan penanda `demo-catalog-v10` dalam tabel pelacakan migrasi yang sudah tersedia. Seluruh produk baru, batch, mutasi stok, jurnal pembuka, perubahan revisi periode, kenaikan versi global, dan penanda selesai ditulis dalam satu transaksi. Constraint penjaga versi menggunakan pola yang sama dengan eksekusi perintah domain; perubahan bersamaan diproses ulang dengan batas empat percobaan. Dua proses inisialisasi tidak menambah stok dua kali.

Aturan pelestarian:

- Hanya pasangan ID/SKU yang belum ada yang ditambahkan. Jika pasangan tersebut sudah ada, seluruh data produk dan stoknya dipertahankan, termasuk kondisi nonaktif dan nama/harga yang disunting. SKU tersebut tidak mendapat stok tambahan.
- Konflik ID dengan SKU lain, atau SKU yang dimiliki ID lain, menghentikan seluruh transaksi. Tidak ada pengambilalihan produk.
- Setelah penanda tercatat, pembukaan ulang aplikasi tidak mereset atau mengisi stok yang sudah terjual. Importer tidak menyunting pesanan, akun, kata sandi, sesi, profil, pemasok, atau dokumen transaksi lama.
- Batch baru memiliki jumlah, lokasi dan harga pokok simulasi. Jumlah didasarkan pada kategori dan indeks produk, bukan data pelanggan. Harga pokok dibulatkan dari 74% harga jual. Masa berlaku batch OMI ditetapkan satu tahun setelah tanggal pembukuan pembuka.

## Pembukuan dan periode tertutup

Jumlah nilai seluruh batch baru dibukukan tepat satu kali sebagai debit **Persediaan** dan kredit **Modal awal**. Tidak ada penerimaan kas, pembayaran pemasok atau penjualan fiktif. Jurnal dan setiap mutasi pembuka memakai sumber `demo-catalog-v10` agar dapat dikenali.

Tanggal jurnal adalah hari ini dalam zona Asia/Jakarta, atau tanggal 1 bulan setelah periode tertutup paling akhir jika hari ini sudah berada dalam rentang yang ditutup. Payload dan snapshot periode tertutup tidak disentuh. Revisi periode terbuka yang terdampak dinaikkan dan persetujuan yang masih menunggu dibuka kembali, sesuai perilaku jurnal dalam mesin domain. Jika periode ditutup saat import berlangsung, penjaga versi membatalkan transaksi dan percobaan berikutnya memilih tanggal terbuka yang baru.

## Verifikasi

Sepuluh tes baru mencakup 200 metadata unik, kesesuaian metadata publik, 236 SKU/212 keluarga, persediaan awal dan jurnal seimbang, pelestarian record lama, kondisi nonaktif dan stok terpakai, konflik identitas, rollback di tengah transaksi, penutupan periode bersamaan, dua initializer bersamaan, serta batas halaman.

Alur pembelian diuji dengan SKU baru: pemeriksaan harga checkout, kepemilikan pelanggan, penolakan akses pelanggan lain, penolakan pengelolaan produk oleh pelanggan, persetujuan kepala, reservasi staf, dan pengiriman kurir yang mengurangi stok sebenarnya.

- Suite domain/RBAC/auth/migrasi: **209/209 lulus**.
- ESLint: **0 error**, 9 peringatan lama pada navigasi akun/workspace dan variabel tes lama.
- TypeScript `tsc --noEmit`: **lulus**.
- Build Next.js produksi: **lulus**, termasuk kompilasi TypeScript, pemrosesan rute, dan penelusuran dependensi server.
- Smoke HTTP akses: **14/14 kelompok lulus**, mencakup 11 akun, pemisahan portal pelanggan/staf, dan pengalihan akses yang benar.
- Smoke HTTP pelanggan: **22/22 kelompok lulus**, mencakup transaksi, saldo/tagihan, lampiran, perubahan kata sandi, pencabutan sesi dan isolasi pelanggan.

Log privat tersedia di `.tools/v10-tests.log`, `.tools/v10-typecheck.log`, `.tools/v10-lint.log`, `.tools/v10-build.log`, `.tools/v10-access-http.log`, dan `.tools/v10-customer-http.log`. Launcher QA privat `.tools/run-v10-qa.mjs` mempertahankan nilai kosong variabel remote secara eksplisit agar `.env.local` tidak mengaktifkan penyimpanan remote saat pengujian. Loader sumber pada helper QA mendukung JSON lokal untuk membaca manifest yang sama dengan aplikasi; pembatasan direktori proyek dan penolakan target produksi tetap berlaku.

Seluruh pengujian memakai SQLite memori atau basis data QA lokal yang terisolasi. Tidak ada pembacaan/ekspor basis data produksi atau deployment dalam langkah implementasi ini.
