"use client";

import Link from "next/link";
import {ScanInput} from './ScanInput';
import {sameScanPackage} from './scan-client';
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { createAccessGateway } from "../administration/access-client";
import { AdministrationNavigation } from "../administration/AdministrationNavigation";
import { OperationRecovery } from "../administration/OperationRecovery";
import { OperationJournal } from "../administration/operation-journal";
import { AccessFailure, accessMessage } from "../administration/workflow";
import { createIdentityGateway } from "../identity/identity-client";
import { IdentityFailure, identityMessage, sessionTiming, type IdentitySession } from "../identity/identity-workflow";
import { createStockCountGateway } from "./stock-count-client";
import { StockCountEditor } from "./stock-count-editor-state";
import { stockCountActor, stockCountQuantity, stockLocationKey, type StockCountCommand, type StockObservation, type StockPackageChoice } from "./stock-count-workflow";
import common from "../administration/access.module.css";
import styles from "./stock-count.module.css";
import { formatDateTime, numberFormatter } from "@/lib/format";

const path = "/inventory/counts";
const loginHref = "/identity/login?returnPath=" + encodeURIComponent(path);
type Loaded = { actorId: string; actions: string[]; session: IdentitySession; observedAt: number; generation: number };
const countFormat = numberFormatter;
const amount = (value: string | number) => countFormat.format(typeof value === "string" ? BigInt(value) : value);
const kind: Readonly<Record<string, string>> = { loose: "Satuan / lepas", sealed: "Kemasan tertutup", other: "Kemasan lain" };
function message(error: unknown): string {
  if (error instanceof IdentityFailure) return identityMessage(error);
  if (error instanceof AccessFailure) {
    if (error.code === "stock_count_selection_required") return "Pilih gudang dan kemasan dari daftar yang tersedia.";
    if (error.code === 'stock_count_source_selection_required') return 'Pilih sumber stok yang benar-benar diamati, atau pilih pengamatan tanpa kaitan sumber secara sengaja.';
    if (error.code === 'stock_count_view_stale') return 'Sesi atau pilihan berubah. Perbarui pilihan sumber dan kemasan sebelum meninjau kembali.';
    if (['stock_count_source_changed', 'stock_source_revision_changed', 'stock_count_pool_changed'].includes(error.code)) return 'Konfigurasi sumber berubah. Muat ulang pilihan dan cocokkan kembali sumber sebelum mencatat pengamatan.';
    if (error.code === "stock_count_input_invalid") return "Isi jumlah kemasan dengan bilangan bulat 0–1.000.000 dan berikan alasan. Kolom kosong bukan jumlah nol.";
    if (error.code === "stock_count_response_invalid") return "Data yang diterima belum sesuai. Referensi permintaan tetap disimpan. Periksa sesi dan data terbaru sebelum melanjutkan.";
    if (error.code === "version_conflict" || error.code === "catalog_revision_conflict") return "Data berubah sejak dipilih. Muat ulang pilihan kemasan, lalu periksa jumlah dan isinya kembali.";
  }
  return accessMessage(error);
}
function sessionLost(error: unknown) {
  return (error instanceof AccessFailure || error instanceof IdentityFailure) && (error.status === 401 || error.code === "actor_changed");
}
export function StockCountWorkspace() {
  const [access] = useState(() => createAccessGateway());
  const [identity] = useState(() => createIdentityGateway());
  const request = useRef<AbortController | null>(null);
  const generation = useRef(0);
  const [loaded, setLoaded] = useState<Loaded | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(true);
  const refresh = useCallback(async () => {
    request.current?.abort();
    const controller = new AbortController(); request.current = controller;
    const current = () => request.current === controller && !controller.signal.aborted;
    setLoaded(null); setLoading(true); setError(null);
    try {
      const session = await identity.session(controller.signal), observedAt = performance.now();
      if (!current()) return;
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, "unauthenticated");
      const account = await access.current();
      if (!current()) return;
      if (account.userId !== session.account.userId) throw new AccessFailure(409, "actor_changed");
      const actorId = stockCountActor(account.userId);
      if (!Array.isArray(account.allowedActions) || account.allowedActions.length > 200 ||
        account.allowedActions.some(action => typeof action !== "string" || !/^[a-z]+(?:[.-][a-z]+){1,5}$/.test(action)))
        throw new AccessFailure(502, "stock_count_response_invalid");
      if (!account.allowedActions.some(action => action === "stock.count" || action === "stock.read")) throw new AccessFailure(403, "access_denied");
      if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, "session_expired");
      setLoaded({ actorId, actions: [...account.allowedActions], session, observedAt, generation: ++generation.current });
    } catch (failure) { if (current()) setError(failure); }
    finally { if (current()) setLoading(false); }
  }, [access, identity]);
  useEffect(() => {
    let active = true;
    void Promise.resolve().then(() => { if (active) void refresh(); });
    const restored = (event: PageTransitionEvent) => { if (event.persisted) void refresh(); };
    window.addEventListener("pageshow", restored);
    return () => { active = false; request.current?.abort(); window.removeEventListener("pageshow", restored); };
  }, [refresh]);
  return <main className={common.page}>
    <header className={common.header}><div>
      <Link className={common.brand} href="/identity/account">Unit Toko</Link>
      <h1>Hitung barang</h1>
      <p>Catat jumlah fisik pada gudang dan kemasan yang dipilih.</p>
    </div></header>
    {loaded ? <CountDesk key={loaded.actorId + ":" + loaded.generation} loaded={loaded} onRefresh={refresh} /> :
      <section className={common.state} aria-busy={loading}>
        <h2>{loading ? "Memeriksa sesi dan akses" : sessionLost(error) ? "Masuk untuk menghitung barang" : "Penghitungan belum tersedia"}</h2>
        {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
        <div className={common.actions}>
          {sessionLost(error) && <Link className={common.primary} href={loginHref}>Masuk kembali</Link>}
          <button type="button" className={common.secondary} disabled={loading} onClick={() => void refresh()}>Periksa kembali</button>
          <Link href="/identity/account">Kembali ke akun saya</Link>
        </div>
      </section>}
  </main>;
}
function CountDesk({ loaded, onRefresh }: { loaded: Loaded; onRefresh: () => Promise<void> }) {
  const [scanBlocked, setScanBlocked] = useState(false), [scanSelection, setScanSelection] = useState<import('./scan-client').OperationalScanSelection | null>(null);
  const [editor] = useState(() => new StockCountEditor({ actorId: loaded.actorId, actions: loaded.actions, session: loaded.session,
    observedAt: loaded.observedAt, now: () => performance.now(), gateway: createStockCountGateway(),
    journal: new OperationJournal(() => window.localStorage) }));
  const state = useSyncExternalStore(editor.subscribe, editor.snapshot, editor.snapshot);
  const reviewFocus = useRef<HTMLDivElement>(null), resultFocus = useRef<HTMLDivElement>(null), quantityFocus = useRef<HTMLInputElement>(null);
  useEffect(() => {
    void editor.start();
    const timer = window.setInterval(() => editor.checkSession(), 500);
    const storage = (event: StorageEvent) => { if (event.key === null || event.key.startsWith("unit-toko:pending:v1:")) editor.refreshReferences(); };
    const leaving = () => editor.dispose();
    window.addEventListener("storage", storage); window.addEventListener("pagehide", leaving);
    return () => { editor.dispose(); window.clearInterval(timer); window.removeEventListener("storage", storage); window.removeEventListener("pagehide", leaving); };
  }, [editor]);
  const warn = state.sending || state.attempt !== null || state.review !== null || state.references.length > 0 || state.recoveryError !== null;
  useEffect(() => {
    if (!warn) return;
    const leaving = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ""; };
    window.addEventListener("beforeunload", leaving); return () => window.removeEventListener("beforeunload", leaving);
  }, [warn]);
  useEffect(() => { if (state.review) reviewFocus.current?.focus(); }, [state.review]);
  useEffect(() => { if (state.result) resultFocus.current?.focus(); }, [state.result]);
  const locked = editor.locked;
  if (state.expired || state.suspended) return <section className={common.state}>
    <h2>Sesi dan akses perlu diperiksa kembali</h2>
    <p>Data sebelumnya disembunyikan. Referensi permintaan yang belum terkonfirmasi tetap disimpan untuk akun pemiliknya.</p>
    {state.error !== null && <p role="alert" className={common.alert}>{message(state.error)}</p>}
    <button type="button" className={common.primary} disabled={state.sending || state.recovering} onClick={() => void onRefresh()}>Periksa sesi terbaru</button>
    <Link href={loginHref}>Masuk kembali</Link>
  </section>;
  let conversion: string | null = null;
  if (state.selectedPackage) {
    try { conversion = stockCountQuantity(state.draft.packages, state.selectedPackage.baseUnitsPerPackage).baseUnits; } catch { /* Empty or unfinished entry has no implied quantity. */ }
  }
  return <>
    <AdministrationNavigation current="counts" actions={loaded.actions} locked={locked} />
    <section className={common.accountSection}>
      <div className={styles.headingRow}><p>Petugas: <strong>{loaded.session.account?.displayName ?? loaded.session.account?.verifiedEmail}</strong></p>
        <button type="button" className={common.secondary} disabled={locked} onClick={() => void onRefresh()}>Periksa sesi dan akses</button></div>
      <div className={styles.modeChoices} role="group" aria-label="Jenis pekerjaan">
        {editor.canCount && <button type="button" aria-pressed={state.mode === "count"} className={state.mode === "count" ? common.primary : common.secondary}
          disabled={locked} onClick={() => void editor.changeMode("count")}>Catat hitungan</button>}
        {editor.canRead && <button type="button" aria-pressed={state.mode === "history"} className={state.mode === "history" ? common.primary : common.secondary}
          disabled={locked} onClick={() => void editor.changeMode("history")}>Riwayat pengamatan</button>}
      </div>
      <label><span id="stock-warehouse-label">Gudang dan kanal</span>
        <select aria-labelledby="stock-warehouse-label" value={state.warehouse ? stockLocationKey(state.warehouse.location) : ""} disabled={locked || state.loadingWarehouses}
          onChange={event => {setScanSelection(null); setScanBlocked(false); void editor.chooseWarehouse(state.warehouses.find(item => stockLocationKey(item.location) === event.target.value)?.location ?? null);}}>
          <option value="">{state.loadingWarehouses ? "Memuat gudang…" : "Pilih gudang dan kanal"}</option>
          {state.warehouses.map(item => <option key={stockLocationKey(item.location)} value={stockLocationKey(item.location)}>
            {item.organizationName} / {item.shopName} / {item.warehouseName} / {item.channelName}
          </option>)}
        </select>
      </label>
      {state.warehouse && <p className={common.scopeSummary}>{state.warehouse.organizationName} / {state.warehouse.shopName}<br />
        <strong>{state.warehouse.warehouseName}</strong> / {state.warehouse.channelName}</p>}
      {!state.loadingWarehouses && state.warehouses.length === 0 && <p className={common.help}>Belum ada gudang aktif untuk tugas ini. Periksa penugasan bersama pengelola.</p>}
      <p className={common.help}>Setiap catatan adalah satu pengamatan fisik. Catatan ini belum menetapkan saldo gudang atau jumlah barang yang tersedia.</p>
      {state.mode === 'count' && state.warehouse && <section aria-label="Pilihan kaitan sumber" aria-busy={state.loadingSources}>
        <label><span id="stock-count-source-label">Sumber stok yang diamati</span>
          <select aria-labelledby="stock-count-source-label" value={state.sourceMode === 'unbound' ? 'unbound' : state.selectedSource?.pool.poolId ?? ''}
            disabled={locked || state.loadingSources} onChange={event => {setScanSelection(null); setScanBlocked(false); editor.chooseSource(event.target.value);}}>
            <option value="">{state.loadingSources ? 'Memuat pilihan sumber…' : 'Pilih sumber atau pengamatan tanpa kaitan'}</option>
            <option value="unbound">Catat pengamatan tanpa kaitan sumber</option>
            {state.sources.items.map(source => <option key={source.pool.poolId} value={source.pool.poolId}>{source.pool.label} — {source.pool.physicalKey}</option>)}
          </select>
        </label>
        <div className={common.actions}><button type="button" className={common.secondary} disabled={locked || state.loadingSources}
          onClick={() => void editor.loadSources()}>Muat ulang pilihan sumber</button></div>
        {state.loadingSources && <p role="status">Memeriksa sumber yang diizinkan pada gudang dan kanal ini…</p>}
        {!state.loadingSources && state.sources.items.length === 0 && <p className={common.help}>Belum ada sumber yang dapat dipilih pada halaman ini. Periksa penugasan atau konfigurasi bersama pengelola. Pengamatan tanpa kaitan harus dipilih secara sengaja.</p>}
        {state.selectedSource && <p className={common.scopeSummary}>Sumber dipilih oleh petugas: <strong>{state.selectedSource.pool.label}</strong> — {state.selectedSource.pool.physicalKey}.
          {' '}Revisi sumber saat ditinjau: {state.selectedSource.pool.poolRevision}. Pilihan mengikuti konfigurasi saat dimuat; penanda aktif bukan bukti ketersediaan atau persetujuan pelepasan stok.</p>}
        {state.sourceMode === 'unbound' && <p className={common.help}>Tanpa kaitan sumber. Gudang dan label lot tidak akan digunakan untuk menebak pool stok.</p>}
        <Paging offset={state.sourceOffset} total={state.sources.total} disabled={locked || state.loadingSources} onPage={offset => void editor.loadSources(offset)} />
      </section>}
    </section>
    {state.sending && <p role="status" className={common.help}>Menyimpan pengamatan… Tunggu hasil sebelum melanjutkan.</p>}
    {state.error !== null && <div role="alert" className={common.alert}><p>{message(state.error)}</p>
      {state.attempt && <><p>Isian asli masih tersimpan pada tab ini. Periksa hasil atau kirim ulang permintaan yang sama.</p>
        <button type="button" className={common.secondary} disabled={state.sending || state.recovering} onClick={() => void editor.retry()}>Periksa ulang permintaan yang sama</button></>}
      {state.error instanceof AccessFailure && state.error.status === 409 && !state.attempt && state.mode === "count" &&
        <button type="button" className={common.secondary} disabled={locked || state.loadingPackages} onClick={() => void editor.loadPackages()}>Muat ulang pilihan kemasan</button>}
    </div>}
    {state.recoveryError !== null && <p role="alert" className={common.alert}>{message(state.recoveryError)}</p>}
    <OperationRecovery entries={state.references} journal={editor.journal} disabled={state.sending}
      onResolved={editor.reconciled} onFailure={editor.recoveryFailed} onBusyChange={editor.setRecovering}
      recoveryAdapters={{ "inventory.stock-count": { lookup: editor.receipt } }} />
    {state.result && <div ref={resultFocus} tabIndex={-1} className={common.success} role="status">
      <h2>Hitungan tersimpan</h2><p>{amount(state.result.packages)} {state.result.packageLabel} = {amount(state.result.baseUnits)} {state.result.baseUnitCode}.</p>
      <p>Referensi pengamatan {state.result.id}. Pilih kemasan kembali untuk membuat pengamatan baru.</p>
      <CountSourceBinding item={state.result} />
    </div>}
    {!state.warehouse ? <section className={common.state}><h2>Pilih gudang untuk mulai</h2><p>Pilihan mengikuti penugasan akun Anda. Barang akan dimuat setelah gudang dipilih.</p></section> :
      state.mode === "history" ? <section className={common.accountSection} aria-busy={state.loadingHistory}>
        <div className={styles.headingRow}><h2>Riwayat pengamatan</h2><button type="button" className={common.secondary} disabled={state.loadingHistory || state.recovering} onClick={() => void editor.loadHistory()}>Muat ulang riwayat</button></div>
        <p className={common.help}>{amount(state.history.total)} pengamatan. Setiap baris menyimpan kemasan yang digunakan saat penghitungan.</p>
        {state.loadingHistory ? <p role="status">Memuat riwayat…</p> : <><ul className={styles.history}>{state.history.items.map(item => <Observation key={item.id} item={item} />)}</ul>
          {!state.history.items.length && <p>Belum ada pengamatan pada halaman ini.</p>}
          <Paging offset={state.historyOffset} total={state.history.total} disabled={state.recovering} onPage={offset => void editor.loadHistory(offset)} />
        </>}
      </section> :
      <div className={styles.desk}>
        <section className={common.queue} aria-busy={state.loadingPackages}>
          {state.warehouse && <ScanInput key={stockLocationKey(state.warehouse.location) + ':' + (state.selectedSource?.pool.poolId ?? state.sourceMode)}
            actorId={loaded.actorId} disabled={locked || state.loadingPackages} current={() => !editor.snapshot().expired && !editor.snapshot().suspended && !sessionTiming(loaded.session, performance.now() - loaded.observedAt).expired}
            context={{purpose: 'stocktake', location: state.warehouse.location, documentId: null, documentRevision: null, auxiliaryId: null, auxiliaryRevision: null, sourceReview: null}}
            onReset={() => {setScanSelection(null); setScanBlocked(true); editor.changeSearch('');}} onManual={() => {setScanSelection(null); setScanBlocked(false); editor.changeSearch(''); void editor.loadPackages();}}
            onConfirm={async candidate => {editor.changeSearch(candidate.skuCode); await editor.loadPackages();
              const choice = editor.snapshot().packages.items.find(item => item.skuId === candidate.skuId);
              if (!choice || candidate.targetReference !== choice.skuId || !sameScanPackage(candidate, choice)) throw new AccessFailure(409, 'operational_scan_context_changed');
              editor.choosePackage(choice.skuId); setScanSelection(candidate.selection); setScanBlocked(false);
            }} />}
          <div className={common.queueHeader}><h2>Pilih kemasan barang</h2>
            <form className={common.lookup} onSubmit={event => { event.preventDefault(); void editor.loadPackages(); }}>
              <label>Cari kode atau kemasan<input value={state.search} maxLength={128} disabled={locked}
                onChange={event => editor.changeSearch(event.target.value)} /></label>
              <button className={common.secondary} disabled={locked || state.loadingPackages}>Cari</button>
            </form>
            <p className={common.help}>{amount(state.packages.total)} kemasan pada hasil ini.</p>
          </div>
          {state.loadingPackages ? <p role="status" className={common.empty}>Memuat pilihan kemasan…</p> : <>
            <ul className={common.queueList}>{state.packages.items.map(item => <li key={item.skuId}>
              <button type="button" className={state.selectedPackage?.skuId === item.skuId ? common.selectedRow : common.queueRow}
                aria-pressed={state.selectedPackage?.skuId === item.skuId} disabled={locked} onClick={() => {setScanSelection(null); editor.choosePackage(item.skuId);}}>
                <strong>{item.familyName}</strong><span>{item.skuCode}</span>
                <span>{item.packageLabel}: {amount(item.baseUnitsPerPackage)} {item.baseUnitCode} per kemasan</span>
              </button>
            </li>)}</ul>
            {!state.packages.items.length && <p className={common.empty}>Belum ada kemasan pada hasil ini. Coba pencarian lain atau periksa katalog bersama pengelola.</p>}
            <Paging offset={state.packageOffset} total={state.packages.total} disabled={locked} onPage={offset => void editor.loadPackages(offset)} />
          </>}
        </section>
        <section className={common.detail}>
          {state.selectedPackage ? <div className={common.form}>
            <PackageDetails choice={state.selectedPackage} />
            <form onSubmit={event => { event.preventDefault(); if (!scanBlocked) editor.reviewCount(scanSelection ? [scanSelection] : undefined); }}>
              <fieldset className={common.fields} disabled={locked}>
                <legend className={styles.formLegend}>Hasil hitungan fisik</legend>
                <label>Jumlah kemasan yang ditemukan
                  <input ref={quantityFocus} type="text" inputMode="numeric" pattern="0|[1-9][0-9]{0,6}" required maxLength={7}
                    aria-describedby="count-quantity-help count-conversion" value={state.draft.packages} onChange={event => editor.changeDraft("packages", event.target.value)} />
                </label>
                <p id="count-quantity-help" className={common.help}>Isi 0 jika tidak ada kemasan yang ditemukan. Batas 1.000.000 kemasan.</p>
                <p id="count-conversion" aria-live="polite" className={styles.conversion}>{conversion === null ? "Isi jumlah untuk melihat konversi." :
                  amount(state.draft.packages) + " " + state.selectedPackage.packageLabel + " = " + amount(conversion) + " " + state.selectedPackage.baseUnitCode}</p>
                <label>Label lot yang terlihat <span className={common.optional}>(opsional, belum diverifikasi)</span>
                  <input maxLength={160} value={state.draft.observedLotLabel} onChange={event => editor.changeDraft("observedLotLabel", event.target.value)} />
                </label>
                <label>Alasan penghitungan
                  <input required maxLength={1000} value={state.draft.reason} onChange={event => editor.changeDraft("reason", event.target.value)} />
                </label>
                <button className={common.primary} disabled={scanBlocked}>Tinjau hitungan</button>
              </fieldset>
            </form>
          </div> : <div className={common.form}><h2>Pilih kemasan dari daftar</h2><p>Periksa isi setiap kemasan sebelum memasukkan jumlah fisiknya.</p></div>}
          {state.review && state.warehouse && <div ref={reviewFocus} tabIndex={-1} className={styles.review}>
            <CountReview command={state.review} warehouse={state.warehouse.warehouseName + " / " + state.warehouse.channelName}
              busy={state.sending || state.recovering} onConfirm={() => void editor.submitReviewed(true)}
              onCancel={() => { editor.cancelReview(); requestAnimationFrame(() => quantityFocus.current?.focus()); }} />
          </div>}
        </section>
      </div>}
  </>;
}
function PackageDetails({ choice }: { choice: StockPackageChoice }) {
  return <section><h2>{choice.familyName}</h2><p className={styles.code}>{choice.skuCode}</p>
    <p className={styles.contents}><strong>{amount(choice.baseUnitsPerPackage)} {choice.baseUnitCode}</strong><span>per {choice.packageLabel}</span></p>
    <p className={common.help}>{kind[choice.packageKind]}</p>
  </section>;
}
function CountReview({ command, warehouse, busy, onConfirm, onCancel }: {
  command: StockCountCommand; warehouse: string; busy: boolean; onConfirm: () => void; onCancel: () => void;
}) {
  const [confirmed, setConfirmed] = useState(false);
  const units = stockCountQuantity(String(command.body.packages), command.choice.baseUnitsPerPackage).baseUnits;
  return <form onSubmit={event => { event.preventDefault(); if (confirmed && !busy) onConfirm(); }} className={common.fields}>
    <h2>Periksa sebelum menyimpan</h2>
    <dl className={common.facts}>
      <div><dt>Gudang / kanal</dt><dd>{warehouse}</dd></div><div><dt>Barang</dt><dd>{command.choice.familyName} ({command.choice.skuCode})</dd></div>
      <div><dt>Sumber dipilih oleh petugas</dt><dd>{command.source ? command.source.pool.label + ' — ' + command.source.pool.physicalKey : 'Tanpa kaitan sumber'}</dd></div>
      {command.source && <div><dt>Revisi sumber saat ditinjau</dt><dd>{command.source.pool.poolRevision}</dd></div>}
      <div><dt>Kemasan</dt><dd>{command.choice.packageLabel}, {amount(command.choice.baseUnitsPerPackage)} {command.choice.baseUnitCode} per kemasan</dd></div>
      <div><dt>Jumlah ditemukan</dt><dd>{amount(command.body.packages)} kemasan = {amount(units)} {command.choice.baseUnitCode}</dd></div>
      <div><dt>Label lot belum diverifikasi</dt><dd>{command.body.observedLotLabel ?? "Tidak dicatat"}</dd></div>
      <div><dt>Alasan</dt><dd>{command.body.reason}</dd></div>
    </dl>
    <p className={common.help}>Pengamatan tersimpan tidak dapat diubah. Catatan ini belum menetapkan saldo atau ketersediaan barang.</p>
    <label className={common.checkbox}><input type="checkbox" checked={confirmed} disabled={busy} onChange={event => setConfirmed(event.target.checked)} />
      Saya sudah memeriksa gudang, pilihan kaitan sumber, kemasan dan jumlah yang ditemukan.</label>
    <div className={common.actions}><button className={common.primary} disabled={busy || !confirmed}>Simpan pengamatan</button>
      <button type="button" className={common.secondary} disabled={busy} onClick={onCancel}>Kembali ke isian</button></div>
  </form>;
}
function Observation({ item }: { item: StockObservation }) {
  return <li><div className={styles.headingRow}><h3>{item.skuCode}</h3>
    <time dateTime={item.recordedAt}>{formatDateTime(item.recordedAt)}</time></div>
    <p><strong>{amount(item.packages)} {item.packageLabel} = {amount(item.baseUnits)} {item.baseUnitCode}</strong></p>
    <p className={common.help}>{amount(item.baseUnitsPerPackage)} {item.baseUnitCode} per kemasan. {kind[item.packageKind]}.</p>
    <p>Alasan: {item.reason}</p>{item.observedLotLabel && <p>Label lot belum diverifikasi: {item.observedLotLabel}</p>}
    <CountSourceBinding item={item} />
    <details><summary>Referensi pengamatan</summary><p>{item.id}</p></details>
  </li>;
}
function CountSourceBinding({ item }: { item: StockObservation }) {
  return item.poolBinding ? <p>Kaitan sumber saat dicatat: <strong>{item.poolBinding.label}</strong> — {item.poolBinding.physicalKey}; revisi {item.poolBinding.poolRevision}.
    {' '}Penanda konfigurasi saat dicatat: {item.poolBinding.active ? 'aktif' : 'tidak aktif'}. Kaitan tersimpan tidak menetapkan saldo, nilai, ketersediaan, pelepasan atau penyelesaian stok.</p>
    : <p>Pengamatan lama/tanpa kaitan sumber. Lokasi dan label lot belum membuktikan kaitan ke pool atau lot stok.</p>;
}
function Paging({ offset, total, disabled, onPage }: { offset: number; total: string; disabled: boolean; onPage: (offset: number) => void }) {
  return <div className={common.pagination}>
    <button type="button" className={common.secondary} disabled={disabled || offset === 0} onClick={() => onPage(offset - 25)}>Sebelumnya</button>
    <span>Halaman {Math.floor(offset / 25) + 1}</span>
    <button type="button" className={common.secondary} disabled={disabled || offset >= 10000 || BigInt(offset + 25) >= BigInt(total)} onClick={() => onPage(offset + 25)}>Berikutnya</button>
  </div>;
}
