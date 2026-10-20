'use client';

import Link from 'next/link';
import {useCallback, useEffect, useRef, useState} from 'react';
import {RequestNotSentError} from '../../api/client';
import {createIdentityGateway} from '../identity/identity-client';
import {IdentityFailure, sessionTiming, type IdentitySession} from '../identity/identity-workflow';
import {createAccessGateway} from '../administration/access-client';
import {AccessFailure, accessMessage} from '../administration/workflow';
import {AdministrationNavigation} from '../administration/AdministrationNavigation';
import {createCourierAssignmentGateway, type CourierChange, type CourierChoice, type CourierPending, type CourierPreview, type CourierTask} from './courier-assignment-client';
import {CourierAssignmentJournal, courierJournalPrefix} from './courier-assignment-journal';
import common from '../administration/access.module.css';
import styles from '../ordering/order-draft.module.css';
import {formatDateTime} from '@/lib/format';
import { SessionAccessState } from "@/components/workspace-kit/session-access-state";

type Account = {actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number};
const time = (value: string) => formatDateTime(value);
const number = (task: CourierTask) => `${task.numberEntity}/${task.numberChannel}/${task.numberYear}/${task.number}`;
function message(error: unknown) {
  if (error instanceof IdentityFailure) return 'Masuk kembali untuk memeriksa penugasan kurir.';
  if (error instanceof AccessFailure && error.code === 'courier_assignment_storage_unavailable') return 'Referensi pemulihan belum dapat disimpan di perangkat. Perubahan belum dikirim. Aktifkan penyimpanan situs lalu muat kembali.';
  if (error instanceof AccessFailure && error.status === 409) return 'Penugasan atau penerimaan sudah berubah. Muat ulang dan tinjau kurir yang masih tersedia. Periksa hasil dahulu jika permintaan sudah dikirim.';
  if (error instanceof AccessFailure && error.status > 0 && error.status < 500 || error instanceof RequestNotSentError) return accessMessage(error);
  return 'Hasil belum dapat dipastikan. Gunakan Periksa hasil penugasan untuk permintaan yang sudah dikirim.';
}
export function CourierAssignmentWorkspace() {
  const [identity] = useState(createIdentityGateway), [access] = useState(createAccessGateway);
  const [account, setAccount] = useState<Account | null>(null), [loading, setLoading] = useState(true), [error, setError] = useState<unknown>(null);
  const [reload, setReload] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => {
    let active = true; const abort = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setLoading(true); setAccount(null); setError(null);
      try {
        const session = await identity.session(abort.signal), observedAt = performance.now();
        if (!active) return;
        if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
        const permissions = await access.current(); if (!active) return;
        if (permissions.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
        if (!permissions.allowedActions.includes('shipment.assign')) throw new AccessFailure(403, 'scope_denied');
        if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
        setAccount({actorId: permissions.userId, actions: permissions.allowedActions, session, observedAt}); setElapsed(performance.now() - observedAt);
      } catch (failure) {if (active) setError(failure);} finally {if (active) setLoading(false);}
    });
    return () => {active = false; abort.abort();};
  }, [identity, access, reload]);
  useEffect(() => {
    const timer = window.setInterval(() => {if (account) setElapsed(performance.now() - account.observedAt);}, 1000);
    const restored = (event: PageTransitionEvent) => {if (event.persisted) {setAccount(null); setReload(value => value + 1);}};
    window.addEventListener('pageshow', restored); return () => {window.clearInterval(timer); window.removeEventListener('pageshow', restored);};
  }, [account]);
  const denied = useCallback((failure: unknown) => {
    if (failure instanceof AccessFailure && (failure.status === 401 || failure.status === 403 || failure.code === 'actor_changed')) {setAccount(null); setError(failure);}
  }, []);
  const expired = account !== null && sessionTiming(account.session, elapsed).expired;
  return <main className={common.page}><header className={common.header}><div><Link href="/identity/account" className={common.brand}>Unit Toko</Link>
    <h1>Penugasan kurir</h1><p>Tinjau kurir yang bertugas dan catat penyerahan tugas pengiriman.</p></div><Link href="/identity/account" className={common.secondary}>Akun saya</Link></header>
    {error !== null && account !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    {loading ? <p role="status">Memeriksa akun…</p> : !account || expired ? <SessionAccessState error={error} returnPath="/fulfillment/courier-assignments" onRetry={() => setReload(value => value + 1)} /> :
      <AssignmentDesk key={account.actorId + ':' + account.observedAt} account={account} onDenied={denied} />}
  </main>;
}
function AssignmentDesk({account, onDenied}: {account: Account; onDenied: (failure: unknown) => void}) {
  const [gateway] = useState(createCourierAssignmentGateway), [journal] = useState(() => new CourierAssignmentJournal(() => window.localStorage));
  const [page, setPage] = useState<{items: CourierTask[]; hasMore: boolean}>({items: [], hasMore: false}), [before, setBefore] = useState<string | null>(null), [reload, setReload] = useState(0);
  const [task, setTask] = useState<CourierTask | null>(null), [couriers, setCouriers] = useState<CourierChoice[]>([]), [moreCouriers, setMoreCouriers] = useState(false);
  const [courierId, setCourierId] = useState(''), [reason, setReason] = useState(''), [preview, setPreview] = useState<CourierPreview | null>(null);
  const [refs, setRefs] = useState<CourierPending[]>([]), [storageError, setStorageError] = useState(false), [busy, setBusy] = useState(false), [offline, setOffline] = useState(false);
  const [error, setError] = useState<unknown>(null), [notice, setNotice] = useState(''), [result, setResult] = useState<CourierChange | null>(null);
  const alive = useRef(false), pending = useRef(false), generation = useRef(0), reviewHeading = useRef<HTMLHeadingElement>(null);
  const current = useCallback(() => alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const context = () => {const observed = generation.current; return {actorId: account.actorId, isCurrent: () => current() && generation.current === observed};};
  const refreshRefs = useCallback(() => {
    try {setRefs(journal.list(account.actorId)); setStorageError(false);} catch {setStorageError(true);}
  }, [journal, account.actorId]);
  const invalidate = useCallback(() => {generation.current++; setTask(null); setPreview(null); setCouriers([]); setCourierId(''); setReason('');}, []);
  useEffect(() => {
    alive.current = true; const connection = () => setOffline(!navigator.onLine); connection();
    const changed = (event: StorageEvent) => {
      if (event.storageArea !== window.localStorage || event.key !== null && !event.key.startsWith(courierJournalPrefix(account.actorId))) return;
      invalidate(); refreshRefs(); setNotice('Penugasan perlu dimuat ulang setelah aktivitas dari tab lain.'); setReload(value => value + 1);
    };
    void Promise.resolve().then(() => {if (alive.current) refreshRefs();});
    window.addEventListener('storage', changed); window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => {alive.current = false; generation.current++; window.removeEventListener('storage', changed); window.removeEventListener('online', connection); window.removeEventListener('offline', connection);};
  }, [account.actorId, invalidate, refreshRefs]);
  useEffect(() => {
    let active = true; const controller = new AbortController(); const observed = generation.current;
    void Promise.resolve().then(async () => {
      if (!active) return; setBusy(true); setPage({items: [], hasMore: false});
      try {const value = await gateway.list(before, {actorId: account.actorId, isCurrent: () => active && current() && observed === generation.current, signal: controller.signal});
        if (active && current() && observed === generation.current) setPage(value);
      } catch (failure) {if (active && current() && observed === generation.current) {setError(failure); onDenied(failure);}} finally {if (active) setBusy(false);}
    }); return () => {active = false; controller.abort();};
  }, [gateway, account.actorId, before, reload, current, onDenied]);
  useEffect(() => {if (preview) reviewHeading.current?.focus();}, [preview]);
  const locked = busy || offline || storageError || refs.length > 0;
  async function select(shipmentId: string) {
    if (!current() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); setNotice(''); setResult(null); invalidate();
    const ctx = context();
    try {
      const next = await gateway.read(shipmentId, ctx); const choices = next.canHandover ? await gateway.couriers(shipmentId, null, ctx) : {items: [], hasMore: false};
      if (ctx.isCurrent()) {setTask(next); setCouriers(choices.items); setMoreCouriers(choices.hasMore);}
    } catch (failure) {if (current()) {setError(failure); onDenied(failure);}} finally {pending.current = false; if (alive.current) setBusy(false);}
  }
  async function loadCouriers() {
    if (!task || !moreCouriers || !current() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); const ctx = context();
    try {const next = await gateway.couriers(task.shipmentId, couriers.at(-1)!.userId, ctx); if (ctx.isCurrent()) {setCouriers(old => [...old, ...next.items]); setMoreCouriers(next.hasMore);}}
    catch (failure) {if (current()) {setError(failure); onDenied(failure);}} finally {pending.current = false; if (alive.current) setBusy(false);}
  }
  async function review() {
    if (!task || !current() || locked || pending.current || !reason.trim()) return; pending.current = true; setBusy(true); setError(null); setPreview(null); const ctx = context();
    try {const next = await gateway.preview(task, courierId, ctx); if (ctx.isCurrent()) setPreview(next);}
    catch (failure) {if (current()) {setError(failure); onDenied(failure);}} finally {pending.current = false; if (alive.current) setBusy(false);}
  }
  async function handover() {
    if (!preview || !current() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); setNotice(''); const ctx = context(); let ref: CourierPending | null = null;
    try {
      try {
        if (journal.list(account.actorId).length > 0) throw new Error('Unresolved reference');
        ref = {actorId: account.actorId, shipmentId: preview.task.shipmentId, expectedRevision: preview.task.currentAssignment.revision,
          previousCourierId: preview.task.currentAssignment.courierId, courierId: preview.courierId, key: crypto.randomUUID(), createdAt: new Date().toISOString()};
        journal.remember(ref); refreshRefs();
      } catch {throw new AccessFailure(400, 'courier_assignment_storage_unavailable');}
      const saved = await gateway.handover(preview, reason.trim(), ref, ctx);
      journal.confirmed(ref); if (ctx.isCurrent()) {setResult(saved); invalidate(); setBefore(null); setReload(value => value + 1);}
    } catch (failure) {
      if (failure instanceof RequestNotSentError && ref) {try {journal.confirmed(ref);} catch {setStorageError(true);}}
      if (current()) {setPreview(null); setError(failure); onDenied(failure);}
    } finally {pending.current = false; if (alive.current) {setBusy(false); refreshRefs();}}
  }
  async function recover(ref: CourierPending) {
    if (!current() || busy || offline || pending.current) return; pending.current = true; setBusy(true); setError(null); setNotice(''); const ctx = context();
    try {
      const saved = await gateway.recover(ref, ctx);
      if (saved) {journal.confirmed(ref); if (ctx.isCurrent()) {setResult(saved); invalidate(); setBefore(null); setReload(value => value + 1);}}
      else if (ctx.isCurrent()) setNotice('Hasil belum terkonfirmasi. Referensi asli tetap disimpan; periksa kembali sebelum mengirim perubahan baru.');
    } catch (failure) {if (current()) {setError(failure); onDenied(failure);}} finally {pending.current = false; if (alive.current) {setBusy(false); refreshRefs();}}
  }
  return <><AdministrationNavigation current="courier-assignments" actions={account.actions} locked={locked} />
    {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    {storageError && <p role="alert">Referensi pemulihan belum dapat dibaca. Perubahan baru menunggu penyimpanan perangkat tersedia. Jangan hapus referensi yang belum terkonfirmasi.</p>}
    {offline && <p role="status">Koneksi terputus. Muat kembali penugasan setelah tersambung.</p>}{notice && <p role="status">{notice}</p>}
    {refs.length > 0 && <section className={common.accountSection} aria-label="Pemulihan penugasan"><h2>Periksa permintaan yang sudah dikirim</h2>
      <p>Referensi ini tetap tersedia setelah tab atau browser dibuka kembali.</p><ul>{refs.map(ref => <li key={ref.key}>Kiriman {ref.shipmentId} · permintaan {ref.key}
        <button className={common.secondary} disabled={busy || offline} onClick={() => void recover(ref)}>Periksa hasil penugasan {ref.shipmentId}</button></li>)}</ul></section>}
    {result && <section className={common.state} aria-label="Hasil penugasan"><h2>Penugasan tersimpan</h2><p>Kiriman {result.shipmentId} · Kurir {result.courierName} · {time(result.recordedAt)}.</p>
      <p>Ini hasil permintaan yang tercatat. Muat penugasan untuk melihat siapa yang bertugas saat ini.</p></section>}
    <section className={common.accountSection} aria-label="Daftar penugasan" aria-busy={busy}><h2>Kiriman dalam tugas Anda</h2>
      {busy && <p role="status">Memeriksa penugasan…</p>}{!busy && page.items.length === 0 && <p>Belum ada kiriman dalam akses penugasan Anda.</p>}
      <ul className={styles.lines}>{page.items.map(row => <li key={row.shipmentId} className={styles.line}><strong>Kiriman {number(row)}</strong><p>Pesanan {row.orderId} · bagian toko {row.suborderId}</p>
        <p>Kurir yang bertugas: {row.currentAssignment.courierName}</p><p>{row.received ? 'Penerimaan sudah tercatat.' : 'Penerimaan belum tercatat.'}</p>
        <button className={common.secondary} disabled={locked} onClick={() => void select(row.shipmentId)}>Tinjau penugasan {number(row)}</button></li>)}</ul>
      <div className={common.actions}><button className={common.secondary} disabled={locked} onClick={() => {invalidate(); setBefore(null); setReload(value => value + 1);}}>Muat penugasan terbaru</button>
        {page.hasMore && <button className={common.secondary} disabled={locked} onClick={() => {invalidate(); setBefore(page.items.at(-1)!.shipmentId);}}>Penugasan lebih lama</button>}</div>
    </section>
    {task && <section className={styles.review} aria-label="Rincian penugasan"><h2>Penugasan kiriman {number(task)}</h2>
      <p>Kurir saat keberangkatan: {task.departureCourierName}</p><p>Kurir yang bertugas: <strong>{task.currentAssignment.courierName}</strong> · sejak {time(task.currentAssignment.effectiveAt)}.</p>
      {task.history.length > 0 && <details><summary>Riwayat penyerahan tugas</summary><ol>{task.history.map(event => <li key={event.id}>{time(event.recordedAt)} · {event.courierName}<p>{event.reason}</p></li>)}</ol></details>}
      {!task.canHandover ? <p>{task.received ? 'Penerimaan sudah tercatat; tugas kurir tidak dapat dialihkan.' : 'Tugas belum dapat dialihkan. Muat ulang untuk memeriksa keadaan terkini.'}</p> : preview ? <>
        <h3 ref={reviewHeading} tabIndex={-1}>Konfirmasi penyerahan tugas</h3><p>Dari {preview.task.currentAssignment.courierName} kepada <strong>{preview.courierName}</strong>.</p>
        <p className={styles.address}>{reason.trim()}</p><p>QR penerimaan sebelumnya akan perlu diperbarui oleh kurir yang bertugas.</p>
        <div className={common.actions}><button className={common.primary} disabled={locked} onClick={() => void handover()}>Konfirmasi penugasan kurir</button>
          <button className={common.secondary} disabled={locked} onClick={() => setPreview(null)}>Kembali ke pilihan kurir</button></div></> :
        <form onSubmit={event => {event.preventDefault(); void review();}}><fieldset disabled={locked}><legend>Serahkan tugas kepada kurir</legend>
          <label htmlFor="handover-courier">Kurir penerima tugas</label><select id="handover-courier" required value={courierId} onChange={event => {setCourierId(event.target.value); setPreview(null);}}>
            <option value="">Pilih kurir</option>{couriers.filter(row => row.userId !== task.currentAssignment.courierId).map(row => <option key={row.userId} value={row.userId}>{row.displayName}</option>)}</select>
          {moreCouriers && <button type="button" className={common.secondary} onClick={() => void loadCouriers()}>Tampilkan kurir lainnya</button>}
          <label htmlFor="handover-reason">Catatan penyerahan tugas</label><textarea id="handover-reason" required maxLength={2000} rows={3} value={reason} onChange={event => {setReason(event.target.value); setPreview(null);}} />
          <button className={common.primary} disabled={!courierId || !reason.trim()}>Tinjau penyerahan tugas</button></fieldset></form>}
      <button className={common.secondary} disabled={locked} onClick={() => void select(task.shipmentId)}>Muat ulang penugasan</button>
    </section>}
  </>;
}
