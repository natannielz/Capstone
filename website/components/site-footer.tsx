import Link from "next/link";
import {ArrowUpRight} from "lucide-react";
import {Brand} from "./brand";

type SiteFooterProps = {
  customer?: boolean;
  accountHref?: string;
};

/** One navigational footer shared by the public site and customer pages. */
export function SiteFooter({customer = false, accountHref = "/account"}: SiteFooterProps) {
  const groups = [
    {title: "Belanja", links: [
      {label: "Semua produk", href: "/shop"},
      {label: "Pantry", href: "/shop?collection=pantry"},
      {label: "Kebutuhan rapat", href: "/shop?collection=rapat"},
      {label: "Merchandise", href: "/shop?collection=merchandise"},
    ]},
    {title: "Akun Anda", links: [
      {label: "Keranjang", href: "/cart"},
      {label: "Pesanan saya", href: "/account/orders"},
      {label: "Profil & alamat", href: accountHref},
    ]},
    {title: "Unit Toko", links: [
      {label: "Tentang toko", href: "/"},
      {label: "Cara berbelanja", href: "/#layanan"},
      {label: "Portal internal", href: "/staff/login"},
    ]},
  ];

  return <footer className={`site-footer${customer ? " site-footer-customer" : ""}`}>
    <div className="site-footer-inner">
      <div className="site-footer-main">
        <div className="site-footer-identity">
          <Brand inverse href={customer ? "/shop" : "/"} context={customer ? "Toko pelanggan" : "Kebutuhan kerja & harian"} className="site-footer-brand"/>
          <p>Pantry, perlengkapan rapat,<br/>dan kebutuhan kegiatan tim.</p>
          <Link href="/shop" className="site-footer-explore">Temukan kebutuhan Anda<ArrowUpRight size={17} aria-hidden="true"/></Link>
        </div>
        <div className="site-footer-navigation">
          {groups.map(group => <nav aria-label={`${group.title} di footer`} key={group.title}>
            <h2>{group.title}</h2>
            <ul>{group.links.map(link => <li key={link.href}><Link href={link.href}>{link.label}</Link></li>)}</ul>
          </nav>)}
        </div>
      </div>
      <div className="site-footer-disclosure">
        <p><strong>Demo capstone</strong><span>Studi kasus Unit Toko · Divisi BNI</span></p>
        <p>Bukan situs resmi BNI.<br/>Data, foto, pengiriman, dan pembayaran adalah simulasi.</p>
      </div>
    </div>
  </footer>;
}
