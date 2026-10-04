// Isolated production Next/HTTPS/Kestrel/Mongo with genuine private originals. No provider or scanner acceptance claim.
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
const load = createRequire(config.playwrightPackage), metadata = load(config.playwrightPackage);
assert.equal(metadata.name, 'playwright'); assert.equal(metadata.version, '1.62.1');
const {chromium} = load('playwright'), channel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'chromium';
assert.ok(['chromium', 'msedge'].includes(channel));
const directory = resolve(config.directory); mkdirSync(directory, {recursive: true});
const app = next({dev: false, dir: process.cwd(), quiet: true}), sockets = new Set(), pages = [], errors = [], writes = [], recoveries = [], buyerReads = [], imageResponses = [];
let browser, frontend, edge, tunnel, dropAssociation = false, dropped = 0;
const staffRoot = `/api/v1/catalog/skus/${config.skuId}`, buyerRoute = `/api/v1/catalog/variants/${config.publicId}/primary-photo/query`;
function track(server) { server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); }); return server; }
async function listen(server) { await new Promise((yes, no) => { server.once('error', no); server.listen(0, '127.0.0.1', yes); }); return server.address().port; }
function forward(req, res, port, isApi) {
  const path = new URL(req.url, origin).pathname;
  const headers = {...req.headers, host: '127.0.0.1:' + port};
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded', 'connection'].includes(name)) delete headers[name];
  if (isApi) { headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https'; }
  const association = req.method === 'POST' && path === staffRoot + '/primary-photo';
  const mutation = association || req.method === 'POST' && (path === staffRoot + '/photo-evidence' || new RegExp('^' + staffRoot + '/photo-evidence/[1-9][0-9]*/content$').test(path));
  const recovery = req.method === 'POST' && path === '/api/v1/catalog/primary-photos/operations/query';
  const buyerRead = req.method === 'POST' && path === buyerRoute;
  const observed = {path, actor: req.headers['x-expected-actor-id'], key: req.headers['idempotency-key'],
    csrfPresent: typeof req.headers['x-csrf-token'] === 'string', origin: req.headers.origin};
  if (mutation) writes.push(observed); if (recovery) recoveries.push(observed); if (buyerRead) buyerReads.push(observed);
  if (association || recovery || buyerRead) {
    const body = []; req.on('data', chunk => body.push(chunk)); req.on('end', () => {
      try { observed.body = JSON.parse(Buffer.concat(body).toString('utf8')); } catch { observed.body = null; }
    });
  }
  const drop = association && dropAssociation; if (drop) dropAssociation = false;
  const upstream = http.request({hostname: '127.0.0.1', port, path: req.url, method: req.method, headers}, response => {
    observed.status = response.statusCode;
    if (buyerRead) observed.sha256 = response.headers['x-content-sha256'];
    if (drop && response.statusCode === 200) {
      response.resume(); response.once('end', () => {
        dropped++; res.writeHead(200, {'content-type': 'application/json', 'content-length': '64', 'cache-control': 'no-store'});
        res.flushHeaders(); res.write('{'); setTimeout(() => res.destroy(), 25);
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
  page.on('response', response => {
    const path = new URL(response.url()).pathname, headers = response.headers();
    if (path.startsWith('/api/v1/catalog/') && headers['content-type']?.startsWith('image/'))
      imageResponses.push({who, status: response.status(), headers});
  });
  page.on('dialog', dialog => dialog.accept()); pages.push({page, who}); return {context, page};
}
const journal = page => page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage.getItem(key))));
async function openPhoto(page, recovery = false) {
  if (recovery) await page.getByRole('button', {name: `Lanjutkan foto SKU ${config.skuId}`, exact: true}).click();
  else {
    await page.goto(origin.origin + '/administration/catalog');
    const owner = page.getByRole('combobox', {name: 'Toko atau gudang pemilik katalog', exact: true});
    await owner.locator('option').nth(1).waitFor({state: 'attached'});
    // The genuine fixture grants three owners; this SKU belongs to its Shop owner.
    const targetOwner = owner.getByRole('option', {name: 'Organisasi 100 / Toko 101 / Kanal 103', exact: true});
    assert.equal(await targetOwner.count(), 1);
    const selectedOwner = await targetOwner.getAttribute('value'); assert.ok(selectedOwner);
    await owner.selectOption(selectedOwner);
    await page.getByRole('button').filter({has: page.getByText(config.familyName, {exact: true})}).click();
    await page.getByRole('button', {name: `Foto ${config.skuCode}`, exact: true}).click();
  }
  const panel = page.getByRole('region', {name: 'Foto utama varian', exact: true}); await panel.waitFor();
  await panel.getByRole('button', {name: 'Muat ulang sumber foto', exact: true}).waitFor();
  return panel;
}
async function verifiedImage(image, expected, width, height) {
  await image.waitFor({state: 'visible'});
  const found = await image.evaluate(async element => {
    await element.decode();
    if (!element.currentSrc.startsWith('blob:')) throw new Error('Expected a verified private blob image');
    const bytes = await (await fetch(element.currentSrc)).arrayBuffer();
    const hash = [...new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))].map(value => value.toString(16).padStart(2, '0')).join('');
    return {hash, width: element.naturalWidth, height: element.naturalHeight};
  });
  assert.deepEqual(found, {hash: expected, width, height});
}
async function uploadAndReview(page, panel, base64, hash, width, height, sequence) {
  const before = writes.length;
  await panel.getByLabel('Foto asli PNG atau JPEG', {exact: false}).setInputFiles({name: `synthetic-catalog-${sequence}.png`, mimeType: 'image/png', buffer: Buffer.from(base64, 'base64')});
  await panel.getByLabel('Alasan penetapan foto utama', {exact: true}).fill(`Foto SINTETIS ${sequence}, diperiksa sesuai kemasan yang sama`);
  await panel.getByRole('button', {name: 'Unggah foto asli', exact: true}).click();
  await panel.getByText('Unggah selesai. Periksa status keamanan dan format sebelum meninjau foto asli.', {exact: true}).waitFor();
  assert.equal(writes.length, before + 2); assert.equal(writes[before].path, staffRoot + '/photo-evidence');
  assert.match(writes[before + 1].path, /\/photo-evidence\/[1-9][0-9]*\/content$/);
  assert.equal(writes[before].status, 200); assert.equal(writes[before + 1].status, 200);
  // Native parent runs the actual outbox processor with its explicitly synthetic scanner and real native decoder.
  console.log('CATALOG_PHOTO_VALIDATE_ORIGINAL');
  const review = panel.getByRole('button', {name: 'Tinjau foto asli tervalidasi', exact: true});
  for (let attempt = 0; attempt < 15 && !(await review.isVisible()); attempt++) {
    await panel.getByRole('button', {name: 'Periksa status foto', exact: true}).click();
    if (!(await review.isVisible())) await new Promise(yes => setTimeout(yes, 1000));
  }
  await review.waitFor(); await review.click();
  await verifiedImage(panel.getByRole('img', {name: `Foto asli ${config.skuCode}, ${config.packageLabel}`, exact: true}), hash, width, height);
  const consent = panel.getByRole('checkbox', {name: 'Saya sudah meninjau foto asli dan memastikan foto sesuai dengan varian serta kemasan ini.', exact: true});
  assert.equal(await consent.isChecked(), false); assert.equal(await panel.getByRole('button', {name: 'Simpan foto utama', exact: true}).isDisabled(), true);
  await consent.check(); await page.screenshot({path: resolve(directory, `staff-reviewed-${sequence}.png`), fullPage: true});
}
async function buyerImage(page, hash, width, height, mode = 'card') {
  const scope = mode === 'dialog' ? page.getByRole('dialog') : page.getByRole('region', {name: mode === 'direct' ? 'Rincian kemasan' : 'Kemasan yang tersedia', exact: true});
  await verifiedImage(scope.getByRole('img', {name: `Foto ${config.familyName} — ${config.packageLabel}`, exact: true}), hash, width, height);
}
async function protectedRead(page, actorId, path) {
  return page.evaluate(async ({actorId, path}) => {
    const tokenResponse = await fetch('/api/v1/identity/csrf', {credentials: 'same-origin', cache: 'no-store'});
    if (!tokenResponse.ok) throw new Error('Actual request token unavailable');
    const token = await tokenResponse.json();
    const response = await fetch(path, {method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error',
      headers: {'Content-Type': 'application/json', 'Origin': location.origin, 'X-CSRF-Token': token.requestToken, 'X-Expected-Actor-Id': actorId},
      body: JSON.stringify({purpose: 'catalog-photo'})});
    return {status: response.status, mime: response.headers.get('content-type'), hash: response.headers.get('x-content-sha256'), bytes: (await response.arrayBuffer()).byteLength};
  }, {actorId, path});
}
try {
  await app.prepare(); const handle = app.getRequestHandler();
  frontend = track(http.createServer((req, res) => { void handle(req, res).catch(() => { res.statusCode = 500; res.end(); }); }));
  const frontendPort = await listen(frontend);
  edge = track(https.createServer({pfx: Buffer.from(config.pfx, 'base64'), passphrase: config.password}, (req, res) => {
    if (req.headers.host !== origin.host) { res.writeHead(403); res.end(); return; }
    const isApi = req.url.startsWith('/api/v1/') || req.url.startsWith('/auth/'); forward(req, res, isApi ? Number(api.port) : frontendPort, isApi);
  })); const tlsPort = await listen(edge);
  tunnel = track(http.createServer((_req, res) => { res.writeHead(403); res.end(); }));
  tunnel.on('connect', (req, socket, head) => {
    if (req.url !== origin.host + ':443') { socket.end('HTTP/1.1 403 Forbidden\r\n\r\n'); return; }
    const upstream = net.connect(tlsPort, '127.0.0.1', () => { socket.write('HTTP/1.1 200 Connection Established\r\n\r\n'); if (head.length) upstream.write(head); socket.pipe(upstream); upstream.pipe(socket); });
    sockets.add(upstream); upstream.on('close', () => sockets.delete(upstream)); upstream.on('error', () => socket.destroy()); socket.on('error', () => upstream.destroy()); socket.on('close', () => upstream.destroy());
  }); const proxyPort = await listen(tunnel);
  browser = await chromium.launch({...(channel === 'msedge' ? {channel} : {}), headless: true,
    args: ['--ignore-certificate-errors-spki-list=' + config.spki], proxy: {server: 'http://127.0.0.1:' + proxyPort}});
  const staff = await user('staff'), buyer = await user('buyer', true);
  let panel = await openPhoto(staff.page);
  await panel.getByText('Belum ada foto utama. Tidak ada gambar pengganti yang diasumsikan.', {exact: true}).waitFor();
  await uploadAndReview(staff.page, panel, config.firstPng, config.firstHash, 13, 7, 'pertama');
  dropAssociation = true;
  await panel.getByRole('button', {name: 'Simpan foto utama', exact: true}).click();
  await panel.getByRole('button', {name: 'Periksa hasil foto utama', exact: true}).waitFor();
  assert.equal(dropped, 1); const association = writes.find(row => row.path === staffRoot + '/primary-photo'); assert.ok(association); assert.equal(association.status, 200);
  const pending = await journal(staff.page); assert.equal(pending.length, 2);
  const saved = pending.find(row => row.operation === 'catalog.photo.associate'); assert.ok(saved);
  assert.deepEqual(Object.keys(saved).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.equal(saved.actorId, config.staffId); assert.equal(saved.key, association.key);
  const beforeRecovery = writes.length;
  await staff.page.reload(); panel = await openPhoto(staff.page, true);
  await panel.getByRole('button', {name: 'Periksa hasil foto utama', exact: true}).click();
  await staff.page.getByText('Foto utama tersimpan. Kelompok produk dan varian terbaru dimuat kembali.', {exact: true}).waitFor();
  assert.equal(writes.length, beforeRecovery); assert.equal(recoveries.length, 1);
  assert.deepEqual(recoveries[0].body, {purpose: 'catalog-photo', key: saved.key}); assert.equal(recoveries[0].actor, config.staffId);
  panel = await openPhoto(staff.page, true);
  await panel.getByRole('button', {name: 'Periksa tempat unggah foto', exact: true}).click();
  await panel.getByText('Unggah ini sudah tercatat sebagai foto utama. Referensi pemulihan telah diselesaikan.', {exact: true}).waitFor();
  assert.deepEqual(await journal(staff.page), []); assert.equal(writes.length, beforeRecovery);
  await panel.getByRole('button', {name: 'Lihat foto yang tercatat', exact: true}).click();
  await verifiedImage(panel.getByRole('img', {name: `Foto asli ${config.skuCode}, ${config.packageLabel}`, exact: true}), config.firstHash, 13, 7);
  await panel.getByRole('button', {name: 'Tutup pratinjau foto', exact: true}).click();
  await buyer.page.goto(origin.origin + '/shop'); await buyerImage(buyer.page, config.firstHash, 13, 7);
  await buyer.page.screenshot({path: resolve(directory, 'buyer-first-photo-mobile.png'), fullPage: true});
  await buyer.page.reload(); await buyerImage(buyer.page, config.firstHash, 13, 7);
  await buyer.page.getByRole('button', {name: `Rincian ${config.skuCode} ${config.packageLabel}`, exact: true}).click();
  await buyerImage(buyer.page, config.firstHash, 13, 7, 'dialog');
  await buyer.page.getByRole('button', {name: 'Tutup rincian', exact: true}).click();
  await uploadAndReview(staff.page, panel, config.secondPng, config.secondHash, 17, 9, 'pengganti');
  await panel.getByRole('button', {name: 'Simpan foto utama', exact: true}).click();
  await staff.page.getByText('Foto utama tersimpan. Kelompok produk dan varian terbaru dimuat kembali.', {exact: true}).waitFor();
  assert.deepEqual(await journal(staff.page), []);
  assert.equal(writes.filter(row => row.path === staffRoot + '/primary-photo').length, 2);
  assert.equal(writes.filter(row => row.path === staffRoot + '/photo-evidence').length, 2);
  assert.equal(writes.filter(row => row.path.endsWith('/content')).length, 2);
  await buyer.page.reload(); await buyerImage(buyer.page, config.secondHash, 17, 9);
  assert.equal(await buyer.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await buyer.page.screenshot({path: resolve(directory, 'buyer-replacement-photo-mobile.png'), fullPage: true});
  await buyer.page.goto(origin.origin + '/shop/' + config.publicId); await buyerImage(buyer.page, config.secondHash, 17, 9, 'direct');
  await buyer.page.reload(); await buyerImage(buyer.page, config.secondHash, 17, 9, 'direct');
  panel = await openPhoto(staff.page); await panel.getByText(/Revisi foto 2 · tersimpan/).waitFor();
  await panel.getByRole('button', {name: 'Lihat foto yang tercatat', exact: true}).click();
  await verifiedImage(panel.getByRole('img', {name: `Foto asli ${config.skuCode}, ${config.packageLabel}`, exact: true}), config.secondHash, 17, 9);
  await staff.page.screenshot({path: resolve(directory, 'staff-replacement-persisted.png'), fullPage: true});
  const outsider = await user('outsider'); await outsider.page.goto(origin.origin + '/shop');
  const denied = await protectedRead(outsider.page, config.outsiderId, staffRoot + '/primary-photo/original/query');
  assert.equal(denied.status, 403); assert.equal(denied.hash, null); assert.ok(!denied.mime?.startsWith('image/'));
  const hidden = await protectedRead(outsider.page, config.outsiderId, buyerRoute);
  // A caller without catalog-read authority is denied before the no-photo projection.
  assert.equal(hidden.status, 403); assert.equal(hidden.hash, null); assert.ok(!hidden.mime?.startsWith('image/'));
  assert.equal(await outsider.page.getByRole('img', {name: `Foto ${config.familyName} — ${config.packageLabel}`, exact: true}).count(), 0);
  for (const write of writes) { assert.equal(write.actor, config.staffId); assert.equal(write.origin, origin.origin); assert.equal(write.csrfPresent, true); assert.equal(write.status, 200); }
  for (const read of buyerReads) { assert.deepEqual(read.body, {purpose: 'catalog-photo'}); assert.equal(read.csrfPresent, true); }
  assert.ok(buyerReads.some(row => row.actor === config.buyerId && row.sha256 === config.firstHash));
  assert.ok(buyerReads.some(row => row.actor === config.buyerId && row.sha256 === config.secondHash));
  assert.ok(imageResponses.some(row => row.who === 'staff')); assert.ok(imageResponses.some(row => row.who === 'buyer'));
  for (const image of imageResponses) {
    assert.notEqual(image.who, 'outsider'); assert.equal(image.status, 200); assert.equal(image.headers['content-type'], 'image/png');
    assert.match(image.headers['cache-control'], /private/); assert.match(image.headers['cache-control'], /no-store/);
    assert.equal(image.headers['x-content-type-options'], 'nosniff');
    assert.ok([config.firstHash, config.secondHash].includes(image.headers['x-content-sha256']));
    assert.ok(Number(image.headers['content-length']) > 0);
  }
  assert.deepEqual(errors, []);
  console.log('REAL_CATALOG_PHOTO_BROWSER_PASS ' + JSON.stringify({originals: 2, explicitConsent: true, originalKeyRecovery: true, noResend: true, buyerCurrentBytes: true, refreshPersistence: true, foreignScopeDenied: true, mobile: true}));
} catch (error) {
  for (const {page, who} of pages) if (!page.isClosed()) {
    console.error('PHOTO FIXTURE PAGE ' + who + ': ' + (await page.locator('main').innerText().catch(() => 'unavailable')).slice(0, 5000));
    await page.screenshot({path: resolve(directory, 'failure-' + who + '.png'), fullPage: true}).catch(() => {});
  }
  console.error(error.stack ?? String(error)); process.exitCode = 1;
} finally {
  let timer;
  try {
    await Promise.race([(async () => {
      if (browser) await browser.close(); for (const socket of sockets) socket.destroy();
      for (const server of [tunnel, edge, frontend]) if (server) await new Promise(yes => server.close(yes)); await app.close();
    })(), new Promise((_, no) => { timer = setTimeout(() => no(new Error('Photo fixture cleanup exceeded 10 seconds')), 10000); })]);
  } catch (error) { console.error(error); process.exitCode = 1; }
  finally { clearTimeout(timer); }
}
process.exit(process.exitCode ?? 0);
