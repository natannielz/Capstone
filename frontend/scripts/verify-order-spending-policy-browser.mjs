// Real shared HTTPS/Next/API/Mongo host. Every participant and business input is synthetic.
import assert from 'node:assert/strict';
import { resolve } from 'node:path';

export async function verifyOrderSpendingPolicyBrowser({ config, maker, user, origin, directory, spendingWrites, spendingRecoveries, dropNext, dropped }) {
  const { page, context } = maker, path = '/ordering/spending-policies', api = '/api/v1/ordering/spending-policies';
  assert.notEqual(config.makerId, config.checkerId);
  const completedWrites = () => spendingWrites.filter(write => write.status === 200);
  const references = current => current.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  const query = (current, actorId, route, body, options = {}) => current.evaluate(async ({ actorId, route, body, options }) => {
    const csrf = await fetch('/api/v1/identity/csrf', { credentials: 'same-origin', cache: 'no-store', redirect: 'error' });
    if (!csrf.ok) throw new Error('Actual request token unavailable'); const token = await csrf.json();
    const headers = { 'Content-Type': 'application/json', 'X-CSRF-Token': token.requestToken, 'X-Expected-Actor-Id': actorId, ...options.headers };
    for (const header of options.omit ?? []) delete headers[header];
    const response = await fetch(route, { method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', headers, body: JSON.stringify(body) });
    const text = await response.text(); return { status: response.status, body: text ? JSON.parse(text) : null };
  }, { actorId, route, body, options });
  async function choose(current) {
    const select = current.getByLabel('Cabang / divisi', { exact: true });
    await select.getByRole('option', { name: config.targetCode + ' · ' + config.targetName + ' — Cabang', exact: true }).waitFor({ state: 'attached' });
    await select.selectOption(config.targetKind + ':' + config.targetId);
    await current.getByRole('region', { name: 'Rincian aturan belanja', exact: true }).waitFor();
  }
  async function pending(current, write, operation) {
    const values = await references(current); assert.equal(values.length, 1);
    assert.deepEqual(Object.keys(values[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.equal(values[0].key, write.key); assert.equal(values[0].actorId, write.actor); assert.equal(values[0].operation, operation);
    for (const privateValue of [config.threshold, write.body.reason, write.body.binding]) assert.ok(!JSON.stringify(values).includes(privateValue));
    assert.equal(await current.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0); return values;
  }
  await page.goto(origin.origin + path); await choose(page);
  await page.getByText('Belum ada aturan belanja yang disetujui untuk unit ini.', { exact: true }).waitFor();
  const readBody = { purpose: 'spending-policy-review', target: { kind: config.targetKind, id: config.targetId } };
  for (const [body, options, status] of [
    [readBody, { omit: ['X-CSRF-Token'] }, 403], [readBody, { omit: ['X-Expected-Actor-Id'] }, 400],
    [{ ...readBody, purpose: 'order-preparation' }, {}, 400],
    [{ ...readBody, target: { ...readBody.target, id: Number(config.targetId) } }, {}, 400],
    [{ ...readBody, unrecognized: true }, {}, 400],
  ]) assert.equal((await query(page, config.makerId, api + '/current/query', body, options)).status, status);
  assert.equal(spendingWrites.length, 0);
  await page.getByRole('button', { name: 'Ajukan aturan belanja', exact: true }).click();
  assert.equal(await page.getByLabel('Kebutuhan persetujuan', { exact: true }).inputValue(), '');
  await page.getByLabel('Kebutuhan persetujuan', { exact: true }).selectOption('above_threshold');
  await page.getByLabel('Ambang belanja (IDR)', { exact: true }).fill(config.threshold);
  await page.getByLabel('Mulai berlaku (WIB)', { exact: true }).fill(config.validFromLocal);
  await page.getByLabel('Berakhir (WIB)', { exact: true }).fill(config.validUntilLocal);
  await page.getByLabel('Alasan pengajuan', { exact: true }).fill('Aturan belanja sintetis cabang untuk pemeriksaan independen');
  await page.getByRole('button', { name: 'Periksa rincian pengajuan', exact: true }).click();
  const review = page.getByRole('region', { name: 'Rincian pengajuan aturan', exact: true }); await review.waitFor();
  assert.equal(await review.getByRole('heading', { name: 'Rincian yang akan diajukan', exact: true }).evaluate(element => element === document.activeElement), true);
  assert.match(await review.innerText(), /Rp500\.000,25/); assert.equal(await review.getByRole('textbox').count(), 0);
  assert.equal(await review.getByRole('button', { name: 'Kirim pengajuan aturan', exact: true }).isDisabled(), true);
  await review.getByRole('checkbox').check(); await context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor();
  assert.equal(await review.getByRole('button', { name: 'Kirim pengajuan aturan', exact: true }).isDisabled(), true); assert.equal(spendingWrites.length, 0);
  await context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'order-spending-policy-proposal-mobile.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'order-spending-policy-proposal-desktop.png'), fullPage: true });
  dropNext(); await review.getByRole('button', { name: 'Kirim pengajuan aturan', exact: true }).click();
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor(); assert.equal(completedWrites().length, 1);
  const proposed = completedWrites()[0]; assert.equal(proposed.path, api + '/proposals'); assert.equal(proposed.actor, config.makerId);
  assert.deepEqual(proposed.body.input.target, { kind: config.targetKind, id: config.targetId });
  assert.deepEqual(proposed.body.input.rule, { mode: 'above_threshold', threshold: config.threshold });
  assert.equal(Date.parse(proposed.body.input.validFrom), Date.parse(config.validFrom)); assert.equal(Date.parse(proposed.body.input.validUntil), Date.parse(config.validUntil));
  const savedReference = await pending(page, proposed, 'ordering.spending.policy.propose');
  await context.addCookies([{ name: '__Host-ut_session', value: config.checkerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('heading', { name: 'Periksa sesi dan akses', exact: true }).waitFor();
  assert.deepEqual(await references(page), savedReference);
  await context.addCookies([{ name: '__Host-ut_session', value: config.makerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden' });
  assert.equal(spendingWrites.length, 1); assert.equal((await references(page)).length, 0); await choose(page);
  assert.equal((await query(page, config.makerId, api + '/preview/query', {
    purpose: 'spending-policy-review', input: { ...proposed.body.input, currency: 'USD' },
  })).status, 400);
  const row = current => current.getByRole('button', { name: /Persetujuan di atas ambang belanja.*Pengajuan/ });
  await row(page).click(); await page.getByRole('heading', { name: 'Menunggu pemeriksaan', exact: true }).waitFor();
  assert.equal(await page.getByRole('button', { name: 'Tinjau persetujuan aturan', exact: true }).count(), 0);
  const checker = await user(config.checkerToken); await checker.page.goto(origin.origin + path); await choose(checker.page); await row(checker.page).click();
  await checker.page.getByLabel('Alasan keputusan', { exact: true }).fill('Aturan dan masa berlaku sintetis diperiksa oleh orang independen');
  await checker.page.getByRole('button', { name: 'Tinjau persetujuan aturan', exact: true }).click();
  const decision = checker.page.getByRole('region', { name: 'Rincian keputusan aturan', exact: true }); await decision.waitFor();
  assert.equal(await decision.getByRole('heading', { name: 'Pastikan persetujuan aturan', exact: true }).evaluate(element => element === document.activeElement), true);
  assert.equal(await decision.getByRole('button', { name: 'Setujui aturan', exact: true }).isDisabled(), true); await decision.getByRole('checkbox').check();
  const history = await query(checker.page, config.checkerId, api + '/proposals/query', readBody);
  assert.equal(history.status, 200); assert.equal(history.body.items.length, 1); assert.equal(history.body.items[0].state, 'pending');
  const proposalId = history.body.items[0].proposalId, decisionPath = api + '/proposals/' + proposalId + '/decisions';
  const decisionPreview = await query(checker.page, config.checkerId, api + '/proposals/' + proposalId + '/decision-preview/query', { purpose: 'spending-policy-review', decision: 'approve' });
  assert.equal(decisionPreview.status, 200); assert.equal(decisionPreview.body.view.proposalId, proposalId);
  const rejectedCommandKey = 'browser-spending-missing-precondition';
  assert.equal((await query(checker.page, config.checkerId, decisionPath,
    { decision: 'approve', binding: decisionPreview.body.binding, reason: 'Synthetic missing precondition probe' },
    { headers: { 'Idempotency-Key': rejectedCommandKey } })).status, 400);
  assert.equal(completedWrites().length, 1);
  const unchangedProposal = await query(checker.page, config.checkerId, api + '/proposals/' + proposalId + '/query', { purpose: 'spending-policy-review' });
  assert.equal(unchangedProposal.status, 200); assert.equal(unchangedProposal.body.state, 'pending');
  dropNext(); await decision.getByRole('button', { name: 'Setujui aturan', exact: true }).click();
  await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor(); assert.equal(completedWrites().length, 2);
  const approved = completedWrites()[1]; assert.equal(approved.path, decisionPath); assert.equal(approved.actor, config.checkerId); assert.equal(approved.body.decision, 'approve'); assert.equal(approved.version, '"1"');
  await pending(checker.page, approved, 'ordering.spending.policy.approve');
  await checker.page.reload(); await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden' });
  assert.equal(completedWrites().length, 2); assert.equal(dropped(), 2); assert.equal((await references(checker.page)).length, 0);
  await choose(checker.page); await checker.page.getByRole('heading', { name: 'Aturan aktif', exact: true }).waitFor();
  assert.match(await checker.page.getByRole('region', { name: 'Rincian aturan belanja', exact: true }).innerText(), /Rp500\.000,25/);
  assert.equal(await checker.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await checker.page.screenshot({ path: resolve(directory, 'order-spending-policy-approved-mobile.png'), fullPage: true });
  const reader = await user(config.readerToken); await reader.page.goto(origin.origin + path); await choose(reader.page);
  await reader.page.getByRole('heading', { name: 'Aturan aktif', exact: true }).waitFor();
  assert.equal(await reader.page.getByRole('button', { name: 'Ajukan aturan belanja', exact: true }).count(), 0);
  await row(reader.page).click(); await reader.page.getByRole('heading', { name: 'Disetujui', exact: true }).waitFor();
  assert.equal(await reader.page.getByRole('button', { name: 'Tinjau persetujuan aturan', exact: true }).count(), 0);
  assert.equal(spendingRecoveries.length, 3); assert.equal(spendingRecoveries[0].status, 409);
  assert.equal(spendingRecoveries[1].status, 200); assert.equal(spendingRecoveries[1].body.key, proposed.key);
  assert.equal(spendingRecoveries[2].status, 200); assert.equal(spendingRecoveries[2].body.key, approved.key);
  const wrongKey = await query(checker.page, config.checkerId, api + '/operations/query', { operation: 'ordering.spending.policy.approve', key: proposed.key, purpose: 'operation-recovery' });
  assert.equal(wrongKey.status, 200); assert.deepEqual(wrongKey.body, { status: 'not_confirmed', result: null });
  const outsider = await user(config.outsiderToken); await outsider.page.goto(origin.origin + '/__spending_scope_probe');
  const foreign = await query(outsider.page, config.outsiderId, api + '/current/query', { purpose: 'spending-policy-review', target: { kind: config.targetKind, id: config.targetId } });
  assert.equal(foreign.status, 403); assert.equal(foreign.body.code, 'scope_denied');
  for (const field of ['rule', 'policyId', 'versionId', 'validFrom', 'validUntil', 'name']) assert.equal(Object.hasOwn(foreign.body, field), false);
  assert.equal(spendingWrites.length, 3); assert.equal(completedWrites().length, 2);
  const rejected = spendingWrites.filter(write => write.status !== 200); assert.equal(rejected.length, 1);
  assert.equal(rejected[0].path, decisionPath); assert.equal(rejected[0].key, rejectedCommandKey); assert.equal(rejected[0].status, 400); assert.equal(rejected[0].version, undefined);
  console.log('REAL_ORDER_SPENDING_POLICY_BROWSER_PASS ' + JSON.stringify({ writes: completedWrites().length, rejectedCommands: rejected.length, requestGuards: true, originalReplyLosses: dropped(), exactActorRecovery: true,
    approvedRule: true, boundedWindow: true, independentApproval: true, noBusinessEffects: true, offlineUnsent: true, mobile: true, desktop: true }));
}
