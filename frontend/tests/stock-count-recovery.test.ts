import { test } from "node:test";
import assert from "node:assert/strict";
import {
  OperationJournal, submitTrackedRequest, reconcileOperationRecovery, operationRecoveryCapabilities,
  type LegacyRecoveryAdapter, type OperationRecoveryAdapters, type OperationReference,
} from "../src/modules/administration/operation-journal";
import { AccessFailure, MutationLedger } from "../src/modules/administration/workflow";

class MemoryStorage {
  values = new Map<string, string>();
  get length() { return this.values.size; }
  key(index: number) { return [...this.values.keys()][index] ?? null; }
  getItem(key: string) { return this.values.get(key) ?? null; }
  setItem(key: string, value: string) { this.values.set(key, value); }
  removeItem(key: string) { this.values.delete(key); }
}
const actorId = "9007199254740993";
const operation = "inventory.stock-count.record";
const entry: OperationReference = { actorId, operation, key: "count-once", createdAt: "2026-09-28T09:30:00.000Z" };
const result = { id: "9223372036854775807", version: "1" };
const committed = { state: "committed", result, recordedAt: entry.createdAt };
const noLegacy: LegacyRecoveryAdapter = {
  async lookup() { assert.fail("a stock-count receipt must never use access lookup"); },
  async close() { assert.fail("a stock-count receipt cannot be closed through access"); },
};
function adapter(lookup: (reference: OperationReference) => Promise<unknown>): OperationRecoveryAdapters {
  return { "inventory.stock-count": { lookup } } as unknown as OperationRecoveryAdapters;
}
function pending() {
  const storage = new MemoryStorage();
  const journal = new OperationJournal(() => storage, () => new Date(entry.createdAt));
  return { storage, journal, reference: journal.remember(actorId, operation, entry.key) };
}

test("an uncertain zero-count retains only its actor-owned reference after reload and a denied retry", async () => {
  const storage = new MemoryStorage(), journal = new OperationJournal(() => storage);
  const ledger = new MutationLedger(() => entry.key);
  const payload = { packages: 0, reason: "Private count reason", observedLotLabel: "PRIVATE-LOT" };
  let dispatched = 0;
  const send = (failure: AccessFailure) => submitTrackedRequest(ledger, journal, actorId, "count", operation, payload, async key => {
    dispatched++; assert.equal(key, entry.key); assert.equal(journal.list(actorId).length, 1);
    throw failure;
  });
  await assert.rejects(send(new AccessFailure(503, "commit_outcome_unknown")), /commit_outcome_unknown/);
  await assert.rejects(send(new AccessFailure(403, "access_denied")), /access_denied/);
  assert.equal(dispatched, 2);
  assert.equal(ledger.isUncertain("count"), true);
  const reloaded = new OperationJournal(() => storage);
  assert.equal(reloaded.list(actorId)[0].key, entry.key);
  assert.deepEqual(reloaded.list("1"), []);
  assert.deepEqual(Object.keys(JSON.parse([...storage.values.values()][0])).sort(), ["actorId", "createdAt", "key", "operation"]);
  assert.doesNotMatch([...storage.values.values()].join(""), /Private count reason|PRIVATE-LOT|packages|reason/);
  await assert.rejects(submitTrackedRequest(ledger, journal, actorId, "count", operation, { ...payload, packages: 1 },
    async () => { assert.fail("changed counts must not reuse an unresolved request"); }), /resolve_previous_attempt/);
});

test("stock recovery requires its dedicated adapter and never permits closure", async () => {
  const { journal, reference } = pending();
  assert.deepEqual(operationRecoveryCapabilities(operation), { canCheck: false, canClose: false, unavailable: "adapter" });
  await assert.rejects(reconcileOperationRecovery(reference, journal, noLegacy), /recovery_adapter_unavailable/);
  const adapters = adapter(async () => { assert.fail("closure must stop before lookup"); });
  assert.deepEqual(operationRecoveryCapabilities(operation, adapters), { canCheck: true, canClose: false, unavailable: null });
  await assert.rejects(reconcileOperationRecovery(reference, journal, noLegacy, adapters, ""), /recovery_close_unsupported/);
  assert.deepEqual(journal.list(actorId), [reference]);
});

test("confirmed immutable stock count clears only its matching actor operation and key", async () => {
  const { journal, reference } = pending();
  journal.remember("7", operation, entry.key);
  journal.remember(actorId, "catalog.sku.revise", entry.key);
  journal.remember(actorId, operation, "another-count");
  const outcome = await reconcileOperationRecovery(reference, journal, noLegacy, adapter(async actual => {
    assert.ok(Object.isFrozen(actual)); assert.deepEqual(actual, reference);
    return committed;
  }));
  assert.deepEqual(outcome, { state: "committed", result });
  assert.equal(journal.list("7").length, 1);
  assert.deepEqual(journal.list(actorId).map(item => [item.operation, item.key]), [
    ["catalog.sku.revise", entry.key], [operation, "another-count"],
  ]);
});

test("missing denied and interrupted stock receipt checks keep the pending reference", async () => {
  const { journal, reference } = pending();
  assert.deepEqual(await reconcileOperationRecovery(reference, journal, noLegacy, adapter(async () => null)), { state: "unconfirmed" });
  for (const failure of [new AccessFailure(403, "access_denied"), new Error("network unavailable")]) {
    await assert.rejects(reconcileOperationRecovery(reference, journal, noLegacy, adapter(async () => { throw failure; })), error => error === failure);
  }
  assert.deepEqual(journal.list(actorId), [reference]);
});

test("malformed or nonimmutable stock receipts cannot discard the count reference", async () => {
  const { journal, reference } = pending();
  const invalid = [
    undefined, {}, { state: "closed", result: null, recordedAt: entry.createdAt },
    { ...committed, result: null }, { ...committed, recordedAt: "bad" },
    { ...committed, result: { id: "01", version: "1" } },
    { ...committed, result: { id: "9223372036854775808", version: "1" } },
    { ...committed, result: { id: 9007199254740993, version: "1" } },
    { ...committed, result: { id: result.id, version: "2" } },
    { ...committed, result: { id: result.id, version: 1 } },
  ];
  for (const receipt of invalid) {
    await assert.rejects(reconcileOperationRecovery(reference, journal, noLegacy, adapter(async () => receipt)), /recovery_receipt_invalid/);
    assert.deepEqual(journal.list(actorId), [reference]);
  }
});

test("a stock receipt arriving after view replacement cannot clear retained evidence", async () => {
  const { journal, reference } = pending();
  let current = true;
  let finish!: (value: typeof committed) => void;
  const completion = new Promise<typeof committed>(resolve => { finish = resolve; });
  const request = reconcileOperationRecovery(reference, journal, noLegacy, adapter(async () => completion), undefined, () => current);
  current = false; finish(committed);
  await assert.rejects(request, /recovery_request_stale/);
  assert.deepEqual(journal.list(actorId), [reference]);
  await assert.rejects(reconcileOperationRecovery(reference, journal, noLegacy,
    adapter(async () => { assert.fail("a stale view must not dispatch"); }), undefined, () => false), /recovery_request_stale/);
});

test("unsupported inventory operations are neither persisted nor routed to another recovery service", async () => {
  const journal = new OperationJournal(() => new MemoryStorage());
  for (const candidate of ["inventory.stock-count.record.extra", "inventory.stock-count.record\n", "inventory.stock-count.close", "inventory.stock.adjust", "inventory.stock-count.Record"]) {
    assert.throws(() => journal.remember(actorId, candidate, entry.key), /recovery_storage_unavailable/);
    assert.deepEqual(operationRecoveryCapabilities(candidate, adapter(async () => committed)), { canCheck: false, canClose: false, unavailable: "operation" });
    await assert.rejects(reconcileOperationRecovery({ ...entry, operation: candidate }, journal, noLegacy), /recovery_operation_unsupported/);
  }
  assert.deepEqual(journal.list(actorId), []);
});
