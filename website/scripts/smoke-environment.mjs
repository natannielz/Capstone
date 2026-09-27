import assert from "node:assert/strict";

/** Authenticate the test target before loading credentials or making mutations. */
export async function assertIsolatedSmokeServer(base, environment = process.env, fetcher = fetch) {
  const url = new URL(base);
  if (!["http:", "https:"].includes(url.protocol) || !["localhost", "127.0.0.1"].includes(url.hostname) || url.username || url.password || url.pathname !== "/" || url.search || url.hash) {
    throw new Error("Smoke mutasi memerlukan origin server lokal terisolasi.");
  }
  const runId = environment.TOKO_TEST_RUN_ID;
  if (!runId || !/^[a-zA-Z0-9_-]{16,80}$/.test(runId)) throw new Error("Set TOKO_TEST_RUN_ID untuk server dan database uji terisolasi.");
  const identity = await fetcher(url.origin + "/api/test-environment", { redirect: "error", signal: AbortSignal.timeout(5000) });
  if (!identity.ok) throw new Error("Server tidak membuktikan database uji terisolasi; tidak ada mutasi dijalankan.");
  assert.deepEqual(await identity.json(), { mode: "isolated", runId }, "Server uji atau database tidak cocok dengan run ini.");
  return url.origin;
}
