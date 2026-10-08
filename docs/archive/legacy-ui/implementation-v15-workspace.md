# Implementasi v15 — daftar pembayaran dan keadaan kosong

Perubahan ini melanjutkan rekomendasi v14 pada modul internal. Tidak mengubah engine transaksi, autentikasi, pembatasan peran, perhitungan saldo, ataupun basis data.

## Pembayaran

`website/components/workspace.tsx` sekarang memberikan `query` dan `onQueryChange` kepada komponen Payments. Daftar penerimaan dana mempunyai:

- Pencarian nama pembayar, referensi, dan nama pembeli yang tersedia dalam state akun.
- Pilihan status: Semua status, Menunggu verifikasi, Terverifikasi, Belum teridentifikasi, Ditolak.
- Pagination 12 catatan per halaman, mempertahankan urutan catatan terbaru terlebih dahulu.
- Jumlah hasil, rentang yang ditampilkan, serta tombol Hapus filter.
- Keadaan terpisah untuk belum ada data dan tidak ada hasil pencarian/filter.
- Caption tabel, `scope="col"`, kolom jumlah/saldo rata kanan, nama wilayah scroll tabel, dan fokus keyboard untuk menggulir tabel.
- Kolom tindakan hanya dirender bagi Penagihan. Tombol pencatatan, verifikasi, penolakan, identifikasi, unggah, dan alokasi tetap memakai izin serta payload sebelumnya.

Query URL yang digunakan: `paymentSearch`, `paymentStatus`, `paymentPage`. Pencarian dan status baru menghapus nomor halaman lama. Reset hanya menghapus tiga query pembayaran tersebut; konteks navigasi lain dipertahankan. Navigasi antarmodul tetap memakai mekanisme remembered URL/history yang sudah ada.

Status filter sesuai badge pada baris: pembayaran yang belum diverifikasi tetap masuk Menunggu verifikasi walaupun pembayar belum dikenali. Belum teridentifikasi mencakup catatan terverifikasi tanpa buyer identity yang valid. Pembayaran Ditolak tidak ikut grup lain. Catatan kredit yang sudah menjadi payment record tetap termasuk data, sehingga label daftar memakai “pembayaran”, bukan menganggap semua baris sebagai transfer baru.

`website/lib/domain/payment-views-v15.ts` menangani parsing dan pemilihan data tanpa memutasi state. Parameter tidak valid kembali ke Semua status/halaman 1. Nomor halaman lama yang melebihi hasil terkini dibatasi ke halaman terakhir yang tersedia pada tampilan.

## Pengiriman dan invoice

Kedua daftar sekarang membedakan:

1. **Belum ada data** — menjelaskan kapan pengiriman/invoice muncul; petunjuk kurir menyebut penugasan oleh Staf Toko.
2. **Filter tidak menghasilkan data** — menawarkan Hapus filter, tanpa menyatakan semua riwayat kosong.
3. **ID langsung tidak tersedia** — menyatakan record tidak ditemukan/tidak dapat diakses, serta tombol menuju daftar lengkap.

Nilai filter status pengiriman dan saldo invoice yang tidak dikenal kembali ke Semua. Tabel pengiriman/invoice memperoleh caption dan header semantik; kolom kuantitas pengiriman dan header nilai invoice diselaraskan sebagai numerik.

## Integrasi modul lain

Workspace sekarang memanggil:

```tsx
<Procurement {...ctx} query={query} onQueryChange={updateQuery}/>
<AccountSettings {...ctx} query={query} onQueryChange={updateQuery}/>
```

Signature modul operasi mengikuti `OperationsQueryProps` (`query: URLSearchParams`, `onQueryChange: (patch: QueryPatch) => void`). Implementasi pengadaan/administrasi tetap dimiliki aliran agen operasi, bukan file ini.

## Tampilan

`website/app/workspace-lists-v15.css` dibatasi seluruhnya ke `.workspace`. Filter pembayaran dua kolom di desktop dan satu kolom pada layar kecil; tinggi kontrol 44px; font input seluler 16px; empty state dan pagination mengikuti ritme modul internal. Tabel data lebar tetap dapat digulir di dalam kontainernya. Tidak ada animasi tambahan, perubahan dialog, atau CSS global baru. Agen utama menambahkan import stylesheet pada layout.

## Verifikasi

- **7 tes selector lulus**: URL invalid, konsistensi status, pencarian kombinasi, pagination tanpa duplikasi, halaman lama/hasil kosong, isolasi PIC/state tanpa mutasi, pencarian pelanggan dan catatan kredit.
- Focused TypeScript untuk helper dan tes lulus dengan target/library yang sesuai proyek.
- Focused ESLint pada workspace/helper/tes: **0 error**, dua peringatan navigasi `window.location.assign` yang sudah ada sebelumnya.
- Pemeriksaan browser dan TypeScript/build seluruh aplikasi dilanjutkan agen utama setelah perubahan modul operasi selesai. Tidak ada pengujian mutasi produksi dalam aliran ini.

Pemeriksaan browser yang dianjurkan: query pembayaran bertahan setelah membuka profil dan kembali; pencarian lalu perubahan status kembali ke halaman pertama; pencarian kosong bisa direset; invoice/pengiriman ID tidak tersedia mempunyai jalan kembali; kontrol 320px tidak membuat halaman melebar; tabel bisa digulir dengan keyboard.

## Perbaikan sinkronisasi URL navigasi

Dalam pemeriksaan browser pengembangan, Hapus filter pada persediaan/perubahan status pembayaran pernah mengembalikan halaman ke Ringkasan setelah pembaruan aplikasi. Percobaan ulang setelah pemuatan bersih bekerja benar; masalah ini tidak dinyatakan sebagai reproduksi produksi yang menetap.

Audit sumber Next.js 16.3.4 yang terpasang menemukan penyebab yang dapat direproduksi terisolasi: aplikasi meneruskan seluruh `window.history.state`, termasuk penanda internal `__NA`, ke `pushState`/`replaceState`. Wrapper native history Next menganggap payload bertanda tersebut sebagai panggilan internal dan melewati pembaruan URL kanonis. Commit router berikutnya dapat menulis kembali URL lama; perubahan filter selanjutnya lalu membaca query halaman yang salah.

`website/lib/client/native-history.ts` sekarang mengizinkan hanya metadata milik aplikasi (`unitTokoIndex`, bilangan bulat aman nonnegatif). `use-workspace-navigation.ts` memakai helper untuk inisialisasi, pindah modul, dan perubahan query; `catalog.tsx` memakainya saat mengganti query katalog. Next mempertahankan metadata routernya melalui wrapper yang terdokumentasi. Mekanisme izin halaman, konfirmasi perubahan belum tersimpan, indeks Back/Forward, dan query yang diingat tidak diubah. Audit seluruh native history call menemukan etalase dan pergantian kemasan produk sudah meneruskan `null` secara benar sehingga tidak perlu diubah.

Verifikasi tambahan:

- **6 tes regresi lulus** dalam `website/tests/native-history-v15.test.ts`, memakai hook sebenarnya dengan simulasi kontrak wrapper Next: reproduksi URL kanonis lama; mount/pindah modul/reset filter; Back/Forward dan filter yang diingat; pembatalan/konfirmasi keluar dari profil kotor; pembatasan halaman; query/hash/konteks dan indeks katalog.
- Focused ESLint untuk hook navigasi, katalog, helper, dan tes: **0 error, 0 warning**.
- Focused TypeScript helper dan tes lulus.
- Verifikasi browser integrasi dilakukan agen utama; tes simulasi tidak menggantikan pengujian browser tersebut.

Pemeriksaan browser lanjutan oleh agen utama: reset status pembayaran tetap berada pada `view=payments`; Back dan Forward yang dijalankan terpisah mengembalikan modul serta filter dengan benar. Percobaan Back/Forward dalam satu rangkaian tanpa menunggu navigasi sempat memberi hasil berbeda, tetapi tidak terulang dalam pemeriksaan bertahap; tidak ditambahkan perubahan history spekulatif untuk kasus tersebut.

## Batas scroll tabel pada layar kecil

Pada viewport 320px, agen utama menemukan label header `sr-only` berposisi absolut pada tabel lebar membuat dokumen ikut melebar (dashboard 468px dan pembayaran 687px dibanding lebar klien 305px), walaupun kontainer tabel sudah memiliki `overflow-x: auto`. Kontainer masih `position: static`, sehingga label tersebut tidak dibatasi oleh scrollport-nya.

`workspace-lists-v15.css` sekarang memberi `position: relative` pada kontainer tabel internal yang sudah memakai scroll horizontal (`table-scroll`, `inventory-desktop`, `ops-desktop-table`, `orders-v5-table`). Label dan caption tetap tersedia untuk pembaca layar; tabel tetap bisa digulir. Perubahan tidak menutup overflow seluruh halaman dan tidak menghapus konten aksesibilitas. Pengukuran browser sesudah perubahan dilanjutkan agen utama.
