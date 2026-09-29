'use client';

import Link from 'next/link';
import { useCallback, useEffect, useRef, useState } from 'react';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure, accessMessage } from '../administration/workflow';
import { AdministrationNavigation } from '../administration/AdministrationNavigation';
import { createShipmentGateway, type Shipment } from './shipment-client';
import { ShipmentDocumentPanel } from './ShipmentDocumentPanel';
import { ShipmentReceiptPanel } from './ShipmentReceiptPanel';
import { DeliveryAttemptPanel, visitTime } from './DeliveryAttemptPanel';
import { createDeliveryAttemptGateway, deliveryOutcomes, type DeliveryAttempt } from './delivery-attempt-client';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import common from '../administration/access.module.css';
import styles from '../ordering/order-draft.module.css';

type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
const time = (value: string) => new Intl.DateTimeFormat('id-ID', { dateStyle: 'medium', timeStyle: 'short', timeZone: 'Asia/Jakarta' }).format(new Date(value));
const number = (item: Shipment) => `${item.numberEntity}/${item.numberChannel}/${item.numberYear}/${item.number}`;
function message(error: unknown) {
  if (error instanceof AccessFailure && error.status >= 400 && error.status < 500) return accessMessage(error);
  if (error instanceof IdentityFailure) return 'Sesi perlu diperiksa kembali. Masuk menggunakan akun yang memiliki penugasan kiriman.';
  return 'Data kiriman belum dapat dimuat. Periksa koneksi lalu muat kembali.';
}
export function ShipmentWorkspace() {
  const [identity] = useState(() => createIdentityGateway()), [access] = useState(() => createAccessGateway());
  const [account, setAccount] = useState<Account | null>(null), [loading, setLoading] = useState(true), [error, setError] = useState<unknown>(null), [revision, setRevision] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setLoading(true); setAccount(null); setError(null);
      try {
        const session = await identity.session(controller.signal), observedAt = performance.now(); if (!active) return;
        if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
        const current = await access.current(); if (!active) return;
        if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
        if (!current.allowedActions.some(action => ['shipment.read', 'delivery.attempt'].includes(action))) throw new AccessFailure(403, 'scope_denied');
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
  const denied = useCallback((failure: unknown) => {
    if (failure instanceof AccessFailure && (failure.status === 401 || failure.status === 403 || failure.code === 'actor_changed')) { setAccount(null); setError(failure); }
  }, []);
  const expired = account !== null && sessionTiming(account.session, elapsed).expired;
  return <main className={common.page}><header className={common.header}><div><Link href="/identity/account" className={common.brand}>Unit Toko</Link>
    <h1>Kiriman</h1><p>Periksa kemasan yang diberangkatkan, tujuan dan kurir dalam akses Anda.</p></div><Link href="/identity/account" className={common.secondary}>Akun saya</Link></header>
    {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    {loading ? <p role="status">Memeriksa akun…</p> : !account || expired ? <section className={common.state}><h2>Periksa sesi dan akses</h2>
      <Link href="/identity/login?returnPath=%2Ffulfillment%2Fshipments">Masuk kembali</Link><button className={common.secondary} onClick={() => setRevision(value => value + 1)}>Perbarui sesi dan akses</button></section>
      : <ShipmentList key={account.actorId + ':' + account.observedAt} account={account} onDenied={denied} />}
  </main>;
}
function ShipmentList({ account, onDenied }: { account: Account; onDenied: (failure: unknown) => void }) {
  const [gateway] = useState(() => createShipmentGateway());
  const [attempts] = useState(createDeliveryAttemptGateway), recovery = useOperationRecovery(account.actorId);
  const [recovering, setRecovering] = useState(false), [childBusy, setChildBusy] = useState(false), [mode, setMode] = useState<'receipt' | 'attempt'>('receipt');
  const [recoveredAttempt, setRecoveredAttempt] = useState<DeliveryAttempt | null>(null), recovered = useRef<DeliveryAttempt | null>(null);
  const canRead = account.actions.includes('shipment.read');
  const [page, setPage] = useState<{ items: Shipment[]; hasMore: boolean }>({ items: [], hasMore: false }), [before, setBefore] = useState<string | null>(null), [revision, setRevision] = useState(0);
  const [selected, setSelected] = useState<Shipment | null>(null), [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null), [offline, setOffline] = useState(false);
  const [documentLoadedFor, setDocumentLoadedFor] = useState<string | null>(null);
  const documentLoaded = useCallback(() => setDocumentLoadedFor(selected?.id ?? null), [selected?.id]);
  const alive = useRef(false), pending = useRef(false), detail = useRef<HTMLElement>(null), opener = useRef<HTMLButtonElement | null>(null);
  const current = useCallback(() => alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  useEffect(() => { alive.current = true; const connection = () => setOffline(!navigator.onLine); connection();
    window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => { alive.current = false; window.removeEventListener('online', connection); window.removeEventListener('offline', connection); }; }, []);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setBusy(true); setError(null); setSelected(null); setPage({ items: [], hasMore: false });
      try { if (canRead) { const next = await gateway.list(account.actorId, before, controller.signal); if (active && current()) setPage(next); } }
      catch (failure) { if (active) { setError(failure); onDenied(failure); } } finally { if (active) setBusy(false); }
    }); return () => { active = false; controller.abort(); };
  }, [gateway, account.actorId, before, revision, current, onDenied, canRead]);
  useEffect(() => { if (selected) detail.current?.focus(); }, [selected]);
  async function open(id: string, button: HTMLButtonElement) {
    if (!current() || busy || childBusy || recovering || pending.current || offline || !canRead) return; pending.current = true; opener.current = button; setBusy(true); setSelected(null); setDocumentLoadedFor(null); setMode('receipt'); setError(null);
    try { const item = await gateway.read(account.actorId, id); if (current()) setSelected(item); }
    catch (failure) { if (current()) { setError(failure); onDenied(failure); } } finally { pending.current = false; if (alive.current) setBusy(false); }
  }
  return <><AdministrationNavigation current="shipments" actions={account.actions} locked={busy || childBusy || recovering} />
    <OperationRecovery entries={recovery.entries.filter(entry => entry.operation === 'fulfillment.delivery-attempt.record')} journal={recovery.journal} disabled={busy || childBusy || offline} onBusyChange={setRecovering}
      recoveryAdapters={{ 'fulfillment.delivery-attempt': { lookup: async entry => { const result = await attempts.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); recovered.current = result?.result ?? null; return result; } } }}
      onFailure={failure => { setError(failure); onDenied(failure); }} onResolved={() => { recovery.refresh(); if (current()) { setRecoveredAttempt(recovered.current); setSelected(null); setDocumentLoadedFor(null); setRevision(value => value + 1); } recovered.current = null; }} />
    {recovery.error !== null && <p role="alert">{accessMessage(recovery.error)}</p>}
    {recoveredAttempt && <p role="status">Upaya pengiriman tersimpan untuk kiriman {recoveredAttempt.shipmentId}: {deliveryOutcomes[recoveredAttempt.outcome]} · {visitTime(recoveredAttempt.occurredAt)} WIB.</p>}
    {offline && <p role="status" className={common.alert}>Koneksi terputus. Data yang tampil adalah hasil terakhir; muat kembali setelah tersambung.</p>}
    {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    {!canRead && <p>Akses saat ini memungkinkan pemeriksaan hasil catatan kunjungan Anda. Daftar kiriman memerlukan akses baca kiriman yang masih berlaku.</p>}
    {canRead && <section className={common.accountSection} aria-label="Daftar kiriman" aria-busy={busy}><h2>Barang yang diberangkatkan</h2>
      {busy && <p role="status">Memuat kiriman…</p>}{!busy && page.items.length === 0 && <p>Belum ada kiriman dalam akses Anda.</p>}
      <ul className={styles.lines}>{page.items.map(item => <li className={styles.line} key={item.id}><strong>{item.buyerName}</strong><p>Kiriman {number(item)} · Pesanan {item.orderId}</p>
        <p>{time(item.dispatchedAt)} WIB · Kurir {item.courierName}</p><p>{item.lines.map(line => `${line.productName} — ${line.packages} ${line.definition.packageLabel}`).join(', ')}</p>
        <button className={common.secondary} disabled={busy || childBusy || recovering || offline} onClick={event => void open(item.id, event.currentTarget)}>Lihat kiriman {number(item)}</button></li>)}</ul>
      <div className={common.actions}><button className={common.secondary} disabled={busy || childBusy || recovering || offline} onClick={() => { setBefore(null); setRevision(value => value + 1); }}>Muat kiriman terbaru</button>
        {page.hasMore && <button className={common.secondary} disabled={busy || childBusy || recovering || offline} onClick={() => setBefore(page.items.at(-1)!.id)}>Kiriman lebih lama</button>}</div>
    </section>}
    {selected && <section ref={detail} tabIndex={-1} className={styles.review} aria-label="Rincian kiriman"><h2>Kiriman {number(selected)}</h2>
      <p>{selected.buyerName} · Pesanan {selected.orderId}</p><p>Diberangkatkan {time(selected.dispatchedAt)} WIB · Kurir {selected.courierName}</p>
      <h3>Tujuan</h3><p className={styles.address}>{selected.delivery.recipientName} · {selected.delivery.phone}{'\n'}{selected.delivery.address}{'\n'}{selected.delivery.city} {selected.delivery.postalCode}</p>
      {selected.delivery.instructions && <p>Petunjuk: {selected.delivery.instructions}</p>}<h3>Kemasan yang diberangkatkan</h3>
      <ul>{selected.lines.map(line => <li key={line.skuId}><strong>{line.productName}</strong> · {line.skuCode}<p>{line.packages} {line.definition.packageLabel} · isi {line.definition.baseUnitsPerPackage} {line.definition.baseUnitCode} per kemasan · total {line.baseUnits} {line.definition.baseUnitCode}</p></li>)}</ul>
      <div className={common.actions}><button className={common.secondary} aria-pressed={mode === 'receipt'} disabled={busy || childBusy || recovering} onClick={() => { if (mode !== 'receipt') { setDocumentLoadedFor(null); setMode('receipt'); } }}>Penerimaan dan dokumen</button>
        <button className={common.secondary} aria-pressed={mode === 'attempt'} disabled={busy || childBusy || recovering} onClick={() => { if (mode !== 'attempt') { setDocumentLoadedFor(null); setMode('attempt'); } }}>Upaya pengiriman</button></div>
      {mode === 'attempt' ? <DeliveryAttemptPanel key={'attempt:' + selected.id} actorId={account.actorId} shipmentId={selected.id} canRecord={account.actions.includes('delivery.attempt') && selected.courierId === account.actorId} current={current} onDenied={onDenied} recovery={recovery} onBusyChange={setChildBusy} /> : <>
        <ShipmentDocumentPanel key={selected.id} actorId={account.actorId} shipment={selected} canIssue={account.actions.includes('shipment.prepare')} current={current} onDenied={onDenied} onLoaded={documentLoaded} />
        {documentLoadedFor === selected.id && <ShipmentReceiptPanel key={'receipt:' + selected.id} actorId={account.actorId} shipmentId={selected.id} canIssueProof={account.actions.includes('shipment.prepare')} canChallenge={account.actions.includes('delivery.evidence') && selected.courierId === account.actorId} current={current} onDenied={onDenied} />}
      </>}
      <button className={common.secondary} disabled={busy || childBusy || recovering} onClick={() => { setSelected(null); opener.current?.focus(); }}>Tutup rincian</button>
    </section>}
  </>;
}
