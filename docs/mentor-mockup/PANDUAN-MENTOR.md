# Unit Toko — panduan presentasi mentor

Disiapkan 24 September 2026 dari kode aplikasi pada workspace Capstone. Diperbarui 25 September 2026, Asia/Jakarta; tanggal data demo dan tangkapan tetap mengikuti sumber.

## Buka hasil

- [Mockup Figma](https://www.figma.com/design/DAVyovkzeqhjSwOXP96Klr) dan [diagram alur FigJam](https://www.figma.com/board/HPRH7NqGQxuU7sL089HjoR).
- [Prototipe desktop](https://www.figma.com/proto/DAVyovkzeqhjSwOXP96Klr/Unit-Toko?node-id=4-2&page-id=0%3A1&scaling=min-zoom&content-scaling=fixed).
- [Prototipe belanja mobile](https://www.figma.com/proto/DAVyovkzeqhjSwOXP96Klr/Unit-Toko?node-id=38-2&page-id=37%3A5&scaling=min-zoom&content-scaling=fixed&starting-point-node-id=38%3A2).
- [Prototipe Penagihan mobile](https://www.figma.com/proto/DAVyovkzeqhjSwOXP96Klr/Unit-Toko?node-id=108-2&page-id=37%3A5&scaling=min-zoom&content-scaling=fixed&starting-point-node-id=108%3A2).
- [Panduan presentasi lokal](http://localhost:3000/mentor-mockup.html) dan [aplikasi yang berjalan](http://localhost:3000).

**Status hasil:** Figma memuat 21 layar desktop, halaman pembuka, dan **64 frame mobile: 46 layar, sebelas drawer navigasi, lima dialog pembayaran, serta dua menu filter**. **Semua tombol belum selesai dihubungkan dan diuji.** Aplikasi lokal menjalankan aturan, otorisasi, perhitungan, dan penyimpanan transaksi; Figma memperagakan skenario yang sudah disiapkan.

Cakupan yang sudah dapat diperagakan:

- **Belanja:** detail produk → keranjang → checkout → detail pesanan telah diuji pada desktop/mobile. Cabang pembatalan mobile juga lulus.
- **Kepala Toko desktop:** 99 sambungan navigasi lulus, terdiri dari 72 rute sidebar, delapan avatar, sembilan Keluar akun, delapan logo ke Dashboard, masuk dari login staf, dan pintasan Pesanan. Ini belum mencakup seluruh tombol bisnis.
- **Kepala Toko mobile:** delapan drawer per modul lulus pembuka, penanda aktif, X, dan rantai perpindahan hingga Dashboard. Pengujian memakai Present di browser; belum membuktikan seluruh kombinasi tujuan atau penggunaan pada perangkat ponsel fisik.
- **Penagihan mobile:** lima pilihan status sebelum/sesudah verifikasi, reset filter, dua jalur verifikasi, serta pintasan invoice belum lunas telah diuji. Escape pada lima dialog dan dua menu filter lulus; dua drawer Penagihan juga lulus Escape/klik di luar dari asal yang dicatat. Logo kedua drawer kembali ke dashboard yang sesuai.
- **Administrasi mobile:** tampilan dari header hingga akun, tiga divisi, tiga pemasok, dan footer diperiksa. Tabel akun/divisi dapat digeser ke kolom kanan; klik Akun/Divisi/Pemasok menggulir ke bagian masing-masing. Penanda tab masih Akun, sedangkan header, filter, form, dan tombol bisnis belum tersambung.
- **Perapian visual:** keluarga font memakai DM Sans dan Archivo Black. Sembilan H1 Kepala Toko, profil Penagihan, serta label tombol lima dialog telah diperiksa setelah perbaikan bobot. Audit semua teks/layar belum lengkap.

Untuk skenario Penagihan, mulai dari dashboard awal dan tekan **Periksa pembayaran**. Satu transfer Rp175.000 tampil pada filter Menunggu verifikasi. Konfirmasi verifikasi mempertahankan filter sehingga hasil menjadi kosong. **Hapus filter** menampilkan tiga catatan, termasuk dana Belum teridentifikasi. Jalur Semua status juga tersedia; setelah verifikasi, buka Laporan lalu kembali Pembayaran untuk menunjukkan hasil yang tetap. Dashboard hasil menyediakan **Buka invoice belum lunas**: satu invoice Dibayar sebagian dengan sisa Rp334.000.

Pencarian, ingatan filter lintas modul, panah/Enter pemilih status, sebagian keyboard drawer, pengisian/validasi form, serta fungsi bisnis lainnya masih perlu dilengkapi. Paket Starter membatasi logika variabel/kondisi, sehingga skenario memakai cabang layar statis tanpa upgrade. Penyuntingan berlangsung melalui editor setelah kuota MCP habis.

Bukti per kontrol dan batasnya ada di [ledger interaksi](LEDGER-INTERAKSI.md), [catatan perapian](PERAPIAN-RESPONSIF.md), dan [status Figma](STATUS-FIGMA.md). Angka navigasi yang lulus tidak berarti semua kemungkinan transaksi telah diuji.

## Tujuan produk

Unit Toko menyatukan belanja pelanggan, permintaan divisi, pengelolaan stok, pengadaan, pengiriman, penerimaan barang, penagihan, pembayaran simulasi, dan tutup periode. Informasi pesanan menjadi dasar aktivitas peran berikutnya sehingga riwayat proses dapat ditelusuri.

Produk ini merupakan studi kasus capstone. Bukan situs resmi BNI. Identitas akun, barang, transaksi, alamat demo, pengiriman, dan pembayaran bersifat sintetis. Tidak ada transfer uang atau integrasi perbankan nyata.

## Alasan desain

| Keputusan | Penerapan | Alasan bagi pengguna |
| --- | --- | --- |
| Identitas yang konsisten | Petrol `#073B45`, latar terang `#F4F7F7`, teks `#142D33`, aksen `#C84513` dan `#F47B49` | Mempertahankan kesinambungan dari etalase ke ruang kerja; aksen menonjolkan tindakan penting. |
| Dua tingkat tipografi | DM Sans untuk teks dan data; Archivo Black untuk judul editorial | Teks operasional tetap mudah dibaca; halaman publik memiliki judul yang kuat. |
| Beranda editorial | Foto produk, tiga koleksi, urutan cara belanja, dua akses | Pengunjung mengenali isi toko dan jalur masuk sebelum menghadapi form. |
| Portal terpisah | Login pelanggan dan login divisi/staf | Mengurangi salah konteks. Peran tetap ditentukan akun dan diperiksa server. |
| Dashboard mengikuti peran | Pekerjaan berikutnya, antrean, ringkasan aktivitas | Pengguna langsung melihat tindakan yang relevan bagi tanggung jawabnya. |
| Satuan dan kemasan eksplisit | Harga, unit, ketersediaan, subtotal dan jumlah | Mengurangi kebingungan antara satuan barang dengan isi paket. Foto bukan bukti isi kemasan. |
| Checkout singkat | Penerima, kontak, alamat, catatan, ringkasan | Pengguna memeriksa tujuan dan pesanan; belum membayar pada tahap ini. |
| Status dan riwayat | Label status, perjalanan pesanan, informasi tindakan | Progres tidak hanya dijelaskan dengan warna. |
| Daftar operasional | Pencarian, filter, pagination, tabel responsif | Membantu menemukan transaksi tanpa memuat seluruh daftar sekaligus. |
| Pemulihan pekerjaan | Draf, konfirmasi meninggalkan form, pemulihan sesi | Mengurangi kehilangan isian saat navigasi atau permintaan gagal. |
| Gerakan terkontrol | Animasi beranda, carousel dengan jeda, preferensi reduced motion | Mendukung orientasi dan eksplorasi; fungsi tetap dapat diakses tanpa mengandalkan gerakan. |

Replika Figma mempertahankan foto, komposisi, ukuran, dan konten dari aplikasi lokal. Alias web `bodyFont` dan `displayFont` dipetakan ke DM Sans dan Archivo Black. Heading dashboard memakai DM Sans variable berbobot 650; sejumlah judul dan tombol sudah diperbaiki agar tidak berubah menjadi Regular saat penggantian font. Audit bobot seluruh teks masih berjalan.

## Peta layar dan fungsi

| Area | Fungsi pada aplikasi | Tujuan demo |
| --- | --- | --- |
| Beranda `/` | Koleksi, cara belanja, pintu masuk pelanggan/internal, footer | Jelaskan sasaran pengguna. |
| Etalase `/shop` | Cari nama/SKU, filter kategori/koleksi/kemasan/stok, urutkan, pagination, carousel | Temukan kebutuhan secara bertahap. |
| Detail `/shop/air` | Pilih kemasan, ubah jumlah, subtotal, tambah keranjang, beli sekarang | Tunjukkan kejelasan harga dan unit. |
| Keranjang `/cart` | Pilih barang, ubah kemasan/jumlah, hapus, checkout pilihan | Barang yang tidak dipilih tetap tersimpan. |
| Checkout `/checkout` | Pemeriksaan akun, draf penerima/alamat, pengajuan pesanan | Tagihan mengikuti penerimaan final, bukan checkout. |
| Akun pelanggan | Profil, foto, alamat bawaan, kata sandi, logout | Pesanan lama menyimpan alamat saat transaksi. |
| Pesanan pelanggan | Detail, status, pembatalan sisa, penerimaan, komplain, lampiran, pembayaran invoice | Pelanggan hanya melihat transaksi sendiri. |
| Ringkasan internal | Prioritas dan antrean per peran, tautan tindakan | Dashboard memandu pekerjaan berikutnya. |
| Katalog internal | Pilih produk dan jumlah, keranjang divisi, ajukan pesanan | Kebutuhan divisi terhubung ke operasi toko. |
| Pesanan | Tinjau/setujui/tolak, reservasi/lepas stok, pengganti, pembatalan sisa | Pengganti membutuhkan keputusan pemesan. |
| Pengiriman | Surat jalan, penugasan kurir, kirim, bukti, gagal/kembali, penerimaan parsial | Kurir hanya melihat tugasnya; harga/HPP tidak ditampilkan. |
| Penyelesaian | Komplain, retur dari kiriman, finalisasi penjualan | Stok tidak dikurangi dua kali saat finalisasi. |
| Persediaan | Batch, lokasi, kedaluwarsa, stok tersedia/ditahan, retur, karantina, opname | Barang bermasalah tidak otomatis dijual lagi. |
| Pengadaan | OMI reguler, DDO, Smart, Pasar Kering, konfirmasi, penerimaan parsial, tutup sisa | Stok bertambah sesuai jumlah benar-benar diterima. |
| Dana pengadaan | Ajukan, setujui, catat dana tersedia; pembayaran pemasok | Pasar Kering memiliki alur pendanaan tersendiri. |
| Invoice & piutang | Pilih penerimaan final, terbitkan invoice, cetak/PDF, pantau sisa dan jatuh tempo | Dasar tagihan dapat ditelusuri. |
| Pembayaran | Catat, lampiran, identifikasi, verifikasi/tolak, alokasi parsial atau multi-invoice | Sisa dana tetap tercatat setelah alokasi. |
| Koreksi | Nota kredit, persetujuan/penolakan, saldo, refund | Invoice asal tetap tersimpan. |
| Biaya | Ajukan, setujui/tolak, bayar simulasi | Pengeluaran memiliki jejak persetujuan. |
| Laporan | Filter periode/divisi/sumber, rekap, cetak, CSV, jurnal | Pembaca mengikuti informasi sesuai akses. |
| Tutup periode | Ajukan revisi, kembalikan, setujui versi, tutup dengan snapshot | Pembayaran bulan berikutnya tidak mengubah snapshot bulan tertutup. |
| Administrasi | Akun/peran/status, reset sandi, divisi, pemasok, data produk sesuai kewenangan | Administrator tidak otomatis memperoleh kewenangan transaksi bisnis. |

## Peran dan urutan pergantian akun

| Peran | Akun demo | Fokus |
| --- | --- | --- |
| Pelanggan | `customer@unit-toko.demo` | Belanja dan transaksi pribadi |
| PIC divisi | `pic-a@unit-toko.demo`, `pic-b@unit-toko.demo` | Kebutuhan dan penerimaan divisi masing-masing |
| Kepala toko | `kepala@unit-toko.demo` | Tinjauan pesanan, pengadaan, persetujuan opname |
| Staf toko | `staf@unit-toko.demo` | Reservasi, surat jalan, penerimaan pemasok, finalisasi |
| Kurir | `kurir@unit-toko.demo` | Pengiriman yang ditugaskan |
| Penagihan | `penagihan@unit-toko.demo` | Invoice, verifikasi dan alokasi dana |
| Pengolah laporan | `laporan@unit-toko.demo` | Rekap dan pengajuan bulan |
| Pimpinan | `pimpinan@unit-toko.demo` | Persetujuan laporan, dana, kredit, biaya/refund |
| Akuntansi | `akuntansi@unit-toko.demo` | Pembukuan dan tutup periode |
| Administrator | `admin@unit-toko.demo` | Akun, peran dan data master |

Kata sandi berada di `website/.data/demo-accounts.json` secara privat. Jangan masukkan berkas itu ke Figma, bahan presentasi, Git, atau tangkapan layar. Keluar akun sebelum berganti peran. Login tidak menyediakan pemilihan peran bebas.

## Naskah presentasi 12–15 menit

1. **Menit 0–2 — masalah dan sasaran.** “Aplikasi ini menghubungkan kebutuhan pelanggan dan divisi dengan pekerjaan toko, pengiriman, serta pencatatan keuangan. Setiap peran memperoleh tindakan sesuai tanggung jawab.” Buka beranda dan dua portal.
2. **Menit 2–5 — pengalaman pelanggan.** Cari Air mineral 600 ml, perlihatkan perbedaan satu dus dan paket, tambahkan ke keranjang, lalu buka checkout. Jelaskan bahwa pembayaran dilakukan setelah invoice diterbitkan. Gunakan data sintetis.
3. **Menit 5–8 — operasi.** Buka diagram utama. Kepala meninjau, staf mereservasi dan membuat surat jalan, kurir mengirim, pelanggan/PIC menerima, staf menyelesaikan komplain lalu memfinalkan. Jelaskan bahwa kiriman bisa parsial.
4. **Menit 8–10 — keuangan.** Tunjukkan invoice, pembayaran, verifikasi dan alokasi. Contoh pengujian lokal berhasil membuat invoice Rp108.000 dari dua dus dan melunasinya melalui simulasi.
5. **Menit 10–12 — pengendalian.** Buka diagram pengadaan dan koreksi. Jelaskan pendanaan Pasar Kering, opname, nota kredit, dan snapshot periode.
6. **Menit 12–15 — desain dan evaluasi.** Jelaskan portal terpisah, dashboard berbasis tugas, status eksplisit, dan pemulihan draf. Sampaikan batas prototipe Figma dan hasil uji aplikasi. Minta masukan atas alur, kewenangan, dan kelengkapan informasi.

Contoh pesanan pelanggan dari pemeriksaan UI: **PO/202609/0006**, satu dus Air mineral 600 ml, Rp54.000, status awal “Menunggu tinjauan”. Alamat dan kontaknya sintetis. Status dapat berubah jika dipakai lagi saat demo.

## Diagram yang tersedia

1. **Pesanan sampai tutup periode:** pilihan barang → checkout → tinjauan kepala → reservasi → surat jalan → pengiriman → penerimaan atau komplain → finalisasi → invoice → pembayaran simulasi → verifikasi/alokasi → pengajuan bulan → persetujuan pimpinan → penutupan akuntansi.
2. **Pengadaan dan koreksi:** pembelian dengan cabang pendanaan Pasar Kering, penerimaan parsial, penambahan stok, retur/opname; serta nota kredit, saldo lebih, alokasi/refund, dan persetujuan.

## Bukti pemeriksaan saat penyusunan

- Node.js **24.19.0**, server Next.js lokal aktif pada port **3000**.
- **266 tes otomatis lulus, 0 gagal**. Log lokal: `tmp/mentor-tests.log`.
- Pemeriksaan HTTP alur lengkap berhasil: order, persetujuan, reservasi, surat jalan, pengiriman, penerimaan, finalisasi, invoice, lampiran privat, pembayaran, verifikasi/alokasi, dokumen, CSV, dan persistensi setelah login ulang. Log: `tmp/mentor-api.log`.
- Pemeriksaan TypeScript **lulus**. Log: `tmp/mentor-typecheck.log`.
- Browser: login pelanggan, buka detail produk, tambah keranjang, checkout, membuat pesanan dan melihat status berhasil diuji.
- Suite khusus `smoke-access-v8` **belum dijalankan**: pengaman meminta basis data QA terisolasi pada port 3007. Ini bukan hasil lulus dan tidak dihitung sebagai cakupan pemeriksaan di atas.
- Pengujian build produksi, audit aksesibilitas lengkap, semua ukuran layar, dan setiap kombinasi peran secara manual tidak diklaim selesai.

## Menjalankan ulang

Gunakan Node.js 24. Dari folder `website`, jalankan `npm run dev`, lalu buka `http://localhost:3000`. Konfigurasi lokal dan kredensial demo sudah disiapkan. Jangan menimpa seed atau basis data jika ingin mempertahankan transaksi presentasi.

Tangkapan Figma bersifat opsional: `FIGMA_CAPTURE=1` hanya memuat skrip tangkapan saat mode pengembangan. Skrip tidak dimuat pada build produksi. Tanpa variabel itu, aplikasi berjalan seperti biasa.

## Pertanyaan untuk mentor

- Apakah pembagian kewenangan sesuai proses unit toko yang hendak dimodelkan?
- Apakah dasar invoice setelah penerimaan final sudah tepat untuk studi kasus ini?
- Informasi apa yang masih dibutuhkan saat terjadi kekurangan barang, pengganti, atau retur?
- Apakah ringkasan per peran membantu menentukan pekerjaan berikutnya?
- Integrasi nyata mana yang perlu diprioritaskan setelah mockup dan simulasi disetujui?
