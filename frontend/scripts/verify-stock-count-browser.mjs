// Opt-in fixture only. Never imported by the application or used as a deployment proxy.
import assert from "node:assert/strict";
import http from "node:http";
import https from "node:https";
import net from "node:net";
import { createHash } from "node:crypto";
import { mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createRequire } from "node:module";
import next from "next";

const chunks = [];
for await (const chunk of process.stdin) chunks.push(chunk);
const config = JSON.parse(Buffer.concat(chunks).toString("utf8"));
assert.equal(process.versions.node.split(".")[0], "24");
const api = new URL(config.apiAddress), origin = new URL(config.origin), issuer = new URL(config.issuer);
assert.equal(api.protocol, "http:"); assert.equal(api.hostname, "127.0.0.1"); assert.ok(Number(api.port) > 0);
assert.equal(origin.href, "https://portal.unit-toko.co.id/"); assert.equal(issuer.href, "https://login.unit-toko.co.id/");
assert.match(config.poolId, /^[1-9][0-9]{0,18}$/);
assert.match(config.proofPrefix, /^inventory-count-source-[a-f0-9]{32}$/);
const loadBrowser = createRequire(config.playwrightPackage);
const browserPackage = loadBrowser(config.playwrightPackage);
assert.equal(browserPackage.name, "playwright"); assert.equal(browserPackage.version, "1.62.1");
const { chromium } = loadBrowser("playwright");
const browserChannel = process.env.UNITTOKO_BROWSER_CHANNEL ?? "chromium";
assert.ok(["chromium", "msedge"].includes(browserChannel));
const path = "/api/v1/inventory/stock-counts";
const location = { kind: "fulfillmentWarehouse", organizationId: "100", branchId: null, divisionId: null, shopId: "101", warehouseId: "102", channelId: "103" };
const writes = [], protectedResponses = [], faults = { dropNextWrite: false, dropped: 0 };
const connections = new Set(), failures = [], checks = [];
const captures = [], proofDirectory = join(process.cwd(), "..", "tmp", "production", "inventory-count-browser-results");
let browser, frontend, edge, tunnel;
const app = next({ dev: false, dir: process.cwd(), quiet: true });
function track(server) {
  server.on("connection", socket => { connections.add(socket); socket.on("close", () => connections.delete(socket)); });
  return server;
}
async function listen(server) {
  await new Promise((resolve, reject) => { server.once("error", reject); server.listen(0, "127.0.0.1", resolve); });
  return server.address().port;
}
function forward(req, res, port, isApi) {
  const headers = { ...req.headers, host: "127.0.0.1:" + port };
  for (const name of Object.keys(headers))
    if (name.startsWith("x-forwarded-") || name.startsWith("x-original-") || name === "forwarded" || name === "connection") delete headers[name];
  if (isApi) {
    // One exact trusted hop, based on the local listener, never on inbound forwarding headers.
    headers["x-forwarded-for"] = req.socket.remoteAddress;
    headers["x-forwarded-host"] = origin.host;
    headers["x-forwarded-proto"] = "https";
  }
  const write = isApi && req.method === "POST" && req.url === path;
  if (write) {
    const observed = { key: req.headers["idempotency-key"], expectedActor: req.headers["x-expected-actor-id"], bodyHash: null };
    const hash = createHash("sha256"); writes.push(observed);
    req.on("data", chunk => hash.update(chunk)); req.on("end", () => { observed.bodyHash = hash.digest("hex"); });
  }
  const drop = write && faults.dropNextWrite;
  if (drop) faults.dropNextWrite = false;
  const upstream = http.request({ hostname: "127.0.0.1", port, path: req.url, method: req.method, headers }, response => {
    if (isApi) {
      const observed = { path: req.url.split("?")[0], method: req.method, status: response.statusCode,
        noStore: response.headers["cache-control"]?.includes("no-store") === true,
        origin: req.headers.origin ?? null, fetchSite: req.headers["sec-fetch-site"] ?? null,
        sessionCookiePresent: (req.headers.cookie ?? "").includes("__Host-ut_session=") };
      protectedResponses.push(observed);
      if (req.headers.origin === issuer.origin && response.statusCode === 403) {
        const denied = []; let length = 0;
        response.on("data", chunk => { length += chunk.length; if (length <= 65536) denied.push(chunk); });
        response.on("end", () => { try { observed.code = JSON.parse(Buffer.concat(denied).toString()).code; } catch { observed.code = null; } });
      }
    }
    if (drop && response.statusCode === 200) {
      // Consume the real successful server response first, then lose delivery to the browser.
      response.resume(); response.once("end", () => {
        faults.dropped++;
        // A reset before response headers can be transparently retried by Chromium. Deliver an incomplete
        // body so the browser has started reading this response, then lose the rest after the real commit.
        res.writeHead(200, { "content-type": "application/json", "content-length": "64", "cache-control": "no-store" });
        res.flushHeaders(); res.write("{"); setTimeout(() => res.destroy(), 25);
      }); return;
    }
    res.writeHead(response.statusCode ?? 502, response.headers); response.pipe(res);
  });
  upstream.on("error", () => { if (!res.headersSent) res.writeHead(502); res.end(); });
  req.on("aborted", () => upstream.destroy()); req.pipe(upstream);
}
async function check(name, action) {
  await action(); checks.push(name); console.log("PASS " + name);
}
async function pending(page) {
  return page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith("unit-toko:pending:v1:")).map(key => JSON.parse(localStorage[key])));
}
async function select(page) {
  await page.getByLabel("Gudang dan kanal", { exact: true }).selectOption({ label: "Koperasi uji / Toko Tebet uji / Gudang utama uji / OMI uji" });
  const source = page.getByLabel("Sumber stok yang diamati", { exact: true });
  await source.locator('option[value="' + config.poolId + '"]').waitFor({ state: "attached" });
  assert.equal(await source.inputValue(), "");
  await source.selectOption(config.poolId);
  await page.getByRole("button", { name: /Pensil browser BROWSER-12/ }).click();
}
async function capture(page, state) {
  await mkdir(proofDirectory, { recursive: true });
  for (const [name, width, height] of [["desktop", 1280, 960], ["mobile", 390, 844]]) {
    await page.setViewportSize({ width, height });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true);
    const path = join(proofDirectory, config.proofPrefix + "-" + state + "-" + name + ".png");
    const pixels = await page.screenshot({ path, fullPage: true });
    captures.push({ state, device: name, path, sha256: createHash("sha256").update(pixels).digest("hex") });
  }
  await page.setViewportSize({ width: 1280, height: 960 });
}
async function review(page, quantity, reason) {
  await page.getByLabel("Jumlah kemasan yang ditemukan", { exact: true }).fill(quantity);
  await page.getByLabel("Alasan penghitungan", { exact: true }).fill(reason);
  await page.getByRole("button", { name: "Tinjau hitungan", exact: true }).click();
  await page.getByRole("heading", { name: "Periksa sebelum menyimpan", exact: true }).waitFor();
  await page.getByText("Sumber dipilih oleh petugas", { exact: true }).waitFor();
  if (!captures.length) await capture(page, "review");
  await page.getByRole("checkbox", { name: "Saya sudah memeriksa gudang, pilihan kaitan sumber, kemasan dan jumlah yang ditemukan.", exact: true }).check();
}
try {
  await app.prepare();
  const handle = app.getRequestHandler();
  frontend = track(http.createServer((req, res) => { void handle(req, res).catch(() => { res.statusCode = 500; res.end(); }); }));
  const frontendPort = await listen(frontend);
  edge = track(https.createServer({ pfx: Buffer.from(config.pfx, "base64"), passphrase: config.password }, (req, res) => {
    if (req.headers.host === issuer.host && req.method === "GET") {
      const url = new URL(req.url, issuer);
      if (url.pathname === "/fixture-other-origin") {
        res.writeHead(200, { "content-type": "text/html; charset=utf-8", "cache-control": "no-store" });
        res.end("<!doctype html><title>Synthetic other-origin page</title>"); return;
      }
      if (url.pathname === "/authorize" && url.searchParams.has("state")) {
        // Synthetic provider front channel. The API still validates signed tokens, nonce, state, PKCE and its assurance proof.
        res.writeHead(302, { location: origin.origin + "/auth/callback?code=fixture-code&state=" + encodeURIComponent(url.searchParams.get("state")), "cache-control": "no-store" });
        res.end(); return;
      }
    }
    if (req.headers.host !== origin.host) { res.writeHead(403); res.end(); return; }
    const isApi = req.url === "/auth" || req.url.startsWith("/auth/") || req.url.startsWith("/api/v1/");
    forward(req, res, isApi ? Number(api.port) : frontendPort, isApi);
  }));
  const tlsPort = await listen(edge);
  tunnel = track(http.createServer((_req, res) => { res.writeHead(403); res.end(); }));
  tunnel.on("connect", (req, socket, head) => {
    if (![origin.host + ":443", issuer.host + ":443"].includes(req.url)) { socket.end("HTTP/1.1 403 Forbidden\r\n\r\n"); return; }
    const upstream = net.connect(tlsPort, "127.0.0.1", () => {
      socket.write("HTTP/1.1 200 Connection Established\r\n\r\n");
      if (head.length) upstream.write(head);
      socket.pipe(upstream); upstream.pipe(socket);
    });
    connections.add(upstream);
    upstream.on("close", () => connections.delete(upstream));
    upstream.on("error", () => socket.destroy()); socket.on("error", () => upstream.destroy()); socket.on("close", () => upstream.destroy());
  });
  const proxyPort = await listen(tunnel);
  browser = await chromium.launch({ ...(browserChannel === "msedge" ? { channel: "msedge" } : {}), headless: true, proxy: { server: "http://127.0.0.1:" + proxyPort } });
  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1280, height: 960 }, locale: "id-ID", timezoneId: "Asia/Jakarta" });
  const page = await context.newPage(); page.setDefaultTimeout(20000);
  page.on("pageerror", error => failures.push(error.message));
  page.on("dialog", dialog => dialog.accept());
  await check("real sign-in callback delivers the protected browser session", async () => {
    await page.goto(origin.origin + "/inventory/counts");
    await page.getByRole("heading", { name: /^(Masuk untuk melanjutkan|Sesi Anda perlu diperbarui)$/ }).waitFor();
    await page.getByRole("link", { name: /^Masuk( kembali)?$/ }).click();
    await page.getByRole("button", { name: "Lanjutkan ke layanan akun", exact: true }).click();
    await page.waitForURL(origin.origin + "/inventory/counts");
    await page.getByLabel("Gudang dan kanal", { exact: true }).waitFor();
    const cookies = await context.cookies(origin.origin);
    for (const name of ["__Host-ut_session", "__Host-ut_csrf"]) {
      const cookie = cookies.find(value => value.name === name);
      assert.ok(cookie); assert.equal(cookie.secure, true); assert.equal(cookie.httpOnly, true);
      assert.equal(cookie.domain, origin.hostname); assert.equal(cookie.path, "/");
      assert.equal(cookie.sameSite, name.endsWith("csrf") ? "Strict" : "Lax");
    }
    assert.equal(await page.evaluate(() => document.cookie.includes("__Host-ut_")), false);
    assert.equal(cookies.some(cookie => cookie.name.includes("Correlation") || cookie.name.includes("Nonce")), false);
    const identity = await page.evaluate(async () => (await fetch("/api/v1/identity/session", { credentials: "same-origin" })).json());
    assert.equal(identity.authenticated, true); assert.equal(identity.account.userId, config.actorId);
  });
  await check("browser CSRF, actor and warehouse guards reject real requests", async () => {
    const statuses = await page.evaluate(async ({ actorId, skuId, definitionId, location, path }) => {
      const body = { location, skuId, expectedSkuRevision: "1", expectedDefinitionId: definitionId, packages: 0, observedLotLabel: null, reason: "Rejected fixture" };
      const post = async (token, actor, payload) => (await fetch(path, { method: "POST", credentials: "same-origin",
        headers: { "Content-Type": "application/json", "X-Expected-Actor-Id": actor, "Idempotency-Key": crypto.randomUUID(), ...(token ? { "X-CSRF-Token": token } : {}) }, body: JSON.stringify(payload) })).status;
      const csrf = await (await fetch("/api/v1/identity/csrf")).json();
      return [
        await post(null, actorId, body),
        await post("invalid-csrf", actorId, body),
        await post(csrf.requestToken, "48001", body),
        await post(csrf.requestToken, actorId, { ...body, location: { ...location, warehouseId: "104" } })
      ];
    }, { actorId: config.actorId, skuId: config.skuId, definitionId: config.definitionId, location, path });
    assert.deepEqual(statuses, [403, 403, 409, 403]);
  });
  await check("a same-site different origin is rejected even when the browser sends its session cookie", async () => {
    const other = await context.newPage();
    try {
      await other.goto(issuer.origin + "/fixture-other-origin");
      const before = protectedResponses.length;
      await other.evaluate(async target => { await fetch(target, { method: "POST", mode: "no-cors",
        credentials: "include", headers: { "Content-Type": "text/plain" }, body: "{}" }); }, origin.origin + path);
      const denied = protectedResponses.slice(before).find(response => response.path === path && response.method === "POST");
      assert.ok(denied); assert.equal(denied.origin, issuer.origin); assert.equal(denied.fetchSite, "same-site");
      assert.equal(denied.sessionCookiePresent, true); assert.equal(denied.status, 403); assert.equal(denied.code, "untrusted_origin");
    } finally { await other.close(); }
  });  await check("trusted local proxy overwrites browser-supplied forwarding headers", async () => {
    const result = await page.evaluate(async () => {
      const response = await fetch("/api/v1/identity/session", { headers: { "X-Forwarded-Host": "outside.invalid", "X-Forwarded-Proto": "http", "X-Forwarded-For": "192.0.2.9", "Forwarded": "host=outside.invalid;proto=http" } });
      return { status: response.status, authenticated: (await response.json()).authenticated };
    });
    assert.deepEqual(result, { status: 200, authenticated: true });
  });
  await check("real browser saves an explicit zero and reads its persisted package facts", async () => {
    await select(page); await review(page, "0", "Browser zero observation");
    await page.getByRole("button", { name: "Simpan pengamatan", exact: true }).click();
    await page.getByRole("heading", { name: "Hitungan tersimpan", exact: true }).waitFor();
    assert.equal((await pending(page)).length, 0);
    await page.getByRole("button", { name: "Riwayat pengamatan", exact: true }).click();
    await page.getByLabel("Gudang dan kanal", { exact: true }).selectOption({ label: "Koperasi uji / Toko Tebet uji / Gudang utama uji / OMI uji" });
    await page.getByRole("heading", { name: "BROWSER-12", exact: true }).waitFor();
    await page.getByText("Alasan: Browser zero observation", { exact: true }).waitFor();
    await page.getByText("0 Dus = 0 piece", { exact: true }).waitFor();
    const binding = page.getByText(/^Kaitan sumber saat dicatat:/);
    await binding.waitFor();
    assert.equal((await binding.innerText()).includes(config.sourceLabel), true);
    await capture(page, "persisted");
    const body = await page.locator("main").innerText();
    assert.equal(body.includes("private-browser-physical"), false);
  });
  await check("lost delivery after a real commit reuses one key and clears only on receipt", async () => {
    await page.getByRole("button", { name: "Catat hitungan", exact: true }).click();
    await select(page); await review(page, "2", "Browser uncertain observation");
    const before = writes.length; faults.dropNextWrite = true;
    await page.getByRole("button", { name: "Simpan pengamatan", exact: true }).click();
    await page.getByRole("button", { name: "Periksa ulang permintaan yang sama", exact: true }).waitFor().catch(async error => {
      console.error(JSON.stringify({ fixtureFaults: faults, writes: writes.length, responseStatuses: protectedResponses.slice(-8) }));
      throw error;
    });
    assert.equal(faults.dropped, 1); assert.equal(writes.length, before + 1);
    let entries = await pending(page); assert.equal(entries.length, 1);
    assert.deepEqual(Object.keys(entries[0]).sort(), ["actorId", "createdAt", "key", "operation"]);
    faults.dropNextWrite = true;
    await page.getByRole("button", { name: "Periksa ulang permintaan yang sama", exact: true }).click();
    await page.waitForFunction(() => { const button = Array.from(document.querySelectorAll("button")).find(button => button.textContent === "Periksa hasil"); return button !== undefined && !button.disabled; });
    assert.equal(faults.dropped, 2); assert.equal(writes.length, before + 2);
    assert.equal(writes[before].key, writes[before + 1].key);
    assert.match(writes[before].bodyHash, /^[a-f0-9]{64}$/);
    assert.equal(writes[before].bodyHash, writes[before + 1].bodyHash);
    entries = await pending(page); assert.equal(entries.length, 1);
    await page.reload();
    await page.getByRole("button", { name: "Periksa hasil", exact: true }).click();
    await page.getByText("Permintaan telah tersimpan", { exact: false }).waitFor();
    assert.equal((await pending(page)).length, 0); assert.equal(writes.length, before + 2);
    assert.equal(await page.getByRole("button", { name: "Selesaikan dan tutup", exact: true }).count(), 0);
  });
  await check("real logout revokes the session and clears the host cookie", async () => {
    await page.getByRole("link", { name: "Akun saya", exact: true }).click();
    await page.getByRole("button", { name: "Keluar dari Unit Toko", exact: true }).click();
    await page.waitForURL(origin.origin + "/identity/login?loggedOut=1");
    assert.equal((await context.cookies(origin.origin)).some(cookie => cookie.name === "__Host-ut_session"), false);
    const session = await page.evaluate(async () => (await fetch("/api/v1/identity/session")).json());
    assert.equal(session.authenticated, false);
  });
  assert.deepEqual(failures, []);
  assert.equal(protectedResponses.filter(response => response.path.startsWith("/api/v1/")).every(response => response.noStore), true);
  assert.equal(checks.length, 7);
  await writeFile(join(proofDirectory, config.proofPrefix + "-proof.json"), JSON.stringify({
    recordedUtc: new Date().toISOString(), checks, captures, poolId: config.poolId,
    originalKeyAndBodyPreserved: true, droppedCommittedResponses: faults.dropped, realApiResponses: protectedResponses.length,
    browserApiMongoJourney: true, syntheticProviderAndAccounts: true, stockRelease: false,
    physicalCustodyClosure: false, stagingValidated: false, productionAccepted: false,
  }, null, 2));
  console.log("REAL_STOCK_BROWSER_PASS " + JSON.stringify({ checks, droppedCommittedResponses: faults.dropped, realApiResponses: protectedResponses.length }));
} catch (error) {
  console.error(error.stack ?? String(error));
  process.exitCode = 1;
} finally {
  if (browser) await browser.close();
  for (const socket of connections) socket.destroy();
  for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve));
  await app.close();
}
