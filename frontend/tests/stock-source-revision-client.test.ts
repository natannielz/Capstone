import assert from "node:assert/strict";
import test from "node:test";
import { createUnitTokoClient, RequestNotSentError } from "../src/api/client";
import { createStockSourceRevisionGateway } from "../src/modules/inventory/stock-source-revision-client";
import { prepareSourceRevision, prepareSourceRevisionDecision } from "../src/modules/inventory/stock-source-revision-workflow";
import { actorId, location } from "./stock-count-fixtures";
import { current, proposal, outcome } from "./stock-source-revision-fixtures";
const command = () => prepareSourceRevision(actorId, current, { ...proposal.proposedSettings, reason: proposal.reason });
function gateway(handler: (request: Request) => Promise<Response>) {
  return createStockSourceRevisionGateway(createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async input => {
    assert.ok(input instanceof Request);
    if (input.url.endsWith("/identity/csrf")) return Response.json({ requestToken: "fresh-csrf" });
    assert.equal(input.method, "POST"); assert.equal(input.headers.get("X-Expected-Actor-Id"), actorId);
    assert.equal(input.headers.get("X-CSRF-Token"), "fresh-csrf");
    assert.equal(input.credentials, "same-origin"); assert.equal(input.cache, "no-store"); assert.equal(input.redirect, "error");
    return handler(input);
  } }));
}
test("revision submit binds exact pool version and reviewed settings to one request key", async () => {
  let calls = 0;
  const client = gateway(async request => {
    calls++; assert.equal(new URL(request.url).pathname, "/api/v1/inventory/sources/pools/" + current.physicalPoolId + "/revisions");
    assert.equal(request.headers.get("If-Match"), '"1"'); assert.equal(request.headers.get("Idempotency-Key"), "revision-key");
    assert.deepEqual(await request.json(), command().body); return Response.json(outcome);
  });
  assert.deepEqual(await client.submit(command(), "revision-key"), outcome); assert.equal(calls, 1);
});

test("revision decisions keep reviewed identity and version through all terminal actions", async () => {
  for (const action of ["approve", "reject", "withdraw"] as const) {
    const reviewed = prepareSourceRevisionDecision(actorId, proposal, action, "Review", "Reason");
    const result = { ...outcome, status: action === "approve" ? "approved" : action === "reject" ? "rejected" : "withdrawn",
      basePoolVersion: null, appliedPoolVersion: action === "approve" ? "2" : null, decisionId: "13" };
    const client = gateway(async request => {
      assert.ok(request.url.endsWith("/revisions/12/" + action)); assert.equal(request.headers.get("If-Match"), '"1"');
      assert.deepEqual(await request.json(), reviewed.body); return Response.json(result);
    });
    assert.deepEqual(await client.submit(reviewed, "decision"), result);
  }
});
test("revision scoped queries preserve purpose pagination exact pool and nullable detail", async () => {
  const client = gateway(async request => {
    const body = await request.json() as Record<string, unknown>; assert.deepEqual(body.location, location);
    if (request.url.includes("/pools/")) {
      assert.equal(body.purpose, "inventory-source-current");
      return Response.json(request.url.endsWith("/pools/query") ? { total: "1", items: [current] } : current);
    }
    assert.equal(body.purpose, "inventory-source-revisions");
    if (request.url.endsWith("/revisions/query")) {
      assert.equal(body.physicalPoolId, current.physicalPoolId); assert.equal(body.offset, 0); assert.equal(body.limit, 25);
      return Response.json({ total: "1", items: [proposal] });
    }
    return Response.json(request.url.endsWith("/999/query") ? null : proposal);
  });
  assert.deepEqual((await client.sources(actorId, location)).items[0], current);
  assert.deepEqual(await client.current(actorId, location, current.physicalPoolId), current);
  assert.deepEqual((await client.history(actorId, location, current.physicalPoolId)).items[0], proposal);
  assert.deepEqual(await client.detail(actorId, location, proposal.id), proposal);
  assert.equal(await client.detail(actorId, location, "999"), null);
  await assert.rejects(gateway(async () => Response.json(current)).current(actorId, location, "999"), /stock_source_revision_response_invalid/);
  await assert.rejects(gateway(async () => Response.json(proposal)).detail(actorId, location, "999"), /stock_source_revision_response_invalid/);
});
test("revision recovery sends references only and never resubmits or closes unknown outcomes", async () => {
  let calls = 0;
  const client = gateway(async request => {
    calls++; assert.ok(request.url.endsWith("/revision-operations/query"));
    assert.equal(request.headers.has("Idempotency-Key"), false);
    assert.deepEqual(await request.json(), { purpose: "operation-recovery", location, operation: "stock.source.revision.propose", key: "uncertain" });
    return Response.json({ state: "not_confirmed", result: null });
  });
  const entry = { actorId, operation: "stock.source.revision.propose", key: "uncertain", createdAt: "2026-09-28T00:00:00Z" };
  assert.equal(await client.receipt(entry, location), null); assert.equal(calls, 1); assert.equal("close" in client, false);
  await assert.rejects(client.receipt({ ...entry, operation: "stock.source.propose" }, location), /stock_source_revision_input_invalid/);
  assert.equal(calls, 1);
});
test("revision altered review stale context and invalid keys fail before network dispatch", async () => {
  let calls = 0;
  const client = createStockSourceRevisionGateway(createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async () => { calls++; throw new Error("unexpected network"); } }));
  await assert.rejects(client.submit({ ...command(), expectedVersion: "2" }, "key"), /stock_source_revision_input_invalid/);
  await assert.rejects(client.submit({ ...command(), poolId: "999" }, "key"), /stock_source_revision_input_invalid/);
  await assert.rejects(client.submit(command(), "key\n"));
  await assert.rejects(client.submit(command(), "key", () => false), error => error instanceof RequestNotSentError);
  await assert.rejects(client.history(actorId, location, null, -1), /stock_source_revision_input_invalid/);
  assert.equal(calls, 0);
});
test("revision late responses and malformed committed results retain an uncertain outcome", async () => {
  let currentView = true, calls = 0;
  const client = gateway(async () => { calls++; currentView = false; return Response.json(outcome); });
  await assert.rejects(client.submit(command(), "key", () => currentView), /commit_outcome_unknown/); assert.equal(calls, 1);
  await assert.rejects(gateway(async () => Response.json({ ...outcome, physicalPoolId: "999" })).submit(command(), "key"), /stock_source_revision_response_invalid/);
});
