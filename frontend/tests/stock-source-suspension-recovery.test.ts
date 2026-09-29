import assert from "node:assert/strict";
import test from "node:test";
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery,
  type OperationRecoveryAdapters, type LegacyRecoveryAdapter } from "../src/modules/administration/operation-journal";
import { SourceMemoryStorage } from "./stock-source-fixtures";
import { actorId } from "./stock-count-fixtures";
import { outcome } from "./stock-source-suspension-fixtures";
const operation = "stock.source.suspend";
const legacy: LegacyRecoveryAdapter = { lookup: async () => { throw new Error("wrong legacy adapter"); }, close: async () => { throw new Error("wrong close adapter"); } };
const adapters = (value: unknown) => ({ "stock.source.suspend": { lookup: async () => value },
  "stock.source": { lookup: async () => { throw new Error("wrong source adapter"); } },
  "stock.source.alias": { lookup: async () => { throw new Error("wrong alias adapter"); } },
  "stock.source.revision": { lookup: async () => { throw new Error("wrong revision adapter"); } } }) as OperationRecoveryAdapters;

test("suspension journal stores only references and requires its dedicated lookup-only adapter", async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage);
  const entry = journal.remember(actorId, operation, "stop-key");
  assert.deepEqual(Object.keys(JSON.parse([...storage.values.values()][0])).sort(), ["actorId", "createdAt", "key", "operation"]);
  assert.deepEqual(operationRecoveryCapabilities(operation), { canCheck: false, canClose: false, unavailable: "adapter" });
  assert.deepEqual(operationRecoveryCapabilities(operation, adapters(null)), { canCheck: true, canClose: false, unavailable: null });
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { "stock.source": adapters(null)["stock.source"] }), /recovery_adapter_unavailable/);
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters(null), "close"), /recovery_close_unsupported/);
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapters(null)), { state: "unconfirmed" });
  assert.equal(new OperationJournal(() => storage).list(actorId).length, 1);
});
test("suspension receipt clears only the exact actor operation and key", async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), entry = journal.remember(actorId, operation, "same");
  journal.remember(actorId, "stock.source.propose", "same"); journal.remember(actorId, "stock.source.alias.propose", "same");
  journal.remember(actorId, operation, "different"); journal.remember("77", operation, "same");
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapters({ state: "committed", result: outcome })),
    { state: "committed", result: { id: outcome.suspensionId, version: outcome.suspensionVersion } });
  assert.equal(journal.list(actorId).length, 3); assert.equal(journal.list("77").length, 1);
});
test("suspension journal retains malformed private and ambiguous numeric receipts", async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), entry = journal.remember(actorId, operation, "key");
  for (const value of [undefined, {}, { state: "closed", result: null }, { state: "committed", result: null },
    { state: "committed", result: outcome, reason: "private" }, ...[{ status: "active" }, { poolVersion: "0" },
      { suspensionVersion: "01" }, { suspensionVersion: "9223372036854775808" }, { suspensionId: "1\n" },
      { physicalPoolId: 9007199254740993 }, { extra: "hidden" }].map(change => ({ state: "committed", result: { ...outcome, ...change } }))]) {
    await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters(value)), /recovery_receipt_invalid/);
    assert.equal(journal.list(actorId).length, 1);
  }
});
test("suspension late receipt and transport errors keep the original recovery reference", async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), entry = journal.remember(actorId, operation, "key");
  let current = true;
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { "stock.source.suspend": { lookup: async () => {
    current = false; return { state: "committed", result: outcome };
  } } } as OperationRecoveryAdapters, undefined, () => current), /recovery_request_stale/);
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { "stock.source.suspend": { lookup: async () => { throw new TypeError("offline"); } } } as OperationRecoveryAdapters), /offline/);
  assert.equal(journal.list(actorId).length, 1);
});
test("suspension fabricated operations cannot enter the journal or use another adapter", () => {
  const journal = new OperationJournal(() => new SourceMemoryStorage());
  for (const value of ["stock.source.suspend.approve", "stock.source.suspend.close", "stock.source.suspend\n", "stock.source.resume", "stock.source.suspended"]) {
    assert.equal(operationRecoveryCapabilities(value, adapters(null)).unavailable, "operation");
    assert.throws(() => journal.remember(actorId, value, "key"), /recovery_storage_unavailable/);
  }
});
