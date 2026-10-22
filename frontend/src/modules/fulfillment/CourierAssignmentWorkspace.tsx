'use client';

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
import local from './fulfillment.module.css';
import {formatDateTime} from '@/lib/format';
import { SessionAccessState } from "@/components/workspace-kit/session-access-state";
import { orderNumber } from '@/lib/document-numbers';
import { Button } from '@/components/workspace-kit/button';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { StateView } from '@/components/workspace-kit/state-view';
import { Section } from '@/components/workspace-kit/section';
import { DataList } from '@/components/workspace-kit/data-list';
import { Badge } from '@/components/workspace-kit/badge';

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
  return <main className={common.page}><PageHeader title="Penugasan kurir" description="Tinjau kurir yang bertugas dan catat penyerahan tugas pengiriman." />
    <div className="ui-stack">
    {error !== null && account !== null && <StateView kind="error" title={message(error)} />}
    {loading ? <StateView kind="loading" title="Memeriksa akun…" /> : !account || expired ? <SessionAccessState kind={account ? "expired" : undefined} error={error} returnPath="/fulfillment/courier-assignments" onRetry={() => setReload(value => value + 1)} /> :
      <AssignmentDesk key={account.actorId + ':' + account.observedAt} account={account} onDenied={denied} />}</div>
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
  // The row button that started a read shows it is busy until the record arrives (§8).
  const [opening, setOpening] = useState<string | null>(null);
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
    {error !== null && page.items.length > 0 && <StateView kind="error" title={message(error)} />}
    {storageError && <StateView kind="error" title="Referensi pemulihan belum dapat dibaca. Perubahan baru menunggu penyimpanan perangkat tersedia. Jangan hapus referensi yang belum terkonfirmasi." />}
    {offline && <StateView kind="offline" title="Koneksi terputus. Muat kembali penugasan setelah tersambung." />}{notice && <p role="status">{notice}</p>}
    {refs.length > 0 && <Section title="Periksa permintaan yang sudah dikirim" label="Pemulihan penugasan" description="Referensi ini tetap tersedia setelah tab atau browser dibuka kembali.">
      <DataList label="Pemulihan penugasan" items={refs} getKey={ref => ref.key} renderTitle={ref => <>Kiriman {ref.shipmentId}</>} renderMeta={ref => <>permintaan {ref.key}</>}
        renderActions={ref => <Button variant="secondary" disabled={busy || offline} onClick={() => void recover(ref)}>Periksa hasil penugasan {ref.shipmentId}</Button>} /></Section>}
    {result && <Section title="Penugasan tersimpan" label="Hasil penugasan"><p>Kiriman {result.shipmentId} · Kurir {result.courierName} · {time(result.recordedAt)}.</p>
      <p>Ini hasil permintaan yang tercatat. Muat penugasan untuk melihat siapa yang bertugas saat ini.</p></Section>}
    <Section title="Kiriman dalam tugas Anda" label="Daftar penugasan" busy={busy}
      actions={<Button variant="secondary" disabled={locked} onClick={() => {invalidate(); setBefore(null); setReload(value => value + 1);}}>Muat penugasan terbaru</Button>}>
      <DataList label="Daftar penugasan" items={page.items} error={error !== null && page.items.length === 0 ? { title: message(error), message: null, onRetry: () => { invalidate(); setBefore(null); setReload(value => value + 1); } } : undefined} getKey={row => row.shipmentId} loading={busy}
        empty={error === null ? { title: "Belum ada kiriman dalam akses penugasan Anda." } : undefined}
        renderTitle={row => <>Kiriman {number(row)}</>} renderBadge={row => <Badge tone={row.received ? "success" : "neutral"} label={row.received ? 'Penerimaan sudah tercatat.' : 'Penerimaan belum tercatat.'} />}
        renderMeta={row => <>Pesanan {orderNumber(row.orderId)} · bagian toko {row.suborderId} · Kurir yang bertugas: {row.currentAssignment.courierName}</>}
        renderActions={row => <Button variant="secondary" disabled={locked} busy={opening === row.shipmentId} onClick={() => { setOpening(row.shipmentId); void select(row.shipmentId).finally(() => setOpening(null)); }}>Tinjau penugasan {number(row)}</Button>} />
      {page.hasMore && <div className="ui-actions"><Button variant="secondary" disabled={locked} onClick={() => {invalidate(); setBefore(page.items.at(-1)!.shipmentId);}}>Penugasan lebih lama</Button></div>}
    </Section>
    {task && <section className={"ui-section " + local.detail} aria-label="Rincian penugasan"><h2>Penugasan kiriman {number(task)}</h2>
      <p>Kurir saat keberangkatan: {task.departureCourierName}</p><p>Kurir yang bertugas: <strong>{task.currentAssignment.courierName}</strong> · sejak {time(task.currentAssignment.effectiveAt)}.</p>
      {task.history.length > 0 && <details><summary>Riwayat penyerahan tugas</summary><ol>{task.history.map(event => <li key={event.id}>{time(event.recordedAt)} · {event.courierName}<p>{event.reason}</p></li>)}</ol></details>}
      {!task.canHandover ? <p>{task.received ? 'Penerimaan sudah tercatat; tugas kurir tidak dapat dialihkan.' : 'Tugas belum dapat dialihkan. Muat ulang untuk memeriksa keadaan terkini.'}</p> : preview ? <>
        <h3 ref={reviewHeading} tabIndex={-1}>Konfirmasi penyerahan tugas</h3><p>Dari {preview.task.currentAssignment.courierName} kepada <strong>{preview.courierName}</strong>.</p>
        <p className={local.address}>{reason.trim()}</p><p>QR penerimaan sebelumnya akan perlu diperbarui oleh kurir yang bertugas.</p>
        <div className={common.actions}><Button variant="primary" disabled={locked} onClick={() => void handover()}>Konfirmasi penugasan kurir</Button>
          <Button variant="secondary" disabled={locked} onClick={() => setPreview(null)}>Kembali ke pilihan kurir</Button></div></> :
        <form onSubmit={event => {event.preventDefault(); void review();}}><fieldset disabled={locked}><legend>Serahkan tugas kepada kurir</legend>
          <label htmlFor="handover-courier">Kurir penerima tugas</label><select id="handover-courier" required value={courierId} onChange={event => {setCourierId(event.target.value); setPreview(null);}}>
            <option value="">Pilih kurir</option>{couriers.filter(row => row.userId !== task.currentAssignment.courierId).map(row => <option key={row.userId} value={row.userId}>{row.displayName}</option>)}</select>
          {moreCouriers && <Button variant="secondary" onClick={() => void loadCouriers()}>Tampilkan kurir lainnya</Button>}
          <label htmlFor="handover-reason">Catatan penyerahan tugas</label><textarea id="handover-reason" required maxLength={2000} rows={3} value={reason} onChange={event => {setReason(event.target.value); setPreview(null);}} />
          <Button type="submit" variant="primary" disabled={!courierId || !reason.trim()}>Tinjau penyerahan tugas</Button></fieldset></form>}
      <Button variant="secondary" disabled={locked} onClick={() => void select(task.shipmentId)}>Muat ulang penugasan</Button>
    </section>}
  </>;
}
