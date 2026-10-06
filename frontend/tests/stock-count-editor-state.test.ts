import { test } from "node:test";
import assert from "node:assert/strict";
import { StockCountEditor } from "../src/modules/inventory/stock-count-editor-state";
import type { StockCountGateway } from "../src/modules/inventory/stock-count-client";
import { OperationJournal } from "../src/modules/administration/operation-journal";
import { AccessFailure } from "../src/modules/administration/workflow";
import type { IdentitySession } from "../src/modules/identity/identity-workflow";
import { actorId, location, warehouse, choice, observation } from "./stock-count-fixtures";

const session: IdentitySession = { authenticated: true, account: { userId: actorId, displayName: "Petugas", verifiedEmail: "synthetic@example.test" },
  serverTime: "2026-09-28T01:00:00Z", factorCompletedAt: "2026-09-28T01:00:00Z", freshFactorUntil: "2026-09-28T01:05:00Z",
  idleExpiresAt: "2026-09-28T01:10:00Z", absoluteExpiresAt: "2026-09-28T02:00:00Z", stepUpOperations: [] };
const second = { ...warehouse, warehouseName: "Gudang cadangan", location: { ...location, warehouseId: "109" } };
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(r => { resolve = r; }); return { promise, resolve }; }
function setup(overrides: Partial<StockCountGateway> = {}, actions = ["stock.count", "stock.read"]) {
  const values = new Map<string, string>();
  const storage = { get length() { return values.size; }, key: (i: number) => [...values.keys()][i] ?? null,
    getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); },
    removeItem: (key: string) => { values.delete(key); } };
  const journal = new OperationJournal(() => storage);
  let now = 0, keys = 0;
  const gateway: StockCountGateway = {
    sources: async () => ({ total: '0', items: [] }),
    warehouses: async () => [warehouse, second], packages: async () => ({ total: "1", items: [choice] }),
    history: async () => ({ total: "1", items: [observation] }), observation: async () => observation,
    receipt: async () => null, submit: async () => observation, ...overrides,
  };
  const editor = new StockCountEditor({ actorId, actions, session, observedAt: 0, now: () => now, gateway, journal, newKey: () => "count-" + ++keys });
  return { editor, journal, values, storage, advance: (time: number) => { now = time; } };
}
async function select(editor: StockCountEditor) {
  await editor.start(); await editor.chooseWarehouse(editor.snapshot().warehouses[0].location);
  editor.chooseSource('unbound');
  editor.choosePackage(choice.skuId); editor.changeDraft("packages", "0"); editor.changeDraft("reason", "Physical count");
}
test("review needs a current selected package and explicit zero; success requires another deliberate review", async () => {
  const sent: unknown[] = [], data = setup({ submit: async (command, key) => { sent.push({ command, key }); return observation; } });
  await data.editor.start(); data.editor.reviewCount();
  assert.equal(data.editor.snapshot().review, null); assert.match(String(data.editor.snapshot().error), /stock_count_selection_required/);
  await data.editor.chooseWarehouse(location); data.editor.chooseSource('unbound'); data.editor.choosePackage(choice.skuId); data.editor.changeDraft("reason", "Physical count");
  data.editor.reviewCount(); assert.equal(data.editor.snapshot().review, null);
  data.editor.changeDraft("packages", "0"); data.editor.reviewCount();
  assert.equal(data.editor.snapshot().review?.body.packages, 0);
  data.editor.changeDraft("packages", "8"); assert.equal(data.editor.snapshot().review?.body.packages, 0);
  await data.editor.submitReviewed(false); assert.equal(sent.length, 0);
  await data.editor.submitReviewed(true);
  assert.equal(sent.length, 1); assert.deepEqual(data.editor.snapshot().result, observation);
  assert.equal(data.editor.snapshot().draft.packages, ""); assert.equal(data.editor.snapshot().selectedPackage, null);
  await data.editor.submitReviewed(true); assert.equal(sent.length, 1);
  data.editor.choosePackage(choice.skuId); data.editor.changeDraft("packages", "0"); data.editor.changeDraft("reason", "Physical count");
  data.editor.reviewCount(); await data.editor.submitReviewed(true);
  assert.equal(sent.length, 2); assert.notDeepEqual(sent[0], sent[1]); assert.equal(data.journal.list(actorId).length, 0);
});
test("warehouse and search replacement abort stale reads and clear old selections", async () => {
  const late = deferred<{ total: string; items: typeof choice[] }>(); const signals: AbortSignal[] = [];
  let requests = 0;
  const data = setup({ packages: async (_actor, _place, _search, _offset, signal) => {
    signals.push(signal!); requests++; return requests === 1 ? late.promise : { total: "1", items: [{ ...choice, skuId: "88" }] };
  } });
  await data.editor.start(); const first = data.editor.chooseWarehouse(location);
  await data.editor.chooseWarehouse(second.location); late.resolve({ total: "1", items: [choice] }); await first;
  assert.equal(signals[0].aborted, true); assert.equal(data.editor.snapshot().packages.items[0].skuId, "88");
  data.editor.choosePackage("88"); data.editor.changeSearch("new query");
  assert.equal(data.editor.snapshot().selectedPackage, null); assert.equal(data.editor.snapshot().packages.items.length, 0);
  data.editor.choosePackage(choice.skuId); assert.equal(data.editor.snapshot().selectedPackage, null);
});
test("history purpose has independent choices and count-only accounts cannot open it", async () => {
  const purposes: string[] = [];
  const data = setup({ warehouses: async (_actor, purpose) => { purposes.push(purpose); return [warehouse]; } }, ["stock.read"]);
  await data.editor.start(); assert.equal(data.editor.snapshot().mode, "history");
  await data.editor.chooseWarehouse(location); assert.deepEqual(data.editor.snapshot().history.items, [observation]);
  await data.editor.changeMode("count"); assert.equal(data.editor.snapshot().mode, "history"); assert.equal(purposes.length, 1);
  const counter = setup({}, ["stock.count"]); await counter.editor.start(); await counter.editor.changeMode("history");
  assert.equal(counter.editor.snapshot().mode, "count");
});
test("unknown writes lock the original request and a denied retry preserves its key", async () => {
  const sent: { packages: number; key: string }[] = [];
  const data = setup({ submit: async (command, key) => {
    sent.push({ packages: command.body.packages, key });
    if (sent.length === 1) throw new Error("lost response");
    throw new AccessFailure(403, "access_denied");
  } });
  await select(data.editor); data.editor.reviewCount(); await data.editor.submitReviewed(true);
  assert.equal(data.editor.snapshot().attempt?.body.packages, 0); assert.equal(data.journal.list(actorId).length, 1);
  data.editor.changeDraft("packages", "5"); await data.editor.chooseWarehouse(second.location);
  assert.equal(data.editor.snapshot().attempt?.body.packages, 0); assert.equal(data.editor.snapshot().warehouse?.location.warehouseId, location.warehouseId);
  await data.editor.retry();
  assert.deepEqual(sent, [{ packages: 0, key: "count-1" }, { packages: 0, key: "count-1" }]);
  assert.equal(data.journal.list(actorId)[0].key, "count-1"); assert.doesNotMatch([...data.values.values()].join(""), /Physical count|packages/);
});
test("double clicks cannot dispatch twice and an expired final write response keeps recovery evidence", async () => {
  const late = deferred<typeof observation>(); let calls = 0;
  const data = setup({ submit: async (_command, _key, current) => {
    calls++; const result = await late.promise;
    if (!current!()) throw new AccessFailure(503, "commit_outcome_unknown");
    return result;
  } });
  await select(data.editor); data.editor.reviewCount();
  const pending = data.editor.submitReviewed(true); await data.editor.submitReviewed(true);
  assert.equal(calls, 1); data.advance(600000); data.editor.checkSession();
  assert.equal(data.editor.snapshot().expired, true); assert.equal(data.editor.snapshot().packages.items.length, 0);
  late.resolve(observation); await pending;
  assert.equal(data.editor.snapshot().result, null); assert.equal(data.journal.list(actorId).length, 1);
});
test("blocked recovery storage stops submission and existing pending references stop new review", async () => {
  let calls = 0; const data = setup({ submit: async () => { calls++; return observation; } });
  await select(data.editor); data.journal.remember(actorId, "inventory.stock-count.record", "previous");
  data.editor.refreshReferences(); data.editor.reviewCount(); assert.equal(data.editor.snapshot().review, null);
  data.journal.confirmed(data.journal.list(actorId)[0]); data.editor.refreshReferences(); data.editor.reviewCount();
  data.storage.setItem = () => { throw new Error("blocked"); }; await data.editor.submitReviewed(true);
  assert.equal(calls, 0); assert.match(String(data.editor.snapshot().error), /recovery_storage_unavailable/);
});
test("receipt results after expiry or disposal cannot clear an actor's pending reference", async () => {
  const receipt = { state: "committed" as const, result: { id: observation.id, version: "1" as const }, recordedAt: observation.recordedAt };
  const late = deferred<typeof receipt>(); const data = setup({ receipt: async () => late.promise });
  await data.editor.start(); const entry = data.journal.remember(actorId, "inventory.stock-count.record", "pending");
  const checking = data.editor.receipt(entry); data.advance(600000); late.resolve(receipt);
  await assert.rejects(checking, /recovery_request_stale/); assert.equal(data.journal.list(actorId).length, 1);
  data.editor.dispose(); await assert.rejects(data.editor.receipt(entry), /recovery_request_stale/);
});
test("disposing a view cancels choices and prevents late private data from returning", async () => {
  const late = deferred<typeof warehouse[]>(); let signal!: AbortSignal;
  const data = setup({ warehouses: async (_actor, _purpose, currentSignal) => { signal = currentSignal!; return late.promise; } });
  const loading = data.editor.start(); data.editor.dispose(); late.resolve([warehouse]); await loading;
  assert.equal(signal.aborted, true); assert.equal(data.editor.snapshot().warehouses.length, 0);
});
test("account/session mismatch never starts a choice request", async () => {
  assert.throws(() => new StockCountEditor({ actorId: "77", actions: ["stock.count"], session, observedAt: 0,
    now: () => 0, gateway: {} as StockCountGateway, journal: {} as OperationJournal }), /actor_changed/);
});

test("the editor itself fences a late successful gateway response after expiry", async () => {
  const late = deferred<typeof observation>();
  const data = setup({ submit: async () => late.promise });
  await select(data.editor); data.editor.reviewCount();
  const pending = data.editor.submitReviewed(true);
  data.advance(600000); late.resolve(observation); await pending;
  assert.equal(data.editor.snapshot().expired, true);
  assert.equal(data.editor.snapshot().result, null);
  assert.equal(data.journal.list(actorId).length, 1);
  assert.equal(data.editor.snapshot().attempt?.body.packages, 0);
});
