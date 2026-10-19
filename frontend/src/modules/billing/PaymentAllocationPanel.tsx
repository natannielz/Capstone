'use client';

import Link from 'next/link';
import { useEffect, useRef, useState } from 'react';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { submitTrackedRequest, type OperationJournal } from '../administration/operation-journal';
import { orderAmount } from '../ordering/order-quote-client';
import { createPaymentAllocationGateway, preparePaymentAllocation, type PaymentAllocationInvoiceChoice, type PaymentAllocationInvoiceChoices, type PaymentAllocationPreview, type PaymentAllocationResult, type PaymentAllocationView } from './payment-allocation-client';
import type { PaymentDraft } from './payment-client';
import type { PaymentVerificationDetail } from './payment-verification-client';
import styles from '../administration/access.module.css';
import invoice from './invoice.module.css';
import { rupiahAmount } from '@/lib/rupiah-input';

const rupiah = (value: string) => orderAmount('IDR', value);
export const allocationInvoiceNumber = (item: PaymentAllocationView['invoices'][number]) => `${item.numberEntity}/${item.numberChannel}/${item.numberYear}/${item.number.padStart(6, '0')}`;
const number = allocationInvoiceNumber;
const statuses: Record<string, string> = { unpaid: 'Belum dibayar', partial: 'Dibayar sebagian', paid: 'Lunas', credited: 'Dikreditkan', settled: 'Selesai', overdue: 'Lewat jatuh tempo', due: 'Jatuh tempo hari ini', not_due: 'Belum jatuh tempo' };
export function allocationAmount(value: string) {
  // Accepts "1.000,50", "1000,50" or "1000.50"; the API receives exactly two decimals.
  const amount = rupiahAmount(value, true);
  if (amount === null || !/[1-9]/.test(amount)) throw new AccessFailure(400, 'invalid_payment_allocation');
  return amount;
}
function message(error: unknown) {
  if (error instanceof AccessFailure) {
    if (error.code === 'payment_allocation_policy_unavailable') return 'Prosedur alokasi belum disetujui atau sudah tidak berlaku. Hubungi penanggung jawab Finance.';
    if (error.code === 'payment_allocation_stale' || error.code === 'payment_allocation_view_stale') return 'Saldo atau pemeriksaan berubah. Muat saldo terbaru dan periksa alokasi kembali.';
    if (error.code === 'payment_allocation_exceeds_receipt') return 'Jumlah alokasi melebihi dana yang masih tersedia.';
    if (error.code === 'payment_allocation_exceeds_invoice') return 'Jumlah alokasi melebihi sisa tagihan invoice.';
    if (error.code === 'payment_allocation_accounting_mapping_changed') return 'Pemetaan akun pembukuan berbeda dari penerimaan atau invoice asli. Penanggung jawab Finance perlu menyelesaikan pemetaan sebelum alokasi.';
    if (error.code === 'invalid_payment_allocation') return 'Pilih invoice, isi nominal positif (misalnya 1.250.000 atau 1250000,50), dan jelaskan alasan alokasi.';
    if (error.code === 'payment_allocation_unavailable' || error.code === 'payment_balance_unavailable') return 'Riwayat dana belum dapat dibuktikan utuh. Jangan mengalokasikan ulang; hubungi penanggung jawab rekonsiliasi.';
    if (error.code === 'independent_person_required') return 'Alokasi memerlukan pemeriksa independen dan identitas orang yang masih terverifikasi.';
  }
  return accessMessage(error);
}
export function PaymentAllocationPanel({ actorId, owner, verified, allowed, fresh, locked, current, journal, onSettled, onDenied, onBusyChange }: {
  actorId: string; owner: PaymentDraft; verified: PaymentVerificationDetail; allowed: boolean; fresh: boolean; locked: boolean; current: () => boolean;
  journal: OperationJournal; onSettled: () => void; onDenied: (failure: unknown) => void; onBusyChange: (busy: boolean) => void;
}) {
  const [gateway] = useState(createPaymentAllocationGateway), [view, setView] = useState<PaymentAllocationView | null>(null), [shown, setShown] = useState<PaymentAllocationPreview | null>(null);
  const [values, setValues] = useState<Record<string, string>>({}), [reason, setReason] = useState(''), [confirm, setConfirm] = useState<ReturnType<typeof preparePaymentAllocation> | null>(null);
  const [saved, setSaved] = useState<PaymentAllocationResult | null>(null), [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null);
  const [additional, setAdditional] = useState<PaymentAllocationInvoiceChoice[]>([]), [choices, setChoices] = useState<PaymentAllocationInvoiceChoices | null>(null);
  const alive = useRef(false), pending = useRef(false), balanceHeading = useRef<HTMLHeadingElement>(null), reviewHeading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; onBusyChange(false); }; }, [onBusyChange]);
  useEffect(() => { if (view) balanceHeading.current?.focus(); }, [view]);
  useEffect(() => { if (confirm) reviewHeading.current?.focus(); }, [confirm]);
  const active = () => alive.current && current();
  const disabled = locked || busy, canAllocate = allowed && fresh && !disabled;
  async function work(action: () => Promise<void>) {
    if (!active() || locked || pending.current) return; pending.current = true; setBusy(true); onBusyChange(true); setError(null);
    try { await action(); } catch (failure) { if (alive.current) { setError(failure); setShown(null); setConfirm(null); if (!(failure instanceof AccessFailure && failure.code === 'step_up_required')) onDenied(failure); } }
    finally { pending.current = false; onBusyChange(false); onSettled(); if (alive.current) setBusy(false); }
  }
  async function read() {
    setChoices(null);
    const result = await gateway.read(actorId, owner, verified, additional);
    if (active()) { setView(result); setShown(null); setConfirm(null); setValues({}); }
  }
  async function findInvoices(before: string | null = null) {
    if (!allowed || !fresh) return;
    setChoices(null);
    const result = await gateway.choices(actorId, owner, verified, before);
    if (active()) setChoices(result);
  }
  async function addInvoice(item: PaymentAllocationInvoiceChoice) {
    if (!canAllocate || confirm || !view || view.invoices.length >= 100 || view.invoices.some(line => line.invoiceId === item.invoiceId)) return;
    const next = [...additional, item], result = await gateway.read(actorId, owner, verified, next);
    if (active()) { setAdditional(next); setView(result); setShown(null); setConfirm(null); }
  }
  return <section className={invoice.correctionPanel} aria-label="Alokasi pembayaran"><h3>Alokasi dana ke invoice</h3>
    <p>Dana bank dibukukan satu kali. Pilih bagian dana yang digunakan untuk setiap invoice; sisanya tetap tersedia sebagai dana belum dialokasikan.</p>
    <button className={styles.secondary} disabled={disabled} onClick={() => void work(read)}>Muat saldo alokasi</button>
    {busy && <p role="status">Memeriksa saldo dan alokasi…</p>}{error !== null && <p role="alert" className={styles.alert}>{message(error)}</p>}
    {allowed && (!fresh || error instanceof AccessFailure && error.code === 'step_up_required') && <p className={styles.alert}>Verifikasi dua langkah terbaru diperlukan. <Link href="/identity/account?operation=payment.allocate&returnPath=%2Fbilling%2Fpayment-reviews">Verifikasi dua langkah untuk alokasi</Link></p>}
    {saved && <p role="status">Alokasi {rupiah(saved.amount)} tersimpan. Dana belum dialokasikan saat pencatatan: {rupiah(saved.unapplied)}.</p>}
    {view && <><h4 ref={balanceHeading} tabIndex={-1}>Saldo dana dan tagihan</h4><p>{view.buyerName} · Transfer bank {view.transactionId} · {view.bookedDate}</p>
      <dl className={invoice.facts}><div><dt>Dana diterima</dt><dd>{rupiah(view.received)}</dd></div><div><dt>Sudah dialokasikan</dt><dd>{rupiah(view.allocated)}</dd></div>
        <div><dt>Dicadangkan untuk pengembalian</dt><dd>{rupiah(view.reservedForRefund)}</dd></div><div><dt>Dana tersedia</dt><dd>{rupiah(view.available)}</dd></div></dl>
      {allowed && <section aria-label="Pilihan invoice pembeli"><h4>Invoice lain untuk pembeli ini</h4>
        <p>Invoice dari pengajuan tetap tersedia di bawah. Pilihan tambahan berasal dari pihak pembeli dan toko yang sama; pilih invoice dan nominalnya secara terpisah.</p>
        <button type="button" className={styles.secondary} disabled={!canAllocate || !!confirm} onClick={() => void work(() => findInvoices())}>Cari invoice lain pembeli ini</button>
        {choices && <><ul className={invoice.candidates}>{choices.items.map(item => {
          const included = view.invoices.some(line => line.invoiceId === item.invoiceId);
          return <li key={item.invoiceId}><strong>Invoice {item.number}</strong><p>{item.buyerName} · {item.sellerName}</p>
            <p>Total {rupiah(item.total)} · Dibayar {rupiah(item.paid)} · Kredit {rupiah(item.credited)} · Sisa tagihan {rupiah(item.outstanding)}</p>
            <p>Jatuh tempo {item.dueDate}</p>{included ? <p>Sudah ada dalam daftar alokasi.</p> :
              <button type="button" className={styles.secondary} disabled={!canAllocate || !!confirm || view.invoices.length >= 100} onClick={() => void work(() => addInvoice(item))}>Tambahkan invoice {item.number}</button>}</li>;
        })}</ul>{choices.items.length === 0 && <p>{choices.nextBeforeInvoiceId ? 'Tidak ada invoice dengan sisa tagihan pada halaman ini. Lanjutkan ke halaman berikutnya.' : 'Tidak ada invoice lain dengan sisa tagihan pada halaman ini.'}</p>}
          {choices.nextBeforeInvoiceId && <button type="button" className={styles.secondary} disabled={!canAllocate || !!confirm} onClick={() => void work(() => findInvoices(choices.nextBeforeInvoiceId))}>Invoice berikutnya</button>}</>}
        {view.invoices.length >= 100 && <p>Daftar alokasi sudah mencapai batas 100 invoice.</p>}
      </section>}
      <form onSubmit={event => { event.preventDefault(); if (!canAllocate) return; void work(async () => {
        const lines = view.invoices.filter(item => Object.hasOwn(values, item.invoiceId)).map(item => ({ invoiceId: item.invoiceId, expectedVersion: item.version, amount: allocationAmount(values[item.invoiceId]) }));
        if (!lines.length) throw new AccessFailure(400, 'invalid_payment_allocation');
        const result = await gateway.preview(actorId, owner, verified, { expectedReceiptVersion: view.receiptVersion, lines }, view);
        if (active()) { setShown(result); setConfirm(preparePaymentAllocation(actorId, result, reason.trim())); }
      }); }}>
        <ul className={invoice.candidates}>{view.invoices.map(item => <li key={item.invoiceId}>
          <strong>Invoice {number(item)}</strong><p>{statuses[item.paymentState]} · {statuses[item.aging]}{item.daysOverdue ? ` (${item.daysOverdue} hari)` : ''}</p>
          <p>{owner.invoices.some(line => line.invoiceId === item.invoiceId) ? 'Dari pengajuan pembeli' : 'Dipilih petugas dari invoice pembeli yang sama'}</p>
          <p>Total {rupiah(item.total)} · Dibayar {rupiah(item.paid)} · Kredit {rupiah(item.credited)} · Sisa tagihan {rupiah(item.outstanding)}</p>
          {allowed && <><label><input type="checkbox" checked={Object.hasOwn(values, item.invoiceId)} disabled={!canAllocate || !!confirm || item.outstanding === '0.00'} onChange={event => {
            setShown(null); setValues(previous => { const next = { ...previous }; if (event.target.checked) next[item.invoiceId] = ''; else delete next[item.invoiceId]; return next; });
          }} /> Pilih invoice {number(item)}</label>
            {Object.hasOwn(values, item.invoiceId) && <div className={invoice.correctionField}><label htmlFor={'allocation-amount-' + item.invoiceId}>Nominal alokasi (IDR)</label>
              <input id={'allocation-amount-' + item.invoiceId} inputMode="decimal" required maxLength={32} placeholder="Contoh: 1000,00" value={values[item.invoiceId]} disabled={!canAllocate || !!confirm}
                onChange={event => { setShown(null); setValues(previous => ({ ...previous, [item.invoiceId]: event.target.value })); }} /></div>}</>}
        </li>)}</ul>
        {allowed && <><label htmlFor="allocation-reason">Alasan alokasi</label><textarea id="allocation-reason" required maxLength={1000} value={reason} disabled={!canAllocate || !!confirm} onChange={event => setReason(event.target.value)} />
          {!confirm ? <button className={styles.primary} disabled={!canAllocate || view.available === '0.00'}>Periksa alokasi</button> : shown && <section className={styles.state} aria-label="Konfirmasi alokasi pembayaran">
            <h4 ref={reviewHeading} tabIndex={-1}>Konfirmasi invoice dan nominal</h4><p>Prosedur: {shown.procedureReference}</p>
            <ul>{shown.invoices.map(line => <li key={line.invoiceId}>Invoice {number(shown.view.invoices.find(item => item.invoiceId === line.invoiceId)!)}: {rupiah(line.amount)}. Sisa tagihan: {rupiah(line.remaining)}.</li>)}</ul>
            <p>Jumlah alokasi {rupiah(shown.amount)} · Dana tersisa {rupiah(shown.unapplied)}</p><p>{confirm.body.reason}</p>
            <p>Saldo invoice berkurang sesuai nominal yang dipilih. Pencatatan kas asli tetap sama.</p>
            <button type="button" className={styles.primary} disabled={!canAllocate} onClick={() => void work(async () => {
              if (journal.list(actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
              const ledger = new MutationLedger(() => 'pa.' + confirm.verificationId + '.' + confirm.receiptId + '.' + crypto.randomUUID());
              const result = await submitTrackedRequest(ledger, journal, actorId, 'billing.payment.allocate', 'billing.payment.allocate', confirm, key => gateway.send(confirm, key, active));
              if (active()) setSaved(result); await read();
            })}>Simpan alokasi pembayaran</button><button type="button" className={styles.secondary} disabled={disabled} onClick={() => { setConfirm(null); setShown(null); balanceHeading.current?.focus(); }}>Kembali ke nominal</button>
          </section>}</>}
      </form></>}
    {!allowed && <p>Akun ini dapat membaca saldo. Alokasi dana memerlukan kewenangan tersendiri.</p>}
  </section>;
}
