# Audit browser v6 — Unit Toko BNI

17 September 2026. Situs: https://unit-toko-bni.vercel.app. Versi produksi setelah penerapan v5. Akun yang digunakan: Kepala Toko demo. Pemeriksaan tidak mengirim pesanan atau mengubah profil/database produksi.

## Temuan yang direproduksi

| ID | Langkah | Hasil aktual | Dampak |
| --- | --- | --- | --- |
| BR-01 | Katalog → pilih Air mineral Paket 6 dus → cari `OMI-001-P3` | Hasil satu keluarga tetap memilih Paket 6, Rp324.000, 22 paket; URL sudah memuat pencarian P3 | SKU yang dicari tidak konsisten dengan kemasan yang akan ditambahkan |
| BR-02 | Tambah satu air → Lanjutkan → pilih Divisi Operasional, isi alamat dan catatan khusus → Periksa → Ubah barang di keranjang → Lanjutkan | Divisi kembali kosong, alamat dan catatan kosong | Koreksi barang menghapus pekerjaan pengguna |
| BR-03 | Viewport 980×900, seluruh 12 keluarga tampil, keranjang berisi satu barang | Pada scrollY 83px, keranjang berada sekitar Y4221px dan tombol lanjut Y4624px. Tombol Lihat keranjang tidak ditampilkan (rect 0×0) | Checkout perlu scroll jauh pada celah breakpoint tablet |
| BR-04 | Persediaan pada 390×844 | Tabel stok 700px dan tabel batch 725px dalam ruang 333px. Kolom stok tersedia/restok tidak terlihat tanpa geser. Ada 36 baris stok dan 36 baris batch tanpa input pencarian | Sulit menemukan barang dan membaca stok siap pakai pada ponsel |

BR-04 adalah temuan kegunaan: tabel dapat memakai scroll horizontal; ini bukan klaim pelanggaran WCAG dengan sendirinya. Lebar dokumen 375px pada viewport 390px, sehingga tidak ada bukti overflow halaman pada kasus ini.

## Pengamatan hierarki halaman

- Pengadaan memiliki tombol global **Buat pesanan** sekaligus **Buat pengadaan** pada bagian pemasok. Label yang hampir sama bersaing meskipun tujuan berbeda.
- Laporan memuat ringkasan keuangan, penerimaan per periode, neraca saldo, piutang saat ini, jurnal, jejak aktivitas, dan biaya operasional dalam satu halaman. Pemisahan cakupan tanggal sudah diberi teks, tetapi masih dapat diperjelas melalui struktur bagian.
- Jejak aktivitas Kepala Toko banyak menampilkan **Akun lain**. Perbaikan nama petugas perlu memeriksa kebijakan akses lebih dahulu; pengamatan ini tidak menjadi izin untuk membagikan seluruh data akun.

## Pemulihan setelah pemeriksaan

Barang uji di keranjang dihapus; keadaan awal keranjang kosong telah dipulihkan. Tidak ada pengajuan checkout. Formulir pengadaan hanya dibuka dan ditutup. Ukuran browser di-reset dan halaman dikembalikan ke profil.

Audit ini tidak mencakup pembaca layar, perangkat sentuh fisik, atau pengukuran performa lapangan. Uji domain kasus pinggir dilakukan terpisah oleh agen dalam memori.
