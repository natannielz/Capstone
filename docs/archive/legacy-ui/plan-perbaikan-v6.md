# Rencana perbaikan v6 — Unit Toko BNI

Tanggal: 17 September 2026. Situs: https://unit-toko-bni.vercel.app. Ruang lingkup: demo capstone Unit Toko untuk divisi BNI, setelah rilis v5.

Status dokumen: implementasi, pengujian lokal, dan rilis Vercel v6 selesai pada 17 September 2026. Hasil serta batas verifikasi berada pada [catatan rilis v6](rilis-v6.md). Rencana di bawah dipertahankan sebagai dasar penerimaan pekerjaan.

## Hasil audit

Tiga agen memeriksa katalog/desain, alur transaksi, serta kualitas operasional/auth. Agen utama memeriksa website publik dengan akun Kepala Toko pada desktop, ponsel 390px, dan tablet 980px. Pengujian kondisi transaksi dilakukan dengan fixture di memori. Tidak ada transaksi uji baru atau perubahan profil pada database produksi.

Fondasi v5 tetap digunakan: keluarga produk, input jumlah, review checkout, filter pesanan, detail dokumen terkait, serta fokus dialog/menu. Masalah berikut adalah celah yang masih tersisa, bukan pengulangan pekerjaan yang sudah selesai.

| ID | Temuan | Bukti | Prioritas |
| --- | --- | --- | --- |
| B01 | Cari SKU P3 setelah memilih P6 tetap menampilkan dan menambahkan P6 | Direproduksi di browser produksi | P1 |
| B02 | Tanggal invoice dapat mendahului pesanan; tanggal penerimaan pengadaan dapat mendahului konfirmasi, persetujuan dana mendahului pengajuan | Terverifikasi melalui perintah domain di memori | P1 |
| B03 | Saat endpoint logout gagal 503, UI tetap pindah ke login dan dapat memberi kesan sesi sudah berakhir | Jalur kode dan uji fungsi dengan respons gagal; bukan insiden produksi yang diamati | P1 |
| B04 | Pembatalan seluruh pesanan setelah Surat Jalan dibatalkan dapat berlabel Terpenuhi; usulan pengganti tetap meminta tindakan setelah pembatalan | Perintah domain dan selector di memori | P2 |
| B05 | Kembali mengubah barang dari review checkout menghapus divisi, alamat, dan catatan | Direproduksi di browser produksi | P2 |
| B06 | Lebih dari 100 baris siap tagih membuat penerbitan invoice gabungan gagal; UI tidak menyediakan pilihan sebagian | Fixture 101 baris dan payload yang sama dengan UI | P2 |
| B07 | Opname lama tetap menunggu persetujuan ketika stok berubah; setelah opname pengganti selesai, dokumen lama masih tidak dapat diselesaikan | Terverifikasi melalui perintah domain di memori | P2 |
| B08 | Perubahan keranjang dari dua tab berpotensi saling menimpa | Risiko teridentifikasi dari kode; reproduksi dua tab wajib sebelum perubahan | P2 |
| B09 | Pilihan landing tetap melewati formulir login ketika sesi masih aktif | Teridentifikasi dari kode; konfirmasi browser sebelum perubahan | P2 |
| B10 | Pengajuan ulang periode menyisakan identitas pemberi persetujuan sebelumnya saat status kembali review | Perintah domain di memori | P3 |
| U01 | Pada 951–1000px, keranjang turun ke bawah katalog tetapi akses cepatnya disembunyikan | Produksi 980px: tombol checkout berada sekitar 4.624px di bawah bagian atas viewport saat pengukuran | P2 |
| U02 | Persediaan memuat 36 baris produk dan 36 batch tanpa pencarian; pada ponsel, stok tersedia berada di kolom luar layar | Produksi 390px: tabel 700/725px dalam ruang 333px | P2 |
| U03 | Tombol Buat pesanan bersaing dengan Buat pengadaan; laporan memuat terlalu banyak jenis pekerjaan dalam satu halaman panjang | Pengamatan browser; rekomendasi hierarki, bukan bug perhitungan | P2 |
| U04 | Refresh/retry belum memberi status sedang mencoba dan belum mencegah permintaan berulang | Kode; uji jaringan tertunda diperlukan | P3 |

P1 didahulukan karena menyangkut barang yang dipilih, urutan pencatatan, atau kejelasan berakhirnya sesi. P2 memperbaiki alur tersendat atau informasi menyesatkan. P3 diselesaikan setelah jalur utama stabil. Tingkat prioritas adalah penilaian audit, bukan skor dampak yang diukur pada pengguna.

## Urutan pelaksanaan

### Tahap 1 — Ketepatan transaksi dan sesi

Selesaikan B01–B04. Tambahkan tes regresi bermakna untuk tiap bug sebelum memperbaiki perilakunya.

- Pencarian SKU persis memilih varian, harga, dan stok yang sesuai; pencarian nama umum tetap mendukung pilihan kemasan.
- Tetapkan tanggal minimum dari dokumen sumber untuk peninjauan, reservasi, pengiriman, penerimaan, finalisasi, invoice, pengajuan dana, dan konfirmasi pengadaan. Simpan tanggal transisi yang memang diperlukan. Pencatatan mundur yang sah dalam periode terbuka tetap didukung.
- Logout menunggu hasil yang benar. Jika gagal, tampilkan kegagalan dan tindakan ulang tanpa mengaku pengguna sudah keluar. Atur penghapusan cookie dan pencabutan sesi secara eksplisit; buktikan perilaku saat penyimpanan sesi gagal.
- Pesanan yang dibatalkan tanpa penerimaan efektif masuk status Dibatalkan. Substitusi yang tidak lagi berlaku ditutup dengan alasan sebenarnya. Penerimaan parsial, riwayat Surat Jalan, dan jurnal yang sah tetap tercatat.

Selesai jika: rangkaian tanggal tidak sah ditolak tanpa efek samping, alur normal tetap berhasil, kasus 0 diterima/semua dibatalkan tidak berlabel Terpenuhi, dan uji logout sukses/gagal/jaringan terputus memberi hasil yang jujur serta aman.

### Tahap 2 — Pemesanan yang tidak menghapus pekerjaan pengguna

Selesaikan B05, B08, B09, U01, dan U04.

- Angkat draft checkout ke pemilik keranjang per akun. Ubah barang atau kembali antar langkah mempertahankan isian. Bersihkan draft sesudah berhasil atau melalui tindakan buang draft yang jelas.
- Buktikan skenario dua tab, kemudian sinkronkan perubahan keranjang dan tangani konflik tanpa diam-diam menimpa kuantitas terbaru. Bersihkan draft akun dengan benar setelah logout/checkout. Jangan menyimpan kata sandi atau data autentikasi di draft.
- Sesi valid meneruskan produk/koleksi dari landing; tujuan tetap dibatasi pada halaman yang diizinkan bagi peran tersebut.
- Samakan breakpoint keranjang dengan akses cepatnya. Sediakan akses keranjang yang dapat ditemukan pada setiap ukuran.
- Bedakan pemuatan awal, refresh data, percobaan ulang, kegagalan, dan berhasil. Cegah permintaan berulang yang tidak diperlukan dan pertahankan isian saat gagal.

Selesai jika: dua kali koreksi barang tidak mereset tujuan pengiriman; pencarian P3 benar; keranjang konsisten pada dua tab; sesi valid tidak diminta login ulang; tombol keranjang dapat dijangkau pada 950, 951, 980, 1000, dan 1001px; error jaringan tetap memiliki cara pemulihan yang jelas.

### Tahap 3 — Penagihan dan kendali operasional

Selesaikan B06, B07, dan B10.

- Invoice gabungan menyediakan pilihan penerimaan/baris dengan jumlah, nilai, dan batas maksimum yang terlihat. Jangan memotong 101 baris menjadi 100 secara diam-diam. Sisa belum ditagih tetap dapat diterbitkan berikutnya tanpa duplikasi.
- Opname yang usang dapat dinyatakan tidak berlaku/digantikan dengan jejak alasan. Penghitungan baru menjadi tindakan lanjutan yang jelas; jangan memaksa penyesuaian berdasarkan stok lama.
- Pengajuan ulang periode menghapus persetujuan aktif lama atau memberi riwayat versi yang jelas. Riwayat audit lama tetap tersedia; tampilan tidak boleh menyatakan versi baru sudah disetujui.

Selesai jika: fixture 101 baris dapat diselesaikan lewat UI; pengiriman tidak tertagih dua kali; opname usang tidak tertinggal sebagai pekerjaan yang mustahil disetujui; metadata persetujuan sesuai versi/status terbaru.

### Tahap 4 — UI operasional yang konsisten dan mudah dipindai

Selesaikan U02 dan U03, lalu terapkan konsistensi yang relevan pada pengiriman, invoice, pengadaan, laporan, periode, dan admin.

- Persediaan mendapat pencarian nama/SKU, filter sumber dan kondisi (perlu restok, kedaluwarsa/menjelang), jumlah hasil, serta navigasi Produk / Batch / Retur / Opname. Filter dan posisi dipertahankan saat kembali dari detail.
- Ponsel menampilkan stok tersedia, satuan, kondisi, dan tindakan bersama nama barang; rincian batch dibuka sesuai kebutuhan. Tabel desktop tetap tersedia untuk perbandingan data.
- Setiap halaman mempunyai satu tindakan utama yang sesuai: Buat pengadaan untuk pengadaan, tindakan laporan untuk laporan. Aksi global yang tidak terkait menjadi sekunder atau dipindahkan.
- Laporan dipisahkan ke bagian yang jelas: Penjualan, Keuangan, Jurnal, dan Aktivitas. Periode transaksi dan posisi saldo sampai tanggal tertentu harus diberi label yang berbeda. Ekspor yang tersedia harus menjelaskan cakupannya dan sesuai filter yang diklaim.
- Rapikan judul, jarak label/isian/error, alignment angka, area tombol, serta keadaan kosong/gagal di modul operasional dengan komponen bersama. Hindari halaman panjang yang hanya menumpuk semua panel.
- Nama petugas pada aktivitas hanya diperjelas jika kebijakan peran mengizinkan proyeksi nama yang aman. Jangan memperluas akses seluruh profil akun untuk menyelesaikan label Akun lain.

Selesai jika: pengguna dapat menemukan SKU atau barang restok langsung; ponsel menampilkan stok tersedia dan aksi tanpa menggeser seluruh tabel; tujuan tombol pengadaan tidak membingungkan; filter, judul, dan ekspor laporan menjelaskan cakupan data dengan benar.

## Arah visual dan penulisan

Pertahankan identitas yang sudah digunakan: petroleum `#073b45`, oranye `#ff6a2a`, teks `#142d33`, teks sekunder `#526773`, latar `#f5f7f9`, putih untuk permukaan. DM Sans tetap menjadi font kerja; Archivo Black terbatas pada headline landing. Tidak perlu tema baru untuk memperbaiki kasus-kasus ini.

Gunakan skala jarak 4/8/12/16/24/32/48px sebagai pedoman, padding panel desktop sekitar 24px dan ponsel 16–20px. Isi utama 15–16px; input ponsel 16px; teks kecil dipakai terbatas untuk metadata. Angka uang rata kanan dengan angka tabular, status memakai teks selain warna, target sentuh tindakan utama sekitar 44px. Nilai akhir harus diperiksa di browser, bukan dianggap baik hanya karena mengikuti token.

Susunan halaman: judul + konteks singkat + tindakan utama → pencarian/filter → hasil atau pekerjaan tertunda → detail pendukung. Foto membantu pemilihan produk dan landing; modul transaksi mengutamakan informasi. Setiap istilah harus sesuai tindakan: ajukan, setujui, catat penerimaan, terbitkan invoice, alokasikan pembayaran. Kurangi pengulangan penjelasan dan badge yang tidak membantu keputusan.

Contoh struktur persediaan:

```text
Persediaan                              [Tambah barang]
[Produk] [Batch] [Retur] [Opname · jumlah tertunda]
[Cari nama / SKU] [Sumber] [Kondisi]      jumlah hasil
Nama + satuan | Tersedia | Kondisi | Tindakan
```

## Tahap 5 — Verifikasi dan rilis

1. Jalankan seluruh tes yang ada dan tes regresi baru yang mencakup kasus di atas. Baseline rilis v5: 40 tes lulus; itu bukan bukti bahwa v6 telah diuji.
2. Periksa matriks akses sembilan peran dan isolasi dua PIC, termasuk URL langsung, perintah API, dokumen, dan ekspor yang tersentuh perubahan.
3. Uji alur lokal: pesan → tinjau → cadangkan → kirim parsial → terima → finalisasi → invoice → alokasi; serta pembatalan, substitusi, pengadaan, opname, dan pengajuan ulang periode.
4. Uji ponsel 320/390px, tablet 768/980px, desktop 1280/1440px, keyboard, fokus, dialog, nominal panjang, dan data kosong/padat. Periksa zoom 400% jika alat mendukung; sebutkan keterbatasan jika belum dapat diuji langsung.
5. Catat baseline ukuran aset dan waktu muat pada kondisi uji yang sama; perbaiki regresi atau bottleneck yang terbukti. Tidak menetapkan skor performa fiktif atau menjanjikan hasil lapangan tanpa pengukuran.
6. Typecheck, lint, dan build harus lolos tanpa error. Review perubahan gabungan, deploy ke Vercel, lalu smoke test publik login, akses peran, halaman/aset, dan tampilan utama. Pengujian transaksi yang mengubah data memakai lingkungan lokal/terisolasi.
7. Simpan catatan rilis, hasil tes, keterbatasan tersisa, dan URL deployment. Goal selesai hanya setelah kriteria penerimaan terpenuhi dan rilis terverifikasi.

## Pembagian kerja implementasi

- Agen transaksi: tanggal, pembatalan/substitusi, batas invoice, dan regresi domain.
- Agen pemesanan: pencarian SKU, draft checkout, keranjang antartab, kelanjutan login, breakpoint katalog.
- Agen operasional: persediaan, pengadaan/laporan, opname, konsistensi tampilan modul.
- Agen utama: koordinasi area berkas bersama, auth/logout, periode, integrasi, QA browser/RBAC, dan deployment. Tetapkan kepemilikan berkas terlebih dahulu agar perubahan `workspace.tsx`, engine, dan CSS tidak saling menimpa.

## Dasar dan batas rekomendasi

Pola pencarian/filter dan rincian bertahap mengikuti [Carbon Data table](https://carbondesignsystem.com/components/data-table/usage/). Penempatan serta hubungan pesan kesalahan ke isian merujuk [GOV.UK Error summary](https://design-system.service.gov.uk/components/error-summary/). Pengujian lebar 320px dan pembesaran mengacu [W3C Reflow](https://www.w3.org/WAI/WCAG22/Understanding/reflow.html); tabel memiliki pengecualian tata letak dua dimensi, sehingga scroll tabel saja bukan bukti pelanggaran.

Ini audit ahli dan pengujian teknis terbatas, bukan studi dengan pengguna, sertifikasi aksesibilitas, atau pengukuran kinerja lapangan. Temuan berbasis kode harus direproduksi sebelum diperlakukan sebagai bug terverifikasi. Implementasi tidak mencakup pembayaran sungguhan, integrasi bank, atau perubahan ruang lingkup demo capstone.

Laporan pendukung: [browser](audit-v6-browser.md), [katalog dan visual](audit-v6-visual-catalog.md), [alur transaksi](audit-v6-workflows.md), [kualitas operasional/auth](audit-v6-quality.md).
