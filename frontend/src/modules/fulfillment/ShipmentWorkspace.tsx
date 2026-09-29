'use client';

import Link from 'next/link';
import { useCallback, useEffect, useRef, useState } from 'react';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure, accessMessage } from '../administration/workflow';
import { AdministrationNavigation } from '../administration/AdministrationNavigation';
import { createShipmentGateway, type Shipment } from './shipment-client';
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
        if (!current.allowedActions.includes('shipment.read')) throw new AccessFailure(403, 'scope_denied');
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
  const [page, setPage] = useState<{ items: Shipment[]; hasMore: boolean }>({ items: [], hasMore: false }), [before, setBefore] = useState<string | null>(null), [revision, setRevision] = useState(0);
  const [selected, setSelected] = useState<Shipment | null>(null), [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null), [offline, setOffline] = useState(false);
  const alive = useRef(false), pending = useRef(false), detail = useRef<HTMLElement>(null), opener = useRef<HTMLButtonElement | null>(null);
  const current = useCallback(() => alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  useEffect(() => { alive.current = true; const connection = () => setOffline(!navigator.onLine); connection();
    window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => { alive.current = false; window.removeEventListener('online', connection); window.removeEventListener('offline', connection); }; }, []);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setBusy(true); setError(null); setSelected(null); setPage({ items: [], hasMore: false });
      try { const next = await gateway.list(account.actorId, before, controller.signal); if (active && current()) setPage(next); }
      catch (failure) { if (active) { setError(failure); onDenied(failure); } } finally { if (active) setBusy(false); }
    }); return () => { active = false; controller.abort(); };
  }, [gateway, account.actorId, before, revision, current, onDenied]);
  useEffect(() => { if (selected) detail.current?.focus(); }, [selected]);
  async function open(id: string, button: HTMLButtonElement) {
    if (!current() || busy || pending.current || offline) return; pending.current = true; opener.current = button; setBusy(true); setSelected(null); setError(null);
    try { const item = await gateway.read(account.actorId, id); if (current()) setSelected(item); }
    catch (failure) { if (current()) { setError(failure); onDenied(failure); } } finally { pending.current = false; if (alive.current) setBusy(false); }
  }
  return <><AdministrationNavigation current="shipments" actions={account.actions} locked={busy} />
    {offline && <p role="status" className={common.alert}>Koneksi terputus. Data yang tampil adalah hasil terakhir; muat kembali setelah tersambung.</p>}
    {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    <section className={common.accountSection} aria-label="Daftar kiriman" aria-busy={busy}><h2>Barang yang diberangkatkan</h2>
      {busy && <p role="status">Memuat kiriman…</p>}{!busy && page.items.length === 0 && <p>Belum ada kiriman dalam akses Anda.</p>}
      <ul className={styles.lines}>{page.items.map(item => <li className={styles.line} key={item.id}><strong>{item.buyerName}</strong><p>Kiriman {number(item)} · Pesanan {item.orderId}</p>
        <p>{time(item.dispatchedAt)} WIB · Kurir {item.courierName}</p><p>{item.lines.map(line => `${line.productName} — ${line.packages} ${line.definition.packageLabel}`).join(', ')}</p>
        <button className={common.secondary} disabled={busy || offline} onClick={event => void open(item.id, event.currentTarget)}>Lihat kiriman {number(item)}</button></li>)}</ul>
      <div className={common.actions}><button className={common.secondary} disabled={busy || offline} onClick={() => { setBefore(null); setRevision(value => value + 1); }}>Muat kiriman terbaru</button>
        {page.hasMore && <button className={common.secondary} disabled={busy || offline} onClick={() => setBefore(page.items.at(-1)!.id)}>Kiriman lebih lama</button>}</div>
    </section>
    {selected && <section ref={detail} tabIndex={-1} className={styles.review} aria-label="Rincian kiriman"><h2>Kiriman {number(selected)}</h2>
      <p>{selected.buyerName} · Pesanan {selected.orderId}</p><p>Diberangkatkan {time(selected.dispatchedAt)} WIB · Kurir {selected.courierName}</p>
      <h3>Tujuan</h3><p className={styles.address}>{selected.delivery.recipientName} · {selected.delivery.phone}{'\n'}{selected.delivery.address}{'\n'}{selected.delivery.city} {selected.delivery.postalCode}</p>
      {selected.delivery.instructions && <p>Petunjuk: {selected.delivery.instructions}</p>}<h3>Kemasan yang diberangkatkan</h3>
      <ul>{selected.lines.map(line => <li key={line.skuId}><strong>{line.productName}</strong> · {line.skuCode}<p>{line.packages} {line.definition.packageLabel} · isi {line.definition.baseUnitsPerPackage} {line.definition.baseUnitCode} per kemasan · total {line.baseUnits} {line.definition.baseUnitCode}</p></li>)}</ul>
      <p>Keberangkatan tercatat. Penerimaan barang oleh cabang belum dicatat.</p>
      <button className={common.secondary} disabled={busy} onClick={() => { setSelected(null); opener.current?.focus(); }}>Tutup rincian</button>
    </section>}
  </>;
}
