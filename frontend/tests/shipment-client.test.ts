import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createShipmentGateway, parseShipment } from '../src/modules/fulfillment/shipment-client';
import { identityReturnPath } from '../src/modules/identity/identity-workflow';
import { actorId, sku, definition } from './catalog-fixtures';

const shipment = { id: '9007199254741090', revision: '1', orderId: '9007199254741001', suborderId: '9007199254741002', numberEntity: '100', numberChannel: '103', numberYear: '2026', number: '9007199254741021',
  dispatchedAt: definition.effectiveAt, buyerName: 'Cabang fixture', courierId: '9100', courierName: 'Kurir fixture',
  delivery: { recipientName: 'Penerima', phone: '081234567890', address: 'Alamat fixture', city: 'Jakarta', postalCode: null, instructions: null },
  lines: [{ skuId: sku.id, skuCode: sku.code, productName: 'Pena biru', definition, packages: 1, baseUnits: String(definition.baseUnitsPerPackage) }] };
test('shipment views retain exact dispatched packaging and lossless document references', () => {
  const parsed = parseShipment(shipment); assert.equal(parsed.id, shipment.id); assert.equal(parsed.number, shipment.number); assert.equal(parsed.lines[0].packages, 1);
  for (const patch of [{ packages: 0 }, { packages: 1.5 }, { baseUnits: '0' }, { baseUnits: '999' }, { skuId: '99' }])
    assert.throws(() => parseShipment({ ...shipment, lines: [{ ...shipment.lines[0], ...patch }] }));
  assert.throws(() => parseShipment({ ...shipment, lines: [...shipment.lines, ...shipment.lines] }));
  assert.throws(() => parseShipment({ ...shipment, revision: '2' })); assert.throws(() => parseShipment({ ...shipment, id: Number(shipment.id) }));
  assert.equal(identityReturnPath('/fulfillment/shipments'), '/fulfillment/shipments');
});
test('shipment clients bind current account and detail identity, and reject duplicate or unordered pages', async () => {
  let body: unknown;
  const gateway = (response: unknown) => createShipmentGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); body = await request.json(); return Response.json(response);
  } }));
  assert.equal((await gateway(shipment).read(actorId, shipment.id)).id, shipment.id); assert.deepEqual(body, { purpose: 'shipment-read' });
  await assert.rejects(() => gateway(shipment).read(actorId, '99'));
  assert.equal((await gateway({ items: [shipment], hasMore: false }).list(actorId)).items.length, 1);
  assert.deepEqual(body, { purpose: 'shipment-read', beforeId: null, limit: 25 });
  await assert.rejects(() => gateway({ items: [shipment], hasMore: false }).list(actorId, shipment.id));
  await assert.rejects(() => gateway({ items: [shipment, shipment], hasMore: false }).list(actorId));
  await assert.rejects(() => gateway({ items: [shipment], hasMore: true }).list(actorId));
});
