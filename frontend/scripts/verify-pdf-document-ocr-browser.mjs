// Supplier PDF only: payment P01 still requires a primary image. No supplementary payment producer is accepted here.
// Named synthetic issued sessions; actual production Next, pinned HTTPS, .NET, Mongo and private native PDF processing.
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
assert.equal(config.mode, 'supplier'); assert.equal(config.expectedSourceKind, 'SupplierInvoice'); assert.equal(config.expectedAmountField, 'total_amount');
assert.equal(config.sourceRevision, '1'); assert.match(config.sourceId, /^[1-9][0-9]*$/);
const require = createRequire(config.playwrightPackage), metadata = require(config.playwrightPackage);
assert.equal(metadata.name, 'playwright'); assert.equal(metadata.version, '1.62.1'); const {chromium} = require('playwright');
const directory = resolve(config.runDirectory); mkdirSync(directory, {recursive: true});
const original = Buffer.from(config.pdfBase64, 'base64'), hash = bytes => createHash('sha256').update(bytes).digest('hex');
assert.equal(hash(original), config.pdfSha256); assert.ok(original.length > 0 && original.length <= 10 * 1024 * 1024);
const app = next({dev: false, dir: process.cwd(), quiet: true}), sockets = new Set(), pages = [], errors = [], writes = [], reads = [];
let frontend, edge, tunnel, browser, reservationId, evidence, view;
const sourcePrefix = `/api/v1/procurement/supplier-documents/drafts/${config.sourceId}`;
const submissionPath = sourcePrefix + '/submissions';
const screenPath = '/procurement/supplier-documents';
const safe = value => {let text = String(value); for (const secret of [config.actorToken, config.reviewerToken, config.outsiderToken, config.pfx, config.password, config.pdfBase64])
  if (typeof secret === 'string' && secret) text = text.replaceAll(secret, '[REDACTED]'); return text.replace(/(__Host-ut_session|cookie|authorization)\s*[:=].*/gi, '$1=[REDACTED]').slice(0, 1400);};
function track(server) {server.on('connection', socket => {sockets.add(socket); socket.on('close', () => sockets.delete(socket));}); return server;}
async function listen(server) {await new Promise((yes, no) => {server.once('error', no); server.listen(0, '127.0.0.1', yes);}); return server.address().port;}
function forward(req, res, port, isApi) {
  const path = new URL(req.url, origin).pathname, headers = {...req.headers, host: '127.0.0.1:' + port};
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded', 'connection'].includes(name)) delete headers[name];
  if (isApi) {headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https';}
  const observed = {path, actor: req.headers['x-expected-actor-id'], key: req.headers['idempotency-key'], csrf: typeof req.headers['x-csrf-token'] === 'string', origin: req.headers.origin};
  if (req.method === 'POST' && (path === sourcePrefix + '/pdf-evidence' || path.includes('/pdf-evidence/') && path.endsWith('/content') || path === submissionPath)) writes.push(observed);
  if (req.method === 'POST' && (path.endsWith('/pdf-ocr/query') || path.endsWith('/evidence-format/query') || path.includes('/pdf-evidence/') && path.endsWith('/original/query'))) reads.push(observed);
  const upstream = http.request({hostname: '127.0.0.1', port, path: req.url, method: req.method, headers}, response => {res.writeHead(response.statusCode ?? 502, response.headers); response.pipe(res);});
  upstream.on('error', () => {if (!res.headersSent) res.writeHead(502); res.end();}); req.on('aborted', () => upstream.destroy()); req.pipe(upstream);
}
async function user(who) {
  const context = await browser.newContext({viewport: {width: 1280, height: 960}, locale: 'id-ID', timezoneId: 'Asia/Jakarta'});
  await context.addCookies([{name: '__Host-ut_session', value: config[who + 'Token'], domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax'}]);
  const page = await context.newPage(); page.setDefaultTimeout(30000); pages.push({who, page}); page.on('pageerror', error => errors.push(safe(error.message)));
  page.on('response', async response => {if (!new URL(response.url()).pathname.startsWith('/api/v1/') || response.status() < 400) return;
    const body = await response.json().catch(() => null); console.log('PDF_API_BOUNDARY ' + JSON.stringify({who, path: new URL(response.url()).pathname, status: response.status(), code: /^[a-z_]{1,100}$/.test(body?.code ?? '') ? body.code : undefined}));});
  return {context, page};
}
async function ready(path) {const deadline = performance.now() + 120000; while (!existsSync(path)) {assert.ok(performance.now() < deadline, 'Native processing readiness did not arrive within the bounded deadline'); await delay(250);}}
async function open(page, submitted = false) {
  await page.goto(new URL(screenPath, origin).href);
  const selector = page.getByLabel('Organisasi / toko / gudang / kanal', {exact: true}); await selector.waitFor();
  await page.waitForFunction(() => {const el = document.querySelector('select[aria-labelledby="supplier-document-location"]'); return el && !el.disabled && el.options.length > 1;});
  const options = await selector.locator('option').evaluateAll(items => items.map(item => item.value).filter(Boolean));
  if (!config.locationKey) assert.equal(options.length, 1, 'Supplier browser fixture must choose one actual configured location');
  await selector.selectOption(config.locationKey ?? options[0]);
  await page.getByRole('button', {name: new RegExp('^' + config.documentReference.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))}).click();
  await page.getByRole('heading', {name: submitted ? 'Dokumen tersimpan untuk pencocokan' : 'Draf menunggu bukti asli', exact: true}).waitFor();
}
async function download(page, button, route) {
  const reply = page.waitForResponse(response => new URL(response.url()).pathname === route && response.request().method() === 'POST'), downloaded = page.waitForEvent('download');
  await button.click(); const response = await reply; assert.equal(response.status(), 200);
  const headers = response.headers(); assert.equal(headers['content-type'], 'application/pdf'); assert.match(headers['cache-control'], /private/); assert.match(headers['cache-control'], /no-store/);
  assert.equal(headers['x-content-type-options'], 'nosniff'); assert.equal(headers['content-security-policy'], "default-src 'none'; sandbox"); assert.match(headers['content-disposition'], /^attachment;/);
  assert.equal(Number(headers['content-length']), original.length); assert.equal(hash(await response.body()), config.pdfSha256);
  const file = await downloaded; assert.equal(await file.failure(), null); const stream = await file.createReadStream(), chunks = []; let length = 0;
  for await (const chunk of stream) {length += chunk.length; assert.ok(length <= 10 * 1024 * 1024); chunks.push(chunk);}
  assert.equal(length, original.length); assert.equal(hash(Buffer.concat(chunks)), config.pdfSha256);
  assert.equal(await page.locator('iframe[src^="blob:"],img[src^="blob:"]').count(), 0, 'Original PDF download must not be represented as a page-image preview');
}
async function post(page, actorId, path, body, revision) {
  return page.evaluate(async ({actorId, path, body, revision}) => {const csrf = await fetch('/api/v1/identity/csrf', {credentials: 'same-origin', cache: 'no-store', redirect: 'error'});
    if (!csrf.ok) throw new Error('Actual CSRF endpoint unavailable'); const token = await csrf.json();
    const response = await fetch(path, {method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error',
      headers: {'Content-Type': 'application/json', 'X-CSRF-Token': token.requestToken, 'X-Expected-Actor-Id': actorId, 'If-Match': `"${revision}"`}, body: JSON.stringify(body)});
    const value = await response.json().catch(() => null); return {status: response.status, value};
  }, {actorId, path, body, revision});
}
async function result(page, prefix, role) {
  await page.setViewportSize({width: 1280, height: 960});
  const panel = page.getByRole('region', {name: 'Pemeriksaan PDF yang diajukan', exact: true}); await panel.waitFor();
  const reply = page.waitForResponse(response => new URL(response.url()).pathname === prefix + '/pdf-ocr/query');
  await panel.getByRole('button', {name: 'Muat hasil OCR PDF', exact: true}).click(); const response = await reply;
  assert.equal(response.status(), 200); const saved = await response.json(); assert.equal(saved.sourceId, config.sourceId); assert.equal(saved.sourceKind, config.expectedSourceKind);
  assert.equal(saved.state, 'succeeded'); assert.equal(saved.failureCode, null); assert.equal(saved.evidence.sha256, config.pdfSha256); assert.equal(saved.result.requiresHumanReview, true);
  assert.equal(saved.result.pages.length, 2); assert.deepEqual(saved.result.pages.map(page => page.raster.pageIndex), [0, 1]);
  const amounts = saved.result.fields.find(field => field.field === config.expectedAmountField); assert.ok(amounts);
  assert.deepEqual(amounts.observations.map(item => item.candidate.value), config.expectedAmounts); assert.ok(amounts.reviewReasons.includes('contradictory_pages'));
  assert.equal(await panel.locator('[data-pdf-ocr-page]').count(), 2); await panel.getByRole('heading', {name: 'Halaman 1 dari 2', exact: true}).waitFor();
  await panel.getByRole('heading', {name: 'Halaman 2 dari 2', exact: true}).waitFor();
  assert.match(await panel.innerText(), /Nilai antarhalaman berbeda/); assert.match(await panel.innerText(), /bukan transaksi tambahan/);
  const candidates = saved.result.pages.flatMap(page => page.fields);
  if (candidates.some(candidate => candidate.value === null)) assert.match(await panel.innerText(), /Belum diketahui/);
  if (candidates.some(candidate => candidate.flags.includes('low_confidence'))) assert.match(await panel.innerText(), /Keyakinan rendah; periksa ulang/);
  assert.equal(await panel.getByRole('button', {name: /^Pilih rujukan halaman/}).count(), 0, 'Original download is required before selecting any OCR observation');
  await download(page, panel.getByRole('button', {name: 'Buka PDF asli untuk pemeriksaan', exact: true}), `${prefix}/pdf-evidence/${reservationId}/original/query`);
  const second = panel.locator('[data-pdf-ocr-page="1"]').locator('article').filter({has: page.getByRole('heading', {name: 'Total tagihan', exact: true})});
  await second.getByRole('checkbox').check(); await second.getByRole('button', {name: /^Pilih rujukan halaman 2:/}).click();
  await panel.getByRole('heading', {name: 'Rujukan observasi yang dipilih', exact: true}).waitFor();
  assert.match(await panel.getByRole('region', {name: 'Rujukan observasi PDF yang dipilih', exact: true}).innerText(), new RegExp(`Halaman 2.*${config.expectedAmountField}`));
  await second.locator('summary').click(); assert.match(await second.innerText(), /Empat sudut pada kanvas PDF/);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({path: resolve(directory, `${config.mode}-${role}-desktop.png`), fullPage: true});
  await page.setViewportSize({width: 390, height: 844}); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({path: resolve(directory, `${config.mode}-${role}-mobile.png`), fullPage: true}); return saved;
}
try {
  await app.prepare(); const handle = app.getRequestHandler(); frontend = track(http.createServer((req, res) => {void handle(req, res).catch(() => {res.statusCode = 500; res.end();});})); const frontendPort = await listen(frontend);
  edge = track(https.createServer({pfx: Buffer.from(config.pfx, 'base64'), passphrase: config.password}, (req, res) => {if (req.headers.host !== origin.host) {res.writeHead(403); res.end(); return;}
    const isApi = req.url.startsWith('/api/v1/') || req.url.startsWith('/auth/'); forward(req, res, isApi ? Number(api.port) : frontendPort, isApi);})); const tlsPort = await listen(edge);
  tunnel = track(http.createServer((_req, res) => {res.writeHead(403); res.end();})); tunnel.on('connect', (req, socket, head) => {
    if (req.url !== origin.host + ':443') {socket.end('HTTP/1.1 403 Forbidden\r\n\r\n'); return;}
    const upstream = net.connect(tlsPort, '127.0.0.1', () => {socket.write('HTTP/1.1 200 Connection Established\r\n\r\n'); if (head.length) upstream.write(head); socket.pipe(upstream); upstream.pipe(socket);});
    sockets.add(upstream); upstream.on('close', () => sockets.delete(upstream)); upstream.on('error', () => socket.destroy()); socket.on('error', () => upstream.destroy()); socket.on('close', () => upstream.destroy());
  }); const proxyPort = await listen(tunnel), channel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'msedge'; assert.ok(['chromium', 'msedge'].includes(channel));
  browser = await chromium.launch({...(channel === 'msedge' ? {channel} : {}), headless: true, args: ['--ignore-certificate-errors-spki-list=' + config.spki], proxy: {server: 'http://127.0.0.1:' + proxyPort}});
  const actor = await user('actor'), page = actor.page; await open(page);
  assert.equal(await page.getByRole('region', {name: 'Bukti PDF asli', exact: true}).count(), 0); await page.getByRole('radio', {name: /Dokumen PDF asli/}).check();
  const admission = page.getByRole('region', {name: 'Bukti PDF asli', exact: true}); await admission.waitFor();
  assert.equal(await admission.getByRole('button', {name: 'Unggah PDF asli', exact: true}).isDisabled(), true);
  await admission.getByRole('button', {name: 'Muat status PDF', exact: true}).click(); await admission.getByText('Belum ada PDF pada sumber ini.', {exact: true}).waitFor();
  await admission.getByLabel('PDF asli', {exact: true}).setInputFiles({name: config.pdfFilename, mimeType: 'application/pdf', buffer: original});
  const reserveReply = page.waitForResponse(response => new URL(response.url()).pathname === sourcePrefix + '/pdf-evidence' && response.request().method() === 'POST');
  const uploadReply = page.waitForResponse(response => new URL(response.url()).pathname.startsWith(sourcePrefix + '/pdf-evidence/') && response.url().endsWith('/content'), {timeout: 120000});
  await admission.getByRole('button', {name: 'Unggah PDF asli', exact: true}).click(); const reserved = await reserveReply; assert.equal(reserved.status(), 200); reservationId = (await reserved.json()).reservationId;
  const uploaded = await uploadReply; assert.equal(uploaded.status(), 200); const receipt = await uploaded.json(); assert.equal(receipt.state, 'quarantined'); assert.equal(receipt.reference.sha256, config.pdfSha256); evidence = receipt.reference;
  console.log('PDF_UPLOADED'); await ready(config.validationReadyPath); await admission.getByRole('button', {name: 'Muat status PDF', exact: true}).click();
  await admission.getByText('Pemeriksaan keamanan PDF selesai; periksa seluruh dokumen asli.', {exact: true}).waitFor();
  const chosen = admission.getByRole('button', {name: 'Tinjau pengajuan dengan PDF ' + reservationId, exact: true}); assert.equal(await chosen.isDisabled(), true);
  await download(page, admission.getByRole('button', {name: `Unduh PDF asli ${reservationId} untuk pemeriksaan`, exact: true}), sourcePrefix + '/pdf-evidence/' + reservationId + '/original/query');
  await admission.getByRole('checkbox', {name: /Saya telah membaca seluruh halaman PDF asli/}).check();
  await page.getByLabel('Alasan pengajuan dokumen', {exact: true}).fill('Seluruh halaman PDF asli sintetis telah diperiksa manusia');
  await chosen.click(); const review = page.getByRole('region', {name: 'Tinjauan akhir dokumen', exact: true}); await review.waitFor();
  await review.getByRole('checkbox').check();
  const submittedReply = page.waitForResponse(response => new URL(response.url()).pathname === submissionPath && response.request().method() === 'POST');
  await review.getByRole('button', {name: 'Ajukan dokumen dengan bukti asli', exact: true}).click(); assert.equal((await submittedReply).status(), 200);
  console.log('PDF_SUBMITTED'); await ready(config.extractionReadyPath);
  const mounted = page.getByRole('region', {name: 'Pemeriksaan PDF yang diajukan', exact: true}); await mounted.waitFor(); view = await result(page, sourcePrefix, 'requester');
  const journal = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).length); assert.equal(journal, 0);
  await open(page, true); const freshView = await result(page, sourcePrefix, 'requester-reloaded'); assert.equal(freshView.result.id, view.result.id); assert.equal(freshView.result.resultHash, view.result.resultHash);
  const purpose = 'supplier-document-pdf-ocr-view';
  const stale = await post(page, config.actorId, sourcePrefix + '/pdf-ocr/query', {purpose, reference: evidence}, String(BigInt(view.sourceRevision) + BigInt(1))); assert.equal(stale.status, 403);
  const malformed = await post(page, config.actorId, sourcePrefix + '/pdf-ocr/query', {purpose, reference: evidence, userId: config.actorId}, view.sourceRevision); assert.equal(malformed.status, 400);
  const outsider = await user('outsider'); await outsider.page.goto(new URL('/identity/account', origin).href);
  const foreign = await post(outsider.page, config.outsiderId, sourcePrefix + '/pdf-ocr/query', {purpose, reference: evidence}, view.sourceRevision); assert.equal(foreign.status, 403);
  const foreignOriginal = await post(outsider.page, config.outsiderId, sourcePrefix + '/pdf-evidence/' + reservationId + '/original/query', {purpose: 'supplier-document-evidence-view', reference: evidence}, view.sourceRevision); assert.equal(foreignOriginal.status, 403);
  const reviewer = await user('reviewer'); await open(reviewer.page, true);
  const shown = await result(reviewer.page, sourcePrefix, 'reviewer'); assert.equal(shown.result.resultHash, view.result.resultHash);
  assert.equal(writes.filter(row => row.path === sourcePrefix + '/pdf-evidence').length, 1); assert.equal(writes.filter(row => row.path.endsWith('/content')).length, 1); assert.equal(writes.filter(row => row.path === submissionPath).length, 1);
  assert.ok(writes.every(row => row.actor === config.actorId && row.csrf && row.origin === origin.origin && typeof row.key === 'string'));
  assert.ok(reads.every(row => row.csrf && row.origin === origin.origin)); assert.deepEqual(errors, []);
  writeFileSync(resolve(directory, 'browser-proof.json'), JSON.stringify({mode: config.mode, sourceId: config.sourceId, reservationId, evidence, resultId: view.result.id, resultHash: view.result.resultHash,
    pages: view.result.pages.length, exactOriginalDownload: true, refreshedSameResult: true, staleDenied: true, foreignDenied: true, desktopMobile: true, writes, reads}, null, 2));
  console.log('REAL_PDF_DOCUMENT_BROWSER_PASS');
} catch (error) {
  console.error('PDF_BROWSER_FAILURE ' + safe(error instanceof Error ? error.message : error));
  for (const {who, page} of pages) {try {await page.screenshot({path: resolve(directory, `failed-${config.mode}-${who}.png`), fullPage: true}); writeFileSync(resolve(directory, `failed-${config.mode}-${who}.txt`), safe(await page.locator('body').innerText()));} catch {}}
  process.exitCode = 1;
} finally {
  await browser?.close(); for (const socket of sockets) socket.destroy();
  await Promise.allSettled([frontend, edge, tunnel].filter(Boolean).map(server => new Promise(yes => server.close(yes)))); process.exit(process.exitCode ?? 0);
}
