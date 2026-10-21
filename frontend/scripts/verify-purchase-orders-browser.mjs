// Actual shared Next/HTTPS/Kestrel/Mongo; producer records are real and all operational inputs are synthetic.
import assert from 'node:assert/strict';
import { resolve } from 'node:path';

export async function verifyPurchaseOrdersBrowser({ config, maker, user, origin, directory, purchaseOrderWrites, purchaseOrderRecoveries, dropNext, dropped }) {
  const { page, context } = maker, path = '/procurement/purchase-orders', api = '/api/v1/procurement/purchase-orders';
  const location = { organizationId: config.organizationId, shopId: config.shopId, warehouseId: config.warehouseId, channelId: config.channelId };
  const canonical = Object.values(location).join(':'), completed = () => purchaseOrderWrites.filter(write => write.status === 200);
  const consent = 'Saya telah memeriksa pemasok, kemasan, jumlah, nilai dan syarat pembayaran.';
  const reference = 'SYNTHETIC-QUOTE-BROWSER-0001', description = 'Syarat kredit sintetis yang ditinjau untuk jatuh tempo tiga puluh hari';
  const input = { location, requestId: config.requestId, requestVersion: '1', requestDecisionId: config.requestDecisionId,
    supplierId: config.supplierId, supplierVersionId: config.supplierVersionId, supplierVersion: config.supplierVersion,
    businessDate: config.businessDate, currency: 'IDR', commercialReference: { kind: 'quote', reference, issuedDate: config.businessDate, validUntil: config.validUntil },
    externalOrderReference: null, paymentTerms: { kind: 'credit', description, requiredAdvance: '0.00', balanceTrigger: 'due_date', dueDate: config.dueDate },
    lines: [{ requestSkuId: config.skuId, packages: 3, sourceSystem: 'supplier', sourceAccountReference: '000001', externalItemCode: '000012', definitionId: config.definitionId,
      definitionVersion: config.definitionVersion, netPricePerPackage: '10000.00', taxAmount: '3300.00', supplierChargeAmount: '700.00' }] };
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
    await select.selectOption(canonical); await current.getByRole('region', { name: 'Rincian pesanan pembelian', exact: true }).waitFor();
  }
  async function pending(current, write, operation) {
    const saved = await references(current); assert.equal(saved.length, 1); assert.deepEqual(Object.keys(saved[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.equal(saved[0].actorId, write.actor); assert.equal(saved[0].key, write.key); assert.equal(saved[0].operation, operation);
    for (const value of [reference, description, write.body.reason, config.specification]) assert.ok(!JSON.stringify(saved).includes(value));
    assert.equal(await current.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0); return saved;
  }
  async function recover(current) {
    await current.reload(); await current.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
    await current.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden' }); assert.equal((await references(current)).length, 0); await choose(current);
  }
  async function facts(region) {
    const text = await region.innerText();
    for (const value of [config.organizationName, config.shopName, config.warehouseName, config.channelName, config.supplierName, config.supplierSubject,
      config.skuCode, config.familyName, config.packageLabel, config.specification, '12 piece per kemasan', '3 kemasan', '36 piece', '000001', '000012', reference,
      'Rp10.000,00', 'Rp30.000,00', 'Rp3.300,00', 'Rp700,00', 'Rp34.000,00', description, config.dueDate]) assert.ok(text.includes(value), value);
  }
  async function reviewReady(region, heading, button) {
    await region.waitFor(); assert.equal(await region.getByRole('heading', { name: heading, exact: true }).evaluate(element => element === document.activeElement), true);
    assert.equal(await region.getByRole('textbox').count(), 0); assert.equal(await region.getByRole('button', { name: button, exact: true }).isDisabled(), true);
    await region.getByRole('checkbox', { name: consent, exact: true }).check();
  }
  assert.notEqual(config.makerId, config.checkerId); await page.goto(origin.origin + path); await choose(page); await page.getByText('Belum ada usulan pesanan.', { exact: true }).waitFor();
  const readBody = { purpose: 'purchase-order-review', location };
  for (const [body, options, expected] of [[readBody, { omit: ['X-CSRF-Token'] }, 403], [readBody, { omit: ['X-Expected-Actor-Id'] }, 400],
    [{ ...readBody, purpose: 'supplier-selection' }, {}, 400], [{ ...readBody, location: { ...location, shopId: Number(location.shopId) } }, {}, 400], [{ ...readBody, unknown: true }, {}, 400]])
    assert.equal((await query(page, config.makerId, api + '/proposals/query', body, options)).status, expected);
  await page.getByRole('button', { name: 'Buat pesanan pembelian', exact: true }).click();
  const request = page.getByLabel('Permintaan yang disetujui', { exact: true }), supplier = page.getByLabel('Pemasok yang disetujui', { exact: true });
  assert.equal(await request.inputValue(), ''); assert.equal(await supplier.inputValue(), ''); await request.selectOption(config.requestId); await supplier.selectOption(config.supplierId);
  const line = page.getByRole('region', { name: 'Tambah barang pesanan', exact: true }); assert.equal(await line.getByLabel('Barang permintaan', { exact: true }).inputValue(), '');
  await line.getByLabel('Barang permintaan', { exact: true }).selectOption(config.skuId); assert.equal(await line.getByLabel('Pemetaan pemasok', { exact: true }).inputValue(), '');
  await line.getByLabel('Pemetaan pemasok', { exact: true }).selectOption('0');
  for (const [label, value] of [['Jumlah kemasan', '3'], ['Harga neto per kemasan (IDR)', '10000.00'], ['Pajak seluruh baris (IDR)', '3300.00'], ['Biaya pemasok seluruh baris (IDR)', '700.00']]) {
    assert.equal(await line.getByLabel(label, { exact: true }).inputValue(), ''); await line.getByLabel(label, { exact: true }).fill(value);
  }
  await line.getByRole('button', { name: 'Tambahkan barang', exact: true }).click();
  const wib = iso => new Date(Date.parse(iso) + 7 * 60 * 60 * 1000).toISOString().slice(0, 16);
  for (const [label, value] of [['Tanggal pesanan', config.businessDate], ['Referensi dokumen komersial', reference], ['Tanggal dokumen', config.businessDate], ['Berlaku hingga (WIB)', wib(config.validUntil)],
    ['Rincian syarat pembayaran', description], ['Uang muka yang disyaratkan (IDR)', '0.00']]) { assert.equal(await page.getByLabel(label, { exact: true }).inputValue(), ''); await page.getByLabel(label, { exact: true }).fill(value); }
  for (const [label, value] of [['Jenis dokumen komersial', 'quote'], ['Syarat pembayaran', 'credit'], ['Pemicu pelunasan', 'due_date']]) {
    assert.equal(await page.getByLabel(label, { exact: true }).inputValue(), ''); await page.getByLabel(label, { exact: true }).selectOption(value);
  }
  assert.equal(await page.getByLabel('Tanggal jatuh tempo', { exact: true }).inputValue(), ''); await page.getByLabel('Tanggal jatuh tempo', { exact: true }).fill(config.dueDate);
  await page.getByLabel('Alasan usulan', { exact: true }).fill('Usulan pesanan sintetis dari permintaan dan pemasok yang benar-benar ditinjau');
  await page.getByRole('button', { name: 'Periksa pesanan pembelian', exact: true }).click();
  const review = page.getByRole('region', { name: 'Rincian usulan pesanan', exact: true }); await reviewReady(review, 'Pastikan usulan pesanan', 'Kirim usulan pesanan'); await facts(review);
  const invalidInputs = [];
  for (const field of ['taxAmount', 'supplierChargeAmount']) { const invalid = structuredClone(input); delete invalid.lines[0][field]; invalidInputs.push(invalid); }
  const noAdvance = structuredClone(input); delete noAdvance.paymentTerms.requiredAdvance; invalidInputs.push(noAdvance);
  invalidInputs.push({ ...input, requestId: Number(input.requestId) }, { ...input, route: 'pasar_kering' }, { ...input, lines: [{ ...input.lines[0], netPricePerPackage: 10000 }] });
  for (const invalid of invalidInputs) assert.equal((await query(page, config.makerId, api + '/preview/query', { purpose: 'purchase-order-review', input: invalid })).status, 400);
  await context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await review.getByRole('button', { name: 'Kirim usulan pesanan', exact: true }).isDisabled(), true); assert.equal(purchaseOrderWrites.length, 0);
  await context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'purchase-order-proposal-mobile.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'purchase-order-proposal-desktop.png'), fullPage: true });
  dropNext(); await review.getByRole('button', { name: 'Kirim usulan pesanan', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(completed().length, 1); const proposed = completed()[0]; assert.equal(proposed.path, api + '/proposals'); assert.equal(proposed.actor, config.makerId);
  assert.deepEqual({ ...proposed.body.input, commercialReference: { ...proposed.body.input.commercialReference, validUntil: new Date(proposed.body.input.commercialReference.validUntil).toISOString() } }, input);
  const saved = await pending(page, proposed, 'procurement.purchase.order.propose');
  await context.addCookies([{ name: '__Host-ut_session', value: config.checkerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('heading', { name: /^(Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor(); assert.deepEqual(await references(page), saved);
  await context.addCookies([{ name: '__Host-ut_session', value: config.makerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await recover(page); assert.equal(purchaseOrderWrites.length, 1);
  const history = await query(page, config.makerId, api + '/proposals/query', readBody); assert.equal(history.status, 200); assert.equal(history.body.items.length, 1);
  const proposal = history.body.items[0], proposalPath = api + '/proposals/' + proposal.id; assert.equal(proposal.state, 'pending'); assert.equal(proposal.decision, null);
  assert.deepEqual(proposal.totals, { net: '30000.00', tax: '3300.00', supplierCharges: '700.00', total: '34000.00' });
  const row = current => current.getByRole('button', { name: new RegExp('Usulan ' + proposal.id + '$') });
  await row(page).click(); assert.equal(await page.getByRole('button', { name: 'Tinjau persetujuan pesanan', exact: true }).count(), 0);
  const checker = await user(config.checkerToken); await checker.page.goto(origin.origin + path); await choose(checker.page); await row(checker.page).click();
  await checker.page.getByLabel('Alasan keputusan', { exact: true }).fill('Peninjauan sintetis independen atas pemasok, spesifikasi, kapasitas dan semua nilai');
  await checker.page.getByRole('button', { name: 'Tinjau persetujuan pesanan', exact: true }).click();
  const decision = checker.page.getByRole('region', { name: 'Rincian keputusan pesanan', exact: true }); await reviewReady(decision, 'Pastikan keputusan pesanan', 'Setujui pesanan secara internal'); await facts(decision);
  assert.ok((await decision.getByRole('region', { name: 'Kapasitas terkini', exact: true }).innerText()).includes('tersisa 6'));
  const preview = await query(checker.page, config.checkerId, proposalPath + '/decision-preview/query', { purpose: 'purchase-order-review', decision: 'approve' }); assert.equal(preview.status, 200);
  const probeKey = 'browser-po-missing-precondition';
  assert.equal((await query(checker.page, config.checkerId, proposalPath + '/decisions', { decision: 'approve', binding: preview.body.binding, reason: 'Synthetic missing precondition probe' }, { headers: { 'Idempotency-Key': probeKey } })).status, 400);
  dropNext(); await decision.getByRole('button', { name: 'Setujui pesanan secara internal', exact: true }).click(); await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(completed().length, 2); const approved = completed()[1]; assert.equal(approved.path, proposalPath + '/decisions'); assert.equal(approved.actor, config.checkerId); assert.equal(approved.version, '"1"');
  await pending(checker.page, approved, 'procurement.purchase.order.approve'); await recover(checker.page); assert.equal(completed().length, 2); assert.equal(dropped(), 2); await row(checker.page).click();
  await checker.page.getByRole('heading', { name: 'Disetujui secara internal', exact: true }).waitFor(); await facts(checker.page.getByRole('region', { name: 'Rincian pesanan pembelian', exact: true }));
  assert.equal(await checker.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await checker.page.screenshot({ path: resolve(directory, 'purchase-order-approved-mobile.png'), fullPage: true });
  assert.equal(purchaseOrderRecoveries.length, 3); assert.equal(purchaseOrderRecoveries[0].status, 409);
  for (const [index, write] of [proposed, approved].entries()) { assert.equal(purchaseOrderRecoveries[index + 1].status, 200); assert.equal(purchaseOrderRecoveries[index + 1].body.key, write.key); }
  const reader = await user(config.readerToken); await reader.page.goto(origin.origin + path); await choose(reader.page); await row(reader.page).click();
  await reader.page.getByRole('region', { name: 'Rincian pesanan pembelian', exact: true }).getByRole('heading', { name: 'Disetujui secara internal', exact: true }).waitFor();
  await facts(reader.page.getByRole('region', { name: 'Rincian pesanan pembelian', exact: true })); assert.equal(await reader.page.getByRole('button', { name: 'Buat pesanan pembelian', exact: true }).count(), 0);
  const remaining = await query(reader.page, config.readerId, api + '/requests/query', readBody); assert.equal(remaining.status, 200); assert.equal(remaining.body.items.length, 1);
  assert.equal(remaining.body.items[0].lines[0].availablePackages, 3); assert.equal(remaining.body.items[0].lines[0].committedPackages, 3);
  const outsider = await user(config.outsiderToken); await outsider.page.goto(origin.origin + '/__purchase_order_scope_probe');
  const foreign = await query(outsider.page, config.outsiderId, proposalPath + '/query', { purpose: 'purchase-order-review' }); assert.equal(foreign.status, 403); assert.equal(foreign.body.code, 'scope_denied');
  for (const field of ['input', 'source', 'supplier', 'totals', 'capacity']) assert.equal(Object.hasOwn(foreign.body, field), false);
  assert.equal(purchaseOrderWrites.length, 3); assert.equal(completed().length, 2); const failed = purchaseOrderWrites.filter(write => write.status !== 200); assert.equal(failed.length, 1);
  assert.equal(failed[0].key, probeKey); assert.equal(failed[0].status, 400); assert.equal(failed[0].version, undefined);
  console.log('REAL_PURCHASE_ORDER_BROWSER_PASS ' + JSON.stringify({ writes: 2, rejectedCommands: 1, originalReplyLosses: dropped(), actualProducerProvenance: true,
    independentReview: true, explicitNonzeroTaxAndCharges: true, exactRemainingPackages: 3, exactActorRecovery: true, offlineUnsent: true, mobile: true, desktop: true }));
}
