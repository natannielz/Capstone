import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { createResolutionGateway, prepareResolution, parseResolutionView, parseResolutionResult, resolutionOwner } from '../src/modules/fulfillment/resolution-client';
import { OperationJournal, reconcileOperationRecovery, operationRecoveryCapabilities } from '../src/modules/administration/operation-journal';
import { operationLabel, recoveryPage } from '../src/modules/administration/operation-recovery-navigation';

const actor = '9007199254740993', invoiceId = '9007199254740994', caseId = '9223372036854775807', returnId = '9007199254740996';
const key = 'cq.' + caseId + '.' + returnId + '.original';
const source = { invoiceId, binding: 'a'.repeat(64), number: '100/103/2026/1', buyerName: 'Cabang Tebet', sellerName: 'Omi', issuedAt: '2026-10-01T03:00:00Z',
  lines: [{ orderId: '91', shipmentId: '92', receiptId: '93', skuId: '94', skuCode: 'ATK01', productName: 'Kertas', packageLabel: 'Karton', baseUnitsPerPackage: 12, baseUnitCode: 'rim', acceptedBaseUnits: '6' }] };
const reported = [{ receiptId: '93', skuId: '94', reportedBaseUnits: '2' }];
const owner = { id: caseId, revision: '2' as const, requestedAt: '2026-10-01T03:01:00Z', state: 'return_authorized' as const, issue: 'damaged' as const, reportedProblem: 'Barang rusak', source, lines: reported,
  decision: { caseId, invoiceId, revision: '2' as const, binding: 'b'.repeat(64), state: 'return_authorized' as const, decidedAt: '2026-10-01T03:02:00Z', publicExplanation: 'Pengembalian untuk pemeriksaan', lines: reported } };
const receipt = { id: returnId, revision: '1', receivedAt: '2026-10-01T03:03:00Z', publicNote: 'Satu barang tiba', lines: [{ receiptId: '93', skuId: '94', receivedBaseUnits: '1', observedCondition: 'opened' as const }] };
const assessment = { inspectedAt: '2026-10-01T03:04:00Z', publicNote: 'Barang sudah diperiksa', lines: [{ receiptId: '93', skuId: '94', inspectedBaseUnits: '1', identity: 'matched' as const, condition: 'opened' as const }] };
const inspection = { caseId, invoiceId, revision: '1' as const, binding: 'c'.repeat(64), receipt, inspection: assessment };
const preview = { caseId, invoiceId, returnId, revision: '0' as const, binding: 'd'.repeat(64), inspection: assessment, request: null };
const proposal = { requestedAt: '2026-10-01T03:05:00Z', action: 'replacement' as const, publicNote: 'Ajukan penggantian barang', state: 'resolution_requested' as const };
const result = { returnId, caseId, invoiceId, revision: '1' as const, requestedAt: proposal.requestedAt, action: proposal.action, state: proposal.state, operation: 'fulfillment.case.resolution_request' as const };

test('resolution requires the exact inspected receipt, keeps IDs lossless and exposes only a pending public proposal', () => {
  assert.deepEqual(resolutionOwner(key), { caseId, returnId });
  assert.deepEqual(parseResolutionView(preview, owner, inspection), preview);
  const requested = { ...preview, revision: '1', request: proposal };
  assert.deepEqual(parseResolutionView(requested, owner, inspection), requested);
  assert.deepEqual(parseResolutionResult(result, key), result);
  for (const changed of [{ ...preview, caseId: '1' }, { ...preview, invoiceId: '1' }, { ...preview, returnId: '1' }, { ...preview, revision: '1' }, { ...preview, amount: '500' },
    { ...preview, inspection: { ...assessment, inspectedAt: receipt.receivedAt } }, { ...preview, inspection: { ...assessment, publicNote: 'Another assessment' } },
    { ...preview, inspection: { ...assessment, lines: [{ ...assessment.lines[0], inspectedBaseUnits: '2' }] } },
    { ...preview, inspection: { ...assessment, lines: [{ ...assessment.lines[0], observedLotLabel: 'Privat' }] } },
    { ...requested, revision: '0' }, { ...requested, request: { ...proposal, requestedAt: receipt.receivedAt } }, { ...requested, request: { ...proposal, state: 'approved' } },
    { ...requested, request: { ...proposal, amount: '500' } }]) assert.throws(() => parseResolutionView(changed, owner, inspection));
  assert.throws(() => parseResolutionView(preview, owner, { ...inspection, revision: '0', inspection: null }));
  for (const invalidKey of ['ci.' + caseId + '.' + returnId + '.original', 'cq.0.' + returnId + '.original', 'cq.9223372036854775808.' + returnId + '.original', key + '\n', 'cq.' + caseId + '.' + returnId + '.' + 'x'.repeat(69)]) assert.throws(() => resolutionOwner(invalidKey));
  for (const changed of [{ ...result, returnId: '1' }, { ...result, caseId: '1' }, { ...result, invoiceId: Number(invoiceId) }, { ...result, operation: 'fulfillment.case.return_inspect' }, { ...result, action: 'restock' },
    { ...result, state: 'approved' }, { ...result, state: 'paid' }, { ...result, refund: '500' }]) assert.throws(() => parseResolutionResult(changed, key));
  assert.deepEqual(recoveryPage(result.operation), { href: '/fulfillment/cases', label: 'halaman pengajuan penyelesaian retur' });
  assert.equal(operationLabel(result.operation), 'Pengajuan penyelesaian retur');
});

test('resolution freezes a request without stock or money fields and rejects unsupported choices or invalid text', () => {
  for (const action of ['replacement', 'credit', 'refund'] as const) {
    const command = prepareResolution(actor, owner, inspection, preview, action, proposal.publicNote, 'Catatan internal');
    assert.ok(Object.isFrozen(command)); assert.ok(Object.isFrozen(command.body));
    assert.deepEqual(command, { actorId: actor, caseId, returnId, invoiceId, revision: '0', body: { binding: preview.binding, action, publicNote: proposal.publicNote, reason: 'Catatan internal' } });
  }
  for (const action of ['paid', 'approved', 'restock', 'refund\n']) assert.throws(() => prepareResolution(actor, owner, inspection, preview, action as never, proposal.publicNote, 'Privat'));
  for (const note of ['', ' ', ' leading', 'trailing ', 'x'.repeat(1001), 'zero\u200bwidth', 'line\nfeed']) {
    assert.throws(() => prepareResolution(actor, owner, inspection, preview, 'replacement', note, 'Privat'));
    assert.throws(() => prepareResolution(actor, owner, inspection, preview, 'replacement', proposal.publicNote, note));
  }
  assert.throws(() => prepareResolution('9223372036854775808', owner, inspection, preview, 'replacement', proposal.publicNote, 'Privat'));
  assert.throws(() => prepareResolution(actor, owner, inspection, { ...preview, revision: '1', request: proposal }, 'replacement', proposal.publicNote, 'Privat'));
});

test('resolution queries the specific action grant and sends exact frozen actor, owner, CSRF and revision preconditions', async () => {
  const requests: Request[] = [];
  const gateway = createResolutionGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' }); requests.push(request);
    assert.ok(request.url.includes('/cases/' + caseId + '/returns/' + returnId + '/resolution')); assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf');
    if (request.url.endsWith('/preview/query')) {
      assert.equal(request.headers.get('If-Match'), '"2"');
      assert.deepEqual(await request.clone().json(), { purpose: 'after-sales-resolution', action: 'refund' });
      return Response.json({ code: 'authorization_denied' }, { status: 403 });
    }
    if (request.url.endsWith('/resolution/query')) {
      assert.equal(request.headers.get('If-Match'), '"2"'); assert.deepEqual(await request.clone().json(), { purpose: 'after-sales-resolution' }); return Response.json(preview);
    }
    assert.equal(request.headers.get('If-Match'), '"0"'); assert.equal(request.headers.get('Idempotency-Key'), key); return Response.json(result);
  } }));
  assert.deepEqual(await gateway.read(actor, owner, inspection), preview);
  await assert.rejects(() => gateway.read(actor, owner, inspection, 'refund'), { status: 403, code: 'authorization_denied' });
  const command = prepareResolution(actor, owner, inspection, preview, 'replacement', proposal.publicNote, 'Privat');
  await assert.rejects(() => gateway.request(command, key, () => false), RequestNotSentError); assert.equal(requests.length, 2);
  await assert.rejects(() => gateway.request(command, 'cq.1.' + returnId + '.wrong', () => true)); assert.equal(requests.length, 2);
  assert.deepEqual(await gateway.request(command, key, () => true), result); assert.deepEqual(await requests[2].clone().json(), command.body);
});

test('a lost resolution response retains metadata until exact owner recovery, without retry, fallback or manual close', async () => {
  const values = new Map<string, string>(), storage = { get length() { return values.size; }, key: (index: number) => [...values.keys()][index] ?? null, getItem: (name: string) => values.get(name) ?? null,
    setItem: (name: string, value: string) => { values.set(name, value); }, removeItem: (name: string) => { values.delete(name); } };
  const journal = new OperationJournal(() => storage), entry = journal.remember(actor, result.operation, key);
  let writes = 0, committed = false;
  const gateway = createResolutionGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actor);
    if (request.url.endsWith('/operations/query')) {
      assert.deepEqual(await request.clone().json(), { purpose: 'operation-recovery', key });
      return Response.json({ status: committed ? 'committed' : 'not_confirmed', result: committed ? result : null });
    }
    writes++; committed = true; throw new TypeError('Committed reply lost');
  } }));
  const legacy = { lookup: async () => { throw new Error('wrong owner'); }, close: async () => { throw new Error('must not close'); } };
  const adapters = { 'fulfillment.case': { lookup: async () => gateway.recover(actor, key) } };
  assert.equal(operationRecoveryCapabilities(entry.operation, adapters).canClose, false);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, adapters, 'close'));
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, adapters)).state, 'unconfirmed'); assert.equal(journal.list(actor).length, 1);
  await assert.rejects(() => gateway.request(prepareResolution(actor, owner, inspection, preview, 'replacement', proposal.publicNote, 'Privat'), key, () => true));
  assert.equal(writes, 1); assert.equal(journal.list(actor).length, 1);
  assert.ok(![...values.values()].join('').includes(proposal.publicNote));
  for (const changed of [{ ...result, returnId: '1' }, { ...result, operation: 'fulfillment.case.return_inspect' }, { ...result, state: 'paid' }]) {
    await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'fulfillment.case': { lookup: async () => ({ state: 'committed', result: changed as never }) } })); assert.equal(journal.list(actor).length, 1);
  }
  await reconcileOperationRecovery(entry, journal, legacy, adapters); assert.equal(journal.list(actor).length, 0); assert.equal(writes, 1);
});

test('resolution retains uncertainty on changed context or mismatched committed action and rejects contradictory recovery envelopes', async () => {
  const command = prepareResolution(actor, owner, inspection, preview, 'replacement', proposal.publicNote, 'Privat');
  let response: unknown = result;
  const gateway = createResolutionGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' }); return Response.json(response);
  } }));
  let observed = 0;
  await assert.rejects(() => gateway.request(command, key, () => ++observed === 1), { status: 503, code: 'commit_outcome_unknown' });
  for (const changed of [{ ...result, action: 'refund' }, { ...result, invoiceId: '1' }]) { response = changed; await assert.rejects(() => gateway.request(command, key, () => true)); }
  for (const changed of [{ status: 'not_confirmed', result }, { status: 'committed', result: null }, { status: 'committed', result, approved: true }]) { response = changed; await assert.rejects(() => gateway.recover(actor, key)); }
});
