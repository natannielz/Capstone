# Implementasi v14 — landing dan footer bersama

23 September 2026. Mengikuti tahap 2 pada [rencana UI/UX v14](plan-ui-ux-v14.md) dan [hasil riset landing/footer](research-v14-landing-footer.md).

## Perubahan yang diterapkan

`website/components/site-footer.tsx` dan `website/app/footer-v14.css` menyediakan satu footer untuk landing serta seluruh halaman yang memakai ShopShell. Bidang petroleum `#103F47` memberi akhir halaman yang jelas; monogram U/T dan kata Unit Toko berwarna terang. Teks konteks kecil pada logo disembunyikan secara visual karena deskripsi toko sudah tersedia di bawahnya, sedangkan nama aksesibilitas logo tetap mengikuti tujuan tautan.

Navigasi dipisahkan menjadi tiga kelompok berjudul: **Belanja**, **Akun Anda**, dan **Unit Toko**. Masing-masing menggunakan landmark `nav` berlabel, judul, serta daftar tautan. Semua tujuan sudah ada: katalog dan tiga koleksinya, keranjang, pesanan, profil/alamat, landing, cara berbelanja, dan portal staf. Footer pelanggan menerima `accountHref` yang sudah dihitung ShopShell; autentikasi dan logika keranjang tidak diubah.

Keterangan demo dipindahkan dari kolom yang bersaing dengan navigasi ke baris bawah dengan pembatas. Seluruh informasi tetap ada: demo capstone, studi kasus divisi BNI, bukan situs resmi BNI, serta data/foto/pengiriman/pembayaran simulasi. Ukurannya 12 px, termasuk pada ponsel. Tautan utama footer berukuran 13–14 px dengan tinggi minimal 44 px.

Pada desktop, identitas dan navigasi memakai grid yang jelas. Tablet menempatkan identitas di atas kelompok navigasi. Ponsel memakai dua kolom kelompok, dengan kelompok Unit Toko pada baris berikutnya. Tidak ada accordion atau marquee. Jarak aman perangkat ditambahkan pada footer pelanggan; ruang 74/92 px yang sudah disediakan ShopShell untuk navigasi/tindakan bawah tetap digunakan. Footer tidak masuk ke timeline animasi; hanya warna dan underline tautan yang bertransisi.

`website/components/public-landing.tsx` kini menggunakan footer yang sama. Paragraf tiga koleksi dipadatkan menjadi satu kalimat konkret, dan baris detail yang mengulangi jenis barang dihapus. Merchandise memakai foto tas kanvas yang sudah tersedia, `images/products/tote.png`, sehingga tidak lagi mengulang foto hero. Foto tersebut ditinjau sebagai aset lokal dan ditempatkan dengan `object-fit: contain`.

`website/app/landing-v13.css` diperbarui langsung. Di layar ≤800 px, navigasi beranda menjadi baris kedua header yang terlihat, berisi Koleksi toko, Cara berbelanja, dan Portal internal. Logo serta tombol Pilih akses tetap berada pada baris pertama. Tidak ada menu tersembunyi baru atau JavaScript tambahan.

Bab koleksi ponsel dipadatkan melalui paragraf yang lebih pendek, penghapusan detail berulang, jarak yang lebih teratur, dan rasio foto 1.45–1.55 dibandingkan foto hampir persegi sebelumnya. Label navigasi koleksi menjadi 12 px dan boleh membungkus, tanpa mengecilkannya ke 10 px. Hierarki judul, foto, CTA dan pemisahan akses tetap dipertahankan.

## Berkas dan batas edit

- `components/public-landing.tsx`: copy koleksi, foto merchandise, pemakaian footer.
- `app/landing-v13.css`: navigasi mobile, kepadatan koleksi dan penghapusan aturan detail/footer yang tidak lagi dipakai pada berkas ini.
- `components/site-footer.tsx`: komponen baru dengan tautan sah dan disclosure lengkap.
- `app/footer-v14.css`: gaya footer bersama, ukuran target, responsive dan safe-area.
- `components/shop/shop-shell.tsx`: hanya impor SiteFooter dan penggantian blok footer.

Koordinator mengimpor `footer-v14.css` pada layout. Tidak ada edit pada autentikasi, cart store, pemuatan katalog, tujuan redirect, aturan izin, atau hook gerakan publik.

## Rekomendasi riset yang tidak diterapkan pada subtask ini

- Logo tidak digambar ulang; rencana mempertahankan identitas yang sudah dikenali.
- Tidak menambah kontak, alamat, kebijakan retur, newsletter, sosial media, atau logo pembayaran yang belum didukung aplikasi.
- Tidak membuat varian checkout ringkas saat ini: tahap yang disepakati memakai footer bersama pada seluruh halaman pelanggan. Varian tersebut dapat dipertimbangkan jika pengujian checkout menunjukkan kebutuhan yang nyata.
- Tidak menambah gambar baru karena foto produk yang ada sudah menghilangkan pengulangan hero.
- Tidak mengubah timeline hero, depth, atau state semantik navigasi bab dalam hook publik. Scope gerakan dikoordinasikan terpisah; perbaikan refresh gambar yang sebelumnya mengganggu anchor tetap dipertahankan.
- Tidak mengganti headline utama atau seluruh komposisi landing. Fokus implementasi adalah kualitas bersama, navigasi yang dapat ditemukan, dan pengurangan pengulangan.

## Validasi pada subtask

Focused ESLint lulus untuk `public-landing.tsx`, `site-footer.tsx`, dan `shop/shop-shell.tsx`. Aset foto merchandise diperiksa secara visual dari berkas lokal. Pemeriksaan source memastikan blok cart/auth ShopShell tidak tersentuh dan tautan footer mengacu ke rute yang tersedia.

QA browser koordinator menemukan aturan `.site-footer` lama pada `polish.css` yang masih menerapkan grid tiga kolom ke pembungkus baru. `footer-v14.css` kini mereset layout pembungkus secara eksplisit (`display`, ukuran, kolom grid, gap, padding, dan border), sehingga hanya grid di dalam komponen yang mengatur kolom. Aturan responsive lama juga ditimpa oleh reset yang diimpor terakhir ini. Pengujian browser ulang ditangani koordinator.

Browser tidak digunakan pada subtask ini. Pengukuran 320 px, footer di atas navigasi/tombol bawah, urutan fokus, tautan sebagai pengunjung/pelanggan, kontras hasil render, dan build akhir harus dicatat oleh koordinator setelah pemeriksaan gabungan. Dokumen ini tidak mengklaim hasil pemeriksaan tersebut sebelum dilakukan.
