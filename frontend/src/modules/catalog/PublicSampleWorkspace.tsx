'use client';

import Link from 'next/link';
import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure, accessMessage } from '../administration/workflow';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { SessionAccessState } from '@/components/workspace-kit/session-access-state';
import { StatusBadge } from '@/components/workspace-kit/status-badge';
import { EmptyState } from '@/components/workspace-kit/empty-state';
import { dateTimeFormatter, formatNumber } from '@/lib/format';
import { createPublicSampleGateway, publicSamplePageSize, type PublicSampleItem, type PublicSamplePage, type PublicSampleProposal } from './public-sample-client';
import styles from '../administration/access.module.css';

export const publicSamplePath = '/catalog/public';
export const publicSampleTitle = 'Contoh katalog untuk tamu';
const ownProposalNote = 'Anda yang mengajukan perubahan ini';
const factorHref = '/identity/account?operation=catalog.public.approve&returnPath=' + encodeURIComponent(publicSamplePath);
type Loaded = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
/** Products whose guest state the maker changed, with the state they want. Sent together as one proposal. */
export type StagedSamples = ReadonlyMap<string, { item: PublicSampleItem; show: boolean }>;
/** One command in flight. Its key is kept until the server answers, so sending again after a lost answer replays instead of repeating. */
type Staged = { target: string; kind: 'propose' | 'approve' | 'reject'; key: string };

const expired = (session: IdentitySession, observedAt: number) => sessionTiming(session, performance.now() - observedAt).expired;

export function stageToggle(staged: StagedSamples, item: PublicSampleItem, show: boolean): StagedSamples {
  const next = new Map(staged);
  if (show === item.isSample) next.delete(item.familyId); else next.set(item.familyId, { item, show });
  return next;
}

export function proposalFrom(staged: StagedSamples): { adds: string[]; removes: string[] } {
  const entries = [...staged.values()];
  return { adds: entries.filter(entry => entry.show).map(entry => entry.item.familyId), removes: entries.filter(entry => !entry.show).map(entry => entry.item.familyId) };
}

/** The server takes one catalog owner per proposal; the page says so before sending. */
export function mixedOwners(items: readonly PublicSampleItem[]): boolean { return new Set(items.map(item => item.ownerLabel)).size > 1; }

/** Whether approving would grow the guest sample past its limit. Narrowing is always allowed. */
export function wouldExceed(sampleCount: number, staged: StagedSamples, limit: number): boolean {
  const { adds, removes } = proposalFrom(staged);
  return adds.length > removes.length && sampleCount + adds.length - removes.length > limit;
}

/** Why Setujui is unavailable to this person, or null when they may decide. The server enforces the same rule. */
export function approveBlock(proposal: PublicSampleProposal): string | null { return proposal.ownProposal ? ownProposalNote : null; }

export function sampleMessage(error: unknown): string {
  if (error instanceof AccessFailure) {
    if (error.code === 'step_up_required') return 'Verifikasi dua langkah terbaru diperlukan sebelum menyetujui contoh katalog.';
    if (error.code === 'self_approval' || error.code === 'independent_actor_required' || error.code === 'approval_independence_required')
      return 'Persetujuan harus dari orang yang berbeda dengan pengaju.';
    if (error.code === 'public_sample_limit') return 'Tamu dapat melihat paling banyak 200 contoh barang. Hapus beberapa contoh lebih dulu.';
    if (error.code === 'catalog_public_sample_pending') return 'Sebagian barang ini masih menunggu tinjauan. Tunggu keputusan pengajuan itu lebih dulu.';
    if (error.code === 'catalog_public_sample_owner_mixed') return 'Ajukan barang dari satu pemilik katalog dalam satu pengajuan.';
    if (error.status === 409) return 'Contoh katalog baru saja berubah. Muat ulang daftar, lalu tinjau kembali.';
    if (error.status === 403) return 'Tindakan tidak diizinkan untuk akun ini.';
    return accessMessage(error);
  }
  return 'Jawaban server belum diterima. Kirim lagi: permintaan yang sama tidak akan tercatat dua kali.';
}

export function SampleRow({ item, staged, canPropose, onToggle }: { item: PublicSampleItem; staged: boolean | undefined; canPropose: boolean;
  onToggle: (item: PublicSampleItem, show: boolean) => void }) {
  const shown = staged ?? item.isSample;
  return <li className={styles.queueRow}>
    <div><strong>{item.familyName}</strong> <StatusBadge text={item.isSample ? 'Tampil untuk tamu' : 'Hanya akun cabang'} />
      <p>{item.ownerLabel} · {formatNumber(item.packages)} kemasan terbit{item.isSample && item.packages === 0 ? '; tamu belum melihatnya' : ''}</p>
      {item.pending && <p><StatusBadge text="Menunggu tinjauan" /> Diajukan {item.pending.adding ? 'tampil untuk tamu' : 'tidak tampil lagi'}.</p>}
      {staged !== undefined && <p>Perubahan dipilih: {staged ? 'tampil untuk tamu' : 'tidak tampil lagi'}.</p>}
    </div>
    <div className={styles.actions}>
      <button type="button" role="switch" aria-checked={shown} className={styles.secondary} disabled={!canPropose || item.pending !== null}
        aria-label={'Tampilkan untuk tamu: ' + item.familyName} onClick={() => onToggle(item, !shown)}>
        <span aria-hidden="true">{shown ? '☑' : '☐'}</span> Tampilkan untuk tamu</button>
    </div>
  </li>;
}

export function ProposalReview({ proposal, canDecide, busy, onDecide }: { proposal: PublicSampleProposal; canDecide: boolean; busy: boolean;
  onDecide: (proposal: PublicSampleProposal, approve: boolean, reason: string) => void }) {
  const [open, setOpen] = useState<'approve' | 'reject' | null>(null), [reason, setReason] = useState('');
  const blocked = approveBlock(proposal);
  function submit(event: FormEvent) { event.preventDefault(); if (open && reason.trim()) onDecide(proposal, open === 'approve', reason.trim()); }
  return <li className={styles.queueRow}>
    <div><strong>Pengajuan {proposal.ownerLabel}</strong> <StatusBadge text="Menunggu tinjauan" />
      <p>Diajukan {dateTimeFormatter.format(new Date(proposal.proposedAt))}. Alasan: {proposal.reason}</p>
      <table className={styles.preview}>
        <thead><tr><th scope="col">Barang</th><th scope="col">Sekarang</th><th scope="col">Sesudah disetujui</th></tr></thead>
        <tbody>
          {proposal.adds.map(change => <tr key={'add-' + change.familyId}><td>{change.familyName}</td><td>Belum tampil</td><td>Tampil untuk tamu</td></tr>)}
          {proposal.removes.map(change => <tr key={'remove-' + change.familyId}><td>{change.familyName}</td><td>Tampil untuk tamu</td><td>Tidak tampil lagi</td></tr>)}
        </tbody>
      </table>
    </div>
    {canDecide && <div className={styles.actions}>
      <button type="button" className={styles.primary} disabled={busy || blocked !== null} aria-describedby={blocked ? 'own-' + proposal.proposalId : undefined}
        onClick={() => { setOpen('approve'); setReason(''); }}>Setujui</button>
      <button type="button" className={styles.secondary} disabled={busy || blocked !== null} onClick={() => { setOpen('reject'); setReason(''); }}>Tolak</button>
      {blocked && <p id={'own-' + proposal.proposalId}>{blocked}</p>}
    </div>}
    {!canDecide && proposal.ownProposal && <p>{ownProposalNote}. Menunggu persetujuan orang lain.</p>}
    {open && <form className={styles.form} onSubmit={submit}>
      <label>Alasan<textarea required maxLength={1000} value={reason} onChange={event => setReason(event.target.value)}
        placeholder={open === 'approve' ? 'Contoh: barang umum, aman dilihat tamu' : 'Contoh: masih ada barang khusus kontrak'} /></label>
      <div className={styles.actions}>
        <button type="submit" className={styles.primary} disabled={busy || !reason.trim()}>{busy ? 'Mengirim…' : open === 'approve' ? 'Setujui perubahan' : 'Tolak pengajuan'}</button>
        <button type="button" className={styles.secondary} disabled={busy} onClick={() => setOpen(null)}>Batal</button>
      </div>
    </form>}
  </li>;
}

export function PublicSampleWorkspace() {
  const [identity] = useState(createIdentityGateway), [access] = useState(createAccessGateway);
  const [loaded, setLoaded] = useState<Loaded | null>(null), [error, setError] = useState<unknown>(null), [loading, setLoading] = useState(true), [attempt, setAttempt] = useState(0);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setLoading(true); setLoaded(null); setError(null);
      try {
        const session = await identity.session(controller.signal), observedAt = performance.now();
        if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
        const current = await access.current(); if (!active) return;
        if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
        if (!current.allowedActions.some(action => action === 'catalog.public.propose' || action === 'catalog.public.approve')) throw new AccessFailure(403, 'scope_denied');
        setLoaded({ actorId: current.userId, actions: current.allowedActions, session, observedAt });
      } catch (failure) { if (active) setError(failure); } finally { if (active) setLoading(false); }
    });
    return () => { active = false; controller.abort(); };
  }, [identity, access, attempt]);
  const lost = useCallback((failure: unknown) => {
    if (failure instanceof AccessFailure && (failure.status === 401 || failure.code === 'actor_changed')) { setLoaded(null); setError(failure); }
  }, []);
  return <main className={styles.page}>
    <header className={styles.header}><div><h1>{publicSampleTitle}</h1>
      <p>Pilih barang yang boleh dilihat tamu sebagai contoh. Harga tidak pernah tampil; setiap perubahan disetujui orang kedua.</p></div>
      <Link href="/katalog" className={styles.secondary} target="_blank" rel="noopener">Lihat katalog tamu</Link></header>
    {loading ? <p role="status">Memeriksa akun…</p>
      : loaded ? <SampleDesk key={loaded.actorId + ':' + loaded.observedAt} {...loaded} onLost={lost} />
        : <SessionAccessState error={error} returnPath={publicSamplePath} onRetry={() => setAttempt(value => value + 1)} />}
  </main>;
}

function SampleDesk({ actorId, actions, session, observedAt, onLost }: Loaded & { onLost: (failure: unknown) => void }) {
  const [gateway] = useState(() => createPublicSampleGateway());
  const [search, setSearch] = useState(''), [term, setTerm] = useState(''), [offset, setOffset] = useState(0);
  const [page, setPage] = useState<PublicSamplePage | null>(null), [state, setState] = useState<'loading' | 'ready' | 'error'>('loading'), [reload, setReload] = useState(0);
  const [staged, setStaged] = useState<StagedSamples>(new Map()), [reason, setReason] = useState('');
  const [command, setCommand] = useState<Staged | null>(null), [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState('');
  const canPropose = actions.includes('catalog.public.propose'), canDecide = actions.includes('catalog.public.approve');
  useEffect(() => {
    const controller = new AbortController();
    void Promise.resolve().then(async () => {
      setState('loading');
      try { const result = await gateway.list(actorId, { search: term, offset }, controller.signal); if (!controller.signal.aborted) { setPage(result); setState('ready'); } }
      catch (failure) { if (!controller.signal.aborted) { setError(failure); setState('error'); onLost(failure); } }
    });
    return () => controller.abort();
  }, [gateway, actorId, term, offset, reload, onLost]);
  async function send(kind: Staged['kind'], target: string, run: (key: string) => Promise<unknown>, done: string) {
    if (busy) return;
    if (expired(session, observedAt)) { onLost(new IdentityFailure(401, 'session_expired')); return; }
    const current = command && command.kind === kind && command.target === target ? command : { kind, target, key: crypto.randomUUID() };
    setCommand(current); setBusy(true); setError(null); setNotice('');
    try {
      await run(current.key);
      setCommand(null); setNotice(done); setReload(value => value + 1);
      if (kind === 'propose') { setStaged(new Map()); setReason(''); }
    } catch (failure) {
      // A clear refusal ends this command; only an unanswered request keeps its key for an exact retry.
      if (failure instanceof AccessFailure) setCommand(null);
      setError(failure); onLost(failure);
    } finally { setBusy(false); }
  }
  const change = proposalFrom(staged), stagedItems = [...staged.values()].map(entry => entry.item);
  const mixed = mixedOwners(stagedItems), over = page ? wouldExceed(page.sampleCount, staged, page.sampleLimit) : false;
  const target = [...change.adds.map(id => '+' + id), ...change.removes.map(id => '-' + id)].sort().join(',');
  const stepUp = error instanceof AccessFailure && error.code === 'step_up_required';
  return <section aria-labelledby="sample-heading">
    <h2 id="sample-heading" className={styles.srOnly}>Daftar barang</h2>
    {notice && <p role="status" className={styles.success}>{notice}</p>}
    {error !== null && <p role="alert" className={styles.alert}>{sampleMessage(error)}{stepUp && <> <Link href={factorHref}>Verifikasi dua langkah</Link></>}</p>}
    {page && page.proposals.length > 0 && <section aria-labelledby="sample-review">
      <h3 id="sample-review">Menunggu tinjauan</h3>
      <ul className={styles.queueList}>{page.proposals.map(proposal => <ProposalReview key={proposal.proposalId} proposal={proposal} canDecide={canDecide} busy={busy}
        onDecide={(item, approve, why) => void send(approve ? 'approve' : 'reject', item.proposalId,
          key => gateway.decide(actorId, item.proposalId, approve, why, key),
          approve ? 'Disetujui. Tamu melihat perubahan ini pada kunjungan berikutnya.' : 'Pengajuan ditolak. Contoh katalog tidak berubah.')} />)}</ul>
    </section>}
    <form className={styles.lookup} role="search" onSubmit={event => { event.preventDefault(); setOffset(0); setTerm(search.trim()); }}>
      <label>Cari barang<input type="search" maxLength={64} value={search} onChange={event => setSearch(event.target.value)} placeholder="Nama barang" /></label>
      <button type="submit" className={styles.secondary}>Cari</button>
    </form>
    {page && <p>{formatNumber(page.sampleCount)} dari {formatNumber(page.sampleLimit)} contoh dipakai.</p>}
    {state === 'loading' && !page ? <p role="status">Memuat barang…</p>
      : state === 'error' && !page ? <section className={styles.state}><h2>Daftar barang belum dapat dimuat</h2>
        <div className={styles.actions}><button type="button" className={styles.primary} onClick={() => setReload(value => value + 1)}>Coba lagi</button></div></section>
      : page && page.items.length === 0 ? <EmptyState kind={term ? 'no-results' : 'first-use'} title={term ? 'Barang tidak ditemukan' : 'Belum ada barang'}
        body={term ? 'Coba kata lain.' : 'Contoh dipilih dari barang katalog yang sudah dibuat di Katalog dan persediaan.'} />
      : page && <>
        <ul className={styles.queueList} aria-busy={state === 'loading'}>
          {page.items.map(item => <SampleRow key={item.familyId} item={item} staged={staged.get(item.familyId)?.show} canPropose={canPropose}
            onToggle={(row, show) => setStaged(current => stageToggle(current, row, show))} />)}
        </ul>
        {page.total > publicSamplePageSize && <div className={styles.pagination}>
          <p>{formatNumber(offset + 1)}–{formatNumber(offset + page.items.length)} dari {formatNumber(page.total)} barang</p>
          <button type="button" className={styles.secondary} disabled={state === 'loading' || offset === 0}
            onClick={() => setOffset(value => Math.max(0, value - publicSamplePageSize))}>Sebelumnya</button>
          <button type="button" className={styles.secondary} disabled={state === 'loading' || offset + page.items.length >= page.total}
            onClick={() => setOffset(value => value + publicSamplePageSize)}>Berikutnya</button></div>}
      </>}
    {canPropose && staged.size > 0 && <form className={styles.form} aria-label="Pengajuan contoh katalog" onSubmit={event => {
      event.preventDefault();
      void send('propose', target, key => gateway.propose(actorId, change, reason.trim(), key), 'Pengajuan terkirim. Orang kedua perlu menyetujuinya sebelum tamu melihat perubahan.');
    }}>
      <p>{change.adds.length} barang ditampilkan, {change.removes.length} barang tidak ditampilkan lagi.</p>
      {mixed && <p role="alert">{sampleMessage(new AccessFailure(400, 'catalog_public_sample_owner_mixed'))}</p>}
      {over && <p role="alert">{sampleMessage(new AccessFailure(409, 'public_sample_limit'))}</p>}
      <label>Alasan<textarea required maxLength={1000} value={reason} onChange={event => setReason(event.target.value)} placeholder="Contoh: barang umum yang aman dilihat tamu" /></label>
      <div className={styles.actions}>
        <button type="submit" className={styles.primary} disabled={busy || mixed || over || !reason.trim()}>{busy ? 'Mengirim…' : command?.kind === 'propose' && command.target === target ? 'Kirim lagi' : 'Kirim pengajuan'}</button>
        <button type="button" className={styles.secondary} disabled={busy} onClick={() => { setStaged(new Map()); setCommand(null); }}>Batalkan pilihan</button>
      </div>
    </form>}
  </section>;
}
