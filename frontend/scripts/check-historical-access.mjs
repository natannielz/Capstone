import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {mkdir, writeFile} from 'node:fs/promises';
import {resolve, join} from 'node:path';
import {fileURLToPath} from 'node:url';

// Fixture-only browser evidence. Run against an isolated, tracked frontend preview.
// UNIT_TOKO_PREVIEW_URL, UNIT_TOKO_PLAYWRIGHT_PACKAGE and UNIT_TOKO_BROWSER_CHANNEL are portable overrides.
const base = process.env.UNIT_TOKO_PREVIEW_URL || 'http://127.0.0.1:3118';
assert.ok(['127.0.0.1', 'localhost', '[::1]'].includes(new URL(base).hostname), 'Use a loopback preview');
const require = createRequire(process.env.UNIT_TOKO_PLAYWRIGHT_PACKAGE ? resolve(process.env.UNIT_TOKO_PLAYWRIGHT_PACKAGE) : import.meta.url);
const {chromium} = require('playwright');
const output = process.env.UNIT_TOKO_BROWSER_OUTPUT || fileURLToPath(new URL('../../tmp/historical-access-browser/', import.meta.url));
await mkdir(output, {recursive: true});
const browser = await chromium.launch({headless: true, channel: process.env.UNIT_TOKO_BROWSER_CHANNEL || (process.platform === 'win32' ? 'msedge' : undefined)});
const actor = '9007199254740993', recipient = '9007199254740994';
const scope = {kind: 'buyingBranch', organizationId: '10', branchId: '11', divisionId: null, shopId: null, warehouseId: null, channelId: null};
const date = '2020-01-01T00:00:00Z';
const memberships = ['10', '20'].map((id, index) => ({id: String(100 + index), scope: {kind: 'buyingOrganization', organizationId: id}, status: 'active', effectiveAt: date, expiresAt: null, version: '1', allowedActions: []}));
const roles = [['buyer', 'Pembeli'], ['store-staff', 'Staf toko'], ['store-head', 'Kepala toko'], ['courier', 'Kurir'], ['finance', 'Keuangan'], ['system-admin', 'Admin sistem']]
  .map(([id, label]) => ({id, label, version: 1, permissions: []}));
const current = {userId: actor, grantVersion: '1', status: 'active', memberships: [], grants: [], scopes: [], allowedActions: ['access.propose', 'access.approve', 'access.revoke', 'membership.propose']};
const catalog = {roles, financePacks: [], permissions: ['history.read', 'report.read'].map(id => ({id, label: id, allowedScopes: ['buyingBranch', 'buyingDivision'], requiresFreshMfa: false})), allowedActions: current.allowedActions};
const recorded = {id: '700', membershipId: '100', templateId: 'historical-read', templateVersion: 1, scope, permissions: ['history.read'], delegablePermissions: [], effectiveAt: date, expiresAt: null, status: 'active', version: '8', allowedActions: ['revoke']};
const proposal = {id: '800', kind: 'grant', subjectId: recipient, requestedBy: '77', membershipId: '100', templateId: 'historical-read', templateVersion: 1, scope,
  permissions: ['history.read'], delegablePermissions: [], effectiveAt: date, expiresAt: null, status: 'pending', version: '5', createdAt: date, reason: 'Tinjauan cabang', allowedActions: ['approve', 'reject']};
const targets = [
  {id: '11', kind: 'branch', parentId: '10', name: 'Cabang Jakarta', code: 'JKT', status: 'suspended', version: '1', scope},
  {id: '12', kind: 'division', parentId: '11', name: 'Divisi Operasional', code: 'OPS', status: 'suspended', version: '1', scope: {...scope, kind: 'buyingDivision', divisionId: '12'}},
  {id: '13', kind: 'branch', parentId: '10', name: 'Cabang Bandung', code: 'BDG', status: 'active', version: '1', scope: {...scope, branchId: '13'}},
];
const checks = [];

async function setup(width = 1365, height = 1000) {
  const context = await browser.newContext({viewport: {width, height}, locale: 'id-ID', timezoneId: 'Asia/Jakarta', serviceWorkers: 'block'});
  const page = await context.newPage(); page.setDefaultTimeout(10000);
  const state = {actor, calls: [], queries: [], errors: [], proposalMode: 'ok', directoryMode: 'ok', paginate: false, actions: ['approve', 'reject']};
  page.on('pageerror', error => state.errors.push(error.message));
  page.on('dialog', dialog => dialog.accept());
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    if (url.origin !== new URL(base).origin) return route.abort();
    if (!url.pathname.startsWith('/api/v1')) return route.continue();
    const answer = (body, status = 200) => route.fulfill({status, contentType: 'application/json', body: JSON.stringify(body), headers: {'Cache-Control': 'no-store'}});
    if (url.pathname === '/api/v1/identity/csrf') return answer({requestToken: 'fixture-csrf'});
    if (url.pathname === '/api/v1/access/current') return answer({...current, userId: state.actor});
    if (request.headers()['x-expected-actor-id'] !== state.actor) return answer({code: 'actor_changed'}, 409);
    if (url.pathname === '/api/v1/access/catalog') return answer(catalog);
    if (url.pathname === '/api/v1/access/proposals') return answer([{...proposal, allowedActions: state.actions}]);
    if (url.pathname.startsWith('/api/v1/access/subjects/')) return answer({userId: recipient, grantVersion: '1', status: 'active', memberships, grants: [recorded], allowedActions: []});
    assert.equal(request.method(), 'POST'); assert.equal(request.headers()['x-csrf-token'], 'fixture-csrf'); assert.equal(url.search, '');
    const body = request.postDataJSON();
    if (url.pathname === '/api/v1/access/historical-targets/query') {
      state.queries.push(body);
      if (state.directoryMode === 'denied') return answer({code: 'scope_not_available'}, 403);
      const items = body.membershipId === '100' ? [...targets, ...(state.paginate ? Array.from({length: 99}, (_, index) => ({...targets[2], id: String(1000 + index), name: 'Cabang tambahan ' + index, scope: {...scope, branchId: String(1000 + index)}})) : [])]
        : [{...targets[0], id: '21', parentId: '20', name: 'Cabang Organisasi Kedua', scope: {...scope, organizationId: '20', branchId: '21'}}];
      return answer({total: String(items.length), items: items.slice(body.offset, body.offset + body.limit)});
    }
    if (url.pathname.endsWith('/preview')) return answer({...body, templateId: 'historical-read', templateVersion: 1, delegablePermissions: [], existingPermissions: [], requiresIndependentApproval: true});
    if (url.pathname === '/api/v1/access/operations/lookup') return answer({state: 'committed', result: {id: '900', version: '1', status: 'pending'}});
    const call = {path: url.pathname, body, key: request.headers()['idempotency-key'], version: request.headers()['if-match']};
    state.calls.push(call); assert.ok(call.key);
    if (url.pathname === '/api/v1/access/historical-grants/proposals') {
      if (state.proposalMode === 'uncertain') return answer({code: 'commit_outcome_unknown'}, 503);
      return answer({id: '900', version: '1', status: 'pending'});
    }
    if (/\/(approve|reject|withdraw|revoke)$/.test(url.pathname)) return answer({id: '900', subjectId: recipient, version: '6', grantVersion: '2', status: 'active'});
    throw new Error('Unexpected API route: ' + url.pathname);
  });
  await page.goto(base + '/administration/access');
  await page.getByRole('heading', {name: 'Kelola akses', exact: true}).waitFor();
  await page.getByLabel('Referensi akun', {exact: true}).fill(recipient);
  await page.getByRole('button', {name: 'Tampilkan akun', exact: true}).click();
  await page.getByRole('button', {name: 'Ajukan akses baca riwayat', exact: true}).click();
  await page.getByRole('option', {name: /Cabang Jakarta/}).waitFor({state: 'attached'});
  return {page, context, state};
}

async function draft(page) {
  await page.getByRole('combobox', {name: /^Cabang yang dituju/}).selectOption('11');
  const checkbox = page.getByRole('checkbox', {name: 'Lihat riwayat sesuai lingkup', exact: true});
  await checkbox.focus(); await page.keyboard.press('Space');
  assert.equal(await checkbox.isChecked(), true, 'permission supports keyboard activation');
  await page.getByLabel('Alasan pengajuan', {exact: true}).fill('Peninjauan riwayat cabang yang ditangguhkan');
}
async function preview(page) {
  await page.getByRole('button', {name: 'Periksa pratinjau izin', exact: true}).click();
  await page.getByRole('heading', {name: 'Pratinjau dari server', exact: true}).waitFor();
}
async function run(name, check) {
  try { await check(); checks.push({name, passed: true}); console.log('PASS ' + name); }
  catch (error) { checks.push({name, passed: false, error: error.message}); console.error('FAIL ' + name + ': ' + error.message); }
}

try {
  for (const [name, width, height] of [['desktop', 1365, 1000], ['mobile', 390, 844]]) await run(name + ' explicit historical grant and keyboard flow', async () => {
    const {page, context, state} = await setup(width, height);
    try {
      assert.equal(await page.getByRole('combobox', {name: 'Peran awal', exact: true}).count(), 0);
      assert.equal(await page.getByRole('checkbox').count(), 2);
      assert.equal(await page.getByRole('checkbox').first().isChecked(), false);
      assert.equal(await page.getByRole('button', {name: 'Ajukan untuk persetujuan', exact: true}).isDisabled(), true);
      assert.equal(await page.getByLabel('Alasan pengajuan', {exact: true}).getAttribute('maxlength'), '500');
      await draft(page); await preview(page);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, 'no horizontal overflow');
      await page.screenshot({path: join(output, 'historical-access-' + name + '.png'), fullPage: true});
      await page.getByRole('button', {name: 'Ajukan untuk persetujuan', exact: true}).click();
      await page.getByRole('status').filter({hasText: 'Pengajuan akses baca riwayat disimpan'}).waitFor();
      assert.equal(await page.getByRole('heading', {name: 'Pratinjau dari server', exact: true}).count(), 0, 'successful proposal invalidates review');
      assert.equal(await page.getByRole('button', {name: 'Ajukan untuk persetujuan', exact: true}).isDisabled(), true);
      assert.equal(state.calls.length, 1);
      assert.deepEqual(state.calls[0].body.grant.permissions, ['history.read']);
      assert.equal(state.calls[0].body.grant.membershipId, '100');
      assert.equal(state.calls[0].body.grant.scope.branchId, '11');
      for (const omitted of ['templateId', 'financePacks', 'additionalPermissions', 'delegablePermissions']) assert.equal(omitted in state.calls[0].body.grant, false);
      assert.deepEqual(state.errors, []);
    } finally { await context.close(); }
  });

  await run('preview invalidates on permission, reason, scope, membership and recipient edits', async () => {
    const {page, context} = await setup();
    try {
      await draft(page); await preview(page);
      for (const edit of [
        () => page.getByRole('checkbox', {name: 'Lihat laporan sesuai lingkup', exact: true}).check(),
        () => page.getByLabel('Alasan pengajuan', {exact: true}).fill('Alasan diperbarui'),
        () => page.getByRole('combobox', {name: /^Cabang yang dituju/}).selectOption('13'),
        () => page.getByLabel('Mulai berlaku', {exact: true}).fill('2027-01-01T10:00'),
        () => page.getByLabel(/^Berakhir/).fill('2027-02-01T10:00'),
      ]) {
        await edit(); assert.equal(await page.getByRole('heading', {name: 'Pratinjau dari server', exact: true}).count(), 0);
        assert.equal(await page.getByRole('button', {name: 'Ajukan untuk persetujuan', exact: true}).isDisabled(), true); await preview(page);
      }
      await page.getByRole('combobox', {name: /^Keanggotaan organisasi pembeli aktif/}).selectOption('101');
      assert.equal(await page.getByRole('heading', {name: 'Pratinjau dari server', exact: true}).count(), 0);
      await page.getByRole('option', {name: /Cabang Organisasi Kedua/}).waitFor({state: 'attached'});
      assert.equal(await page.getByRole('option', {name: /Cabang Jakarta/}).count(), 0);
      await page.getByLabel('Referensi akun', {exact: true}).fill('88');
      assert.equal(await page.getByRole('combobox', {name: /^Keanggotaan organisasi pembeli aktif/}).count(), 0);
    } finally { await context.close(); }
  });

  await run('division scope and suspended targets stay within approved parent', async () => {
    const {page, context, state} = await setup();
    try {
      await draft(page); await page.getByRole('combobox', {name: /^Cakupan akses baca/}).selectOption('buyingDivision');
      assert.equal(await page.getByRole('button', {name: 'Periksa pratinjau izin', exact: true}).isDisabled(), true);
      await page.getByRole('combobox', {name: /^Divisi yang dituju/}).selectOption('12'); await preview(page);
      await page.getByRole('button', {name: 'Ajukan untuk persetujuan', exact: true}).click();
      await page.getByRole('status').filter({hasText: 'Pengajuan akses baca riwayat disimpan'}).waitFor();
      assert.equal(state.calls[0].body.grant.scope.kind, 'buyingDivision'); assert.equal(state.calls[0].body.grant.scope.divisionId, '12');
      assert.ok(state.queries.every(query => query.userId === recipient && query.membershipId === '100'));
    } finally { await context.close(); }
  });

  await run('scoped target directory retrieves every page before enabling selection', async () => {
    const {page, context, state} = await setup();
    try {
      state.paginate = true; state.queries = [];
      await page.getByRole('button', {name: 'Muat ulang', exact: true}).click();
      await page.getByRole('option', {name: /Cabang tambahan 98/}).waitFor({state: 'attached'});
      assert.deepEqual([...new Set(state.queries.map(query => query.offset))], [0, 100]);
      assert.ok(state.queries.every(query => query.limit === 100 && query.userId === recipient && query.membershipId === '100'));
      await page.getByRole('combobox', {name: /^Cabang yang dituju/}).selectOption('1098');
      await page.getByRole('checkbox', {name: 'Lihat laporan sesuai lingkup', exact: true}).check();
      await page.getByLabel('Alasan pengajuan', {exact: true}).fill('Laporan cabang pada halaman berikutnya');
      await preview(page);
    } finally { await context.close(); }
  });

  await run('actor reset clears recipient, review and historical draft', async () => {
    const {page, context, state} = await setup();
    try {
      await draft(page); await preview(page); state.actor = '9007199254740999';
      await page.getByRole('button', {name: 'Muat ulang', exact: true}).click();
      await page.getByLabel('Referensi akun', {exact: true}).waitFor();
      assert.equal(await page.getByLabel('Referensi akun', {exact: true}).inputValue(), '');
      assert.equal(await page.getByRole('heading', {name: 'Pratinjau dari server', exact: true}).count(), 0);
      assert.equal(await page.getByRole('checkbox').count(), 0); assert.equal(state.calls.length, 0);
    } finally { await context.close(); }
  });

  await run('uncertain proposal retries immutable reviewed body with same key and body-free storage', async () => {
    const {page, context, state} = await setup();
    try {
      await draft(page); await preview(page); state.proposalMode = 'uncertain';
      await page.getByRole('button', {name: 'Ajukan untuk persetujuan', exact: true}).click();
      const retry = page.getByRole('button', {name: 'Periksa kembali permintaan yang sama', exact: true}); await retry.waitFor();
      assert.equal(await page.getByLabel('Alasan pengajuan', {exact: true}).isDisabled(), true);
      const stored = await page.evaluate(() => JSON.stringify({...localStorage}));
      assert.match(stored, /access.grant.propose/); assert.doesNotMatch(stored, /history.read|membershipId|Peninjauan riwayat/);
      state.proposalMode = 'ok'; await retry.click();
      await page.getByRole('status').filter({hasText: 'Pengajuan akses baca riwayat disimpan'}).waitFor();
      assert.equal(state.calls.length, 2); assert.equal(state.calls[0].key, state.calls[1].key); assert.deepEqual(state.calls[0].body, state.calls[1].body);
    } finally { await context.close(); }
  });

  await run('reload recovery resolves original receipt without retaining or resending historical draft', async () => {
    const {page, context, state} = await setup();
    try {
      await draft(page); await preview(page); state.proposalMode = 'uncertain';
      await page.getByRole('button', {name: 'Ajukan untuk persetujuan', exact: true}).click();
      await page.getByRole('button', {name: 'Periksa kembali permintaan yang sama', exact: true}).waitFor();
      await page.reload(); await page.getByRole('heading', {name: 'Pemulihan permintaan', exact: true}).waitFor();
      assert.equal(await page.getByRole('heading', {name: 'Pratinjau dari server', exact: true}).count(), 0);
      assert.equal(await page.getByLabel('Referensi akun', {exact: true}).inputValue(), '');
      assert.equal(await page.getByRole('button', {name: 'Ajukan akses baca riwayat', exact: true}).isDisabled(), true);
      await page.getByRole('button', {name: 'Periksa hasil', exact: true}).click();
      await page.getByRole('status').filter({hasText: 'Permintaan telah tersimpan'}).waitFor();
      await page.getByLabel('Referensi akun', {exact: true}).fill(recipient);
      await page.getByRole('button', {name: 'Tampilkan akun', exact: true}).click();
      await page.getByRole('button', {name: 'Ajukan akses baca riwayat', exact: true}).click();
      await page.getByRole('option', {name: /Cabang Jakarta/}).waitFor({state: 'attached'});
      assert.equal(await page.getByRole('heading', {name: 'Pratinjau dari server', exact: true}).count(), 0);
      assert.equal(await page.getByRole('button', {name: 'Ajukan untuk persetujuan', exact: true}).isDisabled(), true);
      assert.equal(await page.getByRole('checkbox').first().isChecked(), false);
      assert.equal(state.calls.length, 1, 'receipt recovery must not resubmit proposal');
      assert.equal(await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:')).length), 0);
    } finally { await context.close(); }
  });

  await run('directory denial fails closed and ordinary six-role flow remains available', async () => {
    const {page, context, state} = await setup();
    try {
      state.directoryMode = 'denied'; await page.getByRole('button', {name: 'Muat ulang', exact: true}).click();
      await page.getByRole('alert').filter({hasText: 'Daftar unit belum dapat dimuat'}).waitFor();
      assert.equal(await page.getByRole('combobox', {name: /^Cabang yang dituju/}).isDisabled(), true);
      assert.equal(await page.getByRole('button', {name: 'Periksa pratinjau izin', exact: true}).isDisabled(), true);
      await page.getByRole('button', {name: 'Ajukan izin', exact: true}).click();
      const role = page.getByRole('combobox', {name: 'Peran awal', exact: true});
      assert.equal(await role.locator('option').count(), 6); assert.equal(await role.locator('option[value="historical-read"]').count(), 0);
      const extras = page.getByText('Izin tambahan yang diminta', {exact: true}).locator('..');
      const delegation = page.locator('details').filter({has: page.locator('summary').filter({hasText: /^Wewenang pemberian izin$/})});
      await extras.locator('summary').click(); await extras.getByRole('checkbox', {name: 'Lihat riwayat sesuai lingkup', exact: true}).check();
      await delegation.locator('summary').click(); await delegation.getByRole('checkbox', {name: 'Lihat laporan sesuai lingkup', exact: true}).check();
      await page.getByRole('button', {name: 'Ajukan akses baca riwayat', exact: true}).click();
      await page.getByRole('combobox', {name: /^Keanggotaan organisasi pembeli aktif/}).selectOption('101');
      await page.getByRole('button', {name: 'Ajukan izin', exact: true}).click();
      await extras.locator('summary').click(); await delegation.locator('summary').click();
      assert.equal(await extras.getByRole('checkbox', {name: 'Lihat riwayat sesuai lingkup', exact: true}).isChecked(), false);
      assert.equal(await delegation.getByRole('checkbox', {name: 'Lihat laporan sesuai lingkup', exact: true}).isChecked(), false);
    } finally { await context.close(); }
  });

  await run('special-grant review retains server allowed actions and versioned decisions', async () => {
    const {page, context, state} = await setup();
    try {
      await page.getByRole('button', {name: /^Akses baca riwayat untuk akun/}).click();
      assert.equal(await page.getByText('Profil izin', {exact: true}).count(), 1);
      assert.equal(await page.getByRole('button', {name: 'Tarik pengajuan saya', exact: true}).count(), 0);
      await page.getByRole('button', {name: 'Tinjau dan setujui', exact: true}).click();
      const reason = page.getByLabel('Alasan keputusan', {exact: true});
      assert.equal(await reason.evaluate(element => document.activeElement === element), true);
      await reason.fill('Ditinjau petugas independen'); await page.getByRole('button', {name: 'Simpan keputusan', exact: true}).click();
      await page.getByRole('status').filter({hasText: 'Perubahan akses telah disimpan'}).waitFor();
      assert.equal(state.calls[0].path, '/api/v1/access/grants/proposals/800/approve'); assert.equal(state.calls[0].version, '"5"');
      await page.getByRole('button', {name: 'Cabut izin', exact: true}).click();
      await page.getByRole('heading', {name: 'Cabut akses baca riwayat 700', exact: true}).waitFor();
      await reason.fill('Peninjauan telah selesai'); await page.getByRole('button', {name: 'Simpan keputusan', exact: true}).click();
      await page.getByRole('status').filter({hasText: 'Perubahan akses telah disimpan'}).waitFor();
      assert.equal(state.calls[1].path, '/api/v1/access/grants/700/revoke'); assert.equal(state.calls[1].version, '"8"');
    } finally { await context.close(); }
  });

  for (const [action, label] of [['reject', 'Tolak pengajuan'], ['withdraw', 'Tarik pengajuan saya']]) await run('historical ' + action + ' action follows server permission', async () => {
    const {page, context, state} = await setup();
    try {
      state.actions = [action]; await page.getByRole('button', {name: 'Muat ulang', exact: true}).click();
      await page.getByRole('button', {name: /^Akses baca riwayat untuk akun/}).click();
      assert.equal(await page.getByRole('button', {name: 'Tinjau dan setujui', exact: true}).count(), 0);
      await page.getByRole('button', {name: label, exact: true}).click();
      assert.ok(await page.getByText('Akses baca riwayat', {exact: true}).count());
      await page.getByLabel('Alasan keputusan', {exact: true}).fill('Keputusan setelah peninjauan');
      await page.getByRole('button', {name: 'Simpan keputusan', exact: true}).click();
      await page.getByRole('status').filter({hasText: 'Perubahan akses telah disimpan'}).waitFor();
      assert.equal(state.calls[0].path, '/api/v1/access/proposals/800/' + action); assert.equal(state.calls[0].version, '"5"');
    } finally { await context.close(); }
  });
} finally {
  await browser.close(); await writeFile(join(output, 'results.json'), JSON.stringify({fixtureOnly: true, checks}, null, 2));
}
if (checks.some(check => !check.passed)) process.exitCode = 1;
