import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createOrderTimelineGateway, parseTimelineShipments, parseTimelineInvoices, parseTimelinePayments, parseTimelineCases,
  parseTimelinePaymentOutcome, parseTimelineCaseOutcome} from '../src/modules/ordering/order-timeline-client';
import {AccessFailure} from '../src/modules/administration/workflow';

const at = '2026-09-20T03:00:00+00:00', later = '2026-09-20T04:00:00Z', orderId = '9007199254740993';
const shipment = {id: '9007199254741000', revision: '2', suborderId: '17', number: '1/2/2026/3', dispatchedAt: at, courierId: '91', courierName: 'Kurir fixture', currentAssignment: {revision: '1', courierId: '91', courierName: 'Kurir fixture', effectiveAt: at}};
const invoice = {id: '29', number: '1/2/2026/4', issuedAt: at, dueDate: '2026-10-01', orderIds: [orderId, '18']};
const payment = {id: '31', revision: '2', createdAt: at, updatedAt: later, state: 'submitted', invoiceIds: ['29'], submittedAt: later};
const caseItem = {id: '41', revision: '2', requestedAt: at, state: 'return_authorized', invoiceId: '29', issue: 'wrong_item'};
const page = (items: unknown[], hasMore = false) => ({orderId, items, hasMore});
const outcome = () => ({orderId, declarationId: '31', declarationRevision: '2', observedAt: later, declarationState: 'submitted', submissionId: '51', submittedAt: at,
  reviewId: null, reviewRevision: '0', reviewState: 'pending', reviewRecordedAt: null, publicExplanation: null, verificationId: null, verifiedAt: null,
  currentAssigneeAccountId: null, reviewActorAccountId: null, verificationActorAccountId: null, invoiceEvents: []});
const caseOutcome = () => ({orderId, caseId: '41', invoiceId: '29', observedAt: later, caseState: 'return_authorized', decisionId: '61', decidedAt: at,
  decisionActorAccountId: '92', publicExplanation: 'Retur diizinkan untuk barang yang dilaporkan.', returns: [{returnId: '71', events: [{eventId: '71', kind: 'received', recordedAt: later, actorAccountId: '93'}]}]});
const invalid = (action: () => unknown) => assert.throws(action, error => error instanceof AccessFailure && error.status === 502);

test('timeline keeps exact large IDs, grouped invoice association and exclusive descending cursors', () => {
  assert.equal(parseTimelineShipments(page([shipment]), orderId).items[0].id, shipment.id);
  assert.equal(parseTimelineInvoices(page([invoice]), orderId).items[0].orderIds.length, 2);
  assert.equal(parseTimelinePayments(page([payment]), orderId).items[0].state, 'submitted');
  assert.equal(parseTimelineCases(page([caseItem]), orderId).items[0].state, 'return_authorized');
  invalid(() => parseTimelineInvoices(page([{...invoice, orderIds: ['18']}]), orderId));
  invalid(() => parseTimelineShipments(page([shipment]), orderId, shipment.id));
  invalid(() => parseTimelineShipments(page([shipment, shipment]), orderId));
  invalid(() => parseTimelineShipments(page([shipment], true), orderId));
  invalid(() => parseTimelineShipments({...page([shipment]), orderId: '18'}, orderId));
  invalid(() => parseTimelineShipments(page([{...shipment, id: 9007199254741000}]), orderId));
});
test('timeline rejects impossible submission status and compares UTC instants rather than string formatting', () => {
  assert.equal(parseTimelinePayments(page([{...payment, createdAt: '2026-09-20T03:00:00.001Z', updatedAt: '2026-09-20T03:00:00.010+00:00', submittedAt: '2026-09-20T03:00:00.010Z'}]), orderId).items.length, 1);
  invalid(() => parseTimelinePayments(page([{...payment, submittedAt: null}]), orderId));
  invalid(() => parseTimelinePayments(page([{...payment, state: 'paid'}]), orderId));
  invalid(() => parseTimelinePayments(page([{...payment, createdAt: later, updatedAt: at}]), orderId));
  invalid(() => parseTimelineInvoices(page([{...invoice, orderIds: [orderId, orderId]}]), orderId));
});
test('payment outcome cannot turn submission or OCR into verified bank or order settlement', () => {
  assert.equal(parseTimelinePaymentOutcome(outcome(), orderId, '31').verifiedAt, null);
  invalid(() => parseTimelinePaymentOutcome({...outcome(), verifiedAt: at}, orderId, '31'));
  invalid(() => parseTimelinePaymentOutcome({...outcome(), reviewState: 'paid'}, orderId, '31'));
  invalid(() => parseTimelinePaymentOutcome({...outcome(), settled: true}, orderId, '31'));
  invalid(() => parseTimelinePaymentOutcome({...outcome(), invoiceEvents: [{invoiceId: '29', eventId: '81', kind: 'allocation', recordedAt: later, originalAllocationId: null, actorAccountId: '94'}]}, orderId, '31'));
  invalid(() => parseTimelinePaymentOutcome(outcome(), '18', '31'));
  invalid(() => parseTimelinePaymentOutcome(outcome(), orderId, '32'));
});
test('verified invoice events preserve allocation reversals and refuse duplicate or future records', () => {
  const allocation = {invoiceId: '29', eventId: '81', kind: 'allocation', recordedAt: at, originalAllocationId: null, actorAccountId: '94'};
  const reversal = {invoiceId: '29', eventId: '82', kind: 'reversal', recordedAt: later, originalAllocationId: '81', actorAccountId: '95'};
  const row = {...outcome(), verificationId: '91', verifiedAt: at, verificationActorAccountId: '93', invoiceEvents: [allocation, reversal]};
  assert.equal(parseTimelinePaymentOutcome(row, orderId, '31').invoiceEvents[1].originalAllocationId, '81');
  invalid(() => parseTimelinePaymentOutcome({...row, invoiceEvents: [allocation, allocation]}, orderId, '31'));
  invalid(() => parseTimelinePaymentOutcome({...row, invoiceEvents: [{...reversal, originalAllocationId: null}]}, orderId, '31'));
  invalid(() => parseTimelinePaymentOutcome({...row, invoiceEvents: [{...allocation, recordedAt: '2026-10-01T00:00:00Z'}]}, orderId, '31'));
});
test('draft has no invented review, verification or recorded payment events', () => {
  const draft = {...outcome(), declarationRevision: '1', declarationState: 'draft', submissionId: null, submittedAt: null, reviewState: 'not_submitted'};
  assert.equal(parseTimelinePaymentOutcome(draft, orderId, '31').reviewRevision, '0');
  invalid(() => parseTimelinePaymentOutcome({...draft, verificationId: '91', verifiedAt: at}, orderId, '31'));
  invalid(() => parseTimelinePaymentOutcome({...draft, publicExplanation: 'Approved'}, orderId, '31'));
});
test('case timeline distinguishes approved, recorded and bank-settled refund events', () => {
  const row = caseOutcome();
  row.returns[0].events.push({eventId: '83', kind: 'refund_approved', recordedAt: later, actorAccountId: '94'}, {eventId: '84', kind: 'payout_recorded', recordedAt: later, actorAccountId: '95'});
  assert.deepEqual(parseTimelineCaseOutcome(row, orderId, '41').returns[0].events.map(event => event.kind), ['received', 'refund_approved', 'payout_recorded']);
  invalid(() => parseTimelineCaseOutcome({...row, caseState: 'rejected'}, orderId, '41'));
  invalid(() => parseTimelineCaseOutcome({...row, decisionId: null}, orderId, '41'));
  invalid(() => parseTimelineCaseOutcome({...row, returns: [row.returns[0], row.returns[0]]}, orderId, '41'));
  invalid(() => parseTimelineCaseOutcome(row, '18', '41'));
});
test('responsibility references bind current assignment separately from authenticated event actors', () => {
  const assigned = {...outcome(), reviewId: '81', reviewRevision: '1', reviewState: 'assigned', reviewRecordedAt: at, reviewActorAccountId: '91', currentAssigneeAccountId: '92'};
  assert.equal(parseTimelinePaymentOutcome(assigned, orderId, '31').currentAssigneeAccountId, '92');
  invalid(() => parseTimelinePaymentOutcome({...assigned, currentAssigneeAccountId: null}, orderId, '31'));
  invalid(() => parseTimelinePaymentOutcome({...assigned, reviewActorAccountId: null}, orderId, '31'));
  invalid(() => parseTimelinePaymentOutcome({...assigned, reviewState: 'rejected'}, orderId, '31'));
  invalid(() => parseTimelinePaymentOutcome({...assigned, assigneeEmail: 'private@example.invalid'}, orderId, '31'));
  invalid(() => parseTimelineCaseOutcome({...caseOutcome(), decisionActorAccountId: null}, orderId, '41'));
  invalid(() => parseTimelineCaseOutcome({...caseOutcome(), returns: [{returnId: '71', events: [{eventId: '71', kind: 'received', recordedAt: later}]}]}, orderId, '41'));
});
test('read gateway binds actor, order, purpose and cursor without operational writes or caching', async () => {
  const calls: {url: string; init?: RequestInit}[] = [];
  const fetcher: typeof fetch = async (input, init) => {
    const url = input instanceof Request ? input.url : String(input); calls.push({url, init});
    return new Response(JSON.stringify(url.endsWith('/csrf') ? {requestToken: 'csrf-fixture'} : page([invoice])), {status: 200, headers: {'Content-Type': 'application/json'}});
  };
  const result = await createOrderTimelineGateway(fetcher, 'https://unit-toko.test/api/v1/identity/csrf').invoices('11', orderId, '30');
  assert.equal(result.items[0].id, '29');
  assert.equal(calls[1].url, '/api/v1/ordering/orders/' + orderId + '/timeline/invoices/query');
  assert.deepEqual(JSON.parse(String(calls[1].init?.body)), {purpose: 'order-timeline', beforeId: '30', limit: 25});
  assert.equal(new Headers(calls[1].init?.headers).get('X-Expected-Actor-Id'), '11');
  assert.equal(new Headers(calls[1].init?.headers).has('Idempotency-Key'), false);
  assert.equal(calls[1].init?.cache, 'no-store'); assert.equal(calls[1].init?.redirect, 'error');
});
