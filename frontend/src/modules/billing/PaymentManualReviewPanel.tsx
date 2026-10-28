'use client';

import Link from 'next/link';
import { useEffect, useRef, useState } from 'react';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { submitTrackedRequest, type OperationJournal } from '../administration/operation-journal';
import { createPaymentManualReviewGateway, prepareManualReview, type PaymentManualReviewView } from './payment-manual-review-client';
import type { PaymentCorrectionField } from './payment-correction-client';
import type { PaymentDraft, PaymentOcrView } from './payment-client';
import styles from '../administration/access.module.css';
import invoice from './invoice.module.css';
import { dateTimeFormatter } from '@/lib/format';
import { Button } from '@/components/workspace-kit/button';

const labels: Record<string, string> = { amount: 'Nominal (IDR)', currency: 'Mata uang', transfer_date: 'Tanggal transfer (YYYY-MM-DD)', transfer_time: 'Waktu transfer (HH:mm:ss)', recipient: 'Nama penerima', account: 'Rekening tujuan pada bukti', bank: 'Bank tujuan pada bukti', reference: 'Nomor referensi' };
function message(error: unknown) {
  if (error instanceof AccessFailure) {
    if (error.code === 'payment_manual_review_policy_unavailable') return 'Prosedur pengecualian belum disetujui atau sudah tidak berlaku. Hubungi penanggung jawab Finance.';
    if (error.code === 'payment_manual_review_failure_required') return 'Gangguan OCR yang memenuhi prosedur belum tersedia. Muat hasil terbaru; bukti harus tetap lolos pemeriksaan keamanan.';
    if (error.code === 'invalid_payment_manual_review' || error.code === 'invalid_payment_correction') return 'Periksa format isian, konfirmasi bukti asli dan alasan. Biarkan kolom kosong jika belum diketahui.';
    if (error.code === 'independent_person_required') return 'Pemeriksa harus merupakan orang terverifikasi yang berbeda dari pembuat pengajuan.';
    if (error.code === 'payment_verification_changed' || error.code === 'payment_already_verified') return 'Pemeriksaan berubah atau sudah diverifikasi. Muat hasil terbaru.';
  }
  return accessMessage(error);
}
export function PaymentManualReviewPanel({ actorId, owner, ocr, allowed, fresh, locked, current, journal, onSettled, onDenied, onBusyChange }: {
  actorId: string; owner: PaymentDraft; ocr: PaymentOcrView | null; allowed: boolean; fresh: boolean; locked: boolean; current: () => boolean;
  journal: OperationJournal; onSettled: () => void; onDenied: (failure: unknown) => void; onBusyChange: (busy: boolean) => void;
}) {
  const [gateway] = useState(createPaymentManualReviewGateway), [ledger] = useState(() => new MutationLedger(() => 'mr.' + owner.id + '.' + crypto.randomUUID()));
  const [view, setView] = useState<PaymentManualReviewView | null>(null), [values, setValues] = useState<PaymentCorrectionField[]>([]);
  const [reason, setReason] = useState(''), [imageReviewed, setImageReviewed] = useState(false), [confirm, setConfirm] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState('');
  const alive = useRef(false), pending = useRef(false), heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; onBusyChange(false); }; }, [onBusyChange]);
  const active = () => alive.current && current();
  async function work(action: () => Promise<void>) {
    if (!active() || locked || pending.current) return; pending.current = true; setBusy(true); onBusyChange(true); setError(null); setNotice('');
    try { await action(); } catch (failure) { if (active()) { setError(failure); if (!(failure instanceof AccessFailure && failure.code === 'step_up_required')) onDenied(failure); } }
    finally { pending.current = false; onBusyChange(false); onSettled(); if (alive.current) setBusy(false); }
  }
  async function load() { const result = await gateway.read(actorId, owner); if (active()) { setView(result); setValues(result.fields); setReason(''); setImageReviewed(false); setConfirm(false); } }
  const available = ocr?.state === 'retry' || ocr?.state === 'dead', disabled = locked || busy, canSave = allowed && fresh && !disabled;
  if (!allowed || !available) return null;
  return <section className={invoice.correctionPanel} aria-label="Pengecualian gangguan OCR"><h3>Pengecualian gangguan OCR</h3>
    <p>Buka bukti asli dan ikuti prosedur Finance yang disetujui. Kolom kosong berarti belum diketahui. Pemeriksaan manual tetap menunggu verifikasi bank independen.</p>
    {!fresh && <p className={styles.alert}>Verifikasi terbaru diperlukan. <Link href="/identity/account?operation=payment.review.manual&returnPath=%2Fbilling%2Fpayment-reviews">Verifikasi dua langkah untuk pengecualian</Link></p>}
    <Button variant="secondary" disabled={!canSave} onClick={() => void work(load)}>Muat pengecualian terbaru</Button>
    {error !== null && <p role="alert" className={styles.alert}>{message(error)}</p>}{notice && <p role="status">{notice}</p>}{busy && <p role="status">Memproses pemeriksaan manual…</p>}
    {view && <form onSubmit={event => { event.preventDefault(); if (!canSave) return; try { prepareManualReview(actorId, view, values, imageReviewed, reason.trim()); setConfirm(true); requestAnimationFrame(() => heading.current?.focus()); } catch (failure) { setError(failure); } }}>
      <p>Versi pemeriksaan manual {view.reviewRevision} · Gangguan {view.failureId}: {view.failureCode}</p><p>Prosedur: {view.procedureReference}</p>
      {view.reviewedBy && <p>Pemeriksa terakhir: akun {view.reviewedBy} · {dateTimeFormatter.format(new Date(view.recordedAt!))}</p>}
      <div className={invoice.correctionFields}>{values.map((field, index) => <div className={invoice.correctionField} key={field.field}>
        <label htmlFor={'manual-' + field.field}>{labels[field.field]}</label><input id={'manual-' + field.field} value={field.value ?? ''} maxLength={256} autoComplete="off" disabled={disabled || confirm || !fresh}
          onChange={event => setValues(rows => rows.map((row, position) => position === index ? { ...row, value: event.target.value || null } : row))} />
      </div>)}</div>
      <label className={styles.checkbox}><input type="checkbox" checked={imageReviewed} disabled={disabled || confirm || !fresh} onChange={event => setImageReviewed(event.target.checked)} /> Saya telah memeriksa bukti asli yang diajukan; isian yang tidak terbaca tetap kosong.</label>
      <label htmlFor="manual-reason">Alasan pengecualian dan hasil pemeriksaan</label><textarea id="manual-reason" value={reason} required maxLength={1000} disabled={disabled || confirm || !fresh} onChange={event => setReason(event.target.value)} />
      {!confirm && <Button variant="primary" type="submit" disabled={!canSave || !imageReviewed || !reason.trim()}>Periksa pengecualian</Button>}
      {confirm && <section className={styles.state} aria-label="Konfirmasi pengecualian OCR"><h4 ref={heading} tabIndex={-1}>Periksa catatan manual sebelum menyimpan</h4>
        <ul>{values.map(field => <li key={field.field}>{labels[field.field]}: {field.value ?? 'Belum diketahui'}</li>)}</ul><p>Alasan: {reason}</p><p>Catatan ini tidak menyatakan OCR berhasil atau membuktikan dana masuk.</p>
        <Button variant="primary" type="button" disabled={!canSave} onClick={() => void work(async () => {
          if (journal.list(actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
          const command = prepareManualReview(actorId, view, values, imageReviewed, reason.trim());
          await submitTrackedRequest(ledger, journal, actorId, 'billing.payment.review.manual', 'billing.payment.review.manual', command, key => gateway.save(command, key, active));
          await load(); if (active()) setNotice('Pemeriksaan manual tersimpan. Pembayaran tetap menunggu verifikasi bank.');
        })}>Simpan pemeriksaan manual</Button><Button variant="secondary" type="button" disabled={disabled} onClick={() => setConfirm(false)}>Kembali ke isian manual</Button>
      </section>}
    </form>}
  </section>;
}
