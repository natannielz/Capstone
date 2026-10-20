// Actual approved admissions and production Next/HTTPS/API/Mongo. Provider/contact/roster inputs are local synthetic fixtures.
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import { createRequire } from 'node:module';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { createHash } from 'node:crypto';
import { EventEmitter, once } from 'node:events';
import next from 'next';

const input = []; for await (const chunk of process.stdin) input.push(chunk);
const config = JSON.parse(Buffer.concat(input).toString('utf8')), api = new URL(config.apiAddress), origin = new URL(config.origin);
assert.equal(process.versions.node.split('.')[0], '24'); assert.equal(api.protocol, 'http:'); assert.equal(api.hostname, '127.0.0.1');
assert.equal(api.pathname, '/'); assert.ok(Number(api.port) > 0); assert.equal(origin.href, 'https://portal.unit-toko.co.id/');
assert.match(config.proofPrefix, /^approved-order-dispatch-[a-f0-9]{32}$/); assert.equal(config.expectedValue, '11250.00');
assert.notEqual(config.actorIds.buyer, config.actorIds.spender); assert.ok(Number.isInteger(config.packages) && config.packages > 0);
const load = createRequire(config.playwrightPackage), metadata = load(config.playwrightPackage); assert.equal(metadata.name, 'playwright'); assert.equal(metadata.version, '1.62.1');
const { chromium } = load('playwright'), channel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'chromium'; assert.ok(['chromium', 'msedge'].includes(channel));
const app = next({ dev: false, dir: process.cwd(), quiet: true }), directory = resolve('../tmp/production/approved-order-dispatch-browser-results');
const sockets = new Set(), pages = [], errors = [], writes = [], recoveries = [], responses = [], captures = [], checks = [], diagnostics = [];
const readTimings = [], startedAt = performance.now(), previewHold = { releasedElapsedMs: null };
const committedResponses = new EventEmitter();
const elapsedMs = () => Math.round(performance.now() - startedAt);
const fault = { armed: null, dropped: 0 }; let browser, frontend, edge, tunnel, held = false, releaseHold;
const hold = new Promise(resolve => { releaseHold = resolve; });
function releasePreviewHold() { if (previewHold.releasedElapsedMs === null) { previewHold.releasedElapsedMs = elapsedMs(); console.log('APPROVED_ORDER_PREVIEW_HOLD ' + JSON.stringify(previewHold)); } releaseHold(); }
function readPhase(read, phase, status = null) {
  if (!read) return; read.phase = phase; read.elapsedMs = elapsedMs() - read.startedElapsedMs; if (status !== null) read.status = status;
  console.log('APPROVED_ORDER_READ_DIAGNOSTIC ' + JSON.stringify(read));
}
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
function track(server) { server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); socket.on('error', () => socket.destroy()); }); return server; }
async function listen(server) { await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); }); return server.address().port; }
function kind(path) {
  if (path === '/api/v1/ordering/orders') return 'submit';
  if (/^\/api\/v1\/ordering\/reviews\/[1-9][0-9]*\/decisions$/.test(path)) return 'decision';
  if (/^\/api\/v1\/fulfillment\/preparations\/[1-9][0-9]*$/.test(path)) return 'preparation';
  if (/^\/api\/v1\/fulfillment\/dispatches\/[1-9][0-9]*$/.test(path)) return 'dispatch';
  return null;
}
function forward(req, res, port, isApi) {
  const headers = { ...req.headers, host: '127.0.0.1:' + port };
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded', 'connection', 'x-fixture-control'].includes(name)) delete headers[name];
  if (isApi) { headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https'; }
  const operation = isApi && req.method === 'POST' ? kind(req.url) : null;
  const recovery = isApi && req.method === 'POST' && req.url.endsWith('/operations/query'); let record;
  if (operation || recovery) {
    record = { kind: operation, path: req.url, actor: req.headers['x-expected-actor-id'], key: req.headers['idempotency-key'], version: req.headers['if-match'], status: null, body: null, response: null };
    (operation ? writes : recoveries).push(record); const chunks = []; let size = 0;
    req.on('data', chunk => { size += chunk.length; if (size <= 131072) chunks.push(chunk); }); req.once('end', () => { assert.ok(size <= 131072); record.body = JSON.parse(Buffer.concat(chunks).toString('utf8')); });
  }
  const drop = operation && fault.armed === operation; if (drop) fault.armed = null;
  const delay = isApi && !held && req.url === '/api/v1/ordering/orders/preview/query'; if (delay) held = true;
  const observe = isApi && req.url.startsWith('/api/v1/') && !req.url.startsWith('/api/v1/identity/') && !req.url.startsWith('/api/v1/access/');
  const read = observe ? { path: req.url.split('?')[0], method: req.method, startedElapsedMs: elapsedMs(), phase: 'initiated', status: null, elapsedMs: 0, bodyBytes: 0, held: delay } : null;
  if (read) { readTimings.push(read); readPhase(read, 'initiated'); }
  const upstream = http.request({ hostname: '127.0.0.1', port, path: req.url, method: req.method, headers }, response => {
    readPhase(read, 'headers', response.statusCode);
    if (record) record.status = response.statusCode;
    if (record || drop || delay || observe) {
      const chunks = []; let size = 0; response.on('data', chunk => { size += chunk.length; if (read) read.bodyBytes = size; if (size <= 131072) chunks.push(chunk); });
      response.once('end', async () => {
        assert.ok(size <= 131072); const body = Buffer.concat(chunks); let value = null;
        try { value = body.length ? JSON.parse(body.toString('utf8')) : null; } catch { /* An actual malformed upstream body remains observable. */ }
        if (record) record.response = value;
        if (record && response.statusCode === 200) committedResponses.emit(record.kind, record);
        if (observe) responses.push({ path: req.url, status: response.statusCode, value, noStore: response.headers['cache-control']?.includes('no-store') === true });
        readPhase(read, 'completed', response.statusCode);
        if (delay) { await hold; readPhase(read, 'hold-released', response.statusCode); } if (res.destroyed) { readPhase(read, 'client-closed'); return; }
        if (drop && response.statusCode === 200) {
          fault.dropped++; res.writeHead(200, { 'content-type': 'application/json', 'cache-control': 'private, no-store' });
          // First consume the genuine committed response, then lose only its
          // envelope. A finite malformed body avoids destroying Next's socket.
          res.end('{'); readPhase(read, 'lost-committed-result', response.statusCode); return;
        }
        res.writeHead(response.statusCode ?? 502, response.headers); res.end(body); readPhase(read, 'forwarded', response.statusCode);
      }); response.once('error', () => { readPhase(read, 'response-error'); if (!res.headersSent) res.writeHead(502); res.end(); }); return;
    }
    res.writeHead(response.statusCode ?? 502, response.headers); response.pipe(res);
  }); upstream.on('error', () => { readPhase(read, 'upstream-error'); if (!res.headersSent) res.writeHead(502); res.end(); }); req.once('aborted', () => { readPhase(read, 'request-aborted'); upstream.destroy(); }); req.pipe(upstream);
}
async function control(action) {
  assert.ok(['state', 'renew-buyer', 'renew-spender', 'renew-dispatcher'].includes(action));
  const response = await fetch(new URL('/__fixture/approved-order/' + action, api), { method: 'POST', headers: { 'X-Fixture-Control': config.controlKey }, redirect: 'error' });
  assert.equal(response.status, 200); return response.json();
}
async function user(name) {
  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1280, height: 960 }, locale: 'id-ID', timezoneId: 'Asia/Jakarta' });
  const page = await context.newPage(); pages.push(page); page.setDefaultTimeout(30000); page.on('pageerror', error => errors.push(error.message));
  await context.addCookies([{ name: '__Host-ut_session', value: config.tokens[name], domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  page.on('response', async response => {
    const path = new URL(response.url()).pathname;
    if (!['/api/v1/identity/session', '/api/v1/access/current'].includes(path) || new URL(response.url()).origin !== origin.origin) return;
    const safe = { participant: name, path, status: response.status() };
    try { const value = await response.json(); if (typeof value.code === 'string') safe.code = value.code.slice(0, 128);
      if (typeof value.authenticated === 'boolean') safe.authenticated = value.authenticated;
      if (Array.isArray(value.allowedActions)) safe.allowedActions = value.allowedActions.filter(item => typeof item === 'string').slice(0, 200);
    } catch { safe.jsonUnavailable = true; }
    diagnostics.push(safe); console.log('APPROVED_ORDER_ACCOUNT_DIAGNOSTIC ' + JSON.stringify(safe));
  }); return { name, context, page };
}
async function renew(person) {
  const { token } = await control('renew-' + person.name);
  await person.context.addCookies([{ name: '__Host-ut_session', value: token, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
}
async function query(person, path, body, options = {}) {
  return person.page.evaluate(async ({ actorId, path, body, options }) => {
    const issuer = await fetch('/api/v1/identity/csrf', { cache: 'no-store', credentials: 'same-origin' }); if (!issuer.ok) throw new Error('Request token unavailable');
    const csrf = await issuer.json(), headers = { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf.requestToken, 'X-Expected-Actor-Id': actorId, ...options.headers };
    for (const name of options.omit ?? []) delete headers[name];
    const response = await fetch(path, { method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', headers, body: JSON.stringify(body) });
    return { status: response.status, body: await response.json() };
  }, { actorId: config.actorIds[person.name], path, body, options });
}
async function capture(page, state) {
  const previous = page.viewportSize(); await mkdir(directory, { recursive: true });
  for (const viewport of [{ width: 1280, height: 960 }, { width: 360, height: 800 }]) {
    await page.setViewportSize(viewport); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, state + ' horizontal overflow');
    const file = config.proofPrefix + '-' + state + '-' + viewport.width + '.png'; const bytes = await page.screenshot({ path: resolve(directory, file), fullPage: true }); captures.push({ state, width: viewport.width, file, sha256: hash(bytes) });
  } await page.setViewportSize(previous);
}
async function pending(page, operation, record) {
  const entries = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  assert.equal(entries.length, 1); assert.deepEqual(Object.keys(entries[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.equal(entries[0].operation, operation); assert.equal(entries[0].actorId, record.actor); assert.equal(entries[0].key, record.key); return entries[0];
}
async function check(name, run) { await run(); checks.push(name); console.log('APPROVED_ORDER_BROWSER_CHECK ' + name); }
function success(operation) { return writes.filter(record => record.kind === operation && record.status === 200); }
function assertUnconsumed(state, before, orders = 1) {
  assert.equal(state.orders, orders); assert.equal(state.shipments, 0); assert.equal(state.journals, before.journals);
  assert.equal(state.transitions, before.transitions); assert.equal(state.onHand, before.onHand); assert.equal(state.remainingValue, config.expectedValue);
  assert.equal(state.valuePackages, before.valuePackages); assert.equal(state.financialDocumentsSha256, before.financialDocumentsSha256);
}
let submitRecord, dispatchRecord, orderId, suborderId;
try {
  await app.prepare(); const handle = app.getRequestHandler();
  frontend = track(http.createServer((req, res) => { void handle(req, res).catch(() => { res.statusCode = 500; res.end(); }); })); const frontendPort = await listen(frontend);
  edge = track(https.createServer({ pfx: Buffer.from(config.pfx, 'base64'), passphrase: config.password }, (req, res) => {
    if (req.headers.host !== origin.host || req.url.startsWith('/__fixture/')) { res.writeHead(403); res.end(); return; }
    const isApi = req.url.startsWith('/api/v1/') || req.url.startsWith('/auth/'); forward(req, res, isApi ? Number(api.port) : frontendPort, isApi);
  })); const tlsPort = await listen(edge);
  tunnel = track(http.createServer((_req, res) => { res.writeHead(403); res.end(); }));
  tunnel.on('connect', (req, socket, head) => {
    if (req.url !== origin.host + ':443') { socket.end('HTTP/1.1 403 Forbidden\r\n\r\n'); return; }
    const upstream = net.connect(tlsPort, '127.0.0.1', () => { socket.write('HTTP/1.1 200 Connection Established\r\n\r\n'); if (head.length) upstream.write(head); socket.pipe(upstream); upstream.pipe(socket); });
    sockets.add(upstream); upstream.on('close', () => sockets.delete(upstream)); upstream.on('error', () => socket.destroy()); socket.on('error', () => upstream.destroy()); socket.on('close', () => upstream.destroy());
  }); const proxyPort = await listen(tunnel);
  browser = await chromium.launch({ ...(channel === 'msedge' ? { channel: 'msedge' } : {}), headless: true, proxy: { server: 'http://127.0.0.1:' + proxyPort } });
  const buyer = await user('buyer'), spender = await user('spender'), dispatcher = await user('dispatcher'), before = await control('state');
  assert.deepEqual(before, config.baseline); assert.deepEqual([before.orders, before.reservations, before.decisions, before.preparations, before.shipments], [0, 0, 0, 0, 0]);
  await check('genuine empty buyer history and loading preview show desktop and mobile without a write', async () => {
    await buyer.page.goto(origin.origin + '/ordering/orders'); await buyer.page.getByText('Belum ada pesanan pada halaman ini.', { exact: true }).waitFor(); await capture(buyer.page, 'empty-order-history');
    await buyer.page.goto(origin.origin + '/ordering/checkout?draft=' + config.draftId);
    await buyer.page.getByRole('button', { name: 'Tinjau pesanan dan biaya', exact: true }).click();
    await buyer.page.getByRole('heading', { name: 'Pesanan menurut kanal toko', exact: true }).waitFor();
    await buyer.page.getByRole('button', { name: 'Tinjau pesanan dan biaya', exact: true }).waitFor();
    assert.equal(await buyer.page.getByRole('button', { name: 'Tinjau pesanan dan biaya', exact: true }).isDisabled(), true);
    assert.equal(await buyer.page.getByRole('button', { name: /^Ajukan (pesanan|untuk persetujuan)$/ }).count(), 0); await capture(buyer.page, 'checkout-loading'); releasePreviewHold();
    await buyer.page.getByRole('heading', { name: 'Periksa sebelum mengajukan', exact: true }).waitFor(); assert.equal(writes.length, 0);
    const review = responses.find(row => row.path === '/api/v1/ordering/orders/preview/query' && row.status === 200).value;
    assert.equal(review.lines.length, 1); assert.equal(review.lines[0].definition.skuId, config.skuId); assert.equal(review.lines[0].packages, config.packages);
    assert.equal(review.spendingState, 'Pending'); assert.ok(BigInt(review.tax.replace('.', '')) > 0n); assert.ok(BigInt(review.total.replace('.', '')) > 0n);
    assert.equal(await buyer.page.getByLabel('Kelompok pesanan', { exact: true }).evaluate(element => element === document.activeElement), true); await capture(buyer.page, 'checkout-review');
    await buyer.page.getByRole('button', { name: 'Batal pengajuan', exact: true }).click(); assert.equal(writes.length, 0);
    await buyer.page.getByRole('button', { name: 'Tinjau pesanan dan biaya', exact: true }).click(); await buyer.page.getByRole('heading', { name: 'Periksa sebelum mengajukan', exact: true }).waitFor();
  });
  await check('lost genuine first-order result resolves original key after reload with reservation and no value consumption', async () => {
    fault.armed = 'submit'; await buyer.page.getByRole('button', { name: /^Ajukan (pesanan|untuk persetujuan)$/ }).click();
    await buyer.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor(); submitRecord = success('submit')[0]; assert.ok(submitRecord);
    orderId = submitRecord.response.orderId; suborderId = submitRecord.response.suborderIds[0]; assert.equal(submitRecord.response.suborderIds.length, 1);
    assert.equal(submitRecord.actor, config.actorIds.buyer); assert.equal(submitRecord.version, '"0"'); await pending(buyer.page, 'ordering.order.submit', submitRecord);
    await capture(buyer.page, 'submit-uncertain'); const state = await control('state'); assertUnconsumed(state, before); assert.equal(state.reservations, 1);
    assert.equal(BigInt(state.reserved), BigInt(before.reserved) + BigInt(config.packages));
    await buyer.page.reload(); await buyer.page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
    await buyer.page.getByRole('heading', { name: 'Pesanan ' + orderId + ' tercatat', exact: true }).waitFor();
    assert.equal(success('submit').length, 1); assert.equal(writes.filter(record => record.key === submitRecord.key).length, 1);
    assert.ok(recoveries.some(record => record.path === '/api/v1/ordering/orders/operations/query' && record.body.key === submitRecord.key && record.response.status === 'committed' && record.response.result.orderId === orderId));
    await dispatcher.page.goto(origin.origin + '/identity/account'); const wrong = await query(dispatcher, '/api/v1/ordering/orders/operations/query', { purpose: 'operation-recovery', key: submitRecord.key }); assert.equal(wrong.status, 403); assert.equal(Object.hasOwn(wrong.body, 'result'), false);
    await buyer.page.goto(origin.origin + '/ordering/orders'); await buyer.page.getByRole('button', { name: 'Lihat pesanan ' + orderId, exact: true }).click();
    await buyer.page.getByRole('region', { name: 'Rincian pesanan', exact: true }).getByText('Menunggu persetujuan belanja', { exact: true }).waitFor(); await capture(buyer.page, 'submitted-order');
  });
  await check('independent spending then exact store acceptance precede preparation and physical dispatch', async () => {
    await renew(spender); await spender.page.goto(origin.origin + '/ordering/reviews'); await spender.page.getByRole('button', { name: 'Tinjau pesanan ' + orderId, exact: true }).click();
    let panel = spender.page.getByRole('region', { name: 'Tinjauan keputusan', exact: true }); await panel.getByLabel('Catatan peninjauan', { exact: true }).fill('Synthetic independent buyer spending review');
    await panel.getByRole('button', { name: 'Setujui belanja', exact: true }).click(); await capture(spender.page, 'spending-review');
    await panel.getByRole('button', { name: 'Konfirmasi persetujuan belanja', exact: true }).click();
    await panel.getByText('Peninjauan selesai atau tidak ada cadangan yang dapat dibatalkan.', { exact: true }).waitFor();
    await renew(dispatcher); await dispatcher.page.goto(origin.origin + '/ordering/reviews');
    await dispatcher.page.getByRole('button', { name: 'Tinjau pesanan ' + orderId + ' / ' + suborderId, exact: true }).click(); panel = dispatcher.page.getByRole('region', { name: 'Tinjauan keputusan', exact: true });
    await panel.getByLabel('Catatan peninjauan', { exact: true }).fill('Synthetic actual store acceptance'); await panel.getByRole('button', { name: 'Terima pesanan toko', exact: true }).click(); await capture(dispatcher.page, 'store-review');
    await panel.getByRole('button', { name: 'Konfirmasi penerimaan toko', exact: true }).click(); await panel.getByText('Bagian pesanan ini sudah diterima toko.', { exact: true }).waitFor();
    assert.equal(success('decision').length, 2); assert.deepEqual(success('decision').map(record => record.actor), [config.actorIds.spender, config.actorIds.dispatcher]);
    assert.deepEqual(success('decision').map(record => record.body.stage), ['Spending', 'Store']); assertUnconsumed(await control('state'), before);
    await dispatcher.page.goto(origin.origin + '/fulfillment/preparations'); await dispatcher.page.getByRole('button', { name: 'Siapkan pesanan ' + orderId + ' / ' + suborderId, exact: true }).click();
    panel = dispatcher.page.getByRole('region', { name: 'Rencana persiapan', exact: true }); assert.equal(await panel.getByRole('checkbox').isChecked(), false);
    await panel.getByRole('checkbox').check(); await panel.getByLabel(/^Jumlah /).fill(String(config.packages)); await panel.getByLabel('Catatan persiapan', { exact: true }).fill('Synthetic full actual reserved demand preparation');
    await panel.getByRole('button', { name: 'Tinjau persiapan', exact: true }).click(); await panel.getByRole('heading', { name: 'Periksa kemasan yang dipilih', exact: true }).waitFor(); await capture(dispatcher.page, 'preparation-review');
    await panel.getByRole('button', { name: 'Konfirmasi persiapan', exact: true }).click(); await panel.getByText('Persiapan versi 1 tersimpan.', { exact: true }).waitFor();
    assert.equal(success('preparation').length, 1); assertUnconsumed(await control('state'), before);
  });
  await check('explicit real lot courier and fresh dispatch loses one committed result then recovers original key without repeat', async () => {
    await renew(dispatcher); await dispatcher.page.goto(origin.origin + '/fulfillment/dispatches');
    await dispatcher.page.getByRole('button', { name: 'Periksa keberangkatan pesanan ' + orderId, exact: true }).click();
    const panel = dispatcher.page.getByRole('region', { name: 'Pemeriksaan keberangkatan', exact: true });
    const quantity = panel.getByLabel('Jumlah lot ' + config.lotId, { exact: true }), courier = panel.getByLabel('Kurir', { exact: true });
    assert.equal(await quantity.inputValue(), ''); assert.equal(await courier.inputValue(), '');
    await quantity.fill(String(config.packages)); await courier.selectOption(config.actorIds.courier); await panel.getByLabel('Catatan keberangkatan', { exact: true }).fill('Synthetic actual physical dispatch of reviewed reserved lot');
    await panel.getByRole('button', { name: 'Tinjau keberangkatan', exact: true }).click(); await panel.getByRole('heading', { name: 'Konfirmasi barang dan kurir', exact: true }).waitFor();
    const reviewed = responses.filter(row => row.path === '/api/v1/fulfillment/dispatches/' + suborderId + '/preview/query' && row.status === 200).at(-1).value;
    assert.equal(reviewed.courierId, config.actorIds.courier); assert.equal(reviewed.selected.length, 1); assert.equal(reviewed.selected[0].packages, config.packages);
    assert.equal(reviewed.preparation.lots.find(lot => lot.reservationId === reviewed.selected[0].reservationId).lotId, config.lotId);
    assert.equal(await panel.evaluate(element => element === document.activeElement), true); await capture(dispatcher.page, 'dispatch-review');
    await panel.getByRole('button', { name: 'Kembali ke lot dan kurir', exact: true }).click(); assert.equal(success('dispatch').length, 0);
    await panel.getByRole('button', { name: 'Tinjau keberangkatan', exact: true }).click();
    await panel.getByRole('heading', { name: 'Konfirmasi barang dan kurir', exact: true }).waitFor();
    await dispatcher.page.waitForFunction(() => [...document.querySelectorAll('button')].some(button => button.textContent?.trim() === 'Konfirmasi keberangkatan' && !button.disabled),
      undefined, { timeout: 30000 });
    const confirmDispatch = panel.getByRole('button', { name: 'Konfirmasi keberangkatan', exact: true });
    assert.equal(await confirmDispatch.isEnabled(), true);
    // Preview has its own existing UI wait. The unchanged dispatch deadline
    // starts only when the independently reviewed command is ready to confirm.
    fault.armed = 'dispatch';
    const dispatchEndsAt = performance.now() + 30000, dispatchDeadline = AbortSignal.timeout(30000);
    const committedDispatch = once(committedResponses, 'dispatch', { signal: dispatchDeadline });
    void committedDispatch.catch(() => {}); // Preserve the same deadline while the pending UI is inspected.
    await confirmDispatch.click();
    await dispatcher.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
    assert.equal(await dispatcher.page.getByRole('button', { name: 'Periksa hasil', exact: true }).isEnabled(), false);
    [dispatchRecord] = await committedDispatch;
    const recoveryRemaining = dispatchEndsAt - performance.now(); assert.ok(recoveryRemaining > 0, 'Dispatch must settle inside the original 30-second window');
    await dispatcher.page.waitForFunction(() => [...document.querySelectorAll('button')].some(button => button.textContent?.trim() === 'Periksa hasil' && !button.disabled),
      undefined, { timeout: recoveryRemaining });
    await pending(dispatcher.page, 'fulfillment.shipment.dispatch', dispatchRecord); await capture(dispatcher.page, 'dispatch-uncertain');
    assert.equal(dispatchRecord.actor, config.actorIds.dispatcher); assert.equal(dispatchRecord.version, '"1"');
    const committed = await control('state'); assert.equal(committed.shipments, 1); assert.equal(committed.journals, before.journals + 1); assert.equal(committed.transitions, before.transitions + 1);
    assert.equal(committed.remainingValue, '0.00'); assert.equal(committed.valuePackages, '0'); assert.equal(committed.financialDocumentsSha256, before.financialDocumentsSha256);
    await dispatcher.page.reload(); await dispatcher.page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await dispatcher.page.getByRole('heading', { name: 'Keberangkatan tercatat', exact: true }).waitFor();
    assert.equal(success('dispatch').length, 1); assert.equal(writes.filter(record => record.key === dispatchRecord.key).length, 1);
    assert.ok(recoveries.some(record => record.path === '/api/v1/fulfillment/dispatches/operations/query' && record.body.key === dispatchRecord.key && record.response.status === 'committed' && record.response.result.shipmentId === dispatchRecord.response.shipmentId));
    assert.deepEqual(await control('state'), committed); await capture(dispatcher.page, 'dispatch-recovered');
  });
  await check('buyer exact order and shipment history survive reload with current actor CSRF and fulfillment guards', async () => {
    await renew(buyer); await buyer.page.goto(origin.origin + '/ordering/orders'); await buyer.page.getByRole('button', { name: 'Lihat pesanan ' + orderId, exact: true }).click();
    const detail = buyer.page.getByRole('region', { name: 'Rincian pesanan', exact: true }); await detail.getByText('Diterima toko — menunggu pemenuhan', { exact: true }).waitFor();
    assert.equal(await detail.evaluate(element => element === document.activeElement), true); await capture(buyer.page, 'buyer-order-history');
    await buyer.page.reload(); await buyer.page.getByRole('button', { name: 'Lihat pesanan ' + orderId, exact: true }).click(); await detail.waitFor();
    const read = '/api/v1/ordering/orders/' + orderId + '/query', readBody = { purpose: 'order-preparation' };
    for (const [options, status] of [[{ omit: ['X-CSRF-Token'] }, 403], [{ headers: { 'X-Expected-Actor-Id': config.actorIds.dispatcher } }, 409]]) assert.equal((await query(buyer, read, readBody, options)).status, status);
    assert.equal((await query(buyer, '/api/v1/fulfillment/dispatches/' + suborderId + '/query', { purpose: 'shipment-dispatch' })).status, 403);
    await buyer.page.goto(origin.origin + '/fulfillment/shipments'); const result = dispatchRecord.response;
    const number = [result.numberEntity, result.numberChannel, result.numberYear, result.number].join('/');
    await buyer.page.getByRole('button', { name: 'Lihat kiriman ' + number, exact: true }).click(); const shipment = buyer.page.getByRole('region', { name: 'Rincian kiriman', exact: true });
    await shipment.getByRole('heading', { name: 'Kiriman ' + number, exact: true }).waitFor(); assert.equal(await shipment.evaluate(element => element === document.activeElement), true);
    const actual = responses.filter(row => row.path === '/api/v1/fulfillment/shipments/' + result.shipmentId + '/query' && row.status === 200).at(-1).value;
    assert.equal(actual.orderId, orderId); assert.equal(actual.suborderId, suborderId); assert.equal(actual.courierId, config.actorIds.courier); assert.equal(actual.lines[0].skuId, config.skuId); assert.equal(actual.lines[0].packages, config.packages);
    for (const privateField of ['journalId', 'valueEffects', 'admission', 'cost', 'stockEffects']) assert.equal(Object.hasOwn(actual, privateField), false);
    await capture(buyer.page, 'buyer-shipment-history'); await buyer.page.reload(); await buyer.page.getByRole('button', { name: 'Lihat kiriman ' + number, exact: true }).click(); await shipment.waitFor();
    assert.equal(success('submit').length, 1); assert.equal(success('dispatch').length, 1); assert.equal(fault.dropped, 2); assert.equal((await control('state')).movements, before.movements + 2);
  });
  assert.deepEqual(errors, []); assert.ok(responses.length); assert.equal(responses.every(response => response.noStore), true);
  await mkdir(directory, { recursive: true }); await writeFile(resolve(directory, config.proofPrefix + '-proof.json'), JSON.stringify({ recordedUtc: new Date().toISOString(), observedElapsedMs: elapsedMs(), buildId: config.buildId, checks, captures, diagnostics, readTimings, previewHold,
    orderId, shipmentId: dispatchRecord.response.shipmentId, originalSubmitKey: submitRecord.key, originalDispatchKey: dispatchRecord.key,
    lostCommittedResults: fault.dropped, successfulWrites: { submit: 1, decisions: 2, preparation: 1, dispatch: 1 }, exactFiniteValue: config.expectedValue,
    metadataOnlyRecovery: true, noSecondOriginalWrite: true, actualApprovedAdmissions: true, noInvoicePaymentOrIssuedDocument: true,
    clockFixture: config.clockFixture, localSyntheticIdentityOnly: true, providerAccepted: false, stagingValidated: false, productionAccepted: false }, null, 2));
  console.log('LOCAL_APPROVED_ORDER_DISPATCH_BROWSER_PASS ' + JSON.stringify({ checks: checks.length, captures: captures.length, lostResults: 2, orderId, shipmentId: dispatchRecord.response.shipmentId, localSyntheticIdentityOnly: true }));
} catch (error) {
  await mkdir(directory, { recursive: true }); for (const [index, page] of pages.entries()) try { await page.screenshot({ path: resolve(directory, config.proofPrefix + '-failure-' + index + '.png'), fullPage: true }); } catch { /* Preserve actual primary failure. */ }
  await writeFile(resolve(directory, config.proofPrefix + '-failure-diagnostics.json'), JSON.stringify({ observedElapsedMs: elapsedMs(), buildId: config.buildId, checks, diagnostics, readTimings, previewHold, statuses: responses.map(({ path, status }) => ({ path, status })) }, null, 2));
  console.error(error.stack ?? String(error)); process.exitCode = 1;
} finally {
  releasePreviewHold(); if (browser) await browser.close(); for (const socket of sockets) socket.destroy(); for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve)); await app.close();
}
