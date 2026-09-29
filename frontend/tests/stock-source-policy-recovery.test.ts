import assert from "node:assert/strict";
import test from "node:test";
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery,
  type OperationRecoveryAdapters, type LegacyRecoveryAdapter } from "../src/modules/administration/operation-journal";
import { SourceMemoryStorage } from "./stock-source-fixtures";
import { actorId } from "./stock-count-fixtures";
import { outcome, approvedOutcome } from "./stock-source-policy-fixtures";
const operation = "stock.source.policy.propose";
const legacy: LegacyRecoveryAdapter = { lookup: async () => { throw new Error("wrong legacy adapter"); }, close: async () => { throw new Error("wrong close adapter"); } };
const adapters = (value: unknown) => ({ "stock.source.policy": { lookup: async () => value },
  "stock.source": { lookup: async () => { throw new Error("wrong source adapter"); } },
  "stock.source.alias": { lookup: async () => { throw new Error("wrong alias adapter"); } },
  "stock.source.revision": { lookup: async () => { throw new Error("wrong revision adapter"); } },
  "stock.source.suspend": { lookup: async () => { throw new Error("wrong suspension adapter"); } } }) as OperationRecoveryAdapters;

test("policy journal survives reload with only original references and requires a dedicated lookup-only adapter", async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage);
  const entry = journal.remember(actorId, operation, "policy-key");
  assert.deepEqual(Object.keys(JSON.parse([...storage.values.values()][0])).sort(), ["actorId", "createdAt", "key", "operation"]);
  assert.deepEqual(new OperationJournal(() => storage).list(actorId), [entry]);
  assert.deepEqual(operationRecoveryCapabilities(operation), { canCheck: false, canClose: false, unavailable: "adapter" });
  assert.deepEqual(operationRecoveryCapabilities(operation, adapters(null)), { canCheck: true, canClose: false, unavailable: null });
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { "stock.source": adapters(null)["stock.source"] }), /recovery_adapter_unavailable/);
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters(null), "close"), /recovery_close_unsupported/);
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapters(null)), { state: "unconfirmed" });
  assert.equal(journal.list(actorId).length, 1);
});
for (const action of ["propose", "approve", "reject", "withdraw"] as const) {
  test("policy " + action + " receipt clears only its exact actor operation and key", async () => {
    const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage);
    const entry = journal.remember(actorId, "stock.source.policy." + action, "same");
    journal.remember(actorId, "stock.source.propose", "same"); journal.remember(actorId, "stock.source.alias.propose", "same");
    journal.remember(actorId, entry.operation, "different"); journal.remember("77", entry.operation, "same");
    const result = action === "propose" ? outcome : action === "approve" ? approvedOutcome
      : { ...outcome, status: action === "reject" ? "rejected" : "withdrawn", basePolicyVersion: null, decisionId: "999" };
    assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapters({ state: "committed", result })),
      { state: "committed", result: { id: outcome.proposalId, version: "1" } });
    assert.equal(journal.list(actorId).length, 3); assert.equal(journal.list("77").length, 1);
  });
}
test("policy recovery retains malformed private mismatched and imprecise receipts", async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), entry = journal.remember(actorId, operation, "key");
  for (const value of [undefined, {}, { state: "closed", result: null }, { state: "committed", result: null },
    { state: "committed", result: outcome, reason: "private" }, ...[
      { proposalId: "1\n" }, { physicalPoolId: 9007199254740993 }, { basePolicyVersion: "00" },
      { basePolicyVersion: "9223372036854775807" }, { proposalVersion: "2" }, { decisionId: "1" },
      { policyId: "1" }, { policyVersion: "1" }, { status: "approved" }, { personApproval: "private" },
    ].map(change => ({ state: "committed", result: { ...outcome, ...change } }))])
    await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters(value)), /recovery_receipt_invalid/);
  const approveEntry = journal.remember(actorId, "stock.source.policy.approve", "approval");
  for (const change of [{ policyId: null }, { policyVersion: "0" }, { policyVersion: "9223372036854775808" },
    { decisionId: null }, { basePolicyVersion: "0" }, { status: "withdrawn" }])
    await assert.rejects(reconcileOperationRecovery(approveEntry, journal, legacy, adapters({ state: "committed", result: { ...approvedOutcome, ...change } })), /recovery_receipt_invalid/);
  assert.equal(journal.list(actorId).length, 2);
});
test("policy late receipts and transport errors retain the original recovery entry", async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), entry = journal.remember(actorId, operation, "key");
  let current = true;
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { "stock.source.policy": { lookup: async () => {
    current = false; return { state: "committed", result: outcome };
  } } } as OperationRecoveryAdapters, undefined, () => current), /recovery_request_stale/);
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { "stock.source.policy": { lookup: async () => { throw new TypeError("offline"); } } } as OperationRecoveryAdapters), /offline/);
  assert.equal(journal.list(actorId).length, 1);
});
test("policy fabricated operations cannot be persisted or fall through to another workflow", () => {
  const journal = new OperationJournal(() => new SourceMemoryStorage());
  for (const value of ["stock.source.policy.activate", "stock.source.policy.close", "stock.source.policy.approve\n", "stock.source.policy", "stock.source.policy.proposed"]) {
    assert.equal(operationRecoveryCapabilities(value, adapters(null)).unavailable, "operation");
    assert.throws(() => journal.remember(actorId, value, "key"), /recovery_storage_unavailable/);
  }
});
test("policy journal rejects noncanonical command keys when recording and loading references", () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage);
  for (const key of ["key\n", "key\r", "key\r\n", "key\u2028", "key\u2029"])
    assert.throws(() => journal.remember(actorId, operation, key), /recovery_storage_unavailable/);
  const entry = journal.remember(actorId, operation, "valid");
  const originalKey = [...storage.values.keys()][0];
  storage.values.delete(originalKey);
  storage.values.set(originalKey + "\n", JSON.stringify({ ...entry, key: entry.key + "\n" }));
  assert.throws(() => new OperationJournal(() => storage).list(actorId), /recovery_storage_unavailable/);
});