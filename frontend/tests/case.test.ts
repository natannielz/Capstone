import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createCaseGateway, prepareCase, prepareCaseDecision, parseCaseResult, parseCaseDecisionResult } from '../src/modules/fulfillment/case-client';
import { OperationJournal, reconcileOperationRecovery, operationRecoveryCapabilities } from '../src/modules/administration/operation-journal';
import { identityDestinations, identityReturnPath } from '../src/modules/identity/identity-workflow';

const actor = '9007199254740993', invoiceId = '9007199254740994', key = 'ca.' + invoiceId + '.original';
const source = { invoiceId, binding: 'a'.repeat(64), number: '100/103/2026/1', buyerName: 'Cabang Tebet', sellerName: 'Omi', issuedAt: '2026-09-30T03:00:00Z',
  lines: [{ orderId: '91', shipmentId: '92', receiptId: '93', skuId: '94', skuCode: 'ATK01', productName: 'Kertas', packageLabel: 'Karton', baseUnitsPerPackage: 12, baseUnitCode: 'rim', acceptedBaseUnits: '6' }] };
const lines = [{ receiptId: '93', skuId: '94', reportedBaseUnits: '1' }];
const result = { caseId: '9007199254740995', invoiceId, revision: '1' as const, requestedAt: '2026-09-30T03:01:00Z', operation: 'fulfillment.case.request' as const };
test('case preserves reviewed packaging and lossless ownership; rejects unaccepted, duplicate, oversized or hidden-money input', () => {
  const command = prepareCase(actor, source, 'damaged', lines, 'Barang rusak'); assert.ok(Object.isFrozen(command.source.lines[0])); assert.ok(Object.isFrozen(command.body.lines[0]));
  assert.deepEqual(parseCaseResult(result, key), result);
  for (const quantity of ['0', '7', '1000001', '1.0', '01']) assert.throws(() => prepareCase(actor, source, 'damaged', [{ ...lines[0], reportedBaseUnits: quantity }], 'Barang rusak'));
  assert.throws(() => prepareCase(actor, source, 'damaged', [...lines, ...lines], 'Barang rusak'));
  assert.throws(() => prepareCase(actor, { ...source, ...{ refund: '100' } }, 'damaged', lines, 'Barang rusak'));
  assert.throws(() => prepareCase(actor, source, 'approved_refund', lines, 'Barang rusak'));
  assert.throws(() => prepareCase(actor, source, 'damaged', lines, 'Barang\u200brusak'));
  for (const invalid of [{ ...result, invoiceId: '1' }, { ...result, revision: '2' }, { ...result, caseId: 9007199254740995 }, { ...result, reason: 'private' }]) assert.throws(() => parseCaseResult(invalid, key));
  assert.equal(identityReturnPath('/fulfillment/cases'), '/fulfillment/cases');
  assert.ok(identityDestinations(['case.read']).some(item => item.href === '/fulfillment/cases')); assert.ok(identityDestinations(['case.request']).some(item => item.href === '/fulfillment/cases'));
});
test('case request sends exact frozen actor, CSRF and create version; stale views are unsent and mismatched results remain unknown', async () => {
  const requests: Request[] = []; let wrong = false;
  const gateway = createCaseGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' }); requests.push(request);
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(request.headers.get('If-Match'), '"0"'); assert.equal(request.headers.get('Idempotency-Key'), key);
    return Response.json({ ...result, invoiceId: wrong ? '1' : invoiceId });
  } })); const command = prepareCase(actor, source, 'damaged', lines, 'Barang rusak');
  await assert.rejects(() => gateway.request(command, key, () => false)); assert.equal(requests.length, 0);
  assert.deepEqual(await gateway.request(command, key, () => true), result); assert.deepEqual(await requests[0].clone().json(), command.body);
  wrong = true; await assert.rejects(() => gateway.request(command, key, () => true)); assert.equal(requests.length, 2);
});
test('case original-key recovery never falls back, closes, resends or removes an absent or wrong-owner receipt', async () => {
  const values = new Map<string, string>(), storage = { get length() { return values.size; }, key: (index: number) => [...values.keys()][index] ?? null,
    getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); }, removeItem: (key: string) => { values.delete(key); } };
  const journal = new OperationJournal(() => storage), entry = journal.remember(actor, 'fulfillment.case.request', key);
  const legacy = { lookup: async () => { throw new Error('wrong owner'); }, close: async () => { throw new Error('must not close'); } };
  const absent = { 'fulfillment.case': { lookup: async () => null } };
  assert.equal(operationRecoveryCapabilities(entry.operation).canCheck, false); assert.equal(operationRecoveryCapabilities(entry.operation, absent).canClose, false);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, absent, 'close'));
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, absent)).state, 'unconfirmed'); assert.equal(journal.list(actor).length, 1);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'fulfillment.case': { lookup: async () => ({ state: 'committed' as const, result: { ...result, invoiceId: '1' } }) } })); assert.equal(journal.list(actor).length, 1);
  await reconcileOperationRecovery(entry, journal, legacy, { 'fulfillment.case': { lookup: async () => ({ state: 'committed' as const, result }) } }); assert.equal(journal.list(actor).length, 0);
});
test('manual case decisions bind original report and actor, preserve private recovery, and reject changed ownership, quantities and mixed receipts', async () => {
  const owner = { id: result.caseId, revision: '1' as const, requestedAt: result.requestedAt, state: 'requested' as const, issue: 'damaged' as const, reportedProblem: 'Barang rusak', source, lines, decision: null };
  const preview = { caseId: owner.id, invoiceId, revision: '1' as const, binding: 'b'.repeat(64), state: 'requested' as const, decidedAt: null, publicExplanation: null, lines: [] };
  const decisionKey = 'cd.' + owner.id + '.original', saved = { caseId: owner.id, invoiceId, revision: '2' as const, state: 'return_authorized' as const, decidedAt: '2026-09-30T03:02:00Z', operation: 'fulfillment.case.decide' as const };
  const command = prepareCaseDecision(actor, owner, preview, 'authorize_return', lines, 'Kembalikan untuk pemeriksaan', 'Catatan petugas'); assert.ok(Object.isFrozen(command.body.lines[0])); assert.equal(command.actorId, actor);
  for (const changed of [{ ...preview, caseId: '1' }, { ...preview, invoiceId: '1' }, { ...preview, binding: 'A'.repeat(64) }]) assert.throws(() => prepareCaseDecision(actor, owner, changed, 'authorize_return', lines, 'Penjelasan', 'Catatan'));
  for (const changed of [[], [{ ...lines[0], reportedBaseUnits: '2' }], [...lines, ...lines]]) assert.throws(() => prepareCaseDecision(actor, owner, preview, 'authorize_return', changed, 'Penjelasan', 'Catatan'));
  assert.throws(() => prepareCaseDecision(actor, owner, preview, 'reject', lines, 'Penjelasan', 'Catatan'));
  assert.throws(() => prepareCaseDecision(actor, owner, preview, 'paid', [], 'Penjelasan', 'Catatan'));
  assert.throws(() => parseCaseDecisionResult(result, decisionKey)); assert.throws(() => parseCaseResult(saved, key)); assert.throws(() => parseCaseDecisionResult({ ...saved, caseId: '1' }, decisionKey));
  assert.deepEqual(parseCaseDecisionResult(saved, decisionKey), saved);
  const requests: Request[] = []; const gateway = createCaseGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' }); requests.push(request);
    if (request.url.endsWith('/operations/query')) return Response.json({ status: 'committed', result: saved });
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(request.headers.get('If-Match'), '"1"'); assert.equal(request.headers.get('Idempotency-Key'), decisionKey); return Response.json(saved);
  } }));
  await assert.rejects(() => gateway.decide(command, decisionKey, () => false)); assert.equal(requests.length, 0);
  assert.deepEqual(await gateway.decide(command, decisionKey, () => true), saved); assert.deepEqual(await requests[0].clone().json(), command.body);
  assert.deepEqual(await gateway.recoverDecision(actor, decisionKey), { state: 'committed', result: saved });
  const values = new Map<string, string>(), storage = { get length() { return values.size; }, key: (index: number) => [...values.keys()][index] ?? null,
    getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); }, removeItem: (key: string) => { values.delete(key); } };
  const journal = new OperationJournal(() => storage), entry = journal.remember(actor, 'fulfillment.case.decide', decisionKey), legacy = { lookup: async () => { throw new Error('wrong owner'); }, close: async () => { throw new Error('must not close'); } };
  assert.ok(!JSON.stringify([...values.values()]).includes('Catatan')); assert.equal(operationRecoveryCapabilities(entry.operation, { 'fulfillment.case': { lookup: async () => null } }).canClose, false);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'fulfillment.case': { lookup: async () => ({ state: 'committed', result }) } })); assert.equal(journal.list(actor).length, 1);
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, { 'fulfillment.case': { lookup: async () => null } })).state, 'unconfirmed'); assert.equal(journal.list(actor).length, 1);
  await reconcileOperationRecovery(entry, journal, legacy, { 'fulfillment.case': { lookup: async () => ({ state: 'committed', result: saved }) } }); assert.equal(journal.list(actor).length, 0);
});
