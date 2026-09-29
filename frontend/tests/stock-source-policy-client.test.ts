import assert from "node:assert/strict";
import test from "node:test";
import { createUnitTokoClient, RequestNotSentError } from "../src/api/client";
import { createStockSourcePolicyGateway } from "../src/modules/inventory/stock-source-policy-client";
import { preparePolicyProposal, preparePolicyDecision } from "../src/modules/inventory/stock-source-policy-workflow";
import { actorId } from "./stock-count-fixtures";
import { pool, preview, input, proposal, review, outcome, approvedOutcome, approved } from "./stock-source-policy-fixtures";
const command = () => preparePolicyProposal(actorId, preview, input, "Reason");
const approval = () => preparePolicyDecision(actorId, review, "Review", "Reason");
const entry = { actorId, operation: "stock.source.policy.propose", key: "policy", createdAt: "2026-09-29T00:00:00Z" };
function gateway(handler: (request: Request) => Promise<Response>) {
  return createStockSourcePolicyGateway(createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async request => {
    assert.ok(request instanceof Request);
    if (request.url.endsWith("/identity/csrf")) return Response.json({ requestToken: "fresh-csrf" });
    assert.equal(request.method, "POST"); assert.equal(request.headers.get("X-Expected-Actor-Id"), actorId);
    assert.equal(request.headers.get("X-CSRF-Token"), "fresh-csrf"); assert.equal(request.credentials, "same-origin");
    assert.equal(request.cache, "no-store"); assert.equal(request.redirect, "error"); return handler(request);
  } }));
}
test("policy submit uses frozen source conditions and decision review exactly once", async () => {
  let calls = 0;
  const client = gateway(async request => {
    calls++; assert.equal(request.headers.get("Idempotency-Key"), "policy");
    const decide = request.url.endsWith("/approve");
    assert.equal(request.headers.get("If-Match"), decide ? '"1"' : '"' + preview.poolVersion + '"');
    assert.deepEqual(await request.json(), decide ? approval().body : command().body);
    assert.ok(request.url.endsWith(decide ? "/policy-proposals/" + proposal.id + "/approve" : "/pools/" + pool + "/policy-proposals"));
    return Response.json(decide ? approvedOutcome : outcome);
  });
  assert.deepEqual(await client.submit(command(), "policy"), outcome);
  assert.deepEqual(await client.submit(approval(), "policy"), approvedOutcome); assert.equal(calls, 2);
});
test("policy private queries enforce selected pool proposal and action with closed purposes", async () => {
  const client = gateway(async request => {
    const body = await request.json() as Record<string, unknown>;
    if (request.url.endsWith("/policy-preview/query")) { assert.deepEqual(body, { purpose: "inventory-source-policy-preview" }); return Response.json(preview); }
    if (request.url.endsWith("/review/query")) { assert.deepEqual(body, { purpose: "inventory-source-policy-review", action: "approve" }); return Response.json(review); }
    if (request.url.endsWith("/policy-proposals/query")) { assert.deepEqual(body, { purpose: "inventory-source-policy-history", offset: 0, limit: 25 }); return Response.json({ total: "1", items: [proposal] }); }
    assert.deepEqual(body, { purpose: "inventory-source-policy-history" });
    return Response.json(request.url.endsWith("/policy/query") ? { bindingCurrent: true, policy: approved }
      : request.url.endsWith("/999/query") ? null : proposal);
  });
  assert.deepEqual(await client.preview(actorId, pool), preview);
  assert.deepEqual(await client.review(actorId, pool, proposal.id, "approve"), review);
  assert.deepEqual((await client.history(actorId, pool)).items, [proposal]);
  assert.deepEqual(await client.detail(actorId, pool, proposal.id), proposal); assert.equal(await client.detail(actorId, pool, "999"), null);
  assert.deepEqual((await client.latest(actorId, pool)).policy, approved);
  await assert.rejects(gateway(async () => Response.json(preview)).preview(actorId, "999"));
  await assert.rejects(gateway(async () => Response.json(review)).review(actorId, pool, "999", "approve"));
  await assert.rejects(gateway(async () => Response.json({ ...review, action: "reject" })).review(actorId, pool, proposal.id, "approve"));
  await assert.rejects(gateway(async () => Response.json(proposal)).detail(actorId, "999", proposal.id));
  await assert.rejects(gateway(async () => Response.json({ bindingCurrent: true, policy: approved })).latest(actorId, "999"));
});
test("all four policy receipts use original references without command retry or closure", async () => {
  let calls = 0;
  const client = gateway(async request => {
    calls++; assert.ok(request.url.endsWith("/policy-operations/query")); assert.equal(request.headers.has("Idempotency-Key"), false);
    const body = await request.json() as Record<string, unknown>;
    assert.deepEqual(body, { purpose: "operation-recovery", physicalPoolId: pool, operation: body.operation, key: entry.key });
    return Response.json({ state: "not_confirmed", result: null });
  });
  for (const action of ["propose", "approve", "reject", "withdraw"])
    assert.equal(await client.receipt({ ...entry, operation: "stock.source.policy." + action }, pool), null);
  assert.equal(calls, 4); assert.equal("retry" in client, false); assert.equal("close" in client, false);
  await assert.rejects(client.receipt({ ...entry, operation: "stock.source.propose" }, pool)); assert.equal(calls, 4);
  await assert.rejects(gateway(async () => Response.json({ state: "committed", result: { ...outcome, physicalPoolId: "999" } })).receipt(entry, pool));
});
test("policy altered snapshots malformed references and stale views fail before network", async () => {
  let calls = 0;
  const client = createStockSourcePolicyGateway(createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async () => { calls++; throw new Error("unexpected"); } }));
  await assert.rejects(client.submit({ ...command(), expectedVersion: "1" }, "policy"));
  await assert.rejects(client.submit({ ...command(), body: { ...command().body, configurationHash: "b".repeat(64) } }, "policy"));
  await assert.rejects(client.submit({ ...approval(), proposalId: "999" }, "policy"));
  await assert.rejects(client.submit(command(), "key\n"));
  await assert.rejects(client.submit(command(), "policy", () => false), error => error instanceof RequestNotSentError);
  await assert.rejects(client.history(actorId, pool, -1)); assert.equal(calls, 0);
});
test("policy late responses foreign results and transport loss never silently resubmit", async () => {
  let current = true, calls = 0;
  await assert.rejects(gateway(async () => { calls++; current = false; return Response.json(outcome); }).submit(command(), "policy", () => current), /commit_outcome_unknown/);
  assert.equal(calls, 1);
  await assert.rejects(gateway(async () => Response.json({ ...approvedOutcome, proposalId: "999" })).submit(approval(), "policy"), /stock_source_policy_response_invalid/);
  calls = 0;
  await assert.rejects(gateway(async () => { calls++; throw new TypeError("lost response"); }).submit(command(), "policy"), /lost response/);
  assert.equal(calls, 1);
});