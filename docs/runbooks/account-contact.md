# Kontak pribadi akun

Kontak pribadi tersedia pada `/identity/account` dalam profil `full`. Pilot `release1` tetap memakai build dan skema sebelumnya. Status implementasi, bukti lokal, staging dan produksi dicatat dalam [ledger](../quality/production-progress.md).

## Penggunaan

Pengguna yang masih memiliki akun, sesi dan penugasan aktif dapat mengisi **Kontak pribadi**: nama kontak, telepon, jabatan dan alamat. Nama serta alasan perubahan wajib diisi; bidang opsional yang dikosongkan disimpan sebagai `null`. Batas nama/jabatan 160 karakter, telepon 40, alamat/alasan 1.000. Antarmuka merapikan spasi tepi dan normalisasi Unicode; API tetap menolak teks tidak kanonis, karakter kendali dan bentuk permintaan yang tidak dikenal.

**Simpan kontak pribadi** memakai revisi yang sedang ditinjau. Data dan **Riwayat kontak pribadi** tetap tersimpan setelah muat ulang atau sesi berikutnya. Pengguna hanya membaca dan mengubah kontaknya sendiri. Tidak ada pemilihan akun sasaran atau hak administrator untuk mengubah kontak orang lain melalui operasi ini.

Jika jawaban terputus setelah pengiriman, gunakan **Periksa hasil perubahan kontak**. Browser mempertahankan identitas akun, operasi, kunci dan waktu pengiriman dalam penyimpanan sesi; tidak menyimpan isi kontak, alasan, cookie atau token. Pemulihan membaca kuitansi asli tanpa mengirim perubahan lagi. Referensi tetap disimpan jika hasil belum ditemukan. Jangan mengirim perubahan baru sampai hasil sebelumnya diketahui. Akun atau revisi yang berubah memerlukan pemeriksaan ulang.

Nama kontak adalah pernyataan pengguna. Ia tidak mengganti nama atau email yang dibuktikan oleh penyedia identitas, kata sandi, verifikasi email, MFA, penugasan atau izin cabang. Kontak tidak memesan barang atau menulis stok, pembayaran maupun jurnal keuangan.

## Penyimpanan dan operasi

Migrasi aditif M82 memiliki kepala kontak, riwayat perubahan dan kuitansi asli. Riwayat lengkap, hash, tautan kepala dan kuitansi harus tetap konsisten. Penulisan memakai transaksi, pemeriksaan revisi, identitas operasi dan penjagaan kewenangan terbaru. Riwayat dibatasi 256 perubahan per akun; bila batas tercapai, diperlukan penanganan operator melalui proses yang disetujui, bukan penghapusan riwayat atau pengulangan kunci.

Pemulihan cadangan harus mempertahankan ketiga pemilik, penghitung ID dan identitas/kewenangan pengguna secara koheren. Migrasi memeriksa definisi pendahulu dan calon penyimpanan sebelum perubahan, menolak pemilik yang tidak dapat dijelaskan serta mempertahankan validator operator yang lebih kuat. Migrasi dijalankan melalui prosedur migrasi, bukan permintaan pengguna.

Operasi API berada pada `/api/v1/identity/contact`: pembacaan terkini, riwayat dan pemulihan menggunakan POST dengan tujuan `account-contact`; penyimpanan menambah `If-Match` dan `Idempotency-Key`. Seluruhnya memerlukan sesi terverifikasi, akun yang ditinjau, origin yang benar dan CSRF. Referensi dan revisi dikirim sebagai string tanpa kehilangan ketelitian. Respons privat tidak boleh dicache.

Penyedia identitas dan sesi sintetis dalam pemeriksaan lokal tidak menerima Auth0 maupun host produksi. Akses nyata, migrasi terotorisasi, cadangan/pemulihan, staging dan penerimaan produksi tetap mengikuti [panduan rilis](../releases/release-1.md) dan ledger.
