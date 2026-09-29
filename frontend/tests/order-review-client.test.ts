import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createOrderReviewGateway, parseOrderReview, parseOrderDecisionResult } from '../src/modules/ordering/order-review-client';
import { OperationJournal, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';
import { actorId, family, sku, definition } from './catalog-fixtures';
const row = { orderId: '9007199254741001', suborderId: null, stage: 'Spending', revision: '1', review: 'a'.repeat(64),
  context: { organizationId: '10', branchId: '11', divisionId: null }, buyerName: 'Cabang fixture', orderedAt: definition.effectiveAt,
  delivery: { recipientName: 'Penerima fixture', phone: '081234567890', address: 'Alamat fixture', city: 'Jakarta', postalCode: '12810', instructions: null },
  currency: 'IDR', catalogSubtotal: '24000.50', tax: '0.00', charges: '1500.00', total: '25500.50', spendingState: 'Pending', storeState: null, canDecide: true,
  lines: [{ familyId: family.id, familyName: family.name, publicId: sku.publicId, skuCode: sku.code, definition,
    price: { id: '9007199254740999', skuId: sku.id, version: '2', currency: 'IDR', unitPrice: '12000.25', effectiveAt: definition.effectiveAt }, packages: 2, baseUnits: '24', catalogSubtotal: '24000.50' }] };
const result = { decisionId: '9007199254741010', orderId: row.orderId, suborderId: null, stage: 'Spending' as const, revision: '2', state: 'approved' as const, recordedAt: definition.effectiveAt };
function gateway(handler: (request: Request) => Promise<Response>) { return createOrderReviewGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
  assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
  assert.equal(request.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); return handler(request);
} })); }
test('review validates exact money and packaging while store projection excludes branch total and other stages', () => {
  assert.equal(parseOrderReview(row, 'Spending').total, '25500.50');
  for (const value of [{ ...row, total: '25500.51' }, { ...row, canDecide: false }, { ...row, lines: [{ ...row.lines[0], baseUnits: '2' }] },
    { ...row, stage: 'Store' }, { ...row, suborderId: '2' }, { ...row, revision: '103' }]) assert.throws(() => parseOrderReview(value, 'Spending'));
  const store = { ...row, stage: 'Store', suborderId: '7', total: null, tax: null, charges: null, storeState: 'awaitingSpending', canDecide: false };
  assert.equal(parseOrderReview(store, 'Store').total, null);
  assert.throws(() => parseOrderReview({ ...store, total: '25500.50' }, 'Store'));
  assert.throws(() => parseOrderReview({ ...store, storeState: 'accepted' }, 'Store'));
  assert.throws(() => parseOrderDecisionResult({ ...result, stage: 'Store' }));
});
test('decision transport binds reviewed revision, stage and target and sends no money or stock; unknown result never retries', async () => {
  const review = parseOrderReview(row, 'Spending'); let calls = 0;
  const client = gateway(async request => { calls++; assert.equal(request.headers.get('If-Match'), '"1"'); assert.equal(request.headers.get('Idempotency-Key'), 'decision-original');
    assert.deepEqual(await request.json(), { stage: 'Spending', suborderId: null, review: row.review, reason: 'Independent review' }); throw new Error('Lost committed response'); });
  await assert.rejects(() => client.decide(actorId, review, 'Independent review', 'decision-original', () => false)); assert.equal(calls, 0);
  await assert.rejects(() => client.decide(actorId, review, 'Independent review', 'decision-original', () => true)); assert.equal(calls, 1);
  await assert.rejects(() => gateway(async () => Response.json({ ...result, revision: '3' })).decide(actorId, review, 'Independent review', 'key', () => true));
});
test('dedicated decision recovery remains unknown without receipt, rejects cross-stage results and cannot close or resubmit', async () => {
  const storage = new Map<string, string>(); const journal = new OperationJournal(() => ({ get length() { return storage.size; }, key: index => [...storage.keys()][index] ?? null,
    getItem: key => storage.get(key) ?? null, setItem: (key, value) => { storage.set(key, value); }, removeItem: key => { storage.delete(key); } }));
  const entry = journal.remember(actorId, 'ordering.order.spending.approve', 'decision-original');
  assert.deepEqual(Object.keys(JSON.parse([...storage.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  const legacy = { lookup: async () => { throw new Error('Legacy lookup'); }, close: async () => { throw new Error('Legacy close'); } };
  const pending = { 'ordering.decision': { lookup: async () => null } };
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, pending), { state: 'unconfirmed' });
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, pending, 'Close'), { code: 'recovery_close_unsupported' });
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'ordering.decision': { lookup: async () => ({ state: 'committed', result: { ...result, stage: 'Store', suborderId: '7', state: 'accepted' } }) } }));
  assert.equal(journal.list(actorId).length, 1);
  const receipt = await gateway(async request => { assert.ok(request.url.endsWith('/operations/query')); assert.deepEqual(await request.json(), { purpose: 'operation-recovery', stage: 'Spending', key: 'decision-original' });
    return Response.json({ status: 'committed', result }); }).recover(actorId, 'Spending', 'decision-original');
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'ordering.decision': { lookup: async () => receipt } }), { state: 'committed', result: { id: row.orderId, version: '2' } });
  assert.equal(journal.list(actorId).length, 0);
});
