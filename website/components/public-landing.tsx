import Link from "next/link";
import {ArrowRight, ArrowUpRight, Building2, ShoppingBag, PackageCheck, Truck, ReceiptText} from "lucide-react";
import {Brand} from "./brand";
import {Art} from "./art";
import {LandingMotion} from "./public-motion";

const collections = [
  {title: "Pantry harian", description: "Kopi, teh, biskuit, dan kebutuhan bersama.", image: "coffee", alt: "Kopi sachet untuk persediaan pantry"},
  {title: "Kebutuhan rapat", description: "Konsumsi dan perlengkapan untuk pertemuan.", image: "snack", alt: "Snack box untuk kebutuhan rapat"},
  {title: "Merchandise", description: "Tumbler, tas, dan pakaian untuk kegiatan tim.", image: "tumbler", alt: "Tumbler stainless pilihan merchandise"},
];

export function PublicLanding() {
  return <LandingMotion>
    <a href="#home-main" className="home-skip">Lewati navigasi</a>
    <div className="home-topline"><div><span>Studi kasus Unit Toko · Divisi BNI</span><span>Demo capstone</span></div></div>
    <header className="home-header">
      <Brand/>
      <nav aria-label="Navigasi beranda"><a href="#kebutuhan">Pilihan kebutuhan</a><a href="#layanan">Layanan toko</a></nav>
      <a className="home-header-access" href="#akses">Pilih akses <ArrowUpRight size={18} aria-hidden="true"/></a>
    </header>
    <main id="home-main">
      <section className="home-hero" aria-labelledby="home-title">
        <div className="home-hero-copy">
          <p className="home-eyebrow">Selamat datang di Unit Toko</p>
          <h1 id="home-title"><span className="home-title-line">Pantry, rapat,</span>{" "}<span className="home-title-line">dan kebutuhan <em>tim.</em></span></h1>
          <p className="home-intro">Dari persediaan harian hingga merchandise kegiatan. Temukan barang yang dibutuhkan, dengan pesanan dan pengiriman yang tercatat.</p>
          <div className="home-hero-actions"><a href="#akses" className="home-button">Pilih akses Anda <ArrowRight size={18} aria-hidden="true"/></a><a href="#kebutuhan" className="home-text-link">Kenali pilihan toko</a></div>
          <div className="home-hero-note"><span className="home-note-line" aria-hidden="true"/><p>Belanja pelanggan dan operasional toko<br/><strong>memiliki pintu masuk masing-masing.</strong></p></div>
        </div>
        <figure className="home-hero-visual">
          <Art src="/images/editorial/hero-still-life-v3.png" alt="Tumbler, tas kanvas, kaos polo, kopi, dan snack pilihan Unit Toko" fetchPriority="high" loading="eager" sizes="(max-width: 800px) 100vw, 55vw"/>
          <figcaption><span>Pilihan Unit Toko</span><span>Untuk meja kerja dan kegiatan tim</span></figcaption>
        </figure>
      </section>

      <section className="home-access" id="akses" aria-labelledby="access-title">
        <div className="home-section-heading"><div><p className="home-eyebrow">Akses Unit Toko</p><h2 id="access-title">Lanjutkan sesuai kebutuhan Anda.</h2></div><p>Pilih toko pelanggan untuk belanja pribadi, atau portal internal untuk pemesanan divisi dan pekerjaan toko.</p></div>
        <div className="home-access-grid">
          <article className="home-access-card home-access-customer">
            <div className="home-access-heading"><span className="home-access-icon"><ShoppingBag size={25} aria-hidden="true"/></span><span>Untuk pelanggan</span></div>
            <h3>Temukan barang.<br/>Kelola pesanan Anda.</h3>
            <p>Jelajahi katalog, pilih kemasan, dan simpan barang di keranjang. Masuk ke akun pelanggan untuk membuat serta memantau pesanan.</p>
            <div className="home-access-actions"><Link href="/shop" className="home-button home-button-customer">Belanja pelanggan <ArrowRight size={18} aria-hidden="true"/></Link><Link href="/customer/login" className="home-text-link">Masuk akun pelanggan</Link></div>
          </article>
          <article className="home-access-card home-access-staff">
            <div className="home-access-heading"><span className="home-access-icon"><Building2 size={25} aria-hidden="true"/></span><span>Untuk divisi & petugas</span></div>
            <h3>Ruang kerja<br/>operasional toko.</h3>
            <p>PIC divisi, admin, petugas toko, kurir, dan tim keuangan masuk ke ruang kerja sesuai tugas dan kewenangannya.</p>
            <div className="home-access-actions"><Link href="/staff/login" className="home-button home-button-staff">Portal divisi & petugas <ArrowUpRight size={18} aria-hidden="true"/></Link><span className="home-access-hint">Gunakan akun internal Anda.</span></div>
          </article>
        </div>
      </section>

      <section className="home-collections" id="kebutuhan" aria-labelledby="collections-title">
        <div className="home-section-heading"><div><p className="home-eyebrow">Pilihan kebutuhan</p><h2 id="collections-title">Ada untuk keseharian Anda.</h2></div><p>Pilihan barang untuk pantry, pertemuan, dan kegiatan bersama.</p></div>
        <div className="home-collection-grid">{collections.map(item => <article className="home-collection" key={item.title}>
          <div className="home-collection-image"><Art src={`/images/products/${item.image}.png`} alt={item.alt} loading="lazy" sizes="(max-width: 640px) 90vw, 33vw"/></div>
          <div className="home-collection-copy"><h3>{item.title}</h3><p>{item.description}</p></div>
        </article>)}</div>
      </section>

      <section className="home-service" id="layanan" aria-labelledby="service-title">
        <div className="home-service-photo"><Art src="/images/editorial/handoff-moment-v3.png" alt="Ilustrasi petugas menyerahkan paket kepada penerima di kantor" loading="lazy" sizes="(max-width: 800px) 100vw, 45vw"/></div>
        <div className="home-service-copy"><p className="home-eyebrow">Layanan toko</p><h2 id="service-title">Tercatat sejak dipesan,<br/>hingga diterima.</h2><p>Setiap tahap terhubung dengan proses kerja toko. Pelanggan dan divisi dapat mengikuti perkembangan pesanannya.</p>
          <ul>{[
            {Icon: PackageCheck, title: "Pesanan ditinjau toko", text: "Barang dan jumlah diperiksa sebelum disiapkan."},
            {Icon: Truck, title: "Pengiriman dapat dipantau", text: "Status kiriman dan jumlah penerimaan dicatat."},
            {Icon: ReceiptText, title: "Tagihan sesuai penerimaan", text: "Invoice diterbitkan setelah penerimaan diselesaikan."},
          ].map(({Icon, title, text}) => <li key={title}><Icon size={22} aria-hidden="true"/><div><h3>{title}</h3><p>{text}</p></div></li>)}</ul>
        </div>
      </section>
    </main>
    <footer className="home-footer"><div className="home-footer-main"><div><Brand inverse/><p>Pantry, perlengkapan rapat,<br/>dan kebutuhan kegiatan tim.</p></div><nav aria-label="Akses dan informasi"><a href="#akses">Pilih akses</a><Link href="/customer/login">Masuk pelanggan</Link><Link href="/staff/login">Portal divisi & petugas</Link></nav></div><div className="home-footer-bottom"><p>Demo capstone · Studi kasus divisi BNI. Bukan situs resmi BNI.</p><p>Data, foto, pengiriman, dan pembayaran adalah simulasi.</p></div></footer>
  </LandingMotion>;
}
