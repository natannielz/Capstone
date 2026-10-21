// Actual production Next/HTTPS/API/Mongo. Contacts, provider proofs and policy values are local synthetic inputs.
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import { createRequire } from 'node:module';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { createHash } from 'node:crypto';
import next from 'next';

const input = []; for await (const chunk of process.stdin) input.push(chunk);
const config = JSON.parse(Buffer.concat(input).toString('utf8')), api = new URL(config.apiAddress), origin = new URL(config.origin);
assert.equal(process.versions.node.split('.')[0], '24'); assert.equal(api.protocol, 'http:'); assert.equal(api.hostname, '127.0.0.1');
assert.equal(api.pathname, '/'); assert.ok(Number(api.port) > 0); assert.equal(origin.href, 'https://portal.unit-toko.co.id/');
assert.match(config.proofPrefix, /^commercial-browser-[a-f0-9]{32}$/); assert.notEqual(config.actorIds.maker, config.actorIds.checker);
const load = createRequire(config.playwrightPackage), metadata = load(config.playwrightPackage); assert.equal(metadata.name, 'playwright'); assert.equal(metadata.version, '1.62.1');
const { chromium } = load('playwright'), channel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'chromium'; assert.ok(['chromium', 'msedge'].includes(channel));
const app = next({ dev: false, dir: process.cwd(), quiet: true }), route = '/ordering/commercial-policies', base = '/api/v1/ordering/commercial-policies', purpose = 'commercial-policy-review';
const directory = resolve('../tmp/production/commercial-policy-browser-results'), sockets = new Set(), pages = [], errors = [], writes = [], recoveries = [], captures = [], checks = [], diagnostics = [];
const fault = { armed: false, dropped: 0 }, responses = []; let browser, frontend, edge, tunnel, maker, checker;
let held = false, releaseHold; const hold = new Promise(resolve => { releaseHold = resolve; });
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
function track(server) { server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); socket.on('error', () => socket.destroy()); }); return server; }
async function listen(server) { await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); }); return server.address().port; }
function forward(req, res, port, isApi) {
  const headers = { ...req.headers, host: '127.0.0.1:' + port };
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded', 'connection', 'x-fixture-control'].includes(name)) delete headers[name];
  if (isApi) { headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https'; }
  const mutation = isApi && req.method === 'POST' && (req.url === base + '/proposals' || new RegExp('^' + base + '/proposals/[1-9][0-9]*/decisions$').test(req.url));
  const recovery = isApi && req.method === 'POST' && req.url === base + '/operations/query'; let record;
  if (mutation || recovery) {
    record = { path: req.url, actor: req.headers['x-expected-actor-id'], key: req.headers['idempotency-key'], version: req.headers['if-match'], status: null, body: null, response: null };
    (mutation ? writes : recoveries).push(record); const chunks = []; let size = 0;
    req.on('data', chunk => { size += chunk.length; if (size <= 131072) chunks.push(chunk); }); req.once('end', () => { assert.ok(size <= 131072); record.body = JSON.parse(Buffer.concat(chunks).toString('utf8')); });
  }
  const drop = mutation && fault.armed; if (drop) fault.armed = false;
  const delay = isApi && !held && req.url === base + '/sellers/query'; if (delay) held = true;
  const upstream = http.request({ hostname: '127.0.0.1', port, path: req.url, method: req.method, headers }, response => {
    if (record) record.status = response.statusCode;
    if (isApi && req.url.startsWith(base)) responses.push({ path: req.url, status: response.statusCode, noStore: response.headers['cache-control']?.includes('no-store') === true });
    if (record || drop || delay) {
      const chunks = []; let size = 0; response.on('data', chunk => { size += chunk.length; if (size <= 131072) chunks.push(chunk); });
      response.once('end', async () => {
        assert.ok(size <= 131072); const body = Buffer.concat(chunks);
        if (record) record.response = body.length ? JSON.parse(body.toString('utf8')) : null;
        if (delay) await hold;
        if (res.destroyed) return;
        if (drop && response.statusCode === 200) {
          fault.dropped++; res.writeHead(200, { 'content-type': 'application/json', 'cache-control': 'private, no-store' });
          // Consume the genuine committed response, then lose its result with a
          // finite malformed envelope. No socket crash or fabricated positive API.
          res.end('{'); return;
        }
        res.writeHead(response.statusCode ?? 502, response.headers); res.end(body);
      });
      response.once('error', () => { if (!res.headersSent) res.writeHead(502); res.end(); }); return;
    }
    res.writeHead(response.statusCode ?? 502, response.headers); response.pipe(res);
  });
  upstream.on('error', () => { if (!res.headersSent) res.writeHead(502); res.end(); }); req.once('aborted', () => upstream.destroy()); req.pipe(upstream);
}
async function control(action) {
  assert.ok(['state', 'same-person', 'advance-factor-window', 'renew-maker', 'renew-checker'].includes(action));
  const response = await fetch(new URL('/__fixture/commercial/' + action, api), { method: 'POST', headers: { 'X-Fixture-Control': config.controlKey }, redirect: 'error' });
  const noContent = ['same-person', 'advance-factor-window'].includes(action); assert.equal(response.status, noContent ? 204 : 200); return noContent ? null : response.json();
}
async function user(name, token = config.tokens[name]) {
  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1280, height: 960 }, locale: 'id-ID', timezoneId: 'Asia/Jakarta' });
  await context.addCookies([{ name: '__Host-ut_session', value: token, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  const page = await context.newPage(); pages.push(page); page.setDefaultTimeout(30000); page.on('pageerror', error => errors.push(error.message)); page.on('dialog', dialog => dialog.accept());
  page.on('response', async response => {
    const url = new URL(response.url());
    if (url.origin !== origin.origin || !['/api/v1/identity/session', '/api/v1/access/current'].includes(url.pathname)) return;
    const safe = { participant: name, path: url.pathname, status: response.status() };
    try {
      const value = await response.json();
      if (typeof value.code === 'string') safe.code = value.code.slice(0, 128);
      if (typeof value.authenticated === 'boolean') safe.authenticated = value.authenticated;
      for (const field of ['allowedActions', 'stepUpOperations']) if (Array.isArray(value[field]))
        safe[field] = value[field].filter(item => typeof item === 'string' && /^[a-z]+(?:[.-][a-z]+){1,6}$/.test(item)).slice(0, 200);
    } catch { safe.jsonUnavailable = true; }
    diagnostics.push(safe); console.log('COMMERCIAL_ACCOUNT_DIAGNOSTIC ' + JSON.stringify(safe));
  });
  return { page, context, name };
}
async function renew(person) {
  const { token } = await control('renew-' + person.name); await person.context.addCookies([{ name: '__Host-ut_session', value: token, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
}
async function query(page, name, suffix, body, options = {}) {
  return page.evaluate(async ({ actor, url, body, options }) => {
    const issuer = await fetch('/api/v1/identity/csrf', { credentials: 'same-origin', cache: 'no-store' }); if (!issuer.ok) throw new Error('Request token unavailable');
    const token = await issuer.json(), headers = { 'Content-Type': 'application/json', 'X-CSRF-Token': token.requestToken, 'X-Expected-Actor-Id': actor, ...options.headers };
    for (const name of options.omit ?? []) delete headers[name];
    const response = await fetch(url, { method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', headers, body: JSON.stringify(body) });
    return { status: response.status, body: await response.json(), noStore: response.headers.get('cache-control')?.includes('no-store') === true };
  }, { actor: config.actorIds[name] ?? config.actorIds.checker, url: base + suffix, body, options });
}
async function choose(person) {
  await person.page.goto(origin.origin + route);
  const seller = person.page.getByLabel('Kanal toko', { exact: true });
  await person.page.waitForFunction(value => { const selects = [...document.querySelectorAll('select')]; const select = selects.find(item => item.parentElement?.textContent?.includes('Kanal toko')); return select && !select.disabled && [...select.options].some(option => option.value === value); }, config.seller.shopId + ':' + config.seller.channelId);
  await seller.selectOption(config.seller.shopId + ':' + config.seller.channelId);
  const target = person.page.getByLabel('Cabang / divisi', { exact: true });
  await person.page.waitForFunction(value => { const select = document.querySelector('select[aria-labelledby="commercial-target-label"]'); return select && !select.disabled && [...select.options].some(option => option.value === value); }, config.targetKind + ':' + config.targetId);
  await target.selectOption(config.targetKind + ':' + config.targetId); await person.page.getByRole('region', { name: 'Rincian aturan komersial', exact: true }).waitFor();
}
async function capture(page, state) {
  await mkdir(directory, { recursive: true });
  for (const [device, width, height] of [['desktop', 1280, 960], ['mobile', 390, 844]]) {
    await page.setViewportSize({ width, height }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, state + ' overflow');
    const path = resolve(directory, config.proofPrefix + '-' + state + '-' + device + '.png'), bytes = await page.screenshot({ path, fullPage: true });
    captures.push({ state, device, width, height, path, sha256: hash(bytes) });
  }
  await page.setViewportSize({ width: 1280, height: 960 });
}
const references = page => page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
async function proposal(person, rate, reason, lost = false) {
  await person.page.getByRole('button', { name: 'Ajukan aturan komersial', exact: true }).click();
  for (const label of ['Anggaran', 'Pusat biaya', 'Persetujuan belanja', 'Penahanan kapasitas kredit', 'Perlakuan pajak', 'Dasar pengenaan', 'Tingkat pembulatan', 'Aturan pembulatan', 'Pengelompokan', 'Awal perhitungan jatuh tempo', 'Perhitungan hari']) assert.equal(await person.page.getByLabel(label, { exact: true }).inputValue(), '');
  for (const [label, value] of [['Anggaran', 'required'], ['Pusat biaya', 'not_applicable'], ['Persetujuan belanja', 'required'], ['Penahanan kapasitas kredit', 'required'], ['Perlakuan pajak', 'taxable_exclusive'], ['Dasar pengenaan', 'subtotal_and_charges'], ['Tingkat pembulatan', 'document'], ['Aturan pembulatan', 'half_even'], ['Pengelompokan', 'receipt'], ['Awal perhitungan jatuh tempo', 'issue_date'], ['Perhitungan hari', 'working_days']]) await person.page.getByLabel(label, { exact: true }).selectOption(value);
  await person.page.getByLabel('Biaya tetap per pesanan (IDR)', { exact: true }).fill('1.25'); await person.page.getByLabel('Tarif pajak (%)', { exact: true }).fill(rate);
  await person.page.getByLabel('Jangka pembayaran (hari)', { exact: true }).fill('14');
  await person.page.getByRole('checkbox', { name: 'Senin', exact: true }).check(); await person.page.getByRole('checkbox', { name: 'Jumat', exact: true }).check();
  await person.page.getByLabel('Tanggal libur yang ditetapkan', { exact: true }).fill('2026-10-14'); await person.page.getByRole('button', { name: 'Tambahkan tanggal libur', exact: true }).click();
  for (const [label, text] of [['Rujukan prosedur penomoran', 'Synthetic reviewed seller numbering procedure'], ['Rujukan koreksi dan penerbitan ulang', 'Synthetic linked correction and reissue procedure'], ['Rujukan proses dokumen pajak', 'Synthetic reviewed tax-document procedure'], ['Alasan pengajuan', reason]]) await person.page.getByLabel(label, { exact: true }).fill(text);
  await person.page.getByLabel('Mulai berlaku (WIB)', { exact: true }).fill(config.validFromLocal); await person.page.getByLabel('Berakhir (WIB)', { exact: true }).fill(config.validUntilLocal);
  const previewReady = person.page.waitForResponse(response => response.url() === origin.origin + base + '/preview/query' && response.request().method() === 'POST', { timeout: 90000 });
  await person.page.getByRole('button', { name: 'Periksa rincian pengajuan', exact: true }).click(); const previewResponse = await previewReady; assert.equal(previewResponse.status(), 200);
  const preview = await previewResponse.json(), review = person.page.getByRole('region', { name: 'Rincian pengajuan aturan', exact: true }); await review.waitFor();
  assert.equal(await review.getByRole('heading', { name: 'Rincian yang akan diajukan', exact: true }).evaluate(element => element === document.activeElement), true);
  assert.equal(await review.getByRole('button', { name: 'Kirim pengajuan aturan', exact: true }).isDisabled(), true); await capture(person.page, 'review-' + writes.filter(write => write.status === 200).length);
  await review.getByRole('checkbox').check();
  if (lost) {
    await person.context.setOffline(true); await person.page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await review.getByRole('button', { name: 'Kirim pengajuan aturan', exact: true }).isDisabled(), true);
    await person.context.setOffline(false); await person.page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' }); fault.armed = true;
  }
  const responseReady = person.page.waitForResponse(response => response.url() === origin.origin + base + '/proposals' && response.request().method() === 'POST', { timeout: 90000 });
  await review.getByRole('button', { name: 'Kirim pengajuan aturan', exact: true }).click(); assert.equal((await responseReady).status(), 200);
  if (lost) await person.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  else await person.page.getByRole('heading', { name: 'Menunggu pemeriksaan', exact: true }).waitFor({ timeout: 90000 });
  const record = writes.filter(write => write.status === 200 && write.path === base + '/proposals').at(-1); assert.ok(record?.response?.proposalId);
  assert.equal(record.body.input.rule.taxRate, rate); assert.equal(record.body.input.rule.charges, '1.25'); assert.deepEqual(record.body.input.rule.terms.workingDays, [1, 5]);
  assert.deepEqual(record.body.input.rule.terms.holidays, ['2026-10-14']); assert.deepEqual(record.body.input.target.seller, config.seller);
  return { id: record.response.proposalId, record, preview };
}
async function decision(person, id, action) {
  await choose(person); const row = person.page.getByRole('button', { name: new RegExp('Pengajuan ' + id + '(?:\\D|$)') }); await row.click();
  await person.page.getByRole('heading', { name: 'Menunggu pemeriksaan', exact: true }).waitFor(); await person.page.getByLabel('Alasan keputusan', { exact: true }).fill('Synthetic independent Finance ' + action);
  await person.page.getByRole('button', { name: action === 'approve' ? 'Tinjau persetujuan aturan' : 'Tinjau penolakan aturan', exact: true }).click();
  const review = person.page.getByRole('region', { name: 'Rincian keputusan aturan', exact: true }); await review.waitFor();
  assert.equal(await review.getByRole('heading').evaluate(element => element === document.activeElement), true);
  const submit = review.getByRole('button', { name: action === 'approve' ? 'Setujui aturan' : 'Simpan penolakan aturan', exact: true }); assert.equal(await submit.isDisabled(), true);
  await review.getByRole('checkbox').check(); const path = base + '/proposals/' + id + '/decisions';
  const responseReady = person.page.waitForResponse(response => response.url() === origin.origin + path && response.request().method() === 'POST', { timeout: 90000 });
  await submit.click(); assert.equal((await responseReady).status(), 200); await person.page.getByRole('heading', { name: action === 'approve' ? 'Disetujui' : 'Ditolak', exact: true }).waitFor({ timeout: 90000 });
}
async function check(name, work) { await work(); checks.push(name); console.log('PASS ' + name); }
try {
  await app.prepare(); const handler = app.getRequestHandler(); frontend = track(http.createServer((req, res) => { void handler(req, res).catch(() => { res.statusCode = 500; res.end(); }); })); const frontendPort = await listen(frontend);
  edge = track(https.createServer({ pfx: Buffer.from(config.pfx, 'base64'), passphrase: config.password }, (req, res) => {
    if (req.headers.host !== origin.host || req.url.startsWith('/__fixture/')) { res.writeHead(403); res.end(); return; }
    const isApi = req.url.startsWith('/api/v1/') || req.url.startsWith('/auth/'); forward(req, res, isApi ? Number(api.port) : frontendPort, isApi);
  })); const tlsPort = await listen(edge);
  tunnel = track(http.createServer((_req, res) => { res.writeHead(403); res.end(); })); tunnel.on('connect', (req, socket, head) => {
    if (req.url !== origin.host + ':443') { socket.end('HTTP/1.1 403 Forbidden\r\n\r\n'); return; }
    const upstream = net.connect(tlsPort, '127.0.0.1', () => { socket.write('HTTP/1.1 200 Connection Established\r\n\r\n'); if (head.length) upstream.write(head); socket.pipe(upstream); upstream.pipe(socket); });
    sockets.add(upstream); upstream.once('close', () => sockets.delete(upstream)); upstream.on('error', () => socket.destroy()); socket.on('error', () => upstream.destroy()); socket.once('close', () => upstream.destroy());
  }); const proxyPort = await listen(tunnel);
  browser = await chromium.launch({ ...(channel === 'msedge' ? { channel: 'msedge' } : {}), headless: true, proxy: { server: 'http://127.0.0.1:' + proxyPort } });
  const before = await control('state'); assert.deepEqual([before.proposals, before.decisions, before.versions], [0, 0, 0]); maker = await user('maker'); checker = await user('checker');
  await maker.page.goto(origin.origin + route); await maker.page.getByText('Memeriksa aturan komersial…', { exact: true }).waitFor(); await capture(maker.page, 'loading'); releaseHold();
  await check('explicit seller buyer choices full form review offline and one lost genuine committed result', async () => {
    await choose(maker); await maker.page.getByText('Belum ada aturan komersial yang disetujui untuk unit ini.', { exact: true }).waitFor(); await capture(maker.page, 'empty');
    const first = await proposal(maker, '12.3456', 'Synthetic first commercial bundle with original-key recovery', true); config.first = first;
    const saved = await references(maker.page); assert.equal(saved.length, 1); assert.deepEqual(Object.keys(saved[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.equal(saved[0].key, first.record.key); assert.equal(saved[0].actorId, config.actorIds.maker); assert.equal(saved[0].operation, 'ordering.commercial.policy.propose');
    for (const value of ['12.3456', '1.25', first.record.body.reason, first.record.body.binding]) assert.ok(!JSON.stringify(saved).includes(value));
    const state = await control('state'); assert.deepEqual([state.proposals, state.decisions, state.versions], [1, 0, 0]); assert.equal(state.businessSha256, before.businessSha256);
    await capture(maker.page, 'uncertain');
    await maker.context.addCookies([{ name: '__Host-ut_session', value: config.tokens.checker, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
    await maker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await maker.page.getByRole('heading', { name: /^(Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor(); assert.deepEqual(await references(maker.page), saved);
    await renew(maker); await maker.page.reload(); const ready = maker.page.waitForResponse(response => response.url() === origin.origin + base + '/operations/query', { timeout: 90000 });
    await maker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); const response = await ready; assert.equal(response.status(), 200);
    assert.deepEqual(JSON.parse(response.request().postData()), { purpose: 'operation-recovery', operation: 'ordering.commercial.policy.propose', key: first.record.key });
    await maker.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden' }); assert.equal((await references(maker.page)).length, 0);
    assert.equal(writes.filter(write => write.status === 200).length, 1); assert.equal(fault.dropped, 1);
  });
  await check('pending relationship and self same-person foreign-scope stale-MFA CSRF actor guards cannot approve', async () => {
    const { id, record } = config.first; const body = { purpose, target: record.body.input.target };
    const reader = await user('reader'); await reader.page.goto(origin.origin + route); await reader.page.getByText('Belum ada toko dan kanal dengan profil atau aturan yang tersedia dalam penugasan Anda.', { exact: true }).waitFor();
    const viewerChoices = await query(reader.page, 'reader', '/sellers/query', { purpose }); assert.equal(viewerChoices.status, 200); assert.deepEqual(viewerChoices.body.items, []);
    for (const [options, status] of [[{ omit: ['X-CSRF-Token'] }, 403], [{ omit: ['X-Expected-Actor-Id'] }, 400], [{ headers: { 'X-Expected-Actor-Id': config.actorIds.checker } }, 409]]) assert.equal((await query(maker.page, 'maker', '/current/query', body, options)).status, status);
    const self = await query(maker.page, 'maker', '/proposals/' + id + '/decision-preview/query', { purpose, decision: 'approve' }); assert.equal(self.status, 403); assert.equal(self.body.code, 'independent_person_required');
    await control('same-person'); const same = await user('samePerson'); await same.page.goto(origin.origin + route);
    const related = await query(same.page, 'samePerson', '/proposals/' + id + '/decision-preview/query', { purpose, decision: 'approve' }); assert.equal(related.status, 403); assert.equal(related.body.code, 'independent_person_required');
    for (const name of ['foreignBuyer', 'foreignSeller']) { const foreign = await user(name); await foreign.page.goto(origin.origin + '/identity/account'); const denied = await query(foreign.page, name, '/current/query', body); assert.equal(denied.status, 403); for (const field of ['rule', 'policyId', 'seller', 'name']) assert.equal(Object.hasOwn(denied.body, field), false); }
    await choose(maker); await maker.page.getByRole('button', { name: new RegExp('Pengajuan ' + id + '(?:\\D|$)') }).click();
    await maker.page.getByLabel('Alasan keputusan', { exact: true }).fill('Synthetic maker self-review refusal'); await maker.page.getByRole('button', { name: 'Tinjau persetujuan aturan', exact: true }).click();
    await maker.page.getByRole('alert').filter({ hasText: 'Keputusan memerlukan pemeriksa yang berbeda dari pengaju.' }).waitFor(); await capture(maker.page, 'error');
    await control('advance-factor-window');
    const expired = await user('checker', config.tokens.expired); await expired.page.goto(origin.origin + route);
    const stale = await query(expired.page, 'checker', '/proposals/' + id + '/decision-preview/query', { purpose, decision: 'approve' });
    assert.equal(stale.status, 403); assert.equal(stale.body.code, 'step_up_required');
    const state = await control('state'); assert.deepEqual([state.proposals, state.decisions, state.versions], [1, 0, 0]); assert.equal(state.businessSha256, before.businessSha256);
  });
  await check('independent Finance approval rejection and second genuine version reload history preserve all business state', async () => {
    await renew(checker); await decision(checker, config.first.id, 'approve'); await choose(checker); await checker.page.getByRole('heading', { name: 'Aturan aktif', exact: true }).waitFor(); await capture(checker.page, 'approved');
    await renew(maker); await choose(maker); const second = await proposal(maker, '13.125', 'Synthetic second proposal deliberately rejected');
    await renew(checker); await decision(checker, second.id, 'reject'); let state = await control('state'); assert.deepEqual([state.proposals, state.decisions, state.versions], [2, 2, 1]);
    await renew(maker); await choose(maker); const third = await proposal(maker, '11.5', 'Synthetic third proposal independently approved as version2');
    await renew(checker); await decision(checker, third.id, 'approve'); await checker.page.reload(); await choose(checker);
    await checker.page.getByRole('heading', { name: 'Aturan aktif', exact: true }).waitFor(); await checker.page.getByText(/Versi 2 · Disetujui/).waitFor();
    assert.equal(await checker.page.getByRole('button', { name: /Pengajuan / }).count(), 3); await capture(checker.page, 'history');
    const reader = await user('reader'); await choose(reader); await reader.page.getByRole('heading', { name: 'Aturan aktif', exact: true }).waitFor(); assert.equal(await reader.page.getByRole('button', { name: 'Ajukan aturan komersial', exact: true }).count(), 0);
    state = await control('state'); assert.deepEqual([state.proposals, state.decisions, state.versions], [3, 3, 2]); assert.equal(state.businessSha256, before.businessSha256);
    assert.equal(writes.filter(write => write.status === 200).length, 6); assert.equal(writes.filter(write => write.key === config.first.record.key).length, 1);
  });
  assert.deepEqual(errors, []); assert.equal(fault.dropped, 1); assert.ok(responses.length); assert.equal(responses.every(response => response.noStore), true);
  await writeFile(resolve(directory, config.proofPrefix + '-proof.json'), JSON.stringify({ recordedUtc: new Date().toISOString(), buildId: config.buildId, checks, captures, diagnostics,
    originalKey: config.first.record.key, lostCommittedResults: fault.dropped, successfulWrites: 6, genuineApprovedVersions: 2,
    metadataOnlyRecovery: true, noSecondOriginalWrite: true, sameActorOriginalKeyRecovery: true, pendingRelationshipsHidden: true,
    independentApprovalAndRejection: true, noOrderStockMoneyInvoiceEffects: true, clockFixture: config.clockFixture,
    localSyntheticOnly: true, providerAccepted: false, stagingValidated: false, productionAccepted: false }, null, 2));
  console.log('LOCAL_SYNTHETIC_COMMERCIAL_POLICY_BROWSER_PASS ' + JSON.stringify({ checks: checks.length, captures: captures.length, successfulWrites: 6, versions: 2, lostResults: 1, localSyntheticOnly: true }));
} catch (error) {
  for (const [index, page] of pages.entries()) try {
    const labels = await page.evaluate(() => [...document.querySelectorAll('select')].slice(0, 20).map(select => ({
      id: select.id, labelledBy: select.getAttribute('aria-labelledby'), disabled: select.disabled, optionCount: select.options.length,
      label: select.getAttribute('aria-labelledby') ? document.getElementById(select.getAttribute('aria-labelledby'))?.textContent?.slice(0, 80) : select.labels?.[0]?.textContent?.slice(0, 80)
    })));
    diagnostics.push({ page: index, selectLabels: labels });
    await mkdir(directory, { recursive: true }); await page.screenshot({ path: resolve(directory, config.proofPrefix + '-failure-' + index + '.png'), fullPage: true });
  } catch { /* Preserve the primary failure. */ }
  await mkdir(directory, { recursive: true }); await writeFile(resolve(directory, config.proofPrefix + '-failure-diagnostics.json'), JSON.stringify({ buildId: config.buildId, checks, diagnostics }, null, 2));
  console.error(error.stack ?? String(error)); process.exitCode = 1;
} finally {
  releaseHold(); if (browser) await browser.close(); for (const socket of sockets) socket.destroy();
  for (const server of [tunnel, edge, frontend]) if (server) await new Promise(resolve => server.close(resolve)); await app.close();
}
