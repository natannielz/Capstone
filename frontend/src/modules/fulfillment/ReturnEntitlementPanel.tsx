'use client';

import Link from 'next/link';
import { useEffect, useRef, useState } from 'react';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { submitTrackedRequest, type OperationJournal } from '../administration/operation-journal';
import { orderAmount } from '../ordering/order-quote-client';
import type { AfterSalesCase } from './case-client';
import { inspectionConditions, inspectionIdentities } from './inspection-client';
import { resolutionActions, type ResolutionView } from './resolution-client';
import { createReturnEntitlementGateway, prepareReturnEntitlement, returnEntitlementInput, type ReturnEntitlementView, type ReturnEntitlementPreview } from './return-entitlement-client';
import styles from '../administration/access.module.css';

const rupiah = (amount: string) => orderAmount('IDR', amount);
function message(failure: unknown) {
  if (failure instanceof AccessFailure) {
    if (failure.code === 'invalid_return_entitlement') return 'Isi pajak dan biaya untuk setiap barang dengan dua angka desimal. Pilih 0.00 secara eksplisit jika tidak diajukan, lalu isi alasan pemeriksaan.';
    if (failure.code === 'return_entitlement_policy_unavailable') return 'Prosedur perhitungan retur belum disetujui atau sudah tidak berlaku. Hubungi penanggung jawab Finance.';
    if (failure.code === 'return_entitlement_stale' || failure.code === 'return_entitlement_view_stale') return 'Kapasitas pengajuan berubah. Periksa nilai terbaru dan tinjau kembali.';
    if (failure.code === 'return_entitlement_capacity_exceeded') return 'Jumlah atau nilai telah digunakan pengajuan lain. Muat data terbaru; nilai tidak boleh melebihi bagian invoice yang masih tersedia.';
    if (failure.code === 'independent_person_required') return 'Pengaju nilai finansial harus independen dari pelanggan yang mengajukan keluhan, dengan identitas orang yang masih terverifikasi. Persetujuan Finance berikutnya memerlukan petugas lain.';
  }
  return accessMessage(failure);
}
export function ReturnEntitlementPanel({ actorId, owner, resolution, fresh, locked, current, journal, onSettled, onDenied, onBusyChange }: {
  actorId: string; owner: AfterSalesCase; resolution: ResolutionView; fresh: boolean; locked: boolean; current: () => boolean; journal: OperationJournal;
  onSettled: () => void; onDenied: (failure: unknown) => void; onBusyChange: (busy: boolean) => void;
}) {
  const [gateway] = useState(createReturnEntitlementGateway), [ledger] = useState(() => new MutationLedger(() => 'er.' + owner.id + '.' + resolution.returnId + '.' + resolution.request!.action + '.' + crypto.randomUUID()));
  const [view, setView] = useState<ReturnEntitlementView | null>(null), [values, setValues] = useState<Record<string, { tax: string; charges: string }>>({}), [reason, setReason] = useState('');
  const [shown, setShown] = useState<ReturnEntitlementPreview | null>(null), [confirm, setConfirm] = useState<ReturnType<typeof prepareReturnEntitlement> | null>(null), [consent, setConsent] = useState(false);
  const [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null); const alive = useRef(false), pending = useRef(false), heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; onBusyChange(false); }; }, [onBusyChange]);
  useEffect(() => { if (confirm) heading.current?.focus(); }, [confirm]);
  const active = () => alive.current && current(), disabled = locked || busy, canAct = fresh && !disabled;
  const product = (receiptId: string, skuId: string) => owner.source.lines.find(item => item.receiptId === receiptId && item.skuId === skuId)!;
  const valueFor = (line: ReturnEntitlementView['lines'][number]) => values[line.receiptId + ':' + line.skuId] ?? { tax: line.issuedTax === '0.00' ? '0.00' : '', charges: line.issuedCharges === '0.00' ? '0.00' : '' };
  function change(line: ReturnEntitlementView['lines'][number], patch: Partial<ReturnType<typeof valueFor>>) {
    setValues(items => ({ ...items, [line.receiptId + ':' + line.skuId]: { ...valueFor(line), ...patch } })); setConfirm(null); setShown(null); setConsent(false);
  }
  async function work(task: () => Promise<void>) {
    if (!active() || locked || pending.current) return; pending.current = true; setBusy(true); onBusyChange(true); setError(null);
    try { await task(); } catch (failure) { if (active()) { setConfirm(null); setShown(null); setConsent(false); setError(failure); if (!(failure instanceof AccessFailure && failure.code === 'step_up_required')) onDenied(failure); } }
    finally { pending.current = false; onBusyChange(false); onSettled(); if (alive.current) setBusy(false); }
  }
  return <section aria-label={'Nilai finansial retur ' + resolution.returnId}><h4>Pengajuan nilai {resolutionActions[resolution.request!.action].toLowerCase()}</h4>
    <p>Nilai berasal dari invoice dan barang yang telah diperiksa. Pengajuan ini memerlukan pemeriksaan serta persetujuan Finance independen berikutnya. Pengajuan belum mengubah tagihan, saldo pembayaran, dana refund atau stok.</p>
    {!fresh && <p role="status">Verifikasi dua langkah terbaru diperlukan. <Link href="/identity/account?operation=case.manage&returnPath=%2Ffulfillment%2Fcases">Verifikasi dua langkah untuk pengajuan nilai retur</Link></p>}
    {error !== null && <p role="alert">{message(error)}</p>}{busy && <p role="status">Memeriksa nilai retur…</p>}
    <button className={styles.secondary} disabled={disabled} onClick={() => void work(async () => { const value = await gateway.read(actorId, owner, resolution); if (active()) { setView(value); setConfirm(null); setShown(null); setConsent(false); setValues({}); } })}>Periksa nilai finansial retur {resolution.returnId}</button>
    {view?.request && <section aria-label="Pengajuan nilai retur tercatat"><p role="status">Nilai retur {rupiah(view.request.credit)} tercatat untuk pemeriksaan Finance independen. Tagihan, saldo pembayaran, dana refund dan stok belum berubah.</p>
      <p>{new Date(view.request.requestedAt).toLocaleString('id-ID')} · Menunggu pemeriksaan finansial</p><p>Nilai barang {rupiah(view.request.net)} · Pajak {rupiah(view.request.tax)} · Biaya {rupiah(view.request.charges)}</p></section>}
    {view && !view.request && !confirm && <form onSubmit={event => { event.preventDefault(); if (!canAct) return; void work(async () => {
      const input = returnEntitlementInput(view, { lines: view.lines.map(line => ({ receiptId: line.receiptId, skuId: line.skuId, ...valueFor(line) })) });
      const preview = await gateway.preview(actorId, owner, resolution, view, input);
      const command = prepareReturnEntitlement(actorId, owner, resolution, preview, input, reason.trim()); if (active()) { setView(preview.view); setShown(preview); setConfirm(command); setConsent(false); }
    }); }}><p>Jumlah barang mengikuti pemeriksaan seluruh penerimaan ini. Nilai barang dihitung dari harga dan pembulatan invoice asli. Tentukan pajak dan biaya secara eksplisit sesuai prosedur; gunakan 0.00 jika tidak diajukan.</p>
      {view.lines.map(line => { const item = product(line.receiptId, line.skuId), value = valueFor(line); return <fieldset key={line.receiptId + ':' + line.skuId} disabled={disabled} style={{ minWidth: 0 }}><legend>{item.productName} · {item.skuCode}</legend>
        <p>{line.inspectedBaseUnits} {item.baseUnitCode} diperiksa · {inspectionIdentities[line.identity]} · {inspectionConditions[line.condition]}. Kemasan invoice: {item.packageLabel}, {item.baseUnitsPerPackage} {item.baseUnitCode} per kemasan.</p>
        <p>Invoice: {line.issuedBaseUnits} {item.baseUnitCode} · Nilai barang {rupiah(line.issuedNet)} · Pajak {rupiah(line.issuedTax)} · Biaya {rupiah(line.issuedCharges)}.</p>
        <p>Telah digunakan pengajuan finansial: {line.previouslyAttributedBaseUnits} {item.baseUnitCode} · Nilai barang {rupiah(line.previouslyAttributedNet)} · Pajak {rupiah(line.previouslyAttributedTax)} · Biaya {rupiah(line.previouslyAttributedCharges)}.</p>
        <label>Pajak yang diajukan — {item.productName}<input required inputMode="decimal" pattern="(0|[1-9][0-9]{0,14})[.][0-9]{2}" maxLength={18} value={value.tax} onChange={event => change(line, { tax: event.target.value })} /></label>
        <label>Biaya yang diajukan — {item.productName}<input required inputMode="decimal" pattern="(0|[1-9][0-9]{0,14})[.][0-9]{2}" maxLength={18} value={value.charges} onChange={event => change(line, { charges: event.target.value })} /></label></fieldset>; })}
      <label>Alasan pengajuan nilai finansial<textarea required maxLength={1000} disabled={disabled} value={reason} onChange={event => { setReason(event.target.value); setConsent(false); }} /></label>
      <button className={styles.primary} disabled={!canAct}>Tinjau nilai finansial retur</button></form>}
    {shown && confirm && <section aria-label="Konfirmasi pengajuan nilai retur"><h4 ref={heading} tabIndex={-1}>Ajukan nilai retur untuk pemeriksaan Finance</h4>
      <p>Keluhan {owner.id} · Penerimaan {resolution.returnId} · Invoice {owner.source.number} · {resolutionActions[confirm.body.action]}</p>
      <ul>{shown.lines.map(line => { const item = product(line.receiptId, line.skuId); return <li key={line.receiptId + ':' + line.skuId}>{item.productName}: {line.attributedBaseUnits} {item.baseUnitCode} · Nilai barang {rupiah(line.net)} · Pajak {rupiah(line.tax)} · Biaya {rupiah(line.charges)}</li>; })}</ul>
      <p>Total pengajuan: {rupiah(shown.credit)}. Pengajuan menyisihkan jumlah dan nilai invoice untuk retur ini agar tidak diajukan dua kali. Persetujuan kredit dan reservasi atau pembayaran refund belum dilakukan.</p>
      <details><summary>Prosedur yang berlaku untuk nilai retur</summary><p>Nilai barang: {shown.attributionProcedureReference}</p><p>Pajak: {shown.taxTreatmentReference}</p><p>Biaya: {shown.chargeTreatmentReference}</p><p>Nota kredit: {shown.creditNoteProcedureReference}</p></details>
      <p>Alasan internal: {confirm.body.reason}</p><label className={styles.checkbox}><input type="checkbox" checked={consent} disabled={disabled} onChange={event => setConsent(event.target.checked)} /><span>Saya telah meninjau barang yang diperiksa, nilai invoice asli serta pajak dan biaya yang diajukan. Saya mengajukan nilai ini untuk pemeriksaan Finance independen, bukan menyetujui kredit atau membayar refund.</span></label>
      <button className={styles.primary} disabled={!canAct || !consent} onClick={() => void work(async () => { if (!canAct || !consent) return; if (journal.list(actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
        await submitTrackedRequest(ledger, journal, actorId, 'billing.return.entitlement.request', 'billing.return.entitlement.request', confirm, key => gateway.request(confirm, key, active));
        const value = await gateway.read(actorId, owner, resolution); if (active()) { setView(value); setConfirm(null); setShown(null); setConsent(false); setReason(''); setValues({}); }
      })}>Kirim pengajuan nilai retur</button><button className={styles.secondary} disabled={disabled} onClick={() => { setConfirm(null); setShown(null); setConsent(false); }}>Kembali ke nilai retur</button></section>}
  </section>;
}
