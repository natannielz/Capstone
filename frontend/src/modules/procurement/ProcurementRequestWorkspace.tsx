'use client';

import Link from 'next/link';
import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { RequestNotSentError } from '../../api/client';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import { submitTrackedRequest } from '../administration/operation-journal';
import { CatalogReadGate } from '../catalog/catalog-editor-state';
import { createProcurementGateway, procurementLocationKey, procurementQuantity, procurementInput, prepareProcurement, prepareProcurementDecision,
  type ProcurementTarget, type ProcurementPackage, type ProcurementInput, type ProcurementPreview, type ProcurementRequest, type ProcurementDecision, type ProcurementDecisionPreview } from './procurement-request-client';
import styles from '../administration/access.module.css';
import { packageMatchExplanation, type PackageMatching } from './package-matching';
import { ReplenishmentSuggestion } from './ReplenishmentSuggestion';
import { dateTimeFormatter, formatDate } from '@/lib/format';
import { SessionAccessState } from "@/components/workspace-kit/session-access-state";
import { recordNumber } from '@/lib/document-numbers';
import { refreshAfterConfirmedSave, restoreKeptLocation, takeKeptKey } from './procurement-desk';
import { Button } from '@/components/workspace-kit/button';
import { Field } from '@/components/workspace-kit/field';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { Section } from '@/components/workspace-kit/section';
import { StateView } from '@/components/workspace-kit/state-view';
import { Badge } from '@/components/workspace-kit/badge';
import { DataList } from '@/components/workspace-kit/data-list';
import { Facts } from '@/components/workspace-kit/facts';

type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
type DraftLine = { choice: ProcurementPackage; quantity: string; specification: string };
const path = '/procurement/requests', readPermission = 'procurement.read', preparePermission = 'procurement.prepare', approvePermission = 'procurement.approve';
const routes = { omi_regular: 'OMI reguler', ddo: 'DDO', smart: 'S-Mart', pasar_kering: 'Pasar Kering' } as const;
const kinds = { stock_need: 'Kebutuhan stok', incoming_notice: 'Pemberitahuan barang masuk', purchase_request: 'Permintaan pembelian' } as const;
const states = { submitted: 'Menunggu pemeriksaan', approved: 'Disetujui', rejected: 'Ditolak' } as const;
const when = (value: string) => dateTimeFormatter.format(new Date(value));
function message(error: unknown) {
  if (error instanceof IdentityFailure) return 'Sesi Anda perlu diperbarui. Masuk kembali untuk melanjutkan.';
  if (error instanceof AccessFailure || error instanceof RequestNotSentError) {
    if (error.code === 'procurement_invalid' || error.code === 'invalid_procurement_request') return 'Pilih lokasi, jalur dan tanggal, lalu periksa kemasan, jumlah, referensi yang diperlukan dan alasan.';
    if (error.code === 'procurement_view_stale' || error.code === 'procurement_request_changed') return 'Sumber lokasi, barang atau permintaan berubah. Muat data terbaru dan periksa kembali rincian.';
    if (error.code === 'procurement_response_invalid' || error.code === 'procurement_request_integrity_unavailable') return 'Riwayat permintaan belum dapat dipastikan lengkap. Hubungi penanggung jawab sebelum melanjutkan.';
    if (error.code === 'independent_person_required') return 'Keputusan memerlukan pemeriksa yang berbeda dari pengaju.';
    if (error.code === 'commit_outcome_unknown' || error.code === 'resolve_previous_attempt') return 'Hasil belum terkonfirmasi. Gunakan Periksa hasil pada permintaan tersimpan; jangan kirim ulang.';
    if (error instanceof AccessFailure && error.status >= 500) return 'Layanan belum tersedia. Jika ada permintaan tersimpan, gunakan Periksa hasil setelah koneksi kembali.';
    return accessMessage(error);
  }
  return 'Koneksi terputus. Jika ada permintaan tersimpan, gunakan Periksa hasil; jangan kirim ulang.';
}
export function ProcurementRequestWorkspace() {
  const [identity] = useState(createIdentityGateway), [access] = useState(createAccessGateway);
  const [account, setAccount] = useState<Account | null>(null), [error, setError] = useState<unknown>(null), [loading, setLoading] = useState(true), [revision, setRevision] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => { let active = true; const controller = new AbortController(); void Promise.resolve().then(async () => {
    if (!active) return; setAccount(null); setLoading(true); setError(null);
    try {
      const session = await identity.session(controller.signal), observedAt = performance.now();
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
      const current = await access.current(); if (!active) return;
      if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
      if (![readPermission, preparePermission, approvePermission].some(value => current.allowedActions.includes(value))) throw new AccessFailure(403, 'scope_denied');
      if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
      setAccount({ actorId: current.userId, actions: current.allowedActions, session, observedAt }); setElapsed(performance.now() - observedAt);
    } catch (failure) { if (active) setError(failure); } finally { if (active) setLoading(false); }
  }); return () => { active = false; controller.abort(); }; }, [identity, access, revision]);
  useEffect(() => { const timer = window.setInterval(() => { if (account) setElapsed(performance.now() - account.observedAt); }, 1000);
    const restored = (event: PageTransitionEvent) => { if (event.persisted) { setAccount(null); setRevision(value => value + 1); } };
    window.addEventListener('pageshow', restored); return () => { window.clearInterval(timer); window.removeEventListener('pageshow', restored); }; }, [account]);
  const denied = useCallback((failure: unknown) => { if (failure instanceof AccessFailure && (failure.status === 401 || failure.code === 'actor_changed' || failure.code === 'session_expired')) { setAccount(null); setError(failure); } }, []);
  return <main className={styles.page}><PageHeader title="Permintaan pengadaan" description={'Catat kebutuhan barang atau pemberitahuan barang masuk, lalu ajukan untuk pemeriksaan independen.'} /><div className="ui-stack">
    {error !== null && account !== null && <StateView kind="error" title={message(error)} />}
    {loading ? <p role="status">Memeriksa akun dan akses…</p> : !account || sessionTiming(account.session, elapsed).expired ? <SessionAccessState kind={account ? "expired" : undefined} error={error} returnPath={path} onRetry={() => setRevision(value => value + 1)} />
      : <RequestDesk key={account.actorId + ':' + account.observedAt} account={account} fresh={sessionTiming(account.session, elapsed).fresh} onDenied={denied} />}
  </div></main>;
}
function RequestDetails({ value }: { value: Pick<ProcurementPreview, 'input' | 'source' | 'lines'> }) {
  return <><Facts items={[...(['organization', 'shop', 'warehouse', 'channel'] as const).map((field, index) => ({ term: ['Organisasi', 'Toko', 'Gudang', 'Kanal'][index], value: <>{value.source[field].code} · {value.source[field].name}</> })), { term: 'Jalur pengadaan', value: routes[value.input.route] }, { term: 'Jenis kebutuhan', value: kinds[value.input.kind] }, { term: 'Tanggal kebutuhan', value: formatDate(value.input.businessDate) }, ...(value.input.externalReference ? [{ term: 'Referensi', value: <span className={styles.preserve}>{value.input.externalReference}</span> }] : [])]} />
    <h4>Barang dan kemasan</h4>{value.lines.map(line => <section className={styles.preview} key={line.skuId}><h4>{line.familyName} — {line.packageLabel}</h4><p>{line.skuCode} · {line.baseUnitsPerPackage} {line.baseUnitCode} per kemasan</p>
      <p>{line.packages} kemasan · Total {(BigInt(line.packages) * BigInt(line.baseUnitsPerPackage)).toString()} {line.baseUnitCode}</p>{line.specification && <p className={styles.preserve}>Spesifikasi: {line.specification}</p>}</section>)}
    <p className={styles.preserve}>Alasan: {value.input.reason}</p><p className={styles.help}>Persetujuan ini mencatat kebutuhan internal. Pemasok dan harga belum ditetapkan; pembayaran dan penerimaan barang belum dicatat.</p></>;
}
function RequestDesk({ account, fresh, onDenied }: { account: Account; fresh: boolean; onDenied: (failure: unknown) => void }) {
  const [gateway] = useState(createProcurementGateway), [gate] = useState(() => new CatalogReadGate()), recovery = useOperationRecovery(account.actorId);
  const ledger = useRef<MutationLedger | null>(null), keyPrefix = useRef('pr');
  const [targets, setTargets] = useState<ProcurementTarget[]>([]), [selected, setSelected] = useState<ProcurementTarget | null>(null);
  const [page, setPage] = useState<{ items: ProcurementRequest[]; nextRequestId: string | null }>({ items: [], nextRequestId: null }), [request, setRequest] = useState<ProcurementRequest | null>(null);
  const [editing, setEditing] = useState(false), [route, setRoute] = useState<ProcurementInput['route'] | ''>(''), [kind, setKind] = useState<ProcurementInput['kind'] | ''>('');
  const [businessDate, setBusinessDate] = useState(''), [externalReference, setExternalReference] = useState(''), [reason, setReason] = useState(''), [draftLines, setDraftLines] = useState<DraftLine[]>([]);
  const [replenishmentReference, setReplenishmentReference] = useState<string | null>(null);
  const [search, setSearch] = useState(''), [packages, setPackages] = useState<ProcurementPackage[]>([]), [packagePage, setPackagePage] = useState<{ search: string; offset: number; hasMore: boolean; matching: PackageMatching } | null>(null);
  const [chosen, setChosen] = useState<ProcurementPackage | null>(null), [quantity, setQuantity] = useState(''), [specification, setSpecification] = useState('');
  const [decisionReason, setDecisionReason] = useState(''), [confirmed, setConfirmed] = useState(false);
  const [submissionReview, setSubmissionReview] = useState<{ preview: ProcurementPreview; command: ReturnType<typeof prepareProcurement> } | null>(null);
  const [decisionReview, setDecisionReview] = useState<{ preview: ProcurementDecisionPreview; command: ReturnType<typeof prepareProcurementDecision> } | null>(null);
  const [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [offline, setOffline] = useState(false), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState(''), [revision, setRevision] = useState(0);
  const alive = useRef(false), pending = useRef(false), heading = useRef<HTMLHeadingElement>(null);
  // The location chosen before "Muat data terbaru", restored once the targets are read again (F-022).
  const keep = useRef<string | null>(null);
  const current = useCallback(() => alive.current && navigator.onLine && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const canRead = account.actions.includes(readPermission), canPrepare = account.actions.includes(preparePermission), canDecide = account.actions.includes(approvePermission);
  const locked = busy || recovering || offline, commandLocked = locked || recovery.locked, reviewLocked = submissionReview !== null || decisionReview !== null;
  const clearReview = useCallback(() => { setRequest(null); setEditing(false); setSubmissionReview(null); setDecisionReview(null); setReason(''); setDecisionReason(''); setConfirmed(false);
    setRoute(''); setKind(''); setBusinessDate(''); setExternalReference(''); setReplenishmentReference(null); setDraftLines([]); setSearch(''); setPackages([]); setPackagePage(null); setChosen(null); setQuantity(''); setSpecification(''); }, []);
  const clearSelection = useCallback(() => { clearReview(); setSelected(null); setPage({ items: [], nextRequestId: null }); }, [clearReview]);
  function mutations() { return ledger.current ??= new MutationLedger(() => `${keyPrefix.current}.${crypto.randomUUID()}`); }
  useEffect(() => { alive.current = true; const connection = () => setOffline(!navigator.onLine); connection(); window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => { alive.current = false; gate.cancel(); window.removeEventListener('online', connection); window.removeEventListener('offline', connection); }; }, [gate]);
  useEffect(() => { const read = gate.begin(account.actorId, 'procurement-targets'); void Promise.resolve().then(async () => {
    if (!read.current() || !current()) return; const remembered = takeKeptKey(keep); setBusy(true); setTargets([]); clearSelection(); setError(null);
    try { if (canRead) { const result = await gateway.targets(account.actorId, read.signal); if (read.current() && current()) { setTargets(result.items);
      const kept = await restoreKeptLocation(result.items, remembered, item => procurementLocationKey(item.location), target => gateway.list(account.actorId, target.location, null, read.signal));
      if (kept && read.current() && current()) { setSelected(kept.target); setPage(kept.page); } } } }
    catch (failure) { if (read.current() && current()) { setError(failure); onDenied(failure); } }
    finally { if (read.current()) setBusy(false); }
  }); return () => gate.cancel(); }, [gateway, gate, account.actorId, revision, current, onDenied, canRead, clearSelection]);
  useEffect(() => { if (submissionReview || decisionReview) heading.current?.focus(); }, [submissionReview, decisionReview]);
  useEffect(() => { if (!busy && recovery.entries.length === 0) return; const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ''; };
    window.addEventListener('beforeunload', warn); return () => window.removeEventListener('beforeunload', warn); }, [busy, recovery.entries.length]);
  async function work(action: (valid: () => boolean, signal: AbortSignal) => Promise<void>) {
    if (!current() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); setNotice('');
    const read = gate.begin(account.actorId, 'procurement-action'), valid = () => read.current() && current();
    try { await action(valid, read.signal); } catch (failure) { if (read.current() && alive.current) {
      setError(failure); if ((failure instanceof AccessFailure || failure instanceof RequestNotSentError) && ([401, 403].includes(failure.status) || ['actor_changed', 'procurement_view_stale', 'procurement_request_changed'].includes(failure.code))) { clearSelection(); setTargets([]); } onDenied(failure);
    } } finally { pending.current = false; recovery.refresh(); if (read.current() && alive.current) setBusy(false); }
  }
  async function select(choice: ProcurementTarget, valid: () => boolean, signal: AbortSignal) { clearSelection(); const next = await gateway.list(account.actorId, choice.location, null, signal); if (valid()) { setSelected(choice); setPage(next); } }
  async function find(query = search.trim(), offset = 0) {
    if (!selected || commandLocked || reviewLocked) return;
    await work(async (valid, signal) => { setChosen(null); setPackages([]); setPackagePage(null); const next = await gateway.packages(account.actorId, selected.location, query, offset, signal);
      if (valid()) { setPackages(next.items); setPackagePage({ search: query, offset: next.offset, hasMore: next.hasMore, matching: next.matching }); } });
  }
  function includePackage() {
    if (!chosen || commandLocked || reviewLocked || !current()) return;
    try { procurementQuantity(quantity); const line = { choice: chosen, quantity, specification }, index = draftLines.findIndex(value => value.choice.skuId === chosen.skuId);
      if (index < 0 && draftLines.length >= 100) throw new RequestNotSentError(400, 'procurement_invalid');
      setDraftLines(index < 0 ? [...draftLines, line] : draftLines.map((value, position) => position === index ? line : value)); setChosen(null); setQuantity(''); setSpecification(''); setPackages([]); setPackagePage(null); setSearch(''); setError(null);
    } catch (failure) { setError(failure); }
  }
  async function previewSubmission(event: FormEvent) {
    event.preventDefault(); if (!selected || !canPrepare || !fresh || commandLocked || route === '' || kind === '') return;
    await work(async (valid, signal) => {
      const input = procurementInput({ location: selected.location, route, kind, businessDate, externalReference: externalReference || null, reason,
        lines: draftLines.map(({ choice, quantity, specification }) => ({ skuId: choice.skuId, expectedSkuRevision: choice.skuRevision, definitionId: choice.definitionId, definitionVersion: choice.definitionVersion, packages: procurementQuantity(quantity), specification: specification || null })) });
      const preview = await gateway.preview(account.actorId, input, signal), command = prepareProcurement(account.actorId, selected, draftLines.map(value => value.choice), input, preview);
      if (valid()) { setSubmissionReview({ preview, command }); setConfirmed(false); }
    });
  }
  async function submit() {
    if (!submissionReview || !confirmed || commandLocked || !canPrepare || !fresh) return;
    const command = submissionReview.command; keyPrefix.current = 'pr.' + procurementLocationKey(command.location).replaceAll(':', '.');
    await work(async (valid, signal) => {
      await refreshAfterConfirmedSave(() => submitTrackedRequest(mutations(), recovery.journal, account.actorId, 'procurement-submit', 'procurement.request.submit', command.body,
        requestKey => gateway.submit(command, requestKey, () => valid() && sessionTiming(account.session, performance.now() - account.observedAt).fresh)), {
        valid, confirmed: () => { clearReview(); setNotice('Permintaan tersimpan dan menunggu pemeriksaan independen.'); },
        list: async () => { const list = await gateway.list(account.actorId, command.location, null, signal); if (valid()) setPage(list); }, // listed without a reload (F-022)
        detail: async saved => { const next = await gateway.request(account.actorId, saved.requestId, signal); if (valid()) setRequest(next); },
      });
    });
  }
  async function previewDecision(value: ProcurementDecision) {
    if (!request || !canDecide || !fresh || commandLocked || request.state !== 'submitted') return;
    await work(async (valid, signal) => { const preview = await gateway.previewDecision(account.actorId, request, value, signal), command = prepareProcurementDecision(account.actorId, request, preview, decisionReason);
      if (valid()) { setDecisionReview({ preview, command }); setConfirmed(false); } });
  }
  async function saveDecision() {
    if (!decisionReview || !confirmed || !canDecide || !fresh || commandLocked) return;
    const command = decisionReview.command; keyPrefix.current = `prd.${command.decision}.${command.requestId}`;
    await work(async (valid, signal) => {
      await refreshAfterConfirmedSave(() => submitTrackedRequest(mutations(), recovery.journal, account.actorId, 'procurement-decision', 'procurement.request.' + command.decision, command.body,
        requestKey => gateway.decide(command, requestKey, () => valid() && sessionTiming(account.session, performance.now() - account.observedAt).fresh)), {
        valid, confirmed: () => { clearReview(); setNotice(command.decision === 'approve' ? 'Permintaan disetujui untuk tindak lanjut pengadaan.' : 'Penolakan permintaan tersimpan.'); },
        list: async () => { if (selected) { const list = await gateway.list(account.actorId, selected.location, null, signal); if (valid()) setPage(list); } },
        detail: async saved => { const next = await gateway.request(account.actorId, saved.requestId, signal); if (valid()) setRequest(next); },
      });
    });
  }
  function reconcile() { ledger.current?.resetAfterReconciliation(); clearSelection(); recovery.refresh(); setRevision(value => value + 1); }
  return <><div className="ui-stack">
    {canRead && canPrepare && account.actions.includes('stock.read') && <ReplenishmentSuggestion actorId={account.actorId} current={current}
      disabled={commandLocked || reviewLocked} onUse={async advice => { await work(async (valid, signal) => {
        const target = targets.find(item => item.location.organizationId === advice.location.organizationId && item.location.shopId === advice.location.shopId &&
          item.location.warehouseId === advice.location.warehouseId && item.location.channelId === advice.location.channelId);
        if (!target || advice.suggestedPurchasePackages === null) throw new AccessFailure(409, 'procurement_view_stale');
        procurementQuantity(advice.suggestedPurchasePackages);
        const choices = await gateway.packages(account.actorId, target.location, advice.skuCode, 0, signal);
        const exact = choices.items.find(item => item.skuId === advice.skuId && item.skuRevision === advice.skuRevision && item.definitionId === advice.definitionId &&
          item.definitionVersion === advice.definitionVersion && item.skuCode === advice.skuCode && item.packageLabel === advice.packageLabel &&
          item.packageKind === advice.packageKind && item.baseUnitCode === advice.baseUnitCode && item.baseUnitsPerPackage === advice.baseUnitsPerPackage);
        if (!exact) throw new AccessFailure(409, 'procurement_view_stale');
        await select(target, valid, signal); if (!valid()) return;
        setEditing(true); setReplenishmentReference('replenishment:' + advice.id); setExternalReference('replenishment:' + advice.id);
        setDraftLines([{choice: exact, quantity: advice.suggestedPurchasePackages, specification: 'Kebutuhan dari pemeriksaan ' + advice.id + ', diamati ' + when(advice.observedAt)}]);
        setNotice('Saran dimasukkan ke formulir. Pilih jalur dan tanggal, isi alasan, lalu periksa kembali jumlah sebelum mengajukan.');
      }); }} />}
    {(canPrepare || canDecide) && !fresh && <div className="ui-notice" data-tone="warning"><p>Perbarui verifikasi kedua sebelum mengirim atau memutuskan permintaan.</p>
      {[preparePermission, approvePermission].filter(permission => account.actions.includes(permission) && account.session.stepUpOperations.includes(permission)).map(permission => <p key={permission}><Link href={`/identity/account?operation=${permission}&returnPath=${encodeURIComponent(path)}`}>{permission === preparePermission ? 'Verifikasi untuk mengirim permintaan' : 'Verifikasi untuk memutuskan permintaan'}</Link></p>)}</div>}
    {offline && <StateView kind="offline" title='Koneksi terputus. Pengiriman dan keputusan ditahan sampai koneksi kembali.' />}
    {error !== null && <StateView kind="error" title={message(error)} />}{notice && <StateView kind="success" title={notice} />}
    {recovery.error !== null && <StateView kind="error" title={message(recovery.error)} />}
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={locked} onResolved={reconcile} onFailure={onDenied} onBusyChange={setRecovering}
      recoveryAdapters={{ 'procurement.request': { lookup: entry => gateway.recover(entry.actorId, entry.operation, entry.key) } }} />
    <Section title="Pilihan lokasi" label="Pilihan lokasi" actions={<Button variant="secondary" disabled={locked || reviewLocked} onClick={() => { keep.current = selected ? procurementLocationKey(selected.location) : null; clearSelection(); setRevision(value => value + 1); }}>Muat data terbaru</Button>}><div className="ui-form"><Field id="pro-request-workspace-1" label="Organisasi / toko / gudang / kanal"><select value={selected ? procurementLocationKey(selected.location) : ''} disabled={locked || reviewLocked || recovery.locked} onChange={event => {
        const choice = targets.find(item => procurementLocationKey(item.location) === event.target.value); if (choice) void work((valid, signal) => select(choice, valid, signal));
      }}><option value="">Pilih lokasi yang ditugaskan</option>{targets.map(item => <option key={procurementLocationKey(item.location)} value={procurementLocationKey(item.location)}>{item.organization.name} · {item.shop.name} · {item.warehouse.name} · {item.channel.name}</option>)}</select></Field>
      {!canRead && <p className="ui-notice" data-tone="warning">Penugasan untuk membaca pengadaan belum tersedia. Hubungi pengelola akses.</p>}
      
      {!busy && canRead && error === null && targets.length === 0 && <p>Belum ada lokasi dengan profil yang disetujui dalam penugasan Anda.</p>}
    </div></Section>
    {busy && <p role="status">Memeriksa permintaan pengadaan…</p>}
    {selected && <div className="ui-desk"><Section title='Daftar permintaan' id="procurement-register-heading" description={<>{selected.shop.code} · {selected.warehouse.name}</>}>{canPrepare && <Button variant="primary" disabled={commandLocked || reviewLocked} onClick={() => { clearReview(); setEditing(true); }}>Buat permintaan</Button>}<DataList label='Daftar permintaan' items={page.items} getKey={item => item.id} disabled={locked || reviewLocked} selectedKey={(request?.id) ?? null} empty={error === null ? { title: 'Belum ada permintaan pengadaan.' } : undefined}onSelect={item => void work(async (valid, signal) => { clearReview(); const next = await gateway.request(account.actorId, item.id, signal); if (valid()) setRequest(next); })} renderTitle={item => <>{routes[item.input.route]} · {formatDate(item.input.businessDate)}</>} renderBadge={item => <Badge status={states[item.state]} />} renderMeta={item => <>{when(item.submittedAt)} · Permintaan {recordNumber('purchaseRequest', item.id)}</>} /><nav className="ui-actions" aria-label="Halaman permintaan"><Button variant="secondary" disabled={locked || reviewLocked} onClick={() => void work((valid, signal) => select(selected, valid, signal))}>Terbaru</Button>
        <Button variant="secondary" disabled={locked || reviewLocked || page.nextRequestId === null} onClick={() => void work(async (valid, signal) => { const next = await gateway.list(account.actorId, selected.location, page.nextRequestId, signal); if (valid()) { clearReview(); setPage(next); } })}>Berikutnya</Button></nav></Section>
      <section className="ui-section" aria-label="Rincian pengadaan"><div className="ui-stack">
        {!editing && !request && <div className="ui-form"><h2>{selected.shop.name} · {selected.warehouse.name}</h2><p>Pilih permintaan untuk membaca rincian dan hasil pemeriksaan.</p></div>}
        {editing && <form className="ui-form" onSubmit={event => void previewSubmission(event)}><h2>Buat permintaan pengadaan</h2>
          <fieldset disabled={commandLocked || submissionReview !== null}><Field id="pro-request-workspace-2" label="Jalur pengadaan"><select required value={route} onChange={event => { const next = event.target.value as ProcurementInput['route'] | ''; setRoute(next); setKind(next === '' || next === 'omi_regular' ? '' : 'purchase_request'); setExternalReference(replenishmentReference ?? ''); }}>
            <option value="">Pilih jalur pengadaan</option>{Object.entries(routes).map(([value, label]) => <option value={value} key={value}>{label}</option>)}</select></Field>
            {route === 'omi_regular' ? <><Field id="pro-request-workspace-3" label="Jenis kebutuhan"><select required value={kind} onChange={event => { const next = event.target.value as ProcurementInput['kind'] | ''; setKind(next); setExternalReference(next === 'incoming_notice' ? '' : replenishmentReference ?? ''); }}><option value="">Pilih jenis kebutuhan OMI</option><option value="stock_need">Kebutuhan stok</option><option value="incoming_notice">Pemberitahuan barang masuk</option></select></Field>
              <p className={styles.help}>Kebutuhan stok mencatat kebutuhan internal. Pemberitahuan barang masuk mencatat referensi pemberitahuan OMI yang telah diterima.</p></> : route !== '' && <p>Jenis kebutuhan: Permintaan pembelian</p>}
            <Field id="pro-request-workspace-4" label="Tanggal kebutuhan"><input required type="date" min="0001-01-02" max="9999-12-31" value={businessDate} onChange={event => setBusinessDate(event.target.value)} /></Field>
            <Field id="pro-request-workspace-5" label={<>{kind === 'incoming_notice' ? 'Referensi pemberitahuan OMI' : 'Referensi pendukung (opsional)'}</>}><input required={kind === 'incoming_notice'} maxLength={128} value={externalReference} onChange={event => setExternalReference(event.target.value)} /></Field>
            <h3>Barang dan kemasan</h3>{draftLines.length === 0 && <p>Tambahkan kemasan dari katalog untuk lokasi ini.</p>}
            {draftLines.map((line, index) => <section className={styles.preview} key={line.choice.skuId}><h4>{line.choice.familyName} — {line.choice.packageLabel}</h4><p>{line.choice.skuCode} · {line.choice.baseUnitsPerPackage} {line.choice.baseUnitCode} per kemasan</p>
              <Field id="pro-request-workspace-6" label={<>Jumlah kemasan barang {index + 1}</>}><input required inputMode="numeric" pattern="[1-9][0-9]{0,6}" maxLength={7} value={line.quantity} onChange={event => setDraftLines(values => values.map((value, position) => position === index ? { ...value, quantity: event.target.value } : value))} /></Field>
              <Field id="pro-request-workspace-7" label={<>Spesifikasi barang {index + 1} (opsional)</>}><input maxLength={1000} value={line.specification} onChange={event => setDraftLines(values => values.map((value, position) => position === index ? { ...value, specification: event.target.value } : value))} /></Field>
              <Button variant="secondary" type="button" onClick={() => setDraftLines(values => values.filter((_, position) => position !== index))}>Hapus barang {index + 1}</Button></section>)}
            <section className={styles.preview} aria-label="Pilih kemasan katalog"><Field id="pro-request-workspace-8" label="Cari barang atau kemasan"><input maxLength={128} value={search} onChange={event => setSearch(event.target.value)} /></Field><Button variant="secondary" type="button" onClick={() => void find()}>Cari kemasan</Button>
              <p className={styles.help}>Hasil pencarian adalah saran. Pilih barang lalu periksa isi dan jumlah kemasan; tidak ada pilihan otomatis.</p>
              {packagePage?.matching.limited && <p role="status">Hasil pencarian dibatasi untuk katalog besar. Cari kode SKU tepat atau potongan nama untuk mempersempit hasil.</p>}
              <DataList label="Kemasan yang tersedia" toggle items={packages} getKey={item => item.skuId} selectedKey={chosen?.skuId ?? null} onSelect={item => { setChosen(item); setQuantity(''); setSpecification(''); }}
                renderTitle={item => <>{item.familyName}</>} renderMeta={item => <>{item.skuCode} · {item.packageLabel} · {item.baseUnitsPerPackage} {item.baseUnitCode} · {packageMatchExplanation(packagePage?.matching.items.find(value => value.skuId === item.skuId))}</>} />
              {packagePage && packages.length === 0 && <p>Tidak ada kemasan yang cocok dalam lokasi ini.</p>}
              {packagePage && <nav className="ui-actions" aria-label="Halaman kemasan"><Button variant="secondary" type="button" disabled={packagePage.offset === 0} onClick={() => void find(packagePage.search, Math.max(0, packagePage.offset - 25))}>Kemasan sebelumnya</Button>
                <Button variant="secondary" type="button" disabled={!packagePage.hasMore || packagePage.offset >= 10000} onClick={() => void find(packagePage.search, packagePage.offset + 25)}>Kemasan berikutnya</Button></nav>}
              {chosen && <section aria-label="Periksa kemasan pilihan"><h4>{chosen.familyName} — {chosen.packageLabel}</h4><p>{chosen.baseUnitsPerPackage} {chosen.baseUnitCode} per kemasan</p>
                <Field id="pro-request-workspace-9" label="Jumlah kemasan"><input inputMode="numeric" pattern="[1-9][0-9]{0,6}" maxLength={7} value={quantity} onChange={event => setQuantity(event.target.value)} /></Field>
                <Field id="pro-request-workspace-10" label="Spesifikasi (opsional)"><input maxLength={1000} value={specification} onChange={event => setSpecification(event.target.value)} /></Field>
                <p className={styles.help}>Gunakan spesifikasi untuk kebutuhan seperti ukuran atau logo khusus yang perlu ditindaklanjuti.</p><Button variant="secondary" type="button" onClick={includePackage}>Tambahkan kemasan</Button>
                <Button variant="secondary" type="button" onClick={() => { setChosen(null); setQuantity(''); setSpecification(''); }}>Batalkan pilihan kemasan</Button></section>}
            </section>
            <Field id="pro-request-workspace-11" label="Alasan permintaan"><textarea required rows={3} maxLength={1000} value={reason} onChange={event => setReason(event.target.value)} /></Field><Button variant="secondary" disabled={!fresh || draftLines.length === 0 || chosen !== null} type="submit">Periksa rincian permintaan</Button></fieldset>
          {submissionReview && <section className={styles.preview} aria-label="Rincian permintaan"><h3 ref={heading} tabIndex={-1}>Rincian yang akan dikirim</h3><RequestDetails value={submissionReview.preview} />
            <label className={styles.checkbox}><input type="checkbox" checked={confirmed} disabled={commandLocked || !fresh} onChange={event => setConfirmed(event.target.checked)} />Saya telah memeriksa jalur, tanggal, kemasan dan jumlah permintaan.</label>
            <div className={styles.actions}><Button variant="primary" type="button" disabled={commandLocked || !fresh || !confirmed} onClick={() => void submit()}>Kirim permintaan</Button>
              <Button variant="secondary" type="button" disabled={commandLocked} onClick={() => { setSubmissionReview(null); setConfirmed(false); }}>Ubah rincian</Button></div></section>}
        </form>}
        {request && <div className="ui-form"><h2>{states[request.state]}</h2><RequestDetails value={request} /><p className={styles.help}>Permintaan {recordNumber('purchaseRequest', request.id)} · {when(request.submittedAt)}</p>
          {request.decision && <p className={styles.preserve}>Keputusan {when(request.decision.decidedAt)}: {request.decision.reason}</p>}
          {request.state === 'submitted' && canDecide && <><p className={styles.help}>Pemeriksa harus berbeda dari pengaju dan menggunakan verifikasi kedua yang masih baru.</p><Field id="pro-request-workspace-12" label="Alasan keputusan"><textarea required rows={3} maxLength={1000} disabled={commandLocked || decisionReview !== null} value={decisionReason} onChange={event => setDecisionReason(event.target.value)} /></Field>
            {!decisionReview && <div className={styles.actions}><Button variant="primary" disabled={commandLocked || !fresh || !decisionReason.trim()} onClick={() => void previewDecision('approve')}>Tinjau persetujuan</Button><Button variant="secondary" disabled={commandLocked || !fresh || !decisionReason.trim()} onClick={() => void previewDecision('reject')}>Tinjau penolakan</Button></div>}</>}
          {decisionReview && <section className={styles.preview} aria-label="Rincian keputusan"><h3 ref={heading} tabIndex={-1}>{decisionReview.command.decision === 'approve' ? 'Pastikan persetujuan permintaan' : 'Pastikan penolakan permintaan'}</h3><RequestDetails value={decisionReview.preview.request} /><p className={styles.preserve}>{decisionReview.command.body.reason}</p>
            <label className={styles.checkbox}><input type="checkbox" checked={confirmed} disabled={commandLocked || !fresh} onChange={event => setConfirmed(event.target.checked)} />Saya telah memeriksa permintaan dan siap mencatat keputusan ini.</label>
            <div className={styles.actions}><Button variant="primary" disabled={commandLocked || !fresh || !confirmed} onClick={() => void saveDecision()}>{decisionReview.command.decision === 'approve' ? 'Setujui permintaan' : 'Simpan penolakan'}</Button><Button variant="secondary" disabled={commandLocked} onClick={() => { setDecisionReview(null); setConfirmed(false); }}>Kembali ke permintaan</Button></div></section>}
        </div>}
      </div></section></div>}
  </div></>;
}
