'use client';

import Link from 'next/link';
import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { RequestNotSentError } from '../../api/client';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import { submitTrackedRequest } from '../administration/operation-journal';
import { CatalogReadGate } from '../catalog/catalog-editor-state';
import { accountingBookKey, accountingBookInput, createAccountingBookGateway, prepareAccountingBook, prepareAccountingBookDecision,
  type AccountingBookChoice, type AccountingBookInput, type AccountingBookCurrent, type AccountingBookProposal, type AccountingBookPreview,
  type AccountingBookDecision, type AccountingBookDecisionPreview } from './accounting-book-client';
import styles from '../administration/access.module.css';
import { dateTimeFormatter } from '@/lib/format';
import { SessionAccessState } from "@/components/workspace-kit/session-access-state";

type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
const path = '/accounting/books', readPermission = 'accounting.book.read', proposePermission = 'accounting.book.propose', approvePermission = 'accounting.book.approve';
const when = (value: string) => dateTimeFormatter.format(new Date(value));
const states = { pending: 'Menunggu pemeriksaan', approved: 'Disetujui', rejected: 'Ditolak' } as const;
function message(error: unknown) {
  if (error instanceof IdentityFailure) return 'Masuk dengan akun organisasi Anda untuk melanjutkan.';
  if (error instanceof AccessFailure || error instanceof RequestNotSentError) {
    if (error.code === 'accounting_book_invalid' || error.code === 'invalid_accounting_book_setup' || error.code === 'invalid_chart' || error.code === 'invalid_period') return 'Pilih buku, isi periode pertama dan pemetaan akun yang sah, lalu sertakan alasan.';
    if (error.code === 'accounting_book_view_stale' || error.code === 'accounting_book_setup_changed') return 'Buku atau sumber organisasi berubah. Muat data terbaru dan periksa kembali pengajuan.';
    if (error.code === 'accounting_book_already_configured') return 'Buku sudah disiapkan. Muat data terbaru untuk melihat konfigurasi yang telah disetujui.';
    if (error.code === 'accounting_book_response_invalid' || error.code === 'accounting_book_setup_integrity_unavailable') return 'Riwayat buku belum dapat dipastikan lengkap. Hubungi penanggung jawab sebelum melanjutkan.';
    if (error.code === 'independent_person_required') return 'Keputusan memerlukan pemeriksa yang berbeda dari pengaju.';
    if (error.code === 'commit_outcome_unknown' || error.code === 'resolve_previous_attempt') return 'Hasil belum terkonfirmasi. Gunakan Periksa hasil pada permintaan tersimpan; jangan kirim ulang.';
    if (error instanceof AccessFailure && error.status >= 500) return 'Layanan belum tersedia. Jika ada permintaan tersimpan, gunakan Periksa hasil setelah koneksi kembali.';
    return accessMessage(error);
  }
  return 'Koneksi terputus. Jika ada permintaan tersimpan, gunakan Periksa hasil; jangan kirim ulang.';
}
export function AccountingBookWorkspace() {
  const [identity] = useState(createIdentityGateway), [access] = useState(createAccessGateway);
  const [account, setAccount] = useState<Account | null>(null), [error, setError] = useState<unknown>(null), [loading, setLoading] = useState(true), [revision, setRevision] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => { let active = true; const controller = new AbortController(); void Promise.resolve().then(async () => {
    if (!active) return; setAccount(null); setLoading(true); setError(null);
    try {
      const session = await identity.session(controller.signal), observedAt = performance.now();
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
      const current = await access.current(); if (!active) return;
      if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
      if (![readPermission, proposePermission, approvePermission].some(value => current.allowedActions.includes(value))) throw new AccessFailure(403, 'scope_denied');
      if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
      setAccount({ actorId: current.userId, actions: current.allowedActions, session, observedAt }); setElapsed(performance.now() - observedAt);
    } catch (failure) { if (active) setError(failure); } finally { if (active) setLoading(false); }
  }); return () => { active = false; controller.abort(); }; }, [identity, access, revision]);
  useEffect(() => { const timer = window.setInterval(() => { if (account) setElapsed(performance.now() - account.observedAt); }, 1000);
    const restored = (event: PageTransitionEvent) => { if (event.persisted) { setAccount(null); setRevision(value => value + 1); } };
    window.addEventListener('pageshow', restored); return () => { window.clearInterval(timer); window.removeEventListener('pageshow', restored); }; }, [account]);
  const denied = useCallback((failure: unknown) => { if (failure instanceof AccessFailure && (failure.status === 401 || failure.code === 'actor_changed' || failure.code === 'session_expired')) { setAccount(null); setError(failure); } }, []);
  return <main className={styles.page}><header className={styles.header}><div><Link className={styles.brand} href="/identity/account">Unit Toko</Link><h1>Buku akuntansi</h1>
    <p>Ajukan pemetaan akun dan periode pertama untuk buku organisasi, toko dan kanal yang ditugaskan.</p></div><Link className={styles.secondary} href="/identity/account">Akun saya</Link></header>
    {error !== null && account !== null && <p role="alert" className={styles.alert}>{message(error)}</p>}
    {loading ? <p role="status">Memeriksa akun dan akses…</p> : !account || sessionTiming(account.session, elapsed).expired ? <SessionAccessState error={error} returnPath={path} onRetry={() => setRevision(value => value + 1)} />
      : <BookDesk key={account.actorId + ':' + account.observedAt} account={account} fresh={sessionTiming(account.session, elapsed).fresh} onDenied={denied} />}
  </main>;
}
function BookDetails({ value }: { value: Pick<AccountingBookProposal, 'key' | 'organization' | 'shop' | 'channel' | 'initialMonth' | 'mappings'> }) {
  return <><dl className={styles.facts}><div><dt>Organisasi</dt><dd>{value.organization.code} · {value.organization.name}</dd></div><div><dt>Toko</dt><dd>{value.shop.code} · {value.shop.name}</dd></div>
    <div><dt>Kanal</dt><dd>{value.channel.code} · {value.channel.name}</dd></div><div><dt>Mata uang</dt><dd>{value.key.currency}</dd></div><div><dt>Periode pertama</dt><dd>{value.initialMonth}</dd></div></dl>
    <h4>Pemetaan akun</h4><dl className={styles.facts}>{value.mappings.map(item => <div key={item.mappingKey}><dt>{item.mappingKey}</dt><dd>{item.accountCode}</dd></div>)}</dl></>;
}
function BookDesk({ account, fresh, onDenied }: { account: Account; fresh: boolean; onDenied: (failure: unknown) => void }) {
  const [gateway] = useState(createAccountingBookGateway), [gate] = useState(() => new CatalogReadGate()), recovery = useOperationRecovery(account.actorId);
  const ledger = useRef<MutationLedger | null>(null), keyPrefix = useRef('ab');
  const [targets, setTargets] = useState<AccountingBookChoice[]>([]), [selected, setSelected] = useState<AccountingBookChoice | null>(null), [book, setBook] = useState<AccountingBookCurrent | null>(null);
  const [page, setPage] = useState<{ items: AccountingBookProposal[]; nextProposalId: string | null }>({ items: [], nextProposalId: null }), [proposal, setProposal] = useState<AccountingBookProposal | null>(null);
  const [editing, setEditing] = useState(false), [initialMonth, setInitialMonth] = useState(''), [mappings, setMappings] = useState<AccountingBookInput['mappings']>([{ mappingKey: '', accountCode: '' }]);
  const [reason, setReason] = useState(''), [decisionReason, setDecisionReason] = useState(''), [confirmed, setConfirmed] = useState(false);
  const [proposalReview, setProposalReview] = useState<{ preview: AccountingBookPreview; command: ReturnType<typeof prepareAccountingBook> } | null>(null);
  const [decisionReview, setDecisionReview] = useState<{ preview: AccountingBookDecisionPreview; command: ReturnType<typeof prepareAccountingBookDecision> } | null>(null);
  const [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [offline, setOffline] = useState(false), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState(''), [revision, setRevision] = useState(0);
  const alive = useRef(false), pending = useRef(false), heading = useRef<HTMLHeadingElement>(null);
  const current = useCallback(() => alive.current && navigator.onLine && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const canRead = account.actions.includes(readPermission), canPropose = account.actions.includes(proposePermission), canDecide = account.actions.includes(approvePermission);
  const locked = busy || recovering || offline, commandLocked = locked || recovery.locked, reviewLocked = proposalReview !== null || decisionReview !== null;
  const clearReview = useCallback(() => { setProposal(null); setEditing(false); setProposalReview(null); setDecisionReview(null); setReason(''); setDecisionReason(''); setConfirmed(false); setInitialMonth(''); setMappings([{ mappingKey: '', accountCode: '' }]); }, []);
  const clearSelection = useCallback(() => { clearReview(); setSelected(null); setBook(null); setPage({ items: [], nextProposalId: null }); }, [clearReview]);
  function mutations() { return ledger.current ??= new MutationLedger(() => `${keyPrefix.current}.${crypto.randomUUID()}`); }
  useEffect(() => { alive.current = true; const connection = () => setOffline(!navigator.onLine); connection(); window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => { alive.current = false; gate.cancel(); window.removeEventListener('online', connection); window.removeEventListener('offline', connection); }; }, [gate]);
  useEffect(() => { const request = gate.begin(account.actorId, 'book-targets'); void Promise.resolve().then(async () => {
    if (!request.current() || !current()) return; setBusy(true); setTargets([]); clearSelection(); setError(null);
    try { if (canRead) { const result = await gateway.targets(account.actorId, request.signal); if (request.current() && current()) setTargets(result.items); } }
    catch (failure) { if (request.current() && current()) { setError(failure); onDenied(failure); } }
    finally { if (request.current()) setBusy(false); }
  }); return () => gate.cancel(); }, [gateway, gate, account.actorId, revision, current, onDenied, canRead, clearSelection]);
  useEffect(() => { if (proposalReview || decisionReview) heading.current?.focus(); }, [proposalReview, decisionReview]);
  useEffect(() => { if (!busy && recovery.entries.length === 0) return; const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ''; };
    window.addEventListener('beforeunload', warn); return () => window.removeEventListener('beforeunload', warn); }, [busy, recovery.entries.length]);
  async function work(action: (valid: () => boolean, signal: AbortSignal) => Promise<void>) {
    if (!current() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); setNotice('');
    const request = gate.begin(account.actorId, 'book-action'), valid = () => request.current() && current();
    try { await action(valid, request.signal); } catch (failure) { if (request.current() && alive.current) {
      setError(failure); if ((failure instanceof AccessFailure || failure instanceof RequestNotSentError) && ([401, 403].includes(failure.status) || ['actor_changed', 'accounting_book_view_stale', 'accounting_book_setup_changed', 'accounting_book_already_configured'].includes(failure.code))) { clearSelection(); setTargets([]); } onDenied(failure);
    } } finally { pending.current = false; recovery.refresh(); if (request.current() && alive.current) setBusy(false); }
  }
  async function select(choice: AccountingBookChoice, valid: () => boolean, signal: AbortSignal) {
    clearSelection(); const [nextBook, nextPage] = await Promise.all([gateway.read(account.actorId, choice.key, signal), gateway.proposals(account.actorId, choice.key, null, signal)]);
    if (valid()) { setSelected(choice); setBook(nextBook); setPage(nextPage); }
  }
  async function previewProposal(event: FormEvent) {
    event.preventDefault(); if (!selected || !canPropose || commandLocked || book?.configured) return;
    await work(async (valid, signal) => {
      const input = accountingBookInput({ key: selected.key, initialMonth, mappings }), preview = await gateway.preview(account.actorId, input, signal);
      const command = prepareAccountingBook(account.actorId, selected, input, preview, reason.normalize('NFKC').trim());
      if (valid()) { setProposalReview({ preview, command }); setConfirmed(false); }
    });
  }
  async function submitProposal() {
    if (!proposalReview || !confirmed || commandLocked || !canPropose) return;
    const command = proposalReview.command; keyPrefix.current = 'ab.' + accountingBookKey(command.key).replaceAll(':', '.');
    await work(async (valid, signal) => {
      const saved = await submitTrackedRequest(mutations(), recovery.journal, account.actorId, 'book-propose', 'accounting.book.propose', command.body, requestKey => gateway.propose(command, requestKey, valid));
      if (!valid()) return; clearReview(); setNotice('Pengajuan tersimpan dan menunggu pemeriksaan independen.');
      const next = await gateway.proposal(account.actorId, saved.proposalId, signal); if (valid()) setProposal(next);
    });
  }
  async function previewDecision(value: AccountingBookDecision) {
    if (!proposal || !canDecide || !fresh || commandLocked || proposal.state !== 'pending') return;
    await work(async (valid, signal) => {
      const preview = await gateway.previewDecision(account.actorId, proposal, value, signal), command = prepareAccountingBookDecision(account.actorId, proposal, preview, decisionReason.normalize('NFKC').trim());
      if (valid()) { setDecisionReview({ preview, command }); setConfirmed(false); }
    });
  }
  async function saveDecision() {
    if (!decisionReview || !confirmed || !canDecide || !fresh || commandLocked) return;
    const command = decisionReview.command; keyPrefix.current = `abd.${command.decision}.${command.proposalId}`;
    await work(async (valid, signal) => {
      const saved = await submitTrackedRequest(mutations(), recovery.journal, account.actorId, 'book-decision', 'accounting.book.' + command.decision, command.body,
        requestKey => gateway.decide(command, requestKey, () => valid() && sessionTiming(account.session, performance.now() - account.observedAt).fresh));
      if (!valid()) return; clearReview(); setNotice(command.decision === 'approve' ? 'Buku, pemetaan akun dan periode pertama telah disiapkan. Tidak ada saldo awal yang dicatat.' : 'Penolakan tersimpan. Buku belum dibuat dari pengajuan ini.');
      const next = await gateway.proposal(account.actorId, saved.proposalId, signal); if (valid()) setProposal(next);
    });
  }
  function reconcile() { ledger.current?.resetAfterReconciliation(); clearSelection(); recovery.refresh(); setRevision(value => value + 1); }
  return <>
    {canDecide && !fresh && <p className={styles.alert}>Perbarui verifikasi kedua sebelum memutuskan pengajuan.
      {account.session.stepUpOperations.includes(approvePermission) && <Link href={`/identity/account?operation=${approvePermission}&returnPath=${encodeURIComponent(path)}`}>Perbarui verifikasi</Link>}</p>}
    {offline && <p role="status" className={styles.alert}>Koneksi terputus. Pengajuan dan keputusan ditahan sampai koneksi kembali.</p>}
    {error !== null && <p role="alert" className={styles.alert}>{message(error)}</p>}{notice && <p role="status" className={styles.success}>{notice}</p>}
    {recovery.error !== null && <p role="alert" className={styles.alert}>{message(recovery.error)}</p>}
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={locked} onResolved={reconcile} onFailure={onDenied} onBusyChange={setRecovering}
      recoveryAdapters={{ 'accounting.book': { lookup: entry => gateway.recover(entry.actorId, entry.operation, entry.key) } }} />
    <section className={styles.accountSection} aria-label="Pilihan buku"><label><span id="book-target-label">Organisasi / toko / kanal</span>
      <select aria-labelledby="book-target-label" value={selected ? accountingBookKey(selected.key) : ''} disabled={locked || reviewLocked || recovery.locked} onChange={event => {
        const choice = targets.find(item => accountingBookKey(item.key) === event.target.value); if (choice) void work((valid, signal) => select(choice, valid, signal));
      }}><option value="">Pilih buku yang ditugaskan</option>{targets.map(item => <option key={accountingBookKey(item.key)} value={accountingBookKey(item.key)}>{item.organization.name} · {item.shop.name} · {item.channel.name} · {item.key.currency}</option>)}</select></label>
      <p className={styles.help}>Persetujuan menyiapkan buku dan membuka periode pertama yang dipilih. Saldo awal dan penutupan periode memerlukan proses tersendiri.</p>
      {!canRead && <p className={styles.alert}>Penugasan untuk membaca buku akuntansi belum tersedia. Hubungi pengelola akses.</p>}
      <button className={styles.secondary} disabled={locked || reviewLocked} onClick={() => { clearSelection(); setRevision(value => value + 1); }}>Muat data terbaru</button>
      {!busy && canRead && targets.length === 0 && <p>Belum ada organisasi, toko dan kanal dengan profil yang disetujui dalam penugasan Anda.</p>}
    </section>
    {busy && <p role="status">Memeriksa buku akuntansi…</p>}
    {selected && book && <div className={styles.desk}><section className={styles.queue} aria-labelledby="book-register-heading"><div className={styles.queueHeader}><h2 id="book-register-heading">Pengajuan buku</h2><span>{selected.shop.code} · {selected.channel.name}</span></div>
      <ul className={styles.queueList}>{page.items.map(item => <li key={item.id}><button className={proposal?.id === item.id ? styles.selectedRow : styles.queueRow} disabled={locked || reviewLocked}
        onClick={() => void work(async (valid, signal) => { clearReview(); const next = await gateway.proposal(account.actorId, item.id, signal); if (valid()) setProposal(next); })}>
        <strong>Periode pertama {item.initialMonth}</strong><span>{states[item.state]}</span><small>{when(item.proposedAt)} · Pengajuan {item.id}</small></button></li>)}</ul>
      {page.items.length === 0 && <p className={styles.empty}>Belum ada pengajuan buku.</p>}
      <nav className={styles.pagination} aria-label="Halaman pengajuan buku"><button className={styles.secondary} disabled={locked || reviewLocked} onClick={() => void work((valid, signal) => select(selected, valid, signal))}>Terbaru</button>
        <button className={styles.secondary} disabled={locked || reviewLocked || page.nextProposalId === null} onClick={() => void work(async (valid, signal) => {
          const next = await gateway.proposals(account.actorId, selected.key, page.nextProposalId, signal); if (valid()) { clearReview(); setPage(next); }
        })}>Berikutnya</button></nav></section>
      <section className={styles.detail} aria-label="Rincian buku akuntansi">
        {!editing && !proposal && <div className={styles.form}><h2>{selected.shop.name} · {selected.channel.name}</h2>
          {book.setup ? <><h3>Buku telah disiapkan</h3><BookDetails value={book.setup} /><p className={styles.help}>Disetujui {when(book.setup.decision!.decidedAt)}. Rincian ini mencatat konfigurasi awal buku.</p></> : <p>Buku dan periode pertama belum disiapkan.</p>}
          {canPropose && !book.configured && <button className={styles.primary} disabled={commandLocked || selected.configured} onClick={() => { clearReview(); setEditing(true); }}>Ajukan buku akuntansi</button>}</div>}
        {editing && <form className={styles.form} onSubmit={event => void previewProposal(event)}><h2>Ajukan buku akuntansi</h2><p>{selected.organization.name} · {selected.shop.name} · {selected.channel.name} · IDR</p>
          <fieldset className={styles.fields} disabled={commandLocked || proposalReview !== null}><label>Periode pertama<input required type="month" min="0001-01" max="9999-12" value={initialMonth} onChange={event => setInitialMonth(event.target.value)} /></label>
            <p id="book-code-help" className={styles.help}>Kegunaan dan kode akun harus diawali huruf, lalu dapat berisi huruf, angka, titik, garis bawah atau tanda hubung, paling banyak 64 karakter. Gunakan kode persis seperti yang disepakati. Satu kode akun dapat dipakai untuk beberapa kegunaan.</p>
            {mappings.map((item, index) => <fieldset className={styles.fields} key={index}><legend>Pemetaan akun {index + 1}</legend><div className={styles.twoFields}>
              <label>Kegunaan akun {index + 1}<input required maxLength={64} pattern="[A-Za-z][A-Za-z0-9._\-]{0,63}" aria-describedby="book-code-help" autoComplete="off" value={item.mappingKey} onChange={event => setMappings(values => values.map((value, position) => position === index ? { ...value, mappingKey: event.target.value } : value))} /></label>
              <label>Kode akun {index + 1}<input required maxLength={64} pattern="[A-Za-z][A-Za-z0-9._\-]{0,63}" aria-describedby="book-code-help" autoComplete="off" value={item.accountCode} onChange={event => setMappings(values => values.map((value, position) => position === index ? { ...value, accountCode: event.target.value } : value))} /></label></div>
              <button type="button" className={styles.secondary} disabled={mappings.length === 1} onClick={() => setMappings(values => values.filter((_, position) => position !== index))}>Hapus pemetaan {index + 1}</button></fieldset>)}
            <button type="button" className={styles.secondary} disabled={mappings.length >= 1000} onClick={() => setMappings(values => [...values, { mappingKey: '', accountCode: '' }])}>Tambah pemetaan akun</button>
            <label>Alasan pengajuan<textarea required rows={3} maxLength={1000} value={reason} onChange={event => setReason(event.target.value)} /></label><button className={styles.secondary}>Periksa rincian pengajuan</button></fieldset>
          {proposalReview && <section className={styles.review} aria-label="Rincian pengajuan buku"><h3 ref={heading} tabIndex={-1}>Rincian yang akan diajukan</h3>
            <BookDetails value={{ ...proposalReview.preview.input, organization: proposalReview.preview.organization, shop: proposalReview.preview.shop, channel: proposalReview.preview.channel }} /><p className={styles.preserve}>{proposalReview.command.body.reason}</p>
            <label className={styles.checkbox}><input type="checkbox" checked={confirmed} disabled={commandLocked} onChange={event => setConfirmed(event.target.checked)} />Saya telah memeriksa buku, periode pertama dan seluruh pemetaan akun.</label>
            <div className={styles.actions}><button type="button" className={styles.primary} disabled={commandLocked || !confirmed} onClick={() => void submitProposal()}>Kirim pengajuan buku</button>
              <button type="button" className={styles.secondary} disabled={commandLocked} onClick={() => { setProposalReview(null); setConfirmed(false); }}>Ubah rincian</button></div></section>}
        </form>}
        {proposal && <div className={styles.form}><h2>{states[proposal.state]}</h2><BookDetails value={proposal} /><p className={styles.help}>Pengajuan {proposal.id} · {when(proposal.proposedAt)}</p>
          {proposal.state === 'approved' && <p role="status">Konfigurasi buku dan periode pertama dibuat melalui persetujuan ini. Saldo awal belum dicatat oleh proses ini.</p>}
          {proposal.state === 'pending' && canDecide && <><p className={styles.help}>Periksa seluruh pemetaan dan periode pertama. Pemeriksa harus berbeda dari pengaju dan menggunakan verifikasi kedua yang masih baru.</p>
            <label>Alasan keputusan<textarea required rows={3} maxLength={1000} disabled={commandLocked || decisionReview !== null} value={decisionReason} onChange={event => setDecisionReason(event.target.value)} /></label>
            {!decisionReview && <div className={styles.actions}><button className={styles.primary} disabled={commandLocked || !fresh || !decisionReason.trim()} onClick={() => void previewDecision('approve')}>Tinjau persetujuan buku</button>
              <button className={styles.secondary} disabled={commandLocked || !fresh || !decisionReason.trim()} onClick={() => void previewDecision('reject')}>Tinjau penolakan buku</button></div>}</>}
          {decisionReview && <section className={styles.review} aria-label="Rincian keputusan buku"><h3 ref={heading} tabIndex={-1}>{decisionReview.command.decision === 'approve' ? 'Pastikan persetujuan buku' : 'Pastikan penolakan buku'}</h3>
            <BookDetails value={decisionReview.preview.proposal} /><p className={styles.preserve}>{decisionReview.command.body.reason}</p>
            <label className={styles.checkbox}><input type="checkbox" checked={confirmed} disabled={commandLocked || !fresh} onChange={event => setConfirmed(event.target.checked)} />Saya telah memeriksa pengajuan dan siap mencatat keputusan ini.</label>
            <div className={styles.actions}><button className={styles.primary} disabled={commandLocked || !fresh || !confirmed} onClick={() => void saveDecision()}>{decisionReview.command.decision === 'approve' ? 'Setujui buku' : 'Simpan penolakan buku'}</button>
              <button className={styles.secondary} disabled={commandLocked} onClick={() => { setDecisionReview(null); setConfirmed(false); }}>Kembali ke pengajuan</button></div></section>}
        </div>}
      </section></div>}
  </>;
}
