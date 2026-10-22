'use client';

import {useCallback, useEffect, useRef, useState} from 'react';
import {MutationLedger, AccessFailure, accessMessage} from '@/modules/administration/workflow';
import {submitTrackedRequest, type OperationJournal} from '@/modules/administration/operation-journal';
import {RequestNotSentError} from '@/api/client';
import common from '@/modules/administration/access.module.css';
import {createPdfEvidenceGateway, pdfEvidenceLabel, pdfEvidenceReady, pdfFileAllowed, pdfReservationKey, pdfReserveOperation,
  type PdfEvidenceTarget, type PdfEvidenceStatus} from './pdf-evidence-client';

export type PdfEvidenceAdmissionPanelProps = {owner: PdfEvidenceTarget; locked: boolean; fresh: boolean; now: number;
  current: () => boolean; journal: OperationJournal; onBusyChange: (busy: boolean) => void; onDenied: (failure: unknown) => void;
  onReviewSubmission: (selected: {status: PdfEvidenceStatus; reviewedOriginalKey: string}) => void};
const binding = (item: PdfEvidenceStatus) => `${item.reservationId}:${item.reference?.id}:${item.reference?.version}:${item.reference?.sha256}`;
const describeError = (failure: unknown) => failure instanceof RequestNotSentError && failure.code === 'explicit_pdf_required' ?
  'Pilih jenis PDF dan satu berkas PDF asli yang tidak kosong, paling besar 10 MiB.' : failure instanceof AccessFailure && failure.code === 'pdf_evidence_response_invalid' ?
  'Hasil unggah atau bukti belum dapat dipastikan. Muat status terbaru; jangan mengirim ulang upaya yang belum pasti.' : accessMessage(failure);

/** Key this component by actor/source/revision and remove it on any format/session change. */
export function PdfEvidenceAdmissionPanel(props: PdfEvidenceAdmissionPanelProps) {
  const [gateway] = useState(createPdfEvidenceGateway), [ledger] = useState(() => new MutationLedger(() => pdfReservationKey(props.owner, crypto.randomUUID())));
  const [items, setItems] = useState<PdfEvidenceStatus[]>([]), [loaded, setLoaded] = useState(false), [file, setFile] = useState<File | null>(null);
  const [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState('');
  const [downloaded, setDownloaded] = useState<string | null>(null), [reviewed, setReviewed] = useState<string | null>(null);
  const alive = useRef(false), pending = useRef(false), fileInput = useRef<HTMLInputElement>(null), read = useRef<AbortController | null>(null), urls = useRef(new Set<string>());
  const permission = props.current, current = useCallback(() => alive.current && permission(), [permission]);
  useEffect(() => { alive.current = true; const privateUrls = urls.current;
    return () => {alive.current = false; read.current?.abort(); for (const url of privateUrls) URL.revokeObjectURL(url); privateUrls.clear();}; }, []);
  async function work(action: () => Promise<void>) {
    if (!current() || props.locked || pending.current) return; pending.current = true; setBusy(true); props.onBusyChange(true); setError(null);
    try {await action();} catch (failure) {if (current()) {setError(failure); props.onDenied(failure);}}
    finally {pending.current = false; if (alive.current) setBusy(false); props.onBusyChange(false);}
  }
  async function load(beforeId: string | null = null) {
    read.current?.abort(); const controller = new AbortController(); read.current = controller;
    const valid = () => current() && read.current === controller && !controller.signal.aborted;
    const rows = await gateway.list(props.owner, beforeId, valid, controller.signal); if (!valid()) return;
    setItems(previous => beforeId === null ? rows : [...previous, ...rows.filter(item => !previous.some(row => row.reservationId === item.reservationId))]); setLoaded(true);
  }
  const uncertainUpload = items.some(item => ['writing', 'publish_pending', 'interrupted'].includes(item.state));
  let recoveryUnavailable = false, unresolvedReservation = false;
  try {unresolvedReservation = props.journal.list(props.owner.actorId).length > 0;} catch {recoveryUnavailable = true;}
  const locked = props.locked || busy, commandLocked = locked || !props.fresh || unresolvedReservation || recoveryUnavailable;
  async function upload(existing?: PdfEvidenceStatus) {
    if (!file || !loaded || commandLocked || uncertainUpload) return; const chosen = file;
    if (existing && (existing.state !== 'fresh' || Date.parse(existing.expiresAt) <= props.now)) return;
    await work(async () => {
      pdfFileAllowed(chosen, 'pdf');
      setDownloaded(null); setReviewed(null);
      const reserved = existing ?? await submitTrackedRequest(ledger, props.journal, props.owner.actorId, pdfReserveOperation(props.owner), pdfReserveOperation(props.owner),
        {sourceId: props.owner.sourceId, sourceRevision: props.owner.sourceRevision}, key => gateway.reserve(props.owner, key, 'Unggah PDF asli untuk pemeriksaan keamanan dan tinjauan manusia', current));
      if (!current()) return; let failed: unknown = null;
      try {await gateway.upload(props.owner, {reservationId: reserved.reservationId, revision: '1', expiresAt: reserved.expiresAt}, chosen, 'pdf', current);} catch (failure) {failed = failure;}
      if (!current()) return; setFile(null); if (fileInput.current) fileInput.current.value = ''; await load();
      if (!current()) return; setNotice('Periksa status upaya unggah yang sama. Penyimpanan dan pemeriksaan PDF tidak menyatakan pembayaran atau pencocokan selesai.');
      if (failed !== null) throw failed;
    });
  }
  async function download(item: PdfEvidenceStatus) {
    if (!pdfEvidenceReady(item) || !item.reference) return;
    await work(async () => {
      setDownloaded(null); setReviewed(null); read.current?.abort(); const controller = new AbortController(); read.current = controller;
      const valid = () => current() && read.current === controller && !controller.signal.aborted;
      const blob = await gateway.original(props.owner.sourceKind === 'Payment' ? 'payment-requester' : 'supplier-document', props.owner, item.reservationId, item.reference!, valid, controller.signal);
      if (!valid()) return; const url = URL.createObjectURL(blob); urls.current.add(url); const link = document.createElement('a');
      try {link.href = url; link.download = `bukti-${item.reference!.id}-${item.reference!.version}.pdf`; document.body.appendChild(link); link.click(); setDownloaded(binding(item));}
      finally {link.remove(); window.setTimeout(() => {URL.revokeObjectURL(url); urls.current.delete(url);}, 1000);}
    });
  }
  return <section className={common.accountSection} aria-label="Bukti PDF asli"><h3>Bukti PDF asli</h3>
    <p>Unggah PDF asli paling besar 10 MiB, sampai 10 halaman. Dokumen terenkripsi, formulir, lampiran dan JavaScript tidak didukung. Pemeriksaan seluruh PDF dapat menolak dokumen; hasil sebagian tidak dipakai.</p>
    {error !== null && <p className={common.alert} role="alert">{describeError(error)}</p>}{notice && <p role="status">{notice}</p>}
    {recoveryUnavailable && <p role="alert">Rujukan pemulihan belum dapat disimpan. Aktifkan penyimpanan situs di browser sebelum mengunggah.</p>}
    <label>PDF asli<input ref={fileInput} type="file" accept="application/pdf,.pdf" disabled={commandLocked || uncertainUpload}
      onChange={event => {setFile(event.target.files?.[0] ?? null); setDownloaded(null); setReviewed(null);}}/></label>
    <div className={common.actions}><button className={common.secondary} type="button" disabled={locked} onClick={() => void work(() => load())}>Muat status PDF</button>
      <button className={common.secondary} type="button" disabled={commandLocked || !loaded || !file || uncertainUpload} onClick={() => void upload()}>Unggah PDF asli</button></div>
    {!loaded && <p>Muat status PDF sebelum mengunggah agar upaya sebelumnya tetap diperiksa.</p>}{loaded && !items.length && <p>Belum ada PDF pada sumber ini.</p>}
    {uncertainUpload && <p role="status">Ada upaya PDF yang belum pasti. Periksa hasil upaya yang sama sebelum mengunggah lagi.</p>}
    <ul className={common.historyList}>{items.map(item => <li key={item.reservationId}><h4>PDF {item.reservationId}</h4><p>{pdfEvidenceLabel(item)}</p>
      {item.state === 'fresh' && <button className={common.secondary} type="button" disabled={commandLocked || !file || !loaded || uncertainUpload || Date.parse(item.expiresAt) <= props.now}
        onClick={() => void upload(item)}>Lanjutkan unggah PDF {item.reservationId}</button>}
      {['writing', 'publish_pending', 'interrupted'].includes(item.state) && <button className={common.secondary} type="button" disabled={commandLocked}
        onClick={() => void work(async () => {await gateway.recoverUpload(props.owner, item.reservationId, current); if (current()) await load();})}>Periksa hasil unggah PDF {item.reservationId}</button>}
      {pdfEvidenceReady(item) && <><button className={common.secondary} type="button" disabled={locked} onClick={() => void download(item)}>Unduh PDF asli {item.reservationId} untuk pemeriksaan</button>
        <label className={common.checkbox}><input type="checkbox" disabled={locked || downloaded !== binding(item)} checked={reviewed === binding(item)}
          onChange={event => setReviewed(event.target.checked ? binding(item) : null)}/><span>Saya telah membaca seluruh halaman PDF asli yang diunduh dan memeriksa dokumen ini.</span></label>
        <button className={common.primary} type="button" disabled={commandLocked || reviewed !== binding(item) || downloaded !== binding(item)} onClick={() => {
          if (current() && !commandLocked && reviewed === binding(item) && downloaded === binding(item)) props.onReviewSubmission({status: item, reviewedOriginalKey: binding(item)});
        }}>Tinjau pengajuan dengan PDF {item.reservationId}</button></>}
    </li>)}</ul>
    {items.length > 0 && items.length % 25 === 0 && <button className={common.secondary} type="button" disabled={locked} onClick={() => void work(() => load(items.at(-1)!.reservationId))}>PDF lebih lama</button>}
  </section>;
}
