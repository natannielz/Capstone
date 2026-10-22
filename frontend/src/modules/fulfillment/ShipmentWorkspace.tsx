'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure, accessMessage } from '../administration/workflow';
import { AdministrationNavigation } from '../administration/AdministrationNavigation';
import { createShipmentGateway, type Shipment } from './shipment-client';
import { ShipmentDocumentPanel } from './ShipmentDocumentPanel';
import { ShipmentReceiptPanel } from './ShipmentReceiptPanel';
import { DeliveryAttemptPanel, visitTime } from './DeliveryAttemptPanel';
import { createDeliveryAttemptGateway, deliveryOutcomes, type DeliveryAttempt } from './delivery-attempt-client';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import common from '../administration/access.module.css';
import local from './fulfillment.module.css';
import { dateTimeFormatter } from '@/lib/format';
import { SessionAccessState } from "@/components/workspace-kit/session-access-state";
import { orderNumber } from '@/lib/document-numbers';
import { Button } from '@/components/workspace-kit/button';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { Section } from '@/components/workspace-kit/section';
import { StateView } from '@/components/workspace-kit/state-view';
import { DataList } from '@/components/workspace-kit/data-list';
import { Tabs } from '@/components/workspace-kit/tabs';

type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
const time = (value: string) => dateTimeFormatter.format(new Date(value));
const number = (item: Shipment) => `${item.numberEntity}/${item.numberChannel}/${item.numberYear}/${item.number}`;
function message(error: unknown) {
  if (error instanceof AccessFailure && error.status >= 400 && error.status < 500) return accessMessage(error);
  if (error instanceof IdentityFailure) return 'Masuk dengan akun organisasi Anda untuk melanjutkan.';
  return 'Data kiriman belum dapat dimuat. Periksa koneksi lalu muat kembali.';
}
export function ShipmentWorkspace() {
  const [identity] = useState(() => createIdentityGateway()), [access] = useState(() => createAccessGateway());
  const [account, setAccount] = useState<Account | null>(null), [loading, setLoading] = useState(true), [error, setError] = useState<unknown>(null), [revision, setRevision] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setLoading(true); setAccount(null); setError(null);
      try {
        const session = await identity.session(controller.signal), observedAt = performance.now(); if (!active) return;
        if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
        const current = await access.current(); if (!active) return;
        if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
        if (!current.allowedActions.some(action => ['shipment.read', 'delivery.attempt'].includes(action))) throw new AccessFailure(403, 'scope_denied');
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
  const denied = useCallback((failure: unknown) => {
    if (failure instanceof AccessFailure && (failure.status === 401 || failure.status === 403 || failure.code === 'actor_changed')) { setAccount(null); setError(failure); }
  }, []);
  const expired = account !== null && sessionTiming(account.session, elapsed).expired;
  return <main className={common.page}><PageHeader title="Kiriman" description="Periksa kemasan yang diberangkatkan, tujuan dan kurir dalam akses Anda." />
    <div className="ui-stack">{error !== null && account !== null && <StateView kind="error" title={message(error)} />}
    {loading ? <StateView kind="loading" title="Memeriksa akun…" /> : !account || expired ? <SessionAccessState kind={account ? "expired" : undefined} error={error} returnPath="/fulfillment/shipments" onRetry={() => setRevision(value => value + 1)} />
      : <ShipmentList key={account.actorId + ':' + account.observedAt} account={account} onDenied={denied} />}</div>
  </main>;
}
function ShipmentList({ account, onDenied }: { account: Account; onDenied: (failure: unknown) => void }) {
  const [gateway] = useState(() => createShipmentGateway());
  const [attempts] = useState(createDeliveryAttemptGateway), recovery = useOperationRecovery(account.actorId);
  const [recovering, setRecovering] = useState(false), [childBusy, setChildBusy] = useState(false), [mode, setMode] = useState<'receipt' | 'attempt'>('receipt');
  const [recoveredAttempt, setRecoveredAttempt] = useState<DeliveryAttempt | null>(null), recovered = useRef<DeliveryAttempt | null>(null);
  const canRead = account.actions.includes('shipment.read');
  const [page, setPage] = useState<{ items: Shipment[]; hasMore: boolean }>({ items: [], hasMore: false }), [before, setBefore] = useState<string | null>(null), [revision, setRevision] = useState(0);
  const [selected, setSelected] = useState<Shipment | null>(null), [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null), [offline, setOffline] = useState(false);
  const [documentLoadedFor, setDocumentLoadedFor] = useState<string | null>(null);
  const documentLoaded = useCallback(() => setDocumentLoadedFor(selected?.id ?? null), [selected?.id]);
  const alive = useRef(false), pending = useRef(false), detail = useRef<HTMLElement>(null), opener = useRef<HTMLButtonElement | null>(null);
  const current = useCallback(() => alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  useEffect(() => { alive.current = true; const connection = () => setOffline(!navigator.onLine); connection();
    window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => { alive.current = false; window.removeEventListener('online', connection); window.removeEventListener('offline', connection); }; }, []);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setBusy(true); setError(null); setSelected(null); setPage({ items: [], hasMore: false });
      try { if (canRead) { const next = await gateway.list(account.actorId, before, controller.signal); if (active && current()) setPage(next); } }
      catch (failure) { if (active) { setError(failure); onDenied(failure); } } finally { if (active) setBusy(false); }
    }); return () => { active = false; controller.abort(); };
  }, [gateway, account.actorId, before, revision, current, onDenied, canRead]);
  useEffect(() => { if (selected) detail.current?.focus(); }, [selected]);
  async function open(id: string, button: HTMLButtonElement) {
    if (!current() || busy || childBusy || recovering || pending.current || offline || !canRead) return; pending.current = true; opener.current = button; setBusy(true); setSelected(null); setDocumentLoadedFor(null); setMode('receipt'); setError(null);
    try { const item = await gateway.read(account.actorId, id); if (current()) setSelected(item); }
    catch (failure) { if (current()) { setError(failure); onDenied(failure); } } finally { pending.current = false; if (alive.current) setBusy(false); }
  }
  return <><AdministrationNavigation current="shipments" actions={account.actions} locked={busy || childBusy || recovering} />
    <OperationRecovery entries={recovery.entries.filter(entry => entry.operation === 'fulfillment.delivery-attempt.record')} journal={recovery.journal} disabled={busy || childBusy || offline} onBusyChange={setRecovering}
      recoveryAdapters={{ 'fulfillment.delivery-attempt': { lookup: async entry => { const result = await attempts.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); recovered.current = result?.result ?? null; return result; } } }}
      onFailure={failure => { setError(failure); onDenied(failure); }} onResolved={() => { recovery.refresh(); if (current()) { setRecoveredAttempt(recovered.current); setSelected(null); setDocumentLoadedFor(null); setRevision(value => value + 1); } recovered.current = null; }} />
    {recovery.error !== null && <StateView kind="error" title={accessMessage(recovery.error)} />}
    {recoveredAttempt && <p role="status">Upaya pengiriman tersimpan untuk kiriman {recoveredAttempt.shipmentId}: {deliveryOutcomes[recoveredAttempt.outcome]} · {visitTime(recoveredAttempt.occurredAt)} WIB.</p>}
    {offline && <StateView kind="offline" title="Koneksi terputus. Data yang tampil adalah hasil terakhir; muat kembali setelah tersambung." />}
    {error !== null && <StateView kind="error" title={message(error)} />}
    {!canRead && <p>Akses saat ini memungkinkan pemeriksaan hasil catatan kunjungan Anda. Daftar kiriman memerlukan akses baca kiriman yang masih berlaku.</p>}
    {canRead && <Section title="Barang yang diberangkatkan" label="Daftar kiriman" busy={busy}
      actions={<Button variant="secondary" disabled={busy || childBusy || recovering || offline} onClick={() => { setBefore(null); setRevision(value => value + 1); }}>Muat kiriman terbaru</Button>}>
      <DataList label="Daftar kiriman" items={page.items} getKey={item => item.id} loading={busy}
        empty={error === null ? { title: "Belum ada kiriman dalam akses Anda." } : undefined}
        renderTitle={item => item.buyerName} renderMeta={item => <>Kiriman {number(item)} · Pesanan {orderNumber(item.orderId)}</>}
        renderContent={item => <><p>{time(item.dispatchedAt)} WIB · Kurir saat keberangkatan: {item.courierName}</p><p>Kurir yang bertugas: {item.currentAssignment.courierName}</p>
          <p>{item.lines.map(line => `${line.productName} — ${line.packages} ${line.definition.packageLabel}`).join(', ')}</p></>}
        renderActions={item => <Button variant="secondary" disabled={busy || childBusy || recovering || offline} onClick={event => void open(item.id, event.currentTarget)}>Lihat kiriman {number(item)}</Button>} />
      {page.hasMore && <div className="ui-actions"><Button variant="secondary" disabled={busy || childBusy || recovering || offline} onClick={() => setBefore(page.items.at(-1)!.id)}>Kiriman lebih lama</Button></div>}
    </Section>}
    {selected && <section ref={detail} tabIndex={-1} className={"ui-section " + local.detail} aria-label="Rincian kiriman"><h2>Kiriman {number(selected)}</h2>
      <p>{selected.buyerName} · Pesanan {orderNumber(selected.orderId)}</p><p>Diberangkatkan {time(selected.dispatchedAt)} WIB · Kurir saat keberangkatan: {selected.courierName}</p><p>Kurir yang bertugas: {selected.currentAssignment.courierName} · sejak {time(selected.currentAssignment.effectiveAt)} WIB.</p>
      <h3>Tujuan</h3><p className={local.address}>{selected.delivery.recipientName} · {selected.delivery.phone}{'\n'}{selected.delivery.address}{'\n'}{selected.delivery.city} {selected.delivery.postalCode}</p>
      {selected.delivery.instructions && <p>Petunjuk: {selected.delivery.instructions}</p>}<h3>Kemasan yang diberangkatkan</h3>
      <ul className={local.packages}>{selected.lines.map(line => <li key={line.skuId}><strong>{line.productName}</strong> · {line.skuCode}<p>{line.packages} {line.definition.packageLabel} · isi {line.definition.baseUnitsPerPackage} {line.definition.baseUnitCode} per kemasan · total {line.baseUnits} {line.definition.baseUnitCode}</p></li>)}</ul>
      <Tabs label="Bagian kiriman" current={mode} disabled={busy || childBusy || recovering} items={[{ label: "Penerimaan dan dokumen", value: "receipt" }, { label: "Upaya pengiriman", value: "attempt" }]}
        onSelect={value => { const next = value === 'attempt' ? 'attempt' : 'receipt'; if (mode !== next) { setDocumentLoadedFor(null); setMode(next); } }} />
      {mode === 'attempt' ? <DeliveryAttemptPanel key={'attempt:' + selected.id + ':' + selected.currentAssignment.revision} actorId={account.actorId} shipmentId={selected.id} canRecord={account.actions.includes('delivery.attempt') && selected.currentAssignment.courierId === account.actorId} current={current} onDenied={onDenied} recovery={recovery} onBusyChange={setChildBusy} /> : <>
        <ShipmentDocumentPanel key={selected.id} actorId={account.actorId} shipment={selected} canIssue={account.actions.includes('shipment.prepare')} current={current} onDenied={onDenied} onLoaded={documentLoaded} />
        {documentLoadedFor === selected.id && <ShipmentReceiptPanel key={'receipt:' + selected.id + ':' + selected.currentAssignment.revision} actorId={account.actorId} shipmentId={selected.id} canIssueProof={account.actions.includes('shipment.prepare')} canChallenge={account.actions.includes('delivery.evidence') && selected.currentAssignment.courierId === account.actorId} current={current} onDenied={onDenied} />}
      </>}
      <div className="ui-actions"><Button variant="secondary" disabled={busy || childBusy || recovering} onClick={() => { setSelected(null); opener.current?.focus(); }}>Tutup rincian</Button></div>
    </section>}
  </>;
}
