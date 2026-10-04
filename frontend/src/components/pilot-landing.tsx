import Link from 'next/link';
import { ArrowRight, ClipboardList, PackageCheck, Building2 } from 'lucide-react';
import { Brand } from './brand';
import styles from './pilot-landing.module.css';

export function PilotLanding() {
  return <div className={styles.page}>
    <a className={styles.skip} href="#main">Langsung ke isi halaman</a>
    <header className={styles.header}><Brand context="Kebutuhan cabang dan organisasi" /><nav aria-label="Navigasi utama"><Link href="/shop">Katalog</Link><Link href="/identity/login" className={styles.signIn}>Masuk ke akun <ArrowRight size={17} aria-hidden="true" /></Link></nav></header>
    <main id="main">
      <section className={styles.hero} aria-labelledby="hero-title"><div><span className={styles.eyebrow}>LAYANAN UNIT TOKO</span><h1 id="hero-title">Kebutuhan cabang,<br /><em>tercatat dengan jelas.</em></h1><p>Pilih barang beserta kemasannya, ajukan kebutuhan organisasi, lalu ikuti hasil peninjauan toko dalam satu tempat.</p><div className={styles.actions}><Link className={styles.primary} href="/shop">Lihat katalog kemasan <ArrowRight size={18} aria-hidden="true" /></Link><Link href="/ordering/requests">Lihat permintaan saya</Link></div><p className={styles.access}>Untuk cabang BNI dan akun usaha yang telah disetujui.</p></div>
        <aside className={styles.guide} aria-label="Tahapan permintaan"><p className={styles.eyebrow}>DARI CABANG KE TOKO</p><ol><li><PackageCheck aria-hidden="true" /><div><strong>Pilih kemasan yang tepat</strong><span>Periksa isi, satuan, minimum, dan kelipatan jumlah.</span></div></li><li><ClipboardList aria-hidden="true" /><div><strong>Ajukan kebutuhan cabang</strong><span>Tinjau tujuan toko dan catatan sebelum mengirim.</span></div></li><li><Building2 aria-hidden="true" /><div><strong>Ikuti peninjauan toko</strong><span>Setiap permintaan memiliki nomor dan hasil peninjauan.</span></div></li></ol></aside>
      </section>
      <section className={styles.scope} aria-labelledby="scope-title"><div><span className={styles.eyebrow}>PERMINTAAN BARANG</span><h2 id="scope-title">Satu catatan untuk tindak lanjut bersama.</h2></div><p>Pengajuan mencatat kebutuhan Anda untuk ditinjau toko. Harga, ketersediaan barang, dan pengaturan pemenuhan akan dikonfirmasi oleh petugas saat menindaklanjuti permintaan.</p></section>
      <section className={styles.staff}><div><h2>Petugas toko dan pengelola katalog</h2><p>Masuk dengan akun yang ditugaskan untuk meninjau permintaan atau memperbarui katalog.</p></div><Link href="/identity/account">Buka ruang kerja <ArrowRight size={18} aria-hidden="true" /></Link></section>
    </main><footer className={styles.footer}><span>Unit Toko · Layanan organisasi</span><Link href="/identity/login">Akses akun terdaftar</Link></footer>
  </div>;
}
