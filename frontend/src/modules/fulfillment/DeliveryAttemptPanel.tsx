'use client';

import { useEffect, useRef, useState } from 'react';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { type useOperationRecovery } from '../administration/OperationRecovery';
import { submitTrackedRequest } from '../administration/operation-journal';
import { createDeliveryAttemptGateway, deliveryOutcomes, jakartaVisitInput, jakartaVisitTime, prepareDeliveryAttempt, type DeliveryHistory, type DeliveryPreview } from './delivery-attempt-client';
import common from '../administration/access.module.css';

export const visitTime = (value: string) => new Intl.DateTimeFormat('id-ID', { dateStyle: 'medium', timeStyle: 'short', timeZone: 'Asia/Jakarta' }).format(new Date(value));
function message(error: unknown) {
  if (error instanceof AccessFailure && ['invalid_delivery_attempt', 'invalid_delivery_attempt_time'].includes(error.code)) return 'Pilih hasil kunjungan, isi waktu sejak keberangkatan dan catatan hingga 1.000 karakter. Waktu tidak boleh melewati waktu saat ini atau mendahului kunjungan terakhir.';
  if (error instanceof AccessFailure && ['delivery_attempt_changed', 'shipment_already_received'].includes(error.code)) return 'Riwayat kiriman berubah. Muat riwayat terbaru sebelum mencatat kunjungan.';
  return accessMessage(error);
}
export function DeliveryAttemptPanel({ actorId, shipmentId, canRecord, current, onDenied, recovery, onBusyChange }: {
  actorId: string; shipmentId: string; canRecord: boolean; current: () => boolean; onDenied: (error: unknown) => void;
  recovery: ReturnType<typeof useOperationRecovery>; onBusyChange: (value: boolean) => void;
}) {
  const [gateway] = useState(createDeliveryAttemptGateway), [ledger] = useState(() => new MutationLedger());
  const [history, setHistory] = useState<DeliveryHistory | null>(null), [preview, setPreview] = useState<DeliveryPreview | null>(null);
  const [before, setBefore] = useState<string | null>(null), [reload, setReload] = useState(0), [busy, setBusy] = useState(false), [offline, setOffline] = useState(false);
  const [outcome, setOutcome] = useState(''), [occurredAt, setOccurredAt] = useState(''), [reason, setReason] = useState(''), [error, setError] = useState<unknown>(null);
  const [review, setReview] = useState<ReturnType<typeof prepareDeliveryAttempt> | null>(null), [saved, setSaved] = useState(false);
  const alive = useRef(false), pending = useRef(false), heading = useRef<HTMLHeadingElement>(null);
  const locked = busy || offline || recovery.locked;
  useEffect(() => { alive.current = true; const connection = () => setOffline(!navigator.onLine); connection(); window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => { alive.current = false; window.removeEventListener('online', connection); window.removeEventListener('offline', connection); }; }, []);
  useEffect(() => { let active = true; const controller = new AbortController(); void Promise.resolve().then(async () => {
    if (!active) return; setBusy(true); onBusyChange(true); setPreview(null); setHistory(null); setReview(null); setError(null);
    try {
      const result = await gateway.read(actorId, shipmentId, before, controller.signal); if (!active || !current()) return; setHistory(result);
      if (canRecord) { const next = await gateway.preview(actorId, shipmentId, controller.signal); if (!active || !current()) return; setPreview(next); setOccurredAt(jakartaVisitInput(next.serverTime)); }
    } catch (failure) { if (active && current()) { setError(failure); onDenied(failure); } }
    finally { if (active) { setBusy(false); onBusyChange(false); } }
  }); return () => { active = false; controller.abort(); onBusyChange(false); }; }, [gateway, actorId, shipmentId, before, reload, canRecord, current, onDenied, onBusyChange]);
  useEffect(() => { if (review) heading.current?.focus(); }, [review]);
  async function record() {
    if (!alive.current || !current() || locked || pending.current || !review || !canRecord) return;
    pending.current = true; setBusy(true); onBusyChange(true); setError(null);
    try {
      if (recovery.journal.list(actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
      await submitTrackedRequest(ledger, recovery.journal, actorId, 'fulfillment.delivery-attempt.record', 'fulfillment.delivery-attempt.record', review, key => gateway.record(review, key, () => alive.current && current()));
      if (alive.current && current()) { setSaved(true); setReview(null); setOutcome(''); setReason(''); setBefore(null); setReload(value => value + 1); }
    } catch (failure) { if (alive.current && current()) { setError(failure); setReview(null); onDenied(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); onBusyChange(false); recovery.refresh(); } }
  }
  return <section className={common.accountSection} aria-label="Upaya pengiriman" aria-busy={busy}><h3>Upaya pengiriman</h3>
    <p>Catat kunjungan yang belum menyelesaikan serah terima. Penerimaan barang tetap memerlukan konfirmasi penerima.</p>
    {error !== null && <p role="alert">{message(error)}</p>}{busy && <p role="status">Memuat riwayat kunjungan…</p>}
    {offline && <p role="status">Koneksi terputus. Catatan belum dikirim; sambungkan kembali sebelum menyimpan.</p>}
    {saved && <p role="status">Upaya pengiriman tersimpan. QR sebelumnya yang masih berlaku telah dicabut.</p>}
    {history && <><h4>Riwayat kunjungan</h4>{history.items.length === 0 ? <p>Belum ada catatan kunjungan pada halaman ini.</p> : <ol>{history.items.map(item => <li key={item.id}>
      <strong>Kunjungan {item.sequence} · {deliveryOutcomes[item.outcome]}</strong><p>{visitTime(item.occurredAt)} WIB</p><p>{item.reason}</p><p>{item.revokedQrCount} QR dicabut · Dicatat {visitTime(item.recordedAt)} WIB</p>
    </li>)}</ol>}{history.hasMore && <button className={common.secondary} disabled={busy || offline} onClick={() => setBefore(history.items.at(-1)!.sequence)}>Kunjungan lebih lama</button>}</>}
    {(preview?.received || history?.received) && <p role="status">Penerimaan telah dikonfirmasi. Kunjungan baru tidak dapat dicatat untuk kiriman ini.</p>}
    {canRecord && preview && !preview.received && preview.sequence !== '1000' && (review ? <><h4 ref={heading} tabIndex={-1}>Tinjau upaya pengiriman</h4>
      <p><strong>{deliveryOutcomes[review.body.outcome]}</strong> · {visitTime(review.body.occurredAt)} WIB</p><p>{review.body.reason}</p>
      <p>QR penerimaan yang masih berlaku akan dicabut. Buat QR baru saat penerima siap pada kunjungan berikutnya. Catatan ini tidak mengembalikan stok atau mengubah tagihan.</p>
      <button className={common.primary} disabled={locked} onClick={() => void record()}>Simpan upaya pengiriman</button><button className={common.secondary} disabled={locked} onClick={() => setReview(null)}>Kembali ke catatan kunjungan</button>
    </> : <form onSubmit={event => { event.preventDefault(); if (!locked && current()) { try { setReview(prepareDeliveryAttempt(actorId, preview, outcome, jakartaVisitTime(occurredAt), reason.trim())); setError(null); setSaved(false); } catch (failure) { setError(failure); } } }}>
      <label><span id="visit-outcome-label">Hasil kunjungan</span><select aria-labelledby="visit-outcome-label" required value={outcome} disabled={locked} onChange={event => setOutcome(event.target.value)}><option value="">Pilih hasil kunjungan</option>{Object.entries(deliveryOutcomes).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
      <label>Waktu kunjungan (WIB)<input type="datetime-local" step="0.001" required value={occurredAt} disabled={locked} onChange={event => setOccurredAt(event.target.value)} /></label>
      <label>Catatan kunjungan<textarea required maxLength={1000} value={reason} disabled={locked} onChange={event => setReason(event.target.value)} /></label>
      <button className={common.secondary} disabled={locked}>Tinjau upaya pengiriman</button>
    </form>)}
    {preview?.sequence === '1000' && <p>Riwayat mencapai batas kunjungan. Hubungi penanggung jawab toko untuk tindak lanjut.</p>}
    <button className={common.secondary} disabled={busy || offline} onClick={() => { setBefore(null); setReload(value => value + 1); }}>Muat riwayat kunjungan</button>
  </section>;
}
