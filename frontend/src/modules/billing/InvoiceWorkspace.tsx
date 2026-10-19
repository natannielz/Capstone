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
import { createInvoiceGateway, prepareInvoice, type Invoice, type InvoiceCandidate, type InvoicePreview } from './invoice-client';
import common from '../administration/access.module.css';
import styles from './invoice.module.css';
import { longDateFormatter } from '@/lib/format';

type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
const number = (item: Invoice) => `${item.numberEntity}/${item.numberChannel}/${item.numberYear}/${item.number}`;
const rupiah = (value: string) => orderAmount('IDR', value);
const date = (value: string) => longDateFormatter.format(new Date(value));
function message(error: unknown) {
  if (error instanceof AccessFailure && ['invoice_policy_unavailable', 'invoice_policy_expired'].includes(error.code)) return 'Aturan tagihan belum tersedia atau belum disetujui. Penanggung jawab perlu melengkapi aturan pajak, jatuh tempo, pengelompokan, penomoran dan pembukuan.';
  if (error instanceof AccessFailure && ['invoice_review_changed', 'receipt_already_invoiced', 'invoice_grouping_changed', 'invoice_commercial_allocation_exceeded'].includes(error.code)) return 'Sumber atau aturan tagihan berubah. Muat data terbaru, lalu tinjau pilihan penerimaan kembali.';
  if (error instanceof AccessFailure && error.code.startsWith('accounting_')) return 'Pembukuan belum siap untuk tanggal terbit ini. Periksa periode dan pengaturan akun bersama penanggung jawab keuangan.';
  if (error instanceof IdentityFailure) return 'Sesi perlu diperiksa kembali. Masuk dengan akun yang memiliki akses tagihan.';
  return accessMessage(error);
}
export function InvoiceWorkspace() {
  const [identity] = useState(createIdentityGateway), [access] = useState(createAccessGateway);
  const [account, setAccount] = useState<Account | null>(null), [loading, setLoading] = useState(true), [error, setError] = useState<unknown>(null), [revision, setRevision] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => { let active = true; const controller = new AbortController(); void Promise.resolve().then(async () => {
    if (!active) return; setLoading(true); setAccount(null); setError(null);
    try {
      const session = await identity.session(controller.signal), observedAt = performance.now(); if (!active) return;
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
      const current = await access.current(); if (!active) return;
      if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
      if (!current.allowedActions.some(action => ['invoice.read', 'invoice.issue'].includes(action))) throw new AccessFailure(403, 'scope_denied');
      if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
      setAccount({ actorId: current.userId, actions: current.allowedActions, session, observedAt }); setElapsed(performance.now() - observedAt);
    } catch (failure) { if (active) setError(failure); } finally { if (active) setLoading(false); }
  }); return () => { active = false; controller.abort(); }; }, [identity, access, revision]);
  useEffect(() => { const timer = window.setInterval(() => { if (account) setElapsed(performance.now() - account.observedAt); }, 1000);
    const restored = (event: PageTransitionEvent) => { if (event.persisted) { setAccount(null); setRevision(value => value + 1); } };
    window.addEventListener('pageshow', restored); return () => { window.clearInterval(timer); window.removeEventListener('pageshow', restored); }; }, [account]);
  const denied = useCallback((failure: unknown) => { if (failure instanceof AccessFailure && (failure.status === 401 || failure.status === 403 || failure.code === 'actor_changed')) { setAccount(null); setError(failure); } }, []);
  return <main className={common.page}><header className={common.header}><div><Link href="/identity/account" className={common.brand}>Unit Toko</Link><h1>Tagihan</h1><p>Terbitkan dan periksa invoice berdasarkan barang yang telah diterima.</p></div><Link href="/identity/account" className={common.secondary}>Akun saya</Link></header>
    {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    {loading ? <p role="status">Memeriksa akun…</p> : !account || sessionTiming(account.session, elapsed).expired ? <section className={common.state}><h2>Periksa sesi dan akses</h2>
      <Link href="/identity/login?returnPath=%2Fbilling%2Finvoices">Masuk kembali</Link><button className={common.secondary} onClick={() => setRevision(value => value + 1)}>Perbarui sesi dan akses</button></section>
      : <InvoiceDesk key={account.actorId + ':' + account.observedAt} account={account} onDenied={denied} />}
  </main>;
}
function InvoiceDetail({ value }: { value: InvoicePreview | ReturnType<typeof prepareInvoice>['detail'] }) {
  const origins = { issue_date: 'tanggal terbit', earliest_receipt: 'penerimaan paling awal', latest_receipt: 'penerimaan paling akhir' };
  return <><dl className={styles.facts}><div><dt>Ditagihkan kepada</dt><dd>{value.buyerName}</dd></div><div><dt>Penjual</dt><dd>{value.sellerName}</dd></div>
    <div><dt>Tanggal terbit</dt><dd>{date(value.issueDate)}</dd></div><div><dt>Jatuh tempo</dt><dd>{date(value.dueDate)}</dd></div>
    <div><dt>Ketentuan pembayaran</dt><dd>{value.termDays} {value.dayRule === 'working_days' ? 'hari kerja' : 'hari kalender'} sejak {origins[value.termOrigin]}</dd></div>
    <div><dt>Periode penerimaan</dt><dd>{date(value.periodFrom)}–{date(value.periodTo)}</dd></div></dl>
    <ul className={styles.lines}>{value.lines.map(line => <li key={line.receiptId + ':' + line.skuId}><strong>{line.productName}</strong><p>{line.skuCode} · {line.packageLabel} · {line.baseUnitsPerPackage} {line.baseUnitCode} per kemasan</p>
      <p>Diterima: {line.acceptedBaseUnits} {line.baseUnitCode} · Bukti {line.receiptId} · Pesanan {line.orderId}</p><p>Bersih {rupiah(line.net)} · Pajak {rupiah(line.tax)} · Biaya {rupiah(line.charges)}</p></li>)}</ul>
    <div className={styles.total}><span>Total invoice</span><strong>{rupiah(value.total)}</strong></div><p>{value.taxDocumentProcess}</p></>;
}
function InvoiceDesk({ account, onDenied }: { account: Account; onDenied: (error: unknown) => void }) {
  const [gateway] = useState(createInvoiceGateway), [ledger] = useState(() => new MutationLedger()), recovery = useOperationRecovery(account.actorId);
  const canRead = account.actions.includes('invoice.read'), canIssue = account.actions.includes('invoice.issue');
  const [candidates, setCandidates] = useState<{ items: InvoiceCandidate[]; nextShipmentId: string | null }>({ items: [], nextShipmentId: null });
  const [page, setPage] = useState<{ items: Invoice[]; hasMore: boolean }>({ items: [], hasMore: false }), [before, setBefore] = useState<string | null>(null), [beforeShipment, setBeforeShipment] = useState<string | null>(null);
  const [selection, setSelection] = useState<string[]>([]), [reason, setReason] = useState(''), [review, setReview] = useState<ReturnType<typeof prepareInvoice> | null>(null), [selected, setSelected] = useState<Invoice | null>(null);
  const [revision, setRevision] = useState(0), [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [offline, setOffline] = useState(false), [error, setError] = useState<unknown>(null), [saved, setSaved] = useState(false);
  const alive = useRef(false), pending = useRef(false), heading = useRef<HTMLHeadingElement>(null), recovered = useRef<Invoice | null>(null);
  const current = useCallback(() => alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const locked = busy || recovering || offline || recovery.locked;
  useEffect(() => { alive.current = true; const connection = () => setOffline(!navigator.onLine); connection(); window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => { alive.current = false; window.removeEventListener('online', connection); window.removeEventListener('offline', connection); }; }, []);
  useEffect(() => { let active = true; const controller = new AbortController(); void Promise.resolve().then(async () => {
    if (!active) return; setBusy(true); setError(null); setReview(null); setSelection([]); setCandidates({ items: [], nextShipmentId: null }); setPage({ items: [], hasMore: false });
    try {
      // These reads fence the same actor and order histories. Keep them sequential so
      // refreshing after recovery does not compete with itself in MongoDB.
      const ready = canIssue ? await gateway.candidates(account.actorId, beforeShipment, controller.signal) : null;
      if (!active || !current()) return;
      const invoices = canRead ? await gateway.list(account.actorId, before, controller.signal) : null;
      if (!active || !current()) return; if (ready) setCandidates(ready); if (invoices) setPage(invoices);
    } catch (failure) { if (active && current()) { setError(failure); onDenied(failure); } } finally { if (active) setBusy(false); }
  }); return () => { active = false; controller.abort(); }; }, [gateway, account.actorId, before, beforeShipment, revision, canRead, canIssue, current, onDenied]);
  useEffect(() => { if (review || selected) heading.current?.focus(); }, [review, selected]);
  async function perform(work: () => Promise<void>) {
    if (!current() || busy || recovering || offline || pending.current) return; pending.current = true; setBusy(true); setError(null);
    try { await work(); } catch (failure) { if (current()) { setReview(null); setError(failure); onDenied(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); recovery.refresh(); } }
  }
  async function preview() {
    if (locked || !canIssue) return;
    await perform(async () => { const chosen = candidates.items.filter(item => selection.includes(item.receiptId) && !item.invoiced).map(item => ({ shipmentId: item.shipmentId, receiptId: item.receiptId }));
      const value = await gateway.preview(account.actorId, chosen); if (current()) { setSelected(null); setReview(prepareInvoice(account.actorId, chosen, value, reason.trim())); setSaved(false); } });
  }
  async function issue() {
    if (locked || !canIssue || !review) return;
    await perform(async () => {
      if (recovery.journal.list(account.actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
      const result = await submitTrackedRequest(ledger, recovery.journal, account.actorId, 'billing.invoice.issue', 'billing.invoice.issue', review, key => gateway.issue(review, key, current));
      if (current()) { setSelected(result); setReview(null); setSaved(true); setReason(''); setBefore(null); setBeforeShipment(null); setRevision(value => value + 1); }
    });
  }
  return <><AdministrationNavigation current="invoices" actions={account.actions} locked={busy || recovering} />
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy || offline} onBusyChange={setRecovering}
      recoveryAdapters={{ 'billing.invoice': { lookup: async entry => { const result = await gateway.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); recovered.current = result?.result ?? null; return result; } } }}
      onFailure={failure => { setError(failure); onDenied(failure); }} onResolved={() => { recovery.refresh(); if (current()) { setSelected(recovered.current); setSaved(recovered.current !== null); setRevision(value => value + 1); } recovered.current = null; }} />
    {recovery.error !== null && <p role="alert">{accessMessage(recovery.error)}</p>}{error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    {offline && <p role="status">Koneksi terputus. Data yang tampil adalah hasil terakhir; penerbitan belum dapat dikirim.</p>}{busy && <p role="status">Memeriksa tagihan…</p>}
    {saved && <p role="status">Invoice tersimpan. Nomor, nilai dan bukti penerimaannya telah dibukukan.</p>}
    {review && <section className={common.accountSection} aria-label="Tinjauan invoice"><h2 ref={heading} tabIndex={-1}>Tinjau penerbitan invoice</h2><InvoiceDetail value={review.detail} /><p>Alasan: {review.body.reason}</p>
      <p>Konfirmasi akan menerbitkan invoice dan mencatat piutang. Jumlah mengikuti barang yang diterima; nomor dan isi dokumen tidak dapat diedit setelah terbit.</p>
      <button className={common.primary} disabled={locked} onClick={() => void issue()}>Terbitkan invoice</button><button className={common.secondary} disabled={busy || recovering} onClick={() => setReview(null)}>Kembali ke pilihan penerimaan</button></section>}
    {selected && <section className={common.accountSection} aria-label="Invoice terbit"><h2 ref={heading} tabIndex={-1}>Invoice {number(selected)}</h2><InvoiceDetail value={selected.detail} />
      {account.actions.includes('case.request') && <Link href={'/fulfillment/cases?invoice=' + selected.id} className={common.secondary} aria-disabled={locked} onClick={event => { if (locked) event.preventDefault(); }}>Laporkan masalah barang</Link>}
      {canRead && <button className={common.secondary} disabled={busy || recovering || offline} onClick={() => void perform(async () => { const blob = await gateway.download(account.actorId, selected); if (!current()) return;
        const url = URL.createObjectURL(blob), link = document.createElement('a'); link.href = url; link.download = `invoice-${selected.id}.html`; document.body.appendChild(link); link.click(); link.remove(); window.setTimeout(() => URL.revokeObjectURL(url), 1000); })}>Unduh invoice</button>}
      <button className={common.secondary} disabled={busy || recovering} onClick={() => setSelected(null)}>Tutup invoice</button></section>}
    {canIssue && !review && <section className={common.accountSection} aria-label="Penerimaan untuk invoice"><h2>Penerimaan untuk invoice</h2><p>Pilih hingga 20 bukti penerimaan yang dapat digabungkan. Aturan pengelompokan, pajak dan jatuh tempo diperiksa saat peninjauan.</p>
      {!busy && candidates.items.length === 0 && <p>Belum ada penerimaan pada halaman ini.</p>}
      <form onSubmit={event => { event.preventDefault(); void preview(); }}><ul className={styles.candidates}>{candidates.items.map(item => <li key={item.receiptId}>
        <label><input type="checkbox" disabled={locked || item.invoiced || selection.length >= 20 && !selection.includes(item.receiptId)} checked={selection.includes(item.receiptId)} onChange={event => setSelection(values => event.target.checked ? [...values, item.receiptId] : values.filter(value => value !== item.receiptId))} />
          <span><strong>Bukti penerimaan {item.receiptId}</strong><span>{item.buyerName} · Kiriman {item.shipmentId}</span><span>{date(item.receivedAt)} · {item.acceptedLines} jenis barang · {item.invoiced ? 'Sudah ditagihkan' : 'Belum ditagihkan'}</span></span></label></li>)}</ul>
        <label>Alasan penerbitan<textarea required maxLength={1000} value={reason} disabled={locked} onChange={event => setReason(event.target.value)} /></label><button className={common.primary} disabled={locked || selection.length === 0}>Tinjau invoice</button></form>
      {candidates.nextShipmentId && <button className={common.secondary} disabled={busy || recovering || offline} onClick={() => setBeforeShipment(candidates.nextShipmentId)}>Penerimaan lebih lama</button>}</section>}
    {canRead && <section className={common.accountSection} aria-label="Daftar invoice"><h2>Invoice terbit</h2>{!busy && page.items.length === 0 && <p>Belum ada invoice dalam akses Anda pada halaman ini.</p>}
      <ul className={styles.candidates}>{page.items.map(item => <li key={item.id}><strong>{number(item)}</strong><p>{item.detail.buyerName} · {rupiah(item.detail.total)} · Jatuh tempo {date(item.detail.dueDate)}</p>
        <button className={common.secondary} disabled={busy || recovering || offline || !!review} onClick={() => void perform(async () => { const detail = await gateway.read(account.actorId, item.id); if (current()) setSelected(detail); })}>Buka invoice {number(item)}</button></li>)}</ul>
      {page.hasMore && <button className={common.secondary} disabled={busy || recovering || offline || !!review} onClick={() => setBefore(page.items.at(-1)!.id)}>Invoice lebih lama</button>}</section>}
    <button className={common.secondary} disabled={busy || recovering || offline} onClick={() => { setBefore(null); setBeforeShipment(null); setRevision(value => value + 1); }}>Muat tagihan terbaru</button>
  </>;
}
