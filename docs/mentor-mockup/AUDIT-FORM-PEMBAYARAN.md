# Audit formulir pembayaran — 25 September 2026

Audit baca saja terhadap sumber dan data lokal; tidak ada transaksi atau kata sandi yang diubah. Bukti aplikasi ini tidak dihitung sebagai pengujian Figma.

## Keadaan sumber

Tiga pembayaran lokal sudah diverifikasi. Dana Rp175.000 dari “Transfer pusat — belum diketahui” belum mempunyai pembeli. Invoice 0001 memiliki total Rp684.000, alokasi Rp350.000, dan sisa Rp334.000; invoice 0002 senilai Rp108.000 sudah lunas. Periode September terbuka, versi 37.

Frame Figma 109/163 menyimpan skenario sebelum verifikasi; 147/249 menyimpan skenario setelah verifikasi. Perbedaan ini harus dipertahankan ketika membuat cabang berikutnya.

## Tolak pembayaran

Tombol hanya sah untuk pembayaran Menunggu verifikasi. Jalur simulasi dimulai dari 109/163 → dialog 138. Alasan contoh: **Bukti transfer tidak sesuai**. Jangan menolak dana Rp175.000 pada basis data sekarang karena statusnya sudah diverifikasi.

Hasil skenario sebelum verifikasi: Semua 3, Menunggu 0, Terverifikasi 2, Belum teridentifikasi 0, Ditolak 1. Baris dana menjadi Ditolak, saldo belum dialokasikan berupa tanda pisah, alasan dan tanggal keputusan terlihat; tindakan Verifikasi, Tolak, Identifikasi, dan Alokasikan hilang. Tidak ada perubahan kas, jurnal, piutang atau invoice. Aktivitas dan versi periode bertambah. Dashboard beralih dari antrean satu transfer ke invoice belum lunas.

Alasan kosong ditahan oleh validasi HTML `required`. Alasan spasi ditolak domain dengan “Alasan penolakan belum benar atau terlalu panjang.” Batas alasan 2.000 karakter. Toast sukses menambahkan tanda titik sendiri, sehingga contoh alasan tidak memakai titik akhir.

## Identifikasi pembayar

Pembeli dan Hasil penelusuran wajib. Contoh: **Divisi Operasional**, **Transfer dikonfirmasi milik Divisi Operasional**. Pembeli ditetapkan pada dana dan lampiran; catatan identifikasi ditambahkan. Identitas tidak dapat dipindahkan lagi.

Dari skenario sebelum verifikasi, status tetap Menunggu verifikasi dan jumlah filter tetap 3/1/2/0/0. Dari skenario sesudah verifikasi, jumlah menjadi 3/0/3/0/0; dana berstatus Terverifikasi dan tombol Alokasikan muncul. Saldo Rp175.000 belum mengurangi invoice sampai alokasi dilakukan. Tidak ada jurnal baru; rincian saldo belum dialokasikan Divisi Operasional bertambah. Toast: “Pembeli pembayar berhasil diidentifikasi.”

## Catat transfer

Tanggal wajib, default tanggal hari ini atau hari pertama sesudah periode terakhir yang ditutup. Pembeli opsional; nilai transfer wajib bilangan bulat 1–100.000.000.000; nama pembayar wajib dan default nama aktor. Referensi dan catatan opsional.

Contoh minimal: pembeli kosong, nilai 175000, nama pembayar default, referensi/catatan kosong. Hasilnya menambah satu pembayaran Menunggu verifikasi. Semua dan Menunggu bertambah satu; Belum teridentifikasi belum bertambah. Tidak ada perubahan kas, invoice, piutang atau jurnal. Toast: “Transfer simulasi dicatat. Belum mengurangi piutang.”

## Perilaku bersama yang perlu direplikasi

- Field wajib kosong tidak mengirim perintah simpan karena validasi browser.
- Selama menyimpan, label “Menyimpan…” tampil; field dan kedua tombol dinonaktifkan.
- Kegagalan domain tampil sebagai pesan merah yang menerima fokus.
- Sukses memperbarui data, menutup dialog dan menampilkan toast.
- Batal, X dan Escape membuang isian tanpa transaksi.
- Penampilan pesan validasi native harus diamati di browser sebelum dibuat di Figma.

## Rujukan sumber

- [workspace.tsx](../../website/components/workspace.tsx): dialog umum baris 162–166; form pembayaran baris 206/211.
- [engine.ts](../../website/lib/domain/engine.ts): batas teks baris 7; catat 157; tolak 165; identifikasi 169; perubahan periode 293.
- [dashboard-views.ts](../../website/lib/domain/dashboard-views.ts): prioritas Penagihan baris 52.
- [buyer-views.ts](../../website/lib/domain/buyer-views.ts): pilihan pembeli baris 9.
- [operations.tsx](../../website/components/operations.tsx): rincian piutang baris 139.

Form dan hasil di atas **belum dibangun atau diuji lengkap dalam Figma**. Audit ini menjadi dasar pengerjaan, bukan klaim penyelesaian.
