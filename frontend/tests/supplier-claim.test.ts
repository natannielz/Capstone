import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { operationLabel, recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
import { identityDestinations, identityReturnPath, loginFields, parseIdentitySession, stepUpFields } from '../src/modules/identity/identity-workflow';
import { createSupplierClaimGateway, supplierClaimInput, supplierClaimKeyOwner, parseSupplierClaimResult, parseSupplierClaimRead, parseSupplierClaimInspections, prepareSupplierClaim, prepareSupplierClaimDecision } from '../src/modules/procurement/supplier-claim-client';
import { SourceMemoryStorage, sourceSession } from './stock-source-fixtures';
import { actor, location, target, order, observedLine, noticeId, intakeId } from './supplier-document-fixtures';

const inspectionId = '9007199254741001', claimId = '9007199254741002', checker = '9007199254741003';
const line = { basis: 'physical_received' as const, inspectionId, inspectionLineNumber: 1, packages: 1, issue: 'damaged' as const, invoiceDocumentId: null, invoiceDocumentLineNumber: null, invoiceMatchId: null, invoiceMatchLineNumber: null };
const input = { location, orderId: order.id, orderVersion: '1' as const, orderApprovalId: order.approvalId, claimReference: '000CLAIM-a', businessDate: '2026-10-03', responseDueOn: '2026-10-12', deadlineBasisReference: 'Ketentuan pemasok 001', requestedResolution: 'replacement' as const, reason: 'Kemasan rusak dari hasil pemeriksaan nyata', lines: [line] };
const capacity = { basis: line.basis, inspectionId, inspectionLineNumber: 1, inspectionHash: 'a'.repeat(64), headId: null, version: '0', totalPackages: '3', occupiedPackages: '0', remainingPackages: '3' };
const occupied = { ...capacity, headId: '810', version: '1', occupiedPackages: '1', remainingPackages: '2' };
const inspection = { id: inspectionId, version: '1' as const, hash: capacity.inspectionHash, inspectionDate: input.businessDate, inspectionReference: '000INSPECT-a', noticeId, intakeId, line: observedLine };
const resolved = { lineNumber: 1, input: line, inspection, invoice: null };
const preview = { input, order, lines: [resolved], capacity: [capacity], binding: 'b'.repeat(64), observedAt: '2026-10-03T01:00:00.000Z' };
const request = { id: claimId, version: '1' as const, input, order, lines: [resolved], requestedBy: actor, recordedAt: '2026-10-03T01:01:00.000Z', state: 'requested' as const };
const read = { request, decision: null, capacity: [occupied], state: 'requested' as const };
const decisionPreview = { request, decision: 'approve' as const, capacity: [occupied], binding: 'c'.repeat(64), observedAt: '2026-10-03T01:02:00.000Z' };
const decision = { id: '811', claimId, decision: 'approve' as const, explanation: 'Sumber, jumlah dan tenggat telah ditinjau secara independen', decidedBy: checker, recordedAt: decisionPreview.observedAt, state: 'approved_pending_supplier_action' as const };
const approvedRead = { ...read, decision, state: decision.state };
const requested = { claimId, claimVersion: '1' as const, decisionId: null, decision: null, state: 'requested' as const }, approved = { ...requested, decisionId: decision.id, decision: decision.decision, state: decision.state };
const choices = { location, orderId: order.id, items: [{ inspectionId, version: '1' as const, inspectionReference: inspection.inspectionReference, inspectionDate: inspection.inspectionDate, noticeId, intakeId, lines: [{ lineNumber: 1, input: observedLine, physical: capacity, shortage: { ...capacity, basis: 'shortage_observed' as const, totalPackages: '0', remainingPackages: '0' } }] }], nextInspectionId: null };
const uuid = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', requestKey = `sc.request.${order.id}.${uuid}`, approveKey = `sc.approve.${claimId}.${uuid}`;
const command = () => prepareSupplierClaim(actor, target, order, input, preview);
const approveCommand = () => prepareSupplierClaimDecision(checker, read, decisionPreview, decision.explanation, 'Keputusan internal berdasarkan fakta pemeriksaan');

test('claim input keeps physical and shortage bases distinct, exact references and explicit deadlines without invented amounts', () => {
  assert.deepEqual(supplierClaimInput(input), input);
  assert.equal(supplierClaimInput({ ...input, claimReference: '000A\u200dB' }).claimReference, '000A\u200dB');
  for (const changed of [{ ...input, lines: [{ ...line, packages: '1' }] }, { ...input, lines: [{ ...line, inspectionId: 9007199254741001 }] }, { ...input, lines: [{ ...line, basis: 'shortage_observed' }] }, { ...input, lines: [{ ...line, issue: 'shortage' }] }, { ...input, lines: [line, line] }, { ...input, lines: [{ ...line, invoiceMatchId: '99', invoiceMatchLineNumber: 1 }] }, { ...input, responseDueOn: '2026-10-02' }, { ...input, businessDate: '2026-02-30' }, { ...input, claimReference: ' 000A' }, { ...input, reason: 'A\nB' }, { ...input, amount: '0.00' }]) assert.throws(() => supplierClaimInput(changed), RequestNotSentError);
  const shortage = supplierClaimInput({ ...input, lines: [{ ...line, basis: 'shortage_observed', issue: 'shortage' }] }); assert.equal(shortage.lines[0].basis, 'shortage_observed'); assert.equal(Object.hasOwn(shortage.lines[0], 'amount'), false);
});

test('genuine inspection choices reconcile each separate capacity and retain overlapping condition observations', () => {
  assert.deepEqual(parseSupplierClaimInspections(choices), choices);
  const source = choices.items[0], row = source.lines[0];
  for (const changed of [{ ...choices, items: [{ ...source, lines: [{ ...row, physical: { ...capacity, remainingPackages: '4' } }] }] }, { ...choices, items: [{ ...source, lines: [{ ...row, shortage: { ...row.shortage, totalPackages: '1', remainingPackages: '1' } }] }] }, { ...choices, items: [{ ...source, lines: [{ ...row, physical: { ...capacity, inspectionId: '9' } }] }] }, { ...choices, items: [{ ...source, privateVideo: 'forbidden' }] }]) assert.throws(() => parseSupplierClaimInspections(changed), AccessFailure);
  assert.equal(parseSupplierClaimRead(read).request.lines[0].invoice, null);
  for (const changed of [{ ...read, capacity: [{ ...occupied, inspectionHash: 'f'.repeat(64) }] }, { ...read, request: { ...request, lines: [{ ...resolved, inspection: { ...inspection, line: { ...observedLine, definitionVersion: '2' } } }] } }, { ...read, request: { ...request, evidence: { private: true } } }, { ...approvedRead, state: 'paid' }]) assert.throws(() => parseSupplierClaimRead(changed), AccessFailure);
});

test('frozen review binds exact PO, source and quantity; internal decision keeps coverage and does not infer a supplier settlement', () => {
  const frozen = command(); assert.equal(Object.isFrozen(frozen.body.input.lines[0]), true); assert.deepEqual(frozen.body.input, input);
  assert.throws(() => prepareSupplierClaim(actor, { ...target, location: { ...location, warehouseId: '999' }, warehouse: { ...target.warehouse, id: '999' } }, order, input, preview));
  assert.throws(() => prepareSupplierClaim(actor, target, order, input, { ...preview, input: { ...input, reason: 'Different reason' } }));
  assert.throws(() => prepareSupplierClaim(actor, target, order, input, { ...preview, capacity: [{ ...occupied, occupiedPackages: '3', remainingPackages: '0' }] }));
  assert.equal(approveCommand().body.decision, 'approve'); assert.equal(Object.isFrozen(approveCommand().body), true); assert.deepEqual(parseSupplierClaimRead(approvedRead).capacity, read.capacity);
  assert.throws(() => prepareSupplierClaimDecision(checker, approvedRead, decisionPreview, decision.explanation, 'Reason'));
  assert.throws(() => prepareSupplierClaimDecision(checker, read, { ...decisionPreview, request: { ...request, input: { ...input, reason: 'Different' } } }, decision.explanation, 'Reason'));
  assert.equal(prepareSupplierClaimDecision(checker, read, { ...decisionPreview, decision: 'reject' }, 'Bukti perlu dilengkapi', 'Tetap ditahan').body.decision, 'reject');
});

test('claim transport uses original actor, CSRF and exact preconditions; denied or changed sessions are never resent', async () => {
  let calls = 0; const gateway = createSupplierClaimGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async incoming => {
    assert.ok(incoming instanceof Request); if (incoming.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++;
    assert.equal(incoming.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(incoming.cache, 'no-store'); assert.equal(incoming.redirect, 'error'); assert.equal(incoming.credentials, 'same-origin');
    if (incoming.url.endsWith('/requests')) { assert.equal(incoming.headers.get('X-Expected-Actor-Id'), actor); assert.equal(incoming.headers.get('Idempotency-Key'), requestKey); assert.deepEqual(await incoming.json(), command().body); return Response.json(requested); }
    assert.equal(incoming.headers.get('X-Expected-Actor-Id'), checker); assert.equal(incoming.headers.get('If-Match'), '"1"');
    if (incoming.url.endsWith('/decision-preview/query')) { assert.equal(incoming.headers.get('Idempotency-Key'), null); assert.deepEqual(await incoming.json(), { purpose: 'supplier-claim-review', decision: 'approve' }); return Response.json(decisionPreview); }
    assert.equal(incoming.headers.get('Idempotency-Key'), approveKey); assert.deepEqual(await incoming.json(), approveCommand().body); return calls === 4 ? Response.json({ code: 'step_up_required' }, { status: 403 }) : Response.json(approved);
  } }));
  await assert.rejects(gateway.submit(command(), requestKey, () => false), RequestNotSentError); assert.equal(calls, 0);
  assert.deepEqual(await gateway.submit(command(), requestKey, () => true), requested); assert.deepEqual(await gateway.previewDecision(checker, read, 'approve'), decisionPreview); assert.deepEqual(await gateway.decide(approveCommand(), approveKey, () => true), approved);
  await assert.rejects(gateway.decide(approveCommand(), approveKey, () => true), { code: 'step_up_required' }); let current = 0; await assert.rejects(gateway.decide(approveCommand(), approveKey, () => ++current === 1), { code: 'commit_outcome_unknown' }); assert.equal(calls, 5);
});

test('unknown claim result recovery stores only the original actor key, verifies saved source and never resends or closes generically', async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => approveKey), op = 'procurement.supplierclaim.approve'; let attempts = 0;
  await assert.rejects(submitTrackedRequest(ledger, journal, checker, 'supplier-claim-approve', op, approveCommand().body, async () => { attempts++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
  const entry = journal.list(checker)[0]; assert.ok(entry); const serialized = [...storage.values.values()][0]; assert.deepEqual(Object.keys(JSON.parse(serialized)).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.equal(journal.list(actor).length, 0); assert.equal(serialized.includes(decision.explanation), false);
  const legacy = { lookup: async () => { throw new Error('No generic lookup'); }, close: async () => { throw new Error('No generic close'); } };
  assert.deepEqual(operationRecoveryCapabilities(op), { canCheck: false, canClose: false, unavailable: 'adapter' });
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'procurement.supplierclaim': { lookup: async () => null } }), { state: 'unconfirmed' });
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'procurement.supplierclaim': { lookup: async () => ({ state: 'committed', result: approved }) } }, 'Close')); assert.equal(journal.list(checker).length, 1);
  let mode: 'missing' | 'saved' | 'wrong' = 'missing'; let lookupCalls = 0;
  const gateway = createSupplierClaimGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async incoming => {
    assert.ok(incoming instanceof Request); if (incoming.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); lookupCalls++; assert.equal(incoming.headers.get('X-Expected-Actor-Id'), checker); assert.equal(incoming.headers.get('Idempotency-Key'), null);
    if (incoming.url.endsWith('/operations/query')) { const body = await incoming.json(); assert.deepEqual(body, { purpose: 'operation-recovery', operation: op, key: approveKey }); return Response.json(mode === 'missing' ? { status: 'not_found', result: null } : { status: 'committed', result: approved }); }
    assert.deepEqual(await incoming.json(), { purpose: 'supplier-claim-review' }); return Response.json(mode === 'wrong' ? { ...approvedRead, decision: { ...decision, id: '999' } } : approvedRead);
  } }));
  assert.equal(await gateway.recover(checker, op, approveKey), null); mode = 'wrong'; await assert.rejects(gateway.recover(checker, op, approveKey)); mode = 'saved';
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'procurement.supplierclaim': { lookup: item => gateway.recover(item.actorId, item.operation, item.key) } }), { state: 'committed', result: { id: claimId, version: '1' } }); assert.equal(journal.list(checker).length, 0); assert.equal(attempts, 1); assert.equal(lookupCalls, 5);
  assert.throws(() => parseSupplierClaimResult({ ...approved, decisionId: null }, approveKey, op)); assert.throws(() => supplierClaimKeyOwner(approveKey, 'procurement.supplierclaim.reject'));
});

test('claim navigation follows read grants, exact return paths and distinct fresh maker/checker operations', () => {
  const path = '/procurement/supplier-claims'; assert.equal(identityReturnPath(path), path); assert.deepEqual(loginFields(path, '', ''), { returnPath: path });
  assert.equal(identityDestinations(['procurement.read']).some(item => item.href === path), true); assert.equal(identityDestinations(['funding.approve']).some(item => item.href === path), false);
  for (const permission of ['supplier.claim', 'supplier.claim.approve']) { const session = parseIdentitySession({ ...sourceSession, stepUpOperations: [permission] }); assert.deepEqual(stepUpFields(session, permission, path), { expectedActorId: session.account!.userId, operation: permission, returnPath: path }); }
  assert.equal(recoveryPage('procurement.supplierclaim.approve').href, path); assert.equal(operationLabel('procurement.supplierclaim.approve'), 'Persetujuan internal klaim pemasok');
  for (const invalid of [path + '/', path + '?next=https://elsewhere.test', '//procurement/supplier-claims']) assert.throws(() => loginFields(invalid, '', ''));
});
