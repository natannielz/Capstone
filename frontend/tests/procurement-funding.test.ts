import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { operationLabel, recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
import { identityDestinations, identityReturnPath, loginFields, parseIdentitySession, stepUpFields } from '../src/modules/identity/identity-workflow';
import { createFundingGateway, fundingInput, fundingPolicyEligible, fundingKeyOwner, parseFundingChoices, parseFundingRequest, parseFundingResult, prepareFunding, prepareFundingDecision } from '../src/modules/procurement/procurement-funding-client';
import { SourceMemoryStorage, sourceSession } from './stock-source-fixtures';

const actor = '9007199254740993', orderId = '9007199254740994', requestId = '9007199254740995';
const location = { organizationId: '100', shopId: '101', warehouseId: '102', channelId: '103' };
const organization = { id: '100', code: 'TOKO', name: 'Koperasi', scopeVersion: '1', profileVersion: '2' };
const source = { organization, shop: { ...organization, id: '101', code: 'SHOP', name: 'Toko' }, warehouse: { ...organization, id: '102', code: 'WARE', name: 'Gudang' }, channel: { ...organization, id: '103', code: 'RETAIL', name: 'Ritel' } }, target = { location, ...source };
const line = { skuId: '500', skuRevision: '2', definitionId: '501', definitionVersion: '1', skuCode: 'PAPER-BOX', familyName: 'Kertas', packageLabel: 'Karton 12', packageKind: 'sealed' as const, baseUnitCode: 'RIM', baseUnitsPerPackage: 12, packages: 2, specification: 'Kemasan utuh', sourceSystem: 'supplier' as const, sourceAccountReference: '0007', externalItemCode: '00Ab-01', netPricePerPackage: '12.34', taxAmount: '1.23', supplierChargeAmount: '0.04', netAmount: '24.68', totalAmount: '25.95', baseUnits: '24' };
const order = { id: orderId, version: '1' as const, approvalId: '700', proposalId: '701', route: 'ddo' as const, businessDate: '2026-10-03', currency: 'IDR' as const, supplier: { id: '600', versionId: '601', version: '1', subjectReference: 'SUP-001', displayName: 'Sumber Kertas' }, total: '25.95', requiredAdvance: '20.00',
  paymentTerms: { kind: 'deposit' as const, description: 'Uang muka sesuai penawaran', requiredAdvance: '20.00', balanceTrigger: 'on_receipt' as const, dueDate: null }, commercialReference: { kind: 'quote' as const, reference: '000QUOTE-A', issuedDate: '2026-10-02', validUntil: '2026-10-04T00:00:00.000Z' }, externalOrderReference: null, lines: [line] };
const capacity = { orderId, orderVersion: '1' as const, requiredAdvance: '20.00', authorized: '5.00', remaining: '15.00', version: '1' };
const policy = { id: '800', location, revision: '1', eligibleRoutes: ['ddo' as const], allowInstallments: true, reserveUndisbursed: true as const, requiresAdditionalBudgetSource: false, requiresAdditionalLimitSource: false, operatingOwnerReference: 'Finance koperasi', procedureReference: 'Prosedur 001', approvalReference: 'Persetujuan 001', approvedAt: '2026-10-01T00:00:00.000Z', expiresAt: '2026-11-01T00:00:00.000Z', active: true };
const chosen = { order, capacity }, choices = { location, source, items: [chosen], policies: [policy], nextOrderId: null };
const input = { location, orderId, orderVersion: '1' as const, orderApprovalId: '700', policyId: '800', policyRevision: '1', amount: '10.01', businessDate: '2026-10-03', neededBy: '2026-10-04' };
const facts = { input, source, order, policy, capacity }, preview = { ...facts, binding: 'a'.repeat(64), observedAt: '2026-10-03T01:00:00.000Z' };
const request = { ...facts, id: requestId, version: '1' as const, reason: 'Uang muka sesuai kebutuhan nyata', requestedAt: '2026-10-03T01:01:00.000Z', state: 'requested' as const, decision: null };
const decisionPreview = { request, decision: 'approve' as const, capacity: { ...capacity, authorized: '8.00', remaining: '12.00', version: '2' }, binding: 'b'.repeat(64), observedAt: '2026-10-03T01:02:00.000Z' };
const requested = { requestId, requestVersion: '1' as const, decisionId: null, authorizationId: null, authorizedAmount: null, status: 'requested' as const }, approved = { ...requested, decisionId: '810', authorizationId: '811', authorizedAmount: '10.01', status: 'approved' as const };
const approvedRequest = { ...request, state: 'approved' as const, decision: { id: '810', decision: 'approve' as const, reason: 'Sesuai kebijakan', decidedAt: decisionPreview.observedAt, authorizationId: '811', authorizedAmount: '10.01' } };
const uuid = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', requestKey = `pf.request.${orderId}.${uuid}`, approveKey = `pf.approve.${requestId}.${uuid}`;
const command = () => prepareFunding(actor, target, choices, chosen, policy, input, preview, request.reason);
const decisionCommand = () => prepareFundingDecision(actor, request, decisionPreview, 'Tinjau batas yang tersisa');

test('funding uses explicit positive decimal amounts and real business dates without inferring an advance or reducing the request', () => {
  assert.deepEqual(fundingInput(input), input);
  for (const value of ['', '0.00', '01.00', '1e2', '-1.00', '0.001', '1000000000000000.00']) assert.throws(() => fundingInput({ ...input, amount: value }), RequestNotSentError);
  for (const changed of [{ ...input, neededBy: '2026-10-02' }, { ...input, businessDate: '2026-02-30' }, { ...input, businessDate: '0001-01-01' }]) assert.throws(() => fundingInput(changed), RequestNotSentError);
  assert.ok(Object.isFrozen(command().body.input)); assert.equal(command().body.input.amount, '10.01');
  assert.throws(() => prepareFunding(actor, target, choices, chosen, policy, input, { ...preview, input: { ...input, amount: '10.00' } }, request.reason));
  assert.throws(() => prepareFunding(actor, target, choices, chosen, policy, { ...input, amount: '15.01' }, { ...preview, input: { ...input, amount: '15.01' } }, request.reason));
});

test('actual finance policy controls installments, dates and routes; unavailable additional sources cannot become usable defaults', () => {
  for (const changed of [{ ...policy, active: false }, { ...policy, revision: '2' }, { ...policy, requiresAdditionalBudgetSource: true }, { ...policy, requiresAdditionalLimitSource: true }, { ...policy, eligibleRoutes: ['smart' as const] }])
    assert.throws(() => prepareFunding(actor, target, { ...choices, policies: [changed] }, chosen, changed, input, { ...preview, policy: changed }, request.reason));
  assert.equal(fundingPolicyEligible(policy, 'ddo', Date.parse(policy.expiresAt)), false); assert.equal(fundingPolicyEligible(policy, 'smart', Date.parse(preview.observedAt)), false);
  assert.throws(() => prepareFunding(actor, target, choices, chosen, policy, input, { ...preview, observedAt: policy.expiresAt }, request.reason));
  const wholePolicy = { ...policy, allowInstallments: false }, wholeCapacity = { ...capacity, authorized: '0.00', remaining: '20.00', version: '0' }, wholeChoice = { order, capacity: wholeCapacity }, wholeInput = { ...input, amount: '20.00' }, wholePreview = { ...preview, policy: wholePolicy, capacity: wholeCapacity, input: wholeInput };
  assert.equal(prepareFunding(actor, target, { ...choices, policies: [wholePolicy], items: [wholeChoice] }, wholeChoice, wholePolicy, wholeInput, wholePreview, request.reason).body.input.amount, '20.00');
  assert.throws(() => prepareFunding(actor, target, { ...choices, policies: [wholePolicy] }, chosen, wholePolicy, input, { ...preview, policy: wholePolicy }, request.reason));
});

test('funding review binds historical PO lines, exact authority and reconciled capacity without payment or cash claims', () => {
  assert.deepEqual(parseFundingChoices(choices), choices); assert.deepEqual(parseFundingRequest(approvedRequest), approvedRequest);
  for (const changed of [{ ...choices, items: [{ ...chosen, capacity: { ...capacity, remaining: '15.01' } }] }, { ...choices, items: [{ ...chosen, capacity: { ...capacity, orderVersion: '2' } }] }, { ...choices, items: [{ ...chosen, order: { ...order, lines: [{ ...line, baseUnits: '12' }] } }] },
    { ...choices, items: [{ ...chosen, order: { ...order, paymentTerms: { ...order.paymentTerms, kind: 'credit', requiredAdvance: '0.00' }, requiredAdvance: '0.00' } }] }, { ...choices, items: [{ ...chosen, order: { ...order, bankAccount: 'private' } }] }]) assert.throws(() => parseFundingChoices(changed), AccessFailure);
  for (const changed of [{ ...preview, order: { ...order, supplier: { ...order.supplier, versionId: '602' } } }, { ...preview, source: { ...source, warehouse: { ...source.warehouse, id: '202' } } }, { ...preview, capacity: { ...capacity, orderId: '99' } }]) assert.throws(() => prepareFunding(actor, target, choices, chosen, policy, input, changed, request.reason));
  for (const changed of [{ ...approvedRequest, bankTransferCompleted: true }, { ...approvedRequest, decision: { ...approvedRequest.decision, authorizedAmount: '10.00' } }, { ...request, decision: approvedRequest.decision }]) assert.throws(() => parseFundingRequest(changed));
});

test('checker keeps original request and reviews fresh capacity, failing when another authorization has used the remaining advance', () => {
  assert.ok(Object.isFrozen(decisionCommand().body)); assert.equal(decisionCommand().amount, input.amount);
  for (const changed of [{ ...decisionPreview, capacity: null }, { ...decisionPreview, capacity: { ...capacity, authorized: '10.00', remaining: '10.00' } }, { ...decisionPreview, capacity: { ...capacity, orderId: '8' } },
    { ...decisionPreview, request: { ...request, reason: 'Changed' } }, { ...decisionPreview, observedAt: policy.expiresAt }]) assert.throws(() => prepareFundingDecision(actor, request, changed, 'Alasan'));
  assert.equal(prepareFundingDecision(actor, request, { ...decisionPreview, decision: 'reject', capacity: null, observedAt: policy.expiresAt }, 'Tolak kebijakan kedaluwarsa').decision, 'reject');
});

test('funding transport binds actor CSRF and If-Match, requires exact approved amount and never resends denial or actor-change failures', async () => {
  let calls = 0; const gateway = createFundingGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++; assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(input.cache, 'no-store'); assert.equal(input.redirect, 'error'); assert.equal(input.credentials, 'same-origin');
    if (input.url.endsWith('/requests')) { assert.equal(input.headers.get('Idempotency-Key'), requestKey); assert.deepEqual(await input.json(), command().body); return Response.json(requested); }
    assert.equal(input.headers.get('If-Match'), '"1"'); assert.equal(input.headers.get('Idempotency-Key'), approveKey); assert.deepEqual(await input.json(), decisionCommand().body);
    if (calls === 3) return Response.json({ code: 'step_up_required' }, { status: 403 }); return Response.json(calls === 4 ? { ...approved, authorizedAmount: '10.00' } : approved);
  } }));
  await assert.rejects(gateway.submit(command(), requestKey, () => false), RequestNotSentError); assert.equal(calls, 0); assert.deepEqual(await gateway.submit(command(), requestKey, () => true), requested); assert.deepEqual(await gateway.decide(decisionCommand(), approveKey, () => true), approved);
  await assert.rejects(gateway.decide(decisionCommand(), approveKey, () => true), { code: 'step_up_required' }); await assert.rejects(gateway.decide(decisionCommand(), approveKey, () => true), { code: 'procurement_funding_response_invalid' });
  let actorChecks = 0; await assert.rejects(gateway.decide(decisionCommand(), approveKey, () => ++actorChecks === 1), { code: 'commit_outcome_unknown' }); assert.equal(calls, 5);
});

test('funding recovery persists only original actor references and proves order/request and exact authorization, without resend or generic close', async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => approveKey), op = 'procurement.funding.approve'; let attempts = 0;
  await assert.rejects(submitTrackedRequest(ledger, journal, actor, 'funding-approve', op, decisionCommand().body, async () => { attempts++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
  const entry = journal.list(actor)[0], serialized = [...storage.values.values()][0]; assert.deepEqual(Object.keys(JSON.parse(serialized)).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.equal(journal.list('99').length, 0); assert.equal(serialized.includes(policy.procedureReference), false); assert.equal(serialized.includes(input.amount), false);
  const legacy = { lookup: async () => { throw new Error('No generic lookup'); }, close: async () => { throw new Error('No generic close'); } };
  assert.deepEqual(operationRecoveryCapabilities(op), { canCheck: false, canClose: false, unavailable: 'adapter' }); assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'procurement.funding': { lookup: async () => null } }), { state: 'unconfirmed' });
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'procurement.funding': { lookup: async () => ({ state: 'committed', result: approved }) } }, 'Close')); assert.equal(journal.list(actor).length, 1);
  let wrong = false; const gateway = createFundingGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('Idempotency-Key'), null);
    if (input.url.endsWith('/operations/query')) { const body = await input.json(); assert.ok(body !== null && typeof body === 'object' && 'operation' in body); return Response.json({ status: 'confirmed', result: body.operation === op ? (wrong ? { ...approved, authorizedAmount: '9.00' } : approved) : requested }); }
    assert.deepEqual(await input.json(), { purpose: 'funding-review' }); return Response.json(approvedRequest);
  } }));
  assert.deepEqual(await gateway.recover(actor, 'procurement.funding.request', requestKey), { state: 'committed', result: requested }); wrong = true; await assert.rejects(gateway.recover(actor, op, approveKey)); wrong = false;
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'procurement.funding': { lookup: entry => gateway.recover(entry.actorId, entry.operation, entry.key) } }), { state: 'committed', result: { id: requestId, version: '1' } }); assert.equal(journal.list(actor).length, 0); assert.equal(attempts, 1);
  assert.throws(() => parseFundingResult({ ...approved, authorizationId: null }, approveKey, op)); assert.throws(() => fundingKeyOwner(approveKey, 'procurement.funding.reject'));
});

test('funding navigation requires explicit finance grants and an exact step-up return path', () => {
  const path = '/procurement/funding'; assert.equal(identityReturnPath(path), path); assert.deepEqual(loginFields(path, '', ''), { returnPath: path });
  for (const permission of ['funding.read', 'funding.request', 'funding.approve']) assert.equal(identityDestinations([permission]).some(item => item.href === path), true);
  for (const permission of ['funding.request', 'funding.approve']) { const session = parseIdentitySession({ ...sourceSession, stepUpOperations: [permission] }); assert.deepEqual(stepUpFields(session, permission, path), { expectedActorId: session.account!.userId, operation: permission, returnPath: path }); }
  for (const permission of ['procurement.read', 'procurement.approve', 'supplier.change.approve']) assert.equal(identityDestinations([permission]).some(item => item.href === path), false);
  assert.equal(recoveryPage('procurement.funding.approve').href, path); assert.equal(operationLabel('procurement.funding.approve'), 'Otorisasi uang muka'); for (const invalid of [path + '/', path + '?return=https://elsewhere.test', '//procurement/funding']) assert.throws(() => loginFields(invalid, '', ''));
});
