'use client';

import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useCallback, useEffect, useRef, useState } from 'react';
import { ChevronRight, LogIn, PackageOpen, SearchX } from 'lucide-react';
import { createAccessGateway } from '../administration/access-client';
import { createIdentityGateway } from '../identity/identity-client';
import { formatNumber } from '@/lib/format';
import { whatsappLink } from '@/lib/whatsapp';
import { createPublicCatalogGateway, publicCatalogPageSize, publicCatalogUrl, readPublicCatalogQuery,
  type PublicCatalogCategory, type PublicCatalogItem, type PublicCatalogQuery } from './public-catalog-client';
import { GuestProductCard, GuestSampleBanner, GuestSampleEmpty, GuestUnavailable, MemberCatalogNote, guestSignInHref, memberRedirect, packLine, signedInEquivalent } from './PublicCatalogParts';
import styles from './public-catalog.module.css';
import catalog from './storefront-catalog.module.css';
import { Button, buttonClassName } from '@/components/workspace-kit/button';
import { Field } from '@/components/workspace-kit/field';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { StateView } from '@/components/workspace-kit/state-view';
import { Toolbar } from '@/components/workspace-kit/toolbar';

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
  // The signed-in equivalent keeps the search, category and order; it is known once the address has been read.
  const target = signedInEquivalent(null, undefined, query ?? undefined);
  useMemberRedirect(visitor, query ? target : null);
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
  return <main className={catalog.page}>
    <PageHeader breadcrumb={<span className={catalog.crumbs}><Link href="/">Beranda</Link><ChevronRight size={14} aria-hidden="true" />
      {current.search || categoryName ? <><Link href="/katalog" onClick={event => { event.preventDefault(); navigate({}); }}>Katalog</Link><ChevronRight size={14} aria-hidden="true" /><span>{title}</span></> : <span>Katalog</span>}</span>}
      title={title} description={<span aria-live="polite">{state === 'loading' ? 'Memuat produk…' : `${formatNumber(total)} produk`}</span>}
      secondaryAction={visitor === 'no-catalog' ? <MemberCatalogNote /> : <GuestSampleBanner target={target} />} />
    <Toolbar label="Saring katalog"
      filters={<>{current.search && <p className={catalog.searchTerm}>Pencarian: <strong>{current.search}</strong><Button onClick={() => navigate({ ...current, search: undefined, offset: undefined })}>Hapus pencarian</Button></p>}
        <div className={catalog.chips} role="group" aria-label="Kategori">
          <button type="button" aria-pressed={!current.category} onClick={() => navigate({ ...current, category: undefined, offset: undefined })}>Semua<span>{everything}</span></button>
          {categories.map(item => <button key={item.code} type="button" aria-pressed={current.category === item.code}
            onClick={() => navigate({ ...current, category: item.code, offset: undefined })}>{item.name}<span>{item.count}</span></button>)}
        </div></>}
      sort={<div className={catalog.sort}><Field id="katalog-urutan" label="Urutkan"><select value={current.sort ?? 'added'} onChange={event => navigate({ ...current, sort: event.target.value === 'name' ? 'name' : undefined, offset: undefined })}>
        <option value="added">Terbaru</option><option value="name">Nama A–Z</option></select></Field></div>} />
    {state === 'error' ? <StateView kind="error" title="Katalog belum dapat dimuat" message="Periksa koneksi, lalu coba lagi."
      action={<Button onClick={() => query && void load(query, false)}>Coba lagi</Button>} />
      : state !== 'loading' && items.length === 0 ? current.search || current.category
        ? <StateView kind="empty" align="center" icon={SearchX} title="Barang tidak ditemukan" message="Coba kata lain, periksa ejaan, atau lihat semua kategori."
          action={<Button variant="primary" onClick={() => navigate({})}>Lihat semua produk</Button>} />
        : <GuestSampleEmpty target={target} />
      : <ul className={catalog.grid} aria-busy={state === 'loading'}>
        {state === 'loading' ? Array.from({ length: 10 }, (_, index) => <li key={index} className={catalog.skeleton} aria-hidden="true"><span /><span /><span /></li>)
          : items.map(item => <GuestProductCard key={item.publicId} item={item} />)}
      </ul>}
    {state !== 'loading' && items.length > 0 && items.length < total && <div className={catalog.more}>
      <p>{items.length} dari {total} produk</p>
      <Button disabled={state === 'more'} onClick={() => query && void load({ ...query, offset: items.length }, true)}>
        {state === 'more' ? 'Memuat…' : `Muat lebih banyak (${Math.min(publicCatalogPageSize, total - items.length)})`}</Button></div>}
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
  if (state === 'loading') return <main className={catalog.page}><StateView kind="loading" title="Memuat produk…" /></main>;
  if (state === 'error') return <main className={catalog.page}><StateView kind="error" title="Produk belum dapat dimuat" message="Periksa koneksi, lalu coba lagi."
    action={<Button onClick={() => { setState('loading'); setAttempt(value => value + 1); }}>Coba lagi</Button>} /></main>;
  if (state === 'absent' || !item) return <main className={catalog.page}><GuestUnavailable publicId={publicId} /></main>;
  const signIn = guestSignInHref(signedInEquivalent(item, publicId));
  return <main className={catalog.page}>
    <PageHeader breadcrumb={<span className={catalog.crumbs}><Link href="/">Beranda</Link><ChevronRight size={14} aria-hidden="true" />
      <Link href="/katalog">Katalog</Link><ChevronRight size={14} aria-hidden="true" />
      <Link href={publicCatalogUrl({ category: item.categoryCode })}>{item.categoryName}</Link><ChevronRight size={14} aria-hidden="true" /><span>{item.name}</span></span>}
      title={item.name} description={`Contoh barang · ${formatNumber(item.packages.length)} kemasan`}
      secondaryAction={visitor === 'no-catalog' ? <MemberCatalogNote /> : undefined} />
    <article className={catalog.product}>
      <div className={catalog.productMedia}><div className={catalog.productTile}><PackageOpen size={72} strokeWidth={1} aria-hidden="true" /></div></div>
      <div className={catalog.productInfo}>
        <Link className={catalog.category} href={publicCatalogUrl({ category: item.categoryCode })}>{item.categoryName}</Link>
        <section className={catalog.buyPanel} aria-label="Harga dan pemesanan">
          <p>Harga mengikuti kontrak cabang Anda dan tampil setelah masuk.</p>
          <Link className={buttonClassName({ variant: 'primary' })} href={signIn}><LogIn aria-hidden="true" />Masuk untuk lihat harga dan memesan</Link>
          <p className={catalog.help}>Belum punya akses? {help ? <a href={help} target="_blank" rel="noopener noreferrer">Hubungi koperasi lewat WhatsApp</a> : 'Hubungi petugas koperasi di unit Anda.'}</p>
        </section>
        <h2>Kemasan</h2>
        <dl className={styles.packages}>
          {item.packages.map(entry => <div key={entry.publicId}><dt>{entry.code}</dt><dd>{packLine(entry)}
            {'. '}Minimal {entry.minimumOrderPackages} kemasan{entry.orderIncrementPackages > 1 ? `, kelipatan ${entry.orderIncrementPackages}` : ''}
            {entry.attributes.length > 0 && <>{'. '}{entry.attributes.map(pair => `${pair.key}: ${pair.value}`).join(', ')}</>}</dd></div>)}
        </dl>
      </div>
      {item.description && <section className={catalog.productWide} aria-labelledby="deskripsi-produk"><h2 id="deskripsi-produk">Deskripsi</h2><p>{item.description}</p></section>}
    </article>
  </main>;
}
