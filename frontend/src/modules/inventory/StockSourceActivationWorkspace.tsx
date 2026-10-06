"use client";

import Link from 'next/link';
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { RequestNotSentError } from '../../api/client';
import { createAccessGateway } from '../administration/access-client';
import { AdministrationNavigation } from '../administration/AdministrationNavigation';
import { OperationRecovery } from '../administration/OperationRecovery';
import { OperationJournal } from '../administration/operation-journal';
import { AccessFailure, accessMessage } from '../administration/workflow';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, identityMessage, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { sourceActor } from './stock-source-workflow';
import { createStockSourceActivationGateway } from './stock-source-activation-client';
import { StockSourceActivationEditor, type ActivationHandoffDraft } from './stock-source-activation-editor-state';
import { PolicyConfigurationFacts } from './StockSourcePolicyForms';
import type { ActivationCommand, ActivationDetail, ActivationHandoff, ActivationPreview } from './stock-source-activation-workflow';
import common from '../administration/access.module.css';
import styles from './stock-source-policy.module.css';

const path = '/inventory/sources/activations';
const loginHref = '/identity/login?returnPath=' + encodeURIComponent(path);
const factorHref = '/identity/account?operation=stock.source.approve&returnPath=' + encodeURIComponent(path);
const status = { proposed: 'Menunggu peninjauan', activated: 'Aktivasi disetujui', rejected: 'Ditolak' };
const domainNames: Record<string, string> = { Orders: 'Pesanan', Reservations: 'Alokasi pesanan', Fulfillment: 'Pengiriman', Procurement: 'Pengadaan',
  AfterSales: 'Purnajual', SourceReconciliation: 'Rekonsiliasi sumber', InventoryOperations: 'Pemeriksaan persediaan' };
const time = (value: string) => new Date(value).toLocaleString('id-ID', { timeZone: 'Asia/Jakarta' }) + ' WIB';
type Loaded = { actorId: string; actions: string[]; session: IdentitySession; observedAt: number; generation: number };
const lostSession = (error: unknown) => (error instanceof AccessFailure || error instanceof IdentityFailure) && (error.status === 401 || error.code === 'actor_changed');
function message(error: unknown) {
  if (error instanceof IdentityFailure) return identityMessage(error);
  if (error instanceof RequestNotSentError) return accessMessage(error);
  if (error instanceof AccessFailure) {
    if (error.code === 'stock_source_activation_input_invalid' || error.code === 'invalid_stock_source_activation')
      return 'Lengkapi acuan dan waktu pemeriksaan. Tulis 0 pada kedua jumlah dan centang ketiga pernyataan serah terima.';
    if (error.code === 'stock_source_nonempty_handoff_required') return 'Sumber ini memiliki catatan stok. Aktivasi sumber kosong tidak dapat digunakan; serah terima stok dan nilainya perlu diselesaikan terlebih dahulu.';
    if (error.code === 'stock_source_activation_policy_required') return 'Kebijakan operasional terbaru harus disetujui sebelum pengajuan aktivasi.';
    if (error.code === 'stock_source_obligations_open' || error.code === 'stock_source_obligations_incomplete' || error.code.includes('coverage_unavailable'))
      return 'Kewajiban atau riwayat sumber belum dapat dinyatakan selesai. Selesaikan catatan yang masih terbuka sebelum melakukan pemeriksaan baru.';
    if (error.code === 'step_up_required') return 'Peninjauan dan pemeriksaan hasil keputusan memerlukan verifikasi dua langkah terbaru.';
    if (error.code.includes('person') || error.code.includes('independen')) return 'Peninjauan harus dilakukan oleh orang yang berbeda dari pengaju, dengan akses yang masih berlaku.';
    if (error.status === 409) return 'Pengaturan, kewajiban, atau waktu serah terima sudah berubah. Muat ulang dan periksa fakta terbaru.';
    if (error.code === 'commit_outcome_unknown') return 'Hasil pengiriman belum dapat dipastikan. Periksa hasil permintaan semula sebelum mengirim permintaan baru.';
    if (error.status >= 500 || error.status === 0) return 'Layanan belum dapat diperiksa. Muat ulang setelah koneksi tersedia. Jika ada referensi permintaan tersimpan, periksa hasilnya terlebih dahulu.';
    return accessMessage(error);
  }
  return 'Koneksi terputus. Periksa hasil melalui referensi pemulihan yang tersimpan.';
}
export function StockSourceActivationWorkspace() {
  const [identity] = useState(() => createIdentityGateway()), [access] = useState(() => createAccessGateway());
  const request = useRef<AbortController | null>(null), generation = useRef(0);
  const [loaded, setLoaded] = useState<Loaded | null>(null), [loading, setLoading] = useState(true), [error, setError] = useState<unknown>(null);
  const refresh = useCallback(async () => {
    request.current?.abort(); const controller = new AbortController(); request.current = controller;
    const current = () => request.current === controller && !controller.signal.aborted;
    setLoaded(null); setLoading(true); setError(null);
    try {
      const session = await identity.session(controller.signal), observedAt = performance.now(); if (!current()) return;
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
      const account = await access.current(); if (!current()) return;
      if (account.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
      const actorId = sourceActor(account.userId);
      if (!Array.isArray(account.allowedActions) || account.allowedActions.length > 200 || account.allowedActions.some(action =>
        typeof action !== 'string' || !/^[a-z]+(?:[.-][a-z]+){1,5}$/.test(action))) throw new AccessFailure(502, 'stock_source_activation_response_invalid');
      if (!['stock.source.read', 'stock.source.propose', 'stock.source.approve'].some(action => account.allowedActions.includes(action))) throw new AccessFailure(403, 'access_denied');
      if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
      setLoaded({ actorId, session, actions: [...account.allowedActions], observedAt, generation: ++generation.current });
    } catch (failure) { if (current()) setError(failure); }
    finally { if (current()) setLoading(false); }
  }, [access, identity]);
  useEffect(() => {
    let active = true; void Promise.resolve().then(() => { if (active) void refresh(); });
    const restored = (event: PageTransitionEvent) => { if (event.persisted) void refresh(); }; window.addEventListener('pageshow', restored);
    return () => { active = false; request.current?.abort(); window.removeEventListener('pageshow', restored); };
  }, [refresh]);
  return <main className={common.page}>
    <a className={styles.skip} href="#activation-workspace">Langsung ke aktivasi sumber</a>
    <header className={common.header}><div><Link className={common.brand} href="/identity/account">Unit Toko</Link>
      <p className={styles.eyebrow}>PENGELOLAAN PERSEDIAAN</p><h1>Aktivasi sumber stok</h1>
      <p>Periksa serah terima sumber kosong dan ajukan peninjauan oleh petugas yang berbeda.</p></div></header>
    <div id="activation-workspace">{loaded ? <ActivationDesk key={loaded.actorId + ':' + loaded.generation} loaded={loaded} refresh={refresh} /> :
      <section className={common.state} aria-busy={loading}><h2>{loading ? 'Memeriksa sesi dan akses' : lostSession(error) ? 'Masuk untuk memeriksa aktivasi' : 'Aktivasi belum tersedia'}</h2>
        {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
        <div className={common.actions}>{lostSession(error) && <Link className={common.primary} href={loginHref}>Masuk kembali</Link>}
          <button className={common.secondary} type="button" disabled={loading} onClick={() => void refresh()}>Periksa kembali</button>
          <Link href="/identity/account">Kembali ke akun saya</Link></div></section>}</div>
  </main>;
}
function ActivationDesk({ loaded, refresh }: { loaded: Loaded; refresh: () => Promise<void> }) {
  const [editor] = useState(() => new StockSourceActivationEditor({ actorId: loaded.actorId, actions: loaded.actions, session: loaded.session,
    observedAt: loaded.observedAt, now: () => performance.now(), gateway: createStockSourceActivationGateway(), journal: new OperationJournal(() => window.localStorage) }));
  const state = useSyncExternalStore(editor.subscribe, editor.snapshot, editor.snapshot), reviewFocus = useRef<HTMLDivElement>(null), resultFocus = useRef<HTMLDivElement>(null);
  useEffect(() => {
    void editor.start(); const timer = window.setInterval(() => editor.checkSession(), 500);
    const changed = (event: StorageEvent) => { if (event.key === null || event.key.startsWith('unit-toko:pending:v1:')) editor.refreshReferences(); };
    const leaving = () => editor.dispose(); window.addEventListener('storage', changed); window.addEventListener('pagehide', leaving);
    return () => { editor.dispose(); window.clearInterval(timer); window.removeEventListener('storage', changed); window.removeEventListener('pagehide', leaving); };
  }, [editor]);
  const warn = state.sending || state.attempt !== null || state.review !== null || state.references.length > 0 || state.recoveryError !== null;
  useEffect(() => { if (!warn) return; const leaving = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ''; };
    window.addEventListener('beforeunload', leaving); return () => window.removeEventListener('beforeunload', leaving); }, [warn]);
  useEffect(() => { if (state.review) reviewFocus.current?.focus(); }, [state.review]);
  useEffect(() => { if (state.result) resultFocus.current?.focus(); }, [state.result]);
  const busy = state.sending || state.recovering, locked = editor.locked;
  if (state.expired || state.suspended) return <section className={common.state}><h2>Sesi dan akses perlu diperiksa kembali</h2>
    {state.error !== null && <p role="alert">{message(state.error)}</p>}<p>Data sebelumnya disembunyikan. Referensi permintaan tetap disimpan untuk pemiliknya.</p>
    <button className={common.primary} type="button" disabled={busy} onClick={() => void refresh()}>Periksa sesi terbaru</button><Link href={loginHref}>Masuk kembali</Link></section>;
  return <>
    <AdministrationNavigation current="source-activations" actions={loaded.actions} locked={locked} />
    <section className={common.accountSection}><div className={styles.heading}><div><h2>Periksa sumber yang ditugaskan</h2>
      <p>Petugas: <strong>{loaded.session.account?.displayName ?? loaded.session.account?.verifiedEmail}</strong></p></div>
      <button className={common.secondary} type="button" disabled={busy || state.review !== null} onClick={() => void refresh()}>Periksa sesi dan akses</button></div>
      {editor.canApprove && <p className={common.help}>{state.fresh ? 'Verifikasi dua langkah masih baru. ' : 'Peninjauan memerlukan verifikasi dua langkah terbaru. '}
        <Link href={factorHref} aria-disabled={busy} onClick={event => { if (busy) event.preventDefault(); }}>Verifikasi untuk peninjauan</Link></p>}
    </section>
    <aside className={styles.notice}><strong>Serah terima ini berlaku untuk sumber yang benar-benar kosong.</strong>
      <p>Pengajuan belum mengaktifkan sumber. Persetujuan hanya mengaktifkan pencatatan stok Unit Toko; jumlah stok, harga, pembayaran, dan invoice tidak diterbitkan oleh langkah ini.</p></aside>
    {state.sending && <p role="status">Menyimpan permintaan… Tunggu hasil sebelum melanjutkan.</p>}
    {state.error !== null && <p role="alert" className={common.alert}>{message(state.error)}</p>}
    {state.recoveryError !== null && <p role="alert" className={common.alert}>{message(state.recoveryError)}</p>}
    {state.references.length > 0 && <section className={common.accountSection}><h2>Periksa permintaan semula</h2>
      <p>Gunakan referensi sumber yang dipakai saat mengirim. Periksa hasil terlebih dahulu; jangan mengulangi pengiriman.</p>
      <label>Referensi sumber semula<input inputMode="numeric" maxLength={19} value={state.recoveryPoolId} disabled={busy || state.review !== null}
        onChange={event => editor.setRecoveryPool(event.target.value)} /></label></section>}
    <OperationRecovery entries={state.references} journal={editor.journal} disabled={state.sending || state.review !== null ||
      !editor.canPropose && !editor.canApprove || !/^[1-9][0-9]{0,18}$/.test(state.recoveryPoolId)}
      recoveryAdapters={{ 'stock.source.activation': { lookup: editor.receipt } }} onResolved={editor.reconciled}
      onFailure={editor.recoveryFailed} onBusyChange={editor.setRecovering} />
    {state.result && <div ref={resultFocus} tabIndex={-1} role="status" className={common.success}><h2>Hasil aktivasi tercatat</h2>
      <p>{status[state.result.status]} · pengajuan {state.result.proposalId} · sumber {state.result.physicalPoolId}.</p>
      <p>{time(state.result.recordedAt)}</p><Link href="/inventory/sources/revisions">Periksa status sumber terbaru</Link></div>}
    {state.attempt && <section className={styles.notice}><h2>Hasil belum terkonfirmasi</h2>
      <p>Permintaan mungkin telah tersimpan. Formulir ditahan sampai hasil permintaan semula dapat dipastikan.</p></section>}
    <section className={common.accountSection}><h2>Pilih sumber stok</h2>
      <p className={common.help}>Gunakan referensi dari pengaturan sumber yang sudah disetujui. <Link href="/inventory/sources/revisions">Lihat pengaturan sumber</Link></p>
      <form className={styles.selection} onSubmit={event => { event.preventDefault(); void editor.loadPool(); }}>
        <label>Referensi sumber stok<input required inputMode="numeric" pattern="[1-9][0-9]{0,18}" maxLength={19} value={state.poolInput}
          disabled={locked || state.loadingPool} onChange={event => editor.changePool(event.target.value)} /></label>
        <button className={common.primary} type="submit" disabled={locked || state.loadingPool}>{state.loadingPool ? 'Memeriksa sumber…' : 'Periksa sumber'}</button>
      </form>
      {state.preview && <section className={styles.preview}><div><PolicyConfigurationFacts configuration={state.preview.configuration} />
        <p>Acuan serah terima yang disetujui: <strong>{state.preview.requiredHandoffReference}</strong></p></div><div><h3>Kewajiban pada pemeriksaan ini</h3>
        <ul className={styles.locations}>{state.preview.obligations.map(item => <li key={item.domain}><strong>{domainNames[item.domain]}</strong>
          <span>Tidak ada kewajiban terbuka yang terkonfirmasi pada pemeriksaan ini.</span></li>)}</ul></div></section>}
      {state.poolId && editor.canPropose && state.preview && <form className={styles.form} onSubmit={event => { event.preventDefault(); editor.reviewProposal(); }}>
        <HandoffFields draft={state.draft} disabled={locked} prefix="maker" onChange={change => editor.changeDraft(change)} />
        <label>Alasan pengajuan<textarea required maxLength={1000} rows={3} value={state.reason} disabled={locked} onChange={event => editor.changeReason(event.target.value)} /></label>
        <button className={common.primary} type="submit" disabled={locked || state.loadingPool}>Tinjau pengajuan</button>
      </form>}
    </section>
    {state.poolId && editor.canApprove && <section className={common.accountSection}><h2>Tinjau pengajuan aktivasi</h2>
      <p>Periksa keadaan fisik secara mandiri. Pengaju dan peninjau harus orang yang berbeda.</p>
      <form className={styles.decision} onSubmit={event => { event.preventDefault(); void editor.loadAction(); }}>
        <fieldset className={common.fields} disabled={locked || state.loadingAction}><legend>Pengajuan dan keputusan</legend>
          <label>Referensi pengajuan<input required inputMode="numeric" pattern="[1-9][0-9]{0,18}" maxLength={19} value={state.proposalInput} onChange={event => editor.changeProposal(event.target.value)} /></label>
          <label>Keputusan<select value={state.action} onChange={event => editor.changeAction(event.target.value === 'reject' ? 'reject' : 'approve')}>
            <option value="approve">Setujui aktivasi</option><option value="reject">Tolak pengajuan</option></select></label>
        </fieldset>
        {state.action === 'approve' && <HandoffFields draft={state.draft} disabled={locked || state.loadingAction} prefix="checker" onChange={change => editor.changeDraft(change)} />}
        <button className={common.secondary} type="submit" disabled={locked || state.loadingAction || !state.fresh}>{state.loadingAction ? 'Memeriksa peninjauan…' : 'Periksa pengajuan dan serah terima'}</button>
      </form>
      {state.actionPreview && <div className={styles.preview}><ActivationFacts detail={state.actionPreview.proposal} /><form className={styles.form} onSubmit={event => { event.preventDefault(); editor.reviewDecision(); }}>
        <label>Alasan keputusan<textarea required maxLength={1000} rows={3} value={state.reason} disabled={locked} onChange={event => editor.changeReason(event.target.value)} /></label>
        <button className={common.primary} type="submit" disabled={locked || !state.fresh}>Tinjau keputusan</button></form></div>}
    </section>}
    {state.review && <div ref={reviewFocus} tabIndex={-1} className={styles.review}><FinalReview key={state.review.operation + state.review.poolId}
      command={state.review} disabled={busy} onCancel={() => editor.cancelReview()} onConfirm={confirmed => void editor.submitReviewed(confirmed)} /></div>}
    {state.poolId && editor.canRead && <section className={styles.history}><header><div className={styles.heading}><div><h2>Riwayat aktivasi</h2>
      <p>Status di bawah adalah keputusan yang tercatat saat itu. Periksa pengaturan sumber untuk mengetahui status terkini.</p></div>
      <button className={common.secondary} type="button" disabled={locked || state.loadingHistory} onClick={() => void editor.loadHistory(state.historyOffset)}>Muat ulang riwayat</button></div></header>
      <div className={styles.historyGrid}><div aria-busy={state.loadingHistory}>
        {state.loadingHistory ? <p role="status">Memuat riwayat…</p> : state.history.items.length === 0 ? <p>Belum ada pengajuan aktivasi pada sumber ini.</p> :
          <ul className={styles.locations}>{state.history.items.map(item => <li key={item.id}><strong>{status[item.status]} · pengajuan {item.id}</strong><span>{time(item.proposedAt)}</span>
            <button className={common.secondary} type="button" disabled={locked || state.loadingDetail} onClick={() => void editor.chooseHistory(item.id)}>Lihat rincian</button></li>)}</ul>}
        <div className={common.actions}><button type="button" className={common.secondary} disabled={locked || state.loadingHistory || state.historyOffset === 0}
          onClick={() => void editor.loadHistory(Math.max(0, state.historyOffset - 25))}>Sebelumnya</button>
          <button type="button" className={common.secondary} disabled={locked || state.loadingHistory || BigInt(state.history.total) <= BigInt(state.historyOffset + 25)}
            onClick={() => void editor.loadHistory(state.historyOffset + 25)}>Berikutnya</button></div></div>
        <div className={styles.detail}>{state.loadingDetail ? <p role="status">Memuat rincian…</p> : state.selected ? <ActivationFacts detail={state.selected} /> : <p>Pilih pengajuan untuk melihat serah terima dan keputusannya.</p>}</div>
      </div></section>}
  </>;
}
function HandoffFields({ draft, disabled, prefix, onChange }: { draft: ActivationHandoffDraft; disabled: boolean; prefix: string; onChange: (change: Partial<ActivationHandoffDraft>) => void }) {
  return <fieldset className={common.fields} disabled={disabled}><legend>Fakta pemeriksaan serah terima saya</legend>
    <label>Acuan serah terima<input required maxLength={160} value={draft.reference} onChange={event => onChange({ reference: event.target.value })} /></label>
    <label>Waktu pemeriksaan (zona waktu perangkat)<input required type="datetime-local" step="1" value={draft.observedAtLocal} onChange={event => onChange({ observedAtLocal: event.target.value })} /></label>
    <label>Jumlah kemasan fisik — tulis 0<input required inputMode="numeric" pattern="0" maxLength={1} value={draft.physicalPackages} onChange={event => onChange({ physicalPackages: event.target.value })} /></label>
    <label>Nilai barang bawaan — tulis 0<input required inputMode="numeric" pattern="0" maxLength={1} value={draft.carriedValueMinorUnits} onChange={event => onChange({ carriedValueMinorUnits: event.target.value })} /></label>
    <label htmlFor={prefix + '-empty'}><input id={prefix + '-empty'} required type="checkbox" checked={draft.emptyPoolConfirmed} onChange={event => onChange({ emptyPoolConfirmed: event.target.checked })} /> Saya memastikan sumber ini kosong.</label>
    <label htmlFor={prefix + '-stopped'}><input id={prefix + '-stopped'} required type="checkbox" checked={draft.previousWriterStopped} onChange={event => onChange({ previousWriterStopped: event.target.checked })} /> Pencatat stok sebelumnya sudah dihentikan.</label>
    <label htmlFor={prefix + '-recorded'}><input id={prefix + '-recorded'} required type="checkbox" checked={draft.noUnrecordedStock} onChange={event => onChange({ noUnrecordedStock: event.target.checked })} /> Tidak ada stok fisik yang belum dicatat.</label>
  </fieldset>;
}
function HandoffFacts({ handoff }: { handoff: ActivationHandoff }) {
  return <dl className={styles.facts}><div><dt>Acuan serah terima</dt><dd>{handoff.reference}</dd></div><div><dt>Waktu pemeriksaan</dt><dd>{time(handoff.observedAt)}</dd></div>
    <div><dt>Kemasan fisik / nilai barang bawaan</dt><dd>0 / 0</dd></div><div><dt>Pernyataan operator</dt><dd>Sumber kosong; pencatat sebelumnya dihentikan; tidak ada stok yang belum dicatat.</dd></div></dl>;
}
function ActivationFacts({ detail }: { detail: ActivationDetail }) {
  return <section><h3>Pengajuan {detail.id} · {status[detail.status]}</h3><p>{time(detail.proposedAt)}</p>
    <HandoffFacts handoff={detail.handoff} /><h4>Alasan pengajuan</h4><p className={common.preserve}>{detail.reason}</p>
    <PolicyConfigurationFacts configuration={detail.configuration} />
    {detail.decision && <section><h4>Keputusan tercatat</h4><p>{time(detail.decision.recordedAt)}</p><p className={common.preserve}>{detail.decision.reason}</p>
      {detail.decision.handoff && <HandoffFacts handoff={detail.decision.handoff} />}</section>}</section>;
}
function FinalReview({ command, disabled, onCancel, onConfirm }: { command: ActivationCommand; disabled: boolean; onCancel: () => void; onConfirm: (confirmed: boolean) => void }) {
  const [confirmed, setConfirmed] = useState(false), deciding = 'proposalId' in command;
  const label = deciding ? command.review.action === 'approve' ? 'Setujui aktivasi sumber' : 'Tolak pengajuan' : 'Kirim pengajuan aktivasi';
  return <form onSubmit={event => { event.preventDefault(); onConfirm(confirmed); }}><h2>Periksa sebelum menyimpan</h2>
    <p>Sumber {command.poolId}{deciding ? ' · pengajuan ' + command.proposalId : ''}</p>
    {deciding ? <ActivationFacts detail={command.review.proposal} /> : <PolicyConfigurationFacts configuration={(command.preview as ActivationPreview).configuration} />}
    {command.body.handoff && <HandoffFacts handoff={command.body.handoff} />}
    <h3>Alasan yang akan dicatat</h3><p className={common.preserve}>{command.body.reason}</p>
    <fieldset className={common.fields} disabled={disabled}><legend>Konfirmasi tindakan</legend>
      <label><input type="checkbox" required checked={confirmed} onChange={event => setConfirmed(event.target.checked)} /> Saya telah memeriksa sumber, serah terima, dan keputusan ini. Tindakan ini tidak menambah stok, menetapkan harga, memproses pembayaran, atau menerbitkan invoice.</label>
    </fieldset><div className={common.actions}><button className={common.primary} type="submit" disabled={disabled || !confirmed}>{label}</button>
      <button className={common.secondary} type="button" disabled={disabled} onClick={onCancel}>Kembali memeriksa</button></div></form>;
}
