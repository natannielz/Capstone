# Implementasi v14 — login, profil, dan akun pelanggan

23 September 2026. Mengikuti [rencana v14](plan-ui-ux-v14.md) dan [riset login/akun](research-v14-login-account.md). Catatan ini mencakup perubahan sumber, pemeriksaan tipe, lint terarah, dan tes helper; pemeriksaan browser serta publikasi dikerjakan agen utama.

## Yang diterapkan

- **Tindakan pesanan:** satu selector `customerOrderActions` digunakan untuk filter daftar, jumlah tindakan pada kartu, dan panel **Perlu tindakan Anda** di detail. Substitusi yang masih berlaku, pengiriman yang perlu dikonfirmasi setelah tiba, bukti pembayaran yang belum diunggah, dan sisa tagihan ditautkan ke bagian yang tepat.
- **Pembayaran menunggu toko:** pembayaran tercatat dengan bukti tidak memunculkan ajakan membayar kembali. Dana terverifikasi yang terkait invoice dan masih tersedia untuk dialokasikan juga tidak diperlakukan sebagai tugas pelanggan. Keadaan belum lengkap mengarah ke unggahan bukti, bukan transaksi kedua. Selector tidak mengubah perhitungan tagihan atau aturan pembayaran.
- **Konteks daftar:** detail membawa pencarian, filter, dan halaman sebelumnya. Parameter kembali disaring untuk rute `/account/orders` beserta parameter yang diizinkan saja. Pagination pada batas awal/akhir menggunakan tombol HTML yang benar-benar nonaktif.
- **Hierarki detail:** identitas/status pesanan, ringkasan tindakan, barang, substitusi, perkembangan, pengiriman, dan tagihan muncul sebelum alamat/lampiran pendukung. Target anchor dapat menerima fokus dan memiliki jarak terhadap header. Tagihan gabungan menunjukkan nilai bagian pesanan ini dan membedakannya dari total/sisa gabungan.
- **Perkembangan:** peristiwa disusun menurut tanggal yang tersedia—dibuat, ditinjau, pengiriman disiapkan, diterima/gagal/dibatalkan, serta invoice diterbitkan. Tidak ada perkiraan tiba atau tanggal dispatch buatan. Peristiwa pada hari yang sama mempertahankan urutan stabil; data tidak selalu memiliki jam.
- **Penerimaan barang:** sebelum mutasi pertama dikirim, pengguna melihat ringkasan penerima serta jumlah dikirim, diterima, dan selisih setiap barang. Tombol **Ubah isian** kembali ke kolom penerima. Pemulihan draf perintah, ID perintah, penguncian hasil ambigu, dan validasi domain tetap digunakan.
- **Profil:** helper jaringan lokal diganti dengan `requestJson` yang memiliki timeout 20 detik dan status HTTP. Respons 401 menampilkan jalur masuk kembali ke portal yang sesuai. Isian tidak otomatis dibuang atau dikirim ulang. Tiga kolom kata sandi memiliki kontrol tampilkan/sembunyikan berlabel; kata sandi tidak disimpan sebagai draf.
- **Login:** dua komposisi pelanggan/staf v13 dipertahankan. Fokus menuju kesalahan setelah kegagalan; salah portal menyediakan tautan langsung ke portal lain. Pesan setelah kata sandi berubah dan sesi berakhir dibatasi ke dua enum tetap, tidak merender teks query bebas.
- **Umpan balik:** refresh data lama memiliki teks status; kegagalan logout menawarkan mencoba logout lagi. Unggahan memiliki pesan keberhasilan dan relasi bantuan/kesalahan ke input. Bantuan alamat profil terhubung ke kolomnya.
- **Tampilan:** `account-v14.css` mengatur permukaan akun, tindakan, kartu pesanan, nilai tagihan, form profil, dan review penerimaan. Pada mobile nilai tagihan menjadi satu kolom; teks panjang dapat membungkus; tombol penting minimal 44 px. Tidak ada animasi masuk pada form atau kontrol transaksi.

## Berkas yang berubah

- `website/components/login.tsx`
- `website/components/profile.tsx`
- `website/components/customer-account.tsx`
- `website/lib/domain/customer-order-views.ts`
- `website/tests/customer-account-v14.test.ts`
- `website/app/account-v14.css` — diimpor oleh agen utama setelah CSS akun sebelumnya
- `website/app/login-v13.css` — tambahan tampilan notifikasi dan fokus kesalahan

Tidak ada perubahan pada engine transaksi, endpoint autentikasi, ShopShell, layout, CSS global, atau modul ruang kerja dalam subtask ini.

## Pemeriksaan yang selesai

- TypeScript seluruh aplikasi: **lulus**, menggunakan `tsc --noEmit --incremental false`.
- Lint terarah pada tiga komponen, helper, dan tes baru: **0 error**. Terdapat **5 warning** navigasi `window.location.assign` pada jalur yang sudah ada di `customer-account.tsx`. Jalur reset sesi dan pemulihan autentikasi tidak dialihkan ke navigasi SPA dalam perubahan ini.
- **10 tes lulus**: empat tes v14 dan enam tes regresi akun v9. Skenario v14 mencakup bukti belum ada → menunggu verifikasi → ditolak → terverifikasi belum dialokasikan → lunas; pengiriman yang bisa/tidak bisa dikonfirmasi; substitusi berlaku/tidak berlaku; serta penyaringan konteks kembali yang berbahaya atau tidak valid.
- Bundler tes memerlukan akses di luar pembatasan sandbox untuk membaca struktur direktori; percobaan dengan izin yang sesuai berhasil. Output hanya di direktori `.test` lokal proyek.

## Batas dan rekomendasi yang belum diterapkan

- Uji visual desktop/mobile, fokus anchor di bawah header aktual, keyboard mobile pada dialog panjang, dan interaksi form perlu dilakukan agen utama. Klaim tidak ada overlap belum dibuat oleh subtask ini.
- Guard perubahan profil masih menggunakan mekanisme link/`beforeunload` yang ada. Perilaku Back browser pada navigasi SPA perlu reproduksi tersendiri; tidak ditambahkan intersepsi history yang berisiko di sini.
- Refresh profil dari sesi lain belum menyinkronkan state isian secara otomatis. Memisahkan pembaruan data bersih dari perubahan lokal memerlukan pemeriksaan alur tambahan agar tidak menimpa isian pengguna.
- Password reset melalui email, pendaftaran mandiri, pelacakan kurir langsung, pilihan tanggal pengiriman, dan aturan refund baru tidak ditambahkan karena backend/kebijakannya belum ada.
- Pembayaran tambahan tetap tersedia melalui tagihan sesuai aturan yang ada. Ringkasan tindakan tidak menganjurkan duplikasi saat ada pembayaran pending; aplikasi tidak melarang cicilan baru atau menghitung ulang saldo dengan aturan buatan.
- Saldo terverifikasi umum yang belum terkait invoice tetap dijelaskan oleh informasi saldo yang ada. Selector tugas hanya dapat mengenali dana yang secara eksplisit terkait invoice; ia tidak menebak alokasi dana pelanggan.
- Tidak ada pengubahan label `neededAt` menjadi estimasi pengiriman. Checkout pelanggan tidak mengumpulkan tanggal tersebut.

## Uji browser yang disarankan

1. Masuk pada kedua portal, salah kata sandi, salah portal, serta pesan setelah password berubah; cek fokus dan viewport 320/390 px.
2. Cari/filter pesanan lalu buka detail dan kembali; cek halaman/pencarian tetap serta kontrol pagination awal/akhir.
3. Ikuti setiap jenis anchor tindakan, termasuk pembayaran dengan/tanpa bukti; pastikan tidak tertutup header.
4. Uji penerimaan sebagian/nol/penuh pada data lokal; lihat review, ubah isian, konfirmasi sekali, dan ulangi respons ambigu menggunakan draft yang sama.
5. Lihat invoice gabungan, nama barang/alamat/berkas panjang, form profil, dan kolom password dengan kontrol tampilkan; cek tidak ada pemotongan pada layar kecil.
