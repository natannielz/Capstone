'use client';

import { useEffect, useRef, useState } from 'react';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import { submitTrackedRequest } from '../administration/operation-journal';
import { createReceiptProofGateway, prepareReceiptProof, type ReceiptProof } from './receipt-proof-client';
import common from '../administration/access.module.css';

export function ReceiptProofPanel({ actorId, shipmentId, receiptId, asRecipient, canIssue, current, onDenied }: {
  actorId: string; shipmentId: string; receiptId: string; asRecipient: boolean; canIssue: boolean; current: () => boolean; onDenied: (error: unknown) => void;
}) {
  const [gateway] = useState(createReceiptProofGateway), [ledger] = useState(() => new MutationLedger()), recovery = useOperationRecovery(actorId);
  const [document, setDocument] = useState<ReceiptProof | null>(null), [loaded, setLoaded] = useState(false), [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false);
  const [error, setError] = useState<unknown>(null), [reload, setReload] = useState(0), [reason, setReason] = useState(''), [review, setReview] = useState(false), [offline, setOffline] = useState(false);
  const alive = useRef(false), pending = useRef(false), heading = useRef<HTMLHeadingElement>(null), recovered = useRef<ReceiptProof | null>(null);
  const locked = busy || recovering || recovery.locked || offline;
  useEffect(() => { alive.current = true; const connection = () => setOffline(!navigator.onLine); connection(); window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => { alive.current = false; window.removeEventListener('online', connection); window.removeEventListener('offline', connection); }; }, []);
  useEffect(() => { let active = true; const controller = new AbortController(); void Promise.resolve().then(async () => {
    if (!active) return; setBusy(true); setLoaded(false); setDocument(null); setReview(false); setError(null);
    try { const result = await gateway.read(actorId, shipmentId, asRecipient, controller.signal); if (active && current()) {
      if (result && result.receiptId !== receiptId) throw new AccessFailure(502, 'receipt_proof_response_invalid'); setDocument(result); setLoaded(true); }
    } catch (failure) { if (active && current()) { setError(failure); onDenied(failure); } } finally { if (active) setBusy(false); }
  }); return () => { active = false; controller.abort(); }; }, [actorId, shipmentId, receiptId, asRecipient, gateway, current, onDenied, reload]);
  useEffect(() => { if (review) heading.current?.focus(); }, [review]);
  async function issue() {
    if (!alive.current || !current() || locked || pending.current || !canIssue || !review || document) return; pending.current = true; setBusy(true); setError(null);
    try { if (recovery.journal.list(actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
      const command = prepareReceiptProof(actorId, shipmentId, receiptId, asRecipient, reason);
      const result = await submitTrackedRequest(ledger, recovery.journal, actorId, 'fulfillment.receipt-proof.issue', 'fulfillment.receipt-proof.issue', command, key => gateway.issue(command, key, () => alive.current && current()));
      if (alive.current && current()) { setDocument(result); setReview(false); }
    } catch (failure) { if (alive.current && current()) { setError(failure); setReview(false); onDenied(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); recovery.refresh(); } }
  }
  async function download() {
    if (!document || !alive.current || !current() || busy || pending.current || offline) return; pending.current = true; setBusy(true); setError(null);
    try { const blob = await gateway.download(actorId, document, asRecipient); if (!alive.current || !current()) return;
      const url = URL.createObjectURL(blob), link = window.document.createElement('a'); link.href = url; link.download = `bukti-penerimaan-${shipmentId}-v1-${document.id}.html`;
      window.document.body.appendChild(link); link.click(); link.remove(); window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (failure) { if (alive.current && current()) { setError(failure); onDenied(failure); } } finally { pending.current = false; if (alive.current) setBusy(false); }
  }
  return <section className={common.accountSection} aria-label="Bukti penerimaan" aria-busy={busy}><h3>Bukti penerimaan</h3>
    <OperationRecovery entries={recovery.entries.filter(entry => entry.operation === 'fulfillment.receipt-proof.issue')} journal={recovery.journal} disabled={busy || offline} onBusyChange={setRecovering}
      recoveryAdapters={{ 'fulfillment.receipt-proof': { lookup: async entry => { const receipt = await gateway.recover(entry.actorId, entry.key); recovered.current = receipt?.result ?? null; return receipt; } } }}
      onFailure={failure => { setError(failure); onDenied(failure); }} onResolved={() => { recovery.refresh(); ledger.resetAfterReconciliation();
        if (alive.current && current() && recovered.current?.shipmentId === shipmentId && recovered.current.receiptId === receiptId) setDocument(recovered.current); recovered.current = null; }} />
    {error !== null && <p role="alert">{accessMessage(error)}</p>}{recovery.error !== null && <p role="alert">{accessMessage(recovery.error)}</p>}
    {offline && <p role="status">Belum tersambung. Penerbitan dan unduhan menunggu koneksi.</p>}{busy && <p role="status">Memeriksa bukti…</p>}
    {document ? <><p role="status">Bukti penerimaan versi 1 tersimpan.</p><p>Salinan memuat pernyataan penerima dan jumlah barang yang telah dikonfirmasi. Buka berkas HTML untuk membaca atau mencetaknya.</p>
      <button className={common.primary} disabled={busy || offline} onClick={() => void download()}>Unduh bukti penerimaan</button></>
      : loaded && <><p>Bukti penerimaan belum diterbitkan.</p>{canIssue && (review ? <><h4 ref={heading} tabIndex={-1}>Konfirmasi penerbitan bukti</h4>
        <p>Kiriman {shipmentId}, penerimaan {receiptId}. Jumlah yang telah dikonfirmasi dan pernyataan penerima akan disimpan sebagai salinan tetap.</p><p>Catatan: {reason}</p>
        <button className={common.primary} disabled={locked} onClick={() => void issue()}>Terbitkan bukti penerimaan</button><button className={common.secondary} disabled={locked} onClick={() => setReview(false)}>Kembali ke catatan bukti</button></>
        : <form onSubmit={event => { event.preventDefault(); if (!locked && current()) { try { prepareReceiptProof(actorId, shipmentId, receiptId, asRecipient, reason); setReview(true); setError(null); } catch (failure) { setError(failure); } } }}>
          <label>Catatan bukti penerimaan<input required maxLength={1000} value={reason} disabled={locked} onChange={event => setReason(event.target.value)} /></label><button className={common.secondary} disabled={locked}>Tinjau bukti penerimaan</button></form>)}</>}
    <button className={common.secondary} disabled={busy || offline} onClick={() => setReload(value => value + 1)}>Muat bukti penerimaan</button>
  </section>;
}
