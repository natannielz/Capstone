"use client";

import { SessionAccessState, lostAccessKind } from '@/components/workspace-kit/session-access-state';
import Link from 'next/link';
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { RequestNotSentError } from '../../api/client';
import { createAccessGateway } from '../administration/access-client';
import { AdministrationNavigation } from '../administration/AdministrationNavigation';
import { OperationRecovery } from '../administration/OperationRecovery';
import { OperationJournal } from '../administration/operation-journal';
import { AccessFailure, accessMessage, businessRefusal } from '../administration/workflow';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, identityMessage, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { sourceActor } from './stock-source-workflow';
import { createStockReleaseGateway } from './stock-release-client';
import { StockReleaseEditor } from './stock-release-editor-state';
import { releaseChoiceKey, releaseLocationKey, type ReleasePreview, type ReleaseReview, type ReleaseCommand } from './stock-release-workflow';
import common from '../administration/access.module.css';
import styles from './stock-source-policy.module.css';
import { formatDateTime, formatMoney } from "@/lib/format";
import { Button } from '@/components/workspace-kit/button';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { Section } from '@/components/workspace-kit/section';
import { StateView } from '@/components/workspace-kit/state-view';
import { DataList } from '@/components/workspace-kit/data-list';
import { Badge } from '@/components/workspace-kit/badge';
import { Facts } from '@/components/workspace-kit/facts';

const path = '/inventory/stock-releases';
const factor = '/identity/account?operation=stock.release.approve&returnPath=' + encodeURIComponent(path);
const status = { proposed: 'Menunggu peninjauan', approved: 'Pelepasan disetujui', rejected: 'Ditolak' };
const time = (value: string) => formatDateTime(value);
const amount = (value: string) => formatMoney('IDR', value);
type Loaded = { actorId: string; actions: string[]; session: IdentitySession; observedAt: number; generation: number };
function message(error: unknown) {
  if ((error instanceof AccessFailure || error instanceof RequestNotSentError) && error.code === 'antiforgery_failed')
    return 'Perlindungan halaman tidak lagi berlaku. Muat ulang sesi dan akses, atau masuk kembali; referensi permintaan semula tetap disimpan.';
  if (error instanceof IdentityFailure) return identityMessage(error);
  if (error instanceof RequestNotSentError) return accessMessage(error);
  if (error instanceof AccessFailure) {
    if (error.code === 'stock_release_input_invalid' || error.code === 'invalid_stock_release') return 'Pilih barang dari daftar, isi jumlah kemasan yang dapat dilepas dan tulis alasan.';
    if (error.code === 'step_up_required') return 'Periksa verifikasi dua langkah terbaru sebelum meninjau atau memeriksa hasil keputusan.';
    if (businessRefusal(error.code)) return 'Peninjau harus orang yang berbeda dari pengaju, dengan penugasan yang masih berlaku.';
    if (error.code === 'stock_release_response_invalid') return 'Data pelepasan belum dapat dibaca dengan aman. Pilih Coba lagi untuk memuat data terbaru.';
    if (error.code === 'commit_outcome_unknown') return 'Hasil pengiriman belum dapat dipastikan. Periksa permintaan semula sebelum mengirim perubahan baru.';
    if (error.status === 409) return 'Stok, nilai atau persetujuan sudah berubah. Muat ulang pilihan dan lakukan pemeriksaan terbaru.';
    if (error.status >= 500 || error.status === 0) return 'Layanan belum dapat diperiksa. Referensi permintaan tersimpan tetap dapat digunakan setelah koneksi pulih.';
    return accessMessage(error);
  }
  return 'Koneksi terputus. Periksa hasil permintaan semula sebelum melanjutkan.';
}
export function StockReleaseWorkspace() {
  const [identity] = useState(() => createIdentityGateway()), [access] = useState(() => createAccessGateway());
  const controller = useRef<AbortController | null>(null), generation = useRef(0);
  const [loaded, setLoaded] = useState<Loaded | null>(null), [loading, setLoading] = useState(true), [error, setError] = useState<unknown>(null);
  const refresh = useCallback(async () => {
    controller.current?.abort(); const request = new AbortController(); controller.current = request;
    const current = () => controller.current === request && !request.signal.aborted;
    setLoaded(null); setLoading(true); setError(null);
    try {
      const session = await identity.session(request.signal), observedAt = performance.now(); if (!current()) return;
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
      const account = await access.current(); if (!current()) return;
      if (account.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
      const actorId = sourceActor(account.userId);
      if (!Array.isArray(account.allowedActions) || account.allowedActions.length > 200 || account.allowedActions.some(action => typeof action !== 'string' || !/^[a-z]+(?:[.-][a-z]+){1,5}$/u.test(action)))
        throw new AccessFailure(502, 'stock_release_response_invalid');
      if (!account.allowedActions.some(action => action === 'stock.release.propose' || action === 'stock.release.approve')) throw new AccessFailure(403, 'access_denied');
      if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
      setLoaded({ actorId, actions: [...account.allowedActions], session, observedAt, generation: ++generation.current });
    } catch (failure) { if (current()) setError(failure); }
    finally { if (current()) setLoading(false); }
  }, [access, identity]);
  useEffect(() => {
    let active = true; void Promise.resolve().then(() => { if (active) void refresh(); });
    const restored = (event: PageTransitionEvent) => { if (event.persisted) void refresh(); }; window.addEventListener('pageshow', restored);
    return () => { active = false; controller.current?.abort(); window.removeEventListener('pageshow', restored); };
  }, [refresh]);
  return <main className={common.page}>
    <a className={styles.skip} href="#release-workspace">Langsung ke pelepasan stok</a>
    <PageHeader title="Pelepasan stok ditahan" description="Periksa kemasan, lot dan jumlah barang yang mutu serta nilainya sudah disetujui." />
    <div id="release-workspace" className="ui-stack">{loaded ? <ReleaseDesk key={loaded.actorId + ':' + loaded.generation} loaded={loaded} refresh={refresh} /> :
      (loading ? <StateView kind="loading" title="Memeriksa sesi dan akses" />
        : <SessionAccessState error={error} returnPath={path} onRetry={() => void refresh()} detail={error !== null ? message(error) : undefined} />)}</div>
  </main>;
}
function ReleaseDesk({ loaded, refresh }: { loaded: Loaded; refresh: () => Promise<void> }) {
  const [editor] = useState(() => new StockReleaseEditor({ actorId: loaded.actorId, actions: loaded.actions, session: loaded.session, observedAt: loaded.observedAt,
    now: () => performance.now(), gateway: createStockReleaseGateway(), journal: new OperationJournal(() => window.localStorage) }));
  const state = useSyncExternalStore(editor.subscribe, editor.snapshot, editor.snapshot), reviewFocus = useRef<HTMLDivElement>(null), resultFocus = useRef<HTMLDivElement>(null);
  const [confirmation, setConfirmation] = useState<{ command: ReleaseCommand | null; checked: boolean }>({ command: null, checked: false });
  const confirmed = confirmation.command === state.review && confirmation.checked;
  useEffect(() => {
    void editor.start(); const timer = window.setInterval(editor.checkSession, 500);
    const changed = (event: StorageEvent) => { if (event.key === null || event.key.startsWith('unit-toko:pending:v1:')) editor.refreshReferences(); };
    const leave = () => editor.dispose(); window.addEventListener('storage', changed); window.addEventListener('pagehide', leave);
    return () => { editor.dispose(); window.clearInterval(timer); window.removeEventListener('storage', changed); window.removeEventListener('pagehide', leave); };
  }, [editor]);
  useEffect(() => { if (state.review) reviewFocus.current?.focus(); }, [state.review]);
  useEffect(() => { if (state.result) resultFocus.current?.focus(); }, [state.result]);
  const warn = state.sending || state.attempt !== null || state.review !== null || state.references.length > 0;
  useEffect(() => { if (!warn) return; const leave = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ''; };
    window.addEventListener('beforeunload', leave); return () => window.removeEventListener('beforeunload', leave); }, [warn]);
  const locked = editor.locked, busy = state.sending || state.recovering;
  if (state.expired || state.suspended) return <SessionAccessState error={state.error} kind={state.expired ? 'expired' : lostAccessKind(state.error)} returnPath={path} busy={busy} onRetry={() => void refresh()} detail={state.error !== null ? message(state.error) : undefined} />;
  return <>
    <AdministrationNavigation current="stock-releases" actions={loaded.actions} locked={locked} />
    <Section title="Gudang yang ditugaskan" description={<>Petugas: <strong>{loaded.session.account?.displayName ?? loaded.session.account?.verifiedEmail}</strong></>}
      actions={<Button variant="secondary" disabled={busy || state.review !== null} onClick={() => void refresh()}>Muat ulang sesi dan akses</Button>}>
      {editor.canApprove && <p className={common.help}>{state.fresh ? 'Verifikasi dua langkah masih baru. ' : 'Peninjauan memerlukan verifikasi dua langkah terbaru. '}
        <Link href={factor} aria-disabled={busy} onClick={event => { if (busy) event.preventDefault(); }}>Verifikasi untuk peninjauan</Link></p>}
    </Section>
    <aside className={styles.notice}><strong>Pelepasan mengikuti mutu dan penilaian yang sudah disetujui.</strong>
      <p>Pengajuan memerlukan peninjauan oleh orang yang berbeda. Persetujuan memindahkan jumlah terpilih dari stok ditahan ke stok tersedia beserta nilai yang tercatat.</p></aside>
    {state.sending && <p role="status">Menyimpan permintaan… Tunggu hasil sebelum melanjutkan.</p>}
    {state.error !== null && <StateView kind="error" title={message(state.error)} />}
    {state.recoveryError !== null && <StateView kind="error" title={message(state.recoveryError)} />}
    <OperationRecovery entries={state.references} journal={editor.journal} disabled={state.sending || state.review !== null}
      recoveryAdapters={{ 'inventory.stock-release': { lookup: editor.receipt } }} onResolved={editor.reconciled}
      onFailure={editor.recoveryFailed} onBusyChange={editor.setRecovering} />
    {state.attempt && <section className={styles.notice}><h2>Hasil belum terkonfirmasi</h2>
      <p>Permintaan mungkin telah tersimpan. Formulir ditahan sampai hasil permintaan semula dapat dipastikan melalui referensi pemulihan.</p></section>}
    {state.result && <div ref={resultFocus} tabIndex={-1} role="status" className={common.success}><h2>Hasil pelepasan tercatat</h2>
      <p>{status[state.result.status]} · pengajuan #{state.result.proposalId}</p>
      {state.result.transferredValue !== null && <p>Nilai yang dilepas: {amount(state.result.transferredValue)}.</p>}
      <Button variant="secondary" type="button" disabled={locked || state.loadingSources || state.loadingHistory} onClick={() => void editor.refreshWarehouse()}>Periksa stok dan riwayat terbaru</Button></div>}
    <Section title="Pilih lokasi pelepasan" busy={state.loadingWarehouses}>
      <label><span id="release-warehouse-label">Gudang dan kanal</span><select aria-labelledby="release-warehouse-label" required value={state.warehouse ? releaseLocationKey(state.warehouse.location) : ''} disabled={locked || state.loadingWarehouses}
        onChange={event => { const choice = state.warehouses.find(item => releaseLocationKey(item.location) === event.target.value); void editor.chooseWarehouse(choice?.location ?? null); }}>
        <option value="">Pilih gudang yang ditugaskan</option>{state.warehouses.map(item => <option key={releaseLocationKey(item.location)} value={releaseLocationKey(item.location)}>
          {item.organizationName} / {item.shopName} / {item.warehouseName} / {item.channelName}</option>)}</select></label>
      {state.loadingWarehouses && <p role="status">Memuat gudang…</p>}{!state.loadingWarehouses && state.warehouses.length === 0 && !state.references.length && <p>Belum ada gudang yang dapat digunakan untuk pelepasan dalam penugasan ini.</p>}
      <Button variant="secondary" disabled={locked || state.loadingWarehouses} onClick={() => void editor.loadWarehouses()}>Muat ulang gudang</Button>
    </Section>
    {state.warehouse && editor.canPropose && <Section title="Ajukan barang yang akan dilepas" busy={state.loadingSources}
      description="Pilih penerimaan, kemasan dan lot yang tercatat. Daftar ini menampilkan paling banyak 25 pilihan yang memenuhi pemeriksaan saat ini.">
      <label><span id="release-source-label">Barang dan lot</span><select aria-labelledby="release-source-label" required disabled={locked || state.loadingSources} value={state.choice ? releaseChoiceKey(state.choice) : ''} onChange={event => editor.chooseSource(event.target.value)}>
        <option value="">Pilih barang yang sudah diperiksa</option>{state.sources.map(item => <option key={releaseChoiceKey(item)} value={releaseChoiceKey(item)}>
          {item.productName} / {item.packagingLabel} / Lot {item.lotReference} / {item.receiptReference}</option>)}</select></label>
      {state.loadingSources && <p role="status">Memeriksa barang, mutu dan penilaian…</p>}{!state.loadingSources && state.sources.length === 0 && <p>Belum ada barang yang dapat dilepas. Selesaikan pemeriksaan mutu, penilaian dan pengaturan sumber yang diperlukan.</p>}
      {state.unavailableCount !== '0' && <p className={common.help}>{state.unavailableCount} catatan belum dapat dinyatakan memenuhi pemeriksaan pelepasan.</p>}
      {state.choice && <><Facts items={[{ term: "Kemasan", value: state.choice.packagingLabel }, { term: "Lot", value: state.choice.lotReference },
        { term: "Kedaluwarsa", value: state.choice.expiresOn ?? 'Tidak memiliki tanggal kedaluwarsa menurut catatan yang disetujui' },
        { term: "Dapat dilepas pada pemeriksaan ini", value: <>{state.choice.releaseablePackages} kemasan</> },
        { term: "Nilai penilaian terpilih", value: <>{amount(state.choice.capitalizedValue)} · nilai pelepasan dihitung dari sisa yang tercatat</> }]} />
        <form className={styles.form} onSubmit={event => { event.preventDefault(); void editor.previewProposal(); }}>
          <label>Jumlah kemasan yang dilepas<input required inputMode="numeric" pattern="[1-9][0-9]{0,6}" maxLength={7} value={state.packages} disabled={locked || state.loadingPreview}
            onChange={event => editor.changePackages(event.target.value)} /></label><Button type="submit" variant={state.preview ? 'secondary' : 'primary'} disabled={locked || state.loadingPreview}>{state.loadingPreview ? 'Memeriksa jumlah…' : 'Periksa stok sebelum dan sesudah'}</Button></form></>}
      {state.preview && <><StockFacts value={state.preview} /><form className={styles.form} onSubmit={event => { event.preventDefault(); editor.reviewProposal(); }}>
        <label>Alasan pengajuan<textarea required rows={3} maxLength={1000} value={state.reason} disabled={locked} onChange={event => editor.changeReason(event.target.value)} /></label>
        <Button type="submit" variant="primary" disabled={locked}>Tinjau pengajuan</Button></form></>}
      <Button variant="secondary" disabled={locked || state.loadingSources} onClick={() => void editor.loadSources()}>Periksa pilihan terbaru</Button>
    </Section>}
    {state.warehouse && <Section title="Riwayat pengajuan pelepasan" busy={state.loadingHistory || state.loadingDetail}
      actions={<Button variant="secondary" disabled={locked || state.loadingHistory} onClick={() => void editor.loadHistory()}>Muat ulang riwayat</Button>}>
      <DataList label="Riwayat pengajuan pelepasan" items={state.loadingHistory ? [] : state.history.items} getKey={item => item.proposalId} loading={state.loadingHistory}
        empty={state.error === null ? { title: "Belum ada pengajuan pada halaman ini." } : undefined}
        renderTitle={item => <>{item.productName} · {item.packagingLabel}</>} renderBadge={item => <Badge status={status[item.status]} />}
        renderMeta={item => <>Lot {item.lotReference} · {item.packages} kemasan · {time(item.recordedAt)}</>}
        renderActions={item => <Button variant="secondary" disabled={locked || state.loadingDetail} onClick={() => void editor.loadDetail(item.proposalId)}>Periksa pengajuan #{item.proposalId}</Button>} />
      <div className={common.actions}>{state.historyCursor && <Button variant="secondary" disabled={locked || state.loadingHistory} onClick={() => void editor.loadHistory()}>Pengajuan terbaru</Button>}
        {state.history.nextAfter && <Button variant="secondary" disabled={locked || state.loadingHistory} onClick={() => void editor.loadHistory(state.history.nextAfter)}>Pengajuan sebelumnya</Button>}</div>
      {state.loadingDetail && <StateView kind="loading" title="Memeriksa rincian…" />}{state.detail && <section className={styles.detail}><h3>Pengajuan #{state.detail.proposal.id}</h3>
        <p>{status[state.detail.status]} · {state.detail.proposal.input.packages} kemasan</p><p>Alasan pengaju: {state.detail.proposal.reason}</p><p>{time(state.detail.proposal.proposedAt)}</p>
        {state.detail.decision && <><p>Alasan peninjau: {state.detail.decision.reason}</p><p>{time(state.detail.decision.decidedAt)}</p>
          {state.detail.decision.transferredValue !== null && <p>Nilai yang dilepas: {amount(state.detail.decision.transferredValue)}.</p>}</>}
        {state.detail.status === 'proposed' && editor.canApprove && <div className={common.actions}>
          <Button variant={state.decision ? 'secondary' : 'primary'} disabled={locked || !state.fresh || state.loadingPreview} onClick={() => void editor.previewDecision('approve')}>Periksa pelepasan untuk disetujui</Button>
          <Button variant="secondary" disabled={locked || !state.fresh || state.loadingPreview} onClick={() => void editor.previewDecision('reject')}>Periksa penolakan</Button></div>}
      </section>}
      {state.decision && <section className={styles.detail}><h3>{state.decision.action === 'approve' ? 'Pemeriksaan stok terkini oleh peninjau' : 'Tinjau penolakan pengajuan'}</h3>
        <StockFacts value={state.decision} /><form className={styles.form} onSubmit={event => { event.preventDefault(); editor.reviewDecision(); }}>
          <label>Alasan keputusan<textarea required rows={3} maxLength={1000} value={state.decisionReason} disabled={locked} onChange={event => editor.changeDecisionReason(event.target.value)} /></label>
          <Button type="submit" variant="primary" disabled={locked || !state.fresh}>Tinjau keputusan</Button></form></section>}
    </Section>}
    {state.review && <div ref={reviewFocus} tabIndex={-1} className={styles.review}><h2>Konfirmasi {('proposalId' in state.review) ? 'keputusan' : 'pengajuan'} pelepasan</h2>
      <p>{'proposalId' in state.review ? state.review.action === 'approve' ? 'Setujui pelepasan jumlah terpilih.' : 'Tolak pengajuan dengan alasan tercatat.' : 'Kirim pengajuan untuk peninjauan independen.'}</p>
      <StockFacts value={state.review.preview} /><p>Alasan: <strong>{state.review.body.reason}</strong></p>
      <label className={common.checkbox}><input type="checkbox" checked={confirmed} disabled={busy} onChange={event => setConfirmation({ command: state.review, checked: event.target.checked })} />
        <span>Saya telah memeriksa kemasan, lot, jumlah dan alasan pada ringkasan ini serta memahami tindakan yang dipilih.</span></label>
      <div className={common.actions}><Button variant="primary" disabled={busy || !confirmed} onClick={() => void editor.confirm(confirmed)}>Konfirmasi dan simpan</Button>
        <Button variant="secondary" disabled={busy} onClick={() => editor.cancelReview()}>Kembali</Button></div></div>}
  </>;
}
function StockFacts({ value }: { value: ReleasePreview | ReleaseReview }) {
  return <div className={styles.preview}><Facts columns={1} items={[{ term: "Jumlah yang dipilih", value: <>{'input' in value ? value.input.packages : value.proposal.input.packages} kemasan</> },
    { term: "Stok ditahan sebelum → sesudah", value: <>{value.stockBefore.heldPackages} → {value.stockAfter.heldPackages}</> },
    { term: "Stok tersedia sebelum → sesudah", value: <>{value.stockBefore.availablePackages} → {value.stockAfter.availablePackages}</> }]} />
    <div><p>Mutu yang disetujui: {value.selection.acceptedPackages} kemasan.</p><p>Jumlah dalam penilaian terpilih: {value.selection.valuedPackages} kemasan.</p>
      <p>Nilai penilaian terpilih: {amount(value.selection.capitalizedValue)}.</p><p className={common.help}>Pemeriksaan: {time(value.observedAt)}. Nilai pelepasan pasti ditampilkan setelah keputusan tercatat.</p></div></div>;
}
