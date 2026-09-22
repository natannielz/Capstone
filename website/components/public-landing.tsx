import Link from "next/link";
import {ArrowRight, ArrowUpRight, Building2, ShoppingBag, PackageCheck, Truck, ReceiptText} from "lucide-react";
import {Brand} from "./brand";
import {Art} from "./art";
import {LandingMotion} from "./public-motion";

const collections = [
  {title:"Pantry harian", collection:"pantry", heading:["Mulai hari.","Ambil jeda."], description:"Kopi untuk mengawali pagi, teh di sela pekerjaan, dan camilan untuk dinikmati bersama. Siapkan persediaan pantry sesuai kebutuhan Anda.", detail:"Kopi, teh, minuman & camilan", image:"/images/editorial/pantry-moment-v3.png", alt:"Kopi dan makanan ringan disiapkan di pantry kantor", action:"Lihat pilihan pantry"},
  {title:"Kebutuhan rapat", collection:"rapat", heading:["Meja siap.","Ide mengalir."], description:"Lengkapi pertemuan dengan konsumsi dan perlengkapan yang dibutuhkan. Pilih barang serta kemasannya sebelum membuat pesanan.", detail:"Konsumsi & perlengkapan pertemuan", image:"/images/hero.png", alt:"Meja pertemuan dengan air minum, snack, buku catatan, dan perlengkapan", action:"Siapkan kebutuhan rapat"},
  {title:"Merchandise", collection:"merchandise", heading:["Dibawa pergi.","Dipakai lagi."], description:"Tumbler, tas, dan pakaian untuk melengkapi kegiatan tim. Temukan pilihan yang sesuai untuk keseharian maupun acara bersama.", detail:"Tumbler, tas & pakaian", image:"/images/editorial/hero-still-life-v3.png", alt:"Tas kanvas, tumbler, dan pakaian untuk kegiatan tim", action:"Jelajahi merchandise"},
];

function CtaArrow(){return <span className="home13-cta-arrow" aria-hidden="true"><ArrowUpRight size={21}/></span>;}

export function PublicLanding(){
 return <LandingMotion>
  <a href="#home-main" className="home-skip">Lewati navigasi</a>
  <div className="home-topline"><div><span>Studi kasus Unit Toko · Divisi BNI</span><span>Demo capstone</span></div></div>
  <header className="home-header">
   <Brand/>
   <nav aria-label="Navigasi beranda"><a href="#kebutuhan">Koleksi toko</a><a href="#layanan">Cara berbelanja</a><Link href="/staff/login">Portal internal</Link></nav>
   <a className="home13-cta home13-cta-small" href="#akses"><span>Pilih akses</span><CtaArrow/></a>
  </header>
  <main id="home-main">
   <section className="home13-cover" aria-labelledby="home-title">
    <p className="home13-kicker">UNIT TOKO / PANTRY, RAPAT & MERCHANDISE</p>
    <div className="home13-cover-heading">
     <h1 id="home-title"><span className="home13-title-mask"><span className="home13-title-line">Pilihan baik,</span></span><span className="home13-title-mask"><span className="home13-title-line">untuk <em>hari Anda.</em></span></span></h1>
     <div className="home13-cover-intro"><p>Dari kopi pertama hingga agenda terakhir. Temukan kebutuhan harian, pertemuan, dan kegiatan tim di Unit Toko.</p><Link href="/shop" className="home13-cta"><span>Belanja pelanggan</span><CtaArrow/></Link><Link href="/staff/login" className="home13-line-link">Portal divisi & petugas<ArrowRight size={17} aria-hidden="true"/></Link></div>
    </div>
    <figure className="home13-cover-figure">
     <div className="home13-cover-photo"><Art src="/images/editorial/hero-still-life-v3.png" alt="Pilihan Unit Toko: tas kanvas, tumbler, pakaian, kopi, dan camilan" fetchPriority="high" loading="eager" sizes="(max-width: 800px) 100vw, 92vw"/><span className="home13-cover-curtain" aria-hidden="true"/></div>
     <figcaption><span>Barang yang menemani keseharian.</span><a href="#kebutuhan">Temukan koleksi<ArrowRight size={17} aria-hidden="true"/></a></figcaption>
    </figure>
   </section>

   <section className="home13-collections" id="kebutuhan" aria-labelledby="collections-title">
    <div className="home13-section-intro"><p className="home13-kicker">KOLEKSI TOKO</p><h2 id="collections-title">Ada di setiap<br/>bagian hari Anda.</h2><p>Tiga pilihan kebutuhan.<br/>Jelajahi sesuai rencana Anda.</p></div>
    <nav className="home13-collection-nav" aria-label="Jelajahi bagian koleksi">{collections.map(item=><a key={item.collection} href={`#koleksi-${item.collection}`}><span>{item.title}</span><ArrowUpRight size={18} aria-hidden="true"/></a>)}</nav>
    <div className="home13-chapters">{collections.map(item=><article className={`home13-chapter home13-chapter-${item.collection}`} key={item.collection} id={`koleksi-${item.collection}`} aria-labelledby={`title-${item.collection}`}>
     <div className="home13-chapter-rule" aria-hidden="true"><span/></div>
     <div className="home13-chapter-copy"><p className="home13-kicker">{item.title}</p><h3 id={`title-${item.collection}`}>{item.heading.map(line=><span key={line}>{line}</span>)}</h3><p className="home13-chapter-description">{item.description}</p><Link href={`/shop?collection=${item.collection}`} className="home13-cta"><span>{item.action}</span><CtaArrow/></Link><p className="home13-chapter-detail">{item.detail}</p></div>
     <figure className="home13-chapter-photo"><Art src={item.image} alt={item.alt} loading="lazy" sizes="(max-width: 800px) 100vw, 52vw"/></figure>
    </article>)}</div>
   </section>

   <section className="home13-service" id="layanan" aria-labelledby="service-title">
    <div className="home13-service-intro"><p className="home13-kicker">CARA BERBELANJA</p><h2 id="service-title">Dari pilihan Anda,<br/>sampai di tujuan.</h2><p>Pesanan, pengiriman, dan penerimaan terhubung. Ikuti perkembangannya dari akun Anda.</p></div>
    <ol className="home13-service-steps">{[
     {Icon:ShoppingBag,title:"Pilih kebutuhan",text:"Jelajahi koleksi, pilih kemasan, dan tambahkan barang ke keranjang."},
     {Icon:PackageCheck,title:"Toko menyiapkan",text:"Barang dan jumlah ditinjau sebelum pesanan disiapkan."},
     {Icon:Truck,title:"Pantau & terima",text:"Ikuti status kiriman, lalu konfirmasikan barang yang diterima."},
     {Icon:ReceiptText,title:"Lihat tagihan",text:"Invoice diterbitkan setelah penerimaan pesanan diselesaikan."},
    ].map(({Icon,title,text})=><li key={title}><Icon size={24} aria-hidden="true"/><h3>{title}</h3><p>{text}</p></li>)}</ol>
   </section>

   <section className="home13-access" id="akses" aria-labelledby="access-title">
    <div className="home13-section-intro"><p className="home13-kicker">AKSES UNIT TOKO</p><h2 id="access-title">Pintu masuk yang tepat<br/>untuk kebutuhan Anda.</h2><p>Akun belanja pelanggan dan ruang kerja internal memiliki akses masing-masing.</p></div>
    <div className="home13-access-grid">
     <article className="home13-access-card home13-access-customer"><ShoppingBag size={29} aria-hidden="true"/><p className="home13-kicker">PELANGGAN</p><h3>Pilih barang.<br/>Kelola pesanan.</h3><p>Belanja dari katalog dan ikuti perkembangan pesanan pribadi Anda.</p><Link href="/shop" className="home13-cta"><span>Mulai belanja</span><CtaArrow/></Link><Link href="/customer/login" className="home13-line-link">Sudah punya akun? Masuk<ArrowRight size={17} aria-hidden="true"/></Link></article>
     <article className="home13-access-card home13-access-internal"><Building2 size={29} aria-hidden="true"/><p className="home13-kicker">DIVISI & PETUGAS</p><h3>Satu ruang kerja.<br/>Peran yang jelas.</h3><p>Untuk PIC divisi, admin, petugas toko, kurir, dan tim keuangan.</p><Link href="/staff/login" className="home13-cta"><span>Masuk portal internal</span><CtaArrow/></Link><span className="home13-access-note">Gunakan akun internal sesuai tugas Anda.</span></article>
    </div>
   </section>
  </main>
  <footer className="home-footer"><div className="home-footer-main"><div><Brand inverse/><p>Pantry, perlengkapan rapat,<br/>dan kebutuhan kegiatan tim.</p></div><nav aria-label="Akses dan informasi"><a href="#kebutuhan">Koleksi toko</a><Link href="/customer/login">Masuk pelanggan</Link><Link href="/staff/login">Portal divisi & petugas</Link></nav></div><div className="home-footer-bottom"><p>Demo capstone · Studi kasus divisi BNI. Bukan situs resmi BNI.</p><p>Data, foto, pengiriman, dan pembayaran adalah simulasi.</p></div></footer>
 </LandingMotion>;
}
