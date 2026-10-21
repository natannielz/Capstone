// Actual production Next/HTTPS/Kestrel/Mongo; the reviewed source and physical counts are synthetic local fixture facts.
import assert from 'node:assert/strict';
import { resolve } from 'node:path';

export async function verifyStockSourceReconciliationBrowser({ config, maker, user, origin, directory, reconciliationWrites, reconciliationRecoveries, dropNext, dropped }) {
  const startedAt = performance.now(); let previousStepAt = startedAt;
  function step(name) { const now = performance.now(); console.log('STOCK_RECONCILIATION_BROWSER_STEP ' + JSON.stringify({ name, elapsedMs: Math.round(now - startedAt), stepMs: Math.round(now - previousStepAt) })); previousStepAt = now; }
  const { page, context } = maker, path = '/inventory/source-reconciliations', api = '/api/v1/inventory/source-reconciliations', purpose = 'stock-source-reconciliation-review';
  const baselineRoute = api + '/pools/' + config.poolId + '/baseline/query', historyRoute = api + '/pools/' + config.poolId + '/proposals/query';
  const consent = 'Saya telah memeriksa seluruh lot, waktu dan referensi pengamatan. Pencatatan ini tidak mengaktifkan sumber atau melepas stok.';
  const notice = 'Rekonsiliasi hanya mencatat perbandingan jumlah fisik. Sumber belum diaktifkan; status tahan, biaya dan ketersediaan barang tidak berubah.';
  const completed = () => reconciliationWrites.filter(item => item.status === 200);
  const references = current => current.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  async function query(current, actorId, route, body, options = {}) {
    const began = performance.now(); const result = await current.evaluate(async ({ actorId, route, body, options }) => {
      const signal = AbortSignal.timeout(60000), csrf = await fetch('/api/v1/identity/csrf', { credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal });
      if (!csrf.ok) throw new Error('Actual request token unavailable'); const token = await csrf.json();
      const headers = { 'Content-Type': 'application/json', 'X-CSRF-Token': token.requestToken, 'X-Expected-Actor-Id': actorId, ...options.headers }; for (const name of options.omit ?? []) delete headers[name];
      const response = await fetch(route, { method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', headers, body: JSON.stringify(body), signal });
      const text = await response.text(); return { status: response.status, body: text ? JSON.parse(text) : null, cacheControl: response.headers.get('cache-control') };
    }, { actorId, route, body, options });
    console.log('STOCK_RECONCILIATION_BROWSER_QUERY ' + JSON.stringify({ route, status: result.status, elapsedMs: Math.round(performance.now() - began) })); return result;
  }
  async function choose(current) {
    await current.getByRole('heading', { name: 'Rekonsiliasi jumlah fisik', exact: true }).waitFor({ timeout: 60000 });
    const warehouse = current.getByRole('combobox', { name: 'Gudang dan kanal', exact: true }), options = warehouse.locator('option:not([value=""])');
    await options.first().waitFor({ state: 'attached', timeout: 60000 }); assert.equal(await options.count(), 1); assert.match(await options.innerText(), /Synthetic/);
    await warehouse.selectOption(await options.getAttribute('value')); const pool = current.getByRole('combobox', { name: 'Pilih sumber stok', exact: true });
    await pool.locator(`option[value="${config.poolId}"]`).waitFor({ state: 'attached', timeout: 60000 }); assert.equal(await pool.locator(`option[value="${config.poolId}"]`).innerText(), config.poolName);
    await pool.selectOption(config.poolId); await current.getByRole('heading', { name: 'Riwayat rekonsiliasi', exact: true }).waitFor({ timeout: 60000 });
  }
  async function pending(current, write, operation) {
    const saved = await references(current); assert.equal(saved.length, 1); assert.deepEqual(Object.keys(saved[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.equal(saved[0].actorId, write.actor); assert.equal(saved[0].key, write.key); assert.equal(saved[0].operation, operation);
    for (const value of [config.reason, config.decisionReason, config.makerSourceReference, config.checkerSourceReference]) assert.ok(!JSON.stringify(saved).includes(value));
    assert.equal(await current.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0); return saved;
  }
  async function recover(current, write) {
    await current.reload(); const response = current.waitForResponse(item => new URL(item.url()).pathname === api + '/operations/query' && item.request().postDataJSON()?.key === write.key, { timeout: 60000 });
    await current.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); assert.equal((await response).status(), 200);
    await current.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden', timeout: 60000 }); assert.equal((await references(current)).length, 0); await choose(current);
  }
  async function open(current, heading) {
    await current.getByRole('button', { name: 'Buka pengajuan ' + config.makerSourceReference, exact: true }).click();
    const region = current.getByRole('region', { name: 'Rincian pengajuan rekonsiliasi', exact: true });
    await region.getByRole('heading', { name: heading, exact: true }).waitFor({ timeout: 60000 }); return region;
  }
  const when = value => new Intl.DateTimeFormat('id-ID', { dateStyle: 'medium', timeStyle: 'medium', timeZone: 'Asia/Jakarta' }).format(new Date(value)) + ' WIB';
  async function entered(current, checker, original) {
    const who = checker ? 'pemeriksa' : 'pengaju', reference = checker ? config.checkerSourceReference : config.makerSourceReference;
    const at = new Date(Math.floor(Date.now() / 1000) * 1000), local = new Date(at.getTime() + 7 * 3600000).toISOString().slice(0, 19);
    assert.ok(at.getTime() >= Date.parse(original.latestSourceAt));
    for (const label of ['Waktu pengamatan ' + who + ' (WIB)', 'Referensi pengamatan ' + who]) assert.equal(await current.getByLabel(label, { exact: true }).inputValue(), '');
    const quantity = current.getByLabel(new RegExp('^Jumlah fisik ' + who + ' ·')); assert.equal(await quantity.count(), 1); assert.equal(await quantity.inputValue(), '');
    await current.getByLabel('Waktu pengamatan ' + who + ' (WIB)', { exact: true }).fill(local); await current.getByLabel('Referensi pengamatan ' + who, { exact: true }).fill(reference); await quantity.fill(config.expectedPackages);
    const reason = current.getByLabel(checker ? 'Alasan keputusan' : 'Alasan pengajuan', { exact: true }); assert.equal(await reason.inputValue(), ''); await reason.fill(checker ? config.decisionReason : config.reason);
    return { observedAt: at.toISOString(), sourceReference: reference, entries: [{ balanceId: config.expectedBalanceId, packages: config.expectedPackages }] };
  }
  async function observationFacts(current, region, title, observed, original) {
    const section = region.getByRole('region', { name: title, exact: true }); await section.waitFor();
    const text = await section.innerText(); for (const value of [observed.sourceReference, when(observed.observedAt), original.balances[0].skuCode, original.balances[0].packageLabel, original.balances[0].lotReference, 'Sesuai · 0']) assert.ok(text.includes(value), value);
    const list = section.getByRole('list', { name: title + ' per lot', exact: true }); assert.equal(await list.locator(':scope > li').count(), 1);
    for (const label of ['Jumlah tercatat (kemasan)', 'Jumlah fisik (kemasan)']) {
      const term = list.getByText(label, { exact: true }); assert.equal(await term.evaluate(element => element.tagName), 'DT');
      const cell = term.locator('..'); assert.equal(await cell.locator(':scope > dt').count(), 1);
      assert.equal(await cell.locator(':scope > dd').innerText(), config.expectedPackages);
    }
    assert.equal(await current.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  }
  async function sourceFacts(region, original) {
    const summary = region.getByText('Dasar sumber dan kebijakan yang ditinjau', { exact: true });
    assert.equal(await summary.evaluate(element => element.tagName), 'SUMMARY'); const details = summary.locator('..');
    assert.equal(await details.evaluate(element => element.tagName), 'DETAILS');
    if (!(await details.evaluate(element => element.open))) await summary.click();
    const text = await region.innerText(); for (const value of [original.poolLabel, original.physicalKey, original.reconciliationProcedureReference, original.configurationHash, original.baselineHash, when(original.latestSourceAt), notice]) assert.ok(text.includes(value), value);
  }
  await page.goto(origin.origin + path); await choose(page); await page.getByText('Belum ada pengamatan yang diajukan.', { exact: true }).waitFor();
  const initial = await query(page, config.makerId, baselineRoute, { purpose }); assert.equal(initial.status, 200); assert.equal(initial.cacheControl, 'private, no-store');
  const original = initial.body; assert.equal(original.poolId, config.poolId); assert.equal(original.balances.length, 1); assert.equal(original.maximumAgeMilliseconds, '900000');
  const balance = original.balances[0]; assert.equal(balance.balanceId, config.expectedBalanceId); assert.equal(balance.onHand, config.expectedPackages); assert.equal(balance.held, config.expectedPackages); assert.equal(balance.reserved, '0'); assert.equal(balance.expired, '0');
  for (const [body, options, status] of [[{ purpose }, { omit: ['X-CSRF-Token'] }, 403], [{ purpose }, { omit: ['X-Expected-Actor-Id'] }, 400], [{ purpose: 'wrong-purpose' }, {}, 400], [{ purpose, extra: true }, {}, 400]]) assert.equal((await query(page, config.makerId, baselineRoute, body, options)).status, status);
  assert.equal((await query(page, config.makerId, api + '/pools/0' + config.poolId + '/baseline/query', { purpose })).status, 400);
  const probeInput = { poolId: original.poolId, poolRevision: original.poolRevision, poolFence: original.poolFence, policyId: original.policyId, policyRevision: original.policyRevision, configurationHash: original.configurationHash,
    observation: { observedAt: new Date().toISOString(), sourceReference: 'SYNTHETIC-BOUNDARY-PROBE', entries: [{ balanceId: balance.balanceId, packages: config.expectedPackages }] } };
  for (const changed of [{ ...probeInput, poolId: Number(probeInput.poolId) }, { ...probeInput, observation: { ...probeInput.observation, entries: [{ balanceId: balance.balanceId, packages: Number(config.expectedPackages) }] } }, { ...probeInput, activate: true }]) {
    assert.equal((await query(page, config.makerId, api + '/proposals', { input: changed, binding: original.binding, reason: config.reason }, { headers: { 'Idempotency-Key': 'recon.propose.' + config.poolId + '.' + crypto.randomUUID() } })).status, 400);
  }
  const aged = await user(config.agedToken); await aged.page.goto(origin.origin + '/__reconciliation_aged_probe');
  const stale = await query(aged.page, config.makerId, api + '/proposals', { input: probeInput, binding: original.binding, reason: config.reason }, { headers: { 'Idempotency-Key': 'recon.propose.' + config.poolId + '.' + crypto.randomUUID() } }); assert.equal(stale.status, 403); assert.equal(stale.body.code, 'step_up_required');
  assert.equal(completed().length, 0); step('genuine_baseline_and_boundaries_verified');
  await context.setOffline(true); await page.getByText('Koneksi terputus. Data sebelumnya disembunyikan; referensi permintaan tetap tersimpan untuk akun semula.', { exact: true }).waitFor();
  assert.equal(await page.getByRole('heading', { name: 'Riwayat rekonsiliasi', exact: true }).count(), 0); assert.equal(await page.getByRole('combobox', { name: 'Pilih sumber stok', exact: true }).count(), 0); assert.equal(completed().length, 0);
  await context.setOffline(false); await page.getByRole('button', { name: 'Muat ulang sumber', exact: true }).click(); await choose(page);
  await page.getByRole('button', { name: 'Catat pengamatan lengkap', exact: true }).click(); await page.getByRole('heading', { name: 'Catat pengamatan lengkap', exact: true }).waitFor({ timeout: 60000 });
  const makerObservation = await entered(page, false, original); await page.getByRole('button', { name: 'Tinjau pengajuan', exact: true }).click();
  const review = page.getByRole('region', { name: 'Tinjauan rekonsiliasi', exact: true }); await review.getByRole('heading', { name: 'Tinjau pengajuan rekonsiliasi', exact: true }).waitFor({ timeout: 60000 });
  assert.equal(await review.getByRole('heading').first().evaluate(element => element === document.activeElement), true); assert.equal(await review.getByRole('textbox').count(), 0); await sourceFacts(review, original); await observationFacts(page, review, 'Pengamatan yang akan diajukan', makerObservation, original);
  assert.equal(await review.getByRole('button', { name: 'Simpan pengajuan', exact: true }).isDisabled(), true); await review.getByRole('checkbox', { name: consent, exact: true }).check();
  await page.screenshot({ path: resolve(directory, 'stock-source-reconciliation-proposal-mobile.png'), fullPage: true }); await page.setViewportSize({ width: 1440, height: 1000 }); await observationFacts(page, review, 'Pengamatan yang akan diajukan', makerObservation, original); await page.screenshot({ path: resolve(directory, 'stock-source-reconciliation-proposal-desktop.png'), fullPage: true });
  step('explicit_complete_maker_review_verified'); dropNext(); await review.getByRole('button', { name: 'Simpan pengajuan', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ timeout: 60000 });
  assert.equal(completed().length, 1); const proposalWrite = completed()[0]; assert.equal(proposalWrite.path, api + '/proposals'); assert.equal(proposalWrite.actor, config.makerId); assert.deepEqual(proposalWrite.body.input.observation, makerObservation); assert.equal(proposalWrite.body.reason, config.reason);
  const pendingProposal = await pending(page, proposalWrite, 'stock.source.reconciliation.propose'); step('committed_proposal_reply_lost');
  await context.addCookies([{ name: '__Host-ut_session', value: config.readerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('heading', { name: /^(Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor({ timeout: 60000 }); assert.deepEqual(await references(page), pendingProposal);
  await context.addCookies([{ name: '__Host-ut_session', value: config.makerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]); await recover(page, proposalWrite); await open(page, 'Menunggu pemeriksaan independen');
  const history = await query(page, config.makerId, historyRoute, { purpose, offset: 0, limit: 25 }); assert.equal(history.status, 200); assert.equal(history.body.total, '1'); assert.equal(history.body.items.length, 1);
  const proposed = history.body.items[0], id = proposed.proposal.id; assert.equal(proposed.decision, null); assert.equal(proposed.proposal.recordedBy, config.makerId); assert.deepEqual(proposed.proposal.observation.entries, makerObservation.entries);
  const beforeDecision = await query(page, config.makerId, baselineRoute, { purpose }); assert.equal(beforeDecision.status, 200); assert.deepEqual(beforeDecision.body.balances, original.balances); assert.equal(beforeDecision.body.baselineHash, original.baselineHash);
  step('maker_recovery_and_unchanged_held_stock_verified');
  const checker = await user(config.checkerToken); await checker.page.goto(origin.origin + path); await choose(checker.page); await open(checker.page, 'Menunggu pemeriksaan independen');
  const decidePath = api + '/proposals/' + id + '/decide', reviewPath = api + '/proposals/' + id + '/review/query';
  assert.equal((await query(checker.page, config.checkerId, decidePath, { decision: 'confirm', observation: makerObservation, binding: original.binding, reason: config.decisionReason }, { headers: { 'Idempotency-Key': 'recon.confirm.' + id + '.' + crypto.randomUUID() } })).status, 400);
  assert.equal((await query(checker.page, config.checkerId, reviewPath, { purpose, decision: 'confirm' })).status, 400);
  assert.equal((await query(page, config.makerId, reviewPath, { purpose, decision: 'confirm', observation: makerObservation })).status, 403);
  await checker.page.getByRole('button', { name: 'Periksa secara independen', exact: true }).click(); await checker.page.getByRole('heading', { name: 'Masukkan pengamatan independen', exact: true }).waitFor({ timeout: 60000 });
  const checkerObservation = await entered(checker.page, true, beforeDecision.body); assert.notEqual(checkerObservation.sourceReference, makerObservation.sourceReference);
  await checker.page.getByRole('button', { name: 'Bandingkan pengamatan independen', exact: true }).click(); const decisionReview = checker.page.getByRole('region', { name: 'Tinjauan rekonsiliasi', exact: true });
  await decisionReview.getByRole('heading', { name: 'Tinjau pemeriksaan independen', exact: true }).waitFor({ timeout: 60000 }); await observationFacts(checker.page, decisionReview, 'Pengamatan pengaju', makerObservation, original); await observationFacts(checker.page, decisionReview, 'Pengamatan pemeriksa', checkerObservation, original);
  assert.equal(await decisionReview.getByRole('button', { name: 'Catat kesesuaian jumlah', exact: true }).isDisabled(), true); await decisionReview.getByRole('checkbox', { name: consent, exact: true }).check();
  step('independent_blank_observation_and_review_verified'); dropNext(); await decisionReview.getByRole('button', { name: 'Catat kesesuaian jumlah', exact: true }).click(); await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ timeout: 60000 });
  assert.equal(completed().length, 2); const decisionWrite = completed()[1]; assert.equal(decisionWrite.path, decidePath); assert.equal(decisionWrite.actor, config.checkerId); assert.equal(decisionWrite.version, '"1"'); assert.equal(decisionWrite.body.decision, 'confirm'); assert.equal(decisionWrite.body.reason, config.decisionReason); assert.deepEqual(decisionWrite.body.observation, checkerObservation);
  await pending(checker.page, decisionWrite, 'stock.source.reconciliation.decide'); await recover(checker.page, decisionWrite); await open(checker.page, 'Jumlah sesuai pada pengamatan yang diperiksa'); step('independent_commit_recovered_exactly_once');
  const reader = await user(config.readerToken); await reader.page.goto(origin.origin + path); await choose(reader.page); assert.equal(await reader.page.getByRole('button', { name: 'Catat pengamatan lengkap', exact: true }).count(), 0);
  const saved = await open(reader.page, 'Jumlah sesuai pada pengamatan yang diperiksa'); assert.equal(await reader.page.getByRole('button', { name: 'Periksa secara independen', exact: true }).count(), 0); await sourceFacts(saved, original); await observationFacts(reader.page, saved, 'Pengamatan pengaju', makerObservation, original); await observationFacts(reader.page, saved, 'Pengamatan pemeriksa', checkerObservation, original); assert.ok((await saved.innerText()).includes(config.decisionReason));
  await reader.page.screenshot({ path: resolve(directory, 'stock-source-reconciliation-confirmed-mobile.png'), fullPage: true });
  const actual = await query(reader.page, config.readerId, api + '/proposals/' + id + '/query', { purpose }); assert.equal(actual.status, 200); assert.equal(actual.body.decision.decision, 'confirm'); assert.equal(actual.body.decision.recordedBy, config.checkerId); assert.equal(actual.body.proposal.recordedBy, config.makerId);
  const final = await query(reader.page, config.readerId, baselineRoute, { purpose }); assert.equal(final.status, 200); assert.deepEqual(final.body.balances, original.balances); assert.equal(final.body.baselineHash, original.baselineHash); assert.equal(final.body.configurationHash, original.configurationHash); assert.equal(BigInt(final.body.poolFence), BigInt(original.poolFence) + 2n);
  assert.equal(BigInt(final.body.balances[0].onHand) - BigInt(final.body.balances[0].held) - BigInt(final.body.balances[0].reserved) - BigInt(final.body.balances[0].expired), 0n);
  const foreignRecovery = await query(reader.page, config.readerId, api + '/operations/query', { purpose: 'operation-recovery', operation: 'stock.source.reconciliation.propose', key: proposalWrite.key }); assert.equal(foreignRecovery.status, 200); assert.deepEqual(foreignRecovery.body, { state: 'not_confirmed', result: null });
  const outsider = await user(config.outsiderToken); await outsider.page.goto(origin.origin + '/__reconciliation_foreign_probe');
  for (const route of [baselineRoute, api + '/proposals/' + id + '/query']) { const denied = await query(outsider.page, config.outsiderId, route, { purpose }); assert.equal(denied.status, 403); assert.ok(!JSON.stringify(denied.body).includes(config.makerSourceReference)); }
  assert.equal(completed().length, 2); for (const write of completed()) assert.equal(reconciliationWrites.filter(item => item.key === write.key).length, 1);
  assert.equal(dropped(), 2); for (const write of completed()) assert.ok(reconciliationRecoveries.some(item => item.status === 200 && item.actor === write.actor && item.body.key === write.key));
  step('read_only_private_scope_and_unchanged_quantity_verified'); console.log('REAL_STOCK_SOURCE_RECONCILIATION_BROWSER_PASS');
}
