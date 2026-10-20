# Pemulihan cadangan dan latihan pemulihan

Pemulihan diterima hanya jika setiap koleksi (dokumen, validator, indeks) dan setiap objek bukti sama persis dengan keadaan sebelum pencadangan. Pemulihan yang "berhasil" tetapi kehilangan satu dokumen, satu indeks unik, atau satu file bukti ditolak (Q09, [checklist utama](../quality/production-progress.md)).

Alat: `backend/tools/UnitToko.RestoreDrill` (verifikasi di `UnitToko.Platform/Operations/RestoreVerification.cs`). Manifest hanya berisi jumlah dan hash SHA-256; tidak ada isi dokumen atau byte file.

## Pemulihan nyata

Cara pencadangan tidak memengaruhi verifikasi: salinan file setelah `mongod` berhenti bersih, `mongodump`/`mongorestore`, atau snapshot layanan terkelola.

1. **Sebelum mencadangkan**, hentikan penulisan (API dan worker), lalu tangkap manifest:

   ```powershell
   dotnet UnitToko.RestoreDrill.dll capture --mongo-uri "<uri>" --database <nama> --evidence-root <folder bukti> --out manifest.json
   ```

   Simpan `manifest.json` bersama cadangan, terpisah dari server yang dicadangkan.
2. Cadangkan basis data dan folder bukti dengan cara yang dipilih.
3. **Setelah memulihkan**, sebelum membuka layanan, verifikasi:

   ```powershell
   dotnet UnitToko.RestoreDrill.dll verify --mongo-uri "<uri>" --database <nama> --evidence-root <folder bukti> --manifest manifest.json
   ```

   Kode keluar `0` dan `restore verified` berarti semua sama. Kode `1` mencetak setiap perbedaan (`FAIL collection missing: …`, `FAIL document count differs: …`, `FAIL validator or indexes differ: …`, `FAIL evidence object missing: …`). Jangan membuka layanan; pulihkan ulang dari cadangan lain dan laporkan perbedaannya.

Manifest ditangkap saat penulisan berhenti. Bila penulisan berjalan selama pencadangan, perbedaan yang muncul adalah perubahan nyata, bukan kesalahan alat.

## Latihan berkala (data sintetis)

```powershell
pwsh -NoProfile -File backend/tools/restore-drill.ps1 -MongodPath ./.tools/mongodb/mongodb-win32-x86_64-windows-8.2.12/bin/mongod.exe
```

Latihan menjalankan `mongod` sementara di `127.0.0.1:27091` (bukan basis data pengembangan 27028), menerapkan seluruh skema, mengisi 20.000 baris dan 200 objek bukti sintetis, menangkap manifest, menghentikan node secara bersih, menyalin file (cadangan), menyalin lagi ke lokasi baru (pemulihan), menyalakan node pada salinan dan memverifikasi. Kontrol negatif menghapus satu objek bukti dari salinan; verifikasi wajib menolaknya. Hasil di `tmp/restore-drill/<waktu>/drill-report.json`. Hapus folder `source`, `backup` dan `restore` setelah membaca laporan.

## Hasil latihan lokal

20 Oktober 2026, `mongod` 8.2.12, skema 118 (358 koleksi), 20.000 baris dan 200 objek bukti sintetis, cadangan 270 MB. Hasil `passed`, kontrol negatif terdeteksi (`evidence object missing`).

| Langkah | Waktu |
| --- | --- |
| Penerapan skema (persiapan latihan) | 44,8 s |
| Penangkapan manifest | 0,5 s |
| Pencadangan (salin file setelah berhenti) | 1,3 s |
| Salin pemulihan | 1,5 s |
| Node pulih siap | 2,0 s |
| Verifikasi | 1,9 s |
| **Total pemulihan (salin + siap + verifikasi)** | **5,4 s** |

Mesin pengembang sedang menjalankan batch uji lain saat latihan; angka ini batas atas untuk volume sintetis tersebut. Volume produksi akan lebih besar; ulangi latihan dengan `-Documents` dan `-EvidenceObjects` yang mendekati data nyata setelah hosting ditetapkan (Phase F), dan catat RTO dari hasil itu.
