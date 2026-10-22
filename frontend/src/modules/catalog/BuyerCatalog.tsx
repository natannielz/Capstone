'use client';

import { useRouter } from 'next/navigation';
import Link from 'next/link';
import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { ArrowRight, ArrowUpRight, ChevronRight, ClipboardList, LayoutGrid, List, PackageOpen, ShieldCheck } from 'lucide-react';
import { Art } from '../../components/art';
import { Button, buttonClassName } from '@/components/workspace-kit/button';
import { Facts } from '@/components/workspace-kit/facts';
import { Field } from '@/components/workspace-kit/field';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { StateView } from '@/components/workspace-kit/state-view';
import { Toolbar } from '@/components/workspace-kit/toolbar';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure } from '../administration/workflow';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { CatalogReadGate } from './catalog-editor-state';
import { buyerCategoryLabel, buyerCurrentCode, createBuyerCatalogGateway, type BuyerCategoryCount, type BuyerVariant } from './catalog-buyer-client';
import { CatalogBuyerPhoto } from './CatalogBuyerPhoto';
import { catalogHistoryUrl, guestCatalogHref, memberCatalogPath, readCatalogHistoryQuery, type CatalogHistoryQuery } from './catalog-history';
import catalog from './storefront-catalog.module.css';
import styles from './buyer-catalog.module.css';
import { BuyBox } from './BuyBox';
import { formatDisplayMoney, formatNumber } from "@/lib/format";

const price = (value: BuyerVariant['currentPrice']) => {
  if (value === null) return 'Harga belum tersedia';
  return formatDisplayMoney(value.currency, value.unitPrice);
};
function message(error: unknown) {
  if (error instanceof AccessFailure || error instanceof IdentityFailure) {
    if (error.code === 'actor_changed') return 'Akun di peramban berubah. Muat ulang halaman sebelum melanjutkan.';
    if (error.status === 401) return 'Masuk dengan akun organisasi Anda untuk membuka katalog.';
    if (error.status === 403) return 'Katalog ini belum tersedia untuk akun Anda. Hubungi pengelola akun.';
    if (error.status === 409) return 'Data berubah saat dimuat. Muat kembali untuk melihat informasi terbaru.';
    if (error.status >= 500) return 'Layanan katalog belum tersedia. Coba lagi nanti atau hubungi pengelola organisasi Anda.';
  }
  return 'Katalog belum dapat dimuat. Periksa koneksi, lalu coba lagi.';
}
type Account = { actorId: string; name: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
function BuyerCodeLabel({ item }: { item: BuyerVariant }) {
  return <><p className={catalog.meta}>{item.buyerIdentity ? 'Kode utama saat ini: ' : 'Referensi SKU: '}{buyerCurrentCode(item)}</p>
    {item.buyerIdentity && item.code !== buyerCurrentCode(item) && <p className={catalog.help}>Referensi SKU tetap pada catatan transaksi: {item.code}. Kode ini hanya dapat dipakai mencari jika aliasnya masih aktif.</p>}</>;
}
const packageKind: Record<string, string> = { sealed: 'tersegel', loose: 'satuan lepas', other: 'kemasan lain' };
/** The product page in the order shoppers know (U-025): the name on the page's left edge, then photo 5 / category, price and
 *  buy box 7; specifications and description below. The one bordered box is none: the buy box is an inset panel. */
function ProductPage({ item, account, isCurrent, category, breadcrumb }: { item: BuyerVariant; account: Account; isCurrent: () => boolean; category: string; breadcrumb: ReactNode }) {
  return <>
    <PageHeader breadcrumb={breadcrumb} title={<span id="buyer-package-title">{item.familyName}</span>}
      description={`${item.definition.packageLabel} · isi ${formatNumber(item.definition.baseUnitsPerPackage)} ${item.definition.baseUnitCode}`} />
    <article className={catalog.product} aria-labelledby="buyer-package-title">
      <div className={`${catalog.productMedia} ${styles.productPhoto}`}><CatalogBuyerPhoto actorId={account.actorId} item={item} isCurrent={isCurrent}/></div>
      <div className={catalog.productInfo}>
        <span className={catalog.category}>{category}</span>
        <BuyerCodeLabel item={item} />
        <p className={catalog.productPrice}>{price(item.currentPrice)}<span> per {item.definition.packageLabel}</span></p>
        <BuyBox item={item} actorId={account.actorId} actions={account.actions} isCurrent={isCurrent} />
      </div>
      <section className={catalog.productWide} aria-labelledby="spesifikasi-produk"><h2 id="spesifikasi-produk">Spesifikasi</h2>
        <Facts items={[{ term: 'Kemasan', value: `${item.definition.packageLabel}, ${packageKind[item.definition.packageKind]}` },
          { term: 'Isi per kemasan', value: `${formatNumber(item.definition.baseUnitsPerPackage)} ${item.definition.baseUnitCode}` },
          { term: 'Minimum pembelian', value: `${item.definition.minimumOrderPackages} kemasan` }, { term: 'Kelipatan jumlah', value: `${item.definition.orderIncrementPackages} kemasan` },
          ...item.definition.attributes.map(attribute => ({ term: attribute.key, value: attribute.value, key: 'attribute:' + attribute.key }))]} /></section>
      {item.description && <section className={catalog.productWide} aria-labelledby="deskripsi-produk"><h2 id="deskripsi-produk">Deskripsi</h2><p>{item.description}</p></section>}
    </article>
  </>;
}
function PackageReview({ item, account, isCurrent, requestOnly, category }: { item: BuyerVariant; account: Account; isCurrent: () => boolean; requestOnly: boolean; category: string }) {
  return <div className={styles.packageLayout}><div className={styles.packageFacts}>
    <CatalogBuyerPhoto actorId={account.actorId} item={item} isCurrent={isCurrent}/>
    <span className={catalog.category}>{category}</span><h2 id="buyer-package-title">{item.familyName} — {item.definition.packageLabel}</h2>
    <BuyerCodeLabel item={item} />{item.description && <p className={styles.description}>{item.description}</p>}
    <p className={styles.contents}>{formatNumber(item.definition.baseUnitsPerPackage)} <span>{item.definition.baseUnitCode} per kemasan</span></p>
    {!requestOnly && <p className={catalog.productPrice}>{price(item.currentPrice)}<span> per {item.definition.packageLabel}</span></p>}
    <Facts items={[{ term: 'Jenis kemasan', value: { sealed: 'Kemasan tersegel', loose: 'Satuan lepas', other: 'Kemasan lain' }[item.definition.packageKind] },
      { term: requestOnly ? 'Minimum permintaan' : 'Minimum pembelian', value: `${item.definition.minimumOrderPackages} kemasan` },
      { term: 'Kelipatan jumlah', value: `${item.definition.orderIncrementPackages} kemasan` },
      ...item.definition.attributes.map(attribute => ({ term: attribute.key, value: attribute.value, key: 'attribute:' + attribute.key }))]} />
    <p className={catalog.help}>{requestOnly ? 'Pilih kemasan ini untuk menyusun permintaan kebutuhan cabang. Petugas toko akan meninjau permintaan Anda.' : 'Harga dan ketersediaan akan diperiksa kembali saat pesanan disiapkan.'}</p>
  </div>{requestOnly ? <div className={styles.quoteArea}><h3>Siapkan permintaan cabang</h3>{account.actions.includes('order.request.submit') ? <><p>Pilih cabang dan jumlah kemasan, lalu tinjau permintaan sebelum mengirim.</p><Link className={buttonClassName({ variant: 'primary' })} href={'/ordering/requests?package=' + item.publicId}>Pilih untuk permintaan</Link></> : <p>Akun Anda dapat melihat katalog. Hubungi pengelola jika Anda memerlukan akses untuk mengajukan kebutuhan cabang.</p>}</div> : item.currentPrice && <div className={styles.quoteArea}><BuyBox item={item} actorId={account.actorId} actions={account.actions} isCurrent={isCurrent} /></div>}</div>;
}
export function BuyerCatalog({ publicId, returnPath = '/shop', requestOnly = false }: { publicId?: string; returnPath?: '/shop'; requestOnly?: boolean } = {}) {
  const [identity] = useState(() => createIdentityGateway()), [access] = useState(() => createAccessGateway());
  const [gateway] = useState(() => createBuyerCatalogGateway()), [listGate] = useState(() => new CatalogReadGate()), [detailGate] = useState(() => new CatalogReadGate());
  const [account, setAccount] = useState<Account | null>(null), [items, setItems] = useState<BuyerVariant[]>([]), [total, setTotal] = useState('0');
  const [catalogQuery, setCatalogQuery] = useState<CatalogHistoryQuery | null>(null), [revision, setRevision] = useState(0);
  const query = catalogQuery?.search ?? '', offset = catalogQuery?.offset ?? 0, category = catalogQuery?.category, sort = catalogQuery?.sort;
  const [categories, setCategories] = useState<BuyerCategoryCount[]>([]), [layout, setLayout] = useState<'grid' | 'list'>('grid');
  const [busy, setBusy] = useState(true), [detailBusy, setDetailBusy] = useState<string | null>(null), [selected, setSelected] = useState<BuyerVariant | null>(null);
  const [error, setError] = useState<unknown>(null), [notice, setNotice] = useState(''), [elapsed, setElapsed] = useState(0);
  const dialog = useRef<HTMLDialogElement>(null), alive = useRef(false), opener = useRef<HTMLButtonElement | null>(null);
  const appliedQuery = useRef<CatalogHistoryQuery | null>(null);
  const current = useCallback(() => alive.current && account !== null && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const refresh = useCallback(() => { detailGate.cancel(); setSelected(null); setDetailBusy(null); setRevision(value => value + 1); }, [detailGate]);
  const changeCatalogQuery = useCallback((next: CatalogHistoryQuery) => {
    const applied = appliedQuery.current;
    if (applied?.search === next.search && applied.offset === next.offset && applied.category === next.category && applied.sort === next.sort) return;
    appliedQuery.current = next;
    listGate.cancel(); detailGate.cancel();
    setCatalogQuery(next);
  }, [listGate, detailGate]);
  useEffect(() => {
    alive.current = true; const timer = window.setInterval(() => { if (account) setElapsed(performance.now() - account.observedAt); }, 1000);
    const restored = (event: PageTransitionEvent) => { if (event.persisted) refresh(); };
    window.addEventListener('pageshow', restored);
    return () => { alive.current = false; window.clearInterval(timer); window.removeEventListener('pageshow', restored); };
  }, [account, refresh]);
  useEffect(() => {
    if (publicId !== undefined) return;
    let active = true;
    const restore = () => {
      if (!active || window.location.pathname !== returnPath) return;
      const next = readCatalogHistoryQuery(window.location.search);
      const canonical = catalogHistoryUrl(returnPath, next, window.location.hash);
      if (window.location.pathname + window.location.search + window.location.hash !== canonical)
        window.history.replaceState({}, '', canonical);
      changeCatalogQuery(next);
    };
    void Promise.resolve().then(restore);
    window.addEventListener('popstate', restore);
    return () => { active = false; window.removeEventListener('popstate', restore); };
  }, [publicId, returnPath, changeCatalogQuery]);
  useEffect(() => {
    if (publicId === undefined && catalogQuery === null) return;
    const request = listGate.begin('session', publicId ?? [query, offset, category ?? '', sort ?? ''].join(':')); let active = true;
    void Promise.resolve().then(async () => {
      if (!active) return; setBusy(true); setItems([]); setCategories([]); setAccount(null); setSelected(null); setError(null); setNotice(''); setTotal('0');
      try {
        const session = await identity.session(request.signal), observedAt = performance.now(); if (!request.current()) return;
        if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
        const scope = await access.current(); if (!request.current()) return;
        if (scope.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
        const page = publicId === undefined ? await gateway.query(scope.userId, query, offset, request.signal, { category, sort }) : null;
        const variant = publicId === undefined ? null : await gateway.variant(scope.userId, publicId, request.signal); if (!request.current()) return;
        if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
        setAccount({ actorId: scope.userId, name: session.account.displayName ?? 'Akun Anda', actions: scope.allowedActions, session, observedAt });
        setElapsed(performance.now() - observedAt); setItems(page?.items ?? []); setTotal(page?.total ?? '0'); setCategories(page?.categories ?? []); setSelected(variant);
        if (publicId !== undefined && variant === null) setNotice('Kemasan ini belum tersedia untuk akun Anda. Kembali ke katalog untuk memeriksa pilihan yang tersedia.');
      } catch (failure) { if (request.current()) setError(failure); }
      finally { if (request.current()) setBusy(false); }
    });
    return () => { active = false; listGate.cancel(); detailGate.cancel(); };
  }, [identity, access, gateway, listGate, detailGate, query, offset, category, sort, revision, publicId, catalogQuery]);
  const expired = account !== null && sessionTiming(account.session, elapsed).expired;
  useEffect(() => {
    if (publicId === undefined && selected && !expired) dialog.current?.showModal(); else dialog.current?.close();
  }, [selected, expired, publicId]);
  async function details(item: BuyerVariant, button: HTMLButtonElement) {
    if (!account || expired || busy || detailBusy) return;
    const request = detailGate.begin(account.actorId, item.publicId); opener.current = button; setSelected(null); setDetailBusy(item.skuId); setError(null); setNotice('');
    try {
      const latest = await gateway.variant(account.actorId, item.publicId, request.signal);
      if (!request.current() || !current()) return;
      if (latest === null) { setItems([]); setTotal('0'); setNotice('Kemasan ini tidak lagi tersedia. Muat kembali katalog.'); }
      else setSelected(latest);
    } catch (failure) {
      if (request.current() && alive.current) { setError(failure); setItems([]); setSelected(null); setAccount(null); }
    } finally { if (request.current() && alive.current) setDetailBusy(null); }
  }
  function close() { dialog.current?.close(); setSelected(null); opener.current?.focus(); }
  function navigateCatalog(next: CatalogHistoryQuery, reload = false) {
    if (publicId !== undefined || window.location.pathname !== returnPath || busy || detailBusy !== null || !current()) return;
    const canonical = catalogHistoryUrl(returnPath, next, window.location.hash);
    if (window.location.pathname + window.location.search + window.location.hash !== canonical)
      window.history.pushState({}, '', canonical);
    changeCatalogQuery(next);
    if (reload) refresh();
  }
  function at(change: Partial<CatalogHistoryQuery>): CatalogHistoryQuery {
    const merged = { search: query, offset: 0, category, sort, ...change }, next: CatalogHistoryQuery = { search: merged.search, offset: merged.offset };
    if (merged.category) next.category = merged.category;
    if (merged.sort) next.sort = merged.sort;
    return next;
  }
  const categoryName = (code: string) => buyerCategoryLabel(code, categories);
  const everything = categories.reduce((sum, item) => sum + item.count, 0), filtered = query !== '' || category !== undefined;
  const needsSignIn = error instanceof IdentityFailure && error.status === 401;
  // Someone who has not signed in browses the public catalog (no prices) instead of meeting a sign-in wall, in every release (G-030).
  const router = useRouter();
  useEffect(() => {
    if (!needsSignIn) return;
    router.replace(guestCatalogHref(publicId, window.location.search));
  }, [needsSignIn, publicId, router]);
  const signInPath = memberCatalogPath(publicId);
  const signInLink = <Link className={buttonClassName({ variant: 'primary' })} href={'/identity/login?returnPath=' + encodeURIComponent(signInPath)}>Masuk atau periksa akun <ArrowRight aria-hidden="true" /></Link>;
  const crumbs = <span className={catalog.crumbs}><Link href="/">Beranda</Link><ChevronRight size={14} aria-hidden="true" />
    {publicId === undefined ? <span>Katalog</span> : <><Link href={returnPath}>Katalog</Link><ChevronRight size={14} aria-hidden="true" /><span>{selected?.familyName ?? 'Produk'}</span></>}</span>;
  return <main id="catalog-main" tabIndex={-1} className={catalog.page}>
    {requestOnly ? <>
      <nav className={`${catalog.crumbs} ${styles.pilotCrumbs}`} aria-label="Jejak halaman"><Link href="/">Beranda</Link><ChevronRight size={14} aria-hidden="true" /><span>{publicId === undefined ? 'Katalog' : 'Rincian kemasan'}</span></nav>
      {publicId === undefined ? <section className={styles.editorial} aria-labelledby="catalog-title">
        <div className={styles.editorialCopy}><p className={styles.eyebrow}>Kebutuhan cabang dan organisasi</p><h1 id="catalog-title">Pilihan tepat.<br /><em>Hari lebih siap.</em></h1><p>Temukan kebutuhan tim, periksa isi kemasannya, lalu ajukan dalam satu permintaan yang tercatat.</p><a className={styles.editorialLink} href="#catalog-list">Jelajahi katalog <ArrowRight size={18} aria-hidden="true" /></a></div>
        <figure className={styles.editorialPhoto}><Art src="/images/editorial/hero-still-life-v3.png" alt="Ilustrasi kebutuhan kerja: tas, tumbler, kopi, dan camilan" loading="eager" fetchPriority="high" sizes="(max-width: 720px) 100vw, 50vw" /><figcaption>Ilustrasi kebutuhan · pilihan tersedia mengikuti katalog akun Anda</figcaption></figure>
      </section> : <PageHeader title="Rincian kemasan" description="Periksa isi dan satuan sebelum mengajukan kebutuhan cabang." />}
      <div className={styles.assurances}><p><PackageOpen size={20} aria-hidden="true" /><span>Isi & satuan kemasan jelas</span></p><p><ShieldCheck size={20} aria-hidden="true" /><span>Akses sesuai akun organisasi</span></p><p><ClipboardList size={20} aria-hidden="true" /><span>Permintaan dapat ditelusuri</span></p></div>
      <div className={styles.sectionHeading} id="catalog-list"><div><p className={styles.eyebrow}>Katalog Unit Toko</p><h2>{publicId === undefined ? 'Katalog kemasan' : 'Informasi kemasan'}</h2></div><Link href="/ordering/requests">Permintaan saya <ArrowUpRight size={18} aria-hidden="true" /></Link></div>
      <p className={styles.availability}>Harga, stok, dan pemenuhan dikonfirmasi petugas. Permintaan yang diterima belum menjadi pemesanan dengan harga mengikat, reservasi stok, pembayaran, atau invoice.</p>
    </> : publicId === undefined && <PageHeader breadcrumb={crumbs} title={query ? `Hasil untuk “${query}”` : category ? categoryName(category) : 'Katalog'} />}
    {publicId !== undefined && (requestOnly || error !== null) && <div className={styles.detailToolbar}><Link className={buttonClassName({ variant: 'tertiary' })} href={returnPath}>Kembali ke katalog</Link><Button disabled={busy} onClick={refresh}>Muat kembali kemasan</Button></div>}
    {error !== null && (needsSignIn
      ? <StateView kind="denied" title="Katalog khusus untuk akun Anda." message="Masuk dengan akun yang telah disetujui untuk melihat pilihan kemasan dan mengajukan kebutuhan cabang Anda." action={signInLink} />
      : <StateView kind="error" title="Katalog belum dapat ditampilkan." message={message(error)} detail="Daftar barang akan tampil setelah layanan tersedia dan akses akun Anda terverifikasi." action={signInLink} />)}
    {expired && <StateView kind="error" title="Sesi Anda perlu diperbarui. Masuk kembali untuk membuka katalog." />}
    {(expired || !account && !busy && error === null) && <div className={styles.signInRow}>{signInLink}</div>}
    {notice && <p role="status" className={styles.notice}>{notice}</p>}
    {publicId !== undefined ? <section className={styles.packagePage} aria-busy={busy} aria-label="Rincian kemasan">{busy ? <p role="status" className={catalog.help}>Memuat kemasan untuk akun Anda…</p> : account && !expired && selected && (requestOnly ? <PackageReview item={selected} account={account} isCurrent={current} requestOnly={requestOnly} category={categoryName(selected.categoryCode)} />
      : <ProductPage item={selected} account={account} isCurrent={current} category={categoryName(selected.categoryCode)} breadcrumb={crumbs} />)}</section>
      : <section aria-busy={busy} aria-label="Kemasan yang tersedia">
        <Toolbar label="Pencarian katalog"
          filters={<>{query && <p className={catalog.searchTerm}>Pencarian: <strong>{query}</strong><Button disabled={busy || detailBusy !== null} onClick={() => navigateCatalog(at({ search: '' }), true)}>Hapus pencarian</Button></p>}
            {!busy && account && !expired && categories.length > 0 && <div className={catalog.chips} role="group" aria-label="Kategori">
              <button type="button" aria-pressed={category === undefined} disabled={detailBusy !== null} onClick={() => navigateCatalog(at({ category: undefined }))}>Semua<span>{formatNumber(everything)}</span></button>
              {categories.map(item => <button key={item.code} type="button" aria-pressed={category === item.code} disabled={detailBusy !== null}
                onClick={() => navigateCatalog(at({ category: item.code }))}>{item.name}<span>{formatNumber(item.count)}</span></button>)}</div>}</>}
          sort={<div className={catalog.sort}><Field id="katalog-urutan" label="Urutkan"><select value={sort ?? 'added'} disabled={busy || detailBusy !== null || !account}
            onChange={event => navigateCatalog(at({ sort: event.target.value === 'name' ? 'name' : undefined }))}><option value="added">Terbaru ditambahkan</option><option value="name">Nama A–Z</option></select></Field></div>}
          view={<div className={catalog.view} role="group" aria-label="Tampilan daftar">{([['grid', 'Kisi', LayoutGrid], ['list', 'Daftar', List]] as const).map(([value, label, Icon]) =>
            <button key={value} type="button" aria-pressed={layout === value} onClick={() => setLayout(value)}><Icon aria-hidden="true" />{label}</button>)}</div>}
          resultText={!busy && account && !expired ? <span role="status">{formatNumber(BigInt(total))} kemasan{category ? ' di ' + categoryName(category) : ' dalam katalog'}{query ? ' untuk “' + query + '”' : ''}</span> : undefined} />
        {busy ? <div className={styles.loading}><p role="status" className={catalog.help}>Memuat katalog untuk akun Anda…</p><ul className={catalog.grid} aria-hidden="true">{Array.from({ length: 5 }, (_, index) => <li key={index} className={catalog.skeleton}><span /><span /><span /></li>)}</ul></div> : account && !expired && <>
          {items.length === 0 ? filtered
            ? <StateView kind="empty" align="center" icon={PackageOpen} title="Tidak ada kemasan yang cocok."
              message={(query ? 'Tidak ada hasil untuk “' + query + '”' : 'Kategori ini belum berisi kemasan') + (category ? ' di ' + categoryName(category) : '') + '. Coba kata lain atau lihat semua kategori.'}
              action={<Button disabled={detailBusy !== null} onClick={() => navigateCatalog({ search: '', offset: 0 }, true)}>Hapus pencarian dan filter</Button>} />
            : <StateView kind="empty" align="center" icon={PackageOpen} title="Belum ada kemasan di katalog Anda." message="Kemasan akan tampil setelah toko menerbitkannya untuk akun Anda." />
            : <ul className={layout === 'list' ? `${catalog.grid} ${styles.list}` : catalog.grid}>{items.map(item => <li className={`${catalog.card} ${styles.buyerCard}`} key={item.skuId}>
            <CatalogBuyerPhoto actorId={account.actorId} item={item} isCurrent={current}/>
            <div className={`${catalog.cardBody} ${styles.cardBody}`}>
              <span className={catalog.category}>{categoryName(item.categoryCode)}</span><h2 className={catalog.name}>{item.familyName}</h2><BuyerCodeLabel item={item} /><h3 className={styles.packageLabel}>{item.definition.packageLabel}</h3>
              <p className={catalog.meta}>{formatNumber(item.definition.baseUnitsPerPackage)} <span>{item.definition.baseUnitCode} per kemasan</span></p>
              {!requestOnly && <p className={catalog.price}>{price(item.currentPrice)}<span> per {item.definition.packageLabel}</span></p>}
              <p className={catalog.help}>Minimum {item.definition.minimumOrderPackages}; kelipatan {item.definition.orderIncrementPackages} kemasan</p>
            </div>
            <div className={`${catalog.cardActions} ${styles.cardActions}`}><Button className="ui-button--wrap" aria-label={'Rincian ' + buyerCurrentCode(item) + ' ' + item.definition.packageLabel} disabled={detailBusy !== null} onClick={event => void details(item, event.currentTarget)}>{detailBusy === item.skuId ? 'Memuat…' : 'Periksa kemasan'}</Button>
              <Link className={buttonClassName({ variant: 'tertiary', className: 'ui-button--wrap' })} href={'/shop/' + item.publicId} aria-label={'Halaman ' + buyerCurrentCode(item) + ' ' + item.definition.packageLabel}>Buka halaman kemasan</Link></div>
          </li>)}</ul>}
          {items.length > 0 && <nav className={catalog.pagination} aria-label="Halaman katalog"><Button disabled={offset === 0 || detailBusy !== null} onClick={() => navigateCatalog(at({ offset: Math.max(0, offset - 25) }))}>Sebelumnya</Button>
            <span>Halaman {offset / 25 + 1}</span><Button disabled={offset >= 10000 || BigInt(offset + items.length) >= BigInt(total) || items.length === 0 || detailBusy !== null} onClick={() => navigateCatalog(at({ offset: offset + 25 }))}>Berikutnya</Button></nav>}
        </>}</section>}
    {publicId === undefined && <dialog ref={dialog} className={styles.dialog} aria-labelledby="buyer-package-title" onCancel={event => { event.preventDefault(); close(); }}>
      {selected && !expired && <><div className={styles.dialogHeader}><p className={catalog.meta}>{buyerCurrentCode(selected)}</p><Button onClick={close}>Tutup rincian</Button></div>
        {account && <PackageReview item={selected} account={account} isCurrent={current} requestOnly={requestOnly} category={categoryName(selected.categoryCode)} />}</>}
    </dialog>}
  </main>;
}
