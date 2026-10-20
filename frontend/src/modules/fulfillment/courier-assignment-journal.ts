import {parseCourierPending, type CourierPending} from './courier-assignment-client';
type StoragePort = Pick<Storage, 'length' | 'key' | 'getItem' | 'setItem' | 'removeItem'>;
export const courierJournalPrefix = (actorId: string) => 'unit-toko.courier-assignment.pending.' + actorId + '.';
/** References only, one key per request. Other tabs cannot overwrite an unknown outcome. */
export class CourierAssignmentJournal {
  constructor(private readonly storage: () => StoragePort) {}
  list(actorId: string): CourierPending[] {
    if (!/^[1-9][0-9]{0,18}$(?![\s\S])/.test(actorId) || BigInt(actorId) > BigInt('9223372036854775807')) throw new Error('Invalid recovery account');
    const store = this.storage(), prefix = courierJournalPrefix(actorId);
    return Array.from({length: store.length}, (_, index) => store.key(index)).filter((key): key is string => key !== null && key.startsWith(prefix)).map(key => {
      const ref = parseCourierPending(JSON.parse(store.getItem(key) ?? 'null'), actorId);
      if (key !== prefix + ref.key) throw new Error('Courier recovery reference changed'); return ref;
    }).sort((a, b) => a.createdAt.localeCompare(b.createdAt) || a.key.localeCompare(b.key));
  }
  remember(value: CourierPending) {
    const ref = parseCourierPending(value, value.actorId), store = this.storage(), key = courierJournalPrefix(ref.actorId) + ref.key;
    const serialized = JSON.stringify(ref), previous = store.getItem(key);
    if (previous !== null && JSON.stringify(parseCourierPending(JSON.parse(previous), ref.actorId)) !== serialized) throw new Error('Courier recovery reference changed');
    if (previous === null && this.list(ref.actorId).length >= 32) throw new Error('Resolve previous courier references');
    store.setItem(key, serialized); if (store.getItem(key) !== serialized) throw new Error('Courier recovery reference was not retained');
  }
  confirmed(value: CourierPending) {
    const ref = parseCourierPending(value, value.actorId), store = this.storage(), key = courierJournalPrefix(ref.actorId) + ref.key, previous = store.getItem(key);
    if (previous !== null && JSON.stringify(parseCourierPending(JSON.parse(previous), ref.actorId)) !== JSON.stringify(ref)) throw new Error('Courier recovery reference changed');
    store.removeItem(key); if (store.getItem(key) !== null) throw new Error('Courier recovery reference could not be cleared');
  }
}
