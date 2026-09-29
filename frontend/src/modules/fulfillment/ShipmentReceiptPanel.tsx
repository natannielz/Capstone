'use client';

import { useEffect, useRef, useState } from 'react';
import { toCanvas } from 'qrcode';
import { MutationLedger, accessMessage } from '../administration/workflow';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import { submitTrackedRequest } from '../administration/operation-journal';
import { createShipmentDocumentGateway } from './shipment-document-client';
import { createReceiptGateway, type Receipt, type ReceiptChallenge } from './receipt-client';
import common from '../administration/access.module.css';

export function ReceiptSummary({ receipt }: { receipt: Receipt }) {
  return <section aria-label="Hasil penerimaan"><h3>Penerimaan tersimpan</h3><p>Dikonfirmasi oleh {receipt.recipientName} pada {new Intl.DateTimeFormat('id-ID', { dateStyle: 'medium', timeStyle: 'short', timeZone: 'Asia/Jakarta' }).format(new Date(receipt.recordedAt))} WIB.</p>
    <p>{receipt.hasException ? 'Ada barang ditolak atau belum diterima. Perbedaan tercatat untuk tindak lanjut.' : 'Seluruh jumlah kiriman diterima.'}</p>
    <ul>{receipt.lines.map(line => <li key={line.skuId}>SKU {line.skuId}: diterima {line.acceptedBaseUnits}, ditolak {line.rejectedBaseUnits}, belum diterima {line.missingBaseUnits} satuan dasar.{line.exceptionReason && <p>{line.exceptionReason}</p>}</li>)}</ul></section>;
}
function ReceiptQr({ challenge }: { challenge: ReceiptChallenge }) {
  const canvas = useRef<HTMLCanvasElement>(null), [link, setLink] = useState(''), [error, setError] = useState(false), [expired, setExpired] = useState(false);
  useEffect(() => {
    if (!challenge.token) return; let active = true;
    const url = new URL('/fulfillment/receive', window.location.origin); url.hash = challenge.id + '.' + challenge.token;
    void Promise.resolve().then(async () => { if (!active) return; setLink(url.href); try { if (canvas.current) await toCanvas(canvas.current, url.href, { width: 256, margin: 4, errorCorrectionLevel: 'M' }); } catch { if (active) setError(true); } });
    const timer = window.setTimeout(() => { if (active) { setExpired(true); setLink(''); } }, Math.max(0, Date.parse(challenge.expiresAt) - Date.parse(challenge.serverTime)));
    return () => { active = false; window.clearTimeout(timer); };
  }, [challenge]);
  if (expired) return <p role="status">QR sudah kedaluwarsa. Buat QR baru setelah memastikan penerima.</p>;
  if (!challenge.token) return <p role="status">Pembuatan QR tersimpan, tetapi tautannya tidak dapat dipulihkan. Pastikan belum ada penerimaan, lalu buat QR baru bila diperlukan.</p>;
  return <div><p>Penerima: <strong>{challenge.recipientName}</strong>. QR berlaku paling lama 15 menit. Penerima harus masuk dengan akun sendiri dan memeriksa jumlah barang.</p>
    <canvas ref={canvas} role="img" aria-label="QR penerimaan untuk akun yang dipilih" style={{ maxWidth: '100%', height: 'auto' }} />
    {error && <p role="alert">QR belum dapat digambar. Gunakan tautan penerimaan.</p>}
    {link && <a href={link} target="_blank" rel="noopener noreferrer" referrerPolicy="no-referrer">Buka tautan penerimaan</a>}<p>Untuk tablet bersama, gunakan sesi penerima yang terpisah. Akun kurir tidak dapat menyatakan penerimaan.</p></div>;
}
export function ShipmentReceiptPanel({ actorId, shipmentId, canChallenge, current, onDenied }: {
  actorId: string; shipmentId: string; canChallenge: boolean; current: () => boolean; onDenied: (error: unknown) => void;
}) {
  const [gateway] = useState(createReceiptGateway), [documents] = useState(createShipmentDocumentGateway), [ledger] = useState(() => new MutationLedger()), recovery = useOperationRecovery(actorId);
  const [receipt, setReceipt] = useState<Receipt | null>(null), [loaded, setLoaded] = useState(false), [documentId, setDocumentId] = useState<string | null>(null);
  const [candidates, setCandidates] = useState<{ id: string; name: string }[]>([]), [hasMore, setHasMore] = useState(false), [before, setBefore] = useState<string | null>(null), [reload, setReload] = useState(0);
  const [recipient, setRecipient] = useState(''), [reason, setReason] = useState(''), [review, setReview] = useState(false), [challenge, setChallenge] = useState<ReceiptChallenge | null>(null);
  const [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [error, setError] = useState<unknown>(null), [offline, setOffline] = useState(false);
  const alive = useRef(false), pending = useRef(false); const locked = busy || recovering || recovery.locked || offline;
  useEffect(() => { alive.current = true; const connection = () => setOffline(!navigator.onLine); connection(); window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => { alive.current = false; window.removeEventListener('online', connection); window.removeEventListener('offline', connection); }; }, []);
  useEffect(() => { let active = true; void Promise.resolve().then(async () => { if (!active) return; setBusy(true); setLoaded(false); setError(null); setReview(false); setRecipient(''); setChallenge(null);
    try { const result = await gateway.read(actorId, shipmentId); if (!active || !current()) return; setReceipt(result);
      if (!result && canChallenge) { const document = await documents.read(actorId, shipmentId); if (!active || !current()) return; setDocumentId(document?.id ?? null);
        const page = await gateway.recipients(actorId, shipmentId, before); if (!active || !current()) return; setCandidates(page.items); setHasMore(page.hasMore); }
      setLoaded(true);
    } catch (failure) { if (active && current()) { setError(failure); onDenied(failure); } } finally { if (active) setBusy(false); }
  }); return () => { active = false; }; }, [actorId, shipmentId, canChallenge, before, reload, gateway, documents, current, onDenied]);
  async function issue() {
    if (!alive.current || !current() || locked || pending.current || !review || !documentId || !recipient || !reason.trim()) return;
    pending.current = true; setBusy(true); setError(null);
    try { if (recovery.journal.list(actorId).length) return;
      const payload = { actorId, shipmentId, documentId, recipient, reason };
      const result = await submitTrackedRequest(ledger, recovery.journal, actorId, 'fulfillment.receipt.challenge.issue', 'fulfillment.receipt.challenge.issue', payload,
        key => gateway.issue(actorId, shipmentId, documentId, recipient, reason, key, () => alive.current && current()));
      if (alive.current && current()) { setChallenge(result); setReview(false); }
    } catch (failure) { if (alive.current && current()) { setError(failure); onDenied(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); recovery.refresh(); } }
  }
  return <section className={common.accountSection} aria-label="Penerimaan kiriman" aria-busy={busy}><h3>Penerimaan kiriman</h3>
    <OperationRecovery entries={recovery.entries.filter(entry => entry.operation.startsWith('fulfillment.receipt.'))} journal={recovery.journal} disabled={busy || offline} onBusyChange={setRecovering}
      recoveryAdapters={{ 'fulfillment.receipt': { lookup: async entry => { const result = await gateway.recover(entry.actorId, entry.key, entry.operation === 'fulfillment.receipt.challenge.issue'); if (alive.current && current() && result && 'token' in result.result && result.result.shipmentId === shipmentId) setChallenge(result.result); return result; } } }}
      onFailure={failure => { setError(failure); onDenied(failure); }} onResolved={() => { recovery.refresh(); ledger.resetAfterReconciliation(); }} />
    {error !== null && <p role="alert">{accessMessage(error)}</p>}{recovery.error !== null && <p role="alert">{accessMessage(recovery.error)}</p>}
    {offline && <p role="status">Belum tersambung. Penerimaan belum dikirim dari perangkat ini.</p>}{busy && <p role="status">Memeriksa penerimaan…</p>}
    {receipt ? <ReceiptSummary receipt={receipt} /> : loaded && <><p>Keberangkatan tercatat. Penerimaan barang oleh cabang belum dicatat.</p>
      {canChallenge && (!documentId ? <p>Terbitkan Surat Jalan sebelum menyiapkan QR penerimaan.</p> : <>
        {challenge && <ReceiptQr key={challenge.id} challenge={challenge} />}
        {review ? <div><h4>Konfirmasi penerima QR</h4><p>{candidates.find(candidate => candidate.id === recipient)?.name} · {reason}</p>
          <button className={common.primary} disabled={locked} onClick={() => void issue()}>Buat QR penerimaan</button><button className={common.secondary} disabled={locked} onClick={() => setReview(false)}>Kembali ke penerima</button></div>
          : <form onSubmit={event => { event.preventDefault(); if (!locked && current()) setReview(true); }}><label htmlFor="receipt-recipient">Penerima terdaftar</label><select id="receipt-recipient" value={recipient} required disabled={locked} onChange={event => setRecipient(event.target.value)}><option value="">Pilih penerima</option>{candidates.map(candidate => <option key={candidate.id} value={candidate.id}>{candidate.name}</option>)}</select>
            <label>Catatan QR<input value={reason} required maxLength={1000} disabled={locked} onChange={event => setReason(event.target.value)} /></label><button className={common.secondary} disabled={locked || !recipient}>Tinjau penerima</button></form>}
        {hasMore && <button className={common.secondary} disabled={locked} onClick={() => setBefore(candidates.at(-1)!.id)}>Penerima lainnya</button>}
      </>)}
    </>}
    <button className={common.secondary} disabled={busy || offline} onClick={() => { setBefore(null); setReload(value => value + 1); }}>Muat penerimaan</button>
  </section>;
}
