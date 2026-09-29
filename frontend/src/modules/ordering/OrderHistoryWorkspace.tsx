'use client';

import Link from 'next/link';
import { useCallback, useEffect, useRef, useState } from 'react';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure, accessMessage } from '../administration/workflow';
import { AdministrationNavigation } from '../administration/AdministrationNavigation';
import { createOrderSubmitGateway, type SubmittedOrder } from './order-submit-client';
import { orderAmount } from './order-quote-client';
import common from '../administration/access.module.css';
import styles from './order-draft.module.css';

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
const date = (value: string) => new Intl.DateTimeFormat('id-ID', { dateStyle: 'medium', timeStyle: 'short', timeZone: 'Asia/Jakarta' }).format(new Date(value)) + ' WIB';
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
  return <main className={common.page}><header className={common.header}><div><Link href="/identity/account" className={common.brand}>Unit Toko</Link>
    <h1>Pesanan saya</h1><p>Lihat pesanan yang telah Anda ajukan, rincian yang disepakati, dan perkembangan peninjauannya.</p></div>
    <Link href="/identity/account" className={common.secondary}>Akun saya</Link></header>
    {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    {loading ? <p role="status">Memeriksa akun…</p> : !account || expired ? <section className={common.state}><h2>Periksa sesi dan akses</h2>
      <Link href="/identity/login?returnPath=%2Fordering%2Forders">Masuk kembali</Link><button className={common.secondary} onClick={() => setRevision(value => value + 1)}>Perbarui sesi dan akses</button></section>
      : <OrderHistoryDesk key={account.actorId + ':' + account.observedAt} account={account} onFailure={failed} />}
  </main>;
}
function OrderHistoryDesk({ account, onFailure }: { account: Account; onFailure: (error: unknown) => void }) {
  const [gateway] = useState(() => createOrderSubmitGateway());
  const [page, setPage] = useState<{ items: SubmittedOrder[]; hasMore: boolean }>({ items: [], hasMore: false });
  const [cursor, setCursor] = useState<string | null>(null), [reload, setReload] = useState(0), [selected, setSelected] = useState<SubmittedOrder | null>(null);
  const [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null), detail = useRef<HTMLElement>(null), alive = useRef(false), pending = useRef(false);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setBusy(true); setError(null); setSelected(null); setPage({ items: [], hasMore: false });
      try { const result = await gateway.list(account.actorId, cursor, controller.signal); if (active) setPage(result); }
      catch (failure) { if (active) { setError(failure); onFailure(failure); } } finally { if (active) setBusy(false); }
    }); return () => { active = false; controller.abort(); };
  }, [gateway, account.actorId, cursor, reload, onFailure]);
  useEffect(() => { if (selected) detail.current?.focus(); }, [selected]);
  async function select(id: string) {
    const current = () => alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired;
    if (!current() || busy || pending.current) return; pending.current = true; setBusy(true); setError(null); setSelected(null);
    try { const result = await gateway.read(account.actorId, id); if (current()) { setSelected(result); setPage(previous => ({ ...previous, items: previous.items.map(order => order.submission.orderId === id ? result : order) })); } }
    catch (failure) { if (current()) { setError(failure); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) setBusy(false); }
  }
  return <>
    <AdministrationNavigation current="orders" actions={account.actions} locked={busy} />
    {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    <section className={common.accountSection} aria-label="Daftar pesanan saya" aria-busy={busy}><h2>Pesanan yang diajukan</h2>
      {busy && <p role="status">Memuat pesanan…</p>}{!busy && page.items.length === 0 && <p>Belum ada pesanan pada halaman ini.</p>}
      <ul className={styles.lines}>{page.items.map(order => <li className={styles.line} key={order.submission.orderId}><strong>{order.buyerName}</strong> · Pesanan {order.submission.orderId}
        <p>{date(order.submission.recordedAt)} · {order.quote.lines.length} jenis kemasan</p><p>{status(order)}</p>
        <p>Total saat pengajuan: <strong>{orderAmount(order.quote.currency, order.total)}</strong></p>
        <button className={common.secondary} disabled={busy} onClick={() => void select(order.submission.orderId)}>Lihat pesanan {order.submission.orderId}</button></li>)}</ul>
      <div className={common.actions}><button className={common.secondary} disabled={busy} onClick={() => { setCursor(null); setReload(value => value + 1); }}>Muat pesanan terbaru</button>
        {page.hasMore && <button className={common.secondary} disabled={busy} onClick={() => setCursor(page.items.at(-1)!.submission.orderId)}>Pesanan lebih lama</button>}
        {account.actions.includes('order.submit') && <Link href="/ordering/drafts" className={common.secondary}>Draf pesanan</Link>}</div>
    </section>
    {selected && <section ref={detail} tabIndex={-1} className={styles.review} aria-label="Rincian pesanan"><h2>Pesanan {selected.submission.orderId}</h2>
      <p>{selected.buyerName} · {date(selected.submission.recordedAt)}</p><p role="status">{status(selected)}</p>
      {selected.input.purchaseReference && <p>Referensi pembelian: {selected.input.purchaseReference}</p>}
      <h3>Kemasan saat pengajuan</h3><ul>{selected.quote.lines.map(line => <li key={line.definition.skuId}><strong>{line.familyName} — {line.definition.packageLabel}</strong>
        <p>{line.packages} kemasan · {line.definition.baseUnitsPerPackage} {line.definition.baseUnitCode} per kemasan · total {line.baseUnits} {line.definition.baseUnitCode}</p>
        <p>{orderAmount(selected.quote.currency, line.price.unitPrice)} per kemasan · subtotal {orderAmount(selected.quote.currency, line.catalogSubtotal)}</p></li>)}</ul>
      <h3>Tujuan pengiriman</h3><p className={styles.address}>{selected.input.delivery!.recipientName} · {selected.input.delivery!.phone}{'\n'}{selected.input.delivery!.address}{'\n'}{selected.input.delivery!.city} {selected.input.delivery!.postalCode}</p>
      {selected.input.delivery!.instructions && <p>{selected.input.delivery!.instructions}</p>}
      <h3>Biaya saat pengajuan</h3><p>Subtotal barang: {orderAmount(selected.quote.currency, selected.quote.catalogSubtotal)}</p>
      <p>Pajak: {orderAmount(selected.quote.currency, selected.tax)} · Biaya lain: {orderAmount(selected.quote.currency, selected.charges)}</p><p><strong>Total: {orderAmount(selected.quote.currency, selected.total)}</strong></p>
      <p>Bagian toko: {selected.progress.acceptedSuborders.length} diterima, {selected.progress.rejectedSuborders.length} ditolak, dari {selected.submission.suborderIds.length} bagian.</p>
      {(selected.progress.cancelled || selected.progress.spendingState === 'Rejected' || selected.progress.rejectedSuborders.length > 0) && <p>Angka di atas menyimpan biaya pengajuan awal. Penolakan atau pembatalan bukan bukti pembayaran maupun pengembalian dana.</p>}
      <button className={common.secondary} disabled={busy} onClick={() => void select(selected.submission.orderId)}>Perbarui perkembangan</button>
    </section>}
  </>;
}
