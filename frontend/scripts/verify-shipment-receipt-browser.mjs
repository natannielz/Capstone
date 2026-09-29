// Real Next/Kestrel/Mongo local rehearsal; identities and operational approvals are synthetic.
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
const load = createRequire(config.playwrightPackage), metadata = load(config.playwrightPackage); assert.equal(metadata.name, 'playwright'); assert.equal(metadata.version, '1.62.1');
const { chromium } = load('playwright'), channel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'chromium'; assert.ok(['chromium', 'msedge'].includes(channel));
const app = next({ dev: false, dir: process.cwd(), quiet: true }), sockets = new Set(), writes = [], errors = [], pages = [];
const directory = resolve('../tmp/production/shipment-receipt-browser-results'); mkdirSync(directory, { recursive: true });
let browser, frontend, edge, tunnel, dropNext = false, dropped = 0, dropDocument = false;
const documentWrites = [];
function track(server) { server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); }); return server; }
async function listen(server) { await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); }); return server.address().port; }
function forward(req, res, port, isApi) {
  const headers = { ...req.headers, host: '127.0.0.1:' + port };
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded', 'connection'].includes(name)) delete headers[name];
  if (isApi) { headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https'; }
  const write = req.method === 'POST' && /^\/api\/v1\/fulfillment\/receipts\/challenges\/[0-9]+\/confirm$/.test(req.url);
  if (write) writes.push({ key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
  const documentWrite = req.method === 'POST' && /^\/api\/v1\/fulfillment\/receipts\/[0-9]+\/challenges$/.test(req.url);
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
  const courier = await user(config.courierToken), page = courier.page;
  await page.goto(origin.origin + '/fulfillment/shipments');
  await page.getByRole('button', { name: 'Lihat kiriman 100/103/2026/1', exact: true }).click();
  const panel = page.getByRole('region', { name: 'Penerimaan kiriman', exact: true });
  await panel.getByLabel('Penerima terdaftar', { exact: true }).selectOption(config.buyerId);
  await panel.getByLabel('Catatan QR', { exact: true }).fill('Penerima memeriksa barang di cabang');
  await panel.getByRole('button', { name: 'Tinjau penerima', exact: true }).click();
  await panel.getByRole('button', { name: 'Kembali ke penerima', exact: true }).click(); assert.equal(documentWrites.length, 0);
  await panel.getByRole('button', { name: 'Tinjau penerima', exact: true }).click();
  await panel.getByRole('button', { name: 'Buat QR penerimaan', exact: true }).click();
  const link = panel.getByRole('link', { name: 'Buka tautan penerimaan', exact: true }); await link.waitFor();
  const href = await link.getAttribute('href'); assert.match(href, /^https:\/\/portal\.unit-toko\.co\.id\/fulfillment\/receive#[1-9][0-9]*\.[0-9a-f]{64}$/);
  const canvas = panel.getByRole('img', { name: 'QR penerimaan untuk akun yang dipilih', exact: true });
  await page.waitForFunction(() => { const canvas = document.querySelector('canvas'); return canvas && canvas.width === 256; });
  assert.equal(await canvas.evaluate(element => element.getContext('2d').getImageData(0, 0, 256, 256).data.some((value, index) => index % 4 !== 3 && value === 0)), true);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'courier-receipt-qr-mobile.png'), fullPage: true });
  const denied = await courier.context.newPage(); pages.push(denied); await denied.goto(href);
  await denied.getByRole('heading', { name: 'Masuk sebagai penerima', exact: true }).waitFor();
  assert.equal(new URL(denied.url()).hash, '');
  const buyer = await user(config.buyerToken), recipient = buyer.page; await recipient.goto(href);
  await recipient.getByRole('button', { name: 'Periksa kiriman', exact: true }).click();
  await recipient.getByRole('heading', { name: 'Hitung barang yang tiba', exact: true }).waitFor();
  assert.equal(new URL(recipient.url()).hash, '');
  assert.equal(await recipient.getByLabel('Diterima — kemasan utuh', { exact: true }).inputValue(), '');
  for (const [label, packages, loose] of [['Diterima', '0', '6'], ['Ditolak', '0', '4'], ['Belum diterima', '0', '2']]) {
    await recipient.getByLabel(label + ' — kemasan utuh', { exact: true }).fill(packages);
    await recipient.getByLabel(label + ' — satuan lepas', { exact: true }).fill(loose);
  }
  await recipient.getByLabel('Alasan perbedaan', { exact: true }).fill('Empat satuan rusak dan dua belum tiba');
  await recipient.getByLabel('Catatan penerimaan', { exact: true }).fill('Hitungan diperiksa bersama kurir');
  await recipient.getByRole('button', { name: 'Tinjau jumlah', exact: true }).click();
  const heading = recipient.getByRole('heading', { name: 'Tinjau jumlah penerimaan', exact: true }); await heading.waitFor();
  assert.equal(await heading.evaluate(element => element === document.activeElement), true);
  assert.equal(await recipient.getByRole('button', { name: 'Konfirmasi penerimaan', exact: true }).isDisabled(), true);
  const check = recipient.getByRole('checkbox'); assert.equal(await check.isChecked(), false); await check.check();
  await buyer.context.setOffline(true); await recipient.getByText(/Koneksi terputus. Isian hanya ada/).waitFor();
  assert.equal(await recipient.getByRole('button', { name: 'Konfirmasi penerimaan', exact: true }).isDisabled(), true); assert.equal(writes.length, 0);
  await buyer.context.setOffline(false); await recipient.getByText(/Koneksi terputus. Isian hanya ada/).waitFor({ state: 'hidden' });
  assert.equal(await recipient.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await recipient.screenshot({ path: resolve(directory, 'recipient-review-mobile.png'), fullPage: true });
  dropNext = true; await recipient.getByRole('button', { name: 'Konfirmasi penerimaan', exact: true }).click();
  await recipient.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  const entries = await recipient.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  assert.equal(entries.length, 1); assert.equal(entries[0].operation, 'fulfillment.receipt.confirm'); assert.equal(entries[0].key, writes[0].key);
  assert.deepEqual(Object.keys(entries[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  const secret = href.split('.').at(-1);
  assert.equal(await recipient.evaluate(token => JSON.stringify(localStorage).includes(token) || JSON.stringify(sessionStorage).includes(token), secret), false);
  await recipient.reload(); await recipient.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  await recipient.getByRole('heading', { name: 'Penerimaan tersimpan', exact: true }).waitFor();
  await recipient.getByText(/^Permintaan telah tersimpan;/).waitFor();
  assert.equal(writes.length, 1); assert.equal(writes[0].actor, config.buyerId); assert.equal(dropped, 1); assert.equal(documentWrites.length, 1);
  assert.equal(await recipient.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
  assert.equal(await recipient.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).length), 0);
  await recipient.screenshot({ path: resolve(directory, 'recipient-recovered-mobile.png'), fullPage: true });
  await panel.getByRole('button', { name: 'Muat penerimaan', exact: true }).click();
  await panel.getByRole('heading', { name: 'Penerimaan tersimpan', exact: true }).waitFor();
  assert.match(await panel.innerText(), /diterima 6, ditolak 4, belum diterima 2/);
  assert.equal(await panel.getByRole('button', { name: 'Tinjau penerima', exact: true }).count(), 0);
  assert.deepEqual(errors, []); console.log('REAL_SHIPMENT_RECEIPT_BROWSER_PASS ' + JSON.stringify({ confirmationAttempts: writes.length, recovered: dropped, separateRecipient: true, explicitCounts: true, offlineUnsent: true, mobile: true }));
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
