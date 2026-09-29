// Real local Next/Kestrel/Mongo rehearsal with synthetic approved policies and sessions. No live acceptance.
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import { createRequire } from 'node:module';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import next from 'next';

const chunks = []; for await (const chunk of process.stdin) chunks.push(chunk);
const config = JSON.parse(Buffer.concat(chunks).toString('utf8')), api = new URL(config.apiAddress), origin = new URL(config.origin);
assert.equal(process.versions.node.split('.')[0], '24'); assert.equal(api.protocol, 'http:'); assert.equal(api.hostname, '127.0.0.1'); assert.ok(Number(api.port) > 0);
assert.equal(origin.href, 'https://portal.unit-toko.co.id/');
const load = createRequire(config.playwrightPackage), metadata = load(config.playwrightPackage);
assert.equal(metadata.name, 'playwright'); assert.equal(metadata.version, '1.62.1');
const { chromium } = load('playwright'), channel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'chromium'; assert.ok(['chromium', 'msedge'].includes(channel));
const app = next({ dev: false, dir: process.cwd(), quiet: true }), sockets = new Set(), writes = [], errors = [], pages = [];
const directory = resolve('../tmp/production/order-submission-browser-results'); mkdirSync(directory, { recursive: true });
let browser, frontend, edge, tunnel, dropNext = false, dropped = 0;
function track(server) { server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); }); return server; }
async function listen(server) { await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); }); return server.address().port; }
function forward(req, res, port, isApi) {
  const headers = { ...req.headers, host: '127.0.0.1:' + port };
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded', 'connection'].includes(name)) delete headers[name];
  if (isApi) { headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https'; }
  const write = req.method === 'POST' && req.url === '/api/v1/ordering/orders';
  if (write) writes.push({ key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
  const drop = write && dropNext; if (drop) dropNext = false;
  const upstream = http.request({ hostname: '127.0.0.1', port, path: req.url, method: req.method, headers }, response => {
    if (drop && response.statusCode === 200) {
      response.resume(); response.once('end', () => { dropped++; res.writeHead(200, { 'content-type': 'application/json', 'content-length': '64', 'cache-control': 'no-store' });
        res.flushHeaders(); res.write('{'); setTimeout(() => res.destroy(), 25); }); return;
    }
    res.writeHead(response.statusCode ?? 502, response.headers); response.pipe(res);
  });
  upstream.on('error', () => { if (!res.headersSent) res.writeHead(502); res.end(); }); req.on('aborted', () => upstream.destroy()); req.pipe(upstream);
}
async function user(token) {
  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 360, height: 800 }, locale: 'id-ID', timezoneId: 'Asia/Jakarta' });
  await context.addCookies([{ name: '__Host-ut_session', value: token, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  const page = await context.newPage(); pages.push(page); page.setDefaultTimeout(15000); page.on('pageerror', error => errors.push(error.message));
  page.on('response', async response => { if (response.url().includes('/api/v1/') && response.status() >= 400) {
    const value = await response.json().catch(() => ({})); console.log('FIXTURE API ' + JSON.stringify({ path: new URL(response.url()).pathname, status: response.status(), code: value?.code }));
  } });
  return { context, page };
}
try {
  await app.prepare(); const handle = app.getRequestHandler();
  frontend = track(http.createServer((req, res) => { void handle(req, res).catch(() => { res.statusCode = 500; res.end(); }); })); const frontendPort = await listen(frontend);
  edge = track(https.createServer({ pfx: Buffer.from(config.pfx, 'base64'), passphrase: config.password }, (req, res) => {
    if (req.headers.host !== origin.host) { res.writeHead(403); res.end(); return; }
    const isApi = req.url.startsWith('/api/v1/') || req.url.startsWith('/auth/'); forward(req, res, isApi ? Number(api.port) : frontendPort, isApi);
  })); const tlsPort = await listen(edge);
  tunnel = track(http.createServer((_req, res) => { res.writeHead(403); res.end(); }));
  tunnel.on('connect', (req, socket, head) => {
    if (req.url !== origin.host + ':443') { socket.end('HTTP/1.1 403 Forbidden\r\n\r\n'); return; }
    const upstream = net.connect(tlsPort, '127.0.0.1', () => { socket.write('HTTP/1.1 200 Connection Established\r\n\r\n'); if (head.length) upstream.write(head); socket.pipe(upstream); upstream.pipe(socket); });
    sockets.add(upstream); upstream.on('close', () => sockets.delete(upstream)); upstream.on('error', () => socket.destroy()); socket.on('error', () => upstream.destroy()); socket.on('close', () => upstream.destroy());
  }); const proxyPort = await listen(tunnel);
  browser = await chromium.launch({ ...(channel === 'msedge' ? { channel: 'msedge' } : {}), headless: true, proxy: { server: 'http://127.0.0.1:' + proxyPort } });
  const buyer = await user(config.buyerToken), page = buyer.page;
  await page.goto(origin.origin + '/ordering/checkout?draft=' + config.draftId);
  await page.getByRole('button', { name: 'Tinjau pesanan dan biaya', exact: true }).click();
  const review = page.getByRole('region', { name: 'Rincian pesanan', exact: true }); await review.waitFor();
  assert.match(await review.innerText(), /2 Dus · 24 piece/); assert.match(await review.innerText(), /Total pesanan: IDR 25\.500,50/);
  assert.equal(await review.evaluate(element => element === document.activeElement), true);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'checkout-mobile.png'), fullPage: true });
  await page.getByRole('button', { name: 'Batal pengajuan', exact: true }).click(); assert.equal(writes.length, 0);
  await page.getByRole('button', { name: 'Tinjau pesanan dan biaya', exact: true }).click(); await review.waitFor();
  dropNext = true; await page.getByRole('button', { name: 'Konfirmasi ajukan pesanan', exact: true }).click();
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  const entries = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  assert.equal(entries.length, 1); assert.equal(entries[0].operation, 'ordering.order.submit'); assert.equal(entries[0].key, writes[0].key);
  assert.deepEqual(Object.keys(entries[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.equal(await page.getByRole('button', { name: 'Tinjau pesanan dan biaya', exact: true }).isDisabled(), true);
  await page.goto(origin.origin + '/ordering/checkout');
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  const confirmed = page.getByRole('region', { name: 'Pesanan tercatat', exact: true }); await confirmed.waitFor();
  await page.getByRole('heading', { name: 'Rincian pesanan tersimpan', exact: true }).waitFor();
  assert.match(await confirmed.innerText(), /Menunggu persetujuan belanja cabang/); assert.match(await confirmed.innerText(), /Stok pesanan telah dicadangkan/);
  assert.equal(writes.length, 1); assert.equal(dropped, 1);
  assert.equal(await page.getByRole('button', { name: 'Konfirmasi ajukan pesanan', exact: true }).count(), 0);
  assert.equal(await page.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
  await page.getByRole('button', { name: 'Muat kembali pesanan', exact: true }).click(); await page.getByRole('heading', { name: 'Rincian pesanan tersimpan', exact: true }).waitFor();
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'submitted-mobile.png'), fullPage: true });
  const orderId = (await confirmed.getByRole('heading').innerText()).match(/[0-9]+/)[0];
  const denied = await user(config.deniedToken); await denied.page.goto(origin.origin + '/ordering/checkout?draft=' + config.draftId);
  await denied.page.getByRole('heading', { name: 'Periksa sesi dan akses', exact: true }).waitFor();
  assert.equal(await denied.page.getByRole('region', { name: 'Rincian pesanan', exact: true }).count(), 0);
  const status = await denied.page.evaluate(async ({ orderId, actorId }) => {
    const csrf = await (await fetch('/api/v1/identity/csrf')).json();
    return (await fetch('/api/v1/ordering/orders/' + orderId + '/query', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf.requestToken, 'X-Expected-Actor-Id': actorId }, body: JSON.stringify({ purpose: 'order-preparation' }) })).status;
  }, { orderId, actorId: config.deniedId }); assert.equal(status, 403);
  assert.deepEqual(errors, []); console.log('REAL_ORDER_BROWSER_PASS ' + JSON.stringify({ droppedCommittedResponses: dropped, submitAttempts: writes.length, mobile: true, unauthorizedDetail: status }));
} catch (error) {
  for (const [index, page] of pages.entries()) if (!page.isClosed()) {
    console.error('FIXTURE PAGE ' + index + ': ' + (await page.locator('main').innerText().catch(() => 'unavailable')).slice(0, 5000));
    await page.screenshot({ path: resolve(directory, 'failure-' + index + '.png'), fullPage: true }).catch(() => {});
  }
  console.error(error.stack ?? String(error)); process.exitCode = 1;
} finally {
  let timer;
  try { await Promise.race([(async () => {
    if (browser) await browser.close(); for (const socket of sockets) socket.destroy();
    for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve)); await app.close();
  })(), new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Fixture cleanup exceeded 10 seconds')), 10000); })]); }
  catch (failure) { console.error(failure); process.exitCode = 1; } finally { clearTimeout(timer); }
}
process.exit(process.exitCode ?? 0);
