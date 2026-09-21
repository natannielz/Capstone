# Sumber font tampilan v3

Diunduh pada 17 September 2026 langsung dari repositori resmi [Google Fonts](https://github.com/google/fonts). Berkas TTF asli disimpan lokal tanpa modifikasi atau subset. Integrasi komponen/CSS dikerjakan terpisah.

| Peran | Berkas lokal | Sumber resmi | Lisensi lokal |
| --- | --- | --- | --- |
| Display — Archivo Black Regular | [archivo-black.ttf](../website/public/fonts/archivo-black.ttf), 90.988 byte | [ArchivoBlack-Regular.ttf](https://github.com/google/fonts/blob/main/ofl/archivoblack/ArchivoBlack-Regular.ttf) | [OFL-ArchivoBlack.txt](../website/public/fonts/OFL-ArchivoBlack.txt) |
| Body — DM Sans variable, upright | [dm-sans-variable.ttf](../website/public/fonts/dm-sans-variable.ttf), 240.164 byte | [DMSans\[opsz,wght\].ttf](https://github.com/google/fonts/blob/main/ofl/dmsans/DMSans%5Bopsz%2Cwght%5D.ttf) | [OFL-DMSans.txt](../website/public/fonts/OFL-DMSans.txt) |

Kedua font disertai salinan SIL Open Font License dari folder sumber masing-masing: [Archivo Black OFL](https://github.com/google/fonts/blob/main/ofl/archivoblack/OFL.txt) dan [DM Sans OFL](https://github.com/google/fonts/blob/main/ofl/dmsans/OFL.txt).

Pemeriksaan berkas mengonfirmasi format TrueType valid. Archivo Black adalah font statis dengan nilai weight metadata **400** meskipun bentuknya tebal; gunakan deklarasi weight 400 agar tidak ditambah bold sintetis. DM Sans memiliki sumbu **wght 100–1000** (default 400) dan **opsz 9–40** (default 9); deklarasikan rentang weight dan gunakan optical sizing otomatis bila diperlukan. Varian italic tidak diunduh.

SHA-256 untuk keterlacakan berkas:

```text
archivo-black.ttf
dd9a89a019b4849f66ab75455fe7bdf931311042cbb0f0f97acc061539703180

dm-sans-variable.ttf
8cd08d97e89c24d0aa92edd2f0f4c8ee6195eee9b7c9f154865a58b02f0c1c0d
```
