# Rilis v12 — tata letak dan animasi belanja

23 September 2026. Website: [Unit Toko](https://unit-toko-bni.vercel.app).

Riset dan implementasi dibagi kepada tiga agen: referensi/pola interaksi, carousel, serta beranda/login. Integrasi etalase, detail produk, pemeriksaan browser dan publikasi dilakukan pada pekerjaan utama. Sumber primer dan batas klaim tersedia di [laporan riset](research-experience-v12.md); keputusan visual ada di [rencana desain](design-plan-v12.md).

## Perubahan

- Menghapus titik oranye pada label `UNIT TOKO / ETALASE PELANGGAN`, termasuk elemen HTML dan CSS-nya.
- Mengganti tiga kartu produk yang bertumpuk dan miring dengan carousel tiga koleksi nyata. Foto dan teks mendapat ruang masing-masing, kontrol tetap berada dalam alur layout.
- Carousel berganti otomatis setiap enam detik, menyediakan Jeda/Mulai dan panah manual, berhenti ketika pengguna berinteraksi, dan menghormati preferensi kurangi gerakan. Slide tidak aktif tidak menerima fokus. [Catatan implementasi](carousel-v12.md).
- Memperjelas etalase dengan permukaan putih/perak dan warna petroleum, memperbaiki hierarki judul, filter, kartu, serta respons penambahan keranjang.
- Detail produk memakai kolom foto dan pembelian yang lebih bersih. Deskripsi didekatkan ke nama, SKU tetap di rincian, stok memakai ikon bermakna, serta kuantitas/subtotal dikelompokkan. Bilah pembelian ponsel tetap terpisah dari navigasi bawah.
- Beranda menggunakan foto dan tautan koleksi sejajar; halaman masuk memisahkan navigasi, foto, cerita dan formulir. Gerakan menjadi pembukaan visual dan pengungkapan bagian yang singkat, dengan kontrol transaksi tetap di tempatnya. [Catatan beranda/login](public-design-v12.md).
- Mengurangi skala hover foto dan menghilangkan pergeseran/zoom berulang seluruh foto ketika hasil katalog berubah.

## Verifikasi yang dilakukan

| Pemeriksaan | Hasil |
| --- | --- |
| TypeScript aplikasi | Lulus |
| ESLint seluruh aplikasi | 0 error; 9 peringatan lama pada navigasi akun/workspace dan variabel tes |
| Build produksi Vercel | Lulus, 24 halaman statis diproses |
| Akses lokal 11 akun | 14 kelompok lulus; pelanggan dan internal tetap terpisah |
| Akses produksi, halaman, login/logout | 25 pemeriksaan lulus, 0 gagal; tanpa perintah transaksi atau koneksi database langsung |
| Katalog publik | 200 gambar unik, 200 deskripsi unik, 0 barang QA aktif; 5 sampel gambar tersedia |
| Etalase lokal | Autoplay terlihat berubah dari slide 1 ke 2; navigasi manual bekerja; Jeda dan fokus keyboard menghentikan; posisi tetap setelah pemeriksaan berikutnya |
| Tampilan responsif | Etalase 320/390/768/desktop, beranda 320/desktop, login 320/768/desktop dan detail 320/desktop diperiksa. Lebar dokumen sama dengan viewport; tidak ditemukan label saling menutupi pada komposisi yang diperiksa |
| Pembelian lokal | Kuantitas 1→2 mengubah subtotal Rp92.000→Rp184.000; dua unit berhasil masuk keranjang dan dihapus kembali sesudah pemeriksaan |
| Pencarian produksi | Pencarian Cangkir menampilkan satu hasil yang tepat dan membuka detail dengan konteks kembali ke pencarian |
| Browser | Tidak ada error konsol pada tab pemeriksaan; override ukuran viewport dikembalikan |

Pemeriksaan reduced motion dilakukan melalui tinjauan implementasi dan review independen: autoplay/drag dimatikan, perpindahan manual langsung, serta GSAP/CSS mengikuti media query. Preferensi OS tidak diubah atau diemulasikan saat pemeriksaan browser ini. Ini bukan audit aksesibilitas atau performa menyeluruh. Tidak ada klaim penghargaan desain atau kenaikan konversi.

Suite domain 224 tes dari v11 tidak dijalankan ulang karena rilis ini tidak mengubah aturan bisnis. Verifikasi rilis memusatkan pada UI, akses, build dan alur belanja yang terdampak. Audit dokumen/alur bisnis sebelumnya tetap tersedia di [audit penjualan](gap-sales-v11.md) dan [audit pengadaan](gap-operations-v11.md).

## Publikasi

Deployment `dpl_7WYyrtaECyDC991cS9kB2VRfXfnq` berstatus READY dan dipromosikan ke alamat utama; pemetaan domain diperiksa kembali melalui Vercel. [Laporan akses produksi](vercel-v12-results.json) dan [laporan katalog](public-catalog-v12-results.json) menyimpan hasil terstruktur. Publikasi source memakai ekspor bersih yang mengecualikan riwayat Git lokal lama, database dan rahasia.
