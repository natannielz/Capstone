# Inventaris sumber Unit Toko

Diperiksa ulang pada **8 Oktober 2026 (WIB)** dalam sesi konsolidasi. Dokumen ini menyimpan provenance dan batas pembacaan, bukan menyalin manual privat. [Spesifikasi](../requirements/unit-toko-production.md), [97 butir laporan](../requirements/unit-toko-report-2026-09-28.md), [keputusan](../quality/report-decisions.md) dan [ledger progres tunggal](../quality/production-progress.md) adalah rujukan kerja.

## Sumber dan pemeriksaan

| Sumber | Lokasi privat / identitas | Pemeriksaan sesi ini |
| --- | --- | --- |
| BPP Unit Toko, tujuh bab | `C:/Users/User/Downloads/BPP Unit Toko.zip`; SHA-256 `e48dad02705a6a232313172a487639ed80133b534ae5bcb0cf180a7cfc1b9f4d` | Seluruh tujuh DOC hasil ekstraksi cocok dengan hash ZIP member. Seluruh tujuh TXT dibaca dan direkonstruksi ulang langsung dari DOC piece tables di memori; hasil persis sama, tanpa replacement character. |
| Sepuluh diagram workflow | `C:/Users/User/Downloads/Diagram Unit Toko.zip`; SHA-256 `cb7e22108ba7ce58fa40e12db7a98eba63962489eac5e03700d241dd8cf92d27` | Seluruh sepuluh PNG cocok dengan hash ZIP member dan ditinjau secara visual oleh agen sumber sesi ini. |
| Ringkasan Eksekutif Hasil Rapat | `C:/Users/User/AppData/Local/Temp/codex-clipboard-aa7efe90-cad9-4d1a-81d2-fe09550d96f6.png` | Gambar asli tersedia dan ditinjau secara visual pada sesi ini. |
| Laporan 97 butir | Attachment `fe46b1db-7c26-4386-a721-ea0069cf7d4f/Pasted text.txt`, 23.723 bytes; SHA-256 `109fc265101e95756ff8e071fb64f8598bf5fffb124bdde17e874f635f8617d0` | Teks dibaca; tepat 97 ID unik, sesuai register P12/V10/I10/R14/S9/F12/U8/Q12/M10. |
| Instruksi migrasi penuh | Attachment `67077d34-8d75-4632-b646-019a6326a3df/Pasted text.txt`, 10.643 bytes; SHA-256 `d1e972f0d20a5901aa24e02845c605c359c96bbaca22021ab871418220e082dc` | Seluruh teks tersedia dan dibaca; melanjutkan cakupan lengkap, bukan menggantinya dengan pilot. |
| Permintaan cleanup/implementasi/rilis saat ini | Attachment `ffbe04ec-8d01-4430-86d9-af58f02f8b42/Pasted text.txt`, 12.911 bytes; SHA-256 `b248b1f33bf344998bf7e1653a645695580f9e777c8dc26f3b7f4e2451161842` | Instruksi aktif: tugas biasa tanpa Goal, cleanup recoverable, satu checklist, seluruh scope, local checks dahulu dan full CI/rilis pada akhir. |
| Instruksi pilot terbatas terdahulu | Attachment `85910477-0145-4cf7-888c-ad8794f6457a/Pasted text.txt`, 5.686 bytes; SHA-256 `ba9ab83ceb4075afacd47e0c349359605f92f9ee6753bec6a277b52de2c5ee1b` | Berlaku sebagai sejarah Release1; pembatasan scope aktifnya digantikan instruksi migrasi penuh dan permintaan terbaru. |

Attachment berada di bawah `C:/Users/User/.codex/attachments/`. Ekstraksi manual/diagram yang dipertahankan berada di `tmp/production-plan-inputs/`, di luar publikasi Git. Ketersediaan file tidak memberi izin menerbitkan manual, data pribadi atau source archive sebagai static assets.

**Batas penting:** DOC asli tidak dirender per halaman. Teks binary dan diagram terpisah telah diperiksa; layout/paginasi serta objek Word nonteks/embedded mungkin belum terlihat. Rujukan manual menggunakan bab/judul bagian, bukan nomor halaman yang belum diverifikasi.

## Isi sumber yang dipertahankan

| Manual | Ekstraksi privat |
| --- | --- |
| I — Gambaran Umum Unit Toko | `BPP Unit Toko/BAB I GAMBARAN UMUM UNIT TOKO.doc` dan `.txt` |
| II — Pengadaan Barang | `BPP Unit Toko/BAB II PENGADAAN BARANG.doc` dan `.txt` |
| III — Penjualan | `BPP Unit Toko/BAB III PENJUALAN.doc` dan `.txt` |
| IV — Retur Barang | `BPP Unit Toko/BAB IV RETUR BARANG.doc` dan `.txt` |
| V — Administrasi Transaksi & Pelaporan | `BPP Unit Toko/BAB V ADMINISTRASI TRANSAKSI & PELAPORAN.doc` dan `.txt` |
| VI — Penagihan dan Penutupan | `BPP Unit Toko/BAB VI PENAGIHAN DAN PENUTUPAN.doc` dan `.txt` |
| VII — Tanggung Jawab dan Wewenang | `BPP Unit Toko/BAB VII TANGGUNG JAWAB DAN WEWENANG.doc` dan `.txt` |

Sepuluh PNG di subfolder `Diagram Unit Toko/`: (1) Restok Otomatis, (2) Restok by Request/DDO, (3) Restok Barang Smart, (4) Retur Barang Expired, (5) Pasar Kering, (6) Penjualan Walk-in Customer, (7) Penjualan ke Divisi/Unit BNI, (8) Input Transaksi, (9) Laporan Harian & Penggabungan, (10) Penagihan/Pembayaran/Penutupan Akhir Bulan.

## Riwayat percakapan dan keputusan

Agen sumber sesi ini membaca seluruh **262 turn MAIN** melalui **27 halaman**, 27 September–8 Oktober, kemudian memproses **206.380 record** dari tiga rollout asli untuk memulihkan bagian yang dibatasi hasil tool. Seluruh prompt manusia tersedia dan dibaca; tidak ditemukan prompt manusia yang tidak dapat dipulihkan. Ini tidak mengklaim setiap technical tool item ditinjau ulang secara semantik.

| Rollout di bawah `C:/Users/User/.codex/sessions/` | Record |
| --- | ---: |
| `2026/09/27/rollout-2026-09-27T22-10-48-01a0e36b-3306-7960-829d-b2e983d053a1.jsonl` | 35.205 |
| `2026/09/29/rollout-2026-09-29T17-12-39-01a0e36b-3306-7960-829d-b2e983d053a1_01a0eca6-f429-7c53-ad2b-b2ee83d15489.jsonl` | 83.019 |
| `2026/10/04/rollout-2026-10-04T12-57-44-01a0e36b-3306-7960-829d-b2e983d053a1_01a1057d-5e0d-7420-bd00-6535eaedb65a.jsonl` | 88.156 |

Keputusan manusia yang tetap berlaku:

- Peluncuran untuk cabang BNI dan approved business accounts; November 5/30 adalah referensi, penerimaan bergantung readiness.
- Tidak ada SSO yang sudah ada; siapkan Auth0 pada stack Next.js/TypeScript, C#/.NET10, MongoDB dan private evidence yang dipilih.
- Setiap kredit bank pengembalian supplier advance milik **satu supplier dan satu purchase order secara utuh**; uang yang sama tidak dapat dipakai ulang untuk PO lain.
- Pemilihan hosting backend yang dapat bekerja didelegasikan; Vercel functions hanya bila sesuai. Delegasi tidak mengotorisasi pembelian, pesan nyata, transfer atau migrasi data operasional.
- Full migration/97 butir tetap wajib; pilot adalah hasil sementara yang dipertahankan.

## Ambiguitas sumber dan aturan interpretasi

- HET disebut dengan makna batas bawah dan maksimum pada sumber yang berbeda. Tidak ada toleransi harga otoritatif yang dapat disimpulkan; Finance/Operations harus menentukan kebijakan nyata.
- Contoh return window berbeda menurut produk; jangan membuat satu hak retur universal.
- Substitusi tanpa konfirmasi, posting tertunda satu–dua minggu, kesulitan pembatalan dan barang retur kembali ke rak adalah praktik/pain point historis. Kontrol eksplisit buyer consent, after-sales dan quarantine aman dalam spesifikasi/laporan mengarahkan perilaku aplikasi baru.
- Tebet/Pancoran/Cawang pada rapat adalah konteks, bukan persetujuan akun, catalog, inventory atau operator produksi.
- Header historis “Pinjaman Produktif” adalah residue template; bukan kebutuhan aplikasi.
- Uraian email/telepon/transfer dalam manual adalah flow bisnis, bukan izin bagi agen menghubungi pihak lain atau memindahkan uang.

