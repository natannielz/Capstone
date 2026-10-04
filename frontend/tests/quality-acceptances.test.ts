import assert from 'node:assert/strict';
import test from 'node:test';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { recoveryPage, operationLabel } from '../src/modules/administration/operation-recovery-navigation';
import { identityDestinations, identityReturnPath } from '../src/modules/identity/identity-workflow';
import { SourceMemoryStorage } from './stock-source-fixtures';
import { location, target, order, observedLine, noticeId, intakeId } from './supplier-document-fixtures';
import { createQualityAcceptanceGateway, qualityAcceptanceInput, qualityAcceptanceObservation, parseQualityAcceptanceChoice, parseQualityAcceptancePreview, parseQualityAcceptanceRead, parseQualityAcceptanceResult, prepareQualityAcceptance, prepareQualityAcceptanceDecision } from '../src/modules/procurement/procurement-quality-acceptance-client';

// Wire parser fixtures only. The native browser fixture owns genuine held sources.
const receiptId = '9007199254741002', inspectionId = '9007199254741001', proposalId = '9007199254741003', actor = '770', checker = '771';
const inspected = { ...observedLine, damagedPackages: 2, openedPackages: 2, expiredPackages: 0, expiryDate: '2026-11-03' };
const originalInput = { location, orderId: order.id, orderVersion: '1' as const, orderApprovalId: order.approvalId, inspectionId, inspectionLineNumber: 1, packages: 3, poolId: '800', poolRevision: '1', receiptReference: 'HELD-001', lotReference: inspected.lotCode, expiresOn: inspected.expiryDate, reason: 'Penerimaan ditahan' };
const inspection = { id: inspectionId, version: '1' as const, hash: 'a'.repeat(64), inspectionDate: '2026-10-03', inspectionReference: 'INSPECT-001', noticeId, intakeId, lineNumber: 1, line: inspected };
const packageSource = order.lines[0], physicalPackage = { skuId: packageSource.skuId, skuRevision: packageSource.skuRevision, definitionId: packageSource.definitionId, definitionVersion: packageSource.definitionVersion, skuCode: packageSource.skuCode, familyName: packageSource.familyName, packageLabel: packageSource.packageLabel, packageKind: packageSource.packageKind, baseUnitCode: packageSource.baseUnitCode, baseUnitsPerPackage: packageSource.baseUnitsPerPackage };
const physicalOrder = { id: order.id, version: order.version, approvalId: order.approvalId, supplier: { id: order.supplier.id, displayName: order.supplier.displayName }, commercialReference: { reference: order.commercialReference.reference }, lines: [physicalPackage] };
const receipt = { id: receiptId, revision: '1' as const, input: originalInput, source: { order: physicalOrder, inspection }, lotId: '820', balanceId: '821', state: 'held_pending_valuation_and_release' as const, recordedAt: '2026-10-04T01:00:00.000Z' };
const heldBalance = { balanceId: '821', lotId: '820', poolId: '800', onHand: '3', reserved: '0', held: '3', expired: '0', revision: '1' };
const capacity = { version: '0', acceptedPackages: '0', remainingPackages: '3', lastAllocationId: null, lastHash: null };
const policy = { id: '830', revision: '1', active: true, location, qualityProcedureReference: 'QUALITY-001', putawayProcedureReference: 'PUTAWAY-001', allowNonExpiring: false, nonExpiringProcedureReference: null, maximumObservationAgeMilliseconds: '3600000', approvalReference: 'APPROVAL-001', approvedAt: '2026-10-03T01:00:00.000Z', expiresAt: '2026-10-05T01:00:00.000Z' };
const input = { location, receiptId, receiptRevision: '1' as const, acceptedPackages: 1, policyId: policy.id, policyRevision: '1', observedAt: '2026-10-04T01:05:00.000Z', qualityReference: 'MAKER-OBSERVATION', putawayReference: inspected.putawayReference, segregationReference: 'SEGREGATED-001', acceptedCondition: 'sealed_undamaged' as const, lotReference: originalInput.lotReference, expiresOn: originalInput.expiresOn, nonExpiringReason: null, reason: 'Satu kemasan diperiksa langsung' };
const choice = { receipt, heldBalance, capacity }, preview = { input, ...choice, policy, binding: 'd'.repeat(64), observedAt: '2026-10-04T01:05:01.000Z' };
const proposal = { id: proposalId, version: '1' as const, input, ...choice, policy, recordedAt: '2026-10-04T01:06:00.000Z', reason: input.reason }, pending = { proposal, decision: null, decisionReason: null, checkerObservation: null };
const observed = { observedAt: '2026-10-04T01:07:00.000Z', acceptedPackages: 1, qualityReference: 'INDEPENDENT-OBSERVATION', putawayReference: input.putawayReference, segregationReference: input.segregationReference, acceptedCondition: input.acceptedCondition, lotReference: input.lotReference, expiresOn: input.expiresOn, nonExpiringReason: null };
const shown = { proposal, decision: 'approve' as const, observation: observed, heldBalance, policy, capacity, binding: 'e'.repeat(64), observedAt: '2026-10-04T01:07:01.000Z' };
const approved = { proposalId, decisionId: '840', allocationId: '841', version: '1' as const, status: 'approved_held' as const, recordedAt: '2026-10-04T01:08:00.000Z' };
const proposeOp = 'procurement.qualityacceptance.propose', decideOp = 'procurement.qualityacceptance.decide', proposeKey = `quality-acceptance.propose.${receiptId}.aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa`, decideKey = `quality-acceptance.approve.${proposalId}.bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb`;
const command = () => prepareQualityAcceptance(actor, target, qualityAcceptanceInput(input), parseQualityAcceptancePreview(preview), parseQualityAcceptanceChoice(choice), policy);
const decisionCommand = () => prepareQualityAcceptanceDecision(checker, parseQualityAcceptanceRead(pending), shown, observed, 'Pemeriksaan independen cocok');

test('explicit accepted subset preserves overlapping observations and exact held source', () => {
  assert.equal(command().body.input.acceptedPackages, 1); assert.equal(preview.receipt.source.inspection.line.damagedPackages + preview.receipt.source.inspection.line.openedPackages, 4); assert.ok(Object.isFrozen(command().body.input));
  for (const changed of [{ acceptedPackages: '1' }, { acceptedPackages: 0 }, { receiptId: Number(receiptId) }, { acceptedCondition: '' }, { expiresOn: '2026-02-30' }, { nonExpiringReason: 'Unnecessary' }, { observedAt: '2026-10-04T08:05:00+07:00' }, { releaseStock: true }]) assert.throws(() => qualityAcceptanceInput({ ...input, ...changed }), RequestNotSentError);
  for (const changed of [{ lotReference: 'OTHER' }, { expiresOn: '2026-11-04' }, { acceptedPackages: 4 }, { observedAt: '2026-10-04T00:59:59.000Z' }, { acceptedCondition: 'intact_as_defined' }]) assert.throws(() => parseQualityAcceptancePreview({ ...preview, input: { ...input, ...changed } }), AccessFailure);
  for (const changed of [{ held: '2' }, { balanceId: '999' }, { onHand: '4' }, { reserved: '1' }]) assert.throws(() => parseQualityAcceptanceChoice({ ...choice, heldBalance: { ...heldBalance, ...changed } }));
  assert.equal(qualityAcceptanceInput({ ...input, observedAt: '2026-10-04T01:05:00+00:00' }).observedAt, input.observedAt);
});

test('non-expiring lots require actual null expiry, approved policy and independent agreed reasons', () => {
  const noExpiryReceipt = { ...receipt, input: { ...receipt.input, expiresOn: null }, source: { ...receipt.source, inspection: { ...inspection, line: { ...inspected, expiryDate: null } } } }, noExpiryInput = { ...input, expiresOn: null, nonExpiringReason: 'Barang memang tidak memiliki kedaluwarsa' }, approvedPolicy = { ...policy, allowNonExpiring: true, nonExpiringProcedureReference: 'NONEXP-001' };
  const checked = parseQualityAcceptancePreview({ ...preview, receipt: noExpiryReceipt, input: noExpiryInput, policy: approvedPolicy }); assert.equal(checked.input.expiresOn, null);
  assert.throws(() => parseQualityAcceptancePreview({ ...preview, input: noExpiryInput, policy: approvedPolicy }));
  assert.throws(() => parseQualityAcceptancePreview({ ...preview, receipt: noExpiryReceipt, input: noExpiryInput }));
  assert.throws(() => qualityAcceptanceObservation({ ...observed, expiresOn: null, nonExpiringReason: null }));
  for (const packageKind of ['loose', 'other'] as const) {
    const otherReceipt = { ...receipt, source: { ...receipt.source, order: { ...physicalOrder, lines: [{ ...physicalPackage, packageKind }] } } };
    assert.equal(parseQualityAcceptancePreview({ ...preview, receipt: otherReceipt, input: { ...input, acceptedCondition: 'intact_as_defined' } }).input.acceptedCondition, 'intact_as_defined');
    assert.throws(() => parseQualityAcceptancePreview({ ...preview, receipt: otherReceipt }));
  }
});

test('quality history contains only physical facts and rejects financial or unrelated package additions', () => {
  for (const changed of [{ ...physicalOrder, paymentTerms: order.paymentTerms }, { ...physicalOrder, requiredAdvance: '0.00' }, { ...physicalOrder, total: order.total }, { ...physicalOrder, lines: [{ ...physicalPackage, netPricePerPackage: '12.34' }] }, { ...physicalOrder, lines: [physicalPackage, { ...physicalPackage, skuId: '999' }] }]) assert.throws(() => parseQualityAcceptanceChoice({ ...choice, receipt: { ...receipt, source: { ...receipt.source, order: changed } } }));
  for (const extra of [{ pool: { active: false } }, { valuation: null }, { unitCost: '1.00' }]) assert.throws(() => parseQualityAcceptanceChoice({ ...choice, receipt: { ...receipt, ...extra } }));
  const rejected = prepareQualityAcceptanceDecision(checker, parseQualityAcceptanceRead(pending), { ...shown, decision: 'reject', observation: null, policy: { ...policy, active: false }, capacity: { version: '1', acceptedPackages: '1', remainingPackages: '2', lastAllocationId: '999', lastHash: 'a'.repeat(64) } }, null, 'Tolak setelah sumber berubah');
  assert.equal(rejected.body.observation, null); assert.equal(rejected.body.decision, 'reject');
});

test('checker must enter own complete agreeing observations and current finite policy', () => {
  assert.equal(decisionCommand().body.observation!.qualityReference, 'INDEPENDENT-OBSERVATION');
  for (const changed of [{ acceptedPackages: 2 }, { lotReference: 'OTHER' }, { putawayReference: 'OTHER' }, { segregationReference: 'OTHER' }, { expiresOn: '2026-11-04' }, { acceptedCondition: 'intact_as_defined' as const }, { observedAt: '2026-10-04T01:08:00.000Z' }]) assert.throws(() => prepareQualityAcceptanceDecision(checker, parseQualityAcceptanceRead(pending), { ...shown, observation: { ...observed, ...changed } }, observed, 'Checked'));
  assert.throws(() => prepareQualityAcceptanceDecision(checker, parseQualityAcceptanceRead(pending), { ...shown, observation: null }, null, 'Checked'));
  assert.throws(() => parseQualityAcceptancePreview({ ...preview, observedAt: '2026-10-04T02:05:00.000Z' }));
  assert.throws(() => parseQualityAcceptancePreview({ ...preview, policy: { ...policy, maximumObservationAgeMilliseconds: '2592000001' } }));
  assert.throws(() => prepareQualityAcceptance(actor, target, input, parseQualityAcceptancePreview(preview), { ...choice, heldBalance: { ...heldBalance, revision: '2' } }, policy), RequestNotSentError);
});

test('quality decision transport binds actor, fresh expected revision and exact independent observation', async () => {
  let writes = 0;
  const gateway = createQualityAcceptanceGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => { assert.ok(request instanceof Request); if (request.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); writes++; assert.equal(request.headers.get('X-Expected-Actor-Id'), checker); assert.equal(request.headers.get('If-Match'), '"1"'); assert.equal(request.headers.get('Idempotency-Key'), decideKey); assert.equal(request.cache, 'no-store'); assert.equal(request.redirect, 'error'); assert.deepEqual(await request.json(), decisionCommand().body); return Response.json(approved); } }));
  await assert.rejects(gateway.decide(decisionCommand(), decideKey, () => false), RequestNotSentError); assert.equal(writes, 0); assert.deepEqual(await gateway.decide(decisionCommand(), decideKey, () => true), approved);
  for (const changed of [{ allocationId: null }, { status: 'released' }, { decisionId: null }, { proposalId: '999' }, { version: 1 }, { movementId: '999' }]) assert.throws(() => parseQualityAcceptanceResult({ ...approved, ...changed }, decideKey, decideOp));
  assert.throws(() => parseQualityAcceptanceRead({ proposal, decision: approved, decisionReason: 'Checked', checkerObservation: { ...observed, acceptedPackages: 2 } }));
});

test('lost approved-held reply recovers only exact original owner and action-only users remain reachable', async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => decideKey); let writes = 0;
  await assert.rejects(submitTrackedRequest(ledger, journal, checker, 'quality', decideOp, decisionCommand().body, async () => { writes++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
  const entry = journal.list(checker)[0]; assert.ok(entry); assert.equal(journal.list(actor).length, 0); assert.deepEqual(Object.keys(JSON.parse([...storage.values.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  let correct = false;
  const gateway = createQualityAcceptanceGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => { assert.ok(request instanceof Request); if (request.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); assert.equal(request.headers.get('Idempotency-Key'), null); if (request.url.endsWith('/operations/query')) return Response.json({ state: 'confirmed', result: approved }); return Response.json({ proposal, decision: approved, decisionReason: 'Checked', checkerObservation: correct ? observed : { ...observed, lotReference: 'OTHER' } }); } }));
  const legacy = { lookup: async () => { throw Error('No generic lookup'); }, close: async () => { throw Error('No generic close'); } }, recover = () => reconcileOperationRecovery(entry, journal, legacy, { 'procurement.qualityacceptance': { lookup: item => gateway.recover(item.actorId, item.operation, item.key) } });
  await assert.rejects(recover()); assert.equal(journal.list(checker).length, 1); correct = true; assert.deepEqual(await recover(), { state: 'committed', result: { id: proposalId, version: '1' } }); assert.equal(journal.list(checker).length, 0); assert.equal(writes, 1);
  assert.deepEqual(await gateway.recover(checker, decideOp, decideKey, false), { state: 'committed', result: approved });
  assert.equal(identityReturnPath('/procurement/quality-acceptances'), '/procurement/quality-acceptances'); assert.equal(recoveryPage(proposeOp).href, '/procurement/quality-acceptances'); assert.equal(operationLabel(decideOp), 'Keputusan penerimaan mutu barang');
  for (const action of ['read', 'propose', 'decide']) assert.ok(identityDestinations(['procurement.qualityacceptance.' + action]).some(item => item.href === '/procurement/quality-acceptances'));
  assert.equal(parseQualityAcceptanceResult({ proposalId, decisionId: null, allocationId: null, version: '1', status: 'proposed', recordedAt: proposal.recordedAt }, proposeKey, proposeOp).status, 'proposed');
});

test('receipt and policy pages allow skipped rows but cannot cross scope or repeat a cursor', async () => {
  let next: unknown = { location, items: [], nextReceiptId: '800' };
  const gateway = createQualityAcceptanceGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => { assert.ok(request instanceof Request); return request.url.endsWith('/csrf') ? Response.json({ requestToken: 'csrf' }) : Response.json(next); } }));
  assert.equal((await gateway.receipts(actor, location, '900')).nextReceiptId, '800'); next = { location, items: [], nextReceiptId: '900' }; await assert.rejects(gateway.receipts(actor, location, '900'));
  next = { location: { ...location, shopId: '999' }, items: [], nextReceiptId: null }; await assert.rejects(gateway.receipts(actor, location));
  next = { receiptId, items: [], nextPolicyId: '800' }; assert.equal((await gateway.policies(actor, parseQualityAcceptanceChoice(choice), '900')).nextPolicyId, '800');
  next = { receiptId: '999', items: [], nextPolicyId: null }; await assert.rejects(gateway.policies(actor, parseQualityAcceptanceChoice(choice)));
});
