// Named synthetic issued sessions; real production Next, HTTPS, .NET and posted Mongo journals.
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import {createRequire} from 'node:module';
import {mkdirSync} from 'node:fs';
import {resolve} from 'node:path';
import next from 'next';

const chunks = []; for await (const chunk of process.stdin) chunks.push(chunk);
const config = JSON.parse(Buffer.concat(chunks).toString('utf8'));
assert.equal(process.versions.node.split('.')[0], '24');
assert.equal(process.env.NEXT_PUBLIC_UNIT_TOKO_RELEASE, 'full');
const api = new URL(config.apiAddress), origin = new URL(config.origin);
assert.equal(api.protocol, 'http:'); assert.equal(api.hostname, '127.0.0.1'); assert.ok(Number(api.port) > 0);
assert.equal(origin.href, 'https://portal.unit-toko.co.id/');
assert.match(config.spki, /^[A-Za-z0-9+/]{43}=$/);
const {chromium} = createRequire(config.playwright)('playwright');
const directory = resolve(config.directory); mkdirSync(directory, {recursive: true});
const app = next({dev: false, dir: process.cwd(), quiet: true});
const sockets = new Set(), errors = [], queries = [], exports = [], pages = [];
let frontend, edge, tunnel, browser;
function track(server) { server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); }); return server; }
async function listen(server) { await new Promise((yes, no) => { server.once('error', no); server.listen(0, '127.0.0.1', yes); }); return server.address().port; }
function forward(req, res, port, isApi) {
  const path = new URL(req.url, origin).pathname;
  const headers = {...req.headers, host: '127.0.0.1:' + port};
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded', 'connection'].includes(name)) delete headers[name];
  if (isApi) { headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https'; }
  const observed = {actor: req.headers['x-expected-actor-id'], csrf: typeof req.headers['x-csrf-token'] === 'string', origin: req.headers.origin};
  if (path === '/api/v1/accounting/reports/query') queries.push(observed);
  if (path === '/api/v1/accounting/reports/export') exports.push(observed);
  const upstream = http.request({hostname: '127.0.0.1', port, path: req.url, method: req.method, headers}, response => {
    res.writeHead(response.statusCode ?? 502, response.headers); response.pipe(res);
  });
  upstream.on('error', () => { if (!res.headersSent) res.writeHead(502); res.end(); });
  req.on('aborted', () => upstream.destroy()); req.pipe(upstream);
}
async function user(who, mobile = false) {
  const context = await browser.newContext({viewport: mobile ? {width: 390, height: 844} : {width: 1280, height: 960}, locale: 'id-ID', timezoneId: 'Asia/Jakarta'});
  await context.addCookies([{name: '__Host-ut_session', value: config.tokens[who], domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax'}]);
  const page = await context.newPage(); page.setDefaultTimeout(30000); page.on('pageerror', error => errors.push(error.message));
  pages.push({who, page});
  page.on('response', async response => {
    const path = new URL(response.url()).pathname;
    if (!['/api/v1/identity/session', '/api/v1/access/current', '/api/v1/accounting/reports/books/query'].includes(path)) return;
    let value; try {value = await response.json();} catch {}
    console.log('REPORT_BOUNDARY ' + JSON.stringify({who, path, status: response.status(),
      authenticated: typeof value?.authenticated === 'boolean' ? value.authenticated : undefined,
      code: typeof value?.code === 'string' ? value.code : undefined, items: Array.isArray(value?.items) ? value.items.length : undefined}));
  });
  return {context, page};
}
async function screen(page) {
  await page.goto(new URL('/reporting/accounts', origin).href);
  await page.getByRole('heading', {name: 'Laporan akun dan jurnal', exact: true}).waitFor();
  await page.waitForFunction(() => {
    const select = document.querySelector('#accounting-report-book'); return select && !select.disabled;
  });
  return page.locator('#accounting-report-workspace');
}
async function report(page) {
  await page.locator('#accounting-report-book').selectOption(config.bookId);
  await page.locator('#accounting-report-end-date').fill(config.endDate);
  const reply = page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1/accounting/reports/query' && response.request().method() === 'POST');
  await page.getByRole('button', {name: 'Tampilkan laporan', exact: true}).click();
  const response = await reply; assert.equal(response.status(), 200); const snapshot = await response.json();
  assert.equal(snapshot.endDate, config.endDate); assert.equal(snapshot.book.id, config.bookId);
  assert.equal(snapshot.timeZone, 'Asia/Jakarta'); assert.equal(snapshot.cumulative, true); assert.equal(snapshot.isApprovedClose, false);
  assert.equal(snapshot.journals.length, config.journalIds.length); assert.equal(snapshot.debit, config.total); assert.equal(snapshot.credit, config.total);
  assert.match(snapshot.readId, /^[1-9][0-9]*$/); assert.match(snapshot.snapshotHash, /^[a-f0-9]{64}$/);
  await page.getByRole('button', {name: 'Unduh CSV lengkap', exact: true}).waitFor();
  return snapshot;
}
async function post(page, path, body, actorId) {
  return page.evaluate(async ({path, body, actorId}) => {
    const csrf = await fetch('/api/v1/identity/csrf', {credentials: 'same-origin', cache: 'no-store'}).then(response => response.json());
    const response = await fetch(path, {method: 'POST', credentials: 'same-origin', cache: 'no-store',
      headers: {'Content-Type': 'application/json', 'X-CSRF-Token': csrf.requestToken, 'X-Expected-Actor-Id': actorId}, body: JSON.stringify(body)});
    return {status: response.status, value: await response.json()};
  }, {path, body, actorId});
}
try {
  await app.prepare(); const handle = app.getRequestHandler();
  frontend = track(http.createServer((req, res) => { void handle(req, res).catch(() => { res.statusCode = 500; res.end(); }); }));
  const frontendPort = await listen(frontend);
  edge = track(https.createServer({pfx: Buffer.from(config.pfx, 'base64'), passphrase: config.password}, (req, res) => {
    if (req.headers.host !== origin.host) { res.writeHead(403); res.end(); return; }
    const isApi = req.url.startsWith('/api/v1/') || req.url.startsWith('/auth/');
    forward(req, res, isApi ? Number(api.port) : frontendPort, isApi);
  })); const tlsPort = await listen(edge);
  tunnel = track(http.createServer((_req, res) => { res.writeHead(403); res.end(); }));
  tunnel.on('connect', (req, socket, head) => {
    if (req.url !== origin.host + ':443') { socket.end('HTTP/1.1 403 Forbidden\r\n\r\n'); return; }
    const upstream = net.connect(tlsPort, '127.0.0.1', () => { socket.write('HTTP/1.1 200 Connection Established\r\n\r\n'); if (head.length) upstream.write(head); socket.pipe(upstream); upstream.pipe(socket); });
    sockets.add(upstream); upstream.on('close', () => sockets.delete(upstream)); upstream.on('error', () => socket.destroy()); socket.on('error', () => upstream.destroy()); socket.on('close', () => upstream.destroy());
  }); const proxyPort = await listen(tunnel);
  const channel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'msedge';
  browser = await chromium.launch({...(channel === 'msedge' ? {channel} : {}), headless: true,
    args: ['--ignore-certificate-errors-spki-list=' + config.spki], proxy: {server: 'http://127.0.0.1:' + proxyPort}});
  const owner = await user('owner');
  const retired = await owner.page.goto(new URL('/api/reports', origin).href);
  assert.equal(retired?.status(), 404, 'Retired legacy report route must not be served by the maintained frontend');
  await screen(owner.page);
  await owner.page.locator('#accounting-report-book').selectOption(config.bookId);
  await owner.page.locator('#accounting-report-end-date').fill('');
  await owner.page.getByRole('button', {name: 'Tampilkan laporan', exact: true}).click();
  assert.equal(await owner.page.locator('#accounting-report-end-date').evaluate(input => input.validity.valueMissing), true);
  assert.equal(queries.length, 0);
  const snapshot = await report(owner.page);
  await owner.page.getByRole('button', {name: 'Jurnal berikutnya', exact: true}).click();
  await owner.page.screenshot({path: resolve(directory, 'report-scoped-desktop.png'), fullPage: true});
  const download = owner.page.waitForEvent('download');
  await owner.page.getByRole('button', {name: 'Unduh CSV lengkap', exact: true}).click();
  const received = await download; assert.equal(await received.failure(), null);
  assert.equal(received.suggestedFilename(), `unit-toko-neraca-jurnal-${config.endDate}-${snapshot.readId}.csv`);
  const stream = await received.createReadStream(); assert.ok(stream); const csvChunks = []; for await (const chunk of stream) csvChunks.push(chunk);
  const bytes = Buffer.concat(csvChunks); assert.deepEqual([...bytes.subarray(0, 3)], [0xef, 0xbb, 0xbf]);
  const csv = bytes.toString('utf8'); for (const id of config.journalIds) assert.ok(csv.includes(`\r\n"'${id}","${config.endDate}","fixture",`), 'CSV must contain each selected journal row, including invisible pages');
  assert.ok(exports.length > 0); assert.equal(exports[0].actor, config.ownerId); assert.equal(exports[0].csrf, true); assert.equal(exports[0].origin, origin.origin);
  const forged = await post(owner.page, '/api/v1/accounting/reports/query', {purpose: 'accounting-reporting', key: config.foreignKey, endDate: config.endDate}, config.ownerId);
  assert.equal(forged.status, 403);
  const stale = await post(owner.page, '/api/v1/accounting/reports/books/query', {purpose: 'accounting-reporting'}, config.foreignId);
  assert.equal(stale.status, 409); assert.equal(stale.value.code, 'actor_changed');
  await owner.page.reload(); await owner.page.getByRole('heading', {name: 'Laporan akun dan jurnal', exact: true}).waitFor();
  await report(owner.page);
  const mobile = await user('owner', true); await screen(mobile.page); await report(mobile.page);
  assert.equal(await mobile.page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 1), false);
  await mobile.page.screenshot({path: resolve(directory, 'report-scoped-mobile.png'), fullPage: true});
  const foreign = await user('foreign'); await foreign.page.goto(new URL('/reporting/accounts', origin).href);
  await foreign.page.getByRole('heading', {name: 'Laporan akun dan jurnal', exact: true}).waitFor();
  await foreign.page.getByText('Belum ada buku terkonfigurasi dalam cakupan laporan Anda. Hubungi penanggung jawab untuk memeriksa konfigurasi dan akses.', {exact: true}).waitFor();
  const foreignOptions = await foreign.page.locator('#accounting-report-book option').evaluateAll(options => options.map(option => option.value));
  assert.ok(!foreignOptions.includes(config.bookId));
  const stolen = await post(foreign.page, '/api/v1/accounting/reports/export', {purpose: 'accounting-reporting', readId: snapshot.readId, snapshotHash: snapshot.snapshotHash}, config.foreignId);
  assert.notEqual(stolen.status, 200);
  assert.deepEqual(errors, []);
  console.log('REAL_ACCOUNTING_REPORT_BROWSER_PASS');
} catch (error) {
  for (const {who, page} of pages) {
    try {
      console.log('REPORT_FAILURE_SCREEN ' + who + ' ' + (await page.locator('body').innerText()).slice(0, 3000));
      await page.screenshot({path: resolve(directory, 'report-failed-' + who + '.png'), fullPage: true});
    } catch {}
  }
  process.exitCode = 1; throw error;
} finally {
  await browser?.close(); for (const socket of sockets) socket.destroy();
  for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve));
  await app.close();
}
