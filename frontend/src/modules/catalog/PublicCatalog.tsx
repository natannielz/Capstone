'use client';

import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useCallback, useEffect, useRef, useState } from 'react';
import { ChevronRight, LogIn, PackageOpen, SearchX } from 'lucide-react';
import { createAccessGateway } from '../administration/access-client';
import { createIdentityGateway } from '../identity/identity-client';
import { storeSignInHref } from '@/components/store/store-navigation';
import { formatNumber } from '@/lib/format';
import { whatsappLink } from '@/lib/whatsapp';
import { createPublicCatalogGateway, publicCatalogPageSize, publicCatalogUrl, readPublicCatalogQuery,
  type PublicCatalogCategory, type PublicCatalogItem, type PublicCatalogQuery } from './public-catalog-client';
import { GuestProductCard, GuestSampleBanner, GuestSampleEmpty, GuestUnavailable, MemberCatalogNote, memberRedirect, packLine, signedInEquivalent } from './PublicCatalogParts';
import styles from './public-catalog.module.css';

type Visitor = 'guest' | 'member' | 'no-catalog';
function useVisitor(): Visitor {
  const [visitor, setVisitor] = useState<Visitor>('guest');
  useEffect(() => {
    let alive = true;
    void createIdentityGateway().session().then(async session => {
      if (!session.authenticated || !session.account) return;
      const actions = await createAccessGateway().current().then(current => current.allowedActions as readonly string[]).catch(() => [] as readonly string[]);
      if (alive) setVisitor(memberRedirect(session, actions, '/shop') ? 'member' : 'no-catalog');
    }).catch(() => undefined);
    return () => { alive = false; };
  }, []);
  return visitor;
}
/** A visitor who may read the organisation catalog belongs on it, with prices; the samples are for everyone else. */
function useMemberRedirect(visitor: Visitor, target: string | null) {
  const router = useRouter();
  useEffect(() => { if (visitor === 'member' && target) router.replace(target); }, [router, visitor, target]);
}

export function PublicCatalog() {
  const [gateway] = useState(() => createPublicCatalogGateway());
  const [query, setQuery] = useState<PublicCatalogQuery | null>(null);
  const [items, setItems] = useState<PublicCatalogItem[]>([]), [categories, setCategories] = useState<PublicCatalogCategory[]>([]);
  const [total, setTotal] = useState(0), [state, setState] = useState<'loading' | 'ready' | 'more' | 'error'>('loading');
  const request = useRef<AbortController | null>(null);
  const visitor = useVisitor();
  useMemberRedirect(visitor, signedInEquivalent(null));
  useEffect(() => {
    const read = () => setQuery(readPublicCatalogQuery(window.location.search));
    void Promise.resolve().then(read); window.addEventListener('popstate', read);
    return () => window.removeEventListener('popstate', read);
  }, []);
  const load = useCallback(async (next: PublicCatalogQuery, append: boolean) => {
    request.current?.abort(); const abort = new AbortController(); request.current = abort;
    setState(append ? 'more' : 'loading');
    try {
      const page = await gateway.query(next, abort.signal);
      if (abort.signal.aborted) return;
      setItems(current => append ? [...current, ...page.items] : page.items); setTotal(page.total); setCategories(page.categories); setState('ready');
    } catch { if (!abort.signal.aborted) setState('error'); }
  }, [gateway]);
  useEffect(() => {
    if (query) void Promise.resolve().then(() => load(query, false));
    return () => request.current?.abort();
  }, [query, load]);
  function navigate(next: PublicCatalogQuery) {
    window.history.pushState({}, '', publicCatalogUrl(next)); setQuery(next); window.scrollTo({ top: 0 });
  }
  const current = query ?? {};
  const categoryName = current.category ? categories.find(item => item.code === current.category)?.name ?? current.category : null;
  const everything = categories.reduce((sum, item) => sum + item.count, 0);
  const title = current.search ? `Hasil untuk “${current.search}”` : categoryName ?? 'Katalog';
  return <main className={styles.page}>
    <nav className={styles.breadcrumb} aria-label="Jejak halaman"><Link href="/">Beranda</Link><ChevronRight size={14} aria-hidden="true" />
      {current.search || categoryName ? <><Link href="/katalog" onClick={event => { event.preventDefault(); navigate({}); }}>Katalog</Link><ChevronRight size={14} aria-hidden="true" /><span>{title}</span></> : <span>Katalog</span>}</nav>
    <header className={styles.heading}>
      <div><h1>{title}</h1><p aria-live="polite">{state === 'loading' ? 'Memuat produk…' : `${formatNumber(total)} produk`}</p></div>
      {visitor === 'no-catalog' ? <MemberCatalogNote /> : <GuestSampleBanner />}
    </header>
    <div className={styles.toolbar}>
      <div className={styles.chips} role="group" aria-label="Kategori">
        <button type="button" aria-pressed={!current.category} onClick={() => navigate({ ...current, category: undefined, offset: undefined })}>Semua<span>{everything}</span></button>
        {categories.map(item => <button key={item.code} type="button" aria-pressed={current.category === item.code}
          onClick={() => navigate({ ...current, category: item.code, offset: undefined })}>{item.name}<span>{item.count}</span></button>)}
      </div>
      <label className={styles.sort}>Urutkan<select value={current.sort ?? 'added'} onChange={event => navigate({ ...current, sort: event.target.value === 'name' ? 'name' : undefined, offset: undefined })}>
        <option value="added">Terbaru</option><option value="name">Nama A–Z</option></select></label>
    </div>
    {current.search && <p className={styles.searchTerm}>Pencarian: <strong>{current.search}</strong><button type="button" onClick={() => navigate({ ...current, search: undefined, offset: undefined })}>Hapus pencarian</button></p>}
    {state === 'error' ? <section className={styles.state}><h2>Katalog belum dapat dimuat</h2><p>Periksa koneksi, lalu coba lagi.</p>
      <button type="button" className={styles.primary} onClick={() => query && void load(query, false)}>Coba lagi</button></section>
      : state !== 'loading' && items.length === 0 ? current.search || current.category
        ? <section className={styles.state}><SearchX size={34} aria-hidden="true" /><h2>Barang tidak ditemukan</h2><p>Coba kata lain, periksa ejaan, atau lihat semua kategori.</p>
          <button type="button" className={styles.primary} onClick={() => navigate({})}>Lihat semua produk</button></section>
        : <GuestSampleEmpty />
      : <ul className={styles.grid} aria-busy={state === 'loading'}>
        {state === 'loading' ? Array.from({ length: 10 }, (_, index) => <li key={index} className={styles.skeleton} aria-hidden="true"><span /><span /><span /></li>)
          : items.map(item => <GuestProductCard key={item.publicId} item={item} />)}
      </ul>}
    {state !== 'loading' && items.length > 0 && items.length < total && <div className={styles.more}>
      <p>{items.length} dari {total} produk</p>
      <button type="button" className={styles.secondary} disabled={state === 'more'} onClick={() => query && void load({ ...query, offset: items.length }, true)}>
        {state === 'more' ? 'Memuat…' : `Muat lebih banyak (${Math.min(publicCatalogPageSize, total - items.length)})`}</button></div>}
  </main>;
}

/** A guest product page. The address may hold the product's id or one of its packages' ids, so shared package links keep working. */
export function PublicProduct({ publicId }: { publicId: string }) {
  const [gateway] = useState(() => createPublicCatalogGateway());
  const [item, setItem] = useState<PublicCatalogItem | null>(null), [state, setState] = useState<'loading' | 'ready' | 'absent' | 'error'>('loading');
  const [attempt, setAttempt] = useState(0);
  const visitor = useVisitor();
  useMemberRedirect(visitor, state === 'ready' || state === 'absent' ? signedInEquivalent(item, publicId) : null);
  useEffect(() => {
    const abort = new AbortController();
    void gateway.item(publicId, abort.signal).then(value => { if (abort.signal.aborted) return; setItem(value); setState(value ? 'ready' : 'absent'); })
      .catch(() => { if (!abort.signal.aborted) setState('error'); });
    return () => abort.abort();
  }, [gateway, publicId, attempt]);
  const help = whatsappLink(process.env.NEXT_PUBLIC_UNIT_TOKO_HELP_WHATSAPP, 'Halo, cabang kami ingin mendapat akses pemesanan Unit Toko.');
  if (state === 'loading') return <main className={styles.page}><p role="status">Memuat produk…</p></main>;
  if (state === 'error') return <main className={styles.page}><section className={styles.state}><h2>Produk belum dapat dimuat</h2><p>Periksa koneksi, lalu coba lagi.</p>
    <button type="button" className={styles.primary} onClick={() => { setState('loading'); setAttempt(value => value + 1); }}>Coba lagi</button></section></main>;
  if (state === 'absent' || !item) return <main className={styles.page}><GuestUnavailable /></main>;
  const signIn = storeSignInHref(signedInEquivalent(item, publicId));
  return <main className={styles.page}>
    <nav className={styles.breadcrumb} aria-label="Jejak halaman"><Link href="/">Beranda</Link><ChevronRight size={14} aria-hidden="true" />
      <Link href="/katalog">Katalog</Link><ChevronRight size={14} aria-hidden="true" />
      <Link href={publicCatalogUrl({ category: item.categoryCode })}>{item.categoryName}</Link><ChevronRight size={14} aria-hidden="true" /><span>{item.name}</span></nav>
    {visitor === 'no-catalog' && <MemberCatalogNote />}
    <article className={styles.product}>
      <div className={styles.productMedia}><PackageOpen size={72} strokeWidth={1} aria-hidden="true" /></div>
      <div className={styles.productInfo}>
        <Link className={styles.category} href={publicCatalogUrl({ category: item.categoryCode })}>{item.categoryName}</Link>
        <h1>{item.name}</h1>
        <p className={styles.code}>Contoh barang · {formatNumber(item.packages.length)} kemasan</p>
        <section className={styles.priceBox} aria-label="Harga dan pemesanan">
          <p>Harga mengikuti kontrak cabang Anda dan tampil setelah masuk.</p>
          <Link className={styles.priceButton} href={signIn}><LogIn size={18} aria-hidden="true" />Masuk untuk lihat harga dan memesan</Link>
          <p className={styles.access}>Belum punya akses? {help ? <a href={help} target="_blank" rel="noopener noreferrer">Hubungi koperasi lewat WhatsApp</a> : 'Hubungi petugas koperasi di unit Anda.'}</p>
        </section>
        <h2 className={styles.code}>Kemasan</h2>
        <dl className={styles.facts}>
          {item.packages.map(entry => <div key={entry.publicId}><dt>{entry.code}</dt><dd>{packLine(entry)}
            {'. '}Minimal {entry.minimumOrderPackages} kemasan{entry.orderIncrementPackages > 1 ? `, kelipatan ${entry.orderIncrementPackages}` : ''}
            {entry.attributes.length > 0 && <>{'. '}{entry.attributes.map(pair => `${pair.key}: ${pair.value}`).join(', ')}</>}</dd></div>)}
        </dl>
      </div>
    </article>
    {item.description && <section className={styles.description} aria-labelledby="deskripsi-produk"><h2 id="deskripsi-produk">Deskripsi</h2><p>{item.description}</p></section>}
  </main>;
}
