import { test } from "node:test";
import assert from "node:assert/strict";
import { isolatedTestEnvironment } from "../../backend/legacy/server/test-environment";
import { assertIsolatedSmokeServer } from "../scripts/smoke-environment.mjs";

const isolated = {
  NODE_ENV: "development",
  TOKO_TEST_MODE: "isolated",
  TOKO_TEST_RUN_ID: "isolated-smoke-run-123",
  TURSO_DATABASE_URL: "file:.test-data/isolated-smoke-run-123.db",
} as const;

test("smoke attestation requires a development server and the exact disposable database", () => {
  assert.deepEqual(isolatedTestEnvironment(isolated), { mode: "isolated", runId: isolated.TOKO_TEST_RUN_ID });
  for (const changes of [
    { NODE_ENV: "production" },
    { NODE_ENV: "test" },
    { TOKO_TEST_MODE: undefined },
    { TOKO_TEST_RUN_ID: undefined },
    { TOKO_TEST_RUN_ID: "../other-database" },
    { TURSO_DATABASE_URL: "libsql://production.example" },
    { TURSO_DATABASE_URL: "file:.data/unit-toko.db" },
    { TURSO_DATABASE_URL: "file:.test-data/another-smoke-run.db" },
    { TURSO_DATABASE_URL: "file:.test-data/../production.db" },
  ]) {
    assert.equal(isolatedTestEnvironment({ ...isolated, ...changes }), null);
  }
});

test("smoke client refuses an unattested server before subsequent mutations", async () => {
  let requests = 0;
  const fetcher = async () => { requests++; return Response.json({ mode: "isolated", runId: isolated.TOKO_TEST_RUN_ID }); };
  assert.equal(await assertIsolatedSmokeServer("http://127.0.0.1:3007", isolated, fetcher), "http://127.0.0.1:3007");
  assert.equal(requests, 1);
  for (const base of ["https://production.example", "http://localhost.evil.example", "http://user:secret@localhost", "http://localhost/proxy"]) {
    await assert.rejects(() => assertIsolatedSmokeServer(base, isolated, fetcher));
  }
  assert.equal(requests, 1);
  await assert.rejects(() => assertIsolatedSmokeServer("http://localhost:3000", isolated, async () => new Response(null, { status: 404 })));
  await assert.rejects(() => assertIsolatedSmokeServer("http://localhost:3000", isolated, async () => Response.json({ mode: "isolated", runId: "different-run-id-123" })));
  await assert.rejects(() => assertIsolatedSmokeServer("http://localhost:3000", { ...isolated, TOKO_TEST_RUN_ID: undefined }, fetcher));
});
