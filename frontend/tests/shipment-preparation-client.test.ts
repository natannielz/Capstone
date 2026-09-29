import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createPreparationGateway, parseFulfillmentOrder, parsePreparation, prepareShipmentCommand } from '../src/modules/fulfillment/preparation-client';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
import { identityReturnPath } from '../src/modules/identity/identity-workflow';
import { actorId, sku, definition } from './catalog-fixtures';

const source = { orderId: '9007199254741001', suborderId: '9007199254741002', revision: '3', review: 'a'.repeat(64), buyerName: 'Cabang fixture',
  delivery: { recipientName: 'Penerima fixture', phone: '081234567890', address: 'Alamat fixture', city: 'Jakarta', postalCode: '12810', instructions: null },
  lines: [{ skuId: sku.id, publicId: sku.publicId, skuCode: sku.code, productName: 'Pena biru', definition: { ...definition, minimumOrderPackages: 2, orderIncrementPackages: 2 }, orderedPackages: 2, remainingPackages: 2 }],
  canPrepare: true, orderedAt: definition.effectiveAt };
const lines = [{ skuId: sku.id, packages: 1 }];
const result = { preparationId: '9007199254741010', versionId: '9007199254741011', revision: '1', orderId: source.orderId, suborderId: source.suborderId, recordedAt: definition.effectiveAt };
const saved = { id: result.preparationId, revision: result.revision, acceptedSource: source, lines, currentSourceMatches: true, recordedAt: result.recordedAt };
const command = () => prepareShipmentCommand(actorId, parseFulfillmentOrder(source), '0', lines, 'Kemasan diperiksa');
function gateway(handler: (request: Request) => Promise<Response>) { return createPreparationGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
  assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
  assert.equal(request.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); return handler(request);
} })); }

test('preparation preserves accepted package meaning and allows deliberate partial fulfillment below purchase minimum', () => {
  const prepared = command(); assert.equal(prepared.lines[0].packages, 1); assert.equal(source.lines[0].definition.baseUnitsPerPackage, 12);
  assert.ok(Object.isFrozen(prepared) && Object.isFrozen(prepared.lines) && Object.isFrozen(prepared.lines[0]));
  assert.equal(parsePreparation(saved)?.acceptedSource.lines[0].definition.minimumOrderPackages, 2);
  for (const selections of [[], [{ skuId: sku.id, packages: 3 }], [{ skuId: sku.id, packages: 1.5 }], [...lines, ...lines], [{ skuId: '8', packages: 1 }]])
    assert.throws(() => prepareShipmentCommand(actorId, source, '0', selections, 'Review'));
  for (const revision of ['01', '-1', '1.0', '9223372036854775807']) assert.throws(() => prepareShipmentCommand(actorId, source, revision, lines, 'Review'));
  assert.throws(() => prepareShipmentCommand(actorId, { ...source, canPrepare: false }, '0', lines, 'Review'));
  assert.throws(() => parseFulfillmentOrder({ ...source, lines: [source.lines[0], source.lines[0]] }));
  assert.throws(() => parseFulfillmentOrder({ ...source, lines: [{ ...source.lines[0], remainingPackages: 3 }] }));
  assert.throws(() => parsePreparation({ ...saved, lines: [{ skuId: sku.id, packages: 3 }] }));
  assert.equal(parsePreparation({ ...saved, currentSourceMatches: false })?.currentSourceMatches, false);
});

test('preparation reads bind target and enforce descending bounded queue without borrowing commercial authority', async () => {
  const page = await gateway(async request => { assert.ok(request.url.endsWith('/sources/query')); assert.deepEqual(await request.json(), { purpose: 'shipment-preparation', beforeId: null, limit: 25 });
    return Response.json({ items: [source], hasMore: false }); }).list(actorId);
  assert.equal(page.items[0].suborderId, source.suborderId);
  for (const response of [{ items: [source, source], hasMore: false }, { items: [source], hasMore: true }, { items: [{ ...source, canPrepare: false }], hasMore: false }])
    await assert.rejects(() => gateway(async () => Response.json(response)).list(actorId));
  await assert.rejects(() => gateway(async () => Response.json({ items: [source], hasMore: false })).list(actorId, source.suborderId));
  await assert.rejects(() => gateway(async () => Response.json(source)).source(actorId, '7'));
  await assert.rejects(() => gateway(async () => Response.json({ preparation: saved })).read(actorId, '7'));
  assert.equal(await gateway(async () => Response.json({ preparation: null })).read(actorId, source.suborderId), null);
});

test('save sends only selected quantities and exact revision once; stale view and lost response cannot resend', async () => {
  let calls = 0; const client = gateway(async request => { calls++; assert.ok(request.url.endsWith('/' + source.suborderId));
    assert.equal(request.headers.get('If-Match'), '"0"'); assert.equal(request.headers.get('Idempotency-Key'), 'original');
    assert.deepEqual(await request.json(), { review: source.review, lines, reason: 'Kemasan diperiksa' }); throw new Error('Lost committed response'); });
  await assert.rejects(() => client.save(command(), 'original', () => false)); assert.equal(calls, 0);
  await assert.rejects(() => client.save(command(), 'original', () => true)); assert.equal(calls, 1);
  await assert.rejects(() => gateway(async () => Response.json({ ...result, revision: '2' })).save(command(), 'original', () => true));
  let current = true;
  await assert.rejects(() => gateway(async () => { current = false; return Response.json(result); }).save(command(), 'original', () => current), { code: 'commit_outcome_unknown' });
});

test('preparation recovery keeps unknown or invalid outcomes pending, has no legacy fallback or close, and respects account changes', async () => {
  assert.equal(identityReturnPath('/fulfillment/preparations'), '/fulfillment/preparations'); assert.equal(recoveryPage('fulfillment.preparation.save').href, '/fulfillment/preparations');
  const storage = new Map<string, string>(); const journal = new OperationJournal(() => ({ get length() { return storage.size; }, key: index => [...storage.keys()][index] ?? null,
    getItem: key => storage.get(key) ?? null, setItem: (key, value) => { storage.set(key, value); }, removeItem: key => { storage.delete(key); } }));
  const entry = journal.remember(actorId, 'fulfillment.preparation.save', 'original');
  assert.deepEqual(Object.keys(JSON.parse([...storage.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  const legacy = { lookup: async () => { throw new Error('Legacy lookup'); }, close: async () => { throw new Error('Legacy close'); } };
  assert.deepEqual(operationRecoveryCapabilities(entry.operation), { canCheck: false, canClose: false, unavailable: 'adapter' });
  const pending = { 'fulfillment.preparation': { lookup: async () => null } };
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, pending), { state: 'unconfirmed' });
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, pending, 'Close'), { code: 'recovery_close_unsupported' });
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'fulfillment.preparation': { lookup: async () => ({ state: 'committed', result: { ...result, suborderId: '' } }) } }));
  assert.equal(journal.list(actorId).length, 1);
  const receipt = await gateway(async request => { assert.ok(request.url.endsWith('/operations/query')); assert.deepEqual(await request.json(), { purpose: 'operation-recovery', key: 'original' });
    return Response.json({ status: 'committed', result }); }).recover(actorId, 'original');
  let current = true;
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'fulfillment.preparation': { lookup: async () => { current = false; return receipt; } } }, undefined, () => current));
  assert.equal(journal.list(actorId).length, 1);
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'fulfillment.preparation': { lookup: async () => receipt } }), { state: 'committed', result: { id: result.preparationId, version: '1' } });
  assert.equal(journal.list(actorId).length, 0);
});
