// Actual production Next + Kestrel + isolated Mongo. All accounts and approvals are synthetic.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
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
const directory = resolve('../tmp/production/' + (config.replacement ? 'payment-linked-replacement-browser-results' : config.workflow ? 'payment-review-action-browser-results' : config.manualRecovery ? 'payment-recovered-ocr-browser-results' : config.manual ? 'payment-manual-review-browser-results' : 'payment-verification-browser-results')); mkdirSync(directory, { recursive: true });
let browser, frontend, edge, tunnel, dropNext = null, dropped = 0;
function track(server) { server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); }); return server; }
async function listen(server) { await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); }); return server.address().port; }
function forward(req, res, port, isApi) {
  const headers = { ...req.headers, host: '127.0.0.1:' + port };
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded', 'connection'].includes(name)) delete headers[name];
  if (isApi) { headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https'; }
  const kind = req.method !== 'POST' ? null : req.url === '/api/v1/billing/payment-reviews/' + config.declarationId + '/verification' ? 'verify' : req.url === '/api/v1/billing/payment-reviews/' + config.declarationId + '/manual-review' ? 'manual' : req.url === '/api/v1/billing/payment-reviews/' + config.declarationId + '/workflow' ? 'workflow' : req.url === '/api/v1/billing/payment-declarations/' + config.declarationId + '/replacement' ? 'replacement' : null; if (kind) writes.push({ kind, key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
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

  async function desk(token) {
    const actor = await user(token); await actor.page.goto(origin.origin + '/billing/payment-reviews');
    await actor.page.getByRole('heading', { name: 'Pemeriksaan pembayaran', exact: true }).waitFor(); return actor;
  }
  async function open(page) { await page.getByRole('button', { name: 'Periksa pengajuan ' + config.declarationId, exact: true }).click(); }
  const reviewer = await desk(config.reviewerToken), page = reviewer.page; await open(page);
  const review = page.getByRole('region', { name: 'Rincian pengajuan pembayaran', exact: true });
  const originalResponse = page.waitForResponse(response => response.url().endsWith('/original/query'));
  await review.getByRole('button', { name: 'Lihat bukti asli', exact: true }).click(); const original = await originalResponse;
  assert.equal(original.status(), 200); assert.match(original.headers()['cache-control'], /no-store/);
  assert.equal(createHash('sha256').update(await original.body()).digest('hex'), createHash('sha256').update(Buffer.from(config.imageBase64, 'base64')).digest('hex'));
  const image = review.getByRole('region', { name: 'Bukti asli untuk pemeriksaan', exact: true });
  await image.getByRole('img').evaluate(element => element.decode()); await image.getByRole('button', { name: 'Tutup bukti', exact: true }).click();
  assert.equal(await review.getByRole('button', { name: 'Lihat bukti asli', exact: true }).evaluate(element => document.activeElement === element), true);
  const panel = review.getByRole('region', { name: 'Verifikasi pembayaran', exact: true });
  await panel.getByRole('button', { name: 'Muat hasil verifikasi', exact: true }).click();
  await panel.getByText('Belum ada verifikasi pembayaran yang tersimpan.', { exact: true }).waitFor();
  if (config.workflow) {
    const workflow = review.getByRole('region', { name: 'Penanggung jawab pemeriksaan', exact: true });
    await workflow.getByRole('button', { name: 'Muat status pemeriksaan', exact: true }).click(); await workflow.getByText(/Menunggu penanggung jawab/).waitFor();
    assert.equal(await workflow.getByText(/Usia pengajuan.*menurut waktu server/).count(), 1);
    await workflow.getByLabel('Alasan tindak lanjut', { exact: true }).fill('Synthetic independent queue owner');
    await workflow.getByRole('button', { name: 'Periksa tindak lanjut', exact: true }).click();
    const confirmation = workflow.getByRole('region', { name: 'Konfirmasi tindak lanjut pemeriksaan', exact: true }); await confirmation.waitFor();
    assert.equal(await confirmation.getByRole('heading').evaluate(element => document.activeElement === element), true);
    await reviewer.context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor();
    assert.equal(await confirmation.getByRole('button', { name: 'Simpan tindak lanjut', exact: true }).isDisabled(), true); assert.equal(writes.length, 0);
    await reviewer.context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
    dropNext = 'workflow'; await confirmation.getByRole('button', { name: 'Simpan tindak lanjut', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
    const references = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
    assert.equal(references.length, 1); assert.equal(references[0].operation, 'billing.payment.review.manage'); assert.deepEqual(Object.keys(references[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.doesNotMatch(JSON.stringify(references), /Synthetic|0000123456789|4000/);
    await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.waitForFunction(() => !Object.keys(localStorage).some(key => key.startsWith('unit-toko:pending:v1:')));
    await open(page); await workflow.getByRole('button', { name: 'Muat status pemeriksaan', exact: true }).click(); await workflow.getByText(/Dalam pemeriksaan/).waitFor();
    for (const [choice, reasonText, expected] of [['release', 'Synthetic ownership release', /Menunggu penanggung jawab/], ['request_replacement', 'Synthetic request for legible replacement', /Bukti pengganti diminta/]]) {
      await workflow.getByLabel('Tindakan', { exact: true }).selectOption(choice); await workflow.getByLabel('Alasan tindak lanjut', { exact: true }).fill(reasonText);
      await workflow.getByRole('button', { name: 'Periksa tindak lanjut', exact: true }).click(); await confirmation.waitFor();
      await confirmation.getByRole('button', { name: 'Simpan tindak lanjut', exact: true }).click(); await workflow.getByText(expected).waitFor();
    }
    assert.equal(await workflow.getByRole('button', { name: 'Periksa tindak lanjut', exact: true }).count(), 0);
    await panel.getByRole('button', { name: 'Muat hasil verifikasi', exact: true }).click(); await panel.getByRole('button', { name: 'Pilih penerimaan bank', exact: true }).click();
    const blocked = page.waitForResponse(response => response.url().endsWith('/verification/preview'));
    await panel.getByRole('button', { name: 'Periksa penerimaan ' + config.receiptId, exact: true }).click(); assert.equal((await blocked).status(), 409);
    await panel.getByRole('alert').getByText(/Pengajuan ini sudah ditutup/).waitFor();
    assert.equal(await panel.getByRole('heading', { name: 'Tinjau pengaitan dana', exact: true }).count(), 0);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await workflow.screenshot({ path: resolve(directory, 'review-action-mobile.png') }); await page.setViewportSize({ width: 1440, height: 1000 }); await workflow.screenshot({ path: resolve(directory, 'review-action-desktop.png') });
    const reader = await desk(config.readerToken); await open(reader.page); const readWorkflow = reader.page.getByRole('region', { name: 'Penanggung jawab pemeriksaan', exact: true });
    await readWorkflow.getByRole('button', { name: 'Muat status pemeriksaan', exact: true }).click(); await readWorkflow.getByText(/Bukti pengganti diminta/).waitFor(); assert.equal(await readWorkflow.getByRole('button', { name: 'Periksa tindak lanjut', exact: true }).count(), 0);
    const buyer = await user(config.buyerToken); await buyer.page.goto(origin.origin + '/billing/payments');
    await buyer.page.getByRole('heading', { name: 'Bukti pembayaran', exact: true }).waitFor();
    await buyer.page.getByRole('button', { name: 'Buka pembayaran ' + config.declarationId, exact: true }).click();
    const followUp = buyer.page.getByRole('region', { name: 'Tindak lanjut pengajuan', exact: true });
    await followUp.getByRole('button', { name: 'Muat tindak lanjut petugas', exact: true }).click();
    await followUp.getByRole('status').getByText('Petugas meminta bukti pengganti', { exact: true }).waitFor();
    assert.equal(await followUp.getByText(/Synthetic request for eligible replacement|Akun 7410/).count(), 0);
    assert.equal(await buyer.page.getByRole('button', { name: 'Ajukan untuk verifikasi', exact: true }).count(), 0);
    assert.equal(await buyer.page.getByRole('button', { name: 'Unggah bukti asli', exact: true }).count(), 0);
    assert.equal(await buyer.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await followUp.screenshot({ path: resolve(directory, 'buyer-follow-up-mobile.png') });
    await buyer.page.reload(); await buyer.page.getByRole('button', { name: 'Buka pembayaran ' + config.declarationId, exact: true }).click();
    await buyer.page.getByRole('button', { name: 'Muat tindak lanjut petugas', exact: true }).click();
    await buyer.page.getByRole('region', { name: 'Tindak lanjut pengajuan', exact: true }).getByRole('status').getByText('Petugas meminta bukti pengganti', { exact: true }).waitFor();
    if (config.replacement) {
      await followUp.getByRole('button', { name: 'Siapkan bukti pengganti', exact: true }).click();
      const replacement = followUp.getByRole('region', { name: 'Konfirmasi draf pengganti', exact: true }); await replacement.waitFor();
      assert.equal(await replacement.getByRole('heading').evaluate(element => document.activeElement === element), true);
      await replacement.screenshot({ path: resolve(directory, 'replacement-confirmation-mobile.png') });
      await buyer.context.setOffline(true); await buyer.page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await replacement.getByRole('button', { name: 'Buat draf pengganti', exact: true }).isDisabled(), true);
      await buyer.context.setOffline(false); await buyer.page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
      dropNext = 'replacement'; await replacement.getByRole('button', { name: 'Buat draf pengganti', exact: true }).click(); await buyer.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
      const pending = await buyer.page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
      assert.equal(pending.length, 1); assert.equal(pending[0].operation, 'billing.payment.replacement.create'); assert.deepEqual(Object.keys(pending[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.doesNotMatch(JSON.stringify(pending), /4000|Synthetic|sha256|request/);
      await buyer.page.reload(); const recovered = buyer.page.waitForResponse(response => response.url().endsWith('/replacement/operations/query'));
      await buyer.page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); const receipt = await (await recovered).json(); assert.equal(receipt.status, 'committed'); const childId = receipt.result.declarationId;
      const form = buyer.page.getByRole('region', { name: 'Draf pembayaran', exact: true }); await form.getByRole('heading', { name: 'Draf pembayaran ' + childId, exact: true }).waitFor();
      assert.equal(await form.getByRole('checkbox').first().isDisabled(), true); assert.equal(await buyer.page.getByRole('button', { name: 'Pilih bukti untuk diajukan', exact: true }).count(), 0);
      await buyer.page.waitForFunction(() => !Object.keys(localStorage).some(key => key.startsWith('unit-toko:pending:v1:')));
      await form.getByLabel('Jumlah transfer (IDR)', { exact: true }).fill('4500.00'); await form.getByLabel('Alasan pencatatan atau perubahan', { exact: true }).fill('Synthetic linked replacement amount correction');
      await form.getByRole('button', { name: 'Simpan draf pembayaran', exact: true }).click(); await buyer.page.getByText('Draf tersimpan. Pembayaran belum diajukan atau diverifikasi.', { exact: true }).waitFor();
      const proof = buyer.page.getByRole('region', { name: 'Bukti asli pembayaran', exact: true }); await proof.getByLabel('Pilih bukti pembayaran', { exact: true }).setInputFiles({ name: 'synthetic-new-original.png', mimeType: 'image/png', buffer: Buffer.from(config.replacementImageBase64, 'base64') });
      await proof.getByRole('button', { name: 'Unggah bukti asli', exact: true }).click(); await proof.getByText('Tersimpan · menunggu pemeriksaan keamanan', { exact: true }).waitFor();
      console.log('REPLACEMENT_VALIDATE_READY');
      for (let attempt = 0; attempt < 30 && !await proof.getByRole('button', { name: 'Pilih bukti untuk diajukan', exact: true }).count(); attempt++) { await proof.getByRole('button', { name: 'Muat status bukti', exact: true }).click(); await new Promise(resolve => setTimeout(resolve, 250)); }
      await proof.getByRole('button', { name: 'Pilih bukti untuk diajukan', exact: true }).click(); const confirmation = buyer.page.getByRole('region', { name: 'Periksa pengajuan pembayaran', exact: true }); await confirmation.waitFor();
      await confirmation.getByRole('button', { name: 'Ajukan untuk verifikasi', exact: true }).click(); await form.getByText('Pengajuan tersimpan', { exact: true }).waitFor();
      assert.equal(await buyer.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await form.screenshot({ path: resolve(directory, 'replacement-submitted-mobile.png') });
      await form.getByRole('button', { name: 'Buka pengajuan asli', exact: true }).click(); await form.getByRole('heading', { name: 'Pembayaran ' + config.declarationId, exact: true }).waitFor();
      assert.equal(await form.getByLabel('Jumlah transfer (IDR)', { exact: true }).count(), 0); assert.equal(await buyer.page.getByRole('button', { name: 'Unggah bukti asli', exact: true }).count(), 0);
      assert.equal(writes.filter(w => w.kind === 'replacement').length, 1); console.log('REAL_LINKED_REPLACEMENT_BROWSER_PASS ' + JSON.stringify({ childId, immutableParent: true, originalKeyRecovery: true, newCheckedOriginal: true, noMoney: true }));
    }
    assert.equal(writes.filter(w => w.kind === 'workflow').length, 3); assert.equal(writes.filter(w => w.kind === 'verify').length, 0); assert.equal(dropped, config.replacement ? 2 : 1); assert.deepEqual(errors, []);
  } else {
  if (config.manual) {
    await review.getByRole('button', { name: 'Muat hasil OCR', exact: true }).click();
    const manual = panel.getByRole('region', { name: 'Pengecualian gangguan OCR', exact: true });
    await manual.getByRole('button', { name: 'Muat pengecualian terbaru', exact: true }).click();
    await manual.getByLabel('Nominal (IDR)', { exact: true }).fill('4000.00'); await manual.getByLabel('Mata uang', { exact: true }).fill('IDR');
    await manual.getByLabel('Rekening tujuan pada bukti', { exact: true }).fill('0000123456789');
    assert.equal(await manual.getByRole('checkbox').isChecked(), false);
    assert.equal(await manual.getByRole('checkbox').evaluate(element => element.getBoundingClientRect().width), 19);
    await manual.getByLabel('Alasan pengecualian dan hasil pemeriksaan', { exact: true }).fill('Synthetic approved outage review of the actual original');
    assert.equal(await manual.getByRole('button', { name: 'Periksa pengecualian', exact: true }).isDisabled(), true);
    await manual.getByRole('checkbox').check(); await manual.getByRole('button', { name: 'Periksa pengecualian', exact: true }).click();
    const confirmation = manual.getByRole('region', { name: 'Konfirmasi pengecualian OCR', exact: true }); await confirmation.waitFor();
    assert.equal(await confirmation.getByRole('heading').evaluate(element => document.activeElement === element), true);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await page.screenshot({ path: resolve(directory, 'manual-review-mobile.png'), fullPage: true });
    await confirmation.screenshot({ path: resolve(directory, 'manual-review-confirmation.png') });
    await reviewer.context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor();
    assert.equal(await confirmation.getByRole('button', { name: 'Simpan pemeriksaan manual', exact: true }).isDisabled(), true); assert.equal(writes.length, 0);
    await reviewer.context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
    dropNext = 'manual'; await confirmation.getByRole('button', { name: 'Simpan pemeriksaan manual', exact: true }).click();
    await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
    const refs = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
    assert.equal(refs.length, 1); assert.equal(refs[0].operation, 'billing.payment.review.manual'); assert.deepEqual(Object.keys(refs[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.doesNotMatch(JSON.stringify(refs), /Synthetic|0000123456789|4000/);
    await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
    await page.waitForFunction(() => !Object.keys(localStorage).some(key => key.startsWith('unit-toko:pending:v1:')));
    await open(page); await panel.getByRole('button', { name: 'Muat hasil verifikasi', exact: true }).click();
    await panel.getByText('Belum ada verifikasi pembayaran yang tersimpan.', { exact: true }).waitFor();
    await review.getByRole('button', { name: 'Muat hasil OCR', exact: true }).click();
    await manual.getByRole('button', { name: 'Muat pengecualian terbaru', exact: true }).click();
    await manual.getByText(/Versi pemeriksaan manual 1/).waitFor(); assert.equal(await manual.getByLabel('Nominal (IDR)', { exact: true }).inputValue(), '4000.00');
    assert.equal(await manual.getByLabel('Nomor referensi', { exact: true }).inputValue(), '');
    await page.setViewportSize({ width: 1440, height: 1000 }); await manual.screenshot({ path: resolve(directory, 'manual-review-desktop.png') });
    await page.setViewportSize({ width: 360, height: 800 });
  }
  await panel.getByRole('button', { name: 'Pilih penerimaan bank', exact: true }).click();
  if (config.manualRecovery) {
    const denied = page.waitForResponse(response => response.url().endsWith('/verification/preview'));
    await panel.getByRole('button', { name: 'Periksa penerimaan ' + config.receiptId, exact: true }).click();
    assert.equal((await denied).status(), 409); await panel.getByText(/Prosedur pengecualian manual sudah tidak berlaku/).waitFor();
    assert.equal(await panel.getByRole('heading', { name: 'Tinjau pengaitan dana', exact: true }).count(), 0);
    await panel.getByLabel('Sumber isian untuk pemeriksaan', { exact: true }).selectOption('ocr');
  }
  await panel.getByRole('button', { name: 'Periksa penerimaan ' + config.receiptId, exact: true }).click();
  const heading = panel.getByRole('heading', { name: 'Tinjau pengaitan dana', exact: true }); await heading.waitFor();
  assert.equal(await heading.evaluate(element => document.activeElement === element), true);
  const boxes = panel.locator('form').last().getByRole('checkbox'); assert.equal(await boxes.count(), 3);
  for (let i = 0; i < 3; i++) assert.equal(await boxes.nth(i).isChecked(), false);
  const fields = panel.locator('textarea[id^="verification-"]:not(#verification-reason)'); assert.ok(await fields.count() >= 1);
  for (let i = 0; i < await fields.count(); i++) await fields.nth(i).fill('Synthetic independently resolved source difference');
  await panel.getByLabel('Referensi pemeriksaan identitas pembayar', { exact: true }).fill('Synthetic independent payer evidence');
  await panel.getByLabel('Alasan verifikasi', { exact: true }).fill('Synthetic review of exact image, bank receipt and payer');
  await panel.getByRole('button', { name: 'Periksa keputusan verifikasi', exact: true }).click();
  await panel.getByRole('alert').waitFor(); assert.equal(writes.filter(w => w.kind === 'verify').length, 0);
  for (let i = 0; i < 3; i++) await boxes.nth(i).check();
  await panel.getByRole('button', { name: 'Periksa keputusan verifikasi', exact: true }).click();
  const confirm = panel.getByRole('region', { name: 'Konfirmasi verifikasi pembayaran', exact: true }); await confirm.waitFor();
  if (config.manualRecovery) {
    await panel.getByLabel('Sumber isian untuk pemeriksaan', { exact: true }).selectOption('manual');
    assert.equal(await confirm.count(), 0); assert.equal(await boxes.count(), 0); assert.equal(writes.filter(w => w.kind === 'verify').length, 0);
    await panel.getByLabel('Sumber isian untuk pemeriksaan', { exact: true }).selectOption('ocr');
    await panel.getByRole('button', { name: 'Periksa penerimaan ' + config.receiptId, exact: true }).click(); await heading.waitFor();
    for (let i = 0; i < await fields.count(); i++) await fields.nth(i).fill('Synthetic independently resolved source difference');
    await panel.getByLabel('Referensi pemeriksaan identitas pembayar', { exact: true }).fill('Synthetic independent payer evidence');
    await panel.getByLabel('Alasan verifikasi', { exact: true }).fill('Synthetic review of exact image, bank receipt and payer');
    for (let i = 0; i < 3; i++) { assert.equal(await boxes.nth(i).isChecked(), false); await boxes.nth(i).check(); }
    await panel.getByRole('button', { name: 'Periksa keputusan verifikasi', exact: true }).click(); await confirm.waitFor();
  }
  assert.equal(await confirm.getByRole('heading').evaluate(element => document.activeElement === element), true);
  await confirm.getByRole('button', { name: 'Kembali ke pemeriksaan', exact: true }).click(); assert.equal(writes.filter(w => w.kind === 'verify').length, 0);
  await panel.getByRole('button', { name: 'Periksa keputusan verifikasi', exact: true }).click(); await confirm.waitFor();
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'payment-verification-mobile.png'), fullPage: true });
  await reviewer.context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor();
  assert.equal(await confirm.getByRole('button', { name: 'Simpan verifikasi pembayaran', exact: true }).isDisabled(), true); assert.equal(writes.filter(w => w.kind === 'verify').length, 0);
  await reviewer.context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
  dropNext = 'verify'; await confirm.getByRole('button', { name: 'Simpan verifikasi pembayaran', exact: true }).click();
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  const references = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  assert.equal(references.length, 1); assert.deepEqual(Object.keys(references[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.equal(references[0].operation, 'billing.payment.verify'); assert.doesNotMatch(JSON.stringify(references), /Synthetic|0000123456789|4000/);
  await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  await page.waitForFunction(() => !Object.keys(localStorage).some(key => key.startsWith('unit-toko:pending:v1:')));
  await open(page); await panel.getByRole('button', { name: 'Muat hasil verifikasi', exact: true }).click();
  await panel.getByRole('heading', { name: 'Pembayaran terverifikasi', exact: true }).waitFor();
  assert.equal(await panel.getByRole('button', { name: 'Pilih penerimaan bank', exact: true }).count(), 0);
  await page.setViewportSize({ width: 1440, height: 950 }); await page.screenshot({ path: resolve(directory, 'payment-verification-desktop.png'), fullPage: true });
  const reader = await desk(config.readerToken); await open(reader.page);
  const readPanel = reader.page.getByRole('region', { name: 'Verifikasi pembayaran', exact: true });
  await readPanel.getByRole('button', { name: 'Muat hasil verifikasi', exact: true }).click();
  await readPanel.getByRole('heading', { name: 'Pembayaran terverifikasi', exact: true }).waitFor();
  assert.equal(await readPanel.getByRole('button', { name: /Pilih penerimaan|Simpan verifikasi/ }).count(), 0);
  const outsider = await desk(config.otherToken); await outsider.page.getByRole('button', { name: 'Muat pengajuan terbaru', exact: true }).waitFor();
  assert.equal(await outsider.page.getByRole('button', { name: 'Periksa pengajuan ' + config.declarationId, exact: true }).count(), 0);
  const denied = await outsider.page.evaluate(async ({ id, actorId }) => {
    const csrf = await (await fetch('/api/v1/identity/csrf', { credentials: 'include' })).json();
    return (await fetch('/api/v1/billing/payment-reviews/' + id + '/verification/query', { method: 'POST', credentials: 'include',
      headers: { 'content-type': 'application/json', 'X-CSRF-Token': csrf.requestToken, 'X-Expected-Actor-Id': actorId }, body: JSON.stringify({ purpose: 'payment-review' }) })).status;
  }, { id: config.declarationId, actorId: config.otherId }); assert.equal(denied, 403);
  assert.equal(writes.filter(w => w.kind === 'verify').length, 1); assert.equal(writes.filter(w => w.kind === 'manual').length, config.manual ? 1 : 0); assert.equal(dropped, config.manual ? 2 : 1); assert.deepEqual(errors, []);
  }
  console.log('REAL_PAYMENT_VERIFICATION_BROWSER_PASS ' + JSON.stringify({ exactOriginal: true, explicitReview: true, lostResponseRecovered: true, oneCashEvent: true,
    readerCannotVerify: true, outsiderDenied: true, offlineUnsent: true, mobile: true, syntheticBankAndPayerPolicy: true, syntheticOcrEngine: true, productionAccepted: false }));

} catch (error) {
  for (const [index, page] of pages.entries()) if (!page.isClosed()) { console.error('FIXTURE PAGE ' + index + ': ' + (await page.locator('main').innerText().catch(() => 'unavailable')).slice(0, 5000)); await page.screenshot({ path: resolve(directory, 'failure-' + index + '.png'), fullPage: true }).catch(() => {}); }
  console.error(error.stack ?? String(error)); process.exitCode = 1;
} finally {
  let timer;
  try { await Promise.race([(async () => { if (browser) await browser.close(); for (const socket of sockets) socket.destroy(); for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve)); await app.close(); })(), new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Fixture cleanup exceeded 10 seconds')), 10000); })]); }
  catch (failure) { console.error(failure); process.exitCode = 1; } finally { clearTimeout(timer); }
}
process.exit(process.exitCode ?? 0);
