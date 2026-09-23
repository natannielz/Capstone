# Lanjutan rencana UI/UX — v15

23 September 2026. Pengguna meminta melanjutkan rencana v14. Rilis ini menyelesaikan pekerjaan lanjutan yang konkret; identitas, landing, dan carousel yang sudah diperbaiki tetap menjadi dasar.

## Urutan pekerjaan dan kriteria selesai

1. **Daftar operasional.** Pembayaran, pengadaan, dan akun administrator memperoleh pencarian/filter/pagination dengan konteks URL. Ubah filter kembali ke halaman pertama; Back dan tautan langsung menampilkan pilihan yang sama. Total seluruh data tidak boleh tertukar dengan jumlah hasil filter.
2. **Pengaturan yang tepat sasaran.** Kartu dashboard administrator menuju bagian akun, divisi, atau pemasok yang sesuai. Navigasi bagian tetap dapat dipakai dengan keyboard dan di layar kecil.
3. **Keadaan kosong.** Bedakan data yang memang belum ada dan hasil pencarian yang kosong; sediakan reset filter atau petunjuk tindakan sesuai hak akses. Cakupan: pengiriman, invoice, persediaan, pengadaan, pembayaran, periode, administrasi.
4. **Profil yang tidak kehilangan isian.** Perubahan profil dari refresh tidak menimpa isian lokal yang belum disimpan. Navigasi Back/Forward pelanggan diuji. Jika pemblokiran history tidak didukung dengan aman, gunakan pemulihan draf profil sementara yang terisolasi akun dan jelaskan perilakunya. Password tidak disimpan sebagai draf.
5. **Tabel dan formulir.** Beri caption serta header kolom semantik pada tabel aplikasi yang tersisa, sejajarkan angka dan satuannya, jaga scroll tabel tetap di dalam panel. Dialog transaksi tetap stabil tanpa gerakan dekoratif; periksa review penerimaan dan tombol pada ukuran kecil.
6. **Uji dan publikasi.** Tes selector, state profil, TypeScript/lint dan build; browser untuk URL/filter/pagination, Back profil, kosong/tabel pada desktop/mobile. Publish ke domain Vercel dan GitHub yang telah diizinkan setelah pemeriksaan lulus.

## Pembagian implementasi

- Agen dashboard: pembayaran, wiring URL modul, keadaan kosong pengiriman/invoice.
- Agen akun: sinkronisasi profil, pemulihan navigasi pelanggan, regresi draf.
- Agen operasional: pengadaan, administrasi, periode dan tabel bersama.
- Agen utama: persediaan, tabel lain, tautan dashboard, integrasi, pengujian browser, publikasi.

## Konsistensi desain

Tetap gunakan petroleum `#103F47`, putih `#FFFFFF`, sage `#E7EEEB`, ivory `#F7F6F1`, teks sekunder `#52656A`, dan aksen tindakan `#AD431E`. DM Sans untuk kontrol/data, Archivo hanya judul utama. Toolbar mengikuti urutan cari → filter → hasil → pagination; label selalu terlihat. Jarak 8/12/16/24/32 px, tombol penting minimal 44 px, focus ring jelas, angka tabular.

```text
Judul + tindakan sesuai peran
Cari dokumen/nama   [Status] [opsi terkait]
Jumlah hasil        Hapus filter
Tabel/kartu / petunjuk kosong yang relevan
Sebelumnya          halaman x/y          Berikutnya
```

Tidak menambahkan pendaftaran mandiri, reset email, pelacakan kurir langsung, aturan refund baru, atau tanggal pengiriman yang belum didukung backend. Rekomendasi tersebut bukan pekerjaan UI yang aman untuk diasumsikan. Tidak mengklaim penghargaan desain, kepatuhan aksesibilitas penuh, atau pengujian perangkat sentuh fisik jika belum dilakukan.
