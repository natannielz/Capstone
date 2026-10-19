'use client';
import Link from 'next/link';
import { useEffect, useRef, useState } from 'react';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { submitTrackedRequest, type OperationJournal } from '../administration/operation-journal';
import { orderAmount } from '../ordering/order-quote-client';
import type { AfterSalesCase } from './case-client';
import { canVerifyRefundSettlement, createRefundSettlementGateway, prepareRefundSettlement,
  type RefundSettlementPreview, type RefundSettlementView } from './refund-settlement-client';
import styles from '../administration/access.module.css';
import { formatDateTime } from '@/lib/format';

type Props = { actorId: string; owner: AfterSalesCase; actions: readonly string[]; fresh: boolean; locked: boolean; current: () => boolean; freshCurrent: () => boolean;
  journal: OperationJournal; onSettled: () => void; onDenied: (failure: unknown) => void; onBusyChange: (busy: boolean) => void };
const rupiah = (amount: string) => orderAmount('IDR', amount);
function message(failure: unknown) {
  if (failure instanceof AccessFailure) {
    if (failure.code === 'independent_person_required') return 'Pemeriksa debit bank harus berbeda dari pencatat refund dan pengimpor mutasi bank.';
    if (failure.code === 'refund_settlement_policy_unavailable') return 'Prosedur verifikasi debit bank yang disetujui belum tersedia. Hubungi penanggung jawab Finance.';
    if (failure.code === 'refund_settlement_already_verified') return 'Refund ini sudah diverifikasi. Muat ulang hasil yang tersimpan.';
    if (failure.code === 'invalid_refund_settlement') return 'Isi ID impor mutasi bank, nomor baris debit, dan referensi pemeriksaan penerima serta transfer.';
    if (failure.code.startsWith('refund_settlement_') || failure.code.startsWith('bank_transaction_')) return 'Debit atau sumber refund belum dapat diperiksa atau berubah. Muat ulang rincian dan periksa mutasi bank yang asli.';
  }
  return accessMessage(failure);
}
export function RefundSettlementPanel({ actorId, owner, actions, fresh, locked, current, freshCurrent, journal, onSettled, onDenied, onBusyChange }: Props) {
  const [gateway] = useState(createRefundSettlementGateway), [ledgers] = useState(() => new Map<string, MutationLedger>());
  const [payoutId, setPayoutId] = useState(''), [view, setView] = useState<RefundSettlementView | null>(null);
  const [bankImportId, setBankImportId] = useState(''), [bankRowNumber, setBankRowNumber] = useState('');
  const [beneficiaryReference, setBeneficiaryReference] = useState(''), [transferReference, setTransferReference] = useState(''), [reason, setReason] = useState('');
  const [shown, setShown] = useState<RefundSettlementPreview | null>(null), [command, setCommand] = useState<ReturnType<typeof prepareRefundSettlement> | null>(null), [consent, setConsent] = useState(false);
  const [reviewedHash, setReviewedHash] = useState<string | null>(null), [original, setOriginal] = useState<string | null>(null), [error, setError] = useState<unknown>(null), [busy, setBusy] = useState(false);
  const alive = useRef(false), pending = useRef(false), originalUrl = useRef<string | null>(null), heading = useRef<HTMLHeadingElement>(null);
  const active = () => alive.current && current(), canAct = fresh && !locked && !busy;
  function closeOriginal() { if (originalUrl.current) URL.revokeObjectURL(originalUrl.current); originalUrl.current = null; setOriginal(null); }
  function clearReview() { setShown(null); setCommand(null); setConsent(false); }
  useEffect(() => { alive.current = true; return () => { alive.current = false; if (originalUrl.current) URL.revokeObjectURL(originalUrl.current); onBusyChange(false); }; }, [onBusyChange]);
  useEffect(() => { if (command || original || view?.settlement) heading.current?.focus(); }, [command, original, view?.settlement]);
  async function work(task: () => Promise<void>) {
    if (!active() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); onBusyChange(true);
    try { await task(); } catch (failure) { if (active()) { setError(failure); clearReview(); onDenied(failure); } }
    finally { pending.current = false; onBusyChange(false); onSettled(); if (alive.current) setBusy(false); }
  }
  async function read(id: string) {
    const actual = await gateway.read(actorId, owner, id); if (!active()) return;
    closeOriginal(); clearReview(); setReviewedHash(null); setView(actual);
  }
  if (!canVerifyRefundSettlement(actions)) return null;
  return <section aria-label="Verifikasi debit bank refund" style={{ minWidth: 0, overflowWrap: 'anywhere' }}><h3>Verifikasi debit bank refund</h3>
    <p>Periksa bukti transfer yang sudah dicatat dan debit pada mutasi bank asli. Pemeriksaan independen ini mencatat pembayaran dari dana refund yang telah disisihkan.</p>
    {!fresh && <p role="status">Verifikasi dua langkah terbaru diperlukan. <Link href="/identity/account?operation=disbursement.verify&returnPath=%2Ffulfillment%2Fcases">Verifikasi dua langkah untuk memeriksa debit refund</Link></p>}
    {error !== null && <p role="alert">{message(error)}</p>}{busy && <p role="status">Memeriksa debit dan sumber refund…</p>}
    <form onSubmit={event => { event.preventDefault(); if (canAct) void work(() => read(payoutId)); }}>
      <label>ID pencatatan refund<input required inputMode="numeric" pattern="[1-9][0-9]*" maxLength={19} value={payoutId} disabled={locked || busy}
        onChange={event => { setPayoutId(event.target.value); setView(null); closeOriginal(); setReviewedHash(null); clearReview(); }} /></label>
      <p>Gunakan ID dari pencatatan bukti refund untuk keluhan ini.</p><button className={styles.secondary} disabled={!canAct || !payoutId}>Buka refund untuk pemeriksaan bank</button>
    </form>
    {view && <><h4>Refund {view.payout.payoutId} · {rupiah(view.amount)}</h4>
      <p>Penerima: {view.beneficiaryName} · Bank {view.beneficiaryBankCode} · Rekening berakhir {view.beneficiaryAccountSuffix}</p>
      <p>Rekening pembayaran: bank {view.payingBankCode} · Berakhir {view.payingAccountSuffix}</p>
      <p>Tanggal transfer tercatat {view.transferDate} · Referensi {view.transferReference}</p>
      {view.settlement ? <section aria-label="Hasil verifikasi debit refund"><h4 ref={heading} tabIndex={-1}>Debit bank refund terverifikasi</h4>
        <p role="status">Pembayaran refund {rupiah(view.settlement.amount)} telah dicocokkan dengan debit bank secara independen.</p>
        <p>Verifikasi {view.settlement.settlementId} · {formatDateTime(view.settlement.recordedAt)}</p>
        <ul>{view.settlement.receipts.map(row => <li key={row.receiptId}>Penerimaan bank {row.receiptId}: refund dibayar {rupiah(row.paid)}, masih disisihkan {rupiah(row.pending)}, dana tersedia {rupiah(row.available)}.</li>)}</ul>
      </section> : <><p role="status">Bukti refund telah dicatat; pembayaran belum diverifikasi.</p><p>{view.candidateInstructions}</p>
        <Link href="/billing/bank-statements">Periksa impor mutasi bank</Link>
        <button className={styles.secondary} disabled={!canAct} onClick={() => void work(async () => {
          closeOriginal(); const bytes = await gateway.original(actorId, view, active); if (!active()) return;
          const url = URL.createObjectURL(bytes); originalUrl.current = url; setOriginal(url); setReviewedHash(view.evidence.sha256); clearReview();
        })}>Lihat bukti asli untuk verifikasi refund</button>
        {reviewedHash === view.evidence.sha256 && <p role="status">Bukti asli telah dibuka untuk dibandingkan dengan mutasi bank.</p>}
        {!command && <form onSubmit={event => { event.preventDefault(); if (!canAct || !freshCurrent() || reviewedHash !== view.evidence.sha256) return;
          void work(async () => {
            const preview = await gateway.preview(actorId, owner, view, { bankImportId, bankRowNumber: Number(bankRowNumber), beneficiaryVerificationReference: beneficiaryReference.trim(), transferVerificationReference: transferReference.trim() });
            const prepared = prepareRefundSettlement(actorId, owner, view, preview, reason.trim(), reviewedHash);
            if (active() && freshCurrent()) { closeOriginal(); setShown(preview); setCommand(prepared); setConsent(false); }
          });
        }}><label>ID impor mutasi bank untuk debit refund<input required inputMode="numeric" pattern="[1-9][0-9]*" maxLength={19} value={bankImportId} disabled={locked || busy} onChange={event => { setBankImportId(event.target.value); clearReview(); }} /></label>
          <label>Nomor baris debit pada impor<input required type="number" min={2} max={501} step={1} value={bankRowNumber} disabled={locked || busy} onChange={event => { setBankRowNumber(event.target.value); clearReview(); }} /></label>
          <label>Referensi pemeriksaan penerima refund<input required maxLength={256} value={beneficiaryReference} disabled={locked || busy} onChange={event => { setBeneficiaryReference(event.target.value); clearReview(); }} /></label>
          <label>Referensi pemeriksaan transfer dan debit bank<input required maxLength={256} value={transferReference} disabled={locked || busy} onChange={event => { setTransferReference(event.target.value); clearReview(); }} /></label>
          <label>Alasan verifikasi debit refund<textarea required maxLength={1000} value={reason} disabled={locked || busy} onChange={event => { setReason(event.target.value); clearReview(); }} /></label>
          <button className={styles.primary} disabled={!canAct || reviewedHash !== view.evidence.sha256}>Tinjau verifikasi debit refund</button>
        </form>}
      </>}
      <button className={styles.secondary} disabled={!canAct} onClick={() => void work(() => read(view.payout.payoutId))}>Muat hasil verifikasi refund terbaru</button>
    </>}
    {original && <section aria-label="Bukti asli untuk verifikasi refund"><h4 ref={heading} tabIndex={-1}>Bukti transfer refund asli</h4>
      {/* Authenticated original bytes remain in a temporary local object URL. */}
      {/* eslint-disable-next-line @next/next/no-img-element */}
      <img src={original} alt="Bukti transfer refund asli untuk dibandingkan dengan mutasi bank" style={{ maxWidth: '100%', height: 'auto' }} />
      <button className={styles.secondary} onClick={closeOriginal}>Tutup bukti asli refund</button></section>}
    {command && shown && <section aria-label="Konfirmasi verifikasi debit refund"><h4 ref={heading} tabIndex={-1}>Konfirmasi debit bank yang telah diperiksa</h4>
      <p>Invoice {owner.source.number} · Refund {shown.view.payout.payoutId} · Nilai {rupiah(shown.view.amount)}</p>
      <p>Penerima {shown.view.beneficiaryName} · Bank {shown.view.beneficiaryBankCode} · Rekening berakhir {shown.view.beneficiaryAccountSuffix}</p>
      <p>Debit bank: impor {shown.debit.bankImportId}, baris {shown.debit.bankRowNumber}, tanggal {shown.debit.bookedDate}, transaksi {shown.debit.transactionId}, nilai {rupiah(shown.debit.amount)}.</p>
      <p>Rekening bank berakhir {shown.debit.accountDisplay}. Dana refund tetap diblokir dari alokasi atau penggunaan ulang.</p>
      <ul>{shown.receipts.map(row => <li key={row.receiptId}>Penerimaan bank {row.receiptId}: refund dibayar menjadi {rupiah(row.paidAfter)}, masih disisihkan {rupiah(row.pending)}, dana tersedia tetap {rupiah(row.available)}.</li>)}</ul>
      <details><summary>Prosedur dan referensi pemeriksaan</summary><p>Debit bank: {shown.policy.bankDebitVerificationProcedureReference}</p><p>Penerima: {shown.policy.beneficiaryVerificationProcedureReference}</p>
        <p>Pemeriksaan penerima: {command.body.input.beneficiaryVerificationReference}</p><p>Pemeriksaan transfer: {command.body.input.transferVerificationReference}</p></details>
      <p>Alasan: {command.body.reason}</p>
      <label><input type="checkbox" checked={consent} disabled={!canAct} onChange={event => setConsent(event.target.checked)} />Saya telah membandingkan bukti asli, penerima, dan debit bank sesuai prosedur. Saya memeriksa secara independen dari pencatat refund dan pengimpor mutasi.</label>
      <button className={styles.primary} disabled={!canAct || !consent} onClick={() => void work(async () => {
        if (!freshCurrent() || !consent) return; if (journal.list(actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
        const ledger = ledgers.get(command.payoutId) ?? new MutationLedger(() => 'rsv.' + command.payoutId + '.' + crypto.randomUUID()); ledgers.set(command.payoutId, ledger);
        const saved = await submitTrackedRequest(ledger, journal, actorId, 'billing.refund.payout.verify', 'billing.refund.payout.verify', command, key => gateway.verify(command, key, () => active() && freshCurrent()));
        if (active()) { setView({ ...shown.view, settlement: saved }); clearReview(); setReason(''); setBeneficiaryReference(''); setTransferReference(''); }
      })}>Simpan verifikasi debit refund</button>
      <button className={styles.secondary} disabled={locked || busy} onClick={clearReview}>Kembali memeriksa</button>
    </section>}
  </section>;
}
