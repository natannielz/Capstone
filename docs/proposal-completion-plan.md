# Rencana Penyempurnaan Proposal Capstone Kelompok 16

Tanggal peninjauan 4 Oktober 2026

## Dasar peninjauan

Seluruh teks proposal SharePoint telah dibaca dari salinan dokumen 28 halaman. Bab I dan II berisi materi; abstrak serta Bab III sampai VII belum diisi. Judul, objek Koperasi Swadharma, metode Waterfall, dan struktur tujuh bab dipertahankan.

## Koreksi materi yang sudah ada

- Perbaiki manfaat bagi perusahaan yang mengulang paragraf manfaat mahasiswa.
- Selaraskan label Material dan Management dengan uraian fishbone.
- Perbaiki Next.js yang salah disebut Nest.js, Unified Modeling Language, dan salah ketik.
- Perkuat argumentasi pemilihan alternatif dengan perbandingan kriteria tanpa mengarang skor hasil wawancara.
- Jelaskan integrasi informasi OMI dan Smart sebagai kebutuhan; akses API, migrasi, dan izin integrasi belum dapat dianggap tersedia.
- Verifikasi referensi ilmiah dan gunakan dokumentasi resmi untuk definisi teknologi serta standar.
- Rapikan sampul, hierarki judul, tabel, gambar, daftar isi, daftar gambar, dan daftar tabel.

## Materi pelengkap

1. Abstrak proposal yang merangkum masalah, pendekatan, keluaran, dan rencana validasi.
2. Bab III: batasan realistis, standar, kebutuhan fungsional dan nonfungsional, peran, asumsi, tahap Waterfall dan kriteria penerimaan.
3. Bab IV: rancangan alur informasi, arsitektur Next.js dan C# ASP.NET Core serta MongoDB, struktur data, bukti antarmuka aktual, matriks keterlacakan dan batas implementasi.
4. Bab V: komponen biaya, estimasi berbasis asumsi, skenario manfaat, kelayakan dan implementasi bertahap.
5. Bab VI: prosedur black box dan UAT, indikator terukur, validasi pengguna, serta evaluasi. Status rencana dibedakan dari hasil aktual; subbab 6.3 Penilaian Rekan dikosongkan sesuai arahan terbaru pengguna.
6. Bab VII: kesimpulan rancangan yang menjawab rumusan masalah, keterbatasan dan saran operasional.

## Visualisasi

Gunakan diagram deterministik untuk alur proses, arsitektur dan mekanisme Waterfall. Gunakan tangkapan layar antarmuka aktual dari aplikasi di workspace atau frontend yang tersedia, dengan penanda data sintetis dan status prototipe. Hindari gambar generatif untuk bukti implementasi atau hasil pengujian.

## Pemeriksaan akhir

Pastikan semua bab terisi kecuali isi Penilaian Rekan, tidak ada hasil empiris rekaan, kutipan sesuai daftar pustaka, tabel dan diagram terbaca, penomoran konsisten serta perubahan tersimpan pada dokumen SharePoint melalui computer use. Identitas anggota yang tidak tersedia tidak ditebak. Dua diagram asli Bab I dan II dipertahankan; visual tambahan diberi nomor terpisah.

## Hasil sebelum tambahan review

Proposal pada tautan SharePoint telah dilengkapi melalui Word Online. Abstrak serta Bab III sampai VII terisi, dengan isi subbab 6.3 Penilaian Rekan kosong; Bab I dan II diselaraskan dengan sumber ilmiah, struktur codebase, dan batas bukti yang tersedia. Setelah arahan suara berikutnya, fishbone tambahan dihapus beserta caption, penjelasan, dan entri daftar gambar; hanya fishbone asli tim yang dipertahankan. Dokumen cloud berisi 38 halaman, 9 visual, dan 15 tabel isi. Tiga daftar menggunakan tabel tanpa garis; nomor halaman dicocokkan dengan ekspor yang tersedia dan rujukan subbab 1.2.2 disesuaikan menjadi halaman 9.

Visual meliputi dua gambar asli tim, pemetaan IS2020 tambahan, Waterfall, alur informasi, arsitektur, model konseptual data, dan dua tangkapan layar frontend yang dijalankan dari codebase lokal. Gambar asli dipulihkan dari proposal awal dengan SHA256 yang tetap identik: fishbone `ce0faee13ed0b45f1e8010d5dc495f67e6e9aaee26656aa40dddab766a9a6241` dan BoK `7b8f495dce281a291c5fa730a0b1b02819502089e0208bc1877cef93268bc763`. Pengguna meminta tidak mengunduh salinan lagi; ekspor yang tersimpan di `docs/output` merupakan snapshot sebelum revisi suara terakhir. Dokumen cloud merupakan hasil terkini. Bukti visual cloud diperbarui pada `docs/output/Proposal_SharePoint_Proof.png`.

Biaya, jadwal, target kinerja, dan kriteria UAT diberi status asumsi atau usulan. Evaluasi dan validasi berisi rencana pengujian; hasil aktual belum diisi. Tidak ada klaim bahwa wawancara, UAT organisasi, benchmark produksi, atau penilaian anggota telah dijalankan. Bukti pilot lokal dikutip dari dokumentasi rilis internal dan tidak dijalankan ulang selama penyusunan proposal.

## Tinjauan kelengkapan setelah pertanyaan pengguna

Rubrik dosen belum tersedia. Tinjauan menilai kesesuaian struktur dengan template awal dan konsistensi akademik, bukan menyatakan kepatuhan terhadap aturan kampus yang belum diketahui. Pemeriksaan memakai dokumen cloud serta salinan yang sudah tersedia; tidak ada unduhan baru setelah larangan pengguna.

- Bab III perlu mekanisme pengumpulan data yang lebih rinci, kriteria penerimaan fungsi prioritas, dan penegasan luaran semester antara pilot awal dan target sistem lengkap.
- Bab IV belum cukup menggambarkan interaksi utama: screenshot katalog memperlihatkan layanan belum tersedia. Diperlukan rancangan atau bukti layar pengajuan permintaan, keputusan petugas, dan status/riwayat. Model data masih konseptual; atribut kunci, relasi, dan aturan perlu diperinci. Matriks verifikasi belum mencakup N02, N03, dan N06.
- Bab V benar secara aritmetika: nilai tenaga Rp13.500.000, kontingensi Rp2.025.000, kebutuhan awal Rp15.525.000, operasi tahunan Rp8.600.000, tahun pertama Rp24.125.000. Dasar 270 jam dan Rp50.000/jam, pembagian per peran/tahap, cakupan biaya, dan biaya yang belum masuk memerlukan penjelasan lebih terarah. Seluruh angka tetap simulasi.
- Bab VI memisahkan rencana dari hasil aktual dengan baik. Instrumen perlu keterlacakan ke F/N, prasyarat, data uji konkret, serta rencana sampel dan baseline sebelum pelaksanaan.
- Bab VII perlu konsisten sebagai kesimpulan sementara rancangan; kalimat yang menyebut kesesuaian pengguna terjawab melalui UAT tidak boleh dibaca sebagai bukti validasi yang sudah terjadi.

Temuan di atas telah ditindaklanjuti setelah pengguna meminta seluruh tambahan dimasukkan ke dokumen cloud. Isi Penilaian Rekan tetap kosong.

## Penyelesaian tambahan review di dokumen cloud

Semua tambahan disimpan langsung melalui Word Online pada dokumen SharePoint yang sama. Tidak ada unduhan DOCX atau PDF baru. Tampilan final yang diperiksa berisi 43 halaman, 57 entri daftar isi, 12 gambar, 21 tabel isi, dan 13 referensi. Daftar isi, daftar gambar, serta daftar tabel diperbarui menggunakan halaman yang dibaca dari dokumen cloud. Tiga tabel indeks tanpa garis tetap terpisah dari tabel isi.

- Bab III: batas pilot F01–F03 dan roadmap F04–F09 diperjelas; kriteria penerimaan, rencana observasi / wawancara / telaah dokumen, serta konfirmasi kebutuhan ditambahkan.
- Bab IV: kamus data logis dengan atribut, kunci, kardinalitas dan aturan status ditambahkan. Tiga wireframe baru menjelaskan pengajuan, keputusan petugas, serta status / riwayat menggunakan data sintetis. Screenshot katalog lama dinyatakan sebagai keadaan layanan belum tersedia. Matriks keterlacakan sekarang mencakup seluruh F01–F09 dan N01–N07.
- Bab V: 270 jam dijelaskan sebagai total usaha pilot sejak awal, dengan alokasi jam menurut peran dan tahap. Tarif tetap asumsi valuasi waktu. Komponen yang belum termasuk serta pemicu perubahan lingkup dijelaskan. Aritmetika tahun pertama tetap Rp24.125.000.
- Bab VI: prasyarat dan fixture konkret, 14 skenario yang terhubung ke kebutuhan, format laporan eksekusi, rencana UAT empat peserta dan 24 percobaan per kondisi, baseline yang setara, serta cakupan indikator ditambahkan. Semua angka merupakan usulan. Hasil aktual dan penilaian tetap belum diisi.
- Bab VII: kesimpulan dinyatakan sementara berdasarkan rancangan; instrumen UAT tidak dianggap bukti penerimaan pengguna.
- Waterfall diganti atas permintaan pengguna dengan diagram bertingkat enam tahap: analisis, desain, implementasi, verifikasi, validasi / penerapan, serta operasi / pemeliharaan. Tabel tahapan diselaraskan dan referensi IBM Developer (Gallagher, Dunleavy, dan Reeves, 2019) ditambahkan. Diagram baru ini menggantikan diagram Waterfall buatan asisten, bukan diagram asli teman.

Nomor tabel disusun kembali sesuai urutan kemunculan, dan rujukan di paragraf serta matriks diperbarui. Caption gambar diperiksa bersama gambar; diagram kompetensi tambahan dipindahkan ke awal halaman agar caption tidak terpisah. Dua gambar asli tim tidak diganti. Fishbone tambahan tetap tidak ada.

Verifikasi dilakukan lagi setelah dokumen dibuka ulang: status Saved, semua judul dan tabel tambahan tersedia, tiga caption wireframe tersedia, sumber Waterfall baru tersedia, total biaya konsisten, matriks mencakup N01–N07, serta isi antara subbab 6.3 dan Bab VII kosong kecuali pergantian halaman. Bukti diagram Waterfall yang tersimpan di cloud: `docs/output/Proposal_Waterfall_Cloud_Proof.png`. Dokumen cloud menjadi hasil terkini; file DOCX / PDF lama tetap snapshot historis.

Yang masih memerlukan input atau kegiatan nyata adalah rubrik dosen, konfirmasi pemilik proses dan aturan organisasi, penawaran layanan aktual, baseline / hasil uji lapangan, keputusan penerimaan, serta Penilaian Rekan oleh tim. Hal tersebut tidak diisi dengan hasil rekaan dan tidak dianggap sebagai kekurangan yang dapat dituntaskan lewat penulisan saja.
