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
const writes = [], protectedResponses = [], faults = { dropNextWrite: false, dropped: 0 };
const connections = new Set(), failures = [], checks = [];
let browser, frontend, edge, tunnel;
let firstApiAt = 0;
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
    if (!firstApiAt) firstApiAt = Date.now();
    // One exact trusted hop, based on the local listener, never on inbound forwarding headers.
    headers["x-forwarded-for"] = req.socket.remoteAddress;
    headers["x-forwarded-host"] = origin.host;
    headers["x-forwarded-proto"] = "https";
  }
  const write = req.method === "POST" && /^\/api\/v1\/inventory\/sources\/pools\/[0-9]+\/suspend$/.test(req.url);
  if (write) writes.push({ key: req.headers["idempotency-key"], expectedActor: req.headers["x-expected-actor-id"] });
  const drop = write && faults.dropNextWrite;
  if (drop) faults.dropNextWrite = false;
  const upstream = http.request({ hostname: "127.0.0.1", port, path: req.url, method: req.method, headers }, response => {
    if (isApi) {
    if (!firstApiAt) firstApiAt = Date.now();
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
const screenshotDirectory = resolve(process.cwd(), "../tmp/production/stock-source-suspension-staff-results/browser");
mkdirSync(screenshotDirectory, { recursive: true });
const path = "/inventory/sources/suspensions";
async function control(action) {
  const response = await fetch(new URL("/__fixture/suspension/" + action, api), { method: "POST", headers: { "X-Fixture-Control": config.controlKey } });
  assert.equal(response.status, 204);
}
async function signin(page, expectedActor) {
  await page.goto(origin.origin + path);
  await page.getByRole("heading", { name: /^(Masuk untuk melanjutkan|Sesi Anda perlu diperbarui)$/ }).waitFor();
  await page.locator('section[aria-labelledby="workspace-access-state"]').getByRole("link", { name: /^Masuk( kembali)?$/ }).click();
  await page.getByRole("button", { name: "Lanjutkan ke layanan akun", exact: true }).click();
  await page.waitForURL(origin.origin + path); await page.getByLabel("Referensi alokasi", { exact: true }).waitFor();
  const identity = await page.evaluate(async () => (await fetch("/api/v1/identity/session")).json());
  assert.equal(identity.account.userId, expectedActor);
}
async function choose(page) {
  await page.getByLabel("Referensi alokasi", { exact: true }).fill(config.poolId);
  await page.getByRole("button", { name: "Muat alokasi", exact: true }).click();
}
async function review(page, reason) {
  await choose(page); await page.getByRole("heading", { name: "Alokasi " + config.poolId, exact: true }).waitFor();
  await page.getByLabel("Alasan penghentian", { exact: true }).fill(reason);
  await page.getByRole("button", { name: "Tinjau penghentian", exact: true }).click();
  const form = page.getByRole("heading", { name: "Konfirmasi penghentian", exact: true }).locator("..");
  await form.waitFor();
  assert.equal(await form.evaluate(item => item.parentElement === document.activeElement), true);
  for (const channel of ["103", "104"]) assert.equal(await form.getByText("Kanal " + channel, { exact: true }).count(), 1);
  assert.equal(await page.getByRole("button", { name: "Konfirmasi penghentian", exact: true }).isDisabled(), true);
  assert.equal(await page.getByLabel("Referensi alokasi", { exact: true }).isDisabled(), true);
  const checkbox = form.getByRole("checkbox", { name: /Saya telah memeriksa alokasi/ });
  await checkbox.focus(); await checkbox.press("Space"); assert.equal(await checkbox.isChecked(), true);
  await page.keyboard.press("Tab");
  assert.equal(await page.getByRole("button", { name: "Konfirmasi penghentian", exact: true }).evaluate(item => item === document.activeElement), true);
}
async function logout(page, context) {
  await page.goto(origin.origin + "/identity/account");
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
  page.on("pageerror", error => failures.push(error.message)); page.on("dialog", dialog => dialog.accept());
  await check("suspension-only account signs in without unrelated source permissions", async () => {
    await signin(page, config.actorId);
    assert.equal(await page.getByRole("link", { name: "Sumber stok", exact: true }).count(), 0);
    assert.equal(await page.getByRole("link", { name: "Pemetaan kanal", exact: true }).count(), 0);
    for (const name of ["__Host-ut_session", "__Host-ut_csrf"]) {
      const cookie = (await context.cookies(origin.origin)).find(item => item.name === name);
      assert.ok(cookie); assert.equal(cookie.httpOnly, true); assert.equal(cookie.secure, true); assert.equal(cookie.path, "/");
    }
  });
  await check("real MFA returns to the exact suspension task", async () => {
    await page.getByRole("link", { name: "Verifikasi untuk penghentian", exact: true }).click();
    await page.getByRole("button", { name: "Verifikasi dua langkah", exact: true }).click();
    await page.waitForURL(origin.origin + path); await page.getByLabel("Referensi alokasi", { exact: true }).waitFor();
  });
  await check("frozen keyboard confirmation and command-only reload recover one lost committed response", async () => {
    await review(page, "Penghentian pertama");
    await page.screenshot({ path: resolve(screenshotDirectory, "suspension-review-desktop.png"), fullPage: true });
    await page.getByRole("heading", { name: "Konfirmasi penghentian", exact: true }).locator("..").screenshot({ path: resolve(screenshotDirectory, "suspension-review-focused.png") });
    faults.dropNextWrite = true;
    await page.getByRole("button", { name: "Konfirmasi penghentian", exact: true }).click();
    await page.getByRole("button", { name: "Periksa hasil", exact: true }).waitFor();
    const entries = await pending(page); assert.equal(entries.length, 1);
    assert.deepEqual(Object.keys(entries[0]).sort(), ["actorId", "createdAt", "key", "operation"]);
    assert.equal(entries[0].actorId, config.actorId); assert.equal(entries[0].operation, "stock.source.suspend");
    assert.equal(writes.length, 1); assert.equal(writes[0].key, entries[0].key);
    assert.equal(await page.getByRole("button", { name: "Selesaikan dan tutup", exact: true }).count(), 0);
    const boundary = protectedResponses.length;
    await page.reload(); await page.getByLabel("Referensi alokasi semula", { exact: true }).waitFor();
    assert.equal(await page.getByRole("button", { name: "Periksa hasil", exact: true }).isDisabled(), true);
    await page.getByLabel("Referensi alokasi semula", { exact: true }).fill(config.poolId);
    await page.getByRole("button", { name: "Periksa hasil", exact: true }).click();
    await page.getByText("Permintaan telah tersimpan", { exact: false }).waitFor();
    assert.equal((await pending(page)).length, 0); assert.equal(writes.length, 1);
    assert.equal(protectedResponses.slice(boundary).some(value => /\/pools\/[0-9]+\/suspensions?(?:\/|$)/.test(value.path)), false);
    assert.equal(await page.getByRole("heading", { name: "Riwayat penghentian", exact: true }).count(), 0);
  });
  await check("changed source settings reject the frozen stop before a fresh confirmation", async () => {
    await review(page, "Pengaturan berubah"); await control("settings");
    await page.getByRole("button", { name: "Konfirmasi penghentian", exact: true }).click();
    await page.getByText("Pengaturan atau penghentian telah berubah.", { exact: false }).waitFor();
    assert.equal((await pending(page)).length, 0); assert.equal(writes.length, 2);
    await review(page, "Penghentian kedua");
    await page.getByRole("button", { name: "Konfirmasi penghentian", exact: true }).click();
    await page.getByRole("heading", { name: "Penghentian tercatat", exact: true }).waitFor();
    assert.equal(writes.length, 3);
    assert.ok(protectedResponses.some(value => value.path.endsWith("/suspend") && value.status === 409));
  });
  const readerContext = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 390, height: 844 }, locale: "id-ID", timezoneId: "Asia/Jakarta" });
  const reader = await readerContext.newPage(); reader.setDefaultTimeout(20000);
  reader.on("pageerror", error => failures.push(error.message)); reader.on("dialog", dialog => dialog.accept());
  await check("separate reader sees immutable history on mobile without command or private fields", async () => {
    const boundary = protectedResponses.length;
    await signin(reader, config.readerId); await choose(reader);
    await reader.getByRole("button", { name: /^Penghentian pertama / }).click();
    await reader.getByRole("heading", { name: /^Penghentian #[0-9]+$/ }).waitFor();
    assert.equal(await reader.getByRole("button", { name: "Tinjau penghentian", exact: true }).count(), 0);
    assert.equal(protectedResponses.slice(boundary).some(value => value.path.endsWith("/suspension-preview/query")), false);
    assert.equal(await reader.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await reader.screenshot({ path: resolve(screenshotDirectory, "suspension-history-mobile.png"), fullPage: true });
    await reader.getByRole("heading", { name: /^Penghentian #[0-9]+$/ }).locator("..").screenshot({ path: resolve(screenshotDirectory, "suspension-history-focused-mobile.png") });
    const text = await reader.locator("main").innerText();
    assert.ok(text.includes("9007199254740993"));
    for (const hidden of ["auth0|", "suspendedBy", "auditId", "bodyHash", "handoffReference", "Synthetic handoff"]) assert.equal(text.includes(hidden), false);
  });
  await check("revoking one shared channel conceals the reader's private history", async () => {
    await control("grant"); await reader.getByRole("button", { name: "Muat ulang riwayat", exact: true }).click();
    await reader.getByRole("heading", { name: /^(Masuk untuk melanjutkan|Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor();
    assert.equal(await reader.getByRole("heading", { name: /^Penghentian #[0-9]+$/ }).count(), 0);
    await logout(reader, readerContext);
  });
  // Both accounts share this local proxy IP. Respect the production rate-limit window.
  await new Promise(resolve => setTimeout(resolve, Math.max(0, 61000 - (Date.now() - firstApiAt))));
  await check("server-side MFA expiry denies a reviewed stop without a new event", async () => {
    await control("settings"); await review(page, "Verifikasi kedaluwarsa"); await control("mfa");
    await page.getByRole("button", { name: "Konfirmasi penghentian", exact: true }).click();
    await page.getByText("Verifikasi dua langkah terbaru diperlukan", { exact: false }).waitFor();
    assert.equal((await pending(page)).length, 0); assert.equal(writes.length, 4);
    assert.ok(protectedResponses.some(value => value.path.endsWith("/suspend") && value.status === 403));
  });
  await check("account suspension hides the workspace and refuses the retained session cookie", async () => {
    await control("account");
    await page.getByRole("button", { name: "Muat ulang sesi dan akses", exact: true }).click();
    await page.getByRole("heading", { name: /^(Masuk untuk melanjutkan|Sesi Anda perlu diperbarui)$/ }).waitFor();
    assert.equal(await page.getByLabel("Referensi alokasi", { exact: true }).count(), 0);
    const rejected = await page.evaluate(async () => ({
      identity: await (await fetch("/api/v1/identity/session")).json(),
      protectedStatus: (await fetch("/api/v1/access/current")).status,
    }));
    assert.equal(rejected.identity.authenticated, false);
    assert.equal(rejected.identity.account, null);
    assert.equal(rejected.protectedStatus, 401);
  });
  assert.deepEqual(failures, []);
  assert.equal(protectedResponses.filter(value => value.path.startsWith("/api/v1/")).every(value => value.noStore), true);
  assert.equal(writes.every(value => value.expectedActor === config.actorId), true);
  assert.equal(faults.dropped, 1); assert.equal(checks.length, 8);
  console.log("REAL_SOURCE_SUSPENSION_BROWSER_PASS " + JSON.stringify({ checks, droppedCommittedResponses: faults.dropped, realApiResponses: protectedResponses.length }));
} catch (error) {
  if (browser) for (const context of browser.contexts()) for (const page of context.pages()) {
    if (page.url().startsWith(origin.origin + "/inventory/sources")) {
      console.error("SUSPENSION_FIXTURE_VIEW " + (await page.locator("main").innerText()).slice(0, 4000));
      console.error("SUSPENSION_FIXTURE_FIELDS " + JSON.stringify(await page.locator("select").evaluateAll(items => items.map(item => ({ id: item.id, label: item.labels?.[0]?.textContent, disabled: item.disabled })))));
    }
  }
  console.error("SUSPENSION_FIXTURE_RESPONSES " + JSON.stringify(protectedResponses.slice(-12)));
  console.error(error.stack ?? String(error));
  process.exitCode = 1;
} finally {
  if (browser) await browser.close();
  for (const socket of connections) socket.destroy();
  for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve));
  await app.close();
}
