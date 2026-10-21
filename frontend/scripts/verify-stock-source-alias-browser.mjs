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
  const write = req.method === "POST" && (/^\/api\/v1\/inventory\/sources\/pools\/[0-9]+\/aliases$/.test(req.url) || /^\/api\/v1\/inventory\/sources\/alias-proposals\/[0-9]+\/(approve|reject|withdraw)$/.test(req.url));
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
const screenshotDirectory = resolve(process.cwd(), "../tmp/production/stock-source-alias-ui-results/browser");
mkdirSync(screenshotDirectory, { recursive: true });
const labels = { propose: "Kirim pemetaan", approve: "Setujui pemetaan", reject: "Tolak pemetaan", withdraw: "Tarik pemetaan" };
const ownerLabel = "Koperasi uji / Toko Tebet uji / Gudang utama uji / OMI uji";
const requestedLabel = "Koperasi uji / Toko Tebet uji / Gudang utama uji / S-Mart uji";
const path = "/inventory/sources/aliases";
async function choose(page) {
  await page.getByLabel("Gudang dan kanal pemilik alokasi", { exact: true }).selectOption({ label: ownerLabel });
  await page.getByRole("button", { name: "Muat ulang alokasi", exact: true }).click();
  await page.getByRole("button", { name: /^Alokasi browser uji / }).click();
  await page.getByLabel("Kanal tujuan pemetaan", { exact: true }).selectOption({ label: requestedLabel });
  await page.getByRole("heading", { name: "Alokasi browser uji", exact: true }).waitFor();
}
async function confirm(page, action) {
  await page.getByRole("heading", { name: labels[action], exact: true }).waitFor();
  assert.equal(await page.getByRole("button", { name: labels[action], exact: true }).isDisabled(), true);
  const checkbox = page.getByRole("checkbox", { name: /Saya telah memeriksa alokasi/ });
  await checkbox.focus(); await checkbox.press("Space"); assert.equal(await checkbox.isChecked(), true);
  await page.keyboard.press("Tab");
  assert.equal(await page.getByRole("button", { name: labels[action], exact: true }).evaluate(item => item === document.activeElement), true);
}
async function proposal(page, enabled, reason) {
  await choose(page);
  await page.getByLabel("Penggunaan alokasi oleh kanal tujuan", { exact: true }).selectOption(enabled ? "enabled" : "disabled");
  await page.getByLabel("Alasan pemetaan", { exact: true }).fill(reason);
  await page.getByRole("button", { name: "Tinjau pemetaan", exact: true }).click(); await confirm(page, "propose");
}
async function saved(page) { await page.getByRole("heading", { name: "Pemetaan tersimpan", exact: true }).waitFor(); }
async function selectProposal(page, reason) {
  await choose(page); await page.getByRole("button", { name: new RegExp("^" + reason + " ") }).click();
  await page.getByRole("heading", { name: /^Pengajuan #[0-9]+$/ }).waitFor();
}
async function decisionReview(page, reason, action) {
  await selectProposal(page, reason);
  await page.getByLabel("Tindakan", { exact: true }).selectOption(action);
  await page.getByLabel("Acuan peninjauan", { exact: true }).fill("Synthetic independent mapping review");
  await page.getByLabel("Alasan keputusan", { exact: true }).fill("Synthetic channel decision");
  await page.getByRole("button", { name: "Tinjau keputusan", exact: true }).click(); await confirm(page, action);
}
async function signin(page, expectedActor) {
  await page.goto(origin.origin + path);
  await page.getByRole("heading", { name: /^(Masuk untuk melanjutkan|Sesi Anda perlu diperbarui)$/ }).waitFor();
  await page.getByRole("link", { name: /^Masuk( kembali)?$/ }).click();
  await page.getByRole("button", { name: "Lanjutkan ke layanan akun", exact: true }).click();
  await page.waitForURL(origin.origin + path); await page.getByLabel("Akses lokasi", { exact: true }).waitFor();
  const identity = await page.evaluate(async () => (await fetch("/api/v1/identity/session")).json());
  assert.equal(identity.account.userId, expectedActor);
}
async function logout(page, context) {
  await page.goto(origin.origin + "/identity/account");
  await page.getByRole("button", { name: "Keluar dari Unit Toko", exact: true }).click();
  await page.waitForURL(origin.origin + "/identity/login?loggedOut=1");
  assert.equal((await context.cookies(origin.origin)).some(cookie => cookie.name === "__Host-ut_session"), false);
}
async function recover(page, operation, before, commandOnly = false) {
  await page.getByRole("button", { name: "Periksa hasil", exact: true }).waitFor();
  await page.waitForFunction(() => Array.from(document.querySelectorAll("button")).some(item => item.textContent === "Periksa hasil" && !item.disabled));
  const entry = (await pending(page))[0]; assert.deepEqual(Object.keys(entry).sort(), ["actorId", "createdAt", "key", "operation"]);
  assert.equal(entry.operation, operation); assert.equal(writes.length, before + 1);
  assert.equal(await page.getByRole("button", { name: "Selesaikan dan tutup", exact: true }).count(), 0);
  if (commandOnly) await control("command-only");
  const boundary = protectedResponses.length;
  await page.reload(); await page.getByLabel("Akses lokasi", { exact: true }).waitFor();
  assert.equal(await page.getByRole("button", { name: "Periksa hasil", exact: true }).isDisabled(), true);
  if (commandOnly) await page.getByRole("heading", { name: "Akses pengaturan diperlukan", exact: true }).waitFor();
  await page.getByLabel("Referensi alokasi semula", { exact: true }).fill(config.poolId);
  await page.getByLabel("Kanal tujuan semula", { exact: true }).selectOption({ label: requestedLabel });
  await page.getByRole("button", { name: "Periksa hasil", exact: true }).click();
  await page.getByText("Permintaan telah tersimpan", { exact: false }).waitFor();
  assert.equal((await pending(page)).length, 0); assert.equal(writes.length, before + 1);
  if (commandOnly) {
    assert.equal(protectedResponses.slice(boundary).some(value => value.path.includes("/inventory/sources/pools")), false);
    await control("restore-read"); await page.reload(); await page.getByLabel("Gudang dan kanal pemilik alokasi", { exact: true }).waitFor();
  }
}
async function control(action) {
  const response = await fetch(new URL("/__fixture/alias/" + action, api), { method: "POST", headers: { "X-Fixture-Control": config.controlKey } });
  assert.equal(response.status, 204);
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
  await check("alias sign-in returns to the staff task with protected cookies", async () => {
    await signin(page, config.actorId);
    for (const name of ["__Host-ut_session", "__Host-ut_csrf"]) {
      const cookie = (await context.cookies(origin.origin)).find(value => value.name === name);
      assert.ok(cookie); assert.equal(cookie.httpOnly, true); assert.equal(cookie.secure, true); assert.equal(cookie.path, "/");
    }
  });
  await check("keyboard-reviewed mapping survives a lost commit response and command-only reload recovery", async () => {
    await proposal(page, true, "Pemetaan pertama");
    await page.screenshot({ path: resolve(screenshotDirectory, "alias-review-desktop.png"), fullPage: true });
    await page.getByRole("heading", { name: labels.propose, exact: true }).locator("..").screenshot({ path: resolve(screenshotDirectory, "alias-review-focused.png") });
    const before = writes.length; faults.dropNextWrite = true;
    await page.getByRole("button", { name: labels.propose, exact: true }).click();
    await recover(page, "stock.source.alias.propose", before, true);
  });
  await check("maker cannot approve their own mapping", async () => {
    await decisionReview(page, "Pemetaan pertama", "approve");
    await page.getByRole("button", { name: labels.approve, exact: true }).click();
    await page.getByRole("heading", { name: /^(Masuk untuk melanjutkan|Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor();
    await page.getByText("Keputusan memerlukan peninjau yang berbeda", { exact: false }).waitFor();
    assert.equal((await pending(page)).length, 0);
    await page.getByRole("button", { name: /^(Periksa ulang|Periksa ulang akses|Coba lagi)$/ }).click();
    await page.getByLabel("Gudang dan kanal pemilik alokasi", { exact: true }).waitFor();
  });
  await check("competing proposal and maker withdrawal retain separate immutable history", async () => {
    await proposal(page, true, "Pemetaan pembanding");
    await page.getByRole("button", { name: labels.propose, exact: true }).click(); await saved(page);
    await proposal(page, true, "Ditarik pengaju");
    await page.getByRole("button", { name: labels.propose, exact: true }).click(); await saved(page);
    await decisionReview(page, "Ditarik pengaju", "withdraw");
    await page.getByRole("button", { name: labels.withdraw, exact: true }).click(); await saved(page);
  });
  const reviewerContext = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1280, height: 960 }, locale: "id-ID", timezoneId: "Asia/Jakarta" });
  const reviewer = await reviewerContext.newPage(); reviewer.setDefaultTimeout(20000);
  reviewer.on("pageerror", error => failures.push(error.message)); reviewer.on("dialog", dialog => dialog.accept());
  await check("independent reviewer completes real MFA round-trip and recovers one approval", async () => {
    await signin(reviewer, config.reviewerId);
    await reviewer.getByRole("link", { name: "Verifikasi untuk peninjauan", exact: true }).click();
    await reviewer.getByRole("button", { name: "Verifikasi dua langkah", exact: true }).click();
    await reviewer.waitForURL(origin.origin + path); await reviewer.getByLabel("Akses lokasi", { exact: true }).waitFor();
    await decisionReview(reviewer, "Pemetaan pertama", "approve");
    const before = writes.length; faults.dropNextWrite = true;
    await reviewer.getByRole("button", { name: labels.approve, exact: true }).click();
    await recover(reviewer, "stock.source.alias.approve", before);
    await choose(reviewer); await reviewer.getByText("Sesuai pengaturan saat ini", { exact: true }).waitFor();
    assert.equal(await reviewer.getByRole("heading", { name: "Ajukan pemetaan", exact: true }).count(), 0);
  });
  await check("stale mapping base blocks approval in the screen and server but remains rejectable", async () => {
    await selectProposal(reviewer, "Pemetaan pembanding");
    await reviewer.getByText("Dasar pengajuan telah berubah", { exact: false }).waitFor();
    assert.equal(await reviewer.getByLabel("Tindakan", { exact: true }).locator('option[value="approve"]').evaluate(item => item.disabled), true);
    assert.equal(await reviewer.getByLabel("Tindakan", { exact: true }).inputValue(), "reject");
    const id = (await reviewer.getByRole("heading", { name: /^Pengajuan #[0-9]+$/ }).innerText()).split("#")[1];
    const status = await reviewer.evaluate(async ({ id, actorId }) => {
      const csrf = await (await fetch("/api/v1/identity/csrf")).json();
      return (await fetch("/api/v1/inventory/sources/alias-proposals/" + id + "/approve", { method: "POST",
        headers: { "Content-Type": "application/json", "X-CSRF-Token": csrf.requestToken, "X-Expected-Actor-Id": actorId, "If-Match": '"1"', "Idempotency-Key": crypto.randomUUID() },
        body: JSON.stringify({ reviewReference: "Synthetic stale probe", reason: "Synthetic stale probe" }) })).status;
    }, { id, actorId: config.reviewerId });
    assert.equal(status, 409);
    await decisionReview(reviewer, "Pemetaan pembanding", "reject");
    await reviewer.getByRole("button", { name: labels.reject, exact: true }).click(); await saved(reviewer);
  });
  await check("changed source settings require explicit mapping reapproval", async () => {
    await control("settings");
    await choose(page); await page.getByText("Perlu peninjauan ulang", { exact: true }).waitFor();
    await proposal(page, true, "Pemetaan pengaturan baru");
    await page.getByRole("button", { name: labels.propose, exact: true }).click(); await saved(page);
    await decisionReview(reviewer, "Pemetaan pengaturan baru", "approve");
    await reviewer.getByRole("button", { name: labels.approve, exact: true }).click(); await saved(reviewer);
    await choose(reviewer); await reviewer.getByText("Sesuai pengaturan saat ini", { exact: true }).waitFor();
  });
  await check("disable preserves prior decisions and readable mobile history without leaking internals", async () => {
    await proposal(page, false, "Nonaktifkan kanal");
    await page.getByRole("button", { name: labels.propose, exact: true }).click(); await saved(page);
    await decisionReview(reviewer, "Nonaktifkan kanal", "approve");
    await reviewer.getByRole("button", { name: labels.approve, exact: true }).click(); await saved(reviewer);
    await selectProposal(reviewer, "Pemetaan pertama");
    await reviewer.getByText("Pemetaan dinonaktifkan", { exact: true }).waitFor();
    await reviewer.getByRole("heading", { name: "Keputusan tercatat", exact: true }).waitFor();
    await reviewer.setViewportSize({ width: 390, height: 844 });
    assert.equal(await reviewer.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await reviewer.screenshot({ path: resolve(screenshotDirectory, "alias-history-mobile.png"), fullPage: true });
    await reviewer.getByRole("heading", { name: /^Pengajuan #[0-9]+$/ }).locator("..").screenshot({ path: resolve(screenshotDirectory, "alias-history-focused-mobile.png") });
    const text = await reviewer.locator("main").innerText();
    for (const hidden of ["auth0|", "personApproval", "private-person-", "bodyHash"]) assert.equal(text.includes(hidden), false);
    await reviewer.setViewportSize({ width: 1280, height: 960 });
  });
  // This compact automated scenario shares one IP across both people. Respect the unchanged production
  // 240-request/minute window rather than relaxing the limiter or retrying any business command.
  await new Promise(resolve => setTimeout(resolve, Math.max(0, 61000 - (Date.now() - firstApiAt))));
  await check("MFA expiry after frozen review denies approval without a stored mapping", async () => {
    await proposal(page, true, "Verifikasi perlu baru");
    await page.getByRole("button", { name: labels.propose, exact: true }).click(); await saved(page);
    await decisionReview(reviewer, "Verifikasi perlu baru", "approve"); await control("mfa");
    await reviewer.getByRole("button", { name: labels.approve, exact: true }).click();
    await reviewer.getByText("Lakukan verifikasi dua langkah terbaru", { exact: false }).waitFor();
    assert.equal((await pending(reviewer)).length, 0);
    assert.ok(protectedResponses.some(value => /\/alias-proposals\/[0-9]+\/approve$/.test(value.path) && value.status === 403));
  });
  await check("revoking the requested channel grant conceals private history and still permits sign-out", async () => {
    await choose(reviewer); await reviewer.getByRole("button", { name: /^Verifikasi perlu baru / }).waitFor();
    await control("grant"); await reviewer.getByRole("button", { name: "Muat ulang riwayat", exact: true }).click();
    await reviewer.getByRole("heading", { name: /^(Masuk untuk melanjutkan|Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor();
    assert.equal(await reviewer.getByRole("heading", { name: "Alokasi browser uji", exact: true }).count(), 0);
    await logout(reviewer, reviewerContext); await logout(page, context);
  });
  assert.deepEqual(failures, []);
  assert.equal(protectedResponses.filter(value => value.path.startsWith("/api/v1/")).every(value => value.noStore), true);
  assert.equal(faults.dropped, 2); assert.equal(checks.length, 10);
  console.log("REAL_SOURCE_ALIAS_BROWSER_PASS " + JSON.stringify({ checks, droppedCommittedResponses: faults.dropped, realApiResponses: protectedResponses.length }));

} catch (error) {
  if (browser) for (const context of browser.contexts()) for (const page of context.pages()) {
    if (page.url().startsWith(origin.origin + "/inventory/sources")) {
      console.error("ALIAS_FIXTURE_VIEW " + (await page.locator("main").innerText()).slice(0, 4000));
      console.error("ALIAS_FIXTURE_FIELDS " + JSON.stringify(await page.locator("select").evaluateAll(items => items.map(item => ({ id: item.id, label: item.labels?.[0]?.textContent, disabled: item.disabled })))));
    }
  }
  console.error("ALIAS_FIXTURE_RESPONSES " + JSON.stringify(protectedResponses.slice(-12)));
  console.error(error.stack ?? String(error));
  process.exitCode = 1;
} finally {
  if (browser) await browser.close();
  for (const socket of connections) socket.destroy();
  for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve));
  await app.close();
}
