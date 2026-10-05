import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { createPurchaseOrderGateway, parsePurchaseOrderProposal, preparePurchaseOrder, preparePurchaseOrderDecision } from '../src/modules/procurement/purchase-order-client';
import { purchaseOrderInboundSelection } from '../src/modules/procurement/purchase-order-inbound-client';
import { SourceMemoryStorage } from './stock-source-fixtures';
// Unexecuted draft tests: synthetic DTO/transport fixtures, not Mongo or activation acceptance.
const actor = '9007199254740993', requestId = '9007199254740994', proposalId = '9007199254740995';
const location = { organizationId: '100', shopId: '101', warehouseId: '102', channelId: '103' };
const organization = { id: '100', code: 'TOKO', name: 'Koperasi', scopeVersion: '1', profileVersion: '2' };
const source = { organization, shop: { ...organization, id: '101', code: 'SHOP', name: 'Toko' }, warehouse: { ...organization, id: '102', code: 'WARE', name: 'Gudang' }, channel: { ...organization, id: '103', code: 'RETAIL', name: 'Ritel' } }, target = { location, ...source };
const packageChoice = { skuId: '500', skuRevision: '2', definitionId: '501', definitionVersion: '1', skuCode: 'PAPER-BOX', familyName: 'Kertas', packageLabel: 'Karton 12', packageKind: 'sealed' as const, baseUnitCode: 'RIM', baseUnitsPerPackage: 12 };
const mapping = { location, sourceSystem: 'supplier' as const, sourceAccountReference: '0007', externalItemCode: '00Ab-01', skuId: '500', expectedSkuRevision: '2', definitionId: '501', definitionVersion: '1', validFrom: '2026-10-01T00:00:00.000Z', validUntil: '2026-11-01T00:00:00.000Z' }, mapped = { mapping, package: packageChoice };
const supplier = { supplierId: '600', versionId: '601', version: '1', subjectReference: 'SUP-001', displayName: 'Sumber Kertas', mappings: [mapped] };
const requestLine = { ...packageChoice, packages: 5, specification: 'Kemasan utuh', committedPackages: 1, availablePackages: 4 };
const request = { id: requestId, version: '1' as const, decisionId: '550', route: 'ddo' as const, kind: 'purchase_request' as const, businessDate: '2026-10-03', externalReference: null, reason: 'Kebutuhan cabang', source, lines: [requestLine] };
const lineInput = { requestSkuId: '500', packages: 2, sourceSystem: mapping.sourceSystem, sourceAccountReference: mapping.sourceAccountReference, externalItemCode: mapping.externalItemCode, definitionId: '501', definitionVersion: '1', netPricePerPackage: '12.34', taxAmount: '1.23', supplierChargeAmount: '0.04' };
const input = { location, requestId, requestVersion: '1' as const, requestDecisionId: '550', supplierId: '600', supplierVersionId: '601', supplierVersion: '1', businessDate: '2026-10-03', currency: 'IDR' as const,
  commercialReference: { kind: 'quote' as const, reference: '000QUOTE-A', issuedDate: '2026-10-02', validUntil: '2026-10-04T00:00:00.000Z' }, externalOrderReference: null,
  paymentTerms: { kind: 'deposit' as const, description: 'Uang muka sesuai penawaran', requiredAdvance: '10.00', balanceTrigger: 'on_receipt' as const, dueDate: null }, lines: [lineInput] };
const capacity = [{ requestSkuId: '500', requestedPackages: 5, committedPackages: 1, availablePackages: 4, version: '1' }];
const reviewedLine = { ...packageChoice, packages: 2, specification: requestLine.specification, sourceSystem: mapping.sourceSystem, sourceAccountReference: mapping.sourceAccountReference, externalItemCode: mapping.externalItemCode, netPricePerPackage: '12.34', taxAmount: '1.23', supplierChargeAmount: '0.04', netAmount: '24.68', totalAmount: '25.95', baseUnits: '24' };
const facts = { input, route: 'ddo' as const, source, request: { id: requestId, version: '1' as const, decisionId: '550' }, supplier: { id: '600', versionId: '601', version: '1', subjectReference: supplier.subjectReference, displayName: supplier.displayName }, lines: [reviewedLine], totals: { net: '24.68', tax: '1.23', supplierCharges: '0.04', total: '25.95' }, capacity };
const preview = { ...facts, binding: 'a'.repeat(64), observedAt: '2026-10-03T01:00:00.000Z' };
const proposal = { ...facts, id: proposalId, version: '1' as const, reason: 'Beli sesuai penawaran asli', proposedAt: '2026-10-03T01:01:00.000Z', state: 'pending' as const, decision: null };
const decisionPreview = { proposal, decision: 'approve' as const, capacity: [{ ...capacity[0], committedPackages: 2, availablePackages: 3, version: '2' }], binding: 'b'.repeat(64), observedAt: '2026-10-03T01:02:00.000Z' };
const proposed = { proposalId, proposalVersion: '1' as const, decisionId: null, orderId: null, orderVersion: null, status: 'proposed' as const }, approved = { ...proposed, decisionId: '701', orderId: '702', orderVersion: '1', status: 'approved' as const };
const uuid = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', proposeKey = `po.propose.${requestId}.${uuid}`, approveKey = `po.approve.${proposalId}.${uuid}`;
const command = () => preparePurchaseOrder(actor, target, request, supplier, [mapped], input, preview, proposal.reason);
const decisionCommand = () => preparePurchaseOrderDecision(actor, proposal, decisionPreview, 'Tinjau kapasitas dan nilai pembelian');

const pool = (poolId: string) => ({ id: poolId, revision: '1', active: false, location, requestedLocation: location, physicalKey: 'synthetic-' + poolId, label: 'Tujuan sintetis ' + poolId, mode: 'WebLedger' as const, control: 'SharedWebLedger' as const, configurationHash: 'c'.repeat(64), policyId: '800', policyRevision: '1', policyHash: 'd'.repeat(64), writer: 'unit_toko' as const, reconciliationProcedureReference: 'SYNTHETIC-RECONCILIATION', outageProcedureReference: 'SYNTHETIC-OUTAGE' });
const lowPool = pool('9007199254740996'), highPool = pool('9007199254740997');
const split = { allocations: [{ requestSkuId: '500', packages: 1, poolId: highPool.id, poolRevision: '1' }, { requestSkuId: '500', packages: 1, poolId: lowPool.id, poolRevision: '1' }] };
const canonical = purchaseOrderInboundSelection(input, split), shown = { ...preview, inboundPools: { allocations: canonical.allocations, receivingPools: [lowPool, highPool] } };
const boundProposal = { ...proposal, inboundPools: { ...shown.inboundPools, selectionId: proposalId, selectionHash: 'e'.repeat(64), decisionId: null, state: 'proposed' as const, unplannedPackagesBound: false as const, stockReserved: false as const, physicalStockReleased: false as const } };
const selectedCommand = () => preparePurchaseOrder(actor, target, request, supplier, [mapped], input, shown, proposal.reason, split, [highPool, lowPool]);

test('draft planned splits are lossless, canonical and exact per SKU; partial, duplicate, excess and authority input are rejected before dispatch', () => {
  assert.deepEqual(canonical.allocations.map(row => row.poolId), [lowPool.id, highPool.id]);
  assert.deepEqual(command().body, { input, binding: preview.binding, reason: proposal.reason });
  assert.equal(Object.hasOwn(command().body, 'inboundPools'), false);
  assert.ok(Object.isFrozen(selectedCommand().body.inboundPools!.allocations[0]));
  assert.deepEqual(selectedCommand().body.inboundPools, canonical);
  for (const invalid of [null, { allocations: [] }, { allocations: [{ ...split.allocations[0], packages: 2 }, split.allocations[1]] }, { allocations: [split.allocations[0]] },
    { allocations: [split.allocations[0], split.allocations[0]] }, { allocations: [{ ...split.allocations[0], requestSkuId: '501' }, split.allocations[1]] },
    { allocations: [{ ...split.allocations[0], poolId: '9223372036854775806' }, split.allocations[1]] }, { ...split, active: true },
    { allocations: [{ ...split.allocations[0], packages: 1.1 }, split.allocations[1]] }, { allocations: [{ ...split.allocations[0], poolId: 9007199254740997 }, split.allocations[1]] }]) assert.throws(() => purchaseOrderInboundSelection(input, invalid), RequestNotSentError);
  assert.throws(() => preparePurchaseOrder(actor, target, request, supplier, [mapped], input, shown, proposal.reason));
  assert.throws(() => preparePurchaseOrder(actor, target, request, supplier, [mapped], input, preview, proposal.reason, split, [highPool, lowPool]));
  assert.throws(() => preparePurchaseOrder(actor, target, request, supplier, [mapped], input, shown, proposal.reason, split, [{ ...lowPool, revision: '2' }, highPool]));
});

test('draft PO preview and save reuse actor/CSRF/idempotency; existing receiving-pool query is scoped and optional absence has exact legacy body', async () => {
  const bodies: unknown[] = []; let inbound = false;
  const gateway = createPurchaseOrderGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' });
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(request.cache, 'no-store'); assert.equal(request.redirect, 'error'); assert.equal(request.credentials, 'same-origin');
    const body = await request.json(); bodies.push(body);
    if (request.url.endsWith('/pools/query')) { assert.deepEqual(body, { purpose: 'procurement-stock-receipt-review', location, beforeId: highPool.id }); assert.equal(request.headers.get('Idempotency-Key'), null); return Response.json({ location, items: [lowPool], nextPoolId: null }); }
    if (request.url.endsWith('/preview/query')) { assert.deepEqual(body, { purpose: 'purchase-order-review', input, ...(inbound ? { inboundPools: canonical } : {}) }); return Response.json(inbound ? shown : preview); }
    assert.equal(request.headers.get('Idempotency-Key'), proposeKey); assert.deepEqual(body, selectedCommand().body); return Response.json(proposed);
  } }));
  assert.deepEqual(await gateway.receivingPools(actor, location, highPool.id), { location, items: [lowPool], nextPoolId: null });
  assert.deepEqual(await gateway.preview(actor, input), preview); inbound = true;
  assert.deepEqual(await gateway.preview(actor, input, undefined, split), shown);
  assert.deepEqual(await gateway.propose(selectedCommand(), proposeKey, () => true), proposed);
  const prior = bodies.length; await assert.rejects(gateway.preview(actor, input, undefined, { allocations: [] }), RequestNotSentError); assert.equal(bodies.length, prior);
  await assert.rejects(gateway.propose(selectedCommand(), proposeKey, () => false), RequestNotSentError); assert.equal(bodies.length, prior);
});

test('draft saved plans must match the exact proposal, SKU totals, pool revisions, scope and decision while all legacy views remain accepted', () => {
  assert.deepEqual(parsePurchaseOrderProposal(proposal), proposal); assert.deepEqual(parsePurchaseOrderProposal(boundProposal), boundProposal);
  const approvedView = { ...boundProposal, state: 'approved' as const, decision: { id: approved.decisionId, decision: 'approve' as const, reason: 'Synthetic independent approval', decidedAt: decisionPreview.observedAt, orderId: approved.orderId, orderVersion: '1' }, inboundPools: { ...boundProposal.inboundPools, decisionId: approved.decisionId, state: 'approve' as const } };
  assert.deepEqual(parsePurchaseOrderProposal(approvedView), approvedView);
  const rejected = { ...boundProposal, state: 'rejected' as const, decision: { ...approvedView.decision, decision: 'reject' as const, orderId: null, orderVersion: null }, inboundPools: { ...boundProposal.inboundPools, decisionId: approved.decisionId, state: 'reject' as const } };
  assert.deepEqual(parsePurchaseOrderProposal(rejected), rejected);
  for (const change of [{ ...boundProposal, inboundPools: null }, { ...boundProposal, inboundPools: { ...boundProposal.inboundPools, stockReserved: true } },
    { ...boundProposal, inboundPools: { ...boundProposal.inboundPools, selectionId: requestId } }, { ...approvedView, inboundPools: { ...approvedView.inboundPools, decisionId: '8' } },
    { ...boundProposal, inboundPools: { ...boundProposal.inboundPools, allocations: [...canonical.allocations].reverse() } },
    { ...boundProposal, inboundPools: { ...boundProposal.inboundPools, receivingPools: [{ ...lowPool, revision: '2' }, highPool] } },
    { ...boundProposal, inboundPools: { ...boundProposal.inboundPools, receivingPools: [{ ...lowPool, requestedLocation: { ...location, warehouseId: '104' } }, highPool] } },
    { ...boundProposal, inboundPools: { ...boundProposal.inboundPools, allocations: [{ ...canonical.allocations[0], packages: 2 }, canonical.allocations[1]] } },
    { ...boundProposal, inboundPools: { ...boundProposal.inboundPools, approved: true } }]) assert.throws(() => parsePurchaseOrderProposal(change));
});

test('draft lost selected-plan save is recovered only by original key and actual saved plan; journal contains no financial or allocation body', async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => proposeKey), op = 'procurement.purchase.order.propose'; let sends = 0, reads = 0;
  await assert.rejects(submitTrackedRequest(ledger, journal, actor, 'purchase-order-propose', op, selectedCommand().body, async () => { sends++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
  const serialized = [...storage.values.values()][0]; assert.deepEqual(Object.keys(JSON.parse(serialized)).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.equal(serialized.includes(lowPool.id), false); assert.equal(serialized.includes('inboundPools'), false);
  const gateway = createPurchaseOrderGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); reads++; assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('Idempotency-Key'), null);
    if (request.url.endsWith('/operations/query')) { assert.deepEqual(await request.json(), { purpose: 'operation-recovery', operation: op, key: proposeKey }); return Response.json({ status: 'confirmed', result: proposed }); }
    assert.ok(request.url.endsWith('/proposals/' + proposalId + '/query')); return Response.json(boundProposal);
  } }));
  const entry = journal.list(actor)[0], generic = { lookup: async () => { throw new Error('No generic lookup'); }, close: async () => { throw new Error('No generic close'); } };
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, generic, { 'procurement.purchase.order': { lookup: item => gateway.recover(item.actorId, item.operation, item.key) } }), { state: 'committed', result: { id: proposalId, version: '1' } });
  assert.equal(sends, 1); assert.equal(reads, 2); assert.equal(journal.list(actor).length, 0);
});

test('draft annotated decision keeps original If-Match and authority while current selected plan participates in unchanged independent review', async () => {
  const checked = { ...decisionPreview, proposal: boundProposal }, prepared = preparePurchaseOrderDecision(actor, boundProposal, checked, 'Tinjau tujuan, nilai dan jumlah'); let calls = 0;
  const gateway = createPurchaseOrderGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++; assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('If-Match'), '"1"'); assert.equal(request.headers.get('Idempotency-Key'), approveKey); assert.deepEqual(await request.json(), prepared.body); return Response.json(approved);
  } }));
  assert.deepEqual(await gateway.decide(prepared, approveKey, () => true), approved); assert.equal(calls, 1);
  assert.throws(() => preparePurchaseOrderDecision(actor, boundProposal, { ...checked, proposal: proposal }, 'Tinjau'), RequestNotSentError);
  assert.deepEqual(decisionCommand().body, { decision: 'approve', binding: decisionPreview.binding, reason: 'Tinjau kapasitas dan nilai pembelian' });
});
