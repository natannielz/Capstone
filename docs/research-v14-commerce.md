# Riset v14 — alur belanja pelanggan

Tanggal: 23 September 2026. Cakupan: katalog, detail produk, keranjang, checkout, serta keadaan kosong, gagal, dan stok berubah. Metode: audit sumber aplikasi dan pembacaan sumber primer di bawah. Laporan ini belum merupakan hasil uji browser, pembandingan konversi, pengukuran Core Web Vitals, atau audit WCAG menyeluruh. Tidak ada perubahan transaksi/data pada tahap riset.

## Kesimpulan desain

Kualitas alur transaksi sudah lebih matang daripada kesan visualnya. Prioritas paling berguna adalah memperjelas pilihan, status, dan tindakan berikutnya sambil menyatukan hierarki visual antarlaman. Jangan mengganti cart store, kontrak checkout, atau pengamanan akun hanya untuk membuat tampilan terasa baru. Presentasi etalase boleh lebih ekspresif; jumlah, harga, kemasan, validasi, dan tombol transaksi harus tenang dan mudah dibaca.

Subjek desain adalah Unit Toko untuk kebutuhan pantry, rapat, dan merchandise. Gagasan khas yang disarankan: **lembar pesanan toko**. Label produk dan kemasan menyerupai label inventaris yang rapi; ringkasan belanja menyerupai nota dengan angka rata kanan dan garis pemisah jelas. Bukan antarmuka marketplace penuh promosi palsu. Tidak menambahkan penilaian bintang, diskon, stok mendesak, atau angka penjualan tanpa data.

## Bukti dari sumber primer

| Sumber yang dibaca | Temuan sumber | Implikasi untuk Unit Toko — penilaian desain, bukan klaim sumber |
| --- | --- | --- |
| [W3C WAI: pemberitahuan formulir](https://www.w3.org/WAI/tutorials/forms/notifications/) | Kesalahan perlu dijelaskan singkat, disertai cara memperbaiki; label dan pesan dapat dihubungkan; fokus dapat diarahkan ke input bermasalah. | Pertahankan validasi checkout yang sudah menghubungkan pesan dengan field. Tambahkan konteks pada tombol nonaktif dan pertahankan data saat gagal. |
| [W3C: ukuran sasaran minimum](https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum.html) | WCAG 2.2 AA menetapkan minimum 24 × 24 CSS px atau pengecualian jarak yang dijelaskan. Sasaran lebih besar membantu banyak pengguna. | Gunakan tinggi praktis 44 px untuk chip yang bisa dihapus, kontrol jumlah, dan tindakan utama; ini pilihan produk, bukan klaim bahwa AA selalu mewajibkan 44 px. |
| [W3C: fokus tidak tertutup](https://www.w3.org/WAI/WCAG22/Understanding/focus-not-obscured-minimum.html) | Header/footer lengket dapat menutupi kontrol yang mendapat fokus; scroll padding adalah salah satu teknik mitigasi. | Bilah beli bawah perlu diperiksa dengan keyboard pada detail, keranjang, checkout; ruang bawah saja belum membuktikan fokus selalu terlihat. |
| [W3C WAI: formulir beberapa tahap](https://www.w3.org/WAI/tutorials/forms/multi-page/) | Indikasi progres membantu pengguna memahami lokasi dalam alur panjang dan langkah tersisa. | Tampilkan alur nyata Belanja → Keranjang → Periksa pesanan. Untuk Beli sekarang, tampilkan hanya Produk → Periksa pesanan agar tidak menyatakan langkah keranjang telah dijalani. Tidak perlu memecah formulir pendek menjadi wizard. |
| [Shopify: pedoman desain aplikasi](https://shopify.dev/docs/apps/design) | Pedoman menekankan pengalaman yang mudah diprediksi, adaptasi mobile, konsistensi, dan aksesibilitas. Cakupannya aplikasi Shopify/admin, bukan resep visual storefront ini. | Pertahankan bahasa tindakan dan hierarki yang sama dari katalog sampai checkout. Bedakan keluarga tombol menurut fungsi, bukan setiap tombol menjadi gaya acak. |
| [Baymard: ringkasan filter aktif](https://baymard.com/research-articles/how-to-design-applied-filters) | Riset primer mereka menghubungkan ringkasan filter dengan konfirmasi pilihan, kemudahan melepas pilihan, dan pemahaman cakupan hasil. | Chip aktif harus dapat dihapus satu per satu; jumlah aktif saja bukan pengganti nama filter. Unit Toko telah menampilkan nama, tetapi belum menyediakan pelepasan per chip. |
| [web.dev: mencegah pergeseran tata letak](https://web.dev/articles/optimize-cls) | Gambar tanpa ruang yang disediakan dan konten dinamis dapat menggeser tampilan. Target CLS umum yang dibahas adalah ≤0,1 pada persentil 75. | Pertahankan dimensi/aspect ratio gambar dan kerangka pemuatan; jangan menggerakkan posisi tombol transaksi saat harga atau notifikasi berubah. Target bukan hasil pengukuran situs ini. |

## Audit konkret aplikasi

### Yang sudah baik dan perlu dipertahankan

- `website/components/shop/storefront.tsx`: filter dan urutan berada di URL; perubahan filter kembali ke halaman pertama; posisi gulir saat kembali dari detail disimpan; ada skeleton, pesan gagal, keadaan kosong, dan pagination berlabel.
- `website/components/shop/shopping-pages.tsx`: kemasan menggunakan URL SKU, harga/subtotal jelas, jumlah dibatasi, Beli sekarang terpisah dari Tambah ke keranjang, subtotal keranjang hanya menghitung pilihan.
- `website/components/shop/checkout-page.tsx`: draft penerima tersimpan; stock/harga/sesi diperiksa lagi; perubahan keranjang di tab lain ditangani; pengajuan tidak pasti dapat dicoba dengan identitas permintaan yang sama; error field memakai `aria-invalid`/`aria-describedby`; fokus error tersedia.
- `website/lib/client/customer-cart.ts` dan `shop-shell.tsx`: penyimpanan dibedakan per pemilik, penggabungan guest, revisi antar-tab, serta peringatan bila penyimpanan persisten tidak tersedia. Ini penting dipertahankan.
- `website/components/art.tsx`: gambar menggunakan Next Image dengan dimensi; detail/catalog mempunyai permintaan ukuran responsif. Jangan menggantinya dengan background besar tanpa reservasi ruang.

### Temuan dan tindakan berurutan

| Prioritas | Keadaan saat ini / bukti kode | Perbaikan terbatas | Kriteria selesai |
| --- | --- | --- | --- |
| P1 | Chip pada `storefront.tsx` berupa `span`; hanya ada Hapus filter untuk semua. | Ubah chip pencarian/koleksi/kategori/stok menjadi tombol lepas individual; pertahankan reset total sebagai tindakan tersendiri. | Menghapus stok mempertahankan pencarian dan kategori; URL, jumlah hasil, badge, serta halaman konsisten. Keyboard dapat mengaktifkan tiap chip. |
| P1 | Filter stok memakai `familyProduct(family, search).available > 0`; perwakilan default adalah kemasan pertama. | Saat stock=available, pilih kemasan cocok yang masih tersedia. Tetap hormati pencarian SKU spesifik. Jika tidak dibangun sekarang, catat sebagai bug cakupan keluarga. | Fixture keluarga kemasan pertama habis dan kedua tersedia tetap muncul pada filter stok; kartu menampilkan kemasan tersedia yang sama dengan target detail. Pencarian SKU habis tidak diam-diam dialihkan ke SKU lain. |
| P1 | Quick-add nonaktif ketika seluruh stok perwakilan sudah ada di keranjang tetapi teks di sebelahnya tetap “N tersedia”. | Bedakan keadaan habis vs stok sudah di keranjang; gunakan teks singkat “Stok sudah di keranjang”/jumlah yang telah dipilih. Hindari tooltip sebagai satu-satunya penjelasan. | Pengguna dapat memahami alasan tanpa hover; tidak menambah melewati stok dan tidak mengubah source stock. |
| P1 | Pilihan kemasan detail hanya menunjukkan teks kemasan dan centang. Kartu varian tidak menunjukkan harga/stok alternatif. | Jadikan pilihan kemasan label kompak dua baris: nama kemasan, harga per satuan, ketersediaan. Stok habis tetap dapat dibaca dan dibandingkan. | Ganti kemasan memperbarui URL, harga, stok, gambar, subtotal; aktif teridentifikasi tanpa hanya warna; 320 px tidak melebar. |
| P1 | `ShopQuantity` detail memakai max minimal 1 meskipun stok nol; input tetap aktif meski membeli tidak mungkin. | Nonaktifkan input jumlah saat stok nol; tampilkan “Kemasan ini sedang habis” dan pilihan lain bila ada. Jangan tampilkan rentang 1–1 sebagai saran pembelian barang habis. | Produk nol stok tidak memberi sinyal bahwa jumlah 1 dapat dibeli; varian lain tetap dapat dipilih. |
| P2 | Kontrol jumlah keranjang tidak menerima max stok, sehingga tombol plus tetap dapat menaikkan di atas stok; checkout kemudian diblokir. | Terus tampilkan jumlah lama yang lebih besar dari stok dengan pesan; batasi penambahan baru pada stok tersedia, izinkan pengurangan dan penghapusan. | Stok turun tidak menghapus pilihan diam-diam; pengguna dapat memperbaiki jumlah; stok 0 tidak menghasilkan nilai kuantitas 0 dari tombol minus. |
| P2 | Tidak ada indikator tahap yang konsisten antara cart dan checkout; kedua breadcrumb hanya menunjukkan area saat ini. | Tambahkan progres pendek pada konten utama, dengan halaman sebelumnya berupa link dan tahap aktif `aria-current="step"`. | Mode cart dan buy-now memiliki urutan yang jujur; tidak menciptakan halaman baru/fake step “Pembayaran” karena pembayaran terjadi setelah invoice. |
| P2 | Keadaan kosong/gagal menggunakan ikon tas/pesan generik pada shell. | Buat komposisi state dengan satu judul, alasan ringkas, tindakan pemulihan spesifik. Pada nol hasil, tampilkan pencarian/filter terkait; jangan menambahkan carousel ke state gagal. | Retry tetap menjalankan refresh yang sama; cart kosong menuju katalog; produk tidak tersedia tidak dianggap login gagal. |
| P2 | Detail memakai frasa “pilihan SKU” di bawah foto; pengguna seharusnya memahami kemasan. | Ganti kalimat menjadi “Foto ilustrasi. Isi pesanan mengikuti kemasan yang dipilih.” Kode tetap tersedia pada rincian produk untuk identifikasi. | Foto, kemasan, harga, dan satuan tidak bertentangan; tidak menyembunyikan fakta simulasi. |
| Pemeriksaan | Bilah mobile detail/cart/checkout fixed bottom di `storefront-v7.css`; nav bawah disembunyikan saat ada shopping-action, padding bawah 92 px sudah ada. | Periksa 320/390/768, pembesaran teks, safe area, fokus field paling bawah. Perlu penambahan scroll-margin/padding hanya bila terbukti. | Tidak ada dua bar yang bertumpuk; input error yang difokuskan terlihat; tombol panjang dan nilai rupiah besar tidak terpotong. Ini risiko yang perlu diuji, bukan bug overlap yang telah dibuktikan. |

## Rencana visual yang koheren

Token mempertahankan identitas yang ada: petrol `#103F47` untuk teks/tindakan penting, orange `#C54F26` untuk satu tindakan pembelian utama, kertas `#F4F7F7`, putih `#FFFFFF`, garis `#CFDADF`, abu teks `#52656A`. Gunakan font body proyek untuk nama dan form; angka harga tabular; hierarki 32/24/18/14/12 px dengan variasi responsif. Tidak perlu font baru yang menambah unduhan.

Katalog: foto persegi sebagai fokus; nama dua baris dengan ritme konsisten; harga kuat; kemasan tepat di bawahnya; stok dan tambah dalam baris bawah. Filter menjadi alat kerja yang ringkas, bukan panel hias. Detail: komposisi foto kiri dan informasi kanan; harga, kemasan, jumlah, lalu dua CTA. Keranjang: baris barang yang padat namun terpisah jelas; ringkasan nota di kanan desktop. Checkout: penerima dan barang berada dalam kolom utama; nota ringkasan dan tindakan di kanan. Mobile mengalir satu kolom dengan satu bar tindakan.

Gerak digunakan untuk perubahan keadaan: centang setelah tambah, pergantian kemasan singkat, expand/collapse filter, dan umpan balik jumlah. Jangan menganimasikan form, harga, atau tombol menjauh dari pointer. Reduced-motion tetap mengubah status dan informasi tanpa pergerakan. Tidak ada penghargaan desain yang bisa dijanjikan oleh implementasi; sasaran konkret adalah komposisi khas, transaksi jelas, dan tanpa gangguan.

## Paket implementasi dan validasi

1. **Commerce A — penemuan:** `storefront.tsx`, `shop-data.ts`, stylesheet commerce terisolasi. Chip individual + perwakilan varian stok + status quick-add.
2. **Commerce B — pilihan produk:** `shopping-pages.tsx`, stylesheet yang sama. Kemasan lebih informatif, keadaan habis, copy foto, kontrol jumlah.
3. **Commerce C — kesinambungan:** komponen progres kecil baru, dipakai cart/checkout; visual ringkasan konsisten. Biarkan fetch/session/command/draft/idempotency tetap utuh.
4. **QA terarah:** fixture untuk keluarga multi-kemasan dan filter stok (perubahan perilaku nyata), lalu browser alur katalog → detail → cart → checkout tanpa mengirim transaksi produksi. Periksa empty search, stok nol, melebihi stok, badge, back/scroll restoration, tombol keyboard, viewport sempit, dan bar mobile.

Pisahkan hasil riset, implementasi, dan hasil tes dalam catatan rilis. Laporan ini menyampaikan sumber dan temuan kode; implementasi atau lulus tes tidak boleh diklaim sebelum diverifikasi.
