import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createDispatchGateway, parseDispatchPreparation, parseDispatchReview, prepareDispatchCommand } from '../src/modules/fulfillment/dispatch-client';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
import { identityReturnPath } from '../src/modules/identity/identity-workflow';
import { actorId, sku, definition } from './catalog-fixtures';

const source = { orderId: '9007199254741001', suborderId: '9007199254741002', revision: '3', review: 'a'.repeat(64), buyerName: 'Cabang fixture',
  delivery: { recipientName: 'Penerima fixture', phone: '081234567890', address: 'Alamat fixture', city: 'Jakarta', postalCode: '12810', instructions: null },
  lines: [{ skuId: sku.id, lineRevision: '0', publicId: sku.publicId, skuCode: sku.code, productName: 'Pena biru', definition, orderedPackages: 2, remainingPackages: 2 }], canPrepare: true, orderedAt: definition.effectiveAt };
const prepared = { preparationId: '9007199254741010', preparationRevision: '1', source, lines: [{ skuId: sku.id, lineRevision: '0', packages: 1 }],
  lots: [{ reservationId: '9007199254741011', skuId: sku.id, lineRevision: '0', definitionId: definition.id, lotId: '202', remainingPackages: 2, expiresOn: null }] };
const review = { preparation: prepared, courierId: '9100', courierName: 'Kurir fixture', selected: [{ reservationId: prepared.lots[0].reservationId, packages: 1 }], review: 'b'.repeat(64) };
const result = { shipmentId: '9007199254741020', revision: '1', orderId: source.orderId, suborderId: source.suborderId, recordedAt: definition.effectiveAt,
  numberEntity: '100', numberChannel: '103', numberYear: '2026', number: '9007199254741021' };
const command = () => prepareDispatchCommand(actorId, parseDispatchReview(review), 'Barang diperiksa');
function gateway(handler: (request: Request) => Promise<Response>) { return createDispatchGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
  assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
  assert.equal(request.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); return handler(request);
} })); }

test('dispatch confirms complete packages against explicit reserved lots and keeps numeric identities exact', () => {
  const accepted = command(); assert.equal(accepted.selection.lots[0].packages, 1); assert.equal(accepted.orderId, source.orderId);
  assert.ok(Object.isFrozen(accepted) && Object.isFrozen(accepted.selection) && Object.isFrozen(accepted.selection.lots) && Object.isFrozen(accepted.selection.lots[0]));
  for (const selected of [[], [...review.selected, ...review.selected], [{ ...review.selected[0], packages: 2 }], [{ ...review.selected[0], packages: 1.5 }], [{ reservationId: '6', packages: 1 }]])
    assert.throws(() => parseDispatchReview({ ...review, selected }));
  assert.throws(() => parseDispatchPreparation({ ...prepared, lots: [{ ...prepared.lots[0], remainingPackages: 1 }] }));
  assert.throws(() => parseDispatchPreparation({ ...prepared, lots: [{ ...prepared.lots[0], expiresOn: '2026-02-30' }] }));
  assert.throws(() => parseDispatchPreparation({ ...prepared, source: { ...source, canPrepare: false } }));
  assert.throws(() => prepareDispatchCommand(actorId, review, ''));
});
test('dispatch preview binds the preparation and exact lot choices and writes once with original recovery reference', async () => {
  const preview = await gateway(async request => { assert.ok(request.url.endsWith('/preview/query')); const body = await request.json() as { selection: { lots: unknown } };
    assert.deepEqual(body.selection.lots, review.selected); return Response.json(review); }).preview(actorId, prepared, review.courierId, review.selected);
  assert.equal(preview.review, review.review);
  await assert.rejects(() => gateway(async () => Response.json({ ...review, courierId: '99' })).preview(actorId, prepared, review.courierId, review.selected));
  let calls = 0;
  const client = gateway(async request => { calls++; assert.equal(request.headers.get('If-Match'), '"1"'); assert.equal(request.headers.get('Idempotency-Key'), 'dispatch-one');
    assert.deepEqual(await request.json(), { selection: command().selection, review: review.review, reason: 'Barang diperiksa' }); throw new Error('Lost committed response'); });
  await assert.rejects(() => client.dispatch(command(), 'dispatch-one', () => false)); assert.equal(calls, 0);
  await assert.rejects(() => client.dispatch(command(), 'dispatch-one', () => true)); assert.equal(calls, 1);
  await assert.rejects(() => gateway(async () => Response.json({ ...result, suborderId: '7' })).dispatch(command(), 'dispatch-one', () => true));
  await assert.rejects(() => gateway(async () => Response.json({ items: [{ ...prepared, canDispatch: true }], hasMore: false })).list(actorId, prepared.preparationId));
  await assert.rejects(() => gateway(async () => Response.json({ items: [{ userId: '9100', displayName: 'Kurir' }, { userId: '9100', displayName: 'Kurir' }], hasMore: false })).couriers(actorId, source.suborderId));
});
test('dispatch recovery never closes unknown outcomes or borrows legacy retry authority', async () => {
  assert.equal(identityReturnPath('/fulfillment/dispatches'), '/fulfillment/dispatches'); assert.equal(recoveryPage('fulfillment.shipment.dispatch').href, '/fulfillment/dispatches');
  const storage = new Map<string, string>(); const journal = new OperationJournal(() => ({ get length() { return storage.size; }, key: index => [...storage.keys()][index] ?? null,
    getItem: key => storage.get(key) ?? null, setItem: (key, value) => { storage.set(key, value); }, removeItem: key => { storage.delete(key); } }));
  const entry = journal.remember(actorId, 'fulfillment.shipment.dispatch', 'original');
  assert.deepEqual(Object.keys(JSON.parse([...storage.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  const legacy = { lookup: async () => { throw new Error('Legacy lookup'); }, close: async () => { throw new Error('Legacy close'); } };
  assert.deepEqual(operationRecoveryCapabilities(entry.operation), { canCheck: false, canClose: false, unavailable: 'adapter' });
  const pending = { 'fulfillment.shipment': { lookup: async () => null } };
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, pending), { state: 'unconfirmed' });
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, pending, 'Close'), { code: 'recovery_close_unsupported' });
  const receipt = await gateway(async () => Response.json({ status: 'committed', result })).recover(actorId, 'original');
  let current = true;
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'fulfillment.shipment': { lookup: async () => { current = false; return receipt; } } }, undefined, () => current));
  assert.equal(journal.list(actorId).length, 1);
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'fulfillment.shipment': { lookup: async () => receipt } }), { state: 'committed', result: { id: result.shipmentId, version: '1' } });
  assert.equal(journal.list(actorId).length, 0);
});
