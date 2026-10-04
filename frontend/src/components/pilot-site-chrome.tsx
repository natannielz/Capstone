import Link from 'next/link';
import { ArrowUpRight } from 'lucide-react';
import { Brand } from './brand';
import styles from './pilot-site-chrome.module.css';

export function PilotSiteHeader({ active }: { active?: 'catalog' | 'requests' | 'account' }) {
  return <>
    <div className={styles.topline}><div><span>Kebutuhan kerja & kegiatan tim</span><span>Layanan untuk akun organisasi</span></div></div>
    <header className={styles.header}>
      <Brand context="Kebutuhan kerja & harian" />
      <nav aria-label="Navigasi utama">
        <Link href="/shop" aria-current={active === 'catalog' ? 'page' : undefined}>Katalog toko</Link>
        <Link href="/ordering/requests" aria-current={active === 'requests' ? 'page' : undefined}>Permintaan saya</Link>
        <Link href="/#layanan">Cara mengajukan</Link>
      </nav>
      <Link href="/identity/account" className={styles.account} aria-current={active === 'account' ? 'page' : undefined}><span>Akun saya</span><span aria-hidden="true"><ArrowUpRight size={19} /></span></Link>
    </header>
  </>;
}

const footerGroups = [
  { title: 'Kebutuhan Anda', links: [{ label: 'Katalog kemasan', href: '/shop' }, { label: 'Inspirasi kebutuhan', href: '/#kebutuhan' }, { label: 'Cara mengajukan', href: '/#layanan' }] },
  { title: 'Permintaan & akun', links: [{ label: 'Permintaan saya', href: '/ordering/requests' }, { label: 'Akun saya', href: '/identity/account' }, { label: 'Masuk ke akun', href: '/identity/login' }] },
  { title: 'Unit Toko', links: [{ label: 'Beranda', href: '/' }, { label: 'Akses cabang & petugas', href: '/#akses' }, { label: 'Ruang kerja petugas', href: '/identity/account' }] },
];

export function PilotSiteFooter() {
  return <footer className={styles.footer}><div className={styles.footerInner}>
    <div className={styles.footerMain}>
      <div className={styles.identity}><Brand inverse context="Kebutuhan kerja & harian" className={styles.footerBrand} /><p>Dari kebutuhan harian<br />hingga kegiatan bersama.</p><Link className={styles.explore} href="/shop">Temukan kebutuhan Anda<ArrowUpRight size={17} aria-hidden="true" /></Link></div>
      <div className={styles.footerNavigation}>{footerGroups.map(group => <nav key={group.title} aria-label={group.title + ' di footer'}><h2>{group.title}</h2><ul>{group.links.map(link => <li key={link.href}><Link href={link.href}>{link.label}</Link></li>)}</ul></nav>)}</div>
    </div>
    <div className={styles.notes}><p><strong>Unit Toko · Layanan organisasi</strong>Untuk cabang BNI dan akun usaha yang telah disetujui.</p><p>Permintaan ditinjau oleh petugas toko.<br />Harga, ketersediaan, dan pemenuhan dikonfirmasi saat tindak lanjut.</p></div>
  </div></footer>;
}
