// Actual production Next + Kestrel + isolated Mongo. All accounts and approvals are synthetic.
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import { createRequire } from 'node:module';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { createHash } from 'node:crypto';
import next from 'next';

const chunks = []; for await (const chunk of process.stdin) chunks.push(chunk);
const config = JSON.parse(Buffer.concat(chunks).toString('utf8')), api = new URL(config.apiAddress), origin = new URL(config.origin);
assert.equal(process.versions.node.split('.')[0], '24'); assert.equal(api.protocol, 'http:'); assert.equal(api.hostname, '127.0.0.1'); assert.ok(Number(api.port) > 0); assert.equal(origin.href, 'https://portal.unit-toko.co.id/');
const load = createRequire(config.playwrightPackage), metadata = load(config.playwrightPackage); assert.equal(metadata.name, 'playwright'); assert.equal(metadata.version, '1.62.1');
const { chromium } = load('playwright'), channel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'chromium'; assert.ok(['chromium', 'msedge'].includes(channel));
const app = next({ dev: false, dir: process.cwd(), quiet: true }), sockets = new Set(), writes = [], errors = [], pages = [];
const directory = resolve('../tmp/production/payment-browser-results'); mkdirSync(directory, { recursive: true });
let browser, frontend, edge, tunnel, dropNext = null, dropped = 0;
function track(server) { server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); }); return server; }
async function listen(server) { await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); }); return server.address().port; }
function forward(req, res, port, isApi) {
  const headers = { ...req.headers, host: '127.0.0.1:' + port };
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded', 'connection'].includes(name)) delete headers[name];
  if (isApi) { headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https'; }
  const kind = req.method !== 'POST' ? null : req.url === '/api/v1/billing/payment-declarations' ? 'save' : /^\/api\/v1\/billing\/payment-declarations\/[1-9][0-9]*\/evidence$/.test(req.url) ? 'reserve' : req.url.endsWith('/content') ? 'content' : null; if (kind) writes.push({ kind, key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
  const drop = kind !== null && kind === dropNext; if (drop) dropNext = null;
  const upstream = http.request({ hostname: '127.0.0.1', port, path: req.url, method: req.method, headers }, response => {
    if (drop && response.statusCode === 200) { response.resume(); response.once('end', () => { dropped++; res.writeHead(200, { 'content-type': 'application/json', 'content-length': '64', 'cache-control': 'no-store' }); res.flushHeaders(); res.write('{'); setTimeout(() => res.destroy(), 25); }); return; }
    res.writeHead(response.statusCode ?? 502, response.headers); response.pipe(res);
  }); upstream.on('error', () => { if (!res.headersSent) res.writeHead(502); res.end(); }); req.on('aborted', () => upstream.destroy()); req.pipe(upstream);
}
async function user(token) {
  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 360, height: 800 }, locale: 'id-ID', timezoneId: 'Asia/Jakarta' });
  await context.addCookies([{ name: '__Host-ut_session', value: token, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  const page = await context.newPage(); pages.push(page); page.setDefaultTimeout(15000); page.on('pageerror', error => errors.push(error.message));
  page.on('response', async response => { if (response.url().includes('/api/v1/') && response.status() >= 400) { const value = await response.json().catch(() => ({})); console.log('FIXTURE API ' + JSON.stringify({ path: new URL(response.url()).pathname, status: response.status(), code: value?.code })); } }); return { context, page };
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
  await page.goto(origin.origin + '/identity/account'); await page.getByRole('link', { name: /^Bukti pembayaran\b/ }).click();
  const form = page.getByRole('region', { name: 'Draf pembayaran', exact: true });
  await form.getByRole('checkbox').check(); await form.getByLabel('Jumlah transfer (IDR)', { exact: true }).fill('4000.01');
  await form.getByLabel('Tanggal transfer', { exact: true }).fill(config.transferDate); await form.getByLabel('Nama pengirim', { exact: true }).fill('Pembayar fixture');
  await form.getByLabel('Alasan pencatatan atau perubahan', { exact: true }).fill('Bukti transfer cabang uji lokal');
  await buyer.context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await form.getByRole('button', { name: 'Simpan draf pembayaran', exact: true }).isDisabled(), true); assert.equal(writes.length, 0);
  await buyer.context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
  dropNext = 'save'; await form.getByRole('button', { name: 'Simpan draf pembayaran', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  const refs = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  assert.equal(refs.length, 1); assert.equal(refs[0].operation, 'billing.payment.draft.save'); assert.deepEqual(Object.keys(refs[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await form.getByRole('heading', { name: /^Draf pembayaran [1-9]/ }).waitFor();
  await page.waitForFunction(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).length === 0);
  assert.equal(writes.filter(item => item.kind === 'save').length, 1);
  await form.getByLabel('Jumlah transfer (IDR)', { exact: true }).fill('2000.01'); await form.getByLabel('Alasan pencatatan atau perubahan', { exact: true }).fill('Koreksi nilai deklarasi');
  await form.getByRole('button', { name: 'Simpan draf pembayaran', exact: true }).click(); await page.getByText(/^Draf tersimpan\./).waitFor();
  const image = { name: 'bukti-asli.png', mimeType: 'image/png', buffer: Buffer.from(config.imageBase64, 'base64') };
  const proof = page.getByRole('region', { name: 'Bukti asli pembayaran', exact: true });
  await proof.getByLabel('Pilih bukti pembayaran', { exact: true }).setInputFiles(image);
  dropNext = 'reserve'; await proof.getByRole('button', { name: 'Unggah bukti asli', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(writes.filter(item => item.kind === 'content').length, 0);
  await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await proof.getByText('Siap menerima berkas', { exact: true }).waitFor();
  await page.waitForFunction(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).length === 0);
  await proof.getByLabel('Pilih bukti pembayaran', { exact: true }).setInputFiles(image);
  dropNext = 'content'; await proof.getByRole('button', { name: 'Lanjutkan unggah ini', exact: true }).click(); await proof.getByText('Tersimpan · menunggu pemeriksaan keamanan', { exact: true }).waitFor();
  console.log('VALIDATE_EVIDENCE_READY');
  const decoded = proof.getByText('Pemeriksaan keamanan selesai. Bukti tetap dalam draf; pembayaran belum diverifikasi.', { exact: true });
  for (let attempt = 0; attempt < 20 && !await decoded.isVisible(); attempt++) {
    await proof.getByRole('button', { name: 'Muat status bukti', exact: true }).click();
    await decoded.waitFor({ timeout: 1000 }).catch(() => {});
  }
  await decoded.waitFor();
  const readResponse = page.waitForResponse(response => response.url().endsWith('/original/query'));
  await proof.getByRole('button', { name: 'Lihat bukti asli', exact: true }).click();
  const originalResponse = await readResponse; assert.equal(originalResponse.status(), 200);
  const originalHeaders = originalResponse.headers(); assert.equal(originalHeaders['content-type'], 'image/png'); assert.match(originalHeaders['cache-control'], /no-store/);
  assert.equal(originalHeaders['x-content-type-options'], 'nosniff'); assert.equal(originalHeaders['content-security-policy'], "default-src 'none'; sandbox");
  assert.match(originalHeaders['content-disposition'], /^attachment;/); assert.equal(originalHeaders['referrer-policy'], 'no-referrer');
  assert.equal(createHash('sha256').update(await originalResponse.body()).digest('hex'), createHash('sha256').update(image.buffer).digest('hex'));
  const preview = page.getByRole('region', { name: /^Bukti asli · unggah/ }); await preview.waitFor();
  const originalImage = preview.getByRole('img'); await originalImage.evaluate(image => image.decode());
  assert.deepEqual(await originalImage.evaluate(image => [image.naturalWidth, image.naturalHeight]), [13, 7]);
  const originalUrl = await originalImage.getAttribute('src'); assert.match(originalUrl, /^blob:/); assert.equal(await originalImage.getAttribute('srcset'), null);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'payment-original-mobile.png'), fullPage: true });
  await preview.getByRole('button', { name: 'Tutup bukti', exact: true }).click(); await preview.waitFor({ state: 'hidden' });
  assert.equal(await proof.getByRole('button', { name: 'Lihat bukti asli', exact: true }).evaluate(element => document.activeElement === element), true);
  assert.equal(await page.evaluate(async url => { try { await fetch(url); return false; } catch { return true; } }, originalUrl), true);
  await proof.getByRole('button', { name: 'Lihat bukti asli', exact: true }).click(); await preview.waitFor();
  await form.getByLabel('Catatan (opsional)', { exact: true }).fill('Perubahan draf uji'); await preview.waitFor({ state: 'hidden' });
  assert.equal(await proof.getByRole('button', { name: 'Lihat bukti asli', exact: true }).isDisabled(), true);
  await form.getByLabel('Catatan (opsional)', { exact: true }).fill('');
  assert.equal(writes.filter(item => item.kind === 'save').length, 2); assert.equal(writes.filter(item => item.kind === 'reserve').length, 1); assert.equal(writes.filter(item => item.kind === 'content').length, 1);
  assert.equal(dropped, 3); assert.ok(writes.every(item => item.actor === config.buyerId)); assert.equal(await page.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'payment-buyer-mobile.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.evaluate(async () => { await document.fonts.ready; await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))); });
  await page.screenshot({ path: resolve(directory, 'payment-buyer-desktop.png'), fullPage: true });
  const outsider = await user(config.outsiderToken); await outsider.page.goto(origin.origin + '/billing/payments'); await outsider.page.getByRole('heading', { name: 'Periksa sesi dan akses', exact: true }).waitFor();
  assert.equal(await outsider.page.getByRole('button', { name: /^Buka draf/ }).count(), 0);
  assert.deepEqual(errors, []); console.log('REAL_PAYMENT_BROWSER_PASS ' + JSON.stringify({ draftWrites: 2, originalUploads: 1, durableValidation: true, privateOriginalRead: true, exactHash: true, blobRevoked: true, realDecoder: true, syntheticScanner: true, lostResponsesRecovered: dropped, outsiderDenied: true, offlineUnsent: true, mobile: true }));
} catch (error) {
  for (const [index, page] of pages.entries()) if (!page.isClosed()) { console.error('FIXTURE PAGE ' + index + ': ' + (await page.locator('main').innerText().catch(() => 'unavailable')).slice(0, 5000)); await page.screenshot({ path: resolve(directory, 'failure-' + index + '.png'), fullPage: true }).catch(() => {}); }
  console.error(error.stack ?? String(error)); process.exitCode = 1;
} finally {
  let timer;
  try { await Promise.race([(async () => { if (browser) await browser.close(); for (const socket of sockets) socket.destroy(); for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve)); await app.close(); })(), new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Fixture cleanup exceeded 10 seconds')), 10000); })]); }
  catch (failure) { console.error(failure); process.exitCode = 1; } finally { clearTimeout(timer); }
}
process.exit(process.exitCode ?? 0);
