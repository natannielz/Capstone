# Implementasi katalog dan landing v5

17 September 2026. Perubahan terbatas pada `website/components/catalog.tsx`, `landing.tsx`, `moment-showcase.tsx`, `website/lib/domain/catalog.ts`, dan stylesheet baru `website/app/catalog-v5.css`. Backend, seed, aturan domain, dan RBAC tetap memakai implementasi yang ada.

- **Keluarga produk:** katalog menampilkan 12 keluarga dari 36 SKU seed. Pemilih kemasan mengganti produk yang digunakan untuk harga, persediaan, dan penambahan keranjang. SKU satuan/paket tetap menjadi baris keranjang terpisah; stok tidak digabungkan.
- **Kemasan:** label menggunakan satuan dan nama paket yang sudah tersimpan. Informasi jumlah botol per dus air dan gelas per pak belum tersedia; UI menyatakannya tanpa membuat angka baru. SKU dan pemasok berada di detail produk.
- **Jumlah:** keranjang dan detail mempunyai isian langsung serta tombol −/+. Hanya bilangan bulat 1–10.000 diterima. Nilai kosong/desimal/eksponen/di luar batas menampilkan pesan, tidak mengganti jumlah valid sebelumnya, dan menghalangi checkout sampai diperbaiki. Subtotal dan total mengikuti jumlah valid.
- **Review pengajuan:** dialog dua tahap menampilkan divisi, tanggal kebutuhan, titik pengiriman, catatan, tanggal pencatatan, setiap barang/kemasan/jumlah/harga/subtotal, stok, dan total. Kekurangan stok dijelaskan sebagai menunggu pemenuhan, bukan larangan pemesanan. Pengguna dapat kembali mengubah pengiriman atau barang sebelum mengajukan.
- **Penyimpanan:** tetap memakai `order.create` ke `/api/commands`. Kunci idempotensi dipertahankan saat retry tanpa perubahan isian, tombol submit dikunci selama permintaan berlangsung, dan error tetap terlihat. Keranjang baru dibersihkan setelah respons berhasil. Keranjang perangkat dipisahkan per akun dan menolak nilai tersimpan yang bukan integer valid/produk aktif.
- **Tautan landing:** produk dan koleksi mengarah ke `/login?next=<encoded /workspace?...>`. Konvensi tujuan: `view=catalog`, `collection=pantry|rapat|merchandise`, serta `product=<ID atau SKU persis yang aktif>`. Landing menggunakan ID produk. Login dan validasi hak akses tujuan diintegrasikan oleh agen utama.
- **Konteks katalog:** `q` (maksimal 120 karakter), `category` (allowlist), `collection` (allowlist), dan `product` dibaca dari URL; perubahan memakai `history.replaceState`, mempertahankan parameter lain. `popstate` memulihkan filter/detail. Filter koleksi terlihat dan dapat dihapus. Pemilih kemasan pada kartu bertahan selama katalog terpasang; pilihan detail tersimpan pada parameter produk.
- **Foto landing:** tab kebutuhan memakai tiga foto produk terkait, dengan satu foto utama dan dua pendamping. Foto kategori tidak lagi mengulang masthead atau menampilkan makanan yang berbeda dari pilihan produk. Keyboard tab ArrowLeft/ArrowRight/Home/End tetap tersedia. Masthead, warna petroleum/oranye, dan font yang sudah dipilih dipertahankan.

## Integrasi

Impor `catalog-v5.css` setelah `polish.css`. Komponen tetap menerima `WorkspaceContext`; `ask` tidak diubah. Checkout menggunakan `refresh` dan `go("orders", id)` setelah berhasil. Dialog menggunakan komponen UI bersama, termasuk pemulihan fokus yang sedang ditangani agen utama.

## Pemeriksaan oleh agen katalog

- TypeScript `--noEmit --incremental false`: PASS.
- ESLint pada empat berkas TS/TSX yang diubah: PASS, tanpa pengecualian lint baru.
- Tujuh kelompok pemeriksaan helper dengan `seedState`: PASS — 12 keluarga/36 SKU, harga dan stok SKU terpisah, kemasan dari metadata, lookup produk aktif, allowlist koleksi, roundtrip tautan login, dan batas integer. Stok tersedia air pada seed adalah 26/16/22 untuk satuan/paket 3/paket 6; reservasi seed sudah diperhitungkan. Helper tidak memutasi state.
- Browser, build produksi, dan deployment bukan bagian pemeriksaan agen ini; ditangani agen utama. Stylesheet menyediakan penyesuaian 1 kolom pada layar di bawah 360px, dialog yang dapat digulir, dan input jumlah dengan target tombol minimal 36×42px.

Dasar perubahan: [audit visual v5](research-visual-v5.md) dan [prioritas UI/UX gabungan](research-ui-ux-v5.md).
