import assert from "node:assert/strict";
import test from "node:test";
import { createUnitTokoClient, RequestNotSentError } from "../src/api/client";
import { createStockSourceSuspensionGateway } from "../src/modules/inventory/stock-source-suspension-client";
import { prepareSourceSuspension } from "../src/modules/inventory/stock-source-suspension-workflow";
import { actorId } from "./stock-count-fixtures";
import { pool, preview, outcome, detail } from "./stock-source-suspension-fixtures";
const command = () => prepareSourceSuspension(actorId, preview, "Reason");
const entry = { actorId, operation: "stock.source.suspend", key: "stop", createdAt: "2026-09-29T00:00:00Z" };
function gateway(handler: (request: Request) => Promise<Response>) {
  return createStockSourceSuspensionGateway(createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async input => {
    assert.ok(input instanceof Request);
    if (input.url.endsWith("/identity/csrf")) return Response.json({ requestToken: "fresh-csrf" });
    assert.equal(input.method, "POST"); assert.equal(input.headers.get("X-Expected-Actor-Id"), actorId);
    assert.equal(input.headers.get("X-CSRF-Token"), "fresh-csrf"); assert.equal(input.credentials, "same-origin");
    assert.equal(input.cache, "no-store"); assert.equal(input.redirect, "error"); return handler(input);
  } }));
}
test("suspension submit sends exactly one frozen command with both version preconditions", async () => {
  let calls = 0;
  const client = gateway(async request => {
    calls++; assert.ok(request.url.endsWith("/pools/" + pool + "/suspend")); assert.equal(request.headers.get("If-Match"), '"' + preview.poolVersion + '"');
    assert.equal(request.headers.get("Idempotency-Key"), "stop"); assert.deepEqual(await request.json(), command().body); return Response.json(outcome);
  });
  assert.deepEqual(await client.submit(command(), "stop"), outcome); assert.equal(calls, 1);
});
test("suspension private queries preserve purposes pages nullable detail and selected identities", async () => {
  const client = gateway(async request => {
    const body = await request.json() as Record<string, unknown>;
    if (request.url.endsWith("/suspension-preview/query")) { assert.deepEqual(body, { purpose: "inventory-source-suspension-preview" }); return Response.json(preview); }
    if (request.url.endsWith("/suspensions/query")) {
      assert.deepEqual(body, { purpose: "inventory-source-suspension-history", offset: 0, limit: 25 }); return Response.json({ total: "1", items: [detail] });
    }
    assert.deepEqual(body, { purpose: "inventory-source-suspension-history" });
    return Response.json(request.url.endsWith("/999/query") ? null : detail);
  });
  assert.deepEqual(await client.preview(actorId, pool), preview); assert.deepEqual(await client.latest(actorId, pool), detail);
  assert.deepEqual((await client.history(actorId, pool)).items[0], detail);
  assert.deepEqual(await client.detail(actorId, pool, detail.id), detail); assert.equal(await client.detail(actorId, pool, "999"), null);
  await assert.rejects(gateway(async () => Response.json(preview)).preview(actorId, "999"), /stock_source_suspension_response_invalid/);
  await assert.rejects(gateway(async () => Response.json(detail)).detail(actorId, pool, "999"), /stock_source_suspension_response_invalid/);
  await assert.rejects(gateway(async () => Response.json(detail)).latest(actorId, "999"), /stock_source_suspension_response_invalid/);
});
test("suspension recovery uses the original reference and selected pool without dispatch or closure", async () => {
  let calls = 0;
  const client = gateway(async request => {
    calls++; assert.ok(request.url.endsWith("/suspension-operations/query")); assert.equal(request.headers.has("Idempotency-Key"), false);
    assert.deepEqual(await request.json(), { purpose: "operation-recovery", physicalPoolId: pool, operation: "stock.source.suspend", key: "stop" });
    return Response.json({ state: "not_confirmed", result: null });
  });
  assert.equal(await client.receipt(entry, pool), null); assert.equal(calls, 1);
  assert.equal("retry" in client, false); assert.equal("close" in client, false);
  await assert.rejects(client.receipt({ ...entry, operation: "stock.source.approve" }, pool)); assert.equal(calls, 1);
  await assert.rejects(gateway(async () => Response.json({ state: "committed", result: { ...outcome, physicalPoolId: "999" } })).receipt(entry, pool), /stock_source_suspension_response_invalid/);
});
test("suspension altered review stale context malformed keys and paging stop before network", async () => {
  let calls = 0;
  const client = createStockSourceSuspensionGateway(createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async () => { calls++; throw new Error("unexpected"); } }));
  await assert.rejects(client.submit({ ...command(), expectedVersion: "2" }, "key"));
  await assert.rejects(client.submit({ ...command(), body: { ...command().body, configurationHash: "b".repeat(64) } }, "key"));
  await assert.rejects(client.submit(command(), "key\n"));
  await assert.rejects(client.submit(command(), "key", () => false), error => error instanceof RequestNotSentError);
  await assert.rejects(client.history(actorId, pool, -1)); assert.equal(calls, 0);
});
test("suspension late or foreign write results never confirm the current review", async () => {
  let current = true, calls = 0;
  const client = gateway(async () => { calls++; current = false; return Response.json(outcome); });
  await assert.rejects(client.submit(command(), "key", () => current), /commit_outcome_unknown/); assert.equal(calls, 1);
  await assert.rejects(gateway(async () => Response.json({ ...outcome, suspensionVersion: "2" })).submit(command(), "key"), /stock_source_suspension_response_invalid/);
});
test("suspension lost response never triggers an automatic command retry", async () => {
  let calls = 0; const client = gateway(async () => { calls++; throw new TypeError("lost response"); });
  await assert.rejects(client.submit(command(), "key"), /lost response/); assert.equal(calls, 1);
});
