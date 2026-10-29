'use client';

import Link from 'next/link';
import { useCallback, useEffect, useRef, useState } from 'react';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure, MutationLedger } from '../administration/workflow';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import { submitTrackedRequest } from '../administration/operation-journal';
import { createBranchRequestGateway, type BranchRequest, type BranchRequestDecisionPreview, type BranchRequestPreview } from './branch-request-client';
import { branchRequestNumber, requestMessage, requestStateLabel, type BranchRequestAction, type BranchRequestOperation, type BranchRequestResult } from './branch-request-state';
import { BranchRequestComposer } from './BranchRequestComposer';
import { BranchRequestFacts, requestDate } from './BranchRequestFacts';
import { buttonClassName } from '@/components/workspace-kit/button';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { StateView } from '@/components/workspace-kit/state-view';
import { Badge } from '@/components/workspace-kit/badge';
import { Tabs } from '@/components/workspace-kit/tabs';
import { Button } from '@/components/workspace-kit/button';
import { Section } from '@/components/workspace-kit/section';
import { DataList } from '@/components/workspace-kit/data-list';
import { Field } from '@/components/workspace-kit/field';
import common from '../administration/access.module.css';
import styles from './branch-request.module.css';
import { SessionAccessState } from "@/components/workspace-kit/session-access-state";

type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
export function BranchRequestWorkspace({ incoming = false }: { incoming?: boolean }) {
  const [identity] = useState(() => createIdentityGateway()), [access] = useState(() => createAccessGateway());
  const [account, setAccount] = useState<Account | null>(null), [error, setError] = useState<unknown>(null), [loading, setLoading] = useState(true), [revision, setRevision] = useState(0), [elapsed, setElapsed] = useState(0);
  const path = incoming ? '/ordering/requests/incoming' : '/ordering/requests';
  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      setLoading(true); setAccount(null); setError(null);
      try {
        const session = await identity.session(controller.signal), observedAt = performance.now(); if (controller.signal.aborted) return;
        if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
        const scope = await access.current(); if (controller.signal.aborted) return;
        if (scope.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
        if (incoming ? !scope.allowedActions.includes('order.request.decide') : !scope.allowedActions.some(action => ['order.request.submit', 'order.request.read'].includes(action))) throw new AccessFailure(403, 'scope_denied');
        if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
        setAccount({ actorId: scope.userId, actions: scope.allowedActions, session, observedAt }); setElapsed(performance.now() - observedAt);
      } catch (failure) { if (!controller.signal.aborted) setError(failure); }
      finally { if (!controller.signal.aborted) setLoading(false); }
    })();
    return () => controller.abort();
  }, [identity, access, revision, incoming]);
  useEffect(() => {
    const timer = window.setInterval(() => { if (account) setElapsed(performance.now() - account.observedAt); }, 1000);
    const restored = (event: PageTransitionEvent) => { if (event.persisted) { setAccount(null); setRevision(value => value + 1); } };
    window.addEventListener('pageshow', restored); return () => { window.clearInterval(timer); window.removeEventListener('pageshow', restored); };
  }, [account]);
  const failed = useCallback((failure: unknown) => {
    if (failure instanceof AccessFailure && (failure.status === 401 || failure.code === 'actor_changed')) { setAccount(null); setError(failure); }
  }, []);
  const expired = account !== null && sessionTiming(account.session, elapsed).expired;
  const canDecide = !loading && account !== null && !expired && !incoming && account.actions.includes('order.request.decide');
  return <main className={common.page}><PageHeader title={incoming ? 'Permintaan masuk' : 'Permintaan kebutuhan cabang'} description={incoming ? 'Periksa kebutuhan cabang dan berikan hasil peninjauan sesuai penugasan toko Anda.' : 'Susun pilihan kemasan, ajukan ke toko, dan ikuti hasil peninjauannya.'}
    secondaryAction={<nav className="ui-actions" aria-label="Navigasi permintaan"><Link className={buttonClassName({ variant: 'secondary' })} href="/shop">Katalog kemasan</Link>
      {canDecide && <Link className={buttonClassName({ variant: 'secondary' })} href="/ordering/requests/incoming">Permintaan masuk</Link>}</nav>} />
    {error !== null && account !== null && <StateView kind="error" title={requestMessage(error)} />}
    {loading ? <StateView kind="loading" title="Memeriksa akun dan akses…" /> : !account || expired ? <SessionAccessState kind={account ? "expired" : undefined} error={error} returnPath={path} onRetry={() => setRevision(value => value + 1)} />
      : <RequestDesk key={account.actorId + ':' + account.observedAt + ':' + incoming} account={account} incoming={incoming} onFailure={failed} />}
  </main>;
}

function RequestDesk({ account, incoming, onFailure }: { account: Account; incoming: boolean; onFailure: (failure: unknown) => void }) {
  const [gateway] = useState(() => createBranchRequestGateway()), [ledger] = useState(() => new MutationLedger());
  const canSubmit = !incoming && account.actions.includes('order.request.submit'), canRead = account.actions.includes('order.request.read');
  const [tab, setTab] = useState<'new' | 'history'>(canSubmit ? 'new' : 'history');
  const [page, setPage] = useState<{ items: BranchRequest[]; nextRequestId: string | null }>({ items: [], nextRequestId: null });
  const [cursor, setCursor] = useState<string | null>(null), [reload, setReload] = useState(0), [selected, setSelected] = useState<BranchRequest | null>(null);
  const [preview, setPreview] = useState<BranchRequestDecisionPreview | null>(null), [reason, setReason] = useState(''), [confirmed, setConfirmed] = useState<BranchRequestResult | null>(null);
  const [busy, setBusy] = useState(false), [acting, setActing] = useState<string | null>(null), [recovering, setRecovering] = useState(false), [error, setError] = useState<{ value: unknown; writing: boolean } | null>(null);
  const recovery = useOperationRecovery(account.actorId), alive = useRef(false), pending = useRef(false), recovered = useRef<BranchRequestResult | null>(null), detail = useRef<HTMLElement>(null);
  const current = useCallback(() => alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const locked = busy || recovering || recovery.locked;
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  useEffect(() => { if (selected || preview || confirmed) detail.current?.focus(); }, [selected, preview, confirmed]);
  useEffect(() => {
    const controller = new AbortController();
    if (tab !== 'history' || !canRead) return () => controller.abort();
    void (async () => {
      setBusy(true); setPage({ items: [], nextRequestId: null }); setSelected(null); setPreview(null); setError(null);
      try { const result = await gateway.list(account.actorId, incoming ? 'incoming' : 'mine', cursor, controller.signal); if (!controller.signal.aborted && current()) setPage(result); }
      catch (failure) { if (!controller.signal.aborted) { setError({ value: failure, writing: false }); onFailure(failure); } }
      finally { if (!controller.signal.aborted) { setBusy(false); setActing(null); } }
    })(); return () => controller.abort();
  }, [gateway, account.actorId, incoming, cursor, reload, tab, canRead, current, onFailure]);
  async function select(id: string) {
    if (!current() || pending.current || locked) return; pending.current = true; setBusy(true); setActing('open:' + id); setError(null); setSelected(null); setPreview(null); setReason('');
    try { const result = await gateway.read(account.actorId, id); if (current()) setSelected(result); }
    catch (failure) { if (current()) { setError({ value: failure, writing: false }); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); setActing(null); } }
  }
  async function readResult(result: BranchRequestResult) {
    if (!current()) return; setConfirmed(result); setPreview(null); setSelected(null);
    if (canRead) {
      try { const value = await gateway.read(account.actorId, result.requestId); if (current()) { setSelected(value); setPage(previous => ({ ...previous, items: previous.items.map(item => item.id === value.id ? value : item) })); } }
      catch (failure) { if (current()) { setError({ value: failure, writing: false }); onFailure(failure); } }
    }
  }
  async function submit(value: BranchRequestPreview) {
    if (!current() || locked || pending.current) return;
    pending.current = true; setBusy(true); setError(null); setConfirmed(null);
    try {
      if (recovery.journal.list(account.actorId).length > 0) throw new AccessFailure(400, 'resolve_previous_attempt');
      const result = await submitTrackedRequest(ledger, recovery.journal, account.actorId, 'ordering.branchrequest.submit', 'ordering.branchrequest.submit', { actorId: account.actorId, input: value.input, binding: value.binding }, key => gateway.submit(account.actorId, value, key, current));
      await readResult(result);
    } catch (failure) { if (current()) { setError({ value: failure, writing: true }); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); recovery.refresh(); } }
  }
  async function prepareDecision(action: BranchRequestAction) {
    if (!selected || !current() || locked || pending.current) return; pending.current = true; setBusy(true); setActing('decision:' + action); setError(null); setPreview(null);
    try { const value = await gateway.previewDecision(account.actorId, selected.id, action); if (current()) { setSelected(value.request); setPreview(value); } }
    catch (failure) { if (current()) { setError({ value: failure, writing: false }); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); setActing(null); } }
  }
  async function decide() {
    if (!preview || !current() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); setConfirmed(null);
    try {
      if (recovery.journal.list(account.actorId).length > 0) throw new AccessFailure(400, 'resolve_previous_attempt');
      const result = await submitTrackedRequest(ledger, recovery.journal, account.actorId, 'ordering.branchrequest.decide', 'ordering.branchrequest.decide',
        { actorId: account.actorId, requestId: preview.request.id, version: preview.request.version, binding: preview.binding, decision: preview.decision, reason: reason.trim() }, key => gateway.decide(account.actorId, preview, reason.trim(), key, current));
      await readResult(result);
    } catch (failure) { if (current()) { setPreview(null); setError({ value: failure, writing: true }); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); recovery.refresh(); } }
  }
  const views = [...canSubmit ? [{ label: 'Buat permintaan', value: 'new' }] : [], ...canRead ? [{ label: 'Riwayat permintaan', value: 'history' }] : []];
  function show(value: string) {
    if (locked) return;
    if (value === 'new') { setTab('new'); setSelected(null); setConfirmed(null); setPreview(null); setError(null); }
    else { setTab('history'); setCursor(null); setReload(current => current + 1); setConfirmed(null); }
  }
  const stateTone = (state: BranchRequest['state']) => state === 'accepted' ? 'success' as const : state === 'rejected' ? 'danger' as const : 'neutral' as const;
  return <>
    {views.length > 1 && <div className={styles.tabs}><Tabs label="Bagian permintaan" items={views} current={tab} disabled={locked} onSelect={show} /></div>}
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy} onBusyChange={setRecovering} recoveryAdapters={{ 'ordering.branchrequest': { lookup: async entry => { const result = await gateway.recover(entry.actorId, entry.operation as BranchRequestOperation, entry.key); recovered.current = result?.result ?? null; return result; } } }}
      onFailure={failure => { setError({ value: failure, writing: false }); onFailure(failure); }} onResolved={() => { recovery.refresh(); ledger.resetAfterReconciliation(); setError(null); const result = recovered.current; recovered.current = null; if (result && current()) { setBusy(true); void readResult(result).finally(() => { if (alive.current) setBusy(false); }); } }} />
    {recovery.error !== null && <StateView kind="error" title={requestMessage(recovery.error)} />}
    {error && <StateView kind="error" title={requestMessage(error.value, error.writing)} />}
    {confirmed && <section ref={detail} tabIndex={-1} className={styles.recorded} aria-label="Hasil permintaan"><h2>{branchRequestNumber(confirmed.requestId)} tercatat</h2><p role="status">{requestStateLabel(confirmed.state)}</p><p>{confirmed.state === 'pending' ? 'Permintaan telah tersimpan dan menunggu peninjauan toko.' : confirmed.state === 'accepted' ? 'Toko telah menerima permintaan untuk ditindaklanjuti.' : 'Penolakan toko telah tersimpan.'}</p></section>}
    {tab === 'new' && canSubmit && !confirmed && <BranchRequestComposer actorId={account.actorId} locked={locked} current={current} onSubmit={submit} onFailure={onFailure} />}
    {tab === 'history' && canRead && <Section title={incoming ? 'Kebutuhan yang ditujukan ke toko Anda' : 'Permintaan yang Anda ajukan'} label={incoming ? 'Daftar permintaan masuk' : 'Daftar permintaan saya'} busy={busy}
      actions={<Button disabled={locked} onClick={() => { setActing('refresh'); setCursor(null); setReload(value => value + 1); }} busy={acting === 'refresh'}>Muat permintaan terbaru</Button>}>
      <DataList label={incoming ? 'Permintaan masuk' : 'Permintaan saya'} items={page.items} getKey={item => item.id} loading={busy}
        empty={error ? undefined : { title: 'Belum ada permintaan pada halaman ini.' }}
        renderTitle={item => branchRequestNumber(item.id)} renderBadge={item => <Badge tone={stateTone(item.state)} label={requestStateLabel(item.state)} />}
        renderMeta={item => <>{item.labels.branch} → {item.labels.shop} · {item.lines.length} jenis kemasan<br />{requestDate(item.recordedAt)}</>}
        renderActions={item => <Button disabled={locked} busy={acting === 'open:' + item.id} onClick={() => void select(item.id)}>Lihat {branchRequestNumber(item.id)}</Button>} />
      <div className="ui-actions"><Button disabled={locked || cursor === null} onClick={() => setCursor(null)}>Awal daftar</Button><Button disabled={locked || page.nextRequestId === null} onClick={() => setCursor(page.nextRequestId)}>Permintaan lebih lama</Button></div>
    </Section>}
    {incoming && !canRead && <Section title="Pemeriksaan hasil tindakan Anda"><p>Akun ini dapat memeriksa hasil keputusan miliknya. Akses membaca daftar permintaan belum termasuk dalam penugasan Anda.</p></Section>}
    {selected && <section ref={confirmed ? undefined : detail} tabIndex={-1} className={`${styles.section} ${styles.details}`} aria-label="Rincian permintaan"><BranchRequestFacts value={selected} />
      {incoming && selected.state === 'pending' && selected.createdBy !== account.actorId && <><h3>Peninjauan toko</h3><Field id="request-review-note" label="Catatan peninjauan"><input value={reason} maxLength={1000} disabled={locked || preview !== null} onChange={event => setReason(event.target.value)} /></Field>
        {preview ? <div className={styles.confirm}><h3>{preview.decision === 'accept' ? 'Konfirmasi penerimaan permintaan' : 'Konfirmasi penolakan permintaan'}</h3><p>{reason.trim()}</p><p>{preview.decision === 'accept' ? 'Permintaan diterima untuk ditindaklanjuti toko. Konfirmasikan pengaturan pemenuhan kepada pemesan melalui prosedur kerja yang berlaku.' : 'Alasan penolakan akan tampil pada riwayat pemesan.'}</p><div className="ui-actions"><Button variant={preview.decision === 'reject' ? 'danger-confirm' : 'primary'} disabled={locked || !preview.canDecide} onClick={() => void decide()}>{preview.decision === 'accept' ? 'Konfirmasi terima permintaan' : 'Konfirmasi tolak permintaan'}</Button><Button disabled={locked} onClick={() => setPreview(null)}>Kembali meninjau</Button></div></div>
          : <div className="ui-actions"><Button variant="primary" disabled={locked || reason.trim().length === 0} busy={acting === 'decision:accept'} onClick={() => void prepareDecision('accept')}>Terima permintaan</Button><Button variant="danger" disabled={locked || reason.trim().length === 0} busy={acting === 'decision:reject'} onClick={() => void prepareDecision('reject')}>Tolak permintaan</Button></div>}
      </>}
      {incoming && selected.state === 'pending' && selected.createdBy === account.actorId && <p>Permintaan Anda perlu ditinjau oleh petugas lain yang berwenang.</p>}
      <div className="ui-actions"><Button disabled={locked} onClick={() => void select(selected.id)} busy={acting === 'open:' + selected.id}>Perbarui rincian</Button></div>
    </section>}
  </>;
}
