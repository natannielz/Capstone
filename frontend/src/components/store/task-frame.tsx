'use client';

import { usePathname } from 'next/navigation';
import { useEffect, useState } from 'react';
import { WorkspaceFrame } from '@/components/workspace-kit/workspace-frame';
import { createAccessGateway } from '@/modules/administration/access-client';
import { createIdentityGateway } from '@/modules/identity/identity-client';
import { StoreFrame } from './store-frame';
import { taskFrame } from './task-frame-rule';

/** The frame for pages that are a buyer task for some people and back-office work for others (G-037). Display only. */
export function TaskFrame({ children }: { children: React.ReactNode }) {
  const pathname = usePathname() ?? '/';
  const [actions, setActions] = useState<readonly string[] | null>(null);
  const decides = taskFrame(pathname, null) === 'pending';
  useEffect(() => {
    if (!decides) return;
    let alive = true;
    void (async () => {
      try {
        const session = await createIdentityGateway().session();
        if (!session.authenticated || !session.account) { if (alive) setActions([]); return; }
        const scope = await createAccessGateway().current();
        if (alive) setActions(scope.allowedActions);
      } catch { if (alive) setActions([]); }
    })();
    return () => { alive = false; };
  }, [decides]);
  const frame = taskFrame(pathname, actions);
  if (frame === 'pending') return <p role="status" aria-busy="true">Memeriksa akun…</p>;
  return frame === 'store' ? <StoreFrame>{children}</StoreFrame> : <WorkspaceFrame>{children}</WorkspaceFrame>;
}
