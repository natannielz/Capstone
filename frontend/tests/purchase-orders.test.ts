import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { operationLabel, recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
import { identityDestinations, identityReturnPath, loginFields, parseIdentitySession, stepUpFields } from '../src/modules/identity/identity-workflow';
import { createPurchaseOrderGateway, parsePurchaseOrderProposal, parsePurchaseOrderRequests, parsePurchaseOrderResult, parsePurchaseOrderSuppliers,
  preparePurchaseOrder, preparePurchaseOrderDecision, purchaseOrderInput, purchaseOrderKeyOwner } from '../src/modules/procurement/purchase-order-client';
import { supplierTime } from '../src/modules/procurement/supplier-client';
import { SourceMemoryStorage, sourceSession } from './stock-source-fixtures';

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

test('commercial input requires explicit exact amounts and valid reference dates, preserving source zeros and payment semantics', () => {
  assert.deepEqual(purchaseOrderInput(input), input); assert.equal(supplierTime('2026-10-04T07:00'), input.commercialReference.validUntil);
  for (const changed of [{ ...input, commercialReference: { ...input.commercialReference, issuedDate: '2026-10-04' } }, { ...input, commercialReference: { ...input.commercialReference, validUntil: '2026-10-01T00:00:00Z' } },
    { ...input, commercialReference: { ...input.commercialReference, reference: ' quote' } }, { ...input, lines: [{ ...lineInput, netPricePerPackage: '' }] }, { ...input, lines: [{ ...lineInput, netPricePerPackage: '1e2' }] },
    { ...input, lines: [{ ...lineInput, netPricePerPackage: '0.001' }] }, { ...input, lines: [{ ...lineInput, netPricePerPackage: '999999999999999.99' }] }, { ...input, lines: [lineInput, lineInput] },
    { ...input, paymentTerms: { ...input.paymentTerms, requiredAdvance: '25.95' } }, { ...input, paymentTerms: { ...input.paymentTerms, kind: 'on_receipt' as const } },
    { ...input, paymentTerms: { ...input.paymentTerms, kind: 'credit' as const, requiredAdvance: '0.00', balanceTrigger: 'due_date' as const, dueDate: '2026-10-02' } }]) assert.throws(() => purchaseOrderInput(changed), RequestNotSentError);
  assert.equal(purchaseOrderInput({ ...input, paymentTerms: { kind: 'full_before_dispatch', description: 'Lunas sebelum kirim', requiredAdvance: '25.95', balanceTrigger: 'before_dispatch', dueDate: null } }).paymentTerms.requiredAdvance, '25.95');
  assert.equal(purchaseOrderInput({ ...input, paymentTerms: { kind: 'on_receipt', description: 'Bayar setelah diterima', requiredAdvance: '0.00', balanceTrigger: 'on_receipt', dueDate: null } }).paymentTerms.kind, 'on_receipt');
  assert.equal(purchaseOrderInput({ ...input, paymentTerms: { kind: 'credit', description: 'Tagihan sesuai dokumen', requiredAdvance: '0.00', balanceTrigger: 'due_date', dueDate: '2026-10-30' } }).paymentTerms.dueDate, '2026-10-30');
  const exact = '\uFEFFＲujukan\u200d'; assert.equal(purchaseOrderInput({ ...input, commercialReference: { ...input.commercialReference, reference: exact } }).commercialReference.reference, exact);
});

test('frozen PO matches approved request package, source mapping and exact line totals without retail prices or private supplier details', () => {
  assert.ok(Object.isFrozen(command().body.input.paymentTerms)); assert.ok(Object.isFrozen(command().body.input.lines[0]));
  for (const changed of [{ ...preview, lines: [{ ...reviewedLine, totalAmount: '25.94' }] }, { ...preview, lines: [{ ...reviewedLine, baseUnits: '12' }] }, { ...preview, totals: { ...preview.totals, tax: '2.46' } },
    { ...preview, supplier: { ...facts.supplier, versionId: '602' } }, { ...preview, route: 'omi_regular' as const }, { ...preview, capacity: [{ ...capacity[0], committedPackages: 4, availablePackages: 1 }] },
    { ...preview, lines: [{ ...reviewedLine, specification: 'Different' }] }, { ...preview, source: { ...source, shop: { ...source.shop, profileVersion: '3' } } }]) assert.throws(() => preparePurchaseOrder(actor, target, request, supplier, [mapped], input, changed, proposal.reason));
  assert.throws(() => preparePurchaseOrder(actor, target, request, { ...supplier, mappings: [{ ...mapped, mapping: { ...mapping, validUntil: preview.observedAt } }] }, [{ ...mapped, mapping: { ...mapping, validUntil: preview.observedAt } }], input, preview, proposal.reason));
  assert.throws(() => preparePurchaseOrder(actor, target, request, supplier, [mapped], input, { ...preview, observedAt: input.commercialReference.validUntil }, proposal.reason));
  assert.deepEqual(parsePurchaseOrderSuppliers({ location, items: [supplier], nextSupplierId: null }).items, [supplier]); assert.throws(() => parsePurchaseOrderSuppliers({ location, items: [{ ...supplier, contactValue: 'private' }], nextSupplierId: null }));
  assert.throws(() => parsePurchaseOrderSuppliers({ location, items: [{ ...supplier, mappings: [{ ...mapped, package: { ...packageChoice, retailPrice: '999.00' } }] }], nextSupplierId: null }));
  assert.deepEqual(parsePurchaseOrderRequests({ location, items: [request], nextRequestId: null }).items, [request]); assert.throws(() => parsePurchaseOrderRequests({ location, items: [{ ...request, lines: [{ ...requestLine, availablePackages: 5 }] }], nextRequestId: null }));
});

test('decision review preserves original proposal and separately reviews current capacity; approval creates only internal order identity', () => {
  assert.ok(Object.isFrozen(decisionCommand().body)); assert.deepEqual(decisionPreview.proposal.capacity, capacity);
  for (const changed of [{ ...decisionPreview, capacity: [] }, { ...decisionPreview, capacity: [{ ...capacity[0], committedPackages: 4, availablePackages: 1 }] },
    { ...decisionPreview, capacity: [{ ...capacity[0], requestedPackages: 6, availablePackages: 5 }] }, { ...decisionPreview, proposal: { ...proposal, reason: 'Changed' } }]) assert.throws(() => preparePurchaseOrderDecision(actor, proposal, changed, 'Alasan'));
  assert.equal(preparePurchaseOrderDecision(actor, proposal, { ...decisionPreview, decision: 'reject', capacity: [] }, 'Tolak rincian').decision, 'reject');
  const approvedProposal = { ...proposal, state: 'approved' as const, decision: { id: approved.decisionId, decision: 'approve' as const, reason: 'Sesuai', decidedAt: decisionPreview.observedAt, orderId: approved.orderId, orderVersion: '1' } };
  assert.deepEqual(parsePurchaseOrderProposal(approvedProposal), approvedProposal);
  for (const changed of [{ ...approvedProposal, decision: null }, { ...proposal, supplierConfirmed: true }, { ...approvedProposal, decision: { ...approvedProposal.decision, orderId: null } }, { ...proposal, decision: approvedProposal.decision }]) assert.throws(() => parsePurchaseOrderProposal(changed));
});

test('PO transport sends original actor, CSRF, reviewed amounts and immutable If-Match, without resend on denial or lost actor', async () => {
  let calls = 0; const gateway = createPurchaseOrderGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++; assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(input.cache, 'no-store'); assert.equal(input.redirect, 'error'); assert.equal(input.credentials, 'same-origin');
    if (input.url.endsWith('/proposals')) { assert.equal(input.headers.get('Idempotency-Key'), proposeKey); assert.deepEqual(await input.json(), command().body); return Response.json(proposed); }
    assert.equal(input.headers.get('If-Match'), '"1"'); assert.equal(input.headers.get('Idempotency-Key'), approveKey); assert.deepEqual(await input.json(), decisionCommand().body);
    return calls === 3 ? Response.json({ code: 'step_up_required' }, { status: 403 }) : Response.json(approved);
  } }));
  await assert.rejects(gateway.propose(command(), proposeKey, () => false), RequestNotSentError); assert.equal(calls, 0);
  assert.deepEqual(await gateway.propose(command(), proposeKey, () => true), proposed); assert.deepEqual(await gateway.decide(decisionCommand(), approveKey, () => true), approved);
  await assert.rejects(gateway.decide(decisionCommand(), approveKey, () => true), { code: 'step_up_required' }); assert.equal(calls, 3);
  let actorChecks = 0; await assert.rejects(gateway.decide(decisionCommand(), approveKey, () => ++actorChecks === 1), { code: 'commit_outcome_unknown' }); assert.equal(calls, 4);
});

test('lookup-only PO recovery retains original actor metadata and rejects foreign or contradictory receipts without closing', async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => approveKey), op = 'procurement.purchase.order.approve'; let attempts = 0;
  await assert.rejects(submitTrackedRequest(ledger, journal, actor, 'po-approve', op, decisionCommand().body, async () => { attempts++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
  const entry = journal.list(actor)[0], serialized = [...storage.values.values()][0]; assert.deepEqual(Object.keys(JSON.parse(serialized)).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.equal(journal.list('99').length, 0); assert.equal(serialized.includes(input.commercialReference.reference), false); assert.equal(serialized.includes(input.paymentTerms.description), false);
  const legacy = { lookup: async () => { throw new Error('No generic lookup'); }, close: async () => { throw new Error('No generic close'); } };
  assert.deepEqual(operationRecoveryCapabilities(op), { canCheck: false, canClose: false, unavailable: 'adapter' }); assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'procurement.purchase.order': { lookup: async () => null } }), { state: 'unconfirmed' });
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'procurement.purchase.order': { lookup: async () => ({ state: 'committed', result: approved }) } }, 'Close'));
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'procurement.purchase.order': { lookup: async () => ({ state: 'committed', result: { ...approved, proposalId: '8' } }) } }));
  assert.equal(journal.list(actor).length, 1); assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'procurement.purchase.order': { lookup: async () => ({ state: 'committed', result: approved }) } }), { state: 'committed', result: { id: proposalId, version: '1' } }); assert.equal(journal.list(actor).length, 0); assert.equal(attempts, 1);
  assert.throws(() => parsePurchaseOrderResult({ ...approved, orderId: null }, approveKey, op)); assert.throws(() => purchaseOrderKeyOwner(approveKey, 'procurement.purchase.order.reject'));
});

test('proposal recovery proves the original request and narrow supplier choice pagination remains an actor-bound read', async () => {
  let foreign = false, calls = 0; const gateway = createPurchaseOrderGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++; assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('Idempotency-Key'), null);
    if (input.url.endsWith('/choices/query')) { assert.deepEqual(await input.json(), { purpose: 'supplier-selection', location, beforeId: '700' }); return Response.json({ location, items: [], nextSupplierId: '650' }); }
    if (input.url.endsWith('/operations/query')) { assert.deepEqual(await input.json(), { purpose: 'operation-recovery', operation: 'procurement.purchase.order.propose', key: proposeKey }); return Response.json({ status: 'confirmed', result: proposed }); }
    assert.deepEqual(await input.json(), { purpose: 'purchase-order-review' }); return Response.json(foreign ? { ...proposal, request: { ...facts.request, id: '8' }, input: { ...input, requestId: '8' } } : proposal);
  } }));
  assert.deepEqual(await gateway.recover(actor, 'procurement.purchase.order.propose', proposeKey), { state: 'committed', result: proposed }); foreign = true; await assert.rejects(gateway.recover(actor, 'procurement.purchase.order.propose', proposeKey)); assert.equal(calls, 4);
  assert.equal((await gateway.suppliers(actor, location, '700')).nextSupplierId, '650');
});

test('PO destination and step-up use the exact approved path and existing procurement grants', () => {
  const path = '/procurement/purchase-orders'; assert.equal(identityReturnPath(path), path); assert.deepEqual(loginFields(path, '', ''), { returnPath: path });
  for (const permission of ['procurement.read', 'procurement.prepare', 'procurement.approve']) assert.equal(identityDestinations([permission]).some(item => item.href === path), true);
  for (const permission of ['procurement.prepare', 'procurement.approve']) { const session = parseIdentitySession({ ...sourceSession, stepUpOperations: [permission] }); assert.deepEqual(stepUpFields(session, permission, path), { expectedActorId: session.account!.userId, operation: permission, returnPath: path }); }
  assert.equal(identityDestinations(['supplier.change.approve']).some(item => item.href === path), false); assert.equal(recoveryPage('procurement.purchase.order.approve').href, path); assert.equal(operationLabel('procurement.purchase.order.approve'), 'Persetujuan internal pesanan pembelian');
  for (const invalid of [path + '/', path + '?return=https://elsewhere.test', '//procurement/purchase-orders']) assert.throws(() => loginFields(invalid, '', ''));
});
