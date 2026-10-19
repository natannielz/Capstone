'use client';

import Link from 'next/link';
import { useEffect, useRef, useState } from 'react';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { submitTrackedRequest, type OperationJournal } from '../administration/operation-journal';
import type { AfterSalesCase } from './case-client';
import type { InspectionView } from './inspection-client';
import { ReturnEntitlementPanel } from './ReturnEntitlementPanel';
import { createResolutionGateway, prepareResolution, resolutionActions, type ResolutionAction, type ResolutionView } from './resolution-client';
import styles from '../administration/access.module.css';
import { formatDateTime } from '@/lib/format';

export function CaseResolutionPanel({ actorId, owner, inspection, allowedActions, canReadInvoice = false, fresh, locked, current, journal, onSettled, onDenied, onBusyChange }: {
  actorId: string; owner: AfterSalesCase; inspection: InspectionView; allowedActions: readonly ResolutionAction[]; canReadInvoice?: boolean; fresh: boolean; locked: boolean; current: () => boolean; journal: OperationJournal;
  onSettled: () => void; onDenied: (failure: unknown) => void; onBusyChange: (busy: boolean) => void;
}) {
  const [gateway] = useState(createResolutionGateway), [ledger] = useState(() => new MutationLedger(() => 'cq.' + owner.id + '.' + inspection.receipt.id + '.' + crypto.randomUUID()));
  const [view, setView] = useState<ResolutionView | null>(null), [action, setAction] = useState<ResolutionAction>(allowedActions[0] ?? 'replacement'), [preview, setPreview] = useState(false);
  const [publicNote, setPublicNote] = useState(''), [reason, setReason] = useState(''), [consent, setConsent] = useState(false), [confirm, setConfirm] = useState<ReturnType<typeof prepareResolution> | null>(null);
  const [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null); const alive = useRef(false), pending = useRef(false), heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; onBusyChange(false); }; }, [onBusyChange]);
  useEffect(() => { if (confirm) heading.current?.focus(); }, [confirm]);
  const active = () => alive.current && current(), disabled = locked || busy, canAct = allowedActions.includes(action) && fresh && !disabled;
  async function work(task: () => Promise<void>) {
    if (!active() || locked || pending.current) return; pending.current = true; setBusy(true); onBusyChange(true); setError(null);
    try { await task(); } catch (failure) { if (active()) { setConfirm(null); setPreview(false); setError(failure); if (!(failure instanceof AccessFailure && failure.code === 'step_up_required')) onDenied(failure); } }
    finally { pending.current = false; onBusyChange(false); onSettled(); if (alive.current) setBusy(false); }
  }
  return <section aria-label={'Penyelesaian retur ' + inspection.receipt.id}><h4>Ajukan penyelesaian barang</h4>
    <p>Hasil pemeriksaan menjadi dasar pengajuan. Penggantian, kredit tagihan atau pengembalian dana masih memerlukan persetujuan dan penanganan berikutnya.</p>
    {allowedActions.length > 0 && !fresh && <p role="status">Verifikasi dua langkah terbaru diperlukan. <Link href="/identity/account?operation=case.manage&returnPath=%2Ffulfillment%2Fcases">Verifikasi dua langkah untuk pengajuan penyelesaian</Link></p>}
    {error !== null && <p role="alert">{error instanceof AccessFailure && error.code === 'invalid_return_resolution' ? 'Pilih penyelesaian yang tersedia dan isi catatan pengajuan.' : accessMessage(error)}</p>}{busy && <p role="status">Memeriksa pengajuan penyelesaian…</p>}
    <button className={styles.secondary} disabled={disabled} onClick={() => void work(async () => { const value = await gateway.read(actorId, owner, inspection); if (active()) { setView(value); setPreview(false); setConfirm(null); } })}>Lihat pengajuan penyelesaian {inspection.receipt.id}</button>
    {view && !view.request && <p>Belum ada pengajuan penyelesaian untuk penerimaan ini.</p>}
    {view?.request && <section aria-label="Pengajuan penyelesaian barang"><p>{resolutionActions[view.request.action]} · Menunggu persetujuan penyelesaian</p><p>{formatDateTime(view.request.requestedAt)}</p><p>{view.request.publicNote}</p></section>}
    {view?.request && view.request.action !== 'replacement' && canReadInvoice && allowedActions.includes(view.request.action) &&
      <ReturnEntitlementPanel key={view.returnId + ':' + view.request.action} actorId={actorId} owner={owner} resolution={view} fresh={fresh} locked={disabled} current={current} journal={journal} onSettled={onSettled} onDenied={onDenied} onBusyChange={onBusyChange} />}
    {allowedActions.length > 0 && !view?.request && !confirm && <><label>Pilihan penyelesaian<select disabled={disabled} value={action} onChange={event => { setAction(event.target.value as ResolutionAction); setPreview(false); setConfirm(null); }}>{allowedActions.map(value => <option key={value} value={value}>{resolutionActions[value]}</option>)}</select></label>
      <button className={styles.secondary} disabled={!canAct} onClick={() => void work(async () => { const value = await gateway.read(actorId, owner, inspection, action); if (active()) { setView(value); setPreview(true); setConfirm(null); setConsent(false); } })}>Periksa pilihan penyelesaian</button></>}
    {preview && view && view.revision === '0' && !confirm && <form onSubmit={event => { event.preventDefault(); if (!canAct) return;
      try { setConfirm(prepareResolution(actorId, owner, inspection, view, action, publicNote.trim(), reason.trim())); setConsent(false); setError(null); } catch (failure) { setError(failure); }
    }}><p>{resolutionActions[action]} untuk seluruh barang pada penerimaan {inspection.receipt.id} yang telah diperiksa. Persetujuan, jumlah finansial atau pengiriman pengganti ditentukan pada tahap berikutnya.</p>
      <label>Pengajuan penyelesaian untuk pelanggan<textarea required maxLength={1000} disabled={disabled} value={publicNote} onChange={event => setPublicNote(event.target.value)} /></label>
      <label>Catatan pengajuan penyelesaian internal<textarea required maxLength={1000} disabled={disabled} value={reason} onChange={event => setReason(event.target.value)} /></label>
      <button className={styles.primary} disabled={!canAct}>Tinjau pengajuan penyelesaian</button></form>}
    {confirm && <section aria-label="Konfirmasi pengajuan penyelesaian"><h4 ref={heading} tabIndex={-1}>Ajukan penyelesaian untuk persetujuan</h4><p>Keluhan {owner.id} · Penerimaan {inspection.receipt.id} · Invoice {owner.source.number}</p>
      <p>{resolutionActions[confirm.body.action]}</p><p>Untuk pelanggan: {confirm.body.publicNote}</p><p>Catatan internal: {confirm.body.reason}</p>
      <ul>{view!.inspection.lines.map(line => { const product = owner.source.lines.find(item => item.receiptId === line.receiptId && item.skuId === line.skuId)!; return <li key={line.receiptId + ':' + line.skuId}>{product.productName}: {line.inspectedBaseUnits} {product.baseUnitCode} diperiksa. {product.packageLabel}: {product.baseUnitsPerPackage} {product.baseUnitCode} per kemasan.</li>; })}</ul>
      <label className={styles.checkbox}><input type="checkbox" checked={consent} disabled={disabled} onChange={event => setConsent(event.target.checked)} /><span>Saya telah memeriksa hasil pemeriksaan barang dan mengajukan penyelesaian ini untuk persetujuan berikutnya.</span></label>
      <button className={styles.primary} disabled={!canAct || !consent} onClick={() => void work(async () => { if (journal.list(actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
        await submitTrackedRequest(ledger, journal, actorId, 'fulfillment.case.resolution_request', 'fulfillment.case.resolution_request', confirm, key => gateway.request(confirm, key, active));
        const value = await gateway.read(actorId, owner, inspection); if (active()) { setView(value); setPreview(false); setConfirm(null); setPublicNote(''); setReason(''); setConsent(false); }
      })}>Kirim pengajuan penyelesaian</button><button className={styles.secondary} disabled={disabled} onClick={() => { setConfirm(null); setConsent(false); }}>Kembali ke pengajuan penyelesaian</button></section>}
  </section>;
}
