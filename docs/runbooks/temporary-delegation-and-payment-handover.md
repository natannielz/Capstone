# Delegasi sementara dan serah-terima pemeriksaan pembayaran

Status: alur delegasi sementara dan serah-terima pemeriksaan pembayaran terhubung serta teruji lokal pada schema113. R10 masih parsial karena serah-terima kurir belum diterima. Staging dan produksi belum diterima; bukti serta kekurangan dicatat pada `docs/quality/production-progress.md`.

## Mengajukan dan menyetujui delegasi

1. Buka **Delegasi sementara** dari akun Anda (`/administration/delegations`). Pilih **Akses sumber** yang masih berlaku dan memang boleh didelegasikan. Daftar kosong memerlukan penetapan akses oleh pihak yang berwenang; halaman ini tidak menambah izin sumber.
2. Masukkan **Nomor akun penerima**, pilih **Periksa penerima**, lalu pilih keanggotaan aktif yang sesuai. Pilih izin, waktu mulai/berakhir dalam WIB, dan alasan yang menjelaskan kebutuhan serah-terima.
3. Gunakan **Tinjau pengajuan delegasi**. Periksa penerima, lingkup, izin, waktu, dan alasan pada konfirmasi sebelum mengirim.
4. Penyetuju yang berwenang membuka usulan tersebut, memeriksa rincian, lalu menggunakan tindakan **Setujui delegasi** yang tersedia. Pemberi, penerima, dan penyetuju harus memenuhi pemisahan orang yang berlaku; mengganti akun milik orang yang sama tidak melewati pemeriksaan ini.

Persetujuan belum berarti izin efektif sebelum waktu mulai. Izin turunan bergantung pada sumber, keanggotaan, lingkup organisasi, identitas, dan sesi yang tetap berlaku. Masa berakhir tidak dapat melampaui sumbernya. Izin turunan tidak dapat dijadikan sumber delegasi berikutnya.

Pada **Rincian delegasi**, **Catat serah-terima** menyimpan catatan peserta dan riwayatnya. **Hentikan delegasi**, jika tersedia bagi akun saat ini, menghentikan delegasi melalui keputusan tercatat. Kedaluwarsa atau pencabutan sumber menghentikan akses turunan; riwayat bukan izin baru. Gunakan **Perbarui sesi dan akses** setelah perubahan, lalu periksa hasil yang berlaku.

## Mengambil alih pemeriksaan pembayaran

1. Reviewer yang masih memiliki kewenangan membuka meja **Pemeriksaan pembayaran** (`/billing/payment-reviews`) dan memilih pengajuan yang perlu ditangani.
2. Pada **Serah-terima pemeriksaan**, gunakan **Muat pilihan serah-terima**. Cocokkan penanggung jawab, versi tugas, dan delegasi terkait. Jika diperlukan, selesaikan **Verifikasi dua langkah untuk serah-terima**.
3. Pilih delegasi terkait, tulis alasan, lalu gunakan **Tinjau serah-terima**. Tinjauan memperlihatkan pengajuan, versi tugas/delegasi, dan akun penanggung jawab asal.
4. Setelah memeriksa rincian, gunakan **Konfirmasi ambil pemeriksaan**. Muat kembali hasil sebelum melanjutkan pemeriksaan pembayaran melalui alurnya sendiri.

Pengambilalihan mengubah penanggung jawab pemeriksaan. Dana, invoice, dan keputusan verifikasi pembayaran tidak berubah. Reviewer harus memiliki kewenangan aktif dan memenuhi independensi saat tindakan dilakukan. Delegasi yang sudah berakhir atau dicabut boleh menjadi konteks serah-terima yang sah, tetapi tidak memberi reviewer akses baru.

Jika penanggung jawab, versi, keputusan, atau kaitan delegasi berubah sejak dibaca, tinjauan lama ditolak. Muat ulang tugas dan buat tinjauan dari catatan terbaru. **Batalkan serah-terima** menutup tinjauan tanpa mengirim perubahan.

## Hasil belum pasti, akses berubah, dan operasi

Jika respons terputus, pertahankan browser beserta penyimpanan lokalnya. **Periksa hasil asli** pada delegasi atau pemulihan serah-terima menggunakan referensi operasi yang sama, termasuk setelah browser dibuka kembali. Jangan membuat pengambilalihan pengganti sebelum hasil asal jelas. Referensi lokal berisi penunjuk pemulihan; catatan bisnis tetap berada di server.

Jika penyimpanan referensi tidak tersedia, pengiriman dikunci. Jika sesi, akses, atau identitas berubah, perbarui sesi/akses dan periksa kembali catatan yang dapat dibaca. Jangan memakai akun pengganti atau mengubah data langsung untuk memaksa pemulihan. Penolakan akses atau konflik tidak membuktikan bahwa operasi sebelumnya gagal tersimpan.

Operator menelusuri ID delegasi, ID pengajuan, revisi tugas, pelaku, alasan, dan kunci operasi pada riwayat/audit yang berwenang. Bukti atau kaitan yang hilang menyebabkan penolakan; pemulihan harus menjaga bukti asli, bukan membuat catatan persetujuan pengganti. Tidak ada akun operator nyata yang dibuat oleh fixture pengujian.

## Batas cakupan

Serah-terima tugas pada versi ini mencakup antrean pemeriksaan pembayaran. Penugasan kurir setelah dispatch merupakan celah yang masih perlu diselesaikan: izin delegasi tidak mengganti akun kurir yang terikat pada percobaan pengantaran dan tantangan penerimaan. Pada pemilik yang diperiksa, antrean persetujuan, procurement, persiapan/dispatch, dan anomali operator berupa pool role/lingkup, bukan penugasan reviewer tertentu; akun berwenang tetap mengambil tindakan melalui pemilik bisnis masing-masing. R10 tetap parsial sampai seluruh cakupannya dibuktikan. Kampanye access review, join/move/leave, serta penetapan orang utama/cadangan/recovery tetap mengikuti item R11/R12.

Pengujian lokal memakai identitas dan data sintetis melalui pemilik bisnis aplikasi. Hal itu tidak membuktikan tenant Auth0, host Windows, bank, operator, atau cabang pilot nyata. Penerimaan rilis memerlukan bukti lingkungan yang tercatat terpisah.
