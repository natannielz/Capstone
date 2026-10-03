// Real production Next/HTTPS/Kestrel/Mongo, retained private original, and actual imported bank debit. Inputs are synthetic local evidence.
import assert from 'node:assert/strict';
import { resolve } from 'node:path';

export async function verifyProcurementSettlementBrowser({ config, verifier, user, origin, directory, settlementWrites, settlementRecoveries, originalReads, dropNext, dropped }) {
  const { page, context } = verifier, path = '/procurement/settlements', api = '/api/v1/procurement/settlements', purpose = 'settlement-review';
  const location = { organizationId: config.organizationId, shopId: config.shopId, warehouseId: config.warehouseId, channelId: config.channelId };
  const ownerPath = api + '/disbursements/' + config.disbursementId, readBody = { purpose, location, beforeId: null };
  const input = { policyId: config.policyId, policyRevision: config.policyRevision, bankImportId: config.importId, bankRowNumber: config.rowNumber,
    observedBeneficiary: { bankCode: config.observedBankCode, accountNumber: config.observedAccountNumber, holderName: config.observedHolderName, currency: 'IDR' },
    beneficiaryVerificationReference: config.beneficiaryVerificationReference, transferVerificationReference: config.transferVerificationReference };
  const reason = 'Pemeriksaan sintetis independen atas bukti asli, penerima, debit bank dan jurnal uang muka pemasok';
  const remaining = deadline => { const timeout = Math.ceil(deadline - performance.now()); assert.ok(timeout > 0, 'Actual settlement action exceeded its bounded verification deadline'); return timeout; };
  const completed = () => settlementWrites.filter(item => item.status === 200);
  const references = current => current.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  const query = (current, actorId, route, body, options = {}) => current.evaluate(async ({ actorId, route, body, options }) => {
    const controller = new AbortController(), timer = setTimeout(() => controller.abort(), 60000);
    try {
      const csrf = await fetch('/api/v1/identity/csrf', { credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: controller.signal });
      if (!csrf.ok) throw new Error('Actual request token unavailable'); const token = await csrf.json();
      const headers = { 'Content-Type': 'application/json', 'X-CSRF-Token': token.requestToken, 'X-Expected-Actor-Id': actorId, ...options.headers };
      for (const name of options.omit ?? []) delete headers[name];
      const response = await fetch(route, { method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', headers, body: JSON.stringify(body), signal: controller.signal });
      const text = await response.text(); return { status: response.status, body: text ? JSON.parse(text) : null, cache: response.headers.get('cache-control') };
    } finally { clearTimeout(timer); }
  }, { actorId, route, body, options });
  async function choose(current, deadline = performance.now() + 60000) {
    await current.getByRole('heading', { name: 'Verifikasi debit pemasok', exact: true }).waitFor({ timeout: remaining(deadline) });
    const select = current.getByLabel('Organisasi / toko / gudang / kanal', { exact: true });
    await select.getByRole('option', { name: [config.organizationName, config.shopName, config.warehouseName, config.channelName].join(' · '), exact: true }).waitFor({ state: 'attached', timeout: remaining(deadline) });
    await select.selectOption(Object.values(location).join(':'), { timeout: remaining(deadline) });
    await current.getByRole('region', { name: 'Rincian verifikasi debit pemasok', exact: true }).getByRole('heading', { name: config.shopName, exact: true }).waitFor({ timeout: remaining(deadline) });
  }
  async function open(current, settled = false) {
    const deadline = performance.now() + 60000;
    await current.getByRole('button').filter({ hasText: config.transferReference }).click({ timeout: remaining(deadline) });
    const detail = current.getByRole('region', { name: 'Rincian verifikasi debit pemasok', exact: true });
    await detail.getByRole('heading', { name: settled ? 'Pencatatan dengan debit terverifikasi' : 'Menunggu verifikasi debit bank', exact: true }).waitFor({ timeout: remaining(deadline) });
    return detail;
  }
  async function recover(write) {
    const started = performance.now(), deadline = started + 60000;
    await page.reload({ timeout: remaining(deadline) });
    await page.getByLabel('Organisasi / toko / gudang / kanal', { exact: true }).getByRole('option', { name: [config.organizationName, config.shopName, config.warehouseName, config.channelName].join(' · '), exact: true }).waitFor({ state: 'attached', timeout: remaining(deadline) });
    const [lookup, refreshed] = await Promise.all([
      page.waitForResponse(response => new URL(response.url()).pathname === api + '/operations/query' && response.request().method() === 'POST' && response.request().postDataJSON()?.key === write.key && response.request().headers()['x-expected-actor-id'] === config.verifierId, { timeout: remaining(deadline) }),
      page.waitForResponse(response => new URL(response.url()).pathname === api + '/targets/query' && response.request().method() === 'POST', { timeout: remaining(deadline) }),
      page.getByRole('button', { name: 'Periksa hasil', exact: true }).click({ timeout: remaining(deadline) })
    ]);
    assert.equal(lookup.status(), 200); assert.equal(refreshed.status(), 200);
    await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden', timeout: remaining(deadline) });
    assert.equal((await references(page)).length, 0); await choose(page, deadline);
    console.log('FIXTURE PROCUREMENT SETTLEMENT RECOVERY', JSON.stringify({ elapsedMs: Math.round(performance.now() - started), originalActor: true, originalKey: true, refreshed: true }));
  }
  assert.notEqual(config.verifierId, config.recorderId); assert.notEqual(config.verifierId, config.importerId);
  await page.goto(origin.origin + path); await choose(page); let detail = await open(page);
  const before = await query(page, config.verifierId, ownerPath + '/query', { purpose }); assert.equal(before.status, 200); assert.equal(before.body.settlement, null); assert.match(before.cache, /no-store/);
  const source = before.body.disbursement; assert.equal(source.id, config.disbursementId); assert.equal(source.amount, config.amount); assert.equal(source.transferDate, config.transferDate); assert.equal(source.original.reference.sha256, config.originalSha256);
  assert.equal(source.authorization.supplier.displayName, config.supplierName); assert.equal(source.authorization.supplier.subjectReference, config.supplierSubject);
  assert.equal(Object.hasOwn(source.beneficiary, 'accountNumber'), false); assert.equal(Object.hasOwn(source.beneficiary, 'holderName'), false); assert.equal(Object.hasOwn(source.payingAccount, 'accountNumber'), false);
  for (const [body, options, status] of [[readBody, { omit: ['X-CSRF-Token'] }, 403], [readBody, { omit: ['X-Expected-Actor-Id'] }, 400], [{ ...readBody, purpose: 'disbursement-review' }, {}, 400], [{ ...readBody, location: { ...location, shopId: Number(location.shopId) } }, {}, 400], [{ ...readBody, unknown: true }, {}, 400]]) assert.equal((await query(page, config.verifierId, api + '/disbursements/query', body, options)).status, status);
  const readHeaders = { headers: { 'If-Match': '"1"' } }, previewBody = { purpose, input };
  assert.equal((await query(page, config.verifierId, ownerPath + '/preview/query', previewBody)).status, 400);
  for (const invalid of [{ ...input, bankImportId: Number(input.bankImportId) }, { ...input, bankRowNumber: '2' }, { ...input, amount: config.amount }]) assert.equal((await query(page, config.verifierId, ownerPath + '/preview/query', { purpose, input: invalid }, readHeaders)).status, 400);
  const aged = await user(config.agedToken); await aged.page.goto(origin.origin + '/__settlement_aged_factor_probe');
  const expired = await query(aged.page, config.verifierId, ownerPath + '/preview/query', previewBody, readHeaders); assert.equal(expired.status, 403); assert.equal(expired.body.code, 'step_up_required');
  for (const [token, actorId] of [[config.recorderToken, config.recorderId], [config.importerToken, config.importerId]]) {
    const independent = await user(token); await independent.page.goto(origin.origin + '/__settlement_independence_probe');
    const denied = await query(independent.page, actorId, ownerPath + '/preview/query', previewBody, readHeaders); assert.equal(denied.status, 403); assert.equal(denied.body.code, 'independent_person_required');
  }
  const outsider = await user(config.outsiderToken); await outsider.page.goto(origin.origin + '/__settlement_scope_probe');
  const denied = await query(outsider.page, config.outsiderId, ownerPath + '/query', { purpose }); assert.equal(denied.status, 403); assert.equal(denied.body.code, 'scope_denied'); assert.equal(Object.hasOwn(denied.body, 'disbursement'), false);

  const selectionDeadline = performance.now() + 60000;
  await detail.getByRole('button', { name: 'Mulai pemeriksaan debit pemasok', exact: true }).click({ timeout: remaining(selectionDeadline) });
  const policy = page.getByLabel('Kebijakan verifikasi Finance', { exact: true }), bankImport = page.getByLabel('Impor mutasi rekening pembayaran', { exact: true });
  await policy.getByRole('option', { name: config.policyApprovalReference, exact: true }).waitFor({ state: 'attached', timeout: remaining(selectionDeadline) }); await policy.selectOption(config.policyId, { timeout: remaining(selectionDeadline) });
  await bankImport.locator('option[value="' + config.importId + '"]').waitFor({ state: 'attached', timeout: remaining(selectionDeadline) });
  assert.ok((await bankImport.locator('option[value="' + config.importId + '"]').innerText()).includes(config.importFilename)); assert.ok((await bankImport.locator('option[value="' + config.importId + '"]').innerText()).includes(config.importProfileName));
  await bankImport.selectOption(config.importId, { timeout: remaining(selectionDeadline) });
  const debit = page.getByLabel('Debit bank yang dicocokkan', { exact: true }); await debit.waitFor({ timeout: remaining(selectionDeadline) }); await debit.selectOption(String(config.rowNumber), { timeout: remaining(selectionDeadline) });
  for (const [label, value] of [['Kode bank penerima yang diamati', config.observedBankCode], ['Nomor rekening penerima yang diamati', config.observedAccountNumber], ['Nama pemilik rekening yang diamati', config.observedHolderName], ['Referensi pemeriksaan penerima pemasok', config.beneficiaryVerificationReference], ['Referensi pemeriksaan transfer dan debit bank', config.transferVerificationReference], ['Alasan verifikasi debit pemasok', reason]]) { assert.equal(await page.getByLabel(label, { exact: true }).inputValue(), ''); await page.getByLabel(label, { exact: true }).fill(value); }
  assert.equal(await page.getByRole('button', { name: 'Tinjau verifikasi debit pemasok', exact: true }).isDisabled(), true);
  const originalPath = '/api/v1/procurement/disbursements/drafts/' + source.original.draftId + '/evidence/' + source.original.reservationId + '/original/query', originalDeadline = performance.now() + 60000;
  const [originalResponse] = await Promise.all([page.waitForResponse(response => new URL(response.url()).pathname === originalPath && response.request().method() === 'POST', { timeout: remaining(originalDeadline) }), page.getByRole('button', { name: 'Lihat bukti asli untuk verifikasi pemasok', exact: true }).click({ timeout: remaining(originalDeadline) })]);
  assert.equal(originalResponse.status(), 200); const image = page.getByRole('img', { name: 'Bukti transfer pemasok asli untuk dibandingkan dengan mutasi bank', exact: true }); await image.waitFor({ timeout: remaining(originalDeadline) });
  const viewed = await image.evaluate(async element => {
    if (!element.complete) await new Promise(resolve => { element.addEventListener('load', resolve, { once: true }); element.addEventListener('error', resolve, { once: true }); });
    if (!element.src.startsWith('blob:')) throw new Error('Private original must remain in a local object URL');
    const response = await fetch(element.src), bytes = await response.arrayBuffer();
    return { width: element.naturalWidth, height: element.naturalHeight, hash: Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)), value => value.toString(16).padStart(2, '0')).join('') };
  });
  assert.equal(viewed.width, config.originalWidth); assert.equal(viewed.height, config.originalHeight); assert.equal(viewed.hash, config.originalSha256);
  assert.equal(originalReads.length, 1); assert.equal(originalReads[0].actor, config.verifierId); assert.equal(originalReads[0].version, '"1"'); assert.deepEqual(originalReads[0].body, { purpose: 'procurement-disbursement-evidence-view', reference: source.original.reference });
  await page.getByRole('button', { name: 'Tutup bukti asli pemasok', exact: true }).click();
  await page.getByRole('button', { name: 'Tinjau verifikasi debit pemasok', exact: true }).click();
  const review = page.getByRole('region', { name: 'Konfirmasi verifikasi debit pemasok', exact: true }); await review.getByRole('heading', { name: 'Pastikan verifikasi debit pemasok', exact: true }).waitFor({ timeout: 60000 });
  assert.equal(await review.getByRole('heading', { name: 'Pastikan verifikasi debit pemasok', exact: true }).evaluate(element => element === document.activeElement), true); assert.equal(await review.getByRole('textbox').count(), 0); assert.equal(await review.getByRole('combobox').count(), 0);
  const reviewText = await review.innerText(); for (const fact of [config.supplierName, config.supplierSubject, config.transferReference, config.transferDate, config.policyApprovalReference, config.trustedBankChannelReference, config.importFilename, config.importProfileName, config.transactionId, config.bookedDate, config.observedBankCode, config.observedAccountNumber, config.observedHolderName, config.beneficiaryVerificationReference, config.transferVerificationReference, config.supplierAdvanceAccountCode, config.cashAccountCode, reason]) assert.ok(reviewText.includes(fact), fact);
  const save = review.getByRole('button', { name: 'Simpan verifikasi debit pemasok', exact: true }); assert.equal(await save.isDisabled(), true);
  const actualPreview = await query(page, config.verifierId, ownerPath + '/preview/query', previewBody, readHeaders); assert.equal(actualPreview.status, 200); assert.equal(actualPreview.body.debit.amount, config.amount); assert.equal(actualPreview.body.debit.transactionId, config.transactionId);
  const missingKey = 'browser-settlement-missing-version'; assert.equal((await query(page, config.verifierId, ownerPath + '/verify', { input, binding: actualPreview.body.binding, attested: true, reason }, { headers: { 'Idempotency-Key': missingKey } })).status, 400);
  await review.getByRole('checkbox').check(); await context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await save.isDisabled(), true); assert.equal(completed().length, 0); await context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'procurement-settlement-review-mobile.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'procurement-settlement-review-desktop.png'), fullPage: true });
  dropNext(); await save.click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ timeout: 60000 }); assert.equal(completed().length, 1);
  const write = completed()[0]; assert.equal(write.path, ownerPath + '/verify'); assert.equal(write.actor, config.verifierId); assert.equal(write.version, '"1"'); assert.deepEqual(write.body.input, input); assert.equal(write.body.attested, true); assert.equal(write.body.reason, reason); assert.match(write.body.binding, /^[0-9a-f]{64}$/);
  const saved = await references(page); assert.equal(saved.length, 1); assert.deepEqual(Object.keys(saved[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.equal(saved[0].actorId, config.verifierId); assert.equal(saved[0].key, write.key); assert.equal(saved[0].operation, 'procurement.settlement.verify');
  for (const secret of [config.observedAccountNumber, config.observedHolderName, config.originalSha256, config.beneficiaryVerificationReference, reason]) assert.ok(!JSON.stringify(saved).includes(secret)); assert.equal(await page.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
  await context.addCookies([{ name: '__Host-ut_session', value: config.recorderToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('heading', { name: 'Periksa sesi dan akses', exact: true }).waitFor({ timeout: 60000 }); assert.deepEqual(await references(page), saved);
  await context.addCookies([{ name: '__Host-ut_session', value: config.verifierToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]); await recover(write); detail = await open(page, true);
  await detail.getByRole('heading', { name: 'Debit bank pemasok terverifikasi', exact: true }).waitFor(); assert.equal(await detail.getByRole('button', { name: 'Mulai pemeriksaan debit pemasok', exact: true }).count(), 0);
  const after = await query(page, config.verifierId, ownerPath + '/query', { purpose }); assert.equal(after.status, 200); assert.deepEqual(after.body.disbursement, source); assert.equal(after.body.settlement.status, 'bank_debit_verified'); assert.equal(after.body.settlement.amount, config.amount); assert.equal(after.body.settlement.debit.transactionId, config.transactionId); assert.match(after.body.settlement.journalId, /^[1-9][0-9]*$/); assert.equal(Object.hasOwn(after.body.settlement, 'observedBeneficiary'), false);
  await page.setViewportSize({ width: 360, height: 800 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'procurement-settlement-saved-mobile.png'), fullPage: true });
  const reader = await user(config.readerToken); await reader.page.goto(origin.origin + path); await choose(reader.page); const readDetail = await open(reader.page, true);
  assert.equal(await readDetail.getByRole('button', { name: 'Lihat bukti asli untuk verifikasi pemasok', exact: true }).count(), 0); assert.equal(await readDetail.getByRole('button', { name: 'Simpan verifikasi debit pemasok', exact: true }).count(), 0); assert.ok(!(await readDetail.innerText()).includes(config.observedAccountNumber));
  assert.equal((await query(reader.page, config.readerId, originalPath, { purpose: 'procurement-disbursement-evidence-view', reference: source.original.reference }, readHeaders)).status, 403);
  assert.equal(completed().length, 1); assert.equal(dropped(), 1); assert.equal(settlementWrites.length, 2); assert.equal(settlementWrites.find(item => item.status !== 200).key, missingKey);
  assert.equal(settlementRecoveries.length, 2); assert.equal(settlementRecoveries[0].status, 409); assert.equal(settlementRecoveries[1].status, 200); for (const item of settlementRecoveries) { assert.equal(item.actor, config.verifierId); assert.deepEqual(item.body, { purpose: 'operation-recovery', operation: 'procurement.settlement.verify', key: write.key }); }
  console.log('REAL_PROCUREMENT_SETTLEMENT_BROWSER_PASS ' + JSON.stringify({ writes: 1, rejectedCommands: 1, lostCommittedReplies: 1, originalActorKeyRecovery: true, independentRecorderAndImporter: true, blankObservations: true, namedBankSources: true, originalDigestChecked: true, exactDebit: true, actualJournalId: after.body.settlement.journalId, originalRecordingPreserved: true, noTransfer: true, readOnlyPrivacy: true, offlineUnsent: true, mobile: true, desktop: true }));
}
