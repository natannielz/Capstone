# Riset desain dan gerakan Unit Toko v13

23 September 2026 · Sumber primer · Rekomendasi implementasi, bukan klaim penghargaan.

Permintaan terbaru: hilangkan tombol besar “Mulai” pada carousel otomatis; buat landing page lebih khas; bedakan pengalaman masuk pelanggan dan staf; berikan bentuk tombol yang sesuai fungsinya. Sasaran desain adalah kualitas presentasi yang matang sekaligus akses yang mudah. Istilah “award winning” dipakai sebagai arah kualitas, bukan status penghargaan Unit Toko.

## Metode dan batas bukti

Riset membaca konten halaman resmi, dokumentasi resmi GSAP/W3C/web.dev, dan kode Unit Toko v12. Halaman referensi dibaca melalui hasil web terstruktur; tidak ada inspeksi visual langsung atau pengukuran animasi situs referensi dalam riset ini. Karena itu, struktur konten yang teramati dibedakan dari rancangan visual yang diusulkan. Screenshot, geometri, alur login, serta animasi implementasi Unit Toko perlu diverifikasi di browser sebelum rilis. Tidak ada studi pengguna, pengukuran konversi, atau klaim skor aksesibilitas penuh.

## Yang benar-benar ditemukan

| Sumber primer | Pengamatan yang didukung sumber | Penerapan yang disarankan |
| --- | --- | --- |
| [Fellow — situs resmi](https://fellowproducts.com/) | Halaman menempatkan satu produk utama beserta aksi yang jelas, kemudian pilihan kategori, produk terpilih, dan penjelasan penggunaan. | Satu gagasan utama di layar pertama; foto berkualitas mengarahkan ke kebutuhan nyata. Jangan menumpuk beberapa promosi dengan bobot sama. Ini inferensi desain, bukan pengukuran performa situs Fellow. |
| [Aesop — situs resmi](https://www.aesop.com/) | Halaman menghubungkan koleksi dengan rutinitas penggunaan. Pilihan aroma menggunakan kategori yang dapat dipilih; produk memperlihatkan ukuran/harga serta tindakan. | Landing Unit Toko dapat bercerita melalui momen pantry, rapat, dan kegiatan. Pergantian visual harus memperlihatkan pilihan yang benar-benar berbeda dan mengubah tujuan tautan. |
| [Apple — MacBook Air](https://www.apple.com/macbook-air/) | Konten memisahkan pengenalan singkat, sorotan, dan bab manfaat produk. Foto digunakan untuk menerangkan manfaat tertentu. | Ambil pola satu adegan untuk satu pesan. Untuk Unit Toko, animasikan pergantian kebutuhan kantor; tidak perlu menyalin adegan 3D atau membuat klaim produk yang tidak ada. Kecepatan dan jenis animasi situs Apple tidak diverifikasi di sini. |
| [GSAP — matchMedia](https://gsap.com/docs/v3/GSAP/gsap.matchMedia()/) | Breakpoint dan preferensi gerakan dapat dikelompokkan dalam kondisi. Animasi dalam konteks dapat dibersihkan ketika kondisi berubah. | Pisahkan koreografi desktop/mobile, tangani reduced motion, dan bersihkan pengamat serta listener. Jangan menyimpan transform lama setelah resize atau navigasi. |
| [GSAP — ScrollTrigger](https://gsap.com/docs/v3/Plugins/ScrollTrigger/) | Mendukung pemicu ketika bagian masuk layar; `once` mencegah pemutaran berulang ketika pengguna kembali ke bagian tersebut. | Tampilkan bab kebutuhan dengan pembukaan sekali. Scroll tetap milik browser; tidak diperlukan pin panjang, scrub yang memaksa, atau scroll hijack. |
| [web.dev — animation performance](https://web.dev/articles/animations-guide) | Transform dan opacity menghindari banyak pekerjaan layout/paint; kebutuhan optimasi perlu diperiksa, bukan diasumsikan. | Gerakkan lapisan foto dalam bingkai stabil. Jangan mengubah tinggi section, ukuran input, atau posisi tombol saat transisi. Jangan memasang `will-change` di semua elemen. |
| [W3C APG — Carousel](https://www.w3.org/WAI/ARIA/apg/patterns/carousel/) | Carousel otomatis memerlukan kendali rotasi; fokus menghentikan rotasi sampai pengguna memulai kembali. Hover juga menghentikan rotasi. Kontrol tidak memindahkan fokus pengguna. | Hapus presentasi tombol besar “Mulai”, lalu gunakan kontrol kecil yang tetap jelas dan dapat dioperasikan. Autoplay dapat menjadi keadaan awal tanpa menjadikan putar/jeda sebagai CTA utama. |
| [WCAG 2.2 — Pause, Stop, Hide](https://www.w3.org/WAI/WCAG22/Understanding/pause-stop-hide.html) | Konten otomatis perlu mekanisme untuk menjeda, menghentikan, atau menyembunyikannya. Berhenti hanya selama fokus tertahan tidak memadai. Konten yang memperbarui diri tidak mendapat pengecualian lima detik. | Jangan menghapus seluruh mekanisme jeda sambil membiarkan pergantian otomatis tanpa batas. Opsi terbaik untuk kebutuhan ini adalah kontrol ikon yang tidak mendominasi. |
| [WCAG — Consistent Identification](https://www.w3.org/WAI/WCAG22/Understanding/consistent-identification.html) | Fungsi yang berulang perlu diidentifikasi secara konsisten, termasuk nama aksesibel dan alternatif teks. | “Tombol unik” berarti keluarga bentuk menurut fungsi: aksi utama, filter, navigasi, kuantitas, dan tindakan destruktif. Tombol untuk fungsi sama tetap seragam di seluruh website. |
| [WCAG — Target Size](https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum.html) | Ukuran minimum adalah 24 × 24 CSS px dengan pengecualian tertentu; target lebih besar membantu penggunaan. | Gunakan area sentuh sekitar 44 px pada kontrol penting. Ikon boleh tampak kecil di dalam target besar; jangan memperkecil area klik demi tampilan minimal. |

## Diagnosis khusus kode v12

- Landing sudah mempunyai gambar hero, pratinjau produk, pita kategori, akses, tiga kartu koleksi, dan layanan. Namun kategori pantry/rapat/merchandise muncul berulang dalam beberapa bentuk, sehingga belum ada satu interaksi khas yang membawa pengguna dari cerita ke katalog.
- Gerakan landing hampir semuanya pola yang sama: teks naik sedikit dan foto membesar menuju ukuran asli. Ini rapi, tetapi peran tiap bagian belum dibedakan. Menambah lebih banyak gerakan masuk yang identik tidak menghasilkan arah desain baru.
- `Login` memilih teks berdasarkan portal, tetapi kedua portal tetap memakai foto `pantry-moment-v3.png` dan susunan visual yang sama. Permintaan pengguna membutuhkan perbedaan komposisi dan konteks, bukan sekadar mengganti judul.
- Screenshot pengguna menunjukkan kontrol rotasi mendapat bobot visual seperti tombol pembelian. Karena carousel sudah otomatis, penekanan tersebut membuat pengguna merasa harus memulai sesuatu. Hierarki tombol perlu diperbaiki.

## Lima keputusan prioritas

### 1. Satu adegan khas: kebutuhan sepanjang hari

Konsep yang dapat diterapkan dengan aset yang sudah ada: area editorial berisi satu foto besar dan tiga pilihan momen. Labelnya “Pagi · Pantry”, “Siang · Rapat”, dan “Kegiatan · Merchandise”; waktu jam dapat dipakai hanya sebagai konteks cerita, bukan jam operasional toko.

Komposisi desktop: teks pendek dan dua akses yang jelas di sisi kiri; bingkai foto lebar di kanan. Di bawah foto, tiga pilihan berupa tab datar dengan angka urut dan garis aktif. Memilih suatu momen mengganti foto, kalimat penjelas singkat, dan tautan kategori. Seluruh caption berada di alur normal; tidak menumpang foto atau tombol. Mobile: judul → foto → pilihan → caption. Tidak ada tumpukan kartu diagonal.

Koreografi: pembukaan foto 700–900 ms dengan skala kecil, kata judul berurutan sekitar 70 ms; setelah itu diam. Pergantian momen menggunakan crossfade sekitar 280–360 ms, perpindahan gambar maksimal 12 px, dan garis aktif singkat. Klik cepat mengganti tujuan terkini tanpa mengantrikan animasi. Pembelian dan navigasi selalu siap diklik. Pilihan momen harus berfungsi sebagai kontrol asli, bukan tiga label dekoratif.

Ini usulan desain; pemilihan akhir dan verifikasi implementasi dicatat dalam laporan rilis. Tidak wajib menambah carousel otomatis kedua di landing bila sudah ada carousel toko. Interaksi yang dikendalikan pengguna cukup untuk menciptakan identitas.

### 2. Dua pintu masuk yang terlihat berbeda

**Pelanggan:** suasana retail editorial, latar hangat terang, satu komposisi barang/produk yang besar, judul ramah dan ringkas, akses kembali ke katalog. Form memiliki kartu atau kolom terang dengan tombol aksi pelanggan. Foto dan teks masuk sekali; form tidak bergerak.

**Staf:** latar petrol gelap atau kolom operasional yang tegas, angka langkah besar, dan diagram sederhana “Pesanan → Persediaan → Pengiriman → Administrasi”. Diagram merupakan penjelasan alur, bukan indikator aktivitas langsung. Form putih dengan label internal yang jelas serta tombol masuk ruang kerja yang lebih terstruktur. Hindari foto belanja yang sama, status palsu, angka transaksi rekaan, atau indikator hijau seolah server sedang dipantau.

Form pada keduanya tetap mempertahankan label permanen, password visibility, autocomplete, pesan kesalahan, keadaan loading, dan tautan beralih portal. Hanya cerita pendamping yang dianimasikan. Pada layar kecil, cerita dipadatkan agar email dan kata sandi segera terlihat.

### 3. Tombol berbeda menurut pekerjaan

| Fungsi | Bentuk yang diusulkan | Gerak dan keadaan |
| --- | --- | --- |
| CTA landing | Blok solid dengan bidang kecil untuk panah di ujung | Panah bergeser 2–3 px saat hover; hitbox diam |
| Login pelanggan | Lebar penuh, warna pelanggan yang kontras | Teks berubah ketika memeriksa akun; tidak memantul |
| Login staf | Lebar penuh, struktur persegi lunak, ikon akses | Aksen garis atau warna berubah; form tetap diam |
| Kategori/filter | Chip berbingkai dengan ikon/kategori yang bermakna | Terpilih memakai isi + tanda centang, tidak hanya warna |
| Urutan hasil | Kontrol persegi dengan label “Urutkan” dan chevron | Dropdown native atau kontrol yang menjaga keyboard |
| Tambah barang | Tombol ringkas dengan plus yang mudah dikenali | Centang muncul hanya setelah mutasi berhasil |
| Kuantitas | Sepasang tombol persegi dalam satu stepper | Batas stok/angka nol mengubah disabled secara nyata |
| Hapus/reset | Tombol teks yang jelas atau warna bahaya sesuai konsekuensi | Tidak memakai gaya CTA pembelian |
| Prev/next carousel | Tombol ikon dengan ukuran sentuh tetap | Menunjukkan arah; tanpa animasi magnetik |

Keunikan berasal dari perbedaan peran, proporsi, warna, dan detail; bukan setiap tombol mendapat gaya acak. Fokus terlihat pada semua keluarga.

### 4. Rotasi otomatis tanpa tombol “Mulai” yang dominan

Pertahankan autoplay saat area terlihat dan preferensi gerak mengizinkan. Letakkan angka `01 / 03`, garis progres yang tenang, dan ikon jeda/putar kecil dalam satu baris utilitas. Ikon memiliki nama aksesibel serta tooltip yang menjelaskan tindakan berikutnya. Area klik tetap sekitar 44 px; warna/border lebih tenang daripada CTA koleksi. Kontrol putar tidak perlu label besar “Mulai”.

Fokus, drag, atau navigasi manual menghentikan autoplay sampai ada tindakan memulai kembali. Hover hanya menjeda sementara. Tab browser tersembunyi dan carousel di luar viewport menghentikan timer. Reduced motion berarti awal statis dan perpindahan manual langsung. Jangan menyembunyikan kontrol jeda hanya untuk pengguna keyboard, karena orang yang menggunakan pointer juga dapat membutuhkan jeda.

Jika seluruh kontrol rotasi benar-benar dihapus, alternatif yang menjaga kemampuan pengguna adalah carousel manual atau konten statis. Menghapus kontrol sambil mempertahankan pergantian otomatis terus-menerus mengorbankan aksesibilitas; itu bukan penyederhanaan yang disarankan.

### 5. Geometri dan keterbacaan sebagai syarat rilis

Periksa 320, 390, 768, dan desktop; tinggi layar 720 px untuk login; judul panjang; fokus keyboard; reduced motion; hover; perubahan ukuran saat animasi berjalan; pemilihan cepat; serta keadaan form error/loading. Foto boleh dipotong secara artistik, tetapi teks, fokus, tooltip, dan tombol tidak boleh terpotong. Jangan memakai `overflow:hidden` pada induk hanya untuk menyembunyikan kesalahan layout.

Konten harus tetap terlihat tanpa JavaScript animasi. Batasi gerak satu kali pada pembukaan, satu perubahan yang berkaitan dengan pilihan, dan umpan balik aksi. Tidak ada scroll hijack, infinite bobbing, cursor khusus yang wajib, blur yang menghalangi baca, atau pengejaran pointer di form login.

## Ukuran keberhasilan yang dapat diperiksa

1. Tombol besar “Mulai” tidak tampil sebagai CTA; autoplay dan kendali jeda tetap dapat dipahami.
2. Screenshot kedua portal memperlihatkan cerita dan komposisi berbeda sebelum teks kecil dibaca.
3. Satu interaksi landing mengubah isi atau tujuan yang nyata, bukan hanya dekorasi.
4. Fungsi filter, navigasi, login, dan pembelian mempunyai hierarki visual berbeda namun konsisten.
5. Tidak ada horizontal overflow, teks tertutup, fokus terpotong, atau input bergerak saat pengguna bekerja.

Riset ini tidak menjanjikan penghargaan, 60fps di semua perangkat, atau peningkatan konversi. Pemeriksaan yang benar-benar dijalankan harus dilaporkan terpisah dari rekomendasi ini.
