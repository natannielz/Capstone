# Implementasi katalog v6

Status 17 September 2026: kode selesai; 15 regresi katalog, TypeScript seluruh website, dan lint berkas yang berubah lulus. Pemeriksaan browser setelah perubahan dilakukan oleh agen utama; dokumen ini tidak menyatakan hasil browser v6 yang belum diterima.

## Perubahan

- **B01 — pencarian SKU:** kecocokan SKU tepat mengutamakan kemasan SKU tersebut, termasuk harga dan stoknya, meskipun pengguna sebelumnya memilih kemasan lain. Pencarian nama umum tetap menghormati pilihan kemasan yang relevan. Mengganti kemasan secara sengaja saat pencarian SKU aktif menghapus pencarian SKU agar hasil tidak bertentangan.
- **B05 — draft checkout:** divisi, tanggal, alamat, catatan, dan identitas pengajuan tetap tersedia setelah dialog ditutup untuk mengubah barang, berpindah halaman, atau memuat ulang tab. Draft tersimpan per akun dalam `sessionStorage`, sehingga tujuan pengiriman dari dua tab tidak saling mengganti. Password tidak disimpan. Identitas tindakan juga menyertakan revisi keranjang agar perubahan barang tidak memakai kembali identitas permintaan berbeda.
- **B08 — keranjang antar-tab:** mutasi memakai Web Locks per akun dan membaca nilai terbaru di dalam transaksi. Penambahan barang bersifat tambahan terhadap jumlah terbaru. Edit jumlah absolut atau penghapusan dengan revisi lama ditolak secara eksplisit, bukan menimpa perubahan tab lain. Revisi per barang tetap disimpan setelah barang dihapus untuk mencegah isian lama menghidupkannya kembali.
- **Pengajuan dan pengosongan:** checkout memeriksa revisi yang sudah ditinjau, memegang kunci akun selama pengajuan, lalu mengosongkan keranjang setelah respons berhasil. Pengajuan kedua dari revisi yang sama tidak dikirim. Penambahan barang yang menunggu kunci berjalan setelah pengosongan, sehingga tetap menjadi keranjang baru. Peristiwa penyimpanan dan `BroadcastChannel` menyampaikan perubahan ke tab lain; tanda pengosongan mencegah draft sebelum checkout dipulihkan dari tab yang sempat tertunda.
- **U01 — lebar 951–1000 px:** akses tetap ke keranjang ditampilkan mengikuti batas perubahan tata letak menjadi satu kolom. Ruang bagian bawah disediakan agar tombol tidak menutupi konten.

Jika penyimpanan gagal atau kemampuan sinkronisasi tidak tersedia, keranjang tetap dapat digunakan dalam memori tab dengan pemberitahuan bahwa keranjang tidak tersimpan atau tersinkron. Mode ini tidak memberikan jaminan sinkronisasi antar-tab. Keranjang lama dimigrasikan pada penulisan v6 yang berhasil.

## Bukti dan validasi

Sebelum perubahan, agen utama mengonfirmasi di produksi: tab A memiliki air 1; tab B dibuka; tab A mengubah air menjadi 5; tab B menambah teh 1; setelah memuat ulang tab A, keranjang kembali menjadi air 1 + teh 1 (Rp66.000). Empat dus hilang. Model deterministik untuk perilaku lama mereproduksi hasil itu; store baru menghasilkan air 5 + teh 1.

`website/tests/catalog-v6.test.ts` memuat 15 kasus: reproduksi perilaku lama, SKU tepat, dua tab mengubah barang berbeda, penambahan serentak, konflik edit/hapus, input berurutan dari tab yang sama, isolasi akun, pengosongan dan pencegahan pemulihan isian lama, checkout dua tab, penambahan setelah checkout, kegagalan pengajuan, penyimpanan diblokir/penuh, migrasi, validasi jumlah, serta pemulihan dan invalidasi draft. Pemeriksaan ini menguji helper yang dipakai komponen; perilaku dialog dan ukuran layar tetap memerlukan QA browser.

Validasi terfokus memakai `node:test` dengan transpiler TypeScript karena esbuild pada sesi ini gagal membaca direktori leluhur di luar sandbox. Runner proyek menemukan file test baru secara otomatis. Tidak ada perubahan pada runner oleh agen katalog.

## Integrasi

- Modul baru: `website/lib/client/cart-storage.ts`.
- Logout: impor `clearCartForAccount` dari `@/lib/client/cart-storage`, lalu `await clearCartForAccount(actor.id)` **setelah** logout server berhasil. Logout gagal tidak boleh memanggilnya.
- CSS menggunakan `website/app/catalog-v5.css` yang sudah diimpor. Tidak perlu impor CSS tambahan.
- Berkas lain: `website/components/catalog.tsx`, `website/lib/domain/catalog.ts`, dan `website/tests/catalog-v6.test.ts`.
- Tidak mengubah seed, server harga/stok, aturan domain, RBAC, atau endpoint checkout.

Rujukan: [audit v6](audit-v6-visual-catalog.md), [implementasi v5](ui-implementation-v5.md).
