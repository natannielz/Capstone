import type {AnomalyKind} from './anomaly-client';

type StoragePort = Pick<Storage, 'length' | 'key' | 'getItem' | 'setItem' | 'removeItem'>;
export type AnomalyReference = Readonly<{actorId: string; operation: 'configure' | 'evaluate' | 'review'; sourceKind: AnomalyKind;
  sourceId: string; evaluationId: string | null; expectedRevision: string | null; key: string; createdAt: string}>;
export const anomalyJournalPrefix = (actorId: string) => 'unit-toko.anomaly.pending.' + actorId + '.';
const validId = (value: unknown): value is string => typeof value === 'string' && /^[1-9][0-9]{0,18}$(?![\s\S])/.test(value) && BigInt(value) <= BigInt('9223372036854775807');
const storageKey = (value: AnomalyReference) => anomalyJournalPrefix(value.actorId) + value.operation + '.' + value.key;
export function parseAnomalyReference(value: unknown, actorId: string): AnomalyReference {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Invalid anomaly recovery reference');
  const ref = value as AnomalyReference;
  if (Object.keys(ref).sort().join(',') !== 'actorId,createdAt,evaluationId,expectedRevision,key,operation,sourceId,sourceKind' ||
    !validId(actorId) || ref.actorId !== actorId || !validId(ref.sourceId) || !['order', 'refund'].includes(ref.sourceKind) ||
    !['configure', 'evaluate', 'review'].includes(ref.operation) ||
    (ref.operation === 'review' ? !validId(ref.evaluationId) : ref.evaluationId !== null) ||
    (ref.operation === 'configure' ? !validId(ref.expectedRevision) : ref.expectedRevision !== null) ||
    typeof ref.key !== 'string' || !/^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/.test(ref.key) ||
    typeof ref.createdAt !== 'string' || ref.createdAt.length > 30 || !Number.isFinite(Date.parse(ref.createdAt))) throw new Error('Invalid anomaly recovery reference');
  return ref;
}
/** Only original-operation identifiers persist; reasons, policy values and result bodies stay out of browser storage. */
export class AnomalyJournal {
  constructor(private readonly storage: () => StoragePort) {}
  list(actorId: string): AnomalyReference[] {
    if (!validId(actorId)) throw new Error('Invalid anomaly actor');
    const storage = this.storage(), prefix = anomalyJournalPrefix(actorId);
    return Array.from({length: storage.length}, (_, index) => storage.key(index))
      .filter((key): key is string => key !== null && key.startsWith(prefix)).map(key => {
        const value = parseAnomalyReference(JSON.parse(storage.getItem(key) ?? 'null'), actorId);
        if (storageKey(value) !== key) throw new Error('Anomaly recovery reference mismatch'); return value;
      }).sort((left, right) => left.createdAt.localeCompare(right.createdAt) || left.key.localeCompare(right.key));
  }
  remember(value: AnomalyReference): void {
    const ref = parseAnomalyReference(value, value.actorId), storage = this.storage(), key = storageKey(ref), existing = storage.getItem(key);
    const serialized = JSON.stringify(ref);
    if (existing !== null && JSON.stringify(parseAnomalyReference(JSON.parse(existing), ref.actorId)) !== serialized) throw new Error('Anomaly recovery reference changed');
    if (existing === null && this.list(ref.actorId).length >= 32) throw new Error('Unresolved anomaly reference limit reached');
    storage.setItem(key, serialized); if (storage.getItem(key) !== serialized) throw new Error('Anomaly reference was not retained');
  }
  confirmed(value: AnomalyReference): void {
    const ref = parseAnomalyReference(value, value.actorId), storage = this.storage(), key = storageKey(ref), existing = storage.getItem(key);
    if (existing !== null && JSON.stringify(parseAnomalyReference(JSON.parse(existing), ref.actorId)) !== JSON.stringify(ref)) throw new Error('Anomaly recovery reference changed');
    storage.removeItem(key); if (storage.getItem(key) !== null) throw new Error('Anomaly reference could not be cleared');
  }
}
