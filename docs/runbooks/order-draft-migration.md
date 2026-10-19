# Order draft migration and recovery

Migration 21 adds private buyer drafts after reviewed catalog pricing. Applied migrations 1–20 are unchanged. This runbook prepares operation; it does not approve a production migration or cutover.

1. Use the reviewed release and approved destination/secret configuration. Pause and drain all application, worker and integration writers. The migration lease prevents competing migrators, not business writes.
2. Preserve a coordinated database backup and verify restore before applying to real data. Include migration history, counters, authorization/session records, catalog provenance, drafts/versions, audits, command receipts and outbox. Never restore a head separately from its immutable versions and receipts.
3. Run the existing worker migration entry point from `backend`: `dotnet run --project src/UnitToko.Worker -- --migrate`. API startup does not apply it automatically.
4. Verify the prior migration receipts are unchanged and migration 21 completes. Check strict validators and the unique draft/revision index. Compare the recorded business baseline before reopening traffic.
5. In the isolated staging branch/account, create a draft, reload and update it. Check that another account cannot read it and an obsolete revision cannot overwrite it. Deliberately lose a successful response and recover the same receipt without another save. Verify no order, reservation, stock movement, invoice or journal is created by this journey.

The new collections are `orderDrafts` and `orderDraftVersions`. All IDs/revisions are BSON Int64 and JSON strings; package quantities are bounded integers. Versions contain private buyer-entered delivery details. Their audit and outbox payloads carry references, not copies of those contact details. The private version, head, audit, receipt and outbox write are atomic. Drafts do not commit a price or verify an address.

Preflight inspects both targets before changing either. As there was no authorized draft writer before migration 21, new draft stores must be empty during installation/resumption. Unexpected rows, TTL/unsafe indexes, incompatible uniqueness/options or exhausted counters require investigation. Do not delete data, reduce counters, manufacture receipts or mark the migration applied to bypass failure. Compatible stronger validators and safe existing indexes are preserved.

If installation is interrupted, keep writers paused, retain diagnostics and retry the same reviewed migration after the lease is legitimately available. Compatible partial empty DDL can resume. A partial migration does not activate the draft writer. Do not downgrade the schema or restore only selected collections to roll back an application release.

For a lost browser response, sign in as the original account and open **Draf pesanan → Periksa hasil**. Only the original operation reference is stored on that device. A missing receipt means unconfirmed, not failed; retain the reference and do not create a replacement draft to bypass it. Revoked authority remains denied during recovery. Once a receipt is confirmed, read the current draft; its current revision may be newer than that receipt. Price/package changes require explicit reselection before a new save, but do not invalidate recovery of an already committed save.

## Memesan ulang dari riwayat

Buka **Riwayat pesanan**, pilih pesanan dalam lingkup akun saat ini, lalu pilih **Pesan ulang sebagai draf**. Aplikasi membaca ulang pesanan sumber dan menyalin pilihan SKU, kemasan, jumlah serta rincian pengirimannya ke draf baru yang belum tersimpan. Periksa kembali alamat dan kontak. Referensi pembelian lama dikosongkan agar pengguna memasukkan referensi baru bila diperlukan.

Pratinjau memeriksa kembali harga, kemasan, jumlah dan akses melalui API yang sama dengan draf biasa. Pilihan historis yang sudah tidak berlaku memerlukan pemilihan ulang secara eksplisit. Harga lama tidak menjadi janji harga baru. Setelah pemeriksaan dan konfirmasi, **Simpan draf** membuat satu draf privat dengan alasan audit yang menyebut pesanan sumber. **Tinjau pengajuan draf** membuka checkout untuk langkah pengajuan terpisah; penyimpanan draf sendiri tidak membuat pesanan, reservasi, invoice atau pembayaran.

Jika respons penyimpanan hilang, tetap gunakan **Periksa hasil** di riwayat dengan akun dan perangkat semula, termasuk setelah browser dibuka kembali. Referensi operasi yang sama dipakai untuk menemukan hasil sebenarnya. Tab lain dikunci dan pratinjau lama dibatalkan selama hasil belum diketahui. Jangan membuat pengajuan pengganti selama operasi tersebut belum terkonfirmasi. Membatalkan pratinjau sebelum penyimpanan tidak membuat data bisnis baru.

Pemesanan ulang tidak mengganti barang pada pesanan sumber dan tidak merupakan persetujuan substitusi. Pesanan sumber, harga historis, reservasi dan transaksi uangnya tetap terikat alur asal.

The actual local browser fixture uses synthetic accounts and session-store-issued sessions, a production Next build, real HTTPS/CSRF, .NET and an isolated Mongo database. It is not live Auth0, a production restore or operator/business acceptance. Those checks remain M6 gates. Reorder browser131-1 passed on backend131/full5 (1/1, 6.4529 minutes), including cancellation without a write, current quote, one lost committed response, actual browser restart, original-key recovery, stale-tab invalidation, refresh, denied actor/branch and unchanged source business state. Four desktop/mobile screenshots and their hashes are retained in `tmp/production/preserved-u07-reorder131-screenshots/MANIFEST.json`.

## Memasukkan banyak SKU ke draf

Pada editor draf, pilih **Masukkan banyak SKU**. Salin dua kolom tanpa judul kolom dari daftar Anda: kode SKU yang ditampilkan pada katalog dan jumlah kemasan yang ingin dipesan. Pisahkan kedua kolom dengan tab atau titik koma, satu SKU per baris. Masukkan paling banyak 100 SKU berbeda. Jumlah harus bilangan bulat positif; isi per kemasan, minimum pembelian dan kelipatannya mengikuti kemasan SKU yang dipilih.

Pilih **Periksa daftar SKU**. Seluruh daftar diperiksa menggunakan katalog yang dapat diakses akun saat ini. Kode yang tidak ditemukan, sudah dinonaktifkan atau tidak dapat diakses menyebabkan daftar ditolak secara utuh. Pesan penolakan tidak mengungkapkan apakah suatu SKU tersedia untuk akun lain. Kode supplier dan barcode belum didukung pada isian ini. Gunakan resolver pemindaian yang sesuai untuk kegiatan gudang; jangan mengubah barcode menjadi SKU berdasarkan perkiraan.

Periksa setiap kode, nama, kemasan, isi, harga per kemasan dan jumlah pada pratinjau. Jika SKU sudah ada di draf, jumlah pada daftar akan **mengganti** jumlah SKU tersebut setelah Anda memilih **Terapkan daftar ke draf**. Pilihan lain di draf tetap dipertahankan. **Perbaiki daftar SKU** kembali ke isian, sedangkan **Batalkan daftar SKU** menutup daftar tanpa mengubah pilihan draf.

Menerapkan daftar baru mengubah isian di layar. Pilih **Tinjau perubahan draf** untuk meminta quote terkini, kemudian konfirmasi penyimpanan. Harga, versi kemasan, akses dan aturan jumlah kembali diperiksa oleh pemilik operasi draf. Penyimpanan tidak membuat pesanan, reservasi stok, invoice atau pembayaran. Jika kemasan atau harga berubah sebelum penyimpanan, tinjau dan pilih kembali berdasarkan hasil pemeriksaan terbaru.

Jika hasil penyimpanan belum diketahui, gunakan **Periksa hasil** dengan akun, perangkat dan referensi operasi semula. Jangan membuat draf pengganti. Pemeriksaan dan pemulihan mengikuti panduan draf biasa, termasuk setelah browser dibuka kembali. Referensi pemulihan tidak menyimpan salinan isi daftar atau rincian pengiriman pada penyimpanan browser.

Local bulk acceptance: seven unique native cases have latest passing results, client 6/6 and scoped type/lint passed. Actual main backend5 and fullfrontend6 passed; browser main5-2 passed 1/1 in 6.5310 minutes, including explicit preview/apply, one lost committed response, actual browser restart/original-key recovery, stale-tab locking, refresh, denied actor/branch and unchanged source order/stock/money. Six desktop1440/mobile360 screenshots were inspected. Source, runtime and proof hashes are recorded in `tmp/u07-bulk-candidate/acceptance-main5-2.json`. The original selector failure and its driver-only correction remain recorded; this is local synthetic-identity acceptance, not staging or production acceptance.

## Buang, pulihkan dan retensi 30 hari (migrasi 118)

Aturan D02 yang disetujui pada 16 Oktober 2026 berlaku untuk draf pesanan yang belum diajukan. Pesanan yang sudah diajukan, invoice, pembayaran dan jurnal tidak pernah dihapus.

- **Buang draf.** Pembuat draf memilih **Buang draf**, meninjau tanggal batas pemulihan, lalu mengonfirmasi. Draf hilang dari *Draf saya* dan tampil di *Draf yang dibuang*. Draf yang dibuang tidak dapat disimpan atau dibuka untuk diubah sampai dipulihkan.
- **Pulihkan draf.** Selama 30 hari sejak dibuang, pembuat dapat memilih **Pulihkan draf**. Setelah batas itu, pemulihan ditolak dengan pesan "Masa pemulihan draf ini sudah berakhir", meskipun pembersihan belum berjalan.
- **Pembersihan.** Worker (`OrderDraftPurgeWorker`) memeriksa setiap 6 jam. Draf yang batas pemulihannya lewat dihapus permanen, dalam batch berisi paling banyak 200 draf dan satu transaksi per draf. Yang dihapus: head draf, semua versi termasuk alamat pengiriman, receipt simpan/buang/pulihkan, event outbox draf, dan baris buang.
- **Jejak audit tetap disimpan.** Event `ordering.draft.saved`, `ordering.draft.discarded` dan `ordering.draft.restored` tetap ada. Pembersihan menambah satu event `ordering.draft.purged` per draf, berisi ID dan jumlah saja, tanpa isi draf atau data kontak.
- **Integritas.** Baris buang yang tanggalnya tidak cocok dengan event auditnya tidak pernah dihapus lebih awal. Worker mencatat status `integrity_refused`, dan baris itu perlu diperiksa sebelum dibersihkan manual.
- **Menonaktifkan.** Setel `Retention:OrderDraftPurge:Enabled=false` pada worker hanya bila ada perintah penahanan data (legal hold). Worker lalu mencatat peringatan saat mulai. Pengaturan ini tidak mengubah tombol buang dan pulihkan.

Migrasi 118 hanya membuat koleksi kosong `orderDraftDiscards`, lengkap dengan validator ketat (batas pemulihan tepat 30 hari setelah dibuang) dan indeks `restorableUntil`. Draf yang sudah ada tetap aktif.
