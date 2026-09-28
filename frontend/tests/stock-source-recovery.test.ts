import assert from "node:assert/strict";
import test from "node:test";
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, type OperationRecoveryAdapters, type LegacyRecoveryAdapter } from "../src/modules/administration/operation-journal";
import { identityDestinations, identityReturnPath, stepUpFields } from "../src/modules/identity/identity-workflow";
import { operationLabel, recoveryPage } from "../src/modules/administration/operation-recovery-navigation";
import { actorId } from "./stock-count-fixtures";
import { sourceResult, sourceSession, SourceMemoryStorage } from "./stock-source-fixtures";
const legacy: LegacyRecoveryAdapter = { lookup: async () => { throw new Error("wrong adapter"); }, close: async () => { throw new Error("wrong adapter"); } };
const adapters = (value: unknown) => ({ "stock.source": { lookup: async () => value } }) as OperationRecoveryAdapters;
test("source recovery has its own receipt adapter and cannot close or activate stock", async () => {
  const journal = new OperationJournal(() => new SourceMemoryStorage()); // Capabilities do not access storage.
  assert.equal(operationRecoveryCapabilities("stock.source.propose").canCheck, false);
  assert.equal(operationRecoveryCapabilities("stock.source.approve", adapters(null)).canCheck, true);
  assert.equal(operationRecoveryCapabilities("stock.source.approve", adapters(null)).canClose, false);
  assert.equal(operationRecoveryCapabilities("stock.source.activate", adapters(null)).unavailable, "operation");
  const entry = { actorId, operation: "stock.source.propose", key: "key", createdAt: sourceSession.serverTime };
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy), /recovery_adapter_unavailable/);
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters(null), "close"), /recovery_close_unsupported/);
});
test("source recovery retains absent malformed mismatched and late receipts", async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage);
  const entry = journal.remember(actorId, "stock.source.propose", "key");
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapters(null)), { state: "unconfirmed" });
  for (const value of [undefined, {}, { state: "closed", result: null }, { state: "committed", result: { ...sourceResult, status: "approved" } },
    { state: "committed", result: { ...sourceResult, proposalId: 123 } }, { state: "committed", result: { ...sourceResult, proposalVersion: "2" } }]) {
    await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters(value)), /recovery_receipt_invalid/);
  }
  let current = true;
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { "stock.source": { lookup: async () => {
    current = false; return { state: "committed", result: sourceResult };
  } } }, undefined, () => current), /recovery_request_stale/);
  assert.equal(journal.list(actorId).length, 1);
});
test("valid source receipt clears only matching reference with exact long identifiers", async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage);
  const entry = journal.remember(actorId, "stock.source.propose", "key");
  journal.remember(actorId, "stock.source.withdraw", "key"); journal.remember("77", "stock.source.propose", "key");
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapters({ state: "committed", result: sourceResult })),
    { state: "committed", result: { id: sourceResult.proposalId, version: "1" } });
  assert.equal(journal.list(actorId)[0].operation, "stock.source.withdraw"); assert.equal(journal.list("77").length, 1);
});
test("source navigation is separately granted and survives sign-in and MFA return paths", () => {
  assert.equal(identityDestinations(["stock.count", "technical.configure"]).some(item => item.href === "/inventory/sources"), false);
  for (const permission of ["stock.source.read", "stock.source.propose", "stock.source.approve"]) {
    assert.ok(identityDestinations([permission]).some(item => item.href === "/inventory/sources"));
  }
  assert.equal(identityReturnPath("/inventory/sources"), "/inventory/sources");
  assert.equal(stepUpFields(sourceSession, "stock.source.approve", "/inventory/sources").returnPath, "/inventory/sources");
  assert.equal(recoveryPage("stock.source.propose").href, "/inventory/sources");
  assert.equal(operationLabel("stock.source.approve"), "Persetujuan sumber stok");
});
