import { test } from "node:test";
import assert from "node:assert/strict";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { OperationJournal, reconcileOperationRecovery, operationRecoveryCapabilities, type OperationReference, type LegacyRecoveryAdapter, type OperationRecoveryAdapters } from "../src/modules/administration/operation-journal";
import { OperationRecovery } from "../src/modules/administration/OperationRecovery";
import { identityDestinations, loginFields } from "../src/modules/identity/identity-workflow";

class MemoryStorage {
  values = new Map<string, string>();
  get length() { return this.values.size; }
  key(index: number) { return [...this.values.keys()][index] ?? null; }
  getItem(key: string) { return this.values.get(key) ?? null; }
  setItem(key: string, value: string) { this.values.set(key, value); }
  removeItem(key: string) { this.values.delete(key); }
}
const actorId = "9007199254740993";
const operations = ["catalog.category.save", "catalog.family.create", "catalog.family.edit", "catalog.sku.create", "catalog.sku.revise", "catalog.sku.replace", "catalog.sku.retire", "catalog.sku.publish"];
const entry: OperationReference = { actorId, operation: "catalog.sku.create", key: "catalog-once", createdAt: "2026-09-28T01:00:00.000Z" };
const result = { id: "9007199254740995", version: "9007199254740997" };
const committed = { state: "committed" as const, result, recordedAt: entry.createdAt };
const noLegacy: LegacyRecoveryAdapter = {
  async lookup() { assert.fail("catalog must not use access lookup"); },
  async close() { assert.fail("catalog must not use access close"); },
};
function pending(operation = entry.operation) {
  const storage = new MemoryStorage();
  const journal = new OperationJournal(() => storage, () => new Date(entry.createdAt));
  return { storage, journal, reference: journal.remember(actorId, operation, entry.key) };
}
for (const operation of operations) test(operation + " survives reload with actor-owned metadata and no private catalog fields", () => {
  const { storage, journal, reference } = pending(operation);
  assert.deepEqual(new OperationJournal(() => storage).list(actorId), [reference]);
  assert.deepEqual(Object.keys(JSON.parse([...storage.values.values()][0])).sort(), ["actorId", "createdAt", "key", "operation"]);
  assert.deepEqual(journal.list("1"), []);
});

test("catalog receipts have no legacy fallback or supported close even when explicitly requested", async () => {
  const { journal, reference } = pending();
  assert.deepEqual(operationRecoveryCapabilities(entry.operation), { canCheck: false, canClose: false, unavailable: "adapter" });
  await assert.rejects(reconcileOperationRecovery(reference, journal, noLegacy), /recovery_adapter_unavailable/);
  const adapters = { catalog: { async lookup() { assert.fail("close must not send anything"); } } } as OperationRecoveryAdapters;
  assert.deepEqual(operationRecoveryCapabilities(entry.operation, adapters), { canCheck: true, canClose: false, unavailable: null });
  await assert.rejects(reconcileOperationRecovery(reference, journal, noLegacy, adapters, "close it"), /recovery_close_unsupported/);
  assert.deepEqual(journal.list(actorId), [reference]);
});

test("catalog receipt recovery clears only the matching actor operation and key", async () => {
  const { journal, reference } = pending();
  journal.remember("7", entry.operation, entry.key);
  journal.remember(actorId, "catalog.sku.revise", entry.key);
  const adapters = { catalog: { async lookup(actual: OperationReference) { assert.ok(Object.isFrozen(actual)); assert.deepEqual(actual, reference); return committed; } } } as OperationRecoveryAdapters;
  assert.deepEqual(await reconcileOperationRecovery(reference, journal, noLegacy, adapters), { state: "committed", result });
  assert.equal(journal.list("7").length, 1);
  assert.deepEqual(journal.list(actorId).map(item => item.operation), ["catalog.sku.revise"]);
});

test("absent and failed catalog receipts remain unresolved without automatic command retries", async () => {
  const { journal, reference } = pending();
  assert.deepEqual(await reconcileOperationRecovery(reference, journal, noLegacy, { catalog: { async lookup() { return null; } } } as OperationRecoveryAdapters), { state: "unconfirmed" });
  await assert.rejects(reconcileOperationRecovery(reference, journal, noLegacy, { catalog: { async lookup() { throw new Error("permission denied"); } } } as OperationRecoveryAdapters), /permission denied/);
  assert.deepEqual(journal.list(actorId), [reference]);
});

test("malformed and closed catalog receipts cannot erase the retained command reference", async () => {
  const { journal, reference } = pending();
  for (const receipt of [{ state: "closed", result: null, recordedAt: entry.createdAt }, { ...committed, result: null }, { ...committed, result: { id: 1, version: "1" } }, { ...committed, recordedAt: "bad" }, { ...committed, result: { id: "01", version: "1" } }]) {
    const adapters = { catalog: { async lookup() { return receipt; } } } as unknown as OperationRecoveryAdapters;
    await assert.rejects(reconcileOperationRecovery(reference, journal, noLegacy, adapters), /recovery_receipt_invalid/);
    assert.deepEqual(journal.list(actorId), [reference]);
  }
});

test("catalog success after actor view replacement cannot clear another view's journal", async () => {
  const { journal, reference } = pending();
  let current = true;
  let finish!: (value: typeof committed) => void;
  const completion = new Promise<typeof committed>(resolve => { finish = resolve; });
  const request = reconcileOperationRecovery(reference, journal, noLegacy, { catalog: { async lookup() { return completion; } } } as OperationRecoveryAdapters, undefined, () => current);
  current = false; finish(committed);
  await assert.rejects(request, /recovery_request_stale/);
  assert.deepEqual(journal.list(actorId), [reference]);
});

test("unknown catalog operations cannot be journaled or dispatched", async () => {
  const storage = new MemoryStorage(), journal = new OperationJournal(() => storage);
  for (const operation of ["catalog.sku.delete", "catalog.price.approve", "catalog.sku.create.extra", "catalog.category.save\n"]) {
    assert.throws(() => journal.remember(actorId, operation, entry.key));
    await assert.rejects(reconcileOperationRecovery({ ...entry, operation }, journal, noLegacy), /recovery_operation_unsupported/);
  }
  assert.equal(storage.length, 0);
});

test("other screens direct catalog recovery to its owner without offering access closure", () => {
  const journal = new OperationJournal(() => new MemoryStorage());
  const html = renderToStaticMarkup(createElement(OperationRecovery, { entries: [entry], journal, disabled: false, onResolved() {} }));
  assert.match(html, /href="\/administration\/catalog"/);
  assert.match(html, /<button[^>]*disabled[^>]*>Periksa hasil/);
  assert.doesNotMatch(html, /Selesaikan dan tutup|href="\/administration\/person-reviews"/);
});

test("catalog management permission enables its own navigation and protected login return", () => {
  assert.equal(identityDestinations(["catalog.manage"]).some(item => item.href === "/administration/catalog"), true);
  assert.equal(identityDestinations(["catalog.read", "technical.configure"]).some(item => item.href === "/administration/catalog"), false);
  assert.deepEqual(loginFields("/administration/catalog", "", ""), { returnPath: "/administration/catalog" });
});
