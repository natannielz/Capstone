# Riset UI/UX Unit Toko BNI — prioritas v5

17 September 2026 · Website yang diperiksa: https://unit-toko-bni.vercel.app · Status: rekomendasi, belum diimplementasikan.

## Kesimpulan

Ada ruang perbaikan yang jelas. Fondasi visual v4 sudah cukup konsisten; peningkatan berikutnya sebaiknya memperjelas pekerjaan yang harus dilakukan, mempertahankan konteks saat berpindah halaman, dan menjelaskan barang yang benar-benar dipesan. Karakter visual dapat diperkuat melalui fotografi kegiatan kantor yang sesuai dengan katalog serta hierarki informasi barang yang lebih rapi.

Pertahankan warna petroleum, aksen oranye terbatas, DM Sans, dan Archivo Black pada headline landing. Jangan menambah slogan, badge, foto berulang, animasi, atau kartu statistik tanpa kebutuhan pengguna. Perubahan tipografi dan spacing berikutnya harus menyelesaikan kasus nyata, seperti pencarian mobile yang masih dibatasi 180px oleh stylesheet lama.

## Metode dan batas bukti

Tiga agen bekerja paralel: desain visual/katalog, alur pengguna, dan aksesibilitas/responsivitas. Pemeriksaan mencakup kode aplikasi, aset foto, catatan v4, serta referensi primer Baymard, NN/g, Carbon, GOV.UK, W3C/WAI, dan Radix. Agen utama memeriksa website produksi dengan akun demo Kepala Toko, desktop 1280px dan mobile 390px, termasuk navigasi keyboard.

Ini audit ahli, bukan penelitian dengan pengguna atau sertifikasi WCAG. Temuan diberi dasar **browser**, **kode**, atau **hipotesis yang perlu diuji**. Tidak ada transaksi, perubahan profil, perubahan aplikasi, atau deployment. Ukuran layar dikembalikan dan browser dikembalikan ke profil setelah pemeriksaan.

## Prioritas gabungan

P1: hambatan pada alur utama atau penggunaan keyboard. P2: peningkatan kejelasan dan kenyamanan. Usaha kecil berarti komponen terfokus; sedang berarti melibatkan beberapa komponen atau alur. Urutan ini merupakan penilaian audit, bukan hasil pengukuran dampak pengguna.

| Urutan | Temuan dan bukti saat ini | Perbaikan yang disarankan | Prioritas / usaha |
| --- | --- | --- | --- |
| 1 | **Antrean kehilangan konteks.** Di browser, “Menunggu tinjauan 2” membuka “Semua pesanan 5”. Pencarian `0004` hilang setelah detail dibuka lalu kembali. | Bawa filter dari dashboard; tampilkan filter aktif, jumlah hasil, dan opsi hapus. Pertahankan pencarian, filter, dan posisi daftar saat kembali. | P1 / sedang |
| 2 | **Interaksi keyboard terputus.** Di mobile, membuka navigasi tidak memindahkan fokus dan Escape tidak menutupnya. Menutup dialog peninjauan menaruh fokus pada BODY. Fokus pencarian tidak mempunyai outline/shadow pengganti. | Kelola fokus drawer dan dialog; kembalikan ke pemicu atau tujuan lanjutan. Pulihkan indikator fokus pencarian serta state buka/tutup yang dapat dibaca teknologi bantu. | P1 / kecil–sedang |
| 3 | **Status dan aksi pesanan sulit dipindai di ponsel.** Pada 390px, tabel selebar sekitar 554px berada dalam ruang 333px; status terpotong dan aksi memerlukan geser horizontal. | Tampilkan daftar pesanan mobile dengan nomor/divisi, status, tanggal, nilai, dan tombol detail dalam satu blok. Tabel desktop tetap tersedia. Detail panjang dibuka sesuai kebutuhan. | P1 / sedang |
| 4 | **Jumlah dan kemasan kurang praktis untuk pembelian divisi.** Kode hanya memberi −/+; dari 1 ke 50 membutuhkan 49 kali tambah. Foto air menampilkan satu botol, tetapi satuan pesannya dus tanpa informasi isi. | Isian jumlah langsung, satuan yang jelas, subtotal langsung berubah, keterangan isi kemasan, dan ringkasan seluruh barang sebelum pengajuan. Data isi kemasan harus disepakati, bukan ditebak. | P1 / sedang |
| 5 | **Katalog dan landing belum tersambung baik.** Kode berisi 12 produk dasar × tiga pilihan kemasan menjadi 36 kartu dengan foto berulang. Pilihan produk/kebutuhan landing menuju login tanpa membawa pilihan. | Kelompokkan keluarga produk dengan pilihan kemasan; harga/stok/SKU tetap terpisah. Teruskan koleksi atau produk setelah login sesuai hak akses. | P2 / sedang |
| 6 | **Foto kurang mewakili beberapa pilihan.** Audit aset menemukan foto Merchandise mengulang masthead dan foto Pantry menonjolkan kue basah, sementara barang pilihannya kopi, teh, dan biskuit. | Kurasi satu adegan yang tepat untuk setiap kebutuhan, dengan pencahayaan dan komposisi konsisten. Foto harus menjelaskan kategori serta kemasan. | P2 / kecil–sedang |
| 7 | **Detail pesanan belum menghubungkan seluruh tindak lanjut.** Kode PIC memisahkan penerimaan, invoice, dan pembayaran pada menu berbeda. Struktur detail tanpa invoice terkait juga terlihat pada Kepala Toko. | Jadikan detail pesanan titik temu barang, pengiriman, tindakan PIC, dan invoice terkait. Bedakan status pemenuhan dari status pembayaran; dukung pengiriman parsial. | P2 / sedang |
| 8 | **Error dan perubahan form belum cukup terlindungi.** Dari kode, error profil hanya berupa toast, beberapa error layanan login ikut menandai input invalid, dan perubahan profil hilang saat pindah menu. | Pesan menetap di field atau ringkasan yang tepat, isian tetap tersimpan ketika gagal, status perubahan belum disimpan, dan pilihan tetap mengedit/buang perubahan. | P2 / sedang |

Tabel mobile yang bergeser secara horizontal tidak otomatis melanggar WCAG; temuan nomor 3 menilai kemudahan membaca status dan melakukan tindakan. Risiko keranjang fixed pada lebar 320px/zoom 400% masih perlu direproduksi dan tidak dicatat sebagai kegagalan yang sudah terbukti.

## Bentuk pengalaman yang dituju

**Dashboard operasional:** pekerjaan yang memerlukan tindakan mendapat urutan teratas, memakai jumlah dan status yang sama dengan daftar tujuan. Statistik pendukung tetap ringkas. Contoh: klik “Menunggu tinjauan · 2” membuka dua pesanan dengan filter “Menunggu tinjauan” terlihat; selesai meninjau satu pesanan, pengguna kembali ke daftar yang sama.

**Kartu produk:** foto yang sesuai → nama barang → pilihan dan isi kemasan → harga per satuan pesan → ketersediaan → jumlah dan tindakan tambah. OMI/Smart dan SKU menjadi informasi sekunder. Penggabungan kartu tidak boleh menggabungkan stok atau harga varian.

**Detail pesanan:** tampilkan ringkasan pemenuhan dan “Perlu tindakan Anda” jika relevan, kemudian barang, pengiriman, dan tagihan terkait. Hindari satu timeline lurus yang memberi kesan semua pengiriman dan pembayaran selalu selesai bersamaan.

**Landing:** pertahankan komposisi dan tipografi yang sudah ada, tetapi setiap foto dan tautan kebutuhan harus sesuai dengan isi katalog. Pilihan “Rapat” dapat menjadi koleksi lintas kategori, bukan memaksa kategori katalog diubah. Login melanjutkan pilihan hanya jika peran tersebut memiliki akses.

## Rencana pelaksanaan yang disarankan

1. **Fondasi interaksi:** filter antrean, pelestarian konteks daftar, fokus keyboard/drawer/dialog, pencarian mobile, dan penyajian pesanan di ponsel. Selesaikan sebelum menambah dekorasi.
2. **Pemesanan dan kesinambungan transaksi:** jumlah langsung, ringkasan pengajuan, metadata kemasan, detail pesanan terhubung, pesan form dan perlindungan isian.
3. **Penyempurnaan katalog/landing:** keluarga produk, pilihan yang berlanjut setelah login, kurasi/generasi foto yang sesuai. Pertimbangkan halaman alokasi pembayaran multi-invoice dengan preview saldo untuk peran Penagihan; engine sudah mendukung beberapa baris, tetapi UI saat ini hanya satu invoice per dialog.

Seluruh perubahan mempertahankan otorisasi server, batas divisi, dan aturan urutan transaksi. Tautan langsung, filter URL, dan dokumen terkait wajib diuji dengan akun yang berhak maupun yang tidak berhak. Peningkatan UI tidak boleh dianggap sebagai bukti RBAC tanpa pemeriksaan tersebut.

## Kriteria penerimaan terukur

- Lima pesanan, dua menunggu tinjauan: kartu Kepala membuka tepat dua hasil; pencarian `0004` tetap ada setelah buka detail dan kembali.
- Pada 390px, status dan tombol detail setiap pesanan terbaca tanpa menggeser halaman ke samping. Input pencarian menggunakan lebar ruang yang tersedia.
- Menu mobile dapat dibuka, dijelajahi, dan ditutup dengan keyboard. Setelah Escape/Batal, fokus kembali ke pemicu atau tujuan yang logis. Kedua kolom pencarian memiliki fokus yang terlihat.
- Mengetik jumlah 50 langsung memperbarui subtotal; review menampilkan barang, kemasan, jumlah, tanggal, alamat, dan total. Kekurangan stok dijelaskan sesuai dukungan pengiriman susulan, bukan otomatis menolak pesanan.
- Mengubah pilihan kemasan memperbarui harga dan stok SKU yang benar. Kembali dari login mempertahankan pilihan produk/koleksi yang diizinkan.
- Pesan kesalahan form tetap dapat ditemukan setelah 10 detik; kegagalan jaringan dibedakan dari input salah; meninggalkan profil yang berubah tidak menghilangkan isian tanpa pilihan pengguna.
- Uji tambahan 320px dan zoom 400% memeriksa keranjang, nominal besar, tombol, serta fokus yang berpotensi tertutup footer. Belum dijalankan dalam audit ini.

Lanjutkan dengan pengujian tugas bersama beberapa perwakilan PIC, Toko, dan Penagihan: memesan kebutuhan dalam jumlah besar, meninjau antrean, melacak pengiriman parsial, dan membagi pembayaran. Catat keberhasilan tanpa bantuan, salah pilih, pengulangan langkah, serta waktu penyelesaian sebagai baseline dan pembanding. Belum ada klaim persentase peningkatan.

## Dasar riset

- [Carbon — Data table](https://carbondesignsystem.com/components/data-table/usage/): pencarian, filter, pengurutan, ukuran baris, dan detail yang dibuka bertahap. Menjadi dasar pola toolbar; keputusan daftar mobile merupakan adaptasi untuk tugas Unit Toko.
- [Baymard — Quantity changes](https://baymard.com/research-articles/auto-update-users-quantity-changes): kombinasi isian jumlah dan tombol serta pembaruan total tanpa langkah Update terpisah.
- [Baymard — Product listing information](https://baymard.com/research-articles/product-listing-information) dan [Combine variations](https://baymard.com/blog/combine-variations-one-list-item): atribut keputusan pada kartu dan pengelompokan variasi. Adaptasi ke paket/dus perlu pengujian dengan pengguna divisi.
- [NN/g — Information scent](https://www.nngroup.com/articles/information-scent/): kesesuaian label, gambar, konteks, dan tujuan tautan.
- [GOV.UK — Check answers](https://design-system.service.gov.uk/patterns/check-answers/): pemeriksaan dan koreksi data sebelum dikirim. [Error summary](https://design-system.service.gov.uk/components/error-summary/): ringkasan error, hubungan ke field, dan fokus yang membantu perbaikan.
- [WAI APG — Modal dialog](https://www.w3.org/WAI/ARIA/apg/patterns/dialog-modal/) dan [WCAG — Focus Visible](https://www.w3.org/WAI/WCAG22/Understanding/focus-visible.html): perilaku fokus, Escape, pemulihan fokus, dan indikator keyboard.

## Laporan pendukung

- [Desain visual dan katalog](research-visual-v5.md)
- [Alur pengguna, bukti kode, dan acceptance criteria](research-flows-v5.md)
- [Aksesibilitas dan responsivitas](research-accessibility-v5.md)
