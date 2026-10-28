'use client';

import Link from 'next/link';
import { useCallback, useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react';
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
import { Button } from '@/components/workspace-kit/button';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { Section } from '@/components/workspace-kit/section';
import { StateView } from '@/components/workspace-kit/state-view';
import { Field } from '@/components/workspace-kit/field';
import { Facts } from '@/components/workspace-kit/facts';
import { Badge } from '@/components/workspace-kit/badge';
import { DataList } from '@/components/workspace-kit/data-list';

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
  // The desk owns the refresh, so it renders the header with it; the access states render it without.
  const header = (action?: ReactNode) => <PageHeader title="Buku akuntansi" description="Ajukan pemetaan akun dan periode pertama untuk buku organisasi, toko dan kanal yang ditugaskan." secondaryAction={action} />;
  return <main className={styles.page}>
    {account !== null && !loading && !sessionTiming(account.session, elapsed).expired
      ? <BookDesk key={account.actorId + ':' + account.observedAt} account={account} fresh={sessionTiming(account.session, elapsed).fresh} onDenied={denied} header={header} />
      : <>{header()}<div className="ui-stack">
        {error !== null && account !== null && <StateView kind="error" title={message(error)} />}
        {loading ? <StateView kind="loading" title="Memeriksa akun dan akses…" /> : <SessionAccessState kind={account ? "expired" : undefined} error={error} returnPath={path} onRetry={() => setRevision(value => value + 1)} />}
      </div></>}
  </main>;
}
function BookDetails({ value }: { value: Pick<AccountingBookProposal, 'key' | 'organization' | 'shop' | 'channel' | 'initialMonth' | 'mappings'> }) {
  return <><Facts items={[{ term: 'Organisasi', value: <>{value.organization.code} · {value.organization.name}</> }, { term: 'Toko', value: <>{value.shop.code} · {value.shop.name}</> },
    { term: 'Kanal', value: <>{value.channel.code} · {value.channel.name}</> }, { term: 'Mata uang', value: value.key.currency }, { term: 'Periode pertama', value: value.initialMonth }]} />
    <h4>Pemetaan akun</h4><Facts items={value.mappings.map(item => ({ term: item.mappingKey, value: item.accountCode }))} /></>;
}
function BookDesk({ account, fresh, onDenied, header }: { account: Account; fresh: boolean; onDenied: (failure: unknown) => void; header: (action?: ReactNode) => ReactNode }) {
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
    {header(<Button variant="secondary" disabled={locked || reviewLocked} onClick={() => { clearSelection(); setRevision(value => value + 1); }}>Muat data terbaru</Button>)}
    <div className="ui-stack">
    {canDecide && !fresh && <p role="status" className={styles.help}>Perbarui verifikasi kedua sebelum memutuskan pengajuan.
      {account.session.stepUpOperations.includes(approvePermission) && <> <Link href={`/identity/account?operation=${approvePermission}&returnPath=${encodeURIComponent(path)}`}>Perbarui verifikasi</Link></>}</p>}
    {offline && <StateView kind="offline" title="Koneksi terputus. Pengajuan dan keputusan ditahan sampai koneksi kembali." />}
    {error !== null && <StateView kind="error" title={message(error)} />}{notice && <StateView kind="success" title={notice} />}
    {recovery.error !== null && <StateView kind="error" title={message(recovery.error)} />}
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={locked} onResolved={reconcile} onFailure={onDenied} onBusyChange={setRecovering}
      recoveryAdapters={{ 'accounting.book': { lookup: entry => gateway.recover(entry.actorId, entry.operation, entry.key) } }} />
    <Section title="Pilihan buku" label="Pilihan buku" busy={busy}>
      <div className="ui-form">
        <Field id="book-target" label="Organisasi / toko / kanal" help="Persetujuan menyiapkan buku dan membuka periode pertama yang dipilih. Saldo awal dan penutupan periode memerlukan proses tersendiri.">
          <select value={selected ? accountingBookKey(selected.key) : ''} disabled={locked || reviewLocked || recovery.locked} onChange={event => {
            const choice = targets.find(item => accountingBookKey(item.key) === event.target.value); if (choice) void work((valid, signal) => select(choice, valid, signal));
          }}><option value="">Pilih buku yang ditugaskan</option>{targets.map(item => <option key={accountingBookKey(item.key)} value={accountingBookKey(item.key)}>{item.organization.name} · {item.shop.name} · {item.channel.name} · {item.key.currency}</option>)}</select></Field>
        {!canRead && <StateView kind="denied" title="Penugasan untuk membaca buku akuntansi belum tersedia. Hubungi pengelola akses." />}
        {busy && <StateView kind="loading" title="Memeriksa buku akuntansi…" rows={1} />}
        {!busy && canRead && targets.length === 0 && error === null && <StateView kind="empty" title="Belum ada organisasi, toko dan kanal dengan profil yang disetujui dalam penugasan Anda." />}
      </div>
    </Section>
    {selected && book && <div className="ui-desk">
      <Section title="Pengajuan buku" id="book-register-heading" description={<>{selected.shop.code} · {selected.channel.name}</>}>
        <DataList label="Pengajuan buku" items={page.items} getKey={item => item.id} disabled={locked || reviewLocked} selectedKey={proposal?.id ?? null}
          empty={{ title: 'Belum ada pengajuan buku.' }}
          onSelect={item => void work(async (valid, signal) => { clearReview(); const next = await gateway.proposal(account.actorId, item.id, signal); if (valid()) setProposal(next); })}
          renderTitle={item => <>Periode pertama {item.initialMonth}</>} renderBadge={item => <Badge status={states[item.state]} />}
          renderMeta={item => <>{when(item.proposedAt)} · Pengajuan {item.id}</>} />
        <div className="ui-actions" role="group" aria-label="Halaman pengajuan buku">
          <Button variant="secondary" disabled={locked || reviewLocked} onClick={() => void work((valid, signal) => select(selected, valid, signal))}>Terbaru</Button>
          <Button variant="secondary" disabled={locked || reviewLocked || page.nextProposalId === null} onClick={() => void work(async (valid, signal) => {
            const next = await gateway.proposals(account.actorId, selected.key, page.nextProposalId, signal); if (valid()) { clearReview(); setPage(next); }
          })}>Berikutnya</Button></div>
      </Section>
      <section className="ui-section" aria-label="Rincian buku akuntansi"><div className="ui-stack">
        {!editing && !proposal && <div className="ui-form"><h2>{selected.shop.name} · {selected.channel.name}</h2>
          {book.setup ? <><h3>Buku telah disiapkan</h3><BookDetails value={book.setup} /><p className={styles.help}>Disetujui {when(book.setup.decision!.decidedAt)}. Rincian ini mencatat konfigurasi awal buku.</p></> : <p>Buku dan periode pertama belum disiapkan.</p>}
          {canPropose && !book.configured && <Button variant="primary" disabled={commandLocked || selected.configured} onClick={() => { clearReview(); setEditing(true); }}>Ajukan buku akuntansi</Button>}</div>}
        {editing && <form className="ui-form" onSubmit={event => void previewProposal(event)}><h2>Ajukan buku akuntansi</h2><p>{selected.organization.name} · {selected.shop.name} · {selected.channel.name} · IDR</p>
          <fieldset disabled={commandLocked || proposalReview !== null}><Field id="book-initial-month" label="Periode pertama" width="date"><input required type="month" min="0001-01" max="9999-12" value={initialMonth} onChange={event => setInitialMonth(event.target.value)} /></Field>
            <p id="book-code-help" className={styles.help}>Kegunaan dan kode akun harus diawali huruf, lalu dapat berisi huruf, angka, titik, garis bawah atau tanda hubung, paling banyak 64 karakter. Gunakan kode persis seperti yang disepakati. Satu kode akun dapat dipakai untuk beberapa kegunaan.</p>
            {mappings.map((item, index) => <fieldset key={index}><legend>Pemetaan akun {index + 1}</legend><div className={styles.twoFields}>
              <Field id={`book-mapping-key-${index}`} label={`Kegunaan akun ${index + 1}`}><input required maxLength={64} pattern="[A-Za-z][A-Za-z0-9._\-]{0,63}" aria-describedby="book-code-help" autoComplete="off" value={item.mappingKey} onChange={event => setMappings(values => values.map((value, position) => position === index ? { ...value, mappingKey: event.target.value } : value))} /></Field>
              <Field id={`book-mapping-code-${index}`} label={`Kode akun ${index + 1}`}><input required maxLength={64} pattern="[A-Za-z][A-Za-z0-9._\-]{0,63}" aria-describedby="book-code-help" autoComplete="off" value={item.accountCode} onChange={event => setMappings(values => values.map((value, position) => position === index ? { ...value, accountCode: event.target.value } : value))} /></Field></div>
              <Button variant="secondary" type="button" disabled={mappings.length === 1} onClick={() => setMappings(values => values.filter((_, position) => position !== index))}>Hapus pemetaan {index + 1}</Button></fieldset>)}
            <Button variant="secondary" type="button" disabled={mappings.length >= 1000} onClick={() => setMappings(values => [...values, { mappingKey: '', accountCode: '' }])}>Tambah pemetaan akun</Button>
            <Field id="book-proposal-reason" label="Alasan pengajuan"><textarea required rows={3} maxLength={1000} value={reason} onChange={event => setReason(event.target.value)} /></Field><Button variant="secondary" type="submit">Periksa rincian pengajuan</Button></fieldset>
          {proposalReview && <section className={styles.preview} aria-label="Rincian pengajuan buku"><h3 ref={heading} tabIndex={-1}>Rincian yang akan diajukan</h3>
            <BookDetails value={{ ...proposalReview.preview.input, organization: proposalReview.preview.organization, shop: proposalReview.preview.shop, channel: proposalReview.preview.channel }} /><p className={styles.preserve}>{proposalReview.command.body.reason}</p>
            <label className={styles.checkbox}><input type="checkbox" checked={confirmed} disabled={commandLocked} onChange={event => setConfirmed(event.target.checked)} />Saya telah memeriksa buku, periode pertama dan seluruh pemetaan akun.</label>
            <div className={styles.actions}><Button variant="primary" type="button" disabled={commandLocked || !confirmed} onClick={() => void submitProposal()} busy={busy}>Kirim pengajuan buku</Button>
              <Button variant="secondary" type="button" disabled={commandLocked} onClick={() => { setProposalReview(null); setConfirmed(false); }}>Ubah rincian</Button></div></section>}
        </form>}
        {proposal && <div className="ui-form"><h2>{states[proposal.state]}</h2><BookDetails value={proposal} /><p className={styles.help}>Pengajuan {proposal.id} · {when(proposal.proposedAt)}</p>
          {proposal.state === 'approved' && <p role="status">Konfigurasi buku dan periode pertama dibuat melalui persetujuan ini. Saldo awal belum dicatat oleh proses ini.</p>}
          {proposal.state === 'pending' && canDecide && <><p className={styles.help}>Periksa seluruh pemetaan dan periode pertama. Pemeriksa harus berbeda dari pengaju dan menggunakan verifikasi kedua yang masih baru.</p>
            <Field id="book-decision-reason" label="Alasan keputusan"><textarea required rows={3} maxLength={1000} disabled={commandLocked || decisionReview !== null} value={decisionReason} onChange={event => setDecisionReason(event.target.value)} /></Field>
            {!decisionReview && <div className={styles.actions}><Button variant="primary" disabled={commandLocked || !fresh || !decisionReason.trim()} onClick={() => void previewDecision('approve')}>Tinjau persetujuan buku</Button>
              <Button variant="secondary" disabled={commandLocked || !fresh || !decisionReason.trim()} onClick={() => void previewDecision('reject')}>Tinjau penolakan buku</Button></div>}</>}
          {decisionReview && <section className={styles.preview} aria-label="Rincian keputusan buku"><h3 ref={heading} tabIndex={-1}>{decisionReview.command.decision === 'approve' ? 'Pastikan persetujuan buku' : 'Pastikan penolakan buku'}</h3>
            <BookDetails value={decisionReview.preview.proposal} /><p className={styles.preserve}>{decisionReview.command.body.reason}</p>
            <label className={styles.checkbox}><input type="checkbox" checked={confirmed} disabled={commandLocked || !fresh} onChange={event => setConfirmed(event.target.checked)} />Saya telah memeriksa pengajuan dan siap mencatat keputusan ini.</label>
            <div className={styles.actions}><Button variant="primary" disabled={commandLocked || !fresh || !confirmed} onClick={() => void saveDecision()}>{decisionReview.command.decision === 'approve' ? 'Setujui buku' : 'Simpan penolakan buku'}</Button>
              <Button variant="secondary" disabled={commandLocked} onClick={() => { setDecisionReview(null); setConfirmed(false); }}>Kembali ke pengajuan</Button></div></section>}
        </div>}
      </div></section></div>}
    </div>
  </>;
}
