// Real shared HTTPS/Next/API/Mongo host supplied by verify-catalog-price-browser.
// Every account and provider proof in this rehearsal is explicitly synthetic.
import assert from 'node:assert/strict';
import { resolve } from 'node:path';

export async function verifyPrimaryShopBrowser({ config, user, origin, directory, writes, requests, loseNext, dropped }) {
  assert.match(config.publicId, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
  const buyer = await user('buyer'); const page = buyer.page;
  await page.getByRole('heading', { name: 'Katalog kemasan', exact: true }).waitFor();
  const open = page.getByRole('button', { name: 'Rincian PRIMARY-12 Dus dua belas', exact: true }); await open.waitFor();
  assert.equal(await page.getByRole('dialog').count(), 0, 'A package must never be selected implicitly');
  const link = page.getByRole('link', { name: 'Halaman PRIMARY-12 Dus dua belas', exact: true });
  assert.equal(await link.getAttribute('href'), '/shop/' + config.publicId);
  await page.setViewportSize({ width: 360, height: 800 });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'primary-shop-mobile.png'), fullPage: true });
  await open.click(); const dialog = page.getByRole('dialog', { name: 'Pensil utama — Dus dua belas', exact: true }); await dialog.waitFor();
  assert.match(await dialog.innerText(), /12 piece per kemasan/); assert.match(await dialog.innerText(), /IDR 12\.000,25/);
  assert.equal(await dialog.getByLabel('Cabang pemesan', { exact: true }).inputValue(), '');
  assert.equal(await dialog.getByLabel('Jumlah kemasan', { exact: true }).inputValue(), '');
  assert.equal(writes.length, 0); await dialog.getByRole('button', { name: 'Tutup rincian', exact: true }).click();
  await dialog.waitFor({ state: 'hidden' }); assert.equal(await open.evaluate(element => element === document.activeElement), true);
  await open.click(); await dialog.waitFor(); await page.keyboard.press('Escape'); await dialog.waitFor({ state: 'hidden' });
  assert.equal(await open.evaluate(element => element === document.activeElement), true); assert.equal(writes.length, 0, 'Dismissal saves nothing');
  await link.click(); await page.waitForURL(origin.origin + '/shop/' + config.publicId);
  await page.getByRole('heading', { name: 'Rincian kemasan', exact: true }).waitFor();
  const details = page.getByRole('region', { name: 'Rincian kemasan', exact: true }); await details.waitFor();
  await details.getByRole('heading', { name: 'Pensil utama — Dus dua belas', exact: true }).waitFor();
  assert.equal(await page.getByRole('dialog').count(), 0); assert.match(await details.innerText(), /Pensil utama — Dus dua belas/);
  assert.match(await details.innerText(), /12 piece per kemasan/); assert.match(await details.innerText(), /IDR 12\.000,25/);
  assert.equal(await details.getByRole('img').count(), 0, 'There are no invented product images in the empty-media fixture');
  const branch = details.getByLabel('Cabang pemesan', { exact: true }), quantity = details.getByLabel('Jumlah kemasan', { exact: true });
  await branch.waitFor(); assert.equal(await branch.inputValue(), ''); assert.equal(await quantity.inputValue(), '');
  await branch.selectOption('10:11:');
  for (const invalid of ['3', '5']) {
    const previews = requests.filter(request => request.path === '/api/v1/ordering/quote/query').length;
    await quantity.fill(invalid); await details.getByRole('button', { name: 'Periksa subtotal', exact: true }).click();
    await details.getByRole('alert').filter({ hasText: 'Isi jumlah kemasan sesuai minimum dan kelipatan pemesanan.' }).waitFor();
    assert.equal(await details.getByRole('button', { name: 'Simpan sebagai draf', exact: true }).count(), 0);
    assert.equal(requests.filter(request => request.path === '/api/v1/ordering/quote/query').length, previews, 'Invalid minimum/increment never reaches the API');
  }
  await quantity.fill('6'); await details.getByRole('button', { name: 'Periksa subtotal', exact: true }).click();
  const subtotal = details.getByRole('status').filter({ hasText: 'Subtotal barang' }); await subtotal.waitFor();
  assert.match(await subtotal.innerText(), /IDR 72\.001,50/); assert.match(await subtotal.innerText(), /6 Dus dua belas · 72 piece/);
  assert.match(await subtotal.innerText(), /Pesanan belum dikirim dan stok belum dipesan/);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'primary-package-subtotal-mobile.png'), fullPage: true });
  await page.setViewportSize({ width: 1280, height: 960 });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'primary-package-subtotal-desktop.png'), fullPage: true });
  loseNext(); await details.getByRole('button', { name: 'Simpan sebagai draf', exact: true }).click();
  await details.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(dropped(), 1); assert.equal(writes.length, 1);
  const attempt = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  assert.equal(attempt.length, 1); assert.deepEqual(Object.keys(attempt[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.equal(attempt[0].actorId, config.buyerId); assert.equal(attempt[0].operation, 'ordering.draft.save'); assert.equal(attempt[0].key, writes[0].key);
  const selection = writes[0].body.input.selections;
  assert.equal(selection.length, 1); assert.equal(selection[0].skuId, config.skuId); assert.equal(selection[0].definitionId, config.definitionId);
  assert.equal(selection[0].priceId, config.priceId); assert.equal(selection[0].packages, 6);
  await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(await page.getByRole('button', { name: 'Selesaikan dan tutup', exact: true }).count(), 0);
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  await page.getByRole('status').filter({ hasText: 'Draf tersimpan.' }).waitFor();
  assert.equal(writes.length, 1, 'A lost completed reply is recovered by lookup, without a replacement draft');
  const recoveries = requests.filter(request => request.path === '/api/v1/ordering/drafts/operations/query');
  assert.equal(recoveries.length, 1); assert.deepEqual(recoveries[0].body, { purpose: 'operation-recovery', key: attempt[0].key });
  assert.equal(await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).length), 0);
  await page.getByRole('link', { name: 'Buka draf pesanan', exact: true }).click();
  await page.getByLabel('Cabang pemesan', { exact: true }).selectOption('10:11:');
  await page.getByRole('region', { name: 'Daftar draf', exact: true }).getByRole('button', { name: /Draf [0-9]+/ }).waitFor();
  await page.screenshot({ path: resolve(directory, 'primary-draft-recovered-desktop.png'), fullPage: true });
  const foreign = await user('foreign');
  // Active branch permissions do not replace an independently approved catalog entitlement.
  await foreign.page.getByText('Katalog ini belum tersedia untuk akun Anda. Hubungi pengelola akun.', { exact: true }).waitFor();
  assert.equal(await foreign.page.getByRole('button', { name: 'Rincian PRIMARY-12 Dus dua belas', exact: true }).count(), 0);
  const unavailable = await foreign.page.evaluate(async ({ id, actor }) => {
    const csrf = await fetch('/api/v1/identity/csrf', { credentials: 'same-origin', cache: 'no-store' });
    if (!csrf.ok) throw new Error('Foreign catalog probe requires the real CSRF issuer');
    const { requestToken } = await csrf.json();
    if (typeof requestToken !== 'string' || !requestToken) throw new Error('Invalid real CSRF response');
    const results = [];
    for (const path of ['/api/v1/catalog/variants/query', '/api/v1/catalog/variants/' + id + '/query']) {
      const response = await fetch(path, { method: 'POST', credentials: 'same-origin', headers: {
        'Content-Type': 'application/json', 'X-Expected-Actor-Id': actor, 'X-CSRF-Token': requestToken,
      }, body: '{}' });
      results.push({ status: response.status, body: await response.json() });
    }
    return results;
  }, { id: config.publicId, actor: config.foreignId });
  assert.equal(unavailable.length, 2);
  for (const denial of unavailable) {
    assert.equal(denial.status, 403); assert.equal(denial.body.code, 'catalog_visibility_denied');
    for (const field of ['items', 'total', 'skuId', 'publicId', 'definition', 'currentPrice', 'familyId']) assert.equal(Object.hasOwn(denial.body, field), false);
    assert.doesNotMatch(JSON.stringify(denial.body), /PRIMARY-12|Pensil utama|12000\.25/);
  }
  assert.doesNotMatch(await foreign.page.locator('main').innerText(), /PRIMARY-12|Pensil utama/);
  assert.equal(writes.length, 1);
  assert.deepEqual(requests.filter(request => /^\/api\/(catalog|session|commands)(?:\/|$)/.test(request.path)), [], 'Primary shop uses no legacy catalog/session/command routes');
  await foreign.context.close(); await buyer.context.close();
  console.log('REAL_PRIMARY_SHOP_BROWSER_PASS ' + JSON.stringify({ savedDraftCommands: writes.length, exactReceiptLookups: recoveries.length,
    droppedCommittedResponses: dropped(), packagePublicId: config.publicId, screenshotDirectory: directory }));
}
