'use client';

import { useEffect, useState } from 'react';
import { OutcomeState } from '@/components/workspace-kit/outcome-state';
import { AccessFailure } from '../administration/workflow';
import { createWorkSummaryGateway, visibleWorkItems, type WorkSummary as Summary } from './work-summary-client';
import { WorkQueue } from './WorkQueue';
import styles from './identity.module.css';
import { EmptyState } from '@/components/workspace-kit/empty-state';

type State = { kind: 'loading' } | { kind: 'ready'; summary: Summary } | { kind: 'offline' } | { kind: 'denied' } | { kind: 'error' };

/** "Pekerjaan saya": waiting work across owners, oldest first. Each count comes from the owner's own authorised queue. */
export function WorkSummary({ actorId, actions, heading = 'Pekerjaan saya' }: { actorId: string; actions: readonly string[]; heading?: string }) {
  const [gateway] = useState(createWorkSummaryGateway);
  const [state, setState] = useState<State>({ kind: 'loading' });
  const [refresh, setRefresh] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!navigator.onLine) { setState({ kind: 'offline' }); return; }
      setState({ kind: 'loading' });
      try { const summary = await gateway.read(actorId, controller.signal); if (!controller.signal.aborted) setState({ kind: 'ready', summary }); }
      catch (failure) { if (!controller.signal.aborted) setState({ kind: failure instanceof AccessFailure && (failure.status === 401 || failure.status === 403) ? 'denied' : 'error' }); }
    });
    return () => controller.abort();
  }, [gateway, actorId, refresh]);
  // Coming back from a workspace (back/forward cache) re-reads, so handled work disappears.
  useEffect(() => {
    const restored = (event: PageTransitionEvent) => { if (event.persisted) setRefresh(value => value + 1); };
    window.addEventListener('pageshow', restored); return () => window.removeEventListener('pageshow', restored);
  }, []);

  const items = state.kind === 'ready' ? visibleWorkItems(state.summary.items, actions) : [];
  return <section className={styles.workspace} aria-labelledby="work-heading">
    <div className={styles.directoryHeader}><div><h2 id="work-heading" tabIndex={-1}>{heading}</h2>
      <p className={styles.help}>Pekerjaan yang menunggu Anda, dari yang paling lama. Jumlah dihitung dari antrean yang dapat Anda buka.</p></div>
      <button type="button" className={styles.secondary} onClick={() => setRefresh(value => value + 1)} disabled={state.kind === 'loading'}>Muat ulang</button></div>
    {state.kind === 'loading' && <OutcomeState kind="loading" />}
    {state.kind === 'offline' && <OutcomeState kind="offline" />}
    {state.kind === 'denied' && <OutcomeState kind="denied" />}
    {state.kind === 'error' && <OutcomeState kind="error" onAction={() => setRefresh(value => value + 1)} />}
    {state.kind === 'ready' && (items.length === 0
      ? <EmptyState kind="done" title="Semua pekerjaan selesai" body="Tidak ada pekerjaan yang menunggu Anda saat ini." />
      : <WorkQueue items={items} observedAt={state.summary.observedAt} />)}
  </section>;
}
