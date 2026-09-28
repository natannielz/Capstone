import assert from "node:assert/strict";
import test from "node:test";
import { createUnitTokoClient, RequestNotSentError } from "../src/api/client";
import { createStockSourceGateway } from "../src/modules/inventory/stock-source-client";
import { prepareSourceProposal, prepareSourceDecision, parseSourceDetail } from "../src/modules/inventory/stock-source-workflow";
import { actorId, location, warehouse } from "./stock-count-fixtures";

const command = () => prepareSourceProposal(actorId, location, { physicalKey: "pool-utama", label: "Alokasi gudang", mode: "webLedger",
  control: "exclusiveWebAllocation", maximumAgeSeconds: "60", safetyBufferBaseUnits: "0", externalNamespace: "", reservationProtocolReference: "",
  discrepancyOwnerReference: "PIC gudang", handoffReference: "Serah terima", reason: "Pengajuan" });
const result = { proposalId: "9007199254740993", proposalVersion: "1", status: "proposed", decisionId: null, physicalPoolId: null };
const detail = () => parseSourceDetail({ ...command().body, id: result.proposalId, proposalVersion: "1",
  status: "proposed", proposedAt: "2026-09-28T00:00:00Z", decision: null, physicalPoolId: null });
function gateway(handler: (request: Request) => Promise<Response>) {
  return createStockSourceGateway(createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async input => {
    assert.ok(input instanceof Request);
    if (input.url.endsWith("/identity/csrf")) return Response.json({ requestToken: "fresh-csrf" });
    assert.equal(input.method, "POST"); assert.equal(input.headers.get("X-Expected-Actor-Id"), actorId);
    assert.equal(input.headers.get("X-CSRF-Token"), "fresh-csrf");
    assert.equal(input.credentials, "same-origin"); assert.equal(input.cache, "no-store"); assert.equal(input.redirect, "error");
    return handler(input);
  } }));
}
test("source proposal sends exact reviewed settings once with one key", async () => {
  let calls = 0;
  const client = gateway(async request => {
    calls++; assert.equal(new URL(request.url).pathname, "/api/v1/inventory/sources");
    assert.equal(request.headers.get("Idempotency-Key"), "one-key"); assert.deepEqual(await request.json(), command().body);
    return Response.json(result);
  });
  assert.deepEqual(await client.submit(command(), "one-key"), result); assert.equal(calls, 1);
});
test("source decisions send the expected proposal version and bound result", async () => {
  for (const action of ["approve", "reject", "withdraw"] as const) {
    const decision = prepareSourceDecision(actorId, detail(), action, "Review reference", "Review reason");
    const status = action === "approve" ? "approved" : action === "reject" ? "rejected" : "withdrawn";
    const client = gateway(async request => {
      assert.equal(new URL(request.url).pathname, "/api/v1/inventory/sources/" + result.proposalId + "/" + action);
      assert.equal(request.headers.get("If-Match"), '"1"'); assert.equal(request.headers.get("Idempotency-Key"), "decision-key");
      assert.deepEqual(await request.json(), decision.body);
      return Response.json({ ...result, status, decisionId: "22", physicalPoolId: action === "approve" ? "23" : null });
    });
    assert.equal((await client.submit(decision, "decision-key")).status, status);
  }
});
test("source choices history and detail keep purpose location and exact identity", async () => {
  const client = gateway(async request => {
    const body = await request.json() as Record<string, unknown>;
    if (request.url.endsWith("/warehouses/query")) {
      assert.deepEqual(body, { purpose: "inventory-source-propose" }); return Response.json({ items: [warehouse] });
    }
    assert.equal(body.purpose, "inventory-source-setup"); assert.deepEqual(body.location, location);
    if (request.url.endsWith("/sources/query")) return Response.json({ total: "1", items: [detail()] });
    return Response.json(request.url.includes("/999/") ? null : detail());
  });
  assert.deepEqual(await client.warehouses(actorId, "inventory-source-propose"), [warehouse]);
  assert.equal((await client.history(actorId, location)).items[0].id, result.proposalId);
  assert.deepEqual(await client.detail(actorId, location, result.proposalId), detail());
  assert.equal(await client.detail(actorId, location, "999"), null);
  await assert.rejects(gateway(async () => Response.json(detail())).detail(actorId, location, "998"), /stock_source_response_invalid/);
});
test("source recovery sends only actor-owned reference and selected location and never closes or resubmits", async () => {
  let calls = 0;
  const client = gateway(async request => {
    calls++; assert.ok(request.url.endsWith("/sources/operations/query"));
    assert.equal(request.headers.has("Idempotency-Key"), false);
    assert.deepEqual(await request.json(), { purpose: "operation-recovery", location, operation: "stock.source.propose", key: "pending" });
    return Response.json({ state: "not_confirmed", result: null });
  });
  const entry = { actorId, operation: "stock.source.propose", key: "pending", createdAt: "2026-09-28T00:00:00Z" };
  assert.equal(await client.receipt(entry, location), null); assert.equal(calls, 1);
  assert.equal("close" in client, false);
  await assert.rejects(client.receipt({ ...entry, operation: "stock.source.activate" }, location), /stock_source_input_invalid/);
  assert.equal(calls, 1);
});
test("source invalid input and altered review stop before any network call", async () => {
  let calls = 0;
  const client = createStockSourceGateway(createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async () => { calls++; throw new Error("unexpected"); } }));
  await assert.rejects(client.submit({ ...command(), body: { ...command().body, safetyBufferBaseUnits: "00" } }, "key"), /stock_source_input_invalid/);
  await assert.rejects(client.submit(command(), "bad\n"), /stock_source_input_invalid/);
  await assert.rejects(client.history(actorId, location, -1), /stock_source_input_invalid/);
  await assert.rejects(client.submit(command(), "key", () => false), error => error instanceof RequestNotSentError);
  assert.equal(calls, 0);
});
test("source late result cannot confirm a replaced account view", async () => {
  let current = true;
  const client = gateway(async () => { current = false; return Response.json(result); });
  await assert.rejects(client.submit(command(), "key", () => current), /commit_outcome_unknown/);
});
