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
  const write = req.method === "POST" && (/^\/api\/v1\/inventory\/sources\/pools\/[0-9]+\/policy-proposals$/.test(req.url) || /^\/api\/v1\/inventory\/sources\/policy-proposals\/[0-9]+\/(approve|reject|withdraw)$/.test(req.url));
  if (write) writes.push({ path: req.url, key: req.headers["idempotency-key"], expectedActor: req.headers["x-expected-actor-id"] });
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
const screenshotDirectory = resolve(process.cwd(), "../tmp/production/stock-source-operating-policy-staff-results/browser");
mkdirSync(screenshotDirectory, { recursive: true });
const path = "/inventory/sources/policies";
async function control(action) {
  const response = await fetch(new URL("/__fixture/policy/" + action, api), { method: "POST", headers: { "X-Fixture-Control": config.controlKey } });
  assert.equal(response.status, action === "state" ? 200 : 204);
  return action === "state" ? response.json() : null;
}
async function signin(page, who) {
  await control(who); await page.goto(origin.origin + path);
  await page.getByRole("heading", { name: /^(Masuk untuk melanjutkan|Sesi Anda perlu diperbarui)$/ }).waitFor();
  await page.getByRole("link", { name: /^Masuk( kembali)?$/ }).click();
  await page.getByRole("button", { name: "Lanjutkan ke layanan akun", exact: true }).click();
  await page.waitForURL(origin.origin + path); await page.getByLabel("Referensi alokasi", { exact: true }).waitFor();
  const identity = await page.evaluate(async () => (await fetch("/api/v1/identity/session")).json());
  assert.equal(identity.account.userId, config[who + "Id"]);
}
async function choose(page) {
  await page.getByLabel("Referensi alokasi", { exact: true }).fill(config.poolId);
  await page.getByRole("button", { name: "Muat alokasi", exact: true }).click();
}
async function frozen(page, title) {
  const form = page.getByRole("heading", { name: title, exact: true }).locator("..");
  await form.waitFor();
  assert.equal(await form.evaluate(item => item.parentElement === document.activeElement), true);
  assert.equal(await form.getByRole("button", { name: title, exact: true }).isDisabled(), true);
  assert.equal(await page.getByLabel("Referensi alokasi", { exact: true }).isDisabled(), true);
  const text = await form.innerText();
  for (const fact of ["Kanal 103", "Kanal 104", "9.007.199.254.740.993 satuan dasar", "Synthetic owner"]) assert.ok(text.includes(fact), fact);
  const checkbox = form.getByRole("checkbox"); await checkbox.focus(); await checkbox.press("Space");
  assert.equal(await checkbox.isChecked(), true); await page.keyboard.press("Tab");
  assert.equal(await form.getByRole("button", { name: title, exact: true }).evaluate(item => item === document.activeElement), true);
  return form;
}
async function propose(page, reason) {
  await choose(page); await page.getByLabel("Pencatat stok utama", { exact: true }).selectOption("unitToko");
  await page.getByLabel("Acuan prosedur rekonsiliasi", { exact: true }).fill("Rekonsiliasi " + reason);
  await page.getByLabel("Acuan prosedur gangguan", { exact: true }).fill("Gangguan " + reason);
  await page.getByLabel("Alasan pengajuan", { exact: true }).fill(reason);
  await page.getByRole("button", { name: "Tinjau pengajuan", exact: true }).click();
  return frozen(page, "Kirim pengajuan");
}
const actionTitle = { approve: "Setujui kebijakan", reject: "Tolak pengajuan", withdraw: "Tarik pengajuan saya" };
async function decision(page, id, action, reason) {
  await choose(page); await page.getByLabel("Referensi pengajuan", { exact: true }).fill(id);
  await page.getByLabel("Tindakan", { exact: true }).selectOption(action);
  await page.getByRole("button", { name: "Periksa pengajuan", exact: true }).click();
  await page.getByLabel("Acuan peninjauan", { exact: true }).fill("Peninjauan " + reason);
  await page.getByLabel("Alasan keputusan", { exact: true }).fill(reason);
  await page.getByRole("button", { name: "Tinjau keputusan", exact: true }).click();
  return frozen(page, actionTitle[action]);
}
async function save(page, title) {
  await page.getByRole("button", { name: title, exact: true }).click();
  await page.getByRole("heading", { name: "Kebijakan tercatat", exact: true }).waitFor();
}
async function loseAndRecover(page, title, actor, operation) {
  const before = writes.length; faults.dropNextWrite = true;
  await page.getByRole("button", { name: title, exact: true }).click();
  await page.getByRole("button", { name: "Periksa hasil", exact: true }).waitFor();
  await page.waitForFunction(() => [...document.querySelectorAll("button")].some(item => item.textContent === "Periksa hasil" && !item.disabled));
  const entries = await pending(page); assert.equal(entries.length, 1);
  assert.deepEqual(Object.keys(entries[0]).sort(), ["actorId", "createdAt", "key", "operation"]);
  assert.equal(entries[0].actorId, actor); assert.equal(entries[0].operation, operation);
  assert.equal(writes.length, before + 1); assert.equal(writes.at(-1).key, entries[0].key);
  assert.equal(await page.getByRole("button", { name: "Selesaikan dan tutup", exact: true }).count(), 0);
  const boundary = protectedResponses.length;
  await page.reload(); await page.getByLabel("Referensi alokasi semula", { exact: true }).waitFor();
  assert.equal(await page.getByRole("button", { name: "Periksa hasil", exact: true }).isDisabled(), true);
  await page.getByLabel("Referensi alokasi semula", { exact: true }).fill(config.poolId);
  await page.getByRole("button", { name: "Periksa hasil", exact: true }).click();
  await page.getByText("Permintaan telah tersimpan", { exact: false }).waitFor();
  assert.equal((await pending(page)).length, 0); assert.equal(writes.length, before + 1);
  assert.equal(protectedResponses.slice(boundary).some(value => /\/pools\/[0-9]+\/policy(?:-proposals)?\/query$/.test(value.path)), false);
}
async function nextWindow() {
  const duration = Math.max(0, 60000 - (Date.now() - firstApiAt));
  if (duration) await new Promise(resolve => setTimeout(resolve, duration));
  await new Promise(resolve => setTimeout(resolve, 1200)); firstApiAt = Date.now();
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
  await check("policy-only maker signs in without source management or history access", async () => {
    await signin(page, "maker");
    for (const label of ["Sumber stok", "Pemetaan kanal", "Akses dan keanggotaan"])
      assert.equal(await page.getByRole("link", { name: label, exact: true }).count(), 0);
    for (const name of ["__Host-ut_session", "__Host-ut_csrf"]) {
      const cookie = (await context.cookies(origin.origin)).find(item => item.name === name);
      assert.ok(cookie); assert.equal(cookie.httpOnly, true); assert.equal(cookie.secure, true); assert.equal(cookie.path, "/");
    }
  });
  await check("keyboard confirmation freezes reviewed facts before lost proposal recovery", async () => {
    const form = await propose(page, "Kebijakan pertama");
    await form.screenshot({ path: resolve(screenshotDirectory, "policy-proposal-review-desktop.png") });
    await loseAndRecover(page, "Kirim pengajuan", config.makerId, "stock.source.policy.propose");
    assert.equal(await page.getByRole("heading", { name: "Riwayat kebijakan", exact: true }).count(), 0);
  });
  const first = (await control("state")).ids[0]; assert.equal(first, "9007199254740993");
  await check("original maker withdrawal recovers its result after reload", async () => {
    await propose(page, "Kebijakan ditarik"); await save(page, "Kirim pengajuan");
    const id = (await control("state")).ids[1];
    await decision(page, id, "withdraw", "Penarikan oleh pengaju");
    await loseAndRecover(page, "Tarik pengajuan saya", config.makerId, "stock.source.policy.withdraw");
  });
  await propose(page, "Kebijakan tertunda"); await save(page, "Kirim pengajuan");
  const pendingId = (await control("state")).ids[2];
  await nextWindow();
  const checkerContext = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1280, height: 960 }, locale: "id-ID", timezoneId: "Asia/Jakarta" });
  const checker = await checkerContext.newPage(); checker.setDefaultTimeout(20000);
  checker.on("pageerror", error => failures.push(error.message)); checker.on("dialog", dialog => dialog.accept());
  await check("checker-only provider MFA returns to the exact policy desk", async () => {
    const boundary = protectedResponses.length; await signin(checker, "checker");
    await checker.getByRole("link", { name: "Verifikasi untuk peninjauan", exact: true }).click();
    await checker.getByRole("button", { name: "Verifikasi dua langkah", exact: true }).click();
    await checker.waitForURL(origin.origin + path); await checker.getByLabel("Referensi alokasi", { exact: true }).waitFor(); await choose(checker);
    assert.equal(await checker.getByLabel("Pencatat stok utama", { exact: true }).count(), 0);
    assert.equal(await checker.getByRole("heading", { name: "Riwayat kebijakan", exact: true }).count(), 0);
    assert.equal(protectedResponses.slice(boundary).some(value => value.path.endsWith("/policy-preview/query")), false);
  });
  await check("accounts linked to the same person cannot inspect a checker decision", async () => {
    await control("same-person"); await checker.getByLabel("Referensi pengajuan", { exact: true }).fill(first);
    await checker.getByRole("button", { name: "Periksa pengajuan", exact: true }).click();
    await checker.getByRole("heading", { name: /^(Masuk untuk melanjutkan|Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor();
    await control("distinct-people"); await checker.getByRole("button", { name: /^(Periksa ulang|Periksa ulang akses|Coba lagi)$/ }).click();
    await checker.getByLabel("Referensi alokasi", { exact: true }).waitFor();
  });
  await check("a real stop rejects frozen approval while stale rejection recovers after reload", async () => {
    await decision(checker, first, "approve", "Persetujuan sebelum penghentian"); await control("stop");
    await checker.getByRole("button", { name: "Setujui kebijakan", exact: true }).click();
    await checker.getByText("Pengaturan atau pengajuan telah berubah.", { exact: false }).waitFor();
    assert.equal((await pending(checker)).length, 0);
    const form = await decision(checker, first, "reject", "Penolakan setelah penghentian");
    assert.ok((await form.innerText()).includes("Pengaturan dasar sudah berubah"));
    await loseAndRecover(checker, "Tolak pengajuan", config.checkerId, "stock.source.policy.reject");
  });
  await propose(page, "Kebijakan siap ditinjau"); await save(page, "Kirim pengajuan");
  const finalId = (await control("state")).ids[3];
  await check("independent approval recovers one committed policy without source activation", async () => {
    const form = await decision(checker, finalId, "approve", "Persetujuan terbaru");
    await form.screenshot({ path: resolve(screenshotDirectory, "policy-approval-review-desktop.png") });
    await loseAndRecover(checker, "Setujui kebijakan", config.checkerId, "stock.source.policy.approve");
  });
  await nextWindow();
  const readerContext = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 390, height: 844 }, locale: "id-ID", timezoneId: "Asia/Jakarta" });
  const reader = await readerContext.newPage(); reader.setDefaultTimeout(20000);
  reader.on("pageerror", error => failures.push(error.message)); reader.on("dialog", dialog => dialog.accept());
  await check("mobile reader sees all four states and exact references without command access", async () => {
    const boundary = protectedResponses.length; await signin(reader, "reader"); await choose(reader);
    await reader.getByRole("button", { name: /^Kebijakan pertama / }).waitFor();
    const queue = reader.getByRole("list").filter({ has: reader.getByRole("button", { name: /^Kebijakan pertama / }) });
    for (const status of ["Menunggu peninjauan", "Disetujui", "Ditolak", "Ditarik"]) assert.ok((await queue.innerText()).includes(status), status);
    await reader.getByRole("button", { name: /^Kebijakan siap ditinjau / }).click();
    await reader.getByText("Pengajuan #" + finalId + " · Disetujui ·", { exact: false }).waitFor();
    assert.equal(await reader.getByRole("button", { name: "Tinjau pengajuan", exact: true }).count(), 0);
    assert.equal(await reader.getByLabel("Referensi pengajuan", { exact: true }).count(), 0);
    assert.equal(protectedResponses.slice(boundary).some(value => value.path.endsWith("/policy-preview/query") || value.path.endsWith("/review/query")), false);
    assert.equal(await reader.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    const text = await reader.locator("main").innerText(); assert.ok(text.includes("9007199254740993"));
    for (const hidden of ["auth0|", "proposedBy", "auditId", "bodyHash", "correlationId", "configurationHash"]) assert.equal(text.includes(hidden), false);
    await reader.screenshot({ path: resolve(screenshotDirectory, "policy-history-mobile.png"), fullPage: true });
  });
  await check("revoking one shared-channel grant conceals private history", async () => {
    await control("grant"); await reader.getByRole("button", { name: "Muat ulang riwayat", exact: true }).click();
    await reader.getByRole("heading", { name: /^(Masuk untuk melanjutkan|Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor();
    assert.equal(await reader.getByRole("heading", { name: "Riwayat kebijakan", exact: true }).count(), 0);
  });
  await check("server MFA expiry refuses a reviewed decision without a new event", async () => {
    await decision(checker, pendingId, "reject", "Verifikasi kedaluwarsa"); await control("mfa");
    await checker.getByRole("button", { name: "Tolak pengajuan", exact: true }).click();
    await checker.getByText("Verifikasi dua langkah terbaru diperlukan", { exact: false }).waitFor();
    assert.equal((await pending(checker)).length, 0);
    assert.ok(protectedResponses.some(value => value.path.endsWith("/reject") && value.status === 403));
  });
  await check("account suspension conceals the maker workspace", async () => {
    await control("account"); await page.getByRole("button", { name: "Muat ulang sesi dan akses", exact: true }).click();
    await page.getByRole("heading", { name: /^(Masuk untuk melanjutkan|Sesi Anda perlu diperbarui)$/ }).waitFor();
    assert.equal(await page.getByLabel("Referensi alokasi", { exact: true }).count(), 0);
  });
  assert.deepEqual(failures, []);
  assert.equal(protectedResponses.filter(value => value.path.startsWith("/api/v1/")).every(value => value.noStore), true);
  assert.equal(writes.every(value => value.expectedActor === (value.path.endsWith("/policy-proposals") || value.path.endsWith("/withdraw") ? config.makerId : config.checkerId)), true);
  assert.equal(faults.dropped, 4); assert.equal(writes.length, 9); assert.equal(checks.length, 11);
  console.log("REAL_SOURCE_POLICY_BROWSER_PASS " + JSON.stringify({ checks, droppedCommittedResponses: faults.dropped, realApiResponses: protectedResponses.length }));
} catch (error) {
  if (browser) for (const context of browser.contexts()) for (const page of context.pages()) {
    if (page.url().startsWith(origin.origin + "/inventory/sources")) {
      console.error("POLICY_FIXTURE_VIEW " + (await page.locator("main").innerText()).slice(0, 4000));
      console.error("POLICY_FIXTURE_FIELDS " + JSON.stringify(await page.locator("select").evaluateAll(items => items.map(item => ({ id: item.id, label: item.labels?.[0]?.textContent, disabled: item.disabled })))));
    }
  }
  console.error("POLICY_FIXTURE_RESPONSES " + JSON.stringify(protectedResponses.slice(-12)));
  console.error(error.stack ?? String(error));
  process.exitCode = 1;
} finally {
  if (browser) await browser.close();
  for (const socket of connections) socket.destroy();
  for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve));
  await app.close();
}
