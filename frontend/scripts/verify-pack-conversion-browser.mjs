// Opt-in local synthetic fixture. Never imported by the application or used as a deployment proxy.
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
assert.equal(api.pathname, "/"); assert.equal(api.username + api.password + api.search + api.hash, "");
assert.equal(origin.href, "https://portal.unit-toko.co.id/"); assert.equal(issuer.href, "https://login.unit-toko.co.id/");
for (const name of ["makerId", "reviewerId", "foreignId", "sourceSkuId", "destinationSkuId", "sourceDefinitionId", "destinationDefinitionId"])
  assert.match(config[name], /^[1-9][0-9]{0,18}$/);
assert.equal(new Set([config.makerId, config.reviewerId, config.foreignId]).size, 3);
assert.equal(typeof config.controlKey, "string"); assert.ok(config.controlKey.length > 0 && config.controlKey.length <= 256);
assert.equal(config.warehouseLabel, "Koperasi uji / Toko Tebet uji / Gudang utama uji / OMI uji");
assert.match(config.proofPrefix, /^pack-conversion-rule-[a-f0-9]{32}$/);
const loadBrowser = createRequire(config.playwrightPackage), browserPackage = loadBrowser(config.playwrightPackage);
assert.equal(browserPackage.name, "playwright"); assert.equal(browserPackage.version, "1.62.1");
const { chromium } = loadBrowser("playwright"), browserChannel = process.env.UNITTOKO_BROWSER_CHANNEL ?? "chromium";
assert.ok(["chromium", "msedge"].includes(browserChannel));
const route = "/inventory/pack-conversions", apiPath = "/api/v1/inventory/pack-conversions";
const confirmation = "Saya telah memeriksa arah dan rasio. Saya memahami bahwa persetujuan aturan tidak memindahkan stok atau nilai barang.";
const writes = [], protectedResponses = [], faults = { dropNextWrite: false, dropped: 0 };
const connections = new Set(), failures = [], checks = [], captures = [], sessionTokens = new Set();
const responseInspections = [];
const proofDirectory = join(process.cwd(), "..", "tmp", "production", "pack-conversion-browser-results");
const privateFields = new Set(["people", "personId", "actorId", "actorGrantVersion", "issuer", "subject", "auditId", "bodyHash", "snapshotHash", "requestKey", "authoringOwner", "physicalItemKey", "catalog"]);
let browser, frontend, edge, tunnel, maker, reviewer, foreign, preview, proposalId, finalState;
const app = next({ dev: false, dir: process.cwd(), quiet: true });
function privateFieldsAbsent(value) {
  if (!value || typeof value !== "object") return;
  for (const [key, child] of Object.entries(value)) { assert.equal(privateFields.has(key), false, "private response field: " + key); privateFieldsAbsent(child); }
}
function track(server) {
  server.on("connection", socket => { connections.add(socket); socket.on("close", () => connections.delete(socket)); }); return server;
}
async function listen(server) {
  await new Promise((resolve, reject) => { server.once("error", reject); server.listen(0, "127.0.0.1", resolve); }); return server.address().port;
}
function forward(req, res, port, isApi) {
  const headers = { ...req.headers, host: "127.0.0.1:" + port };
  for (const name of Object.keys(headers))
    if (name.startsWith("x-forwarded-") || name.startsWith("x-original-") || name === "forwarded" || name === "connection" || name === "x-fixture-control") delete headers[name];
  if (isApi) {
    // One exact trusted hop, based on the local listener, never on inbound forwarding headers.
    headers["x-forwarded-for"] = req.socket.remoteAddress; headers["x-forwarded-host"] = origin.host; headers["x-forwarded-proto"] = "https";
  }
  const write = isApi && req.method === "POST" && (req.url === apiPath + "/proposals" || new RegExp("^" + apiPath + "/proposals/[1-9][0-9]*/(?:approve|reject)$").test(req.url));
  let command;
  if (write) {
    command = { path: req.url, key: req.headers["idempotency-key"], expectedActor: req.headers["x-expected-actor-id"], version: req.headers["if-match"], bodyHash: null, rawBody: null, status: null };
    const body = [], hash = createHash("sha256"); writes.push(command);
    req.on("data", chunk => { body.push(chunk); hash.update(chunk); });
    req.on("end", () => { command.bodyHash = hash.digest("hex"); command.rawBody = Buffer.concat(body).toString("utf8"); });
  }
  const drop = write && faults.dropNextWrite;
  if (drop) faults.dropNextWrite = false;
  const upstream = http.request({ hostname: "127.0.0.1", port, path: req.url, method: req.method, headers }, response => {
    if (command) command.status = response.statusCode;
    if (isApi) {
      const observed = { path: req.url.split("?")[0], method: req.method, status: response.statusCode,
        noStore: response.headers["cache-control"]?.includes("no-store") === true,
        origin: req.headers.origin ?? null, fetchSite: req.headers["sec-fetch-site"] ?? null,
        sessionCookiePresent: (req.headers.cookie ?? "").includes("__Host-ut_session=") };
      protectedResponses.push(observed);
      if (observed.path.startsWith(apiPath)) {
        let finishInspection;
        responseInspections.push(new Promise(resolve => { finishInspection = resolve; }));
        const timeout = setTimeout(() => {
          observed.publicOnly = false; observed.inspectionError = 'upstream_body_timeout';
          response.destroy(); finishInspection();
        }, 20000);
        const body = []; let length = 0;
        response.on("data", chunk => { length += chunk.length; if (length <= 131072) body.push(chunk); });
        response.on("end", () => {
          try { const value = JSON.parse(Buffer.concat(body).toString("utf8")); privateFieldsAbsent(value); observed.publicOnly = true; observed.code = value.code ?? null; }
          catch { observed.publicOnly = false; }
          clearTimeout(timeout);
          finishInspection();
        });
        const incomplete = () => {
          if (!response.readableEnded) { observed.publicOnly = false; observed.inspectionError = 'upstream_body_incomplete'; }
          clearTimeout(timeout); finishInspection();
        };
        response.once('error', incomplete); response.once('aborted', incomplete); response.once('close', incomplete);
      }
    }
    if (drop && response.statusCode === 200) {
      // Lose delivery only after consuming the actual committed API response.
      response.resume(); response.once("end", () => {
        faults.dropped++; res.writeHead(200, { "content-type": "application/json", "content-length": "64", "cache-control": "no-store" });
        res.flushHeaders(); res.write("{"); setTimeout(() => res.destroy(), 25);
      }); return;
    }
    const drain = () => { response.unpipe(res); response.resume(); };
    if (res.destroyed) { drain(); return; }
    res.once('close', drain);
    res.writeHead(response.statusCode ?? 502, response.headers); response.pipe(res);
  });
  upstream.on("error", () => { if (!res.headersSent) res.writeHead(502); res.end(); });
  req.on("aborted", () => upstream.destroy()); req.pipe(upstream);
}
async function control(action) {
  assert.ok(["maker", "reviewer", "foreign", "same-person", "distinct-people", "state"].includes(action));
  const response = await fetch(new URL("/__fixture/pack-conversion/" + action, api), { method: "POST", headers: { "X-Fixture-Control": config.controlKey }, redirect: "error" });
  assert.equal(response.status, action === "state" ? 200 : 204);
  if (action === "state") {
    const value = await response.json(); assert.match(value.durableSha256, /^[a-f0-9]{64}$/); assert.match(value.physicalSha256, /^[a-f0-9]{64}$/); return value;
  }
}
async function check(name, action) { await action(); checks.push(name); console.log("PASS " + name); }
async function pending(page) {
  return page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith("unit-toko:pending:v1:")).map(key => JSON.parse(localStorage[key])));
}
async function query(page, actorId, suffix, body, options = {}) {
  const result = await page.evaluate(async ({ actorId, path, body, options }) => {
    const csrf = await (await fetch("/api/v1/identity/csrf", { credentials: "same-origin", cache: "no-store" })).json();
    const response = await fetch(path, { method: "POST", credentials: "same-origin", cache: "no-store", redirect: "error",
      headers: { "Content-Type": "application/json", "X-Expected-Actor-Id": actorId,
        ...(!options.omitCsrf ? { "X-CSRF-Token": options.csrf ?? csrf.requestToken } : {}), ...options.headers },
      body: options.rawBody ?? JSON.stringify(body) });
    return { status: response.status, body: await response.json(), noStore: response.headers.get("cache-control")?.includes("no-store") === true };
  }, { actorId, path: apiPath + suffix, body, options });
  assert.equal(result.noStore, true); privateFieldsAbsent(result.body); return result;
}
async function signIn(account, actorId, workspace = true) {
  await control(account);
  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1280, height: 960 }, locale: "id-ID", timezoneId: "Asia/Jakarta" });
  const page = await context.newPage(); page.setDefaultTimeout(20000);
  page.on("pageerror", error => failures.push(error.message)); page.on("dialog", dialog => dialog.accept());
  await page.goto(origin.origin + route);
  await page.getByRole("heading", { name: "Masuk untuk menyiapkan aturan", exact: true }).waitFor();
  await page.getByRole("link", { name: "Masuk kembali", exact: true }).click();
  await page.getByRole("button", { name: "Lanjutkan ke layanan akun", exact: true }).click();
  await page.waitForURL(origin.origin + route);
  if (workspace) await page.getByLabel("Gudang dan kanal", { exact: true }).waitFor();
  const cookies = await context.cookies(origin.origin);
  for (const name of ["__Host-ut_session", "__Host-ut_csrf"]) {
    const cookie = cookies.find(value => value.name === name); assert.ok(cookie);
    assert.equal(cookie.secure, true); assert.equal(cookie.httpOnly, true); assert.equal(cookie.domain, origin.hostname); assert.equal(cookie.path, "/");
    assert.equal(cookie.sameSite, name.endsWith("csrf") ? "Strict" : "Lax");
    if (name === "__Host-ut_session") { assert.equal(sessionTokens.has(cookie.value), false); sessionTokens.add(cookie.value); }
  }
  assert.equal(cookies.some(cookie => cookie.name.includes("Correlation") || cookie.name.includes("Nonce")), false);
  assert.equal(await page.evaluate(() => document.cookie.includes("__Host-ut_")), false);
  const session = await page.evaluate(async () => (await fetch("/api/v1/identity/session", { credentials: "same-origin", cache: "no-store" })).json());
  assert.equal(session.authenticated, true); assert.equal(session.account.userId, actorId); return { context, page };
}
async function warehouse(page, empty = false) {
  const select = page.getByLabel("Gudang dan kanal", { exact: true });
  if (empty) assert.equal(await select.inputValue(), "");
  await select.selectOption({ label: config.warehouseLabel });
}
async function prepare(page, reason) {
  await warehouse(page);
  for (const [label, id] of [["Kemasan sumber tertutup", config.sourceSkuId], ["Kemasan tujuan satuan/lepas", config.destinationSkuId]]) {
    const select = page.getByLabel(label, { exact: true }); await select.locator('option[value="' + id + '"]').waitFor({ state: "attached" });
    assert.equal(await select.inputValue(), ""); await select.selectOption(id);
  }
  for (const [label, value] of [["Jumlah kemasan sumber per langkah", "1"], ["Jumlah kemasan tujuan per langkah", "12"]]) {
    const input = page.getByLabel(label, { exact: true }); assert.equal(await input.inputValue(), ""); await input.fill(value);
  }
  await page.getByLabel("Alasan", { exact: true }).fill(reason);
  const response = page.waitForResponse(value => value.url() === origin.origin + apiPath + "/rules/preview/query" && value.request().method() === "POST");
  await page.getByRole("button", { name: "Minta pratinjau aturan", exact: true }).click();
  const result = await response; assert.equal(result.status(), 200); const value = await result.json(); privateFieldsAbsent(value);
  assert.equal(value.source.packageKind, "sealed"); assert.equal(value.destination.packageKind, "loose");
  assert.equal(value.input.sourceSkuId, config.sourceSkuId); assert.equal(value.input.destinationSkuId, config.destinationSkuId);
  assert.equal(value.input.sourceDefinitionId, config.sourceDefinitionId); assert.equal(value.input.destinationDefinitionId, config.destinationDefinitionId);
  assert.equal(value.input.sourcePackagesPerStep, 1); assert.equal(value.input.destinationPackagesPerStep, 12);
  assert.equal(value.sourcePackages, "1"); assert.equal(value.destinationPackages, "12"); assert.equal(value.baseUnits, "12");
  assert.equal(value.physicalConversionAvailable, false);
  await page.getByRole("heading", { name: "Pratinjau aturan dari server", exact: true }).waitFor();
  await page.getByRole("button", { name: "Tinjau pengajuan", exact: true }).click();
  await page.getByRole("heading", { name: "Konfirmasi pengajuan aturan", exact: true }).waitFor();
  assert.equal(await page.getByRole("button", { name: "Konfirmasi pengajuan aturan", exact: true }).isDisabled(), true); return value;
}
async function proposal(page, id) {
  await page.getByRole("button", { name: "Pengajuan dan peninjauan", exact: true }).click();
  await page.getByRole("button", { name: new RegExp("^Pengajuan " + id + "\\b") }).click();
  await page.getByRole("heading", { name: "Pengajuan " + id, exact: true }).waitFor();
}
async function publicUi(page) {
  assert.equal((await page.locator("main").innerText()).includes("private-conversion-physical-item"), false);
  assert.equal(await page.getByRole("button", { name: /^(Jalankan konversi|Laksanakan konversi|Konversi fisik|Pindahkan stok|Aktifkan sumber)/i }).count(), 0);
}
async function capture(page, state) {
  await publicUi(page); await mkdir(proofDirectory, { recursive: true });
  for (const [name, width, height] of [["desktop", 1280, 960], ["mobile", 390, 844]]) {
    await page.setViewportSize({ width, height }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true);
    const path = join(proofDirectory, config.proofPrefix + "-" + state + "-" + name + ".png"), pixels = await page.screenshot({ path, fullPage: true });
    captures.push({ state, device: name, width, height, path, sha256: createHash("sha256").update(pixels).digest("hex") });
  }
  await page.setViewportSize({ width: 1280, height: 960 });
}
const successfulWrites = () => writes.filter(write => write.status === 200);
try {
  await app.prepare(); const handle = app.getRequestHandler();
  frontend = track(http.createServer((req, res) => { void handle(req, res).catch(() => { res.statusCode = 500; res.end(); }); }));
  const frontendPort = await listen(frontend);
  edge = track(https.createServer({ pfx: Buffer.from(config.pfx, "base64"), passphrase: config.password }, (req, res) => {
    if (req.headers.host === issuer.host && req.method === "GET") {
      const url = new URL(req.url, issuer);
      if (url.pathname === "/fixture-other-origin") {
        res.writeHead(200, { "content-type": "text/html; charset=utf-8", "cache-control": "no-store" }); res.end("<!doctype html><title>Synthetic other-origin page</title>"); return;
      }
      if (url.pathname === "/authorize" && url.searchParams.has("state")) {
        // The actual API still validates signed synthetic tokens, nonce, state, PKCE and assurance.
        res.writeHead(302, { location: origin.origin + "/auth/callback?code=fixture-code&state=" + encodeURIComponent(url.searchParams.get("state")), "cache-control": "no-store" }); res.end(); return;
      }
    }
    if (req.headers.host !== origin.host || req.url.startsWith("/__fixture/")) { res.writeHead(403); res.end(); return; }
    const isApi = req.url === "/auth" || req.url.startsWith("/auth/") || req.url.startsWith("/api/v1/");
    forward(req, res, isApi ? Number(api.port) : frontendPort, isApi);
  }));
  const tlsPort = await listen(edge);
  tunnel = track(http.createServer((_req, res) => { res.writeHead(403); res.end(); }));
  tunnel.on("connect", (req, socket, head) => {
    if (![origin.host + ":443", issuer.host + ":443"].includes(req.url)) { socket.end("HTTP/1.1 403 Forbidden\r\n\r\n"); return; }
    const upstream = net.connect(tlsPort, "127.0.0.1", () => {
      socket.write("HTTP/1.1 200 Connection Established\r\n\r\n"); if (head.length) upstream.write(head); socket.pipe(upstream); upstream.pipe(socket);
    });
    connections.add(upstream); upstream.on("close", () => connections.delete(upstream));
    upstream.on("error", () => socket.destroy()); socket.on("error", () => upstream.destroy()); socket.on("close", () => upstream.destroy());
  });
  const proxyPort = await listen(tunnel);
  browser = await chromium.launch({ ...(browserChannel === "msedge" ? { channel: "msedge" } : {}), headless: true, proxy: { server: "http://127.0.0.1:" + proxyPort } });
  const before = await control("state"); assert.deepEqual([before.proposalCount, before.decisionCount, before.ruleCount], [0, 0, 0]);
  await check("maker signs in through the actual protected cookie callback", async () => { maker = await signIn("maker", config.makerId); await warehouse(maker.page, true); });
  await check("maker explicitly selects warehouse sealed and loose packages and confirms the server ratio", async () => {
    preview = await prepare(maker.page, "Pengajuan aturan kemasan sintetis untuk pemeriksaan lokal"); await capture(maker.page, "maker-confirmation");
  });
  await check("actual browser CSRF actor origin and trusted forwarding guards refuse without a proposal", async () => {
    const body = { input: preview.input, binding: preview.binding, reason: "Synthetic rejected browser guard" };
    for (const [actor, options, status] of [[config.makerId, { omitCsrf: true }, 403], [config.makerId, { csrf: "invalid-csrf" }, 403], [config.reviewerId, {}, 409]])
      assert.equal((await query(maker.page, actor, "/proposals", body, { ...options, headers: { "Idempotency-Key": "browser-guard-" + status + "-" + actor } })).status, status);
    const guarded = await query(maker.page, config.makerId, "/warehouses/query", { purpose: "pack-conversion-rule-warehouses" }, {
      headers: { "X-Forwarded-Host": "outside.invalid", "X-Forwarded-Proto": "http", "X-Forwarded-For": "192.0.2.9", "Forwarded": "host=outside.invalid;proto=http" } });
    assert.equal(guarded.status, 200);
    const other = await maker.context.newPage();
    try {
      await other.goto(issuer.origin + "/fixture-other-origin"); const start = protectedResponses.length;
      await other.evaluate(async target => { await fetch(target, { method: "POST", mode: "no-cors", credentials: "include", headers: { "Content-Type": "text/plain" }, body: "{}" }); }, origin.origin + apiPath + "/proposals");
      const denied = protectedResponses.slice(start).find(value => value.path === apiPath + "/proposals");
      assert.ok(denied); assert.equal(denied.origin, issuer.origin); assert.equal(denied.fetchSite, "same-site");
      assert.equal(denied.sessionCookiePresent, true); assert.equal(denied.status, 403); assert.equal(denied.code, "untrusted_origin");
    } finally { await other.close(); }
    const state = await control("state"); assert.equal(state.durableSha256, before.durableSha256); assert.equal(state.physicalSha256, before.physicalSha256);
  });
  await check("confirmed maker proposal persists and the maker cannot review it", async () => {
    await maker.page.getByRole("checkbox", { name: confirmation, exact: true }).check();
    await maker.page.getByRole("button", { name: "Konfirmasi pengajuan aturan", exact: true }).click();
    await maker.page.getByRole("heading", { name: "Pengajuan aturan tersimpan", exact: true }).waitFor(); assert.equal((await pending(maker.page)).length, 0);
    const state = await control("state"); assert.deepEqual([state.proposalCount, state.decisionCount, state.ruleCount], [1, 0, 0]);
    assert.equal(state.proposals[0].status, "pending"); proposalId = state.proposals[0].id; assert.match(proposalId, /^[1-9][0-9]{0,18}$/);
    const written = successfulWrites()[0]; assert.equal(written.expectedActor, config.makerId); assert.equal(written.path, apiPath + "/proposals");
    assert.deepEqual(JSON.parse(written.rawBody).input, preview.input);
    await proposal(maker.page, proposalId);
    await maker.page.getByText("Akun ini belum memenuhi peninjauan independen. Pembuat dan akun lain milik orang yang sama tidak dapat memutuskan pengajuan ini.", { exact: true }).waitFor();
    assert.equal(await maker.page.getByRole("button", { name: "Pratinjau persetujuan", exact: true }).count(), 0);
    const own = await query(maker.page, config.makerId, "/proposals/" + proposalId + "/query", { purpose: "pack-conversion-rule-history" });
    assert.equal(own.status, 200); assert.equal(own.body.canReview, false);
    const denied = await query(maker.page, config.makerId, "/proposals/" + proposalId + "/review/query", { purpose: "pack-conversion-rule-review", action: "approve" });
    assert.equal(denied.status, 403); assert.equal(denied.body.code, "independent_person_required"); await publicUi(maker.page);
  });
  await check("a second genuine cookie account belonging to the same person cannot review", async () => {
    reviewer = await signIn("reviewer", config.reviewerId); await control("same-person");
    const detail = await query(reviewer.page, config.reviewerId, "/proposals/" + proposalId + "/query", { purpose: "pack-conversion-rule-history" });
    assert.equal(detail.status, 200, JSON.stringify({ code: detail.body?.code, title: detail.body?.title })); assert.equal(detail.body.canReview, false);
    const denied = await query(reviewer.page, config.reviewerId, "/proposals/" + proposalId + "/review/query", { purpose: "pack-conversion-rule-review", action: "approve" });
    assert.equal(denied.status, 403); assert.equal(denied.body.code, "independent_person_required"); await control("distinct-people");
  });
  await check("independent reviewer discovers pending canReview and confirms one approval", async () => {
    await warehouse(reviewer.page, true); await proposal(reviewer.page, proposalId);
    const list = await query(reviewer.page, config.reviewerId, "/proposals/query", { purpose: "pack-conversion-rule-history", location: preview.input.location });
    assert.equal(list.status, 200); assert.equal(list.body.items.length, 1); assert.equal(list.body.items[0].id, proposalId);
    assert.equal(list.body.items[0].status, "pending"); assert.equal(list.body.items[0].canReview, true);
    await reviewer.page.getByLabel("Alasan", { exact: true }).fill("Rasio sintetis diperiksa oleh peninjau orang independen");
    await reviewer.page.getByRole("button", { name: "Pratinjau persetujuan", exact: true }).click();
    await reviewer.page.getByText("Pratinjau server siap: persetujuan aturan. Keputusan belum disimpan.", { exact: true }).waitFor();
    await reviewer.page.getByRole("button", { name: "Tinjau keputusan", exact: true }).click();
    await reviewer.page.getByRole("heading", { name: "Konfirmasi persetujuan aturan", exact: true }).waitFor();
    assert.equal(await reviewer.page.getByRole("button", { name: "Konfirmasi persetujuan aturan", exact: true }).isDisabled(), true);
    await capture(reviewer.page, "reviewer-confirmation"); await reviewer.page.getByRole("checkbox", { name: confirmation, exact: true }).check();
    await reviewer.page.getByRole("button", { name: "Konfirmasi persetujuan aturan", exact: true }).click();
    await reviewer.page.getByRole("heading", { name: "Aturan disetujui", exact: true }).waitFor(); assert.equal((await pending(reviewer.page)).length, 0);
    const decision = successfulWrites()[1]; assert.equal(decision.path, apiPath + "/proposals/" + proposalId + "/approve");
    assert.equal(decision.expectedActor, config.reviewerId); assert.equal(decision.version, '"1"');
    const state = await control("state"); assert.deepEqual([state.proposalCount, state.decisionCount, state.ruleCount], [1, 1, 1]); assert.equal(state.physicalSha256, before.physicalSha256);
  });
  await check("maker refresh reads the persisted approved rule with no physical control", async () => {
    await maker.page.reload(); await warehouse(maker.page, true); await maker.page.getByRole("button", { name: "Aturan disetujui", exact: true }).click();
    await maker.page.getByRole("heading", { name: "Aturan " + proposalId + ", versi 1", exact: true }).waitFor();
    const rules = await query(maker.page, config.makerId, "/rules/query", { purpose: "pack-conversion-rule-history", location: preview.input.location });
    assert.equal(rules.status, 200); assert.equal(rules.body.items.length, 1); assert.equal(rules.body.items[0].id, proposalId);
    assert.equal(rules.body.items[0].physicalConversionAvailable, false); assert.deepEqual(rules.body.items[0].input, preview.input); await capture(maker.page, "approved-rule");
  });
  await check("foreign genuine cookie account receives scoped refusals and no other actor receipt", async () => {
    foreign = await signIn("foreign", config.foreignId, false);
    for (const [suffix, body] of [["/proposals/" + proposalId + "/query", { purpose: "pack-conversion-rule-history" }],
      ["/rules/query", { purpose: "pack-conversion-rule-history", location: preview.input.location }]]) {
      const denied = await query(foreign.page, config.foreignId, suffix, body); assert.equal(denied.status, 403);
      for (const field of ["id", "input", "source", "destination", "items", "reason", "canReview", "binding"]) assert.equal(Object.hasOwn(denied.body, field), false);
    }
    const deniedReceipt = await query(foreign.page, config.foreignId, "/operations/query", { purpose: "operation-recovery", operation: "inventory.pack-conversion.rule.propose", key: successfulWrites()[0].key });
    assert.equal(deniedReceipt.status, 200); assert.deepEqual(deniedReceipt.body, { state: "not_confirmed", result: null });
  });
  await check("committed reply loss preserves one key and body then reload recovers through the UI without duplicates", async () => {
    await maker.page.getByRole("button", { name: "Siapkan aturan", exact: true }).click();
    await prepare(maker.page, "Pengajuan sintetis kedua untuk pemulihan balasan yang hilang");
    await maker.page.getByRole("checkbox", { name: confirmation, exact: true }).check(); const start = successfulWrites().length;
    faults.dropNextWrite = true; await maker.page.getByRole("button", { name: "Konfirmasi pengajuan aturan", exact: true }).click();
    await maker.page.getByRole("heading", { name: "Hasil belum terkonfirmasi", exact: true }).waitFor();
    await maker.page.getByRole("button", { name: "Periksa hasil", exact: true }).waitFor(); assert.equal(faults.dropped, 1); assert.equal(successfulWrites().length, start + 1);
    const original = successfulWrites()[start], entries = await pending(maker.page); assert.equal(entries.length, 1);
    assert.deepEqual(Object.keys(entries[0]).sort(), ["actorId", "createdAt", "key", "operation"]);
    assert.equal(entries[0].actorId, config.makerId); assert.equal(entries[0].key, original.key); assert.equal(entries[0].operation, "inventory.pack-conversion.rule.propose");
    assert.match(original.bodyHash, /^[a-f0-9]{64}$/); const committed = await control("state");
    assert.deepEqual([committed.proposalCount, committed.decisionCount, committed.ruleCount], [2, 1, 1]); await capture(maker.page, "uncertain-proposal");
    // Deliberate same-cookie browser API replay. The receipt-only UI stays uncertain until its own recovery action.
    const replay = await query(maker.page, config.makerId, "/proposals", null, { rawBody: original.rawBody, headers: { "Idempotency-Key": original.key } });
    assert.equal(replay.status, 200); assert.equal(replay.body.state, "pending"); assert.equal(successfulWrites().length, start + 2);
    const repeated = successfulWrites()[start + 1]; assert.equal(repeated.key, original.key); assert.equal(repeated.rawBody, original.rawBody);
    assert.equal(repeated.bodyHash, original.bodyHash); assert.equal(repeated.expectedActor, original.expectedActor); assert.deepEqual(await pending(maker.page), entries);
    const afterReplay = await control("state"); assert.equal(afterReplay.durableSha256, committed.durableSha256); assert.equal(afterReplay.physicalSha256, before.physicalSha256);
    await maker.page.reload(); await maker.page.getByRole("button", { name: "Periksa hasil", exact: true }).click();
    await maker.page.getByText("Permintaan telah tersimpan", { exact: false }).waitFor(); assert.equal((await pending(maker.page)).length, 0);
    assert.equal(await maker.page.getByRole("button", { name: "Selesaikan dan tutup", exact: true }).count(), 0); assert.equal(successfulWrites().length, start + 2);
    await warehouse(maker.page, true); await proposal(maker.page, replay.body.proposalId); await capture(maker.page, "recovered-proposal");
    finalState = await control("state"); assert.equal(finalState.durableSha256, committed.durableSha256); assert.equal(finalState.physicalSha256, before.physicalSha256);
    assert.deepEqual([finalState.proposalCount, finalState.decisionCount, finalState.ruleCount], [2, 1, 1]);
    assert.equal(finalState.proposals.find(item => item.id === proposalId)?.status, "approved"); assert.equal(finalState.proposals.find(item => item.id === replay.body.proposalId)?.status, "pending");
  });
  assert.deepEqual(failures, []); assert.equal(sessionTokens.size, 3); assert.equal(checks.length, 9); assert.equal(captures.length, 10);
  assert.equal(successfulWrites().length, 4); assert.equal(new Set(successfulWrites().map(value => value.expectedActor + ":" + value.key)).size, 3);
  const responses = protectedResponses.filter(value => value.path.startsWith(apiPath)); assert.ok(responses.length > 0);
  await Promise.all(responseInspections);
  assert.equal(responses.every(value => value.noStore && value.publicOnly), true,
    JSON.stringify(responses.filter(value => !value.noStore || !value.publicOnly)));
  assert.equal(protectedResponses.some(value => value.method === "POST" && value.path.startsWith("/api/v1/inventory/") && !value.path.startsWith(apiPath + "/")), false);
  await writeFile(join(proofDirectory, config.proofPrefix + "-proof.json"), JSON.stringify({
    recordedUtc: new Date().toISOString(), checks, captures, nodeMajor: "24", playwrightVersion: "1.62.1", browserChannel,
    counts: { proposals: finalState.proposalCount, decisions: finalState.decisionCount, rules: finalState.ruleCount, genuineCookieLogins: sessionTokens.size,
      committedResponseLosses: faults.dropped, successfulCommandRequests: successfulWrites().length, distinctCommands: 3, protectedApiResponses: responses.length },
    physicalBeforeSha256: before.physicalSha256, physicalAfterSha256: finalState.physicalSha256, durableAfterSha256: finalState.durableSha256,
    originalKeyAndBodyPreserved: true, replayThroughGenuineBrowserFetch: true, recoveryThroughUi: true, noDuplicateProposal: true,
    independentPersonApproval: true, publicResponsesOnly: true, noPhysicalControl: true, browserApiMongoJourney: true,
    localFixture: true, syntheticProviderAndAccounts: true, physicalConversionAccepted: false, sourceActivationAuthorityAccepted: false,
    lotValueTransferAccepted: false, stagingValidated: false, productionAccepted: false,
  }, null, 2));
  console.log("LOCAL_SYNTHETIC_PACK_CONVERSION_BROWSER_PASS " + JSON.stringify({ checks, captures: captures.length, proposals: 2, decisions: 1, rules: 1, committedResponseLosses: faults.dropped, syntheticProviderAndAccounts: true, stagingValidated: false, productionAccepted: false }));
} catch (error) {
  console.error(error.stack ?? String(error)); process.exitCode = 1;
} finally {
  if (browser) await browser.close(); for (const socket of connections) socket.destroy();
  for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve)); await app.close();
}
