// Real production Next/API/Mongo postal governance; supplied accounts and postal facts are synthetic fixtures.
import assert from 'node:assert/strict';
import { resolve } from 'node:path';

export async function verifyOrganizationAddressBrowser({ config, maker, user, origin, directory, addressWrites, addressRecoveries, dropNext, dropped }) {
  const { page, context } = maker, path = '/administration/organizations/addresses';
  assert.notEqual(config.makerId, config.checkerId); assert.notEqual(config.readerId, config.checkerId);
  const bootstrap = new Map(), bootstrapPaths = new Set(['/api/v1/identity/session', '/api/v1/access/current']);
  function observeBootstrap(currentPage) {
    if (bootstrap.has(currentPage)) return;
    const observation = { responses: [], pending: [] }; bootstrap.set(currentPage, observation);
    currentPage.on('response', response => {
      const requestPath = new URL(response.url()).pathname; if (!bootstrapPaths.has(requestPath)) return;
      const record = { path: requestPath, status: response.status(), code: null, ...(requestPath === '/api/v1/identity/session' ? { authenticated: null } : {}) }; observation.responses.push(record);
      // Never retain or print a successful session/access body, headers or credentials.
      if (record.status === 200 && requestPath === '/api/v1/identity/session') observation.pending.push(response.json().then(session => {
        if (typeof session?.authenticated === 'boolean') record.authenticated = session.authenticated;
      }).catch(() => {}));
      if (record.status >= 400) observation.pending.push(response.json().then(problem => {
        if (typeof problem?.code === 'string' && /^[a-zA-Z0-9_.-]{1,80}$/.test(problem.code)) record.code = problem.code;
      }).catch(() => {}).finally(() => console.error('FIXTURE POSTAL BOOTSTRAP FAILURE ' + JSON.stringify(record))));
    });
    currentPage.on('requestfailed', request => {
      const requestPath = new URL(request.url()).pathname; if (!bootstrapPaths.has(requestPath)) return;
      const record = { path: requestPath, status: null, code: null, transportFailure: request.failure()?.errorText.match(/net::ERR_[A-Z_]+/)?.[0] ?? 'request_failed' };
      observation.responses.push(record); console.error('FIXTURE POSTAL BOOTSTRAP FAILURE ' + JSON.stringify(record));
    });
  }
  async function reportBootstrapFailure(currentPage) {
    const observation = bootstrap.get(currentPage); if (observation) await Promise.allSettled(observation.pending);
    const purposeCount = await currentPage.getByLabel('Tujuan penggunaan', { exact: true }).count();
    const unitCount = await currentPage.getByLabel('Organisasi / unit', { exact: true }).count();
    const controls = purposeCount === 0 || unitCount === 0 ? await currentPage.evaluate(() => [...document.querySelectorAll('select,input,textarea')].map(control => {
      const referenced = control.getAttribute('aria-labelledby');
      const name = referenced ? referenced.split(/\s+/).map(id => document.getElementById(id)?.textContent ?? '').join(' ')
        : control.getAttribute('aria-label') ?? [...(control.labels ?? [])].map(label => label.textContent ?? '').join(' ');
      return { tag: control.tagName.toLowerCase(), name: name.replace(/\s+/g, ' ').trim() };
    })) : null;
    console.error('FIXTURE POSTAL BOOTSTRAP DIAGNOSTIC ' + JSON.stringify({ responses: observation?.responses ?? [],
      exactLabelCounts: { purpose: purposeCount, unit: unitCount }, controlCount: controls?.length ?? null, controls }));
  }
  observeBootstrap(page);
  const referenceEntries = currentPage => currentPage.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  const protectedQuery = (currentPage, actorId, route, body) => currentPage.evaluate(async ({ actorId, route, body }) => {
    const csrf = await fetch('/api/v1/identity/csrf', { credentials: 'same-origin', cache: 'no-store', redirect: 'error' });
    if (!csrf.ok) throw new Error('Actual request token unavailable'); const token = await csrf.json();
    const response = await fetch(route, { method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': token.requestToken, 'X-Expected-Actor-Id': actorId }, body: JSON.stringify(body) });
    return { status: response.status, body: await response.json() };
  }, { actorId, route, body });
  async function choose(currentPage) {
    try {
      await currentPage.getByLabel('Tujuan penggunaan', { exact: true }).selectOption('delivery');
      const select = currentPage.getByLabel('Organisasi / unit', { exact: true });
      await select.getByRole('option', { name: config.targetCode + ' · ' + config.targetName + ' — Cabang', exact: true }).waitFor({ state: 'attached', timeout: 60000 });
      await select.selectOption(config.targetKind + ':' + config.targetId);
      await currentPage.getByRole('region', { name: 'Rincian alamat organisasi', exact: true }).waitFor({ timeout: 60000 });
    } catch (failure) { await reportBootstrapFailure(currentPage).catch(() => {}); throw failure; }
  }
  async function checkStoredReference(currentPage, operation, write) {
    const pending = await referenceEntries(currentPage); assert.equal(pending.length, 1); assert.equal(pending[0].operation, operation); assert.equal(pending[0].key, write.key);
    assert.deepEqual(Object.keys(pending[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    for (const privateValue of [config.address.line1, config.address.postalCode, write.body.binding, write.body.reason]) assert.ok(!JSON.stringify(pending).includes(privateValue));
    assert.equal(await currentPage.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0); return pending;
  }
  await page.goto(origin.origin + path); await choose(page);
  await page.getByRole('button', { name: 'Ajukan alamat', exact: true }).click();
  for (const [field, label] of Object.entries({ line1: 'Alamat utama', line2: 'Alamat tambahan', city: 'Kota / kabupaten', region: 'Provinsi / wilayah', postalCode: 'Kode pos', countryCode: 'Kode negara' })) {
    if (config.address[field] !== null) await page.getByLabel(label, { exact: field !== 'line2' }).fill(config.address[field]);
  }
  await page.getByLabel('Alamat utama', { exact: true }).focus(); await page.keyboard.press('Tab');
  assert.equal(await page.getByLabel('Alamat tambahan').evaluate(element => element === document.activeElement), true);
  assert.equal(await page.getByRole('textbox', { name: /Penerima|Telepon|Kontak|Email/ }).count(), 0);
  await page.getByLabel('Alasan pengajuan', { exact: true }).fill('Alamat cabang dari sumber organisasi sintetis untuk pengujian independen');
  await page.getByRole('button', { name: 'Periksa rincian pengajuan', exact: true }).click();
  const review = page.getByRole('region', { name: 'Rincian pengajuan alamat', exact: true }); await review.waitFor({ timeout: 60000 });
  assert.equal(await review.getByRole('heading', { name: 'Rincian yang akan diajukan', exact: true }).evaluate(element => element === document.activeElement), true);
  assert.equal(await review.getByRole('textbox').count(), 0); assert.equal(await review.getByRole('button', { name: 'Kirim pengajuan alamat', exact: true }).isDisabled(), true);
  await review.getByRole('checkbox').check(); await context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor();
  assert.equal(await review.getByRole('button', { name: 'Kirim pengajuan alamat', exact: true }).isDisabled(), true); assert.equal(addressWrites.length, 0);
  await context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'organization-address-proposal-mobile.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'organization-address-proposal-desktop.png'), fullPage: true }); await page.setViewportSize({ width: 360, height: 800 });
  dropNext(); await review.getByRole('button', { name: 'Kirim pengajuan alamat', exact: true }).click();
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ timeout: 60000 }); assert.equal(addressWrites.length, 1);
  const write = addressWrites[0]; assert.equal(write.path, '/api/v1/organizations/addresses/proposals'); assert.equal(write.actor, config.makerId);
  assert.match(write.key, new RegExp('^oa\\.' + config.targetId + '\\.delivery\\.[0-9a-f-]{36}$'));
  assert.deepEqual(write.body.input.target, { kind: config.targetKind, id: config.targetId }); assert.equal(write.body.input.purpose, 'delivery'); assert.deepEqual(write.body.input.address, config.address);
  const pending = await checkStoredReference(page, 'organization.address.propose', write);
  await context.addCookies([{ name: '__Host-ut_session', value: config.checkerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('heading', { name: 'Periksa sesi dan akses', exact: true }).waitFor();
  assert.deepEqual(await referenceEntries(page), pending);
  await context.addCookies([{ name: '__Host-ut_session', value: config.makerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden', timeout: 60000 });
  assert.equal(addressWrites.length, 1); assert.equal((await referenceEntries(page)).length, 0);
  await choose(page); await page.getByRole('button', { name: new RegExp(config.address.line1.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')) }).click();
  await page.getByRole('heading', { name: 'Menunggu pemeriksaan', exact: true }).waitFor(); assert.equal(await page.getByRole('button', { name: 'Tinjau persetujuan alamat', exact: true }).count(), 0);
  const checker = await user(config.checkerToken); observeBootstrap(checker.page); await checker.page.goto(origin.origin + path); await choose(checker.page);
  await checker.page.getByRole('button', { name: new RegExp(config.address.line1.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')) }).click();
  await checker.page.getByLabel('Alasan keputusan', { exact: true }).fill('Alamat cabang sintetis telah diperiksa oleh orang yang independen');
  await checker.page.getByRole('button', { name: 'Tinjau persetujuan alamat', exact: true }).click();
  const decision = checker.page.getByRole('region', { name: 'Rincian keputusan alamat', exact: true }); await decision.waitFor({ timeout: 60000 });
  assert.equal(await decision.getByRole('textbox').count(), 0); assert.equal(await decision.getByRole('heading', { name: 'Pastikan persetujuan alamat', exact: true }).evaluate(element => element === document.activeElement), true);
  assert.equal(await decision.getByRole('button', { name: 'Setujui alamat', exact: true }).isDisabled(), true); await decision.getByRole('checkbox').check();
  dropNext(); await decision.getByRole('button', { name: 'Setujui alamat', exact: true }).click();
  await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ timeout: 60000 }); assert.equal(addressWrites.length, 2);
  const approval = addressWrites[1]; assert.equal(approval.actor, config.checkerId); assert.equal(approval.body.decision, 'approve'); assert.equal(approval.version, '"1"');
  await checkStoredReference(checker.page, 'organization.address.approve', approval);
  await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  await checker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden', timeout: 60000 });
  assert.equal(addressWrites.length, 2); assert.equal(dropped(), 2); assert.equal((await referenceEntries(checker.page)).length, 0);
  await choose(checker.page); await checker.page.getByRole('heading', { name: 'Alamat aktif yang disetujui', exact: true }).waitFor({ timeout: 60000 });
  await checker.page.getByText(config.address.line1, { exact: true }).first().waitFor(); await checker.page.screenshot({ path: resolve(directory, 'organization-address-approved-mobile.png'), fullPage: true });
  const reader = await user(config.readerToken); observeBootstrap(reader.page); await reader.page.goto(origin.origin + path); await choose(reader.page);
  await reader.page.getByRole('heading', { name: 'Alamat aktif yang disetujui', exact: true }).waitFor();
  assert.equal(await reader.page.getByRole('button', { name: 'Ajukan alamat', exact: true }).count(), 0);
  await reader.page.getByRole('button', { name: new RegExp(config.address.line1.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')) }).click();
  await reader.page.getByRole('heading', { name: 'Disetujui', exact: true }).waitFor(); assert.equal(await reader.page.getByRole('button', { name: 'Tinjau persetujuan alamat', exact: true }).count(), 0);
  assert.equal(addressRecoveries.length, 3); assert.equal(addressRecoveries[0].actor, config.makerId); assert.equal(addressRecoveries[0].status, 409);
  for (const lookup of addressRecoveries.slice(1)) assert.equal(lookup.status, 200);
  assert.equal(addressRecoveries[1].body.key, write.key); assert.equal(addressRecoveries[2].body.key, approval.key);
  const wrongKey = await protectedQuery(checker.page, config.checkerId, '/api/v1/organizations/addresses/operations/query',
    { operation: 'organization.address.approve', key: write.key, purpose: 'operation-recovery' });
  assert.equal(wrongKey.status, 200); assert.deepEqual(wrongKey.body, { status: 'not_confirmed', result: null });
  const outsider = await user(config.outsiderToken); await outsider.page.goto(origin.origin + '/__address_scope_probe');
  const foreign = await protectedQuery(outsider.page, config.outsiderId, '/api/v1/organizations/addresses/query', { target: { kind: config.targetKind, id: config.targetId }, purpose: 'delivery' });
  assert.equal(foreign.status, 403); assert.equal(foreign.body.code, 'scope_denied'); assert.equal(foreign.body.address, undefined);
  assert.equal(addressWrites.length, 2);
  console.log('REAL_ORGANIZATION_ADDRESS_BROWSER_PASS ' + JSON.stringify({ writes: addressWrites.length, originalReplyLosses: dropped(), exactActorRecovery: true,
    namedApprovedUnit: true, explicitPostalFacts: true, noRecipientContacts: true, independentApproval: true, readOnlyLookup: true, wrongKeyUnconfirmed: true, foreignReadDenied: true,
    frozenReview: true, consent: true, offlineUnsent: true, mobile: true, desktop: true }));
}
