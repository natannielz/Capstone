// Actual production Next/HTTPS/Kestrel/Mongo and native private-file scanner/decoder. All business inputs are synthetic.
import assert from 'node:assert/strict';
import { resolve } from 'node:path';

export async function verifyProcurementDisbursementBrowser({ config, maker, user, origin, directory, disbursementWrites, disbursementRecoveries, disbursementUploads, dropNext, dropped, validateEvidence }) {
  const { page, context } = maker, path = '/procurement/disbursements', api = '/api/v1/procurement/disbursements', purpose = 'disbursement-review';
  const location = { organizationId: config.organizationId, shopId: config.shopId, warehouseId: config.warehouseId, channelId: config.channelId };
  const completed = () => disbursementWrites.filter(item => item.status === 200);
  const remaining = deadline => { const timeout = Math.ceil(deadline - performance.now()); assert.ok(timeout > 0, 'Actual disbursement action exceeded its bounded verification deadline'); return timeout; };
  const references = current => current.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  const query = (current, actorId, route, body, options = {}) => current.evaluate(async ({ actorId, route, body, options }) => {
    const csrf = await fetch('/api/v1/identity/csrf', { credentials: 'same-origin', cache: 'no-store', redirect: 'error' });
    if (!csrf.ok) throw new Error('Actual request token unavailable'); const token = await csrf.json();
    const headers = { 'Content-Type': 'application/json', 'X-CSRF-Token': token.requestToken, 'X-Expected-Actor-Id': actorId, ...options.headers };
    for (const name of options.omit ?? []) delete headers[name];
    const response = await fetch(route, { method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', headers, body: JSON.stringify(body) });
    if (response.ok && response.headers.get('content-type')?.startsWith('image/')) {
      const bytes = await response.arrayBuffer(), hash = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)), value => value.toString(16).padStart(2, '0')).join('');
      return { status: response.status, bytes: bytes.byteLength, hash, cache: response.headers.get('cache-control'), nosniff: response.headers.get('x-content-type-options') };
    }
    const text = await response.text(); return { status: response.status, body: text ? JSON.parse(text) : null };
  }, { actorId, route, body, options });
  async function choose(current, deadline = performance.now() + 60000) {
    await current.getByRole('heading', { name: 'Bukti uang muka pemasok', exact: true }).waitFor({ timeout: remaining(deadline) });
    const select = current.getByLabel('Organisasi / toko / gudang / kanal', { exact: true });
    await select.getByRole('option', { name: [config.organizationName, config.shopName, config.warehouseName, config.channelName].join(' · '), exact: true }).waitFor({ state: 'attached', timeout: remaining(deadline) });
    await select.selectOption(Object.values(location).join(':'), { timeout: remaining(deadline) });
    await current.getByRole('heading', { name: 'Riwayat bukti uang muka', exact: true }).waitFor({ timeout: remaining(deadline) });
    await current.getByRole('region', { name: 'Rincian bukti uang muka', exact: true }).getByRole('heading', { name: config.shopName, exact: true }).waitFor({ timeout: remaining(deadline) });
  }
  async function pending(current, write, operation) {
    const saved = await references(current); assert.equal(saved.length, 1); assert.deepEqual(Object.keys(saved[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.equal(saved[0].actorId, config.makerId); assert.equal(saved[0].key, write.key); assert.equal(saved[0].operation, operation);
    for (const secret of [config.transferReference, config.originalSha256, config.supplierAccount, config.payingAccount]) assert.ok(!JSON.stringify(saved).includes(secret));
    assert.equal(await current.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0); return saved;
  }
  async function recover(write) {
    // Owning lookup, immutable draft read-back and onResolved source refresh are one action.
    const started = performance.now(), deadline = started + 60000;
    await page.reload({ timeout: remaining(deadline) });
    await page.getByLabel('Organisasi / toko / gudang / kanal', { exact: true }).getByRole('option', { name: [config.organizationName, config.shopName, config.warehouseName, config.channelName].join(' · '), exact: true }).waitFor({ state: 'attached', timeout: remaining(deadline) });
    const recoveryPath = write.path.endsWith('/evidence') ? write.path + '/operations/query' : api + '/operations/query';
    const [lookup, refreshed] = await Promise.all([
      page.waitForResponse(response => new URL(response.url()).pathname === recoveryPath && response.request().method() === 'POST' && response.request().postDataJSON()?.key === write.key && response.request().headers()['x-expected-actor-id'] === config.makerId, { timeout: remaining(deadline) }),
      page.waitForResponse(response => new URL(response.url()).pathname === api + '/targets/query' && response.request().method() === 'POST', { timeout: remaining(deadline) }),
      page.getByRole('button', { name: 'Periksa hasil', exact: true }).click({ timeout: remaining(deadline) })
    ]);
    assert.equal(lookup.status(), 200); assert.equal(refreshed.status(), 200);
    await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden', timeout: remaining(deadline) });
    assert.equal((await references(page)).length, 0); await choose(page, deadline);
    console.log('FIXTURE PROCUREMENT DISBURSEMENT RECOVERY', JSON.stringify({ operation: write.path, elapsedMs: Math.round(performance.now() - started), originalKey: true, refreshed: true }));
  }
  async function ready(region, heading, button) {
    await region.getByRole('heading', { name: heading, exact: true }).waitFor();
    assert.equal(await region.getByRole('heading', { name: heading, exact: true }).evaluate(element => element === document.activeElement), true);
    assert.equal(await region.getByRole('textbox').count(), 0); assert.equal(await region.getByRole('button', { name: button, exact: true }).isDisabled(), true);
    const text = await region.innerText();
    for (const fact of [config.supplierName, config.transferDate, config.transferReference, 'SYNTHETIC-DISBURSEMENT-POLICY-APPROVAL', '********5678', '*********6789']) assert.ok(text.includes(fact), fact);
    for (const secret of [config.supplierAccount, config.payingAccount]) assert.ok(!text.includes(secret));
    await region.getByRole('checkbox').check();
  }
  const input = { location, fundingRequestId: config.fundingRequestId, fundingDecisionId: config.fundingDecisionId, authorizationId: config.authorizationId,
    supplierId: config.supplierId, beneficiaryVersionId: config.beneficiaryVersionId, beneficiaryVersion: config.beneficiaryVersion,
    payingAccountId: config.payingAccountId, payingAccountVersionId: config.payingAccountVersionId, policyId: config.policyId, policyRevision: config.policyRevision,
    amount: config.amount, transferDate: config.transferDate, transferReference: config.transferReference };
  await page.goto(origin.origin + path); await choose(page); await page.getByText('Belum ada draf bukti uang muka.', { exact: true }).waitFor();
  const readBody = { purpose, location, beforeId: null };
  for (const [body, options, expected] of [[readBody, { omit: ['X-CSRF-Token'] }, 403], [readBody, { omit: ['X-Expected-Actor-Id'] }, 400],
    [{ ...readBody, purpose: 'funding-review' }, {}, 400], [{ ...readBody, location: { ...location, shopId: Number(location.shopId) } }, {}, 400], [{ ...readBody, executeTransfer: true }, {}, 400]])
    assert.equal((await query(page, config.makerId, api + '/drafts/query', body, options)).status, expected);
  for (const invalid of [{ ...input, amount: Number(input.amount) }, { ...input, authorizationId: Number(input.authorizationId) }, { ...input, bankVerified: true }])
    assert.equal((await query(page, config.makerId, api + '/preview/query', { purpose, input: invalid })).status, 400);
  const aged = await user(config.agedToken); await aged.page.goto(origin.origin + path);
  const expired = await query(aged.page, config.makerId, api + '/preview/query', { purpose, input }); assert.equal(expired.status, 403); assert.equal(expired.body.code, 'step_up_required');
  await page.getByRole('button', { name: 'Buat draf bukti uang muka', exact: true }).click(); await page.getByRole('heading', { name: 'Buat draf bukti uang muka', exact: true }).waitFor();
  const authorization = page.getByLabel('Otorisasi uang muka yang disetujui', { exact: true }); assert.equal(await authorization.inputValue(), ''); await authorization.selectOption(config.authorizationId);
  const policy = page.getByLabel('Kebijakan Finance dan rekening pembayaran', { exact: true }); await policy.getByRole('option', { name: /SYNTHETIC-DISBURSEMENT-POLICY-APPROVAL/ }).waitFor({ state: 'attached' });
  assert.equal(await policy.inputValue(), ''); await policy.selectOption(config.policyId);
  for (const [label, value] of [['Nominal transfer yang dicatat (IDR)', config.amount], ['Tanggal transfer', config.transferDate], ['Referensi transfer', config.transferReference], ['Alasan draf bukti', 'Draf sintetis berdasarkan otorisasi dan rekening yang disetujui']]) {
    assert.equal(await page.getByLabel(label, { exact: true }).inputValue(), ''); await page.getByLabel(label, { exact: true }).fill(value);
  }
  await page.getByRole('button', { name: 'Periksa draf bukti uang muka', exact: true }).click();
  const draftReview = page.getByRole('region', { name: 'Rincian draf bukti uang muka', exact: true }); await ready(draftReview, 'Pastikan draf bukti uang muka', 'Simpan draf bukti uang muka');
  dropNext(); await draftReview.getByRole('button', { name: 'Simpan draf bukti uang muka', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(completed().length, 1); const draftWrite = completed()[0]; assert.deepEqual(draftWrite.body.input, input); const saved = await pending(page, draftWrite, 'procurement.disbursement.draft');
  await context.addCookies([{ name: '__Host-ut_session', value: config.evidenceReaderToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('heading', { name: /^(Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor(); assert.deepEqual(await references(page), saved);
  await context.addCookies([{ name: '__Host-ut_session', value: config.makerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await recover(draftWrite); assert.equal(completed().length, 1);
  const list = await query(page, config.makerId, api + '/drafts/query', readBody); assert.equal(list.status, 200); assert.equal(list.body.items.length, 1);
  const draft = list.body.items[0], draftPath = api + '/drafts/' + draft.id;
  assert.equal(draft.state, 'draft'); assert.equal(draft.capacity.recordedAmount, '0.00'); assert.equal(draft.capacity.remainingAmount, '10000.00');
  const open = async (current, heading = 'Draf menunggu bukti') => { const deadline = performance.now() + 60000; await current.getByRole('button', { name: new RegExp('Draf ' + draft.id + '$') }).click({ timeout: remaining(deadline) }); await current.getByRole('heading', { name: heading, exact: true }).waitFor({ timeout: remaining(deadline) }); };
  await open(page); const evidence = page.getByRole('region', { name: 'Bukti asli uang muka', exact: true });
  await evidence.getByLabel('Alasan pencatatan bukti', { exact: true }).fill('Bukti privat sintetis untuk pemeriksaan debit bank independen');
  await evidence.getByLabel('Gambar bukti transfer uang muka asli', { exact: true }).setInputFiles(config.imagePath);
  dropNext(); await evidence.getByRole('button', { name: 'Unggah bukti uang muka asli', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(completed().length, 2); const reserved = completed()[1]; assert.equal(reserved.path, draftPath + '/evidence'); await pending(page, reserved, 'evidence.procurement-disbursement-image.reserve');
  await recover(reserved); await open(page); assert.equal(completed().length, 2);
  await evidence.getByLabel('Alasan pencatatan bukti', { exact: true }).fill('Bukti privat sintetis untuk pemeriksaan debit bank independen');
  await evidence.getByLabel('Gambar bukti transfer uang muka asli', { exact: true }).setInputFiles(config.imagePath);
  const continueUpload = evidence.getByRole('button', { name: /^Lanjutkan unggah bukti / });
  const reservationMatch = /^Lanjutkan unggah bukti ([1-9][0-9]*)$/.exec(await continueUpload.innerText()); assert.ok(reservationMatch);
  const contentPath = draftPath + '/evidence/' + reservationMatch[1] + '/content', uploadStarted = performance.now();
  // The actual fixture writer policy allows 120 seconds across the guarded upload stages.
  const [uploadResponse] = await Promise.all([page.waitForResponse(response => new URL(response.url()).pathname === contentPath && response.request().method() === 'POST', { timeout: 120000 }), continueUpload.click()]);
  assert.equal(uploadResponse.status(), 200); assert.equal(await uploadResponse.finished(), null);
  const uploadReceipts = disbursementUploads.filter(item => item.path === contentPath); assert.equal(uploadReceipts.length, 1);
  const uploadReceipt = uploadReceipts[0]; assert.equal(uploadReceipt.completed, true); assert.equal(uploadReceipt.status, 200);
  assert.equal(uploadReceipt.error, undefined); assert.equal(uploadReceipt.actor, config.makerId); assert.equal(uploadReceipt.key, 'upload.' + reservationMatch[1]);
  assert.ok(uploadReceipt.bodyBytes > 0 && uploadReceipt.bodyBytes <= 4096); const uploadResult = uploadReceipt.body;
  assert.equal(uploadResult.state, 'quarantined'); assert.equal(uploadResult.reference.sha256, config.originalSha256);
  console.log('FIXTURE PROCUREMENT DISBURSEMENT UPLOAD', JSON.stringify({ elapsedMs: Math.round(performance.now() - uploadStarted), status: uploadResponse.status(), state: uploadResult.state }));
  await evidence.getByText('Bukti ' + reservationMatch[1] + ' · Tersimpan · menunggu pemeriksaan keamanan', { exact: true }).waitFor();
  assert.equal(completed().length, 2); await validateEvidence(); await evidence.getByRole('button', { name: 'Muat status bukti uang muka', exact: true }).click();
  await evidence.getByText('Bukti ' + reservationMatch[1] + ' · Pemeriksaan keamanan selesai. Bukti tetap dalam draf; pembayaran belum diverifikasi.', { exact: true }).waitFor(); const statuses = await query(page, config.makerId, draftPath + '/evidence/query', { purpose: 'procurement-disbursement-evidence', beforeId: null });
  assert.equal(statuses.status, 200); assert.equal(statuses.body.items.length, 1); const admitted = statuses.body.items[0]; assert.equal(admitted.reference.sha256, config.originalSha256);
  assert.equal(admitted.reservationId, reservationMatch[1]); assert.equal(admitted.state, 'quarantined'); assert.equal(admitted.scanStatus, 'clean'); assert.equal(admitted.decoderStatus, 'decoded'); assert.equal(admitted.safeFailure, null);
  const originalPath = draftPath + '/evidence/' + admitted.reservationId + '/original/query', originalBody = { purpose: 'procurement-disbursement-evidence-view', reference: admitted.reference };
  const recordButton = evidence.getByRole('button', { name: 'Tinjau pencatatan bukti ' + admitted.reservationId, exact: true }); assert.equal(await recordButton.isDisabled(), true);
  const reader = await user(config.readerToken); await reader.page.goto(origin.origin + path); await choose(reader.page); await open(reader.page);
  assert.equal(await reader.page.getByRole('region', { name: 'Bukti asli uang muka', exact: true }).count(), 0);
  assert.equal((await query(reader.page, config.readerId, originalPath, originalBody, { headers: { 'If-Match': '"1"' } })).status, 403);
  const outsider = await user(config.outsiderToken); await outsider.page.goto(origin.origin + path);
  assert.equal((await query(outsider.page, config.outsiderId, draftPath + '/query', { purpose })).status, 403);
  assert.equal((await query(page, config.makerId, originalPath, { ...originalBody, purpose: 'refund-payout-evidence-view' }, { headers: { 'If-Match': '"1"' } })).status, 400);
  const originalDeadline = performance.now() + 60000;
  const [originalResponse] = await Promise.all([page.waitForResponse(response => new URL(response.url()).pathname === originalPath && response.request().method() === 'POST', { timeout: remaining(originalDeadline) }), evidence.getByRole('button', { name: 'Lihat bukti uang muka ' + admitted.reservationId, exact: true }).click({ timeout: remaining(originalDeadline) })]);
  assert.equal(originalResponse.status(), 200);
  const image = page.getByRole('img', { name: 'Bukti transfer uang muka asli untuk diperiksa', exact: true }); await image.waitFor({ timeout: remaining(originalDeadline) });
  assert.equal(await image.evaluate(async element => { if (!element.complete) await new Promise(resolve => { element.addEventListener('load', resolve, { once: true }); element.addEventListener('error', resolve, { once: true }); }); return element.naturalWidth === 13 && element.naturalHeight === 7; }), true);
  await page.getByRole('button', { name: 'Tutup gambar bukti uang muka', exact: true }).click(); await recordButton.click();
  const review = page.getByRole('region', { name: 'Rincian pencatatan bukti uang muka', exact: true }); await ready(review, 'Pastikan pencatatan bukti uang muka', 'Catat uang muka belum diverifikasi');
  const recordPreview = await query(page, config.makerId, draftPath + '/record-preview/query', { purpose, reservationId: admitted.reservationId, reference: admitted.reference }, { headers: { 'If-Match': '"1"' } }); assert.equal(recordPreview.status, 200);
  assert.equal((await query(page, config.makerId, draftPath + '/records', { reservationId: admitted.reservationId, reference: admitted.reference, binding: recordPreview.body.binding, reason: 'Synthetic guard probe' }, { headers: { 'Idempotency-Key': 'browser-missing-record-version' } })).status, 400);
  await context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await review.getByRole('button', { name: 'Catat uang muka belum diverifikasi', exact: true }).isDisabled(), true);
  await context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'procurement-disbursement-review-mobile.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'procurement-disbursement-review-desktop.png'), fullPage: true });
  dropNext(); await review.getByRole('button', { name: 'Catat uang muka belum diverifikasi', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(completed().length, 3); const recorded = completed()[2]; assert.equal(recorded.path, draftPath + '/records'); assert.equal(recorded.version, '"1"');
  assert.deepEqual(recorded.body.reference, admitted.reference); await pending(page, recorded, 'procurement.disbursement.record');
  await recover(recorded); await open(page, 'Tercatat, belum diverifikasi bank');
  await page.getByRole('region', { name: 'Uang muka tercatat belum diverifikasi', exact: true }).waitFor(); assert.equal(completed().length, 3); assert.equal(dropped(), 3);
  const savedEvidence = page.getByRole('region', { name: 'Bukti asli uang muka', exact: true });
  await savedEvidence.getByText('Bukti ' + admitted.reservationId + ' · Pemeriksaan keamanan selesai. Bukti terhubung ke catatan uang muka; pemeriksaan bank masih diperlukan.', { exact: true }).waitFor();
  assert.equal(await savedEvidence.getByText(/Bukti tetap dalam draf/).count(), 0);
  const current = await query(page, config.makerId, api + '/authorizations/query', readBody); assert.equal(current.status, 200);
  assert.equal(current.body.items[0].capacity.recordedAmount, '6000.00'); assert.equal(current.body.items[0].capacity.remainingAmount, '4000.00');
  const privateReader = await user(config.evidenceReaderToken); await privateReader.page.goto(origin.origin + path);
  const bytes = await query(privateReader.page, config.evidenceReaderId, originalPath, originalBody, { headers: { 'If-Match': '"1"' } });
  assert.equal(bytes.status, 200); assert.equal(bytes.hash, config.originalSha256); assert.ok(bytes.bytes > 0); assert.match(bytes.cache, /no-store/); assert.equal(bytes.nosniff, 'nosniff');
  await page.setViewportSize({ width: 360, height: 800 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'procurement-disbursement-saved-mobile.png'), fullPage: true });
  assert.equal(disbursementWrites.filter(item => item.status !== 200).length, 1); assert.equal(disbursementWrites.find(item => item.status !== 200).status, 400);
  for (const lookup of disbursementRecoveries) { assert.equal(lookup.actor, config.makerId); assert.ok([draftWrite.key, reserved.key, recorded.key].includes(lookup.body.key)); assert.equal(lookup.body.purpose, 'operation-recovery'); }
  for (const write of completed()) assert.ok(disbursementRecoveries.some(item => item.body.key === write.key));
  console.log('REAL_PROCUREMENT_DISBURSEMENT_BROWSER_PASS ' + JSON.stringify({ draftWrites: 1, reservations: 1, records: 1, lostCommittedReplies: 3, originalKeyRecovery: true,
    actualScanDecoded: true, originalDigestChecked: true, maskedBankDetails: true, privateReaderBoundToRecordedAssociation: true, recordedAmount: '6000.00', remaining: '4000.00',
    recordedUnverified: true, noTransfer: true, noCashOrJournal: true, noResend: true, actorChangeDenied: true, mobile: true, desktop: true }));
}
