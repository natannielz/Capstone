// Actual shared HTTPS/Next/API/Mongo host. People and business inputs are explicitly synthetic.
import assert from 'node:assert/strict';
import { resolve } from 'node:path';
const recordNo = (prefix, id) => prefix + '-' + String(id).padStart(6, '0');

export async function verifyProcurementRequestsBrowser({ config, maker, user, origin, directory, procurementWrites, procurementRecoveries, dropNext, dropped }) {
  const { page, context } = maker, path = '/procurement/requests', api = '/api/v1/procurement/requests';
  const location = { organizationId: config.organizationId, shopId: config.shopId, warehouseId: config.warehouseId, channelId: config.channelId };
  const completed = () => procurementWrites.filter(write => write.status === 200), canonical = Object.values(location).join(':');
  const reason = 'Kebutuhan sintetis dengan kemasan katalog untuk pemeriksaan independen', specification = 'Spesifikasi sintetis yang telah diperiksa';
  assert.notEqual(config.makerId, config.checkerId);
  const references = current => current.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  const query = (current, actorId, route, body, options = {}) => current.evaluate(async ({ actorId, route, body, options }) => {
    const csrf = await fetch('/api/v1/identity/csrf', { credentials: 'same-origin', cache: 'no-store', redirect: 'error' });
    if (!csrf.ok) throw new Error('Actual request token unavailable'); const token = await csrf.json();
    const headers = { 'Content-Type': 'application/json', 'X-CSRF-Token': token.requestToken, 'X-Expected-Actor-Id': actorId, ...options.headers };
    for (const name of options.omit ?? []) delete headers[name];
    const response = await fetch(route, { method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', headers, body: JSON.stringify(body) });
    const text = await response.text(); return { status: response.status, body: text ? JSON.parse(text) : null };
  }, { actorId, route, body, options });
  async function choose(current) {
    const select = current.getByLabel('Organisasi / toko / gudang / kanal', { exact: true });
    await select.getByRole('option', { name: [config.organizationName, config.shopName, config.warehouseName, config.channelName].join(' · '), exact: true }).waitFor({ state: 'attached' });
    await select.selectOption(canonical); await current.getByRole('region', { name: 'Rincian pengadaan', exact: true }).waitFor();
  }
  async function pending(current, write, operation) {
    const values = await references(current); assert.equal(values.length, 1);
    assert.deepEqual(Object.keys(values[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.equal(values[0].key, write.key); assert.equal(values[0].actorId, write.actor); assert.equal(values[0].operation, operation);
    for (const secret of [config.skuCode, write.body.reason ?? write.body.input.reason, write.body.binding]) assert.ok(!JSON.stringify(values).includes(secret));
    assert.equal(await current.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0); return values;
  }
  async function facts(region) {
    const text = await region.innerText();
    for (const value of [config.organizationCode, config.organizationName, config.shopCode, config.shopName, config.warehouseCode, config.warehouseName,
      config.channelCode, config.channelName, config.businessDate, 'DDO', 'Permintaan pembelian', config.skuCode, config.familyName, config.packageLabel,
      config.packageUnits + ' piece per kemasan', '2 kemasan · Total 24 piece', reason, specification]) assert.ok(text.includes(value), value);
    assert.ok(text.includes('Pemasok dan harga belum ditetapkan; pembayaran dan penerimaan barang belum dicatat.'));
  }
  await page.goto(origin.origin + path); await choose(page); await page.getByText('Belum ada permintaan pengadaan.', { exact: true }).waitFor();
  const readBody = { purpose: 'procurement-request-review', location };
  for (const [body, options, expected] of [
    [readBody, { omit: ['X-CSRF-Token'] }, 403], [readBody, { omit: ['X-Expected-Actor-Id'] }, 400],
    [{ ...readBody, purpose: 'accounting-book-review' }, {}, 400], [{ ...readBody, location: { ...location, warehouseId: Number(location.warehouseId) } }, {}, 400],
    [{ ...readBody, unrecognized: true }, {}, 400],
  ]) assert.equal((await query(page, config.makerId, api + '/query', body, options)).status, expected);
  await page.getByRole('button', { name: 'Buat permintaan', exact: true }).click();
  for (const label of ['Jalur pengadaan', 'Tanggal kebutuhan', 'Alasan permintaan']) assert.equal(await page.getByLabel(label, { exact: true }).inputValue(), '');
  await page.getByLabel('Jalur pengadaan', { exact: true }).selectOption('ddo'); await page.getByLabel('Tanggal kebutuhan', { exact: true }).fill(config.businessDate);
  await page.getByLabel('Cari barang atau kemasan', { exact: true }).fill(config.skuCode); await page.getByRole('button', { name: 'Cari kemasan', exact: true }).click();
  await page.getByText('Kode SKU sama persis. Periksa isi kemasan sebelum memilih.', { exact: true }).waitFor();
  assert.equal(await page.getByRole('region', { name: 'Periksa kemasan pilihan', exact: true }).count(), 0);
  // Both genuine definitions share the same family: a spelling hint must leave the choice and quantity to the person.
  await page.getByLabel('Cari barang atau kemasan', { exact: true }).fill('Penzil pengadaan'); await page.getByRole('button', { name: 'Cari kemasan', exact: true }).click();
  await page.getByText('Saran nama mirip (1 perubahan huruf). Belum menjadi pilihan Anda.', { exact: true }).first().waitFor();
  assert.equal(await page.getByText('Saran nama mirip (1 perubahan huruf). Belum menjadi pilihan Anda.', { exact: true }).count(), 2);
  assert.equal(await page.getByRole('region', { name: 'Periksa kemasan pilihan', exact: true }).count(), 0); assert.equal(procurementWrites.length, 0);
  await page.screenshot({ path: resolve(directory, 'procurement-matching-mobile.png'), fullPage: true });
  await page.getByRole('button', { name: new RegExp(config.skuCode + ' · ' + config.packageLabel) }).click();
  const chosen = page.getByRole('region', { name: 'Periksa kemasan pilihan', exact: true }); await chosen.waitFor();
  assert.equal(await chosen.getByLabel('Jumlah kemasan', { exact: true }).inputValue(), '');
  await chosen.getByLabel('Jumlah kemasan', { exact: true }).fill('2'); await chosen.getByLabel('Spesifikasi (opsional)', { exact: true }).fill(specification);
  await chosen.getByRole('button', { name: 'Tambahkan kemasan', exact: true }).click();
  await page.getByLabel('Alasan permintaan', { exact: true }).fill(reason); await page.getByRole('button', { name: 'Periksa rincian permintaan', exact: true }).click();
  const review = page.getByRole('region', { name: 'Rincian permintaan', exact: true }); await review.waitFor(); await facts(review);
  assert.equal(await review.getByRole('heading', { name: 'Rincian yang akan dikirim', exact: true }).evaluate(element => element === document.activeElement), true);
  assert.equal(await review.getByRole('textbox').count(), 0); assert.equal(await review.getByRole('button', { name: 'Kirim permintaan', exact: true }).isDisabled(), true);
  await review.getByRole('checkbox', { name: 'Saya telah memeriksa jalur, tanggal, kemasan dan jumlah permintaan.', exact: true }).check();
  await context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await review.getByRole('button', { name: 'Kirim permintaan', exact: true }).isDisabled(), true); assert.equal(procurementWrites.length, 0);
  await context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'procurement-request-mobile.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'procurement-request-desktop.png'), fullPage: true });
  dropNext(); await review.getByRole('button', { name: 'Kirim permintaan', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(completed().length, 1); const submitted = completed()[0]; assert.equal(submitted.path, api + '/submissions'); assert.equal(submitted.actor, config.makerId);
  assert.deepEqual(submitted.body.input, { location, route: 'ddo', kind: 'purchase_request', businessDate: config.businessDate, externalReference: null, reason,
    lines: [{ skuId: config.skuId, expectedSkuRevision: config.skuRevision, definitionId: config.definitionId, definitionVersion: config.definitionVersion, packages: 2, specification }] });
  const saved = await pending(page, submitted, 'procurement.request.submit');
  await context.addCookies([{ name: '__Host-ut_session', value: config.checkerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('heading', { name: /^(Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor(); assert.deepEqual(await references(page), saved);
  await context.addCookies([{ name: '__Host-ut_session', value: config.makerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden' });
  assert.equal(procurementWrites.length, 1); assert.equal((await references(page)).length, 0); await choose(page);
  assert.equal((await query(page, config.makerId, api + '/preview/query', { purpose: 'procurement-request-review', input: { ...submitted.body.input, supplierCost: '0.00' } })).status, 400);
  const history = await query(page, config.makerId, api + '/query', readBody); assert.equal(history.status, 200); assert.equal(history.body.items.length, 1); assert.equal(history.body.items[0].state, 'submitted');
  const requestId = history.body.items[0].id, row = current => current.getByRole('button', { name: new RegExp('DDO.*Permintaan ' + recordNo('PP', requestId) + '$') });
  await row(page).click(); await page.getByRole('heading', { name: 'Menunggu pemeriksaan', exact: true }).waitFor(); assert.equal(await page.getByRole('button', { name: 'Tinjau persetujuan', exact: true }).count(), 0);
  const checker = await user(config.checkerToken); await checker.page.goto(origin.origin + path); await choose(checker.page); await row(checker.page).click();
  await checker.page.getByLabel('Alasan keputusan', { exact: true }).fill('Kemasan dan kebutuhan sintetis diperiksa secara independen'); await checker.page.getByRole('button', { name: 'Tinjau persetujuan', exact: true }).click();
  const decision = checker.page.getByRole('region', { name: 'Rincian keputusan', exact: true }); await decision.waitFor(); await facts(decision);
  assert.equal(await decision.getByRole('heading', { name: 'Pastikan persetujuan permintaan', exact: true }).evaluate(element => element === document.activeElement), true);
  assert.equal(await decision.getByRole('button', { name: 'Setujui permintaan', exact: true }).isDisabled(), true);
  await decision.getByRole('checkbox', { name: 'Saya telah memeriksa permintaan dan siap mencatat keputusan ini.', exact: true }).check();
  const decisionPath = api + '/' + requestId + '/decisions';
  const preview = await query(checker.page, config.checkerId, api + '/' + requestId + '/decision-preview/query', { purpose: 'procurement-request-review', decision: 'approve' });
  assert.equal(preview.status, 200); assert.equal(preview.body.request.id, requestId);
  const rejectedKey = 'browser-procurement-missing-precondition';
  assert.equal((await query(checker.page, config.checkerId, decisionPath, { decision: 'approve', binding: preview.body.binding, reason: 'Synthetic missing precondition probe' }, { headers: { 'Idempotency-Key': rejectedKey } })).status, 400);
  assert.equal(completed().length, 1); const unchanged = await query(checker.page, config.checkerId, api + '/' + requestId + '/query', { purpose: 'procurement-request-review' });
  assert.equal(unchanged.status, 200); assert.equal(unchanged.body.state, 'submitted');
  dropNext(); await decision.getByRole('button', { name: 'Setujui permintaan', exact: true }).click(); await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(completed().length, 2); const approved = completed()[1]; assert.equal(approved.path, decisionPath); assert.equal(approved.actor, config.checkerId); assert.equal(approved.version, '"1"');
  await pending(checker.page, approved, 'procurement.request.approve'); await checker.page.reload(); await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden' });
  assert.equal(completed().length, 2); assert.equal(dropped(), 2); assert.equal((await references(checker.page)).length, 0);
  await choose(checker.page); await row(checker.page).click(); await checker.page.getByRole('heading', { name: 'Disetujui', exact: true }).waitFor();
  await facts(checker.page.getByRole('region', { name: 'Rincian pengadaan', exact: true }));
  assert.equal(await checker.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await checker.page.screenshot({ path: resolve(directory, 'procurement-approved-mobile.png'), fullPage: true });
  const approvedRead = await query(checker.page, config.checkerId, api + '/' + requestId + '/query', { purpose: 'procurement-request-review' });
  assert.equal(approvedRead.status, 200); assert.equal(approvedRead.body.state, 'approved');
  assert.deepEqual(approvedRead.body.downstream, { supplier: 'unresolved', commercial: 'unpriced', payment: 'not_recorded', receipt: 'not_recorded' });
  const reader = await user(config.readerToken); await reader.page.goto(origin.origin + path); await choose(reader.page); await row(reader.page).click();
  await reader.page.getByRole('heading', { name: 'Disetujui', exact: true }).waitFor(); assert.equal(await reader.page.getByRole('button', { name: 'Buat permintaan', exact: true }).count(), 0);
  assert.equal(await reader.page.getByRole('button', { name: 'Tinjau persetujuan', exact: true }).count(), 0);
  assert.equal(procurementRecoveries.length, 3); assert.equal(procurementRecoveries[0].status, 409); assert.equal(procurementRecoveries[1].status, 200);
  assert.equal(procurementRecoveries[1].body.key, submitted.key); assert.equal(procurementRecoveries[2].status, 200); assert.equal(procurementRecoveries[2].body.key, approved.key);
  const wrong = await query(checker.page, config.checkerId, api + '/operations/query', { purpose: 'operation-recovery', operation: 'procurement.request.approve', key: submitted.key });
  assert.equal(wrong.status, 200); assert.deepEqual(wrong.body, { status: 'not_confirmed', result: null });
  const outsider = await user(config.outsiderToken); await outsider.page.goto(origin.origin + '/__procurement_scope_probe');
  const foreign = await query(outsider.page, config.outsiderId, api + '/' + requestId + '/query', { purpose: 'procurement-request-review' });
  assert.equal(foreign.status, 403); assert.equal(foreign.body.code, 'scope_denied');
  for (const field of ['input', 'source', 'lines', 'decision', 'downstream']) assert.equal(Object.hasOwn(foreign.body, field), false);
  assert.equal(procurementWrites.length, 3); assert.equal(completed().length, 2); const rejected = procurementWrites.filter(write => write.status !== 200); assert.equal(rejected.length, 1);
  assert.equal(rejected[0].path, decisionPath); assert.equal(rejected[0].key, rejectedKey); assert.equal(rejected[0].status, 400); assert.equal(rejected[0].version, undefined);
  console.log('REAL_PROCUREMENT_REQUEST_BROWSER_PASS ' + JSON.stringify({ writes: completed().length, rejectedCommands: rejected.length, requestGuards: true,
    originalReplyLosses: dropped(), exactActorRecovery: true, independentApproval: true, realCatalogPackages: true, explicitAmbiguousMatchChoice: true, downstreamUnresolved: true, offlineUnsent: true, mobile: true, desktop: true }));
}
