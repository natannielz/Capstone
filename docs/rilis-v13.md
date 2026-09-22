# Rilis v13 — landing editorial, dua portal, dan kontrol belanja

23 September 2026. Website: [Unit Toko](https://unit-toko-bni.vercel.app).

Tiga agen menangani riset sumber primer, implementasi landing, dan dua halaman login. Integrasi carousel, keluarga tombol, filter, pemeriksaan browser dan publikasi dilakukan pada pekerjaan utama. Keputusan serta sumber tersedia di [rencana desain](design-plan-v13.md) dan [riset desain/gerakan](research-motion-v13.md).

## Perubahan yang diterapkan

- Tombol besar berlabel “Mulai/Jeda” pada carousel diganti ikon kecil dengan area sentuh 44 × 44 px dan nama aksesibel. Rotasi tetap otomatis setiap enam detik jika terlihat dan preferensi gerak mengizinkan. Ikon jeda dipertahankan agar pengunjung bisa menghentikannya. Garis progres dan nomor koleksi menjadi petunjuk yang tenang.
- Landing memakai judul besar, foto still-life lebar, dan tiga bab koleksi: pantry, rapat, serta merchandise. Foto dibuka dengan animasi tirai; bab menampilkan gerak foto ringan, pengungkapan teks, garis progres, dan navigasi bagian yang mengikuti posisi scroll. Tautan pelanggan dan internal tersedia sejak bagian pertama. [Detail implementasi landing](landing-v13.md).
- Rancangan akhir memilih tiga bab yang ditelusuri melalui scroll, sebagai pengembangan dari konsep momen keseharian dalam riset. Tidak menambahkan carousel otomatis kedua ke landing.
- [Login pelanggan](https://unit-toko-bni.vercel.app/customer/login) memakai latar ivory, foto produk dan formulir membulat. [Login staf](https://unit-toko-bni.vercel.app/staff/login) memakai petroleum gelap, diagram alur kerja dan panel formulir tegas. Gerak pendamping keduanya berbeda; formulir dan tombol submit tetap diam. [Detail dua portal](login-v13.md).
- Gaya tombol dibedakan menurut fungsi: CTA dengan bidang panah, aksi utama terisi, aksi sekunder berbingkai, kontrol filter dengan jumlah filter aktif, urutan hasil, tambah produk, kuantitas, navigasi, dan tindakan hapus. Fungsi yang berulang tetap konsisten.
- Kategori dan ketersediaan dikumpulkan dalam panel “Filter produk” yang dapat dibuka. Urutan hasil tetap langsung tersedia. Filter, pencarian dan pagination mempertahankan konteks URL.
- Memperbaiki gangguan navigasi antarbagian: pembaruan ScrollTrigger pada setiap gambar selesai dimuat sebelumnya dapat menghentikan scroll menuju koleksi. Bingkai gambar sudah memiliki ukuran tetap, sehingga listener tersebut dihapus; pembaruan setelah font siap tetap dipertahankan.

## Verifikasi

| Pemeriksaan | Hasil |
| --- | --- |
| TypeScript | Lulus |
| ESLint seluruh aplikasi | 0 error; 9 peringatan lama pada navigasi akun/workspace dan variabel tes |
| Lint komponen yang diperbaiki terakhir | Lulus tanpa peringatan |
| Build produksi Vercel | Lulus; 24 halaman statis diproses |
| Akses lokal 11 akun | 14 kelompok lulus |
| Halaman, pemisahan akses dan login/logout produksi | 25 pemeriksaan lulus, 0 gagal; tidak mengirim perintah transaksi atau membuka koneksi database langsung |
| Katalog publik | 200 produk dummy dengan 200 gambar unik dan 200 deskripsi unik; pemeriksaan barang QA aktif lulus; 5 sampel gambar tersedia |
| Landing responsif | Desktop, 768 px dan 320 px diperiksa; lebar dokumen sesuai viewport pada ukuran yang diperiksa |
| Navigasi bab | Tautan rapat berhenti sekitar 98 px dari atas layar, bab aktif benar, dan transform foto berubah mengikuti scroll |
| Login | Pelanggan dan staf diperiksa pada desktop serta 320 px; komposisi berbeda, tidak ada horizontal overflow; kontrol tampil/sembunyikan password bekerja |
| Filter | Panel dapat dibuka, Merchandise menghasilkan 63 produk, jumlah filter aktif benar, urutan harga berfungsi; panel 320 px tetap muat |
| Browser produksi | Landing baru dan kontrol carousel ringkas tersedia pada domain utama |

Reduced motion diperiksa melalui tinjauan kode dan review independen; preferensi OS tidak diubah atau diemulasikan. Tidak ada pengukuran FPS, studi pengguna, audit aksesibilitas menyeluruh, atau klaim telah memenangkan penghargaan. Pemeriksaan geometri berlaku pada halaman dan ukuran yang disebutkan, bukan jaminan untuk seluruh kombinasi perangkat.

Aturan bisnis, autentikasi dan penyimpanan tidak diubah pada rilis ini. Suite domain 224 tes dari v11 tidak dijalankan ulang; pemeriksaan difokuskan pada presentasi, akses dan publikasi yang terdampak. [Audit penjualan](gap-sales-v11.md) dan [audit pengadaan](gap-operations-v11.md) tetap menjadi catatan cakupan proses sebelumnya.

## Publikasi

Deployment `dpl_3C6sG64S2WfofE3ruyv8ALSepc2L` berstatus READY dan dipromosikan ke domain utama. [Hasil akses produksi](vercel-v13-results.json) dan [hasil katalog](public-catalog-v13-results.json) menyimpan bukti terstruktur. Source dipublikasikan melalui ekspor bersih yang mengecualikan database, rahasia, unggahan privat dan riwayat Git lokal lama.
