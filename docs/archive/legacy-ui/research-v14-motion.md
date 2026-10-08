# Diagnosis autoplay dan rencana gerakan v14

23 September 2026. Tahap riset; dokumen ini belum menyatakan bug telah diperbaiki. Reproduksi browser oleh agen utama dicatat terpisah di bawah.

## Rencana pemeriksaan sebelum perubahan

1. Cocokkan ikon dan progres dengan syarat nyata yang menjalankan timer.
2. Reproduksi empat situasi terpisah: pointer di dalam/luar carousel, bagian yang hanya sedikit terlihat, tab browser yang kembali aktif, dan perubahan ukuran.
3. Bedakan jeda yang disengaja pengguna dari hambatan sementara, kemudian tentukan status yang dapat dibaca tanpa tombol besar.
4. Perbaiki penjadwalan agar tidak bergantung sepenuhnya pada perubahan nomor slide; verifikasi perpindahan berulang dan pemulihan sesudah resize.
5. Uji alur fokus, reduced motion, swipe, jeda manual, navigasi halaman, dan tampilan mobile sebelum publikasi.

Riset ini memeriksa `components/collection-carousel.tsx`, `app/carousel-v12.css`, pemanggil di `components/shop/storefront.tsx`, serta gerakan pembungkus. Paket lokal `embla-carousel` dan `embla-carousel-react` sama-sama versi **8.6.0**. Dokumentasi daring yang relevan kini berada di jalur `/docs/v8/`; jalur lama `/api/` dan `/plugins/` mengembalikan 404 saat riset. Tidak perlu migrasi ke versi lain untuk memperbaiki perilaku ini.

## Temuan terpenting

Screenshot berisi ikon Pause dan `01 / 03` **tidak membuktikan timer sedang berjalan**. Pada kode sekarang, ikon menampilkan niat autoplay (`requestedRotation`), sedangkan timer memerlukan semua kondisi berikut:

```text
API tersedia
AND jumlah data slide > 1
AND requestedRotation
AND bukan reduced motion
AND tab terlihat
AND minimal 40% seluruh section carousel terlihat
AND pointer tidak sedang di dalam section
```

Karena ikon hanya memeriksa sebagian kondisi itu, pengguna dapat melihat Pause ketika timer bahkan belum dipasang. Ini temuan langsung dari kode. Gambar statis saja tidak merekam pointer, fokus, visibility, intersection, atau timer.

**Reproduksi agen utama:** pada `/shop` lokal dengan viewport 1280 × 720, autoplay mula-mula berpindah normal. Scroll dengan pointer berada pada koordinat `[600,560]` di dalam card menghasilkan `:hover=true`, `data-rotating=false`, label ikon tetap “Jeda pergantian koleksi otomatis”, dan tidak ada fokus pada carousel. Ini mengonfirmasi penghentian karena hover tanpa penjelasan sebagai penyebab perilaku yang dilaporkan. Tidak tepat menyebut seluruh autoplay rusak. Celah reInit dan threshold kecil tetap merupakan risiko sumber yang perlu diuji sendiri.

### Prioritas penyebab dan bukti

| Prioritas | Temuan dari kode | Pengaruh terhadap laporan pengguna | Cara memastikan |
| --- | --- | --- | --- |
| P1 | `onPointerEnter` pada seluruh section mengatur `hovered=true`; `rotating` mensyaratkan `!hovered`. | Pointer diam di foto, teks, atau toolbar menghentikan pergantian tanpa mengubah ikon Pause. **Direproduksi agen utama** dengan langkah dan keadaan yang dicatat di atas. | Pastikan carousel cukup terlihat; tempatkan pointer di luar, tunggu lebih dari 6 detik; bandingkan saat pointer di dalam. |
| P1 | `inView` hanya benar jika `intersectionRatio >= 0.4` terhadap section utuh, termasuk toolbar dan isi. | Pada tampilan sebagian, zoom, atau viewport pendek, konten terlihat tetapi tidak mencapai batas yang dibutuhkan. Ikon Pause masih sama. | Ukur bounds section, bagian yang terlihat, tinggi viewport, serta `data-rotating`; ulangi ketika card masuk lebih penuh. |
| P1 | Tidak ada teks keadaan ketika autoplay tertahan sementara. | Jeda yang disengaja demi keterbacaan tampak sama dengan kegagalan. Progress bar juga kembali kosong sehingga tidak menjelaskan sebab. | Periksa perubahan UI pada hover, fokus, jeda manual, dan reduced motion. |
| P2 | Timeout hanya dipasang ulang ketika `[rotating, current, move]` berubah. `reInit` hanya memperbarui selected index. | Jika timeout terjadi ketika engine belum dapat berpindah snap, selected index tidak berubah dan timeout habis tanpa penjadwalan berikutnya. `reInit` dengan index sama juga tidak memicu render baru. Ini celah ketahanan yang nyata secara struktur, tetapi kejadian produksinya belum dibuktikan. | Uji ukuran awal sempit/tersembunyi lalu membesar; rekam jumlah snap dan siklus timer setelah reInit. |
| P2 | Hover masuk membatalkan timeout; hover keluar memulai lagi 6 detik penuh. | Beberapa kunjungan pointer singkat dapat membuat slide pertama tampak terus diam. Tidak ada informasi bahwa waktu tunggu diulang. | Tunggu 5 detik, hover sebentar, keluar; lihat bahwa perpindahan baru terjadi sesudah 6 detik lagi. |
| P2 | Embla `pointerDown` langsung mematikan niat rotasi, sebelum terbukti ada drag. | Tap pada area foto atau awal gerakan scroll sentuh dapat membuat autoplay berhenti permanen sampai dilanjutkan. Dalam keadaan ini ikon seharusnya Play, sehingga tidak cocok dengan Pause pada screenshot kecuali keadaan berubah setelah gambar diambil. | Tap foto tanpa membuka link, lakukan scroll vertikal di atas card, lalu periksa status dan ikon. |
| P3 | Batas paksa transisi 650 ms terpisah dari siklus Embla. ReInit tidak secara khusus membersihkan timeout penyelesaian tersebut. | Resize saat transisi dapat diikuti lompatan ke target lama. Ini masalah kelancaran potensial, bukan penjelasan utama slide pertama diam. | Resize ketika card berpindah dan periksa tidak ada loncatan balik atau indeks yang salah. |

Fokus keyboard dan jeda manual berbeda dari hover: kode mengubah `requestedRotation=false` secara permanen sampai dilanjutkan. Perilaku ini sengaja menjaga pembacaan, dan tidak boleh dihapus hanya agar carousel tampak selalu aktif. Reduced motion juga sengaja meniadakan autoplay. Tidak ditemukan bukti pembungkus GSAP menahan transform carousel: pembungkus saat ini hanya menggerakkan judul dan foto produk katalog, bukan `.collection-carousel-track`.

## Sumber primer dan arti praktisnya

| Sumber | Temuan yang relevan | Keputusan untuk Unit Toko |
| --- | --- | --- |
| [Embla v8 — Options](https://www.embla-carousel.com/docs/v8/api/options) | `duration` menggunakan simulasi fisika, bukan milidetik. Opsi reaktif dapat menginisialisasi ulang carousel. Loop dapat dinonaktifkan otomatis bila isi tidak cukup. | Jangan menganggap `duration:20` berarti 20 ms. Periksa snap yang benar-benar tersedia; tiga data item tidak otomatis membuktikan tiga posisi geser. |
| [Embla v8 — Events](https://www.embla-carousel.com/docs/v8/api/events) | `select` terjadi ketika snap terpilih berubah; resize memicu reInit. Listener perlu dilepas dengan callback yang sesuai. | Gunakan reInit sebagai peristiwa pemulihan ukuran dan timer, bukan hanya kesempatan membaca index. Bersihkan listener dan timer pada unmount. |
| [Embla v8 — Methods](https://www.embla-carousel.com/docs/v8/api/methods) | `scrollSnapList` menyediakan posisi snap yang nyata; `selectedScrollSnap` memberi index yang terpilih. | Eligibility autoplay menggunakan engine siap dengan lebih dari satu snap. Perubahan jumlah snap harus memperbarui penjadwalan dan kontrol. |
| [Embla v8 — Autoplay](https://www.embla-carousel.com/docs/v8/plugins/autoplay) | Plugin menyediakan keadaan bermain, sisa waktu, dan event timer. Opsi interaksi serta fokus mempengaruhi restart. | Bila memakai plugin, tampilkan progress dari timer plugin, bukan jam CSS terpisah. Default plugin bukan pengganti kebijakan keyboard aplikasi. Komponen sekarang tidak menggunakan plugin; ketiadaan plugin sendiri bukan bug. |
| [W3C APG — Carousel](https://www.w3.org/WAI/ARIA/apg/patterns/carousel/) | Rotasi berhenti ketika pointer berada di carousel. Fokus menghentikan rotasi sampai pengguna memulai kembali. Kontrol rotasi berada sebelum slide dalam urutan fokus. | Pertahankan perlindungan ini. Solusi untuk kesan macet adalah status dan penjadwalan yang benar, bukan menghapus semua jeda. Ikon kecil cukup; tombol besar tidak diwajibkan. |
| [W3C — Intersection Observer](https://w3c.github.io/IntersectionObserver/) | Rasio berasal dari luas intersection dibagi luas target; callback mengikuti perubahan threshold/intersection. Satu batch dapat berisi pembaruan yang perlu dibaca sesuai waktunya. | Mengamati section tinggi pada ambang 40% berbeda dari memastikan ada konten carousel yang terlihat. Gunakan target card/viewport yang tepat dan nilai terbaru, bukan secara membabi buta entri pertama. |
| [MDN — Page Visibility API](https://developer.mozilla.org/en-US/docs/Web/API/Page_Visibility_API) | Browser membatasi timer pada tab latar. Fokus jendela tidak sama dengan visibility dokumen. | Hentikan penjadwalan ketika tab tersembunyi; saat kembali, lanjutkan hanya jika niat autoplay masih aktif. Jangan mengejar beberapa slide yang terlewat. |

## Model perilaku yang disarankan

Pisahkan **niat pengguna**, **hambatan sementara**, dan **jam yang benar-benar aktif**. Satu boolean `requestedRotation` tetap berguna, tetapi tidak cukup menjadi satu-satunya informasi UI.

| Keadaan | Teks pendek dekat angka slide | Perilaku |
| --- | --- | --- |
| Engine siap, card terlihat, autoplay diizinkan | “Bergeser otomatis” | Timer dan progress berjalan dari jam yang sama. |
| Pointer berada di carousel | “Dijeda saat diarahkan” | Timer berhenti sementara; keluar dari card melanjutkan sesuai kebijakan waktu yang jelas. |
| Pengguna memilih jeda atau fokus/manual navigation menghentikan | “Dijeda” | Tidak mulai sendiri saat blur, resize, atau kembali dari tab lain. Ikon lanjut tersedia. |
| Reduced motion | “Gerakan dikurangi” | Pergantian manual tetap berfungsi tanpa animasi; autoplay tidak aktif. |
| Belum siap atau hanya satu snap | Tidak perlu mengiklankan autoplay | Kontrol nonfungsional disembunyikan/dinonaktifkan; isi tetap terlihat. |
| Area di luar layar atau tab tersembunyi | Tidak perlu pengumuman berkala | Timer berhenti. Ketika kembali, evaluasi niat pengguna dan syarat lainnya. |

Status ini adalah teks biasa yang tenang, bukan toast dan bukan countdown yang diumumkan setiap detik. Jangan memakai `aria-live` agresif untuk hover. Tombol tetap ikon kecil dalam target sentuh yang layak; nama aksesibelnya menjelaskan aksi berikutnya. Bila icon Pause dipertahankan ketika hover karena niat autoplay masih aktif, teks harus menerangkan jeda sementara. Mengubah ikon berdasarkan `rotating` saja tanpa memperbaiki handler berisiko membuat tombol menawarkan “lanjut” tetapi hanya menonaktifkan niat yang sebenarnya masih aktif.

### Pilihan cakupan hover setelah reproduksi

Pilihan yang paling dekat dengan APG adalah mempertahankan jeda pada seluruh carousel, menampilkan status jujur, dan memperjelas resume. Namun ini tetap berarti kursor yang kebetulan berada di atas foto menghentikan autoplay. Berdasarkan permintaan pengguna agar slideshow terasa otomatis, tim dapat memilih **hover hanya pada tautan atau kontrol interaktif**, sehingga pointer di area foto kosong tidak mengganggu timer. Ini merupakan penyimpangan yang disengaja dari rekomendasi APG untuk menghentikan pada hover seluruh carousel; jangan melaporkannya sebagai kepatuhan lengkap terhadap pola tersebut.

Jika cakupan hover dipersempit, wajib tetap ada jeda manual yang persisten, penghentian keyboard sampai resume eksplisit, perlindungan reduced motion, dan teks status yang tepat. Pembaca yang menggunakan pointer masih dapat memakai ikon jeda untuk membaca seluruh card. Resume eksplisit dapat mengabaikan hambatan hover saat itu sampai pointer keluar lalu masuk kembali, supaya klik lanjut benar-benar melakukan sesuatu; keputusan ini perlu diuji pada kontrol pointer dan keyboard serta ditulis dalam perilaku akhir.

### Eligibility yang tidak terlalu mudah menganggap carousel tidak terlihat

Amati viewport isi card, bukan seluruh section editorial. Pilihan awal yang layak diuji adalah threshold kecil, misalnya 0.05–0.1, sehingga card yang terlihat sebagian secara wajar dapat bergerak; berhenti ketika tidak beririsan. Nilai tersebut keputusan desain, bukan ketentuan standar. Untuk card tinggi pada zoom besar, evaluasi berdasarkan bagian terlihat dari viewport, bukan menuntut persentase besar seluruh card. Hindari pemeriksaan piksel yang tidak punya threshold/listener untuk memperbaruinya.

Jangan langsung mengubah threshold demi menutupi layout terlalu tinggi. Screenshot desktop dan mobile harus memperlihatkan apakah judul, card, serta kontrol berada pada posisi yang masuk akal. Konfirmasi juga ada ruang baca yang cukup sebelum perpindahan.

### Jam yang tahan terhadap resize dan interaksi

Pilih satu pemilik penjadwalan: custom scheduler yang kecil **atau** plugin Autoplay versi yang sesuai. Jangan menjalankan keduanya. Untuk perubahan minimum, custom scheduler dapat dipertahankan dengan deadline/remaining-time yang eksplisit, satu timeout aktif, dan pembaruan pada reInit walaupun index tidak berubah.

- Timeout menjadwalkan siklus berikutnya berdasarkan engine yang siap, bukan hanya berharap event `select` mengubah state.
- Jika snap belum tersedia, tunggu reInit/ready; jangan polling terus atau memanggil next berulang dalam loop rapat.
- ReInit membersihkan penyelesaian transisi lama, menyinkronkan jumlah snap, dan memulai jam baru hanya jika niat pengguna masih mengizinkan.
- Jeda sementara dapat menyimpan sisa waktu agar tidak selalu mengulang enam detik. Gunakan jeda minimum pendek ketika kembali agar slide tidak melompat seketika tepat setelah pointer keluar. Pilihan ini harus konsisten dengan progress.
- Jeda manual, fokus, dan navigasi manual tidak boleh dihapus oleh resize/visibility. Tidak ada timer yang menghidupkan kembali autoplay tanpa permintaan pengguna.
- Satu siklus berganti sekali. Tab yang kembali aktif tidak memutar beberapa slide untuk mengejar waktu yang lewat.
- Progress mengikuti deadline aktual; ketika berhenti, tampilkan keadaan statis. Jangan membiarkan progress penuh sementara timer sudah habis dan tidak dipasang ulang.

## Matriks pembuktian sebelum rilis

1. **Autoplay nyata:** halaman baru, card terlihat, pointer di luar, tanpa fokus di carousel. Amati minimal 01 → 02 → 03 → 01; satu perpindahan tidak cukup membuktikan scheduler berulang.
2. **Hover:** amati teks jeda, nomor tetap, progress berhenti; setelah keluar, perpindahan berlanjut dan status kembali benar.
3. **Viewport pendek:** 320/390 px lebar, zoom besar, serta desktop pendek; card sebagian terlihat tidak tertahan oleh section yang terlalu tinggi.
4. **Fokus:** Tab ke kontrol/card menghentikan; Tab keluar tidak memulai; aktivasi ikon lanjut melalui keyboard memulai sesuai pilihan.
5. **Jeda manual:** tetap dijeda setelah resize, berpindah tab, dan kembali scroll. Ikon dan nama aksesibel sesuai tindakan.
6. **Reduced motion:** awal statis; prev/next tetap bekerja langsung; perubahan preferensi ketika halaman terbuka menghentikan animasi dan timer.
7. **Resize:** ketika countdown dan ketika transisi, hitung indeks/progress sesudah ukuran baru. Tidak ada lompatan ke target lama dan tidak ada timer mati.
8. **Sentuhan:** swipe nyata, tap foto, dan scroll vertikal di atas card diperiksa terpisah; status menjelaskan keputusan berhenti. Jangan menghalangi scroll halaman.
9. **ReInit tanpa select:** uji engine yang awalnya belum mempunyai beberapa snap lalu siap dengan index sama. Autoplay pulih tanpa klik tambahan.
10. **Navigasi ulang:** masuk/keluar `/shop` beberapa kali tidak menambah kecepatan perpindahan atau meninggalkan listener/timer.

Pengujian status/scheduler dengan timer terkontrol berguna untuk kasus 5, 7, dan 9; pengujian sumber atau HTTP saja tidak dapat membuktikan bahwa carousel benar-benar bergerak. Riset ini tidak menganggap hover sebagai bug standar: bug pengalaman utamanya adalah penghentian yang tidak terjelaskan, syarat visibility yang terlalu ketat untuk sebagian layout, dan jalur pemulihan timer yang belum kuat. Penyebab faktual kejadian pengguna harus dicatat setelah reproduksi browser.
