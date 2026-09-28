import { validReference } from "../organizations/access-scope";
import { AccessFailure, MutationLedger } from "./workflow";

export type OperationReference = Readonly<{ actorId: string; operation: string; key: string; createdAt: string }>;
type JournalStorage = Pick<Storage, "length" | "key" | "getItem" | "setItem" | "removeItem">;
const prefix = "unit-toko:pending:v1:";
const keyPattern = /^[A-Za-z0-9._:-]{1,128}$/;
const operationPattern = /^(?:(access|organization)\.[a-z.]{1,100}|identity\.(?:invitation\.(?:propose|approve|rejected|withdrawn|revoked)|recovery\.request)|person\.review\.(?:propose|approve|reject|withdraw)|catalog\.(?:category\.save|family\.(?:create|edit)|sku\.(?:create|revise|replace|retire|publish)))$(?![\s\S])/;
const storageKey = (entry: OperationReference) => `${prefix}${entry.actorId}:${entry.operation}:${entry.key}`;

/** Only recovery references are stored. Requests, reasons, grants and credentials never enter this journal. */
export class OperationJournal {
  constructor(private readonly storage: () => JournalStorage, private readonly now: () => Date = () => new Date()) {}

  list(actorId: string): OperationReference[] {
    return this.withStorage(storage => {
      if (!validReference(actorId)) throw new Error("Invalid actor reference");
      const keys = Array.from({ length: storage.length }, (_, index) => storage.key(index))
        .filter((key): key is string => key !== null && key.startsWith(`${prefix}${actorId}:`));
      return keys.map(key => {
        const value: unknown = JSON.parse(storage.getItem(key) ?? "null");
        if (!validEntry(value) || value.actorId !== actorId || storageKey(value) !== key) throw new Error("Invalid recovery reference");
        return value;
      }).sort((left, right) => left.createdAt.localeCompare(right.createdAt));
    });
  }

  remember(actorId: string, operation: string, key: string): OperationReference {
    return this.withStorage(storage => {
      const existing = this.list(actorId);
      const previous = existing.find(item => item.operation === operation && item.key === key);
      if (previous) return previous;
      if (existing.length >= 32) throw new Error("Unresolved request limit reached");
      const entry = { actorId, operation, key, createdAt: this.now().toISOString() };
      if (!validEntry(entry)) throw new Error("Invalid recovery reference");
      const serialized = JSON.stringify(entry);
      storage.setItem(storageKey(entry), serialized);
      if (storage.getItem(storageKey(entry)) !== serialized) throw new Error("Recovery reference was not retained");
      return entry;
    });
  }

  confirmed(entry: OperationReference) {
    this.withStorage(storage => {
      if (!validEntry(entry)) throw new Error("Invalid recovery reference");
      storage.removeItem(storageKey(entry));
      if (storage.getItem(storageKey(entry)) !== null) throw new Error("Recovery reference could not be cleared");
    });
  }

  private withStorage<T>(action: (storage: JournalStorage) => T): T {
    try { return action(this.storage()); }
    catch { throw new AccessFailure(400, "recovery_storage_unavailable"); }
  }
}

function validEntry(value: unknown): value is OperationReference {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const entry = value as Record<string, unknown>;
  return Object.keys(entry).sort().join(",") === "actorId,createdAt,key,operation" &&
    typeof entry.actorId === "string" && validReference(entry.actorId) &&
    typeof entry.operation === "string" && operationPattern.test(entry.operation) &&
    typeof entry.key === "string" && keyPattern.test(entry.key) &&
    typeof entry.createdAt === "string" && entry.createdAt.length <= 30 && Number.isFinite(Date.parse(entry.createdAt));
}

export async function submitTrackedRequest<T>(ledger: MutationLedger, journal: OperationJournal,
  actorId: string, logicalOperation: string, operation: string, payload: unknown, send: (key: string) => Promise<T>): Promise<T> {
  const key = ledger.begin(logicalOperation, payload);
  let entry: OperationReference | undefined;
  try {
    // Storage failure stops the command before it reaches the server.
    entry = journal.remember(actorId, operation, key);
    const result = await send(key);
    ledger.complete(logicalOperation);
    // A cleanup failure must never turn a confirmed command into a retry.
    try { journal.confirmed(entry); } catch { /* The remaining reference can be reconciled on reload. */ }
    return result;
  } catch (error) {
    ledger.fail(logicalOperation, error);
    if (entry && !ledger.isUncertain(logicalOperation)) {
      try { journal.confirmed(entry); } catch { /* Retain the reference rather than masking the original failure. */ }
    }
    throw error;
  }
}


export type CommittedRecoveryReceipt = Readonly<{ state: "committed"; result?: Readonly<{ id: string; version: string }> | null }>;
export type PersonRecoveryReceipt = Readonly<{ state: "committed"; result: Readonly<{ id: string; version: string }>; recordedAt: string }> | Readonly<{ state: "closed"; result: null; recordedAt: string }>;
export type CatalogRecoveryReceipt = Extract<PersonRecoveryReceipt, { state: "committed" }>;
export type RecoveryOutcome = CommittedRecoveryReceipt | Readonly<{ state: "closed" | "unconfirmed" }>;
type LookupReceipt = Readonly<{ state: string; result?: Readonly<{ id: string; version: string }> | null }>;
export type LegacyRecoveryAdapter = Readonly<{
  lookup: (entry: OperationReference) => Promise<LookupReceipt | null>;
  close: (entry: OperationReference, reason: string) => Promise<LookupReceipt | null>;
}>;

/** Supply this only from the owning client screen. A missing receipt is never a failed or closed request. */
export type OperationRecoveryAdapters = Readonly<{
  catalog?: Readonly<{ lookup: (entry: OperationReference) => Promise<CatalogRecoveryReceipt | null> }>;
  "person.review"?: Readonly<{
    lookup: (entry: OperationReference) => Promise<PersonRecoveryReceipt | null>;
    close?: Readonly<{ allowed: (operation: string) => boolean; resolve: (entry: OperationReference, reason: string) => Promise<PersonRecoveryReceipt | null> }>;
  }>;
}>;

function recoveryNamespace(operation: string): "access" | "organization" | "identity" | "person.review" | "catalog" | null {
  if (!operationPattern.test(operation)) return null;
  if (operation.startsWith("catalog.")) return "catalog";
  if (operation.startsWith("person.review.")) return "person.review";
  if (operation.startsWith("organization.")) return "organization";
  if (operation.startsWith("identity.")) return "identity";
  if (operation.startsWith("access.")) return "access";
  return null;
}

export function operationRecoveryCapabilities(operation: string, adapters: OperationRecoveryAdapters = {}):
  Readonly<{ canCheck: boolean; canClose: boolean; unavailable: "adapter" | "operation" | null }> {
  const namespace = recoveryNamespace(operation);
  if (!namespace) return { canCheck: false, canClose: false, unavailable: "operation" };
  if (namespace === "catalog") return typeof adapters.catalog?.lookup === "function"
    ? { canCheck: true, canClose: false, unavailable: null } : { canCheck: false, canClose: false, unavailable: "adapter" };
  if (namespace === "person.review") return typeof adapters[namespace]?.lookup === "function"
    ? { canCheck: true, canClose: typeof adapters[namespace]?.close?.resolve === "function" && adapters[namespace]?.close?.allowed(operation) === true, unavailable: null }
    : { canCheck: false, canClose: false, unavailable: "adapter" };
  return { canCheck: true, canClose: true, unavailable: null };
}

/** Reconcile one immutable reference; never rebuild or retry its original command from persisted metadata. */
export async function reconcileOperationRecovery(entry: OperationReference, journal: OperationJournal,
  legacy: LegacyRecoveryAdapter, adapters: OperationRecoveryAdapters = {}, closeReason?: string, isCurrent: () => boolean = () => true): Promise<RecoveryOutcome> {
  const namespace = recoveryNamespace(entry.operation);
  if (!namespace) throw new AccessFailure(400, "recovery_operation_unsupported");
  if (!validEntry(entry)) throw new AccessFailure(400, "recovery_reference_invalid");
  const reference = Object.freeze({ ...entry });
  const requireCurrent = () => { if (!isCurrent()) throw new AccessFailure(409, "recovery_request_stale"); };
  requireCurrent();
  let receipt: LookupReceipt | null;
  if (namespace === "catalog") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    if (typeof adapters.catalog?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const catalogReceipt = await adapters.catalog.lookup(reference);
    requireCurrent();
    if (catalogReceipt !== null && (!catalogReceipt || catalogReceipt.state !== "committed" ||
      typeof catalogReceipt.recordedAt !== "string" || catalogReceipt.recordedAt.length > 40 || !/^\d{4}-\d\d-\d\dT/.test(catalogReceipt.recordedAt) || !Number.isFinite(Date.parse(catalogReceipt.recordedAt)) ||
      !catalogReceipt.result || typeof catalogReceipt.result.id !== "string" || typeof catalogReceipt.result.version !== "string" ||
      !validReference(catalogReceipt.result.id) || !validReference(catalogReceipt.result.version)))
      throw new AccessFailure(502, "recovery_receipt_invalid");
    receipt = catalogReceipt;
  } else if (namespace === "person.review") {
    const adapter = adapters[namespace];
    if (closeReason !== undefined && typeof adapter?.close?.resolve !== "function") throw new AccessFailure(400, "recovery_close_unsupported");
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    let personReceipt: PersonRecoveryReceipt | null;
    if (closeReason === undefined) personReceipt = await adapter.lookup(reference);
    else {
      if (adapter.close?.allowed(reference.operation) !== true) throw new AccessFailure(403, "recovery_close_unavailable");
      if (!closeReason.trim() || closeReason.trim().length > 1000) throw new AccessFailure(400, "recovery_reason_invalid");
      personReceipt = await adapter.close.resolve(reference, closeReason.trim());
    }
    requireCurrent();
    if (personReceipt !== null && (!personReceipt || !["committed", "closed"].includes(personReceipt.state) ||
      typeof personReceipt.recordedAt !== "string" || personReceipt.recordedAt.length > 40 || !/^\d{4}-\d\d-\d\dT/.test(personReceipt.recordedAt) || !Number.isFinite(Date.parse(personReceipt.recordedAt)) ||
      (personReceipt.state === "closed" ? personReceipt.result !== null : !personReceipt.result || typeof personReceipt.result.id !== "string" || typeof personReceipt.result.version !== "string" || !validReference(personReceipt.result.id) || !validReference(personReceipt.result.version))))
      throw new AccessFailure(502, "recovery_receipt_invalid");
    receipt = personReceipt;
  } else {
    receipt = closeReason === undefined ? await legacy.lookup(reference) : await legacy.close(reference, closeReason.trim());
  }
  requireCurrent();
  if (receipt?.state !== "committed" && receipt?.state !== "closed") return { state: "unconfirmed" };
  try { journal.confirmed(reference); }
  catch { throw new AccessFailure(400, "recovery_cleanup_failed"); }
  return receipt.state === "closed" ? { state: "closed" } : { state: "committed", ...(receipt.result ? { result: { id: receipt.result.id, version: receipt.result.version } } : {}) };
}
