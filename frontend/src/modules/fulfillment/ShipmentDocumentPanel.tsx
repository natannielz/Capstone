'use client';

import { useEffect, useRef, useState } from 'react';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import { submitTrackedRequest } from '../administration/operation-journal';
import { createShipmentDocumentGateway, prepareShipmentDocument, type ShipmentDocument } from './shipment-document-client';
import type { Shipment } from './shipment-client';
import common from '../administration/access.module.css';

function message(error: unknown) {
  if (error instanceof AccessFailure && error.code === 'shipment_document_already_issued') return 'Surat Jalan sudah diterbitkan. Muat dokumen untuk membuka salinan yang tersimpan.';
  if (error instanceof AccessFailure && error.status > 0 && error.status < 500) return accessMessage(error);
  return 'Hasil belum dapat dipastikan. Gunakan Periksa hasil untuk penerbitan yang terkirim; unduhan dapat dicoba kembali setelah tersambung.';
}
export function ShipmentDocumentPanel({ actorId, shipment, canIssue, current, onDenied, onLoaded }: {
  actorId: string; shipment: Shipment; canIssue: boolean; current: () => boolean; onDenied: (error: unknown) => void; onLoaded?: () => void;
}) {
  const [gateway] = useState(() => createShipmentDocumentGateway()), [ledger] = useState(() => new MutationLedger()), recovery = useOperationRecovery(actorId);
  const [document, setDocument] = useState<ShipmentDocument | null>(null), [loaded, setLoaded] = useState(false), [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false);
  const [error, setError] = useState<unknown>(null), [reload, setReload] = useState(0), [reason, setReason] = useState(''), [review, setReview] = useState(false), [offline, setOffline] = useState(false);
  const alive = useRef(false), pending = useRef(false), recovered = useRef<ShipmentDocument | null>(null), reviewHeading = useRef<HTMLHeadingElement>(null);
  const locked = busy || recovering || recovery.locked || offline;
  useEffect(() => { alive.current = true; const connection = () => setOffline(!navigator.onLine); connection();
    window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => { alive.current = false; window.removeEventListener('online', connection); window.removeEventListener('offline', connection); };
  }, []);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setBusy(true); setLoaded(false); setDocument(null); setError(null); setReview(false);
      try { const result = await gateway.read(actorId, shipment.id, controller.signal); if (active && current()) { setDocument(result); setLoaded(true); } }
      catch (failure) { if (active) { setError(failure); onDenied(failure); } } finally { if (active) { setBusy(false); if (current()) onLoaded?.(); } }
    }); return () => { active = false; controller.abort(); };
  }, [gateway, actorId, shipment.id, reload, current, onDenied, onLoaded]);
  useEffect(() => { if (review) reviewHeading.current?.focus(); }, [review]);
  async function issue() {
    if (!alive.current || !current() || locked || pending.current || !canIssue || !review || document) return;
    pending.current = true; setBusy(true); setError(null);
    try {
      if (recovery.journal.list(actorId).length > 0) throw new AccessFailure(400, 'resolve_previous_attempt');
      const command = prepareShipmentDocument(actorId, shipment.id, reason);
      const result = await submitTrackedRequest(ledger, recovery.journal, actorId, 'fulfillment.surat-jalan.issue', 'fulfillment.surat-jalan.issue', command,
        key => gateway.issue(command, key, () => alive.current && current()));
      if (alive.current && current()) { setDocument(result); setReview(false); }
    } catch (failure) { if (alive.current && current()) { setError(failure); setReview(false); onDenied(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); recovery.refresh(); } }
  }
  async function download() {
    if (!document || !alive.current || !current() || busy || pending.current || offline) return;
    pending.current = true; setBusy(true); setError(null);
    try {
      const blob = await gateway.download(actorId, document); if (!alive.current || !current()) return;
      const url = URL.createObjectURL(blob), link = window.document.createElement('a'); link.href = url;
      link.download = `surat-jalan-${shipment.id}-v1-${document.id}.html`; window.document.body.appendChild(link); link.click(); link.remove();
      window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (failure) { if (alive.current && current()) { setError(failure); onDenied(failure); } }
    finally { pending.current = false; if (alive.current) setBusy(false); }
  }
  return <section className={common.accountSection} aria-label="Surat Jalan" aria-busy={busy}><h3>Surat Jalan</h3>
    <OperationRecovery entries={recovery.entries.filter(entry => !entry.operation.startsWith('fulfillment.receipt.'))} journal={recovery.journal} disabled={busy || offline} onBusyChange={setRecovering}
      recoveryAdapters={{ 'fulfillment.surat-jalan': { lookup: async entry => { const receipt = await gateway.recover(entry.actorId, entry.key); recovered.current = receipt?.result ?? null; return receipt; } } }}
      onFailure={failure => { setError(failure); onDenied(failure); }} onResolved={() => {
        recovery.refresh(); ledger.resetAfterReconciliation(); if (alive.current && current() && recovered.current?.shipmentId === shipment.id) setDocument(recovered.current);
        recovered.current = null;
      }} />
    {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    {recovery.error !== null && <p role="alert">{accessMessage(recovery.error)}</p>}
    {offline && <p role="status">Belum tersambung. Penerbitan dan unduhan menunggu koneksi.</p>}
    {busy && <p role="status">Memeriksa dokumen…</p>}
    {document ? <><p role="status">Surat Jalan versi {document.version} tersimpan.</p>
      <p>Salinan memuat tujuan dan kemasan saat keberangkatan. Buka berkas HTML yang diunduh untuk membaca atau mencetaknya.</p>
      <button className={common.primary} disabled={busy || offline} onClick={() => void download()}>Unduh Surat Jalan</button></>
      : loaded && <><p>Surat Jalan belum diterbitkan.</p>{canIssue && (review ? <><h4 ref={reviewHeading} tabIndex={-1}>Konfirmasi penerbitan Surat Jalan</h4>
        <p>Nomor {shipment.numberEntity}/{shipment.numberChannel}/{shipment.numberYear}/{shipment.number}. Dokumen akan menyimpan tujuan dan jumlah kemasan di atas secara tetap.</p>
        <p>Catatan: {reason}</p><div className={common.actions}><button className={common.primary} disabled={locked} onClick={() => void issue()}>Terbitkan Surat Jalan</button>
          <button className={common.secondary} disabled={locked} onClick={() => setReview(false)}>Kembali ke catatan</button></div></>
        : <form onSubmit={event => { event.preventDefault(); if (!locked && current()) { try { prepareShipmentDocument(actorId, shipment.id, reason); setReview(true); setError(null); } catch (failure) { setError(failure); } } }}>
          <label>Catatan penerbitan<input required maxLength={1000} value={reason} disabled={locked} onChange={event => setReason(event.target.value)} /></label>
          <button className={common.secondary} disabled={locked}>Tinjau Surat Jalan</button></form>)}</>}
    <button className={common.secondary} disabled={busy || offline} onClick={() => setReload(value => value + 1)}>Muat dokumen</button>
  </section>;
}
