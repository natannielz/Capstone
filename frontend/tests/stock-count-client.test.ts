import { test } from "node:test";
import assert from "node:assert/strict";
import { createUnitTokoClient, RequestNotSentError } from "../src/api/client";
import { createStockCountGateway } from "../src/modules/inventory/stock-count-client";
import { prepareStockCount } from "../src/modules/inventory/stock-count-workflow";
import { MutationLedger, AccessFailure } from "../src/modules/administration/workflow";
import { OperationJournal, submitTrackedRequest } from "../src/modules/administration/operation-journal";
import { actorId, location, warehouse, choice, observation, reference } from "./stock-count-fixtures";
const command = () => prepareStockCount(actorId, location, choice, { packages: "0", reason: "Physical count", observedLotLabel: "" });
function gateway(handler: (request: Request) => Promise<Response>) {
  return createStockCountGateway(createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async input => {
    assert.ok(input instanceof Request);
    if (input.url.endsWith("/identity/csrf")) return Response.json({ requestToken: "fresh-csrf" });
    assert.equal(input.method, "POST"); assert.equal(input.headers.get("X-Expected-Actor-Id"), actorId);
    assert.equal(input.headers.get("X-CSRF-Token"), "fresh-csrf"); assert.equal(input.credentials, "same-origin");
    assert.equal(input.cache, "no-store"); assert.equal(input.redirect, "error");
    return handler(input);
  } }));
}
test("count command sends explicit zero, exact revisions and one key once", async () => {
  let calls = 0;
  const client = gateway(async request => {
    calls++; assert.equal(new URL(request.url).pathname, "/api/v1/inventory/stock-counts");
    assert.equal(request.headers.get("Idempotency-Key"), reference.key); assert.deepEqual(await request.json(), command().body);
    return Response.json(observation);
  });
  assert.deepEqual(await client.submit(command(), reference.key), observation); assert.equal(calls, 1);
});
test("warehouse purposes and package query stay bounded and scoped", async () => {
  const bodies: unknown[] = [];
  const client = gateway(async request => {
    const body = await request.json() as Record<string, unknown>; bodies.push(body);
    assert.equal(request.headers.has("Idempotency-Key"), false);
    return Response.json(request.url.endsWith("/warehouses/query") ? { items: [warehouse] } : { total: "26", items: [choice] });
  });
  assert.deepEqual(await client.warehouses(actorId, "inventory-stock-count"), [warehouse]);
  assert.deepEqual(await client.warehouses(actorId, "inventory-stock-history"), [warehouse]);
  assert.equal((await client.packages(actorId, location, " Pena ", 25)).items[0].definitionId, choice.definitionId);
  assert.deepEqual(bodies, [{ purpose: "inventory-stock-count" }, { purpose: "inventory-stock-history" },
    { purpose: "inventory-stock-count", location, search: "Pena", offset: 25, limit: 25 }]);
});
test("history and nullable details use stock history purpose without requiring count", async () => {
  const client = gateway(async request => {
    const body = await request.json() as Record<string, unknown>;
    assert.equal(body.purpose, "inventory-stock-history");
    if (request.url.endsWith("/stock-counts/query")) {
      assert.deepEqual(body, { purpose: "inventory-stock-history", location, offset: 0, limit: 25 });
      return Response.json({ total: "1", items: [observation] });
    }
    assert.deepEqual(body, { purpose: "inventory-stock-history" });
    return Response.json(request.url.includes("/999/") ? null : observation);
  });
  assert.equal((await client.history(actorId, location)).items[0].id, observation.id);
  assert.deepEqual(await client.observation(actorId, observation.id), observation);
  assert.equal(await client.observation(actorId, "999"), null);
  await assert.rejects(gateway(async () => Response.json(observation)).observation(actorId, "998"), /stock_count_response_invalid/);
});
test("receipt lookup sends only its reference and never resubmits or closes", async () => {
  let calls = 0;
  const client = gateway(async request => {
    calls++; assert.equal(new URL(request.url).pathname, "/api/v1/inventory/stock-counts/operations/query");
    assert.equal(request.headers.has("Idempotency-Key"), false);
    assert.deepEqual(await request.json(), { purpose: "operation-recovery", operation: reference.operation, key: reference.key });
    return Response.json({ state: "not_confirmed", result: null, recordedAt: null });
  });
  assert.equal(await client.receipt(reference), null); assert.equal(calls, 1);
  await assert.rejects(client.receipt({ ...reference, operation: "inventory.stock-count.close" }), /stock_count_input_invalid/);
  assert.equal(calls, 1); assert.equal("close" in client, false);
});
test("invalid local inputs are rejected before network dispatch", async () => {
  let calls = 0;
  const client = createStockCountGateway(createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async () => {
    calls++; throw new Error("should never dispatch");
  } }));
  await assert.rejects(client.packages(actorId, location, "", -1), /stock_count_input_invalid/);
  await assert.rejects(client.packages(actorId, location, "x".repeat(129)), /stock_count_input_invalid/);
  await assert.rejects(client.history("01", location), /stock_count_input_invalid/);
  await assert.rejects(client.submit(command(), "key\n"), /stock_count_input_invalid/);
  await assert.rejects(client.submit({ ...command(), body: { ...command().body, expectedSkuRevision: "2" } }, reference.key), /stock_count_input_invalid/);
  await assert.rejects(client.warehouses(actorId, "other" as "inventory-stock-count"), /stock_count_input_invalid/);
  assert.equal(calls, 0);
});
test("an already replaced view cannot dispatch; a late write result remains uncertain", async () => {
  let calls = 0, current = false;
  const client = gateway(async () => { calls++; current = false; return Response.json(observation); });
  await assert.rejects(client.submit(command(), reference.key, () => current), error => error instanceof RequestNotSentError && error.code === "stock_count_view_stale");
  assert.equal(calls, 0);
  current = true;
  await assert.rejects(client.submit(command(), reference.key, () => current), /commit_outcome_unknown/);
  assert.equal(calls, 1);
});
test("mismatched immutable results cannot confirm a reviewed observation", async () => {
  for (const change of [
    { location: { ...location, warehouseId: "999" } }, { skuId: "9" }, { definitionId: "9" }, { definitionVersion: "2" },
    { packages: 1, baseUnits: "12" }, { packageLabel: "Karton" }, { packageKind: "loose" },
    { baseUnitCode: "BOX" }, { baseUnitsPerPackage: 24 }, { reason: "Changed" }, { observedLotLabel: "Other" },
  ]) await assert.rejects(gateway(async () => Response.json({ ...observation, ...change })).submit(command(), reference.key), /stock_count_response_invalid/);
});
test("lost response then denied retry retains the actor-owned key and private draft only in memory", async () => {
  const values = new Map<string, string>();
  const storage = { get length() { return values.size; }, key: (i: number) => [...values.keys()][i] ?? null,
    getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); },
    removeItem: (key: string) => { values.delete(key); } };
  const journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => reference.key);
  let attempts = 0;
  const client = gateway(async request => {
    attempts++; assert.equal(request.headers.get("Idempotency-Key"), reference.key);
    if (attempts === 1) throw new Error("lost response");
    return Response.json({ code: "access_denied", traceId: "help-id" }, { status: 403 });
  });
  const send = () => submitTrackedRequest(ledger, journal, actorId, "count", reference.operation, command(),
    key => client.submit(command(), key));
  await assert.rejects(send(), /lost response/);
  await assert.rejects(send(), error => error instanceof AccessFailure && error.status === 403 && error.traceId === "help-id");
  assert.equal(ledger.isUncertain("count"), true); assert.equal(journal.list(actorId)[0].key, reference.key);
  assert.equal(attempts, 2); assert.doesNotMatch([...values.values()].join(""), /Physical count|packages|warehouseId/);
});
test("response validation failures retain recovery evidence and storage failure prevents a write", async () => {
  let calls = 0;
  const client = gateway(async () => { calls++; return Response.json({ ...observation, baseUnits: "12" }); });
  const values = new Map<string, string>();
  const storage = { get length() { return values.size; }, key: (i: number) => [...values.keys()][i] ?? null,
    getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); },
    removeItem: (key: string) => { values.delete(key); } };
  const ledger = new MutationLedger(() => reference.key), journal = new OperationJournal(() => storage);
  await assert.rejects(submitTrackedRequest(ledger, journal, actorId, "count", reference.operation, command(),
    key => client.submit(command(), key)), /stock_count_response_invalid/);
  assert.equal(ledger.isUncertain("count"), true); assert.equal(journal.list(actorId).length, 1);
  const unavailable = new OperationJournal(() => { throw new Error("storage blocked"); });
  await assert.rejects(submitTrackedRequest(new MutationLedger(), unavailable, actorId, "count", reference.operation, command(),
    key => client.submit(command(), key)), /recovery_storage_unavailable/);
  assert.equal(calls, 1);
});


test("a source command changed during transport cannot change the reviewed response binding", async () => {
  const mutable = structuredClone(command());
  const client = gateway(async request => {
    mutable.body.reason = "Changed"; mutable.choice.baseUnitsPerPackage = 24; mutable.body.location.warehouseId = "999";
    assert.deepEqual(await request.json(), command().body);
    return Response.json(observation);
  });
  assert.deepEqual(await client.submit(mutable, reference.key), observation);
});
