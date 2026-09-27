import { test } from "node:test";
import assert from "node:assert/strict";
import { createUnitTokoClient, RequestNotSentError } from "../src/api/client";
import { createAccessGateway } from "../src/modules/administration/access-client";
import { MutationLedger, accessMessage } from "../src/modules/administration/workflow";
import { OperationJournal, submitTrackedRequest } from "../src/modules/administration/operation-journal";

test("each unsafe command obtains a fresh same-origin token and preserves its original actor and key", async () => {
  const calls: Request[] = [];
  let sequence = 0;
  const gateway = createAccessGateway(createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async input => {
    assert.ok(input instanceof Request); calls.push(input);
    assert.equal(input.credentials, "same-origin"); assert.equal(input.cache, "no-store"); assert.equal(input.redirect, "error");
    assert.equal(input.headers.has("Authorization"), false);
    if (input.method === "GET") {
      assert.equal(input.url, "https://unit-toko.test/api/v1/identity/csrf");
      return Response.json({ requestToken: `request-token-${++sequence}` });
    }
    assert.equal(input.headers.get("X-CSRF-Token"), `request-token-${sequence}`);
    assert.equal(input.headers.get("X-Expected-Actor-Id"), "9007199254740993");
    assert.equal(input.headers.get("Idempotency-Key"), "same-key");
    assert.deepEqual(await input.json(), { reason: "Reviewed" });
    return Response.json({ id: "14", subjectId: "15", version: "2", grantVersion: "3", status: "active" });
  } }));
  await gateway.change("approveGrant", "12", "1", "Reviewed", "same-key", "9007199254740993");
  await gateway.change("approveGrant", "12", "1", "Reviewed", "same-key", "9007199254740993");
  assert.deepEqual(calls.map(call => call.method), ["GET", "POST", "GET", "POST"]);
});

test("failed or malformed anti-forgery preparation never sends the command", async () => {
  for (const response of [Response.json({ code: "identity_unavailable", traceId: "trace" }, { status: 503 }), Response.json({ requestToken: "" }), Response.json({ requestToken: 123 })]) {
    let calls = 0;
    const gateway = createAccessGateway(createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async input => {
      calls++; assert.ok(input instanceof Request); assert.equal(input.method, "GET"); return response;
    } }));
    await assert.rejects(gateway.change("approveGrant", "12", "1", "Reviewed", "same-key", "15"), error => error instanceof RequestNotSentError);
    assert.equal(calls, 1);
  }
});

test("a command rejected after CSRF preparation is never replayed automatically", async () => {
  let writes = 0;
  const client = createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async input => {
    assert.ok(input instanceof Request);
    if (input.method === "GET") return Response.json({ requestToken: "fresh-token" });
    writes++; return Response.json({ code: "csrf_invalid", status: 403 }, { status: 403 });
  } });
  const result = await client.POST("/api/v1/access/grants/preview", { body: { membershipId: "12", templateId: "buyer", financePacks: [], additionalPermissions: [], delegablePermissions: [], effectiveAt: "2026-09-28T00:00:00Z", expiresAt: null, userId: "15", scope: { kind: "buyerOwned", organizationId: "10", branchId: "11", divisionId: null, shopId: null, warehouseId: null, channelId: null } } });
  assert.equal(result.response.status, 403); assert.equal(writes, 1);
});

test("known pre-dispatch failure clears recovery reference without turning an older uncertain result into failure", async () => {
  const storage = new Map<string, string>();
  const journal = new OperationJournal(() => ({ get length() { return storage.size; }, key: index => [...storage.keys()][index] ?? null,
    getItem: key => storage.get(key) ?? null, setItem: (key, value) => { storage.set(key, value); }, removeItem: key => { storage.delete(key); } }));
  const ledger = new MutationLedger(() => "request-key");
  const failure = new RequestNotSentError(503, "identity_unavailable", "trace");
  await assert.rejects(submitTrackedRequest(ledger, journal, "15", "approve", "access.grant.approve", { id: "12" }, async () => { throw failure; }), RequestNotSentError);
  assert.equal(ledger.isUncertain("approve"), false); assert.equal(journal.list("15").length, 0);
  assert.match(accessMessage(failure), /belum dikirim/i);
  await assert.rejects(submitTrackedRequest(ledger, journal, "15", "approve", "access.grant.approve", { id: "12" }, async () => { throw new Error("lost response"); }));
  await assert.rejects(submitTrackedRequest(ledger, journal, "15", "approve", "access.grant.approve", { id: "12" }, async () => { throw failure; }));
  assert.equal(ledger.isUncertain("approve"), true); assert.equal(journal.list("15").length, 1);
});
