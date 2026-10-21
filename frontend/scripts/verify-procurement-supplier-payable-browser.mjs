// Actual production Next/HTTPS/API/Mongo; approved source and Finance facts are explicitly local synthetic inputs.
import assert from 'node:assert/strict';
import { resolve } from 'node:path';
// Screen rupiah (lib/format formatMoneyText): ",00" is dropped when the fraction is zero.
const rupiah = amount => { const [whole, fraction = '00'] = String(amount).split('.'); const cents = fraction.padEnd(2, '0').slice(0, 2); return 'Rp' + whole.replace(/\B(?=(\d{3})+(?!\d))/g, '.') + (cents === '00' ? '' : ',' + cents); };

export async function verifyProcurementSupplierPayableBrowser({ config, maker, user, origin, directory, payableWrites, payableRecoveries, dropNext, dropped }) {
  const started = performance.now(); let previous = started;
  const step = name => { const now = performance.now(); console.log('SUPPLIER_PAYABLE_BROWSER_STEP ' + JSON.stringify({ name, elapsedMs: Math.round(now - started), stepMs: Math.round(now - previous) })); previous = now; };
  const path = '/procurement/supplier-payables', api = '/api/v1/procurement/supplier-payables', purpose = 'supplier-payable-review';
  const input = config.recognitionInput, location = input.location, canonical = [location.organizationId, location.shopId, location.warehouseId, location.channelId].join(':');
  const consent = 'Saya telah memeriksa sumber, nilai, sisa yang tersedia dan kebijakan pembukuan. Tidak ada transfer uang atau pelepasan barang dari tindakan ini.';
  const completed = () => payableWrites.filter(write => write.status === 200);
  const pending = current => current.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  async function query(current, actorId, route, body, options = {}) {
    const at = performance.now(); const result = await current.evaluate(async ({ actorId, route, body, options }) => {
      const response = await fetch('/api/v1/identity/csrf', { credentials: 'same-origin', cache: 'no-store', redirect: 'error' }); if (!response.ok) throw new Error('Actual request token unavailable');
      const token = await response.json(), headers = { 'Content-Type': 'application/json', 'X-CSRF-Token': token.requestToken, 'X-Expected-Actor-Id': actorId, ...options.headers };
      for (const name of options.omit ?? []) delete headers[name];
      const sent = await fetch(route, { method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', headers, body: JSON.stringify(body) });
      const text = await sent.text(); return { status: sent.status, body: text ? JSON.parse(text) : null };
    }, { actorId, route, body, options });
    console.log('SUPPLIER_PAYABLE_BROWSER_QUERY ' + JSON.stringify({ route, status: result.status, elapsedMs: Math.round(performance.now() - at) })); return result;
  }
  const combo = (current, name) => current.getByRole('combobox', { name, exact: true });
  async function choose(current) {
    await current.getByRole('heading', { name: 'Utang pemasok', exact: true }).waitFor({ timeout: 60000 });
    const select = combo(current, 'Lokasi pengadaan'); await select.locator(`option[value="${canonical}"]`).waitFor({ state: 'attached', timeout: 60000 });
    await select.selectOption(canonical); await current.getByRole('heading', { name: 'Riwayat pembukuan pemasok', exact: true }).waitFor({ timeout: 60000 });
  }
  async function renew(participant, actorId, operation, expiredLink) {
    const current = participant.page, writesBefore = payableWrites.length;
    const session = () => current.evaluate(async () => {
      const response = await fetch('/api/v1/identity/session', { credentials: 'same-origin', cache: 'no-store', redirect: 'error' });
      if (!response.ok) throw new Error('Actual session unavailable for verification'); return response.json();
    });
    const before = await session(), originalCookie = (await participant.context.cookies(origin.origin)).find(cookie => cookie.name === '__Host-ut_session');
    assert.equal(before.account.userId, actorId); assert.ok(originalCookie); assert.equal((await pending(current)).length, 0);
    if (expiredLink) {
      assert.ok(Date.parse(before.freshFactorUntil) < Date.parse(before.serverTime));
      const link = current.getByRole('link', { name: 'Verifikasi untuk pengajuan', exact: true });
      const destination = new URL(await link.getAttribute('href'), origin);
      assert.equal(destination.pathname, '/identity/account'); assert.equal(destination.searchParams.get('operation'), operation); assert.equal(destination.searchParams.get('returnPath'), path);
      await link.click();
    } else await current.getByRole('link', { name: 'Akun saya', exact: true }).click();
    await current.getByRole('heading', { name: 'Verifikasi tindakan penting', exact: true }).waitFor({ timeout: 60000 });
    const select = combo(current, 'Tindakan'); await select.locator(`option[value="${operation}"]`).waitFor({ state: 'attached', timeout: 60000 });
    await select.selectOption(operation);
    const callback = current.waitForResponse(response => new URL(response.url()).pathname === '/auth/callback', { timeout: 60000 });
    const navigation = current.waitForNavigation({ waitUntil: 'domcontentloaded', timeout: 60000 });
    await current.getByRole('button', { name: 'Verifikasi dua langkah', exact: true }).click();
    assert.ok([302, 303].includes((await callback).status())); await navigation;
    assert.equal(new URL(current.url()).pathname, expiredLink ? path : '/identity/account');
    const after = await session(), renewedCookie = (await participant.context.cookies(origin.origin)).find(cookie => cookie.name === '__Host-ut_session');
    assert.equal(after.account.userId, actorId); assert.equal(after.absoluteExpiresAt, before.absoluteExpiresAt);
    assert.ok(Date.parse(after.factorCompletedAt) > Date.parse(before.factorCompletedAt)); assert.ok(Date.parse(after.freshFactorUntil) > Date.parse(after.serverTime));
    assert.ok(renewedCookie); assert.notEqual(renewedCookie.value, originalCookie.value); assert.equal(payableWrites.length, writesBefore);
    if (!expiredLink) {
      const workspace = current.locator('a[href="/procurement/supplier-payables"]');
      await workspace.waitFor({ timeout: 60000 }); assert.equal(await workspace.count(), 1); await workspace.click();
    }
    await current.getByRole('heading', { name: 'Utang pemasok', exact: true }).waitFor({ timeout: 60000 });
    step(operation + '_real_oidc_rotation_same_actor_and_absolute_expiry');
  }
  async function value(region, label, expected) {
    const term = region.getByText(label, { exact: true }); assert.equal(await term.count(), 1); assert.equal(await term.evaluate(node => node.tagName), 'DT');
    assert.equal((await term.locator('..').locator(':scope > dd').innerText()).trim(), expected);
  }
  async function facts(current, region, kind, amount) {
    region = region.locator('[aria-label="Fakta pembukuan yang ditinjau"]'); assert.equal(await region.count(), 1);
    const summary = region.getByText('Sumber dan kebijakan pembukuan', { exact: true }); assert.equal(await summary.evaluate(node => node.tagName), 'SUMMARY');
    const details = summary.locator('..'); assert.equal(await details.evaluate(node => node.tagName), 'DETAILS'); if (!(await details.evaluate(node => node.open))) await summary.click();
    await value(region, 'Tindakan', kind === 'recognize' ? 'Pengakuan utang' : 'Penerapan uang muka');
    await value(region, 'Nilai pembukuan', rupiah(amount)); await value(region, 'Tanggal pembukuan', input.postingDate); await value(region, 'Alasan pengajuan', input.reason);
    for (const label of ['Pemasok', 'Referensi pesanan pembelian', 'Invoice pemasok', 'Persetujuan kebijakan', 'Penanggung jawab', 'Akun akrual penerimaan', 'Akun utang pemasok', 'Akun uang muka pemasok']) {
      const term = region.getByText(label, { exact: true }); assert.equal(await term.evaluate(node => node.tagName), 'DT'); assert.ok((await term.locator('..').locator(':scope > dd').innerText()).trim().length > 0);
    }
    if (kind === 'recognize') { await value(region, 'Kemasan pada alokasi', '1'); await value(region, 'Nilai neto / pajak / biaya', 'Rp10.000 / Rp1.000 / Rp250'); }
    else { await value(region, 'Utang diakui', 'Rp11.250'); await value(region, 'Sisa utang', 'Rp11.250'); await value(region, 'Uang muka terverifikasi', 'Rp6.000'); await value(region, 'Sisa uang muka', 'Rp6.000'); }
    assert.equal(await current.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    assert.equal(await region.evaluate(element => { for (let node = element; node; node = node.parentElement) { const style = getComputedStyle(node); if (['auto', 'scroll', 'hidden'].includes(style.overflowY) && node.scrollHeight > node.clientHeight + 1 && node !== document.documentElement) return false; } return true; }), true);
  }
  async function open(current, id, state) {
    await current.getByRole('button', { name: 'Buka pengajuan ' + id, exact: true }).click();
    const region = current.getByRole('region', { name: 'Rincian pengajuan utang pemasok', exact: true });
    await region.getByRole('heading', { name: state, exact: true }).waitFor({ timeout: 60000 }); return region;
  }
  async function history(current, actor) {
    const result = await query(current, actor, api + '/history/query', { purpose, location, beforeProposalId: null }); assert.equal(result.status, 200); return result.body.items;
  }
  async function recover(current, write) {
    const references = await pending(current); assert.equal(references.length, 1); assert.deepEqual(Object.keys(references[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.equal(references[0].actorId, write.actor); assert.equal(references[0].key, write.key); assert.equal(references[0].operation, 'procurement.payable.decide');
    assert.ok(!JSON.stringify(references).includes(input.reason)); assert.ok(!JSON.stringify(references).includes(config.applicationAmount));
    await current.reload(); const response = current.waitForResponse(item => new URL(item.url()).pathname === api + '/operations/query' && item.request().postDataJSON()?.key === write.key, { timeout: 60000 });
    await current.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); assert.equal((await response).status(), 200);
    await current.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden', timeout: 60000 }); assert.equal((await pending(current)).length, 0); await choose(current);
  }
  async function propose(kind, payableId = null) {
    const current = maker.page;
    await current.getByRole('button', { name: kind === 'recognize' ? 'Akui utang dari penilaian' : 'Terapkan uang muka terverifikasi', exact: true }).click();
    await current.getByRole('heading', { name: kind === 'recognize' ? 'Akui utang dari penilaian' : 'Terapkan uang muka terverifikasi', exact: true }).waitFor({ timeout: 60000 });
    const source = combo(current, kind === 'recognize' ? 'Penilaian yang disetujui' : 'Utang pemasok yang diakui'); assert.equal(await source.inputValue(), '');
    await source.selectOption(kind === 'recognize' ? input.valuationAllocationId : payableId);
    if (kind === 'apply_advance') {
      const advance = combo(current, 'Uang muka terverifikasi'); await advance.locator(`option[value="${config.settlementId}"]`).waitFor({ state: 'attached', timeout: 60000 });
      assert.equal(await advance.inputValue(), ''); await advance.selectOption(config.settlementId);
      const amount = current.getByRole('textbox', { name: 'Nilai uang muka yang diterapkan (IDR)', exact: true }); assert.equal(await amount.inputValue(), ''); await amount.fill(config.applicationAmount);
    } else assert.equal(await current.getByRole('textbox', { name: 'Nilai uang muka yang diterapkan (IDR)', exact: true }).count(), 0);
    const policy = combo(current, 'Kebijakan Finance'); assert.equal(await policy.inputValue(), ''); await policy.selectOption(input.policyId);
    for (const [label, text] of [['Tanggal pembukuan', input.postingDate], ['Alasan pengajuan', input.reason]]) { const field = current.getByLabel(label, { exact: true }); assert.equal(await field.inputValue(), ''); await field.fill(text); }
    await current.getByRole('button', { name: 'Tinjau pengajuan', exact: true }).click(); const region = current.getByRole('region', { name: 'Tinjauan utang pemasok', exact: true });
    await region.getByRole('heading', { name: 'Tinjau pengajuan utang pemasok', exact: true }).waitFor({ timeout: 60000 });
    assert.equal(await region.getByRole('heading').first().evaluate(node => node === document.activeElement), true); assert.equal(await region.getByRole('textbox').count(), 0);
    await facts(current, region, kind, kind === 'recognize' ? '11250.00' : config.applicationAmount);
    assert.equal(await region.getByRole('button', { name: 'Simpan pengajuan', exact: true }).isDisabled(), true); await region.getByRole('checkbox', { name: consent, exact: true }).check();
    if (kind === 'recognize') {
      await current.screenshot({ path: resolve(directory, 'procurement-supplier-payable-recognition-mobile.png'), fullPage: true });
      await current.setViewportSize({ width: 1440, height: 1000 }); await facts(current, region, kind, '11250.00');
      await current.screenshot({ path: resolve(directory, 'procurement-supplier-payable-recognition-desktop.png'), fullPage: true }); await current.setViewportSize({ width: 360, height: 800 });
    }
    await region.getByRole('button', { name: 'Simpan pengajuan', exact: true }).click();
    await current.getByRole('region', { name: 'Rincian pengajuan utang pemasok', exact: true }).getByRole('heading', { name: 'Menunggu pemeriksaan independen', exact: true }).waitFor({ timeout: 60000 });
    const rows = await history(current, config.makerId), saved = rows.find(row => row.proposal.input.kind === kind); assert.ok(saved); assert.equal(saved.decision, null);
    const expectedInput = kind === 'recognize' ? input : { ...input, kind, valuationAllocationId: null, payableId, settlementId: config.settlementId, amount: config.applicationAmount };
    const write = completed().at(-1); assert.equal(write.path, api + '/proposals'); assert.equal(write.actor, config.makerId); assert.deepEqual(write.body.input, expectedInput);
    assert.equal((await query(current, config.makerId, api + '/proposals/' + saved.proposal.id + '/decision-preview/query', { purpose, decision: 'approve' }, { headers: { 'If-Match': '"1"' } })).status, 403);
    step(kind + '_proposal_frozen_and_saved_without_allocation'); return saved;
  }
  const checker = await user(config.checkerToken);
  async function approve(saved, first) {
    const current = checker.page; await current.goto(origin.origin + path);
    if (!first) await renew(checker, config.checkerId, 'procurement.payable.decide', false);
    await choose(current); await open(current, saved.proposal.id, 'Menunggu pemeriksaan independen');
    const reason = current.getByRole('textbox', { name: 'Alasan keputusan', exact: true }); assert.equal(await reason.inputValue(), ''); await reason.fill(config.decisionReason);
    await current.getByRole('button', { name: 'Tinjau persetujuan', exact: true }).click(); const region = current.getByRole('region', { name: 'Tinjauan utang pemasok', exact: true });
    await region.getByRole('heading', { name: 'Tinjau persetujuan pembukuan', exact: true }).waitFor({ timeout: 60000 });
    await facts(current, region, saved.proposal.input.kind, saved.proposal.amount); await region.getByRole('checkbox', { name: consent, exact: true }).check();
    if (!first) await current.screenshot({ path: resolve(directory, 'procurement-supplier-payable-application-mobile.png'), fullPage: true });
    dropNext(); await region.getByRole('button', { name: 'Simpan persetujuan', exact: true }).click(); await current.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ timeout: 60000 });
    const write = completed().at(-1); assert.equal(write.path, api + '/proposals/' + saved.proposal.id + '/decisions'); assert.equal(write.actor, config.checkerId); assert.equal(write.body.decision, 'approve'); assert.equal(write.version, '"1"');
    if (first) {
      const original = await pending(current); await checker.context.addCookies([{ name: '__Host-ut_session', value: config.readerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
      await current.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await current.getByRole('heading', { name: /^(Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor({ timeout: 60000 }); assert.deepEqual(await pending(current), original);
      await checker.context.addCookies([{ name: '__Host-ut_session', value: config.checkerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
    }
    await recover(current, write); const rows = await history(current, config.checkerId), result = rows.find(row => row.proposal.id === saved.proposal.id);
    assert.equal(result.decision.status, 'approved'); assert.equal(result.decisionReason, config.decisionReason); assert.ok(result.decision.journalId);
    step(saved.proposal.input.kind + '_committed_decision_recovered_original_key'); return result.decision;
  }

  await maker.page.goto(origin.origin + path); await choose(maker.page);
  const listBody = { purpose, location, beforeProposalId: null };
  for (const [body, options, status] of [[listBody, { omit: ['X-CSRF-Token'] }, 403], [listBody, { omit: ['X-Expected-Actor-Id'] }, 400], [{ ...listBody, purpose: 'wrong-purpose' }, {}, 400], [{ ...listBody, extra: true }, {}, 400]])
    assert.equal((await query(maker.page, config.makerId, api + '/history/query', body, options)).status, status);
  for (const changed of [null, { ...input, location: null }, { ...input, postingDate: '2026-02-30' }, { ...input, valuationAllocationId: Number(input.valuationAllocationId) }, { ...input, amount: 11250 }, { ...input, releaseStock: true }])
    assert.equal((await query(maker.page, config.makerId, api + '/preview/query', { purpose, input: changed })).status, 400);
  const aged = await user(config.agedToken); await aged.page.goto(origin.origin + '/__supplier_payable_aged_probe');
  const stale = await query(aged.page, config.makerId, api + '/preview/query', { purpose, input }); assert.equal(stale.status, 403); assert.equal(stale.body.code, 'step_up_required');
  await maker.context.setOffline(true); await maker.page.getByRole('alert').filter({ hasText: /^Koneksi terputus/ }).waitFor();
  assert.equal(await combo(maker.page, 'Lokasi pengadaan').inputValue(), ''); assert.equal(await combo(maker.page, 'Lokasi pengadaan').locator('option:not([value=""])').count(), 0); assert.equal(payableWrites.length, 0);
  await maker.context.setOffline(false); await maker.page.reload(); await choose(maker.page); step('strict_body_scope_and_offline_guards');
  const recognition = await propose('recognize'), recognized = await approve(recognition, true);
  assert.equal(recognized.applicationId, null);
  // Continue as the same maker in the aged session that was denied above, then use
  // the actual corrected product link and signed provider callback before the next action.
  maker = aged; await maker.page.goto(origin.origin + path);
  await renew(maker, config.makerId, 'procurement.payable.propose', true); await choose(maker.page);
  const application = await propose('apply_advance', recognized.payableId), applied = await approve(application, false); assert.ok(applied.applicationId); assert.equal(applied.payableId, recognized.payableId);
  const reader = await user(config.readerToken); await reader.page.goto(origin.origin + path); await choose(reader.page);
  const saved = await open(reader.page, application.proposal.id, 'Disetujui dan dibukukan'); await facts(reader.page, saved, 'apply_advance', config.applicationAmount);
  const live = reader.page.getByRole('region', { name: 'Sisa terkini dari sumber', exact: true });
  await live.getByRole('status').filter({ hasText: /^Masih ada utang tercatat$/ }).waitFor({ timeout: 60000 });
  await value(live, 'Sisa utang', 'Rp7.250'); await value(live, 'Sisa uang muka', 'Rp2.000');
  for (const name of ['Akui utang dari penilaian', 'Terapkan uang muka terverifikasi', 'Tinjau persetujuan', 'Tinjau penolakan']) assert.equal(await reader.page.getByRole('button', { name, exact: true }).count(), 0);
  await reader.page.getByRole('button', { name: 'Lihat utang dan sisa', exact: true }).click(); await reader.page.getByRole('heading', { name: 'Utang dan sisa tercatat', exact: true }).waitFor({ timeout: 60000 });
  await combo(reader.page, 'Utang pemasok yang diakui').selectOption(recognized.payableId); const balance = reader.page.getByRole('region', { name: 'Utang yang dipilih', exact: true });
  await value(balance, 'Utang diakui', 'Rp11.250'); await value(balance, 'Uang muka diterapkan ke utang', 'Rp4.000'); await value(balance, 'Sisa utang', 'Rp7.250');
  assert.equal(await reader.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await reader.page.screenshot({ path: resolve(directory, 'procurement-supplier-payable-outstanding-mobile.png'), fullPage: true });
  const currentAdvance = await query(reader.page, config.readerId, api + '/verified-advances/' + config.settlementId + '/query', { purpose });
  assert.equal(currentAdvance.status, 200); assert.equal(currentAdvance.body.capacity.totalAmount, '6000.00'); assert.equal(currentAdvance.body.capacity.appliedAmount, '4000.00'); assert.equal(currentAdvance.body.capacity.remainingAmount, '2000.00');
  const outsider = await user(config.outsiderToken); await outsider.page.goto(origin.origin + '/__supplier_payable_scope_probe');
  const denied = await query(outsider.page, config.outsiderId, api + '/proposals/' + application.proposal.id + '/query', { purpose }); assert.equal(denied.status, 403);
  for (const field of ['proposal', 'decision', 'source', 'capacity', 'policy']) assert.equal(Object.hasOwn(denied.body, field), false);
  assert.equal(payableWrites.length, 4); assert.equal(completed().length, 4); assert.equal(dropped(), 2); assert.equal(payableRecoveries.length, 3);
  assert.equal(payableRecoveries[0].status, 409); assert.ok(payableRecoveries.slice(1).every(item => item.actor === config.checkerId && item.status === 200));
  step('independent_read_exact_remaining_and_single_command_effects');
  console.log('REAL_PROCUREMENT_SUPPLIER_PAYABLE_BROWSER_PASS ' + JSON.stringify({ recognized: '11250.00', verifiedAdvance: '6000.00', applied: '4000.00', payableRemaining: '7250.00', advanceRemaining: '2000.00', originalKeysRecovered: 2, actualStepUpRotations: 2, noResend: true, independentDecisions: true, mobile: true, desktop: true }));
}
