// Genuine approved purchase order, scoped sessions, Next/Kestrel and isolated Mongo.
import assert from 'node:assert/strict';
import {resolve} from 'node:path';

export async function verifyPurchaseArrivalsBrowser({config, maker, user, restartUser, origin, directory}) {
  const path = '/procurement/purchase-orders', api = '/api/v1/procurement/arrivals';
  const canonical = [config.organizationId, config.shopId, config.warehouseId, config.channelId].join(':');
  const reference = 'SYNTHETIC-SUPPLIER-CONFIRMATION-0001', writes = [], recoveries = [], errors = [], newPages = [];
  const consent = 'Saya sudah memeriksa barang, waktu dan konfirmasi pemasok.';
  const panel = current => current.getByRole('region', {name: 'Jadwal kedatangan pemasok', exact: true});
  const pending = (current, actorId) => current.evaluate(actor => {
    const prefix = 'unit-toko.purchase-arrival.pending.' + actor + '.';
    const values = Object.keys(localStorage).filter(key => key.startsWith(prefix)).map(key => JSON.parse(localStorage.getItem(key)));
    if (values.length > 1) throw new Error('Unexpected duplicate arrival recovery references'); return values[0] ?? null;
  }, actorId);
  const query = (current, actorId, route, body) => current.evaluate(async ({actorId, route, body}) => {
    const csrf = await fetch('/api/v1/identity/csrf', {credentials: 'same-origin', cache: 'no-store', redirect: 'error'});
    if (!csrf.ok) throw new Error('Actual request token unavailable'); const token = await csrf.json();
    const response = await fetch(route, {method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error',
      headers: {'Content-Type': 'application/json', 'X-CSRF-Token': token.requestToken, 'X-Expected-Actor-Id': actorId}, body: JSON.stringify(body)});
    return {status: response.status, body: await response.json()};
  }, {actorId, route, body});
  async function choose(current) {
    current.setDefaultTimeout(30000);
    const location = current.getByLabel('Organisasi / toko / gudang / kanal', {exact: true});
    await location.locator('option[value="' + canonical + '"]').waitFor({state: 'attached'});
    await location.selectOption(canonical);
    await current.getByRole('button', {name: new RegExp('Usulan ' + config.orderProposalId + '$')}).click();
    const section = panel(current), sku = section.getByLabel('Barang dan kemasan untuk jadwal');
    await sku.locator('option[value="' + config.skuId + '"]').waitFor({state: 'attached'});
    assert.equal(await sku.inputValue(), ''); await sku.selectOption(config.skuId);
    await section.getByRole('button', {name: 'Muat jadwal kedatangan', exact: true}).click(); return section;
  }
  async function intercept(context) {
    await context.route('**/api/v1/procurement/arrivals/**', async route => {
      const request = route.request(), routePath = new URL(request.url()).pathname;
      const command = routePath === api + '/proposals' || /^\/api\/v1\/procurement\/arrivals\/proposals\/[^/]+\/decisions$/.test(routePath);
      const recovery = routePath === api + '/operations/query';
      if (!command && !recovery) return route.continue();
      try {
        const response = await route.fetch({timeout: 30000});
        const record = {path: routePath, actor: request.headers()['x-expected-actor-id'], key: request.headers()['idempotency-key'],
          version: request.headers()['if-match'], input: request.postDataJSON(), status: response.status(), result: await response.json()};
        (command ? writes : recoveries).push(record);
        // Commit the real request first; only its reply is lost. Recovery must never resubmit it.
        if (command && response.status() === 200) await route.abort('failed'); else await route.fulfill({response});
      } catch (failure) {errors.push(String(failure)); await route.abort('failed');}
    });
  }
  async function confirm(current) {
    const review = current.getByRole('region', {name: 'Konfirmasi jadwal kedatangan', exact: true});
    await review.getByRole('heading', {name: 'Periksa jadwal dan sumber konfirmasi', exact: true}).waitFor();
    assert.ok((await review.innerText()).includes(reference));
    const save = review.getByRole('button', {name: 'Simpan jadwal yang ditinjau', exact: true});
    assert.equal(await save.isDisabled(), true); await review.getByRole('checkbox', {name: consent, exact: true}).check();
    await save.click(); await panel(current).getByText('Hasil belum dapat dipastikan. Jika ada referensi tersimpan, pulihkan hasil sebelum melakukan perubahan lain.', {exact: true}).waitFor();
    await panel(current).getByRole('button', {name: 'Pulihkan hasil jadwal', exact: true}).waitFor();
  }
  async function recoverInNewPage(actor, actorId, action, index) {
    const saved = await pending(actor.page, actorId), write = writes[index];
    assert.equal(write.status, 200); assert.equal(write.actor, actorId); assert.ok(write.key);
    assert.deepEqual(saved, {actorId, orderId: config.orderId, skuId: config.skuId, key: write.key, action});
    assert.ok(!JSON.stringify(saved).includes(reference)); assert.ok(!JSON.stringify(saved).includes(write.input.reason));
    const restarted = index === 1 ? await restartUser(config.checkerToken, await actor.context.storageState()) : null;
    if (restarted) await intercept(restarted.context);
    const current = restarted ? restarted.page : await actor.context.newPage(); newPages.push(current); current.setDefaultTimeout(30000);
    current.on('pageerror', error => errors.push(error.message)); await current.goto(origin.origin + path);
    assert.deepEqual(await pending(current, actorId), saved);
    await panel(current).getByRole('button', {name: 'Pulihkan hasil jadwal', exact: true}).click();
    await panel(current).getByText('Hasil asli sudah ditemukan untuk pesanan ' + config.orderId + '.', {exact: true}).waitFor();
    assert.equal(await pending(current, actorId), null);
    if (!restarted) {
      await panel(actor.page).getByRole('button', {name: 'Pulihkan hasil jadwal', exact: true}).waitFor({state: 'hidden'});
      await panel(actor.page).getByRole('region', {name: 'Konfirmasi jadwal kedatangan', exact: true}).waitFor({state: 'hidden'});
      const location = actor.page.getByLabel('Organisasi / toko / gudang / kanal', {exact: true});
      await location.click({trial: true}); assert.equal(await location.isEnabled(), true);
      assert.equal(await panel(actor.page).getByRole('button', {name: 'Simpan jadwal yang ditinjau', exact: true}).count(), 0);
      assert.equal(await panel(actor.page).getByLabel('Alasan pencatatan atau keputusan', {exact: true}).count(), 0);
    }
    assert.equal(writes.length, index + 1); assert.equal(recoveries.length, index + 1);
    assert.equal(recoveries[index].status, 200); assert.equal(recoveries[index].actor, actorId);
    assert.deepEqual(recoveries[index].input, {purpose: 'operation-recovery', action, key: write.key});
    assert.equal(recoveries[index].result.status, 'confirmed'); return current;
  }
  try {
    assert.notEqual(config.makerId, config.checkerId); await intercept(maker.context);
    await maker.page.goto(origin.origin + path); let section = await choose(maker.page);
    await section.getByRole('heading', {name: 'Belum dicatat', exact: true}).waitFor();
    await section.getByText('Waktu yang disetujui: Belum ada waktu kedatangan yang dapat digunakan.', {exact: true}).waitFor();
    assert.equal(await section.getByLabel('Perkiraan kedatangan (WIB)', {exact: true}).inputValue(), '');
    assert.equal(await section.getByLabel('Referensi konfirmasi pemasok', {exact: true}).inputValue(), '');
    const unknown = section.getByRole('checkbox', {name: 'Pemasok belum dapat memastikan waktu; tarik jadwal sebelumnya.', exact: true});
    assert.equal(await unknown.isChecked(), false); await unknown.check();
    assert.equal(await section.getByLabel('Perkiraan kedatangan (WIB)', {exact: true}).count(), 0); await unknown.uncheck();
    assert.equal(await section.getByLabel('Perkiraan kedatangan (WIB)', {exact: true}).inputValue(), '');
    await section.getByLabel('Perkiraan kedatangan (WIB)', {exact: true}).fill(config.arrivalWib);
    await section.getByLabel('Referensi konfirmasi pemasok', {exact: true}).fill(reference);
    await section.getByLabel('Alasan pencatatan atau keputusan', {exact: true}).fill('Konfirmasi pemasok sintetis untuk kedatangan kemasan yang disetujui');
    await section.getByRole('button', {name: 'Periksa usulan jadwal', exact: true}).click(); await confirm(maker.page);
    assert.equal(writes[0].path, api + '/proposals');
    assert.equal(new Date(writes[0].input.input.expectedAt).toISOString(), new Date(config.expectedArrival).toISOString());
    const recoveredMaker = await recoverInNewPage(maker, config.makerId, 'propose', 0); section = await choose(recoveredMaker);
    await section.getByRole('heading', {name: 'Menunggu pemeriksaan', exact: true}).waitFor();
    await section.getByText('Waktu yang disetujui: Belum ada waktu kedatangan yang dapat digunakan.', {exact: true}).waitFor();
    assert.equal(await section.getByRole('button', {name: 'Tinjau persetujuan jadwal', exact: true}).count(), 0);
    const checker = await user(config.checkerToken); await intercept(checker.context); await checker.page.goto(origin.origin + path); section = await choose(checker.page);
    await section.getByRole('heading', {name: 'Menunggu pemeriksaan', exact: true}).waitFor();
    await section.getByLabel('Alasan pencatatan atau keputusan', {exact: true}).fill('Pemeriksa independen meninjau konfirmasi pemasok dan waktu kedatangan sintetis');
    await section.getByRole('button', {name: 'Tinjau persetujuan jadwal', exact: true}).click(); await confirm(checker.page);
    assert.match(writes[1].path, /\/proposals\/[^/]+\/decisions$/); assert.equal(writes[1].input.decision, 'approve'); assert.equal(writes[1].version, '"2"');
    const approved = await recoverInNewPage(checker, config.checkerId, 'approve', 1); section = await choose(approved);
    await section.getByRole('heading', {name: 'Disetujui', exact: true}).waitFor();
    assert.ok((await section.innerText()).includes(reference));
    const expectedText = new Intl.DateTimeFormat('id-ID', {dateStyle: 'medium', timeStyle: 'short', timeZone: 'Asia/Jakarta'}).format(new Date(config.expectedArrival)) + ' WIB';
    await section.getByText('Waktu yang disetujui: ' + expectedText, {exact: true}).waitFor();
    assert.equal(await approved.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await approved.screenshot({path: resolve(directory, 'purchase-arrival-approved-mobile.png'), fullPage: true});
    await approved.setViewportSize({width: 1440, height: 1000}); assert.equal(await approved.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await approved.screenshot({path: resolve(directory, 'purchase-arrival-approved-desktop.png'), fullPage: true});
    await approved.reload(); section = await choose(approved); await section.getByRole('heading', {name: 'Disetujui', exact: true}).waitFor();
    await section.getByText('Waktu yang disetujui: ' + expectedText, {exact: true}).waitFor();
    const reader = await user(config.readerToken); await reader.page.goto(origin.origin + path); section = await choose(reader.page);
    await section.getByRole('heading', {name: 'Disetujui', exact: true}).waitFor();
    for (const name of ['Periksa usulan jadwal', 'Tinjau persetujuan jadwal', 'Tinjau penolakan jadwal', 'Simpan jadwal yang ditinjau'])
      assert.equal(await section.getByRole('button', {name, exact: true}).count(), 0);
    const outsider = await user(config.outsiderToken); await outsider.page.goto(origin.origin + '/__purchase_arrival_scope_probe');
    const forbidden = await query(outsider.page, config.outsiderId, api + '/' + config.orderId + '/' + config.skuId + '/query', {purpose: 'purchase-arrival-review'});
    assert.equal(forbidden.status, 403); assert.equal(forbidden.body.code, 'scope_denied');
    for (const field of ['orderId', 'skuId', 'expectedAt', 'confirmationReference', 'reason']) assert.equal(Object.hasOwn(forbidden.body, field), false);
    assert.equal(writes.length, 2); assert.equal(recoveries.length, 2); assert.deepEqual(errors, []);
    console.log('REAL_PURCHASE_ARRIVAL_BROWSER_PASS ' + JSON.stringify({writes: 2, originalReplyLosses: 2, actualApprovedOrder: true,
      independentReview: true, newPageOriginalKeyRecovery: true, browserRestartOriginalKeyRecovery: true, crossTabRecovery: true, staleTabPreviewInvalidated: true,
      oldTabLocationUnlocked: true, explicitWib: true, missingScheduleIsUnknown: true, reader: true, scope: true, mobile: true, desktop: true}));
  } finally {for (const current of newPages) if (!current.isClosed()) await current.close();}
}
