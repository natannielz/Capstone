# Implementasi v14 — katalog sampai checkout

23 September 2026. Mengikuti [rencana v14](plan-ui-ux-v14.md) dan [riset commerce](research-v14-commerce.md).

## Perubahan

- Filter pencarian, koleksi, kategori, dan stok dapat dilepas satu per satu; filter lainnya dan urutan tetap berada di URL. Hapus semua tetap tersedia. Perubahan filter kembali ke halaman pertama seperti sebelumnya.
- Filter stok memeriksa seluruh kemasan yang cocok. Bila kemasan pertama habis tetapi kemasan lain tersedia, keluarga produk tetap muncul. Kartu, harga untuk pengurutan, dan tautan detail memakai kemasan tersedia yang sama. Pencarian SKU persis tidak dialihkan ke kemasan berbeda.
- Quick-add menjelaskan “Stok sudah di keranjang” ketika kuantitas yang dipilih telah mencapai stok. Tombol tetap nonaktif, menggunakan centang, dan keterangannya terhubung melalui `aria-describedby`.
- Pilihan kemasan detail menampilkan nama kemasan, harga per satuan, stok, dan centang pilihan. Pilihan stok habis dapat dilihat; input jumlah dan tindakan beli dinonaktifkan. Copy foto menggunakan istilah kemasan.
- Kontrol jumlah keranjang memakai batas stok saat ini. Plus berhenti di batas; saat jumlah lama lebih besar dari stok, minus dapat menyesuaikannya. Jumlah lama tidak dihapus atau dikurangi secara otomatis. Stok nol tidak dapat menghasilkan kuantitas nol lewat kontrol.
- Keranjang dan checkout menggunakan navigasi tahap yang sama. Mode Beli sekarang dimulai dari Pilihan produk. Pesanan saya adalah tautan riwayat, bukan indikator palsu bahwa pesanan baru sudah selesai. Checkout yang belum memiliki intent tidak diberi tautan yang dapat melewati pemilihan barang.
- Kartu katalog, pilihan kemasan, kontrol jumlah, baris keranjang, form, dan ringkasan nota memakai jarak dan hierarki harga konsisten. Tombol tetap stabil. Mobile mempunyai sasaran tambah 44 px dan ruang scroll untuk bilah tindakan bawah.

## File yang berubah

- `website/components/shop/storefront.tsx`
- `website/components/shop/shop-data.ts`
- `website/components/shop/shopping-pages.tsx`
- `website/components/shop/checkout-page.tsx` — hanya import dan penyisipan navigasi tahap, tanpa perubahan submit.
- `website/components/shop/shopping-progress.tsx` — baru.
- `website/app/commerce-v14.css` — baru; diimpor setelah stylesheet kontrol oleh integrator.
- `website/tests/shop-family-selection.test.ts` — baru.

Tidak mengubah API, reducer cart, penyimpanan, sesi, ID pengajuan, pemeriksaan harga/stok server, maupun kontrak checkout. Footer, carousel, login, dan dashboard dikerjakan pada bagian lain.

## Verifikasi pada tahap implementasi

- ESLint terarah untuk enam file TypeScript: lulus, tanpa warning.
- TypeScript seluruh aplikasi: lulus.
- Empat tes regresi pemilihan kemasan: lulus. Mencakup kemasan alternatif tersedia, pencarian SKU persis, pencarian nama/potongan SKU, serta semua kemasan habis dan produk tidak aktif.
- Perbandingan terhadap source publik sebelumnya memastikan checkout hanya menerima tambahan presentasi tahap.

Browser, pembacaan geometri viewport, lint integrasi akhir, akses per akun, build, dan publikasi masih menjadi pekerjaan integrator. Catatan ini tidak mengklaim pengujian browser atau transaksi produksi oleh agen commerce. Keadaan kosong/error yang sudah ada dipertahankan karena tindakan pemulihannya sudah jelas; tidak ditambah ilustrasi atau animasi yang menghalangi pemulihan.
