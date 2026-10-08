import assert from 'node:assert/strict';
import {resolve} from 'node:path';

export async function verifyTemporaryDelegationBrowser({config, maker, user, origin, directory, restartUser}) {
  const endpoint = origin.origin + '/api/v1/access/delegations', path = origin.origin + '/administration/delegations';
  const writes = [], recovered = [];
  let actor = maker, token = config.financeToken;
  const setup = page => page.setDefaultTimeout(30_000);
  async function open(person) {setup(person.page); await person.page.goto(path); await person.page.getByRole('heading', {name: 'Delegasi sementara', exact: true}).waitFor();
    await person.page.getByRole('region', {name: 'Daftar delegasi'}).waitFor(); return person;}
  async function proposal(page) {
    await page.getByRole('combobox', {name: 'Akses sumber', exact: true}).selectOption(config.sourceGrantId);
    await page.getByLabel('Nomor akun penerima', {exact: true}).fill(config.recipientId);
    await page.getByRole('button', {name: 'Periksa penerima', exact: true}).click();
    await page.getByRole('combobox', {name: 'Keanggotaan penerima', exact: true}).selectOption(config.recipientMembershipId);
    const choices = page.getByRole('group', {name: 'Izin yang didelegasikan'}).getByRole('checkbox'); assert.equal(await choices.count(), 1); await choices.check();
    await page.getByLabel('Mulai berlaku (WIB)', {exact: true}).fill(config.effectiveWib);
    await page.getByLabel('Berakhir (WIB)', {exact: true}).fill(config.expiryWib);
    await page.getByRole('textbox', {name: 'Alasan delegasi', exact: true}).fill('Delegasi browser sintetis yang ditinjau');
    await page.getByRole('button', {name: 'Tinjau pengajuan delegasi', exact: true}).click();
    await page.getByRole('region', {name: 'Konfirmasi delegasi'}).waitFor();
  }
  async function commandWithReplyLoss(action, stalePage = null) {
    const page = actor.page, region = page.getByRole('region', {name: 'Konfirmasi delegasi'});
    assert.equal(await region.evaluate(node => node === document.activeElement), true);
    await region.getByRole('checkbox').check();
    let original = null, originalBody = null, resolveCommit, rejectCommit, commitTimer;
    const committed = new Promise((resolve, reject) => {resolveCommit = resolve; rejectCommit = reject;});
    await page.route(endpoint, async route => {
      try {
      assert.equal(route.request().method(), 'POST');
      const body = route.request().postDataJSON(); assert.equal(body.action, action);
      original = route.request().headers()['idempotency-key']; assert.ok(original); originalBody = body;
      const response = await route.fetch({timeout: 30_000}); assert.equal(response.status(), 200, await response.text());
      const result = await response.json(); writes.push({action, key: original, result});
      await route.abort('failed'); resolveCommit();
      } catch (error) {rejectCommit(error); await route.abort('failed').catch(() => {});}
    }, {times: 1});
    commitTimer = setTimeout(() => rejectCommit(new Error('Actual original delegation write did not complete within 30000ms')), 30_000);
    try {await Promise.all([committed, region.getByRole('button', {name: /^Konfirmasi /}).click()]);} finally {clearTimeout(commitTimer);}
    await page.getByRole('region', {name: 'Operasi delegasi belum terkonfirmasi'}).waitFor();
    await page.getByRole('button', {name: 'Periksa hasil asli ' + original, exact: true}).waitFor({state: 'visible'});
    if (stalePage) {
      await stalePage.getByRole('region', {name: 'Konfirmasi delegasi'}).waitFor({state: 'hidden'});
      assert.equal(await stalePage.getByRole('combobox', {name: 'Akses sumber', exact: true}).isDisabled(), true);
    }
    assert.ok(original && originalBody); const storage = await actor.context.storageState();
    const records = storage.origins.find(row => row.origin === origin.origin)?.localStorage.filter(row => row.name.startsWith('unit-toko.delegation.pending.')) ?? [];
    assert.equal(records.length, 1); const reference = JSON.parse(records[0].value);
    assert.equal(reference.key, original); assert.equal(reference.action, action); assert.equal(Object.keys(reference).sort().join(','), 'action,actorId,createdAt,id,key,revision');
    actor = await restartUser(token, storage); await open(actor); assert.equal(await actor.page.evaluate(() => sessionStorage.length), 0);
    const recoveryResponse = actor.page.waitForResponse(response => response.url() === endpoint + '/operations/query' && response.request().method() === 'POST');
    await actor.page.getByRole('button', {name: 'Periksa hasil asli ' + original, exact: true}).click();
    const response = await recoveryResponse; assert.equal(response.status(), 200); const outcome = await response.json();
    assert.equal(outcome.state, 'committed'); assert.deepEqual(outcome.result, writes.at(-1).result); recovered.push({key: original, result: outcome.result});
    await actor.page.getByRole('region', {name: 'Operasi delegasi belum terkonfirmasi'}).waitFor({state: 'hidden'});
    assert.equal(await actor.page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko.delegation.pending.')).length), 0);
    return outcome.result.delegationId;
  }
  await open(actor); await proposal(actor.page);
  let attemptedWrites = 0; actor.page.on('request', request => {if (request.url() === endpoint && request.method() === 'POST') attemptedWrites++;});
  await actor.page.getByRole('button', {name: 'Batalkan tinjauan', exact: true}).click(); assert.equal(attemptedWrites, 0);
  const otherPage = await actor.context.newPage(); setup(otherPage); await otherPage.goto(path); await proposal(otherPage);
  await proposal(actor.page); const id = await commandWithReplyLoss('propose', otherPage);
  const unrelated = await user(config.deniedToken); await open(unrelated);
  const denied = await unrelated.page.evaluate(async ({url, actorId}) => {
    const csrf = await fetch('/api/v1/identity/csrf').then(response => response.json());
    const response = await fetch(url, {method: 'POST', headers: {'Content-Type': 'application/json', 'X-CSRF-Token': csrf.requestToken, 'X-Expected-Actor-Id': actorId}, body: JSON.stringify({purpose: 'delegation-review'})});
    return {status: response.status, body: await response.json()};
  }, {url: endpoint + '/' + id + '/query', actorId: config.deniedId});
  assert.equal(denied.status, 403); assert.equal(JSON.stringify(denied.body).includes('Delegasi browser sintetis'), false);
  token = config.checkerToken; actor = await user(token); await open(actor);
  await actor.page.getByRole('button', {name: new RegExp('^Delegasi ' + id + ' ')}).click();
  await actor.page.getByRole('textbox', {name: 'Alasan keputusan atau catatan serah-terima', exact: true}).fill('Persetujuan orang berbeda dalam browser');
  await actor.page.getByRole('button', {name: 'Tinjau: Setujui delegasi', exact: true}).click(); await commandWithReplyLoss('approve');
  token = config.financeToken; actor = await user(token); await open(actor);
  await actor.page.getByRole('button', {name: new RegExp('^Delegasi ' + id + ' ')}).click();
  await actor.page.getByRole('textbox', {name: 'Alasan keputusan atau catatan serah-terima', exact: true}).fill('Catatan peserta; tugas belum dipindahkan');
  await actor.page.getByRole('button', {name: 'Tinjau: Catat serah-terima', exact: true}).click(); await commandWithReplyLoss('handover');
  await actor.page.getByRole('button', {name: new RegExp('^Delegasi ' + id + ' ')}).click();
  await actor.page.getByRole('textbox', {name: 'Alasan keputusan atau catatan serah-terima', exact: true}).fill('Penghentian delegasi setelah tugas sementara');
  await actor.page.getByRole('button', {name: 'Tinjau: Hentikan delegasi', exact: true}).click(); await commandWithReplyLoss('revoke');
  await actor.page.getByRole('button', {name: new RegExp('^Delegasi ' + id + ' ')}).click();
  await actor.page.getByRole('heading', {name: 'Riwayat delegasi', exact: true}).waitFor();
  assert.equal(await actor.page.getByRole('region', {name: 'Rincian delegasi'}).getByRole('listitem').count(), 5); // one permission plus four immutable events
  for (const width of [1440, 360]) {
    await actor.page.setViewportSize({width, height: 1000});
    assert.equal(await actor.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
    await actor.page.screenshot({path: resolve(directory, config.proofPrefix + '-delegation-' + width + '.png'), fullPage: true});
  }
  assert.deepEqual(writes.map(row => row.action), ['propose', 'approve', 'handover', 'revoke']); assert.equal(new Set(writes.map(row => row.key)).size, 4);
  assert.deepEqual(recovered.map(row => row.key), writes.map(row => row.key));
  console.log('REAL_TEMPORARY_DELEGATION_BROWSER_PASS');
}
