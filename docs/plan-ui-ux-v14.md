# Rencana perbaikan menyeluruh Unit Toko — v14

23 September 2026. Disusun sebelum perubahan aplikasi. Sasaran: pengalaman belanja yang khas dan mudah dipakai, serta ruang kerja yang membantu setiap peran menyelesaikan pekerjaan. “Award winning” menjadi arah kualitas visual dan interaksi; bukan klaim penghargaan.

## Dasar riset dan diagnosis

Lima agen meninjau lima bidang secara terpisah: [gerakan dan autoplay](research-v14-motion.md), [landing dan footer](research-v14-landing-footer.md), [login dan akun](research-v14-login-account.md), [dashboard dan modul internal](research-v14-dashboard.md), serta [katalog sampai checkout](research-v14-commerce.md). Agen utama mereproduksi masalah dan mengintegrasikan keputusan. Sumber primer, temuan kode, dan inferensi desain dibedakan di setiap laporan.

Masalah slider berhasil direproduksi di browser lokal: pointer pada kartu membuat timer berhenti, tetapi ikon jeda tetap muncul tanpa penjelasan. Di luar kartu, autoplay berjalan. Ada pula celah ketahanan timer setelah inisialisasi ulang ukuran dan ambang keterlihatan yang terlalu besar. Perbaikan harus mencakup ketiganya, bukan mengganti ikon saja.

Footer pada screenshot belum memiliki kelompok navigasi yang jelas. Dashboard menyamakan bobot semua angka meskipun prioritas setiap peran berbeda. Pada akun pelanggan, tindakan dan konteks kembali ke daftar masih dapat diperjelas. Checkout sudah memiliki penyimpanan draf dan pemeriksaan stok; kekuatan tersebut harus dipertahankan.

Koreksi temuan awal: checkout pelanggan **tidak meminta tanggal kebutuhan**, dan nilai internal `neededAt` bukan janji tanggal pengiriman. Jangan menampilkannya sebagai tanggal yang dipilih pelanggan. Informasi tanggal hanya ditampilkan jika maknanya memang didukung alur yang ada.

## Arah desain

Pertahankan identitas U/T dan warna yang sudah dikenali. Gunakan petroleum `#103F47` untuk identitas, putih `#FFFFFF` untuk permukaan kerja, ivory `#F7F6F1` pada cerita pelanggan, sage `#E7EEEB` untuk pengelompokan tenang, teks sekunder `#52656A`, serta amber `#AD431E` untuk aksen tindakan penting. Warna status tetap disertai teks/ikon. Archivo digunakan secukupnya untuk judul editorial; DM Sans untuk formulir, navigasi dan data. Angka keuangan menggunakan lebar karakter konsisten.

Skala jarak 8/12/16/24/32/48/64 px; area tekan utama sekitar 44 px; paragraf dibatasi agar mudah dibaca. Hindari mengubah warna dan font seluruh identitas hanya karena rekomendasi otomatis. Hasil pencarian UI/UX Pro Max digunakan untuk daftar pemeriksaan; rekomendasi pink dan dashboard pemasaran generiknya tidak sesuai studi kasus ini.

Ciri khas tetap berupa koleksi kebutuhan kerja yang ditampilkan melalui foto dan tipografi. Landing memberi ruang untuk cerita; dashboard memberi ruang untuk pekerjaan. Gerakan membawa perhatian ke perubahan yang nyata, sementara input, tabel dan tombol transaksi tetap stabil.

```text
Landing:     identitas + akses → foto utama → koleksi → cara belanja → akses → footer
Toko:        pencarian → koleksi otomatis → filter aktif → produk → footer terkelompok
Checkout:    keranjang → pengiriman & pemeriksaan → pesanan tercatat
Dashboard:   peran + navigasi → pekerjaan berikutnya → ringkasan → rincian yang bisa dibuka
Akun:        status pesanan → tindakan yang diperlukan → barang/pengiriman/tagihan
```

## Urutan implementasi

| Tahap | Halaman/cakupan | Perubahan yang dikerjakan | Kriteria selesai |
| --- | --- | --- | --- |
| 1 — slider | `/shop` | Timer berulang yang pulih saat resize; keterlihatan mengacu ke isi kartu; status sesuai keadaan; pointer pada foto kosong tidak menghentikan otomatis. Fokus keyboard, jeda manual dan reduced motion tetap dihormati. | Minimal tiga perpindahan berurutan; pulih setelah resize/keluar layar; jeda eksplisit tidak mulai sendiri; tidak ada tombol Mulai besar. |
| 2 — footer dan landing | `/`, seluruh halaman pelanggan | Footer petroleum dengan grup Belanja, Akun Anda, Unit Toko; disclosure lengkap pada baris terpisah. Navigasi mobile landing terlihat; bab koleksi mobile lebih ringkas dan foto tidak berulang tanpa alasan. | Tautan nyata dan benar; tidak ada alamat/kontak/promo rekaan; 320 px muat; navigasi bawah tidak menutupi footer. |
| 3 — katalog dan pembelian | `/shop`, detail, `/cart`, `/checkout` | Filter aktif dapat dihapus satu per satu; label quick-add menjelaskan stok sudah masuk keranjang; pilihan kemasan menunjukkan harga dan ketersediaan; petunjuk tahapan belanja singkat; spacing dan hierarki harga konsisten. | URL/filter/sort/pagination tetap benar; angka harga mengikuti kemasan; draf, validasi stok, pembatasan submit dan pemulihan transaksi tetap bekerja. |
| 4 — login dan akun | dua login, profil, daftar/detail pesanan | Pertahankan dua komposisi login; perjelas error dan bantuan masuk. Ringkasan tindakan pesanan dengan tautan langsung; konteks daftar dipertahankan ketika kembali. Perjelas keadaan menyimpan/gagal dan pembagian informasi profil. | Akses pelanggan/staf tetap terpisah; label permanen; fokus error jelas; tidak ada janji pengiriman palsu; tindakan mengarah ke bagian yang benar. |
| 5 — dashboard dan internal | seluruh `/workspace?view=…` | Navigasi dikelompokkan sesuai pekerjaan dan izin; panel pekerjaan berikutnya per peran; invoice mendesak didahulukan dan dapat dibuka; tugas kurir punya aksi/keadaan kosong; kartu admin bermakna; sistem panel, formulir, tabel dan angka konsisten. | Semua tujuan diizinkan untuk peran terkait; angka bersumber dari data; empty state mengarahkan langkah berikutnya; tabel tidak memaksa halaman melebar. |
| 6 — pemeriksaan dan publikasi | lokal, Vercel, GitHub | Uji sumber, build, akses, alur yang berubah, geometri dan gerakan; catat bukti serta batas pemeriksaan; publish hasil yang lolos. | Domain menunjuk deployment READY; source bersih di repositori yang diminta; rahasia/database lokal tidak ikut dipublikasikan. |

Tahap ini mencakup audit seluruh halaman utama dan perbaikan bersama yang menjangkau seluruh modul. Tidak setiap layar perlu disusun ulang jika hierarki dan alurnya sudah tepat. Setiap rekomendasi yang tidak diterapkan harus dicatat beserta alasannya di catatan rilis, bukan dianggap selesai.

## Matriks modul internal

| Modul | Fokus perbaikan |
| --- | --- |
| Ringkasan | Prioritas pekerjaan per peran, bukan hanya tiga angka setara |
| Katalog barang | Harga/kemasan, ruang gambar, kontrol jumlah dan keranjang |
| Pesanan | Status, daftar/filter, aksi berikutnya, detail yang mudah dipindai |
| Pengiriman | Tugas dan penerima jelas; aksi langsung serta keadaan tanpa tugas |
| Persediaan | Angka terbaca, status stok, filter dan tabel konsisten |
| Pengadaan | Struktur editor, status penerimaan dan tindakan yang jelas |
| Invoice & piutang | Urutan jatuh tempo, nilai saldo dan tautan detail |
| Pembayaran | Aksi aman, label simulasi dan keadaan verifikasi terbaca |
| Laporan | Filter berkelompok, satuan/angka konsisten dan ekspor dapat ditemukan |
| Tutup periode | Status dan prasyarat terlihat tanpa animasi yang mengganggu |
| Administrasi | Tujuan kartu sesuai isi nyata; pengaturan dan akun mudah ditemukan |
| Profil | Identitas, informasi pribadi, keamanan dan feedback terpisah jelas |

## Kebijakan gerakan dan aksesibilitas

Animasi pembukaan sekitar 500–900 ms hanya untuk cerita utama; umpan balik kontrol sekitar 150–250 ms. Tidak ada scroll hijack, angka palsu, loading buatan, atau gerak formulir saat diketik. Reduced motion meniadakan autoplay dan gerak masuk non-esensial.

Untuk memenuhi kebutuhan autoplay pengguna, jeda hover dibatasi pada tautan/kontrol interaktif, bukan seluruh area foto. Ini keputusan yang berbeda dari rekomendasi hover seluruh carousel pada [pola W3C APG](https://www.w3.org/WAI/ARIA/apg/patterns/carousel/). Jeda manual, penghentian keyboard, dan reduced motion tetap tersedia; jangan mengklaim kepatuhan penuh APG. Teks keadaan menjelaskan kapan slider berjalan atau dijeda.

## Pemeriksaan yang direncanakan

- Desktop, 768 px, 390/375 px, 320 px dan layar pendek; lihat overflow, pemotongan teks, lapisan sticky, fokus dan dialog.
- Slider: pointer di foto/kontrol, beberapa siklus, manual next/prev, jeda/lanjut, fokus keyboard, resize, keluar/masuk layar, pergantian tab, preferensi gerakan sejauh alat mendukung.
- Filter per-chip, urutan hasil, keadaan tanpa hasil, kemasan, keranjang dan checkout lokal terisolasi.
- Login kedua portal dan akses 11 akun; dashboard relevan per peran; detail pesanan kembali dengan filter sebelumnya.
- TypeScript, lint, tes relevan dan build. Tes domain diperluas hanya bila perubahan menyentuh pilihan data atau perilaku yang memerlukan regresi.
- Tidak memakai data/transaksi production untuk pengujian mutasi. Catatan rilis membedakan pemeriksaan browser, review kode dan hal yang belum diuji.
