import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createReturnGateway, prepareReturn, parseReturnView, parseReturnResult } from '../src/modules/fulfillment/return-client';
import { OperationJournal, reconcileOperationRecovery, operationRecoveryCapabilities } from '../src/modules/administration/operation-journal';

const actor = '9007199254740993', invoiceId = '9007199254740994', caseId = '9223372036854775807', key = 'cr.' + caseId + '.original';
const source = { invoiceId, binding: 'a'.repeat(64), number: '100/103/2026/1', buyerName: 'Cabang Tebet', sellerName: 'Omi', issuedAt: '2026-10-01T03:00:00Z',
  lines: [{ orderId: '91', shipmentId: '92', receiptId: '93', skuId: '94', skuCode: 'ATK01', productName: 'Kertas', packageLabel: 'Karton', baseUnitsPerPackage: 12, baseUnitCode: 'rim', acceptedBaseUnits: '6' }] };
const reported = [{ receiptId: '93', skuId: '94', reportedBaseUnits: '2' }];
const owner = { id: caseId, revision: '2' as const, requestedAt: '2026-10-01T03:01:00Z', state: 'return_authorized' as const, issue: 'damaged' as const, reportedProblem: 'Barang rusak', source, lines: reported,
  decision: { caseId, invoiceId, revision: '2' as const, binding: 'b'.repeat(64), state: 'return_authorized' as const, decidedAt: '2026-10-01T03:02:00Z', publicExplanation: 'Pengembalian untuk pemeriksaan', lines: reported } };
const preview = { caseId, invoiceId, revision: '0', binding: 'c'.repeat(64), limits: [{ receiptId: '93', skuId: '94', warehouseId: '112', authorizedBaseUnits: '2', receivedBaseUnits: '0' }], recent: [], hasEarlier: false };
const lines = [{ receiptId: '93', skuId: '94', receivedBaseUnits: '1', observedCondition: 'opened' as const }];
const result = { returnId: '9007199254740996', caseId, invoiceId, revision: '1', receivedAt: '2026-10-01T03:03:00Z', state: 'quarantined' as const, operation: 'fulfillment.case.return_receive' as const };
test('returned units preserve original packaging, exact ownership, remaining approved capacity and observed condition without financial or lot inference', () => {
  const command = prepareReturn(actor, owner, preview, lines, 'Satu barang tiba', 'Catatan petugas'); assert.ok(Object.isFrozen(command.body.lines[0])); assert.equal(command.actorId, actor); assert.equal(command.revision, '0');
  assert.deepEqual(parseReturnResult(result, key), result); assert.deepEqual(parseReturnView(preview, owner), preview);
  for (const receivedBaseUnits of ['0', '3', '1000001', '01', '1.0']) assert.throws(() => prepareReturn(actor, owner, preview, [{ ...lines[0], receivedBaseUnits }], 'Barang tiba', 'Catatan'));
  for (const changed of [{ ...preview, caseId: '1' }, { ...preview, invoiceId: '1' }, { ...preview, ...{ amount: '1' } }, { ...preview, limits: [{ ...preview.limits[0], authorizedBaseUnits: '3' }] }, { ...preview, revision: '1' }, { ...preview, hasEarlier: true }]) assert.throws(() => parseReturnView(changed, owner));
  assert.throws(() => prepareReturn(actor, owner, preview, [...lines, ...lines], 'Barang tiba', 'Catatan'));
  assert.throws(() => prepareReturn(actor, owner, preview, [{ ...lines[0], ...{ lotId: '123' } }], 'Barang tiba', 'Catatan'));
  for (const observedCondition of ['restock', 'refunded', 'eligible']) assert.throws(() => prepareReturn(actor, owner, preview, [{ ...lines[0], observedCondition } as typeof lines[0]], 'Barang tiba', 'Catatan'));
  const after = { ...preview, revision: '1', limits: [{ ...preview.limits[0], receivedBaseUnits: '1' }], recent: [{ id: result.returnId, revision: '1', receivedAt: result.receivedAt, publicNote: 'Satu barang tiba', lines }] };
  assert.deepEqual(parseReturnView(after, owner), after); assert.throws(() => prepareReturn(actor, owner, after, [{ ...lines[0], receivedBaseUnits: '2' }], 'Barang tiba', 'Catatan'));
  for (const changed of [{ ...result, caseId: '1' }, { ...result, returnId: 9007199254740996 }, { ...result, state: 'available' }, { ...result, privateNote: 'privat' }]) assert.throws(() => parseReturnResult(changed, key));
});
test('physical return sends frozen actor and exact revision; stale views are unsent and mismatched revision stays unresolved', async () => {
  const requests: Request[] = []; let wrong = false;
  const gateway = createReturnGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' }); requests.push(request);
    if (request.url.endsWith('/operations/query')) return Response.json({ status: 'committed', result });
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(request.headers.get('If-Match'), '"0"'); assert.equal(request.headers.get('Idempotency-Key'), key); return Response.json({ ...result, revision: wrong ? '2' : '1' });
  } })); const command = prepareReturn(actor, owner, preview, lines, 'Satu barang tiba', 'Catatan petugas');
  await assert.rejects(() => gateway.receive(command, key, () => false)); assert.equal(requests.length, 0);
  assert.deepEqual(await gateway.receive(command, key, () => true), result); assert.deepEqual(await requests[0].clone().json(), command.body);
  wrong = true; await assert.rejects(() => gateway.receive(command, key, () => true)); assert.deepEqual(await gateway.recover(actor, key), { state: 'committed', result });
});
test('return recovery uses the original custody owner, rejects mixed receipts and retains only metadata without close or fallback', async () => {
  const values = new Map<string, string>(), storage = { get length() { return values.size; }, key: (index: number) => [...values.keys()][index] ?? null, getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); }, removeItem: (key: string) => { values.delete(key); } };
  const journal = new OperationJournal(() => storage), entry = journal.remember(actor, result.operation, key), legacy = { lookup: async () => { throw new Error('wrong owner'); }, close: async () => { throw new Error('must not close'); } };
  const absent = { 'fulfillment.case': { lookup: async () => null } }; assert.equal(operationRecoveryCapabilities(entry.operation, absent).canClose, false);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, absent, 'close')); assert.equal((await reconcileOperationRecovery(entry, journal, legacy, absent)).state, 'unconfirmed'); assert.equal(journal.list(actor).length, 1);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'fulfillment.case': { lookup: async () => ({ state: 'committed', result: { ...result, operation: 'fulfillment.case.decide' as const } as never }) } })); assert.equal(journal.list(actor).length, 1);
  await reconcileOperationRecovery(entry, journal, legacy, { 'fulfillment.case': { lookup: async () => ({ state: 'committed', result }) } }); assert.equal(journal.list(actor).length, 0);
});
