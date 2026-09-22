# Carousel koleksi v12

Komponen `CollectionCarousel` memperkenalkan tiga kelompok kebutuhan yang memang tersedia di toko: pantry, rapat, dan merchandise. Konten ini membantu orang menemukan kategori; harga, kolom pencarian, dan tombol pembelian tetap berada di bagian katalog yang tidak bergeser otomatis.

## Arah desain

Warna mengikuti Unit Toko dengan petroleum `#103F47`, putih `#FFFFFF`, silver `#EDF2F4`, teks sekunder `#52656A`, garis `#D2DCE0`, serta burnt orange `#B94D26` untuk penanda fokus. Huruf memakai Archivo Black dan DM Sans yang sudah disimpan lokal. Judul dan foto mendapat area masing-masing, bukan label absolut yang menumpuk di atas produk.

```
Pilihan kebutuhan             [Jeda] 01 / 03 [←] [→]
┌──────────────────────────────┬───────────────────┐
│ Judul koleksi                │                   │
│ Penjelasan singkat           │   Foto produk     │
│ [Lihat koleksi →]            │                   │
└──────────────────────────────┴───────────────────┘
```

Pada ponsel, foto berada di atas teks. Semua slide berbagi tinggi baris yang sama; berganti slide tidak mendorong katalog naik-turun. Kontrol berada dalam alur dokumen, bisa membungkus baris, dan mempunyai area tekan sedikitnya 44 piksel. Nomor menunjukkan posisi sebenarnya, tanpa titik dekoratif atau indikator promosi palsu.

## Riset dan keputusan

- [WAI APG Carousel Pattern](https://www.w3.org/WAI/ARIA/apg/patterns/carousel/): tombol penghenti tersedia lebih dahulu dalam urutan Tab. Fokus menghentikan rotasi sampai pengguna meminta mulai kembali. Tombol sebelumnya/berikutnya mempertahankan fokus. Slide tersembunyi tidak boleh tersisa dalam urutan fokus. Implementasi menggunakan elemen tombol asli, `inert`, label slide dan `aria-hidden`.
- [WCAG 2.2 — Pause, Stop, Hide](https://www.w3.org/WAI/WCAG22/Understanding/pause-stop-hide.html): pembaruan otomatis harus dapat dihentikan tanpa mengharuskan pengguna terus menahan fokus. Karena itu Jeda bersifat tetap, sedangkan hover hanya jeda sementara. Pilihan ini memungkinkan pengguna meneruskan interaksi di bagian halaman lain.
- [WAI Carousel Animation Tutorial](https://www.w3.org/WAI/tutorials/carousels/animations/): kontrol tidak diganti sebagai elemen baru ketika status berubah, sehingga fokus tidak hilang. Tombol tetap di DOM dan hanya teks/ikon berubah. Area `aria-live` memakai `off` saat rotasi berjalan dan `polite` ketika berhenti.
- [Embla v8 React](https://www.embla-carousel.com/docs/v8/get-started/react), [opsi](https://www.embla-carousel.com/docs/v8/api/options), [event](https://www.embla-carousel.com/docs/v8/api/events), dan [metode](https://www.embla-carousel.com/docs/v8/api/methods): memakai paket 8.6 yang sudah terpasang, tanpa menambah plugin. Efek React memasang serta membersihkan timer, observer, dan langganan. Embla menjalankan geser sentuh dengan transform dan tidak mengkloning tautan.

Dokumentasi Embla tanpa `/v8/` telah menunjuk versi prarilis v9 ketika riset dilakukan; implementasi merujuk API v8 yang sesuai dependensi proyek.

## Perilaku

- Berganti koleksi setelah enam detik. Timer hanya berjalan saat setidaknya 40% komponen terlihat, tab browser aktif, pointer tidak sedang hover, dan pengguna mengizinkan rotasi.
- Hover menahan pergantian. Saat pointer keluar, satu interval enam detik baru dimulai; tidak melompat mengejar waktu yang terlewat.
- Fokus, awal drag, dan tombol sebelumnya/berikutnya menghentikan rotasi. Rotasi baru berjalan lagi setelah menekan Mulai. Gerakan yang masih berlangsung langsung selesai ketika fokus masuk, supaya tautan yang difokuskan tidak terus bergerak.
- Preferensi `prefers-reduced-motion` menonaktifkan autoplay dan drag, serta membuat tombol pergantian manual langsung. Tombol tetap menyediakan akses ke seluruh slide tanpa gerakan sesudah jari dilepas. Perubahan preferensi ketika halaman terbuka juga didengarkan.
- Pergantian programatis menggunakan physics Embla pada nilai 20 dan dibatasi 650 ms; nilai `duration` Embla sendiri bukan milidetik. Drag mengikuti gerakan jari. Tidak ada perpindahan fokus otomatis, scroll halaman otomatis, atau animasi posisi kontrol.
- Slide tidak aktif memiliki `inert`, `aria-hidden`, dan tautan `tabIndex=-1`. Fokus berpindah melalui tombol serta tautan dengan perilaku Tab browser biasa.
- Daftar kosong tidak merender komponen. Satu slide tidak memiliki timer/drag atau kontrol yang tidak berguna. Konten slide pertama tersedia dalam HTML server.
- Label tombol bisa berubah dari Jeda ke Mulai ketika fokus menghentikan animasi; niat klik pointer direkam sebelum fokus agar klik Jeda pertama benar-benar menghentikan.

## API

`CollectionCarousel` menerima `slides?: CollectionSlide[]`, `ariaLabel?: string`, dan `className?: string`. Slide memiliki `id`, `title`, `description`, `image`, `href`, dan `linkLabel?`. Tanpa `slides`, komponen memakai tiga koleksi nyata beserta gambar produk yang sudah tersedia. CSS berada di `website/app/carousel-v12.css` dan harus diimpor oleh layout.

## Verifikasi

ESLint pada komponen dan pemeriksaan TypeScript strict terisolasi lulus. Pemeriksaan integrasi browser, pergantian otomatis, jeda/fokus, tampilan sempit, dan navigasi tautan dilakukan dalam pemeriksaan rilis v12; hasilnya dicatat di laporan rilis, bukan diasumsikan dari lint. Ini merupakan penerapan pola aksesibilitas, bukan klaim audit kepatuhan WCAG menyeluruh.
