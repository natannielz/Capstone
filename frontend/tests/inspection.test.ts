import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createInspectionGateway, prepareInspection, parseInspectionView, parseInspectionResult } from '../src/modules/fulfillment/inspection-client';
import { OperationJournal, reconcileOperationRecovery, operationRecoveryCapabilities } from '../src/modules/administration/operation-journal';

const actor = '9007199254740993', invoiceId = '9007199254740994', caseId = '9223372036854775807', returnId = '9007199254740996', key = 'ci.' + caseId + '.' + returnId + '.original';
const source = { invoiceId, binding: 'a'.repeat(64), number: '100/103/2026/1', buyerName: 'Cabang Tebet', sellerName: 'Omi', issuedAt: '2026-10-01T03:00:00Z',
  lines: [{ orderId: '91', shipmentId: '92', receiptId: '93', skuId: '94', skuCode: 'ATK01', productName: 'Kertas', packageLabel: 'Karton', baseUnitsPerPackage: 12, baseUnitCode: 'rim', acceptedBaseUnits: '6' }] };
const reported = [{ receiptId: '93', skuId: '94', reportedBaseUnits: '2' }];
const owner = { id: caseId, revision: '2' as const, requestedAt: '2026-10-01T03:01:00Z', state: 'return_authorized' as const, issue: 'damaged' as const, reportedProblem: 'Barang rusak', source, lines: reported,
  decision: { caseId, invoiceId, revision: '2' as const, binding: 'b'.repeat(64), state: 'return_authorized' as const, decidedAt: '2026-10-01T03:02:00Z', publicExplanation: 'Pengembalian untuk pemeriksaan', lines: reported } };
const receipt = { id: returnId, revision: '1', receivedAt: '2026-10-01T03:03:00Z', publicNote: 'Satu barang tiba', lines: [{ receiptId: '93', skuId: '94', receivedBaseUnits: '1', observedCondition: 'opened' as const }] };
const preview = { caseId, invoiceId, revision: '0' as const, binding: 'c'.repeat(64), receipt, inspection: null };
const lines = [{ receiptId: '93', skuId: '94', inspectedBaseUnits: '1', identity: 'unverified' as const, condition: 'mixed' as const, observedIdentity: 'Label diperiksa privat', observedLotLabel: null }];
const result = { returnId, caseId, invoiceId, revision: '1' as const, inspectedAt: '2026-10-01T03:04:00Z', state: 'inspected_quarantine' as const, operation: 'fulfillment.case.return_inspect' as const };
test('inspection binds exact physical intake and explicit assessment, rejects packaging rounding, invented stock lineage and public private observations', () => {
  const command = prepareInspection(actor, owner, preview, lines, 'Barang diperiksa, identitas belum pasti', 'Catatan internal'); assert.ok(Object.isFrozen(command.body.lines[0])); assert.equal(command.body.lines[0].inspectedBaseUnits, '1'); assert.equal(command.body.lines[0].identity, 'unverified');
  assert.deepEqual(parseInspectionView(preview, owner, returnId), preview); assert.deepEqual(parseInspectionResult(result, key), result);
  for (const changed of [{ ...preview, caseId: '1' }, { ...preview, invoiceId: '1' }, { ...preview, revision: '1' }, { ...preview, receipt: { ...receipt, id: '1' } }, { ...preview, originalLotId: '123' }]) assert.throws(() => parseInspectionView(changed, owner, returnId));
  for (const inspectedBaseUnits of ['0', '2', '12', '01', '1.0', '1000001']) assert.throws(() => prepareInspection(actor, owner, preview, [{ ...lines[0], inspectedBaseUnits }], 'Periksa', 'Catatan'));
  assert.throws(() => prepareInspection(actor, owner, preview, [...lines, ...lines], 'Periksa', 'Catatan')); assert.throws(() => prepareInspection(actor, owner, preview, [{ ...lines[0], ...{ cost: '1', originalLotId: '123' } }], 'Periksa', 'Catatan'));
  const assessed = { ...preview, revision: '1', inspection: { inspectedAt: result.inspectedAt, publicNote: 'Barang diperiksa', lines: [{ receiptId: '93', skuId: '94', inspectedBaseUnits: '1', identity: 'matched', condition: 'opened' }] } };
  assert.deepEqual(parseInspectionView(assessed, owner, returnId), assessed); assert.throws(() => parseInspectionView({ ...assessed, inspection: { ...assessed.inspection, lines: [{ ...assessed.inspection.lines[0], observedIdentity: 'Privat' }] } }, owner, returnId));
  for (const changed of [{ ...result, returnId: '1' }, { ...result, caseId: '1' }, { ...result, operation: 'fulfillment.case.return_receive' }, { ...result, state: 'available' }, { ...result, refund: '1' }]) assert.throws(() => parseInspectionResult(changed, key));
});
test('inspection sends the frozen actor and intake precondition, remains unsent on stale context and only recovers its exact owner', async () => {
  const requests: Request[] = []; const gateway = createInspectionGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' }); requests.push(request);
    assert.ok(request.url.includes('/cases/' + caseId + '/returns/' + returnId + '/inspection')); assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf');
    if (request.url.endsWith('/operations/query')) return Response.json({ status: 'committed', result }); assert.equal(request.headers.get('If-Match'), '"0"'); assert.equal(request.headers.get('Idempotency-Key'), key); return Response.json(result);
  } })); const command = prepareInspection(actor, owner, preview, lines, 'Periksa', 'Privat');
  await assert.rejects(() => gateway.inspect(command, key, () => false)); assert.equal(requests.length, 0); assert.deepEqual(await gateway.inspect(command, key, () => true), result); assert.deepEqual(await requests[0].clone().json(), command.body);
  assert.deepEqual(await gateway.recover(actor, key), { state: 'committed', result }); await assert.rejects(() => gateway.inspect(command, 'ci.1.' + returnId + '.wrong', () => true)); assert.equal(requests.length, 2);
});
test('inspection recovery keeps metadata and rejects intake receipts, fallback and manual close', async () => {
  const values = new Map<string, string>(), storage = { get length() { return values.size; }, key: (index: number) => [...values.keys()][index] ?? null, getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); }, removeItem: (key: string) => { values.delete(key); } };
  const journal = new OperationJournal(() => storage), entry = journal.remember(actor, result.operation, key), legacy = { lookup: async () => { throw new Error('wrong owner'); }, close: async () => { throw new Error('must not close'); } };
  const absent = { 'fulfillment.case': { lookup: async () => null } }; assert.equal(operationRecoveryCapabilities(entry.operation, absent).canClose, false); await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, absent, 'close'));
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, absent)).state, 'unconfirmed'); assert.equal(journal.list(actor).length, 1);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'fulfillment.case': { lookup: async () => ({ state: 'committed', result: { ...result, operation: 'fulfillment.case.return_receive' as const } as never }) } })); assert.equal(journal.list(actor).length, 1);
  await reconcileOperationRecovery(entry, journal, legacy, { 'fulfillment.case': { lookup: async () => ({ state: 'committed', result }) } }); assert.equal(journal.list(actor).length, 0);
});
