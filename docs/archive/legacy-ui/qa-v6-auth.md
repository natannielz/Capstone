# QA API lokal v6 — autentikasi, akses, dan transaksi

Waktu mulai: 2026-09-17T12:17:33.759Z. Selesai: 2026-09-17T12:17:39.268Z.
Target tetap: `http://127.0.0.1:3000`. Hasil: **65 lulus, 0 gagal**.

Pengujian memakai 10 akun yang mewakili sembilan peran, termasuk dua PIC dari divisi berbeda. Kredensial dibaca dari berkas privat lokal dan tidak dicetak. Tidak ada kata sandi/akun yang diubah, reset database, atau akses mutasi ke produksi. Semua sesi milik harness dicoba diakhiri setelah pemeriksaan.

## Fixture lokal

Satu pesanan berlabel QA, tiga satuan SKU, dua pengiriman (1 + 2), satu invoice, satu pembayaran dengan dua alokasi. Penghitungan opname dibuat untuk menguji status usang/pengganti/pembatalan tanpa mengubah jumlah fisik hasil pengiriman. Fixture sengaja dipertahankan sebagai bukti lokal; identitasnya:

```json
{
  "label": "QA-V6-LOCAL-2026-09-17T12-17-33-758Z",
  "date": "2026-09-17",
  "product": "OMI-001",
  "orderId": "9bcc1438-b9c8-403b-b0a2-cf560fc54621",
  "shipmentIds": [
    "77505edc-f954-4c24-9379-5d7e9704154d",
    "9306fc3a-e700-4dcf-9510-b593699e5326"
  ],
  "invoiceId": "e4da6011-3a1b-4674-86e0-ef7eeb27408a",
  "invoiceTotal": 162000,
  "paymentId": "4d528980-94a2-4891-b428-d1d1a8ab3589",
  "stocktakeIds": [
    "848a7e31-1642-4982-a2f9-5493702eaadb",
    "bfbf0d67-c778-4718-8602-2db0295fad4b",
    "f3a91547-c73a-40fe-8d58-f27a4c02ac9d"
  ]
}
```

## Hasil

| Pemeriksaan | Hasil | Durasi |
| --- | --- | --- |
| Anonymous state, reports, and document access rejected | PASS | 2080 ms |
| Login and state: pic-a | PASS | 409 ms |
| Login and state: pic-b | PASS | 46 ms |
| Login and state: kepala | PASS | 45 ms |
| Login and state: staf | PASS | 46 ms |
| Login and state: kurir | PASS | 45 ms |
| Login and state: laporan | PASS | 41 ms |
| Login and state: penagihan | PASS | 41 ms |
| Login and state: pimpinan | PASS | 41 ms |
| Login and state: akuntansi | PASS | 42 ms |
| Login and state: admin | PASS | 41 ms |
| Two PIC divisions and their DTOs remain isolated | PASS | 0 ms |
| Staff, courier, and admin projections preserve restricted collections | PASS | 35 ms |
| Report CSV and printable report permission: pic-a | PASS | 42 ms |
| Report CSV and printable report permission: pic-b | PASS | 21 ms |
| Report CSV and printable report permission: kepala | PASS | 28 ms |
| Report CSV and printable report permission: staf | PASS | 20 ms |
| Report CSV and printable report permission: kurir | PASS | 21 ms |
| Report CSV and printable report permission: laporan | PASS | 22 ms |
| Report CSV and printable report permission: penagihan | PASS | 23 ms |
| Report CSV and printable report permission: pimpinan | PASS | 22 ms |
| Report CSV and printable report permission: akuntansi | PASS | 22 ms |
| Report CSV and printable report permission: admin | PASS | 18 ms |
| Unauthorized command rejected: pic-a / product.update | PASS | 699 ms |
| Unauthorized command rejected: pic-b / stocktake.create | PASS | 12 ms |
| Unauthorized command rejected: kepala / invoice.issue | PASS | 13 ms |
| Unauthorized command rejected: staf / invoice.issue | PASS | 13 ms |
| Unauthorized command rejected: kurir / stocktake.create | PASS | 12 ms |
| Unauthorized command rejected: laporan / order.review | PASS | 10 ms |
| Unauthorized command rejected: penagihan / stock.reserve | PASS | 12 ms |
| Unauthorized command rejected: pimpinan / payment.verify | PASS | 10 ms |
| Unauthorized command rejected: akuntansi / period.approve | PASS | 11 ms |
| Unauthorized command rejected: admin / order.create | PASS | 10 ms |
| Cross-origin command rejected before mutation | PASS | 8 ms |
| Create and review one labelled local order, reserve three units | PASS | 70 ms |
| First partial shipment dispatch is idempotent and invalidates old stocktake | PASS | 69 ms |
| PIC B cannot read or receive PIC A's valid shipment | PASS | 45 ms |
| Two partial shipments are received and finalized for exactly three units | PASS | 122 ms |
| Backdated invoice rejected, then combined invoice issued once | PASS | 51 ms |
| Known invoice document authorization: pic-a | PASS | 15 ms |
| Known invoice document authorization: pic-b | PASS | 13 ms |
| Known invoice document authorization: kepala | PASS | 11 ms |
| Known invoice document authorization: staf | PASS | 10 ms |
| Known invoice document authorization: kurir | PASS | 11 ms |
| Known invoice document authorization: laporan | PASS | 11 ms |
| Known invoice document authorization: penagihan | PASS | 12 ms |
| Known invoice document authorization: pimpinan | PASS | 12 ms |
| Known invoice document authorization: akuntansi | PASS | 12 ms |
| Known invoice document authorization: admin | PASS | 10 ms |
| Payment verification and two allocations fully settle only the new invoice | PASS | 86 ms |
| Stale opname rejects approval, can be replaced, and retains bidirectional history | PASS | 56 ms |
| Opname cancellation preserves stock and rejects a PIC | PASS | 57 ms |
| PIC A sees completed local fixture, PIC B and admin do not | PASS | 41 ms |
| Existing purchase document respects operational and PIC boundaries | PASS | 133 ms |
| Logout invalidates the original token; a fresh login retains the local invoice | PASS | 554 ms |
| Clean up QA session: pic-b | PASS | 9 ms |
| Clean up QA session: kepala | PASS | 10 ms |
| Clean up QA session: staf | PASS | 9 ms |
| Clean up QA session: kurir | PASS | 9 ms |
| Clean up QA session: laporan | PASS | 9 ms |
| Clean up QA session: penagihan | PASS | 10 ms |
| Clean up QA session: pimpinan | PASS | 9 ms |
| Clean up QA session: akuntansi | PASS | 8 ms |
| Clean up QA session: admin | PASS | 9 ms |
| Clean up QA session: pic-a | PASS | 9 ms |

## Batas bukti

Ini pengujian HTTP pada server lokal, bukan bukti deployment produksi atau studi usability. Tampilan, fokus, breakpoint, keranjang antartab, serta tombol ekspor penjualan di browser diuji terpisah oleh agen utama. CSV server dan dokumen cetak diuji sesuai hak peran. Pengujian ini tidak menyuntikkan kegagalan database ke logout; kasus 503/jaringan gagal mengikuti tes unit/UI terpisah. Tidak mengubah peran untuk membuat kurir tambahan. Dokumen pengadaan hanya diperiksa bila fixture pengadaan memang sudah tersedia.
