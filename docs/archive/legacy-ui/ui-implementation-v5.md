# Penerapan riset UI/UX v5

17 September 2026. Dasar pekerjaan: [audit v5](research-ui-ux-v5.md). Tiga agen mengerjakan katalog/landing, pengalaman pesanan, serta aksesibilitas/profil; integrasi dan pemeriksaan browser dilakukan agen utama.

## Perubahan

- Antrean dashboard meneruskan filter yang sesuai ke pesanan, pengiriman, dan invoice. Daftar pesanan memiliki pencarian, status pekerjaan, pengurutan, dan pagination. Pencarian/filter serta posisi daftar dipertahankan ketika kembali dari detail.
- Pesanan pada ponsel menjadi daftar dengan status, tanggal, nilai, dan tombol tindakan yang terlihat. Dashboard memakai komponen responsif yang sama.
- Detail pesanan menghubungkan barang, pengiriman, tindakan PIC, dan invoice. Status pemenuhan terpisah dari pembayaran. Untuk invoice yang menggabungkan pesanan, kontribusi pesanan dibedakan dari saldo seluruh invoice.
- Katalog menampilkan 12 keluarga produk dengan 36 SKU terpisah. Pilihan kemasan memperbarui harga/stok SKU yang sesuai. Isi dus/pak yang belum ada dalam data tidak dikarang.
- Jumlah dapat diketik langsung; subtotal dan kekurangan stok mengikuti jumlah tersebut. Checkout terdiri atas informasi pengiriman dan pemeriksaan lengkap sebelum pengajuan. Setelah berhasil, keranjang dibersihkan dan detail pesanan baru dibuka.
- Landing menggunakan komposisi foto barang yang sesuai untuk tiap kebutuhan. Pilihan koleksi/produk diteruskan melalui login; tujuan dibatasi pada halaman internal yang tersedia bagi peran tersebut.
- Menu ponsel menjadi dialog navigasi dengan pengelolaan fokus, Escape, dan tombol tutup. Dialog mengembalikan fokus ke pemicu atau tujuan lanjutan. Indikator fokus pencarian diperbaiki.
- Profil menampilkan kesalahan di dekat isian, status perubahan, serta tindakan simpan/batalkan. Navigasi saat ada perubahan meminta pilihan tetap mengedit atau buang perubahan, termasuk Back. Error layanan login dibedakan dari kredensial salah.
- Penagihan dapat membagi satu pembayaran ke beberapa invoice dalam satu formulir dengan pratinjau saldo. Validasi dan transaksi server tetap dipakai.

Warna petroleum, aksen oranye, DM Sans, dan Archivo Black dipertahankan. Backend, data produksi, dan kebijakan RBAC tidak diubah. CSS per area dipisahkan agar pekerjaan paralel dan kepemilikan aturan komponen lebih jelas.

## Validasi

| Pemeriksaan | Hasil |
| --- | --- |
| Tes domain, repository, RBAC, navigasi | 40 lulus, 0 gagal |
| TypeScript | Lulus |
| Build produksi Next.js | Lulus |
| Lint seluruh proyek | 0 error; 7 warning berupa reload autentikasi dan variabel lama yang tidak dipakai |
| Riset/in-memory agen | Pengelompokan SKU, kuantitas, filter antrean, dokumen lintas peran/divisi, dan alokasi multi-invoice lulus |
| Browser: landing → login → katalog | Koleksi Pantry diteruskan |
| Browser: kartu antrean Kepala | 2 menunggu → tepat 2 hasil dari 5 pesanan lokal awal |
| Browser: pencarian → detail → kembali | `0004` dan filter tetap tersedia |
| Browser: dialog peninjauan | Escape mengembalikan fokus ke tombol Tinjau & setujui |
| Browser: menu ponsel | Fokus masuk, Escape menutup, fokus kembali ke tombol menu; memilih halaman memfokuskan judul |
| Browser: profil → Back → Tetap mengedit | Draft nama tetap tersedia; fokus kembali ke isian |
| Browser: error profil | Nama kosong/spasi menghasilkan error inline yang masih terlihat sesudah lebih dari 10 detik |
| Browser: checkout | 50 dus × Rp54.000 = Rp2.700.000; kekurangan 26 dus ditampilkan; pengajuan lokal berhasil |
| Responsif | Pesanan 390px tanpa overflow halaman; checkout 320px: dialog 295px dan scrollWidth 295px; detail 320px tanpa overflow halaman |

Pengajuan uji hanya dibuat pada SQLite lokal, diberi catatan “Uji UI v5 lokal — data simulasi.” Tidak ada pesanan uji baru dibuat pada database produksi. Akun dan kata sandi tidak diubah dalam pemeriksaan browser.

Satu percobaan pengisian telepon melalui alat browser tidak menghasilkan nilai sebelum navigasi, sehingga tidak dipakai sebagai bukti kegagalan atau keberhasilan aplikasi. Pengujian draft memakai nama yang dapat diverifikasi. Pengujian ini bukan sertifikasi WCAG atau studi usability dengan pengguna. Zoom browser 400% dan pembaca layar belum diuji langsung.

## Rilis

Sudah terbit di [Unit Toko BNI](https://unit-toko-bni.vercel.app), deployment `dpl_EjKprMjbYCnNeR52bFJ9Fw1EogZ2`, status READY dan alias produksi terpasang. Build Vercel berhasil.

Pemeriksaan publik lulus: landing/login/aset merespons 200, akses anonim ditolak 401, login menggunakan cookie Secure/HttpOnly, tindakan admin yang tidak diizinkan ditolak 403, data dua PIC terpisah berdasarkan divisi, dan data finansial tersembunyi bagi staf. Bukti tersimpan pada `docs/vercel-v5-results.json`. Pemeriksaan browser produksi mengonfirmasi profil dan filter pesanan baru; pada viewport 1280px lebar dokumen 1265px tanpa overflow horizontal.

Lampiran privat, database lokal, dan environment tetap dikecualikan dari paket Vercel.

## Berkas utama

- `website/components/order-experience.tsx`, `payment-allocation.tsx`, `use-workspace-navigation.ts`, `mobile-navigation.tsx`
- `website/components/catalog.tsx`, `landing.tsx`, `moment-showcase.tsx`, `login.tsx`, `profile.tsx`, `workspace.tsx`, `dashboard.tsx`
- `website/lib/domain/catalog.ts`, `navigation.ts`, `order-views.ts`
- `website/app/catalog-v5.css`, `orders-v5.css`, `accessibility-v5.css`

Catatan katalog terperinci: [implementasi katalog](implementasi-katalog-v5.md).
