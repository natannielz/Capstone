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
import { orderAmount } from './order-quote-client';
import { createSpendingPolicyGateway, spendingPolicyInput, spendingPolicyThreshold, spendingPolicyTime, spendingRuleLabels, prepareSpendingPolicy, prepareSpendingPolicyDecision,
  type SpendingPolicyRule, type SpendingPolicyChoice, type SpendingPolicyCurrent, type SpendingPolicyProposal, type SpendingPolicyPreview,
  type SpendingPolicyDecisionPreview, type SpendingPolicyDecision } from './order-spending-policy-client';
import styles from '../administration/access.module.css';
import { dateTimeFormatter } from '@/lib/format';
import { SessionAccessState } from "@/components/workspace-kit/session-access-state";

type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
const path = '/ordering/spending-policies', readPermission = 'order.spending.policy.read', proposePermission = 'order.spending.policy.propose', approvePermission = 'order.spending.policy.approve';
const when = (value: string) => dateTimeFormatter.format(new Date(value));
const status = { pending: 'Menunggu pemeriksaan', approved: 'Disetujui', rejected: 'Ditolak' } as const;
function message(error: unknown) {
  if (error instanceof IdentityFailure) return 'Sesi belum tersedia. Masuk kembali untuk memeriksa aturan belanja.';
  if (error instanceof AccessFailure || error instanceof RequestNotSentError) {
    if (error.code === 'spending_policy_invalid' || error.code === 'order_spending_policy_invalid') return 'Pilih aturan, isi ambang jika diperlukan, tentukan masa berlaku dan sertakan alasan.';
    if (error.code === 'order_spending_policy_changed' || error.code === 'spending_policy_view_stale') return 'Unit atau aturan berubah. Muat data terbaru, lalu periksa ulang pengajuan.';
    if (error.code === 'spending_policy_response_invalid' || error.code === 'order_spending_policy_integrity_unavailable') return 'Riwayat aturan belum dapat dipastikan lengkap. Hubungi penanggung jawab sebelum melanjutkan.';
    if (error.code === 'independent_person_required') return 'Keputusan memerlukan pemeriksa yang berbeda dari pengaju.';
    if (error.code === 'commit_outcome_unknown' || error.code === 'resolve_previous_attempt') return 'Hasil pengajuan belum terkonfirmasi. Gunakan Periksa hasil pada permintaan tersimpan; jangan kirim ulang.';
    if (error instanceof AccessFailure && error.status >= 500) return 'Layanan belum tersedia. Jika ada permintaan tersimpan, gunakan Periksa hasil setelah koneksi kembali.';
  }
  if (!(error instanceof AccessFailure) && !(error instanceof RequestNotSentError)) return 'Koneksi terputus. Jika ada permintaan tersimpan, gunakan Periksa hasil; jangan kirim ulang.';
  return accessMessage(error);
}
export function OrderSpendingPolicyWorkspace() {
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
  return <main className={styles.page}><header className={styles.header}><div><Link href="/identity/account" className={styles.brand}>Unit Toko</Link>
    <h1>Aturan persetujuan belanja</h1><p>Tentukan kapan pesanan cabang atau divisi perlu diperiksa sebelum diproses.</p></div><Link className={styles.secondary} href="/identity/account">Akun saya</Link></header>
    {error !== null && account !== null && <p role="alert" className={styles.alert}>{message(error)}</p>}
    {loading ? <p role="status">Memeriksa akun dan akses…</p> : !account || sessionTiming(account.session, elapsed).expired ? <SessionAccessState error={error} returnPath={path} onRetry={() => setRevision(value => value + 1)} />
      : <PolicyDesk key={account.actorId + ':' + account.observedAt} account={account} elapsed={elapsed} fresh={sessionTiming(account.session, elapsed).fresh} onDenied={denied} />}
  </main>;
}
function RuleDetails({ value }: { value: { rule: SpendingPolicyRule; validFrom: string; validUntil: string } }) {
  return <dl className={styles.facts}><div><dt>Kebutuhan persetujuan</dt><dd>{spendingRuleLabels[value.rule.mode]}</dd></div>
    {value.rule.threshold !== null && <div><dt>Ambang belanja</dt><dd>{orderAmount('IDR', value.rule.threshold)}<br /><small>Persetujuan diperlukan jika total pesanan melebihi nilai ini.</small></dd></div>}
    <div><dt>Mulai berlaku</dt><dd>{when(value.validFrom)}</dd></div><div><dt>Berakhir</dt><dd>{when(value.validUntil)}</dd></div></dl>;
}
function PolicyDesk({ account, elapsed, fresh, onDenied }: { account: Account; elapsed: number; fresh: boolean; onDenied: (failure: unknown) => void }) {
  const [gateway] = useState(createSpendingPolicyGateway), [gate] = useState(() => new CatalogReadGate()), recovery = useOperationRecovery(account.actorId);
  const keyPrefix = useRef('osp'), ledger = useRef<MutationLedger | null>(null);
  const [targets, setTargets] = useState<{ items: SpendingPolicyChoice[]; hasMore: boolean; nextTargetId: string | null }>({ items: [], hasMore: false, nextTargetId: null });
  const [targetCursor, setTargetCursor] = useState<string | null>(null), [selected, setSelected] = useState<SpendingPolicyChoice | null>(null), [policy, setPolicy] = useState<SpendingPolicyCurrent | null>(null);
  const [page, setPage] = useState<{ items: SpendingPolicyProposal[]; nextProposalId: string | null }>({ items: [], nextProposalId: null });
  const [proposal, setProposal] = useState<SpendingPolicyProposal | null>(null), [editing, setEditing] = useState(false), [mode, setMode] = useState<SpendingPolicyRule['mode'] | ''>(''), [threshold, setThreshold] = useState('');
  const [validFrom, setValidFrom] = useState(''), [validUntil, setValidUntil] = useState(''), [reason, setReason] = useState(''), [decisionReason, setDecisionReason] = useState('');
  const [proposalReview, setProposalReview] = useState<{ preview: SpendingPolicyPreview; command: ReturnType<typeof prepareSpendingPolicy> } | null>(null);
  const [decisionReview, setDecisionReview] = useState<{ preview: SpendingPolicyDecisionPreview; command: ReturnType<typeof prepareSpendingPolicyDecision> } | null>(null);
  const [confirmed, setConfirmed] = useState(false), [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [offline, setOffline] = useState(false), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState(''), [revision, setRevision] = useState(0);
  const alive = useRef(false), pending = useRef(false), heading = useRef<HTMLHeadingElement>(null);
  const current = useCallback(() => alive.current && navigator.onLine && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const canRead = account.actions.includes(readPermission), canPropose = account.actions.includes(proposePermission), canDecide = account.actions.includes(approvePermission);
  const serverNow = Date.parse(account.session.serverTime) + Math.max(0, elapsed);
  const policyActive = policy?.active && policy.validFrom !== null && policy.validUntil !== null && Date.parse(policy.validFrom) <= serverNow && serverNow < Date.parse(policy.validUntil);
  const proposalExpired = proposal !== null && Date.parse(proposal.validUntil) <= serverNow;
  const locked = busy || recovering || offline, commandLocked = locked || recovery.locked, reviewLocked = proposalReview !== null || decisionReview !== null;
  const clearReview = useCallback(() => { setProposal(null); setEditing(false); setProposalReview(null); setDecisionReview(null); setReason(''); setDecisionReason(''); setConfirmed(false); setMode(''); setThreshold(''); setValidFrom(''); setValidUntil(''); }, []);
  const clearSelection = useCallback(() => { clearReview(); setSelected(null); setPolicy(null); setPage({ items: [], nextProposalId: null }); }, [clearReview]);
  function mutations() { return ledger.current ??= new MutationLedger(() => `${keyPrefix.current}.${crypto.randomUUID()}`); }
  useEffect(() => { alive.current = true; const connection = () => setOffline(!navigator.onLine); connection(); window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => { alive.current = false; gate.cancel(); window.removeEventListener('online', connection); window.removeEventListener('offline', connection); }; }, [gate]);
  useEffect(() => { const request = gate.begin(account.actorId, 'targets:' + (targetCursor ?? '')); void Promise.resolve().then(async () => {
    if (!request.current() || !current()) return; setBusy(true); setTargets({ items: [], hasMore: false, nextTargetId: null }); clearSelection(); setError(null);
    try { if (canRead) { const result = await gateway.targets(account.actorId, targetCursor, request.signal); if (request.current() && current()) setTargets(result); } }
    catch (failure) { if (request.current() && current()) { setError(failure); onDenied(failure); } }
    finally { if (request.current()) setBusy(false); }
  }); return () => gate.cancel(); }, [gateway, gate, account.actorId, targetCursor, revision, current, onDenied, canRead, clearSelection]);
  useEffect(() => { if (proposalReview || decisionReview) heading.current?.focus(); }, [proposalReview, decisionReview]);
  useEffect(() => { if (!busy && recovery.entries.length === 0) return; const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ''; };
    window.addEventListener('beforeunload', warn); return () => window.removeEventListener('beforeunload', warn); }, [busy, recovery.entries.length]);
  async function work(action: (valid: () => boolean, signal: AbortSignal) => Promise<void>) {
    if (!current() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); setNotice('');
    const request = gate.begin(account.actorId, 'policy-action'), valid = () => request.current() && current();
    try { await action(valid, request.signal); } catch (failure) { if (request.current() && alive.current) {
      setError(failure); if (failure instanceof AccessFailure && ([401, 403].includes(failure.status) || failure.code === 'actor_changed')) { clearSelection(); setTargets({ items: [], hasMore: false, nextTargetId: null }); } onDenied(failure);
    } } finally { pending.current = false; recovery.refresh(); if (request.current() && alive.current) setBusy(false); }
  }
  async function select(choice: SpendingPolicyChoice, valid: () => boolean, signal: AbortSignal) {
    clearSelection(); const [nextPolicy, nextPage] = await Promise.all([gateway.read(account.actorId, choice, signal), gateway.proposals(account.actorId, choice, null, signal)]);
    if (valid()) { setSelected(choice); setPolicy(nextPolicy); setPage(nextPage); }
  }
  async function previewProposal(event: FormEvent) {
    event.preventDefault(); if (!selected || !canPropose || commandLocked || !mode) return;
    await work(async (valid, signal) => {
      const input = spendingPolicyInput({ target: selected.target, expectedScopeVersion: selected.expectedScopeVersion, expectedProfileVersion: selected.expectedProfileVersion, expectedPolicyVersion: selected.policyVersion,
        rule: { mode, threshold: mode === 'above_threshold' ? spendingPolicyThreshold(threshold) : null }, validFrom: spendingPolicyTime(validFrom), validUntil: spendingPolicyTime(validUntil) });
      const preview = await gateway.preview(account.actorId, input, signal), command = prepareSpendingPolicy(account.actorId, selected, input, preview, reason.normalize('NFKC').trim());
      if (valid()) { setProposalReview({ preview, command }); setConfirmed(false); }
    });
  }
  async function submitProposal() {
    if (!proposalReview || !confirmed || commandLocked || !canPropose) return;
    const command = proposalReview.command; keyPrefix.current = `osp.${command.target.kind}.${command.target.id}`;
    await work(async (valid, signal) => {
      const result = await submitTrackedRequest(mutations(), recovery.journal, account.actorId, 'spending-policy-propose', 'ordering.spending.policy.propose', command.body, key => gateway.propose(command, key, valid));
      if (!valid()) return; clearReview(); setNotice('Pengajuan tersimpan. Aturan berlaku setelah disetujui pemeriksa lain dan memasuki masa berlakunya.');
      const next = await gateway.proposal(account.actorId, result.proposalId, signal); if (valid()) setProposal(next);
    });
  }
  async function previewDecision(value: SpendingPolicyDecision) {
    if (!proposal || !canDecide || !fresh || commandLocked || proposal.state !== 'pending' || value === 'approve' && proposalExpired) return;
    await work(async (valid, signal) => {
      const preview = await gateway.previewDecision(account.actorId, proposal, value, signal), command = prepareSpendingPolicyDecision(account.actorId, proposal, preview, decisionReason.normalize('NFKC').trim());
      if (valid()) { setDecisionReview({ preview, command }); setConfirmed(false); }
    });
  }
  async function saveDecision() {
    if (!decisionReview || !confirmed || !canDecide || !fresh || commandLocked || decisionReview.command.decision === 'approve' && proposalExpired) return;
    const command = decisionReview.command; keyPrefix.current = `ospd.${command.decision}.${command.proposalId}`;
    await work(async (valid, signal) => {
      const result = await submitTrackedRequest(mutations(), recovery.journal, account.actorId, 'spending-policy-decision', 'ordering.spending.policy.' + command.decision, command.body,
        key => gateway.decide(command, key, () => valid() && sessionTiming(account.session, performance.now() - account.observedAt).fresh));
      if (!valid()) return; clearReview(); setNotice(command.decision === 'approve' ? 'Aturan disetujui. Muat data terbaru untuk memeriksa status dan masa berlakunya.' : 'Penolakan tersimpan. Aturan yang berlaku tidak berubah.');
      const next = await gateway.proposal(account.actorId, result.proposalId, signal); if (valid()) setProposal(next);
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
      recoveryAdapters={{ 'ordering.spending.policy': { lookup: entry => gateway.recover(entry.actorId, entry.operation, entry.key) } }} />
    <section className={styles.accountSection} aria-label="Unit aturan belanja"><label><span id="spending-target-label">Cabang / divisi</span>
      <select aria-labelledby="spending-target-label" value={selected ? selected.target.kind + ':' + selected.target.id : ''} disabled={locked || reviewLocked || recovery.locked} onChange={event => {
        const choice = targets.items.find(item => item.target.kind + ':' + item.target.id === event.target.value); if (choice) void work((valid, signal) => select(choice, valid, signal));
      }}><option value="">Pilih unit yang ditugaskan</option>{targets.items.map(item => <option key={item.target.id} value={item.target.kind + ':' + item.target.id}>{item.code} · {item.name} — {item.target.kind === 'branch' ? 'Cabang' : 'Divisi'}</option>)}</select></label>
      <p className={styles.help}>Aturan berlaku untuk unit yang dipilih. Persetujuan belanja tidak menetapkan anggaran, batas kredit atau penerimaan pesanan oleh toko.</p>
      {!canRead && <p className={styles.alert}>Penugasan untuk membaca aturan belanja belum tersedia. Hubungi pengelola akses.</p>}
      <div className={styles.actions}><button className={styles.secondary} disabled={locked || reviewLocked} onClick={() => { clearSelection(); setTargetCursor(null); setRevision(value => value + 1); }}>Muat data terbaru</button>
        <button className={styles.secondary} disabled={locked || reviewLocked || !targets.hasMore} onClick={() => setTargetCursor(targets.nextTargetId)}>Unit berikutnya</button></div>
      {!busy && canRead && targets.items.length === 0 && <p>Belum ada cabang atau divisi dengan profil yang disetujui dalam penugasan Anda.</p>}
    </section>
    {busy && <p role="status">Memeriksa aturan belanja…</p>}
    {selected && policy && <div className={styles.desk}><section className={styles.queue} aria-labelledby="spending-register-heading"><div className={styles.queueHeader}><h2 id="spending-register-heading">Pengajuan aturan</h2><span>{selected.code} · {selected.name}</span></div>
      <ul className={styles.queueList}>{page.items.map(item => <li key={item.proposalId}><button className={proposal?.proposalId === item.proposalId ? styles.selectedRow : styles.queueRow} disabled={locked || reviewLocked}
        onClick={() => void work(async (valid, signal) => { clearReview(); const next = await gateway.proposal(account.actorId, item.proposalId, signal); if (valid()) setProposal(next); })}>
        <strong>{spendingRuleLabels[item.rule.mode]}</strong><span>{status[item.state]}</span><small>{when(item.proposedAt)} · Pengajuan {item.proposalId}</small></button></li>)}</ul>
      {page.items.length === 0 && <p className={styles.empty}>Belum ada pengajuan aturan belanja.</p>}
      <nav className={styles.pagination} aria-label="Halaman pengajuan aturan"><button className={styles.secondary} disabled={locked || reviewLocked} onClick={() => void work((valid, signal) => select(selected, valid, signal))}>Terbaru</button>
        <button className={styles.secondary} disabled={locked || reviewLocked || page.nextProposalId === null} onClick={() => void work(async (valid, signal) => {
          const next = await gateway.proposals(account.actorId, selected, page.nextProposalId, signal); if (valid()) { clearReview(); setPage(next); }
        })}>Berikutnya</button></nav></section>
      <section className={styles.detail} aria-label="Rincian aturan belanja">
        {!editing && !proposal && <div className={styles.form}><h2>{selected.code} · {selected.name}</h2>
          {policy.rule && policy.validFrom && policy.validUntil ? <><h3>{policyActive ? 'Aturan aktif' : policy.current ? 'Aturan yang disetujui' : 'Aturan terakhir disetujui'}</h3>
            <RuleDetails value={{ rule: policy.rule, validFrom: policy.validFrom, validUntil: policy.validUntil }} /><p className={styles.help}>Versi {policy.version} · Disetujui {when(policy.approvedAt!)}</p>
            {!policyActive && <p className={styles.alert}>{serverNow < Date.parse(policy.validFrom) ? 'Masa berlaku aturan ini belum dimulai.' : serverNow >= Date.parse(policy.validUntil)
              ? 'Masa berlaku aturan ini telah berakhir.' : 'Aturan ini belum dapat dipakai untuk pesanan baru. Minta pengelola memeriksa kembali aturan dan sumber unit.'}</p>}</> : <p>Belum ada aturan belanja yang disetujui untuk unit ini.</p>}
          {canPropose && <button className={styles.primary} disabled={commandLocked} onClick={() => { clearReview(); setEditing(true); }}>Ajukan aturan belanja</button>}</div>}
        {editing && <form className={styles.form} onSubmit={event => void previewProposal(event)}><h2>Ajukan aturan belanja</h2><p>{selected.code} · {selected.name}</p>
          <fieldset className={styles.fields} disabled={commandLocked || proposalReview !== null}><label><span id="spending-mode-label">Kebutuhan persetujuan</span>
            <select aria-labelledby="spending-mode-label" required value={mode} onChange={event => { setMode(event.target.value as SpendingPolicyRule['mode']); setThreshold(''); }}>
              <option value="">Pilih aturan</option>{Object.entries(spendingRuleLabels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
            {mode === 'above_threshold' && <label><span id="spending-threshold-label">Ambang belanja (IDR)</span><input aria-labelledby="spending-threshold-label" aria-describedby="spending-threshold-help" required inputMode="decimal" maxLength={18} value={threshold} onChange={event => setThreshold(event.target.value)} /><small id="spending-threshold-help">Gunakan titik untuk desimal, tanpa pemisah ribuan. Contoh: 150000.00.</small></label>}
            <div className={styles.twoFields}><label>Mulai berlaku (WIB)<input required type="datetime-local" value={validFrom} onChange={event => setValidFrom(event.target.value)} /></label>
              <label>Berakhir (WIB)<input required type="datetime-local" value={validUntil} onChange={event => setValidUntil(event.target.value)} /></label></div>
            <label>Alasan pengajuan<textarea required rows={3} maxLength={1000} value={reason} onChange={event => setReason(event.target.value)} /></label>
            <button className={styles.secondary}>Periksa rincian pengajuan</button></fieldset>
          {proposalReview && <section className={styles.review} aria-label="Rincian pengajuan aturan"><h3 ref={heading} tabIndex={-1}>Rincian yang akan diajukan</h3>
            <p>{proposalReview.preview.view.code} · {proposalReview.preview.view.name}</p><RuleDetails value={proposalReview.preview.view} /><p className={styles.preserve}>{proposalReview.command.body.reason}</p>
            <label className={styles.checkbox}><input type="checkbox" checked={confirmed} disabled={commandLocked} onChange={event => setConfirmed(event.target.checked)} />Saya telah memeriksa unit, kebutuhan persetujuan dan masa berlaku aturan.</label>
            <div className={styles.actions}><button type="button" className={styles.primary} disabled={commandLocked || !confirmed} onClick={() => void submitProposal()}>Kirim pengajuan aturan</button>
              <button type="button" className={styles.secondary} disabled={commandLocked} onClick={() => { setProposalReview(null); setConfirmed(false); }}>Ubah rincian</button></div></section>}
        </form>}
        {proposal && <div className={styles.form}><h2>{status[proposal.state]}</h2><p>{proposal.code} · {proposal.name}</p><RuleDetails value={proposal} />
          <p className={styles.help}>Pengajuan {proposal.proposalId} · {when(proposal.proposedAt)} · Versi aturan dasar {proposal.baseVersion}</p>
          {proposal.state === 'approved' && <p role="status">Keputusan menghasilkan aturan versi {proposal.policyVersion}.</p>}
          {proposal.state === 'pending' && !proposal.bindingCurrent && <p className={styles.alert}>Sumber unit, aturan dasar atau masa berlaku berubah. Pengajuan tidak dapat disetujui; pemeriksa masih dapat mencatat penolakan.</p>}
          {proposal.state === 'pending' && proposalExpired && <p className={styles.alert}>Masa berlaku pengajuan telah berakhir. Pemeriksa dapat menolak pengajuan; ajukan masa berlaku baru jika masih diperlukan.</p>}
          {proposal.state === 'pending' && canDecide && <><p className={styles.help}>Keputusan memerlukan pemeriksa yang berbeda dari pengaju dan verifikasi kedua yang masih baru.</p>
            <label>Alasan keputusan<textarea required rows={3} maxLength={1000} disabled={commandLocked || decisionReview !== null} value={decisionReason} onChange={event => setDecisionReason(event.target.value)} /></label>
            {!decisionReview && <div className={styles.actions}><button className={styles.primary} disabled={commandLocked || !fresh || !decisionReason.trim() || !proposal.bindingCurrent || proposalExpired} onClick={() => void previewDecision('approve')}>Tinjau persetujuan aturan</button>
              <button className={styles.secondary} disabled={commandLocked || !fresh || !decisionReason.trim()} onClick={() => void previewDecision('reject')}>Tinjau penolakan aturan</button></div>}</>}
          {decisionReview && <section className={styles.review} aria-label="Rincian keputusan aturan"><h3 ref={heading} tabIndex={-1}>{decisionReview.command.decision === 'approve' ? 'Pastikan persetujuan aturan' : 'Pastikan penolakan aturan'}</h3>
            <RuleDetails value={decisionReview.preview.view} /><p className={styles.preserve}>{decisionReview.command.body.reason}</p>
            <label className={styles.checkbox}><input type="checkbox" checked={confirmed} disabled={commandLocked || !fresh} onChange={event => setConfirmed(event.target.checked)} />Saya telah memeriksa aturan dan siap mencatat keputusan ini.</label>
            <div className={styles.actions}><button className={styles.primary} disabled={commandLocked || !fresh || !confirmed || decisionReview.command.decision === 'approve' && proposalExpired} onClick={() => void saveDecision()}>{decisionReview.command.decision === 'approve' ? 'Setujui aturan' : 'Simpan penolakan aturan'}</button>
              <button className={styles.secondary} disabled={commandLocked} onClick={() => { setDecisionReview(null); setConfirmed(false); }}>Kembali ke pengajuan</button></div></section>}
        </div>}
      </section></div>}
  </>;
}
