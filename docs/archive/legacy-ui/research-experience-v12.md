# Riset UI dan animasi Unit Toko v12

Tanggal: 23 September 2026 (Asia/Jakarta). Sasaran: pelanggan yang mencari, membandingkan kemasan, lalu memesan kebutuhan pantry, rapat, dan merchandise. Ukuran keberhasilan desain: pilihan lebih mudah dipindai, tindakan jelas, gerakan dapat dikendalikan, dan teks tidak tertutup.

Riset ini membaca sumber primer dan kode v11. Pengamatan situs referensi berasal dari struktur/konten halaman yang tersedia melalui web, bukan pengukuran visual, pengujian pengguna, atau benchmark performa. Temuan tata letak Unit Toko di bawah membedakan fakta kode dari risiko yang perlu diverifikasi lewat browser. Screenshot pengguna memperlihatkan titik oranye pada label etalase; titik tersebut merupakan elemen HTML, bukan bagian foto produk.

## Temuan paling berguna dari sumber primer

| Sumber, diakses 23 September 2026 | Pengamatan | Implikasi untuk Unit Toko |
| --- | --- | --- |
| [W3C APG — Carousel pattern](https://www.w3.org/WAI/ARIA/apg/patterns/carousel/) | Rotasi otomatis memerlukan tombol mulai/berhenti, sebelumnya/berikutnya, serta berhenti saat hover. Fokus keyboard menghentikan rotasi sampai pengguna meminta mulai lagi. Kontrol rotasi mendahului isi dalam urutan fokus. Slide tersembunyi perlu benar-benar dikeluarkan dari interaksi pembaca layar. | Pakai carousel sederhana dengan kontrol nyata; tombol tetap di tempatnya. Label kontrol menjelaskan tindakan berikutnya. Jangan memindah fokus saat berganti slide. Gunakan `aria-live="off"` saat berputar otomatis dan pengumuman yang tenang saat dikendalikan manual. |
| [W3C WCAG 2.2 — Pause, Stop, Hide](https://www.w3.org/WAI/WCAG22/Understanding/pause-stop-hide.html) | Konten yang bergerak atau memperbarui diri dapat mengganggu pembacaan. Berhenti hanya selama fokus/hover tidak menggantikan mekanisme pause yang dapat dipertahankan pengguna. | Sediakan Pause/Play persisten. Satu area berputar cukup; beberapa animasi otomatis yang bersaing menambah beban perhatian dan kontrol. |
| [WAI — Carousel animations](https://www.w3.org/WAI/tutorials/carousels/animations/) | Mengganti keseluruhan tombol saat mengubah Play menjadi Stop bisa menghilangkan fokus. Teks/kontrol slide yang sedang transisi tidak boleh menimbulkan konteks ganda untuk teknologi bantu. | Pertahankan identitas DOM tombol. Catatan: contoh tutorial lama memulai lagi setelah `focusout`; untuk perilaku fokus v12 ikuti APG yang meminta Play eksplisit. |
| [web.dev — High-performance animations](https://web.dev/articles/animations-guide) | Transform dan opacity lebih cocok untuk gerakan; perubahan ukuran/posisi yang memicu layout perlu dihindari. `will-change` berlebihan dapat menimbulkan biaya tersendiri. | Geser lapisan visual di dalam bingkai stabil. Jangan menganimasikan tinggi kartu, margin katalog, atau posisi tombol beli. Profilkan sebelum menambah promosi lapisan. |
| [web.dev — prefers-reduced-motion](https://web.dev/articles/prefers-reduced-motion) | Gerak fungsional memberikan umpan balik; gerak dekoratif, zoom, dan parallax dapat mengganggu sebagian pengguna. Preferensi dapat berubah saat halaman terbuka. | Reduced motion berarti carousel awalnya diam dan perpindahan manual seketika; isi, harga, kontrol, dan konfirmasi tetap tersedia. Hentikan timer saat preferensi berubah. |
| [MUJI UK — toko resmi](https://uk.muji.eu/) | Navigasi mengelompokkan kebutuhan konkret seperti drinkware, stationery, food, dan travel. Halaman memisahkan pilihan kategori, rangkaian produk, serta cerita penggunaan. | Gunakan kebutuhan nyata Unit Toko untuk discovery; judul dan gambar harus menuju kategori/produk yang sesuai. Hindari label promosi yang tidak didukung data demo. |
| [IKEA — daftar mug](https://www.ikea.com/us/en/cat/mugs-cups-16045/) dan [detail IKEA 365+](https://www.ikea.com/us/en/p/ikea-365-mug-white-80278367/) | Daftar menyediakan jumlah hasil, sort/filter, harga, ukuran dan batas pembelian. Detail memisahkan nama/harga/pilihan ukuran, cara memperoleh barang, informasi material, ukuran, dan kemasan. | Prioritaskan identitas produk, harga per kemasan, stok, jumlah, subtotal, lalu tindakan. Letakkan SKU dan rincian sekunder di bawah. Ambil hierarki informasinya; jangan meniru rating, jaminan, atau janji pengiriman IKEA. |
| [GSAP — showcase resmi](https://gsap.com/showcase/) | Galeri memuat karya studio/portfolio dan beragam penggunaan animasi. Daftar tersebut bukan bukti bahwa efek tertentu meningkatkan transaksi atau cocok untuk setiap toko. | Gunakan sebagai referensi kualitas orkestrasi, bukan alasan menyalin scroll hijacking atau menyebut Unit Toko pemenang penghargaan. |

## Audit kritis v11

1. **Titik oranye tidak menjelaskan apa pun.** `CatalogEditorial` memakai `<span />` kosong pada `.shop-editorial-eyebrow`; CSS memberinya ukuran dan warna. Hapus elemen dan aturan tersebut. Indikator stok memiliki fungsi berbeda; jangan menghapus informasi status secara membabi buta.
2. **Kartu bertumpuk menciptakan risiko nyata label terpotong.** `.shop-shelf-product` adalah tautan produk berposisi absolut, berotasi, dan saling bertumpuk. Induk `.shop-editorial` memakai `overflow: hidden`, sedangkan rak mobile hanya setinggi 204px. Nama panjang dapat melampaui ruang atau tertutup kartu lain tanpa menambah tinggi induk. Ini fakta struktur; tingkat clipping pada tiap lebar harus diperiksa secara visual. Solusi struktural: teks dalam alur normal dan lapisan gambar memiliki ruang khusus, bukan menambah `z-index`.
3. **Mint dan kotak membulat dipakai terlalu merata.** Hero, syarat detail, informasi pembayaran, navigasi akun, dompet, dan profil memakai bahasa permukaan yang mirip. Akibat yang diperkirakan: perhatian sulit dibedakan. Batasi warna kuat pada discovery dan status terpilih; area transaksi menggunakan permukaan netral, garis pembatas, dan hierarki tipe.
4. **Gerak katalog belum menunjukkan hubungan yang penting.** `ShopMotion` mengulang masuknya foto saat `refreshKey` berubah; produk hero juga dirotasi. Hasilnya aktif secara visual, tetapi tidak menambah cara menemukan barang. Carousel pilihan yang nyata serta konfirmasi setelah penambahan berhasil lebih relevan.
5. **Detail produk memiliki fungsi dasar, tetapi perlu penataan ulang.** Kode sudah menyediakan pilihan kemasan, kuantitas, subtotal, batas stok, tambah ke keranjang, beli sekarang, dan rincian. Jangan menduplikasi fungsi tersebut dalam kartu tambahan. Besarkan area foto dengan rasio stabil; sederhanakan kolom beli; rapikan informasi pengiriman/invoice sebagai dua baris layanan. SKU tetap tersedia tanpa mendahului keputusan beli.

## Lima perubahan prioritas yang dapat diterapkan sekarang

1. **Ganti tumpukan hero dengan carousel koleksi/produk yang berguna.** Satu bingkai dengan gambar penuh dan teks terpisah; setiap pilihan memiliki nama, konteks singkat, dan tautan yang benar. Rotasi otomatis hanya pada tampilan discovery awal; pencarian, filter, dan halaman lanjutan tetap langsung ke hasil.
2. **Buat ritme visual yang lebih tegas.** Hapus titik dekoratif; gunakan petrol untuk struktur/aksi, latar terang untuk barang, dan satu bidang aksen pada discovery. Kartu katalog tidak memerlukan lingkaran panah tambahan jika foto/judul sudah jelas bisa dipilih. Pertahankan ruang foto dan baseline harga yang konsisten tanpa memotong nama secara paksa.
3. **Susun ulang detail untuk keputusan beli.** Urutan nama → harga/satuan → stok → kemasan → jumlah/subtotal → tindakan. Ringkasan layanan dan rincian berada sesudahnya. Pada mobile, bilah tindakan tidak boleh bertumpuk dengan navigasi bawah, pesan error, atau akhir konten.
4. **Hubungkan animasi dengan keadaan aplikasi.** Masuk sekali untuk area editorial, perpindahan carousel terarah, foto kartu responsif saat hover, dan konfirmasi keranjang hanya setelah penyimpanan berhasil. Hentikan rotasi ketika pengguna sedang membaca, memilih, atau tab tidak terlihat. Kontrol pembelian langsung bisa dipakai.
5. **Uji geometri dan kontrol sebagai syarat rilis.** Periksa 320, 375/390, 768, 1024, dan desktop lebar; nama panjang; empty/error; kuantitas tidak valid; dan fokus keyboard. Pastikan tidak ada horizontal overflow, label tertutup, target keluar bingkai, atau tombol bawah menutupi konten. Verifikasi reduced motion dan kontrol pause, bukan hanya membaca media query.

## Peta gerakan yang disarankan

Durasi berikut merupakan keputusan desain untuk diuji, bukan angka yang diwajibkan standar.

| Area | Pemicu dan tujuan | Gerak/durasi | Reduced motion |
| --- | --- | --- | --- |
| Discovery | Masuk pertama: mengenalkan satu koleksi | Foto 12–20px + opacity, 450–600ms; teks tetap terbaca | Langsung tampil |
| Carousel | Diam 6–7 detik atau tombol Next/Previous: memperlihatkan pilihan berikutnya | Translate horizontal 450–650ms, arah konsisten, bingkai tetap | Tidak autoplay; pergantian manual langsung |
| Kartu produk | Hover pointer presisi: memperjelas foto aktif | Scale foto maksimal 1.025–1.04, 180–250ms; kartu/tombol tidak bergeser | Warna/focus ring saja |
| Filter | Hasil berubah: menegaskan hasil baru | Opacity singkat maksimal 180ms; tidak menunda klik | Langsung tampil |
| Keranjang | Mutasi berhasil: konfirmasi tindakan | Tanda centang + badge singkat 180–300ms; teks status tetap tersedia | Tanda centang dan teks langsung |
| Detail | Ganti kemasan: menjaga hubungan gambar dan harga | Foto crossfade 150–200ms; harga/jumlah tidak melompat | Perubahan langsung |
| Landing/login | Masuk halaman: satu pembukaan visual | Foto/latar terkoordinasi 500–800ms, tanpa input bergerak | Tampilan statis lengkap |

Untuk carousel: tombol pause menjadi kontrol pertama; klik manual/fokus menghentikan autoplay sampai Play; hover menghentikan sementara; timer berhenti saat offscreen/halaman tersembunyi; jangan mengejar slide yang terlewat saat tab kembali. Slide yang tidak aktif tidak boleh menerima fokus. Pada perubahan ukuran atau navigasi, cleanup harus menghapus timer/listener/tween lama. Tidak perlu marquee tanpa akhir, cursor khusus, atau scroll yang direbut dari browser.

## Batas klaim

Laporan ini tidak membuktikan kenaikan konversi, skor aksesibilitas menyeluruh, 60fps, ataupun penghargaan desain. Bellroy tidak dapat dibuka melalui alat riset sehingga tidak digunakan sebagai dasar. Rilis harus melaporkan pemeriksaan yang benar-benar dilakukan serta sisa keterbatasan; data dummy tidak boleh diberi rating atau promo fiktif untuk membuat halaman tampak ramai.
