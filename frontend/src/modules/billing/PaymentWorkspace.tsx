'use client';

import Link from 'next/link';
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
import { createPaymentGateway, paymentReservationOwner, preparePayment, type PaymentDraft, type PaymentInput, type PaymentEvidenceStatus } from './payment-client';
import common from '../administration/access.module.css';
import styles from './invoice.module.css';

type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
const rupiah = (value: string) => orderAmount('IDR', value);
const dayAt = (time: number) => new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Jakarta', year: 'numeric', month: '2-digit', day: '2-digit' }).format(time);
const empty = (time: number): PaymentInput => ({ invoiceIds: [], declaredAmount: '', transferDate: dayAt(time), payerName: '', bankReference: null, note: null });
function message(error: unknown) {
  if (error instanceof AccessFailure && ['evidence_storage_unavailable', 'evidence_policy_unavailable', 'payment_evidence_storage_unavailable'].includes(error.code)) return 'Penyimpanan bukti belum siap. Draf tetap tersimpan; hubungi penanggung jawab untuk mengaktifkan layanan bukti.';
  if (error instanceof AccessFailure && error.code === 'evidence_capacity_exhausted') return 'Kapasitas unggah sedang penuh. Hubungi penanggung jawab sebelum mencoba kembali.';
  if (error instanceof AccessFailure && ['payment_draft_changed', 'evidence_target_revision_conflict', 'payment_parties_locked'].includes(error.code)) return 'Draf atau kelompok penjual berubah. Muat draf terbaru dan periksa pilihan invoice.';
  if (error instanceof AccessFailure && error.code === 'payment_image_required') return 'Pilih satu foto JPG atau PNG yang tidak kosong, maksimal 10 MB.';
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
  const [evidence, setEvidence] = useState<PaymentEvidenceStatus[]>([]), [file, setFile] = useState<File | null>(null), [notice, setNotice] = useState(''), [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [offline, setOffline] = useState(false), [refresh, setRefresh] = useState(0), [before, setBefore] = useState<string | null>(null), [invoiceBefore, setInvoiceBefore] = useState<string | null>(null);
  const alive = useRef(false), pending = useRef(false), recoveredId = useRef<string | null>(null), heading = useRef<HTMLHeadingElement>(null), fileInput = useRef<HTMLInputElement>(null);
  const [uploadLedgers] = useState(() => new Map<string, MutationLedger>());
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
    const value = await gateway.read(account.actorId, id); if (!current()) return;
    setSelected(value); setInput({ ...value.input, invoiceIds: [...value.input.invoiceIds] }); setReason(''); setEvidence([]); setFile(null); if (fileInput.current) fileInput.current.value = '';
    const images = await gateway.evidence(account.actorId, id); if (current()) { setEvidence(images); heading.current?.focus(); }
  }
  async function save() {
    if (locked) return; await perform(async () => {
      if (recovery.journal.list(account.actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
      const command = preparePayment(account.actorId, selected?.id ?? null, selected?.revision ?? '0', input, reason.trim());
      const result = await submitTrackedRequest(ledger, recovery.journal, account.actorId, 'billing.payment.draft.save', 'billing.payment.draft.save', command, key => gateway.save(command, key, current));
      if (current()) { await open(result.declarationId); if (current()) { setNotice('Draf tersimpan. Pembayaran belum diajukan atau diverifikasi.'); setBefore(null); setRefresh(value => value + 1); } }
    });
  }
  async function upload(existing?: PaymentEvidenceStatus) {
    if (locked || !selected || !file || JSON.stringify(input) !== JSON.stringify(selected.input)) return; const owner = selected, chosen = file;
    await perform(async () => {
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
    });
  }
  const choices = new Map((selected?.invoices ?? []).map(item => [item.invoiceId, item]));
  for (const item of invoicePage.items) choices.set(item.id, { invoiceId: item.id, number: `${item.numberEntity}/${item.numberChannel}/${item.numberYear}/${item.number}`, total: item.detail.total, buyerName: item.detail.buyerName, sellerName: item.detail.sellerName });
  const hasPending = evidence.some(item => ['fresh', 'writing', 'publish_pending'].includes(item.state) && Date.parse(item.expiresAt) > now);
  const unsaved = selected !== null && JSON.stringify(input) !== JSON.stringify(selected.input);
  return <><AdministrationNavigation current="payments" actions={account.actions} locked={busy || recovering} />
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy || offline} onBusyChange={setRecovering} recoveryAdapters={{
      'billing.payment': { lookup: async entry => { const receipt = await gateway.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); recoveredId.current = receipt?.result.declarationId ?? null; return receipt; } },
      'evidence.payment-image': { lookup: async entry => { const receipt = await gateway.recoverReservation(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); recoveredId.current = receipt ? paymentReservationOwner(entry.key) : null; return receipt; } },
    }} onFailure={failure => { setError(failure); onDenied(failure); }} onResolved={() => { recovery.refresh(); ledger.resetAfterReconciliation(); for (const item of uploadLedgers.values()) item.resetAfterReconciliation(); const id = recoveredId.current; recoveredId.current = null;
      void (async () => { if (!current()) return; setBusy(true); try { if (id) await open(id); if (current()) setRefresh(value => value + 1); }
        catch (failure) { if (current()) { setError(failure); onDenied(failure); } } finally { if (alive.current) setBusy(false); } })(); }} />
    {recovery.error !== null && <p role="alert">{accessMessage(recovery.error)}</p>}{error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    {offline && <p role="status">Koneksi terputus. Draf dan berkas belum dapat dikirim.</p>}{busy && <p role="status">Memeriksa pembayaran…</p>}{notice && <p role="status">{notice}</p>}
    <section className={common.accountSection} aria-label="Draf pembayaran"><h2 ref={heading} tabIndex={-1}>{selected ? `Draf pembayaran ${selected.id}` : 'Catat pembayaran'}</h2>
      <p>Draf bersifat pribadi. Nilai transfer yang dicatat belum mengurangi tagihan. Bukti pembayaran wajib diperiksa sebelum dapat diajukan.</p>
      <form onSubmit={event => { event.preventDefault(); void save(); }}><fieldset disabled={locked}><legend>Invoice yang dibayar</legend><ul className={styles.candidates}>{[...choices.values()].map(item => <li key={item.invoiceId}><label>
        <input type="checkbox" checked={input.invoiceIds.includes(item.invoiceId)} disabled={locked || input.invoiceIds.length >= 20 && !input.invoiceIds.includes(item.invoiceId)} onChange={event => setInput(value => ({ ...value, invoiceIds: event.target.checked ? [...value.invoiceIds, item.invoiceId] : value.invoiceIds.filter(id => id !== item.invoiceId) }))} />
        <span><strong>{item.number}</strong><span>{item.buyerName} · {item.sellerName}</span><span>Total invoice {rupiah(item.total)}</span></span></label></li>)}</ul>
        {!choices.size && !busy && <p>Belum ada invoice dalam akses Anda.</p>}
        {invoicePage.hasMore && <button type="button" className={common.secondary} onClick={() => setInvoiceBefore(invoicePage.items.at(-1)!.id)}>Invoice lebih lama</button>}
        <label>Jumlah transfer (IDR)<input required inputMode="decimal" pattern="(?:0|[1-9][0-9]{0,14})(?:\.[0-9]{1,2})?" maxLength={18} value={input.declaredAmount} onChange={event => setInput(value => ({ ...value, declaredAmount: event.target.value }))} aria-describedby="payment-amount-help" /></label>
        <p id="payment-amount-help">Tanpa pemisah ribuan. Gunakan titik untuk sen, misalnya 150000.00.</p>
        <label>Tanggal transfer<input type="date" required max={dayAt(now)} value={input.transferDate} onChange={event => setInput(value => ({ ...value, transferDate: event.target.value }))} /></label>
        <label>Nama pengirim<input required maxLength={160} value={input.payerName} onChange={event => setInput(value => ({ ...value, payerName: event.target.value }))} /></label>
        <label>Referensi bank (opsional)<input maxLength={160} value={input.bankReference ?? ''} onChange={event => setInput(value => ({ ...value, bankReference: event.target.value || null }))} /></label>
        <label>Catatan (opsional)<textarea maxLength={1000} value={input.note ?? ''} onChange={event => setInput(value => ({ ...value, note: event.target.value || null }))} /></label>
        <label>Alasan pencatatan atau perubahan<textarea required maxLength={1000} value={reason} onChange={event => setReason(event.target.value)} /></label>
        <button className={common.primary} disabled={locked || input.invoiceIds.length === 0}>Simpan draf pembayaran</button></fieldset></form>
      {selected && <button className={common.secondary} disabled={locked} onClick={() => { setSelected(null); setInput(empty(now)); setReason(''); setEvidence([]); setFile(null); }}>Buat draf baru</button>}
    </section>
    {selected && <section className={common.accountSection} aria-label="Bukti asli pembayaran"><h2>Bukti asli pembayaran</h2><p>Pilih foto atau hasil pindai JPG/PNG, maksimal 10 MB. Berkas asli disimpan untuk pemeriksaan. Pemeriksaan keamanan belum tersedia; pembayaran tetap belum diajukan.</p>
      <label>Pilih bukti pembayaran<input ref={fileInput} type="file" accept="image/jpeg,image/png" disabled={locked} onChange={event => setFile(event.target.files?.[0] ?? null)} /></label>
      {unsaved && <p>Simpan perubahan draf sebelum mengunggah bukti.</p>}
      {file && <p>{file.name} · {Math.ceil(file.size / 1024)} KB</p>}<button className={common.primary} disabled={locked || !file || hasPending || unsaved} onClick={() => void upload()}>Unggah bukti asli</button>
      <ul className={styles.lines}>{evidence.map(item => <li key={item.reservationId}><strong>Upaya unggah {item.reservationId}</strong><p>{({ fresh: 'Siap menerima berkas', writing: 'Unggah belum terkonfirmasi', publish_pending: 'Penyimpanan perlu dikonfirmasi', interrupted: 'Unggah terputus; berkas belum lengkap', quarantined: 'Tersimpan · menunggu pemeriksaan keamanan' })[item.state]}</p>
        {item.state === 'fresh' && <button className={common.secondary} disabled={locked || !file || unsaved || Date.parse(item.expiresAt) <= now} onClick={() => void upload(item)}>Lanjutkan unggah ini</button>}
        {['writing', 'publish_pending'].includes(item.state) && <button className={common.secondary} disabled={locked} onClick={() => void perform(async () => { const result = await gateway.recoverUpload(account.actorId, selected.id, item.reservationId); if (current()) { setNotice(result.state === 'quarantined' ? 'Bukti asli tersimpan. Pembayaran belum diverifikasi.' : 'Hasil belum dapat dipastikan. Jangan mengulang unggah; periksa kembali atau hubungi penanggung jawab.'); const images = await gateway.evidence(account.actorId, selected.id); if (current()) setEvidence(images); } })}>Periksa hasil unggah</button>}</li>)}</ul>
      <button className={common.secondary} disabled={locked} onClick={() => void perform(async () => { const images = await gateway.evidence(account.actorId, selected.id); if (current()) setEvidence(images); })}>Muat status bukti</button>
    </section>}
    <section className={common.accountSection} aria-label="Daftar draf pembayaran"><h2>Draf pembayaran saya</h2><ul className={styles.candidates}>{page.items.map(item => <li key={item.id}><strong>Draf {item.id}</strong><p>{rupiah(item.input.declaredAmount)} · {item.input.transferDate} · {item.input.invoiceIds.length} invoice · Belum diajukan</p>
      <button className={common.secondary} disabled={locked} onClick={() => void perform(() => open(item.id))}>Buka draf {item.id}</button></li>)}</ul>{!busy && !page.items.length && <p>Belum ada draf pada halaman ini.</p>}
      {page.hasMore && <button className={common.secondary} disabled={locked} onClick={() => setBefore(page.items.at(-1)!.id)}>Draf lebih lama</button>}
      <button className={common.secondary} disabled={locked} onClick={() => { setBefore(null); setInvoiceBefore(null); setRefresh(value => value + 1); }}>Muat pembayaran terbaru</button></section>
  </>;
}
