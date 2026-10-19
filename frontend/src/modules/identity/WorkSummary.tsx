'use client';

import Link from 'next/link';
import { useEffect, useState } from 'react';
import { ListTable } from '@/components/workspace-kit/list-table';
import { OutcomeState } from '@/components/workspace-kit/outcome-state';
import { AccessFailure } from '../administration/workflow';
import { createWorkSummaryGateway, visibleWorkItems, workAgeText, workCountText, type WorkSummary as Summary } from './work-summary-client';
import styles from './identity.module.css';

type State = { kind: 'loading' } | { kind: 'ready'; summary: Summary } | { kind: 'offline' } | { kind: 'denied' } | { kind: 'error' };

/** "Pekerjaan saya": waiting work across owners, oldest first. Each count comes from the owner's own authorised queue. */
export function WorkSummary({ actorId, actions }: { actorId: string; actions: readonly string[] }) {
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
    <div className={styles.directoryHeader}><div><h2 id="work-heading" tabIndex={-1}>Pekerjaan saya</h2>
      <p className={styles.help}>Pekerjaan yang menunggu Anda, dari yang paling lama. Jumlah dihitung dari antrean yang dapat Anda buka.</p></div>
      <button type="button" className={styles.secondary} onClick={() => setRefresh(value => value + 1)} disabled={state.kind === 'loading'}>Muat ulang</button></div>
    {state.kind === 'loading' && <OutcomeState kind="loading" />}
    {state.kind === 'offline' && <OutcomeState kind="offline" />}
    {state.kind === 'denied' && <OutcomeState kind="denied" />}
    {state.kind === 'error' && <OutcomeState kind="error" onAction={() => setRefresh(value => value + 1)} />}
    {state.kind === 'ready' && (items.length === 0
      ? <p role="status" className={styles.help}>Tidak ada pekerjaan yang menunggu.</p>
      : <ListTable caption="Pekerjaan yang menunggu" rows={items} getKey={item => item.key}
        columns={[
          { key: 'work', header: 'Pekerjaan', render: item => <><Link href={item.href}>{item.label}</Link>{item.unavailable && <p className={styles.help}>Belum dapat dimuat. Buka ruang kerjanya untuk memeriksa.</p>}</> },
          { key: 'count', header: 'Jumlah', align: 'end', render: item => item.unavailable ? '—' : workCountText(item.count, item.hasMore) },
          { key: 'age', header: 'Menunggu', render: item => workAgeText(item.oldestAt, Date.parse(state.summary.observedAt), item.hasMore) || '—' },
        ]} />)}
  </section>;
}
