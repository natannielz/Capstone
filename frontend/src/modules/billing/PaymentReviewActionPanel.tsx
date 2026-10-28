'use client';

import Link from 'next/link';
import { useEffect, useRef, useState } from 'react';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { submitTrackedRequest, type OperationJournal } from '../administration/operation-journal';
import { createPaymentReviewActionGateway, prepareReviewAction, type PaymentReviewActionView } from './payment-review-action-client';
import type { PaymentDraft } from './payment-client';
import styles from '../administration/access.module.css';
import invoice from './invoice.module.css';
import { dateTimeFormatter } from '@/lib/format';
import { autoLoadEndsSession } from './payment-review-session';
import { Button } from '@/components/workspace-kit/button';

const states: Record<string, string> = { pending: 'Menunggu penanggung jawab', assigned: 'Dalam pemeriksaan', rejected: 'Pengajuan ditolak', replacement_requested: 'Bukti pengganti diminta' };
const actions: Record<string, string> = { claim: 'Ambil tanggung jawab pemeriksaan', release: 'Lepaskan tanggung jawab', reject: 'Tolak pengajuan', request_replacement: 'Minta bukti pengganti' };
function message(error: unknown) {
  if (error instanceof AccessFailure && (error.code === 'payment_review_closed' || error.code === 'payment_already_verified' || error.code === 'payment_verification_changed')) return 'Pemeriksaan berubah, sudah ditutup atau terverifikasi. Muat status terbaru sebelum melanjutkan.';
  if (error instanceof AccessFailure && error.code === 'invalid_payment_review_action') return 'Pilih tindakan yang masih tersedia dan isi alasan.';
  return accessMessage(error);
}
export function PaymentReviewActionPanel({ actorId, owner, allowed, fresh, locked, current, journal, onSettled, onDenied, onBusyChange }: {
  actorId: string; owner: PaymentDraft; allowed: boolean; fresh: boolean; locked: boolean; current: () => boolean;
  journal: OperationJournal; onSettled: () => void; onDenied: (failure: unknown) => void; onBusyChange: (busy: boolean) => void;
}) {
  const [gateway] = useState(createPaymentReviewActionGateway), [ledger] = useState(() => new MutationLedger(() => 'ra.' + owner.id + '.' + crypto.randomUUID()));
  const [view, setView] = useState<PaymentReviewActionView | null>(null), [action, setAction] = useState('claim'), [reason, setReason] = useState('');
  const [publicExplanation, setPublicExplanation] = useState('');
  const [confirm, setConfirm] = useState<ReturnType<typeof prepareReviewAction> | null>(null), [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null);
  const alive = useRef(false), pending = useRef(false), heading = useRef<HTMLHeadingElement>(null), formHeading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; onBusyChange(false); }; }, [onBusyChange]);
  useEffect(() => { if (confirm) heading.current?.focus(); }, [confirm]);
  const active = () => alive.current && current();
  async function work(task: () => Promise<void>, automatic = false) {
    if (!active() || locked || pending.current) return; pending.current = true; setBusy(true); if (!automatic) onBusyChange(true); setError(null);
    try { await task(); } catch (failure) { if (active()) { setError(failure); if (!(failure instanceof AccessFailure && failure.code === 'step_up_required') && (!automatic || autoLoadEndsSession(failure))) onDenied(failure); } }
    finally { pending.current = false; if (!automatic) onBusyChange(false); onSettled(); if (alive.current) setBusy(false); }
  }
  async function load() { const result = await gateway.read(actorId, owner); if (active()) { setView(result); setConfirm(null); setReason(''); setPublicExplanation(''); setAction(result.assigneeId === actorId ? 'release' : 'claim'); } }
  // Opening a submission loads its status once; the panel is keyed by submission and revision, so a remount discards late replies.
  const autoLoaded = useRef(false);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { if (locked || autoLoaded.current) return; autoLoaded.current = true; void Promise.resolve().then(() => work(load, true /* automatic */)); }, [locked]);
  const disabled = locked || busy, canAct = allowed && fresh && !disabled, open = view && !view.verified && ['pending', 'assigned'].includes(view.state);
  return <section className={invoice.correctionPanel} aria-label="Penanggung jawab pemeriksaan"><h3>Penanggung jawab dan tindak lanjut</h3>
    <p>Diajukan {dateTimeFormatter.format(new Date(owner.submission!.submittedAt))}. Pengajuan dan bukti asli tetap tersimpan.</p>
    <Button variant="secondary" disabled={disabled} onClick={() => void work(load)}>Muat ulang status pemeriksaan</Button>
    {busy && <p role="status">Memeriksa status…</p>}{error !== null && <p role="alert" className={styles.alert}>{message(error)}</p>}
    {allowed && !fresh && <p className={styles.alert}>Verifikasi dua langkah terbaru diperlukan. <Link href="/identity/account?operation=payment.review.manage&returnPath=%2Fbilling%2Fpayment-reviews">Verifikasi dua langkah untuk tindak lanjut</Link></p>}
    {view && <><dl className={styles.facts}>
        <div><dt>Status</dt><dd>{view.verified ? 'Pembayaran sudah terverifikasi' : states[view.state]}</dd></div>
        <div><dt>Versi tindak lanjut</dt><dd>{view.reviewRevision}</dd></div>
        <div><dt>Usia saat diperiksa</dt><dd>{Math.floor((Date.parse(view.observedAt) - Date.parse(view.submittedAt)) / 60000)} menit menurut waktu server</dd></div>
        <div><dt>Penanggung jawab</dt><dd>{view.assigneeId ? 'Akun ' + view.assigneeId : 'Belum ditetapkan'}</dd></div>
        {view.reason && <div><dt>Alasan internal terakhir</dt><dd>{view.reason}</dd></div>}
        {view.publicExplanation && <div><dt>Penjelasan untuk pelanggan</dt><dd>{view.publicExplanation}</dd></div>}
        {view.recordedAt && <div><dt>Dicatat oleh</dt><dd>Akun {view.recordedBy}, {dateTimeFormatter.format(new Date(view.recordedAt))}</dd></div>}
      </dl>
      {!open && <p>Pengajuan ini tidak menerima tindakan baru. Pemeriksaan terdahulu tetap dapat dibaca. Pengganti dan tindak lanjut penjualan harus ditautkan melalui alur yang sesuai.</p>}
      {open && allowed && <form onSubmit={event => { event.preventDefault(); if (!canAct) return; try { setConfirm(prepareReviewAction(actorId, view, action, reason.trim(), ['reject', 'request_replacement'].includes(action) ? publicExplanation.trim() : null)); } catch (failure) { setError(failure); } }}>
        <h4 ref={formHeading} tabIndex={-1}>Tinjau tindakan pemeriksaan</h4><label htmlFor="review-action">Tindakan</label><select id="review-action" value={action} disabled={disabled || !!confirm} onChange={event => setAction(event.target.value)}>
          {Object.entries(actions).map(([value, label]) => <option key={value} value={value} disabled={value === 'claim' && view.assigneeId === actorId || value === 'release' && view.state !== 'assigned'}>{label}</option>)}</select>
        <label htmlFor="review-action-reason">Alasan tindak lanjut</label><textarea id="review-action-reason" required maxLength={1000} value={reason} disabled={disabled || !!confirm} onChange={event => setReason(event.target.value)} />
        <p>Alasan tindak lanjut hanya untuk petugas yang berwenang.</p>
        {['reject', 'request_replacement'].includes(action) && <><label htmlFor="review-action-public">Penjelasan untuk pelanggan</label>
          <textarea id="review-action-public" required maxLength={1000} value={publicExplanation} disabled={disabled || !!confirm} onChange={event => setPublicExplanation(event.target.value)} />
          <p>Jelaskan perbaikan yang diperlukan. Teks ini terlihat oleh pemilik pengajuan; hindari catatan internal atau data rekening.</p></>}
        <p>Tanggung jawab tidak menambah kewenangan verifikasi bank. Penolakan atau permintaan pengganti menutup verifikasi baru untuk pengajuan ini. Dana dan saldo invoice tetap utuh.</p>
        {!confirm ? <Button variant="primary" type="submit" disabled={!canAct}>Periksa tindak lanjut</Button> : <section className={styles.state} aria-label="Konfirmasi tindak lanjut pemeriksaan">
          <h4 ref={heading} tabIndex={-1}>{actions[confirm.body.action]}</h4><p>Alasan internal: {confirm.body.reason}</p>
          {confirm.body.publicExplanation && <p>Penjelasan untuk pelanggan: {confirm.body.publicExplanation}</p>}
          <p>Pengajuan {owner.id} · Bukti versi {owner.submission!.evidence.version} · Tindak lanjut versi {confirm.revision}</p>
          <Button variant="primary" type="button" disabled={!canAct} onClick={() => void work(async () => {
            if (journal.list(actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
            await submitTrackedRequest(ledger, journal, actorId, 'billing.payment.review.manage', 'billing.payment.review.manage', confirm, key => gateway.save(confirm, key, active)); await load();
          })}>Simpan tindak lanjut</Button><Button variant="secondary" type="button" disabled={disabled} onClick={() => { setConfirm(null); formHeading.current?.focus(); }}>Kembali ke tindakan</Button>
        </section>}
      </form>}
    </>}
    {!allowed && <p>Tindakan memerlukan kewenangan pengelolaan pemeriksaan tersendiri.</p>}
  </section>;
}
