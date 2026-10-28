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
import { createReceivingAccountGateway, prepareReceivingProposal, prepareReceivingObservation, prepareReceivingDecision,
  type ReceivingCommand, type ReceivingDetails, type ReceivingPolicy, type ReceivingSummary, type ReceivingView } from './receiving-account-client';
import common from '../administration/access.module.css';
import styles from './invoice.module.css';
import { dateTimeFormatter } from '@/lib/format';
import { SessionAccessState } from "@/components/workspace-kit/session-access-state";
import { Button } from '@/components/workspace-kit/button';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { Section } from '@/components/workspace-kit/section';
import { StateView } from '@/components/workspace-kit/state-view';
import { DataList } from '@/components/workspace-kit/data-list';
import { Field } from '@/components/workspace-kit/field';
import { Facts } from '@/components/workspace-kit/facts';
import { Badge } from '@/components/workspace-kit/badge';

type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
const at = (value: string) => dateTimeFormatter.format(new Date(value));
const blank = (): ReceivingDetails => ({ bankCode: '', accountNumber: '', holderName: '', currency: 'IDR' });
const stateName: Record<string, string> = { pending: 'Menunggu keputusan', approve: 'Disetujui', reject: 'Ditolak', withdraw: 'Ditarik' };
const actionName: Record<string, string> = { propose: 'Kirim usulan', observe: 'Simpan hasil pemeriksaan bank', approve: 'Setujui rekening', reject: 'Tolak usulan', withdraw: 'Tarik usulan' };
function message(error: unknown) {
  if (error instanceof IdentityFailure) return 'Masuk dengan akun organisasi Anda untuk melanjutkan.';
  if (error instanceof AccessFailure) {
    if (error.code === 'receiving_account_changed') return 'Usulan, rekening atau kebijakan pemeriksaan berubah atau kedaluwarsa. Muat data terbaru sebelum melanjutkan.';
    if (error.code === 'receiving_account_observation_mismatch') return 'Hasil pemeriksaan bank tidak sama dengan usulan. Periksa selisih dan ajukan usulan baru bila perlu.';
    if (error.code === 'invalid_receiving_account') return 'Isi rincian bank secara persis, sertakan alasan dan pastikan sumber pemeriksaan telah dipilih.';
    if (error.code === 'receiving_account_response_invalid' || error.code === 'receiving_account_integrity_unavailable') return 'Riwayat rekening belum dapat dipastikan lengkap. Jangan mengulang keputusan; hubungi penanggung jawab.';
  }
  return accessMessage(error);
}
export function ReceivingAccountWorkspace() {
  const [identity] = useState(createIdentityGateway), [access] = useState(createAccessGateway);
  const [account, setAccount] = useState<Account | null>(null), [error, setError] = useState<unknown>(null), [loading, setLoading] = useState(true), [refresh, setRefresh] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => { let active = true; const controller = new AbortController(); void Promise.resolve().then(async () => {
    if (!active) return; setLoading(true); setAccount(null); setError(null);
    try {
      const session = await identity.session(controller.signal), observedAt = performance.now();
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
      const current = await access.current(); if (!active) return;
      if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
      if (!current.allowedActions.includes('receiving.account.read')) throw new AccessFailure(403, 'scope_denied');
      if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
      setAccount({ actorId: current.userId, actions: current.allowedActions, session, observedAt }); setElapsed(performance.now() - observedAt);
    } catch (failure) { if (active) setError(failure); } finally { if (active) setLoading(false); }
  }); return () => { active = false; controller.abort(); }; }, [identity, access, refresh]);
  useEffect(() => { const timer = window.setInterval(() => { if (account) setElapsed(performance.now() - account.observedAt); }, 1000);
    const restored = (event: PageTransitionEvent) => { if (event.persisted) { setAccount(null); setRefresh(value => value + 1); } };
    window.addEventListener('pageshow', restored); return () => { window.clearInterval(timer); window.removeEventListener('pageshow', restored); }; }, [account]);
  const denied = useCallback((failure: unknown) => { if (failure instanceof AccessFailure && failure.code !== 'step_up_required' && (failure.status === 401 || failure.status === 403 || failure.code === 'actor_changed')) { setAccount(null); setError(failure); } }, []);
  return <main className={common.page}><PageHeader title="Rekening penerimaan" description="Ajukan rekening, periksa melalui sumber bank yang disetujui, lalu catat keputusan independen." /><div className="ui-stack">
    {error !== null && account !== null && <StateView kind="error" title={message(error)} />}
    {loading ? <StateView kind="loading" title="Memeriksa akun…" /> : !account || sessionTiming(account.session, elapsed).expired ? <SessionAccessState kind={account ? "expired" : undefined} error={error} returnPath="/billing/receiving-accounts" onRetry={() => setRefresh(value => value + 1)} />
      : <ReceivingDesk key={account.actorId + ':' + account.observedAt} account={account} fresh={sessionTiming(account.session, elapsed).fresh} onDenied={denied} />}
  </div></main>;
}
function Details({ value }: { value: ReceivingDetails }) {
  return <Facts items={[{ term: 'Bank', value: value.bankCode }, { term: 'Nomor rekening', value: value.accountNumber }, { term: 'Nama pemilik', value: value.holderName }, { term: 'Mata uang', value: value.currency }]} />;
}
function DetailFields({ value, onChange, disabled, prefix, fixedBank = false }: { value: ReceivingDetails; onChange: (value: ReceivingDetails) => void; disabled: boolean; prefix: string; fixedBank?: boolean }) {
  return <div className={styles.detailFields}>{(['bankCode', 'accountNumber', 'holderName'] as const).map(field => <Field key={field} id={prefix + '-' + field} label={field === 'bankCode' ? 'Kode bank' : field === 'accountNumber' ? 'Nomor rekening' : 'Nama pemilik rekening'}
    help={field === 'accountNumber' ? 'Salin seluruh angka termasuk nol di depan.' : undefined}><input required value={value[field]} autoComplete="off" disabled={disabled}
      readOnly={fixedBank && field === 'bankCode'} inputMode={field === 'accountNumber' ? 'numeric' : 'text'} maxLength={field === 'holderName' ? 160 : field === 'accountNumber' ? 34 : 32}
      onChange={event => onChange({ ...value, [field]: event.target.value })} /></Field>)}</div>;
}
function ReceivingDesk({ account, fresh, onDenied }: { account: Account; fresh: boolean; onDenied: (failure: unknown) => void }) {
  const [gateway] = useState(createReceivingAccountGateway), recovery = useOperationRecovery(account.actorId);
  const [page, setPage] = useState<{ items: ReceivingSummary[]; hasMore: boolean }>({ items: [], hasMore: false }), [policies, setPolicies] = useState<{ items: ReceivingPolicy[]; hasMore: boolean }>({ items: [], hasMore: false });
  const [view, setView] = useState<ReceivingView | null>(null), [draft, setDraft] = useState<{ accountId: string | null; baseVersion: string; scope: ReceivingPolicy['scope'] | null } | null>(null);
  const [policyId, setPolicyId] = useState(''), [values, setValues] = useState(blank), [observed, setObserved] = useState(blank), [source, setSource] = useState(''), [attested, setAttested] = useState(false);
  const [reason, setReason] = useState(''), [observationReason, setObservationReason] = useState(''), [decisionReason, setDecisionReason] = useState('');
  const [confirmation, setConfirmation] = useState<ReceivingCommand | null>(null), [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [offline, setOffline] = useState(false), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState('');
  const alive = useRef(false), pending = useRef(false), title = useRef<HTMLHeadingElement>(null), confirmTitle = useRef<HTMLHeadingElement>(null);
  // A failed first read (not a failed command) replaces the list with one error body (U-036).
  const [readFailed, setReadFailed] = useState(false);
  const current = useCallback(() => alive.current && navigator.onLine && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const allowed = (permission: string) => account.actions.includes('receiving.account.' + permission);
  const full = allowed('details.read'), locked = busy || recovering || offline, commandLocked = locked || recovery.locked;
  useEffect(() => { alive.current = true; const connection = () => setOffline(!navigator.onLine); connection(); window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => { alive.current = false; window.removeEventListener('online', connection); window.removeEventListener('offline', connection); }; }, []);
  useEffect(() => { let active = true; void Promise.resolve().then(async () => {
    if (!active || !current()) return; setBusy(true);
    try { const result = await gateway.list(account.actorId); if (active && current()) setPage(result); }
    catch (failure) { if (active && current()) { setError(failure); setReadFailed(true); onDenied(failure); } } finally { if (active) setBusy(false); }
  }); return () => { active = false; }; }, [gateway, account.actorId, current, onDenied]);
  useEffect(() => { if (view) title.current?.focus(); }, [view]);
  useEffect(() => { if (confirmation) confirmTitle.current?.focus(); }, [confirmation]);
  async function work(action: () => Promise<void>) {
    if (!current() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); setNotice('');
    try { await action(); } catch (failure) { if (alive.current) { setError(failure); onDenied(failure); } }
    finally { pending.current = false; recovery.refresh(); if (alive.current) setBusy(false); }
  }
  async function loadPage(afterId: string | null = null) {
    setView(null); setDraft(null); setConfirmation(null); const result = await gateway.list(account.actorId, afterId); if (current()) { setPage(result); setReadFailed(false); }
  }
  async function open(id: string) {
    setView(null); setDraft(null); setConfirmation(null); setObserved(blank()); setSource(''); setAttested(false); setObservationReason(''); setDecisionReason('');
    const result = await gateway.read(account.actorId, id); if (current()) setView(result);
  }
  async function startDraft(change: boolean) {
    const next = change && view ? { accountId: view.proposal.accountId, baseVersion: view.currentVersion?.version ?? '0', scope: view.proposal.policy.scope } : { accountId: null, baseVersion: '0', scope: null };
    setDraft(null); setConfirmation(null); setValues(blank()); setReason(''); setPolicyId('');
    const result = await gateway.policies(account.actorId); if (current()) { setPolicies(result); setDraft(next); setView(null); }
  }
  function preview(create: () => ReceivingCommand) { if (commandLocked || !current()) return; try { setError(null); setConfirmation(create()); } catch (failure) { setError(failure); } }
  const policyOptions = policies.items.filter(p => !draft?.scope || JSON.stringify(p.scope) === JSON.stringify(draft.scope));
  const chosen = policyOptions.find(p => p.id === policyId);
  const pendingView = view && !view.decision, same = view?.observation && JSON.stringify(view.proposal.details) === JSON.stringify(view.observation.details);
  const stale = view && view.proposal.baseVersion !== (view.currentVersion?.version ?? '0');
  const own = view?.proposal.actorId === account.actorId;
  const reload = () => void work(() => loadPage());
  return <><AdministrationNavigation current="receiving-accounts" actions={account.actions} locked={busy || recovering} />
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy || offline} onBusyChange={setRecovering}
      recoveryAdapters={{ 'billing.receiving-account': { lookup: async entry => { const result = await gateway.recover(entry.actorId, entry.key, entry.operation); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return result; } } }}
      onFailure={failure => { setError(failure); onDenied(failure); }} onResolved={() => { recovery.refresh(); setConfirmation(null); setDraft(null); setView(null); setNotice('Hasil permintaan telah dipastikan. Muat daftar terbaru.'); }} />
    {recovery.error !== null && <StateView kind="error" title={accessMessage(recovery.error)} />}
    {offline && <StateView kind="offline" title="Koneksi terputus. Isian belum terkirim tetap di halaman ini; sambungkan dan muat data terbaru sebelum melanjutkan." />}
    {error !== null && !readFailed && <StateView kind="error" title={message(error)} />}{notice && <p role="status">{notice}</p>}{busy && <p role="status">Memproses rekening…</p>}
    {(!fresh || error instanceof AccessFailure && error.code === 'step_up_required') && <section className={styles.notice} aria-label="Verifikasi dua langkah"><p>Verifikasi dua langkah terbaru diperlukan untuk pemeriksaan bank dan keputusan independen.</p>
      {(['verify', 'approve'] as const).filter(allowed).map(permission => <p key={permission}><Link href={'/identity/account?operation=receiving.account.' + permission + '&returnPath=%2Fbilling%2Freceiving-accounts'}>Verifikasi untuk {permission === 'verify' ? 'pemeriksaan bank' : 'persetujuan rekening'}</Link></p>)}</section>}
    <Section title="Usulan dan riwayat rekening" label="Daftar usulan rekening" description="Nomor pada daftar disamarkan. Rincian lengkap memerlukan izin tersendiri."
      actions={<><Button variant="secondary" disabled={locked} onClick={reload}>Muat daftar terbaru</Button>
        {full && allowed('propose') && <Button variant="primary" disabled={commandLocked} onClick={() => void work(() => startDraft(false))}>Ajukan rekening baru</Button>}</>}>
      {readFailed ? <StateView kind="error" title={message(error)} action={<Button disabled={locked} onClick={reload}>Coba lagi</Button>} />
        : <DataList label="Usulan dan riwayat rekening" items={page.items} getKey={item => item.proposalId} loading={busy && !page.items.length} empty={{ title: 'Belum ada usulan dalam cakupan akses Anda.' }}
          renderTitle={item => <>{item.bankCode} · {item.maskedAccount}</>} renderBadge={item => <Badge status={stateName[item.state]} />}
          renderMeta={item => <>Rekening {item.accountId} · Versi aktif {item.currentVersion} · Usulan {item.proposalId} oleh akun {item.actorId} · {at(item.recordedAt)}</>}
          renderActions={full ? item => <Button variant="secondary" disabled={locked} onClick={() => void work(() => open(item.proposalId))}>Periksa usulan {item.proposalId}</Button> : undefined} />}
      {page.hasMore && <Button variant="secondary" disabled={locked} onClick={() => void work(() => loadPage(page.items.at(-1)!.proposalId))}>Usulan berikutnya</Button>}
    </Section>
    {draft && <Section title={draft.accountId ? 'Ajukan perubahan rekening ' + draft.accountId : 'Ajukan rekening penerimaan'} label="Usulan rekening baru"
      description="Persetujuan menggunakan kanal pemeriksaan yang sudah ditetapkan Finance. Kontak yang diberikan pengusul tidak menetapkan sumber tepercaya.">
      <form className="ui-form" onSubmit={event => { event.preventDefault(); if (chosen) preview(() => prepareReceivingProposal(account.actorId, chosen, values, reason.trim(), draft.accountId, draft.baseVersion)); }}>
        <Field id="receiving-policy" label="Kebijakan pemeriksaan bank"><select required value={policyId} disabled={commandLocked || !!confirmation} onChange={event => { setPolicyId(event.target.value); setValues({ ...values, bankCode: policyOptions.find(p => p.id === event.target.value)?.bankCode ?? '' }); }}>
          <option value="">Pilih kebijakan yang disetujui</option>{policyOptions.map(p => <option key={p.id} value={p.id} disabled={!p.active}>{p.bankName} · Toko {p.scope.shopId} · Kanal {p.scope.channelId} · Kebijakan {p.id}/{p.revision}{!p.active ? ' (nonaktif)' : ''}</option>)}</select></Field>
        {policyOptions.length === 0 && <p role="status">Belum ada kebijakan pemeriksaan untuk cakupan ini. Finance perlu menetapkan kanal bank, penanggung jawab dan persetujuan operasional sebelum rekening dapat diajukan.</p>}
        {policies.hasMore && <Button variant="secondary" type="button" disabled={locked} onClick={() => void work(async () => { const more = await gateway.policies(account.actorId, policies.items.at(-1)!.id); if (current()) setPolicies({ items: [...policies.items, ...more.items], hasMore: more.hasMore }); })}>Muat kebijakan lainnya</Button>}
        {chosen && <Policy value={chosen} />}
        <DetailFields prefix="proposed" value={values} onChange={setValues} disabled={commandLocked || !!confirmation} fixedBank />
        <Field id="proposal-reason" label="Alasan usulan"><textarea required maxLength={1000} value={reason} disabled={commandLocked || !!confirmation} onChange={event => setReason(event.target.value)} /></Field>
        <Button variant="primary" type="submit" disabled={commandLocked || !!confirmation || !chosen}>Periksa usulan sebelum dikirim</Button>
      </form>
    </Section>}
    {view && <section className="ui-section" aria-label="Rincian usulan rekening"><h2 ref={title} tabIndex={-1}>Usulan rekening {view.proposal.id}</h2>
      <p>{stateName[view.decision?.decision ?? 'pending']} · Diajukan akun {view.proposal.actorId} pada {at(view.proposal.recordedAt)}</p><p>Alasan: {view.proposal.reason}</p><Policy value={view.proposal.policy} />
      <div className={styles.compare}><section><h3>Rincian yang diusulkan</h3><Details value={view.proposal.details} /></section><section><h3>Hasil pemeriksaan bank</h3>
        {view.observation ? <><Details value={view.observation.details} /><p>Petugas {view.observation.actorId} · {at(view.observation.checkedAt)}</p><p>Referensi: {view.observation.sourceReference}</p><p>{view.observation.reason}</p></> : <p>Belum ada pemeriksaan melalui kanal bank yang disetujui.</p>}</section></div>
      {view.observation && <StateView kind={same ? 'success' : 'error'} title={same ? 'Rincian pengamatan sama persis dengan usulan.' : 'Ada perbedaan rincian. Persetujuan belum dapat dilakukan; tolak atau tarik usulan untuk membuat koreksi baru.'} />}
      {view.currentVersion && <section><h3>Rekening yang saat ini disetujui · Versi {view.currentVersion.version}</h3><Details value={view.currentVersion.details} /><p>Berlaku sejak {at(view.currentVersion.effectiveAt)}. Transaksi terdahulu tetap merujuk versi yang sebelumnya digunakan.</p></section>}
      {view.decision && <p>Keputusan akun {view.decision.actorId} pada {at(view.decision.recordedAt)}: {view.decision.reason}</p>}
      {stale && pendingView && <StateView kind="unknown" title="Rekening aktif telah berubah sejak usulan ini dibuat. Usulan dapat ditolak atau ditarik; persetujuan memerlukan usulan baru." />}
      {full && allowed('propose') && <Button variant="secondary" disabled={commandLocked} onClick={() => void work(() => startDraft(true))}>Ajukan perubahan rekening ini</Button>}
      {pendingView && !own && allowed('verify') && !view.observation && !stale && <form className="ui-form" aria-label="Pemeriksaan melalui bank" onSubmit={event => { event.preventDefault(); preview(() => prepareReceivingObservation(account.actorId, view, observed, source.trim(), observationReason.trim(), attested)); }}>
        <h3>Catat hasil pemeriksaan melalui bank</h3><p>Gunakan kanal pada kebijakan di atas. Masukkan apa yang Anda amati dari bank; isian ini tidak diisi dari usulan.</p>
        <DetailFields prefix="observed" value={observed} onChange={setObserved} disabled={commandLocked || !fresh || !!confirmation} />
        <Field id="bank-source" label="Referensi konfirmasi atau catatan bank"><input required maxLength={256} autoComplete="off" value={source} disabled={commandLocked || !fresh || !!confirmation} onChange={event => setSource(event.target.value)} /></Field>
        <Field id="observation-reason" label="Catatan pemeriksaan"><textarea required maxLength={1000} value={observationReason} disabled={commandLocked || !fresh || !!confirmation} onChange={event => setObservationReason(event.target.value)} /></Field>
        <label className={common.checkbox}><input type="checkbox" checked={attested} required disabled={commandLocked || !fresh || !!confirmation} onChange={event => setAttested(event.target.checked)} />Saya memeriksa langsung sumber bank melalui kanal yang disetujui dan mencatat hasilnya secara independen.</label>
        <Button variant="primary" type="submit" disabled={commandLocked || !fresh || !!confirmation}>Periksa hasil sebelum disimpan</Button>
      </form>}
      {pendingView && (own && allowed('propose') || !own && allowed('approve')) && <section className="ui-form" aria-label="Keputusan usulan"><h3>Keputusan</h3>
        <Field id="decision-reason" label="Alasan keputusan"><textarea maxLength={1000} value={decisionReason} disabled={commandLocked || !!confirmation} onChange={event => setDecisionReason(event.target.value)} /></Field>
        {own ? <Button variant="secondary" disabled={commandLocked || !!confirmation || !decisionReason.trim()} onClick={() => preview(() => prepareReceivingDecision(account.actorId, view, 'withdraw', decisionReason.trim()))}>Tarik usulan</Button> : <div className="ui-actions">
          <Button variant="primary" disabled={commandLocked || !fresh || !!confirmation || !same || !!stale || !decisionReason.trim()} onClick={() => preview(() => prepareReceivingDecision(account.actorId, view, 'approve', decisionReason.trim()))} busy={busy}>Setujui rekening</Button>
          <Button variant="secondary" disabled={commandLocked || !fresh || !!confirmation || !decisionReason.trim()} onClick={() => preview(() => prepareReceivingDecision(account.actorId, view, 'reject', decisionReason.trim()))}>Tolak usulan</Button></div>}
      </section>}
    </section>}
    {confirmation && <section className="ui-section" aria-label="Konfirmasi tindakan rekening"><h2 ref={confirmTitle} tabIndex={-1}>{actionName[confirmation.action]}</h2>
      {'details' in confirmation.body && <Details value={confirmation.body.details} />}<p>Alasan: {confirmation.body.reason}</p><p>Tindakan dicatat dengan akun dan waktu. Persetujuan rekening tidak mencatat dana masuk atau menjalankan transfer.</p>
      <div className="ui-actions"><Button variant="primary" disabled={commandLocked || (confirmation.action === 'observe' || confirmation.action === 'approve' || confirmation.action === 'reject') && !fresh} onClick={() => void work(async () => {
        if (recovery.journal.list(account.actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
        const command = confirmation, operation = 'billing.receiving-account.' + command.action;
        const ledger = new MutationLedger(() => 'ra.' + command.action + '.' + command.owner + '.' + crypto.randomUUID());
        const result = await submitTrackedRequest(ledger, recovery.journal, account.actorId, operation, operation, command, key => gateway.send(command, key, current));
        if (current()) { await open(result.proposalId); setNotice('Tindakan tersimpan. Periksa riwayat rekening terbaru.'); }
      })} busy={busy}>Konfirmasi dan simpan</Button><Button variant="secondary" disabled={locked} onClick={() => setConfirmation(null)}>Kembali ke isian</Button></div>
    </section>}
  </>;
}
function Policy({ value }: { value: ReceivingPolicy }) {
  return <section className={styles.policy} aria-label="Kanal pemeriksaan yang disetujui"><h3>{value.bankName} · Kebijakan {value.id}/{value.revision}</h3>
    <p>{value.method === 'bank-contact' ? 'Konfirmasi melalui kontak bank yang telah dikenal' : 'Pemeriksaan portal atau catatan bank terautentikasi'}</p>
    <p>Kanal: {value.trustedChannelReference}</p><p>Penanggung jawab: {value.operatingOwnerReference}</p><p>Dasar persetujuan: {value.approvalReference}</p>
    <p>Berlaku {at(value.approvedAt)} sampai {at(value.expiresAt)}. {value.active ? 'Aktif pada versi kebijakan ini.' : 'Kebijakan tidak aktif.'}</p>
  </section>;
}
