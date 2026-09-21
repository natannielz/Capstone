# Rencana v7 — Etalase dan akun pelanggan

Tanggal: 21 September 2026. Status: rencana implementasi, belum diterapkan.

Permintaan pengguna: publikasikan kode yang sudah selesai ke `natannielz/Capstone`, lalu rencanakan satu akun pelanggan demo tambahan dan halaman belanja dengan pola marketplace seperti Shopee. Pengguna memilih **pelanggan demo khusus**, terpisah dari PIC divisi. Dasar implementasi adalah rilis v6; studi kasus tetap Unit Toko dengan operasional penjualan kepada divisi BNI, ditambah kanal pelanggan demo.

## Hasil yang dituju

Pelanggan dapat menemukan barang, memilih kemasan, memasukkan barang ke keranjang, membuat pesanan, memantau pengiriman, mengonfirmasi penerimaan, dan membayar tagihan secara simulasi. Pesanan tersebut masuk ke proses toko yang sama dan dibatasi berdasarkan pemiliknya di server.

Tambahkan satu akun: ID `customer-demo`, nama **Pelanggan Demo**, email **customer@unit-toko.demo**, role `customer`. Akun ini belum dibuat pada tahap perencanaan. Sesudah implementasi, total menjadi 11 akun demo dan 10 role. Password awal unik disimpan dalam berkas privat dan disampaikan secara privat; tidak ditulis dalam repositori publik.

Tiga review terpisah memeriksa pengalaman belanja, model transaksi/otorisasi, dan kelayakan publikasi kode. Temuan utama: antarmuka belanja saat ini masih berada dalam workspace; hak baca selain PIC/kurir belum memiliki cabang pelanggan; identitas pembeli pada invoice dan transaksi masih bergantung pada divisi. Halaman customer memerlukan perubahan model pembeli dan izin, bukan hanya menu baru.

## Pengalaman dan halaman

| Halaman | Isi dan perilaku |
| --- | --- |
| `/` | Beranda toko: pencarian utama, kategori, koleksi Pantry/Rapat/Merchandise, harga dan produk pilihan. Tautan jelas ke portal divisi dan petugas. |
| `/shop` | Semua produk, cari nama/SKU, filter kategori/ketersediaan, urut harga/nama, jumlah hasil, dan hapus filter. Simpan filter pada URL. |
| `/shop/[product]` | Foto, deskripsi faktual, pilihan kemasan/SKU, harga dan ketersediaan yang sesuai, jumlah, Tambah ke keranjang, Beli sekarang. |
| `/cart` | Pilih sebagian/semua barang, ubah jumlah/kemasan, hapus, subtotal pilihan, dan lanjut checkout. Barang yang tidak dibeli tetap tersimpan. |
| `/checkout` | Penerima, nomor kontak, alamat, catatan, dan rincian harga server. Tombol akhir **Buat pesanan**. Pelanggan tidak memilih divisi atau tanggal pembukuan. |
| `/account/orders` | Pesanan sendiri, pencarian/filter status, ringkasan barang dan total, serta langkah berikutnya. |
| `/account/orders/[id]` | Detail pesanan, kronologi, pengiriman parsial, tanggapan pengganti, penerimaan, komplain, dokumen, dan tagihan terkait. |
| `/account` | Nama, kontak, foto profil, alamat bawaan, password, dan keluar akun. Peran serta pemilik transaksi tidak dapat diedit sendiri. |

Katalog dapat dilihat sebelum login. Pengunjung boleh menyimpan pilihan belanja lokal; checkout memerlukan sesi customer. Setelah login, pertahankan produk, SKU, jumlah, dan tujuan halaman. Penggabungan keranjang pengunjung ke akun dilakukan sekali, memiliki aturan konflik yang jelas, dan tidak memindahkan draft pelanggan lama ke akun lain. Sesi customer diarahkan ke toko/akun; sesi PIC dan petugas mengikuti izin portal mereka.

Impor keranjang tamu hanya membawa SKU/jumlah, tanpa alamat atau draft akun. Gunakan identitas impor untuk mencegah duplikasi; gabungkan SKU sama dengan batas jumlah yang divalidasi, tampilkan koreksi yang diperlukan, dan tandai impor selesai secara atomik. Uji login berulang dan pergantian akun.

Tautan kembali dari detail mempertahankan pencarian dan posisi katalog. Muat ulang checkout mempertahankan draft yang sah. Beli sekarang menggunakan pilihan produk tersebut tanpa menghapus isi keranjang lain. Server memeriksa ulang harga, produk aktif, jumlah, dan ketersediaan sebelum membuat pesanan; perubahan ditampilkan untuk ditinjau kembali.

## Arah visual

Pola marketplace dipakai pada pencarian, kategori, pilihan variasi, keranjang, dan pesanan pribadi. Identitas Unit Toko tetap petroleum `#073B45`, orange `#FF6A2A`, canvas `#F5F7F9`, dan teks `#142D33`. Gunakan DM Sans untuk isi/form dan Archivo Black secara terbatas untuk judul koleksi. Hindari judul promosi panjang, dekorasi tanpa fungsi, dan kepadatan elemen yang saling bersaing.

- Desktop: konten maksimal sekitar 1280px, grid 4–5 kolom, foto rasio konsisten, nama dua baris, harga dan satuan mudah dibandingkan. Keranjang berada pada halaman sendiri.
- Ponsel: grid dua kolom bila ruang cukup, pencarian mudah dijangkau, navigasi Belanja/Keranjang/Pesanan/Akun. Pada detail/checkout, tindakan bawah menggantikan navigasi agar tidak bertumpuk.
- Spacing menggunakan skala 4/8/12/16/24/32/48px; kelompok terkait dekat, pergantian bagian memiliki jarak lebih besar. Ukuran teks isi umumnya 14–16px dengan tinggi baris yang cukup.
- Pakai 12 foto keluarga produk, 36 pilihan SKU, serta foto editorial yang sudah tersedia. Buat aset tambahan hanya bila ada kebutuhan visual nyata; jangan membuat sudut produk/isi kemasan fiktif.
- Harga, stok, keterangan paket, error, loading, kosong, dan berhasil memiliki tampilan konsisten. Tidak ada rating, penjualan, diskon, ongkir, atau hitung mundur rekaan.
- Label form tetap terlihat, fokus keyboard jelas, sentuhan utama minimal sekitar 44px, kontras memadai, dan gerak menghormati reduced motion.

Referensi pola interaksi: panduan resmi Shopee tentang [pencarian](https://help.shopee.co.id/portal/4/article/72368), [checkout](https://help.shopee.co.id/portal/4/article/71961), dan [status pesanan](https://help.shopee.co.id/portal/4/article/72518). Fitur dan status Unit Toko tetap mengikuti kemampuan aplikasi.

## Alur transaksi tahap pertama

1. Customer memilih barang dan membuat pesanan. Simpan penerima, kontak, dan alamat sebagai snapshot agar perubahan profil tidak mengubah pengiriman lama.
2. Kepala Toko meninjau pesanan dengan kanal **Pelanggan**. Staf melakukan reservasi dan Surat Jalan; Kurir menangani pengiriman. Pengadaan, substitusi, pembatalan sisa, dan kiriman parsial memakai aturan v6.
3. Customer mengonfirmasi penerimaan dan mengajukan komplain bila perlu. Staf menangani pengembalian serta finalisasi.
4. Penagihan menerbitkan invoice berdasarkan jumlah yang benar-benar diterima dan difinalkan. Customer mencatat transfer simulasi serta bukti; Penagihan memverifikasi dan mengalokasikan dana.
5. Customer melihat status pemenuhan dan pembayaran secara terpisah. Checkout tidak langsung berarti dibayar atau lunas. Label menjelaskan bahwa pembayaran tersedia setelah tagihan diterbitkan.

Keputusan tahap pertama: **bayar tagihan setelah invoice terbit**. Uang muka langsung sesudah checkout bukan bagian rilis ini karena memerlukan relasi dana–pesanan dan rekonsiliasi tambahan. Saldo, nota kredit, refund, dan pengiriman parsial yang sudah didukung tetap harus mengenali pemilik customer dengan benar.

Aturan ini berlaku juga pada API: `payment.record` oleh customer wajib merujuk invoice miliknya yang sudah terbit. Percobaan sebelum invoice atau dengan invoice asing ditolak. Referensi pembayaran belum berarti alokasi/lunas; alur pencatatan dana internal lama tetap tersedia. Tanggal pembukuan checkout ditentukan server menurut periode terbuka yang berlaku, sementara waktu kejadian sebenarnya tetap dicatat. Uji ketika bulan kalender sudah ditutup tanpa mengubah snapshot lama.

## Model data dan RBAC

- Tambahkan role `customer` dengan `divisionId: null`. Gunakan referensi pembeli yang memilih tepat satu dari divisi atau customer; `customerId` mengacu pada user pelanggan.
- Constraint tepat satu pemilik diwajibkan pada order/invoice dan entitas transaksi milik pembeli. Pembayaran internal yang belum teridentifikasi dan lampiran biaya tetap boleh tidak memiliki pembeli sesuai fungsi aslinya; jangan menerapkan constraint secara membabi buta ke semua tabel.
- Perbarui order, invoice, komplain, pembayaran, alokasi, saldo/nota kredit, refund, dokumen, dan lampiran agar identitas pembeli konsisten. Identitas turunan berasal dari transaksi sumber, bukan pilihan bebas dari browser.
- Pusatkan pemeriksaan dalam helper seperti `buyerKey`, `sameBuyer`, `buyerLabel`, dan `assertBuyerAccess`. Dua `divisionId: null` tidak membuktikan dua pelanggan adalah pemilik yang sama.
- Buat scope customer tersendiri. Tolak role yang tidak dikenal secara default; customer tidak boleh masuk ke cabang data internal.
- `customerId`, harga, dan perubahan status bisnis ditentukan server. Permintaan palsu yang mengganti pembeli, harga, role, tanggal terlarang, atau status pembayaran ditolak atau dinetralkan secara eksplisit.
- Endpoint katalog publik hanya memuat produk aktif, foto, varian, harga, dan ketersediaan agregat. `/api/state` tetap memerlukan autentikasi. Supplier, HPP, lokasi batch, jurnal, audit internal, dan data akun lain tidak ikut respons publik/customer.
- Dokumen, ekspor, download bukti, avatar privat, dan unggahan memeriksa kepemilikan pada server, termasuk akses langsung melalui ID yang diketahui.
- Invoice gabungan dan alokasi hanya menerima sumber dari pembeli yang sama. Portal staf menyediakan label/filter pembeli yang membedakan divisi dan pelanggan.

| Customer boleh | Customer tidak boleh |
| --- | --- |
| Melihat katalog, profil, pesanan, kiriman, tagihan dan saldo sendiri | Membaca transaksi PIC atau pelanggan lain |
| Memesan, menanggapi substitusi, membatalkan sisa yang masih memenuhi aturan | Menyetujui pesanan sebagai petugas, mencadangkan stok, atau memfinalkan penjualan |
| Mengonfirmasi penerimaan, komplain, transfer simulasi, dan bukti sendiri | Menerbitkan invoice, memverifikasi/mengalokasikan pembayaran |
| Mengubah profil/password yang diizinkan | Mengelola pengadaan, jurnal, periode, admin, role, atau harga jual |

## Migrasi dan satu akun baru

1. Tambahkan kolom/FK/index `customer_id` dan ubah keharusan `division_id` pada entitas yang perlu dua jenis pembeli. Tambahkan constraint kepemilikan dan perbarui payload JSON serta mapper SQL bersama-sama.
2. Transaksi v6 tetap dimiliki divisi asal; snapshot periode tertutup, nomor dokumen, stok, kredensial, dan histori terjaga. Tidak mereset database.
3. Provisioning akun terpisah dan idempotent, dapat berjalan di database kosong maupun v6 terpasang. Menambah daftar seed saja tidak cukup untuk deployment yang sudah memiliki data.
4. Rerun tidak menggandakan akun, mereset password, mengaktifkan akun yang telah dinonaktifkan, atau mengubah profil lama. Simpan hash autentikasi di database dan kredensial awal hanya dalam artefak privat yang diabaikan Git.
   Normalisasi email. Bila ID/email sudah dimiliki akun berbeda, hentikan provisioning dengan konflik yang jelas; jangan mengambil alih akun atau mengganti role-nya.
5. Uji migrasi pada salinan data terisolasi. Siapkan backup sebelum perubahan remote, verifikasi hitungan/invarian sebelum dan sesudah, serta rencana pemulihan. Jangan memakai Preview dengan database Production bersama untuk uji mutasi.

## Tahap pengerjaan

| Tahap | Pekerjaan | Bukti selesai |
| --- | --- | --- |
| 1. Fondasi pembeli | Role, referensi pembeli, scope baca, izin command, migrasi dan provisioning akun | Migrasi dua kali aman; uji isolasi customer/PIC lulus |
| 2. Etalase | Beranda, katalog publik, pencarian/filter, detail produk dan navigasi responsif | Produk/SKU/harga tepat; alur penemuan bisa dipakai sebelum login |
| 3. Pembelian | Keranjang terpilih, Beli sekarang, login lanjutan, checkout/draft dan retry | Pesanan tercatat sekali, barang lain tetap di keranjang, konflik dua tab tertangani |
| 4. Akun dan operasional | Pesanan pribadi, profil/alamat, penerimaan/komplain, tagihan/bukti, label pembeli pada staf | Alur customer → toko → kurir → invoice → pembayaran selesai dan terisolasi |
| 5. QA dan publikasi | Regresi v6, tes customer, browser/responsif, aksesibilitas dasar, dokumentasi, push dan Vercel | Build dan tes lulus; deployment terverifikasi; akun disampaikan privat |

Berkas utama: `lib/domain/accounts.ts`, `model.ts`, `selectors.ts`, `engine.ts`, helper pembeli, `order-views.ts`, `invoice-selection.ts`, `db/schema.ts`, `drizzle/`, `lib/server/database.ts`, `repository.ts`, `documents.ts`, pemeriksaan lampiran, `lib/domain/navigation.ts`, `lib/client/cart-storage.ts`, komponen penagihan, dan route etalase/akun baru. Baca dokumentasi Next.js lokal yang relevan sebelum mengubah kode aplikasi.

## Kriteria penerimaan

- Customer A tidak dapat membaca/mengubah pesanan, pengiriman, invoice, pembayaran, saldo, atau lampiran customer B maupun PIC, termasuk melalui endpoint langsung dan ID yang diketahui. Customer B cukup fixture uji; hanya satu akun baru diprovisikan untuk demo.
- PIC tidak mendapatkan transaksi customer. Staf, Kurir, Kepala, Penagihan, dan role lain tetap dibatasi sesuai tugas. Uji setiap jenis tindakan, tidak hanya menu tersembunyi.
- Harga/SKU dan pemilik yang dimanipulasi tidak diterima. Invoice/alokasi lintas customer–customer dan customer–divisi ditolak.
- Fixture stok 10 dan pesanan 3 × Rp10.000: setelah reservasi tersedia 7; setelah kirim fisik 7; penerimaan penuh menghasilkan invoice Rp30.000; transfer terverifikasi dan dialokasikan menghasilkan piutang nol.
- Penerimaan hanya 2 dengan 1 dikembalikan menghasilkan fisik 8 dan invoice Rp20.000. Retry tidak menggandakan stok, invoice, bukti, alokasi, atau refund. Pajak/biaya fixture nol agar hasil dapat diperiksa langsung.
- Login dari produk/checkout mempertahankan konteks; tujuan mengikuti role dan mencegah redirect ke domain lain. Form sebelum hidrasi tetap POST dan aman; logout gagal tidak berpura-pura berhasil.
- Checkout hanya menghapus kuantitas yang berhasil dibeli; barang tidak dipilih tetap ada. Kegagalan/putus respons tidak menghilangkan draft, dan retry idempotent menghasilkan satu pesanan. Perubahan pada tab lain tidak terhapus diam-diam.
- Profil/alamat yang diedit tidak mengubah snapshot pesanan lama. Akun nonaktif tidak dapat bertransaksi. Tidak ada data customer lain pada respons, ekspor, cache, atau draft lintas akun.
- Migrasi/provisioning dua kali mempertahankan transaksi, akun, password yang telah diganti, status aktif, dan periode lama.
- Periksa layar 320/390/768/980/1280/1440px, keyboard/fokus, error form, loading/kosong, gambar, tombol kembali, dan kontras. Tindakan bawah tidak menutupi isi atau bertumpuk.
- Jalankan typecheck, lint, tes domain/repository/RBAC/migrasi/HTTP dan build yang relevan. Gunakan bukti uji v6 sebagai baseline, bukan klaim bahwa tes lama membuktikan fitur v7.
- Sesudah rilis, pastikan URL publik, login customer, pembatasan role, persistensi, gambar, dan alur utama bekerja. Catat hasil, batas, commit, serta deployment yang benar-benar terverifikasi.

## Batas dan handoff

Satu toko, satu akun pelanggan demo tambahan, barang ilustrasi, pengiriman internal, dan pembayaran simulasi. Multi-vendor, signup publik, chat penjual, review/rating, program promo, payment gateway, dan integrasi bank/kurir nyata tidak masuk rilis ini.

Kode v6 dan rencana ini dipublikasikan ke `https://github.com/natannielz/Capstone` dengan aplikasi di `website/` dan dokumentasi di `docs/`. Arsip sumber, data lokal, kredensial, log, dan konfigurasi akun layanan tetap privat. Pekerjaan customer dimulai melalui goal setelah rencana tersedia; dokumen ini tidak menyatakan akun/halaman customer sudah dibuat.
