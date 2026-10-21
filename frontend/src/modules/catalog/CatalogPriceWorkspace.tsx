'use client';

import Link from 'next/link';
import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { createAccessGateway } from '../administration/access-client';
import { AdministrationNavigation } from '../administration/AdministrationNavigation';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import { submitTrackedRequest } from '../administration/operation-journal';
import { AccessFailure, accessMessage } from '../administration/workflow';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, identityMessage, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createCatalogPriceGateway } from './catalog-price-client';
import { CatalogReadGate } from './catalog-editor-state';
import { PriceMutationLedger, priceRecoverySku } from './catalog-price-recovery';
import { pricePath, preparePriceProposal, preparePriceDecision, priceChange, type PriceAccess, type PriceChoice, type PricePreview,
  type PriceProposal, type PriceCommand, type CatalogPrice } from './catalog-price-workflow';
import styles from '../administration/access.module.css';
import { dateTimeFormatter, formatMoney, formatRupiah } from "@/lib/format";
import { rupiahAmount } from "@/lib/rupiah-input";

const loginHref = '/identity/login?returnPath=' + encodeURIComponent(pricePath);
const factorHref = '/identity/account?operation=pricing.approve&returnPath=' + encodeURIComponent(pricePath);
type Loaded = { actorId: string; actions: string[]; session: IdentitySession; observedAt: number; generation: number };
const lost = (error: unknown) => (error instanceof AccessFailure || error instanceof IdentityFailure) && (error.status === 401 || error.code === 'actor_changed');
function message(error: unknown): string {
  if (error instanceof IdentityFailure) return identityMessage(error);
  if (error instanceof AccessFailure) {
    if (error.code === 'catalog_price_input_invalid') return 'Lengkapi mata uang, harga per kemasan dan alasan. Tulis harga seperti 12.000 atau 12000,50 (paling banyak dua angka di belakang koma).';
    if (error.code === 'step_up_required') return 'Verifikasi dua langkah terbaru diperlukan untuk meninjau harga dan memeriksa hasil keputusan.';
    if (error.status === 409) return 'Harga atau kemasan telah berubah. Muat ulang dan tinjau kembali sebelum mengirim.';
    if (error.status === 403) return 'Tindakan tidak diizinkan. Pemeriksa harus orang terverifikasi yang berbeda dari pengaju dan memiliki penugasan yang masih berlaku.';
    return accessMessage(error);
  }
  return 'Hasil belum dapat dipastikan. Periksa referensi permintaan yang tersimpan sebelum mengirim lagi.';
}
const amount = (price: { currency: string; unitPrice: string } | null) => {
  if (price === null) return 'Belum ada harga disetujui';
  return formatMoney(price.currency, price.unitPrice);
};
const date = (value: string) => dateTimeFormatter.format(new Date(value));
/** Current, proposed and the move between them, so a reviewer sees the size of a change before reading the reason. */
function PriceComparison({ base, proposed }: { base: { currency: string; unitPrice: string } | null; proposed: { currency: string; unitPrice: string } }) {
  const change = priceChange(base, proposed);
  const moved = change.kind === 'first' ? 'Harga pertama untuk kemasan ini' : change.kind === 'currency' ? 'Mata uang berbeda dari harga berlaku'
    : change.kind === 'same' ? 'Tidak berubah' : (change.kind === 'up' ? 'Naik ' : 'Turun ') + formatMoney(proposed.currency, change.difference!) + (change.percent ? ' (' + (change.kind === 'up' ? '+' : '−') + change.percent + '%)' : '');
  return <><dl className={styles.facts}><div><dt>Harga berlaku</dt><dd>{amount(base)}</dd></div><div><dt>Harga diajukan</dt><dd><strong>{amount(proposed)}</strong></dd></div>
    <div><dt>Perubahan</dt><dd>{moved}</dd></div></dl>
    {change.large && <p className={styles.alert}>Perubahan besar. Periksa kontrak, acuan harga dan alasan sebelum memutuskan.</p>}</>;
}
const status = (value: PriceProposal['status']) => ({ proposed: 'Menunggu tinjauan', approve: 'Disetujui', reject: 'Ditolak', withdraw: 'Ditarik' })[value];

export function CatalogPriceWorkspace() {
  const [identity] = useState(() => createIdentityGateway()), [access] = useState(() => createAccessGateway()), [gate] = useState(() => new CatalogReadGate());
  const [loaded, setLoaded] = useState<Loaded | null>(null), [error, setError] = useState<unknown>(null), [loading, setLoading] = useState(true);
  const generation = useRef(0);
  const refresh = useCallback(async () => {
    const request = gate.begin('session', pricePath); setLoaded(null); setLoading(true); setError(null);
    try {
      const session = await identity.session(request.signal), observedAt = performance.now(); if (!request.current()) return;
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
      const account = await access.current(); if (!request.current()) return;
      if (account.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
      if (!account.allowedActions.some(action => ['pricing.manage', 'pricing.approve'].includes(action))) throw new AccessFailure(403, 'permission_denied');
      setLoaded({ actorId: account.userId, actions: [...account.allowedActions], session, observedAt, generation: ++generation.current });
    } catch (failure) { if (request.current()) setError(failure); }
    finally { if (request.current()) setLoading(false); }
  }, [identity, access, gate]);
  useEffect(() => {
    let active = true; void Promise.resolve().then(() => { if (active) void refresh(); });
    const restored = (event: PageTransitionEvent) => { if (event.persisted) void refresh(); };
    window.addEventListener('pageshow', restored); return () => { active = false; gate.cancel(); window.removeEventListener('pageshow', restored); };
  }, [refresh, gate]);
  const failedSession = useCallback((failure: unknown) => { if (lost(failure)) { gate.cancel(); setLoaded(null); setError(failure); setLoading(false); } }, [gate]);
  return <main className={styles.page}><header className={styles.header}><div><Link href="/identity/account" className={styles.brand}>Unit Toko</Link><h1>Harga kemasan</h1><p>Harga yang jelas untuk setiap kemasan, dengan peninjauan oleh orang yang berbeda.</p></div></header>
    {loaded ? <PriceDesk key={loaded.actorId + ':' + loaded.generation} {...loaded} onRefresh={refresh} onFailure={failedSession} /> :
      <section className={styles.state} aria-busy={loading}><h2>{loading ? 'Memeriksa sesi dan akses' : 'Ruang harga belum dapat dibuka'}</h2>
        {error !== null && <p role="alert">{message(error)}</p>}{lost(error) && <Link href={loginHref}>Masuk kembali</Link>}
        <button className={styles.secondary} disabled={loading} onClick={() => void refresh()}>Periksa kembali</button></section>}
  </main>;
}

function PriceDesk({ actorId, actions, session, observedAt, onRefresh, onFailure }: Loaded & { onRefresh: () => Promise<void>; onFailure: (error: unknown) => void }) {
  const [gateway] = useState(() => createCatalogPriceGateway()), [listGate] = useState(() => new CatalogReadGate()), [detailGate] = useState(() => new CatalogReadGate());
  const recovery = useOperationRecovery(actorId), alive = useRef(false), pending = useRef(false), reviewRef = useRef<HTMLDivElement>(null), formRef = useRef<HTMLDivElement>(null);
  const [ledger] = useState(() => new PriceMutationLedger());
  const [mode, setMode] = useState<PriceAccess>(actions.includes('pricing.manage') ? 'prepare' : 'review');
  const [choices, setChoices] = useState<PriceChoice[]>([]), [search, setSearch] = useState(''), [query, setQuery] = useState(''), [after, setAfter] = useState<string | null>(null);
  const [preview, setPreview] = useState<PricePreview | null>(null), [proposals, setProposals] = useState<PriceProposal[]>([]), [history, setHistory] = useState<CatalogPrice[]>([]);
  const [selected, setSelected] = useState<PriceProposal | null>(null), [staged, setStaged] = useState<PriceCommand | null>(null);
  const [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [loading, setLoading] = useState(true), [detailLoading, setDetailLoading] = useState(false);
  const [error, setError] = useState<unknown>(null), [notice, setNotice] = useState(''), [hidden, setHidden] = useState(false), [elapsed, setElapsed] = useState(0), [refresh, setRefresh] = useState(0);
  const [currency, setCurrency] = useState('IDR'), [price, setPrice] = useState(''), [reason, setReason] = useState(''), [reference, setReference] = useState('');
  const timing = sessionTiming(session, elapsed), forbidden = hidden || timing.expired || mode === 'review' && !timing.fresh;
  const locked = busy || recovering || recovery.locked || staged !== null;
  const current = useCallback(() => alive.current && !sessionTiming(session, performance.now() - observedAt).expired, [session, observedAt]);
  function clear() { detailGate.cancel(); setPreview(null); setProposals([]); setHistory([]); setSelected(null); setStaged(null); setDetailLoading(false); setReason(''); setReference(''); }
  function failed(failure: unknown) {
    if (!alive.current) return; setError(failure); onFailure(failure);
    if (failure instanceof AccessFailure && (failure.status === 403 && failure.code !== 'step_up_required' || failure.code === 'catalog_price_response_invalid')) {
      listGate.cancel(); clear(); setChoices([]); setHidden(true);
    }
  }
  useEffect(() => {
    alive.current = true; const timer = window.setInterval(() => setElapsed(performance.now() - observedAt), 1000);
    return () => { alive.current = false; listGate.cancel(); detailGate.cancel(); window.clearInterval(timer); };
  }, [observedAt, listGate, detailGate]);
  useEffect(() => {
    const request = listGate.begin(actorId, mode + ':' + query + ':' + after); let active = true;
    void Promise.resolve().then(async () => {
      if (!active) return; setLoading(true); setChoices([]);
      try { const rows = await gateway.choices(actorId, mode, query, after, request.signal); if (request.current()) setChoices(rows); }
      catch (failure) { if (request.current()) { setError(failure); onFailure(failure); } }
      finally { if (request.current()) setLoading(false); }
    });
    return () => { active = false; listGate.cancel(); };
  }, [actorId, mode, query, after, gateway, listGate, onFailure, refresh]);
  useEffect(() => { if (staged) reviewRef.current?.focus(); }, [staged]);
  useEffect(() => {
    if (!locked) return; const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ''; };
    window.addEventListener('beforeunload', warn); return () => window.removeEventListener('beforeunload', warn);
  }, [locked]);
  async function load(skuId: string) {
    const request = detailGate.begin(actorId, mode + ':' + skuId); setDetailLoading(true); setPreview(null); setSelected(null); setProposals([]); setHistory([]); setError(null); setStaged(null);
    try {
      // These reads fence the same catalog/authority rows; avoid colliding with this page's own reads.
      const p = await gateway.preview(actorId, skuId, mode, request.signal);
      if (!request.current() || !current()) return false;
      const list = await gateway.proposals(actorId, skuId, mode, null, request.signal);
      if (!request.current() || !current()) return false;
      const versions = await gateway.history(actorId, skuId, mode, null, request.signal);
      if (!request.current() || !current()) return false;
      setPreview(p); setProposals(list); setHistory(versions); setPrice(''); setReason(''); setReference(''); return true;
    } catch (failure) { if (request.current()) failed(failure); }
    finally { if (request.current()) setDetailLoading(false); }
    return false;
  }
  function review(event: FormEvent, action: 'propose' | 'approve' | 'reject' | 'withdraw') {
    event.preventDefault(); if (locked || forbidden || !preview) return;
    try { setError(null); setNotice(''); setStaged(action === 'propose' ? preparePriceProposal(actorId, preview, currency, currency === 'IDR' ? rupiahAmount(price) ?? price : price, reason)
      : preparePriceDecision(actorId, selected, action, reference, reason)); } catch (failure) { failed(failure); }
  }
  async function confirm() {
    if (!staged || pending.current || forbidden || recovery.locked) return;
    pending.current = true; setBusy(true); setError(null); ledger.useSku(staged.skuId); const command = staged, operation = 'catalog.price.' + command.action;
    try {
      await submitTrackedRequest(ledger, recovery.journal, actorId, operation, operation, command, key => gateway.submit(command, key,
        () => current() && (command.action === 'propose' || command.action === 'withdraw' || sessionTiming(session, performance.now() - observedAt).fresh)));
      if (!current()) return; setStaged(null); const refreshed = await load(command.skuId);
      if (current()) setNotice(refreshed ? 'Permintaan tersimpan. Riwayat telah diperbarui.' : 'Permintaan tersimpan. Pilih kembali kemasan untuk memuat riwayat terbaru.');
    } catch (failure) {
      failed(failure);
      if (failure instanceof AccessFailure && failure.status === 409 && !ledger.isUncertain(operation)) setStaged(null);
    } finally { pending.current = false; if (alive.current) { setBusy(false); recovery.refresh(); } }
  }
  async function older(kind: 'proposals' | 'history') {
    if (!preview || locked || forbidden || detailLoading) return;
    const request = detailGate.begin(actorId, mode + ':' + preview.sku.id); setDetailLoading(true);
    try {
      if (kind === 'proposals') { const rows = await gateway.proposals(actorId, preview.sku.id, mode, proposals.at(-1)?.proposalId ?? null, request.signal); if (request.current()) { setProposals(rows); setSelected(null); } }
      else { const rows = await gateway.history(actorId, preview.sku.id, mode, history.at(-1)?.version ?? null, request.signal); if (request.current()) setHistory(rows); }
    } catch (failure) { if (request.current()) failed(failure); }
    finally { if (request.current()) setDetailLoading(false); }
  }
  const fact = staged?.action === 'propose' ? staged.preview : staged ? staged.proposal : null;
  return <>
    <AdministrationNavigation current="prices" actions={actions} locked={locked} />
    <section className={styles.accountSection}><h2>Ruang harga sesuai penugasan</h2><p>Setiap harga berlaku untuk satu SKU dan kemasan tertentu. Pengajuan belum mengubah harga yang berlaku.</p>
      <div className={styles.actions}>{actions.includes('pricing.manage') && <button className={mode === 'prepare' ? styles.primary : styles.secondary} disabled={locked} onClick={() => { clear(); setMode('prepare'); setAfter(null); }}>Ajukan harga</button>}
        {actions.includes('pricing.approve') && <button className={mode === 'review' ? styles.primary : styles.secondary} disabled={locked} onClick={() => { clear(); setMode('review'); setAfter(null); }}>Tinjau harga</button>}
        <button className={styles.secondary} disabled={busy || recovering} onClick={() => void onRefresh()}>Perbarui sesi dan akses</button></div>
      {(!timing.fresh && actions.includes('pricing.approve') || error instanceof AccessFailure && error.code === 'step_up_required') && <p className={styles.alert}>Verifikasi terbaru diperlukan. <Link href={factorHref}>Verifikasi dua langkah</Link></p>}
      {timing.expired && <p role="alert">Sesi telah berakhir. <Link href={loginHref}>Masuk kembali</Link></p>}
    </section>
    {error !== null && <p className={styles.alert} role="alert">{message(error)}</p>}{notice && <p className={styles.success} role="status">{notice}</p>}
    {recovery.error !== null && <p className={styles.alert} role="alert">{accessMessage(recovery.error)}</p>}
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy || timing.expired} onBusyChange={setRecovering} onFailure={failed}
      recoveryAdapters={{ 'catalog.price': { lookup: entry => gateway.receipt(entry, priceRecoverySku(entry.key)) } }}
      onResolved={() => { recovery.refresh(); ledger.resetAfterReconciliation(); clear(); setNotice('Hasil permintaan dipastikan. Pilih kembali kemasan untuk melihat riwayat terbaru.'); setRefresh(value => value + 1); }} />
    {!forbidden && <div className={styles.desk}>
      <section className={styles.queue} aria-busy={loading}><div className={styles.queueHeader}><h2>Pilih kemasan</h2>
        <form className={styles.lookup} onSubmit={event => { event.preventDefault(); if (locked) return; clear(); setAfter(null); setQuery(search.trim()); setRefresh(value => value + 1); }}>
          <label>Cari produk atau kode SKU<input value={search} maxLength={128} disabled={locked} onChange={event => setSearch(event.target.value)} /></label><button className={styles.secondary} disabled={locked || loading}>Cari</button></form></div>
        {loading ? <p className={styles.empty}>Memuat kemasan…</p> : choices.length === 0 ? <p className={styles.empty}>Tidak ada kemasan dalam penugasan atau pencarian ini.</p> :
          <ul className={styles.queueList}>{choices.map(item => <li key={item.sku.id}><button className={preview?.sku.id === item.sku.id ? styles.selectedRow : styles.queueRow} disabled={locked || detailLoading} onClick={() => void load(item.sku.id)}>
            <strong>{item.familyName}</strong><span>{item.sku.code} · {item.definition.packageLabel}</span><span>{item.definition.baseUnitsPerPackage} {item.definition.baseUnitCode} per kemasan{!item.sku.active ? ' · Nonaktif' : ''}</span></button></li>)}</ul>}
        <div className={styles.pagination}><button className={styles.secondary} disabled={locked || loading || after === null} onClick={() => { clear(); setAfter(null); }}>Awal daftar</button>
          <button className={styles.secondary} disabled={locked || loading || choices.length < 25} onClick={() => { clear(); setAfter(choices.at(-1)!.sku.id); }}>Berikutnya</button></div>
      </section>
      <section className={styles.detail} aria-busy={detailLoading}><div className={styles.form} ref={formRef} tabIndex={-1}>
        {!preview ? <p>{detailLoading ? 'Memuat harga dan riwayat…' : 'Pilih kemasan untuk melihat harga dan pengajuannya.'}</p> : <>
          <h2>{preview.sku.code} — {preview.definition.packageLabel}</h2><PackageFacts value={preview} />
          <dl className={styles.facts}><div><dt>Harga berlaku per kemasan</dt><dd>{amount(preview.currentPrice)}</dd></div><div><dt>Waktu persetujuan</dt><dd>{preview.currentPrice ? date(preview.currentPrice.effectiveAt) : 'Belum tersedia'}</dd></div></dl>
          <button className={styles.secondary} disabled={locked || detailLoading} onClick={() => void load(preview.sku.id)}>Muat ulang harga dan riwayat</button>
          {mode === 'prepare' && <form onSubmit={event => review(event, 'propose')}><fieldset className={styles.fields} disabled={locked || detailLoading || !preview.sku.active}>
            <legend><h3>Harga baru per kemasan</h3></legend><div className={styles.twoFields}><label>Mata uang<input value={currency} maxLength={3} onChange={event => setCurrency(event.target.value.toUpperCase())} required /></label>
              <label>Harga per kemasan<input inputMode="decimal" value={price} onChange={event => setPrice(event.target.value)} placeholder="Contoh: 12.000" required /></label>{currency === 'IDR' && price.trim() && <small>{rupiahAmount(price) === null ? 'Periksa angka harga, misalnya 12.000 atau 12000,50.' : 'Akan dicatat: ' + formatRupiah(rupiahAmount(price)!, { decimals: 2 })}</small>}</div>
            <label>Alasan pengajuan<textarea value={reason} maxLength={1000} onChange={event => setReason(event.target.value)} required /></label><button className={styles.primary}>Tinjau pengajuan harga</button>
          </fieldset></form>}
          <section><h3>Pengajuan harga</h3>{proposals.length === 0 ? <p>Belum ada pengajuan pada halaman ini.</p> : <ul className={styles.queueList}>{proposals.map(item => <li key={item.proposalId}><button className={selected?.proposalId === item.proposalId ? styles.selectedRow : styles.queueRow} disabled={locked || detailLoading}
            onClick={() => { setSelected(item); setReason(''); setReference(''); }}><strong>{amount(item.input)}</strong><span>{status(item.status)} · {date(item.proposedAt)}{(() => { const change = priceChange(item.basePrice, item.input); return change.percent && change.kind !== 'same' ? ' · ' + (change.kind === 'up' ? '+' : '−') + change.percent + '%' : ''; })()}</span></button></li>)}</ul>}
            <button className={styles.secondary} disabled={locked || detailLoading || proposals.length < 25} onClick={() => void older('proposals')}>Pengajuan lebih lama</button></section>
          {selected && <section className={styles.review}><h3>Rincian pengajuan</h3><PackageFacts value={selected} /><PriceComparison base={selected.basePrice} proposed={selected.input} />
            <p>Diajukan {selected.ownProposal ? 'oleh Anda' : 'oleh rekan lain'} · {date(selected.proposedAt)}</p>
            <p className={styles.preserve}>{selected.reason}</p><p>{status(selected.status)}</p>{selected.decision && <p>{selected.decision.reviewReference} · {selected.decision.reason} · {date(selected.decision.decidedAt)}</p>}
            {selected.status === 'proposed' && mode === 'review' && selected.ownProposal && <p className={styles.help}>Anda yang mengajukan harga ini. Persetujuan atau penolakan harus dilakukan orang lain yang terverifikasi.</p>}
            {selected.status === 'proposed' && !(mode === 'review' && selected.ownProposal) && <form onSubmit={event => review(event, mode === 'review' ? 'approve' : 'withdraw')}><fieldset className={styles.fields} disabled={locked || detailLoading}>
              <label>Acuan pemeriksaan<input value={reference} maxLength={160} onChange={event => setReference(event.target.value)} required /></label><label>Alasan keputusan<textarea value={reason} maxLength={1000} onChange={event => setReason(event.target.value)} required /></label>
              <p className={styles.help}>{mode === 'review' ? 'Persetujuan dan penolakan harus dilakukan orang terverifikasi yang berbeda dari pengaju.' : 'Hanya akun pengaju asli yang dapat menarik pengajuan ini.'}</p>
              <div className={styles.actions}><button className={styles.primary}>{mode === 'review' ? 'Tinjau persetujuan' : 'Tinjau penarikan'}</button>{mode === 'review' && <button type="button" className={styles.danger} onClick={event => review(event, 'reject')}>Tinjau penolakan</button>}</div>
            </fieldset></form>}</section>}
          {staged && fact && <div ref={reviewRef} tabIndex={-1} className={styles.preview} role="region" aria-label="Konfirmasi harga"><h3>Periksa sebelum mengirim</h3><PackageFacts value={fact} />
            <p>Tindakan: <strong>{{ propose: 'Ajukan harga', approve: 'Setujui harga', reject: 'Tolak pengajuan', withdraw: 'Tarik pengajuan' }[staged.action]}</strong></p>
            <PriceComparison base={staged.action === 'propose' ? staged.preview.currentPrice : staged.proposal.basePrice} proposed={staged.action === 'propose' ? staged.body.input : staged.proposal.input} />
            {staged.action !== 'propose' && <p>Acuan: {staged.body.reviewReference}</p>}<p className={styles.preserve}>Alasan: {staged.body.reason}</p>
            <div className={styles.actions}><button className={styles.primary} disabled={busy || recovering || recovery.locked} onClick={() => void confirm()}>{busy ? 'Menyimpan…' : 'Konfirmasi dan kirim'}</button>
              <button className={styles.secondary} disabled={busy || recovering || recovery.locked} onClick={() => { setStaged(null); formRef.current?.focus(); }}>Kembali ke isian</button></div></div>}
          <section><h3>Riwayat harga disetujui</h3>{history.length === 0 ? <p>Belum ada harga disetujui pada halaman ini.</p> : <ul className={styles.historyList}>{history.map(item => <li key={item.id}><strong>{amount(item)}</strong><p>Versi {item.version} · {date(item.effectiveAt)}</p></li>)}</ul>}
            <button className={styles.secondary} disabled={locked || detailLoading || history.length < 25} onClick={() => void older('history')}>Harga lebih lama</button></section>
        </>}
      </div></section>
    </div>}
  </>;
}
function PackageFacts({ value }: { value: Pick<PricePreview, 'sku' | 'definition'> }) {
  const d = value.definition;
  return <dl className={styles.facts}><div><dt>Kode dan kemasan</dt><dd>{value.sku.code} · {d.packageLabel}</dd></div><div><dt>Isi per kemasan</dt><dd>{d.baseUnitsPerPackage} {d.baseUnitCode}</dd></div>
    <div><dt>Jenis kemasan</dt><dd>{{ sealed: 'Kemasan tersegel', loose: 'Satuan lepas', other: 'Kemasan lain' }[d.packageKind]}</dd></div><div><dt>Pemesanan</dt><dd>Minimum {d.minimumOrderPackages}; kelipatan {d.orderIncrementPackages} kemasan</dd></div>
    {d.attributes.map(item => <div key={item.key}><dt>{item.key}</dt><dd>{item.value}</dd></div>)}</dl>;
}
