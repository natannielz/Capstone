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
import { canUseStockSourceSuspensions, IdentityFailure, identityMessage, sessionTiming, type IdentitySession } from "../identity/identity-workflow";
import type { AccessScope } from "../organizations/access-scope";
import { sourceActor, sourceLocationKey } from "./stock-source-workflow";
import { createStockSourceSuspensionGateway } from "./stock-source-suspension-client";
import { StockSourceSuspensionEditor } from "./stock-source-suspension-editor-state";
import type { SourceSuspensionCommand, SuspensionPreview } from "./stock-source-suspension-workflow";
import common from "../administration/access.module.css";
import styles from "./stock-source-suspension.module.css";

const path = "/inventory/sources/suspensions";
const loginHref = "/identity/login?returnPath=" + encodeURIComponent(path);
const factorHref = "/identity/account?operation=stock.source.suspend&returnPath=" + encodeURIComponent(path);
type Loaded = { actorId: string; actions: string[]; session: IdentitySession; observedAt: number; generation: number };
const lostSession = (error: unknown) => (error instanceof AccessFailure || error instanceof IdentityFailure) && (error.status === 401 || error.code === "actor_changed");
function message(error: unknown) {
  if (error instanceof RequestNotSentError) return accessMessage(error);
  if (error instanceof IdentityFailure) return identityMessage(error);
  if (error instanceof AccessFailure) {
    if (error.code === "stock_source_suspension_input_invalid" || error.code === "stock_source_input_invalid" || error.code === "stock_source_selection_required")
      return "Periksa referensi alokasi dan isi alasan penghentian. Muat alokasi untuk meninjau kanal yang terdampak.";
    if (error.code === "stock_source_suspension_changed")
      return "Pengaturan atau penghentian telah berubah. Muat alokasi kembali dan periksa informasi terbaru sebelum mengonfirmasi.";
    if (error.code === "step_up_required") return "Verifikasi dua langkah terbaru diperlukan untuk memeriksa atau menghentikan sumber ini.";
    if (error.status >= 500 || error.status === 0)
      return "Hasil belum dapat dipastikan. Jika ada referensi pemulihan, gunakan Periksa hasil. Jangan kirim penghentian baru sebelum hasilnya jelas.";
    return accessMessage(error);
  }
  return "Koneksi terputus. Periksa hasil permintaan melalui referensi pemulihan yang tersimpan sebelum melanjutkan.";
}
export function StockSourceSuspensionWorkspace() {
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
        account.allowedActions.some(action => typeof action !== "string" || !/^[a-z]+(?:[.-][a-z]+){1,5}$/.test(action)))
        throw new AccessFailure(502, "stock_source_suspension_response_invalid");
      if (!canUseStockSourceSuspensions(account.allowedActions)) throw new AccessFailure(403, "access_denied");
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
    <a className={styles.skip} href="#suspension-workspace">Langsung ke penghentian sumber</a>
    <header className={common.header}><div><Link className={common.brand} href="/identity/account">Unit Toko</Link>
      <p className={styles.eyebrow}>PENGELOLAAN PERSEDIAAN</p><h1>Penghentian sumber stok</h1>
      <p>Tinjau alokasi dan seluruh kanal yang terdampak sebelum menghentikan penggunaannya.</p></div></header>
    <div id="suspension-workspace">{loaded ? <SuspensionDesk key={loaded.actorId + ":" + loaded.generation} loaded={loaded} onRefresh={refresh} /> :
      <section className={common.state} aria-busy={loading}><h2>{loading ? "Memeriksa sesi dan akses" : lostSession(error) ? "Masuk untuk memeriksa sumber" : "Penghentian belum tersedia"}</h2>
        {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
        <div className={common.actions}>{lostSession(error) && <Link className={common.primary} href={loginHref}>Masuk kembali</Link>}
          <button type="button" className={common.secondary} disabled={loading} onClick={() => void refresh()}>Periksa kembali</button>
          <Link href="/identity/account">Kembali ke akun saya</Link></div></section>}</div>
  </main>;
}
function SuspensionDesk({ loaded, onRefresh }: { loaded: Loaded; onRefresh: () => Promise<void> }) {
  const [editor] = useState(() => new StockSourceSuspensionEditor({ actorId: loaded.actorId, actions: loaded.actions, session: loaded.session,
    observedAt: loaded.observedAt, now: () => performance.now(), gateway: createStockSourceSuspensionGateway(),
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
    <AdministrationNavigation current="source-suspensions" actions={loaded.actions} locked={locked} />
    <section className={common.accountSection} aria-labelledby="suspension-account-heading">
      <div className={styles.heading}><div><h2 id="suspension-account-heading">Periksa sumber yang ditugaskan</h2>
        <p>Petugas: <strong>{loaded.session.account?.displayName ?? loaded.session.account?.verifiedEmail}</strong></p></div>
        <button type="button" className={common.secondary} disabled={busy || state.review !== null} onClick={() => void onRefresh()}>Periksa sesi dan akses</button></div>
      {editor.canSuspend && <p className={common.help}>{state.fresh ? "Verifikasi dua langkah masih baru. " : "Penghentian memerlukan verifikasi dua langkah terbaru. "}
        <Link href={factorHref} aria-disabled={busy} onClick={event => { if (busy) event.preventDefault(); }}>Verifikasi untuk penghentian</Link></p>}
    </section>
    <aside className={styles.notice}><strong>Penghentian berlaku untuk seluruh kanal pada alokasi ini.</strong>
      <p>Pesanan dan kewajiban yang sudah ada tetap perlu diselesaikan. Pengoperasian kembali memerlukan pemeriksaan dan persetujuan tersendiri.</p></aside>
    {state.sending && <p role="status">Menyimpan penghentian… Tunggu hasil sebelum melanjutkan.</p>}
    {state.error !== null && <p role="alert" className={common.alert}>{message(state.error)}</p>}
    {state.recoveryError !== null && <p role="alert" className={common.alert}>{message(state.recoveryError)}</p>}
    {state.references.length > 0 && <section className={common.accountSection} aria-labelledby="suspension-recovery-context">
      <h2 id="suspension-recovery-context">Periksa permintaan semula</h2>
      <p>Masukkan referensi alokasi yang digunakan saat mengirim penghentian. Pemeriksaan ini tidak mengirim penghentian baru.</p>
      <label><span id="suspension-recovery-label">Referensi alokasi semula</span><input aria-labelledby="suspension-recovery-label"
        inputMode="numeric" maxLength={19} value={state.recoveryPoolId} disabled={busy || state.review !== null}
        onChange={event => editor.setRecoveryPool(event.target.value)} /></label>
      {!editor.canSuspend && <p className={common.help}>Pemulihan penghentian memerlukan izin menghentikan sumber pada seluruh kanal terkait.</p>}
    </section>}
    <OperationRecovery entries={state.references} journal={editor.journal}
      disabled={state.sending || state.review !== null || !editor.canSuspend || !state.fresh || !/^[1-9][0-9]{0,18}$/.test(state.recoveryPoolId)}
      recoveryAdapters={{ "stock.source.suspend": { lookup: editor.receipt } }} onResolved={editor.reconciled} onFailure={editor.recoveryFailed} onBusyChange={editor.setRecovering} />
    {state.result && <div ref={resultFocus} tabIndex={-1} className={common.success} role="status"><h2>Penghentian tercatat</h2>
      <p>Alokasi {state.result.physicalPoolId}. Referensi penghentian {state.result.suspensionId}, versi {state.result.suspensionVersion}.</p>
      <p>Kewajiban yang sudah ada tetap perlu diperiksa. Muat sumber kembali sebelum melakukan tindakan lain.</p></div>}
    {state.review && <div className={styles.review} ref={reviewFocus} tabIndex={-1}>
      <StopReview command={state.review} busy={busy} onSubmit={confirmed => void editor.submitReviewed(confirmed)} onCancel={() => editor.cancelReview()} />
    </div>}
    <section className={common.accountSection} aria-labelledby="suspension-pool-heading" aria-busy={state.loadingPool}>
      <h2 id="suspension-pool-heading">Pilih alokasi</h2><p>Gunakan referensi alokasi dari pengaturan sumber atau penugasan Anda.</p>
      <form className={styles.selection} onSubmit={event => { event.preventDefault(); void editor.loadPool(); }}>
        <label><span id="suspension-pool-label">Referensi alokasi</span><input aria-labelledby="suspension-pool-label" inputMode="numeric"
          required maxLength={19} value={state.poolInput} disabled={locked} onChange={event => editor.changePool(event.target.value)} /></label>
        <button className={common.secondary} disabled={locked || state.loadingPool || !state.poolInput || !editor.canRead && !state.fresh}>Muat alokasi</button>
      </form>
      {state.loadingPool && <p role="status">Memeriksa alokasi dan kanal…</p>}
      {state.preview && <div className={styles.preview}><PreviewFacts preview={state.preview} />
        {editor.canSuspend && <form className={styles.form} onSubmit={event => { event.preventDefault(); editor.reviewStop(); }}>
          <label><span id="suspension-reason-label">Alasan penghentian</span><textarea aria-labelledby="suspension-reason-label" required rows={3}
            maxLength={1000} value={state.reason} disabled={locked || !state.fresh} onChange={event => editor.changeReason(event.target.value)} /></label>
          <button className={common.primary} disabled={locked || state.loadingPool || !state.fresh || !state.reason.trim()}>Tinjau penghentian</button>
        </form>}
      </div>}
    </section>
    {editor.canRead && state.poolId && <section className={styles.history} aria-busy={state.loadingHistory}>
      <header className={styles.heading}><div><h2>Riwayat penghentian</h2><p>{BigInt(state.history.total).toLocaleString("id-ID")} catatan pada alokasi {state.poolId}.</p></div>
        <button type="button" className={common.secondary} disabled={locked || state.loadingPool || state.loadingHistory} onClick={() => void editor.loadHistory()}>Muat ulang riwayat</button></header>
      <div className={styles.historyGrid}><section className={common.queue}>
        {state.loadingHistory ? <p role="status" className={common.empty}>Memuat riwayat…</p> : <>
          <ul className={common.queueList}>{state.history.items.map(item => <li key={item.id}><button type="button" disabled={locked}
            className={state.selected?.id === item.id ? common.selectedRow : common.queueRow} aria-pressed={state.selected?.id === item.id}
            onClick={() => void editor.chooseHistory(item.id)}><strong>{item.reason}</strong><span>Penghentian #{item.id} · versi {item.suspensionVersion}</span>
            <span>{new Date(item.suspendedAt).toLocaleString("id-ID", { timeZone: "Asia/Jakarta" })} WIB</span></button></li>)}</ul>
          {!state.history.items.length && <p className={common.empty}>Belum ada catatan pada halaman ini.</p>}
          <div className={common.pagination} aria-label="Halaman riwayat">
            <button type="button" className={common.secondary} aria-label="Halaman riwayat sebelumnya" disabled={locked || state.historyOffset === 0} onClick={() => void editor.loadHistory(state.historyOffset - 25)}>Sebelumnya</button>
            <span>{Math.floor(state.historyOffset / 25) + 1}</span><button type="button" className={common.secondary} aria-label="Halaman riwayat berikutnya"
              disabled={locked || state.historyOffset >= 10000 || BigInt(state.historyOffset + 25) >= BigInt(state.history.total)} onClick={() => void editor.loadHistory(state.historyOffset + 25)}>Berikutnya</button>
          </div></>}
      </section><section className={styles.detail} aria-busy={state.loadingDetail}>{state.loadingDetail ? <p role="status">Memeriksa catatan…</p> : state.selected ? <>
        <h3>Penghentian #{state.selected.id}</h3><p className={common.preserve}>{state.selected.reason}</p>
        <dl className={styles.facts}><div><dt>Alokasi</dt><dd>{state.selected.physicalKey} · {state.selected.physicalPoolId}</dd></div>
          <div><dt>Versi pengaturan saat dihentikan</dt><dd>{state.selected.poolVersion}</dd></div>
          <div><dt>Versi penghentian</dt><dd>{state.selected.suspensionVersion}</dd></div>
          <div><dt>Keadaan sebelumnya</dt><dd>{state.selected.wasActive ? "Aktif" : "Tidak aktif"}</dd></div></dl>
        <h4>Kanal yang tercatat</h4><Locations locations={state.selected.affectedLocations} />
        <p className={common.help}>Catatan ini menunjukkan keadaan saat penghentian, bukan persetujuan pengoperasian kembali.</p>
      </> : <><h3>Pilih catatan</h3><p>Periksa alasan, versi, dan kanal yang tercatat saat sumber dihentikan.</p></>}</section></div>
    </section>}
  </>;
}
function Locations({ locations }: { locations: readonly AccessScope[] }) {
  return <ul className={styles.locations}>{locations.map(location => <li key={sourceLocationKey(location)}>
    <strong>Kanal {location.channelId}</strong><span>Organisasi {location.organizationId} · Toko {location.shopId} · Gudang {location.warehouseId}</span>
  </li>)}</ul>;
}
function PreviewFacts({ preview }: { preview: SuspensionPreview }) {
  return <section><h3>Alokasi {preview.physicalPoolId}</h3>
    <dl className={styles.facts}><div><dt>Keadaan saat diperiksa</dt><dd>{preview.active ? "Aktif" : "Tidak aktif"}</dd></div>
      <div><dt>Versi pengaturan</dt><dd>{preview.poolVersion}</dd></div>
      <div><dt>Versi penghentian terakhir</dt><dd>{preview.suspensionVersion === "0" ? "Belum tercatat" : preview.suspensionVersion}</dd></div></dl>
    <h4>Seluruh kanal yang terdampak</h4><Locations locations={preview.affectedLocations} />
  </section>;
}
function StopReview({ command, busy, onSubmit, onCancel }: { command: SourceSuspensionCommand; busy: boolean; onSubmit: (confirmed: boolean) => void; onCancel: () => void }) {
  const [confirmed, setConfirmed] = useState(false);
  return <form onSubmit={event => { event.preventDefault(); onSubmit(confirmed); }} aria-labelledby="suspension-review-heading">
    <h2 id="suspension-review-heading">Konfirmasi penghentian</h2><PreviewFacts preview={command.preview} />
    <h3>Alasan</h3><p className={common.preserve}>{command.body.reason}</p>
    <label className={common.checkbox}><input type="checkbox" required checked={confirmed} disabled={busy} onChange={event => setConfirmed(event.target.checked)} />
      <span>Saya telah memeriksa alokasi dan seluruh kanal di atas. Saya memahami bahwa kewajiban yang sudah ada tetap perlu diselesaikan.</span></label>
    <div className={common.actions}><button className={common.primary} disabled={busy || !confirmed}>Konfirmasi penghentian</button>
      <button type="button" className={common.secondary} disabled={busy} onClick={onCancel}>Kembali</button></div>
  </form>;
}
