import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createOrderSubmitGateway, parseCheckoutReview, parseSubmittedOrder } from '../src/modules/ordering/order-submit-client';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
import { identityReturnPath } from '../src/modules/identity/identity-workflow';
import { actorId, family, sku, definition } from './catalog-fixtures';
const input = { context: { organizationId: '10', branchId: '11', divisionId: null }, selections: [
  { skuId: sku.id, definitionId: definition.id, definitionVersion: definition.version, priceId: '9007199254740999', priceVersion: '2', packages: 2 } ],
  delivery: { recipientName: 'Penerima fixture', phone: '081234567890', address: 'Alamat fixture', city: 'Jakarta', postalCode: '12810', instructions: null }, purchaseReference: null };
const review = { input, review: 'a'.repeat(64), calculatedAt: definition.effectiveAt, currency: 'IDR', catalogSubtotal: '24000.50', tax: '0.00', charges: '1500.00', total: '25500.50', spendingState: 'Pending',
  lines: [{ familyId: family.id, familyName: family.name, publicId: sku.publicId, skuCode: sku.code, definition,
    price: { id: input.selections[0].priceId, skuId: sku.id, version: '2', currency: 'IDR', unitPrice: '12000.25', effectiveAt: definition.effectiveAt },
    packages: 2, baseUnits: '24', catalogSubtotal: '24000.50' }] };
const result = { orderId: '9007199254741001', revision: '1', suborderIds: ['9007199254741002'], recordedAt: definition.effectiveAt, spendingState: 'Pending' as const };
function gateway(handler: (request: Request) => Promise<Response>) { return createOrderSubmitGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
  assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
  assert.equal(request.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); return handler(request);
} })); }
test('checkout review binds exact branch, delivery, packaging, approved prices and checked decimal total', () => {
  const parsed = parseCheckoutReview(review, input); assert.equal(parsed.total, '25500.50'); assert.equal(parsed.quote.lines[0].baseUnits, '24');
  for (const change of [ { ...review, input: { ...input, context: { ...input.context, branchId: '12' } } },
    { ...review, input: { ...input, delivery: { ...input.delivery, address: 'Another address' } } }, { ...review, total: '0.00' }, { ...review, tax: null },
    { ...review, review: 'a'.repeat(63) }, { ...review, spendingState: 'Approved' }, { ...review, lines: [{ ...review.lines[0], packages: 3 }] } ])
    assert.throws(() => parseCheckoutReview(change, input));
  assert.equal(parseSubmittedOrder({ ...review, submission: result, progress: { revision: '1', spendingState: 'Pending', acceptedSuborders: [], rejectedSuborders: [], cancelled: false } }).submission.orderId, result.orderId);
  assert.equal(parseSubmittedOrder({ ...review, submission: result, progress: { revision: '2', spendingState: 'Rejected', acceptedSuborders: [], rejectedSuborders: [], cancelled: false } }).progress.spendingState, 'Rejected');
  assert.throws(() => parseSubmittedOrder({ ...review, submission: result, progress: { revision: '3', spendingState: 'Rejected', acceptedSuborders: [], rejectedSuborders: [], cancelled: true } }));
});
test('order transport sends only accepted references and never retries a lost submission or sends from a stale view', async () => {
  let calls = 0; const checked = parseCheckoutReview(review, input);
  const client = gateway(async request => { calls++; assert.equal(request.headers.get('Idempotency-Key'), 'original'); assert.equal(request.headers.get('If-Match'), '"0"');
    const body = await request.json() as Record<string, unknown>; assert.deepEqual(body.input, input); assert.equal(body.review, review.review); assert.equal('total' in body, false);
    throw new Error('Lost response'); });
  await assert.rejects(() => client.submit(actorId, checked, 'original', () => false)); assert.equal(calls, 0);
  await assert.rejects(() => client.submit(actorId, checked, 'original', () => true)); assert.equal(calls, 1);
  await assert.rejects(() => gateway(async () => Response.json({ ...review, submission: result })).read(actorId, '7'));
});
test('order recovery is lookup-only, preserves unknown outcomes and rejects an incomplete receipt', async () => {
  assert.equal(identityReturnPath('/ordering/checkout'), '/ordering/checkout'); assert.equal(recoveryPage('ordering.order.submit').href, '/ordering/checkout');
  const storage = new Map<string, string>(); const journal = new OperationJournal(() => ({ get length() { return storage.size; }, key: index => [...storage.keys()][index] ?? null,
    getItem: key => storage.get(key) ?? null, setItem: (key, value) => { storage.set(key, value); }, removeItem: key => { storage.delete(key); } }));
  const entry = journal.remember(actorId, 'ordering.order.submit', 'original'); assert.deepEqual(Object.keys(JSON.parse([...storage.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  const legacy = { lookup: async () => { throw new Error('Legacy lookup'); }, close: async () => { throw new Error('Legacy close'); } };
  assert.deepEqual(operationRecoveryCapabilities(entry.operation), { canCheck: false, canClose: false, unavailable: 'adapter' });
  const pending = { 'ordering.order': { lookup: async () => null } };
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, pending), { state: 'unconfirmed' }); assert.equal(journal.list(actorId).length, 1);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, pending, 'Close'), { code: 'recovery_close_unsupported' });
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'ordering.order': { lookup: async () => ({ state: 'committed', result: { ...result, suborderIds: [] } }) } }));
  assert.equal(journal.list(actorId).length, 1);
  const receipt = await gateway(async request => { assert.ok(request.url.endsWith('/operations/query')); assert.deepEqual(await request.json(), { purpose: 'operation-recovery', key: 'original' }); return Response.json({ status: 'committed', result }); }).recover(actorId, 'original');
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'ordering.order': { lookup: async () => receipt } }), { state: 'committed', result: { id: result.orderId, version: '1' } });
  assert.equal(journal.list(actorId).length, 0);
});
