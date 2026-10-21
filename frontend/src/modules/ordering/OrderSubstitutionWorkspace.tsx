'use client';

import Link from 'next/link';
import { useCallback, useEffect, useId, useRef, useState } from 'react';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AdministrationNavigation } from '../administration/AdministrationNavigation';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import { submitTrackedRequest } from '../administration/operation-journal';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { orderAmount, orderDisplayAmount, orderQuantity } from './order-quote-client';
import { createOrderSubstitutionGateway, replacementSelection, substitutionOperation, substitutionLineKey, type SubstitutionDecision,
  type SubstitutionDecisionReview, type SubstitutionHistory, type SubstitutionLine, type SubstitutionProposalReview,
  type SubstitutionResult, type SubstitutionTarget } from './order-substitution-client';
import styles from '../administration/access.module.css';
import { formatDateTime } from '@/lib/format';
import { SessionAccessState } from "@/components/workspace-kit/session-access-state";
import { orderNumber } from '@/lib/document-numbers';

type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
type Page<T> = { items: T[]; hasMore: boolean };
function message(error: unknown) {
  if (error instanceof AccessFailure) {
    if (error.status === 409) return 'Pesanan, stok, kemasan, harga, atau akses berubah. Muat ulang dan tinjau pengajuan terbaru sebelum melanjutkan.';
    if (error.status === 502) return 'Rincian penggantian belum dapat dipastikan. Periksa hasil penyimpanan sebelum membuat perubahan baru.';
    if (error.status === 503 || error.status === 0) return 'Hasil belum dapat dipastikan. Gunakan Periksa hasil; jangan mengirim pengajuan pengganti.';
  }
  return accessMessage(error);
}
const label = (value: SubstitutionLine) => `${value.familyName} · ${value.definition.packageLabel} (${value.definition.baseUnitsPerPackage} ${value.definition.baseUnitCode})`;
const statusLabel: Record<SubstitutionHistory['status'], string> = { pending: 'Menunggu pembeli', accepted: 'Disetujui pembeli', declined: 'Ditolak pembeli', stale: 'Perlu pengajuan baru' };

export function OrderSubstitutionWorkspace({ orderId = null }: { orderId?: string | null }) {
  const [identity] = useState(() => createIdentityGateway()), [access] = useState(() => createAccessGateway());
  const [account, setAccount] = useState<Account | null>(null), [loading, setLoading] = useState(true), [error, setError] = useState<unknown>(null), [revision, setRevision] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setAccount(null); setLoading(true); setError(null);
      try {
        const session = await identity.session(controller.signal), observedAt = performance.now();
        if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
        const current = await access.current(); if (!active) return;
        if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
        if (!current.allowedActions.some(action => ['substitution.propose', 'substitution.accept'].includes(action))) throw new AccessFailure(403, 'scope_denied');
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
  const failed = useCallback((failure: unknown) => {
    if (failure instanceof AccessFailure && (failure.status === 401 || failure.status === 403 || failure.code === 'actor_changed')) { setAccount(null); setError(failure); }
  }, []);
  return <main className={styles.page}><header className={styles.header}><div><Link href="/identity/account" className={styles.brand}>Unit Toko</Link>
    <h1>Penggantian barang pesanan</h1><p>Petugas mengajukan pilihan pengganti. Pembeli memeriksa kemasan, jumlah, dan harga sebelum menyetujuinya.</p></div>
    <Link href="/ordering/orders" className={styles.secondary}>Riwayat pesanan</Link></header>
    {error !== null && account !== null && <p role="alert" className={styles.alert}>{message(error)}</p>}
    {loading ? <p role="status">Memeriksa akun…</p> : !account || sessionTiming(account.session, elapsed).expired ? <SessionAccessState error={error} returnPath="/ordering/substitutions" onRetry={() => setRevision(value => value + 1)} />
      : <SubstitutionDesk key={account.actorId + ':' + account.observedAt} account={account} orderId={orderId} onFailure={failed} />}
  </main>;
}

function SubstitutionDesk({ account, orderId, onFailure }: { account: Account; orderId: string | null; onFailure: (error: unknown) => void }) {
  const { actorId, actions } = account, [gateway] = useState(() => createOrderSubstitutionGateway()), [ledger] = useState(() => new MutationLedger());
  const [forBuyer, setForBuyer] = useState(() => actions.includes('substitution.accept'));
  // The buyer's decision view is a buyer screen (whole rupiah without ",00"); the staff proposal view keeps exact amounts.
  const amount = forBuyer ? orderDisplayAmount : orderAmount;
  const [history, setHistory] = useState<Page<SubstitutionHistory>>({ items: [], hasMore: false }), [targets, setTargets] = useState<Page<SubstitutionTarget>>({ items: [], hasMore: false });
  const [historyBefore, setHistoryBefore] = useState<string | null>(null), [targetBefore, setTargetBefore] = useState<string | null>(null), [reload, setReload] = useState(0);
  const [target, setTarget] = useState<SubstitutionTarget | null>(null), [originalSku, setOriginalSku] = useState(''), [originalPackages, setOriginalPackages] = useState('');
  const [packages, setPackages] = useState<Page<SubstitutionLine>>({ items: [], hasMore: false }), [search, setSearch] = useState(''), [searchUsed, setSearchUsed] = useState('');
  const [replacement, setReplacement] = useState<SubstitutionLine | null>(null), [replacementPackages, setReplacementPackages] = useState('');
  const [proposalReview, setProposalReview] = useState<SubstitutionProposalReview | null>(null), [decisionReview, setDecisionReview] = useState<SubstitutionDecisionReview | null>(null);
  const [reason, setReason] = useState(''), [confirmed, setConfirmed] = useState(false), [notice, setNotice] = useState(''), [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false), [reading, setReading] = useState(false), [recovering, setRecovering] = useState(false);
  const recovery = useOperationRecovery(actorId), alive = useRef(false), pending = useRef(false), epoch = useRef(0), recovered = useRef<SubstitutionResult | null>(null), panel = useRef<HTMLElement>(null);
  const current = () => alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired;
  const locked = busy || reading || recovering || recovery.locked;
  // A control wrapped in its label may take its own value into its accessible name; name each exactly.
  const field = useId(), name = (part: string) => field + '-' + part;
  useEffect(() => { alive.current = true; return () => { alive.current = false; epoch.current++; }; }, []);
  const [expired, setExpired] = useState(false);
  useEffect(() => {
    const timer = window.setInterval(() => { if (sessionTiming(account.session, performance.now() - account.observedAt).expired) setExpired(true); }, 250);
    return () => window.clearInterval(timer);
  }, [account]);
  const clearReview = () => { epoch.current++; setProposalReview(null); setDecisionReview(null); setConfirmed(false); setReason(''); };
  const resetSelection = () => { clearReview(); setTarget(null); setOriginalSku(''); setOriginalPackages(''); setReplacement(null); setReplacementPackages(''); setPackages({ items: [], hasMore: false }); setSearch(''); setSearchUsed(''); };
  useEffect(() => {
    let active = true; const controller = new AbortController(), captured = ++epoch.current;
    void Promise.resolve().then(async () => {
      if (!active) return; setReading(true); setError(null); setHistory({ items: [], hasMore: false }); setTargets({ items: [], hasMore: false });
      setTarget(null); setReplacement(null); setProposalReview(null); setDecisionReview(null); setConfirmed(false); setReason('');
      try {
        const values = await gateway.history(actorId, forBuyer, orderId, historyBefore, controller.signal);
        const available = forBuyer ? { items: [], hasMore: false } : await gateway.targets(actorId, targetBefore, controller.signal);
        if (active && captured === epoch.current && current()) { setHistory(values); setTargets(available); }
      } catch (failure) { if (active && captured === epoch.current) { setError(failure); onFailure(failure); } }
      finally { if (active) setReading(false); }
    }); return () => { active = false; controller.abort(); };
    // Account changes remount this desk; current() intentionally reads its live session deadline.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [gateway, actorId, forBuyer, orderId, historyBefore, targetBefore, reload, onFailure]);
  async function read<T>(action: () => Promise<T>, apply: (value: T) => void) {
    if (!current() || pending.current || locked) return;
    const captured = ++epoch.current; pending.current = true; setBusy(true); setError(null); setNotice('');
    setProposalReview(null); setDecisionReview(null); setConfirmed(false); setReason('');
    try { const value = await action(); if (current() && captured === epoch.current) apply(value); }
    catch (failure) { if (current() && captured === epoch.current) { setError(failure); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) setBusy(false); }
  }
  const saved = (value: SubstitutionResult) => {
    resetSelection(); setHistoryBefore(null); setTargetBefore(null); setReload(value => value + 1);
    setNotice(value.status === 'proposed' ? 'Pengajuan tersimpan. Pembeli perlu meninjau dan memutuskan penggantian ini.' : value.status === 'declined' ? 'Penggantian ditolak. Pesanan dan cadangan barang tetap seperti sebelumnya.' : 'Penggantian disetujui dan cadangan barang diperbarui. Persetujuan belanja serta penerimaan toko diperiksa kembali sebelum penyiapan kiriman.');
  };
  async function submit() {
    if (!current() || pending.current || locked || !confirmed || !reason.trim() || (!proposalReview && !decisionReview)) return;
    const captured = epoch.current, valid = () => current() && epoch.current === captured;
    pending.current = true; setBusy(true); setError(null);
    try {
      if (recovery.journal.list(actorId).length > 0) throw new AccessFailure(400, 'resolve_previous_attempt');
      const operation = 'ordering.substitution.' + (proposalReview ? 'propose' : decisionReview!.decision === 'Accept' ? 'accept' : 'decline');
      const result = await submitTrackedRequest(ledger, recovery.journal, actorId, operation, operation, { review: proposalReview ?? decisionReview, reason: reason.trim() },
        key => proposalReview ? gateway.propose(actorId, proposalReview, reason.trim(), key, valid) : gateway.decide(actorId, decisionReview!, reason.trim(), key, valid));
      if (valid()) saved(result);
    } catch (failure) { if (valid()) { setError(failure); onFailure(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); recovery.refresh(); } }
  }
  async function findPackages(afterId: string | null = null) {
    if (!target) return; const chosen = target, query = afterId === null ? search : searchUsed;
    await read(() => gateway.packages(actorId, chosen, query, afterId), value => { setPackages(value); setSearchUsed(query); setReplacement(null); setReplacementPackages(''); });
  }
  async function previewProposal() {
    if (!target || !replacement) return;
    try {
      const original = target.lines.find(value => substitutionLineKey(value.line) === originalSku);
      const quantity = orderQuantity(originalPackages, 1, 1);
      if (!original || quantity > original.remainingPackages) throw new AccessFailure(400, 'order_package_quantity_invalid');
      const input = { orderId: target.orderId, suborderId: target.suborderId, expectedOrderRevision: target.revision, expectedDispatchSequence: target.dispatchSequence,
        originalSkuId: original.line.definition.skuId, originalLineRevision: original.line.lineRevision, originalPackages: quantity, replacement: replacementSelection(replacement, orderQuantity(replacementPackages, replacement.definition.minimumOrderPackages, replacement.definition.orderIncrementPackages)) };
      await read(() => gateway.preview(actorId, input), value => { setProposalReview(value); requestAnimationFrame(() => panel.current?.focus()); });
    } catch (failure) { setError(failure); }
  }
  async function reviewDecision(item: SubstitutionHistory, decision: SubstitutionDecision) {
    await read(() => gateway.review(actorId, item, decision), value => { setDecisionReview(value); requestAnimationFrame(() => panel.current?.focus()); });
  }
  return <>
    <AdministrationNavigation current="substitutions" actions={actions} locked={locked} />
    <div className={styles.actions}>
      {actions.includes('substitution.accept') && actions.includes('substitution.propose') && <><button className={styles.secondary} disabled={locked || forBuyer} onClick={() => { resetSelection(); setHistoryBefore(null); setTargetBefore(null); setForBuyer(true); }}>Keputusan pembeli</button>
        <button className={styles.secondary} disabled={locked || !forBuyer} onClick={() => { resetSelection(); setHistoryBefore(null); setTargetBefore(null); setForBuyer(false); }}>Pengajuan petugas</button></>}
      <button className={styles.secondary} disabled={locked} onClick={() => { resetSelection(); setHistoryBefore(null); setTargetBefore(null); setReload(value => value + 1); }}>Muat ulang penggantian</button>
    </div>
    {notice && <p role="status" className={styles.help}>{notice}</p>}
    {error !== null && <p role="alert" className={styles.alert}>{message(error)}</p>}
    {recovery.error !== null && <p role="alert" className={styles.alert}>{accessMessage(recovery.error)}</p>}
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy || reading || expired} onBusyChange={setRecovering} onFailure={onFailure}
      recoveryAdapters={{ 'ordering.substitution': { lookup: async entry => { const result = await gateway.recover(entry.actorId, substitutionOperation(entry.operation), entry.key, current); recovered.current = result?.result ?? null; return result; } } }}
      onResolved={() => { recovery.refresh(); ledger.resetAfterReconciliation(); setError(null); if (current() && recovered.current) saved(recovered.current); recovered.current = null; }} />
    {reading && <p role="status">Memuat pesanan dan pengajuan…</p>}
    {!forBuyer && <section className={styles.accountSection}><h2>Ajukan barang pengganti</h2><p>Pilih sisa barang yang belum dikirim. Pengajuan ini belum mengubah pesanan atau cadangan barang.</p>
      {!reading && targets.items.length === 0 && <p>Belum ada pesanan dalam penugasan Anda.</p>}
      <ul className={styles.recoveryList}>{targets.items.map(value => <li key={value.suborderId}><div><strong>{value.buyerName}</strong><p>Pesanan {orderNumber(value.orderId)} · {value.lines.filter(line => line.remainingPackages > 0).map(line => `${line.line.familyName}: ${line.remainingPackages} ${line.line.definition.packageLabel}`).join(', ') || 'Tidak ada sisa barang'}</p></div>
        <button className={styles.secondary} disabled={locked || !value.canPropose} onClick={() => { resetSelection(); setTarget(value); }}>Pilih pesanan {orderNumber(value.orderId)}</button></li>)}</ul>
      {targets.hasMore && <button className={styles.secondary} disabled={locked} onClick={() => { resetSelection(); setTargetBefore(targets.items.at(-1)!.suborderId); }}>Pesanan berikutnya</button>}
      {target && <div className={styles.review}><h3>{target.buyerName} · Pesanan {orderNumber(target.orderId)}</h3>
        <label><span id={name('original')}>Barang asal</span><select aria-labelledby={name('original')} disabled={locked} value={originalSku} onChange={event => { clearReview(); setOriginalSku(event.target.value); setOriginalPackages(''); }}><option value="">Pilih barang yang belum dikirim</option>
          {target.lines.filter(value => value.remainingPackages > 0).map(value => <option key={substitutionLineKey(value.line)} value={substitutionLineKey(value.line)}>{label(value.line)} · {amount(value.line.price.currency, value.line.price.unitPrice)} · {value.line.lineRevision === '0' ? 'pesanan awal' : `persetujuan ke-${value.line.lineRevision}`} · sisa {value.remainingPackages}</option>)}</select></label>
        <label><span id={name('original-packages')}>Jumlah kemasan asal yang diganti</span><input aria-labelledby={name('original-packages')} disabled={locked} inputMode="numeric" value={originalPackages} onChange={event => { clearReview(); setOriginalPackages(event.target.value); }} /></label>
        <form onSubmit={event => { event.preventDefault(); void findPackages(); }}><label><span id={name('search')}>Cari nama barang pengganti</span><input aria-labelledby={name('search')} disabled={locked} value={search} maxLength={100} onChange={event => { clearReview(); setSearch(event.target.value); }} /></label>
          <button className={styles.secondary} disabled={locked}>Cari kemasan pengganti</button></form>
        <ul className={styles.recoveryList}>{packages.items.map(value => <li key={value.definition.skuId}><div><strong>{label(value)}</strong><p>{amount(value.price.currency, value.price.unitPrice)} per kemasan · minimum {value.definition.minimumOrderPackages}, kelipatan {value.definition.orderIncrementPackages}</p></div>
          <button className={styles.secondary} disabled={locked} onClick={() => { clearReview(); setReplacement(value); setReplacementPackages(String(value.definition.minimumOrderPackages)); }}>Pilih {value.definition.packageLabel}</button></li>)}</ul>
        {packages.hasMore && <button className={styles.secondary} disabled={locked} onClick={() => void findPackages(packages.items.at(-1)!.definition.skuId)}>Kemasan berikutnya</button>}
        {replacement && <><p><strong>Pengganti: {label(replacement)}</strong></p><label><span id={name('replacement-packages')}>Jumlah kemasan pengganti</span><input aria-labelledby={name('replacement-packages')} disabled={locked} inputMode="numeric" value={replacementPackages} onChange={event => { clearReview(); setReplacementPackages(event.target.value); }} /></label>
          <button className={styles.primary} disabled={locked || !originalSku || !originalPackages || !replacementPackages} onClick={() => void previewProposal()}>Tinjau pengajuan penggantian</button></>}
      </div>}
    </section>}
    <section className={styles.accountSection}><h2>{forBuyer ? 'Pengajuan untuk pembeli' : 'Riwayat pengajuan'}</h2>
      {orderId && <p>Penggantian terkait pesanan {orderNumber(orderId)}. Rincian pesanan awal dan kiriman sebelumnya tetap tersimpan.</p>}
      {!reading && history.items.length === 0 && <p>Belum ada pengajuan penggantian.</p>}
      <ul className={styles.recoveryList}>{history.items.map(value => <li key={value.proposalId}><div><strong>{value.buyerName} · Pesanan {orderNumber(value.orderId)}</strong>
        <p>{value.input.originalPackages} × {label(value.original)} → {value.replacement.packages} × {label(value.replacement)}</p>
        <p>Harga pengganti {amount(value.replacement.price.currency, value.replacement.price.unitPrice)} per kemasan · subtotal {amount(value.replacement.price.currency, value.replacement.catalogSubtotal)}</p>
        <p>{statusLabel[value.status]} · diajukan {formatDateTime(value.proposedAt)}{value.decidedAt ? ` · diputuskan ${formatDateTime(value.decidedAt)}` : ''}</p></div>
        {forBuyer && value.status === 'pending' && <div className={styles.actions}><button className={styles.primary} disabled={locked} onClick={() => void reviewDecision(value, 'Accept')}>Tinjau persetujuan</button><button className={styles.secondary} disabled={locked} onClick={() => void reviewDecision(value, 'Decline')}>Tinjau penolakan</button></div>}</li>)}</ul>
      {history.hasMore && <button className={styles.secondary} disabled={locked} onClick={() => { clearReview(); setHistoryBefore(history.items.at(-1)!.proposalId); }}>Pengajuan sebelumnya</button>}
    </section>
    {(proposalReview || decisionReview) && <section ref={panel} tabIndex={-1} className={styles.review} aria-labelledby="substitution-review-heading"><h2 id="substitution-review-heading">{proposalReview ? 'Konfirmasi pengajuan penggantian' : decisionReview!.decision === 'Accept' ? 'Konfirmasi persetujuan pembeli' : 'Konfirmasi penolakan pembeli'}</h2>
      <p>Pesanan {orderNumber((proposalReview?.input.orderId ?? decisionReview!.orderId))}</p>
      <p><strong>Barang asal:</strong> {proposalReview?.input.originalPackages ?? decisionReview!.originalPackages} × {label((proposalReview ?? decisionReview)!.original)} · {amount((proposalReview ?? decisionReview)!.original.price.currency, (proposalReview ?? decisionReview)!.original.price.unitPrice)} per kemasan</p>
      <p><strong>Barang pengganti:</strong> {(proposalReview ?? decisionReview)!.replacement.packages} × {label((proposalReview ?? decisionReview)!.replacement)} · {amount((proposalReview ?? decisionReview)!.replacement.price.currency, (proposalReview ?? decisionReview)!.replacement.price.unitPrice)} per kemasan</p>
      {proposalReview ? <p>Pembeli akan meninjau ulang ketersediaan, harga, pajak, biaya, dan total pesanan sebelum memberikan persetujuan.</p> : <>
        <h3>{decisionReview!.decision === 'Accept' ? 'Rincian pesanan setelah penggantian' : 'Rincian pesanan yang tetap berlaku'}</h3>
        <ul>{decisionReview!.lines.map(value => <li key={substitutionLineKey(value)}>{value.packages} × {label(value)} · {amount(value.price.currency, value.catalogSubtotal)} · {value.lineRevision === '0' ? 'pesanan awal' : `persetujuan ke-${value.lineRevision}`}</li>)}</ul>
        <p>Subtotal {amount(decisionReview!.currency, decisionReview!.catalogSubtotal)} · Pajak {amount(decisionReview!.currency, decisionReview!.tax)} · Biaya {amount(decisionReview!.currency, decisionReview!.charges)}</p>
        <p><strong>Total {amount(decisionReview!.currency, decisionReview!.total)}</strong></p>
        <p>{decisionReview!.decision === 'Accept' ? 'Persetujuan memindahkan cadangan sisa barang ke pengganti. Persetujuan belanja dan penerimaan toko diperiksa kembali sebelum kiriman berikutnya. Kiriman dan tagihan sebelumnya tetap mengikuti rincian yang sudah disetujui saat itu.' : 'Penolakan mempertahankan pesanan dan cadangan barang saat ini.'}</p>
      </>}
      <form onSubmit={event => { event.preventDefault(); void submit(); }}><label><span id={name('reason')}>Alasan</span><textarea aria-labelledby={name('reason')} disabled={locked} value={reason} maxLength={1000} required onChange={event => setReason(event.target.value)} /></label>
        <label className={styles.checkbox}><input type="checkbox" disabled={locked} checked={confirmed} onChange={event => setConfirmed(event.target.checked)} /><span>{proposalReview ? 'Saya telah memeriksa barang, kemasan, jumlah, dan harga pengganti untuk diajukan kepada pembeli.' : decisionReview!.decision === 'Accept' ? 'Saya menyetujui barang, kemasan, jumlah, harga, pajak, biaya, dan total pesanan yang ditampilkan.' : 'Saya menolak penggantian ini dan mempertahankan rincian pesanan saat ini.'}</span></label>
        <div className={styles.actions}><button className={styles.primary} disabled={locked || !confirmed || !reason.trim()}>{proposalReview ? 'Kirim pengajuan ke pembeli' : decisionReview!.decision === 'Accept' ? 'Setujui penggantian barang' : 'Tolak penggantian barang'}</button>
          <button type="button" className={styles.secondary} disabled={locked} onClick={clearReview}>Kembali</button></div></form>
    </section>}
  </>;
}
