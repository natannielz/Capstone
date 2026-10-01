// Actual production Next + Kestrel + isolated Mongo. All accounts and approvals are synthetic.
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import { createRequire } from 'node:module';
import { mkdirSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import next from 'next';

const chunks = []; for await (const chunk of process.stdin) chunks.push(chunk);
const config = JSON.parse(Buffer.concat(chunks).toString('utf8')), api = new URL(config.apiAddress), origin = new URL(config.origin);
assert.equal(process.versions.node.split('.')[0], '24'); assert.equal(api.protocol, 'http:'); assert.equal(api.hostname, '127.0.0.1'); assert.ok(Number(api.port) > 0); assert.equal(origin.href, 'https://portal.unit-toko.co.id/');
const load = createRequire(config.playwrightPackage), metadata = load(config.playwrightPackage); assert.equal(metadata.name, 'playwright'); assert.equal(metadata.version, '1.62.1');
const { chromium } = load('playwright'), channel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'chromium'; assert.ok(['chromium', 'msedge'].includes(channel));
const app = next({ dev: false, dir: process.cwd(), quiet: true }), sockets = new Set(), writes = [], caseWrites = [], decisionWrites = [], returnWrites = [], errors = [], pages = [];
const directory = resolve('../tmp/production/invoice-browser-results'); mkdirSync(directory, { recursive: true });
let browser, frontend, edge, tunnel, dropNext = false, dropped = 0;
function track(server) { server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); }); return server; }
async function listen(server) { await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); }); return server.address().port; }
function forward(req, res, port, isApi) {
  const headers = { ...req.headers, host: '127.0.0.1:' + port };
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded', 'connection'].includes(name)) delete headers[name];
  if (isApi) { headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https'; }
  const write = req.method === 'POST' && req.url === '/api/v1/billing/invoices'; if (write) writes.push({ key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
  const caseWrite = req.method === 'POST' && req.url === '/api/v1/fulfillment/cases'; if (caseWrite) caseWrites.push({ key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
  const decisionWrite = req.method === 'POST' && /^\/api\/v1\/fulfillment\/cases\/[1-9][0-9]*\/decision$/.test(req.url); if (decisionWrite) decisionWrites.push({ key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
  const returnWrite = req.method === 'POST' && /^\/api\/v1\/fulfillment\/cases\/[1-9][0-9]*\/returns$/.test(req.url); if (returnWrite) returnWrites.push({ key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
  const drop = (write || caseWrite || decisionWrite || returnWrite) && dropNext; if (drop) dropNext = false;
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
  const finance = await user(config.financeToken), page = finance.page;
  await page.goto(origin.origin + '/identity/account'); await page.getByRole('link', { name: /^Tagihan\b/ }).click();
  const candidates = page.getByRole('region', { name: 'Penerimaan untuk invoice', exact: true });
  await candidates.getByRole('checkbox').check(); await candidates.getByLabel('Alasan penerbitan', { exact: true }).fill('Bukti penerimaan cabang diperiksa');
  await candidates.getByRole('button', { name: 'Tinjau invoice', exact: true }).click();
  const review = page.getByRole('region', { name: 'Tinjauan invoice', exact: true }); await review.waitFor();
  const heading = review.getByRole('heading', { name: 'Tinjau penerbitan invoice', exact: true }); assert.equal(await heading.evaluate(element => element === document.activeElement), true);
  await review.getByText(/Diterima: 6/).waitFor(); await review.getByRole('button', { name: 'Kembali ke pilihan penerimaan', exact: true }).click(); assert.equal(writes.length, 0);
  await candidates.getByRole('button', { name: 'Tinjau invoice', exact: true }).click();
  await review.waitFor();
  await finance.context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await review.getByRole('button', { name: 'Terbitkan invoice', exact: true }).isDisabled(), true); assert.equal(writes.length, 0);
  await finance.context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'invoice-review-mobile.png'), fullPage: true });
  dropNext = true; await review.getByRole('button', { name: 'Terbitkan invoice', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  const references = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  assert.equal(references.length, 1); assert.equal(references[0].operation, 'billing.invoice.issue'); assert.deepEqual(Object.keys(references[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByText(/^Invoice tersimpan/).waitFor();
  assert.equal(writes.length, 1); assert.equal(writes[0].actor, config.financeId); assert.equal(dropped, 1); assert.equal(await page.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
  assert.equal(await candidates.getByRole('checkbox').isDisabled(), true);
  const buyer = await user(config.buyerToken); await buyer.page.goto(origin.origin + '/billing/invoices');
  assert.equal(await buyer.page.getByRole('region', { name: 'Penerimaan untuk invoice', exact: true }).count(), 0);
  await buyer.page.getByRole('button', { name: 'Buka invoice 100/103/2026/1', exact: true }).click();
  const download = buyer.page.waitForEvent('download'); await buyer.page.getByRole('button', { name: 'Unduh invoice', exact: true }).click();
  const file = await download, bytes = readFileSync(await file.path()); assert.match(file.suggestedFilename(), /^invoice-[1-9][0-9]*\.html$/);
  assert.ok(bytes.includes(Buffer.from('Cabang fixture'))); assert.ok(bytes.includes(Buffer.from('Alamat badan fixture'))); assert.ok(!bytes.includes(Buffer.from('recordHash')));
  assert.equal(await buyer.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await buyer.page.screenshot({ path: resolve(directory, 'invoice-buyer-mobile.png'), fullPage: true });
  if (config.afterSales) {
    await buyer.page.getByRole('link', { name: 'Laporkan masalah barang', exact: true }).click();
    await buyer.page.getByRole('button', { name: 'Periksa barang pada invoice', exact: true }).click();
    const form = buyer.page.getByRole('region', { name: 'Pengajuan keluhan', exact: true }); await form.waitFor();
    await form.getByText(/Diterima: 6/).waitFor(); await form.getByLabel(/^Jumlah bermasalah/).fill('1');
    await form.getByLabel('Jelaskan masalah', { exact: true }).fill('Satu barang rusak saat dibuka');
    await form.getByRole('button', { name: 'Tinjau keluhan', exact: true }).click();
    const caseReview = buyer.page.getByRole('region', { name: 'Tinjauan keluhan', exact: true }); await caseReview.waitFor();
    assert.equal(await caseReview.getByRole('heading', { name: 'Tinjau keluhan barang', exact: true }).evaluate(element => element === document.activeElement), true);
    await caseReview.getByRole('button', { name: 'Kembali ke isian', exact: true }).click(); assert.equal(caseWrites.length, 0);
    await form.getByRole('button', { name: 'Tinjau keluhan', exact: true }).click(); await caseReview.getByRole('checkbox').check();
    await buyer.context.setOffline(true); await buyer.page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await caseReview.getByRole('button', { name: 'Kirim keluhan', exact: true }).isDisabled(), true);
    assert.equal(caseWrites.length, 0); await buyer.context.setOffline(false); await buyer.page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
    assert.equal(await buyer.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await buyer.page.screenshot({ path: resolve(directory, 'case-review-mobile.png'), fullPage: true });
    dropNext = true; await caseReview.getByRole('button', { name: 'Kirim keluhan', exact: true }).click(); await buyer.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
    const caseReferences = await buyer.page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
    assert.equal(caseReferences.length, 1); assert.equal(caseReferences[0].operation, 'fulfillment.case.request'); assert.deepEqual(Object.keys(caseReferences[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.match(caseReferences[0].key, /^ca\.[1-9][0-9]*\./); assert.ok(!JSON.stringify(caseReferences).includes('rusak'));
    await buyer.page.reload(); await buyer.page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await buyer.page.getByText(/^Pengajuan keluhan \d+ tercatat/).waitFor();
    assert.equal(caseWrites.length, 1); assert.equal(caseWrites[0].actor, config.buyerId); assert.equal(dropped, 2); assert.equal(await buyer.page.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
    await buyer.page.getByRole('button', { name: /^Buka keluhan \d+$/ }).click(); await buyer.page.getByRole('region', { name: 'Rincian keluhan', exact: true }).getByText('Satu barang rusak saat dibuka', { exact: true }).waitFor();
    assert.equal(await buyer.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await buyer.page.screenshot({ path: resolve(directory, 'case-buyer-mobile.png'), fullPage: true });
    if (config.caseDecision) {
      assert.equal(await buyer.page.getByRole('button', { name: 'Periksa kewenangan keputusan', exact: true }).count(), 0);
      await page.goto(origin.origin + '/fulfillment/cases'); await page.getByRole('button', { name: /^Buka keluhan \d+$/ }).click();
      const decisionPanel = page.getByRole('region', { name: 'Pemeriksaan keluhan', exact: true });
      await decisionPanel.getByRole('button', { name: 'Periksa kewenangan keputusan', exact: true }).click();
      await decisionPanel.getByLabel(/^Jumlah retur/).fill('1'); await decisionPanel.getByLabel('Penjelasan keputusan untuk pelanggan', { exact: true }).fill('Kembalikan satu barang untuk pemeriksaan kondisi');
      await decisionPanel.getByLabel('Catatan pemeriksa internal', { exact: true }).fill('Pertimbangan pemeriksa privat');
      await decisionPanel.getByRole('button', { name: 'Tinjau keputusan keluhan', exact: true }).click();
      const decisionReview = decisionPanel.getByRole('region', { name: 'Konfirmasi keputusan keluhan', exact: true }); await decisionReview.waitFor();
      assert.equal(await decisionReview.getByRole('heading', { name: 'Izinkan pengembalian barang', exact: true }).evaluate(element => element === document.activeElement), true);
      await decisionReview.getByRole('button', { name: 'Kembali ke pemeriksaan', exact: true }).click(); assert.equal(decisionWrites.length, 0);
      await decisionPanel.getByRole('button', { name: 'Tinjau keputusan keluhan', exact: true }).click(); await decisionReview.getByRole('checkbox').check();
      await finance.context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await decisionReview.getByRole('button', { name: 'Simpan keputusan keluhan', exact: true }).isDisabled(), true); assert.equal(decisionWrites.length, 0);
      await finance.context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'case-decision-review-mobile.png'), fullPage: true });
      dropNext = true; await decisionReview.getByRole('button', { name: 'Simpan keputusan keluhan', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
      const pending = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
      assert.equal(pending.length, 1); assert.equal(pending[0].operation, 'fulfillment.case.decide'); assert.match(pending[0].key, /^cd\.[1-9][0-9]*\./);
      assert.deepEqual(Object.keys(pending[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.ok(!JSON.stringify(pending).includes('privat'));
      await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden' });
      assert.equal(decisionWrites.length, 1); assert.equal(decisionWrites[0].actor, config.financeId); assert.equal(dropped, 3); assert.equal(await page.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
      await buyer.page.reload(); await buyer.page.getByRole('button', { name: /^Buka keluhan \d+$/ }).click();
      const explanation = buyer.page.getByRole('region', { name: 'Keputusan keluhan', exact: true }); await explanation.getByText('Kembalikan satu barang untuk pemeriksaan kondisi', { exact: true }).waitFor();
      assert.ok(!(await buyer.page.locator('main').innerText()).includes('Pertimbangan pemeriksa privat')); assert.equal(await buyer.page.getByRole('button', { name: 'Periksa kewenangan keputusan', exact: true }).count(), 0);
      assert.equal(await buyer.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await buyer.page.screenshot({ path: resolve(directory, 'case-decision-buyer-mobile.png'), fullPage: true });
      console.log('REAL_CASE_DECISION_BROWSER_PASS ' + JSON.stringify({ decisions: decisionWrites.length, independentReviewer: true, lostDecisionRecovered: true, privateNoteHidden: true, originalReportPreserved: true, offlineUnsent: true, mobile: true }));
      if (config.caseReturn) {
        assert.equal(await buyer.page.getByRole('button', { name: 'Periksa penerimaan retur', exact: true }).count(), 0);
        await page.getByRole('button', { name: /^Buka keluhan \d+$/ }).click(); const intake = page.getByRole('region', { name: 'Penerimaan retur', exact: true });
        await intake.getByRole('button', { name: 'Periksa penerimaan retur', exact: true }).click(); await intake.getByLabel(/^Jumlah tiba/).fill('1'); await intake.getByLabel(/^Kondisi terlihat/).selectOption('opened');
        await intake.getByLabel('Catatan penerimaan untuk pelanggan', { exact: true }).fill('Satu barang diterima dalam karantina'); await intake.getByLabel('Catatan petugas retur internal', { exact: true }).fill('Pertimbangan retur privat');
        await intake.getByRole('button', { name: 'Tinjau penerimaan retur', exact: true }).click(); const returnReview = intake.getByRole('region', { name: 'Konfirmasi penerimaan retur', exact: true }); await returnReview.waitFor();
        assert.equal(await returnReview.getByRole('heading', { name: 'Terima barang dalam karantina', exact: true }).evaluate(element => element === document.activeElement), true);
        await returnReview.getByRole('button', { name: 'Kembali ke penerimaan', exact: true }).click(); assert.equal(returnWrites.length, 0);
        await intake.getByRole('button', { name: 'Tinjau penerimaan retur', exact: true }).click(); await returnReview.getByRole('checkbox').check();
        await finance.context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await returnReview.getByRole('button', { name: 'Catat penerimaan retur', exact: true }).isDisabled(), true); assert.equal(returnWrites.length, 0);
        await finance.context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
        await page.screenshot({ path: resolve(directory, 'return-intake-review-mobile.png'), fullPage: true }); dropNext = true; await returnReview.getByRole('button', { name: 'Catat penerimaan retur', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
        const pendingReturn = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
        assert.equal(pendingReturn.length, 1); assert.equal(pendingReturn[0].operation, 'fulfillment.case.return_receive'); assert.match(pendingReturn[0].key, /^cr\.[1-9][0-9]*\./); assert.deepEqual(Object.keys(pendingReturn[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.ok(!JSON.stringify(pendingReturn).includes('privat'));
        await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden' });
        assert.equal(returnWrites.length, 1); assert.equal(returnWrites[0].actor, config.financeId); assert.equal(dropped, 4); assert.equal(await page.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
        await buyer.page.reload(); await buyer.page.getByRole('button', { name: /^Buka keluhan \d+$/ }).click(); const buyerReturns = buyer.page.getByRole('region', { name: 'Penerimaan retur', exact: true });
        await buyerReturns.getByRole('button', { name: 'Perbarui riwayat retur', exact: true }).click(); await buyerReturns.getByText('Satu barang diterima dalam karantina', { exact: true }).waitFor();
        await buyerReturns.getByText(/1 dari 1 .*telah diterima dalam karantina/).waitFor(); assert.ok(!(await buyer.page.locator('main').innerText()).includes('Pertimbangan retur privat')); assert.equal(await buyerReturns.getByRole('button', { name: 'Periksa penerimaan retur', exact: true }).count(), 0);
        assert.equal(await buyer.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await buyer.page.screenshot({ path: resolve(directory, 'return-intake-buyer-mobile.png'), fullPage: true });
        console.log('REAL_RETURN_QUARANTINE_BROWSER_PASS ' + JSON.stringify({ receipts: returnWrites.length, lostIntakeRecovered: true, privateNoteHidden: true, baseUnits: 1, condition: 'opened', offlineUnsent: true, mobile: true }));
      }
    }
    const caseOutsider = await user(config.courierToken); await caseOutsider.page.goto(origin.origin + '/fulfillment/cases'); await caseOutsider.page.getByRole('heading', { name: 'Periksa sesi dan akses', exact: true }).waitFor();
    assert.equal(await caseOutsider.page.getByRole('button', { name: /^Buka keluhan/ }).count(), 0);
    console.log('REAL_CASE_BROWSER_PASS ' + JSON.stringify({ requests: caseWrites.length, lostResponseRecovered: true, originalSaleLinked: true, requester: true, outsiderDenied: true, privateMetadataOnly: true, offlineUnsent: true, mobile: true }));
  }
  const outsider = await user(config.courierToken); await outsider.page.goto(origin.origin + '/billing/invoices'); await outsider.page.getByRole('heading', { name: 'Periksa sesi dan akses', exact: true }).waitFor();
  assert.equal(await outsider.page.getByRole('button', { name: /^Buka invoice/ }).count(), 0);
  assert.deepEqual(errors, []); console.log('REAL_INVOICE_BROWSER_PASS ' + JSON.stringify({ issued: writes.length, lostResponseRecovered: dropped, acceptedBaseUnits: 6, buyerDownload: true, outsiderDenied: true, offlineUnsent: true, mobile: true }));
} catch (error) {
  for (const [index, page] of pages.entries()) if (!page.isClosed()) { console.error('FIXTURE PAGE ' + index + ': ' + (await page.locator('main').innerText().catch(() => 'unavailable')).slice(0, 5000)); await page.screenshot({ path: resolve(directory, 'failure-' + index + '.png'), fullPage: true }).catch(() => {}); }
  console.error(error.stack ?? String(error)); process.exitCode = 1;
} finally {
  let timer;
  try { await Promise.race([(async () => { if (browser) await browser.close(); for (const socket of sockets) socket.destroy(); for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve)); await app.close(); })(), new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Fixture cleanup exceeded 10 seconds')), 10000); })]); }
  catch (failure) { console.error(failure); process.exitCode = 1; } finally { clearTimeout(timer); }
}
process.exit(process.exitCode ?? 0);
