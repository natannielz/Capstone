'use client';

import Link from 'next/link';
import { SessionAccessState } from '@/components/workspace-kit/session-access-state';
import {useEffect, useRef, useState} from 'react';
import {RequestNotSentError} from '../../api/client';
import {createIdentityGateway} from '../identity/identity-client';
import {IdentityFailure, sessionTiming, type IdentitySession} from '../identity/identity-workflow';
import {createAccessGateway} from '../administration/access-client';
import {AccessFailure} from '../administration/workflow';
import {createAnomalyGateway, retainAnomalyPolicyReference, type AnomalyChoice, type AnomalyEvaluation, type AnomalyKind, type AnomalyOutcome} from './anomaly-client';
import {AnomalyJournal, anomalyJournalPrefix, parseAnomalyReference, type AnomalyReference} from './anomaly-journal';
import common from '../administration/access.module.css';
import styles from './anomaly.module.css';
import {AnomalyPolicyPanel} from './AnomalyPolicyPanel';
import { formatDateTime } from '@/lib/format';

type Account = {actorId: string; session: IdentitySession; observedAt: number; review: boolean; configure: boolean; kinds: AnomalyKind[]};
const states = {consistent: 'Konsisten', flagged: 'Perlu tinjauan', within_policy: 'Dalam batas tinjauan', policy_needed: 'Kebijakan belum tersedia', unavailable: 'Sumber belum tersedia'};
const outcomes = {acknowledged: 'Sudah ditinjau', requires_action: 'Perlu tindak lanjut', not_concerning: 'Tidak perlu tindak lanjut'};
function message(error: unknown) {
  if (error instanceof IdentityFailure) return 'Sesi Anda perlu diperbarui. Masuk kembali untuk melanjutkan.';
  if (error instanceof AccessFailure || error instanceof RequestNotSentError) {
    if (error.status === 401 || error.status === 403) return 'Sesi atau izin tinjauan belum tersedia. Periksa akun dan cakupan toko.';
    if (error.code === 'anomaly_source_changed') return 'Sumber atau kebijakan berubah. Tampilkan pemeriksaan baru sebelum menyimpan tindak lanjut.';
    if (error.code === 'anomaly_already_reviewed') return 'Petugas telah menyimpan tinjauan. Perbarui hasil untuk melihat catatannya.';
  }
  return 'Hasil belum dapat dipastikan. Perbarui hasil untuk memeriksa catatan tersimpan, atau ulangi permintaan yang sama.';
}
export function AnomalyWorkspace() {
  const [identity] = useState(createIdentityGateway), [access] = useState(createAccessGateway);
  const [account, setAccount] = useState<Account | null>(null), [error, setError] = useState<unknown>(null), [attempt, setAttempt] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void (async () => {
      try {
        const session = await identity.session(controller.signal), observedAt = performance.now();
        if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
        const scope = await access.current();
        if (scope.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
        if (!scope.allowedActions.includes('operations.anomaly.read')) throw new AccessFailure(403, 'scope_denied');
        const kinds: AnomalyKind[] = [];
        if (scope.allowedActions.includes('order.read')) kinds.push('order');
        if (['disbursement.record', 'case.read', 'invoice.read', 'payment.review.read'].every(permission => scope.allowedActions.includes(permission))) kinds.push('refund');
        if (kinds.length === 0) throw new AccessFailure(403, 'scope_denied');
        if (active) {setAccount({actorId: scope.userId, session, observedAt, review: scope.allowedActions.includes('operations.anomaly.review'), configure: scope.allowedActions.includes('operations.anomaly.policy.configure'), kinds}); setError(null);}
      } catch (failure) {if (active) {setError(failure); setAccount(null);}}
    })();
    return () => {active = false; controller.abort();};
  }, [identity, access, attempt]);
  useEffect(() => {
    const tick = window.setInterval(() => {if (account) setElapsed(performance.now() - account.observedAt);}, 1000);
    const restore = (event: PageTransitionEvent) => {if (event.persisted) {setAccount(null); setAttempt(x => x + 1);}};
    window.addEventListener('pageshow', restore); return () => {window.clearInterval(tick); window.removeEventListener('pageshow', restore);};
  }, [account]);
  return <main className={common.page}>
    <header className={common.header}><div><Link className={common.brand} href="/identity/account">Unit Toko</Link><h1>Tinjauan operasional</h1>
      <p>Periksa tanda pada harga, jumlah, dan pengembalian dana. Simpan alasan tindak lanjut setelah meninjau sumber.</p></div><Link href="/identity/account">Akun saya</Link></header>
    <p>Hasil ini membantu petugas meninjau. Persetujuan pesanan, perubahan stok, serta pembayaran tetap dilakukan pada alur masing-masing.</p>
    {error !== null && account !== null && <p className={common.alert} role="alert">{message(error)}</p>}
    {!account && error === null ? <p role="status">Memeriksa sesi dan akses…</p> : !account || sessionTiming(account.session, elapsed).expired
      ? <SessionAccessState error={error} kind={account ? "expired" : undefined} returnPath="/operations/anomalies" onRetry={() => {setAccount(null); setError(null); setAttempt(x => x + 1);}} detail={error !== null ? message(error) : undefined} />
      : <Desk key={account.actorId + ':' + account.observedAt} account={account} onDenied={() => {setAccount(null); setError(new AccessFailure(403, 'scope_denied'));}} />}
  </main>;
}
function Desk({account, onDenied}: {account: Account; onDenied: () => void}) {
  const [gateway] = useState(createAnomalyGateway), alive = useRef(true), generation = useRef(0);
  const [kind, setKind] = useState<AnomalyKind>(account.kinds[0]), [items, setItems] = useState<AnomalyChoice[]>([]), [selected, setSelected] = useState('');
  const [cursors, setCursors] = useState<(string | null)[]>([null]), [nextCursor, setNextCursor] = useState<string | null>(null);
  const cursor = cursors[cursors.length - 1];
  const [value, setValue] = useState<AnomalyEvaluation | null>(null), [error, setError] = useState<unknown>(null), [busy, setBusy] = useState(true);
  const [reason, setReason] = useState(''), [outcome, setOutcome] = useState<AnomalyOutcome>('acknowledged'), [reload, setReload] = useState(0);
  const pending = useRef<{key: string; operation: 'evaluate' | 'review'; source: AnomalyChoice; value: AnomalyEvaluation | null; reason: string; outcome: AnomalyOutcome} | null>(null);
  const [uncertain, setUncertain] = useState(false);
  const [journal] = useState(() => new AnomalyJournal(() => window.localStorage));
  const lastReferences = useRef<AnomalyReference[]>([]);
  const [references, setReferences] = useState<AnomalyReference[]>([]), [storageBlocked, setStorageBlocked] = useState(false), [recoveryNotice, setRecoveryNotice] = useState('');
  function readReferences() {
    try {
      const legacyPrefix = 'unit-toko.anomaly-policy:' + account.actorId + ':';
      for (const key of Object.keys(window.sessionStorage).filter(key => key.startsWith(legacyPrefix))) {
        const old = JSON.parse(window.sessionStorage.getItem(key) ?? 'null');
        if (!old || Object.keys(old).sort().join(',') !== 'actorId,createdAt,expectedRevision,key,sourceId,sourceKind' ||
            key !== legacyPrefix + old.sourceKind + ':' + old.sourceId) throw new Error('Invalid prior anomaly reference');
        journal.remember(parseAnomalyReference({...old, operation: 'configure', evaluationId: null}, account.actorId));
        window.sessionStorage.removeItem(key);
      }
      const current = journal.list(account.actorId);
      if (lastReferences.current.some(prior => !current.some(ref => ref.operation === prior.operation && ref.key === prior.key))) {setValue(null); setReason('');}
      lastReferences.current = current; setReferences(current); setStorageBlocked(false);
      if (pending.current && !current.some(ref => ref.key === pending.current!.key)) {pending.current = null; setUncertain(false);}
    } catch {setStorageBlocked(true);}
  }
  useEffect(() => {
    // Retain prior tab-only references durably before removing the old session key.
    void Promise.resolve().then(() => {if (alive.current) readReferences();});
    const changed = (event: StorageEvent) => {if (event.key === null || event.key.startsWith(anomalyJournalPrefix(account.actorId))) readReferences();};
    window.addEventListener('storage', changed); return () => window.removeEventListener('storage', changed);
    // The journal and account are stable for this mounted desk.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [journal, account.actorId]);
  const recoveryLocked = uncertain || storageBlocked || references.length > 0;
  useEffect(() => {alive.current = true; return () => {alive.current = false;};}, []);
  const context = (version: number) => ({actorId: account.actorId, isCurrent: () => alive.current && generation.current === version && !sessionTiming(account.session, performance.now() - account.observedAt).expired});
  const failed = (failure: unknown) => {setError(failure); if ((failure instanceof AccessFailure || failure instanceof RequestNotSentError) && (failure.status === 401 || failure.status === 403 || failure.code === 'actor_changed')) onDenied();};
  useEffect(() => {
    const version = ++generation.current; let active = true;
    void gateway.choices(kind, context(version), cursor).then(data => {if (active) {setItems(data.items); setNextCursor(data.nextCursor); setError(null);}})
      .catch(failure => {if (active) failed(failure);}).finally(() => {if (active) setBusy(false);});
    return () => {active = false;};
    // The account and gateway are stable for this mounted desk.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [kind, reload, cursor]);
  async function refresh(source = items.find(item => item.id === selected)) {
    if (!source) return; const version = ++generation.current; setBusy(true); setValue(null);
    try {const result = await gateway.latest(source, context(version)); if (context(version).isCurrent()) {setValue(result); setError(null);}}
    catch (failure) {if (context(version).isCurrent()) failed(failure);} finally {if (context(version).isCurrent()) setBusy(false);}
  }
  async function send(operation: 'evaluate' | 'review') {
    const source = items.find(item => item.id === selected); if (!source || busy || storageBlocked || references.length > 0 && !pending.current) return;
    const earlierAttemptExists = pending.current !== null;
    const attempt = pending.current ?? {key: crypto.randomUUID(), operation, source, value, reason: reason.trim(), outcome};
    const original = references.find(ref => ref.key === attempt.key) ?? {actorId: account.actorId, operation: attempt.operation,
      sourceKind: attempt.source.kind, sourceId: attempt.source.id, evaluationId: attempt.operation === 'review' ? attempt.value!.id : null,
      expectedRevision: null, key: attempt.key, createdAt: new Date().toISOString()};
    pending.current = attempt; const version = ++generation.current; setBusy(true); setError(null);
    try {
      try {journal.remember(original); readReferences();} catch {throw new RequestNotSentError(503, 'anomaly_recovery_storage_unavailable');}
      const result = attempt.operation === 'evaluate' ? await gateway.evaluate(attempt.source, attempt.reason, attempt.key, context(version)) :
        await gateway.review(attempt.value!, attempt.outcome, attempt.reason, attempt.key, context(version));
      if (context(version).isCurrent()) {journal.confirmed(original); readReferences(); setValue(result); pending.current = null; setUncertain(false); setReason('');}
    } catch (failure) {if (context(version).isCurrent()) {
      const notSent = !earlierAttemptExists && failure instanceof RequestNotSentError;
      if (notSent || !retainAnomalyPolicyReference(failure, earlierAttemptExists)) {
        try {journal.confirmed(original); readReferences(); pending.current = null; setUncertain(false);} catch {setStorageBlocked(true); setUncertain(true);}
        if (failure instanceof AccessFailure && failure.code === 'anomaly_source_changed') setValue(null);
      }
      else setUncertain(true);
      failed(failure);
    }}
    finally {if (context(version).isCurrent()) setBusy(false);}
  }
  async function recover(original: AnomalyReference) {
    if (busy || storageBlocked) return; const version = ++generation.current; setBusy(true); setRecoveryNotice('');
    try {
      if (original.operation === 'configure') {
        const result = await gateway.recoverPolicy({kind: original.sourceKind, id: original.sourceId, label: original.sourceId}, original.key, context(version));
        if (!context(version).isCurrent()) return;
        if (result.status === 'confirmed') {journal.confirmed(original); readReferences(); setRecoveryNotice(`Versi ${result.policyVersion} terkonfirmasi tersimpan dari permintaan asli.`);}
        else setRecoveryNotice('Permintaan belum terkonfirmasi. Referensi asli tetap tersimpan; periksa kembali tanpa membuat permintaan baru.');
      } else {
        const result = await gateway.recover(original, context(version)); if (!context(version).isCurrent()) return;
        if (result.status === 'confirmed') {journal.confirmed(original); readReferences(); setValue(result.evaluation); setError(null);
          setRecoveryNotice('Hasil permintaan asli sudah ditemukan. Referensi lain tetap tersimpan.');}
        else setRecoveryNotice('Permintaan belum terkonfirmasi. Referensi asli tetap tersimpan; periksa kembali tanpa membuat permintaan baru.');
      }
    } catch (failure) {if (context(version).isCurrent()) {setRecoveryNotice('Catatan belum dapat diperiksa. Referensi asli tetap tersimpan; muat ulang halaman untuk memeriksa izin Anda.'); failed(failure);}}
    finally {if (context(version).isCurrent()) setBusy(false);}
  }
  return <section className={styles.desk} aria-busy={busy}>
    {storageBlocked && <p role="alert">Referensi pemulihan tidak dapat dibaca. Perubahan baru ditahan; periksa penyimpanan peramban.</p>}
    {recoveryNotice && <p role="status">{recoveryNotice}</p>}
    {references.length > 0 && <section className={styles.notice} aria-label="Pemulihan tinjauan"><h2>Periksa hasil permintaan asli</h2>
      <p>Referensi tersimpan tetap tersedia setelah peramban ditutup. Pulihkan setiap hasil sebelum membuat perubahan baru.</p>
      {references.map(ref => <div key={ref.operation + ':' + ref.key} data-recovery-key={ref.key}><p>{ref.operation === 'configure' ? 'Kebijakan' : ref.operation === 'evaluate' ? 'Pemeriksaan' : 'Tindak lanjut'} · {ref.sourceKind} #{ref.sourceId}</p>
        <button type="button" disabled={busy || storageBlocked} onClick={() => void recover(ref)}>Periksa hasil asli {ref.operation === 'configure' ? 'kebijakan' : ref.operation === 'evaluate' ? 'pemeriksaan' : 'tinjauan'}</button></div>)}</section>}
    <div className={styles.controls}><label>Jenis sumber<select value={kind} disabled={busy || recoveryLocked} onChange={event => {setBusy(true); setKind(event.target.value as AnomalyKind); setCursors([null]); setNextCursor(null); setSelected(''); setItems([]); setValue(null); generation.current++;}}>
      {account.kinds.includes('order') && <option value="order">Harga dan jumlah pesanan</option>}{account.kinds.includes('refund') && <option value="refund">Pengembalian dana</option>}</select></label>
      <label>Sumber yang diizinkan<select value={selected} disabled={busy || recoveryLocked} onChange={event => {setSelected(event.target.value); setValue(null); void refresh(items.find(item => item.id === event.target.value));}}>
        <option value="">Pilih sumber</option>{items.map(item => <option key={item.id} value={item.id}>{item.label}</option>)}</select></label>
      <button type="button" disabled={busy} onClick={() => selected ? void refresh() : (setBusy(true), setReload(x => x + 1))}>Perbarui hasil</button>
      <button type="button" disabled={busy || recoveryLocked || cursors.length === 1} onClick={() => {setBusy(true); setSelected(''); setValue(null); setItems([]); setCursors(current => current.slice(0, -1));}}>Sumber sebelumnya</button>
      <button type="button" disabled={busy || recoveryLocked || nextCursor === null} onClick={() => {setBusy(true); setSelected(''); setValue(null); setItems([]); setCursors(current => [...current, nextCursor]);}}>Sumber berikutnya</button></div>
    {items.length === 0 && <p role="status">Belum ada sumber yang dapat ditinjau dalam cakupan dan izin akun ini.</p>}
    <p className={styles.notice}>Penyesuaian stok: sumber dan pembanding yang disetujui belum tersedia. Hasil hitung fisik tidak dianggap sebagai penyesuaian.</p>
    {error !== null && <p className={common.alert} role="alert">{message(error)}</p>}
    {value && <article><h2>Hasil pemeriksaan #{value.id}</h2><p>Aturan {value.ruleVersion} · Kebijakan {value.policyVersion ?? 'belum tersedia'} · {formatDateTime(value.recordedAt)}</p>
      {!value.current && <p role="alert">Sumber atau kebijakan telah berubah. Buat pemeriksaan baru untuk melanjutkan.</p>}
      <ul className={styles.signals}>{value.signals.map((signal, index) => <li key={signal.code + ':' + signal.reference + ':' + index} data-state={signal.state}>
        <strong>{states[signal.state]}</strong><span>{signal.reference}</span><p>{signal.explanation}</p></li>)}</ul>
      {value.disposition && <div className={styles.notice}><h3>{outcomes[value.disposition.outcome]}</h3><p>{value.disposition.reason}</p>
        <p>Petugas #{value.disposition.reviewerId} · {formatDateTime(value.disposition.recordedAt)}</p></div>}
    </article>}
    {selected && <div className={styles.controls}><label>Alasan pemeriksaan atau tindak lanjut<textarea value={reason} maxLength={1000} disabled={busy || recoveryLocked} onChange={event => setReason(event.target.value)} /></label>
      <button type="button" disabled={busy || !reason.trim() || recoveryLocked} onClick={() => void send('evaluate')}>Buat pemeriksaan baru</button>
      {account.review && value?.current && !value.disposition && <><label>Hasil tinjauan<select value={outcome} disabled={busy || recoveryLocked} onChange={event => setOutcome(event.target.value as AnomalyOutcome)}>
        {Object.entries(outcomes).map(([key, label]) => <option key={key} value={key}>{label}</option>)}</select></label><button type="button" disabled={busy || !reason.trim() || recoveryLocked} onClick={() => void send('review')}>Simpan tinjauan</button></>}
      {uncertain && <button type="button" disabled={busy || storageBlocked} onClick={() => {if (pending.current) void send(pending.current.operation);}}>Ulangi permintaan yang sama</button>}</div>}
    {account.configure && selected && <AnomalyPolicyPanel key={selected} actorId={account.actorId} session={account.session} observedAt={account.observedAt}
      source={items.find(item => item.id === selected)!} evaluation={value} journal={journal} onReferences={readReferences}
      reference={references.find(ref => ref.operation === 'configure' && ref.sourceKind === kind && ref.sourceId === selected) ?? null}
      disabled={busy || storageBlocked || uncertain || references.some(ref => ref.operation !== 'configure' || ref.sourceKind !== kind || ref.sourceId !== selected)}
      onBusy={setBusy} onSaved={() => refresh()} />}
  </section>;
}
