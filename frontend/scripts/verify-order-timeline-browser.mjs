import assert from 'node:assert/strict';
import {resolve} from 'node:path';
const recordNo = (prefix, id) => prefix + '-' + String(id).padStart(6, '0');
// Order numbers as the screens show them (src/lib/document-numbers.ts).
const orderNumber = id => 'PSN-' + String(id).padStart(6, '0');

export async function verifyOrderTimelineBrowser({config, buyer, user, origin, directory, restartUser}) {
  const errors = [], operationalWrites = [], observedReads = [];
  const timeline = page => page.getByRole('region', {name: 'Riwayat pemenuhan dan penyelesaian', exact: true});
  function observe(page) {
    page.setDefaultTimeout(30000); page.setDefaultNavigationTimeout(30000);
    page.on('pageerror', error => errors.push(error.message));
    page.on('request', request => {
      const url = new URL(request.url());
      if (url.pathname.startsWith('/api/v1/') && request.headers()['idempotency-key']) operationalWrites.push(url.pathname);
      if (url.pathname.includes('/timeline/')) observedReads.push({path: url.pathname, actor: request.headers()['x-expected-actor-id'], body: request.postDataJSON()});
    });
  }
  async function select(page, order) {
    await page.getByRole('button', {name: 'Lihat rincian pesanan ' + orderNumber(order), exact: true}).click();
    await timeline(page).waitFor();
    for (const title of ['Pengiriman', 'Invoice', 'Pengajuan pembayaran saya', 'Kasus, retur, dan refund'])
      await timeline(page).getByRole('button', {name: 'Muat ' + title.toLowerCase() + ' terbaru', exact: true}).click({trial: true});
  }
  async function shot(page, suffix) {
    for (const width of [1440, 360]) {
      await page.setViewportSize({width, height: 1000});
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
      await page.screenshot({path: resolve(directory, config.proofPrefix + '-' + suffix + '-' + width + '.png'), fullPage: true});
    }
    await page.setViewportSize({width: 1440, height: 1000});
  }
  const direct = (page, actor, path, body = {purpose: 'order-timeline', beforeId: null, limit: 25}) => page.evaluate(async ({actor, path, body}) => {
    const csrfResponse = await fetch('/api/v1/identity/csrf', {credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: AbortSignal.timeout(30000)});
    if (!csrfResponse.ok) throw new Error('Actual CSRF unavailable');
    const csrf = await csrfResponse.json();
    const response = await fetch(path, {method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: AbortSignal.timeout(30000),
      headers: {'Content-Type': 'application/json', 'X-CSRF-Token': csrf.requestToken, 'X-Expected-Actor-Id': actor},
      body: JSON.stringify(body)});
    return {status: response.status, body: await response.json()};
  }, {actor, path, body});
  let current = buyer; observe(current.page);
  await current.page.goto(origin.origin + '/ordering/orders');
  await select(current.page, config.orderA);
  const panel = timeline(current.page);
  await panel.getByText('Invoice gabungan untuk 2 pesanan. Catatan pembayarannya berlaku pada invoice ini.', {exact: true}).waitFor();
  assert.equal(await panel.getByRole('button', {name: 'Periksa kasus ' + recordNo('KLH', config.refundCaseId), exact: true}).count(), 0);
  await panel.getByRole('button', {name: 'Periksa kasus ' + recordNo('KLH', config.creditCaseId), exact: true}).click();
  await panel.getByText(/Penyesuaian kredit disetujui/).waitFor();
  await panel.getByRole('button', {name: 'Periksa pembayaran ' + config.declarationId, exact: true}).click();
  await panel.getByText(/Penerimaan bank diverifikasi/).waitFor();
  await panel.getByText(/Pembayaran dialokasikan ke invoice/).waitFor();
  await panel.getByText(/Alokasi pembayaran dibalik ke invoice/).waitFor();
  const otherOrderCase = await direct(current.page, config.buyerId,
    '/api/v1/ordering/orders/' + config.orderA + '/timeline/cases/' + config.refundCaseId + '/query', {purpose: 'order-timeline'});
  assert.equal(otherOrderCase.status, 403); assert.equal('returns' in otherOrderCase.body, false);
  await shot(current.page, 'credit-and-payment');

  let dropped = false;
  const paymentPath = '/api/v1/ordering/orders/' + config.orderA + '/timeline/payments/' + config.declarationId + '/query';
  await current.context.route('**' + paymentPath, async route => {
    const response = await route.fetch({timeout: 30000});
    assert.equal(response.status(), 200); await response.json();
    if (!dropped) {dropped = true; await route.abort('failed');} else await route.fulfill({response});
  });
  await panel.getByRole('button', {name: 'Periksa pembayaran ' + config.declarationId, exact: true}).click();
  await panel.getByRole('alert').getByText('Riwayat belum dapat dimuat. Periksa koneksi, lalu coba kembali.', {exact: true}).waitFor();
  assert.equal(await panel.getByText(/Penerimaan bank diverifikasi/).count(), 0);
  await panel.getByRole('button', {name: 'Periksa pembayaran ' + config.declarationId, exact: true}).click();
  await panel.getByText(/Penerimaan bank diverifikasi/).waitFor(); assert.equal(dropped, true);
  await current.context.unroute('**' + paymentPath);

  const state = await current.context.storageState(); await current.context.close();
  current = await restartUser(config.financeToken, state); observe(current.page);
  await current.page.goto(origin.origin + '/ordering/orders'); await select(current.page, config.orderB);
  const second = timeline(current.page);
  assert.equal(await second.getByRole('button', {name: 'Periksa kasus ' + recordNo('KLH', config.creditCaseId), exact: true}).count(), 0);
  await second.getByRole('button', {name: 'Periksa kasus ' + recordNo('KLH', config.refundCaseId), exact: true}).click();
  await second.getByText(/Refund disetujui/).waitFor();
  await second.getByText(/Pembayaran refund dicatat/).waitFor();
  await second.getByText(/Penyelesaian refund diverifikasi bank/).waitFor();
  await shot(current.page, 'refund-after-restart');
  const denied = await user(config.deniedToken); observe(denied.page); await denied.page.goto(origin.origin + '/ordering/orders');
  for (const kind of ['shipments', 'invoices', 'payments', 'cases']) {
    const reply = await direct(denied.page, config.deniedId, '/api/v1/ordering/orders/' + config.orderA + '/timeline/' + kind + '/query');
    assert.equal(reply.status, 403); assert.equal(Array.isArray(reply.body.items), false);
  }
  for (const [kind, id] of [['payments', config.declarationId], ['cases', config.creditCaseId]]) {
    const reply = await direct(denied.page, config.deniedId,
      '/api/v1/ordering/orders/' + config.orderA + '/timeline/' + kind + '/' + id + '/query', {purpose: 'order-timeline'});
    assert.equal(reply.status, 403); assert.equal('invoiceEvents' in reply.body || 'returns' in reply.body, false);
  }
  const deniedState = await denied.context.storageState(); await current.context.clearCookies(); await current.context.addCookies(deniedState.cookies);
  await second.getByRole('button', {name: 'Perbarui seluruh riwayat', exact: true}).click();
  await timeline(current.page).waitFor({state: 'hidden'});
  assert.equal(await current.page.getByText(/Penyelesaian refund diverifikasi bank/).count(), 0);
  assert.ok(observedReads.some(row => row.path === paymentPath && row.actor === config.buyerId));
  assert.deepEqual(operationalWrites, []); assert.deepEqual(errors, []);
  await denied.context.close(); await current.context.close();
  console.log('REAL_ORDER_TIMELINE_BROWSER_PASS groupedInvoice=true exactCaseOrder=true verifiedFinancialOwners=true droppedReadRetry=true browserRestart=true staleActorCleared=true operationalWrites=0');
}
