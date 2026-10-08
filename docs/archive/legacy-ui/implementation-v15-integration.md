# Integrasi v15 — tabel, persediaan, dan tujuan dashboard

Mengikuti [rencana lanjutan](plan-ui-ux-v15.md).

## Perubahan agen utama

- Persediaan membedakan barang/batch/retur/opname yang belum ada dan hasil filter kosong. Kepala Toko, Staf, dan Bagian Laporan menerima petunjuk sesuai kewenangannya. Tombol menuju pengadaan hanya diberikan kepada Kepala/Staf; tindakan batch diarahkan kepada Staf yang memiliki batch.
- Keadaan kosong tidak lagi menampilkan tabel header tanpa isi atau dua tombol reset yang identik. Pagination persediaan memiliki nama navigasi.
- Tabel persediaan, daftar pesanan, dan rekap penjualan mempunyai caption serta `scope=col`. Nilai stok/jumlah/harga memakai kolom numerik yang sesuai. Agen lain menangani tabel pada workspace dan modul operasional, sehingga seluruh tabel HTML aplikasi kini memiliki caption dan scope header; komponen primitif tabel tidak diberi caption universal palsu.
- Kartu admin membawa `adminSection=users|divisions|suppliers`; kartu pemeriksaan pembayaran membawa status menunggu verifikasi dan menghapus pencarian lama yang bisa menyembunyikan antrean. Assertion tujuan ditambahkan ke tes dashboard yang ada.
- URL halaman yang tidak diizinkan kini tetap mempunyai judul “Halaman tidak tersedia” dan petunjuk menuju menu sesuai peran; sebelumnya judul dapat kosong. Izin data/aksi tetap mengikuti server.
- CSS daftar workspace dan operasional diimpor setelah gaya sebelumnya. Reset filter yang berulang pada keadaan kosong pembayaran/pengadaan/admin disederhanakan.

## Temuan saat pemeriksaan

Saat live update lokal berlangsung, beberapa perubahan filter kembali ke `/workspace?`. Pemeriksaan sumber Next menunjukkan kode lama menyalin marker history privat framework, yang membuat integrasi URL canonical dilewati. Agen navigasi memperbaiki metadata history hanya milik aplikasi dan menambahkan regresi. Kejadian browser yang diamati adalah saat pengembangan; bukan klaim sudah direproduksi pada produksi.

Review independen profil menemukan potensi logout terhalang ketika storage tidak tersedia, draf yang muncul kembali setelah save jika cleanup gagal, serta respons PATCH lama yang dapat menghapus draf baru. Ketiganya diperbaiki dan diberi regresi sebelum rilis; hasil final ada di catatan akun dan rilis.

Pemeriksaan mobile juga menemukan label aksesibel berposisi absolut keluar dari kontainer tabel. Penetapan posisi relatif pada kontainer membatasi gulir di dalam tabel tanpa menyembunyikan caption dari pembaca layar. Pengukuran ulang pembayaran dan pengadaan pada viewport 320 px tidak menunjukkan overflow dokumen.

Pengujian browser, jumlah tes, build, dan status publikasi final dicatat di catatan rilis v15 agar tidak mencampur hasil sementara dengan hasil final.
