"use client";

import { SessionAccessState, lostAccessKind } from '@/components/workspace-kit/session-access-state';
import Link from "next/link";
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { RequestNotSentError } from "../../api/client";
import { createAccessGateway } from "../administration/access-client";
import { AdministrationNavigation } from "../administration/AdministrationNavigation";
import { OperationRecovery } from "../administration/OperationRecovery";
import { OperationJournal } from "../administration/operation-journal";
import { AccessFailure, accessMessage } from "../administration/workflow";
import { createIdentityGateway } from "../identity/identity-client";
import { canUseStockSources, IdentityFailure, identityMessage, sessionTiming, type IdentitySession } from "../identity/identity-workflow";
import { createStockSourceGateway } from "./stock-source-client";
import { createStockSourceRevisionGateway } from "./stock-source-revision-client";
import { StockSourceRevisionEditor } from "./stock-source-revision-editor-state";
import { sourceActor, sourceLocationKey, type SourcePurpose } from "./stock-source-workflow";
import { RevisionFacts, RevisionComparison, RevisionDraftForm, RevisionDecisionForm, RevisionReview, revisionStatus, sourceAmount } from "./StockSourceRevisionForms";
import common from "../administration/access.module.css";
import styles from "./stock-source-revision.module.css";
import { formatDateTime } from "@/lib/format";
import { Button } from '@/components/workspace-kit/button';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { Section } from '@/components/workspace-kit/section';
import { StateView } from '@/components/workspace-kit/state-view';
import { DataList } from '@/components/workspace-kit/data-list';
import { Badge } from '@/components/workspace-kit/badge';

const path = "/inventory/sources/revisions";
const factorHref = "/identity/account?operation=stock.source.approve&returnPath=" + encodeURIComponent(path);
type Loaded = { actorId: string; actions: string[]; session: IdentitySession; observedAt: number; generation: number };
function message(error: unknown) {
  if (error instanceof RequestNotSentError) return accessMessage(error);
  if (error instanceof IdentityFailure) return identityMessage(error);
  if (error instanceof AccessFailure) {
    if (error.code === "stock_source_revision_input_invalid") return "Periksa semua isian dan pastikan ada pengaturan yang berubah. Batas usia harus positif dengan maksimal tiga angka desimal; cadangan harus berupa satuan dasar bulat.";
    if (error.code === "stock_source_selection_required") return "Pilih gudang, kanal, alokasi, dan pengajuan dari daftar yang sesuai.";
    if (error.code === "stock_source_revision_not_approvable") return "Pengajuan memakai versi lama atau alokasi masih aktif. Periksa pengaturan terbaru; persetujuan belum dapat dilakukan.";
    if (error.code === "stock_source_revision_changed" || error.code === "stock_source_already_decided") return "Pengajuan atau versi sumber telah berubah. Muat ulang alokasi dan periksa data terbaru.";
    if (error.code === "independent_actor_required" || error.code === "independent_person_required") return "Keputusan memerlukan peninjau yang berbeda dari pengaju.";
    if (error.code === "stock_source_proposer_required") return "Hanya pengaju semula yang dapat menarik perubahan ini.";
    if (error.code === "step_up_required") return "Lakukan verifikasi dua langkah terbaru sebelum meninjau atau memutuskan perubahan.";
    if (error.status >= 500 || error.status === 0) return "Hasil belum dapat dipastikan. Jika ada referensi pemulihan, gunakan Periksa hasil. Referensi tetap disimpan; jangan kirim perubahan baru.";
    return accessMessage(error);
  }
  return "Koneksi terputus. Hasil belum dapat dipastikan. Gunakan Periksa hasil pada referensi pemulihan yang tersimpan.";
}
export function StockSourceRevisionWorkspace() {
  const [access] = useState(() => createAccessGateway()), [identity] = useState(() => createIdentityGateway());
  const request = useRef<AbortController | null>(null), generation = useRef(0);
  const [loaded, setLoaded] = useState<Loaded | null>(null), [error, setError] = useState<unknown>(null), [loading, setLoading] = useState(true);
  const refresh = useCallback(async () => {
    request.current?.abort(); const controller = new AbortController(); request.current = controller;
    const current = () => request.current === controller && !controller.signal.aborted;
    setLoaded(null); setLoading(true); setError(null);
    try {
      const session = await identity.session(controller.signal), observedAt = performance.now();
      if (!current()) return;
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, "unauthenticated");
      const account = await access.current(); if (!current()) return;
      if (account.userId !== session.account.userId) throw new AccessFailure(409, "actor_changed");
      const actorId = sourceActor(account.userId);
      if (!Array.isArray(account.allowedActions) || account.allowedActions.length > 200 ||
        account.allowedActions.some(action => typeof action !== "string" || !/^[a-z]+(?:[.-][a-z]+){1,5}$/.test(action))) throw new AccessFailure(502, "stock_source_revision_response_invalid");
      if (!canUseStockSources(account.allowedActions)) throw new AccessFailure(403, "access_denied");
      if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, "session_expired");
      setLoaded({ actorId, actions: [...account.allowedActions], session, observedAt, generation: ++generation.current });
    } catch (failure) { if (current()) setError(failure); }
    finally { if (current()) setLoading(false); }
  }, [access, identity]);
  useEffect(() => {
    let active = true; void Promise.resolve().then(() => { if (active) void refresh(); });
    const restored = (event: PageTransitionEvent) => { if (event.persisted) void refresh(); };
    window.addEventListener("pageshow", restored);
    return () => { active = false; request.current?.abort(); window.removeEventListener("pageshow", restored); };
  }, [refresh]);
  return <main className={common.page}>
    <a className={styles.skip} href="#revision-workspace">Langsung ke perubahan sumber</a>
    <PageHeader title="Perubahan sumber stok" description="Bandingkan pengaturan, periksa tanggung jawab, dan simpan keputusan untuk alokasi yang sudah terdaftar." />
    <div id="revision-workspace" className="ui-stack">{loaded ? <RevisionDesk key={loaded.actorId + ":" + loaded.generation} loaded={loaded} onRefresh={refresh} /> :
      (loading ? <StateView kind="loading" title="Memeriksa sesi dan akses" />
        : <SessionAccessState error={error} returnPath={path} onRetry={() => void refresh()} detail={error !== null ? message(error) : undefined} />)}</div>
  </main>;
}
function RevisionDesk({ loaded, onRefresh }: { loaded: Loaded; onRefresh: () => Promise<void> }) {
  const [editor] = useState(() => new StockSourceRevisionEditor({ actorId: loaded.actorId, actions: loaded.actions, session: loaded.session,
    observedAt: loaded.observedAt, now: () => performance.now(), gateway: createStockSourceRevisionGateway(),
    warehouses: createStockSourceGateway().warehouses, journal: new OperationJournal(() => window.localStorage) }));
  const state = useSyncExternalStore(editor.subscribe, editor.snapshot, editor.snapshot);
  const reviewFocus = useRef<HTMLDivElement>(null), resultFocus = useRef<HTMLDivElement>(null);
  useEffect(() => {
    void editor.start(); const timer = window.setInterval(() => editor.checkSession(), 500);
    const changed = (event: StorageEvent) => { if (event.key === null || event.key.startsWith("unit-toko:pending:v1:")) editor.refreshReferences(); };
    const leaving = () => editor.dispose();
    window.addEventListener("storage", changed); window.addEventListener("pagehide", leaving);
    return () => { editor.dispose(); window.clearInterval(timer); window.removeEventListener("storage", changed); window.removeEventListener("pagehide", leaving); };
  }, [editor]);
  const warn = state.sending || state.attempt !== null || state.review !== null || state.references.length > 0 || state.recoveryError !== null;
  useEffect(() => {
    if (!warn) return;
    const leaving = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ""; };
    window.addEventListener("beforeunload", leaving); return () => window.removeEventListener("beforeunload", leaving);
  }, [warn]);
  useEffect(() => { if (state.review) reviewFocus.current?.focus(); }, [state.review]);
  useEffect(() => { if (state.result) resultFocus.current?.focus(); }, [state.result]);
  const busy = state.sending || state.recovering, locked = editor.locked;
  if (state.expired || state.suspended) return <SessionAccessState error={state.error} kind={state.expired ? 'expired' : lostAccessKind(state.error)} returnPath={path} busy={busy} onRetry={() => void onRefresh()} detail={state.error !== null ? message(state.error) : undefined} />;
  return <>
    <AdministrationNavigation current="source-revisions" actions={loaded.actions} locked={locked} />
    <Section title="Pilih ruang kerja" id="revision-location-title" description={<>Petugas: <strong>{loaded.session.account?.displayName ?? loaded.session.account?.verifiedEmail}</strong></>}
      actions={<Button variant="secondary" disabled={busy || state.review !== null} onClick={() => void onRefresh()}>Muat ulang sesi dan akses</Button>}>
      <div className={styles.location}><label><span id="revision-purpose-label">Akses lokasi</span><select aria-labelledby="revision-purpose-label" disabled={editor.selectionLocked || state.loadingWarehouses} value={state.purpose}
        onChange={event => void editor.changePurpose(event.target.value as SourcePurpose)}>
        {editor.canRead && <option value="inventory-source-history">Melihat pengaturan dan riwayat</option>}
        {editor.canPropose && <option value="inventory-source-propose">Mengajukan atau memulihkan pengajuan</option>}
        {editor.canApprove && <option value="inventory-source-review">Meninjau atau memulihkan keputusan</option>}</select></label>
        <label><span id="revision-warehouse-label">Gudang dan kanal</span><select aria-labelledby="revision-warehouse-label" value={state.warehouse ? sourceLocationKey(state.warehouse.location) : ""} disabled={editor.selectionLocked || state.loadingWarehouses}
          onChange={event => void editor.chooseWarehouse(state.warehouses.find(item => sourceLocationKey(item.location) === event.target.value)?.location ?? null)}>
          <option value="">{state.loadingWarehouses ? "Memuat gudang…" : "Pilih gudang dan kanal"}</option>
          {state.warehouses.map(item => <option key={sourceLocationKey(item.location)} value={sourceLocationKey(item.location)}>
            {item.organizationName} / {item.shopName} / {item.warehouseName} / {item.channelName}</option>)}</select></label></div>
      <Button variant="secondary" disabled={editor.selectionLocked || state.loadingWarehouses} onClick={() => void editor.loadWarehouses()}>Muat ulang gudang</Button>
      {!state.loadingWarehouses && !state.warehouses.length && <p className={common.help}>Belum ada gudang untuk tugas ini. Periksa penugasan dan verifikasi akun.</p>}
      {editor.canApprove && <p className={common.help}>{state.fresh ? "Verifikasi dua langkah masih baru. " : "Peninjauan memerlukan verifikasi dua langkah terbaru. "}
        <Link href={factorHref} aria-disabled={busy} onClick={event => { if (busy) event.preventDefault(); }}>Verifikasi untuk peninjauan</Link></p>}
    </Section>
    <aside className={styles.notice}><strong>Persetujuan menyimpan pengaturan yang belum aktif.</strong>
      <p>Saldo dan jumlah tersedia belum berubah. Pengoperasian sumber memerlukan pemeriksaan kesiapan dan serah terima tersendiri.</p></aside>
    {state.sending && <p role="status">Menyimpan permintaan… Tunggu hasil sebelum melanjutkan.</p>}
    {state.error !== null && <StateView kind="error" title={message(state.error)} />}
    {state.recoveryError !== null && <StateView kind="error" title={message(state.recoveryError)} />}
    {state.references.length > 0 && <p className={common.help}>Pilih akses lokasi serta gudang dan kanal yang digunakan semula untuk memeriksa hasil. Perubahan baru ditahan hingga hasilnya terkonfirmasi.</p>}
    <OperationRecovery entries={state.references} journal={editor.journal} disabled={state.sending || state.review !== null || !state.warehouse}
      recoveryAdapters={{ "stock.source.revision": { lookup: editor.receipt } }} onResolved={editor.reconciled} onFailure={editor.recoveryFailed} onBusyChange={editor.setRecovering} />
    {state.result && <div ref={resultFocus} tabIndex={-1} className={common.success} role="status"><h2>Perubahan tersimpan</h2>
      <p>{revisionStatus[state.result.status]}. Referensi pengajuan {state.result.proposalId}.</p>
      {state.result.appliedPoolVersion && <p>Versi pengaturan {state.result.appliedPoolVersion} pada alokasi {state.result.physicalPoolId}. Belum aktif.</p>}
      <p>Muat ulang alokasi untuk memeriksa data terbaru sebelum membuat perubahan lain.</p></div>}
    {state.review && <div className={styles.review} ref={reviewFocus} tabIndex={-1}>
      <RevisionReview command={state.review} current={state.current} busy={busy} onSubmit={confirmed => void editor.submitReviewed(confirmed)} onCancel={() => editor.cancelReview()} />
    </div>}
    {!state.warehouse ? <StateView kind="empty" title="Pilih gudang untuk mulai" message="Pilihan mengikuti penugasan akun Anda." /> :
      !editor.canRead ? <StateView kind="denied" title="Akses pengaturan diperlukan"
        message="Izin mengajukan atau meninjau tidak otomatis memberi akses membaca pengaturan. Pemulihan permintaan milik Anda tetap tersedia setelah memilih lokasi semula." /> :
      <><div className={styles.desk}>
        <Section title="Alokasi terdaftar" busy={state.loadingSources} description={<>{sourceAmount(state.sources.total)} alokasi pada gudang ini.</>}
          actions={<Button variant="secondary" disabled={locked || state.loadingSources} onClick={() => void editor.loadSources()}>Muat ulang alokasi</Button>}>
          <DataList label="Alokasi terdaftar" items={state.loadingSources ? [] : state.sources.items} getKey={item => item.physicalPoolId} loading={state.loadingSources}
            empty={state.error === null ? { title: state.references.length ? "Selesaikan pemeriksaan hasil untuk memuat alokasi." : "Belum ada alokasi pada halaman ini." } : undefined}
            renderTitle={item => item.settings.label} renderMeta={item => <>{item.settings.physicalKey} · Versi {item.poolVersion}</>}
            renderBadge={item => <Badge status={item.poolState === "inactive" ? "Belum aktif" : "Aktif"} />}
            onSelect={item => void editor.chooseSource(item.physicalPoolId)} selectedKey={state.current?.physicalPoolId ?? null} disabled={locked}
            openLabel={item => item.settings.label + " " + item.settings.physicalKey + " Versi " + item.poolVersion + " · " + (item.poolState === "inactive" ? "Belum aktif" : "Aktif")}
            pagination={pager("alokasi", state.sourceOffset, state.sources.items.length, state.sources.total, locked, offset => void editor.loadSources(offset))} />
        </Section>
        <section className="ui-section" aria-busy={state.loadingCurrent}>{state.loadingCurrent ? <StateView kind="loading" title="Memeriksa pengaturan terbaru…" /> : state.current ? <>
          <h2>{state.current.settings.label}</h2><p>Pengaturan saat ini · versi <strong>{state.current.poolVersion}</strong> · {state.current.poolState === "inactive" ? "Belum aktif" : "Aktif"}</p>
          <p className={common.help}>Alokasi {state.current.physicalPoolId}. Disetujui {formatDateTime(state.current.approvedAt)}.</p>
          <details><summary>Lihat pengaturan saat ini</summary><RevisionFacts settings={state.current.settings} /></details>
          {editor.canPropose && <section className={styles.section}><h3>Ajukan perubahan pengaturan</h3>
            <RevisionDraftForm draft={state.draft} disabled={locked} onChange={(field, value) => editor.changeDraft(field, value)} onReview={() => editor.reviewProposal()} /></section>}
        </> : <><h2>Pilih alokasi</h2><p>Periksa pengaturan saat ini, ajukan perubahan, atau lihat riwayat keputusan.</p></>}</section>
      </div>
      {state.current && <Section title="Riwayat perubahan alokasi" busy={state.loadingHistory} description={<>{sourceAmount(state.history.total)} pengajuan. Keputusan terdahulu tetap tersimpan.</>}
        actions={<Button variant="secondary" disabled={locked || state.loadingHistory} onClick={() => void editor.loadHistory()}>Muat ulang riwayat</Button>}>
        <div className={styles.desk}><div>
          <DataList label="Riwayat perubahan alokasi" items={state.loadingHistory ? [] : state.history.items} getKey={item => item.id} loading={state.loadingHistory}
            empty={state.error === null ? { title: "Belum ada pengajuan perubahan pada halaman ini." } : undefined}
            renderTitle={item => item.reason} renderMeta={item => <>#{item.id} · dasar versi {item.basePoolVersion}</>} renderBadge={item => <Badge status={revisionStatus[item.status]} />}
            onSelect={item => void editor.chooseProposal(item.id)} selectedKey={state.selected?.id ?? null} disabled={locked}
            openLabel={item => item.reason + " #" + item.id + " · dasar versi " + item.basePoolVersion + " " + revisionStatus[item.status]}
            pagination={pager("riwayat", state.historyOffset, state.history.items.length, state.history.total, locked, offset => void editor.loadHistory(offset))} /></div>
          <section className={styles.details} aria-busy={state.loadingDetail}>{state.loadingDetail ? <StateView kind="loading" title="Memeriksa pengajuan terbaru…" /> :
            state.selected ? <><h2>Pengajuan #{state.selected.id}</h2><Badge status={revisionStatus[state.selected.status]} />
              <p>{state.selected.reason}</p><p className={common.help}>Diajukan {formatDateTime(state.selected.proposedAt)}.</p>
              {state.selected.status === "proposed" && editor.staleBase && <div role="status" className="ui-notice" data-tone="warning">Dasar pengajuan sudah berbeda dari versi saat ini. Pengajuan ini belum dapat disetujui; periksa versi terbaru sebelum mengajukan pengganti.</div>}
              {state.selected.status === "proposed" && state.current.poolState === "active" && <div className="ui-notice" data-tone="warning">Alokasi masih aktif. Perubahan belum dapat disetujui.</div>}
              <RevisionComparison base={state.selected.baseSettings} proposed={state.selected.proposedSettings} baseVersion={state.selected.basePoolVersion} current={state.current} />
              {state.selected.decision && <section className={styles.section}><h3>Keputusan tercatat</h3><p>{state.selected.decision.reviewReference}</p><p>{state.selected.decision.reason}</p>
                <p>{formatDateTime(state.selected.decision.decidedAt)}{state.selected.appliedPoolVersion ? " · diterapkan sebagai versi " + state.selected.appliedPoolVersion : ""}.</p></section>}
              <section className={styles.section}><RevisionDecisionForm key={state.selected.id + ":" + state.current.poolVersion} proposal={state.selected}
                disabled={locked} fresh={state.fresh} approvable={!editor.staleBase && state.current.poolState === "inactive"} canPropose={editor.canPropose} canApprove={editor.canApprove}
                onReview={(action, reference, reason) => editor.reviewDecision(action, reference, reason)} /></section>
            </> : <><h2>Pilih pengajuan</h2><p>Bandingkan dasar pengajuan dengan perubahan yang diminta sebelum mengambil keputusan.</p></>}</section></div>
      </Section>}</>}
  </>;
}
/** The shared pager (hidden when one page holds everything), 25 rows per page as the API serves them. */
function pager(name: string, offset: number, count: number, total: string, disabled: boolean, onPage: (offset: number) => void) {
  return { label: "Halaman " + name, start: offset + 1, end: offset + count, total: Number(total), disabled, hasPrevious: offset > 0,
    hasNext: !(offset >= 10000 || BigInt(offset + 25) >= BigInt(total)), onPrevious: () => onPage(offset - 25), onNext: () => onPage(offset + 25) };
}
