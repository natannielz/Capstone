// Synthetic issued sessions; actual production Next, HTTPS, .NET authority and Mongo contact owners.
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
const sockets = new Set(), pages = [], errors = [], writes = [], recoveries = [];
let frontend, edge, tunnel, browser, dropped = false;
function track(server) { server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); }); return server; }
async function listen(server) { await new Promise((yes, no) => { server.once('error', no); server.listen(0, '127.0.0.1', yes); }); return server.address().port; }
function forward(req, res, port, isApi) {
  const path = new URL(req.url, origin).pathname;
  const headers = {...req.headers, host: '127.0.0.1:' + port};
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded', 'connection'].includes(name)) delete headers[name];
  if (isApi) { headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https'; }
  const save = req.method === 'POST' && path === '/api/v1/identity/contact';
  const recovery = req.method === 'POST' && path === '/api/v1/identity/contact/operations/query';
  const observed = {actor: req.headers['x-expected-actor-id'], key: req.headers['idempotency-key'], csrf: typeof req.headers['x-csrf-token'] === 'string', origin: req.headers.origin};
  if (save) writes.push(observed); if (recovery) recoveries.push(observed);
  const upstream = http.request({hostname: '127.0.0.1', port, path: req.url, method: req.method, headers}, response => {
    if (save && !dropped && response.statusCode === 200) {
      dropped = true; response.resume(); response.on('end', () => {
        res.writeHead(200, response.headers); res.flushHeaders(); res.write('{'); setTimeout(() => res.destroy(), 25);
      }); return;
    }
    res.writeHead(response.statusCode ?? 502, response.headers); response.pipe(res);
  });
  upstream.on('error', () => { if (!res.headersSent) res.writeHead(502); res.end(); });
  req.on('aborted', () => upstream.destroy()); req.pipe(upstream);
}
async function user(who, mobile = false) {
  const context = await browser.newContext({viewport: mobile ? {width: 390, height: 844} : {width: 1280, height: 960}, locale: 'id-ID', timezoneId: 'Asia/Jakarta'});
  await context.addCookies([{name: '__Host-ut_session', value: config.tokens[who], domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax'}]);
  const page = await context.newPage(); page.setDefaultTimeout(30000); page.on('pageerror', error => errors.push(error.message));
  pages.push({who, page}); return {context, page};
}
const journal = page => page.evaluate(() => Object.keys(sessionStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(sessionStorage.getItem(key))));
async function panel(page) {
  await page.goto(new URL('/identity/account', origin).href);
  const result = page.locator('#account-contact'); await result.getByRole('heading', {name: 'Kontak pribadi', exact: true}).waitFor();
  await result.getByRole('button', {name: 'Muat ulang kontak dan riwayat', exact: true}).waitFor();
  await page.waitForFunction(() => { const el = document.querySelector('#account-contact-name'); return el && !el.disabled; });
  return result;
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
  const owner = await user('owner'); let contact = await panel(owner.page);
  assert.equal(await contact.getByLabel('Nama kontak', {exact: true}).inputValue(), '');
  await contact.getByLabel('Alasan perubahan kontak', {exact: true}).fill('Kontak cabang untuk pilot lokal');
  await contact.getByRole('button', {name: 'Simpan kontak pribadi', exact: true}).click();
  assert.equal(await contact.getByLabel('Nama kontak', {exact: true}).evaluate(input => input.validity.valueMissing), true);
  assert.equal(writes.length, 0);
  await contact.getByLabel('Nama kontak', {exact: true}).fill('Kontak pilot Tebet');
  await contact.getByLabel('Nomor telepon (opsional)', {exact: true}).fill('+62 812 1234');
  await contact.getByLabel('Jabatan (opsional)', {exact: true}).fill('PIC Cabang');
  await contact.getByLabel('Alamat kontak (opsional)', {exact: true}).fill('Tebet, Jakarta');
  await contact.getByRole('button', {name: 'Simpan kontak pribadi', exact: true}).click();
  await contact.getByRole('heading', {name: 'Perubahan yang hasilnya belum pasti', exact: true}).waitFor();
  assert.equal(dropped, true); assert.equal(writes.length, 1); assert.equal(writes[0].actor, config.ownerId); assert.equal(writes[0].csrf, true); assert.equal(writes[0].origin, origin.origin);
  const retained = await journal(owner.page); assert.equal(retained.length, 1); const entry = retained[0];
  assert.deepEqual(Object.keys(entry).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.equal(entry.operation, 'account.contact.update'); assert.equal(entry.actorId, config.ownerId); assert.equal(entry.key, writes[0].key);
  await owner.page.screenshot({path: resolve(directory, 'contact-uncertain-desktop.png'), fullPage: true});
  await owner.page.reload(); contact = owner.page.locator('#account-contact');
  await contact.getByRole('button', {name: 'Periksa hasil perubahan kontak', exact: true}).click();
  await contact.getByRole('status').filter({hasText: 'Perubahan asli ditemukan pada versi 1'}).waitFor();
  await contact.getByRole('heading', {name: 'Perubahan versi 1', exact: true}).waitFor();
  assert.equal(writes.length, 1); assert.equal(recoveries.length, 1); assert.equal(recoveries[0].actor, config.ownerId); assert.equal((await journal(owner.page)).length, 0);
  await owner.page.reload(); contact = owner.page.locator('#account-contact');
  await contact.getByRole('heading', {name: 'Perubahan versi 1', exact: true}).waitFor();
  assert.equal(await contact.getByLabel('Nama kontak', {exact: true}).inputValue(), 'Kontak pilot Tebet');
  assert.equal(await owner.page.getByText('owner@example.invalid', {exact: true}).count(), 1);
  await owner.page.screenshot({path: resolve(directory, 'contact-persisted-desktop.png'), fullPage: true});
  const mobile = await user('owner', true), mobilePanel = await panel(mobile.page);
  await mobilePanel.getByRole('heading', {name: 'Perubahan versi 1', exact: true}).waitFor();
  assert.equal(await mobilePanel.getByLabel('Nama kontak', {exact: true}).inputValue(), 'Kontak pilot Tebet');
  assert.equal(await mobile.page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), true);
  await mobile.page.screenshot({path: resolve(directory, 'contact-persisted-mobile.png'), fullPage: true});
  const foreign = await user('foreign', true), foreignPanel = await panel(foreign.page);
  assert.equal(await foreignPanel.getByLabel('Nama kontak', {exact: true}).inputValue(), '');
  assert.equal(await foreignPanel.getByRole('heading', {name: 'Perubahan versi 1', exact: true}).count(), 0);
  const denied = await foreign.page.evaluate(async ({ownerId}) => {
    const csrf = await fetch('/api/v1/identity/csrf', {credentials: 'same-origin', cache: 'no-store'}).then(r => r.json());
    const response = await fetch('/api/v1/identity/contact/history/query', {method: 'POST', credentials: 'same-origin', cache: 'no-store',
      headers: {'Content-Type': 'application/json', 'X-CSRF-Token': csrf.requestToken, 'X-Expected-Actor-Id': ownerId},
      body: JSON.stringify({purpose: 'account-contact', beforeHistoryId: null, limit: 20})});
    return response.status;
  }, {ownerId: config.ownerId}); assert.equal(denied, 409);
  assert.equal(writes.length, 1); assert.deepEqual(errors, []);
  console.log('REAL_ACCOUNT_CONTACT_BROWSER_PASS ' + JSON.stringify({lostCommittedReply: true, originalKeyRecovery: true, noResend: true, ownHistory: true, refreshPersistence: true, mobile: true, foreignIsolation: true, validation: true}));
} catch (error) {
  for (const {who, page} of pages) await page.screenshot({path: resolve(directory, 'failure-' + who + '.png'), fullPage: true}).catch(() => {});
  console.error(error); process.exitCode = 1;
} finally {
  let timer;
  try {
    await Promise.race([(async () => {
      if (browser) await browser.close(); for (const socket of sockets) socket.destroy();
      for (const server of [tunnel, edge, frontend]) if (server) await new Promise(yes => server.close(yes)); await app.close();
    })(), new Promise((_, no) => { timer = setTimeout(() => no(new Error('Contact fixture cleanup exceeded 10 seconds')), 10000); })]);
  } catch (error) { console.error(error); process.exitCode = 1; }
  finally { clearTimeout(timer); }
}
process.exit(process.exitCode ?? 0);
