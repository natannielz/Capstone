import assert from 'node:assert/strict';
import {existsSync} from 'node:fs';
import {writeFile} from 'node:fs/promises';
import {resolve} from 'node:path';
import {createHash} from 'node:crypto';

// Genuine owner calls only. Synthetic local identities never bypass production authorization.
export async function verifyCourierAssignmentBrowser({config, admin, user, origin, directory, restartUser}) {
  const api = '/api/v1/fulfillment/courier-assignments', pagePath = '/fulfillment/courier-assignments';
  const writes = [], recoveries = [], captures = [], watched = new WeakSet();
  const journalPrefix = 'unit-toko.courier-assignment.pending.' + config.adminId + '.';
  function watch(account) {
    if (!watched.has(account.context)) {
      watched.add(account.context); account.context.on('request', request => {
        const path = new URL(request.url()).pathname;
        if (request.method() !== 'POST') return;
        if (path === api + '/' + config.shipmentId + '/handover') writes.push({path, key: request.headers()['idempotency-key'], actor: request.headers()['x-expected-actor-id'],
          version: request.headers()['if-match'], body: request.postDataJSON()});
        if (path === api + '/operations/query') recoveries.push({actor: request.headers()['x-expected-actor-id'], body: request.postDataJSON()});
      });
    } return account;
  }
  async function exact(page, role, name) {const control = page.getByRole(role, {name, exact: true}); await control.waitFor(); assert.equal(await control.count(), 1); return control;}
  async function capture(page, state) {
    const original = page.viewportSize();
    for (const width of [360, 1440]) {
      await page.setViewportSize({width, height: 900}); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
      const file = config.proofPrefix + '-' + state + '-' + width + '.png';
      const bytes = await page.screenshot({path: resolve(directory, file), fullPage: true});
      captures.push({state, width, file, sha256: createHash('sha256').update(bytes).digest('hex')});
    } await page.setViewportSize(original);
  }
  async function query(page, actor, endpoint, body, headers = {}) {
    return page.evaluate(async ({actor, endpoint, body, headers}) => {
      const csrfResponse = await fetch('/api/v1/identity/csrf', {credentials: 'same-origin', cache: 'no-store', signal: AbortSignal.timeout(30_000)});
      if (!csrfResponse.ok) throw new Error('Fixture could not obtain actual CSRF protection');
      const csrf = await csrfResponse.json();
      const response = await fetch(endpoint, {method: 'POST', credentials: 'same-origin', cache: 'no-store', signal: AbortSignal.timeout(30_000),
        headers: {'Content-Type': 'application/json', 'X-CSRF-Token': csrf.requestToken, 'X-Expected-Actor-Id': actor, ...headers}, body: JSON.stringify(body)});
      if (response.headers.get('content-type')?.startsWith('text/html')) {
        const bytes = await response.arrayBuffer(), digest = await crypto.subtle.digest('SHA-256', bytes);
        return {status: response.status, hash: Array.from(new Uint8Array(digest), value => value.toString(16).padStart(2, '0')).join(''), length: bytes.byteLength};
      } return {status: response.status, body: await response.json()};
    }, {actor, endpoint, body, headers});
  }
  async function open(account) {
    await account.page.goto(origin.origin + pagePath); await (await exact(account.page, 'heading', 'Penugasan kurir')).waitFor();
    await (await exact(account.page, 'button', 'Tinjau penugasan ' + config.shipmentReference)).click();
    return exact(account.page, 'region', 'Rincian penugasan');
  }
  async function preview(account, reason) {
    const detail = await open(account), courier = await exact(detail, 'combobox', 'Kurir penerima tugas');
    assert.equal(await courier.inputValue(), ''); assert.equal(await courier.locator('option[value="' + config.courierAId + '"]').count(), 0);
    assert.equal(await courier.locator('option[value="' + config.courierBId + '"]').count(), 1);
    await courier.selectOption(config.courierBId);
    const note = await exact(detail, 'textbox', 'Catatan penyerahan tugas'); assert.equal(await note.inputValue(), ''); await note.fill(reason);
    const arrived = account.page.waitForResponse(response => new URL(response.url()).pathname === api + '/' + config.shipmentId + '/preview/query');
    await (await exact(detail, 'button', 'Tinjau penyerahan tugas')).click();
    const response = await arrived; assert.equal(response.status(), 200); const value = await response.json();
    assert.equal(value.task.shipmentId, config.shipmentId); assert.equal(value.task.currentAssignment.revision, '1');
    assert.equal(value.task.departureCourierId, config.courierAId); assert.equal(value.task.currentAssignment.courierId, config.courierAId);
    assert.equal(value.courierId, config.courierBId); assert.equal(value.courierName, config.courierBName);
    const heading = await exact(detail, 'heading', 'Konfirmasi penyerahan tugas');
    assert.equal(await heading.evaluate(element => element === document.activeElement), true); return value;
  }
  let current = watch(admin);
  const reason = 'Serah tugas kepada kurir yang tersedia; barang tetap mengikuti Surat Jalan asli';
  const firstPreview = await preview(current, reason); assert.equal(writes.length, 0);
  const forbiddenRead = await query(current.page, config.adminId, '/api/v1/fulfillment/shipments/' + config.shipmentId + '/query', {purpose: 'shipment-read'});
  assert.equal(forbiddenRead.status, 403);
  const forbiddenDocument = await query(current.page, config.adminId, '/api/v1/fulfillment/shipment-documents/' + config.shipmentId + '/query', {purpose: 'shipment-document'});
  assert.equal(forbiddenDocument.status, 403);
  const stale = {context: current.context, page: await current.context.newPage()}; stale.page.setDefaultTimeout(30_000);
  await preview(stale, 'Pratinjau dari tab lain belum dikirim');
  await capture(current.page, 'review');
  let resolveCommit, rejectCommit;
  const committed = new Promise((resolve, reject) => {resolveCommit = resolve; rejectCommit = reject;});
  const commandUrl = origin.origin + api + '/' + config.shipmentId + '/handover';
  const intercept = async route => {
    try {
      const started = performance.now(), response = await route.fetch({timeout: 30_000, maxRetries: 0});
      assert.equal(response.status(), 200); assert.ok(performance.now() - started < 30_000);
      const result = await response.json(), request = route.request();
      const saved = {result, key: request.headers()['idempotency-key'], body: request.postDataJSON()};
      await route.abort('failed'); await current.page.unroute(commandUrl, intercept); resolveCommit(saved);
    } catch (failure) {rejectCommit(failure); await route.abort('failed').catch(() => {});}
  };
  await current.page.route(commandUrl, intercept);
  await (await exact(current.page, 'button', 'Konfirmasi penugasan kurir')).click(); const saved = await committed;
  assert.equal(saved.result.shipmentId, config.shipmentId); assert.equal(saved.result.revision, '2');
  assert.equal(saved.result.previousCourierId, config.courierAId); assert.equal(saved.result.courierId, config.courierBId);
  assert.equal(saved.result.reason, reason); assert.equal(saved.body.review, firstPreview.review);
  assert.equal(writes.length, 1); assert.equal(writes[0].version, '"1"'); assert.equal(writes[0].actor, config.adminId);
  await (await exact(current.page, 'region', 'Pemulihan penugasan')).waitFor();
  await stale.page.getByRole('heading', {name: 'Konfirmasi penyerahan tugas', exact: true}).waitFor({state: 'hidden'});
  assert.equal(await (await exact(stale.page, 'button', 'Muat penugasan terbaru')).isDisabled(), true);
  const refs = await current.page.evaluate(prefix => Object.entries(localStorage).filter(([key]) => key.startsWith(prefix)).map(([, value]) => JSON.parse(value)), journalPrefix);
  assert.equal(refs.length, 1); assert.deepEqual(Object.keys(refs[0]).sort(), ['actorId', 'courierId', 'createdAt', 'expectedRevision', 'key', 'previousCourierId', 'shipmentId']);
  assert.equal(refs[0].key, saved.key); assert.equal(refs[0].expectedRevision, '1'); assert.equal(refs[0].courierId, config.courierBId);
  const retained = await current.context.storageState(); assert.ok(retained.origins.some(row => row.localStorage.some(item => item.name === journalPrefix + saved.key)));
  current = watch(await restartUser(config.adminToken, retained)); await current.page.goto(origin.origin + pagePath);
  assert.equal(await current.page.getByRole('region', {name: 'Rincian penugasan', exact: true}).count(), 0);
  const recoveredResponse = current.page.waitForResponse(response => new URL(response.url()).pathname === api + '/operations/query');
  await (await exact(current.page, 'button', 'Periksa hasil penugasan ' + config.shipmentId)).click();
  const recovered = await recoveredResponse; assert.equal(recovered.status(), 200, 'recovery ' + recovered.status() + ': ' + await recovered.text()); assert.deepEqual((await recovered.json()).result, saved.result);
  await (await exact(current.page, 'region', 'Hasil penugasan')).waitFor();
  assert.equal(await current.page.evaluate(prefix => Object.keys(localStorage).filter(key => key.startsWith(prefix)).length, journalPrefix), 0);
  assert.equal(writes.length, 1); assert.ok(recoveries.some(row => row.actor === config.adminId && row.body.key === saved.key));
  const refreshed = await open(current); assert.ok((await refreshed.innerText()).includes(config.courierBName));
  await (await exact(refreshed, 'button', 'Muat ulang penugasan')).click();
  const originalAgain = await query(current.page, config.adminId, api + '/operations/query', {purpose: 'operation-recovery', key: saved.key});
  assert.equal(originalAgain.status, 200); assert.deepEqual(originalAgain.body.result, saved.result); await capture(current.page, 'recovered');

  const wrong = await user(config.wrongScopeToken); await wrong.page.goto(origin.origin + pagePath);
  for (const [suffix, body] of [['/query', {purpose: 'courier-assignment'}], ['/couriers/query', {purpose: 'courier-assignment', beforeId: null, limit: 25}],
    ['/preview/query', {purpose: 'courier-assignment', expectedRevision: '2', courierId: config.courierAId}]]) {
    assert.equal((await query(wrong.page, config.wrongScopeId, api + '/' + config.shipmentId + suffix, body)).status, 403);
  }
  await current.context.addCookies([{name: '__Host-ut_session', value: config.wrongScopeToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax'}]);
  const staleActor = current.page.waitForResponse(response => new URL(response.url()).pathname === api + '/' + config.shipmentId + '/query');
  await (await exact(current.page, 'button', 'Muat ulang penugasan')).click(); assert.equal((await staleActor).status(), 409);
  await (await exact(current.page, 'heading', 'Periksa sesi dan akses')).waitFor();
  assert.equal(await current.page.getByRole('region', {name: 'Rincian penugasan', exact: true}).count(), 0);
  await current.context.addCookies([{name: '__Host-ut_session', value: config.adminToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax'}]);

  // Pause before any recipient write so the native side can prove the handover-only boundary.
  assert.equal(existsSync(config.handoverCheckedPath), false);
  console.log('VALIDATE_COURIER_HANDOVER_READY');
  const checkDeadline = Date.now() + 30_000;
  while (!existsSync(config.handoverCheckedPath)) {
    if (Date.now() >= checkDeadline) throw new Error('Handover-only owner snapshot did not complete within30 seconds');
    await new Promise(accept => setTimeout(accept, 100));
  }

  const former = await user(config.courierAToken); await former.page.goto(origin.origin + '/fulfillment/shipments');
  const deniedChallenge = await query(former.page, config.courierAId, '/api/v1/fulfillment/receipts/' + config.shipmentId + '/challenges',
    {documentId: config.documentId, recipientId: config.recipientId, reason: 'Former courier cannot issue a fresh QR'}, {'Idempotency-Key': crypto.randomUUID()});
  assert.equal(deniedChallenge.status, 403);
  const recipient = await user(config.recipientToken);
  const oldHref = origin.origin + '/fulfillment/receive#' + config.oldChallengeId + '.' + config.oldChallengeToken;
  await recipient.page.goto(oldHref);
  const oldReview = recipient.page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1/fulfillment/receipts/challenges/' + config.oldChallengeId + '/query');
  await (await exact(recipient.page, 'button', 'Periksa kiriman')).click(); assert.equal((await oldReview).status(), 403);
  assert.equal(await recipient.page.getByRole('heading', {name: 'Hitung barang yang tiba', exact: true}).count(), 0);

  const courier = await user(config.courierBToken); await courier.page.goto(origin.origin + '/fulfillment/shipments');
  await (await exact(courier.page, 'button', 'Lihat kiriman ' + config.shipmentReference)).click();
  const shipment = await exact(courier.page, 'region', 'Rincian kiriman');
  await shipment.getByText('Kurir saat keberangkatan: ' + config.courierAName, {exact: false}).waitFor();
  await shipment.getByText('Kurir yang bertugas: ' + config.courierBName, {exact: false}).waitFor();
  const document = await query(courier.page, config.courierBId, '/api/v1/fulfillment/shipment-documents/' + config.shipmentId + '/' + config.documentId + '/download', {purpose: 'shipment-document'});
  assert.equal(document.status, 200); assert.equal(document.hash, config.documentHash); assert.equal(document.length, config.documentLength);
  const panel = await exact(courier.page, 'region', 'Penerimaan kiriman');
  await (await exact(panel, 'combobox', 'Penerima terdaftar')).selectOption(config.recipientId);
  await (await exact(panel, 'textbox', 'Catatan QR')).fill('Kurir penerima tugas menyiapkan penerimaan terverifikasi');
  await (await exact(panel, 'button', 'Tinjau penerima')).click();
  const createdQr = courier.page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1/fulfillment/receipts/' + config.shipmentId + '/challenges');
  await (await exact(panel, 'button', 'Buat QR penerimaan')).click(); const qrResponse = await createdQr; assert.equal(qrResponse.status(), 200);
  const qr = await qrResponse.json(); assert.notEqual(qr.id, config.oldChallengeId); assert.equal(qr.documentId, config.documentId);
  const href = await (await exact(panel, 'link', 'Buka tautan penerimaan')).getAttribute('href'); assert.notEqual(href, oldHref);
  await capture(courier.page, 'current-courier');
  await recipient.page.goto(origin.origin + '/identity/account'); await recipient.page.goto(href);
  await (await exact(recipient.page, 'button', 'Periksa kiriman')).click();
  await (await exact(recipient.page, 'heading', 'Hitung barang yang tiba')).waitFor();
  for (const [label, packages] of [['Diterima', String(config.packages)], ['Ditolak', '0'], ['Belum diterima', '0']]) {
    const whole = await exact(recipient.page, 'textbox', label + ' — kemasan utuh'); assert.equal(await whole.inputValue(), ''); await whole.fill(packages);
    await (await exact(recipient.page, 'textbox', label + ' — satuan lepas')).fill('0');
  }
  await (await exact(recipient.page, 'textbox', 'Catatan penerimaan')).fill('Penerima menghitung jumlah sesuai Surat Jalan asli');
  await (await exact(recipient.page, 'button', 'Tinjau jumlah')).click();
  const confirm = await exact(recipient.page, 'button', 'Konfirmasi penerimaan'); assert.equal(await confirm.isDisabled(), true);
  const consent = recipient.page.getByRole('checkbox'); assert.equal(await consent.count(), 1); assert.equal(await consent.isChecked(), false); await consent.check();
  const accepted = recipient.page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1/fulfillment/receipts/challenges/' + qr.id + '/confirm');
  await confirm.click(); const receiptResponse = await accepted; assert.equal(receiptResponse.status(), 200); const receipt = await receiptResponse.json();
  assert.equal(receipt.shipmentId, config.shipmentId); assert.equal(receipt.recipientId, config.recipientId); assert.equal(receipt.documentId, config.documentId); assert.equal(receipt.hasException, false);
  await (await exact(recipient.page, 'heading', 'Penerimaan tersimpan')).waitFor(); await capture(recipient.page, 'received');
  const completed = await open(current); await completed.getByText('Penerimaan sudah tercatat; tugas kurir tidak dapat dialihkan.', {exact: true}).waitFor();
  assert.equal(await completed.getByRole('combobox', {name: 'Kurir penerima tugas', exact: true}).count(), 0);
  const closed = await query(current.page, config.adminId, api + '/' + config.shipmentId + '/preview/query', {purpose: 'courier-assignment', expectedRevision: '2', courierId: config.courierAId});
  assert.equal(closed.status, 409); assert.equal(closed.body.code, 'shipment_already_received');
  const retainedResult = await query(current.page, config.adminId, api + '/operations/query', {purpose: 'operation-recovery', key: saved.key});
  assert.equal(retainedResult.status, 200); assert.deepEqual(retainedResult.body.result, saved.result); assert.equal(writes.length, 1);
  await writeFile(resolve(directory, config.proofPrefix + '-proof.json'), JSON.stringify({recordedAt: new Date().toISOString(), writes, recoveries,
    originalChange: saved.result, receiptId: receipt.id, captures, actualBrowserRestart: true, lostCommittedReplies: 1, currentCourier: config.courierBId,
    oldQrDenied: true, formerCourierDenied: true, wrongScopeDenied: true, changedActorDenied: true, privateReadNotGranted: true,
    immutableDocumentHash: document.hash, physicalCustodyClaimedByHandover: false, productionAccepted: false}, null, 2));
  console.log('REAL_COURIER_ASSIGNMENT_BROWSER_PASS ' + JSON.stringify({handovers: 1, originalKeysRecovered: 1, recipientReceipts: 1, captures: captures.length}));
}
