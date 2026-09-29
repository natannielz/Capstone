import assert from "node:assert/strict";
import test from "node:test";
import { StockSourceSuspensionEditor } from "../src/modules/inventory/stock-source-suspension-editor-state";
import type { StockSourceSuspensionGateway } from "../src/modules/inventory/stock-source-suspension-client";
import { parseSuspensionPreview, parseSourceSuspension, parseSuspensionResult } from "../src/modules/inventory/stock-source-suspension-workflow";
import { OperationJournal, reconcileOperationRecovery } from "../src/modules/administration/operation-journal";
import { AccessFailure } from "../src/modules/administration/workflow";
import { actorId } from "./stock-count-fixtures";
import { sourceSession, SourceMemoryStorage } from "./stock-source-fixtures";
import { pool, preview, detail, outcome } from "./stock-source-suspension-fixtures";
const actions = ["stock.source.read", "stock.source.suspend"];
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(r => { resolve = r; }); return { promise, resolve }; }
function setup(overrides: Partial<StockSourceSuspensionGateway> = {}, permissions = actions, storage = new SourceMemoryStorage()) {
  const journal = new OperationJournal(() => storage); let now = 0, keys = 0, previews = 0, histories = 0;
  const gateway: StockSourceSuspensionGateway = {
    preview: async () => { previews++; return parseSuspensionPreview(preview); },
    latest: async () => parseSourceSuspension(detail),
    history: async () => { histories++; return { total: "1", items: [parseSourceSuspension(detail)] }; },
    detail: async () => parseSourceSuspension(detail), submit: async () => parseSuspensionResult(outcome), receipt: async () => null, ...overrides,
  };
  const editor = new StockSourceSuspensionEditor({ actorId, actions: permissions,
    session: { ...sourceSession, stepUpOperations: ["stock.source.suspend"] }, observedAt: 0, now: () => now, gateway, journal,
    newKey: () => "stop-" + ++keys });
  return { editor, journal, storage, advance: (ms: number) => { now = ms; }, previews: () => previews, histories: () => histories };
}
async function choose(editor: StockSourceSuspensionEditor) { await editor.start(); editor.changePool(pool); await editor.loadPool(); }
async function review(editor: StockSourceSuspensionEditor) { await choose(editor); editor.changeReason("Synthetic stop"); editor.reviewStop(); }
const legacy = { lookup: async () => { throw new Error("legacy fallback"); }, close: async () => null };

test("suspension command-only operator previews and confirms a frozen stop exactly once", async () => {
  let calls = 0;
  const data = setup({ submit: async command => { calls++; assert.equal(command.poolId, pool);
    assert.equal(command.body.reason, "Synthetic stop"); return parseSuspensionResult(outcome); } }, ["stock.source.suspend"]);
  await review(data.editor); assert.ok(data.editor.snapshot().review); assert.equal(data.histories(), 0);
  data.editor.changePool("999"); data.editor.changeReason("Replacement");
  assert.equal(data.editor.snapshot().review?.poolId, pool); assert.equal(data.editor.snapshot().review?.body.reason, "Synthetic stop");
  await data.editor.submitReviewed(false); assert.equal(calls, 0);
  await data.editor.submitReviewed(true); await data.editor.submitReviewed(true);
  assert.equal(calls, 1); assert.equal(data.editor.snapshot().result?.suspensionId, outcome.suspensionId);
  assert.equal(data.editor.snapshot().preview, null); assert.equal(data.journal.list(actorId).length, 0);
});
test("suspension history permission never grants stop preview or recovery", async () => {
  const data = setup({}, ["stock.source.read"]); await choose(data.editor); await data.editor.chooseHistory(detail.id);
  assert.equal(data.previews(), 0); assert.equal(data.histories(), 1); assert.equal(data.editor.snapshot().selected?.id, detail.id);
  data.editor.setRecoveryPool(pool);
  await assert.rejects(data.editor.receipt({ actorId, operation: "stock.source.suspend", key: "old", createdAt: sourceSession.serverTime }), /access_denied/);
  data.editor.changeReason("Reason"); data.editor.reviewStop(); assert.equal(data.editor.snapshot().review, null);
});
test("fresh MFA is required before preview, confirmation and receipt lookup", async () => {
  let sent = 0, receipts = 0;
  const data = setup({ submit: async () => { sent++; return parseSuspensionResult(outcome); },
    receipt: async () => { receipts++; return null; } }, ["stock.source.suspend"]);
  await review(data.editor); data.advance(300000); await data.editor.submitReviewed(true);
  assert.equal(sent, 0); assert.equal(data.editor.snapshot().fresh, false); assert.equal(data.editor.snapshot().review, null);
  data.editor.setRecoveryPool(pool);
  await assert.rejects(data.editor.receipt({ actorId, operation: "stock.source.suspend", key: "old", createdAt: sourceSession.serverTime }), /step_up_required/);
  await data.editor.loadPool(); assert.equal(data.previews(), 1); assert.equal(receipts, 0);
});
test("unknown stop keeps only its recovery reference and cannot be resent or replaced", async () => {
  const sent: string[] = []; const data = setup({ submit: async (_command, key) => { sent.push(key); throw new TypeError("lost"); } });
  await review(data.editor); await data.editor.submitReviewed(true); await data.editor.submitReviewed(true);
  data.editor.cancelReview(); data.editor.changePool("999"); data.editor.changeReason("Replacement"); data.editor.reviewStop();
  assert.deepEqual(sent, ["stop-1"]); assert.ok(data.editor.snapshot().attempt); assert.equal(data.editor.snapshot().poolId, pool);
  assert.deepEqual(Object.keys(JSON.parse([...data.storage.values.values()][0])).sort(), ["actorId", "createdAt", "key", "operation"]);
  assert.equal("retry" in data.editor, false);
});
test("matching receipt clears uncertainty and requires a new pool review before another command", async () => {
  const data = setup({ submit: async () => { throw new TypeError("lost"); }, receipt: async () => parseSuspensionResult(outcome) });
  await review(data.editor); await data.editor.submitReviewed(true);
  await reconcileOperationRecovery(data.journal.list(actorId)[0], data.journal, legacy, { "stock.source.suspend": { lookup: data.editor.receipt } });
  data.editor.reconciled();
  assert.equal(data.editor.snapshot().attempt, null); assert.equal(data.editor.snapshot().preview, null);
  assert.equal(data.editor.snapshot().poolId, ""); assert.equal(data.editor.snapshot().references.length, 0);
});
test("reloaded command-only recovery requires deliberate original pool selection without history reads", async () => {
  const data = setup({ receipt: async (entry, selected) => { assert.equal(entry.key, "pending"); assert.equal(selected, pool); return parseSuspensionResult(outcome); } }, ["stock.source.suspend"]);
  const entry = data.journal.remember(actorId, "stock.source.suspend", "pending"); await data.editor.start();
  await assert.rejects(data.editor.receipt(entry)); data.editor.setRecoveryPool(pool);
  assert.deepEqual(await data.editor.receipt(entry), { state: "committed", result: parseSuspensionResult(outcome) });
  assert.equal(data.histories(), 0); assert.equal(data.previews(), 0);
  await assert.rejects(data.editor.receipt({ ...entry, actorId: "77" }), /recovery_request_stale/);
});
test("suspension context change suppresses a late preview from the old pool", async () => {
  const pending = deferred<ReturnType<typeof parseSuspensionPreview>>();
  const data = setup({ preview: async (_actor, id) => id === pool ? pending.promise : parseSuspensionPreview({ ...preview, physicalPoolId: id }) }, ["stock.source.suspend"]);
  await data.editor.start(); data.editor.changePool(pool); const old = data.editor.loadPool();
  data.editor.changePool("999"); await data.editor.loadPool(); pending.resolve(parseSuspensionPreview(preview)); await old;
  assert.equal(data.editor.snapshot().poolId, "999"); assert.equal(data.editor.snapshot().preview?.physicalPoolId, "999");
});
test("foreign preview or private malformed data hides the previous source", async () => {
  for (const raw of [{ ...preview, physicalPoolId: "999" }, { ...preview, auditId: "private" }]) {
    const data = setup({ preview: async () => raw as ReturnType<typeof parseSuspensionPreview> });
    await choose(data.editor); assert.equal(data.editor.snapshot().preview, null); assert.equal(data.editor.snapshot().suspended, true);
  }
});
test("session expiry conceals source data and a late submitted result cannot clear recovery", async () => {
  const pending = deferred<ReturnType<typeof parseSuspensionResult>>();
  const data = setup({ submit: async () => pending.promise }); await review(data.editor); const request = data.editor.submitReviewed(true);
  await new Promise(resolve => setImmediate(resolve)); data.advance(600000); data.editor.checkSession();
  assert.equal(data.editor.snapshot().expired, true); assert.equal(data.editor.snapshot().preview, null); assert.equal(data.editor.snapshot().selected, null);
  pending.resolve(parseSuspensionResult(outcome)); await request;
  assert.equal(data.journal.list(actorId).length, 1); assert.equal(data.editor.snapshot().result, null);
});
test("storage failure prevents a stop from reaching the gateway", async () => {
  const storage = new SourceMemoryStorage(); let calls = 0;
  const data = setup({ submit: async () => { calls++; return parseSuspensionResult(outcome); } }, actions, storage);
  await review(data.editor); storage.setItem = () => { throw new Error("storage blocked"); };
  await data.editor.submitReviewed(true); assert.equal(calls, 0); assert.equal(data.editor.snapshot().attempt, null);
});
test("a receipt inconsistent with the in-memory stop never clears its reference", async () => {
  const data = setup({ submit: async () => { throw new TypeError("lost"); },
    receipt: async () => parseSuspensionResult({ ...outcome, suspensionVersion: "2" }) });
  await review(data.editor); await data.editor.submitReviewed(true); const entry = data.journal.list(actorId)[0];
  await assert.rejects(reconcileOperationRecovery(entry, data.journal, legacy, { "stock.source.suspend": { lookup: data.editor.receipt } }), /stock_source_suspension_response_invalid/);
  assert.equal(data.journal.list(actorId).length, 1);
});
test("unknown recovery remains pending and changed recovery context rejects a late receipt", async () => {
  const pending = deferred<ReturnType<typeof parseSuspensionResult>>();
  const data = setup({ receipt: async () => pending.promise }, ["stock.source.suspend"]); await data.editor.start();
  const entry = data.journal.remember(actorId, "stock.source.suspend", "old"); data.editor.setRecoveryPool(pool);
  const read = data.editor.receipt(entry); data.editor.setRecoveryPool("999"); pending.resolve(parseSuspensionResult(outcome));
  await assert.rejects(read, /recovery_request_stale/); assert.equal(data.journal.list(actorId).length, 1);
  const unknown = setup({}, ["stock.source.suspend"]); await unknown.editor.start(); unknown.editor.setRecoveryPool(pool);
  assert.equal(await unknown.editor.receipt(entry), null);
});
test("history detail must match the chosen immutable history row", async () => {
  for (const patch of [{ id: "999" }, { physicalPoolId: "999" }, { physicalKey: "foreign" }, { reason: "Rewritten" }]) {
    const data = setup({ detail: async () => parseSourceSuspension({ ...detail, ...patch }) }, ["stock.source.read"]);
    await choose(data.editor); await data.editor.chooseHistory(detail.id);
    assert.equal(data.editor.snapshot().selected, null); assert.equal(data.editor.snapshot().suspended, true);
  }
});
test("read access revocation hides history while retaining pending operation references", async () => {
  const data = setup({ history: async () => { throw new AccessFailure(403, "scope_denied"); } });
  await choose(data.editor); assert.equal(data.editor.snapshot().preview, null); assert.equal(data.editor.snapshot().history.items.length, 0);
  assert.equal(data.editor.snapshot().suspended, true);
});
test("known stop conflict removes the failed attempt and requires fresh preview", async () => {
  let calls = 0; const data = setup({ submit: async () => { calls++; throw new AccessFailure(409, "stock_source_suspension_changed"); } });
  await review(data.editor); await data.editor.submitReviewed(true); assert.equal(calls, 1); assert.equal(data.editor.snapshot().attempt, null);
  assert.equal(data.editor.snapshot().preview, null); assert.equal(data.journal.list(actorId).length, 0);
});

test("MFA expiry during receipt lookup retains the original reference", async () => {
  const pending = deferred<ReturnType<typeof parseSuspensionResult>>();
  const data = setup({ receipt: async () => pending.promise }, ["stock.source.suspend"]); await data.editor.start();
  const entry = data.journal.remember(actorId, "stock.source.suspend", "old"); data.editor.setRecoveryPool(pool);
  const read = reconcileOperationRecovery(entry, data.journal, legacy, { "stock.source.suspend": { lookup: data.editor.receipt } });
  data.advance(300000); pending.resolve(parseSuspensionResult(outcome));
  await assert.rejects(read, /step_up_required/); assert.equal(data.journal.list(actorId).length, 1);
});
test("MFA expiry during a stop keeps uncertainty and removes the old confirmation", async () => {
  const pending = deferred<ReturnType<typeof parseSuspensionResult>>();
  const data = setup({ submit: async () => pending.promise }); await review(data.editor); const request = data.editor.submitReviewed(true);
  await new Promise(resolve => setImmediate(resolve)); data.advance(300000); data.editor.checkSession();
  pending.resolve(parseSuspensionResult(outcome)); await request;
  assert.equal(data.editor.snapshot().preview, null); assert.equal(data.editor.snapshot().review, null);
  assert.equal(data.editor.snapshot().result, null); assert.equal(data.journal.list(actorId).length, 1);
});
