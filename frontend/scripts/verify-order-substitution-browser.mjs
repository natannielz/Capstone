// Genuine protected Next/API/Mongo. Fault injection loses a response only after the actual owner committed.
import assert from 'node:assert/strict';
import { writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { createHash } from 'node:crypto';
// Order numbers as the screens show them (src/lib/document-numbers.ts).
const orderNumber = id => 'PSN-' + String(id).padStart(6, '0');

export async function verifyOrderSubstitutionBrowser({ config, maker, user, restartUser, origin, directory }) {
  const path = '/ordering/substitutions', api = '/api/v1/ordering/substitutions', writes = [], recoveries = [], captures = [], errors = [], timings = [];
  let staff = maker, buyer; const reason = 'Explicitly reviewed replacement package, quantity and price';
  function observe(person) {
    person.page.setDefaultTimeout(30000); person.page.on('pageerror', error => errors.push(error.message));
    person.page.on('request', request => {
      const route = new URL(request.url()).pathname;
      if (request.method() !== 'POST' || !route.startsWith(api)) return;
      const value = { path: route, actor: request.headers()['x-expected-actor-id'], key: request.headers()['idempotency-key'], revision: request.headers()['if-match'], body: request.postDataJSON() };
      if (route === api || route.endsWith('/decisions')) writes.push(value);
      else if (route.endsWith('/operations/query')) recoveries.push(value);
    }); return person;
  }
  const state = async () => {
    const response = await fetch(config.apiAddress + '/__fixture/substitution-state', { method: 'POST', headers: { 'X-Fixture-Control': config.controlKey } });
    assert.equal(response.status, 200); return response.json();
  };
  async function capture(page, name) {
    const original = page.viewportSize();
    for (const viewport of [{ width: 1280, height: 960 }, { width: 360, height: 800 }]) {
      await page.setViewportSize(viewport); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, name + ' overflow');
      const file = resolve(directory, config.proofPrefix + '-' + name + '-' + viewport.width + '.png');
      const bytes = await page.screenshot({ path: file, fullPage: true }); captures.push({ name, width: viewport.width, file, sha256: createHash('sha256').update(bytes).digest('hex') });
    } await page.setViewportSize(original);
  }
  async function ready(page, label) {
    await page.waitForFunction(label => [...document.querySelectorAll('button')].some(button => button.textContent?.trim() === label && !button.disabled), label, { timeout: 30000 });
    return page.getByRole('button', { name: label, exact: true });
  }
  async function chooseProposal(person) {
    await person.page.goto(origin.origin + path);
    await (await ready(person.page, 'Pilih pesanan ' + config.orderId)).click();
    await person.page.getByLabel('Barang asal', { exact: true }).selectOption(config.originalSkuId + ':0');
    await person.page.getByLabel('Jumlah kemasan asal yang diganti', { exact: true }).fill('4');
    await person.page.getByLabel('Cari nama barang pengganti', { exact: true }).fill(config.replacementCode);
    await person.page.getByRole('button', { name: 'Cari kemasan pengganti', exact: true }).click();
    await (await ready(person.page, 'Pilih ' + config.replacementLabel)).click();
    await person.page.getByLabel('Jumlah kemasan pengganti', { exact: true }).fill('1');
    await person.page.getByRole('button', { name: 'Tinjau pengajuan penggantian', exact: true }).click();
    await person.page.getByRole('heading', { name: 'Konfirmasi pengajuan penggantian', exact: true }).waitFor();
    const panel = person.page.getByRole('region', { name: 'Konfirmasi pengajuan penggantian', exact: true });
    await panel.getByLabel('Alasan', { exact: true }).fill(reason);
    assert.equal(await panel.getByRole('checkbox').isChecked(), false); await panel.getByRole('checkbox').check();
    await ready(person.page, 'Kirim pengajuan ke pembeli'); return panel;
  }
  async function lostWrite(person, routePath, buttonLabel, operation, token) {
    let committed; const before = writes.length;
    await person.page.route(origin.origin + routePath, async route => {
      const response = await route.fetch({ timeout: 30000 }); assert.equal(response.status(), 200); committed = await response.json(); await route.abort('failed');
    }, { times: 1 });
    const button = await ready(person.page, buttonLabel), started = performance.now(); await button.click();
    await ready(person.page, 'Periksa hasil');
    const elapsedMs = performance.now() - started; assert.ok(elapsedMs < 30000, 'Commit and recovery readiness retain the original 30 second action bound'); timings.push({ operation, elapsedMs });
    assert.ok(committed?.proposalId); assert.equal(writes.length, before + 1); const write = writes.at(-1);
    const pending = await person.page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => ({ key, value: JSON.parse(localStorage[key]) })));
    assert.equal(pending.length, 1); assert.deepEqual(Object.keys(pending[0].value).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.equal(pending[0].value.operation, operation); assert.equal(pending[0].value.key, write.key); assert.equal(pending[0].value.actorId, write.actor);
    assert.equal(JSON.stringify(pending).includes(reason), false); await capture(person.page, operation.endsWith('propose') ? 'proposal-uncertain' : 'consent-uncertain');
    const other = await person.context.newPage(); other.setDefaultTimeout(30000); await other.goto(origin.origin + path);
    await other.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
    assert.equal(await other.getByRole('button', { name: 'Muat ulang penggantian', exact: true }).isDisabled(), true);
    const storage = await person.context.storageState(); person = observe(await restartUser(token, storage));
    await person.page.goto(origin.origin + path); await (await ready(person.page, 'Periksa hasil')).click();
    await ready(person.page, 'Muat ulang penggantian'); assert.equal(await person.page.evaluate(key => localStorage.getItem(key), pending[0].key), null);
    assert.equal(writes.filter(value => value.key === write.key).length, 1);
    assert.equal(recoveries.filter(value => value.body.key === write.key).length, 1);
    await capture(person.page, operation.endsWith('propose') ? 'proposal-recovered' : 'consent-recovered'); return { person, committed };
  }
  const query = async (person, actor, endpoint, body) => person.page.evaluate(async ({ actor, endpoint, body }) => {
    const csrf = await fetch('/api/v1/identity/csrf', { cache: 'no-store', credentials: 'same-origin' }).then(response => response.json());
    const response = await fetch(endpoint, { method: 'POST', cache: 'no-store', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf.requestToken, 'X-Expected-Actor-Id': actor }, body: JSON.stringify(body) });
    return { status: response.status, body: await response.json() };
  }, { actor, endpoint, body });
  observe(staff); const before = await state(); assert.deepEqual(before, config.baseline); assert.equal(before.shipments, 1);
  await chooseProposal(staff); await capture(staff.page, 'staff-proposal-review');
  const proposed = await lostWrite(staff, api, 'Kirim pengajuan ke pembeli', 'ordering.substitution.propose', config.makerToken); staff = proposed.person;
  const afterProposed = await state(); assert.equal(afterProposed.physicalHash, before.physicalHash); assert.equal(afterProposed.financialHash, before.financialHash); assert.equal(afterProposed.movements, 0);
  const unauthorized = await query(staff, config.actorIds.staff, api + '/' + proposed.committed.proposalId + '/preview/query', { purpose: 'order-substitution', decision: 'Accept' });
  assert.equal(unauthorized.status, 403); assert.equal(Object.hasOwn(unauthorized.body, 'lines'), false);
  buyer = observe(await user(config.buyerToken)); await buyer.page.goto(origin.origin + path + '?order=' + config.orderId);
  await (await ready(buyer.page, 'Tinjau penolakan')).click(); await buyer.page.getByRole('heading', { name: 'Konfirmasi penolakan pembeli', exact: true }).waitFor();
  let panel = buyer.page.getByRole('region', { name: 'Konfirmasi penolakan pembeli', exact: true });
  await panel.getByLabel('Alasan', { exact: true }).fill('Pembeli memilih mempertahankan barang asal'); await panel.getByRole('checkbox').check();
  await panel.getByRole('button', { name: 'Tolak penggantian barang', exact: true }).click();
  await buyer.page.getByText('Penggantian ditolak. Pesanan dan cadangan barang tetap seperti sebelumnya.', { exact: true }).waitFor();
  const afterDecline = await state(); assert.equal(afterDecline.physicalHash, before.physicalHash); assert.equal(afterDecline.financialHash, before.financialHash); assert.equal(afterDecline.movements, 0); assert.equal(afterDecline.decisions, 1);
  await capture(buyer.page, 'buyer-declined');
  await chooseProposal(staff); await staff.page.getByRole('button', { name: 'Kirim pengajuan ke pembeli', exact: true }).click();
  await staff.page.getByText('Pengajuan tersimpan. Pembeli perlu meninjau dan memutuskan penggantian ini.', { exact: true }).waitFor();
  await buyer.page.reload(); await (await ready(buyer.page, 'Tinjau persetujuan')).click();
  await buyer.page.getByRole('heading', { name: 'Konfirmasi persetujuan pembeli', exact: true }).waitFor(); panel = buyer.page.getByRole('region', { name: 'Konfirmasi persetujuan pembeli', exact: true });
  assert.equal(await panel.getByRole('checkbox').isChecked(), false); assert.equal(await panel.getByRole('button', { name: 'Setujui penggantian barang', exact: true }).isDisabled(), true);
  await panel.getByLabel('Alasan', { exact: true }).fill(reason); await panel.getByRole('checkbox').check(); await capture(buyer.page, 'buyer-exact-consent');
  const second = writes.filter(value => value.path === api).at(-1); assert.equal(second.body.input.originalPackages, 4); assert.equal(second.body.input.replacement.packages, 1); assert.equal(second.body.input.replacement.skuId, config.replacementSkuId);
  const pendingHistory = await query(buyer, config.actorIds.buyer, api + '/query', { purpose: 'order-substitution', forBuyer: true, orderId: config.orderId, beforeId: null });
  assert.equal(pendingHistory.status, 200); const pendingProposal = pendingHistory.body.items.find(value => value.status === 'pending'); assert.ok(pendingProposal);
  const accepted = await lostWrite(buyer, api + '/' + pendingProposal.proposalId + '/decisions', 'Setujui penggantian barang', 'ordering.substitution.accept', config.buyerToken); buyer = accepted.person;
  const afterAccept = await state(); assert.equal(afterAccept.movements, 2); assert.equal(afterAccept.decisions, 2); assert.equal(afterAccept.shipments, 1); assert.equal(afterAccept.financialHash, before.financialHash);
  assert.notEqual(afterAccept.physicalHash, before.physicalHash);
  const spender = observe(await user(config.spenderToken)); await spender.page.goto(origin.origin + '/ordering/reviews');
  await spender.page.getByRole('button', { name: 'Tinjau pesanan ' + orderNumber(config.orderId), exact: true }).click(); panel = spender.page.getByRole('region', { name: 'Tinjauan keputusan', exact: true });
  await panel.getByLabel('Catatan peninjauan', { exact: true }).fill('Independent approval of exact buyer replacement total');
  await panel.getByRole('button', { name: 'Setujui belanja', exact: true }).click(); await panel.getByRole('button', { name: 'Konfirmasi persetujuan belanja', exact: true }).click();
  await panel.getByText('Peninjauan selesai atau tidak ada cadangan yang dapat dibatalkan.', { exact: true }).waitFor();
  staff = observe(await user(config.makerToken)); await staff.page.goto(origin.origin + '/ordering/reviews');
  await staff.page.getByRole('button', { name: 'Tinjau pesanan ' + orderNumber(config.orderId) + ' / ' + config.suborderId, exact: true }).click(); panel = staff.page.getByRole('region', { name: 'Tinjauan keputusan', exact: true });
  await panel.getByLabel('Catatan peninjauan', { exact: true }).fill('Store accepts the explicitly revised buyer package'); await panel.getByRole('button', { name: 'Terima pesanan toko', exact: true }).click();
  await panel.getByRole('button', { name: 'Konfirmasi penerimaan toko', exact: true }).click(); await panel.getByText('Bagian pesanan ini sudah diterima toko.', { exact: true }).waitFor();
  await staff.page.goto(origin.origin + '/fulfillment/preparations'); await staff.page.getByRole('button', { name: 'Siapkan pesanan ' + orderNumber(config.orderId) + ' / ' + config.suborderId, exact: true }).click();
  panel = staff.page.getByRole('region', { name: 'Rencana persiapan', exact: true });
  const quantity = panel.getByLabel('Jumlah ' + config.replacementCode, { exact: false });
  // Select by the exact current replacement SKU row, never the original already dispatched line.
  const row = quantity.locator('..').locator('..'); await row.getByRole('checkbox').check(); await quantity.fill('1');
  await panel.getByLabel('Catatan persiapan', { exact: true }).fill('Prepare accepted replacement carton'); await panel.getByRole('button', { name: 'Tinjau persiapan', exact: true }).click();
  await panel.getByRole('heading', { name: 'Periksa kemasan yang dipilih', exact: true }).waitFor(); await panel.getByRole('button', { name: 'Konfirmasi persiapan', exact: true }).click();
  await panel.getByText('Persiapan versi 2 tersimpan.', { exact: true }).waitFor();
  // A real dispatcher completes MFA again before dispatching; the setup session is older than the dispatch fresh-factor age.
  const stepUp = await fetch(config.apiAddress + '/__fixture/fresh-session?actor=staff', { method: 'POST', headers: { 'X-Fixture-Control': config.controlKey } });
  assert.equal(stepUp.status, 200); await staff.context.close(); staff = observe(await user((await stepUp.json()).token));
  await staff.page.goto(origin.origin + '/fulfillment/dispatches'); await staff.page.getByRole('button', { name: 'Periksa keberangkatan pesanan ' + config.orderId, exact: true }).click();
  panel = staff.page.getByRole('region', { name: 'Pemeriksaan keberangkatan', exact: true });
  await panel.getByLabel('Jumlah lot ' + config.replacementLotId, { exact: true }).fill('1'); await panel.getByLabel('Kurir', { exact: true }).selectOption(config.actorIds.courier);
  await panel.getByLabel('Catatan keberangkatan', { exact: true }).fill('Actual agreed replacement leaves after independent approval');
  await panel.getByRole('button', { name: 'Tinjau keberangkatan', exact: true }).click(); await panel.getByRole('heading', { name: 'Konfirmasi barang dan kurir', exact: true }).waitFor();
  await capture(staff.page, 'replacement-dispatch-review'); await (await ready(staff.page, 'Konfirmasi keberangkatan')).click();
  await staff.page.getByRole('heading', { name: 'Keberangkatan tercatat', exact: true }).waitFor(); await capture(staff.page, 'replacement-dispatched');
  await buyer.page.goto(origin.origin + '/ordering/orders'); await buyer.page.getByRole('button', { name: 'Lihat rincian pesanan ' + orderNumber(config.orderId), exact: true }).click();
  await buyer.page.getByRole('link', { name: 'Lihat pengajuan dan keputusan penggantian pesanan ini', exact: true }).click();
  await buyer.page.getByText(/Disetujui pembeli/).waitFor(); await capture(buyer.page, 'order-linked-history');
  const after = await state(); assert.equal(after.shipments, 2); assert.equal(after.movements, 2); assert.equal(after.financialHash, before.financialHash);
  assert.equal(writes.length, 4); assert.equal(recoveries.length, 2); assert.deepEqual(errors, []);
  await writeFile(resolve(directory, config.proofPrefix + '-proof.json'), JSON.stringify({ localOnly: true, buildId: config.buildId, captures, writes, recoveries, timings, before, after,
    checks: ['staff proposal has no stock or financial effect', 'buyer decline has no stock or financial effect', 'staff cannot supply buyer consent', 'exact buyer consent replaces actual remaining reservations',
      'two committed reply losses recover original keys after real browser restarts', 'independent approvals precede actual replacement dispatch', 'original order links immutable amendment history'] }, null, 2));
  console.log('REAL_ORDER_SUBSTITUTION_BROWSER_PASS');
}
