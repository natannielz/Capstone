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
import { StockSourceAliasEditor } from "./stock-source-alias-editor-state";
import { createStockSourceAliasGateway } from "./stock-source-alias-client";
import { sourceActor, sourceLocationKey, type SourcePurpose } from "./stock-source-workflow";
import { RevisionFacts } from "./StockSourceRevisionForms";
import { AliasFacts, AliasDraftForm, AliasDecisionForm, AliasReview, aliasStatus, aliasAmount, bindingLabel } from "./StockSourceAliasForms";
import type { StockWarehouse } from "./stock-count-workflow";
import common from "../administration/access.module.css";
import styles from "./stock-source-alias.module.css";
import { formatDateTime } from "@/lib/format";
import { Button } from '@/components/workspace-kit/button';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { Section } from '@/components/workspace-kit/section';
import { StateView } from '@/components/workspace-kit/state-view';
import { DataList } from '@/components/workspace-kit/data-list';
import { Badge } from '@/components/workspace-kit/badge';

const path = "/inventory/sources/aliases";
const factorHref = "/identity/account?operation=stock.source.approve&returnPath=" + encodeURIComponent(path);
type Loaded = { actorId: string; actions: string[]; session: IdentitySession; observedAt: number; generation: number };
function message(error: unknown) {
  if (error instanceof RequestNotSentError) return accessMessage(error);
  if (error instanceof IdentityFailure) return identityMessage(error);
  if (error instanceof AccessFailure) {
    if (error.code === "stock_source_alias_input_invalid") return "Isi alasan dan pilih perubahan pemetaan. Pemetaan yang tetap sama hanya dapat diajukan ulang setelah pengaturan sumber berubah.";
    if (error.code === "stock_source_selection_required") return "Pilih gudang, kanal, alokasi, dan pengajuan dari daftar yang sesuai.";
    if (error.code === "stock_source_alias_not_approvable") return "Pengajuan memakai versi lama atau alokasi masih aktif. Periksa pengaturan terbaru; persetujuan belum dapat dilakukan.";
    if (error.code === "stock_source_alias_changed" || error.code === "stock_source_already_decided") return "Pengajuan atau versi sumber telah berubah. Muat ulang alokasi dan periksa data terbaru.";
    if (error.code === "independent_actor_required" || error.code === "independent_person_required") return "Keputusan memerlukan peninjau yang berbeda dari pengaju.";
    if (error.code === "stock_source_proposer_required") return "Hanya pengaju semula yang dapat menarik pemetaan ini.";
    if (error.code === "step_up_required") return "Lakukan verifikasi dua langkah terbaru sebelum meninjau atau memutuskan pemetaan.";
    if (error.status >= 500 || error.status === 0) return "Hasil belum dapat dipastikan. Jika ada referensi pemulihan, gunakan Periksa hasil. Referensi tetap disimpan; jangan kirim pemetaan baru.";
    return accessMessage(error);
  }
  return "Koneksi terputus. Hasil belum dapat dipastikan. Gunakan Periksa hasil pada referensi pemulihan yang tersimpan.";
}
export function StockSourceAliasWorkspace() {
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
        account.allowedActions.some(action => typeof action !== "string" || !/^[a-z]+(?:[.-][a-z]+){1,5}$/.test(action))) throw new AccessFailure(502, "stock_source_alias_response_invalid");
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
    <a className={styles.skip} href="#alias-workspace">Langsung ke pemetaan kanal</a>
    <PageHeader title="Pemetaan kanal" description="Kelola penggunaan satu alokasi fisik oleh kanal lain, dengan pengajuan dan peninjauan terpisah." />
    <div id="alias-workspace" className="ui-stack">{loaded ? <AliasDesk key={loaded.actorId + ":" + loaded.generation} loaded={loaded} onRefresh={refresh} /> :
      (loading ? <StateView kind="loading" title="Memeriksa sesi dan akses" />
        : <SessionAccessState error={error} returnPath={path} onRetry={() => void refresh()} detail={error !== null ? message(error) : undefined} />)}</div>
  </main>;
}
const warehouseLabel = (item: StockWarehouse | null) => item ? [item.organizationName, item.shopName, item.warehouseName, item.channelName].join(" / ") : "";
function AliasDesk({ loaded, onRefresh }: { loaded: Loaded; onRefresh: () => Promise<void> }) {
  const [editor] = useState(() => new StockSourceAliasEditor({ actorId: loaded.actorId, actions: loaded.actions, session: loaded.session,
    observedAt: loaded.observedAt, now: () => performance.now(), gateway: createStockSourceAliasGateway(),
    warehouses: createStockSourceGateway().warehouses, sources: createStockSourceRevisionGateway().sources,
    journal: new OperationJournal(() => window.localStorage) }));
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
    <AdministrationNavigation current="source-aliases" actions={loaded.actions} locked={locked} />
    <Section title="Pilih ruang kerja" id="alias-location-title" description={<>Petugas: <strong>{loaded.session.account?.displayName ?? loaded.session.account?.verifiedEmail}</strong></>}
      actions={<Button variant="secondary" disabled={busy || state.review !== null} onClick={() => void onRefresh()}>Muat ulang sesi dan akses</Button>}>
      <div className={styles.location}>
        <label><span id="alias-purpose-label">Akses lokasi</span><select aria-labelledby="alias-purpose-label" disabled={editor.selectionLocked || state.loadingWarehouses} value={state.purpose}
          onChange={event => void editor.changePurpose(event.target.value as SourcePurpose)}>
          {editor.canRead && <option value="inventory-source-history">Melihat pengaturan dan riwayat</option>}
          {editor.canPropose && <option value="inventory-source-propose">Mengajukan atau memulihkan pengajuan</option>}
          {editor.canApprove && <option value="inventory-source-review">Meninjau atau memulihkan keputusan</option>}</select></label>
        <Button variant="secondary" disabled={editor.selectionLocked || state.loadingWarehouses} onClick={() => void editor.loadWarehouses()}>Muat ulang lokasi</Button>
      </div>
      {state.loadingWarehouses && <p role="status">Memuat lokasi yang ditugaskan…</p>}
      {!state.loadingWarehouses && !state.warehouses.length && <p className={common.help}>Belum ada lokasi untuk tugas ini. Periksa penugasan dan verifikasi akun.</p>}
      {editor.canApprove && <p className={common.help}>{state.fresh ? "Verifikasi dua langkah masih baru. " : "Peninjauan memerlukan verifikasi dua langkah terbaru. "}
        <Link href={factorHref} aria-disabled={busy} onClick={event => { if (busy) event.preventDefault(); }}>Verifikasi untuk peninjauan</Link></p>}
    </Section>
    <aside className={styles.notice}><strong>Satu alokasi fisik, tanpa saldo ganda.</strong>
      <p>Pemetaan mengatur hubungan antar kanal. Jumlah barang tetap. Pengoperasian sumber memerlukan pemeriksaan kesiapan dan serah terima tersendiri.</p></aside>
    {state.sending && <p role="status">Menyimpan permintaan… Tunggu hasil sebelum melanjutkan.</p>}
    {state.error !== null && <StateView kind="error" title={message(state.error)} />}
    {state.recoveryError !== null && <StateView kind="error" title={message(state.recoveryError)} />}
    {state.references.length > 0 && <Section title="Pilih konteks permintaan semula" id="alias-recovery-context">
      <p>Masukkan referensi alokasi dan kanal tujuan yang dipakai saat mengirim. Pilihan ini hanya untuk memeriksa hasil; perubahan baru tetap ditahan.</p>
      <div className={styles.formPair}>
        <label><span id="alias-recovery-pool-label">Referensi alokasi semula</span><input aria-labelledby="alias-recovery-pool-label"
          inputMode="numeric" maxLength={19} disabled={busy || state.review !== null} value={state.recoveryPoolId}
          onChange={event => editor.setRecoveryContext(event.target.value, state.recoveryWarehouse?.location ?? null)} /></label>
        <label><span id="alias-recovery-channel-label">Kanal tujuan semula</span><select aria-labelledby="alias-recovery-channel-label"
          disabled={busy || state.review !== null || state.loadingWarehouses} value={state.recoveryWarehouse ? sourceLocationKey(state.recoveryWarehouse.location) : ""}
          onChange={event => editor.setRecoveryContext(state.recoveryPoolId, state.warehouses.find(item => sourceLocationKey(item.location) === event.target.value)?.location ?? null)}>
          <option value="">Pilih kanal tujuan semula</option>{state.warehouses.map(item => <option key={sourceLocationKey(item.location)}
            value={sourceLocationKey(item.location)}>{warehouseLabel(item)}</option>)}</select></label>
      </div>
      {!editor.canRead && <p className={common.help}>Izin membaca pengaturan tidak diperlukan untuk memeriksa permintaan Anda sendiri. Gunakan referensi alokasi semula.</p>}
    </Section>}
    <OperationRecovery entries={state.references} journal={editor.journal}
      disabled={state.sending || state.review !== null || !state.recoveryWarehouse || !/^[1-9][0-9]{0,18}$/.test(state.recoveryPoolId)}
      recoveryAdapters={{ "stock.source.alias": { lookup: editor.receipt } }} onResolved={editor.reconciled} onFailure={editor.recoveryFailed} onBusyChange={editor.setRecovering} />
    {state.result && <div ref={resultFocus} tabIndex={-1} className={common.success} role="status"><h2>Pemetaan tersimpan</h2>
      <p>{aliasStatus[state.result.status]}. Referensi pengajuan {state.result.proposalId}.</p>
      {state.result.appliedAliasVersion && <p>Versi pemetaan {state.result.appliedAliasVersion} pada alokasi {state.result.physicalPoolId}.</p>}
      <p>Muat ulang alokasi untuk memeriksa data terbaru sebelum membuat perubahan lain.</p></div>}
    {state.review && <div className={styles.review} ref={reviewFocus} tabIndex={-1}>
      <AliasReview command={state.review} current={state.current} ownerLabel={warehouseLabel(state.warehouse)} requestedLabel={warehouseLabel(state.requested)}
        busy={busy} onSubmit={confirmed => void editor.submitReviewed(confirmed)} onCancel={() => editor.cancelReview()} />
    </div>}
    {!editor.canRead ? <StateView kind="denied" title="Akses pengaturan diperlukan"
      message="Izin mengajukan atau meninjau tidak otomatis memberi akses membaca pengaturan. Pemulihan permintaan milik Anda tetap tersedia." /> :
      <><Section title="Alokasi dan kanal">
        <div className={styles.formPair}>
          <label><span id="alias-owner-label">Gudang dan kanal pemilik alokasi</span><select aria-labelledby="alias-owner-label"
            value={state.warehouse ? sourceLocationKey(state.warehouse.location) : ""} disabled={locked || state.loadingWarehouses}
            onChange={event => void editor.chooseWarehouse(state.warehouses.find(item => sourceLocationKey(item.location) === event.target.value)?.location ?? null)}>
            <option value="">Pilih pemilik alokasi</option>{state.warehouses.map(item => <option key={sourceLocationKey(item.location)}
              value={sourceLocationKey(item.location)}>{warehouseLabel(item)}</option>)}</select></label>
          <label><span id="alias-requested-label">Kanal tujuan pemetaan</span><select aria-labelledby="alias-requested-label"
            value={state.requested ? sourceLocationKey(state.requested.location) : ""} disabled={locked || !state.poolId}
            onChange={event => void editor.chooseRequested(editor.requestedChoices.find(item => sourceLocationKey(item.location) === event.target.value)?.location ?? null)}>
            <option value="">Pilih kanal lain di gudang yang sama</option>{editor.requestedChoices.map(item => <option key={sourceLocationKey(item.location)}
              value={sourceLocationKey(item.location)}>{warehouseLabel(item)}</option>)}</select></label>
        </div>
        {state.warehouse && !editor.requestedChoices.length && <p className={common.help}>Belum ada kanal lain yang dapat dipilih pada gudang ini untuk tugas Anda.</p>}
      </Section>
      <div className={styles.desk}>
        <Section title="Alokasi terdaftar" busy={state.loadingSources} description={<>{aliasAmount(state.sources.total)} alokasi pada gudang dan kanal pemilik.</>}
          actions={<Button variant="secondary" disabled={locked || state.loadingSources || !state.warehouse} onClick={() => void editor.loadSources()}>Muat ulang alokasi</Button>}>
          <DataList label="Alokasi terdaftar" items={state.loadingSources ? [] : state.sources.items} getKey={item => item.physicalPoolId} loading={state.loadingSources}
            empty={state.error === null ? { title: state.references.length ? "Periksa hasil permintaan sebelum memuat alokasi." : "Pilih pemilik alokasi atau muat ulang daftar." } : undefined}
            renderTitle={item => item.settings.label} renderMeta={item => <>{item.settings.physicalKey} · Versi {item.poolVersion}</>}
            renderBadge={item => <Badge status={item.poolState === "inactive" ? "Belum aktif" : "Aktif"} />}
            onSelect={item => editor.chooseSource(item.physicalPoolId)} selectedKey={state.poolId} disabled={locked}
            openLabel={item => item.settings.label + " " + item.settings.physicalKey + " Versi " + item.poolVersion + " · " + (item.poolState === "inactive" ? "Belum aktif" : "Aktif")}
            pagination={pager("alokasi", state.sourceOffset, state.sources.items.length, state.sources.total, locked || !state.warehouse, offset => void editor.loadSources(offset))} />
        </Section>
        <section className="ui-section" aria-busy={state.loadingCurrent}>{state.loadingCurrent ? <StateView kind="loading" title="Memeriksa pemetaan saat ini…" /> : state.current ? <>
          <h2>{state.current.source.settings.label}</h2><p><strong>{bindingLabel[state.current.bindingState]}</strong></p>
          <p>Pemilik: {warehouseLabel(state.warehouse)}<br />Tujuan: {warehouseLabel(state.requested)}</p>
          <p className={common.help}>Alokasi {state.current.source.physicalPoolId}; pengaturan sumber versi {state.current.source.poolVersion}.
            Pemetaan {state.current.mapping?.appliedAliasVersion ? "versi " + state.current.mapping.appliedAliasVersion : "belum ada"}.</p>
          {state.current.bindingState === "stale" && <StateView kind="unknown" title="Pengaturan sumber telah berubah. Pemetaan memerlukan pengajuan dan peninjauan ulang." />}
          <details><summary>Lihat pengaturan sumber saat ini</summary><RevisionFacts settings={state.current.source.settings} /></details>
          {editor.canPropose && <section className={styles.section}><AliasDraftForm enabled={state.enabled} reason={state.reason} disabled={locked}
            onChange={(enabled, reason) => editor.changeDraft(enabled, reason)} onReview={() => editor.reviewProposal()} /></section>}
        </> : <><h2>Pilih alokasi dan kanal tujuan</h2><p>Periksa hubungan kanal sebelum mengajukan atau meninjau pemetaan.</p></>}</section>
      </div>
      {state.current && <Section title="Riwayat pemetaan kanal" busy={state.loadingHistory} description={<>{aliasAmount(state.history.total)} pengajuan pada alokasi dan kanal tujuan ini.</>}
        actions={<Button variant="secondary" disabled={locked || state.loadingHistory} onClick={() => void editor.loadHistory()}>Muat ulang riwayat</Button>}>
        <div className={styles.desk}><div>
          <DataList label="Riwayat pemetaan kanal" items={state.loadingHistory ? [] : state.history.items} getKey={item => item.id} loading={state.loadingHistory}
            empty={state.error === null ? { title: "Belum ada pengajuan pemetaan pada halaman ini." } : undefined}
            renderTitle={item => item.reason} renderMeta={item => <>#{item.id} · pengaturan versi {item.poolVersion}</>} renderBadge={item => <Badge status={aliasStatus[item.status]} />}
            onSelect={item => void editor.chooseProposal(item.id)} selectedKey={state.selected?.id ?? null} disabled={locked}
            openLabel={item => item.reason + " #" + item.id + " · pengaturan versi " + item.poolVersion + " " + aliasStatus[item.status]}
            pagination={pager("riwayat", state.historyOffset, state.history.items.length, state.history.total, locked, offset => void editor.loadHistory(offset))} />
        </div>
        <section className={styles.details} aria-busy={state.loadingDetail}>{state.loadingDetail ? <StateView kind="loading" title="Memeriksa pengajuan terbaru…" /> : state.selected ? <>
          <h2>Pengajuan #{state.selected.id}</h2><AliasFacts proposal={state.selected} />
          {state.selected.status === "proposed" && editor.staleBase && <StateView kind="unknown" title="Dasar pengajuan telah berubah. Pengajuan ini belum dapat disetujui; periksa pemetaan dan pengaturan terbaru." />}
          {state.selected.status === "proposed" && state.current.source.poolState === "active" && <StateView kind="unknown" title="Alokasi masih aktif. Pemetaan belum dapat disetujui." />}
          {state.selected.decision && <section className={styles.section}><h3>Keputusan tercatat</h3><p>{state.selected.decision.reviewReference}</p>
            <p className={common.preserve}>{state.selected.decision.reason}</p><p>{formatDateTime(state.selected.decision.decidedAt)}.</p></section>}
          <section className={styles.section}><AliasDecisionForm key={state.selected.id + ":" + state.current.source.poolVersion + ":" + state.current.mapping?.appliedAliasVersion}
            proposal={state.selected} disabled={locked} fresh={state.fresh} approvable={!editor.staleBase && state.current.source.poolState === "inactive"}
            canPropose={editor.canPropose} canApprove={editor.canApprove} onReview={(action, reference, reason) => editor.reviewDecision(action, reference, reason)} /></section>
        </> : <><h2>Pilih pengajuan</h2><p>Periksa perubahan dan dasar pemetaan sebelum mengambil keputusan.</p></>}</section></div>
      </Section>}</>}
  </>;
}
/** The shared pager (hidden when one page holds everything), 25 rows per page as the API serves them. */
function pager(name: string, offset: number, count: number, total: string, disabled: boolean, onPage: (offset: number) => void) {
  return { label: "Halaman " + name, start: offset + 1, end: offset + count, total: Number(total), disabled, hasPrevious: offset > 0,
    hasNext: !(offset >= 10000 || BigInt(offset + 25) >= BigInt(total)), onPrevious: () => onPage(offset - 25), onNext: () => onPage(offset + 25) };
}
