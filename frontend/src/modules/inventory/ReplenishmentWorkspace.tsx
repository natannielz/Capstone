'use client';

import Link from 'next/link';
import { SessionAccessState } from '@/components/workspace-kit/session-access-state';
import {useCallback, useEffect, useRef, useState, type FormEvent} from 'react';
import {RequestNotSentError} from '../../api/client';
import {createAccessGateway} from '../administration/access-client';
import {AccessFailure, accessMessage} from '../administration/workflow';
import {createIdentityGateway} from '../identity/identity-client';
import {IdentityFailure, sessionTiming, type IdentitySession} from '../identity/identity-workflow';
import {stockLocationKey, type StockWarehouse, type StockPackageChoice} from './stock-count-workflow';
import {createReplenishmentGateway, isReplenishmentRecoveryKey, parseReplenishmentInput, replenishmentValues, ReplenishmentRecovery,
  type ReplenishmentEvaluation, type ReplenishmentPending, type ReplenishmentSource, type ReplenishmentValues} from './replenishment-client';
import common from '../administration/access.module.css';
import styles from './replenishment.module.css';
import { dateTimeFormatter, numberFormatter } from '@/lib/format';
import { Button, buttonClassName } from '@/components/workspace-kit/button';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { Section } from '@/components/workspace-kit/section';
import { StateView } from '@/components/workspace-kit/state-view';
import { Facts } from '@/components/workspace-kit/facts';
import { Pagination } from '@/components/workspace-kit/pagination';

const path = '/inventory/replenishment';
type Account = {actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number};
const when = (value: string) => dateTimeFormatter.format(new Date(value));
const amount = (value: string | null) => value === null ? 'Belum diketahui' : numberFormatter.format(BigInt(value));
const warnings: Record<string, string> = {
  daily_demand_missing: 'Pemakaian per hari belum diisi.', lead_time_missing: 'Waktu tunggu belum diisi.', safety_stock_missing: 'Stok pengaman belum diisi.',
  reorder_point_missing: 'Batas peringatan belum diisi.', stock_authority_unavailable: 'Sumber stok belum dapat dipastikan berlaku.',
  usable_stock_unknown: 'Jumlah stok layak belum dapat dipastikan.', commitment_coverage_unknown: 'Cakupan pesanan yang mengikat stok belum lengkap.',
  supply_coverage_unknown: 'Cakupan pasokan masuk belum lengkap.', supply_unverified: 'Jadwal salah satu pasokan belum disetujui.',
  supply_expected_arrival_missing: 'Tanggal kedatangan salah satu pasokan belum diketahui.', supply_overdue: 'Ada pasokan yang melewati tanggal kedatangan.',
  supply_after_horizon: 'Ada pasokan yang baru dijadwalkan setelah masa tunggu.', supply_lead_time_missing: 'Pasokan belum dapat dibandingkan dengan masa tunggu.',
  stock_source_inactive: 'Sumber stok tidak aktif.', stock_baseline_missing: 'Belum ada saldo dengan asal barang yang dapat dipastikan.',
  stock_old_definition: 'Masih ada stok dalam definisi kemasan lama; periksa sebelum menghitung kebutuhan.', stock_expired_lot: 'Ada lot kedaluwarsa yang perlu ditangani oleh petugas stok.',
};
const supplyStates: Record<string, string> = {eligible: 'Dihitung dalam masa tunggu', after_horizon: 'Setelah masa tunggu', overdue: 'Terlambat',
  unverified: 'Belum disetujui', expected_arrival_missing: 'Tanggal belum diketahui', lead_time_missing: 'Waktu tunggu belum diisi'};
function message(error: unknown) {
  if (error instanceof IdentityFailure) return 'Masuk dengan akun yang ditugaskan untuk memeriksa stok.';
  if (error instanceof AccessFailure || error instanceof RequestNotSentError) {
    if (error.code === 'invalid_replenishment_review') return 'Pilih gudang, sumber dan kemasan. Isi aturan pembelian serta referensi; angka yang belum diketahui boleh dikosongkan.';
    if (['replenishment_source_changed', 'catalog_revision_conflict', 'stock_source_revision_changed', 'replenishment_view_stale'].includes(error.code)) return 'Data atau sesi berubah. Muat pilihan terbaru dan hitung ulang.';
    if (error.code === 'replenishment_response_invalid' || error.code === 'replenishment_evidence_unavailable') return 'Hasil belum dapat dipastikan. Periksa permintaan tersimpan sebelum memulai pemeriksaan lain.';
    return accessMessage(error);
  }
  return 'Hasil belum terkonfirmasi. Periksa permintaan tersimpan setelah koneksi kembali.';
}
export function ReplenishmentWorkspace() {
  const [identity] = useState(createIdentityGateway), [access] = useState(createAccessGateway);
  const [account, setAccount] = useState<Account | null>(null), [error, setError] = useState<unknown>(null), [loading, setLoading] = useState(true), [generation, setGeneration] = useState(0);
  useEffect(() => {let active = true; const controller = new AbortController(); void Promise.resolve().then(async () => {
    setLoading(true); setAccount(null); setError(null);
    try {const session = await identity.session(controller.signal), observedAt = performance.now();
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
      const current = await access.current(); if (!active) return;
      if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
      if (!current.allowedActions.includes('stock.read')) throw new AccessFailure(403, 'scope_denied');
      if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
      setAccount({actorId: current.userId, actions: current.allowedActions, session, observedAt});
    } catch (failure) {if (active) setError(failure);} finally {if (active) setLoading(false);}
  }); const restored = (event: PageTransitionEvent) => {if (event.persisted) setGeneration(value => value + 1);};
    window.addEventListener('pageshow', restored); return () => {active = false; controller.abort(); window.removeEventListener('pageshow', restored);};
  }, [identity, access, generation]);
  return <main className={common.page}><PageHeader title="Kebutuhan pembelian" description="Tinjau stok, pesanan dan pasokan masuk untuk memperkirakan kebutuhan selama waktu tunggu." />
    <div className="ui-stack">{loading ? <StateView kind="loading" title="Memeriksa sesi dan akses…" /> : account ? <Desk key={account.actorId + ':' + account.observedAt} account={account} refresh={() => setGeneration(value => value + 1)} /> :
      <SessionAccessState error={error} returnPath={path} onRetry={() => setGeneration(value => value + 1)} detail={error !== null ? message(error) : undefined} />}</div>
  </main>;
}

const emptyValues: Record<keyof ReplenishmentValues, string> = {dailyDemandPerDay: '', leadTimeDays: '', safetyStockPackages: '', reorderPointPackages: '', purchaseMinimumPackages: '', purchaseIncrementPackages: ''};
function Desk({account, refresh}: {account: Account; refresh: () => void}) {
  const [gateway] = useState(createReplenishmentGateway), [recovery] = useState(() => new ReplenishmentRecovery(() => window.localStorage));
  const [warehouses, setWarehouses] = useState<StockWarehouse[]>([]), [place, setPlace] = useState<StockWarehouse | null>(null);
  const [sources, setSources] = useState<ReplenishmentSource[]>([]), [source, setSource] = useState<ReplenishmentSource | null>(null);
  const [packages, setPackages] = useState<StockPackageChoice[]>([]), [choice, setChoice] = useState<StockPackageChoice | null>(null);
  const [history, setHistory] = useState<ReplenishmentEvaluation[] | null>(null), [historyBefore, setHistoryBefore] = useState<string | null>(null);
  const [search, setSearch] = useState(''), [offset, setOffset] = useState(0), [hasMore, setHasMore] = useState(false);
  const [inputs, setInputs] = useState({...emptyValues}), [inputReference, setInputReference] = useState(''), [reason, setReason] = useState('');
  const [value, setValue] = useState<ReplenishmentEvaluation | null>(null), [pending, setPending] = useState<ReplenishmentPending | null>(null);
  const [storageBlocked, setStorageBlocked] = useState(false);
  const [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState(''), [expired, setExpired] = useState(false), [offline, setOffline] = useState(false);
  const alive = useRef(false), serial = useRef(0), active = useRef<AbortController | null>(null), sending = useRef(false), heading = useRef<HTMLHeadingElement>(null);
  const current = useCallback(() => alive.current && navigator.onLine && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const context = useCallback((number: number, controller: AbortController) => ({actorId: account.actorId, signal: controller.signal,
    isCurrent: () => serial.current === number && !controller.signal.aborted && current()}), [account.actorId, current]);
  const syncPending = useCallback(() => {
    try {const entry = recovery.read(account.actorId); setPending(entry); setStorageBlocked(false); return entry;}
    catch (failure) {setStorageBlocked(true); throw failure;}
  }, [recovery, account.actorId]);
  function clearPending(key: string) {
    try {recovery.clear(account.actorId, key); syncPending();}
    catch (failure) {setStorageBlocked(true); throw failure;}
  }
  useEffect(() => {alive.current = true; const controller = new AbortController(); active.current = controller; const number = ++serial.current;
    void Promise.resolve().then(async () => {setBusy(true); try {
      try {syncPending();} catch (failure) {if (alive.current) setError(failure);}
      const result = await gateway.warehouses(context(number, controller));
      if (context(number, controller).isCurrent()) setWarehouses(result);
      const saved = new URL(window.location.href).searchParams.get('review');
      if (saved) {const result = await gateway.read(saved, context(number, controller)); if (context(number, controller).isCurrent()) setValue(result);}
    } catch (failure) {if (alive.current) setError(failure);} finally {if (alive.current) setBusy(false);}});
    const connection = () => setOffline(!navigator.onLine); connection(); window.addEventListener('online', connection); window.addEventListener('offline', connection);
    const storageChanged = (event: StorageEvent) => {
      if (!isReplenishmentRecoveryKey(account.actorId, event.key)) return;
      try {syncPending();} catch (failure) {if (alive.current) setError(failure);}
    };
    window.addEventListener('storage', storageChanged);
    const timer = window.setInterval(() => {if (sessionTiming(account.session, performance.now() - account.observedAt).expired) {
      active.current?.abort(); ++serial.current; setExpired(true); setValue(null); setWarehouses([]); setPackages([]); setSources([]);
    }}, 500);
    return () => {alive.current = false; controller.abort(); active.current?.abort(); window.clearInterval(timer); window.removeEventListener('online', connection); window.removeEventListener('offline', connection); window.removeEventListener('storage', storageChanged);};
  }, [account, gateway, recovery, context, syncPending]);
  useEffect(() => {if (value) heading.current?.focus();}, [value]);
  useEffect(() => {if (!busy && !pending) return; const warn = (event: BeforeUnloadEvent) => {event.preventDefault(); event.returnValue = '';};
    window.addEventListener('beforeunload', warn); return () => window.removeEventListener('beforeunload', warn);}, [busy, pending]);
  async function work(action: (ctx: ReturnType<typeof context>) => Promise<void>) {
    if (busy || sending.current || !current()) return; sending.current = true; setBusy(true); setError(null); setNotice(''); active.current?.abort();
    const controller = new AbortController(); active.current = controller; const number = ++serial.current; const ctx = context(number, controller);
    try {await action(ctx);} catch (failure) {if (alive.current) {setError(failure); if (failure instanceof AccessFailure && [401, 403].includes(failure.status)) {setExpired(true); setValue(null);}}}
    finally {sending.current = false; if (alive.current) setBusy(false);}
  }
  function remember(result: ReplenishmentEvaluation) {setValue(result); const url = new URL(window.location.href); url.searchParams.set('review', result.id); window.history.replaceState(null, '', url);}
  async function selectPlace(selected: StockWarehouse) {await work(async ctx => {
    setHistory(null); setHistoryBefore(null); setPlace(null); setSource(null); setSources([]); setChoice(null); setPackages([]); setValue(null); setInputs({...emptyValues}); setInputReference(''); setReason('');
    const next = await gateway.sources(selected.location, ctx); if (ctx.isCurrent()) {setPlace(selected); setSources(next);}
  });}
  async function loadHistory(before: string | null = null) {if (!place) return; await work(async ctx => {
    setHistory(null); setHistoryBefore(null);
    const page = await gateway.history(place.location, before, ctx);
    if (ctx.isCurrent()) {setHistory(page.items); setHistoryBefore(page.nextBefore);}
  });}
  async function find(nextOffset = 0) {if (!place) return; await work(async ctx => {
    setChoice(null); setPackages([]); const result = await gateway.packages(place.location, search, nextOffset, ctx);
    if (ctx.isCurrent()) {setPackages(result.items); setOffset(nextOffset); setHasMore(BigInt(nextOffset + result.items.length) < BigInt(result.total));}
  });}
  async function submit(event: FormEvent) {
    event.preventDefault(); if (!place || !source || !choice || pending || storageBlocked) return;
    await work(async ctx => {
      // React state and storage events may lag another tab's already retained operation.
      if (syncPending()) return;
      const body = parseReplenishmentInput({location: place.location, poolId: source.id, expectedPoolRevision: source.revision, skuId: choice.skuId,
        expectedSkuRevision: choice.skuRevision, definitionId: choice.definitionId, inputs: replenishmentValues(inputs), inputReference: inputReference.trim(), reason: reason.trim()});
      const entry = {actorId: account.actorId, key: 'replenishment.' + crypto.randomUUID(), createdAt: new Date().toISOString()};
      try {recovery.save(entry); syncPending();} catch (failure) {setStorageBlocked(true); throw failure;}
      try {const result = await gateway.evaluate(body, entry.key, ctx); if (ctx.isCurrent()) {
        if (result.packageLabel !== choice.packageLabel || result.baseUnitsPerPackage !== choice.baseUnitsPerPackage || result.skuCode !== choice.skuCode) throw new AccessFailure(502, 'replenishment_response_invalid');
        clearPending(entry.key); remember(result);
      }} catch (failure) {
        if (failure instanceof RequestNotSentError || failure instanceof AccessFailure && failure.status < 500) clearPending(entry.key);
        throw failure;
      }
    });
  }
  async function recover() {if (!pending || storageBlocked) return; const entry = pending; await work(async ctx => {
    const result = await gateway.recover(entry.key, ctx); if (!ctx.isCurrent()) return;
    if (result) {clearPending(entry.key); remember(result); setNotice('Hasil yang sudah tersimpan berhasil ditemukan.');}
    else setNotice('Permintaan belum ditemukan sebagai hasil tersimpan. Referensinya tetap disimpan; periksa kembali sebelum memulai pemeriksaan lain.');
  });}
  if (expired) return <SessionAccessState error={null} kind="expired" returnPath={path} onRetry={refresh} />;
  const locked = busy || offline || storageBlocked || pending !== null;
  return <>
    {error !== null && <StateView kind="error" title={message(error)} />}{notice && <p role="status">{notice}</p>}{offline && <p role="status">Koneksi terputus. Pilihan dan pengiriman dinonaktifkan.</p>}
    {storageBlocked && <section className={common.review}><h2>Referensi pemeriksaan belum dapat dibaca</h2><p>Penyimpanan browser belum dapat dipastikan. Pengiriman dikunci agar referensi hasil sebelumnya tetap terlindungi.</p>
      <Button disabled={busy} onClick={() => {try {syncPending(); setError(null);} catch (failure) {setError(failure);}}}>Periksa referensi browser</Button></section>}
    {pending && <section className={common.review}><h2>Periksa hasil yang belum terkonfirmasi</h2><p>Referensi pemeriksaan disimpan tanpa isi formulir. Jangan mengirim pemeriksaan yang sama lagi.</p>
      <Button variant="primary" disabled={busy || offline || storageBlocked} onClick={() => void recover()}>Periksa hasil tersimpan</Button></section>}
    <Section title="Pilih gudang dan barang" busy={busy}>
      <label className={styles.field}>Gudang<select aria-label="Gudang" disabled={locked} value={place ? stockLocationKey(place.location) : ''}
        onChange={event => {const selected = warehouses.find(item => stockLocationKey(item.location) === event.target.value); if (selected) void selectPlace(selected);}}>
        <option value="">Pilih gudang</option>{warehouses.map(item => <option key={stockLocationKey(item.location)} value={stockLocationKey(item.location)}>{item.organizationName} · {item.shopName} · {item.warehouseName} · {item.channelName}</option>)}</select></label>
      {place && <><label className={styles.field}>Sumber stok<select aria-label="Sumber stok" disabled={locked} value={source?.id ?? ''} onChange={event => {setSource(sources.find(item => item.id === event.target.value) ?? null); setValue(null);}}>
        <option value="">Pilih sumber fisik</option>{sources.map(item => <option key={item.id} value={item.id}>{item.label} · {item.physicalKey}{item.active ? '' : ' · Tidak aktif'}</option>)}</select></label>
        {sources.length === 0 && <p>Belum ada sumber stok yang dapat ditampilkan untuk gudang ini.</p>}
        <form className={styles.search} onSubmit={event => {event.preventDefault(); void find();}}><label className={styles.field}>Cari barang<input aria-label="Cari barang" value={search} maxLength={128} disabled={locked} onChange={event => setSearch(event.target.value)} /></label><Button disabled={locked} type="submit">Cari kemasan</Button></form>
        <div className={styles.options}>{packages.map(item => <Button key={item.skuId} variant="secondary" type="button" disabled={locked} aria-pressed={choice?.skuId === item.skuId}
          onClick={() => {setChoice(item); setValue(null);}}><strong>{item.familyName} — {item.packageLabel}</strong><span>{item.skuCode} · {item.baseUnitsPerPackage} {item.baseUnitCode} per kemasan</span></Button>)}</div>
        <Pagination label="Halaman kemasan" start={offset + 1} end={offset + packages.length} hasPrevious={offset > 0} hasNext={hasMore} disabled={locked}
          onPrevious={() => void find(Math.max(0, offset - 25))} onNext={() => void find(offset + 25)} />
      </>}
    </Section>
    {place && <Section title="Riwayat pemeriksaan kebutuhan" label="Riwayat pemeriksaan kebutuhan"
      description="Hasil terdahulu untuk gudang ini. Setiap hasil merekam kondisi dan dasar angka saat diperiksa."
      actions={<Button disabled={locked} onClick={() => void loadHistory()}>Muat riwayat terbaru</Button>}>
      {history !== null && history.length === 0 && <StateView kind="empty" title="Belum ada hasil pada halaman ini." />}
      {history && <ul>{history.map(item => <li key={item.id}><Button variant="secondary" disabled={locked}
        onClick={() => void work(async ctx => {const saved = await gateway.read(item.id, ctx); if (ctx.isCurrent()) {
          if (!saved) throw new AccessFailure(409, 'replenishment_view_stale'); remember(saved);
        }})}>{item.skuCode} · {item.packageLabel} · {when(item.observedAt)} · Saran: {amount(item.suggestedPurchasePackages)}{item.suggestedPurchasePackages !== null ? ' kemasan' : ''}</Button></li>)}</ul>}
      {historyBefore !== null && <div className="ui-actions"><Button disabled={locked} onClick={() => void loadHistory(historyBefore)}>Riwayat lebih lama</Button></div>}
    </Section>}
    {choice && source && place && <form className="ui-section" onSubmit={submit}><h2>Dasar perhitungan</h2><p><strong>{choice.skuCode} · {choice.packageLabel}</strong> — seluruh jumlah di bawah memakai kemasan ini, berisi {choice.baseUnitsPerPackage} {choice.baseUnitCode}.</p>
      <p>Kosongkan angka yang belum diketahui. Batas peringatan masih dapat diperiksa bila pemakaian belum tersedia. Tidak ada pembelian yang dibuat otomatis.</p>
      <div className={styles.fields}>{([['dailyDemandPerDay', 'Pemakaian rata-rata per hari (kemasan)'], ['leadTimeDays', 'Waktu tunggu (hari)'], ['safetyStockPackages', 'Stok pengaman (kemasan)'], ['reorderPointPackages', 'Batas peringatan (kemasan)'], ['purchaseMinimumPackages', 'Minimum pembelian (kemasan)'], ['purchaseIncrementPackages', 'Kelipatan pembelian (kemasan)']] as const).map(([key, label]) =>
        <label className={styles.field} key={key}>{label}<input aria-label={label} inputMode={key === 'dailyDemandPerDay' ? 'decimal' : 'numeric'} value={inputs[key]} disabled={locked} required={key.startsWith('purchase')}
          onChange={event => {setInputs({...inputs, [key]: event.target.value}); setValue(null);}} /></label>)}</div>
      <label className={styles.field}>Referensi dasar angka<input aria-label="Referensi dasar angka" value={inputReference} disabled={locked} required maxLength={500} placeholder="Contoh: kebijakan kebutuhan yang telah ditinjau atau catatan pemakaian" onChange={event => setInputReference(event.target.value)} /></label>
      <label className={styles.field}>Alasan pemeriksaan<textarea aria-label="Alasan pemeriksaan" value={reason} disabled={locked} required maxLength={1000} onChange={event => setReason(event.target.value)} /></label>
      <Button variant="primary" disabled={locked} type="submit">Hitung dan simpan hasil</Button>
    </form>}
    {value && <section className={common.review} data-testid="replenishment-result"><h2 ref={heading} tabIndex={-1}>Hasil pemeriksaan tersimpan</h2>
      <p>{value.skuCode} · {value.packageLabel} · {value.baseUnitsPerPackage} {value.baseUnitCode} per kemasan</p><p>Diamati {when(value.observedAt)}. Hasil ini merekam keadaan saat pemeriksaan; hitung ulang sebelum mengambil keputusan berdasarkan perubahan terbaru.</p>
      <h3>Dasar angka saat pemeriksaan</h3><div data-testid="replenishment-recorded-inputs"><Facts items={
        ([['Pemakaian rata-rata per hari', value.inputs.dailyDemandPerDay, 'kemasan per hari'],
          ['Waktu tunggu', value.inputs.leadTimeDays === null ? null : String(value.inputs.leadTimeDays), 'hari'],
          ['Stok pengaman', value.inputs.safetyStockPackages, 'kemasan'], ['Batas peringatan', value.inputs.reorderPointPackages, 'kemasan'],
          ['Minimum pembelian', value.inputs.purchaseMinimumPackages, 'kemasan'], ['Kelipatan pembelian', value.inputs.purchaseIncrementPackages, 'kemasan']] as [string, string | null, string][])
          .map(([label, number, unit]) => ({ term: label, value: number === null ? 'Belum diketahui' : number + ' ' + unit }))} /></div>
      {value.thresholdAlert !== null && (value.thresholdAlert ? <div className="ui-notice" data-tone="warning">Stok berada pada atau di bawah batas peringatan.</div> : <p>Stok berada di atas batas peringatan.</p>)}
      <Facts items={([['Stok layak sebelum reservasi', value.usableBeforeReservations], ['Direservasi untuk pesanan', value.reservedPackages], ['Komitmen tambahan di luar reservasi', value.additionalUnreservedCommitments],
        ['Tersedia setelah komitmen', value.availableAfterCommitments], ['Pasokan terverifikasi dalam masa tunggu', value.eligibleSupplyPackages], ['Posisi stok', value.inventoryPosition], ['Kebutuhan selama masa tunggu dan stok pengaman', value.requiredStockPackages],
        ['Saran jumlah pembelian', value.suggestedPurchasePackages]] as [string, string | null][]).map(([label, number]) => ({ term: label, value: <>{amount(number)}{number !== null ? ' kemasan' : ''}</> }))} />
      <p>Perhitungan: pemakaian per hari × hari tunggu dibulatkan ke atas, ditambah stok pengaman, lalu dikurangi posisi stok. Kekurangan mengikuti minimum dan kelipatan pembelian yang Anda isi.</p>
      <p>Dasar angka: {value.inputReference}</p><p>Aturan: {value.ruleVersion}</p>
      {value.warnings.length > 0 && <div role="status"><h3>Data yang perlu diperiksa</h3><ul>{value.warnings.map(code => <li key={code}>{warnings[code] ?? 'Ada informasi sumber yang perlu diperiksa sebelum jumlah pembelian dapat dipastikan.'}</li>)}</ul></div>}
      {value.supplies.length > 0 && <><h3>Pasokan masuk</h3><ul>{value.supplies.map(item => <li key={item.reference}>{item.reference} · {amount(item.packages)} kemasan · {item.expectedAt ? when(item.expectedAt) : 'Tanggal belum diketahui'} · {supplyStates[item.state]}</li>)}</ul></>}
      {account.actions.includes('procurement.prepare') && value.suggestedPurchasePackages !== null && BigInt(value.suggestedPurchasePackages) > BigInt(0) && BigInt(value.suggestedPurchasePackages) <= BigInt(1000000) &&
        <Link className={buttonClassName({ variant: 'primary' })} href={'/procurement/requests?replenishment=' + value.id}>Tinjau sebagai permintaan pengadaan</Link>}
      <p>Petugas tetap memilih jalur pengadaan, memeriksa jumlah dan mengajukan permintaan untuk persetujuan.</p>
    </section>}
  </>;
}
