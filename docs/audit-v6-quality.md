# Audit kualitas v6 — operasional dan autentikasi

17 September 2026. Pemeriksaan read-only terhadap implementasi setelah v5, mengacu pada `website/AGENTS.md`, `docs/ui-implementation-v5.md`, rencana digitalisasi, dan catatan sumber pengadaan. Tidak ada kode aplikasi, kredensial, database lokal/produksi, atau transaksi produksi yang diubah. Tidak menggunakan browser bersama.

Empat temuan di bawah diprioritaskan untuk demo. Bukti domain berasal dari fungsi `runCommand` aktual yang ditranspilasi dan dijalankan di memori dengan fixture sintetis. Pemeriksaan logout memakai fungsi frontend aktual dengan respons HTTP tiruan. Ini bukan pengujian penetrasi maupun sertifikasi aksesibilitas.

## 1. Kegagalan logout tetap terlihat sebagai keluar akun

- **Prioritas:** P1; keyakinan tinggi. Kondisi: endpoint logout mengembalikan 503, misalnya penghapusan sesi pada database gagal.
- **Lokasi:** `website/components/workspace.tsx:99`; `website/app/api/auth/logout/route.ts:4`.
- **Bukti:** fungsi `logout()` menunggu `fetch`, tetapi tidak memeriksa `res.ok`, lalu selalu membuka `/login`. Uji fungsi aktual dengan respons 503 menghasilkan pengalihan tersebut. Endpoint mengirim cookie kedaluwarsa hanya setelah operasi penghapusan sesi berhasil; jalur `catch` mengembalikan error tanpa menghapus cookie. Pada kegagalan database, pengguna mendapat kesan sudah keluar sementara sesi/cookie dapat tetap berlaku.
- **Batas bukti:** pengalihan 503 direproduksi di memori; kegagalan database endpoint ditelusuri dari kode, tidak disuntikkan ke server atau produksi.
- **Perbaikan:** periksa hasil logout, tampilkan kegagalan persisten dengan tindakan coba lagi, dan tentukan perilaku penghapusan cookie lokal yang jelas. Jangan menganggap sesi server telah dicabut bila penghapusannya gagal. Tangani juga kegagalan jaringan dan cegah pengiriman berulang selama proses berlangsung.
- **Penerimaan:** respons 200 menghapus cookie dan membuka login; 503 serta network rejection menghasilkan pesan yang dapat ditindaklanjuti dan tidak mengklaim logout berhasil. Uji integrasi memastikan sesi lama tidak berlaku sesudah logout sukses. Estimasi usaha: kecil.

## 2. Tanggal tahapan pengadaan dapat berurutan terbalik

- **Prioritas:** P2; keyakinan tinggi, direproduksi di memori.
- **Lokasi:** `website/lib/domain/engine.ts:136`, `:138`, `:139`, `:140`, `:142`, `:149`; `website/lib/domain/model.ts:20`.
- **Bukti:** pengadaan dibuat 17 September; pengajuan dana 20 September; persetujuan dana **19 September** diterima. Setelah dana tersedia 21 September dan pembelian dikonfirmasi **22 September**, penerimaan bertanggal **18 September** juga diterima, membuat pengadaan `complete` dan jurnal penerimaan 18 September. Pemeriksaan penerimaan hanya membandingkan tanggal pembuatan pembelian dan pembayaran sebelumnya, bukan konfirmasi. Tanggal tahapan pendanaan/konfirmasi tidak disimpan dalam record pengadaan; tanggal audit memang ada tetapi tidak digunakan sebagai batas.
- **Dampak:** laporan dan histori dapat mencatat persediaan diterima sebelum pembelian dikonfirmasi atau dana disetujui. Semua peran pada uji sesuai otorisasinya; ini celah kronologi, bukan bypass RBAC.
- **Perbaikan:** simpan tanggal efektif permohonan/persetujuan/ketersediaan dana dan konfirmasi pembelian. Validasi tahapan berikutnya terhadap tanggal prasyarat yang relevan, termasuk penerimaan dan pembayaran pemasok; sediakan migrasi kompatibel untuk record lama.
- **Penerimaan:** approve sebelum request, disburse sebelum approve, serta receive/pay sebelum konfirmasi ditolak tanpa perubahan parsial; urutan pada hari yang sama dan urutan normal tetap berhasil. Estimasi usaha: sedang.

## 3. Pengajuan opname usang tidak memiliki jalan penyelesaian

- **Prioritas:** P2; keyakinan tinggi, direproduksi di memori.
- **Lokasi:** `website/lib/domain/engine.ts:161–163`; `website/components/operations.tsx:44`; `website/lib/domain/model.ts:24`.
- **Bukti:** opname A menyimpan fisik sistem 20. Opname lain disetujui sehingga fisik menjadi 19; persetujuan A ditolak 409 dengan pesan untuk menghitung ulang. Opname pengganti lalu disetujui, tetapi A tetap `requested`. Model hanya mengenal `requested/approved`, tidak ada pembatalan atau penandaan telah diganti; UI terus menawarkan persetujuan A.
- **Dampak:** penghitungan ulang menyelesaikan angka stok tetapi tidak menyelesaikan antrean lama. Pengguna tidak dapat membedakan pengajuan aktif dengan pengajuan yang tidak lagi dapat diproses.
- **Perbaikan:** beri status dibatalkan/usang/digantikan beserta alasan dan tautan ke penghitungan pengganti. Tetap pertahankan pemeriksaan stok terbaru; jangan menghapus histori atau melonggarkan kontrol jumlah stok.
- **Penerimaan:** perubahan stok membuat pengajuan lama dikenali sebagai usang; penghitungan ulang menutup atau menghubungkan pengajuan lama, tanpa jurnal/penyesuaian ganda. Hanya pengajuan aktif menampilkan tindakan persetujuan. Estimasi usaha: sedang.

## 4. Pengajuan ulang periode masih menampilkan pemberi persetujuan lama

- **Prioritas:** P3; keyakinan tinggi, direproduksi di memori.
- **Lokasi:** `website/lib/domain/engine.ts:182`; `website/components/operations.tsx:63`.
- **Bukti:** `period.submit → period.approve → period.submit` menghasilkan `status="review"`, `approvedRevision=null`, tetapi `approvedBy="pimpinan"` tetap ada. Kolom Persetujuan menampilkan nama/fallback berdasarkan `approvedBy` tanpa memeriksa status, sehingga laporan yang menunggu persetujuan tampak sudah memiliki persetujuan.
- **Batas dampak:** `period.close` tetap memeriksa status dan versi. Uji ini tidak menemukan kemungkinan posting tanpa persetujuan; masalahnya konsistensi informasi peninjauan.
- **Perbaikan:** kosongkan persetujuan aktif pada pengajuan ulang, atau pisahkan persetujuan lama sebagai histori berlabel jelas. Audit historis tetap dipertahankan.
- **Penerimaan:** pengajuan ulang menampilkan menunggu persetujuan dan tidak bisa diposting sampai disetujui lagi; nama pemberi persetujuan aktif baru muncul sesudah approval berikutnya. Estimasi usaha: kecil.

## Ringkasan bukti dan batas pemeriksaan

```json
{
  "procurement": {
    "fundingRequested": "2026-09-20",
    "fundingApproved": "2026-09-19",
    "confirmed": "2026-09-22",
    "received": "2026-09-18",
    "finalStatus": "complete"
  },
  "stocktake": {
    "oldStatus": "requested",
    "replacementStatus": "approved",
    "oldApproval": "409: stok telah berubah"
  },
  "periodResubmission": {
    "status": "review",
    "approvedBy": "pimpinan",
    "approvedRevision": null
  },
  "logoutWithMock503": { "redirectedToLogin": true }
}
```

Tidak ada temuan baru tentang kebocoran lampiran atau lintas-divisi yang dibuktikan pada audit terbatas ini. Klaim keamanan umum tidak disimpulkan hanya dari tidak ditemukannya masalah. Kinerja dengan data besar belum diukur; pembacaan seluruh state dan tabel panjang merupakan kandidat pengukuran, bukan temuan penurunan performa yang direproduksi. Temuan ergonomi tabel ponsel dan keterbacaan audit aktor dari pemeriksaan browser agen utama dilaporkan terpisah agar tidak dihitung dua kali.
