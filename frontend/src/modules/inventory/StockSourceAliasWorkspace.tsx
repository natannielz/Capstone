"use client";

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

const path = "/inventory/sources/aliases";
const loginHref = "/identity/login?returnPath=" + encodeURIComponent(path);
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
const lostSession = (error: unknown) => (error instanceof AccessFailure || error instanceof IdentityFailure) && (error.status === 401 || error.code === "actor_changed");
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
    <header className={common.header}><div><Link className={common.brand} href="/identity/account">Unit Toko</Link>
      <p className={styles.eyebrow}>PENGELOLAAN PERSEDIAAN</p><h1>Pemetaan kanal</h1>
      <p>Kelola penggunaan satu alokasi fisik oleh kanal lain, dengan pengajuan dan peninjauan terpisah.</p></div></header>
    <div id="alias-workspace">{loaded ? <AliasDesk key={loaded.actorId + ":" + loaded.generation} loaded={loaded} onRefresh={refresh} /> :
      <section className={common.state} aria-busy={loading}><h2>{loading ? "Memeriksa sesi dan akses" : lostSession(error) ? "Masuk untuk mengelola pemetaan" : "Pemetaan belum tersedia"}</h2>
        {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
        <div className={common.actions}>{lostSession(error) && <Link className={common.primary} href={loginHref}>Masuk kembali</Link>}
          <button type="button" className={common.secondary} disabled={loading} onClick={() => void refresh()}>Periksa kembali</button>
          <Link href="/identity/account">Kembali ke akun saya</Link></div></section>}</div>
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
  if (state.expired || state.suspended) return <section className={common.state}><h2>Sesi dan akses perlu diperiksa kembali</h2>
    {state.error !== null && <p role="alert">{message(state.error)}</p>}
    <p>Data sebelumnya disembunyikan. Referensi permintaan tetap disimpan untuk akun pemiliknya.</p>
    <button type="button" className={common.primary} disabled={busy} onClick={() => void onRefresh()}>Periksa sesi terbaru</button><Link href={loginHref}>Masuk kembali</Link></section>;
  return <>
    <AdministrationNavigation current="source-aliases" actions={loaded.actions} locked={locked} />
    <section className={common.accountSection} aria-labelledby="alias-location-title">
      <div className={styles.heading}><div><h2 id="alias-location-title">Pilih ruang kerja</h2>
        <p className={common.help}>Petugas: <strong>{loaded.session.account?.displayName ?? loaded.session.account?.verifiedEmail}</strong></p></div>
        <button type="button" className={common.secondary} disabled={busy || state.review !== null} onClick={() => void onRefresh()}>Periksa sesi dan akses</button></div>
      <div className={styles.location}>
        <label><span id="alias-purpose-label">Akses lokasi</span><select aria-labelledby="alias-purpose-label" disabled={editor.selectionLocked || state.loadingWarehouses} value={state.purpose}
          onChange={event => void editor.changePurpose(event.target.value as SourcePurpose)}>
          {editor.canRead && <option value="inventory-source-history">Melihat pengaturan dan riwayat</option>}
          {editor.canPropose && <option value="inventory-source-propose">Mengajukan atau memulihkan pengajuan</option>}
          {editor.canApprove && <option value="inventory-source-review">Meninjau atau memulihkan keputusan</option>}</select></label>
        <button type="button" className={common.secondary} disabled={editor.selectionLocked || state.loadingWarehouses} onClick={() => void editor.loadWarehouses()}>Muat ulang lokasi</button>
      </div>
      {state.loadingWarehouses && <p role="status">Memuat lokasi yang ditugaskan…</p>}
      {!state.loadingWarehouses && !state.warehouses.length && <p className={common.help}>Belum ada lokasi untuk tugas ini. Periksa penugasan dan verifikasi akun.</p>}
      {editor.canApprove && <p className={common.help}>{state.fresh ? "Verifikasi dua langkah masih baru. " : "Peninjauan memerlukan verifikasi dua langkah terbaru. "}
        <Link href={factorHref} aria-disabled={busy} onClick={event => { if (busy) event.preventDefault(); }}>Verifikasi untuk peninjauan</Link></p>}
    </section>
    <aside className={styles.notice}><strong>Satu alokasi fisik, tanpa saldo ganda.</strong>
      <p>Pemetaan mengatur hubungan antar kanal. Jumlah barang tetap. Pengoperasian sumber memerlukan pemeriksaan kesiapan dan serah terima tersendiri.</p></aside>
    {state.sending && <p role="status">Menyimpan permintaan… Tunggu hasil sebelum melanjutkan.</p>}
    {state.error !== null && <p role="alert" className={common.alert}>{message(state.error)}</p>}
    {state.recoveryError !== null && <p role="alert" className={common.alert}>{message(state.recoveryError)}</p>}
    {state.references.length > 0 && <section className={common.accountSection} aria-labelledby="alias-recovery-context">
      <h2 id="alias-recovery-context">Pilih konteks permintaan semula</h2>
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
    </section>}
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
    {!editor.canRead ? <section className={common.state}><h2>Akses pengaturan diperlukan</h2>
      <p>Izin mengajukan atau meninjau tidak otomatis memberi akses membaca pengaturan. Pemulihan permintaan milik Anda tetap tersedia.</p></section> :
      <><section className={common.accountSection}><h2>Alokasi dan kanal</h2>
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
      </section>
      <div className={styles.desk}>
        <section className={common.queue} aria-busy={state.loadingSources}><div className={common.queueHeader}><h2>Alokasi terdaftar</h2>
          <p className={common.help}>{aliasAmount(state.sources.total)} alokasi pada gudang dan kanal pemilik.</p>
          <button type="button" className={common.secondary} disabled={locked || state.loadingSources || !state.warehouse} onClick={() => void editor.loadSources()}>Muat ulang alokasi</button></div>
          {state.loadingSources ? <p role="status" className={common.empty}>Memuat alokasi…</p> : <>
            <ul className={common.queueList}>{state.sources.items.map(item => <li key={item.physicalPoolId}><button type="button"
              className={state.poolId === item.physicalPoolId ? common.selectedRow : common.queueRow} disabled={locked} aria-pressed={state.poolId === item.physicalPoolId}
              onClick={() => editor.chooseSource(item.physicalPoolId)}><strong>{item.settings.label}</strong><span>{item.settings.physicalKey}</span>
              <span>Versi {item.poolVersion} · {item.poolState === "inactive" ? "Belum aktif" : "Aktif"}</span></button></li>)}</ul>
            {!state.sources.items.length && <p className={common.empty}>{state.references.length ? "Periksa hasil permintaan sebelum memuat alokasi." : "Pilih pemilik alokasi atau muat ulang daftar."}</p>}
            <Pager name="alokasi" offset={state.sourceOffset} total={state.sources.total} disabled={locked || !state.warehouse} onPage={offset => void editor.loadSources(offset)} /></>}
        </section>
        <section className={styles.details} aria-busy={state.loadingCurrent}>{state.loadingCurrent ? <p role="status">Memeriksa pemetaan saat ini…</p> : state.current ? <>
          <h2>{state.current.source.settings.label}</h2><p><strong>{bindingLabel[state.current.bindingState]}</strong></p>
          <p>Pemilik: {warehouseLabel(state.warehouse)}<br />Tujuan: {warehouseLabel(state.requested)}</p>
          <p className={common.help}>Alokasi {state.current.source.physicalPoolId}; pengaturan sumber versi {state.current.source.poolVersion}.
            Pemetaan {state.current.mapping?.appliedAliasVersion ? "versi " + state.current.mapping.appliedAliasVersion : "belum ada"}.</p>
          {state.current.bindingState === "stale" && <p className={common.alert}>Pengaturan sumber telah berubah. Pemetaan memerlukan pengajuan dan peninjauan ulang.</p>}
          <details><summary>Lihat pengaturan sumber saat ini</summary><RevisionFacts settings={state.current.source.settings} /></details>
          {editor.canPropose && <section className={styles.section}><AliasDraftForm enabled={state.enabled} reason={state.reason} disabled={locked}
            onChange={(enabled, reason) => editor.changeDraft(enabled, reason)} onReview={() => editor.reviewProposal()} /></section>}
        </> : <><h2>Pilih alokasi dan kanal tujuan</h2><p>Periksa hubungan kanal sebelum mengajukan atau meninjau pemetaan.</p></>}</section>
      </div>
      {state.current && <section className={styles.history} aria-busy={state.loadingHistory}><header className={styles.heading}><div>
        <h2>Riwayat pemetaan kanal</h2><p>{aliasAmount(state.history.total)} pengajuan pada alokasi dan kanal tujuan ini.</p></div>
        <button type="button" className={common.secondary} disabled={locked || state.loadingHistory} onClick={() => void editor.loadHistory()}>Muat ulang riwayat</button></header>
        <div className={styles.desk}><section className={common.queue}>
          {state.loadingHistory ? <p role="status" className={common.empty}>Memuat riwayat…</p> : <>
            <ul className={common.queueList}>{state.history.items.map(item => <li key={item.id}><button type="button" disabled={locked}
              className={state.selected?.id === item.id ? common.selectedRow : common.queueRow} aria-pressed={state.selected?.id === item.id}
              onClick={() => void editor.chooseProposal(item.id)}><strong>{item.reason}</strong><span>#{item.id} · pengaturan versi {item.poolVersion}</span>
              <span>{aliasStatus[item.status]}</span></button></li>)}</ul>
            {!state.history.items.length && <p className={common.empty}>Belum ada pengajuan pemetaan pada halaman ini.</p>}
            <Pager name="riwayat" offset={state.historyOffset} total={state.history.total} disabled={locked} onPage={offset => void editor.loadHistory(offset)} /></>}
        </section>
        <section className={styles.details} aria-busy={state.loadingDetail}>{state.loadingDetail ? <p role="status">Memeriksa pengajuan terbaru…</p> : state.selected ? <>
          <h2>Pengajuan #{state.selected.id}</h2><AliasFacts proposal={state.selected} />
          {state.selected.status === "proposed" && editor.staleBase && <p role="status" className={common.alert}>Dasar pengajuan telah berubah. Pengajuan ini belum dapat disetujui; periksa pemetaan dan pengaturan terbaru.</p>}
          {state.selected.status === "proposed" && state.current.source.poolState === "active" && <p className={common.alert}>Alokasi masih aktif. Pemetaan belum dapat disetujui.</p>}
          {state.selected.decision && <section className={styles.section}><h3>Keputusan tercatat</h3><p>{state.selected.decision.reviewReference}</p>
            <p className={common.preserve}>{state.selected.decision.reason}</p><p>{new Date(state.selected.decision.decidedAt).toLocaleString("id-ID", { timeZone: "Asia/Jakarta" })} WIB.</p></section>}
          <section className={styles.section}><AliasDecisionForm key={state.selected.id + ":" + state.current.source.poolVersion + ":" + state.current.mapping?.appliedAliasVersion}
            proposal={state.selected} disabled={locked} fresh={state.fresh} approvable={!editor.staleBase && state.current.source.poolState === "inactive"}
            canPropose={editor.canPropose} canApprove={editor.canApprove} onReview={(action, reference, reason) => editor.reviewDecision(action, reference, reason)} /></section>
        </> : <><h2>Pilih pengajuan</h2><p>Periksa perubahan dan dasar pemetaan sebelum mengambil keputusan.</p></>}</section></div>
      </section>}</>}
  </>;
}
function Pager({ name, offset, total, disabled, onPage }: { name: string; offset: number; total: string; disabled: boolean; onPage: (offset: number) => void }) {
  return <div className={common.pagination} aria-label={"Halaman " + name}>
    <button type="button" className={common.secondary} aria-label={"Halaman " + name + " sebelumnya"} disabled={disabled || offset === 0} onClick={() => onPage(offset - 25)}>Sebelumnya</button>
    <span>{Math.floor(offset / 25) + 1}</span><button type="button" className={common.secondary} aria-label={"Halaman " + name + " berikutnya"}
      disabled={disabled || offset >= 10000 || BigInt(offset + 25) >= BigInt(total)} onClick={() => onPage(offset + 25)}>Berikutnya</button>
  </div>;
}
