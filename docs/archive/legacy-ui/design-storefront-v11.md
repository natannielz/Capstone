# Storefront v11

## Arah desain

Katalog pelanggan memakai komposisi editorial yang ringkas: judul besar, tiga foto produk nyata dalam kartu miring, dan pilihan kategori bergambar. Petroleum `#073B45`, ink `#143F43`, offwhite `#F6F3ED`, mint `#E5F2EF`, dan aksen orange `#C65B32` mengikuti identitas toko. Tombol oranye memakai varian lebih gelap `#B64C27` agar teks putih memiliki rasio kontras sekitar 5,18:1. Archivo Black dipakai untuk judul, DM Sans untuk informasi belanja.

Hero hanya muncul pada katalog awal tanpa pencarian/filter, halaman pertama, dan urutan nama. Pencarian, kategori, koleksi, perubahan urutan, serta halaman berikutnya langsung mengutamakan hasil. Foto dan tautan hero berasal dari produk aktif; tidak ada promosi atau angka penjualan buatan.

## Perubahan

- Navigasi, pencarian, kategori, kartu produk, harga, status stok, dan pagination memiliki hierarki serta kontras yang lebih jelas.
- Kategori bergambar menjalankan filter katalog yang sebenarnya. Kartu produk tetap mengarah ke varian yang dipilih dan menyimpan konteks kembali ke katalog.
- Umpan balik tambah ke keranjang mengikuti hasil mutasi yang berhasil. Operasi gagal tidak menampilkan status berhasil.
- CSS dalam `.shop-app` menyelaraskan detail produk, keranjang, checkout, riwayat pesanan, dan profil pelanggan tanpa mengubah alur formulirnya.
- Tata letak dua kolom produk tetap tersedia di ponsel. Judul produk minimum 13 px, kategori 10 px, dan status stok 11 px.

## Perilaku yang dipertahankan

RBAC, stok, kuantitas keranjang, kondisi pending, parameter filter, dan pagination menggunakan perilaku yang sudah ada. Navigasi filter atau halaman yang dilakukan pengguna menggulir ke judul katalog setelah render. Kembali dari detail produk tetap memulihkan posisi sebelumnya; pembaruan keranjang atau hidrasi tidak memicu perpindahan halaman.

Halaman kosong mandiri tetap memiliki `h1`. Keadaan kosong/error di dalam katalog menggunakan `h2`, karena judul halaman sudah tersedia.

## Gerakan

GSAP mengatur kemunculan judul/foto hero, produk hasil filter, serta umpan balik badge keranjang. Gerakan terbatas pada elemen presentasi; tombol dan bidang formulir tetap langsung dapat digunakan. Implementasi menghormati `prefers-reduced-motion`, membersihkan animasi ketika komponen dilepas, dan menyelesaikan reveal saat fokus keyboard memasuki kontennya. Pembaruan keranjang awal dan kondisi pending tidak memicu animasi sukses. Scroll menggunakan perilaku native tanpa mengambil alih pengguliran pengguna.

Callback yang mengikuti perubahan halaman terikat pada context media GSAP yang sama. Ini mencegah siklus pembersihan animasi ketika komponen dibuka atau dilepas ulang. Fokus keyboard menyelesaikan animasi masuk pada tautan produk.

## Verifikasi

ESLint terarah lulus. Pemeriksaan gabungan menghasilkan 224 tes lulus, TypeScript lulus, lint tanpa error, serta build produksi Vercel berhasil. [Catatan rilis](rilis-v11.md) memuat hasil lengkap.

Pemeriksaan browser yang dilakukan:

- Lebar 320 dan 390 px serta desktop: tidak ada overflow horizontal pada etalase yang diperiksa.
- Pencarian, kategori, pengurutan harga dan halaman kedua: hasil serta URL sesuai; filter/pagination membawa layar ke judul katalog.
- Tambah ke keranjang: jumlah bertambah dan umpan balik muncul sesudah berhasil. Barang pengujian kemudian dihapus dari keranjang lokal.
- Rincian produk, kembali ke hasil, keranjang kosong/terisi, dan pengalihan checkout tamu ke login pelanggan diperiksa.
- Pencarian barang QA menghasilkan 0 produk dan satu judul halaman utama.

Cabang gagal/pending keranjang, isolasi akun, checkout terautentikasi dan pengelolaan pesanan tercakup tes domain serta HTTP. Reduced motion diperiksa melalui sumber, bukan emulasi preferensi sistem pada browser. Pemeriksaan tampilan ini tidak mengklaim pengujian visual seluruh keadaan setiap halaman akun.
