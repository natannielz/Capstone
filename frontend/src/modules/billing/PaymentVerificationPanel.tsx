'use client';

import Link from 'next/link';
import { PaymentAllocationPanel } from './PaymentAllocationPanel';
import { PaymentAllocationCorrectionPanel } from './PaymentAllocationCorrectionPanel';
import { useEffect, useRef, useState } from 'react';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { submitTrackedRequest, type OperationJournal } from '../administration/operation-journal';
import { createBankReceiptGateway, type BankReceiptView } from './bank-receipt-client';
import { createPaymentVerificationGateway, preparePaymentVerification, type PaymentVerificationDetail, type PaymentVerificationPreview } from './payment-verification-client';
import { orderAmount } from '../ordering/order-quote-client';
import type { PaymentDraft } from './payment-client';
import styles from '../administration/access.module.css';
import invoice from './invoice.module.css';

const labels: Record<string, string> = { amount: 'Nominal', currency: 'Mata uang', transfer_date: 'Tanggal transfer dan pembukuan', transfer_time: 'Waktu dan zona transfer', recipient: 'Nama penerima', account: 'Rekening tujuan', bank: 'Nama atau kode bank', reference: 'Referensi transfer' };
function message(error: unknown) {
  if (error instanceof AccessFailure) {
    if (error.code === 'payment_verification_facts_mismatch') return 'Nominal pengajuan dan bukti, mata uang IDR, serta rekening pada bukti harus sesuai dengan penerimaan bank. Periksa koreksi; jangan menebak angka yang tidak terbaca.';
    if (error.code === 'payment_verification_policy_unavailable') return 'Prosedur verifikasi pembayar belum disetujui atau sudah tidak berlaku. Hubungi penanggung jawab Finance.';
    if (error.code === 'payment_manual_review_policy_unavailable') return 'Prosedur pengecualian manual sudah tidak berlaku. Jika OCR berhasil dipulihkan, pilih hasil OCR dan periksa penerimaan lagi. Hubungi penanggung jawab Finance untuk prosedur manual.';
    if (error.code === 'payment_verification_ocr_required') return 'Hasil OCR yang berhasil belum tersedia. Muat hasil OCR terbaru; pengecualian manual hanya dapat dipakai melalui prosedur yang disetujui.';
    if (error.code === 'payment_manual_review_required') return 'Belum ada pemeriksaan manual yang tersimpan untuk bukti ini.';
    if (error.code === 'payment_verification_changed' || error.code === 'payment_already_verified') return 'Pemeriksaan berubah atau sudah diverifikasi. Muat hasil terbaru sebelum melanjutkan.';
    if (error.code === 'payment_receipt_payer_conflict') return 'Penerimaan ini sudah dikaitkan dengan pihak lain. Hubungi penanggung jawab rekonsiliasi.';
    if (error.code === 'independent_person_required') return 'Verifikator harus merupakan orang terverifikasi yang berbeda dari pembuat dan pengunggah pengajuan.';
    if (error.code === 'invalid_payment_verification') return 'Lengkapi pemeriksaan pembayar, ketiga pernyataan, alasan dan penyelesaian setiap perbedaan.';
  }
  return accessMessage(error);
}
export function PaymentVerificationPanel({ actorId, owner, allowed, allocationAllowed, allocationCorrectionAllowed, fresh, locked, current, journal, onSettled, onDenied, onBusyChange, manualReview }: {
  actorId: string; owner: PaymentDraft; allowed: boolean; allocationAllowed: boolean; allocationCorrectionAllowed: boolean; fresh: boolean; locked: boolean; current: () => boolean;
  journal: OperationJournal; onSettled: () => void; onDenied: (failure: unknown) => void; onBusyChange: (busy: boolean) => void; manualReview?: React.ReactNode;
}) {
  const [gateway] = useState(createPaymentVerificationGateway), [bank] = useState(createBankReceiptGateway);
  const [allocationRevision, setAllocationRevision] = useState(0);
  const [saved, setSaved] = useState<PaymentVerificationDetail | null>(null), [checked, setChecked] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null);
  const [page, setPage] = useState<{ items: BankReceiptView[]; hasMore: boolean } | null>(null), [shown, setShown] = useState<PaymentVerificationPreview | null>(null);
  const [reviewSource, setReviewSource] = useState<'ocr' | 'manual' | null>(null);
  const [payerReference, setPayerReference] = useState(''), [reason, setReason] = useState(''), [image, setImage] = useState(false), [bankChecked, setBankChecked] = useState(false), [payer, setPayer] = useState(false);
  const [resolutions, setResolutions] = useState<Record<string, string>>({}), [confirm, setConfirm] = useState<ReturnType<typeof preparePaymentVerification> | null>(null);
  const alive = useRef(false), pending = useRef(false), heading = useRef<HTMLHeadingElement>(null), reviewHeading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; onBusyChange(false); }; }, [onBusyChange]);
  useEffect(() => { if (shown) heading.current?.focus(); }, [shown]);
  useEffect(() => { if (confirm) reviewHeading.current?.focus(); }, [confirm]);
  const active = () => alive.current && current();
  async function work(action: () => Promise<void>) {
    if (!active() || locked || pending.current) return; pending.current = true; setBusy(true); onBusyChange(true); setError(null);
    try { await action(); } catch (failure) { if (alive.current) { setError(failure); if (!(failure instanceof AccessFailure && failure.code === 'step_up_required')) onDenied(failure); } }
    finally { pending.current = false; onBusyChange(false); onSettled(); if (alive.current) setBusy(false); }
  }
  async function read() { const result = await gateway.read(actorId, owner); if (active()) { setSaved(result); setChecked(true); setShown(null); setConfirm(null); setPage(null); } }
  async function receipts(after: string | null) { const result = await bank.list(actorId, after); if (active()) setPage(result); }
  async function choose(receiptId: string) {
    const result = await gateway.preview(actorId, owner, receiptId, reviewSource);
    if (active()) { setShown(result); setConfirm(null); setPayerReference(''); setReason(''); setImage(false); setBankChecked(false); setPayer(false); setResolutions({}); }
  }
  const disabled = locked || busy, canVerify = allowed && fresh && !disabled;
  return <section className={invoice.correctionPanel} aria-label="Verifikasi pembayaran"><h3>Verifikasi pembayaran</h3>
    <p>Hubungkan pengajuan dan bukti asli dengan dana yang telah dikonfirmasi melalui bank. Pembayar harus diverifikasi melalui prosedur Finance yang disetujui.</p>
    <button className={styles.secondary} disabled={disabled} onClick={() => void work(read)}>Muat hasil verifikasi</button>
    {busy && <p role="status">Memeriksa verifikasi…</p>}{error !== null && <p role="alert" className={styles.alert}>{message(error)}</p>}
    {allowed && (!fresh || error instanceof AccessFailure && error.code === 'step_up_required') && <p className={styles.alert}>Verifikasi dua langkah terbaru diperlukan. <Link href="/identity/account?operation=payment.verify&returnPath=%2Fbilling%2Fpayment-reviews">Verifikasi dua langkah untuk pembayaran</Link></p>}
    {checked && !saved && <p>Belum ada verifikasi pembayaran yang tersimpan.</p>}
    {!saved && manualReview}
    {saved ? <section className={styles.state} aria-label="Hasil verifikasi pembayaran"><h4>Pembayaran terverifikasi</h4>
      <p>Penerimaan bank {saved.result.receiptId} · Pemeriksaan versi {saved.result.reviewRevision} · Verifikator {saved.verifiedBy}</p>
      <p>Sumber pemeriksaan: {saved.reviewSource === 'manual' ? 'Pengecualian OCR dengan pemeriksaan manual' : 'Hasil OCR dan koreksi petugas'}</p>
      <p>{new Intl.DateTimeFormat('id-ID', { dateStyle: 'medium', timeStyle: 'short', timeZone: 'Asia/Jakarta' }).format(new Date(saved.result.recordedAt))}</p>
      <p>Referensi pemeriksaan pembayar: {saved.payerVerificationReference}</p><p>Alasan: {saved.reason}</p>
      <ul>{saved.resolutions.map(item => <li key={item.field}>{labels[item.field]}: {item.reason}</li>)}</ul>
      <p>Pengaitan menetapkan pemilik dana. Periksa saldo dan alokasi invoice melalui bagian di bawah ini.</p>
    </section> : allowed && <>
      <label htmlFor="verification-review-source">Sumber isian untuk pemeriksaan</label>
      <select id="verification-review-source" value={reviewSource ?? ''} disabled={!canVerify} onChange={event => {
        setReviewSource(event.target.value === 'ocr' ? 'ocr' : event.target.value === 'manual' ? 'manual' : null);
        setShown(null); setConfirm(null); setPayerReference(''); setReason(''); setImage(false); setBankChecked(false); setPayer(false); setResolutions({}); setError(null);
      }}><option value="">Pemeriksaan terkini</option><option value="ocr">Hasil OCR dan koreksi petugas</option><option value="manual">Pengecualian manual yang tersimpan</option></select>
      <p>Jika OCR pulih setelah pengecualian, pilih hasil OCR lalu periksa penerimaan kembali. Pilihan ini mempertahankan riwayat manual dan memerlukan keputusan baru sebelum pengaitan dana.</p>
      <button className={styles.secondary} disabled={!canVerify || !checked} onClick={() => void work(() => receipts(null))}>Pilih penerimaan bank</button>
      <p>Jika dana belum dikonfirmasi, buka <Link href="/billing/bank-receipts">penerimaan bank</Link>. Nama atau referensi yang mirip bukan bukti identitas transfer.</p>
      {page && <><ul className={invoice.candidates}>{page.items.map(item => <li key={item.receiptId}><strong>Penerimaan {item.receiptId} · {orderAmount('IDR', item.amount)}</strong>
        <p>{item.bookedDate} · Identitas bank {item.transactionId}</p><button className={styles.secondary} disabled={!canVerify || confirm !== null} onClick={() => void work(() => choose(item.receiptId))}>Periksa penerimaan {item.receiptId}</button></li>)}</ul>
        {page.items.length === 0 && <p>Belum ada penerimaan bank yang dapat diperiksa.</p>}
        {page.hasMore && <button className={styles.secondary} disabled={!canVerify || confirm !== null} onClick={() => void work(() => receipts(page.items.at(-1)!.receiptId))}>Penerimaan berikutnya</button>}</>}
      {shown && <form onSubmit={event => { event.preventDefault(); if (!canVerify) return; setError(null); try {
        setConfirm(preparePaymentVerification(actorId, shown, payerReference.trim(), image, bankChecked, payer,
          shown.discrepancies.map(item => ({ field: item.field, reason: resolutions[item.field]?.trim() ?? '' })), reason.trim()));
      } catch (failure) { setError(failure); } }}>
        <h4 ref={heading} tabIndex={-1}>Tinjau pengaitan dana</h4><p>{shown.buyerName} · Nama yang dinyatakan: {shown.declaredPayerName}</p>
        <dl className={invoice.facts}><div><dt>Dana bank</dt><dd>{orderAmount('IDR', shown.amount)}</dd></div><div><dt>Identitas transfer bank</dt><dd>{shown.transactionId}</dd></div>
          <div><dt>Rekening penerimaan</dt><dd>{shown.bankCode} · {shown.accountNumber} · {shown.accountHolder}</dd></div><div><dt>Tanggal pembukuan</dt><dd>{shown.bookedDate}</dd></div>
          <div><dt>Prosedur pembayar yang disetujui</dt><dd>{shown.payerVerificationProcedureReference}</dd></div><div><dt>Pemeriksaan bukti</dt><dd>Versi {shown.reviewRevision}</dd></div></dl>
        <h5>Nilai dari bukti yang diperiksa</h5><ul>{shown.fields.map(item => <li key={item.field}>{labels[item.field]}: {item.value ?? 'Belum diketahui'}</li>)}</ul>
        <h5>Perbedaan yang perlu dijelaskan</h5>{shown.discrepancies.map(item => <div key={item.field} className={invoice.correctionField}>
          <label htmlFor={'verification-' + item.field}>{labels[item.field]} — penyelesaian</label>
          <p>Pengajuan: {item.declaration ?? 'Tidak tersedia'} · Bukti: {item.image ?? 'Tidak diketahui'} · Bank: {item.bank ?? 'Tidak tersedia'}</p>
          <textarea id={'verification-' + item.field} required maxLength={1000} value={resolutions[item.field] ?? ''} disabled={disabled || !!confirm} onChange={event => setResolutions(values => ({ ...values, [item.field]: event.target.value }))} />
        </div>)}
        <label htmlFor="verification-payer-reference">Referensi pemeriksaan identitas pembayar</label><input id="verification-payer-reference" required maxLength={256} value={payerReference} disabled={disabled || !!confirm} onChange={event => setPayerReference(event.target.value)} />
        <p>Gunakan sumber yang ditetapkan prosedur. Kesamaan nama saja tidak menetapkan bahwa dana milik organisasi ini.</p>
        <label><input type="checkbox" checked={image} disabled={disabled || !!confirm} onChange={event => setImage(event.target.checked)} /> Saya telah memeriksa bukti asli yang tepat.</label>
        <label><input type="checkbox" checked={bankChecked} disabled={disabled || !!confirm} onChange={event => setBankChecked(event.target.checked)} /> Saya telah memeriksa sumber bank dan penerimaan yang dipilih.</label>
        <label><input type="checkbox" checked={payer} disabled={disabled || !!confirm} onChange={event => setPayer(event.target.checked)} /> Kepemilikan dana untuk organisasi pembeli telah diverifikasi melalui prosedur yang disetujui.</label>
        <label htmlFor="verification-reason">Alasan verifikasi</label><textarea id="verification-reason" required maxLength={1000} value={reason} disabled={disabled || !!confirm} onChange={event => setReason(event.target.value)} />
        {!confirm ? <button className={styles.primary} disabled={!canVerify}>Periksa keputusan verifikasi</button> : <section className={styles.state} aria-label="Konfirmasi verifikasi pembayaran">
          <h4 ref={reviewHeading} tabIndex={-1}>Konfirmasi pengaitan yang ditinjau</h4><p>Pengajuan {owner.id} akan dikaitkan dengan penerimaan {shown.receiptId}, sebesar {orderAmount('IDR', shown.amount)} untuk {shown.buyerName}.</p>
          <p>{confirm.body.reason}</p><p>Kas tidak ditambahkan lagi. Saldo invoice belum berkurang. Koreksi bukti yang diterima akan dikunci sebagai riwayat keputusan.</p>
          <button type="button" className={styles.primary} disabled={!canVerify} onClick={() => void work(async () => {
            if (journal.list(actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
            const ledger = new MutationLedger(() => 'pv.' + confirm.declarationId + '.' + confirm.body.receiptId + '.' + crypto.randomUUID());
            await submitTrackedRequest(ledger, journal, actorId, 'billing.payment.verify', 'billing.payment.verify', confirm, key => gateway.send(confirm, key, active)); await read();
          })}>Simpan verifikasi pembayaran</button><button type="button" className={styles.secondary} disabled={disabled} onClick={() => { setConfirm(null); heading.current?.focus(); }}>Kembali ke pemeriksaan</button>
        </section>}
        <p>Sumber isian: {shown.reviewSource === 'manual' ? 'Pemeriksaan manual melalui pengecualian OCR yang tercatat' : 'Hasil OCR dan koreksi petugas'}</p>
      </form>}
    </>}
    {saved && <PaymentAllocationPanel key={saved.result.verificationId + ':' + allocationRevision} actorId={actorId} owner={owner} verified={saved} allowed={allocationAllowed} fresh={fresh} locked={locked || busy}
      current={current} journal={journal} onSettled={onSettled} onDenied={onDenied} onBusyChange={onBusyChange} />}
    {saved && <PaymentAllocationCorrectionPanel key={'correction:' + saved.result.verificationId} actorId={actorId} owner={owner} verified={saved} allowed={allocationCorrectionAllowed} fresh={fresh} locked={locked || busy}
      current={current} journal={journal} onSettled={onSettled} onDenied={onDenied} onBusyChange={onBusyChange} onCorrected={() => setAllocationRevision(value => value + 1)} />}
    {!allowed && <p>Akun ini dapat membaca hasil; verifikasi memerlukan kewenangan tersendiri dan pemeriksa independen.</p>}
  </section>;
}
