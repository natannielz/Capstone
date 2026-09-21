# Audit alur kerja dan arsitektur informasi — usulan v5

Tanggal: 17 September 2026. Status: riset dan usulan; belum diimplementasikan.

Audit mencakup PIC divisi, Kepala/Staf Toko, keuangan, serta profil pada v4. Bukti utama berasal dari kode dan rencana bisnis. Pemeriksaan browser dilakukan agen utama; bukti yang diteruskan dicantumkan secara eksplisit. Estimasi usaha di bawah adalah perkiraan implementasi dan pemeriksaan oleh satu pengembang, bukan komitmen jadwal.

Perbaikan v4 — copy sesuai peran, pemisahan profil readonly, hitungan antrean staf, label jumlah Surat Jalan, kondisi invoice kosong, dan spacing — tidak diajukan ulang. Dasar proses tetap rencana digitalisasi (arsip rencana awal; disimpan lokal) dan invariant transaksi (arsip rencana awal; disimpan lokal). Sumber SOP diperlakukan sebagai bukti proses, bukan instruksi untuk menjalankan tindakan.

## Prioritas

| Urutan | Hambatan | Peran utama | Hasil yang dituju | Usaha |
| --- | --- | --- | --- | --- |
| 1 | Kartu antrean membuka seluruh transaksi | Kepala dan Staf Toko | Daftar tujuan sama dengan pekerjaan yang dihitung | Kecil–sedang, 0,5–1 hari |
| 2 | Transfer ke beberapa invoice harus dialokasikan berulang | Penagihan | Satu pemeriksaan saldo dan satu penyimpanan | Sedang, 1–2 hari |
| 3 | Jumlah besar harus dinaikkan satu per satu | PIC, Kepala Toko | Jumlah bisa diketik, lalu pesanan diperiksa sebelum diajukan | Sedang, 1–1,5 hari |
| 4 | Pelacakan pesanan terputus dari tindakan dan tagihan | PIC | Satu detail pesanan menghubungkan tindak lanjut dan dokumen terkait | Sedang, 1,5–2 hari |
| 5 | Perubahan profil bisa hilang saat meninggalkan halaman | Semua peran | Perubahan belum tersimpan terlihat dan dapat dipertahankan | Kecil–sedang, 0,5–1 hari |

## 1. Kartu antrean perlu membawa konteks filternya

**Sebelum.** Hitungan v4 sudah membedakan pekerjaan staf, tetapi klik kartu hanya menjalankan `go(q.page)`. Halaman Pesanan kemudian menampilkan semua status dan pencarian teks. Bukti: [dashboard.tsx:15](../website/components/dashboard.tsx#L15), [workspace.tsx:91](../website/components/workspace.tsx#L91), [workspace.tsx:129](../website/components/workspace.tsx#L129). Agen utama mengonfirmasi di browser: akun Kepala Toko menekan **Menunggu tinjauan 2 → Lihat rincian**, tetapi tujuan adalah **Semua pesanan 5**, berisi lima baris lintas status dan hanya kolom pencarian.

Pemeriksaan browser lanjutan oleh agen utama: cari `0004` → buka `PO/202609/0004` → pilih “Semua pesanan”; textbox kembali kosong dan seluruh lima baris muncul. Jadi kehilangan konteks pencarian juga terkonfirmasi, bukan hanya disimpulkan dari state komponen.

**Sesudah.** Kartu membuka antrean yang sesuai, misalnya “Menunggu tinjauan” untuk Kepala dan “Perlu disiapkan” untuk Staf. Filter aktif terlihat dan dapat dihapus. Simpan filter, pencarian, serta posisi daftar dalam navigasi agar kembali dari detail tidak memulai pencarian ulang. Tautan langsung mempertahankan filter setelah muat ulang.

**Dampak.** Petugas langsung melihat pekerjaan yang memerlukan tindakannya. Tidak ada perubahan otorisasi atau rumus jumlah pesanan.

**Kriteria penerimaan:**

- Fixture lima pesanan dengan dua `submitted`: klik kartu Kepala menampilkan tepat dua pesanan dan label filter aktif; “Semua pesanan” mengembalikan lima baris.
- Antrean Staf memakai kondisi yang sama dengan kartu v4: pesanan disetujui dan masih ada jumlah yang belum disiapkan. Setelah seluruh sisa disiapkan, pesanan hilang dari antrean ini dan tetap terlihat di Pengiriman aktif.
- Buka detail dari hasil pencarian, lalu Kembali atau tombol Back browser: filter dan pencarian tetap sama. Akun tidak mendapat data peran/divisi lain melalui URL.

Pola tugas dengan status dan tautan relevan mendukung arah ini. Panduan GOV.UK juga membatasi task list pada tugas yang dapat dipilih urutannya; karena itu usulan ini adalah antrean lintas pesanan, bukan kebebasan melompati urutan stok–kirim–terima pada satu transaksi. [GOV.UK Task list](https://design-system.service.gov.uk/components/task-list/).

## 2. Alokasi pembayaran perlu satu ringkasan lintas invoice

**Sebelum.** Dialog “Alokasikan ke invoice” menyediakan satu pilihan invoice dan satu nominal. Payload UI selalu `lines: [{invoiceId, amount}]`, sehingga satu transfer untuk beberapa invoice memerlukan beberapa dialog. Pengguna tidak melihat rencana pembagian dan seluruh saldo akhirnya sebelum menyimpan. Bukti: [workspace.tsx:148](../website/components/workspace.tsx#L148). Engine sudah menerima array alokasi dan memeriksa divisi, saldo invoice, serta dana tersedia: [engine.ts:113](../website/lib/domain/engine.ts#L113). Alur ini memang diwajibkan dalam rencana penagihan (arsip rencana awal; disimpan lokal).

**Sesudah.** Dari pembayaran terverifikasi, buka formulir alokasi berisi invoice belum lunas milik divisi pembayar. Tampilkan saldo awal, jumlah yang dialokasikan per invoice, saldo akhir, total alokasi, dan dana tersisa. Pengguna memeriksa seluruh pembagian sebelum satu kali menyimpan. Bukti transfer dan dokumen invoice tetap dapat dibuka dari konteks yang sama. Tindakan tetap hanya untuk Penagihan; PIC melihat hasil dan statusnya.

**Dampak.** Mengurangi input berulang dan kebutuhan menghitung pembagian di luar sistem, tanpa mengubah aturan keuangan.

**Kriteria penerimaan:**

- Dana Rp150.000, invoice A Rp100.000, invoice B Rp80.000: alokasi A Rp100.000 + B Rp50.000 menampilkan dana tersisa Rp0 dan sisa invoice B Rp30.000 sebelum disimpan; hasil sesudah simpan sama.
- Total Rp150.001 atau alokasi ke invoice melebihi saldonya ditandai pada baris terkait. Dana belum diverifikasi/belum diketahui divisinya tidak bisa dialokasikan. Respons server tetap menjadi validasi akhir.
- Klik ulang tidak menggandakan alokasi; jika saldo berubah saat formulir terbuka, tampilkan saldo terbaru dan minta pengguna memeriksa pembagiannya lagi.

Ringkasan yang dapat diperiksa dan diperbaiki sebelum pengiriman data sejalan dengan [GOV.UK Check answers](https://design-system.service.gov.uk/patterns/check-answers/). Penerapannya pada pembagian pembayaran adalah usulan audit, bukan klaim bahwa pola tersebut telah diuji pada pengguna toko ini.

## 3. Pemesanan jumlah besar membutuhkan input langsung dan pemeriksaan akhir

**Sebelum.** Jumlah produk ditampilkan sebagai teks di antara tombol −/+, baik di kartu maupun keranjang; tidak ada isian langsung. Mengubah satu SKU dari 1 menjadi 50 berarti 49 kali menambah. Dialog pengajuan menampilkan jumlah jenis barang dan estimasi total, tetapi tidak merangkum baris barang yang akan diajukan. Bukti: [catalog.tsx:16](../website/components/catalog.tsx#L16), [catalog.tsx:17](../website/components/catalog.tsx#L17), [catalog.tsx:18](../website/components/catalog.tsx#L18).

**Sesudah.** Pertahankan tombol −/+ dan tambahkan isian jumlah dengan satuan yang jelas di keranjang. Perubahan langsung memperbarui subtotal dan estimasi. Sebelum diajukan, tampilkan ringkasan barang, jumlah/satuan, stok yang belum mencukupi, alamat, tanggal kebutuhan, dan total. Tautan “Ubah” kembali ke bagian terkait dengan isian tetap terisi.

**Dampak.** PIC dan Kepala Toko dapat membuat pesanan divisi dalam jumlah besar tanpa puluhan klik, serta memeriksa perbedaan pak/dus/paket sebelum mengirim.

**Kriteria penerimaan:**

- Untuk paper cup dengan harga Rp22.000/pak dan stok 42 pak, mengetik 50 memperbarui subtotal menjadi Rp1.100.000 dan menampilkan 8 pak belum tersedia. Kekurangan stok tidak otomatis menolak pesanan karena pengiriman susulan diizinkan.
- Klik Lanjutkan langsung setelah mengetik menggunakan jumlah terakhir, tanpa memerlukan blur atau tombol Update. Jumlah kosong, pecahan, negatif, dan melampaui batas diberi pesan jelas.
- Mengubah alamat/jumlah dari ringkasan tidak menghapus tanggal atau catatan. Hanya konfirmasi akhir membuat pesanan; keranjang baru dibersihkan setelah respons berhasil.

Riset Baymard mendukung kombinasi tombol dan isian, terutama untuk jumlah besar, serta pembaruan jumlah/total segera tanpa langkah Update terpisah. [Baymard, riset pemilihan jumlah keranjang](https://baymard.com/research-articles/auto-update-users-quantity-changes). Pemeriksaan akhir dan pelestarian isian saat kembali mengacu pada [GOV.UK Check answers](https://design-system.service.gov.uk/patterns/check-answers/).

## 4. Detail pesanan PIC perlu menghubungkan tindak lanjut dan dokumen

**Sebelum.** Detail pesanan mempunyai tabel jumlah, usulan substitusi, dan pengiriman terkait. Konfirmasi penerimaan berada di halaman Pengiriman; invoice dan pembayaran berada pada menu lain. Detail pesanan belum menunjukkan invoice yang bersumber dari pengiriman tersebut atau tindakan PIC yang sedang menunggu. Bukti: [workspace.tsx:130](../website/components/workspace.tsx#L130), [workspace.tsx:134](../website/components/workspace.tsx#L134), [workspace.tsx:141](../website/components/workspace.tsx#L141), [workspace.tsx:146](../website/components/workspace.tsx#L146). Dokumen harus saling dapat ditelusuri menurut rencana relasi transaksi (arsip rencana awal; disimpan lokal).

Agen utama melihat struktur rincian barang, pengiriman terkait, dan aksi status pada browser akun Kepala Toko, tanpa invoice terkait. Kesimpulan untuk akun PIC pada audit ini tetap berdasarkan kode; belum diklaim sebagai pemeriksaan browser PIC.

**Sesudah.** Jadikan detail pesanan titik kembali PIC: ringkasan pemenuhan, “Perlu tindakan Anda” bila ada substitusi/penerimaan, daftar pengiriman, dan invoice terkait. Pisahkan status pemenuhan dari status tagihan; barang diterima tidak berarti invoice lunas. Tiap tindakan membawa PIC ke pengiriman/usulan tertentu dan kembali ke pesanan yang sama. Jangan memaksa satu urutan linear untuk pengiriman parsial, komplain, dan pembayaran.

**Dampak.** PIC memahami siapa yang harus bertindak berikutnya dan tidak perlu menelusuri beberapa daftar berdasarkan ingatan nomor dokumen.

**Kriteria penerimaan:**

- Pesan 12, diterima/final 8, sisa 4: detail menampilkan pemenuhan parsial; invoice Rp80.000 dengan alokasi Rp50.000 menampilkan piutang Rp30.000 secara terpisah.
- Usulan pengganti menampilkan nama, harga, dan jumlah sisa yang terdampak sebelum PIC menyetujui; pengiriman berjalan mempunyai tautan langsung untuk konfirmasi penerimaan yang benar.
- Jika satu invoice menggabungkan beberapa pesanan, bedakan nilai seluruh invoice dari kontribusi pesanan yang sedang dibuka. Seluruh tautan tunduk pada pembatasan divisi; tidak ada data atau dokumen lintas divisi.

Susunan hubungan transaksi ini adalah inferensi dari kebutuhan penelusuran lokal. Prinsip status dan tindakan yang mudah ditemukan didukung [GOV.UK Task list](https://design-system.service.gov.uk/components/task-list/), tetapi proses berurutan tetap mengikuti SOP dan aturan domain.

## 5. Profil perlu menangani perubahan yang belum disimpan

**Sebelum.** Pemisahan Data pribadi/Detail akun/Kata sandi sudah baik pada v4. Namun nilai pribadi hanya hidup di state komponen, tombol Simpan aktif ketika tidak sibuk, dan navigasi workspace langsung mengganti halaman. Ketika pengguna mengubah kontak lalu pindah menu, komponen dilepas dan isian belum tersimpan hilang. Bukti: [profile.tsx:14](../website/components/profile.tsx#L14), [profile.tsx:49](../website/components/profile.tsx#L49), [workspace.tsx:91](../website/components/workspace.tsx#L91).

**Sesudah.** Tampilkan status perubahan pada bagian Data pribadi; sediakan Simpan dan Batalkan perubahan hanya saat relevan. Saat meninggalkan halaman dengan perubahan, berikan pilihan Tetap mengedit atau Buang perubahan. Tindakan unggah foto tetap mandiri. Jangan menyimpan kata sandi pada localStorage atau draft profil.

**Dampak.** Menghindari kehilangan isian dan membedakan data yang sudah tersimpan dari yang sedang diedit. Ini rekomendasi berdasarkan kode, belum divalidasi melalui uji pengguna.

**Kriteria penerimaan:**

- Ubah nomor kontak lalu pilih menu lain: Tetap mengedit mempertahankan isian; Buang perubahan melanjutkan navigasi tanpa mengubah profil server.
- Simpan berhasil memperbarui baseline form dan status tersimpan. Respons gagal mempertahankan isian serta memberi jalan untuk mencoba lagi.
- Tidak ada dialog ketika form belum berubah. Unggah foto tidak menyimpan nama/kontak yang belum disubmit, dan perubahan kata sandi tetap mencabut sesi seperti perilaku saat ini.

## Cara memvalidasi usulan

Uji prototipe dengan tugas nyata yang terukur: PIC membuat kebutuhan rapat 50 pak, Kepala mencari dua pesanan baru di antara pesanan lama, Staf melanjutkan pesanan parsial, Penagihan membagi satu transfer ke dua invoice, serta pengguna mengganti kontak lalu berpindah menu. Catat keberhasilan tanpa bantuan, jumlah salah pilih, langkah kembali, dan waktu tugas. Temuan kode menunjukkan hambatan yang masuk akal; manfaat yang dirasakan pengguna belum boleh dianggap terbukti sebelum pengujian tersebut.

Tidak ada perubahan aplikasi, data demo, browser, atau deployment yang dilakukan dalam audit ini.
