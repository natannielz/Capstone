# Data produk dummy v10

Manifest [demo-products-v10.json](../website/lib/domain/demo-products-v10.json) berisi tepat 200 produk kurasi dengan ID `demo-001` sampai `demo-200` dan SKU `DEMO-001` sampai `DEMO-200`. Nama, deskripsi dua kalimat, dan prompt gambar berbeda untuk setiap entri. Harga serta spesifikasi merupakan data simulasi capstone, tanpa merek dagang atau logo BNI.

| Rentang ID | Kelompok etalase | Jumlah | Koleksi |
| --- | --- | ---: | --- |
| 001–040 | Minuman | 40 | pantry / rapat |
| 041–100 | Pantry & konsumsi | 60 | pantry / rapat |
| 101–140 | Kebutuhan kantor | 40 | rapat |
| 141–200 | Merchandise | 60 | merchandise |

Kategori operasional menggunakan `OMI` untuk minuman, konsumsi, dan alat tulis habis pakai, serta `Smart` untuk aksesori kantor dan merchandise. `group` adalah kategori yang ditampilkan pada etalase; nilainya terpisah dari kategori operasional `category`.

| Field | Bentuk dan fungsi |
| --- | --- |
| `id` | Identitas stabil `demo-NNN` |
| `sku` | Kode unik `DEMO-NNN` |
| `name` | Nama produk Bahasa Indonesia, termasuk ukuran/isi bila diperlukan |
| `description` | Dua kalimat yang menjelaskan isi, bahan, kapasitas, atau pemakaian |
| `category` | `OMI` atau `Smart` |
| `unit` | Satuan jual seperti buah, botol, pak, kotak, atau set |
| `price` | Bilangan bulat harga simulasi dalam rupiah |
| `group` | Minuman, Pantry & konsumsi, Kebutuhan kantor, atau Merchandise |
| `collection` | `pantry`, `rapat`, atau `merchandise` |
| `image` | Aset pengiriman web `/images/products/generated/demo-NNN.webp` |
| `imagePrompt` | Subjek foto studio spesifik per produk, komposisi persegi, latar putih/abu muda, tanpa teks/logo/watermark/kolase |

Companion `website/lib/domain/demo-catalog-public-v10.json` hanya menyimpan `id`, `image`, `description`, `group`, dan `collection`. Berkas ini digunakan untuk metadata etalase agar prompt pembuatan gambar tidak perlu dimuat di browser. Manifest lengkap tetap menjadi acuan provisioning data.

## Hasil pembuatan gambar

Seluruh 200 gambar telah dibuat melalui 200 panggilan terpisah ke alat pembuatan gambar bawaan, satu hasil untuk setiap produk. Setiap gambar menggunakan subjek dan bahan sesuai produk, dengan komposisi foto studio persegi dan latar netral. Hasil ditinjau secara visual selama pembuatan. Prompt untuk seluruh produk disimpan pada field `imagePrompt` di [manifest lengkap](../website/lib/domain/demo-products-v10.json).

Pemeriksaan akhir mencatat 200 aset WebP berukuran 1024 × 1024 piksel, 200 hash SHA-256 yang berbeda, dan total ukuran pengiriman **11.395.138 byte** (sekitar 11,40 MB). Tidak ada aset yang hilang atau gagal pemeriksaan. Ringkasan yang dapat dibaca mesin tersedia di [data-dummy-v10-results.json](data-dummy-v10-results.json).

## Lokasi aset dan asal gambar

Seluruh jalur relatif berikut berada di workspace lokal `C:\Users\acer\Documents\ALL My Project\Capstone`. `NNN` adalah nomor tiga digit dari `001` sampai `200`.

| Berkas | Lokasi | Kegunaan |
| --- | --- | --- |
| PNG asli | `outputs/generated-products-v10/demo-NNN.png` | Salinan hasil asli untuk arsip lokal; tidak dikirim sebagai gambar katalog |
| Catatan asal gambar | `outputs/generated-products-v10/demo-NNN.json` | ID produk, jalur sumber alat, jalur PNG dan WebP, SHA-256 WebP, ukuran byte, serta dimensi |
| WebP situs | `website/public/images/products/generated/demo-NNN.webp` | Aset katalog yang diakses melalui `/images/products/generated/demo-NNN.webp` |
| Data dan prompt lengkap | `website/lib/domain/demo-products-v10.json` | Penghubung produk, deskripsi, harga simulasi, prompt, dan gambar |

PNG sumber yang dibuat alat tetap berada di folder `C:\Users\acer\.codex\generated_images\`; jalur tepat setiap sumber tercatat pada field `source` di catatan asal gambar. Arsip PNG dan catatan tersebut disimpan lokal di luar Git. Versi WebP dibuat dari PNG dengan batas dimensi 1024 × 1024, tanpa memperbesar gambar, menggunakan kualitas 84.

## Cara pemeriksaan

Pemeriksa lokal `.tools/check-product-images-v10.mjs --final` membaca manifest dan memastikan jumlah produk tepat 200 dengan ID, nama, deskripsi, dan jalur gambar yang unik. Untuk setiap entri, pemeriksa membaca WebP, memeriksa format dan dimensi melalui `sharp`, menghitung SHA-256, mencocokkannya dengan catatan asal gambar, serta memastikan PNG asli tersedia dan tidak kosong. Hash yang sama untuk dua produk menyebabkan pemeriksaan gagal; hash berbeda membuktikan berkas gambar berbeda, sedangkan kesesuaian subjek ditinjau secara visual.

Pemeriksaan katalog publik lokal juga menemukan 200 produk dan deskripsi yang unik, serta lima gambar sampel yang dapat dimuat. Catatan ini mendokumentasikan aset dan pemeriksaan lokal; status penerbitan situs dicatat terpisah pada catatan rilis.
