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
import { stockLocationKey, type StockWarehouse } from './stock-count-workflow';
import { createSourceReconciliationGateway, prepareSourceReconciliation, prepareSourceReconciliationDecision, sourceReconciliationObservation, reconciliationObservationTime, reconciliationDifferences, reconciliationObservationCurrent, type SourceReconciliationBaseline, type SourceReconciliationRead, type SourceReconciliationObservation, type SourceReconciliationDecisionPreview, type SourceReconciliationTarget } from './stock-source-reconciliation-client';
import { ReconciliationSourceFacts, ReconciliationBalanceFacts, ReconciliationObservationFields, ReconciliationObservations, reconciliationNotice, reconciliationWhen } from './stock-source-reconciliation-forms';
import styles from '../administration/access.module.css';

const path = '/inventory/source-reconciliations', permission = 'stock.source.reconciliation.';
const consent = 'Saya telah memeriksa seluruh lot, waktu dan referensi pengamatan. Pencatatan ini tidak mengaktifkan sumber atau melepas stok.';
type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
type Review = { kind: 'propose'; baseline: SourceReconciliationBaseline; command: ReturnType<typeof prepareSourceReconciliation> } |
  { kind: 'decide'; preview: SourceReconciliationDecisionPreview; observation: SourceReconciliationObservation | null; command: ReturnType<typeof prepareSourceReconciliationDecision> | null };
const status = (value: SourceReconciliationRead) => !value.decision ? 'Menunggu pemeriksaan independen' : value.decision.decision === 'confirm' ? 'Jumlah sesuai pada pengamatan yang diperiksa' : 'Ditolak · selisih atau sumber perlu ditindaklanjuti';
function message(error: unknown) {
  if (error instanceof IdentityFailure) return 'Sesi berakhir. Masuk kembali untuk memeriksa rekonsiliasi.';
  if (error instanceof AccessFailure || error instanceof RequestNotSentError) {
    if (error.code === 'invalid_stock_source_reconciliation') return 'Isi waktu, referensi, alasan dan jumlah setiap lot dengan bilangan bulat. Isian kosong bukan nol.';
    if (['stock_source_reconciliation_changed', 'stock_source_reconciliation_view_stale', 'version_conflict'].includes(error.code)) return 'Data sumber atau usia pengamatan berubah. Muat dasar terbaru dan lakukan pengamatan yang masih berlaku sebelum meninjau kembali.';
    if (['stock_source_reconciliation_unavailable', 'stock_source_reconciliation_response_invalid'].includes(error.code)) return 'Dasar lengkap sumber belum dapat dipastikan. Periksa pengaturan sumber dan hubungi penanggung jawab; jumlah belum direkonsiliasi.';
    if (['independent_person_required', 'independent_approver_required'].includes(error.code)) return 'Pemeriksaan harus dilakukan oleh orang yang berbeda dari pengaju.';
    if (['commit_outcome_unknown', 'resolve_previous_attempt'].includes(error.code)) return 'Hasil belum terkonfirmasi. Gunakan Periksa hasil dengan akun semula; jangan kirim ulang.';
    return accessMessage(error);
  }
  return 'Koneksi terputus. Periksa hasil permintaan tersimpan sebelum melanjutkan.';
}
export function StockSourceReconciliationWorkspace() {
  const [identity] = useState(createIdentityGateway), [access] = useState(createAccessGateway);
  const [account, setAccount] = useState<Account | null>(null), [error, setError] = useState<unknown>(null), [loading, setLoading] = useState(true), [revision, setRevision] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setAccount(null); setError(null); setLoading(true);
      try {
        const session = await identity.session(controller.signal), observedAt = performance.now();
        if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
        const current = await access.current(); if (!active) return;
        if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
        if (!['read', 'propose', 'decide'].some(action => current.allowedActions.includes(permission + action))) throw new AccessFailure(403, 'scope_denied');
        if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
        setAccount({ actorId: current.userId, actions: current.allowedActions, session, observedAt }); setElapsed(performance.now() - observedAt);
      } catch (failure) { if (active) setError(failure); } finally { if (active) setLoading(false); }
    });
    return () => { active = false; controller.abort(); };
  }, [identity, access, revision]);
  useEffect(() => {
    const timer = window.setInterval(() => { if (account) setElapsed(performance.now() - account.observedAt); }, 1000);
    const restored = (event: PageTransitionEvent) => { if (event.persisted) { setAccount(null); setRevision(value => value + 1); } };
    window.addEventListener('pageshow', restored); return () => { window.clearInterval(timer); window.removeEventListener('pageshow', restored); };
  }, [account]);
  const denied = useCallback((failure: unknown) => { if (failure instanceof AccessFailure && (failure.status === 401 || ['actor_changed', 'session_expired'].includes(failure.code))) { setAccount(null); setError(failure); } }, []);
  return <main className={styles.page}>
    <header className={styles.header}><div><Link className={styles.brand} href="/identity/account">Unit Toko</Link><h1>Rekonsiliasi jumlah fisik</h1><p>Bandingkan pengamatan seluruh lot dengan catatan sumber dan pemeriksaan orang berbeda.</p></div><Link className={styles.secondary} href="/identity/account">Akun saya</Link></header>
    {error !== null && <p className={styles.alert} role="alert">{message(error)}</p>}
    {loading ? <p role="status">Memeriksa akun dan akses…</p> : !account || sessionTiming(account.session, elapsed).expired ? <section className={styles.state}><h2>Periksa sesi dan akses</h2><Link href={`/identity/login?returnPath=${encodeURIComponent(path)}`}>Masuk kembali</Link><button className={styles.secondary} onClick={() => setRevision(value => value + 1)}>Perbarui sesi dan akses</button></section> :
      <ReconciliationDesk key={account.actorId + ':' + account.observedAt} account={account} fresh={sessionTiming(account.session, elapsed).fresh} now={Date.parse(account.session.serverTime) + Math.max(0, elapsed)} onDenied={denied} />}
  </main>;
}

function ReconciliationDesk({ account, fresh, now, onDenied }: { account: Account; fresh: boolean; now: number; onDenied: (error: unknown) => void }) {
  const [gateway] = useState(createSourceReconciliationGateway), [gate] = useState(() => new CatalogReadGate()), recovery = useOperationRecovery(account.actorId);
  const alive = useRef(false), pending = useRef(false), ledger = useRef<MutationLedger | null>(null), keyPrefix = useRef('recon.propose'), reviewHeading = useRef<HTMLHeadingElement>(null);
  const [targets, setTargets] = useState<SourceReconciliationTarget[]>([]), [selected, setSelected] = useState<SourceReconciliationTarget | null>(null);
  const [warehouses, setWarehouses] = useState<StockWarehouse[]>([]), [warehouse, setWarehouse] = useState<StockWarehouse | null>(null), [targetTotal, setTargetTotal] = useState('0'), [targetOffset, setTargetOffset] = useState(0);
  const [page, setPage] = useState<Awaited<ReturnType<typeof gateway.list>> | null>(null), [offset, setOffset] = useState(0), [saved, setSaved] = useState<SourceReconciliationRead | null>(null), [baseline, setBaseline] = useState<SourceReconciliationBaseline | null>(null);
  const [mode, setMode] = useState<'propose' | 'check' | null>(null), [observedAt, setObservedAt] = useState(''), [reference, setReference] = useState(''), [quantities, setQuantities] = useState<Record<string, string>>({}), [reason, setReason] = useState('');
  const [review, setReview] = useState<Review | null>(null), [confirmed, setConfirmed] = useState(false), [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [offline, setOffline] = useState(false), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState(''), [revision, setRevision] = useState(0);
  const canRead = account.actions.includes(permission + 'read'), canPropose = account.actions.includes(permission + 'propose'), canDecide = account.actions.includes(permission + 'decide'), locked = busy || recovering || offline, commandLocked = locked || recovery.locked;
  const current = useCallback(() => alive.current && navigator.onLine && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const clearFields = useCallback(() => { setObservedAt(''); setReference(''); setQuantities({}); setReason(''); setReview(null); setConfirmed(false); }, []);
  const clearView = useCallback(() => { clearFields(); setSaved(null); setBaseline(null); setMode(null); }, [clearFields]);
  const clearScope = useCallback(() => { clearView(); setSelected(null); setPage(null); setOffset(0); }, [clearView]);
  useEffect(() => {
    alive.current = true;
    const connection = () => { setOffline(!navigator.onLine); if (!navigator.onLine) { gate.cancel(); clearScope(); setTargets([]); setWarehouse(null); setWarehouses([]); setBusy(false); } };
    connection(); window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => { alive.current = false; gate.cancel(); window.removeEventListener('online', connection); window.removeEventListener('offline', connection); };
  }, [gate, clearScope]);
  useEffect(() => {
    const request = gate.begin(account.actorId, 'reconciliation-targets');
    void Promise.resolve().then(async () => {
      if (!request.current() || !current()) return; clearScope(); setTargets([]); setWarehouse(null); setWarehouses([]); setError(null); if (!canRead) { setBusy(false); return; } setBusy(true);
      try { const values = await gateway.warehouses(account.actorId, request.signal); if (request.current() && current()) setWarehouses(values); }
      catch (failure) { if (request.current() && current()) { setError(failure); onDenied(failure); } }
      finally { if (request.current()) setBusy(false); }
    });
    return () => gate.cancel();
  }, [gateway, gate, account.actorId, revision, current, clearScope, onDenied, canRead]);
  useEffect(() => { if (review) reviewHeading.current?.focus(); }, [review]);
  useEffect(() => { if (!busy && !mode && recovery.entries.length === 0) return; const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ''; }; window.addEventListener('beforeunload', warn); return () => window.removeEventListener('beforeunload', warn); }, [busy, mode, recovery.entries.length]);
  function mutations() { return ledger.current ??= new MutationLedger(() => `${keyPrefix.current}.${crypto.randomUUID()}`); }
  async function work(action: (valid: () => boolean, signal: AbortSignal) => Promise<void>) {
    if (!current() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); setNotice('');
    const request = gate.begin(account.actorId, 'reconciliation-action'), valid = () => request.current() && current();
    try { await action(valid, request.signal); }
    catch (failure) { if (request.current() && alive.current) { setError(failure); if ((failure instanceof AccessFailure || failure instanceof RequestNotSentError) && ([401, 403].includes(failure.status) || ['actor_changed', 'stock_source_reconciliation_changed', 'stock_source_reconciliation_view_stale'].includes(failure.code))) { clearScope(); setTargets([]); setWarehouse(null); setWarehouses([]); } onDenied(failure); } }
    finally { pending.current = false; recovery.refresh(); if (request.current() && alive.current) setBusy(false); }
  }
  async function chooseWarehouse(next: StockWarehouse, targetPage = 0) { if (commandLocked || review) return; await work(async (valid, signal) => { clearScope(); setTargets([]); setWarehouse(null); const values = await gateway.targets(account.actorId, next, targetPage, signal); if (valid()) { setWarehouse(next); setTargets(values.items); setTargetOffset(targetPage); setTargetTotal(values.total); } }); }
  async function select(target: SourceReconciliationTarget) { if (commandLocked || review) return; await work(async (valid, signal) => { clearScope(); const value = await gateway.list(account.actorId, target.poolId, 0, signal); if (valid()) { setSelected(target); setPage(value); } }); }
  async function historyPage(next: number) { if (!selected || commandLocked || review) return; await work(async (valid, signal) => { clearView(); const value = await gateway.list(account.actorId, selected.poolId, next, signal); if (valid()) { setPage(value); setOffset(next); } }); }
  async function open(id: string) { if (commandLocked || review) return; await work(async (valid, signal) => { clearView(); const value = await gateway.read(account.actorId, id, signal); if (valid()) setSaved(value); }); }
  async function inspectBaseline() { if (!selected || commandLocked || review) return; await work(async (valid, signal) => { clearView(); const value = await gateway.baseline(account.actorId, selected.poolId, signal); if (valid()) setBaseline(value); }); }
  async function begin(checker = false) {
    if (!selected || commandLocked || review || checker && (!saved || saved.decision || !canDecide) || !checker && !canPropose) return;
    await work(async (valid, signal) => { clearFields(); setBaseline(null); setMode(null); if (!checker) setSaved(null); const value = await gateway.baseline(account.actorId, selected.poolId, signal); if (valid()) { setBaseline(value); setMode(checker ? 'check' : 'propose'); } });
  }
  function enteredObservation(source: SourceReconciliationBaseline) { return sourceReconciliationObservation({ observedAt: reconciliationObservationTime(observedAt), sourceReference: reference, entries: source.balances.map(row => ({ balanceId: row.balanceId, packages: quantities[row.balanceId] ?? '' })) }); }
  async function preview(event: FormEvent) {
    event.preventDefault(); if (!baseline || !mode || !fresh || commandLocked || review) return;
    await work(async (valid, signal) => {
      const observation = enteredObservation(baseline);
      if (mode === 'propose') { const command = prepareSourceReconciliation(account.actorId, baseline, observation, reason, now); if (valid()) { setReview({ kind: 'propose', baseline, command }); setConfirmed(false); } }
      else if (saved) { const shown = await gateway.previewDecision(account.actorId, saved, 'confirm', observation, signal); if (!valid()) return; const eligible = shown.canConfirm && shown.baseline && reconciliationObservationCurrent(shown.baseline, saved.proposal.observation, now) && reconciliationObservationCurrent(shown.baseline, observation, now); const command = eligible ? prepareSourceReconciliationDecision(account.actorId, saved, shown, observation, reason, now) : null; setReview({ kind: 'decide', preview: shown, observation, command }); setConfirmed(false); }
    });
  }
  async function reject() { if (!saved || saved.decision || !canDecide || !fresh || commandLocked || review) return; await work(async (valid, signal) => { const shown = await gateway.previewDecision(account.actorId, saved, 'reject', null, signal), command = prepareSourceReconciliationDecision(account.actorId, saved, shown, null, reason, now); if (valid()) { setReview({ kind: 'decide', preview: shown, observation: null, command }); setConfirmed(false); } }); }
  async function save() {
    if (!review || !review.command || !confirmed || !fresh || commandLocked || (review.kind === 'propose' ? !canPropose : !canDecide)) return;
    const frozen = review, action = frozen.kind === 'propose' ? 'propose' : frozen.preview.decision;
    keyPrefix.current = frozen.kind === 'propose' ? `recon.propose.${frozen.command.poolId}` : `recon.${action}.${frozen.preview.proposal.id}`;
    await work(async (valid, signal) => {
      const verified = () => valid() && sessionTiming(account.session, performance.now() - account.observedAt).fresh;
      const command = frozen.command; if (!command) return;
      const result = await submitTrackedRequest(mutations(), recovery.journal, account.actorId, 'source-reconciliation-' + action, permission + (action === 'propose' ? 'propose' : 'decide'), command.body,
        key => frozen.kind === 'propose' ? gateway.propose(frozen.command, key, verified) : gateway.decide(frozen.command!, key, verified));
      if (!valid()) return; clearView(); const actual = await gateway.read(account.actorId, result.proposalId, signal);
      if (valid()) { setSaved(actual); setNotice(action === 'propose' ? 'Pengamatan tersimpan dan menunggu pemeriksaan orang berbeda.' : action === 'confirm' ? 'Kesesuaian jumlah dicatat untuk pengamatan yang diperiksa. Status stok dan sumber tidak berubah.' : 'Penolakan tersimpan. Pengamatan dan selisih tetap dapat ditelusuri.'); }
    });
  }
  function reconcile() { ledger.current?.resetAfterReconciliation(); clearScope(); recovery.refresh(); setRevision(value => value + 1); }
  const reviewStale = review?.kind === 'propose' ? !reconciliationObservationCurrent(review.baseline, review.command.body.input.observation, now) : review?.preview.decision === 'confirm' && review.preview.baseline && review.observation ? !reconciliationObservationCurrent(review.preview.baseline, review.observation, now) || !reconciliationObservationCurrent(review.preview.baseline, review.preview.proposal.observation, now) : false;
  const makerStale = review?.kind === 'decide' && review.preview.decision === 'confirm' && review.preview.baseline && !reconciliationObservationCurrent(review.preview.baseline, review.preview.proposal.observation, now);
  return <>
    {(canPropose || canDecide) && !fresh && <div className={styles.alert}><p>Perbarui verifikasi kedua sebelum menyimpan pengajuan atau keputusan.</p>{['propose', 'decide'].filter(action => account.actions.includes(permission + action) && account.session.stepUpOperations.includes(permission + action)).map(action => <Link key={action} href={`/identity/account?operation=${permission + action}&returnPath=${encodeURIComponent(path)}`}>{action === 'propose' ? 'Verifikasi untuk pengajuan rekonsiliasi' : 'Verifikasi untuk pemeriksaan rekonsiliasi'}</Link>)}</div>}
    {offline && <p role="alert" className={styles.alert}>Koneksi terputus. Data sebelumnya disembunyikan; referensi permintaan tetap tersimpan untuk akun semula.</p>}
    {error !== null && <p role="alert" className={styles.alert}>{message(error)}</p>}
    {notice && <p role="status" className={styles.success}>{notice}</p>}
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy || offline} onBusyChange={setRecovering} onFailure={failure => { setError(failure); onDenied(failure); }} onResolved={reconcile} recoveryAdapters={{ 'stock.source.reconciliation': { lookup: entry => gateway.recover(entry.actorId, entry.operation, entry.key, canRead) } }} />
    {!canRead ? <section className={styles.state}><h2>Akses baca diperlukan untuk pekerjaan baru</h2><p>Akun ini dapat memeriksa hasil permintaan miliknya yang belum terkonfirmasi. Membuka sumber atau riwayat memerlukan izin baca rekonsiliasi dalam penugasan yang sama.</p></section> : <section className={styles.accountSection} aria-busy={busy}>
      <div className={styles.actions}><button className={styles.secondary} disabled={locked || review !== null} onClick={() => setRevision(value => value + 1)}>Muat ulang sumber</button></div>
      <label htmlFor="reconciliation-warehouse">Gudang dan kanal<select id="reconciliation-warehouse" value={warehouse ? stockLocationKey(warehouse.location) : ''} disabled={commandLocked || review !== null} onChange={event => { const next = warehouses.find(item => stockLocationKey(item.location) === event.target.value); if (next) void chooseWarehouse(next); else { clearScope(); setWarehouse(null); setTargets([]); } }}><option value="">Pilih gudang dan kanal</option>{warehouses.map(item => <option key={stockLocationKey(item.location)} value={stockLocationKey(item.location)}>{item.organizationName} / {item.shopName} / {item.warehouseName} / {item.channelName}</option>)}</select></label>
      {warehouse && <><label htmlFor="reconciliation-pool">Pilih sumber stok<select id="reconciliation-pool" value={selected?.poolId ?? ''} disabled={commandLocked || review !== null} onChange={event => { const target = targets.find(item => item.poolId === event.target.value); if (target) void select(target); else clearScope(); }}><option value="">Pilih sumber stok</option>{targets.map(target => <option key={target.poolId} value={target.poolId}>{target.label}</option>)}</select></label><nav aria-label="Halaman sumber stok" className={styles.actions}><button className={styles.secondary} disabled={commandLocked || review !== null || targetOffset === 0} onClick={() => void chooseWarehouse(warehouse, Math.max(0, targetOffset - 25))}>Sumber sebelumnya</button><button className={styles.secondary} disabled={commandLocked || review !== null || BigInt(targetOffset + targets.length) >= BigInt(targetTotal)} onClick={() => void chooseWarehouse(warehouse, targetOffset + 25)}>Sumber berikutnya</button></nav></>}
      {!busy && (warehouses.length === 0 || warehouse && targets.length === 0) && <p>Belum ada sumber yang dapat dibaca dalam penugasan Anda. Periksa izin dan kebijakan bersama pengelola.</p>}
      {selected && <><p className={styles.scopeSummary}>{selected.organizationName} / {selected.shopName} / {selected.warehouseName}<br /><strong>{selected.label}</strong> · {selected.channelNames.join(', ')}</p><div className={styles.actions}><button className={styles.secondary} disabled={commandLocked || review !== null} onClick={() => void inspectBaseline()}>Lihat dasar jumlah lengkap</button>{canPropose && <button className={styles.primary} disabled={commandLocked || review !== null} onClick={() => void begin()}>Catat pengamatan lengkap</button>}</div></>}
      <p className={styles.help}>{reconciliationNotice}</p>
    </section>}
    {selected && !review && page && <section className={styles.accountSection}><h2>Riwayat rekonsiliasi</h2><p>{page.total} pengajuan dalam sumber ini.</p>{page.items.length === 0 ? <p>Belum ada pengamatan yang diajukan.</p> : <ul className={styles.historyList}>{page.items.map(item => <li key={item.proposal.id}><strong>{item.proposal.observation.sourceReference}</strong><p>{status(item)} · {reconciliationWhen(item.proposal.recordedAt)}</p><button className={styles.secondary} disabled={commandLocked} onClick={() => void open(item.proposal.id)}>Buka pengajuan {item.proposal.observation.sourceReference}</button></li>)}</ul>}<nav aria-label="Halaman riwayat rekonsiliasi" className={styles.actions}><button className={styles.secondary} disabled={commandLocked || offset === 0} onClick={() => void historyPage(Math.max(0, offset - 25))}>Sebelumnya</button><button className={styles.secondary} disabled={commandLocked || BigInt(offset + page.items.length) >= BigInt(page.total)} onClick={() => void historyPage(offset + 25)}>Berikutnya</button></nav></section>}
    {saved && !review && <section className={styles.accountSection} aria-label="Rincian pengajuan rekonsiliasi"><h2>{status(saved)}</h2><ReconciliationSourceFacts source={saved.proposal.baseline} /><ReconciliationObservations source={saved.proposal.baseline} observed={saved.proposal.observation} title="Pengamatan pengaju" /><p><strong>Alasan pengajuan:</strong> {saved.proposal.reason}</p>
      {saved.decision ? <><h3>Keputusan pemeriksa</h3><p>{saved.decision.reason} · {reconciliationWhen(saved.decision.recordedAt)}</p>{saved.decision.observation && <ReconciliationObservations source={saved.proposal.baseline} observed={saved.decision.observation} title="Pengamatan pemeriksa" />}</> : canDecide && <><p>Pemeriksa harus berbeda dari pengaju; sistem memeriksa kewenangan dan keterkaitan orang saat peninjauan.</p>{mode !== 'check' && <button className={styles.primary} disabled={commandLocked} onClick={() => void begin(true)}>Periksa secara independen</button>}<label htmlFor="reconciliation-reason">Alasan keputusan<input id="reconciliation-reason" required maxLength={1000} value={reason} disabled={commandLocked} onChange={event => setReason(event.target.value)} /></label><button className={styles.danger} disabled={commandLocked || !fresh || !reason} onClick={() => void reject()}>Tinjau penolakan</button></>}
    </section>}
    {baseline && !mode && !review && <section className={styles.accountSection}><h2>Dasar sumber saat dibaca</h2><ReconciliationSourceFacts source={baseline} /><ReconciliationBalanceFacts source={baseline} /></section>}
    {baseline && mode && !review && <form className={styles.accountSection} onSubmit={event => void preview(event)}><h2>{mode === 'propose' ? 'Catat pengamatan lengkap' : 'Masukkan pengamatan independen'}</h2><ReconciliationSourceFacts source={baseline} /><ReconciliationObservationFields source={baseline} checker={mode === 'check'} observedAt={observedAt} sourceReference={reference} quantities={quantities} disabled={commandLocked} onTime={setObservedAt} onReference={setReference} onQuantity={(id, value) => setQuantities(previous => ({ ...previous, [id]: value }))} />{mode === 'propose' && <label htmlFor="reconciliation-maker-reason">Alasan pengajuan<input id="reconciliation-maker-reason" required maxLength={1000} disabled={commandLocked} value={reason} onChange={event => setReason(event.target.value)} /></label>}<div className={styles.actions}><button className={styles.primary} disabled={commandLocked || !fresh}>{mode === 'propose' ? 'Tinjau pengajuan' : 'Bandingkan pengamatan independen'}</button><button type="button" className={styles.secondary} disabled={commandLocked} onClick={() => { clearFields(); setBaseline(null); setMode(null); }}>Batalkan isian</button></div></form>}
    {review && <section className={styles.accountSection} aria-label="Tinjauan rekonsiliasi"><h2 ref={reviewHeading} tabIndex={-1}>{review.kind === 'propose' ? 'Tinjau pengajuan rekonsiliasi' : review.preview.decision === 'confirm' ? 'Tinjau pemeriksaan independen' : 'Tinjau penolakan rekonsiliasi'}</h2>
      {review.kind === 'propose' ? <><ReconciliationSourceFacts source={review.baseline} /><ReconciliationObservations source={review.baseline} observed={review.command.body.input.observation} title="Pengamatan yang akan diajukan" />{reconciliationDifferences(review.baseline, review.command.body.input.observation).length > 0 && <p className={styles.alert}>Ada selisih. Pengajuan menyimpan hasil pengamatan; selisih tidak diselesaikan dan jumlah tidak dinyatakan sesuai.</p>}<p><strong>Alasan:</strong> {review.command.body.reason}</p></> : <><ReconciliationObservations source={review.preview.proposal.baseline} observed={review.preview.proposal.observation} title="Pengamatan pengaju" />{review.observation && review.preview.baseline && <ReconciliationObservations source={review.preview.baseline} observed={review.observation} title="Pengamatan pemeriksa" />}<p><strong>Alasan keputusan:</strong> {review.command?.body.reason ?? reason}</p>{review.preview.decision === 'confirm' && !review.command && <p role="alert" className={styles.alert}>Pengamatan atau dasar sumber belum sesuai, atau usia pengamatan sudah berakhir. Kesesuaian belum dapat dicatat. Tindak lanjuti perbedaan dan ajukan pengamatan baru bila diperlukan.</p>}</>}
      <p className={styles.help}>{reconciliationNotice}</p>{reviewStale && <p role="alert" className={styles.alert}>{makerStale ? 'Pengamatan pengaju sudah tidak berlaku. Pengamatan pemeriksa yang baru tidak memperpanjang usia pengamatan pengaju; diperlukan pengajuan baru.' : 'Usia pengamatan telah berakhir. Kembali untuk membaca sumber dan melakukan pengamatan baru.'}</p>}<label className={styles.checkbox}><input type="checkbox" checked={confirmed} disabled={commandLocked || !review.command || Boolean(reviewStale)} onChange={event => setConfirmed(event.target.checked)} />{consent}</label><div className={styles.actions}><button className={styles.primary} disabled={commandLocked || !fresh || !confirmed || !review.command || Boolean(reviewStale)} onClick={() => void save()}>{review.kind === 'propose' ? 'Simpan pengajuan' : review.preview.decision === 'confirm' ? 'Catat kesesuaian jumlah' : 'Simpan penolakan'}</button><button className={styles.secondary} disabled={commandLocked} onClick={() => { setReview(null); setConfirmed(false); }}>Kembali ke isian</button></div>
    </section>}
  </>;
}
