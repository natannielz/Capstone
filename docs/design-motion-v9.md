# Gerak landing dan login v9

22 September 2026. Animasi memperjelas urutan membaca halaman Unit Toko: headline, foto kebutuhan pantry, pilihan akses, lalu layanan. Warna petroleum, putih, dan aksen oranye serta huruf Archivo/DM Sans tetap memakai identitas yang ada. Tidak ada intro layar penuh, scroll yang ditahan, elemen berputar, atau animasi berulang.

## Perilaku

- **Landing:** dua baris headline masuk sejauh 14–26 piksel selama 0,78 detik. Keterangan masuk dengan jeda pendek; foto utama menyelesaikan pembesaran awal dalam 1,1 detik. Teks tidak pernah dimulai dari transparan penuh.
- **Bagian berikutnya:** judul, kartu akses, koleksi, dan penjelasan layanan masuk sekali saat bagian mencapai 88% tinggi layar. Gerak 14–24 piksel, durasi 0,62 detik, jeda antarelemen 0,06–0,09 detik. Konten tetap terlihat sebelum bagian tersebut tercapai.
- **Desktop dengan pointer presisi:** foto utama bergerak dari −8 ke 8 piksel mengikuti scroll; foto layanan dari −10 ke 10 piksel. Bingkai foto memotong area tepi, dengan sedikit pembesaran agar tidak ada ruang kosong. Scroll tetap memakai perilaku browser biasa; tidak ada pinning atau penggantian pengendali scroll.
- **Kedua login:** foto masuk dengan pembesaran kecil selama 0,75–1,2 detik, disusul teks foto. Judul formulir bergerak 10 piksel selama 0,55 detik. Kolom email, kata sandi, tombol masuk, tautan portal, dan tautan kembali tidak masuk timeline dan tidak menunggu animasi.
- **Tombol:** ikon panah bergeser 3 piksel saat hover/fokus, tanpa mengubah ukuran tombol.

## Aksesibilitas dan pemulihan

Pengaturan `prefers-reduced-motion: reduce` menonaktifkan seluruh timeline dan parallax. Perubahan preferensi atau breakpoint membatalkan animasi lama dan mengembalikan gaya asal. Fokus keyboard pada halaman langsung menyelesaikan entrance agar sasaran tidak terus bergerak selama digunakan.

HTML awal dari server sudah memuat seluruh teks, gambar, dan navigasi dengan tampilan normal. Tidak ada kelas pembuka yang menyembunyikan seluruh halaman atau menunggu GSAP berhasil. Bila animasi/JavaScript tidak dimuat, konten landing dan tampilan login tetap terlihat. Proses autentikasi tetap memerlukan JavaScript sebagaimana implementasi login sebelumnya; motion tidak menambah syarat atau penundaan pada form.

Animasi dibatasi ke referensi komponen dengan `useGSAP`; callback memakai `contextSafe`. Media query, listener gambar, listener fokus, dan ScrollTrigger dibersihkan saat komponen dilepas atau portal berubah. Posisi trigger diperbarui setelah gambar dan font dimuat. Pola ini mengikuti [panduan React GSAP](https://gsap.com/resources/React/), [matchMedia](https://gsap.com/docs/v3/GSAP/gsap.matchMedia/), dan [ScrollTrigger](https://gsap.com/docs/v3/Plugins/ScrollTrigger/).

## Validasi pelaksanaan

- TypeScript tanpa emit: lulus.
- ESLint khusus `public-motion.tsx`, `public-landing.tsx`, dan `login.tsx`: lulus tanpa peringatan.
- Pembacaan respons server lokal untuk `/`, `/customer/login`, dan `/staff/login`: HTTP 200, teks dan elemen form tersedia pada HTML, tanpa inline style yang menyembunyikan konten untuk gerak.
- Uji visual browser, keyboard, reduced motion, dan responsivitas dicatat oleh pemeriksaan rilis utama. Dokumen ini tidak menyatakan pemeriksaan browser tersebut telah dilakukan.

Perubahan berada di `components/public-motion.tsx`, integrasi ringan landing/login, `app/motion-v9.css`, impor stylesheet pada layout, dan dependensi GSAP. Alur akun, kewenangan, transaksi, data, serta pekerjaan perbaikan keuangan v9 tetap dipertahankan.
