// Local synthetic signed OIDC only. All business origins are genuine owning commands.
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import { createHash } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { createRequire } from 'node:module';
import next from 'next';

const input = []; for await (const chunk of process.stdin) input.push(chunk);
const config = JSON.parse(Buffer.concat(input).toString('utf8'));
assert.equal(process.versions.node.split('.')[0], '24');
const api = new URL(config.apiAddress), origin = new URL(config.origin), issuer = new URL(config.issuer);
assert.equal(api.protocol, 'http:'); assert.equal(api.hostname, '127.0.0.1'); assert.ok(Number(api.port) > 0);
assert.equal(api.pathname, '/'); assert.equal(api.username + api.password + api.search + api.hash, '');
assert.equal(origin.href, 'https://portal.unit-toko.co.id/'); assert.equal(issuer.href, 'https://login.unit-toko.co.id/');
for (const name of ['makerId', 'foreignId', 'ruleId', 'sourceBalanceId']) assert.match(config[name], /^[1-9][0-9]{0,18}$/);
assert.notEqual(config.makerId, config.foreignId); assert.match(config.proofPrefix, /^pack-execution-[a-f0-9]{32}$/);
assert.equal(typeof config.buildId, 'string'); assert.ok(config.buildId.length > 0 && config.buildId.length < 100);
assert.equal(typeof config.controlKey, 'string'); assert.ok(config.controlKey.length > 0 && config.controlKey.length <= 256);
const load = createRequire(config.playwrightPackage), pkg = load(config.playwrightPackage);
assert.equal(pkg.name, 'playwright'); assert.equal(pkg.version, '1.62.1');
const { chromium } = load('playwright'), browserChannel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'chromium';
assert.ok(['chromium', 'msedge'].includes(browserChannel));
const route = '/inventory/pack-conversion-executions', base = '/api/v1/inventory/pack-conversions', path = base + '/executions';
const operation = 'inventory.pack-conversion.execute', policy = 'remaining-proportional-minor-unit-floor-v1';
const confirmation = 'Saya telah memeriksa lot, arah kemasan, jumlah, dan nilai. Saya memahami bahwa pelaksanaan ini memindahkan stok dan nilai barang.';
const location = { kind: 'fulfillmentWarehouse', organizationId: '100', shopId: '101', warehouseId: '102', channelId: '103', branchId: null, divisionId: null };
const writes = [], responses = [], inspections = [], failures = [], checks = [], captures = [], connections = new Set(), tokens = new Set();
const fault = { armed: false, dropped: 0 }, directory = join(process.cwd(), '..', 'tmp', 'production', 'pack-execution-browser-results');
const privateKeys = new Set(['people', 'personId', 'actorId', 'actorGrantVersion', 'issuer', 'subject', 'auditId', 'pinId', 'pinHash', 'journalId',
  'bodyHash', 'snapshotHash', 'requestKey', 'causeHash', 'phase', 'sourceLot', 'destinationLot', 'sourceValue', 'poolSource', 'catalog', 'rule', 'invoice', 'match']);
let browser, frontend, edge, tunnel, maker, foreign, preview, committed;
const app = next({ dev: false, dir: process.cwd(), quiet: true });
function publicOnly(value) { if (!value || typeof value !== 'object') return; for (const [key, child] of Object.entries(value)) { assert.equal(privateKeys.has(key), false, 'private field: ' + key); publicOnly(child); } }
function track(server) { server.on('connection', socket => { connections.add(socket); socket.on('close', () => connections.delete(socket)); }); return server; }
async function listen(server) { await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); }); return server.address().port; }
function forward(req, res, port, isApi) {
  const headers = { ...req.headers, host: '127.0.0.1:' + port };
  for (const key of Object.keys(headers)) if (key.startsWith('x-forwarded-') || key.startsWith('x-original-') || ['forwarded', 'connection', 'x-fixture-control'].includes(key)) delete headers[key];
  if (isApi) { headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https'; }
  const write = isApi && req.method === 'POST' && req.url === path;
  let record;
  if (write) {
    record = { path: req.url, key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'], version: req.headers['if-match'], status: null, body: null, sha256: null };
    writes.push(record); const bytes = [], hash = createHash('sha256');
    req.on('data', value => { bytes.push(value); hash.update(value); }); req.on('end', () => { record.body = Buffer.concat(bytes).toString('utf8'); record.sha256 = hash.digest('hex'); });
  }
  const drop = write && fault.armed; if (drop) fault.armed = false;
  const upstream = http.request({ hostname: '127.0.0.1', port, path: req.url, method: req.method, headers }, response => {
    if (record) record.status = response.statusCode;
    if (isApi && req.url.startsWith(base)) {
      const observed = { path: req.url, status: response.statusCode, noStore: response.headers['cache-control']?.includes('no-store') === true, publicOnly: false };
      responses.push(observed); const bytes = []; let size = 0, settle;
      inspections.push(new Promise(resolve => { settle = resolve; }));
      const timeout = setTimeout(() => { response.destroy(); settle(); }, 15000);
      response.on('data', value => { size += value.length; if (size <= 131072) bytes.push(value); });
      response.on('end', () => { try { assert.ok(size <= 131072); publicOnly(JSON.parse(Buffer.concat(bytes).toString('utf8'))); observed.publicOnly = true; } catch { observed.publicOnly = false; } clearTimeout(timeout); settle(); });
      response.once('error', () => { clearTimeout(timeout); settle(); }); response.once('aborted', () => { clearTimeout(timeout); settle(); });
    }
    if (drop && response.statusCode === 200) {
      response.resume(); response.once('end', () => { fault.dropped++; res.writeHead(200, { 'content-type': 'application/json', 'content-length': '64', 'cache-control': 'no-store' }); res.flushHeaders(); res.write('{'); setTimeout(() => res.destroy(), 25); }); return;
    }
    const drain = () => { response.unpipe(res); response.resume(); }; if (res.destroyed) { drain(); return; } res.once('close', drain);
    res.writeHead(response.statusCode ?? 502, response.headers); response.pipe(res);
  });
  upstream.on('error', () => { if (!res.headersSent) res.writeHead(502); res.end(); }); req.once('aborted', () => upstream.destroy()); req.pipe(upstream);
}
async function control(action) {
  assert.ok(['maker', 'foreign', 'state'].includes(action));
  const response = await fetch(new URL('/__fixture/pack-execution/' + action, api), { method: 'POST', headers: { 'X-Fixture-Control': config.controlKey }, redirect: 'error' });
  assert.equal(response.status, action === 'state' ? 200 : 204);
  if (action === 'state') { const value = await response.json(); assert.match(value.durableSha256, /^[a-f0-9]{64}$/); assert.match(value.originalSha256, /^[a-f0-9]{64}$/); return value; }
}
async function check(name, action) { await action(); checks.push(name); console.log('PASS ' + name); }
async function pending(page) { return page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key]))); }
async function query(page, actorId, suffix, body, options = {}) {
  const result = await page.evaluate(async ({ actorId, url, body, options }) => {
    const issuer = await fetch('/api/v1/identity/csrf', { credentials: 'same-origin', cache: 'no-store' }); const csrf = await issuer.json();
    const response = await fetch(url, { method: 'POST', credentials: 'same-origin', cache: 'no-store', headers: { 'Content-Type': 'application/json',
      'X-Expected-Actor-Id': actorId, ...(!options.omitCsrf ? { 'X-CSRF-Token': csrf.requestToken } : {}), ...options.headers }, body: JSON.stringify(body) });
    return { status: response.status, body: await response.json(), noStore: response.headers.get('cache-control')?.includes('no-store') === true, issuerStatus: issuer.status };
  }, { actorId, url: path + suffix, body, options });
  assert.equal(result.issuerStatus, 200); assert.equal(result.noStore, true); publicOnly(result.body); return result;
}
async function signIn(account, actorId) {
  await control(account); const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1280, height: 960 }, locale: 'id-ID', timezoneId: 'Asia/Jakarta' });
  const page = await context.newPage(); page.setDefaultTimeout(20000); page.on('pageerror', error => failures.push(error.message)); page.on('dialog', dialog => dialog.accept());
  page.on('response', response => { const url = new URL(response.url()); if (url.origin === origin.origin && url.pathname.startsWith(path)) console.log('PACK_BROWSER_RESPONSE ' + JSON.stringify({ account, path: url.pathname, status: response.status() })); });
  await page.goto(origin.origin + route); await page.getByRole('heading', { name: 'Masuk untuk membuka kemasan', exact: true }).waitFor();
  await page.getByRole('link', { name: 'Masuk kembali', exact: true }).click(); await page.getByRole('button', { name: 'Lanjutkan ke layanan akun', exact: true }).click();
  await page.waitForURL(origin.origin + route); await page.getByLabel('Gudang dan kanal', { exact: true }).waitFor();
  for (const name of ['__Host-ut_session', '__Host-ut_csrf']) {
    const cookie = (await context.cookies(origin.origin)).find(value => value.name === name); assert.ok(cookie); assert.equal(cookie.secure, true); assert.equal(cookie.httpOnly, true);
    assert.equal(cookie.domain, origin.hostname); assert.equal(cookie.path, '/'); assert.equal(cookie.sameSite, name.endsWith('csrf') ? 'Strict' : 'Lax');
    if (name.endsWith('session')) { assert.equal(tokens.has(cookie.value), false); tokens.add(cookie.value); }
  }
  assert.equal(await page.evaluate(() => document.cookie.includes('__Host-ut_')), false);
  const session = await page.evaluate(async () => (await fetch('/api/v1/identity/session', { credentials: 'same-origin', cache: 'no-store' })).json());
  assert.equal(session.authenticated, true); assert.equal(session.account.userId, actorId); return { context, page };
}
async function warehouse(page) {
  const directory = await page.evaluate(async ({ actorId, url }) => {
    const issuer = await fetch('/api/v1/identity/csrf', { credentials: 'same-origin', cache: 'no-store' }); const csrf = await issuer.json();
    const response = await fetch(url, { method: 'POST', credentials: 'same-origin', cache: 'no-store', headers: {
      'Content-Type': 'application/json', 'X-Expected-Actor-Id': actorId, 'X-CSRF-Token': csrf.requestToken }, body: JSON.stringify({ purpose: 'pack-conversion-rule-warehouses' }) });
    return { status: response.status, body: await response.json(), noStore: response.headers.get('cache-control')?.includes('no-store') === true, issuerStatus: issuer.status };
  }, { actorId: config.makerId, url: base + '/warehouses/query' });
  assert.equal(directory.status, 200); assert.equal(directory.issuerStatus, 200); assert.equal(directory.noStore, true); publicOnly(directory.body);
  const warehouse = directory.body.items.find(item => item.location.organizationId === '100' && item.location.shopId === '101' && item.location.warehouseId === '102' && item.location.channelId === '103');
  assert.ok(warehouse);
  await page.waitForFunction(() => { const select = document.querySelector('select[aria-labelledby="execution-warehouse-label"]'); return select && !select.disabled && select.options.length > 1; });
  const loaded = page.waitForResponse(response => response.url() === origin.origin + base + '/rules/query' && response.request().method() === 'POST', { timeout: 60000 });
  await page.getByLabel('Gudang dan kanal', { exact: true }).selectOption({ label: [warehouse.organizationName, warehouse.shopName, warehouse.warehouseName, warehouse.channelName].join(' / ') });
  const response = await loaded; assert.equal(response.status(), 200); const rules = await response.json(); publicOnly(rules);
  assert.ok(rules.items.some(value => value.id === config.ruleId)); await page.getByLabel('Aturan disetujui', { exact: true }).waitFor();
}
async function chooseRule(page) {
  await page.waitForFunction(ruleId => { const select = document.querySelector('select[aria-labelledby="execution-rule-label"]'); return select && !select.disabled && [...select.options].some(option => option.value === ruleId); }, config.ruleId);
  const sourceRead = page.waitForResponse(response => response.url() === origin.origin + path + '/sources/query' && response.request().method() === 'POST', { timeout: 60000 });
  await page.getByLabel('Aturan disetujui', { exact: true }).selectOption(config.ruleId);
  const response = await sourceRead; assert.equal(response.status(), 200); const sources = await response.json(); publicOnly(sources);
  await page.getByLabel('Lot penerimaan', { exact: true }).waitFor(); return sources;
}
async function capture(page, state) {
  await mkdir(directory, { recursive: true });
  for (const [device, width, height] of [['desktop', 1280, 960], ['mobile', 390, 844]]) {
    await page.setViewportSize({ width, height }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, state + ' overflow');
    const name = join(directory, config.proofPrefix + '-' + state + '-' + device + '.png'), pixels = await page.screenshot({ path: name, fullPage: true });
    captures.push({ state, device, width, height, path: name, sha256: createHash('sha256').update(pixels).digest('hex') });
  }
  await page.setViewportSize({ width: 1280, height: 960 });
}
try {
  await app.prepare(); const handle = app.getRequestHandler();
  frontend = track(http.createServer((req, res) => { void handle(req, res).catch(() => { res.statusCode = 500; res.end(); }); })); const frontendPort = await listen(frontend);
  edge = track(https.createServer({ pfx: Buffer.from(config.pfx, 'base64'), passphrase: config.password }, (req, res) => {
    if (req.headers.host === issuer.host && req.method === 'GET') { const url = new URL(req.url, issuer); if (url.pathname === '/authorize' && url.searchParams.has('state')) {
      res.writeHead(302, { location: origin.origin + '/auth/callback?code=fixture-code&state=' + encodeURIComponent(url.searchParams.get('state')), 'cache-control': 'no-store' }); res.end(); return;
    } }
    if (req.headers.host !== origin.host || req.url.startsWith('/__fixture/')) { res.writeHead(403); res.end(); return; }
    const isApi = req.url === '/auth' || req.url.startsWith('/auth/') || req.url.startsWith('/api/v1/'); forward(req, res, isApi ? Number(api.port) : frontendPort, isApi);
  })); const tlsPort = await listen(edge);
  tunnel = track(http.createServer((_req, res) => { res.writeHead(403); res.end(); })); tunnel.on('connect', (req, socket, head) => {
    if (![origin.host + ':443', issuer.host + ':443'].includes(req.url)) { socket.end('HTTP/1.1 403 Forbidden\r\n\r\n'); return; }
    const upstream = net.connect(tlsPort, '127.0.0.1', () => { socket.write('HTTP/1.1 200 Connection Established\r\n\r\n'); if (head.length) upstream.write(head); socket.pipe(upstream); upstream.pipe(socket); });
    connections.add(upstream); upstream.once('close', () => connections.delete(upstream)); upstream.on('error', () => socket.destroy()); socket.on('error', () => upstream.destroy()); socket.once('close', () => upstream.destroy());
  }); const proxyPort = await listen(tunnel);
  browser = await chromium.launch({ ...(browserChannel === 'msedge' ? { channel: 'msedge' } : {}), headless: true, proxy: { server: 'http://127.0.0.1:' + proxyPort } });
  const before = await control('state'); assert.deepEqual([before.conversionCount, before.movementCount, before.valueTransitionCount], [0, 0, 0]); assert.equal(before.sourceOnHand, '3'); assert.equal(before.sourceHeld, '2');
  await check('genuine signed cookie and explicit warehouse rule lot quantity preview', async () => {
    maker = await signIn('maker', config.makerId); await warehouse(maker.page); const sources = await chooseRule(maker.page);
    const source = sources.items.find(value => value.sourceBalanceId === config.sourceBalanceId); assert.ok(source); assert.equal(source.availableSourcePackages, '1'); assert.equal(source.remainingValue, '11250.00');
    await maker.page.waitForFunction(balanceId => { const select = document.querySelector('select[aria-labelledby="execution-source-label"]'); return select && !select.disabled && [...select.options].some(option => option.value === balanceId); }, source.sourceBalanceId);
    const lot = maker.page.getByLabel('Lot penerimaan', { exact: true }); assert.equal(await lot.inputValue(), ''); await lot.selectOption(source.sourceBalanceId);
    await maker.page.getByLabel('Jumlah langkah konversi', { exact: true }).fill('1'); await maker.page.getByLabel('Alasan pelaksanaan', { exact: true }).fill('Pembukaan satu kemasan untuk pemeriksaan browser lokal sintetis');
    const responseReady = maker.page.waitForResponse(response => response.url() === origin.origin + path + '/preview/query' && response.request().method() === 'POST', { timeout: 60000 });
    await maker.page.getByRole('button', { name: 'Minta pratinjau pelaksanaan', exact: true }).click(); const response = await responseReady; assert.equal(response.status(), 200); preview = await response.json(); publicOnly(preview);
    assert.equal(preview.input.ruleId, config.ruleId); assert.equal(preview.input.sourceBalanceId, config.sourceBalanceId); assert.equal(preview.input.steps, 1);
    assert.equal(preview.stockBefore.onHand, '3'); assert.equal(preview.stockAfter.onHand, '2'); assert.equal(preview.stockBefore.held, '2'); assert.equal(preview.stockAfter.held, '2');
    assert.deepEqual([preview.sourcePackages, preview.destinationPackages, preview.baseUnits], ['1', '12', '12']); assert.equal(preview.policy, policy); await capture(maker.page, 'preview');
    await maker.page.getByRole('button', { name: 'Tinjau pelaksanaan', exact: true }).click(); await maker.page.getByRole('heading', { name: 'Konfirmasi pelaksanaan buka kemasan', exact: true }).waitFor();
    assert.equal(await maker.page.getByRole('button', { name: 'Catat konversi fisik', exact: true }).isDisabled(), true); await capture(maker.page, 'confirmation');
  });
  await check('anonymous CSRF and actor-change refusals preserve all durable business data', async () => {
    const anonymous = await browser.newContext({ ignoreHTTPSErrors: true }); const page = await anonymous.newPage(); await page.goto(origin.origin + route);
    const denied = await query(page, config.makerId, '/preview/query', { purpose: 'pack-conversion-preview', input: preview.input }); assert.equal(denied.status, 401); await anonymous.close();
    const body = { input: preview.input, expected: preview.expected, binding: preview.binding, reason: 'Rejected local browser guard' };
    const headers = { 'Idempotency-Key': 'pack-browser-refused', 'If-Match': '"' + preview.expected.sourceBalanceRevision + '"' };
    const csrf = await query(maker.page, config.makerId, '', body, { omitCsrf: true, headers }); assert.equal(csrf.status, 403); assert.equal(csrf.body.code, 'antiforgery_failed');
    const actor = await query(maker.page, config.foreignId, '', body, { headers }); assert.equal(actor.status, 409); assert.equal(actor.body.code, 'actor_changed');
    assert.equal((await control('state')).durableSha256, before.durableSha256);
  });
  await check('one lost committed reply persists the original reference and UI recovery never resubmits', async () => {
    await maker.page.getByRole('checkbox', { name: confirmation, exact: true }).check(); fault.armed = true;
    await maker.page.getByRole('button', { name: 'Catat konversi fisik', exact: true }).click();
    await maker.page.getByRole('heading', { name: 'Hasil pelaksanaan belum terkonfirmasi', exact: true }).waitFor({ timeout: 120000 }); assert.equal(fault.dropped, 1);
    const successful = writes.filter(value => value.status === 200); assert.equal(successful.length, 1); const original = successful[0];
    assert.equal(original.actor, config.makerId); assert.equal(original.version, '"' + preview.expected.sourceBalanceRevision + '"'); assert.match(original.key, /^[A-Za-z0-9_.:-]{1,128}$/);
    const references = await pending(maker.page); assert.equal(references.length, 1); assert.deepEqual(Object.keys(references[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.equal(references[0].actorId, config.makerId); assert.equal(references[0].key, original.key); assert.equal(references[0].operation, operation);
    committed = await control('state'); assert.deepEqual([committed.conversionCount, committed.movementCount, committed.valueTransitionCount], [1, 1, 1]);
    assert.equal(committed.sourceOnHand, '2'); assert.equal(committed.sourceHeld, '2'); assert.equal(committed.destinationPackages, '12'); assert.equal(committed.destinationValue, '11250.00'); assert.equal(committed.originalSha256, before.originalSha256);
    await capture(maker.page, 'uncertain'); await maker.page.reload();
    const receiptReady = maker.page.waitForResponse(response => response.url() === origin.origin + path + '/operations/query', { timeout: 60000 });
    await maker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); const response = await receiptReady; assert.equal(response.status(), 200); const receipt = await response.json(); publicOnly(receipt);
    assert.deepEqual(JSON.parse(response.request().postData()), { purpose: 'operation-recovery', operation, key: original.key });
    assert.equal(receipt.state, 'committed'); assert.equal(receipt.result.conversionId, committed.conversionId); assert.equal(receipt.result.transferredValue, '11250.00');
    await maker.page.getByText('Permintaan telah tersimpan', { exact: false }).waitFor(); assert.equal((await pending(maker.page)).length, 0);
    assert.equal(writes.filter(value => value.status === 200).length, 1); assert.equal((await control('state')).durableSha256, committed.durableSha256);
  });
  await check('reloaded genuine committed history contains the persistent conversion reference', async () => {
    await warehouse(maker.page); await maker.page.getByRole('heading', { name: 'Konversi ' + committed.conversionId, exact: true }).waitFor({ timeout: 60000 });
    const detail = await query(maker.page, config.makerId, '/' + committed.conversionId + '/query', { purpose: 'pack-conversion-history' }); assert.equal(detail.status, 200);
    assert.equal(detail.body.id, committed.conversionId); assert.equal(detail.body.transferredValue, '11250.00'); assert.equal(detail.body.destinationAfter.onHand, '12');
    await capture(maker.page, 'history'); assert.equal((await control('state')).durableSha256, committed.durableSha256);
  });
  await check('foreign signed cookie scope refuses source and detail while original-actor recovery stays private', async () => {
    foreign = await signIn('foreign', config.foreignId);
    const source = await query(foreign.page, config.foreignId, '/sources/query', { purpose: 'pack-conversion-sources', location, ruleId: config.ruleId, ruleVersion: '1', limit: 25 }); assert.equal(source.status, 403);
    const detail = await query(foreign.page, config.foreignId, '/' + committed.conversionId + '/query', { purpose: 'pack-conversion-history' }); assert.equal(detail.status, 403);
    const original = writes.find(value => value.status === 200); const recovery = await query(foreign.page, config.foreignId, '/operations/query', { purpose: 'operation-recovery', operation, key: original.key });
    assert.equal(recovery.status, 200); assert.deepEqual(recovery.body, { state: 'not_confirmed', result: null });
    assert.equal((await control('state')).durableSha256, committed.durableSha256); assert.equal((await control('state')).originalSha256, before.originalSha256);
  });
  await Promise.all(inspections); assert.deepEqual(failures, []); assert.equal(checks.length, 5); assert.equal(captures.length, 8); assert.equal(tokens.size, 2);
  assert.ok(responses.length > 0); assert.equal(responses.every(value => value.noStore && value.publicOnly), true);
  assert.equal(writes.filter(value => value.status === 200).length, 1); assert.equal(fault.dropped, 1);
  await writeFile(join(directory, config.proofPrefix + '-proof.json'), JSON.stringify({ recordedUtc: new Date().toISOString(), buildId: config.buildId, checks, captures,
    nodeMajor: '24', playwrightVersion: '1.62.1', browserChannel, genuineCookieLogins: 2, committedReplyLosses: 1, successfulConversionRequests: 1,
    conversionCount: 1, movementCount: 1, valueTransitionCount: 1, sourceHeldBefore: '2', sourceHeldAfter: committed.sourceHeld, destinationPackages: '12', destinationValue: '11250.00',
    durableSha256: committed.durableSha256, originalBeforeSha256: before.originalSha256, originalAfterSha256: committed.originalSha256,
    originalKeyRecoveryThroughUi: true, noSecondConversionRequest: true, originalAcquisitionDocumentsJournalPayableUnchanged: true, noCogsPosted: true,
    localSyntheticOnly: true, syntheticSignedOidcProvider: true, externalProviderAccepted: false, stagingValidated: false, productionAccepted: false }, null, 2));
  console.log('LOCAL_SYNTHETIC_PACK_EXECUTION_BROWSER_PASS ' + JSON.stringify({ checks, captures: captures.length, conversions: 1, movements: 1, valueTransitions: 1, localSyntheticOnly: true }));
} catch (error) { console.error(error.stack ?? String(error)); process.exitCode = 1; }
finally { if (browser) await browser.close(); for (const socket of connections) socket.destroy(); for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve)); await app.close(); }
