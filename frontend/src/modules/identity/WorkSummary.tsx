'use client';

import { useEffect, useState } from 'react';
import { outcomeCopy, type OutcomeKind } from '@/components/workspace-kit/outcome-copy';
import { Button } from '@/components/workspace-kit/button';
import { Section } from '@/components/workspace-kit/section';
import { StateView } from '@/components/workspace-kit/state-view';
import { AccessFailure } from '../administration/workflow';
import { createWorkSummaryGateway, visibleWorkItems, type WorkSummary as Summary } from './work-summary-client';
import { WorkQueue } from './WorkQueue';

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
  const reload = () => setRefresh(value => value + 1);
  const copy = (kind: OutcomeKind) => outcomeCopy(kind);
  return <Section title={heading} id="work-heading" variant="plain" focusable busy={state.kind === 'loading'}
    description="Pekerjaan yang menunggu Anda, dari yang paling lama. Jumlah dihitung dari antrean yang dapat Anda buka."
    actions={<Button onClick={reload} disabled={state.kind === 'loading'}>Muat ulang</Button>}>
    {state.kind === 'loading' && <StateView kind="loading" title={copy('loading').title} />}
    {state.kind === 'offline' && <StateView kind="offline" title={copy('offline').title} message={copy('offline').body} />}
    {state.kind === 'denied' && <StateView kind="denied" title={copy('denied').title} message={copy('denied').body} />}
    {state.kind === 'error' && <StateView kind="error" title={copy('error').title} message={copy('error').body}
      action={<Button onClick={reload}>{copy('error').action}</Button>} />}
    {state.kind === 'ready' && (items.length === 0
      ? <StateView kind="empty" title="Semua pekerjaan selesai" message="Tidak ada pekerjaan yang menunggu Anda saat ini." />
      : <WorkQueue items={items} observedAt={state.summary.observedAt} />)}
  </Section>;
}
