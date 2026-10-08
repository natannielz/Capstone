# QA keandalan v6

17 September 2026. Pemeriksaan independen menjelang rilis, khusus kompatibilitas tanggal formulir operasional, refresh/logout, dan perbaikan login prahidrasi. Tidak melakukan mutasi produksi, tidak mengendalikan browser bersama, dan tidak membaca kredensial akun.

## Kesimpulan

Tidak ditemukan blocker baru dalam cakupan ini. Lima skenario handler refresh/logout lulus. Dua uji login baru lulus dan tersimpan di `website/tests/login-v6.test.ts`. Pemeriksaan tanggal melalui pembacaan kode menunjukkan formulir operasional tetap dapat mengirim tanggal yang memenuhi aturan domain v6. Ada satu penyempurnaan UX tanggal sumber masa depan yang belum diperlukan untuk keamanan data.

Lint terarah untuk test login dan invoice composer lulus. Typecheck proyek lulus sebelum pergantian nama variabel lokal test yang hanya memperbaiki lint. Agen utama melaporkan seluruh runner berisi 86 uji lulus, termasuk dua uji login baru. Bukti browser normal dan build/deploy dikelola agen utama, bukan pengujian dalam dokumen ini.

## Metode dan batas bukti

Pengujian refresh/logout mengeksekusi `Workspace` asli dari source TypeScript dalam Node, dengan penyimpanan hook sederhana agar handler dan hasil render dapat diperiksa. Komponen dekoratif diganti penanda; efek mount/navigasi tidak dijalankan. `requestJson`, `singleFlight`, `logoutSession`, dan `cart-storage` memakai implementasi asli. Fetch, localStorage, sessionStorage, Web Locks, serta `window.location.assign` disimulasikan. Respons lambat dikendalikan promise tertunda; kegagalan menggunakan HTTP 503 dan penolakan fetch.

Ini membuktikan perilaku handler, state, pencegahan permintaan ganda, dan urutan pembersihan penyimpanan terhadap redirect. Ini bukan bukti intersepsi jaringan pada browser, tata letak visual, atau perilaku native cookie. Tidak ada permintaan dikirim ke server. Percobaan awal harness memakai argumen fallback draft yang tidak lengkap; setelah disesuaikan dengan kontrak helper, kelima skenario lulus.

Test login menggunakan React `renderToStaticMarkup` asli untuk kontrak server. Pengujian sesudah hidrasi memakai hook/fetch/window simulasi dengan komponen Login, helper request, dan pembatas tujuan navigasi asli. Komponen UI anak dipetakan ke elemen HTML untuk mengisolasi kontrak formulir. Test memakai nilai kredensial acak lokal, tidak menampilkan nilainya dan tidak menghubungi endpoint akun.

## Hasil refresh dan logout

Sumber: `website/components/workspace.tsx:97`, `website/components/workspace.tsx:103`, `website/components/workspace.tsx:118`, `website/components/workspace.tsx:130`, `website/components/workspace.tsx:134`, `website/components/workspace.tsx:135`, `website/lib/client/requests.ts:20`, `website/lib/client/requests.ts:28`, `website/lib/client/cart-storage.ts:403`.

| Skenario | Pemeriksaan dan hasil |
| --- | --- |
| Refresh lambat lalu 503 | Tiga pemanggilan handler hanya menghasilkan satu fetch. Tombol disabled dan status memperbarui muncul. Sesudah 503, objek state sebelumnya dipertahankan, peringatan muncul, tombol aktif kembali, dan tidak ada redirect. Lulus. |
| Coba kembali setelah refresh gagal | Permintaan kedua menerima state revisi baru. State diperbarui, pesan gagal hilang, dan status berhasil muncul. Lulus. |
| Logout lambat lalu 503 | Dua pemanggilan handler hanya menghasilkan satu fetch. Tombol keluar disabled selama menunggu. Kegagalan tidak menghapus keranjang/draft dan tidak redirect. Peringatan serta tombol coba keluar kembali tersedia. Lulus. |
| Logout gagal karena koneksi | Retry yang fetch-nya ditolak tetap mempertahankan workspace, keranjang, dan draft. Pesan menyatakan sesi mungkin masih aktif; retry tetap dapat ditekan. Lulus. |
| Logout retry berhasil | Respons berhasil diikuti pembersihan keranjang memory/localStorage dan draft sessionStorage melalui helper asli. Callback redirect memeriksa bahwa semuanya sudah bersih sebelum menerima `/login`. Tepat satu redirect. Lulus. |

## Login prahidrasi dan percobaan ulang

Agen utama menemukan native submit sebelum handler terpasang pada browser lokal, lalu memperbaiki Login. Dokumen ini tidak menyatakan insiden yang sama terjadi pada produksi.

Sumber: `website/components/login.tsx:15`, `website/components/login.tsx:22`, `website/components/login.tsx:41`; test `website/tests/login-v6.test.ts:58` dan `website/tests/login-v6.test.ts:75`.

1. **Render server:** form secara eksplisit `method="post"`; `aria-busy` aktif; input email/password kosong dan disabled; tombol submit disabled dengan keterangan menyiapkan halaman. Menghapus POST atau mengubah snapshot server menjadi siap akan menggagalkan test.
2. **Sesudah hidrasi:** input aktif. Pemanggilan handler sebelum siap menghasilkan nol request. Dua submit cepat sesudah siap menghasilkan satu POST ke path tetap `/api/auth/login`, payload JSON, dan signal batas waktu. Kegagalan koneksi menampilkan pesan serta membuka retry tanpa redirect. HTTP 401 menandai kedua input invalid. Retry berhasil menuju halaman pesanan yang diizinkan. Seluruh request memakai POST dan tidak menempatkan nilai input pada URL.

Kedua uji lulus. Bundler esbuild dari proses subagen sempat ditolak sandbox saat membaca direktori induk; runner alternatif transpile in-memory menjalankan kedua uji dengan sukses. Agen utama kemudian menjalankan runner proyek yang mencakup kedua test, seluruh 86 uji lulus. Tidak ada perubahan konfigurasi bundler untuk mengatasi batas sandbox tersebut.

## Kompatibilitas tanggal formulir operasional

| Formulir | Kontrak UI dan aturan domain yang diperiksa |
| --- | --- |
| Tanggal bersama | `workspace.tsx:151`–`165`: ActionDialog mendapatkan tanggal saran setelah periode tertutup; input tanggal pencatatan tetap editable dan dikirim sebagai `command.date`. Perubahan tanggal membuat ID command baru, retry tanpa perubahan mempertahankan ID. Error server ditampilkan di dialog sehingga pengguna dapat memperbaiki tanggal. |
| Pendanaan dan konfirmasi pengadaan | `operations.tsx:38`–`41` mengirim ID pengadaan serta amount bila diperlukan. Engine `159`–`163` menjaga urutan pengajuan → persetujuan → dana tersedia → konfirmasi. Tidak ada field tanggal internal baru yang wajib dikirim UI; engine mengisinya. Peran dan status tombol sesuai izin command. |
| Penerimaan pemasok | `operations.tsx:42` mengirim jumlah aktual positif terpilih, kode batch opsional, dan expiry opsional per baris. Engine `164`–`169` memakai tanggal command, menolak tanggal sebelum konfirmasi/penerimaan/pembayaran terdahulu dan expiry tidak sesudah tanggal penerimaan. UI tidak mencampur expiry dengan tanggal pencatatan. |
| Pembayaran pemasok | `operations.tsx:43` mengirim purchaseId, amount sisa maksimal, dan reference. Engine `171`–`172` memeriksa tanggal terhadap konfirmasi, angsuran dan penerimaan sebelumnya. Tanggal efektif tetap dapat diperbaiki melalui ActionDialog. |
| Retur dan pelepasan barang | `inventory-workspace.tsx:54`–`70` mengirim batch/id, qty, fromHeld boolean, alasan/hasil pemeriksaan. Engine `174`–`183` memeriksa tanggal batch/pengajuan serta kelayakan barang. Field UI mendukung data yang dibutuhkan. |
| Opname dan hitung ulang | `inventory-workspace.tsx:46`–`50` mengirim batchId, counted, reason, dan replacesId saat hitung ulang. `74`–`76` memakai status efektif untuk tindakan. Engine `185`–`205` menyimpan tanggal, snapshot mutasi, dan relasi pengganti; tanggal tidak boleh mendahului hitung/mutasi. Status usang tidak menawarkan persetujuan. |

Pembacaan ini melengkapi uji domain v6 yang sudah mencakup penolakan kronologi secara atomik, pencatatan mundur sah pada periode terbuka, dan opname usang. Tidak menjalankan lagi seluruh alur di browser dalam subtask ini.

### Catatan UX nonblocker

Tanggal saran ActionDialog mengikuti hari ini/periode terbuka, belum dihitung dari tanggal sumber spesifik. Bila konfirmasi pembelian atau mutasi batch dicatat pada tanggal masa depan, default dapat mendahuluinya. Server menolak dengan pesan kronologi dan pengguna dapat mengubah tanggal lalu menyimpan kembali; tidak terjadi pencatatan tanggal salah. Penyempurnaan berikutnya dapat mengusulkan `max(hari ini, awal periode terbuka, tanggal aktivitas sumber)` dan menampilkan tanggal minimal. Tetap izinkan tanggal historis yang sah di periode terbuka; jangan mengunci semua tindakan ke hari ini.

## Penyesuaian teks invoice yang diizinkan

`website/components/invoice-composer.tsx:60`–`61` diperbarui terbatas sesuai temuan browser agen utama:

- Saat 100 baris terpilih dan masih ada sisa, petunjuk menyatakan batas tercapai, terbitkan invoice ini lalu buat invoice berikutnya.
- Saat seluruh baris halaman dipilih, tombol menyatakan semua telah dipilih; halaman kosong menyatakan tidak ada baris untuk dipilih.

Tidak mengubah seleksi, tanggal, harga, ID retry, handler penerbitan, CSS, atau source produksi lainnya dalam subtask QA ini.
