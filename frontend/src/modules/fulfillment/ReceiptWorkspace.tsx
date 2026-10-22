'use client';

import { SessionAccessState, lostAccessKind } from '@/components/workspace-kit/session-access-state';
import { useCallback, useEffect, useRef, useState } from 'react';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import { submitTrackedRequest } from '../administration/operation-journal';
import { createReceiptGateway, parseReceiptLink, prepareReceipt, type Receipt, type ReceiptReview, type ReceiptDeclaration } from './receipt-client';
import { ReceiptSummary } from './ShipmentReceiptPanel';
import { ReceiptProofPanel } from './ReceiptProofPanel';
import { createReceiptProofGateway, type ReceiptProof } from './receipt-proof-client';
import common from '../administration/access.module.css';
import local from './fulfillment.module.css';
import { Button } from '@/components/workspace-kit/button';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { StateView } from '@/components/workspace-kit/state-view';

type Account = { actorId: string; session: IdentitySession; observedAt: number; canReceive: boolean };
type ReceiptLink = ReturnType<typeof parseReceiptLink>;
function message(failure: unknown) {
  if (failure instanceof AccessFailure && failure.code === 'fulfillment_source_obligation_unavailable')
    return 'Riwayat stok untuk kiriman ini belum dapat diverifikasi. Hubungi penanggung jawab toko. Jika konfirmasi penerimaan sudah dikirim, gunakan Periksa hasil sebelum mengirim ulang.';
  if (failure instanceof AccessFailure && ['stock_source_revision_changed', 'stock_pool_changed'].includes(failure.code))
    return 'Sumber stok untuk kiriman ini berubah. Muat kembali data penerimaan dan periksa jumlah barang sebelum mengonfirmasi. Jika konfirmasi sudah dikirim, gunakan Periksa hasil terlebih dahulu.';
  if (failure instanceof AccessFailure && ['shipment_receipt_unavailable', 'shipment_recipient_unavailable', 'independent_recipient_required'].includes(failure.code))
    return 'Penerimaan tidak dapat dibuka. Gunakan akun penerima yang dituju dan QR yang masih berlaku. Kurir dan penerima harus orang yang berbeda.';
  if (failure instanceof AccessFailure && failure.code === 'invalid_delivery_receipt') return 'Isi semua jumlah secara tepat, termasuk nol. Total diterima, ditolak, dan belum diterima harus sama dengan kiriman. Jelaskan setiap perbedaan.';
  if (failure instanceof AccessFailure && failure.code === 'invalid_receipt_link') return 'Tautan penerimaan tidak sesuai. Pindai kembali QR dari kurir.';
  return accessMessage(failure);
}
export function ReceiptWorkspace() {
  const [identity] = useState(() => createIdentityGateway()), [access] = useState(() => createAccessGateway());
  const [account, setAccount] = useState<Account | null>(null), [link, setLink] = useState<ReceiptLink | null>(null), [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(true), [revision, setRevision] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => {
    const fragment = window.location.hash;
    // Fragments never reach the API. Clear the address bar before any asynchronous account work.
    if (fragment) { window.history.replaceState(null, '', '/fulfillment/receive');
      void Promise.resolve().then(() => { try { setLink(parseReceiptLink(fragment)); } catch (failure) { setError(failure); } }); }
    const restored = (event: PageTransitionEvent) => { if (event.persisted) { setLink(null); setAccount(null); setRevision(value => value + 1); } };
    window.addEventListener('pageshow', restored); return () => window.removeEventListener('pageshow', restored);
  }, []);
  useEffect(() => { let active = true; const controller = new AbortController(); void Promise.resolve().then(async () => {
    if (!active) return; setLoading(true); setAccount(null);
    try { const session = await identity.session(controller.signal), observedAt = performance.now(); if (!active) return;
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
      const current = await access.current(); if (!active) return;
      if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
      const canReceive = current.allowedActions.includes('receipt.accept');
      if (!canReceive && !current.allowedActions.includes('shipment.prepare')) throw new AccessFailure(403, 'scope_denied');
      if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
      setAccount({ actorId: current.userId, session, observedAt, canReceive }); setElapsed(performance.now() - observedAt);
    } catch (failure) { if (active) setError(failure); } finally { if (active) setLoading(false); }
  }); return () => { active = false; controller.abort(); }; }, [identity, access, revision]);
  useEffect(() => { const timer = window.setInterval(() => { if (account) setElapsed(performance.now() - account.observedAt); }, 1000); return () => window.clearInterval(timer); }, [account]);
  // A session lost while the recipient was confirming is a session that ended, not a first sign-in (G-014).
  const [lost, setLost] = useState(false);
  const denied = useCallback((failure: unknown) => { if (failure instanceof AccessFailure && failure.status === 401) { setAccount(null); setLink(null); setError(failure); setLost(true); } }, []);
  const expired = account && sessionTiming(account.session, elapsed).expired;
  return <main className={common.page}><PageHeader title="Penerimaan kiriman" description="Periksa barang bersama kurir. Konfirmasi hanya dengan akun penerima yang dituju." />
    <div className="ui-stack">
    {error !== null && account && !expired && <StateView kind="error" title={message(error)} />}
    {loading ? <StateView kind="loading" title="Memeriksa akun penerima…" /> : !account || expired ? <>
      <SessionAccessState error={error} kind={expired ? "expired" : lost ? lostAccessKind(error) : undefined} returnPath="/fulfillment/receive" onRetry={() => { setError(null); setRevision(value => value + 1); }} detail={error !== null ? message(error) : undefined} />
      <p>Gunakan akun penerima sendiri. Setelah masuk, pindai kembali QR dari kurir; tautan QR tidak disimpan selama proses masuk.</p></>
      : <ReceiptForm key={account.actorId + ':' + account.observedAt} account={account} link={link} onDenied={denied} />}</div>
  </main>;
}
type Entry = { accepted: [string, string]; rejected: [string, string]; missing: [string, string]; exceptionReason: string };
const groups = ['accepted', 'rejected', 'missing'] as const;
const labels = { accepted: 'Diterima', rejected: 'Ditolak', missing: 'Belum diterima' };
function integer(value: string) { if (!/^(0|[1-9][0-9]{0,6})$(?![\s\S])/.test(value) || Number(value) > 1000000) throw new AccessFailure(400, 'invalid_delivery_receipt'); return Number(value); }
function ReceiptForm({ account, link, onDenied }: { account: Account; link: ReceiptLink | null; onDenied: (failure: unknown) => void }) {
  const [gateway] = useState(() => createReceiptGateway()), [ledger] = useState(() => new MutationLedger()), recovery = useOperationRecovery(account.actorId);
  const [review, setReview] = useState<ReceiptReview | null>(null), [observedAt, setObservedAt] = useState(0), [elapsed, setElapsed] = useState(0);
  const [entries, setEntries] = useState<Record<string, Entry>>({}), [reason, setReason] = useState(''), [consent, setConsent] = useState(false);
  const [command, setCommand] = useState<ReturnType<typeof prepareReceipt> | null>(null), [receipt, setReceipt] = useState<Receipt | null>(null);
  const [proofs] = useState(createReceiptProofGateway), [recoveredProof, setRecoveredProof] = useState<ReceiptProof | null>(null);
  const [error, setError] = useState<unknown>(null), [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [offline, setOffline] = useState(false);
  const alive = useRef(false), pending = useRef(false), heading = useRef<HTMLHeadingElement>(null);
  const firstQuantity = useRef<HTMLInputElement>(null), returningToQuantities = useRef(false);
  const current = useCallback(() => alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const expired = !!review && elapsed >= Math.max(0, Date.parse(review.expiresAt) - Date.parse(review.serverTime));
  const locked = busy || recovering || recovery.locked || offline || expired;
  useEffect(() => { alive.current = true; const connection = () => setOffline(!navigator.onLine); connection(); window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => { alive.current = false; window.removeEventListener('online', connection); window.removeEventListener('offline', connection); }; }, []);
  useEffect(() => { const timer = window.setInterval(() => setElapsed(performance.now() - observedAt), 1000); return () => window.clearInterval(timer); }, [observedAt]);
  useEffect(() => { if (command) heading.current?.focus();
    else if (returningToQuantities.current) { returningToQuantities.current = false; firstQuantity.current?.focus(); } }, [command]);
  function failed(failure: unknown) { setError(failure); onDenied(failure); }
  async function load() {
    if (!account.canReceive || !link || !current() || busy || pending.current || offline || recovery.locked) return; pending.current = true; setBusy(true); setError(null); setCommand(null);
    try { const result = await gateway.review(account.actorId, link.challengeId, link.token); if (!current()) return;
      setReview(result); setObservedAt(performance.now()); setElapsed(0); setEntries(Object.fromEntries(result.lines.map(line => [line.skuId, { accepted: ['', ''], rejected: ['', ''], missing: ['', ''], exceptionReason: '' }]))); setConsent(false);
    } catch (failure) { if (current()) { setReview(null); failed(failure); } } finally { pending.current = false; if (alive.current) setBusy(false); }
  }
  function preview() {
    if (!link || !review || locked || !current()) return;
    try { const lines: ReceiptDeclaration[] = review.lines.map(line => { const value = entries[line.skuId]; const amount = (group: typeof groups[number]) => ({ packages: integer(value[group][0]), looseUnits: integer(value[group][1]) });
      return { skuId: line.skuId, lineRevision: line.lineRevision, accepted: amount('accepted'), rejected: amount('rejected'), missing: amount('missing'), exceptionReason: value.exceptionReason || null }; });
      setCommand(prepareReceipt(account.actorId, review, link.token, lines, true, reason)); setConsent(false); setError(null);
    } catch (failure) { setError(failure); }
  }
  async function confirm() {
    if (!command || !consent || locked || !current() || pending.current) return; pending.current = true; setBusy(true); setError(null);
    try { if (recovery.journal.list(account.actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
      const result = await submitTrackedRequest(ledger, recovery.journal, account.actorId, 'fulfillment.receipt.confirm', 'fulfillment.receipt.confirm', command,
        key => gateway.confirm(command, key, current)); if (current()) { setReceipt(result); setCommand(null); setEntries({}); setReview(null); }
    } catch (failure) { if (current()) failed(failure); } finally { pending.current = false; if (alive.current) { setBusy(false); recovery.refresh(); } }
  }
  function change(sku: string, group: typeof groups[number], position: 0 | 1, value: string) {
    setEntries(previous => { const next: [string, string] = [...previous[sku][group]]; next[position] = value; return { ...previous, [sku]: { ...previous[sku], [group]: next } }; });
  }
  return <section className="ui-section" aria-label="Konfirmasi penerimaan" aria-busy={busy}>
    <OperationRecovery entries={recovery.entries.filter(entry => entry.operation !== 'fulfillment.receipt-proof.issue' || !receipt && !recoveredProof)} journal={recovery.journal} disabled={busy || offline} onBusyChange={setRecovering}
      recoveryAdapters={{ 'fulfillment.receipt-proof': { lookup: async entry => { const result = await proofs.recover(entry.actorId, entry.key); if (current() && result) setRecoveredProof(result.result); return result; } },
        'fulfillment.receipt': { lookup: async entry => { const result = await gateway.recover(entry.actorId, entry.key, entry.operation === 'fulfillment.receipt.challenge.issue'); if (current() && result && 'recordedAt' in result.result) setReceipt(result.result); return result; } } }}
      onFailure={failed} onResolved={() => { recovery.refresh(); ledger.resetAfterReconciliation(); setCommand(null); setEntries({}); setReview(null); }} />
    {error !== null && <StateView kind="error" title={message(error)} />}{recovery.error !== null && <StateView kind="error" title={message(recovery.error)} />}
    {offline && <StateView kind="offline" title="Koneksi terputus. Isian hanya ada di halaman ini dan belum dikirim. Jangan tutup halaman sebelum tersambung." />}
    {expired && !receipt && <p role="status">QR kedaluwarsa. Pindai QR baru sebelum mengirim penerimaan.</p>}{busy && <StateView kind="loading" title="Memeriksa kiriman…" rows={2} />}
    {receipt || recoveredProof ? <>{receipt && <ReceiptSummary receipt={receipt} />}<ReceiptProofPanel actorId={account.actorId} shipmentId={receipt?.shipmentId ?? recoveredProof!.shipmentId} receiptId={receipt?.id ?? recoveredProof!.receiptId}
      asRecipient={receipt ? true : recoveredProof!.issuedAsRecipient} canIssue={!!receipt} current={current} onDenied={failed} /></> : !review ? (!account.canReceive ? <p>Gunakan Periksa hasil untuk memulihkan bukti penerimaan yang telah Anda terbitkan.</p>
      : link ? <><p>Buka rincian kiriman sebelum menghitung barang.</p>
        <div className="ui-actions"><Button variant="primary" disabled={busy || offline || recovery.locked} onClick={() => void load()}>Periksa kiriman</Button></div></>
      : <StateView kind="empty" title="Pindai QR dari kurir" message="Jika konfirmasi sebelumnya terputus, gunakan Periksa hasil di atas." />)
      : command ? <div className={local.inset}><h2 ref={heading} tabIndex={-1}>Tinjau jumlah penerimaan</h2><p>{review.recipientName} · Kiriman {review.shipmentId} · Surat Jalan versi {review.documentVersion}</p>
        <ul>{command.body.lines.map(line => { const source = review.lines.find(item => item.skuId === line.skuId)!; return <li key={line.skuId}><strong>{source.productName}</strong>
          {groups.map(group => <p key={group}>{labels[group]}: {line[group].packages} {source.packageLabel} + {line[group].looseUnits} {source.baseUnitCode}</p>)}{line.exceptionReason && <p>{line.exceptionReason}</p>}</li>; })}</ul>
        <p>Catatan: {command.body.reason}</p><label className={common.checkbox}><input type="checkbox" checked={consent} disabled={locked} onChange={event => setConsent(event.target.checked)} /><span>{review.consentText}</span></label>
        <div className="ui-actions"><Button variant="primary" disabled={locked || !consent} onClick={() => void confirm()}>Konfirmasi penerimaan</Button>
        <Button variant="secondary" disabled={locked} onClick={() => { returningToQuantities.current = true; setCommand(null); setConsent(false); }}>Kembali ke jumlah</Button></div></div>
      : <form onSubmit={event => { event.preventDefault(); preview(); }}><h2>Hitung barang yang tiba</h2><p>Penerima: {review.recipientName}. Isi jumlah kemasan utuh dan satuan lepas; masukkan nol bila tidak ada.</p>
        {review.lines.map(line => <fieldset key={line.skuId} className={local.count} disabled={locked}><legend>{line.productName} · {line.skuCode}</legend><p>Dikirim {line.packages} {line.packageLabel}. Isi {line.baseUnitsPerPackage} {line.baseUnitCode} per kemasan.</p>
          {groups.map(group => <div key={group}><h3>{labels[group]}</h3><label>{labels[group]} — kemasan utuh<input ref={group === 'accepted' && line.skuId === review.lines[0].skuId ? firstQuantity : undefined} inputMode="numeric" required pattern="[0-9]+" maxLength={7} value={entries[line.skuId][group][0]} onChange={event => change(line.skuId, group, 0, event.target.value)} /></label>
            <label>{labels[group]} — satuan lepas<input inputMode="numeric" required pattern="[0-9]+" maxLength={7} value={entries[line.skuId][group][1]} onChange={event => change(line.skuId, group, 1, event.target.value)} /></label></div>)}
          <label>Alasan perbedaan<textarea aria-label="Alasan perbedaan" maxLength={1000} value={entries[line.skuId].exceptionReason} onChange={event => setEntries(previous => ({ ...previous, [line.skuId]: { ...previous[line.skuId], exceptionReason: event.target.value } }))} /></label></fieldset>)}
        <label>Catatan penerimaan<input required maxLength={1000} disabled={locked} value={reason} onChange={event => setReason(event.target.value)} /></label>
        <Button type="submit" variant="primary" disabled={locked}>Tinjau jumlah</Button></form>}
  </section>;
}
