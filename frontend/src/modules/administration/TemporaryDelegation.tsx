'use client';

import { SessionAccessState } from '@/components/workspace-kit/session-access-state';
import {useCallback, useEffect, useRef, useState, type ReactNode} from 'react';
import {RequestNotSentError} from '../../api/client';
import {createIdentityGateway} from '../identity/identity-client';
import {IdentityFailure, sessionTiming, stepUpFields, type IdentitySession} from '../identity/identity-workflow';
import {ProtectedNavigationForm} from '../identity/ProtectedNavigationForm';
import {describeScope} from '../organizations/access-scope';
import {createAccessGateway, type AccessSubject} from './access-client';
import {AccessFailure} from './workflow';
import {AdministrationNavigation} from './AdministrationNavigation';
import {permissionLabel} from './permission-labels';
import {createDelegationGateway, parseDelegationCommand, type DelegationCommand, type DelegationItem, type DelegationSource} from './delegation-client';
import {DelegationJournal, delegationJournalPrefix, type DelegationAction, type DelegationReference} from './delegation-journal';
import styles from './access.module.css';
import { formatDateTime } from '@/lib/format';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { Section } from '@/components/workspace-kit/section';
import { StateView } from '@/components/workspace-kit/state-view';
import { DataList } from '@/components/workspace-kit/data-list';
import { Field } from '@/components/workspace-kit/field';
import { Badge } from '@/components/workspace-kit/badge';
import { Button } from '@/components/workspace-kit/button';

const path = '/administration/delegations';
const names: Record<DelegationAction, string> = {propose: 'Ajukan delegasi', approve: 'Setujui delegasi', revoke: 'Hentikan delegasi', handover: 'Catat serah-terima'};
const states = {pending: 'Menunggu persetujuan', active: 'Disetujui', revoked: 'Dihentikan'};
type Account = {actorId: string; actions: string[]; session: IdentitySession; observed: number};
function utcFromWib(value: string) {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(value)) throw new RequestNotSentError(400, 'delegation_input_invalid');
  const date = new Date(value + '+07:00');
  if (!Number.isFinite(date.getTime())) throw new RequestNotSentError(400, 'delegation_input_invalid'); return date.toISOString();
}
function message(error: unknown) {
  if (error instanceof AccessFailure || error instanceof RequestNotSentError) {
    if (error.code === 'independent_person_required' || error.code === 'independent_actor_required') return 'Pemberi, penerima, dan penyetuju harus merupakan orang yang berbeda.';
    if (error.code === 'delegation_source_changed' || error.code === 'delegation_changed') return 'Akses sumber atau catatan delegasi berubah. Perbarui sebelum meninjau lagi.';
    if (error.code === 'step_up_required') return 'Lakukan verifikasi dua langkah, lalu perbarui sesi sebelum meninjau lagi.';
    if ([401, 403].includes(error.status)) return 'Akses delegasi belum tersedia untuk akun ini. Periksa cakupan delegasi Anda.';
    if (error instanceof RequestNotSentError) return 'Periksa penerima, izin, masa berlaku, dan alasan. Permintaan belum dikirim.';
  }
  return 'Hasil belum dapat dipastikan. Periksa hasil asli yang tersimpan sebelum mengirim perubahan lain.';
}
export function TemporaryDelegation() {
  const [identity] = useState(createIdentityGateway), [access] = useState(createAccessGateway);
  const [account, setAccount] = useState<Account | null>(null), [error, setError] = useState<unknown>(null), [refresh, setRefresh] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void (async () => {try {
      const session = await identity.session(controller.signal), current = await access.current();
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
      if (session.account.userId !== current.userId) throw new AccessFailure(409, 'actor_changed');
      if (active) {setAccount({actorId: current.userId, actions: current.allowedActions, session, observed: performance.now()}); setElapsed(0); setError(null);}
    } catch (failure) {if (active) {setAccount(null); setError(failure);}}})();
    return () => {active = false; controller.abort();};
  }, [identity, access, refresh]);
  useEffect(() => {
    const timer = window.setInterval(() => {if (account) setElapsed(performance.now() - account.observed);}, 1000);
    const restored = (event: PageTransitionEvent) => {if (event.persisted) {setAccount(null); setRefresh(x => x + 1);}};
    window.addEventListener('pageshow', restored); return () => {window.clearInterval(timer); window.removeEventListener('pageshow', restored);};
  }, [account]);
  // The desk owns the session refresh and the verification step, so it renders the header with them; the access states without.
  const header = (actions?: ReactNode) => <PageHeader title="Delegasi sementara" description="Pilih izin dari akses Anda yang masih berlaku. Delegasi memerlukan persetujuan orang lain dan selalu memiliki waktu berakhir." secondaryAction={actions} />;
  return <main className={styles.page}>
    {account && !sessionTiming(account.session, elapsed).expired ? <Desk key={account.actorId + ':' + account.observed} account={account} header={header} alert={error !== null ? message(error) : null} refreshSession={() => {setAccount(null); setRefresh(v => v + 1);}} />
      : <>{header()}<div className="ui-stack">
        {error !== null && account && <StateView kind="error" title={message(error)} />}
        {!account && error === null ? <StateView kind="loading" title="Memeriksa sesi dan akses…" />
          : <SessionAccessState error={error} kind={account ? "expired" : undefined} returnPath={path} onRetry={() => {setAccount(null); setError(null); setRefresh(v => v + 1);}} detail={error !== null ? message(error) : undefined} />}
      </div></>}
  </main>;
}

function Desk({account, refreshSession, header, alert}: {account: Account; refreshSession: () => void; header: (actions?: ReactNode) => ReactNode; alert: string | null}) {
  const [gateway] = useState(createDelegationGateway), [access] = useState(createAccessGateway), [journal] = useState(() => new DelegationJournal(() => window.localStorage));
  const alive = useRef(true), epoch = useRef(0), confirmRegion = useRef<HTMLElement>(null);
  const [items, setItems] = useState<DelegationItem[]>([]), [sources, setSources] = useState<DelegationSource[]>([]), [cursors, setCursors] = useState<(string | null)[]>([null]), [next, setNext] = useState<string | null>(null);
  const [detail, setDetail] = useState<Awaited<ReturnType<typeof gateway.detail>> | null>(null), [busy, setBusy] = useState(true), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState('');
  const [pending, setPending] = useState<DelegationReference[]>([]), [storageBlocked, setStorageBlocked] = useState(false), [reload, setReload] = useState(0);
  const [sourceId, setSourceId] = useState(''), [beneficiaryId, setBeneficiaryId] = useState(''), [subject, setSubject] = useState<AccessSubject | null>(null), [membershipId, setMembershipId] = useState('');
  const [permissions, setPermissions] = useState<string[]>([]), [effective, setEffective] = useState(''), [expiry, setExpiry] = useState(''), [reason, setReason] = useState('');
  const [review, setReview] = useState<DelegationCommand | null>(null), [accepted, setAccepted] = useState(false);
  // A failed list read is the queue's own error state; a failed command keeps the page alert (U-037: one state per region).
  const [readFailed, setReadFailed] = useState(false);
  const cursor = cursors[cursors.length - 1], source = sources.find(row => row.id === sourceId), locked = busy || storageBlocked || pending.length > 0;
  const canPropose = account.actions.includes('access.propose');
  const clearReview = useCallback(() => {setReview(null); setAccepted(false);}, []);
  const context = useCallback((version: number) => ({actorId: account.actorId, isCurrent: () => alive.current && version === epoch.current && !sessionTiming(account.session, performance.now() - account.observed).expired}), [account]);
  const readJournal = useCallback(() => {try {const refs = journal.list(account.actorId); setPending(refs); setStorageBlocked(false); return refs;} catch (failure) {setStorageBlocked(true); setError(failure); throw failure;}}, [journal, account.actorId]);
  useEffect(() => {
    alive.current = true; const lifecycle = epoch;
    const changed = (event: StorageEvent) => {
      if (event.storageArea === window.localStorage && (event.key === null || event.key.startsWith(delegationJournalPrefix(account.actorId)))) {
        epoch.current++; setBusy(true); clearReview(); setDetail(null); setSubject(null); setMembershipId(''); setSourceId(''); setPermissions([]); setEffective(''); setExpiry(''); setReason('');
        try {readJournal(); setNotice('Catatan operasi berubah di tab lain. Data akan dimuat kembali.'); setReload(x => x + 1);} catch { /* Keep the storage refusal visible. */ }
      }
    };
    queueMicrotask(() => {if (alive.current) {try {readJournal();} catch { /* A blocked journal keeps every command disabled. */ }}});
    window.addEventListener('storage', changed); return () => {alive.current = false; lifecycle.current++; window.removeEventListener('storage', changed);};
  }, [account.actorId, readJournal, clearReview]);
  useEffect(() => {
    const version = ++epoch.current; let cancelled = false;
    void (async () => {try {
      const page = await gateway.list(context(version), cursor), choices = canPropose ? await gateway.sources(context(version)) : [];
      if (!cancelled && context(version).isCurrent()) {setItems(page.items); setNext(page.nextId); setSources(choices); setError(null); setReadFailed(false);}
    } catch (failure) {if (!cancelled && alive.current && version === epoch.current) {setItems([]); setSources([]); setError(failure); setReadFailed(true);}}
    finally {if (!cancelled && alive.current && version === epoch.current) setBusy(false);}})();
    return () => {cancelled = true;};
  }, [gateway, context, cursor, reload, canPropose]);
  useEffect(() => {if (review) confirmRegion.current?.focus();}, [review]);
  async function select(id: string) {
    if (locked) return; const version = ++epoch.current; setBusy(true); clearReview(); setReason(''); setDetail(null);
    try {const value = await gateway.detail(id, context(version)); if (context(version).isCurrent()) setDetail(value);}
    catch (failure) {if (alive.current && version === epoch.current) setError(failure);} finally {if (alive.current && version === epoch.current) setBusy(false);}
  }
  async function lookup() {
    if (locked || !/^[1-9][0-9]{0,18}$/.test(beneficiaryId) || beneficiaryId === account.actorId) return;
    const version = ++epoch.current; setBusy(true); setSubject(null); setMembershipId(''); clearReview();
    try {const value = await access.subject(beneficiaryId, account.actorId); if (context(version).isCurrent()) setSubject(value);}
    catch (failure) {if (alive.current && version === epoch.current) setError(failure);} finally {if (alive.current && version === epoch.current) setBusy(false);}
  }
  function preview(action: DelegationAction) {
    if (locked) return;
    try {
      if (readJournal().length !== 0) return;
      const request = action === 'propose' && source && subject && subject.status === 'active' ? {
        sourceGrantId: source.id, sourceVersion: source.version, beneficiaryUserId: subject.userId, beneficiaryMembershipId: membershipId,
        scope: source.scope, permissions: [...permissions].sort(), effectiveAt: utcFromWib(effective), expiresAt: utcFromWib(expiry),
      } : null;
      const value = parseDelegationCommand({action, id: action === 'propose' ? null : detail?.item.id, revision: action === 'propose' ? '0' : detail?.item.revision, request, reason: reason.trim()});
      if (action !== 'propose' && !detail?.item.allowedActions.includes(action)) throw new RequestNotSentError(403, 'delegation_unavailable');
      setReview(value); setAccepted(false); setError(null);
    } catch (failure) {setError(failure);}
  }
  async function send() {
    if (locked || !review || !accepted) return;
    const version = ++epoch.current, input = review;
    const ref: DelegationReference = {actorId: account.actorId, action: input.action, id: input.id, revision: input.revision, key: crypto.randomUUID(), createdAt: new Date().toISOString()};
    setBusy(true); setError(null);
    try {
      if (readJournal().length !== 0) {clearReview(); return;}
      try {journal.remember(ref);} catch (failure) {setStorageBlocked(true); throw failure;} readJournal();
      const result = await gateway.change(input, ref, context(version));
      journal.confirmed(ref); if (context(version).isCurrent()) {readJournal(); clearReview(); setDetail(null); setReason(''); setNotice('Catatan delegasi ' + result.delegationId + ' tersimpan.'); setReload(x => x + 1);}
    } catch (failure) {
      if (failure instanceof RequestNotSentError) {try {journal.confirmed(ref); readJournal();} catch (storageFailure) {setStorageBlocked(true); setError(storageFailure);}}
      if (alive.current) {clearReview(); setError(failure);}
    } finally {if (alive.current && version === epoch.current) setBusy(false);}
  }
  async function recover(ref: DelegationReference) {
    if (busy || storageBlocked) return; const version = ++epoch.current; clearReview(); setBusy(true);
    try {
      const result = await gateway.recover(ref, context(version));
      if (result === null) {setNotice('Hasil asli belum terkonfirmasi. Referensi tetap disimpan; perubahan baru tetap dikunci.'); return;}
      journal.confirmed(ref); if (context(version).isCurrent()) {readJournal(); setDetail(null); setReason(''); setNotice('Hasil asli delegasi ' + result.delegationId + ' terkonfirmasi.'); setReload(x => x + 1);}
    } catch (failure) {if (alive.current) setError(failure);} finally {if (alive.current && version === epoch.current) setBusy(false);}
  }
  let fields: Record<string, string> | null = null;
  const preferredOperation = review?.action === 'approve' ? 'access.approve' : review?.action === 'revoke' ? 'access.revoke' : 'access.propose';
  const operation = account.session.stepUpOperations.includes(preferredOperation) ? preferredOperation :
    ['access.approve', 'access.revoke'].find(value => account.session.stepUpOperations.includes(value)) ?? preferredOperation;
  try {fields = stepUpFields(account.session, operation, path);} catch { /* Only existing provider-authorized operations may start verification. */ }
  return <>
    {header(<><Button variant="secondary" type="button" disabled={busy} onClick={refreshSession}>Muat ulang sesi dan akses</Button>
      {fields && <ProtectedNavigationForm action="/auth/step-up" fields={fields} disabled={busy} label="Verifikasi dua langkah" secondary />}</>)}
    <div className="ui-stack">
    <AdministrationNavigation current="delegations" actions={account.actions} locked={busy || review !== null} />
    {alert !== null && <StateView kind="error" title={alert} />}
    {error !== null && !readFailed && <StateView kind="error" title={message(error)} />}
    {notice && <p role="status">{notice}</p>}
    <p className={styles.help}>Serah-terima di halaman ini menyimpan catatan peserta. Pengambilalihan tugas pemeriksaan pembayaran tersedia pada meja pemeriksaan bagi reviewer berwenang. Jenis tugas lain belum tersedia. Tugas dan persetujuan operasional tetap mengikuti pemilik serta pemisahan tugas masing-masing.</p>
    {storageBlocked && <StateView kind="error" title="Penyimpanan referensi operasi belum tersedia. Pengiriman perubahan dikunci." />}
    {pending.length > 0 && <Section title="Periksa hasil asli" label="Operasi delegasi belum terkonfirmasi" description="Referensi ini tetap tersedia setelah peramban dibuka kembali.">
      <DataList label="Operasi belum terkonfirmasi" items={pending} getKey={ref => ref.key} renderTitle={ref => <>{names[ref.action]} · {ref.id ?? 'pengajuan baru'}</>}
        renderActions={ref => <Button variant="secondary" type="button" disabled={busy || storageBlocked} onClick={() => void recover(ref)}>Periksa hasil asli {ref.key}</Button>} /></Section>}
    <div className="ui-desk" aria-busy={busy}>
      <Section title="Delegasi dalam akses Anda" label="Daftar delegasi">
        <DataList label="Delegasi dalam akses Anda" items={items} getKey={row => row.id} loading={busy && !items.length} disabled={locked || review !== null}
          error={readFailed ? { message: message(error), onRetry: () => {setBusy(true); setReload(x => x + 1);} } : undefined}
          empty={{ title: 'Belum ada catatan pada halaman ini.' }} selectedKey={detail?.item.id ?? null} onSelect={row => void select(row.id)}
          renderTitle={row => <>Delegasi {row.id}</>} renderBadge={row => <Badge status={states[row.status]} />}
          renderMeta={row => <>Penerima {row.request.beneficiaryUserId} · {row.sourceState === 'current' ? 'sumber berlaku' : 'sumber tidak tersedia'}</>}
          pagination={{ start: (cursors.length - 1) * 25 + 1, end: (cursors.length - 1) * 25 + items.length, hasPrevious: cursors.length > 1, hasNext: next !== null, disabled: locked || review !== null,
            onPrevious: () => {setBusy(true); setDetail(null); setCursors(v => v.slice(0, -1));}, onNext: () => {setBusy(true); setDetail(null); setCursors(v => [...v, next]);} }} />
      </Section>
      <div className="ui-stack">
        {canPropose && <Section title="Ajukan delegasi sementara" label="Pengajuan delegasi">
          <div className="ui-form"><fieldset disabled={locked || review !== null}>
            <Field id="delegation-source" label="Akses sumber"><select value={sourceId} onChange={event => {setSourceId(event.target.value); setPermissions([]);}}><option value="">Pilih akses sumber</option>{sources.map(row => <option key={row.id} value={row.id}>{row.id} · {describeScope(row.scope)}</option>)}</select></Field>
            {!sources.length && <p>Tidak ada izin sumber milik akun ini yang dapat didelegasikan. Izin yang belum ditetapkan sebagai dapat didelegasikan tidak ditambahkan otomatis.</p>}
            <Field id="delegation-beneficiary" label="Nomor akun penerima" width="search"><input value={beneficiaryId} onChange={event => {setBeneficiaryId(event.target.value); setSubject(null); setMembershipId('');}} inputMode="numeric" /></Field>
            <Button variant="secondary" type="button" onClick={() => void lookup()}>Periksa penerima</Button>
            <Field id="delegation-membership" label="Keanggotaan penerima"><select value={membershipId} onChange={event => setMembershipId(event.target.value)}><option value="">Pilih keanggotaan aktif</option>{subject?.memberships.filter(row => row.status === 'active').map(row => <option key={row.id} value={row.id}>{row.id} · {describeScope(row.scope)}</option>)}</select></Field>
            {source && <fieldset className={styles.choiceGroup}><legend>Izin yang didelegasikan</legend>{source.permissions.map(permission => <label key={permission} className={styles.checkbox}><input type="checkbox" checked={permissions.includes(permission)} onChange={event => setPermissions(rows => event.target.checked ? [...rows, permission] : rows.filter(p => p !== permission))} />{permissionLabel(permission)}</label>)}</fieldset>}
            <div className={styles.twoFields}><Field id="delegation-effective" label="Mulai berlaku (WIB)"><input type="datetime-local" value={effective} onChange={event => setEffective(event.target.value)} /></Field><Field id="delegation-expiry" label="Berakhir (WIB)"><input type="datetime-local" value={expiry} onChange={event => setExpiry(event.target.value)} /></Field></div>
            {source?.expiresAt && <p className={styles.help}>Izin sumber berakhir {formatDateTime(source.expiresAt)}.</p>}
            <Field id="delegation-reason" label="Alasan delegasi"><textarea value={reason} maxLength={1000} onChange={event => setReason(event.target.value)} /></Field>
            <Button variant="primary" type="button" onClick={() => preview('propose')}>Tinjau pengajuan delegasi</Button>
          </fieldset></div>
        </Section>}
        {detail && <Section title={'Delegasi ' + detail.item.id} label="Rincian delegasi" description={<>{states[detail.item.status]} · {detail.item.sourceState === 'current' ? 'Sumber masih berlaku' : 'Sumber tidak tersedia; akses turunan tidak diberikan'}</>}>
          <p>{describeScope(detail.item.request.scope)}</p><p>Pemberi {detail.item.sourceUserId} · penerima {detail.item.request.beneficiaryUserId} · penyetuju {detail.item.approvedBy ?? 'belum ada'}</p>
          <p>{formatDateTime(detail.item.request.effectiveAt)} sampai {formatDateTime(detail.item.request.expiresAt)}</p>
          <ul>{detail.item.request.permissions.map(permission => <li key={permission}>{permissionLabel(permission)}</li>)}</ul><p className={styles.preserve}>{detail.item.reason}</p>
          <Field id="delegation-decision-reason" label="Alasan keputusan atau catatan serah-terima"><textarea disabled={locked || review !== null} value={reason} maxLength={1000} onChange={event => setReason(event.target.value)} /></Field>
          <div className="ui-actions">{detail.item.allowedActions.map(action => <Button key={action} type="button" variant="secondary" disabled={locked || review !== null} onClick={() => preview(action)}>Tinjau: {names[action]}</Button>)}</div>
          <h3>Riwayat delegasi</h3><ol className={styles.historyList}>{detail.history.map(row => <li key={row.id}><strong>{names[row.action]}</strong><p>Akun {row.actorId} · revisi {row.revision}</p><p className={styles.preserve}>{row.reason}</p></li>)}</ol>
        </Section>}
        {review && <section ref={confirmRegion} tabIndex={-1} className="ui-section" aria-label="Konfirmasi delegasi"><h2>{names[review.action]}</h2>
          {review.request ? <><p>Penerima {review.request.beneficiaryUserId} · keanggotaan {review.request.beneficiaryMembershipId}</p><p>{describeScope(review.request.scope)}</p>
            <p>{formatDateTime(review.request.effectiveAt)} sampai {formatDateTime(review.request.expiresAt)}</p>
            <ul>{review.request.permissions.map(permission => <li key={permission}>{permissionLabel(permission)}</li>)}</ul></> : <p>Delegasi {review.id} · revisi {review.revision}</p>}
          <p className={styles.preserve}>{review.reason}</p><p>Catatan ini tidak memindahkan tugas operasional atau menggantikan persetujuan orang yang berbeda.</p>
          <label className={styles.checkbox}><input type="checkbox" checked={accepted} disabled={locked} onChange={event => setAccepted(event.target.checked)} />Saya sudah memeriksa penerima, izin, waktu, dan alasan.</label>
          <div className="ui-actions"><Button variant="primary" type="button" disabled={locked || !accepted} onClick={() => void send()} busy={busy}>Konfirmasi {names[review.action].toLocaleLowerCase('id-ID')}</Button>
            <Button variant="secondary" type="button" disabled={busy} onClick={clearReview}>Batalkan tinjauan</Button></div>
        </section>}
      </div>
    </div>
  </div></>;
}
