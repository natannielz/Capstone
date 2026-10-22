'use client';

import Link from 'next/link';
import { useCallback, useEffect, useRef, useState } from 'react';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { AdministrationNavigation } from '../administration/AdministrationNavigation';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import { submitTrackedRequest } from '../administration/operation-journal';
import { createCaseGateway, prepareCase, type AfterSalesCase, type CaseResult, type CaseSource } from './case-client';
import { CaseDecisionPanel } from './CaseDecisionPanel';
import { CaseReturnPanel } from './CaseReturnPanel';
import { createReturnGateway } from './return-client';
import { createInspectionGateway } from './inspection-client';
import { createResolutionGateway } from './resolution-client';
import { createReturnLotGateway, returnLotOperation } from './return-lot-client';
import { createReturnEntitlementGateway, returnEntitlementOwner } from './return-entitlement-client';
import { canApproveReturnFinancial, createReturnFinancialApprovalGateway, returnFinancialApprovalOwner } from './return-financial-approval-client';
import { ReturnFinancialApprovalPanel } from './ReturnFinancialApprovalPanel';
import { RefundPayoutPanel } from './RefundPayoutPanel';
import { canRecordRefundPayout, createRefundPayoutGateway } from './refund-payout-client';
import { RefundSettlementPanel } from './RefundSettlementPanel';
import { canVerifyRefundSettlement, createRefundSettlementGateway } from './refund-settlement-client';
import common from '../administration/access.module.css';
import local from './fulfillment.module.css';
import { SessionAccessState } from "@/components/workspace-kit/session-access-state";
import { recordNumber } from '@/lib/document-numbers';
import { Button } from '@/components/workspace-kit/button';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { StateView } from '@/components/workspace-kit/state-view';
import { Section } from '@/components/workspace-kit/section';
import { DataList } from '@/components/workspace-kit/data-list';
import { Badge } from '@/components/workspace-kit/badge';

type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
const issues = { wrong_item: 'Barang tidak sesuai', damaged: 'Barang rusak', missing: 'Barang kurang', other: 'Masalah lainnya' };
const states = { requested: 'Menunggu pemeriksaan', return_authorized: 'Pengembalian diizinkan untuk pemeriksaan', rejected: 'Pengajuan ditolak' };
function message(failure: unknown) {
  if (failure instanceof AccessFailure && failure.code === 'invalid_after_sales_case') return 'Pilih barang yang bermasalah, isi jumlah satuan dasar sesuai barang yang diterima, dan jelaskan masalahnya.';
  if (failure instanceof AccessFailure && failure.code === 'after_sales_case_unavailable') return 'Pengajuan atau sumber penjualannya belum dapat diperiksa. Muat ulang dan hubungi penanggung jawab jika masalah berlanjut.';
  return accessMessage(failure);
}
export function CaseWorkspace() {
  const [identity] = useState(createIdentityGateway), [access] = useState(createAccessGateway);
  const [account, setAccount] = useState<Account | null>(null), [loading, setLoading] = useState(true), [error, setError] = useState<unknown>(null), [revision, setRevision] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => { let active = true; const controller = new AbortController(); void Promise.resolve().then(async () => {
    if (!active) return; setLoading(true); setAccount(null); setError(null);
    try {
      const session = await identity.session(controller.signal), observedAt = performance.now(); if (!active) return;
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
      const current = await access.current(); if (!active) return;
      if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
      if (!current.allowedActions.some(action => ['case.read', 'case.request'].includes(action))) throw new AccessFailure(403, 'scope_denied');
      if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
      setAccount({ actorId: current.userId, actions: current.allowedActions, session, observedAt }); setElapsed(performance.now() - observedAt);
    } catch (failure) { if (active) setError(failure); } finally { if (active) setLoading(false); }
  }); return () => { active = false; controller.abort(); }; }, [identity, access, revision]);
  useEffect(() => { const timer = window.setInterval(() => { if (account) setElapsed(performance.now() - account.observedAt); }, 1000);
    const restored = (event: PageTransitionEvent) => { if (event.persisted) { setAccount(null); setRevision(value => value + 1); } };
    window.addEventListener('pageshow', restored); return () => { window.clearInterval(timer); window.removeEventListener('pageshow', restored); }; }, [account]);
  const denied = useCallback((failure: unknown) => { if (failure instanceof AccessFailure && failure.code !== 'step_up_required' && (failure.status === 401 || failure.status === 403 || failure.code === 'actor_changed')) { setAccount(null); setError(failure); } }, []);
  return <main className={common.page}><PageHeader title="Keluhan barang" description="Laporkan masalah pada barang yang diterima. Penanggung jawab akan memeriksa pengajuan sebelum menentukan penyelesaiannya." />
    <div className="ui-stack">
    {error !== null && account !== null && <StateView kind="error" title={message(error)} />}
    {loading ? <StateView kind="loading" title="Memeriksa akun…" /> : !account || sessionTiming(account.session, elapsed).expired ? <SessionAccessState kind={account ? "expired" : undefined} error={error} returnPath="/fulfillment/cases" onRetry={() => setRevision(value => value + 1)} />
      : <CaseDesk key={account.actorId + ':' + account.observedAt} account={account} fresh={sessionTiming(account.session, elapsed).fresh} onDenied={denied} />}</div>
  </main>;
}
function CaseDesk({ account, fresh, onDenied }: { account: Account; fresh: boolean; onDenied: (failure: unknown) => void }) {
  const [gateway] = useState(createCaseGateway), [returns] = useState(createReturnGateway), [inspections] = useState(createInspectionGateway), [resolutions] = useState(createResolutionGateway), [returnLots] = useState(createReturnLotGateway), [entitlements] = useState(createReturnEntitlementGateway), [financialApprovals] = useState(createReturnFinancialApprovalGateway), recovery = useOperationRecovery(account.actorId);
  const [ledgers] = useState(() => new Map<string, MutationLedger>());
  const canRead = account.actions.includes('case.read'), canRequest = account.actions.includes('case.request'), canManage = account.actions.includes('case.manage');
  const canApproveFinancial = canApproveReturnFinancial(account.actions, 'credit') || canApproveReturnFinancial(account.actions, 'refund');
  const [payouts] = useState(createRefundPayoutGateway), canRecordPayout = canRecordRefundPayout(account.actions);
  const [settlements] = useState(createRefundSettlementGateway), canVerifyPayout = canVerifyRefundSettlement(account.actions);
  const [page, setPage] = useState<{ items: AfterSalesCase[]; hasMore: boolean }>({ items: [], hasMore: false }), [before, setBefore] = useState<string | null>(null), [refresh, setRefresh] = useState(0);
  const [invoiceId, setInvoiceId] = useState(''), [source, setSource] = useState<CaseSource | null>(null), [quantities, setQuantities] = useState<Record<string, string>>({});
  const [issue, setIssue] = useState('damaged'), [reason, setReason] = useState(''), [review, setReview] = useState<ReturnType<typeof prepareCase> | null>(null), [consent, setConsent] = useState(false);
  const [selected, setSelected] = useState<AfterSalesCase | null>(null), [result, setResult] = useState<CaseResult | null>(null), [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [offline, setOffline] = useState(false), [editing, setEditing] = useState(false);
  const alive = useRef(false), pending = useRef(false), heading = useRef<HTMLHeadingElement>(null), recovered = useRef<CaseResult | null>(null);
  const current = useCallback(() => alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const freshCurrent = useCallback(() => current() && sessionTiming(account.session, performance.now() - account.observedAt).fresh, [current, account]);
  const locked = busy || recovering || offline || recovery.locked || editing;
  useEffect(() => { alive.current = true; const connection = () => setOffline(!navigator.onLine); connection(); window.addEventListener('online', connection); window.addEventListener('offline', connection);
    void Promise.resolve().then(() => { if (alive.current) setInvoiceId(new URL(window.location.href).searchParams.get('invoice') ?? ''); });
    return () => { alive.current = false; window.removeEventListener('online', connection); window.removeEventListener('offline', connection); }; }, []);
  useEffect(() => { let active = true; const controller = new AbortController(); void Promise.resolve().then(async () => {
    if (!active || !canRead) return; setBusy(true); setPage({ items: [], hasMore: false });
    try { const value = await gateway.list(account.actorId, before, controller.signal); if (active && current()) setPage(value); }
    catch (failure) { if (active && current()) { setError(failure); onDenied(failure); } } finally { if (active) setBusy(false); }
  }); return () => { active = false; controller.abort(); }; }, [gateway, account.actorId, before, refresh, canRead, current, onDenied]);
  useEffect(() => { if (review || selected) heading.current?.focus(); }, [review, selected]);
  async function perform(work: () => Promise<void>) {
    if (!current() || busy || recovering || offline || editing || pending.current) return; pending.current = true; setBusy(true); setError(null);
    try { await work(); } catch (failure) { if (current()) { setReview(null); setError(failure); onDenied(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); recovery.refresh(); } }
  }
  async function load() {
    if (locked || !canRequest) return;
    await perform(async () => { setSource(null); setReview(null); setSelected(null); setResult(null); setQuantities({}); setReason('');
      const value = await gateway.source(account.actorId, invoiceId); if (current()) setSource(value); });
  }
  function preview() {
    if (locked || !canRequest || !source || !current()) return;
    try { const lines = source.lines.flatMap(line => { const quantity = quantities[line.receiptId + ':' + line.skuId]; return quantity ? [{ receiptId: line.receiptId, skuId: line.skuId, reportedBaseUnits: quantity }] : []; });
      setReview(prepareCase(account.actorId, source, issue, lines, reason.trim())); setConsent(false); setError(null); }
    catch (failure) { setError(failure); }
  }
  async function confirm() {
    if (locked || !canRequest || !review || !consent) return;
    await perform(async () => { if (recovery.journal.list(account.actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
      const ledger = ledgers.get(review.body.invoiceId) ?? new MutationLedger(() => 'ca.' + review.body.invoiceId + '.' + crypto.randomUUID()); ledgers.set(review.body.invoiceId, ledger);
      const saved = await submitTrackedRequest(ledger, recovery.journal, account.actorId, 'fulfillment.case.request', 'fulfillment.case.request', review, key => gateway.request(review, key, current));
      if (current()) { setResult(saved); setReview(null); setSource(null); setReason(''); setQuantities({}); setBefore(null); setRefresh(value => value + 1); } });
  }
  return <><AdministrationNavigation current="cases" actions={account.actions} locked={busy || recovering || editing} />
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy || offline || editing} onBusyChange={setRecovering}
      recoveryAdapters={canRequest || canManage || canApproveFinancial || canRecordPayout || canVerifyPayout ? { 'billing.refund.payout.verify': { lookup: async entry => {
        if (!canVerifyPayout || entry.actorId !== account.actorId) throw new AccessFailure(403, 'scope_denied');
        const value = await settlements.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return value;
      } }, 'billing.refund.payout': { lookup: async entry => {
        if (!canRecordPayout || entry.actorId !== account.actorId) throw new AccessFailure(403, 'scope_denied');
        const value = entry.operation === 'billing.refund.payout.draft' ? await payouts.recoverDraft(entry.actorId, entry.key) : await payouts.recoverRecord(entry.actorId, entry.key);
        if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return value;
      } }, 'evidence.refund-payout-image': { lookup: async entry => {
        if (!canRecordPayout || entry.actorId !== account.actorId) throw new AccessFailure(403, 'scope_denied');
        const value = await payouts.recoverReservation(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return value;
      } }, 'billing.return.financial.approve': { lookup: async entry => {
        const financial = returnFinancialApprovalOwner(entry.key);
        if (!canApproveReturnFinancial(account.actions, financial.action)) throw new AccessFailure(403, 'scope_denied');
        const value = await financialApprovals.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return value;
      } }, 'billing.return.entitlement.request': { lookup: async entry => {
        const financial = returnEntitlementOwner(entry.key);
        if (!canRead || !canManage || !account.actions.includes('invoice.read') || !account.actions.includes(financial.action + '.request')) throw new AccessFailure(403, 'scope_denied');
        const value = await entitlements.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return value;
      } }, 'fulfillment.case': { lookup: async entry => {
        if (entry.operation === returnLotOperation) { if (entry.actorId !== account.actorId || !canManage || !account.actions.includes('stock.count')) throw new AccessFailure(403, 'scope_denied'); const value = await returnLots.recover(entry.actorId, entry.key, freshCurrent); if (!freshCurrent()) throw new AccessFailure(409, 'recovery_request_stale'); return value; }
        if (entry.operation === 'fulfillment.case.resolution_request') { if (!canManage) throw new AccessFailure(403, 'scope_denied'); const value = await resolutions.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return value; }
        if (entry.operation === 'fulfillment.case.return_inspect') { if (!canManage || !account.actions.includes('stock.count')) throw new AccessFailure(403, 'scope_denied'); const value = await inspections.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return value; }
        if (entry.operation === 'fulfillment.case.return_receive') { if (!canManage || !account.actions.includes('stock.receive')) throw new AccessFailure(403, 'scope_denied'); const value = await returns.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return value; }
        if (entry.operation === 'fulfillment.case.decide') { if (!canManage) throw new AccessFailure(403, 'scope_denied'); const value = await gateway.recoverDecision(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return value; }
        if (!canRequest) throw new AccessFailure(403, 'scope_denied'); const value = await gateway.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); recovered.current = value?.result ?? null; return value;
      } } } : {}}
      onFailure={failure => { setError(failure); onDenied(failure); }} onResolved={() => { recovery.refresh(); ledgers.clear(); if (current()) { setResult(recovered.current); setSource(null); setReview(null); setSelected(null); setReason(''); setQuantities({}); setBefore(null); setRefresh(value => value + 1); } recovered.current = null; }} />
    {canManage && (!fresh || error instanceof AccessFailure && error.code === 'step_up_required') && <p role="status">Verifikasi dua langkah terbaru diperlukan untuk keputusan dan pemulihannya. <Link href="/identity/account?operation=case.manage&returnPath=%2Ffulfillment%2Fcases">Verifikasi dua langkah untuk keputusan</Link></p>}
    {recovery.error !== null && <StateView kind="error" title={message(recovery.error)} />}{error !== null && <StateView kind="error" title={message(error)} />}
    {offline && <StateView kind="offline" title="Koneksi terputus. Pengajuan belum dapat dikirim; isian hanya tersimpan di halaman ini." />}{busy && <StateView kind="loading" title="Memeriksa keluhan…" rows={2} />}
    {result && <p role="status">Pengajuan keluhan {recordNumber('case', result.caseId)} tercatat untuk invoice {result.invoiceId}. Buka rincian untuk memeriksa keputusan terbaru. Penyelesaian mengikuti pemeriksaan dan persetujuan yang berlaku.</p>}
    {canRequest && !source && !review && <Section title="Pilih penjualan" label="Sumber keluhan">
      {account.actions.includes('invoice.read') && <p>Buka invoice melalui <Link href="/billing/invoices">halaman tagihan</Link>, lalu pilih Laporkan masalah barang.</p>}
      <form onSubmit={event => { event.preventDefault(); void load(); }}><label>Nomor ID invoice<input required inputMode="numeric" pattern="[1-9][0-9]*" maxLength={19} disabled={locked} value={invoiceId} onChange={event => setInvoiceId(event.target.value)} /></label>
        <Button type="submit" variant="primary" disabled={locked}>Periksa barang pada invoice</Button></form></Section>}
    {source && !review && <Section title="Laporkan masalah barang" label="Pengajuan keluhan"><p>Invoice {source.number} · {source.buyerName} · {source.sellerName}</p>
      <form onSubmit={event => { event.preventDefault(); preview(); }}><label>Jenis masalah<select disabled={locked} value={issue} onChange={event => setIssue(event.target.value)}>{Object.entries(issues).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
        <p>Isi jumlah satuan dasar hanya untuk barang yang bermasalah. Kosongkan barang lainnya. Jumlah tidak boleh melebihi penerimaan pada invoice ini.</p>
        {source.lines.map(line => <fieldset className={local.count} key={line.receiptId + ':' + line.skuId} disabled={locked}><legend>{line.productName} · {line.skuCode}</legend>
          <p>{line.packageLabel}: {line.baseUnitsPerPackage} {line.baseUnitCode} per kemasan. Diterima: {line.acceptedBaseUnits} {line.baseUnitCode}. Bukti {line.receiptId}.</p>
          <label>Jumlah bermasalah — {line.productName} — bukti {line.receiptId}<input inputMode="numeric" pattern="[1-9][0-9]*" maxLength={19} value={quantities[line.receiptId + ':' + line.skuId] ?? ''} onChange={event => setQuantities(values => ({ ...values, [line.receiptId + ':' + line.skuId]: event.target.value }))} /></label></fieldset>)}
        <label>Jelaskan masalah<textarea required maxLength={1000} disabled={locked} value={reason} onChange={event => setReason(event.target.value)} /></label><Button type="submit" variant="primary" disabled={locked}>Tinjau keluhan</Button>
        <Button variant="secondary" disabled={locked} onClick={() => { setSource(null); setQuantities({}); setReason(''); }}>Ganti invoice</Button></form></Section>}
    {review && <section className="ui-section" aria-label="Tinjauan keluhan"><h2 ref={heading} tabIndex={-1}>Tinjau keluhan barang</h2><p>Invoice {review.source.number} · {issues[review.body.issue]}</p>
      <ul>{review.body.lines.map(line => { const item = review.source.lines.find(item => item.receiptId === line.receiptId && item.skuId === line.skuId)!; return <li key={line.receiptId + ':' + line.skuId}><strong>{item.productName}</strong><p>{line.reportedBaseUnits} {item.baseUnitCode} bermasalah dari {item.acceptedBaseUnits} {item.baseUnitCode} diterima. {item.packageLabel}: {item.baseUnitsPerPackage} {item.baseUnitCode} per kemasan. Bukti {line.receiptId}.</p></li>; })}</ul>
      <p>{review.body.reason}</p><label className={common.checkbox}><input type="checkbox" disabled={locked} checked={consent} onChange={event => setConsent(event.target.checked)} /><span>Saya telah memeriksa barang dan jumlah yang dilaporkan. Pengajuan ini akan diperiksa sebelum penyelesaian disetujui.</span></label>
      <div className="ui-actions"><Button variant="primary" disabled={locked || !consent} onClick={() => void confirm()}>Kirim keluhan</Button><Button variant="secondary" disabled={locked} onClick={() => setReview(null)}>Kembali ke isian</Button></div></section>}
    {selected && <section className="ui-section" aria-label="Rincian keluhan"><h2 ref={heading} tabIndex={-1}>Keluhan {recordNumber('case', selected.id)}</h2><p>{states[selected.state]} · {issues[selected.issue]} · Invoice {selected.source.number}</p><p>{selected.reportedProblem}</p>
      <ul>{selected.lines.map(line => { const item = selected.source.lines.find(item => item.receiptId === line.receiptId && item.skuId === line.skuId)!; return <li key={line.receiptId + ':' + line.skuId}>{item.productName}: {line.reportedBaseUnits} {item.baseUnitCode} bermasalah dari {item.acceptedBaseUnits} diterima. {item.packageLabel} berisi {item.baseUnitsPerPackage} {item.baseUnitCode}. Bukti {line.receiptId}.</li>; })}</ul>
      {selected.decision && <section aria-label="Keputusan keluhan"><h3>Keputusan dan penjelasan</h3><p>{selected.decision.publicExplanation}</p>
        <ul>{selected.decision.lines.map(line => { const item = selected.source.lines.find(item => item.receiptId === line.receiptId && item.skuId === line.skuId)!; return <li key={line.receiptId + ':' + line.skuId}>{item.productName}: {line.reportedBaseUnits} {item.baseUnitCode} diizinkan untuk pemeriksaan. Bukti {line.receiptId}.</li>; })}</ul>
        {selected.state === 'return_authorized' && <p>Barang yang kembali akan diperiksa kondisinya sebelum penyelesaian berikutnya.</p>}</section>}
      {canManage && selected.state === 'requested' && <CaseDecisionPanel key={selected.id + ':' + selected.revision} actorId={account.actorId} owner={selected} fresh={fresh} locked={locked} current={current}
        journal={recovery.journal} onDenied={onDenied} onBusyChange={setEditing} onSettled={recovery.refresh}
        onSaved={async saved => { const value = await gateway.read(account.actorId, saved.caseId, saved.revision); if (current()) { setSelected(value); setBefore(null); setRefresh(count => count + 1); } }} />}
      {selected.state === 'return_authorized' && <CaseReturnPanel key={'returns:' + selected.id} actorId={account.actorId} owner={selected} canReceive={canManage && account.actions.includes('stock.receive')} canInspect={canManage && account.actions.includes('stock.count')} freshCurrent={freshCurrent} fresh={fresh} locked={locked} current={current}
        resolutionOptions={canManage ? ['replacement', ...(account.actions.includes('credit.request') ? ['credit' as const] : []), ...(account.actions.includes('refund.request') ? ['refund' as const] : [])] : []} canReadInvoice={account.actions.includes('invoice.read')}
        journal={recovery.journal} onDenied={onDenied} onBusyChange={setEditing} onSettled={recovery.refresh} />}
      {selected.state === 'return_authorized' && canApproveFinancial && <ReturnFinancialApprovalPanel key={'financial:' + selected.id} actorId={account.actorId} owner={selected} actions={account.actions}
        fresh={fresh} locked={locked} current={current} freshCurrent={freshCurrent} journal={recovery.journal} onDenied={onDenied} onBusyChange={setEditing} onSettled={recovery.refresh} />}
      {selected.state === 'return_authorized' && canRecordPayout && <RefundPayoutPanel key={'payout:' + selected.id} actorId={account.actorId} owner={selected} actions={account.actions}
        fresh={fresh} locked={locked} current={current} freshCurrent={freshCurrent} journal={recovery.journal} onDenied={onDenied} onBusyChange={setEditing} onSettled={recovery.refresh} />}
      {selected.state === 'return_authorized' && canVerifyPayout && <RefundSettlementPanel key={'settlement:' + selected.id} actorId={account.actorId} owner={selected} actions={account.actions}
        fresh={fresh} locked={locked} current={current} freshCurrent={freshCurrent} journal={recovery.journal} onDenied={onDenied} onBusyChange={setEditing} onSettled={recovery.refresh} />}
      <div className="ui-actions"><Button variant="secondary" disabled={busy || recovering || editing} onClick={() => setSelected(null)}>Tutup keluhan</Button></div></section>}
    {canRead && <Section title="Pengajuan dalam akses Anda" label="Daftar keluhan"
      actions={<Button variant="secondary" disabled={busy || recovering || offline || editing || !!review} onClick={() => { setBefore(null); setRefresh(value => value + 1); }}>Muat keluhan terbaru</Button>}>
      <DataList label="Daftar keluhan" items={page.items} getKey={item => item.id} loading={busy}
        empty={error === null ? { title: "Belum ada pengajuan pada halaman ini." } : undefined}
        renderTitle={item => <>Keluhan {recordNumber('case', item.id)}</>} renderBadge={item => <Badge status={states[item.state]} />}
        renderMeta={item => <>{item.source.buyerName} · {item.source.number}</>}
        renderActions={item => <Button variant="secondary" disabled={busy || recovering || offline || editing || !!review} onClick={() => void perform(async () => { const value = await gateway.read(account.actorId, item.id, item.revision); if (current()) setSelected(value); })}>Buka keluhan {recordNumber('case', item.id)}</Button>} />
      {page.hasMore && <div className="ui-actions"><Button variant="secondary" disabled={busy || recovering || offline || editing || !!review} onClick={() => setBefore(page.items.at(-1)!.id)}>Keluhan lebih lama</Button></div>}</Section>}
  </>;
}
