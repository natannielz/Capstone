'use client';

import Link from 'next/link';
import { useCallback, useEffect, useId, useRef, useState } from 'react';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure } from '../administration/workflow';
import { CatalogReadGate } from '../catalog/catalog-editor-state';
import { createOrderDraftGateway, type OrderDraft } from './order-draft-client';
import { createOrderQuoteGateway, orderContextKey, type OrderContextOption, type OrderQuote } from './order-quote-client';
import { draftMessage, useDraftSave } from './DraftSave';
import { OrderDraftEditor } from './OrderDraftEditor';
import common from '../administration/access.module.css';
import styles from './order-draft.module.css';
import { formatDateTime } from '@/lib/format';

type Account = { actorId: string; session: IdentitySession; observedAt: number; options: OrderContextOption[] };
export const branchLabel = (option: OrderContextOption) => `${option.organizationName ?? 'Organisasi ' + option.context.organizationId} · ${option.branchName ?? 'Cabang ' + option.context.branchId}${option.context.divisionId ? ' · ' + (option.divisionName ?? 'Divisi ' + option.context.divisionId) : ''}`;

export function OrderDraftWorkspace() {
  const [identity] = useState(() => createIdentityGateway()), [access] = useState(() => createAccessGateway()), [quotes] = useState(() => createOrderQuoteGateway());
  const [account, setAccount] = useState<Account | null>(null), [error, setError] = useState<unknown>(null), [loading, setLoading] = useState(true), [revision, setRevision] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => {
    const controller = new AbortController(); let active = true;
    void Promise.resolve().then(async () => {
      if (!active) return; setLoading(true); setAccount(null); setError(null);
      try {
        const session = await identity.session(controller.signal), observedAt = performance.now(); if (!active) return;
        if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
        const current = await access.current(); if (!active) return;
        if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
        const options = await quotes.contexts(current.userId, controller.signal); if (!active) return;
        if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
        setAccount({ actorId: current.userId, session, observedAt, options }); setElapsed(performance.now() - observedAt);
      } catch (failure) { if (active) setError(failure); }
      finally { if (active) setLoading(false); }
    });
    return () => { active = false; controller.abort(); };
  }, [identity, access, quotes, revision]);
  useEffect(() => {
    const timer = window.setInterval(() => { if (account) setElapsed(performance.now() - account.observedAt); }, 1000);
    const restored = (event: PageTransitionEvent) => { if (event.persisted) setRevision(value => value + 1); };
    window.addEventListener('pageshow', restored);
    return () => { window.clearInterval(timer); window.removeEventListener('pageshow', restored); };
  }, [account]);
  const expired = account !== null && sessionTiming(account.session, elapsed).expired;
  const failed = useCallback((failure: unknown) => {
    if (failure instanceof AccessFailure && ([401, 403].includes(failure.status) || failure.code === 'actor_changed')) { setAccount(null); setError(failure); }
  }, []);
  return <main className={common.page}><header className={common.header}><div><Link className={common.brand} href="/identity/account">Unit Toko</Link>
    <h1>Draf pesanan</h1><p>Susun kebutuhan cabang dan lanjutkan kapan saja. Draf hanya terlihat oleh akun pembuatnya.</p></div>
    <nav className={common.actions}><Link className={common.secondary} href="/catalog/browse">Katalog kemasan</Link><Link className={common.secondary} href="/identity/account">Akun saya</Link></nav></header>
    {error !== null && <p role="alert" className={common.alert}>{draftMessage(error, true)}</p>}
    {loading ? <p role="status">Memeriksa akun dan cabang…</p> : !account || expired ? <section className={common.state}><h2>Periksa sesi dan akses</h2>
      {expired && <p>Sesi telah berakhir.</p>}<Link href="/identity/login?returnPath=%2Fordering%2Fdrafts">Masuk kembali</Link>
      <button className={common.secondary} onClick={() => setRevision(value => value + 1)}>Perbarui sesi dan akses</button></section>
      : <DraftDesk key={account.actorId + ':' + account.observedAt} account={account} onFailure={failed} />}
  </main>;
}

function DraftDesk({ account, onFailure }: { account: Account; onFailure: (error: unknown) => void }) {
  const branchLabelId = useId();
  const { actorId, options } = account, [gateway] = useState(() => createOrderDraftGateway()), [quotes] = useState(() => createOrderQuoteGateway()), [gate] = useState(() => new CatalogReadGate());
  const [branch, setBranch] = useState(''), [rows, setRows] = useState<OrderDraft[]>([]), [hasMore, setHasMore] = useState(false), [after, setAfter] = useState<string | null>(null);
  const [loaded, setLoaded] = useState<{ draft: OrderDraft; quote: OrderQuote | null; issue: unknown; sequence: number } | null>(null);
  const [loading, setLoading] = useState(false), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState('');
  const alive = useRef(false), pending = useRef(false), sequence = useRef(0);
  const current = () => alive.current && !sessionTiming(account.session, performance.now() - account.observedAt).expired;
  const save = useDraftSave(actorId, current, result => { setNotice('Draf tersimpan. Pesanan belum diajukan.'); void reload(null, result.draftId); }, onFailure, loading);
  useEffect(() => { alive.current = true; return () => { alive.current = false; gate.cancel(); }; }, [gate]);
  async function reload(cursor: string | null = null, draftId?: string, selectedBranch = branch) {
    if (!current() || pending.current) return;
    const option = options.find(value => orderContextKey(value.context) === selectedBranch);
    pending.current = true; setLoading(true); setError(null); setLoaded(null); setRows([]); setHasMore(false);
    const request = gate.begin(actorId, selectedBranch + ':' + (draftId ?? cursor));
    try {
      // Receipt recovery may precede branch selection. Read its owner-scoped draft to locate the branch.
      const recovered = draftId ? await gateway.read(actorId, draftId, request.signal) : null;
      if (!request.current() || !current()) return;
      const context = recovered?.input.context ?? option?.context; if (!context) return;
      const contextKey = orderContextKey(context);
      if (!options.some(value => orderContextKey(value.context) === contextKey)) throw new AccessFailure(403, 'order_scope_unavailable');
      setBranch(contextKey);
      const page = await gateway.list(actorId, context, cursor, request.signal); if (!request.current() || !current()) return;
      setRows(page.items); setHasMore(page.hasMore); setAfter(cursor);
      if (draftId) {
        const draft = recovered ?? await gateway.read(actorId, draftId, request.signal); if (!request.current() || !current()) return;
        if (orderContextKey(draft.input.context) !== contextKey) throw new AccessFailure(409, 'draft_context_changed');
        let quote: OrderQuote | null = null, issue: unknown = null;
        try { quote = await quotes.preview(actorId, draft.input.context, draft.input.selections, request.signal); }
        catch (failure) {
          if (failure instanceof AccessFailure && (failure.status === 401 || failure.code === 'actor_changed')) throw failure;
          issue = failure;
        }
        if (request.current() && current()) setLoaded({ draft, quote, issue, sequence: ++sequence.current });
      }
    } catch (failure) { if (request.current() && current()) { setError(failure); setRows([]); onFailure(failure); } }
    finally { pending.current = false; if (request.current()) setLoading(false); }
  }
  const locked = loading || save.locked;
  return <>
    <section className={common.accountSection}><h2>Kebutuhan cabang</h2><p className={common.help}>Penyimpanan draf tidak mengunci harga, memesan stok, atau memulai penagihan.</p>
      <label><span id={branchLabelId}>Cabang pemesan</span><select aria-labelledby={branchLabelId} value={branch} disabled={locked} onChange={event => { setBranch(event.target.value); setNotice(''); void reload(null, undefined, event.target.value); }}>
        <option value="">Pilih cabang</option>{options.map(option => <option key={orderContextKey(option.context)} value={orderContextKey(option.context)}>{branchLabel(option)}</option>)}</select></label>
      {options.length === 0 && <p>Belum ada cabang pemesan yang tersedia untuk akun Anda.</p>}</section>
    {save.recoveryView}
    {error !== null && <p role="alert" className={common.alert}>{draftMessage(error, true)}</p>}
    {notice && <p role="status" className={common.success}>{notice}</p>}
    {branch && <div className={styles.desk}><section className={common.queue} aria-label="Daftar draf" aria-busy={loading}><div className={common.queueHeader}><h2>Draf saya</h2>
      <button className={common.secondary} disabled={locked} onClick={() => void reload()}>Muat kembali draf</button></div>
      {loading ? <p className={styles.empty} role="status">Memuat draf…</p> : rows.length === 0 ? <p className={styles.empty}>Belum ada draf di halaman ini. Pilih kemasan dari katalog untuk memulai.</p> :
        <ul className={common.queueList}>{rows.map(draft => <li key={draft.id}><button className={loaded?.draft.id === draft.id ? common.selectedRow : common.queueRow} disabled={locked}
          onClick={() => { setNotice(''); void reload(after, draft.id); }}><strong>{draft.input.purchaseReference ?? 'Draf ' + draft.id}</strong>
          <span>{draft.input.selections.length} jenis kemasan · versi {draft.revision}</span><small>{formatDateTime(draft.updatedAt)}</small></button></li>)}</ul>}
      <div className={common.pagination}><button className={common.secondary} disabled={locked || after === null} onClick={() => void reload()}>Awal daftar</button>
        <button className={common.secondary} disabled={locked || !hasMore} onClick={() => void reload(rows.at(-1)!.id)}>Berikutnya</button></div></section>
      {loaded ? <OrderDraftEditor key={loaded.draft.id + ':' + loaded.draft.revision + ':' + loaded.sequence} actorId={actorId} draft={loaded.draft} initialQuote={loaded.quote} initialIssue={loaded.issue}
        locked={locked} isCurrent={current} onSubmit={save.submit} onFailure={onFailure} /> : <section className={common.state}><h2>Lanjutkan draf Anda</h2><p>Pilih draf untuk memeriksa kemasan, jumlah, dan rencana pengiriman.</p></section>}
    </div>}
  </>;
}
