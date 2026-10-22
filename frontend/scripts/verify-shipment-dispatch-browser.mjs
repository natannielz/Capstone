// Real Next/Kestrel/Mongo local rehearsal; identities and operational approvals are synthetic.
import assert from 'node:assert/strict';
import { openDestination } from './open-destination.mjs';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import { createRequire } from 'node:module';
import { mkdirSync, readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { resolve } from 'node:path';
import next from 'next';

const chunks = []; for await (const chunk of process.stdin) chunks.push(chunk);
const config = JSON.parse(Buffer.concat(chunks).toString('utf8')), api = new URL(config.apiAddress), origin = new URL(config.origin);
const orderNumber = id => 'PSN-' + String(id).padStart(6, '0');
assert.equal(process.versions.node.split('.')[0], '24'); assert.equal(api.protocol, 'http:'); assert.equal(api.hostname, '127.0.0.1'); assert.ok(Number(api.port) > 0);
assert.equal(origin.href, 'https://portal.unit-toko.co.id/');
const load = createRequire(config.playwrightPackage), metadata = load(config.playwrightPackage); assert.equal(metadata.name, 'playwright'); assert.equal(metadata.version, '1.62.1');
const { chromium } = load('playwright'), channel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'chromium'; assert.ok(['chromium', 'msedge'].includes(channel));
const app = next({ dev: false, dir: process.cwd(), quiet: true }), sockets = new Set(), writes = [], errors = [], pages = [];
const directory = resolve('../tmp/production/shipment-dispatch-browser-results'); mkdirSync(directory, { recursive: true });
let browser, frontend, edge, tunnel, dropNext = false, dropped = 0, dropDocument = false;
const documentWrites = [];
function track(server) { server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); }); return server; }
async function listen(server) { await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); }); return server.address().port; }
function forward(req, res, port, isApi) {
  const headers = { ...req.headers, host: '127.0.0.1:' + port };
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded', 'connection'].includes(name)) delete headers[name];
  if (isApi) { headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https'; }
  const write = req.method === 'POST' && /^\/api\/v1\/fulfillment\/dispatches\/[0-9]+$/.test(req.url);
  if (write) writes.push({ key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
  const documentWrite = req.method === 'POST' && /^\/api\/v1\/fulfillment\/shipment-documents\/[0-9]+$/.test(req.url);
  if (documentWrite) documentWrites.push({ key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
  const drop = write && dropNext || documentWrite && dropDocument; if (drop) { dropNext = false; dropDocument = false; }
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
  } }); return { context, page };
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
  const staff = await user(config.dispatcherToken), page = staff.page;
  await page.goto(origin.origin + '/fulfillment/dispatches');
  await page.getByRole('button', { name: 'Periksa keberangkatan pesanan ' + orderNumber(config.orderId), exact: true }).click();
  const review = page.getByRole('region', { name: 'Pemeriksaan keberangkatan', exact: true }); await review.waitFor();
  assert.match(await review.innerText(), /1 Dus · total 12 piece/); assert.equal(await review.evaluate(element => element === document.activeElement), true);
  assert.equal(await review.getByLabel('Jumlah lot 202', { exact: true }).inputValue(), ''); assert.equal(await review.getByLabel('Kurir', { exact: true }).inputValue(), '');
  await review.getByLabel('Jumlah lot 202', { exact: true }).fill('1'); await review.getByLabel('Kurir', { exact: true }).selectOption(config.courierId);
  await review.getByLabel('Catatan keberangkatan', { exact: true }).fill('Kemasan dan lot fisik sesuai persiapan');
  await review.getByRole('button', { name: 'Tinjau keberangkatan', exact: true }).click();
  await review.getByRole('heading', { name: 'Konfirmasi barang dan kurir', exact: true }).waitFor();
  assert.match(await review.innerText(), /Kurir fixture/); assert.match(await review.innerText(), /Lot 202 · 1 Dus/);
  await review.getByRole('button', { name: 'Kembali ke lot dan kurir', exact: true }).click(); assert.equal(writes.length, 0);
  await review.getByRole('button', { name: 'Tinjau keberangkatan', exact: true }).click();
  await review.getByRole('heading', { name: 'Konfirmasi barang dan kurir', exact: true }).waitFor();
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'dispatch-review-mobile.png'), fullPage: true });
  dropNext = true; await review.getByRole('button', { name: 'Konfirmasi keberangkatan', exact: true }).click();
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(await page.getByRole('button', { name: 'Muat persiapan terbaru', exact: true }).isDisabled(), true);
  const entries = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  assert.equal(entries.length, 1); assert.equal(entries[0].operation, 'fulfillment.shipment.dispatch'); assert.equal(entries[0].key, writes[0].key);
  assert.deepEqual(Object.keys(entries[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  await page.getByRole('heading', { name: 'Keberangkatan tercatat', exact: true }).waitFor();
  await page.getByText('Sisa pesanan berubah. Perbarui persiapan untuk pengiriman berikutnya.', { exact: true }).waitFor();
  assert.equal(writes.length, 1); assert.equal(dropped, 1); assert.equal(writes[0].actor, config.dispatcherId);
  assert.equal(await page.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
  assert.equal(await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).length), 0);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'dispatch-recovered-mobile.png'), fullPage: true });
  const denied = await user(config.deniedToken); await denied.page.goto(origin.origin + '/fulfillment/dispatches');
  await denied.page.getByRole('heading', { name: /^(Masuk untuk melanjutkan|Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor();
  const status = await denied.page.evaluate(async ({ suborderId, actorId }) => {
    const csrf = await (await fetch('/api/v1/identity/csrf')).json();
    return (await fetch('/api/v1/fulfillment/dispatches/' + suborderId + '/query', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf.requestToken, 'X-Expected-Actor-Id': actorId }, body: JSON.stringify({ purpose: 'shipment-dispatch' }) })).status;
  }, { suborderId: config.suborderId, actorId: config.deniedId }); assert.equal(status, 403);
  await denied.page.goto(origin.origin + '/fulfillment/shipments');
  await denied.page.getByRole('button', { name: 'Lihat kiriman 100/103/2026/1', exact: true }).click();
  const documentPanel = denied.page.getByRole('region', { name: 'Surat Jalan', exact: true });
  await documentPanel.getByLabel('Catatan penerbitan', { exact: true }).fill('Surat Jalan sesuai kemasan dan tujuan yang diberangkatkan');
  await documentPanel.getByRole('button', { name: 'Tinjau Surat Jalan', exact: true }).click();
  await documentPanel.getByRole('button', { name: 'Kembali ke catatan', exact: true }).click(); assert.equal(documentWrites.length, 0);
  await documentPanel.getByRole('button', { name: 'Tinjau Surat Jalan', exact: true }).click();
  dropDocument = true; await documentPanel.getByRole('button', { name: 'Terbitkan Surat Jalan', exact: true }).click();
  await documentPanel.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  await denied.page.reload(); await denied.page.getByRole('button', { name: 'Lihat kiriman 100/103/2026/1', exact: true }).click();
  await documentPanel.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  await documentPanel.getByText(/^Permintaan telah tersimpan;/).waitFor();
  await documentPanel.getByText('Surat Jalan versi 1 tersimpan.', { exact: true }).waitFor();
  assert.equal(documentWrites.length, 1); assert.equal(documentWrites[0].actor, config.deniedId); assert.equal(dropped, 2);
  assert.equal(await documentPanel.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
  assert.equal(await denied.page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).length), 0);
  let acceptedFileHash;
  for (const [label, token] of [['courier', config.courierToken], ['buyer', config.buyerToken]]) {
    const reader = await user(token), readPage = reader.page;
    await openDestination(readPage, origin, 'Kiriman');
    const open = readPage.getByRole('button', { name: 'Lihat kiriman 100/103/2026/1', exact: true }); await open.click();
    const detail = readPage.getByRole('region', { name: 'Rincian kiriman', exact: true }); await detail.waitFor();
    assert.match(await detail.innerText(), /1 Dus · isi 12 piece per kemasan · total 12 piece/);
    assert.match(await detail.innerText(), /Alamat cabang sintetis 1/);
    await detail.getByText('Keberangkatan tercatat. Penerimaan barang oleh cabang belum dicatat.', { exact: true }).waitFor();
    assert.equal(await detail.evaluate(element => element === document.activeElement), true);
    const documents = detail.getByRole('region', { name: 'Surat Jalan', exact: true });
    await documents.getByText('Surat Jalan versi 1 tersimpan.', { exact: true }).waitFor();
    assert.equal(await documents.getByRole('button', { name: 'Terbitkan Surat Jalan', exact: true }).count(), 0);
    const [download] = await Promise.all([readPage.waitForEvent('download'), documents.getByRole('button', { name: 'Unduh Surat Jalan', exact: true }).click()]);
    assert.match(download.suggestedFilename(), /^surat-jalan-[0-9]+-v1-[0-9]+\.html$/);
    const path = resolve(directory, 'surat-jalan-' + label + '.html'); await download.saveAs(path);
    const bytes = readFileSync(path), fileHash = createHash('sha256').update(bytes).digest('hex');
    assert.match(bytes.toString('utf8'), /Alamat cabang sintetis 1/); assert.match(bytes.toString('utf8'), /Penjual fixture/);
    assert.doesNotMatch(bytes.toString('utf8'), /7000\.25|<script/);
    if (acceptedFileHash) assert.equal(fileHash, acceptedFileHash); acceptedFileHash = fileHash;
    if (label === 'courier') {
      const copy = await reader.context.newPage(); await copy.setContent(bytes.toString('utf8'));
      assert.equal(await copy.getByRole('heading', { name: 'Surat Jalan', exact: true }).count(), 1);
      assert.equal(await copy.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
      await copy.screenshot({ path: resolve(directory, 'surat-jalan-mobile.png'), fullPage: true });
      await copy.setViewportSize({ width: 1000, height: 1200 }); await copy.emulateMedia({ media: 'print' });
      await copy.screenshot({ path: resolve(directory, 'surat-jalan-print.png'), fullPage: true }); await copy.close();
    }
    assert.equal(await readPage.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await readPage.screenshot({ path: resolve(directory, 'shipment-' + label + '-mobile.png'), fullPage: true });
    await detail.getByRole('button', { name: 'Tutup rincian', exact: true }).click();
    assert.equal(await open.evaluate(element => element === document.activeElement), true);
    await readPage.getByRole('button', { name: 'Muat kiriman terbaru', exact: true }).click(); await open.waitFor();
    await reader.context.close();
  }
  assert.equal(writes.length, 1);
  assert.equal(documentWrites.length, 1);
  assert.deepEqual(errors, []); console.log('REAL_SHIPMENT_DISPATCH_BROWSER_PASS ' + JSON.stringify({ dispatchAttempts: writes.length, recovered: dropped, explicitLot: true, explicitCourier: true, mobile: true, unauthorizedDetail: status }));
} catch (error) {
  for (const [index, page] of pages.entries()) if (!page.isClosed()) {
    console.error('FIXTURE PAGE ' + index + ': ' + (await page.locator('main').innerText().catch(() => 'unavailable')).slice(0, 5000));
    await page.screenshot({ path: resolve(directory, 'failure-' + index + '.png'), fullPage: true }).catch(() => {});
  } console.error(error.stack ?? String(error)); process.exitCode = 1;
} finally {
  let timer;
  try { await Promise.race([(async () => { if (browser) await browser.close(); for (const socket of sockets) socket.destroy();
    for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve)); await app.close();
  })(), new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Fixture cleanup exceeded 10 seconds')), 10000); })]); }
  catch (failure) { console.error(failure); process.exitCode = 1; } finally { clearTimeout(timer); }
}
process.exit(process.exitCode ?? 0);
