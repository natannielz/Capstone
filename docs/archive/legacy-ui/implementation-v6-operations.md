# Implementasi operasional v6

17 September 2026. Mencakup U02/U03 serta integrasi tampilan B07/B10 pada rencana v6. Identitas visual v5, izin peran, dan perhitungan transaksi tetap digunakan. Implementasi ini belum dengan sendirinya membuktikan rilis produksi; build, QA browser, dan deployment dikerjakan agen utama.

## Persediaan

`InventoryWorkspace` menggantikan dua blok stok panjang. Bagian Produk, Batch, Retur, dan Opname memakai tab yang mendukung keyboard. Pencarian nama/SKU dan kode batch, filter OMI/Smart, kondisi stok, jumlah hasil, serta pagination 12 baris disimpan pada URL. Kondisi mencakup restok, kedaluwarsa, menjelang kedaluwarsa, dan barang ditahan. Pengingat akhir bulan dibatasi ke hari terakhir bulan tujuan.

Produk/batch memakai tabel desktop dan kartu pada lebar viewport sampai 1100px. Kartu menampilkan stok tersedia beserta satuan, kondisi, lokasi/tanggal yang relevan, dan tindakan pada area yang sama. Retur/opname memakai catatan ringkas dengan status dan tindakan. Tidak perlu menggeser tabel untuk menemukan stok tersedia pada ponsel.

Lihat batch menggunakan navigasi workspace sehingga Back dapat memulihkan URL/filter/posisi daftar. Atur barang tersedia pada produk meskipun belum memiliki batch. Semua tindakan lama dipertahankan sesuai peran: Kepala menambah/mengatur produk, melepas retur ditolak, dan menyetujui opname; Staf mencatat retur, keputusan pemasok, dan opname; Pengolah Laporan hanya melihat.

UI opname memakai `stocktakeStatus` dari implementasi domain B07. Pengajuan usang berlabel Perlu hitung ulang, tidak menawarkan persetujuan; Staf dapat membuat penghitungan pengganti dengan `replacesId`. Pembatalan hanya ditawarkan kepada Kepala atau Staf pemilik. Riwayat pengganti/alasan tetap terlihat.

## Laporan dan pengadaan

- Penjualan menampilkan rentang tanggal **finalisasi penerimaan**, sumber barang, dan divisi. Total serta CSV memakai selector baris yang sama. CSV memuat seluruh hasil filter, bukan hanya halaman yang terlihat, dan menjelaskan bahwa nilai sebelum nota kredit/pajak invoice. Rentang tanggal terbalik mendapat error inline.
- Keuangan memakai **saldo kumulatif sampai tanggal**, ringkasan hasil usaha, dan neraca. Ekspor diberi label CSV neraca & jurnal; mencakup seluruh jurnal sampai tanggal pilihan. Piutang operasional saat ini dipisahkan dalam rincian yang dapat dibuka dan secara eksplisit tidak mengikuti tanggal saldo historis.
- Jurnal memiliki pagination per jurnal, sehingga baris debit/kredit satu transaksi tidak dipisahkan ke halaman berikutnya. Tampilan ponsel menjadi kelompok akun per transaksi.
- Aktivitas menampilkan paling banyak 100 catatan sesuai proyeksi akses yang sudah ada, 20 per halaman. Nama profil pengguna lain tidak ditambahkan demi mengubah fallback Akun lain.
- Biaya operasional tetap dapat diakses di Keuangan, dengan jumlah belum dibayar pada pembuka rincian. Tindakan dan lampiran tetap memakai komponen lama.
- Pengadaan mempertahankan satu tindakan Buat pengadaan, jarak panel/tombol yang konsisten, dan menonaktifkan konfirmasi Pasar Kering sampai dana tersedia. Aksi global yang tidak relevan di header diatur oleh integrasi workspace.
- Tampilan periode hanya memperlihatkan persetujuan aktif pada status approved/closed, mendukung perbaikan metadata B10 di domain.

## Integrasi

`website/components/workspace.tsx` menggunakan:

```tsx
<InventoryWorkspace {...ctx} query={query} onQueryChange={updateQuery}/>
<Reports {...ctx} query={query} onQueryChange={updateQuery}/>
```

`website/app/layout.tsx` memuat `operations-v6.css` setelah CSS v5. Ekspor `StockOperations` lama dihapus agar tidak ada dua implementasi tindakan stok.

Key URL persediaan: `stockTab`, `stockSearch`, `stockSource`, `stockCondition`, `stockProduct`, `stockPage`. Laporan: `reportTab`, `reportAsOf`, `salesStart`, `salesEnd`, `salesSource`, `salesDivision`, `salesPage`, `journalPage`, `auditPage`.

Berkas utama:

- `website/components/inventory-workspace.tsx`
- `website/components/operations.tsx`
- `website/components/sales-register.tsx`
- `website/app/operations-v6.css`
- `website/lib/domain/operations-views.ts`
- `website/tests/operations-views.test.ts`

## Validasi agen operasional

- Lint terarah pada tiga komponen, helper, dan berkas tes: **0 error, 0 warning**.
- Lima tes helper lulus: agregasi stok layak, pencarian SKU/sumber/kondisi, pengingat akhir bulan/kode batch, parameter/tanggal invalid, kesesuaian filter CSV, dan pengamanan formula/tanda kutip CSV (beberapa pemeriksaan digabung dalam satu tes).
- Typecheck gabungan saat pemeriksaan awal tidak melaporkan error pada berkas operasional; saat itu masih ada tiga error pada pekerjaan katalog/keranjang paralel. Hasil akhir typecheck/build mengikuti validasi agen utama.
- Tes dijalankan di memori dengan fixture sintetis. Tidak ada pengujian yang mengubah produksi, kredensial, atau profil.

QA browser yang harus digabung dalam pemeriksaan utama: stok dan laporan pada 320/390/980/1280px; navigasi tab lewat keyboard; cari SKU → Lihat batch → Back; filter kosong; CSV dengan periode/sumber/divisi; opname usang → hitung ulang/batal dengan peran yang benar; keuangan dan jurnal memakai tanggal/cakupan ekspor yang sama. Ukuran target klik dan tampilan responsif belum diklaim terverifikasi hanya berdasarkan CSS.
