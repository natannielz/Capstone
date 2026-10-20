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
import { requestMessage, requestStateLabel, type BranchRequestAction, type BranchRequestOperation, type BranchRequestResult } from './branch-request-state';
import { BranchRequestComposer } from './BranchRequestComposer';
import { BranchRequestFacts, requestDate } from './BranchRequestFacts';
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
  return <main className={common.page}><header className={common.header}><div className={styles.intro}><Link className={common.brand} href="/identity/account">Unit Toko</Link><h1>{incoming ? 'Permintaan masuk' : 'Permintaan kebutuhan cabang'}</h1><p>{incoming ? 'Periksa kebutuhan cabang dan berikan hasil peninjauan sesuai penugasan toko Anda.' : 'Susun pilihan kemasan, ajukan ke toko, dan ikuti hasil peninjauannya.'}</p></div><nav className={common.actions} aria-label="Navigasi permintaan"><Link className={common.secondary} href="/shop">Katalog kemasan</Link><Link className={common.secondary} href="/identity/account">Akun saya</Link></nav></header>
    {error !== null && account !== null && <p role="alert" className={common.alert}>{requestMessage(error)}</p>}
    {loading ? <p role="status">Memeriksa akun dan akses…</p> : !account || expired ? <SessionAccessState error={error} returnPath={path} onRetry={() => setRevision(value => value + 1)} />
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
  const [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [error, setError] = useState<{ value: unknown; writing: boolean } | null>(null);
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
      finally { if (!controller.signal.aborted) setBusy(false); }
    })(); return () => controller.abort();
  }, [gateway, account.actorId, incoming, cursor, reload, tab, canRead, current, onFailure]);
  async function select(id: string) {
    if (!current() || pending.current || locked) return; pending.current = true; setBusy(true); setError(null); setSelected(null); setPreview(null); setReason('');
    try { const result = await gateway.read(account.actorId, id); if (current()) setSelected(result); }
    catch (failure) { if (current()) { setError({ value: failure, writing: false }); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) setBusy(false); }
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
    if (!selected || !current() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); setPreview(null);
    try { const value = await gateway.previewDecision(account.actorId, selected.id, action); if (current()) { setSelected(value.request); setPreview(value); } }
    catch (failure) { if (current()) { setError({ value: failure, writing: false }); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) setBusy(false); }
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
  return <>
    <nav className={styles.tabs} aria-label="Bagian permintaan">{canSubmit && <button className={tab === 'new' ? common.primary : common.secondary} disabled={locked} onClick={() => { setTab('new'); setSelected(null); setConfirmed(null); setPreview(null); setError(null); }}>Buat permintaan</button>}
      {canRead && <button className={tab === 'history' ? common.primary : common.secondary} disabled={locked} onClick={() => { setTab('history'); setCursor(null); setReload(value => value + 1); setConfirmed(null); }}>Riwayat permintaan</button>}
      {!incoming && account.actions.includes('order.request.decide') && <Link className={common.secondary} href="/ordering/requests/incoming">Permintaan masuk</Link>}
    </nav>
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy} onBusyChange={setRecovering} recoveryAdapters={{ 'ordering.branchrequest': { lookup: async entry => { const result = await gateway.recover(entry.actorId, entry.operation as BranchRequestOperation, entry.key); recovered.current = result?.result ?? null; return result; } } }}
      onFailure={failure => { setError({ value: failure, writing: false }); onFailure(failure); }} onResolved={() => { recovery.refresh(); ledger.resetAfterReconciliation(); setError(null); const result = recovered.current; recovered.current = null; if (result && current()) { setBusy(true); void readResult(result).finally(() => { if (alive.current) setBusy(false); }); } }} />
    {recovery.error !== null && <p className={common.alert} role="alert">{requestMessage(recovery.error)}</p>}
    {error && <p className={common.alert} role="alert">{requestMessage(error.value, error.writing)}</p>}
    {confirmed && <section ref={detail} tabIndex={-1} className={styles.review} aria-label="Hasil permintaan"><h2>{confirmed.number} tercatat</h2><p role="status">{requestStateLabel(confirmed.state)}</p><p>{confirmed.state === 'pending' ? 'Permintaan telah tersimpan dan menunggu peninjauan toko.' : confirmed.state === 'accepted' ? 'Toko telah menerima permintaan untuk ditindaklanjuti.' : 'Penolakan toko telah tersimpan.'}</p></section>}
    {tab === 'new' && canSubmit && !confirmed && <BranchRequestComposer actorId={account.actorId} locked={locked} current={current} onSubmit={submit} onFailure={onFailure} />}
    {tab === 'history' && canRead && <section className={styles.section} aria-label={incoming ? 'Daftar permintaan masuk' : 'Daftar permintaan saya'} aria-busy={busy}><div className={styles.row}><h2>{incoming ? 'Kebutuhan yang ditujukan ke toko Anda' : 'Permintaan yang Anda ajukan'}</h2><button className={common.secondary} disabled={locked} onClick={() => { setCursor(null); setReload(value => value + 1); }}>Muat permintaan terbaru</button></div>
      {busy && <p role="status">Memuat permintaan…</p>}{!busy && page.items.length === 0 && <p className={styles.empty}>Belum ada permintaan pada halaman ini.</p>}
      <ul className={styles.list}>{page.items.map(item => <li className={styles.item} key={item.id}><div className={styles.row}><strong>{item.number}</strong><span className={`${styles.badge} ${item.state === 'accepted' ? styles.accepted : item.state === 'rejected' ? styles.rejected : ''}`}>{requestStateLabel(item.state)}</span></div><p>{item.labels.branch} → {item.labels.shop} · {item.lines.length} jenis kemasan</p><small>{requestDate(item.recordedAt)}</small><button className={common.secondary} disabled={locked} onClick={() => void select(item.id)}>Lihat {item.number}</button></li>)}</ul>
      <div className={common.actions}><button className={common.secondary} disabled={locked || cursor === null} onClick={() => setCursor(null)}>Awal daftar</button><button className={common.secondary} disabled={locked || page.nextRequestId === null} onClick={() => setCursor(page.nextRequestId)}>Permintaan lebih lama</button></div>
    </section>}
    {incoming && !canRead && <section className={styles.section}><h2>Pemeriksaan hasil tindakan Anda</h2><p>Akun ini dapat memeriksa hasil keputusan miliknya. Akses membaca daftar permintaan belum termasuk dalam penugasan Anda.</p></section>}
    {selected && <section ref={confirmed ? undefined : detail} tabIndex={-1} className={`${styles.section} ${styles.details}`} aria-label="Rincian permintaan"><BranchRequestFacts value={selected} />
      {incoming && selected.state === 'pending' && selected.createdBy !== account.actorId && <><h3>Peninjauan toko</h3><label>Catatan peninjauan<input value={reason} maxLength={1000} disabled={locked || preview !== null} onChange={event => setReason(event.target.value)} /></label>
        {preview ? <div className={styles.review}><h3>{preview.decision === 'accept' ? 'Konfirmasi penerimaan permintaan' : 'Konfirmasi penolakan permintaan'}</h3><p>{reason.trim()}</p><p>{preview.decision === 'accept' ? 'Permintaan diterima untuk ditindaklanjuti toko. Konfirmasikan pengaturan pemenuhan kepada pemesan melalui prosedur kerja yang berlaku.' : 'Alasan penolakan akan tampil pada riwayat pemesan.'}</p><div className={common.actions}><button className={preview.decision === 'reject' ? common.danger : common.primary} disabled={locked || !preview.canDecide} onClick={() => void decide()}>{preview.decision === 'accept' ? 'Konfirmasi terima permintaan' : 'Konfirmasi tolak permintaan'}</button><button className={common.secondary} disabled={locked} onClick={() => setPreview(null)}>Kembali meninjau</button></div></div>
          : <div className={common.actions}><button className={common.primary} disabled={locked || reason.trim().length === 0} onClick={() => void prepareDecision('accept')}>Terima permintaan</button><button className={common.secondary} disabled={locked || reason.trim().length === 0} onClick={() => void prepareDecision('reject')}>Tolak permintaan</button></div>}
      </>}
      {incoming && selected.state === 'pending' && selected.createdBy === account.actorId && <p>Permintaan Anda perlu ditinjau oleh petugas lain yang berwenang.</p>}
      <button className={common.secondary} disabled={locked} onClick={() => void select(selected.id)}>Perbarui rincian</button>
    </section>}
  </>;
}
