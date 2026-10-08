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
