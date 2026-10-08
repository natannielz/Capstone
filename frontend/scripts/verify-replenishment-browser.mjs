// Genuine Next, protected API and Mongo owners. Only the committed response is deliberately lost.
import assert from 'node:assert/strict';
import {resolve} from 'node:path';

export async function verifyReplenishmentBrowser({config, maker, user, restartUser, origin, directory, procurementWrites}) {
  let {page, context} = maker;
  const path = '/inventory/replenishment', api = '/api/v1/inventory/replenishment', storagePrefix = 'unit-toko:replenishment-pending:v2:' + config.makerId + ':';
  const tabErrors = [];
  const openOtherTab = async () => {
    const other = await context.newPage(); other.setDefaultTimeout(15000); other.on('pageerror', error => tabErrors.push(error.message));
    await other.goto(origin.origin + path); await other.getByLabel('Gudang', {exact: true}).locator('option').nth(1).waitFor({state: 'attached'});
    return other;
  };
  const trace = [], reference = 'Reviewed synthetic demand and replenishment instruction', reason = 'Inspect actual warehouse purchase need';
  const observe = current => current.on('request', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname.startsWith(api)) trace.push({path: new URL(request.url()).pathname, key: request.headers()['idempotency-key'], body: request.postDataJSON()});
  });
  const query = async (current, actor, endpoint, body) => current.evaluate(async ({actor, endpoint, body}) => {
    const csrf = await fetch('/api/v1/identity/csrf', {credentials: 'same-origin', cache: 'no-store'}).then(response => response.json());
    const response = await fetch(endpoint, {method: 'POST', credentials: 'same-origin', cache: 'no-store', headers: {'Content-Type': 'application/json', 'X-CSRF-Token': csrf.requestToken, 'X-Expected-Actor-Id': actor}, body: JSON.stringify(body)});
    return {status: response.status, body: await response.json()};
  }, {actor, endpoint, body});
  observe(page); await page.goto(origin.origin + path);
  const warehouse = page.getByLabel('Gudang', {exact: true});
  await warehouse.locator('option').nth(1).waitFor({state: 'attached'});
  assert.equal(await warehouse.locator('option').count(), 2);
  await warehouse.selectOption(await warehouse.locator('option').nth(1).getAttribute('value'));
  const source = page.getByLabel('Sumber stok', {exact: true}); await source.locator('option[value="' + config.poolId + '"]').waitFor({state: 'attached'}); await source.selectOption(config.poolId);
  await page.getByLabel('Cari barang', {exact: true}).fill(config.skuCode); await page.getByRole('button', {name: 'Cari kemasan', exact: true}).click();
  const choice = page.getByRole('button', {name: new RegExp(config.skuCode.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))});
  assert.equal(await choice.getAttribute('aria-pressed'), 'false'); await choice.click(); assert.equal(await choice.getAttribute('aria-pressed'), 'true');
  for (const [label, value] of [['Pemakaian rata-rata per hari (kemasan)', '4'], ['Waktu tunggu (hari)', '2'], ['Stok pengaman (kemasan)', '3'], ['Batas peringatan (kemasan)', '2'], ['Minimum pembelian (kemasan)', '1'], ['Kelipatan pembelian (kemasan)', '2']]) {
    assert.equal(await page.getByLabel(label, {exact: true}).inputValue(), ''); await page.getByLabel(label, {exact: true}).fill(value);
  }
  await page.getByLabel('Referensi dasar angka', {exact: true}).fill(reference); await page.getByLabel('Alasan pemeriksaan', {exact: true}).fill(reason);
  let otherTab = await openOtherTab();
  assert.equal(await otherTab.getByLabel('Gudang', {exact: true}).isEnabled(), true);
  let committed;
  await page.route(origin.origin + api, async route => {
    const response = await route.fetch({timeout: 30000}); assert.equal(response.status(), 200); committed = await response.json(); await route.abort('failed');
  }, {times: 1});
  const started = performance.now(); await page.getByRole('button', {name: 'Hitung dan simpan hasil', exact: true}).click();
  await page.getByRole('alert').filter({hasText: 'Hasil belum terkonfirmasi'}).waitFor({timeout: 30000});
  assert.ok(performance.now() - started < 30000, 'The actual committed review must settle within the unchanged 30 second action ceiling');
  assert.ok(committed?.id); assert.equal(committed.suggestedPurchasePackages, '10'); assert.equal(committed.usableBeforeReservations, '1');
  const writes = trace.filter(row => row.path === api); assert.equal(writes.length, 1);
  const storageKey = storagePrefix + writes[0].key;
  const raw = await page.evaluate(key => localStorage.getItem(key), storageKey), pending = JSON.parse(raw);
  assert.deepEqual(Object.keys(pending).sort(), ['actorId', 'createdAt', 'key']); assert.equal(pending.actorId, config.makerId); assert.equal(pending.key, writes[0].key);
  for (const privateFact of [reference, reason, config.skuCode]) assert.equal(raw.includes(privateFact), false);
  assert.equal(await page.getByRole('button', {name: 'Hitung dan simpan hasil', exact: true}).isDisabled(), true);
  // The existing second tab observes the retained reference through a real storage event.
  await otherTab.getByRole('button', {name: 'Periksa hasil tersimpan', exact: true}).waitFor();
  assert.equal(await otherTab.getByLabel('Gudang', {exact: true}).isDisabled(), true);
  assert.equal(await otherTab.evaluate(key => localStorage.getItem(key), storageKey), raw);
  // A real browser process restarts with its persisted cookies/localStorage; sessionStorage is not transferred.
  const persisted = await context.storageState(); ({page, context} = await restartUser(config.makerToken, persisted)); observe(page);
  await page.goto(origin.origin + path); await page.getByRole('button', {name: 'Periksa hasil tersimpan', exact: true}).waitFor();
  otherTab = await openOtherTab();
  await otherTab.getByRole('button', {name: 'Periksa hasil tersimpan', exact: true}).waitFor();
  assert.equal(await page.evaluate(key => localStorage.getItem(key), storageKey), raw);
  await page.getByRole('button', {name: 'Periksa hasil tersimpan', exact: true}).click();
  const result = page.getByTestId('replenishment-result'); await result.waitFor({timeout: 30000});
  assert.equal(await page.evaluate(key => localStorage.getItem(key), storageKey), null); assert.equal(new URL(page.url()).searchParams.get('review'), committed.id);
  await otherTab.getByRole('button', {name: 'Periksa hasil tersimpan', exact: true}).waitFor({state: 'hidden'});
  assert.equal(await otherTab.getByLabel('Gudang', {exact: true}).isEnabled(), true);
  assert.equal(await otherTab.evaluate(key => localStorage.getItem(key), storageKey), null);
  await otherTab.close(); assert.deepEqual(tabErrors, []);
  const recoveries = trace.filter(row => row.path === api + '/operations/query'); assert.equal(recoveries.length, 1); assert.equal(recoveries[0].body.key, pending.key);
  assert.equal(trace.filter(row => row.path === api).length, 1);
  for (const [label, number] of [['Stok layak sebelum reservasi', '1'], ['Direservasi untuk pesanan', '0'], ['Komitmen tambahan di luar reservasi', '0'], ['Tersedia setelah komitmen', '1'], ['Pasokan terverifikasi dalam masa tunggu', '0'], ['Saran jumlah pembelian', '10']]) {
    assert.equal(await result.locator('dl > div').filter({has: page.getByText(label, {exact: true})}).locator('dd').innerText(), number + ' kemasan');
  }
  await result.getByText('Stok berada pada atau di bawah batas peringatan.', {exact: true}).waitFor();
  const recordedInputs = result.getByTestId('replenishment-recorded-inputs');
  for (const [label, amount] of [['Pemakaian rata-rata per hari', '4 kemasan per hari'], ['Waktu tunggu', '2 hari'], ['Stok pengaman', '3 kemasan'],
    ['Batas peringatan', '2 kemasan'], ['Minimum pembelian', '1 kemasan'], ['Kelipatan pembelian', '2 kemasan']]) {
    assert.equal(await recordedInputs.locator('div').filter({has: page.getByText(label, {exact: true})}).locator('dd').innerText(), amount);
  }
  const savedText = await result.innerText(); await page.reload(); await result.waitFor(); assert.equal(await result.innerText(), savedText);
  // Find a recorded review through the actual scoped history, without relying on a saved URL.
  await page.goto(origin.origin + path);
  const historyWarehouse = page.getByLabel('Gudang', {exact: true});
  await historyWarehouse.locator('option').nth(1).waitFor({state: 'attached'});
  await historyWarehouse.selectOption(await historyWarehouse.locator('option').nth(1).getAttribute('value'));
  const history = page.getByRole('region', {name: 'Riwayat pemeriksaan kebutuhan', exact: true});
  await history.getByRole('button', {name: 'Muat riwayat terbaru', exact: true}).click();
  const entry = history.getByRole('button').filter({hasText: config.skuCode});
  await entry.waitFor(); assert.equal(await entry.count(), 1);
  await entry.click(); await result.waitFor(); assert.equal(await result.innerText(), savedText);
  assert.equal(new URL(page.url()).searchParams.get('review'), committed.id);
  assert.equal(trace.filter(row => row.path === api).length, 1);
  const original = await query(page, config.makerId, api + '/' + committed.id + '/query', {purpose: 'inventory-replenishment'});
  assert.equal(original.status, 200); assert.deepEqual(original.body, committed);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({path: resolve(directory, 'replenishment-mobile.png'), fullPage: true});
  await page.setViewportSize({width: 1440, height: 1000}); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({path: resolve(directory, 'replenishment-desktop.png'), fullPage: true});
  const outsider = await user(config.outsiderToken); await outsider.page.goto(origin.origin + '/__replenishment_scope_probe');
  const denied = await query(outsider.page, config.outsiderId, api + '/' + committed.id + '/query', {purpose: 'inventory-replenishment'}); assert.equal(denied.status, 403);
  for (const key of ['inputs', 'supplies', 'usableBeforeReservations', 'suggestedPurchasePackages']) assert.equal(Object.hasOwn(denied.body, key), false);
  await page.setViewportSize({width: 360, height: 800}); await result.getByRole('link', {name: 'Tinjau sebagai permintaan pengadaan', exact: true}).click();
  await page.getByRole('button', {name: 'Gunakan saran dalam formulir', exact: true}).click();
  for (const label of ['Jalur pengadaan', 'Tanggal kebutuhan', 'Alasan permintaan']) assert.equal(await page.getByLabel(label, {exact: true}).inputValue(), '');
  assert.equal(await page.getByLabel('Referensi pendukung (opsional)', {exact: true}).inputValue(), 'replenishment:' + committed.id);
  await page.getByLabel('Jalur pengadaan', {exact: true}).selectOption('omi_regular');
  await page.getByLabel('Jenis kebutuhan', {exact: true}).selectOption('stock_need');
  assert.equal(await page.getByLabel('Referensi pendukung (opsional)', {exact: true}).inputValue(), 'replenishment:' + committed.id);
  await page.getByLabel('Jenis kebutuhan', {exact: true}).selectOption('incoming_notice');
  assert.equal(await page.getByLabel('Referensi pemberitahuan OMI', {exact: true}).inputValue(), '');
  await page.getByLabel('Referensi pemberitahuan OMI', {exact: true}).fill('Synthetic operator-entered arrival notice');
  await page.getByLabel('Jenis kebutuhan', {exact: true}).selectOption('stock_need');
  assert.equal(await page.getByLabel('Referensi pendukung (opsional)', {exact: true}).inputValue(), 'replenishment:' + committed.id);
  await page.getByLabel('Jalur pengadaan', {exact: true}).selectOption('ddo'); await page.getByLabel('Tanggal kebutuhan', {exact: true}).fill(config.businessDate);
  assert.equal(await page.getByLabel('Referensi pendukung (opsional)', {exact: true}).inputValue(), 'replenishment:' + committed.id);
  await page.getByLabel('Alasan permintaan', {exact: true}).fill('Actual replenishment advice reviewed by synthetic procurement preparer');
  await page.getByRole('button', {name: 'Periksa rincian permintaan', exact: true}).click();
  const review = page.getByRole('region', {name: 'Rincian permintaan', exact: true}); await review.waitFor(); assert.ok((await review.innerText()).includes('10 kemasan'));
  await review.getByText('replenishment:' + committed.id, {exact: true}).waitFor();
  assert.equal(await review.getByRole('button', {name: 'Kirim permintaan', exact: true}).isDisabled(), true);
  await review.getByRole('checkbox', {name: 'Saya telah memeriksa jalur, tanggal, kemasan dan jumlah permintaan.', exact: true}).check();
  await review.getByRole('button', {name: 'Kirim permintaan', exact: true}).click(); await page.getByRole('heading', {name: 'Menunggu pemeriksaan', exact: true}).waitFor({timeout: 30000});
  assert.equal(procurementWrites.length, 1); assert.equal(procurementWrites[0].status, 200); assert.equal(procurementWrites[0].body.input.externalReference, 'replenishment:' + committed.id);
  assert.equal(procurementWrites[0].body.input.lines.length, 1); assert.equal(procurementWrites[0].body.input.lines[0].packages, 10);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({path: resolve(directory, 'replenishment-procurement-mobile.png'), fullPage: true});
  console.log('REAL_REPLENISHMENT_BROWSER_PASS ' + JSON.stringify({actualStock: true, savedReviews: 1, replyLoss: true, browserProcessRestart: true, originalKeyRecovery: true, twoTabStorageEvents: true, refreshSameObservation: true, scopedHistory: true, scopedRead: true, explicitProcurementRequest: true, mobile: true, desktop: true}));
}
