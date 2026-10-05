// Actual rendered CaseWorkspace + .NET + Mongo. Synthetic local fixture only.
import assert from 'node:assert/strict';
import { writeFileSync } from 'node:fs';
import { basename, resolve } from 'node:path';

export async function verifyReturnLotBrowser({ config, actor, user, origin, directory, returnLotWrites, returnLotRecoveries, dropNext, dropped }) {
  const { page, context } = actor, path = '/fulfillment/cases';
  const base = `/api/v1/fulfillment/cases/${config.caseId}/returns/${config.returnId}/lot-verification`;
  const purpose = 'after-sales-return-lot-verification', operation = 'fulfillment.case.return_lot_verify';
  const observedLotLabel = 'LABEL-RETUR-BROWSER-SINTETIS', reason = 'Petugas sintetis mencocokkan satu kemasan utuh dan label fisik dengan lot pengiriman asli; tetap karantina';
  const consent = 'Saya telah mencocokkan barang, label lot dan kemasan utuh secara fisik dengan lot pengiriman asli yang dipilih. Pernyataan ini tercatat dengan akun saya. Seluruh barang tetap dikarantina; jumlah yang belum dicocokkan memerlukan penanganan manual.';
  const success = () => returnLotWrites.filter(value => value.status === 200), screenshots = [];
  const pending = current => current.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  async function query(current, actorId, suffix, body, headers = {}) {
    return current.evaluate(async ({ actorId, url, body, headers }) => {
      const csrf = await fetch('/api/v1/identity/csrf', { credentials: 'same-origin', cache: 'no-store', redirect: 'error' });
      assertCsrf(csrf.ok); const token = await csrf.json();
      const response = await fetch(url, { method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error',
        headers: { 'Content-Type': 'application/json', 'X-Expected-Actor-Id': actorId, 'X-CSRF-Token': token.requestToken, ...headers }, body: JSON.stringify(body) });
      return { status: response.status, body: await response.json(), etag: response.headers.get('etag'), cacheControl: response.headers.get('cache-control') };
      function assertCsrf(ok) { if (!ok) throw new Error('Actual request token unavailable'); }
    }, { actorId, url: base + suffix, body, headers });
  }
  async function open(current) {
    await current.goto(origin.origin + path);
    await current.getByRole('link', { name: 'Unit Toko', exact: true }).waitFor();
    await current.getByRole('button', { name: `Buka keluhan ${config.caseId}`, exact: true }).click();
    const detail = current.getByRole('region', { name: 'Rincian keluhan', exact: true });
    await detail.getByRole('heading', { name: `Keluhan ${config.caseId}`, exact: true }).waitFor();
    const returns = detail.getByRole('region', { name: 'Penerimaan retur', exact: true });
    await returns.getByRole('button', { name: 'Perbarui riwayat retur', exact: true }).click();
    const inspected = returns.getByRole('region', { name: `Pemeriksaan retur ${config.returnId}`, exact: true });
    await inspected.getByRole('button', { name: `Lihat hasil pemeriksaan ${config.returnId}`, exact: true }).click();
    await inspected.getByRole('region', { name: 'Hasil pemeriksaan barang', exact: true }).waitFor();
    return { detail, inspected, panel: inspected.getByRole('region', { name: `Pencocokan lot penerimaan ${config.returnId}`, exact: true }) };
  }
  async function screen(current, suffix) {
    assert.equal(await current.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    const file = resolve(directory, basename(config.proofPath, '.json') + '-' + suffix + '.png');
    await current.screenshot({ path: file, fullPage: true }); screenshots.push(file);
  }
  const { panel } = await open(page); await panel.waitFor();
  await panel.getByText('Perbarui pencocokan lot untuk melihat lot asli dan jumlah yang dapat diperiksa. Akses terbatas pada petugas keluhan dengan izin pemeriksaan stok.', { exact: true }).waitFor();
  const beforeText = await panel.innerText();
  assert.ok(beforeText.includes('Barang tetap dalam karantina.'));
  assert.ok(beforeText.includes('Pencocokan ini tidak menetapkan gudang tujuan retur, melepas stok, menyetujui pembayaran, atau menyelesaikan pengembalian.'));
  assert.equal(await panel.getByLabel(/^Barang yang dicocokkan/).count(), 0);
  // One real read is prevented, then a different real read is paused: no fake
  // successful data, private source, review, lot, stock or money response.
  await page.route('**' + base + '/query', route => route.abort('failed'), { times: 1 });
  await panel.getByRole('button', { name: 'Perbarui pencocokan lot', exact: true }).click();
  await panel.getByRole('alert').filter({ hasText: 'Koneksi terputus' }).waitFor(); assert.equal(returnLotWrites.length, 0);
  let resume; const paused = new Promise(resolve => { resume = resolve; });
  let entered; const reached = new Promise(resolve => { entered = resolve; });
  await page.route('**' + base + '/query', async route => { entered(); await paused; await route.continue(); }, { times: 1 });
  try {
    await panel.getByRole('button', { name: 'Perbarui pencocokan lot', exact: true }).click(); await reached;
    await panel.getByRole('status').filter({ hasText: 'Memeriksa pencocokan lot…' }).waitFor();
    assert.equal(await panel.getByRole('button', { name: 'Perbarui pencocokan lot', exact: true }).isDisabled(), true);
  } finally { resume(); }
  const selection = panel.getByLabel(/^Barang yang dicocokkan/); await selection.waitFor();
  await panel.getByRole('status').filter({ hasText: 'Memeriksa pencocokan lot…' }).waitFor({ state: 'hidden' });
  assert.equal(await selection.inputValue(), '');
  const lot = panel.getByLabel(/^Lot pengiriman asli/); assert.equal(await lot.inputValue(), ''); assert.equal(await lot.isDisabled(), true);
  await selection.selectOption(config.receiptId + ':' + config.skuId); assert.equal(await lot.inputValue(), '');
  await lot.selectOption(config.originalLotId);
  const count = panel.getByLabel('Jumlah kemasan utuh yang cocok', { exact: true }), label = panel.getByLabel('Label lot yang terlihat dan dicocokkan', { exact: true });
  assert.equal(await count.inputValue(), ''); assert.equal(await label.inputValue(), '');
  await count.fill('2'); await label.fill(observedLotLabel); await panel.getByLabel('Catatan petugas internal', { exact: true }).fill(reason);
  await panel.getByRole('button', { name: 'Tinjau pencocokan lot', exact: true }).click();
  await panel.getByRole('alert').filter({ hasText: 'Pilih lot pengiriman asli dan jumlah kemasan utuh yang sudah dicocokkan secara fisik.' }).waitFor();
  assert.equal(await panel.getByRole('region', { name: 'Konfirmasi pencocokan lot', exact: true }).count(), 0); assert.equal(returnLotWrites.length, 0);
  await count.fill('1'); await panel.getByRole('button', { name: 'Tinjau pencocokan lot', exact: true }).click();
  const review = panel.getByRole('region', { name: 'Konfirmasi pencocokan lot', exact: true });
  const heading = review.getByRole('heading', { name: 'Catat pernyataan pencocokan manual', exact: true }); await heading.waitFor();
  assert.equal(await heading.evaluate(element => element === document.activeElement), true);
  const text = await review.innerText();
  for (const value of [config.productName, `lot asli ${config.originalLotId}`, `${config.baseUnitsPerPackage} ${config.baseUnitCode} dalam kemasan utuh.`, observedLotLabel, reason]) assert.ok(text.includes(value), value);
  assert.equal(await review.getByRole('textbox').count(), 0);
  const save = review.getByRole('button', { name: 'Catat pencocokan — tetap karantina', exact: true });
  assert.equal(await save.isDisabled(), true); const consentControl = review.getByRole('checkbox', { name: consent, exact: true });
  assert.equal(await consentControl.isChecked(), false); await consentControl.focus(); await page.keyboard.press('Space'); assert.equal(await save.isDisabled(), false);
  await context.setOffline(true); await page.getByRole('status').filter({ hasText: 'Koneksi terputus.' }).waitFor(); assert.equal(await save.isDisabled(), true);
  await context.setOffline(false); await page.getByRole('status').filter({ hasText: 'Koneksi terputus.' }).waitFor({ state: 'hidden' });
  await screen(page, 'manual-review-mobile'); await page.setViewportSize({ width: 1440, height: 1000 }); await screen(page, 'manual-review-desktop');
  dropNext(); await save.click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ timeout: 60000 });
  assert.equal(success().length, 1); const committed = success()[0];
  assert.equal(committed.path, base); assert.equal(committed.actor, config.actorId); assert.equal(committed.version, '"0"');
  assert.match(committed.key, new RegExp(`^cl\\.${config.caseId}\\.${config.returnId}\\.[0-9a-f-]{36}$`));
  assert.deepEqual(committed.body, { binding: committed.body.binding, observedLotLabel, confirmPhysicalOriginalLotMatch: true,
    lines: [{ receiptId: config.receiptId, skuId: config.skuId, originalLotId: config.originalLotId, verifiedBaseUnits: config.baseUnitsPerPackage.toString() }], reason });
  assert.match(committed.body.binding, /^[0-9a-f]{64}$/); assert.equal(committed.completed, true);
  assert.equal(committed.response.returnId, config.returnId); assert.equal(committed.response.caseId, config.caseId); assert.equal(committed.response.invoiceId, config.invoiceId);
  assert.equal(committed.response.state, 'verified_original_lot_quarantined'); assert.equal(committed.response.operation, operation);
  const saved = await pending(page); assert.equal(saved.length, 1); assert.deepEqual(Object.keys(saved[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.equal(saved[0].actorId, config.actorId); assert.equal(saved[0].key, committed.key); assert.equal(saved[0].operation, operation);
  for (const value of [observedLotLabel, reason, committed.body.binding]) assert.ok(!JSON.stringify(saved).includes(value));
  // Reload while outcome is uncertain. Receipt-only lookup must use this exact
  // original actor/key; no write retry is offered or sent by this proof.
  await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden' }); assert.equal((await pending(page)).length, 0);
  const refreshed = await open(page); await refreshed.panel.getByRole('button', { name: 'Perbarui pencocokan lot', exact: true }).click();
  await refreshed.panel.getByRole('status').filter({ hasText: 'Pernyataan petugas sudah tersimpan.' }).waitFor();
  const quantityText = await refreshed.panel.getByRole('list', { name: 'Jumlah pencocokan dalam karantina', exact: true }).innerText();
  assert.ok(quantityText.includes(`Diterima ${config.baseUnitsPerPackage} ${config.baseUnitCode}; dicocokkan ${config.baseUnitsPerPackage} ${config.baseUnitCode}; belum dicocokkan 0 ${config.baseUnitCode}.`));
  assert.equal(await refreshed.panel.getByLabel(/^Barang yang dicocokkan/).count(), 0);
  assert.equal(await refreshed.panel.getByRole('button', { name: 'Catat pencocokan — tetap karantina', exact: true }).count(), 0);
  await screen(page, 'persisted-desktop'); await page.setViewportSize({ width: 360, height: 800 }); await screen(page, 'persisted-mobile');
  const final = await query(page, config.actorId, '/query', { purpose }, { 'If-Match': '"2"' });
  assert.equal(final.status, 200); assert.equal(final.etag, '"1"'); assert.ok(final.cacheControl.includes('private') && final.cacheControl.includes('no-store'));
  assert.equal(final.body.caseId, config.caseId); assert.equal(final.body.returnId, config.returnId); assert.equal(final.body.revision, '1');
  assert.equal(final.body.state, 'lot_asli_diverifikasi_tetap_dikarantina'); assert.equal(final.body.lines.length, 1);
  assert.deepEqual({ ...final.body.lines[0], explanation: '' }, { receiptId: config.receiptId, skuId: config.skuId, receivedBaseUnits: config.baseUnitsPerPackage.toString(),
    verifiedBaseUnits: config.baseUnitsPerPackage.toString(), manualBaseUnits: '0', state: 'tetap_dikarantina', explanation: '', choices: [] });
  assert.ok(!JSON.stringify(final.body).includes(observedLotLabel)); assert.equal(success().length, 1);
  const recovered = await query(page, config.actorId, '/operations/query', { purpose: 'operation-recovery', key: committed.key });
  assert.equal(recovered.status, 200); assert.equal(recovered.body.status, 'committed'); assert.deepEqual(recovered.body.result, committed.response);
  const reader = await user(config.readerToken); const read = await open(reader.page);
  assert.equal(await read.inspected.getByRole('region', { name: `Pencocokan lot penerimaan ${config.returnId}`, exact: true }).count(), 0);
  assert.ok(!(await read.detail.innerText()).includes(observedLotLabel));
  const deniedRead = await query(reader.page, config.readerId, '/query', { purpose }, { 'If-Match': '"2"' }); assert.equal(deniedRead.status, 403);
  const deniedWrite = await query(reader.page, config.readerId, '', committed.body, { 'If-Match': '"0"', 'Idempotency-Key': `cl.${config.caseId}.${config.returnId}.00000000-0000-4000-8000-000000000001` });
  assert.equal(deniedWrite.status, 403);
  const foreign = await user(config.foreignToken); await foreign.page.goto(origin.origin + path);
  await foreign.page.getByRole('region', { name: 'Daftar keluhan', exact: true }).getByText('Belum ada pengajuan pada halaman ini.', { exact: true }).waitFor();
  assert.equal(await foreign.page.getByRole('button', { name: `Buka keluhan ${config.caseId}`, exact: true }).count(), 0);
  const foreignRead = await query(foreign.page, config.foreignId, '/query', { purpose }, { 'If-Match': '"2"' }); assert.equal(foreignRead.status, 403);
  const foreignWrite = await query(foreign.page, config.foreignId, '', committed.body, { 'If-Match': '"0"', 'Idempotency-Key': `cl.${config.caseId}.${config.returnId}.00000000-0000-4000-8000-000000000002` });
  assert.equal(foreignWrite.status, 403);
  for (const response of [deniedRead, deniedWrite, foreignRead, foreignWrite])
    for (const key of ['lines', 'binding', 'source', 'input', 'returnId', 'invoiceId']) assert.equal(Object.hasOwn(response.body, key), false);
  assert.equal(success().length, 1); assert.equal(returnLotWrites.length, 3); assert.equal(dropped(), 1);
  assert.equal(returnLotRecoveries.length, 2); for (const recovery of returnLotRecoveries) {
    assert.equal(recovery.status, 200); assert.equal(recovery.actor, config.actorId); assert.equal(recovery.body.key, committed.key); assert.equal(Object.hasOwn(recovery, 'version'), false);
  }
  assert.equal((await pending(page)).length, 0);
  writeFileSync(config.proofPath, JSON.stringify({ localSyntheticOnly: true, hostedProviderAcceptance: false, actualBrowserApiMongo: true,
    caseId: config.caseId, returnId: config.returnId, invoiceId: config.invoiceId, originalLotId: config.originalLotId,
    originalKey: committed.key, successfulWrite: committed, recoveredResult: recovered.body.result, persistedView: final.body,
    actualSuccessfulWriteCount: success().length, responseLosses: dropped(), readOnlyAndForeignDenied: true,
    manualPhysicalConsent: true, noAutoselection: true, overQuantityRefused: true, loadingErrorAndValidation: true,
    mobile: true, desktop: true, keyboard: true, noStockReleaseOrFinancialAuthority: true, screenshotPaths: screenshots }, null, 2) + '\n', { encoding: 'utf8', flag: 'wx' });
  console.log('REAL_RETURN_LOT_BROWSER_PASS ' + JSON.stringify({ proofPath: config.proofPath, returnId: config.returnId, originalLotId: config.originalLotId,
    actualSuccessfulWriteCount: 1, localSyntheticOnly: true, hostedProviderAcceptance: false }));
}
