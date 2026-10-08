type StoragePort = Pick<Storage, 'length' | 'key' | 'getItem' | 'setItem' | 'removeItem'>;
export type PaymentHandoverReference = Readonly<{actorId: string; declarationId: string; submissionId: string; reviewRevision: string;
  delegationId: string; delegationRevision: string; delegationEventId: string; originalAssigneeId: string; declarationRevision: string; key: string; createdAt: string}>;
const id = (value: unknown): value is string => typeof value === 'string' && /^[1-9][0-9]{0,18}$(?![\s\S])/.test(value) && BigInt(value) <= BigInt('9223372036854775807');
export const paymentHandoverPrefix = (actorId: string) => 'unit-toko.payment-handover.pending.' + actorId + '.';
export function parsePaymentHandoverReference(value: unknown, actorId: string): PaymentHandoverReference {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Invalid payment handover reference');
  const ref = value as PaymentHandoverReference;
  if (Object.keys(ref).sort().join(',') !== 'actorId,createdAt,declarationId,declarationRevision,delegationEventId,delegationId,delegationRevision,key,originalAssigneeId,reviewRevision,submissionId' ||
    ref.actorId !== actorId || ![ref.actorId, ref.declarationId, ref.submissionId, ref.reviewRevision, ref.delegationId, ref.delegationRevision,
      ref.delegationEventId, ref.originalAssigneeId, ref.declarationRevision].every(id) || ref.actorId === ref.originalAssigneeId ||
    typeof ref.key !== 'string' || !new RegExp('^ph\\.' + ref.declarationId + '\\.[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$(?![\\s\\S])').test(ref.key) ||
    typeof ref.createdAt !== 'string' || ref.createdAt.length > 30 || !Number.isFinite(Date.parse(ref.createdAt))) throw new Error('Invalid payment handover reference');
  return Object.freeze({...ref});
}
/** Only failures inside a concrete storage operation receive this classification. */
export class PaymentHandoverStorageError extends Error {
  constructor(readonly operation: 'read' | 'write' | 'remove') {
    super('Payment handover reference storage failed'); this.name = 'PaymentHandoverStorageError';
  }
}
function storageOperation<T>(operation: PaymentHandoverStorageError['operation'], task: () => T): T {
  try {return task();} catch (error) {
    if (error instanceof PaymentHandoverStorageError) throw error;
    throw new PaymentHandoverStorageError(operation);
  }
}
/** Persist only original operation references, never reasons, payment evidence, grants or session data. */
export class PaymentHandoverJournal {
  constructor(private readonly storage: () => StoragePort) {}
  list(actorId: string): PaymentHandoverReference[] {
    if (!id(actorId)) throw new Error('Invalid payment handover actor');
    return storageOperation('read', () => {
      const store = this.storage(), prefix = paymentHandoverPrefix(actorId);
      return Array.from({length: store.length}, (_, index) => store.key(index)).filter((key): key is string => !!key && key.startsWith(prefix)).map(key => {
        const ref = parsePaymentHandoverReference(JSON.parse(store.getItem(key) ?? 'null'), actorId);
        if (key !== prefix + ref.key) throw new Error('Payment handover reference mismatch'); return ref;
      }).sort((a, b) => a.createdAt.localeCompare(b.createdAt) || a.key.localeCompare(b.key));
    });
  }
  remember(value: PaymentHandoverReference) {
    const ref = parsePaymentHandoverReference(value, value.actorId);
    storageOperation('write', () => {
      const store = this.storage(), key = paymentHandoverPrefix(ref.actorId) + ref.key, text = JSON.stringify(ref), old = store.getItem(key);
      if (old !== null && JSON.stringify(parsePaymentHandoverReference(JSON.parse(old), ref.actorId)) !== text) throw new Error('Payment handover reference changed');
      if (old === null && this.list(ref.actorId).length >= 32) throw new Error('Reconcile previous payment handovers');
      store.setItem(key, text); if (store.getItem(key) !== text) throw new Error('Payment handover reference not retained');
    });
  }
  confirmed(value: PaymentHandoverReference) {
    const ref = parsePaymentHandoverReference(value, value.actorId);
    storageOperation('remove', () => {
      const store = this.storage(), key = paymentHandoverPrefix(ref.actorId) + ref.key, old = store.getItem(key);
      if (old !== null && JSON.stringify(parsePaymentHandoverReference(JSON.parse(old), ref.actorId)) !== JSON.stringify(ref)) throw new Error('Payment handover reference changed');
      store.removeItem(key); if (store.getItem(key) !== null) throw new Error('Payment handover reference not cleared');
    });
  }
}
