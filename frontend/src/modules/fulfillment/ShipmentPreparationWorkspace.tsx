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
import { createPreparationGateway, initialPreparationValues, prepareShipmentCommand, type FulfillmentOrder, type Preparation, type PreparationCommand, type PreparationResult } from './preparation-client';
import common from '../administration/access.module.css';
import styles from '../ordering/order-draft.module.css';
import { SessionAccessState } from "@/components/workspace-kit/session-access-state";
import { orderNumber } from '@/lib/document-numbers';

type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
function message(error: unknown) {
  if (error instanceof AccessFailure) {
    if (error.status === 409 || error.code === 'shipment_order_changed') return 'Pesanan atau persiapannya berubah. Muat kembali dan periksa jumlah kemasan sebelum menyimpan.';
    if (error.code === 'invalid_shipment_preparation' || error.code === 'shipment_quantity_exceeds_remainder') return 'Pilih kemasan, isi jumlah dalam sisa pesanan, dan tambahkan catatan persiapan.';
    if (error.status > 0 && error.status < 500) return accessMessage(error);
  }
  return 'Permintaan belum dapat dipastikan. Jika penyimpanan sudah dikirim, gunakan Periksa hasil sebelum mengirim lagi.';
}
export function ShipmentPreparationWorkspace() {
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
        if (!current.allowedActions.includes('shipment.prepare')) throw new AccessFailure(403, 'scope_denied');
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
  const failed = useCallback((failure: unknown) => { if (failure instanceof AccessFailure && ([401, 403].includes(failure.status) || failure.code === 'actor_changed')) { setAccount(null); setError(failure); } }, []);
  const expired = account !== null && sessionTiming(account.session, elapsed).expired;
  return <main className={common.page}><header className={common.header}><div><Link href="/identity/account" className={common.brand}>Unit Toko</Link>
    <h1>Persiapan kiriman</h1><p>Siapkan jumlah kemasan dari pesanan toko yang sudah diterima. Persiapan dapat mencakup sebagian pesanan.</p></div><Link href="/identity/account" className={common.secondary}>Akun saya</Link></header>
    {error !== null && account !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    {loading ? <p role="status">Memeriksa akun…</p> : !account || expired ? <SessionAccessState kind={account ? "expired" : undefined} error={error} returnPath="/fulfillment/preparations" onRetry={() => setRevision(value => value + 1)} />
      : <PreparationDesk key={account.actorId + ':' + account.observedAt} account={account} onFailure={failed} />}
  </main>;
}
function PreparationDesk({ account, onFailure }: { account: Account; onFailure: (error: unknown) => void }) {
  const { actorId } = account, [gateway] = useState(() => createPreparationGateway()), [ledger] = useState(() => new MutationLedger());
  const [page, setPage] = useState<{ items: FulfillmentOrder[]; hasMore: boolean }>({ items: [], hasMore: false }), [before, setBefore] = useState<string | null>(null), [reload, setReload] = useState(0);
  const [selected, setSelected] = useState<FulfillmentOrder | null>(null), [saved, setSaved] = useState<Preparation | null>(null);
  const [values, setValues] = useState<Record<string, { selected: boolean; packages: string }>>({}), [reason, setReason] = useState(''), [staged, setStaged] = useState<PreparationCommand | null>(null);
  const [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState<string | null>(null);
  const recovery = useOperationRecovery(actorId), alive = useRef(false), pending = useRef(false), panel = useRef<HTMLElement>(null), recovered = useRef<PreparationResult | null>(null);
  const current = useCallback(() => alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const locked = busy || recovering || recovery.locked;
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setBusy(true); setError(null); setSelected(null); setSaved(null); setStaged(null); setPage({ items: [], hasMore: false });
      try { const next = await gateway.list(actorId, before, controller.signal); if (active && current()) setPage(next); }
      catch (failure) { if (active) { setError(failure); onFailure(failure); } } finally { if (active) setBusy(false); }
    }); return () => { active = false; controller.abort(); };
  }, [gateway, actorId, before, reload, onFailure, current]);
  useEffect(() => { if (selected || staged) panel.current?.focus(); }, [selected, staged]);
  async function load(suborderId: string) {
    const source = await gateway.source(actorId, suborderId); if (!current()) return;
    const preparation = await gateway.read(actorId, suborderId); if (!current()) return;
    setSelected(source); setSaved(preparation); setStaged(null); setReason(''); setScanSelection(null); setScanBlocked(false);
    setValues(initialPreparationValues(source, preparation));
  }
  async function select(suborderId: string) {
    if (!current() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); setNotice(null);
    try { await load(suborderId); } catch (failure) { if (current()) { setError(failure); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) setBusy(false); }
  }
  const [scanBlocked, setScanBlocked] = useState(false), [scanSelection, setScanSelection] = useState<import('../inventory/scan-client').OperationalScanSelection | null>(null);
  async function review() {
    if (!selected || !current() || locked || pending.current || scanBlocked) return; pending.current = true; setBusy(true); setError(null); setNotice(null);
    try {
      const lines = selected.lines.filter(line => values[orderLineKey(line)]?.selected).map(line => ({ skuId: line.skuId, lineRevision: line.lineRevision, packages: /^[1-9][0-9]{0,6}$(?![\s\S])/.test(values[orderLineKey(line)].packages) ? Number(values[orderLineKey(line)].packages) : NaN }));
      const command = prepareShipmentCommand(actorId, selected, saved?.revision ?? '0', lines, reason.trim(), scanSelection ? [scanSelection] : undefined);
      const source = await gateway.source(actorId, selected.suborderId); if (!current()) return;
      const preparation = await gateway.read(actorId, selected.suborderId); if (!current()) return;
      if (!source.canPrepare || source.review !== command.review || (preparation?.revision ?? '0') !== command.expectedRevision) throw new AccessFailure(409, 'shipment_order_changed');
      setStaged(command);
    } catch (failure) { if (current()) { setError(failure); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) setBusy(false); }
  }
  async function confirm() {
    if (!staged || !current() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); setNotice(null); const command = staged;
    try {
      if (recovery.journal.list(actorId).length > 0) throw new AccessFailure(400, 'resolve_previous_attempt');
      await submitTrackedRequest(ledger, recovery.journal, actorId, 'fulfillment.preparation.save', 'fulfillment.preparation.save', command,
        key => gateway.save(command, key, current));
      if (current()) { await load(command.suborderId); if (current()) setNotice('Persiapan tersimpan. Kiriman belum diberangkatkan.'); }
    } catch (failure) { if (current()) { setStaged(null); setError(failure); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); recovery.refresh(); } }
  }
  return <>
    <AdministrationNavigation current="preparations" actions={account.actions} locked={locked} />
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy} onBusyChange={setRecovering}
      recoveryAdapters={{ 'fulfillment.preparation': { lookup: async entry => { const result = await gateway.recover(entry.actorId, entry.key); recovered.current = result?.result ?? null; return result; } } }}
      onFailure={failure => { setError(failure); onFailure(failure); }} onResolved={() => {
        recovery.refresh(); ledger.resetAfterReconciliation(); const result = recovered.current; recovered.current = null;
        if (result && current()) { setError(null); setBusy(true); void load(result.suborderId).then(() => {
          if (current()) setNotice('Persiapan ditemukan dan tercatat. Kiriman belum diberangkatkan.');
        }).catch(failure => { if (current()) { setError(failure); onFailure(failure); } }).finally(() => { if (alive.current) setBusy(false); }); }
      }} />
    {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}{recovery.error !== null && <p role="alert" className={common.alert}>{accessMessage(recovery.error)}</p>}
    {notice && <p role="status" className={common.success}>{notice}</p>}
    <section className={common.accountSection} aria-label="Pesanan siap disiapkan" aria-busy={busy}><h2>Pesanan diterima toko</h2>
      {busy && <p role="status">Memuat persiapan…</p>}{!busy && page.items.length === 0 && <p>Belum ada sisa pesanan yang dapat disiapkan dalam penugasan ini.</p>}
      <ul className={styles.lines}>{page.items.map(item => <li className={styles.line} key={item.suborderId}><strong>{item.buyerName}</strong> · Pesanan {orderNumber(item.orderId)}
        <p>{item.lines.map(line => line.productName + ' — ' + line.definition.packageLabel).join(', ')}</p>
        <button className={common.secondary} disabled={locked} onClick={() => void select(item.suborderId)}>Siapkan pesanan {orderNumber(item.orderId)} / {item.suborderId}</button></li>)}</ul>
      <div className={common.actions}><button className={common.secondary} disabled={locked} onClick={() => { setBefore(null); setReload(value => value + 1); }}>Muat pesanan terbaru</button>
        {page.hasMore && <button className={common.secondary} disabled={locked} onClick={() => setBefore(page.items.at(-1)!.suborderId)}>Pesanan lebih lama</button>}</div>
    </section>
    {selected && <section ref={panel} tabIndex={-1} className={styles.review} aria-label="Rencana persiapan"><h2>Siapkan pesanan {orderNumber(selected.orderId)}</h2>
      <p>{selected.buyerName}</p><p className={styles.address}>{selected.delivery.recipientName} · {selected.delivery.phone}{'\n'}{selected.delivery.address}{'\n'}{selected.delivery.city} {selected.delivery.postalCode}</p>
      {saved && <p role="status">Persiapan versi {saved.revision} tersimpan.{!saved.currentSourceMatches && ' Pesanan sudah berubah; persiapan ini perlu ditinjau ulang sebelum digunakan.'}</p>}
      {selected.canPrepare && !staged && <ScanInput key={selected.suborderId + ':' + selected.review} actorId={actorId} disabled={locked} current={current}
        context={{purpose: 'picking', location: null, documentId: selected.suborderId, documentRevision: selected.revision,
          auxiliaryId: null, auxiliaryRevision: null, sourceReview: selected.review}}
        onReset={() => {setScanSelection(null); setScanBlocked(true); setValues(Object.fromEntries(selected.lines.map(line => [orderLineKey(line), {selected: false, packages: ''}])));}}
        onManual={() => {setScanSelection(null); setScanBlocked(false);}} onConfirm={candidate => {
          const matches = selected.lines.filter(item => item.skuId === candidate.skuId && item.remainingPackages > 0 && item.definition.id === candidate.definitionId);
          const line = matches.length === 1 ? matches[0] : matches.find(item => values[orderLineKey(item)]?.selected);
          if (!current() || !line || candidate.targetReference !== line.skuId || !sameScanPackage(candidate, {...line.definition, skuId: line.skuId, skuCode: line.skuCode,
            definitionId: line.definition.id, definitionVersion: line.definition.version})) throw new AccessFailure(409, 'operational_scan_context_changed');
          setValues(previous => ({...previous, [orderLineKey(line)]: {selected: true, packages: ''}})); setScanSelection(candidate.selection); setScanBlocked(false);
        }} />}
      {!selected.canPrepare ? <p role="alert">Sisa pesanan tidak lagi dapat disiapkan. Riwayat persiapan tetap tersimpan.</p> : staged ? <>
        <h3>Periksa kemasan yang dipilih</h3><ul>{staged.lines.map(line => { const item = selected.lines.find(item => sameOrderLine(item, line))!;
          return <li key={orderLineKey(line)}>{item.productName} — {line.packages} {item.definition.packageLabel} · total {(BigInt(line.packages) * BigInt(item.definition.baseUnitsPerPackage)).toString()} {item.definition.baseUnitCode}</li>; })}</ul>
        <p>Catatan: {staged.reason}</p><p>Menyimpan persiapan belum memberangkatkan barang. Jumlah dan akses akan diperiksa lagi sebelum pengiriman.</p>
        <div className={common.actions}><button className={common.primary} disabled={locked} onClick={() => void confirm()}>Konfirmasi persiapan</button>
          <button className={common.secondary} disabled={locked} onClick={() => setStaged(null)}>Kembali ke jumlah</button></div>
      </> : <form onSubmit={event => { event.preventDefault(); void review(); }}><fieldset disabled={locked}><legend>Pilih kemasan untuk persiapan ini</legend>
        <p>Pilih satu rincian persetujuan per barang untuk kiriman ini.</p>
        {selected.lines.map(line => <div key={orderLineKey(line)} className={styles.line}><label className={common.checkbox}>
          <input type="checkbox" checked={values[orderLineKey(line)]?.selected ?? false} disabled={line.remainingPackages === 0} onChange={event => setValues(previous => Object.fromEntries(selected.lines.map(item => [orderLineKey(item), {...previous[orderLineKey(item)], selected: sameOrderLine(item, line) ? event.target.checked : item.skuId === line.skuId && event.target.checked ? false : previous[orderLineKey(item)]?.selected ?? false}])))} />
          <span>{line.productName} — {line.definition.packageLabel} · {line.lineRevision === '0' ? 'pesanan awal' : `persetujuan ke-${line.lineRevision}`}</span></label><p>{line.definition.baseUnitsPerPackage} {line.definition.baseUnitCode} per kemasan · sisa {line.remainingPackages} kemasan</p>
          <label>Jumlah {line.skuCode}<input inputMode="numeric" value={values[orderLineKey(line)]?.packages ?? ''} disabled={!values[orderLineKey(line)]?.selected || line.remainingPackages === 0} maxLength={7}
            onChange={event => setValues(previous => ({ ...previous, [orderLineKey(line)]: { ...previous[orderLineKey(line)], packages: event.target.value } }))} /></label></div>)}
        <label>Catatan persiapan<input required maxLength={1000} value={reason} onChange={event => setReason(event.target.value)} /></label>
        <button className={common.primary} disabled={locked || scanBlocked}>Tinjau persiapan</button></fieldset></form>}
      <button className={common.secondary} disabled={locked} onClick={() => void select(selected.suborderId)}>Muat ulang persiapan</button>
    </section>}
  </>;
}
