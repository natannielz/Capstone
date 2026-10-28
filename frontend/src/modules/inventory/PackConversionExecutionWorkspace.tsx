'use client';

import { SessionAccessState, lostAccessKind } from '@/components/workspace-kit/session-access-state';
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
import { stockLocationKey } from './stock-count-workflow';
import { sourceActor } from './stock-source-workflow';
import { createPackExecutionGateway } from './pack-conversion-execution-client';
import { PackExecutionEditor } from './pack-conversion-execution-state';
import { conversionTransferredValue, type ConversionCommand, type ConversionPreview, type ConversionSource, type ConversionDetail } from './pack-conversion-execution-workflow';
import common from '../administration/access.module.css';
import styles from './stock-count.module.css';
import { formatDateTime, numberFormatter, formatDate } from '@/lib/format';

const path = '/inventory/pack-conversion-executions';
type Loaded = { actorId: string; actions: string[]; session: IdentitySession; observedAt: number; generation: number };
const format = numberFormatter, amount = (value: string | number) => format.format(typeof value === 'string' ? BigInt(value) : value);
const valueLabel = (value: string) => amount(value.split('.')[0]) + ',' + value.split('.')[1];
function message(error: unknown): string {
  if (error instanceof RequestNotSentError) return accessMessage(error);
  if (error instanceof IdentityFailure) return identityMessage(error);
  if (error instanceof AccessFailure) {
    if (error.code === 'pack_execution_selection_required') return 'Pilih gudang, aturan disetujui, dan lot sumber yang tersedia dari daftar.';
    if (error.code === 'pack_execution_input_invalid') return 'Isi jumlah langkah bilangan bulat sesuai batas lot (maksimal 1.000.000) dan alasan hingga 1.000 karakter.';
    if (error.code === 'pack_execution_preview_required') return 'Minta pratinjau terbaru sebelum meninjau pelaksanaan.';
    if (error.code === 'pack_execution_response_invalid') return 'Respons tidak sesuai dengan aturan, lot, atau jumlah yang dipilih. Pilih Coba lagi untuk memuat aturan dan lot terbaru.';
    if (error.code === 'pack_execution_view_stale' || error.code === 'recovery_request_stale') return 'Sesi atau pilihan berubah. Periksa kembali; referensi semula tetap disimpan.';
    if (['pack_conversion_changed', 'pack_conversion_rule_changed', 'stock_balance_revision_conflict', 'lot_value_revision_conflict', 'insufficient_available_stock', 'stock_conversion_definition_mismatch'].includes(error.code))
      return 'Aturan, ketersediaan, atau nilai lot telah berubah. Muat sumber terbaru dan minta pratinjau ulang.';
    if (error.code === 'commit_outcome_unknown' || error.status >= 500) return 'Hasil belum dapat dipastikan. Periksa hasil melalui referensi semula; permintaan tidak dikirim ulang.';
    return accessMessage(error);
  }
  return 'Koneksi terputus. Periksa hasil melalui referensi semula sebelum melanjutkan.';
}
export function PackConversionExecutionWorkspace() {
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
      const actorId = sourceActor(account.userId);
      if (!Array.isArray(account.allowedActions) || account.allowedActions.length > 200 || account.allowedActions.some(action =>
        typeof action !== 'string' || !/^[a-z]+(?:[.-][a-z]+){1,5}$/.test(action))) throw new AccessFailure(502, 'pack_execution_response_invalid');
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
    <h1>Pelaksanaan buka kemasan</h1><p>Konversikan lot kemasan tertutup yang tersedia memakai aturan disetujui.</p></div></header>
    {loaded ? <ExecutionDesk key={loaded.actorId + ':' + loaded.generation} loaded={loaded} onRefresh={refresh} /> :
      (loading ? <section className={common.state} aria-busy="true"><h2>Memeriksa sesi dan akses</h2></section>
        : <SessionAccessState error={error} returnPath={path} onRetry={() => void refresh()} detail={error !== null ? message(error) : undefined} />)}
  </main>;
}
function ExecutionDesk({ loaded, onRefresh }: { loaded: Loaded; onRefresh: () => Promise<void> }) {
  const [editor] = useState(() => new PackExecutionEditor({ actorId: loaded.actorId, actions: loaded.actions, session: loaded.session,
    observedAt: loaded.observedAt, now: () => performance.now(), gateway: createPackExecutionGateway(), journal: new OperationJournal(() => window.localStorage) }));
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
    <AdministrationNavigation current='pack-conversion-executions' actions={loaded.actions} locked={locked} />
    <section className={common.accountSection} aria-labelledby='execution-context-heading'><div className={styles.headingRow}><div><h2 id='execution-context-heading'>Gudang dan aturan disetujui</h2>
      <p className={common.help}>Petugas: <strong>{loaded.session.account?.displayName ?? loaded.session.account?.verifiedEmail}</strong></p></div>
      <button type='button' className={common.secondary} disabled={busy || state.review !== null} onClick={() => void onRefresh()}>Muat ulang sesi dan akses</button></div>
      <label><span id='execution-warehouse-label'>Gudang dan kanal</span><select aria-labelledby='execution-warehouse-label' value={state.warehouse ? stockLocationKey(state.warehouse.location) : ''}
        disabled={locked || state.loadingWarehouses} onChange={event => void editor.chooseWarehouse(event.target.value)}><option value=''>Pilih lokasi yang diizinkan</option>
        {state.warehouses.map(item => <option key={stockLocationKey(item.location)} value={stockLocationKey(item.location)}>{[item.organizationName, item.shopName, item.warehouseName, item.channelName].join(' / ')}</option>)}</select></label>
      {state.loadingWarehouses ? <p role='status'>Memuat lokasi yang ditugaskan…</p> : !state.warehouses.length && <p className={common.help}>Belum ada gudang untuk izin ini. Periksa penugasan akun Anda.</p>}
      <button type='button' className={common.secondary} disabled={locked || state.loadingWarehouses} onClick={() => void editor.loadWarehouses()}>Muat ulang lokasi</button>
      {state.warehouse && <><label><span id='execution-rule-label'>Aturan disetujui</span><select aria-labelledby='execution-rule-label' value={state.rule?.id ?? ''}
        disabled={locked || state.loadingRules} onChange={event => void editor.chooseRule(event.target.value)}><option value=''>Pilih arah kemasan yang disetujui</option>
        {state.rules.items.map(rule => <option key={rule.id} value={rule.id}>{rule.source.familyName} · {rule.input.sourcePackagesPerStep} × {rule.source.packageLabel} ({rule.source.skuCode}) → {rule.input.destinationPackagesPerStep} × {rule.destination.packageLabel} ({rule.destination.skuCode})</option>)}</select></label>
        {state.loadingRules ? <p role='status'>Memuat aturan disetujui…</p> : !state.rules.items.length && <p className={common.empty}>Belum ada aturan disetujui pada halaman lokasi ini. Siapkan dan tinjau aturan melalui Aturan buka kemasan.</p>}
        <CursorButtons cursor={state.ruleCursor} next={state.rules.next} disabled={locked || state.loadingRules} onPage={cursor => void editor.loadRules(cursor)} />
        <button type='button' className={common.secondary} disabled={locked || state.loadingRules} onClick={() => void editor.loadRules()}>Muat ulang aturan</button></>}
    </section>
    <aside className={styles.conversion}><strong>Pelaksanaan ini memindahkan jumlah dan nilai barang antar-kemasan.</strong><p>Persetujuan aturan saja belum memindahkan stok. Konversi ini tidak mencatat penjualan, tagihan, pembayaran, atau harga pokok penjualan.</p></aside>
    {state.error !== null && <p role='alert' className={common.alert}>{message(state.error)}</p>}
    {state.recoveryError !== null && <p role='alert' className={common.alert}>{message(state.recoveryError)}</p>}
    {state.sending && <p role='status'>Mencatat konversi… Tunggu hasil sebelum melanjutkan.</p>}
    <OperationRecovery entries={state.references} journal={editor.journal} disabled={busy} onBusyChange={editor.setRecovering} onResolved={editor.reconciled}
      onFailure={editor.recoveryFailed} recoveryAdapters={{ 'inventory.pack-conversion.execute': { lookup: editor.receipt } }} />
    {state.attempt && !state.sending && <section className={common.accountSection}><h2>Hasil pelaksanaan belum terkonfirmasi</h2><p>Isian ditahan. Gunakan Periksa hasil dengan referensi semula sebelum melakukan konversi lain. Hasil yang belum terkonfirmasi belum berarti pelaksanaan gagal.</p></section>}
    {state.result && <div ref={resultFocus} tabIndex={-1} className={styles.review} role='status'><h2>Konversi tercatat</h2>
      <p>{amount(state.result.sourcePackages)} kemasan sumber → {amount(state.result.destinationPackages)} kemasan tujuan; {amount(state.result.baseUnits)} satuan dasar tetap.</p>
      <p>Nilai dipindahkan: {valueLabel(state.result.transferredValue)}.</p><p>Referensi konversi {state.result.conversionId}, versi {state.result.revision}. Muat riwayat dan sumber terbaru sebelum melanjutkan.</p></div>}
    {!state.warehouse && <section className={common.state}><h2>Pilih gudang untuk melanjutkan</h2><p>Aturan, ketersediaan lot, dan riwayat mengikuti gudang serta kanal yang diizinkan.</p></section>}
    {state.rule && <section className={common.accountSection} aria-labelledby='execution-source-heading'><div className={styles.headingRow}><h2 id='execution-source-heading'>Lot sumber yang tersedia</h2>
      <button type='button' className={common.secondary} disabled={locked || state.loadingSources} onClick={() => void editor.loadSources()}>Muat ulang sumber</button></div>
      <label><span id='execution-source-label'>Lot penerimaan</span><select aria-labelledby='execution-source-label' value={state.source?.sourceBalanceId ?? ''}
        disabled={locked || state.loadingSources} onChange={event => editor.chooseSource(event.target.value)}><option value=''>Pilih lot dengan sengaja</option>
        {state.sources.items.map(source => <option key={source.sourceBalanceId} value={source.sourceBalanceId} disabled={source.maximumSteps === '0'}>
          {source.receiptReference} · {source.lotReference} · {source.source.packageLabel} · tersedia {amount(source.availableSourcePackages)} · kedaluwarsa {source.expiresOn ? formatDate(source.expiresOn) : 'tidak ditetapkan'}</option>)}</select></label>
      {state.loadingSources ? <p role='status'>Memeriksa lot, ketersediaan, dan nilai sumber…</p> : !state.sources.items.length && <p className={common.empty}>Belum ada lot yang tersedia untuk aturan ini pada halaman sumber. Pastikan penerimaan telah dinilai dan dilepas; barang yang ditahan, kedaluwarsa, atau direservasi tidak dipakai.</p>}
      {state.sources.unavailableCount !== '0' && <p className={common.help}>{amount(state.sources.unavailableCount)} saldo pada halaman ini tidak mempunyai jumlah tersedia untuk satu langkah.</p>}
      <CursorButtons cursor={state.sourceCursor} next={state.sources.next} disabled={locked || state.loadingSources} onPage={cursor => void editor.loadSources(cursor)} />
      {state.source && <SourceFacts source={state.source} />}
      <form onSubmit={event => { event.preventDefault(); void editor.preview(); }}><fieldset className={common.fields} disabled={locked || !state.source}>
        <label><span id='execution-steps-label'>Jumlah langkah konversi</span><input aria-labelledby='execution-steps-label' aria-describedby='execution-steps-help' type='number' min='1' max={state.source ? (BigInt(state.source.maximumSteps) > BigInt("1000000") ? '1000000' : state.source.maximumSteps) : '1000000'} step='1' required
          inputMode='numeric' value={state.steps} onChange={event => editor.change('steps', event.target.value)} /></label>
        <label><span id='execution-reason-label'>Alasan pelaksanaan</span><textarea aria-labelledby='execution-reason-label' required rows={3} maxLength={1000} value={state.reason} onChange={event => editor.change('reason', event.target.value)} /></label>
      </fieldset><p id='execution-steps-help' className={common.help}>Satu langkah memakai {amount(state.rule.input.sourcePackagesPerStep)} {state.rule.source.packageLabel} dan menghasilkan {amount(state.rule.input.destinationPackagesPerStep)} {state.rule.destination.packageLabel}. Batas mengikuti jumlah tersedia pada lot.</p>
        <div className={common.actions}><button className={common.primary} disabled={locked || !state.source || state.loadingPreview || state.loadingSources}>Minta pratinjau pelaksanaan</button>
          <button type='button' className={common.secondary} disabled={locked || !state.preview || state.loadingPreview} onClick={() => editor.review()}>Tinjau pelaksanaan</button></div>
      </form>{state.loadingPreview && <p role='status'>Memeriksa stok dan nilai terbaru…</p>}{state.preview && <PreviewFacts preview={state.preview} />}
    </section>}
    {state.warehouse && <section className={common.accountSection} aria-labelledby='execution-history-heading'><div className={styles.headingRow}><h2 id='execution-history-heading'>Riwayat konversi fisik</h2>
      <button type='button' className={common.secondary} disabled={locked || state.loadingHistory} onClick={() => void editor.loadHistory()}>Muat ulang riwayat</button></div>
      {state.loadingHistory ? <p role='status'>Memuat konversi yang tercatat…</p> : <><ul className={styles.history}>{state.history.items.map(item => <li key={item.id}><HistoryFacts item={item} /></li>)}</ul>
        {!state.history.items.length && <p className={common.empty}>Belum ada konversi pada halaman riwayat ini. Gunakan Muat ulang riwayat untuk memeriksa hasil terbaru.</p>}
        <CursorButtons cursor={state.historyCursor} next={state.history.next} disabled={locked} onPage={cursor => void editor.loadHistory(cursor)} /></>}
    </section>}
    {state.review && <div ref={reviewFocus} tabIndex={-1} className={styles.review}><ExecutionReview key={state.review.preview.binding} command={state.review} busy={busy}
      onConfirm={() => void editor.execute(true)} onCancel={() => editor.cancelReview()} /></div>}
  </>;
}
function CursorButtons({ cursor, next, disabled, onPage }: { cursor: string | null; next: string | null; disabled: boolean; onPage: (cursor: string | null) => void }) {
  return <div className={common.actions}><button type='button' className={common.secondary} disabled={disabled || cursor === null} onClick={() => onPage(null)}>Ke halaman terbaru</button>
    <button type='button' className={common.secondary} disabled={disabled || next === null} onClick={() => onPage(next)}>Halaman berikutnya</button></div>;
}
function SourceFacts({ source }: { source: ConversionSource }) {
  return <section className={styles.contents}><h3>{source.source.familyName} · {source.source.packageLabel}</h3><p>Penerimaan: {source.receiptReference}. Lot: {source.lotReference}.</p>
    <p>Tersedia {amount(source.availableSourcePackages)} kemasan; maksimal {amount(BigInt(source.maximumSteps) > BigInt("1000000") ? '1000000' : source.maximumSteps)} langkah.</p>
    <p>Kedaluwarsa: {source.expiresOn ? formatDate(source.expiresOn) : 'tidak ditetapkan'}. Nilai lot tersisa: {valueLabel(source.remainingValue)} untuk {amount(source.remainingPackages)} kemasan.</p>
    <p className={common.help}>Jumlah tersedia mengecualikan reservasi, barang ditahan, dan kedaluwarsa. Nilai lot dapat mencakup kemasan yang direservasi. Pratinjau memeriksa saldo terbaru.</p></section>;
}
function PreviewFacts({ preview }: { preview: ConversionPreview }) {
  return <section className={styles.contents}><h3>Pratinjau pelaksanaan dari server</h3><p><strong>{amount(preview.sourcePackages)} × {preview.source.packageLabel} ({preview.source.skuCode}) → {amount(preview.destinationPackages)} × {preview.destination.packageLabel} ({preview.destination.skuCode})</strong></p>
    <p>{amount(preview.baseUnits)} {preview.source.baseUnitCode} tetap. Saldo sumber: {amount(preview.stockBefore.onHand)} → {amount(preview.stockAfter.onHand)} kemasan; reservasi {amount(preview.stockBefore.reserved)} tetap.</p>
    <p>Nilai dipindahkan: {valueLabel(conversionTransferredValue(preview))}. Kebijakan: proporsi nilai lot tersisa, dibulatkan ke bawah sampai dua desimal; sisa pembulatan tetap di lot sumber.</p>
    <p>Diperiksa <time dateTime={preview.observedAt}>{formatDateTime(preview.observedAt)}</time>. Konversi belum dicatat.</p></section>;
}
function HistoryFacts({ item }: { item: ConversionDetail }) {
  return <><h3>Konversi {item.id}</h3><p>{item.source.familyName} · {amount(item.sourcePackages)} {item.source.packageLabel} ({item.source.skuCode}) → {amount(item.destinationPackages)} {item.destination.packageLabel} ({item.destination.skuCode})</p>
    <p>Penerimaan {item.receiptReference}; lot sumber {item.sourceLotReference}. Nilai dipindahkan: {valueLabel(item.transferredValue)}.</p>
    <p>{amount(item.baseUnits)} {item.source.baseUnitCode} tetap. Saldo sumber {amount(item.stockBefore.onHand)} → {amount(item.stockAfter.onHand)}; saldo tujuan baru {amount(item.destinationAfter.onHand)} kemasan.</p>
    <p>Alasan: {item.reason}</p><p>Dicatat <time dateTime={item.recordedAt}>{formatDateTime(item.recordedAt)}</time>.</p>
    <details><summary>Referensi aturan dan lot</summary><p>Aturan {item.input.ruleId}, versi {item.input.ruleVersion}; lot sumber {item.stockBefore.lotId}; lot tujuan {item.destinationAfter.lotId}. Versi saldo sumber {item.stockBefore.revision} → {item.stockAfter.revision}.</p></details></>;
}
function ExecutionReview({ command, busy, onConfirm, onCancel }: { command: ConversionCommand; busy: boolean; onConfirm: () => void; onCancel: () => void }) {
  const [confirmed, setConfirmed] = useState(false);
  return <form onSubmit={event => { event.preventDefault(); if (confirmed) onConfirm(); }}><h2>Konfirmasi pelaksanaan buka kemasan</h2><PreviewFacts preview={command.preview} /><p>Alasan: {command.body.reason}</p>
    <label className={common.checkbox}><input type='checkbox' required checked={confirmed} disabled={busy} onChange={event => setConfirmed(event.target.checked)} /><span>Saya telah memeriksa lot, arah kemasan, jumlah, dan nilai. Saya memahami bahwa pelaksanaan ini memindahkan stok dan nilai barang.</span></label>
    <div className={common.actions}><button className={common.primary} disabled={busy || !confirmed}>Catat konversi fisik</button><button type='button' className={common.secondary} disabled={busy} onClick={onCancel}>Kembali ke isian</button></div></form>;
}
