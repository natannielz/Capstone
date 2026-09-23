# Implementasi v15 — draf profil dan pembaruan data server

23 September 2026. Cakupan terbatas pada profil bersama, navigasi akun pelanggan, helper draf profil baru, tes terkait, dan tambahan kecil CSS akun. Tidak mengubah workspace, layout, endpoint autentikasi, atau aturan transaksi. Browser, build/publikasi akhir, dan pemeriksaan integrasi dilakukan agen utama.

## Diagnosis dan keputusan

Kode sebelumnya hanya menjaga klik tautan dan `beforeunload`. Tombol Back/Forward pada navigasi SPA tidak selalu membongkar dokumen, sehingga `beforeunload` tidak cukup untuk menjaga perubahan. Profil juga memakai nilai actor hanya sebagai state awal; refresh actor tidak memperbarui kolom bersih dan tidak menjelaskan benturan dengan perubahan lokal.

Dokumentasi Next lokal yang dibaca:

- `website/AGENTS.md` dan `node_modules/next/dist/docs/01-app/01-getting-started/04-linking-and-navigating.md`: perpindahan klien mempertahankan bagian UI dan tidak identik dengan pemuatan ulang dokumen.
- `node_modules/next/dist/docs/01-app/03-api-reference/02-components/link.md`, bagian **Blocking navigation**: `onNavigate` menjaga navigasi melalui Link, bukan kontrak penahanan semua tombol sejarah browser.
- `node_modules/next/dist/docs/01-app/03-api-reference/04-functions/use-router.md`: API Back/Forward dan refresh serta pelestarian state. Tidak menambahkan intersepsi `popstate`, `history.go`, atau perulangan redirect untuk memaksa pengguna tetap di halaman.

Solusi yang dipilih adalah **pemulihan draf data profil pelanggan di tab yang sama**. Guard klik/beforeunload tetap menjaga perubahan dan kata sandi, sementara Back/Forward yang tidak dapat ditahan memiliki salinan isian profil yang dapat dipulihkan.

## Perilaku yang berubah

### Draf pelanggan

- Setiap perubahan nama, telepon, jabatan, atau alamat menulis draf secara sinkron di event input—sebelum pengguna dapat meninggalkan halaman. Penyimpanan menggunakan `sessionStorage`, tidak `localStorage`.
- Payload dibatasi pada empat kolom teks tersebut, identitas akun, baseline server asli, versi, dan waktu pembaruan. Password, token, email akun, foto/berkas, role, dan data transaksi tidak ditulis.
- Kunci serta payload sama-sama diperiksa menurut actor. Editor juga memakai key `role:id` sehingga pergantian akun langsung membuat state baru, termasuk kolom kata sandi kosong.
- Draf berlaku maksimal delapan jam sejak perubahan terakhir, dan mengikuti masa hidup penyimpanan sesi tab. Browser dapat mempertahankan sessionStorage saat memulihkan tab; batas waktu tetap diperiksa aplikasi.
- Setelah remount, kolom yang pernah diedit dipulihkan; kolom yang sebelumnya bersih mengikuti data server saat ini. Notifikasi membedakan draf tersimpan, draf dipulihkan, dan penyimpanan yang tidak tersedia.
- Setiap penulisan memiliki generasi unik. Editor menyimpan snapshot generasi yang dimilikinya; penyelesaian PATCH dan efek form bersih hanya boleh menghapus snapshot yang masih sama. Respons halaman lama tidak menghapus draf baru setelah Back/Forward, termasuk bila nilai dan milidetik penulisannya sama. Callback editor yang sudah unmount tidak memperbarui UI atau memanggil refresh; edit yang lebih baru di editor yang masih aktif juga dipertahankan.
- Mengembalikan seluruh isian ke baseline, menyimpan profil dengan sukses, membatalkan perubahan, meninggalkan profil dengan pilihan buang perubahan, serta logout normal membersihkan draf. Kata sandi tidak pernah dipulihkan dari draf.

### Pembaruan actor

- Form bersih mengikuti properti actor yang baru.
- Pada form yang sedang diedit, kolom bersih mengikuti server; kolom yang telah diubah tetap berisi perubahan lokal.
- Bila server berubah sementara ada perubahan lokal, notifikasi menjelaskan penggabungan tersebut. Tombol **Buang perubahan & gunakan data terbaru** menghapus edit lokal dan menggunakan baseline terbaru secara eksplisit.
- Nilai server yang sudah menyamai hasil trim dari draf lama dikenali sebagai data yang telah tersimpan saat pemulihan. Draf dengan spasi awal/akhir tidak menghidupkan kembali perubahan yang sebelumnya sukses disimpan.
- Perubahan ini diterapkan pada komponen Profile bersama, termasuk profil staf. Penyimpanan draf sesi tetap khusus pelanggan agar perilaku navigasi workspace tidak berubah.

### Kegagalan penyimpanan dan logout

- Setiap penulisan diverifikasi dengan pembacaan kembali. Kegagalan terlihat di form; aplikasi tidak menjanjikan pemulihan Back/Forward jika draf tidak tersimpan.
- Pembersihan mencoba menghapus entri, lalu menulis penanda kosong tanpa isi profil jika penghapusan ditolak. Bila keduanya gagal, actor ditandai telah membuang draf di memori aplikasi saat ini. Hanya ID actor yang ada dalam penanda memori tersebut.
- Buang perubahan tetap mereset form walaupun penyimpanan tidak tersedia, dengan pemberitahuan bahwa pembersihan fisik belum pasti. Pengguna tidak terjebak pada form yang tidak pernah memiliki draf tersimpan.
- **Logout selalu mencoba mencabut sesi di server terlebih dahulu.** Hasil pembersihan draf adalah keadaan terpisah. Pembersihan keranjang juga ditunggu menggunakan `allSettled`, sehingga kegagalan penyimpanan lokal tidak membatalkan redirect setelah sesi berhasil dicabut.
- Pembersihan lokal yang benar-benar gagal setelah logout atau saat meninggalkan halaman menampilkan pemberitahuan browser singkat sebelum navigasi. Tidak ada klaim bahwa data lokal terhapus bila hasilnya tidak diketahui.

## Berkas

- `website/components/profile.tsx`
- `website/components/customer-account.tsx`
- `website/lib/client/customer-profile-draft.ts` — baru
- `website/tests/customer-profile-v15.test.ts` — baru
- `website/app/account-v14.css` — gaya status/notifikasi draf

## Verifikasi yang selesai

- TypeScript aplikasi: **lulus**, `tsc --noEmit --incremental false`.
- Lint terarah: **0 error**, lima warning navigasi `window.location.assign` yang sudah ada di customer-account. Tidak menambah warning baru dari helper atau profil.
- **15 tes terkait lulus**: sembilan tes v15 dan enam regresi draf transaksi v7. Setelah perbaikan race PATCH, sembilan tes v15 dijalankan ulang dan lulus; enam regresi transaksi sudah lulus pada pemeriksaan sebelumnya.
- Tes v15 mencakup penyimpanan langsung sebelum rerender, remount komponen Profile asli, kolom bersih/dirty pada pembaruan actor, pemulihan dengan baseline lama, pembuangan ke nilai server terbaru, cross-actor, exclusion kredensial, revert, TTL, payload tidak valid, kegagalan read/write, fallback penghapusan, pengenalan nilai hasil trim, serta logout ketika penyimpanan opsional ditolak.
- Regresi respons terlambat memakai PATCH yang ditahan pada komponen Profile asli: editor lama unmount, editor lama masih terpasang, dan edit baru yang sudah masuk pada editor yang sama. Sesudah PATCH lama selesai, nilai baru dan snapshot penyimpanannya harus tetap ada, termasuk setelah remount berikutnya. Tes generasi terpisah memeriksa dua penulisan identik dalam milidetik yang sama.
- Tes regresi tetap memastikan perintah pembayaran yang hasilnya tidak pasti mempertahankan ID/payload yang sama; fitur draf profil tidak mengubah pemulihan transaksi tersebut.

## Uji browser yang perlu dicatat agen utama

1. Dari toko buka profil, ubah nama/alamat tanpa simpan, lalu gunakan Back dan Forward browser. Saat profil tampil lagi, isian lokal ada dan kata sandi tidak dipulihkan dari storage.
2. Muat ulang atau buka kembali profil pada tab yang sama: draf milik akun tersebut dipulihkan, dengan indikator jelas. Masuk sebagai akun lain tidak membawa kolom akun sebelumnya.
3. Pilih **Batalkan perubahan**, lalu Back/Forward atau reload. Isian kembali ke server; draf tidak dipulihkan lagi pada penyimpanan normal.
4. Edit nama lokal; ubah telepon dan nama actor dari sesi lain, lalu refresh data akun. Nama lokal bertahan, telepon baru muncul, dan notifikasi tersedia. **Buang perubahan & gunakan data terbaru** memakai keduanya dari server.
5. Simpan nama dengan spasi, kemudian reload. Nilai server yang telah dinormalisasi muncul tanpa draf dirty lama.
6. Pada penyimpanan yang sengaja ditolak, form menjelaskan draf tidak tersimpan; logout tetap mencabut sesi. Tidak membuat transaksi production untuk pengujian.

## Batas yang disengaja

- Draf tidak dibagikan lintas tab/perangkat dan bukan mekanisme offline untuk menyimpan ke server.
- Bila seluruh operasi penyimpanan browser ditolak setelah sebuah draf pernah ada, aplikasi tidak dapat menjamin penghapusan fisik setelah pemuatan ulang penuh. Penanda memori berlaku pada runtime saat ini, kegagalan diberitahukan, dan draf tetap memiliki batas umur. Normalisasi server mencegah perubahan yang sudah tersimpan muncul kembali hanya karena spasi.
- Kata sandi tidak diserialisasi. Guard yang ada tetap berlaku pada klik tautan dan penutupan dokumen; tidak ada klaim bahwa seluruh navigasi Back/Forward browser bisa diblokir atau bahwa kata sandi dapat dipulihkan.
- Tidak ada penguncian edit serentak di server. Penggabungan ini mencegah penggantian isian diam-diam pada UI, sementara penyimpanan tetap mengikuti endpoint profil yang ada.
