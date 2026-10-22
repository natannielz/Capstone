// Real production Next/HTTPS/Kestrel/Mongo; bank policies and observed facts are explicit synthetic local inputs.
import assert from 'node:assert/strict';
import { resolve } from 'node:path';

export async function verifySupplierBankBrowser({ config, maker, user, origin, directory, supplierBankWrites, supplierBankRecoveries, dropNext, dropped }) {
  const { page, context } = maker, path = '/procurement/supplier-banks', api = '/api/v1/procurement/suppliers/bank-beneficiaries';
  const owner = { organizationId: config.organizationId, shopId: config.shopId, channelId: config.channelId }, canonical = Object.values(owner).join(':');
  const completed = () => supplierBankWrites.filter(write => write.status === 200), purpose = 'supplier-bank-review';
  const consent = 'Saya telah memeriksa rincian rekening dan dasar pemeriksaan sebelum mencatat tindakan ini.';
  const fields = { bankCode: 'Kode bank', accountNumber: 'Nomor rekening', holderName: 'Nama pemilik rekening' };
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
    await select.selectOption(canonical); await current.getByRole('heading', { name: 'Usulan rekening pemasok', exact: true }).waitFor();
    await current.getByRole('region', { name: 'Rincian rekening pemasok', exact: true }).getByRole('heading', { name: config.shopName, exact: true }).waitFor();
  }
  async function fillDetails(region, proposed = false) {
    for (const [field, label] of Object.entries(fields)) {
      const input = region.getByLabel(label, { exact: true });
      if (proposed && field === 'bankCode') { assert.equal(await input.inputValue(), config.details.bankCode); assert.equal(await input.getAttribute('readonly'), ''); }
      else { assert.equal(await input.inputValue(), ''); await input.fill(config.details[field]); }
    }
  }
  async function pending(current, write, operation) {
    const saved = await references(current); assert.equal(saved.length, 1); assert.deepEqual(Object.keys(saved[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.equal(saved[0].actorId, write.actor); assert.equal(saved[0].key, write.key); assert.equal(saved[0].operation, operation);
    for (const secret of [config.details.accountNumber, config.details.holderName, config.trustedChannelReference, config.sourceReference, write.body.reason]) assert.ok(!JSON.stringify(saved).includes(secret));
    assert.equal(await current.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0); return saved;
  }
  async function recover(current) {
    await current.reload(); await current.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
    await current.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden' }); assert.equal((await references(current)).length, 0); await choose(current);
  }
  async function facts(region) {
    const text = await region.innerText();
    for (const value of [config.organizationName, config.shopName, config.channelName, config.supplierName, config.supplierSubject,
      config.trustedChannelReference, 'SYNTHETIC-BANK-AUTHORIZATION', 'SYNTHETIC-BANK-POLICY-APPROVAL', ...Object.values(config.details)]) assert.ok(text.includes(value), value);
  }
  async function reviewReady(region, heading, button) {
    await region.getByRole('heading', { name: heading, exact: true }).waitFor();
    assert.equal(await region.getByRole('heading', { name: heading, exact: true }).evaluate(element => element === document.activeElement), true);
    assert.equal(await region.getByRole('textbox').count(), 0); assert.equal(await region.getByRole('button', { name: button, exact: true }).isDisabled(), true);
    await region.getByRole('checkbox', { name: consent, exact: true }).check();
  }
  assert.notEqual(config.makerId, config.checkerId); assert.ok(config.details.accountNumber.startsWith('0000'));
  await page.goto(origin.origin + path); await choose(page); await page.getByText('Belum ada usulan rekening pemasok.', { exact: true }).waitFor();
  const readBody = { purpose, owner };
  for (const [body, options, expected] of [[readBody, { omit: ['X-CSRF-Token'] }, 403], [readBody, { omit: ['X-Expected-Actor-Id'] }, 400],
    [{ ...readBody, purpose: 'supplier-review' }, {}, 400], [{ ...readBody, owner: { ...owner, shopId: Number(owner.shopId) } }, {}, 400], [{ ...readBody, transferAllowed: true }, {}, 400]])
    assert.equal((await query(page, config.makerId, api + '/proposals/query', body, options)).status, expected);
  await page.getByRole('button', { name: 'Ajukan rekening pemasok', exact: true }).click(); await page.getByRole('heading', { name: 'Ajukan rekening pemasok', exact: true }).waitFor();
  assert.equal(await page.getByLabel('Pemasok yang disetujui', { exact: true }).inputValue(), '');
  await page.getByLabel('Pemasok yang disetujui', { exact: true }).selectOption(config.supplierId);
  const policySelect = page.getByLabel('Kebijakan pemeriksaan bank', { exact: true }); await policySelect.getByRole('option', { name: /SYNTHETIC-BANK-POLICY-APPROVAL/ }).waitFor({ state: 'attached' });
  assert.equal(await policySelect.inputValue(), ''); await policySelect.selectOption(config.policyId);
  await fillDetails(page.getByRole('region', { name: 'Rincian rekening yang diusulkan', exact: true }), true);
  await page.getByLabel('Alasan usulan rekening', { exact: true }).fill('Usulan rekening sintetis untuk pemeriksaan bank independen');
  await page.getByRole('button', { name: 'Periksa usulan rekening', exact: true }).click();
  const review = page.getByRole('region', { name: 'Rincian usulan rekening', exact: true }); await reviewReady(review, 'Pastikan usulan rekening', 'Kirim usulan rekening'); await facts(review);
  await context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await review.getByRole('button', { name: 'Kirim usulan rekening', exact: true }).isDisabled(), true); assert.equal(supplierBankWrites.length, 0);
  await context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'supplier-bank-proposal-mobile.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'supplier-bank-proposal-desktop.png'), fullPage: true });
  const expectedInput = { owner, supplierId: config.supplierId, supplierVersionId: config.supplierVersionId, supplierVersion: config.supplierVersion,
    baseVersion: '0', policyId: config.policyId, policyRevision: config.policyRevision, details: config.details };
  for (const input of [{ ...expectedInput, supplierId: Number(config.supplierId) }, { ...expectedInput, details: { ...config.details, accountNumber: Number(config.details.accountNumber) } },
    { ...expectedInput, details: { ...config.details, currency: 'USD' } }]) assert.equal((await query(page, config.makerId, api + '/preview/query', { purpose, input })).status, 400);
  const aged = await user(config.agedToken); await aged.page.goto(origin.origin + path);
  const agedResult = await query(aged.page, config.makerId, api + '/preview/query', { purpose, input: expectedInput }); assert.equal(agedResult.status, 403); assert.equal(agedResult.body.code, 'step_up_required');
  dropNext(); await review.getByRole('button', { name: 'Kirim usulan rekening', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(completed().length, 1); const proposed = completed()[0]; assert.equal(proposed.path, api + '/proposals'); assert.equal(proposed.actor, config.makerId); assert.deepEqual(proposed.body.input, expectedInput);
  const saved = await pending(page, proposed, 'procurement.supplierbank.propose');
  await context.addCookies([{ name: '__Host-ut_session', value: config.checkerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('heading', { name: /^(Masuk untuk melanjutkan|Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor(); assert.deepEqual(await references(page), saved);
  await context.addCookies([{ name: '__Host-ut_session', value: config.makerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await recover(page); assert.equal(supplierBankWrites.length, 1);
  const history = await query(page, config.makerId, api + '/proposals/query', readBody); assert.equal(history.status, 200); assert.equal(history.body.items.length, 1);
  const proposal = history.body.items[0]; assert.equal(proposal.state, 'pending'); assert.equal(proposal.details.maskedAccount, '********5678');
  const row = current => current.getByRole('button', { name: new RegExp('Usulan ' + proposal.id + '$') }), proposalPath = api + '/proposals/' + proposal.id;
  await row(page).click(); await page.getByRole('heading', { name: 'Menunggu pemeriksaan', exact: true }).waitFor(); assert.equal(await page.getByRole('form', { name: 'Pemeriksaan bank independen', exact: true }).count(), 0);
  const checker = await user(config.checkerToken); await checker.page.goto(origin.origin + path); await choose(checker.page); await row(checker.page).click();
  await checker.page.getByRole('heading', { name: 'Menunggu pemeriksaan', exact: true }).waitFor();
  const observed = checker.page.getByRole('form', { name: 'Pemeriksaan bank independen', exact: true }); await observed.waitFor();
  assert.equal(await checker.page.getByRole('button', { name: 'Tinjau persetujuan rekening', exact: true }).isDisabled(), true);
  await fillDetails(observed); assert.equal(await observed.getByLabel('Referensi konfirmasi bank', { exact: true }).inputValue(), '');
  await observed.getByLabel('Referensi konfirmasi bank', { exact: true }).fill(config.sourceReference); await observed.getByLabel('Catatan pemeriksaan bank', { exact: true }).fill('Pemeriksaan sintetis melalui kanal bank yang disetujui');
  await observed.getByRole('checkbox', { name: 'Saya memeriksa langsung kanal bank yang disetujui dan mencatat hasilnya secara independen.', exact: true }).check();
  await observed.getByRole('button', { name: 'Periksa hasil pemeriksaan bank', exact: true }).click();
  const observationReview = checker.page.getByRole('region', { name: 'Rincian pemeriksaan bank', exact: true }); await reviewReady(observationReview, 'Pastikan hasil pemeriksaan bank', 'Simpan pemeriksaan bank');
  const observationText = await observationReview.innerText(); for (const value of [config.sourceReference, config.trustedChannelReference, ...Object.values(config.details)]) assert.ok(observationText.includes(value));
  const observationPreview = await query(checker.page, config.checkerId, proposalPath + '/observation-preview/query', { purpose, observed: config.details, sourceReference: config.sourceReference }); assert.equal(observationPreview.status, 200);
  const observationProbeKey = 'browser-bank-observation-missing-precondition';
  assert.equal((await query(checker.page, config.checkerId, proposalPath + '/observations', { observed: config.details, sourceReference: config.sourceReference,
    binding: observationPreview.body.binding, attested: true, reason: 'Synthetic missing precondition probe' }, { headers: { 'Idempotency-Key': observationProbeKey } })).status, 400);
  dropNext(); await observationReview.getByRole('button', { name: 'Simpan pemeriksaan bank', exact: true }).click(); await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(completed().length, 2); const observationWrite = completed()[1]; assert.equal(observationWrite.path, proposalPath + '/observations'); assert.equal(observationWrite.version, '"1"');
  assert.deepEqual(observationWrite.body.observed, config.details); assert.equal(observationWrite.body.sourceReference, config.sourceReference); assert.equal(observationWrite.body.attested, true);
  await pending(checker.page, observationWrite, 'procurement.supplierbank.observe'); await recover(checker.page); assert.equal(completed().length, 2); await row(checker.page).click();
  await checker.page.getByRole('heading', { name: 'Menunggu pemeriksaan', exact: true }).waitFor(); await checker.page.getByText('Rincian pemeriksaan sama persis dengan usulan.', { exact: true }).waitFor();
  assert.equal(await checker.page.getByRole('form', { name: 'Pemeriksaan bank independen', exact: true }).count(), 0);
  await checker.page.getByLabel('Alasan keputusan rekening', { exact: true }).fill('Rincian bank sintetis sama persis dengan pemeriksaan independen'); await checker.page.getByRole('button', { name: 'Tinjau persetujuan rekening', exact: true }).click();
  const decision = checker.page.getByRole('region', { name: 'Rincian keputusan rekening', exact: true }); await reviewReady(decision, 'Pastikan keputusan rekening', 'Setujui rekening pemasok'); await facts(decision);
  const read = await query(checker.page, config.checkerId, proposalPath + '/query', { purpose }); assert.equal(read.status, 200); assert.equal(read.body.state, 'pending');
  const preview = await query(checker.page, config.checkerId, proposalPath + '/decision-preview/query', { purpose, decision: 'approve', observationId: read.body.observation.id }); assert.equal(preview.status, 200);
  const decisionProbeKey = 'browser-bank-decision-missing-precondition';
  assert.equal((await query(checker.page, config.checkerId, proposalPath + '/decisions', { decision: 'approve', observationId: read.body.observation.id, binding: preview.body.binding,
    reason: 'Synthetic missing precondition probe' }, { headers: { 'Idempotency-Key': decisionProbeKey } })).status, 400);
  dropNext(); await decision.getByRole('button', { name: 'Setujui rekening pemasok', exact: true }).click(); await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(completed().length, 3); const approved = completed()[2]; assert.equal(approved.path, proposalPath + '/decisions'); assert.equal(approved.version, '"1"'); assert.equal(approved.actor, config.checkerId);
  await pending(checker.page, approved, 'procurement.supplierbank.approve'); await recover(checker.page); assert.equal(completed().length, 3); assert.equal(dropped(), 3); await row(checker.page).click();
  await checker.page.getByRole('heading', { name: 'Disetujui', exact: true }).waitFor(); await facts(checker.page.getByRole('region', { name: 'Rincian rekening pemasok', exact: true }));
  assert.equal(await checker.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await checker.page.screenshot({ path: resolve(directory, 'supplier-bank-approved-mobile.png'), fullPage: true });
  assert.equal(supplierBankRecoveries.length, 4); assert.equal(supplierBankRecoveries[0].status, 409);
  for (const [index, write] of [proposed, observationWrite, approved].entries()) { assert.equal(supplierBankRecoveries[index + 1].status, 200); assert.equal(supplierBankRecoveries[index + 1].body.key, write.key); }
  const wrong = await query(checker.page, config.checkerId, api + '/operations/query', { purpose: 'operation-recovery', operation: 'procurement.supplierbank.approve', key: proposed.key });
  assert.equal(wrong.status, 200); assert.deepEqual(wrong.body, { status: 'not_confirmed', result: null });
  const reader = await user(config.readerToken); await reader.page.goto(origin.origin + path); await choose(reader.page); await row(reader.page).click(); await reader.page.getByRole('heading', { name: 'Disetujui', exact: true }).waitFor();
  const maskedText = await reader.page.getByRole('region', { name: 'Rincian rekening pemasok', exact: true }).innerText(); assert.ok(maskedText.includes('********5678'));
  for (const secret of [config.details.accountNumber, config.trustedChannelReference, config.sourceReference]) assert.ok(!maskedText.includes(secret));
  assert.equal(await reader.page.getByRole('button', { name: 'Ajukan rekening pemasok', exact: true }).count(), 0);
  const masked = await query(reader.page, config.readerId, api + '/current/query', { purpose, supplierId: config.supplierId }); assert.equal(masked.status, 200); assert.equal(masked.body.version, '1'); assert.equal(masked.body.eligible, true);
  assert.equal(masked.body.details.maskedAccount, '********5678'); for (const field of ['accountNumber', 'holderName']) assert.equal(Object.hasOwn(masked.body.details, field), false);
  assert.equal((await query(reader.page, config.readerId, proposalPath + '/query', { purpose })).status, 403);
  const detailsReader = await user(config.detailsReaderToken); await detailsReader.page.goto(origin.origin + path); await choose(detailsReader.page); await row(detailsReader.page).click(); await detailsReader.page.getByRole('heading', { name: 'Disetujui', exact: true }).waitFor();
  await facts(detailsReader.page.getByRole('region', { name: 'Rincian rekening pemasok', exact: true })); assert.equal(await detailsReader.page.getByRole('button', { name: 'Ajukan rekening pemasok', exact: true }).count(), 0);
  const outsider = await user(config.outsiderToken); await outsider.page.goto(origin.origin + '/__supplier_bank_scope_probe');
  const foreign = await query(outsider.page, config.outsiderId, proposalPath + '/query', { purpose }); assert.equal(foreign.status, 403); assert.equal(foreign.body.code, 'scope_denied');
  for (const field of ['input', 'source', 'supplier', 'policy', 'observation']) assert.equal(Object.hasOwn(foreign.body, field), false);
  assert.equal(supplierBankWrites.length, 5); assert.equal(completed().length, 3); const failed = supplierBankWrites.filter(write => write.status !== 200); assert.equal(failed.length, 2);
  assert.deepEqual(failed.map(write => write.key), [observationProbeKey, decisionProbeKey]); for (const write of failed) { assert.equal(write.status, 400); assert.equal(write.version, undefined); }
  console.log('REAL_SUPPLIER_BANK_BROWSER_PASS ' + JSON.stringify({ writes: completed().length, rejectedCommands: failed.length, originalReplyLosses: dropped(),
    independentBlankObservation: true, explicitSyntheticBankPolicy: true, genuineSupplierHistory: true, leadingZeroesPreserved: true, maskedReadPrivateDetails: true, exactActorRecovery: true, staleFactorDenied: true, offlineUnsent: true, mobile: true, desktop: true }));
}
