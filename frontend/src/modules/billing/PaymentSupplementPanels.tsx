'use client';

import {useLayoutEffect, useRef, useState} from 'react';
import Link from 'next/link';
import common from '@/modules/administration/access.module.css';
import {pdfEvidenceLabel, pdfEvidenceReady, pdfFileAllowed, type PdfEvidenceStatus} from '@/modules/documents/pdf-evidence-client';
import {createPaymentSupplementGateway, paymentSupplementError, paymentSupplementOwnerKey, supplementBinding, supplementReason,
  type PaymentSupplement, type PaymentSupplementList, type PaymentSupplementOwner} from './payment-pdf-supplement-client';
import {SupplementJournal, recoverSupplement, sendSupplementOnce, supplementKey, supplementKeyBinding, supplementOperations,
  type SupplementPending} from './payment-pdf-supplement-recovery';
import {PaymentSupplementReviewPanel, type PaymentSupplementObservation} from './PaymentSupplementReviewPanel';

type ReadProps = {owner: PaymentSupplementOwner; locked: boolean; current: () => boolean; onDenied: (failure: unknown) => void;
  onSelectObservation?: (selection: PaymentSupplementObservation) => void};
export type PaymentSupplementRequesterProps = ReadProps & {fresh: boolean; now: number; journal: SupplementJournal; onBusyChange?: (busy: boolean) => void};
export function PaymentSupplementRequesterPanel(props: PaymentSupplementRequesterProps) {
  if (!props.current()) return <SupplementUnavailable/>;
  return <SupplementDesk key={'requester:' + paymentSupplementOwnerKey(props.owner)} {...props} context="requester"/>;
}
export function PaymentSupplementReviewerPanel(props: ReadProps) {
  if (!props.current()) return <SupplementUnavailable/>;
  return <SupplementDesk key={'reviewer:' + paymentSupplementOwnerKey(props.owner)} {...props} context="reviewer" fresh={false} now={0}/>;
}
function SupplementUnavailable() {return <section className={common.accountSection}><h2>Lampiran PDF tambahan</h2><p role="status">Sesi atau sumber berubah. Muat pengajuan foto terbaru sebelum membuka lampiran.</p></section>;}
type DeskProps = ReadProps & {context: 'requester' | 'reviewer'; fresh: boolean; now: number; journal?: SupplementJournal; onBusyChange?: (busy: boolean) => void};
function SupplementDesk(props: DeskProps) {
  const [gateway] = useState(createPaymentSupplementGateway), [shown, setShown] = useState<PaymentSupplementList | null>(null), [selected, setSelected] = useState<PaymentSupplement | null>(null);
  const [reservations, setReservations] = useState<PdfEvidenceStatus[]>([]), [statusLoaded, setStatusLoaded] = useState(false), [file, setFile] = useState<File | null>(null);
  const [createReason, setCreateReason] = useState(''), [attachReason, setAttachReason] = useState(''), [understood, setUnderstood] = useState(false);
  const [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState(''), [recoveryGeneration, setRecoveryGeneration] = useState(0);
  const active = useRef(false), pending = useRef(false), latest = useRef(props), controller = useRef<AbortController | null>(null), input = useRef<HTMLInputElement>(null);
  useLayoutEffect(() => {latest.current = props;}, [props]);
  const authorized = props.current();
  const ownerKey = paymentSupplementOwnerKey(props.owner), current = () => active.current && latest.current.current() && paymentSupplementOwnerKey(latest.current.owner) === ownerKey;
  useLayoutEffect(() => {active.current = true; return () => {active.current = false; controller.current?.abort();};}, []);
  useLayoutEffect(() => {if (!authorized) controller.current?.abort();}, [authorized]);
  async function work(action: (valid: () => boolean, signal: AbortSignal) => Promise<void>) {
    if (!current() || props.locked || pending.current) return; pending.current = true; controller.current?.abort();
    const request = new AbortController(); controller.current = request; const valid = () => current() && controller.current === request && !request.signal.aborted;
    setBusy(true); setError(null); props.onBusyChange?.(true);
    try {await action(valid, request.signal);} catch (failure) {if (valid()) {setError(failure); latest.current.onDenied(failure);}}
    finally {pending.current = false; if (active.current) {setBusy(false); setRecoveryGeneration(value => value + 1);} latest.current.onBusyChange?.(false);}
  }
  const clearSelection = () => {setSelected(null); setReservations([]); setStatusLoaded(false); setFile(null); setAttachReason(''); setUnderstood(false); if (input.current) input.current.value = '';};
  async function load(valid: () => boolean, signal: AbortSignal, beforeId: string | null = null) {
    if (beforeId === null) {clearSelection(); setShown(null);}
    const data = await gateway.list(props.context, props.owner, beforeId, valid, signal); if (!valid()) return;
    if (data.readiness === 'policy_unavailable') clearSelection();
    setShown(previous => beforeId !== null && previous?.readiness === data.readiness ? {...data, items: [...previous.items, ...data.items]} : data);
  }
  async function pick(saved: PaymentSupplement, valid: () => boolean, signal: AbortSignal) {
    clearSelection(); const detail = await gateway.detail(props.context, props.owner, saved.supplementId, valid, signal); if (valid()) setSelected(detail);
  }
  async function loadStatus(saved: PaymentSupplement, valid: () => boolean, signal?: AbortSignal, beforeId: string | null = null) {
    setStatusLoaded(false); const rows = await gateway.reservations(props.owner, saved, beforeId, valid, signal); if (!valid()) return;
    setReservations(previous => beforeId === null ? rows : [...previous, ...rows]); setStatusLoaded(true);
  }
  // Re-evaluate journal storage after each operation without putting PDF or form values into storage.
  void recoveryGeneration;
  let entries: SupplementPending[] = [], storageUnavailable = false, hasPending = false;
  try {entries = props.journal?.list(props.owner.actorId) ?? []; hasPending = props.journal?.hasAnyUnresolved(props.owner.actorId) ?? false;} catch {storageUnavailable = true;}
  const ownEntries = entries.filter(entry => {const binding = supplementKeyBinding(entry.operation, entry.key, props.owner.actorId);
    return binding.declarationId === props.owner.declarationId && binding.submissionId === props.owner.submissionId;});
  const locked = props.locked || busy, mayWrite = props.context === 'requester' && props.fresh && shown?.readiness === 'allowed' && !storageUnavailable && !hasPending && !!props.journal && !locked;
  const unsettledUpload = reservations.some(item => ['writing', 'publish_pending', 'interrupted'].includes(item.state));
  async function create() {
    if (!mayWrite || !props.journal) return;
    await work(async valid => {
      const reason = supplementReason(createReason), key = supplementKey(supplementOperations.create, supplementBinding(props.owner, null), crypto.randomUUID());
      const saved = await sendSupplementOnce(props.journal!, {actorId: props.owner.actorId, operation: supplementOperations.create, key}, () => gateway.create(props.owner, key, reason, valid));
      if (!valid()) return; setSelected(saved); setReservations([]); setStatusLoaded(false); setCreateReason('');
      setShown(previous => previous ? {...previous, items: [saved, ...previous.items]} : previous); setNotice('Draf lampiran tambahan tersimpan. Bukti foto utama dan status pembayaran tetap terpisah.');
    });
  }
  async function upload(existing?: PdfEvidenceStatus) {
    if (!mayWrite || !selected || selected.state !== 'draft' || !file || !statusLoaded || unsettledUpload || !props.journal) return;
    if (existing && (existing.state !== 'fresh' || Date.parse(existing.expiresAt) <= props.now)) return;
    const saved = selected, chosen = file;
    await work(async (valid, signal) => {
      pdfFileAllowed(chosen, 'pdf'); const key = supplementKey(supplementOperations.reserve, supplementBinding(props.owner, saved), crypto.randomUUID());
      const reserved = existing ?? await sendSupplementOnce(props.journal!, {actorId: props.owner.actorId, operation: supplementOperations.reserve, key}, () => gateway.reserve(props.owner, saved, key, valid));
      if (!valid()) return; setStatusLoaded(false); let failed: unknown = null;
      try {await gateway.upload(props.owner, saved, {reservationId: reserved.reservationId, revision: '1', expiresAt: reserved.expiresAt}, chosen, valid);} catch (failure) {failed = failure;}
      if (!valid()) return; setFile(null); if (input.current) input.current.value = ''; await loadStatus(saved, valid, signal);
      if (!valid()) return; setNotice('Periksa keamanan dan hasil upaya unggah yang sama. PDF belum ditautkan dan belum mengonfirmasi pembayaran.');
      if (failed !== null) throw failed;
    });
  }
  async function attach(item: PdfEvidenceStatus) {
    if (!mayWrite || !selected || selected.state !== 'draft' || !pdfEvidenceReady(item) || !understood || !props.journal) return;
    const saved = selected;
    await work(async valid => {
      const reason = supplementReason(attachReason), key = supplementKey(supplementOperations.attach, supplementBinding(props.owner, saved), crypto.randomUUID());
      const attached = await sendSupplementOnce(props.journal!, {actorId: props.owner.actorId, operation: supplementOperations.attach, key}, () => gateway.attach(props.owner, saved, item, reason, key, valid));
      if (!valid()) return; setSelected(attached); setReservations([]); setStatusLoaded(false); setFile(null); setUnderstood(false);
      setShown(previous => previous ? {...previous, items: previous.items.map(row => row.supplementId === attached.supplementId ? attached : row)} : previous);
      setNotice('Lampiran PDF tambahan ditautkan. Pembayaran belum dikonfirmasi; bukti foto dan bank independen tetap wajib diperiksa.');
    });
  }
  async function recover(entry: SupplementPending) {
    if (!props.journal || props.context !== 'requester') return;
    await work(async (valid, signal) => {
      const binding = supplementKeyBinding(entry.operation, entry.key, props.owner.actorId);
      const saved = binding.supplementId === null ? null : await gateway.detail('requester', props.owner, binding.supplementId, valid, signal);
      if (!valid()) return;
      if (entry.operation === supplementOperations.reserve) {
        const reserved = await recoverSupplement(props.journal!, entry, valid, (_binding, key) => gateway.recoverReservation(props.owner, saved!, key, valid));
        if (!valid()) return; if (reserved) {setSelected(saved); await loadStatus(saved!, valid, signal);}
        setNotice(reserved ? 'Reservasi asli ditemukan. Pilih berkas kembali untuk melanjutkan hanya reservasi yang masih siap; tidak ada unggah otomatis.' : 'Hasil reservasi belum terkonfirmasi. Rujukan tetap disimpan; tidak ada pengiriman ulang.');
      } else {
        const result = await recoverSupplement(props.journal!, entry, valid, (_binding, key) => gateway.recoverCommand(entry.operation as typeof supplementOperations.create | typeof supplementOperations.attach, props.owner, key, saved, valid));
        if (!valid()) return; if (result) {setSelected(result); setReservations([]); setStatusLoaded(false);
          setShown(previous => previous ? {...previous, items: [result, ...previous.items.filter(item => item.supplementId !== result.supplementId)]} : previous);}
        setNotice(result ? 'Hasil upaya asli ditemukan. Tidak ada perintah yang dikirim ulang; muat lampiran untuk pemeriksaan.' : 'Hasil upaya belum terkonfirmasi. Rujukan tetap disimpan; tidak ada pengiriman ulang.');
      }
    });
  }
  if (!authorized) return <section className={common.accountSection}><h2>Lampiran PDF tambahan</h2><p role="status">Sesi atau sumber berubah. Muat pengajuan foto terbaru sebelum membuka lampiran.</p></section>;
  return <section className={common.accountSection} aria-label="Lampiran PDF tambahan"><h2>Lampiran PDF tambahan</h2>
    <p>PDF merupakan lampiran tambahan pada pengajuan foto {props.owner.submissionId}. Foto bukti pembayaran yang memenuhi syarat tetap wajib.
      {' '}Lampiran tidak mengubah nilai transfer, OCR atau koreksi foto utama, verifikasi bank, maupun status pembayaran.</p>
    {error !== null && <p className={common.alert} role="alert">{paymentSupplementError(error)}</p>}{notice && <p role="status">{notice}</p>}
    <button className={common.secondary} type="button" disabled={locked} onClick={() => void work((valid, signal) => load(valid, signal))}>Muat lampiran PDF tambahan</button>
    {!shown && <p role="status">{busy ? 'Memuat lampiran tambahan…' : 'Muat lampiran untuk melihat kebijakan dan upaya tersimpan.'}</p>}
    {shown?.readiness === 'policy_unavailable' && <p role="status">Lampiran PDF belum diizinkan oleh kebijakan yang disetujui. Unggah dan pemeriksaan PDF belum tersedia. Pengajuan foto utama tetap digunakan.</p>}
    {props.context === 'requester' && !props.fresh && <p role="status">Verifikasi akun terbaru diperlukan sebelum membuat atau menautkan lampiran. <Link href="/identity/account">Periksa verifikasi akun</Link>.</p>}
    {shown && !shown.items.length && <p>Belum ada lampiran PDF tambahan pada pengajuan ini.</p>}
    {storageUnavailable && <p role="alert">Rujukan pemulihan tidak dapat dibaca dengan aman. Periksa penyimpanan browser atau sesi sebelum membuat lampiran.</p>}
    {hasPending && entries.length === 0 && <p role="status">Ada upaya lain yang belum pasti. Periksa pemulihan permintaan sebelum membuat lampiran baru.</p>}
    {entries.length > ownEntries.length && <p role="status">Ada upaya lampiran pada pengajuan lain yang belum pasti. Buka pengajuan asal dan periksa rujukannya sebelum membuat upaya baru.</p>}
    {ownEntries.length > 0 && <section className={common.preview} aria-label="Pemulihan lampiran PDF tambahan"><h3>Periksa upaya yang sama</h3>
      <p>Hasil belum pasti. Rujukan asli tetap disimpan; pemeriksaan ini tidak mengirim ulang perintah.</p>
      {ownEntries.map(entry => <p key={entry.key}><span>{entry.operation} · {entry.createdAt}.</span>{' '}<button className={common.secondary} type="button" disabled={locked}
        onClick={() => void recover(entry)}>Periksa upaya lampiran {supplementKeyBinding(entry.operation, entry.key, props.owner.actorId).supplementId ?? 'baru'}</button></p>)}</section>}
    <ul className={common.historyList}>{shown?.items.map(item => <li key={item.supplementId}><h3>Lampiran {item.supplementId}</h3>
      <p>{item.state === 'attached' ? 'PDF tambahan telah ditautkan; pembayaran belum dikonfirmasi.' : 'Draf lampiran tambahan belum memiliki PDF yang ditautkan.'}</p>
      <p>{item.reason}</p><button className={common.secondary} type="button" disabled={locked} onClick={() => void work((valid, signal) => pick(item, valid, signal))}>Lihat lampiran {item.supplementId}</button></li>)}</ul>
    {shown?.nextSupplementId && <button className={common.secondary} type="button" disabled={locked} onClick={() => void work((valid, signal) => load(valid, signal, shown.nextSupplementId))}>Lampiran lebih lama</button>}
    {props.context === 'requester' && <section><h3>Buat lampiran tambahan</h3><label>Alasan membuat lampiran PDF tambahan<textarea value={createReason} maxLength={2000} disabled={!mayWrite} onChange={event => setCreateReason(event.target.value)}/></label>
      <p className={common.help}>{Array.from(createReason.normalize('NFC')).length} dari paling banyak 1.000 karakter.</p>
      <button className={common.secondary} type="button" disabled={!mayWrite || !createReason.trim() || Array.from(createReason.normalize('NFC')).length > 1000} onClick={() => void create()}>Buat draf lampiran tambahan</button></section>}
    {selected?.state === 'draft' && props.context === 'requester' && <section className={common.preview} aria-label="Unggah lampiran PDF tambahan"><h3>Unggah PDF untuk lampiran {selected.supplementId}</h3>
      <p>PDF asli paling besar 10 MiB dan 10 halaman. PDF terenkripsi, formulir, lampiran tertanam atau JavaScript tidak didukung. Kegagalan keamanan tidak dapat dilewati.</p>
      <label>PDF tambahan asli<input ref={input} type="file" accept="application/pdf,.pdf" disabled={!mayWrite || unsettledUpload} onChange={event => setFile(event.target.files?.[0] ?? null)}/></label>
      <div className={common.actions}><button className={common.secondary} type="button" disabled={locked || shown?.readiness !== 'allowed'} onClick={() => void work((valid, signal) => loadStatus(selected, valid, signal))}>Muat status PDF tambahan</button>
        <button className={common.secondary} type="button" disabled={!mayWrite || !statusLoaded || !file || unsettledUpload} onClick={() => void upload()}>Unggah PDF tambahan</button></div>
      {!statusLoaded && <p>Muat status sebelum mengunggah agar upaya sebelumnya diperiksa.</p>}{statusLoaded && !reservations.length && <p>Belum ada upaya PDF untuk lampiran ini.</p>}
      {unsettledUpload && <p role="status">Ada unggah yang belum pasti. Periksa upaya yang sama sebelum mengunggah lagi.</p>}
      <label>Alasan menautkan PDF tambahan<textarea value={attachReason} maxLength={2000} disabled={!mayWrite} onChange={event => setAttachReason(event.target.value)}/></label>
      <p className={common.help}>{Array.from(attachReason.normalize('NFC')).length} dari paling banyak 1.000 karakter.</p>
      <label className={common.checkbox}><input type="checkbox" checked={understood} disabled={!mayWrite} onChange={event => setUnderstood(event.target.checked)}/><span>Saya memahami PDF ini hanya lampiran tambahan; foto bukti utama tetap wajib dan pembayaran belum dikonfirmasi.</span></label>
      <ul className={common.historyList}>{reservations.map(item => <li key={item.reservationId}><h4>Upaya PDF {item.reservationId}</h4><p>{pdfEvidenceLabel(item)}</p>
        {item.state === 'fresh' && <button className={common.secondary} type="button" disabled={!mayWrite || !file || unsettledUpload || Date.parse(item.expiresAt) <= props.now} onClick={() => void upload(item)}>Lanjutkan reservasi PDF {item.reservationId}</button>}
        {['writing', 'publish_pending', 'interrupted'].includes(item.state) && <button className={common.secondary} type="button" disabled={locked} onClick={() => void work(async (valid, signal) => {await gateway.recoverUpload(props.owner, selected, item.reservationId, valid); if (valid()) await loadStatus(selected, valid, signal);})}>Periksa upaya unggah PDF {item.reservationId}</button>}
        {pdfEvidenceReady(item) && <button className={common.secondary} type="button" disabled={!mayWrite || !understood || !attachReason.trim() || Array.from(attachReason.normalize('NFC')).length > 1000} onClick={() => void attach(item)}>Tautkan sebagai lampiran tambahan {item.reservationId}</button>}
      </li>)}</ul>
      {reservations.length > 0 && reservations.length % 25 === 0 && <button className={common.secondary} type="button" disabled={locked} onClick={() => void work((valid, signal) => loadStatus(selected, valid, signal, reservations.at(-1)!.reservationId))}>Upaya PDF lebih lama</button>}
    </section>}
    {selected?.state === 'attached' && shown?.readiness === 'allowed' && <PaymentSupplementReviewPanel key={selected.supplementId + ':' + selected.supplementRevision}
      context={props.context} owner={props.owner} supplement={selected} locked={locked} current={current} onDenied={props.onDenied} onSelectObservation={props.onSelectObservation}/>}
  </section>;
}
