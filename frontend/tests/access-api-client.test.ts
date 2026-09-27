import { test } from "node:test";
import assert from "node:assert/strict";
import { createUnitTokoClient } from "../src/api/client";
import { createAccessGateway } from "../src/modules/administration/access-client";
import { AccessFailure } from "../src/modules/administration/workflow";

test("access catalog response retains usable typed role and permission arrays", async () => {
  const gateway = createAccessGateway(createUnitTokoClient({
    baseUrl: "https://unit-toko.test",
    fetch: async () => Response.json({
      roles: [{ id: "finance", label: "Keuangan", version: 1, permissions: [] }],
      financePacks: [{ id: "billing", label: "Penagihan", permissions: ["invoice.issue"] }],
      permissions: [{ id: "invoice.issue", label: "Terbitkan tagihan", allowedScopes: ["fulfillmentShop"], requiresFreshMfa: false }],
      allowedActions: ["access.propose"],
    }),
  }));
  const catalog = await gateway.catalog();
  assert.deepEqual(catalog.roles.map(role => role.id), ["finance"]);
  assert.equal(catalog.financePacks.some(pack => pack.permissions.includes("invoice.issue")), true);
  assert.equal(catalog.permissions[0].allowedScopes[0], "fulfillmentShop");
});

test("approval sends exact Int64 strings and required version and retry headers", async () => {
  const id = "9007199254740993";
  const version = "9007199254740995";
  let calls = 0;
  const gateway = createAccessGateway(createUnitTokoClient({
    baseUrl: "https://unit-toko.test",
    fetch: async input => {
      calls++;
      assert.ok(input instanceof Request);
      assert.equal(input.url, `https://unit-toko.test/api/v1/access/grants/proposals/${id}/approve`);
      assert.equal(input.method, "POST");
      assert.equal(input.headers.get("If-Match"), `"${version}"`);
      assert.equal(input.headers.get("Idempotency-Key"), "review-once");
      assert.equal(input.headers.get("X-Expected-Actor-Id"), "1002");
      assert.equal(input.credentials, "same-origin");
      assert.equal(input.cache, "no-store");
      assert.equal(input.headers.has("Authorization"), false);
      assert.deepEqual(await input.json(), { reason: "Lingkup dan tugas telah diperiksa" });
      return Response.json({ id: "9007199254740997", subjectId: "9007199254740999", version: "1", grantVersion: "2", status: "active" });
    },
  }));
  const result = await gateway.change("approveGrant", id, version, "Lingkup dan tugas telah diperiksa", "review-once", "1002");
  assert.equal(result.id, "9007199254740997");
  assert.equal(result.subjectId, "9007199254740999");
  assert.equal(calls, 1);
});

test("receipt recovery keeps the original actor and retry key when a session has changed", async () => {
  const requests: Request[] = [];
  const gateway = createAccessGateway(createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async input => {
    assert.ok(input instanceof Request); requests.push(input);
    assert.equal(input.headers.get("X-Expected-Actor-Id"), "9007199254740993");
    return Response.json({ code: "actor_changed", status: 409, traceId: "actor-switch" }, { status: 409 });
  } }));
  await assert.rejects(gateway.receipt("access.grant.propose", "original-key", "9007199254740993"), error => error instanceof AccessFailure && error.code === "actor_changed");
  await assert.rejects(gateway.resolve("access.grant.propose", "original-key", "Interrupted request", "9007199254740993"), error => error instanceof AccessFailure && error.code === "actor_changed");
  assert.equal(requests.length, 2);
  assert.equal(requests[1].headers.get("Idempotency-Key"), "original-key");
});

test("access gateway preserves uncertain response and trace without automatic mutation retry", async () => {
  let calls = 0;
  const gateway = createAccessGateway(createUnitTokoClient({
    baseUrl: "https://unit-toko.test",
    fetch: async () => {
      calls++;
      return Response.json({ type: "about:blank", title: "Commit outcome unknown", status: 503,
        code: "commit_outcome_unknown", traceId: "review-trace" }, { status: 503 });
    },
  }));
  await assert.rejects(gateway.change("approveMembership", "12", "1", "Sudah diperiksa", "same-key", "1002"),
    error => error instanceof AccessFailure && error.status === 503 && error.code === "commit_outcome_unknown" && error.traceId === "review-trace");
  assert.equal(calls, 1);
});
