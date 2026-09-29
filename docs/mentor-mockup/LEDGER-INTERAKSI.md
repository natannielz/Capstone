# Ledger cakupan interaksi Figma

Audit dimulai 24 September 2026; pembaruan terakhir 25 September 2026, Asia/Jakarta. Dokumen ini membandingkan kontrol aplikasi dengan bukti prototipe yang sudah dicatat. Audit sumber dilakukan tanpa membuka browser atau mengubah Figma; bukti sesi penyuntingan dan pengujian Present dirujuk melalui catatan perapian. Perubahan editor belum dianggap terbukti sebelum hasil pengujiannya ditambahkan. Tanggal data demo/tangkapan 24 September tetap mengikuti sumber.

File desain: [Unit Toko — Mockup & Presentasi Mentor](https://www.figma.com/design/DAVyovkzeqhjSwOXP96Klr). Diagram: [alur Unit Toko](https://www.figma.com/board/HPRH7NqGQxuU7sL089HjoR).

**Permintaan semua tombol berfungsi belum terbukti.** Ledger ini memprioritaskan pembayaran, laporan, profil, administrasi, dan kontrol bersama. Ledger bukan inventaris lengkap seluruh modul. Tidak ada persentase penyelesaian karena jumlah seluruh instans kontrol belum dihitung.

## Cara membaca status

| Status | Makna dan batas bukti |
| --- | --- |
| Belum dibuat | Dokumen secara eksplisit menyatakan layar atau perilaku belum direplikasi. Bukan kesimpulan hanya karena bukti tidak ditemukan. |
| Belum terverifikasi | Belum ada bukti memadai untuk memastikan kontrol dibuat atau diuji. Status tambahan ini mencegah menyamakan tidak adanya dokumentasi dengan tidak adanya implementasi. |
| Tersambung | Reaksi/tujuan tersimpan di editor; belum ada bukti klik nyata di Present untuk instans tersebut. |
| Diuji klik | Klik nyata di Present membuka/menutup tujuan yang dicatat. Belum otomatis membuktikan validasi, perubahan data, atau kesinambungan keadaan. |
| Diuji hasil | Perubahan tampilan/data yang disebut dalam baris sudah diperiksa setelah tindakan. Bukti hanya berlaku untuk skenario, peran, dan viewport yang ditulis. |

`M` menandai frame mobile 390 × 844, `D` frame desktop 1280 × 900. Ukuran browser Present dapat berbeda dan dicatat terpisah; frame mobile bukan bukti pengujian perangkat ponsel fisik. Dialog dan drawer mempunyai ukuran tersendiri. `—` berarti ID belum dicatat atau belum diverifikasi; bukan bukti bahwa node tidak ada. Setiap tombol berulang tetap membutuhkan pemeriksaan instans dan tujuan. Satu baris kelompok tidak otomatis membuktikan semua elemen di dalam kelompok.

Hasil Figma adalah simulasi interaksi. Tes aplikasi, tangkapan layar, keberadaan frame, dan pemeriksaan panel Prototype adalah bukti berbeda. Tes aplikasi yang lulus tidak boleh meningkatkan status Figma menjadi Diuji klik atau Diuji hasil.

## Sumber bukti dan kontrak perilaku

| Kode | Sumber |
| --- | --- |
| B1 | [STATUS-FIGMA.md](STATUS-FIGMA.md), inventaris layar dan batas yang belum selesai. Pembaruan: 21 frame aplikasi desktop; 64 frame mobile terdiri dari 46 layar, sebelas drawer navigasi, lima dialog pembayaran dan dua menu filter. Delapan drawer terbaru lulus pembuka, penanda aktif, X dan rantai rute pada B13. |
| B2 | [PERAPIAN-RESPONSIF.md](PERAPIAN-RESPONSIF.md), bagian “Penambahan dan navigasi mobile internal” dan “Hasil uji lanjutan drawer dan masuk”. |
| B3 | [PERAPIAN-RESPONSIF.md](PERAPIAN-RESPONSIF.md), bagian “Perapian Penagihan dan dialog pembayaran”. |
| B4 | [PERAPIAN-RESPONSIF.md](PERAPIAN-RESPONSIF.md), bagian “Dialog verifikasi, penolakan, dan identifikasi”. |
| B5 | [PERAPIAN-RESPONSIF.md](PERAPIAN-RESPONSIF.md), bagian “Hasil verifikasi dalam prototype”. |
| B6 | [PERAPIAN-RESPONSIF.md](PERAPIAN-RESPONSIF.md), bagian interaksi tambahan, navigasi Profil & alamat, dan perilaku gulir pelanggan. |
| B7 | [PERAPIAN-RESPONSIF.md](PERAPIAN-RESPONSIF.md), bagian “Cabang sesudah verifikasi dan invoice belum lunas”, navigasi tujuh modul, hasil filter invoice, bobot font dan batas paket Starter. |
| B8 | [PERAPIAN-RESPONSIF.md](PERAPIAN-RESPONSIF.md), bagian “Pembayaran terfilter dan hasil kosong”, frame 163/185/186, verifikasi dengan filter tetap, dua Hapus filter, serta kontrol header/avatar/Catat transfer pada hasil kosong. |
| B9 | [PERAPIAN-RESPONSIF.md](PERAPIAN-RESPONSIF.md), bagian “Lima pilihan status pembayaran”, delapan frame baru, tujuan filter, hasil dan reset sebelum/sesudah verifikasi. |
| B10 | [PERAPIAN-RESPONSIF.md](PERAPIAN-RESPONSIF.md), bagian “Sidebar Kepala Toko desktop”, 99 sambungan tersimpan termasuk 72 sidebar dan delapan logo, serta cakupan uji Present. |
| B11 | [PERAPIAN-RESPONSIF.md](PERAPIAN-RESPONSIF.md), bagian “Administrasi mobile”, frame `327:2`, ukuran dan batas pemeriksaan. |
| B12 | [PERAPIAN-RESPONSIF.md](PERAPIAN-RESPONSIF.md), bagian “Escape dialog dan menu pembayaran”, lima dialog dan dua menu ditutup dengan keyboard tanpa mengubah hasil asal. |
| B13 | [PERAPIAN-RESPONSIF.md](PERAPIAN-RESPONSIF.md), bagian “Drawer Kepala Toko per modul”, delapan salinan nomor 57–64: pembuka, penanda aktif, X dan rantai delapan perpindahan lulus. Present diuji pada browser 1024×1000. |
| B14 | [PERAPIAN-RESPONSIF.md](PERAPIAN-RESPONSIF.md), bagian “Penutup drawer Penagihan dan avatar”, Escape/area luar dari dua asal, tiga avatar, serta dua logo menuju dashboard. |
| S1 | [workspace.tsx](../../website/components/workspace.tsx): `Workspace`, `ActionDialog`, `Payments`, `LeaveChangesDialog`. |
| S2 | [operations.tsx](../../website/components/operations.tsx): `Attachments`, `SettlementControls`, `ExpenseCenter`, `DocumentLink`, `Reports`, `Periods`, `AccountSettings`, `ListPagination`. |
| S3 | [payment-allocation.tsx](../../website/components/payment-allocation.tsx): form dan ringkasan alokasi. |
| S4 | [payment-views-v15.ts](../../website/lib/domain/payment-views-v15.ts), [engine.ts](../../website/lib/domain/engine.ts): status, filter dan aturan perubahan dana. |
| S5 | [sales-register.tsx](../../website/components/sales-register.tsx): penjualan, filter, CSV dan pagination. |
| S6 | [profile.tsx](../../website/components/profile.tsx): foto, data pribadi, kata sandi, draft/error dan perubahan belum disimpan. |
| S7 | [admin-password-reset.tsx](../../website/components/admin-password-reset.tsx): reset kata sandi admin dan pengulangan permintaan. |
| S8 | [dashboard.tsx](../../website/components/dashboard.tsx), [dashboard-views.ts](../../website/lib/domain/dashboard-views.ts): tujuan pintasan dan parameter filter/anchor. |
| S9 | [mobile-navigation.tsx](../../website/components/mobile-navigation.tsx), [use-workspace-navigation.ts](../../website/components/use-workspace-navigation.ts): drawer dan navigasi ruang kerja. |

## Kontrol bersama dan navigasi

| ID | Peran / layar | Kontrol dan prasyarat | Hasil yang diharapkan | Frame / node | Status | Bukti / langkah berikutnya |
| --- | --- | --- | --- | --- | --- | --- |
| NAV-01 | Kepala, M | Buka navigasi pada sembilan modul/profil | Drawer kiri dengan latar redup, sesuai modul asal | `104:2→107:2`; delapan pembuka modul pada NAV-36 | Diuji klik | B2/B13; sembilan pembuka awal diuji. Delapan kini menuju salinan per modul dan diuji ulang; Dashboard tetap memakai `107:2`. |
| NAV-02 | Kepala, M | Ringkasan; Katalog; Pesanan; Pengiriman; Persediaan; Pengadaan; Invoice; Laporan; Profil | Tujuan sesuai label dan peran | `107:29→104:2`; `43→98:2`; `51→99:2`; `66→100:2`; `76→101:2`; `93→102:2`; `107→103:2`; `116→105:2`; `130→106:2` (semua sumber berawalan `107:`) | Diuji klik | B2; belum membuktikan penanda aktif atau state transaksi lintas menu. |
| NAV-03 | Kepala, M | X; gulir drawer sampai bawah; Keluar akun | Tutup ke layar asal; footer terjangkau; keluar ke login | `107:7`; wadah `107:11`; `107:154→41:2` | Diuji hasil | B2, hasil uji lanjutan; posisi header tetap dan footer terjangkau dicatat. |
| NAV-04 | Kepala, M | Avatar dashboard | Profil Kepala Toko | `104:2` → `106:2` | Diuji klik | B2; ID tombol avatar belum dirinci dalam dokumen. |
| NAV-05 | Kepala, M | Enam avatar lain | Profil Kepala Toko tanpa berganti akun | Asal `99,100,101,102,103,105:2` → `106:2` | Tersambung | B2 menyebut delapan avatar tersimpan. Dashboard dan Katalog telah diuji pada NAV-04/NAV-40; enam instans ini belum. |
| NAV-06 | Penagihan, M | Buka navigasi pada tujuh layar awal | Drawer Penagihan | Asal `108,123,121,109,124,111,120:2` → `119:2` | Diuji klik | B3. |
| NAV-07 | Penagihan, M | Ringkasan; Pengadaan; Invoice; Pembayaran; Laporan; Periode; Profil | Tujuan sesuai peran | `119:29→108:2`; `43→123:2`; `57→121:2`; `66→109:2`; `74→124:2`; `83→111:2`; `98→120:2` (semua sumber berawalan `119:`) | Diuji klik | B3; cabang hasil transaksi belum dibuktikan dengan menu ini. |
| NAV-08 | Penagihan, M | X dan Keluar akun | Tutup ke asal; keluar ke login | `119:7`; `119:122→41:2` | Diuji klik | B3. |
| NAV-09 | Penagihan, M | Avatar dashboard | Profil Penagihan | `108:2` → `120:2` | Diuji klik | B3. |
| NAV-10 | Penagihan, M | Empat avatar layar awal lain | Profil Penagihan | Asal `111,121,123,124:2` → `120:2` | Tersambung | B3. Avatar pembayaran awal kini diuji pada NAV-39; empat instans ini belum. |
| NAV-11 | Semua internal, D/M | Logo Unit Toko; Panduan demo | Logo menuju dashboard peran; panduan terbuka | — | Belum terverifikasi | S1; dua logo drawer Penagihan diuji pada NAV-41–42 dan delapan logo Kepala Toko desktop pada NAV-43. Logo peran/instans lain serta Panduan demo belum terbukti. |
| NAV-12 | Semua internal, D/M | Muat ulang data | Pending/disabled, data diperbarui; error menyediakan Coba kembali | — | Belum terverifikasi | S1. Tangkapan awal saja tidak menguji tindakan. |
| NAV-13 | Semua internal, D/M | Coba kembali; Coba keluar kembali pada error | Percobaan ulang dengan feedback dan pencegahan klik berulang | — | Belum terverifikasi | S1; perlu state error yang dapat dicapai. |
| NAV-14 | Penagihan, M | Penanda menu aktif sesudah pindah modul | Modul tujuan ditandai aktif, identitas akun tetap | Drawer `119:2`, `152:2` | Belum dibuat | Penagihan masih memakai penanda Ringkasan. Penanda Kepala Toko sudah ditangani dan diuji pada NAV-36/B13; bukti itu tidak mencakup Penagihan. |
| NAV-15 | Semua internal, D | Seluruh sidebar, avatar, logo, panduan, keluar | Navigasi desktop peran dan keadaan yang benar | Layar internal `20:5`–`33:5` sesuai B1 | Belum terverifikasi | Kepala Toko dibuktikan B10/NAV-27–35/NAV-43. Cakupan seluruh peran/instans serta Panduan demo belum lengkap; jangan menyimpulkan semua navigasi desktop lulus. |
| NAV-16 | Kepala, M | Klik latar drawer dan Escape | Tutup ke layar asal tanpa kehilangan state | `107:2` | Belum terverifikasi | B2 menyebut outside-close diaktifkan; bukti pengaturan belum sama dengan klik. Escape Kepala belum diuji. Bukti Penagihan dipisah NAV-37–38. |
| NAV-17 | Kepala, M | Tinjau pesanan masuk | Daftar antrean yang sesuai, bukan sekadar layar berjudul Pesanan | `104:62→99:2` | Diuji klik | B2 membuktikan tujuan; filter antrean perlu dibandingkan dengan S8. |
| NAV-18 | Login staf, M | Masuk ke ruang kerja | Simulasi Kepala Toko | `41:80→104:2` | Diuji klik | B2. Input kredensial/validasi login secara eksplisit belum direplikasi. |
| NAV-19 | Pelanggan, M | Profil & alamat pada semua lokasi | Tujuan profil yang benar; tidak ada reaksi pada heading/nonkontrol | Contoh `78:256→47:2`; `74:2→47:2` | Belum terverifikasi | B6: 18 pilihan massal menunjukkan Mixed; hanya contoh tertentu dibuktikan. Inventaris per instans belum selesai. |
| NAV-20 | Penagihan hasil, M | Buka navigasi pada tujuh modul hasil | Drawer cabang hasil | `147:7` dan header pada `152:907,152:435,152:267,152:535,152:825,152:120` → `152:2` | Diuji klik | B7; semua pembuka dipakai saat berpindah modul. |
| NAV-21 | Penagihan hasil, M | Tujuh tujuan menu cabang hasil | Modul dan akun Penagihan tetap benar | `152:24→152:907`; `36→152:435`; `48→152:267`; `56→147:2`; `63→152:535`; `71→152:825`; `84→152:120` (semua sumber berawalan `152:`) | Diuji klik | B7; status pembayaran saat kembali melalui Laporan juga diperiksa, lihat PAY-14. |
| NAV-22 | Penagihan hasil, M | Avatar pembayaran hasil | Profil pada cabang hasil | `147:21→152:120` | Diuji klik | B7 menggantikan tujuan lama `120:2`. |
| NAV-23 | Penagihan hasil, M | Empat avatar salinan Pengadaan, Invoice, Laporan, Periode | Profil pada cabang hasil tampil benar | Asal `152:435,152:267,152:535,152:825` → `152:120` | Tersambung | B7; Pengadaan mencapai URL profil tetapi pratinjau kemudian kosong, belum lulus visual. Tiga lainnya belum diuji satu per satu. |
| NAV-24 | Penagihan hasil, M | Menu invoice Belum lunas | Drawer cabang hasil | Asal `152:1051` → `152:2` | Diuji klik | B7; pembuka menu telah dipakai. |
| NAV-25 | Penagihan hasil, M | Avatar invoice Belum lunas | Profil cabang hasil | Asal `152:1051` → `152:120` | Tersambung | B7; mewarisi sambungan, belum diuji. |
| NAV-26 | Penagihan hasil, M | Avatar dashboard hasil | Profil cabang hasil tampil benar | `152:923→152:120` | Diuji klik | B7; tujuan dan tampilan profil berhasil diperiksa. |
| NAV-27 | Kepala, D sembilan layar | Delapan tujuan sidebar ke modul lain per layar | Membuka modul Kepala Toko sesuai pilihan | Asal `20:5,29:5,22:5,21:5,24:5,23:5,25:5,26:5,27:5` | Diuji klik | B10; seluruh 72 rute tersimpan telah diklik. Uji lanjutan 49 rute memeriksa URL dan screenshot setiap tujuan, melengkapi 23 rute terdahulu. Item modul aktif tidak mengubah halaman. |
| NAV-28 | Kepala, D dashboard | Delapan pilihan modul/profil | Katalog, Pesanan, Pengiriman, Persediaan, Pengadaan, Invoice, Laporan, Profil | `20:380→29:5`; `388→22:5`; `403→21:5`; `413→24:5`; `430→23:5`; `444→25:5`; `453→26:5`; `467→27:5` (semua sumber `20:`) | Diuji klik | B10; delapan tujuan diuji Present. |
| NAV-29 | Kepala, D delapan modul | Ringkasan | Dashboard Kepala Toko | `29:5,22:5,21:5,24:5,23:5,25:5,26:5,27:5` → `20:5`; contoh kontrol `29:940` | Diuji klik | B10; delapan jalur kembali diuji Present. |
| NAV-30 | Kepala, D profil | Tujuh modul selain Ringkasan/Profil | Modul yang sesuai | `27:5` → `29:5,22:5,21:5,24:5,23:5,25:5,26:5` | Diuji klik | B10; tujuh tujuan diuji. Ringkasan masuk NAV-29. |
| NAV-31 | Kepala, D | Avatar pada delapan layar selain Profil | Profil Kepala Toko | `20:5,29:5,22:5,21:5,24:5,23:5,25:5,26:5` → `27:5` | Diuji klik | B10; delapan avatar diuji, ID kontrol belum dirinci di laporan. |
| NAV-32 | Kepala, D | Keluar akun pada sembilan layar | Login staf | Sembilan frame B10 → `10:2` | Diuji klik | B10; sembilan tombol Keluar diuji setelah footer sidebar dapat digulir. |
| NAV-33 | Kepala, D | Gulir sidebar hingga footer | Sidebar bergerak mandiri, footer Keluar terjangkau | Sidebar pada sembilan frame B10 | Diuji hasil | B10; Position Fixed dan Overflow Vertical diterapkan. Tidak membuktikan gulir seluruh tabel/modul. |
| NAV-34 | Login staf, D | Masuk ke ruang kerja | Simulasi masuk ke Dashboard Kepala Toko | `10:170→20:5` | Diuji hasil | B10; klik, URL tujuan, dan screenshot lulus. Input kredensial/validasi belum direplikasi. |
| NAV-35 | Kepala, D dashboard | Tinjau pesanan masuk | Membuka daftar Pesanan | `20:74→22:5` | Diuji hasil | B10; klik, URL tujuan, dan screenshot lulus. Bukti tujuan ini tidak mencakup semua variasi antrean/filter. |
| NAV-36 | Kepala, M | Delapan pembuka drawer; penanda aktif; X | Modul asal ditandai aktif; X kembali ke asal | `98:7→392:2`; `99:7→405:2`; `100:7→405:150`; `101:7→405:298`; `102:7→405:446`; `103:7→405:594`; `105:7→405:742`; `106:7→405:890` | Diuji hasil | B13; seluruh delapan pembuka, penanda aktif, dan X lulus klik serta visual. Overlay Top left, latar 25%, close outside tersimpan. Browser Present 1024×1000; tidak membuktikan interaksi perangkat ponsel 390×844. |
| NAV-37 | Penagihan, M Semua status awal | Escape; klik di luar drawer | Drawer tertutup, Semua status dan tiga catatan tetap | `109:2` → drawer `119:2` | Diuji hasil | B14; setiap penutup diuji setelah membuka ulang menu, screenshot asal tidak berubah. |
| NAV-38 | Penagihan hasil, M Belum teridentifikasi | Escape; klik di luar drawer | Drawer tertutup, filter Belum teridentifikasi dan satu dari tiga catatan tetap | `249:2` → drawer `152:2` | Diuji hasil | B14; setiap penutup diuji setelah membuka ulang, screenshot hasil tetap. Tidak membuktikan semua asal atau Keluar akun. |
| NAV-39 | Penagihan, M pembayaran awal | Avatar | Profil Sinta pada cabang awal | `109:2` → `120:2` | Diuji hasil | B14; klik, URL tujuan, dan screenshot identitas Sinta lulus. |
| NAV-40 | Kepala, M katalog | Avatar | Profil Dewi/Kepala Toko | `98:2` → `106:2` | Diuji hasil | B14; klik, URL tujuan, dan screenshot identitas Dewi lulus. |
| NAV-41 | Penagihan awal, M drawer | Logo Unit Toko | Dashboard awal dengan satu transfer menunggu verifikasi | `119:12→108:2` | Diuji hasil | B14; dari `109:2`, buka drawer lalu klik logo. URL dan screenshot Dana masuk menunggu pemeriksaan sesuai. |
| NAV-42 | Penagihan hasil, M drawer | Logo Unit Toko | Dashboard hasil dengan satu invoice dan sisa Rp334.000 | `152:11→152:907` | Diuji hasil | B14; dari profil `152:120`, buka drawer lalu klik logo. URL dan screenshot Ringkasan sesuai. |
| NAV-43 | Kepala, D delapan modul/profil | Logo Unit Toko | Kembali Dashboard Kepala Toko | Asal `29:5,22:5,21:5,24:5,23:5,25:5,26:5,27:5` → `20:5`; contoh logo `29:923` | Diuji hasil | B10; delapan klik nyata, URL, dan screenshot tujuan lulus. Logo Dashboard sendiri tidak diberi reaksi; peran Pembayaran/Periode/Admin dikecualikan. |
| NAV-44 | Kepala, M delapan salinan drawer | Rantai pilihan modul menuju Dashboard | Setiap tujuan dan penanda menu cocok dengan modul yang dibuka | `98:2→99:2→100:2→101:2→102:2→103:2→105:2→106:2→104:2`, melalui drawer masing-masing | Diuji hasil | B13; delapan perpindahan lulus klik, URL, dan screenshot. Tidak membuktikan seluruh kombinasi sembilan tujuan pada delapan salinan. |
| NAV-45 | Kepala, M Pesanan | Klik di luar drawer baru | Kembali Pesanan tanpa mengubah daftar/filter | `99:2` → `405:2` → tutup | Diuji hasil | B13; enam pesanan dan filter Semua tetap. Close outside pada tujuh salinan lain baru terbukti pengaturannya, belum diuji satu per satu. |

## Pembayaran

Frame awal Penagihan: M `109:2` (Semua status), `163:2` (Menunggu verifikasi), D `31:5`. Hasil verifikasi: M `147:2` (Semua status), `185:2` (hasil kosong dengan Menunggu verifikasi tetap aktif). Semua baris berikut memakai Penagihan kecuali disebut lain. Bukti M tidak berlaku otomatis untuk D.

| ID | Layar | Kontrol dan prasyarat | Hasil yang diharapkan | Frame / node | Status | Bukti / langkah berikutnya |
| --- | --- | --- | --- | --- | --- | --- |
| PAY-01 | M dashboard | Periksa pembayaran saat ada dana menunggu | Menunggu verifikasi; satu Rp175.000 dari tiga catatan, pencarian kosong, halaman pertama | `108:62→163:2` | Diuji hasil | B8; klik dan visual target terfilter berhasil. Menggantikan tujuan lama `109:2`. Hapus filter pada hasil positif juga diuji, lihat PAY-42. |
| PAY-02 | M awal | Catat transfer | Dialog terpusat, latar redup | `109:59→132:2` | Diuji klik | B3. |
| PAY-03 | M dialog | Batal; X; klik luar dialog catat | Tutup tanpa mencatat; kembali ke asal | `132:61`, `132:66`, outside | Diuji klik | B3; jangan menaruh Close overlay pada header atau wadah form. |
| PAY-04 | M dialog | Gulir form catat | Kolom Catatan terjangkau, header/footer tetap terlihat | Wadah `132:11` | Diuji hasil | B3. |
| PAY-05 | M | Tanggal, pembeli, nominal, pembayar, referensi, catatan | Isian dapat diubah; pembeli/referensi/catatan opsional; nominal minimal 1; pembayar wajib | `132:2` | Belum dibuat | B1/B3 menyatakan masukan form belum berfungsi. S1. Padanan desktop dicakup PAY-36. |
| PAY-06 | D/M | Catat transfer dengan input valid | Pending lalu baris baru Menunggu verifikasi; piutang belum berkurang | M `132:2` → — | Belum terverifikasi | S1/S4; hasil simpan belum ada dalam B1/B5. |
| PAY-07 | D/M | Catat transfer kosong/invalid/gagal | Error tepat; isian bertahan; tidak menambah baris | — | Belum terverifikasi | S1/S4; perlu skenario terpisah dari sukses. |
| PAY-08 | M awal | Geser tabel penerimaan | Status dan aksi kanan dapat dijangkau | Wadah `109:83` | Diuji hasil | B3. |
| PAY-09 | M awal | Verifikasi pada pembayaran recorded | Dialog verifikasi dana | `109:176→136:2` | Diuji klik | B4. |
| PAY-10 | M dialog | Batal dan X verifikasi | Tutup, posisi tabel dipertahankan | `136:19`, `136:25` | Diuji klik | B4. |
| PAY-11 | M dialog | Verifikasi dana Rp175.000 dengan pembeli belum diketahui | Hasil **Belum teridentifikasi**, belum dialokasikan Rp175.000, hanya aksi Identifikasi | `136:22→147:2` | Diuji hasil | B5; sesuai perubahan yang dicatat di aplikasi. |
| PAY-12 | M hasil | Menu; avatar; Catat transfer/X; Identifikasi | Masing-masing dapat dibuka dari hasil; X catat kembali ke hasil | `147:7→152:2`; `147:21→152:120`; `147:55→132:2`; `147:176→140:2` | Diuji klik | B5/B7. Tujuan menu/avatar diperbarui; uji X drawer lama tidak otomatis membuktikan X drawer hasil. |
| PAY-13 | M hasil | Geser tabel hasil | Label Belum teridentifikasi, saldo Rp175.000 dan aksi Identifikasi terlihat | `147:83` | Diuji hasil | B5. |
| PAY-14 | M | Verifikasi, buka Laporan melalui menu hasil, kembali Pembayaran | Status Belum teridentifikasi tetap pada cabang hasil | `108:2→119:2→109:2→136:2→147:2→152:2→152:535→152:2→147:2` | Diuji hasil | B7. Masalah diselesaikan sebagian dengan cabang frame statis: tujuh modul sudah diuji klik, jalur pulang melalui Laporan diuji hasil. Belum seluruh jalur pulang/aksi lain; padanan desktop PAY-38. |
| PAY-15 | M awal | Tolak pada pembayaran recorded | Dialog alasan penolakan | `109:179→138:2` | Diuji klik | B4. |
| PAY-16 | M dialog | Batal dan X penolakan | Tutup tanpa menolak | `138:24`, `138:30` | Diuji klik | B4. |
| PAY-17 | D/M | Alasan kosong; alasan valid; konfirmasi Tolak | Kosong ditolak; sukses menjadi Ditolak, alasan/tanggal terlihat, aksi verifikasi/identifikasi/upload hilang | M `138:2` → — | Belum terverifikasi | S1/S4; B5 menyebut hasil simpan Tolak belum lengkap. |
| PAY-18 | M awal/hasil | Identifikasi pada dana tanpa pembeli | Dialog pembeli dan hasil penelusuran | `109:182→140:2`; `147:176→140:2` | Diuji klik | B4/B5. |
| PAY-19 | M dialog | Batal dan X identifikasi | Tutup tanpa mengubah identitas | `140:35`, `140:41` | Diuji klik | B4. |
| PAY-20 | D/M | Pembeli dan hasil penelusuran; Simpan | Kedua isian wajib. Sukses menetapkan pembeli; dana verified menjadi Terverifikasi dan dapat Alokasikan bila bersaldo | M `140:2` → — | Belum terverifikasi | S1/S4; form dan hasil belum lengkap dalam B5. |
| PAY-21 | D/M | Cari pembayaran | Cocokkan pembayar/referensi/pembeli; jumlah hasil berubah; halaman kembali pertama | `109:2`, `147:2`, `163:2`, `185:2`, `31:5` | Belum terverifikasi | S1/S4; belum ada bukti masukan dan hasil. B8 menegaskan pencarian cabang terfilter belum interaktif. |
| PAY-22 | M Penagihan | Filter Semua status / Menunggu verifikasi / Terverifikasi / Belum teridentifikasi / Ditolak | Hanya baris berstatus tampilan yang dipilih; halaman pertama | Menu `215:2` sebelum, `218:14` sesudah; sepuluh pembuka dan sepuluh pilihan dirinci di B9 | Diuji hasil | B9; dua siklus lengkap lulus klik dan visual. Jumlah sebelum 3/1/2/0/0, sesudah 3/0/2/1/0. Pencarian kosong; padanan desktop dan gabungan pencarian belum terbukti. |
| PAY-23 | M hasil kosong | Hapus filter setelah verifikasi pada recorded | Semua status dengan tiga pembayaran, termasuk dana Belum teridentifikasi | `185:95→147:2` | Diuji hasil | B8; klik dan visual sesuai. Belum membuktikan input pencarian, pilihan status lain, atau padanan desktop. |
| PAY-24 | D/M | Sebelumnya/Berikutnya, bila >12 hasil | 12 per halaman; batas disabled; jumlah/rentang benar | — | Belum terverifikasi | S1/S4; data yang sedikit tidak menguji pagination. |
| PAY-25 | PIC/Penagihan, D/M | Unggah bukti pada pembayaran tidak ditolak | Simulasi pemilihan PNG/JPG/PDF ≤4 MB; pending, sukses/error, lampiran muncul | — | Belum terverifikasi | S2; tidak ditemukan bukti Figma. |
| PAY-26 | Sesuai akses, D/M | Klik lampiran yang sudah ada | Dokumen bukti dapat diperiksa dan kembali | — | Belum terverifikasi | S2; jangan arahkan ke file privat yang tak dapat diakses mentor. |
| PAY-27 | D/M | Alokasikan pada verified + pembeli dikenal + saldo >0 | Dialog invoice pembeli yang sama | — | Belum terverifikasi | S1/S3. |
| PAY-28 | D/M alokasi | Ubah tanggal/nominal per invoice; Dokumen | Ringkasan total/dana sisa/sisa invoice berubah; dokumen invoice tepat | — | Belum terverifikasi | S3. |
| PAY-29 | D/M alokasi | Nol, teks/desimal, melebihi invoice/dana, invoice tak tersedia | Error spesifik; Simpan disabled; tidak mengalokasikan | — | Belum terverifikasi | S3: rupiah bulat, batas 100 invoice, total >0. |
| PAY-30 | D/M alokasi | Simpan alokasi valid; Batal/X | Sukses memperbarui saldo/piutang; batal tetap; gagal mempertahankan nominal untuk diperiksa | — | Belum terverifikasi | S3/S4. Perlu alokasi parsial dan multi-invoice, bukan satu contoh saja. |
| PAY-31 | Penagihan, D/M | Ajukan koreksi | Pilih invoice, nilai, alasan; hasil menunggu persetujuan | — | Belum terverifikasi | S2 `SettlementControls`; tersedia juga di Invoice & piutang. |
| PAY-32 | Pimpinan, D/M | Setujui koreksi / Tolak dengan alasan | Status, piutang/saldo berubah sesuai keputusan; batal tidak mengubah | — | Belum terverifikasi | S2/S4; membutuhkan layar peran Pimpinan. |
| PAY-33 | Penagihan, D/M | Ajukan pengembalian | Pilih saldo, nominal dan alasan; hasil requested | — | Belum terverifikasi | S2; tidak boleh melebihi saldo yang dapat dikembalikan. |
| PAY-34 | Pimpinan/Penagihan, D/M | Setujui/Tolak pengembalian; Catat pembayaran setelah approved | Peran dan tombol benar; hasil status/saldo benar | — | Belum terverifikasi | S2/S4. |
| PAY-35 | Sesuai peran, D/M | Ajukan biaya, lampiran, Setujui/Tolak biaya, Catat pembayaran | Terkait pengiriman/pengadaan; keputusan dan pembayaran terlihat | — | Belum terverifikasi | S2 `ExpenseCenter`; cakupan tidak hanya panel Penerimaan dana. |
| PAY-36 | D | Padanan empat dialog, semua penutup dan konfirmasi | Perilaku sama pada desktop, ukuran/scroll sesuai | Asal `31:5`, dialog — | Belum terverifikasi | B1 memuat dialog M saja. Bukti PAY-02–20 tidak mencakup D. |
| PAY-37 | M dashboard hasil | Buka invoice belum lunas | Filter Belum lunas; satu INV/202609/0001 Dibayar sebagian, total Rp684.000, sisa Rp334.000 | `152:953→152:1051` | Diuji hasil | B7. Judul/deskripsi/hitungan dashboard disamakan dengan sumber; screenshot hasil filter cocok. |
| PAY-38 | D | Navigasi keluar/kembali setelah verifikasi | Status, saldo, identitas dan aksi setelah verifikasi bertahan | — | Belum terverifikasi | B7 hanya mencakup mobile. |
| PAY-39 | M invoice hasil | Ubah filter dari Belum lunas | Daftar/jumlah sesuai pilihan dan navigasi tetap pada cabang hasil | `152:1051`, teks filter `152:1090` | Belum dibuat | B7 menyatakan filter belum dapat diubah. PAY-37 hanya membuktikan hasil filter yang sudah disiapkan. |
| PAY-40 | M terfilter | Verifikasi, lalu konfirmasi pada satu-satunya transfer recorded | Modal khusus, lalu filter tetap Menunggu verifikasi dan 0 pembayaran dari 3 catatan | `163:125→186:2`; `186:16→185:2` | Diuji hasil | B8; kedua langkah diuji klik/visual. Modal terpusat, latar redup 25%. Judul kosong dan penjelasan sesuai sumber. |
| PAY-41 | M terfilter | Gulir horizontal tabel | Status dan aksi transfer Rp175.000 terjangkau | Frame `163:2`, wadah `163:66` | Diuji hasil | B8; wadah Hug 269,48 dan Payments `163:36` Hug setelah dua baris lain dihapus. |
| PAY-42 | M hasil positif | Hapus filter kecil saat filter aktif dan hasil >0 | Kembali Semua status dengan tiga catatan awal | `201:2` di `163:63` → `109:2` | Diuji hasil | B8; klik dan visual berhasil. Gaya ghost kecil disamakan dengan sumber; wadah Hug menjadi tinggi 66. |
| PAY-43 | M dialog terfilter | Batal/X/klik di luar dialog | Tutup ke `163:2` tanpa verifikasi dan tanpa menghilangkan filter | Batal `186:18`, X `186:20`, area luar overlay `186:2` | Diuji hasil | B8; ketiganya diuji klik dan visual, kembali ke satu pembayaran Menunggu verifikasi tanpa perubahan. Escape dicatat terpisah pada PAY-47. |
| PAY-44 | M terfilter | Pindah modul lalu kembali Pembayaran sebelum/sesudah verifikasi | Mengingat Menunggu verifikasi beserta pencarian/halaman sebelumnya | `163:2` / `185:2` → modul lain → kembali | Belum dibuat | B8/S9: sumber mengingat query per modul; menu prototipe dapat mereset ke Semua status. Bukti PAY-14 berawal dari Semua status dan tidak mencakup kasus ini. |
| PAY-45 | M hasil kosong | Menu/X; avatar; Catat transfer/Batal | Menu dan catat dapat ditutup kembali ke filter recorded kosong; profil Sinta tampil | `185:7→152:2`; `185:21→152:120`; `185:55→132:2` | Diuji hasil | B8; klik dan visual semua langkah berhasil. Belum menguji kembali dari profil ke filter kosong melalui menu. |
| PAY-46 | D / state M lain | Hapus filter pada hasil positif/kosong lainnya | Pencarian, status dan halaman direset; seluruh catatan kembali | — | Belum terverifikasi | S1/S4; PAY-23 hanya membuktikan satu skenario hasil kosong M. |
| PAY-47 | M dialog terfilter | Escape saat dialog verifikasi terbuka | Tutup ke `163:2` tanpa verifikasi dan tanpa menghilangkan filter | Overlay `186:2` | Diuji hasil | B12; dialog dibuka, Escape ditekan, screenshot asal diperiksa: Menunggu verifikasi, satu dari tiga catatan, transfer tetap. |
| PAY-48 | M Terverifikasi awal | Hapus filter dari dua hasil | Semua status, tiga pembayaran awal | `229:2` pada `210:2` → `109:2` | Diuji hasil | B9; klik, tujuan dan visual lulus. Dua hasil awal Rp108.000 dan Rp350.000. |
| PAY-49 | M Terverifikasi hasil | Hapus filter dari dua hasil | Semua status, tiga pembayaran sesudah verifikasi | `232:66→147:2` | Diuji hasil | B9; klik, tujuan dan visual lulus. |
| PAY-50 | M Belum teridentifikasi awal | Hapus filter dari nol hasil | Semua status, tiga pembayaran awal | `245:75→109:2` | Diuji hasil | B9; uji ulang setelah pemulihan dropdown lulus klik dan visual: Semua status serta tiga pembayaran terlihat normal. |
| PAY-51 | M Ditolak awal | Hapus filter dari nol hasil | Semua status, tiga pembayaran awal | `243:75→109:2` | Diuji hasil | B9; klik, tujuan dan visual lulus. |
| PAY-52 | M Belum teridentifikasi hasil | Hapus filter dari satu hasil | Semua status, tiga pembayaran sesudah verifikasi | `259:2` pada `249:2` → `147:2` | Diuji hasil | B9; klik, tujuan dan visual lulus. Asal memuat Rp175.000 dan sisa Rp175.000. |
| PAY-53 | M Ditolak hasil | Hapus filter dari nol hasil | Semua status, tiga pembayaran sesudah verifikasi | Kontrol pada `236:2` → `147:2`; ID tombol belum dicatat | Diuji hasil | B9; klik, tujuan dan visual lulus. Jangan menebak ID tombol dari salinan. |
| PAY-54 | M dua layar status baru | Header menu dan avatar | Memakai menu/profil sebelum atau sesudah verifikasi yang sesuai | Asal `210:2`, `236:2` | Tersambung | B9; mewarisi sambungan salinan, belum uji Present setiap instans. Bukti `232:2`, `245:2`, `243:2`, dan `249:2` dipisah PAY-58–60/PAY-66. |
| PAY-55 | D / M dengan pencarian | Pemilih status dan pencarian gabungan | Status mempertahankan kata pencarian, mereset halaman dan menampilkan hasil gabungan | — | Belum terverifikasi | S1/S4; sepuluh cabang B9 hanya memakai pencarian kosong dan data demo tiga catatan. |
| PAY-56 | M pilihan status | Klik di luar menu pada tiga asal | Menu tertutup, filter dan hasil asal tidak berubah | Asal `109:2`, `147:2`, `249:2`; menu `215:2` / `218:14` | Diuji hasil | B9; klik dan visual lulus pada ketiga asal ini. Belum seluruh instans penutup atau keyboard. |
| PAY-57 | M pilihan status | Keyboard untuk membuka, memindahkan pilihan dan memilih | Navigasi fokus serta panah/Enter sesuai perilaku sumber | Menu `215:2`, `218:14` | Belum terverifikasi | B9 membatasi bukti pilihan pada klik. Escape sudah diuji terpisah pada PAY-62; penutup tidak membuktikan perpindahan fokus/pilihan. |
| PAY-58 | M Terverifikasi hasil | Buka menu/X; avatar | Menu hasil dapat ditutup ke asal; profil Sinta pada cabang hasil | `232:5→152:2`, X kembali `232:2`; `232:18→152:120` | Diuji hasil | B9; klik dan visual menu, tutup, serta identitas Profil Sinta lulus. |
| PAY-59 | M Belum teridentifikasi awal kosong | Buka menu/X; avatar | X kembali dengan filter dan hasil kosong tetap; profil Sinta pada cabang awal | `245:5→119:2`, X kembali `245:2`; `245:18→120:2` | Diuji hasil | B9; klik nyata dan screenshot membuktikan hasil kosong Belum teridentifikasi tetap, serta profil Sinta tampil. |
| PAY-60 | M Ditolak awal kosong | Buka menu/X; avatar | X kembali dengan filter dan hasil kosong tetap; profil Sinta pada cabang awal | `243:5→119:2`, X kembali `243:2`; `243:18→120:2` | Diuji hasil | B9; klik nyata dan screenshot membuktikan hasil kosong Ditolak tetap, serta profil Sinta tampil. |
| PAY-61 | M empat dialog awal | Escape setelah membuka Catat, Verifikasi, Tolak, atau Identifikasi | Dialog tertutup tanpa perubahan; asal Semua status dengan tiga catatan tetap | Overlay `132:2`, `136:2`, `138:2`, `140:2` dari `109:2` | Diuji hasil | B12; setiap dialog dibuka dan ditutup melalui Escape, lalu screenshot asal diperiksa. Dana Rp175.000 tetap Menunggu verifikasi. Bukti terfilter terpisah PAY-47. |
| PAY-62 | M pilihan status sebelum/sesudah verifikasi | Escape setelah membuka menu | Menu tertutup; Semua status dan tiga catatan asal tetap | `215:2` dari `109:2`; `218:14` dari `147:2` | Diuji hasil | B12; kedua menu dibuka dengan klik, Escape ditekan, lalu screenshot hasil asal diperiksa. Tidak mencakup panah/Enter atau setiap asal terfilter. |
| PAY-63 | M Ditolak hasil | Geser tabel Biaya operasional ke kanan | Kolom Bukti & tindakan terjangkau; halaman tidak ikut bergeser | `236:129` pada `236:2` | Diuji hasil | B9; Overflow Horizontal menggantikan No scrolling, Clip tetap aktif. Present memperlihatkan kolom kanan utuh dan hanya isi tabel bergerak. Asal tambahan dicatat PAY-64–65; aksi biaya belum tercakup. |
| PAY-64 | M Semua status awal | Geser tabel Biaya operasional ke kanan | Bukti & tindakan terlihat utuh; hanya isi tabel bergerak | Tabel biaya pada `109:2` | Diuji hasil | B9; klik/gulir dan screenshot lulus. Sebelas wadah yang cocok sudah disetel Horizontal; tiga asal diuji individual pada PAY-63–65. |
| PAY-65 | M Semua status hasil | Geser tabel Biaya operasional ke kanan | Bukti & tindakan terlihat utuh; hanya isi tabel bergerak | Tabel biaya pada `147:2` | Diuji hasil | B9; gulir nyata hingga kolom kanan dan screenshot lulus. Delapan instans lainnya belum diuji individual. |
| PAY-66 | M Belum teridentifikasi hasil | Menu; Escape/area luar; avatar | Menu hasil dapat ditutup dengan filter tetap; avatar ke profil Sinta cabang hasil | `249:2` → `152:2`; avatar → `152:120` | Diuji hasil | B14; pembuka, kedua penutup, dan avatar lulus klik/keyboard, URL serta screenshot. Penutup dirinci NAV-38; X dari asal ini belum diuji tersendiri. |

Cabang hasil memakai layar statis, bukan logika variabel. Boolean `Demo/Dana diverifikasi` pada koleksi `Keadaan demo` bernilai awal False tidak digunakan karena editor Starter meminta Professional untuk multiple actions/variabel/conditional prototypes. Tidak ada upgrade. Keberadaan variabel ini bukan bukti interaksi berfungsi. Outside/Escape drawer hasil sudah diuji dari `249:2`; asal lainnya, Keluar akun, avatar yang belum lulus visual, ingatan filter antar-modul dan kesinambungan hasil aksi lain tetap perlu pengujian tersendiri. X drawer hasil sudah diuji dari `185:2`; jangan memperluas bukti tersebut ke seluruh instans. Dari Semua status, `136:2` menuju `147:2`; dari Menunggu verifikasi, dialog `186:2` menuju hasil kosong `185:2`. H1 Pembayaran dan H2 Penerimaan dana pada `109:2`, `147:2`, `163:2`, `185:2` sudah diubah ke bobot 650; visual `163:2` diperiksa tanpa tumpang tindih. Pemeriksaan visual tiga salinan lain dan audit heading lain belum lengkap.

## Laporan dan periode

Frame laporan: Kepala D `26:5`, M `105:2`; Penagihan M `124:2`, cabang hasil `152:535`. Periode Penagihan D `32:5`, M `111:2`, cabang hasil `152:825`. Bukti navigasi salinan tidak otomatis membuktikan kontrol laporan di dalamnya.

| ID | Peran / layar | Kontrol dan prasyarat | Hasil yang diharapkan | Frame / node | Status | Bukti / langkah berikutnya |
| --- | --- | --- | --- | --- | --- | --- |
| REP-01 | Kepala/Penagihan, M | Tampilan nilai tanggal awal/akhir | Rentang sumber 1–24 September 2026 terbaca | Teks `129:3,129:4` pada `124:2`; `129:5,129:6` pada `105:2` | Diuji hasil | B3. Ini hanya bukti visual, bukan kontrol tanggal interaktif. |
| REP-02 | Sesuai akses, D/M | Tab Penjualan, Keuangan, Jurnal, Aktivitas | Konten, tab aktif dan parameter halaman sesuai | `26:5`, `105:2`, `124:2`; tujuan — | Belum terverifikasi | S2 `Reports`; tidak ada bukti perpindahan empat tab. |
| REP-03 | D/M penjualan | Ubah tanggal awal/akhir | Hasil, total dan grafik sesuai rentang; pagination reset | — | Belum dibuat | B1/B3 menyatakan pemilih tanggal/input belum direplikasi. |
| REP-04 | D/M penjualan | Rentang terbalik atau hasil kosong | Pesan invalid berbeda dari hasil kosong; CSV disabled saat invalid/kosong | — | Belum terverifikasi | S5. |
| REP-05 | D/M penjualan | Pilih sumber OMI & Smart / OMI / Smart; pembeli | Hasil dan total sesuai gabungan filter; halaman pertama | — | Belum terverifikasi | S5. |
| REP-06 | D/M penjualan | CSV sesuai filter | Hasil ekspor yang dapat diperiksa memuat semua baris terfilter | — | Belum terverifikasi | S5; bukan hanya baris pada halaman aktif. |
| REP-07 | D/M penjualan | Invoice pada baris/card | Dokumen invoice tepat; Siap ditagih bukan tautan palsu | — | Belum terverifikasi | S5. |
| REP-08 | D/M penjualan | Sebelumnya/Berikutnya | 12 baris per halaman; batas disabled | — | Belum terverifikasi | S5. |
| REP-09 | D/M keuangan/jurnal | Saldo sampai tanggal | Neraca/jurnal kumulatif sampai tanggal pilihan; pagination jurnal reset | — | Belum terverifikasi | S2; berbeda dari rentang penjualan. |
| REP-10 | D/M keuangan/jurnal | CSV neraca & jurnal; Cetak keuangan | Dokumen sesuai tanggal saldo dan bisa diperiksa | — | Belum terverifikasi | S2 `asOf`, `DocumentLink`. |
| REP-11 | D/M keuangan | Buka/tutup Piutang pembeli saat ini; Biaya operasional | Disclosure membuka/menutup rincian; label saldo operasional jelas | — | Belum terverifikasi | S2; kontrol summary juga masuk cakupan tombol. |
| REP-12 | D/M jurnal | Sebelumnya/Berikutnya | 12 jurnal per halaman, semua baris akun jurnal tetap utuh | — | Belum terverifikasi | S2. |
| REP-13 | D/M aktivitas | Sebelumnya/Berikutnya | 20 aktivitas per halaman, maksimal 100 terbaru sesuai akses | — | Belum terverifikasi | S2. |
| REP-14 | Penagihan, M periode | Geser tabel periode | Persetujuan dan Tinjau laporan terjangkau | `111:58` | Diuji hasil | B3. |
| REP-15 | Penagihan/Laporan, D/M | Ajukan laporan, bulan YYYY-MM, Simpan/Batal/X | Bulan valid diajukan; hasil review/versi terlihat | `111:2`, `32:5`; dialog — | Belum terverifikasi | S2 `Periods`. |
| REP-16 | Sesuai akses, D/M periode | Tinjau laporan / Snapshot tertutup | Dokumen periode tepat; snapshot tertutup tetap | — | Belum terverifikasi | S2; scroll yang membuat link terlihat belum membuktikan link. |
| REP-17 | Pimpinan, D/M periode | Setujui versi / Minta perbaikan | Persetujuan sesuai versi; alasan perbaikan wajib dan terlihat | — | Belum terverifikasi | S2; perlu peran dan state review. |
| REP-18 | Akuntansi, D/M periode | Posting & tutup | Setelah approved: konfirmasi lalu periode tertutup/snapshot; transaksi susulan tidak mengubah snapshot | — | Belum terverifikasi | S2. |
| REP-19 | Sesuai akses, D/M periode | Tinjau laporan transaksi pada keadaan kosong | Buka Laporan | — | Belum terverifikasi | S2; perlu state belum ada laporan. |

## Profil

Frame: Kepala D `27:5`, M `106:2`; Penagihan M `120:2`, cabang hasil `152:120`; pelanggan D `18:5`, M `47:2`. Navigasi menuju profil tercatat pada NAV; hal itu belum membuktikan aksi di dalam profil.

| ID | Peran / layar | Kontrol dan prasyarat | Hasil yang diharapkan | Frame / node | Status | Bukti / langkah berikutnya |
| --- | --- | --- | --- | --- | --- | --- |
| PRO-01 | Semua, D/M | Edit nama/kontak; jabatan internal atau alamat pelanggan | Isian berubah; Simpan aktif setelah perubahan; status belum disimpan | Frame profil di atas; kontrol — | Belum terverifikasi | S6. |
| PRO-02 | Semua, D/M | Simpan perubahan valid | Pending/disabled lalu nama/kontak/jabatan/alamat baru dan pesan tersimpan; header identitas konsisten | — | Belum terverifikasi | S6; tanpa password nyata. |
| PRO-03 | Semua, D/M | Nama kosong atau batas panjang | Error terkait field; tidak menyimpan | — | Belum terverifikasi | S6: nama wajib ≤100, kontak ≤30, jabatan ≤100, alamat ≤500. |
| PRO-04 | Semua, D/M | Batalkan perubahan saat kotor | Nilai kembali ke tersimpan, pesan pembatalan, Simpan disabled | — | Belum terverifikasi | S6. |
| PRO-05 | Semua, D/M | Ubah foto | Simulasi pilihan PNG/JPG ≤2 MB; foto/header diperbarui atau error | — | Belum terverifikasi | S6; file terlalu besar/format salah memiliki pesan tersendiri. |
| PRO-06 | Semua, D/M | Tiga ikon tampil/sembunyi kata sandi | Masing-masing field berubah tampil/tersembunyi dan label ikon tepat | — | Belum terverifikasi | S6; gunakan nilai tiruan, jangan ekspor sandi akun. |
| PRO-07 | Semua, D/M | Isi current/new/confirm; Ubah kata sandi | Current wajib; baru 12–128 karakter; confirm sama; error field tepat | — | Belum terverifikasi | S6. |
| PRO-08 | Semua, D/M | Ubah kata sandi saat data pribadi belum disimpan | Menolak dengan pesan simpan/batalkan perubahan pribadi dahulu | — | Belum terverifikasi | S6. |
| PRO-09 | Semua, D/M | Ubah kata sandi valid; Kosongkan isian | Sukses menuju login dengan pemberitahuan; kosongkan menghapus isian/errors tanpa ubah password | — | Belum terverifikasi | S6; alur simulasi tidak perlu mengubah sandi sungguhan. |
| PRO-10 | Internal, D/M | Pindah modul/keluar akun saat profil kotor | Dialog Perubahan belum disimpan; Tetap mengedit mempertahankan; Buang perubahan melanjutkan | — | Belum terverifikasi | S1 `LeaveChangesDialog`, S9. |
| PRO-11 | Semua, D/M | Sesi berakhir; Masuk kembali; gagal simpan | Isian pribadi tetap, pesan akurat; password tidak menjadi draft | — | Belum terverifikasi | S6. |
| PRO-12 | Pelanggan, D/M | Draft dipulihkan; data server berubah; Buang perubahan & gunakan data terbaru | Draft dan konflik ditampilkan, pilihan buang memakai data terbaru | — | Belum terverifikasi | S6; state khusus pelanggan, bukan wajib tampil pada profil internal biasa. |

## Administrasi

Frame administrasi desktop `33:5` dan mobile `327:2` tersedia. Frame mobile bernomor 25 memakai 390 × 844, Clip content dan gulir vertikal. Keberadaan layar tidak membuktikan kontrol administrasi berfungsi. Dashboard, menu dan profil Admin mobile belum tercatat; ledger tidak menganggap layar Kepala Toko sebagai pengganti Admin.

| ID | Peran / layar | Kontrol dan prasyarat | Hasil yang diharapkan | Frame / node | Status | Bukti / langkah berikutnya |
| --- | --- | --- | --- | --- | --- | --- |
| ADM-01 | Admin, M | Layar administrasi | Tata letak mobile sesuai akun Admin; bagian utama hingga footer terjangkau | `327:2` | Diuji hasil | B11; Present dari header/H1/tab/ringkasan/filter, baris akun, tiga divisi, tiga pemasok hingga footer lulus visual. Bukti tata letak/gulir tidak membuktikan tombol atau form berfungsi. |
| ADM-02 | Admin, D/M | Buka pengaturan akun; tiga kartu ringkasan | Akun → users dengan filter reset; Divisi/Pemasok → anchor bagiannya | — | Belum terverifikasi | S8. |
| ADM-03 | Admin, D/M | Penanda Akun/Divisi/Pemasok setelah berpindah bagian | Penanda mengikuti bagian yang dipilih | D `33:5`; M `327:2` | Belum terverifikasi | S2 `AccountSettings`; tiga gulir mobile diuji pada ADM-19, tetapi penanda mobile masih Akun. Desktop belum diuji. |
| ADM-04 | Admin, D/M | Cari akun + Cari; peran; status | Hasil berdasarkan nama/email/divisi dan gabungan filter; halaman reset | — | Belum terverifikasi | S2; pencarian dikirim dengan tombol/form, berbeda dari pembayaran yang langsung berubah. |
| ADM-05 | Admin, D/M | Hapus filter termasuk pada hasil kosong | Kembali semua akun dan bagian Akun | — | Belum terverifikasi | S2. |
| ADM-06 | Admin, D/M | Sebelumnya/Berikutnya | 12 akun/halaman; batas disabled; ringkasan terlihat | — | Belum terverifikasi | S2 `ListPagination`. |
| ADM-07 | Admin, D/M | Atur akun pada akun lain | Dialog peran/divisi/status dengan nilai awal | — | Belum terverifikasi | S2. |
| ADM-08 | Admin, D/M | Simpan Atur akun, invalid, Batal/X | PIC wajib divisi; customer tetap customer; hasil peran/status sesuai; batal tetap | — | Belum terverifikasi | S2. |
| ADM-09 | Admin, D/M | Atur akun / Atur ulang sandi pada akun sendiri | Keduanya disabled; petunjuk memakai Profil saya | — | Belum terverifikasi | S2; disabled yang benar tidak berarti tombol harus dipaksa aktif. |
| ADM-10 | Admin, D/M | Atur ulang sandi akun lain | Dialog identitas target, empat isian, Tutup/X | — | Belum terverifikasi | S7. |
| ADM-11 | Admin, D/M | Validasi/reset; Tutup; Periksa kembali | Current wajib, baru 12–128, konfirmasi sama, alasan wajib; pending terkunci; hasil tidak pasti memakai retry yang sama | — | Belum terverifikasi | S7. Nilai sandi tiruan/kosong saja dalam Figma. |
| ADM-12 | Admin, D/M | Tambah divisi; Simpan/Batal/X | Nama, kode, alamat wajib; sukses baris/jumlah bertambah | — | Belum terverifikasi | S2. |
| ADM-13 | Admin, D/M | Ubah divisi; Simpan/Batal/X | Nilai awal terisi; sukses baris berubah; alamat pesanan lama tetap | — | Belum terverifikasi | S2. |
| ADM-14 | Admin, D/M | Tambah pemasok; Simpan/Batal/X | Nama dan kategori wajib; sukses baris/jumlah bertambah | — | Belum terverifikasi | S2. |
| ADM-15 | Admin, D/M | Ubah pemasok; Simpan/Batal/X | Nilai awal terisi; sukses berubah tanpa menghilangkan hubungan pengadaan/batch | — | Belum terverifikasi | S2. |
| ADM-16 | Admin, M | Dashboard, menu dan profil Admin | Identitas/peran Admin konsisten; navigasi hanya hak akses Admin | — | Belum terverifikasi | Tidak terdaftar di B1; verifikasi keberadaan dan inventaris capture masing-masing sebelum menyimpulkan belum dibuat. |
| ADM-17 | Admin, M | Gulir tabel akun vertikal dan horizontal | Beberapa baris serta kolom status/aksi kanan terjangkau | `327:2`, tabel akun | Diuji hasil | B11; Present mencapai beberapa nama akun, kemudian geser kanan menampilkan Aktif, Atur akun, dan Atur ulang sandi. Bukti jangkauan ini bukan uji klik atau form aksi tersebut. |
| ADM-18 | Admin, M | Gulir tabel divisi; tampilkan pemasok | Alamat/Ubah divisi terjangkau; tiga kolom pemasok muat | `327:468` tabel divisi; bagian pemasok `327:526` | Diuji hasil | B11; geser divisi ke kanan memperlihatkan alamat dan Ubah. Tiga pemasok beserta tiga kolomnya tampil pada lebar 390. Tidak mencakup klik Ubah/Tambah. |
| ADM-19 | Admin, M | Klik Akun, Divisi, Pemasok pada navigasi bagian | Gulir ke bagian yang dipilih | `327:47→440:3`; `327:54→424:3`; `327:61→440:2` | Diuji hasil | B11; ketiganya Scroll to dan lulus klik/screenshot. Heading Akun/Divisi berada sekitar 110 px; Pemasok berhenti di batas bawah alami sehingga tiga pemasok/footer terlihat. Penanda aktif tetap Akun, dicatat terpisah ADM-03. |

## Kelompok yang belum diinventarisasi lengkap

Kelompok berikut tetap bagian permintaan awal. Belum lengkapnya ledger ini tidak menghapus kebutuhan tersebut:

- Beranda/etalase: seluruh CTA/header/footer, carousel, pencarian, kategori/koleksi/kemasan/stok, pengurutan dan pagination, seluruh kartu produk.
- Detail/keranjang/checkout: pilihan kemasan, tambah/kurangi jumlah, hapus barang, kosong/tersedia/habis, formulir pengiriman, validasi dan pembuatan pesanan.
- Akun/pesanan pelanggan: filter, daftar/detail, pembayaran pelanggan, pembatalan beserta keadaan sesudahnya, semua tautan dokumen/navigasi. Beberapa jalur sudah terbukti pada B6, tetapi belum disalin menjadi ledger setiap kontrol.
- Login pelanggan/staf: input, show/hide password bila ada, validasi, akses portal/peran, kesalahan sesi dan kembali. NAV-18 hanya simulasi jalur Kepala Toko.
- Katalog internal, antrean/detail pesanan, persetujuan/reservasi/pemenuhan, pengiriman/penerimaan/komplain/finalisasi.
- Persediaan, batch, retur, opname, pengadaan dan pendanaan, invoice/composer dan filter/aksi detail.
- Peran PIC, Staf, Kurir, Pengolah Laporan, Pimpinan dan Akuntansi: layar/perbedaan aksi yang tidak dapat diwakili oleh Kepala Toko/Penagihan/Admin.
- Semua instans tautan cetak/PDF/CSV/lampiran; seluruh keyboard/fokus, disclosure, empty/error/loading, overlay dan disabled states.
- Kesetaraan tampilan desktop/mobile setiap state, posisi header/footer tetap, gulir vertikal/horizontal, titik klik ikon versus label, serta akses tautan mentor dari perangkat lain.

## Urutan pengujian dan aturan peningkatan status

1. Pertahankan bukti jalur terfilter, dua siklus pilihan status (PAY-22), seluruh reset baru (PAY-48–53), dan Escape lima dialog (PAY-47/PAY-61). Lanjutkan keyboard pemilih status (PAY-57), ingatan filter lintas modul (PAY-44), pencarian/gabungan filter serta visual salinan dan heading yang belum diperiksa.
2. Lanjutkan PAY-05–07, PAY-17 dan PAY-20 sebelum memperluas transaksi alokasi. Perluas pula bukti PAY-14 untuk jalur pulang, avatar, penutup drawer, keadaan transaksi lain dan desktop. Cabang statis yang berhasil diuji tidak membuktikan seluruh kemungkinan navigasi atau form.
3. Bangun hasil filter, validasi, alokasi dan lampiran; lanjutkan laporan/profil/admin beserta padanan desktop. Jangan berhenti pada dialog yang sekadar bisa dibuka.
4. Pecah setiap baris kelompok menjadi instans saat diuji. Catat ID frame/tombol asal, prasyarat/data demo, input, hasil yang diharapkan, tujuan aktual, viewport, dan bukti screenshot/catatan Present.
5. Naikkan menjadi Diuji hasil hanya setelah hasil relevan diperiksa. Perubahan node-id cukup untuk Diuji klik, tetapi tidak cukup untuk memastikan filter atau transaksi benar.
6. Uji jalur kembali dan pengulangan. Navigasi yang mengembalikan transaksi ke keadaan lama berarti kesinambungan belum lulus meskipun konfirmasi pertama berhasil.
7. Untuk tombol disabled, buktikan keadaan yang menonaktifkan dan keadaan yang mengaktifkannya. Untuk form, minimal catat kosong/invalid, valid, pending, sukses, gagal/retry dan batal/tutup sesuai perilaku sumber.
8. Cantumkan tanggal, peran dan viewport setiap bukti tambahan. Jangan memperluas satu bukti mobile menjadi desktop, satu instans menjadi semua instans, atau satu peran menjadi semua peran.

Template penambahan bukti:

| Tanggal / ID ledger | Peran / viewport | State asal dan prasyarat | Node kontrol / input | Hasil aktual dan node tujuan | Bukti Present | Status baru / batas |
| --- | --- | --- | --- | --- | --- | --- |
| — | — | — | — | — | — | — |
