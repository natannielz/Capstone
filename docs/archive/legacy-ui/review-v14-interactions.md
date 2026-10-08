# Review independen interaksi v14

23 September 2026. Review sumber oleh agen terpisah; tidak menggunakan browser atau mengubah kode aplikasi. Cakupan: carousel, aturan visualnya, `commerce-v14.css`, dan `ShoppingProgress`.

## Temuan awal — keduanya diperbaiki dan ditinjau ulang

Patch lanjutan telah diperiksa kembali pada sumber. Kedua P2 di bawah sudah ditangani. Uraian awal dipertahankan sebagai jejak masalah; hasil review perbaikannya dicatat setelah setiap temuan. Tidak ada temuan baru yang menghambat rilis dari pembacaan patch tersebut.

### P2 — Swipe dari tautan card lolos dari penghentian autoplay

Pada `collection-carousel.tsx`, `onPointerDownCapture` mengisi `gestureStart=null` jika target berada di dalam `a` atau `button`. Ini benar untuk toolbar, tetapi juga mengecualikan tautan CTA di dalam viewport slide. Embla 8.6 yang terpasang tidak mengecualikan anchor dari drag: `DragHandler` pada paket lokal hanya melewatkan elemen `INPUT`, `SELECT`, dan `TEXTAREA`.

Akibatnya, horizontal swipe sentuh yang dimulai di CTA dapat menggeser slide tanpa pernah memasuki logika `dx > 10`. Pada touch tidak ada jeda hover, dan swipe yang membatalkan klik tidak harus memberi fokus pada anchor. Niat autoplay dapat tetap aktif setelah interaksi manual, lalu mengganti slide lagi. Ini bertentangan dengan rencana penghentian manual yang persisten.

Perbaikan: catat gesture untuk semua target di dalam viewport slide, termasuk tautan, sambil tetap mengecualikan toolbar. Atau identifikasi drag nyata melalui event Embla dan gerakan pointer. Jangan mematikan autoplay secara permanen hanya karena touchstart; scroll vertikal halaman harus tetap normal.

Pembuktian: mulai autoplay, sentuh CTA tanpa membuka link, geser horizontal lebih dari ambang, lepaskan, tunggu lebih dari enam detik. Status harus “Dijeda” dan nomor tidak berubah lagi sampai resume. Ulangi dengan swipe dari foto sebagai pembanding.

**Hasil review patch: terselesaikan pada sumber.** Pointer utama dengan tombol utama dilacak bila berada di dalam viewport, termasuk target anchor CTA. Toolbar berada di luar cakupan ini. Pengamatan `pointermove` pada fase capture window tetap berjalan ketika drag keluar dari viewport; dx di atas 10 px yang dominan horizontal mengubah niat rotasi menjadi jeda persisten. `pointerId` memastikan gerakan pointer lain tidak salah mengakhiri gesture utama. Tidak ada `preventDefault` tambahan yang mematikan link atau mengambil alih scroll.

### P2 — Timer dapat berpindah saat gesture masih ditahan

Pointer down pada foto hanya menyimpan koordinat awal. `rotating` tidak mempertimbangkan pointer sedang ditekan. Sebelum gerakan horizontal melewati 10 px, timeout masih aktif; pada tekanan diam atau gesture lambat yang melintasi akhir hitung mundur, `move(1)` dapat berjalan di bawah jari. Hal ini juga dapat terjadi sebelum browser menyerahkan gesture vertikal kepada scroll halaman. Timeout pemaksa penyelesaian 650 ms tidak dibatalkan hanya oleh pointer down.

Perbaikan: gunakan hambatan sementara selama pointer aktif di viewport, bersihkan penyelesaian transisi pada awal gesture, lalu lepaskan hambatan pada pointer up/cancel. Gerakan horizontal nyata mengubahnya menjadi jeda manual persisten; gerakan vertikal atau tap nonaksi dapat melanjutkan autoplay. Pastikan pointer yang keluar viewport tidak membuat status tertahan selamanya; cleanup perlu mengikuti akhir gesture yang sebenarnya.

Pembuktian: tunggu hampir enam detik lalu tekan diam di foto lebih dari satu detik. Slide harus tetap stabil selama ditekan. Setelah gesture vertikal/cancel, autoplay dapat lanjut; setelah horizontal swipe, tetap dijeda. Periksa mouse dan touch secara terpisah.

**Hasil review patch: terselesaikan pada sumber.** `pointerHeld` kini menjadi syarat penghentian timer, dan pointer down langsung membersihkan timeout penyelesaian transisi. `pointerup`/`pointercancel` dengan id yang sesuai melepas hambatan sementara; window blur juga membersihkan gesture yang kehilangan akhir interaksi. Pointer leave hanya membersihkan hover dan tidak lagi melupakan gesture aktif. Release tidak mengubah `requestedRotation`, sehingga horizontal drag atau fokus tetap dijeda; gerakan vertikal dapat kembali otomatis. Semua listener window dilepas pada cleanup. Release capture sebelum event click tidak mengubah niat tombol toolbar karena toolbar tidak memulai gesture.

Kedua temuan dan penilaian perbaikannya merupakan hasil penalaran kode serta perilaku paket yang terpasang, bukan reproduksi browser oleh agen reviewer. Agen utama telah menerima langkah reproduksinya. Pengujian touch asli dan penggantian preferensi reduced motion tidak tersedia melalui alat browser yang sedang digunakan; hal tersebut tetap merupakan batas verifikasi, bukan klaim sudah diuji.

## Bagian yang sudah ditangani dengan tepat

- Scheduler berulang menjadwalkan advance berikutnya secara eksplisit, sehingga tidak lagi bergantung pada perubahan `selected` semata. `engineRevision` memulai ulang penjadwalan saat reInit, termasuk ketika index tidak berubah.
- ReInit membersihkan timeout penyelesaian, membaca jumlah snap aktual, dan tidak mengubah niat jeda pengguna. Unmount melepas listener Embla, observer, timeout utama, timeout penyelesaian, serta animation frame awal.
- Readiness memakai lebih dari satu snap aktual, bukan hanya jumlah item data. Target perpindahan menggunakan jumlah snap engine saat tindakan terjadi.
- Intersection mengamati viewport card dengan batas 5%, menggunakan entri batch terakhir. Ini menangani masalah section tinggi jauh lebih baik daripada batas 40% seluruh section.
- Pointer pada bagian foto kosong tidak lagi menjadi hover blocker. Status singkat membedakan autoplay, pilihan kontrol, jeda manual, reduced motion, dan area yang belum terlihat.
- Resume eksplisit dapat mengabaikan hover yang sedang aktif. Handler menyimpan niat pointer sebelum event fokus, sehingga klik Pause tidak berubah menjadi Play akibat race fokus/click.
- Fokus baru menghentikan rotasi secara persisten dan menyelesaikan posisi slide. Blur tidak memulai ulang secara otomatis.
- Preferensi reduced motion menghentikan autoplay dan mengubah perpindahan menjadi seketika. Keadaan jeda eksplisit tetap disimpan.
- Slide tidak aktif memakai `inert`, `aria-hidden`, dan tautan yang tidak masuk urutan Tab. Live region tidak mengumumkan pergantian otomatis.
- Pembatasan hover pada kontrol merupakan keputusan rencana yang didokumentasikan sebagai perbedaan dari pola APG all-hover. Review ini tidak menyebut implementasi patuh penuh APG.

## Review commerce dan tahapan belanja

Tidak ditemukan bug fungsional baru yang dapat dipastikan hanya dari sumber pada bagian ini.

- `ShoppingProgress` memakai navigation landmark, ordered list, dan `aria-current="step"` pada tahap sekarang. Panah dekoratif disembunyikan dari pembaca layar. Mode beli langsung mengarah kembali ke produk, sedangkan mode keranjang kembali ke cart; tautan “Pesanan saya” tetap terpisah dari urutan tahap.
- Filter chip menggunakan batas lebar dan pembungkusan kata; toolbar serta tahapan belanja dapat membungkus. Ini mengurangi risiko memaksa halaman melebar.
- Pada mobile, quick-add memiliki target 44 px dan label aksesibel tetap berasal dari tombol; hilangnya teks visual tidak menghapus nama tombol. Tombol pembelian detail mewarisi `white-space:normal`, sehingga label panjang dapat membungkus.
- Grid varian memakai kolom `minmax(0,1fr)` pada mobile; elemen harga dan unit dapat membungkus. Tabel harga nyata tetap lebih penting daripada memberi tinggi tetap pada kartu varian.
- Cart mendapat susunan ulang khusus di bawah 370 px, sehingga kontrol kuantitas tidak dipaksa ke kolom sempit di samping foto.
- Ruang untuk bilah pembelian tetap dan safe-area masih bergantung pada aturan bersama halaman toko. Tidak ada bukti statis cukup untuk menjamin bebas overlap; agen utama perlu memeriksa footer/CTA pada perangkat pendek dan pesan error yang panjang.

## Verifikasi yang masih dibutuhkan di browser

Agen utama sudah melaporkan autoplay 01 → 02 → 03 ketika pointer berada di foto, jeda manual bertahan lebih dari enam detik, dan resume bekerja meski pointer masih di kontrol. Review ini tidak mengulang klaim tersebut sebagai tes yang dilakukan reviewer.

Prioritas lanjutan: kasus gesture mouse yang tersedia di alat browser, resize di tengah transisi, fokus keyboard setelah resume, serta 320/390/768 px untuk varian, cart, checkout, subtotal panjang, dan bilah pembelian. Touch asli dan perubahan reduced motion saat runtime dicatat sebagai review sumber bila tidak bisa dijalankan. Periksa daftar hasil dan tindakan nyata; lulus TypeScript tidak membuktikan geometri atau perilaku pointer.
