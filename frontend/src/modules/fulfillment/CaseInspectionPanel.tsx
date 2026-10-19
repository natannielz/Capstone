'use client';

import Link from 'next/link';
import { useEffect, useRef, useState } from 'react';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { submitTrackedRequest, type OperationJournal } from '../administration/operation-journal';
import type { AfterSalesCase } from './case-client';
import { CaseResolutionPanel } from './CaseResolutionPanel';
import { CaseReturnedLotPanel } from './CaseReturnedLotPanel';
import type { ResolutionAction } from './resolution-client';
import { createInspectionGateway, prepareInspection, inspectionIdentities, inspectionConditions, type InspectionLine, type InspectionView } from './inspection-client';
import styles from '../administration/access.module.css';
import { formatDateTime } from '@/lib/format';

export function CaseInspectionPanel({ actorId, owner, returnId, canInspect, resolutionOptions, canReadInvoice = false, fresh, locked, current, freshCurrent, journal, onSettled, onDenied, onBusyChange }: {
  actorId: string; owner: AfterSalesCase; returnId: string; canInspect: boolean; resolutionOptions: readonly ResolutionAction[]; canReadInvoice?: boolean; fresh: boolean; locked: boolean; current: () => boolean; freshCurrent: () => boolean; journal: OperationJournal;
  onSettled: () => void; onDenied: (failure: unknown) => void; onBusyChange: (busy: boolean) => void;
}) {
  const [gateway] = useState(createInspectionGateway), [ledger] = useState(() => new MutationLedger(() => 'ci.' + owner.id + '.' + returnId + '.' + crypto.randomUUID()));
  const [view, setView] = useState<InspectionView | null>(null), [preview, setPreview] = useState(false), [values, setValues] = useState<Record<string, { identity: InspectionLine['identity']; condition: InspectionLine['condition'] | ''; observedIdentity: string; observedLotLabel: string }>>({});
  const [publicNote, setPublicNote] = useState(''), [reason, setReason] = useState(''), [consent, setConsent] = useState(false), [confirm, setConfirm] = useState<ReturnType<typeof prepareInspection> | null>(null);
  const [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null); const alive = useRef(false), pending = useRef(false), heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; onBusyChange(false); }; }, [onBusyChange]);
  useEffect(() => { if (confirm) heading.current?.focus(); }, [confirm]);
  const active = () => alive.current && current(), disabled = locked || busy, canAct = canInspect && fresh && !disabled;
  const product = (receiptId: string, skuId: string) => owner.source.lines.find(line => line.receiptId === receiptId && line.skuId === skuId)!;
  const valueFor = (key: string) => values[key] ?? { identity: 'unverified' as const, condition: '' as const, observedIdentity: '', observedLotLabel: '' };
  function change(key: string, patch: Partial<ReturnType<typeof valueFor>>) { setValues(items => ({ ...items, [key]: { ...valueFor(key), ...patch } })); }
  async function work(task: () => Promise<void>) {
    if (!active() || locked || pending.current) return; pending.current = true; setBusy(true); onBusyChange(true); setError(null);
    try { await task(); } catch (failure) { if (active()) { setConfirm(null); setPreview(false); setError(failure); if (!(failure instanceof AccessFailure && failure.code === 'step_up_required')) onDenied(failure); } }
    finally { pending.current = false; onBusyChange(false); onSettled(); if (alive.current) setBusy(false); }
  }
  return <section aria-label={'Pemeriksaan retur ' + returnId}><h4>Pemeriksaan penerimaan {returnId}</h4>
    <p>Pemeriksa membandingkan barang yang tiba dengan produk pada invoice dan mencatat kondisi fisiknya. Barang tetap dalam karantina setelah pemeriksaan.</p>
    {canInspect && !fresh && <p role="status">Verifikasi dua langkah terbaru diperlukan. <Link href="/identity/account?operation=case.manage&returnPath=%2Ffulfillment%2Fcases">Verifikasi dua langkah untuk pemeriksaan retur</Link></p>}
    {error !== null && <p role="alert">{error instanceof AccessFailure && error.code === 'invalid_return_inspection' ? 'Periksa seluruh barang pada penerimaan ini, pilih identitas dan kondisi, lalu isi catatan.' : accessMessage(error)}</p>}{busy && <p role="status">Memeriksa hasil pemeriksaan retur…</p>}
    <button className={styles.secondary} disabled={disabled} onClick={() => void work(async () => { const value = await gateway.read(actorId, owner, returnId); if (active()) { setView(value); setPreview(false); setConfirm(null); } })}>Lihat hasil pemeriksaan {returnId}</button>
    {canInspect && <button className={styles.secondary} disabled={!canAct} onClick={() => void work(async () => { const value = await gateway.read(actorId, owner, returnId, true); if (active()) { setView(value); setPreview(true); setConfirm(null); setConsent(false); } })}>Periksa barang retur {returnId}</button>}
    {view && !view.inspection && <p>Belum ada hasil pemeriksaan barang untuk penerimaan ini.</p>}
    {view?.inspection && <section aria-label="Hasil pemeriksaan barang"><p>{formatDateTime(view.inspection.inspectedAt)} · Telah diperiksa, tetap karantina</p><p>{view.inspection.publicNote}</p>
      <ul>{view.inspection.lines.map(line => { const item = product(line.receiptId, line.skuId); return <li key={line.receiptId + ':' + line.skuId}>{item.productName}: {line.inspectedBaseUnits} {item.baseUnitCode} · {inspectionIdentities[line.identity]} · {inspectionConditions[line.condition]}</li>; })}</ul></section>}
    {view?.inspection && canInspect && <CaseReturnedLotPanel actorId={actorId} owner={owner} inspection={view} fresh={fresh} locked={disabled} current={current} freshCurrent={freshCurrent} journal={journal} onSettled={onSettled} onDenied={onDenied} onBusyChange={onBusyChange} />}
    {view?.inspection && <CaseResolutionPanel actorId={actorId} owner={owner} inspection={view} allowedActions={resolutionOptions} canReadInvoice={canReadInvoice} fresh={fresh} locked={disabled} current={current} journal={journal} onSettled={onSettled} onDenied={onDenied} onBusyChange={onBusyChange} />}
    {canInspect && preview && view && view.revision === '0' && !confirm && <form onSubmit={event => { event.preventDefault(); if (!canAct) return;
      try { setConfirm(prepareInspection(actorId, owner, view, view.receipt.lines.map(line => { const value = valueFor(line.receiptId + ':' + line.skuId); return { receiptId: line.receiptId, skuId: line.skuId,
        inspectedBaseUnits: line.receivedBaseUnits, identity: value.identity, condition: value.condition as InspectionLine['condition'], observedIdentity: value.observedIdentity.trim(), observedLotLabel: value.observedLotLabel.trim() || null }; }), publicNote.trim(), reason.trim())); setConsent(false); setError(null); }
      catch (failure) { setError(failure); }
    }}><p>Periksa jumlah satuan dasar pada penerimaan ini. Kondisi campuran dan identitas yang belum pasti tetap memerlukan penanganan sebelum keputusan stok berikutnya.</p>
      {view.receipt.lines.map(line => { const key = line.receiptId + ':' + line.skuId, item = product(line.receiptId, line.skuId), value = valueFor(key); return <fieldset key={key} disabled={disabled} style={{ minWidth: 0 }}><legend>{item.productName} · {item.skuCode}</legend>
        <p>{line.receivedBaseUnits} {item.baseUnitCode} diterima. Kemasan pada invoice: {item.packageLabel}, {item.baseUnitsPerPackage} {item.baseUnitCode} per kemasan.</p>
        <label>Identitas barang — {item.productName}<select value={value.identity} onChange={event => change(key, { identity: event.target.value as InspectionLine['identity'] })}>{Object.entries(inspectionIdentities).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
        <label>Kondisi hasil pemeriksaan — {item.productName}<select required value={value.condition} onChange={event => change(key, { condition: event.target.value as InspectionLine['condition'] })}><option value="">Pilih kondisi yang diperiksa</option>{Object.entries(inspectionConditions).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
        <label>Identitas atau label yang diperiksa — {item.productName}<input required maxLength={160} value={value.observedIdentity} onChange={event => change(key, { observedIdentity: event.target.value })} /></label>
        <label>Label lot yang terlihat — {item.productName} (opsional)<input maxLength={160} value={value.observedLotLabel} onChange={event => change(key, { observedLotLabel: event.target.value })} /></label><p>Observasi ini bersifat internal. Label yang terbaca belum membuktikan lot, kedaluwarsa atau biaya stok asal.</p></fieldset>; })}
      <label>Hasil pemeriksaan untuk pelanggan<textarea required maxLength={1000} disabled={disabled} value={publicNote} onChange={event => setPublicNote(event.target.value)} /></label>
      <label>Catatan pemeriksaan internal<textarea required maxLength={1000} disabled={disabled} value={reason} onChange={event => setReason(event.target.value)} /></label><button className={styles.primary} disabled={!canAct}>Tinjau hasil pemeriksaan</button></form>}
    {confirm && <section aria-label="Konfirmasi pemeriksaan retur"><h4 ref={heading} tabIndex={-1}>Catat hasil pemeriksaan barang</h4><p>Keluhan {owner.id} · Penerimaan {returnId} · Invoice {owner.source.number}</p>
      <p>Untuk pelanggan: {confirm.body.publicNote}</p><p>Catatan internal: {confirm.body.reason}</p><ul>{confirm.body.lines.map(line => { const item = product(line.receiptId, line.skuId); return <li key={line.receiptId + ':' + line.skuId}>{item.productName}: {line.inspectedBaseUnits} {item.baseUnitCode} · {inspectionIdentities[line.identity]} · {inspectionConditions[line.condition]}<p>Observasi internal: {line.observedIdentity} · Label lot: {line.observedLotLabel ?? 'Tidak terbaca/tidak ada'}</p></li>; })}</ul>
      <label className={styles.checkbox}><input type="checkbox" checked={consent} disabled={disabled} onChange={event => setConsent(event.target.checked)} /><span>Saya telah memeriksa seluruh barang pada penerimaan ini dan mencatat identitas serta kondisi fisiknya. Barang tetap dalam karantina.</span></label>
      <button className={styles.primary} disabled={!canAct || !consent} onClick={() => void work(async () => { if (journal.list(actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
        await submitTrackedRequest(ledger, journal, actorId, 'fulfillment.case.return_inspect', 'fulfillment.case.return_inspect', confirm, key => gateway.inspect(confirm, key, active));
        const value = await gateway.read(actorId, owner, returnId); if (active()) { setView(value); setPreview(false); setConfirm(null); setValues({}); setPublicNote(''); setReason(''); setConsent(false); }
      })}>Catat pemeriksaan retur</button><button className={styles.secondary} disabled={disabled} onClick={() => { setConfirm(null); setConsent(false); }}>Kembali ke pemeriksaan</button></section>}
  </section>;
}
