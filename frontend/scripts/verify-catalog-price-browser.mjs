// Isolated local rehearsal. Synthetic sessions are issued by the real session store, not by Auth0.
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import { createRequire } from 'node:module';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import next from 'next';
import { verifyPrimaryShopBrowser } from './verify-primary-shop-browser.mjs';

const chunks = []; for await (const chunk of process.stdin) chunks.push(chunk);
const config = JSON.parse(Buffer.concat(chunks).toString('utf8'));
assert.equal(process.versions.node.split('.')[0], '24');
const api = new URL(config.apiAddress), origin = new URL(config.origin);
assert.equal(api.protocol, 'http:'); assert.equal(api.hostname, '127.0.0.1'); assert.ok(Number(api.port) > 0);
assert.equal(origin.href, 'https://portal.unit-toko.co.id/');
const load = createRequire(config.playwrightPackage), metadata = load(config.playwrightPackage);
assert.equal(metadata.name, 'playwright'); assert.equal(metadata.version, '1.62.1');
const { chromium } = load('playwright'), channel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'chromium';
assert.ok(['chromium', 'msedge'].includes(channel));
const app = next({ dev: false, dir: process.cwd(), quiet: true }), sockets = new Set(), writes = [], errors = [], checks = [];
const directory = resolve(config.primaryShop ? '../tmp/production/primary-shop-browser-results' : '../tmp/production/catalog-price-browser-results'); mkdirSync(directory, { recursive: true });
const requests = [];
let browser, frontend, edge, tunnel, dropNext = false, dropped = 0, firstApiAt;
const pages = [];
function track(server) { server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); }); return server; }
async function listen(server) { await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); }); return server.address().port; }
function forward(req, res, port, isApi) {
  const observed = { path: new URL(req.url, origin).pathname, method: req.method };
  if (isApi) { observed.apiAt = performance.now(); firstApiAt ??= observed.apiAt; }
  if (config.primaryShop) {
    requests.push(observed);
    if (req.method === 'POST' && /^\/api\/v1\/ordering\/(drafts(?:\/operations\/query)?|quote\/query)$/.test(observed.path)) {
      const body = []; req.on('data', chunk => body.push(chunk)); req.on('end', () => {
        try { observed.body = JSON.parse(Buffer.concat(body).toString('utf8')); } catch { observed.body = null; }
      });
    }
  }
  const headers = { ...req.headers, host: '127.0.0.1:' + port };
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded', 'connection'].includes(name)) delete headers[name];
  if (isApi) { headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https'; }
  const write = req.method === 'POST' && (req.url === '/api/v1/ordering/drafts' || /^\/api\/v1\/catalog\/prices\/skus\/[0-9]+\/proposals$/.test(req.url) || /^\/api\/v1\/catalog\/prices\/proposals\/[0-9]+\/(approve|reject|withdraw)$/.test(req.url));
  if (write) {
    const entry = { path: req.url, key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] }; writes.push(entry);
    if (config.primaryShop) Object.defineProperty(entry, 'body', { get: () => observed.body });
  }
  const drop = write && dropNext; if (drop) dropNext = false;
  const upstream = http.request({ hostname: '127.0.0.1', port, path: req.url, method: req.method, headers }, response => {
    if (drop && response.statusCode === 200) {
      response.resume(); response.once('end', () => { dropped++; res.writeHead(200, { 'content-type': 'application/json', 'content-length': '64', 'cache-control': 'no-store' });
        res.flushHeaders(); res.write('{'); setTimeout(() => res.destroy(), 25); }); return;
    }
    res.writeHead(response.statusCode ?? 502, response.headers); response.pipe(res);
  });
  upstream.on('error', () => { if (!res.headersSent) res.writeHead(502); res.end(); }); req.on('aborted', () => upstream.destroy()); req.pipe(upstream);
}
const path = config.primaryShop ? '/shop' : '/catalog/prices';
async function check(name, action) { await action(); checks.push(name); console.log('PASS ' + name); }
const cookie = who => ({ name: '__Host-ut_session', value: config.tokens[who], domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' });
async function user(who) {
  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1280, height: 960 }, locale: 'id-ID', timezoneId: 'Asia/Jakarta' });
  await context.addCookies([cookie(who)]); const page = await context.newPage(); page.setDefaultTimeout(15000); pages.push({ page, who });
  page.on('response', async response => {
    if (response.url().includes('/api/v1/')) {
      const value = await response.json().catch(() => ({}));
      console.log('FIXTURE API ' + JSON.stringify({ who, path: new URL(response.url()).pathname, status: response.status(), code: value?.code,
        authenticated: value?.authenticated, actions: value?.allowedActions }));
    }
  });
  page.on('pageerror', error => errors.push(error.message)); page.on('dialog', dialog => dialog.accept());
  await page.goto(origin.origin + path); return { context, page };
}
async function choose(page) {
  await page.getByRole('button', { name: /Pensil browser BROWSER-12/ }).click();
  await page.getByRole('heading', { name: /BROWSER-12 — Dus/ }).waitFor();
  await page.getByRole('button', { name: 'Muat ulang harga dan riwayat', exact: true }).waitFor();
}
async function proposal(page, amount) {
  await page.getByLabel('Harga per kemasan', { exact: true }).fill(amount);
  await page.getByLabel('Alasan pengajuan', { exact: true }).fill('Harga kemasan terisolasi ' + amount);
  await page.getByRole('button', { name: 'Tinjau pengajuan harga', exact: true }).click();
  const review = page.getByRole('region', { name: 'Konfirmasi harga', exact: true }); await review.waitFor();
  assert.equal(await review.evaluate(element => element === document.activeElement), true);
  assert.equal(await page.getByLabel('Harga per kemasan', { exact: true }).isDisabled(), true);
}
async function submit(page, lose = false) {
  const before = writes.length; dropNext = lose;
  await page.getByRole('button', { name: 'Konfirmasi dan kirim', exact: true }).click();
  if (lose) {
    await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
    const entries = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
    assert.equal(entries.length, 1); assert.deepEqual(Object.keys(entries[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.equal(entries[0].key, writes.at(-1).key);
    await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
    await page.getByText('Hasil permintaan dipastikan. Pilih kembali kemasan untuk melihat riwayat terbaru.', { exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: /Tutup permintaan/ }).count(), 0);
    await choose(page);
  } else await page.getByRole('status').filter({ hasText: 'Permintaan tersimpan' }).waitFor();
  assert.equal(writes.length, before + 1, 'The browser must not resubmit the business command during recovery');
}
async function decision(page, displayed, action) {
  await page.getByRole('button', { name: new RegExp(displayed + ' Menunggu tinjauan') }).click();
  await page.getByLabel('Acuan pemeriksaan', { exact: true }).fill('Tinjauan independen fixture');
  await page.getByLabel('Alasan keputusan', { exact: true }).fill('Kemasan dan harga diperiksa');
  await page.getByRole('button', { name: { approve: 'Tinjau persetujuan', reject: 'Tinjau penolakan', withdraw: 'Tinjau penarikan' }[action], exact: true }).click();
  await page.getByRole('region', { name: 'Konfirmasi harga', exact: true }).waitFor();
}
async function openDraft(page) {
  await page.goto(origin.origin + '/ordering/drafts');
  await page.getByLabel('Cabang pemesan').selectOption('10:11:');
  await page.getByRole('region', { name: 'Daftar draf' }).getByRole('button', { name: /(?:Draf [0-9]+|PO-FIXTURE)/ }).click();
  await page.getByRole('region', { name: 'Isi draf' }).waitFor();
}
async function draftRecovery(page, beforeWrites) {
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  const entries = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  assert.equal(entries.length, 1); assert.equal(entries[0].operation, 'ordering.draft.save');
  assert.deepEqual(Object.keys(entries[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.equal(entries[0].key, writes.at(-1).key);
  await page.goto(origin.origin + '/ordering/drafts');
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  await page.getByRole('region', { name: 'Isi draf' }).waitFor();
  assert.equal(writes.length, beforeWrites + 1, 'Draft recovery must only look up the original receipt');
  assert.equal(await page.getByRole('button', { name: 'Selesaikan dan tutup', exact: true }).count(), 0);
}
async function verifyDrafts(buyer) {
  // All fixture users share one loopback IP. Respect the real 240/minute limiter rather than disabling it.
  const pacing = Math.max(0, 61000 - (performance.now() - firstApiAt));
  if (pacing > 0) { console.log('FIXTURE pacing before the next workflow to respect the real API rate window'); await new Promise(resolve => setTimeout(resolve, pacing)); }
  await buyer.page.getByRole('button', { name: 'Rincian BROWSER-12 Dus dua belas', exact: true }).click();
  const detail = buyer.page.getByRole('dialog'); await detail.getByLabel('Cabang pemesan').selectOption('10:11:');
  await detail.getByLabel('Jumlah kemasan').fill('3'); await detail.getByRole('button', { name: 'Periksa subtotal', exact: true }).click();
  let before = writes.length; dropNext = true;
  await detail.getByRole('button', { name: 'Simpan sebagai draf', exact: true }).click(); await draftRecovery(buyer.page, before);
  assert.equal(await buyer.page.getByLabel('Jumlah kemasan 1', { exact: true }).inputValue(), '3');
  await buyer.page.getByLabel('Jumlah kemasan 1', { exact: true }).fill('4');
  await buyer.page.getByLabel('Referensi pembelian (opsional)', { exact: true }).fill('PO-FIXTURE');
  await buyer.page.getByLabel('Isi rencana pengiriman', { exact: true }).check();
  await buyer.page.getByLabel('Nama penerima', { exact: true }).fill('Penerima lokal');
  await buyer.page.getByLabel('Telepon penerima', { exact: true }).fill('+62 812-3456-7890');
  await buyer.page.getByLabel('Alamat pengiriman', { exact: true }).fill('Jalan Fixture 1\nLantai 2');
  await buyer.page.getByLabel('Kota', { exact: true }).fill('Jakarta');
  await buyer.page.getByLabel('Kode pos (opsional)', { exact: true }).fill('12810');
  await buyer.page.getByRole('button', { name: 'Tinjau perubahan draf', exact: true }).click();
  const review = buyer.page.getByRole('region', { name: 'Konfirmasi draf' }); await review.waitFor();
  assert.match(await review.innerText(), /4 Dus dua belas · 48 piece/);
  assert.equal(await review.evaluate(element => element === document.activeElement), true);
  assert.equal(await buyer.page.getByLabel('Jumlah kemasan 1', { exact: true }).isDisabled(), true);
  assert.equal(await buyer.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await buyer.page.screenshot({ path: resolve(directory, 'buyer-draft-mobile.png'), fullPage: true });
  before = writes.length; dropNext = true; await buyer.page.getByRole('button', { name: 'Konfirmasi simpan draf', exact: true }).click();
  await draftRecovery(buyer.page, before);
  assert.equal(await buyer.page.getByLabel('Jumlah kemasan 1', { exact: true }).inputValue(), '4');
  assert.equal(await buyer.page.getByLabel('Alamat pengiriman', { exact: true }).inputValue(), 'Jalan Fixture 1\nLantai 2');
  const stale = await user('buyer'); await openDraft(stale.page);
  await stale.page.getByLabel('Jumlah kemasan 1', { exact: true }).fill('5');
  await stale.page.getByRole('button', { name: 'Tinjau perubahan draf', exact: true }).click();
  await stale.page.getByRole('region', { name: 'Konfirmasi draf' }).waitFor();
  // Reselect the real package explicitly. The browser must not silently replace its saved price/version.
  await buyer.page.getByLabel('Cari kemasan untuk draf', { exact: true }).fill('BROWSER-12');
  await buyer.page.getByRole('button', { name: 'Cari kemasan', exact: true }).click();
  await buyer.page.getByRole('button', { name: /Pensil browser BROWSER-12 · Dus dua belas/ }).click();
  const choice = buyer.page.getByRole('region', { name: 'Periksa pilihan kemasan' }); await choice.waitFor();
  await choice.getByLabel('Jumlah kemasan pilihan').fill('6');
  await choice.getByRole('button', { name: 'Ganti pilihan kode ini', exact: true }).click();
  await buyer.page.getByRole('button', { name: 'Tinjau perubahan draf', exact: true }).click();
  await buyer.page.getByRole('button', { name: 'Konfirmasi simpan draf', exact: true }).click();
  await buyer.page.getByRole('region', { name: 'Isi draf' }).getByText('Versi 3.', { exact: false }).waitFor();
  await stale.page.getByRole('button', { name: 'Konfirmasi simpan draf', exact: true }).click();
  await stale.page.getByRole('alert').filter({ hasText: 'Draf atau akses telah berubah' }).waitFor();
  await openDraft(stale.page); assert.equal(await stale.page.getByLabel('Jumlah kemasan 1', { exact: true }).inputValue(), '6');
  await stale.context.close(); await buyer.page.goto(origin.origin + '/catalog/browse');
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
  if (config.primaryShop) {
    await verifyPrimaryShopBrowser({ config, user, origin, directory, writes, requests, firstApiAt: () => firstApiAt,
      loseNext: () => { dropNext = true; }, dropped: () => dropped });
    assert.deepEqual(errors, []);
  } else {
  const maker = await user('maker'), checker = await user('checker');
  await check('price-only maker selects explicit packaging and recovers a lost committed proposal', async () => {
    await choose(maker.page); assert.match(await maker.page.locator('main').innerText(), /12 piece/); assert.match(await maker.page.locator('main').innerText(), /Belum ada harga disetujui/);
    await proposal(maker.page, '12000.25'); await submit(maker.page, true);
  });
  await check('independent checker approves through the API and recovers the exact decision after reload', async () => {
    await choose(checker.page); await decision(checker.page, 'IDR 12.000,25', 'approve'); await submit(checker.page, true);
    assert.match(await checker.page.locator('main').innerText(), /Harga berlaku per kemasan\nIDR 12.000,25/);
  });
  await check('explicit zero creates another immutable reviewed version', async () => {
    await maker.page.getByRole('button', { name: 'Muat ulang harga dan riwayat', exact: true }).click();
    await proposal(maker.page, '0.00'); await submit(maker.page);
    await checker.page.getByRole('button', { name: 'Muat ulang harga dan riwayat', exact: true }).click();
    await decision(checker.page, 'IDR 0,00', 'approve'); await submit(checker.page);
    await checker.page.getByText(/Versi 2 ·/).waitFor(); await checker.page.getByText(/Versi 1 ·/).waitFor();
  });
  await check('withdrawal and rejection recover without creating price versions', async () => {
    await maker.page.getByRole('button', { name: 'Muat ulang harga dan riwayat', exact: true }).click();
    await proposal(maker.page, '13000.00'); await submit(maker.page); await decision(maker.page, 'IDR 13.000,00', 'withdraw'); await submit(maker.page, true);
    await proposal(maker.page, '14000.00'); await submit(maker.page);
    await checker.page.getByRole('button', { name: 'Muat ulang harga dan riwayat', exact: true }).click();
    await decision(checker.page, 'IDR 14.000,00', 'reject'); await submit(checker.page, true);
  });
  await check('stale package review conflicts after a real authoring change', async () => {
    await proposal(maker.page, '15000.00'); await submit(maker.page);
    await checker.page.getByRole('button', { name: 'Muat ulang harga dan riwayat', exact: true }).click();
    await decision(checker.page, 'IDR 15.000,00', 'approve');
    const author = await user('author');
    const status = await author.page.evaluate(async ({ skuId, authorId }) => {
      const csrf = await (await fetch('/api/v1/identity/csrf')).json();
      const response = await fetch('/api/v1/catalog/administration/skus/' + skuId + '/definition', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf.requestToken, 'X-Expected-Actor-Id': authorId, 'Idempotency-Key': 'browser-relabel', 'If-Match': '"2"' },
        body: JSON.stringify({ definition: { packageLabel: 'Dus dua belas', packageKind: 'sealed', baseUnitCode: 'piece', baseUnitsPerPackage: 12, attributes: [], imageAssetIds: [], minimumOrderPackages: 1, orderIncrementPackages: 1 }, reason: 'Koreksi label fixture' }) }); return response.status;
    }, { skuId: config.skuId, authorId: config.authorId }); assert.equal(status, 200); await author.context.close();
    await checker.page.getByRole('button', { name: 'Konfirmasi dan kirim', exact: true }).click();
    await checker.page.getByRole('alert').filter({ hasText: 'Harga atau kemasan telah berubah' }).waitFor();
  });
  await check('mobile review remains readable and changed account cannot submit a frozen maker command', async () => {
    await maker.page.getByRole('button', { name: 'Muat ulang harga dan riwayat', exact: true }).click();
    await maker.page.setViewportSize({ width: 360, height: 800 }); await proposal(maker.page, '16000.00');
    assert.equal(await maker.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await maker.page.screenshot({ path: resolve(directory, 'price-review-mobile.png'), fullPage: true });
    await maker.context.addCookies([cookie('checker')]); await maker.page.getByRole('button', { name: 'Konfirmasi dan kirim', exact: true }).click();
    await maker.page.getByRole('heading', { name: 'Ruang harga belum dapat dibuka', exact: true }).waitFor();
    assert.equal(await maker.page.getByLabel('Harga per kemasan', { exact: true }).count(), 0);
  });
  await check('buyer reads entitled current price and fresh explicit package details while staff history and unpublished details remain private', async () => {
    const buyer = await user('buyer'); await buyer.page.getByRole('heading', { name: 'Ruang harga belum dapat dibuka', exact: true }).waitFor();
    assert.equal(await buyer.page.getByRole('button', { name: /Pensil browser/ }).count(), 0);
    await buyer.page.goto(origin.origin + '/catalog/browse');
    const open = buyer.page.getByRole('button', { name: 'Rincian BROWSER-12 Dus dua belas', exact: true }); await open.waitFor();
    assert.equal(await buyer.page.getByRole('dialog').count(), 0, 'No package is selected implicitly');
    await buyer.page.setViewportSize({ width: 360, height: 800 });
    assert.equal(await buyer.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await buyer.page.screenshot({ path: resolve(directory, 'buyer-catalog-mobile.png'), fullPage: true });
    await open.click(); const detail = buyer.page.getByRole('dialog'); await detail.waitFor();
    assert.match(await detail.innerText(), /12 piece per kemasan/); assert.match(await detail.innerText(), /IDR 0,00/);
    assert.doesNotMatch(await detail.innerText(), /Tinjauan independen fixture|Harga kemasan terisolasi/);
    const branch = detail.getByLabel('Cabang pemesan'); await branch.waitFor();
    assert.equal(await branch.inputValue(), '', 'The buyer must select a branch explicitly');
    await branch.selectOption('10:11:'); await detail.getByLabel('Jumlah kemasan').fill('2');
    await detail.getByRole('button', { name: 'Periksa subtotal', exact: true }).click();
    const subtotal = detail.getByRole('status').filter({ hasText: 'Subtotal barang' }); await subtotal.waitFor();
    assert.match(await subtotal.innerText(), /IDR 0,00/); assert.match(await subtotal.innerText(), /2 Dus dua belas · 24 piece/);
    assert.match(await subtotal.innerText(), /Pesanan belum dikirim/);
    assert.equal(await detail.evaluate(element => element.scrollWidth <= element.clientWidth), true);
    await buyer.page.screenshot({ path: resolve(directory, 'buyer-quote-mobile.png'), fullPage: true });
    await detail.getByLabel('Jumlah kemasan').fill('3'); assert.equal(await subtotal.count(), 0, 'An edited quantity invalidates the displayed quote');
    await buyer.page.keyboard.press('Escape'); await detail.waitFor({ state: 'hidden' });
    assert.equal(await open.evaluate(element => element === document.activeElement), true);
    await check('buyer saves and edits a private Mongo draft, recovers two lost responses and rejects a stale competing edit', () => verifyDrafts(buyer));
    const author = await user('author');
    const status = await author.page.evaluate(async ({ skuId, authorId }) => {
      const csrf = await (await fetch('/api/v1/identity/csrf')).json();
      return (await fetch('/api/v1/catalog/administration/skus/' + skuId + '/publications', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf.requestToken, 'X-Expected-Actor-Id': authorId, 'Idempotency-Key': 'browser-unpublish', 'If-Match': '"3"' },
        body: JSON.stringify({ assortmentIds: [], expectedAssortments: [{ id: '501', version: '2' }], reason: 'End synthetic catalog publication' }) })).status;
    }, { skuId: config.skuId, authorId: config.authorId }); assert.equal(status, 200); await author.context.close();
    await open.click(); await buyer.page.getByRole('status').filter({ hasText: 'Kemasan ini tidak lagi tersedia' }).waitFor();
    assert.equal(await buyer.page.getByRole('dialog').count(), 0); assert.equal(await open.count(), 0);
    await openDraft(buyer.page);
    await buyer.page.getByRole('alert').filter({ hasText: 'Sebagian kemasan atau harga tidak lagi tersedia' }).waitFor();
    const invalidQuote = buyer.page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1/ordering/quote/query');
    await buyer.page.getByRole('button', { name: 'Tinjau perubahan draf', exact: true }).click();
    assert.equal((await invalidQuote).status(), 403);
    await buyer.page.locator('main').getByRole('alert').filter({ hasText: 'Akses untuk tindakan ini tidak tersedia' }).waitFor();
    assert.equal(await buyer.page.getByRole('region', { name: 'Konfirmasi draf' }).count(), 0);
    await buyer.context.close();
  });
  assert.deepEqual(errors, []); assert.equal(dropped, 6);
  console.log('REAL_PRICE_BROWSER_PASS ' + JSON.stringify({ checks, droppedCommittedResponses: dropped, attempts: writes.length }));
  }
} catch (error) {
  for (const { page, who } of pages) if (!page.isClosed()) {
    console.error('FIXTURE PAGE ' + who + ': ' + (await page.locator('main').innerText().catch(() => 'unavailable')).slice(0, 5000));
    await page.screenshot({ path: resolve(directory, 'failure-' + who + '.png'), fullPage: true }).catch(() => {});
  }
  console.error(error.stack ?? String(error)); process.exitCode = 1;
}
finally {
  let timer;
  try {
    await Promise.race([(async () => {
      if (browser) await browser.close(); for (const socket of sockets) socket.destroy();
      for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve)); await app.close();
    })(), new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Fixture cleanup exceeded 10 seconds')), 10000); })]);
  } catch (failure) { console.error(failure); process.exitCode = 1; }
  finally { clearTimeout(timer); }
}
// Isolated fixture process only; preserve any assertion or cleanup failure as a failing exit.
process.exit(process.exitCode ?? 0);
