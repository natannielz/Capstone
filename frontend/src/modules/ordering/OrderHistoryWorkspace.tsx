'use client';

import Link from 'next/link';
import { useCallback, useEffect, useRef, useState } from 'react';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure, accessMessage } from '../administration/workflow';
import { useRouter } from 'next/navigation';
import { PackageOpen, SlidersHorizontal } from 'lucide-react';
import { Button, buttonClassName } from '@/components/workspace-kit/button';
import { DataList } from '@/components/workspace-kit/data-list';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { Section } from '@/components/workspace-kit/section';
import { StateView } from '@/components/workspace-kit/state-view';
import { Toolbar } from '@/components/workspace-kit/toolbar';
import { StatusBadge } from '@/components/workspace-kit/status-badge';
import { createBuyerCatalogGateway } from '../catalog/catalog-buyer-client';
import { createCartGateway } from './cart-client';
import { addToCart, cartReasons, reorderChoices } from './cart-model';
import { rememberCartBranch } from './cart-branch';
import { addToCartMessage } from './AddToCartPanel';
import { buyerOrderStatus } from './order-status';
import { createOrderSubmitGateway, type SubmittedOrder } from './order-submit-client';
import { orderDisplayAmount } from './order-quote-client';
import { OrderSavedViewPanel } from './OrderSavedViewPanel';
import { createOrderViewGateway, type OrderHistoryFilter } from './order-view-client';
import { useDraftSave } from './DraftSave';
import { OrderDraftEditor } from './OrderDraftEditor';
import type { DraftResult } from './order-draft-client';
import { prepareReorderDraft } from './order-reorder';
import common from '../administration/access.module.css';
import styles from './order-draft.module.css';
import { dateTimeFormatter } from '@/lib/format';
import { OrderTimelinePanel } from './OrderTimelinePanel';
import { SessionAccessState } from "@/components/workspace-kit/session-access-state";
import { orderNumber } from '@/lib/document-numbers';
import { requestedOrderId } from './order-links';

type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
function status(order: SubmittedOrder) {
  const progress = order.progress;
  if (progress.cancelled) return 'Sisa pesanan dibatalkan';
  if (progress.spendingState === 'Rejected') return 'Belanja cabang ditolak';
  if (progress.spendingState === 'Pending') return 'Menunggu persetujuan belanja';
  if (progress.rejectedSuborders.length === order.submission.suborderIds.length) return 'Ditolak seluruh toko';
  if (progress.rejectedSuborders.length > 0) return 'Sebagian pesanan ditolak toko';
  if (progress.acceptedSuborders.length === order.submission.suborderIds.length) return 'Diterima toko — menunggu pemenuhan';
  return 'Menunggu penerimaan toko';
}
const date = (value: string) => dateTimeFormatter.format(new Date(value));
function message(error: unknown) {
  if (error instanceof AccessFailure && error.status > 0 && error.status < 500) return accessMessage(error);
  return 'Pesanan belum dapat dimuat. Periksa koneksi dan coba kembali.';
}
export function OrderHistoryWorkspace() {
  const [identity] = useState(() => createIdentityGateway()), [access] = useState(() => createAccessGateway());
  const [account, setAccount] = useState<Account | null>(null), [error, setError] = useState<unknown>(null), [loading, setLoading] = useState(true), [revision, setRevision] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setAccount(null); setError(null); setLoading(true);
      try {
        const session = await identity.session(controller.signal), observedAt = performance.now(); if (!active) return;
        if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
        const current = await access.current(); if (!active) return;
        if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
        if (!current.allowedActions.includes('order.read')) throw new AccessFailure(403, 'scope_denied');
        if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
        setAccount({ actorId: current.userId, actions: current.allowedActions, session, observedAt }); setElapsed(performance.now() - observedAt);
      } catch (failure) { if (active) setError(failure); } finally { if (active) setLoading(false); }
    }); return () => { active = false; controller.abort(); };
  }, [identity, access, revision]);
  useEffect(() => {
    const timer = window.setInterval(() => { if (account) setElapsed(performance.now() - account.observedAt); }, 1000);
    const restored = (event: PageTransitionEvent) => { if (event.persisted) { setAccount(null); setRevision(value => value + 1); } };
    window.addEventListener('pageshow', restored); return () => { window.clearInterval(timer); window.removeEventListener('pageshow', restored); };
  }, [account]);
  const failed = useCallback((failure: unknown) => {
    if (failure instanceof AccessFailure && ([401, 403].includes(failure.status) || failure.code === 'actor_changed')) { setAccount(null); setError(failure); }
  }, []);
  const expired = account !== null && sessionTiming(account.session, elapsed).expired;
  return <main className={common.page}><PageHeader title="Pesanan saya" description="Status setiap pesanan, rinciannya, dan pesan lagi dengan sekali tekan." />
    {error !== null && account !== null && <StateView kind="error" title={message(error)} />}
    {loading ? <StateView kind="loading" title="Memeriksa akun…" /> : !account || expired ? <SessionAccessState kind={account ? "expired" : undefined} error={error} returnPath="/ordering/orders" onRetry={() => setRevision(value => value + 1)} />
      : <OrderHistoryDesk key={account.actorId + ':' + account.observedAt} account={account} onFailure={failed} />}
  </main>;
}
function OrderHistoryDesk({ account, onFailure }: { account: Account; onFailure: (error: unknown) => void }) {
  const [gateway] = useState(() => createOrderSubmitGateway()), [views] = useState(() => createOrderViewGateway());
  const [filter, setFilter] = useState<OrderHistoryFilter | null>(null), [filterLocked, setFilterLocked] = useState(true);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [page, setPage] = useState<{ items: SubmittedOrder[]; hasMore: boolean }>({ items: [], hasMore: false });
  const [cursor, setCursor] = useState<string | null>(null), [reload, setReload] = useState(0), [selected, setSelected] = useState<SubmittedOrder | null>(null);
  const [busy, setBusy] = useState(false), [acting, setActing] = useState<string | null>(null), [error, setError] = useState<unknown>(null), detail = useRef<HTMLElement>(null), alive = useRef(false), pending = useRef(false);
  const [repeat, setRepeat] = useState<ReturnType<typeof prepareReorderDraft> | null>(null), [saved, setSaved] = useState<DraftResult | null>(null), [notice, setNotice] = useState('');
  const [cart] = useState(() => createCartGateway()), [catalog] = useState(() => createBuyerCatalogGateway()), router = useRouter(), [cartError, setCartError] = useState<unknown>(null);
  const repeatRegion = useRef<HTMLDivElement>(null), repeatReadEpoch = useRef(0), requested = useRef<string | null | undefined>(undefined);
  const current = () => alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired;
  const save = useDraftSave(account.actorId, current, result => { repeatReadEpoch.current++; setRepeat(null); setSaved(result); setNotice(''); }, onFailure, busy);
  const locked = busy || save.locked || repeat !== null || filterLocked;
  const applyFilter = useCallback((value: OrderHistoryFilter) => {
    repeatReadEpoch.current++; setRepeat(null); setSelected(null); setSaved(null); setCursor(null); setFilter(value); setReload(current => current + 1);
  }, []);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  useEffect(() => { if (repeat) repeatRegion.current?.focus(); }, [repeat]);
  useEffect(() => {
    const changed = (event: StorageEvent) => {
      if (event.key === null || event.key.startsWith('unit-toko:pending:v1:' + account.actorId + ':')) {
        repeatReadEpoch.current++;
        setRepeat(null); setSaved(null); setNotice('Ada perubahan referensi di tab lain. Periksa hasil yang belum pasti, lalu muat kembali pesanan sebelum membuat draf baru.');
      }
    };
    window.addEventListener('storage', changed); return () => window.removeEventListener('storage', changed);
  }, [account.actorId]);
  useEffect(() => {
    if (filter === null) return;
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setBusy(true); setError(null); setSelected(null); setPage({ items: [], hasMore: false });
      try {
        const result = await views.orders(account.actorId, filter, cursor, controller.signal); if (active) {setPage(result); setNextCursor(result.nextCursor);}
        // An address naming one order (/ordering/orders?order=41, where old /account/orders/41 links lead) opens it with the first list (G-009).
        if (requested.current === undefined) requested.current = requestedOrderId(window.location.search);
        const wanted = requested.current; requested.current = null;
        if (active && wanted) { const order = await gateway.read(account.actorId, wanted); if (active) setSelected(order); }
      }
      catch (failure) { if (active) { setError(failure); onFailure(failure); } } finally { if (active) setBusy(false); }
    }); return () => { active = false; controller.abort(); };
  }, [views, gateway, account.actorId, filter, cursor, reload, onFailure]);
  useEffect(() => { if (selected && repeat === null) detail.current?.focus(); }, [selected, repeat]);
  async function select(id: string) {
    const current = () => alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired;
    if (!current() || locked || pending.current) return; pending.current = true; setBusy(true); setActing('open:' + id); setError(null); setSelected(null);
    try { const result = await gateway.read(account.actorId, id); if (current()) { setSelected(result); setPage(previous => ({ ...previous, items: previous.items.map(order => order.submission.orderId === id ? result : order) })); } }
    catch (failure) { if (current()) { setError(failure); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); setActing(null); } }
  }
  async function beginRepeat(id: string) {
    if (!current() || locked || pending.current || !account.actions.includes('order.submit')) return;
    const epoch = ++repeatReadEpoch.current;
    pending.current = true; setBusy(true); setActing('repeat:' + id); setError(null); setSaved(null); setNotice('');
    try {
      // Reload through the historical owner before using an old snapshot as editable draft input.
      const original = await gateway.read(account.actorId, id);
      if (current() && repeatReadEpoch.current === epoch) { setSelected(original); setRepeat(prepareReorderDraft(original)); }
    } catch (failure) { if (current()) { setError(failure); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); setActing(null); } }
  }
  /** "Pesan lagi": the order's packages go into the branch cart at today's package and price, and the buyer lands on the cart to check them. */
  async function orderAgain(order: SubmittedOrder) {
    if (!current() || locked || pending.current || !account.actions.includes('order.submit')) return;
    pending.current = true; setBusy(true); setActing('again:' + order.submission.orderId); setError(null); setCartError(null); setNotice('');
    try {
      const { choices, unavailable } = reorderChoices(order.input.selections, await catalog.bySku(account.actorId, order.input.selections.map(line => line.skuId)));
      if (choices.length === 0) { setNotice('Barang dari pesanan ini tidak lagi tersedia untuk cabang Anda.'); return; }
      await cart.apply(account.actorId, order.input.context, current => choices.reduce((list, item) => addToCart(list, item.choice, item.rule), current), cartReasons.reorder);
      rememberCartBranch(account.actorId, order.input.context);
      router.push(unavailable.length ? '/cart?terlewat=' + unavailable.length : '/cart');
    } catch (failure) { if (current()) { setCartError(failure); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); setActing(null); } }
  }
  const canOrder = account.actions.includes('order.submit');
  return <>
    {save.recoveryView}
    {cartError !== null && <StateView kind="error" title={addToCartMessage(cartError)} />}
    {notice && <p role="status" className={common.help}>{notice}</p>}
    {saved && <section className={styles.saved} aria-label="Draf baru tersimpan"><h2>Draf {saved.draftId} tersimpan</h2>
      <p role="status">Pesanan baru belum diajukan. Harga, ketersediaan, dan tujuan tetap diperiksa pada pengajuan.</p>
      <div className="ui-actions"><Link className={buttonClassName({ variant: 'secondary' })} href={'/ordering/checkout?draft=' + encodeURIComponent(saved.draftId)}>Tinjau pengajuan draf</Link>
        <Link className={buttonClassName({ variant: 'secondary' })} href="/ordering/drafts">Daftar draf saya</Link></div>
    </section>}
    {repeat && <div ref={repeatRegion} tabIndex={-1} role="region" aria-label="Pemesanan ulang" className={styles.repeat}>
      <p>Pilihan dari pesanan {orderNumber(repeat.sourceOrderId)} menjadi awal draf baru untuk cabang yang sama. Jumlah dan rencana pengiriman masih perlu Anda periksa. Referensi pembelian lama tidak disalin.</p>
      <p>Kemasan yang sudah berubah, harga yang tidak berlaku, atau akses cabang yang dicabut harus diperiksa kembali; sistem tidak menggantinya otomatis.</p>
      <OrderDraftEditor key={repeat.sourceOrderId} actorId={account.actorId} draft={repeat.draft} initialQuote={repeat.historicalQuote} initialIssue={null}
        reorderSourceId={repeat.sourceOrderId} locked={busy || save.locked} isCurrent={current} onSubmit={save.submit} onFailure={onFailure} />
      <Button disabled={busy || save.locked} onClick={() => setRepeat(null)}>Batalkan draf pemesanan ulang</Button>
    </div>}
    {error !== null && <StateView kind="error" title={message(error)} />}
    <Section title="Pesanan yang diajukan" label="Daftar pesanan saya" busy={busy}
      actions={<><Button disabled={locked} onClick={() => { setCursor(null); setReload(value => value + 1); }}>{filter?.sort === 'oldest' ? 'Muat pesanan pertama' : 'Muat pesanan terbaru'}</Button>
        {canOrder && !locked && <Link href="/ordering/drafts" className={buttonClassName({ variant: 'tertiary' })}>Draf pesanan</Link>}</>}>
      <Toolbar label="Filter pesanan" filters={<details className={styles.filterPanel}><summary className={buttonClassName({ variant: 'secondary' })}><SlidersHorizontal aria-hidden="true" />Filter dan urutan</summary>
        <OrderSavedViewPanel actorId={account.actorId} active={filter} locked={busy || save.locked || repeat !== null} isCurrent={current}
          onApply={applyFilter} onLock={setFilterLocked} onFailure={onFailure} /></details>} />
      <DataList label="Pesanan saya" items={page.items} getKey={order => order.submission.orderId} loading={busy}
        empty={error !== null ? undefined : { title: 'Belum ada pesanan pada halaman ini.', message: canOrder ? 'Pesanan yang diajukan dari draf akan muncul di sini, lengkap dengan statusnya.' : 'Halaman ini hanya menampilkan pesanan yang diajukan dari akun Anda sendiri. Akun Anda tidak mengajukan pesanan cabang; pesanan untuk toko ditinjau di Pekerjaan saya.' }}
        renderTitle={order => orderNumber(order.submission.orderId)} renderMeta={order => date(order.submission.recordedAt)}
        renderBadge={order => <StatusBadge text={buyerOrderStatus(order).label} />}
        renderContent={order => { const extra = order.quote.lines.length - 2; return <>
          <ul className={styles.orderLines}>{order.quote.lines.slice(0, 2).map(line => <li key={line.definition.skuId}>
            <span className={styles.orderThumb}><PackageOpen size={22} strokeWidth={1.4} aria-hidden="true" /></span>
            <span><strong>{line.familyName}</strong><small>{line.packages} × {line.definition.packageLabel} · {orderDisplayAmount(order.quote.currency, line.price.unitPrice)}</small></span></li>)}</ul>
          {extra > 0 && <p className={styles.listMeta}>+{extra} barang lain</p>}
          <p className={styles.orderNext}>{buyerOrderStatus(order).detail}</p>
          <p className={styles.orderTotal}>Total <strong>{orderDisplayAmount(order.quote.currency, order.total)}</strong></p></>; }}
        renderActions={order => { const id = order.submission.orderId; return <>
          <Button disabled={locked} busy={acting === 'open:' + id} aria-label={'Lihat rincian pesanan ' + orderNumber(id)} onClick={() => void select(id)}>Lihat rincian</Button>
          {canOrder && <Button disabled={locked} busy={acting === 'again:' + id} onClick={() => void orderAgain(order)}>Pesan lagi</Button>}</>; }} />
      {page.hasMore && <div className="ui-actions"><Button disabled={locked} onClick={() => setCursor(nextCursor)}>{filter?.sort === 'oldest' ? 'Pesanan lebih baru' : 'Pesanan lebih lama'}</Button></div>}
    </Section>
    {selected && <section ref={detail} tabIndex={-1} className={styles.detail} aria-label="Rincian pesanan"><h2>Pesanan {orderNumber(selected.submission.orderId)}</h2>
      <p>{selected.buyerName} · {date(selected.submission.recordedAt)}</p><p role="status"><StatusBadge text={buyerOrderStatus(selected).label} /> <span>{status(selected)}</span></p>
      {selected.input.purchaseReference && <p>Referensi pembelian: {selected.input.purchaseReference}</p>}
      <h3>Kemasan saat pengajuan</h3><ul>{selected.quote.lines.map(line => <li key={line.definition.skuId}><strong>{line.familyName} — {line.definition.packageLabel}</strong>
        <p>{line.packages} kemasan · {line.definition.baseUnitsPerPackage} {line.definition.baseUnitCode} per kemasan · total {line.baseUnits} {line.definition.baseUnitCode}</p>
        <p>{orderDisplayAmount(selected.quote.currency, line.price.unitPrice)} per kemasan · subtotal {orderDisplayAmount(selected.quote.currency, line.catalogSubtotal)}</p></li>)}</ul>
      <h3>Tujuan pengiriman</h3><p className={styles.address}>{selected.input.delivery!.recipientName} · {selected.input.delivery!.phone}{'\n'}{selected.input.delivery!.address}{'\n'}{selected.input.delivery!.city} {selected.input.delivery!.postalCode}</p>
      {selected.input.delivery!.instructions && <p>{selected.input.delivery!.instructions}</p>}
      {account.actions.includes('order.cancel') && !selected.progress.cancelled && <p><Link href="/ordering/reviews">Batalkan sisa pesanan ini di Peninjauan pesanan</Link></p>}
      {account.actions.some(action => ['substitution.propose', 'substitution.accept'].includes(action)) && <p><Link href={'/ordering/substitutions?order=' + selected.submission.orderId}>Lihat pengajuan dan keputusan penggantian pesanan ini</Link></p>}
      <h3>Biaya saat pengajuan</h3><p>Subtotal barang: {orderDisplayAmount(selected.quote.currency, selected.quote.catalogSubtotal)}</p>
      <p>Pajak: {orderDisplayAmount(selected.quote.currency, selected.tax)} · Biaya lain: {orderDisplayAmount(selected.quote.currency, selected.charges)}</p><p><strong>Total: {orderDisplayAmount(selected.quote.currency, selected.total)}</strong></p>
      <p>Bagian toko: {selected.progress.acceptedSuborders.length} diterima, {selected.progress.rejectedSuborders.length} ditolak, dari {selected.submission.suborderIds.length} bagian.</p>
      {(selected.progress.cancelled || selected.progress.spendingState === 'Rejected' || selected.progress.rejectedSuborders.length > 0) && <p>Angka di atas menyimpan biaya pengajuan awal. Penolakan atau pembatalan bukan bukti pembayaran maupun pengembalian dana.</p>}
      <OrderTimelinePanel key={selected.submission.orderId} actorId={account.actorId} orderId={selected.submission.orderId}
        actions={account.actions} isCurrent={current} onFailure={onFailure} />
      <div className="ui-actions"><Button disabled={locked} onClick={() => void select(selected.submission.orderId)}>Perbarui perkembangan</Button>
        {canOrder && <><Button variant="primary" disabled={locked} busy={acting === 'again:' + selected.submission.orderId} onClick={() => void orderAgain(selected)}>Pesan lagi</Button>
          <Button disabled={locked} busy={acting === 'repeat:' + selected.submission.orderId} onClick={() => void beginRepeat(selected.submission.orderId)}>Pesan ulang sebagai draf</Button></>}
      </div>
    </section>}
  </>;
}
