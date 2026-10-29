// Production Next/HTTPS/API/Mongo with genuine source commands and explicitly local synthetic operator inputs.
import assert from 'node:assert/strict';
import { resolve } from 'node:path';
import { displayDate, openAccountFromFrame, returnToWorkspacePage } from './workspace-rehearsal.mjs';
const rupiah = amount => { const [whole, fraction = '00'] = String(amount).split('.'); return 'Rp' + whole.replace(/\B(?=(\d{3})+(?!\d))/g, '.') + ',' + fraction.padEnd(2, '0').slice(0, 2); };

export async function verifyProcurementAdvanceReturnBrowser({ config, maker, user, origin, directory, advanceReturnWrites, advanceReturnRecoveries, dropNext, dropped }) {
  const started = performance.now();
  const step = name => console.log('ADVANCE_RETURN_BROWSER_STEP ' + JSON.stringify({ name, elapsedMs: Math.round(performance.now() - started) }));
  const path = '/procurement/advance-returns', api = '/api/v1/procurement/advance-returns', purpose = 'procurement-advance-return-review';
  const input = config.returnInput, location = input.location, canonical = [location.organizationId, location.shopId, location.warehouseId, location.channelId].join(':');
  const consent = 'Saya telah memeriksa seluruh kredit bank, pemasok, pesanan pembelian, rincian uang muka dan kebijakan pembukuan.';
  const pending = current => current.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  const combo = (current, name) => current.getByRole('combobox', { name, exact: true });
  async function query(current, actorId, route, body, options = {}) {
    return current.evaluate(async ({ actorId, route, body, options }) => {
      const response = await fetch('/api/v1/identity/csrf', { credentials: 'same-origin', cache: 'no-store', redirect: 'error' }); if (!response.ok) throw new Error('Actual request token unavailable');
      const token = await response.json(), headers = { 'Content-Type': 'application/json', 'X-CSRF-Token': token.requestToken, 'X-Expected-Actor-Id': actorId, ...options.headers };
      for (const name of options.omit ?? []) delete headers[name];
      const sent = await fetch(route, { method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', headers, body: JSON.stringify(body) });
      const text = await sent.text(); return { status: sent.status, body: text ? JSON.parse(text) : null };
    }, { actorId, route, body, options });
  }
  async function choose(current) {
    await current.getByRole('heading', { name: 'Pengembalian uang muka pemasok', exact: true }).waitFor({ timeout: 60000 });
    const select = combo(current, 'Lokasi pengadaan'); await select.locator(`option[value="${canonical}"]`).waitFor({ state: 'attached', timeout: 60000 });
    await select.selectOption(canonical); await current.getByRole('heading', { name: 'Riwayat pengembalian uang muka', exact: true }).waitFor({ timeout: 60000 });
  }
  async function renew(participant, actorId, operation, expiredLink) {
    const current = participant.page;
    const session = () => current.evaluate(async () => { const result = await fetch('/api/v1/identity/session', { credentials: 'same-origin', cache: 'no-store' }); if (!result.ok) throw new Error('Actual session missing'); return result.json(); });
    const before = await session(), original = (await participant.context.cookies(origin.origin)).find(cookie => cookie.name === '__Host-ut_session');
    assert.equal(before.account.userId, actorId); assert.ok(original); assert.equal((await pending(current)).length, 0);
    if (expiredLink) {
      assert.ok(Date.parse(before.freshFactorUntil) < Date.parse(before.serverTime));
      const link = current.getByRole('link', { name: 'Verifikasi untuk pengajuan', exact: true }), target = new URL(await link.getAttribute('href'), origin);
      assert.equal(target.searchParams.get('operation'), operation); assert.equal(target.searchParams.get('returnPath'), path); await link.click();
    } else await openAccountFromFrame(current);
    await current.getByRole('heading', { name: 'Verifikasi tindakan penting', exact: true }).waitFor({ timeout: 60000 });
    const actions = combo(current, 'Tindakan'); await actions.locator(`option[value="${operation}"]`).waitFor({ state: 'attached', timeout: 60000 }); await actions.selectOption(operation);
    const callback = current.waitForResponse(response => new URL(response.url()).pathname === '/auth/callback', { timeout: 60000 });
    const navigation = current.waitForNavigation({ waitUntil: 'domcontentloaded', timeout: 60000 });
    await current.getByRole('button', { name: 'Verifikasi dua langkah', exact: true }).click(); assert.ok([302, 303].includes((await callback).status())); await navigation;
    assert.equal(new URL(current.url()).pathname, expiredLink ? path : '/identity/account');
    const after = await session(), renewed = (await participant.context.cookies(origin.origin)).find(cookie => cookie.name === '__Host-ut_session');
    assert.equal(after.account.userId, actorId); assert.equal(after.absoluteExpiresAt, before.absoluteExpiresAt);
    assert.ok(Date.parse(after.factorCompletedAt) > Date.parse(before.factorCompletedAt)); assert.ok(Date.parse(after.freshFactorUntil) > Date.parse(after.serverTime)); assert.notEqual(renewed.value, original.value);
    if (!expiredLink) await returnToWorkspacePage(current, '/procurement/advance-returns');
    step(operation + '_real_oidc_rotation');
  }
  async function value(region, label, expected) {
    const term = region.getByText(label, { exact: true }); assert.equal(await term.count(), 1); assert.equal(await term.evaluate(node => node.tagName), 'DT');
    assert.equal((await term.locator('..').locator(':scope > dd').innerText()).trim(), expected);
  }
  async function facts(current, outer) {
    const region = outer.locator('[aria-label="Fakta pengembalian yang ditinjau"]'), summary = region.getByText('Sumber dan kebijakan pembukuan', { exact: true });
    if (!(await summary.locator('..').evaluate(node => node.open))) await summary.click();
    await value(region, 'Seluruh kredit bank', 'Rp7.000,00'); await value(region, 'Tanggal pembukuan', displayDate(input.postingDate)); await value(region, 'Alasan pengajuan', input.reason);
    for (const label of ['Pemasok', 'Referensi pesanan pembelian', 'Referensi bank', 'Persetujuan kebijakan', 'Penanggung jawab', 'Akun penerimaan belum dialokasikan', 'Akun uang muka pemasok']) {
      const term = region.getByText(label, { exact: true }); assert.equal(await term.evaluate(node => node.tagName), 'DT'); assert.ok((await term.locator('..').locator(':scope > dd').innerText()).trim());
    }
    for (const line of input.lines) {
      const row = region.getByRole('heading', { name: 'Uang muka terverifikasi ' + line.settlementId, exact: true }).locator('..');
      await value(row, 'Nilai pengembalian', rupiah(line.amount)); await value(row, 'Sudah diterapkan ke utang', 'Rp0,00'); await value(row, 'Sudah dikembalikan pemasok', 'Rp0,00');
    }
    assert.equal(await current.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    assert.equal(await region.evaluate(element => { for (let node = element; node; node = node.parentElement) { const style = getComputedStyle(node); if (['auto', 'scroll', 'hidden'].includes(style.overflowY) && node.scrollHeight > node.clientHeight + 1 && node !== document.documentElement) return false; } return true; }), true);
  }
  async function history(current, actor) { const result = await query(current, actor, api + '/history/query', { purpose, location, beforeProposalId: null }); assert.equal(result.status, 200); return result.body.items; }
  async function open(current, id, state) { await current.getByRole('button', { name: 'Buka pengajuan ' + id, exact: true }).click(); const region = current.getByRole('region', { name: 'Rincian pengembalian uang muka', exact: true }); await region.getByRole('heading', { name: state, exact: true }).waitFor({ timeout: 60000 }); return region; }

  await maker.page.goto(origin.origin + path); await choose(maker.page);
  const listBody = { purpose, location, beforeProposalId: null };
  for (const [body, options, status] of [[listBody, { omit: ['X-CSRF-Token'] }, 403], [listBody, { omit: ['X-Expected-Actor-Id'] }, 400], [{ ...listBody, purpose: 'wrong-purpose' }, {}, 400], [{ ...listBody, extra: true }, {}, 400]])
    assert.equal((await query(maker.page, config.makerId, api + '/history/query', body, options)).status, status);
  for (const changed of [null, { ...input, location: null }, { ...input, postingDate: '2026-02-30' }, { ...input, receiptId: Number(input.receiptId) }, { ...input, lines: [{ ...input.lines[0], amount: 4000 }] }, { ...input, transferMoney: true }])
    assert.equal((await query(maker.page, config.makerId, api + '/preview/query', { purpose, input: changed })).status, 400);
  const aged = await user(config.agedToken); await aged.page.goto(origin.origin + '/__advance_return_aged_probe');
  const stale = await query(aged.page, config.makerId, api + '/preview/query', { purpose, input }); assert.equal(stale.status, 403); assert.equal(stale.body.code, 'step_up_required');
  await maker.context.setOffline(true); await maker.page.getByRole('alert').filter({ hasText: /^Koneksi terputus/ }).waitFor();
  assert.equal(await combo(maker.page, 'Lokasi pengadaan').inputValue(), ''); assert.equal(await combo(maker.page, 'Lokasi pengadaan').locator('option:not([value=""])').count(), 0); assert.equal(advanceReturnWrites.length, 0); await maker.context.setOffline(false);
  maker = aged; await maker.page.goto(origin.origin + path); await renew(maker, config.makerId, 'procurement.advancereturn.propose', true); await choose(maker.page);
  await maker.page.getByRole('button', { name: 'Catat pengembalian uang muka', exact: true }).click();
  const receipts = combo(maker.page, 'Kredit bank terkonfirmasi'); await receipts.locator(`option[value="${input.receiptId}"]`).waitFor({ state: 'attached', timeout: 60000 }); assert.equal(await receipts.inputValue(), ''); await receipts.selectOption(input.receiptId);
  for (const line of input.lines) { const chooseLine = maker.page.getByRole('button', { name: 'Pilih uang muka ' + line.settlementId, exact: true }); await chooseLine.waitFor({ timeout: 60000 }); await chooseLine.click(); const field = maker.page.getByRole('textbox', { name: 'Nilai pengembalian uang muka ' + line.settlementId + ' (IDR)', exact: true }); assert.equal(await field.inputValue(), ''); await field.fill(line.amount); }
  await maker.page.getByRole('button', { name: 'Uang muka terbaru', exact: true }).click();
  for (const line of input.lines) { await maker.page.getByRole('button', { name: 'Uang muka ' + line.settlementId + ' sudah dipilih', exact: true }).waitFor({ timeout: 60000 }); assert.equal(await maker.page.getByRole('textbox', { name: 'Nilai pengembalian uang muka ' + line.settlementId + ' (IDR)', exact: true }).inputValue(), line.amount); }
  const policy = combo(maker.page, 'Kebijakan Finance'); assert.equal(await policy.inputValue(), ''); await policy.selectOption(input.policyId);
  for (const [label, text] of [['Tanggal pembukuan', input.postingDate], ['Alasan pengajuan', input.reason]]) { const field = maker.page.getByLabel(label, { exact: true }); assert.equal(await field.inputValue(), ''); await field.fill(text); }
  const firstAmount = maker.page.getByRole('textbox', { name: 'Nilai pengembalian uang muka ' + input.lines[0].settlementId + ' (IDR)', exact: true }); await firstAmount.fill('1.00'); assert.equal(await maker.page.getByRole('button', { name: 'Tinjau pengajuan', exact: true }).isDisabled(), true); await firstAmount.fill(input.lines[0].amount);
  await maker.page.getByRole('button', { name: 'Tinjau pengajuan', exact: true }).click();
  const review = maker.page.getByRole('region', { name: 'Tinjauan pengembalian uang muka', exact: true }); await review.getByRole('heading', { name: 'Tinjau pengajuan pengembalian', exact: true }).waitFor({ timeout: 60000 });
  assert.equal(await review.getByRole('heading').first().evaluate(node => node === document.activeElement), true); assert.equal(await review.getByRole('textbox').count(), 0); await facts(maker.page, review);
  assert.equal(await review.getByRole('button', { name: 'Simpan pengajuan', exact: true }).isDisabled(), true); await review.getByRole('checkbox', { name: consent, exact: true }).check();
  await maker.page.screenshot({ path: resolve(directory, 'procurement-advance-return-mobile.png'), fullPage: true }); await maker.page.setViewportSize({ width: 1440, height: 1000 }); await facts(maker.page, review); await maker.page.screenshot({ path: resolve(directory, 'procurement-advance-return-desktop.png'), fullPage: true }); await maker.page.setViewportSize({ width: 360, height: 800 });
  await review.getByRole('button', { name: 'Simpan pengajuan', exact: true }).click(); await maker.page.getByRole('region', { name: 'Rincian pengembalian uang muka', exact: true }).getByRole('heading', { name: 'Menunggu pemeriksaan independen', exact: true }).waitFor({ timeout: 60000 });
  const proposed = (await history(maker.page, config.makerId))[0]; assert.ok(proposed); assert.equal(proposed.decision, null); assert.deepEqual(advanceReturnWrites[0].body.input, input); assert.equal(advanceReturnWrites[0].status, 200);
  assert.equal((await query(maker.page, config.makerId, api + '/proposals/' + proposed.proposal.id + '/decision-preview/query', { purpose, decision: 'approve' }, { headers: { 'If-Match': '"1"' } })).status, 403);
  step('whole_credit_two_payouts_reviewed_and_proposed');

  const checker = await user(config.checkerToken); await checker.page.goto(origin.origin + path); await renew(checker, config.checkerId, 'procurement.advancereturn.decide', false); await choose(checker.page); await open(checker.page, proposed.proposal.id, 'Menunggu pemeriksaan independen');
  await checker.page.getByRole('textbox', { name: 'Alasan keputusan', exact: true }).fill(config.decisionReason); await checker.page.getByRole('button', { name: 'Tinjau persetujuan', exact: true }).click();
  const decision = checker.page.getByRole('region', { name: 'Tinjauan pengembalian uang muka', exact: true }); await decision.getByRole('heading', { name: 'Tinjau persetujuan pengembalian', exact: true }).waitFor({ timeout: 60000 }); await facts(checker.page, decision); await decision.getByRole('checkbox', { name: consent, exact: true }).check();
  dropNext(); await decision.getByRole('button', { name: 'Simpan persetujuan', exact: true }).click(); await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ timeout: 60000 });
  const write = advanceReturnWrites.at(-1), references = await pending(checker.page); assert.equal(write.path, api + '/proposals/' + proposed.proposal.id + '/decisions'); assert.equal(write.actor, config.checkerId); assert.equal(write.version, '"1"');
  assert.equal(references.length, 1); assert.deepEqual(Object.keys(references[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.equal(references[0].actorId, config.checkerId); assert.equal(references[0].key, write.key); assert.equal(references[0].operation, 'procurement.advancereturn.decide'); assert.ok(!JSON.stringify(references).includes(input.reason));
  const actualCheckerCookie = (await checker.context.cookies(origin.origin)).find(cookie => cookie.name === '__Host-ut_session');
  await checker.context.addCookies([{ name: '__Host-ut_session', value: config.readerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await checker.page.getByRole('heading', { name: /^(Masuk untuk melanjutkan|Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor({ timeout: 60000 }); assert.deepEqual(await pending(checker.page), references);
  await checker.context.addCookies([actualCheckerCookie]); await checker.page.reload();
  const recovered = checker.page.waitForResponse(response => new URL(response.url()).pathname === api + '/operations/query' && response.request().postDataJSON()?.key === write.key, { timeout: 60000 }); await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); assert.equal((await recovered).status(), 200); await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden', timeout: 60000 }); assert.equal((await pending(checker.page)).length, 0); await choose(checker.page);
  const completed = (await history(checker.page, config.checkerId))[0]; assert.equal(completed.decision.status, 'returned'); assert.equal(completed.decision.returnLineIds.length, 2); assert.ok(completed.decision.journalId); assert.equal(completed.decisionReason, config.decisionReason);
  const available = await query(checker.page, config.checkerId, api + '/receipts/query', { purpose, location, beforeReceiptId: null }); assert.equal(available.status, 200); assert.ok(!available.body.items.some(row => row.receiptId === input.receiptId));
  const reader = await user(config.readerToken); await reader.page.goto(origin.origin + path); await choose(reader.page); const saved = await open(reader.page, proposed.proposal.id, 'Pengembalian dibukukan'); await facts(reader.page, saved);
  for (const name of ['Catat pengembalian uang muka', 'Tinjau persetujuan', 'Tinjau penolakan']) assert.equal(await reader.page.getByRole('button', { name, exact: true }).count(), 0);
  const outsider = await user(config.outsiderToken); await outsider.page.goto(origin.origin + '/__advance_return_scope_probe'); const denied = await query(outsider.page, config.outsiderId, api + '/proposals/' + proposed.proposal.id + '/query', { purpose }); assert.equal(denied.status, 403); for (const field of ['proposal', 'decision', 'source', 'capacity', 'policy']) assert.equal(Object.hasOwn(denied.body, field), false);
  assert.equal(advanceReturnWrites.length, 2); assert.ok(advanceReturnWrites.every(item => item.status === 200)); assert.equal(dropped(), 1); assert.equal(advanceReturnRecoveries.length, 2); assert.equal(advanceReturnRecoveries[0].status, 409); assert.equal(advanceReturnRecoveries[1].status, 200);
  step('independent_decision_original_key_recovery_and_read_without_bank_grants');
  console.log('REAL_PROCUREMENT_ADVANCE_RETURN_BROWSER_PASS ' + JSON.stringify({ wholeCredit: '7000.00', payoutLines: 2, oneSupplierAndOrder: true, originalKeyRecovered: true, actualStepUpRotations: 2, noResend: true, independentDecision: true, mobile: true, desktop: true }));
}
