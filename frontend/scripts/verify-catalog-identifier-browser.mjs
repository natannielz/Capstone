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
assert.match(config.proofPrefix, /^catalog-identifier-count-[a-f0-9]{32}$/);
const loadBrowser = createRequire(config.playwrightPackage);
const browserPackage = loadBrowser(config.playwrightPackage);
assert.equal(browserPackage.name, "playwright"); assert.equal(browserPackage.version, "1.62.1");
const { chromium } = loadBrowser("playwright");
const browserChannel = process.env.UNITTOKO_BROWSER_CHANNEL ?? "chromium";
assert.ok(["chromium", "msedge"].includes(browserChannel));
const path = "/api/v1/inventory/stock-counts";
const location = { kind: "fulfillmentWarehouse", organizationId: "100", branchId: null, divisionId: null, shopId: "101", warehouseId: "102", channelId: "103" };
const writes = [], identifierWrites = [], scanReads = [], recoveries = [], protectedResponses = [], faults = { dropNextWrite: false, dropped: 0 };
const connections = new Set(), failures = [], checks = [];
const captures = [], catalogOwnerOptions = [], panelAccessibility = [], proofDirectory = join(process.cwd(), "..", "tmp", "production", "catalog-identifier-browser-results");
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
  let recording = null;
  if (write) {
    const observed = { key: req.headers["idempotency-key"], expectedActor: req.headers["x-expected-actor-id"], bodyHash: null };
    const hash = createHash("sha256"), body = []; writes.push(observed); recording = observed;
    req.on("data", chunk => {hash.update(chunk); body.push(chunk);}); req.on("end", () => { observed.bodyHash = hash.digest("hex"); observed.input = JSON.parse(Buffer.concat(body).toString("utf8")); });
  }
  const drop = write && faults.dropNextWrite;
  if (drop) faults.dropNextWrite = false;
  const upstream = http.request({ hostname: "127.0.0.1", port, path: req.url, method: req.method, headers }, response => {
    if (recording) {
      recording.status = response.statusCode; const body = [];
      response.on("data", chunk => body.push(chunk));
      response.on("end", () => {recording.result = JSON.parse(Buffer.concat(body).toString("utf8"));});
    }
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
  await page.getByRole("region", {name: "Periksa kode barang", exact: true}).waitFor();
}
async function capture(page, state) {
  await mkdir(proofDirectory, { recursive: true });
  for (const [name, width, height] of [["desktop", 1440, 1000], ["mobile", 360, 800]]) {
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

const panel = page => page.getByRole("region", {name: "Kode barang dan GTIN", exact: true});
const scanPanel = page => page.getByRole("region", {name: "Periksa kode barang", exact: true});
async function intercept(context) {
  await context.route("**/api/v1/**", async route => {
    const request = route.request(), target = new URL(request.url()).pathname;
    const identifier = target.startsWith('/api/v1/catalog/administration/') && !!request.headers()['idempotency-key'];
    const scan = target === '/api/v1/catalog/scans/query', recovery = target === path + '/operations/query';
    if (!identifier && !scan && !recovery) return route.continue();
    try {
      const response = await route.fetch({timeout: 30000});
      const observed = {path: target, status: response.status(), actor: request.headers()['x-expected-actor-id'],
        key: request.headers()['idempotency-key'], revision: request.headers()['if-match'], input: request.postDataJSON(), result: await response.json()};
      (identifier ? identifierWrites : scan ? scanReads : recoveries).push(observed); await route.fulfill({response});
    } catch (error) {failures.push(String(error)); await route.abort('failed');}
  });
}
async function catalog(page) {
  await page.goto(origin.origin + '/administration/catalog');
  const owner = page.getByLabel('Toko atau gudang pemilik katalog', {exact: true});
  await owner.locator('option').nth(1).waitFor({state: 'attached'});
  const options = await owner.locator('option').evaluateAll(rows => rows.filter(row => row.value).map(row => ({value: row.value, label: row.textContent})));
  const expectedShop = {...location, kind: 'fulfillmentShop', warehouseId: null};
  // The genuine shop CatalogManage grant also covers its warehouse. Bind both exact fixture scopes,
  // then choose the authored shop by identity, never an option index or a weakened minimum count.
  assert.deepEqual(options.map(row => JSON.parse(row.value)).sort((a, b) => a.kind.localeCompare(b.kind)), [expectedShop, location]);
  const selected = options.filter(row => JSON.parse(row.value).kind === 'fulfillmentShop');
  assert.equal(selected.length, 1); assert.equal(selected[0].label, 'Organisasi 100 / Toko 101 / Kanal 103');
  catalogOwnerOptions.push(options.map(row => ({owner: JSON.parse(row.value), label: row.label})));
  await owner.selectOption(selected[0].value); assert.deepEqual(JSON.parse(await owner.inputValue()), expectedShop);
  await page.getByRole('button').filter({hasText: 'Pensil browser'}).click();
  await page.getByRole('button', {name: 'Kode dan GTIN ' + config.originalSkuCode, exact: true}).click();
  await panel(page).getByRole('button', {name: 'Koreksi kode utama', exact: true}).waitFor();
  const snapshot = await panel(page).ariaSnapshot();
  panelAccessibility.push(snapshot); await mkdir(proofDirectory, {recursive: true});
  await writeFile(join(proofDirectory, config.proofPrefix + '-panel-' + panelAccessibility.length + '.yml'), snapshot);
}
async function identifierReview(page, reference, reason) {
  await panel(page).getByLabel('Referensi asal kode', {exact: true}).fill(reference);
  await panel(page).getByLabel('Alasan pencatatan kode', {exact: true}).fill(reason);
  await panel(page).getByRole('button', {name: 'Tinjau pencatatan kode', exact: true}).click();
  await panel(page).getByRole('region', {name: 'Konfirmasi pencatatan kode', exact: true}).waitFor();
}
async function saveIdentifier(page) {
  const before = identifierWrites.length;
  await panel(page).getByRole('button', {name: 'Simpan kode', exact: true}).click();
  await panel(page).waitFor({state: 'hidden'});
  assert.equal(identifierWrites.length, before + 1); const write = identifierWrites.at(-1);
  assert.equal(write.status, 200); assert.equal(write.actor, config.actorId); assert.ok(write.key);
  assert.equal(write.result.skuId, config.skuId); return write;
}
async function scan(page, kind, raw, expectedKind = 'match') {
  const section = scanPanel(page), before = scanReads.length;
  await section.getByLabel('Jenis kode', {exact: true}).selectOption(kind);
  await section.getByLabel('Kode hasil pindai', {exact: true}).fill(raw);
  await section.getByRole('button', {name: 'Periksa kode', exact: true}).click();
  if (expectedKind === 'match') await section.getByRole('button', {name: 'Konfirmasi kemasan hasil pindai', exact: true}).waitFor();
  else await section.getByRole('alert').waitFor();
  assert.equal(scanReads.length, before + 1); const response = scanReads.at(-1);
  assert.equal(response.status, 200); assert.equal(response.result.kind, expectedKind);
  assert.equal(response.input.rawCode, raw); assert.equal(response.input.codeKind, kind);
  assert.equal(writes.length, expectedKind === 'match' ? 0 : 1);
  // A scan alone cannot select a package or add a quantity.
  assert.equal(await page.getByLabel('Jumlah kemasan yang ditemukan', {exact: true}).count(), 0);
  if (expectedKind !== 'match') {assert.equal(response.result.candidates.length, 0); return response;}
  const candidate = response.result.candidates[0]; assert.equal(response.result.candidates.length, 1);
  assert.equal(candidate.skuId, config.skuId); assert.equal(candidate.definitionId, config.definitionId);
  assert.equal(candidate.skuCode, config.originalSkuCode); assert.equal(candidate.currentPrimaryCode, config.correctedSkuCode);
  assert.equal(candidate.baseUnitsPerPackage, 12); assert.equal(candidate.selection.source, null);
  await section.getByRole('button', {name: 'Konfirmasi kemasan hasil pindai', exact: true}).click();
  const quantity = page.getByLabel('Jumlah kemasan yang ditemukan', {exact: true}); await quantity.waitFor();
  assert.equal(await quantity.inputValue(), ''); await quantity.focus(); assert.equal(await quantity.evaluate(element => document.activeElement === element), true);
  return response;
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
  let context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1280, height: 960 }, locale: "id-ID", timezoneId: "Asia/Jakarta" });
  let page = await context.newPage(); page.setDefaultTimeout(20000);
  page.on("pageerror", error => failures.push(error.message));
  page.on("dialog", dialog => dialog.accept());
  await check("real sign-in callback delivers the protected browser session", async () => {
    await page.goto(origin.origin + "/inventory/counts");
    // Signed-out visitors are redirected to the sign-in page by the proxy (G-012).
    await page.waitForURL(origin.origin + "/identity/login?returnPath=%2Finventory%2Fcounts");
    await page.getByRole("heading", { name: "Masuk ke Unit Toko" }).waitFor();
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
  await intercept(context);
  await check('catalog UI assigns a supplied GTIN with exact package and explicit confirmation', async () => {
    await catalog(page);
    const kind = panel(page).getByRole('combobox', {name: /^Jenis kode/});
    assert.equal(await kind.count(), 1);
    assert.deepEqual(await kind.locator('option').evaluateAll(rows => rows.map(row => row.value)), ['skuCode', 'gtin']);
    await kind.selectOption('gtin'); assert.equal(await kind.inputValue(), 'gtin');
    await panel(page).getByLabel('GTIN dari kemasan', {exact: true}).fill(config.rawGtin);
    await identifierReview(page, 'Synthetic checked package label', 'Synthetic GTIN browser assignment');
    assert.equal(identifierWrites.length, 0); await panel(page).getByText('Nilai pembanding: ' + config.canonicalGtin, {exact: true}).waitFor();
    await capture(page, 'gtin-review'); const write = await saveIdentifier(page);
    assert.equal(write.revision, '"1"'); assert.equal(write.input.kind, 'gtin'); assert.equal(write.input.rawValue, config.rawGtin);
    assert.equal(write.input.definitionId, config.definitionId); assert.equal(write.result.skuRevision, '2');
  });
  await check('corrected primary preserves the stable original SKU and its explicit alias', async () => {
    await catalog(page); await panel(page).getByRole('button', {name: 'Koreksi kode utama', exact: true}).click();
    await panel(page).getByLabel('Kode utama baru', {exact: true}).fill(config.correctedSkuCode);
    await identifierReview(page, 'Synthetic reviewed primary correction', 'Synthetic primary code browser correction');
    const write = await saveIdentifier(page); assert.equal(write.revision, '"2"'); assert.equal(write.input.newCode, config.correctedSkuCode);
    assert.equal(write.result.skuRevision, '3'); await catalog(page);
    await panel(page).getByText('SKU tetap untuk riwayat: ' + config.originalSkuCode + '. Koreksi kode utama tidak mengganti identitas barang atau kode pada transaksi yang telah dicatat.', {exact: true}).waitFor();
    await panel(page).getByRole('button', {name: 'Hentikan kode ' + config.originalSkuCode, exact: true}).waitFor();
    await capture(page, 'primary-and-alias');
  });
  await check('three real identifier forms resolve one exact package and the explicit GTIN count carries its proof', async () => {
    await page.goto(origin.origin + '/inventory/counts'); await select(page);
    const alias = await scan(page, 'sku', config.originalSkuCode);
    assert.equal(alias.result.candidates[0].selection.identifier.canonicalValue, config.originalSkuCode);
    const primary = await scan(page, 'sku', config.correctedSkuCode);
    assert.equal(primary.result.candidates[0].selection.identifier.canonicalValue, config.correctedSkuCode);
    const gtin = await scan(page, 'gtin', config.rawGtin);
    assert.equal(gtin.result.candidates[0].selection.identifier.canonicalValue, config.canonicalGtin);
    await review(page, '2', 'Synthetic identifier-bound physical observation');
    await capture(page, 'scanned-count-review'); faults.dropNextWrite = true;
    await page.getByRole('button', {name: 'Simpan pengamatan', exact: true}).click();
    await page.getByRole('button', {name: 'Periksa ulang permintaan yang sama', exact: true}).waitFor();
    assert.equal(faults.dropped, 1); assert.equal(writes.length, 1);
    const write = writes[0]; assert.equal(write.status, 200); assert.equal(write.expectedActor, config.actorId);
    assert.equal(write.input.packages, 2); assert.equal(write.input.skuId, config.skuId); assert.equal(write.input.expectedSkuRevision, '3');
    assert.equal(write.input.expectedDefinitionId, config.definitionId);
    assert.deepEqual(write.input.scanSelections, [gtin.result.candidates[0].selection]);
    const refs = await pending(page); assert.equal(refs.length, 1); assert.equal(refs[0].key, write.key);
    assert.deepEqual(Object.keys(refs[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  });
  await check('closing the browser preserves only the original operation reference and recovers one committed count', async () => {
    const refs = await pending(page), storageState = await context.storageState(), oldPage = page;
    await browser.close(); assert.equal(oldPage.isClosed(), true);
    browser = await chromium.launch({ ...(browserChannel === 'msedge' ? {channel: 'msedge'} : {}), headless: true, proxy: {server: 'http://127.0.0.1:' + proxyPort} });
    context = await browser.newContext({ignoreHTTPSErrors: true, storageState, viewport: {width: 360, height: 800}, locale: 'id-ID', timezoneId: 'Asia/Jakarta'});
    page = await context.newPage(); page.setDefaultTimeout(20000); page.on('pageerror', error => failures.push(error.message)); await intercept(context);
    await page.goto(origin.origin + '/inventory/counts'); assert.equal(await page.evaluate(() => sessionStorage.length), 0);
    await page.getByRole('button', {name: 'Periksa hasil', exact: true}).waitFor(); assert.deepEqual(await pending(page), refs);
    await page.getByRole('button', {name: 'Periksa hasil', exact: true}).click();
    await page.getByText('Permintaan telah tersimpan', {exact: false}).waitFor();
    assert.deepEqual(await pending(page), []); assert.equal(writes.length, 1); assert.equal(recoveries.length, 1);
    assert.equal(recoveries[0].status, 200); assert.equal(recoveries[0].input.key, writes[0].key);
    assert.equal(recoveries[0].actor, config.actorId);
    await page.getByRole('button', {name: 'Riwayat pengamatan', exact: true}).click();
    await page.getByLabel('Gudang dan kanal', {exact: true}).selectOption({label: 'Koperasi uji / Toko Tebet uji / Gudang utama uji / OMI uji'});
    await page.getByText('Alasan: Synthetic identifier-bound physical observation', {exact: true}).waitFor();
    await page.getByText('2 Dus = 24 piece', {exact: true}).waitFor(); await capture(page, 'recovered-count');
  });
  await check('retired alias and unrelated valid GTIN cannot select a package or write another count', async () => {
    await catalog(page); await panel(page).getByRole('button', {name: 'Hentikan kode ' + config.originalSkuCode, exact: true}).click();
    await identifierReview(page, 'Synthetic alias retirement review', 'Synthetic browser alias retirement');
    const write = await saveIdentifier(page); assert.equal(write.revision, '"3"'); assert.equal(write.result.skuRevision, '4');
    await page.goto(origin.origin + '/inventory/counts'); await select(page);
    await scan(page, 'sku', config.originalSkuCode, 'unknown'); await scan(page, 'gtin', '00012345600012', 'unknown');
    assert.equal(await page.getByRole('button', {name: 'Tinjau hitungan', exact: true}).count(), 0);
    assert.equal(writes.length, 1); assert.equal(identifierWrites.length, 3); assert.deepEqual(await pending(page), []);
    await capture(page, 'unknown-code');
  });
  assert.deepEqual(failures, []);
  assert.equal(protectedResponses.filter(response => response.path.startsWith("/api/v1/")).every(response => response.noStore), true);
  assert.equal(checks.length, 6);
  await writeFile(join(proofDirectory, config.proofPrefix + "-proof.json"), JSON.stringify({
    recordedUtc: new Date().toISOString(), checks, captures, catalogOwnerOptions, panelAccessibility, poolId: config.poolId,
    originalKeyAndBodyPreserved: true, browserRestart: true, identifierWrites: identifierWrites.length, scanSelections: writes[0].input.scanSelections, droppedCommittedResponses: faults.dropped, realApiResponses: protectedResponses.length,
    browserApiMongoJourney: true, syntheticProviderAndAccounts: true, stockRelease: false,
    physicalCustodyClosure: false, stagingValidated: false, productionAccepted: false,
  }, null, 2));
  console.log("REAL_IDENTIFIER_COUNT_BROWSER_PASS " + JSON.stringify({ checks, droppedCommittedResponses: faults.dropped, realApiResponses: protectedResponses.length }));
} catch (error) {
  for (const [index, page] of (browser?.contexts().flatMap(context => context.pages()) ?? []).entries()) {
    try {
      await mkdir(proofDirectory, {recursive: true});
      await writeFile(join(proofDirectory, config.proofPrefix + '-failure-' + index + '.yml'), await page.locator('body').ariaSnapshot());
      await page.screenshot({path: join(proofDirectory, config.proofPrefix + '-failure-' + index + '.png'), fullPage: true});
    } catch (diagnosticError) { console.error('Failure diagnostics: ' + diagnosticError); }
  }
  console.error(error.stack ?? String(error));
  process.exitCode = 1;
} finally {
  if (browser) await browser.close();
  for (const socket of connections) socket.destroy();
  for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve));
  await app.close();
}
