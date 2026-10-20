import Link from "next/link";
import { Brand } from "../brand";
import { PilotSiteFooter } from "../pilot-site-chrome";
import { KemasanJourney, type JourneyAct } from "./KemasanJourney";
import styles from "./landing.module.css";

const SUBMITTED: JourneyAct = { status: "Diajukan", time: "Sen 09.12", detail: "PT-00000142",
  text: "PIC cabang memilih barang dari katalog dan jumlah kemasannya, lalu mengajukannya ke toko. Permintaan langsung bernomor dan tercatat di akun cabang." };
const PILOT_ACTS: JourneyAct[] = [SUBMITTED,
  { status: "Ditinjau toko", time: "Sen 10.40", detail: "Diterima untuk diproses",
    text: "Petugas toko memeriksa setiap barang dan jumlah kemasannya. Hasil peninjauan beserta alasannya tampil di akun Anda." },
];
const FULL_ACTS: JourneyAct[] = [SUBMITTED,
  { status: "Ditinjau toko", time: "Sen 10.40", detail: "Disiapkan dalam 1 kardus",
    text: "Petugas toko memeriksa setiap barang dan jumlah kemasannya, lalu menyiapkannya dalam satu kardus bertanda nomor permintaan. Bila ada yang ditolak, alasannya tampil di akun Anda." },
  { status: "Dikirim", time: "Sel 08.15", detail: "Nomor kiriman KRM-0388",
    text: "Kardus berangkat ke cabang dengan nomor kiriman yang bisa Anda ikuti dari akun yang sama." },
  { status: "Diterima", time: "Sel 11.20", detail: "Tagihan terbit",
    text: "PIC cabang mencatat barang yang diterima, termasuk bila ada selisih. Tagihan terbit sesuai barang yang benar-benar diterima." },
];

const CATALOG = [
  { category: "Air dan minuman", item: "Air mineral 600 ml", pack: "Karton isi 24 botol", minimum: "1 karton" },
  { category: "Pantry kopi dan teh", item: "Kopi bubuk sachet", pack: "Dus isi 24 sachet", minimum: "1 dus" },
  { category: "Pantry kopi dan teh", item: "Teh celup melati", pack: "Kotak isi 25 kantong", minimum: "2 kotak" },
  { category: "Snack rapat", item: "Snack box rapat", pack: "Kotak per porsi", minimum: "10 kotak" },
  { category: "Alat tulis kantor", item: "Kertas HVS A4 70 gsm", pack: "Rim isi 500 lembar", minimum: "1 rim" },
  { category: "Alat tulis kantor", item: "Pulpen hitam", pack: "Dus isi 12 batang", minimum: "1 dus" },
  { category: "Kebersihan", item: "Tisu wajah 250 lembar", pack: "Karton isi 48 bungkus", minimum: "1 karton" },
  { category: "Merchandise", item: "Tumbler 500 ml", pack: "Satuan dalam kotak", minimum: "5 buah" },
];

/** Public landing page. `pilot` limits the story to what Release 1 does: request and store review. */
export function Landing({ pilot }: { pilot: boolean }) {
  return <div className={styles.page}>
    <a href="#konten" className={styles.skip}>Lewati navigasi</a>
    <header className={styles.header}>
      <Brand context="Kebutuhan kerja & harian" />
      <nav aria-label="Navigasi utama" className={styles.nav}>
        <Link href="/shop">Katalog toko</Link>
        <Link href="/ordering/requests">Permintaan saya</Link>
        <a href="#layanan">Cara mengajukan</a>
      </nav>
      <Link href="/identity/login" className={styles.headerSignIn}>Masuk</Link>
    </header>
    <main id="konten" tabIndex={-1} className={styles.main}>
      <section id="layanan" aria-labelledby="judul-beranda">
        <KemasanJourney acts={pilot ? PILOT_ACTS : FULL_ACTS} closing={<>
          <h2>Siap mengajukan kebutuhan cabang?</h2>
          <p>Masuk dengan akun organisasi Anda. Katalog, permintaan, dan riwayatnya menunggu di satu tempat.</p>
          <div className={styles.actions}>
            <Link href="/identity/login" className={styles.primary}>Masuk ke akun</Link>
            <Link href="/shop" className={styles.secondary}>Lihat katalog</Link>
          </div>
        </>}>
          <h1 id="judul-beranda">Kebutuhan cabang, dihitung per kemasan.</h1>
          <p>{pilot
            ? "Pilih barang dari katalog akun Anda, tentukan jumlah kemasan, lalu ajukan ke toko. Petugas meninjau setiap permintaan, dan Anda mengikuti hasilnya dari akun yang sama."
            : "Pilih barang dari katalog akun Anda, tentukan jumlah kemasan, lalu ajukan ke toko. Petugas meninjau, menyiapkan, dan mengirim pesanan; Anda mengikuti statusnya sampai tagihan terbit."}</p>
          <div className={styles.actions}>
            <Link href="/identity/login" className={styles.primary}>Masuk ke akun</Link>
            <Link href="/shop" className={styles.secondary}>Lihat katalog</Link>
          </div>
          <p className={styles.scrollCue}><span aria-hidden="true" />Gulir untuk mengikuti satu permintaan</p>
        </KemasanJourney>
      </section>

      <div className={styles.paper}>
        <section className={styles.catalog} id="kebutuhan" aria-labelledby="judul-katalog">
          <div className={styles.intro}>
            <h2 id="judul-katalog">Yang biasa diajukan cabang.</h2>
            <p>Setiap barang tercatat dengan isi kemasan, minimum, dan kelipatannya, jadi jumlah yang Anda ajukan selalu bisa disiapkan utuh.</p>
            <Link href="/shop" className={styles.inlineLink}>Buka katalog</Link>
          </div>
          <table className={styles.packing}>
            <caption>Contoh dari katalog toko. Isi katalog mengikuti akun organisasi Anda.</caption>
            <thead><tr><th scope="col">Barang</th><th scope="col">Kemasan</th><th scope="col">Minimum</th></tr></thead>
            <tbody>{CATALOG.map(row => <tr data-row key={row.item}>
              <th scope="row"><span className={styles.category}>{row.category}</span>{row.item}</th><td>{row.pack}</td><td>{row.minimum}</td>
            </tr>)}</tbody>
          </table>
        </section>

        <section className={styles.access} id="akses" aria-labelledby="judul-akses">
          <h2 id="judul-akses">Masuk sesuai peran Anda.</h2>
          <div className={styles.doors}>
            <div>
              <h3>Cabang dan akun usaha</h3>
              <p>Susun permintaan dari katalog organisasi Anda dan ikuti setiap peninjauan.</p>
              <Link href="/ordering/requests" className={styles.inlineLink}>Lihat permintaan saya</Link>
            </div>
            <div>
              <h3>Petugas toko</h3>
              <p>Tinjau permintaan yang ditujukan ke toko Anda dan perbarui katalog sesuai penugasan.</p>
              <Link href="/identity/account" className={styles.inlineLink}>Buka ruang kerja</Link>
            </div>
          </div>
          <p className={styles.accessNote}>Untuk cabang BNI dan akun usaha yang telah disetujui. Pilihan kerja mengikuti keanggotaan dan penugasan akun Anda.</p>
        </section>
      </div>
    </main>
    <PilotSiteFooter />
  </div>;
}
