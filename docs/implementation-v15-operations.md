# V15 — pengadaan dan administrasi

## Rencana sebelum implementasi

Halaman ini dipakai petugas untuk menemukan dokumen atau akun dan melanjutkan tindakan yang tepat. Gunakan register operasional: pencarian berlabel, filter ringkas, jumlah hasil, dan paginasi; pertahankan semua formulir dan aturan peran.

- Warna mengikuti workspace: petrol `#103F47`, putih `#FFFFFF`, sage `#E7EEEB`, garis `#CFDADF`, teks sekunder `#52656A`, dan aksen hangat `#AD431E`. Pertahankan keluarga huruf workspace, judul sans yang tegas, teks kontrol yang mudah dibaca, angka tabular. Tidak menambah dekorasi atau gerakan saat orang mengisi formulir.
- Pengadaan: bilah pencarian nomor/pemasok/barang di atas kartu dokumen; status terpisah; maksimal enam dokumen per halaman. Tindakan dan rincian keuangan tetap utuh.
- Administrasi: navigasi langsung **Akun / Divisi / Pemasok** dengan jumlah nyata. Cari akun berdasarkan nama, email, peran, atau divisi; filter peran dan status; maksimal dua belas akun per halaman. Navigasi menargetkan judul bagian yang dapat menerima fokus.
- Periode kosong: jelaskan urutan kerja sesuai peran dan beri langkah berikutnya ke pengajuan atau laporan. Tabel memakai caption, `scope="col"`, dan kolom angka rata kanan.
- Pada layar sempit, kontrol membungkus ke baris baru; tabel tetap dapat digeser di dalam panel. Tombol cari, hapus filter, dan ganti halaman mempunyai label eksplisit.

Sketsa: `judul + tindakan → cari | status → jumlah hasil → dokumen/tabel → halaman`. Ciri yang dipertahankan adalah dokumen pengadaan dengan nomor, jalur pemasok, penerimaan, dan posisi pembayaran; bukan kartu metrik dekoratif. Kritik rencana: navigasi admin membawa pengguna langsung ke pekerjaan, sehingga tidak perlu menambah dashboard statistik kedua.

## Kontrak URL

- Pengadaan: `purchaseQ`, `purchaseStatus=all|open|draft|ordered|complete|closed`, `purchasePage`.
- Akun: `accountQ`, `accountRole=all|<peran>`, `accountStatus=all|active|inactive`, `accountPage`.
- Bagian admin: `adminSection=users|divisions|suppliers`; target `admin-users`, `admin-divisions`, `admin-suppliers`.
- Pencarian/filter mengatur ulang halaman daftar terkait saja. Nilai URL tidak valid memakai pilihan aman. Halaman di luar rentang dijepit ke halaman terakhir, tanpa menghilangkan hasil.

## Validasi yang direncanakan

Uji selector mencakup pencarian relasi pemasok/barang, status terbuka, akun menurut peran/status/divisi, URL tidak valid, paginasi setelah penyaringan, dan tidak mengubah data sumber. Lint terarah dan pemeriksaan tipe setelah integrasi. Tinjauan browser, build, dan publikasi ditangani agen utama.

## Hasil implementasi dan pemeriksaan

- Kontrak URL di atas sudah diterapkan dengan props opsional yang sama seperti halaman Laporan. Workspace meneruskan query melalui integrasi agen workspace.
- Pengadaan mempertahankan urutan dokumen terbaru dari daftar sumber, rincian keuangan, serta semua formulir dan pembatasan peran sebelumnya. Pencarian juga menemukan SKU dan nomor pesanan divisi yang terkait.
- Akun mempertahankan aturan akun sendiri, batas pemisahan customer/staf, dan dialog reset sandi. Navigasi bagian memakai tautan nyata, mendukung buka tab baru, serta mengarahkan fokus judul setelah navigasi workspace selesai memulihkan posisi.
- Semua tabel pada komponen operations mendapat caption dan scope kolom; kuantitas pengadaan dan versi laporan memakai format kolom angka. Periode tanpa data menjelaskan langkah berikutnya sesuai peran; divisi/pemasok kosong mengarahkan ke tindakan tambah yang tersedia.
- Kontrol pencarian, filter, dan halaman memiliki tinggi sentuh minimal 44 px. Label unggah dan nama aksesibel lampiran memakai istilah Indonesia (pembayaran/penerimaan/biaya), dengan petunjuk tipe dan ukuran file yang terhubung ke input; nilai scope API tetap sama.
- Enam tes selector terarah lulus. Lint ketiga berkas TypeScript yang diubah dan pemeriksaan tipe aplikasi lulus. Bundler tes membutuhkan akses filesystem normal karena pemeriksaan direktori leluhur diblokir sandbox; pengulangan lokal berhasil.
- Belum mengklaim pemeriksaan visual/browser atau publikasi dari subtask ini. Agen utama mengimpor CSS dan memeriksa integrasi akhir.
