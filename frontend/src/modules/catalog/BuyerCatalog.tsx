'use client';

import Link from 'next/link';
import { useCallback, useEffect, useRef, useState } from 'react';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure } from '../administration/workflow';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { CatalogReadGate } from './catalog-editor-state';
import { createBuyerCatalogGateway, type BuyerVariant } from './catalog-buyer-client';
import shared from '../administration/access.module.css';
import styles from './buyer-catalog.module.css';
import { OrderQuotePanel } from '../ordering/OrderQuotePanel';

const price = (value: BuyerVariant['currentPrice']) => {
  if (value === null) return 'Harga belum tersedia';
  const [whole, fraction] = value.unitPrice.split('.'); return value.currency + ' ' + BigInt(whole).toLocaleString('id-ID') + ',' + fraction;
};
function message(error: unknown) {
  if (error instanceof AccessFailure || error instanceof IdentityFailure) {
    if (error.code === 'actor_changed') return 'Akun di peramban berubah. Periksa sesi sebelum melanjutkan.';
    if (error.status === 401) return 'Sesi telah berakhir. Masuk kembali untuk membuka katalog.';
    if (error.status === 403) return 'Katalog ini belum tersedia untuk akun Anda. Hubungi pengelola akun.';
    if (error.status === 409) return 'Data berubah saat dimuat. Muat kembali untuk melihat informasi terbaru.';
  }
  return 'Katalog belum dapat dimuat. Periksa koneksi, lalu coba lagi.';
}
type Account = { actorId: string; name: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
function PackageReview({ item, account, isCurrent, requestOnly }: { item: BuyerVariant; account: Account; isCurrent: () => boolean; requestOnly: boolean }) {
  return <div className={styles.packageLayout}><div className={styles.packageFacts}>
    {requestOnly && <div className={styles.photoPlaceholder}><span aria-hidden="true">▧</span><p>Foto belum tersedia</p></div>}
    <span className={styles.category}>{item.categoryCode}</span><h2 id="buyer-package-title" className={styles.packageTitle}>{item.familyName} — {item.definition.packageLabel}</h2>
    <p className={styles.code}>{item.code}</p>{item.description && <p className={styles.description}>{item.description}</p>}
    <p className={styles.contents}>{item.definition.baseUnitsPerPackage.toLocaleString('id-ID')} <span>{item.definition.baseUnitCode} per kemasan</span></p>
    {!requestOnly && <p className={styles.price}>{price(item.currentPrice)}<span> per {item.definition.packageLabel}</span></p>}
    <dl className={shared.facts}><div><dt>Jenis kemasan</dt><dd>{{ sealed: 'Kemasan tersegel', loose: 'Satuan lepas', other: 'Kemasan lain' }[item.definition.packageKind]}</dd></div>
      <div><dt>Minimum pembelian</dt><dd>{item.definition.minimumOrderPackages} kemasan</dd></div><div><dt>Kelipatan pemesanan</dt><dd>{item.definition.orderIncrementPackages} kemasan</dd></div>
      {item.definition.attributes.map(attribute => <div key={attribute.key}><dt>{attribute.key}</dt><dd>{attribute.value}</dd></div>)}</dl>
    <p className={shared.help}>{requestOnly ? 'Pilih kemasan ini untuk menyusun permintaan kebutuhan cabang. Petugas toko akan meninjau permintaan Anda.' : 'Harga dan ketersediaan akan diperiksa kembali saat pesanan disiapkan.'}</p>
  </div>{requestOnly ? <div className={styles.quoteArea}><h3>Siapkan permintaan cabang</h3>{account.actions.includes('order.request.submit') ? <><p>Pilih cabang dan jumlah kemasan, lalu tinjau permintaan sebelum mengirim.</p><Link className={shared.primary} href={'/ordering/requests?package=' + item.publicId}>Pilih untuk permintaan</Link></> : <p>Akun Anda dapat melihat katalog. Hubungi pengelola jika Anda memerlukan akses untuk mengajukan kebutuhan cabang.</p>}</div> : item.currentPrice && <div className={styles.quoteArea}><OrderQuotePanel key={account.actorId + ':' + item.skuId + ':' + item.definition.id + ':' + item.currentPrice.id}
    actorId={account.actorId} item={item} isCurrent={isCurrent} /></div>}</div>;
}
export function BuyerCatalog({ publicId, returnPath = '/catalog/browse', requestOnly = false }: { publicId?: string; returnPath?: '/shop' | '/catalog/browse'; requestOnly?: boolean } = {}) {
  const [identity] = useState(() => createIdentityGateway()), [access] = useState(() => createAccessGateway());
  const [gateway] = useState(() => createBuyerCatalogGateway()), [listGate] = useState(() => new CatalogReadGate()), [detailGate] = useState(() => new CatalogReadGate());
  const [account, setAccount] = useState<Account | null>(null), [items, setItems] = useState<BuyerVariant[]>([]), [total, setTotal] = useState('0');
  const [search, setSearch] = useState(''), [query, setQuery] = useState(''), [offset, setOffset] = useState(0), [revision, setRevision] = useState(0);
  const [busy, setBusy] = useState(true), [detailBusy, setDetailBusy] = useState<string | null>(null), [selected, setSelected] = useState<BuyerVariant | null>(null);
  const [error, setError] = useState<unknown>(null), [notice, setNotice] = useState(''), [elapsed, setElapsed] = useState(0);
  const dialog = useRef<HTMLDialogElement>(null), alive = useRef(false), opener = useRef<HTMLButtonElement | null>(null);
  const current = useCallback(() => alive.current && account !== null && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const refresh = useCallback(() => { detailGate.cancel(); setSelected(null); setDetailBusy(null); setRevision(value => value + 1); }, [detailGate]);
  useEffect(() => {
    alive.current = true; const timer = window.setInterval(() => { if (account) setElapsed(performance.now() - account.observedAt); }, 1000);
    const restored = (event: PageTransitionEvent) => { if (event.persisted) refresh(); };
    window.addEventListener('pageshow', restored);
    return () => { alive.current = false; window.clearInterval(timer); window.removeEventListener('pageshow', restored); };
  }, [account, refresh]);
  useEffect(() => {
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
  }, [identity, access, gateway, listGate, detailGate, query, offset, revision, publicId]);
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
  return <main className={`${shared.page} ${styles.page}`}><header className={shared.header}><div><Link className={shared.brand} href="/identity/account">Unit Toko</Link><h1>{publicId === undefined ? 'Katalog kemasan' : 'Rincian kemasan'}</h1>
    <p>{requestOnly ? 'Periksa isi dan satuan setiap kemasan sebelum mengajukan kebutuhan cabang.' : 'Periksa isi, satuan, dan harga setiap kemasan sebelum menyusun pesanan.'}</p></div><nav className={shared.actions} aria-label="Navigasi belanja">{publicId !== undefined && <Link className={shared.secondary} href={returnPath}>Katalog kemasan</Link>}<Link className={shared.secondary} href={requestOnly ? '/ordering/requests' : '/ordering/drafts'}>{requestOnly ? 'Permintaan saya' : 'Draf pesanan'}</Link><Link className={shared.secondary} href="/identity/account">Akun saya</Link></nav></header>
    {publicId === undefined ? <section className={styles.toolbar} aria-label="Pencarian katalog"><form className={styles.search} onSubmit={event => { event.preventDefault(); detailGate.cancel(); setSelected(null); setOffset(0); setQuery(search.trim()); refresh(); }}>
      <label>Cari kode atau kemasan<input value={search} maxLength={128} disabled={busy || detailBusy !== null} onChange={event => setSearch(event.target.value)} /></label><button className={shared.primary} disabled={busy || detailBusy !== null}>Cari</button></form>
      <button className={shared.secondary} disabled={busy || detailBusy !== null} onClick={refresh}>Muat kembali katalog</button></section> : <div className={styles.detailToolbar}><Link href={returnPath}>Kembali ke katalog</Link><button className={shared.secondary} disabled={busy} onClick={refresh}>Muat kembali kemasan</button></div>}
    {error !== null && <p role="alert" className={shared.alert}>{message(error)}</p>}
    {expired && <p role="alert" className={shared.alert}>Sesi telah berakhir. Masuk kembali untuk membuka katalog.</p>}
    {(expired || !account && !busy) && <Link href={'/identity/login?returnPath=' + encodeURIComponent(returnPath)}>Masuk atau periksa akun</Link>}
    {notice && <p role="status">{notice}</p>}
    {publicId !== undefined ? <section className={styles.packagePage} aria-busy={busy} aria-label="Rincian kemasan">{busy ? <p role="status">Memuat kemasan untuk akun Anda…</p> : account && !expired && selected && <PackageReview item={selected} account={account} isCurrent={current} requestOnly={requestOnly} />}</section>
      : <section aria-busy={busy} aria-label="Kemasan yang tersedia">{busy ? <p role="status">Memuat katalog untuk akun Anda…</p> : account && !expired && <>
      <p className={shared.help}>{account.name} · {BigInt(total).toLocaleString('id-ID')} kemasan dalam katalog</p>
      {items.length === 0 ? <p>Belum ada kemasan yang sesuai. Ubah pencarian atau periksa kembali nanti.</p> : <ul className={styles.grid}>{items.map(item => <li className={styles.card} key={item.skuId}>
        {requestOnly && <div className={styles.photoPlaceholder}><span aria-hidden="true">▧</span><p>Foto belum tersedia</p></div>}
        <span className={styles.category}>{item.categoryCode}</span><h2>{item.familyName}</h2><p className={styles.code}>{item.code}</p><h3>{item.definition.packageLabel}</h3>
        <p className={styles.contents}>{item.definition.baseUnitsPerPackage.toLocaleString('id-ID')} <span>{item.definition.baseUnitCode} per kemasan</span></p>
        {!requestOnly && <p className={styles.price}>{price(item.currentPrice)}<span> per {item.definition.packageLabel}</span></p>}
        <p className={shared.help}>Minimum {item.definition.minimumOrderPackages}; kelipatan {item.definition.orderIncrementPackages} kemasan</p>
        <div className={styles.cardActions}><button className={shared.primary} aria-label={'Rincian ' + item.code + ' ' + item.definition.packageLabel} disabled={detailBusy !== null} onClick={event => void details(item, event.currentTarget)}>{detailBusy === item.skuId ? 'Memuat…' : 'Periksa kemasan'}</button>
          <Link className={styles.packageLink} href={'/shop/' + item.publicId} aria-label={'Halaman ' + item.code + ' ' + item.definition.packageLabel}>Buka halaman kemasan</Link></div>
      </li>)}</ul>}
      <nav className={styles.pagination} aria-label="Halaman katalog"><button className={shared.secondary} disabled={offset === 0 || detailBusy !== null} onClick={() => setOffset(value => Math.max(0, value - 25))}>Sebelumnya</button>
        <span>Halaman {offset / 25 + 1}</span><button className={shared.secondary} disabled={offset >= 10000 || BigInt(offset + items.length) >= BigInt(total) || items.length === 0 || detailBusy !== null} onClick={() => setOffset(value => value + 25)}>Berikutnya</button></nav>
    </>}</section>}
    {publicId === undefined && <dialog ref={dialog} className={styles.dialog} aria-labelledby="buyer-package-title" onCancel={event => { event.preventDefault(); close(); }}>
      {selected && !expired && <><div className={styles.dialogHeader}><p className={styles.code}>{selected.code}</p><button className={shared.secondary} onClick={close}>Tutup rincian</button></div>
        {account && <PackageReview item={selected} account={account} isCurrent={current} requestOnly={requestOnly} />}</>}
    </dialog>}
  </main>;
}
