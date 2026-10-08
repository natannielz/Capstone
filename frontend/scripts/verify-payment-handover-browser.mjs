import assert from 'node:assert/strict';
import {writeFileSync} from 'node:fs';
import {resolve} from 'node:path';

export async function verifyPaymentHandoverBrowser({config, reviewer, user, origin, directory, restartUser}) {
  const writes = [], recoveries = [], prefix = config.proofPrefix;
  let page = reviewer.page, context = reviewer.context;
  const panel = p => p.getByRole('region', {name: 'Serah-terima pemeriksaan pembayaran', exact: true});
  async function open(p) {
    p.setDefaultTimeout(30_000); await p.goto(origin.origin + '/billing/payment-reviews');
    await p.getByRole('button', {name: 'Periksa pengajuan ' + config.declarationId, exact: true}).click();
    await panel(p).getByRole('button', {name: 'Muat pilihan serah-terima', exact: true}).click();
    await panel(p).getByLabel('Delegasi terkait pemeriksaan').selectOption(config.delegationId);
    await panel(p).getByLabel('Alasan serah-terima pemeriksaan').fill('Reviewer pengganti berwenang mengambil pemeriksaan setelah delegasi dicabut.');
    await panel(p).getByRole('button', {name: 'Tinjau serah-terima', exact: true}).click();
    await panel(p).getByRole('heading', {name: 'Ambil tanggung jawab pemeriksaan', exact: true}).waitFor();
  }
  function observe(p) {p.on('request', request => {
    if (request.method() !== 'POST') return;
    const path = new URL(request.url()).pathname;
    if (path.endsWith('/handover')) writes.push({key: request.headers()['idempotency-key'], body: request.postDataJSON()});
    if (path.endsWith('/handover/operations/query')) recoveries.push(request.postDataJSON());
  });}
  observe(page); await page.setViewportSize({width: 1440, height: 1000}); await open(page);
  assert.equal(await page.evaluate(() => document.activeElement?.textContent), 'Ambil tanggung jawab pemeriksaan');
  await page.screenshot({path: resolve(directory, prefix + '-review-1440.png'), fullPage: true});
  await panel(page).getByRole('button', {name: 'Batalkan serah-terima', exact: true}).click(); assert.equal(writes.length, 0);
  await panel(page).getByRole('button', {name: 'Tinjau serah-terima', exact: true}).click();
  const stale = await context.newPage(); await open(stale);
  let lost = false, committed = null, resolveCommit, rejectCommit, commitTimer;
  const originalCommitted = new Promise((resolve, reject) => {resolveCommit = resolve; rejectCommit = reject;});
  await page.route('**/api/v1/billing/payment-reviews/*/handover', async route => {
    if (route.request().method() !== 'POST' || lost) return route.continue();
    try {
      lost = true; const response = await route.fetch({timeout: 30_000}); assert.equal(response.status(), 200); committed = await response.json(); await route.abort('failed'); resolveCommit();
    } catch (error) {rejectCommit(error); await route.abort('failed').catch(() => {});}
  });
  commitTimer = setTimeout(() => rejectCommit(new Error('Actual original handover write did not complete within 30000ms')), 30_000);
  try {await Promise.all([originalCommitted, panel(page).getByRole('button', {name: 'Konfirmasi ambil pemeriksaan', exact: true}).click()]);} finally {clearTimeout(commitTimer);}
  await panel(page).getByRole('heading', {name: 'Hasil serah-terima belum dipastikan', exact: true}).waitFor();
  await page.waitForFunction(() => ![...document.querySelectorAll('p[role="status"]')].some(p => p.textContent === 'Memeriksa serah-terima…'), {timeout: 30_000});
  assert.equal(lost, true); assert.equal(writes.length, 1); assert.equal(committed.assigneeId, config.claimantId);
  assert.equal(committed.handover.originalAssigneeId, config.originalAssigneeId); assert.equal(committed.handover.delegationId, config.delegationId);
  await panel(stale).getByRole('heading', {name: 'Hasil serah-terima belum dipastikan', exact: true}).waitFor();
  assert.equal(await stale.getByRole('region', {name: 'Konfirmasi serah-terima pemeriksaan', exact: true}).count(), 0);
  assert.equal(await stale.getByRole('button', {name: 'Muat pengajuan terbaru', exact: true}).isDisabled(), true);
  await page.screenshot({path: resolve(directory, prefix + '-unknown-1440.png'), fullPage: true});
  const storageState = await context.storageState();
  const references = storageState.origins.flatMap(item => item.localStorage).filter(item => item.name.startsWith('unit-toko.payment-handover.pending.'));
  assert.equal(references.length, 1); assert.equal(JSON.parse(references[0].value).key, writes[0].key);
  ({page, context} = await restartUser(config.financeToken, storageState)); observe(page);
  page.setDefaultTimeout(30_000); await page.goto(origin.origin + '/billing/payment-reviews');
  await panel(page).getByRole('button', {name: 'Periksa hasil serah-terima ' + config.declarationId, exact: true}).waitFor();
  // Recovery is available immediately after restart, without reselecting the declaration or delegation.
  const recoveryTab = await context.newPage(); recoveryTab.setDefaultTimeout(30_000); observe(recoveryTab);
  await recoveryTab.goto(origin.origin + '/billing/payment-reviews');
  await panel(recoveryTab).getByRole('button', {name: 'Periksa hasil serah-terima ' + config.declarationId, exact: true}).click();
  await panel(recoveryTab).getByText('Serah-terima asli ditemukan; tidak ada pengambilalihan kedua.', {exact: true}).waitFor();
  await panel(page).getByRole('heading', {name: 'Hasil serah-terima belum dipastikan', exact: true}).waitFor({state: 'detached'});
  const refreshButton = page.getByRole('button', {name: 'Muat pengajuan terbaru', exact: true});
  // Clearing the durable reference deliberately starts an asynchronous parent queue refresh.
  // Keep the original enabled assertion after the existing 30s UI action boundary settles.
  await page.waitForFunction(button => !button.disabled, await refreshButton.elementHandle(), {timeout: 30_000});
  assert.equal(await refreshButton.isEnabled(), true);
  assert.equal(writes.length, 1); assert.ok(recoveries.some(item => item.key === writes[0].key));
  assert.equal((await context.storageState()).origins.flatMap(item => item.localStorage).filter(item => item.name.startsWith('unit-toko.payment-handover.pending.')).length, 0);
  await recoveryTab.setViewportSize({width: 360, height: 800});
  assert.equal(await recoveryTab.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), true);
  await recoveryTab.screenshot({path: resolve(directory, prefix + '-recovered-360.png'), fullPage: true});
  const reader = await user(config.readerToken); reader.page.setDefaultTimeout(30_000); await reader.page.goto(origin.origin + '/billing/payment-reviews');
  await reader.page.getByRole('button', {name: 'Periksa pengajuan ' + config.declarationId, exact: true}).click();
  await panel(reader.page).getByRole('button', {name: 'Muat pilihan serah-terima', exact: true}).click();
  assert.equal(await panel(reader.page).getByRole('button', {name: 'Konfirmasi ambil pemeriksaan', exact: true}).count(), 0);
  const denied = await user(config.deniedToken); denied.page.setDefaultTimeout(30_000); await denied.page.goto(origin.origin + '/billing/payment-reviews');
  await denied.page.getByText('Belum ada pengajuan yang dapat Anda periksa.', {exact: true}).waitFor();
  assert.equal(await denied.page.getByRole('button', {name: 'Periksa pengajuan ' + config.declarationId, exact: true}).count(), 0);
  writeFileSync(resolve(directory, prefix + '-proof.json'), JSON.stringify({buildId: config.buildId, writes, recoveries, committed,
    actualBrowserRestart: true, sameAccountStaleTabCleared: true, separateTabRecovery: true, cancelledBeforeWrite: true}, null, 2));
  console.log('REAL_PAYMENT_HANDOVER_BROWSER_PASS');
}
