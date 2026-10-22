// Actual shared HTTPS/Next/API/Mongo host; all identities, mappings and inputs are synthetic.
import assert from 'node:assert/strict';
import { resolve } from 'node:path';

export async function verifyAccountingBooksBrowser({ config, maker, user, origin, directory, bookWrites, bookRecoveries, dropNext, dropped }) {
  const { page, context } = maker, path = '/accounting/books', api = '/api/v1/accounting/books';
  const key = { organizationId: config.organizationId, shopId: config.shopId, channelId: config.channelId, currency: config.currency };
  const completed = () => bookWrites.filter(write => write.status === 200), canonical = [key.organizationId, key.shopId, key.channelId, key.currency].join(':');
  assert.notEqual(config.makerId, config.checkerId);
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
    await select.getByRole('option', { name: [config.organizationName, config.shopName, config.channelName, config.currency].join(' · '), exact: true }).waitFor({ state: 'attached' });
    await select.selectOption(canonical); await current.getByRole('region', { name: 'Rincian buku akuntansi', exact: true }).waitFor();
  }
  async function pending(current, write, operation) {
    const values = await references(current); assert.equal(values.length, 1);
    assert.deepEqual(Object.keys(values[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.equal(values[0].key, write.key); assert.equal(values[0].actorId, write.actor); assert.equal(values[0].operation, operation);
    for (const secret of ['ASTOCK', write.body.reason, write.body.binding]) assert.ok(!JSON.stringify(values).includes(secret));
    assert.equal(await current.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0); return values;
  }
  async function facts(region) {
    const text = await region.innerText();
    for (const value of [config.organizationCode, config.organizationName, config.shopCode, config.shopName, config.channelCode, config.channelName, config.initialMonth, 'inventory', 'ASTOCK', 'payable', 'LAP']) assert.ok(text.includes(value), value);
  }
  await page.goto(origin.origin + path); await choose(page);
  await page.getByText('Buku dan periode pertama belum disiapkan.', { exact: true }).waitFor();
  const readBody = { purpose: 'accounting-book-review', key };
  for (const [body, options, expected] of [
    [readBody, { omit: ['X-CSRF-Token'] }, 403], [readBody, { omit: ['X-Expected-Actor-Id'] }, 400],
    [{ ...readBody, purpose: 'spending-policy-review' }, {}, 400], [{ ...readBody, key: { ...key, shopId: Number(key.shopId) } }, {}, 400],
    [{ ...readBody, unrecognized: true }, {}, 400],
  ]) assert.equal((await query(page, config.makerId, api + '/current/query', body, options)).status, expected);
  await page.getByRole('button', { name: 'Ajukan buku akuntansi', exact: true }).click();
  for (const label of ['Periode pertama', 'Kegunaan akun 1', 'Kode akun 1']) assert.equal(await page.getByLabel(label, { exact: true }).inputValue(), '');
  await page.getByLabel('Periode pertama', { exact: true }).fill(config.initialMonth);
  await page.getByLabel('Kegunaan akun 1', { exact: true }).fill('inventory'); await page.getByLabel('Kode akun 1', { exact: true }).fill('ASTOCK');
  await page.getByRole('button', { name: 'Tambah pemetaan akun', exact: true }).click();
  await page.getByLabel('Kegunaan akun 2', { exact: true }).fill('payable'); await page.getByLabel('Kode akun 2', { exact: true }).fill('LAP');
  await page.getByLabel('Alasan pengajuan', { exact: true }).fill('Konfigurasi awal sintetis untuk pemeriksaan independen');
  await page.getByRole('button', { name: 'Periksa rincian pengajuan', exact: true }).click();
  const review = page.getByRole('region', { name: 'Rincian pengajuan buku', exact: true }); await review.waitFor(); await facts(review);
  assert.equal(await review.getByRole('heading', { name: 'Rincian yang akan diajukan', exact: true }).evaluate(element => element === document.activeElement), true);
  assert.equal(await review.getByRole('textbox').count(), 0); assert.equal(await review.getByRole('button', { name: 'Kirim pengajuan buku', exact: true }).isDisabled(), true);
  await review.getByRole('checkbox').check(); await context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor();
  assert.equal(await review.getByRole('button', { name: 'Kirim pengajuan buku', exact: true }).isDisabled(), true); assert.equal(bookWrites.length, 0);
  await context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'accounting-books-proposal-mobile.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'accounting-books-proposal-desktop.png'), fullPage: true });
  dropNext(); await review.getByRole('button', { name: 'Kirim pengajuan buku', exact: true }).click();
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor(); assert.equal(completed().length, 1);
  const proposed = completed()[0]; assert.equal(proposed.path, api + '/proposals'); assert.equal(proposed.actor, config.makerId);
  assert.deepEqual(proposed.body.input, { key, initialMonth: config.initialMonth, mappings: [{ mappingKey: 'inventory', accountCode: 'ASTOCK' }, { mappingKey: 'payable', accountCode: 'LAP' }] });
  const saved = await pending(page, proposed, 'accounting.book.propose');
  await context.addCookies([{ name: '__Host-ut_session', value: config.checkerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('heading', { name: /^(Masuk untuk melanjutkan|Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor();
  assert.deepEqual(await references(page), saved);
  await context.addCookies([{ name: '__Host-ut_session', value: config.makerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden' });
  assert.equal(bookWrites.length, 1); assert.equal((await references(page)).length, 0); await choose(page);
  assert.equal((await query(page, config.makerId, api + '/preview/query', { purpose: 'accounting-book-review', input: { ...proposed.body.input, balance: '0.00' } })).status, 400);
  const row = current => current.getByRole('button', { name: /Periode pertama.*Pengajuan/ });
  await row(page).click(); await page.getByRole('heading', { name: 'Menunggu pemeriksaan', exact: true }).waitFor();
  assert.equal(await page.getByRole('button', { name: 'Tinjau persetujuan buku', exact: true }).count(), 0);
  const checker = await user(config.checkerToken); await checker.page.goto(origin.origin + path); await choose(checker.page); await row(checker.page).click();
  await checker.page.getByLabel('Alasan keputusan', { exact: true }).fill('Buku dan pemetaan sintetis diperiksa oleh orang independen');
  await checker.page.getByRole('button', { name: 'Tinjau persetujuan buku', exact: true }).click();
  const decision = checker.page.getByRole('region', { name: 'Rincian keputusan buku', exact: true }); await decision.waitFor(); await facts(decision);
  assert.equal(await decision.getByRole('heading', { name: 'Pastikan persetujuan buku', exact: true }).evaluate(element => element === document.activeElement), true);
  assert.equal(await decision.getByRole('button', { name: 'Setujui buku', exact: true }).isDisabled(), true); await decision.getByRole('checkbox').check();
  const history = await query(checker.page, config.checkerId, api + '/proposals/query', readBody);
  assert.equal(history.status, 200); assert.equal(history.body.items.length, 1); assert.equal(history.body.items[0].state, 'pending');
  const proposalId = history.body.items[0].id, decisionPath = api + '/proposals/' + proposalId + '/decisions';
  const preview = await query(checker.page, config.checkerId, api + '/proposals/' + proposalId + '/decision-preview/query', { purpose: 'accounting-book-review', decision: 'approve' });
  assert.equal(preview.status, 200); assert.equal(preview.body.proposal.id, proposalId);
  const rejectedKey = 'browser-book-missing-precondition';
  assert.equal((await query(checker.page, config.checkerId, decisionPath, { decision: 'approve', binding: preview.body.binding, reason: 'Synthetic missing precondition probe' }, { headers: { 'Idempotency-Key': rejectedKey } })).status, 400);
  assert.equal(completed().length, 1);
  const unchanged = await query(checker.page, config.checkerId, api + '/proposals/' + proposalId + '/query', { purpose: 'accounting-book-review' });
  assert.equal(unchanged.status, 200); assert.equal(unchanged.body.state, 'pending');
  dropNext(); await decision.getByRole('button', { name: 'Setujui buku', exact: true }).click();
  await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor(); assert.equal(completed().length, 2);
  const approved = completed()[1]; assert.equal(approved.path, decisionPath); assert.equal(approved.actor, config.checkerId); assert.equal(approved.version, '"1"');
  await pending(checker.page, approved, 'accounting.book.approve');
  await checker.page.reload(); await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden' });
  assert.equal(completed().length, 2); assert.equal(dropped(), 2); assert.equal((await references(checker.page)).length, 0);
  await choose(checker.page); await checker.page.getByRole('heading', { name: 'Buku telah disiapkan', exact: true }).waitFor(); await facts(checker.page.getByRole('region', { name: 'Rincian buku akuntansi', exact: true }));
  assert.equal(await checker.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await checker.page.screenshot({ path: resolve(directory, 'accounting-books-approved-mobile.png'), fullPage: true });
  const reader = await user(config.readerToken); await reader.page.goto(origin.origin + path); await choose(reader.page);
  await reader.page.getByRole('heading', { name: 'Buku telah disiapkan', exact: true }).waitFor(); assert.equal(await reader.page.getByRole('button', { name: 'Ajukan buku akuntansi', exact: true }).count(), 0);
  await row(reader.page).click(); await reader.page.getByRole('heading', { name: 'Disetujui', exact: true }).waitFor(); assert.equal(await reader.page.getByRole('button', { name: 'Tinjau persetujuan buku', exact: true }).count(), 0);
  assert.equal(bookRecoveries.length, 3); assert.equal(bookRecoveries[0].status, 409);
  assert.equal(bookRecoveries[1].status, 200); assert.equal(bookRecoveries[1].body.key, proposed.key); assert.equal(bookRecoveries[2].status, 200); assert.equal(bookRecoveries[2].body.key, approved.key);
  const wrong = await query(checker.page, config.checkerId, api + '/operations/query', { purpose: 'operation-recovery', operation: 'accounting.book.approve', key: proposed.key });
  assert.equal(wrong.status, 200); assert.deepEqual(wrong.body, { status: 'not_confirmed', result: null });
  const outsider = await user(config.outsiderToken); await outsider.page.goto(origin.origin + '/__book_scope_probe');
  const foreign = await query(outsider.page, config.outsiderId, api + '/current/query', readBody);
  assert.equal(foreign.status, 403); assert.equal(foreign.body.code, 'scope_denied');
  for (const field of ['setup', 'configured', 'mappings', 'bookId', 'chartId', 'periodId']) assert.equal(Object.hasOwn(foreign.body, field), false);
  assert.equal(bookWrites.length, 3); assert.equal(completed().length, 2);
  const rejected = bookWrites.filter(write => write.status !== 200); assert.equal(rejected.length, 1);
  assert.equal(rejected[0].path, decisionPath); assert.equal(rejected[0].key, rejectedKey); assert.equal(rejected[0].status, 400); assert.equal(rejected[0].version, undefined);
  console.log('REAL_ACCOUNTING_BOOK_BROWSER_PASS ' + JSON.stringify({ writes: completed().length, rejectedCommands: rejected.length, requestGuards: true, originalReplyLosses: dropped(),
    exactActorRecovery: true, independentApproval: true, explicitMappings: true, offlineUnsent: true, mobile: true, desktop: true }));
}
