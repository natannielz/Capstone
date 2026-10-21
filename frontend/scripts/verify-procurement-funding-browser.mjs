// Actual production Next/HTTPS/Kestrel/Mongo and genuine approved PO history; all policy and identity inputs are synthetic local evidence.
import assert from 'node:assert/strict';
import { resolve } from 'node:path';

export async function verifyProcurementFundingBrowser({ config, maker, user, origin, directory, fundingWrites, fundingRecoveries, dropNext, dropped }) {
  const { page, context } = maker, path = '/procurement/funding', api = '/api/v1/procurement/funding';
  const location = { organizationId: config.organizationId, shopId: config.shopId, warehouseId: config.warehouseId, channelId: config.channelId };
  const canonical = Object.values(location).join(':'), completed = () => fundingWrites.filter(write => write.status === 200);
  const consent = 'Saya telah memeriksa pesanan, kebijakan, nominal dan tanggal kebutuhan.';
  const reason = 'Permintaan sintetis uang muka sebagian sesuai pesanan dan kebijakan keuangan yang ditinjau';
  const input = { location, orderId: config.orderId, orderVersion: '1', orderApprovalId: config.orderApprovalId,
    policyId: config.policyId, policyRevision: config.policyRevision, amount: '10000.00', businessDate: config.businessDate, neededBy: config.neededBy };
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
    await current.getByRole('heading', { name: 'Otorisasi uang muka', exact: true }).waitFor();
    const select = current.getByLabel('Organisasi / toko / gudang / kanal', { exact: true });
    await select.getByRole('option', { name: [config.organizationName, config.shopName, config.warehouseName, config.channelName].join(' · '), exact: true }).waitFor({ state: 'attached' });
    await select.selectOption(canonical); await current.getByRole('region', { name: 'Rincian otorisasi uang muka', exact: true }).waitFor();
  }
  async function pending(current, write, operation) {
    const saved = await references(current); assert.equal(saved.length, 1); assert.deepEqual(Object.keys(saved[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.equal(saved[0].actorId, write.actor); assert.equal(saved[0].key, write.key); assert.equal(saved[0].operation, operation);
    for (const value of [reason, write.body.reason, config.specification, '10000.00']) assert.ok(!JSON.stringify(saved).includes(value));
    assert.equal(await current.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0); return saved;
  }
  async function recover(current) {
    await current.reload(); await current.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
    await current.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden' }); assert.equal((await references(current)).length, 0); await choose(current);
  }
  async function facts(region) {
    const text = await region.innerText();
    for (const value of [config.organizationName, config.shopName, config.warehouseName, config.channelName, config.supplierName, config.supplierSubject,
      config.skuCode, config.familyName, config.packageLabel, config.specification, '12 piece per kemasan', '6 kemasan', '72 piece', '000001', '000012', 'SYNTHETIC-QUOTE-0001',
      'Rp10.000', 'Rp60.000', 'Rp3.300', 'Rp700', 'Rp64.000', 'Rp20.000', 'Explicit synthetic deposit; balance on receipt', config.neededBy,
      'SYNTHETIC-LOCAL-ADVANCE-PROCEDURE', 'SYNTHETIC-LOCAL-POLICY-APPROVAL', 'SYNTHETIC-LOCAL-FINANCE-OWNER', 'Otorisasi yang belum dicairkan tetap mengurangi sisa batas.',
      'Transfer, ketersediaan kas, penerimaan barang dan jurnal pembayaran belum dicatat']) assert.ok(text.includes(value), value);
  }
  async function reviewReady(region, heading, button) {
    await region.getByRole('heading', { name: heading, exact: true }).waitFor();
    assert.equal(await region.getByRole('heading', { name: heading, exact: true }).evaluate(element => element === document.activeElement), true);
    assert.equal(await region.getByRole('textbox').count(), 0); assert.equal(await region.getByRole('button', { name: button, exact: true }).isDisabled(), true);
    await region.getByRole('checkbox', { name: consent, exact: true }).check();
  }
  assert.notEqual(config.makerId, config.checkerId); await page.goto(origin.origin + path); await choose(page); await page.getByText('Belum ada permintaan uang muka.', { exact: true }).waitFor();
  const readBody = { purpose: 'funding-review', location };
  for (const [body, options, expected] of [[readBody, { omit: ['X-CSRF-Token'] }, 403], [readBody, { omit: ['X-Expected-Actor-Id'] }, 400],
    [{ ...readBody, purpose: 'purchase-order-review' }, {}, 400], [{ ...readBody, location: { ...location, shopId: Number(location.shopId) } }, {}, 400], [{ ...readBody, unknown: true }, {}, 400]])
    assert.equal((await query(page, config.makerId, api + '/requests/query', body, options)).status, expected);
  await page.getByRole('button', { name: 'Ajukan uang muka', exact: true }).click(); await page.getByRole('heading', { name: 'Ajukan otorisasi uang muka', exact: true }).waitFor();
  const order = page.getByLabel('Pesanan pembelian yang disetujui', { exact: true }); await order.selectOption(config.orderId);
  const policy = page.getByLabel('Kebijakan keuangan yang disetujui', { exact: true }); await policy.selectOption(config.policyId);
  for (const label of ['Nominal uang muka yang diminta (IDR)', 'Tanggal permintaan', 'Dibutuhkan pada', 'Alasan permintaan']) assert.equal(await page.getByLabel(label, { exact: true }).inputValue(), '');
  await page.getByLabel('Nominal uang muka yang diminta (IDR)', { exact: true }).fill(input.amount);
  await page.getByLabel('Tanggal permintaan', { exact: true }).fill(input.businessDate); await page.getByLabel('Dibutuhkan pada', { exact: true }).fill(input.neededBy);
  await page.getByLabel('Alasan permintaan', { exact: true }).fill(reason); await page.getByRole('button', { name: 'Periksa permintaan uang muka', exact: true }).click();
  const review = page.getByRole('region', { name: 'Rincian permintaan uang muka', exact: true }); await reviewReady(review, 'Pastikan permintaan uang muka', 'Kirim permintaan uang muka'); await facts(review);
  const noAmount = { ...input }; delete noAmount.amount;
  for (const invalid of [noAmount, { ...input, amount: 10000 }, { ...input, orderId: Number(input.orderId) }, { ...input, policyRevision: 1 }, { ...input, route: 'smart' }])
    assert.equal((await query(page, config.makerId, api + '/preview/query', { purpose: 'funding-review', input: invalid })).status, 400);
  const aged = await user(config.agedToken); await aged.page.goto(origin.origin + '/__funding_aged_factor_probe');
  const expired = await query(aged.page, config.makerId, api + '/preview/query', { purpose: 'funding-review', input }); assert.equal(expired.status, 403); assert.equal(expired.body.code, 'step_up_required');
  await context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await review.getByRole('button', { name: 'Kirim permintaan uang muka', exact: true }).isDisabled(), true); assert.equal(fundingWrites.length, 0);
  await context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'procurement-funding-request-mobile.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'procurement-funding-request-desktop.png'), fullPage: true });
  dropNext(); await review.getByRole('button', { name: 'Kirim permintaan uang muka', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(completed().length, 1); const requested = completed()[0]; assert.equal(requested.path, api + '/requests'); assert.equal(requested.actor, config.makerId); assert.deepEqual(requested.body.input, input);
  const saved = await pending(page, requested, 'procurement.funding.request');
  await context.addCookies([{ name: '__Host-ut_session', value: config.checkerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('heading', { name: /^(Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor(); assert.deepEqual(await references(page), saved);
  await context.addCookies([{ name: '__Host-ut_session', value: config.makerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await recover(page); assert.equal(fundingWrites.length, 1);
  const history = await query(page, config.makerId, api + '/requests/query', readBody); assert.equal(history.status, 200); assert.equal(history.body.items.length, 1);
  const request = history.body.items[0], requestPath = api + '/requests/' + request.id; assert.equal(request.state, 'requested'); assert.equal(request.decision, null);
  assert.equal(request.capacity.authorized, '0.00'); assert.equal(request.capacity.remaining, '20000.00');
  const row = current => current.getByRole('button', { name: new RegExp('Permintaan ' + request.id + '$') });
  await row(page).click(); await page.getByRole('heading', { name: 'Menunggu pemeriksaan', exact: true }).waitFor(); assert.equal(await page.getByRole('button', { name: 'Tinjau persetujuan uang muka', exact: true }).count(), 0);
  const checker = await user(config.checkerToken); await checker.page.goto(origin.origin + path); await choose(checker.page); await row(checker.page).click();
  await checker.page.getByRole('heading', { name: 'Menunggu pemeriksaan', exact: true }).waitFor();
  await checker.page.getByLabel('Alasan keputusan', { exact: true }).fill('Pemeriksaan sintetis independen atas uang muka pesanan, kebijakan dan sisa batas');
  await checker.page.getByRole('button', { name: 'Tinjau persetujuan uang muka', exact: true }).click();
  const decision = checker.page.getByRole('region', { name: 'Rincian keputusan uang muka', exact: true }); await reviewReady(decision, 'Pastikan keputusan uang muka', 'Otorisasi nominal yang diminta'); await facts(decision);
  const capacity = decision.getByRole('region', { name: 'Batas otorisasi terkini', exact: true }); assert.match(await capacity.innerText(), /Rp20\.000(?![\d.,])/); assert.match(await capacity.innerText(), /Rp0(?![\d.,])/);
  const preview = await query(checker.page, config.checkerId, requestPath + '/decision-preview/query', { purpose: 'funding-review', decision: 'approve' }); assert.equal(preview.status, 200);
  const probeKey = 'browser-funding-missing-precondition';
  assert.equal((await query(checker.page, config.checkerId, requestPath + '/decisions', { decision: 'approve', binding: preview.body.binding, reason: 'Synthetic missing precondition probe' }, { headers: { 'Idempotency-Key': probeKey } })).status, 400);
  dropNext(); await decision.getByRole('button', { name: 'Otorisasi nominal yang diminta', exact: true }).click(); await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(completed().length, 2); const approved = completed()[1]; assert.equal(approved.path, requestPath + '/decisions'); assert.equal(approved.actor, config.checkerId); assert.equal(approved.version, '"1"');
  await pending(checker.page, approved, 'procurement.funding.approve'); await recover(checker.page); assert.equal(completed().length, 2); assert.equal(dropped(), 2); await row(checker.page).click();
  const detail = checker.page.getByRole('region', { name: 'Rincian otorisasi uang muka', exact: true }); await detail.getByRole('heading', { name: 'Diotorisasi secara internal', exact: true }).waitFor(); await facts(detail);
  assert.equal(await checker.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await checker.page.screenshot({ path: resolve(directory, 'procurement-funding-approved-mobile.png'), fullPage: true });
  assert.equal(fundingRecoveries.length, 3); assert.equal(fundingRecoveries[0].status, 409);
  for (const [index, write] of [requested, approved].entries()) { assert.equal(fundingRecoveries[index + 1].status, 200); assert.equal(fundingRecoveries[index + 1].body.key, write.key); }
  const reader = await user(config.readerToken); await reader.page.goto(origin.origin + path); await choose(reader.page); await row(reader.page).click();
  const readDetail = reader.page.getByRole('region', { name: 'Rincian otorisasi uang muka', exact: true }); await readDetail.getByRole('heading', { name: 'Diotorisasi secara internal', exact: true }).waitFor(); await facts(readDetail);
  assert.equal(await reader.page.getByRole('button', { name: 'Ajukan uang muka', exact: true }).count(), 0);
  const remaining = await query(reader.page, config.readerId, api + '/orders/query', readBody); assert.equal(remaining.status, 200); assert.equal(remaining.body.items.length, 1);
  assert.equal(remaining.body.items[0].capacity.remaining, '10000.00'); assert.equal(remaining.body.items[0].capacity.authorized, '10000.00');
  await page.getByRole('button', { name: 'Ajukan uang muka', exact: true }).click(); await page.getByRole('heading', { name: 'Ajukan otorisasi uang muka', exact: true }).waitFor();
  await page.getByLabel('Pesanan pembelian yang disetujui', { exact: true }).selectOption(config.orderId);
  const live = page.getByRole('region', { name: 'Pesanan pilihan', exact: true });
  assert.equal(await live.locator('dl div').filter({ has: page.getByText('Sisa yang dapat diotorisasi', { exact: true }) }).getByRole('definition').innerText(), 'Rp10.000');
  const outsider = await user(config.outsiderToken); await outsider.page.goto(origin.origin + '/__funding_scope_probe');
  const foreign = await query(outsider.page, config.outsiderId, requestPath + '/query', { purpose: 'funding-review' }); assert.equal(foreign.status, 403); assert.equal(foreign.body.code, 'scope_denied');
  for (const field of ['input', 'source', 'order', 'policy', 'capacity']) assert.equal(Object.hasOwn(foreign.body, field), false);
  assert.equal(fundingWrites.length, 3); assert.equal(completed().length, 2); const failed = fundingWrites.filter(write => write.status !== 200); assert.equal(failed.length, 1);
  assert.equal(failed[0].key, probeKey); assert.equal(failed[0].status, 400); assert.equal(failed[0].version, undefined);
  console.log('REAL_PROCUREMENT_FUNDING_BROWSER_PASS ' + JSON.stringify({ writes: 2, rejectedCommands: 1, originalReplyLosses: dropped(), actualApprovedOrder: true,
    independentReview: true, authorized: '10000.00', remaining: '10000.00', internalAuthorizationOnly: true, exactActorRecovery: true, offlineUnsent: true, mobile: true, desktop: true }));
}
