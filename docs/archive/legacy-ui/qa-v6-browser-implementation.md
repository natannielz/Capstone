# QA browser implementasi v6

17 September 2026. Pengujian dilakukan pada `http://127.0.0.1:3000` dengan akun demo. Ini bukti implementasi lokal; pemeriksaan deployment dicatat terpisah.

## Hasil yang diamati

| Area | Pemeriksaan dan hasil |
| --- | --- |
| SKU / kemasan | Pilih air Paket 6 dus, cari `OMI-001-P3`: pilihan berganti Paket 3 dus, harga Rp162.000, stok dan isi keranjang sesuai P3. |
| Draft checkout | Isi Divisi Human Capital, titik pengiriman dan catatan; dua kali Periksa pesanan → Ubah barang → tambah jumlah → buka checkout. Semua isian tetap tersedia. |
| Dua tab | A berisi air P3; B membuka keranjang yang sama. A mengubah jumlah menjadi 5; B menambah teh 1. A dimuat ulang: air 5 + teh 1, Rp822.000. Tidak kehilangan empat satuan sebagaimana reproduksi v5. |
| Logout / ganti akun | Logout berhasil menuju login. Setelah login sebagai Penagihan lalu kembali Kepala, keranjang Kepala kosong. Tidak mencampur keranjang kedua akun. Skenario jaringan gagal diuji pada handler dan endpoint terisolasi, bukan melalui intersepsi browser. |
| Kelanjutan sesi | Dari landing dengan sesi Kepala aktif, klik Kopi sachet. Langsung tiba di katalog dengan dialog produk kopi terbuka tanpa mengisi login ulang. |
| Keranjang tablet | Pada 950, 951, 980 dan 1000px, pintasan `Lihat keranjang` tampil tetap di dasar viewport. Pada 1001px, keranjang kembali di kolom samping. Tombol pintasan diuji dan menggulir ke keranjang. |
| Persediaan | Cari SKU P3 → Lihat batch → Back memulihkan pencarian dan tab Produk. Pada 390px tersedia kartu nama/SKU, stok 16 paket, kondisi, harga, Lihat batch dan Atur barang tanpa geser mendatar. |
| Opname | Hasil pengujian HTTP lokal terlihat sebagai Dibatalkan, Disetujui, dan Digantikan; jumlah awal/sekarang, alasan, serta hubungan pengganti dapat dibaca. |
| Laporan | Tab Penjualan / Keuangan / Jurnal / Aktivitas dapat diakses. ArrowRight dari Penjualan memindahkan fokus dan pilihan ke Keuangan. Fokus terlihat. Label tanggal finalisasi dibedakan dari saldo kumulatif sampai tanggal. |
| Ekspor | Filter September 2026 + OMI + Divisi Operasional, klik CSV sesuai filter. File `penjualan-2026-09-01-2026-09-17.csv` benar-benar tersimpan di Downloads; metadata dan enam baris sesuai filter. Event unduhan alat browser timeout walau file berhasil tersimpan; isi diverifikasi dari berkas. |
| Invoice 101 baris | Fixture lokal Divisi Teknologi memiliki 101 baris penerimaan final. Pilih empat halaman berisi 25 baris; baris ke-101 dinonaktifkan, jumlah 100/100 dan sisa 1 terlihat. Terbitkan invoice pertama; buat invoice kedua untuk satu baris tersisa. Persistensi melalui API memastikan 101 baris unik ditagih tepat sekali. |
| Dialog invoice | Ditemukan area gulir awal membuat tombol di luar layar ponsel. Diperbaiki menjadi header dan footer tetap, isi tengah bergulir. Dialog dibuka ulang dan diverifikasi visual pada 390px; tombol terbit/batal tetap tersedia. |
| Login sebelum siap | Uji lokal yang terlalu cepat menemukan form native GET sebelum JavaScript siap. Form kini eksplisit POST, isian/tombol dinonaktifkan hingga siap. Browser memperlihatkan keadaan Menyiapkan halaman lalu Masuk; login normal berhasil melalui POST. Tidak mereproduksi dengan kredensial produksi. |
| Pengadaan | Halaman hanya menampilkan tindakan sesuai peran dan pekerjaan pengadaan; tombol global Buat pesanan tidak lagi bersaing di header. |

## Ukuran layar

Persediaan diperiksa pada 320, 390, 768, 980, 1280, 1440px. Tab laporan Penjualan, Jurnal, Aktivitas diperiksa pada ukuran yang sama. Lebar dokumen sama dengan lebar isi viewport (tanpa overflow halaman). Pada Windows scrollbar mengambil sekitar 15px.

Dialog invoice diperiksa pada enam ukuran tersebut, dengan footer berada di dalam viewport. Fokus awal masuk ke pilihan divisi; Escape menutup dialog dan memulihkan fokus. Layout dan fokus tab diperiksa secara visual, bukan hanya berdasarkan CSS. Breakpoint katalog tambahan: 950/951/980/1000/1001px.

Keterbatasan: alat tidak menyediakan kontrol zoom browser 400%; pemeriksaan 320px dilakukan sebagai uji reflow, bukan klaim zoom 400% atau sertifikasi WCAG. Kegagalan jaringan/logout memakai simulasi endpoint/handler yang dicatat di laporan keandalan. Ini pengujian teknis dan review ahli, bukan studi usability dengan pengguna.

Data bisnis pengujian hanya ditambahkan pada database lokal. Tidak ada pesanan, invoice, pembayaran, profil, atau password produksi yang diubah.

Bukti terkait: [101 baris](qa-v6-invoice-ui.json), [fixture](v6-invoice-fixture-result.json), [65 pemeriksaan HTTP](qa-v6-auth.md), [keandalan](qa-v6-reliability.md).
