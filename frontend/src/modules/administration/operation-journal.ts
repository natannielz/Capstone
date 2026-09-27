import { validReference } from "../organizations/access-scope";
import { AccessFailure, MutationLedger } from "./workflow";

export type OperationReference = Readonly<{ actorId: string; operation: string; key: string; createdAt: string }>;
type JournalStorage = Pick<Storage, "length" | "key" | "getItem" | "setItem" | "removeItem">;
const prefix = "unit-toko:pending:v1:";
const keyPattern = /^[A-Za-z0-9._:-]{1,128}$/;
const operationPattern = /^(?:(access|organization)\.[a-z.]{1,100}|identity\.(?:invitation\.(?:propose|approve|rejected|withdrawn|revoked)|recovery\.request))$/;
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
