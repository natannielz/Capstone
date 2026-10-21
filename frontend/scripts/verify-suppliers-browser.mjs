// Real shared HTTPS/Next/Kestrel/Mongo. Verification policy, people and observed details are synthetic local inputs.
import assert from 'node:assert/strict';
import { resolve } from 'node:path';

export async function verifySuppliersBrowser({ config, maker, user, origin, directory, supplierWrites, supplierRecoveries, dropNext, dropped }) {
  const { page, context } = maker, path = '/procurement/suppliers', api = '/api/v1/procurement/suppliers';
  const owner = { organizationId: config.organizationId, shopId: config.shopId, channelId: config.channelId };
  const location = { organizationId: config.organizationId, shopId: config.shopId, warehouseId: config.warehouseId, channelId: config.channelId };
  const canonical = Object.values(owner).join(':'), completed = () => supplierWrites.filter(write => write.status === 200);
  const consent = 'Saya telah memeriksa seluruh rincian dan siap mencatat tindakan ini.';
  const fields = { subjectReference: 'Referensi identitas pemasok', legalName: 'Nama badan usaha', displayName: 'Nama tampilan', registrationNamespace: 'Jenis registrasi (opsional)',
    registrationValue: 'Nomor registrasi (opsional)', address: 'Alamat pemasok', contactName: 'Nama kontak', contactValue: 'Detail kontak' };
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
    const select = current.getByLabel('Organisasi / toko / kanal', { exact: true });
    await select.getByRole('option', { name: [config.organizationName, config.shopName, config.channelName].join(' · '), exact: true }).waitFor({ state: 'attached' });
    await select.selectOption(canonical); await current.getByRole('region', { name: 'Rincian pemasok', exact: true }).waitFor();
  }
  async function fillDetails(region) {
    for (const [field, label] of Object.entries(fields)) { assert.equal(await region.getByLabel(label, { exact: true }).inputValue(), ''); await region.getByLabel(label, { exact: true }).fill(config.details[field]); }
    assert.equal(await region.getByLabel('Metode kontak', { exact: true }).inputValue(), ''); await region.getByLabel('Metode kontak', { exact: true }).selectOption(config.details.contactMethod);
  }
  async function pending(current, write, operation) {
    const saved = await references(current); assert.equal(saved.length, 1); assert.deepEqual(Object.keys(saved[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.equal(saved[0].actorId, write.actor); assert.equal(saved[0].key, write.key); assert.equal(saved[0].operation, operation);
    for (const secret of [config.details.contactValue, config.details.address, config.trustedChannelReference, write.body.reason]) assert.ok(!JSON.stringify(saved).includes(secret));
    assert.equal(await current.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0); return saved;
  }
  async function recover(current) {
    await current.reload(); await current.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
    await current.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden' }); assert.equal((await references(current)).length, 0); await choose(current);
  }
  async function facts(region) {
    const text = await region.innerText();
    for (const value of [config.organizationName, config.shopName, config.warehouseName, config.channelName, config.skuCode, config.familyName, config.packageLabel,
      '12 piece per kemasan', '000001', '000012', config.trustedChannelReference, ...Object.values(config.details).filter(value => value !== 'email')]) assert.ok(text.includes(value), value);
  }
  async function reviewReady(region, heading, button) {
    await region.waitFor(); assert.equal(await region.getByRole('heading', { name: heading, exact: true }).evaluate(element => element === document.activeElement), true);
    assert.equal(await region.getByRole('textbox').count(), 0); assert.equal(await region.getByRole('button', { name: button, exact: true }).isDisabled(), true);
    await region.getByRole('checkbox', { name: consent, exact: true }).check();
  }
  assert.notEqual(config.makerId, config.checkerId); assert.notEqual(config.trustedChannelReference, config.details.contactValue);
  await page.goto(origin.origin + path); await choose(page); await page.getByText('Belum ada usulan pemasok.', { exact: true }).waitFor();
  const readBody = { purpose: 'supplier-review', access: 'propose', owner };
  for (const [body, options, expected] of [[readBody, { omit: ['X-CSRF-Token'] }, 403], [readBody, { omit: ['X-Expected-Actor-Id'] }, 400],
    [{ ...readBody, purpose: 'procurement-request-review' }, {}, 400], [{ ...readBody, owner: { ...owner, shopId: Number(owner.shopId) } }, {}, 400], [{ ...readBody, bankAccount: 'not-authorized' }, {}, 400]])
    assert.equal((await query(page, config.makerId, api + '/proposals/query', body, options)).status, expected);
  await page.getByRole('button', { name: 'Ajukan pemasok', exact: true }).click(); assert.equal(await page.getByLabel('Kebijakan verifikasi pemasok', { exact: true }).inputValue(), '');
  await page.getByLabel('Kebijakan verifikasi pemasok', { exact: true }).selectOption(config.policyId);
  await fillDetails(page.getByRole('region', { name: 'Identitas pemasok yang diusulkan', exact: true }));
  const mappingRegion = page.getByRole('region', { name: 'Tambah pemetaan sumber', exact: true });
  assert.equal(await mappingRegion.getByLabel('Gudang untuk pemetaan', { exact: true }).inputValue(), '');
  await mappingRegion.getByLabel('Gudang untuk pemetaan', { exact: true }).selectOption(config.warehouseId);
  await mappingRegion.getByLabel('Cari barang atau kemasan', { exact: true }).fill(config.skuCode); await mappingRegion.getByRole('button', { name: 'Cari kemasan', exact: true }).click();
  await mappingRegion.getByRole('button', { name: new RegExp(config.skuCode + ' · ' + config.packageLabel) }).click();
  for (const label of ['Sistem sumber', 'Referensi akun sumber', 'Kode barang sumber', 'Berlaku mulai (WIB)', 'Berlaku hingga (WIB)']) assert.equal(await mappingRegion.getByLabel(label, { exact: true }).inputValue(), '');
  await mappingRegion.getByLabel('Sistem sumber', { exact: true }).selectOption('supplier'); await mappingRegion.getByLabel('Referensi akun sumber', { exact: true }).fill('000001');
  await mappingRegion.getByLabel('Kode barang sumber', { exact: true }).fill('000012');
  const wib = iso => new Date(Date.parse(iso) + 7 * 60 * 60 * 1000).toISOString().slice(0, 16);
  await mappingRegion.getByLabel('Berlaku mulai (WIB)', { exact: true }).fill(wib(config.validFrom)); await mappingRegion.getByLabel('Berlaku hingga (WIB)', { exact: true }).fill(wib(config.validUntil));
  await mappingRegion.getByRole('button', { name: 'Tambahkan pemetaan', exact: true }).click();
  await page.getByLabel('Alasan usulan', { exact: true }).fill('Usulan pemasok sintetis dan pemetaan katalog untuk pemeriksaan independen');
  await page.getByRole('button', { name: 'Periksa usulan pemasok', exact: true }).click();
  const review = page.getByRole('region', { name: 'Rincian usulan pemasok', exact: true }); await reviewReady(review, 'Pastikan usulan pemasok', 'Kirim usulan pemasok'); await facts(review);
  await context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await review.getByRole('button', { name: 'Kirim usulan pemasok', exact: true }).isDisabled(), true); assert.equal(supplierWrites.length, 0);
  await context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'supplier-proposal-mobile.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'supplier-proposal-desktop.png'), fullPage: true });
  dropNext(); await review.getByRole('button', { name: 'Kirim usulan pemasok', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(completed().length, 1); const proposed = completed()[0]; assert.equal(proposed.path, api + '/proposals'); assert.equal(proposed.actor, config.makerId);
  const mapping = { location, sourceSystem: 'supplier', sourceAccountReference: '000001', externalItemCode: '000012', skuId: config.skuId,
    expectedSkuRevision: config.skuRevision, definitionId: config.definitionId, definitionVersion: config.definitionVersion, validFrom: config.validFrom, validUntil: config.validUntil };
  assert.deepEqual(proposed.body.input, { owner, supplierId: null, baseVersion: '0', policyId: config.policyId, policyRevision: config.policyRevision, details: config.details, mappings: [mapping] });
  const saved = await pending(page, proposed, 'procurement.supplier.propose');
  await context.addCookies([{ name: '__Host-ut_session', value: config.checkerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('heading', { name: /^(Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor(); assert.deepEqual(await references(page), saved);
  await context.addCookies([{ name: '__Host-ut_session', value: config.makerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await recover(page); assert.equal(supplierWrites.length, 1);
  const history = await query(page, config.makerId, api + '/proposals/query', readBody); assert.equal(history.status, 200); assert.equal(history.body.items.length, 1);
  const proposal = history.body.items[0]; assert.equal(proposal.state, 'pending'); assert.equal(proposal.observation, null);
  const row = current => current.getByRole('button', { name: new RegExp('Usulan ' + proposal.id + '$') }), proposalPath = api + '/proposals/' + proposal.id;
  await row(page).click(); assert.equal(await page.getByRole('form', { name: 'Pemeriksaan pemasok', exact: true }).count(), 0);
  const checker = await user(config.checkerToken); await checker.page.goto(origin.origin + path); await choose(checker.page); await row(checker.page).click();
  const observed = checker.page.getByRole('form', { name: 'Pemeriksaan pemasok', exact: true }); await observed.waitFor();
  assert.equal(await checker.page.getByRole('button', { name: 'Tinjau persetujuan pemasok', exact: true }).isDisabled(), true);
  await fillDetails(observed); assert.equal(await observed.getByLabel('Referensi konfirmasi', { exact: true }).inputValue(), '');
  await observed.getByLabel('Referensi konfirmasi', { exact: true }).fill(config.sourceReference); await observed.getByLabel('Catatan pemeriksaan', { exact: true }).fill('Pengamatan sintetis independen melalui kanal kebijakan');
  await observed.getByRole('checkbox', { name: 'Saya memeriksa langsung kanal tepercaya pada kebijakan dan mencatat hasilnya secara independen.', exact: true }).check();
  await observed.getByRole('button', { name: 'Periksa hasil pengamatan', exact: true }).click();
  const observationReview = checker.page.getByRole('region', { name: 'Rincian hasil pengamatan', exact: true }); await reviewReady(observationReview, 'Pastikan hasil pengamatan', 'Simpan hasil pengamatan');
  const observationText = await observationReview.innerText(); for (const value of [config.sourceReference, config.trustedChannelReference, config.details.contactValue]) assert.ok(observationText.includes(value));
  const observationProbeKey = 'browser-supplier-observation-missing-precondition';
  assert.equal((await query(checker.page, config.checkerId, proposalPath + '/observations', { observed: config.details, policyId: config.policyId, policyRevision: config.policyRevision,
    trustedChannelReference: config.trustedChannelReference, sourceReference: config.sourceReference, attested: true, reason: 'Synthetic missing precondition probe' }, { headers: { 'Idempotency-Key': observationProbeKey } })).status, 400);
  dropNext(); await observationReview.getByRole('button', { name: 'Simpan hasil pengamatan', exact: true }).click(); await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(completed().length, 2); const observationWrite = completed()[1]; assert.equal(observationWrite.path, proposalPath + '/observations'); assert.equal(observationWrite.version, '"1"');
  assert.deepEqual(observationWrite.body.observed, config.details); assert.equal(observationWrite.body.trustedChannelReference, config.trustedChannelReference); assert.equal(observationWrite.body.attested, true);
  await pending(checker.page, observationWrite, 'procurement.supplier.observe'); await recover(checker.page); assert.equal(completed().length, 2); await row(checker.page).click();
  await checker.page.getByText('Rincian pengamatan sama persis dengan usulan.', { exact: true }).waitFor(); assert.equal(await checker.page.getByRole('form', { name: 'Pemeriksaan pemasok', exact: true }).count(), 0);
  await checker.page.getByLabel('Alasan keputusan', { exact: true }).fill('Identitas dan pemetaan sintetis sesuai hasil pengamatan independen'); await checker.page.getByRole('button', { name: 'Tinjau persetujuan pemasok', exact: true }).click();
  const decision = checker.page.getByRole('region', { name: 'Rincian keputusan pemasok', exact: true }); await reviewReady(decision, 'Pastikan keputusan pemasok', 'Setujui pemasok'); await facts(decision);
  const read = await query(checker.page, config.checkerId, proposalPath + '/query', { purpose: 'supplier-review', access: 'approve' }); assert.equal(read.status, 200); assert.equal(read.body.state, 'pending');
  const preview = await query(checker.page, config.checkerId, proposalPath + '/decision-preview/query', { purpose: 'supplier-review', decision: 'approve', observationId: read.body.observation.id }); assert.equal(preview.status, 200);
  const decisionProbeKey = 'browser-supplier-decision-missing-precondition';
  assert.equal((await query(checker.page, config.checkerId, proposalPath + '/decisions', { decision: 'approve', observationId: read.body.observation.id, binding: preview.body.binding,
    reason: 'Synthetic missing precondition probe' }, { headers: { 'Idempotency-Key': decisionProbeKey } })).status, 400);
  dropNext(); await decision.getByRole('button', { name: 'Setujui pemasok', exact: true }).click(); await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(completed().length, 3); const approved = completed()[2]; assert.equal(approved.path, proposalPath + '/decisions'); assert.equal(approved.version, '"1"'); assert.equal(approved.actor, config.checkerId);
  await pending(checker.page, approved, 'procurement.supplier.approve'); await recover(checker.page); assert.equal(completed().length, 3); assert.equal(dropped(), 3); await row(checker.page).click();
  await checker.page.getByRole('heading', { name: 'Disetujui', exact: true }).waitFor(); await facts(checker.page.getByRole('region', { name: 'Rincian pemasok', exact: true }));
  assert.equal(await checker.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await checker.page.screenshot({ path: resolve(directory, 'supplier-approved-mobile.png'), fullPage: true });
  const current = await query(checker.page, config.checkerId, api + '/current/query', { purpose: 'supplier-review', access: 'approve', supplierId: proposal.supplierId });
  assert.equal(current.status, 200); assert.equal(current.body.version, '1'); assert.equal(current.body.proposal.id, proposal.id);
  assert.equal(supplierRecoveries.length, 4); assert.equal(supplierRecoveries[0].status, 409);
  for (const [index, write] of [proposed, observationWrite, approved].entries()) { assert.equal(supplierRecoveries[index + 1].status, 200); assert.equal(supplierRecoveries[index + 1].body.key, write.key); }
  const wrong = await query(checker.page, config.checkerId, api + '/operations/query', { purpose: 'operation-recovery', operation: 'procurement.supplier.approve', key: proposed.key });
  assert.equal(wrong.status, 200); assert.deepEqual(wrong.body, { status: 'not_confirmed', result: null });
  const reader = await user(config.readerToken); await reader.page.goto(origin.origin + path); await reader.page.getByRole('heading', { name: /^(Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor();
  const choices = await query(reader.page, config.readerId, api + '/choices/query', { purpose: 'supplier-selection', location }); assert.equal(choices.status, 200); assert.equal(choices.body.items.length, 1);
  const choice = choices.body.items[0]; assert.equal(choice.supplierId, proposal.supplierId); assert.equal(choice.displayName, config.details.displayName);
  const selectedMapping = choice.mappings[0].mapping;
  assert.deepEqual({ ...selectedMapping, validFrom: new Date(selectedMapping.validFrom).toISOString(), validUntil: new Date(selectedMapping.validUntil).toISOString() }, mapping);
  const narrow = JSON.stringify(choices.body); for (const secret of [config.details.contactValue, config.details.address, config.details.contactName, config.trustedChannelReference, config.sourceReference, 'approvalReference']) assert.ok(!narrow.includes(secret));
  assert.equal((await query(reader.page, config.readerId, proposalPath + '/query', { purpose: 'supplier-review', access: 'approve' })).status, 403);
  const outsider = await user(config.outsiderToken); await outsider.page.goto(origin.origin + '/__supplier_scope_probe');
  const foreign = await query(outsider.page, config.outsiderId, proposalPath + '/query', { purpose: 'supplier-review', access: 'approve' }); assert.equal(foreign.status, 403); assert.equal(foreign.body.code, 'scope_denied');
  for (const field of ['input', 'source', 'mappingSources', 'policy', 'observation']) assert.equal(Object.hasOwn(foreign.body, field), false);
  assert.equal(supplierWrites.length, 5); assert.equal(completed().length, 3); const failed = supplierWrites.filter(write => write.status !== 200); assert.equal(failed.length, 2);
  assert.deepEqual(failed.map(write => write.key), [observationProbeKey, decisionProbeKey]); for (const write of failed) { assert.equal(write.status, 400); assert.equal(write.version, undefined); }
  console.log('REAL_SUPPLIER_BROWSER_PASS ' + JSON.stringify({ writes: completed().length, rejectedCommands: failed.length, originalReplyLosses: dropped(),
    independentBlankObservation: true, approvedSyntheticPolicy: true, realCatalogMappings: true, privateContactRedacted: true, exactActorRecovery: true, offlineUnsent: true, mobile: true, desktop: true }));
}
