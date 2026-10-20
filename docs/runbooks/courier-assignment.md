# Penugasan dan penyerahan tugas kurir

Halaman `/fulfillment/courier-assignments` memungkinkan petugas dengan izin `shipment.assign` menyerahkan tugas pengiriman yang sudah berangkat kepada kurir lain, misalnya karena kurir pertama berhalangan. Tidak ada izin baru; verifikasi dua langkah dan cakupan `shipment.assign` yang sudah ada tetap berlaku.

## Yang tetap dan yang berubah

- **Tetap:** kurir saat keberangkatan, dispatch yang diterima, dokumen terbit (Surat Jalan), serta kunjungan, QR dan tanda terima lama.
- **Berubah:** penugasan saat ini, dengan revisi yang dimulai dari 1 dan naik satu pada setiap penyerahan. Setiap penyerahan menyimpan alasan, petugas, waktu dan kurir sebelumnya.

QR penerimaan dari revisi sebelumnya tidak dapat dipakai lagi, termasuk bila tugas kembali ke kurir semula (A→B→A). Kontrol kunjungan dan QR di halaman kiriman membandingkan akun yang masuk dengan kurir yang bertugas saat ini, bukan kurir saat keberangkatan. Setelah penerima mengonfirmasi tanda terima, tugas tidak dapat diserahkan lagi. Penyerahan tidak mengubah stok, pembayaran, pembukuan atau barang fisik.

## Menyerahkan tugas

1. Pilih kiriman. Halaman menampilkan kurir saat keberangkatan, kurir yang bertugas dan riwayat penyerahan.
2. Pilih kurir pengganti dari daftar kurir yang memenuhi syarat menurut penugasan yang disetujui. Kurir yang sedang bertugas tidak dapat dipilih lagi.
3. Tinjau, isi alasan, lalu konfirmasi. Permintaan memakai revisi yang ditinjau (`If-Match`) dan kunci idempotensi; bila tugas berubah sementara itu, tinjau ulang.

## Migrasi 120

Migrasi memasang penyimpanan penugasan dengan jangkar tetap untuk setiap kiriman yang sudah berangkat, termasuk pengikatan riwayat lama yang terverifikasi. Prefiks riwayat 1–119 yang tidak lengkap, efek yatim atau penghitung yang rusak ditolak sebelum DDL.

## Pemulihan

Referensi permintaan disimpan per akun dan per permintaan asli di penyimpanan lokal browser; bila penyimpanan gagal, permintaan tidak dikirim. Hasil yang belum pasti diperiksa dengan kunci asli melalui halaman yang sama. Hasil asli tetap dapat dibaca meskipun ada penyerahan berikutnya.
