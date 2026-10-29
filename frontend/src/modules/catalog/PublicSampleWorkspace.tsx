'use client';

import Link from 'next/link';
import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure, accessMessage } from '../administration/workflow';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { SessionAccessState } from '@/components/workspace-kit/session-access-state';
import { Toolbar } from '@/components/workspace-kit/toolbar';
import { dateTimeFormatter, formatNumber } from '@/lib/format';
import { createPublicSampleGateway, publicSamplePageSize, type PublicSampleItem, type PublicSamplePage, type PublicSampleProposal } from './public-sample-client';
import styles from '../administration/access.module.css';
import { Button, buttonClassName } from '@/components/workspace-kit/button';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { Section } from '@/components/workspace-kit/section';
import { StateView } from '@/components/workspace-kit/state-view';
import { DataList } from '@/components/workspace-kit/data-list';
import { Badge } from '@/components/workspace-kit/badge';

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
  return <DataList label="Barang katalog" items={[item]} getKey={value => value.familyId} {...sampleRows(() => staged, canPropose, onToggle)} />;
}

/** One product row (DataList, ui-standard §6.3): name and guest state as the title line, owner and packages as meta, the switch on the right. */
export function sampleRows(stagedFor: (item: PublicSampleItem) => boolean | undefined, canPropose: boolean, onToggle: (item: PublicSampleItem, show: boolean) => void) {
  return {
    renderTitle: (item: PublicSampleItem) => item.familyName,
    renderBadge: (item: PublicSampleItem) => <Badge status={item.isSample ? 'Tampil untuk tamu' : 'Hanya akun cabang'} />,
    renderMeta: (item: PublicSampleItem) => <>{item.ownerLabel} · {formatNumber(item.packages)} kemasan terbit{item.isSample && item.packages === 0 ? '; tamu belum melihatnya' : ''}</>,
    renderContent: (item: PublicSampleItem) => { const staged = stagedFor(item); return item.pending || staged !== undefined ? <>
      {item.pending && <p><Badge status="Menunggu tinjauan" /> Diajukan {item.pending.adding ? 'tampil untuk tamu' : 'tidak tampil lagi'}.</p>}
      {staged !== undefined && <p>Perubahan dipilih: {staged ? 'tampil untuk tamu' : 'tidak tampil lagi'}.</p>}</> : null; },
    renderActions: (item: PublicSampleItem) => { const shown = stagedFor(item) ?? item.isSample;
      return <Button role="switch" aria-checked={shown} variant="secondary" disabled={!canPropose || item.pending !== null}
        aria-label={'Tampilkan untuk tamu: ' + item.familyName} onClick={() => onToggle(item, !shown)}>
        <span aria-hidden="true">{shown ? '☑' : '☐'}</span> Tampilkan untuk tamu</Button>; },
  };
}

/** A proposal's row title. The server falls back to "Toko 5 (nama belum tersedia)" for an unnamed catalog owner; that placeholder is
 *  never a title (U-034): the row is then named for what it is, and the owner stays visible in the meta line. */
export function sampleOwnerTitle(ownerLabel: string): string {
  return /\(nama belum tersedia\)\s*$/.test(ownerLabel) ? 'Pengajuan contoh katalog' : 'Pengajuan ' + ownerLabel;
}

export function ProposalReview({ proposal, canDecide, busy, onDecide }: { proposal: PublicSampleProposal; canDecide: boolean; busy: boolean;
  onDecide: (proposal: PublicSampleProposal, approve: boolean, reason: string) => void }) {
  const [open, setOpen] = useState<'approve' | 'reject' | null>(null), [reason, setReason] = useState('');
  const blocked = approveBlock(proposal);
  function submit(event: FormEvent) { event.preventDefault(); if (open && reason.trim()) onDecide(proposal, open === 'approve', reason.trim()); }
  const placeholder = sampleOwnerTitle(proposal.ownerLabel) === 'Pengajuan contoh katalog';
  return <DataList label="Pengajuan contoh katalog" items={[proposal]} getKey={value => value.proposalId}
    renderTitle={value => sampleOwnerTitle(value.ownerLabel)} renderBadge={() => <Badge status="Menunggu tinjauan" />}
    renderMeta={value => <>{placeholder ? 'Pemilik katalog: ' + value.ownerLabel + ' · ' : ''}Diajukan {dateTimeFormatter.format(new Date(value.proposedAt))}. Alasan: {value.reason}</>}
    renderContent={() => <>
      <table className={styles.preview}>
        <thead><tr><th scope="col">Barang</th><th scope="col">Sekarang</th><th scope="col">Sesudah disetujui</th></tr></thead>
        <tbody>
          {proposal.adds.map(change => <tr key={'add-' + change.familyId}><td>{change.familyName}</td><td>Belum tampil</td><td>Tampil untuk tamu</td></tr>)}
          {proposal.removes.map(change => <tr key={'remove-' + change.familyId}><td>{change.familyName}</td><td>Tampil untuk tamu</td><td>Tidak tampil lagi</td></tr>)}
        </tbody>
      </table>
      {blocked && canDecide && <p id={'own-' + proposal.proposalId}>{blocked}</p>}
      {!canDecide && proposal.ownProposal && <p>{ownProposalNote}. Menunggu persetujuan orang lain.</p>}
      {open && <form className={styles.form} onSubmit={submit}>
        <label>Alasan<textarea required maxLength={1000} value={reason} onChange={event => setReason(event.target.value)}
          placeholder={open === 'approve' ? 'Contoh: barang umum, aman dilihat tamu' : 'Contoh: masih ada barang khusus kontrak'} /></label>
        <div className="ui-actions">
          <Button type="submit" variant="primary" disabled={busy || !reason.trim()}>{busy ? 'Mengirim…' : open === 'approve' ? 'Setujui perubahan' : 'Tolak pengajuan'}</Button>
          <Button variant="secondary" disabled={busy} onClick={() => setOpen(null)}>Batal</Button>
        </div>
      </form>}</>}
    renderActions={() => canDecide ? <>
      <Button variant="secondary" disabled={busy || blocked !== null} aria-describedby={blocked ? 'own-' + proposal.proposalId : undefined}
        onClick={() => { setOpen('approve'); setReason(''); }}>Setujui</Button>
      <Button variant="secondary" disabled={busy || blocked !== null} onClick={() => { setOpen('reject'); setReason(''); }}>Tolak</Button></> : null} />;
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
    <PageHeader title={publicSampleTitle} description="Pilih barang yang boleh dilihat tamu sebagai contoh. Harga tidak pernah tampil; setiap perubahan disetujui orang kedua."
      secondaryAction={<Link href="/katalog" className={buttonClassName({ variant: 'secondary' })} target="_blank" rel="noopener">Lihat katalog tamu</Link>} />
    <div className="ui-stack">{loading ? <StateView kind="loading" title="Memeriksa akun…" />
      : loaded ? <SampleDesk key={loaded.actorId + ':' + loaded.observedAt} {...loaded} onLost={lost} />
        : <SessionAccessState error={error} returnPath={publicSamplePath} onRetry={() => setAttempt(value => value + 1)} />}</div>
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
  return <>
    {notice && <p role="status" className={styles.success}>{notice}</p>}
    {error !== null && <StateView kind="error" title={sampleMessage(error)} action={stepUp ? <Link className={buttonClassName({ variant: 'secondary' })} href={factorHref}>Verifikasi dua langkah</Link> : undefined} />}
    {page && page.proposals.length > 0 && <Section title="Menunggu tinjauan" id="sample-review"><div className="ui-stack">
      {page.proposals.map(proposal => <ProposalReview key={proposal.proposalId} proposal={proposal} canDecide={canDecide} busy={busy}
        onDecide={(item, approve, why) => void send(approve ? 'approve' : 'reject', item.proposalId,
          key => gateway.decide(actorId, item.proposalId, approve, why, key),
          approve ? 'Disetujui. Tamu melihat perubahan ini pada kunjungan berikutnya.' : 'Pengajuan ditolak. Contoh katalog tidak berubah.')} />)}
    </div></Section>}
    <Section title="Daftar barang" id="sample-heading" busy={state === 'loading'}>
      <Toolbar label="Cari barang contoh" resultText={page ? <>{formatNumber(page.sampleCount)} dari {formatNumber(page.sampleLimit)} contoh dipakai.</> : undefined}
        search={<form className={styles.lookup} role="search" onSubmit={event => { event.preventDefault(); setOffset(0); setTerm(search.trim()); }}>
          <label>Cari barang<input type="search" maxLength={64} value={search} onChange={event => setSearch(event.target.value)} placeholder="Nama barang" /></label>
          <Button type="submit" variant="secondary">Cari</Button>
        </form>} />
      {state === 'loading' && !page ? <StateView kind="loading" title="Memuat barang…" />
        : state === 'error' && !page ? <StateView kind="error" title="Daftar barang belum dapat dimuat" action={<Button onClick={() => setReload(value => value + 1)}>Coba lagi</Button>} />
        : page && page.items.length === 0 ? <StateView kind="empty" title={term ? 'Barang tidak ditemukan' : 'Belum ada barang'}
          message={term ? 'Coba kata lain.' : 'Contoh dipilih dari barang katalog yang sudah dibuat di Katalog dan persediaan.'} />
        : page && <DataList label="Daftar barang" items={page.items} getKey={item => item.familyId}
          {...sampleRows(item => staged.get(item.familyId)?.show, canPropose, (row, show) => setStaged(current => stageToggle(current, row, show)))}
          pagination={{ label: 'Halaman barang', start: offset + 1, end: offset + page.items.length, total: page.total, disabled: state === 'loading',
            hasPrevious: page.total > publicSamplePageSize && offset > 0, hasNext: page.total > publicSamplePageSize && offset + page.items.length < page.total,
            onPrevious: () => setOffset(value => Math.max(0, value - publicSamplePageSize)), onNext: () => setOffset(value => value + publicSamplePageSize) }} />}
    </Section>
    {canPropose && staged.size > 0 && <form className="ui-section" aria-label="Pengajuan contoh katalog" onSubmit={event => {
      event.preventDefault();
      void send('propose', target, key => gateway.propose(actorId, change, reason.trim(), key), 'Pengajuan terkirim. Orang kedua perlu menyetujuinya sebelum tamu melihat perubahan.');
    }}>
      <p>{change.adds.length} barang ditampilkan, {change.removes.length} barang tidak ditampilkan lagi.</p>
      {mixed && <p role="alert">{sampleMessage(new AccessFailure(400, 'catalog_public_sample_owner_mixed'))}</p>}
      {over && <p role="alert">{sampleMessage(new AccessFailure(409, 'public_sample_limit'))}</p>}
      <label>Alasan<textarea required maxLength={1000} value={reason} onChange={event => setReason(event.target.value)} placeholder="Contoh: barang umum yang aman dilihat tamu" /></label>
      <div className={styles.actions}>
        <Button type="submit" variant="primary" disabled={busy || mixed || over || !reason.trim()}>{busy ? 'Mengirim…' : command?.kind === 'propose' && command.target === target ? 'Kirim lagi' : 'Kirim pengajuan'}</Button>
        <Button variant="secondary" disabled={busy} onClick={() => { setStaged(new Map()); setCommand(null); }}>Batalkan pilihan</Button>
      </div>
    </form>}
  </>;
}
