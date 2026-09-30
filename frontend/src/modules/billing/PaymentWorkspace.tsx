'use client';

import Link from 'next/link';
import Image from 'next/image';
import { useCallback, useEffect, useRef, useState } from 'react';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { AdministrationNavigation } from '../administration/AdministrationNavigation';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import { submitTrackedRequest } from '../administration/operation-journal';
import { orderAmount } from '../ordering/order-quote-client';
import { createInvoiceGateway, type Invoice } from './invoice-client';
import { createPaymentGateway, paymentReservationOwner, paymentEvidenceLabel, preparePayment, preparePaymentSubmission, preparePaymentReplacement, type PaymentReplacementOrigin, type PaymentDraft, type PaymentInput, type PaymentEvidenceStatus } from './payment-client';
import common from '../administration/access.module.css';
import styles from './invoice.module.css';
import { PaymentOcrPanel } from './PaymentOcrPanel';
import type { PaymentOcrView, PaymentFollowUp } from './payment-client';

type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
const rupiah = (value: string) => orderAmount('IDR', value);
const transferDay = (value: string) => new Intl.DateTimeFormat('id-ID', { timeZone: 'Asia/Jakarta', day: 'numeric', month: 'long', year: 'numeric' }).format(new Date(value + 'T00:00:00+07:00'));
const dayAt = (time: number) => new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Jakarta', year: 'numeric', month: '2-digit', day: '2-digit' }).format(time);
const empty = (time: number): PaymentInput => ({ invoiceIds: [], declaredAmount: '', transferDate: dayAt(time), payerName: '', bankReference: null, note: null });
function message(error: unknown) {
  if (error instanceof AccessFailure && ['evidence_storage_unavailable', 'evidence_policy_unavailable', 'payment_evidence_storage_unavailable'].includes(error.code)) return 'Penyimpanan bukti belum siap. Draf tetap tersimpan; hubungi penanggung jawab untuk mengaktifkan layanan bukti.';
  if (error instanceof AccessFailure && error.code === 'evidence_capacity_exhausted') return 'Kapasitas unggah sedang penuh. Hubungi penanggung jawab sebelum mencoba kembali.';
  if (error instanceof AccessFailure && ['payment_draft_changed', 'evidence_target_revision_conflict', 'payment_parties_locked'].includes(error.code)) return 'Draf atau kelompok penjual berubah. Muat draf terbaru dan periksa pilihan invoice.';
  if (error instanceof AccessFailure && error.code === 'payment_image_required') return 'Pilih satu foto JPG atau PNG yang tidak kosong, maksimal 10 MB.';
  if (error instanceof AccessFailure && error.code === 'payment_replacement_exists') return 'Draf pengganti sudah dibuat. Muat permintaan terbaru untuk membuka draf tersebut.';
  if (error instanceof AccessFailure && ['evidence_read_not_eligible', 'evidence_read_policy_unavailable'].includes(error.code)) return 'Bukti belum dapat ditampilkan. Muat status terbaru atau hubungi penanggung jawab pemeriksaan bukti.';
  if (error instanceof AccessFailure && ['evidence_read_lease_expired', 'evidence_read_reference_changed', 'evidence_read_integrity_unavailable', 'evidence_validation_integrity', 'payment_response_invalid'].includes(error.code)) return 'Bukti tidak dapat ditampilkan dengan aman. Muat draf terbaru dan periksa kembali; hubungi penanggung jawab jika masalah berlanjut.';
  if (error instanceof IdentityFailure) return 'Masuk kembali untuk memeriksa draf pembayaran Anda.';
  return accessMessage(error);
}
export function PaymentWorkspace() {
  const [identity] = useState(createIdentityGateway), [access] = useState(createAccessGateway);
  const [account, setAccount] = useState<Account | null>(null), [loading, setLoading] = useState(true), [error, setError] = useState<unknown>(null), [refresh, setRefresh] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => { let active = true; const controller = new AbortController(); void Promise.resolve().then(async () => {
    if (!active) return; setLoading(true); setAccount(null); setError(null);
    try {
      const session = await identity.session(controller.signal), observedAt = performance.now();
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
      const current = await access.current(); if (!active) return;
      if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
      if (!['payment.evidence', 'invoice.read'].every(action => current.allowedActions.includes(action))) throw new AccessFailure(403, 'scope_denied');
      if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
      setAccount({ actorId: current.userId, actions: current.allowedActions, session, observedAt }); setElapsed(performance.now() - observedAt);
    } catch (failure) { if (active) setError(failure); } finally { if (active) setLoading(false); }
  }); return () => { active = false; controller.abort(); }; }, [identity, access, refresh]);
  useEffect(() => { const timer = window.setInterval(() => { if (account) setElapsed(performance.now() - account.observedAt); }, 1000);
    const restored = (event: PageTransitionEvent) => { if (event.persisted) { setAccount(null); setRefresh(value => value + 1); } };
    window.addEventListener('pageshow', restored); return () => { window.clearInterval(timer); window.removeEventListener('pageshow', restored); }; }, [account]);
  const denied = useCallback((failure: unknown) => { if (failure instanceof AccessFailure && (failure.status === 401 || failure.status === 403 || failure.code === 'actor_changed')) { setAccount(null); setError(failure); } }, []);
  return <main className={common.page}><header className={common.header}><div><Link href="/identity/account" className={common.brand}>Unit Toko</Link><h1>Bukti pembayaran</h1><p>Catat transfer dan simpan bukti asli untuk invoice cabang Anda.</p></div><Link href="/billing/invoices" className={common.secondary}>Lihat tagihan</Link></header>
    {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    {loading ? <p role="status">Memeriksa akun…</p> : !account || sessionTiming(account.session, elapsed).expired ? <section className={common.state}><h2>Periksa sesi dan akses</h2>
      <Link href="/identity/login?returnPath=%2Fbilling%2Fpayments">Masuk kembali</Link><button className={common.secondary} onClick={() => setRefresh(value => value + 1)}>Perbarui sesi dan akses</button></section>
      : <PaymentDesk key={account.actorId + ':' + account.observedAt} account={account} now={Date.parse(account.session.serverTime) + elapsed} onDenied={denied} />}
  </main>;
}
function PaymentDesk({ account, now, onDenied }: { account: Account; now: number; onDenied: (failure: unknown) => void }) {
  const [gateway] = useState(createPaymentGateway), [invoices] = useState(createInvoiceGateway), [ledger] = useState(() => new MutationLedger()), recovery = useOperationRecovery(account.actorId);
  const [page, setPage] = useState<{ items: PaymentDraft[]; hasMore: boolean }>({ items: [], hasMore: false }), [invoicePage, setInvoicePage] = useState<{ items: Invoice[]; hasMore: boolean }>({ items: [], hasMore: false });
  const [selected, setSelected] = useState<PaymentDraft | null>(null), [input, setInput] = useState<PaymentInput>(() => empty(now)), [reason, setReason] = useState('');
  const [ocr, setOcr] = useState<PaymentOcrView | null>(null);
  const [followUp, setFollowUp] = useState<PaymentFollowUp | null>(null);
  const [origin, setOrigin] = useState<PaymentReplacementOrigin | null>(null), [replacement, setReplacement] = useState<ReturnType<typeof preparePaymentReplacement> | null>(null);
  const replacementHeading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { if (replacement) replacementHeading.current?.focus(); }, [replacement]);
  const [evidence, setEvidence] = useState<PaymentEvidenceStatus[]>([]), [file, setFileState] = useState<File | null>(null), [notice, setNotice] = useState(''), [error, setError] = useState<unknown>(null);
  const [localPreview, setLocalPreview] = useState<{ url: string; rotation: number } | null>(null), [uploading, setUploading] = useState(false);
  const localPreviewUrl = useRef<string | null>(null), cameraInput = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [offline, setOffline] = useState(false), [refresh, setRefresh] = useState(0), [before, setBefore] = useState<string | null>(null), [invoiceBefore, setInvoiceBefore] = useState<string | null>(null);
  const alive = useRef(false), pending = useRef(false), recoveredId = useRef<string | null>(null), heading = useRef<HTMLHeadingElement>(null), fileInput = useRef<HTMLInputElement>(null);
  const [uploadLedgers] = useState(() => new Map<string, MutationLedger>());
  const [submissionLedgers] = useState(() => new Map<string, MutationLedger>());
  const [replacementLedgers] = useState(() => new Map<string, MutationLedger>());
  function setFile(next: File | null) {
    setError(null);
    if (localPreviewUrl.current) URL.revokeObjectURL(localPreviewUrl.current); localPreviewUrl.current = null; setLocalPreview(null); setFileState(null);
    if (fileInput.current) fileInput.current.value = ''; if (cameraInput.current) cameraInput.current.value = '';
    if (!next) return;
    if (next.size <= 0 || next.size > 10 * 1024 * 1024 || !['image/jpeg', 'image/png'].includes(next.type)) { setError(new AccessFailure(400, 'payment_image_required')); return; }
    const url = URL.createObjectURL(next); localPreviewUrl.current = url; setFileState(next); setLocalPreview({ url, rotation: 0 });
  }
  useEffect(() => () => { if (localPreviewUrl.current) URL.revokeObjectURL(localPreviewUrl.current); localPreviewUrl.current = null; }, []);
  const [confirmation, setConfirmation] = useState<ReturnType<typeof preparePaymentSubmission> | null>(null);
  const confirmationHeading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { if (confirmation) confirmationHeading.current?.focus(); }, [confirmation]);
  const [preview, setPreview] = useState<{ url: string; reservationId: string } | null>(null);
  const previewUrl = useRef<string | null>(null), previewEpoch = useRef(0), previewHeading = useRef<HTMLHeadingElement>(null), previewTrigger = useRef<HTMLButtonElement | null>(null);
  const closePreview = useCallback((restoreFocus = false) => {
    previewEpoch.current++; if (previewUrl.current) URL.revokeObjectURL(previewUrl.current); previewUrl.current = null; setPreview(null);
    if (restoreFocus) previewTrigger.current?.focus();
  }, []);
  useEffect(() => () => { previewEpoch.current++; if (previewUrl.current) URL.revokeObjectURL(previewUrl.current); previewUrl.current = null; }, []);
  useEffect(() => { if (preview) previewHeading.current?.focus(); }, [preview]);
  const current = useCallback(() => alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const locked = busy || recovering || offline || recovery.locked;
  useEffect(() => { alive.current = true; const connection = () => setOffline(!navigator.onLine); connection(); window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => { alive.current = false; window.removeEventListener('online', connection); window.removeEventListener('offline', connection); }; }, []);
  useEffect(() => { let active = true; const controller = new AbortController(); void Promise.resolve().then(async () => {
    if (!active) return; setBusy(true); setError(null); setPage({ items: [], hasMore: false }); setInvoicePage({ items: [], hasMore: false });
    try {
      const list = await gateway.list(account.actorId, before, controller.signal); if (!active || !current()) return;
      const bills = await invoices.list(account.actorId, invoiceBefore, controller.signal); if (!active || !current()) return; setPage(list); setInvoicePage(bills);
    } catch (failure) { if (active && current()) { setError(failure); onDenied(failure); } } finally { if (active) setBusy(false); }
  }); return () => { active = false; controller.abort(); }; }, [gateway, invoices, account.actorId, before, invoiceBefore, refresh, current, onDenied]);
  async function perform(work: () => Promise<void>) {
    if (!current() || busy || recovering || offline || pending.current) return; pending.current = true; setBusy(true); setError(null); setNotice('');
    try { await work(); } catch (failure) { if (current()) { setError(failure); onDenied(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); recovery.refresh(); } }
  }
  async function open(id: string) {
    closePreview(); setConfirmation(null); setOcr(null); setFollowUp(null); setReplacement(null); setOrigin(null); setSelected(null);
    const value = await gateway.read(account.actorId, id); if (!current()) return;
    const linked = await gateway.replacementOrigin(account.actorId, value); if (!current()) return; setOrigin(linked);
    setSelected(value); setInput({ ...value.input, invoiceIds: [...value.input.invoiceIds] }); setReason(''); setEvidence([]); setFile(null); if (fileInput.current) fileInput.current.value = '';
    const images = await gateway.evidence(account.actorId, id); if (current()) { setEvidence(images); heading.current?.focus(); }
  }
  async function save() {
    if (locked || selected?.state === 'submitted' || confirmation) return; closePreview(); await perform(async () => {
      if (recovery.journal.list(account.actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
      const command = preparePayment(account.actorId, selected?.id ?? null, selected?.revision ?? '0', input, reason.trim());
      const result = await submitTrackedRequest(ledger, recovery.journal, account.actorId, 'billing.payment.draft.save', 'billing.payment.draft.save', command, key => gateway.save(command, key, current));
      if (current()) { await open(result.declarationId); if (current()) { setNotice('Draf tersimpan. Pembayaran belum diajukan atau diverifikasi.'); setBefore(null); setRefresh(value => value + 1); } }
    });
  }
  async function upload(existing?: PaymentEvidenceStatus) {
    if (locked || !selected || selected.state !== 'draft' || confirmation || !file || JSON.stringify(input) !== JSON.stringify(selected.input)) return; const owner = selected, chosen = file;
    setUploading(true); try { await perform(async () => {
      if (chosen.size <= 0 || chosen.size > 10 * 1024 * 1024 || !['image/jpeg', 'image/png'].includes(chosen.type)) throw new AccessFailure(400, 'payment_image_required');
      if (recovery.journal.list(account.actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
      const ownerLedger = uploadLedgers.get(owner.id) ?? new MutationLedger(() => 'p.' + owner.id + '.' + crypto.randomUUID()); uploadLedgers.set(owner.id, ownerLedger);
      const reservation = existing ?? await submitTrackedRequest(ownerLedger, recovery.journal, account.actorId, 'evidence.payment-image.reserve', 'evidence.payment-image.reserve', { declarationId: owner.id, revision: owner.revision }, key => gateway.reserve(account.actorId, owner.id, owner.revision, key, current));
      if (!current()) return;
      let uploadFailure: unknown = null;
      try {
        const result = await gateway.upload(account.actorId, owner.id, reservation.reservationId, reservation.revision, chosen, current);
        if (current()) setNotice(result.state === 'quarantined' ? 'Bukti asli tersimpan dan menunggu pemeriksaan keamanan. Pembayaran belum diverifikasi.' : 'Unggah belum selesai. Periksa hasil upaya yang sama sebelum mengirim berkas lagi.');
      } catch (failure) { uploadFailure = failure; }
      if (current()) {
        setFile(null); if (fileInput.current) fileInput.current.value = '';
        const images = await gateway.evidence(account.actorId, owner.id); if (!current()) return; setEvidence(images);
        if (images.some(item => item.reservationId === reservation.reservationId && item.state === 'quarantined')) {
          setNotice('Bukti asli tersimpan dan menunggu pemeriksaan keamanan. Pembayaran belum diverifikasi.'); return;
        }
      }
      if (uploadFailure !== null) throw uploadFailure;
    }); } finally { if (alive.current) setUploading(false); }
  }
  async function viewOriginal(item: PaymentEvidenceStatus, trigger: HTMLButtonElement) {
    if (locked || !selected) return; const owner = selected; closePreview(); previewTrigger.current = trigger; const epoch = previewEpoch.current;
    const stillCurrent = () => current() && previewEpoch.current === epoch;
    await perform(async () => {
      const blob = await gateway.original(account.actorId, owner.id, owner.revision, item, stillCurrent);
      if (!stillCurrent()) return; const url = URL.createObjectURL(blob); previewUrl.current = url; setPreview({ url, reservationId: item.reservationId });
    });
  }
  function reviewSubmission(item: PaymentEvidenceStatus) {
    if (locked || !selected || selected.state !== 'draft' || JSON.stringify(input) !== JSON.stringify(selected.input)) return;
    try { setConfirmation(preparePaymentSubmission(account.actorId, selected, item, 'Pengajuan bukti pembayaran untuk pemeriksaan')); setError(null); }
    catch (failure) { setError(failure); }
  }
  async function submit() {
    if (locked || !confirmation || !selected || selected.id !== confirmation.declarationId || selected.revision !== confirmation.revision || selected.state !== 'draft') return;
    const command = confirmation; closePreview(); await perform(async () => {
      if (recovery.journal.list(account.actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
      const ownerLedger = submissionLedgers.get(command.declarationId) ?? new MutationLedger(() => 's.' + command.declarationId + '.' + crypto.randomUUID());
      submissionLedgers.set(command.declarationId, ownerLedger);
      const result = await submitTrackedRequest(ownerLedger, recovery.journal, account.actorId, 'billing.payment.submit', 'billing.payment.submit', command, key => gateway.submit(command, key, current));
      if (current()) { await open(result.declarationId); if (current()) { setNotice('Bukti diajukan. Pembayaran menunggu verifikasi; saldo tagihan belum berubah.'); setBefore(null); setRefresh(value => value + 1); } }
    });
  }
  async function previewReplacement() {
    if (locked || !selected || followUp?.state !== 'replacement_requested') return;
    const owner = selected; await perform(async () => {
      setReplacement(null); const shown = await gateway.replacement(account.actorId, owner); if (!current()) return;
      if (shown.existing) { await open(shown.existing.declarationId); return; }
      setReplacement(preparePaymentReplacement(account.actorId, owner, shown, 'Draf pengganti atas permintaan pemeriksaan bukti'));
    });
  }
  async function createReplacement() {
    if (locked || !replacement || !selected || selected.id !== replacement.parentId || selected.revision !== replacement.revision) return;
    const command = replacement; await perform(async () => {
      if (recovery.journal.list(account.actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
      const ownerLedger = replacementLedgers.get(command.parentId) ?? new MutationLedger(() => 'rp.' + command.parentId + '.' + crypto.randomUUID()); replacementLedgers.set(command.parentId, ownerLedger);
      const result = await submitTrackedRequest(ownerLedger, recovery.journal, account.actorId, 'billing.payment.replacement.create', 'billing.payment.replacement.create', command, key => gateway.createReplacement(command, key, current));
      if (current()) { await open(result.declarationId); if (current()) { setNotice('Draf pengganti tersimpan. Periksa data transfer dan unggah bukti asli baru sebelum mengajukan.'); setBefore(null); setRefresh(value => value + 1); } }
    });
  }
  const choices = new Map((selected?.invoices ?? []).map(item => [item.invoiceId, item]));
  for (const item of invoicePage.items) choices.set(item.id, { invoiceId: item.id, number: `${item.numberEntity}/${item.numberChannel}/${item.numberYear}/${item.number}`, total: item.detail.total, buyerName: item.detail.buyerName, sellerName: item.detail.sellerName });
  const hasPending = evidence.some(item => ['fresh', 'writing', 'publish_pending'].includes(item.state) && Date.parse(item.expiresAt) > now);
  const unsaved = selected !== null && JSON.stringify(input) !== JSON.stringify(selected.input);
  const submitted = selected?.state === 'submitted';
  return <><AdministrationNavigation current="payments" actions={account.actions} locked={busy || recovering} />
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy || offline} onBusyChange={setRecovering} recoveryAdapters={{
      'billing.payment.replacement.create': { lookup: async entry => { const receipt = await gateway.recoverReplacement(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); recoveredId.current = receipt?.result.declarationId ?? null; return receipt; } },
      'billing.payment.submit': { lookup: async entry => { const receipt = await gateway.recoverSubmission(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); recoveredId.current = receipt?.result.declarationId ?? null; return receipt; } },
      'billing.payment': { lookup: async entry => { const receipt = await gateway.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); recoveredId.current = receipt?.result.declarationId ?? null; return receipt; } },
      'evidence.payment-image': { lookup: async entry => { const receipt = await gateway.recoverReservation(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); recoveredId.current = receipt ? paymentReservationOwner(entry.key) : null; return receipt; } },
    }} onFailure={failure => { setError(failure); onDenied(failure); }} onResolved={() => { recovery.refresh(); ledger.resetAfterReconciliation(); for (const item of [...uploadLedgers.values(), ...submissionLedgers.values(), ...replacementLedgers.values()]) item.resetAfterReconciliation(); const id = recoveredId.current; recoveredId.current = null;
      void (async () => { if (!current()) return; setBusy(true); try { if (id) await open(id); if (current()) setRefresh(value => value + 1); }
        catch (failure) { if (current()) { setError(failure); onDenied(failure); } } finally { if (alive.current) setBusy(false); } })(); }} />
    {recovery.error !== null && <p role="alert">{accessMessage(recovery.error)}</p>}{error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    {offline && <p role="status">Koneksi terputus. Draf dan berkas belum dapat dikirim.</p>}{busy && <p role="status">Memeriksa pembayaran…</p>}{notice && <p role="status">{notice}</p>}
    <section className={common.accountSection} aria-label="Draf pembayaran"><h2 ref={heading} tabIndex={-1}>{selected ? `${submitted ? 'Pembayaran' : 'Draf pembayaran'} ${selected.id}` : 'Catat pembayaran'}</h2>
      {origin && <p>Pengganti pengajuan {origin.parentDeclarationId}. Invoice tetap mengikuti pengajuan asli. <button className={common.secondary} disabled={locked} onClick={() => void perform(() => open(origin.parentDeclarationId))}>Buka pengajuan asli</button></p>}
      {submitted ? <p role="status"><strong>Pengajuan tersimpan</strong> · Data dan bukti pengajuan telah dikunci. Periksa tindak lanjut petugas dan rincian saldo pada invoice.</p>
        : <p>Draf bersifat pribadi. Nilai transfer yang dicatat belum mengurangi tagihan. Bukti pembayaran wajib diperiksa sebelum dapat diajukan.</p>}
      {submitted && selected ? <><dl className={styles.facts}>
        <div><dt>Jumlah transfer</dt><dd>{rupiah(selected.input.declaredAmount)}</dd></div><div><dt>Tanggal transfer</dt><dd>{transferDay(selected.input.transferDate)}</dd></div>
        <div><dt>Nama pengirim</dt><dd>{selected.input.payerName}</dd></div><div><dt>Referensi bank</dt><dd>{selected.input.bankReference ?? 'Belum dicantumkan'}</dd></div>
        {selected.input.note && <div><dt>Catatan</dt><dd>{selected.input.note}</dd></div>}</dl>
        <h3>Invoice yang diajukan</h3><ul className={styles.lines}>{selected.invoices.map(item => <li key={item.invoiceId}><strong>{item.number}</strong><p>{item.buyerName} · {item.sellerName}</p><p>Total invoice {rupiah(item.total)}</p></li>)}</ul>
        <p>Jumlah transfer ini belum dialokasikan. Saldo invoice tetap menunggu pemeriksaan dana masuk.</p></>
      : <form onChangeCapture={() => { closePreview(); setConfirmation(null); }} onSubmit={event => { event.preventDefault(); void save(); }}><fieldset disabled={locked || confirmation !== null}><legend>Invoice yang dibayar</legend><ul className={styles.candidates}>{[...choices.values()].map(item => <li key={item.invoiceId}><label>
        <input type="checkbox" checked={input.invoiceIds.includes(item.invoiceId)} disabled={locked || origin !== null || input.invoiceIds.length >= 20 && !input.invoiceIds.includes(item.invoiceId)} onChange={event => setInput(value => ({ ...value, invoiceIds: event.target.checked ? [...value.invoiceIds, item.invoiceId] : value.invoiceIds.filter(id => id !== item.invoiceId) }))} />
        <span><strong>{item.number}</strong><span>{item.buyerName} · {item.sellerName}</span><span>Total invoice {rupiah(item.total)}</span></span></label></li>)}</ul>
        {!choices.size && !busy && <p>Belum ada invoice dalam akses Anda.</p>}
        {invoicePage.hasMore && <button type="button" className={common.secondary} onClick={() => setInvoiceBefore(invoicePage.items.at(-1)!.id)}>Invoice lebih lama</button>}
        <label>Jumlah transfer (IDR)<input required inputMode="decimal" pattern="(?:0|[1-9][0-9]{0,14})(?:\.[0-9]{1,2})?" maxLength={18} value={input.declaredAmount} onChange={event => setInput(value => ({ ...value, declaredAmount: event.target.value }))} aria-describedby="payment-amount-help" /></label>
        <p id="payment-amount-help">Tanpa pemisah ribuan. Gunakan titik untuk sen, misalnya 150000.00.</p>
        <label>Tanggal transfer<input type="date" required max={dayAt(now)} value={input.transferDate} onChange={event => setInput(value => ({ ...value, transferDate: event.target.value }))} /></label>
        <label>Nama pengirim<input required maxLength={160} value={input.payerName} onChange={event => setInput(value => ({ ...value, payerName: event.target.value }))} /></label>
        <label>Referensi bank (opsional)<input maxLength={160} value={input.bankReference ?? ''} onChange={event => setInput(value => ({ ...value, bankReference: event.target.value || null }))} /></label>
        <label htmlFor="payment-note">Catatan (opsional)</label><textarea id="payment-note" maxLength={1000} value={input.note ?? ''} onChange={event => setInput(value => ({ ...value, note: event.target.value || null }))} />
        <label htmlFor="payment-reason">Alasan pencatatan atau perubahan</label><textarea id="payment-reason" required maxLength={1000} value={reason} onChange={event => setReason(event.target.value)} />
        <button className={common.primary} disabled={locked || input.invoiceIds.length === 0}>Simpan draf pembayaran</button></fieldset></form>}
      {selected && <button className={common.secondary} disabled={locked} onClick={() => { closePreview(); setConfirmation(null); setReplacement(null); setOrigin(null); setSelected(null); setInput(empty(now)); setReason(''); setEvidence([]); setFile(null); }}>Buat draf baru</button>}
    </section>
    {submitted && selected && <section className={common.accountSection} aria-label="Tindak lanjut pengajuan"><h2>Tindak lanjut pengajuan</h2>
      <button className={common.secondary} disabled={locked} onClick={() => void perform(async () => { setFollowUp(null); setReplacement(null); const result = await gateway.followUp(account.actorId, selected); if (current()) setFollowUp(result); })}>Muat tindak lanjut petugas</button>
      {followUp?.declarationId === selected.id && <><p role="status">{{ pending: 'Belum ada penanggung jawab tindak lanjut', assigned: 'Penanggung jawab tindak lanjut telah ditetapkan', rejected: 'Pengajuan bukti ditolak', replacement_requested: 'Petugas meminta bukti pengganti' }[followUp.state]}</p>
        {followUp.recordedAt && <p>Diperbarui {new Intl.DateTimeFormat('id-ID', { dateStyle: 'medium', timeStyle: 'short', timeZone: 'Asia/Jakarta' }).format(new Date(followUp.recordedAt))}.</p>}
        {['rejected', 'replacement_requested'].includes(followUp.state) && <p>Pengajuan ini telah ditutup untuk verifikasi baru. Data dan bukti aslinya tetap tersimpan.</p>}
        {followUp.notification.state !== 'not_applicable' && <p>Notifikasi aplikasi: {{ pending: 'Menunggu layanan', running: 'Sedang diproses', retry: 'Menunggu pengulangan layanan', failed: 'Belum tersedia; hubungi penanggung jawab tindak lanjut', available: 'Pemberitahuan tindak lanjut tersedia pada akun ini' }[followUp.notification.state]}. Status ini tidak menyatakan email terkirim atau dana diterima.</p>}
        {followUp.state === 'replacement_requested' && <button className={common.secondary} disabled={locked} onClick={() => void previewReplacement()}>Siapkan bukti pengganti</button>}
        {replacement?.parentId === selected.id && <section aria-label="Konfirmasi draf pengganti"><h3 ref={replacementHeading} tabIndex={-1}>Buat draf bukti pengganti</h3>
          <p>Invoice dan data transfer disalin ke draf terpisah. Periksa nilai transfer, unggah bukti asli baru, lalu ajukan untuk pemeriksaan. Pengajuan {selected.id} tetap terkunci; saldo invoice belum berubah.</p>
          <button className={common.primary} disabled={locked} onClick={() => void createReplacement()}>Buat draf pengganti</button>
          <button className={common.secondary} disabled={locked} onClick={() => setReplacement(null)}>Kembali ke tindak lanjut</button></section>}
        <p>Tindak lanjut bukti tidak menetapkan penerimaan dana atau saldo invoice.</p></>}
    </section>}
    {submitted && selected && <PaymentOcrPanel view={ocr} locked={locked} onRefresh={() => void perform(async () => { setOcr(null); const result = await gateway.ocr(account.actorId, selected); if (current()) setOcr(result); })} />}
    {selected && <section className={common.accountSection} aria-label="Bukti asli pembayaran"><h2>Bukti asli pembayaran</h2><p>{submitted ? 'Bukti berikut terikat pada pengajuan ini. Pemeriksaan keamanan gambar tidak membuktikan dana diterima.' : 'Pilih foto atau hasil pindai JPG/PNG, maksimal 10 MB. Berkas asli disimpan untuk pemeriksaan. Hasil pemeriksaan keamanan tidak memverifikasi pembayaran; draf tetap belum diajukan.'}</p>
      {!submitted && <><label>Pilih bukti pembayaran<input ref={fileInput} type="file" accept="image/jpeg,image/png" disabled={locked || confirmation !== null} onChange={event => setFile(event.target.files?.[0] ?? null)} /></label>
      <label>Ambil foto bukti pembayaran<input ref={cameraInput} type="file" accept="image/jpeg,image/png" capture="environment" disabled={locked || confirmation !== null} onChange={event => setFile(event.target.files?.[0] ?? null)} /></label>
      {unsaved && <p>Simpan perubahan draf sebelum mengunggah bukti.</p>}
      {file && <p>{file.name} · {Math.ceil(file.size / 1024)} KB</p>}
      {localPreview && <section className={styles.evidencePreview} aria-label="Pratinjau berkas pilihan"><h3>Pratinjau berkas pilihan</h3><p>Putar tampilan untuk memeriksa keterbacaan. Berkas asli tetap utuh; gambar belum diunggah atau lolos pemeriksaan keamanan.</p>
        <div className={styles.evidenceImage} style={{ width: 'min(100%, 560px)', height: 'auto', minHeight: 0, aspectRatio: '1' }}><Image key={localPreview.url} src={localPreview.url} alt="Bukti pembayaran pilihan sebelum unggah" fill unoptimized sizes="(max-width: 600px) 90vw, 560px" style={{ objectFit: 'contain', transform: `rotate(${localPreview.rotation}deg)` }} onError={() => { if (localPreviewUrl.current !== localPreview.url) return; setFile(null); setError(new AccessFailure(400, 'payment_image_required')); }} /></div>
        <button type="button" className={common.secondary} disabled={locked || confirmation !== null} onClick={() => setLocalPreview(value => value ? { ...value, rotation: (value.rotation + 90) % 360 } : null)}>Putar tampilan 90°</button>
        <button type="button" className={common.secondary} disabled={locked || confirmation !== null} onClick={() => { setFile(null); fileInput.current?.focus(); }}>Hapus pilihan berkas</button>
      </section>}
      {uploading && <div role="status"><progress aria-label="Proses unggah bukti asli" /><p>Menyimpan bukti asli. Tunggu hasil upaya ini sebelum mencoba kembali.</p></div>}
      <button className={common.primary} disabled={locked || !file || hasPending || unsaved || confirmation !== null} onClick={() => void upload()}>Unggah bukti asli</button></>}
      <ul className={styles.lines}>{evidence.filter(item => !submitted || item.reservationId === selected.submission?.reservationId).map(item => <li key={item.reservationId}><strong>Upaya unggah {item.reservationId}</strong><p>{submitted && item.scanStatus === 'clean' && item.decoderStatus === 'decoded' && item.safeFailure === null ? 'Bukti pengajuan · pemeriksaan keamanan selesai' : paymentEvidenceLabel(item)}</p>
        {item.scanStatus === 'clean' && item.decoderStatus === 'decoded' && item.safeFailure === null && <button className={common.secondary} disabled={locked || unsaved} onClick={event => void viewOriginal(item, event.currentTarget)}>Lihat bukti asli</button>}
        {!submitted && item.scanStatus === 'clean' && item.decoderStatus === 'decoded' && item.safeFailure === null && <button className={common.primary} disabled={locked || unsaved || confirmation !== null} onClick={() => reviewSubmission(item)}>Pilih bukti untuk diajukan</button>}
        {!submitted && item.state === 'fresh' && <button className={common.secondary} disabled={locked || !file || unsaved || confirmation !== null || Date.parse(item.expiresAt) <= now} onClick={() => void upload(item)}>Lanjutkan unggah ini</button>}
        {!submitted && ['writing', 'publish_pending'].includes(item.state) && <button className={common.secondary} disabled={locked} onClick={() => void perform(async () => { const result = await gateway.recoverUpload(account.actorId, selected.id, item.reservationId); if (current()) { setNotice(result.state === 'quarantined' ? 'Bukti asli tersimpan. Pembayaran belum diverifikasi.' : 'Hasil belum dapat dipastikan. Jangan mengulang unggah; periksa kembali atau hubungi penanggung jawab.'); const images = await gateway.evidence(account.actorId, selected.id); if (current()) setEvidence(images); } })}>Periksa hasil unggah</button>}</li>)}</ul>
      <button className={common.secondary} disabled={locked} onClick={() => { closePreview(); void perform(async () => { const images = await gateway.evidence(account.actorId, selected.id); if (current()) setEvidence(images); }); }}>Muat status bukti</button>
      {preview && <section aria-labelledby="payment-original-heading" className={styles.evidencePreview}>
        <h3 id="payment-original-heading" ref={previewHeading} tabIndex={-1}>Bukti asli · unggah {preview.reservationId}</h3>
        <p>Gambar ini berasal dari bukti yang tersimpan. {submitted ? 'Periksa tindak lanjut petugas dan rincian saldo pada invoice.' : 'Pembayaran belum diajukan atau diverifikasi.'}</p>
        <div className={styles.evidenceImage}><Image src={preview.url} alt={`Bukti transfer asli untuk draf pembayaran ${selected.id}`} fill unoptimized sizes="(max-width: 600px) 90vw, 800px" style={{ objectFit: 'contain' }} onError={() => { closePreview(); setError(new AccessFailure(502, 'payment_response_invalid')); }} /></div>
        <button className={common.secondary} onClick={() => closePreview(true)}>Tutup bukti</button>
      </section>}
    </section>}
    {confirmation && selected && <section className={common.accountSection} aria-label="Periksa pengajuan pembayaran"><h2 ref={confirmationHeading} tabIndex={-1}>Periksa pengajuan pembayaran</h2>
      <p><strong>{rupiah(selected.input.declaredAmount)}</strong> · {transferDay(selected.input.transferDate)} · {selected.input.payerName}</p>
      <p>Referensi bank: {selected.input.bankReference ?? 'Belum dicantumkan'}. Bukti terpilih: unggah {confirmation.body.reservationId}.</p>
      <ul>{selected.invoices.map(item => <li key={item.invoiceId}>{item.number} · {item.sellerName} · {rupiah(item.total)}</li>)}</ul>
      <p>Pengajuan mengunci data dan bukti ini untuk pemeriksaan. Pembacaan otomatis (OCR) dapat menyusul. Petugas yang berwenang tetap harus memeriksa dana masuk; pengajuan tidak mengurangi saldo tagihan.</p>
      <button className={common.primary} disabled={locked} onClick={() => void submit()}>Ajukan untuk verifikasi</button>
      <button className={common.secondary} disabled={locked} onClick={() => { setConfirmation(null); heading.current?.focus(); }}>Kembali ke draf</button>
    </section>}
    <section className={common.accountSection} aria-label="Daftar draf pembayaran"><h2>Pembayaran saya</h2><ul className={styles.candidates}>{page.items.map(item => <li key={item.id}><strong>{item.state === 'submitted' ? 'Pembayaran' : 'Draf'} {item.id}</strong><p>{rupiah(item.input.declaredAmount)} · {transferDay(item.input.transferDate)} · {item.input.invoiceIds.length} invoice · {item.state === 'submitted' ? 'Pengajuan tersimpan' : 'Belum diajukan'}</p>
      <button className={common.secondary} disabled={locked} onClick={() => void perform(() => open(item.id))}>Buka {item.state === 'submitted' ? 'pembayaran' : 'draf'} {item.id}</button></li>)}</ul>{!busy && !page.items.length && <p>Belum ada pembayaran pada halaman ini.</p>}
      {page.hasMore && <button className={common.secondary} disabled={locked} onClick={() => setBefore(page.items.at(-1)!.id)}>Draf lebih lama</button>}
      <button className={common.secondary} disabled={locked} onClick={() => { setBefore(null); setInvoiceBefore(null); setRefresh(value => value + 1); }}>Muat pembayaran terbaru</button></section>
  </>;
}
