import assert from "node:assert/strict";
import test from "node:test";
import { createUnitTokoClient, RequestNotSentError } from "../src/api/client";
import { createStockSourceAliasGateway } from "../src/modules/inventory/stock-source-alias-client";
import { prepareSourceAlias, prepareSourceAliasDecision } from "../src/modules/inventory/stock-source-alias-workflow";
import { actorId } from "./stock-count-fixtures";
import { current, requestedLocation, proposal, outcome, approval } from "./stock-source-alias-fixtures";
const command = () => prepareSourceAlias(actorId, current, true, "Reason");
const pool = current.source.physicalPoolId;
function gateway(handler: (request: Request) => Promise<Response>) {
  return createStockSourceAliasGateway(createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async input => {
    assert.ok(input instanceof Request);
    if (input.url.endsWith("/identity/csrf")) return Response.json({ requestToken: "fresh-csrf" });
    assert.equal(input.method, "POST"); assert.equal(input.headers.get("X-Expected-Actor-Id"), actorId);
    assert.equal(input.headers.get("X-CSRF-Token"), "fresh-csrf"); assert.equal(input.credentials, "same-origin");
    assert.equal(input.cache, "no-store"); assert.equal(input.redirect, "error"); return handler(input);
  } }));
}
test("alias submit sends the frozen pool settings and mapping bases under one key", async () => {
  let calls = 0;
  const client = gateway(async request => {
    calls++; assert.ok(request.url.endsWith("/pools/" + pool + "/aliases")); assert.equal(request.headers.get("If-Match"), '"1"');
    assert.equal(request.headers.get("Idempotency-Key"), "mapping"); assert.deepEqual(await request.json(), command().body); return Response.json(outcome);
  });
  assert.deepEqual(await client.submit(command(), "mapping"), outcome); assert.equal(calls, 1);
});
test("alias terminal actions bind the reviewed proposal and version", async () => {
  for (const action of ["approve", "reject", "withdraw"] as const) {
    const reviewed = prepareSourceAliasDecision(actorId, proposal, action, "Review", "Reason");
    const result = { ...approval, status: action === "approve" ? "approved" : action === "reject" ? "rejected" : "withdrawn", appliedAliasVersion: action === "approve" ? "1" : null };
    const client = gateway(async request => {
      assert.ok(request.url.endsWith("/alias-proposals/" + proposal.id + "/" + action)); assert.equal(request.headers.get("If-Match"), '"1"');
      assert.deepEqual(await request.json(), reviewed.body); return Response.json(result);
    });
    assert.deepEqual(await client.submit(reviewed, "decision"), result);
  }
});
test("alias queries validate exact pool channel identity nullable detail and page size", async () => {
  const client = gateway(async request => {
    const body = await request.json() as Record<string, unknown>; assert.deepEqual(body.requestedLocation, requestedLocation);
    if (request.url.endsWith("/aliases/query")) { assert.equal(body.purpose, "inventory-source-alias"); return Response.json(current); }
    assert.equal(body.purpose, "inventory-source-alias-history");
    assert.equal(body.offset, 0); assert.equal(body.limit, 25);
    if (request.url.endsWith("/alias-proposals/query")) return Response.json({ total: "1", items: [proposal] });
    return Response.json(request.url.endsWith("/999/query") ? null : proposal);
  });
  assert.deepEqual(await client.current(actorId, pool, requestedLocation), current);
  assert.deepEqual((await client.history(actorId, pool, requestedLocation)).items[0], proposal);
  assert.deepEqual(await client.detail(actorId, pool, requestedLocation, proposal.id), proposal);
  assert.equal(await client.detail(actorId, pool, requestedLocation, "999"), null);
  await assert.rejects(gateway(async () => Response.json(current)).current(actorId, "999", requestedLocation), /stock_source_alias_response_invalid/);
  await assert.rejects(gateway(async () => Response.json(proposal)).detail(actorId, pool, requestedLocation, "999"), /stock_source_alias_response_invalid/);
});
test("alias recovery uses references and selected scope only and cannot resubmit or close unknown work", async () => {
  let calls = 0;
  const client = gateway(async request => {
    calls++; assert.ok(request.url.endsWith("/alias-operations/query")); assert.equal(request.headers.has("Idempotency-Key"), false);
    assert.deepEqual(await request.json(), { purpose: "operation-recovery", physicalPoolId: pool, requestedLocation, operation: "stock.source.alias.propose", key: "unknown" });
    return Response.json({ state: "not_confirmed", result: null });
  });
  const entry = { actorId, operation: "stock.source.alias.propose", key: "unknown", createdAt: "2026-09-28T00:00:00Z" };
  assert.equal(await client.receipt(entry, pool, requestedLocation), null); assert.equal(calls, 1);
  assert.equal("close" in client, false); assert.equal("retry" in client, false);
  await assert.rejects(client.receipt({ ...entry, operation: "stock.source.propose" }, pool, requestedLocation)); assert.equal(calls, 1);
  await assert.rejects(gateway(async () => Response.json({ state: "committed", result: { ...outcome, physicalPoolId: "999" } })).receipt(entry, pool, requestedLocation), /stock_source_alias_response_invalid/);
});
test("alias altered review stale context and bad keys stop before any network request", async () => {
  let calls = 0;
  const client = createStockSourceAliasGateway(createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async () => { calls++; throw new Error("unexpected"); } }));
  await assert.rejects(client.submit({ ...command(), expectedVersion: "2" }, "key"));
  await assert.rejects(client.submit({ ...command(), body: { ...command().body, enabled: false } }, "key"));
  await assert.rejects(client.submit(command(), "key\n"));
  await assert.rejects(client.submit(command(), "key", () => false), error => error instanceof RequestNotSentError);
  await assert.rejects(client.history(actorId, pool, requestedLocation, -1));
  assert.equal(calls, 0);
});
test("alias late or foreign write results cannot confirm the current review", async () => {
  let live = true, calls = 0;
  const client = gateway(async () => { calls++; live = false; return Response.json(outcome); });
  await assert.rejects(client.submit(command(), "key", () => live), /commit_outcome_unknown/); assert.equal(calls, 1);
  await assert.rejects(gateway(async () => Response.json({ ...outcome, poolVersion: "2" })).submit(command(), "key"), /stock_source_alias_response_invalid/);
});
test("alias transport failure after dispatch never retries the business request", async () => {
  let calls = 0; const client = gateway(async () => { calls++; throw new TypeError("lost response"); });
  await assert.rejects(client.submit(command(), "key"), /lost response/); assert.equal(calls, 1);
});
