'use client';
import { orderLineKey, sameOrderLine } from '../ordering/order-line-identity';

import Link from 'next/link';
import {ScanInput} from '../inventory/ScanInput';
import {sameScanPackage} from '../inventory/scan-client';
import { useCallback, useEffect, useRef, useState } from 'react';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { AdministrationNavigation } from '../administration/AdministrationNavigation';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import { submitTrackedRequest } from '../administration/operation-journal';
import { createDispatchGateway, prepareDispatchCommand, type CourierChoice, type DispatchCommand, type DispatchPreparation, type DispatchQueueItem, type DispatchResult, type DispatchReview } from './dispatch-client';
import common from '../administration/access.module.css';
import styles from '../ordering/order-draft.module.css';
import { SessionAccessState } from "@/components/workspace-kit/session-access-state";
import { orderNumber } from '@/lib/document-numbers';

type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
function message(error: unknown) {
  if (error instanceof AccessFailure) {
    if (error.code === 'fulfillment_source_obligation_unavailable') return 'Riwayat stok untuk kiriman ini belum dapat diverifikasi. Hubungi penanggung jawab toko. Jika keberangkatan sudah dikirim, gunakan Periksa hasil sebelum mengirim ulang.';
    if (error.code === 'shipment_dispatch_admission_unavailable') return 'Sumber stok, biaya atau kebijakan pengiriman belum disetujui atau perlu diperbarui. Hubungi penanggung jawab toko.';
    if (error.code === 'shipment_courier_unavailable') return 'Penugasan kurir sudah berubah. Muat kembali daftar kurir yang tersedia.';
    if (error.status === 409) return 'Persiapan, stok atau penugasan berubah. Muat kembali dan tinjau pengiriman sebelum mengonfirmasi.';
    if (error.code === 'shipment_picked_lots_changed' || error.code === 'invalid_shipment_dispatch_request') return 'Pilih kurir, isi jumlah lot sesuai persiapan, dan tambahkan catatan keberangkatan.';
    if (error.status > 0 && error.status < 500) return accessMessage(error);
  }
  return 'Hasil belum dapat dipastikan. Jika keberangkatan sudah dikirim, gunakan Periksa hasil sebelum membuat pengiriman lain.';
}
export function ShipmentDispatchWorkspace() {
  const [identity] = useState(() => createIdentityGateway()), [access] = useState(() => createAccessGateway());
  const [account, setAccount] = useState<Account | null>(null), [loading, setLoading] = useState(true), [error, setError] = useState<unknown>(null), [revision, setRevision] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setAccount(null); setError(null); setLoading(true);
      try {
        const session = await identity.session(controller.signal), observedAt = performance.now(); if (!active) return;
        if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
        const current = await access.current(); if (!active) return;
        if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
        // The server admits dispatch with shipment.dispatch alone; the page asks for no more (G-031).
        if (!current.allowedActions.includes('shipment.dispatch')) throw new AccessFailure(403, 'scope_denied');
        if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
        setAccount({ actorId: current.userId, actions: current.allowedActions, session, observedAt }); setElapsed(performance.now() - observedAt);
      } catch (failure) { if (active) setError(failure); } finally { if (active) setLoading(false); }
    }); return () => { active = false; controller.abort(); };
  }, [identity, access, revision]);
  useEffect(() => {
    const timer = window.setInterval(() => { if (account) setElapsed(performance.now() - account.observedAt); }, 1000);
    const restored = (event: PageTransitionEvent) => { if (event.persisted) { setAccount(null); setRevision(value => value + 1); } };
    window.addEventListener('pageshow', restored); return () => { window.clearInterval(timer); window.removeEventListener('pageshow', restored); };
  }, [account]);
  const failed = useCallback((failure: unknown) => { if (failure instanceof AccessFailure && (failure.status === 401 || failure.code === 'actor_changed' || failure.status === 403 && failure.code !== 'shipment_courier_unavailable')) { setAccount(null); setError(failure); } }, []);
  const expired = account !== null && sessionTiming(account.session, elapsed).expired;
  return <main className={common.page}><header className={common.header}><div><Link href="/identity/account" className={common.brand}>Unit Toko</Link>
    <h1>Keberangkatan kiriman</h1><p>Periksa kemasan yang disiapkan, pilih lot fisik dan kurir, lalu catat barang yang diberangkatkan.</p></div><Link href="/identity/account" className={common.secondary}>Akun saya</Link></header>
    {error !== null && account !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    {loading ? <p role="status">Memeriksa akun…</p> : !account || expired ? <SessionAccessState error={error} returnPath="/fulfillment/dispatches" onRetry={() => setRevision(value => value + 1)} />
      : <DispatchDesk key={account.actorId + ':' + account.observedAt} account={account} onFailure={failed} />}
  </main>;
}
function DispatchDesk({ account, onFailure }: { account: Account; onFailure: (error: unknown) => void }) {
  const { actorId } = account, [gateway] = useState(() => createDispatchGateway()), [ledger] = useState(() => new MutationLedger());
  const [page, setPage] = useState<{ items: DispatchQueueItem[]; hasMore: boolean }>({ items: [], hasMore: false }), [before, setBefore] = useState<string | null>(null), [reload, setReload] = useState(0);
  const [prepared, setPrepared] = useState<DispatchPreparation | null>(null), [couriers, setCouriers] = useState<CourierChoice[]>([]), [moreCouriers, setMoreCouriers] = useState(false), [courierId, setCourierId] = useState('');
  const [values, setValues] = useState<Record<string, string>>({}), [reason, setReason] = useState(''), [review, setReview] = useState<DispatchReview | null>(null), [command, setCommand] = useState<DispatchCommand | null>(null);
  const [busy, setBusy] = useState(false), [dispatchPending, setDispatchPending] = useState(false), [recovering, setRecovering] = useState(false), [error, setError] = useState<unknown>(null), [result, setResult] = useState<DispatchResult | null>(null);
  const recovery = useOperationRecovery(actorId), alive = useRef(false), pending = useRef(false), panel = useRef<HTMLElement>(null), recovered = useRef<DispatchResult | null>(null);
  const current = useCallback(() => alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const locked = busy || recovering || recovery.locked;
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setBusy(true); setError(null); setPrepared(null); setReview(null); setCommand(null); setPage({ items: [], hasMore: false });
      try { const next = await gateway.list(actorId, before, controller.signal); if (active && current()) setPage(next); }
      catch (failure) { if (active) { setError(failure); onFailure(failure); } } finally { if (active) setBusy(false); }
    }); return () => { active = false; controller.abort(); };
  }, [gateway, actorId, before, reload, onFailure, current]);
  useEffect(() => { if (prepared || review) panel.current?.focus(); }, [prepared, review]);
  async function select(suborderId: string) {
    if (!current() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); setResult(null); setPrepared(null); setReview(null); setCommand(null);
    try {
      const source = await gateway.read(actorId, suborderId); if (!current()) return;
      const choices = await gateway.couriers(actorId, suborderId); if (!current()) return;
      setPrepared(source); setCouriers(choices.items); setMoreCouriers(choices.hasMore); setCourierId(''); setValues({}); setReason(''); setScanSelection(null); setScanBlocked(false);
    } catch (failure) { if (current()) { setError(failure); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) setBusy(false); }
  }
  async function loadCouriers() {
    if (!prepared || !current() || locked || pending.current || !moreCouriers) return; pending.current = true; setBusy(true); setError(null);
    try { const next = await gateway.couriers(actorId, prepared.source.suborderId, couriers.at(-1)!.userId); if (current()) { setCouriers(previous => [...previous, ...next.items]); setMoreCouriers(next.hasMore); } }
    catch (failure) { if (current()) { setError(failure); onFailure(failure); } } finally { pending.current = false; if (alive.current) setBusy(false); }
  }
  const [scanBlocked, setScanBlocked] = useState(false), [scannedSku, setScannedSku] = useState<string | null>(null),
    [scanSelection, setScanSelection] = useState<import('../inventory/scan-client').OperationalScanSelection | null>(null);
  async function preview() {
    if (!prepared || !current() || locked || pending.current || scanBlocked) return; pending.current = true; setBusy(true); setError(null);
    try {
      const lots = prepared.lots.filter(lot => (values[lot.reservationId] ?? '').trim() !== '').map(lot => ({ reservationId: lot.reservationId,
        packages: /^[1-9][0-9]{0,6}$(?![\s\S])/.test(values[lot.reservationId]) ? Number(values[lot.reservationId]) : NaN }));
      const next = await gateway.preview(actorId, prepared, courierId, lots); if (!current()) return;
      setCommand(prepareDispatchCommand(actorId, next, reason.trim(), scanSelection ? [scanSelection] : undefined)); setReview(next);
    } catch (failure) { if (current()) { setError(failure); onFailure(failure); } } finally { pending.current = false; if (alive.current) setBusy(false); }
  }
  async function confirm() {
    if (!command || !current() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null);
    try {
      if (recovery.journal.list(actorId).length > 0) throw new AccessFailure(400, 'resolve_previous_attempt');
      const accepted = await submitTrackedRequest(ledger, recovery.journal, actorId, 'fulfillment.shipment.dispatch', 'fulfillment.shipment.dispatch', command,
        key => { setDispatchPending(true); recovery.refresh(); return gateway.dispatch(command, key, current); });
      if (current()) { setResult(accepted); setPrepared(null); setCommand(null); setReview(null); setReload(value => value + 1); }
    } catch (failure) { if (current()) { setCommand(null); setReview(null); setError(failure); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); setDispatchPending(false); recovery.refresh(); } }
  }
  return <>
    <AdministrationNavigation current="dispatches" actions={account.actions} locked={locked} />
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy} onBusyChange={setRecovering}
      recoveryAdapters={{ 'fulfillment.shipment': { lookup: async entry => { const receipt = await gateway.recover(entry.actorId, entry.key); recovered.current = receipt?.result ?? null; return receipt; } } }}
      onFailure={failure => { setError(failure); onFailure(failure); }} onResolved={() => {
        recovery.refresh(); ledger.resetAfterReconciliation(); if (current() && recovered.current) { setResult(recovered.current); recovered.current = null; setReload(value => value + 1); }
      }} />
    {dispatchPending && <p role="status">Permintaan keberangkatan sedang diproses. Referensi bantuan telah disimpan. Tunggu hasil sebelum memeriksa atau mengirim permintaan lain.</p>}
    {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}{recovery.error !== null && <p role="alert" className={common.alert}>{accessMessage(recovery.error)}</p>}
    {result && <section className={common.success} role="status"><h2>Keberangkatan tercatat</h2><p>Kiriman {result.shipmentId} · nomor {result.numberEntity}/{result.numberChannel}/{result.numberYear}/{result.number}</p>
      <p>Stok dan sisa pesanan sudah diperbarui. Penerimaan barang oleh cabang belum dicatat.</p></section>}
    <section className={common.accountSection} aria-label="Persiapan untuk diberangkatkan" aria-busy={busy}><h2>Persiapan tersimpan</h2>
      {busy && !dispatchPending && <p role="status">Memuat pengiriman…</p>}{!busy && page.items.length === 0 && <p>Belum ada persiapan dalam penugasan ini.</p>}
      <ul className={styles.lines}>{page.items.map(item => <li className={styles.line} key={item.preparationId}><strong>{item.source.buyerName}</strong> · Pesanan {orderNumber(item.source.orderId)}
        <p>{item.lines.map(line => { const product = item.source.lines.find(product => sameOrderLine(product, line))!; return `${product.productName} — ${line.packages} ${product.definition.packageLabel}`; }).join(', ')}</p>
        {item.canDispatch ? <button className={common.secondary} disabled={locked} onClick={() => void select(item.source.suborderId)}>Periksa keberangkatan pesanan {orderNumber(item.source.orderId)}</button>
          : <p>{item.source.canPrepare ? 'Sisa pesanan berubah. Perbarui persiapan untuk pengiriman berikutnya.' : 'Pesanan ini tidak memiliki sisa yang dapat diberangkatkan.'}</p>}</li>)}</ul>
      <div className={common.actions}><button className={common.secondary} disabled={locked} onClick={() => { setBefore(null); setReload(value => value + 1); }}>Muat persiapan terbaru</button>
        {page.hasMore && <button className={common.secondary} disabled={locked} onClick={() => setBefore(page.items.at(-1)!.preparationId)}>Persiapan lebih lama</button>}</div>
    </section>
    {prepared && <section ref={panel} tabIndex={-1} className={styles.review} aria-label="Pemeriksaan keberangkatan"><h2>Keberangkatan pesanan {orderNumber(prepared.source.orderId)}</h2>
      <p>{prepared.source.buyerName}</p><p className={styles.address}>{prepared.source.delivery.recipientName} · {prepared.source.delivery.phone}{'\n'}{prepared.source.delivery.address}{'\n'}{prepared.source.delivery.city} {prepared.source.delivery.postalCode}</p>
      <ul>{prepared.lines.map(line => { const item = prepared.source.lines.find(item => sameOrderLine(item, line))!;
        return <li key={orderLineKey(line)}>{item.productName} — {line.packages} {item.definition.packageLabel} · total {(BigInt(line.packages) * BigInt(item.definition.baseUnitsPerPackage)).toString()} {item.definition.baseUnitCode}</li>; })}</ul>
      {!review && <ScanInput key={prepared.preparationId + ':' + prepared.preparationRevision + ':' + prepared.source.review} actorId={actorId} disabled={locked} current={current}
        context={{purpose: 'dispatch', location: null, documentId: prepared.source.suborderId, documentRevision: prepared.source.revision,
          auxiliaryId: prepared.preparationId, auxiliaryRevision: prepared.preparationRevision, sourceReview: prepared.source.review}}
        onReset={() => {setScanSelection(null); setScanBlocked(true); setScannedSku(null); setValues({});}} onManual={() => {setScanSelection(null); setScanBlocked(false); setScannedSku(null);}}
        onConfirm={candidate => {const line = prepared.source.lines.find(item => item.skuId === candidate.skuId && prepared.lines.some(selected => sameOrderLine(item, selected)));
          if (!current() || !line || candidate.targetReference !== line.skuId ||
            !sameScanPackage(candidate, {...line.definition, skuId: line.skuId, skuCode: line.skuCode, definitionId: line.definition.id, definitionVersion: line.definition.version}))
            throw new AccessFailure(409, 'operational_scan_context_changed');
          setScannedSku(line.skuId); setScanSelection(candidate.selection); setScanBlocked(false);
        }} />}
      {scannedSku && !review && <p role="status">Kemasan telah dikonfirmasi dari hasil pindai. Pilih lot yang sesuai dan isi jumlah sebenarnya pada baris {prepared.source.lines.find(item => item.skuId === scannedSku)?.skuCode}.</p>}
      {review && command ? <><h3>Konfirmasi barang dan kurir</h3><p>Kurir: <strong>{review.courierName}</strong></p><ul>{review.selected.map(lot => {
        const reserved = prepared.lots.find(item => item.reservationId === lot.reservationId)!; const product = prepared.source.lines.find(item => sameOrderLine(item, reserved))!;
        return <li key={lot.reservationId}>{product.productName} · Lot {reserved.lotId} · {lot.packages} {product.definition.packageLabel}</li>; })}</ul>
        <p>Catatan: {command.reason}</p><p>Konfirmasi saat barang diserahkan untuk berangkat. Jumlah tersebut akan dikurangi dari stok fisik dan reservasi pesanan.</p>
        <div className={common.actions}><button className={common.primary} disabled={locked} onClick={() => void confirm()}>Konfirmasi keberangkatan</button>
          <button className={common.secondary} disabled={locked} onClick={() => { setReview(null); setCommand(null); }}>Kembali ke lot dan kurir</button></div>
      </> : <form onSubmit={event => { event.preventDefault(); void preview(); }}><fieldset disabled={locked}><legend>Pilih lot fisik dan kurir</legend>
        <p>Isi jumlah pada lot yang benar-benar diambil. Kosongkan lot yang tidak digunakan. Total harus sesuai kemasan yang disiapkan.</p>
        {prepared.lots.filter(lot => lot.remainingPackages > 0 && prepared.lines.some(line => sameOrderLine(line, lot))).map(lot => {
          const item = prepared.source.lines.find(item => sameOrderLine(item, lot))!;
          return <div className={styles.line} key={lot.reservationId}><strong>{item.productName} — {item.definition.packageLabel}</strong><p>Lot {lot.lotId} · sisa reservasi {lot.remainingPackages} kemasan{lot.expiresOn ? ` · kedaluwarsa ${lot.expiresOn}` : ''}</p>
            <label>Jumlah lot {lot.lotId}<input inputMode="numeric" maxLength={7} value={values[lot.reservationId] ?? ''} onChange={event => setValues(previous => ({ ...previous, [lot.reservationId]: event.target.value }))} /></label></div>;
        })}
        <label htmlFor="shipment-dispatch-courier">Kurir</label><select id="shipment-dispatch-courier" required value={courierId} onChange={event => setCourierId(event.target.value)}><option value="">Pilih kurir yang ditugaskan</option>{couriers.map(courier => <option key={courier.userId} value={courier.userId}>{courier.displayName}</option>)}</select>
        {couriers.length === 0 && <p>Belum ada kurir dengan penugasan aktif untuk toko dan kanal ini.</p>}
        {moreCouriers && <button type="button" className={common.secondary} onClick={() => void loadCouriers()}>Tampilkan kurir lainnya</button>}
        <label>Catatan keberangkatan<input required maxLength={1000} value={reason} onChange={event => setReason(event.target.value)} /></label>
        <button className={common.primary} disabled={locked || !courierId || scanBlocked}>Tinjau keberangkatan</button></fieldset></form>}
      <button className={common.secondary} disabled={locked} onClick={() => void select(prepared.source.suborderId)}>Muat ulang lot dan kurir</button>
    </section>}
  </>;
}
