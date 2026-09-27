import { test } from "node:test";
import assert from "node:assert/strict";
import { createUnitTokoClient } from "../src/api/client";

test("generated API client carries the system response without browser token storage", async () => {
  const client = createUnitTokoClient({
    baseUrl: "https://unit-toko.test",
    fetch: async (input) => {
      const request = input as Request;
      assert.equal(request.url, "https://unit-toko.test/api/v1/system/status");
      assert.equal(request.method, "GET");
      assert.equal(request.credentials, "same-origin");
      assert.equal(request.cache, "no-store");
      assert.equal(request.headers.has("Authorization"), false);
      return Response.json({ status: "ready", utcNow: "2026-09-27T05:00:00Z" });
    },
  });
  const { data, error, response } = await client.GET("/api/v1/system/status");
  assert.equal(error, undefined);
  assert.equal(response.status, 200);
  assert.equal(data?.status, "ready");
  assert.equal(data?.utcNow, "2026-09-27T05:00:00Z");
});

test("generated API client preserves typed denial and trace without retrying", async () => {
  let calls = 0;
  const client = createUnitTokoClient({
    baseUrl: "https://unit-toko.test",
    fetch: async () => {
      calls++;
      return Response.json({
        type: "https://unit-toko.example/problems/unauthenticated",
        title: "Authentication required",
        status: 401,
        code: "unauthenticated",
        traceId: "client-contract-test",
      }, { status: 401, headers: { "Content-Type": "application/problem+json" } });
    },
  });
  const { data, error, response } = await client.GET("/api/v1/system/status");
  assert.equal(calls, 1);
  assert.equal(data, undefined);
  assert.equal(response.status, 401);
  assert.equal(error?.code, "unauthenticated");
  assert.equal(error?.traceId, "client-contract-test");
});
