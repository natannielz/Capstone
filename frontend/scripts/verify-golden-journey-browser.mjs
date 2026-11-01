// Golden journey (docs/quality/website-ready/journeys.md §5): production Next build, HTTPS edge, real API and Mongo.
// Each step runs in its own browser context with the persona that owns it, and asserts the visible result and the
// record's typed number. Sessions, contacts and the courier roster are local synthetic fixtures (GoldenJourneyBrowserTests).
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import { createRequire } from 'node:module';
import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import next from 'next';

// Typed record numbers exactly as src/lib/document-numbers.ts writes them.
const prefixes = { order: 'PSN', case: 'KLH', return: 'RTR', purchaseRequest: 'PP', purchaseOrder: 'SP', branchRequest: 'PB' };
const recordNumber = (kind, id) => prefixes[kind] + '-' + String(id).padStart(6, '0');
const documentNumber = value => [value.numberEntity, value.numberChannel, value.numberYear, value.number].join('/');

const input = []; for await (const chunk of process.stdin) input.push(chunk);
const config = JSON.parse(Buffer.concat(input).toString('utf8')), api = new URL(config.apiAddress), origin = new URL(config.origin), journey = config.journey;
assert.equal(api.protocol, 'http:'); assert.equal(api.hostname, '127.0.0.1'); assert.equal(origin.href, 'https://portal.unit-toko.co.id/');
assert.match(config.proofPrefix, /^golden-[a-z0-9]+-[a-f0-9]{32}$/);
const load = createRequire(config.playwrightPackage), metadata = load(config.playwrightPackage); assert.equal(metadata.name, 'playwright');
const { chromium } = load('playwright'), channel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'chromium'; assert.ok(['chromium', 'msedge'].includes(channel));
const app = next({ dev: false, dir: process.cwd(), quiet: true }), directory = resolve('../tmp/production/golden-journey-browser-results');
const sockets = new Set(), pages = [], errors = [], writes = [], responses = [], steps = [], apiTimes = [], captures = [];
let browser, frontend, edge, tunnel;

function track(server) { server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); socket.on('error', () => socket.destroy()); }); return server; }
async function listen(server) { await new Promise((yes, no) => { server.once('error', no); server.listen(0, '127.0.0.1', yes); }); return server.address().port; }
// One loopback IP for every persona: stay inside the real 240/min API window instead of raising it.
async function paceApi(budget) {
  for (;;) {
    const now = Date.now(); while (apiTimes.length && apiTimes[0] <= now - 60000) apiTimes.shift();
    if (apiTimes.length + budget <= 240) return;
    console.log('GOLDEN_JOURNEY pacing for the API rate window');
    await new Promise(yes => setTimeout(yes, apiTimes[0] + 60000 - now + 50));
  }
}
function forward(req, res, port, isApi) {
  if (isApi) apiTimes.push(Date.now());
  const headers = { ...req.headers, host: '127.0.0.1:' + port };
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded', 'connection', 'x-fixture-control'].includes(name)) delete headers[name];
  if (isApi) { headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https'; }
  const observe = isApi && req.url.startsWith('/api/v1/') && !req.url.startsWith('/api/v1/identity/');
  const command = observe && req.method === 'POST' && !/\/query$/.test(req.url.split('?')[0]);
  let record = null;
  if (command) {
    record = { path: req.url.split('?')[0], actor: req.headers['x-expected-actor-id'], status: null, body: null, response: null }; writes.push(record);
    const chunks = []; req.on('data', chunk => chunks.push(chunk)); req.once('end', () => { try { record.body = JSON.parse(Buffer.concat(chunks).toString('utf8')); } catch { record.body = null; } });
  }
  const upstream = http.request({ hostname: '127.0.0.1', port, path: req.url, method: req.method, headers }, response => {
    if (!observe) { res.writeHead(response.statusCode ?? 502, response.headers); response.pipe(res); return; }
    const chunks = []; response.on('data', chunk => chunks.push(chunk));
    response.once('end', () => {
      const body = Buffer.concat(chunks); let value = null; try { value = body.length ? JSON.parse(body.toString('utf8')) : null; } catch { value = null; }
      responses.push({ path: req.url.split('?')[0], method: req.method, status: response.statusCode, value });
      if (record) { record.status = response.statusCode; record.response = value; }
      if (res.destroyed) return; res.writeHead(response.statusCode ?? 502, response.headers); res.end(body);
    });
    response.once('error', () => { if (!res.headersSent) res.writeHead(502); res.end(); });
  });
  upstream.on('error', () => { if (!res.headersSent) res.writeHead(502); res.end(); }); req.once('aborted', () => upstream.destroy()); req.pipe(upstream);
}
async function control(action) {
  const response = await fetch(new URL('/__fixture/golden/' + action, api), { method: 'POST', headers: { 'X-Fixture-Control': config.controlKey }, redirect: 'error' });
  assert.equal(response.status, 200, 'fixture control ' + action); return response.json();
}
async function persona(name) {
  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1280, height: 960 }, locale: 'id-ID', timezoneId: 'Asia/Jakarta' });
  const page = await context.newPage(); pages.push(page); page.setDefaultTimeout(30000); page.on('pageerror', error => errors.push(name + ': ' + error.message));
  if (name !== 'tamu') await context.addCookies([{ name: '__Host-ut_session', value: journey.tokens[name], domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  return { name, context, page, id: journey.actorIds?.[name] };
}
async function renew(person) {
  const { token } = await control('renew-' + person.name);
  await person.context.addCookies([{ name: '__Host-ut_session', value: token, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
}
async function capture(page, label) {
  await mkdir(directory, { recursive: true }); const file = config.proofPrefix + '-' + label + '.png';
  await page.screenshot({ path: resolve(directory, file), fullPage: true }); captures.push(file);
}
async function step(id, who, text, run) {
  await paceApi(60); const started = performance.now(); await run(); const elapsedMs = Math.round(performance.now() - started);
  steps.push({ id, who, text, elapsedMs }); console.log('GOLDEN_JOURNEY_STEP ' + JSON.stringify({ id, who, text, elapsedMs }));
}
// The persona reaches its page from its own home, not only by typing the address: staff from the workspace directory,
// buyers (whose home is the storefront) from the storefront account menu.
async function reachesFromWorkspace(person, label) {
  const name = new RegExp('^' + label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
  await person.page.goto(origin.origin + '/workspace');
  await person.page.waitForLoadState('networkidle');
  if (new URL(person.page.url()).pathname === '/workspace') {
    const link = person.page.getByRole('link', { name }).first(); await link.waitFor(); return link.getAttribute('href');
  }
  await person.page.locator('button[aria-controls="menu-akun"]').click();
  const link = person.page.locator('#menu-akun').getByRole('link', { name }).first(); await link.waitFor();
  const href = await link.getAttribute('href'); await person.page.keyboard.press('Escape'); return href;
}
const committed = path => writes.filter(record => record.path === path && record.status === 200);
const noPrice = text => !/Rp\s?[0-9]/.test(text);

async function addToCart(buyer, label) {
  await buyer.page.goto(origin.origin + '/shop/' + journey.publicId);
  const details = buyer.page.getByRole('region', { name: 'Rincian kemasan', exact: true }); await details.waitFor();
  const branch = details.getByLabel('Pesan untuk', { exact: true }), quantity = details.getByLabel('Jumlah kemasan', { exact: true });
  const add = details.getByRole('button', { name: 'Tambah ke keranjang', exact: true });
  const preset = await branch.inputValue(); assert.ok(preset === '' || preset === journey.branch, 'Only the last branch the buyer named may be remembered');
  if (preset === '') assert.equal(await add.isDisabled(), true, 'Nothing is added before the branch is named');
  await branch.selectOption(journey.branch); const packages = Number(await quantity.inputValue()); assert.ok(packages >= 1);
  assert.match(await details.innerText(), /Rp[0-9]/, 'A signed-in buyer sees the price'); assert.doesNotMatch(await details.innerText(), /Rp [0-9]/);
  await add.click(); await buyer.page.getByRole('status').filter({ hasText: /masuk keranjang/ }).waitFor();
  await buyer.page.getByRole('link', { name: 'Lihat keranjang', exact: true }).click(); await buyer.page.waitForURL(origin.origin + '/cart');
  await buyer.page.getByRole('region', { name: 'Barang di keranjang', exact: true }).waitFor();
  const summary = await buyer.page.getByRole('complementary', { name: 'Ringkasan belanja', exact: true }).innerText();
  assert.match(summary, /Rp[0-9]{1,3}(\.[0-9]{3})*/); await capture(buyer.page, label + '-keranjang'); return packages;
}
// A keranjang's delivery belongs to its own draft (F-030): a fresh keranjang asks for it again, an earlier one already has it.
async function giveDelivery(buyer) {
  const change = buyer.page.getByRole('button', { name: 'Ubah pengiriman', exact: true }), fill = buyer.page.getByRole('button', { name: 'Isi dari alamat cabang', exact: true });
  await change.or(fill).first().waitFor();
  if (await fill.count()) {
    await fill.click(); await buyer.page.getByRole('status').filter({ hasText: /^Alamat diisi dari alamat/ }).waitFor();
    await buyer.page.getByLabel('Nama penerima', { exact: true }).fill('Penerima cabang sintetis');
    await buyer.page.getByLabel('Telepon penerima', { exact: true }).fill('+628123456789');
    await buyer.page.getByRole('button', { name: 'Simpan pengiriman', exact: true }).click(); await change.waitFor();
  }
}
async function checkoutFromCart(buyer, label) {
  const before = committed('/api/v1/ordering/orders').length;
  await buyer.page.getByRole('link', { name: 'Lanjut ke pengajuan', exact: true }).click(); await buyer.page.waitForURL(url => url.pathname === '/ordering/checkout');
  await giveDelivery(buyer);
  await buyer.page.getByRole('button', { name: 'Tinjau pesanan dan biaya', exact: true }).click();
  await buyer.page.getByRole('heading', { name: 'Periksa sebelum mengajukan', exact: true }).waitFor(); await capture(buyer.page, label + '-checkout-review');
  await buyer.page.getByRole('button', { name: /^Ajukan (pesanan|untuk persetujuan)$/ }).click();
  await buyer.page.getByRole('heading', { name: /^Pesanan PSN-[0-9]{6} tercatat$/ }).waitFor();
  const submit = committed('/api/v1/ordering/orders'); assert.equal(submit.length, before + 1); assert.equal(submit.at(-1).actor, buyer.id);
  const orderId = submit.at(-1).response.orderId, suborderId = submit.at(-1).response.suborderIds[0], order = recordNumber('order', orderId);
  await buyer.page.getByRole('heading', { name: 'Pesanan ' + order + ' tercatat', exact: true }).waitFor();
  await buyer.page.goto(origin.origin + '/ordering/orders'); await buyer.page.getByRole('button', { name: 'Lihat rincian pesanan ' + order, exact: true }).click();
  await buyer.page.getByRole('region', { name: 'Rincian pesanan', exact: true }).getByText('Menunggu persetujuan belanja', { exact: true }).waitFor();
  await capture(buyer.page, label + '-order-waiting'); console.log('GOLDEN_JOURNEY_NUMBER ' + JSON.stringify({ order })); return { orderId, suborderId, order };
}
async function orderToReceipt() {
  const tamu = await persona('tamu'), pemohon = await persona('pemohon'), penyetuju = await persona('penyetuju-cabang'), peninjau = await persona('peninjau-toko');
  const gudang = await persona('petugas-gudang'), kirim = await persona('petugas-kirim'), kurir = await persona('kurir');
  let orderId, suborderId, order, shipment, number, packages;
  await step('A0', 'tamu', 'guest catalog shows no price and the workspace asks to sign in', async () => {
    await tamu.page.goto(origin.origin + '/katalog'); await tamu.page.getByRole('heading', { level: 1 }).first().waitFor();
    assert.ok(noPrice(await tamu.page.locator('body').innerText()), 'A guest never sees a price');
    await capture(tamu.page, 'A0-guest-katalog');
    await tamu.page.goto(origin.origin + '/workspace'); await tamu.page.waitForURL(url => url.pathname === '/identity/login');
    assert.equal(new URL(tamu.page.url()).searchParams.get('returnPath'), '/workspace');
  });
  await step('A1', 'pemohon', 'adds the package to the keranjang for an explicitly named branch', async () => { packages = await addToCart(pemohon, 'A1'); });
  await step('A2', 'pemohon', 'checks out from the keranjang with the branch address and gets PSN-', async () => {
    ({ orderId, suborderId, order } = await checkoutFromCart(pemohon, 'A2'));
  });
  await step('A3', 'penyetuju-cabang', 'approves branch spending for ' + 'PSN', async () => {
    await renew(penyetuju); assert.equal(await reachesFromWorkspace(penyetuju, 'Peninjauan pesanan'), '/ordering/reviews');
    await penyetuju.page.goto(origin.origin + '/ordering/reviews'); await penyetuju.page.getByRole('button', { name: 'Tinjau pesanan ' + order, exact: true }).click();
    const panel = penyetuju.page.getByRole('region', { name: 'Tinjauan keputusan', exact: true });
    await panel.getByLabel('Catatan peninjauan', { exact: true }).fill('Belanja cabang sesuai kebutuhan');
    await panel.getByRole('button', { name: 'Setujui belanja', exact: true }).click(); await capture(penyetuju.page, 'A3-spending-review');
    await panel.getByRole('button', { name: 'Konfirmasi persetujuan belanja', exact: true }).click();
    await panel.getByText('Peninjauan selesai atau tidak ada cadangan yang dapat dibatalkan.', { exact: true }).waitFor();
  });
  await step('A4', 'peninjau-toko', 'accepts the store part of the order', async () => {
    await renew(peninjau); assert.equal(await reachesFromWorkspace(peninjau, 'Peninjauan pesanan'), '/ordering/reviews');
    await peninjau.page.goto(origin.origin + '/ordering/reviews');
    await peninjau.page.getByRole('button', { name: 'Tinjau pesanan ' + order + ' / ' + suborderId, exact: true }).click();
    const panel = peninjau.page.getByRole('region', { name: 'Tinjauan keputusan', exact: true });
    await panel.getByLabel('Catatan peninjauan', { exact: true }).fill('Toko menerima pesanan cabang');
    await panel.getByRole('button', { name: 'Terima pesanan toko', exact: true }).click(); await capture(peninjau.page, 'A4-store-review');
    await panel.getByRole('button', { name: 'Konfirmasi penerimaan toko', exact: true }).click(); await panel.getByText('Bagian pesanan ini sudah diterima toko.', { exact: true }).waitFor();
    const decisions = writes.filter(record => /\/api\/v1\/ordering\/reviews\/[0-9]+\/decisions$/.test(record.path) && record.status === 200);
    assert.deepEqual(decisions.map(record => record.actor), [penyetuju.id, peninjau.id]);
  });
  await step('A5', 'petugas-gudang', 'prepares the accepted packages', async () => {
    await renew(gudang); assert.equal(await reachesFromWorkspace(gudang, 'Persiapan kiriman'), '/fulfillment/preparations');
    await gudang.page.goto(origin.origin + '/fulfillment/preparations');
    await gudang.page.getByRole('button', { name: 'Siapkan pesanan ' + order + ' / ' + suborderId, exact: true }).click();
    const panel = gudang.page.getByRole('region', { name: 'Rencana persiapan', exact: true });
    await panel.getByRole('checkbox').check(); await panel.getByLabel(/^Jumlah /).fill(String(packages));
    await panel.getByLabel('Catatan persiapan', { exact: true }).fill('Kemasan lengkap disiapkan');
    assert.ok(noPrice(await panel.innerText()), 'Preparation shows no price');
    await panel.getByRole('button', { name: 'Tinjau persiapan', exact: true }).click(); await panel.getByRole('heading', { name: 'Periksa kemasan yang dipilih', exact: true }).waitFor();
    await panel.getByRole('button', { name: 'Konfirmasi persiapan', exact: true }).click(); await panel.getByText('Persiapan versi 1 tersimpan.', { exact: true }).waitFor();
    await capture(gudang.page, 'A5-prepared');
  });
  await step('A6', 'petugas-kirim', 'dispatches the prepared lot with the kurir and gets the shipment number', async () => {
    await renew(kirim); assert.equal(await reachesFromWorkspace(kirim, 'Keberangkatan kiriman'), '/fulfillment/dispatches');
    await kirim.page.goto(origin.origin + '/fulfillment/dispatches');
    await kirim.page.getByRole('button', { name: 'Periksa keberangkatan pesanan ' + order, exact: true }).click();
    const panel = kirim.page.getByRole('region', { name: 'Pemeriksaan keberangkatan', exact: true });
    await panel.getByLabel('Jumlah lot ' + journey.lotId, { exact: true }).fill(String(packages));
    await panel.getByLabel('Kurir', { exact: true }).selectOption(journey.actorIds.kurir);
    await panel.getByLabel('Catatan keberangkatan', { exact: true }).fill('Lot fisik dan kurir diperiksa');
    await panel.getByRole('button', { name: 'Tinjau keberangkatan', exact: true }).click(); await panel.getByRole('heading', { name: 'Konfirmasi barang dan kurir', exact: true }).waitFor();
    await kirim.page.waitForFunction(() => [...document.querySelectorAll('button')].some(button => button.textContent?.trim() === 'Konfirmasi keberangkatan' && !button.disabled));
    await panel.getByRole('button', { name: 'Konfirmasi keberangkatan', exact: true }).click();
    await kirim.page.getByRole('heading', { name: 'Keberangkatan tercatat', exact: true }).waitFor();
    const dispatch = committed('/api/v1/fulfillment/dispatches/' + suborderId); assert.equal(dispatch.length, 1); assert.equal(dispatch[0].actor, kirim.id);
    shipment = dispatch[0].response; number = documentNumber(shipment); await capture(kirim.page, 'A6-dispatched');
    console.log('GOLDEN_JOURNEY_NUMBER ' + JSON.stringify({ shipment: number }));
  });
  await step('A7', 'petugas-gudang', 'issues the Surat Jalan for the shipment', async () => {
    await gudang.page.goto(origin.origin + '/fulfillment/shipments'); await gudang.page.getByRole('button', { name: 'Lihat kiriman ' + number, exact: true }).click();
    await gudang.page.getByRole('heading', { name: 'Kiriman ' + number, exact: true }).waitFor();
    const documentPanel = gudang.page.getByRole('region', { name: 'Surat Jalan', exact: true });
    await documentPanel.getByLabel('Catatan penerbitan', { exact: true }).fill('Surat Jalan sesuai kemasan yang diberangkatkan');
    await documentPanel.getByRole('button', { name: 'Tinjau Surat Jalan', exact: true }).click();
    await documentPanel.getByRole('button', { name: 'Terbitkan Surat Jalan', exact: true }).click();
    await documentPanel.getByText('Surat Jalan versi 1 tersimpan.', { exact: true }).waitFor(); await capture(gudang.page, 'A7-surat-jalan');
  });
  let href;
  await step('A8', 'kurir', 'opens the assigned shipment and makes the receipt QR for the buyer', async () => {
    assert.equal(await reachesFromWorkspace(kurir, 'Kiriman'), '/fulfillment/shipments');
    await kurir.page.goto(origin.origin + '/fulfillment/shipments'); await kurir.page.getByRole('button', { name: 'Lihat kiriman ' + number, exact: true }).click();
    const panel = kurir.page.getByRole('region', { name: 'Penerimaan kiriman', exact: true });
    await panel.getByLabel('Penerima terdaftar', { exact: true }).selectOption(journey.actorIds.pemohon);
    await panel.getByLabel('Catatan QR', { exact: true }).fill('Penerima memeriksa barang di cabang');
    await panel.getByRole('button', { name: 'Tinjau penerima', exact: true }).click();
    await panel.getByRole('button', { name: 'Buat QR penerimaan', exact: true }).click();
    const link = panel.getByRole('link', { name: 'Buka tautan penerimaan', exact: true }); await link.waitFor();
    href = await link.getAttribute('href'); assert.match(href, /^https:\/\/portal\.unit-toko\.co\.id\/fulfillment\/receive#[1-9][0-9]*\.[0-9a-f]{64}$/);
    await capture(kurir.page, 'A8-receipt-qr');
  });
  await step('A9', 'pemohon', 'counts what arrived and confirms receipt', async () => {
    await pemohon.page.goto(origin.origin + '/identity/account'); await pemohon.page.goto(href);
    await pemohon.page.getByRole('button', { name: 'Periksa kiriman', exact: true }).click();
    await pemohon.page.getByRole('heading', { name: 'Hitung barang yang tiba', exact: true }).waitFor();
    for (const [label, whole] of [['Diterima', String(packages)], ['Ditolak', '0'], ['Belum diterima', '0']]) {
      await pemohon.page.getByLabel(label + ' — kemasan utuh', { exact: true }).fill(whole);
      await pemohon.page.getByLabel(label + ' — satuan lepas', { exact: true }).fill('0');
    }
    await pemohon.page.getByLabel('Catatan penerimaan', { exact: true }).fill('Barang diterima lengkap');
    await pemohon.page.getByRole('button', { name: 'Tinjau jumlah', exact: true }).click();
    await pemohon.page.getByRole('heading', { name: 'Tinjau jumlah penerimaan', exact: true }).waitFor();
    await pemohon.page.getByRole('checkbox').check(); await capture(pemohon.page, 'A9-receipt-review');
    await pemohon.page.getByRole('button', { name: 'Konfirmasi penerimaan', exact: true }).click();
    await pemohon.page.getByRole('heading', { name: 'Penerimaan tersimpan', exact: true }).waitFor(); await capture(pemohon.page, 'A9-received');
    await pemohon.page.goto(origin.origin + '/ordering/orders'); await pemohon.page.getByRole('button', { name: 'Lihat rincian pesanan ' + order, exact: true }).click();
    await pemohon.page.getByRole('region', { name: 'Rincian pesanan', exact: true }).waitFor(); await capture(pemohon.page, 'A9-order-after-receipt');
  });
  return { order, shipment: number, orderId, suborderId };
}

// Stage A2 (journeys.md §5.2 A10–A13): the received order of A1 is invoiced and paid. The bank's credit for the issued invoice and
// the background evidence validation and OCR runs are fixture actions the browser asks for when the step needs them.
async function invoiceToPayment() {
  const received = await orderToReceipt();
  const keuangan = await persona('keuangan-tagihan'), pemeriksa = await persona('pemeriksa-pembayaran'), pemohon = await persona('pemohon');
  // "Rp12.345,00" as the page writes it -> "12345.00" as a field takes it.
  const decimal = text => text.replace(/^Rp\s?/, '').replace(/\./g, '').replace(',', '.');
  // The OCR panel's amount reading: present (not "Belum diketahui") and, as digits, not already the invoice total, so the checker's correction is a real change.
  const digits = text => text.replace(/\D/g, '');
  const ocrAmount = async region => {
    const text = (await region.locator('dl > div').filter({ has: region.page().locator('dt', { hasText: /^Jumlah transfer$/ }) }).locator('dd > span').first().innerText()).trim();
    assert.notEqual(text, 'Belum diketahui'); assert.match(text, /[0-9]/, 'The OCR panel reads an amount'); return text;
  };
  const openOriginal = async (person, region, bytes) => {
    const response = person.page.waitForResponse(value => value.url().endsWith('/original/query'));
    await region.getByRole('button', { name: 'Lihat bukti asli', exact: true }).click(); const original = await response;
    assert.equal(original.status(), 200); assert.match(original.headers()['cache-control'], /no-store/);
    assert.equal(createHash('sha256').update(await original.body()).digest('hex'), createHash('sha256').update(bytes).digest('hex'), 'The exact original is shown');
  };
  const releaseChecks = 'Saya sudah memeriksa penerimaan, alokasi biaya, kalender, dan akun pembukuan yang ditampilkan.', saved = 'Perubahan persetujuan invoice tersimpan. Muat riwayat untuk memeriksanya.';
  let release, invoice, number, total, world, declarationId, image, ocrReading;
  await step('A10a', 'keuangan-tagihan', 'proposes the invoice release for the received order', async () => {
    await renew(keuangan); assert.equal(await reachesFromWorkspace(keuangan, 'Persetujuan penerbitan tagihan'), '/billing/invoice-releases');
    await keuangan.page.goto(origin.origin + '/billing/invoice-releases');
    const source = keuangan.page.getByRole('region', { name: 'Penerimaan untuk persetujuan invoice', exact: true });
    const receipt = source.getByRole('checkbox', { name: /^Penerimaan [0-9]+ — / }); await receipt.first().waitFor(); assert.equal(await receipt.count(), 1); await receipt.check();
    await keuangan.page.getByRole('button', { name: 'Periksa sumber penerbitan', exact: true }).click();
    const form = keuangan.page.getByRole('region', { name: 'Usulan persetujuan invoice', exact: true }); await form.waitFor();
    const agreed = /pajak disepakati (Rp\s?[0-9]+(?:\.[0-9]{3})*,[0-9]{2}), sudah dibukukan Rp\s?[0-9]+(?:\.[0-9]{3})*,[0-9]{2}; biaya disepakati (Rp\s?[0-9]+(?:\.[0-9]{3})*,[0-9]{2})/.exec(await form.innerText());
    assert.ok(agreed, 'The release page states the tax and charges the order agreed');
    const inputs = await control('release-inputs');
    for (const [label, value] of [['Awal periode penerimaan', inputs.periodFrom], ['Akhir periode penerimaan', inputs.periodTo],
      ['Mulai berlaku (WIB)', inputs.validFromWib], ['Akhir berlaku (WIB)', inputs.validUntilWib]]) await form.getByLabel(label, { exact: true }).fill(value);
    for (const [label, value] of [['Referensi persetujuan format invoice', 'Synthetic existing HTML template acceptance'], ['Referensi persetujuan penomoran', 'Synthetic entity/channel/year numbering acceptance'],
      ['Referensi pemeriksaan kelayakan', 'Synthetic no-hold source review'], ['Pemetaan akun piutang', 'invoice_ar'], ['Pemetaan akun pendapatan', 'invoice_revenue'],
      ['Pemetaan akun pajak', 'invoice_tax'], ['Pemetaan akun biaya', 'invoice_charges'], ['Alasan perubahan persetujuan invoice', 'Synthetic invoice release for the received golden order']])
      await form.getByRole('textbox', { name: label, exact: true }).fill(value);
    await form.getByRole('textbox', { name: /^Pajak / }).fill(decimal(agreed[1])); await form.getByRole('textbox', { name: /^Biaya / }).fill(decimal(agreed[2]));
    await form.getByRole('button', { name: 'Tinjau usulan penerbitan', exact: true }).click();
    const confirmation = keuangan.page.getByRole('region', { name: 'Konfirmasi persetujuan invoice', exact: true }); await confirmation.waitFor();
    await confirmation.getByRole('checkbox', { name: releaseChecks, exact: true }).check(); await capture(keuangan.page, 'A10-release-proposal');
    await confirmation.getByRole('button', { name: 'Konfirmasi usulan penerbitan', exact: true }).click(); await keuangan.page.getByText(saved, { exact: true }).waitFor();
    const proposed = committed('/api/v1/billing/invoice-releases'); assert.equal(proposed.length, 1); assert.equal(proposed[0].actor, keuangan.id); release = proposed[0].response;
    await keuangan.page.getByRole('button', { name: 'Lihat persetujuan ' + release.releaseId, exact: true }).click();
    const stored = keuangan.page.getByRole('region', { name: 'Fakta persetujuan tersimpan', exact: true }); await stored.waitFor();
    assert.equal(await stored.getByRole('button', { name: /^Tinjau (persetujuan|penolakan) penerbitan$/ }).count(), 0, 'The proposer cannot decide the own release');
    // The page is this release's stored proposal and offers the proposer neither the reviewer's decision section nor its reason field.
    await stored.getByRole('heading', { name: 'Persetujuan ' + release.releaseId + ' — Usulan', exact: true }).waitFor();
    assert.equal(await stored.getByText('Keputusan pemeriksa', { exact: true }).count(), 0, 'No decision section for the proposer');
    assert.equal(await stored.getByRole('textbox', { name: 'Alasan keputusan persetujuan invoice', exact: true }).count(), 0, 'No decision reason field for the proposer');
  });
  await step('A10b', 'pemeriksa-pembayaran', 'approves the invoice release proposed by someone else', async () => {
    await renew(pemeriksa); await pemeriksa.page.goto(origin.origin + '/billing/invoice-releases');
    await pemeriksa.page.getByRole('button', { name: 'Lihat persetujuan ' + release.releaseId, exact: true }).click();
    const stored = pemeriksa.page.getByRole('region', { name: 'Fakta persetujuan tersimpan', exact: true }); await stored.waitFor();
    await stored.getByText('Keputusan pemeriksa', { exact: true }).waitFor(); // the same release, which the proposer was not offered a decision on
    await stored.getByRole('textbox', { name: 'Alasan keputusan persetujuan invoice', exact: true }).fill('Synthetic independent checker acceptance');
    await stored.getByRole('button', { name: 'Tinjau persetujuan penerbitan', exact: true }).click();
    const confirmation = pemeriksa.page.getByRole('region', { name: 'Konfirmasi persetujuan invoice', exact: true }); await confirmation.waitFor();
    await confirmation.getByRole('checkbox', { name: releaseChecks, exact: true }).check(); await capture(pemeriksa.page, 'A10-release-approval');
    await confirmation.getByRole('button', { name: 'Konfirmasi disetujui penerbitan', exact: true }).click(); await pemeriksa.page.getByText(saved, { exact: true }).waitFor();
    const decided = writes.filter(record => /\/api\/v1\/billing\/invoice-releases\/proposals\/[0-9]+\/decisions$/.test(record.path) && record.status === 200);
    assert.equal(decided.length, 1); assert.equal(decided[0].actor, pemeriksa.id); assert.notEqual(decided[0].actor, keuangan.id);
  });
  await step('A11', 'keuangan-tagihan', 'issues the invoice from the approved release and gets its number', async () => {
    assert.equal(await reachesFromWorkspace(keuangan, 'Tagihan'), '/billing/invoices');
    await keuangan.page.goto(origin.origin + '/billing/invoices'); await keuangan.page.getByRole('heading', { name: 'Tagihan', exact: true }).waitFor();
    const candidates = keuangan.page.getByRole('region', { name: 'Penerimaan untuk invoice', exact: true });
    const receipt = candidates.getByRole('checkbox', { name: /^Bukti penerimaan [0-9]+\b/ }); await receipt.first().waitFor(); assert.equal(await receipt.count(), 1); await receipt.check();
    await candidates.getByRole('textbox', { name: 'Alasan penerbitan', exact: true }).fill('Synthetic invoice from independently approved received goods');
    await candidates.getByRole('button', { name: 'Tinjau invoice', exact: true }).click();
    const review = keuangan.page.getByRole('region', { name: 'Tinjauan invoice', exact: true }); await review.waitFor(); await capture(keuangan.page, 'A11-invoice-review');
    await review.getByRole('button', { name: 'Terbitkan invoice', exact: true }).click(); await keuangan.page.getByRole('region', { name: 'Invoice terbit', exact: true }).waitFor();
    const issued = committed('/api/v1/billing/invoices'); assert.equal(issued.length, 1); assert.equal(issued[0].actor, keuangan.id); invoice = issued[0].response; number = documentNumber(invoice);
    await keuangan.page.getByRole('heading', { name: 'Invoice ' + number, exact: true }).waitFor();
    const download = keuangan.page.waitForEvent('download'); await keuangan.page.getByRole('button', { name: 'Unduh invoice', exact: true }).click();
    assert.equal(createHash('sha256').update(await readFile(await (await download).path())).digest('hex'), invoice.documentHash, 'The downloaded invoice is the issued document');
    await keuangan.page.reload(); await keuangan.page.getByRole('button', { name: 'Buka invoice ' + number, exact: true }).waitFor();
    await capture(keuangan.page, 'A11-invoice-issued'); console.log('GOLDEN_JOURNEY_NUMBER ' + JSON.stringify({ invoice: number }));
  });
  await step('A12a', 'pemohon', 'saves the payment draft for the invoice and uploads the original proof with a preview', async () => {
    await paceApi(120); world = await control('payment-world'); total = world.total; image = Buffer.from(world.imageBase64, 'base64');
    await renew(pemohon); assert.equal(await reachesFromWorkspace(pemohon, 'Bukti pembayaran'), '/billing/payments');
    await pemohon.page.goto(origin.origin + '/billing/payments');
    const form = pemohon.page.getByRole('region', { name: 'Draf pembayaran', exact: true });
    const choice = form.getByRole('checkbox'); await choice.first().waitFor(); assert.equal(await choice.count(), 1); await choice.check();
    assert.ok((await form.innerText()).includes(number), 'The payment form names the invoice ' + number);
    await form.getByLabel('Jumlah transfer (IDR)', { exact: true }).fill(total); await form.getByLabel('Tanggal transfer', { exact: true }).fill(world.businessDate);
    await form.getByLabel('Nama pengirim', { exact: true }).fill('Pembayar cabang sintetis'); await form.getByLabel('Referensi bank (opsional)', { exact: true }).fill('GOLDEN-CREDIT');
    await form.getByLabel('Alasan pencatatan atau perubahan', { exact: true }).fill('Transfer cabang untuk invoice ' + number);
    await form.getByRole('button', { name: 'Simpan draf pembayaran', exact: true }).click(); await pemohon.page.getByText(/^Draf tersimpan\./).waitFor();
    const drafts = committed('/api/v1/billing/payment-declarations'); assert.equal(drafts.length, 1); assert.equal(drafts[0].actor, pemohon.id); declarationId = String(drafts[0].response.declarationId);
    const proof = pemohon.page.getByRole('region', { name: 'Bukti asli pembayaran', exact: true });
    await proof.getByLabel('Pilih bukti pembayaran', { exact: true }).setInputFiles({ name: 'bukti-asli.png', mimeType: 'image/png', buffer: image });
    const local = proof.getByRole('region', { name: 'Pratinjau berkas pilihan', exact: true }); await local.getByRole('img').evaluate(element => element.decode()); await capture(pemohon.page, 'A12-proof-preview');
    await proof.getByRole('button', { name: 'Unggah bukti asli', exact: true }).click(); await proof.getByText('Tersimpan · menunggu pemeriksaan keamanan', { exact: true }).waitFor();
    assert.equal((await control('validate-evidence')).state, 'completed');
    const decoded = proof.getByText('Pemeriksaan keamanan selesai. Bukti tetap dalam draf; pembayaran belum diverifikasi.', { exact: true });
    for (let attempt = 0; attempt < 20 && !await decoded.isVisible(); attempt++) { await proof.getByRole('button', { name: 'Muat status bukti', exact: true }).click(); await decoded.waitFor({ timeout: 1000 }).catch(() => {}); }
    await decoded.waitFor(); await openOriginal(pemohon, proof, image);
    const preview = pemohon.page.getByRole('region', { name: /^Bukti asli · unggah/ }); await preview.waitFor(); await preview.getByRole('img').evaluate(element => element.decode());
    await capture(pemohon.page, 'A12-draft-with-original'); await preview.getByRole('button', { name: 'Tutup bukti', exact: true }).click();
  });
  await step('A12b', 'pemohon', 'reads the OCR suggestion, confirms and submits the payment as Pengajuan', async () => {
    const form = pemohon.page.getByRole('region', { name: 'Draf pembayaran', exact: true }), proof = pemohon.page.getByRole('region', { name: 'Bukti asli pembayaran', exact: true });
    await proof.getByRole('button', { name: 'Pilih bukti untuk diajukan', exact: true }).click();
    const confirmation = pemohon.page.getByRole('region', { name: 'Periksa pengajuan pembayaran', exact: true }); await confirmation.waitFor();
    assert.match(await confirmation.innerText(), /tidak mengurangi saldo tagihan/); await capture(pemohon.page, 'A12-submit-review');
    await confirmation.getByRole('button', { name: 'Ajukan untuk verifikasi', exact: true }).click(); await form.getByText('Pengajuan tersimpan', { exact: true }).waitFor();
    const submitted = writes.filter(record => /\/api\/v1\/billing\/payment-declarations\/[0-9]+\/submit$/.test(record.path) && record.status === 200); assert.equal(submitted.length, 1); assert.equal(submitted[0].actor, pemohon.id);
    const ocr = pemohon.page.getByRole('region', { name: 'Pembacaan bukti otomatis', exact: true }); await ocr.getByRole('button', { name: 'Muat hasil OCR', exact: true }).click();
    await ocr.getByText('Menunggu pembacaan', { exact: true }).waitFor(); assert.equal((await control('process-ocr')).state, 'completed');
    const available = ocr.getByText('Hasil pembacaan tersedia', { exact: true });
    for (let attempt = 0; attempt < 20 && !await available.isVisible(); attempt++) { await ocr.getByRole('button', { name: 'Muat hasil OCR', exact: true }).click(); await available.waitFor({ timeout: 1000 }).catch(() => {}); }
    await available.waitFor(); assert.match(await ocr.innerText(), /tidak mengubah pengajuan atau saldo tagihan/); await capture(pemohon.page, 'A12-submitted-with-ocr');
    ocrReading = await ocrAmount(ocr); assert.notEqual(digits(ocrReading), digits(total), 'The advisory reading differs from the invoice total');
    console.log('GOLDEN_JOURNEY_NUMBER ' + JSON.stringify({ payment: 'Pengajuan ' + declarationId }));
  });
  await step('A13a', 'pemeriksa-pembayaran', 'checks the exact original, corrects the OCR reading and verifies it against the bank credit', async () => {
    await paceApi(120); await renew(pemeriksa); assert.equal(await reachesFromWorkspace(pemeriksa, 'Pemeriksaan pembayaran'), '/billing/payment-reviews');
    await pemeriksa.page.goto(origin.origin + '/billing/payment-reviews'); await pemeriksa.page.getByRole('heading', { name: 'Pemeriksaan pembayaran', exact: true }).waitFor();
    await pemeriksa.page.getByRole('button', { name: 'Periksa pengajuan ' + declarationId, exact: true }).click();
    const review = pemeriksa.page.getByRole('region', { name: 'Rincian pengajuan pembayaran', exact: true }); await review.waitFor();
    await openOriginal(pemeriksa, review, image);
    const original = review.getByRole('region', { name: 'Bukti asli untuk pemeriksaan', exact: true }); await original.getByRole('img').evaluate(element => element.decode());
    await capture(pemeriksa.page, 'A13-original'); await original.getByRole('button', { name: 'Tutup bukti', exact: true }).click();
    const ocr = review.getByRole('region', { name: 'Pembacaan bukti otomatis', exact: true });
    await ocr.getByRole('button', { name: 'Muat hasil OCR', exact: true }).click(); await ocr.getByText('Hasil pembacaan tersedia', { exact: true }).waitFor();
    assert.equal(await ocrAmount(ocr), ocrReading, 'The checker sees the same OCR reading the buyer saw');
    const correction = review.getByRole('region', { name: 'Koreksi pemeriksaan', exact: true });
    await correction.getByRole('button', { name: 'Muat pemeriksaan terbaru', exact: true }).click();
    const ocrOriginal = await correction.locator('label[for="correction-amount"] + p').innerText();
    assert.equal(digits(ocrOriginal), digits(ocrReading), 'The correction form shows the OCR amount it replaces'); assert.doesNotMatch(ocrOriginal, /Belum diketahui/);
    for (const [label, value] of [['Nominal (IDR)', total], ['Mata uang', 'IDR'], ['Rekening tujuan pada bukti', world.account], ['Alasan koreksi', 'Nominal dan rekening dibaca dari bukti asli']])
      await correction.getByLabel(label, { exact: true }).fill(value);
    await correction.getByRole('button', { name: 'Periksa koreksi', exact: true }).click();
    const confirmCorrection = correction.getByRole('region', { name: 'Konfirmasi koreksi', exact: true }); await confirmCorrection.waitFor();
    await confirmCorrection.getByRole('button', { name: 'Simpan koreksi', exact: true }).click(); await correction.getByText(/Versi pemeriksaan 1\./).waitFor();
    const panel = review.getByRole('region', { name: 'Verifikasi pembayaran', exact: true });
    await panel.getByRole('button', { name: 'Muat ulang hasil verifikasi', exact: true }).click(); await panel.getByText('Belum ada verifikasi pembayaran yang tersimpan.', { exact: true }).waitFor();
    await panel.getByRole('button', { name: 'Pilih penerimaan bank', exact: true }).click();
    await panel.getByRole('button', { name: 'Periksa penerimaan ' + world.receiptId, exact: true }).click();
    await panel.getByRole('heading', { name: 'Tinjau pengaitan dana', exact: true }).waitFor();
    const boxes = panel.locator('form').last().getByRole('checkbox'); assert.equal(await boxes.count(), 3); for (let i = 0; i < 3; i++) await boxes.nth(i).check();
    const differences = panel.locator('textarea[id^="verification-"]:not(#verification-reason)');
    for (let i = 0; i < await differences.count(); i++) await differences.nth(i).fill('Synthetic independently resolved source difference');
    await panel.getByLabel('Referensi pemeriksaan identitas pembayar', { exact: true }).fill('Synthetic independent payer evidence');
    await panel.getByLabel('Alasan verifikasi', { exact: true }).fill('Synthetic review of exact image, bank receipt and payer');
    await panel.getByRole('button', { name: 'Periksa keputusan verifikasi', exact: true }).click();
    const confirm = panel.getByRole('region', { name: 'Konfirmasi verifikasi pembayaran', exact: true }); await confirm.waitFor(); await capture(pemeriksa.page, 'A13-verification-review');
    await confirm.getByRole('button', { name: 'Simpan verifikasi pembayaran', exact: true }).click(); await panel.getByRole('heading', { name: 'Pembayaran terverifikasi', exact: true }).waitFor();
    const verified = writes.filter(record => record.path.endsWith('/verification') && record.status === 200); assert.equal(verified.length, 1); assert.equal(verified[0].actor, pemeriksa.id);
    await capture(pemeriksa.page, 'A13-verified');
  });
  await step('A13b', 'pemeriksa-pembayaran', 'allocates the verified payment to the invoice', async () => {
    const page = pemeriksa.page; await page.getByRole('button', { name: 'Muat ulang hasil verifikasi', exact: true }).click();
    const panel = page.getByRole('region', { name: 'Alokasi pembayaran', exact: true });
    await panel.getByRole('button', { name: 'Muat saldo alokasi', exact: true }).click(); await panel.getByRole('heading', { name: 'Saldo dana dan tagihan', exact: true }).waitFor();
    const choice = panel.getByRole('checkbox'); assert.equal(await choice.count(), 1); await choice.check();
    await panel.getByLabel('Nominal alokasi (IDR)', { exact: true }).fill(total.replace('.', ','));
    await panel.getByLabel('Alasan alokasi', { exact: true }).fill('Synthetic full allocation of the verified payment to its invoice');
    await panel.getByRole('button', { name: 'Periksa alokasi', exact: true }).click();
    const confirm = panel.getByRole('region', { name: 'Konfirmasi alokasi pembayaran', exact: true }); await confirm.waitFor(); await capture(page, 'A13-allocation-review');
    await confirm.getByRole('button', { name: 'Simpan alokasi pembayaran', exact: true }).click();
    // This invoice's own row shows it paid in full: status Lunas, paid = total, nothing left to pay.
    const parts = number.split('/'), padded = new RegExp('^Invoice ' + parts.slice(0, 3).join('/') + '/0*' + parts[3] + '$'); // the desk zero-pads the sequence
    const row = panel.locator('li', { has: page.getByText(padded) }); await row.getByText(/^Lunas ·/).waitFor();
    const figures = /Total (Rp\s?[0-9.]+,[0-9]{2}) · Dibayar (Rp\s?[0-9.]+,[0-9]{2}) · Kredit (Rp\s?[0-9.]+,[0-9]{2}) · Sisa tagihan (Rp\s?[0-9.]+,[0-9]{2})/.exec(await row.innerText());
    assert.ok(figures, 'The allocation row states its figures'); assert.equal(decimal(figures[1]), total); assert.equal(decimal(figures[2]), total); assert.equal(decimal(figures[3]), '0.00'); assert.equal(decimal(figures[4]), '0.00');
    const allocated = writes.filter(record => record.path.endsWith('/allocations') && record.status === 200); assert.equal(allocated.length, 1); assert.equal(allocated[0].actor, pemeriksa.id);
    await capture(page, 'A13-allocated');
  });
  await step('A13c', 'pemohon', 'sees the verified bank credit and the allocation to the invoice on the order', async () => {
    await pemohon.page.goto(origin.origin + '/ordering/orders'); await pemohon.page.getByRole('button', { name: 'Lihat rincian pesanan ' + received.order, exact: true }).click();
    const timeline = pemohon.page.getByRole('region', { name: 'Riwayat pemenuhan dan penyelesaian', exact: true }); await timeline.waitFor();
    await timeline.getByRole('button', { name: 'Muat pengajuan pembayaran saya terbaru', exact: true }).click();
    await timeline.getByRole('button', { name: 'Periksa pembayaran ' + declarationId, exact: true }).click();
    // The buyer's own page states this payment's verification and its allocation to this invoice, both by the checker's account.
    await timeline.getByText(new RegExp('^Penerimaan bank diverifikasi .+ oleh akun ' + pemeriksa.id + '[.]$')).waitFor();
    await timeline.getByText(new RegExp('^Pembayaran dialokasikan ke invoice ' + String(invoice.id) + ' · .+ · Dicatat akun ' + pemeriksa.id + '$')).waitFor();
    await capture(pemohon.page, 'A13-buyer-sees-allocation');
  });
  await step('A13d', 'pemohon', 'cannot reach the payment verification desk', async () => {
    await pemohon.page.goto(origin.origin + '/billing/payment-reviews'); await pemohon.page.getByRole('heading', { name: 'Halaman ini di luar tugas akun Anda', exact: true }).waitFor();
    assert.equal(await pemohon.page.getByRole('button', { name: /^Periksa pengajuan / }).count(), 0, 'The buyer has no queue to open');
    assert.equal(await pemohon.page.getByRole('region', { name: /Verifikasi pembayaran|Alokasi pembayaran|Rincian pengajuan pembayaran/ }).count(), 0, 'The buyer has no verification or allocation desk');
    assert.equal(await pemohon.page.getByRole('button', { name: /^(Simpan verifikasi pembayaran|Periksa keputusan verifikasi|Simpan alokasi pembayaran)$/ }).count(), 0, 'The buyer has no verification control');
    await capture(pemohon.page, 'A13-buyer-no-verification-desk');
  });
  return { order: received.order, shipment: received.shipment, invoice: number, payment: 'Pengajuan ' + declarationId, total };
}

async function procurementToReceipt() {
  const pengadaan = await persona('pengadaan'), penyetuju = await persona('penyetuju-pengadaan'), gudang = await persona('petugas-gudang');
  const quote = 'SYNTHETIC-QUOTE-GOLDEN-0001', delivery = 'SYNTHETIC-SUPPLIER-DELIVERY-GOLDEN-001';
  async function choose(page, region) {
    const select = page.getByLabel('Organisasi / toko / gudang / kanal', { exact: true });
    await select.getByRole('option', { name: journey.locationLabel, exact: true }).waitFor({ state: 'attached' });
    await select.selectOption(journey.location); await page.getByRole('region', { name: region, exact: true }).waitFor();
  }
  let requestId, request, order;
  await step('B1', 'pengadaan', 'requests catalog packages and gets PP-', async () => {
    assert.equal(await reachesFromWorkspace(pengadaan, 'Permintaan pengadaan'), '/procurement/requests');
    await pengadaan.page.goto(origin.origin + '/procurement/requests'); await choose(pengadaan.page, 'Rincian pengadaan');
    await pengadaan.page.getByRole('button', { name: 'Buat permintaan', exact: true }).click();
    await pengadaan.page.getByLabel('Jalur pengadaan', { exact: true }).selectOption('ddo');
    await pengadaan.page.getByLabel('Tanggal kebutuhan', { exact: true }).fill(journey.businessDate);
    await pengadaan.page.getByLabel('Cari barang atau kemasan', { exact: true }).fill(journey.skuCode);
    await pengadaan.page.getByRole('button', { name: 'Cari kemasan', exact: true }).click();
    await pengadaan.page.getByRole('button', { name: new RegExp(journey.skuCode + ' · ' + journey.packageLabel) }).click();
    const chosen = pengadaan.page.getByRole('region', { name: 'Periksa kemasan pilihan', exact: true });
    await chosen.getByLabel('Jumlah kemasan', { exact: true }).fill('6'); await chosen.getByRole('button', { name: 'Tambahkan kemasan', exact: true }).click();
    await pengadaan.page.getByLabel('Alasan permintaan', { exact: true }).fill('Persediaan alat tulis untuk cabang menipis');
    await pengadaan.page.getByRole('button', { name: 'Periksa rincian permintaan', exact: true }).click();
    const review = pengadaan.page.getByRole('region', { name: 'Rincian permintaan', exact: true }); await review.waitFor();
    await review.getByRole('checkbox', { name: 'Saya telah memeriksa jalur, tanggal, kemasan dan jumlah permintaan.', exact: true }).check();
    await capture(pengadaan.page, 'B1-request-review');
    await review.getByRole('button', { name: 'Kirim permintaan', exact: true }).click();
    await pengadaan.page.getByRole('heading', { name: 'Menunggu pemeriksaan', exact: true }).waitFor();
    const saved = pengadaan.page.getByText(/^Permintaan PP-[0-9]{6} · /); await saved.waitFor();
    request = /PP-[0-9]{6}/.exec(await saved.innerText())[0]; requestId = String(Number(request.slice(3)));
    const row = pengadaan.page.getByRole('button', { name: new RegExp('DDO.*Permintaan ' + request + '$') });
    // Observed 22 Okt 2026: the list beside the saved request still says "Belum ada permintaan pengadaan." until reloaded (F-022).
    const listedAtOnce = await row.count() > 0;
    console.log('GOLDEN_JOURNEY_OBSERVATION ' + JSON.stringify({ step: 'B1', requestListedWithoutReload: listedAtOnce }));
    // "Muat data terbaru" also clears the chosen location, so the location is chosen again (F-022).
    if (!listedAtOnce) { await pengadaan.page.getByRole('button', { name: 'Muat data terbaru', exact: true }).click(); await choose(pengadaan.page, 'Rincian pengadaan'); }
    await row.waitFor();
    const submitted = writes.filter(record => record.path === '/api/v1/procurement/requests/submissions' && record.status === 200);
    assert.equal(submitted.length, 1); assert.equal(submitted[0].actor, pengadaan.id);
    await row.click(); await pengadaan.page.getByRole('heading', { name: 'Menunggu pemeriksaan', exact: true }).waitFor();
    await pengadaan.page.getByText('Permintaan ' + request, { exact: false }).first().waitFor();
    assert.equal(await pengadaan.page.getByRole('button', { name: 'Tinjau persetujuan', exact: true }).count(), 0, 'The maker cannot approve the own request');
    await capture(pengadaan.page, 'B1-request-waiting'); console.log('GOLDEN_JOURNEY_NUMBER ' + JSON.stringify({ request }));
  });
  await step('B2', 'penyetuju-pengadaan', 'approves the request', async () => {
    await renew(penyetuju); assert.equal(await reachesFromWorkspace(penyetuju, 'Permintaan pengadaan'), '/procurement/requests');
    await penyetuju.page.goto(origin.origin + '/procurement/requests'); await choose(penyetuju.page, 'Rincian pengadaan');
    await penyetuju.page.getByRole('button', { name: new RegExp('DDO.*Permintaan ' + request + '$') }).click();
    await penyetuju.page.getByLabel('Alasan keputusan', { exact: true }).fill('Kebutuhan dan kemasan diperiksa');
    await penyetuju.page.getByRole('button', { name: 'Tinjau persetujuan', exact: true }).click();
    const decision = penyetuju.page.getByRole('region', { name: 'Rincian keputusan', exact: true }); await decision.waitFor();
    await decision.getByRole('checkbox', { name: 'Saya telah memeriksa permintaan dan siap mencatat keputusan ini.', exact: true }).check();
    await decision.getByRole('button', { name: 'Setujui permintaan', exact: true }).click();
    await penyetuju.page.getByRole('heading', { name: 'Disetujui', exact: true }).waitFor(); await capture(penyetuju.page, 'B2-request-approved');
  });
  await step('B3', 'pengadaan', 'proposes the supplier order from the approved ' + 'PP', async () => {
    assert.equal(await reachesFromWorkspace(pengadaan, 'Pesanan pembelian'), '/procurement/purchase-orders');
    await pengadaan.page.goto(origin.origin + '/procurement/purchase-orders'); await choose(pengadaan.page, 'Rincian pesanan pembelian');
    await pengadaan.page.getByRole('button', { name: 'Buat pesanan pembelian', exact: true }).click();
    const requestSelect = pengadaan.page.getByLabel('Permintaan yang disetujui', { exact: true });
    await requestSelect.selectOption(requestId); await pengadaan.page.getByLabel('Pemasok yang disetujui', { exact: true }).selectOption(journey.supplierId);
    const line = pengadaan.page.getByRole('region', { name: 'Tambah barang pesanan', exact: true });
    await line.getByLabel('Barang permintaan', { exact: true }).selectOption(journey.skuId); await line.getByLabel('Pemetaan pemasok', { exact: true }).selectOption('0');
    for (const [label, value] of [['Jumlah kemasan', '6'], ['Harga neto per kemasan (IDR)', '10000.00'], ['Pajak seluruh baris (IDR)', '6600.00'], ['Biaya pemasok seluruh baris (IDR)', '0.00']])
      await line.getByLabel(label, { exact: true }).fill(value);
    await line.getByRole('button', { name: 'Tambahkan barang', exact: true }).click();
    for (const [label, value] of [['Tanggal pesanan', journey.businessDate], ['Referensi dokumen komersial', quote], ['Tanggal dokumen', journey.businessDate],
      ['Berlaku hingga (WIB)', journey.validUntilWib], ['Rincian syarat pembayaran', 'Kredit tiga puluh hari setelah penerimaan'], ['Uang muka yang disyaratkan (IDR)', '0.00']])
      await pengadaan.page.getByLabel(label, { exact: true }).fill(value);
    for (const [label, value] of [['Jenis dokumen komersial', 'quote'], ['Syarat pembayaran', 'credit'], ['Pemicu pelunasan', 'due_date']])
      await pengadaan.page.getByLabel(label, { exact: true }).selectOption(value);
    await pengadaan.page.getByLabel('Tanggal jatuh tempo', { exact: true }).fill(journey.dueDate);
    await pengadaan.page.getByLabel('Alasan usulan', { exact: true }).fill('Pemasok tetap dengan harga penawaran terbaru');
    await pengadaan.page.getByRole('button', { name: 'Periksa pesanan pembelian', exact: true }).click();
    const review = pengadaan.page.getByRole('region', { name: 'Rincian usulan pesanan', exact: true }); await review.waitFor();
    const text = await review.innerText(); assert.ok(text.includes(request), 'The order review names the approved request ' + request);
    assert.ok(text.includes('Rp66.600,00'), 'Money is written Rp66.600,00 (no space)');
    await review.getByRole('checkbox', { name: 'Saya telah memeriksa pemasok, kemasan, jumlah, nilai dan syarat pembayaran.', exact: true }).check();
    await capture(pengadaan.page, 'B3-order-review');
    await review.getByRole('button', { name: 'Kirim usulan pesanan', exact: true }).click();
    await pengadaan.page.getByRole('heading', { name: 'Menunggu pemeriksaan', exact: true }).waitFor();
    await pengadaan.page.getByText(/^Usulan pesanan [0-9]+ · /).waitFor();
    // Same as B1: the proposal list beside the saved proposal still says "Belum ada usulan pesanan." (F-022).
    console.log('GOLDEN_JOURNEY_OBSERVATION ' + JSON.stringify({ step: 'B3', proposalListedWithoutReload: await pengadaan.page.getByRole('button', { name: /Usulan pesanan [0-9]+$/ }).count() > 0 }));
    await capture(pengadaan.page, 'B3-order-proposed');
    const proposed = writes.filter(record => record.path === '/api/v1/procurement/purchase-orders/proposals' && record.status === 200);
    assert.equal(proposed.length, 1); assert.equal(proposed[0].actor, pengadaan.id);
  });
  await step('B4', 'penyetuju-pengadaan', 'approves the supplier order internally', async () => {
    await renew(penyetuju); await penyetuju.page.goto(origin.origin + '/procurement/purchase-orders'); await choose(penyetuju.page, 'Rincian pesanan pembelian');
    await penyetuju.page.getByRole('button', { name: /Usulan pesanan [0-9]+$/ }).first().click();
    await penyetuju.page.getByLabel('Alasan keputusan', { exact: true }).fill('Pemasok, kemasan dan nilai diperiksa');
    await penyetuju.page.getByRole('button', { name: 'Tinjau persetujuan pesanan', exact: true }).click();
    const decision = penyetuju.page.getByRole('region', { name: 'Rincian keputusan pesanan', exact: true }); await decision.waitFor();
    await decision.getByRole('checkbox').first().check();
    await decision.getByRole('button', { name: 'Setujui pesanan secara internal', exact: true }).click();
    await penyetuju.page.getByRole('heading', { name: 'Disetujui secara internal', exact: true }).waitFor();
    const detail = await penyetuju.page.getByRole('region', { name: 'Rincian pesanan pembelian', exact: true }).innerText();
    assert.ok(detail.includes(request), 'The approved order still names ' + request);
    console.log('GOLDEN_JOURNEY_OBSERVATION ' + JSON.stringify({ step: 'B4', supplierOrderNumberOnOrderPage: /SP-[0-9]{6}/.test(detail) }));
    await capture(penyetuju.page, 'B4-order-approved');
  });
  await step('B5', 'pengadaan', 'records the supplier arrival notice against SP-', async () => {
    await pengadaan.page.goto(origin.origin + '/procurement/receiving'); await choose(pengadaan.page, 'Rincian penerimaan pengadaan');
    await pengadaan.page.getByRole('button', { name: 'Catat pemberitahuan pemasok', exact: true }).click();
    const orderSelect = pengadaan.page.getByLabel('Pesanan pembelian yang disetujui', { exact: true });
    const option = orderSelect.getByRole('option', { name: journey.supplierName + ' · DDO · ' + quote, exact: true }); await option.waitFor({ state: 'attached' });
    await orderSelect.selectOption({ label: journey.supplierName + ' · DDO · ' + quote });
    const source = await pengadaan.page.getByRole('region', { name: 'Pesanan sumber penerimaan', exact: true }).innerText();
    order = /SP-[0-9]{6}/.exec(source)?.[0]; assert.ok(order, 'The receiving page names the supplier order with SP-');
    for (const [label, value] of [['Tanggal pemberitahuan pemasok', journey.businessDate], ['Referensi dokumen pemasok', delivery], ['Referensi permintaan DDO', 'SYNTHETIC-DDO-REQUEST-GOLDEN'],
      ['Referensi konfirmasi telepon DDO', 'SYNTHETIC-DDO-TELEPHONE-GOLDEN'], ['Koli yang dinyatakan pemasok', '5'], ['Catatan pemberitahuan pemasok', 'Pemasok mengabarkan lima koli'],
      ['Alasan pencatatan pemberitahuan', 'Pemberitahuan pemasok diterima melalui telepon']]) await pengadaan.page.getByLabel(label, { exact: true }).fill(value);
    await pengadaan.page.getByRole('button', { name: 'Tinjau pemberitahuan pemasok', exact: true }).click();
    const review = pengadaan.page.getByRole('region', { name: 'Konfirmasi pemberitahuan pemasok', exact: true }); await review.waitFor();
    await review.getByRole('checkbox').check(); await review.getByRole('button', { name: 'Simpan pemberitahuan pemasok', exact: true }).click();
    await pengadaan.page.getByText('Pemberitahuan pemasok tersimpan. Penerimaan fisik belum dicatat.', { exact: true }).waitFor();
    const savedNotice = pengadaan.page.locator('body');
    await savedNotice.getByText(order, { exact: false }).first().waitFor();
    // Same as B1/B3: the notice list beside the saved notice still says "Belum ada pemberitahuan pemasok." (F-022).
    console.log('GOLDEN_JOURNEY_OBSERVATION ' + JSON.stringify({ step: 'B5', noticeListedWithoutReload: await pengadaan.page.getByRole('button').filter({ hasText: delivery }).count() > 0 }));
    await capture(pengadaan.page, 'B5-notice');
    console.log('GOLDEN_JOURNEY_NUMBER ' + JSON.stringify({ order }));
  });
  await step('B6', 'petugas-gudang', 'records the koli physically received for ' + 'SP', async () => {
    assert.equal(await reachesFromWorkspace(gudang, 'Penerimaan pengadaan'), '/procurement/receiving');
    await gudang.page.goto(origin.origin + '/procurement/receiving'); await choose(gudang.page, 'Rincian penerimaan pengadaan');
    await gudang.page.getByRole('button').filter({ hasText: delivery }).first().click();
    const detail = gudang.page.getByRole('region', { name: 'Rincian penerimaan pengadaan', exact: true }); await detail.waitFor();
    await gudang.page.getByText(order, { exact: false }).first().waitFor();
    await detail.getByRole('button', { name: 'Catat penerimaan fisik koli', exact: true }).click();
    for (const [label, value] of [['Tanggal penerimaan fisik', journey.businessDate], ['Referensi penerimaan fisik', 'SYNTHETIC-CONTAINER-INTAKE-GOLDEN'], ['Koli cocok pada penerimaan ini', '3'],
      ['Koli lebih pada penerimaan ini', '1'], ['Koli ditahan pada penerimaan ini', '1'], ['Perbedaan dan tindak lanjut', 'Satu koli lebih ditahan untuk diperiksa'],
      ['Alasan pencatatan penerimaan', 'Koli dihitung saat bongkar muat']]) await gudang.page.getByLabel(label, { exact: true }).fill(value);
    await gudang.page.getByLabel('Kondisi teramati', { exact: true }).selectOption('mixed');
    await gudang.page.getByRole('button', { name: 'Tinjau penerimaan fisik koli', exact: true }).click();
    const review = gudang.page.getByRole('region', { name: 'Konfirmasi penerimaan fisik koli', exact: true }); await review.waitFor();
    assert.ok((await review.innerText()).includes('Tidak ada penambahan stok barang atau penempatan rak otomatis.'));
    await review.getByRole('checkbox').check(); await capture(gudang.page, 'B6-intake-review');
    await review.getByRole('button', { name: /^Simpan penerimaan/ }).click();
    await gudang.page.getByRole('region', { name: 'Riwayat penerimaan fisik', exact: true }).getByText('SYNTHETIC-CONTAINER-INTAKE-GOLDEN', { exact: false }).first().waitFor();
    await capture(gudang.page, 'B6-received');
  });
  return { request, order };
}

// F-018 and F-017: keranjang changes (quantity, a price changed by the store, removing the last line), draft discard and
// restore, the buyer cancelling an outstanding order, and the store rejecting its part of another.
async function cartAndCancellation() {
  const pemohon = await persona('pemohon'), penyetuju = await persona('penyetuju-cabang'), peninjau = await persona('peninjau-toko');
  const cartLines = () => pemohon.page.getByRole('region', { name: 'Barang di keranjang', exact: true });
  const summary = () => pemohon.page.getByRole('complementary', { name: 'Ringkasan belanja', exact: true });
  const checkoutReady = () => pemohon.page.getByRole('link', { name: 'Lanjut ke pengajuan', exact: true });
  let packages, before;
  await step('K1', 'pemohon', 'changes the quantity in the keranjang and the total follows', async () => {
    packages = await addToCart(pemohon, 'K1'); before = await summary().innerText();
    await cartLines().getByRole('button', { name: 'Tambah jumlah', exact: true }).click();
    for (let tries = 0; tries < 60 && (await summary().innerText()) === before; tries++) await pemohon.page.waitForTimeout(500);
    await checkoutReady().waitFor();
    const typed = Number(await cartLines().getByLabel('Jumlah', { exact: true }).inputValue()); assert.ok(typed > packages, 'Quantity went up');
    await pemohon.page.reload(); await cartLines().waitFor();
    assert.equal(Number(await cartLines().getByLabel('Jumlah', { exact: true }).inputValue()), typed, 'The new quantity was saved, not only typed');
    assert.notEqual(await summary().innerText(), before); await capture(pemohon.page, 'K1-quantity-changed');
  });
  await step('K2', 'pemohon', 'sees a price the store changed and accepts it explicitly', async () => {
    const changed = await control('change-price'); await pemohon.page.reload(); await cartLines().waitFor();
    const notice = cartLines().getByText(/^Harga berubah menjadi Rp/); await notice.waitFor();
    assert.equal(await checkoutReady().count(), 0, 'Checkout waits until the buyer accepts the new price');
    await capture(pemohon.page, 'K2-price-changed');
    await cartLines().getByRole('button', { name: 'Pakai harga terbaru', exact: true }).click(); await notice.waitFor({ state: 'hidden' }); await checkoutReady().waitFor();
    assert.ok((await cartLines().innerText()).includes(changed.display), 'The keranjang now uses ' + changed.display);
  });
  await step('K3', 'pemohon', 'removes the last line and the keranjang is empty', async () => {
    await cartLines().getByRole('button', { name: /^Hapus .* dari keranjang$/ }).click();
    await pemohon.page.getByRole('heading', { name: 'Keranjang masih kosong', exact: true }).waitFor(); await capture(pemohon.page, 'K3-cart-empty');
  });
  await step('K4', 'pemohon', 'restores the discarded draft, then discards it again', async () => {
    await pemohon.page.goto(origin.origin + '/ordering/drafts');
    await pemohon.page.getByLabel('Cabang pemesan', { exact: true }).selectOption(journey.branch);
    const discarded = pemohon.page.getByRole('region', { name: 'Draf yang dibuang', exact: true }); await discarded.waitFor();
    const restore = discarded.getByRole('button', { name: /^Pulihkan draf / });
    await discarded.getByRole('button', { name: /^Pulihkan draf / }).or(discarded.getByText('Tidak ada draf yang dibuang di cabang ini.', { exact: true })).first().waitFor();
    const emptiedCartIsDiscarded = await restore.count();
    console.log('GOLDEN_JOURNEY_OBSERVATION ' + JSON.stringify({ step: 'K4', emptiedCartDraftsDiscarded: emptiedCartIsDiscarded }));
    if (emptiedCartIsDiscarded === 0) {
      await addToCart(pemohon, 'K4'); await pemohon.page.goto(origin.origin + '/ordering/drafts');
      await pemohon.page.getByLabel('Cabang pemesan', { exact: true }).selectOption(journey.branch);
    } else { await restore.first().click(); await discarded.getByText('Tidak ada draf yang dibuang di cabang ini.', { exact: true }).waitFor(); }
    const list = pemohon.page.getByRole('region', { name: 'Daftar draf', exact: true }); await list.getByRole('button', { name: /Draf [0-9]+/ }).first().click();
    await pemohon.page.getByRole('button', { name: 'Buang draf', exact: true }).first().click();
    await pemohon.page.getByRole('heading', { name: 'Buang draf ini?', exact: true }).waitFor(); await capture(pemohon.page, 'K4-discard-review');
    await pemohon.page.getByRole('button', { name: 'Buang draf', exact: true }).last().click();
    await pemohon.page.getByText(/^Draf dibuang\. Anda dapat memulihkannya sampai /).waitFor();
    await discarded.getByRole('button', { name: /^Pulihkan draf / }).first().waitFor(); await capture(pemohon.page, 'K4-discarded');
  });
  let cancelled;
  await step('K5', 'pemohon', 'cancels an outstanding order of their own', async () => {
    await addToCart(pemohon, 'K5'); cancelled = await checkoutFromCart(pemohon, 'K5');
    // F-029: a buyer who may cancel has no menu entry to the cancellation page; it is reached by its address.
    console.log('GOLDEN_JOURNEY_OBSERVATION ' + JSON.stringify({ step: 'K5', cancellationInBuyerMenu: await reachesFromWorkspace(pemohon, 'Peninjauan pesanan').then(() => true, () => false) }));
    await openCancellationReviews(pemohon);
    await pemohon.page.getByRole('button', { name: new RegExp('^Tinjau pesanan ' + cancelled.order) }).click();
    const panel = pemohon.page.getByRole('region', { name: 'Tinjauan keputusan', exact: true });
    await panel.getByLabel('Catatan peninjauan', { exact: true }).fill('Kebutuhan cabang dibatalkan');
    await panel.getByRole('button', { name: 'Batalkan sisa pesanan', exact: true }).click(); await capture(pemohon.page, 'K5-cancel-review');
    await panel.getByRole('button', { name: 'Konfirmasi pembatalan sisa', exact: true }).click();
    await pemohon.page.getByText('Sisa pesanan dibatalkan. Cadangan barang yang belum dikirim telah dilepas.', { exact: true }).waitFor();
    await pemohon.page.goto(origin.origin + '/ordering/orders'); await pemohon.page.getByRole('button', { name: 'Lihat rincian pesanan ' + cancelled.order, exact: true }).click();
    await pemohon.page.getByRole('region', { name: 'Rincian pesanan', exact: true }).getByText(/dibatalkan/i).first().waitFor(); await capture(pemohon.page, 'K5-cancelled');
  });
  return { cancelled: cancelled.order };
}

// The review page shows its view tabs only when the account has 2+ views (a3efa6d4); a cancel-only buyer lands on Cancellation.
async function openCancellationReviews(actor) {
  await actor.page.goto(origin.origin + '/ordering/reviews');
  await actor.page.getByRole('button', { name: 'Muat ulang sesi dan akses', exact: true }).waitFor();
  const tab = actor.page.getByRole('button', { name: 'Pembatalan pesanan', exact: true });
  if (await tab.count()) await tab.click();
}

// F-030: after a first order from the keranjang, a second order from the keranjang can be submitted, and the store can
// reject its part of it (PTK-02).
async function secondOrderAndStoreRejection() {
  const pemohon = await persona('pemohon'), penyetuju = await persona('penyetuju-cabang'), peninjau = await persona('peninjau-toko');
  let first, rejected;
  await step('S1', 'pemohon', 'places a first order from the keranjang and cancels it (its stock is released)', async () => {
    await addToCart(pemohon, 'S1'); first = await checkoutFromCart(pemohon, 'S1');
    await openCancellationReviews(pemohon);
    await pemohon.page.getByRole('button', { name: new RegExp('^Tinjau pesanan ' + first.order) }).click();
    const panel = pemohon.page.getByRole('region', { name: 'Tinjauan keputusan', exact: true });
    await panel.getByLabel('Catatan peninjauan', { exact: true }).fill('Diganti pesanan baru');
    await panel.getByRole('button', { name: 'Batalkan sisa pesanan', exact: true }).click(); await panel.getByRole('button', { name: 'Konfirmasi pembatalan sisa', exact: true }).click();
    await pemohon.page.getByText('Sisa pesanan dibatalkan. Cadangan barang yang belum dikirim telah dilepas.', { exact: true }).waitFor();
  });
  await step('S2', 'pemohon', 'adds to the keranjang again and checkout offers a new order', async () => {
    await addToCart(pemohon, 'S2');
    await pemohon.page.getByRole('link', { name: 'Lanjut ke pengajuan', exact: true }).click(); await pemohon.page.waitForURL(url => url.pathname === '/ordering/checkout');
    await giveDelivery(pemohon);
    const earlier = pemohon.page.getByRole('heading', { name: 'Pesanan ' + first.order + ' tercatat', exact: true });
    const review = pemohon.page.getByRole('heading', { name: 'Periksa sebelum mengajukan', exact: true });
    await pemohon.page.getByRole('button', { name: 'Tinjau pesanan dan biaya', exact: true }).click();
    await earlier.or(review).first().waitFor(); await capture(pemohon.page, 'S2-second-checkout');
    assert.equal(await earlier.count(), 0, 'The second keranjang must not reopen ' + first.order + ' as already recorded (F-030)');
    await pemohon.page.getByRole('button', { name: /^Ajukan (pesanan|untuk persetujuan)$/ }).click();
    await pemohon.page.getByRole('heading', { name: /^Pesanan PSN-[0-9]{6} tercatat$/ }).waitFor();
    const submit = committed('/api/v1/ordering/orders'); rejected = { orderId: submit.at(-1).response.orderId, suborderId: submit.at(-1).response.suborderIds[0] };
    rejected.order = recordNumber('order', rejected.orderId);
    assert.notEqual(rejected.order, first.order);
  });
  await step('S3', 'peninjau-toko', 'rejects the store part of another order and the buyer sees it', async () => {
    await renew(penyetuju); await penyetuju.page.goto(origin.origin + '/ordering/reviews');
    await penyetuju.page.getByRole('button', { name: 'Tinjau pesanan ' + rejected.order, exact: true }).click();
    let panel = penyetuju.page.getByRole('region', { name: 'Tinjauan keputusan', exact: true });
    await panel.getByLabel('Catatan peninjauan', { exact: true }).fill('Belanja sesuai kebutuhan');
    await panel.getByRole('button', { name: 'Setujui belanja', exact: true }).click(); await panel.getByRole('button', { name: 'Konfirmasi persetujuan belanja', exact: true }).click();
    await panel.getByText('Peninjauan selesai atau tidak ada cadangan yang dapat dibatalkan.', { exact: true }).waitFor();
    await renew(peninjau); await peninjau.page.goto(origin.origin + '/ordering/reviews');
    await peninjau.page.getByRole('button', { name: 'Tinjau pesanan ' + rejected.order + ' / ' + rejected.suborderId, exact: true }).click();
    panel = peninjau.page.getByRole('region', { name: 'Tinjauan keputusan', exact: true });
    await panel.getByLabel('Catatan peninjauan', { exact: true }).fill('Kemasan tidak dapat dipenuhi toko');
    await panel.getByRole('button', { name: 'Tolak pesanan toko', exact: true }).click(); await capture(peninjau.page, 'S3-reject-review');
    await panel.getByRole('button', { name: 'Konfirmasi penolakan', exact: true }).click();
    await panel.getByText('Bagian pesanan ini sudah ditolak toko.', { exact: true }).waitFor();
    await pemohon.page.goto(origin.origin + '/ordering/orders'); await pemohon.page.getByRole('button', { name: 'Lihat rincian pesanan ' + rejected.order, exact: true }).click();
    await pemohon.page.getByRole('region', { name: 'Rincian pesanan', exact: true }).getByText(/ditolak/i).first().waitFor(); await capture(pemohon.page, 'S3-rejected');
  });
  return { first: first.order, rejected: rejected.order };

}

// F-013: the pilot's core journey, driven in the full release: compose and send a branch request, store accepts one and rejects another.
async function branchRequests() {
  const pemohon = await persona('pemohon'), peninjau = await persona('peninjau-toko');
  async function compose(note) {
    await pemohon.page.goto(origin.origin + '/ordering/requests');
    const tabs = pemohon.page.getByRole('navigation', { name: 'Bagian permintaan', exact: true });
    await tabs.getByRole('button', { name: 'Buat permintaan', exact: true }).click();
    const picker = pemohon.page.getByRole('region', { name: 'Pilih kemasan permintaan', exact: true });
    await picker.getByRole('combobox').first().selectOption(journey.branch);
    await picker.getByLabel('Cari nama atau kode kemasan', { exact: true }).fill(journey.skuCode);
    await picker.getByRole('button', { name: 'Cari kemasan', exact: true }).click();
    await picker.getByRole('button', { name: 'Pilih ' + journey.skuCode, exact: true }).click();
    const chosen = pemohon.page.getByRole('region', { name: 'Periksa kemasan pilihan', exact: true });
    await chosen.getByLabel('Jumlah kemasan', { exact: true }).fill('2'); await chosen.getByRole('button', { name: 'Tambahkan kemasan', exact: true }).click();
    const contents = pemohon.page.getByRole('region', { name: 'Isi permintaan', exact: true });
    await contents.getByLabel('Catatan kebutuhan (opsional)', { exact: true }).fill(note);
    await contents.getByRole('button', { name: 'Tinjau permintaan', exact: true }).click();
    const confirm = pemohon.page.getByRole('region', { name: 'Konfirmasi permintaan', exact: true }); await confirm.waitFor();
    assert.ok(noPrice(await pemohon.page.locator('main').innerText()), 'A branch request shows no price');
    await confirm.getByRole('button', { name: 'Konfirmasi ajukan permintaan', exact: true }).click();
    const result = pemohon.page.getByRole('region', { name: 'Hasil permintaan', exact: true });
    const heading = result.getByRole('heading', { name: /^PB-[0-9]{6,} tercatat$/ }); await heading.waitFor();
    await result.getByText('Menunggu peninjauan', { exact: true }).waitFor();
    return /PB-[0-9]{6,}/.exec(await heading.innerText())[0];
  }
  async function decide(number, accept) {
    await peninjau.page.goto(origin.origin + '/ordering/requests/incoming');
    await peninjau.page.getByRole('button', { name: 'Lihat ' + number, exact: true }).click();
    const detail = peninjau.page.getByRole('region', { name: 'Rincian permintaan', exact: true });
    await detail.getByLabel('Catatan peninjauan', { exact: true }).fill(accept ? 'Kemasan tersedia untuk cabang' : 'Kemasan ini sedang tidak dijual');
    await detail.getByRole('button', { name: accept ? 'Terima permintaan' : 'Tolak permintaan', exact: true }).click();
    await detail.getByRole('button', { name: accept ? 'Konfirmasi terima permintaan' : 'Konfirmasi tolak permintaan', exact: true }).click();
    await detail.getByText(accept ? 'Diterima untuk diproses' : 'Ditolak toko', { exact: false }).first().waitFor();
  }
  let first, second;
  await step('R1', 'pemohon', 'composes and sends a branch request and gets its number', async () => {
    assert.equal(await reachesFromWorkspace(pemohon, 'Permintaan saya'), '/ordering/requests');
    first = await compose('Kebutuhan rapat cabang'); await capture(pemohon.page, 'R1-request-sent');
    console.log('GOLDEN_JOURNEY_NUMBER ' + JSON.stringify({ branchRequest: first }));
  });
  await step('R2', 'peninjau-toko', 'accepts the incoming request', async () => {
    assert.equal(await reachesFromWorkspace(peninjau, 'Permintaan masuk'), '/ordering/requests/incoming');
    await decide(first, true); await capture(peninjau.page, 'R2-accepted');
  });
  await step('R3', 'pemohon', 'sends a second request that the store rejects, and sees the reason', async () => {
    second = await compose('Kebutuhan tambahan');
    await decide(second, false); await capture(peninjau.page, 'R3-rejected');
    await pemohon.page.goto(origin.origin + '/ordering/requests');
    await pemohon.page.getByRole('navigation', { name: 'Bagian permintaan', exact: true }).getByRole('button', { name: 'Riwayat permintaan', exact: true }).click();
    await pemohon.page.getByRole('button', { name: 'Lihat ' + second, exact: true }).click();
    const detail = pemohon.page.getByRole('region', { name: 'Rincian permintaan', exact: true });
    await detail.getByText('Kemasan ini sedang tidak dijual', { exact: false }).waitFor();
    await pemohon.page.getByRole('button', { name: 'Lihat ' + first, exact: true }).click();
    await detail.getByText('Diterima untuk diproses', { exact: false }).first().waitFor(); await capture(pemohon.page, 'R3-buyer-history');
  });
  return { first, second };
}

try {
  await app.prepare(); const handle = app.getRequestHandler();
  frontend = track(http.createServer((req, res) => { void handle(req, res).catch(() => { res.statusCode = 500; res.end(); }); })); const frontendPort = await listen(frontend);
  edge = track(https.createServer({ pfx: Buffer.from(config.pfx, 'base64'), passphrase: config.password }, (req, res) => {
    if (req.headers.host !== origin.host || req.url.startsWith('/__fixture/')) { res.writeHead(403); res.end(); return; }
    const isApi = req.url.startsWith('/api/v1/') || req.url.startsWith('/auth/'); forward(req, res, isApi ? Number(api.port) : frontendPort, isApi);
  })); const tlsPort = await listen(edge);
  tunnel = track(http.createServer((_req, res) => { res.writeHead(403); res.end(); }));
  tunnel.on('connect', (req, socket, head) => {
    if (req.url !== origin.host + ':443') { socket.end('HTTP/1.1 403 Forbidden\r\n\r\n'); return; }
    const upstream = net.connect(tlsPort, '127.0.0.1', () => { socket.write('HTTP/1.1 200 Connection Established\r\n\r\n'); if (head.length) upstream.write(head); socket.pipe(upstream); upstream.pipe(socket); });
    sockets.add(upstream); upstream.on('close', () => sockets.delete(upstream)); upstream.on('error', () => socket.destroy()); socket.on('error', () => upstream.destroy()); socket.on('close', () => upstream.destroy());
  }); const proxyPort = await listen(tunnel);
  browser = await chromium.launch({ ...(channel === 'msedge' ? { channel: 'msedge' } : {}), headless: true, proxy: { server: 'http://127.0.0.1:' + proxyPort } });
  const stages = { 'order-to-receipt': [orderToReceipt, 'GOLDEN_JOURNEY_ORDER_TO_RECEIPT_PASS'],
    'invoice-to-payment': [invoiceToPayment, 'GOLDEN_JOURNEY_INVOICE_TO_PAYMENT_PASS'],
    'procurement-to-receipt': [procurementToReceipt, 'GOLDEN_JOURNEY_PROCUREMENT_TO_RECEIPT_PASS'],
    'branch-requests': [branchRequests, 'FUNCTION_GAP_BRANCH_REQUESTS_PASS'],
    'cart-and-cancellation': [cartAndCancellation, 'FUNCTION_GAP_CART_AND_CANCELLATION_PASS'],
    'second-order-store-rejection': [secondOrderAndStoreRejection, 'FUNCTION_GAP_SECOND_ORDER_STORE_REJECTION_PASS'] };
  const [run, marker] = stages[journey.stage] ?? assert.fail('Unknown golden journey stage ' + journey.stage);
  const result = await run();
  assert.deepEqual(errors, [], 'No page error in any persona');
  assert.equal(writes.filter(record => record.status !== null && record.status >= 500).length, 0, 'No server error on any command');
  await mkdir(directory, { recursive: true });
  await writeFile(resolve(directory, config.proofPrefix + '-proof.json'), JSON.stringify({ recordedUtc: new Date().toISOString(), buildId: config.buildId, stage: journey.stage, steps, captures, result,
    commands: writes.map(({ path, actor, status }) => ({ path, actor, status })), localSyntheticIdentityOnly: true }, null, 2));
  console.log(marker + ' ' + JSON.stringify({ steps: steps.length, ...result }));
} catch (error) {
  await mkdir(directory, { recursive: true });
  for (const [index, page] of pages.entries()) try { await page.screenshot({ path: resolve(directory, config.proofPrefix + '-failure-' + index + '.png'), fullPage: true }); } catch { /* keep the primary failure */ }
  await writeFile(resolve(directory, config.proofPrefix + '-failure-diagnostics.json'), JSON.stringify({ steps, errors,
    commands: writes.map(({ path, actor, status, response }) => ({ path, actor, status, code: response?.code ?? null })),
    statuses: responses.filter(row => row.status >= 400).map(({ path, status, value }) => ({ path, status, code: value?.code ?? null })) }, null, 2));
  console.error('GOLDEN_JOURNEY_FAILED_AFTER ' + JSON.stringify(steps.map(item => item.id)));
  console.error(error.stack ?? String(error)); process.exitCode = 1;
} finally {
  if (browser) await browser.close(); for (const socket of sockets) socket.destroy();
  for (const server of [tunnel, edge, frontend]) if (server) await new Promise(yes => server.close(yes)); await app.close();
}
