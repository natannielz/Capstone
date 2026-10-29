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
import { canUseStockSourceSuspensions, IdentityFailure, identityMessage, sessionTiming, type IdentitySession } from "../identity/identity-workflow";
import type { AccessScope } from "../organizations/access-scope";
import { sourceActor, sourceLocationKey } from "./stock-source-workflow";
import { createStockSourceSuspensionGateway } from "./stock-source-suspension-client";
import { StockSourceSuspensionEditor } from "./stock-source-suspension-editor-state";
import type { SourceSuspensionCommand, SuspensionPreview } from "./stock-source-suspension-workflow";
import common from "../administration/access.module.css";
import styles from "./stock-source-suspension.module.css";
import { formatDateTime, formatNumber } from "@/lib/format";
import { Button } from '@/components/workspace-kit/button';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { Section } from '@/components/workspace-kit/section';
import { StateView } from '@/components/workspace-kit/state-view';
import { DataList } from '@/components/workspace-kit/data-list';
import { Facts } from '@/components/workspace-kit/facts';

const path = "/inventory/sources/suspensions";
const factorHref = "/identity/account?operation=stock.source.suspend&returnPath=" + encodeURIComponent(path);
type Loaded = { actorId: string; actions: string[]; session: IdentitySession; observedAt: number; generation: number };
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
    <PageHeader title="Penghentian sumber stok" description="Tinjau alokasi dan seluruh kanal yang terdampak sebelum menghentikan penggunaannya." />
    <div id="suspension-workspace" className="ui-stack">{loaded ? <SuspensionDesk key={loaded.actorId + ":" + loaded.generation} loaded={loaded} onRefresh={refresh} /> :
      (loading ? <StateView kind="loading" title="Memeriksa sesi dan akses" />
        : <SessionAccessState error={error} returnPath={path} onRetry={() => void refresh()} detail={error !== null ? message(error) : undefined} />)}</div>
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
  if (state.expired || state.suspended) return <SessionAccessState error={state.error} kind={state.expired ? 'expired' : lostAccessKind(state.error)} returnPath={path} busy={busy} onRetry={() => void onRefresh()} detail={state.error !== null ? message(state.error) : undefined} />;
  return <>
    <AdministrationNavigation current="source-suspensions" actions={loaded.actions} locked={locked} />
    <Section title="Periksa sumber yang ditugaskan" id="suspension-account-heading" description={<>Petugas: <strong>{loaded.session.account?.displayName ?? loaded.session.account?.verifiedEmail}</strong></>}
      actions={<Button variant="secondary" disabled={busy || state.review !== null} onClick={() => void onRefresh()}>Muat ulang sesi dan akses</Button>}>
      {editor.canSuspend && <p className={common.help}>{state.fresh ? "Verifikasi dua langkah masih baru. " : "Penghentian memerlukan verifikasi dua langkah terbaru. "}
        <Link href={factorHref} aria-disabled={busy} onClick={event => { if (busy) event.preventDefault(); }}>Verifikasi untuk penghentian</Link></p>}
    </Section>
    <aside className={styles.notice}><strong>Penghentian berlaku untuk seluruh kanal pada alokasi ini.</strong>
      <p>Pesanan dan kewajiban yang sudah ada tetap perlu diselesaikan. Pengoperasian kembali memerlukan pemeriksaan dan persetujuan tersendiri.</p></aside>
    {state.sending && <p role="status">Menyimpan penghentian… Tunggu hasil sebelum melanjutkan.</p>}
    {state.error !== null && <StateView kind="error" title={message(state.error)} />}
    {state.recoveryError !== null && <StateView kind="error" title={message(state.recoveryError)} />}
    {state.references.length > 0 && <Section title="Periksa permintaan semula" id="suspension-recovery-context">
      <p>Masukkan referensi alokasi yang digunakan saat mengirim penghentian. Pemeriksaan ini tidak mengirim penghentian baru.</p>
      <label><span id="suspension-recovery-label">Referensi alokasi semula</span><input aria-labelledby="suspension-recovery-label"
        inputMode="numeric" maxLength={19} value={state.recoveryPoolId} disabled={busy || state.review !== null}
        onChange={event => editor.setRecoveryPool(event.target.value)} /></label>
      {!editor.canSuspend && <p className={common.help}>Pemulihan penghentian memerlukan izin menghentikan sumber pada seluruh kanal terkait.</p>}
    </Section>}
    <OperationRecovery entries={state.references} journal={editor.journal}
      disabled={state.sending || state.review !== null || !editor.canSuspend || !state.fresh || !/^[1-9][0-9]{0,18}$/.test(state.recoveryPoolId)}
      recoveryAdapters={{ "stock.source.suspend": { lookup: editor.receipt } }} onResolved={editor.reconciled} onFailure={editor.recoveryFailed} onBusyChange={editor.setRecovering} />
    {state.result && <div ref={resultFocus} tabIndex={-1} className={common.success} role="status"><h2>Penghentian tercatat</h2>
      <p>Alokasi {state.result.physicalPoolId}. Referensi penghentian {state.result.suspensionId}, versi {state.result.suspensionVersion}.</p>
      <p>Kewajiban yang sudah ada tetap perlu diperiksa. Muat sumber kembali sebelum melakukan tindakan lain.</p></div>}
    {state.review && <div className={styles.review} ref={reviewFocus} tabIndex={-1}>
      <StopReview command={state.review} busy={busy} onSubmit={confirmed => void editor.submitReviewed(confirmed)} onCancel={() => editor.cancelReview()} />
    </div>}
    <Section title="Pilih alokasi" id="suspension-pool-heading" busy={state.loadingPool} description="Gunakan referensi alokasi dari pengaturan sumber atau penugasan Anda.">
      <form className={styles.selection} onSubmit={event => { event.preventDefault(); void editor.loadPool(); }}>
        <label><span id="suspension-pool-label">Referensi alokasi</span><input aria-labelledby="suspension-pool-label" inputMode="numeric"
          required maxLength={19} value={state.poolInput} disabled={locked} onChange={event => editor.changePool(event.target.value)} /></label>
        <Button type="submit" variant="secondary" disabled={locked || state.loadingPool || !state.poolInput || !editor.canRead && !state.fresh}>Muat alokasi</Button>
      </form>
      {state.loadingPool && <p role="status">Memeriksa alokasi dan kanal…</p>}
      {state.preview && <div className={styles.preview}><PreviewFacts preview={state.preview} />
        {editor.canSuspend && <form className={styles.form} onSubmit={event => { event.preventDefault(); editor.reviewStop(); }}>
          <label><span id="suspension-reason-label">Alasan penghentian</span><textarea aria-labelledby="suspension-reason-label" required rows={3}
            maxLength={1000} value={state.reason} disabled={locked || !state.fresh} onChange={event => editor.changeReason(event.target.value)} /></label>
          <Button type="submit" variant="primary" disabled={locked || state.loadingPool || !state.fresh || !state.reason.trim()}>Tinjau penghentian</Button>
        </form>}
      </div>}
    </Section>
    {editor.canRead && state.poolId && <Section title="Riwayat penghentian" busy={state.loadingHistory} description={<>{formatNumber(BigInt(state.history.total))} catatan pada alokasi {state.poolId}.</>}
      actions={<Button variant="secondary" disabled={locked || state.loadingPool || state.loadingHistory} onClick={() => void editor.loadHistory()}>Muat ulang riwayat</Button>}>
      <div className={styles.historyGrid}><div>
        <DataList label="Riwayat penghentian" items={state.loadingHistory ? [] : state.history.items} getKey={item => item.id} loading={state.loadingHistory}
          empty={state.error === null ? { title: "Belum ada catatan pada halaman ini." } : undefined}
          renderTitle={item => item.reason} renderMeta={item => <>Penghentian #{item.id} · versi {item.suspensionVersion} · {formatDateTime(item.suspendedAt)}</>}
          onSelect={item => void editor.chooseHistory(item.id)} selectedKey={state.selected?.id ?? null} disabled={locked}
          openLabel={item => item.reason + " Penghentian #" + item.id + " · versi " + item.suspensionVersion + " " + formatDateTime(item.suspendedAt)}
          pagination={{ label: "Halaman riwayat", start: state.historyOffset + 1, end: state.historyOffset + state.history.items.length, total: Number(state.history.total), disabled: locked,
            hasPrevious: state.historyOffset > 0, hasNext: !(state.historyOffset >= 10000 || BigInt(state.historyOffset + 25) >= BigInt(state.history.total)),
            onPrevious: () => void editor.loadHistory(state.historyOffset - 25), onNext: () => void editor.loadHistory(state.historyOffset + 25) }} />
      </div><section className={styles.detail} aria-busy={state.loadingDetail}>{state.loadingDetail ? <StateView kind="loading" title="Memeriksa catatan…" /> : state.selected ? <>
        <h3>Penghentian #{state.selected.id}</h3><p className={common.preserve}>{state.selected.reason}</p>
        <Facts columns={1} items={[{ term: "Alokasi", value: <>{state.selected.physicalKey} · {state.selected.physicalPoolId}</> },
          { term: "Versi pengaturan saat dihentikan", value: state.selected.poolVersion }, { term: "Versi penghentian", value: state.selected.suspensionVersion },
          { term: "Keadaan sebelumnya", value: state.selected.wasActive ? "Aktif" : "Tidak aktif" }]} />
        <h4>Kanal yang tercatat</h4><Locations locations={state.selected.affectedLocations} />
        <p className={common.help}>Catatan ini menunjukkan keadaan saat penghentian, bukan persetujuan pengoperasian kembali.</p>
      </> : <><h3>Pilih catatan</h3><p>Periksa alasan, versi, dan kanal yang tercatat saat sumber dihentikan.</p></>}</section></div>
    </Section>}
  </>;
}
function Locations({ locations }: { locations: readonly AccessScope[] }) {
  return <ul className={styles.locations}>{locations.map(location => <li key={sourceLocationKey(location)}>
    <strong>Kanal {location.channelId}</strong><span>Organisasi {location.organizationId} · Toko {location.shopId} · Gudang {location.warehouseId}</span>
  </li>)}</ul>;
}
function PreviewFacts({ preview }: { preview: SuspensionPreview }) {
  return <section><h3>Alokasi {preview.physicalPoolId}</h3>
    <Facts columns={1} items={[{ term: "Keadaan saat diperiksa", value: preview.active ? "Aktif" : "Tidak aktif" }, { term: "Versi pengaturan", value: preview.poolVersion },
      { term: "Versi penghentian terakhir", value: preview.suspensionVersion === "0" ? "Belum tercatat" : preview.suspensionVersion }]} />
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
    <div className={common.actions}><Button type="submit" variant="primary" disabled={busy || !confirmed}>Konfirmasi penghentian</Button>
      <Button variant="secondary" disabled={busy} onClick={onCancel}>Kembali</Button></div>
  </form>;
}
