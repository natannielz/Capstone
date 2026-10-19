'use client';

import Link from 'next/link';
import { useEffect, useRef, useState } from 'react';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { submitTrackedRequest, type OperationJournal } from '../administration/operation-journal';
import { createPaymentCorrectionGateway, prepareCorrection, paymentCorrectionLabels, type PaymentCorrectionField, type PaymentCorrectionView,
  type PaymentCorrectionSupplementaryObservation, type RememberedPaymentObservation } from './payment-correction-client';
import type { PaymentDraft, PaymentOcrView } from './payment-client';
import styles from '../administration/access.module.css';
import invoice from './invoice.module.css';
import { dateTimeFormatter } from '@/lib/format';

const labels = paymentCorrectionLabels;
const formats: Record<string, string> = { amount: 'Contoh: 2000.01', currency: 'IDR atau kosong jika tidak diketahui', transfer_date: 'YYYY-MM-DD', transfer_time: 'HH:mm:ss; zona waktu tetap perlu diperiksa' };
function correctionMessage(error: unknown) {
  if (!(error instanceof AccessFailure)) return accessMessage(error);
  if (['payment_draft_changed', 'payment_supplement_view_stale', 'pdf_document_processing_history_invalid', 'evidence_processing_reference_changed'].includes(error.code))
    return 'Pemeriksaan atau rujukan lampiran berubah. Muat versi terbaru dan periksa kembali PDF asli sebelum menyimpan.';
  if (['payment_supplement_policy_unavailable', 'payment_supplement_policy_changed'].includes(error.code))
    return 'Lampiran PDF belum diizinkan oleh kebijakan yang berlaku. Hapus rujukan PDF untuk melanjutkan koreksi foto.';
  if (error.code === 'evidence_deadline_expired') return 'Waktu pemeriksaan bukti telah berakhir. Muat pengajuan dan lampiran terbaru sebelum mencoba koreksi.';
  if (['evidence_read_policy_unavailable', 'payment_review_integrity_unavailable'].includes(error.code))
    return 'Bukti atau riwayat belum dapat diperiksa dengan aman. Muat ulang atau hubungi penanggung jawab pemeriksaan.';
  if (error.code === 'invalid_payment_correction') return 'Periksa format nominal, tanggal, waktu, alasan dan rujukan PDF. Ikuti contoh di bawah kolom; kosongkan nilai yang belum diketahui.';
  if (error.code === 'correction_unchanged') return 'Ubah setidaknya satu isian sebelum menyimpan koreksi. Rujukan PDF saja tidak membuat koreksi.';
  return accessMessage(error);
}
export function PaymentCorrectionPanel({ actorId, owner, ocr, allowed, fresh, locked, current, journal, onSettled, onDenied, onBusyChange,
  observations = [], onRemoveObservation, onObservationsSaved }: {
  actorId: string; owner: PaymentDraft; ocr: PaymentOcrView | null; allowed: boolean; fresh: boolean; locked: boolean;
  current: () => boolean; journal: OperationJournal; onSettled: () => void; onDenied: (error: unknown) => void; onBusyChange: (busy: boolean) => void;
  observations?: RememberedPaymentObservation[]; onRemoveObservation?: (field: PaymentCorrectionField['field']) => void; onObservationsSaved?: () => void;
}) {
  const [gateway] = useState(createPaymentCorrectionGateway), [ledger] = useState(() => new MutationLedger(() => 'c.' + owner.id + '.' + crypto.randomUUID()));
  const [view, setView] = useState<PaymentCorrectionView | null>(null), [values, setValues] = useState<PaymentCorrectionField[]>([]), [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null), [confirm, setConfirm] = useState<{
    command: ReturnType<typeof prepareCorrection>; observations: RememberedPaymentObservation[];
  } | null>(null), [notice, setNotice] = useState('');
  const pending = useRef(false), heading = useRef<HTMLHeadingElement>(null);
  const alive = useRef(false);
  useEffect(() => { alive.current = true; return () => { alive.current = false; onBusyChange(false); }; }, [onBusyChange]);
  const active = () => alive.current && current();
  async function work(action: () => Promise<void>) {
    if (!active() || locked || pending.current) return; pending.current = true; setBusy(true); onBusyChange(true); setError(null); setNotice('');
    try { await action(); } catch (failure) { if (active()) { setError(failure); if (!(failure instanceof AccessFailure && failure.code === 'step_up_required')) onDenied(failure); } }
    finally { pending.current = false; onBusyChange(false); onSettled(); if (alive.current) setBusy(false); }
  }
  async function load() {
    if (!ocr?.result) return; const result = await gateway.read(actorId, owner, ocr);
    if (active()) { setView(result); setValues(result.fields); setReason(''); setConfirm(null); }
  }
  const disabled = locked || busy, canSave = allowed && fresh && !disabled;
  const shownObservations = confirm?.observations ?? observations;
  return <section className={invoice.correctionPanel} aria-label="Koreksi pemeriksaan"><h3>Koreksi pemeriksaan</h3>
    <p>Bandingkan bukti asli dengan hasil pembacaan. Kolom kosong berarti belum diketahui. Rekening pada gambar belum menjadi rekening penerimaan yang disetujui.</p>
    {allowed && (!fresh || error instanceof AccessFailure && error.code === 'step_up_required') && <p className={styles.alert}>Verifikasi terbaru diperlukan. <Link href="/identity/account?operation=payment.review.correct&returnPath=%2Fbilling%2Fpayment-reviews">Verifikasi dua langkah</Link></p>}
    {!ocr?.result && <p>Muat hasil OCR sebelum memeriksa koreksi. Untuk gangguan yang tercatat, petugas berwenang dapat mengikuti prosedur pengecualian di bagian pemeriksaan manual.</p>}
    <button className={styles.secondary} disabled={disabled || !ocr?.result} onClick={() => void work(load)}>Muat pemeriksaan terbaru</button>
    {error !== null && <p role="alert" className={styles.alert}>{correctionMessage(error)}</p>}
    {notice && <p role="status">{notice}</p>}{busy && <p role="status">Memproses pemeriksaan…</p>}
    {view && <form onSubmit={event => { event.preventDefault(); if (canSave && active() && reason.trim()) {
      try {const command = prepareCorrection(actorId, view, values, reason.trim(), observations.length ? observations.map(item => item.reference) : undefined);
        setConfirm({command, observations: structuredClone(observations)}); setError(null); requestAnimationFrame(() => heading.current?.focus());
      } catch (failure) {setError(failure);}
    } }}>
      <p>Versi pemeriksaan {view.reviewRevision}. Koreksi tidak memverifikasi pembayaran atau mengubah saldo tagihan.</p>
      {shownObservations.length > 0 && <p role="status">Rujukan PDF tambahan siap dibandingkan di bawah. Isian koreksi tetap diisi sendiri berdasarkan pemeriksaan foto utama.</p>}
      <div className={invoice.correctionFields}>{values.map((item, index) => <div key={item.field} className={invoice.correctionField}>
        <label htmlFor={'correction-' + item.field}>{labels[item.field]}</label><p>OCR asli: {view.originalFields[index].value ?? 'Belum diketahui'}</p>
        <input id={'correction-' + item.field} value={item.value ?? ''} maxLength={256} autoComplete="off" readOnly={!allowed} disabled={disabled || confirm !== null || !fresh}
          onChange={event => setValues(rows => rows.map((row, position) => position === index ? { ...row, value: event.target.value === '' ? null : event.target.value } : row))} aria-describedby={formats[item.field] ? 'format-' + item.field : undefined} />
        {formats[item.field] && <p id={'format-' + item.field}>{formats[item.field]}</p>}
        {shownObservations.filter(observation => observation.reference.field === item.field).map(observation => <aside key={item.field} aria-label={'Perbandingan PDF: ' + labels[item.field]}>
          <p><strong>PDF tambahan:</strong> lampiran {observation.reference.supplementId}, halaman {observation.reference.candidate.pageIndex + 1}.</p>
          <p>Nilai pada halaman PDF: {observation.value ?? 'Belum diketahui'}. Nilai ini tidak disalin ke isian koreksi.</p>
          {observation.flags.length > 0 && <p>Perlu perhatian: {observation.flags.map(flag => ({unknown: 'belum diketahui', ambiguous: 'lebih dari satu kemungkinan', low_confidence: 'keyakinan pembacaan rendah',
            unparsed: 'belum dapat dibaca', masked: 'sebagian disamarkan', timezone_unverified: 'zona waktu belum dipastikan'}[flag] ?? 'perlu pemeriksaan manusia')).join(', ')}.</p>}
          <ObservationDetails reference={observation.reference}/>
          {allowed && onRemoveObservation && <button type="button" className={styles.secondary} disabled={disabled || confirm !== null || !fresh} onClick={() => {
            if (active()) onRemoveObservation(item.field);
          }}>Hapus rujukan PDF: {labels[item.field]}</button>}
        </aside>)}
      </div>)}</div>
      {allowed && <><label htmlFor="correction-reason">Alasan koreksi</label><textarea id="correction-reason" value={reason} required maxLength={1000} disabled={disabled || confirm !== null || !fresh} onChange={event => setReason(event.target.value)} />
        {!confirm && <button className={styles.primary} disabled={!canSave || !reason.trim()}>Periksa koreksi</button>}
        {confirm && <section className={styles.state} aria-label="Konfirmasi koreksi"><h4 ref={heading} tabIndex={-1}>Periksa perubahan sebelum menyimpan</h4>
          <ul>{values.filter((item, index) => item.value !== view.fields[index].value).map(item => <li key={item.field}>{labels[item.field]}: {view.fields.find(row => row.field === item.field)?.value ?? 'Belum diketahui'} → {item.value ?? 'Belum diketahui'}</li>)}</ul>
          {confirm.command.body.supplementaryObservations && <><h4>Rujukan PDF yang ikut disimpan</h4><ObservationList observations={confirm.command.body.supplementaryObservations}/>
            <p>PDF asli pada halaman yang dipilih telah diperiksa. Rujukan ini melengkapi pemeriksaan foto utama; nilai PDF tidak menetapkan nilai koreksi.</p></>}
          <p>Alasan: {confirm.command.body.reason}</p><p>Nama akun pemeriksa, waktu, nilai sebelum dan sesudah akan disimpan. Dana tetap menunggu konfirmasi bank independen.</p>
          <button type="button" className={styles.primary} disabled={!canSave} onClick={() => void work(async () => {
            if (journal.list(actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
            const command = confirm.command;
            await submitTrackedRequest(ledger, journal, actorId, 'billing.payment.review.correct', 'billing.payment.review.correct', command, key => gateway.save(command, key, active));
            await load(); if (active()) {onObservationsSaved?.(); setNotice('Koreksi tersimpan. Pembayaran tetap menunggu verifikasi.');}
          })}>Simpan koreksi</button><button type="button" className={styles.secondary} disabled={disabled} onClick={() => setConfirm(null)}>Kembali ke isian</button>
        </section>}</>}
      <h4>Riwayat pemeriksaan</h4>{view.recent.length === 0 ? <p>Belum ada koreksi petugas.</p> : <><p>Menampilkan {view.recent.length} versi terbaru; seluruh versi tetap tersimpan.</p><ul>{view.recent.map(row => <li key={row.id}>
        <strong>Versi {row.revision} · Akun {row.actorId}</strong><p>{dateTimeFormatter.format(new Date(row.recordedAt))} · {row.reason}</p>
        <ul>{row.fields.filter((item, index) => item.value !== row.beforeFields[index].value).map(item => <li key={item.field}>{labels[item.field]}: {row.beforeFields.find(value => value.field === item.field)?.value ?? 'Belum diketahui'} → {item.value ?? 'Belum diketahui'}</li>)}</ul>
        {row.supplementaryObservations && <section aria-label={'Rujukan PDF tersimpan versi ' + row.revision}><p>Rujukan PDF tambahan yang diperiksa pada koreksi ini:</p><ObservationList observations={row.supplementaryObservations}/></section>}
      </li>)}</ul></>}
    </form>}
  </section>;
}
function ObservationDetails({reference}: {reference: PaymentCorrectionSupplementaryObservation}) {
  return <details><summary>Rincian rujukan dokumen</summary><p>Versi lampiran {reference.supplementRevision} · hasil pembacaan {reference.candidate.resultId}.</p>
    <p>Sidik hasil: {reference.candidate.resultHash}</p><p>Bukti {reference.candidate.evidence.id} · versi {reference.candidate.evidence.version} · sidik bukti: {reference.candidate.evidence.sha256}</p></details>;
}
function ObservationList({observations}: {observations: PaymentCorrectionSupplementaryObservation[]}) {
  return <ul>{observations.map(reference => <li key={reference.field}>{labels[reference.field]} · lampiran {reference.supplementId} · halaman {reference.candidate.pageIndex + 1} · PDF asli diperiksa.
    <ObservationDetails reference={reference}/></li>)}</ul>;
}
