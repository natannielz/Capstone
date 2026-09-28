import assert from "node:assert/strict";
import test from "node:test";
import { StockSourceRevisionEditor } from "../src/modules/inventory/stock-source-revision-editor-state";
import type { StockSourceRevisionGateway } from "../src/modules/inventory/stock-source-revision-client";
import type { StockSourceGateway } from "../src/modules/inventory/stock-source-client";
import { OperationJournal, reconcileOperationRecovery } from "../src/modules/administration/operation-journal";
import { AccessFailure } from "../src/modules/administration/workflow";
import { actorId, location, warehouse } from "./stock-count-fixtures";
import { sourceSession, SourceMemoryStorage } from "./stock-source-fixtures";
import { current, proposal, outcome, settings } from "./stock-source-revision-fixtures";
import { parseCurrentSource, parseSourceRevision, parseSourceRevisionResult } from "../src/modules/inventory/stock-source-revision-workflow";
const otherWarehouse = { ...warehouse, location: { ...location, warehouseId: "109" } };
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(r => { resolve = r; }); return { promise, resolve }; }
function setup(overrides: Partial<StockSourceRevisionGateway> = {}, actions = ["stock.source.propose", "stock.source.read", "stock.source.approve"],
  choices: StockSourceGateway["warehouses"] = async () => [warehouse, otherWarehouse]) {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage); let now = 0, keys = 0;
  const gateway: StockSourceRevisionGateway = { sources: async () => ({ total: "1", items: [parseCurrentSource(current)] }),
    current: async () => parseCurrentSource(current), history: async () => ({ total: "1", items: [parseSourceRevision(proposal)] }),
    detail: async () => parseSourceRevision(proposal), submit: async () => parseSourceRevisionResult(outcome), receipt: async () => null, ...overrides };
  const editor = new StockSourceRevisionEditor({ actorId, actions, session: sourceSession, observedAt: 0, now: () => now, gateway, warehouses: choices,
    journal, newKey: () => "revision-" + ++keys });
  return { editor, journal, storage, advance: (value: number) => { now = value; } };
}
async function choose(editor: StockSourceRevisionEditor) {
  await editor.start(); await editor.chooseWarehouse(location); await editor.chooseSource(current.physicalPoolId);
}
async function review(editor: StockSourceRevisionEditor) {
  await choose(editor); editor.changeDraft("label", "Revised"); editor.changeDraft("reason", "Perubahan sumber"); editor.reviewProposal();
}
test("revision review preserves exact ages and physical identity, freezes inputs and requires confirmation", async () => {
  let calls = 0;
  const data = setup({ submit: async command => { calls++; assert.equal(command.body.reason, "Perubahan sumber");
    assert.ok("current" in command); if ("current" in command) { assert.equal(command.body.maximumAgeMilliseconds, "60001");
      assert.equal(command.body.safetyBufferBaseUnits, "9007199254740993"); assert.equal(command.body.physicalKey, settings.physicalKey); }
    return parseSourceRevisionResult(outcome); } });
  await review(data.editor); assert.equal(data.editor.snapshot().draft.maximumAgeSeconds, "60.001");
  data.editor.changeDraft("label", "changed after review"); assert.equal(data.editor.snapshot().review?.body.reason, "Perubahan sumber");
  await data.editor.submitReviewed(false); assert.equal(calls, 0);
  await data.editor.submitReviewed(true); await data.editor.submitReviewed(true);
  assert.equal(calls, 1); assert.equal(data.editor.snapshot().result?.proposalId, "12"); assert.equal(data.editor.snapshot().current, null);
});
test("unchanged settings and invalid fractional ages cannot enter review", async () => {
  const { editor } = setup(); await choose(editor); editor.changeDraft("reason", "Reason"); editor.reviewProposal();
  assert.equal(editor.snapshot().review, null);
  editor.changeDraft("label", "Changed");
  for (const age of ["0", "1.0001", "-1", "1e3", "NaN", "99999999999999999999"]) {
    editor.changeDraft("maximumAgeSeconds", age); editor.reviewProposal(); assert.equal(editor.snapshot().review, null);
  }
});
test("stale or active source blocks approval but permits rejection and withdrawal review", async () => {
  for (const variant of [{ ...current, poolVersion: "2", revisionProposalId: "18" }, { ...current, poolState: "active" }]) {
    const { editor } = setup({ current: async () => parseCurrentSource(variant) });
    await choose(editor); await editor.chooseProposal("12"); editor.reviewDecision("approve", "REF", "Reason");
    assert.equal(editor.snapshot().review, null);
    editor.reviewDecision("reject", "REF", "Reason"); assert.equal(editor.snapshot().review?.operation, "stock.source.revision.reject");
    editor.cancelReview(); editor.reviewDecision("withdraw", "REF", "Reason");
    assert.equal(editor.snapshot().review?.operation, "stock.source.revision.withdraw");
  }
});
test("permissions stay separate and a command-only actor can select location for own recovery", async () => {
  let reads = 0; const purposes: string[] = [];
  const data = setup({ sources: async () => { reads++; return { total: "0", items: [] }; }, receipt: async () => parseSourceRevisionResult(outcome) },
    ["stock.source.propose"], async (_actor, purpose) => { purposes.push(purpose); return [warehouse]; });
  const entry = data.journal.remember(actorId, "stock.source.revision.propose", "pending");
  await data.editor.start(); assert.equal(data.editor.selectionLocked, false); assert.equal(data.editor.locked, true);
  await data.editor.chooseWarehouse(location); assert.equal(reads, 0);
  assert.deepEqual(await data.editor.receipt(entry), { state: "committed", result: parseSourceRevisionResult(outcome) });
  assert.deepEqual(purposes, ["inventory-source-propose"]);
  data.editor.reviewProposal(); assert.equal(data.editor.snapshot().review, null);
  const reader = setup({}, ["stock.source.read"]); await choose(reader.editor); await reader.editor.chooseProposal("12");
  reader.editor.reviewDecision("approve", "REF", "Reason"); assert.equal(reader.editor.snapshot().review, null);
});
test("MFA expiry after review prevents approve and reject without dispatch", async () => {
  let calls = 0; const data = setup({ submit: async () => { calls++; return parseSourceRevisionResult(outcome); } });
  await choose(data.editor); await data.editor.chooseProposal("12"); data.editor.reviewDecision("approve", "REF", "Reason");
  assert.ok(data.editor.snapshot().review); data.advance(300000); await data.editor.submitReviewed(true);
  assert.equal(calls, 0); assert.match(String(data.editor.snapshot().error), /step_up_required/);
});
test("uncertain revision is never resubmitted or replaced and contains only a persisted reference", async () => {
  const sent: string[] = []; const data = setup({ submit: async (_command, key) => { sent.push(key); throw new Error("lost response"); } });
  await review(data.editor); await data.editor.submitReviewed(true); await data.editor.submitReviewed(true);
  data.editor.changeDraft("label", "other"); data.editor.reviewProposal(); await data.editor.submitReviewed(true);
  assert.deepEqual(sent, ["revision-1"]); assert.equal(data.editor.snapshot().attempt?.operation, "stock.source.revision.propose");
  assert.deepEqual(Object.keys(JSON.parse([...data.storage.values.values()][0])).sort(), ["actorId", "createdAt", "key", "operation"]);
  assert.equal("retry" in data.editor, false);
});
test("valid receipt clears uncertainty and forces fresh data before another change", async () => {
  const data = setup({ submit: async () => { throw new Error("lost"); }, receipt: async () => parseSourceRevisionResult(outcome) });
  await review(data.editor); await data.editor.submitReviewed(true);
  const entry = data.journal.list(actorId)[0];
  await reconcileOperationRecovery(entry, data.journal, { lookup: async () => { throw new Error("legacy"); }, close: async () => null },
    { "stock.source.revision": { lookup: data.editor.receipt } });
  data.editor.reconciled(); assert.equal(data.editor.snapshot().attempt, null); assert.equal(data.editor.snapshot().current, null);
  assert.equal(data.editor.snapshot().references.length, 0);
});
test("double click and late successful response after disposal retain uncertainty", async () => {
  const late = deferred<ReturnType<typeof parseSourceRevisionResult>>(); let calls = 0;
  const data = setup({ submit: async () => { calls++; return late.promise; } });
  await review(data.editor); const pending = data.editor.submitReviewed(true); await data.editor.submitReviewed(true);
  assert.equal(calls, 1); data.editor.dispose(); late.resolve(parseSourceRevisionResult(outcome)); await pending;
  assert.equal(data.editor.snapshot().result, null); assert.equal(data.editor.snapshot().current, null);
  assert.equal(data.editor.snapshot().attempt, null); assert.equal(data.journal.list(actorId).length, 1);
});
test("blocked or newly corrupt journal stops writes before dispatch", async () => {
  for (const corrupt of [false, true]) {
    let calls = 0; const data = setup({ submit: async () => { calls++; return parseSourceRevisionResult(outcome); } });
    await review(data.editor);
    if (corrupt) data.storage.setItem("unit-toko:pending:v1:" + actorId + ":broken", "{}");
    else data.storage.setItem = () => { throw new Error("blocked"); };
    await data.editor.submitReviewed(true); assert.equal(calls, 0);
  }
});
test("expired sessions hide private data and reject late receipt without losing its reference", async () => {
  const late = deferred<ReturnType<typeof parseSourceRevisionResult>>(), data = setup({ receipt: async () => late.promise });
  const entry = data.journal.remember(actorId, "stock.source.revision.propose", "pending");
  await data.editor.start(); await data.editor.chooseWarehouse(location); const pending = data.editor.receipt(entry);
  data.advance(600000); data.editor.checkSession(); late.resolve(parseSourceRevisionResult(outcome));
  await assert.rejects(pending, /recovery_request_stale/); assert.equal(data.editor.snapshot().expired, true);
  assert.equal(data.editor.snapshot().warehouse, null); assert.equal(data.journal.list(actorId).length, 1);
});
test("a replaced location discards late sources and prevents a foreign receipt lookup", async () => {
  const late = deferred<{ total: string; items: ReturnType<typeof parseCurrentSource>[] }>(); let reads = 0, receipts = 0;
  const data = setup({ sources: async () => ++reads === 1 ? late.promise : { total: "0", items: [] },
    receipt: async () => { receipts++; return null; } });
  await data.editor.start(); const old = data.editor.chooseWarehouse(location); await data.editor.chooseWarehouse(otherWarehouse.location);
  late.resolve({ total: "1", items: [parseCurrentSource(current)] }); await old;
  assert.equal(data.editor.snapshot().sources.items.length, 0);
  await assert.rejects(data.editor.receipt({ actorId: "77", operation: "stock.source.revision.propose", key: "x", createdAt: sourceSession.serverTime }), /recovery_request_stale/);
  assert.equal(receipts, 0);
});
test("changing recovery location invalidates the late result", async () => {
  const late = deferred<ReturnType<typeof parseSourceRevisionResult>>(), data = setup({ receipt: async () => late.promise });
  const entry = data.journal.remember(actorId, "stock.source.revision.propose", "pending");
  await data.editor.start(); await data.editor.chooseWarehouse(location); const pending = data.editor.receipt(entry);
  await data.editor.chooseWarehouse(otherWarehouse.location); late.resolve(parseSourceRevisionResult(outcome));
  await assert.rejects(pending, /recovery_request_stale/); assert.equal(data.journal.list(actorId).length, 1);
});
test("revoked scope hides the previous view and leaves unresolved reference recoverable", async () => {
  const data = setup({ receipt: async () => { throw new AccessFailure(403, "access_denied"); } });
  const entry = data.journal.remember(actorId, "stock.source.revision.propose", "pending");
  await data.editor.start(); await data.editor.chooseWarehouse(location);
  try { await data.editor.receipt(entry); } catch (error) { data.editor.recoveryFailed(error); }
  assert.equal(data.editor.snapshot().suspended, true); assert.equal(data.editor.snapshot().warehouse, null);
  assert.equal(data.journal.list(actorId).length, 1);
});

test("changing the selected source discards its late current settings", async () => {
  const late = deferred<ReturnType<typeof parseCurrentSource>>(), second = parseCurrentSource({ ...current, physicalPoolId: "9007199254740994", settings: { ...settings, physicalKey: "other-pool" } });
  const data = setup({ sources: async () => ({ total: "2", items: [parseCurrentSource(current), second] }),
    current: async (_actor, _place, id) => id === current.physicalPoolId ? late.promise : second,
    history: async () => ({ total: "0", items: [] }) });
  await data.editor.start(); await data.editor.chooseWarehouse(location);
  const old = data.editor.chooseSource(current.physicalPoolId); await data.editor.chooseSource(second.physicalPoolId);
  late.resolve(parseCurrentSource(current)); await old;
  assert.equal(data.editor.snapshot().current?.physicalPoolId, second.physicalPoolId);
});
test("out-of-order history detail never replaces the newer deliberate selection", async () => {
  const late = deferred<ReturnType<typeof parseSourceRevision>>(), newer = parseSourceRevision({ ...proposal, id: "13", reason: "Newer selection" });
  const data = setup({ history: async () => ({ total: "2", items: [parseSourceRevision(proposal), newer] }),
    detail: async (_actor, _place, id) => id === "12" ? late.promise : newer });
  await choose(data.editor); const old = data.editor.chooseProposal("12"); await data.editor.chooseProposal("13");
  late.resolve(parseSourceRevision(proposal)); await old; assert.equal(data.editor.snapshot().selected?.id, "13");
});
test("foreign current or history details conceal the source instead of entering review", async () => {
  for (const gateway of [{ current: async () => parseCurrentSource({ ...current, physicalPoolId: "88" }) },
    { detail: async () => parseSourceRevision({ ...proposal, physicalPoolId: "88" }) }]) {
    const data = setup(gateway); await choose(data.editor); await data.editor.chooseProposal("12");
    assert.equal(data.editor.snapshot().suspended, true); assert.equal(data.editor.snapshot().current, null); assert.equal(data.editor.snapshot().review, null);
  }
});
test("pagination bounds stop invalid reads and changing page clears previous details", async () => {
  const offsets: number[] = []; const data = setup({ history: async (_actor, _place, _pool, offset) => {
    offsets.push(offset ?? 0); return offset ? { total: "26", items: [parseSourceRevision({ ...proposal, id: "99" })] } : { total: "1", items: [parseSourceRevision(proposal)] }; } });
  await choose(data.editor); await data.editor.chooseProposal("12"); assert.ok(data.editor.snapshot().selected);
  await data.editor.loadHistory(-1); await data.editor.loadHistory(10001); await data.editor.loadHistory(0.5);
  assert.deepEqual(offsets, [0]); await data.editor.loadHistory(25);
  assert.equal(data.editor.snapshot().selected, null); assert.equal(data.editor.snapshot().historyOffset, 25);
});
test("a mismatched signed-in account cannot construct a revision editor", () => {
  assert.throws(() => new StockSourceRevisionEditor({ actorId: "77", actions: ["stock.source.read"], session: sourceSession,
    observedAt: 0, now: () => 0, gateway: {} as StockSourceRevisionGateway, warehouses: async () => [],
    journal: new OperationJournal(() => new SourceMemoryStorage()) }), /actor_changed/);
});
test("a mismatched receipt cannot clear the original in-memory revision attempt", async () => {
  const data = setup({ submit: async () => { throw new Error("lost"); }, receipt: async () => parseSourceRevisionResult({ ...outcome, physicalPoolId: "88" }) });
  await review(data.editor); await data.editor.submitReviewed(true);
  await assert.rejects(data.editor.receipt(data.journal.list(actorId)[0]), /stock_source_revision_response_invalid/);
  assert.equal(data.journal.list(actorId).length, 1); assert.ok(data.editor.snapshot().attempt);
});
