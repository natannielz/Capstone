type StoragePort = Pick<Storage, 'length' | 'key' | 'getItem' | 'setItem' | 'removeItem'>;
export type DelegationAction = 'propose' | 'approve' | 'revoke' | 'handover';
export type DelegationReference = Readonly<{actorId: string; action: DelegationAction; id: string | null; revision: string; key: string; createdAt: string}>;
const validId = (value: unknown): value is string => typeof value === 'string' && /^[1-9][0-9]{0,18}$(?![\s\S])/.test(value) && BigInt(value) <= BigInt('9223372036854775807');
export const delegationJournalPrefix = (actor: string) => 'unit-toko.delegation.pending.' + actor + '.';
const storageKey = (ref: DelegationReference) => delegationJournalPrefix(ref.actorId) + ref.key;
export function parseDelegationReference(value: unknown, actorId: string): DelegationReference {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Invalid delegation reference');
  const ref = value as DelegationReference;
  if (Object.keys(ref).sort().join(',') !== 'action,actorId,createdAt,id,key,revision' || !validId(actorId) || ref.actorId !== actorId ||
    !['propose', 'approve', 'revoke', 'handover'].includes(ref.action) ||
    (ref.action === 'propose' ? ref.id !== null || ref.revision !== '0' : !validId(ref.id) || !validId(ref.revision)) ||
    typeof ref.key !== 'string' || !/^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/.test(ref.key) || typeof ref.createdAt !== 'string' ||
    ref.createdAt.length > 30 || !Number.isFinite(Date.parse(ref.createdAt))) throw new Error('Invalid delegation reference');
  return Object.freeze({...ref});
}
/** Reference-only durable journal: no permissions, people evidence, reasons or provider/session data. */
export class DelegationJournal {
  constructor(private readonly storage: () => StoragePort) {}
  list(actorId: string): DelegationReference[] {
    if (!validId(actorId)) throw new Error('Invalid delegation actor');
    const store = this.storage(), prefix = delegationJournalPrefix(actorId);
    return Array.from({length: store.length}, (_, index) => store.key(index)).filter((key): key is string => key !== null && key.startsWith(prefix)).map(key => {
      const ref = parseDelegationReference(JSON.parse(store.getItem(key) ?? 'null'), actorId);
      if (key !== storageKey(ref)) throw new Error('Delegation reference mismatch'); return ref;
    }).sort((a, b) => a.createdAt.localeCompare(b.createdAt) || a.key.localeCompare(b.key));
  }
  remember(value: DelegationReference) {
    const ref = parseDelegationReference(value, value.actorId), store = this.storage(), key = storageKey(ref), existing = store.getItem(key), text = JSON.stringify(ref);
    if (existing !== null && JSON.stringify(parseDelegationReference(JSON.parse(existing), ref.actorId)) !== text) throw new Error('Delegation reference changed');
    if (existing === null && this.list(ref.actorId).length >= 32) throw new Error('Reconcile earlier delegation operations first');
    store.setItem(key, text); if (store.getItem(key) !== text) throw new Error('Delegation reference not retained');
  }
  confirmed(value: DelegationReference) {
    const ref = parseDelegationReference(value, value.actorId), store = this.storage(), key = storageKey(ref), existing = store.getItem(key);
    if (existing !== null && JSON.stringify(parseDelegationReference(JSON.parse(existing), ref.actorId)) !== JSON.stringify(ref)) throw new Error('Delegation reference changed');
    store.removeItem(key); if (store.getItem(key) !== null) throw new Error('Delegation reference not cleared');
  }
}
