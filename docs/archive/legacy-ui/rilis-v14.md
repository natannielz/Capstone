# Rilis v14 — pengalaman belanja dan ruang kerja

23 September 2026. [Website](https://unit-toko-bni.vercel.app) · [Rencana sebelum implementasi](plan-ui-ux-v14.md).

## Dasar keputusan

Lima agen meneliti gerakan, landing/footer, login/akun, commerce, dan dashboard. Laporan sumber primer serta matriks halaman tersedia melalui rencana. Sasaran visual adalah pengalaman khas dengan hierarki yang jelas; tidak ada klaim penghargaan atau peningkatan konversi yang sudah diukur.

## Yang berubah

- **Carousel:** masalah pointer pada foto yang menghentikan otomatis tanpa penjelasan berhasil direproduksi dan diperbaiki. Timer berulang pulih saat Embla menginisialisasi ulang; keterlihatan mengacu ke isi kartu. Status menunjukkan berjalan/jeda/tidak terlihat. Fokus keyboard, geser horizontal, jeda manual, dan reduced motion tetap dihormati. Menahan pointer menunda timer sampai dilepas; kontrol jeda tetap ringkas.
- **Footer dan landing:** footer bersama berwarna petroleum dengan kelompok Belanja, Akun Anda, dan Unit Toko, serta keterangan simulasi terpisah. Navigasi landing mobile terlihat, bab koleksi lebih ringkas, foto merchandise tidak mengulang hero. Benturan CSS footer lama yang sempat menyebabkan kolom bertumpuk ditemukan dan diperbaiki saat QA.
- **Katalog hingga checkout:** chip filter dapat dihapus terpisah; stok dan harga kartu mengikuti kemasan yang benar-benar cocok; pilihan kemasan menampilkan harga/stok. Quick-add menjelaskan stok sudah masuk keranjang, tombol tambah berhenti pada batas stok, tahapan keranjang/checkout diperjelas. Kontras tombol tautan pada keadaan kosong diperbaiki setelah ditemukan dalam browser.
- **Login, profil, pesanan:** fokus pesan gagal masuk, tautan koreksi portal, notifikasi sesi/password, kontrol tampilkan kata sandi, timeout profil dan pemulihan sesi. Daftar/detail menggunakan tindakan pelanggan yang sama, mempertahankan konteks filter ketika kembali, menampilkan timeline dan ringkasan tagihan lebih jelas. Konfirmasi penerimaan memiliki tahap tinjau sebelum kirim.
- **Ruang kerja:** satu panel pekerjaan berikutnya sesuai peran, angka pendukung lebih tenang, navigasi berkelompok, invoice berdasarkan urgensi, tugas kurir dengan tujuan langsung, serta angka admin yang berasal dari data nyata. Sistem jarak, formulir, tabel, status, dan angka diterapkan bersama pada 12 modul yang tercantum dalam rencana.

Gerakan pembukaan landing, bab koleksi saat scroll, dan dua komposisi login dari v13 dipertahankan. v14 menajamkan struktur serta memperbaiki interaksi; tidak mengganti semua halaman dengan efek baru.

## Validasi

- TypeScript seluruh aplikasi: lulus.
- ESLint seluruh aplikasi: 0 error, 9 warning yang sudah ada (7 navigasi reset sesi melalui `location.assign`, 2 variabel tes tidak terpakai).
- Tes: **238/238 lulus**. Termasuk empat tes keluarga kemasan, enam tes prioritas dashboard dan izin tujuan, empat tes tindakan/return URL pelanggan. Harness login lama diselaraskan dengan hook animasi aktual tanpa menghapus assertion POST, hydration, duplicate-submit, retry; assertion fokus error ditambahkan.
- Akses lokal: 14 kelompok pemeriksaan, 11 akun, pemisahan pelanggan/internal dan redirect per peran lulus. Tidak membuat transaksi bisnis dalam smoke akses.
- Build produksi Vercel: berhasil, seluruh 24 halaman statis selesai; deployment `dpl_85rj8Bm5gzW3taGnwrGzGnSkyNpn`, [URL deployment](https://unit-toko-c7fg6jdco-natannielzs-projects.vercel.app).
- Domain utama dikonfirmasi menunjuk deployment READY tersebut. Smoke produksi **25/25 lulus**: halaman, login terpisah, 11 akun, pembatasan akses, dan pencabutan sesi uji. Katalog publik **200 produk, 200 path gambar unik, 200 deskripsi unik**; lima sampel gambar berhasil dimuat. Helper tidak membaca API bisnis privat atau mengirim perintah bisnis.

### Bukti browser lokal

- Carousel berpindah 01 → 02 → 03 ketika pointer berada di foto. Jeda manual bertahan lebih dari satu interval, tombol lanjut bekerja saat pointer masih di kontrol. Setelah patch gesture, geser mouse horizontal mengubah slide dan status menjadi Dijeda.
- Resize 1280 → 1024 px diikuti perpindahan 01 → 03 dengan status berjalan. Fokus keyboard pada panah menjeda. Saat di luar layar status menjadi Otomatis saat terlihat; kembali ke atas memulihkan timer dan slide berubah 03 → 01.
- Footer diuji pada 1280, 390, dan 320 px; tidak ada overflow halaman. Disclosure tidak tertutup navigasi belanja bawah.
- Landing 320 px: anchor Kebutuhan rapat berhenti pada y=84 px, di bawah navigasi koleksi yang berakhir pada y=63 px; judul terlihat dan lebar halaman tidak meluber.
- Filter 320 px: kategori Minuman + stok tersedia + harga terendah, lalu hapus kategori saja; stok dan urutan harga tetap pada URL.
- Detail kemasan air: pilihan 3 dus memperbarui harga menjadi Rp162.000, jumlah dua menghasilkan Rp324.000. Keranjang dibatasi pada stok 16; subtotal mengikuti jumlah. Barang yang ditambahkan untuk QA sudah dihapus kembali.
- Checkout 390 px: hierarki penerima/barang/ringkasan dan tombol tetap terbaca, lebar dokumen sama dengan viewport konten; tidak mengirim pesanan baru.
- Dashboard kepala toko desktop dan 320 px: satu prioritas nyata, CTA membuka antrean review, sidebar mobile membuka/menutup dengan benar, tanpa overflow halaman.
- Profil: tombol tampilkan/sembunyikan benar-benar mengubah tipe input tanpa mengganti password. Detail pesanan pelanggan 390 px muat; kembali ke daftar mempertahankan filter Terpenuhi; pagination awal benar-benar disabled.
- Tombol keadaan kosong diverifikasi kembali: teks putih pada latar petroleum.
- Dua login pada domain produksi diperiksa di 390 px: komposisi staf petroleum dan pelanggan ivory/foto tetap berbeda, formulir terbaca, dan tidak ada overflow horizontal.

## Batas pemeriksaan dan pekerjaan lanjutan

Ini audit lima bidang dan perbaikan lintas halaman, bukan klaim setiap kemungkinan state telah diuji visual. Perilaku prioritas seluruh peran diuji dengan fixture; browser dashboard menggunakan kepala toko sebagai sampel. Gestur sentuh fisik, perubahan preferensi gerakan pada OS, keyboard layar ponsel, pembaca layar, dan semua jenis anchor/konfirmasi penerimaan belum diuji langsung di perangkat; reduced motion dan alur pemulihan ditinjau pada sumber/tes yang relevan. Tidak ada klaim audit WCAG lengkap.

Rekomendasi yang belum diterapkan dicatat pada [laporan dashboard](implementation-v14-dashboard.md) dan [laporan akun](implementation-v14-account.md): search/pagination tambahan pada beberapa modul, anchor seksi administrasi, caption seluruh tabel lama, penyempurnaan dirty guard Back SPA, dan sinkronisasi perubahan profil dari sesi lain. Alur reset email/pendaftaran/pelacakan langsung tidak dibuat tanpa backend yang mendukung.

Autoplay berhenti ketika pointer memilih tautan/kontrol; pointer pada ruang foto tidak menghentikannya. Ini keputusan produk yang berbeda dari rekomendasi APG untuk berhenti pada seluruh area hover, dibahas pada rencana. Kontrol jeda, fokus keyboard dan reduced motion tetap disediakan.

## Publikasi

Hasil pemeriksaan domain dan katalog setelah promosi dicatat di [smoke produksi](vercel-v14-results.json) dan [katalog publik](public-catalog-v14-results.json). Export GitHub hanya memuat kode/aset dan dokumen yang diizinkan, dengan pemindaian credential; database, konfigurasi privat, dan riwayat Git lokal lama dikecualikan.
