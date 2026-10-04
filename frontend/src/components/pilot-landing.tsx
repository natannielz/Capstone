import Link from 'next/link';
import { ArrowRight, ArrowUpRight, Building2, ClipboardList, PackageCheck, ShoppingBag, ClipboardCheck } from 'lucide-react';
import { Art } from './art';
import { LandingMotion } from './public-motion';
import { PilotSiteHeader, PilotSiteFooter } from './pilot-site-chrome';
import styles from './pilot-landing.module.css';

const inspirations = [
  { title: 'Pantry harian', key: 'pantry', heading: ['Mulai hari.', 'Ambil jeda.'], description: 'Dari kopi pagi hingga waktu rehat bersama. Catat kebutuhan rutin tim, lalu periksa pilihan dan isi kemasannya di katalog akun Anda.', image: '/images/editorial/pantry-moment-v3.png', alt: 'Ilustrasi editorial kopi dan makanan ringan di pantry kantor' },
  { title: 'Kebutuhan rapat', key: 'rapat', heading: ['Meja siap.', 'Ide mengalir.'], description: 'Pertemuan yang tertata dimulai dari kebutuhan yang jelas. Susun perlengkapan dan jumlah kemasan sesuai agenda cabang Anda.', image: '/images/hero.png', alt: 'Ilustrasi editorial meja pertemuan dengan minuman, camilan, dan buku catatan' },
  { title: 'Kegiatan tim', key: 'merchandise', heading: ['Dibawa pergi.', 'Dipakai lagi.'], description: 'Untuk keseharian dan kegiatan bersama. Periksa katalog yang tersedia bagi organisasi Anda sebelum mengajukan kebutuhan kepada toko.', image: '/images/products/tote.png', alt: 'Ilustrasi editorial tas kanvas polos untuk kegiatan sehari-hari' },
];
function CtaArrow() { return <span className="home13-cta-arrow" aria-hidden="true"><ArrowUpRight size={21} /></span>; }

export function PilotLanding() {
  return <LandingMotion>
    <a href="#home-main" className="home-skip">Lewati navigasi</a>
    <PilotSiteHeader />
    <main id="home-main" className={styles.main}>
      <section className="home13-cover" aria-labelledby="home-title">
        <p className="home13-kicker">UNIT TOKO / KEBUTUHAN CABANG & KEGIATAN TIM</p>
        <div className="home13-cover-heading">
          <h1 id="home-title"><span className="home13-title-mask"><span className="home13-title-line">Pilihan baik,</span></span><span className="home13-title-mask"><span className="home13-title-line">untuk <em>hari Anda.</em></span></span></h1>
          <div className="home13-cover-intro"><p>Pilih kemasan yang tepat, ajukan kebutuhan cabang, dan ikuti peninjauan toko. Satu tempat untuk menyiapkan hari kerja bersama.</p><Link href="/shop" className="home13-cta"><span>Jelajahi katalog</span><CtaArrow /></Link><Link href="/ordering/requests" className="home13-line-link">Lihat permintaan saya<ArrowRight size={17} aria-hidden="true" /></Link></div>
        </div>
        <figure className="home13-cover-figure">
          <div className="home13-cover-photo"><Art src="/images/editorial/hero-still-life-v3.png" alt="Ilustrasi editorial tas kanvas, tumbler, pakaian, kopi, dan camilan" fetchPriority="high" loading="eager" sizes="(max-width: 800px) 100vw, 92vw" /><span className="home13-cover-curtain" aria-hidden="true" /></div>
          <figcaption><span>Ilustrasi kebutuhan sehari-hari. Pilihan barang mengikuti katalog akun Anda.</span><a href="#kebutuhan">Temukan inspirasi<ArrowRight size={17} aria-hidden="true" /></a></figcaption>
        </figure>
      </section>
      <section className="home13-collections" id="kebutuhan" aria-labelledby="collections-title">
        <div className="home13-section-intro"><p className="home13-kicker">INSPIRASI KEBUTUHAN</p><h2 id="collections-title">Ada di setiap<br />bagian hari Anda.</h2><p>Gambaran kebutuhan bersama.<br />Buka katalog untuk melihat kemasan yang dapat diajukan.</p></div>
        <nav className="home13-collection-nav" aria-label="Jelajahi inspirasi kebutuhan">{inspirations.map(item => <a key={item.key} href={'#koleksi-' + item.key}><span>{item.title}</span><ArrowUpRight size={18} aria-hidden="true" /></a>)}</nav>
        <div className="home13-chapters">{inspirations.map(item => <article className={'home13-chapter home13-chapter-' + item.key} key={item.key} id={'koleksi-' + item.key} aria-labelledby={'title-' + item.key}>
          <div className="home13-chapter-rule" aria-hidden="true"><span /></div>
          <div className="home13-chapter-copy"><p className="home13-kicker">{item.title}</p><h3 id={'title-' + item.key}>{item.heading.map(line => <span key={line}>{line}</span>)}</h3><p className="home13-chapter-description">{item.description}</p><Link href="/shop" className="home13-cta"><span>Periksa pilihan di katalog</span><CtaArrow /></Link></div>
          <figure className="home13-chapter-photo"><Art src={item.image} alt={item.alt} loading="lazy" sizes="(max-width: 800px) 100vw, 52vw" /><figcaption className={styles.illustration}>Ilustrasi editorial</figcaption></figure>
        </article>)}</div>
      </section>
      <section className="home13-service" id="layanan" aria-labelledby="service-title">
        <div className="home13-service-intro"><p className="home13-kicker">CARA MENGAJUKAN</p><h2 id="service-title">Dari kebutuhan Anda,<br />ke peninjauan toko.</h2><p>Pengajuan mencatat kebutuhan cabang. Harga, ketersediaan, dan pemenuhan dikonfirmasi petugas saat tindak lanjut.</p></div>
        <ol className="home13-service-steps">{[
          { Icon: ShoppingBag, title: 'Pilih kebutuhan', text: 'Masuk dengan akun organisasi dan jelajahi katalog yang tersedia untuk Anda.' },
          { Icon: PackageCheck, title: 'Periksa kemasan', text: 'Cermati isi, satuan, minimum, dan kelipatan jumlah setiap kemasan.' },
          { Icon: ClipboardList, title: 'Ajukan ke toko', text: 'Pilih cabang, tinjau tujuan toko, lalu konfirmasikan kebutuhan dan catatan Anda.' },
          { Icon: ClipboardCheck, title: 'Ikuti hasilnya', text: 'Lihat nomor permintaan, hasil peninjauan, dan catatan petugas di akun Anda.' },
        ].map(({ Icon, title, text }) => <li key={title}><Icon size={24} aria-hidden="true" /><h3>{title}</h3><p>{text}</p></li>)}</ol>
      </section>
      <section className="home13-access" id="akses" aria-labelledby="access-title">
        <div className="home13-section-intro"><p className="home13-kicker">AKSES UNIT TOKO</p><h2 id="access-title">Kebutuhan bersama.<br />Tugas yang jelas.</h2><p>Untuk cabang BNI dan akun usaha yang telah disetujui. Pilihan kerja mengikuti penugasan akun Anda.</p></div>
        <div className="home13-access-grid">
          <article className="home13-access-card home13-access-customer"><ShoppingBag size={29} aria-hidden="true" /><p className="home13-kicker">CABANG & AKUN USAHA</p><h3>Pilih kemasan.<br />Ajukan kebutuhan.</h3><p>Susun permintaan cabang dan lihat hasil peninjauan toko dari akun organisasi Anda.</p><Link href="/shop" className="home13-cta"><span>Buka katalog</span><CtaArrow /></Link><Link href="/identity/login" className="home13-line-link">Masuk ke akun terdaftar<ArrowRight size={17} aria-hidden="true" /></Link></article>
          <article className="home13-access-card home13-access-internal"><Building2 size={29} aria-hidden="true" /><p className="home13-kicker">PETUGAS TOKO</p><h3>Satu ruang kerja.<br />Tindak lanjut terarah.</h3><p>Tinjau permintaan yang ditujukan ke toko Anda atau perbarui katalog sesuai penugasan.</p><Link href="/identity/account" className="home13-cta"><span>Buka ruang kerja</span><CtaArrow /></Link><span className="home13-access-note">Gunakan akun yang telah diberi akses oleh pengelola.</span></article>
        </div>
      </section>
    </main>
    <PilotSiteFooter />
  </LandingMotion>;
}
