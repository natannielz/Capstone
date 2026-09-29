# Perapian desktop dan mobile — catatan kerja

Tujuan tetap: mockup Figma sesuai demo, rapi, dapat diakses dari desktop dan ponsel, dengan kontrol prototipe berfungsi. Status belum selesai; dokumen ini mencatat hasil yang sudah diverifikasi.

Pembaruan terakhir: 25 September 2026, Asia/Jakarta. Pekerjaan dimulai 24 September; tanggal data demo dan tangkapan tidak diubah ketika dokumen diperbarui.

Bagian-bagian berikut mencatat tahapan pekerjaan. Angka dan keterbatasan pada tahap awal dapat diperbarui oleh bagian sesudahnya. Ringkasan terkini tersedia di [STATUS-FIGMA.md](STATUS-FIGMA.md); bukti per kontrol tersedia di [LEDGER-INTERAKSI.md](LEDGER-INTERAKSI.md).

## Perubahan terverifikasi di editor Figma

- Kuota konektor `use_figma` masih habis, tetapi akun pengguna kini memiliki akses editor melalui browser. Pekerjaan dilanjutkan lewat antarmuka Figma.
- Dialog Missing fonts mendeteksi 1.954 penggunaan `bodyFont` dan 17 penggunaan `displayFont`. Keduanya diganti menjadi **DM Sans** dan **Archivo Black**. Setelah Replace fonts, indikator Missing fonts hilang.
- Halaman utama menjadi **01 · Desktop — Demo** (`0:1`). Dua puluh satu layar diberi nama bernomor sesuai proses.
- Tujuh frame replika duplikat dipindahkan ke **99 · Referensi cadangan**, bukan dihapus. ID frame hasil pemindahan perlu dibaca ulang; jangan mengandalkan ID replika `16:*` dalam STATUS-FIGMA.md.
- Halaman **02 · Mobile — Demo** dibuat, ID `37:5`.
- Sembilan layar mobile telah masuk dari browser 390 × 844: beranda `38:2`, katalog `43:2`, login pelanggan `40:2`, login staf `41:2`, detail produk `44:2`, keranjang `45:2`, checkout `46:2`, detail pesanan `48:2`, dan akun `47:2`.
- Seluruh sembilan frame mobile berukuran 390 × 844, Clip content aktif, dan Overflow Vertical. Isi halaman yang panjang tetap dipertahankan untuk digulir.
- Tambahan 647 penggunaan `bodyFont` dan 17 penggunaan `displayFont` pada tangkapan mobile diganti dengan DM Sans dan Archivo Black; indikator Missing fonts hilang.
- Tangkapan `39:2` adalah dashboard administrator 1280 piksel akibat pengalihan sesi, bukan katalog mobile. Tangkapan itu dipindahkan ke halaman referensi cadangan dan sekarang ber-ID `42:2`.
- Flow awal mobile bernama **Mobile · Pengalaman pelanggan**, mulai pada `38:2`.

## Koreksi visual beranda mobile

Hasil tangkapan menyimpan beberapa lapisan saat animasi masuk masih berlangsung. Lapisan asli diperbaiki di editor, tanpa mengganti seluruh layar:

- Kicker `38:38`: opacity 45% menjadi 100%, Y 10 menjadi 0.
- Judul baris pertama `38:44`: opacity 42,8% menjadi 100%, Y 14,8 menjadi 0.
- Judul baris kedua `38:49`: opacity 15% menjadi 100%, Y 22 menjadi 0.
- Paragraf `38:55`: opacity 45% menjadi 100%, Y 10 menjadi 0.
- Penutup animasi foto `38:79`: opacity 100% menjadi 0%, sesuai keadaan akhir demo.
- Screenshot mode Present terakhir menunjukkan kedua baris judul utuh, paragraf terbaca, dan foto hero tampil. Aset foto asli tetap dipakai.

## Interaksi yang sudah tersambung

| Kontrol mobile | Tujuan | Bukti |
| --- | --- | --- |
| Belanja pelanggan `38:58` | Katalog `43:2` | Diklik di Present; URL berubah ke node `43-2` |
| Portal divisi & petugas `38:66` | Masuk staf `41:2` | Diklik di Present; URL berubah ke node `41-2` |
| Portal internal pada navigasi `38:25` | Masuk staf `41:2` | Panel Interaction menampilkan tujuan yang benar |
| Gambar Air mineral 600 ml pada katalog | Detail produk `44:2` | Panel Interaction menampilkan tujuan yang benar |
| Tambah Air mineral 600 ml, 1 dus pada katalog | Keranjang `45:2` | Panel Interaction menampilkan tujuan yang benar |
| Tambah ke keranjang pada detail produk `44:326` | Keranjang `45:2` | Diklik di Present; URL berubah ke node `45-2` |
| Beli sekarang pada detail produk `44:333` | Checkout `46:2` | Diklik di Present; URL berubah ke node `46-2` |
| Kembali ke produk pada detail produk | Katalog `43:2` | Diklik di Present; URL berubah ke node `43-2` |
| Lanjut checkout pada keranjang | Checkout `46:2` | Diklik di Present; URL berubah ke node `46-2` |
| Buat pesanan pada checkout | Detail pesanan `48:2` | Diklik di Present; URL berubah ke node `48-2` |

Tombol sampul desktop `12:135` sebelumnya sudah terhubung ke beranda desktop `4:2`. Sepuluh kontrol mobile pada tabel sudah memiliki tujuan; tujuh di antaranya sudah diuji dengan klik langsung di Present. Urutan detail produk → keranjang → checkout → detail pesanan berhasil dilalui. Data pesanan adalah state demo yang sudah disiapkan, bukan transaksi baru dari Figma. Daftar ini bukan klaim seluruh tombol telah selesai. Input, perubahan jumlah, filter, produk lain, dialog, dan aksi modul internal belum semuanya dibuat atau diuji sebagai interaksi Figma.

[Buka prototipe mobile](https://www.figma.com/proto/DAVyovkzeqhjSwOXP96Klr/Unit-Toko?node-id=38-2&page-id=37%3A5&scaling=min-zoom&content-scaling=fixed&starting-point-node-id=38%3A2)

## Keterbatasan alat yang teramati

Editor Figma menampilkan **MCP — Out of calls**. Pengubahan melalui antarmuka biasa masih berhasil. Editor plugin Scripter berada di iframe bertingkat; input dan fokus keyboard tidak dapat dikendalikan secara andal oleh alat browser. Tidak ada skrip perapian massal yang berhasil dijalankan lewat plugin tersebut. Perubahan dalam catatan ini dilakukan dan diverifikasi melalui editor Figma biasa.

## Pemeriksaan sumber mobile

Browser beranda, login pelanggan, login staf dan katalog pelanggan: viewport 390 piksel, lebar dokumen 375 piksel (ruang scrollbar), tidak ada overflow horizontal pada pemeriksaan tersebut. Screenshot beranda memperlihatkan susunan vertikal sesuai CSS mobile demo.

Semua tangkapan mobile berikutnya menggunakan satu tab berukuran 390 piksel dan ukuran diperiksa sebelum ekspor. Pengaturan viewport juga pernah membuat editor Figma menyempit dan sidebar bertumpuk; selalu reset viewport setelah capture sebelum kembali menyunting Figma.

## Pembaruan melalui editor — 24 September 2026

Pengguna telah memilih melanjutkan melalui editor yang tersedia. Tidak perlu meminta persetujuan ulang tentang kuota MCP. Ekspor data demo sintetis ke file yang sama juga telah disetujui.

### Susunan layar

- Seluruh 21 frame aplikasi desktop berukuran **1280 × 900**, Clip content aktif, dan Overflow Vertical. Sampul `12:2` tetap terpisah.
- Posisi 21 frame desktop telah dibaca ulang setelah dirapikan: tujuh kolom pada X 100, 1580, 3060, 4540, 6020, 7500, 8980; tiga baris pada Y 100, 1300, 2500. Urutan mengikuti nomor layar 01–21.
- Mobile bertambah menjadi **13 frame**. Tambahan: daftar pesanan awal `71:2`, konfirmasi pembatalan `72:2`, hasil pembatalan `74:2`, dan daftar setelah pembatalan `78:2`.
- Dua belas frame pertama telah diverifikasi 390 × 844 dengan empat kolom X 100, 650, 1200, 1750 dan baris Y 100, 1300, 2500. Frame `78:2` bernama **13 · Daftar setelah pembatalan · Mobile**, diposisikan X 100, Y 3700, tinggi 844, Clip content aktif, Overflow Vertical.
- Tambahan font pada tiga frame 71/72/74: 204 bodyFont dan 3 displayFont diganti DM Sans/Archivo Black. Frame 78: 60 bodyFont dan 1 displayFont juga diganti. Indikator missing fonts tidak muncul pada pemeriksaan editor berikutnya.

### Interaksi tambahan yang tersimpan

| Kontrol | Tujuan | Verifikasi |
| --- | --- | --- |
| Kembali ke beranda pada login pelanggan dan staf mobile | `38:2` | Area LinkComponent termasuk ikon sudah dihubungkan; klik ikon pada kedua layar berhasil |
| Batal sisa pesanan `48:175` | Dialog `72:2` | Klik Present berhasil |
| Konfirmasi pembatalan `72:404` | Hasil `74:2` | Klik Present berhasil, status Dibatalkan terlihat |
| Kembali pada dialog `72:401` | Detail awal `48:2` | Klik Present berhasil |
| Tutup dialog `72:407` | Detail awal `48:2` | Klik Present berhasil |
| Nomor pesanan pada daftar awal `71:152` | Detail awal `48:2` | Klik Present berhasil |
| Lihat pesanan pada daftar awal `71:188` | Detail awal `48:2` | Klik Present berhasil |
| Kembali ke daftar dari detail awal `48:118` | Daftar awal `71:2` | Klik Present berhasil |
| Kembali ke daftar dari hasil `74:116` | Daftar setelah pembatalan `78:2` | Klik Present berhasil |
| Nomor pesanan pada daftar terbaru `78:152` | Hasil `74:2` | Klik Present berhasil |
| Lihat pesanan pada daftar terbaru `78:188` | Hasil `74:2` | Klik Present berhasil |
| Belanja pelanggan desktop `4:65` | Katalog `6:2` | Klik Present berhasil |
| Portal divisi & petugas desktop `4:73` | Login staf `10:2` | Klik Present berhasil |
| Tambah ke keranjang desktop `11:174` | Keranjang `13:5` | Klik Present berhasil |
| Beli sekarang desktop `11:181` | Checkout `14:5` | Klik Present berhasil |
| Lanjut checkout desktop `13:218` | Checkout `14:5` | Klik Present berhasil |
| Buat pesanan desktop `14:253` | Detail `17:5` | Klik Present berhasil; pesan berhasil dibuat dan PO/202609/0006 terlihat |
| Gambar Air mineral 600 ml pada katalog desktop | Detail `11:2` | Klik Present berhasil |
| Tambah Air mineral 600 ml 1 dus pada katalog desktop | Keranjang `13:5` | Klik Present berhasil |
| Kembali ke produk desktop `11:71` | Katalog `6:2` | Klik Present berhasil |
| Lanjut belanja desktop `13:74` | Katalog `6:2` | Klik Present berhasil |
| Ubah barang desktop `14:74` | Keranjang `13:5` | Klik Present berhasil |
| Kembali ke beranda pada dua login desktop, termasuk `10:16` | Beranda `4:2` | Klik ikon pada kedua layar berhasil di Present |

Navigasi **Profil & alamat** diterapkan pada 18 hasil pencarian. Panel massal menampilkan Mixed, sehingga belum boleh dihitung sebagai 18 interaksi tervalidasi. Tautan footer `78:256` sudah diperiksa sendiri dan tujuannya `47:2`; tautan profil atas dari `74:2` berhasil diklik ke `47:2` di Present. Pilihan yang berada pada halaman akun itu sendiri dan kemungkinan heading perlu diaudit serta dibersihkan dari reaksi kosong. Jangan mengulang penambahan aksi massal tanpa memeriksa aksi yang sudah ada.

### Perilaku gulir

Navigasi bawah pada daftar awal `71:294` semula ikut bergulir. Pengaturan **Position → Fixed (stay in place)** berhasil memperbaikinya; screenshot setelah digulir menunjukkan bilah tetap di bagian bawah viewport. Pengaturan sama kemudian diterapkan pada enam container navigasi yang ditemukan melalui label tepat “Keranjang (1)”; panel menunjukkan Fixed. Termasuk `71:294` dan `78:294`. Daftar terbaru `78:2` juga diuji gulir dan bilah tetap di bawah. Layar lainnya masih perlu uji gulir satu per satu. Bar checkout/keranjang dan navigasi katalog/detail produk belum diaudit.

### Koreksi hero desktop

- Judul pertama `4:51`: Y 37,84 → 0; opacity 25,19% → 100%.
- Judul kedua `4:56`: Y 43 → 0; opacity 15% → 100%.
- Paragraf `4:62` dan kicker `4:45`: Y 10 → 0; opacity 45% → 100%.
- Mode Present memperlihatkan kedua baris judul dan paragraf utuh dengan foto asli. Tidak ada tangkapan ulang beranda; lapisan yang ada diperbaiki langsung.
- Pada login staf desktop, baris alur operasional bagian bawah masih terlihat memudar. Bandingkan dengan keadaan akhir animasi demo sebelum mengoreksi; belum dinyatakan selesai.

### Pemeriksaan akhir sesi editor

- Seluruh 13 kontrol desktop yang ditambahkan pada sesi ini telah diuji dengan klik langsung, termasuk gambar/tambah katalog, beli langsung, dua tautan login kembali, dan tiga jalur kembali dalam belanja.
- Dua belas kontrol mobile tambahan pada tabel (dua login kembali serta sepuluh kontrol daftar/pembatalan) juga telah diuji klik. Tautan profil dari hasil pembatalan berhasil, tetapi audit seluruh 18 hasil pilihan massal tetap belum selesai.
- Frame `78:2` dibaca ulang: X 100, Y 3700, W 390, H 844. Indikator Missing fonts berjumlah nol. Screenshot canvas memperlihatkan 13 frame mobile tersusun rapi.
- Panduan HTML diperbarui dengan 21 layar desktop, 13 mobile, hasil uji yang benar, dan status pesanan lokal yang sudah dibatalkan. Isi pembaruan diperiksa melalui browser aplikasi yang masih berjalan.

### Konsistensi data dan pekerjaan lanjutan

Pesanan demo `PO/202609/0006` pada basis data lokal kini **Dibatalkan**, melalui aksi aplikasi asli dengan alasan “Kebutuhan berubah — pembatalan simulasi untuk demonstrasi mentor.” Nilai tersisa Rp0. Frame awal 48/71 sengaja menyimpan state sebelum pembatalan; frame 74/78 menyimpan hasilnya. Transisi Figma tidak melakukan transaksi baru.

Belum ada hubungan sidebar Ringkasan desktop yang disimpan. Percobaan reaksi kosong pada 13 pilihan sudah dihapus. Jangan mengarahkan semua peran ke dashboard kepala; halaman pembayaran/tutup periode memakai penagihan dan halaman administrasi memakai admin.

Tidak ada capture yang masih pending. Seluruh ID capture yang menghasilkan frame 71, 72, 74, dan 78 sudah dikonsumsi dan tidak boleh digunakan ulang.

## Belum selesai

- Selesaikan seluruh layar dan state mobile; ganti alias font yang masuk bersama tangkapan baru.
- Verifikasi posisi, tinggi, clipping, gambar, dan tipografi semua layar desktop/mobile terhadap demo.
- Buat dan uji hubungan prototipe, termasuk navigasi, dialog, form, filter, pagination, serta aksi kembali/batal/tutup.
- Periksa alur Present desktop dan mobile; audit akses tautan tanpa mengubah izin berbagi yang tidak diperlukan.
- Perbarui panduan dan status final hanya setelah bukti sesuai cakupan permintaan.

## Penambahan dan navigasi mobile internal — lanjutan 24 September 2026

Tiga belas frame baru telah selesai diekspor, tanpa kata sandi. Capture dikerjakan satu per satu setelah verifikasi alamat, peran, dan lebar 390 px. Penolakan awal terhadap sebagian permintaan capture paralel diselesaikan dengan metode berurutan; tidak ada capture tertunda.

| Frame | Isi | Ukuran | Posisi X,Y |
| --- | --- | --- | --- |
| 104:2 | Dashboard Kepala Toko | 390×844 | 650,3700 |
| 98:2 | Katalog internal | 390×844 | 1200,3700 |
| 99:2 | Pesanan internal | 390×844 | 1750,3700 |
| 100:2 | Pengiriman | 390×844 | 100,4900 |
| 101:2 | Persediaan | 390×844 | 650,4900 |
| 102:2 | Pengadaan | 390×844 | 1200,4900 |
| 103:2 | Invoice dan piutang | 390×844 | 1750,4900 |
| 109:2 | Pembayaran (Penagihan) | 390×844 | 100,6100 |
| 105:2 | Laporan (Kepala Toko) | 390×844 | 650,6100 |
| 111:2 | Tutup periode (Penagihan) | 390×844 | 1200,6100 |
| 106:2 | Profil Kepala Toko | 390×844 | 1750,6100 |
| 107:2 | Drawer Kepala Toko | 320×844 | 650,7300 |
| 108:2 | Dashboard Penagihan | 390×844 | 1200,7300 |

Nama frame sudah diseragamkan, tinggi 844, Clip content aktif, dan overflow Vertical. Screenshot keseluruhan canvas memperlihatkan susunan empat kolom tanpa tumpang tindih. Penggantian bodyFont → DM Sans diterapkan pada 959 elemen; indikator Missing fonts hilang setelah penggantian.

### Hubungan menu Kepala Toko yang sudah diuji di Present

- Sembilan tombol Buka navigasi pada frame 104,98,99,100,101,102,103,105,106 membuka overlay 107:2 di kiri atas; semua diuji klik.
- Ringkasan 107:29 → 104:2.
- Katalog 107:43 → 98:2.
- Pesanan 107:51 → 99:2.
- Pengiriman 107:66 → 100:2.
- Persediaan 107:76 → 101:2.
- Pengadaan 107:93 → 102:2.
- Invoice dan piutang 107:107 → 103:2.
- Laporan 107:116 → 105:2.
- Profil saya 107:130 → 106:2.
- X 107:7 → Close overlay, diuji kembali ke dashboard tanpa berpindah halaman.

Kesembilan tujuan menu telah diuji berurutan melalui klik nyata dan perubahan node-id Present. Latar overlay redup dan Close when clicking outside diaktifkan. Penanda aktif menu masih Ringkasan untuk semua modul; perlu variasi atau variabel agar identik dengan demo.

### Tambahan yang perlu pemeriksaan lanjutan

- Tombol Keluar akun 107:154 → login staf 41:2 telah disimpan; uji klik menunggu perbaikan gulir drawer.
- Delapan avatar Kepala Toko → profil 106:2 telah disimpan; avatar pada dashboard 104 berhasil diuji. Tujuh lainnya belum diuji satu per satu.
- Tombol Tinjau pesanan masuk 104:62 → 99:2 telah disimpan; belum diuji klik.
- Flow “Mobile · Kepala Toko” dimulai dari 104:2, menggantikan Flow 1 yang sempat dimulai pada drawer.
- Wadah isi drawer 107:11, tinggi 776, sudah diatur Vertical dan Clip content. Perlu uji untuk memastikan tombol paling bawah terjangkau.

### Perbaikan label dashboard

Label Pekerjaan berikutnya bertumpuk dengan peran. Teks 104:54 disetel Auto width dan X80; teks 108:54 Auto width dan X125. Screenshot editor kedua dashboard memperlihatkan peran / pekerjaan berikutnya terbaca terpisah. Present dashboard Kepala Toko juga telah diperiksa setelah pemuatan ulang. Visibilitas paragraf 104:48 sudah dipulihkan setelah pemilihan lapisan sempat menonaktifkannya.

Pada tahap ini belum selesai: administrasi mobile, menu/profil sesuai peran Penagihan dan Admin, aksi formulir/filter/transaksi, koreksi indikator menu aktif, dan audit seluruh tombol desktop/mobile. Perkembangan berikutnya dicatat pada bagian selanjutnya; jangan menyatakan semua tombol selesai.

### Hasil uji lanjutan drawer dan masuk

- Gulir isi drawer 107:11 berhasil; header Navigasi dan X tetap di atas, tombol Keluar akun terjangkau di bawah.
- Keluar akun 107:154 berhasil membuka login staf 41:2 dalam Present.
- Tinjau pesanan masuk 104:62 berhasil membuka Pesanan 99:2 dalam Present.
- Masuk ke ruang kerja 41:80 → 104:2 telah ditambahkan dan diuji. Ini simulasi navigasi akun Kepala Toko; belum ada input kredensial/validasi form native Figma dan tidak mengekspor kata sandi.
- Tautan flow internal: https://www.figma.com/proto/DAVyovkzeqhjSwOXP96Klr/Unit-Toko?node-id=104-2&page-id=37%3A5&scaling=min-zoom&content-scaling=fixed&starting-point-node-id=104%3A2
- Present sempat menampilkan hitam setelah perubahan flow; tombol Restart memulihkan render, kemudian seluruh uji di atas berhasil. Tidak ada kesalahan konsol yang ditemukan pada pemeriksaan singkat tersebut.

## Perapian Penagihan dan dialog pembayaran

Lima frame khusus akun Sinta Ayu ditambahkan agar navigasi Penagihan tidak berpindah ke identitas Kepala Toko: menu `119:2`, profil `120:2`, invoice `121:2`, pengadaan `123:2`, dan laporan `124:2`. Alias font pada 217 elemen diganti menjadi DM Sans. Empat layar memakai 390×844; menu memakai 320×844. Semuanya memakai Clip content dan overflow Vertical.

Tujuh pembuka menu pada dashboard, pengadaan, invoice, pembayaran, laporan, periode, dan profil telah diuji membuka overlay `119:2`. Ketujuh tujuan menu berikut berhasil diuji dengan klik nyata di Present:

| Tombol | Tujuan |
|---|---|
| Ringkasan `119:29` | Dashboard `108:2` |
| Pengadaan `119:43` | `123:2` |
| Invoice & piutang `119:57` | `121:2` |
| Pembayaran `119:66` | `109:2` |
| Laporan `119:74` | `124:2` |
| Tutup periode `119:83` | `111:2` |
| Profil saya `119:98` | `120:2` |

X `119:7` menutup overlay; Keluar akun `119:122` menuju login staf `41:2`. Keduanya sudah diuji. Enam avatar Penagihan sudah tersambung ke `120:2`; avatar dashboard telah diuji, lima lainnya masih perlu uji klik. Penanda menu aktif masih tetap Ringkasan.

Flow **Mobile · Penagihan** dimulai di `108:2`. Tombol **Periksa pembayaran** `108:62` → `109:2` sudah diuji. Label pekerjaan berikutnya pada dashboard Penagihan juga telah diperiksa dan terbaca tanpa tumpang tindih.

Perbaikan visual yang sudah diuji dalam Present:

- Wadah tabel periode `111:58` memakai overflow Horizontal; kolom Persetujuan dan Tinjau laporan dapat dijangkau.
- Wadah tabel pembayaran `109:83` memakai overflow Horizontal; status serta tombol Verifikasi, Tolak, dan Identifikasi terlihat setelah digeser.
- Nilai tanggal awal/akhir laporan ditambahkan pada layar Penagihan (`129:3`, `129:4`) dan Kepala Toko (`129:5`, `129:6`). Rentang sumber adalah 1–24 September 2026. Teks memakai DM Sans Regular 16/24, warna #173941, posisi 13,11 di dalam input. Kedua layar telah diperiksa dalam Present. Pemilih tanggal interaktif belum direplikasi.

Dialog **33 · Catat transfer · Mobile** `132:2` ditangkap dari dialog aplikasi, 358×812, posisi canvas 100,9700. Tombol pembuka `109:59` membuka overlay terpusat dengan latar redup. Batal `132:61`, X `132:66`, dan klik di luar dialog sudah diuji menutup overlay. Wadah formulir `132:11` memakai overflow Vertical; gulir sampai kolom Catatan berhasil sementara header dan footer tetap terlihat. Nilai tanggal `134:6` diperbaiki menjadi 24 September 2026 dengan gaya input yang sama. Form ini belum menerima masukan atau menyimpan transaksi melalui prototype.

Semua tangkapan di bagian ini menggunakan data simulasi yang disetujui. Tidak ada kata sandi yang dikirim ke Figma. Pengujian aplikasi sebelumnya tetap terpisah dari pengujian prototype.

### Dialog verifikasi, penolakan, dan identifikasi

| Dialog | Frame | Pembuka dari pembayaran | Batal | X |
|---|---|---|---|---|
| Verifikasi dana | `136:2` | `109:176` | `136:19` | `136:25` |
| Tolak pembayaran | `138:2` | `109:179` | `138:24` | `138:30` |
| Identifikasi pembayar | `140:2` | `109:182` | `140:35` | `140:41` |

Ketiga pembuka, Batal, dan X sudah diuji dengan klik dalam Present. Overlay berada di tengah dengan latar redup; tabel mempertahankan posisi gulir saat dialog ditutup. Semua alias bodyFont diganti DM Sans. Tanggal pencatatan 24 September 2026 ditambahkan pada `145:3`, `146:3`, dan `146:5`, dengan DM Sans Regular 16/24, warna #173941, posisi 13,11; hasilnya sudah diperiksa di Present.

Saat pemasangan X dialog catat transfer, interaksi sempat diterapkan pada pilihan tiga lapisan. Interaksi tersebut sudah dihapus dari pilihan bersama, lalu dipasang hanya pada tombol X `132:66`. Jangan menerapkan Close overlay pada DialogHeader atau ActionDialog karena klik formulir akan ikut menutup dialog.

Data lokal: transfer simulasi “Transfer pusat — belum diketahui”, Rp175.000, sudah diverifikasi melalui UI aplikasi. Hasil yang benar adalah label **Belum teridentifikasi**, saldo belum dialokasikan Rp175.000, dan hanya tombol Identifikasi yang tersisa. Status sebelum verifikasi tetap ada pada frame `109:2` untuk awal skenario prototype. Hasil ini bukan transaksi uang nyata.

### Hasil verifikasi dalam prototype

Frame `147:2`, **37 · Pembayaran setelah verifikasi · Mobile**, menangkap hasil transaksi simulasi di atas. Dimensi 390×844, canvas 100,10900, Clip content dan overflow Vertical; bodyFont diganti DM Sans. Wadah tabel `147:83` memakai Horizontal.

Hubungan berikut sudah diuji melalui klik di Present pada tahap awal; tujuan menu dan avatar `147:2` kemudian dialihkan ke cabang hasil pada bagian berikutnya:

- Konfirmasi Verifikasi dana `136:22` → hasil `147:2`.
- Menu `147:7` → drawer Penagihan `119:2`, posisi kiri atas; X menutupnya kembali.
- Avatar `147:21` → profil Penagihan `120:2`.
- Catat transfer `147:55` → overlay `132:2`, X kembali ke hasil.
- Identifikasi `147:176` → overlay `140:2`.
- Gulir horizontal menampilkan label Belum teridentifikasi dan hanya aksi Identifikasi pada baris Rp175.000; tampilan cocok dengan aplikasi setelah verifikasi.

Catatan tahap awal: drawer Penagihan bersama masih mengarah ke pembayaran awal `109:2`. Masalah ini kemudian ditangani melalui cabang hasil di bawah. Masukan formulir, validasi, hasil simpan Identifikasi/Catat transfer/Tolak, filter, unggah bukti, dan aksi modul lain belum lengkap. Jangan menghitung dialog yang dapat dibuka sebagai formulir yang sudah berfungsi penuh.

## Cabang sesudah verifikasi dan invoice belum lunas — 24 September 2026

Delapan frame ditambahkan melalui duplikasi dan penyuntingan di editor. Pada tahap ini total mobile menjadi **44 frame: 37 layar 390×844, tiga drawer 320×844, dan empat dialog pembayaran selebar 358**; tambahan cabang terfilter pada bagian berikutnya menaikkan jumlahnya menjadi 47. Tidak ada perubahan kode bisnis pada pekerjaan ini.

| Nomor / isi | Node | Ukuran | Posisi X,Y |
| --- | --- | --- | --- |
| 38 · Menu Penagihan setelah verifikasi | `152:2` | 320×844 | 650,10900 |
| 39 · Profil Penagihan setelah verifikasi | `152:120` | 390×844 | 1200,10900 |
| 40 · Invoice Penagihan setelah verifikasi | `152:267` | 390×844 | 1750,10900 |
| 41 · Pengadaan Penagihan setelah verifikasi | `152:435` | 390×844 | 100,12100 |
| 42 · Laporan Penagihan setelah verifikasi | `152:535` | 390×844 | 650,12100 |
| 43 · Periode Penagihan setelah verifikasi | `152:825` | 390×844 | 1200,12100 |
| 44 · Dashboard Penagihan setelah verifikasi | `152:907` | 390×844 | 1750,12100 |
| 45 · Invoice belum lunas | `152:1051` | 390×844 | 100,13300 |

### Navigasi cabang hasil yang sudah diuji

| Tombol menu baru | Tujuan |
| --- | --- |
| Ringkasan `152:24` | `152:907` |
| Pengadaan `152:36` | `152:435` |
| Invoice `152:48` | `152:267` |
| Pembayaran `152:56` | `147:2` |
| Laporan `152:63` | `152:535` |
| Tutup periode `152:71` | `152:825` |
| Profil `152:84` | `152:120` |

Ketujuh tujuan sudah diklik dalam Present; halaman dan akun Penagihan sesuai. Pembuka menu pada `147:2` dan enam layar salinan juga diuji saat berpindah modul. Header `147:7` kini menuju `152:2`; avatar `147:21` menuju `152:120` dan sudah diuji. Avatar dashboard hasil `152:923` juga berhasil membuka profil `152:120` dan tampilannya diperiksa. Empat avatar salinan lainnya tersambung; avatar Pengadaan mencapai URL profil yang benar tetapi pratinjau kemudian kosong, sehingga belum lulus pemeriksaan visual. Avatar salinan Invoice, Laporan dan Periode belum diuji satu per satu.

Uji kesinambungan yang berhasil: `108:2` → menu `119:2` → pembayaran awal `109:2` → dialog verifikasi `136:2` → hasil `147:2` → menu baru `152:2` → laporan `152:535` → menu → pembayaran `147:2`. Status **Belum teridentifikasi** tetap terlihat setelah kembali. Navigasi Pengadaan → Invoice → Periode → Profil → Dashboard juga membuka node cabang `152:*` yang benar. Ini menyelesaikan sebagian masalah kesinambungan hasil melalui tujuh modul; belum membuktikan seluruh jalur pulang, hasil aksi transaksi lain, atau padanan desktop.

### Dashboard hasil dan filter invoice

Dashboard `152:907` disamakan dengan sumber setelah verifikasi: judul **Tinjau saldo tagihan pembeli**, deskripsi **Periksa jatuh tempo dan sisa piutang setelah pembayaran terverifikasi.**, tombol **Buka invoice belum lunas**, dan hitungan **1 invoice belum lunas**.

Tombol `152:953` → `152:1051` sudah diuji di Present. Hasil menampilkan filter **Belum lunas** dan satu invoice **INV/202609/0001**, status **Dibayar sebagian**, total **Rp684.000**, sisa **Rp334.000**. Layar dibuat dari salinan invoice `152:267`; baris lunas `152:1101` dihapus hanya dari salinan tersebut, teks filter `152:1090` diubah, dan container Billing `152:1094` disetel Hug untuk menghilangkan celah kosong. Screenshot Present cocok dengan hasil sumber. Pembuka menu layar ini sudah dipakai dan diuji; avatar mewarisi sambungan cabang hasil, belum diuji. Filter belum dapat diubah secara interaktif.

### Perbaikan bobot font

Sumber aplikasi memuat DM Sans variable dengan rentang 100–1000. Bobot 650 diperbaiki melalui pengaturan sumbu font pada tiga H1 **Ruang kerja Anda** (`104:2`, `108:2`, `152:907`), tiga **Sekilas aktivitas**, dua **Tagihan yang perlu ditindaklanjuti**, judul hero `152:949`, teks tombol baru `152:954`, dan teks tombol awal `108:63`. Penggantian keluarga font ke DM Sans Regular sebelumnya belum mempertahankan bobot ini. Pemeriksaan global terhadap teks berbobot 650 dan bobot lain belum selesai.

### Batas alat dan pekerjaan berikutnya

Koleksi `Keadaan demo` berisi boolean `Demo/Dana diverifikasi`, nilai awal False. Variabel tidak digunakan karena editor paket **Starter** menampilkan kebutuhan **Professional** untuk multiple actions, variabel dan conditional prototypes. Ini hambatan fitur paket yang terpisah dari kuota MCP. Tidak dilakukan upgrade. Cabang frame statis di atas dipakai untuk skenario hasil.

Catatan tahap ini: tombol Periksa pembayaran di dashboard awal `108:62` masih mengarah ke Semua status. Cabang terfilter pada bagian berikutnya memperbaiki tujuan tersebut. Masukan/hasil form, administrasi mobile, sambungan internal desktop, penanda menu aktif, serta audit avatar/tombol/bobot font belum selesai. Rincian per kontrol dipelihara dalam [LEDGER-INTERAKSI.md](LEDGER-INTERAKSI.md).

## Pembayaran terfilter dan hasil kosong — 24 September 2026

Pada tahap ini total mobile **47 frame: 39 layar 390×844, tiga menu 320×844, dan lima dialog pembayaran selebar 358**. Delapan tambahan pada bagian berikutnya menaikkan total menjadi 55. Tidak ada perubahan kode bisnis.

| Nomor / isi | Node | Ukuran | Posisi X,Y |
| --- | --- | --- | --- |
| 46 · Pembayaran menunggu verifikasi | `163:2` | 390×844 | 650,13300 |
| 47 · Tidak ada pembayaran menunggu verifikasi | `185:2` | 390×844 | 1200,13300 |
| 48 · Verifikasi pembayaran terfilter | `186:2` | Lebar 358 | 1750,13300 |

Frame 46 diduplikasi dari `109:2`. Baris grid kedua dan keempat dihapus melalui Grid settings agar hanya transfer Rp175.000 yang tersisa. Ringkasan menjadi **1 pembayaran dari 3 catatan**, pagination 1–1; wadah `163:66` memakai Hug dengan tinggi 269,48, dan `Payments` `163:36` juga memakai Hug. Gulir horizontal tabel sudah diuji.

Frame 47 berasal dari capture aplikasi yang selesai, ID `4d10cf2c-fdef-4788-a6fe-5d4428cdcf1c`, setelah verifikasi akun Sinta Ayu / Penagihan, viewport 390×844, URL `view=payments&paymentStatus=recorded`, dan nol isian kata sandi. Alias font diganti menjadi DM Sans. ID capture sudah dikonsumsi.

### Perilaku sumber dan bukti klik

Sumber aplikasi mempertahankan `paymentStatus=recorded` ketika verifikasi selesai: callback sukses hanya memuat ulang data, menutup dialog, dan menampilkan pesan. Karena satu-satunya transfer yang menunggu sudah diverifikasi, filter tetap **Menunggu verifikasi** dan hasil menjadi kosong. Ini berbeda dari jalur Semua status yang langsung memperlihatkan dana Belum teridentifikasi.

| Aksi yang diuji di Present | Tujuan / hasil visual |
| --- | --- |
| Periksa pembayaran `108:62` | `163:2`, Menunggu verifikasi, satu transfer Rp175.000 |
| Verifikasi `163:125` | Dialog `186:2`, terpusat, latar redup 25%; outside-close diaktifkan |
| Konfirmasi `186:16` | `185:2`, filter tetap aktif, **0 pembayaran dari 3 catatan** |
| Hapus filter `185:95` | `147:2`, **Semua status**, tiga pembayaran |

Keempat langkah sudah diuji dengan klik dan pemeriksaan visual. Layar kosong memakai judul **Tidak ada pembayaran yang cocok** dan teks **Coba nama atau referensi lain, atau hapus filter untuk melihat seluruh catatan pembayaran.** Tabel dan pagination tidak tampil. Dialog `136:2` → `147:2` tetap menjadi cabang verifikasi dari Semua status; konfirmasi asal terfilter memakai `186:2` → `185:2`.

### Kontrol tambahan yang sudah diuji

- Catat transfer `185:55` membuka dialog `132:2` terpusat. Batal kembali ke `185:2`, dengan filter Menunggu verifikasi dan hasil kosong tetap.
- Buka navigasi `185:7` membuka menu `152:2` di kiri atas. X menutup menu kembali ke hasil kosong tanpa menghilangkan filter.
- Avatar `185:21` membuka profil `152:120`; identitas Sinta dan tampilan profil sudah diperiksa.
- Hapus filter pada hasil positif, tombol `201:2` di container `163:63`, membuka `109:2`: Semua status dengan tiga catatan awal. Perubahan dari satu hasil Menunggu verifikasi ke seluruh catatan diuji klik dan visual.
- Batal `186:18`, X `186:20`, dan klik di luar dialog `186:2` telah diuji klik dan visual. Ketiganya kembali ke `163:2`, dengan satu transfer masih Menunggu verifikasi dan filter tetap aktif.

Tombol `201:2` disalin dari `185:95`, lalu disamakan dengan gaya ghost kecil sumber: fill/stroke dihapus, padding horizontal 12 dan vertikal 8, tinggi minimum 40, radius 5, teks DM Sans 13 berbobot 600 dengan warna #173941. Wadah ringkasan memakai Hug dan otomatis menjadi tinggi 66; tampilan sudah diperiksa rapi. Tidak ada frame tambahan pada langkah ini; jumlah tetap 47.

H1 **Pembayaran** dan H2 **Penerimaan dana** pada `109:2`, `147:2`, `163:2`, dan `185:2` telah diubah ke bobot 650. Pemeriksaan visual `163:2` menunjukkan tidak ada tumpang tindih. Perubahan bobot tiga salinan lainnya tersimpan, tetapi belum menjadi bukti pemeriksaan visual masing-masing.

### Batas cabang terfilter

- Pada tahap ini pencarian dan dropdown status belum interaktif. Bagian berikutnya mencatat kemajuan pemilih status; pencarian tetap belum tersambung.
- Pada tahap awal Escape `186:2` belum diuji. Pengujian lanjutan kelima dialog pada bagian “Escape dialog dan menu pembayaran” di bawah sudah membuktikan perilaku keyboard tersebut.
- Aplikasi mengingat filter saat pindah modul dan kembali melalui menu. Prototipe belum mempertahankan ingatan filter ini; navigasi dari keadaan recorded dapat kembali ke Semua status.
- Pemeriksaan visual salinan heading pembayaran selain `163:2`, serta audit heading lain, masih belum lengkap.
- Form transaksi, hasil aksi lainnya, administrasi mobile, dan sambungan internal desktop tetap belum lengkap. Tidak ada klaim semua tombol selesai.

## Lima pilihan status pembayaran — 24 September 2026

Pada tahap penambahan filter total menjadi **55 frame mobile: 45 layar 390×844, tiga drawer navigasi 320×844, lima dialog pembayaran selebar 358, dan dua menu filter 314×220**. Enam layar hasil dan dua menu dibuat melalui editor; kode bisnis tidak berubah. Penambahan Administrasi mobile pada bagian berikutnya menaikkan total terbaru menjadi 56 frame.

| Nomor / isi | Node | Ukuran |
| --- | --- | --- |
| 49 · Menu status sebelum verifikasi | `215:2` | 314×220 |
| 50 · Menu status setelah verifikasi | `218:14` | 314×220 |
| 51 · Terverifikasi sebelum verifikasi | `210:2` | 390×844 |
| 52 · Terverifikasi setelah verifikasi | `232:2` | 390×844 |
| 53 · Belum teridentifikasi sebelum verifikasi | `245:2` | 390×844 |
| 54 · Ditolak sebelum verifikasi | `243:2` | 390×844 |
| 55 · Belum teridentifikasi setelah verifikasi | `249:2` | 390×844 |
| 56 · Ditolak setelah verifikasi | `236:2` | 390×844 |

Menu pilihan Figma berupa overlay terpusat tanpa latar redup, dengan klik di luar untuk menutup. Ini simulasi pemilih status; bentuknya bukan replika persis popup bawaan sistem operasi dan bukan input HTML yang dapat diedit. Pencarian tetap belum tersambung.

### Kontrak hasil pada data demo

Sumber `payment-views-v15.ts` membedakan status tersimpan dan status tampilan: pembayaran verified tanpa pembeli yang valid masuk **Belum teridentifikasi**, sedangkan pembayaran recorded tanpa pembeli tetap **Menunggu verifikasi**. Pemeriksaan basis data dilakukan read-only pada kolom pembayaran/alokasi/refund demo, tanpa membaca kredensial. Basis data saat diperiksa sudah sesudah verifikasi; keadaan awal direkonstruksi dari sumber seed dan bukti frame awal, tanpa mengubah basis data.

| Pilihan | Sebelum verifikasi Rp175.000 | Sesudah verifikasi Rp175.000 |
| --- | --- | --- |
| Semua status | 3 catatan, `109:2` | 3 catatan, `147:2` |
| Menunggu verifikasi | 1 catatan, `163:2` | Kosong, `185:2` |
| Terverifikasi | 2 catatan, `210:2` | 2 catatan, `232:2` |
| Belum teridentifikasi | Kosong, `245:2` | 1 catatan, `249:2` |
| Ditolak | Kosong, `243:2` | Kosong, `236:2` |

Dua pembayaran Terverifikasi adalah PIC demo Rp108.000 (referensi TEST-ce87981d) dan Divisi Operasional Rp350.000 (TRF-DEMO-001), keduanya bersisa Rp0. Dana Rp175.000 tidak memiliki referensi atau pembeli, dengan sisa Rp175.000 setelah verifikasi. Hasil positif terfilter menampilkan jumlah dari tiga catatan dan Hapus filter kecil; hasil kosong menampilkan pesan sumber serta Hapus filter, tanpa tabel/pagination.

### Sambungan menu yang tersimpan

| Cabang | Pembuka pilihan status | Urutan baris pilihan dan tujuan |
| --- | --- | --- |
| Sebelum | `109:72`, `163:57`, `210:57`, `245:57`, `243:57` → `215:2` | `211:6→109:2`; `218:2→163:2`; `218:5→210:2`; `218:8→245:2`; `218:11→243:2` |
| Sesudah | `147:72`, `185:72`, `232:57`, `249:57`, `236:57` → `218:14` | `218:15→147:2`; `218:17→185:2`; `218:19→232:2`; `218:21→249:2`; `218:23→236:2` |

Sepuluh pembuka memakai On click → Open overlay → Instant, posisi Centered, tanpa latar redup, dan outside-close. Sepuluh baris menu memakai On click → Navigate to → Instant. Semua pembuka dan pilihan telah lulus klik dan visual dalam Present, termasuk membuka ulang menu pada setiap layar hasil.

Siklus sebelum verifikasi: `109:2 → 163:2 → 210:2 → 245:2 → 243:2 → 109:2`. Siklus sesudah verifikasi: `147:2 → 185:2 → 232:2 → 249:2 → 236:2 → 147:2`. Label dan jumlah hasil sesuai tabel kontrak di atas. Klik di luar menu pada `109:2`, `147:2`, dan `249:2` menutup overlay tanpa mengubah hasil. Bukti ini belum mencakup seluruh instans outside-close atau keyboard.

Kedua menu memakai lima baris 304×42 dalam frame 314×220, latar putih, border #D5E1E5, radius 5, teks DM Sans 15 Regular. Regresi select tersembunyi pada `109:72` sudah dipulihkan; Present baru dan siklus filter akhir lulus.

### Bukti Hapus filter dan konsistensi cabang

| Asal / kontrol | Tujuan | Bukti |
| --- | --- | --- |
| `210:2`, tombol `229:2` | `109:2` | Klik, tujuan, dan visual lulus |
| `232:66` | `147:2` | Klik, tujuan, dan visual lulus |
| `245:75` | `109:2` | Uji ulang klik, tujuan, dan visual lulus setelah dropdown dipulihkan |
| `243:75` | `109:2` | Klik, tujuan, dan visual lulus |
| `249:2`, tombol `259:2` | `147:2` | Klik, tujuan, dan visual lulus |
| Kontrol pada `236:2`; ID tombol belum dicatat | `147:2` | Klik, tujuan, dan visual lulus |

Asal `245:2` sudah diperiksa visual: Belum teridentifikasi, 0 pembayaran dari 3 catatan. Saat reset pertamanya, dropdown `109:72` sempat tersembunyi akibat pilihan layer; visibilitas sudah dipulihkan. Uji ulang langsung `245:75→109:2` kemudian lulus klik dan visual, dengan dropdown Semua status dan tiga pembayaran terlihat normal. Keenam reset pada tabel di atas kini lulus.

Panel editor membuktikan `232:5→152:2`, `232:18→152:120`, `232:66→147:2`; `245:5→119:2`, `245:18→120:2`, `245:75→109:2`; `243:5→119:2`, `243:18→120:2`, `243:75→109:2`. Sambungan lainnya mengikuti salinan awal/hasil. Buka menu dari `232:2` lalu X sudah diuji visual kembali ke asal; avatar `232:18` juga lulus visual menuju Profil Sinta `152:120`.

Uji lanjutan pada `245:2` dan `243:2` memakai klik nyata dan screenshot. `245:5` membuka menu `119:2`; X kembali ke hasil kosong Belum teridentifikasi, lalu `245:18` membuka Profil Sinta `120:2`. `243:5` membuka menu yang sama; X kembali ke hasil kosong Ditolak, lalu `243:18` membuka Profil Sinta. Kedua penutupan mempertahankan label filter dan hasil kosong. Header/avatar `249:2` kemudian diuji pada bagian “Penutup drawer Penagihan dan avatar”; `210:2` dan `236:2` belum diuji Present satu per satu.

### Perbaikan judul keadaan kosong dan panel lanjutan

Sumber judul keadaan kosong memakai DM Sans 650, 17 px/24,65 px. H2 Nota kredit, Pengembalian saldo pembeli, dan Biaya operasional memakai DM Sans 650, mobile 17 px/23,8 px. Node dipilih berdasarkan teks yang persis sama, lalu jumlah dan gaya diperiksa sebelum diubah.

| Kelompok yang diperbaiki | Jumlah node | Ukuran / tinggi baris Figma | Bobot |
| --- | --- | --- | --- |
| Tidak ada pembayaran yang cocok | 4 | 17 / 24,6 px | 650 |
| Nota kredit | 14 | 17 / 23,8 px | 650 |
| Pengembalian saldo pembeli | 14 | 17 / 23,8 px | 650 |
| Biaya operasional | 11 | 17 / 23,8 px | 650 |

Screenshot `245:2` membuktikan judul keadaan kosong dan Nota kredit berbobot sesuai tanpa tumpang tindih. Bagian bawah `236:2` membuktikan Pengembalian saldo pembeli dan Biaya operasional tanpa tumpang tindih, wadah tetap muat pada lebar 390. Sampel tersebut tidak menjadi klaim audit seluruh instans atau seluruh tabel.

Tabel biaya `236:129` semula memotong kolom kanan karena No scrolling. Overflow diubah menjadi Horizontal; Clip content tetap aktif pada ukuran 352 × 43,5. Uji Present di bagian bawah `236:2` menunjukkan geser kanan menampilkan **Bukti & tindakan** secara utuh, sedangkan bagian awal kolom Biaya bergeser keluar sesuai gerakan tabel. Hanya isi tabel bergerak.

Perbaikan diperluas melalui Select matching layers ke **11 wadah Table** yang sama-sama berukuran 352 × 43,5 dengan Clip aktif. Pilihan 11 layer diverifikasi sebelum semua diubah menjadi Horizontal; tidak ada perubahan nama dari dialog pemeriksaan. Uji tambahan `109:2` serta `147:2` memperlihatkan Bukti & tindakan utuh, dengan bagian halaman tetap dan hanya isi tabel bergerak. Tiga instans (`236:2`, `109:2`, `147:2`) lulus; delapan lainnya baru terbukti pengaturannya tersimpan, belum pengujian individual. Bukti ini tidak mencakup klik tindakan biaya.

Ingatan filter ketika keluar modul lalu kembali, pencarian, keyboard pemilih dan form belum lengkap. Perkembangan administrasi mobile serta navigasi desktop dicatat pada bagian berikutnya.

## Sidebar Kepala Toko desktop — 24 September 2026

Sembilan frame yang ditangani: Dashboard `20:5`, Katalog `29:5`, Pesanan `22:5`, Pengiriman `21:5`, Persediaan `24:5`, Pengadaan `23:5`, Invoice `25:5`, Laporan `26:5`, Profil `27:5`. Tidak ada frame baru.

Sebanyak **99 dari 99 sambungan tersimpan telah diuji**: 72 sidebar antarmodul (delapan tujuan pada masing-masing dari sembilan layar), delapan avatar, sembilan Keluar akun, delapan logo menuju Dashboard, login staf ke Dashboard, dan pintasan Dashboard ke Pesanan. Item modul yang sedang aktif tidak mengubah halaman. Uji lanjutan melengkapi 49 rute sidebar yang belum diuji pada tahap awal dan kemudian menambahkan delapan logo.

| Cakupan uji Present | Jumlah | Hasil |
| --- | --- | --- |
| Dashboard → delapan modul/profil | 8 | Tujuan berhasil dibuka |
| Delapan modul/profil → Ringkasan | 8 | Kembali `20:5` |
| Profil → tujuh modul selain Ringkasan | 7 | Tujuan berhasil dibuka |
| Tujuh modul → enam modul lain | 42 | Setiap klik, URL tujuan, dan screenshot lulus |
| Tujuh modul → Profil | 7 | Setiap klik, URL tujuan, dan screenshot lulus |
| Avatar delapan layar selain Profil | 8 | Membuka `27:5` |
| Keluar akun pada sembilan layar | 9 | Membuka login staf `10:2` |
| Logo delapan modul/profil | 8 | Membuka Dashboard `20:5`, klik/URL/screenshot lulus |
| Masuk ke ruang kerja `10:170` | 1 | Membuka Dashboard `20:5`, klik/URL/screenshot lulus |
| Tinjau pesanan masuk `20:74` | 1 | Membuka Pesanan `22:5`, klik/URL/screenshot lulus |

Kontrol Dashboard: `20:380` Katalog, `20:388` Pesanan, `20:403` Pengiriman, `20:413` Persediaan, `20:430` Pengadaan, `20:444` Invoice, `20:453` Laporan, `20:467` Profil. Contoh kontrol Ringkasan pada Katalog adalah `29:940`. ID avatar/Keluar per instans belum dirinci dalam laporan; jangan mengarangnya dari pola salinan.

Sidebar sebelumnya berukuran 240×720 dan memotong footer Keluar. Position Fixed serta Overflow Vertical diterapkan pada kesembilan sidebar. Gulir mandiri dan keterjangkauan sembilan tombol Keluar telah diuji. Ini tidak membuktikan seluruh tabel atau konten modul dapat digulir dengan benar.

Uji lanjutan memakai masing-masing sumber Katalog `29:5`, Pesanan `22:5`, Pengiriman `21:5`, Persediaan `24:5`, Pengadaan `23:5`, Invoice `25:5`, dan Laporan `26:5`. Dari setiap sumber diuji enam modul lain dalam himpunan tersebut serta Profil `27:5`: 42 + 7 = 49 rute unik. Semua tujuan benar melalui klik nyata, URL, dan screenshot setiap perpindahan; tidak ada rute salah. Enam rute Profil → modul dipakai lagi saat pengujian, tetapi tidak dihitung sebagai tambahan koneksi unik. Login dan pintasan Pesanan juga diuji ulang dan lulus.

Delapan logo ditambahkan pada Katalog `29:5`, Pesanan `22:5`, Pengiriman `21:5`, Persediaan `24:5`, Pengadaan `23:5`, Invoice `25:5`, Laporan `26:5`, dan Profil `27:5`, semuanya menuju Dashboard `20:5`. Seluruh delapan rute lulus klik nyata, URL serta screenshot tujuan. ID logo Katalog adalah `29:923`; ID tujuh logo lain belum dirinci. Pilihan massal mengecualikan `31:352`, `32:131`, dan `33:613` agar peran Pembayaran/Periode/Admin tidak diarahkan ke Kepala Toko. Logo Dashboard `20:349` tidak diberi aksi; reaksi None akibat tujuan ke dirinya sendiri dihapus.

Cakupan 99 koneksi ini tidak mencakup tombol bisnis, peran desktop lain, Panduan demo, form, atau transaksi. Login Figma hanya simulasi navigasi Kepala Toko; input kredensial dan validasi belum direplikasi. Bukti pintasan Pesanan memastikan tujuan tampil, bukan seluruh variasi antrean/filter. Pengujian 49 rute tidak membutuhkan perubahan; penambahan delapan logo merupakan perubahan Figma berikutnya. Tidak ada perubahan kode bisnis.

## Escape dialog dan menu pembayaran — 24 September 2026

Kelima frame dialog `132:2`, `136:2`, `138:2`, `140:2`, dan `186:2` memiliki reaksi **Key/Gamepad → Esc → Close overlay**. Reaksi keyboard diterapkan pada frame dialog; tidak ditambahkan aksi klik penutup pada keseluruhan wadah form.

Uji ulang membuka setiap dialog, menekan Escape, lalu memeriksa screenshot layar asal. Catat, Verifikasi, Tolak, dan Identifikasi dari `109:2` kembali ke **Semua status**, tiga catatan, dengan transfer Rp175.000 tetap Menunggu verifikasi. Dialog `186:2` dari `163:2` kembali ke **Menunggu verifikasi**, satu pembayaran dari tiga catatan. Kelima penutup lulus tanpa mengubah transaksi atau filter.

Kedua menu filter `215:2` dan `218:14` juga memiliki **Key/Gamepad → Esc → Close overlay**. Pengujian dari `109:2` dan `147:2` membuka menu melalui klik, menekan Escape, lalu memeriksa tampilan kembali: **Semua status** dan tiga catatan tetap. Kedua penutup lulus. Bukti ini belum mencakup seluruh asal terfilter, tombol panah/Enter untuk memilih, fokus keyboard, Escape drawer, atau pengisian formulir.

## Judul profil dan tombol dialog — 24 September 2026

Tiga H1 **Profil saya** pada `106:2` (Dewi/Kepala Toko), `120:2` (Sinta/Penagihan), dan `152:120` (Sinta/cabang hasil) diubah dari DM Sans Regular menjadi bobot **650**. Ukuran 26 px, tinggi baris 31,2 px, dan jarak huruf −0,91 px dipertahankan sesuai sumber mobile. Ketiga layar sudah diperiksa di Present; identitas akun benar dan judul tidak bertumpang tindih.

H1 Katalog internal `98:31` diubah dari bobot 400 menjadi 650, dengan ukuran 26 px, tinggi baris 31,2 px dan jarak huruf −0,91 px dipertahankan. Screenshot Present `98:2` membuktikan judul satu baris utuh dan terbaca. Enam H1 Kepala Toko lainnya juga diubah dari 400 menjadi 650: Pesanan `99:31`, Pengiriman `100:31`, Persediaan `101:31`, Pengadaan `102:31`, Invoice & piutang `103:31`, dan Laporan `105:31`. H1 Ruang kerja Anda `104:31` sudah 650 sehingga tidak diubah.

Pemeriksaan Present individual pada `104:2`, `99:2`, `100:2`, `101:2`, `102:2`, `103:2`, dan `105:2` menunjukkan judul satu baris utuh, tanpa clipping/tumpang tindih; subjudul dan aksi di bawahnya tetap terpisah. Ditambah pemeriksaan Katalog serta Profil Kepala Toko, sembilan H1 layar utama Kepala Toko kini memiliki bukti visual. Perubahan ini hanya mencakup H1 yang disebut, bukan audit semua heading atau teks dalam modul.

Lima label **Batal** dan lima label tindakan utama pada dialog pembayaran sudah diubah dari bobot 400 menjadi **650**, mengikuti tombol sumber berukuran 14 px dengan tinggi baris 19,6 px. Label utama mencakup Catat `132:64`, Verifikasi dana pada `136:23` dan salinan dialog `186:2`, Tolak `138:28`, serta Simpan `140:39`. Pemeriksaan visual kelima dialog membuktikan kedua label tombol muat dan terbaca. Pemeriksaan bobot/posisi label ini tidak membuktikan pengisian atau pengiriman form.

## Administrasi mobile — 24 September 2026

Frame **25 · Administrasi · Mobile**, `327:2`, sudah ditambahkan pada X 100, Y 7300. Ukurannya 390 × 844 dengan Clip content dan Overflow Vertical. Pada tahap penambahan Admin, total menjadi **56 frame mobile: 46 layar penuh, tiga drawer navigasi, lima dialog pembayaran, dan dua menu filter**. Nomor layar 25 kini terisi; delapan drawer pada bagian berikutnya kemudian menaikkan inventaris menjadi 64.

Capture berasal dari akun Admin Demo / Administrator pada bagian Akun, dengan viewport 390 × 844 dan nol isian kata sandi. Sumber memuat 11 akun demo, tiga divisi, dan tiga pemasok. Tidak ada kata sandi yang diekspor, dan tidak ada perubahan data akun, peran atau kata sandi pada pekerjaan ini. Capture sudah selesai; jangan digunakan ulang.

Sebanyak 145 teks dengan alias bodyFont dipetakan ke DM Sans (1 Bold, 98 Regular, 46 SemiBold). Pemeriksaan Present dari bagian atas hingga footer membuktikan header, H1, tiga tab, angka ringkasan, label filter, baris akun, tiga divisi dan tiga pemasok terbaca. Gulir horizontal akun ke kanan menampilkan status Aktif serta tombol Atur akun dan Atur ulang sandi. Gulir horizontal divisi mencapai alamat dan Ubah; tiga kolom pemasok muat pada lebar 390. Jangkauan tombol tidak dihitung sebagai bukti bahwa dialog atau aksinya berfungsi.

Sebanyak 17 teks diperbaiki dari bobot 400 menjadi 650 sesuai CSS sumber: tiga label `327:83/96/107`, sebelas nama akun `327:143/170/197/224/251/278/305/332/359/386/413`, dan tiga angka ringkasan `327:52/59/66`. Pemeriksaan visual akhir mencakup nama dan angka yang sudah diperbaiki.

Wadah bagian diberi nama **Bagian · Akun & hak akses** (`327:68`), **Bagian · Data divisi** (`327:456`), dan **Bagian · Daftar pemasok** (`327:526`). Visibilitas filter `327:79` dan tabel divisi `327:468` dipulihkan; keduanya terlihat pada gambar akhir. Percobaan Scroll to awal belum menghasilkan tujuan bagian yang dapat dipilih. Reaksi kosong None pada `327:54` dihapus sebelum perbaikan berikutnya. Tidak ada frame baru atau perubahan data/kredensial pada tahap perapian ini.

Navigasi bagian kemudian dihubungkan ke tiga anchor transparan 1 × 1 sebagai anak langsung `327:2`, dengan Ignore auto layout: Akun `440:3` pada X 0/Y 280, Divisi `424:3` pada X 0/Y 2442, dan Pemasok `440:2` pada X 0/Y 2824,45. Reaksi Scroll to `327:47→440:3`, `327:54→424:3`, dan `327:61→440:2` semuanya lulus klik nyata serta screenshot di Present. Akun dan Divisi menempatkan heading sekitar 110 px dari atas; Pemasok mencapai batas gulir alami dengan ketiga pemasok dan footer terlihat. Jumlah frame tetap, karena anchor berada di dalam layar.

Penanda tab masih tetap **Akun** ketika Divisi/Pemasok dibuka. Header, filter, form, dan tombol bisnis Admin belum tersambung; keberhasilan tiga navigasi bagian ini tidak berarti fungsi Admin sudah lengkap.

## Drawer Kepala Toko per modul — 24–25 September 2026

Delapan salinan drawer `107:2` menampilkan penanda modul aktif sesuai layar asal. Ukuran masing-masing **320 × 844**. Total inventaris kini **64 frame mobile: 46 layar, sebelas drawer, lima dialog pembayaran, dan dua menu filter**. Seluruh delapan pembuka, penanda aktif, dan X sudah lulus uji Present; cakupan rute dan batas perangkat dijelaskan di bawah.

| Nomor / isi | Node | Posisi X,Y |
| --- | --- | --- |
| 57 · Menu kepala — Katalog · Mobile | `392:2` | 100,16900 |
| 58 · Menu kepala — Pesanan · Mobile | `405:2` | 650,16900 |
| 59 · Menu kepala — Pengiriman · Mobile | `405:150` | 1200,16900 |
| 60 · Menu kepala — Persediaan · Mobile | `405:298` | 1750,16900 |
| 61 · Menu kepala — Pengadaan · Mobile | `405:446` | 100,18100 |
| 62 · Menu kepala — Invoice · Mobile | `405:594` | 650,18100 |
| 63 · Menu kepala — Laporan · Mobile | `405:742` | 1200,18100 |
| 64 · Menu kepala — Profil · Mobile | `405:890` | 1750,18100 |

Penanda aktif memakai latar #E9F1F0, teks #103F47, aksen sisi dalam #C27147 selebar 3 px, dan DM Sans 650. Ringkasan yang tidak aktif memakai teks #D0E3EA berbobot 450. Label Ringkasan aktif pada drawer asal `107:36` juga diperbaiki ke 650; jalur Dashboard `104:2→107:2` diperiksa visual setelah perbaikan.

Delapan pembuka `98:7`, `99:7`, `100:7`, `101:7`, `102:7`, `103:7`, `105:7`, dan `106:7` menuju drawer 57–64 secara berurutan. Semua memakai Open overlay, Top left, latar redup 25%, dan Close outside. Klik setiap pembuka menunjukkan modul asal aktif, lalu X menutup ke asal; kedelapan uji lulus. Pada Pesanan `99:2`, klik di luar juga diuji dan mempertahankan enam pesanan dengan filter Semua.

Rantai Katalog `98:2` → Pesanan `99:2` → Pengiriman `100:2` → Persediaan `101:2` → Pengadaan `102:2` → Invoice `103:2` → Laporan `105:2` → Profil `106:2` → Dashboard `104:2` lulus klik, URL, dan screenshot melalui drawer masing-masing. Setiap menu yang dibuka menunjukkan indikator modul yang benar. Sembilan tujuan dan Keluar diwarisi dari drawer asal, tetapi hanya rantai delapan perpindahan ini diuji pada salinan; seluruh kombinasi tujuan belum diperiksa. Footer Keluar pada drawer Profil 64 dapat digulir hingga terlihat, tetapi aksi Keluar pada delapan salinan belum diuji ulang. Escape dan outside pada seluruh salinan juga belum terbukti.

Uji Present menggunakan browser **1024 × 1000** dengan frame/desain mobile. Percobaan klik native pada viewport 390×844 tidak memberikan respons, dan screenshot 1280×720 sempat hitam; pengujian dilanjutkan dengan screenshot browser 1024×1000 yang berhasil. Bukti ini memastikan interaksi desain mobile dalam Present tersebut, bukan pengujian pada perangkat ponsel fisik atau interaksi browser 390×844. Tidak ada perubahan pada Penagihan, Admin, atau form dalam pekerjaan ini.

## Penutup drawer Penagihan dan avatar — 24 September 2026

Drawer `119:2` dan `152:2` diberi reaksi **Key/Gamepad → Esc → Close overlay**. Dari `109:2`, menu `119:2` dibuka lalu ditutup dengan Escape; Semua status dengan tiga catatan tetap. Menu dibuka ulang dan ditutup melalui klik di luar; hasil tetap. Dari `249:2`, menu `152:2` dibuka lalu ditutup dengan Escape dan, setelah dibuka ulang, klik di luar; filter Belum teridentifikasi dengan satu dari tiga catatan tetap. Keempat penutup lulus pemeriksaan screenshot.

Tiga avatar juga lulus klik, URL tujuan, serta screenshot: pembayaran awal `109:2` → Profil Sinta `120:2`; hasil Belum teridentifikasi `249:2` → Profil Sinta `152:120`; Katalog Kepala Toko `98:2` → Profil Dewi `106:2`. Bukti ini tidak mencakup semua asal drawer, X dari `249:2`, Keluar cabang hasil, atau Escape drawer Kepala Toko `107:2`.

Logo kedua drawer Penagihan mengikuti sumber `workspace.tsx`, yang membawa pengguna ke dashboard. `119:12→108:2` diuji dari pembayaran `109:2`: klik logo membuka dashboard **Dana masuk menunggu pemeriksaan** dengan satu transfer menunggu verifikasi. `152:11→152:907` diuji dari profil hasil `152:120`: logo membuka Ringkasan hasil dengan satu invoice dan sisa Rp334.000. Kedua jalur lulus URL dan screenshot. Tautan Panduan demo tidak diubah; bukti logo ini tidak mencakup logo peran atau instans lain.

## Pemeriksaan panduan presentasi lokal — 24 September 2026

Panduan `mentor-mockup.html` diperiksa setelah dimuat ulang. Sepuluh tautan sidebar menuju hash bagian yang sesuai, dari `#mulai` hingga `#diskusi`. Dari slide 10, Sebelumnya menuju `#bukti`; Berikutnya kembali ke `#diskusi`. Filter Sudut pandang Penagihan menampilkan tujuh area: Ringkasan, Invoice, Pembayaran, Pengadaan, Laporan, Tutup periode, dan Profil. Mengembalikan Semua fungsi menampilkan 15 area. Kembali ke bagian 01 dan screenshot setelah animasi menunjukkan halaman pembuka dengan posisi 1/10.

Pemeriksaan struktur juga memastikan 10 bagian, 17 ID HTML unik, tautan anchor valid, dan tiga tautan langsung prototipe. Respons HTTP panduan adalah 200. Ini bukti fungsi panduan lokal, bukan bukti tambahan tombol Figma atau seluruh fungsi aplikasi bisnis.
