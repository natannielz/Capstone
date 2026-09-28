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
  const write = isApi && req.method === "POST" && (/^\/api\/v1\/inventory\/sources\/pools\/[0-9]+\/revisions$/.test(req.url) || /^\/api\/v1\/inventory\/sources\/revisions\/[0-9]+\/(approve|reject|withdraw)$/.test(req.url));
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
const screenshotDirectory = resolve(process.cwd(), "../tmp/production/stock-source-revision-ui-results/browser");
mkdirSync(screenshotDirectory, { recursive: true });
const labels = { propose: "Kirim perubahan", approve: "Setujui perubahan", reject: "Tolak perubahan", withdraw: "Tarik perubahan" };
async function select(page) {
  await page.getByLabel("Gudang dan kanal", { exact: true }).selectOption({ label: "Koperasi uji / Toko Tebet uji / Gudang utama uji / OMI uji" });
}
async function choose(page, label) {
  await select(page);
  await page.getByRole("button", { name: "Muat ulang alokasi", exact: true }).click();
  await page.getByRole("button", { name: new RegExp("^" + label + " ") }).click();
  await page.getByRole("heading", { name: label, exact: true }).waitFor();
}
async function confirm(page, action) {
  await page.getByRole("heading", { name: labels[action], exact: true }).waitFor();
  assert.equal(await page.getByRole("button", { name: labels[action], exact: true }).isDisabled(), true);
  const checkbox = page.getByRole("checkbox", { name: /Saya telah memeriksa alokasi/ });
  await checkbox.focus(); await checkbox.press("Space"); assert.equal(await checkbox.isChecked(), true);
  await page.keyboard.press("Tab");
  assert.equal(await page.getByRole("button", { name: labels[action], exact: true }).evaluate(item => item === document.activeElement), true);
}
async function proposal(page, sourceName, label, reason) {
  await choose(page, sourceName);
  assert.equal(await page.getByLabel("Batas usia data (detik)", { exact: true }).inputValue(), "60.001");
  assert.equal(await page.getByLabel("Cadangan pengaman (satuan dasar)", { exact: true }).inputValue(), "9007199254740993");
  await page.getByLabel("Nama alokasi", { exact: true }).fill(label);
  await page.getByLabel("Alasan perubahan", { exact: true }).fill(reason);
  await page.getByRole("button", { name: "Tinjau perubahan", exact: true }).click();
  await confirm(page, "propose");
}
async function saved(page) { await page.getByRole("heading", { name: "Perubahan tersimpan", exact: true }).waitFor(); }
async function selectProposal(page, sourceName, reason) {
  await choose(page, sourceName); await page.getByRole("button", { name: new RegExp("^" + reason + " ") }).click();
  await page.getByRole("heading", { name: /^Pengajuan #[0-9]+$/ }).waitFor();
}
async function decisionReview(page, sourceName, reason, action) {
  await selectProposal(page, sourceName, reason);
  await page.getByLabel("Tindakan", { exact: true }).selectOption(action);
  await page.getByLabel("Acuan peninjauan", { exact: true }).fill("Synthetic reviewed decision");
  await page.getByLabel("Alasan keputusan", { exact: true }).fill("Synthetic revision decision");
  await page.getByRole("button", { name: "Tinjau keputusan", exact: true }).click();
  await confirm(page, action);
}
async function signin(page, expectedActor) {
  await page.goto(origin.origin + "/inventory/sources/revisions");
  await page.getByRole("heading", { name: "Masuk untuk mengubah sumber stok", exact: true }).waitFor();
  await page.getByRole("link", { name: "Masuk kembali", exact: true }).click();
  await page.getByRole("button", { name: "Lanjutkan ke layanan akun", exact: true }).click();
  await page.waitForURL(origin.origin + "/inventory/sources/revisions");
  await page.getByLabel("Gudang dan kanal", { exact: true }).waitFor();
  const identity = await page.evaluate(async () => (await fetch("/api/v1/identity/session")).json());
  assert.equal(identity.account.userId, expectedActor);
}
async function logout(page, context) {
  await page.goto(origin.origin + "/identity/account");
  await page.getByRole("button", { name: "Keluar dari Unit Toko", exact: true }).click();
  await page.waitForURL(origin.origin + "/identity/login?loggedOut=1");
  assert.equal((await context.cookies(origin.origin)).some(cookie => cookie.name === "__Host-ut_session"), false);
}
async function recover(page, operation, before) {
  await page.getByRole("button", { name: "Periksa hasil", exact: true }).waitFor();
  await page.waitForFunction(() => Array.from(document.querySelectorAll("button")).some(item => item.textContent === "Periksa hasil" && !item.disabled));
  const entry = (await pending(page))[0]; assert.deepEqual(Object.keys(entry).sort(), ["actorId", "createdAt", "key", "operation"]);
  assert.equal(entry.operation, operation); assert.equal(writes.length, before + 1);
  assert.equal(await page.getByRole("button", { name: "Selesaikan dan tutup", exact: true }).count(), 0);
  await page.reload(); await page.getByLabel("Gudang dan kanal", { exact: true }).waitFor();
  assert.equal(await page.getByRole("button", { name: "Periksa hasil", exact: true }).isDisabled(), true);
  await select(page); await page.getByRole("button", { name: "Periksa hasil", exact: true }).click();
  await page.getByText("Permintaan telah tersimpan", { exact: false }).waitFor();
  assert.equal((await pending(page)).length, 0); assert.equal(writes.length, before + 1);
}
async function control(action) {
  const response = await fetch(new URL("/__fixture/revision/" + action, api), { method: "POST", headers: { "X-Fixture-Control": config.controlKey } });
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
  page.on("pageerror", error => failures.push(error.message));
  page.on("dialog", dialog => dialog.accept());
  await check("revision sign-in returns to the new task with protected cookies", async () => {
    await signin(page, config.actorId);
    for (const name of ["__Host-ut_session", "__Host-ut_csrf"]) {
      const cookie = (await context.cookies(origin.origin)).find(value => value.name === name);
      assert.ok(cookie); assert.equal(cookie.httpOnly, true); assert.equal(cookie.secure, true); assert.equal(cookie.path, "/");
    }
  });
  await check("keyboard-reviewed precise revision survives a lost response and reload without resubmission", async () => {
    await proposal(page, "Alokasi browser uji", "Alokasi versi dua", "Perubahan pertama");
    await page.screenshot({ path: resolve(screenshotDirectory, "revision-review-desktop.png"), fullPage: true });
    await page.getByRole("heading", { name: labels.propose, exact: true }).locator("..").screenshot({ path: resolve(screenshotDirectory, "revision-review-focused.png") });
    const before = writes.length; faults.dropNextWrite = true;
    await page.getByRole("button", { name: labels.propose, exact: true }).click();
    await recover(page, "stock.source.revision.propose", before);
  });
  await check("competing revision and maker withdrawal retain immutable history", async () => {
    await proposal(page, "Alokasi browser uji", "Pengajuan bersaing", "Versi pembanding");
    await page.getByRole("button", { name: labels.propose, exact: true }).click(); await saved(page);
    await proposal(page, "Alokasi browser uji", "Pengajuan ditahan", "Ditahan pengaju");
    await page.getByRole("button", { name: labels.propose, exact: true }).click(); await saved(page);
    await decisionReview(page, "Alokasi browser uji", "Ditahan pengaju", "withdraw");
    await page.getByRole("button", { name: labels.withdraw, exact: true }).click(); await saved(page);
  });
  const reviewerContext = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1280, height: 960 }, locale: "id-ID", timezoneId: "Asia/Jakarta" });
  const reviewer = await reviewerContext.newPage(); reviewer.setDefaultTimeout(20000);
  reviewer.on("pageerror", error => failures.push(error.message)); reviewer.on("dialog", dialog => dialog.accept());
  await check("separate reviewer performs real MFA round-trip back to revisions", async () => {
    await signin(reviewer, config.reviewerId);
    await reviewer.getByRole("link", { name: "Verifikasi untuk peninjauan", exact: true }).click();
    await reviewer.getByRole("button", { name: "Verifikasi dua langkah", exact: true }).click();
    await reviewer.waitForURL(origin.origin + "/inventory/sources/revisions");
    await reviewer.getByLabel("Gudang dan kanal", { exact: true }).waitFor();
    assert.equal(await reviewer.getByRole("heading", { name: "Ajukan perubahan pengaturan", exact: true }).count(), 0);
  });
  await check("independent approval recovers its own lost receipt and applies version two once", async () => {
    await decisionReview(reviewer, "Alokasi browser uji", "Perubahan pertama", "approve");
    const before = writes.length; faults.dropNextWrite = true;
    await reviewer.getByRole("button", { name: labels.approve, exact: true }).click();
    await recover(reviewer, "stock.source.revision.approve", before);
    await choose(reviewer, "Alokasi versi dua");
    await reviewer.getByText("Pengaturan saat ini · versi", { exact: false }).waitFor();
  });
  await check("stale competing revision is visibly blocked and the server rejects approval", async () => {
    await selectProposal(reviewer, "Alokasi versi dua", "Versi pembanding");
    await reviewer.getByText("Dasar pengajuan sudah berbeda", { exact: false }).waitFor();
    assert.equal(await reviewer.getByLabel("Tindakan", { exact: true }).locator('option[value="approve"]').evaluate(item => item.disabled), true);
    assert.equal(await reviewer.getByLabel("Tindakan", { exact: true }).inputValue(), "reject");
    const id = (await reviewer.getByRole("heading", { name: /^Pengajuan #[0-9]+$/ }).innerText()).split("#")[1];
    const status = await reviewer.evaluate(async ({ id, actorId }) => {
      const csrf = await (await fetch("/api/v1/identity/csrf")).json();
      return (await fetch("/api/v1/inventory/sources/revisions/" + id + "/approve", { method: "POST",
        headers: { "Content-Type": "application/json", "X-CSRF-Token": csrf.requestToken, "X-Expected-Actor-Id": actorId, "If-Match": '"1"', "Idempotency-Key": crypto.randomUUID() },
        body: JSON.stringify({ reviewReference: "Synthetic stale probe", reason: "Synthetic stale probe" }) })).status;
    }, { id, actorId: config.reviewerId });
    assert.equal(status, 409);
    await decisionReview(reviewer, "Alokasi versi dua", "Versi pembanding", "reject");
    await reviewer.getByRole("button", { name: labels.reject, exact: true }).click(); await saved(reviewer);
  });
  await check("second approved revision preserves original precision and the prior immutable decision", async () => {
    await proposal(page, "Alokasi versi dua", "Alokasi versi tiga", "Perubahan kedua");
    await page.getByRole("button", { name: labels.propose, exact: true }).click(); await saved(page);
    await decisionReview(reviewer, "Alokasi versi dua", "Perubahan kedua", "approve");
    await reviewer.getByRole("button", { name: labels.approve, exact: true }).click(); await saved(reviewer);
    await reviewer.getByText("Versi pengaturan 3 pada alokasi", { exact: false }).waitFor();
    await selectProposal(reviewer, "Alokasi versi tiga", "Perubahan pertama");
    await reviewer.getByText("diterapkan sebagai versi 2", { exact: false }).waitFor();
    await reviewer.setViewportSize({ width: 390, height: 844 });
    assert.equal(await reviewer.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await reviewer.screenshot({ path: resolve(screenshotDirectory, "revision-history-mobile.png"), fullPage: true });
    await reviewer.getByRole("heading", { name: /^Pengajuan #[0-9]+$/ }).locator("..").screenshot({ path: resolve(screenshotDirectory, "revision-history-focused-mobile.png") });
    const text = await reviewer.locator("main").innerText();
    for (const hidden of ["auth0|", "personApproval", "private-person-", "bodyHash"]) assert.equal(text.includes(hidden), false);
    await reviewer.setViewportSize({ width: 1280, height: 960 });
  });
  await check("MFA expiring after frozen review denies approval without a stored change", async () => {
    await proposal(page, "Alokasi versi tiga", "Belum boleh diterapkan", "Verifikasi perlu baru");
    await page.getByRole("button", { name: labels.propose, exact: true }).click(); await saved(page);
    await decisionReview(reviewer, "Alokasi versi tiga", "Verifikasi perlu baru", "approve");
    await control("mfa");
    await reviewer.getByRole("button", { name: labels.approve, exact: true }).click();
    await reviewer.getByText("Lakukan verifikasi dua langkah terbaru", { exact: false }).waitFor();
    assert.equal((await pending(reviewer)).length, 0);
    assert.ok(protectedResponses.some(value => /\/revisions\/[0-9]+\/approve$/.test(value.path) && value.status === 403));
  });
  await check("revoked current grant conceals the old view while both accounts can sign out", async () => {
    await choose(reviewer, "Alokasi versi tiga");
    await reviewer.getByRole("button", { name: /^Verifikasi perlu baru / }).waitFor();
    await control("grant");
    await reviewer.getByRole("button", { name: "Muat ulang riwayat", exact: true }).click();
    await reviewer.getByRole("heading", { name: "Sesi dan akses perlu diperiksa kembali", exact: true }).waitFor();
    assert.equal(await reviewer.getByRole("heading", { name: "Alokasi versi tiga", exact: true }).count(), 0);
    await logout(reviewer, reviewerContext); await logout(page, context);
  });
  assert.deepEqual(failures, []);
  assert.equal(protectedResponses.filter(value => value.path.startsWith("/api/v1/")).every(value => value.noStore), true);
  assert.equal(faults.dropped, 2); assert.equal(checks.length, 9);
  console.log("REAL_SOURCE_REVISION_BROWSER_PASS " + JSON.stringify({ checks, droppedCommittedResponses: faults.dropped, realApiResponses: protectedResponses.length }));
} catch (error) {
  if (browser) for (const context of browser.contexts()) for (const page of context.pages()) {
    if (page.url().startsWith(origin.origin + "/inventory/sources")) {
      console.error("REVISION_FIXTURE_VIEW " + (await page.locator("main").innerText()).slice(0, 4000));
      console.error("REVISION_FIXTURE_FIELDS " + JSON.stringify(await page.locator("select").evaluateAll(items => items.map(item => ({ id: item.id, label: item.labels?.[0]?.textContent, disabled: item.disabled })))));
    }
  }
  console.error("REVISION_FIXTURE_RESPONSES " + JSON.stringify(protectedResponses.slice(-12)));
  console.error(error.stack ?? String(error));
  process.exitCode = 1;
} finally {
  if (browser) await browser.close();
  for (const socket of connections) socket.destroy();
  for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve));
  await app.close();
}
