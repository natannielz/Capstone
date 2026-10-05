'use client';

import Link from 'next/link';
import Image from 'next/image';
import { useCallback, useEffect, useRef, useState } from 'react';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure, accessMessage } from '../administration/workflow';
import { AdministrationNavigation } from '../administration/AdministrationNavigation';
import { orderAmount } from '../ordering/order-quote-client';
import { createPaymentReviewGateway, type PaymentReviewQueueEntry } from './payment-review-client';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import { createPaymentCorrectionGateway, correctionFields, rememberPaymentCorrectionObservation, type RememberedPaymentObservation } from './payment-correction-client';
import { PaymentCorrectionPanel } from './PaymentCorrectionPanel';
import { PaymentManualReviewPanel } from './PaymentManualReviewPanel';
import { createPaymentManualReviewGateway } from './payment-manual-review-client';
import { createPaymentReviewActionGateway } from './payment-review-action-client';
import { PaymentReviewActionPanel } from './PaymentReviewActionPanel';
import { PaymentDuplicatePanel } from './PaymentDuplicatePanel';
import { PaymentVerificationPanel } from './PaymentVerificationPanel';
import { createPaymentAllocationGateway } from './payment-allocation-client';
import { createPaymentAllocationCorrectionGateway } from './payment-allocation-correction-client';
import { createPaymentVerificationGateway } from './payment-verification-client';
import { PaymentOcrPanel } from './PaymentOcrPanel';
import type { PaymentDraft, PaymentOcrView } from './payment-client';
import { PaymentSupplementReviewerPanel } from './PaymentSupplementPanels';
import { paymentSupplementOwner } from './payment-pdf-supplement-client';
import common from '../administration/access.module.css';
import styles from './invoice.module.css';

type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
const rupiah = (value: string) => orderAmount('IDR', value);
const date = (value: string) => new Intl.DateTimeFormat('id-ID', { dateStyle: 'medium', timeZone: 'Asia/Jakarta' }).format(new Date(value));
function message(error: unknown) {
  if (error instanceof AccessFailure && (error.code.startsWith('evidence_') || error.code === 'payment_response_invalid')) return 'Bukti tidak dapat dibuka dengan aman. Muat pengajuan terbaru atau hubungi penanggung jawab pemeriksaan bukti.';
  if (error instanceof AccessFailure && error.code === 'payment_draft_changed') return 'Pengajuan berubah. Muat kembali pengajuan sebelum melanjutkan pemeriksaan.';
  if (error instanceof IdentityFailure) return 'Masuk kembali dengan akun yang memiliki akses pemeriksaan pembayaran.';
  return accessMessage(error);
}
export function PaymentReviewWorkspace() {
  const [identity] = useState(createIdentityGateway), [access] = useState(createAccessGateway);
  const [account, setAccount] = useState<Account | null>(null), [error, setError] = useState<unknown>(null), [loading, setLoading] = useState(true), [refresh, setRefresh] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => { let active = true; const controller = new AbortController(); void Promise.resolve().then(async () => {
    if (!active) return; setLoading(true); setAccount(null); setError(null);
    try {
      const session = await identity.session(controller.signal), observedAt = performance.now();
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
      const current = await access.current(); if (!active) return;
      if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
      if (!['payment.review.read', 'invoice.read'].every(action => current.allowedActions.includes(action))) throw new AccessFailure(403, 'scope_denied');
      if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
      setAccount({ actorId: current.userId, actions: current.allowedActions, session, observedAt }); setElapsed(performance.now() - observedAt);
    } catch (failure) { if (active) setError(failure); } finally { if (active) setLoading(false); }
  }); return () => { active = false; controller.abort(); }; }, [identity, access, refresh]);
  useEffect(() => { const timer = window.setInterval(() => { if (account) setElapsed(performance.now() - account.observedAt); }, 1000);
    const restored = (event: PageTransitionEvent) => { if (event.persisted) { setAccount(null); setRefresh(value => value + 1); } };
    window.addEventListener('pageshow', restored); return () => { window.clearInterval(timer); window.removeEventListener('pageshow', restored); }; }, [account]);
  const denied = useCallback((failure: unknown) => { if (failure instanceof AccessFailure && (failure.status === 401 || failure.status === 403 || failure.code === 'actor_changed')) { setAccount(null); setError(failure); } }, []);
  return <main className={common.page}><header className={common.header}><div><Link href="/identity/account" className={common.brand}>Unit Toko</Link><h1>Pemeriksaan pembayaran</h1><p>Bandingkan pengajuan cabang, tagihan, bukti asli dan saran pembacaan.</p></div><Link href="/billing/invoices" className={common.secondary}>Lihat tagihan</Link></header>
    {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    {loading ? <p role="status">Memeriksa akun…</p> : !account || sessionTiming(account.session, elapsed).expired ? <section className={common.state}><h2>Periksa sesi dan akses</h2>
      <Link href="/identity/login?returnPath=%2Fbilling%2Fpayment-reviews">Masuk kembali</Link><button className={common.secondary} onClick={() => setRefresh(value => value + 1)}>Perbarui sesi dan akses</button></section>
      : <ReviewDesk key={account.actorId + ':' + account.observedAt} account={account} fresh={sessionTiming(account.session, elapsed).fresh} onDenied={denied} />}
  </main>;
}
function ReviewDesk({ account, fresh, onDenied }: { account: Account; fresh: boolean; onDenied: (failure: unknown) => void }) {
  const [gateway] = useState(createPaymentReviewGateway), [corrections] = useState(createPaymentCorrectionGateway), [manual] = useState(createPaymentManualReviewGateway), [reviewActions] = useState(createPaymentReviewActionGateway), [verification] = useState(createPaymentVerificationGateway), [allocation] = useState(createPaymentAllocationGateway), [allocationCorrections] = useState(createPaymentAllocationCorrectionGateway), recovery = useOperationRecovery(account.actorId);
  const [recovering, setRecovering] = useState(false), [editingBusy, setEditingBusy] = useState(false);
  const [page, setPage] = useState<{ items: PaymentReviewQueueEntry[]; hasMore: boolean; observedAt: string | null }>({ items: [], hasMore: false, observedAt: null }), [before, setBefore] = useState<string | null>(null), [refresh, setRefresh] = useState(0);
  const [filter, setFilter] = useState('all');
  const [selected, setSelected] = useState<PaymentDraft | null>(null), [ocr, setOcr] = useState<PaymentOcrView | null>(null), [preview, setPreview] = useState<string | null>(null);
  const [rememberedObservations, setRememberedObservations] = useState<RememberedPaymentObservation[]>([]);
  const [busy, setBusy] = useState(false), [offline, setOffline] = useState(false), [error, setError] = useState<unknown>(null);
  const alive = useRef(false), pending = useRef(false), previewUrl = useRef<string | null>(null), epoch = useRef(0), heading = useRef<HTMLHeadingElement>(null), previewHeading = useRef<HTMLHeadingElement>(null), previewTrigger = useRef<HTMLButtonElement>(null);
  const clearPreview = useCallback(() => { epoch.current++; if (previewUrl.current) URL.revokeObjectURL(previewUrl.current); previewUrl.current = null; setPreview(null); }, []);
  const current = useCallback(() => alive.current && navigator.onLine && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  useEffect(() => { alive.current = true; const generation = epoch; const connection = () => { setOffline(!navigator.onLine); if (!navigator.onLine) {clearPreview(); setRememberedObservations([]);} }; connection();
    window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => { alive.current = false; generation.current++; if (previewUrl.current) URL.revokeObjectURL(previewUrl.current); window.removeEventListener('online', connection); window.removeEventListener('offline', connection); };
  }, [clearPreview]);
  useEffect(() => { if (selected) heading.current?.focus(); }, [selected]);
  useEffect(() => { if (preview) previewHeading.current?.focus(); }, [preview]);
  useEffect(() => { let active = true; const controller = new AbortController(); void Promise.resolve().then(async () => {
    if (!active || !current()) return; setBusy(true); setError(null); setPage({ items: [], hasMore: false, observedAt: null });
    try { const result = await gateway.queue(account.actorId, before, controller.signal); if (active && current()) setPage(result); }
    catch (failure) { if (active && current()) { setError(failure); onDenied(failure); } } finally { if (active) setBusy(false); }
  }); return () => { active = false; controller.abort(); }; }, [gateway, account.actorId, before, refresh, current, onDenied]);
  async function perform(work: () => Promise<void>) {
    if (!current() || busy || editingBusy || recovering || pending.current) return; pending.current = true; setBusy(true); setError(null);
    try { await work(); } catch (failure) { if (alive.current) { clearPreview(); setOcr(null); setSelected(null); setRememberedObservations([]); setError(failure); onDenied(failure); } }
    finally { pending.current = false; if (alive.current) setBusy(false); }
  }
  const locked = busy || offline || recovering || editingBusy;
  function refreshPage(next: string | null) { clearPreview(); setSelected(null); setOcr(null); setRememberedObservations([]); setBefore(next); setRefresh(value => value + 1); }
  async function open(id: string) { clearPreview(); setSelected(null); setOcr(null); setRememberedObservations([]); const result = await gateway.read(account.actorId, id); if (current()) setSelected(result); }
  async function showOriginal() {
    if (!selected) return; clearPreview(); const version = epoch.current;
    const blob = await gateway.original(account.actorId, selected, () => current() && epoch.current === version);
    if (!current() || epoch.current !== version) return; const url = URL.createObjectURL(blob); previewUrl.current = url; setPreview(url);
  }
  const visible = page.items.filter(row => {
    if (filter === 'all') return true;
    if (filter === 'attention') return row.discrepancies.length > 0 || ['retry', 'dead'].includes(row.ocrState);
    return filter === 'closed' ? ['rejected', 'replacement_requested'].includes(row.state) : row.state === filter;
  });
  return <><AdministrationNavigation current="payment-reviews" actions={account.actions} locked={busy || editingBusy || recovering} />
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy || offline || editingBusy} onBusyChange={setRecovering}
      recoveryAdapters={{ 'billing.payment.review.correct': { lookup: async entry => { const result = await corrections.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return result; } },
        'billing.payment.review.manual': { lookup: async entry => { const result = await manual.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return result; } },
        'billing.payment.review.manage': { lookup: async entry => { const result = await reviewActions.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return result; } },
        'billing.payment.allocate': { lookup: async entry => { const result = await allocation.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return result; } },
        'billing.payment.allocation.correct': { lookup: async entry => { const result = await allocationCorrections.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return result; } },
        'billing.payment.verify': { lookup: async entry => { const result = await verification.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return result; } } }}
      onFailure={failure => { setError(failure); if (!(failure instanceof AccessFailure && failure.code === 'step_up_required')) onDenied(failure); }}
      onResolved={() => { recovery.refresh(); refreshPage(null); }} />
    {account.actions.includes('payment.review.correct') && (!fresh || error instanceof AccessFailure && error.code === 'step_up_required') && <p className={common.alert}>Verifikasi terbaru diperlukan untuk menyimpan atau memulihkan koreksi. <Link href="/identity/account?operation=payment.review.correct&returnPath=%2Fbilling%2Fpayment-reviews">Verifikasi dua langkah</Link></p>}
    {account.actions.includes('payment.review.manual') && (!fresh || error instanceof AccessFailure && error.code === 'step_up_required') && <p className={common.alert}>Verifikasi terbaru diperlukan untuk menyimpan atau memulihkan pengecualian OCR. <Link href="/identity/account?operation=payment.review.manual&returnPath=%2Fbilling%2Fpayment-reviews">Verifikasi dua langkah untuk pengecualian</Link></p>}
    {account.actions.includes('payment.verify') && (!fresh || error instanceof AccessFailure && error.code === 'step_up_required') && <p className={common.alert}>Verifikasi terbaru diperlukan untuk menyimpan atau memulihkan keputusan pembayaran. <Link href="/identity/account?operation=payment.verify&returnPath=%2Fbilling%2Fpayment-reviews">Verifikasi dua langkah untuk keputusan pembayaran</Link></p>}
    {recovery.error !== null && <p role="alert">{accessMessage(recovery.error)}</p>}
    {offline && <p role="status" className={common.alert}>Koneksi terputus. Sambungkan kembali lalu muat pengajuan terbaru.</p>}
    {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    <section className={common.accountSection} aria-label="Pengajuan yang menunggu pemeriksaan"><h2>Pengajuan masuk</h2><p>Hanya pengajuan yang sudah dikirim dan berada dalam cakupan akses Anda.</p>
      <button className={common.secondary} disabled={locked} onClick={() => refreshPage(null)}>Muat pengajuan terbaru</button>
      <label htmlFor="payment-queue-filter">Tampilan halaman ini</label><select id="payment-queue-filter" value={filter} disabled={locked} onChange={event => setFilter(event.target.value)}><option value="all">Semua pengajuan</option><option value="pending">Belum ditugaskan</option><option value="assigned">Dalam pemeriksaan</option><option value="attention">Perlu perhatian OCR</option><option value="closed">Tindak lanjut ditutup</option></select>
      <p>Ringkasan dan usia mengikuti waktu server saat dimuat. Filter berlaku pada halaman ini; hasil OCR tidak menetapkan dana masuk.</p>
      {busy && <p role="status">Memuat data…</p>}
      {!busy && page.items.length === 0 && <p>Belum ada pengajuan yang dapat Anda periksa.</p>}
      {!busy && page.items.length > 0 && visible.length === 0 && <p>Tidak ada pengajuan untuk tampilan ini. Periksa halaman lain atau ubah tampilan.</p>}
      <ul className={styles.candidates}>{visible.map(({ declaration: item, ...queue }) => <li key={item.id}><strong>{item.input.payerName}</strong><p>{item.invoices[0].buyerName} · {item.invoices[0].sellerName}</p>
        <p>{rupiah(item.input.declaredAmount)} · Diajukan {date(item.submission!.submittedAt)}</p>
        <p>{{ pending: 'Belum ditugaskan', assigned: 'Dalam pemeriksaan', rejected: 'Pengajuan ditolak', replacement_requested: 'Bukti pengganti diminta' }[queue.state]}{queue.assigneeId && ` · Penanggung jawab: akun ${queue.assigneeId}`}</p>
        <p>Usia pengajuan: {Math.floor((Date.parse(page.observedAt!) - Date.parse(item.submission!.submittedAt)) / 60000)} menit menurut waktu server.</p>
        {queue.notification.state !== 'not_applicable' && <p>Notifikasi aplikasi: {{ pending: 'Menunggu layanan', running: 'Sedang diproses', retry: 'Menunggu pengulangan layanan', failed: 'Gagal; perlu tindak lanjut petugas', available: 'Tersedia pada akun pembeli' }[queue.notification.state]}. Ketersediaan tidak membuktikan pesan telah dibaca atau email terkirim.</p>}
        <p>OCR: {{ queued: 'Menunggu pembacaan', running: 'Sedang dibaca', retry: 'Pembacaan perlu diulang oleh layanan', dead: 'Pembacaan perlu tindak lanjut petugas', succeeded: 'Hasil pembacaan tersedia' }[queue.ocrState]}</p>
        {queue.discrepancies.length > 0 && <ul aria-label="Perhatian pada bukti">{queue.discrepancies.map(flag => <li key={flag}>{{ amount_unknown: 'Nominal bukti belum diketahui', amount_differs: 'Nominal pada bukti berbeda dari pengajuan', date_unknown: 'Tanggal pada bukti belum diketahui', date_differs: 'Tanggal pada bukti berbeda dari pengajuan', currency_unknown: 'Mata uang belum diketahui', ocr_quality: 'Keterbacaan perlu diperiksa', human_corrected: 'Ada koreksi manusia yang tersimpan' }[flag]}</li>)}</ul>}
        <p>Buka pengajuan untuk melihat hasil verifikasi terbaru.</p>
        <button className={common.secondary} disabled={locked} onClick={() => void perform(() => open(item.id))}>Periksa pengajuan {item.id}</button></li>)}</ul>
      {page.hasMore && <button className={common.secondary} disabled={locked} onClick={() => refreshPage(page.items.at(-1)!.declaration.id)}>Pengajuan lebih lama</button>}
    </section>
    {selected && <section className={common.accountSection} aria-label="Rincian pengajuan pembayaran"><h2 ref={heading} tabIndex={-1}>Pengajuan {selected.id}</h2><p>Pengajuan telah dikirim untuk pemeriksaan.</p>
      <p>Data berikut berasal dari pengajuan pembeli. Pembacaan dan pemeriksaan gambar belum membuktikan dana masuk.</p>
      <dl className={styles.facts}><div><dt>Jumlah yang dinyatakan</dt><dd>{rupiah(selected.input.declaredAmount)}</dd></div><div><dt>Nama pembayar</dt><dd>{selected.input.payerName}</dd></div>
        <div><dt>Tanggal transfer yang dinyatakan</dt><dd>{date(selected.input.transferDate + 'T00:00:00+07:00')}</dd></div><div><dt>Referensi yang dinyatakan</dt><dd>{selected.input.bankReference ?? 'Belum diisi'}</dd></div>
        <div><dt>Catatan pembeli</dt><dd>{selected.input.note ?? 'Tidak ada catatan'}</dd></div><div><dt>Versi pengajuan</dt><dd>{selected.revision} · Pengiriman {selected.submission!.id}</dd></div></dl>
      <h3>Tagihan yang disebut pembeli</h3><p>Total invoice berikut adalah nilai terbit, bukan saldo piutang setelah pembayaran atau kredit.</p>
      <ul className={styles.lines}>{selected.invoices.map(invoice => <li key={invoice.invoiceId}><strong>{invoice.number}</strong><p>{invoice.buyerName} · {invoice.sellerName}</p><p>Total terbit {rupiah(invoice.total)}</p></li>)}</ul>
      <button ref={previewTrigger} className={common.secondary} disabled={locked} onClick={() => void perform(showOriginal)}>Lihat bukti asli</button>
      {preview && <section className={styles.evidencePreview} aria-label="Bukti asli untuk pemeriksaan"><h3 ref={previewHeading} tabIndex={-1}>Bukti asli yang diajukan</h3><button className={common.secondary} onClick={() => { clearPreview(); previewTrigger.current?.focus(); }}>Tutup bukti</button>
        <div className={styles.evidenceImage}><Image src={preview} alt="Bukti pembayaran asli yang diajukan pembeli" fill unoptimized style={{ objectFit: 'contain' }} /></div></section>}
      <PaymentReviewActionPanel key={'workflow:' + selected.id + ':' + selected.revision} actorId={account.actorId} owner={selected} allowed={account.actions.includes('payment.review.manage')}
        fresh={fresh} locked={locked || recovery.locked} current={current} journal={recovery.journal} onSettled={recovery.refresh} onDenied={onDenied} onBusyChange={setEditingBusy} />
      <PaymentDuplicatePanel key={'duplicates:' + selected.id + ':' + selected.revision} actorId={account.actorId} owner={selected}
        locked={locked} current={current} onOpen={id => void perform(() => open(id))} onDenied={onDenied} onBusyChange={setEditingBusy} />
      <PaymentOcrPanel view={ocr} locked={locked} onRefresh={() => void perform(async () => { const value = await gateway.ocr(account.actorId, selected); if (current()) setOcr(value); })} />
      {selected.state === 'submitted' && selected.submission && <PaymentSupplementReviewerPanel key={'supplements:' + selected.id + ':' + selected.revision + ':' + selected.submission.id}
        owner={paymentSupplementOwner(account.actorId, selected)} locked={locked || recovery.locked} current={current} onDenied={onDenied}
        onSelectObservation={account.actions.includes('payment.review.correct') && fresh && ocr?.result && !locked && !recovery.locked ? selection => {
          if (!current() || locked || recovery.locked) return;
          try {const remembered = rememberPaymentCorrectionObservation(account.actorId, selected, selection);
            setRememberedObservations(previous => [...previous.filter(item => item.reference.field !== remembered.reference.field), remembered]
              .sort((first, second) => correctionFields.indexOf(first.reference.field) - correctionFields.indexOf(second.reference.field)));
          } catch (failure) {setError(failure); onDenied(failure);}
        } : undefined}/>}
      <PaymentCorrectionPanel key={selected.id + ':' + selected.revision + ':' + (ocr?.result?.id ?? 'pending')} actorId={account.actorId} owner={selected} ocr={ocr}
        allowed={account.actions.includes('payment.review.correct')} fresh={fresh}
        locked={locked || recovery.locked} current={current} journal={recovery.journal} onSettled={recovery.refresh} onDenied={onDenied} onBusyChange={setEditingBusy}
        observations={rememberedObservations} onRemoveObservation={field => setRememberedObservations(previous => previous.filter(item => item.reference.field !== field))}
        onObservationsSaved={() => setRememberedObservations([])} />
      <PaymentVerificationPanel key={'verify:' + selected.id + ':' + selected.revision} actorId={account.actorId} owner={selected}
        manualReview={<PaymentManualReviewPanel actorId={account.actorId} owner={selected} ocr={ocr} allowed={account.actions.includes('payment.review.manual')} fresh={fresh} locked={locked || recovery.locked}
          current={current} journal={recovery.journal} onSettled={recovery.refresh} onDenied={onDenied} onBusyChange={setEditingBusy} />}
        allowed={account.actions.includes('payment.verify')} allocationAllowed={account.actions.includes('payment.allocate')} allocationCorrectionAllowed={account.actions.includes('payment.allocate') && account.actions.includes('journal.reverse')} fresh={fresh} locked={locked || recovery.locked} current={current}
        journal={recovery.journal} onSettled={recovery.refresh} onDenied={onDenied} onBusyChange={setEditingBusy} />
    </section>}
  </>;
}
