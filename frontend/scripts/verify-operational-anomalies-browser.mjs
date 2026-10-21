import assert from 'node:assert/strict';
import {resolve} from 'node:path';

export async function verifyOperationalAnomaliesBrowser({config, maker, user, restartUser, origin, directory}) {
  let current = maker; const commands = [], failures = []; let evaluationLosses = 0, policyLosses = 0, reviewLosses = 0;
  const path = origin + 'operations/anomalies', prefix = 'unit-toko.anomaly.pending.' + config.makerId + '.';
  const references = page => page.evaluate(prefix => Object.keys(localStorage).filter(key => key.startsWith(prefix)).map(key => JSON.parse(localStorage.getItem(key))).sort((a, b) => a.key.localeCompare(b.key)), prefix);
  const source = page => page.getByLabel('Sumber yang diizinkan');
  const reason = page => page.getByLabel('Alasan pemeriksaan atau tindak lanjut', {exact: true});
  async function choose(page) {
    page.setDefaultTimeout(60000); await source(page).locator('option[value="' + config.sourceId + '"]').waitFor({state: 'attached'});
    await source(page).selectOption(config.sourceId); await page.getByRole('button', {name: 'Perbarui hasil', exact: true}).waitFor();
  }
  async function intercept(context) {
    await context.route('**/api/v1/operations/anomalies/**', async route => {
      const request = route.request(); if (request.method() !== 'POST') return route.continue();
      const requestPath = new URL(request.url()).pathname, key = request.headers()['idempotency-key']; commands.push({path: requestPath, key});
      const response = await route.fetch(); assert.equal(response.status(), 200);
      if (requestPath.endsWith('/evaluate') && evaluationLosses < 2) {evaluationLosses++; return route.abort('failed');}
      if (requestPath.endsWith('/policy') && policyLosses === 0) {policyLosses++; return route.abort('failed');}
      if (requestPath.endsWith('/disposition') && reviewLosses === 0) {reviewLosses++; return route.abort('failed');}
      return route.fulfill({response});
    });
  }
  async function restart() {
    current = await restartUser(config.financeToken, await current.context.storageState()); current.page.setDefaultTimeout(60000);
    await intercept(current.context); await current.page.goto(path);
    await current.page.getByRole('region', {name: 'Pemulihan tinjauan', exact: true}).waitFor(); return current.page;
  }
  async function recover(page, original) {
    const row = page.locator('[data-recovery-key="' + original.key + '"]');
    await row.getByRole('button').click(); await row.waitFor({state: 'hidden'});
    assert.equal((await references(page)).some(ref => ref.key === original.key), false);
  }
  await intercept(current.context); let page = current.page; page.setDefaultTimeout(60000);
  await page.setViewportSize({width: 1280, height: 900}); await page.goto(path); await choose(page);
  // Dispatch both real forms in one browser turn before storage events can lock the second form.
  // This reproduces the simultaneous-tab race; every retained key belongs to a genuine UI command.
  const popup = page.waitForEvent('popup'); await page.evaluate(url => {window.open(url, 'anomaly-sibling');}, path);
  const sibling = await popup; sibling.setDefaultTimeout(60000); sibling.on('pageerror', error => failures.push(error.message)); await choose(sibling);
  await reason(page).fill('Tinjauan awal sintetis tanpa ambang'); await reason(sibling).fill('Tinjauan serentak sintetis dari tab kedua');
  await page.evaluate(() => {
    const press = doc => {const button = [...doc.querySelectorAll('button')].find(item => item.textContent === 'Buat pemeriksaan baru');
      if (!button || button.disabled) throw new Error('Real evaluation form not ready'); button.click();};
    press(document); const other = window.open('', 'anomaly-sibling'); if (!other) throw new Error('Second review tab unavailable'); press(other.document);
  });
  await Promise.all([page, sibling].map(tab => tab.getByRole('button', {name: 'Ulangi permintaan yang sama', exact: true}).waitFor()));
  assert.equal(evaluationLosses, 2); const originals = await references(page); assert.equal(originals.length, 2); assert.notEqual(originals[0].key, originals[1].key);
  for (const ref of originals) {
    assert.deepEqual(Object.keys(ref).sort(), ['actorId', 'createdAt', 'evaluationId', 'expectedRevision', 'key', 'operation', 'sourceId', 'sourceKind']);
    assert.equal(ref.actorId, config.makerId); assert.equal(ref.operation, 'evaluate'); assert.equal(ref.sourceId, config.sourceId);
  }
  page = await restart(); assert.deepEqual(await references(page), originals);
  await recover(page, originals[0]); assert.deepEqual(await references(page), [originals[1]]);
  await page.getByRole('button', {name: 'Buat pemeriksaan baru', exact: true}).waitFor({state: 'hidden'});
  await recover(page, originals[1]); assert.equal((await references(page)).length, 0); assert.equal(commands.length, 2);
  await choose(page); await page.getByText('Kebijakan belum tersedia', {exact: true}).first().waitFor();
  assert.equal(await page.getByText('Kebijakan belum tersedia', {exact: true}).count(), 2);
  await page.getByLabel('Versi kebijakan', {exact: true}).fill('synthetic-browser-v1');
  await page.getByLabel('Referensi penetapan kebijakan', {exact: true}).fill('SYNTHETIC-TEST-ONLY');
  await page.getByLabel('Batas jumlah paket', {exact: true}).fill('1');
  await page.getByLabel('Alasan perubahan kebijakan', {exact: true}).fill('Kebijakan tinjauan sintetis untuk pembuktian');
  await page.getByRole('button', {name: 'Tinjau kebijakan', exact: true}).click();
  await page.getByRole('heading', {name: 'Konfirmasi kebijakan synthetic-browser-v1', exact: true}).waitFor();
  await page.getByRole('button', {name: 'Simpan kebijakan tinjauan', exact: true}).click();
  await page.getByText('Hasil penyimpanan belum dapat dipastikan. Periksa catatan dengan referensi asli sebelum membuat permintaan lain.', {exact: true}).waitFor();
  assert.equal(policyLosses, 1); const policy = (await references(page))[0]; assert.equal(policy.operation, 'configure');
  page = await restart(); await recover(page, policy);
  await page.getByText('Versi synthetic-browser-v1 terkonfirmasi tersimpan dari permintaan asli.', {exact: true}).waitFor();
  await choose(page); await reason(page).fill('Tinjauan jumlah dengan kebijakan sintetis');
  await page.getByRole('button', {name: 'Buat pemeriksaan baru', exact: true}).click();
  await page.getByText('Perlu tinjauan', {exact: true}).waitFor(); await page.getByText(/batas tinjauan 1 paket pada kebijakan synthetic-browser-v1/).waitFor();
  await page.setViewportSize({width: 1280, height: 900}); await page.screenshot({path: resolve(directory, 'operational-anomalies-desktop.png'), fullPage: true});
  await page.getByLabel('Hasil tinjauan').selectOption('requires_action'); await reason(page).fill('Jumlah perlu dibahas petugas; pesanan tetap pada alurnya');
  await page.getByRole('button', {name: 'Simpan tinjauan', exact: true}).click(); await page.getByRole('button', {name: 'Ulangi permintaan yang sama', exact: true}).waitFor();
  assert.equal(reviewLosses, 1); const review = (await references(page))[0]; assert.equal(review.operation, 'review');
  // A latest-row read cannot prove which original request committed.
  await page.getByRole('button', {name: 'Perbarui hasil', exact: true}).click();
  await page.getByRole('heading', {name: 'Perlu tindak lanjut', exact: true}).waitFor(); assert.deepEqual(await references(page), [review]);
  page = await restart(); await recover(page, review); await choose(page);
  await page.getByText('Jumlah perlu dibahas petugas; pesanan tetap pada alurnya', {exact: true}).waitFor();
  await page.reload(); await choose(page); await page.getByText('Jumlah perlu dibahas petugas; pesanan tetap pada alurnya', {exact: true}).waitFor();
  await page.setViewportSize({width: 360, height: 800}); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({path: resolve(directory, 'operational-anomalies-mobile.png'), fullPage: true});
  const denied = await user(config.deniedToken); await denied.page.goto(path);
  await denied.page.getByRole('alert').filter({hasText: 'Sesi atau izin tinjauan belum tersedia. Periksa akun dan cakupan toko.'}).waitFor();
  await denied.page.getByRole('heading', { name: /^(Masuk untuk melanjutkan|Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor(); assert.equal(await denied.page.getByLabel('Sumber yang diizinkan').count(), 0);
  assert.equal(commands.filter(command => command.path.endsWith('/evaluate')).length, 3); assert.equal(commands.filter(command => command.path.endsWith('/policy')).length, 1);
  assert.equal(commands.filter(command => command.path.endsWith('/disposition')).length, 1); assert.equal(new Set(commands.map(command => command.key)).size, 5); assert.deepEqual(failures, []);
  console.log('REAL_OPERATIONAL_ANOMALIES_BROWSER_PASS ' + JSON.stringify({actualBrowserRestarts: 3, originalReplyLosses: 4, simultaneousTabs: true, referenceOnly: true, exactKeyRecovery: true}));
}
