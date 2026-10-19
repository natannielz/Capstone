'use client';

import Link from 'next/link';
import { useCallback, useEffect, useRef, useState } from 'react';
import { ArrowRight, ArrowUpRight, ClipboardList, PackageOpen, Search, ShieldCheck } from 'lucide-react';
import { Art } from '../../components/art';
import { PilotSiteHeader, PilotSiteFooter } from '../../components/pilot-site-chrome';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure } from '../administration/workflow';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { CatalogReadGate } from './catalog-editor-state';
import { buyerCurrentCode, createBuyerCatalogGateway, type BuyerVariant } from './catalog-buyer-client';
import { CatalogBuyerPhoto } from './CatalogBuyerPhoto';
import { catalogHistoryUrl, normalizeCatalogHistorySearch, readCatalogHistoryQuery, type CatalogHistoryQuery } from './catalog-history';
import shared from '../administration/access.module.css';
import styles from './buyer-catalog.module.css';
import { OrderQuotePanel } from '../ordering/OrderQuotePanel';
import { formatMoney, formatNumber } from "@/lib/format";

const price = (value: BuyerVariant['currentPrice']) => {
  if (value === null) return 'Harga belum tersedia';
  return formatMoney(value.currency, value.unitPrice);
};
function message(error: unknown) {
  if (error instanceof AccessFailure || error instanceof IdentityFailure) {
    if (error.code === 'actor_changed') return 'Akun di peramban berubah. Periksa sesi sebelum melanjutkan.';
    if (error.status === 401) return 'Sesi telah berakhir. Masuk kembali untuk membuka katalog.';
    if (error.status === 403) return 'Katalog ini belum tersedia untuk akun Anda. Hubungi pengelola akun.';
    if (error.status === 409) return 'Data berubah saat dimuat. Muat kembali untuk melihat informasi terbaru.';
    if (error.status >= 500) return 'Layanan katalog belum tersedia. Coba lagi nanti atau hubungi pengelola organisasi Anda.';
  }
  return 'Katalog belum dapat dimuat. Periksa koneksi, lalu coba lagi.';
}
type Account = { actorId: string; name: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
function BuyerCodeLabel({ item }: { item: BuyerVariant }) {
  return <><p className={styles.code}>{item.buyerIdentity ? 'Kode utama saat ini: ' : 'Referensi SKU: '}{buyerCurrentCode(item)}</p>
    {item.buyerIdentity && item.code !== buyerCurrentCode(item) && <p className={shared.help}>Referensi SKU tetap pada catatan transaksi: {item.code}. Kode ini hanya dapat dipakai mencari jika aliasnya masih aktif.</p>}</>;
}
function PackageReview({ item, account, isCurrent, requestOnly }: { item: BuyerVariant; account: Account; isCurrent: () => boolean; requestOnly: boolean }) {
  return <div className={styles.packageLayout}><div className={styles.packageFacts}>
    {requestOnly ? <div className={styles.photoPlaceholder}><PackageOpen size={42} strokeWidth={1} aria-hidden="true" /><p>Foto belum tersedia</p></div> : <CatalogBuyerPhoto actorId={account.actorId} item={item} isCurrent={isCurrent}/>}
    <span className={styles.category}>{item.categoryCode}</span><h2 id="buyer-package-title" className={styles.packageTitle}>{item.familyName} — {item.definition.packageLabel}</h2>
    <BuyerCodeLabel item={item} />{item.description && <p className={styles.description}>{item.description}</p>}
    <p className={styles.contents}>{formatNumber(item.definition.baseUnitsPerPackage)} <span>{item.definition.baseUnitCode} per kemasan</span></p>
    {!requestOnly && <p className={styles.price}>{price(item.currentPrice)}<span> per {item.definition.packageLabel}</span></p>}
    <dl className={shared.facts}><div><dt>Jenis kemasan</dt><dd>{{ sealed: 'Kemasan tersegel', loose: 'Satuan lepas', other: 'Kemasan lain' }[item.definition.packageKind]}</dd></div>
      <div><dt>{requestOnly ? 'Minimum permintaan' : 'Minimum pembelian'}</dt><dd>{item.definition.minimumOrderPackages} kemasan</dd></div><div><dt>Kelipatan jumlah</dt><dd>{item.definition.orderIncrementPackages} kemasan</dd></div>
      {item.definition.attributes.map(attribute => <div key={attribute.key}><dt>{attribute.key}</dt><dd>{attribute.value}</dd></div>)}</dl>
    <p className={shared.help}>{requestOnly ? 'Pilih kemasan ini untuk menyusun permintaan kebutuhan cabang. Petugas toko akan meninjau permintaan Anda.' : 'Harga dan ketersediaan akan diperiksa kembali saat pesanan disiapkan.'}</p>
  </div>{requestOnly ? <div className={styles.quoteArea}><h3>Siapkan permintaan cabang</h3>{account.actions.includes('order.request.submit') ? <><p>Pilih cabang dan jumlah kemasan, lalu tinjau permintaan sebelum mengirim.</p><Link className={shared.primary} href={'/ordering/requests?package=' + item.publicId}>Pilih untuk permintaan</Link></> : <p>Akun Anda dapat melihat katalog. Hubungi pengelola jika Anda memerlukan akses untuk mengajukan kebutuhan cabang.</p>}</div> : item.currentPrice && <div className={styles.quoteArea}><OrderQuotePanel key={account.actorId + ':' + item.skuId + ':' + item.definition.id + ':' + item.currentPrice.id}
    actorId={account.actorId} item={item} isCurrent={isCurrent} /></div>}</div>;
}
export function BuyerCatalog({ publicId, returnPath = '/catalog/browse', requestOnly = false }: { publicId?: string; returnPath?: '/shop' | '/catalog/browse'; requestOnly?: boolean } = {}) {
  const [identity] = useState(() => createIdentityGateway()), [access] = useState(() => createAccessGateway());
  const [gateway] = useState(() => createBuyerCatalogGateway()), [listGate] = useState(() => new CatalogReadGate()), [detailGate] = useState(() => new CatalogReadGate());
  const [account, setAccount] = useState<Account | null>(null), [items, setItems] = useState<BuyerVariant[]>([]), [total, setTotal] = useState('0');
  const [search, setSearch] = useState(''), [catalogQuery, setCatalogQuery] = useState<CatalogHistoryQuery | null>(null), [revision, setRevision] = useState(0);
  const query = catalogQuery?.search ?? '', offset = catalogQuery?.offset ?? 0;
  const [busy, setBusy] = useState(true), [detailBusy, setDetailBusy] = useState<string | null>(null), [selected, setSelected] = useState<BuyerVariant | null>(null);
  const [error, setError] = useState<unknown>(null), [notice, setNotice] = useState(''), [elapsed, setElapsed] = useState(0);
  const dialog = useRef<HTMLDialogElement>(null), alive = useRef(false), opener = useRef<HTMLButtonElement | null>(null);
  const appliedQuery = useRef<CatalogHistoryQuery | null>(null);
  const current = useCallback(() => alive.current && account !== null && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const refresh = useCallback(() => { detailGate.cancel(); setSelected(null); setDetailBusy(null); setRevision(value => value + 1); }, [detailGate]);
  const changeCatalogQuery = useCallback((next: CatalogHistoryQuery) => {
    if (appliedQuery.current?.search === next.search && appliedQuery.current.offset === next.offset) return;
    appliedQuery.current = next;
    listGate.cancel(); detailGate.cancel();
    setCatalogQuery(next); setSearch(next.search);
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
    const request = listGate.begin('session', publicId ?? query + ':' + offset); let active = true;
    void Promise.resolve().then(async () => {
      if (!active) return; setBusy(true); setItems([]); setAccount(null); setSelected(null); setError(null); setNotice(''); setTotal('0');
      try {
        const session = await identity.session(request.signal), observedAt = performance.now(); if (!request.current()) return;
        if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
        const scope = await access.current(); if (!request.current()) return;
        if (scope.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
        const page = publicId === undefined ? await gateway.query(scope.userId, query, offset, request.signal) : null;
        const variant = publicId === undefined ? null : await gateway.variant(scope.userId, publicId, request.signal); if (!request.current()) return;
        if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
        setAccount({ actorId: scope.userId, name: session.account.displayName ?? 'Akun Anda', actions: scope.allowedActions, session, observedAt });
        setElapsed(performance.now() - observedAt); setItems(page?.items ?? []); setTotal(page?.total ?? '0'); setSelected(variant);
        if (publicId !== undefined && variant === null) setNotice('Kemasan ini belum tersedia untuk akun Anda. Kembali ke katalog untuk memeriksa pilihan yang tersedia.');
      } catch (failure) { if (request.current()) setError(failure); }
      finally { if (request.current()) setBusy(false); }
    });
    return () => { active = false; listGate.cancel(); detailGate.cancel(); };
  }, [identity, access, gateway, listGate, detailGate, query, offset, revision, publicId, catalogQuery]);
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
    if (reload) setSearch(next.search);
    if (reload) refresh();
  }
  const needsSignIn = error instanceof IdentityFailure && error.status === 401;
  return <div className={styles.storefront}>{requestOnly && <><a className={styles.skip} href="#catalog-main">Langsung ke katalog</a><PilotSiteHeader active="catalog" /></>}
    <main id="catalog-main" tabIndex={-1} className={`${shared.page} ${styles.page}`}>
    {requestOnly ? <>
      <nav className={styles.breadcrumb} aria-label="Jejak halaman"><Link href="/">Beranda</Link><span aria-hidden="true">/</span><span>{publicId === undefined ? 'Katalog' : 'Rincian kemasan'}</span></nav>
      {publicId === undefined ? <section className={styles.editorial} aria-labelledby="catalog-title">
        <div className={styles.editorialCopy}><p className={styles.eyebrow}>KEBUTUHAN CABANG & ORGANISASI</p><h1 id="catalog-title">Pilihan tepat.<br /><em>Hari lebih siap.</em></h1><p>Temukan kebutuhan tim, periksa isi kemasannya, lalu ajukan dalam satu permintaan yang tercatat.</p><a className={styles.editorialLink} href="#catalog-list">Jelajahi katalog <ArrowRight size={18} aria-hidden="true" /></a></div>
        <figure className={styles.editorialPhoto}><Art src="/images/editorial/hero-still-life-v3.png" alt="Ilustrasi kebutuhan kerja: tas, tumbler, kopi, dan camilan" loading="eager" fetchPriority="high" sizes="(max-width: 720px) 100vw, 50vw" /><figcaption>Ilustrasi kebutuhan · pilihan tersedia mengikuti katalog akun Anda</figcaption></figure>
      </section> : <header className={styles.detailHeading}><p className={styles.eyebrow}>KENALI PILIHAN ANDA</p><h1>Rincian kemasan</h1><p>Periksa isi dan satuan sebelum mengajukan kebutuhan cabang.</p></header>}
      <div className={styles.assurances}><p><PackageOpen size={20} aria-hidden="true" /><span>Isi & satuan kemasan jelas</span></p><p><ShieldCheck size={20} aria-hidden="true" /><span>Akses sesuai akun organisasi</span></p><p><ClipboardList size={20} aria-hidden="true" /><span>Permintaan dapat ditelusuri</span></p></div>
      <div className={styles.sectionHeading} id="catalog-list"><div><p className={styles.eyebrow}>KATALOG UNIT TOKO</p><h2>{publicId === undefined ? 'Katalog kemasan' : 'Informasi kemasan'}</h2></div><Link href="/ordering/requests">Permintaan saya <ArrowUpRight size={18} aria-hidden="true" /></Link></div>
      <p className={styles.availability}>Harga, stok, dan pemenuhan dikonfirmasi petugas. Permintaan yang diterima belum menjadi pemesanan dengan harga mengikat, reservasi stok, pembayaran, atau invoice.</p>
    </> : <header className={shared.header}><div><Link className={shared.brand} href="/identity/account">Unit Toko</Link><h1>{publicId === undefined ? 'Katalog kemasan' : 'Rincian kemasan'}</h1>
    <p>Periksa isi, satuan, dan harga setiap kemasan sebelum menyusun pesanan.</p></div><nav className={shared.actions} aria-label="Navigasi belanja">{publicId !== undefined && <Link className={shared.secondary} href={returnPath}>Katalog kemasan</Link>}<Link className={shared.secondary} href="/ordering/drafts">Draf pesanan</Link><Link className={shared.secondary} href="/identity/account">Akun saya</Link></nav></header>}
    {publicId === undefined ? <section className={styles.toolbar} aria-label="Pencarian katalog"><form className={styles.search} onSubmit={event => { event.preventDefault(); navigateCatalog({ search: normalizeCatalogHistorySearch(search), offset: 0 }, true); }}>
      <label>Cari kode atau kemasan<input value={search} placeholder="Nama barang, kode, atau jenis kemasan" maxLength={128} disabled={busy || detailBusy !== null || !account} onChange={event => setSearch(event.target.value)} /></label><button className={shared.primary} disabled={busy || detailBusy !== null || !account}><Search size={17} aria-hidden="true" /> Cari</button></form>
      <button className={shared.secondary} disabled={busy || detailBusy !== null} onClick={refresh}>Muat kembali katalog</button></section> : <div className={styles.detailToolbar}><Link href={returnPath}>Kembali ke katalog</Link><button className={shared.secondary} disabled={busy} onClick={refresh}>Muat kembali kemasan</button></div>}
    {error !== null && <div role={needsSignIn ? 'status' : 'alert'} className={styles.accessState}><div className={styles.stateIcon}><ShieldCheck size={27} strokeWidth={1.5} aria-hidden="true" /></div><div><h2>{needsSignIn ? 'Katalog khusus untuk akun Anda.' : 'Katalog belum dapat ditampilkan.'}</h2><p>{needsSignIn ? 'Masuk dengan akun yang telah disetujui untuk melihat pilihan kemasan dan mengajukan kebutuhan cabang Anda.' : message(error)}</p>{!needsSignIn && <p className={styles.stateHint}>Daftar barang akan tampil setelah layanan tersedia dan akses akun Anda terverifikasi.</p>}<Link className={styles.stateAction} href={'/identity/login?returnPath=' + encodeURIComponent(returnPath)}>Masuk atau periksa akun <ArrowRight size={17} aria-hidden="true" /></Link></div></div>}
    {expired && <p role="alert" className={shared.alert}>Sesi telah berakhir. Masuk kembali untuk membuka katalog.</p>}
    {(expired || !account && !busy && error === null) && <Link className={styles.stateAction} href={'/identity/login?returnPath=' + encodeURIComponent(returnPath)}>Masuk atau periksa akun <ArrowRight size={17} aria-hidden="true" /></Link>}
    {notice && <p role="status">{notice}</p>}
    {publicId !== undefined ? <section className={styles.packagePage} aria-busy={busy} aria-label="Rincian kemasan">{busy ? <p role="status">Memuat kemasan untuk akun Anda…</p> : account && !expired && selected && <PackageReview item={selected} account={account} isCurrent={current} requestOnly={requestOnly} />}</section>
      : <section aria-busy={busy} aria-label="Kemasan yang tersedia">{busy ? <div className={styles.loading}><p role="status">Memuat katalog untuk akun Anda…</p><div className={styles.skeletonGrid} aria-hidden="true"><span /><span /><span /></div></div> : account && !expired && <>
      <p className={shared.help}>{account.name} · {formatNumber(BigInt(total))} kemasan dalam katalog</p>
      {items.length === 0 ? <div className={styles.empty}><PackageOpen size={36} strokeWidth={1.25} aria-hidden="true" /><h3>Belum ada kemasan yang sesuai.</h3><p>Ubah pencarian atau periksa kembali nanti.</p></div> : <ul className={styles.grid}>{items.map(item => <li className={styles.card} key={item.skuId}>
        {requestOnly ? <div className={styles.photoPlaceholder}><PackageOpen size={48} strokeWidth={1} aria-hidden="true" /><p>Foto belum tersedia</p></div> : <CatalogBuyerPhoto actorId={account.actorId} item={item} isCurrent={current}/>}
        <span className={styles.category}>{item.categoryCode}</span><h2>{item.familyName}</h2><BuyerCodeLabel item={item} /><h3>{item.definition.packageLabel}</h3>
        <p className={styles.contents}>{formatNumber(item.definition.baseUnitsPerPackage)} <span>{item.definition.baseUnitCode} per kemasan</span></p>
        {!requestOnly && <p className={styles.price}>{price(item.currentPrice)}<span> per {item.definition.packageLabel}</span></p>}
        <p className={shared.help}>Minimum {item.definition.minimumOrderPackages}; kelipatan {item.definition.orderIncrementPackages} kemasan</p>
        <div className={styles.cardActions}><button className={shared.primary} aria-label={'Rincian ' + buyerCurrentCode(item) + ' ' + item.definition.packageLabel} disabled={detailBusy !== null} onClick={event => void details(item, event.currentTarget)}>{detailBusy === item.skuId ? 'Memuat…' : 'Periksa kemasan'}</button>
          <Link className={styles.packageLink} href={'/shop/' + item.publicId} aria-label={'Halaman ' + buyerCurrentCode(item) + ' ' + item.definition.packageLabel}>Buka halaman kemasan</Link></div>
      </li>)}</ul>}
      <nav className={styles.pagination} aria-label="Halaman katalog"><button className={shared.secondary} disabled={offset === 0 || detailBusy !== null} onClick={() => navigateCatalog({ search: query, offset: Math.max(0, offset - 25) })}>Sebelumnya</button>
        <span>Halaman {offset / 25 + 1}</span><button className={shared.secondary} disabled={offset >= 10000 || BigInt(offset + items.length) >= BigInt(total) || items.length === 0 || detailBusy !== null} onClick={() => navigateCatalog({ search: query, offset: offset + 25 })}>Berikutnya</button></nav>
    </>}</section>}
    {publicId === undefined && <dialog ref={dialog} className={styles.dialog} aria-labelledby="buyer-package-title" onCancel={event => { event.preventDefault(); close(); }}>
      {selected && !expired && <><div className={styles.dialogHeader}><p className={styles.code}>{buyerCurrentCode(selected)}</p><button className={shared.secondary} onClick={close}>Tutup rincian</button></div>
        {account && <PackageReview item={selected} account={account} isCurrent={current} requestOnly={requestOnly} />}</>}
    </dialog>}
  </main>{requestOnly && <PilotSiteFooter />}</div>;
}
