// Browser acceptance for the isolated persistent synthetic pilot. Private credentials are read only in memory.
// Run beforeRestart, restart the same pilot host, then run afterRestart with the saved non-secret receipt.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { resolve, dirname } from 'node:path';

const chunks = []; for await (const chunk of process.stdin) chunks.push(chunk);
const config = JSON.parse(Buffer.concat(chunks).toString('utf8'));
assert.ok(['beforeRestart', 'afterRestart'].includes(config.stage));
const settings = JSON.parse(readFileSync(resolve(config.settingsPath), 'utf8'));
const launcher = readFileSync(resolve(config.browserLauncherPath), 'utf8');
const pin = /--ignore-certificate-errors-spki-list=([A-Za-z0-9+/=]+)/u.exec(launcher)?.[1]; assert.ok(pin, 'Pinned pilot certificate must be available');
const origin = 'https://portal.unit-toko-pilot.home.arpa', provider = 'https://login.unit-toko-pilot.home.arpa';
const load = createRequire(config.playwrightPackage), metadata = load(config.playwrightPackage); assert.equal(metadata.name, 'playwright');
const { chromium } = load('playwright'), channel = config.channel ?? 'msedge'; assert.ok(['msedge', 'chromium'].includes(channel));
const output = resolve(config.receiptPath), directory = resolve(config.artifactDirectory); mkdirSync(directory, { recursive: true });
const browser = await chromium.launch({ channel, headless: true, args: [
  '--host-resolver-rules=MAP portal.unit-toko-pilot.home.arpa 127.0.0.1, MAP login.unit-toko-pilot.home.arpa 127.0.0.1',
  '--ignore-certificate-errors-spki-list=' + pin, '--no-proxy-server', '--no-first-run', '--disable-sync',
] });
const errors = [], contexts = [], diagnostics = new Map();
const progress = { stage: config.stage, submittedRequests: [], decisions: [] };
function saveProgress() { writeFileSync(resolve(directory, 'pilot-progress.json'), JSON.stringify(progress, null, 2)); }
const privateValues = [settings.ClientSecret, settings.CertificatePassword, ...settings.Accounts.flatMap(account => [account.Password, account.FactorCode])].filter(Boolean);
function safeText(value) {
  let text = String(value);
  for (const secret of privateValues) text = text.replaceAll(secret, '[redacted]');
  return text.replace(/https?:\/\/[^\s<>"']+/gu, value => {
    try { const url = new URL(value); return url.origin + url.pathname; } catch { return '[url]'; }
  });
}
async function captureFailure(page, name) {
  if (page.isClosed()) return;
  const controls = await page.locator('input, textarea, select, button').evaluateAll(elements => elements.map(element => ({ tag: element.tagName.toLowerCase(), type: element.getAttribute('type'), ariaLabel: element.getAttribute('aria-label'),
    label: Array.from(element.labels ?? []).map(label => Array.from(label.childNodes).filter(node => node.nodeType === Node.TEXT_NODE).map(node => node.textContent).join('').trim()).join(' / '),
    button: element.tagName === 'BUTTON' ? element.textContent?.trim() : null, disabled: element.disabled }))).catch(() => []);
  const value = { location: safeText(page.url()), body: safeText(await page.locator('body').innerText().catch(() => 'Page body unavailable')), controls: JSON.parse(safeText(JSON.stringify(controls))), events: diagnostics.get(page) ?? [] };
  writeFileSync(resolve(directory, name + '.json'), JSON.stringify(value, null, 2));
  await page.screenshot({ path: resolve(directory, name + '.png'), fullPage: true, mask: [page.locator('input, textarea')] }).catch(() => {});
}
async function signedOutStates(page) {
  let releaseSession;
  const paused = new Promise(resolve => { releaseSession = resolve; });
  const sessionPath = origin + '/api/v1/identity/session';
  const held = new Set();
  const hold = route => {
    const operation = paused.then(() => route.continue()); held.add(operation);
    return operation.finally(() => held.delete(operation));
  };
  // A visitor without any session cookie never sees the page shell: the proxy sends them to sign-in with the page as return target (G-012).
  await page.goto(origin + '/ordering/requests'); await page.waitForURL(url => url.pathname === '/identity/login' && url.searchParams.get('returnPath') === '/ordering/requests');
  // An expired session (a stale cookie the server no longer knows) still meets the page's own sign-in state.
  await page.context().addCookies([{ name: '__Host-ut_session', value: 'expiredSyntheticSessionToken0000000000000000', domain: new URL(origin).hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.route(sessionPath, hold);
  try {
    await page.goto(origin + '/ordering/requests', { waitUntil: 'domcontentloaded' });
    await page.getByRole('status').filter({ hasText: 'Memeriksa akun dan akses…' }).waitFor();
    await page.screenshot({ path: resolve(directory, 'pilot-request-loading-mobile.png'), fullPage: true });
  } finally { releaseSession(); await Promise.all(held); await page.unroute(sessionPath, hold); }
  await page.getByRole('heading', { name: /^(Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor();
  await page.getByRole('link', { name: 'Masuk kembali', exact: true }).waitFor();
  assert.equal(await page.getByRole('button', { name: 'Konfirmasi ajukan permintaan', exact: true }).count(), 0);
  await cleanViewport(page); await page.screenshot({ path: resolve(directory, 'pilot-request-unauthenticated-mobile.png'), fullPage: true });
}
async function user(id, mobile = false) {
  const account = settings.Accounts.find(item => String(item.Id) === id); assert.ok(account, 'Synthetic pilot account is provisioned');
  const context = await browser.newContext({ viewport: mobile ? { width: 390, height: 844 } : { width: 1365, height: 920 }, locale: 'id-ID', timezoneId: 'Asia/Jakarta' }); contexts.push(context);
  const page = await context.newPage(); page.setDefaultTimeout(20000); page.on('pageerror', () => errors.push('Unexpected browser page error'));
  const events = []; diagnostics.set(page, events);
  const record = value => { events.push(value); if (events.length > 60) events.shift(); };
  page.on('console', message => { if (['warning', 'error'].includes(message.type())) record({ kind: message.type(), message: safeText(message.text()) }); });
  page.on('response', response => record({ kind: 'response', status: response.status(), location: safeText(response.url()) }));
  page.on('requestfailed', request => record({ kind: 'requestfailed', location: safeText(request.url()), failure: safeText(request.failure()?.errorText ?? '') }));
  page.on('request', async request => {
    if (request.url() !== provider + '/local-login' || request.method() !== 'POST') return;
    const headers = await request.allHeaders(), form = new URLSearchParams(request.postData() ?? '');
    const cookie = (headers.cookie ?? '').split(';').map(value => value.trim()).find(value => value.startsWith('__Host-ut_pilot_login='))?.slice('__Host-ut_pilot_login='.length);
    record({ kind: 'login-request-checks', origin: headers.origin ?? null, contentType: headers['content-type'] ?? null, contentLength: headers['content-length'] ?? null,
      transactionPresent: Boolean(form.get('transaction')), csrfPresent: Boolean(form.get('csrf')), cookiePresent: Boolean(cookie), csrfMatchesCookie: Boolean(cookie && cookie === form.get('csrf')) });
  });
  try {
  if (id === '1001' && config.stage === 'beforeRestart') await signedOutStates(page);
  await page.goto(origin + '/identity/login?returnPath=%2Fidentity%2Faccount'); await page.getByRole('button', { name: 'Lanjutkan ke layanan akun', exact: true }).click();
  await page.waitForURL(url => url.origin === provider);
  await page.getByLabel('Nama masuk', { exact: true }).fill(account.Username);
  await page.getByLabel('Kata sandi lokal', { exact: true }).fill(account.Password);
  await page.getByLabel('Kode faktor SIMULASI', { exact: true }).fill(account.FactorCode);
  await page.getByRole('button', { name: 'Masuk', exact: true }).click();
  await page.waitForURL(url => url.origin === origin && url.pathname === '/identity/account');
  await page.getByRole('heading', { name: 'Akun saya', exact: true }).waitFor();
  const session = await page.evaluate(async () => { const response = await fetch('/api/v1/identity/session', { cache: 'no-store' }); return { status: response.status, value: await response.json() }; });
  assert.equal(session.status, 200); assert.equal(session.value.authenticated, true); assert.equal(session.value.account.userId, id); assert.ok(session.value.factorCompletedAt);
  const access = await page.evaluate(async () => { const response = await fetch('/api/v1/access/current', { cache: 'no-store' }); const value = await response.json(); return { status: response.status, actorId: value.userId, actions: value.allowedActions }; });
  assert.equal(access.status, 200); assert.equal(access.actorId, id);
  for (const action of id === '2001' ? ['order.request.read', 'order.request.decide', 'catalog.manage'] : ['order.request.read', 'order.request.submit']) assert.ok(access.actions.includes(action), 'Actual current access must expose ' + action);
  const cookie = (await context.cookies(origin)).find(value => value.name === '__Host-ut_session'); assert.ok(cookie?.httpOnly && cookie.secure);
  return { context, page, id };
  } catch (error) { await captureFailure(page, 'pilot-login-failure-' + id); throw error; }
}
async function read(user, path, body, expectedActor = user.id) {
  assert.ok(path.startsWith('/api/v1/'));
  return user.page.evaluate(async ({ path, body, expectedActor }) => {
    const csrf = await fetch('/api/v1/identity/csrf', { cache: 'no-store' }); const token = await csrf.json();
    const response = await fetch(path, { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': token.requestToken, 'X-Expected-Actor-Id': expectedActor }, body: JSON.stringify(body) });
    return { status: response.status, body: await response.json() };
  }, { path, body, expectedActor });
}
async function cleanViewport(page) {
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), true, 'Page must not overflow the viewport');
}
async function emptyHistory(outsider) {
  await outsider.page.goto(origin + '/ordering/requests');
  await outsider.page.getByRole('button', { name: 'Riwayat permintaan', exact: true }).click();
  const history = outsider.page.getByRole('region', { name: 'Daftar permintaan saya', exact: true });
  await history.getByText('Belum ada permintaan pada halaman ini.', { exact: true }).waitFor();
  const actual = await read(outsider, '/api/v1/ordering/requests/query', { purpose: 'branch-request-history', view: 'mine', beforeRequestId: null });
  assert.equal(actual.status, 200); assert.deepEqual(actual.body.items, []);
  assert.equal(await history.getByRole('button', { name: /^Lihat PT-/u }).count(), 0);
  await cleanViewport(outsider.page); await outsider.page.screenshot({ path: resolve(directory, 'pilot-other-branch-empty-history.png'), fullPage: true });
}
async function decision(staff, request, action) {
  await staff.page.goto(origin + '/ordering/requests/incoming');
  await staff.page.getByRole('button', { name: 'Lihat ' + request.number, exact: true }).click();
  const detail = staff.page.getByRole('region', { name: 'Rincian permintaan' });
  await detail.getByLabel('Catatan peninjauan', { exact: true }).fill(action === 'accept' ? 'SINTETIS: kebutuhan diterima untuk tindak lanjut pilot.' : 'SINTETIS: permintaan ditolak untuk pengujian riwayat pilot.');
  await detail.getByRole('button', { name: action === 'accept' ? 'Terima permintaan' : 'Tolak permintaan', exact: true }).click();
  await detail.getByRole('button', { name: action === 'accept' ? 'Konfirmasi terima permintaan' : 'Konfirmasi tolak permintaan', exact: true }).click();
  const status = staff.page.getByRole('region', { name: 'Hasil permintaan' }).getByRole('status'); await status.waitFor();
  assert.equal(await status.textContent(), action === 'accept' ? 'Diterima untuk diproses' : 'Ditolak toko');
  progress.decisions.push({ requestId: request.id, number: request.number, action }); saveProgress();
}
async function addPackage(page, code, amount, validate = false) {
  await page.getByLabel('Cari nama atau kode kemasan', { exact: true }).fill(code);
  await page.getByRole('button', { name: 'Cari kemasan', exact: true }).click();
  await page.getByRole('button', { name: 'Pilih ' + code, exact: true }).click();
  if (validate) {
    await page.getByLabel('Jumlah kemasan', { exact: true }).fill('0');
    await page.getByRole('button', { name: 'Tambahkan kemasan', exact: true }).click();
    await page.getByRole('alert').filter({ hasText: 'Isi jumlah sesuai minimum dan kelipatan kemasan.' }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Tinjau permintaan', exact: true }).isDisabled(), true);
    await cleanViewport(page); await page.screenshot({ path: resolve(directory, 'pilot-request-quantity-validation-mobile.png'), fullPage: true });
  }
  await page.getByLabel('Jumlah kemasan', { exact: true }).fill(String(amount));
  await page.getByRole('button', { name: 'Tambahkan kemasan', exact: true }).click();
}
async function createRequest(requester, contextKey, index, dropResponse = false) {
  await requester.page.goto(origin + '/ordering/requests');
  await requester.page.getByRole('combobox', { name: /^Cabang pemesan/u }).selectOption(contextKey);
  await addPackage(requester.page, 'SYN-PENCIL-12', 2, index === 0);
  if (index === 0) await addPackage(requester.page, 'SYN-NOTEBOOK-6', 1);
  await requester.page.getByLabel('Catatan kebutuhan (opsional)', { exact: true }).fill('SINTETIS: permintaan pilot ' + (index + 1));
  await requester.page.getByRole('button', { name: 'Tinjau permintaan', exact: true }).click();
  const review = requester.page.getByRole('region', { name: 'Konfirmasi permintaan' }); await review.getByRole('heading', { name: '3. Konfirmasi sebelum mengajukan' }).waitFor();
  assert.match(await review.textContent(), /Toko pilot SINTETIS/u); assert.match(await review.textContent(), /12/u);
  assert.equal(await review.getByText(/Rp|IDR|Subtotal|Pajak/u).count(), 0);
  let committed, writes = 0, dropped = 0;
  const cdp = await requester.context.newCDPSession(requester.page);
  await cdp.send('Fetch.enable', { patterns: [{ urlPattern: origin + '/api/v1/ordering/requests', requestStage: 'Response' }] });
  cdp.on('Fetch.requestPaused', async event => {
    try {
      if (event.request.method === 'POST' && event.responseStatusCode === 200) {
        writes++; const response = await cdp.send('Fetch.getResponseBody', { requestId: event.requestId });
        committed = JSON.parse(response.base64Encoded ? Buffer.from(response.body, 'base64').toString('utf8') : response.body);
        progress.submittedRequests.push({ id: committed.requestId, number: committed.number, state: committed.state }); saveProgress();
        if (dropResponse && dropped === 0) { dropped++; await cdp.send('Fetch.failRequest', { requestId: event.requestId, errorReason: 'Aborted' }); return; }
      }
      await cdp.send('Fetch.continueRequest', { requestId: event.requestId });
    } catch { errors.push('Request response interception failed'); }
  });
  await review.getByRole('button', { name: 'Konfirmasi ajukan permintaan', exact: true }).click();
  if (dropResponse) {
    await requester.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
    await requester.page.screenshot({ path: resolve(directory, 'pilot-request-response-lost-mobile.png'), fullPage: true });
    const pending = await requester.page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage.getItem(key))));
    assert.equal(pending.length, 1); assert.equal(pending[0].actorId, requester.id); assert.equal(pending[0].operation, 'ordering.branchrequest.submit');
    assert.deepEqual(Object.keys(pending[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    await requester.page.reload(); await requester.page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  }
  const status = requester.page.getByRole('region', { name: 'Hasil permintaan' }).getByRole('status'); await status.waitFor(); assert.equal(await status.textContent(), 'Menunggu peninjauan');
  assert.equal(writes, 1); assert.equal(dropped, dropResponse ? 1 : 0); assert.match(committed.number, /^PT-[0-9]{8,}$/u);
  await cdp.send('Fetch.disable'); await cdp.detach();
  assert.equal(await requester.page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).length), 0);
  return { id: committed.requestId, number: committed.number };
}
try {
  const requester = await user('1001', true), staff = await user('2001'), outsider = await user('1002');
  await emptyHistory(outsider);
  if (config.stage === 'beforeRestart') {
    await requester.page.goto(origin + '/shop');
    await requester.page.getByRole('button', { name: /Rincian SYN-PENCIL-12/u }).waitFor();
    assert.equal(await requester.page.getByText('Foto belum tersedia', { exact: true }).count(), 3); await cleanViewport(requester.page);
    await requester.page.screenshot({ path: resolve(directory, 'pilot-catalog-mobile.png'), fullPage: true });
    await requester.page.getByRole('button', { name: /Rincian SYN-PENCIL-12/u }).click();
    await requester.page.getByRole('dialog').getByRole('link', { name: 'Pilih untuk permintaan', exact: true }).click();
    await requester.page.getByRole('combobox', { name: /^Cabang pemesan/u }).waitFor();
    const contexts = await read(requester, '/api/v1/ordering/requests/contexts/query', { purpose: 'branch-request-review' });
    assert.equal(contexts.status, 200); assert.equal(contexts.body.items.length, 1); const selected = contexts.body.items[0]; assert.equal(selected.context.branchId, '11'); assert.match(selected.branchName, /SINTETIS/u);
    const contextKey = [selected.context.organizationId, selected.context.branchId, selected.context.divisionId ?? ''].join(':');
    let first, second;
    if (config.resumeProgressPath) {
      const saved = JSON.parse(readFileSync(resolve(config.resumeProgressPath), 'utf8'));
      assert.equal(saved.stage, 'beforeRestart'); assert.equal(saved.submittedRequests.length, 2);
      [first, second] = saved.submittedRequests;
      assert.match(first.id, /^[1-9][0-9]*$/u); assert.match(second.id, /^[1-9][0-9]*$/u); assert.notEqual(first.id, second.id);
      assert.deepEqual(saved.decisions, [{ requestId: first.id, number: first.number, action: 'accept' }, { requestId: second.id, number: second.number, action: 'reject' }]);
      progress.submittedRequests = saved.submittedRequests; progress.decisions = saved.decisions; saveProgress();
    } else {
      first = await createRequest(requester, contextKey, 0, true); second = await createRequest(requester, contextKey, 1);
      await decision(staff, first, 'accept'); await decision(staff, second, 'reject');
      await cleanViewport(staff.page); await staff.page.screenshot({ path: resolve(directory, 'pilot-incoming-reviewed-desktop.png'), fullPage: true });
    }
    const own = await read(requester, '/api/v1/ordering/requests/' + first.id + '/query', { purpose: 'branch-request-history' });
    assert.equal(own.status, 200); assert.equal(own.body.state, 'accepted'); assert.equal(own.body.lines.length, 2); assert.equal(own.body.lines[0].baseUnitsPerPackage, 12);
    const rejected = await read(requester, '/api/v1/ordering/requests/' + second.id + '/query', { purpose: 'branch-request-history' }); assert.equal(rejected.status, 200); assert.equal(rejected.body.state, 'rejected');
    const foreign = await read(outsider, '/api/v1/ordering/requests/' + first.id + '/query', { purpose: 'branch-request-history' }); assert.equal(foreign.status, 403);
    const actorChanged = await read(requester, '/api/v1/ordering/requests/' + first.id + '/query', { purpose: 'branch-request-history' }, '1002'); assert.equal(actorChanged.status, 409);
    const forbidden = await requester.page.evaluate(async () => (await fetch('/api/v1/billing/invoices/query', { cache: 'no-store' })).status); assert.equal(forbidden, 404);
    await requester.page.goto(origin + '/ordering/requests'); await requester.page.getByRole('button', { name: 'Riwayat permintaan', exact: true }).click();
    await requester.page.getByRole('button', { name: 'Lihat ' + first.number, exact: true }).click(); await requester.page.getByRole('region', { name: 'Rincian permintaan' }).getByText('Diterima untuk diproses', { exact: true }).waitFor();
    await cleanViewport(requester.page); await requester.page.screenshot({ path: resolve(directory, 'pilot-request-history-mobile.png'), fullPage: true });
    const ownersResponse = staff.page.waitForResponse(response => response.url() === origin + '/api/v1/catalog/administration/owners/query' && response.status() === 200);
    await staff.page.goto(origin + '/administration/catalog'); await ownersResponse;
    const ownerSelect = staff.page.getByRole('combobox', { name: 'Toko atau gudang pemilik katalog', exact: true });
    await ownerSelect.locator('option[value]:not([value=""])').first().waitFor({ state: 'attached' });
    const ownerOptions = await ownerSelect.locator('option').evaluateAll(options => options.map(option => ({ value: option.value, text: option.textContent })).filter(option => option.value));
    const selectedOwner = ownerOptions.find(option => { const scope = JSON.parse(option.value); return ['kind', 'organizationId', 'shopId', 'warehouseId', 'channelId'].every(key => scope[key] === own.body.owner[key]); });
    assert.ok(selectedOwner, 'Choose the real fulfillment owner of the saved request'); await ownerSelect.selectOption(selectedOwner.value);
    await staff.page.getByRole('button', { name: /Pensil contoh SINTETIS/u }).click(); await staff.page.getByRole('button', { name: 'Ubah kelompok', exact: true }).click();
    const description = 'SINTETIS: uraian kemasan diperiksa pada pilot persisten.';
    await staff.page.getByRole('textbox', { name: /^Deskripsi produk/u }).fill(description);
    await staff.page.getByRole('textbox', { name: /^Alasan perubahan/u }).fill('SINTETIS: pemeriksaan pengelolaan katalog pilot.');
    await staff.page.getByRole('button', { name: 'Periksa sebelum menyimpan', exact: true }).click(); await staff.page.getByRole('button', { name: 'Konfirmasi dan simpan', exact: true }).click();
    await staff.page.getByText('Perubahan tersimpan. Data terbaru dimuat untuk diperiksa sebelum perubahan berikutnya.', { exact: true }).waitFor();
    await staff.page.getByText(description, { exact: true }).waitFor();
    await cleanViewport(staff.page); await staff.page.screenshot({ path: resolve(directory, 'pilot-catalog-maintenance-desktop.png'), fullPage: true });
    await requester.page.goto(origin + '/shop'); await requester.page.getByRole('button', { name: /Rincian SYN-PENCIL-12/u }).click(); await requester.page.getByRole('dialog').getByText(description, { exact: true }).waitFor();
    mkdirSync(dirname(output), { recursive: true }); writeFileSync(output, JSON.stringify({ version: 1, requests: [{ ...first, state: 'accepted' }, { ...second, state: 'rejected' }], catalogDescription: description }, null, 2));
    assert.deepEqual(errors, []); console.log('REAL_PERSISTENT_PILOT_BROWSER_BEFORE_RESTART_PASS');
  } else {
    const receipt = JSON.parse(readFileSync(output, 'utf8')); assert.equal(receipt.version, 1); assert.equal(receipt.requests.length, 2);
    await requester.page.goto(origin + '/ordering/requests'); await requester.page.getByRole('button', { name: 'Riwayat permintaan', exact: true }).click();
    for (const item of receipt.requests) {
      await requester.page.getByRole('button', { name: 'Lihat ' + item.number, exact: true }).click();
      await requester.page.getByRole('region', { name: 'Rincian permintaan' }).getByText(item.state === 'accepted' ? 'Diterima untuk diproses' : 'Ditolak toko', { exact: true }).waitFor();
      const saved = await read(requester, '/api/v1/ordering/requests/' + item.id + '/query', { purpose: 'branch-request-history' }); assert.equal(saved.status, 200); assert.equal(saved.body.state, item.state);
      const denied = await read(outsider, '/api/v1/ordering/requests/' + item.id + '/query', { purpose: 'branch-request-history' }); assert.equal(denied.status, 403);
      await cleanViewport(requester.page); await requester.page.screenshot({ path: resolve(directory, 'pilot-persisted-' + item.state + '-mobile.png'), fullPage: true });
    }
    await staff.page.goto(origin + '/ordering/requests/incoming'); for (const item of receipt.requests) await staff.page.getByRole('button', { name: 'Lihat ' + item.number, exact: true }).waitFor();
    await cleanViewport(staff.page); await staff.page.screenshot({ path: resolve(directory, 'pilot-persisted-incoming-desktop.png'), fullPage: true });
    await requester.page.goto(origin + '/shop'); await requester.page.getByRole('button', { name: /Rincian SYN-PENCIL-12/u }).click(); await requester.page.getByRole('dialog').getByText(receipt.catalogDescription, { exact: true }).waitFor();
    await requester.page.screenshot({ path: resolve(directory, 'pilot-persisted-catalog-description-mobile.png'), fullPage: true });
    assert.deepEqual(errors, []); console.log('REAL_PERSISTENT_PILOT_BROWSER_AFTER_RESTART_PASS');
  }
} catch (error) {
  let index = 0; for (const context of contexts) for (const page of context.pages()) await captureFailure(page, 'pilot-journey-failure-' + (++index));
  throw new Error(safeText(error instanceof Error ? error.stack ?? error.message : error));
} finally { for (const context of contexts) await context.close(); await browser.close(); }
