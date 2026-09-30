// Actual production Next + Kestrel + isolated Mongo. All accounts and approvals are synthetic.
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
assert.equal(process.versions.node.split('.')[0], '24'); assert.equal(api.protocol, 'http:'); assert.equal(api.hostname, '127.0.0.1'); assert.ok(Number(api.port) > 0); assert.equal(origin.href, 'https://portal.unit-toko.co.id/');
const load = createRequire(config.playwrightPackage), metadata = load(config.playwrightPackage); assert.equal(metadata.name, 'playwright'); assert.equal(metadata.version, '1.62.1');
const { chromium } = load('playwright'), channel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'chromium'; assert.ok(['chromium', 'msedge'].includes(channel));
const app = next({ dev: false, dir: process.cwd(), quiet: true }), sockets = new Set(), writes = [], errors = [], pages = [];
const directory = resolve('../tmp/production/bank-statement-browser-results'); mkdirSync(directory, { recursive: true });
let browser, frontend, edge, tunnel, dropNext = null, dropped = 0;
function track(server) { server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); }); return server; }
async function listen(server) { await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); }); return server.address().port; }
function forward(req, res, port, isApi) {
  const headers = { ...req.headers, host: '127.0.0.1:' + port };
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded', 'connection'].includes(name)) delete headers[name];
  if (isApi) { headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https'; }
  const kind = req.method === 'POST' && req.url === '/api/v1/billing/bank-statements/imports' ? 'stage' : null; if (kind) writes.push({ kind, key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
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

  async function desk(token) { const actor = await user(token); await actor.page.goto(origin.origin + '/identity/account'); await actor.page.getByRole('link', { name: /^Mutasi bank\b/ }).click(); await actor.page.getByRole('heading', { name: 'Mutasi bank', exact: true }).waitFor(); return actor; }
  const maker = await desk(config.makerToken), page = maker.page;
  await page.getByRole('button', { name: 'Siapkan impor mutasi', exact: true }).click();
  const form = page.getByRole('region', { name: 'Siapkan impor bank', exact: true });
  await form.getByLabel('Format ekspor bank', { exact: true }).selectOption('910');
  await form.getByLabel('Rekening penerimaan dan versi', { exact: true }).selectOption(config.accountVersionId);
  assert.equal(await form.getByLabel('Rekening penerimaan dan versi', { exact: true }).inputValue(), config.accountVersionId);
  const original = Buffer.from(config.original, 'base64');
  await form.getByLabel('Berkas mutasi asli', { exact: true }).setInputFiles({ name: 'wrong-account.csv', mimeType: 'text/csv', buffer: Buffer.from(original.toString('utf8').replace('0000123456789', '123456789')) });
  await form.getByRole('button', { name: 'Periksa isi berkas', exact: true }).click();
  await form.getByText(/Nomor rekening berbeda/).waitFor(); assert.equal(await form.getByRole('button', { name: 'Tinjau sebelum menyimpan', exact: true }).isDisabled(), true);
  await form.getByLabel('Berkas mutasi asli', { exact: true }).setInputFiles({ name: 'synthetic.csv', mimeType: 'text/csv', buffer: original });
  await form.getByRole('button', { name: 'Periksa isi berkas', exact: true }).click();
  await form.getByRole('cell', { name: /0001/ }).waitFor();
  await form.getByLabel('Referensi sumber bank', { exact: true }).fill('Synthetic approved bank portal');
  await form.getByLabel('Catatan impor', { exact: true }).fill('Pemeriksaan sumber fixture lokal'); await form.getByRole('checkbox').check();
  await form.getByRole('button', { name: 'Tinjau sebelum menyimpan', exact: true }).click();
  const confirmation = page.getByRole('region', { name: 'Konfirmasi impor', exact: true }); await confirmation.waitFor();
  assert.equal(await confirmation.getByRole('heading').evaluate(element => element === document.activeElement), true);
  await maker.context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await confirmation.getByRole('button', { name: 'Simpan impor', exact: true }).isDisabled(), true); assert.equal(writes.length, 0);
  await maker.context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
  dropNext = 'stage'; await confirmation.getByRole('button', { name: 'Simpan impor', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  const references = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  assert.equal(references.length, 1); assert.deepEqual(Object.keys(references[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.equal(references[0].operation, 'billing.bank-statement.stage');
  assert.doesNotMatch(JSON.stringify(references), /Synthetic|0000123456789|originalBase64/);
  await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.waitForFunction(() => !Object.keys(localStorage).some(key => key.startsWith('unit-toko:pending:v1:')));
  await page.getByRole('button', { name: 'Muat daftar terbaru', exact: true }).click();
  const open = page.getByRole('button', { name: /^Periksa impor [1-9]/ }).first(); const importId = (await open.innerText()).match(/([1-9][0-9]*)$/)[1]; await open.click();
  const view = page.getByRole('region', { name: 'Rincian impor', exact: true }); await view.waitFor(); assert.match(await view.innerText(), /0000123456789/); assert.match(await view.innerText(), /0001/);
  const downloaded = page.waitForEvent('download'); await view.getByRole('button', { name: 'Unduh berkas asli', exact: true }).click(); const download = await downloaded;
  const stream = await download.createReadStream(); const source = []; for await (const chunk of stream) source.push(chunk); assert.deepEqual(Buffer.concat(source), original);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'bank-statement-mobile.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 950 }); await page.screenshot({ path: resolve(directory, 'bank-statement-desktop.png'), fullPage: true });
  const reader = await desk(config.readerToken); assert.equal(await reader.page.getByRole('button', { name: 'Siapkan impor mutasi', exact: true }).count(), 0);
  await reader.page.getByRole('button', { name: 'Periksa impor ' + importId, exact: true }).click(); await reader.page.getByRole('region', { name: 'Rincian impor', exact: true }).waitFor();
  const outsider = await desk(config.otherToken); await outsider.page.getByText('Belum ada impor dalam cakupan akses Anda.', { exact: true }).waitFor();
  const denied = await outsider.page.evaluate(async ({ importId, actorId }) => {
    const csrf = await (await fetch('/api/v1/identity/csrf', { credentials: 'include' })).json();
    const response = await fetch('/api/v1/billing/bank-statements/imports/' + importId + '/original/query', { method: 'POST', credentials: 'include', headers: { 'content-type': 'application/json', 'X-CSRF-Token': csrf.requestToken, 'X-Expected-Actor-Id': actorId }, body: JSON.stringify({ purpose: 'bank-statement-review' }) }); return response.status;
  }, { importId, actorId: config.otherId }); assert.equal(denied, 403);
  assert.equal(writes.length, 1); assert.equal(dropped, 1); assert.deepEqual(errors, []);
  console.log('REAL_BANK_STATEMENT_BROWSER_PASS ' + JSON.stringify({ wrongAccountBlocked: true, explicitAccountVersion: true, exactPrivateOriginal: true, lostResponseRecovered: true, oneImport: true, readerCannotStage: true, outsiderDenied: true, offlineUnsent: true, mobile: true, syntheticBankProfile: true, productionAccepted: false }));
} catch (error) {
  for (const [index, page] of pages.entries()) if (!page.isClosed()) { console.error('FIXTURE PAGE ' + index + ': ' + (await page.locator('main').innerText().catch(() => 'unavailable')).slice(0, 5000)); await page.screenshot({ path: resolve(directory, 'failure-' + index + '.png'), fullPage: true }).catch(() => {}); }
  console.error(error.stack ?? String(error)); process.exitCode = 1;
} finally {
  let timer;
  try { await Promise.race([(async () => { if (browser) await browser.close(); for (const socket of sockets) socket.destroy(); for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve)); await app.close(); })(), new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Fixture cleanup exceeded 10 seconds')), 10000); })]); }
  catch (failure) { console.error(failure); process.exitCode = 1; } finally { clearTimeout(timer); }
}
process.exit(process.exitCode ?? 0);
