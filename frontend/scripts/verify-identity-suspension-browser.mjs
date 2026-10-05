// Synthetic local accounts/provider proofs; actual production Next, HTTPS, .NET and Mongo authority.
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import {createRequire} from 'node:module';
import {mkdirSync, writeFileSync} from 'node:fs';
import {resolve} from 'node:path';
import next from 'next';

const chunks = []; for await (const chunk of process.stdin) chunks.push(chunk);
const config = JSON.parse(Buffer.concat(chunks).toString('utf8'));
assert.equal(process.versions.node.split('.')[0], '24');
assert.equal(process.env.NEXT_PUBLIC_UNIT_TOKO_RELEASE, 'full');
const api = new URL(config.apiAddress), origin = new URL(config.origin);
assert.equal(api.protocol, 'http:'); assert.equal(api.hostname, '127.0.0.1'); assert.ok(Number(api.port) > 0);
assert.equal(origin.href, 'https://portal.unit-toko.co.id/'); assert.match(config.spki, /^[A-Za-z0-9+/]{43}=$/);
for (const name of ['adminId', 'targetId', 'scopedId', 'outsideId', 'otherAdminId']) assert.match(config[name], /^[1-9][0-9]{0,18}$/);
assert.match(config.controlKey, /^[A-F0-9]{64}$/);
const {chromium} = createRequire(config.playwright)('playwright');
const directory = resolve(config.directory); mkdirSync(directory, {recursive: true});
const app = next({dev: false, dir: process.cwd(), quiet: true});
const sockets = new Set(), pages = [], errors = [], commands = [], lookups = [];
let frontend, edge, tunnel, browser, dropped = false, committedResponse;
const operation = 'access.identity.suspend', reason = 'Penonaktifan akun untuk uji browser lokal sintetis';
const commandPath = '/api/v1/access/subjects/' + config.targetId + '/suspend';
function track(server) { server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); }); return server; }
async function listen(server) { await new Promise((yes, no) => { server.once('error', no); server.listen(0, '127.0.0.1', yes); }); return server.address().port; }
function forward(req, res, port, isApi) {
  const path = new URL(req.url, origin).pathname;
  const headers = {...req.headers, host: '127.0.0.1:' + port};
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded', 'connection'].includes(name)) delete headers[name];
  if (isApi) { headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https'; }
  const save = req.method === 'POST' && /^\/api\/v1\/access\/subjects\/[1-9][0-9]*\/suspend$/.test(path);
  const lookup = req.method === 'POST' && path === '/api/v1/access/operations/lookup';
  const observed = {path, actor: req.headers['x-expected-actor-id'], key: req.headers['idempotency-key'],
    ifMatch: req.headers['if-match'], csrf: typeof req.headers['x-csrf-token'] === 'string', origin: req.headers.origin};
  if (save) commands.push(observed); if (lookup) lookups.push(observed);
  const upstream = http.request({hostname: '127.0.0.1', port, path: req.url, method: req.method, headers}, response => {
    if (save) observed.status = response.statusCode;
    if (lookup) observed.status = response.statusCode;
    if (save && path === commandPath && !dropped && response.statusCode === 200) {
      const body = []; response.on('data', chunk => body.push(chunk));
      response.on('end', () => {
        committedResponse = JSON.parse(Buffer.concat(body).toString('utf8')); dropped = true;
        res.writeHead(200, response.headers); res.flushHeaders(); res.write('{'); setTimeout(() => res.destroy(), 25);
      }); return;
    }
    res.writeHead(response.statusCode ?? 502, response.headers); response.pipe(res);
  });
  upstream.on('error', () => { if (!res.headersSent) res.writeHead(502); res.end(); });
  req.on('aborted', () => upstream.destroy());
  if (save || lookup) {
    const body = []; req.on('data', chunk => body.push(chunk)); req.on('end', () => {
      const content = Buffer.concat(body); observed.body = JSON.parse(content.toString('utf8')); upstream.end(content);
    });
  } else req.pipe(upstream);
}
async function user(who, mobile = false) {
  const context = await browser.newContext({viewport: mobile ? {width: 390, height: 844} : {width: 1280, height: 960}, locale: 'id-ID', timezoneId: 'Asia/Jakarta'});
  await context.addCookies([{name: '__Host-ut_session', value: config.tokens[who], domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax'}]);
  const page = await context.newPage(); page.setDefaultTimeout(30000); page.on('pageerror', error => errors.push(error.message));
  pages.push({who: who + (mobile ? '-mobile' : ''), page}); return {context, page};
}
async function get(page, path, actorId) {
  return page.evaluate(async ({path, actorId}) => {
    const response = await fetch(path, {credentials: 'same-origin', cache: 'no-store', headers: actorId ? {'X-Expected-Actor-Id': actorId} : {}});
    return {status: response.status, data: await response.json(), etag: response.headers.get('ETag')};
  }, {path, actorId});
}
async function post(page, path, actorId, body, extra = {}) {
  return page.evaluate(async ({path, actorId, body, extra}) => {
    const csrfResponse = await fetch('/api/v1/identity/csrf', {credentials: 'same-origin', cache: 'no-store'});
    const csrf = await csrfResponse.json();
    const response = await fetch(path, {method: 'POST', credentials: 'same-origin', cache: 'no-store',
      headers: {'Content-Type': 'application/json', 'X-CSRF-Token': csrf.requestToken, 'X-Expected-Actor-Id': actorId, ...extra}, body: JSON.stringify(body)});
    return {status: response.status, data: await response.json()};
  }, {path, actorId, body, extra});
}
async function access(page) {
  await page.goto(new URL('/administration/access', origin).href);
  await page.getByRole('heading', {name: 'Kelola akses', exact: true}).waitFor();
  await page.waitForFunction(() => { const button = [...document.querySelectorAll('button')].find(el => el.textContent === 'Tampilkan akun'); return button && !document.querySelector('input[inputmode="numeric"]')?.disabled; });
}
async function subject(page, id) {
  await page.getByLabel('Referensi akun', {exact: true}).fill(id);
  const response = page.waitForResponse(res => new URL(res.url()).pathname === '/api/v1/access/subjects/' + id && res.request().method() === 'GET');
  await page.getByRole('button', {name: 'Tampilkan akun', exact: true}).click();
  const actual = await response; const data = await actual.json();
  if (actual.status() === 200) await page.getByText('Akun ' + id, {exact: true}).first().waitFor();
  return {status: actual.status(), data};
}
const journal = page => page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage.getItem(key))));
async function history(page) {
  await page.goto(new URL('/administration/history', origin).href);
  await page.getByRole('heading', {name: 'Riwayat perubahan akses', exact: true}).waitFor();
  await page.getByLabel(/^Referensi subjek/).fill(config.targetId);
  const response = page.waitForResponse(res => new URL(res.url()).pathname === '/api/v1/access/history/query' && res.request().method() === 'POST');
  await page.getByRole('button', {name: 'Tampilkan riwayat', exact: true}).click();
  const actual = await response; assert.equal(actual.status(), 200); const data = await actual.json();
  await page.getByRole('heading', {name: 'Akun ditangguhkan', exact: true}).waitFor();
  await page.getByText(reason, {exact: true}).waitFor(); return data;
}
try {
  await app.prepare(); const handle = app.getRequestHandler();
  frontend = track(http.createServer((req, res) => { void handle(req, res).catch(() => { res.statusCode = 500; res.end(); }); }));
  const frontendPort = await listen(frontend);
  edge = track(https.createServer({pfx: Buffer.from(config.pfx, 'base64'), passphrase: config.password}, (req, res) => {
    if (req.headers.host !== origin.host) { res.writeHead(403); res.end(); return; }
    const isApi = req.url.startsWith('/api/v1/') || req.url.startsWith('/auth/') || req.url.startsWith('/__fixture/');
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
  const target = await user('target'); await target.page.goto(new URL('/identity/account', origin).href);
  const targetBefore = await get(target.page, '/api/v1/access/current', config.targetId);
  assert.equal(targetBefore.status, 200); assert.equal(targetBefore.data.userId, config.targetId);
  const businessBefore = await post(target.page, '/api/v1/ordering/requests/query', config.targetId, {purpose: 'branch-request-history', view: 'mine'});
  assert.equal(businessBefore.status, 200); assert.deepEqual(businessBefore.data.items, []);
  const scoped = await user('scoped'); await access(scoped.page); const scopedSubject = await subject(scoped.page, config.targetId);
  assert.equal(scopedSubject.status, 200); assert.deepEqual(scopedSubject.data.allowedActions, []);
  assert.deepEqual(scopedSubject.data.memberships.map(row => row.id), [config.targetId]);
  assert.deepEqual(scopedSubject.data.grants.map(row => row.id), [config.targetId]);
  assert.equal(await scoped.page.getByRole('button', {name: 'Nonaktifkan akun', exact: true}).count(), 0);
  const scopedDenied = await post(scoped.page, commandPath, config.scopedId, {reason: 'Tidak boleh menonaktifkan seluruh akun dari izin cabang'},
    {'If-Match': '"1"', 'Idempotency-Key': 'r11-scoped-refusal'});
  assert.equal(scopedDenied.status, 403); assert.equal(scopedDenied.data.code, 'scope_denied');
  const outside = await user('outside'); await access(outside.page);
  const outsideDenied = await subject(outside.page, config.targetId); assert.equal(outsideDenied.status, 403);
  assert.equal(await outside.page.getByRole('button', {name: 'Nonaktifkan akun', exact: true}).count(), 0);
  await outside.page.getByRole('alert').first().waitFor();
  const admin = await user('admin'); await access(admin.page);
  const self = await subject(admin.page, config.adminId); assert.equal(self.status, 200); assert.deepEqual(self.data.allowedActions, []);
  assert.equal(await admin.page.getByRole('button', {name: 'Nonaktifkan akun', exact: true}).count(), 0);
  const selfDenied = await post(admin.page, '/api/v1/access/subjects/' + config.adminId + '/suspend', config.adminId,
    {reason: 'Tidak boleh menonaktifkan akun pelaksana sendiri'}, {'If-Match': '"1"', 'Idempotency-Key': 'r11-self-refusal'});
  assert.equal(selfDenied.status, 400); assert.equal(selfDenied.data.code, 'identity_self_suspension_forbidden');
  const stale = await post(admin.page, commandPath, config.adminId, {reason: 'Versi akun harus cocok sebelum perubahan'},
    {'If-Match': '"2"', 'Idempotency-Key': 'r11-stale-refusal'});
  assert.equal(stale.status, 409); assert.equal(stale.data.code, 'version_conflict');
  const changedActor = await post(admin.page, commandPath, config.targetId, {reason: 'Pelaksana lama tidak boleh dipakai oleh akun lain'},
    {'If-Match': '"1"', 'Idempotency-Key': 'r11-actor-refusal'});
  assert.equal(changedActor.status, 409); assert.equal(changedActor.data.code, 'actor_changed');
  const active = await subject(admin.page, config.targetId); assert.equal(active.status, 200);
  assert.equal(active.data.status, 'active'); assert.equal(active.data.grantVersion, '1'); assert.deepEqual(active.data.allowedActions, ['suspend']);
  assert.equal(active.data.memberships.length, 2); assert.equal(active.data.grants.length, 2);
  await admin.page.getByRole('button', {name: 'Nonaktifkan akun', exact: true}).click();
  const form = admin.page.getByRole('form', {name: 'Konfirmasi penonaktifan akun', exact: true});
  await form.getByRole('heading', {name: 'Nonaktifkan akun ' + config.targetId, exact: true}).waitFor();
  const finalButton = form.getByRole('button', {name: 'Pastikan dan nonaktifkan akun', exact: true});
  assert.equal(await finalButton.isDisabled(), true);
  await form.getByLabel('Alasan keputusan', {exact: true}).fill(reason); assert.equal(await finalButton.isDisabled(), true);
  await form.getByRole('checkbox', {name: 'Saya memahami bahwa seluruh akses dan proses masuk akun ini akan dihentikan.', exact: true}).check();
  assert.equal(await finalButton.isEnabled(), true); await finalButton.click();
  await admin.page.getByRole('heading', {name: 'Pemulihan permintaan', exact: true}).waitFor();
  assert.equal(dropped, true); const successful = commands.filter(row => row.status === 200); assert.equal(successful.length, 1);
  const original = successful[0]; assert.equal(original.path, commandPath); assert.equal(original.actor, config.adminId);
  assert.equal(original.ifMatch, '"1"'); assert.equal(original.csrf, true); assert.equal(original.origin, origin.origin); assert.deepEqual(original.body, {reason});
  assert.deepEqual(committedResponse, {id: config.targetId, subjectId: config.targetId, version: '2', grantVersion: '2', status: 'suspended'});
  const retained = await journal(admin.page); assert.equal(retained.length, 1); const entry = retained[0];
  assert.deepEqual(Object.keys(entry).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.equal(entry.actorId, config.adminId); assert.equal(entry.operation, operation); assert.equal(entry.key, original.key);
  assert.equal(await admin.page.getByRole('button', {name: 'Periksa kembali permintaan yang sama', exact: true}).count(), 0);
  await admin.page.screenshot({path: resolve(directory, 'suspension-uncertain-desktop.png'), fullPage: true});
  await admin.page.reload(); await admin.page.getByRole('heading', {name: 'Pemulihan permintaan', exact: true}).waitFor();
  const recoveryResponse = admin.page.waitForResponse(res => new URL(res.url()).pathname === '/api/v1/access/operations/lookup' && res.request().method() === 'POST');
  await admin.page.getByRole('button', {name: 'Periksa hasil', exact: true}).click();
  const actualRecovery = await recoveryResponse; assert.equal(actualRecovery.status(), 200); const recovery = await actualRecovery.json();
  assert.equal(recovery.state, 'committed'); assert.equal(recovery.operation, operation);
  for (const key of ['id', 'subjectId', 'version', 'grantVersion', 'status']) assert.equal(recovery.result[key], committedResponse[key]);
  await admin.page.getByRole('status').filter({hasText: 'Permintaan telah tersimpan; referensi hasil ' + config.targetId + ', versi 2.'}).waitFor();
  assert.deepEqual(await journal(admin.page), []); assert.equal(lookups.length, 1); assert.equal(lookups[0].actor, config.adminId);
  assert.equal(lookups[0].ifMatch, undefined); assert.deepEqual(lookups[0].body, {operation, key: original.key, purpose: 'operation-recovery'});
  const suspended = await subject(admin.page, config.targetId); assert.equal(suspended.status, 200);
  assert.equal(suspended.data.status, 'suspended'); assert.equal(suspended.data.grantVersion, '2'); assert.deepEqual(suspended.data.allowedActions, []);
  await admin.page.getByText('Dinonaktifkan', {exact: true}).waitFor();
  assert.equal(await admin.page.getByRole('button', {name: 'Nonaktifkan akun', exact: true}).count(), 0);
  await admin.page.screenshot({path: resolve(directory, 'suspension-persisted-desktop.png'), fullPage: true});
  const actualHistory = await history(admin.page); assert.equal(actualHistory.total, '1'); assert.equal(actualHistory.items.length, 1);
  const event = actualHistory.items[0]; assert.equal(event.operation, 'access.identity.suspended'); assert.equal(event.actorId, config.adminId);
  assert.equal(event.subjectId, config.targetId); assert.equal(event.reason, reason); assert.equal(event.scope.kind, 'system');
  await admin.page.screenshot({path: resolve(directory, 'suspension-history-desktop.png'), fullPage: true});
  const mobile = await user('admin', true); await access(mobile.page); const mobileSubject = await subject(mobile.page, config.targetId);
  assert.deepEqual(mobileSubject.data, suspended.data); await mobile.page.getByRole('link', {name: 'Unit Toko', exact: true}).waitFor();
  assert.equal(await mobile.page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), true);
  await mobile.page.screenshot({path: resolve(directory, 'suspension-persisted-mobile.png'), fullPage: true});
  assert.deepEqual(await history(mobile.page), actualHistory); assert.equal(await mobile.page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), true);
  await mobile.page.screenshot({path: resolve(directory, 'suspension-history-mobile.png'), fullPage: true});
  assert.equal((await get(target.page, '/api/v1/access/current', config.targetId)).status, 401);
  assert.equal((await post(target.page, '/api/v1/ordering/requests/query', config.targetId, {purpose: 'branch-request-history', view: 'mine'})).status, 401);
  await target.page.reload(); await target.page.getByRole('link', {name: /^(Masuk kembali|Ke halaman masuk)$/}).first().waitFor();
  const freshTarget = await user('target'); await freshTarget.page.goto(new URL('/identity/account', origin).href);
  assert.equal((await get(freshTarget.page, '/api/v1/access/current', config.targetId)).status, 401);
  const newAdmission = await admin.page.evaluate(async controlKey => {
    const response = await fetch('/__fixture/r11/new-session', {method: 'POST', headers: {'X-Fixture-Control': controlKey}});
    return {status: response.status, data: await response.json()};
  }, config.controlKey);
  assert.deepEqual(newAdmission, {status: 401, data: {code: 'session_unavailable'}});
  const receiptDenied = await post(scoped.page, '/api/v1/access/operations/lookup', config.scopedId, {operation, key: original.key, purpose: 'operation-recovery'});
  assert.equal(receiptDenied.status, 403); assert.equal(receiptDenied.data.code, 'permission_denied');
  const otherAdmin = await user('otherAdmin'); await access(otherAdmin.page);
  const privateReceipt = await post(otherAdmin.page, '/api/v1/access/operations/lookup', config.otherAdminId, {operation, key: original.key, purpose: 'operation-recovery'});
  assert.equal(privateReceipt.status, 200); assert.equal(privateReceipt.data.operation, operation);
  assert.equal(privateReceipt.data.state, 'not_confirmed'); assert.equal(privateReceipt.data.result, null); assert.equal(privateReceipt.data.recordedAt, null);
  const hiddenHistory = await post(scoped.page, '/api/v1/access/history/query', config.scopedId,
    {purpose: 'access-review', offset: 0, limit: 25, subjectId: config.targetId});
  assert.equal(hiddenHistory.status, 200); assert.equal(hiddenHistory.data.total, '0'); assert.deepEqual(hiddenHistory.data.items, []);
  assert.equal(commands.filter(row => row.status === 200).length, 1); assert.equal(commands.length, 5); assert.deepEqual(errors, []);
  const proof = {syntheticLocal: true, key: original.key, reason, historyId: event.id, committedResponse, recovery, suspended: suspended.data,
    history: actualHistory, originalKeyRecovery: true, noResend: true, requesterBusinessBefore: true, existingSessionDenied: true,
    freshBrowserSessionDenied: true, genuineNewSessionAdmissionDenied: true, scopedAccountSuspensionDenied: true,
    foreignSummaryDenied: true, foreignActorReceiptPrivate: true, selfDenied: true, staleVersionDenied: true, changedActorDenied: true, mobile: true, validation: true};
  writeFileSync(resolve(directory, 'proof.json'), JSON.stringify(proof, null, 2));
  console.log('REAL_IDENTITY_SUSPENSION_BROWSER_PASS ' + JSON.stringify({syntheticLocal: true, originalKeyRecovery: true, noResend: true, existingAndNewSessionsDenied: true, scopedIsolation: true, mobile: true}));
} catch (error) {
  for (const {who, page} of pages) await page.screenshot({path: resolve(directory, 'failure-' + who + '.png'), fullPage: true}).catch(() => {});
  console.error(error); process.exitCode = 1;
} finally {
  let timer;
  try {
    await Promise.race([(async () => {
      if (browser) await browser.close(); for (const socket of sockets) socket.destroy();
      for (const server of [tunnel, edge, frontend]) if (server) await new Promise(yes => server.close(yes)); await app.close();
    })(), new Promise((_, no) => { timer = setTimeout(() => no(new Error('Identity suspension fixture cleanup exceeded 10 seconds')), 10000); })]);
  } catch (error) { console.error(error); process.exitCode = 1; }
  finally { clearTimeout(timer); }
}
process.exit(process.exitCode ?? 0);
