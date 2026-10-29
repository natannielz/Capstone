'use client';

import Link from 'next/link';
import { ArrowLeft, ArrowRight, Check, ClipboardList, FileText, PackageCheck, ShoppingBag } from 'lucide-react';
import { useCallback, useEffect, useRef, useState } from 'react';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure, MutationLedger, accessMessage, stableContent } from '../administration/workflow';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import { submitTrackedRequest } from '../administration/operation-journal';
import { priceId } from '../catalog/catalog-price-workflow';
import { createOrderDraftGateway, type OrderDraft } from './order-draft-client';
import { createOrderSubmitGateway, orderSubmitOperation, readCheckoutGroupAttempt, type CheckoutGroup, type CheckoutReview, type OrderSubmitResult, type SubmittedOrder } from './order-submit-client';
import { OrderCheckoutDetails } from './order-checkout-details';
import { OrderDeliveryPanel } from './OrderDeliveryPanel';
import { Button, buttonClassName } from '@/components/workspace-kit/button';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { StateView } from '@/components/workspace-kit/state-view';
import common from '../administration/access.module.css';
import styles from './order-checkout.module.css';
import { SessionAccessState } from "@/components/workspace-kit/session-access-state";
import { orderNumber } from '@/lib/document-numbers';
import { requestedDraftId } from './order-links';

type Account = { actorId: string; session: IdentitySession; observedAt: number; draftId: string | null };
function message(error: unknown, reading = false) {
  if (error instanceof AccessFailure) {
    if (error.code === 'order_admission_unavailable' || error.code === 'order_storage_unavailable') return 'Pengajuan belum tersedia. Aturan pembelian, tujuan pengiriman, atau sumber stok belum disetujui. Draf Anda tetap tersimpan.';
    if (error.code === 'order_credit_unavailable') return 'Batas pembelian cabang tidak mencukupi. Hubungi penanggung jawab cabang untuk peninjauan.';
    if (error.code === 'insufficient_available_stock') return 'Stok yang dapat dipesan tidak mencukupi. Periksa kembali jumlah atau kemasan.';
    if (error.status === 409 || error.code === 'order_selection_changed') return 'Harga, kemasan, atau syarat pesanan berubah. Periksa kembali draf dan tinjau ulang sebelum mengajukan.';
    if (error.status > 0 && error.status < 500) return accessMessage(error);
  }
  return reading ? 'Data belum dapat dimuat. Periksa koneksi dan muat kembali.' : 'Hasil pengajuan belum dapat dipastikan. Gunakan Periksa hasil untuk permintaan ini; jangan membuat pengajuan pengganti.';
}

export function OrderCheckoutWorkspace() {
  const [identity] = useState(() => createIdentityGateway()), [access] = useState(() => createAccessGateway());
  const [account, setAccount] = useState<Account | null>(null), [error, setError] = useState<unknown>(null), [loading, setLoading] = useState(true), [revision, setRevision] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setLoading(true); setAccount(null); setError(null);
      try {
        const session = await identity.session(controller.signal), observedAt = performance.now(); if (!active) return;
        if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
        const current = await access.current(); if (!active) return;
        if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
        if (!current.allowedActions.includes('order.submit')) throw new AccessFailure(403, 'scope_denied');
        if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
        const supplied = requestedDraftId(window.location.search);
        setAccount({ actorId: current.userId, session, observedAt, draftId: supplied === null ? null : priceId(supplied) }); setElapsed(performance.now() - observedAt);
      } catch (failure) { if (active) setError(failure); } finally { if (active) setLoading(false); }
    });
    return () => { active = false; controller.abort(); };
  }, [identity, access, revision]);
  useEffect(() => {
    const timer = window.setInterval(() => { if (account) setElapsed(performance.now() - account.observedAt); }, 1000);
    const restored = (event: PageTransitionEvent) => { if (event.persisted) setRevision(value => value + 1); };
    window.addEventListener('pageshow', restored); return () => { window.clearInterval(timer); window.removeEventListener('pageshow', restored); };
  }, [account]);
  const failed = useCallback((failure: unknown) => {
    if (failure instanceof AccessFailure && ([401, 403].includes(failure.status) || failure.code === 'actor_changed')) { setAccount(null); setError(failure); }
  }, []);
  const expired = account !== null && sessionTiming(account.session, elapsed).expired;
  // The storefront frame supplies the header, search and footer; this page only carries the checkout itself.
  return <main id="checkout-main" tabIndex={-1} className={`${common.page} ${styles.page}`}>
    <PageHeader breadcrumb={<Link href="/cart" className={styles.back}><ArrowLeft size={16} aria-hidden="true" />Kembali ke keranjang</Link>} title="Pengajuan pesanan"
      description="Isi pengiriman, tinjau biaya, lalu ajukan. Pesanan yang memerlukan persetujuan belanja dikirim ke penyetuju cabang."
      secondaryAction={!loading && account && !expired ? <Link className={buttonClassName({ variant: 'tertiary' })} href="/ordering/orders">Pesanan saya<ArrowRight aria-hidden="true" /></Link> : undefined} />
    {error !== null && account !== null && <StateView kind="error" title={message(error, true)} />}
    {loading ? <StateView kind="loading" title="Memeriksa akun…" /> : !account || expired ? <SessionAccessState kind={account ? "expired" : undefined} error={error} returnPath="/ordering/checkout" onRetry={() => setRevision(value => value + 1)} />
      : <CheckoutDesk key={account.actorId + ':' + account.observedAt} account={account} onFailure={failed} />}
  </main>;
}

type DeskGroup = { group: CheckoutGroup; requestKey: string; review: CheckoutReview | null; result: OrderSubmitResult | null;
  order: SubmittedOrder | null; uncertain: boolean; error: { value: unknown; reading: boolean } | null };

function CheckoutDesk({ account, onFailure }: { account: Account; onFailure: (error: unknown) => void }) {
  const { actorId } = account, [drafts] = useState(() => createOrderDraftGateway()), [gateway] = useState(() => createOrderSubmitGateway()), [ledger] = useState(() => new MutationLedger());
  const recovery = useOperationRecovery(actorId), [draft, setDraft] = useState<OrderDraft | null>(null), [groups, setGroups] = useState<DeskGroup[]>([]);
  const [otherReceipt, setOtherReceipt] = useState<OrderSubmitResult | null>(null), [busy, setBusy] = useState(false), [acting, setActing] = useState<string | null>(null), [recovering, setRecovering] = useState(false);
  const [error, setError] = useState<{ value: unknown; reading: boolean } | null>(null), [focused, setFocused] = useState(0);
  const alive = useRef(false), pending = useRef(false), controller = useRef<AbortController | null>(null), reviewPanel = useRef<HTMLDivElement>(null);
  const recovered = useRef<{ key: string; result: OrderSubmitResult } | null>(null);
  const current = () => alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired;
  useEffect(() => { alive.current = true; controller.current = new AbortController(); return () => { alive.current = false; controller.current?.abort(); }; }, []);
  useEffect(() => {
    let active = true; const initial = new AbortController();
    const accepted = () => active && alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired;
    void Promise.resolve().then(async () => {
      if (!active || !account.draftId) return; setBusy(true); pending.current = true;
      try { const value = await drafts.read(actorId, account.draftId, initial.signal); if (accepted()) setDraft(value); }
      catch (failure) { if (accepted()) { setError({ value: failure, reading: true }); onFailure(failure); } }
      finally { pending.current = false; if (active) setBusy(false); }
    });
    return () => { active = false; initial.abort(); };
  }, [drafts, actorId, account.draftId, onFailure, account.session, account.observedAt]);
  useEffect(() => { if (focused > 0) reviewPanel.current?.focus(); }, [focused]);
  const locked = busy || recovering || recovery.locked;
  const signal = () => controller.current?.signal;
  const update = (groupKey: string, changes: Partial<DeskGroup>) => setGroups(items => items.map(item => item.group.groupKey === groupKey ? { ...item, ...changes } : item));
  async function attempt(saved: OrderDraft, group: CheckoutGroup) {
    const checked = await readCheckoutGroupAttempt(actorId, saved.id, group.groupKey, recovery.journal, key => gateway.recover(actorId, key, signal()));
    if (!current()) throw new AccessFailure(409, 'order_view_stale');
    if (checked.result) {
      const entry = recovery.journal.list(actorId).find(entry => entry.operation === orderSubmitOperation && entry.key === checked.requestKey);
      if (entry) { try { recovery.journal.confirmed(entry); } catch { /* A committed group stays closed even if metadata cleanup fails. */ } }
      recovery.refresh();
    }
    return checked;
  }
  async function readConfirmed(groupKey: string, result: OrderSubmitResult) {
    if (!current()) return; update(groupKey, { result, review: null, uncertain: false, order: null, error: null }); setFocused(value => value + 1);
    try { const value = await gateway.read(actorId, result.orderId, signal()); if (current()) update(groupKey, { order: value }); }
    catch (failure) { if (current()) { update(groupKey, { error: { value: failure, reading: true } }); onFailure(failure); } }
  }
  async function freshGroup(saved: OrderDraft, item: DeskGroup) {
    const discovery = await gateway.groups(actorId, saved.input, signal());
    const actual = discovery.items.find(group => group.groupKey === item.group.groupKey);
    if (!actual || stableContent(actual.input) !== stableContent(item.group.input)) throw new AccessFailure(409, 'order_selection_changed');
    return actual;
  }
  async function prepare(item?: DeskGroup) {
    if (!current() || locked || pending.current || !draft || item?.result || item?.uncertain) return;
    pending.current = true; setBusy(true); setActing(item ? 'prepare:' + item.group.groupKey : 'prepare'); setError(null);
    if (item) update(item.group.groupKey, { review: null, error: null });
    try {
      const saved = await drafts.read(actorId, draft.id, signal()); if (!current()) return; setDraft(saved);
      if (item) {
        const actual = await freshGroup(saved, item), checked = await attempt(saved, actual);
        if (checked.result) await readConfirmed(actual.groupKey, checked.result);
        else if (checked.uncertain) update(actual.groupKey, { uncertain: true });
        else { const review = await gateway.preview(actorId, actual.input, signal()); if (current()) { update(actual.groupKey, { group: actual, review }); setFocused(value => value + 1); } }
      } else {
        const discovery = await gateway.groups(actorId, saved.input, signal()), rows: DeskGroup[] = [];
        for (const group of discovery.items) {
          const checked = await attempt(saved, group);
          rows.push({ group, ...checked, review: null, order: null, error: null });
        }
        if (!current()) return; setGroups(rows); setFocused(value => value + 1);
        for (const row of rows) {
          if (!current()) return;
          if (row.result) await readConfirmed(row.group.groupKey, row.result);
          else if (!row.uncertain) {
            try { const review = await gateway.preview(actorId, row.group.input, signal()); if (current()) update(row.group.groupKey, { review }); }
            catch (failure) { if (current()) { update(row.group.groupKey, { error: { value: failure, reading: true } }); onFailure(failure); } }
          }
        }
      }
    } catch (failure) { if (current()) { if (item) update(item.group.groupKey, { error: { value: failure, reading: true } }); else setError({ value: failure, reading: true }); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); setActing(null); } }
  }
  async function submit(item: DeskGroup) {
    if (!current() || locked || pending.current || !draft || !item.review || item.result || item.uncertain) return;
    const review = item.review, logical = `${orderSubmitOperation}:${item.group.groupKey}`;
    pending.current = true; setBusy(true); setActing('submit:' + item.group.groupKey); setError(null); update(item.group.groupKey, { error: null });
    try {
      if (recovery.journal.list(actorId).length > 0) throw new AccessFailure(400, 'resolve_previous_attempt');
      const saved = await drafts.read(actorId, draft.id, signal()); if (!current()) return;
      const actual = await freshGroup(saved, item), checked = await attempt(saved, actual);
      if (checked.requestKey !== item.requestKey) throw new AccessFailure(400, 'invalid_original_request_key');
      if (checked.result) { await readConfirmed(actual.groupKey, checked.result); return; }
      if (checked.uncertain || recovery.journal.list(actorId).length > 0) throw new AccessFailure(400, 'resolve_previous_attempt');
      if (!current()) return;
      const result = await submitTrackedRequest(ledger, recovery.journal, actorId, logical, orderSubmitOperation, { actorId, input: review.input, review: review.review },
        key => gateway.submit(actorId, review, key, current), item.requestKey);
      await readConfirmed(item.group.groupKey, result);
    } catch (failure) { if (current()) { update(item.group.groupKey, { review: null, uncertain: ledger.isUncertain(logical), error: { value: failure, reading: false } }); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); setActing(null); recovery.refresh(); } }
  }
  const completed = groups.filter(item => item.result !== null).length, uncertain = groups.filter(item => item.uncertain).length;
  const allComplete = groups.length > 0 && completed === groups.length;
  const draftSummary = draft && <section className={styles.draft} aria-labelledby="checkout-draft-heading"><h2 id="checkout-draft-heading">Barang dari keranjang</h2><p className={styles.draftReference}>{draft.input.selections.length} barang{draft.input.purchaseReference ? ' · ' + draft.input.purchaseReference : ''}</p>
    <p className={common.help}>Harga, stok dan aturan belanja diperiksa ulang saat Anda meninjau. <Link href="/cart">Ubah isi keranjang</Link></p>
    {!draft.input.delivery && <p className={styles.missingDelivery}>Simpan pengiriman di atas untuk melanjutkan.</p>}
    {groups.length === 0 && <div className={styles.draftAction}><Button variant="primary" disabled={locked || !draft.input.delivery} busy={acting === 'prepare'} onClick={() => void prepare()}>{busy ? 'Memeriksa…' : 'Tinjau pesanan dan biaya'}</Button><p>Setiap kanal toko diperiksa dan diajukan secara terpisah.</p></div>}</section>;
  return <>
    <nav className={styles.progress} aria-label="Tahapan pengajuan pesanan"><ol>
      <li><Link href="/cart"><ShoppingBag size={18} aria-hidden="true" /><span>Keranjang</span></Link></li>
      <li aria-hidden="true" className={styles.progressArrow}><ArrowRight size={16} /></li>
      <li aria-current={!allComplete ? 'step' : undefined}><span><ClipboardList size={18} aria-hidden="true" />Periksa pesanan</span></li>
      <li aria-hidden="true" className={styles.progressArrow}><ArrowRight size={16} /></li>
      <li aria-current={allComplete ? 'step' : undefined}><span>{allComplete ? <Check size={18} aria-hidden="true" /> : <FileText size={18} aria-hidden="true" />}Tercatat</span></li>
    </ol></nav>
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy} onBusyChange={setRecovering}
      recoveryAdapters={{ 'ordering.order': { lookup: async entry => {
        if (!current() || entry.actorId !== actorId) throw new AccessFailure(409, 'actor_changed');
        const result = await gateway.recover(entry.actorId, entry.key, signal());
        if (!current()) throw new AccessFailure(409, 'order_view_stale');
        recovered.current = result ? { key: entry.key, result: result.result } : null; return result;
      } } }} onFailure={failure => { setError({ value: failure, reading: true }); onFailure(failure); }} onResolved={() => {
        recovery.refresh(); ledger.resetAfterReconciliation(); setError(null);
        const receipt = recovered.current; recovered.current = null;
        if (receipt && current()) {
          const item = groups.find(item => item.requestKey === receipt.key);
          if (item) { setBusy(true); void readConfirmed(item.group.groupKey, receipt.result).finally(() => { if (alive.current) setBusy(false); }); }
          else setOtherReceipt(receipt.result);
        }
      }} />
    {recovery.error !== null && <StateView kind="error" title={accessMessage(recovery.error)} />}
    {error && <StateView kind="error" title={message(error.value, error.reading)} />}
    {otherReceipt && <section className={styles.confirmed}><h2>Pesanan {orderNumber(otherReceipt.orderId)} tercatat</h2><p>Hasil permintaan semula telah terkonfirmasi. Buka riwayat untuk melihat isinya.</p><Link href="/ordering/orders">Buka pesanan saya</Link></section>}
    {/* Before review: the delivery form in the 720 px column, the cart summary and its one next step beside it (U-028). */}
    {draft && groups.length === 0 ? <div className={styles.body}><div className={styles.form}><OrderDeliveryPanel key={draft.id} actorId={actorId} draft={draft} locked={locked} onFailure={onFailure}
      onSaved={async () => { const saved = await drafts.read(actorId, draft.id, signal()); if (current()) setDraft(saved); }} /></div>{draftSummary}</div> : draftSummary}
    {!draft && !busy && !recovery.locked && <StateView kind="empty" align="center" title="Keranjang belum dipilih" message="Buka keranjang, lalu tekan Lanjut ke pengajuan."
      action={<Link className={buttonClassName({ variant: 'primary' })} href="/cart">Buka keranjang</Link>} />}
    {groups.length > 0 && <div ref={reviewPanel} tabIndex={-1} aria-label="Kelompok pesanan">
      <h2>Pesanan menurut kanal toko</h2><p role="status">{completed} dari {groups.length} kelompok tercatat; {groups.length - completed - uncertain} belum diajukan; {uncertain} belum terkonfirmasi.</p>
      <p className={common.help}>Kelompok yang sudah tercatat tetap tersimpan jika kelompok lain belum berhasil. Untuk mengganti pesanan yang sudah tercatat, buat draf baru.</p>
      {groups.map(item => {
        const shown = item.order ?? item.review;
        return <section key={item.group.groupKey} className={styles.review} aria-label={`Kelompok ${item.group.channelName ?? item.group.channelId}`}>
          <h3>{item.group.shopName ?? `Toko ${item.group.shopId}`} · {item.group.channelName ?? `Kanal ${item.group.channelId}`}</h3>
          <p>{item.group.organizationName ?? `Organisasi ${item.group.organizationId}`} · {item.group.input.selections.length} jenis kemasan</p>
          {item.error && <StateView kind="error" title={message(item.error.value, item.error.reading)} />}
          {item.uncertain && <p role="status">Hasil kelompok ini belum terkonfirmasi. Gunakan Periksa hasil untuk permintaan semula; pengajuan ulang ditahan.</p>}
          {item.result && <div className={styles.confirmed}><div className={styles.confirmedHeading}><PackageCheck size={28} strokeWidth={1.5} aria-hidden="true" /><h2>Pesanan {orderNumber(item.result.orderId)} tercatat</h2></div>
            <p>{item.order?.progress.cancelled ? 'Sisa pesanan dibatalkan. Cadangan barang yang belum dikirim telah dilepas.' : item.order?.progress.spendingState === 'Rejected' ? 'Belanja cabang ditolak. Cadangan pesanan telah dilepas.' : (item.order?.progress.spendingState ?? item.result.spendingState) === 'Pending' ? 'Menunggu persetujuan belanja cabang. Penerimaan oleh toko merupakan langkah terpisah.' : 'Belanja telah disetujui atau tidak memerlukan persetujuan tambahan sesuai aturan pembelian.'}</p>
            {item.order && <p>{item.order.progress.acceptedSuborders.length} dari {item.result.suborderIds.length} kelompok pemenuhan telah diterima toko.</p>}
            {item.order && item.order.progress.rejectedSuborders.length > 0 && <p>{item.order.progress.rejectedSuborders.length} kelompok ditolak toko; cadangan bagian tersebut telah dilepas.</p>}
            <p>{item.result.suborderIds.length} kelompok pemenuhan tersimpan.{item.order && !item.order.progress.cancelled && item.order.progress.spendingState !== 'Rejected' && item.order.progress.rejectedSuborders.length === 0 ? ' Stok pesanan telah dicadangkan.' : ''}</p>
            <Button disabled={busy || recovering} onClick={() => { setBusy(true); setActing('reload:' + item.group.groupKey); void readConfirmed(item.group.groupKey, item.result!).finally(() => { if (alive.current) { setBusy(false); setActing(null); } }); }} busy={acting === 'reload:' + item.group.groupKey}>Muat kembali pesanan</Button></div>}
          {!item.result && !item.review && !item.uncertain && <Button variant="primary" disabled={locked} busy={acting === 'prepare:' + item.group.groupKey} onClick={() => void prepare(item)}>Tinjau pesanan dan biaya</Button>}
          {shown && <OrderCheckoutDetails value={shown} reviewing={item.review !== null}>
            {item.review && <><p>{item.review.spendingState === 'Pending' ? 'Pesanan memerlukan persetujuan belanja cabang sebelum diterima toko.' : 'Aturan pembelian yang berlaku tidak memerlukan persetujuan belanja tambahan.'}</p>
              <p className={common.help}>Stok kelompok ini diperiksa dan dicadangkan saat pengajuannya berhasil. Kelompok lain memerlukan konfirmasi terpisah.</p>
              <div className={styles.submitActions}><Button variant="primary" disabled={locked} busy={acting === 'submit:' + item.group.groupKey} onClick={() => void submit(item)}>{item.review.spendingState === 'Pending' ? 'Ajukan untuk persetujuan' : 'Ajukan pesanan'}</Button>
                <Button disabled={locked} onClick={() => update(item.group.groupKey, { review: null })}>Batal pengajuan</Button></div></>}
          </OrderCheckoutDetails>}
        </section>;
      })}
    </div>}
  </>;
}
