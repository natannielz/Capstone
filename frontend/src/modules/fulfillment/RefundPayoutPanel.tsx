'use client';
import Link from 'next/link';
import { useEffect, useRef, useState } from 'react';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { submitTrackedRequest, type OperationJournal } from '../administration/operation-journal';
import { orderAmount } from '../ordering/order-quote-client';
import type { PaymentEvidenceStatus } from '../billing/payment-client';
import type { AfterSalesCase } from './case-client';
import { createReturnGateway, type ReturnView } from './return-client';
import { canRecordRefundPayout, createRefundPayoutGateway, prepareRefundPayoutDraft, prepareRefundPayoutRecord,
  type ApprovedRefundView, type RefundPayoutPreview, type RefundPayoutView } from './refund-payout-client';
import styles from '../administration/access.module.css';
import { formatDateTime } from '@/lib/format';

type Props = { actorId: string; owner: AfterSalesCase; actions: readonly string[]; fresh: boolean; locked: boolean; current: () => boolean; freshCurrent: () => boolean;
  journal: OperationJournal; onSettled: () => void; onDenied: (failure: unknown) => void; onBusyChange: (busy: boolean) => void };
const rupiah = (amount: string) => orderAmount('IDR', amount);
function message(failure: unknown) {
  if (failure instanceof AccessFailure) {
    if (failure.code === 'independent_person_required') return 'Pencatat refund harus berbeda dari petugas yang menyetujui reservasi refund.';
    if (failure.code === 'refund_payout_policy_unavailable') return 'Prosedur pencatatan refund dan rekening pembayaran yang telah disetujui belum tersedia. Hubungi penanggung jawab Finance.';
    if (failure.code === 'refund_beneficiary_unavailable') return 'Rekening penerima refund yang telah diverifikasi secara independen belum tersedia. Hubungi penanggung jawab Finance.';
    if (failure.code === 'refund_payout_already_recorded') return 'Pencatatan untuk persetujuan ini sudah tersedia. Muat ulang rincian untuk memeriksa hasilnya.';
    if (failure.code === 'refund_payout_image_required') return 'Pilih gambar bukti asli JPEG atau PNG, maksimal 10 MiB.';
    if (failure.code.startsWith('refund_payout_')) return 'Sumber refund atau bukti berubah atau belum dapat diperiksa. Muat ulang rincian; hubungi penanggung jawab jika masalah berlanjut.';
  }
  return accessMessage(failure);
}
function imageLabel(item: PaymentEvidenceStatus) {
  if (item.state === 'quarantined' && item.scanStatus === 'clean' && item.decoderStatus === 'decoded' && item.safeFailure === null) return 'Gambar lolos pemeriksaan keamanan. Refund belum diverifikasi oleh bank.';
  if (item.state === 'quarantined') return item.scanStatus === 'infected' || item.decoderStatus === 'invalid' ? 'Gambar ditahan: pemeriksaan keamanan gagal. Pilih bukti asli yang aman.' : 'Gambar tersimpan; menunggu pemeriksaan keamanan.';
  return item.state === 'fresh' ? 'Siap menerima berkas' : 'Unggah belum selesai. Periksa hasil upaya ini sebelum mengirim berkas lagi.';
}
function PayoutStatus({ payout, settlement }: { payout: NonNullable<ApprovedRefundView['payout']>; settlement: ApprovedRefundView['settlement'] }) {
  return settlement ? <section aria-label="Hasil verifikasi debit refund"><p role="status">Pembayaran refund {rupiah(settlement.amount)} telah dicocokkan dengan debit bank secara independen.</p>
    <p>Verifikasi {settlement.settlementId} · {formatDateTime(settlement.recordedAt)}</p>
    <ul>{settlement.receipts.map(row => <li key={row.receiptId}>Penerimaan bank {row.receiptId}: refund dibayar {rupiah(row.paid)}, masih disisihkan {rupiah(row.pending)}, dana tersedia {rupiah(row.available)}.</li>)}</ul>
  </section> : <section aria-label="Refund tercatat belum diverifikasi"><p role="status">Bukti refund {rupiah(payout.amount)} tercatat, menunggu verifikasi bank independen.</p>
    <p>Pencatatan {payout.payoutId} · {formatDateTime(payout.recordedAt)}</p><p>Catatan ini belum membuktikan uang diterima pelanggan.</p></section>;
}
export function RefundPayoutPanel(props: Props) {
  const { actorId, owner, actions, locked, fresh, current, onBusyChange, onDenied, onSettled } = props;
  const [returns] = useState(createReturnGateway), [gateway] = useState(createRefundPayoutGateway);
  const [physical, setPhysical] = useState<ReturnView | null>(null), [returnId, setReturnId] = useState(''), [olderId, setOlderId] = useState('');
  const [opened, setOpened] = useState<ApprovedRefundView | null>(null), [error, setError] = useState<unknown>(null), [busy, setBusy] = useState(false);
  const alive = useRef(false), pending = useRef(false);
  useEffect(() => { alive.current = true; return () => { alive.current = false; onBusyChange(false); }; }, [onBusyChange]);
  async function work(task: () => Promise<void>) {
    if (!current() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); onBusyChange(true);
    try { await task(); } catch (failure) { if (alive.current && current()) { setError(failure); onDenied(failure); } }
    finally { pending.current = false; onBusyChange(false); onSettled(); if (alive.current) setBusy(false); }
  }
  if (!canRecordRefundPayout(actions)) return null;
  return <section aria-label="Pencatatan bukti refund" style={{ minWidth: 0, overflowWrap: 'anywhere' }}><h3>Pencatatan bukti refund</h3>
    <p>Gunakan persetujuan refund yang sudah menyisihkan dana. Pencatatan ini menyimpan rincian dan bukti transfer untuk pemeriksaan bank independen; saldo dana tersedia tetap sama.</p>
    {!fresh && <p role="status">Verifikasi dua langkah terbaru diperlukan. <Link href="/identity/account?operation=disbursement.record&returnPath=%2Ffulfillment%2Fcases">Verifikasi dua langkah untuk mencatat refund</Link></p>}
    {error !== null && <p role="alert">{message(error)}</p>}{busy && <p role="status">Memeriksa persetujuan refund…</p>}
    <button className={styles.secondary} disabled={locked || busy || !fresh} onClick={() => void work(async () => { const value = await returns.read(actorId, owner); if (alive.current && current()) { setPhysical(value); setReturnId(value.recent[0]?.id ?? ''); setOlderId(''); setOpened(null); } })}>Muat retur untuk pencatatan refund</button>
    {physical && <form onSubmit={event => { event.preventDefault(); const selected = olderId || returnId; if (selected && fresh) void work(async () => { setOpened(null); const value = await gateway.approved(actorId, owner, selected); if (alive.current && current()) setOpened(value); }); }}>
      {physical.recent.length ? <label>Penerimaan barang retur untuk refund<select required value={returnId} disabled={locked || busy || !!olderId} onChange={event => { setReturnId(event.target.value); setOpened(null); }}>{physical.recent.map(row => <option key={row.id} value={row.id}>{row.id} · {formatDateTime(row.receivedAt)}</option>)}</select></label> : <p>Belum ada penerimaan retur.</p>}
      {physical.hasEarlier && <label>ID penerimaan retur yang lebih lama untuk refund<input inputMode="numeric" pattern="[1-9][0-9]*" maxLength={19} value={olderId} disabled={locked || busy} onChange={event => { setOlderId(event.target.value); setOpened(null); }} /></label>}
      <button className={styles.secondary} disabled={locked || busy || !fresh || !(olderId || returnId)}>Buka persetujuan refund</button></form>}
    {opened && (opened.approval === null ? <p role="status">Belum ada persetujuan refund untuk penerimaan ini.</p> : opened.payout ? <PayoutStatus payout={opened.payout} settlement={opened.settlement} /> : <PayoutDesk key={opened.approval.financialApprovalId} {...props} approved={opened} />)}
  </section>;
}
function PayoutDesk({ actorId, owner, approved, fresh, locked, current, freshCurrent, journal, onSettled, onDenied, onBusyChange }: Props & { approved: ApprovedRefundView }) {
  const approval = approved.approval!;
  const [gateway] = useState(createRefundPayoutGateway), [draftLedger] = useState(() => new MutationLedger(() => 'rpd.' + approval.financialApprovalId + '.' + crypto.randomUUID()));
  const [recordLedgers] = useState(() => new Map<string, MutationLedger>()), [uploadLedgers] = useState(() => new Map<string, MutationLedger>());
  const [view, setView] = useState<RefundPayoutView | null>(null), [shown, setShown] = useState<RefundPayoutPreview | null>(null);
  const [draftCommand, setDraftCommand] = useState<ReturnType<typeof prepareRefundPayoutDraft> | null>(null), [recordCommand, setRecordCommand] = useState<ReturnType<typeof prepareRefundPayoutRecord> | null>(null);
  const [date, setDate] = useState(''), [reference, setReference] = useState(''), [reason, setReason] = useState(''), [consent, setConsent] = useState(false);
  const [evidence, setEvidence] = useState<PaymentEvidenceStatus[]>([]), [file, setFile] = useState<File | null>(null), [seen, setSeen] = useState<string | null>(null);
  const [original, setOriginal] = useState<{ url: string; id: string } | null>(null), [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null);
  const [now, setNow] = useState(() => Date.now());
  const alive = useRef(false), pending = useRef(false), fileInput = useRef<HTMLInputElement>(null), heading = useRef<HTMLHeadingElement>(null), originalUrl = useRef<string | null>(null);
  const active = () => alive.current && current(), currentRecord = () => active() && freshCurrent();
  const disabled = locked || busy, canAct = fresh && !disabled;
  const unresolvedUpload = evidence.some(item => ['writing', 'publish_pending'].includes(item.state));
  function closeOriginal() { if (originalUrl.current) URL.revokeObjectURL(originalUrl.current); originalUrl.current = null; setOriginal(null); }
  useEffect(() => { alive.current = true; return () => { alive.current = false; if (originalUrl.current) URL.revokeObjectURL(originalUrl.current); onBusyChange(false); }; }, [onBusyChange]);
  useEffect(() => { const timer = window.setInterval(() => setNow(Date.now()), 1000); return () => window.clearInterval(timer); }, []);
  useEffect(() => { if (draftCommand || recordCommand || original) heading.current?.focus(); }, [draftCommand, recordCommand, original]);
  async function read(draftId: string) { const value = await gateway.read(actorId, owner, approval, draftId); if (!active()) return; setView(value); setShown(null); setDraftCommand(null); setRecordCommand(null); setConsent(false);
    const images = await gateway.evidence(actorId, draftId); if (active()) setEvidence(images); }
  async function work(task: () => Promise<void>) {
    if (!active() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); onBusyChange(true);
    try { await task(); } catch (failure) { if (active()) { setError(failure); setDraftCommand(null); setRecordCommand(null); setShown(null); setConsent(false); onDenied(failure); } }
    finally { pending.current = false; onBusyChange(false); onSettled(); if (alive.current) setBusy(false); }
  }
  function clearReview() { setShown(null); setDraftCommand(null); setRecordCommand(null); setConsent(false); }
  async function upload(existing?: PaymentEvidenceStatus) {
    if (!view?.draft || !file || !canAct || unresolvedUpload) return; const draft = view.draft, chosen = file; closeOriginal(); setSeen(null);
    await work(async () => {
      if (chosen.size <= 0 || chosen.size > 10 * 1024 * 1024 || !['image/jpeg', 'image/png'].includes(chosen.type)) throw new AccessFailure(400, 'refund_payout_image_required');
      if (journal.list(actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
      const ledger = uploadLedgers.get(draft.draftId) ?? new MutationLedger(() => 'rpe.' + draft.draftId + '.' + crypto.randomUUID()); uploadLedgers.set(draft.draftId, ledger);
      const reservation = existing ?? await submitTrackedRequest(ledger, journal, actorId, 'evidence.refund-payout-image.reserve', 'evidence.refund-payout-image.reserve', { draftId: draft.draftId, revision: draft.revision }, key => gateway.reserve(actorId, draft.draftId, draft.revision, key, currentRecord));
      if (!active()) return; let uploadFailure: unknown = null;
      try { await gateway.upload(actorId, draft.draftId, reservation.reservationId, reservation.revision, chosen, currentRecord); } catch (failure) { uploadFailure = failure; }
      if (!active()) return; setFile(null); if (fileInput.current) fileInput.current.value = '';
      const images = await gateway.evidence(actorId, draft.draftId); if (active()) setEvidence(images);
      if (uploadFailure !== null && !images.some(item => item.reservationId === reservation.reservationId && item.state === 'quarantined')) throw uploadFailure;
    });
  }
  const draft = view?.draft;
  return <section aria-label="Dossier bukti refund"><h4>Refund disisihkan {rupiah(approval.reservedRefund)} · Persetujuan {approval.financialApprovalId}</h4>
    {error !== null && <p role="alert">{message(error)}</p>}{busy && <p role="status">Memeriksa dan menyimpan bukti refund…</p>}
    {approved.draft && !view && <button className={styles.secondary} disabled={!canAct} onClick={() => void work(() => read(approved.draft!.draftId))}>Lanjutkan draf refund tersimpan</button>}
    {!draft && !approved.draft && !draftCommand && <form onSubmit={event => { event.preventDefault(); if (!canAct || !currentRecord()) return; void work(async () => {
      const preview = await gateway.previewDraft(actorId, owner, approval, date, reference.trim()); const command = prepareRefundPayoutDraft(actorId, owner, approval, preview, reason.trim());
      if (currentRecord()) { setShown(preview); setDraftCommand(command); setConsent(false); }
    }); }}><p>Masukkan tanggal dan referensi dari transfer yang dilakukan melalui prosedur bank organisasi.</p>
      <label>Tanggal transfer refund<input type="date" required value={date} disabled={disabled} onChange={event => { setDate(event.target.value); clearReview(); }} /></label>
      <label>Referensi transfer refund<input required maxLength={160} value={reference} disabled={disabled} onChange={event => { setReference(event.target.value); clearReview(); }} /></label>
      <label>Alasan pencatatan refund<textarea required maxLength={1000} value={reason} disabled={disabled} onChange={event => { setReason(event.target.value); clearReview(); }} /></label>
      <button className={styles.primary} disabled={!canAct}>Tinjau draf refund</button></form>}
    {view && <><p>Penerima terverifikasi: {view.beneficiaryName} · Bank {view.beneficiaryBankCode} · Rekening berakhir {view.beneficiaryAccountSuffix}</p>
      <p>Rekening pembayaran: bank {view.payingBankCode} · Berakhir {view.payingAccountSuffix} · Nilai {rupiah(view.amount)}</p>
      <p>Tanggal {view.input.transferDate} · Referensi {view.input.transferReference}</p>
      {view.payout ? <PayoutStatus payout={view.payout} settlement={view.settlement} /> : draft && !recordCommand && <>
        <label>Alasan pencatatan bukti refund<textarea required maxLength={1000} value={reason} disabled={disabled} onChange={event => { setReason(event.target.value); clearReview(); }} /></label>
        <label>Gambar bukti transfer refund asli<input ref={fileInput} type="file" accept="image/png,image/jpeg" disabled={disabled || unresolvedUpload} onChange={event => { setFile(event.target.files?.[0] ?? null); setSeen(null); closeOriginal(); clearReview(); }} /></label>
        <p>JPEG atau PNG, maksimal 10 MiB. Bukti disimpan secara privat dan diperiksa keamanannya.</p>
        {unresolvedUpload && <p role="status">Unggah sebelumnya belum terkonfirmasi. Periksa hasil upaya yang sama sebelum mengunggah lagi.</p>}
        <button className={styles.secondary} disabled={!canAct || !file || unresolvedUpload} onClick={() => void upload()}>Unggah bukti refund asli</button>
        <button className={styles.secondary} disabled={disabled} onClick={() => void work(async () => { const items = await gateway.evidence(actorId, draft.draftId); if (active()) setEvidence(items); })}>Muat status bukti refund</button>
        <ul>{evidence.map(item => <li key={item.reservationId}><p>Bukti {item.reservationId} · {imageLabel(item)}</p>
          {item.state === 'fresh' && <button className={styles.secondary} disabled={!canAct || !file || Date.parse(item.expiresAt) <= now} onClick={() => void upload(item)}>Lanjutkan unggah bukti {item.reservationId}</button>}
          {['writing', 'publish_pending', 'interrupted'].includes(item.state) && <button className={styles.secondary} disabled={!canAct} onClick={() => void work(async () => { await gateway.recoverUpload(actorId, draft.draftId, item.reservationId); const items = await gateway.evidence(actorId, draft.draftId); if (active()) setEvidence(items); })}>Periksa hasil unggah {item.reservationId}</button>}
          {item.scanStatus === 'clean' && item.decoderStatus === 'decoded' && item.safeFailure === null && item.reference && <>
            <button className={styles.secondary} disabled={disabled} onClick={() => void work(async () => { closeOriginal(); const blob = await gateway.original(actorId, draft.draftId, draft.revision, item, active); if (!active()) return;
              const url = URL.createObjectURL(blob); originalUrl.current = url; setOriginal({ url, id: item.reservationId }); setSeen(item.reservationId + ':' + item.reference!.sha256); })}>Lihat bukti refund {item.reservationId}</button>
            <button className={styles.primary} disabled={!canAct || !reason.trim() || seen !== item.reservationId + ':' + item.reference.sha256} onClick={() => void work(async () => { const preview = await gateway.previewRecord(actorId, owner, approval, view, item);
              const command = prepareRefundPayoutRecord(actorId, owner, approval, preview, item, reason.trim()); if (currentRecord()) { closeOriginal(); setShown(preview); setRecordCommand(command); setConsent(false); } })}>Tinjau pencatatan bukti {item.reservationId}</button>
          </>}</li>)}</ul>
        {evidence.length > 0 && evidence.length % 25 === 0 && <button className={styles.secondary} disabled={disabled} onClick={() => void work(async () => { const older = await gateway.evidence(actorId, draft.draftId, evidence.at(-1)!.reservationId); if (active()) setEvidence(previous => [...previous, ...older.filter(item => !previous.some(existing => existing.reservationId === item.reservationId))]); })}>Bukti refund lebih lama</button>}
      </>}
      <button className={styles.secondary} disabled={disabled} onClick={() => void work(() => read(draft!.draftId))}>Muat rincian refund terbaru</button></>}
    {original && <section aria-label="Gambar bukti refund asli"><h5 ref={heading} tabIndex={-1}>Bukti refund {original.id}</h5>
      {/* Authenticated original bytes are held in a temporary local object URL. */}
      {/* eslint-disable-next-line @next/next/no-img-element */}
      <img src={original.url} alt="Bukti transfer refund asli untuk diperiksa" style={{ maxWidth: '100%', height: 'auto' }} />
      <button className={styles.secondary} onClick={closeOriginal}>Tutup gambar bukti refund</button></section>}
    {shown && (draftCommand || recordCommand) && <section aria-label="Konfirmasi pencatatan refund"><h4 ref={heading} tabIndex={-1}>{draftCommand ? 'Simpan draf bukti refund' : 'Catat bukti refund untuk verifikasi'}</h4>
      <p>Invoice {owner.source.number} · Retur {shown.view.returnId} · Nilai {rupiah(shown.view.amount)}</p>
      <p>Penerima terverifikasi: {shown.view.beneficiaryName} · Bank {shown.view.beneficiaryBankCode} · Rekening berakhir {shown.view.beneficiaryAccountSuffix}</p>
      <p>Rekening pembayaran: bank {shown.view.payingBankCode} · Berakhir {shown.view.payingAccountSuffix}</p>
      <p>Tanggal {shown.view.input.transferDate} · Referensi {shown.view.input.transferReference}</p>
      <details><summary>Prosedur yang berlaku</summary><p>Penerima: {shown.view.beneficiaryVerificationProcedureReference}</p><p>Pencatatan: {shown.view.payoutRecordingProcedureReference}</p></details>
      <p>Alasan internal: {(draftCommand ?? recordCommand)!.body.reason}</p>
      <label className={styles.checkbox}><input type="checkbox" disabled={disabled} checked={consent} onChange={event => setConsent(event.target.checked)} /><span>Saya telah memeriksa penerima, rekening, nilai dan referensi{recordCommand && ' serta gambar bukti asli'}. Catatan ini menunggu pemeriksaan debit bank independen dan belum membuktikan uang diterima pelanggan.</span></label>
      <button className={styles.primary} disabled={!canAct || !consent} onClick={() => void work(async () => {
        if (!consent || !currentRecord()) return; if (journal.list(actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
        if (draftCommand) { const saved = await submitTrackedRequest(draftLedger, journal, actorId, 'billing.refund.payout.draft', 'billing.refund.payout.draft', draftCommand, key => gateway.createDraft(draftCommand, key, currentRecord)); if (active()) await read(saved.draftId); }
        else if (recordCommand) { const command = recordCommand, ledger = recordLedgers.get(command.draftId) ?? new MutationLedger(() => 'rpr.' + command.draftId + '.' + crypto.randomUUID()); recordLedgers.set(command.draftId, ledger);
          await submitTrackedRequest(ledger, journal, actorId, 'billing.refund.payout.record', 'billing.refund.payout.record', command, key => gateway.record(command, key, currentRecord)); if (active()) { setSeen(null); await read(command.draftId); } }
      })}>{draftCommand ? 'Simpan draf refund' : 'Catat refund belum diverifikasi'}</button>
      <button className={styles.secondary} disabled={disabled} onClick={clearReview}>Kembali ke pemeriksaan refund</button></section>}
  </section>;
}
