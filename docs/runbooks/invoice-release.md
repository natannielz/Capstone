# Persetujuan penerbitan tagihan

Halaman `/billing/invoice-releases` menambahkan persetujuan dua orang sebelum invoice diterbitkan. Alurnya: penerimaan barang yang memenuhi syarat → usulan penerbitan → pemeriksa lain menyetujui atau menolak → Finance menerbitkan invoice yang tetap (immutable), jurnal piutang dan dokumen pribadi melalui alur invoice yang sudah ada. Penerbitan tidak mengubah stok fisik, alokasi pembayaran atau HPP yang sudah tercatat (F11, [checklist utama](../quality/production-progress.md)).

## Izin

| Izin | Untuk | Catatan |
| --- | --- | --- |
| `invoice.release.read` | Melihat penerimaan yang memenuhi syarat, usulan dan riwayatnya | Harus bersama `invoice.read` dan cakupan setiap penerimaan |
| `invoice.release.propose` | Membuat atau mengubah usulan | Wajib verifikasi dua langkah |
| `invoice.release.approve` | Menyetujui atau menolak usulan | Wajib verifikasi dua langkah dan bukti orang yang berbeda dari pengusul |
| `invoice.issue` (sudah ada) | Menerbitkan invoice dari usulan yang disetujui | Finance tidak mendapat izin pesanan atau izin pembeli |

Fitur ini tidak memberi izin, kebijakan keuangan, nominal atau pemetaan akun kepada peran mana pun. Semuanya diberikan melalui matriks D02 yang disetujui.

## Mengusulkan

1. Pilih bukti penerimaan pada **Pilih bukti penerimaan**, lalu **Periksa sumber penerbitan**. Penerimaan diambil dari pemilik pengiriman; jumlah, harga historis dan kemasan tidak dapat diubah di sini.
2. Pada **Lengkapi usulan**, isi periode pembukuan, masa berlaku (WIB), referensi persetujuan templat, penomoran dan kelayakan, empat akun pembukuan, serta alokasi pajak dan biaya per baris penerimaan. Nilai kosong tidak dianggap nol.
3. Centang konfirmasi pemeriksaan, tinjau, lalu kirim. Usulan tersimpan dengan revisi; perubahan berikutnya harus menyebut revisi yang terakhir dilihat.

## Memeriksa dan menerbitkan

Pemeriksa harus orang yang berbeda dari pengusul, dengan verifikasi dan akses yang masih berlaku. Sumber, sisa anggaran pajak/biaya yang disetujui, periode pembukuan dan akses diperiksa ulang saat persetujuan dan sekali lagi sebelum invoice selesai diterbitkan. Setiap invoice setelah migrasi 119 memiliki ikatan rilis yang tetap ke invoice, penerimaan, audit dan peristiwa persetujuan aslinya.

## Migrasi 119

Migrasi memasang penyimpanan rilis kosong dan menandai invoice lama yang lengkap sebagai `pre_release` dengan jangkar migrasi; tidak ada persetujuan historis yang dikarang. Ikatan yang hilang atau rusak setelah pemasangan menolak permintaan (fail closed), termasuk pemutaran ulang kunci asli.

## Pemulihan

Browser hanya menyimpan referensi permintaan (akun, aksi, kunci asli), tidak menyimpan nilai keuangan. Bila hasil belum pasti, buka kembali halaman dengan akun yang sama dan pilih **Periksa hasil permintaan asli**. Penolakan, konflik atau hasil "belum dipastikan" mempertahankan kunci asli; jangan membuat permintaan pengganti.
