# Pengalaman pelanggan dan audit proses v11

Subjek: Unit Toko, tempat pelanggan dan divisi mendapatkan kebutuhan pantry, rapat, dan merchandise. Beranda membantu memilih akses; etalase membantu menemukan dan membeli barang.

## Arah visual

Palet: petroleum `#073B45`, tinta `#143F43`, putih hangat `#F6F3ED`, mint pucat `#E5F2EF`, oranye tanah `#C65B32`, putih `#FFFFFF`. Archivo Black untuk judul singkat; DM Sans untuk isi, formulir, harga, dan navigasi. Foto produk yang sudah tersedia menjadi bahan utama.

Beranda menggunakan judul besar yang bergerak per baris dan komposisi foto dengan kartu produk bertumpuk. Etalase memakai pembuka editorial ringkas, pilihan kategori bergambar, dan kartu produk dengan harga serta tindakan yang lebih mudah ditemukan. Panel pencarian tetap dekat dengan hasil, dan pembuka dipadatkan ketika pengguna sedang mencari.

```text
Beranda: [logo / navigasi] [judul besar | foto + kartu produk]
         [dua pintu akses] [koleksi bergambar] [proses layanan]
Etalase: [logo / pencarian / keranjang] [editorial + kategori]
         [filter ringkas] [produk dengan foto dominan] [halaman]
```

Ciri utama adalah rak produk berlapis yang bergerak mengikuti urutan pembukaan, pointer desktop, dan scroll. Tidak ada klaim diskon, popularitas, rating, atau angka penjualan buatan. Navigasi, formulir, dan checkout tidak menunggu animasi. Preferensi reduced motion menampilkan komposisi statis lengkap.

Referensi kalibrasi: [GSAP Showcase](https://gsap.com/showcase/) dan [koleksi interaktif Awwwards](https://www.awwwards.com/websites/web-interactive/). Referensi digunakan untuk arah gerak dan kualitas komposisi, bukan klaim bahwa aplikasi telah memenangkan penghargaan. Implementasi memakai lifecycle React, cleanup, dan matchMedia dari dokumentasi GSAP.

## Audit dan pelaksanaan

1. Baca ulang tujuh bab BPP dan sepuluh diagram; petakan terhadap fungsi yang benar-benar tersedia pada source dan tes.
2. Catat gap dengan sumber, dampak, dan prioritas. Implementasikan gap material yang dapat ditangani dalam demo tanpa mengarang kebijakan bisnis atau integrasi eksternal.
3. Bangun ulang tampilan etalase, perkuat landing/login, dan tambahkan interaksi pada kartu/keranjang serta transisi halaman pelanggan.
4. Verifikasi desktop/ponsel, keyboard, login dan pemisahan akses, pencarian/filter/halaman, keranjang, checkout, serta perubahan domain yang dipilih dari audit.
5. Terbitkan ke Vercel dan sinkronkan snapshot sumber bersih ke GitHub setelah pemeriksaan lulus.

Kritik rancangan: hero besar di setiap hasil pencarian akan menghambat belanja. Karena itu hero etalase hanya menjadi pembuka untuk penjelajahan awal; filter dan hasil mendapat prioritas saat pengguna mencari. Foto dan produk nyata dalam katalog menggantikan elemen abstrak yang tidak membantu memilih barang.
