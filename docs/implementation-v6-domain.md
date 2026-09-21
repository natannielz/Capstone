# Implementasi domain v6

17 September 2026. Mencakup B02, B04, B07, dan B10 dari rencana perbaikan v6. Tidak mengubah database, seed produksi, kredensial, atau kebijakan akses baca. Integrasi UI operasional, runner utama, QA browser, dan deployment ditangani agen utama/operasional.

## Perubahan

### B02 — Urutan tanggal transaksi

Pesanan menyimpan tanggal peninjauan, baris pengganti menyimpan tanggal efektif dibuat, dan reservasi menyimpan tanggal pencadangan. Peninjauan tidak dapat mendahului pesanan; reservasi/penggantian/Surat Jalan mengikuti tanggal persetujuan dan pemenuhan terkait. Pembatalan tidak dapat mendahului aktivitas pesanan atau pengembalian barang yang menciptakan sisa pesanan.

Tanggal bukti, gagal kirim, penerimaan, komplain, dan penyelesaian komplain menjadi batas tindakan berikutnya. Finalisasi tidak dapat mendahului penyelesaian komplain. Pemeriksaan tanggal pengiriman, finalisasi, invoice, alokasi, dan periode yang sudah ada tetap berlaku.

Pengadaan menyimpan tanggal permohonan dana, persetujuan dana, ketersediaan dana, dan konfirmasi pembelian. Penerimaan serta pembayaran pemasok mengikuti konfirmasi, penerimaan sebelumnya, dan angsuran sebelumnya. Pengadaan yang ditautkan ke pesanan divisi tidak dapat mendahului pesanan sumber.

Semua tanggal baru opsional pada tipe data untuk kompatibilitas data lama. Urutan pembacaan adalah tanggal eksplisit, tanggal efektif audit tindakan terkait, lalu tanggal dokumen sumber yang tersedia. Untuk pesanan tanpa tanggal efektif/audit, waktu pembuatan dibaca dalam zona Asia/Jakarta. Tidak digunakan tanggal hari ini sebagai pengganti tanggal historis yang diketahui. Bila tanggal transisi dan audit sama-sama tidak pernah disimpan pada data lama, sistem hanya dapat menerapkan batas dokumen sumber yang tersedia; tidak mengarang tanggal transisi.

Pencatatan mundur yang benar pada periode terbuka tetap didukung. Uji pesanan sampai invoice pada 5 September menghasilkan nilai Rp40.000 dan jurnal seimbang. Penolakan dilakukan pada salinan state, sehingga tidak menambah reservasi, transaksi, audit, atau jurnal sebagian.

### B04 — Pembatalan dan substitusi

Pembatalan penuh tanpa pengiriman efektif menghasilkan `Order.status="cancelled"`, termasuk ketika Surat Jalan sebelumnya sudah dibatalkan atau seluruh barang gagal kirim sudah kembali. Penerimaan parsial yang sah tetap dipertahankan; membatalkan sisanya tidak menghapus penerimaan maupun tagihan.

Usulan pengganti yang masih menunggu ikut menjadi `cancelled`, dengan `cancelledReason`, `decidedDate`, dan aktor pembatalan. UI menampilkan “Tidak berlaku” serta alasan sebenarnya, bukan mengaku PIC telah menolak pengganti. Riwayat tetap tersedia. Selector tidak lagi menampilkan tindakan PIC untuk usulan yatim pada pesanan lama yang sudah dibatalkan.

Selector juga mengatasi data lama yang masih berstatus `approved` walaupun semua barang sudah dibatalkan dan tidak ada penerimaan: label menjadi Dibatalkan dan masuk antrean ditolak/dibatalkan. Tombol membatalkan sisa hanya muncul jika benar-benar ada sisa; API menolak pengulangan tanpa sisa.

### B07 — Opname usang, pengganti, dan pembatalan

Status model: `requested | approved | stale | superseded | cancelled`.

- Pengajuan baru menyimpan `movementCount`, jumlah mutasi batch saat hitung dilakukan. Perubahan jumlah stok menandai pengajuan menunggu sebagai usang. Pengajuan lama tidak dapat menjadi sah lagi hanya karena stok kembali ke angka semula.
- `stocktakeStatus(s, stocktake)` dari `selectors.ts` memberi status efektif. Untuk dokumen lama tanpa penghitung mutasi, perubahan `expected` terhadap fisik terbaru dikenali sebagai usang. Mutasi setelah penerapan v6 menandai pengajuan lama juga; neto mutasi historis yang sudah kembali sama sebelum v6 tidak dapat direkonstruksi dari snapshot angka saja.
- `stocktake.create` menerima `replacesId?`. Pengajuan yang diganti harus masih requested/stale dan berasal dari batch sama. Dokumen lama menjadi superseded; `supersededBy` dan `replacesId` membentuk hubungan dua arah. `resolutionReason` dan `resolvedBy` menyimpan alasan/aktor.
- `stocktake.cancel {id, reason}` tersedia untuk Kepala Toko atau Staf pemilik pengajuan. Pengajuan yang sudah approved/superseded/cancelled tidak dapat dibatalkan lagi.
- Persetujuan hanya berlaku pada status efektif requested dan tetap menjaga pemisahan pembuat/pemberi persetujuan. Pembatalan/penggantian tidak membuat jurnal; opname pengganti hanya mencatat selisih fisik terbaru. Penyesuaian nol tidak menghasilkan jurnal tambahan.

Kontrak UI telah disampaikan ke agen operasional. UI harus memakai `stocktakeStatus`, bukan hanya status tersimpan, untuk label dan kelayakan persetujuan.

### B10 — Persetujuan periode

`period.submit` mengosongkan `approvedBy` dan `approvedRevision`. Audit persetujuan lama tetap tersimpan, tetapi tidak ditampilkan sebagai persetujuan aktif versi yang baru diajukan. Penutupan tetap membutuhkan persetujuan baru atas revisi yang sama. Snapshot periode tertutup tidak berubah karena transaksi bulan berikutnya.

## Berkas yang diubah

- `website/lib/domain/engine.ts`
- `website/lib/domain/model.ts`
- `website/lib/domain/selectors.ts`
- `website/lib/domain/order-views.ts`
- `website/components/order-experience.tsx`
- `website/tests/domain-v6.test.ts` (baru)

Kolom penyimpanan yang sudah ada menyimpan isi record JSON; penambahan field opsional tidak memerlukan perubahan skema database. Tidak dilakukan penulisan ulang record produksi. Agen utama memperbarui runner agar menemukan seluruh `*.test.ts`, termasuk `tests/domain-v6.test.ts`.

## Validasi

16 tes baru mencakup reproduksi empat kelompok bug, penolakan atomik, tanggal mundur yang sah, fallback audit legacy, empat jalur pengadaan, stok kembali ke angka awal, penghitungan ulang, pembatasan pembatalan opname, penerimaan parsial, dan snapshot periode tertutup.

Gabungan tes baru dengan `domain.test.ts`, `rbac.test.ts`, dan `navigation.test.ts`: **55 lulus, 0 gagal**. Eksekusi menggunakan loader TypeScript di memori, tanpa server atau penyimpanan database. Tes repository tidak dijalankan oleh agen ini; tetap menjadi bagian verifikasi menyeluruh agen utama.

Sesudah integrasi runner, agen utama melaporkan **67 tes penuh lulus**, mencakup tambahan invoice 101 baris, logout/permintaan sesi, dan operasional. Hasil final rilis mengikuti catatan QA agen utama.

Lint terhadap keenam berkas implementasi/tes di atas lulus tanpa warning/error. Typecheck seluruh proyek pada saat serah terima menemukan tiga error di area katalog yang masih dikerjakan paralel (`catalog.tsx` dua ketidakcocokan nullable string dan `cart-storage.ts` tipe promise). Tidak ada error di berkas milik agen domain. Hasil typecheck/build akhir harus dicatat ulang setelah integrasi selesai; dokumen ini tidak mengklaim build v6 telah lulus atau terbit.

Tidak menggunakan browser, mengirim transaksi ke API, atau menerbitkan deployment dalam subtask ini.
