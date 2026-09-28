import assert from "node:assert/strict";
import test from "node:test";
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, type OperationRecoveryAdapters, type LegacyRecoveryAdapter } from "../src/modules/administration/operation-journal";
import { SourceMemoryStorage, sourceSession } from "./stock-source-fixtures";
import { actorId } from "./stock-count-fixtures";
import { outcome } from "./stock-source-revision-fixtures";
const legacy: LegacyRecoveryAdapter = { lookup: async () => { throw new Error("wrong legacy adapter"); }, close: async () => { throw new Error("wrong close adapter"); } };
const adapters = (value: unknown) => ({ "stock.source.revision": { lookup: async () => value },
  "stock.source": { lookup: async () => { throw new Error("wrong creation adapter"); } } }) as OperationRecoveryAdapters;
const operation = "stock.source.revision.propose";
test("revision journal stores only references and selects its dedicated receipt adapter", async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage);
  const entry = journal.remember(actorId, operation, "revision-key");
  assert.deepEqual(Object.keys(JSON.parse([...storage.values.values()][0])).sort(), ["actorId", "createdAt", "key", "operation"]);
  assert.deepEqual(operationRecoveryCapabilities(operation), { canCheck: false, canClose: false, unavailable: "adapter" });
  assert.deepEqual(operationRecoveryCapabilities(operation, adapters(null)), { canCheck: true, canClose: false, unavailable: null });
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { "stock.source": adapters(null)["stock.source"] }), /recovery_adapter_unavailable/);
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters(null), "close"), /recovery_close_unsupported/);
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapters(null)), { state: "unconfirmed" });
  assert.equal(journal.list(actorId).length, 1);
});
for (const action of ["propose", "approve", "reject", "withdraw"] as const) {
  test("revision " + action + " receipt clears only the exact actor/operation/key", async () => {
    const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage);
    const entry = journal.remember(actorId, "stock.source.revision." + action, "same");
    journal.remember(actorId, "stock.source.propose", "same"); journal.remember("77", entry.operation, "same");
    const result = { ...outcome, status: { propose: "proposed", approve: "approved", reject: "rejected", withdraw: "withdrawn" }[action],
      basePoolVersion: action === "propose" ? "1" : null, appliedPoolVersion: action === "approve" ? "2" : null, decisionId: action === "propose" ? null : "14" };
    assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapters({ state: "committed", result })),
      { state: "committed", result: { id: "12", version: "1" } });
    assert.equal(journal.list(actorId)[0].operation, "stock.source.propose"); assert.equal(journal.list("77").length, 1);
  });
}
test("revision recovery retains malformed mismatched and late receipts", async () => {
  const journal = new OperationJournal(() => storage), storage = new SourceMemoryStorage();
  const entry = journal.remember(actorId, operation, "key");
  for (const value of [undefined, {}, { state: "closed", result: null }, { state: "committed", result: { ...outcome, physicalPoolId: null } },
    { state: "committed", result: { ...outcome, status: "approved" } }, { state: "committed", result: { ...outcome, basePoolVersion: "9223372036854775807" } },
    { state: "committed", result: { ...outcome, proposalVersion: "2" } }, { state: "committed", result: { ...outcome, decisionId: "14" } },
    { state: "committed", result: { ...outcome, physicalPoolId: 9007199254740993 } }]) {
    await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters(value)), /recovery_receipt_invalid/);
  }
  let current = true;
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { "stock.source.revision": { lookup: async () => {
    current = false; return { state: "committed", result: { ...outcome, status: "proposed", proposalVersion: "1" } };
  } } }, undefined, () => current), /recovery_request_stale/);
  assert.equal(journal.list(actorId).length, 1);
});
test("revision recovery rejects invented operations without enabling creation fallback", () => {
  const journal = new OperationJournal(() => new SourceMemoryStorage(), () => new Date(sourceSession.serverTime));
  for (const operation of ["stock.source.revision.activate", "stock.source.revision.close", "stock.source.revision.approve\n"]) {
    assert.equal(operationRecoveryCapabilities(operation, adapters(null)).unavailable, "operation");
    assert.throws(() => journal.remember(actorId, operation, "key"), /recovery_storage_unavailable/);
  }
});

test("revision navigation survives login and MFA without granting unrelated staff access", async () => {
  const { identityReturnPath, identityDestinations, loginFields, stepUpFields } = await import("../src/modules/identity/identity-workflow");
  const { recoveryPage, operationLabel } = await import("../src/modules/administration/operation-recovery-navigation");
  const path = "/inventory/sources/revisions";
  assert.equal(identityReturnPath(path), path); assert.deepEqual(loginFields(path, "", ""), { returnPath: path });
  assert.equal(stepUpFields(sourceSession, "stock.source.approve", path).returnPath, path);
  for (const permission of ["stock.source.read", "stock.source.propose", "stock.source.approve"])
    assert.ok(identityDestinations([permission]).some(item => item.href === path));
  assert.equal(identityDestinations(["stock.count", "technical.configure"]).some(item => item.href === path), false);
  assert.equal(recoveryPage(operation).href, path);
  assert.equal(operationLabel(operation), "Pengajuan perubahan sumber stok");
});
