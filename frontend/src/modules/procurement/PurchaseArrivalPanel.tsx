'use client';

import {useCallback, useEffect, useRef, useState, type FormEvent} from 'react';
import {RequestNotSentError} from '../../api/client';
import {AccessFailure} from '../administration/workflow';
import {arrivalUtc, createPurchaseArrivalGateway, type PurchaseArrivalPending, type PurchaseArrivalPreview, type PurchaseArrivalView} from './purchase-arrival-client';
import {arrivalJournalPrefix, PurchaseArrivalJournal} from './purchase-arrival-journal';
import styles from '../administration/access.module.css';
import { dateTimeFormatter } from '@/lib/format';

type Line = {skuId: string; definitionId: string; skuCode: string; familyName: string; packageLabel: string};
type Props = {actorId: string; order: {id: string; lines: readonly Line[]} | null; canPrepare: boolean; canApprove: boolean;
  fresh: boolean; isCurrent: () => boolean; disabled: boolean; onLocked: (value: boolean) => void; onDenied: (error: unknown) => void};
const when = (value: string | null) => value === null ? 'Belum ada waktu kedatangan yang dapat digunakan.'
  : dateTimeFormatter.format(new Date(value));
const states = {unrecorded: 'Belum dicatat', pending: 'Menunggu pemeriksaan', approved: 'Disetujui', rejected: 'Ditolak'};
function message(error: unknown) {
  if (error instanceof AccessFailure || error instanceof RequestNotSentError) {
    if ([401, 403].includes(error.status)) return 'Sesi atau akses perlu diperiksa kembali. Data belum dapat ditampilkan.';
    if (error.status === 409) return 'Rincian atau jadwal telah berubah. Muat data terbaru dan periksa kembali.';
    if (error.status === 400) return 'Periksa tanggal, referensi konfirmasi dan alasan yang diisi.';
  }
  return 'Hasil belum dapat dipastikan. Jika ada referensi tersimpan, pulihkan hasil sebelum melakukan perubahan lain.';
}

export function PurchaseArrivalPanel({actorId, order, canPrepare, canApprove, fresh, isCurrent, disabled, onLocked, onDenied}: Props) {
  const [gateway] = useState(createPurchaseArrivalGateway), alive = useRef(false), active = useRef(false), controller = useRef<AbortController | null>(null);
  const [journal] = useState(() => new PurchaseArrivalJournal(() => localStorage));
  const [skuId, setSkuId] = useState(''), [value, setValue] = useState<PurchaseArrivalView | null>(null), [busy, setBusy] = useState(false);
  const [pending, setPending] = useState<PurchaseArrivalPending | null>(null), [storageBlocked, setStorageBlocked] = useState(false);
  const [date, setDate] = useState(''), [unknown, setUnknown] = useState(false), [reference, setReference] = useState(''), [reason, setReason] = useState('');
  const [review, setReview] = useState<{shown: PurchaseArrivalPreview; reason: string} | null>(null), [confirmed, setConfirmed] = useState(false);
  const [error, setError] = useState<unknown>(null), [notice, setNotice] = useState('');
  const storagePrefix = arrivalJournalPrefix(actorId);
  const clearForm = useCallback(() => {setValue(null); setDate(''); setUnknown(false); setReference(''); setReason(''); setReview(null); setConfirmed(false);}, []);
  useEffect(() => {alive.current = true; return () => {alive.current = false; controller.current?.abort();};}, []);
  useEffect(() => {
    const read = () => {if (!alive.current) return; try {
      setPending(journal.list(actorId)[0] ?? null); setStorageBlocked(false);
    } catch (failure) {setStorageBlocked(true); setError(failure);} };
    const changed = (event: StorageEvent) => {
      if (event.key?.startsWith(storagePrefix) || event.key === null) {
        read();
        if (event.newValue === null) {clearForm(); setNotice('Referensi berubah di tab lain. Muat jadwal terbaru sebelum melanjutkan.');}
      }
    };
    void Promise.resolve().then(read); window.addEventListener('storage', changed);
    return () => window.removeEventListener('storage', changed);
  }, [actorId, clearForm, journal, storagePrefix]);
  useEffect(() => {void Promise.resolve().then(() => {if (alive.current) {setSkuId(''); clearForm();}});}, [order?.id, clearForm]);
  useEffect(() => {onLocked(busy || pending !== null || storageBlocked || review !== null); return () => onLocked(false);}, [busy, pending, storageBlocked, review, onLocked]);
  useEffect(() => {if (!busy && !pending) return; const warn = (event: BeforeUnloadEvent) => {event.preventDefault(); event.returnValue = '';};
    window.addEventListener('beforeunload', warn); return () => window.removeEventListener('beforeunload', warn);}, [busy, pending]);
  const locked = disabled || busy || !isCurrent(), commandLocked = locked || pending !== null || storageBlocked;
  function store(saved: PurchaseArrivalPending) {
    const existing = journal.list(actorId)[0];
    if (existing) {setPending(existing); throw new RequestNotSentError(503, 'purchase_arrival_pending_exists');}
    journal.remember(saved); setPending(saved);
  }
  function clearReference(saved: PurchaseArrivalPending) {
    journal.confirmed(saved); setPending(journal.list(actorId)[0] ?? null);
  }
  async function work(action: (context: {actorId: string; isCurrent: () => boolean; signal: AbortSignal}) => Promise<void>) {
    if (active.current || locked) return; active.current = true; setBusy(true); setError(null); setNotice('');
    const request = new AbortController(); controller.current = request;
    const context = {actorId, isCurrent: () => alive.current && isCurrent() && controller.current === request, signal: request.signal};
    try {await action(context);} catch (failure) {if (alive.current) {setError(failure); onDenied(failure);}}
    finally {active.current = false; if (alive.current) setBusy(false);}
  }
  async function load() {if (!order || !skuId || commandLocked) return; const selected = order.lines.find(line => line.skuId === skuId); if (!selected) return;
    await work(async context => {const current = await gateway.read(order.id, skuId, context); if (current.definitionId !== selected.definitionId) throw new AccessFailure(502, 'purchase_arrival_response_invalid');
      if (context.isCurrent()) {clearForm(); setValue(current);}});
  }
  async function prepare(event: FormEvent) {event.preventDefault(); if (!value || !canPrepare || !fresh || commandLocked) return;
    await work(async context => {const shown = await gateway.preview({orderId: value.orderId, orderVersion: value.orderVersion, approvalId: value.approvalId,
      skuId: value.skuId, definitionId: value.definitionId, expectedRevision: value.revision, expectedAt: unknown ? null : arrivalUtc(date), confirmationReference: reference}, context);
      if (context.isCurrent()) {setReview({shown, reason: reason.trim()}); setConfirmed(false);}});
  }
  async function decide(choice: 'approve' | 'reject') {if (!value || !canApprove || !fresh || commandLocked || !reason.trim()) return;
    await work(async context => {const shown = await gateway.decision(value, choice, context); if (context.isCurrent()) {setReview({shown, reason: reason.trim()}); setConfirmed(false);}});
  }
  async function save() {if (!review || !confirmed || !fresh || commandLocked) return;
    await work(async context => {
      const saved: PurchaseArrivalPending = {actorId, orderId: review.shown.input.orderId, skuId: review.shown.input.skuId,
        key: 'arrival.' + (review.shown.decision ?? 'propose') + '.' + crypto.randomUUID(), action: review.shown.decision ?? 'propose'};
      try {store(saved);} catch {throw new RequestNotSentError(503, 'purchase_arrival_storage_unavailable');}
      try {
        await gateway.submit(review.shown, review.reason, saved.key, context);
        if (context.isCurrent()) {clearReference(saved); setReview(null); setConfirmed(false); setValue(await gateway.read(saved.orderId, saved.skuId, context)); setNotice('Jadwal kedatangan tersimpan.');}
      } catch (failure) {
        if (failure instanceof RequestNotSentError || failure instanceof AccessFailure && [400, 401, 403, 409, 413].includes(failure.status)) clearReference(saved);
        throw failure;
      }
    });
  }
  async function recover() {if (!pending || locked) return; const saved = pending;
    await work(async context => {const found = await gateway.recover(saved, context); if (!context.isCurrent()) return;
      if (found.status === 'confirmed') {clearReference(saved); setReview(null); setConfirmed(false); setNotice('Hasil asli sudah ditemukan untuk pesanan ' + saved.orderId + '.');
        if (order?.id === saved.orderId && skuId === saved.skuId) setValue(await gateway.read(saved.orderId, saved.skuId, context));}
      else setNotice('Hasil belum terkonfirmasi. Referensi tetap tersimpan; periksa kembali tanpa mengirim ulang.');});
  }
  return <section className={styles.form} aria-label="Jadwal kedatangan pemasok"><h2>Jadwal kedatangan pemasok</h2>
    <p>Catat waktu dari konfirmasi pemasok untuk kemasan pada pesanan yang sudah disetujui. Pemeriksa independen meninjau setiap perubahan. Jadwal tidak menambah stok atau mencatat pembayaran.</p>
    {error !== null && <p role="alert" className={styles.alert}>{message(error)}</p>}{notice && <p role="status">{notice}</p>}{busy && <p role="status">Memeriksa jadwal kedatangan…</p>}
    {pending && <section className={styles.review}><p>Referensi hasil tersimpan untuk pesanan {pending.orderId}. Pulihkan sebelum membuat perubahan lain.</p><button type="button" className={styles.secondary} disabled={locked} onClick={() => void recover()}>Pulihkan hasil jadwal</button></section>}
    {storageBlocked && <p role="alert">Referensi lokal tidak dapat dibaca. Hubungi operator; perubahan baru ditahan agar hasil yang belum diketahui tidak terulang.</p>}
    {!order ? <p>Pilih pesanan pembelian yang disetujui untuk melihat jadwal per barang.</p> : <><label>Barang dan kemasan untuk jadwal<select value={skuId} disabled={commandLocked || review !== null} onChange={event => {setSkuId(event.target.value); clearForm();}}><option value="">Pilih barang</option>{order.lines.map(line => <option key={line.skuId} value={line.skuId}>{line.familyName} · {line.skuCode} · {line.packageLabel}</option>)}</select></label>
      <button type="button" className={styles.secondary} disabled={commandLocked || !skuId || review !== null} onClick={() => void load()}>Muat jadwal kedatangan</button></>}
    {value && <><h3>{states[value.state]}</h3><p>Waktu yang disetujui: {when(value.expectedAt)}</p>{value.state === 'pending' && <p>Usulan kedatangan: {when(value.proposedAt)}</p>}
      {value.confirmationReference && <p>Referensi konfirmasi: {value.confirmationReference}</p>}
      {!review && <><label>Alasan pencatatan atau keputusan<textarea rows={3} maxLength={1000} disabled={commandLocked} value={reason} onChange={event => setReason(event.target.value)} /></label>
        {canPrepare && <form onSubmit={event => void prepare(event)}><fieldset className={styles.fields} disabled={commandLocked || !fresh}>
          <label className={styles.checkbox}><input type="checkbox" checked={unknown} onChange={event => setUnknown(event.target.checked)} />Pemasok belum dapat memastikan waktu; tarik jadwal sebelumnya.</label>
          {!unknown && <label>Perkiraan kedatangan (WIB)<input required type="datetime-local" value={date} onChange={event => setDate(event.target.value)} /></label>}
          <label>Referensi konfirmasi pemasok<input required maxLength={256} value={reference} onChange={event => setReference(event.target.value)} /></label>
          <button className={styles.secondary} disabled={!reason.trim()}>Periksa usulan jadwal</button></fieldset></form>}
        {canApprove && value.state === 'pending' && <div className={styles.actions}><button type="button" className={styles.primary} disabled={commandLocked || !fresh || !reason.trim()} onClick={() => void decide('approve')}>Tinjau persetujuan jadwal</button><button type="button" className={styles.secondary} disabled={commandLocked || !fresh || !reason.trim()} onClick={() => void decide('reject')}>Tinjau penolakan jadwal</button></div>}
      </>}
      {!fresh && <p>Verifikasi MFA terbaru diperlukan sebelum mengubah atau memutuskan jadwal.</p>}
    </>}
    {review && <section className={styles.review} aria-label="Konfirmasi jadwal kedatangan"><h3>Periksa jadwal dan sumber konfirmasi</h3>
      <p>{when(review.shown.input.expectedAt)}</p><p>Referensi pemasok: {review.shown.input.confirmationReference}</p><p>Alasan: {review.reason}</p>
      <p>{review.shown.decision === 'approve' ? 'Keputusan: setujui jadwal.' : review.shown.decision === 'reject' ? 'Keputusan: tolak jadwal.' : 'Usulan akan diperiksa oleh orang lain yang berwenang.'}</p>
      <label className={styles.checkbox}><input type="checkbox" checked={confirmed} disabled={commandLocked} onChange={event => setConfirmed(event.target.checked)} />Saya sudah memeriksa barang, waktu dan konfirmasi pemasok.</label>
      <div className={styles.actions}><button type="button" className={styles.primary} disabled={commandLocked || !fresh || !confirmed} onClick={() => void save()}>Simpan jadwal yang ditinjau</button><button type="button" className={styles.secondary} disabled={commandLocked} onClick={() => {setReview(null); setConfirmed(false);}}>Kembali</button></div>
    </section>}
  </section>;
}
