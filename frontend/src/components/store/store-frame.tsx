'use client';

import Link from 'next/link';
import { usePathname, useRouter } from 'next/navigation';
import { useEffect, useRef, useState } from 'react';
import { Briefcase, ChevronDown, ClipboardList, LayoutGrid, Search, ShoppingCart, UserRound } from 'lucide-react';
import { Brand, BrandMark } from '@/components/brand';
import { whatsappLink } from '@/lib/whatsapp';
import { createAccessGateway } from '@/modules/administration/access-client';
import { createIdentityGateway } from '@/modules/identity/identity-client';
import { ProtectedNavigationForm } from '@/modules/identity/ProtectedNavigationForm';
import { isPilotRelease } from '@/lib/release-scope';
import { createCartGateway } from '@/modules/ordering/cart-client';
import { CART_CHANGED_EVENT, rememberedCartContext } from '@/modules/ordering/cart-branch';
import { storeLinks, storeMember, type StoreViewer } from './store-navigation';
import styles from './store-frame.module.css';

/** Pages where the buyer is deciding or paying: the tab bar steps aside for the page's own action bar. */
const focusedPath = (path: string) => /^\/shop\/[^/]+$/.test(path) || ['/cart', '/ordering/drafts', '/ordering/checkout'].includes(path);

/**
 * The one storefront frame around every buyer page, in three states:
 * - guest: browsing plus a clear "Masuk";
 * - buyer: cart and account menu;
 * - staff: the same, plus the door to their back office.
 * The logo always returns to the landing page.
 */
export function StoreFrame({ children }: { children: React.ReactNode }) {
  const pathname = usePathname() ?? '/', router = useRouter();
  const [viewer, setViewer] = useState<StoreViewer>({ kind: 'loading' });
  const [query, setQuery] = useState('');
  const [session, setSession] = useState<{ userId: string } | null>(null);
  useEffect(() => {
    let alive = true;
    void (async () => {
      try {
        const current = await createIdentityGateway().session();
        if (!current.authenticated || !current.account) { if (alive) setViewer({ kind: 'guest' }); return; }
        const scope = await createAccessGateway().current();
        if (!alive) return;
        setSession({ userId: current.account.userId });
        setViewer(storeMember(current.account.displayName ?? 'Akun Anda', scope.allowedActions));
      } catch { if (alive) setViewer({ kind: 'guest' }); }
    })();
    return () => { alive = false; };
  }, []);
  const [cartLines, setCartLines] = useState<number | null>(null);
  const orders = viewer.kind === 'member' && viewer.orders;
  useEffect(() => {
    // The count is a convenience: one read of the branch the buyer already chose (none before a first choice), then whatever the page announces.
    if (!session || !orders || isPilotRelease) return;
    let alive = true;
    void (async () => {
      try {
        const context = rememberedCartContext(session.userId); if (!context) return;
        const cart = await createCartGateway().read(session.userId, context);
        if (alive) setCartLines(cart?.input.selections.length ?? 0);
      } catch { /* leave the icon without a count */ }
    })();
    const changed = (event: Event) => { const lines = (event as CustomEvent<number>).detail; if (Number.isSafeInteger(lines)) setCartLines(lines); };
    window.addEventListener(CART_CHANGED_EVENT, changed);
    return () => { alive = false; window.removeEventListener(CART_CHANGED_EVENT, changed); };
  }, [session, orders]);
  const cartName = isPilotRelease ? 'Permintaan' : cartLines ? 'Keranjang, ' + cartLines + ' barang' : 'Keranjang';
  const badge = !isPilotRelease && cartLines ? <span className={styles.badge} aria-hidden="true">{cartLines > 99 ? '99+' : cartLines}</span> : null;
  const links = storeLinks(viewer, isPilotRelease, pathname);
  const help = whatsappLink(process.env.NEXT_PUBLIC_UNIT_TOKO_HELP_WHATSAPP, 'Halo, saya perlu bantuan memesan di Unit Toko.');
  const member = viewer.kind === 'member' ? viewer : null;
  // The search box shows the catalog's current query, as marketplace search does, after links, back, forward and reload.
  useEffect(() => {
    const read = () => { if (window.location.pathname === links.catalog) setQuery((new URLSearchParams(window.location.search).get('q') ?? '').slice(0, 128)); };
    void Promise.resolve().then(read); window.addEventListener('popstate', read);
    return () => window.removeEventListener('popstate', read);
  }, [pathname, links.catalog]);
  function submit(event: React.FormEvent) {
    event.preventDefault();
    const term = query.trim().slice(0, 128), target = links.catalog + (term ? '?q=' + encodeURIComponent(term) : '');
    // The catalog keeps its own query in the address; on the catalog itself, update the address and let it re-read.
    if (pathname === links.catalog) { window.history.pushState({}, '', target); window.dispatchEvent(new PopStateEvent('popstate')); }
    else router.push(target);
  }
  const searchForm = (className: string) => <form role="search" className={className} onSubmit={submit}>
    <label className={styles.visuallyHidden} htmlFor={className + '-q'}>Cari barang</label>
    <input id={className + '-q'} type="search" value={query} maxLength={128} placeholder="Cari barang, merek, atau kode" onChange={event => setQuery(event.target.value)} />
    <button type="submit" aria-label="Cari"><Search size={18} aria-hidden="true" /></button>
  </form>;
  return <div className={styles.frame} data-store-frame="">
    <div className={styles.utility}><div className={styles.inner}>
      <nav aria-label="Informasi"><Link href="/#layanan">Cara pemesanan cabang</Link>{help && <a href={help} target="_blank" rel="noopener noreferrer">Bantuan · WhatsApp</a>}</nav>
      {links.staffDoor && viewer.kind !== 'loading' && <Link className={styles.staffDoor} href={links.staffDoor}>Ruang kerja petugas</Link>}
    </div></div>
    <header className={styles.header}><div className={`${styles.inner} ${styles.mainBar}`}>
      <Brand className={styles.brand} context="Toko kebutuhan cabang" />
      <Link className={styles.mark} href="/" aria-label="Unit Toko — beranda"><BrandMark /></Link>
      <Link className={styles.categories} href={links.catalog}><LayoutGrid size={18} aria-hidden="true" />Kategori</Link>
      {searchForm(styles.search)}
      <div className={styles.actions}>
        {member?.staff && <Link className={styles.workspace} href="/workspace"><Briefcase size={17} aria-hidden="true" />Ruang kerja</Link>}
        {viewer.kind !== 'loading' && <Link className={styles.iconLink} href={links.cart} aria-label={cartName}>
          <span className={styles.cartIcon}><ShoppingCart size={21} aria-hidden="true" />{badge}</span><span className={styles.iconLabel}>{isPilotRelease ? 'Permintaan' : 'Keranjang'}</span></Link>}
        {viewer.kind === 'guest' && <Link className={styles.signIn} href={links.signIn}>Masuk</Link>}
        {member && session && <AccountMenu name={member.name} approver={member.approver} staff={member.staff} orders={links.orders} actorId={session.userId} />}
        {viewer.kind === 'loading' && <span className={styles.pending} aria-hidden="true" />}
      </div>
    </div>
    {searchForm(styles.mobileSearch)}
    </header>
    <div id="konten-utama" tabIndex={-1} className={styles.content}>{children}</div>
    <StoreFooter staffDoor={links.staffDoor ?? '/workspace'} member={member !== null} />
    {!focusedPath(pathname) && <nav className={styles.tabs} aria-label="Navigasi toko">
      <Link href={links.catalog} aria-current={pathname.startsWith('/shop') ? 'page' : undefined}><LayoutGrid size={20} aria-hidden="true" />Katalog</Link>
      <Link href={links.cart} aria-label={cartName} aria-current={pathname === '/cart' ? 'page' : undefined}><span className={styles.cartIcon}><ShoppingCart size={20} aria-hidden="true" />{badge}</span>{isPilotRelease ? 'Permintaan' : 'Keranjang'}</Link>
      <Link href={links.orders} aria-current={pathname.startsWith('/ordering/orders') ? 'page' : undefined}><ClipboardList size={20} aria-hidden="true" />Pesanan</Link>
      <Link href={links.account} aria-current={pathname === '/identity/account' ? 'page' : undefined}><UserRound size={20} aria-hidden="true" />{member ? 'Akun' : 'Masuk'}</Link>
    </nav>}
  </div>;
}

function AccountMenu({ name, approver, staff, orders, actorId }: { name: string; approver: boolean; staff: boolean; orders: string; actorId: string }) {
  const [open, setOpen] = useState(false), root = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const away = (event: MouseEvent) => { if (!root.current?.contains(event.target as Node)) setOpen(false); };
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', away); document.addEventListener('keydown', escape);
    return () => { document.removeEventListener('mousedown', away); document.removeEventListener('keydown', escape); };
  }, [open]);
  return <div className={styles.account} ref={root}>
    <button type="button" className={styles.accountButton} aria-expanded={open} aria-controls="menu-akun" onClick={() => setOpen(value => !value)}>
      <UserRound size={19} aria-hidden="true" /><span>{name}</span><ChevronDown size={16} aria-hidden="true" /></button>
    {open && <div id="menu-akun" className={styles.menu}>
      <p className={styles.menuName}>{name}</p>
      <Link href={orders} onClick={() => setOpen(false)}>Pesanan saya</Link>
      <Link href="/ordering/requests" onClick={() => setOpen(false)}>Permintaan saya</Link>
      {approver && <Link href="/ordering/reviews" onClick={() => setOpen(false)}>Persetujuan belanja</Link>}
      <Link href="/identity/account" onClick={() => setOpen(false)}>Akun saya</Link>
      {staff && <Link href="/workspace" onClick={() => setOpen(false)}>Ruang kerja petugas</Link>}
      <div className={styles.menuSignOut}><ProtectedNavigationForm action="/auth/logout" fields={{ expectedActorId: actorId }} label="Keluar" secondary /></div>
    </div>}
  </div>;
}

function StoreFooter({ staffDoor, member }: { staffDoor: string; member: boolean }) {
  return <footer className={styles.footer}><div className={`${styles.inner} ${styles.footerGrid}`}>
    <div><Brand inverse context="Toko kebutuhan cabang" /><p>Kebutuhan kantor, pantry dan kegiatan untuk cabang BNI, dipesan dan dilacak dari satu akun.</p></div>
    <nav aria-label="Belanja"><h2>Belanja</h2><Link href="/shop">Katalog</Link><Link href="/#layanan">Cara pemesanan cabang</Link></nav>
    <nav aria-label="Akun"><h2>Akun</h2>{member ? <><Link href="/ordering/orders">Pesanan saya</Link><Link href="/identity/account">Akun saya</Link></> :
      <><Link href="/identity/login">Masuk</Link><Link href="/#akses">Belum punya akses?</Link></>}</nav>
    <nav aria-label="Koperasi"><h2>Unit Toko</h2><Link href="/">Beranda</Link><Link href={staffDoor}>Ruang kerja petugas</Link></nav>
  </div></footer>;
}
