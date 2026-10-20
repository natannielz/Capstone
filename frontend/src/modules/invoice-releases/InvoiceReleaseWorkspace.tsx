'use client';

import Link from 'next/link';
import {useCallback, useEffect, useRef, useState} from 'react';
import {RequestNotSentError} from '../../api/client';
import {createIdentityGateway} from '../identity/identity-client';
import {IdentityFailure, sessionTiming, type IdentitySession} from '../identity/identity-workflow';
import {createAccessGateway} from '../administration/access-client';
import {AccessFailure} from '../administration/workflow';
import {orderAmount} from '../ordering/order-quote-client';
import {createInvoiceReleaseGateway, invoiceReleaseWib, parseInvoiceReleaseInput, prepareInvoiceRelease,
  type InvoiceReleaseCommand, type InvoiceReleaseEligibility, type InvoiceReleaseFacts, type InvoiceReleaseInput, type InvoiceReleaseView} from './invoice-release-client';
import {InvoiceReleaseJournal, invoiceReleaseJournalPrefix, type InvoiceReleaseReference} from './invoice-release-journal';
import common from '../administration/access.module.css';
import styles from '../billing/invoice.module.css';
import {formatDate, formatDateTime} from '@/lib/format';
import { SessionAccessState } from "@/components/workspace-kit/session-access-state";

type Account = {actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number};
const currency = (value: string) => orderAmount('IDR', value);
const date = (value: string) => formatDate(value);
const actionText = {propose: 'Usulan', approve: 'Disetujui', reject: 'Ditolak'};
function message(error: unknown) {
  if (error instanceof RequestNotSentError) return 'Perubahan belum dikirim. Lengkapi isian yang ditinjau dan muat sumber terbaru.';
  if (error instanceof AccessFailure && [401, 403].includes(error.status)) return 'Akses atau verifikasi akun perlu diperbarui. Hasil permintaan asli tetap dapat diperiksa setelah akses yang sesuai tersedia.';
  if (error instanceof AccessFailure && error.status === 409) return 'Sumber, persetujuan, atau pembukuan berubah. Periksa hasil permintaan asli dahulu, lalu muat sumber terbaru.';
  return 'Hasil belum dapat dipastikan. Periksa hasil permintaan asli sebelum membuat perubahan lain.';
}
export function InvoiceReleaseWorkspace() {
  const [identity] = useState(createIdentityGateway), [access] = useState(createAccessGateway);
  const [account, setAccount] = useState<Account | null>(null), [busy, setBusy] = useState(true), [failure, setFailure] = useState<unknown>(null);
  const [refresh, setRefresh] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => {
    let live = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!live) return; setBusy(true); setAccount(null); setFailure(null);
      try {
        const session = await identity.session(controller.signal), observedAt = performance.now(); if (!live) return;
        if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
        const current = await access.current(); if (!live) return;
        if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
        if (!['invoice.read', 'invoice.release.read'].every(permission => current.allowedActions.includes(permission))) throw new AccessFailure(403, 'scope_denied');
        if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
        setAccount({actorId: current.userId, actions: current.allowedActions, session, observedAt}); setElapsed(performance.now() - observedAt);
      } catch (error) {if (live) setFailure(error);} finally {if (live) setBusy(false);}
    }); return () => {live = false; controller.abort();};
  }, [identity, access, refresh]);
  useEffect(() => {
    const timer = window.setInterval(() => {if (account) setElapsed(performance.now() - account.observedAt);}, 1000);
    const restored = (event: PageTransitionEvent) => {if (event.persisted) {setAccount(null); setRefresh(value => value + 1);}};
    window.addEventListener('pageshow', restored); return () => {window.clearInterval(timer); window.removeEventListener('pageshow', restored);};
  }, [account]);
  const denied = useCallback((error: unknown) => {if (error instanceof AccessFailure && ([401, 403].includes(error.status) || error.code === 'actor_changed')) {setAccount(null); setFailure(error);}}, []);
  return <main className={common.page}><header className={common.header}><div><Link href="/identity/account" className={common.brand}>Unit Toko</Link>
    <h1>Persetujuan penerbitan invoice</h1><p>Periksa penerimaan, ketentuan tagihan, dan pembukuan sebelum memberikan persetujuan penerbitan.</p></div>
    <Link href="/identity/account" className={common.secondary}>Akun saya</Link></header>
    {failure !== null && account !== null && <p role="alert" className={common.alert}>{message(failure)}</p>}
    {busy ? <p role="status">Memeriksa akun…</p> : !account || sessionTiming(account.session, elapsed).expired ? <SessionAccessState error={failure} returnPath="/billing/invoice-releases" onRetry={() => setRefresh(value => value + 1)} />
      : <ReleaseDesk key={account.actorId + ':' + account.observedAt} account={account} onDenied={denied} />}</main>;
}
function Facts({value}: {value: InvoiceReleaseFacts}) {
  const origins = {issue_date: 'tanggal terbit', earliest_receipt: 'penerimaan pertama', latest_receipt: 'penerimaan terakhir'};
  const days = ['Minggu', 'Senin', 'Selasa', 'Rabu', 'Kamis', 'Jumat', 'Sabtu'];
  return <><dl className={styles.facts}><div><dt>Pembeli</dt><dd>{value.buyerName}</dd></div><div><dt>Penjual</dt><dd>{value.sellerName}</dd></div>
    <div><dt>Jatuh tempo</dt><dd>{value.termDays} {value.dayRule === 'working_days' ? 'hari kerja' : 'hari kalender'} sejak {origins[value.termOrigin]}</dd></div>
    <div><dt>Pengelompokan</dt><dd>{value.grouping === 'receipt' ? 'Per penerimaan' : value.grouping === 'buying_branch_period_channel' ? 'Cabang, periode, dan kanal' : 'Organisasi pembeli, periode, dan kanal'}</dd></div>
    <div><dt>Pembulatan</dt><dd>{value.rounding === 'half_even' ? 'Nilai tengah ke angka genap' : 'Nilai tengah dibulatkan naik'}</dd></div>
    <div><dt>Periode pembukuan</dt><dd>{value.accountingPeriod ?? 'Diperiksa pada pratinjau'}</dd></div></dl>
    <p>Hari kerja: {value.workingDays.map(day => days[day]).join(', ') || 'Tidak dicantumkan'}.</p><p>Hari libur: {value.holidays.map(date).join(', ') || 'Tidak dicantumkan'}.</p>
    <p>Dokumen pajak: {value.taxDocumentProcess}</p><p>Penomoran: {value.numberingProcedure}</p><p>Penerbitan ulang: {value.reissueProcedure}</p>
    <ul className={styles.lines}>{value.lines.map(line => <li className={styles.line} key={[line.receiptId, line.skuId, line.lineRevision].join(':')}><strong>{line.productName} — {line.packageLabel}</strong>
      <p>{line.skuCode} · baris {line.lineRevision} · bukti penerimaan {line.receiptId} · pesanan {line.orderId}</p><p>Diterima {line.acceptedBaseUnits} satuan dasar · harga kemasan {currency(line.unitPrice)} · bersih {currency(line.net)}</p>
      <p>Pajak: {line.tax === null ? 'Belum diisi' : currency(line.tax)} · biaya: {line.charges === null ? 'Belum diisi' : currency(line.charges)}</p></li>)}</ul>
    <h3>Batas biaya pesanan</h3><ul>{value.budgets.map(item => <li key={item.orderId}>Pesanan {item.orderId}: pajak disepakati {currency(item.approvedTax)}, sudah dibukukan {currency(item.postedTax)}; biaya disepakati {currency(item.approvedCharges)}, sudah dibukukan {currency(item.postedCharges)}.</li>)}</ul>
    {value.accounts.length > 0 && <><h3>Akun pembukuan yang diperiksa</h3><ul>{value.accounts.map(item => <li key={item.role}>{({receivable: 'Piutang', revenue: 'Pendapatan', tax: 'Pajak', charges: 'Biaya'})[item.role]}: {item.mappingKey} → akun {item.accountCode}</li>)}</ul></>}
  </>;
}
function InputFacts({value}: {value: InvoiceReleaseInput}) {
  return <><p>Periode penerimaan {date(value.periodFrom)} sampai {date(value.periodTo)}.</p>
    <p>Berlaku {formatDateTime(value.validFrom)} sampai {formatDateTime(value.validUntil)}.</p>
    <p>Referensi format invoice: {value.templateApprovalReference}</p><p>Referensi penomoran: {value.numberingApprovalReference}</p><p>Referensi kelayakan penerbitan: {value.eligibilityReviewReference}</p></>;
}
const blank = () => ({periodFrom: '', periodTo: '', validFrom: '', validUntil: '', templateApprovalReference: '', numberingApprovalReference: '', eligibilityReviewReference: '', receivable: '', revenue: '', tax: '', charges: ''});
function ReleaseDesk({account, onDenied}: {account: Account; onDenied: (error: unknown) => void}) {
  const [gateway] = useState(createInvoiceReleaseGateway), [journal] = useState(() => new InvoiceReleaseJournal(() => localStorage));
  const [candidates, setCandidates] = useState<Awaited<ReturnType<typeof gateway.candidates>>>({items: [], nextShipmentId: null});
  const [history, setHistory] = useState<Awaited<ReturnType<typeof gateway.list>>>({items: [], nextId: null});
  const [sourceCursor, setSourceCursor] = useState<string | null>(null), [historyCursor, setHistoryCursor] = useState<string | null>(null), [refresh, setRefresh] = useState(0);
  const [chosen, setChosen] = useState<string[]>([]), [eligible, setEligible] = useState<InvoiceReleaseEligibility | null>(null), [record, setRecord] = useState<InvoiceReleaseView | null>(null);
  const [fields, setFields] = useState(blank), [amounts, setAmounts] = useState<Record<string, {tax: string; charges: string}>>({}), [reason, setReason] = useState('');
  const [review, setReview] = useState<InvoiceReleaseCommand | null>(null), [confirmed, setConfirmed] = useState(false), [pending, setPending] = useState<InvoiceReleaseReference[]>([]);
  const [busy, setBusy] = useState(false), [storageFailed, setStorageFailed] = useState(false), [offline, setOffline] = useState(false), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState('');
  const alive = useRef(false), epoch = useRef(0), running = useRef(false), focus = useRef<HTMLElement>(null);
  const current = useCallback(() => alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const canPropose = account.actions.includes('invoice.release.propose'), canApprove = account.actions.includes('invoice.release.approve');
  const locked = busy || offline || storageFailed || pending.length > 0;
  const references = useCallback(() => {try {setPending(journal.list(account.actorId)); setStorageFailed(false);} catch {setStorageFailed(true);}}, [journal, account.actorId]);
  const clear = useCallback(() => {epoch.current++; setReview(null); setConfirmed(false); setRecord(null); setEligible(null); setChosen([]); setFields(blank()); setAmounts({}); setReason('');}, []);
  useEffect(() => {
    alive.current = true; let live = true; const generation = epoch;
    const changed = (event: StorageEvent) => {if (event.key === null || event.key.startsWith(invoiceReleaseJournalPrefix(account.actorId))) {
      clear(); references(); setNotice('Ada perubahan permintaan di tab lain. Periksa hasil asli, lalu muat sumber terbaru.'); setRefresh(value => value + 1);
    }};
    const connection = () => setOffline(!navigator.onLine);
    void Promise.resolve().then(() => {if (live) {references(); connection();}});
    window.addEventListener('storage', changed); window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => {live = false; alive.current = false; generation.current++; window.removeEventListener('storage', changed); window.removeEventListener('online', connection); window.removeEventListener('offline', connection);};
  }, [account.actorId, clear, references]);
  useEffect(() => {if (review || record) focus.current?.focus();}, [review, record]);
  useEffect(() => {
    let live = true; const controller = new AbortController(), generation = ++epoch.current;
    void Promise.resolve().then(async () => {
      if (!live) return; setBusy(true); setError(null);
      try {
        const sources = canPropose ? await gateway.candidates(account.actorId, sourceCursor, controller.signal) : {items: [], nextShipmentId: null};
        const records = await gateway.list(account.actorId, historyCursor, controller.signal);
        if (!live || !current() || generation !== epoch.current) return; setCandidates(sources); setHistory(records);
      } catch (failure) {if (live && current()) {setError(failure); onDenied(failure);}} finally {if (live) setBusy(false);}
    }); return () => {live = false; controller.abort();};
  }, [gateway, account.actorId, canPropose, sourceCursor, historyCursor, refresh, current, onDenied]);
  async function work(task: (valid: () => boolean) => Promise<void>) {
    if (running.current || busy || offline || !current()) return; running.current = true; setBusy(true); setError(null); const generation = ++epoch.current;
    const valid = () => current() && epoch.current === generation;
    try {await task(valid);} catch (failure) {if (current()) {setError(failure); onDenied(failure);}} finally {running.current = false; if (alive.current) {setBusy(false); references();}}
  }
  const selections = () => candidates.items.filter(item => chosen.includes(item.receiptId) && !item.invoiced).map(item => ({shipmentId: item.shipmentId, receiptId: item.receiptId}));
  async function inspect() {
    if (locked || review || !canPropose) return;
    await work(async valid => {const value = await gateway.eligibility(account.actorId, selections()); if (valid()) {setEligible(value); setRecord(null); setFields(blank()); setAmounts({}); setReason('');}});
  }
  async function propose() {
    if (locked || review || !eligible || !canPropose) return;
    await work(async valid => {
      const input = parseInvoiceReleaseInput({receipts: selections(), expectedReleaseRevision: eligible.revision,
        periodFrom: fields.periodFrom, periodTo: fields.periodTo, validFrom: invoiceReleaseWib(fields.validFrom), validUntil: invoiceReleaseWib(fields.validUntil),
        templateApprovalReference: fields.templateApprovalReference, numberingApprovalReference: fields.numberingApprovalReference, eligibilityReviewReference: fields.eligibilityReviewReference,
        mappings: {receivable: fields.receivable, revenue: fields.revenue, tax: fields.tax, charges: fields.charges},
        allocations: eligible.facts.lines.map(line => ({receiptId: line.receiptId, skuId: line.skuId, lineRevision: line.lineRevision,
          tax: amounts[[line.receiptId, line.skuId, line.lineRevision].join(':')]?.tax ?? '', charges: amounts[[line.receiptId, line.skuId, line.lineRevision].join(':')]?.charges ?? ''}))});
      const preview = await gateway.preview(account.actorId, input, eligible.selectionKey); if (valid()) {setReview(prepareInvoiceRelease(account.actorId, preview, 'propose', null, reason)); setConfirmed(false);}
    });
  }
  async function load(recordId: string) {
    if (locked || review) return;
    await work(async valid => {const value = await gateway.read(account.actorId, recordId); if (valid()) {setRecord(value); setEligible(null); setChosen([]); setReason('');}});
  }
  async function decide(action: 'approve' | 'reject') {
    if (locked || review || !record || !canApprove) return;
    await work(async valid => {const preview = await gateway.decisionPreview(account.actorId, record, action); if (valid()) {setReview(prepareInvoiceRelease(account.actorId, preview, action, record.result.proposalId, reason)); setConfirmed(false);}});
  }
  async function commit() {
    if (locked || !review || !confirmed) return; const checked = review;
    await work(async valid => {
      if (journal.list(account.actorId).length) throw new RequestNotSentError(409, 'resolve_original_operation');
      const reference: InvoiceReleaseReference = {actorId: account.actorId, action: checked.action, key: 'invoice-release.' + crypto.randomUUID(),
        revision: checked.revision, proposalId: checked.proposalId, selectionKey: checked.selectionKey};
      journal.remember(reference); references();
      try {await gateway.change(checked, reference.key, valid); journal.confirmed(reference); if (valid()) {clear(); setNotice('Perubahan persetujuan invoice tersimpan. Muat riwayat untuk memeriksanya.'); setRefresh(value => value + 1);}}
      catch (failure) {if (failure instanceof RequestNotSentError) journal.confirmed(reference); throw failure;}
    });
  }
  async function recover(reference: InvoiceReleaseReference) {
    if (busy || offline || storageFailed) return;
    await work(async valid => {
      const result = await gateway.recover(account.actorId, reference); if (result === null) {if (valid()) setNotice('Hasil asli belum ditemukan. Referensi tetap tersimpan; periksa lagi tanpa membuat pengganti.'); return;}
      journal.confirmed(reference); if (valid()) {clear(); setNotice('Hasil permintaan asli ditemukan: ' + actionText[result.action] + '. Muat riwayat untuk memeriksa fakta yang tersimpan.'); setRefresh(value => value + 1);}
    });
  }
  const update = (key: keyof ReturnType<typeof blank>, value: string) => setFields(previous => ({...previous, [key]: value}));
  return <>{error !== null && account !== null && <p role="alert" className={common.alert}>{message(error)}</p>}{notice && <p role="status">{notice}</p>}
    {offline && <p role="status">Koneksi belum tersedia. Referensi permintaan tetap disimpan.</p>}{storageFailed && <p role="alert">Penyimpanan referensi pemulihan belum tersedia. Periksa penyimpanan browser sebelum membuat perubahan.</p>}
    {pending.length > 0 && <section className={common.accountSection} aria-label="Pemulihan persetujuan invoice"><h2>Periksa hasil permintaan asli</h2>
      {pending.map(reference => <p key={reference.action + reference.key}>{actionText[reference.action]} · referensi {reference.key}<button className={common.secondary} disabled={busy || offline || storageFailed} onClick={() => void recover(reference)}>Periksa hasil {actionText[reference.action].toLowerCase()}</button></p>)}</section>}
    {canPropose && <section className={common.accountSection} aria-label="Penerimaan untuk persetujuan invoice"><h2>Pilih bukti penerimaan</h2>
      {candidates.items.length === 0 && <p>Belum ada penerimaan yang tersedia pada halaman ini.</p>}
      <ul className={styles.lines}>{candidates.items.map(item => <li className={styles.line} key={item.shipmentId}><label><input type="checkbox" checked={chosen.includes(item.receiptId)} disabled={locked || !!review || item.invoiced} onChange={event => {
        epoch.current++; setEligible(null); setRecord(null); setChosen(value => event.target.checked ? [...value, item.receiptId] : value.filter(id => id !== item.receiptId));
      }} />Penerimaan {item.receiptId} — {item.buyerName}</label><p>Kiriman {item.shipmentId} · {date(item.receivedAt)} · {item.acceptedLines} baris {item.invoiced && '· Sudah ditagihkan'}</p></li>)}</ul>
      <div className={common.actions}><button className={common.primary} disabled={locked || !!review || chosen.length === 0} onClick={() => void inspect()}>Periksa sumber penerbitan</button>
        <button className={common.secondary} disabled={locked || !!review} onClick={() => {clear(); setSourceCursor(null); setRefresh(value => value + 1);}}>Penerimaan terbaru</button>
        {candidates.nextShipmentId && <button className={common.secondary} disabled={locked || !!review} onClick={() => {clear(); setSourceCursor(candidates.nextShipmentId);}}>Penerimaan sebelumnya</button>}</div>
    </section>}
    {eligible && !review && <section className={common.accountSection} aria-label="Usulan persetujuan invoice"><h2>Lengkapi usulan</h2><Facts value={eligible.facts} />
      {eligible.state === 'propose' ? <p role="status">Masih ada usulan untuk sumber ini. Periksa riwayat dan keputusan pemeriksa.</p> : <fieldset disabled={locked}><legend>Fakta yang telah ditinjau</legend>
        <p>Isi nilai dari persetujuan dan sumber pembukuan yang berlaku. Nilai kosong belum dianggap nol.</p>
        <label>Awal periode penerimaan<input type="date" value={fields.periodFrom} onChange={event => update('periodFrom', event.target.value)} /></label>
        <label>Akhir periode penerimaan<input type="date" value={fields.periodTo} onChange={event => update('periodTo', event.target.value)} /></label>
        <label>Mulai berlaku (WIB)<input type="datetime-local" value={fields.validFrom} onChange={event => update('validFrom', event.target.value)} /></label>
        <label>Akhir berlaku (WIB)<input type="datetime-local" value={fields.validUntil} onChange={event => update('validUntil', event.target.value)} /></label>
        {([['templateApprovalReference', 'Referensi persetujuan format invoice'], ['numberingApprovalReference', 'Referensi persetujuan penomoran'], ['eligibilityReviewReference', 'Referensi pemeriksaan kelayakan'], ['receivable', 'Pemetaan akun piutang'], ['revenue', 'Pemetaan akun pendapatan'], ['tax', 'Pemetaan akun pajak'], ['charges', 'Pemetaan akun biaya']] as const).map(([key, label]) => <label key={key}>{label}<input value={fields[key]} maxLength={1000} onChange={event => update(key, event.target.value)} /></label>)}
        {eligible.facts.lines.map(line => {const key = [line.receiptId, line.skuId, line.lineRevision].join(':'); return <fieldset key={key}><legend>{line.skuCode} · penerimaan {line.receiptId} · baris {line.lineRevision}</legend>
          <label>Pajak {key}<input inputMode="decimal" value={amounts[key]?.tax ?? ''} onChange={event => setAmounts(value => ({...value, [key]: {tax: event.target.value, charges: value[key]?.charges ?? ''}}))} /></label>
          <label>Biaya {key}<input inputMode="decimal" value={amounts[key]?.charges ?? ''} onChange={event => setAmounts(value => ({...value, [key]: {tax: value[key]?.tax ?? '', charges: event.target.value}}))} /></label></fieldset>;})}
        <label>Alasan perubahan persetujuan invoice<textarea value={reason} maxLength={1000} onChange={event => setReason(event.target.value)} /></label>
        <button className={common.primary} onClick={() => void propose()}>Tinjau usulan penerbitan</button></fieldset>}
    </section>}
    <section className={common.accountSection} aria-label="Riwayat persetujuan invoice"><h2>Persetujuan yang tersimpan</h2>
      {history.items.length === 0 && <p>Belum ada persetujuan yang tersedia pada halaman ini.</p>}<ul>{history.items.map(item => <li key={item.result.releaseId}><button className={common.secondary} disabled={locked || !!review} onClick={() => void load(item.result.recordId)}>Lihat persetujuan {item.result.releaseId}</button> · {actionText[item.result.action]} · {item.facts.buyerName}</li>)}</ul>
      <button className={common.secondary} disabled={locked || !!review} onClick={() => {clear(); setHistoryCursor(null); setRefresh(value => value + 1);}}>Riwayat terbaru</button>
      {history.nextId && <button className={common.secondary} disabled={locked || !!review} onClick={() => {clear(); setHistoryCursor(history.nextId);}}>Riwayat sebelumnya</button>}
    </section>
    {record && !review && <section ref={focus} tabIndex={-1} className={common.accountSection} aria-label="Fakta persetujuan tersimpan"><h2>Persetujuan {record.result.releaseId} — {actionText[record.result.action]}</h2>
      <p>Fakta berikut merupakan catatan pada {date(record.recordedAt)}. Keputusan baru akan memeriksa sumber dan akses kembali.</p><InputFacts value={record.input} /><Facts value={record.facts} /><p>Alasan tersimpan: {record.reason}</p>
      {record.result.action === 'propose' && canApprove && <fieldset disabled={locked}><legend>Keputusan pemeriksa</legend><p>Pemeriksa harus berbeda orang dari pengusul dan memiliki verifikasi serta akses yang masih berlaku.</p>
        <label>Alasan keputusan persetujuan invoice<textarea value={reason} maxLength={1000} onChange={event => setReason(event.target.value)} /></label>
        <div className={common.actions}><button className={common.primary} onClick={() => void decide('approve')}>Tinjau persetujuan penerbitan</button><button className={common.secondary} onClick={() => void decide('reject')}>Tinjau penolakan penerbitan</button></div></fieldset>}
    </section>}
    {review && <section ref={focus} tabIndex={-1} className={common.accountSection} aria-label="Konfirmasi persetujuan invoice"><h2>Periksa {actionText[review.action].toLowerCase()} penerbitan</h2>
      <InputFacts value={review.preview.input} /><Facts value={review.preview.facts} /><p>Alasan: {review.reason}</p>
      <label><input type="checkbox" checked={confirmed} disabled={locked} onChange={event => setConfirmed(event.target.checked)} />Saya sudah memeriksa penerimaan, alokasi biaya, kalender, dan akun pembukuan yang ditampilkan.</label>
      <div className={common.actions}><button className={common.primary} disabled={locked || !confirmed} onClick={() => void commit()}>Konfirmasi {actionText[review.action].toLowerCase()} penerbitan</button>
        <button className={common.secondary} disabled={locked} onClick={() => {epoch.current++; setReview(null); setConfirmed(false);}}>Batalkan perubahan persetujuan</button></div></section>}
  </>;
}
