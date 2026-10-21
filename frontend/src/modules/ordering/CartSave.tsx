'use client';

import { useEffect, useRef, useState } from 'react';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import { submitTrackedRequest } from '../administration/operation-journal';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { createOrderDraftGateway } from './order-draft-client';
import { createCartGateway } from './cart-client';
import palette from '../workspace-palette.module.css';
import styles from './cart.module.css';

/** Cart saves on the draft journal, like every other draft save: a save whose answer was lost stays listed, even after a
 *  reload, until "Periksa hasil" asks the server whether it committed. Until then no further cart change is sent. */
export function useCartSave(actorId: string, isCurrent: () => boolean, onResolved: () => void) {
  const recovery = useOperationRecovery(actorId), [ledger] = useState(() => new MutationLedger()), [drafts] = useState(() => createOrderDraftGateway());
  const latest = useRef({ isCurrent, onResolved, refresh: recovery.refresh });
  useEffect(() => { latest.current = { isCurrent, onResolved, refresh: recovery.refresh }; });
  const [recovering, setRecovering] = useState(false), [failure, setFailure] = useState<unknown>(null);
  const [cart] = useState(() => createCartGateway({ drafts, submit: async command => {
    if (recovery.journal.list(actorId).length > 0) throw new AccessFailure(400, 'resolve_previous_attempt');
    try {
      return await submitTrackedRequest(ledger, recovery.journal, actorId, 'ordering.draft.save', 'ordering.draft.save', command,
        key => drafts.submit(command, key, () => latest.current.isCurrent()));
    } finally { latest.current.refresh(); }
  } }));
  return { cart, locked: recovering || recovery.locked,
    recoveryView: recovery.entries.length > 0 || recovery.error !== null || failure !== null ? <div className={palette.palette}>
      {recovery.error !== null && <p role="alert" className={styles.alert}>{accessMessage(recovery.error)}</p>}
      {failure !== null && <p role="alert" className={styles.alert}>{accessMessage(failure)}</p>}
      <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={recovering} onBusyChange={setRecovering} onFailure={setFailure}
        recoveryAdapters={{ 'ordering.draft': { lookup: entry => drafts.recover(entry.actorId, entry.key) } }}
        onResolved={() => { recovery.refresh(); ledger.resetAfterReconciliation(); setFailure(null); latest.current.onResolved(); }} />
    </div> : null };
}
