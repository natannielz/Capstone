// Opt-in synthetic-local proof only. This is never an application/deployment proxy.
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import { createHash } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { createRequire } from 'node:module';
import next from 'next';

const chunks = []; for await (const chunk of process.stdin) chunks.push(chunk);
const config = JSON.parse(Buffer.concat(chunks).toString('utf8'));
assert.equal(process.versions.node.split('.')[0], '24');
const api = new URL(config.apiAddress), origin = new URL(config.origin), issuer = new URL(config.issuer);
assert.equal(api.protocol, 'http:'); assert.equal(api.hostname, '127.0.0.1'); assert.ok(Number(api.port) > 0);
assert.equal(api.pathname, '/'); assert.equal(api.username + api.password + api.search + api.hash, '');
assert.equal(origin.href, 'https://portal.unit-toko.co.id/'); assert.equal(issuer.href, 'https://login.unit-toko.co.id/');
for (const key of ['makerId', 'reviewerId', 'foreignId', 'poolId']) assert.match(config[key], /^[1-9][0-9]{0,18}$/);
assert.equal(new Set([config.makerId, config.reviewerId, config.foreignId]).size, 3);
assert.equal(typeof config.controlKey, 'string'); assert.ok(config.controlKey.length > 0 && config.controlKey.length <= 256);
assert.equal(config.handoffReference, 'Synthetic executed empty physical handoff'); assert.match(config.proofPrefix, /^source-activation-[a-f0-9]{32}$/);
const loadBrowser = createRequire(config.playwrightPackage), browserPackage = loadBrowser(config.playwrightPackage);
assert.equal(browserPackage.name, 'playwright'); assert.equal(browserPackage.version, '1.62.1');
const { chromium } = loadBrowser('playwright'), browserChannel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'chromium';
assert.ok(['chromium', 'msedge'].includes(browserChannel));
const route = '/inventory/sources/activations', apiPath = '/api/v1/inventory/sources';
const proposalPath = apiPath + '/pools/' + config.poolId + '/activation-proposals';
const confirmation = 'Saya telah memeriksa sumber, serah terima, dan keputusan ini. Tindakan ini tidak menambah stok, menetapkan harga, memproses pembayaran, atau menerbitkan invoice.';
const writes = [], protectedResponses = [], responseInspections = [], faults = { dropNextWrite: false, dropped: 0 };
const connections = new Set(), failures = [], checks = [], captures = [], sessionTokens = new Set();
const proofDirectory = join(process.cwd(), '..', 'tmp', 'production', 'source-activation-browser-results');
const privateFields = new Set(['people', 'personId', 'actorId', 'actorGrantVersion', 'issuer', 'subject', 'auditId', 'bodyHash', 'snapshotHash', 'requestKey']);
let browser, frontend, edge, tunnel, maker, reviewer, foreign, preview, proposalId, finalState;
const app = next({ dev: false, dir: process.cwd(), quiet: true });
function privateFieldsAbsent(value) {
  if (!value || typeof value !== 'object') return;
  for (const [key, child] of Object.entries(value)) { assert.equal(privateFields.has(key), false, 'private response field: ' + key); privateFieldsAbsent(child); }
}
function track(server) { server.on('connection', socket => { connections.add(socket); socket.on('close', () => connections.delete(socket)); }); return server; }
async function listen(server) { await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); }); return server.address().port; }
function forward(req, res, port, isApi) {
  const headers = { ...req.headers, host: '127.0.0.1:' + port };
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || name === 'forwarded' || name === 'connection' || name === 'x-fixture-control') delete headers[name];
  if (isApi) { headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https'; }
  const mutation = isApi && req.method === 'POST' && (req.url === proposalPath || new RegExp('^' + apiPath + '/activation-proposals/[1-9][0-9]*/(?:approve|reject)$').test(req.url));
  let command;
  if (mutation) {
    command = { path: req.url, key: req.headers['idempotency-key'], expectedActor: req.headers['x-expected-actor-id'], version: req.headers['if-match'], bodyHash: null, rawBody: null, status: null };
    const body = [], hash = createHash('sha256'); writes.push(command); req.on('data', chunk => { body.push(chunk); hash.update(chunk); });
    req.on('end', () => { command.bodyHash = hash.digest('hex'); command.rawBody = Buffer.concat(body).toString('utf8'); });
  }
  const drop = mutation && faults.dropNextWrite; if (drop) faults.dropNextWrite = false;
  const upstream = http.request({ hostname: '127.0.0.1', port, path: req.url, method: req.method, headers }, response => {
    if (command) command.status = response.statusCode;
    if (isApi) {
      const observed = { path: req.url.split('?')[0], method: req.method, status: response.statusCode, noStore: response.headers['cache-control']?.includes('no-store') === true,
        origin: req.headers.origin ?? null, fetchSite: req.headers['sec-fetch-site'] ?? null, sessionCookiePresent: (req.headers.cookie ?? '').includes('__Host-ut_session=') };
      protectedResponses.push(observed);
      if (observed.path.startsWith(apiPath + '/')) {
        let finish; responseInspections.push(new Promise(resolve => { finish = resolve; }));
        const timeout = setTimeout(() => { observed.publicOnly = false; observed.inspectionError = 'upstream_body_timeout'; response.destroy(); finish(); }, 20000);
        const body = []; let length = 0;
        response.on('data', chunk => { length += chunk.length; if (length <= 131072) body.push(chunk); });
        response.on('end', () => {
          try { const value = JSON.parse(Buffer.concat(body).toString('utf8')); privateFieldsAbsent(value); observed.publicOnly = true; observed.code = value?.code ?? null; }
          catch { observed.publicOnly = false; } clearTimeout(timeout); finish();
        });
        const incomplete = () => { if (!response.readableEnded) { observed.publicOnly = false; observed.inspectionError = 'upstream_body_incomplete'; } clearTimeout(timeout); finish(); };
        response.once('error', incomplete); response.once('aborted', incomplete); response.once('close', incomplete);
      }
    }
    if (drop && response.statusCode === 200) {
      response.resume(); response.once('end', () => {
        faults.dropped++; res.writeHead(200, { 'content-type': 'application/json', 'content-length': '64', 'cache-control': 'no-store' });
        res.flushHeaders(); res.write('{'); setTimeout(() => res.destroy(), 25);
      }); return;
    }
    // Independently finish inspecting genuine upstream replies even if a browser
    // cancels a stale read. Cancellation must not produce an unobserved false green.
    const drain = () => { response.unpipe(res); response.resume(); }; if (res.destroyed) { drain(); return; }
    res.once('close', drain); res.writeHead(response.statusCode ?? 502, response.headers); response.pipe(res);
  });
  upstream.on('error', () => { if (!res.headersSent) res.writeHead(502); res.end(); }); req.on('aborted', () => upstream.destroy()); req.pipe(upstream);
}
async function control(action) {
  assert.ok(['maker', 'reviewer', 'foreign', 'same-person', 'distinct-people', 'state'].includes(action));
  const response = await fetch(new URL('/__fixture/source-activation/' + action, api), { method: 'POST', headers: { 'X-Fixture-Control': config.controlKey }, redirect: 'error' });
  assert.equal(response.status, action === 'state' ? 200 : 204);
  if (action === 'state') { const state = await response.json(); assert.match(state.durableSha256, /^[a-f0-9]{64}$/); assert.match(state.physicalSha256, /^[a-f0-9]{64}$/); return state; }
}
async function check(name, action) { await action(); checks.push(name); console.log('PASS ' + name); }
async function pending(page) { return page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key]))); }
async function query(page, actorId, suffix, body, options = {}) {
  const result = await page.evaluate(async ({ actorId, path, body, options }) => {
    const csrf = await (await fetch('/api/v1/identity/csrf', { credentials: 'same-origin', cache: 'no-store' })).json();
    const response = await fetch(path, { method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', headers: { 'Content-Type': 'application/json',
      'X-Expected-Actor-Id': actorId, ...(!options.omitCsrf ? { 'X-CSRF-Token': options.csrf ?? csrf.requestToken } : {}), ...options.headers }, body: options.rawBody ?? JSON.stringify(body) });
    return { status: response.status, body: await response.json(), noStore: response.headers.get('cache-control')?.includes('no-store') === true };
  }, { actorId, path: apiPath + suffix, body, options });
  assert.equal(result.noStore, true); privateFieldsAbsent(result.body); return result;
}
async function signIn(account, actorId, workspace = true) {
  await control(account); const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1280, height: 960 }, locale: 'id-ID', timezoneId: 'Asia/Jakarta' });
  const page = await context.newPage(); page.setDefaultTimeout(20000); page.on('pageerror', error => failures.push(error.message)); page.on('dialog', dialog => dialog.accept());
  await page.goto(origin.origin + route); await page.getByRole('heading', { name: /^(Masuk untuk melanjutkan|Sesi Anda perlu diperbarui)$/ }).waitFor();
  await page.locator('section[aria-labelledby="workspace-access-state"]').getByRole('link', { name: /^Masuk( kembali)?$/ }).click(); await page.getByRole('button', { name: 'Lanjutkan ke layanan akun', exact: true }).click();
  await page.waitForURL(origin.origin + route); if (workspace) await page.getByLabel('Referensi sumber stok', { exact: true }).waitFor();
  const cookies = await context.cookies(origin.origin);
  for (const name of ['__Host-ut_session', '__Host-ut_csrf']) {
    const cookie = cookies.find(value => value.name === name); assert.ok(cookie); assert.equal(cookie.secure, true); assert.equal(cookie.httpOnly, true);
    assert.equal(cookie.domain, origin.hostname); assert.equal(cookie.path, '/'); assert.equal(cookie.sameSite, name.endsWith('csrf') ? 'Strict' : 'Lax');
    if (name === '__Host-ut_session') { assert.equal(sessionTokens.has(cookie.value), false); sessionTokens.add(cookie.value); }
  }
  assert.equal(cookies.some(cookie => cookie.name.includes('Correlation') || cookie.name.includes('Nonce')), false);
  assert.equal(await page.evaluate(() => document.cookie.includes('__Host-ut_')), false);
  const session = await page.evaluate(async () => (await fetch('/api/v1/identity/session', { credentials: 'same-origin', cache: 'no-store' })).json());
  assert.equal(session.authenticated, true); assert.equal(session.account.userId, actorId); return { context, page };
}
async function choosePool(page) {
  const input = page.getByLabel('Referensi sumber stok', { exact: true }); assert.equal(await input.inputValue(), ''); await input.fill(config.poolId);
  await page.getByRole('button', { name: 'Periksa sumber', exact: true }).click(); await page.getByRole('heading', { name: 'Riwayat aktivasi', exact: true }).waitFor();
  await page.getByRole('button', { name: 'Muat ulang riwayat', exact: true }).waitFor();
}
async function fillHandoff(page, prefix) {
  const fields = page.locator('fieldset').filter({ has: page.locator('#' + prefix + '-empty') });
  for (const label of ['Jumlah kemasan fisik — tulis 0', 'Nilai barang bawaan — tulis 0']) assert.equal(await fields.getByLabel(label, { exact: true }).inputValue(), '');
  for (const suffix of ['empty', 'stopped', 'recorded']) assert.equal(await page.locator('#' + prefix + '-' + suffix).isChecked(), false);
  await fields.getByLabel('Acuan serah terima', { exact: true }).fill(config.handoffReference);
  const observedAt = await page.evaluate(() => {
    const date = new Date(), pad = value => String(value).padStart(2, '0');
    return date.getFullYear() + '-' + pad(date.getMonth() + 1) + '-' + pad(date.getDate()) + 'T' + pad(date.getHours()) + ':' + pad(date.getMinutes()) + ':' + pad(date.getSeconds());
  });
  await fields.getByLabel('Waktu pemeriksaan (zona waktu perangkat)', { exact: true }).fill(observedAt);
  await fields.getByLabel('Jumlah kemasan fisik — tulis 0', { exact: true }).fill('0'); await fields.getByLabel('Nilai barang bawaan — tulis 0', { exact: true }).fill('0');
  for (const suffix of ['empty', 'stopped', 'recorded']) await page.locator('#' + prefix + '-' + suffix).check();
}
async function capture(page, state) {
  await mkdir(proofDirectory, { recursive: true });
  for (const [device, width, height] of [['desktop', 1280, 960], ['mobile', 390, 844]]) {
    await page.setViewportSize({ width, height }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true);
    const path = join(proofDirectory, config.proofPrefix + '-' + state + '-' + device + '.png'), pixels = await page.screenshot({ path, fullPage: true });
    captures.push({ state, device, width, height, path, sha256: createHash('sha256').update(pixels).digest('hex') });
  } await page.setViewportSize({ width: 1280, height: 960 });
}
const successfulWrites = () => writes.filter(write => write.status === 200);
try {
  await app.prepare(); const handle = app.getRequestHandler();
  frontend = track(http.createServer((req, res) => { void handle(req, res).catch(() => { res.statusCode = 500; res.end(); }); })); const frontendPort = await listen(frontend);
  edge = track(https.createServer({ pfx: Buffer.from(config.pfx, 'base64'), passphrase: config.password }, (req, res) => {
    if (req.headers.host === issuer.host && req.method === 'GET') {
      const url = new URL(req.url, issuer);
      if (url.pathname === '/fixture-other-origin') { res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' }); res.end('<!doctype html><title>Synthetic other-origin page</title>'); return; }
      if (url.pathname === '/authorize' && url.searchParams.has('state')) { res.writeHead(302, { location: origin.origin + '/auth/callback?code=fixture-code&state=' + encodeURIComponent(url.searchParams.get('state')), 'cache-control': 'no-store' }); res.end(); return; }
    }
    if (req.headers.host !== origin.host || req.url.startsWith('/__fixture/')) { res.writeHead(403); res.end(); return; }
    const isApi = req.url === '/auth' || req.url.startsWith('/auth/') || req.url.startsWith('/api/v1/'); forward(req, res, isApi ? Number(api.port) : frontendPort, isApi);
  })); const tlsPort = await listen(edge);
  tunnel = track(http.createServer((_req, res) => { res.writeHead(403); res.end(); }));
  tunnel.on('connect', (req, socket, head) => {
    if (![origin.host + ':443', issuer.host + ':443'].includes(req.url)) { socket.end('HTTP/1.1 403 Forbidden\r\n\r\n'); return; }
    const upstream = net.connect(tlsPort, '127.0.0.1', () => { socket.write('HTTP/1.1 200 Connection Established\r\n\r\n'); if (head.length) upstream.write(head); socket.pipe(upstream); upstream.pipe(socket); });
    connections.add(upstream); upstream.on('close', () => connections.delete(upstream)); upstream.on('error', () => socket.destroy()); socket.on('error', () => upstream.destroy()); socket.on('close', () => upstream.destroy());
  }); const proxyPort = await listen(tunnel);
  browser = await chromium.launch({ ...(browserChannel === 'msedge' ? { channel: 'msedge' } : {}), headless: true, proxy: { server: 'http://127.0.0.1:' + proxyPort } });
  const before = await control('state'); assert.deepEqual([before.proposalCount, before.decisionCount, before.activationCount, before.poolActive], [0, 0, 0, false]);
  await check('maker has a genuine protected OIDC cookie and explicitly observed empty 0/0 handoff', async () => {
    maker = await signIn('maker', config.makerId);
    const response = maker.page.waitForResponse(value => value.url() === origin.origin + apiPath + '/pools/' + config.poolId + '/activation-preview/query');
    await choosePool(maker.page); const actual = await response; assert.equal(actual.status(), 200); preview = await actual.json(); privateFieldsAbsent(preview);
    assert.equal(preview.physicalPoolId, config.poolId); assert.equal(preview.active, false); assert.equal(preview.obligations.length, 7);
    assert.equal(new Set(preview.obligations.map(item => item.domain)).size, 7); assert.equal(preview.obligations.every(item => item.complete && item.openCount === '0'), true);
    await maker.page.getByText('Belum ada pengajuan aktivasi pada sumber ini.', { exact: true }).waitFor();
    assert.equal(await maker.page.locator('#maker-empty').evaluate(input => input.form.checkValidity()), false);
    await maker.page.getByRole('button', { name: 'Tinjau pengajuan', exact: true }).click();
    assert.equal(await maker.page.getByRole('heading', { name: 'Periksa sebelum menyimpan', exact: true }).count(), 0);
    assert.equal((await control('state')).proposalCount, 0);
    await fillHandoff(maker.page, 'maker'); await maker.page.getByLabel('Alasan pengajuan', { exact: true }).fill('Serah terima kosong sintetis untuk browser lokal');
    await maker.page.getByRole('button', { name: 'Tinjau pengajuan', exact: true }).click(); await maker.page.getByRole('heading', { name: 'Periksa sebelum menyimpan', exact: true }).waitFor();
    assert.equal(await maker.page.getByRole('button', { name: 'Kirim pengajuan aktivasi', exact: true }).isDisabled(), true); await capture(maker.page, 'maker-confirmation');
  });
  await check('browser CSRF and actor boundary refuse without producing business records', async () => {
    const handoff = { reference: config.handoffReference, observedAt: new Date().toISOString(), physicalPackages: '0', carriedValueMinorUnits: '0', emptyPoolConfirmed: true, previousWriterStopped: true, noUnrecordedStock: true };
    const body = { expected: preview.expected, handoff, reason: 'Synthetic forbidden browser guard' }, suffix = '/pools/' + config.poolId + '/activation-proposals';
    for (const [actor, options, status] of [[config.makerId, { omitCsrf: true }, 403], [config.makerId, { csrf: 'invalid-csrf' }, 403], [config.reviewerId, {}, 409]])
      assert.equal((await query(maker.page, actor, suffix, body, { ...options, headers: { 'Idempotency-Key': 'activation-guard-' + status + '-' + actor, 'If-Match': '"' + preview.expected.poolRevision + '"' } })).status, status);
    const guarded = await query(maker.page, config.makerId, '/pools/' + config.poolId + '/activation-proposals/query',
      { purpose: 'inventory-source-activation-history', offset: 0, limit: 25 }, { headers: { 'X-Forwarded-Host': 'outside.invalid', 'X-Forwarded-Proto': 'http', 'X-Forwarded-For': '192.0.2.9', 'Forwarded': 'host=outside.invalid;proto=http' } });
    assert.equal(guarded.status, 200);
    const other = await maker.context.newPage();
    try {
      await other.goto(issuer.origin + '/fixture-other-origin'); const start = protectedResponses.length;
      await other.evaluate(async target => { await fetch(target, { method: 'POST', mode: 'no-cors', credentials: 'include', headers: { 'Content-Type': 'text/plain' }, body: '{}' }); }, origin.origin + proposalPath);
      await Promise.all(responseInspections);
      const denied = protectedResponses.slice(start).find(value => value.path === proposalPath);
      assert.ok(denied); assert.equal(denied.origin, issuer.origin); assert.equal(denied.fetchSite, 'same-site'); assert.equal(denied.sessionCookiePresent, true);
      assert.equal(denied.status, 403); assert.equal(denied.code, 'untrusted_origin');
    } finally { await other.close(); }
    const state = await control('state'); assert.equal(state.durableSha256, before.durableSha256); assert.equal(state.physicalSha256, before.physicalSha256); assert.equal(state.poolActive, false);
  });
  await check('lost committed proposal reply keeps metadata and original key; reload UI recovery creates no duplicate', async () => {
    await maker.page.getByRole('checkbox', { name: confirmation, exact: true }).check(); faults.dropNextWrite = true;
    const submission = maker.page.waitForResponse(response => response.url() === origin.origin + proposalPath && response.request().method() === 'POST');
    await maker.page.getByRole('button', { name: 'Kirim pengajuan aktivasi', exact: true }).click();
    assert.equal((await submission).status(), 200, JSON.stringify(protectedResponses.slice(-3)));
    await maker.page.getByRole('heading', { name: 'Hasil belum terkonfirmasi', exact: true }).waitFor(); assert.equal(faults.dropped, 1);
    const entries = await pending(maker.page), original = successfulWrites()[0]; assert.equal(entries.length, 1);
    assert.deepEqual(Object.keys(entries[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.equal(entries[0].key, original.key);
    assert.equal(entries[0].operation, 'stock.source.activation.propose'); assert.equal(original.expectedActor, config.makerId);
    const body = JSON.parse(original.rawBody); assert.equal(body.handoff.physicalPackages, '0'); assert.equal(body.handoff.carriedValueMinorUnits, '0');
    assert.equal(body.handoff.emptyPoolConfirmed && body.handoff.previousWriterStopped && body.handoff.noUnrecordedStock, true);
    const committed = await control('state'); assert.deepEqual([committed.proposalCount, committed.decisionCount, committed.activationCount, committed.poolActive], [1, 0, 0, false]);
    proposalId = committed.proposals[0].id; await capture(maker.page, 'uncertain-proposal');
    const suffix = '/pools/' + config.poolId + '/activation-proposals', headers = { 'Idempotency-Key': original.key, 'If-Match': original.version };
    const replay = await query(maker.page, config.makerId, suffix, null, { rawBody: original.rawBody, headers }); assert.equal(replay.status, 200); assert.equal(replay.body.proposalId, proposalId);
    const changed = await query(maker.page, config.makerId, suffix, { ...body, reason: 'Changed original-key reason' }, { headers }); assert.equal(changed.status, 409); assert.equal(changed.body.code, 'idempotency_key_reused');
    assert.deepEqual(await pending(maker.page), entries); const afterReplay = await control('state'); assert.equal(afterReplay.durableSha256, committed.durableSha256);
    const repeated = successfulWrites()[1]; assert.equal(repeated.key, original.key); assert.equal(repeated.rawBody, original.rawBody); assert.equal(repeated.bodyHash, original.bodyHash);
    await maker.page.reload(); await maker.page.getByLabel('Referensi sumber semula', { exact: true }).fill(config.poolId);
    await maker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await maker.page.getByText('Permintaan telah tersimpan', { exact: false }).waitFor();
    assert.equal((await pending(maker.page)).length, 0); assert.equal(successfulWrites().length, 2); assert.equal(await maker.page.getByRole('button', { name: 'Selesaikan dan tutup', exact: true }).count(), 0);
    const own = await query(maker.page, config.makerId, '/activation-proposals/' + proposalId + '/review/query',
      { purpose: 'inventory-source-activation-review', physicalPoolId: config.poolId, action: 'reject', handoff: null }); assert.equal(own.status, 403);
    assert.equal((await control('state')).durableSha256, committed.durableSha256); await capture(maker.page, 'recovered-proposal');
  });
  await check('same-person second genuine account cannot approve; independent person then reviews actual empty handoff', async () => {
    reviewer = await signIn('reviewer', config.reviewerId); await choosePool(reviewer.page); await control('same-person');
    const handoff = { reference: config.handoffReference, observedAt: new Date().toISOString(), physicalPackages: '0', carriedValueMinorUnits: '0', emptyPoolConfirmed: true, previousWriterStopped: true, noUnrecordedStock: true };
    const denied = await query(reviewer.page, config.reviewerId, '/activation-proposals/' + proposalId + '/review/query',
      { purpose: 'inventory-source-activation-review', physicalPoolId: config.poolId, action: 'approve', handoff }); assert.equal(denied.status, 403);
    assert.ok(denied.body.code.includes('person') || denied.body.code.includes('independent'), denied.body.code);
    await control('distinct-people'); const state = await control('state'); assert.deepEqual([state.proposalCount, state.decisionCount, state.activationCount, state.poolActive], [1, 0, 0, false]);
    await reviewer.page.getByLabel('Referensi pengajuan', { exact: true }).fill(proposalId); await fillHandoff(reviewer.page, 'checker');
    const response = reviewer.page.waitForResponse(value => value.url() === origin.origin + apiPath + '/activation-proposals/' + proposalId + '/review/query');
    await reviewer.page.getByRole('button', { name: 'Periksa pengajuan dan serah terima', exact: true }).click(); const actual = await response; assert.equal(actual.status(), 200);
    const result = await actual.json(); privateFieldsAbsent(result); assert.equal(result.proposalId, proposalId); assert.equal(result.physicalPoolId, config.poolId); assert.equal(result.action, 'approve');
    await reviewer.page.getByLabel('Alasan keputusan', { exact: true }).fill('Serah terima kosong diperiksa oleh orang berbeda');
    await reviewer.page.getByRole('button', { name: 'Tinjau keputusan', exact: true }).click(); await reviewer.page.getByRole('heading', { name: 'Periksa sebelum menyimpan', exact: true }).waitFor();
    assert.equal(await reviewer.page.getByRole('button', { name: 'Setujui aktivasi sumber', exact: true }).isDisabled(), true); await capture(reviewer.page, 'independent-confirmation');
    await reviewer.page.getByRole('checkbox', { name: confirmation, exact: true }).check(); await reviewer.page.getByRole('button', { name: 'Setujui aktivasi sumber', exact: true }).click();
    await reviewer.page.getByRole('heading', { name: 'Hasil aktivasi tercatat', exact: true }).waitFor(); assert.equal((await pending(reviewer.page)).length, 0);
    const activated = await control('state'); assert.deepEqual([activated.proposalCount, activated.decisionCount, activated.activationCount, activated.poolActive], [1, 1, 1, true]);
    assert.equal(activated.physicalSha256, before.physicalSha256); assert.equal(successfulWrites()[2].expectedActor, config.reviewerId); assert.equal(successfulWrites()[2].version, '"1"');
  });
  await check('maker refreshes and reads durable activation history and detail through actual API', async () => {
    await maker.page.reload(); await choosePool(maker.page);
    await maker.page.getByRole('button', { name: 'Lihat rincian', exact: true }).click(); await maker.page.getByRole('heading', { name: 'Pengajuan ' + proposalId + ' · Aktivasi disetujui', exact: true }).waitFor();
    const history = await query(maker.page, config.makerId, '/pools/' + config.poolId + '/activation-proposals/query', { purpose: 'inventory-source-activation-history', offset: 0, limit: 25 });
    assert.equal(history.status, 200); assert.equal(history.body.items.length, 1); assert.equal(history.body.items[0].id, proposalId); assert.equal(history.body.items[0].status, 'activated');
    assert.equal(history.body.items[0].decision.handoff.physicalPackages, '0'); assert.equal(history.body.items[0].decision.handoff.carriedValueMinorUnits, '0'); await capture(maker.page, 'activated-history');
  });
  await check('foreign source scope and another actor original-key receipt remain inaccessible', async () => {
    foreign = await signIn('foreign', config.foreignId, false);
    for (const [suffix, body] of [[ '/pools/' + config.poolId + '/activation-proposals/query', { purpose: 'inventory-source-activation-history', offset: 0, limit: 25 } ],
      [ '/pools/' + config.poolId + '/activation-proposals/' + proposalId + '/query', { purpose: 'inventory-source-activation-history' } ],
      [ '/activation-operations/query', { purpose: 'operation-recovery', physicalPoolId: config.poolId, operation: 'stock.source.activation.propose', key: successfulWrites()[0].key } ]]) {
      const denied = await query(foreign.page, config.foreignId, suffix, body); assert.equal(denied.status, 403);
      for (const field of ['id', 'items', 'handoff', 'expected', 'configuration', 'result', 'proposal']) assert.equal(Object.hasOwn(denied.body, field), false);
    }
    const originalDecision = successfulWrites()[2], replay = await query(reviewer.page, config.reviewerId, '/activation-proposals/' + proposalId + '/approve', null,
      { rawBody: originalDecision.rawBody, headers: { 'Idempotency-Key': originalDecision.key, 'If-Match': originalDecision.version } });
    assert.equal(replay.status, 200); assert.equal(replay.body.status, 'activated');
    finalState = await control('state'); assert.deepEqual([finalState.proposalCount, finalState.decisionCount, finalState.activationCount, finalState.poolActive], [1, 1, 1, true]);
    assert.equal(finalState.physicalSha256, before.physicalSha256);
  });
  assert.deepEqual(failures, []); assert.equal(sessionTokens.size, 3); assert.equal(checks.length, 6); assert.equal(captures.length, 10);
  assert.equal(successfulWrites().length, 4); assert.equal(new Set(successfulWrites().map(write => write.expectedActor + ':' + write.key)).size, 2);
  const responses = protectedResponses.filter(value => value.path.startsWith(apiPath + '/')); assert.ok(responses.length > 0); await Promise.all(responseInspections);
  assert.equal(responses.every(value => value.noStore && value.publicOnly), true, JSON.stringify(responses.filter(value => !value.noStore || !value.publicOnly)));
  await writeFile(join(proofDirectory, config.proofPrefix + '-proof.json'), JSON.stringify({ recordedUtc: new Date().toISOString(), checks, captures,
    nodeMajor: '24', playwrightVersion: '1.62.1', browserChannel, counts: { proposals: 1, decisions: 1, activations: 1, genuineCookieLogins: sessionTokens.size,
      committedReplyLosses: faults.dropped, successfulCommandRequests: successfulWrites().length, distinctCommands: 2, protectedResponses: responses.length },
    physicalBeforeSha256: before.physicalSha256, physicalAfterSha256: finalState.physicalSha256, durableAfterSha256: finalState.durableSha256,
    originalKeyAndBodyPreserved: true, recoveryThroughUi: true, changedBodyOriginalKeyDenied: true, independentPersonApproval: true,
    actualPoolActive: finalState.poolActive, noDuplicateProposalOrActivation: true, browserApiMongoJourney: true, unchangedStockAndMoney: true, publicResponsesOnly: true,
    localFixture: true, syntheticProviderAndAccounts: true, externalProviderAccepted: false, stagingValidated: false, productionAccepted: false }, null, 2));
  console.log('LOCAL_SYNTHETIC_SOURCE_ACTIVATION_BROWSER_PASS ' + JSON.stringify({ checks, captures: captures.length, proposals: 1, decisions: 1, activations: 1,
    committedReplyLosses: faults.dropped, actualPoolActive: finalState.poolActive, syntheticProviderAndAccounts: true, stagingValidated: false, productionAccepted: false }));
} catch (error) {
  await mkdir(proofDirectory, { recursive: true });
  await writeFile(join(proofDirectory, config.proofPrefix + '-failure.json'), JSON.stringify({ checkedUtc: new Date().toISOString(), checks, captures,
    commands: writes, responses: protectedResponses, faults, error: error.message, localFixture: true, productionAccepted: false }, null, 2));
  if (maker?.page && !maker.page.isClosed()) await maker.page.screenshot({ path: join(proofDirectory, config.proofPrefix + '-failed-screen.png'), fullPage: true });
  console.error(error.stack ?? String(error)); process.exitCode = 1;
}
finally { if (browser) await browser.close(); for (const socket of connections) socket.destroy();
  for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve)); await app.close(); }
