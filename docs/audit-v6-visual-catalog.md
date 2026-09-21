# Audit v6 — visual, katalog, dan transisi pemesanan

17 September 2026. Audit baca-saja atas kode v5 dan catatan [penerapan v5](ui-implementation-v5.md). Tidak ada perubahan aplikasi, deployment, kredensial, atau data oleh agen audit ini. Agen utama memeriksa browser produksi; konfirmasinya dicatat terpisah dari kesimpulan berbasis kode.

Pengelompokan 12 keluarga/36 SKU, input jumlah langsung, subtotal, review pengajuan, deep-link dari landing, foto kategori yang relevan, fokus dialog, dan perbaikan overflow 320px sudah selesai pada v5. Hal tersebut tidak diajukan ulang sebagai pekerjaan baru.

## Enam temuan, menurut prioritas

### 1. Pencarian SKU dapat menampilkan dan menambahkan kemasan yang berbeda

**Prioritas P1 · Confidence tinggi · Dikonfirmasi browser produksi.**

`chosenProduct()` mengutamakan pilihan kemasan sebelumnya sebelum produk yang cocok dengan pencarian: [catalog.tsx:329](../website/components/catalog.tsx#L329). Mengganti teks pencarian hanya memperbarui query, tanpa menyesuaikan pilihan: [catalog.tsx:404](../website/components/catalog.tsx#L404).

**Reproduksi:** pilih air Paket 6 dus, lalu cari `OMI-001-P3`. Satu hasil muncul, tetapi masih Paket 6, Rp324.000, 22 paket. URL sudah berisi pencarian P3. Tombol Tambah memakai varian yang masih terpilih, sehingga PIC dapat memasukkan kemasan yang berbeda dari SKU yang dicari.

**Perbaikan/acceptance:** pencarian SKU persis mendahului pilihan lama; hasil P3 menunjukkan Paket 3 dus, harga/stok P3, dan Tambah mengirim ID `air--3`. Pencarian nama umum tetap boleh mempertahankan pilihan yang cocok. Uji berpindah P6 → P3 → nama umum dan membuka detail.

### 2. Mengubah barang dari review menghapus seluruh isian pengiriman

**Prioritas P2 · Confidence tinggi · Dikonfirmasi browser produksi.**

Tombol “Ubah barang di keranjang” memanggil `close`: [catalog.tsx:1123](../website/components/catalog.tsx#L1123). Dialog kemudian dilepas dari tree: [catalog.tsx:773](../website/components/catalog.tsx#L773), sementara divisi/tanggal/alamat/catatan hanya disimpan dalam state dialog: [catalog.tsx:817](../website/components/catalog.tsx#L817).

**Reproduksi:** sebagai Kepala Toko, isi divisi, alamat khusus, tanggal, dan catatan → Periksa pesanan → Ubah barang di keranjang → Lanjutkan pesanan. Browser produksi mengonfirmasi divisi kembali ke “Pilih divisi” serta alamat/catatan kosong. Pengguna harus mengulang isian hanya untuk mengoreksi barang.

**Perbaikan/acceptance:** simpan draft checkout pada pemilik keranjang per akun; pergantian tahap atau koreksi barang tidak menghapus isian. Hapus draft setelah pengajuan berhasil atau tindakan buang draft yang jelas. Uji PIC dan Kepala; nilai tetap sama setelah dua kali kembali mengubah barang.

### 3. Tab lain dapat menimpa jumlah keranjang yang lebih baru

**Prioritas P2 · Confidence tinggi dari kode · Belum diuji di browser.**

Keranjang diambil sekali ketika komponen dipasang: [catalog.tsx:254](../website/components/catalog.tsx#L254). Setiap perubahan menulis seluruh snapshot lokal: [catalog.tsx:284](../website/components/catalog.tsx#L284). Tidak ada langganan perubahan `storage` pada kode katalog.

**Reproduksi terarah:** tab A dan B akun sama sama-sama membuka keranjang berisi air 1. A mengubah air menjadi 5. B menambah teh. B menulis snapshot lama berisi air 1 + teh, sehingga saat A dimuat ulang jumlah air kembali 1. Setelah checkout berhasil di A, tab B juga masih menampilkan keranjang sebelum checkout.

**Perbaikan/acceptance:** sinkronkan perubahan keranjang per akun antartab dan tentukan penanganan edit bersamaan; jangan diam-diam menimpa versi lebih baru. Skenario A:air5 + B:teh1 menghasilkan keranjang gabungan yang konsisten atau pemberitahuan konflik sebelum overwrite. Checkout di A membersihkan/memperbarui keranjang di B. Isolasi akun tetap berlaku.

### 4. Pada lebar 951–1000px, akses cepat ke keranjang hilang

**Prioritas P2 · Confidence tinggi · Dikonfirmasi browser produksi pada 980×900.**

Keranjang turun ke bawah daftar pada breakpoint 1000px: [catalog-v5.css:524](../website/app/catalog-v5.css#L524). Ringkasan fixed baru ditampilkan pada breakpoint 950px: [polish.css:392](../website/app/polish.css#L392) dan [polish.css:413](../website/app/polish.css#L413); default-nya `display:none`.

**Reproduksi:** viewport 980×900, tampilkan seluruh katalog lalu tambah barang. Agen utama mengukur posisi Y keranjang sekitar 4221px dan tombol lanjut sekitar 4624px, sementara “Lihat keranjang” memiliki `display:none` dan rect 0. Pengguna harus melewati seluruh daftar produk untuk melanjutkan.

**Perbaikan/acceptance:** gunakan breakpoint yang sama untuk keranjang di bawah daftar dan akses cepatnya, atau tampilkan tombol keranjang pada toolbar semua ukuran. Uji 950, 951, 980, 1000, dan 1001px: setelah menambah barang selalu ada akses ke keranjang yang terlihat tanpa menelusuri seluruh katalog; kontrol tidak menutupi akhir formulir.

### 5. Pilihan landing meminta login ulang meskipun sesi masih aktif

**Prioritas P2 · Confidence tinggi dari kode · Belum diuji khusus di browser.**

Semua pilihan produk/koleksi dibuat sebagai URL login: [catalog.ts:111](../website/lib/domain/catalog.ts#L111). Halaman login langsung merender formulir tanpa pemeriksaan sesi: [app/login/page.tsx:2](../website/app/login/page.tsx#L2). Pengalihan baru terjadi setelah formulir login dikirim: [login.tsx:19](../website/components/login.tsx#L19).

**Reproduksi terarah:** masuk sebagai PIC → kembali ke beranda publik tanpa logout → klik produk kopi. Meskipun sesi masih valid, alur mengarah ke formulir email/kata sandi, bukan langsung ke detail kopi. Ini memutus kelanjutan pilihan bagi pengguna yang sudah masuk.

**Perbaikan/acceptance:** sesi yang valid meneruskan tujuan internal yang sudah diperiksa hak aksesnya; sesi tidak valid tetap melalui login. Uji PIC, Kepala, dan peran tanpa akses katalog. Tidak boleh membuka halaman yang dilarang hanya karena membawa `next`.

### 6. Tombol retry pemuatan tidak memperlihatkan status sedang mencoba

**Prioritas P3 · Confidence tinggi dari kode · Belum diuji dengan jaringan tertunda.**

`refresh()` tidak mengubah `loading` menjadi true sebelum permintaan; hanya mengubahnya menjadi false pada akhir. Tampilan gagal menampilkan tombol “Coba kembali” tanpa disabled/busy: [workspace.tsx:94](../website/components/workspace.tsx#L94) dan [workspace.tsx:100](../website/components/workspace.tsx#L100).

**Reproduksi terarah:** gagalkan `/api/state` pada pembukaan awal, pulihkan jaringan tetapi tunda respons, lalu klik Coba kembali. Pesan gagal lama dan tombol aktif tetap terlihat selama permintaan baru berlangsung; klik berulang membuat permintaan tambahan.

**Perbaikan/acceptance:** retry mempunyai status memuat, label tindakan yang jelas, dan pencegah permintaan duplikat. Pesan error yang tersisa tidak memberi kesan percobaan baru sudah gagal. Uji kegagalan → retry tertunda → berhasil serta gagal lagi; fokus dan pengumuman status tetap masuk akal.

## Penyempurnaan visual yang mengikuti temuan

Pertahankan identitas petroleum/oranye, font, masthead, dan foto produk yang sekarang sudah relevan. Fokus v6 adalah pengalaman menyusun pesanan divisi: kemasan/harga yang terlihat harus konsisten dengan SKU yang dicari, akses keranjang harus berada di posisi yang dapat diprediksi pada semua ukuran, dan formulir harus menunjukkan tahap serta status pemrosesan tanpa menghapus pekerjaan pengguna.

Tidak ada bukti dalam audit ini yang mengharuskan ganti tema, menambah slogan, foto dekoratif, animasi, atau kartu statistik. Sebelum menambah mode katalog baru, perbaiki enam transisi di atas dan uji satu tugas nyata: memilih tiga barang dengan kemasan berbeda, mengoreksi jumlah/pengiriman, lalu mengajukan pesanan. Pemeriksaan pembaca layar dan zoom 400% tetap belum dilakukan; audit kode ini tidak mengubah batas bukti QA v5.
