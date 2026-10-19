'use client';

import Link from 'next/link';
import { useEffect, useRef, useState } from 'react';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { submitTrackedRequest, type OperationJournal } from '../administration/operation-journal';
import type { AfterSalesCase } from './case-client';
import { CaseInspectionPanel } from './CaseInspectionPanel';
import type { ResolutionAction } from './resolution-client';
import { createReturnGateway, prepareReturn, returnConditions, type ReturnLine, type ReturnView } from './return-client';
import styles from '../administration/access.module.css';
import { formatDateTime } from '@/lib/format';

export function CaseReturnPanel({ actorId, owner, canReceive, canInspect, resolutionOptions, canReadInvoice = false, fresh, locked, current, freshCurrent, journal, onSettled, onDenied, onBusyChange }: {
  actorId: string; owner: AfterSalesCase; canReceive: boolean; canInspect: boolean; resolutionOptions: readonly ResolutionAction[]; canReadInvoice?: boolean; fresh: boolean; locked: boolean; current: () => boolean; freshCurrent: () => boolean; journal: OperationJournal;
  onSettled: () => void; onDenied: (failure: unknown) => void; onBusyChange: (busy: boolean) => void;
}) {
  const [gateway] = useState(createReturnGateway), [ledger] = useState(() => new MutationLedger(() => 'cr.' + owner.id + '.' + crypto.randomUUID()));
  const [view, setView] = useState<ReturnView | null>(null), [preview, setPreview] = useState(false), [quantities, setQuantities] = useState<Record<string, string>>({}), [conditions, setConditions] = useState<Record<string, ReturnLine['observedCondition']>>({});
  const [publicNote, setPublicNote] = useState(''), [reason, setReason] = useState(''), [consent, setConsent] = useState(false), [confirm, setConfirm] = useState<ReturnType<typeof prepareReturn> | null>(null);
  const [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null); const alive = useRef(false), pending = useRef(false), heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; onBusyChange(false); }; }, [onBusyChange]);
  useEffect(() => { if (confirm) heading.current?.focus(); }, [confirm]);
  const active = () => alive.current && current(), disabled = locked || busy, canAct = canReceive && fresh && !disabled;
  async function work(task: () => Promise<void>) {
    if (!active() || locked || pending.current) return; pending.current = true; setBusy(true); onBusyChange(true); setError(null);
    try { await task(); } catch (failure) { if (active()) { setConfirm(null); setPreview(false); setError(failure); if (!(failure instanceof AccessFailure && failure.code === 'step_up_required')) onDenied(failure); } }
    finally { pending.current = false; onBusyChange(false); onSettled(); if (alive.current) setBusy(false); }
  }
  const product = (receiptId: string, skuId: string) => owner.source.lines.find(item => item.receiptId === receiptId && item.skuId === skuId)!;
  return <section className={styles.state} aria-label="Penerimaan retur"><h3>Barang kembali dan karantina</h3>
    <p>Catat barang yang benar-benar tiba di gudang pengiriman semula. Setiap penerimaan tetap dalam karantina untuk pemeriksaan dan penyelesaian berikutnya.</p>
    {canReceive && !fresh && <p role="status">Verifikasi dua langkah terbaru diperlukan untuk mencatat retur. <Link href="/identity/account?operation=case.manage&returnPath=%2Ffulfillment%2Fcases">Verifikasi dua langkah untuk penerimaan retur</Link></p>}
    {error !== null && <p role="alert">{error instanceof AccessFailure && error.code === 'invalid_returned_stock' ? 'Isi jumlah barang yang tiba, kondisi terlihat dan catatan penerimaan. Jumlah tidak boleh melebihi sisa izin retur.' : accessMessage(error)}</p>}{busy && <p role="status">Memeriksa penerimaan retur…</p>}
    <button className={styles.secondary} disabled={disabled} onClick={() => void work(async () => { const value = await gateway.read(actorId, owner); if (active()) { setView(value); setPreview(false); setConfirm(null); } })}>Perbarui riwayat retur</button>
    {canReceive && <button className={styles.secondary} disabled={!canAct} onClick={() => void work(async () => { const value = await gateway.read(actorId, owner, true); if (active()) { setView(value); setPreview(true); setConfirm(null); setConsent(false); } })}>Periksa penerimaan retur</button>}
    {view && <><ul>{view.limits.map(item => { const original = product(item.receiptId, item.skuId); return <li key={item.receiptId + ':' + item.skuId}>{original.productName}: {item.receivedBaseUnits} dari {item.authorizedBaseUnits} {original.baseUnitCode} telah diterima dalam karantina. Sisa {String(BigInt(item.authorizedBaseUnits) - BigInt(item.receivedBaseUnits))} {original.baseUnitCode}. {original.packageLabel} berisi {original.baseUnitsPerPackage} {original.baseUnitCode}.</li>; })}</ul>
      {view.recent.length === 0 && <p>Belum ada penerimaan retur yang tercatat.</p>}
      <ul aria-label="Riwayat penerimaan retur">{view.recent.map(row => <li key={row.id}><strong>Penerimaan {row.id}</strong><p>{formatDateTime(row.receivedAt)} · Karantina</p><p>{row.publicNote}</p><ul>{row.lines.map(item => { const original = product(item.receiptId, item.skuId); return <li key={item.receiptId + ':' + item.skuId}>{original.productName}: {item.receivedBaseUnits} {original.baseUnitCode} · {returnConditions[item.observedCondition]}</li>; })}</ul>
        <CaseInspectionPanel actorId={actorId} owner={owner} returnId={row.id} canInspect={canInspect} resolutionOptions={resolutionOptions} canReadInvoice={canReadInvoice} fresh={fresh} locked={disabled} current={current} freshCurrent={freshCurrent} journal={journal} onSettled={onSettled} onDenied={onDenied} onBusyChange={onBusyChange} /></li>)}</ul>
      {view.hasEarlier && <p>25 penerimaan terakhir ditampilkan. Jumlah di atas mencakup seluruh penerimaan retur.</p>}</>}
    {canReceive && preview && view && !confirm && view.limits.some(item => BigInt(item.receivedBaseUnits) < BigInt(item.authorizedBaseUnits)) && <form onSubmit={event => { event.preventDefault(); if (!canAct) return;
      try { const lines = view.limits.flatMap(item => { const key = item.receiptId + ':' + item.skuId; return quantities[key] ? [{ receiptId: item.receiptId, skuId: item.skuId, receivedBaseUnits: quantities[key], observedCondition: conditions[key] ?? 'uninspected' }] : []; });
        setConfirm(prepareReturn(actorId, owner, view, lines, publicNote.trim(), reason.trim())); setConsent(false); setError(null); } catch (failure) { setError(failure); }
    }}><p>Isi satuan dasar yang tiba pada penerimaan ini. Barang yang belum tiba dapat dicatat pada penerimaan berikutnya.</p>
      {view.limits.filter(item => BigInt(item.receivedBaseUnits) < BigInt(item.authorizedBaseUnits)).map(item => { const key = item.receiptId + ':' + item.skuId, original = product(item.receiptId, item.skuId); return <fieldset key={key} disabled={disabled} style={{ minWidth: 0 }}><legend>{original.productName} · Gudang asal {item.warehouseId}</legend>
        <label>Jumlah tiba — {original.productName} ({original.baseUnitCode})<input inputMode="numeric" pattern="[1-9][0-9]*" maxLength={19} value={quantities[key] ?? ''} onChange={event => setQuantities(values => ({ ...values, [key]: event.target.value }))} /></label>
        <label>Kondisi terlihat — {original.productName}<select value={conditions[key] ?? 'uninspected'} onChange={event => setConditions(values => ({ ...values, [key]: event.target.value as ReturnLine['observedCondition'] }))}>{Object.entries(returnConditions).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label></fieldset>; })}
      <label>Catatan penerimaan untuk pelanggan<textarea required maxLength={1000} disabled={disabled} value={publicNote} onChange={event => setPublicNote(event.target.value)} /></label><p>Pelanggan dapat membaca catatan ini.</p>
      <label>Catatan petugas retur internal<textarea required maxLength={1000} disabled={disabled} value={reason} onChange={event => setReason(event.target.value)} /></label><button className={styles.primary} disabled={!canAct}>Tinjau penerimaan retur</button></form>}
    {confirm && <section aria-label="Konfirmasi penerimaan retur"><h4 ref={heading} tabIndex={-1}>Terima barang dalam karantina</h4><p>Keluhan {owner.id} · Invoice {owner.source.number}</p>
      <p>Untuk pelanggan: {confirm.body.publicNote}</p><p>Catatan internal: {confirm.body.reason}</p><ul>{confirm.body.lines.map(line => { const original = product(line.receiptId, line.skuId); const limit = view!.limits.find(item => item.receiptId === line.receiptId && item.skuId === line.skuId)!;
        return <li key={line.receiptId + ':' + line.skuId}>{original.productName}: {line.receivedBaseUnits} {original.baseUnitCode} · {returnConditions[line.observedCondition]} · Gudang asal {limit.warehouseId}</li>; })}</ul>
      <label className={styles.checkbox}><input type="checkbox" checked={consent} disabled={disabled} onChange={event => setConsent(event.target.checked)} /><span>Saya memastikan jumlah dan kondisi barang yang benar-benar tiba di gudang asal serta menempatkannya dalam karantina.</span></label>
      <button className={styles.primary} disabled={!canAct || !consent} onClick={() => void work(async () => { if (journal.list(actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
        await submitTrackedRequest(ledger, journal, actorId, 'fulfillment.case.return_receive', 'fulfillment.case.return_receive', confirm, key => gateway.receive(confirm, key, active));
        const value = await gateway.read(actorId, owner, true); if (active()) { setView(value); setPreview(true); setConfirm(null); setQuantities({}); setConditions({}); setPublicNote(''); setReason(''); setConsent(false); }
      })}>Catat penerimaan retur</button><button className={styles.secondary} disabled={disabled} onClick={() => { setConfirm(null); setConsent(false); }}>Kembali ke penerimaan</button></section>}
  </section>;
}
