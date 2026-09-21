# Rilis v6 — Unit Toko BNI

**Status: diterapkan dan terverifikasi pada 17 September 2026.**

- Website: https://unit-toko-bni.vercel.app
- Deployment: https://unit-toko-kgcavn0tv-natannielzs-projects.vercel.app
- ID: `dpl_6LVFXuQmHyvEoL6hXKm4BgmdQ8ch`, Vercel `READY`, target production, alias utama sudah terpasang.
- Rencana: [plan-perbaikan-v6.md](plan-perbaikan-v6.md).

## Yang berubah

| Rencana | Hasil |
| --- | --- |
| B01 | Pencarian SKU memilih kemasan, harga dan stok yang sesuai. |
| B02 | Tanggal transaksi mengikuti dokumen/prasyarat sebelumnya; pencatatan historis yang sah tetap didukung. Invoice memperhitungkan periode tertutup. |
| B03 | Logout menunggu pencabutan sesi berhasil. Kegagalan tetap menampilkan workspace dan pilihan mencoba kembali. |
| B04 | Pembatalan penuh berstatus Dibatalkan, usulan pengganti yang tidak berlaku ditutup, penerimaan parsial dan audit tetap dipertahankan. |
| B05 | Draft pengiriman bertahan setelah mengubah barang, berpindah langkah, dan memuat ulang. |
| B06 | Penerimaan invoice dapat dipilih per baris/halaman, maksimal 100 dengan total dan sisa yang jelas. Fixture 101 diselesaikan melalui UI sebagai 100 + 1 tanpa duplikasi. |
| B07 | Opname usang dapat dibatalkan atau diganti dengan penghitungan baru; hubungan dan alasan tetap tercatat. |
| B08 | Keranjang disinkronkan per akun dengan pembacaan terbaru dan pemeriksaan konflik. Logout/checkout membersihkan draft yang sudah tidak berlaku. |
| B09 | Sesi valid langsung meneruskan pilihan produk dari landing ke halaman yang diizinkan. |
| B10 | Pengajuan ulang periode menghapus persetujuan aktif lama tanpa menghapus audit. |
| U01 | Pintasan keranjang tersedia sampai 1000px, sesuai perubahan tata letak. |
| U02 | Persediaan memiliki tab, pencarian, filter, pagination, kartu ponsel, serta navigasi batch yang mempertahankan konteks. |
| U03 | Tindakan utama mengikuti halaman; laporan dipisah menjadi Penjualan, Keuangan, Jurnal, Aktivitas. Cakupan tanggal dan ekspor diperjelas. |
| U04 | Pemuatan awal, refresh, kegagalan dan retry memiliki status; permintaan refresh berulang digabung, data lama tetap ditampilkan saat gagal. |

Temuan tambahan selama implementasi: login sebelum halaman siap kini menggunakan form POST dengan kontrol disabled hingga siap, batas waktu jaringan, dan penjagaan klik ganda. Dialog invoice pada ponsel memakai header/footer tetap dengan area isi yang bergulir. Teks batas pilihan invoice diperjelas.

Identitas petroleum/oranye, foto yang sudah ada, sembilan peran, aturan divisi, dan riwayat transaksi dipertahankan. Tidak mereset atau mengubah transaksi, profil, maupun password produksi. Pengujian publik hanya membaca data bisnis; login/logout membuat dan mencabut sesi uji.

## Verifikasi

- **86 tes regresi lulus, 0 gagal**, termasuk domain, repository atomik, RBAC, cart, tanggal invoice, logout, dan login sebelum/sesudah hidrasi.
- **65 pemeriksaan HTTP lokal lulus**, mencakup 10 akun yang mewakili sembilan peran, dua PIC, dokumen/CSV, pengiriman parsial → invoice → pelunasan, serta opname usang.
- **5 skenario handler keandalan lulus**: refresh lambat/gagal/retry dan logout 503/offline/retry dengan urutan pembersihan draft.
- Browser lokal: SKU, draft setelah dua koreksi, dua tab, pembersihan keranjang, kelanjutan sesi, ekspor nyata sesuai filter, invoice 101 baris, keyboard/tab/fokus, dan layar 320/390/768/980/1280/1440px. Batas tablet 950/951/980/1000/1001px juga diperiksa.
- TypeScript dan build lokal lulus. Lint: **0 error, 6 warning** (navigasi penuh saat pergantian sesi serta variabel/import lama yang tidak digunakan).
- Build Vercel lulus dan alias utama terpasang. **73 pemeriksaan publik lulus, 0 gagal, 1 dilewati** karena produksi tidak memiliki fixture dokumen pengadaan; hak dokumen pengadaan sudah diuji lokal.
- Browser produksi: persediaan baru dengan pencarian SKU/kartu ponsel tampil benar; produk dari landing langsung membuka dialog katalog dengan sesi aktif. Tidak ada error console pada pemeriksaan ini.

Bukti: [API lokal](qa-v6-auth.md), [browser](qa-v6-browser-implementation.md), [invoice 101 baris](qa-v6-invoice-ui.json), [keandalan](qa-v6-reliability.md), [pemeriksaan publik](vercel-v6-results.json).

## Ukuran aset dan sampel HTTP

Metode sama sebelum/sesudah: tiga GET anonim berurutan per halaman dari runtime Node lokal, lalu ukuran JS/CSS hasil decode. Ini bukan pengukuran LCP, waktu interaktif, skor Lighthouse, atau kinerja lapangan.

| Halaman | JS sebelum → sesudah | CSS sebelum → sesudah | Tiga respons sesudah |
| --- | --- | --- | --- |
| Landing | 590.3 → 590.6 kB | 232.6 → 244.5 kB | 288 / 208 / 78 ms |
| Login | 641.8 → 643.0 kB | 232.6 → 244.5 kB | 112 / 89 / 113 ms |
| Workspace | 873.8 → 926.0 kB | 232.6 → 244.5 kB | 81 / 73 / 73 ms |

Seluruh halaman dan aset terukur mendapat HTTP 200. Fitur keranjang, pemilihan invoice, dan tampilan operasional menambah JS workspace sekitar 6%; CSS bertambah sekitar 5%. Landing tidak mengalami pertumbuhan JS berarti. Sampel jaringan/cache bervariasi, sehingga tidak dipakai untuk mengklaim situs lebih cepat. Tidak menambahkan library runtime atau gambar baru untuk perubahan v6 ini. Data mentah: [sebelum](performance-v6-before.json), [sesudah](performance-v6-after.json).

## Batas yang dicatat

- Zoom browser 400% tidak tersedia pada alat; reflow 320px diuji. Tidak mengklaim sertifikasi aksesibilitas atau studi pengguna.
- Tanpa penyimpanan/Web Locks yang tersedia, keranjang menggunakan memori tab dan memberi pemberitahuan bahwa sinkronisasi/persistensi tidak tersedia.
- Formulir operasional dengan dokumen sumber bertanggal masa depan masih memerlukan penyesuaian tanggal manual. Server menolak kronologi yang salah dan mempertahankan isian untuk koreksi.
- Tetap merupakan demo capstone; tidak ada pembayaran atau integrasi bank sungguhan.

Catatan implementasi: [domain](implementation-v6-domain.md), [katalog](implementation-v6-catalog.md), [operasional](implementation-v6-operations.md).
