// Real Next/Kestrel/Mongo: explicit bulk package input produces one private draft only.
import assert from 'node:assert/strict';
import {writeFile} from 'node:fs/promises';
import {resolve} from 'node:path';

export async function verifyOrderBulkBrowser({config, buyer, user, origin, directory, restartUser}) {
  const writes = [], quotes = [], bulkReads = [], recoveries = [], unexpectedWrites = [], errors = [], pages = [];
  const path = '/ordering/orders', api = '/api/v1/ordering', bulkApi = '/api/v1/catalog/variants/exact-skus/query', expected = config.expectedInput;
  const bulk = page => page.getByRole('region', {name: 'Masukkan banyak SKU', exact: true});
  const bulkInput = page => bulk(page).getByRole('textbox', {name: 'Daftar SKU dan jumlah kemasan', exact: true});
  const bulkReview = page => page.getByRole('region', {name: 'Konfirmasi daftar SKU', exact: true});
  const repeat = page => page.getByRole('region', {name: 'Pemesanan ulang', exact: true});
  const review = page => page.getByRole('region', {name: 'Konfirmasi draf', exact: true});
  const pending = page => page.evaluate(actor => Object.keys(localStorage)
    .filter(key => key.startsWith('unit-toko:pending:v1:' + actor + ':'))
    .map(key => JSON.parse(localStorage.getItem(key))), config.buyerId);
  const observe = page => {page.setDefaultTimeout(30000); page.setDefaultNavigationTimeout(30000); page.on('pageerror', error => errors.push(error.message));};
  const query = (page, actor, route, body) => page.evaluate(async ({actor, route, body}) => {
    const tokenResponse = await fetch('/api/v1/identity/csrf', {credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: AbortSignal.timeout(30000)});
    if (!tokenResponse.ok) throw new Error('Actual request token unavailable'); const token = await tokenResponse.json();
    const response = await fetch(route, {method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: AbortSignal.timeout(30000),
      headers: {'Content-Type': 'application/json', 'X-CSRF-Token': token.requestToken, 'X-Expected-Actor-Id': actor}, body: JSON.stringify(body)});
    return {status: response.status, body: await response.json()};
  }, {actor, route, body});
  async function intercept(context) {
    await context.route('**/api/v1/**', async route => {
      const request = route.request(), routePath = new URL(request.url()).pathname;
      const write = request.method() === 'POST' && routePath === api + '/drafts';
      const quote = routePath === api + '/quote/query', recovery = routePath === api + '/drafts/operations/query', bulkRead = routePath === bulkApi;
      if (request.method() === 'POST' && request.headers()['idempotency-key'] && !write) unexpectedWrites.push(routePath);
      if (!write && !quote && !recovery && !bulkRead) return route.continue();
      try {
        const response = await route.fetch({timeout: 30000});
        const record = {path: routePath, actor: request.headers()['x-expected-actor-id'], key: request.headers()['idempotency-key'],
          revision: request.headers()['if-match'], input: request.postDataJSON(), status: response.status(), result: await response.json()};
        (write ? writes : quote ? quotes : bulkRead ? bulkReads : recoveries).push(record);
        // The command genuinely commits. Lose only that response; never mock its result or replay its write.
        if (write && response.status() === 200) await route.abort('failed'); else await route.fulfill({response});
      } catch (failure) {errors.push(String(failure)); await route.abort('failed');}
    });
  }
  async function selected(page) {
    await page.getByRole('button', {name: 'Lihat pesanan ' + config.orderId, exact: true}).click();
    await page.getByRole('region', {name: 'Rincian pesanan', exact: true}).waitFor();
    await page.getByRole('button', {name: 'Pesan ulang sebagai draf', exact: true}).click();
    await repeat(page).waitFor();
    await page.waitForFunction(() => document.activeElement?.getAttribute('aria-label') === 'Pemesanan ulang');
    await fields(page, config.originalPackages);
  }
  async function fields(page, packages = config.bulkPackages) {
    for (let index = 0; index < expected.selections.length; index++)
      assert.equal(await page.getByLabel('Jumlah kemasan ' + (index + 1), {exact: true}).inputValue(), String(packages));
    assert.equal(await page.getByLabel('Referensi pembelian (opsional)', {exact: true}).inputValue(), '');
    assert.equal(await page.getByRole('checkbox', {name: 'Isi rencana pengiriman', exact: true}).isChecked(), true);
    for (const [label, key] of [['Nama penerima', 'recipientName'], ['Telepon penerima', 'phone'], ['Alamat pengiriman', 'address'],
      ['Kota', 'city'], ['Kode pos (opsional)', 'postalCode'], ['Catatan pengiriman (opsional)', 'instructions']])
      assert.equal(await page.getByLabel(label, {exact: true}).inputValue(), expected.delivery[key] ?? '');
  }
  async function inspectBulk(page, text, expectedStatus = 200) {
    const before = bulkReads.length;
    assert.equal(await bulkInput(page).count(), 1);
    await bulkInput(page).fill(text);
    await bulk(page).getByRole('button', {name: 'Periksa daftar SKU', exact: true}).click();
    if (expectedStatus === 200) await bulkReview(page).waitFor();
    else await bulk(page).getByRole('alert').filter({hasText: 'Sebagian SKU tidak tersedia untuk akun ini.'}).waitFor();
    assert.equal(bulkReads.length, before + 1);
    const read = bulkReads.at(-1); assert.equal(read.status, expectedStatus); assert.equal(read.actor, config.buyerId);
    assert.equal(writes.length, 0); return read;
  }
  async function reviewBulk(page) {
    await page.getByRole('button', {name: 'Masukkan banyak SKU', exact: true}).click();
    const read = await inspectBulk(page, config.skuCode + ';' + config.bulkPackages);
    assert.deepEqual(read.input, {codes: [config.skuCode]}); assert.equal(read.result.length, 1);
    assert.equal(read.result[0].skuId, expected.selections[0].skuId);
    assert.equal(read.result[0].code, config.skuCode);
    assert.equal(read.result[0].definition.id, expected.selections[0].definitionId);
    assert.equal(read.result[0].definition.version, expected.selections[0].definitionVersion);
    assert.equal(read.result[0].currentPrice.id, expected.selections[0].priceId);
    assert.equal(read.result[0].currentPrice.version, expected.selections[0].priceVersion);
    await page.waitForFunction(() => document.activeElement?.getAttribute('aria-label') === 'Konfirmasi daftar SKU');
    await bulkReview(page).getByText('Jumlah SKU ini dalam draf akan diganti menjadi ' + config.bulkPackages + ' setelah Anda menerapkan daftar.', {exact: true}).waitFor();
    await fields(page, config.originalPackages);
  }
  async function applyBulk(page) {
    await reviewBulk(page);
    await bulkReview(page).getByRole('button', {name: 'Terapkan daftar ke draf', exact: true}).click();
    await bulk(page).waitFor({state: 'hidden'}); await fields(page);
    assert.equal(await review(page).count(), 0); assert.equal(writes.length, 0);
  }
  async function preview(page) {
    const before = quotes.length; await page.getByRole('button', {name: 'Tinjau perubahan draf', exact: true}).click();
    await review(page).waitFor(); await page.waitForFunction(() => document.activeElement?.getAttribute('aria-label') === 'Konfirmasi draf');
    assert.equal(quotes.length, before + 1); const quote = quotes.at(-1);
    assert.equal(quote.status, 200); assert.equal(quote.actor, config.buyerId);
    assert.deepEqual(quote.input, {purpose: 'order-preparation', context: expected.context, selections: expected.selections});
    assert.ok((await review(page).innerText()).includes(config.orderId));
    assert.ok((await review(page).innerText()).includes('Draf belum menjadi pesanan'));
    assert.equal(writes.length, 0);
  }
  async function snapshot(page, label) {
    for (const [width, height, size] of [[360, 800, 'mobile'], [1440, 1000, 'desktop']]) {
      await page.setViewportSize({width, height});
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
      await page.screenshot({path: resolve(directory, config.proofPrefix + '-' + label + '-' + size + '.png'), fullPage: true});
    }
    await page.setViewportSize({width: 360, height: 800});
  }
  try {
    observe(buyer.page); await intercept(buyer.context); await buyer.page.goto(origin.origin + path);
    await selected(buyer.page);
    await buyer.page.getByRole('button', {name: 'Masukkan banyak SKU', exact: true}).click();
    for (const [text, issue] of [
      [config.skuCode + ';2\n' + config.skuCode.toLowerCase() + ';4', 'SKU yang sama hanya boleh dicantumkan sekali.'],
      [config.skuCode + ';0', 'Jumlah kemasan harus bilangan bulat'],
      [config.skuCode + ';1.5', 'Jumlah kemasan harus bilangan bulat'],
      [config.skuCode, 'Pisahkan SKU dan jumlah kemasan'],
    ]) {
      const reads = bulkReads.length, quoteCount = quotes.length;
      assert.equal(await bulkInput(buyer.page).count(), 1);
      await bulkInput(buyer.page).fill(text);
      await bulk(buyer.page).getByRole('button', {name: 'Periksa daftar SKU', exact: true}).click();
      await bulk(buyer.page).getByRole('alert').filter({hasText: issue}).waitFor();
      assert.equal(bulkReads.length, reads); assert.equal(quotes.length, quoteCount); assert.equal(writes.length, 0);
      assert.equal(await bulkReview(buyer.page).count(), 0); await fields(buyer.page, config.originalPackages);
    }
    for (const code of [config.missingCode, config.inactiveCode, config.hiddenCode]) {
      const read = await inspectBulk(buyer.page, code + ';2', 403);
      assert.deepEqual(read.input, {codes: [code]}); assert.equal(Array.isArray(read.result), false);
      assert.equal(Object.hasOwn(read.result, 'skuId'), false); assert.equal(await bulkReview(buyer.page).count(), 0);
      await fields(buyer.page, config.originalPackages);
    }
    await bulk(buyer.page).getByRole('button', {name: 'Batalkan daftar SKU', exact: true}).click();
    await reviewBulk(buyer.page); await snapshot(buyer.page, 'bulk-preview');
    await bulk(buyer.page).getByRole('button', {name: 'Batalkan daftar SKU', exact: true}).click();
    await bulk(buyer.page).waitFor({state: 'hidden'}); await fields(buyer.page, config.originalPackages);
    assert.equal(quotes.length, 0); assert.equal(writes.length, 0); assert.deepEqual(await pending(buyer.page), []);
    await applyBulk(buyer.page); await preview(buyer.page); await snapshot(buyer.page, 'current-review');

    const second = await buyer.context.newPage(); pages.push(second); observe(second);
    await second.goto(origin.origin + path); await selected(second); await applyBulk(second); await preview(second);
    await review(buyer.page).getByRole('button', {name: 'Konfirmasi simpan draf', exact: true}).click();
    await buyer.page.getByText('Hasil penyimpanan belum dapat dipastikan. Gunakan Periksa hasil; jangan membuat draf pengganti untuk permintaan ini.', {exact: true}).waitFor();
    await buyer.page.getByRole('button', {name: 'Periksa hasil', exact: true}).waitFor();
    await repeat(second).waitFor({state: 'hidden'}); await review(second).waitFor({state: 'hidden'}); await bulkReview(second).waitFor({state: 'hidden'});
    assert.equal(await second.getByRole('button', {name: 'Muat pesanan terbaru', exact: true}).isDisabled(), true);
    assert.equal(await second.getByRole('button', {name: 'Konfirmasi simpan draf', exact: true}).count(), 0);
    assert.equal(writes.length, 1); const write = writes[0];
    assert.equal(write.status, 200); assert.equal(write.actor, config.buyerId); assert.equal(write.revision, '"0"'); assert.ok(write.key);
    assert.deepEqual(write.input, {draftId: null, input: expected, reason: 'Buat draf pemesanan ulang dari pesanan ' + config.orderId});
    const references = await pending(buyer.page); assert.equal(references.length, 1); const reference = references[0];
    assert.deepEqual(Object.keys(reference).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.equal(reference.actorId, config.buyerId); assert.equal(reference.operation, 'ordering.draft.save'); assert.equal(reference.key, write.key);
    assert.equal(Number.isNaN(Date.parse(reference.createdAt)), false); assert.deepEqual(await pending(second), references);

    // storageState contains cookies/localStorage only; the browser process and both old pages are closed.
    const restarted = await restartUser(config.financeToken, await buyer.context.storageState());
    assert.equal(buyer.page.isClosed(), true); assert.equal(second.isClosed(), true);
    observe(restarted.page); await intercept(restarted.context); await restarted.page.goto(origin.origin + path);
    assert.equal(await restarted.page.evaluate(() => sessionStorage.length), 0);
    await restarted.page.getByRole('button', {name: 'Periksa hasil', exact: true}).waitFor();
    assert.deepEqual(await pending(restarted.page), references); assert.equal(await repeat(restarted.page).count(), 0);
    assert.equal(await restarted.page.getByRole('region', {name: 'Rincian pesanan', exact: true}).count(), 0);
    const waitingTab = await restarted.context.newPage(); pages.push(waitingTab); observe(waitingTab); await waitingTab.goto(origin.origin + path);
    await waitingTab.getByRole('button', {name: 'Periksa hasil', exact: true}).waitFor();
    assert.equal(await waitingTab.getByRole('button', {name: 'Muat pesanan terbaru', exact: true}).isDisabled(), true);
    await restarted.page.getByRole('button', {name: 'Periksa hasil', exact: true}).click();
    const saved = restarted.page.getByRole('region', {name: 'Draf baru tersimpan', exact: true}); await saved.waitFor();
    assert.equal(recoveries.length, 1); assert.equal(recoveries[0].status, 200); assert.equal(recoveries[0].actor, config.buyerId);
    assert.deepEqual(recoveries[0].input, {purpose: 'operation-recovery', key: write.key});
    assert.equal(recoveries[0].result.status, 'committed'); assert.deepEqual(recoveries[0].result.result, write.result);
    assert.deepEqual(await pending(restarted.page), []); await waitingTab.getByRole('button', {name: 'Periksa hasil', exact: true}).waitFor({state: 'hidden'});
    await waitingTab.getByRole('button', {name: 'Muat pesanan terbaru', exact: true}).click({trial: true});
    assert.equal(await repeat(waitingTab).count(), 0); assert.equal(await review(waitingTab).count(), 0);
    const draftId = write.result.draftId; assert.match(draftId, /^[1-9][0-9]*$/);
    const checkout = saved.getByRole('link', {name: 'Tinjau pengajuan draf', exact: true});
    assert.equal(await checkout.getAttribute('href'), '/ordering/checkout?draft=' + draftId);
    await snapshot(restarted.page, 'recovered');
    await checkout.click(); await restarted.page.getByRole('heading', {name: 'Pengajuan pesanan', exact: true}).waitFor();
    assert.equal(new URL(restarted.page.url()).searchParams.get('draft'), draftId);
    // Finite stock remains reserved by the source order; this proof never submits another order.
    await restarted.page.goto(origin.origin + '/ordering/drafts');
    const contextKey = [expected.context.organizationId, expected.context.branchId, expected.context.divisionId ?? ''].join(':');
    const branch = restarted.page.getByLabel('Cabang pemesan', {exact: true});
    await branch.locator('option[value="' + contextKey + '"]').waitFor({state: 'attached'}); await branch.selectOption(contextKey);
    await restarted.page.getByRole('region', {name: 'Daftar draf', exact: true}).getByRole('button').filter({hasText: 'Draf ' + draftId}).click();
    await restarted.page.getByLabel('Jumlah kemasan 1', {exact: true}).waitFor(); await fields(restarted.page);
    await restarted.page.reload(); await branch.locator('option[value="' + contextKey + '"]').waitFor({state: 'attached'}); await branch.selectOption(contextKey);
    await restarted.page.getByRole('region', {name: 'Daftar draf', exact: true}).getByRole('button').filter({hasText: 'Draf ' + draftId}).click();
    await restarted.page.getByLabel('Jumlah kemasan 1', {exact: true}).waitFor(); await fields(restarted.page);

    const wrongBranch = await query(restarted.page, config.buyerId, api + '/quote/query', {purpose: 'order-preparation',
      context: {...expected.context, branchId: config.deniedBranchId}, selections: expected.selections});
    assert.equal(wrongBranch.status, 403); assert.equal(Object.hasOwn(wrongBranch.body, 'lines'), false);
    const outsider = await user(config.deniedToken); observe(outsider.page); await outsider.page.goto(origin.origin + path);
    await outsider.page.getByRole('heading', {name: 'Periksa sesi dan akses', exact: true}).waitFor();
    assert.equal(await outsider.page.getByRole('button', {name: 'Pesan ulang sebagai draf', exact: true}).count(), 0);
    const denied = await query(outsider.page, config.deniedId, bulkApi, {codes: [config.skuCode]});
    assert.equal(denied.status, 403); assert.equal(Array.isArray(denied.body), false); assert.equal(Object.hasOwn(denied.body, 'skuId'), false);
    assert.equal(writes.length, 1); assert.equal(recoveries.length, 1); assert.deepEqual(unexpectedWrites, []); assert.deepEqual(errors, []);
    console.log('REAL_ORDER_BULK_BROWSER_PASS ' + JSON.stringify({writes: 1, originalReplyLosses: 1, exactBulkSelectionPreserved: true, bulkPackages: config.bulkPackages, malformedAndDuplicateWithoutRequest: true,
      missingInactiveAndOtherAssortmentDenied: true, explicitBulkPreviewAndApply: true,
      cancellationWithoutWrite: true, explicitCurrentQuote: true, browserRestartOriginalKeyRecovery: true, recoveryWithoutReselectingOrder: true,
      staleSecondTabPreviewCleared: true, secondTabLockedThenUnlocked: true, persistedDraftRefresh: true, checkoutLink: true,
      deniedActor: true, deniedBranch: true, mobile: true, desktop: true, focus: true}));
  } catch (failure) {
    let index = 0;
    for (const page of new Set([buyer.page, ...pages])) {
      if (page.isClosed()) continue;
      try {
        const prefix = resolve(directory, config.proofPrefix + '-failure-' + index++);
        await writeFile(prefix + '.aria.txt', await page.locator('body').ariaSnapshot());
        await page.screenshot({path: prefix + '.png', fullPage: true});
      } catch (diagnosticFailure) {console.error('Bulk failure capture unavailable: ' + String(diagnosticFailure));}
    }
    throw failure;
  } finally {for (const page of pages) if (!page.isClosed()) await page.close();}
}
