import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
import { identityDestinations, identityReturnPath, parseIdentitySession, stepUpFields } from '../src/modules/identity/identity-workflow';
import { createProcurementReceivingGateway, receivingNoticeInput, receivingIntakeInput, parseReceivingOrders, parseReceivingRead, prepareReceivingNotice, prepareReceivingIntake, parseProcurementReceivingResult } from '../src/modules/procurement/procurement-receiving-client';
import { SourceMemoryStorage, sourceSession } from './stock-source-fixtures';

const actor = '9007199254740993', orderId = '9007199254740994', noticeId = '9007199254740995';
const location = { organizationId: '100', shopId: '101', warehouseId: '102', channelId: '103' };
const organization = { id: '100', code: 'TOKO', name: 'Koperasi', scopeVersion: '1', profileVersion: '2' };
const source = { organization, shop: { ...organization, id: '101', code: 'SHOP', name: 'Toko' }, warehouse: { ...organization, id: '102', code: 'WARE', name: 'Gudang' }, channel: { ...organization, id: '103', code: 'RETAIL', name: 'Ritel' } }, target = { location, ...source };
const line = { skuId: '500', skuRevision: '2', definitionId: '501', definitionVersion: '1', skuCode: 'PAPER-BOX', familyName: 'Kertas', packageLabel: 'Karton 12', packageKind: 'sealed' as const, baseUnitCode: 'RIM', baseUnitsPerPackage: 12, packages: 2, specification: 'Kemasan utuh', sourceSystem: 'supplier' as const, sourceAccountReference: '0007', externalItemCode: '00Ab-01', netPricePerPackage: '12.34', taxAmount: '1.23', supplierChargeAmount: '0.04', netAmount: '24.68', totalAmount: '25.95', baseUnits: '24' };
const order = { id: orderId, version: '1' as const, approvalId: '700', proposalId: '701', route: 'ddo' as const, businessDate: '2026-10-03', currency: 'IDR' as const, supplier: { id: '600', versionId: '601', version: '1', subjectReference: 'SUP-001', displayName: 'Sumber Kertas' }, total: '25.95', requiredAdvance: '0.00', paymentTerms: { kind: 'on_receipt' as const, description: 'Dibayar setelah diterima', requiredAdvance: '0.00', balanceTrigger: 'on_receipt' as const, dueDate: null }, commercialReference: { kind: 'quote' as const, reference: '000QUOTE-A', issuedDate: '2026-10-02', validUntil: '2026-10-03T00:00:00.000Z' }, externalOrderReference: null, lines: [line] };
const orders = { location, items: [order], nextOrderId: null };
const input = { location, orderId, orderVersion: '1' as const, orderApprovalId: '700', noticeDate: '2026-10-03', supplierDocumentReference: '000DOC-Ab', ddoRequestReference: '000DDO-a', ddoTelephoneReference: 'Call-01', declaredContainers: 5, note: 'Lima koli sesuai dokumen pemasok' };
const preview = { input, order, source, binding: 'a'.repeat(64), observedAt: '2026-10-03T01:00:00.000Z' };
const notice = { id: noticeId, version: '1' as const, input, order, source, recordedAt: '2026-10-03T01:01:00.000Z', reason: 'Dokumen nyata telah diperiksa', state: 'notice_recorded' as const };
const capacity = { version: '0', declaredContainers: 5, matchedContainers: 0, remainingContainers: 5, excessContainers: '0', heldContainers: '0', receivedContainers: '0' };
const read = { notice, capacity, receipts: [], nextReceiptId: null };
const observed = { receivedDate: '2026-10-03', intakeReference: '000INTAKE-a', matchedContainers: 3, excessContainers: 1, heldContainers: 1, condition: 'mixed' as const, discrepancy: 'Satu koli lebih ditahan untuk konfirmasi' };
const after = { version: '1', declaredContainers: 5, matchedContainers: 3, remainingContainers: 2, excessContainers: '1', heldContainers: '1', receivedContainers: '4' };
const intakePreview = { notice, capacity, input: observed, after, binding: 'b'.repeat(64), observedAt: '2026-10-03T01:02:00.000Z' };
const receipt = { id: '810', noticeId, version: '1', input: observed, recordedAt: '2026-10-03T01:03:00.000Z', reason: 'Hitungan fisik sudah diperiksa', state: 'pending_inspection' as const };
const intakeRead = { notice, capacity: after, receipts: [receipt], nextReceiptId: null };
const uuid = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', noticeKey = `prn.${orderId}.${uuid}`, intakeKey = `pri.${noticeId}.${uuid}`;
const noticeResult = { operation: 'procurement.receiving.notice' as const, noticeId, receiptId: null, version: '0', state: 'notice_recorded' as const, recordedAt: notice.recordedAt };
const intakeResult = { operation: 'procurement.receiving.intake' as const, noticeId, receiptId: receipt.id, version: '1', state: 'pending_inspection' as const, recordedAt: receipt.recordedAt };
const noticeCommand = () => prepareReceivingNotice(actor, target, orders, order, input, preview, notice.reason);
const intakeCommand = () => prepareReceivingIntake(actor, read, observed, intakePreview, receipt.reason);

test('receiving selects all four real routes including zero advance without inferring supplier references or item quantities', () => {
  assert.deepEqual(parseReceivingOrders(orders), orders); assert.deepEqual(receivingNoticeInput(input), input);
  assert.ok(Object.isFrozen(noticeCommand().body.input)); assert.equal(noticeCommand().body.input.supplierDocumentReference, '000DOC-Ab');
  for (const route of ['omi_regular', 'smart', 'pasar_kering'] as const) {
    const selected = { ...order, route, externalOrderReference: route === 'omi_regular' ? 'OMI-0001' : null }, entered = { ...input, ddoRequestReference: null, ddoTelephoneReference: null };
    assert.equal(prepareReceivingNotice(actor, target, { ...orders, items: [selected] }, selected, entered, { ...preview, order: selected, input: entered }, notice.reason).body.input.declaredContainers, 5);
  }
  for (const changed of [{ ...input, declaredContainers: '5' }, { ...input, declaredContainers: 0 }, { ...input, orderId: Number(orderId) }, { ...input, noticeDate: '2026-02-30' }, { ...input, noticeDate: '1999-12-31' }, { ...input, supplierDocumentReference: ' DOC' }, { ...input, note: 'x\n' }, { ...input, stock: 5 }]) assert.throws(() => receivingNoticeInput(changed), RequestNotSentError);
  assert.equal(receivingNoticeInput({ ...input, note: 'Pengamatan\u200bnyata' }).note, 'Pengamatan\u200bnyata');
  for (const changed of [{ ...preview, input: { ...input, ddoTelephoneReference: null } }, { ...preview, input: { ...input, noticeDate: '2026-10-04' } }, { ...preview, source: { ...source, warehouse: { ...source.warehouse, id: '999' } } }, { ...preview, order: { ...order, approvalId: '999' } }]) assert.throws(() => prepareReceivingNotice(actor, target, orders, order, input, changed, notice.reason));
});

test('container observations distinguish matched, excess and held quantities with explicit condition and discrepancy', () => {
  assert.deepEqual(receivingIntakeInput(observed), observed);
  for (const changed of [{ ...observed, matchedContainers: '3' }, { ...observed, matchedContainers: 0, excessContainers: 0, heldContainers: 0 }, { ...observed, heldContainers: 0 }, { ...observed, heldContainers: 5 }, { ...observed, discrepancy: null }, { ...observed, condition: 'damaged' }, { ...observed, condition: 'opened' }, { ...observed, condition: 'unverified' }, { ...observed, matchedContainers: 1_000_000 }, { ...observed, itemsAccepted: true }]) assert.throws(() => receivingIntakeInput(changed), RequestNotSentError);
  assert.equal(receivingIntakeInput({ ...observed, condition: 'damaged', heldContainers: 4 }).heldContainers, 4);
  assert.equal(receivingIntakeInput({ ...observed, matchedContainers: 0 }).matchedContainers, 0);
  assert.equal(receivingIntakeInput({ ...observed, condition: 'intact', excessContainers: 0, heldContainers: 0, discrepancy: null }).discrepancy, null);
});

test('frozen intake binds current container capacity and preserves immutable notice and partial history', () => {
  assert.deepEqual(parseReceivingRead(read), read); assert.deepEqual(parseReceivingRead(intakeRead), intakeRead); assert.equal(intakeCommand().version, '0');
  for (const changed of [{ ...intakePreview, capacity: { ...capacity, version: '1' } }, { ...intakePreview, after: { ...after, remainingContainers: 1 } }, { ...intakePreview, after: { ...after, heldContainers: '2' } }, { ...intakePreview, after: { ...after, receivedContainers: '5' } }, { ...intakePreview, input: { ...observed, matchedContainers: 6 } }, { ...intakePreview, input: { ...observed, receivedDate: '2026-10-02' } }]) assert.throws(() => prepareReceivingIntake(actor, read, observed, changed, receipt.reason));
  for (const changed of [{ ...intakeRead, capacity: { ...after, excessContainers: 1 } }, { ...intakeRead, receipts: [{ ...receipt, noticeId: '99' }] }, { ...intakeRead, receipts: [{ ...receipt, state: 'stock_available' }] }, { ...intakeRead, notice: { ...notice, state: 'awaiting_containers' } }, { ...intakeRead, warehouseStock: 24 }]) assert.throws(() => parseReceivingRead(changed), AccessFailure);
});

test('intake transport sends live revision zero with actor CSRF and binds the resulting revision, never treating quoted counts as integers', async () => {
  const requests: Request[] = [];
  const gateway = createProcurementReceivingGateway(createUnitTokoClient({ baseUrl: 'https://unit.test', fetch: async input => { const request = new Request(input); if (new URL(request.url).pathname.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); requests.push(request); return Response.json(intakeResult); } }));
  assert.deepEqual(await gateway.intake(intakeCommand(), intakeKey, () => true), intakeResult);
  assert.equal(requests.length, 1); assert.equal(requests[0].headers.get('If-Match'), '"0"'); assert.equal(requests[0].headers.get('X-Expected-Actor-Id'), actor); assert.equal(requests[0].headers.get('X-CSRF-Token'), 'csrf'); assert.equal(requests[0].headers.get('Idempotency-Key'), intakeKey);
  assert.deepEqual(await requests[0].json(), intakeCommand().body);
  await assert.rejects(() => gateway.intake(intakeCommand(), intakeKey, () => false), RequestNotSentError); assert.equal(requests.length, 1);
  assert.throws(() => parseProcurementReceivingResult({ ...intakeResult, version: '0' }, intakeKey, intakeResult.operation));
  assert.throws(() => parseProcurementReceivingResult(intakeResult, `pri.99.${uuid}`, intakeResult.operation));
  assert.throws(() => parseProcurementReceivingResult(noticeResult, intakeKey, intakeResult.operation));
});

test('lost notice and intake responses recover only the original actor operation and key, without replay or generic close', async () => {
  for (const [key, saved, body, send] of [
    [noticeKey, noticeResult, noticeCommand().body, (gateway: ReturnType<typeof createProcurementReceivingGateway>) => gateway.notice(noticeCommand(), noticeKey, () => true)],
    [intakeKey, intakeResult, intakeCommand().body, (gateway: ReturnType<typeof createProcurementReceivingGateway>) => gateway.intake(intakeCommand(), intakeKey, () => true)],
  ] as const) {
    const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => key), requests: Request[] = [];
    const gateway = createProcurementReceivingGateway(createUnitTokoClient({ baseUrl: 'https://unit.test', fetch: async input => { const request = new Request(input); if (new URL(request.url).pathname.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); requests.push(request); if (new URL(request.url).pathname.endsWith('/operations/query')) return Response.json({ status: 'committed', result: saved }); throw new TypeError('Lost actual response'); } }));
    await assert.rejects(() => submitTrackedRequest(ledger, journal, actor, 'receiving', saved.operation, body, () => send(gateway)));
    const [entry] = journal.list(actor); assert.deepEqual(Object.keys(entry).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.equal(entry.key, key);
    const adapters = { 'procurement.receiving': { lookup: (item: typeof entry) => gateway.recover(item.actorId, item.operation, item.key) } };
    assert.equal(operationRecoveryCapabilities(entry.operation, adapters).canClose, false);
    const closed = { lookup: async () => { throw new Error('Generic lookup prohibited'); }, close: async () => { throw new Error('Generic close prohibited'); } };
    const recovered = await reconcileOperationRecovery(entry, journal, closed, adapters);
    assert.equal(recovered.state, 'committed'); assert.equal(journal.list(actor).length, 0); assert.equal(requests.length, 2);
    assert.deepEqual(await requests[1].json(), { purpose: 'operation-recovery', operation: saved.operation, key }); assert.equal(requests[1].headers.get('X-Expected-Actor-Id'), actor);
  }
});

test('receiving discovery and fresh step-up use only actual procurement authority', () => {
  assert.equal(identityReturnPath('/procurement/receiving'), '/procurement/receiving');
  assert.equal(identityDestinations(['funding.read']).some(item => item.href === '/procurement/receiving'), false);
  assert.equal(identityDestinations(['procurement.read']).some(item => item.href === '/procurement/receiving'), true);
  assert.deepEqual(recoveryPage('procurement.receiving.intake'), { href: '/procurement/receiving', label: 'halaman penerimaan pengadaan' });
  assert.equal(stepUpFields(parseIdentitySession({ ...sourceSession, stepUpOperations: ['procurement.container.receive'] }), 'procurement.container.receive', '/procurement/receiving').returnPath, '/procurement/receiving');
});
