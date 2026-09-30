'use client';

import Link from 'next/link';
import { useEffect, useRef, useState } from 'react';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { submitTrackedRequest, type OperationJournal } from '../administration/operation-journal';
import { createPaymentCorrectionGateway, prepareCorrection, type PaymentCorrectionField, type PaymentCorrectionView } from './payment-correction-client';
import type { PaymentDraft, PaymentOcrView } from './payment-client';
import styles from '../administration/access.module.css';
import invoice from './invoice.module.css';

const labels: Record<string, string> = { amount: 'Nominal (IDR)', currency: 'Mata uang', transfer_date: 'Tanggal transfer', transfer_time: 'Waktu transfer', recipient: 'Nama penerima', account: 'Rekening tujuan pada bukti', bank: 'Bank tujuan pada bukti', reference: 'Nomor referensi' };
const formats: Record<string, string> = { amount: 'Contoh: 2000.01', currency: 'IDR atau kosong jika tidak diketahui', transfer_date: 'YYYY-MM-DD', transfer_time: 'HH:mm:ss; zona waktu tetap perlu diperiksa' };
export function PaymentCorrectionPanel({ actorId, owner, ocr, allowed, fresh, locked, current, journal, onSettled, onDenied, onBusyChange }: {
  actorId: string; owner: PaymentDraft; ocr: PaymentOcrView | null; allowed: boolean; fresh: boolean; locked: boolean;
  current: () => boolean; journal: OperationJournal; onSettled: () => void; onDenied: (error: unknown) => void; onBusyChange: (busy: boolean) => void;
}) {
  const [gateway] = useState(createPaymentCorrectionGateway), [ledger] = useState(() => new MutationLedger(() => 'c.' + owner.id + '.' + crypto.randomUUID()));
  const [view, setView] = useState<PaymentCorrectionView | null>(null), [values, setValues] = useState<PaymentCorrectionField[]>([]), [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null), [confirm, setConfirm] = useState(false), [notice, setNotice] = useState('');
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
    if (active()) { setView(result); setValues(result.fields); setReason(''); setConfirm(false); }
  }
  const disabled = locked || busy, canSave = allowed && fresh && !disabled;
  return <section className={invoice.correctionPanel} aria-label="Koreksi pemeriksaan"><h3>Koreksi pemeriksaan</h3>
    <p>Bandingkan bukti asli dengan hasil pembacaan. Kolom kosong berarti belum diketahui. Rekening pada gambar belum menjadi rekening penerimaan yang disetujui.</p>
    {allowed && (!fresh || error instanceof AccessFailure && error.code === 'step_up_required') && <p className={styles.alert}>Verifikasi terbaru diperlukan. <Link href="/identity/account?operation=payment.review.correct&returnPath=%2Fbilling%2Fpayment-reviews">Verifikasi dua langkah</Link></p>}
    {!ocr?.result && <p>Muat hasil OCR sebelum memeriksa koreksi. Untuk gangguan yang tercatat, petugas berwenang dapat mengikuti prosedur pengecualian di bagian pemeriksaan manual.</p>}
    <button className={styles.secondary} disabled={disabled || !ocr?.result} onClick={() => void work(load)}>Muat pemeriksaan terbaru</button>
    {error !== null && <p role="alert" className={styles.alert}>{error instanceof AccessFailure && error.code === 'payment_draft_changed' ? 'Pemeriksaan berubah. Muat versi terbaru sebelum menyimpan koreksi.' : error instanceof AccessFailure && error.code === 'invalid_payment_correction' ? 'Periksa format nominal, tanggal, waktu dan alasan. Ikuti contoh di bawah kolom; kosongkan nilai yang belum diketahui.' : error instanceof AccessFailure && error.code === 'correction_unchanged' ? 'Ubah setidaknya satu isian sebelum menyimpan koreksi.' : accessMessage(error)}</p>}
    {notice && <p role="status">{notice}</p>}{busy && <p role="status">Memproses pemeriksaan…</p>}
    {view && <form onSubmit={event => { event.preventDefault(); if (canSave && reason.trim()) { setConfirm(true); requestAnimationFrame(() => heading.current?.focus()); } }}>
      <p>Versi pemeriksaan {view.reviewRevision}. Koreksi tidak memverifikasi pembayaran atau mengubah saldo tagihan.</p>
      <div className={invoice.correctionFields}>{values.map((item, index) => <div key={item.field} className={invoice.correctionField}>
        <label htmlFor={'correction-' + item.field}>{labels[item.field]}</label><p>OCR asli: {view.originalFields[index].value ?? 'Belum diketahui'}</p>
        <input id={'correction-' + item.field} value={item.value ?? ''} maxLength={256} autoComplete="off" readOnly={!allowed} disabled={disabled || confirm || !fresh}
          onChange={event => setValues(rows => rows.map((row, position) => position === index ? { ...row, value: event.target.value === '' ? null : event.target.value } : row))} aria-describedby={formats[item.field] ? 'format-' + item.field : undefined} />
        {formats[item.field] && <p id={'format-' + item.field}>{formats[item.field]}</p>}
      </div>)}</div>
      {allowed && <><label htmlFor="correction-reason">Alasan koreksi</label><textarea id="correction-reason" value={reason} required maxLength={1000} disabled={disabled || confirm || !fresh} onChange={event => setReason(event.target.value)} />
        {!confirm && <button className={styles.primary} disabled={!canSave || !reason.trim()}>Periksa koreksi</button>}
        {confirm && <section className={styles.state} aria-label="Konfirmasi koreksi"><h4 ref={heading} tabIndex={-1}>Periksa perubahan sebelum menyimpan</h4>
          <ul>{values.filter((item, index) => item.value !== view.fields[index].value).map(item => <li key={item.field}>{labels[item.field]}: {view.fields.find(row => row.field === item.field)?.value ?? 'Belum diketahui'} → {item.value ?? 'Belum diketahui'}</li>)}</ul>
          <p>Alasan: {reason}</p><p>Nama akun pemeriksa, waktu, nilai sebelum dan sesudah akan disimpan. Dana tetap menunggu konfirmasi bank independen.</p>
          <button type="button" className={styles.primary} disabled={!canSave} onClick={() => void work(async () => {
            if (journal.list(actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
            const command = prepareCorrection(actorId, view, values, reason.trim());
            await submitTrackedRequest(ledger, journal, actorId, 'billing.payment.review.correct', 'billing.payment.review.correct', command, key => gateway.save(command, key, active));
            await load(); if (active()) setNotice('Koreksi tersimpan. Pembayaran tetap menunggu verifikasi.');
          })}>Simpan koreksi</button><button type="button" className={styles.secondary} disabled={disabled} onClick={() => setConfirm(false)}>Kembali ke isian</button>
        </section>}</>}
      <h4>Riwayat pemeriksaan</h4>{view.recent.length === 0 ? <p>Belum ada koreksi petugas.</p> : <><p>Menampilkan {view.recent.length} versi terbaru; seluruh versi tetap tersimpan.</p><ul>{view.recent.map(row => <li key={row.id}>
        <strong>Versi {row.revision} · Akun {row.actorId}</strong><p>{new Intl.DateTimeFormat('id-ID', { dateStyle: 'medium', timeStyle: 'short', timeZone: 'Asia/Jakarta' }).format(new Date(row.recordedAt))} · {row.reason}</p>
        <ul>{row.fields.filter((item, index) => item.value !== row.beforeFields[index].value).map(item => <li key={item.field}>{labels[item.field]}: {row.beforeFields.find(value => value.field === item.field)?.value ?? 'Belum diketahui'} → {item.value ?? 'Belum diketahui'}</li>)}</ul>
      </li>)}</ul></>}
    </form>}
  </section>;
}
