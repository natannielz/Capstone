import assert from "node:assert/strict";
import test from "node:test";
import { StockSourceEditor } from "../src/modules/inventory/stock-source-editor-state";
import type { StockSourceGateway } from "../src/modules/inventory/stock-source-client";
import { OperationJournal } from "../src/modules/administration/operation-journal";
import { AccessFailure } from "../src/modules/administration/workflow";
import { actorId, location, warehouse } from "./stock-count-fixtures";
import { sourceDraft, sourceDetail, sourceResult, sourceSession, SourceMemoryStorage } from "./stock-source-fixtures";
const second = { ...warehouse, location: { ...location, warehouseId: "109" } };
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(r => { resolve = r; }); return { promise, resolve }; }
function setup(overrides: Partial<StockSourceGateway> = {}, actions = ["stock.source.propose", "stock.source.read", "stock.source.approve"]) {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage);
  let now = 0, keys = 0;
  const gateway: StockSourceGateway = { warehouses: async () => [warehouse, second], history: async () => ({ total: "1", items: [sourceDetail] }),
    detail: async () => sourceDetail, submit: async () => sourceResult, receipt: async () => null, ...overrides };
  const editor = new StockSourceEditor({ actorId, actions, session: sourceSession, observedAt: 0, now: () => now, gateway, journal, newKey: () => "source-" + ++keys });
  return { editor, journal, storage, advance: (value: number) => { now = value; } };
}
async function proposal(editor: StockSourceEditor) {
  await editor.start(); await editor.chooseWarehouse(location);
  for (const [field, value] of Object.entries(sourceDraft)) editor.changeDraft(field as keyof typeof sourceDraft, value);
  editor.reviewProposal();
}
test("source proposal needs explicit settings and confirmation, then clears its reviewed form", async () => {
  let calls = 0;
  const data = setup({ submit: async command => { calls++; assert.equal(command.body.reason, sourceDraft.reason); return sourceResult; } });
  await data.editor.start(); data.editor.reviewProposal(); assert.equal(data.editor.snapshot().review, null);
  await data.editor.chooseWarehouse(location); data.editor.reviewProposal(); assert.equal(data.editor.snapshot().review, null);
  for (const [field, value] of Object.entries(sourceDraft)) data.editor.changeDraft(field as keyof typeof sourceDraft, value);
  data.editor.reviewProposal(); data.editor.changeDraft("reason", "changed"); await data.editor.submitReviewed(false); assert.equal(calls, 0);
  await data.editor.submitReviewed(true); assert.equal(calls, 1); assert.equal(data.editor.snapshot().draft.reason, "");
  assert.deepEqual(data.editor.snapshot().result, sourceResult); await data.editor.submitReviewed(true); assert.equal(calls, 1);
});
test("independent permissions control work modes and review requires explicit history access", async () => {
  const purposes: string[] = [];
  const reader = setup({ warehouses: async (_actor, purpose) => { purposes.push(purpose); return [warehouse]; } }, ["stock.source.read"]);
  await reader.editor.start(); assert.equal(reader.editor.snapshot().mode, "history");
  await reader.editor.changeMode("propose"); assert.equal(reader.editor.snapshot().mode, "history");
  await reader.editor.chooseWarehouse(location); await reader.editor.chooseProposal(sourceDetail.id);
  reader.editor.reviewDecision("approve", "REF", "reason"); assert.equal(reader.editor.snapshot().review, null);
  assert.deepEqual(purposes, ["inventory-source-history"]);
  let histories = 0;
  const reviewer = setup({ history: async () => { histories++; return { total: "0", items: [] }; } }, ["stock.source.approve"]);
  await reviewer.editor.start(); await reviewer.editor.chooseWarehouse(location);
  assert.equal(reviewer.editor.snapshot().mode, "review"); assert.equal(histories, 0);
});
test("replaced location discards late history and detail", async () => {
  const history = deferred<{ total: string; items: typeof sourceDetail[] }>(), detail = deferred<typeof sourceDetail>();
  let reads = 0;
  const data = setup({ history: async () => ++reads === 1 ? history.promise : { total: "0", items: [] }, detail: async () => detail.promise }, ["stock.source.read"]);
  await data.editor.start(); const old = data.editor.chooseWarehouse(location); await data.editor.chooseWarehouse(second.location);
  history.resolve({ total: "1", items: [sourceDetail] }); await old; assert.equal(data.editor.snapshot().history.items.length, 0);
  await data.editor.chooseWarehouse(location); await data.editor.loadHistory(); // No selected row can be fabricated.
  await data.editor.chooseProposal(sourceDetail.id); assert.equal(data.editor.snapshot().selected, null);
});
test("decision uses freshly fetched immutable proposal and current MFA", async () => {
  const decided = { ...sourceDetail, status: "rejected" as const, decision: { status: "rejected" as const, decidedAt: "2026-09-28T00:30:00Z", reviewReference: "OTHER", reason: "Earlier" } };
  let detailCalls = 0;
  const data = setup({ detail: async () => ++detailCalls === 1 ? decided : sourceDetail });
  await data.editor.start(); await data.editor.changeMode("review"); await data.editor.chooseWarehouse(location);
  await data.editor.chooseProposal(sourceDetail.id); assert.equal(data.editor.snapshot().selected?.status, "rejected");
  data.editor.reviewDecision("approve", "REF", "Review"); assert.equal(data.editor.snapshot().review, null);
  await data.editor.chooseProposal(sourceDetail.id); data.editor.reviewDecision("approve", "REF", "Review");
  assert.equal(data.editor.snapshot().review?.operation, "stock.source.approve");
  data.advance(300000); data.editor.checkSession(); await data.editor.submitReviewed(true);
  assert.equal(data.editor.snapshot().result, null); assert.equal(data.journal.list(actorId).length, 0);
  assert.match(String(data.editor.snapshot().error), /step_up_required/);
});
test("uncertain requests retain only reference, freeze original payload and retry same key", async () => {
  const sent: string[] = [];
  const data = setup({ submit: async (_command, key) => { sent.push(key); throw new Error("response lost"); } });
  await proposal(data.editor); await data.editor.submitReviewed(true);
  data.editor.changeDraft("reason", "new"); await data.editor.chooseWarehouse(second.location); await data.editor.changeMode("history");
  assert.equal(data.editor.snapshot().attempt?.body.reason, sourceDraft.reason);
  assert.equal(data.editor.snapshot().warehouse?.location.warehouseId, location.warehouseId);
  await data.editor.retry(); assert.deepEqual(sent, ["source-1", "source-1"]);
  const saved = JSON.parse([...data.storage.values.values()][0]);
  assert.deepEqual(Object.keys(saved).sort(), ["actorId", "createdAt", "key", "operation"]);
  assert.doesNotMatch(JSON.stringify(saved), /Pengajuan|PIC|physicalKey/);
});
test("reloaded recovery permits deliberate warehouse selection but blocks new writes", async () => {
  let lookups = 0;
  const data = setup({ receipt: async (entry, place) => { lookups++; assert.equal(entry.key, "pending"); assert.deepEqual(place, location); return sourceResult; } });
  const entry = data.journal.remember(actorId, "stock.source.propose", "pending");
  await data.editor.start(); assert.equal(data.editor.locked, true); assert.equal(data.editor.selectionLocked, false);
  await assert.rejects(data.editor.receipt(entry), /stock_source_selection_required/);
  await data.editor.chooseWarehouse(location); data.editor.reviewProposal(); assert.equal(data.editor.snapshot().review, null);
  assert.deepEqual(await data.editor.receipt(entry), { state: "committed", result: sourceResult }); assert.equal(lookups, 1);
});
test("double submission and late results after disposal cannot create confirmed UI state", async () => {
  const late = deferred<typeof sourceResult>(); let calls = 0;
  const data = setup({ submit: async () => { calls++; return late.promise; } });
  await proposal(data.editor); const pending = data.editor.submitReviewed(true); await data.editor.submitReviewed(true);
  assert.equal(calls, 1); data.editor.dispose(); late.resolve(sourceResult); await pending;
  assert.equal(data.editor.snapshot().result, null); assert.equal(data.editor.snapshot().warehouses.length, 0);
  assert.equal(data.journal.list(actorId).length, 1);
});
test("blocked journal prevents dispatch and foreign recovery cannot query", async () => {
  let calls = 0;
  const data = setup({ submit: async () => { calls++; return sourceResult; } });
  await proposal(data.editor); data.storage.setItem = () => { throw new Error("blocked"); }; await data.editor.submitReviewed(true);
  assert.equal(calls, 0); assert.match(String(data.editor.snapshot().error), /recovery_storage_unavailable/);
  await assert.rejects(data.editor.receipt({ actorId: "77", operation: "stock.source.propose", key: "x", createdAt: sourceDetail.proposedAt }), /recovery_request_stale/);
});
test("session expiry hides private setup and a late recovery cannot clear pending reference", async () => {
  const late = deferred<typeof sourceResult>(), data = setup({ receipt: async () => late.promise });
  const entry = data.journal.remember(actorId, "stock.source.propose", "pending");
  await data.editor.start(); await data.editor.chooseWarehouse(location);
  const lookup = data.editor.receipt(entry); data.advance(600000); data.editor.checkSession(); late.resolve(sourceResult);
  await assert.rejects(lookup, /recovery_request_stale/);
  assert.equal(data.editor.snapshot().expired, true); assert.equal(data.editor.snapshot().warehouse, null);
  assert.equal(data.journal.list(actorId).length, 1);
});
test("a late proposal detail cannot replace the later selected proposal", async () => {
  const late = deferred<typeof sourceDetail>(), newer = { ...sourceDetail, id: "999", label: "Later selection" };
  const data = setup({ history: async () => ({ total: "2", items: [sourceDetail, newer] }),
    detail: async (_actor, _place, id) => id === sourceDetail.id ? late.promise : newer }, ["stock.source.read"]);
  await data.editor.start(); await data.editor.chooseWarehouse(location);
  const first = data.editor.chooseProposal(sourceDetail.id); await data.editor.chooseProposal(newer.id);
  late.resolve(sourceDetail); await first;
  assert.equal(data.editor.snapshot().selected?.id, newer.id);
});
test("a denied retry preserves an uncertain source reference and hides the previous view", async () => {
  const keys: string[] = [];
  const data = setup({ submit: async (_command, key) => { keys.push(key); throw keys.length === 1 ? new Error("lost") : new AccessFailure(403, "access_denied"); } });
  await proposal(data.editor); await data.editor.submitReviewed(true); await data.editor.retry();
  assert.deepEqual(keys, ["source-1", "source-1"]); assert.equal(data.editor.snapshot().suspended, true);
  assert.equal(data.editor.snapshot().warehouse, null); assert.equal(data.editor.snapshot().result, null);
  assert.equal(data.journal.list(actorId)[0].key, "source-1");
});
test("a changed recovery location invalidates a late receipt without removing the reference", async () => {
  const late = deferred<typeof sourceResult>(), data = setup({ receipt: async () => late.promise });
  const entry = data.journal.remember(actorId, "stock.source.propose", "pending");
  await data.editor.start(); await data.editor.chooseWarehouse(location);
  const request = data.editor.receipt(entry); await data.editor.chooseWarehouse(second.location);
  late.resolve(sourceResult); await assert.rejects(request, /recovery_request_stale/);
  assert.equal(data.journal.list(actorId).length, 1);
});
