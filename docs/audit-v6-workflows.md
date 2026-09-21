# Audit alur transaksi setelah v5

Tanggal: 17 September 2026. Ruang lingkup: pesanan, pengiriman, invoice, alokasi pembayaran, validasi domain, dan state navigasi. Audit ini hanya membaca aplikasi dan menjalankan fixture di memori; tidak mengubah implementasi, kredensial, database lokal, atau produksi.

Dasar: [AGENTS.md](../website/AGENTS.md) dan [penerapan UI v5](ui-implementation-v5.md). Empat temuan di bawah **terverifikasi**, bukan dugaan. Tidak ada temuan yang mengulang filter antrean, pencarian hilang ketika kembali dari detail, responsivitas daftar pesanan, atau alokasi satu invoice yang sudah diperbaiki pada v5.

## Ringkasan prioritas

| ID | Prioritas | Temuan | Dampak utama | Perkiraan lingkup |
| --- | --- | --- | --- | --- |
| WF-01 | P1 | Urutan tanggal dapat menghasilkan invoice sebelum tanggal pesanan | Riwayat transaksi dan tanggal pengakuan penjualan tidak konsisten | Validasi domain, tanggal sumber, regresi alur |
| WF-02 | P2 | Pembatalan seluruh pesanan setelah pembatalan Surat Jalan tampil sebagai terpenuhi | Status dan antrean pekerjaan menyesatkan | Transisi pembatalan dan selector status |
| WF-03 | P2 | Usulan pengganti tetap meminta tindakan setelah pesanan dibatalkan | PIC diberi tindakan yang pasti ditolak | Penyelesaian usulan dan filter tindakan |
| WF-04 | P2 | Invoice gabungan macet ketika divisi memiliki lebih dari 100 baris siap tagih | Penagihan tidak dapat menerbitkan invoice dari UI | Pemilihan/batas baris invoice dan validasi formulir |

P1 berarti perlu ditangani sebelum memperluas skenario pencatatan tanggal. P2 berarti alur masih dapat menyesatkan atau menghambat pekerjaan pada kondisi yang dijelaskan; bukan kebocoran akses.

## WF-01 — Tanggal operasional dapat mendahului pesanan

**Status: bug terverifikasi melalui rangkaian perintah domain.**

Lokasi utama: [engine.ts:39](../website/lib/domain/engine.ts#L39), [engine.ts:48](../website/lib/domain/engine.ts#L48), dan [engine.ts:62](../website/lib/domain/engine.ts#L62). Pesanan menyimpan `createdDate` pada [engine.ts:35](../website/lib/domain/engine.ts#L35), tetapi peninjauan, reservasi, dan pembuatan Surat Jalan tidak membandingkan tanggal tindakan dengan tanggal pesanan. Pemeriksaan pengiriman berikutnya hanya mengikuti tanggal Surat Jalan pada [engine.ts:73](../website/lib/domain/engine.ts#L73). Formulir tindakan membolehkan pengguna mengisi tanggal pencatatan pada [workspace.tsx:138](../website/components/workspace.tsx#L138).

Reproduksi di memori:

1. PIC membuat pesanan satu pak kopi Rp28.500, tanggal pesanan 17 September 2026.
2. Kepala menyetujui dengan tanggal 10 September 2026.
3. Staf mencadangkan stok dan membuat Surat Jalan bertanggal 10 September; pilih batch yang belum memiliki mutasi setelah tanggal tersebut.
4. Pengiriman, penerimaan, finalisasi, dan penerbitan invoice semuanya menggunakan 10 September.

Hasil aktual: seluruh perintah diterima. Pesanan memiliki `createdDate=2026-09-17`, sedangkan invoice Rp28.500 memiliki `date=2026-09-10`. Jadi kontrol tanggal pada tahap akhir tidak menutup celah yang muncul di awal alur. Ini tidak membuktikan bahwa periode tertutup dapat ditulis; pemeriksaan periode tertutup tetap berjalan.

Usulan: tetapkan tanggal sumber minimum untuk setiap transisi. Minimal, peninjauan/reservasi/Surat Jalan tidak boleh mendahului tanggal pesanan. Jika tanggal persetujuan harus menjadi batas berikutnya, simpan tanggal persetujuan secara eksplisit. Tinjau juga `shipment.proof`, `shipment.fail`, dan penyelesaian komplain agar aturan tanggal seragam; ketiganya bukan bukti tambahan dalam uji ini.

Acceptance criteria:

- Pesanan 17 September tidak dapat ditinjau atau dibuatkan Surat Jalan pada 10 September; penolakan tidak menambah audit, reservasi, Surat Jalan, atau jurnal.
- Alur pada 17 September atau setelahnya tetap berhasil.
- Penerimaan/finalisasi/invoice tidak dapat mendahului dokumen sumber masing-masing.
- Pencatatan tanggal mundur yang masih sah dalam periode terbuka tetap tersedia; perbaikan tidak sekadar melarang semua tanggal selain hari ini.

## WF-02 — Pembatalan penuh setelah Surat Jalan dibatalkan diberi label terpenuhi

**Status: bug terverifikasi melalui perintah domain dan selector yang dipakai daftar/detail.**

Lokasi: [engine.ts:45](../website/lib/domain/engine.ts#L45), [order-views.ts:35](../website/lib/domain/order-views.ts#L35), [order-views.ts:52](../website/lib/domain/order-views.ts#L52), dan [order-views.ts:53](../website/lib/domain/order-views.ts#L53).

`order.cancel` hanya mengganti status pesanan menjadi `cancelled` jika tidak ada objek Surat Jalan sama sekali. Surat Jalan yang sudah berstatus `cancelled` masih memenuhi pemeriksaan keberadaan tersebut. Setelah semua kuantitas pesanan dibatalkan, selector membandingkan `accepted >= desired`; nilai 0 >= 0 dianggap terpenuhi.

Reproduksi:

1. Buat dan setujui pesanan tiga pak kopi.
2. Cadangkan tiga pak, buat Surat Jalan, lalu batalkan Surat Jalan sebelum berangkat.
3. PIC membatalkan seluruh sisa pesanan.

Hasil aktual: status pesanan masih `approved`, barang diterima 0, label `Terpenuhi`, `matchesOrderQueue(..., "fulfilled")` bernilai true, dan antrean `closed` bernilai false. Riwayat Surat Jalan tetap ada, tetapi tidak pernah ada pengiriman yang diterima.

Usulan: bedakan keberadaan riwayat Surat Jalan dari pemenuhan efektif. Pembatalan penuh tanpa pengiriman efektif harus menjadi pesanan dibatalkan. Jangan menetapkan `Terpenuhi` semata-mata karena jumlah aktif dan diterima sama-sama nol.

Acceptance criteria:

- Skenario di atas tampil `Dibatalkan`, masuk antrean ditolak/dibatalkan, dan tidak masuk antrean terpenuhi.
- Riwayat nomor Surat Jalan yang dibatalkan tetap tersedia.
- Skenario barang sudah diterima sebagian kemudian sisa dibatalkan tetap menunjukkan jumlah diterima dan jumlah dibatalkan secara benar; jangan menghapus riwayat penjualan/penerimaan.

## WF-03 — Usulan pengganti masih meminta keputusan setelah pembatalan

**Status: bug terverifikasi melalui perintah domain, selector, dan kondisi render.**

Lokasi: pembatalan [engine.ts:41](../website/lib/domain/engine.ts#L41), guard keputusan [engine.ts:58](../website/lib/domain/engine.ts#L58), antrean [order-views.ts:49](../website/lib/domain/order-views.ts#L49), daftar tindakan [order-experience.tsx:126](../website/components/order-experience.tsx#L126), serta tombol keputusan [order-experience.tsx:202](../website/components/order-experience.tsx#L202).

Reproduksi:

1. Buat dan setujui pesanan tiga pak kopi tanpa Surat Jalan.
2. Staf mengusulkan teh sebagai barang pengganti; status usulan `pending`.
3. PIC membatalkan seluruh pesanan.
4. Lihat antrean `Perlu tindakan PIC` dan detail pesanan, lalu pilih setujui pengganti.

Hasil aktual: pesanan sudah `cancelled`, usulan tetap `pending`, dan antrean masih memasukkan pesanan tersebut. Kondisi render tetap menyediakan tombol setuju/tolak. Persetujuan kemudian ditolak domain dengan pesan “Pesanan berubah. Minta toko meninjau ulang usulan.” Jalur menolak masih dapat membersihkan usulan secara manual, tetapi PIC seharusnya tidak perlu mengambil keputusan pembelian tambahan untuk pesanan yang telah dibatalkan.

Usulan: saat pembatalan meniadakan kuantitas usulan, selesaikan/nyatakan usulan tidak berlaku dengan alasan pembatalan. Simpan riwayatnya. Filter tindakan harus memakai kelayakan keputusan saat ini, bukan hanya `sub.status === "pending"`.

Acceptance criteria:

- Setelah pembatalan, pesanan tidak lagi muncul sebagai perlu keputusan pengganti dan tombol persetujuan tidak tersedia.
- Riwayat usulan menyebutkan bahwa pesanan dibatalkan, tanpa mengaku PIC menolak substitusi jika PIC tidak melakukan tindakan itu.
- Usulan untuk pesanan aktif tetap dapat disetujui/ditolak; pengiriman parsial yang sah tetap didukung.
- Permintaan persetujuan lama yang dikirim langsung ke API tetap ditolak secara atomik.

## WF-04 — Lebih dari 100 baris siap tagih membuat aksi invoice tidak dapat diselesaikan

**Status: bug terverifikasi melalui fixture sintetis 101 baris final dan payload identik dengan pemetaan UI.**

Lokasi: batas `rows()` pada [engine.ts:7](../website/lib/domain/engine.ts#L7), pemakaian oleh penerbitan invoice pada [engine.ts:102](../website/lib/domain/engine.ts#L102), pengumpulan baris pada [workspace.tsx:154](../website/components/workspace.tsx#L154), dan pemetaan seluruh baris divisi pada [workspace.tsx:155](../website/components/workspace.tsx#L155).

Kondisi: satu divisi memiliki 101 atau lebih baris penerimaan final yang belum ditagih. Baris tersebut bisa berasal dari banyak pesanan/pengiriman; batas 100 barang per pesanan tidak mencegah kondisi ini.

Reproduksi di memori: sediakan 101 baris final belum ditagih pada satu divisi, pilih divisi tersebut pada formulir invoice, dan gunakan pemetaan `billable.filter(...).map(...)` yang sama dengan UI. Server menolak dengan “Pilih 1–100 baris barang.” Formulir hanya menyediakan divisi dan jatuh tempo, sehingga pengguna tidak dapat memilih sebagian baris melalui UI. Mengubah tanggal atau mencoba lagi tidak menyelesaikan masalah.

Usulan: sediakan pilihan baris/pengiriman dengan ringkasan jumlah dan nilai serta batas yang jelas, atau dukung penerbitan gabungan dengan batas server yang disepakati dan diuji. Jangan diam-diam memotong data menjadi 100 karena pengguna akan mengira seluruh penerimaan sudah ditagih. Batas serupa juga dipakai alokasi multi-invoice; jaga konsistensi batas pada formulir tersebut jika skala demo diperluas.

Acceptance criteria:

- Pada 101 baris, Penagihan dapat menerbitkan invoice untuk pilihan maksimal yang didukung dan melihat sisa baris belum ditagih; alternatif dukungan 101+ server harus eksplisit.
- Setelah penerbitan pertama, baris yang sudah ditagih tidak dapat dipilih ulang dan sisanya dapat diterbitkan tanpa duplikasi.
- Ringkasan nilai dan jumlah baris sesuai payload yang disimpan.
- Penolakan server mempertahankan pilihan pengguna dan memberikan pesan yang menyebut batas invoice, bukan sekadar “baris barang”.

## Bukti pengujian dan batas audit

Uji dilakukan memakai `seedState()`, `runCommand()`, dan selector TypeScript yang ditranspilasi di memori. Fixture WF-04 dibuat secara sintetis untuk menguji batas jumlah baris; tidak dibuat melalui 101 transaksi UI. Semua hasil berikut benar-benar dikembalikan kode saat audit:

```json
[
  {"case":"chronology_before_order","orderDate":"2026-09-17","invoiceDate":"2026-09-10","invoiceValue":28500,"outcome":"ACCEPTED"},
  {"case":"cancelled_shipment_then_order_cancel","orderStatus":"approved","display":"Terpenuhi","fulfilledQueue":true,"closedQueue":false,"accepted":0},
  {"case":"pending_substitution_after_cancel","orderStatus":"cancelled","substitutionStatus":"pending","needsPic":true,"approveError":"Pesanan berubah. Minta toko meninjau ulang usulan."},
  {"case":"invoice_ui_all_lines_cap","billable":101,"error":"Pilih 1–100 baris barang.","outcome":"REJECTED_NO_UI_SUBSET"}
]
```

Integrasi alokasi pembayaran tetap menggunakan satu perintah atomik; audit ini tidak menemukan bukti duplikasi alokasi atau bypass divisi. State navigasi pada `use-workspace-navigation.ts` tetap menyimpan filter saat detail dibuka dan mengembalikannya lewat `go`; tidak ditemukan regresi konkret pada inspeksi ini. Tidak ada klaim pemeriksaan browser tambahan: browser, deployment, perangkat sentuh, dan pembaca layar berada di luar audit ini.

Urutan kerja yang disarankan: WF-01 terlebih dahulu, kemudian WF-02 dan WF-03 sebagai satu perbaikan konsistensi pembatalan, terakhir WF-04 untuk kapasitas penagihan. Setelah perubahan, jalankan regresi domain dan UI atas skenario numerik di atas serta alur normal v5. Dokumen ini adalah masukan audit, bukan pelaksanaan perbaikan atau pembuatan goal baru.
