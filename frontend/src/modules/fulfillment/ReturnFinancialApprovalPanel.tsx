'use client';
import Link from 'next/link';
import { useEffect, useRef, useState } from 'react';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { submitTrackedRequest, type OperationJournal } from '../administration/operation-journal';
import { orderAmount } from '../ordering/order-quote-client';
import { paymentAllocationSchemas } from '../billing/payment-allocation-client';
import type { AfterSalesCase } from './case-client';
import { createReturnGateway, type ReturnView } from './return-client';
import { canApproveReturnFinancial, createReturnFinancialApprovalGateway, prepareReturnFinancialApproval, returnFinancialApprovalInput,
  type ReturnFinancialAction, type ReturnFinancialApprovalView, type ReturnFinancialApprovalPreview } from './return-financial-approval-client';
import styles from '../administration/access.module.css';

const rupiah = (value: string) => orderAmount('IDR', value);
const money = (value: bigint) => (value / BigInt(100)).toString() + '.' + (value % BigInt(100)).toString().padStart(2, '0');
const { cents } = paymentAllocationSchemas;
type Props = { actorId: string; owner: AfterSalesCase; actions: readonly string[]; fresh: boolean; locked: boolean; current: () => boolean; freshCurrent: () => boolean;
  journal: OperationJournal; onSettled: () => void; onDenied: (failure: unknown) => void; onBusyChange: (busy: boolean) => void };
type Selection = { allocation: ReturnFinancialApprovalView['allocations'][number]; receipt: ReturnFinancialApprovalView['receipts'][number]; amount: string };
function message(failure: unknown) {
  if (failure instanceof AccessFailure) {
    if (failure.code === 'independent_person_required') return 'Persetujuan memerlukan petugas Finance yang berbeda dari pengaju keluhan, pengaju nilai finansial, dan petugas yang mencatat atau memeriksa pembayaran sumber.';
    if (failure.code === 'return_financial_policy_unavailable') return 'Prosedur nota kredit dan reservasi refund yang telah disetujui belum tersedia untuk unit ini. Hubungi penanggung jawab Finance.';
    if (failure.code === 'return_financial_already_approved') return 'Penyelesaian finansial ini sudah tercatat. Periksa rincian terbaru sebelum melanjutkan.';
    if (['return_financial_changed', 'return_financial_stale', 'return_financial_balance_unavailable'].includes(failure.code)) return 'Saldo atau sumber finansial berubah. Muat ulang rincian dan tinjau kembali pilihan alokasi sebelum menyetujui.';
    if (failure.code === 'invalid_return_financial_approval') return 'Periksa nilai refund dan alokasi pembayaran asli. Kredit harus sesuai seluruh pengajuan; reservasi tidak boleh melebihi kredit, pembayaran invoice, atau kapasitas alokasi.';
  }
  return accessMessage(failure);
}
export function ReturnFinancialApprovalPanel(props: Props) {
  const { actorId, owner, actions, locked, current, onBusyChange, onDenied, onSettled } = props;
  const [returns] = useState(createReturnGateway), [physical, setPhysical] = useState<ReturnView | null>(null), [returnId, setReturnId] = useState(''), [olderId, setOlderId] = useState('');
  const [action, setAction] = useState<ReturnFinancialAction>('credit'), [opened, setOpened] = useState<{ returnId: string; action: ReturnFinancialAction } | null>(null);
  const [error, setError] = useState<unknown>(null), [busy, setBusy] = useState(false); const alive = useRef(false), pending = useRef(false);
  useEffect(() => { alive.current = true; return () => { alive.current = false; onBusyChange(false); }; }, [onBusyChange]);
  const allowed = (['credit', 'refund'] as const).filter(value => canApproveReturnFinancial(actions, value));
  async function load() {
    if (!current() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); onBusyChange(true);
    try { const value = await returns.read(actorId, owner); if (alive.current && current()) { setPhysical(value); setReturnId(value.recent[0]?.id ?? ''); setOlderId(''); setOpened(null); } }
    catch (failure) { if (alive.current && current()) { setError(failure); onDenied(failure); } }
    finally { pending.current = false; onBusyChange(false); onSettled(); if (alive.current) setBusy(false); }
  }
  if (!allowed.length) return null;
  return <section aria-label="Persetujuan finansial retur independen"><h3>Persetujuan Finance independen</h3>
    <p>Periksa pengajuan nilai yang berasal dari barang retur dan invoice asli. Persetujuan mencatat nota kredit; untuk refund, dana dari alokasi pembayaran asli disisihkan secara bersamaan. Reservasi ini belum mengirim uang kepada pelanggan.</p>
    {error !== null && <p role="alert">{message(error)}</p>}{busy && <p role="status">Memeriksa penerimaan retur…</p>}
    <button className={styles.secondary} disabled={locked || busy} onClick={() => void load()}>Muat penerimaan retur untuk Finance</button>
    {physical && <form onSubmit={event => { event.preventDefault(); const selected = olderId || returnId; if (!locked && !busy && current() && selected && canApproveReturnFinancial(actions, action)) setOpened({ returnId: selected, action }); }}>
      {physical.recent.length ? <label>Penerimaan barang retur<select required value={returnId} disabled={locked || busy || !!olderId} onChange={event => { setReturnId(event.target.value); setOpened(null); }}>
        {physical.recent.map(row => <option key={row.id} value={row.id}>{row.id} · {new Date(row.receivedAt).toLocaleString('id-ID')}</option>)}</select></label> : <p>Belum ada barang retur yang diterima untuk keluhan ini.</p>}
      {physical.hasEarlier && <label>ID penerimaan retur yang lebih lama<input inputMode="numeric" pattern="[1-9][0-9]*" maxLength={19} disabled={locked || busy} value={olderId} onChange={event => { setOlderId(event.target.value); setOpened(null); }} /></label>}
      <label>Penyelesaian yang diajukan<select value={action} disabled={locked || busy} onChange={event => { setAction(event.target.value as ReturnFinancialAction); setOpened(null); }}>{allowed.map(value => <option key={value} value={value}>{value === 'credit' ? 'Nota kredit' : 'Nota kredit dan reservasi refund'}</option>)}</select></label>
      <button className={styles.secondary} disabled={locked || busy || !(olderId || returnId)}>Buka pengajuan finansial</button></form>}
    {opened && <FinancialReview key={opened.returnId + ':' + opened.action} {...props} returnId={opened.returnId} action={opened.action} />}
  </section>;
}
function FinancialReview({ actorId, owner, returnId, action, fresh, locked, current, freshCurrent, journal, onSettled, onDenied, onBusyChange }: Props & { returnId: string; action: ReturnFinancialAction }) {
  const [gateway] = useState(createReturnFinancialApprovalGateway), [ledger] = useState(() => new MutationLedger(() => 'fa.' + owner.id + '.' + returnId + '.' + action + '.' + crypto.randomUUID()));
  const [view, setView] = useState<ReturnFinancialApprovalView | null>(null), [selections, setSelections] = useState<Record<string, Selection>>({}), [reason, setReason] = useState('');
  const [shown, setShown] = useState<ReturnFinancialApprovalPreview | null>(null), [confirm, setConfirm] = useState<ReturnType<typeof prepareReturnFinancialApproval> | null>(null), [consent, setConsent] = useState(false);
  const [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null); const alive = useRef(false), pending = useRef(false), heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; onBusyChange(false); }; }, [onBusyChange]);
  useEffect(() => { if (confirm) heading.current?.focus(); }, [confirm]);
  const active = () => alive.current && current(), currentApproval = () => active() && freshCurrent();
  const disabled = locked || busy, canAct = fresh && !disabled, selected = Object.values(selections);
  async function work(task: () => Promise<void>) {
    if (!active() || locked || pending.current) return; pending.current = true; setBusy(true); onBusyChange(true); setError(null);
    try { await task(); } catch (failure) { if (active()) { setShown(null); setConfirm(null); setConsent(false); setError(failure); if (!(failure instanceof AccessFailure && failure.code === 'step_up_required')) onDenied(failure); } }
    finally { pending.current = false; onBusyChange(false); onSettled(); if (alive.current) setBusy(false); }
  }
  function change(allocation: ReturnFinancialApprovalView['allocations'][number], amount: string) {
    const receipt = view!.receipts.find(value => value.receiptId === allocation.receiptId)!;
    setSelections(previous => { const next = { ...previous }; if (!amount) delete next[allocation.originalAllocationId]; else next[allocation.originalAllocationId] = { allocation, receipt, amount }; return next; });
    setShown(null); setConfirm(null); setConsent(false);
  }
  async function read(before: string | null = null) {
    const value = await gateway.read(actorId, owner, returnId, action, before);
    if (!active()) return;
    if (before && view && (JSON.stringify(value.request) !== JSON.stringify(view.request) || JSON.stringify(value.invoice) !== JSON.stringify(view.invoice) || selected.some(item =>
      value.receipts.some(receipt => receipt.receiptId === item.receipt.receiptId && JSON.stringify(receipt) !== JSON.stringify(item.receipt))))) throw new AccessFailure(409, 'return_financial_changed');
    setView(value); setShown(null); setConfirm(null); setConsent(false); if (!before) setSelections({});
  }
  function reviewedSelection() {
    if (!view) throw new AccessFailure(400, 'invalid_return_financial_approval');
    const allocations = selected.map(item => item.allocation).toSorted((left, right) => BigInt(left.originalAllocationId) > BigInt(right.originalAllocationId) ? -1 : 1);
    const receipts = [...new Map(selected.map(item => [item.receipt.receiptId, item.receipt])).values()];
    const source = { ...view, allocations, receipts, hasMore: false, beforeAllocationId: allocations.at(-1)?.originalAllocationId ?? null };
    const input = returnFinancialApprovalInput(source, { expectedInvoiceVersion: view.invoice.version, releases: selected.map(item => ({ originalAllocationId: item.allocation.originalAllocationId, expectedReceiptVersion: item.receipt.version, amount: item.amount })) });
    return { source, input };
  }
  const selectedTotal = selected.every(item => paymentAllocationSchemas.positive.safeParse(item.amount).success) ? money(selected.reduce((sum, item) => sum + cents(item.amount), BigInt(0))) : null;
  return <section aria-label={'Pemeriksaan finansial penerimaan ' + returnId}><h4>{action === 'credit' ? 'Nota kredit' : 'Nota kredit dan reservasi refund'} · Retur {returnId}</h4>
    {!fresh && <p role="status">Verifikasi dua langkah terbaru diperlukan untuk persetujuan. <Link href={'/identity/account?operation=' + action + '.approve&returnPath=%2Ffulfillment%2Fcases'}>Verifikasi dua langkah untuk persetujuan Finance</Link></p>}
    {error !== null && <p role="alert">{message(error)}</p>}{busy && <p role="status">Memeriksa sumber dan saldo finansial…</p>}
    <button className={styles.secondary} disabled={disabled} onClick={() => void work(() => read())}>Periksa pengajuan dan saldo terbaru</button>
    {view && <><p>Invoice {owner.source.number} · {view.invoice.buyerName} · Total {rupiah(view.invoice.issued)} · Dibayar {rupiah(view.invoice.paid)} · Dikreditkan {rupiah(view.invoice.credited)} · Sisa {rupiah(view.invoice.outstanding)}</p>
      <p>Pengajuan tetap: {rupiah(view.request.credit)} · Nilai barang {rupiah(view.request.net)} · Pajak {rupiah(view.request.tax)} · Biaya {rupiah(view.request.charges)}</p>
      <ul>{view.request.lines.map(line => { const item = owner.source.lines.find(item => item.receiptId === line.receiptId && item.skuId === line.skuId)!; return <li key={line.receiptId + ':' + line.skuId}>{item.productName}: {line.attributedBaseUnits} {item.baseUnitCode} · Nilai {rupiah(line.net)} · Pajak {rupiah(line.tax)} · Biaya {rupiah(line.charges)}</li>; })}</ul>
      {view.approval ? <section aria-label="Persetujuan finansial tercatat"><p role="status">Nota kredit {rupiah(view.approval.credit)} tercatat. {view.approval.action === 'refund' && <>Dana refund {rupiah(view.approval.reservedRefund)} telah disisihkan; uang belum dikirim.</>}</p><p>{new Date(view.approval.recordedAt).toLocaleString('id-ID')} · Persetujuan {view.approval.financialApprovalId}</p></section>
        : !confirm && <form onSubmit={event => { event.preventDefault(); if (!canAct || !currentApproval()) return; void work(async () => {
          const { source, input } = reviewedSelection(), preview = await gateway.preview(actorId, owner, source, input);
          const command = prepareReturnFinancialApproval(actorId, owner, returnId, action, preview, input, reason.trim()); if (currentApproval()) { setShown(preview); setConfirm(command); setConsent(false); }
        }); }}>
          {action === 'refund' && <><p>Pilih alokasi pembayaran asli untuk dana refund. Maksimal 100 alokasi per persetujuan; pilihan tetap tersimpan di halaman ini saat membuka alokasi lebih lama.</p>
            {view.receipts.map(receipt => <p key={receipt.receiptId}>Penerimaan bank {receipt.receiptId} · Diterima {rupiah(receipt.received)} · Dialokasikan {rupiah(receipt.allocated)} · Refund tersisih {rupiah(receipt.reservedForRefund)} · Tersedia {rupiah(receipt.available)}</p>)}
            {view.allocations.map(allocation => <fieldset key={allocation.originalAllocationId} disabled={disabled || selected.length >= 100 && !selections[allocation.originalAllocationId]} style={{ minWidth: 0 }}><legend>Alokasi {allocation.originalAllocationId} · Penerimaan bank {allocation.receiptId}</legend>
              <p>Semula {rupiah(allocation.originallyAllocated)} · Telah dilepas {rupiah(allocation.previouslyReleased)} · Kapasitas {rupiah(allocation.available)}</p>
              <label>Nilai refund dari alokasi {allocation.originalAllocationId}<input inputMode="decimal" pattern="(0|[1-9][0-9]{0,14})[.][0-9]{2}" maxLength={18} value={selections[allocation.originalAllocationId]?.amount ?? ''} onChange={event => change(allocation, event.target.value)} /></label></fieldset>)}
            {!view.allocations.length && <p>Belum ada alokasi pembayaran asli pada halaman ini.</p>}
            {view.hasMore && <button type="button" className={styles.secondary} disabled={disabled} onClick={() => void work(() => read(view.beforeAllocationId))}>Alokasi pembayaran lebih lama</button>}
            <p>{selected.length} alokasi dipilih{selectedTotal !== null && <> · Reservasi refund {rupiah(selectedTotal)}</>}</p>
            {selected.length > 0 && <ul>{selected.map(item => <li key={item.allocation.originalAllocationId}>Alokasi {item.allocation.originalAllocationId} · {item.amount} <button type="button" className={styles.secondary} disabled={disabled} onClick={() => { setSelections(previous => { const next = { ...previous }; delete next[item.allocation.originalAllocationId]; return next; }); }}>Hapus pilihan alokasi {item.allocation.originalAllocationId}</button></li>)}</ul>}
          </>}
          <label>Alasan persetujuan Finance<textarea required maxLength={1000} disabled={disabled} value={reason} onChange={event => { setReason(event.target.value); setConsent(false); }} /></label>
          <button className={styles.primary} disabled={!canAct || action === 'refund' && !selected.length}>Tinjau persetujuan finansial</button></form>}
    </>}
    {shown && confirm && <section aria-label="Konfirmasi persetujuan Finance"><h4 ref={heading} tabIndex={-1}>Setujui nota kredit{action === 'refund' && ' dan reservasi refund'}</h4>
      <p>Keluhan {owner.id} · Retur {returnId} · Invoice {owner.source.number} · Kredit {rupiah(shown.credit)} · Refund tersisih {rupiah(shown.reservedRefund)} · Sisa invoice {rupiah(shown.remainingInvoice)}</p>
      <ul>{shown.releases.map(line => <li key={line.originalAllocationId}>Alokasi pembayaran asli {line.originalAllocationId}: {rupiah(line.amount)}</li>)}</ul>
      {shown.receipts.map(receipt => <p key={receipt.receiptId}>Penerimaan bank {receipt.receiptId}: {rupiah(receipt.released)} dipindahkan langsung dari alokasi ke reservasi refund · Total reservasi {rupiah(receipt.reservedForRefund)} · Dana tersedia tetap {rupiah(receipt.available)}</p>)}
      <details><summary>Prosedur Finance yang berlaku</summary><p>Nota kredit: {shown.creditNoteProcedureReference}</p><p>Pajak: {shown.taxTreatmentReference}</p><p>Biaya: {shown.chargeTreatmentReference}</p><p>Reservasi refund: {shown.refundReservationProcedureReference}</p></details>
      <p>Alasan internal: {confirm.body.reason}</p><label className={styles.checkbox}><input type="checkbox" checked={consent} disabled={disabled} onChange={event => setConsent(event.target.checked)} /><span>Saya telah memeriksa barang retur, nilai invoice, pengajuan dan sumber pembayaran. Saya menyetujui nota kredit serta reservasi yang ditampilkan; persetujuan ini belum membayar refund.</span></label>
      <button className={styles.primary} disabled={!canAct || !consent} onClick={() => void work(async () => { if (!canAct || !consent || !currentApproval()) return; if (journal.list(actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
        await submitTrackedRequest(ledger, journal, actorId, 'billing.return.financial.approve', 'billing.return.financial.approve', confirm, key => gateway.approve(confirm, key, currentApproval));
        await read(); if (active()) { setReason(''); setSelections({}); }
      })}>Setujui persetujuan Finance</button><button className={styles.secondary} disabled={disabled} onClick={() => { setShown(null); setConfirm(null); setConsent(false); }}>Kembali ke pemeriksaan</button></section>}
  </section>;
}
