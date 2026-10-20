# Kesehatan sistem

Halaman `/operations/health` menunjukkan apakah pekerjaan latar berjalan, apakah basis data siap, dan apakah pembersihan draf mengikuti jadwal. Akses memerlukan `technical.configure` pada cakupan administratif. Halaman tersedia pada rilis pilot dan rilis penuh, karena rilis pertama juga perlu diawasi.

Laporan hanya berisi jumlah, umur, dan status. Tidak ada nama, nominal, nomor dokumen, atau isi pekerjaan yang keluar dari basis data melalui laporan ini (Q10, [checklist utama](../quality/production-progress.md)). API: `POST /api/v1/operations/health/query` dengan `{"purpose": "operator-health"}`; balasan `Cache-Control: private, no-store`.

## Membaca laporan

Bagian atas memberi satu keadaan: **Semua pemeriksaan normal**, **Ada yang perlu diperhatikan**, atau **Perlu tindakan sekarang**. Keadaan mengikuti temuan terburuk. Halaman membaca ulang setiap menit selama tab terlihat; **Periksa ulang** membaca seketika.

| Temuan | Arti | Batas |
| --- | --- | --- |
| Pekerjaan gagal | Item outbox berstatus `dead`; tidak akan dicoba lagi otomatis. | Tindakan bila lebih dari 0 |
| Sedang dicoba ulang | Item berstatus `retry`; sistem masih mencoba. | Perhatian bila lebih dari 0 |
| Pekerjaan tertua menunggu | Umur item `pending`, `retry`, atau `leased` tertua. Item gagal dihitung, tidak diberi umur. | Perhatian ≥ 10 menit, tindakan ≥ 60 menit |
| Draf terlambat dibersihkan | Draf yang dibuang dan masa pemulihannya berakhir lebih dari sehari lalu, tetapi belum dihapus. | Perhatian bila lebih dari 0 |
| Versi skema | Migrasi terakhir yang diterapkan dibanding versi yang dibutuhkan aplikasi. | Tindakan bila berbeda |
| Basis data | Node yang dipakai API menerima penulisan (`isWritablePrimary`). | Tindakan bila tidak |
| Replikasi | Selisih waktu oplog replika terhadap node utama. Kosong bila akun basis data tidak boleh membaca `replSetGetStatus`. | Perhatian ≥ 10 detik, tindakan ≥ 60 detik |

**Antrean latar** memisahkan OCR dokumen (jenis yang berakhir `.ocr.requested`: bukti pembayaran dan dokumen pemasok) dari pekerjaan latar lain (notifikasi dan integrasi).

**Pembersihan draf terakhir** adalah waktu draf terakhir benar-benar dihapus. Audit hanya ditulis saat ada draf yang dihapus, jadi waktu lama tanpa draf terlambat bukan masalah.

## Menanggapi temuan

1. **Pekerjaan gagal.** Periksa log worker (`UnitToko.Worker`) untuk jenis pekerjaan yang gagal. Untuk OCR, pastikan layanan dokumen berjalan ([document-processing](document-processing.md)). Jangan mengubah status item langsung di basis data; perbaiki penyebab, lalu ikuti prosedur pemrosesan ulang modul terkait.
2. **Antrean menumpuk atau menunggu lama.** Pastikan proses worker hidup dan tidak berhenti karena migrasi (`Migrate` hanya untuk proses migrasi). Periksa beban CPU dan koneksi Mongo.
3. **Draf terlambat dibersihkan.** Pastikan `Retention:OrderDraftPurge:Enabled` tidak bernilai `false` pada worker. Penolakan karena ketidakcocokan audit disimpan untuk diperiksa dan tidak pernah dihapus lebih awal; laporkan ke penanggung jawab integritas.
4. **Versi skema berbeda.** Jalankan worker dalam mode migrasi sebelum membuka layanan. Bila basis data lebih baru dari aplikasi, aplikasi yang terpasang lebih lama dari migrasi; pasang versi yang sesuai.
5. **Basis data tidak menerima penulisan atau replika tertinggal.** Periksa status replica set dan node utama. Penulisan akan ditolak sampai node utama tersedia.

## Bukti pemeriksaan lokal

Backend: `OperatorHealthTests` (jumlah, umur, draf terlambat, tanpa isi bisnis; izin `technical.configure`), `ReleaseProfileTests.Pilot_maps_the_operator_health_report`, dan return path pada `Auth0ClaimsTests`. Frontend: `tests/operator-health.test.ts` (parser ketat, temuan dan batas, tampilan tabel dan lencana, navigasi akun) dan `tests/release-scope.test.ts`.
