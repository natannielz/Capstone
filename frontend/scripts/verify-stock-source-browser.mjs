// Opt-in fixture only. Never imported by the application or used as a deployment proxy.
import assert from "node:assert/strict";
import http from "node:http";
import https from "node:https";
import net from "node:net";
import { createRequire } from "node:module";
import next from "next";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";

const chunks = [];
for await (const chunk of process.stdin) chunks.push(chunk);
const config = JSON.parse(Buffer.concat(chunks).toString("utf8"));
assert.equal(process.versions.node.split(".")[0], "24");
const api = new URL(config.apiAddress), origin = new URL(config.origin), issuer = new URL(config.issuer);
assert.equal(api.protocol, "http:"); assert.equal(api.hostname, "127.0.0.1"); assert.ok(Number(api.port) > 0);
assert.equal(origin.href, "https://portal.unit-toko.co.id/"); assert.equal(issuer.href, "https://login.unit-toko.co.id/");
const loadBrowser = createRequire(config.playwrightPackage);
const browserPackage = loadBrowser(config.playwrightPackage);
assert.equal(browserPackage.name, "playwright"); assert.equal(browserPackage.version, "1.62.1");
const { chromium } = loadBrowser("playwright");
const browserChannel = process.env.UNITTOKO_BROWSER_CHANNEL ?? "chromium";
assert.ok(["chromium", "msedge"].includes(browserChannel));
const path = "/api/v1/inventory/sources";
const location = { kind: "fulfillmentWarehouse", organizationId: "100", branchId: null, divisionId: null, shopId: "101", warehouseId: "102", channelId: "103" };
const writes = [], protectedResponses = [], faults = { dropNextWrite: false, dropped: 0 };
const connections = new Set(), failures = [], checks = [];
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
  const write = isApi && req.method === "POST" && (req.url === path || /^\/api\/v1\/inventory\/sources\/[0-9]+\/(approve|reject|withdraw)$/.test(req.url));
  if (write) writes.push({ key: req.headers["idempotency-key"], expectedActor: req.headers["x-expected-actor-id"] });
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
const screenshotDirectory = resolve(process.cwd(), "../tmp/production/stock-source-ui-results/browser");
mkdirSync(screenshotDirectory, { recursive: true });
const labels = { propose: "Kirim pengajuan", approve: "Setujui pengaturan", reject: "Tolak pengajuan", withdraw: "Tarik pengajuan" };
const body = { location, physicalKey: "guard-only", label: "Rejected fixture", mode: "webLedger", control: "exclusiveWebAllocation",
  maximumAgeMilliseconds: "60000", safetyBufferBaseUnits: "0", externalNamespace: null, reservationProtocolReference: null,
  discrepancyOwnerReference: "Fixture owner", handoffReference: "Fixture handoff", reason: "Rejected fixture" };
async function select(page) {
  await page.getByLabel("Gudang dan kanal", { exact: true }).selectOption({ label: "Koperasi uji / Toko Tebet uji / Gudang utama uji / OMI uji" });
}
async function proposal(page, key, label, external = false) {
  await page.getByRole("button", { name: "Pengajuan baru", exact: true }).click();
  await select(page);
  await page.getByLabel("Kode alokasi fisik", { exact: true }).fill(key);
  await page.getByLabel("Nama alokasi", { exact: true }).fill(label);
  await page.getByLabel("Cara pencatatan", { exact: true }).selectOption(external ? "externalLedger" : "webLedger");
  await page.getByLabel("Pengendalian alokasi", { exact: true }).selectOption(external ? "externalHold" : "exclusiveWebAllocation");
  await page.getByLabel("Batas usia data (detik)", { exact: true }).fill("60");
  await page.getByLabel("Cadangan pengaman (satuan dasar)", { exact: true }).fill("0");
  if (external) {
    await page.getByLabel("Kode sumber sistem lain", { exact: true }).fill("fixture-source");
    await page.getByLabel("Acuan protokol penahanan pesanan", { exact: true }).fill("Fixture reservation protocol");
  }
  await page.getByLabel("Penanggung jawab selisih", { exact: true }).fill("Fixture discrepancy owner");
  await page.getByLabel("Acuan serah terima", { exact: true }).fill("Fixture reviewed handoff");
  await page.getByLabel("Alasan pengajuan", { exact: true }).fill("Synthetic browser proposal");
  await page.getByRole("button", { name: "Tinjau pengaturan", exact: true }).click();
  await confirm(page, "propose");
}
async function confirm(page, action) {
  await page.getByRole("heading", { name: labels[action], exact: true }).waitFor();
  assert.equal(await page.getByRole("button", { name: labels[action], exact: true }).isDisabled(), true);
  const checkbox = page.getByRole("checkbox", { name: /Saya telah memeriksa gudang/ });
  await checkbox.focus(); await checkbox.press("Space"); assert.equal(await checkbox.isChecked(), true);
  await page.keyboard.press("Tab");
  assert.equal(await page.getByRole("button", { name: labels[action], exact: true }).evaluate(item => item === document.activeElement), true);
}
async function decision(page, label, action) {
  await page.getByRole("button", { name: "Riwayat pengajuan", exact: true }).click();
  await select(page);
  await page.getByRole("button", { name: new RegExp("^" + label + " ") }).click();
  await page.getByLabel("Tindakan", { exact: true }).selectOption(action);
  await page.getByLabel("Acuan peninjauan", { exact: true }).fill("Fixture reviewed decision");
  await page.getByLabel("Alasan keputusan", { exact: true }).fill("Synthetic browser decision");
  await page.getByRole("button", { name: "Tinjau keputusan", exact: true }).click();
  await confirm(page, action);
  await page.getByRole("button", { name: labels[action], exact: true }).click();
}
async function signin(page, expectedActor) {
  await page.goto(origin.origin + "/inventory/sources");
  await page.getByRole("heading", { name: /^(Masuk untuk melanjutkan|Sesi Anda perlu diperbarui)$/ }).waitFor();
  await page.locator('section[aria-labelledby="workspace-access-state"]').getByRole("link", { name: /^Masuk( kembali)?$/ }).click();
  await page.getByRole("button", { name: "Lanjutkan ke layanan akun", exact: true }).click();
  await page.waitForURL(origin.origin + "/inventory/sources");
  await page.getByLabel("Gudang dan kanal", { exact: true }).waitFor();
  const identity = await page.evaluate(async () => (await fetch("/api/v1/identity/session")).json());
  assert.equal(identity.account.userId, expectedActor);
}
async function logout(page, context) {
  await page.getByRole("link", { name: "Akun saya", exact: true }).click();
  await page.getByRole("button", { name: "Keluar dari Unit Toko", exact: true }).click();
  await page.waitForURL(origin.origin + "/identity/login?loggedOut=1");
  assert.equal((await context.cookies(origin.origin)).some(cookie => cookie.name === "__Host-ut_session"), false);
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
  await check("source workspace signs in through real verified cookies", async () => {
    await signin(page, config.actorId);
    for (const name of ["__Host-ut_session", "__Host-ut_csrf"]) {
      const cookie = (await context.cookies(origin.origin)).find(value => value.name === name);
      assert.ok(cookie); assert.equal(cookie.httpOnly, true); assert.equal(cookie.secure, true);
      assert.equal(cookie.domain, origin.hostname); assert.equal(cookie.path, "/");
    }
    assert.equal(await page.evaluate(() => document.cookie.includes("__Host-ut_")), false);
  });
  await check("source endpoint rejects missing CSRF, changed actor and unauthorized warehouse", async () => {
    const statuses = await page.evaluate(async ({ actorId, body, path }) => {
      const csrf = await (await fetch("/api/v1/identity/csrf")).json();
      const post = async (token, actor, payload) => (await fetch(path, { method: "POST",
        headers: { "Content-Type": "application/json", "X-Expected-Actor-Id": actor, "Idempotency-Key": crypto.randomUUID(),
          ...(token ? { "X-CSRF-Token": token } : {}) }, body: JSON.stringify(payload) })).status;
      return [await post(null, actorId, body), await post("invalid-csrf", actorId, body), await post(csrf.requestToken, "48002", body),
        await post(csrf.requestToken, actorId, { ...body, location: { ...body.location, warehouseId: "104" } })];
    }, { actorId: config.actorId, body, path });
    assert.deepEqual(statuses, [403, 403, 409, 403]);
  });
  await check("reviewed web proposal persists exact long ID and rejects maker self-approval", async () => {
    await proposal(page, "browser-web", "Sumber web uji");
    await page.screenshot({ path: resolve(screenshotDirectory, "source-proposal-desktop.png"), fullPage: true });
    await page.getByRole("button", { name: labels.propose, exact: true }).click();
    await page.getByRole("heading", { name: "Permintaan tersimpan", exact: true }).waitFor();
    assert.equal((await pending(page)).length, 0);
    await decision(page, "Sumber web uji", "approve");
    await page.getByRole("heading", { name: /^(Masuk untuk melanjutkan|Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor();
    assert.equal((await pending(page)).length, 0);
    assert.ok(protectedResponses.some(value => /\/approve$/.test(value.path) && value.status === 403));
    await page.getByRole("button", { name: /^(Periksa ulang|Periksa ulang akses|Coba lagi)$/ }).click();
    await page.getByLabel("Gudang dan kanal", { exact: true }).waitFor();
  });
  await check("lost committed external proposal keeps one key and recovers after reload", async () => {
    await proposal(page, "browser-external", "Sumber eksternal uji", true);
    const before = writes.length; faults.dropNextWrite = true;
    await page.getByRole("button", { name: labels.propose, exact: true }).click();
    await page.getByRole("button", { name: "Periksa ulang permintaan yang sama", exact: true }).waitFor();
    assert.equal(writes.length, before + 1); assert.equal(faults.dropped, 1);
    const entry = (await pending(page))[0]; assert.deepEqual(Object.keys(entry).sort(), ["actorId", "createdAt", "key", "operation"]);
    assert.equal(entry.actorId, config.actorId); assert.equal(entry.operation, "stock.source.propose");
    assert.equal(await page.getByLabel("Kode alokasi fisik", { exact: true }).isDisabled(), true);
    faults.dropNextWrite = true;
    await page.getByRole("button", { name: "Periksa ulang permintaan yang sama", exact: true }).click();
    await page.waitForFunction(() => { const button = Array.from(document.querySelectorAll("button")).find(value => value.textContent === "Periksa hasil"); return button && !button.disabled; });
    assert.equal(faults.dropped, 2); assert.equal(writes.length, before + 2); assert.equal(writes[before].key, writes[before + 1].key);
    await page.reload(); await page.getByLabel("Gudang dan kanal", { exact: true }).waitFor();
    assert.equal(await page.getByRole("button", { name: "Periksa hasil", exact: true }).isDisabled(), true);
    await select(page);
    await page.getByRole("button", { name: "Periksa hasil", exact: true }).click();
    await page.getByText("Permintaan telah tersimpan", { exact: false }).waitFor();
    assert.equal((await pending(page)).length, 0); assert.equal(writes.length, before + 2);
    assert.equal(await page.getByRole("button", { name: "Selesaikan dan tutup", exact: true }).count(), 0);
  });
  await check("maker withdraws only through an explicit reviewed decision", async () => {
    await proposal(page, "browser-withdraw", "Pengajuan ditarik uji");
    await page.getByRole("button", { name: labels.propose, exact: true }).click();
    await page.getByRole("heading", { name: "Permintaan tersimpan", exact: true }).waitFor();
    await decision(page, "Pengajuan ditarik uji", "withdraw");
    await page.getByRole("heading", { name: "Permintaan tersimpan", exact: true }).waitFor();
    await page.getByText(/^Ditarik. Referensi pengajuan/).waitFor();
    await logout(page, context);
  });
  await check("a separately authenticated person approves web and rejects external setup", async () => {
    await signin(page, config.reviewerId);
    assert.equal(await page.getByRole("button", { name: "Pengajuan baru", exact: true }).count(), 0);
    await decision(page, "Sumber web uji", "approve");
    await page.getByRole("heading", { name: "Permintaan tersimpan", exact: true }).waitFor();
    await page.getByText(/^Disetujui · belum aktif. Referensi pengajuan/).waitFor();
    await decision(page, "Sumber eksternal uji", "reject");
    await page.getByRole("heading", { name: "Permintaan tersimpan", exact: true }).waitFor();
    await page.getByText(/^Ditolak. Referensi pengajuan/).waitFor();
    assert.equal((await pending(page)).length, 0);
  });
  await check("mobile history remains readable and private authority fields stay absent", async () => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByRole("button", { name: "Riwayat pengajuan", exact: true }).click(); await select(page);
    await page.getByRole("button", { name: /^Sumber web uji / }).click();
    await page.getByRole("heading", { name: "Sumber web uji", exact: true }).waitFor();
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    const text = await page.locator("main").innerText();
    for (const hidden of ["auth0|", "personApproval", "private-person-", "private-source-file-", "bodyHash"]) assert.equal(text.includes(hidden), false);
    await page.screenshot({ path: resolve(screenshotDirectory, "source-history-mobile.png"), fullPage: true });
    await logout(page, context);
  });
  assert.deepEqual(failures, []);
  assert.equal(protectedResponses.filter(value => value.path.startsWith("/api/v1/")).every(value => value.noStore), true);
  assert.equal(checks.length, 7);
  console.log("REAL_SOURCE_BROWSER_PASS " + JSON.stringify({ checks, droppedCommittedResponses: faults.dropped, realApiResponses: protectedResponses.length }));
} catch (error) {
  if (browser) for (const context of browser.contexts()) for (const page of context.pages()) {
    if (page.url().startsWith(origin.origin + "/inventory/sources")) {
      console.error("SOURCE_FIXTURE_VIEW " + (await page.locator("main").innerText()).slice(0, 4000));
      console.error("SOURCE_FIXTURE_FIELDS " + JSON.stringify(await page.locator("select").evaluateAll(items => items.map(item => ({ id: item.id, label: item.labels?.[0]?.textContent, disabled: item.disabled })))));
    }
  }
  console.error("SOURCE_FIXTURE_RESPONSES " + JSON.stringify(protectedResponses.slice(-12)));
  console.error(error.stack ?? String(error));
  process.exitCode = 1;
} finally {
  if (browser) await browser.close();
  for (const socket of connections) socket.destroy();
  for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve));
  await app.close();
}
