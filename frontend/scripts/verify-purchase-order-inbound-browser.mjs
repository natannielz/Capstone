// Real production Next/HTTPS/Kestrel/Mongo; local people, documents, scanner and policies are synthetic.
// A planned receiving destination leaves the genuinely reviewed source INACTIVE and creates no stock.
import assert from 'node:assert/strict';
import { writeFileSync } from 'node:fs';
import { basename, resolve } from 'node:path';

export async function verifyPurchaseOrderInboundBrowser({ config, maker, user, origin, directory, purchaseOrderWrites, purchaseOrderRecoveries, dropNext, dropped }) {
  const { page, context } = maker, path = '/procurement/purchase-orders', api = '/api/v1/procurement/purchase-orders';
  const poolApi = '/api/v1/procurement/stock-receipts/pools/query', poolRoute = '**' + poolApi;
  const location = { organizationId: config.organizationId, shopId: config.shopId, warehouseId: config.warehouseId, channelId: config.channelId };
  const canonical = Object.values(location).join(':'), completed = () => purchaseOrderWrites.filter(write => write.status === 200);
  const consent = 'Saya telah memeriksa pemasok, kemasan, jumlah, nilai dan syarat pembayaran.';
  const reference = 'SYNTHETIC-QUOTE-INBOUND-BROWSER-0001', description = 'Syarat kredit sintetis yang ditinjau untuk jatuh tempo tiga puluh hari';
  const disclaimer = 'Rencana ini tidak mencadangkan stok dan bukan penerimaan fisik, pelepasan stok, pembayaran atau invoice.';
  const input = { location, requestId: config.requestId, requestVersion: '1', requestDecisionId: config.requestDecisionId,
    supplierId: config.supplierId, supplierVersionId: config.supplierVersionId, supplierVersion: config.supplierVersion,
    businessDate: config.businessDate, currency: 'IDR', commercialReference: { kind: 'quote', reference, issuedDate: config.businessDate, validUntil: config.validUntil },
    externalOrderReference: null, paymentTerms: { kind: 'credit', description, requiredAdvance: '0.00', balanceTrigger: 'due_date', dueDate: config.dueDate },
    lines: [{ requestSkuId: config.skuId, packages: 2, sourceSystem: 'supplier', sourceAccountReference: '000001', externalItemCode: '000012', definitionId: config.definitionId,
      definitionVersion: config.definitionVersion, netPricePerPackage: '10000.00', taxAmount: '3300.00', supplierChargeAmount: '700.00' }] };
  const selected = { allocations: [{ requestSkuId: config.skuId, packages: 2, poolId: config.poolId, poolRevision: config.poolRevision }] };
  const references = current => current.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  const query = (current, actorId, route, body, headers = {}) => current.evaluate(async ({ actorId, route, body, headers }) => {
    const csrf = await fetch('/api/v1/identity/csrf', { credentials: 'same-origin', cache: 'no-store', redirect: 'error' });
    if (!csrf.ok) throw new Error('Actual request token unavailable'); const token = await csrf.json();
    const response = await fetch(route, { method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': token.requestToken, 'X-Expected-Actor-Id': actorId, ...headers }, body: JSON.stringify(body) });
    const text = await response.text(); return { status: response.status, body: text ? JSON.parse(text) : null };
  }, { actorId, route, body, headers });
  async function choose(current) {
    const select = current.getByLabel('Organisasi / toko / gudang / kanal', { exact: true });
    await select.getByRole('option', { name: [config.organizationName, config.shopName, config.warehouseName, config.channelName].join(' · '), exact: true }).waitFor({ state: 'attached' });
    await select.selectOption(canonical); await current.getByRole('region', { name: 'Rincian pesanan pembelian', exact: true }).waitFor();
    await current.getByRole('link', { name: 'Unit Toko', exact: true }).waitFor();
  }
  async function pending(current, write, operation) {
    const saved = await references(current); assert.equal(saved.length, 1); assert.deepEqual(Object.keys(saved[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.equal(saved[0].actorId, write.actor); assert.equal(saved[0].key, write.key); assert.equal(saved[0].operation, operation);
    for (const value of [reference, description, write.body.reason, config.specification, config.poolLabel]) assert.ok(!JSON.stringify(saved).includes(value));
    assert.equal(await current.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0); return saved;
  }
  async function recover(current) {
    await current.reload(); await current.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
    await current.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden' }); assert.equal((await references(current)).length, 0); await choose(current);
  }
  async function facts(region, selectionId) {
    if (selectionId) await region.getByText(`Rencana tersimpan ${selectionId}. Rincian sumber ini merupakan catatan saat pengajuan, bukan pernyataan ketersediaan atau kewenangan sumber saat ini.`, { exact: true }).waitFor();
    const text = await region.innerText();
    for (const value of [config.organizationName, config.shopName, config.warehouseName, config.channelName, config.supplierName, config.supplierSubject,
      config.skuCode, config.familyName, config.packageLabel, config.specification, '12 piece per kemasan', '2 kemasan', '24 piece', '000001', '000012', reference,
      'Rp10000.00', 'Rp20000.00', 'Rp3300.00', 'Rp700.00', 'Rp24000.00', description, config.dueDate]) assert.ok(text.includes(value), value);
    const plan = region.getByRole('region', { name: 'Rencana tujuan penerimaan', exact: true });
    const details = await plan.innerText(); for (const value of [config.poolLabel, 'Referensi tujuan ' + config.poolId + ', revisi ' + config.poolRevision, '2 kemasan', disclaimer, 'jumlah berlebih']) assert.ok(details.includes(value), value);
  }
  async function reviewReady(current, region, heading, button) {
    await region.waitFor(); assert.equal(await region.getByRole('heading', { name: heading, exact: true }).evaluate(element => element === document.activeElement), true);
    assert.equal(await region.getByRole('textbox').count(), 0); assert.equal(await region.getByRole('button', { name: button, exact: true }).isDisabled(), true);
    await region.getByRole('checkbox', { name: consent, exact: true }).focus(); await current.keyboard.press('Space');
    assert.equal(await region.getByRole('button', { name: button, exact: true }).isDisabled(), false);
  }
  const screenshotPaths = [], artifactName = basename(config.proofPath, '.json');
  async function screen(current, suffix) {
    assert.equal(await current.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    const target = resolve(directory, artifactName + '-' + suffix + '.png'); await current.screenshot({ path: target, fullPage: true }); screenshotPaths.push(target);
  }
  function checkPlan(value, state, decisionId) {
    assert.deepEqual(value.allocations, selected.allocations); assert.equal(value.state, state); assert.equal(value.decisionId, decisionId);
    assert.equal(value.receivingPools.length, 1); const pool = value.receivingPools[0]; assert.equal(pool.id, config.poolId); assert.equal(pool.revision, config.poolRevision);
    assert.equal(pool.active, false); assert.equal(pool.label, config.poolLabel); assert.deepEqual(pool.requestedLocation, location);
    assert.equal(value.unplannedPackagesBound, false); assert.equal(value.stockReserved, false); assert.equal(value.physicalStockReleased, false);
    assert.match(value.selectionHash, /^[0-9a-f]{64}$/); return pool;
  }
  assert.notEqual(config.makerId, config.checkerId); await page.goto(origin.origin + path); await choose(page);
  const readBody = { purpose: 'purchase-order-review', location };
  const legacy = await query(page, config.makerId, api + '/proposals/' + config.legacyProposalId + '/query', { purpose: 'purchase-order-review' });
  assert.equal(legacy.status, 200); assert.equal(legacy.body.state, 'approved'); assert.equal(Object.hasOwn(legacy.body, 'inboundPools'), false);
  await page.getByRole('button', { name: new RegExp('Usulan ' + config.legacyProposalId + '$') }).click();
  await page.getByText('Tujuan penerimaan belum diikat pada pesanan ini. Jangan menyimpulkan tujuan dari lokasi saat ini atau penerimaan yang terjadi kemudian.', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Buat pesanan pembelian', exact: true }).click();
  const request = page.getByLabel('Permintaan yang disetujui', { exact: true }), supplier = page.getByLabel('Pemasok yang disetujui', { exact: true });
  assert.equal(await request.inputValue(), ''); assert.equal(await supplier.inputValue(), ''); await request.selectOption(config.requestId); await supplier.selectOption(config.supplierId);
  const line = page.getByRole('region', { name: 'Tambah barang pesanan', exact: true }); await line.getByLabel('Barang permintaan', { exact: true }).selectOption(config.skuId);
  await line.getByLabel('Pemetaan pemasok', { exact: true }).selectOption('0');
  for (const [label, value] of [['Jumlah kemasan', '2'], ['Harga neto per kemasan (IDR)', '10000.00'], ['Pajak seluruh baris (IDR)', '3300.00'], ['Biaya pemasok seluruh baris (IDR)', '700.00']]) {
    assert.equal(await line.getByLabel(label, { exact: true }).inputValue(), ''); await line.getByLabel(label, { exact: true }).fill(value);
  }
  await line.getByRole('button', { name: 'Tambahkan barang', exact: true }).click();
  const editor = page.getByRole('region', { name: 'Susun rencana tujuan penerimaan', exact: true });
  const enable = editor.getByRole('checkbox', { name: 'Tentukan pembagian jumlah ke tujuan penerimaan', exact: true });
  assert.equal(await enable.isChecked(), false); assert.equal(await enable.isDisabled(), false); await enable.focus(); await page.keyboard.press('Space');
  await editor.getByText('Tujuan belum dimuat. Muat daftar yang dapat Anda akses sebelum memilih.', { exact: true }).waitFor();
  assert.equal(await editor.getByLabel('Tujuan penerimaan', { exact: true }).inputValue(), '');
  // A real read is prevented once to rehearse a network error; no fake successful data/approval is injected.
  await page.route(poolRoute, route => route.abort('failed'), { times: 1 });
  await editor.getByRole('button', { name: 'Muat tujuan penerimaan terbaru', exact: true }).click();
  await page.getByRole('alert').filter({ hasText: /Koneksi terputus|Layanan belum tersedia/ }).waitFor(); assert.equal(purchaseOrderWrites.length, 0);
  let resume; const pause = new Promise(resolve => { resume = resolve; });
  let entered; const reached = new Promise(resolve => { entered = resolve; });
  await page.route(poolRoute, async route => { entered(); await pause; await route.continue(); }, { times: 1 });
  try {
    await editor.getByRole('button', { name: 'Muat tujuan penerimaan terbaru', exact: true }).click(); await reached;
    await page.getByRole('status').filter({ hasText: 'Memeriksa pesanan pembelian…' }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Periksa pesanan pembelian', exact: true }).isDisabled(), true);
  } finally { resume(); }
  const poolSelect = editor.getByLabel('Tujuan penerimaan', { exact: true });
  await poolSelect.getByRole('option', { name: config.poolLabel + ' · referensi ' + config.poolId + ' · revisi ' + config.poolRevision, exact: true }).waitFor({ state: 'attached' });
  await page.getByRole('status').filter({ hasText: 'Memeriksa pesanan pembelian…' }).waitFor({ state: 'hidden' });
  const pools = await query(page, config.makerId, poolApi, { purpose: 'procurement-stock-receipt-review', location, beforeId: null });
  assert.equal(pools.status, 200); const genuinePool = pools.body.items.find(item => item.id === config.poolId); assert.ok(genuinePool); assert.equal(genuinePool.active, false);
  assert.equal(await poolSelect.inputValue(), ''); await editor.getByLabel('Barang untuk pembagian', { exact: true }).selectOption(config.skuId); await poolSelect.selectOption(config.poolId);
  await editor.getByLabel('Jumlah kemasan untuk tujuan ini', { exact: true }).fill('1');
  await editor.getByRole('button', { name: 'Tambahkan pembagian tujuan', exact: true }).click();
  const wib = iso => new Date(Date.parse(iso) + 7 * 60 * 60 * 1000).toISOString().slice(0, 16);
  for (const [label, value] of [['Tanggal pesanan', config.businessDate], ['Referensi dokumen komersial', reference], ['Tanggal dokumen', config.businessDate], ['Berlaku hingga (WIB)', wib(config.validUntil)],
    ['Rincian syarat pembayaran', description], ['Uang muka yang disyaratkan (IDR)', '0.00']]) await page.getByLabel(label, { exact: true }).fill(value);
  for (const [label, value] of [['Jenis dokumen komersial', 'quote'], ['Syarat pembayaran', 'credit'], ['Pemicu pelunasan', 'due_date']]) await page.getByLabel(label, { exact: true }).selectOption(value);
  await page.getByLabel('Tanggal jatuh tempo', { exact: true }).fill(config.dueDate); await page.getByLabel('Alasan usulan', { exact: true }).fill('Rencana sintetis dua kemasan ke sumber yang benar-benar ditinjau, tetap tidak aktif');
  await page.getByRole('button', { name: 'Periksa pesanan pembelian', exact: true }).click();
  await page.getByRole('alert').filter({ hasText: 'Periksa permintaan, pemetaan, tanggal, jumlah dan seluruh nilai komersial.' }).waitFor();
  assert.equal(await page.getByRole('heading', { name: 'Pastikan usulan pesanan', exact: true }).count(), 0); assert.equal(purchaseOrderWrites.length, 0);
  await editor.getByRole('button', { name: 'Hapus pembagian 1', exact: true }).click();
  await editor.getByLabel('Barang untuk pembagian', { exact: true }).selectOption(config.skuId); await poolSelect.selectOption(config.poolId);
  const packages = editor.getByLabel('Jumlah kemasan untuk tujuan ini', { exact: true }); await packages.fill('2'); await packages.focus(); await page.keyboard.press('Tab');
  assert.equal(await editor.getByRole('button', { name: 'Tambahkan pembagian tujuan', exact: true }).evaluate(element => element === document.activeElement), true);
  await page.keyboard.press('Enter'); await editor.getByText('Pensil pengadaan · Dus: dipesan 2, direncanakan 2 kemasan.', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Periksa pesanan pembelian', exact: true }).click();
  const review = page.getByRole('region', { name: 'Rincian usulan pesanan', exact: true }); await reviewReady(page, review, 'Pastikan usulan pesanan', 'Kirim usulan pesanan'); await facts(review);
  await context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await review.getByRole('button', { name: 'Kirim usulan pesanan', exact: true }).isDisabled(), true); assert.equal(purchaseOrderWrites.length, 0);
  await context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
  await screen(page, 'proposal-mobile'); await page.setViewportSize({ width: 1440, height: 1000 }); await screen(page, 'proposal-desktop');
  dropNext(); await review.getByRole('button', { name: 'Kirim usulan pesanan', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ timeout: 60000 });
  assert.equal(completed().length, 1); const proposed = completed()[0]; assert.equal(proposed.path, api + '/proposals'); assert.equal(proposed.actor, config.makerId);
  assert.deepEqual({ ...proposed.body.input, commercialReference: { ...proposed.body.input.commercialReference, validUntil: new Date(proposed.body.input.commercialReference.validUntil).toISOString() } }, input);
  assert.deepEqual(proposed.body.inboundPools, selected); const saved = await pending(page, proposed, 'procurement.purchase.order.propose');
  await context.addCookies([{ name: '__Host-ut_session', value: config.checkerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('heading', { name: 'Periksa sesi dan akses', exact: true }).waitFor(); assert.deepEqual(await references(page), saved);
  await context.addCookies([{ name: '__Host-ut_session', value: config.makerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await recover(page); assert.equal(purchaseOrderWrites.length, 1);
  const history = await query(page, config.makerId, api + '/proposals/query', readBody); assert.equal(history.status, 200); assert.equal(history.body.items.length, 2);
  const proposal = history.body.items.find(item => item.input.commercialReference.reference === reference); assert.ok(proposal); assert.equal(proposal.state, 'pending');
  assert.equal(proposal.decision, null); checkPlan(proposal.inboundPools, 'proposed', null); assert.equal(proposal.inboundPools.selectionId, proposal.id);
  assert.deepEqual(proposal.totals, { net: '20000.00', tax: '3300.00', supplierCharges: '700.00', total: '24000.00' });
  const proposalPath = api + '/proposals/' + proposal.id, row = current => current.getByRole('button', { name: new RegExp('Usulan ' + proposal.id + '$') });
  await row(page).click(); await facts(page.getByRole('region', { name: 'Rincian pesanan pembelian', exact: true }), proposal.inboundPools.selectionId); assert.equal(await page.getByRole('button', { name: 'Tinjau persetujuan pesanan', exact: true }).count(), 0);
  const checker = await user(config.checkerToken); await checker.page.goto(origin.origin + path); await choose(checker.page); await row(checker.page).click();
  await checker.page.getByLabel('Alasan keputusan', { exact: true }).fill('Pemeriksa sintetis independen meninjau pembagian dua kemasan dan sumber penerimaan yang tetap tidak aktif');
  await checker.page.getByRole('button', { name: 'Tinjau persetujuan pesanan', exact: true }).click();
  const decision = checker.page.getByRole('region', { name: 'Rincian keputusan pesanan', exact: true }); await reviewReady(checker.page, decision, 'Pastikan keputusan pesanan', 'Setujui pesanan secara internal'); await facts(decision);
  assert.ok((await decision.getByRole('region', { name: 'Kapasitas terkini', exact: true }).innerText()).includes('tersisa 3'));
  dropNext(); await decision.getByRole('button', { name: 'Setujui pesanan secara internal', exact: true }).click(); await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ timeout: 60000 });
  assert.equal(completed().length, 2); const approved = completed()[1]; assert.equal(approved.path, proposalPath + '/decisions'); assert.equal(approved.actor, config.checkerId); assert.equal(approved.version, '"1"');
  await pending(checker.page, approved, 'procurement.purchase.order.approve'); await recover(checker.page); await row(checker.page).click();
  await checker.page.getByRole('heading', { name: 'Disetujui secara internal', exact: true }).waitFor(); await facts(checker.page.getByRole('region', { name: 'Rincian pesanan pembelian', exact: true }), proposal.inboundPools.selectionId); await screen(checker.page, 'approved-mobile');
  await page.reload(); await choose(page); await row(page).click(); await facts(page.getByRole('region', { name: 'Rincian pesanan pembelian', exact: true }), proposal.inboundPools.selectionId); await screen(page, 'reloaded-maker-desktop');
  const final = await query(page, config.makerId, proposalPath + '/query', { purpose: 'purchase-order-review' }); assert.equal(final.status, 200); assert.equal(final.body.state, 'approved');
  checkPlan(final.body.inboundPools, 'approve', final.body.decision.id); assert.equal(final.body.inboundPools.selectionHash, proposal.inboundPools.selectionHash);
  const reader = await user(config.readerToken); await reader.page.goto(origin.origin + path); await choose(reader.page); await row(reader.page).click();
  await facts(reader.page.getByRole('region', { name: 'Rincian pesanan pembelian', exact: true }), proposal.inboundPools.selectionId); assert.equal(await reader.page.getByRole('button', { name: 'Buat pesanan pembelian', exact: true }).count(), 0);
  const read = await query(reader.page, config.readerId, proposalPath + '/query', { purpose: 'purchase-order-review' }); assert.equal(read.status, 200); assert.deepEqual(read.body, final.body);
  const remaining = await query(reader.page, config.readerId, api + '/requests/query', readBody); assert.equal(remaining.status, 200); assert.equal(remaining.body.items.length, 1);
  assert.equal(remaining.body.items[0].lines[0].availablePackages, 1); assert.equal(remaining.body.items[0].lines[0].committedPackages, 5);
  const outsider = await user(config.outsiderToken); await outsider.page.goto(origin.origin + '/__purchase_order_inbound_scope_probe');
  const foreign = await query(outsider.page, config.outsiderId, proposalPath + '/query', { purpose: 'purchase-order-review' }); assert.equal(foreign.status, 403); assert.equal(foreign.body.code, 'scope_denied');
  for (const field of ['input', 'source', 'supplier', 'totals', 'capacity', 'inboundPools']) assert.equal(Object.hasOwn(foreign.body, field), false);
  const foreignPools = await query(outsider.page, config.outsiderId, poolApi, { purpose: 'procurement-stock-receipt-review', location, beforeId: null }); assert.equal(foreignPools.status, 403);
  assert.equal(Object.hasOwn(foreignPools.body, 'items'), false); assert.equal(purchaseOrderWrites.length, 2); assert.equal(dropped(), 2);
  assert.equal(purchaseOrderRecoveries.length, 3); assert.equal(purchaseOrderRecoveries[0].status, 409);
  for (const [index, write] of [proposed, approved].entries()) { assert.equal(purchaseOrderRecoveries[index + 1].status, 200); assert.equal(purchaseOrderRecoveries[index + 1].body.key, write.key); }
  assert.equal((await references(page)).length, 0); assert.equal((await references(checker.page)).length, 0);
  const proof = { localSyntheticOnly: true, hostedProviderAcceptance: false, actualBrowserApiMongo: true, originalUnboundProposalId: config.legacyProposalId,
    proposalKey: proposed.key, decisionKey: approved.key, receivingPool: genuinePool, pendingProposal: proposal, finalProposal: final.body, readerProposal: read.body,
    remainingPackages: 1, committedPackages: 5, responseLosses: dropped(), actualWriteCount: completed().length, screenshotPaths,
    keyboard: true, mobile: true, desktop: true, loadingAndError: true, incompletePlanRefused: true, foreignScopesDenied: true, noActivationOrStockAuthority: true };
  writeFileSync(config.proofPath, JSON.stringify(proof, null, 2) + '\n', { encoding: 'utf8', flag: 'wx' });
  console.log('REAL_PURCHASE_ORDER_INBOUND_BROWSER_PASS ' + JSON.stringify({ proofPath: config.proofPath, proposalId: proposal.id,
    decisionId: final.body.decision.id, poolId: config.poolId, plannedPackages: 2, localSyntheticOnly: true, noActivationOrStockAuthority: true }));
}
