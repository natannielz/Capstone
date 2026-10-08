type StoragePort = Pick<Storage, 'length' | 'key' | 'getItem' | 'setItem' | 'removeItem'>;
export type OrderViewReference = Readonly<{actorId: string; action: 'create' | 'rename' | 'remove'; viewId: string | null; revision: string; key: string; createdAt: string}>;
const validId = (value: unknown): value is string => typeof value === 'string' && /^[1-9][0-9]{0,18}$(?![\s\S])/.test(value) && BigInt(value) <= BigInt('9223372036854775807');
export const orderViewJournalPrefix = (actorId: string) => 'unit-toko.order-view.pending.' + actorId + '.';
function checked(value: unknown, actorId: string): OrderViewReference {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Invalid saved-view reference');
  const ref = value as OrderViewReference;
  if (Object.keys(ref).sort().join(',') !== 'action,actorId,createdAt,key,revision,viewId' || !validId(actorId) || ref.actorId !== actorId ||
    !['create', 'rename', 'remove'].includes(ref.action) || (ref.action === 'create' ? ref.viewId !== null || ref.revision !== '0' : !validId(ref.viewId) || !validId(ref.revision)) ||
    typeof ref.key !== 'string' || !/^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/.test(ref.key) || typeof ref.createdAt !== 'string' || ref.createdAt.length > 30 || !Number.isFinite(Date.parse(ref.createdAt))) throw new Error('Invalid saved-view reference');
  return ref;
}
/** Separate original-key references survive restart and cannot overwrite another tab's pending command. No names or filters persist here. */
export class OrderViewJournal {
  constructor(private readonly storage: () => StoragePort) {}
  list(actorId: string): OrderViewReference[] {
    if (!validId(actorId)) throw new Error('Invalid account'); const store = this.storage(), prefix = orderViewJournalPrefix(actorId);
    return Array.from({length: store.length}, (_, index) => store.key(index)).filter((key): key is string => key !== null && key.startsWith(prefix)).map(key => {
      const ref = checked(JSON.parse(store.getItem(key) ?? 'null'), actorId); if (key !== prefix + ref.key) throw new Error('Reference changed'); return ref;
    }).sort((left, right) => left.createdAt.localeCompare(right.createdAt) || left.key.localeCompare(right.key));
  }
  remember(value: OrderViewReference) {
    const ref = checked(value, value.actorId), store = this.storage(), key = orderViewJournalPrefix(ref.actorId) + ref.key, serialized = JSON.stringify(ref), old = store.getItem(key);
    if (old !== null && JSON.stringify(checked(JSON.parse(old), ref.actorId)) !== serialized) throw new Error('Reference changed');
    if (old === null && this.list(ref.actorId).length >= 32) throw new Error('Resolve pending requests');
    store.setItem(key, serialized); if (store.getItem(key) !== serialized) throw new Error('Reference was not saved');
  }
  confirmed(value: OrderViewReference) {
    const ref = checked(value, value.actorId), store = this.storage(), key = orderViewJournalPrefix(ref.actorId) + ref.key, old = store.getItem(key);
    if (old !== null && JSON.stringify(checked(JSON.parse(old), ref.actorId)) !== JSON.stringify(ref)) throw new Error('Reference changed');
    store.removeItem(key); if (store.getItem(key) !== null) throw new Error('Reference was not cleared');
  }
}
