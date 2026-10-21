'use client';

import { SessionAccessState, lostAccessKind } from '@/components/workspace-kit/session-access-state';
import Link from 'next/link';
import { RequestNotSentError } from '../../api/client';
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { createAccessGateway } from '../administration/access-client';
import { AdministrationNavigation } from '../administration/AdministrationNavigation';
import { OperationRecovery } from '../administration/OperationRecovery';
import { OperationJournal } from '../administration/operation-journal';
import { AccessFailure, accessMessage } from '../administration/workflow';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, identityMessage, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createPackConversionGateway } from './pack-conversion-client';
import { PackConversionEditor } from './pack-conversion-editor-state';
import { packActor, type PackCommand, type PackPreview, type PackInput, type PackProposal, type PackRole } from './pack-conversion-workflow';
import { stockLocationKey, type StockPackageChoice } from './stock-count-workflow';
import common from '../administration/access.module.css';
import styles from './stock-count.module.css';
import { formatDateTime, numberFormatter } from '@/lib/format';

const path = '/inventory/pack-conversions';
type Loaded = { actorId: string; actions: string[]; session: IdentitySession; observedAt: number; generation: number };
const format = numberFormatter, amount = (value: number | string) => format.format(typeof value === 'string' ? BigInt(value) : value);
const statusLabel = { pending: 'Menunggu peninjauan', approved: 'Disetujui sebagai aturan', rejected: 'Ditolak' };
function message(error: unknown) {
  if (error instanceof RequestNotSentError) return accessMessage(error);
  if (error instanceof IdentityFailure) return identityMessage(error);
  if (error instanceof AccessFailure) {
    if (error.code === 'pack_conversion_selection_required') return 'Pilih gudang serta kemasan sumber dan tujuan dari daftar yang diizinkan.';
    if (error.code === 'pack_conversion_input_invalid') return 'Isi rasio bilangan bulat 1–1.000.000 dan alasan. Jumlah satuan dasar pada kedua sisi harus sama.';
    if (error.code === 'pack_conversion_preview_required') return 'Minta pratinjau terbaru sebelum meninjau pengajuan atau keputusan.';
    if (error.code === 'independent_person_required') return 'Peninjauan memerlukan orang terverifikasi yang berbeda dari pembuat pengajuan. Akun lain milik orang yang sama tidak memenuhi syarat.';
    if (error.code === 'pack_conversion_response_invalid') return 'Respons tidak cocok dengan pilihan yang ditinjau. Data disembunyikan; periksa sesi dan pilihan terbaru.';
    if (error.code === 'pack_conversion_view_stale') return 'Sesi atau pilihan berubah. Periksa kembali; referensi yang belum terkonfirmasi tetap disimpan.';
    if (error.code === 'pack_conversion_rule_changed' || error.code === 'stock_conversion_definition_mismatch') return 'Definisi kemasan atau pengajuan telah berubah. Muat daftar dan minta pratinjau ulang.';
    if (error.code === 'invalid_pack_conversion_rule') return 'Pilihan atau rasio belum dapat diterima. Pilih kemasan tertutup sebagai sumber, satuan/lepas sebagai tujuan, dan rasio bilangan bulat yang tepat.';
    if (error.code === 'stock_conversion_not_conserved' || error.code === 'stock_conversion_incompatible') return 'Kedua kemasan belum cocok atau jumlah satuan dasarnya berbeda. Periksa arah dan rasio; sistem tidak menebak konversi.';
    if (error.code === 'commit_outcome_unknown' || error.status >= 500) return 'Hasil belum dapat dipastikan. Periksa hasil melalui referensi semula; permintaan tidak dikirim ulang.';
  }
  if (!(error instanceof IdentityFailure) && !(error instanceof AccessFailure)) return 'Koneksi terputus. Periksa hasil melalui referensi semula sebelum melanjutkan.';
  return accessMessage(error);
}
export function PackConversionRuleWorkspace() {
  const [access] = useState(() => createAccessGateway()), [identity] = useState(() => createIdentityGateway());
  const request = useRef<AbortController | null>(null), generation = useRef(0);
  const [loaded, setLoaded] = useState<Loaded | null>(null), [error, setError] = useState<unknown>(null), [loading, setLoading] = useState(true);
  const refresh = useCallback(async () => {
    request.current?.abort(); const controller = new AbortController(); request.current = controller;
    const current = () => request.current === controller && !controller.signal.aborted;
    setLoaded(null); setError(null); setLoading(true);
    try {
      const session = await identity.session(controller.signal), observedAt = performance.now(); if (!current()) return;
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
      const account = await access.current(); if (!current()) return;
      if (account.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
      const actorId = packActor(account.userId);
      if (!Array.isArray(account.allowedActions) || account.allowedActions.length > 200 || account.allowedActions.some(action =>
        typeof action !== 'string' || !/^[a-z]+(?:[.-][a-z]+){1,5}$/.test(action))) throw new AccessFailure(502, 'pack_conversion_response_invalid');
      if (!account.allowedActions.includes('stock.convert')) throw new AccessFailure(403, 'access_denied');
      if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
      setLoaded({ actorId, actions: [...account.allowedActions], session, observedAt, generation: ++generation.current });
    } catch (failure) { if (current()) setError(failure); } finally { if (current()) setLoading(false); }
  }, [access, identity]);
  useEffect(() => {
    let active = true; void Promise.resolve().then(() => { if (active) void refresh(); });
    const restored = (event: PageTransitionEvent) => { if (event.persisted) void refresh(); }; window.addEventListener('pageshow', restored);
    return () => { active = false; request.current?.abort(); window.removeEventListener('pageshow', restored); };
  }, [refresh]);
  return <main className={common.page}><header className={common.header}><div><Link className={common.brand} href='/identity/account'>Unit Toko</Link>
    <h1>Aturan konversi kemasan</h1><p>Siapkan dan tinjau aturan membuka kemasan tertutup menjadi satuan/lepas.</p></div></header>
    {loaded ? <RuleDesk key={loaded.actorId + ':' + loaded.generation} loaded={loaded} onRefresh={refresh} /> :
      (loading ? <section className={common.state} aria-busy="true"><h2>Memeriksa sesi dan akses</h2></section>
        : <SessionAccessState error={error} returnPath={path} onRetry={() => void refresh()} detail={error !== null ? message(error) : undefined} />)}
  </main>;
}
function RuleDesk({ loaded, onRefresh }: { loaded: Loaded; onRefresh: () => Promise<void> }) {
  const [editor] = useState(() => new PackConversionEditor({ actorId: loaded.actorId, actions: loaded.actions, session: loaded.session,
    observedAt: loaded.observedAt, now: () => performance.now(), gateway: createPackConversionGateway(), journal: new OperationJournal(() => window.localStorage) }));
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
  const locked = editor.locked, busy = state.sending || state.recovering;
  if (state.expired || state.suspended) return <SessionAccessState error={state.error} kind={state.expired ? 'expired' : lostAccessKind(state.error)} returnPath={path} busy={busy} onRetry={() => void onRefresh()} detail={state.error !== null ? message(state.error) : undefined} />;
  return <>
    <AdministrationNavigation current='pack-conversion-rules' actions={loaded.actions} locked={locked} />
    <section className={common.accountSection} aria-labelledby='pack-context-heading'><div className={styles.headingRow}><div><h2 id='pack-context-heading'>Ruang kerja aturan</h2>
      <p className={common.help}>Petugas: <strong>{loaded.session.account?.displayName ?? loaded.session.account?.verifiedEmail}</strong></p></div>
      <button type='button' className={common.secondary} disabled={busy || state.review !== null} onClick={() => void onRefresh()}>Muat ulang sesi dan akses</button></div>
      <div className={styles.modeChoices}>{([['prepare', 'Siapkan aturan'], ['proposals', 'Pengajuan dan peninjauan'], ['rules', 'Aturan disetujui']] as const).map(([mode, label]) =>
        <button key={mode} type='button' className={state.mode === mode ? common.primary : common.secondary} aria-pressed={state.mode === mode}
          disabled={locked} onClick={() => void editor.changeMode(mode)}>{label}</button>)}</div>
      <label><span id='pack-warehouse-label'>Gudang dan kanal</span><select aria-labelledby='pack-warehouse-label' value={state.warehouse ? stockLocationKey(state.warehouse.location) : ''}
        disabled={locked || state.loadingWarehouses} onChange={event => void editor.chooseWarehouse(state.warehouses.find(item => stockLocationKey(item.location) === event.target.value)?.location ?? null)}>
        <option value=''>Pilih lokasi yang diizinkan</option>{state.warehouses.map(item => <option key={stockLocationKey(item.location)} value={stockLocationKey(item.location)}>
          {[item.organizationName, item.shopName, item.warehouseName, item.channelName].join(' / ')}</option>)}</select></label>
      {state.loadingWarehouses ? <p role='status'>Memuat lokasi yang ditugaskan…</p> : !state.warehouses.length && <p className={common.help}>Belum ada gudang untuk izin ini. Periksa penugasan akun Anda.</p>}
      <button type='button' className={common.secondary} disabled={locked || state.loadingWarehouses} onClick={() => void editor.loadWarehouses()}>Muat ulang lokasi</button>
    </section>
    <aside className={styles.conversion}><strong>Persetujuan aturan tidak memindahkan stok.</strong><p>Pelaksanaan tersedia melalui Pelaksanaan buka kemasan setelah memilih lot sumber yang tersedia dan meninjau jumlah serta nilai. Layar aturan ini tidak mengubah saldo, ketersediaan, reservasi, atau nilai barang.</p></aside>
    {state.error !== null && <p role='alert' className={common.alert}>{message(state.error)}</p>}
    {state.recoveryError !== null && <p role='alert' className={common.alert}>{message(state.recoveryError)}</p>}
    {state.sending && <p role='status'>Menyimpan permintaan… Tunggu hasil sebelum melanjutkan.</p>}
    <OperationRecovery entries={state.references} journal={editor.journal} disabled={busy} onBusyChange={editor.setRecovering} onResolved={editor.reconciled}
      onFailure={editor.recoveryFailed} recoveryAdapters={{ 'inventory.pack-conversion.rule': { lookup: editor.receipt } }} />
    {state.attempt && !state.sending && <section className={common.accountSection}><h2>Hasil belum terkonfirmasi</h2><p>Isian ditahan. Gunakan Periksa hasil pada pemulihan permintaan dengan referensi semula. Hasil yang belum terkonfirmasi belum berarti pengajuan gagal.</p></section>}
    {state.result && <div ref={resultFocus} tabIndex={-1} className={styles.review} role='status'><h2>{state.result.state === 'pending' ? 'Pengajuan aturan tersimpan' : state.result.state === 'approve' ? 'Aturan disetujui' : 'Pengajuan aturan ditolak'}</h2>
      <p>Referensi pengajuan {state.result.proposalId}, versi {state.result.revision}{state.result.ruleId && `; aturan ${state.result.ruleId}`}.</p><p>Stok dan nilai barang tetap. Muat ulang daftar untuk melihat riwayat.</p></div>}
    {!state.warehouse && <section className={common.state}><h2>Pilih gudang untuk melanjutkan</h2><p>Daftar kemasan, pengajuan, dan aturan mengikuti gudang serta kanal yang diizinkan.</p></section>}
    {state.warehouse && state.mode === 'prepare' && <section className={common.accountSection} aria-labelledby='pack-draft-heading'><h2 id='pack-draft-heading'>Arah dan rasio aturan</h2>
      <div className={styles.desk}>{(['source', 'destination'] as const).map(role => <PackageSelector key={role} role={role} editor={editor} locked={locked} />)}</div>
      <form onSubmit={event => { event.preventDefault(); void editor.previewRule(); }}><fieldset className={common.fields} disabled={locked}>
        <label><span id='pack-source-ratio-label'>Jumlah kemasan sumber per langkah</span><input aria-labelledby='pack-source-ratio-label' inputMode='numeric' pattern='[1-9][0-9]*' required maxLength={7}
          value={state.sourceRatio} onChange={event => editor.changeDraft('sourceRatio', event.target.value)} /></label>
        <label><span id='pack-destination-ratio-label'>Jumlah kemasan tujuan per langkah</span><input aria-labelledby='pack-destination-ratio-label' inputMode='numeric' pattern='[1-9][0-9]*' required maxLength={7}
          value={state.destinationRatio} onChange={event => editor.changeDraft('destinationRatio', event.target.value)} /></label>
        <Reason reason={state.reason} onChange={value => editor.changeDraft('reason', value)} />
      </fieldset><p className={common.help}>Masukkan rasio dengan sengaja. Kedua sisi harus mempunyai satuan dasar dan jumlah yang sama; pratinjau server memeriksa kesesuaian varian.</p>
        <div className={common.actions}><button className={common.primary} disabled={locked || state.loadingPreview || state.loadingSource || state.loadingDestination}>Minta pratinjau aturan</button>
          <button type='button' className={common.secondary} disabled={locked || !state.preview || state.loadingPreview} onClick={() => editor.reviewProposal()}>Tinjau pengajuan</button></div>
      </form>{state.loadingPreview && <p role='status'>Memeriksa definisi kemasan dan rasio…</p>}{state.preview && <PreviewFacts preview={state.preview} />}
    </section>}
    {state.warehouse && state.mode === 'proposals' && <section className={common.accountSection} aria-labelledby='pack-proposals-heading'><div className={styles.headingRow}><h2 id='pack-proposals-heading'>Pengajuan aturan dan riwayat</h2>
      <button type='button' className={common.secondary} disabled={locked || state.loadingProposals} onClick={() => void editor.loadProposals()}>Muat ulang pengajuan</button></div>
      <div className={styles.desk}><section className={common.queue}>{state.loadingProposals ? <p role='status'>Memuat pengajuan…</p> : <>
        <ul className={common.queueList}>{state.proposals.items.map(item => <li key={item.id}><button type='button' disabled={locked} className={state.selected?.id === item.id ? common.selectedRow : common.queueRow}
          aria-pressed={state.selected?.id === item.id} onClick={() => void editor.chooseProposal(item.id)}><strong>Pengajuan {item.id}</strong><span>{item.source.skuCode} → {item.destination.skuCode}</span><span>{statusLabel[item.status]}</span>
          {item.status === 'pending' && <span>Buka rincian untuk memeriksa hak peninjauan.</span>}</button></li>)}</ul>
        {!state.proposals.items.length && <p className={common.empty}>Belum ada pengajuan pada halaman lokasi ini.</p>}
        <CursorButtons previous={state.proposalCursor} next={state.proposals.next} disabled={locked} onPage={id => void editor.loadProposals(id)} /></>}</section>
        <section>{state.loadingDetail ? <p role='status'>Memeriksa rincian dan hak peninjauan…</p> : state.selected ? <>
          <h3>Pengajuan {state.selected.id}</h3><p><strong>{statusLabel[state.selected.status]}</strong></p><ProposalFacts proposal={state.selected} />
          {state.selected.status === 'pending' && !state.selected.canReview && <p className={common.help}>Akun ini belum memenuhi peninjauan independen. Pembuat dan akun lain milik orang yang sama tidak dapat memutuskan pengajuan ini.</p>}
          {state.selected.status === 'pending' && state.selected.canReview && <form onSubmit={event => { event.preventDefault(); editor.reviewDecision(); }}>
            <fieldset className={common.fields} disabled={locked}><Reason reason={state.reason} onChange={value => editor.changeDraft('reason', value)} /></fieldset>
            <div className={common.actions}><button type='button' className={common.secondary} disabled={locked || state.loadingDecision} onClick={() => void editor.previewDecision('approve')}>Pratinjau persetujuan</button>
              <button type='button' className={common.secondary} disabled={locked || state.loadingDecision} onClick={() => void editor.previewDecision('reject')}>Pratinjau penolakan</button>
              <button className={common.primary} disabled={locked || !state.decisionPreview || state.loadingDecision}>Tinjau keputusan</button></div>
            {state.loadingDecision && <p role='status'>Memeriksa peninjau independen dan keadaan pengajuan…</p>}
            {state.decisionPreview && <p role='status'>Pratinjau server siap: {state.decisionPreview.action === 'approve' ? 'persetujuan aturan' : 'penolakan pengajuan'}. Keputusan belum disimpan.</p>}
          </form>}
        </> : <><h3>Pilih pengajuan</h3><p>Periksa arah, rasio, dan riwayat sebelum meninjau keputusan.</p></>}</section></div>
    </section>}
    {state.warehouse && state.mode === 'rules' && <section className={common.accountSection} aria-labelledby='pack-rules-heading'><div className={styles.headingRow}><h2 id='pack-rules-heading'>Aturan disetujui</h2>
      <button type='button' className={common.secondary} disabled={locked || state.loadingRules} onClick={() => void editor.loadRules()}>Muat ulang aturan</button></div>
      {state.loadingRules ? <p role='status'>Memuat aturan…</p> : <><ul className={styles.history}>{state.rules.items.map(rule => <li key={rule.id}><h3>Aturan {rule.id}, versi {rule.version}</h3>
        <RatioFacts input={rule.input} source={rule.source} destination={rule.destination} /><p>Disetujui <time dateTime={rule.approvedAt}>{formatDateTime(rule.approvedAt)}</time>.</p>
        <p className={common.help}>Referensi pengajuan {rule.proposalId}; keputusan {rule.decisionId}. Pelaksanaan tersedia melalui Pelaksanaan buka kemasan.</p></li>)}</ul>
        {!state.rules.items.length && <p className={common.empty}>Belum ada aturan disetujui pada halaman lokasi ini.</p>}
        <CursorButtons previous={state.ruleCursor} next={state.rules.next} disabled={locked} onPage={id => void editor.loadRules(id)} /></>}
    </section>}
    {state.review && <div ref={reviewFocus} tabIndex={-1} className={styles.review}><CommandReview key={state.review.operation + ('proposalId' in state.review ? state.review.proposalId : state.review.preview.binding)}
      command={state.review} busy={busy} onConfirm={() => void editor.submitReviewed(true)} onCancel={() => editor.cancelReview()} /></div>}
  </>;
}
function Reason({ reason, onChange }: { reason: string; onChange: (value: string) => void }) {
  return <label><span id='pack-reason-label'>Alasan</span><textarea aria-labelledby='pack-reason-label' required rows={3} maxLength={1000} value={reason} onChange={event => onChange(event.target.value)} /></label>;
}
function PackageSelector({ role, editor, locked }: { role: PackRole; editor: PackConversionEditor; locked: boolean }) {
  const state = editor.snapshot(), source = role === 'source', page = source ? state.sourcePage : state.destinationPage, selected = source ? state.source : state.destination;
  const search = source ? state.sourceSearch : state.destinationSearch, offset = source ? state.sourceOffset : state.destinationOffset, loading = source ? state.loadingSource : state.loadingDestination;
  const label = source ? 'Kemasan sumber tertutup' : 'Kemasan tujuan satuan/lepas';
  return <section aria-busy={loading}><h3>{label}</h3><form onSubmit={event => { event.preventDefault(); void editor.loadPackages(role); }}>
    <label><span id={'pack-' + role + '-search-label'}>Cari {source ? 'sumber' : 'tujuan'}</span><input aria-labelledby={'pack-' + role + '-search-label'} type='search' maxLength={100} disabled={locked} value={search} onChange={event => editor.changeSearch(role, event.target.value)} /></label>
    <button className={common.secondary} disabled={locked || loading}>Cari kemasan {source ? 'sumber' : 'tujuan'}</button></form>
    <label><span id={'pack-' + role + '-label'}>{label}</span><select aria-labelledby={'pack-' + role + '-label'} disabled={locked || loading} value={selected?.skuId ?? ''} onChange={event => editor.choosePackage(role, event.target.value)}>
      <option value=''>Pilih kemasan dengan sengaja</option>{page.items.map(item => <option key={item.skuId} value={item.skuId}>{item.familyName} · {item.skuCode} · {item.packageLabel} · {amount(item.baseUnitsPerPackage)} {item.baseUnitCode}</option>)}</select></label>
    {loading ? <p role='status'>Memuat kemasan {source ? 'sumber' : 'tujuan'}…</p> : !page.items.length && <p className={common.empty}>Belum ada kemasan {source ? 'tertutup' : 'satuan/lepas'} yang sesuai pada halaman ini. Periksa katalog; pilihan tidak ditebak.</p>}
    {selected && <p className={common.help}>Revisi SKU {selected.skuRevision}; definisi {selected.definitionId}, versi {selected.definitionVersion}. Ini pilihan definisi katalog, bukan bukti barang tersedia.</p>}
    <div className={common.actions}><button type='button' className={common.secondary} disabled={locked || loading || offset === 0} onClick={() => void editor.loadPackages(role, offset - 25)}>Sebelumnya</button>
      <button type='button' className={common.secondary} disabled={locked || loading || BigInt(offset + 25) >= BigInt(page.total)} onClick={() => void editor.loadPackages(role, offset + 25)}>Berikutnya</button></div>
  </section>;
}
function CursorButtons({ previous, next, disabled, onPage }: { previous: string | null; next: string | null; disabled: boolean; onPage: (id: string | null) => void }) {
  return <div className={common.actions}><button type='button' className={common.secondary} disabled={disabled || previous === null} onClick={() => onPage(null)}>Ke halaman terbaru</button>
    <button type='button' className={common.secondary} disabled={disabled || next === null} onClick={() => onPage(next)}>Halaman lebih lama</button></div>;
}
function RatioFacts({ input, source, destination }: { input: PackInput; source: StockPackageChoice; destination: StockPackageChoice }) {
  return <><p><strong>{source.familyName}</strong><br />{amount(input.sourcePackagesPerStep)} × {source.packageLabel} ({source.skuCode}) → {amount(input.destinationPackagesPerStep)} × {destination.packageLabel} ({destination.skuCode})</p>
    <p>{amount((BigInt(input.sourcePackagesPerStep) * BigInt(source.baseUnitsPerPackage)).toString())} {source.baseUnitCode} pada setiap sisi per langkah.</p>
    <details><summary>Definisi kemasan yang diikat</summary><p>Sumber: SKU {source.skuId}, revisi {source.skuRevision}, definisi {source.definitionId}, versi {source.definitionVersion}.<br />
      Tujuan: SKU {destination.skuId}, revisi {destination.skuRevision}, definisi {destination.definitionId}, versi {destination.definitionVersion}.</p></details></>;
}
function PreviewFacts({ preview }: { preview: PackPreview }) { return <section className={styles.contents}><h3>Pratinjau aturan dari server</h3><RatioFacts {...preview} />
  <p>Diperiksa <time dateTime={preview.observedAt}>{formatDateTime(preview.observedAt)}</time>. Aturan belum diajukan; barang belum dikonversi.</p></section>; }
function ProposalFacts({ proposal }: { proposal: PackProposal }) { return <><RatioFacts {...proposal} /><p>Alasan pengajuan: {proposal.reason}</p>
  <p>Diajukan <time dateTime={proposal.proposedAt}>{formatDateTime(proposal.proposedAt)}</time>.</p>
  {proposal.decision && <p>Keputusan {proposal.decision.id}: {proposal.decision.action === 'approve' ? 'persetujuan aturan' : 'penolakan'}, {formatDateTime(proposal.decision.decidedAt)}.<br />Alasan: {proposal.decision.reason}</p>}</>; }
function CommandReview({ command, busy, onConfirm, onCancel }: { command: PackCommand; busy: boolean; onConfirm: () => void; onCancel: () => void }) {
  const [confirmed, setConfirmed] = useState(false), decision = 'proposalId' in command;
  const title = decision ? command.action === 'approve' ? 'Konfirmasi persetujuan aturan' : 'Konfirmasi penolakan pengajuan' : 'Konfirmasi pengajuan aturan';
  const facts = decision ? command.preview.proposal : command.preview;
  return <form onSubmit={event => { event.preventDefault(); if (confirmed) onConfirm(); }}><h2>{title}</h2><RatioFacts {...facts} /><p>Alasan: {command.body.reason}</p>
    <p>Gudang {facts.input.location.warehouseId}, kanal {facts.input.location.channelId}. Pilihan dan ikatan pratinjau dibekukan untuk permintaan ini.</p>
    <label className={common.checkbox}><input type='checkbox' required checked={confirmed} disabled={busy} onChange={event => setConfirmed(event.target.checked)} /><span>Saya telah memeriksa arah dan rasio. Saya memahami bahwa persetujuan aturan tidak memindahkan stok atau nilai barang.</span></label>
    <div className={common.actions}><button className={common.primary} disabled={busy || !confirmed}>{title}</button><button type='button' className={common.secondary} disabled={busy} onClick={onCancel}>Kembali ke isian</button></div></form>;
}
