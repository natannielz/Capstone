# Riset v14 — landing, identitas bersama, dan footer

Tanggal pemeriksaan: 23 September 2026. Dokumen ini adalah hasil riset dan rekomendasi untuk penyusunan rencana; belum merupakan implementasi v14.

## Kesimpulan utama

Prioritas tertinggi adalah menyatukan footer dan memperjelas hirarki navigasinya. Footer toko saat ini memakai tiga kolom setara untuk identitas, tiga tautan tanpa kelompok, dan keterangan simulasi. Keterangan tersebut memperoleh bobot ruang yang sama dengan navigasi. Memperbesar logo atau menambahkan animasi tidak menyelesaikan masalah ini.

Pertahankan monogram U/T, warna petroleum, fotografi produk, serta pemisahan akses pelanggan dan internal. Arah berikutnya adalah **toko kebutuhan kerja dengan penyajian editorial yang mudah ditelusuri**: satu identitas kuat, koleksi yang konkret, tindakan yang konsisten, dan penutup halaman yang membantu orang melanjutkan tugasnya.

Landing v13 sudah memiliki komposisi tersendiri. Masalah lanjutannya ialah pengulangan dan kepadatan perjalanan di ponsel: tiga bab panjang, beberapa lapis teks untuk koleksi yang sama, foto hero yang dipakai kembali, dan footer yang berbeda keluarga visual dengan toko. Perbaikan berikutnya perlu menajamkan struktur tersebut, bukan mengganti gaya kembali.

## Metode dan batas bukti

Yang diperiksa langsung: `public-landing.tsx`, `landing-v13.css`, `public-motion.tsx`, `shop-shell.tsx`, aturan footer pada `storefront-v7.css`/`storefront-v11.css`, footer pada `landing-v8.css`, serta `brand.tsx`/`brand-v8.css`. Rute akun dan pesanan juga diperiksa untuk memastikan usulan tautan memiliki tujuan nyata.

Empat situs ritel resmi ditelaah melalui konten halaman dan struktur tautan yang dikembalikan alat web. Tidak ada pengendalian browser atau screenshot baru pada subtask ini. Karena itu, referensi di bawah mendukung pola konten dan arsitektur informasi; riset ini **tidak** mengklaim telah mengukur animasi, posisi pixel, warna layar, atau kualitas mobile situs referensi. Gambaran footer yang diberikan pengguna konsisten dengan struktur dan CSS toko, tetapi tidak diperlakukan sebagai hasil pengukuran layar baru.

Tidak ada data konversi, rekaman pengguna, atau hasil pengujian pengguna. Dampak terhadap kemudahan pakai di bawah merupakan penilaian desain yang perlu divalidasi, bukan peningkatan konversi yang telah terbukti. Istilah kualitas setara situs penghargaan adalah standar ambisi pengerjaan, bukan klaim memperoleh penghargaan.

## Empat referensi ritel primer

| Referensi resmi | Yang benar-benar teramati | Pelajaran yang dapat diterapkan | Yang tidak perlu ditiru |
|---|---|---|---|
| [Bellroy](https://bellroy.com/) | Navigasi mengelompokkan produk menurut aktivitas dan jenis; halaman menghubungkan cerita penggunaan dengan koleksi; bagian akhir memisahkan bantuan, produk, penelusuran koleksi, dan informasi perusahaan. | Pisahkan kebutuhan belanja, tugas akun, dan informasi toko. Editorial harus berakhir pada tujuan produk yang jelas. | Banyak kategori, newsletter, klaim reputasi, garansi, serta statistik bisnis milik Bellroy. Unit Toko tidak memiliki dasar untuk menampilkan klaim serupa. |
| [IKEA Indonesia](https://www.ikea.co.id/en) | Ada penelusuran produk dan ruang, tautan pelacakan pesanan, bagian inspirasi/layanan, dan tautan bantuan pada akhir halaman. Konten video juga menampilkan kontrol jeda dan transkrip dalam hasil halaman. | Pisahkan inspirasi dari tugas praktis. Pelacakan pesanan dan cara berbelanja layak mendapat jalur yang mudah ditemukan. Konten bergerak harus memiliki kontrol yang relevan. | Skala menu besar, program keanggotaan, kebijakan retur, promosi, dan layanan yang tidak tersedia di aplikasi ini. |
| [MUJI USA](https://www.muji.us/) | Kategori konkret mencakup alat tulis, kebutuhan rumah, minuman dan makanan; pilihan editorial mengarah ke kategori, disertai jalur pencarian/akun. | Nama barang/kebutuhan yang dikenali lebih berguna daripada label pemasaran saja. Tiga koleksi Unit Toko perlu tetap terlihat dan langsung menuju katalog. | Struktur kategori puluhan cabang, penawaran musiman dan besarnya katalog MUJI. Hasil halaman yang diambil tidak menyediakan footer lengkap; footer MUJI tidak menjadi dasar rekomendasi. |
| [UNIQLO Indonesia](https://www.uniqlo.com/id/en/) | Hasil halaman menyediakan penelusuran menurut kelompok pengguna serta bagian pencarian kategori dengan nama produk spesifik. Ekstraksinya terbatas dan tidak menampilkan footer lengkap. | Jalur langsung menuju kategori perlu tetap dapat ditemukan tanpa membaca seluruh cerita landing. | Footer, detail transisi, atau tata letak visual yang tidak teramati; promosi instalasi aplikasi tidak relevan untuk website ini. |

Kesamaan yang berguna adalah adanya tujuan navigasi yang jelas dan hubungan antara cerita produk dengan kegiatan belanja. Tidak ada bukti dari keempat referensi yang mengharuskan lebih banyak efek, footer yang sangat besar, pergantian logo, ataupun penambahan carousel kedua.

## Temuan source dan rekomendasi prioritas

| Prioritas | Bukti pada aplikasi sekarang | Dampak yang diperkirakan | Rekomendasi spesifik |
|---|---|---|---|
| P1 | Footer toko, `shop-shell.tsx:446`, terdiri atas identitas, sebuah `div` dengan tiga tautan, dan paragraf simulasi. CSS desktop memakai `1fr 1fr 1fr`. | Tidak ada petunjuk kelompok; disclaimer berkompetisi dengan tugas pengguna. Ini masalah struktur, bukan panjang kalimat semata. | Buat footer bersama dengan area identitas dan kelompok bernama. Pisahkan disclosure menjadi baris bawah selebar kontainer. |
| P1 | `.shop-app` menggunakan latar `#F5F7F8`; footer tidak memberikan bidang latar tersendiri. Landing menggunakan footer petroleum. | Akhir halaman toko terasa seperti sisa konten dan identitas berubah antara landing dan belanja. | Gunakan bidang petroleum yang sama pada footer landing/toko, dengan kontainer isi sejajar grid halaman. Pilih satu token warna dan satu struktur, bukan menambah override khusus per versi. |
| P1 | Pada mobile, footer toko mengubah tautan menjadi flex row yang membungkus; ukuran teks dasar 11 px dan disclosure 10 px. | Tautan mudah terlihat seperti catatan tambahan; pembungkusan dapat menghasilkan jarak yang tidak teratur. | Gunakan kolom kelompok eksplisit, tautan 13–14 px dan baris sentuh yang cukup tinggi. Disclosure setidaknya 12 px sebagai keputusan desain, tanpa mengubah isi penting. |
| P1 | Navigasi landing disembunyikan pada lebar ≤800 px; tersisa logo dan tombol menuju akses di bawah halaman. | Jalur langsung ke koleksi/proses tidak sejelas desktop. | Sediakan menu mobile ringkas dengan tujuan yang sama, atau dua jalur ringkas yang tetap terlihat. Pilih setelah diuji di 320 px; jangan memaksa tiga tautan kecil ke satu baris. |
| P2 | Landing memiliki label koleksi, judul dua baris, paragraf, CTA, detail jenis barang, dan foto pada masing-masing tiga bab. Pada mobile semuanya tersusun vertikal. | Banyak pemindaian ulang sebelum mencapai proses dan akses; cerita terasa lebih panjang daripada pilihan yang tersedia. | Pertahankan tiga bab, tetapi setiap bab mobile cukup memiliki nama kebutuhan, satu kalimat manfaat konkret, satu foto, dan satu CTA. Hilangkan detail jenis barang yang mengulang paragraf, atau jadikan label singkat di caption. |
| P2 | Hero dan bab merchandise menggunakan `hero-still-life-v3.png` yang sama. | Pengulangan foto melemahkan perbedaan antarbagian. | Pertahankan foto hero; beri merchandise komposisi khusus dari foto barang yang sudah ada atau satu foto editorial baru bila dibutuhkan. Jangan menambahkan objek melayang di atas teks. |
| P2 | Label utility/kicker berada di rentang 10–11 px; navigasi koleksi mobile menjadi 10–11 px meskipun tinggi barisnya besar. | Ruang tersedia tidak digunakan untuk keterbacaan. | Naikkan ukuran label navigasi ke 12–13 px, izinkan dua baris dengan tinggi stabil, dan uji teks yang diperbesar. Ukuran ini rekomendasi, bukan batas minimum universal WCAG. |
| P2 | Foto memiliki depth, copy memiliki stagger, bab memiliki progress rule dan tautan aktif; header/hero juga memiliki beberapa entrance. | Hirarki gerak dapat terasa seragam atau ramai meskipun tiap efek kecil. | Tetapkan satu momen utama pada hero. Bab cukup memakai salah satu penegas progres yang jelas dan depth ringan; hentikan pengulangan reveal pada setiap label/detail. |
| P2 | Footer landing dan toko didefinisikan terpisah dan diwarisi dari beberapa berkas CSS versi sebelumnya. | Perbaikan salah satu footer tidak otomatis memperbaiki yang lain; drift visual lebih mudah terjadi. | Satu komponen footer dan satu stylesheet bertoken dengan varian penuh/ringkas, tanpa selector `nth-child` sebagai kontrak struktur. |

Prioritas di atas bukan daftar bug runtime yang sudah direproduksi. Misalnya, kecilnya tautan footer adalah risiko keterbacaan/target yang perlu diukur di browser sebelum menyatakan kegagalan aksesibilitas.

## Rancangan footer yang direkomendasikan

Tujuan footer: mengembalikan pengguna ke barang, membantu tugas akun, lalu menjelaskan konteks situs. Bukan hero kedua dan bukan tempat memuat promosi palsu.

Desktop:

```
bidang petroleum, isi sejajar grid halaman

Unit Toko + monogram       Belanja          Akun Anda        Unit Toko
Pantry, rapat,             Semua produk     Keranjang        Tentang toko
dan kebutuhan tim.        Pantry           Pesanan saya     Cara berbelanja
                          Rapat            Profil & alamat  Portal internal
                          Merchandise

─────────────────────────────────────────────────────────────────────
Demo capstone · Bukan situs resmi BNI.
Data, foto, pengiriman, dan pembayaran disimulasikan.
```

Gunakan identitas sekitar sepertiga lebar total, lalu tiga kelompok yang lebih kecil; batas kontainer mengikuti konten utama. Jarak atas/bawah desktop 40–48 px dan jarak antararea 24–36 px cukup sebagai titik awal. Tidak perlu logo raksasa, ikon pada setiap tautan, nomor bagian, newsletter, atau deretan logo pembayaran.

Pada lebar 320–540 px: identitas satu baris/blok, kemudian dua kolom kelompok pendek. Kelompok ketiga dapat berada di bawah dengan judulnya sendiri. Pertahankan kelompok statis terlebih dahulu karena hanya ada sedikit tautan; accordion hanya layak jika isinya benar-benar bertambah. Keterangan simulasi berada setelah divider, tidak disembunyikan dalam accordion. Sisakan ruang terhadap navigasi/action bar bawah agar tautan terakhir tetap dapat disentuh.

Gunakan `nav` dengan label yang unik atau satu `nav` berisi kelompok berjudul dan daftar. Ini membuat struktur penelusuran juga tersedia di luar presentasi visual. [Panduan region W3C](https://www.w3.org/WAI/tutorials/page-structure/regions/) menjelaskan penggunaan region/footer/navigation serta penamaan beberapa landmark sejenis.

### Peta tautan yang tersedia sekarang

| Kelompok | Label | Tujuan nyata |
|---|---|---|
| Belanja | Semua produk | `/shop` |
| Belanja | Pantry | `/shop?collection=pantry` |
| Belanja | Kebutuhan rapat | `/shop?collection=rapat` |
| Belanja | Merchandise | `/shop?collection=merchandise` |
| Akun Anda | Keranjang | `/cart` |
| Akun Anda | Pesanan saya | `/account/orders` — rute sudah memeriksa sesi pelanggan |
| Akun Anda | Profil & alamat | `/account` atau tujuan login pelanggan yang mempertahankan tujuan kembali |
| Unit Toko | Tentang toko | `/` |
| Unit Toko | Cara berbelanja | `/#layanan` |
| Unit Toko | Portal internal | `/staff/login`, tampil sebagai akses terpisah dan tersier |

Jangan membuat link bantuan, kontak WhatsApp, kebijakan retur, syarat komersial atau alamat toko yang belum memiliki isi dan dasar nyata. Jika rencana ingin menambahkan FAQ, buat kontennya dari kemampuan demo yang benar-benar ada dan pastikan tujuannya tersedia sebelum memasukkan link ke footer.

Varian ringkas untuk checkout dapat mempertahankan identitas, bantuan/cara berbelanja yang relevan, akses kembali, dan disclosure. Ia tetap menggunakan sistem yang sama tetapi tidak perlu menampilkan seluruh kelompok kategori ketika pengguna sedang menyelesaikan pesanan. Ini keputusan fokus tugas, bukan pengecualian kualitas visual.

## Identitas dan tipografi

Monogram yang ada adalah satu path SVG: T di dalam U, berwarna tunggal, dan digunakan bersama oleh berbagai halaman. Tidak ditemukan alasan fungsional pada audit source ini untuk menggantinya lagi. Yang lebih bernilai adalah konsistensi ukuran, ruang bebas, kontras, dan teks pendamping.

Pertahankan ikon, kata Unit Toko, dan warna petroleum. Untuk footer, hilangkan teks konteks kecil yang sekadar mengulang fungsi halaman bila sudah ada kalimat penjelasan di bawahnya. Jangan mengubah arti link logo: logo toko menuju `/shop`; logo landing menuju `/`. Label aksesibilitas harus tetap sesuai tujuan.

Pertahankan Archivo Black untuk satu headline utama, DM Sans untuk isi dan kontrol. Kurangi kebutuhan uppercase yang rapat pada bagian bawah halaman. Tetapkan satu skala: judul kelompok footer 13 px/semibold, tautan 14 px, penjelasan/disclosure 12–13 px, dengan line-height sekitar 1.5–1.7. Perbedaan peran lebih penting daripada menambah jenis font ketiga.

Petroleum penuh pada footer merupakan perluasan identitas yang sudah ada di landing, bukan pergantian tema. Warna hangat tetap terbatas pada koleksi atau penanda tindakan tertentu. Jangan membawa tiga warna koleksi sekaligus ke footer; penutup yang lebih tenang membuat bagian fotografi tetap menjadi fokus.

## Gerak yang layak dipertahankan atau diubah

| Area | Perlakuan yang disarankan | Batas penting |
|---|---|---|
| Hero landing | Satu rangkaian: headline → pembukaan foto. Intro dan CTA segera dapat dipakai. | Hindari mengulang curtain pada navigasi kembali jika membuat pengalaman terasa memulai ulang; perlu pengujian perilaku riwayat, bukan asumsi. |
| Koleksi | Native scroll, photo depth kecil di dalam frame, penanda bab aktif yang jelas. | Tidak perlu progress line dan perubahan dekoratif lain yang menyampaikan hal identik. Hindari memindahkan tombol. |
| Footer | Perubahan underline/warna 150–200 ms pada hover/focus. | Tidak ada entrance yang membuat link sempat hilang, marquee, logo berputar, ataupun auto-slide. |
| Mobile | Gerak lebih pendek dan sederhana. | Jangan memindahkan kategori atau target sentuh saat jari akan memilihnya. |
| Reduced motion | Semua isi tetap terlihat; kurangi/hapus transform dekoratif. | Informasi dan kontrol tidak boleh bergantung pada timeline yang berjalan. |

[web.dev tentang motion dan aksesibilitas](https://web.dev/learn/accessibility/motion) menjadi dasar untuk alternatif reduced motion, bukan alasan menambahkan animasi ke setiap area. Keputusan timing spesifik di atas adalah rekomendasi desain.

Implementasi v13 sudah menghapus refresh pada setiap image-load setelah diketahui dapat mengganggu native anchor scroll. Pertahankan perbaikan itu. Footer baru tidak memerlukan ScrollTrigger sama sekali. Jika highlight bab tetap digunakan, pertimbangkan state semantik `aria-current="location"` pada anchor aktif, bukan hanya kelas warna; lifecycle-nya harus ikut dibersihkan ketika halaman atau preferensi gerak berubah.

## Kriteria validasi sebelum menyebut perbaikan selesai

1. Bandingkan landing, katalog, detail produk, keranjang, akun, pesanan, serta checkout: footer dan lockup terasa satu keluarga, dengan varian ringkas hanya ketika tugasnya membutuhkan.
2. Uji 320, 375, 768, 1024, dan 1440 px serta pembesaran teks. Tidak ada dua arah scroll untuk membaca teks, judul kelompok terpotong, atau logo mendorong link keluar kontainer. [W3C reflow](https://www.w3.org/WAI/WCAG22/Understanding/reflow.html) memberi acuan untuk lebar setara 320 CSS px.
3. Ukur target link, gap, dan focus ring nyata. Target tinggi 44 px adalah sasaran kenyamanan desain; [WCAG 2.2 target size minimum](https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum.html) memiliki batas 24 CSS px dan pengecualian, sehingga jangan menyamakan rekomendasi 44 px dengan persyaratan AA tersebut.
4. Pastikan `Tab` mengikuti kelompok yang terbaca, footer mudah ditemukan melalui landmark, dan action bar mobile tidak menutupi target terakhir.
5. Klik semua link sebagai pengunjung dan pelanggan. Rute pesanan/akun harus membawa pengunjung ke login yang benar dan mempertahankan tujuan, sementara portal internal tetap terpisah.
6. Uji anchor koleksi dengan foto lambat dimuat, preferensi reduced motion, refresh, dan tombol Back. Hash dan bagian yang tampak harus sesuai.
7. Periksa kontras warna hasil implementasi, bukan hanya nilai token usulan. Teks disclosure harus tetap nyaman dibaca pada tema footer gelap.
8. Nilai before/after dengan tugas konkret: menemukan pantry, membuka pesanan, kembali ke beranda, dan memilih portal yang benar. Klaim peningkatan kualitas didasarkan pada hasil ini serta konsistensi visual, bukan pada banyaknya efek.

## Urutan yang paling masuk akal untuk rencana berikutnya

Mulai dari kontrak footer bersama dan lockup, lalu rapikan hirarki/mobile landing, sesudah itu selaraskan tata bahasa gerak. Jangan memulai ulang logo, palet, dan seluruh hero sekaligus. Ini menjaga identitas yang telah terbentuk sambil memperbaiki bagian yang paling jelas belum menyatu.
