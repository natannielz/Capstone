import {AccessFailure} from '@/modules/administration/workflow';
import {RequestNotSentError} from '@/api/client';
import {OperationJournal} from '../administration/operation-journal';

export const supplementOperations = {create: 'billing.payment.supplement.create', reserve: 'evidence.payment-supplement-pdf.reserve', attach: 'billing.payment.supplement.attach'} as const;
export type SupplementOperation = typeof supplementOperations[keyof typeof supplementOperations];
export type SupplementKeyBinding = {actorId: string; declarationId: string; submissionId: string; supplementId: string | null};
export type SupplementPending = Readonly<{actorId: string; operation: SupplementOperation; key: string; createdAt: string}>;
type StoragePort = Pick<Storage, 'length' | 'key' | 'getItem' | 'setItem' | 'removeItem'>;
const idPattern = /^[1-9][0-9]{0,18}$(?![\s\S])/;
const uuidPattern = '[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}';
const positiveId = (value: string) => idPattern.test(value) && BigInt(value) < BigInt('9223372036854775806');
const actionOf = (operation: SupplementOperation) => operation === supplementOperations.create ? 'c' : operation === supplementOperations.reserve ? 'r' : 'a';
const invalid = (): never => {throw new AccessFailure(400, 'payment_supplement_recovery_invalid');};
export function supplementKeyBinding(operation: SupplementOperation, key: string, actorId: string): SupplementKeyBinding {
  if (!Object.values(supplementOperations).includes(operation) || !positiveId(actorId) || key.length > 128) return invalid();
  const match = new RegExp(`^ps\\.${actionOf(operation)}\\.([1-9][0-9]{0,18})\\.([1-9][0-9]{0,18})\\.([1-9][0-9]{0,18})${operation === supplementOperations.create ? '' : '\\.([1-9][0-9]{0,18})'}\\.(${uuidPattern})$(?![\\s\\S])`).exec(key);
  if (!match || match[1] !== actorId || !match.slice(1, operation === supplementOperations.create ? 4 : 5).every(positiveId)) return invalid();
  return {actorId: match[1], declarationId: match[2], submissionId: match[3], supplementId: operation === supplementOperations.create ? null : match[4]};
}
export function supplementKey(operation: SupplementOperation, owner: SupplementKeyBinding, uuid: string): string {
  const key = `ps.${actionOf(operation)}.${owner.actorId}.${owner.declarationId}.${owner.submissionId}.${operation === supplementOperations.create ? '' : `${owner.supplementId}.`}${uuid}`;
  const parsed = supplementKeyBinding(operation, key, owner.actorId);
  if (parsed.supplementId !== owner.supplementId) return invalid();
  return key;
}
export function isSupplementOperation(operation: string): operation is SupplementOperation {
  return Object.values(supplementOperations).includes(operation as SupplementOperation);
}
function validEntry(value: unknown): value is SupplementPending {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const item = value as Record<string, unknown>;
  if (Object.keys(item).sort().join(',') !== 'actorId,createdAt,key,operation' || typeof item.actorId !== 'string' || typeof item.operation !== 'string' ||
    !Object.values(supplementOperations).includes(item.operation as SupplementOperation) || typeof item.key !== 'string' || typeof item.createdAt !== 'string' ||
    !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$(?![\s\S])/.test(item.createdAt) || !Number.isFinite(Date.parse(item.createdAt)) || new Date(item.createdAt).toISOString() !== item.createdAt) return false;
  try {supplementKeyBinding(item.operation as SupplementOperation, item.key, item.actorId); return true;} catch {return false;}
}

/** Uses the established journal prefix, actor-wide limit and storage checks. No second journal is introduced. */
export class SupplementJournal {
  private readonly journal: OperationJournal;
  constructor(storage: () => StoragePort, now = () => new Date(), journal?: OperationJournal) {
    // Reference the class at construction time to keep the parser/journal module cycle safe.
    this.journal = journal ?? new OperationJournal(storage, now);
  }
  hasAnyUnresolved(actorId: string): boolean {return this.journal.list(actorId).length > 0;}
  list(actorId: string): SupplementPending[] {
    if (!positiveId(actorId)) return invalid();
    return this.journal.list(actorId).filter(entry => isSupplementOperation(entry.operation)).map(entry => {
      if (!validEntry(entry)) return invalid(); return entry;
    });
  }
  remember(actorId: string, operation: SupplementOperation, key: string): SupplementPending {
    supplementKeyBinding(operation, key, actorId);
    const entry = this.journal.remember(actorId, operation, key); if (!validEntry(entry)) return invalid(); return entry;
  }
  confirmed(entry: SupplementPending) {if (!validEntry(entry)) return invalid(); this.journal.confirmed(entry);}
}

export async function sendSupplementOnce<T>(journal: SupplementJournal, entry: {actorId: string; operation: SupplementOperation; key: string}, send: () => Promise<T>): Promise<T> {
  if (journal.hasAnyUnresolved(entry.actorId)) throw new RequestNotSentError(409, 'resolve_previous_attempt');
  const retained = journal.remember(entry.actorId, entry.operation, entry.key);
  try {const result = await send(); try {journal.confirmed(retained);} catch { /* Confirmed result remains usable; reload reconciles retained key. */ } return result;}
  catch (failure) {
    if (failure instanceof RequestNotSentError || failure instanceof AccessFailure && failure.status >= 400 && failure.status < 500) {
      try {journal.confirmed(retained);} catch { /* Preserve original reference if browser storage is unavailable. */ }
    }
    throw failure;
  }
}

/** Read-only original-key resolution. Null never authorizes replay or a new key. */
export async function recoverSupplement<T>(journal: SupplementJournal, entry: SupplementPending, current: () => boolean,
  lookup: (binding: SupplementKeyBinding, key: string) => Promise<T | null>): Promise<T | null> {
  const owner = supplementKeyBinding(entry.operation, entry.key, entry.actorId);
  if (!current()) throw new RequestNotSentError(409, 'payment_supplement_view_stale');
  const result = await lookup(owner, entry.key);
  if (!current()) throw new RequestNotSentError(409, 'payment_supplement_view_stale');
  if (result !== null) journal.confirmed(entry); return result;
}
