import assert from "node:assert/strict";
import test from "node:test";
import { StockSourceAliasEditor } from "../src/modules/inventory/stock-source-alias-editor-state";
import type { StockSourceAliasGateway } from "../src/modules/inventory/stock-source-alias-client";
import { OperationJournal, reconcileOperationRecovery } from "../src/modules/administration/operation-journal";
import { AccessFailure } from "../src/modules/administration/workflow";
import { actorId, location, warehouse } from "./stock-count-fixtures";
import { sourceSession, SourceMemoryStorage } from "./stock-source-fixtures";
import { current, mapped, proposal, outcome, requestedLocation } from "./stock-source-alias-fixtures";
import { parseCurrentAlias, parseSourceAlias, parseSourceAliasResult } from "../src/modules/inventory/stock-source-alias-workflow";
import { parseCurrentSource } from "../src/modules/inventory/stock-source-revision-workflow";
const requested = { ...warehouse, location: requestedLocation, channelName: "Kanal lain" };
const third = { ...warehouse, location: { ...location, channelId: "105" }, channelName: "Ketiga" };
const elsewhere = { ...warehouse, location: { ...location, warehouseId: "109" } };
const actions = ["stock.source.read", "stock.source.propose", "stock.source.approve"];
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(r => { resolve = r; }); return { promise, resolve }; }
function setup(overrides: Partial<StockSourceAliasGateway> = {}, permissions = actions) {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage); let now = 0, calls = 0, reads = 0;
  const gateway: StockSourceAliasGateway = {
    current: async () => parseCurrentAlias(current), history: async () => ({ total: "1", items: [parseSourceAlias(proposal)] }),
    detail: async () => parseSourceAlias(proposal), submit: async () => parseSourceAliasResult(outcome), receipt: async () => null, ...overrides
  };
  const editor = new StockSourceAliasEditor({ actorId, actions: permissions, session: sourceSession, observedAt: 0, now: () => now, gateway, journal,
    warehouses: async () => [warehouse, requested, third, elsewhere], sources: async () => { reads++; return { total: "1", items: [parseCurrentSource(current.source)] }; },
    newKey: () => "alias-" + ++calls });
  return { editor, journal, storage, advance: (ms: number) => { now = ms; }, reads: () => reads };
}
async function choose(editor: StockSourceAliasEditor) {
  await editor.start(); await editor.chooseWarehouse(location); editor.chooseSource(current.source.physicalPoolId); await editor.chooseRequested(requestedLocation);
}
async function review(editor: StockSourceAliasEditor) {
  await choose(editor); editor.changeDraft(true, "Akses kanal"); editor.reviewProposal();
}
test("alias editor freezes exact mapping review, requires confirmation and sends once", async () => {
  let calls = 0; const data = setup({ submit: async command => { calls++; assert.equal(command.poolId, current.source.physicalPoolId);
    assert.equal(command.body.reason, "Akses kanal"); return parseSourceAliasResult(outcome); } });
  await review(data.editor); assert.ok(data.editor.snapshot().review);
  data.editor.changeDraft(false, "Changed"); await data.editor.chooseRequested(third.location);
  assert.equal(data.editor.snapshot().review?.body.reason, "Akses kanal");
  await data.editor.submitReviewed(false); assert.equal(calls, 0);
  await data.editor.submitReviewed(true); await data.editor.submitReviewed(true);
  assert.equal(calls, 1); assert.equal(data.editor.snapshot().result?.proposalId, proposal.id); assert.equal(data.editor.snapshot().current, null);
});
test("alias selection permits only different channels in the same physical location", async () => {
  const { editor } = setup(); await editor.start(); await editor.chooseWarehouse(location); editor.chooseSource(current.source.physicalPoolId);
  assert.deepEqual(editor.requestedChoices.map(x => x.location.channelId), ["104", "105"]);
  for (const invalid of [location, elsewhere.location, { ...location, channelId: "999" }]) {
    await editor.chooseRequested(invalid); assert.equal(editor.snapshot().current, null);
  }
  await editor.chooseRequested(requestedLocation); assert.equal(editor.snapshot().current?.bindingState, "absent");
});
test("unchanged mapping cannot be reviewed, but stale bindings can be reapproved", async () => {
  const data = setup({ current: async () => parseCurrentAlias(mapped) }); await choose(data.editor);
  data.editor.changeDraft(true, "Unchanged"); data.editor.reviewProposal(); assert.equal(data.editor.snapshot().review, null);
  data.editor.changeDraft(false, "Disable"); data.editor.reviewProposal(); assert.ok(data.editor.snapshot().review);
  const stale = { ...mapped, bindingState: "stale", source: { ...current.source, poolVersion: "2", revisionProposalId: "19", approvedAt: "2026-09-28T00:03:00Z" } };
  const other = setup({ current: async () => parseCurrentAlias(stale) }); await choose(other.editor);
  other.editor.changeDraft(true, "Reapprove"); other.editor.reviewProposal(); assert.equal(other.editor.snapshot().review?.expectedVersion, "2");
});
test("stale settings, stale mapping or active pool prevent approval but allow reject/withdraw review", async () => {
  for (const value of [{ ...current, source: { ...current.source, poolState: "active" } }, mapped,
    { ...current, source: { ...current.source, poolVersion: "2", revisionProposalId: "19" } }]) {
    const data = setup({ current: async () => parseCurrentAlias(value) }); await choose(data.editor); await data.editor.chooseProposal(proposal.id);
    data.editor.reviewDecision("approve", "Review", "Reason"); assert.equal(data.editor.snapshot().review, null);
    data.editor.reviewDecision("reject", "Review", "Reason"); assert.equal(data.editor.snapshot().review?.operation, "stock.source.alias.reject");
    data.editor.cancelReview(); data.editor.reviewDecision("withdraw", "Review", "Reason");
    assert.equal(data.editor.snapshot().review?.operation, "stock.source.alias.withdraw");
  }
});
test("MFA expiring after frozen approval blocks dispatch", async () => {
  let sent = 0; const data = setup({ submit: async () => { sent++; return parseSourceAliasResult(outcome); } });
  await choose(data.editor); await data.editor.chooseProposal(proposal.id); data.editor.reviewDecision("approve", "Review", "Reason");
  assert.ok(data.editor.snapshot().review); data.advance(300000); await data.editor.submitReviewed(true);
  assert.equal(sent, 0); assert.match(String(data.editor.snapshot().error), /step_up_required/);
});
test("command-only account recovers its own reference without reading source/history", async () => {
  const data = setup({ receipt: async (entry, pool, place) => { assert.equal(entry.key, "pending");
    assert.equal(pool, current.source.physicalPoolId); assert.equal(place.channelId, "104"); return parseSourceAliasResult(outcome); } }, ["stock.source.propose"]);
  const entry = data.journal.remember(actorId, "stock.source.alias.propose", "pending");
  await data.editor.start(); data.editor.setRecoveryContext(current.source.physicalPoolId, requestedLocation);
  assert.equal(data.reads(), 0); assert.deepEqual(await data.editor.receipt(entry), { state: "committed", result: parseSourceAliasResult(outcome) });
  data.editor.reviewProposal(); assert.equal(data.editor.snapshot().review, null);
  const reader = setup({}, ["stock.source.read"]); await choose(reader.editor); await reader.editor.chooseProposal(proposal.id);
  reader.editor.reviewDecision("approve", "Review", "Reason"); assert.equal(reader.editor.snapshot().review, null);
});
test("uncertain command retains only its reference and is never resent or replaced", async () => {
  const sent: string[] = []; const data = setup({ submit: async (_command, key) => { sent.push(key); throw new Error("lost"); } });
  await review(data.editor); await data.editor.submitReviewed(true); await data.editor.submitReviewed(true);
  data.editor.cancelReview(); data.editor.changeDraft(false, "Other"); data.editor.reviewProposal(); await data.editor.submitReviewed(true);
  assert.deepEqual(sent, ["alias-1"]); assert.ok(data.editor.snapshot().attempt);
  assert.deepEqual(Object.keys(JSON.parse([...data.storage.values.values()][0])).sort(), ["actorId", "createdAt", "key", "operation"]);
  assert.equal("retry" in data.editor, false);
});
test("matching receipt clears uncertainty and forces fresh data before another write", async () => {
  const data = setup({ submit: async () => { throw new Error("lost"); }, receipt: async () => parseSourceAliasResult(outcome) });
  await review(data.editor); await data.editor.submitReviewed(true); const entry = data.journal.list(actorId)[0];
  await reconcileOperationRecovery(entry, data.journal, { lookup: async () => { throw new Error("legacy"); }, close: async () => null },
    { "stock.source.alias": { lookup: data.editor.receipt } });
  data.editor.reconciled(); assert.equal(data.editor.snapshot().attempt, null); assert.equal(data.editor.snapshot().current, null);
  assert.equal(data.editor.snapshot().references.length, 0);
});
test("double click and disposal before a successful response retain the original uncertain reference", async () => {
  const late = deferred<ReturnType<typeof parseSourceAliasResult>>(); let sent = 0;
  const data = setup({ submit: async () => { sent++; return late.promise; } }); await review(data.editor);
  const pending = data.editor.submitReviewed(true); await data.editor.submitReviewed(true); data.editor.dispose();
  late.resolve(parseSourceAliasResult(outcome)); await pending;
  assert.equal(sent, 1); assert.equal(data.editor.snapshot().result, null); assert.equal(data.editor.snapshot().current, null);
  assert.equal(data.editor.snapshot().attempt, null); assert.equal(data.journal.list(actorId).length, 1);
});
test("blocked or newly corrupt storage prevents dispatch", async () => {
  for (const corrupt of [false, true]) { let sent = 0; const data = setup({ submit: async () => { sent++; return parseSourceAliasResult(outcome); } });
    await review(data.editor); if (corrupt) data.storage.setItem("unit-toko:pending:v1:" + actorId + ":broken", "{}"); else data.storage.setItem = () => { throw new Error("blocked"); };
    await data.editor.submitReviewed(true); assert.equal(sent, 0);
  }
});
test("recovery after reload requires deliberate context and refuses foreign actor or result", async () => {
  let lookups = 0; const data = setup({ receipt: async () => { lookups++; return parseSourceAliasResult({ ...outcome, physicalPoolId: "88" }); } });
  const entry = data.journal.remember(actorId, "stock.source.alias.propose", "pending"); await data.editor.start();
  await assert.rejects(data.editor.receipt(entry), /recovery_request_stale/); assert.equal(lookups, 0);
  data.editor.setRecoveryContext(current.source.physicalPoolId, requestedLocation);
  await assert.rejects(data.editor.receipt({ ...entry, actorId: "77" }), /recovery_request_stale/);
  await assert.rejects(data.editor.receipt(entry), /stock_source_alias_response_invalid/); assert.equal(data.journal.list(actorId).length, 1);
});
test("expiry or selected recovery context change discards a late receipt", async () => {
  for (const expire of [false, true]) {
    const late = deferred<ReturnType<typeof parseSourceAliasResult>>(), data = setup({ receipt: async () => late.promise });
    const entry = data.journal.remember(actorId, "stock.source.alias.propose", "pending"); await data.editor.start();
    data.editor.setRecoveryContext(current.source.physicalPoolId, requestedLocation); const pending = data.editor.receipt(entry);
    if (expire) { data.advance(600000); data.editor.checkSession(); } else data.editor.setRecoveryContext("88", third.location);
    late.resolve(parseSourceAliasResult(outcome)); await assert.rejects(pending, /recovery_request_stale/);
    assert.equal(data.journal.list(actorId).length, 1); if (expire) assert.equal(data.editor.snapshot().warehouse, null);
  }
});
test("scope revocation conceals private data without losing pending work", async () => {
  const data = setup({ receipt: async () => { throw new AccessFailure(403, "access_denied"); } });
  const entry = data.journal.remember(actorId, "stock.source.alias.propose", "pending"); await data.editor.start();
  data.editor.setRecoveryContext(current.source.physicalPoolId, requestedLocation);
  try { await data.editor.receipt(entry); } catch (error) { data.editor.recoveryFailed(error); }
  assert.equal(data.editor.snapshot().suspended, true); assert.equal(data.editor.snapshot().warehouses.length, 0);
  assert.equal(data.journal.list(actorId).length, 1);
});
test("changing requested channel discards an old in-flight mapping", async () => {
  const late = deferred<ReturnType<typeof parseCurrentAlias>>();
  const data = setup({ current: async (_actor, _pool, place) => place.channelId === "104" ? late.promise : parseCurrentAlias({ ...current, requestedLocation: third.location }),
    history: async () => ({ total: "0", items: [] }) });
  await data.editor.start(); await data.editor.chooseWarehouse(location); data.editor.chooseSource(current.source.physicalPoolId);
  const old = data.editor.chooseRequested(requestedLocation); await data.editor.chooseRequested(third.location);
  late.resolve(parseCurrentAlias(current)); await old; assert.equal(data.editor.snapshot().current?.requestedLocation.channelId, "105");
});
test("foreign current or detail conceals data before review", async () => {
  for (const gateway of [{ current: async () => parseCurrentAlias({ ...current, source: { ...current.source, physicalPoolId: "88" } }) },
    { detail: async () => parseSourceAlias({ ...proposal, physicalPoolId: "88" }) }]) {
    const data = setup(gateway); await choose(data.editor); await data.editor.chooseProposal(proposal.id);
    assert.equal(data.editor.snapshot().suspended, true); assert.equal(data.editor.snapshot().current, null);
  }
});

test("mapping detail cannot substitute another physical key for the selected pool", async () => {
  const data = setup({ detail: async () => parseSourceAlias({ ...proposal, physicalKey: "foreign-physical-key" }) });
  await choose(data.editor); await data.editor.chooseProposal(proposal.id);
  assert.equal(data.editor.snapshot().suspended, true); assert.equal(data.editor.snapshot().selected, null);
});
test("late selected history detail cannot replace a newer proposal", async () => {
  const late = deferred<ReturnType<typeof parseSourceAlias>>(), newer = parseSourceAlias({ ...proposal, id: "9007199254741002", reason: "Newer" });
  const data = setup({ history: async () => ({ total: "2", items: [parseSourceAlias(proposal), newer] }),
    detail: async (_actor, _pool, _place, id) => id === proposal.id ? late.promise : newer });
  await choose(data.editor); const old = data.editor.chooseProposal(proposal.id); await data.editor.chooseProposal(newer.id);
  late.resolve(parseSourceAlias(proposal)); await old; assert.equal(data.editor.snapshot().selected?.id, newer.id);
});
test("busy recovery locks its selected context and keeps an unconfirmed receipt", async () => {
  const data = setup(); const entry = data.journal.remember(actorId, "stock.source.alias.propose", "pending"); await data.editor.start();
  data.editor.setRecoveryContext(current.source.physicalPoolId, requestedLocation); data.editor.setRecovering(true);
  data.editor.setRecoveryContext("88", third.location);
  assert.equal(data.editor.snapshot().recoveryPoolId, current.source.physicalPoolId);
  assert.equal(await data.editor.receipt(entry), null); assert.equal(data.journal.list(actorId).length, 1);
  data.editor.setRecovering(false);
});

test("a business refusal (self-approval) stays inline with its own reason and never hides the page as lost access", async () => {
  const data = setup({ submit: async () => { throw new AccessFailure(403, "independent_person_required"); } });
  await review(data.editor); await data.editor.submitReviewed(true);
  assert.equal(data.editor.snapshot().suspended, false);
  assert.equal((data.editor.snapshot().error as AccessFailure).code, "independent_person_required");
});
