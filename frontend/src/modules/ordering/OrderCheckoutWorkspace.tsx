'use client';

import Link from 'next/link';
import { ArrowLeft, ArrowRight, Check, ClipboardList, FileText, PackageCheck, ShoppingBag } from 'lucide-react';
import { useCallback, useEffect, useRef, useState } from 'react';
import { Brand } from '../../components/brand';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import { submitTrackedRequest } from '../administration/operation-journal';
import { priceId } from '../catalog/catalog-price-workflow';
import { createOrderDraftGateway, type OrderDraft } from './order-draft-client';
import { createOrderSubmitGateway, type CheckoutReview, type OrderSubmitResult, type SubmittedOrder } from './order-submit-client';
import { OrderCheckoutDetails } from './order-checkout-details';
import common from '../administration/access.module.css';
import styles from './order-checkout.module.css';

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
        const supplied = new URLSearchParams(window.location.search).get('draft');
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
  return <div className={styles.shell}><a className={styles.skip} href="#checkout-main">Langsung ke pengajuan pesanan</a>
    <header className={styles.siteHeader}><Brand context="Kebutuhan kerja & harian" /><nav aria-label="Navigasi belanja"><Link href="/shop">Katalog kemasan</Link><Link href="/ordering/drafts">Draf pesanan</Link><Link href="/identity/account">Akun saya</Link></nav></header>
    <main id="checkout-main" tabIndex={-1} className={`${common.page} ${styles.page}`}><header className={styles.heading}>
      <Link href="/ordering/drafts" className={styles.back}><ArrowLeft size={16} aria-hidden="true" />Kembali ke draf</Link>
      <h1>Pengajuan pesanan</h1><p>Tinjau kemasan, tujuan pengiriman, dan jumlah biaya sebelum mengajukan kebutuhan cabang.</p></header>
    {error !== null && <p className={common.alert} role="alert">{message(error, true)}</p>}
    {loading ? <p className={styles.loading} role="status">Memeriksa akun…</p> : !account || expired ? <section className={common.state}><h2>Periksa sesi dan akses</h2>
      <Link href="/identity/login?returnPath=%2Fordering%2Fcheckout">Masuk kembali</Link><button className={common.secondary} onClick={() => setRevision(value => value + 1)}>Perbarui sesi dan akses</button></section>
      : <CheckoutDesk key={account.actorId + ':' + account.observedAt} account={account} onFailure={failed} />}
  </main><footer className={styles.footer}>Unit Toko · Kebutuhan cabang dan organisasi</footer></div>;
}

function CheckoutDesk({ account, onFailure }: { account: Account; onFailure: (error: unknown) => void }) {
  const { actorId } = account, [drafts] = useState(() => createOrderDraftGateway()), [gateway] = useState(() => createOrderSubmitGateway()), [ledger] = useState(() => new MutationLedger());
  const recovery = useOperationRecovery(actorId), [draft, setDraft] = useState<OrderDraft | null>(null), [review, setReview] = useState<CheckoutReview | null>(null);
  const [confirmed, setConfirmed] = useState<OrderSubmitResult | null>(null), [order, setOrder] = useState<SubmittedOrder | null>(null);
  const [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [error, setError] = useState<{ value: unknown; reading: boolean } | null>(null);
  const alive = useRef(false), pending = useRef(false), recovered = useRef<OrderSubmitResult | null>(null), reviewPanel = useRef<HTMLDivElement>(null);
  const current = () => alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired;
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active || !account.draftId) return; setBusy(true); pending.current = true;
      try { const value = await drafts.read(actorId, account.draftId, controller.signal); if (active) setDraft(value); }
      catch (failure) { if (active) { setError({ value: failure, reading: true }); onFailure(failure); } }
      finally { pending.current = false; if (active) setBusy(false); }
    });
    return () => { active = false; controller.abort(); };
  }, [drafts, actorId, account.draftId, onFailure]);
  useEffect(() => { if (review || confirmed) reviewPanel.current?.focus(); }, [review, confirmed]);
  const locked = busy || recovering || recovery.locked;
  async function prepare() {
    if (!current() || locked || pending.current || !draft || confirmed) return;
    pending.current = true; setBusy(true); setError(null); setReview(null);
    try {
      const saved = await drafts.read(actorId, draft.id); if (!current()) return; setDraft(saved);
      const checked = await gateway.preview(actorId, saved.input); if (current()) setReview(checked);
    } catch (failure) { if (current()) { setError({ value: failure, reading: true }); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) setBusy(false); }
  }
  async function readConfirmed(result: OrderSubmitResult) {
    if (!current()) return; setConfirmed(result); setReview(null); setDraft(null); setOrder(null);
    try { const value = await gateway.read(actorId, result.orderId); if (current()) setOrder(value); }
    catch (failure) { if (current()) { setError({ value: failure, reading: true }); onFailure(failure); } }
  }
  async function submit() {
    if (!current() || locked || pending.current || !review || confirmed) return;
    pending.current = true; setBusy(true); setError(null);
    try {
      if (recovery.journal.list(actorId).length > 0) throw new AccessFailure(400, 'resolve_previous_attempt');
      const result = await submitTrackedRequest(ledger, recovery.journal, actorId, 'ordering.order.submit', 'ordering.order.submit', { actorId, input: review.input, review: review.review },
        key => gateway.submit(actorId, review, key, current));
      await readConfirmed(result);
    } catch (failure) { if (current()) { setReview(null); setError({ value: failure, reading: false }); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); recovery.refresh(); } }
  }
  const shown = order ?? review;
  return <>
    <nav className={styles.progress} aria-label="Tahapan pengajuan pesanan"><ol>
      <li><Link href="/ordering/drafts"><ShoppingBag size={18} aria-hidden="true" /><span>Draf pesanan</span></Link></li>
      <li aria-hidden="true" className={styles.progressArrow}><ArrowRight size={16} /></li>
      <li aria-current={!confirmed ? 'step' : undefined}><span><ClipboardList size={18} aria-hidden="true" />Periksa pesanan</span></li>
      <li aria-hidden="true" className={styles.progressArrow}><ArrowRight size={16} /></li>
      <li aria-current={confirmed ? 'step' : undefined}><span>{confirmed ? <Check size={18} aria-hidden="true" /> : <FileText size={18} aria-hidden="true" />}Tercatat</span></li>
    </ol><Link className={styles.historyLink} href="/ordering/orders">Pesanan saya<ArrowRight size={16} aria-hidden="true" /></Link></nav>
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy} onBusyChange={setRecovering}
      recoveryAdapters={{ 'ordering.order': { lookup: async entry => { const result = await gateway.recover(entry.actorId, entry.key); recovered.current = result?.result ?? null; return result; } } }}
      onFailure={failure => { setError({ value: failure, reading: true }); onFailure(failure); }} onResolved={() => {
        recovery.refresh(); ledger.resetAfterReconciliation(); setError(null);
        const result = recovered.current; recovered.current = null;
        if (result && current()) { setBusy(true); void readConfirmed(result).finally(() => { if (alive.current) setBusy(false); }); }
      }} />
    {recovery.error !== null && <p role="alert" className={common.alert}>{accessMessage(recovery.error)}</p>}
    {error && <p role="alert" className={common.alert}>{message(error.value, error.reading)}</p>}
    {confirmed && <section className={styles.confirmed} ref={reviewPanel} tabIndex={-1} aria-label="Pesanan tercatat"><div className={styles.confirmedHeading}><PackageCheck size={28} strokeWidth={1.5} aria-hidden="true" /><h2>Pesanan {confirmed.orderId} tercatat</h2></div>
      <p>{order?.progress.cancelled ? 'Sisa pesanan dibatalkan. Cadangan barang yang belum dikirim telah dilepas.' : order?.progress.spendingState === 'Rejected' ? 'Belanja cabang ditolak. Cadangan pesanan telah dilepas.' : (order?.progress.spendingState ?? confirmed.spendingState) === 'Pending' ? 'Menunggu persetujuan belanja cabang. Penerimaan oleh toko merupakan langkah terpisah.' : 'Belanja telah disetujui atau tidak memerlukan persetujuan tambahan sesuai aturan pembelian.'}</p>
      {order && <p>{order.progress.acceptedSuborders.length} dari {confirmed.suborderIds.length} kelompok pemenuhan telah diterima toko.</p>}
      {order && order.progress.rejectedSuborders.length > 0 && <p>{order.progress.rejectedSuborders.length} kelompok ditolak toko; cadangan bagian tersebut telah dilepas.</p>}
      <p>{confirmed.suborderIds.length} kelompok pemenuhan tersimpan.{!order?.progress.cancelled && order?.progress.spendingState !== 'Rejected' && order?.progress.rejectedSuborders.length === 0 ? ' Stok pesanan telah dicadangkan.' : ''}</p>
      <button className={common.secondary} disabled={busy || recovering} onClick={() => { setBusy(true); void readConfirmed(confirmed).finally(() => { if (alive.current) setBusy(false); }); }}>Muat kembali pesanan</button></section>}
    {!confirmed && draft && <section className={styles.draft}><div><h2>Draf tersimpan yang akan diajukan</h2><p className={styles.draftReference}>{draft.input.purchaseReference ?? 'Kebutuhan cabang'} · {draft.input.selections.length} jenis kemasan · versi {draft.revision}</p>
      <p className={common.help}>Pengajuan menggunakan isi draf yang telah tersimpan. Pastikan perubahan di halaman draf sudah disimpan.</p>
      {!draft.input.delivery && <p className={styles.missingDelivery}>Lengkapi rencana pengiriman pada draf terlebih dahulu.</p>}</div>
      <div className={styles.draftAction}><button className={common.primary} disabled={locked || !draft.input.delivery || review !== null} onClick={() => void prepare()}>{busy ? 'Memeriksa…' : 'Tinjau pesanan dan biaya'}</button>{!review && <p>Rincian harga dan biaya ditampilkan setelah pemeriksaan.</p>}</div></section>}
    {!confirmed && !draft && !busy && !recovery.locked && <section className={common.state}><h2>Pilih draf untuk melanjutkan</h2><Link href="/ordering/drafts">Buka draf pesanan</Link></section>}
    {shown && <section className={styles.review} ref={review ? reviewPanel : undefined} tabIndex={-1} aria-label="Rincian pesanan"><OrderCheckoutDetails value={shown} reviewing={review !== null}>
      {review && <><p>{review.spendingState === 'Pending' ? 'Pesanan memerlukan persetujuan belanja cabang sebelum diterima toko.' : 'Aturan pembelian yang berlaku tidak memerlukan persetujuan belanja tambahan.'}</p>
        <p className={common.help}>Stok diperiksa dan dicadangkan saat pengajuan berhasil. Pengajuan belum berarti barang diterima toko atau dikirim.</p>
        <div className={styles.submitActions}><button className={common.primary} disabled={locked} onClick={() => void submit()}>Konfirmasi ajukan pesanan</button>
          <button className={common.secondary} disabled={locked} onClick={() => setReview(null)}>Batal pengajuan</button></div></>}
      </OrderCheckoutDetails>
    </section>}
  </>;
}
