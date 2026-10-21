// Real local Next/Kestrel/Mongo rehearsal with synthetic approved policies and sessions. No live acceptance.
import assert from 'node:assert/strict';
import { openDestination } from './open-destination.mjs';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import { createRequire } from 'node:module';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import next from 'next';
// Order numbers as the screens show them (src/lib/document-numbers.ts).
const orderNumber = id => 'PSN-' + String(id).padStart(6, '0');

const chunks = []; for await (const chunk of process.stdin) chunks.push(chunk);
const config = JSON.parse(Buffer.concat(chunks).toString('utf8')), api = new URL(config.apiAddress), origin = new URL(config.origin);
assert.equal(process.versions.node.split('.')[0], '24'); assert.equal(api.protocol, 'http:'); assert.equal(api.hostname, '127.0.0.1'); assert.ok(Number(api.port) > 0);
assert.equal(origin.href, 'https://portal.unit-toko.co.id/');
const load = createRequire(config.playwrightPackage), metadata = load(config.playwrightPackage);
assert.equal(metadata.name, 'playwright'); assert.equal(metadata.version, '1.62.1');
const { chromium } = load('playwright'), channel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'chromium'; assert.ok(['chromium', 'msedge'].includes(channel));
const app = next({ dev: false, dir: process.cwd(), quiet: true }), sockets = new Set(), writes = [], decisionWrites = [], preparationWrites = [], errors = [], pages = [];
const directory = resolve('../tmp/production/order-submission-browser-results'); mkdirSync(directory, { recursive: true });
let browser, frontend, edge, tunnel, dropNext = false, dropped = 0, dropNextDecision = false, droppedDecisions = 0, dropNextPreparation = false, droppedPreparations = 0;
function track(server) { server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); }); return server; }
async function listen(server) { await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); }); return server.address().port; }
function forward(req, res, port, isApi) {
  const headers = { ...req.headers, host: '127.0.0.1:' + port };
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded', 'connection'].includes(name)) delete headers[name];
  if (isApi) { headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https'; }
  const write = req.method === 'POST' && req.url === '/api/v1/ordering/orders';
  const decision = req.method === 'POST' && /^\/api\/v1\/ordering\/reviews\/[0-9]+\/decisions$/.test(req.url);
  const preparation = req.method === 'POST' && /^\/api\/v1\/fulfillment\/preparations\/[0-9]+$/.test(req.url);
  if (write) writes.push({ key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
  if (decision) decisionWrites.push({ key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
  if (preparation) preparationWrites.push({ key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
  const drop = write && dropNext || decision && dropNextDecision || preparation && dropNextPreparation;
  if (drop) { if (preparation) dropNextPreparation = false; else if (decision) dropNextDecision = false; else dropNext = false; }
  const upstream = http.request({ hostname: '127.0.0.1', port, path: req.url, method: req.method, headers }, response => {
    if (drop && response.statusCode === 200) {
      response.resume(); response.once('end', () => { if (preparation) droppedPreparations++; else if (decision) droppedDecisions++; else dropped++; res.writeHead(200, { 'content-type': 'application/json', 'content-length': '64', 'cache-control': 'no-store' });
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
  } });
  return { context, page };
}
async function captureCheckout(page, name) {
  const viewport = page.viewportSize(), scroll = await page.evaluate(() => ({ x: scrollX, y: scrollY }));
  const observeSkipLink = async phase => console.log('CHECKOUT_CAPTURE ' + JSON.stringify({ name, phase,
    ...(await page.locator('a[href="#checkout-main"]').evaluate(element => {
      const rect = element.getBoundingClientRect(), style = getComputedStyle(element);
      return { viewport: { width: innerWidth, height: innerHeight }, scrollY, focused: document.activeElement === element,
        position: style.position, transform: style.transform, visibility: style.visibility,
        bounds: { x: rect.x, y: rect.y, width: rect.width, height: rect.height },
        intersectsViewport: rect.bottom > 0 && rect.top < innerHeight && rect.right > 0 && rect.left < innerWidth };
    })) }));
  await observeSkipLink('before-capture');
  try {
    // A top-of-page capture avoids painting an offscreen fixed skip link into a scrolled full-page image.
    await page.evaluate(() => window.scrollTo(0, 0));
    await observeSkipLink('mobile');
    await page.screenshot({ path: resolve(directory, name + '-mobile.png'), fullPage: true });
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.evaluate(() => window.scrollTo(0, 0));
    await observeSkipLink('desktop');
    await page.screenshot({ path: resolve(directory, name + '-desktop.png'), fullPage: true });
  } finally {
    await page.setViewportSize(viewport);
    await page.evaluate(value => window.scrollTo(value.x, value.y), scroll);
  }
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
  await page.goto(origin.origin + '/ordering/checkout?draft=' + config.draftId);
  await page.getByRole('button', { name: 'Tinjau pesanan dan biaya', exact: true }).click();
  const review = page.getByRole('region', { name: 'Rincian pesanan', exact: true }); await review.waitFor();
  assert.match(await review.innerText(), /2 Dus · 24 piece/); assert.match(await review.innerText(), /Total pesanan: Rp25\.500,50/);
  assert.equal(await review.evaluate(element => element === document.activeElement), true);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await captureCheckout(page, 'checkout');
  await page.getByRole('button', { name: 'Batal pengajuan', exact: true }).click(); assert.equal(writes.length, 0);
  await page.getByRole('button', { name: 'Tinjau pesanan dan biaya', exact: true }).click(); await review.waitFor();
  dropNext = true; await page.getByRole('button', { name: /^Ajukan (pesanan|untuk persetujuan)$/ }).click();
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  const entries = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  assert.equal(entries.length, 1); assert.equal(entries[0].operation, 'ordering.order.submit'); assert.equal(entries[0].key, writes[0].key);
  assert.deepEqual(Object.keys(entries[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.equal(await page.getByRole('button', { name: 'Tinjau pesanan dan biaya', exact: true }).isDisabled(), true);
  await page.goto(origin.origin + '/ordering/checkout');
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  const confirmed = page.getByRole('region', { name: 'Pesanan tercatat', exact: true }); await confirmed.waitFor();
  await page.getByRole('heading', { name: 'Rincian pesanan tersimpan', exact: true }).waitFor();
  assert.match(await confirmed.innerText(), /Menunggu persetujuan belanja cabang/); assert.match(await confirmed.innerText(), /Stok pesanan telah dicadangkan/);
  assert.equal(writes.length, 1); assert.equal(dropped, 1);
  assert.equal(await page.getByRole('button', { name: /^Ajukan (pesanan|untuk persetujuan)$/ }).count(), 0);
  assert.equal(await page.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
  await page.getByRole('button', { name: 'Muat kembali pesanan', exact: true }).click(); await page.getByRole('heading', { name: 'Rincian pesanan tersimpan', exact: true }).waitFor();
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await captureCheckout(page, 'submitted');
  const orderId = (await confirmed.getByRole('heading').innerText()).match(/[0-9]+/)[0];
  const denied = await user(config.deniedToken); await denied.page.goto(origin.origin + '/ordering/checkout?draft=' + config.draftId);
  await denied.page.getByRole('heading', { name: /^(Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor();
  assert.equal(await denied.page.getByRole('region', { name: 'Rincian pesanan', exact: true }).count(), 0);
  const status = await denied.page.evaluate(async ({ orderId, actorId }) => {
    const csrf = await (await fetch('/api/v1/identity/csrf')).json();
    return (await fetch('/api/v1/ordering/orders/' + orderId + '/query', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf.requestToken, 'X-Expected-Actor-Id': actorId }, body: JSON.stringify({ purpose: 'order-preparation' }) })).status;
  }, { orderId, actorId: config.deniedId }); assert.equal(status, 403);
  const store = await user(config.storeToken); await store.page.goto(origin.origin + '/ordering/reviews');
  await store.page.getByRole('button', { name: new RegExp('^Tinjau pesanan ' + orderId + ' / ') }).click();
  const storeReview = store.page.getByRole('region', { name: 'Tinjauan keputusan', exact: true }); await storeReview.waitFor();
  assert.match(await storeReview.innerText(), /Menunggu persetujuan belanja cabang/);
  assert.equal(await storeReview.getByRole('button', { name: 'Terima pesanan toko', exact: true }).count(), 0);
  assert.equal((await storeReview.innerText()).includes('Total belanja:'), false);
  const approver = await user(config.approverToken); await approver.page.goto(origin.origin + '/ordering/reviews');
  await approver.page.getByRole('button', { name: 'Tinjau pesanan ' + orderNumber(orderId), exact: true }).click();
  const branchReview = approver.page.getByRole('region', { name: 'Tinjauan keputusan', exact: true }); await branchReview.waitFor();
  assert.match(await branchReview.innerText(), /Total belanja: Rp25\.500,50/);
  assert.equal(await branchReview.evaluate(element => element === document.activeElement), true);
  await branchReview.getByLabel('Catatan peninjauan').fill('Peninjauan kebutuhan dan biaya fixture');
  await branchReview.getByRole('button', { name: 'Setujui belanja', exact: true }).click();
  await branchReview.getByRole('button', { name: 'Batal keputusan', exact: true }).click(); assert.equal(decisionWrites.length, 0);
  await branchReview.getByRole('button', { name: 'Setujui belanja', exact: true }).click();
  assert.equal(await approver.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await approver.page.screenshot({ path: resolve(directory, 'spending-review-mobile.png'), fullPage: true });
  dropNextDecision = true; await branchReview.getByRole('button', { name: 'Konfirmasi persetujuan belanja', exact: true }).click();
  await approver.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  const decisionEntries = await approver.page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  assert.equal(decisionEntries.length, 1); assert.equal(decisionEntries[0].operation, 'ordering.order.spending.approve');
  assert.deepEqual(Object.keys(decisionEntries[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  await approver.page.reload(); await approver.page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  await approver.page.getByText('Persetujuan belanja ditemukan dan tercatat.', { exact: true }).waitFor();
  assert.equal(decisionWrites.length, 1); assert.equal(droppedDecisions, 1);
  await store.page.reload(); await store.page.getByRole('button', { name: new RegExp('^Tinjau pesanan ' + orderId + ' / ') }).click();
  await storeReview.getByLabel('Catatan peninjauan').fill('Kesiapan pemenuhan toko fixture diperiksa');
  await storeReview.getByRole('button', { name: 'Terima pesanan toko', exact: true }).click();
  await storeReview.getByRole('button', { name: 'Konfirmasi penerimaan toko', exact: true }).click();
  await storeReview.getByText('Bagian pesanan ini sudah diterima toko.', { exact: true }).waitFor();
  await store.page.getByRole('region', { name: 'Pesanan sesuai akses', exact: true }).getByText('Diterima toko', { exact: true }).waitFor();
  assert.equal(decisionWrites.length, 2); assert.equal(writes.length, 1);
  await store.page.screenshot({ path: resolve(directory, 'store-acceptance-mobile.png'), fullPage: true });
  await page.getByRole('button', { name: 'Muat kembali pesanan', exact: true }).click();
  await confirmed.getByText('1 dari 1 kelompok pemenuhan telah diterima toko.', { exact: true }).waitFor();
  const picker = await user(config.pickerToken); await openDestination(picker.page, origin, 'Persiapan kiriman');
  const prepareQueue = picker.page.getByRole('region', { name: 'Pesanan siap disiapkan', exact: true });
  await prepareQueue.getByRole('button', { name: new RegExp('^Siapkan pesanan ' + orderId + ' / ') }).click();
  const plan = picker.page.getByRole('region', { name: 'Rencana persiapan', exact: true }); await plan.waitFor();
  assert.equal(await plan.getByRole('checkbox').isChecked(), false);
  assert.equal(await plan.evaluate(element => element === document.activeElement), true);
  await plan.getByRole('checkbox').check(); await plan.getByLabel(/^Jumlah /).fill('1');
  await plan.getByLabel('Catatan persiapan', { exact: true }).fill('Satu dus fixture disiapkan sebagian');
  await plan.getByRole('button', { name: 'Tinjau persiapan', exact: true }).click();
  await plan.getByRole('heading', { name: 'Periksa kemasan yang dipilih', exact: true }).waitFor();
  assert.match(await plan.innerText(), /1 Dus · total 12 piece/);
  await plan.getByRole('button', { name: 'Kembali ke jumlah', exact: true }).click(); assert.equal(preparationWrites.length, 0);
  await plan.getByRole('button', { name: 'Tinjau persiapan', exact: true }).click();
  dropNextPreparation = true; await plan.getByRole('button', { name: 'Konfirmasi persiapan', exact: true }).click();
  await picker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  const preparationEntries = await picker.page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  assert.equal(preparationEntries.length, 1); assert.equal(preparationEntries[0].operation, 'fulfillment.preparation.save');
  assert.equal(preparationEntries[0].key, preparationWrites[0].key); assert.deepEqual(Object.keys(preparationEntries[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  await picker.page.reload(); await picker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  await picker.page.getByText('Persiapan ditemukan dan tercatat. Kiriman belum diberangkatkan.', { exact: true }).waitFor();
  assert.equal(await plan.getByLabel(/^Jumlah /).inputValue(), '1'); assert.equal(preparationWrites.length, 1); assert.equal(droppedPreparations, 1);
  await plan.getByLabel(/^Jumlah /).fill('2'); await plan.getByLabel('Catatan persiapan', { exact: true }).fill('Dua dus fixture siap diperiksa');
  await plan.getByRole('button', { name: 'Tinjau persiapan', exact: true }).click();
  await plan.getByRole('button', { name: 'Konfirmasi persiapan', exact: true }).click();
  await picker.page.getByText('Persiapan tersimpan. Kiriman belum diberangkatkan.', { exact: true }).waitFor();
  await plan.getByText('Persiapan versi 2 tersimpan.', { exact: true }).waitFor(); assert.equal(preparationWrites.length, 2);
  assert.equal(await picker.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await picker.page.screenshot({ path: resolve(directory, 'shipment-preparation-mobile.png'), fullPage: true });
  await denied.page.goto(origin.origin + '/fulfillment/preparations'); await denied.page.getByRole('heading', { name: /^(Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor();
  assert.equal(await denied.page.getByRole('region', { name: 'Rencana persiapan', exact: true }).count(), 0);
  const cancellationPage = await buyer.context.newPage(); pages.push(cancellationPage); cancellationPage.setDefaultTimeout(15000); cancellationPage.on('pageerror', error => errors.push(error.message));
  await cancellationPage.goto(origin.origin + '/ordering/reviews');
  await cancellationPage.getByRole('button', { name: 'Tinjau pesanan ' + orderNumber(orderId), exact: true }).click();
  const cancellation = cancellationPage.getByRole('region', { name: 'Tinjauan keputusan', exact: true });
  await cancellation.getByLabel('Catatan peninjauan').fill('Sisa kebutuhan fixture dibatalkan sebelum dikirim');
  await cancellation.getByRole('button', { name: 'Batalkan sisa pesanan', exact: true }).click();
  assert.equal(await cancellationPage.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await cancellationPage.screenshot({ path: resolve(directory, 'cancel-review-mobile.png'), fullPage: true });
  dropNextDecision = true; await cancellation.getByRole('button', { name: 'Konfirmasi pembatalan sisa', exact: true }).click();
  await cancellationPage.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  await cancellationPage.reload(); await cancellationPage.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  await cancellationPage.getByText('Pembatalan ditemukan dan tercatat. Cadangan sisa pesanan telah dilepas.', { exact: true }).waitFor();
  await cancellation.getByText('Sisa pesanan sudah dibatalkan.', { exact: true }).waitFor();
  assert.equal(decisionWrites.length, 3); assert.equal(droppedDecisions, 2);
  await page.getByRole('button', { name: 'Muat kembali pesanan', exact: true }).click();
  await confirmed.getByText('Sisa pesanan dibatalkan. Cadangan barang yang belum dikirim telah dilepas.', { exact: true }).waitFor();
  await plan.getByRole('button', { name: 'Muat ulang persiapan', exact: true }).click();
  await plan.getByText('Sisa pesanan tidak lagi dapat disiapkan. Riwayat persiapan tetap tersimpan.', { exact: true }).waitFor();
  assert.equal(await plan.getByRole('button', { name: 'Tinjau persiapan', exact: true }).count(), 0); assert.equal(preparationWrites.length, 2);
  await picker.page.getByRole('button', { name: 'Muat pesanan terbaru', exact: true }).click();
  await prepareQueue.getByText('Belum ada sisa pesanan yang dapat disiapkan dalam penugasan ini.', { exact: true }).waitFor();
  // A deliberate new order may now use the released stock. Reject it through the branch screen.
  await page.goto(origin.origin + '/ordering/checkout?draft=' + config.draftId);
  await page.getByRole('button', { name: 'Tinjau pesanan dan biaya', exact: true }).click();
  await page.getByRole('button', { name: /^Ajukan (pesanan|untuk persetujuan)$/ }).click();
  await confirmed.waitFor(); await page.getByRole('heading', { name: 'Rincian pesanan tersimpan', exact: true }).waitFor();
  const rejectedOrderId = (await confirmed.getByRole('heading').innerText()).match(/[0-9]+/)[0]; assert.notEqual(rejectedOrderId, orderId);
  await approver.page.reload(); await approver.page.getByRole('button', { name: 'Tinjau pesanan ' + orderNumber(rejectedOrderId), exact: true }).click();
  await branchReview.getByLabel('Catatan peninjauan').fill('Kebutuhan kedua fixture ditolak cabang');
  await branchReview.getByRole('button', { name: 'Tolak belanja', exact: true }).click();
  await branchReview.getByRole('button', { name: 'Konfirmasi penolakan', exact: true }).click();
  await branchReview.getByText('Belanja cabang sudah ditolak.', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Muat kembali pesanan', exact: true }).click();
  await confirmed.getByText('Belanja cabang ditolak. Cadangan pesanan telah dilepas.', { exact: true }).waitFor();
  assert.equal(decisionWrites.length, 4); assert.equal(writes.length, 2); assert.equal(droppedDecisions, 2);
  await openDestination(page, origin, 'Pesanan saya');
  await page.getByRole('heading', { name: 'Pesanan saya', exact: true }).waitFor();
  const history = page.getByRole('region', { name: 'Daftar pesanan saya', exact: true });
  await history.getByRole('button', { name: 'Lihat rincian pesanan ' + orderNumber(rejectedOrderId), exact: true }).waitFor();
  assert.deepEqual(await history.getByRole('button', { name: /^Lihat rincian pesanan / }).evaluateAll(buttons => buttons.map(button => button.getAttribute('aria-label'))), ['Lihat rincian pesanan ' + orderNumber(rejectedOrderId), 'Lihat rincian pesanan ' + orderNumber(orderId)]);
  await history.getByRole('button', { name: 'Lihat rincian pesanan ' + orderNumber(rejectedOrderId), exact: true }).click();
  const details = page.getByRole('region', { name: 'Rincian pesanan', exact: true });
  await details.getByText('Belanja cabang ditolak', { exact: true }).waitFor();
  assert.match(await details.innerText(), /12 piece per kemasan · total 24 piece/);
  assert.equal(await details.evaluate(element => element === document.activeElement), true);
  await history.getByRole('button', { name: 'Lihat rincian pesanan ' + orderNumber(orderId), exact: true }).click();
  await details.getByText('Sisa pesanan dibatalkan', { exact: true }).waitFor();
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'order-history-mobile.png'), fullPage: true });
  await page.reload(); await history.getByRole('button', { name: 'Lihat rincian pesanan ' + orderNumber(orderId), exact: true }).waitFor();
  assert.deepEqual(errors, []); console.log('REAL_ORDER_BROWSER_PASS ' + JSON.stringify({ droppedCommittedResponses: dropped, submitAttempts: writes.length,
    decisionAttempts: decisionWrites.length, recoveredApprovals: droppedDecisions, preparationAttempts: preparationWrites.length, recoveredPreparations: droppedPreparations,
    mobile: true, ownHistory: true, unauthorizedDetail: status }));
} catch (error) {
  for (const [index, page] of pages.entries()) if (!page.isClosed()) {
    console.error('FIXTURE PAGE ' + index + ': ' + (await page.locator('main').innerText().catch(() => 'unavailable')).slice(0, 5000));
    await page.screenshot({ path: resolve(directory, 'failure-' + index + '.png'), fullPage: true }).catch(() => {});
  }
  console.error(error.stack ?? String(error)); process.exitCode = 1;
} finally {
  let timer;
  try { await Promise.race([(async () => {
    if (browser) await browser.close(); for (const socket of sockets) socket.destroy();
    for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve)); await app.close();
  })(), new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Fixture cleanup exceeded 10 seconds')), 10000); })]); }
  catch (failure) { console.error(failure); process.exitCode = 1; } finally { clearTimeout(timer); }
}
process.exit(process.exitCode ?? 0);
