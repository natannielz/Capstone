// Opt-in local synthetic fixture only. No external identity provider or production acceptance is claimed.
import assert from "node:assert/strict";
import http from "node:http";
import https from "node:https";
import net from "node:net";
import { createHash } from "node:crypto";
import { mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createRequire } from "node:module";
import next from "next";

const chunks = []; for await (const chunk of process.stdin) chunks.push(chunk);
const config = JSON.parse(Buffer.concat(chunks).toString("utf8"));
assert.equal(process.versions.node.split(".")[0], "24");
const api = new URL(config.apiAddress), origin = new URL(config.origin), issuer = new URL(config.issuer);
assert.equal(api.protocol, "http:"); assert.equal(api.hostname, "127.0.0.1"); assert.ok(Number(api.port) > 0);
assert.equal(api.pathname, "/"); assert.equal(api.username + api.password + api.search + api.hash, "");
assert.equal(origin.href, "https://portal.unit-toko.co.id/"); assert.equal(issuer.href, "https://login.unit-toko.co.id/");
for (const key of ["makerId", "reviewerId", "foreignId", "receiptId", "qualityAllocationId", "valuationAllocationId"]) assert.match(config[key], /^[1-9][0-9]{0,18}$/);
assert.equal(new Set([config.makerId, config.reviewerId, config.foreignId]).size, 3);
assert.equal(config.policy, "remaining-proportional-minor-unit-floor-v1"); assert.match(config.proofPrefix, /^stock-release-[a-f0-9]{32}$/);
assert.equal(typeof config.controlKey, "string"); assert.ok(config.controlKey.length > 0 && config.controlKey.length <= 256);
assert.equal(typeof config.buildId, "string"); assert.ok(config.buildId.length > 0 && config.buildId.length < 100);
const loadBrowser = createRequire(config.playwrightPackage), packageInfo = loadBrowser(config.playwrightPackage);
assert.equal(packageInfo.name, "playwright"); assert.equal(packageInfo.version, "1.62.1");
const { chromium } = loadBrowser("playwright"), browserChannel = process.env.UNITTOKO_BROWSER_CHANNEL ?? "chromium";
assert.ok(["chromium", "msedge"].includes(browserChannel));
const route = "/inventory/stock-releases", apiPath = "/api/v1/inventory/stock-releases";
const confirmation = "Saya telah memeriksa kemasan, lot, jumlah dan alasan pada ringkasan ini serta memahami tindakan yang dipilih.";
const location = { kind: "fulfillmentWarehouse", organizationId: "100", branchId: null, divisionId: null, shopId: "101", warehouseId: "102", channelId: "103" };
const writes = [], responses = [], inspections = [], failures = [], checks = [], captures = [], connections = new Set(), sessionTokens = new Set();
const faults = { dropNextWrite: false, dropped: 0 };
const privateFields = new Set(["source", "people", "personId", "actorId", "actorGrantVersion", "issuer", "subject", "auditId", "journalId", "bodyHash", "snapshotHash", "requestKey", "causeHash", "proposalHash", "phase", "qualityJoin", "invoice", "match", "receiptHash", "valuationAllocationHash", "qualityAllocationHash"]);
const proofDirectory = join(process.cwd(), "..", "tmp", "production", "stock-release-browser-results");
let browser, frontend, edge, tunnel, maker, reviewer, foreign, preview, proposalId, finalState;
const app = next({ dev: false, dir: process.cwd(), quiet: true });
function privateFieldsAbsent(value) {
  if (!value || typeof value !== "object") return;
  for (const [key, child] of Object.entries(value)) { assert.equal(privateFields.has(key), false, "private response field: " + key); privateFieldsAbsent(child); }
}
function track(server) { server.on("connection", socket => { connections.add(socket); socket.on("close", () => connections.delete(socket)); }); return server; }
async function listen(server) { await new Promise((resolve, reject) => { server.once("error", reject); server.listen(0, "127.0.0.1", resolve); }); return server.address().port; }
function forward(req, res, port, isApi) {
  const headers = { ...req.headers, host: "127.0.0.1:" + port };
  for (const name of Object.keys(headers)) if (name.startsWith("x-forwarded-") || name.startsWith("x-original-") || ["forwarded", "connection", "x-fixture-control"].includes(name)) delete headers[name];
  if (isApi) { headers["x-forwarded-for"] = req.socket.remoteAddress; headers["x-forwarded-host"] = origin.host; headers["x-forwarded-proto"] = "https"; }
  const write = isApi && req.method === "POST" && (req.url === apiPath + "/proposals" || new RegExp("^" + apiPath + "/proposals/[1-9][0-9]*/(?:approve|reject)$").test(req.url));
  let command;
  if (write) {
    command = { path: req.url, key: req.headers["idempotency-key"], expectedActor: req.headers["x-expected-actor-id"], version: req.headers["if-match"], rawBody: null, bodyHash: null, status: null };
    writes.push(command); const body = [], hash = createHash("sha256"); req.on("data", chunk => { body.push(chunk); hash.update(chunk); });
    req.on("end", () => { command.rawBody = Buffer.concat(body).toString("utf8"); command.bodyHash = hash.digest("hex"); });
  }
  const drop = write && faults.dropNextWrite; if (drop) faults.dropNextWrite = false;
  const upstream = http.request({ hostname: "127.0.0.1", port, path: req.url, method: req.method, headers }, response => {
    if (command) command.status = response.statusCode;
    if (isApi) {
      const observed = { path: req.url.split("?")[0], method: req.method, status: response.statusCode,
        noStore: response.headers["cache-control"]?.includes("no-store") === true, publicOnly: null };
      responses.push(observed);
      if (observed.path.startsWith(apiPath)) {
        let finish; inspections.push(new Promise(resolve => { finish = resolve; })); const body = []; let size = 0;
        const timer = setTimeout(() => { observed.publicOnly = false; response.destroy(); finish(); }, 20000);
        response.on("data", chunk => { size += chunk.length; if (size <= 131072) body.push(chunk); });
        response.on("end", () => { try { privateFieldsAbsent(JSON.parse(Buffer.concat(body).toString("utf8"))); observed.publicOnly = true; } catch { observed.publicOnly = false; } clearTimeout(timer); finish(); });
        const incomplete = () => { if (!response.readableEnded) observed.publicOnly = false; clearTimeout(timer); finish(); };
        response.once("error", incomplete); response.once("aborted", incomplete); response.once("close", incomplete);
      }
    }
    if (drop && response.statusCode === 200) {
      response.resume(); response.once("end", () => {
        faults.dropped++; res.writeHead(200, { "content-type": "application/json", "content-length": "64", "cache-control": "no-store" });
        res.flushHeaders(); res.write("{"); setTimeout(() => res.destroy(), 25);
      }); return;
    }
    const drain = () => { response.unpipe(res); response.resume(); }; if (res.destroyed) { drain(); return; } res.once("close", drain);
    res.writeHead(response.statusCode ?? 502, response.headers); response.pipe(res);
  });
  upstream.on("error", () => { if (!res.headersSent) res.writeHead(502); res.end(); }); req.on("aborted", () => upstream.destroy()); req.pipe(upstream);
}
async function control(action) {
  assert.ok(["maker", "reviewer", "foreign", "same-person", "distinct-people", "state"].includes(action));
  const response = await fetch(new URL("/__fixture/stock-release/" + action, api), { method: "POST", headers: { "X-Fixture-Control": config.controlKey }, redirect: "error" });
  assert.equal(response.status, action === "state" ? 200 : 204); if (action === "state") {
    const value = await response.json(); assert.match(value.durableSha256, /^[a-f0-9]{64}$/); assert.match(value.originalSha256, /^[a-f0-9]{64}$/); return value;
  }
}
async function check(name, action) { await action(); checks.push(name); console.log("PASS " + name); }
async function pending(page) { return page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith("unit-toko:pending:v1:")).map(key => JSON.parse(localStorage[key]))); }
async function query(page, actorId, suffix, body, options = {}) {
  const value = await page.evaluate(async ({ actorId, url, body, options }) => {
    const csrf = await (await fetch("/api/v1/identity/csrf", { credentials: "same-origin", cache: "no-store" })).json();
    const response = await fetch(url, { method: "POST", credentials: "same-origin", cache: "no-store", headers: { "Content-Type": "application/json", "X-Expected-Actor-Id": actorId,
      ...(!options.omitCsrf ? { "X-CSRF-Token": csrf.requestToken } : {}), ...options.headers }, body: options.rawBody ?? JSON.stringify(body) });
    return { status: response.status, body: await response.json(), noStore: response.headers.get("cache-control")?.includes("no-store") === true };
  }, { actorId, url: apiPath + suffix, body, options }); assert.equal(value.noStore, true); privateFieldsAbsent(value.body); return value;
}
async function signIn(account, actorId) {
  await control(account); const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1280, height: 960 }, locale: "id-ID", timezoneId: "Asia/Jakarta" });
  const page = await context.newPage(); page.setDefaultTimeout(20000); page.on("pageerror", error => failures.push(error.message)); page.on("dialog", dialog => dialog.accept());
  page.on('request', request => {
    const url = new URL(request.url());
    if (url.origin === origin.origin && url.pathname.startsWith(apiPath))
      console.log('RELEASE_BROWSER_REQUEST ' + JSON.stringify({ account, path: url.pathname, method: request.method() }));
  });
  page.on('requestfailed', request => {
    const url = new URL(request.url());
    if (url.origin === origin.origin && url.pathname.startsWith(apiPath))
      console.log('RELEASE_BROWSER_REQUEST_FAILED ' + JSON.stringify({ account, path: url.pathname, reason: request.failure()?.errorText }));
  });
  page.on("response", response => {
    const url = new URL(response.url());
    if (url.origin === origin.origin && (url.pathname.startsWith("/api/v1/") || url.pathname.startsWith("/auth/")))
      console.log("RELEASE_BROWSER_RESPONSE " + JSON.stringify({ account, path: url.pathname, status: response.status() }));
  });
  await page.goto(origin.origin + route); await page.getByRole("heading", { name: "Masuk untuk memeriksa pelepasan", exact: true }).waitFor();
  await page.getByRole("link", { name: "Masuk kembali", exact: true }).click(); await page.getByRole("button", { name: "Lanjutkan ke layanan akun", exact: true }).click();
  await page.waitForURL(origin.origin + route);
  try { await page.getByLabel("Gudang dan kanal", { exact: true }).waitFor(); }
  catch (error) {
    console.log("RELEASE_LOGIN_STATE " + JSON.stringify(await page.evaluate(() => ({ pathname: location.pathname,
      headings: [...document.querySelectorAll('h1,h2')].map(element => element.textContent?.trim()).slice(0,8),
      alerts: [...document.querySelectorAll('[role="alert"]')].map(element => element.textContent?.trim()).slice(0,4) }))));
    await capture(page, account + "-login-failure"); throw error;
  }
  const cookies = await context.cookies(origin.origin);
  for (const name of ["__Host-ut_session", "__Host-ut_csrf"]) {
    const cookie = cookies.find(value => value.name === name); assert.ok(cookie); assert.equal(cookie.secure, true); assert.equal(cookie.httpOnly, true);
    assert.equal(cookie.domain, origin.hostname); assert.equal(cookie.path, "/"); assert.equal(cookie.sameSite, name.endsWith("csrf") ? "Strict" : "Lax");
    if (name === "__Host-ut_session") { assert.equal(sessionTokens.has(cookie.value), false); sessionTokens.add(cookie.value); }
  }
  assert.equal(await page.evaluate(() => document.cookie.includes("__Host-ut_")), false);
  const session = await page.evaluate(async () => (await fetch("/api/v1/identity/session", { credentials: "same-origin", cache: "no-store" })).json());
  assert.equal(session.authenticated, true); assert.equal(session.account.userId, actorId); return { context, page };
}
async function warehouse(page, actorId) {
  const directory = await query(page, actorId, "/warehouses/query", { purpose: "stock-release-warehouses" }); assert.equal(directory.status, 200);
  const item = directory.body.items.find(value => value.location.organizationId === "100" && value.location.shopId === "101" && value.location.warehouseId === "102" && value.location.channelId === "103");
  assert.ok(item); const select = page.getByLabel("Gudang dan kanal", { exact: true }); assert.equal(await select.inputValue(), "");
  const sourceStarted = performance.now();
  const queried = actorId === config.makerId ? page.waitForResponse(response => response.url() === origin.origin + apiPath + "/sources/query" && response.request().method() === "POST", { timeout: 45000 }) : null;
  await select.selectOption({ label: [item.organizationName, item.shopName, item.warehouseName, item.channelName].join(" / ") });
  if (queried) {
    let response;
    try { response = await queried; }
    catch (error) {
      console.log('RELEASE_SOURCE_TIMEOUT_STATE ' + JSON.stringify(await page.evaluate(() => ({ pathname: location.pathname,
        headings: [...document.querySelectorAll('main h2')].map(element => element.textContent?.trim()),
        alerts: [...document.querySelectorAll('main [role="alert"]')].map(element => element.textContent?.trim()),
        selects: [...document.querySelectorAll('main select')].map(element => ({ label: element.getAttribute('aria-labelledby'), disabled: element.disabled, empty: element.value === '' })),
        pendingOperations: Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key]).operation) }))));
      await capture(page, 'source-timeout'); throw error;
    }
    assert.equal(response.status(), 200); const body = await response.json(); privateFieldsAbsent(body);
    console.log('RELEASE_SOURCE_READ_MS ' + Math.round(performance.now() - sourceStarted));
    console.log('RELEASE_SOURCE_SHAPE ' + JSON.stringify({ fields: Object.keys(body), unavailableCount: body.unavailableCount,
      items: body.items.map(item => ({ fields: Object.keys(item), acceptedPackages: item.acceptedPackages, valuedPackages: item.valuedPackages,
        releaseablePackages: item.releaseablePackages, capitalizedValue: item.capitalizedValue, expiresOn: item.expiresOn, policy: item.policy })) }));
    await page.locator('section[aria-busy="false"]').filter({ has: page.getByRole('heading', { name: 'Riwayat pengajuan pelepasan', exact: true }) }).waitFor();
    const alerts = await page.locator('main').getByRole('alert').allTextContents();
    if (alerts.length) console.log('RELEASE_WAREHOUSE_ALERTS ' + JSON.stringify(alerts));
    assert.equal(alerts.length, 0, 'Warehouse source/history reads must both finish without a conflict');
    return body;
  }
}
async function detail(page, id) {
  await page.getByRole("button", { name: "Periksa pengajuan #" + id, exact: true }).click(); await page.getByRole("heading", { name: "Pengajuan #" + id, exact: true }).waitFor();
}
async function capture(page, state) {
  await mkdir(proofDirectory, { recursive: true });
  for (const [device, width, height] of [["desktop", 1280, 960], ["mobile", 390, 844]]) {
    await page.setViewportSize({ width, height }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true);
    const path = join(proofDirectory, config.proofPrefix + "-" + state + "-" + device + ".png"), pixels = await page.screenshot({ path, fullPage: true });
    captures.push({ state, device, width, height, path, sha256: createHash("sha256").update(pixels).digest("hex") });
  }
  await page.setViewportSize({ width: 1280, height: 960 });
}
const successfulWrites = () => writes.filter(value => value.status === 200);
try {
  await app.prepare(); const handle = app.getRequestHandler();
  frontend = track(http.createServer((req, res) => { void handle(req, res).catch(() => { res.statusCode = 500; res.end(); }); })); const frontendPort = await listen(frontend);
  edge = track(https.createServer({ pfx: Buffer.from(config.pfx, "base64"), passphrase: config.password }, (req, res) => {
    if (req.headers.host === issuer.host && req.method === "GET") {
      const url = new URL(req.url, issuer); if (url.pathname === "/authorize" && url.searchParams.has("state")) {
        res.writeHead(302, { location: origin.origin + "/auth/callback?code=fixture-code&state=" + encodeURIComponent(url.searchParams.get("state")), "cache-control": "no-store" }); res.end(); return;
      }
    }
    if (req.headers.host !== origin.host || req.url.startsWith("/__fixture/")) { res.writeHead(403); res.end(); return; }
    const isApi = req.url === "/auth" || req.url.startsWith("/auth/") || req.url.startsWith("/api/v1/"); forward(req, res, isApi ? Number(api.port) : frontendPort, isApi);
  })); const tlsPort = await listen(edge);
  tunnel = track(http.createServer((_req, res) => { res.writeHead(403); res.end(); })); tunnel.on("connect", (req, socket, head) => {
    if (![origin.host + ":443", issuer.host + ":443"].includes(req.url)) { socket.end("HTTP/1.1 403 Forbidden\r\n\r\n"); return; }
    const upstream = net.connect(tlsPort, "127.0.0.1", () => { socket.write("HTTP/1.1 200 Connection Established\r\n\r\n"); if (head.length) upstream.write(head); socket.pipe(upstream); upstream.pipe(socket); });
    connections.add(upstream); upstream.on("close", () => connections.delete(upstream)); upstream.on("error", () => socket.destroy()); socket.on("error", () => upstream.destroy()); socket.on("close", () => upstream.destroy());
  }); const proxyPort = await listen(tunnel);
  browser = await chromium.launch({ ...(browserChannel === "msedge" ? { channel: "msedge" } : {}), headless: true, proxy: { server: "http://127.0.0.1:" + proxyPort } });
  const before = await control("state"); assert.deepEqual([before.proposalCount, before.decisionCount, before.movementCount, before.valueTransitionCount], [0, 0, 0, 0]);
  assert.equal(before.held, "3"); assert.equal(before.availablePackages, "0");
  await check("genuine maker cookie login and explicit actual warehouse source and quantity selection", async () => {
    maker = await signIn("maker", config.makerId); const sources = await warehouse(maker.page, config.makerId);
    const item = sources.items.find(value => value.receiptId === config.receiptId && value.qualityAllocationId === config.qualityAllocationId && value.valuationAllocationId === config.valuationAllocationId);
    assert.ok(item); assert.equal(item.releaseablePackages, "2"); const select = maker.page.getByLabel("Barang dan lot", { exact: true }); assert.equal(await select.inputValue(), "");
    await select.selectOption({ label: [item.productName, item.packagingLabel, "Lot " + item.lotReference, item.receiptReference].join(" / ") });
    const quantity = maker.page.getByLabel("Jumlah kemasan yang dilepas", { exact: true }); assert.equal(await quantity.inputValue(), ""); await quantity.fill("1");
    const request = maker.page.waitForResponse(value => value.url() === origin.origin + apiPath + "/preview/query" && value.request().method() === "POST");
    await maker.page.getByRole("button", { name: "Periksa stok sebelum dan sesudah", exact: true }).click(); const response = await request; assert.equal(response.status(), 200); preview = await response.json();
    privateFieldsAbsent(preview); assert.equal(preview.input.receiptId, config.receiptId); assert.equal(preview.input.qualityAllocationId, config.qualityAllocationId);
    assert.equal(preview.input.valuationAllocationId, config.valuationAllocationId); assert.equal(preview.input.packages, 1);
    assert.equal(preview.stockBefore.heldPackages, "3"); assert.equal(preview.stockAfter.heldPackages, "2"); assert.equal(preview.stockAfter.availablePackages, "1");
    await maker.page.getByLabel("Alasan pengajuan", { exact: true }).fill("Pelepasan satu kemasan untuk pemeriksaan browser lokal sintetis");
    await maker.page.getByRole("button", { name: "Tinjau pengajuan", exact: true }).click(); await maker.page.getByRole("heading", { name: "Konfirmasi pengajuan pelepasan", exact: true }).waitFor();
    assert.equal(await maker.page.getByRole("button", { name: "Konfirmasi dan simpan", exact: true }).isDisabled(), true); await capture(maker.page, "maker-confirmation");
  });
  await check("CSRF and expected actor guards refuse genuine requests without publishing", async () => {
    const body = { input: preview.input, expected: preview.expected, binding: preview.binding, reason: "Rejected synthetic browser guard" };
    const headers = { "Idempotency-Key": "release-browser-never-written", "If-Match": '"' + preview.expected.balanceRevision + '"' };
    const csrf = await query(maker.page, config.makerId, "/proposals", body, { omitCsrf: true, headers }); assert.equal(csrf.status, 403); assert.equal(csrf.body.code, "antiforgery_failed");
    const actor = await query(maker.page, config.reviewerId, "/proposals", body, { headers }); assert.equal(actor.status, 409); assert.equal(actor.body.code, "actor_changed");
    assert.equal((await control("state")).durableSha256, before.durableSha256);
  });
  await check("deliberate maker confirmation persists one proposal and self-person approval is denied", async () => {
    await maker.page.getByRole("checkbox", { name: confirmation, exact: true }).check(); await maker.page.getByRole("button", { name: "Konfirmasi dan simpan", exact: true }).click();
    await maker.page.getByRole("heading", { name: "Hasil pelepasan tercatat", exact: true }).waitFor(); assert.equal((await pending(maker.page)).length, 0);
    const state = await control("state"); assert.deepEqual([state.proposalCount, state.decisionCount], [1, 0]); proposalId = state.proposals[0].id;
    await maker.page.getByRole("button", { name: "Periksa stok dan riwayat terbaru", exact: true }).click(); await detail(maker.page, proposalId);
    await maker.page.getByRole("button", { name: "Periksa pelepasan untuk disetujui", exact: true }).click();
    await maker.page.getByRole("alert").filter({ hasText: "Peninjau harus orang yang berbeda" }).waitFor();
    const denied = await query(maker.page, config.makerId, "/proposals/" + proposalId + "/review/query", { purpose: "stock-release-review", action: "approve", policy: config.policy });
    assert.equal(denied.status, 403); assert.equal(denied.body.code, "independent_person_required");
  });
  await check("different genuine reviewer cookie checks same-person refusal then fresh stock before confirmation", async () => {
    reviewer = await signIn("reviewer", config.reviewerId); await control("same-person");
    const denied = await query(reviewer.page, config.reviewerId, "/proposals/" + proposalId + "/review/query", { purpose: "stock-release-review", action: "approve", policy: config.policy });
    assert.equal(denied.status, 403); assert.equal(denied.body.code, "independent_person_required"); await control("distinct-people");
    await warehouse(reviewer.page, config.reviewerId); await detail(reviewer.page, proposalId);
    const request = reviewer.page.waitForResponse(value => value.url() === origin.origin + apiPath + "/proposals/" + proposalId + "/review/query");
    await reviewer.page.getByRole("button", { name: "Periksa pelepasan untuk disetujui", exact: true }).click(); const response = await request; assert.equal(response.status(), 200); const review = await response.json();
    privateFieldsAbsent(review); assert.equal(review.stockBefore.revision, review.expected.balanceRevision); assert.equal(review.stockBefore.heldPackages, "3"); assert.equal(review.stockAfter.heldPackages, "2");
    await reviewer.page.getByRole("heading", { name: "Pemeriksaan stok terkini oleh peninjau", exact: true }).waitFor();
    await reviewer.page.getByLabel("Alasan keputusan", { exact: true }).fill("Peninjauan independen jumlah dan nilai browser lokal sintetis");
    await reviewer.page.getByRole("button", { name: "Tinjau keputusan", exact: true }).click(); await reviewer.page.getByRole("heading", { name: "Konfirmasi keputusan pelepasan", exact: true }).waitFor();
    assert.equal(await reviewer.page.getByRole("button", { name: "Konfirmasi dan simpan", exact: true }).isDisabled(), true); await capture(reviewer.page, "reviewer-confirmation");
  });
  await check("lost committed approval preserves original key and exact replay then UI reload recovers one stock value transfer", async () => {
    await reviewer.page.getByRole("checkbox", { name: confirmation, exact: true }).check(); const count = successfulWrites().length;
    faults.dropNextWrite = true; await reviewer.page.getByRole("button", { name: "Konfirmasi dan simpan", exact: true }).click();
    await reviewer.page.getByRole("heading", { name: "Hasil belum terkonfirmasi", exact: true }).waitFor(); assert.equal(faults.dropped, 1);
    const original = successfulWrites()[count]; assert.equal(original.path, apiPath + "/proposals/" + proposalId + "/approve"); assert.equal(original.expectedActor, config.reviewerId); assert.equal(original.version, '"1"');
    const entries = await pending(reviewer.page); assert.equal(entries.length, 1); assert.deepEqual(Object.keys(entries[0]).sort(), ["actorId", "createdAt", "key", "operation"]);
    assert.equal(entries[0].actorId, config.reviewerId); assert.equal(entries[0].key, original.key); assert.equal(entries[0].operation, "inventory.stock-release.decide");
    const committed = await control("state"); assert.deepEqual([committed.proposalCount, committed.decisionCount, committed.movementCount, committed.valueTransitionCount], [1, 1, 1, 1]);
    assert.equal(committed.held, "2"); assert.equal(committed.availablePackages, "1"); assert.equal(committed.availableValue, "11250.00"); assert.equal(committed.originalSha256, before.originalSha256);
    await capture(reviewer.page, "uncertain-approval");
    const replay = await query(reviewer.page, config.reviewerId, "/proposals/" + proposalId + "/approve", null, { rawBody: original.rawBody, headers: { "Idempotency-Key": original.key, "If-Match": original.version } });
    assert.equal(replay.status, 200); assert.equal(replay.body.status, "approved"); assert.equal(replay.body.transferredValue, "11250.00");
    const repeated = successfulWrites()[count + 1]; assert.equal(repeated.key, original.key); assert.equal(repeated.rawBody, original.rawBody); assert.equal(repeated.bodyHash, original.bodyHash);
    assert.deepEqual(await pending(reviewer.page), entries); assert.equal((await control("state")).durableSha256, committed.durableSha256);
    await reviewer.page.reload(); await reviewer.page.getByRole("button", { name: "Periksa hasil", exact: true }).click(); await reviewer.page.getByText("Permintaan telah tersimpan", { exact: false }).waitFor();
    assert.equal((await pending(reviewer.page)).length, 0); assert.equal(successfulWrites().length, count + 2);
    await warehouse(reviewer.page, config.reviewerId); await detail(reviewer.page, proposalId); await capture(reviewer.page, "recovered-approval");
    assert.equal((await control("state")).durableSha256, committed.durableSha256);
  });
  await check("maker refreshed actual approved history and finite remaining source choice", async () => {
    await maker.page.reload(); const remaining = await warehouse(maker.page, config.makerId); await detail(maker.page, proposalId);
    await maker.page.getByText("Pelepasan disetujui · 1 kemasan", { exact: true }).waitFor(); await capture(maker.page, "maker-approved-history");
    const item = remaining.items.find(value => value.receiptId === config.receiptId && value.qualityAllocationId === config.qualityAllocationId && value.valuationAllocationId === config.valuationAllocationId);
    assert.ok(item); assert.equal(item.releaseablePackages, "1");
  });
  await check("foreign genuine cookie scope and original actor receipts refuse with unchanged acquisition", async () => {
    foreign = await signIn("foreign", config.foreignId);
    for (const [suffix, body] of [["/sources/query", { purpose: "stock-release-sources", location }], ["/proposals/" + proposalId + "/query", { purpose: "stock-release-history" }]]) {
      const denied = await query(foreign.page, config.foreignId, suffix, body); assert.equal(denied.status, 403);
    }
    const command = successfulWrites().find(value => value.path === apiPath + "/proposals");
    const receipt = await query(foreign.page, config.foreignId, "/operations/query", { purpose: "operation-recovery", operation: "inventory.stock-release.propose", key: command.key });
    assert.equal(receipt.status, 200); assert.deepEqual(receipt.body, { state: "not_confirmed", result: null }); finalState = await control("state");
    assert.equal(finalState.originalSha256, before.originalSha256); assert.deepEqual([finalState.proposalCount, finalState.decisionCount, finalState.movementCount, finalState.valueTransitionCount], [1, 1, 1, 1]);
  });
  await Promise.all(inspections); assert.deepEqual(failures, []); assert.equal(checks.length, 7); assert.equal(captures.length, 10); assert.equal(sessionTokens.size, 3);
  const releaseResponses = responses.filter(value => value.path.startsWith(apiPath)); assert.ok(releaseResponses.length > 0); assert.equal(releaseResponses.every(value => value.noStore && value.publicOnly), true);
  assert.equal(successfulWrites().length, 3); assert.equal(new Set(successfulWrites().map(value => value.expectedActor + ":" + value.key)).size, 2);
  await writeFile(join(proofDirectory, config.proofPrefix + "-proof.json"), JSON.stringify({ recordedUtc: new Date().toISOString(), buildId: config.buildId, checks, captures,
    nodeMajor: "24", playwrightVersion: "1.62.1", browserChannel, genuineCookieLogins: 3, committedApprovalResponseLosses: faults.dropped,
    successfulCommandRequests: 3, distinctCommands: 2, proposalCount: 1, decisionCount: 1, movementCount: 1, valueTransitionCount: 1, heldBefore: "3", heldAfter: finalState.held,
    availableAfter: finalState.availablePackages, availableValue: finalState.availableValue, durableSha256: finalState.durableSha256,
    originalBeforeSha256: before.originalSha256, originalAfterSha256: finalState.originalSha256, originalKeyAndBodyPreserved: true,
    replayThroughGenuineBrowserFetch: true, recoveryThroughUi: true, noDuplicateStockOrValueTransfer: true, originalAcquisitionJournalPayableUnchanged: true,
    localSyntheticOnly: true, syntheticSignedOidcProvider: true, externalProviderAccepted: false, stagingValidated: false, productionAccepted: false }, null, 2));
  console.log("LOCAL_SYNTHETIC_STOCK_RELEASE_BROWSER_PASS " + JSON.stringify({ checks, captures: captures.length, proposals: 1, decisions: 1, movements: 1, valueTransitions: 1, syntheticLocalOnly: true }));
} catch (error) { console.error(error.stack ?? String(error)); process.exitCode = 1; }
finally { if (browser) await browser.close(); for (const socket of connections) socket.destroy(); for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve)); await app.close(); }
