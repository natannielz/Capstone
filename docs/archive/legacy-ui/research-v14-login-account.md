# Riset v14 — akses, profil, dan pesanan pelanggan

23 September 2026. Dokumen ini adalah audit sumber dan rencana perbaikan, bukan laporan implementasi atau hasil uji visual browser. Ruang lingkup: kedua portal masuk, profil bersama, daftar/detail pesanan pelanggan, konfirmasi penerimaan, tagihan, dan lampiran. Pemeriksaan browser dan integrasi dilakukan agen utama.

## Kesimpulan dan prioritas

Login pelanggan dan staf v13 sudah memiliki komposisi berbeda. Perubahan berikutnya sebaiknya mempertahankan perbedaan itu sambil memperbaiki pemulihan kesalahan. Kekurangan paling nyata justru ada setelah masuk: tindakan penting tersebar jauh dalam detail pesanan, daftar kehilangan konteks ketika kembali dari detail, serta permintaan profil memakai penanganan jaringan yang berbeda dari bagian aplikasi lainnya.

Lima perubahan yang dapat segera dikerjakan:

1. Samakan penanganan permintaan profil dengan helper jaringan aplikasi: batas waktu, status HTTP, penanganan sesi kedaluwarsa, dan tombol yang selalu keluar dari keadaan sibuk.
2. Buat ringkasan **Perlu tindakan Anda** berdasarkan kondisi transaksi nyata, dengan tautan langsung ke penggantian barang, penerimaan, atau tagihan yang memerlukan perhatian. Selaraskan filter daftar dengan definisi ini.
3. Pertahankan pencarian, filter, dan halaman daftar ketika membuka lalu kembali dari detail; benahi kontrol pagination yang tampak nonaktif tetapi masih berupa tautan aktif.
4. Rapikan detail menjadi ringkasan pesanan → tindakan → barang/pengiriman/tagihan → dokumen; jelaskan nilai pesanan ini dibanding total tagihan gabungan. Urutkan peristiwa yang memang memiliki tanggal, tanpa mengarang pelacakan atau estimasi pengiriman.
5. Tambahkan ringkasan sebelum menyimpan penerimaan sebagian, serta umpan balik yang jelas setelah unggahan dan penggantian kata sandi. Perhalus tampilan mobile, panjang teks, dan fokus keyboard berdasarkan hasil uji nyata.

## Sumber primer dan penerapannya

Semua sumber berikut dibaca untuk riset ini. Rekomendasi adalah adaptasi untuk aplikasi Unit Toko, bukan klaim bahwa aplikasi harus meniru keseluruhan produk atau desain sumber.

| Sumber resmi | Temuan yang didukung | Penerapan yang disarankan |
| --- | --- | --- |
| [W3C WAI — User Notifications](https://www.w3.org/WAI/tutorials/forms/notifications/) | Kesalahan perlu menyatakan masalah dan cara memperbaikinya; keberhasilan perlu dikonfirmasi. Notifikasi dinamis harus dapat diumumkan teknologi bantu. | Pertahankan `FieldError` yang sudah memakai `role=alert`. Kaitkan bantuan/kesalahan unggahan dengan input dan beri konfirmasi unggahan yang singkat. Jangan menyebut semua error saat ini tidak aksesibel. |
| [web.dev — Sign-in form best practices](https://web.dev/articles/sign-in-form-best-practices) | Label, nama input yang stabil, autocomplete yang sesuai, pengungkapan kata sandi, dan keterjangkauan form pada mobile membantu proses masuk. | Pertahankan label dan autocomplete yang sudah benar. Lengkapi kontrol tampilkan kata sandi di profil, serta periksa form ketika keyboard mobile terbuka. |
| [GOV.UK — Error summary](https://design-system.service.gov.uk/components/error-summary/) | Pola GOV.UK mengarahkan fokus ke ringkasan kesalahan yang menaut ke kolom bermasalah dan konsisten dengan pesan inline. | Terapkan pada dialog penerimaan yang panjang bila ada beberapa kesalahan. Ini pola desain yang direkomendasikan, bukan alasan mengganti seluruh login dua kolom yang sudah memiliki alert. |
| [GOV.UK — Task list](https://design-system.service.gov.uk/components/task-list/) | Nama tugas dan status yang ringkas membantu pengguna memilih tugas yang dapat dikerjakan. Panduannya tidak menyarankan pola ini untuk setiap urutan linear. | Adaptasi menjadi daftar tindakan transaksi yang independen. Jangan menyulap alur pesanan menjadi checklist wajib atau progres persentase yang tidak bersumber dari data. |
| [GOV.UK — Check answers](https://design-system.service.gov.uk/patterns/check-answers/) | Ringkasan sebelum konfirmasi dapat membantu pengguna memeriksa transaksi dan kembali memperbaiki bagian tertentu. | Gunakan untuk jumlah diterima/selisih dan keputusan substitusi dengan perubahan harga. Hindari menambah satu halaman review untuk setiap edit profil sederhana. |
| [Shopify — Order status page](https://help.shopify.com/en/manual/fulfillment/setup/order-status-page) | Halaman status memusatkan perkembangan pesanan dan pengiriman. Pelacakan langsung bergantung pada data pelacakan dan dukungan pengangkut. | Satukan informasi Unit Toko yang benar-benar tersedia. Kurir internal dan tanggal transaksi tidak cukup untuk menampilkan peta langsung, ETA, atau nomor resi fiktif. |
| [W3C WCAG 2.2 — Reflow](https://www.w3.org/WAI/WCAG22/Understanding/reflow.html) | Konten biasa perlu dapat digunakan pada lebar setara 320 CSS px tanpa kehilangan informasi atau fungsi; terdapat pengecualian untuk konten yang membutuhkan dua dimensi. | Uji nama panjang, nilai rupiah, nama berkas, dialog, dan pembesaran teks. Menyembunyikan overflow bukan bukti reflow berhasil. |

## Fakta yang sudah baik dan harus dipertahankan

- Portal berbeda bukan hanya warna: pelanggan memiliki foto produk dan komposisi editorial, staf memiliki panel operasional dan diagram proses konseptual. Form tetap berada di dokumen; animasi bukan prasyarat interaksi.
- `/api/auth/login` memvalidasi portal dan peran di server. `safeLoginDestination` menyaring tujuan lokal serta akses halaman menurut peran. Jangan menggantinya dengan redirect dari parameter URL mentah.
- Login sudah memiliki label eksplisit, autocomplete, tampilkan kata sandi, status sibuk, pencegahan kirim ganda, dan pesan kesalahan yang diumumkan.
- Detail pesanan sudah menyediakan substitusi, penerimaan sebagian, komplain, lampiran, tagihan, pembayaran simulasi, dan status verifikasi. Ini harus ditata lebih jelas, bukan dianggap belum ada.
- Dialog tindakan menyimpan identitas perintah untuk mencoba kembali ketika hasil tidak pasti. Pesan kondisi ini sudah menjelaskan mengapa input dikunci; pertahankan perlindungan terhadap transaksi ganda.
- Profil sudah memvalidasi kolom, memfokuskan kesalahan tertentu, memperingatkan perubahan yang belum disimpan, dan menjelaskan bahwa penggantian kata sandi mengakhiri sesi.

## Temuan dari kode

Nomor baris adalah posisi saat audit dan dapat bergeser setelah implementasi.

| Prioritas | Bukti | Dampak dan perbaikan |
| --- | --- | --- |
| Tinggi | `website/components/profile.tsx:33–42`: helper lokal memakai `fetch` tanpa timeout dan melempar `Error` tanpa status. Helper bersama di `website/lib/client/requests.ts:5` sudah menyediakan batas waktu dan `ApiError`. | Permintaan yang tidak selesai dapat membuat form terus sibuk; respons sesi kedaluwarsa tidak dapat dibedakan dari kesalahan biasa oleh pemanggil. Gunakan helper bersama, pulihkan busy melalui `finally`, dan arahkan ke portal yang sesuai pada 401 dengan tujuan kembali yang aman. Jangan otomatis mengulang mutasi kata sandi yang hasilnya tidak pasti. |
| Tinggi | `customer-account.tsx:33` memberi nama **Perlu tindakan** pada queue `needs-pic`; `order-views.ts:56` hanya memeriksa substitusi menunggu dan pengiriman berstatus dispatched. | Tagihan belum dibayar, bukti yang perlu dilengkapi, atau pembayaran ditolak tidak masuk filter tersebut. Buat selector tindakan khusus pelanggan berdasarkan status yang benar-benar tersedia, atau gunakan nama filter yang lebih terbatas. Pisahkan tindakan pelanggan dari keadaan **menunggu verifikasi toko**. |
| Tinggi | `customer-account.tsx:155–170`: ringkasan, alamat, lampiran, substitusi, perjalanan, pengiriman, lalu tagihan. | Tindakan mendesak dapat berada jauh di bawah konten informasional. Letakkan panel tindakan sebelum rincian, dengan CTA menuju bagian yang sudah ada, bukan tombol duplikat yang memicu mutasi tanpa penjelasan. |
| Sedang | Tautan kartu detail di `customer-account.tsx:129–133` tidak membawa konteks daftar; tombol kembali di `:154` selalu `/account/orders`. | Pengguna kehilangan pencarian/filter/halaman setelah memeriksa sebuah pesanan. Bawa hanya parameter daftar yang diizinkan dan gunakan sebagai return URL lokal; sediakan fallback daftar umum. |
| Sedang | `customer-account.tsx:134` menggunakan `Button asChild disabled` yang membungkus `Link` dengan `aria-disabled` dan tetap memiliki `href`. | `aria-disabled` menjelaskan keadaan tetapi tidak mematikan navigasi anchor. Ujung pagination harus benar-benar noninteraktif, atau memakai handler yang menolak aktivasi dan urutan fokus yang sesuai. Verifikasi Enter, Space, dan klik, bukan hanya warna abu-abu. |
| Sedang | `customer-account.tsx:168` menyusun pembuatan/review, seluruh pengiriman, kemudian seluruh tagihan. | Susunan dapat tidak kronologis ketika ada beberapa pengiriman dan tagihan. Bentuk daftar peristiwa dari tanggal yang tercatat, urutkan stabil, dan bedakan tanggal penerbitan, penerimaan, serta status sekarang. Jangan membuat timestamp dispatch yang tidak tersimpan. |
| Sedang | `relatedOrderInvoices` mengembalikan `orderAmount`, `total`, `balance`, dan `shared`; komponen tagihan pelanggan hanya memanfaatkan sebagian, pada `customer-account.tsx:188–198`. | Tagihan gabungan bisa tampak lebih besar daripada pesanan yang sedang dilihat. Tampilkan nilai pesanan ini dan total/sisa tagihan gabungan dengan label tegas, termasuk alasan total berbeda. |
| Sedang | Penerimaan di `customer-account.tsx:178` langsung menyimpan jumlah per baris melalui dialog; jumlah awal sama dengan jumlah dikirim. | Tambahkan review yang memperlihatkan dikirim, diterima, dan selisih sebelum konfirmasi. Ini pengurangan kesalahan input, bukan bukti bahwa implementasi sekarang melanggar aturan domain. Review khususnya berguna untuk nol/sebagian dan banyak baris. |
| Sedang | `profile.tsx:190` langsung mengarahkan pengguna ke login setelah mengganti kata sandi. | Login berikutnya tidak menerangkan hasil tindakan sebelumnya. Bawa kode pesan aman yang dipetakan ke salinan tetap: **Kata sandi diperbarui. Masuk kembali.** Jangan menampilkan teks bebas dari query string. |
| Sedang | `profile.tsx:280–292` memiliki tiga kolom password tanpa kontrol pengungkapan; login sudah memilikinya. | Lengkapi kontrol tampilkan/sembunyikan berlabel, tetap memakai autocomplete current/new-password. Jangan menonaktifkan paste atau password manager. |
| Rendah | `customer-account.tsx:99` memakai satu error dan tombol **Coba kembali** yang selalu menjalankan refresh; error yang sama juga dapat berasal dari logout. | Tautkan aksi pemulihan ke operasi yang gagal. Setelah kegagalan logout, **Coba keluar lagi** harus menjalankan logout; refresh data tidak menyelesaikan kegagalan itu. |
| Rendah | Lampiran `customer-account.tsx:207–221` memiliki deskripsi tipe/ukuran serta alert, tetapi belum mengaitkan seluruh bantuan/kesalahan melalui `aria-describedby`; keberhasilan hanya terlihat melalui daftar yang diperbarui. | Tambahkan ID hubungan, pesan status keberhasilan, dan pertahankan nama berkas yang dapat dibaca. Biarkan pengguna mengunggah ulang berkas yang sama setelah gagal; reset file input yang sudah ada mendukung ini. |

### Koreksi temuan awal mengenai tanggal

Customer checkout sekarang **tidak meminta tanggal kebutuhan**. Pada `website/lib/domain/engine.ts:64`, `neededAt` untuk customer diisi tanggal perintah; nilai ini bukan janji atau pilihan tanggal pengiriman. Karena itu, tidak ditampilkannya `neededAt` di detail pelanggan bukan bug tanggal pengiriman. Jangan menambah label **Estimasi tiba** atau **Tanggal pengiriman yang diminta** dari field ini. Permintaan jadwal pelanggan adalah pengembangan aturan bisnis terpisah bila memang diperlukan.

### Risiko yang masih membutuhkan reproduksi browser

- Guard perubahan profil menangani `beforeunload` dan klik link, tetapi tidak terlihat menangani `popstate`. Uji tombol Back browser pada navigasi SPA sebelum menyimpulkan data pasti hilang.
- State isian profil dibuat dari actor awal. Periksa refresh ketika profil berubah dari sesi lain: data bersih sebaiknya mengikuti pembaruan actor, sedangkan data kotor tidak boleh ditimpa diam-diam.
- `account-v7.css` mempertahankan dua kolom nilai tagihan pada mobile; uji nilai rupiah panjang pada 320 px. Jangan menyatakan ada overflow sebelum reproduksi.
- Dialog panjang memiliki area scroll dan footer di dalam form. Uji penerimaan banyak barang dengan keyboard mobile terbuka dan pembesaran teks.
- Beberapa lapisan CSS akun v7/v11 masih mengatur permukaan, padding, dan sidebar yang sama. Consolidasi selektor selama perubahan agar hasil bukan penumpukan override yang sulit diprediksi.

## Rencana per halaman

### Masuk pelanggan dan staf

Pertahankan bentuk pelanggan yang terang dengan fotografi produk, judul hangat, serta tombol utama bulat berwarna petroleum. Pertahankan staf sebagai panel putih ringkas di latar gelap dengan tombol lebih terstruktur dan diagram proses nyata. Perbedaan tugas harus terlihat dari judul dan tujuan halaman, bukan badge atau dekorasi tambahan.

Urutan kerja: rapikan pesan salah portal → tentukan pemulihan sesi → tambahkan hasil penggantian kata sandi → uji keyboard dan mobile. Saat salah portal, tampilkan tautan ke portal yang benar. Pertahankan `next` hanya jika sesuai peran/allowlist; jangan sekadar menyalinnya. Bantuan akun mengikuti kebijakan provisioning administrator yang ada; jangan membuat tombol reset via email atau daftar akun yang belum memiliki layanan backend.

Keadaan wajib: form siap, pengiriman, kredensial salah, portal salah, batas percobaan, gangguan jaringan, sesi masih aktif, dan berhasil masuk. Pesan rate limit saat ini menyebut 15 menit; jangan membuat countdown akurat palsu tanpa data waktu kedaluwarsa dari server. Field tidak perlu slide/stagger saat pengguna mengetik. Gerakan artwork yang ada boleh tetap, dengan reduced motion dan cleanup.

### Daftar pesanan

Pertahankan pencarian dan filter URL yang sudah ada. Perjelas hierarki kartu: nomor dan tanggal, ringkasan barang, status pemenuhan, nilai, lalu satu aksi **Lihat pesanan**. Tambahkan tanda tindakan berdasarkan selector bersama, bukan status warna generik. Bedakan **Terpenuhi** dari **Lunas** karena pemenuhan barang dan pembayaran adalah dua kondisi berbeda.

Keadaan wajib: memuat pertama kali, memperbarui data lama, kosong, hasil pencarian kosong, gagal muat dengan retry, dan halaman di luar batas. Perbaiki pagination serta konteks kembali sebelum menambah animasi kartu. Bila refresh mempertahankan data lama, gunakan status kecil **Memperbarui…**; jangan mengosongkan seluruh daftar tanpa alasan.

### Detail pesanan dan penerimaan

Susunan yang direncanakan:

1. Nomor, tanggal dibuat, status pemenuhan, nilai pesanan, dan tautan kembali berkonteks.
2. **Perlu tindakan Anda**, hanya bila ada tindakan, masing-masing dengan alasan dan tujuan yang jelas.
3. Barang dipesan dan keputusan substitusi, termasuk perubahan total sebelum menyetujui.
4. Pengiriman dan penerimaan, diikuti perkembangan kronologis yang tersedia.
5. Tagihan/pembayaran, alamat penerima, dan dokumen dengan judul ringkas.

Tindakan berisiko salah input memakai ringkasan sebelum simpan. Penerimaan menampilkan jumlah dikirim/diterima/selisih dan identitas penerima. Pertahankan validasi domain, penanganan hasil ambigu, ID perintah, serta draft yang dapat dipulihkan. Komplain setelah finalisasi harus mengikuti aturan bisnis yang ada; bila aksi tidak tersedia, jelaskan batas status tersebut tanpa menjanjikan refund baru.

### Tagihan dan pembayaran

Pisahkan **nilai pesanan ini**, **total tagihan gabungan**, **sisa tagihan**, dan **pembayaran menunggu verifikasi**. Pembayaran tercatat belum sama dengan lunas. Saat sudah ada pembayaran pending, utamakan melihat bukti/status verifikasi; pencatatan tambahan tetap mengikuti kebijakan cicilan, bukan otomatis ditutup atau digandakan. Pertahankan label simulasi serta larangan menyuruh pengguna mentransfer uang sungguhan pada demo.

### Profil dan alamat

Rapikan tiga kelompok: identitas/foto, alamat dan kontak, keamanan. Pertahankan email/peran read-only. Perbaiki helper jaringan terlebih dahulu, kemudian tambah kontrol pengungkapan password, pesan keberhasilan yang konsisten, dan pemulihan sesi. Menyimpan satu bagian tidak boleh menghilangkan perubahan yang belum disimpan di bagian lain. Saat refresh, bedakan state bersih dan state yang telah diubah sebelum menyinkronkan isian.

## Pemeriksaan penerimaan sebelum publikasi

- Pelanggan tidak dapat memakai portal staf; staf tidak masuk etalase pelanggan lewat redirect atau URL langsung. Tujuan setelah login tetap diizinkan per peran.
- Jaringan terputus, request tidak menjawab, dan 401 pada profil memiliki jalan pemulihan; busy tidak menggantung selamanya. Kata sandi tidak tersimpan dalam draft lokal.
- Filter **Perlu tindakan** dan panel detail berasal dari selector yang sama; pembayaran pending dibedakan dari tindakan pelanggan.
- Buka pesanan dari halaman kedua hasil pencarian, kembali, dan dapatkan konteks semula. Kontrol pagination ujung tidak aktif melalui keyboard maupun pointer.
- Penerimaan nol/sebagian/penuh dan substitusi dengan perubahan harga dapat ditinjau sebelum dikirim. Retry hasil ambigu tidak menciptakan transaksi kedua.
- Tagihan gabungan menjelaskan total berbeda; status terpenuhi tidak disamakan dengan lunas. Perjalanan pesanan tidak menampilkan tanggal mundur akibat urutan array kategori.
- Pada 320/390/768 px dan pembesaran teks: judul, nilai rupiah panjang, nama berkas, serta tombol tetap terbaca; tidak tertutup sidebar/header/footer atau keyboard. Fokus terlihat dan tidak terperangkap.
- Reduced motion tidak mengubah akses fungsi. Animasi bukan indikator tunggal loading, sukses, kesalahan, atau status transaksi.

Riset ini tidak menambahkan kode aplikasi, kebijakan baru, integrasi email, pelacakan kurir, atau klaim audit keamanan menyeluruh. Implementasi perlu diikuti uji alur dan pemeriksaan visual oleh agen utama.
