import Link from "next/link";
import { Art } from "./art";
import { MomentShowcase } from "./moment-showcase";
import { catalogLoginHref } from "@/lib/domain/catalog";
import {
  ArrowUpRight,
  Check,
  PackageCheck,
  Truck,
  ReceiptText,
} from "lucide-react";
import {Brand} from "./brand";
export {Brand} from "./brand";

const picks = [
  {
    id: "kopi",
    name: "Kopi sachet 20 pcs",
    category: "Pantry",
    image: "coffee",
  },
  {
    id: "snack",
    name: "Snack box rapat",
    category: "Konsumsi rapat",
    image: "snack",
  },
  {
    id: "tumbler",
    name: "Tumbler stainless 500 ml",
    category: "Merchandise",
    image: "tumbler",
  },
  {
    id: "tas",
    name: "Tas belanja kanvas",
    category: "Merchandise",
    image: "tote",
  },
];
export function Landing() {
  return (
    <div className="landing">
      <header className="site-header">
        <Brand />
        <nav aria-label="Navigasi utama">
          <a href="#pilihan">Pilihan kebutuhan</a>
          <a href="#solusi">Pengelolaan pesanan</a>
          <a href="#cara-kerja">Cara memesan</a>
        </nav>
        <Link href="/login" className="site-button small">
          Masuk portal
          <ArrowUpRight size={18} aria-hidden="true" />
        </Link>
      </header>
      <main>
        <section className="campaign-hero">
          <div className="campaign-heading">
            <div>
              <div className="overline">Pemesanan kebutuhan divisi BNI</div>
              <h1>
                Untuk meja kerja.
                <br />
                <span>Untuk seluruh divisi.</span>
              </h1>
            </div>
            <div className="campaign-intro">
              <p>
                Pesan kebutuhan pantry, rapat, dan merchandise, lalu pantau
                pengiriman serta tagihan divisi melalui Unit Toko.
              </p>
              <Link href={catalogLoginHref()} className="site-button">
                Masuk untuk memesan
                <ArrowUpRight size={20} aria-hidden="true" />
              </Link>
            </div>
          </div>
          <div className="campaign-stage">
            <div className="campaign-photo">
              <Art
                src="/images/editorial/hero-still-life-v3.png"
                alt="Pilihan tumbler, kopi, snack, tas kanvas, dan kaos polo di Unit Toko"
                fetchPriority="high"
                loading="eager"
                sizes="100vw"
              />
            </div>
          </div>
        </section>
        <section className="moments-section" id="pilihan">
          <div className="editorial-heading">
            <div>
              <h2>Pilih kebutuhan divisi</h2>
            </div>
            <p>Lihat pilihan untuk pantry, konsumsi rapat, dan acara divisi.</p>
          </div>
          <MomentShowcase />
        </section>
        <section className="product-shelf">
          <div className="shelf-heading">
            <h2>Pilihan produk</h2>
            <Link href={catalogLoginHref()} className="text-arrow">
              Masuk ke katalog
              <ArrowUpRight size={19} aria-hidden="true" />
            </Link>
          </div>
          <div className="shelf-products">
            {picks.map((p, i) => (
              <Link
                key={p.image}
                href={catalogLoginHref({ product: p.id })}
                className={`shelf-product shelf-product-${i}`}
              >
                <div className="shelf-art">
                  <Art
                    loading="lazy"
                    src={`/images/products/${p.image}.png`}
                    alt={p.name}
                  />
                  <span className="round-arrow">
                    <ArrowUpRight size={19} aria-hidden="true" />
                  </span>
                </div>
                <span className="product-kicker">{p.category}</span>
                <h3>{p.name}</h3>
              </Link>
            ))}
          </div>
        </section>
        <section className="connected-section" id="solusi">
          <div className="connected-photo">
            <Art
              loading="lazy"
              src="/images/editorial/handoff-moment-v3.png"
              alt="Ilustrasi staf toko menyerahkan paket kepada penerima di kantor divisi"
              sizes="(max-width: 850px) 100vw, 50vw"
            />
            <div className="connected-caption">
              <Check size={18} aria-hidden="true" />
              <span>Penerimaan barang dicatat oleh divisi.</span>
            </div>
          </div>
          <div className="connected-copy">
            <h2>Pesanan, pengiriman, dan tagihan tercatat.</h2>
            <p>
              PIC divisi, petugas toko, kurir, dan tim keuangan mengelola setiap
              tahap sesuai kewenangannya.
            </p>
            <div className="connected-benefits">
              <div>
                <p>
                  <strong>Pengiriman bertahap</strong>Jumlah barang pada setiap
                  pengiriman dan penerimaan dicatat terpisah.
                </p>
              </div>
              <div>
                <p>
                  <strong>Persetujuan barang pengganti</strong>Divisi dapat
                  menyetujui atau menolak usulan pengganti dari toko.
                </p>
              </div>
              <div>
                <p>
                  <strong>Invoice sesuai penerimaan</strong>Tagihan mengikuti
                  jumlah barang yang sudah diterima divisi.
                </p>
              </div>
            </div>
          </div>
        </section>
        <section className="journey-section" id="cara-kerja">
          <div className="editorial-heading">
            <div>
              <h2>Cara memesan</h2>
            </div>
          </div>
          <div className="journey-grid">
            {[
              {
                icon: PackageCheck,
                title: "Buat pesanan",
                text: "Masuk sebagai PIC divisi. Pilih barang, jumlah, dan tanggal kebutuhan, lalu kirim pesanan untuk ditinjau toko.",
              },
              {
                icon: Truck,
                title: "Pantau pengiriman",
                text: "Lihat status penyiapan dan pengiriman. Jika ada usulan barang pengganti, berikan keputusan melalui portal.",
              },
              {
                icon: ReceiptText,
                title: "Konfirmasi penerimaan",
                text: "Periksa dan catat barang yang diterima. Setelah invoice terbit, lihat tagihan dan catat pembayaran divisi.",
              },
            ].map((x, i) => (
              <article key={x.title}>
                <div className="journey-top">
                  <span>{i + 1}</span>
                  <x.icon size={31} aria-hidden="true" />
                </div>
                <h3>{x.title}</h3>
                <p>{x.text}</p>
              </article>
            ))}
          </div>
        </section>
      </main>
      <footer className="site-footer">
        <div>
          <Brand />
          <p>Pemesanan dan pengelolaan kebutuhan divisi BNI.</p>
        </div>
        <div>
          <a href="#pilihan">Pilihan kebutuhan</a>
          <a href="#cara-kerja">Cara memesan</a>
          <Link href="/login">
            Masuk portal
            <ArrowUpRight size={15} aria-hidden="true" />
          </Link>
        </div>
        <p className="demo-disclosure">
          Demo capstone · Studi kasus Unit Toko untuk divisi BNI.
          <br />
          Data, foto, dan transaksi adalah simulasi.
          <br />
          Bukan situs resmi BNI.
        </p>
      </footer>
    </div>
  );
}
