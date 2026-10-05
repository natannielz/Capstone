// Actual production Next, HTTPS, .NET, Mongo, private files and pinned native PDF OCR.
// Provider sessions, operator policies and scanner are named synthetic local inputs.
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import {createRequire} from 'node:module';
import {mkdirSync, existsSync, writeFileSync} from 'node:fs';
import {resolve} from 'node:path';
import {createHash} from 'node:crypto';
import {setTimeout as delay} from 'node:timers/promises';
import next from 'next';

const chunks = []; for await (const chunk of process.stdin) chunks.push(chunk);
const config = JSON.parse(Buffer.concat(chunks).toString('utf8')), api = new URL(config.apiAddress), origin = new URL(config.origin);
assert.equal(process.versions.node.split('.')[0], '24'); assert.equal(process.env.NEXT_PUBLIC_UNIT_TOKO_RELEASE, 'full');
assert.equal(api.protocol, 'http:'); assert.equal(api.hostname, '127.0.0.1'); assert.ok(Number(api.port) > 0);
assert.equal(origin.href, 'https://portal.unit-toko.co.id/'); assert.match(config.spki, /^[A-Za-z0-9+/]{43}=$/);
for (const field of ['actorId', 'reviewerId', 'outsiderId', 'declarationId', 'declarationRevision', 'submissionId', 'primaryReservationId']) assert.match(config[field], /^[1-9][0-9]*$/);
const require = createRequire(config.playwrightPackage), metadata = require(config.playwrightPackage);
assert.equal(metadata.name, 'playwright'); assert.equal(metadata.version, '1.62.1'); const {chromium} = require('playwright');
const directory = resolve(config.runDirectory); mkdirSync(directory, {recursive: true});
const original = Buffer.from(config.pdfBase64, 'base64'), image = Buffer.from(config.imageBase64, 'base64');
const hash = bytes => createHash('sha256').update(bytes).digest('hex'); assert.equal(hash(original), config.pdfSha256); assert.equal(hash(image), config.primaryEvidence.sha256);
const app = next({dev: false, dir: process.cwd(), quiet: true}), sockets = new Set(), pages = [], errors = [], writes = [], reads = [];
const primaryPath = `/api/v1/billing/payment-declarations/${config.declarationId}`, requesterPath = primaryPath + '/supplements';
const reviewerPath = `/api/v1/billing/payment-reviews/${config.declarationId}/supplements`;
let frontend, edge, tunnel, browser, dropNext = null, activeDrop = null, dropped = 0, supplement, reservation, evidence, finalView;
const traceEvents = [], traceStarted = performance.now(), privateKeys = new Set(); let traceRequests = 0;
function keyHash(key) {if (typeof key !== 'string' || !key) return null; privateKeys.add(key); return hash(Buffer.from(key, 'utf8'));}
function trace(event, detail = {}) {if (traceEvents.length < 128) traceEvents.push({sequence: traceEvents.length + 1,
  elapsedMs: Math.round(performance.now() - traceStarted), event, ...detail});}
function persistTrace(outcome) {writeFileSync(resolve(directory, 'transport-trace.json'), JSON.stringify({outcome, dropped,
  writes: writes.length, reads: reads.length, events: traceEvents}, null, 2), 'utf8');}
function observedProof(row) {return {path: row.path, kind: row.kind, keyHash: keyHash(row.key),
  actorMatchesRequester: row.actor === config.actorId, revision: row.revision, csrf: row.csrf, originMatches: row.origin === origin.origin};}
const transportCode = error => /^[A-Z0-9_]{1,64}$/.test(error?.code ?? '') ? error.code : 'UNCLASSIFIED';
const safe = value => {let text = String(value); for (const secret of [config.actorToken, config.reviewerToken, config.outsiderToken, config.pfx, config.password, config.pdfBase64, ...privateKeys])
  if (typeof secret === 'string' && secret) text = text.replaceAll(secret, '[REDACTED]'); return text.replace(/(__Host-ut_session|cookie|authorization)\s*[:=].*/gi, '$1=[REDACTED]').slice(0, 1400);};
function track(server) {server.on('connection', socket => {sockets.add(socket); socket.on('close', () => sockets.delete(socket));}); return server;}
async function listen(server) {await new Promise((yes, no) => {server.once('error', no); server.listen(0, '127.0.0.1', yes);}); return server.address().port;}
function kind(path, method) {
  if (method !== 'POST') return null;
  if (path === requesterPath) return 'create';
  if (new RegExp('^' + requesterPath + '/[1-9][0-9]*/pdf-evidence$').test(path)) return 'reserve';
  if (path.startsWith(requesterPath + '/') && path.endsWith('/content')) return 'upload';
  if (path.startsWith(requesterPath + '/') && path.endsWith('/attach')) return 'attach';
  return null;
}
function armDroppedResponse(operation) {
  assert.equal(dropNext, null); assert.ok(activeDrop === null || activeDrop.dropped);
  dropNext = operation; activeDrop = {operation, request: null, status: null, ended: false, complete: false, dropped: false, code: null, failure: null};
  trace('drop_armed', {operation}); return activeDrop;
}
async function waitForDroppedResponse(observed) {
  const deadline = performance.now() + 30000;
  while (!observed.dropped) {
    if (observed.failure) throw new Error(`Intentional ${observed.operation} drop failed: ${observed.failure}`);
    if (observed.ended && observed.status !== 200)
      throw new Error(`Intentional ${observed.operation} drop refused: upstream status ${observed.status}, code ${observed.code ?? 'UNCLASSIFIED'}`);
    assert.ok(performance.now() < deadline, `Actual successful ${observed.operation} response/drop did not finish within 30 seconds`);
    await delay(25);
  }
  assert.equal(observed.status, 200); assert.equal(observed.ended, true); assert.equal(observed.complete, true);
  trace('drop_verified_before_recovery', {request: observed.request, operation: observed.operation, upstreamStatus: observed.status});
}
function forward(req, res, port, isApi) {
  const path = new URL(req.url, origin).pathname, headers = {...req.headers, host: '127.0.0.1:' + port};
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded', 'connection'].includes(name)) delete headers[name];
  if (isApi) {headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https';}
  const operation = kind(path, req.method), observed = {path, actor: req.headers['x-expected-actor-id'], key: req.headers['idempotency-key'],
    revision: req.headers['if-match'], csrf: typeof req.headers['x-csrf-token'] === 'string', origin: req.headers.origin};
  if (operation) writes.push({...observed, kind: operation});
  if (req.method === 'POST' && (path.endsWith('/operations/query') || path.endsWith('/pdf-ocr/query') || path.endsWith('/original/query'))) reads.push(observed);
  const drop = operation !== null && operation === dropNext; if (drop) dropNext = null;
  const tracked = isApi && (operation !== null || path.endsWith('/operations/query'));
  const request = tracked ? ++traceRequests : null;
  const intentionalDrop = drop ? activeDrop : null;
  if (intentionalDrop) intentionalDrop.request = request;
  if (tracked) trace('proxy_dispatch', {request, method: req.method, path, operation, dropArmed: drop,
    keyHash: keyHash(observed.key), actorMatchesRequester: observed.actor === config.actorId,
    csrfPresent: observed.csrf, originMatches: observed.origin === origin.origin});
  const upstream = http.request({hostname: '127.0.0.1', port, path: req.url, method: req.method, headers}, response => {
    if (intentionalDrop) {
      intentionalDrop.status = response.statusCode ?? null;
      response.once('end', () => {intentionalDrop.ended = true; intentionalDrop.complete = response.complete;});
    }
    if (tracked) {
      trace('upstream_status', {request, status: response.statusCode ?? null});
      response.once('end', () => trace('upstream_end', {request, status: response.statusCode ?? null, complete: response.complete}));
      response.once('aborted', () => {if (intentionalDrop) intentionalDrop.failure = 'UPSTREAM_RESPONSE_ABORTED';
        trace('upstream_response_aborted', {request, status: response.statusCode ?? null});});
      response.once('error', error => {if (intentionalDrop) intentionalDrop.failure = transportCode(error);
        trace('upstream_response_error', {request, code: transportCode(error)});});
      if (response.statusCode !== 200) {
        const errorChunks = []; let errorBytes = 0;
        response.on('data', chunk => {errorBytes += chunk.length; if (errorBytes <= 4096) errorChunks.push(chunk); else errorChunks.length = 0;});
        response.once('end', () => {
          let code = 'UNCLASSIFIED';
          if (errorBytes <= 4096) {try {const error = JSON.parse(Buffer.concat(errorChunks).toString('utf8'));
            if (/^[a-z_]{1,100}$/.test(error?.code ?? '')) code = error.code;
          } catch { /* Keep only a bounded safe code; response bodies never enter the proof. */ }}
          if (intentionalDrop) intentionalDrop.code = code;
          trace('upstream_refusal', {request, status: response.statusCode ?? null, code});
        });
      }
    }
    if (drop && response.statusCode === 200) {
      response.resume(); response.once('end', () => {trace('successful_response_drop_started', {request, operation, upstreamStatus: 200});
        res.writeHead(200, {'content-type': 'application/json', 'content-length': '64', 'cache-control': 'no-store'});
        res.flushHeaders(); res.write('{'); setTimeout(() => {
          if (res.destroyed) {intentionalDrop.failure = 'CLIENT_CLOSED_BEFORE_DROP'; trace('intentional_drop_refused', {request, code: intentionalDrop.failure}); return;}
          res.destroy(); intentionalDrop.dropped = true; dropped++; trace('successful_response_dropped', {request, operation});
        }, 25);}); return;
    }
    res.writeHead(response.statusCode ?? 502, response.headers); response.pipe(res);
  });
  upstream.once('finish', () => {if (tracked) trace('upstream_request_finished', {request});});
  upstream.on('error', error => {if (intentionalDrop) intentionalDrop.failure = transportCode(error);
    if (tracked) trace('upstream_request_error', {request, code: transportCode(error)});
    if (!res.headersSent) res.writeHead(502); res.end();});
  req.on('aborted', () => {if (intentionalDrop) intentionalDrop.failure = 'CLIENT_REQUEST_ABORTED';
    if (tracked) trace('client_request_aborted', {request}); upstream.destroy();}); req.pipe(upstream);
}
async function user(who) {
  const context = await browser.newContext({viewport: {width: 1280, height: 960}, locale: 'id-ID', timezoneId: 'Asia/Jakarta'});
  await context.addCookies([{name: '__Host-ut_session', value: config[who + 'Token'], domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax'}]);
  const page = await context.newPage(); page.setDefaultTimeout(30000); pages.push({who, page}); page.on('pageerror', error => errors.push(safe(error.message)));
  page.on('response', async response => {if (!new URL(response.url()).pathname.startsWith('/api/v1/') || response.status() < 400) return;
    const body = await response.json().catch(() => null), boundary = {who, path: new URL(response.url()).pathname,
      status: response.status(), code: /^[a-z_]{1,100}$/.test(body?.code ?? '') ? body.code : undefined};
    trace('api_boundary', boundary); console.log('SUPPLEMENT_API_BOUNDARY ' + JSON.stringify(boundary));});
  return {context, page};
}
async function ready(path) {const deadline = performance.now() + 180000; while (!existsSync(path)) {assert.ok(performance.now() < deadline, 'Bounded native processing did not finish'); await delay(250);}}
async function open(page, review = false) {
  await page.goto(new URL(review ? '/billing/payment-reviews' : '/billing/payments', origin).href);
  await page.getByRole('link', {name: 'Unit Toko', exact: true}).first().waitFor();
  await page.getByRole('button', {name: `${review ? 'Periksa pengajuan' : 'Buka pembayaran'} ${config.declarationId}`, exact: true}).click();
  await page.getByRole('region', {name: review ? 'Rincian pengajuan pembayaran' : 'Draf pembayaran', exact: true}).waitFor();
  await page.getByRole('region', {name: 'Lampiran PDF tambahan', exact: true}).waitFor();
}
const desk = page => page.getByRole('region', {name: 'Lampiran PDF tambahan', exact: true});
async function load(page, expected = null) {
  const panel = desk(page), reply = page.waitForResponse(response => new URL(response.url()).pathname === (expected?.review ? reviewerPath : requesterPath) + '/query');
  await panel.getByRole('button', {name: 'Muat lampiran PDF tambahan', exact: true}).click(); const response = await reply; assert.equal(response.status(), 200);
  const shown = await response.json(); assert.equal(shown.readiness, 'allowed');
  if (expected?.id) await panel.getByRole('button', {name: `Lihat lampiran ${expected.id}`, exact: true}).click();
  return shown;
}
async function recover(page, expectedOperation, route, revision) {
  const pending = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  assert.equal(pending.length, 1); assert.equal(pending[0].operation, expectedOperation); assert.equal(pending[0].actorId, config.actorId);
  assert.deepEqual(Object.keys(pending[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  const originalKeyHash = keyHash(pending[0].key);
  trace('recovery_begin', {operation: expectedOperation, path: route, keyHash: originalKeyHash,
    matchesDispatchedKey: writes.some(row => keyHash(row.key) === originalKeyHash)});
  await page.reload(); const reply = page.waitForResponse(response => new URL(response.url()).pathname === route);
  await page.getByRole('button', {name: 'Periksa hasil', exact: true}).click(); const response = await reply; assert.equal(response.status(), 200);
  const recoveryKey = response.request().postDataJSON().key;
  trace('recovery_request_binding', {operation: expectedOperation, path: route, status: response.status(), keyHash: keyHash(recoveryKey),
    matchesPendingKey: recoveryKey === pending[0].key, revisionMatches: response.request().headers()['if-match'] === `"${revision}"`});
  assert.equal(response.request().headers()['if-match'], `"${revision}"`); assert.equal(recoveryKey, pending[0].key);
  const result = await response.json();
  trace('recovery_result', {operation: expectedOperation, path: route, status: response.status(),
    recoveryStatus: ['committed', 'not_confirmed', 'unavailable'].includes(result.status) ? result.status : 'UNRECOGNIZED'});
  assert.equal(result.status, 'committed');
  await page.waitForFunction(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).length === 0);
  await page.getByRole('heading', {name: `Pembayaran ${config.declarationId}`, exact: true}).waitFor();
  return result;
}
function primaryTuple(value) {
  assert.equal(value.declarationId, config.declarationId); assert.equal(value.declarationRevision, config.declarationRevision);
  assert.equal(value.submissionId, config.submissionId); assert.deepEqual(value.primaryEvidence, config.primaryEvidence);
}
async function primaryOriginal(page, review = false) {
  const panel = page.getByRole('region', {name: review ? 'Rincian pengajuan pembayaran' : 'Bukti asli pembayaran', exact: true});
  const path = review ? `/api/v1/billing/payment-reviews/${config.declarationId}/evidence/${config.primaryReservationId}/original/query`
    : primaryPath + `/evidence/${config.primaryReservationId}/original/query`;
  const reply = page.waitForResponse(response => new URL(response.url()).pathname === path);
  await panel.getByRole('button', {name: 'Lihat bukti asli', exact: true}).click(); const response = await reply; assert.equal(response.status(), 200);
  assert.equal(response.headers()['content-type'], 'image/png'); assert.equal(hash(await response.body()), config.primaryEvidence.sha256);
  const preview = review ? panel.getByRole('region', {name: 'Bukti asli untuk pemeriksaan', exact: true}) : page.getByRole('region', {name: /^Bukti asli · unggah/});
  const picture = preview.getByRole('img'); await picture.evaluate(element => element.decode());
  assert.deepEqual(await picture.evaluate(element => [element.naturalWidth, element.naturalHeight]), [13, 7]);
  assert.match(await picture.getAttribute('src'), /^blob:/);
  await page.screenshot({path: resolve(directory, review ? 'reviewer-primary-image-desktop.png' : 'requester-primary-image-desktop.png'), fullPage: true});
  await preview.getByRole('button', {name: 'Tutup bukti', exact: true}).click();
}
async function download(page, panel, route) {
  const reply = page.waitForResponse(response => new URL(response.url()).pathname === route), downloaded = page.waitForEvent('download');
  await panel.getByRole('button', {name: 'Buka PDF asli untuk pemeriksaan', exact: true}).click(); const response = await reply; assert.equal(response.status(), 200);
  const headers = response.headers(); assert.equal(headers['content-type'], 'application/pdf'); assert.match(headers['cache-control'], /no-store/);
  assert.equal(headers['x-content-type-options'], 'nosniff'); assert.equal(headers['content-security-policy'], "default-src 'none'; sandbox");
  assert.match(headers['content-disposition'], /^attachment;/); assert.equal(Number(headers['content-length']), original.length); assert.equal(hash(await response.body()), config.pdfSha256);
  const file = await downloaded; assert.equal(await file.failure(), null); const stream = await file.createReadStream(), chunks = []; let length = 0;
  for await (const chunk of stream) {length += chunk.length; assert.ok(length <= 10 * 1024 * 1024); chunks.push(chunk);}
  assert.equal(length, original.length); assert.equal(hash(Buffer.concat(chunks)), config.pdfSha256);
}
async function inspect(page, review, label) {
  await load(page, {id: supplement.supplementId, review}); const panel = desk(page).getByRole('region', {name: 'Pemeriksaan lampiran PDF tambahan', exact: true});
  await panel.waitFor(); assert.match(await panel.innerText(), /tidak menggantikan foto wajib/);
  const prefix = (review ? reviewerPath : requesterPath) + '/' + supplement.supplementId;
  const reply = page.waitForResponse(response => new URL(response.url()).pathname === prefix + '/pdf-ocr/query');
  await panel.getByRole('button', {name: 'Muat hasil OCR PDF', exact: true}).click(); const response = await reply; assert.equal(response.status(), 200);
  const wrapper = await response.json(); primaryTuple(wrapper); assert.equal(wrapper.supplementId, supplement.supplementId); assert.equal(wrapper.supplementRevision, '2');
  assert.deepEqual(wrapper.pdfEvidence, evidence); const view = wrapper.ocr;
  assert.equal(view.sourceKind, 'PaymentSupplement'); assert.equal(view.sourceId, supplement.supplementId); assert.equal(view.sourceRevision, '2');
  assert.equal(view.submissionId, supplement.supplementId); assert.deepEqual(view.evidence, evidence); assert.equal(view.state, 'succeeded'); assert.equal(view.failureCode, null);
  assert.equal(view.result.requiresHumanReview, true); assert.equal(view.result.purpose, 'Payment'); assert.equal(view.result.pages.length, 2);
  assert.deepEqual(view.result.pages.map(item => item.raster.pageIndex), [0, 1]);
  const amount = view.result.fields.find(item => item.field === 'amount'); assert.ok(amount);
  assert.deepEqual(amount.observations.map(item => item.candidate.value), ['1234567.89', '2345678.90']); assert.ok(amount.reviewReasons.includes('contradictory_pages'));
  assert.equal(await panel.locator('[data-pdf-ocr-page]').count(), 2); await panel.getByRole('heading', {name: 'Halaman 1 dari 2', exact: true}).waitFor();
  await panel.getByRole('heading', {name: 'Halaman 2 dari 2', exact: true}).waitFor(); assert.match(await panel.innerText(), /Nilai antarhalaman berbeda/);
  assert.equal(await panel.getByRole('button', {name: /^Pilih rujukan halaman/}).count(), 0);
  await download(page, panel, prefix + `/pdf-evidence/${reservation.reservationId}/original/query`);
  const second = panel.locator('[data-pdf-ocr-page="1"]').locator('article').filter({has: page.getByRole('heading', {name: 'Jumlah transfer', exact: true})});
  await second.getByRole('checkbox').check(); await second.getByRole('button', {name: 'Pilih rujukan halaman 2: Jumlah transfer', exact: true}).click();
  const selection = panel.getByRole('region', {name: 'Rujukan observasi lampiran PDF tambahan', exact: true});
  await selection.waitFor(); assert.match(await selection.innerText(), /halaman 2 · Nominal \(IDR\)/); assert.match(await selection.innerText(), /2345678\.90/);
  assert.match(await selection.innerText(), /tidak mengisi koreksi foto/);
  await second.locator('summary').click(); assert.match(await second.innerText(), /Empat sudut pada kanvas PDF/);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({path: resolve(directory, `${label}-desktop.png`), fullPage: true}); await page.setViewportSize({width: 390, height: 844});
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({path: resolve(directory, `${label}-mobile.png`), fullPage: true}); await page.setViewportSize({width: 1280, height: 960}); return wrapper;
}
async function post(page, actorId, path, body, revision) {
  return page.evaluate(async ({actorId, path, body, revision}) => {const csrf = await fetch('/api/v1/identity/csrf', {credentials: 'same-origin', cache: 'no-store', redirect: 'error'});
    if (!csrf.ok) throw new Error('Actual CSRF endpoint unavailable'); const token = await csrf.json();
    const response = await fetch(path, {method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error',
      headers: {'Content-Type': 'application/json', 'X-CSRF-Token': token.requestToken, 'X-Expected-Actor-Id': actorId, 'If-Match': `"${revision}"`}, body: JSON.stringify(body)});
    const value = await response.json().catch(() => null); return {status: response.status, value};
  }, {actorId, path, body, revision});
}
try {
  await app.prepare(); const handle = app.getRequestHandler(); frontend = track(http.createServer((req, res) => {void handle(req, res).catch(() => {res.statusCode = 500; res.end();});}));
  const frontendPort = await listen(frontend); edge = track(https.createServer({pfx: Buffer.from(config.pfx, 'base64'), passphrase: config.password}, (req, res) => {
    if (req.headers.host !== origin.host) {res.writeHead(403); res.end(); return;}
    const isApi = req.url.startsWith('/api/v1/') || req.url.startsWith('/auth/'); forward(req, res, isApi ? Number(api.port) : frontendPort, isApi);
  })); const tlsPort = await listen(edge);
  tunnel = track(http.createServer((_req, res) => {res.writeHead(403); res.end();})); tunnel.on('connect', (req, socket, head) => {
    if (req.url !== origin.host + ':443') {socket.end('HTTP/1.1 403 Forbidden\r\n\r\n'); return;}
    const upstream = net.connect(tlsPort, '127.0.0.1', () => {socket.write('HTTP/1.1 200 Connection Established\r\n\r\n'); if (head.length) upstream.write(head); socket.pipe(upstream); upstream.pipe(socket);});
    sockets.add(upstream); upstream.on('close', () => sockets.delete(upstream)); upstream.on('error', () => socket.destroy()); socket.on('error', () => upstream.destroy()); socket.on('close', () => upstream.destroy());
  }); const proxyPort = await listen(tunnel), channel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'msedge'; assert.ok(['chromium', 'msedge'].includes(channel));
  browser = await chromium.launch({...(channel === 'msedge' ? {channel} : {}), headless: true, args: ['--ignore-certificate-errors-spki-list=' + config.spki], proxy: {server: 'http://127.0.0.1:' + proxyPort}});
  const actor = await user('actor'), page = actor.page; await open(page); await primaryOriginal(page);
  const shown = await load(page); assert.deepEqual(shown.items, []); await desk(page).getByText('Belum ada lampiran PDF tambahan pada pengajuan ini.', {exact: true}).waitFor();
  await page.screenshot({path: resolve(directory, 'requester-empty-desktop.png'), fullPage: true});
  await desk(page).getByLabel('Alasan membuat lampiran PDF tambahan', {exact: true}).fill('Dokumen tambahan sintetis; gambar utama tetap sah dan wajib');
  const createDrop = armDroppedResponse('create'); await desk(page).getByRole('button', {name: 'Buat draf lampiran tambahan', exact: true}).click();
  await waitForDroppedResponse(createDrop);
  await desk(page).getByRole('region', {name: 'Pemulihan lampiran PDF tambahan', exact: true}).waitFor();
  supplement = (await recover(page, 'billing.payment.supplement.create', requesterPath + '/operations/query', config.declarationRevision)).result;
  primaryTuple(supplement); assert.equal(supplement.state, 'draft'); assert.equal(supplement.pdfEvidence, null);
  await load(page, {id: supplement.supplementId});
  const admission = () => desk(page).getByRole('region', {name: 'Unggah lampiran PDF tambahan', exact: true});
  await admission().getByRole('button', {name: 'Muat status PDF tambahan', exact: true}).click();
  await admission().getByText('Belum ada upaya PDF untuk lampiran ini.', {exact: true}).waitFor();
  await admission().getByLabel('PDF tambahan asli', {exact: true}).setInputFiles({name: config.pdfFilename, mimeType: 'application/pdf', buffer: original});
  const reserveDrop = armDroppedResponse('reserve'); await admission().getByRole('button', {name: 'Unggah PDF tambahan', exact: true}).click();
  await waitForDroppedResponse(reserveDrop);
  await desk(page).getByRole('region', {name: 'Pemulihan lampiran PDF tambahan', exact: true}).waitFor(); assert.equal(writes.filter(row => row.kind === 'upload').length, 0);
  reservation = (await recover(page, 'evidence.payment-supplement-pdf.reserve', requesterPath + `/${supplement.supplementId}/pdf-evidence/operations/query`, '1')).reservation;
  await load(page, {id: supplement.supplementId}); await admission().getByRole('button', {name: 'Muat status PDF tambahan', exact: true}).click();
  await admission().getByLabel('PDF tambahan asli', {exact: true}).setInputFiles({name: config.pdfFilename, mimeType: 'application/pdf', buffer: original});
  const uploadedReply = page.waitForResponse(response => new URL(response.url()).pathname === requesterPath + `/${supplement.supplementId}/pdf-evidence/${reservation.reservationId}/content`, {timeout: 120000});
  await admission().getByRole('button', {name: `Lanjutkan reservasi PDF ${reservation.reservationId}`, exact: true}).click();
  const uploaded = await uploadedReply; assert.equal(uploaded.status(), 200); const received = await uploaded.json(); assert.equal(received.state, 'quarantined');
  evidence = received.reference; assert.equal(evidence.sha256, config.pdfSha256); assert.notDeepEqual(evidence, config.primaryEvidence);
  console.log('SUPPLEMENT_PDF_UPLOADED'); await ready(config.validationReadyPath);
  await admission().getByRole('button', {name: 'Muat status PDF tambahan', exact: true}).click();
  await admission().getByText('Pemeriksaan keamanan PDF selesai; periksa seluruh dokumen asli.', {exact: true}).waitFor();
  const attach = () => admission().getByRole('button', {name: `Tautkan sebagai lampiran tambahan ${reservation.reservationId}`, exact: true});
  assert.equal(await attach().isDisabled(), true); await admission().getByLabel('Alasan menautkan PDF tambahan', {exact: true}).fill('PDF sintetis dua halaman untuk ditinjau; tidak menyatakan dana diterima');
  await admission().getByRole('checkbox', {name: /Saya memahami PDF ini hanya lampiran tambahan/}).check();
  await actor.context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await attach().isDisabled(), true);
  await actor.context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({state: 'hidden'});
  const attachDrop = armDroppedResponse('attach'); await attach().click(); await waitForDroppedResponse(attachDrop);
  await desk(page).getByRole('region', {name: 'Pemulihan lampiran PDF tambahan', exact: true}).waitFor();
  console.log('SUPPLEMENT_PDF_ATTACHED'); await ready(config.extractionReadyPath);
  supplement = (await recover(page, 'billing.payment.supplement.attach', requesterPath + `/${supplement.supplementId}/attachment/operations/query`, '2')).result;
  primaryTuple(supplement); assert.equal(supplement.state, 'attached'); assert.equal(supplement.supplementRevision, '2'); assert.deepEqual(supplement.pdfEvidence, evidence);
  finalView = await inspect(page, false, 'requester-native-all-pages');
  await page.reload(); await page.getByRole('button', {name: `Buka pembayaran ${config.declarationId}`, exact: true}).click();
  const refreshed = await inspect(page, false, 'requester-refreshed'); assert.equal(refreshed.ocr.result.id, finalView.ocr.result.id); assert.equal(refreshed.ocr.result.resultHash, finalView.ocr.result.resultHash);
  await primaryOriginal(page);
  const other = await user('outsider'); await other.page.goto(new URL('/identity/account', origin).href);
  const foreignRead = await post(other.page, config.outsiderId, requesterPath + `/${supplement.supplementId}/pdf-ocr/query`, {purpose: 'payment-supplement-pdf-ocr-view', reference: evidence}, '2'); assert.equal(foreignRead.status, 403);
  const foreignOriginal = await post(other.page, config.outsiderId, requesterPath + `/${supplement.supplementId}/pdf-evidence/${reservation.reservationId}/original/query`, {purpose: 'payment-supplement-pdf-view', reference: evidence}, '2'); assert.equal(foreignOriginal.status, 403);
  const stale = await post(page, config.actorId, requesterPath + `/${supplement.supplementId}/pdf-ocr/query`, {purpose: 'payment-supplement-pdf-ocr-view', reference: evidence}, '1'); assert.equal(stale.status, 409);
  const reviewer = await user('reviewer'); await open(reviewer.page, true); await primaryOriginal(reviewer.page, true);
  const reviewed = await inspect(reviewer.page, true, 'independent-reviewer'); assert.equal(reviewed.ocr.result.id, finalView.ocr.result.id);
  await reviewer.page.reload(); await reviewer.page.getByRole('button', {name: `Periksa pengajuan ${config.declarationId}`, exact: true}).click();
  const reread = await inspect(reviewer.page, true, 'independent-reviewer-refreshed'); assert.equal(reread.ocr.result.resultHash, finalView.ocr.result.resultHash);
  assert.equal(await desk(reviewer.page).getByRole('button', {name: 'Buat draf lampiran tambahan', exact: true}).count(), 0);
  assert.equal(await desk(reviewer.page).getByRole('button', {name: /Tautkan sebagai lampiran/}).count(), 0);
  assert.equal(await reviewer.page.getByRole('button', {name: 'Simpan koreksi', exact: true}).count(), 0);
  for (const operation of ['create', 'reserve', 'upload', 'attach']) assert.equal(writes.filter(row => row.kind === operation).length, 1);
  assert.ok(writes.every(row => row.actor === config.actorId && row.csrf && row.origin === origin.origin)); assert.equal(dropped, 3);
  assert.equal(new Set(writes.map(row => row.key)).size, 4); assert.deepEqual(errors, []);
  const proof = {acceptance: 'local-synthetic-inputs-actual-native-runtime', primaryEvidence: config.primaryEvidence, declarationId: config.declarationId,
    declarationRevision: config.declarationRevision, submissionId: config.submissionId, supplementId: supplement.supplementId, supplementRevision: '2',
    reservationId: reservation.reservationId, pdfEvidence: evidence, resultId: finalView.ocr.result.id, resultHash: finalView.ocr.result.resultHash,
    pageCount: 2, recoveredLostResponses: dropped, nativeAllPages: true, unchangedPrimaryImage: true, foreignScopeDenied: true,
    independentReviewer: true, desktopAndMobile: true, noFinancialCorrection: true, writes: writes.map(observedProof), reads: reads.map(observedProof),
    responseBinding: {requester: finalView, requesterRefreshed: refreshed, reviewer: reviewed, reviewerRefreshed: reread}};
  writeFileSync(resolve(directory, 'proof.json'), JSON.stringify(proof, null, 2), 'utf8');
  console.log('REAL_PAYMENT_SUPPLEMENT_BROWSER_PASS ' + JSON.stringify({supplementId: supplement.supplementId, reservationId: reservation.reservationId,
    resultId: finalView.ocr.result.id, nativePages: 2, lostResponsesRecovered: dropped, actualPrimaryImage: true, independentReviewer: true, foreignScopeDenied: true,
    desktopAndMobile: true, acceptance: proof.acceptance}));
} catch (error) {
  persistTrace('failed');
  for (const {who, page} of pages) if (!page.isClosed()) {console.error('SUPPLEMENT PAGE ' + who + ': ' + safe(await page.locator('main').innerText().catch(() => 'unavailable')));
    await page.screenshot({path: resolve(directory, `failure-${who}.png`), fullPage: true}).catch(() => {});}
  console.error(safe(error.stack ?? String(error))); process.exitCode = 1;
} finally {
  let timer; try {await Promise.race([(async () => {if (browser) await browser.close(); for (const socket of sockets) socket.destroy();
    for (const server of [tunnel, edge, frontend]) if (server) await new Promise(yes => server.close(yes)); await app.close();})(),
    new Promise((_, no) => {timer = setTimeout(() => no(new Error('Fixture cleanup exceeded 10 seconds')), 10000);})]);}
  catch (failure) {console.error(safe(failure)); process.exitCode = 1;} finally {clearTimeout(timer); persistTrace(process.exitCode ? 'failed' : 'succeeded');}
}
process.exit(process.exitCode ?? 0);
