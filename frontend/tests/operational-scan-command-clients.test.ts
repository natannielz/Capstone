import test from 'node:test';
import assert from 'node:assert/strict';
import {createUnitTokoClient} from '../src/api/client';
import {createPreparationGateway, prepareShipmentCommand} from '../src/modules/fulfillment/preparation-client';
import {createDispatchGateway, prepareDispatchCommand} from '../src/modules/fulfillment/dispatch-client';
import {createStockCountGateway} from '../src/modules/inventory/stock-count-client';
import {prepareStockCount} from '../src/modules/inventory/stock-count-workflow';
import {createProcurementStockReceiptGateway, prepareStockReceipt} from '../src/modules/procurement/procurement-stock-receipt-client';
import {type OperationalScanSelection} from '../src/modules/inventory/scan-client';
import {actorId, sku, definition} from './catalog-fixtures';
import * as count from './stock-count-fixtures';
import {actor, location, target, order, observedLine, noticeId, intakeId} from './supplier-document-fixtures';

function proof(skuId: string, definitionId: string, definitionVersion: string): OperationalScanSelection {
  return {identifier: {identifierId: '901', identifierRevision: '2', skuId, definitionId, definitionVersion,
    kind: 'gtin', namespace: 'catalog:101:103:warehouse', canonicalValue: '04006381333931'}, source: null};
}
function client(handler: (request: Request) => Promise<Response>) {
  return createUnitTokoClient({baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request);
    if (request.url.endsWith('/csrf')) return Response.json({requestToken: 'csrf'});
    assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); return handler(request);
  }});
}
const source = {orderId: '9007199254741001', suborderId: '9007199254741002', revision: '3', review: 'a'.repeat(64), buyerName: 'Cabang fixture',
  delivery: {recipientName: 'Penerima fixture', phone: '081234567890', address: 'Alamat fixture', city: 'Jakarta', postalCode: '12810', instructions: null},
  lines: [{skuId: sku.id, publicId: sku.publicId, skuCode: sku.code, productName: 'Pena biru', definition, orderedPackages: 2, remainingPackages: 2}],
  canPrepare: true, orderedAt: definition.effectiveAt};
const selected = proof(sku.id, definition.id, definition.version);

test('picking sends the reviewed exact mapping with the original command key and leaves manual requests unchanged', async () => {
  const lines = [{skuId: sku.id, packages: 1}], scanned = prepareShipmentCommand(actorId, source, '0', lines, 'Review', [selected]);
  const manual = prepareShipmentCommand(actorId, source, '0', lines, 'Review');
  assert.equal(Object.hasOwn(manual, 'scanSelections'), false);
  assert.ok(Object.isFrozen(scanned.scanSelections) && Object.isFrozen(scanned.scanSelections![0].identifier));
  const sent: unknown[] = [];
  const gateway = createPreparationGateway(client(async request => {
    assert.equal(request.headers.get('Idempotency-Key'), 'scan-picking'); sent.push(await request.json());
    return Response.json({preparationId: '910', versionId: '911', revision: '1', orderId: source.orderId, suborderId: source.suborderId, recordedAt: definition.effectiveAt});
  }));
  await gateway.save(scanned, 'scan-picking', () => true); await gateway.save(manual, 'scan-picking', () => true);
  assert.deepEqual(sent, [{review: source.review, lines, reason: 'Review', scanSelections: [selected]}, {review: source.review, lines, reason: 'Review'}]);
  assert.throws(() => prepareShipmentCommand(actorId, source, '0', lines, 'Review', [proof(sku.id, '99', definition.version)]));
});

test('dispatch binds scan evidence to the selected reserved package without changing explicit lot quantities', async () => {
  const prepared = {preparationId: '910', preparationRevision: '1', source, lines: [{skuId: sku.id, packages: 1}],
    lots: [{reservationId: '920', skuId: sku.id, definitionId: definition.id, lotId: '202', remainingPackages: 2, expiresOn: null}]};
  const review = {preparation: prepared, courierId: '930', courierName: 'Kurir fixture', selected: [{reservationId: '920', packages: 1}], review: 'b'.repeat(64)};
  const scanned = prepareDispatchCommand(actorId, review, 'Review', [selected]), manual = prepareDispatchCommand(actorId, review, 'Review');
  assert.equal(Object.hasOwn(manual, 'scanSelections'), false); assert.deepEqual(scanned.selection, manual.selection);
  await createDispatchGateway(client(async request => {
    assert.equal(request.headers.get('Idempotency-Key'), 'scan-dispatch');
    assert.deepEqual(await request.json(), {selection: manual.selection, review: review.review, reason: 'Review', scanSelections: [selected]});
    return Response.json({shipmentId: '940', revision: '1', orderId: source.orderId, suborderId: source.suborderId, recordedAt: definition.effectiveAt,
      numberEntity: '100', numberChannel: '103', numberYear: '2026', number: '950'});
  })).dispatch(scanned, 'scan-dispatch', () => true);
  assert.throws(() => prepareDispatchCommand(actorId, review, 'Review', [proof(sku.id, definition.id, '99')]));
});

test('stock count retains the reviewed mapping in its frozen request while quantity zero stays explicit', async () => {
  const selected = proof(count.choice.skuId, count.choice.definitionId, count.choice.definitionVersion);
  const form = {packages: '0', reason: 'Physical count', observedLotLabel: ''};
  const scanned = prepareStockCount(count.actorId, count.location, count.choice, form, undefined, [selected]);
  const manual = prepareStockCount(count.actorId, count.location, count.choice, form);
  assert.equal(Object.hasOwn(manual.body, 'scanSelections'), false); assert.equal(scanned.body.packages, 0);
  await createStockCountGateway(client(async request => {
    assert.equal(request.headers.get('Idempotency-Key'), count.reference.key);
    assert.deepEqual(await request.json(), {...manual.body, scanSelections: [selected]}); return Response.json(count.observation);
  })).submit(scanned, count.reference.key);
  assert.throws(() => prepareStockCount(count.actorId, count.location, count.choice, form, undefined, [proof('99', count.choice.definitionId, '1')]));
});

test('receiving retains the approved source namespace/account and inspection line; unrelated source proof is refused before send', async () => {
  const inspectionId = '9007199254741001', input = {location, orderId: order.id, orderVersion: '1' as const, orderApprovalId: order.approvalId,
    inspectionId, inspectionLineNumber: 1, packages: 2, poolId: '800', poolRevision: '1', receiptReference: '000RECEIPT-a', lotReference: observedLine.lotCode,
    expiresOn: observedLine.expiryDate, reason: 'Penerimaan fisik tetap ditahan dan belum dinilai'};
  const capacity = {basis: 'physical_received' as const, inspectionId, inspectionLineNumber: 1, inspectionHash: 'a'.repeat(64), headId: '810', version: '1', totalPackages: '3', occupiedPackages: '1', remainingPackages: '2'};
  const inspection = {id: inspectionId, version: '1' as const, hash: capacity.inspectionHash, inspectionDate: '2026-10-03', inspectionReference: '000INSPECTION-a', noticeId, intakeId, lineNumber: 1, line: observedLine};
  const choice = {inspectionId, version: '1' as const, inspectionDate: inspection.inspectionDate, inspectionReference: inspection.inspectionReference, noticeId, intakeId, lines: [{lineNumber: 1, input: observedLine, capacity}]};
  const pool = {id: '800', revision: '1', active: false, location, requestedLocation: location, physicalKey: 'held-source', label: 'Alokasi ditahan', mode: 'WebLedger' as const, control: 'ExclusiveWebAllocation' as const,
    configurationHash: 'b'.repeat(64), policyId: '802', policyRevision: '1', policyHash: 'c'.repeat(64), writer: 'unit_toko' as const, reconciliationProcedureReference: 'RECON-001', outageProcedureReference: 'OUTAGE-001'};
  const preview = {input, source: {order, inspection}, pool, capacity, binding: 'd'.repeat(64), observedAt: '2026-10-03T01:00:00.000Z'};
  const product = order.lines.find(line => line.skuId === observedLine.requestSkuId)!;
  const selected: OperationalScanSelection = {identifier: null, source: {orderId: order.id, orderVersion: order.version, inspectionId, inspectionRevision: choice.version, inspectionLineNumber: 1,
    skuId: product.skuId, definitionId: product.definitionId, definitionVersion: product.definitionVersion,
    sourceSystem: product.sourceSystem, sourceAccount: product.sourceAccountReference, sourceCode: product.externalItemCode}};
  const prepare = (selections?: OperationalScanSelection[]) => prepareStockReceipt(actor, target, order, choice, 1, pool, input, preview, selections);
  const scanned = prepare([selected]), manual = prepare(); assert.equal(Object.hasOwn(manual.body, 'scanSelections'), false);
  const key = `sr.receive.${inspectionId}.aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa`;
  await createProcurementStockReceiptGateway(client(async request => {
    assert.equal(request.headers.get('Idempotency-Key'), key); assert.deepEqual(await request.json(), {...manual.body, scanSelections: [selected]});
    return Response.json({receiptId: '9007199254741002', revision: '1', lotId: '820', balanceId: '821', movementId: '822', state: 'held_pending_valuation_and_release'});
  })).receive(scanned, key, () => true);
  assert.throws(() => prepare([{identifier: null, source: {...selected.source!, sourceAccount: 'wrong-account'}}]));
  assert.throws(() => prepare([{identifier: null, source: {...selected.source!, inspectionLineNumber: 2}}]));
});
