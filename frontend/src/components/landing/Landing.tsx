import Link from "next/link";
import { Brand } from "../brand";
import { PilotSiteFooter } from "../pilot-site-chrome";
import { KemasanDemo } from "./KemasanDemo";
import styles from "./landing.module.css";

type Step = { status: string; time: string; text: string };

const REVIEW: Step[] = [
  { status: "Diajukan", time: "Sen 09.12", text: "PIC cabang mengajukan 2 karton air mineral dan 3 dus kopi sachet untuk rapat Kamis." },
  { status: "Ditinjau toko", time: "Sen 10.40", text: "Petugas toko memeriksa barang, jumlah kemasan, dan catatan permintaan." },
];
const PILOT_STEPS: Step[] = [...REVIEW,
  { status: "Hasil peninjauan", time: "Sen 11.05", text: "Toko menerima permintaan dan menuliskan tindak lanjut. Hasilnya tampil di akun Anda." },
];
const FULL_STEPS: Step[] = [...REVIEW,
  { status: "Disiapkan", time: "Sen 13.30", text: "Barang diambil dari gudang dan dikemas sesuai permintaan." },
  { status: "Dikirim", time: "Sel 08.15", text: "Kiriman berangkat dengan nomor pengiriman yang bisa Anda ikuti." },
  { status: "Diterima cabang", time: "Sel 11.20", text: "PIC cabang mencatat barang yang diterima, termasuk bila ada selisih." },
  { status: "Tagihan terbit", time: "Sel 15.00", text: "Invoice terbit setelah penerimaan selesai, sesuai barang yang diterima." },
];

const PACKING = [
  { need: "Pantry harian", items: "Kopi, teh, gula, camilan", pack: "Dus isi 24 sachet" },
  { need: "Kebutuhan rapat", items: "Air mineral, snack box, alat tulis", pack: "Karton isi 24 botol" },
  { need: "Kegiatan tim", items: "Tumbler, tas kanvas, kaos", pack: "Kotak isi 1 sampai 12" },
];

/** Public landing page. `pilot` limits the story to what Release 1 does: request and store review. */
export function Landing({ pilot }: { pilot: boolean }) {
  const steps = pilot ? PILOT_STEPS : FULL_STEPS;
  return <div className={styles.page}>
    <a href="#konten" className={styles.skip}>Lewati navigasi</a>
    <section className={styles.hero} aria-labelledby="judul-beranda">
      <header className={styles.header}>
        <Brand inverse context="Kebutuhan kerja & harian" />
        <nav aria-label="Navigasi utama" className={styles.nav}>
          <Link href="/shop">Katalog toko</Link>
          <Link href="/ordering/requests">Permintaan saya</Link>
          <a href="#layanan">Cara mengajukan</a>
        </nav>
        <Link href="/identity/login" className={styles.headerSignIn}>Masuk</Link>
      </header>
      <div className={styles.copy} id="konten" tabIndex={-1}>
        <h1 id="judul-beranda">Kebutuhan cabang, dihitung per kemasan.</h1>
        <p>{pilot
          ? "Pilih barang dari katalog akun Anda, tentukan jumlah kemasan, lalu ajukan ke toko. Petugas meninjau setiap permintaan, dan Anda mengikuti hasilnya dari akun yang sama."
          : "Pilih barang dari katalog akun Anda, tentukan jumlah kemasan, lalu ajukan ke toko. Petugas meninjau, menyiapkan, dan mengirim pesanan; Anda mengikuti statusnya sampai tagihan terbit."}</p>
        <div className={styles.actions}>
          <Link href="/identity/login" className={styles.primary}>Masuk ke akun</Link>
          <Link href="/shop" className={styles.secondary}>Lihat katalog</Link>
        </div>
      </div>
      <KemasanDemo />
    </section>

    <main className={styles.main}>
      <section className={styles.trail} id="layanan" aria-labelledby="judul-riwayat">
        <div className={styles.intro}>
          <h2 id="judul-riwayat">Setiap permintaan punya riwayat.</h2>
          <p>Akun Anda mencatat siapa meninjau, kapan, dan apa catatannya. Contoh di samping memakai jam WIB, seperti tampilan di akun.</p>
        </div>
        <figure className={styles.record}>
          <figcaption>Contoh permintaan <strong>PRM-0142</strong> dari kantor cabang</figcaption>
          <ol aria-label="Contoh riwayat permintaan">
            {steps.map((step, index) => <li data-step={step.status} key={step.status} data-current={index === steps.length - 1 || undefined}>
              <span className={styles.time}>{step.time} WIB</span>
              <span className={styles.status}>{step.status}</span>
              <span className={styles.stepText}>{step.text}</span>
            </li>)}
          </ol>
        </figure>
      </section>

      <section className={styles.catalog} id="kebutuhan" aria-labelledby="judul-katalog">
        <div className={styles.intro}>
          <h2 id="judul-katalog">Yang biasa diajukan cabang.</h2>
          <p>Setiap barang tercatat dengan isi kemasan, minimum, dan kelipatannya, jadi jumlah yang Anda ajukan selalu bisa disiapkan utuh.</p>
          <Link href="/shop" className={styles.inlineLink}>Buka katalog</Link>
        </div>
        <table className={styles.packing}>
          <caption>Contoh. Isi katalog mengikuti akun organisasi Anda.</caption>
          <thead><tr><th scope="col">Kebutuhan</th><th scope="col">Contoh barang</th><th scope="col">Contoh kemasan</th></tr></thead>
          <tbody>{PACKING.map(row => <tr data-row key={row.need}><th scope="row">{row.need}</th><td>{row.items}</td><td>{row.pack}</td></tr>)}</tbody>
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
    </main>
    <PilotSiteFooter />
  </div>;
}
