import assert from "node:assert/strict";
import test from "node:test";
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, type OperationRecoveryAdapters, type LegacyRecoveryAdapter } from "../src/modules/administration/operation-journal";
import { SourceMemoryStorage, sourceSession } from "./stock-source-fixtures";
import { actorId } from "./stock-count-fixtures";
import { outcome, proposal } from "./stock-source-alias-fixtures";
const legacy: LegacyRecoveryAdapter = { lookup: async () => { throw new Error("wrong legacy adapter"); }, close: async () => { throw new Error("wrong close adapter"); } };
const adapters = (value: unknown) => ({ "stock.source.alias": { lookup: async () => value },
  "stock.source": { lookup: async () => { throw new Error("wrong source adapter"); } },
  "stock.source.revision": { lookup: async () => { throw new Error("wrong revision adapter"); } } }) as OperationRecoveryAdapters;
const operation = "stock.source.alias.propose";
test("alias journal retains only reference fields and needs its dedicated lookup-only adapter", async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage);
  const entry = journal.remember(actorId, operation, "alias-key");
  assert.deepEqual(Object.keys(JSON.parse([...storage.values.values()][0])).sort(), ["actorId", "createdAt", "key", "operation"]);
  assert.deepEqual(operationRecoveryCapabilities(operation), { canCheck: false, canClose: false, unavailable: "adapter" });
  assert.deepEqual(operationRecoveryCapabilities(operation, adapters(null)), { canCheck: true, canClose: false, unavailable: null });
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { "stock.source": adapters(null)["stock.source"] }), /recovery_adapter_unavailable/);
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters(null), "close"), /recovery_close_unsupported/);
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapters(null)), { state: "unconfirmed" });
  assert.equal(new OperationJournal(() => storage).list(actorId).length, 1);
});
for (const action of ["propose", "approve", "reject", "withdraw"] as const) test("alias " + action + " recovery clears only its exact reference", async () => {
  const journal = new OperationJournal(() => storage), storage = new SourceMemoryStorage();
  const entry = journal.remember(actorId, "stock.source.alias." + action, "same");
  journal.remember(actorId, "stock.source.propose", "same"); journal.remember(actorId, "stock.source.revision.propose", "same"); journal.remember("77", entry.operation, "same");
  const result = { ...outcome, status: { propose: "proposed", approve: "approved", reject: "rejected", withdraw: "withdrawn" }[action],
    baseAliasVersion: action === "propose" ? "0" : null, appliedAliasVersion: action === "approve" ? "1" : null, decisionId: action === "propose" ? null : "14" };
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapters({ state: "committed", result })),
    { state: "committed", result: { id: proposal.id, version: "1" } });
  assert.equal(journal.list(actorId).length, 2); assert.equal(journal.list("77").length, 1);
});
test("alias recovery retains malformed, mismatched and late results", async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), entry = journal.remember(actorId, operation, "key");
  for (const value of [undefined, {}, { state: "closed", result: null }, { state: "committed", result: { ...outcome, physicalPoolId: null } },
    ...[{ status: "approved" }, { poolVersion: "0" }, { baseAliasVersion: "9223372036854775807" }, { baseAliasVersion: "00" },
      { proposalVersion: "2" }, { decisionId: "14" }, { physicalPoolId: 9007199254740993 }, { extra: "hidden" }]
      .map(change => ({ state: "committed", result: { ...outcome, ...change } }))]) {
    await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters(value)), /recovery_receipt_invalid/);
  }
  let current = true;
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { "stock.source.alias": { lookup: async () => {
    current = false; return { state: "committed", result: { ...outcome, status: "proposed", proposalVersion: "1" } };
  } } } as OperationRecoveryAdapters, undefined, () => current), /recovery_request_stale/);
  assert.equal(journal.list(actorId).length, 1);
});
test("alias recovery rejects fabricated operations before any fallback", () => {
  const journal = new OperationJournal(() => new SourceMemoryStorage());
  for (const value of ["stock.source.alias.activate", "stock.source.alias.close", "stock.source.alias.approve\n", "stock.source.alias.revision.propose"]) {
    assert.equal(operationRecoveryCapabilities(value, adapters(null)).unavailable, "operation");
    assert.throws(() => journal.remember(actorId, value, "key"), /recovery_storage_unavailable/);
  }
});
test("alias navigation returns through sign-in and MFA without granting unrelated access", async () => {
  const { identityReturnPath, identityDestinations, loginFields, stepUpFields } = await import("../src/modules/identity/identity-workflow");
  const { recoveryPage, operationLabel } = await import("../src/modules/administration/operation-recovery-navigation");
  const path = "/inventory/sources/aliases";
  assert.equal(identityReturnPath(path), path); assert.deepEqual(loginFields(path, "", ""), { returnPath: path });
  assert.equal(stepUpFields(sourceSession, "stock.source.approve", path).returnPath, path);
  for (const permission of ["stock.source.read", "stock.source.propose", "stock.source.approve"]) assert.ok(identityDestinations([permission]).some(item => item.href === path));
  assert.equal(identityDestinations(["stock.count", "technical.configure"]).some(item => item.href === path), false);
  assert.equal(recoveryPage(operation).href, path); assert.equal(operationLabel(operation), "Pengajuan pemetaan kanal");
  for (const suffix of ["/other", "?returnPath=https://outside.test", "//", "\n"]) assert.notEqual(identityReturnPath(path + suffix), path + suffix);
});
