'use client';

import Link from 'next/link';
import { useEffect, useRef, useState } from 'react';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { submitTrackedRequest, type OperationJournal } from '../administration/operation-journal';
import { orderAmount } from '../ordering/order-quote-client';
import { allocationAmount, allocationInvoiceNumber } from './PaymentAllocationPanel';
import { createPaymentAllocationCorrectionGateway, preparePaymentAllocationCorrection, type PaymentAllocationCorrectionView, type PaymentAllocationCorrectionPreview, type PaymentAllocationCorrectionResult } from './payment-allocation-correction-client';
import type { PaymentDraft } from './payment-client';
import type { PaymentVerificationDetail } from './payment-verification-client';
import styles from '../administration/access.module.css';
import invoice from './invoice.module.css';
import { dateTimeFormatter } from '@/lib/format';

const rupiah = (value: string) => orderAmount('IDR', value);
function message(error: unknown) {
  if (error instanceof AccessFailure) {
    if (error.code === 'payment_correction_policy_unavailable') return 'Prosedur koreksi alokasi belum disetujui atau sudah tidak berlaku. Hubungi penanggung jawab Finance.';
    if (error.code === 'payment_reversal_exceeds_allocation') return 'Nominal koreksi melebihi alokasi asli yang masih dapat dikoreksi.';
    if (error.code === 'independent_person_required') return 'Petugas koreksi harus independen dari pengalokasi asli dan pengaju pembayaran, dengan identitas orang yang masih terverifikasi.';
    if (error.code === 'payment_allocation_stale' || error.code === 'payment_allocation_view_stale') return 'Saldo berubah. Muat alokasi asli dan periksa nominal kembali.';
    if (error.code === 'payment_allocation_unavailable' || error.code === 'payment_balance_unavailable') return 'Riwayat dana belum dapat dibuktikan utuh. Hubungi penanggung jawab rekonsiliasi.';
  }
  return accessMessage(error);
}
export function PaymentAllocationCorrectionPanel({ actorId, owner, verified, allowed, fresh, locked, current, journal, onSettled, onDenied, onBusyChange, onCorrected }: {
  actorId: string; owner: PaymentDraft; verified: PaymentVerificationDetail; allowed: boolean; fresh: boolean; locked: boolean; current: () => boolean;
  journal: OperationJournal; onSettled: () => void; onDenied: (failure: unknown) => void; onBusyChange: (busy: boolean) => void; onCorrected: () => void;
}) {
  const [gateway] = useState(createPaymentAllocationCorrectionGateway), [items, setItems] = useState<PaymentAllocationCorrectionView[] | null>(null), [hasMore, setHasMore] = useState(false);
  const [selected, setSelected] = useState<PaymentAllocationCorrectionView | null>(null), [values, setValues] = useState<Record<string, string>>({}), [reason, setReason] = useState('');
  const [shown, setShown] = useState<PaymentAllocationCorrectionPreview | null>(null), [confirm, setConfirm] = useState<ReturnType<typeof preparePaymentAllocationCorrection> | null>(null);
  const [saved, setSaved] = useState<PaymentAllocationCorrectionResult | null>(null), [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null);
  const alive = useRef(false), pending = useRef(false), heading = useRef<HTMLHeadingElement>(null), reviewHeading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; onBusyChange(false); }; }, [onBusyChange]);
  useEffect(() => { if (selected) heading.current?.focus(); }, [selected]);
  useEffect(() => { if (confirm) reviewHeading.current?.focus(); }, [confirm]);
  const active = () => alive.current && current();
  const disabled = locked || busy, canCorrect = allowed && fresh && !disabled;
  async function work(action: () => Promise<void>) {
    if (!active() || locked || pending.current) return; pending.current = true; setBusy(true); onBusyChange(true); setError(null);
    try { await action(); } catch (failure) { if (alive.current) { setError(failure); setShown(null); setConfirm(null); if (!(failure instanceof AccessFailure && failure.code === 'step_up_required')) onDenied(failure); } }
    finally { pending.current = false; onBusyChange(false); onSettled(); if (alive.current) setBusy(false); }
  }
  async function candidates(before: string | null = null) {
    const page = await gateway.candidates(actorId, owner, verified, before);
    if (active()) { setItems(page.items); setHasMore(page.hasMore); setSelected(null); setValues({}); setShown(null); setConfirm(null); }
  }
  async function select(id: string) {
    const value = await gateway.read(actorId, owner, verified, id);
    if (active()) { setSelected(value); setValues({}); setReason(''); setShown(null); setConfirm(null); }
  }
  return <section className={invoice.correctionPanel} aria-label="Koreksi alokasi pembayaran"><h3>Koreksi alokasi asli</h3>
    <p>Pilih alokasi yang salah dan nominal yang dikembalikan ke dana belum dialokasikan. Koreksi mencatat jurnal penyeimbang dan menambah kembali sisa tagihan. Penerimaan bank dan alokasi asli tetap menjadi riwayat.</p>
    <p>Koreksi ini tidak menyetujui kredit atau pengembalian uang kepada pembeli.</p>
    <button className={styles.secondary} disabled={disabled} onClick={() => void work(() => candidates())}>Muat alokasi asli</button>
    {busy && <p role="status">Memeriksa riwayat dan saldo koreksi…</p>}{error !== null && <p role="alert" className={styles.alert}>{message(error)}</p>}
    {allowed && (!fresh || error instanceof AccessFailure && error.code === 'step_up_required') && <p className={styles.alert}>Verifikasi dua langkah terbaru diperlukan. <Link href="/identity/account?operation=payment.allocate&returnPath=%2Fbilling%2Fpayment-reviews">Verifikasi dua langkah untuk koreksi alokasi</Link></p>}
    {saved && <p role="status">Koreksi {rupiah(saved.amount)} tersimpan. Dana belum dialokasikan saat pencatatan: {rupiah(saved.unapplied)}.</p>}
    {items && <><ul className={invoice.candidates}>{items.map(item => <li key={item.originalAllocationId}><strong>Alokasi {item.originalAllocationId}</strong>
      <p>Dicatat {dateTimeFormatter.format(new Date(item.originalRecordedAt))}</p>
      <ul>{item.capacities.map(line => <li key={line.invoiceId}>Invoice {allocationInvoiceNumber(item.view.invoices.find(value => value.invoiceId === line.invoiceId)!)}: alokasi asli {rupiah(line.originallyAllocated)} · Telah dikoreksi {rupiah(line.previouslyReversed)}.</li>)}</ul>
      <button className={styles.secondary} disabled={disabled || !!confirm} onClick={() => void work(() => select(item.originalAllocationId))}>Tinjau alokasi {item.originalAllocationId}</button></li>)}</ul>
      {!items.length && <p>Belum ada alokasi asli untuk pengajuan ini.</p>}
      {hasMore && <button className={styles.secondary} disabled={disabled || !!confirm} onClick={() => void work(() => candidates(items.at(-1)!.originalAllocationId))}>Alokasi lebih lama</button>}</>}
    {selected && <><h4 ref={heading} tabIndex={-1}>Saldo untuk koreksi alokasi {selected.originalAllocationId}</h4><p>{selected.view.buyerName} · Transfer bank {selected.view.transactionId}</p>
      <dl className={invoice.facts}><div><dt>Sudah dialokasikan</dt><dd>{rupiah(selected.view.allocated)}</dd></div><div><dt>Dicadangkan untuk pengembalian</dt><dd>{rupiah(selected.view.reservedForRefund)}</dd></div><div><dt>Dana tersedia</dt><dd>{rupiah(selected.view.available)}</dd></div></dl>
      <form onSubmit={event => { event.preventDefault(); if (!canCorrect) return; void work(async () => {
        const lines = selected.view.invoices.filter(item => Object.hasOwn(values, item.invoiceId)).map(item => ({ invoiceId: item.invoiceId, expectedVersion: item.version, amount: allocationAmount(values[item.invoiceId]) }));
        if (!lines.length) throw new AccessFailure(400, 'invalid_payment_allocation');
        const preview = await gateway.preview(actorId, owner, verified, selected, { expectedReceiptVersion: selected.view.receiptVersion, lines });
        if (active()) { setShown(preview); setConfirm(preparePaymentAllocationCorrection(actorId, preview, reason.trim())); }
      }); }}><ul className={invoice.candidates}>{selected.view.invoices.map(item => {
        const capacity = selected.capacities.find(line => line.invoiceId === item.invoiceId)!;
        return <li key={item.invoiceId}><strong>Invoice {allocationInvoiceNumber(item)}</strong><p>Alokasi asli {rupiah(capacity.originallyAllocated)} · Telah dikoreksi {rupiah(capacity.previouslyReversed)} · Pembayaran sekarang {rupiah(item.paid)} · Kredit {rupiah(item.credited)} · Sisa tagihan {rupiah(item.outstanding)}</p>
          {allowed && <><label><input type="checkbox" checked={Object.hasOwn(values, item.invoiceId)} disabled={!canCorrect || !!confirm || capacity.originallyAllocated === capacity.previouslyReversed || item.paid === '0.00'} onChange={event => {
            setValues(previous => { const next = { ...previous }; if (event.target.checked) next[item.invoiceId] = ''; else delete next[item.invoiceId]; return next; }); setShown(null);
          }} /> Pilih koreksi invoice {allocationInvoiceNumber(item)}</label>
          {Object.hasOwn(values, item.invoiceId) && <div className={invoice.correctionField}><label htmlFor={'allocation-correction-amount-' + item.invoiceId}>Nominal koreksi (IDR)</label><input id={'allocation-correction-amount-' + item.invoiceId} inputMode="decimal" required maxLength={32} value={values[item.invoiceId]} disabled={!canCorrect || !!confirm} onChange={event => { setValues(previous => ({ ...previous, [item.invoiceId]: event.target.value })); setShown(null); }} /></div>}</>}
        </li>;
      })}</ul>{allowed && <><label htmlFor="allocation-correction-reason">Alasan koreksi alokasi</label><textarea id="allocation-correction-reason" required maxLength={1000} value={reason} disabled={!canCorrect || !!confirm} onChange={event => setReason(event.target.value)} />
        {!confirm ? <button className={styles.primary} disabled={!canCorrect}>Periksa koreksi alokasi</button> : shown && <section className={styles.state} aria-label="Konfirmasi koreksi alokasi"><h4 ref={reviewHeading} tabIndex={-1}>Konfirmasi alokasi asli dan nominal koreksi</h4>
          <p>Alokasi asli {shown.originalAllocationId} · Prosedur: {shown.procedureReference}</p><ul>{shown.invoices.map(line => <li key={line.invoiceId}>Invoice {allocationInvoiceNumber(shown.view.invoices.find(item => item.invoiceId === line.invoiceId)!)}: dikoreksi {rupiah(line.amount)} · Sisa tagihan {rupiah(line.remaining)}.</li>)}</ul>
          <p>Jumlah koreksi {rupiah(shown.amount)} · Dana belum dialokasikan setelah koreksi {rupiah(shown.unapplied)}</p><p>{confirm.body.reason}</p>
          <button type="button" className={styles.primary} disabled={!canCorrect} onClick={() => void work(async () => {
            if (journal.list(actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
            const ledger = new MutationLedger(() => 'pc.' + confirm.verificationId + '.' + confirm.receiptId + '.' + confirm.originalAllocationId + '.' + crypto.randomUUID());
            const result = await submitTrackedRequest(ledger, journal, actorId, 'billing.payment.allocation.correct', 'billing.payment.allocation.correct', confirm, key => gateway.correct(confirm, key, active));
            if (active()) { setSaved(result); onCorrected(); } await candidates();
          })}>Simpan koreksi alokasi</button><button type="button" className={styles.secondary} disabled={disabled} onClick={() => { setConfirm(null); setShown(null); heading.current?.focus(); }}>Kembali ke nominal koreksi</button>
        </section>}</>}
      </form></>}
    {!allowed && <p>Koreksi memerlukan kewenangan alokasi dan koreksi jurnal tersendiri, serta petugas yang independen.</p>}
  </section>;
}
