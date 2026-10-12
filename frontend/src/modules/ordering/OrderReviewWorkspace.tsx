'use client';

import Link from 'next/link';
import { useCallback, useEffect, useRef, useState } from 'react';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { AdministrationNavigation } from '../administration/AdministrationNavigation';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import { submitTrackedRequest } from '../administration/operation-journal';
import { createOrderReviewGateway, orderDecisionOperation, orderDecisionSelection, type OrderReview, type OrderDecisionResult, type ReviewStage, type ReviewAction } from './order-review-client';
import { orderAmount } from './order-quote-client';
import common from '../administration/access.module.css';
import styles from './order-draft.module.css';

type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
const factorHref = '/identity/account?operation=order.spend.approve&returnPath=%2Fordering%2Freviews';
function decisionNotice(result: Pick<OrderDecisionResult, 'stage' | 'action'>, recovered = false) {
  if (result.action === 'CancelRemainder') return recovered ? 'Pembatalan ditemukan dan tercatat. Cadangan sisa pesanan telah dilepas.' : 'Sisa pesanan dibatalkan. Cadangan barang yang belum dikirim telah dilepas.';
  if (result.action === 'Reject') return result.stage === 'Spending' ? 'Penolakan belanja tercatat. Cadangan pesanan telah dilepas.' : 'Penolakan toko tercatat. Cadangan bagian toko ini telah dilepas.';
  return result.stage === 'Spending' ? recovered ? 'Persetujuan belanja ditemukan dan tercatat.' : 'Persetujuan belanja tercatat. Toko dapat meninjau bagian pesanannya.' : recovered ? 'Penerimaan toko ditemukan dan tercatat.' : 'Penerimaan toko tercatat. Pesanan menunggu proses pemenuhan.';
}
function statusText(item: OrderReview) {
  return item.cancelled ? 'Sisa pesanan dibatalkan' : item.spendingState === 'Rejected' ? 'Belanja cabang ditolak' : item.storeState === 'rejected' ? 'Ditolak toko' :
    item.storeState === 'accepted' ? 'Diterima toko' : item.spendingState === 'Pending' ? 'Menunggu persetujuan belanja' : item.stage === 'Store' ? 'Menunggu penerimaan toko' : 'Belanja disetujui atau tidak memerlukan persetujuan tambahan';
}
function message(error: unknown) {
  if (error instanceof AccessFailure) {
    if (error.code === 'step_up_required') return 'Verifikasi dua langkah terbaru diperlukan untuk persetujuan belanja dan pemeriksaan hasilnya.';
    if (['independent_actor_required', 'independent_person_required'].includes(error.code)) return 'Persetujuan belanja harus dilakukan oleh orang lain yang berwenang, terpisah dari pemesan.';
    if (error.code === 'order_storage_unavailable') return 'Peninjauan pesanan belum diaktifkan. Data pesanan tetap tersimpan.';
    if (error.status === 409) return 'Keadaan pesanan berubah. Muat kembali dan tinjau sebelum mengambil keputusan.';
    if (error.status > 0 && error.status < 500) return accessMessage(error);
  }
  return 'Hasil permintaan belum dapat dipastikan. Untuk keputusan yang telah dikirim, gunakan Periksa hasil sebelum membuat permintaan lain.';
}
export function OrderReviewWorkspace() {
  const [identity] = useState(() => createIdentityGateway()), [access] = useState(() => createAccessGateway());
  const [account, setAccount] = useState<Account | null>(null), [loading, setLoading] = useState(true), [error, setError] = useState<unknown>(null), [revision, setRevision] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setAccount(null); setError(null); setLoading(true);
      try {
        const session = await identity.session(controller.signal), observedAt = performance.now(); if (!active) return;
        if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
        const current = await access.current(); if (!active) return;
        if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
        if (!current.allowedActions.some(action => ['order.spend.approve', 'order.accept', 'order.cancel'].includes(action))) throw new AccessFailure(403, 'scope_denied');
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
    if (failure instanceof AccessFailure && (failure.status === 401 || failure.status === 403 && failure.code !== 'step_up_required' && !failure.code.startsWith('independent_') || failure.code === 'actor_changed')) {
      setAccount(null); setError(failure);
    }
  }, []);
  const expired = account !== null && sessionTiming(account.session, elapsed).expired;
  return <main className={common.page}><header className={common.header}><div><Link href="/identity/account" className={common.brand}>Unit Toko</Link>
    <h1>Peninjauan pesanan</h1><p>Tinjau belanja, penerimaan toko, atau pembatalan sesuai penugasan Anda.</p></div>
    <nav className={common.actions}><Link href="/identity/account" className={common.secondary}>Akun saya</Link></nav></header>
    {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    {loading ? <p role="status">Memeriksa akun…</p> : !account || expired ? <section className={common.state}><h2>Periksa sesi dan akses</h2>
      <Link href="/identity/login?returnPath=%2Fordering%2Freviews">Masuk kembali</Link><button className={common.secondary} onClick={() => setRevision(value => value + 1)}>Perbarui sesi dan akses</button></section>
      : <ReviewDesk key={account.actorId + ':' + account.observedAt} account={account} elapsed={elapsed} onFailure={failed} onRefresh={() => setRevision(value => value + 1)} />}
  </main>;
}
function ReviewDesk({ account, elapsed, onFailure, onRefresh }: { account: Account; elapsed: number; onFailure: (error: unknown) => void; onRefresh: () => void }) {
  const { actorId, actions } = account, [gateway] = useState(() => createOrderReviewGateway()), [ledger] = useState(() => new MutationLedger());
  const [stage, setStage] = useState<ReviewStage>(() => actions.includes('order.spend.approve') ? 'Spending' : actions.includes('order.accept') ? 'Store' : 'Cancellation');
  const [action, setAction] = useState<ReviewAction>('Approve');
  const [page, setPage] = useState<{ items: OrderReview[]; hasMore: boolean }>({ items: [], hasMore: false }), [before, setBefore] = useState<string | null>(null), [reload, setReload] = useState(0);
  const [selected, setSelected] = useState<OrderReview | null>(null), [staged, setStaged] = useState(false), [reason, setReason] = useState(''), [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [error, setError] = useState<unknown>(null);
  const recovery = useOperationRecovery(actorId), alive = useRef(false), pending = useRef(false), reviewPanel = useRef<HTMLElement>(null), recovered = useRef<OrderDecisionResult | null>(null);
  const current = () => alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired;
  const timing = sessionTiming(account.session, elapsed), locked = busy || recovering || recovery.locked;
  const fresh = stage !== 'Spending' || timing.fresh;
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setBusy(true); setPage({ items: [], hasMore: false }); setSelected(null); setStaged(false); setError(null);
      try { const value = await gateway.list(actorId, stage, before, controller.signal); if (active) setPage(value); }
      catch (failure) { if (active) { setError(failure); onFailure(failure); } }
      finally { if (active) setBusy(false); }
    }); return () => { active = false; controller.abort(); };
  }, [gateway, actorId, stage, before, reload, onFailure]);
  useEffect(() => { if (selected) reviewPanel.current?.focus(); }, [selected, staged]);
  async function select(value: Pick<OrderReview, 'orderId' | 'suborderId' | 'stage'>) {
    if (!current() || pending.current) return; pending.current = true; setBusy(true); setError(null); setSelected(null); setStaged(false); setReason('');
    try { const result = await gateway.read(actorId, value.orderId, value.suborderId, value.stage); if (current()) { setSelected(result); setAction(result.stage === 'Cancellation' ? 'CancelRemainder' : 'Approve'); setPage(previous => ({ ...previous, items: previous.items.map(item => item.orderId === result.orderId && item.suborderId === result.suborderId && item.stage === result.stage ? result : item) })); } }
    catch (failure) { if (current()) { setError(failure); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) setBusy(false); }
  }
  async function confirm() {
    if (!current() || locked || pending.current || !selected || !staged || !fresh) return;
    pending.current = true; setBusy(true); setError(null); setNotice(null); const command = selected, chosen = action, operation = orderDecisionOperation(command.stage, chosen);
    try {
      if (recovery.journal.list(actorId).length > 0) throw new AccessFailure(400, 'resolve_previous_attempt');
      await submitTrackedRequest(ledger, recovery.journal, actorId, operation, operation,
        { actorId, orderId: command.orderId, suborderId: command.suborderId, revision: command.revision, review: command.review, action: chosen, reason: reason.trim() },
        key => gateway.decide(actorId, command, reason.trim(), key, () => current() && (command.stage !== 'Spending' || sessionTiming(account.session, performance.now() - account.observedAt).fresh), chosen));
      if (current()) {
        setStaged(false); const latest = await gateway.read(actorId, command.orderId, command.suborderId, command.stage);
        if (current()) {
          setSelected(latest); setPage(previous => ({ ...previous, items: previous.items.map(item => item.orderId === latest.orderId && item.suborderId === latest.suborderId ? latest : item) }));
          setNotice(decisionNotice({ stage: command.stage, action: chosen }));
        }
      }
    } catch (failure) { if (current()) { setStaged(false); setSelected(null); setError(failure); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); recovery.refresh(); } }
  }
  return <>
    <AdministrationNavigation current="order-reviews" actions={actions} locked={locked} />
    <section className={common.accountSection}><h2>Keputusan sesuai penugasan</h2><div className={common.actions}>
      {actions.includes('order.spend.approve') && <button className={stage === 'Spending' ? common.primary : common.secondary} disabled={locked} onClick={() => { setStage('Spending'); setBefore(null); setNotice(null); }}>Belanja cabang</button>}
      {actions.includes('order.accept') && <button className={stage === 'Store' ? common.primary : common.secondary} disabled={locked} onClick={() => { setStage('Store'); setBefore(null); setNotice(null); }}>Penerimaan toko</button>}
      {actions.includes('order.cancel') && <button className={stage === 'Cancellation' ? common.primary : common.secondary} disabled={locked} onClick={() => { setStage('Cancellation'); setBefore(null); setNotice(null); }}>Pembatalan pesanan</button>}
      <button className={common.secondary} disabled={busy || recovering} onClick={onRefresh}>Perbarui sesi dan akses</button></div>
      {(!fresh || error instanceof AccessFailure && error.code === 'step_up_required') && <p role="alert">Verifikasi terbaru diperlukan. <Link href={factorHref}>Verifikasi dua langkah</Link></p>}
    </section>
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy} onBusyChange={setRecovering}
      recoveryAdapters={{ 'ordering.decision': { lookup: async entry => { const selected = orderDecisionSelection(entry.operation); const result = await gateway.recover(entry.actorId, selected.stage, entry.key, undefined, selected.action); recovered.current = result?.result ?? null; return result; } } }}
      onFailure={failure => { setError(failure); onFailure(failure); }} onResolved={() => {
        recovery.refresh(); ledger.resetAfterReconciliation(); setError(null); const result = recovered.current; recovered.current = null;
        if (result && current()) { setNotice(decisionNotice(result, true)); void select(result); }
      }} />
    {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}{recovery.error !== null && <p role="alert" className={common.alert}>{accessMessage(recovery.error)}</p>}
    {notice && <p role="status" className={common.success}>{notice}</p>}
    <section className={common.accountSection} aria-label="Pesanan sesuai akses" aria-busy={busy}><h2>{stage === 'Store' ? 'Bagian pesanan toko' : stage === 'Spending' ? 'Pesanan cabang' : 'Pesanan untuk ditinjau pembatalannya'}</h2>
      {!busy && page.items.length === 0 && <p>Belum ada pesanan yang dapat ditinjau dalam penugasan ini.</p>}
      <ul className={styles.lines}>{page.items.map(item => <li key={item.suborderId ?? item.orderId}><strong>{item.buyerName}</strong> · Pesanan {item.orderId} · {item.quote.lines.length} kemasan
        <p>{statusText(item)}</p>
        <button className={common.secondary} disabled={locked || !fresh} onClick={() => void select(item)}>Tinjau pesanan {item.orderId}{item.suborderId ? ' / ' + item.suborderId : ''}</button></li>)}</ul>
      <div className={common.actions}><button className={common.secondary} disabled={locked} onClick={() => { setBefore(null); setReload(value => value + 1); }}>Muat pesanan terbaru</button>
        {page.hasMore && <button className={common.secondary} disabled={locked} onClick={() => { const last = page.items.at(-1); if (last) setBefore(last.suborderId ?? last.orderId); }}>Pesanan lebih lama</button>}</div>
    </section>
    {selected && <section ref={reviewPanel} tabIndex={-1} className={styles.review} aria-label="Tinjauan keputusan"><h2>{selected.stage === 'Spending' ? 'Tinjau belanja cabang' : selected.stage === 'Store' ? 'Tinjau penerimaan toko' : 'Tinjau pembatalan sisa pesanan'}</h2>
      <p>{selected.buyerName} · Pesanan {selected.orderId} · versi keputusan {selected.revision}</p>
      <ul>{selected.quote.lines.map(line => <li key={`${line.definition.skuId}:${line.lineRevision}`}>{line.familyName} — {line.packages} {line.definition.packageLabel} · {line.baseUnits} {line.definition.baseUnitCode} · {orderAmount(selected.quote.currency, line.catalogSubtotal)} · {line.lineRevision === '0' ? 'rincian awal' : `persetujuan ${line.lineRevision}`}</li>)}</ul>
      <p className={styles.address}>{selected.input.delivery!.recipientName} · {selected.input.delivery!.phone}{'\n'}{selected.input.delivery!.address}{'\n'}{selected.input.delivery!.city} {selected.input.delivery!.postalCode}</p>
      <p>Subtotal barang: {orderAmount(selected.quote.currency, selected.quote.catalogSubtotal)}</p>
      {selected.total !== null && <><p>Pajak: {orderAmount(selected.quote.currency, selected.tax!)} · Biaya lain: {orderAmount(selected.quote.currency, selected.charges!)}</p><p><strong>Total belanja: {orderAmount(selected.quote.currency, selected.total)}</strong></p></>}
      <p>{selected.stage === 'Spending' ? 'Keputusan harus diberikan oleh orang lain yang berwenang. Persetujuan ini belum merupakan penerimaan toko.' : selected.stage === 'Store' ? 'Keputusan hanya mencakup kemasan toko ini. Keputusan ini belum berarti barang telah dikirim.' : 'Pembatalan melepas cadangan barang yang belum dikirim. Riwayat pesanan dan keputusan sebelumnya tetap tersimpan.'}</p>
      {!selected.canDecide ? <p role="status">{selected.cancelled ? 'Sisa pesanan sudah dibatalkan.' : selected.spendingState === 'Rejected' ? 'Belanja cabang sudah ditolak.' : selected.storeState === 'rejected' ? 'Bagian pesanan ini sudah ditolak toko.' : selected.storeState === 'accepted' ? 'Bagian pesanan ini sudah diterima toko.' : selected.spendingState === 'Pending' ? 'Menunggu persetujuan belanja cabang.' : 'Peninjauan selesai atau tidak ada cadangan yang dapat dibatalkan.'}</p> : <>
        <label>Catatan peninjauan<input value={reason} maxLength={1000} disabled={locked || staged} onChange={event => setReason(event.target.value)} /></label>
        {staged ? <><p>{action === 'Reject' ? 'Penolakan akan melepas cadangan bagian pesanan yang ditinjau. Pastikan alasan sudah benar.' : action === 'CancelRemainder' ? 'Sisa pesanan akan dibatalkan dan cadangannya dilepas. Tindakan ini tidak menghapus riwayat atau mengirim pengembalian dana.' : 'Pastikan kemasan, tujuan dan biaya sudah ditinjau.'}</p>
          <div className={common.actions}><button className={common.primary} disabled={locked || !fresh} onClick={() => void confirm()}>{action === 'Reject' ? 'Konfirmasi penolakan' : action === 'CancelRemainder' ? 'Konfirmasi pembatalan sisa' : selected.stage === 'Spending' ? 'Konfirmasi persetujuan belanja' : 'Konfirmasi penerimaan toko'}</button>
            <button className={common.secondary} disabled={locked} onClick={() => setStaged(false)}>Batal keputusan</button></div></> : <div className={common.actions}>
          <button className={common.primary} disabled={locked || !fresh || reason.trim().length === 0}
            onClick={() => { setAction(selected.stage === 'Cancellation' ? 'CancelRemainder' : 'Approve'); setStaged(true); }}>{selected.stage === 'Spending' ? 'Setujui belanja' : selected.stage === 'Store' ? 'Terima pesanan toko' : 'Batalkan sisa pesanan'}</button>
          {selected.stage !== 'Cancellation' && <button className={common.secondary} disabled={locked || !fresh || reason.trim().length === 0}
            onClick={() => { setAction('Reject'); setStaged(true); }}>{selected.stage === 'Spending' ? 'Tolak belanja' : 'Tolak pesanan toko'}</button>}
        </div>}
      </>}
    </section>}
  </>;
}
