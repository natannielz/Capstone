import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {createHash} from 'node:crypto';
import {resolve} from 'node:path';

export async function verifyInvoiceReleaseBrowser({config, maker, user, origin, directory, restartUser}) {
  const path = '/billing/invoice-releases', api = '/api/v1/billing/invoice-releases', invoices = '/api/v1/billing/invoices';
  const writes = [], recoveries = [], lost = [], observed = new WeakSet();
  const command = value => value === api || value === invoices || new RegExp('^' + api + '/proposals/[1-9][0-9]*/decisions$').test(value);
  function watch(account) {
    if (observed.has(account.context)) return account; observed.add(account.context);
    account.context.on('request', request => {
      if (request.method() !== 'POST') return; const endpoint = new URL(request.url()).pathname;
      const row = {path: endpoint, key: request.headers()['idempotency-key'], actor: request.headers()['x-expected-actor-id'], body: request.postDataJSON()};
      if (command(endpoint)) writes.push(row);
      if ([api + '/operations/query', invoices + '/operations/query'].includes(endpoint)) recoveries.push(row);
    }); return account;
  }
  async function open(account, invoice = false) {
    await account.page.goto(origin.origin + (invoice ? '/billing/invoices' : path));
    await account.page.getByRole('heading', {name: invoice ? 'Tagihan' : 'Persetujuan penerbitan invoice', exact: true}).waitFor();
    await account.page.getByRole('region', {name: invoice ? 'Daftar invoice' : 'Riwayat persetujuan invoice', exact: true}).waitFor();
  }
  async function field(region, label, value, role = 'textbox') {
    const control = role === 'label' ? region.getByLabel(label, {exact: true}) : region.getByRole(role, {name: label, exact: true});
    assert.equal(await control.count(), 1); await control.fill(value);
  }
  async function screenshot(page, name) {
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await page.screenshot({path: resolve(directory, config.proofPrefix + '-' + name + '.png'), fullPage: true});
  }
  async function query(page, actor, endpoint, body) {
    return page.evaluate(async ({actor, endpoint, body}) => {
      const csrf = await fetch('/api/v1/identity/csrf', {credentials: 'same-origin', cache: 'no-store', signal: AbortSignal.timeout(30_000)}).then(response => response.json());
      const response = await fetch(endpoint, {method: 'POST', credentials: 'same-origin', cache: 'no-store', signal: AbortSignal.timeout(30_000),
        headers: {'Content-Type': 'application/json', 'X-CSRF-Token': csrf.requestToken, 'X-Expected-Actor-Id': actor}, body: JSON.stringify(body)});
      return {status: response.status, body: await response.json()};
    }, {actor, endpoint, body});
  }
  const confirmation = page => page.getByRole('region', {name: 'Konfirmasi persetujuan invoice', exact: true});
  async function confirm(page, name) {
    const region = confirmation(page); await region.waitFor();
    assert.equal(await region.evaluate(element => element === document.activeElement), true);
    assert.equal(await region.getByRole('button', {name, exact: true}).isDisabled(), true);
    await region.getByRole('checkbox', {name: 'Saya sudah memeriksa penerimaan, alokasi biaya, kalender, dan akun pembukuan yang ditampilkan.', exact: true}).check();
    return region.getByRole('button', {name, exact: true});
  }
  async function committedReplyLoss(account, endpoint, action, button) {
    let done; const received = new Promise((accept, reject) => {done = {accept, reject};});
    const intercept = async route => {
      try {
        const request = route.request(), response = await route.fetch({timeout: 30_000, maxRetries: 0});
        assert.equal(response.status(), 200); const result = await response.json();
        const receipt = {action, key: request.headers()['idempotency-key'], actor: request.headers()['x-expected-actor-id'], result};
        lost.push(receipt); await route.abort('failed'); await account.page.unroute(origin.origin + endpoint, intercept); done.accept(receipt);
      } catch (error) {done.reject(error); await route.abort('failed').catch(() => {});}
    };
    await account.page.route(origin.origin + endpoint, intercept); await button.click(); const receipt = await received;
    const recovery = account.page.getByRole('region', {name: action === 'issue' ? 'Pemulihan permintaan' : 'Pemulihan persetujuan invoice', exact: true});
    await recovery.waitFor(); assert.equal(writes.filter(row => row.key === receipt.key).length, 1);
    const prefix = action === 'issue' ? 'unit-toko:pending:v1:' + receipt.actor + ':' : 'unit-toko.invoice-release.pending.' + receipt.actor + '.';
    const refs = await account.page.evaluate(prefix => Object.entries(localStorage).filter(([key]) => key.startsWith(prefix)).map(([, text]) => JSON.parse(text)), prefix);
    assert.equal(refs.length, 1); assert.equal(refs[0].key, receipt.key); assert.equal(refs[0].actorId, receipt.actor);
    if (action === 'issue') assert.deepEqual(Object.keys(refs[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    else {
      assert.deepEqual(Object.keys(refs[0]).sort(), ['action', 'actorId', 'key', 'proposalId', 'revision', 'selectionKey']);
      assert.equal(refs[0].action, action); assert.equal(refs[0].selectionKey, receipt.result.selectionKey);
      assert.equal(BigInt(refs[0].revision) + 1n, BigInt(receipt.result.revision));
    }
    return receipt;
  }
  async function restartRecover(account, token, receipt) {
    const state = await account.context.storageState();
    assert.ok(state.origins.some(row => row.origin === origin.origin && row.localStorage.some(item => item.name.endsWith(receipt.key))));
    const restarted = watch(await restartUser(token, state)); await open(restarted, receipt.action === 'issue');
    const label = receipt.action === 'issue' ? 'Periksa hasil' : 'Periksa hasil ' + (receipt.action === 'propose' ? 'usulan' : 'disetujui');
    const check = restarted.page.getByRole('button', {name: label, exact: true}); await check.click(); await check.waitFor({state: 'hidden'});
    assert.equal(writes.filter(row => row.key === receipt.key).length, 1);
    assert.ok(recoveries.some(row => row.actor === receipt.actor && row.body.key === receipt.key));
    if (receipt.action === 'issue') await restarted.page.getByRole('region', {name: 'Invoice terbit', exact: true}).waitFor();
    else await restarted.page.getByRole('button', {name: 'Lihat persetujuan ' + receipt.result.releaseId, exact: true}).waitFor();
    return restarted;
  }
  async function load(account, releaseId) {
    await account.page.getByRole('button', {name: 'Lihat persetujuan ' + releaseId, exact: true}).click();
    const result = account.page.getByRole('region', {name: 'Fakta persetujuan tersimpan', exact: true}); await result.waitFor(); return result;
  }
  let owner = watch(maker); await open(owner);
  const source = owner.page.getByRole('region', {name: 'Penerimaan untuk persetujuan invoice', exact: true});
  for (const item of config.selection) {
    const checkbox = source.getByRole('checkbox', {name: new RegExp('^Penerimaan ' + item.receiptId + ' — ')});
    assert.equal(await checkbox.count(), 1); await checkbox.check();
  }
  await owner.page.getByRole('button', {name: 'Periksa sumber penerbitan', exact: true}).click();
  const form = owner.page.getByRole('region', {name: 'Usulan persetujuan invoice', exact: true}); await form.waitFor();
  assert.equal(await form.locator('input').evaluateAll(inputs => inputs.every(input => input.value === '')), true);
  const entered = config.expected;
  for (const [label, value] of [['Awal periode penerimaan', entered.periodFrom], ['Akhir periode penerimaan', entered.periodTo],
    ['Mulai berlaku (WIB)', entered.validFromWib], ['Akhir berlaku (WIB)', entered.validUntilWib]]) await field(form, label, value, 'label');
  for (const [label, value] of [['Referensi persetujuan format invoice', entered.templateApprovalReference], ['Referensi persetujuan penomoran', entered.numberingApprovalReference],
    ['Referensi pemeriksaan kelayakan', entered.eligibilityReviewReference], ['Pemetaan akun piutang', entered.mappings.receivable],
    ['Pemetaan akun pendapatan', entered.mappings.revenue], ['Pemetaan akun pajak', entered.mappings.tax], ['Pemetaan akun biaya', entered.mappings.charges]]) await field(form, label, value);
  for (const row of entered.allocations) {
    const key = [row.receiptId, row.skuId, row.lineRevision].join(':'); await field(form, 'Pajak ' + key, row.tax); await field(form, 'Biaya ' + key, row.charges);
  }
  await field(form, 'Alasan perubahan persetujuan invoice', 'Synthetic actual invoice source review');
  await form.getByRole('button', {name: 'Tinjau usulan penerbitan', exact: true}).click();
  const propose = await confirm(owner.page, 'Konfirmasi usulan penerbitan');
  await screenshot(owner.page, 'proposal-mobile'); await owner.page.setViewportSize({width: 1440, height: 1000}); await screenshot(owner.page, 'proposal-desktop');
  await owner.context.setOffline(true); await owner.page.getByText('Koneksi belum tersedia. Referensi permintaan tetap disimpan.', {exact: true}).waitFor();
  assert.equal(await propose.isDisabled(), true); assert.equal(writes.length, 0); await owner.context.setOffline(false);
  await owner.page.getByText('Koneksi belum tersedia. Referensi permintaan tetap disimpan.', {exact: true}).waitFor({state: 'hidden'});
  const proposed = await committedReplyLoss(owner, api, 'propose', propose);
  assert.equal(proposed.actor, config.makerId); assert.equal(proposed.result.action, 'propose'); assert.equal(proposed.result.revision, '1');
  const proposalBody = writes[0].body.input; assert.deepEqual(proposalBody.receipts, config.selection); assert.deepEqual(proposalBody.allocations, entered.allocations);
  assert.deepEqual(proposalBody.mappings, entered.mappings); assert.equal(Date.parse(proposalBody.validFrom), Date.parse(entered.validFrom));
  assert.equal(Date.parse(proposalBody.validUntil), Date.parse(entered.validUntil)); assert.equal(proposalBody.expectedReleaseRevision, '0');
  owner = await restartRecover(owner, config.makerToken, proposed); const original = await load(owner, proposed.result.releaseId);
  assert.equal(await original.getByRole('button', {name: 'Tinjau persetujuan penerbitan', exact: true}).count(), 0);
  let checker = watch(await user(config.checkerToken)); await open(checker); const pending = await load(checker, proposed.result.releaseId);
  assert.equal(await checker.page.getByRole('region', {name: 'Penerimaan untuk persetujuan invoice', exact: true}).count(), 0);
  await field(pending, 'Alasan keputusan persetujuan invoice', 'Synthetic independent checker acceptance');
  await pending.getByRole('button', {name: 'Tinjau persetujuan penerbitan', exact: true}).click();
  const approve = await confirm(checker.page, 'Konfirmasi disetujui penerbitan'); await screenshot(checker.page, 'independent-review-mobile');
  const approved = await committedReplyLoss(checker, api + '/proposals/' + proposed.result.proposalId + '/decisions', 'approve', approve);
  assert.equal(approved.actor, config.checkerId); assert.notEqual(approved.actor, proposed.actor);
  assert.equal(approved.result.action, 'approve'); assert.equal(approved.result.revision, '2');
  assert.equal(approved.result.proposalId, proposed.result.proposalId); assert.equal(approved.result.selectionKey, proposed.result.selectionKey);
  checker = await restartRecover(checker, config.checkerToken, approved); await load(checker, approved.result.releaseId);
  const reader = watch(await user(config.readerToken)); await open(reader); const viewed = await load(reader, approved.result.releaseId);
  assert.equal(await viewed.getByRole('button', {name: /Tinjau.*penerbitan/}).count(), 0); await screenshot(reader.page, 'reader-mobile');
  const outsider = watch(await user(config.outsiderToken)); await open(outsider);
  const unavailable = await query(outsider.page, config.outsiderId, api + '/query', {purpose: 'invoice-releases', beforeId: null});
  assert.equal(unavailable.status, 200); assert.deepEqual(unavailable.body.items, []);
  assert.equal((await query(outsider.page, config.outsiderId, api + '/records/' + approved.result.recordId + '/query', {purpose: 'invoice-releases'})).status, 403);
  let finance = watch(await user(config.financeToken)); await open(finance, true);
  const candidates = finance.page.getByRole('region', {name: 'Penerimaan untuk invoice', exact: true});
  for (const item of config.selection) {
    const select = candidates.getByRole('checkbox', {name: new RegExp('^Bukti penerimaan ' + item.receiptId + '\\b')}); assert.equal(await select.count(), 1); await select.check();
  }
  await field(candidates, 'Alasan penerbitan', 'Synthetic invoice from independently approved received goods');
  await candidates.getByRole('button', {name: 'Tinjau invoice', exact: true}).click();
  const invoiceReview = finance.page.getByRole('region', {name: 'Tinjauan invoice', exact: true}); await invoiceReview.waitFor(); await screenshot(finance.page, 'invoice-review-mobile');
  const issued = await committedReplyLoss(finance, invoices, 'issue', invoiceReview.getByRole('button', {name: 'Terbitkan invoice', exact: true}));
  assert.equal(issued.actor, config.financeId); assert.deepEqual(writes[2].body.receipts, config.selection);
  finance = await restartRecover(finance, config.financeToken, issued);
  const download = finance.page.waitForEvent('download'); await finance.page.getByRole('button', {name: 'Unduh invoice', exact: true}).click();
  const file = await download, bytes = readFileSync(await file.path());
  assert.equal(createHash('sha256').update(bytes).digest('hex'), issued.result.documentHash);
  assert.ok(!bytes.includes(Buffer.from('recordHash'))); assert.ok(!bytes.includes(Buffer.from('invoiceReleaseBindings')));
  await screenshot(finance.page, 'issued-mobile'); await finance.page.setViewportSize({width: 1440, height: 1000}); await screenshot(finance.page, 'issued-desktop');
  await finance.page.reload(); await finance.page.getByRole('button', {name: /^Buka invoice /}).click();
  await finance.page.getByRole('region', {name: 'Invoice terbit', exact: true}).waitFor();
  const foreignInvoice = watch(await user(config.outsiderToken)); await open(foreignInvoice, true);
  assert.equal((await query(foreignInvoice.page, config.outsiderId, invoices + '/' + issued.result.id + '/query', {purpose: 'invoices'})).status, 403);
  assert.equal(writes.length, 3); assert.equal(lost.length, 3); assert.equal(new Set(writes.map(row => row.key)).size, 3);
  assert.deepEqual(writes.map(row => row.actor), [config.makerId, config.checkerId, config.financeId]);
  console.log('REAL_INVOICE_RELEASE_BROWSER_PASS ' + JSON.stringify({writes: 3, actualIndependentApproval: true, actualFiniteReceipt: true,
    lostCommittedReplies: 3, browserProcessRestarts: 3, originalKeysRecovered: true, privateDownloadHash: true, wrongScopeDenied: true, desktop: true, mobile: true}));
}
