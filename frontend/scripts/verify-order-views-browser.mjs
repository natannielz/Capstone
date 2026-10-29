import assert from 'node:assert/strict';
import {resolve} from 'node:path';
// Order numbers as the screens show them (src/lib/document-numbers.ts).
const orderNumber = id => 'PSN-' + String(id).padStart(6, '0');

// Genuine Next/API/Mongo owners. Only committed HTTP replies are deliberately lost.
export async function verifyOrderViewsBrowser({config, buyer, user, origin, directory, restartUser}) {
  const writes = [], recoveries = [], errors = [], unexpected = [], writeObservers = new Set();
  const path = '/ordering/orders', api = '/api/v1/ordering/order-views', contextKey = [config.expectedInput.context.organizationId, config.expectedInput.context.branchId, config.expectedInput.context.divisionId ?? ''].join(':');
  const review = page => page.getByRole('region', {name: 'Konfirmasi perubahan filter', exact: true});
  const pending = page => page.evaluate(actor => Object.keys(localStorage).filter(key => key.startsWith('unit-toko.order-view.pending.' + actor + '.')).map(key => JSON.parse(localStorage.getItem(key))), config.buyerId);
  const observe = page => {page.setDefaultTimeout(30000); page.setDefaultNavigationTimeout(30000); page.on('pageerror', error => errors.push(error.message));};
  async function intercept(context) {
    await context.route('**/api/v1/**', async route => {
      const request = route.request(), endpoint = new URL(request.url()).pathname;
      const write = endpoint === api && request.method() === 'POST', recovery = endpoint === api + '/operations/query';
      if (request.headers()['idempotency-key'] && !write) unexpected.push(endpoint);
      if (!write && !recovery) return route.continue();
      try {
        const response = await route.fetch({timeout: 30000}); const record = {actor: request.headers()['x-expected-actor-id'], key: request.headers()['idempotency-key'],
          revision: request.headers()['if-match'], body: request.postDataJSON(), status: response.status(), result: await response.json()};
        (write ? writes : recoveries).push(record); if (write) for (const notify of writeObservers) notify();
        if (write && response.status() === 200) await route.abort('failed'); else await route.fulfill({response});
      } catch (error) {errors.push(String(error)); await route.abort('failed');}
    });
  }
  async function committedWrite(count, submit) {
    let timer; let notify;
    const completed = new Promise((resolve, reject) => {
      notify = () => {if (writes.length >= count) resolve();}; writeObservers.add(notify);
      timer = setTimeout(() => reject(new Error('Actual original write did not complete within 30000ms')), 30000);
    });
    try {await Promise.all([completed, submit()]);} finally {clearTimeout(timer); writeObservers.delete(notify);}
  }
  // The filters sit in a collapsed "Filter dan urutan" disclosure on the order page, so a person opens it first; closed content is not in the accessibility tree.
  async function openFilters(page) {
    const summary = page.locator('details > summary', {hasText: 'Filter dan urutan'}); await summary.waitFor();
    if (!(await summary.evaluate(element => element.parentElement.open))) await summary.click();
  }
  async function ready(page) {
    await page.getByRole('heading', {name: 'Pesanan saya', exact: true}).waitFor(); await openFilters(page);
    await page.getByRole('combobox', {name: 'Cabang atau divisi pesanan', exact: true}).locator('option[value="' + contextKey + '"]').waitFor({state: 'attached'});
    await page.getByRole('button', {name: 'Terapkan filter', exact: true}).click({trial: true});
  }
  async function shot(page, suffix) {
    for (const width of [1440, 360]) {
      await page.setViewportSize({width, height: 1000});
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true);
      await page.screenshot({path: resolve(directory, config.proofPrefix + '-' + suffix + '-' + width + '.png'), fullPage: true});
    }
    await page.setViewportSize({width: 1440, height: 1000});
  }
  const query = (page, actor, endpoint, body) => page.evaluate(async ({actor, endpoint, body}) => {
    const csrfResponse = await fetch('/api/v1/identity/csrf', {credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: AbortSignal.timeout(30000)});
    if (!csrfResponse.ok) throw new Error('Actual CSRF unavailable');
    const csrf = await csrfResponse.json(), response = await fetch(endpoint, {method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: AbortSignal.timeout(30000),
      headers: {'Content-Type': 'application/json', 'X-CSRF-Token': csrf.requestToken, 'X-Expected-Actor-Id': actor}, body: JSON.stringify(body)});
    return {status: response.status, body: await response.json()};
  }, {actor, endpoint, body});
  let current = buyer; observe(current.page); await intercept(current.context); await current.page.goto(origin.origin + path); await ready(current.page);
  await current.page.getByRole('button', {name: 'Lihat rincian pesanan ' + orderNumber(config.orderId), exact: true}).waitFor();
  await current.page.getByRole('combobox', {name: 'Cabang atau divisi pesanan', exact: true}).selectOption(contextKey);
  await current.page.getByRole('combobox', {name: 'Urutan pesanan', exact: true}).selectOption('oldest');
  await current.page.getByRole('button', {name: 'Terapkan filter', exact: true}).click();
  await current.page.getByRole('button', {name: 'Muat pesanan pertama', exact: true}).click({trial: true});
  await current.page.getByRole('textbox', {name: 'Nama filter pesanan', exact: true}).fill('Cabang tertua');
  await current.page.getByRole('button', {name: 'Tinjau simpan filter baru', exact: true}).click();
  await review(current.page).waitFor(); await current.page.getByRole('button', {name: 'Batalkan perubahan filter', exact: true}).click();
  assert.equal(writes.length, 0);
  for (const action of ['create', 'rename', 'remove']) {
    if (action !== 'create') {
      await current.page.getByRole('button', {name: 'Muat filter ' + (action === 'rename' ? 'Cabang tertua' : 'Cabang tertua baru'), exact: true}).click();
      await current.page.getByRole('button', {name: 'Tinjau nama filter', exact: true}).click({trial: true});
    }
    if (action === 'rename') await current.page.getByRole('textbox', {name: 'Nama filter pesanan', exact: true}).fill('Cabang tertua baru');
    const second = await current.context.newPage(); observe(second); await second.goto(origin.origin + path); await ready(second);
    await second.getByRole('textbox', {name: 'Nama filter pesanan', exact: true}).fill('Pratinjau tab lama');
    await second.getByRole('button', {name: 'Tinjau simpan filter baru', exact: true}).click(); await review(second).waitFor();
    const button = action === 'create' ? 'Tinjau simpan filter baru' : action === 'rename' ? 'Tinjau nama filter' : 'Tinjau hapus filter';
    await current.page.getByRole('button', {name: button, exact: true}).click(); await review(current.page).waitFor();
    await current.page.waitForFunction(() => document.activeElement?.getAttribute('aria-label') === 'Konfirmasi perubahan filter');
    await shot(current.page, action + '-review');
    await current.page.getByRole('checkbox', {name: 'Saya sudah memeriksa perubahan filter ini.', exact: true}).check();
    await committedWrite(['create', 'rename', 'remove'].indexOf(action) + 1, () => current.page.getByRole('button', {name: 'Konfirmasi perubahan filter', exact: true}).click());
    await current.page.getByRole('button', {name: 'Periksa hasil filter', exact: true}).waitFor();
    await review(second).waitFor({state: 'hidden'});
    const reference = (await pending(current.page))[0], write = writes.at(-1);
    assert.equal(writes.length, ['create', 'rename', 'remove'].indexOf(action) + 1); assert.equal(write.status, 200); assert.equal(write.body.action, action);
    assert.equal(write.actor, config.buyerId); assert.equal(reference.key, write.key); assert.equal(reference.action, action);
    assert.deepEqual(Object.keys(reference).sort(), ['action', 'actorId', 'createdAt', 'key', 'revision', 'viewId']);
    assert.deepEqual(await pending(second), [reference]);
    const old = current; current = await restartUser(config.financeToken, await current.context.storageState());
    assert.equal(old.page.isClosed(), true); assert.equal(second.isClosed(), true); observe(current.page); await intercept(current.context);
    await current.page.goto(origin.origin + path); assert.equal(await current.page.evaluate(() => sessionStorage.length), 0); await openFilters(current.page);
    await current.page.getByRole('button', {name: 'Periksa hasil filter', exact: true}).waitFor(); assert.deepEqual(await pending(current.page), [reference]);
    const waiting = await current.context.newPage(); observe(waiting); await waiting.goto(origin.origin + path); await openFilters(waiting);
    await waiting.getByRole('button', {name: 'Periksa hasil filter', exact: true}).waitFor();
    assert.equal(await waiting.getByRole('button', {name: 'Terapkan filter', exact: true}).isDisabled(), true);
    await current.page.getByRole('button', {name: 'Periksa hasil filter', exact: true}).click();
    await current.page.getByRole('button', {name: 'Periksa hasil filter', exact: true}).waitFor({state: 'hidden'}); await ready(current.page);
    const receipt = recoveries.at(-1); assert.equal(receipt.status, 200); assert.deepEqual(receipt.body, {purpose: 'operation-recovery', key: reference.key});
    assert.deepEqual(receipt.result.result, write.result); assert.deepEqual(await pending(current.page), []);
    await waiting.getByRole('button', {name: 'Periksa hasil filter', exact: true}).waitFor({state: 'hidden'}); await ready(waiting); assert.equal(await review(waiting).count(), 0); await waiting.close();
    if (action !== 'remove') {
      await current.page.getByRole('button', {name: 'Muat filter ' + (action === 'create' ? 'Cabang tertua' : 'Cabang tertua baru'), exact: true}).waitFor();
      if (action === 'create') {
        await current.page.getByRole('button', {name: 'Muat filter Cabang tertua', exact: true}).click();
        await current.page.getByRole('button', {name: 'Tinjau nama filter', exact: true}).click({trial: true});
        const link = await current.page.getByRole('textbox', {name: 'Tautan filter pesanan', exact: true}).inputValue();
        assert.equal(new URL(link).searchParams.has('actor'), false); assert.equal(new URL(link).searchParams.has('viewId'), false);
        const recipient = await user(config.recipientToken); observe(recipient.page);
        const recipientOrders = recipient.page.waitForResponse(response => new URL(response.url()).pathname === api + '/orders/query' && response.request().method() === 'POST');
        await recipient.page.goto(link); const ownOrders = await recipientOrders;
        assert.equal(ownOrders.status(), 200); const ownPage = await ownOrders.json();
        assert.deepEqual(ownPage.items, []); assert.equal(ownPage.hasMore, false); assert.equal(ownPage.nextCursor, null);
        await ready(recipient.page);
        await recipient.page.getByText('Belum ada pesanan pada halaman ini.', {exact: true}).waitFor();
        assert.equal(await recipient.page.getByRole('button', {name: 'Lihat rincian pesanan ' + orderNumber(config.orderId), exact: true}).count(), 0);
        const deniedView = await query(recipient.page, config.recipientId, api + '/' + write.result.viewId + '/query', {purpose: 'order-history'});
        assert.equal(deniedView.status, 403); assert.equal(Object.hasOwn(deniedView.body, 'filter'), false); await recipient.context.close();
      }
    } else await current.page.getByText('Belum ada filter tersimpan yang tersedia pada halaman ini.', {exact: true}).waitFor();
  }
  await shot(current.page, 'recovered'); await current.page.reload(); await ready(current.page);
  assert.equal(writes.length, 3); assert.equal(recoveries.length, 3);
  const bad = await query(current.page, config.buyerId, api + '/orders/query', {purpose: 'order-history', filter: {context: {...config.expectedInput.context, branchId: config.deniedBranchId}, sort: 'newest'}, cursor: null});
  assert.equal(bad.status, 403); assert.equal(Object.hasOwn(bad.body, 'items'), false);
  const denied = await user(config.deniedToken); observe(denied.page); await denied.page.goto(origin.origin + path);
  await denied.page.getByRole('heading', { name: /^(Masuk untuk melanjutkan|Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor();
  const deniedList = await query(denied.page, config.deniedId, api + '/query', {purpose: 'order-history', beforeId: null}); assert.equal(deniedList.status, 403);
  assert.deepEqual(unexpected, []); assert.deepEqual(errors, []);
  console.log('REAL_ORDER_VIEWS_BROWSER_PASS ' + JSON.stringify({writes: 3, originalReplyLosses: 3, actualBrowserRestarts: 3,
    originalKeyRecovery: true, sameAccountStalePreviewCleared: true, sharedFilterRecipientSeesOwnOrders: true,
    cancellationWithoutWrite: true, deniedActor: true, deniedBranch: true, desktop: true, mobile: true, focus: true}));
}
