'use client';

import Link from 'next/link';
import { useEffect, useRef, useState } from 'react';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { submitTrackedRequest, type OperationJournal } from '../administration/operation-journal';
import { createCaseGateway, prepareCaseDecision, type AfterSalesCase, type CaseDecision, type CaseDecisionResult } from './case-client';
import styles from '../administration/access.module.css';
import { recordNumber } from '@/lib/document-numbers';

export function CaseDecisionPanel({ actorId, owner, fresh, locked, current, journal, onSaved, onSettled, onDenied, onBusyChange }: {
  actorId: string; owner: AfterSalesCase; fresh: boolean; locked: boolean; current: () => boolean; journal: OperationJournal;
  onSaved: (result: CaseDecisionResult) => Promise<void>; onSettled: () => void; onDenied: (failure: unknown) => void; onBusyChange: (busy: boolean) => void;
}) {
  const [gateway] = useState(createCaseGateway), [ledger] = useState(() => new MutationLedger(() => 'cd.' + owner.id + '.' + crypto.randomUUID()));
  const [review, setReview] = useState<CaseDecision | null>(null), [action, setAction] = useState('authorize_return'), [quantities, setQuantities] = useState<Record<string, string>>({});
  const [reason, setReason] = useState(''), [explanation, setExplanation] = useState(''), [consent, setConsent] = useState(false);
  const [confirm, setConfirm] = useState<ReturnType<typeof prepareCaseDecision> | null>(null), [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null);
  const alive = useRef(false), pending = useRef(false), heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; onBusyChange(false); }; }, [onBusyChange]);
  useEffect(() => { if (confirm) heading.current?.focus(); }, [confirm]);
  const active = () => alive.current && current(), disabled = locked || busy, canAct = fresh && !disabled;
  async function work(task: () => Promise<void>) {
    if (!active() || locked || pending.current) return; pending.current = true; setBusy(true); onBusyChange(true); setError(null);
    try { await task(); } catch (failure) { if (active()) { setConfirm(null); setError(failure); if (!(failure instanceof AccessFailure && failure.code === 'step_up_required')) onDenied(failure); } }
    finally { pending.current = false; onBusyChange(false); onSettled(); if (alive.current) setBusy(false); }
  }
  return <section className={styles.state} aria-label="Pemeriksaan keluhan"><h3>Pemeriksaan dan keputusan</h3>
    <p>Persetujuan dilakukan petugas dengan identitas orang yang berbeda dari pengaju. Izin retur menetapkan barang untuk pemeriksaan kondisi; penyelesaian berikutnya mengikuti hasil pemeriksaan.</p>
    {!fresh && <p role="status">Verifikasi dua langkah terbaru diperlukan. <Link href="/identity/account?operation=case.manage&returnPath=%2Ffulfillment%2Fcases">Verifikasi dua langkah untuk keputusan</Link></p>}
    {error !== null && <p role="alert">{accessMessage(error)}</p>}{busy && <p role="status">Memeriksa keputusan…</p>}
    <button className={styles.secondary} disabled={!canAct} onClick={() => void work(async () => { const value = await gateway.decision(actorId, owner); if (active()) { setReview(value); setConfirm(null); setConsent(false); } })}>Periksa kewenangan keputusan</button>
    {review && review.state === 'requested' && !confirm && <form onSubmit={event => { event.preventDefault(); if (!canAct) return;
      try { const lines = action === 'reject' ? [] : owner.lines.flatMap(line => { const qty = quantities[line.receiptId + ':' + line.skuId]; return qty ? [{ ...line, reportedBaseUnits: qty }] : []; });
        setConfirm(prepareCaseDecision(actorId, owner, review, action, lines, explanation.trim(), reason.trim())); setConsent(false); setError(null); }
      catch (failure) { setError(failure); }
    }}><label>Tindakan pemeriksaan<select disabled={disabled} value={action} onChange={event => setAction(event.target.value)}><option value="authorize_return">Izinkan pengembalian untuk pemeriksaan</option><option value="reject">Tolak pengajuan</option></select></label>
      {action === 'authorize_return' && <><p>Isi satuan dasar yang diizinkan untuk dikembalikan. Kosongkan barang lain. Jumlah dibatasi oleh keluhan dan penerimaan asli.</p>
        {owner.lines.map(line => { const item = owner.source.lines.find(item => item.receiptId === line.receiptId && item.skuId === line.skuId)!; return <label key={line.receiptId + ':' + line.skuId}>Jumlah retur — {item.productName} — bukti {line.receiptId} (dilaporkan {line.reportedBaseUnits} {item.baseUnitCode})
          <input inputMode="numeric" pattern="[1-9][0-9]*" maxLength={19} disabled={disabled} value={quantities[line.receiptId + ':' + line.skuId] ?? ''} onChange={event => setQuantities(values => ({ ...values, [line.receiptId + ':' + line.skuId]: event.target.value }))} /></label>; })}</>}
      <label>Penjelasan keputusan untuk pelanggan<textarea required maxLength={1000} disabled={disabled} value={explanation} onChange={event => setExplanation(event.target.value)} /></label>
      <p>Penjelasan ini dapat dibaca pelanggan. Simpan pertimbangan internal pada catatan pemeriksa.</p>
      <label>Catatan pemeriksa internal<textarea required maxLength={1000} disabled={disabled} value={reason} onChange={event => setReason(event.target.value)} /></label>
      <button className={styles.primary} disabled={!canAct}>Tinjau keputusan keluhan</button></form>}
    {confirm && <section aria-label="Konfirmasi keputusan keluhan"><h4 ref={heading} tabIndex={-1}>{confirm.body.action === 'reject' ? 'Tolak pengajuan' : 'Izinkan pengembalian barang'}</h4>
      <p>Keluhan {recordNumber('case', owner.id)} · Invoice {owner.source.number}</p><p>Untuk pelanggan: {confirm.body.publicExplanation}</p><p>Catatan internal: {confirm.body.reason}</p>
      <ul>{confirm.body.lines.map(line => { const item = owner.source.lines.find(item => item.receiptId === line.receiptId && item.skuId === line.skuId)!; return <li key={line.receiptId + ':' + line.skuId}>{item.productName}: {line.reportedBaseUnits} {item.baseUnitCode} untuk pemeriksaan. Bukti {line.receiptId}.</li>; })}</ul>
      <label className={styles.checkbox}><input type="checkbox" checked={consent} disabled={disabled} onChange={event => setConsent(event.target.checked)} /><span>Saya telah memeriksa pengajuan, jumlah barang dan penjelasan keputusan.</span></label>
      <button className={styles.primary} disabled={!canAct || !consent} onClick={() => void work(async () => {
        if (journal.list(actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
        const saved = await submitTrackedRequest(ledger, journal, actorId, 'fulfillment.case.decide', 'fulfillment.case.decide', confirm, key => gateway.decide(confirm, key, active));
        if (active()) await onSaved(saved);
      })}>Simpan keputusan keluhan</button><button className={styles.secondary} disabled={disabled} onClick={() => { setConfirm(null); setConsent(false); }}>Kembali ke pemeriksaan</button></section>}
  </section>;
}
