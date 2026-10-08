# Dua pintu masuk Unit Toko — v13

23 September 2026. Perubahan presentasi login memisahkan kebutuhan pelanggan dan pengguna internal. Proses autentikasi, portal yang dikirim, penguncian submit, penanganan error, serta tujuan setelah login tetap menggunakan kode sebelumnya.

## Arah visual

Pelanggan memakai kanvas ivory `#F7F6F1`, kertas putih `#FFFFFF`, petroleum `#103F47`, warna teks sekunder `#5B6A6B`, dan sage `#5C7771`. Judul Archivo ekspresif berpasangan dengan DM Sans untuk formulir. Foto still-life yang sudah tersedia menjadi satu karya utama dalam bingkai; foto dan judul berada di samping panel masuk yang membulat. Tidak ada tumpukan kartu, titik dekoratif, atau nama produk tertutup.

Portal staf memakai petroleum gelap `#103640`, teks putih lembut `#F3F7F5`, panel putih, garis `#42666D`, serta konektor sage `#7BA89F`. Komposisinya berupa pengantar dan diagram proses vertikal di kiri, formulir dokumen kompak di kanan. Diagram memperlihatkan Pesanan → Persediaan → Pengiriman → Penagihan sebagai alur konseptual, bukan data transaksi atau indikator pekerjaan yang sedang berjalan. Tidak memakai foto pelanggan atau angka rekaan. Tombol utama dan input bersudut kecil untuk struktur yang lebih tegas.

Kritik terhadap rancangan: awalnya kedua halaman berpotensi kembali menjadi split-screen foto yang sama dengan warna berbeda. Rancangan akhir mengganti keseluruhan bagian editorial staf dengan diagram operasional, mengubah ukuran tipe, permukaan panel, serta keluarga bentuk tombol. Customer mempertahankan foto barang; staf memperlihatkan hubungan pekerjaan.

## Gerak

Hook `useLoginMotion` terpisah dari animasi halaman publik. Customer mendapat satu urutan masuk judul dan karya foto (650–900ms). Staf mendapat urutan teks, konektor proses, lalu simpul (550–650ms dengan stagger). Formulir, label, input, error, dan tombol submit tidak bergerak atau menunggu animasi. Fokus keyboard menyelesaikan urutan yang sudah ada tanpa membuat tween baru.

`useGSAP` mengelola scope; `matchMedia` membatasi efek ke `prefers-reduced-motion: no-preference` dan direvert saat unmount/pergantian portal. CSS tetap tampil lengkap tanpa JavaScript. Tidak memakai animasi berulang atau callback yang memasukkan ulang parent context. Perubahan reduced motion mengembalikan tampilan statis. Hover panah dibatasi pada pointer yang mendukung hover dan pengguna tanpa reduced motion.

## Referensi primer

- [W3C WAI — Labeling Controls](https://www.w3.org/WAI/tutorials/forms/labels/), diakses 23 September 2026: label terlihat di atas field, asosiasi `htmlFor`/`id`, dan hubungan dekat dengan kontrol dipertahankan. Placeholder tidak menggantikan label.
- [GSAP — React](https://gsap.com/resources/React/), diakses 23 September 2026: hook `useGSAP`, scope, dan cleanup animasi dipakai agar halaman aman saat navigasi serta perubahan preferensi.
- Dokumentasi Next.js lokal `05-server-and-client-components.md`: state, event handler, dan GSAP berada dalam komponen client. Browser API tidak dijalankan saat render server.

## Cakupan dan pemeriksaan

CSS memakai namespace `.login-v13` dan `.login-portal-customer`/`.login-portal-staff`; halaman lain tidak terkena. Pada layar kecil foto customer berubah menjadi thumbnail di samping judul, sedangkan diagram staf disembunyikan karena tidak dibutuhkan untuk masuk. Judul, navigasi, dan form tetap dalam alur normal. Panel tidak memakai tinggi tetap sehingga error dan teks yang membesar dapat menambah tinggi dengan aman.

Validasi agen: lint terfokus pada `login.tsx` dan `login-motion.tsx` lulus tanpa peringatan; blok fungsi submit dibandingkan dengan checkout publikasi dan tidak berubah. Pemeriksaan visual browser, integrasi CSS, dan pengujian akses final dilakukan oleh agen utama sebelum rilis; dokumen ini tidak mengklaim hasil yang belum dijalankan.
