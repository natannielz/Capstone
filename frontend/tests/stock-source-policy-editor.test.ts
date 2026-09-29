import assert from "node:assert/strict";
import test from "node:test";
import { StockSourcePolicyEditor } from "../src/modules/inventory/stock-source-policy-editor-state";
import type { StockSourcePolicyGateway } from "../src/modules/inventory/stock-source-policy-client";
import { parsePolicyPreview, parsePolicyReview, parseSourcePolicy, parsePolicyResult } from "../src/modules/inventory/stock-source-policy-workflow";
import { OperationJournal, reconcileOperationRecovery } from "../src/modules/administration/operation-journal";
import { AccessFailure } from "../src/modules/administration/workflow";
import { actorId } from "./stock-count-fixtures";
import { sourceSession, SourceMemoryStorage } from "./stock-source-fixtures";
import { pool, preview, input, proposal, review, outcome, approvedOutcome, approved } from "./stock-source-policy-fixtures";
const actions = ["stock.source.read", "stock.source.policy.propose", "stock.source.policy.approve"];
const legacy = { lookup: async () => { throw new Error("legacy fallback"); }, close: async () => null };
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(r => { resolve = r; }); return { promise, resolve }; }
function setup(overrides: Partial<StockSourcePolicyGateway> = {}, permissions = actions, storage = new SourceMemoryStorage()) {
  const journal = new OperationJournal(() => storage); let now = 0, keys = 0, previews = 0, histories = 0, reviews = 0;
  const gateway: StockSourcePolicyGateway = {
    preview: async () => { previews++; return parsePolicyPreview(preview); },
    review: async (_actor, _pool, _proposal, action) => { reviews++; return parsePolicyReview({ ...review, action }); },
    latest: async () => ({ bindingCurrent: true, policy: parseSourcePolicy(approved) }),
    history: async () => { histories++; return { total: "1", items: [parseSourcePolicy(proposal)] }; },
    detail: async () => parseSourcePolicy(proposal), submit: async () => parsePolicyResult(outcome), receipt: async () => null, ...overrides,
  };
  const editor = new StockSourcePolicyEditor({ actorId, actions: permissions,
    session: { ...sourceSession, stepUpOperations: ["stock.source.policy.approve"] }, observedAt: 0, now: () => now, gateway, journal,
    newKey: () => "policy-" + ++keys });
  return { editor, journal, storage, advance: (ms: number) => { now = ms; }, previews: () => previews, histories: () => histories, reviews: () => reviews };
}
async function choose(editor: StockSourcePolicyEditor) { await editor.start(); editor.changePool(pool); await editor.loadPool(); }
async function propose(editor: StockSourcePolicyEditor) {
  await choose(editor); editor.changeDraft({ ...input, reason: "Synthetic policy" }); editor.reviewProposal();
}
async function decide(editor: StockSourcePolicyEditor, action: "approve" | "reject" | "withdraw" = "approve") {
  await choose(editor); editor.changeProposal(proposal.id); editor.changeAction(action); await editor.loadAction();
  editor.changeDecision("Review", "Synthetic independent review"); editor.reviewDecision();
}

test("policy maker can freeze and submit a proposal exactly once without history access", async () => {
  let calls = 0;
  const data = setup({ submit: async command => { calls++; assert.equal(command.poolId, pool); return parsePolicyResult(outcome); } }, ["stock.source.policy.propose"]);
  await propose(data.editor); assert.ok(data.editor.snapshot().review); assert.equal(data.histories(), 0); assert.equal(data.reviews(), 0);
  data.editor.changePool("999"); data.editor.changeDraft({ ...input, reason: "Replacement" });
  assert.equal(data.editor.snapshot().review?.body.reason, "Synthetic policy");
  await data.editor.submitReviewed(false); assert.equal(calls, 0);
  await data.editor.submitReviewed(true); await data.editor.submitReviewed(true);
  assert.equal(calls, 1); assert.equal(data.editor.snapshot().result?.proposalId, proposal.id);
  assert.equal(data.editor.snapshot().preview, null); assert.equal(data.journal.list(actorId).length, 0);
});
test("policy checker reviews its explicit action without proposer or history reads", async () => {
  let sent = 0;
  const data = setup({ submit: async command => { sent++; assert.equal(command.operation, "stock.source.policy.approve"); return parsePolicyResult(approvedOutcome); } }, ["stock.source.policy.approve"]);
  await decide(data.editor); assert.ok(data.editor.snapshot().review); assert.equal(data.previews(), 0); assert.equal(data.histories(), 0);
  assert.equal(data.reviews(), 1); await data.editor.submitReviewed(true); assert.equal(sent, 1);
});
test("policy history reader cannot obtain action preview submit or operation receipt", async () => {
  const data = setup({}, ["stock.source.read"]); await choose(data.editor); await data.editor.chooseHistory(proposal.id);
  assert.equal(data.previews(), 0); assert.equal(data.histories(), 1); assert.equal(data.editor.snapshot().selected?.id, proposal.id);
  data.editor.changeProposal(proposal.id); await data.editor.loadAction(); data.editor.reviewProposal(); data.editor.reviewDecision();
  assert.equal(data.reviews(), 0); assert.equal(data.editor.snapshot().review, null);
  data.editor.setRecoveryPool(pool);
  await assert.rejects(data.editor.receipt({ actorId, operation: "stock.source.policy.propose", key: "old", createdAt: sourceSession.serverTime }), /access_denied/);
});
test("fresh MFA expires reviewer action but permits maker proposal and withdrawal", async () => {
  let sent = 0;
  const checker = setup({ submit: async () => { sent++; return parsePolicyResult(approvedOutcome); } }, ["stock.source.policy.approve"]);
  await decide(checker.editor); checker.advance(300000); await checker.editor.submitReviewed(true);
  assert.equal(sent, 0); assert.equal(checker.editor.snapshot().actionPreview, null); assert.equal(checker.editor.snapshot().review, null);
  const maker = setup({}, ["stock.source.policy.propose"]);
  await propose(maker.editor); maker.advance(300000); maker.editor.checkSession();
  assert.ok(maker.editor.snapshot().review); await maker.editor.submitReviewed(true); assert.equal(maker.editor.snapshot().result?.status, "proposed");
  const withdrawing = setup({ review: async () => parsePolicyReview({ ...review, action: "withdraw", bindingCurrent: false,
    current: { ...review.current, suspensionVersion: "3" } }), submit: async () => parsePolicyResult({ ...outcome, status: "withdrawn", decisionId: "22", basePolicyVersion: null }) }, ["stock.source.policy.propose"]);
  withdrawing.advance(300000); await decide(withdrawing.editor, "withdraw"); assert.ok(withdrawing.editor.snapshot().review);
  await withdrawing.editor.submitReviewed(true); assert.equal(withdrawing.editor.snapshot().result?.status, "withdrawn");
});
test("unknown policy request survives reload using only its reference and original pool", async () => {
  const sent: string[] = [], data = setup({ submit: async (_command, key) => { sent.push(key); throw new TypeError("lost"); } }, ["stock.source.policy.propose"]);
  await propose(data.editor); await data.editor.submitReviewed(true); await data.editor.submitReviewed(true);
  data.editor.cancelReview(); data.editor.changePool("999"); data.editor.reviewProposal();
  assert.deepEqual(sent, ["policy-1"]); assert.ok(data.editor.snapshot().attempt);
  assert.deepEqual(Object.keys(JSON.parse([...data.storage.values.values()][0])).sort(), ["actorId", "createdAt", "key", "operation"]);
  const reloaded = setup({ receipt: async (entry, selected) => { assert.equal(entry.key, "policy-1"); assert.equal(selected, pool); return parsePolicyResult(outcome); } }, ["stock.source.policy.propose"], data.storage);
  await reloaded.editor.start(); const entry = reloaded.journal.list(actorId)[0];
  await assert.rejects(reloaded.editor.receipt(entry)); reloaded.editor.setRecoveryPool(pool);
  await reconcileOperationRecovery(entry, reloaded.journal, legacy, { "stock.source.policy": { lookup: reloaded.editor.receipt } });
  reloaded.editor.reconciled(); assert.equal(reloaded.editor.snapshot().references.length, 0);
  assert.equal(reloaded.editor.snapshot().poolId, ""); assert.equal(reloaded.previews(), 0); assert.equal(reloaded.histories(), 0);
});
test("policy mismatched in-memory receipt cannot clear the original attempt", async () => {
  const data = setup({ submit: async () => { throw new TypeError("lost"); },
    receipt: async () => parsePolicyResult({ ...outcome, basePolicyVersion: "0" }) }, ["stock.source.policy.propose"]);
  await propose(data.editor); await data.editor.submitReviewed(true); const entry = data.journal.list(actorId)[0];
  await assert.rejects(reconcileOperationRecovery(entry, data.journal, legacy, { "stock.source.policy": { lookup: data.editor.receipt } }), /stock_source_policy_response_invalid/);
  assert.equal(data.journal.list(actorId).length, 1);
});
test("policy changed selection suppresses late previews and private or foreign responses conceal prior data", async () => {
  const pending = deferred<ReturnType<typeof parsePolicyPreview>>();
  const data = setup({ preview: async (_actor, id) => id === pool ? pending.promise : parsePolicyPreview({ ...preview, physicalPoolId: id }) }, ["stock.source.policy.propose"]);
  await data.editor.start(); data.editor.changePool(pool); const old = data.editor.loadPool();
  data.editor.changePool("999"); await data.editor.loadPool(); pending.resolve(parsePolicyPreview(preview)); await old;
  assert.equal(data.editor.snapshot().preview?.physicalPoolId, "999");
  for (const wrong of [{ ...review, action: "reject" }, { ...review, proposal: { ...proposal, physicalPoolId: "999" } }, { ...review, auditId: "private" }]) {
    const broken = setup({ review: async () => wrong as ReturnType<typeof parsePolicyReview> }, ["stock.source.policy.approve"]);
    await decide(broken.editor); assert.equal(broken.editor.snapshot().actionPreview, null); assert.equal(broken.editor.snapshot().suspended, true);
  }
});
test("policy session expiry hides private views and retains a late committed outcome for recovery", async () => {
  const pending = deferred<ReturnType<typeof parsePolicyResult>>();
  const data = setup({ submit: async () => pending.promise }); await propose(data.editor);
  const sending = data.editor.submitReviewed(true); await new Promise(resolve => setImmediate(resolve));
  data.advance(600000); data.editor.checkSession(); assert.equal(data.editor.snapshot().expired, true);
  assert.equal(data.editor.snapshot().preview, null); pending.resolve(parsePolicyResult(outcome)); await sending;
  assert.equal(data.journal.list(actorId).length, 1); assert.equal(data.editor.snapshot().result, null);
});
test("policy history permits a newly decided proposal but rejects changed immutable review facts", async () => {
  const legitimate = setup({ detail: async () => parseSourcePolicy(approved) }, ["stock.source.read"]);
  await choose(legitimate.editor); await legitimate.editor.chooseHistory(proposal.id); assert.equal(legitimate.editor.snapshot().selected?.status, "approved");
  for (const changed of [{ reason: "Changed" }, { physicalPoolId: "999" }, { id: "999" }]) {
    const data = setup({ detail: async () => parseSourcePolicy({ ...approved, ...changed }) }, ["stock.source.read"]);
    await choose(data.editor); await data.editor.chooseHistory(proposal.id);
    assert.equal(data.editor.snapshot().selected, null); assert.equal(data.editor.snapshot().suspended, true);
  }
});
test("policy storage failure prevents dispatch and failed current authority clears private state", async () => {
  let calls = 0; const data = setup({ submit: async () => { calls++; return parsePolicyResult(outcome); } });
  await propose(data.editor); data.storage.setItem = () => { throw new Error("blocked storage"); };
  await data.editor.submitReviewed(true); assert.equal(calls, 0);
  const denied = setup({ review: async () => { throw new AccessFailure(403, "scope_denied"); } }, ["stock.source.policy.approve"]);
  await decide(denied.editor); assert.equal(denied.editor.snapshot().suspended, true); assert.equal(denied.editor.snapshot().poolId, "");
});
for (const action of ["propose", "approve", "reject", "withdraw"] as const) {
  test("policy " + action + " lost response reload recovers only the original reference with the current action grant", async () => {
    const operation = "stock.source.policy." + action;
    const permissions = [action === "propose" || action === "withdraw" ? "stock.source.policy.propose" : "stock.source.policy.approve"];
    const result = action === "propose" ? outcome : action === "approve" ? approvedOutcome :
      { ...outcome, status: action === "reject" ? "rejected" : "withdrawn", basePolicyVersion: null, decisionId: "24" };
    let writes = 0;
    const initial = setup({ submit: async () => { writes++; throw new TypeError("lost"); } }, permissions);
    if (action === "propose") await propose(initial.editor); else await decide(initial.editor, action);
    await initial.editor.submitReviewed(true); assert.equal(writes, 1); initial.editor.dispose();
    const saved = initial.journal.list(actorId)[0]; assert.equal(saved.operation, operation);
    const reloaded = setup({ receipt: async (entry, id) => {
      assert.deepEqual(entry, saved); assert.equal(id, pool); return parsePolicyResult(result);
    } }, permissions, initial.storage);
    await reloaded.editor.start(); reloaded.editor.setRecoveryPool(pool);
    await reconcileOperationRecovery(saved, reloaded.journal, legacy, { "stock.source.policy": { lookup: reloaded.editor.receipt } });
    reloaded.editor.reconciled(); assert.equal(reloaded.journal.list(actorId).length, 0); assert.equal(writes, 1);
    assert.equal(reloaded.editor.snapshot().poolId, "");
  });
}
test("policy recovery keeps its reference when the selected pool, session, or reviewer factor changes during lookup", async () => {
  for (const change of ["pool", "session", "factor", "unmount"]) {
    const pending = deferred<ReturnType<typeof parsePolicyResult>>();
    const data = setup({ receipt: async () => pending.promise }, ["stock.source.policy.approve"]);
    await data.editor.start(); const entry = data.journal.remember(actorId, "stock.source.policy.approve", "old");
    data.editor.setRecoveryPool(pool);
    const recovery = reconcileOperationRecovery(entry, data.journal, legacy, { "stock.source.policy": { lookup: data.editor.receipt } });
    await new Promise(resolve => setImmediate(resolve));
    if (change === "pool") data.editor.setRecoveryPool("999");
    else if (change === "unmount") data.editor.dispose();
    else data.advance(change === "session" ? 600000 : 300000);
    pending.resolve(parsePolicyResult(approvedOutcome));
    await assert.rejects(recovery); assert.equal(data.journal.list(actorId).length, 1);
  }
});
test("policy action changes discard a late action preview and MFA expiry cannot restore it", async () => {
  const pending = deferred<ReturnType<typeof parsePolicyReview>>();
  const data = setup({ review: async (_a, _p, _id, action) => action === "approve" ? pending.promise : parsePolicyReview({ ...review, action }) }, ["stock.source.policy.approve"]);
  await choose(data.editor); data.editor.changeProposal(proposal.id); const old = data.editor.loadAction();
  data.editor.changeAction("reject"); await data.editor.loadAction(); pending.resolve(parsePolicyReview(review)); await old;
  assert.equal(data.editor.snapshot().actionPreview?.action, "reject");
  const delayed = deferred<ReturnType<typeof parsePolicyReview>>();
  const expiry = setup({ review: async () => delayed.promise }, ["stock.source.policy.approve"]);
  await choose(expiry.editor); expiry.editor.changeProposal(proposal.id); const request = expiry.editor.loadAction();
  expiry.advance(300000); delayed.resolve(parsePolicyReview(review)); await request;
  assert.equal(expiry.editor.snapshot().actionPreview, null); assert.equal(expiry.editor.snapshot().fresh, false);
});
test("a policy reference written by another tab after review prevents a second mutation", async () => {
  let writes = 0; const data = setup({ submit: async () => { writes++; return parsePolicyResult(outcome); } });
  await propose(data.editor); data.journal.remember(actorId, "stock.source.policy.propose", "other-tab");
  await data.editor.submitReviewed(true); assert.equal(writes, 0); assert.equal(data.editor.snapshot().references.length, 1);
});
test("policy history discards late pool details and rejects changed terminal decisions", async () => {
  const pending = deferred<ReturnType<typeof parseSourcePolicy>>();
  const data = setup({ detail: async () => pending.promise }, ["stock.source.read"]);
  await choose(data.editor); const reading = data.editor.chooseHistory(proposal.id);
  data.editor.changePool("999"); pending.resolve(parseSourcePolicy(approved)); await reading; assert.equal(data.editor.snapshot().selected, null);
  const terminal = setup({ history: async () => ({ total: "1", items: [parseSourcePolicy(approved)] }),
    detail: async () => parseSourcePolicy({ ...approved, decision: { ...approved.decision, reason: "Altered terminal record" } }) }, ["stock.source.read"]);
  await choose(terminal.editor); await terminal.editor.chooseHistory(proposal.id); assert.equal(terminal.editor.snapshot().suspended, true);
});
test("policy unknown and foreign receipt outcomes never remove the original reference", async () => {
  for (const response of [null, { ...outcome, physicalPoolId: "999" }, { ...approvedOutcome }]) {
    const data = setup({ receipt: async () => response as ReturnType<typeof parsePolicyResult> | null }, ["stock.source.policy.propose"]);
    await data.editor.start(); const entry = data.journal.remember(actorId, "stock.source.policy.propose", "pending");
    data.editor.setRecoveryPool(pool);
    const result = reconcileOperationRecovery(entry, data.journal, legacy, { "stock.source.policy": { lookup: data.editor.receipt } });
    if (response === null) assert.equal((await result).state, "unconfirmed"); else await assert.rejects(result);
    assert.equal(data.journal.list(actorId).length, 1);
  }
});