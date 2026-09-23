# Rilis v15 — daftar operasional dan profil yang lebih andal

23 September 2026. [Website](https://unit-toko-bni.vercel.app) · [Rencana sebelum implementasi](plan-ui-ux-v15.md).

## Perubahan

- Pembayaran, pengadaan, dan akun administrator kini mempunyai pencarian, filter, jumlah hasil, dan pagination yang mengikuti URL. Pencarian baru kembali ke halaman pertama; daftar tetap mengikuti cakupan akses server.
- Dashboard administrator langsung menuju akun, divisi, atau pemasok. Kartu pemeriksaan pembayaran membuka antrean menunggu verifikasi. Navigasi bagian admin mengarahkan fokus ke judul tujuannya.
- Keadaan belum ada data dibedakan dari pencarian tanpa hasil pada persediaan, pengadaan, pembayaran, pengiriman, invoice, periode, dan administrasi. Petunjuk serta tindakan mengikuti peran.
- Tabel aplikasi memperoleh caption, header kolom semantik, dan perataan angka. Kontrol daftar lebih teratur di layar kecil. Label tersembunyi untuk aksesibilitas kini tetap di dalam kontainer tabel; sebelumnya label tersebut dapat membuat halaman mobile melebar.
- Draf profil pelanggan pulih setelah Back/Forward pada tab yang sama. Pembaruan server menyegarkan kolom yang belum diedit sambil mempertahankan perubahan lokal. Password tidak disimpan dalam draf.
- Logout mencabut sesi sebelum pembersihan browser opsional. Penyimpanan yang gagal tidak menghalangi logout. Respons simpan yang terlambat tidak menghapus draf atau isian yang lebih baru.
- Penulisan history hanya membawa metadata milik aplikasi. Marker privat Next tidak lagi disalin, sehingga perubahan filter tidak melewati sinkronisasi URL framework.

Landing, dua portal masuk, carousel otomatis, footer, dan desain toko dari v14 dipertahankan. Rilis ini menyelesaikan pekerjaan lanjutan yang tertulis pada rencana, dengan tiga agen implementasi dan review silang.

## Validasi lokal

- **266/266 tes lulus**: regresi domain, RBAC, auth, transaksi, draf, dan navigasi. Tambahan v15 mencakup tujuh tes pembayaran, enam pengadaan/admin, sembilan profil, dan enam history.
- TypeScript seluruh aplikasi lulus. ESLint: **0 error, 9 warning** yang sudah ada (tujuh navigasi pemuatan ulang sesi, dua variabel tes tidak terpakai).
- Browser pembayaran: 33 catatan terbagi menjadi tiga halaman; halaman kedua menampilkan 13–24. Pencarian tanpa hasil menghapus nomor halaman, reset mempertahankan modul, dan Back/Forward bertahap memulihkan pembayaran dengan status pilihannya.
- Browser pengadaan: 24 dokumen terbagi menjadi empat halaman; halaman kedua menampilkan 7–12. Pencarian tanpa hasil dan reset bekerja. Pembayaran dan pengadaan pada viewport 320 px memiliki lebar dokumen sama dengan lebar konten (305 px setelah scrollbar).
- Browser admin: kartu pemasok menuju `adminSection=suppliers` dengan fokus pada “Daftar pemasok”. Filter pelanggan menghasilkan dua akun fixture; kombinasi status nonaktif menghasilkan keadaan kosong dengan satu tombol reset. Reset mempertahankan bagian akun. Lebar dokumen 305 px pada viewport 320 px; label/jumlah navigasi kemudian diseragamkan menjadi dua baris.
- Browser persediaan: hasil pencarian kosong, reset, dan opname tanpa data mempunyai pesan sesuai Kepala Toko. Pemeriksaan 390 px tidak menemukan overflow halaman. Halaman pembayaran yang tidak diizinkan tidak mengekspos data dan tetap mempunyai judul yang jelas.
- Browser profil staf: mencoba meninggalkan edit membuka dialog tetap mengedit/buang perubahan. Tidak menyimpan perubahan QA ke server.
- Browser profil pelanggan: nama draf tetap ada setelah Back ke pesanan dan Forward ke profil; refresh data tidak menimpanya. Batalkan perubahan mengembalikan nilai server; Back/Forward berikutnya tidak memunculkan draf dan tombol simpan kembali nonaktif. Profil pada viewport 390 px tidak meluber (375 px lebar konten). Sesi QA ditutup dan ukuran browser dipulihkan.
- Kasus pembaruan actor dari server, storage ditolak, normalisasi setelah simpan, serta PATCH tertunda diuji dengan helper dan komponen Profile asli. Tidak mengklaim semuanya disimulasikan melalui dua browser nyata.

Pemeriksaan browser memakai database lokal terisolasi. Nama transaksi/barang QA di database itu adalah fixture pengujian, bukan data yang diterbitkan ke katalog publik. Tidak ada transaksi bisnis baru dibuat untuk pemeriksaan browser rilis ini.

## Batas

Draf profil disimpan sementara di tab yang sama selama maksimal delapan jam; bukan penyimpanan lintas perangkat. Jika browser menolak seluruh operasi storage, aplikasi memberi tahu keterbatasan pemulihan/pembersihan. Sinkronisasi form tidak menyediakan penguncian edit serentak di server. Pengujian mobile menggunakan viewport browser, bukan perangkat sentuh fisik atau audit pembaca layar penuh.

Temuan reset URL direproduksi saat pengembangan dengan live update; tidak dinyatakan sebagai kejadian produksi yang telah direproduksi. Tidak menambahkan reset email, pendaftaran publik, pelacakan kurir langsung, atau kebijakan transaksi baru tanpa dukungan backend.

## Catatan implementasi

[Workspace dan navigasi](implementation-v15-workspace.md) · [Pengadaan dan administrasi](implementation-v15-operations.md) · [Akun dan draf profil](implementation-v15-account.md) · [Integrasi](implementation-v15-integration.md).

## Publikasi

- Build produksi Vercel berhasil; 24 halaman statis selesai. Deployment `dpl_4knw2TGb5tZGBaHJvCUQ4rURg46K`, [URL deployment](https://unit-toko-b55ptl5kv-natannielzs-projects.vercel.app).
- Setelah promosi, inspeksi domain utama mengonfirmasi deployment tersebut berstatus READY.
- Katalog publik terverifikasi: **200 produk, 200 path gambar unik, 200 deskripsi unik**; lima sampel gambar berhasil dimuat. [Hasil katalog](public-catalog-v15-results.json).
- Pemeriksaan akses produksi **25/25 lulus**, termasuk 11 akun, pemisahan portal/peran, dan pencabutan seluruh sesi uji; dicatat pada [hasil smoke](vercel-v15-results.json). Helper hanya memakai halaman dan login/logout; tidak mengambil API bisnis privat atau mengirim perintah bisnis.
- Export GitHub hanya mencakup kode, aset, dan dokumen yang diizinkan. Kredensial, database, konfigurasi privat, arsip studi kasus, dan riwayat Git lokal lama dikecualikan.
