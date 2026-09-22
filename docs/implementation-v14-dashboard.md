# Implementasi v14 — dashboard dan ruang kerja

23 September 2026. Mengikuti [rencana utama](plan-ui-ux-v14.md) dan [riset dashboard](research-v14-dashboard.md). Catatan ini membedakan perubahan yang telah ditulis dari rekomendasi lanjutan.

## Perubahan nyata

- `website/components/dashboard.tsx`: satu panel pekerjaan berikutnya dengan satu tindakan utama, penjelasan, dan jumlah yang berasal dari data; tiga ringkasan pendamping lebih tenang. Heading menjadi “Ruang kerja Anda”. Daftar pesanan terbaru tetap menyertakan riwayat agar data pembatalan/penyelesaian tidak hilang.
- `website/lib/domain/dashboard-views.ts`: selector tampilan murni untuk memilih prioritas, total, dan tujuan. Tidak mengubah state, domain engine, API, sesi, atau aturan pembukuan. Kartu difilter lagi dengan `canOpenPage`, dan seluruh navigasi tetap melalui guard workspace.
- Dashboard keuangan menampilkan lima invoice belum lunas dengan jatuh tempo paling awal, urutan sekunder nomor invoice, penanda lewat jatuh tempo, dan tombol membuka invoice tertentu. Tabel mempunyai caption dan header kolom semantik. Invoice lunas tidak ikut daftar ini.
- Dashboard kurir membuka surat jalan tertentu, mendahulukan tugas dalam perjalanan, menyediakan tombol pada setiap tugas, dan menjelaskan keadaan tanpa tugas aktif. Selector menggunakan state yang sudah dibatasi server.
- Kartu administrator menampilkan pengguna aktif, divisi, dan pemasok nyata. Kartu aktivitas dihilangkan karena halaman pengaturan tidak mempunyai log aktivitas. Semua kartu admin membuka halaman pengaturan yang diizinkan; kartu divisi/pemasok belum berupa jump ke subsection.
- `website/components/workspace.tsx`: menu dikelompokkan menjadi Ruang kerja, Operasional, Keuangan, Akun. Grup kosong tidak ditampilkan. Halaman, ikon, `aria-current`, izin, history/query, drawer, dan guard isian belum disimpan dipertahankan. Tombol “Buat pesanan” di header tetap berada pada daftar pesanan untuk PIC/kepala; dashboard menggunakan panel prioritas agar tidak menduplikasi tindakan utama.
- `website/app/workspace-v14.css`: hierarki panel/header, tipografi data, jarak konsisten, angka tabular, format kolom numerik yang sudah diberi class, kontrol/tombol/form, focus outline, status, profil, tabs, dan kontainer tabel. Semua selector berada di `.workspace`; pengecualian portal drawer dibatasi `.mobile-navigation-sidebar:has(.workspace-nav)` agar hanya menu internal yang terkena.
- Gerakan dashboard hanya fade + translate 5px selama 220ms pada panel prioritas, dengan `prefers-reduced-motion`. Tabel, angka uang, input, dan tombol transaksi tidak dianimasikan berpindah tempat.

## Prioritas menurut peran

| Peran | Pemilihan pekerjaan berikutnya |
|---|---|
| PIC divisi | Pesanan yang membutuhkan keputusan pengganti/penerimaan; jika tidak ada, katalog divisi |
| Kepala toko | Pesanan menunggu tinjauan; jika tidak ada, pemeriksaan persediaan |
| Staf toko | Surat jalan siap berangkat, kemudian pesanan perlu disiapkan, kemudian persediaan |
| Kurir | Surat jalan dalam perjalanan, kemudian siap berangkat, kemudian riwayat |
| Penagihan | Transfer tercatat perlu verifikasi, penerimaan final belum ditagih, kemudian invoice terbuka |
| Pimpinan | Pengajuan koreksi/refund/biaya, pendanaan Pasar Kering, laporan menunggu tinjauan, kemudian laporan umum |
| Akuntansi | Periode telah disetujui untuk diperiksa sebelum tutup; jika tidak ada, laporan keuangan |
| Pengolah laporan | Periode terbuka untuk diajukan; jika tidak ada, rekap penjualan |
| Administrator | Pengaturan akun dan organisasi |
| Pelanggan | Selector tidak menghasilkan dashboard internal |

Ini urutan prioritas antarkelompok pekerjaan yang eksplisit, bukan estimasi urgensi berbasis AI. Daftar invoice memakai tanggal nyata; tidak ada statistik atau pertumbuhan rekaan.

## Jangkauan seluruh modul

| Modul | Implementasi v14 |
|---|---|
| Ringkasan | Struktur baru, selector per peran, tindakan langsung, empty state |
| Katalog barang | Shell/header/form/tombol menerima sistem jarak dan tipografi internal; basket dan checkout dipertahankan |
| Pesanan | Shell, tabel, tombol, dan toolbar konsisten; semua filter serta detail lama dipertahankan |
| Pengiriman | Dashboard kurir diperbarui; modul menerima tipografi tabel, actionbar, dan angka konsisten |
| Persediaan | Header, filter, tab, status, kartu seluler, angka dan tindakan mendapat CSS bersama |
| Pengadaan | Header, panel, tabel, ringkasan total, dan tindakan mendapat CSS bersama |
| Invoice & piutang | Tautan langsung dari dashboard dan tampilan jumlah; komposer dan aturan tagihan tetap |
| Pembayaran | Tabel, kontrol, actionbar, helper text dan angka mendapat CSS bersama; verifikasi/alokasi tetap |
| Laporan | Tab, filter, panel, angka dan ekspor mendapat CSS bersama; rentang/as-of tetap berbeda |
| Tutup periode | Panel, tabel, status, tindakan menerima CSS bersama; versi dan posting tetap |
| Administrasi | Dashboard lebih jujur; tabel/form/tindakan mendapat CSS bersama |
| Profil | Pembagian panel dan identitas lebih jelas, form konsisten, metadata panjang tetap membungkus |

Dialog transaksi memakai portal terpisah; stylesheet internal sengaja tidak menjangkau portal umum agar formulir pelanggan/login tidak ikut berubah. Validasi transaksi dan dialog yang sudah ada tidak ditulis ulang.

## Pemeriksaan yang selesai pada aliran ini

- Focused ESLint pada dashboard, workspace, helper, dan tes: **0 error**, dua peringatan `window.location.assign` yang sudah ada sebelumnya pada workspace.
- **6 tes regresi baru lulus** di `website/tests/dashboard-v14.test.ts`: seluruh akun internal dengan state kosong/terisi mempunyai tujuan yang diizinkan; pembacaan tidak memutasi state; prioritas PIC terisolasi divisi; invoice lunas dikecualikan dan jatuh tempo disortir stabil; kurir membuka tugas yang ditugaskan; admin memakai nilai nyata; tahap keuangan berbeda dengan benar.
- Persiapan tes pertama terkena keterbatasan pembacaan direktori induk oleh esbuild di sandbox. Eksekusi ulang yang diizinkan berjalan memakai fixture lokal saja dan lulus; tidak membaca/mengubah basis data produksi.
- Pemeriksaan browser, TypeScript keseluruhan, build, akses produksi, dan publikasi dilakukan oleh agen utama. Laporan ini tidak mengklaim hasil pemeriksaan tersebut sebelum ada catatan rilis utama.

## Rekomendasi yang belum diterapkan

- Search/filter/pagination baru untuk pembayaran, administrator, dan pengadaan; persist filter pengadaan pada URL.
- Anchor/tab yang membuka tepat seksi divisi atau pemasok pada pengaturan.
- Empty filter tambahan pada pengiriman/invoice dan empty table periode; helper kosong persediaan yang bergantung pada peran.
- Caption/header semantik pada seluruh tabel lama; kolom numerik lama tanpa class belum diubah dengan selector posisi yang berisiko.
- Penyusunan ulang dialog transaksi atau penambahan animasi pada aktivitas finansial.

Rekomendasi tersebut bukan penghambat perbaikan shell/dashboard ini, tetapi tidak dilabeli selesai. Tidak ada klaim penghargaan desain atau studi pengguna.
