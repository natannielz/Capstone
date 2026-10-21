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
import { createPublicShowcaseGateway, type PublicShowcase } from './public-showcase-client';
import styles from '../administration/access.module.css';

export const publicShowcasePath = '/catalog/public';
const factorHref = '/identity/account?operation=catalog.public.approve&returnPath=' + encodeURIComponent(publicShowcasePath);
type Loaded = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
/** One command in flight. Its key is kept until the server answers, so sending again after a lost answer replays instead of repeating. */
type Staged = { assortmentId: string; kind: 'propose' | 'approve' | 'reject'; key: string };

export function showcaseMessage(error: unknown): string {
  if (error instanceof AccessFailure) {
    if (error.code === 'step_up_required') return 'Verifikasi dua langkah terbaru diperlukan sebelum menyetujui katalog publik.';
    if (error.status === 409) return 'Katalog ini baru saja berubah. Muat ulang daftar, lalu tinjau kembali.';
    if (error.status === 403) return 'Tindakan tidak diizinkan. Persetujuan harus dari orang yang berbeda dengan pengaju dan masih memiliki penugasan.';
    return accessMessage(error);
  }
  return 'Jawaban server belum diterima. Kirim lagi: permintaan yang sama tidak akan tercatat dua kali.';
}

/** What the person in front of this row can do now; the server still decides. */
export function showcaseChoice(row: PublicShowcase, actions: readonly string[]): 'propose' | 'decide' | 'own-proposal' | 'waiting' | 'none' {
  if (row.pending) {
    if (row.pending.ownProposal) return 'own-proposal';
    return actions.includes('catalog.public.approve') ? 'decide' : 'waiting';
  }
  return actions.includes('catalog.public.propose') ? 'propose' : 'none';
}

export function PublicShowcaseWorkspace() {
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
    <header className={styles.header}><div><h1>Katalog publik</h1>
      <p>Tentukan katalog yang boleh dilihat pengunjung yang belum masuk. Harga tidak pernah tampil; setiap perubahan disetujui orang kedua.</p></div>
      <Link href="/katalog" className={styles.secondary} target="_blank" rel="noopener">Lihat katalog publik</Link></header>
    {loading ? <p role="status">Memeriksa akun…</p>
      : loaded ? <ShowcaseDesk key={loaded.actorId + ':' + loaded.observedAt} {...loaded} onLost={lost} />
        : <SessionAccessState error={error} returnPath={publicShowcasePath} onRetry={() => setAttempt(value => value + 1)} />}
  </main>;
}

function ShowcaseDesk({ actorId, actions, session, observedAt, onLost }: Loaded & { onLost: (failure: unknown) => void }) {
  const [gateway] = useState(() => createPublicShowcaseGateway());
  const [rows, setRows] = useState<PublicShowcase[]>([]), [state, setState] = useState<'loading' | 'ready' | 'error'>('loading'), [reload, setReload] = useState(0);
  const [open, setOpen] = useState<{ assortmentId: string; kind: Staged['kind'] } | null>(null), [reason, setReason] = useState('');
  const [staged, setStaged] = useState<Staged | null>(null), [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState('');
  useEffect(() => {
    const controller = new AbortController();
    void Promise.resolve().then(async () => {
      setState('loading');
      try { const items = await gateway.list(actorId, controller.signal); if (!controller.signal.aborted) { setRows(items); setState('ready'); } }
      catch (failure) { if (!controller.signal.aborted) { setError(failure); setState('error'); onLost(failure); } }
    });
    return () => controller.abort();
  }, [gateway, actorId, reload, onLost]);
  function begin(assortmentId: string, kind: Staged['kind']) { setOpen({ assortmentId, kind }); setReason(''); setError(null); setNotice(''); }
  async function submit(event: FormEvent, row: PublicShowcase) {
    event.preventDefault(); if (!open || busy) return;
    if (sessionTiming(session, performance.now() - observedAt).expired) { onLost(new IdentityFailure(401, 'session_expired')); return; }
    const command = staged && staged.assortmentId === row.assortmentId && staged.kind === open.kind ? staged : { assortmentId: row.assortmentId, kind: open.kind, key: crypto.randomUUID() };
    setStaged(command); setBusy(true); setError(null);
    try {
      if (command.kind === 'propose') await gateway.propose(actorId, row, !row.isPublic, reason.trim(), command.key);
      else await gateway.decide(actorId, row.pending!.proposalId, command.kind === 'approve', reason.trim(), command.key);
      setStaged(null); setOpen(null); setReason('');
      setNotice(command.kind === 'propose' ? 'Pengajuan terkirim. Orang kedua perlu menyetujuinya sebelum pengunjung melihat perubahan.'
        : command.kind === 'approve' ? 'Disetujui. Perubahan tampil di katalog publik dalam satu menit.' : 'Pengajuan ditolak. Katalog publik tidak berubah.');
      setReload(value => value + 1);
    } catch (failure) {
      // A clear refusal ends this command; only an unanswered request keeps its key for an exact retry.
      if (failure instanceof AccessFailure) setStaged(null);
      setError(failure); onLost(failure);
    } finally { setBusy(false); }
  }
  const stepUp = error instanceof AccessFailure && error.code === 'step_up_required';
  if (state === 'loading') return <p role="status">Memuat katalog…</p>;
  if (state === 'error') return <section className={styles.state}><h2>Daftar katalog belum dapat dimuat</h2><p role="alert">{error instanceof AccessFailure ? showcaseMessage(error) : 'Periksa koneksi, lalu coba lagi.'}</p>
    <div className={styles.actions}><button type="button" className={styles.primary} onClick={() => setReload(value => value + 1)}>Coba lagi</button></div></section>;
  if (rows.length === 0) return <EmptyState kind="first-use" title="Belum ada katalog toko" body="Katalog publik dipilih dari katalog toko yang sudah berisi produk. Buat katalog toko lebih dulu di Katalog dan persediaan." />;
  return <section aria-labelledby="showcase-heading">
    <h2 id="showcase-heading" className={styles.srOnly}>Daftar katalog toko</h2>
    {notice && <p role="status" className={styles.success}>{notice}</p>}
    {error !== null && <p role="alert" className={styles.alert}>{showcaseMessage(error)}{stepUp && <> <Link href={factorHref}>Verifikasi dua langkah</Link></>}</p>}
    <ul className={styles.queueList}>
      {rows.map(row => {
        const choice = showcaseChoice(row, actions), editing = open?.assortmentId === row.assortmentId;
        return <li key={row.assortmentId} className={styles.queueRow}>
          <div><strong>{row.ownerLabel}</strong> <StatusBadge text={row.isPublic ? 'Terbuka untuk umum' : 'Hanya akun cabang'} />
            <p>{formatNumber(row.listedProducts)} produk di katalog ini</p>
            {row.pending && <p><StatusBadge text="Menunggu tinjauan" /> Diajukan {row.pending.makePublic ? 'dibuka untuk umum' : 'ditutup dari umum'} pada {dateTimeFormatter.format(new Date(row.pending.proposedAt))}. Alasan: {row.pending.reason}</p>}
          </div>
          <div className={styles.actions}>
            {choice === 'propose' && !editing && <button type="button" className={styles.secondary} disabled={busy} onClick={() => begin(row.assortmentId, 'propose')}>{row.isPublic ? 'Ajukan tutup dari umum' : 'Ajukan buka untuk umum'}</button>}
            {choice === 'decide' && !editing && <><button type="button" className={styles.primary} disabled={busy} onClick={() => begin(row.assortmentId, 'approve')}>Setujui</button>
              <button type="button" className={styles.secondary} disabled={busy} onClick={() => begin(row.assortmentId, 'reject')}>Tolak</button></>}
            {choice === 'own-proposal' && <p>Menunggu persetujuan orang lain.</p>}
            {choice === 'waiting' && <p>Menunggu peninjau katalog publik.</p>}
          </div>
          {editing && open && <form className={styles.form} onSubmit={event => void submit(event, row)}>
            <label>Alasan<textarea required maxLength={1000} value={reason} onChange={event => setReason(event.target.value)}
              placeholder={open.kind === 'propose' ? 'Contoh: barang umum yang aman dilihat pengunjung' : open.kind === 'approve' ? 'Contoh: isi katalog sudah diperiksa' : 'Contoh: masih ada barang khusus kontrak'} /></label>
            <div className={styles.actions}>
              <button type="submit" className={styles.primary} disabled={busy || !reason.trim()}>{busy ? 'Mengirim…'
                : staged?.assortmentId === row.assortmentId && staged.kind === open.kind ? 'Kirim lagi'
                  : open.kind === 'propose' ? 'Kirim pengajuan' : open.kind === 'approve' ? 'Setujui perubahan' : 'Tolak pengajuan'}</button>
              <button type="button" className={styles.secondary} disabled={busy} onClick={() => { setOpen(null); setStaged(null); }}>Batal</button>
            </div>
          </form>}
        </li>;
      })}
    </ul>
  </section>;
}
