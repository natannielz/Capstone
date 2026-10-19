"use client";

import Link from "next/link";
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { createAccessGateway } from "../administration/access-client";
import { AdministrationNavigation } from "../administration/AdministrationNavigation";
import { OperationRecovery } from "../administration/OperationRecovery";
import { OperationJournal } from "../administration/operation-journal";
import { AccessFailure, accessMessage } from "../administration/workflow";
import { createIdentityGateway } from "../identity/identity-client";
import { canUseStockSources, IdentityFailure, identityMessage, sessionTiming, type IdentitySession } from "../identity/identity-workflow";
import { createStockSourceGateway } from "./stock-source-client";
import { StockSourceEditor } from "./stock-source-editor-state";
import { sourceActor, sourceLocationKey, sourceAgeSeconds, type SourceCommand, type SourceDecision, type SourceDetail, type SourceDraft } from "./stock-source-workflow";
import common from "../administration/access.module.css";
import styles from "./stock-source.module.css";
import { formatDateTime, numberFormatter } from "@/lib/format";

const path = "/inventory/sources";
const loginHref = "/identity/login?returnPath=" + encodeURIComponent(path);
const factorHref = "/identity/account?operation=stock.source.approve&returnPath=" + encodeURIComponent(path);
type Loaded = { actorId: string; actions: string[]; session: IdentitySession; observedAt: number; generation: number };
const statusLabel = { proposed: "Menunggu peninjauan", approved: "Disetujui · belum aktif", rejected: "Ditolak", withdrawn: "Ditarik" };
const modeLabel: Record<string, string> = { webLedger: "Catatan stok web", externalLedger: "Catatan stok sistem lain" };
const controlLabel: Record<string, string> = { exclusiveWebAllocation: "Alokasi khusus web", sharedWebLedger: "Catatan bersama pada web", externalHold: "Pemesanan ditahan di sistem lain" };
const amount = (value: string) => numberFormatter.format(BigInt(value));
function message(error: unknown) {
  if (error instanceof IdentityFailure) return identityMessage(error);
  if (error instanceof AccessFailure) {
    if (error.code === "stock_source_input_invalid") return "Periksa semua isian. Pilih cara pencatatan dan pengendalian, isi batas usia dengan detik bulat positif, serta cadangan dengan satuan dasar bulat mulai dari 0.";
    if (error.code === "stock_source_selection_required") return "Pilih gudang dan kanal dari daftar, lalu pilih pengajuan yang akan diperiksa.";
    if (error.code === "stock_source_response_invalid") return "Data belum dapat dipastikan. Periksa sesi dan data terbaru; referensi permintaan tetap disimpan.";
    if (error.code === "stock_source_profile_unavailable") return "Nama atau struktur gudang belum siap. Minta pengelola memeriksa data organisasi, lalu muat ulang pilihan.";
    if (error.code === "stock_source_already_decided" || error.code === "stock_source_revision_changed") return "Pengajuan telah berubah atau sudah diputuskan. Muat ulang daftar sebelum melanjutkan.";
    if (error.code === "stock_source_proposer_required") return "Hanya pengaju semula yang dapat menarik pengajuan ini.";
    if (error.code === "independent_actor_required" || error.code === "independent_person_required") return "Keputusan memerlukan peninjau yang berbeda dari pengaju.";
    if (error.code === "step_up_required") return "Lakukan verifikasi dua langkah terbaru sebelum meninjau atau memutuskan pengajuan.";
  }
  return accessMessage(error);
}
function sessionLost(error: unknown) { return (error instanceof AccessFailure || error instanceof IdentityFailure) && (error.status === 401 || error.code === "actor_changed"); }
export function StockSourceWorkspace() {
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
        account.allowedActions.some(action => typeof action !== "string" || !/^[a-z]+(?:[.-][a-z]+){1,5}$/.test(action))) throw new AccessFailure(502, "stock_source_response_invalid");
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
    <a className={styles.skip} href="#source-workspace">Langsung ke pengaturan sumber</a>
    <header className={common.header}><div><Link className={common.brand} href="/identity/account">Unit Toko</Link>
      <p className={styles.eyebrow}>Pengelolaan persediaan</p><h1>Sumber stok</h1>
      <p>Tentukan asal pencatatan dan tanggung jawab persediaan untuk setiap gudang dan kanal.</p></div></header>
    <div id="source-workspace">{loaded ? <SourceDesk key={loaded.actorId + ":" + loaded.generation} loaded={loaded} onRefresh={refresh} /> :
      <section className={common.state} aria-busy={loading}><h2>{loading ? "Memeriksa sesi dan akses" : sessionLost(error) ? "Masuk untuk mengelola sumber stok" : "Pengaturan belum tersedia"}</h2>
        {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
        <div className={common.actions}>{sessionLost(error) && <Link className={common.primary} href={loginHref}>Masuk kembali</Link>}
          <button type="button" className={common.secondary} disabled={loading} onClick={() => void refresh()}>Periksa kembali</button>
          <Link href="/identity/account">Kembali ke akun saya</Link></div></section>}</div>
  </main>;
}
function SourceDesk({ loaded, onRefresh }: { loaded: Loaded; onRefresh: () => Promise<void> }) {
  const [editor] = useState(() => new StockSourceEditor({ actorId: loaded.actorId, actions: loaded.actions, session: loaded.session,
    observedAt: loaded.observedAt, now: () => performance.now(), gateway: createStockSourceGateway(), journal: new OperationJournal(() => window.localStorage) }));
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
    <p>Data sebelumnya disembunyikan. Referensi permintaan tetap disimpan untuk akun pemiliknya.</p>
    {state.error !== null && <p role="alert" className={common.alert}>{message(state.error)}</p>}
    <button type="button" className={common.primary} disabled={busy} onClick={() => void onRefresh()}>Periksa sesi terbaru</button>
    <Link href={loginHref}>Masuk kembali</Link></section>;
  return <>
    <AdministrationNavigation current="sources" actions={loaded.actions} locked={locked} />
    <section className={common.accountSection} aria-labelledby="source-location-title">
      <div className={styles.headingRow}><div><h2 id="source-location-title">Ruang kerja gudang</h2>
        <p className={common.help}>Petugas: <strong>{loaded.session.account?.displayName ?? loaded.session.account?.verifiedEmail}</strong></p></div>
        <button type="button" className={common.secondary} disabled={busy} onClick={() => void onRefresh()}>Periksa sesi dan akses</button></div>
      <div className={styles.modeChoices} role="group" aria-label="Jenis pekerjaan">
        {editor.canPropose && <button type="button" aria-pressed={state.mode === "propose"} className={state.mode === "propose" ? common.primary : common.secondary}
          disabled={editor.selectionLocked} onClick={() => void editor.changeMode("propose")}>Pengajuan baru</button>}
        {editor.canApprove && <button type="button" aria-pressed={state.mode === "review"} className={state.mode === "review" ? common.primary : common.secondary}
          disabled={editor.selectionLocked} onClick={() => void editor.changeMode("review")}>Tinjau pengajuan</button>}
        {editor.canRead && <button type="button" aria-pressed={state.mode === "history"} className={state.mode === "history" ? common.primary : common.secondary}
          disabled={editor.selectionLocked} onClick={() => void editor.changeMode("history")}>Riwayat pengajuan</button>}
      </div>
      <label><span id="source-warehouse-label">Gudang dan kanal</span><select aria-labelledby="source-warehouse-label" value={state.warehouse ? sourceLocationKey(state.warehouse.location) : ""} disabled={editor.selectionLocked || state.loadingWarehouses}
        onChange={event => void editor.chooseWarehouse(state.warehouses.find(item => sourceLocationKey(item.location) === event.target.value)?.location ?? null)}>
        <option value="">{state.loadingWarehouses ? "Memuat gudang…" : "Pilih gudang dan kanal"}</option>
        {state.warehouses.map(item => <option key={sourceLocationKey(item.location)} value={sourceLocationKey(item.location)}>
          {item.organizationName} / {item.shopName} / {item.warehouseName} / {item.channelName}</option>)}</select></label>
      {state.warehouse && <p className={common.scopeSummary}>{state.warehouse.organizationName} / {state.warehouse.shopName}<br /><strong>{state.warehouse.warehouseName}</strong> / {state.warehouse.channelName}</p>}
      {!state.loadingWarehouses && state.warehouses.length === 0 && <p className={common.help}>Belum ada pilihan gudang untuk tugas ini. Periksa penugasan dan status verifikasi akun.</p>}
      <button type="button" className={common.secondary} disabled={editor.selectionLocked || state.loadingWarehouses} onClick={() => void editor.loadWarehouses()}>Muat ulang gudang</button>
      {editor.canApprove && <div className={styles.verification}><p>{state.fresh ? "Verifikasi dua langkah masih baru." : "Peninjauan memerlukan verifikasi dua langkah terbaru."}</p>
        <Link href={factorHref} aria-disabled={busy} onClick={event => { if (busy) event.preventDefault(); }}>Verifikasi untuk peninjauan</Link>
        <p className={common.help}>Setelah kembali, pilih dan periksa pengajuan lagi. Referensi permintaan yang belum selesai tetap disimpan.</p></div>}
    </section>
    <aside className={styles.notice}><strong>Persetujuan menyimpan pengaturan dalam keadaan belum aktif.</strong>
      <p>Saldo, jumlah tersedia, dan pemesanan barang belum berubah. Aktivasi memerlukan tahap kesiapan persediaan berikutnya.</p></aside>
    {state.sending && <p role="status">Menyimpan permintaan… Tunggu hasil sebelum melanjutkan.</p>}
    {state.error !== null && <div role="alert" className={common.alert}><p>{message(state.error)}</p>
      {state.attempt && <><p>Isian asli masih tersimpan pada tab ini. Pemeriksaan ulang memakai referensi permintaan yang sama.</p>
        <button type="button" className={common.secondary} disabled={busy} onClick={() => void editor.retry()}>Periksa ulang permintaan yang sama</button></>}</div>}
    {state.recoveryError !== null && <p role="alert" className={common.alert}>{message(state.recoveryError)}</p>}
    {state.references.some(entry => entry.operation.startsWith("stock.source.")) && <p className={common.help}>Untuk memulihkan permintaan sumber stok, pilih jenis pekerjaan serta gudang dan kanal yang digunakan semula.</p>}
    <OperationRecovery entries={state.references} journal={editor.journal} disabled={state.sending || !state.warehouse}
      recoveryAdapters={{ "stock.source": { lookup: editor.receipt } }} onResolved={editor.reconciled} onFailure={editor.recoveryFailed} onBusyChange={editor.setRecovering} />
    {state.result && <div ref={resultFocus} tabIndex={-1} className={common.success} role="status"><h2>Permintaan tersimpan</h2>
      <p>{statusLabel[state.result.status]}. Referensi pengajuan {state.result.proposalId}.</p>
      {state.result.physicalPoolId && <p>Referensi alokasi {state.result.physicalPoolId}. Pengaturan belum aktif.</p>}</div>}
    {!state.warehouse ? <section className={common.state}><h2>Pilih gudang untuk mulai</h2><p>Pilihan mengikuti penugasan akun Anda.</p></section> :
      state.mode === "propose" ? <div className={styles.desk}><section className={common.detail}>
        <div className={common.form}><h2>Pengaturan sumber baru</h2><SourceForm draft={state.draft} disabled={locked} onChange={(field, value) => editor.changeDraft(field, value)} onReview={() => editor.reviewProposal()} /></div>
      </section><aside className={styles.guide}><span className={styles.eyebrow}>Sebelum mengajukan</span><h2>Satu alokasi, tanggung jawab yang jelas</h2>
        <ol><li>Pastikan alokasi fisik yang sama belum diajukan dengan kode lain.</li><li>Gunakan batas usia data dan cadangan yang telah disepakati pengelola.</li>
          <li>Cantumkan penanggung jawab selisih serta acuan serah terima.</li></ol><p>Peninjau yang berbeda akan memeriksa pengajuan sebelum pengaturan disetujui.</p></aside></div> :
      !editor.canRead ? <section className={common.state}><h2>Akses riwayat diperlukan</h2><p>Izin peninjauan tidak otomatis memberi akses membaca pengajuan. Minta penugasan membaca pada gudang yang sesuai.</p></section> :
      <div className={styles.desk}><section className={common.queue} aria-busy={state.loadingHistory}>
        <div className={common.queueHeader}><h2>Daftar pengajuan</h2><p className={common.help}>{amount(state.history.total)} pengajuan pada gudang ini.</p>
          <button type="button" className={common.secondary} disabled={editor.selectionLocked || state.loadingHistory} onClick={() => void editor.loadHistory()}>Muat ulang pengajuan</button></div>
        {state.loadingHistory ? <p role="status" className={common.empty}>Memuat pengajuan…</p> : <><ul className={common.queueList}>
          {state.history.items.map(item => <li key={item.id}><button type="button" className={state.selected?.id === item.id ? common.selectedRow : common.queueRow}
            aria-pressed={state.selected?.id === item.id} disabled={locked} onClick={() => void editor.chooseProposal(item.id)}>
            <strong>{item.label}</strong><span>{item.physicalKey}</span><span>{statusLabel[item.status]}</span></button></li>)}</ul>
          {!state.history.items.length && <p className={common.empty}>Belum ada pengajuan pada halaman ini.</p>}
          <div className={common.pagination}><button type="button" className={common.secondary} disabled={editor.selectionLocked || state.historyOffset === 0} onClick={() => void editor.loadHistory(state.historyOffset - 25)}>Sebelumnya</button>
            <span>Halaman {Math.floor(state.historyOffset / 25) + 1}</span><button type="button" className={common.secondary} disabled={editor.selectionLocked || state.historyOffset >= 10000 || BigInt(state.historyOffset + 25) >= BigInt(state.history.total)} onClick={() => void editor.loadHistory(state.historyOffset + 25)}>Berikutnya</button></div></>}
      </section><section className={common.detail} aria-busy={state.loadingDetail}><div className={common.form}>
        {state.loadingDetail ? <p role="status">Memeriksa pengajuan terbaru…</p> : state.selected ? <><h2>{state.selected.label}</h2><p className={styles.badge}>{statusLabel[state.selected.status]}</p>
          <SourceFacts value={state.selected} /><p className={common.help}>Diajukan {formatDateTime(state.selected.proposedAt)}. Referensi {state.selected.id}.</p>
          {state.selected.decision && <section><h3>Keputusan tercatat</h3><p>{state.selected.decision.reviewReference}</p><p className={common.preserve}>{state.selected.decision.reason}</p></section>}
          {state.selected.status === "proposed" && (editor.canPropose || editor.canApprove) && <DecisionForm key={state.selected.id + ":" + state.mode} disabled={locked} canPropose={editor.canPropose} canApprove={editor.canApprove}
            onReview={(decision, reference, reason) => editor.reviewDecision(decision, reference, reason)} />}</> : <><h2>Pilih pengajuan</h2><p>Pengaturan terbaru akan dimuat sebelum Anda memeriksa atau mengambil keputusan.</p></>}
      </div></section></div>}
    {state.review && state.warehouse && <div ref={reviewFocus} tabIndex={-1} className={styles.review}>
      <SourceReview command={state.review} warehouse={state.warehouse.warehouseName + " / " + state.warehouse.channelName} busy={busy}
        onConfirm={() => void editor.submitReviewed(true)} onCancel={() => editor.cancelReview()} /></div>}
  </>;
}
function SourceForm({ draft, disabled, onChange, onReview }: { draft: SourceDraft; disabled: boolean; onChange: (field: keyof SourceDraft, value: string) => void; onReview: () => void }) {
  return <form onSubmit={event => { event.preventDefault(); onReview(); }}><fieldset className={common.fields} disabled={disabled}>
    <legend className={common.srOnly}>Isian pengaturan sumber stok</legend>
    <div className={common.twoFields}><label>Kode alokasi fisik<input required maxLength={64} pattern="[a-z][a-z0-9._-]*" value={draft.physicalKey} onChange={event => onChange("physicalKey", event.target.value)} aria-describedby="source-key-help" /></label>
      <label>Nama alokasi<input required maxLength={160} value={draft.label} onChange={event => onChange("label", event.target.value)} /></label></div>
    <p id="source-key-help" className={common.help}>Kode unik pada organisasi, diawali huruf kecil; gunakan huruf kecil, angka, titik, garis bawah, atau tanda hubung.</p>
    <label><span id="source-mode-label">Cara pencatatan</span><select aria-labelledby="source-mode-label" required value={draft.mode} onChange={event => onChange("mode", event.target.value)}><option value="">Pilih cara pencatatan</option>
      <option value="webLedger">Catatan stok web</option><option value="externalLedger">Catatan stok sistem lain</option></select></label>
    <label><span id="source-control-label">Pengendalian alokasi</span><select aria-labelledby="source-control-label" required value={draft.control} onChange={event => onChange("control", event.target.value)}><option value="">Pilih pengendalian</option>
      {draft.mode === "webLedger" && <><option value="exclusiveWebAllocation">Alokasi khusus web</option><option value="sharedWebLedger">Catatan bersama pada web</option></>}
      {draft.mode === "externalLedger" && <option value="externalHold">Pemesanan ditahan di sistem lain</option>}</select></label>
    <div className={common.twoFields}><label>Batas usia data (detik)<input required type="text" inputMode="numeric" pattern="[1-9][0-9]*" maxLength={12} value={draft.maximumAgeSeconds} onChange={event => onChange("maximumAgeSeconds", event.target.value)} /></label>
      <label>Cadangan pengaman (satuan dasar)<input required type="text" inputMode="numeric" pattern="0|[1-9][0-9]*" maxLength={19} value={draft.safetyBufferBaseUnits} onChange={event => onChange("safetyBufferBaseUnits", event.target.value)} /></label></div>
    <p className={common.help}>Gunakan keputusan operasional yang disetujui. Isian kosong tidak dianggap nol.</p>
    {draft.mode === "externalLedger" && <><label>Kode sumber sistem lain<input required maxLength={64} pattern="[a-z][a-z0-9._-]*" value={draft.externalNamespace} onChange={event => onChange("externalNamespace", event.target.value)} /></label>
      <label>Acuan protokol penahanan pesanan<input required maxLength={160} value={draft.reservationProtocolReference} onChange={event => onChange("reservationProtocolReference", event.target.value)} /></label></>}
    <label>Penanggung jawab selisih<input required maxLength={160} value={draft.discrepancyOwnerReference} onChange={event => onChange("discrepancyOwnerReference", event.target.value)} /></label>
    <label>Acuan serah terima<input required maxLength={160} value={draft.handoffReference} onChange={event => onChange("handoffReference", event.target.value)} /></label>
    <label>Alasan pengajuan<textarea required rows={3} maxLength={1000} value={draft.reason} onChange={event => onChange("reason", event.target.value)} /></label>
    <button className={common.primary}>Tinjau pengaturan</button>
  </fieldset></form>;
}
function SourceFacts({ value }: { value: SourceCommand["body"] | SourceDetail }) {
  if (!("physicalKey" in value)) return null;
  const pairs = [["Kode alokasi", value.physicalKey], ["Nama alokasi", value.label], ["Cara pencatatan", modeLabel[value.mode]],
    ["Pengendalian", controlLabel[value.control]], ["Batas usia data", sourceAgeSeconds(value.maximumAgeMilliseconds) + " detik"],
    ["Cadangan pengaman", amount(value.safetyBufferBaseUnits) + " satuan dasar"], ["Penanggung jawab selisih", value.discrepancyOwnerReference], ["Acuan serah terima", value.handoffReference]];
  if (value.externalNamespace) pairs.push(["Sumber sistem lain", value.externalNamespace], ["Protokol penahanan", value.reservationProtocolReference!]);
  return <><dl className={common.facts}>{pairs.map(([label, text]) => <div key={label}><dt>{label}</dt><dd>{text}</dd></div>)}</dl>
    <div><h3>Alasan pengajuan</h3><p className={common.preserve}>{value.reason}</p></div></>;
}
function DecisionForm({ disabled, canPropose, canApprove, onReview }: { disabled: boolean; canPropose: boolean; canApprove: boolean; onReview: (decision: SourceDecision, reference: string, reason: string) => void }) {
  const [decision, setDecision] = useState(""), [reference, setReference] = useState(""), [reason, setReason] = useState("");
  return <form onSubmit={event => { event.preventDefault(); if (decision) onReview(decision as SourceDecision, reference, reason); }}><fieldset className={common.fields} disabled={disabled}>
    <legend className={styles.legend}>Keputusan atas pengajuan</legend><label><span id="source-decision-label">Tindakan</span><select aria-labelledby="source-decision-label" required value={decision} onChange={event => setDecision(event.target.value)}><option value="">Pilih tindakan</option>
      {canApprove && <><option value="approve">Setujui pengaturan</option><option value="reject">Tolak pengajuan</option></>}{canPropose && <option value="withdraw">Tarik pengajuan saya</option>}</select></label>
    <p className={common.help}>Persetujuan dan penolakan memerlukan peninjau yang berbeda dari pengaju. Penarikan hanya dapat dilakukan oleh pengaju semula.</p>
    <label>Acuan peninjauan<input required maxLength={160} value={reference} onChange={event => setReference(event.target.value)} /></label>
    <label>Alasan keputusan<textarea required rows={3} maxLength={1000} value={reason} onChange={event => setReason(event.target.value)} /></label>
    <button className={common.primary}>Tinjau keputusan</button></fieldset></form>;
}
function SourceReview({ command, warehouse, busy, onConfirm, onCancel }: { command: SourceCommand; warehouse: string; busy: boolean; onConfirm: () => void; onCancel: () => void }) {
  const [confirmed, setConfirmed] = useState(false);
  const labels = { "stock.source.propose": "Kirim pengajuan", "stock.source.approve": "Setujui pengaturan", "stock.source.reject": "Tolak pengajuan", "stock.source.withdraw": "Tarik pengajuan" };
  return <form onSubmit={event => { event.preventDefault(); if (confirmed) onConfirm(); }}>
    <span className={styles.eyebrow}>Periksa sebelum menyimpan</span><h2>{labels[command.operation]}</h2><p className={common.scopeSummary}>{warehouse}</p>
    <SourceFacts value={"proposal" in command ? command.proposal : command.body} />
    {"proposal" in command && <><p>Referensi pengajuan {command.proposalId}, versi {command.expectedVersion}.</p><h3>Acuan peninjauan</h3><p>{command.body.reviewReference}</p><h3>Alasan keputusan</h3><p className={common.preserve}>{command.body.reason}</p></>}
    <label className={common.checkbox}><input type="checkbox" required checked={confirmed} disabled={busy} onChange={event => setConfirmed(event.target.checked)} />
      <span>Saya telah memeriksa gudang, kanal, seluruh pengaturan, dan alasan. Persetujuan menyimpan pengaturan yang belum aktif.</span></label>
    <div className={common.actions}><button className={common.primary} disabled={busy || !confirmed}>{labels[command.operation]}</button>
      <button type="button" className={common.secondary} disabled={busy} onClick={onCancel}>Kembali ke isian</button></div>
  </form>;
}
