import {parseArrivalPending, type PurchaseArrivalPending} from './purchase-arrival-client';

type ReferenceStorage = Pick<Storage, 'length' | 'key' | 'getItem' | 'setItem' | 'removeItem'>;
export const arrivalJournalPrefix = (actorId: string) => 'unit-toko.purchase-arrival.pending.' + actorId + '.';
const referenceKey = (value: PurchaseArrivalPending) => arrivalJournalPrefix(value.actorId) + value.key;

/** Persist only original-operation references; separate keys preserve simultaneous tabs' outcomes. */
export class PurchaseArrivalJournal {
  constructor(private readonly storage: () => ReferenceStorage) {}
  list(actorId: string): PurchaseArrivalPending[] {
    const storage = this.storage(), prefix = arrivalJournalPrefix(actorId);
    const keys = Array.from({length: storage.length}, (_, index) => storage.key(index))
      .filter((key): key is string => key !== null && key.startsWith(prefix)).sort();
    return keys.map(key => {
      const value = parseArrivalPending(JSON.parse(storage.getItem(key) ?? 'null'), actorId);
      if (referenceKey(value) !== key) throw new Error('Invalid arrival recovery reference');
      return value;
    });
  }
  remember(value: PurchaseArrivalPending): void {
    const saved = parseArrivalPending(value, value.actorId), storage = this.storage();
    if (this.list(saved.actorId).length >= 32) throw new Error('Unresolved arrival reference limit reached');
    const key = referenceKey(saved), serialized = JSON.stringify(saved), existing = storage.getItem(key);
    if (existing !== null && existing !== serialized) throw new Error('Arrival recovery reference changed');
    storage.setItem(key, serialized);
    if (storage.getItem(key) !== serialized) throw new Error('Arrival recovery reference was not retained');
  }
  confirmed(value: PurchaseArrivalPending): void {
    const saved = parseArrivalPending(value, value.actorId), storage = this.storage(), key = referenceKey(saved);
    const existing = storage.getItem(key);
    if (existing !== null && JSON.stringify(parseArrivalPending(JSON.parse(existing), saved.actorId)) !== JSON.stringify(saved))
      throw new Error('Arrival recovery reference changed');
    storage.removeItem(key);
    if (storage.getItem(key) !== null) throw new Error('Arrival recovery reference could not be cleared');
  }
}
