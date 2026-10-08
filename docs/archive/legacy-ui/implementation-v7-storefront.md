# Implementasi etalase pelanggan v7

21 September 2026. Mengikuti [rencana marketplace pelanggan](plan-customer-marketplace-v7.md). Catatan ini mencakup frontend etalase dan keranjang; migrasi pembeli, autentikasi, API, serta halaman akun dikerjakan terpisah.

## Halaman dan interaksi

- `/`: etalase dengan pencarian, tiga koleksi, foto editorial yang sudah ada, pilihan produk, dan penjelasan proses pemesanan. Identitas petroleum/oranye, DM Sans/Archivo tetap dipakai.
- `/shop`: produk dikelompokkan sebagai keluarga, dengan kemasan/SKU yang tetap terpisah. Pencarian nama atau SKU, kategori, koleksi, stok, dan urutan harga/nama menggunakan URL. SKU yang dicari persis menentukan harga, stok, dan tautan kemasan pada kartu. Filter tetap berlaku saat kembali dari rincian produk.
- `/shop/[product]`: foto, SKU, pilihan kemasan, stok, jumlah bilangan bulat, subtotal, tambah keranjang, dan Beli sekarang. Kemasan terpilih tercermin di URL dan bertahan setelah muat ulang. Foto tidak diperlakukan sebagai bukti isi paket.
- `/cart`: pilih sebagian barang, ubah jumlah langsung, pindah kemasan, hapus barang, serta subtotal pilihan. Jumlah yang sedang diketik membawa versi awalnya; perubahan di tab lain menimbulkan konflik yang jelas alih-alih menimpa jumlah baru.
- `/checkout`: khusus pelanggan, memeriksa penerima, kontak, alamat, catatan, kemasan, jumlah, dan total. Nama/kontak/alamat dapat diisi dari profil. Isian bertahan setelah kembali mengubah barang. Tidak ada pilihan divisi, tanggal buatan, ongkir rekaan, atau pembayaran sebelum invoice.

Header pencarian dan koleksi tersedia di seluruh halaman. Tata letak mobile memakai dua kolom produk, navigasi bawah, serta bilah tindakan tetap pada rincian, keranjang, dan checkout. Pemuatan, katalog gagal, sesi gagal, hasil kosong, stok tidak cukup, dan perubahan harga memiliki keadaan tersendiri. Peran petugas/PIC diarahkan ke portal untuk transaksi mereka. Foto dan transaksi tetap dinyatakan sebagai simulasi capstone; tidak ada ulasan, diskon, atau klaim penjualan rekaan.

## Kontrak integrasi

- `components/shop/shop-shell.tsx`: ekspor `ShopShell({ children, actor?, cartCount?, active?, actionBar? })`. `active` menerima `shop`, `cart`, `orders`, atau `account`. Tanpa prop actor, shell memuat `/api/session`. Shell menyediakan satu `main#shop-main`, skip link, dan Toaster; halaman anak jangan membuat main kedua. Komponen anak etalase menggunakan `useShop()`.
- Root mengimpor `app/storefront-v7.css` setelah gaya dasar. Seluruh gaya etalase dibatasi oleh `.shop-v7` atau nama `.shop-*`.
- GET `/api/catalog`: `{ products, revision }`; produk aktif berisi `id`, `familyId`, `sku`, `name`, `unit`, `price`, `active`, `category`, `image`, `description`, `packaging`, `available`. Tidak membaca state internal untuk katalog publik.
- GET `/api/session`: `{ actor: Actor | null }`; kegagalan layanan berbeda dari sesi tamu.
- POST `/api/commands`: `{ id, type: "order.create", data: { lines: [{ productId, qty, unitPrice }], recipientName, recipientPhone, address, note } }`. Sesi diperiksa kembali sebelum submit. Kepemilikan, tanggal, harga, dan stok tetap divalidasi server.
- `lib/client/customer-cart.ts`: root memanggil `await clearCustomerCart(actor.id)` **sesudah logout server berhasil**. Ini membersihkan keranjang pemilik tersebut, mengubah epoch, dan menyiarkan perubahan ke tab lain. Logout gagal tidak menghapus draft.
- Route Next memakai async `params`; hook URL berada di dalam Suspense. Navigasi Link serta perubahan native history mengikuti sumber URL yang sama.

## Penyimpanan dan pemulihan

Keranjang tamu dan tiap akun tersimpan sebagai SKU/jumlah dalam satu envelope. Web Locks membaca kondisi terbaru dan menulis mutasi secara berurutan. Impor tamu menggabungkan jumlah serta mengosongkan keranjang tamu dalam satu penulisan, dengan penanda impor sekali. Kelebihan batas menolak seluruh penggabungan tanpa menghilangkan salah satu keranjang. Alamat, nama penerima, dan catatan tidak masuk envelope atau impor tamu; draft checkout menggunakan sessionStorage yang dipisah per akun, dengan fallback memori.

Checkout dari keranjang hanya mengurangi jumlah terpilih; Beli sekarang mempertahankan keranjang. Checkout menyimpan identitas perintah dan hash muatan sebelum permintaan. Kegagalan jaringan yang belum pasti mempertahankan identitas tersebut dan mengunci perubahan; retry mengirim perintah yang sama. Penolakan server yang pasti membuka kembali penyuntingan. Hasil yang sudah berhasil dan identitas intent mencegah pengurangan atau submit kedua, termasuk buy-now dari tab duplikat. Mutasi yang menunggu checkout berjalan setelah hasil disimpan.

Jika penyimpanan atau Web Locks diblokir, keranjang memori tetap dapat dipakai dengan pemberitahuan bahwa penyimpanan dan sinkronisasi antar-tab tidak tersedia. Draft checkout tetap milik tab/akun; bila tab asal ditutup saat respons belum pasti, tab lain tidak menyalin alamat privat dan menampilkan konflik pengajuan. Jangan menganggap kegagalan jaringan sebagai bukti pesanan belum tercatat: periksa Pesanan saya sebelum memulai pesanan baru.

## Validasi pada berkas akhir

- TypeScript `--noEmit --incremental false`: PASS.
- ESLint untuk seluruh halaman/komponen etalase, helper, dan tes baru: PASS.
- `tests/customer-cart-v7.test.ts`: **19 PASS**, meliputi penambahan lintas tab, edit stale, impor sekali dan batas jumlah, pergantian kemasan, checkout terpilih, buy-now, deduplikasi intent, retry respons hilang, harga berubah, mutasi yang antre, isolasi akun/logout, fallback, larangan replay callback gagal, validasi jumlah, draft/alamat, fingerprint, exact SKU, serta penolakan tautan kembali eksternal.
- Browser QA desktop/mobile, alur akun, checkout, dan dua tab dicatat pada [testing-customer-v7.md](testing-customer-v7.md). Hasil deployment serta pemeriksaan produksi dicatat pada [rilis-v7.md](rilis-v7.md).

Dokumen ini menjelaskan komponen etalase. Perubahan domain, backend, autentikasi, migrasi, dan deployment dalam rilis gabungan dijelaskan pada catatan rilis dan hasil pengujian terkait.
