import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { recoveryPage, operationLabel } from '../src/modules/administration/operation-recovery-navigation';
import { identityDestinations, identityReturnPath, loginFields, parseIdentitySession, stepUpFields } from '../src/modules/identity/identity-workflow';
import { createProcurementStockReceiptGateway, parseStockReceipt, parseStockReceiptPools, parseStockReceiptResult, prepareStockReceipt, stockReceiptInput, stockReceiptKeyOwner, stockReceiptOperation } from '../src/modules/procurement/procurement-stock-receipt-client';
import { SourceMemoryStorage, sourceSession } from './stock-source-fixtures';
import { actor, location, target, order, observedLine, noticeId, intakeId } from './supplier-document-fixtures';

const inspectionId = '9007199254741001', receiptId = '9007199254741002';
const input = { location, orderId: order.id, orderVersion: '1' as const, orderApprovalId: order.approvalId, inspectionId, inspectionLineNumber: 1, packages: 2, poolId: '800', poolRevision: '1', receiptReference: '000RECEIPT-a', lotReference: observedLine.lotCode, expiresOn: observedLine.expiryDate, reason: 'Penerimaan fisik tetap ditahan dan belum dinilai' };
const capacity = { basis: 'physical_received' as const, inspectionId, inspectionLineNumber: 1, inspectionHash: 'a'.repeat(64), headId: '810', version: '1', totalPackages: '3', occupiedPackages: '1', remainingPackages: '2' };
const inspection = { id: inspectionId, version: '1' as const, hash: capacity.inspectionHash, inspectionDate: '2026-10-03', inspectionReference: '000INSPECTION-a', noticeId, intakeId, lineNumber: 1, line: observedLine };
const choice = { inspectionId, version: '1' as const, inspectionDate: inspection.inspectionDate, inspectionReference: inspection.inspectionReference, noticeId, intakeId, lines: [{ lineNumber: 1, input: observedLine, capacity }] };
const pool = { id: '800', revision: '1', active: false, location, requestedLocation: location, physicalKey: 'held-source', label: 'Alokasi ditahan', mode: 'WebLedger' as const, control: 'ExclusiveWebAllocation' as const, configurationHash: 'b'.repeat(64), policyId: '802', policyRevision: '1', policyHash: 'c'.repeat(64), writer: 'unit_toko' as const, reconciliationProcedureReference: 'RECON-001', outageProcedureReference: 'OUTAGE-001' };
const source = { order, inspection }, preview = { input, source, pool, capacity, binding: 'd'.repeat(64), observedAt: '2026-10-03T01:00:00.000Z' };
const after = { ...capacity, version: '2', occupiedPackages: '3', remainingPackages: '0' };
const receipt = { id: receiptId, revision: '1' as const, input, source, pool, before: capacity, after, lotId: '820', balanceId: '821', movementId: '822', onHand: '2', held: '2', reserved: '0' as const, expired: '0' as const, available: '0' as const, valuationState: 'pending' as const, valuation: null, state: 'held_pending_valuation_and_release' as const, recordedBy: actor, recordedAt: '2026-10-03T01:01:00.000Z' };
const saved = { receiptId, revision: '1' as const, lotId: '820', balanceId: '821', movementId: '822', state: receipt.state }, key = `sr.receive.${inspectionId}.aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa`;
const command = () => prepareStockReceipt(actor, target, order, choice, 1, pool, input, preview);

test('held receipt input preserves explicit source, lot and expiry without numeric IDs, guessed quantity or valuation', () => {
  assert.deepEqual(stockReceiptInput(input), input); assert.equal(stockReceiptInput({ ...input, expiresOn: null }).expiresOn, null);
  for (const value of [{ ...input, packages: '2' }, { ...input, inspectionId: 9007199254741001 }, { ...input, packages: 0 }, { ...input, poolRevision: 1 }, { ...input, expiresOn: '2026-02-30' }, { ...input, reason: 'two\nlines' }, { ...input, unitCost: '0.00' }]) assert.throws(() => stockReceiptInput(value), RequestNotSentError);
  assert.equal(stockReceiptInput({ ...input, lotReference: '000A\u200dB' }).lotReference, '000A\u200dB');
});
test('frozen source and current pool policy bind preview, including inactive local pools while forbidding external writers', () => {
  assert.ok(Object.isFrozen(command().body.input)); assert.equal(command().body.input.packages, 2);
  assert.deepEqual(parseStockReceiptPools({ location, items: [pool], nextPoolId: null }).items, [pool]);
  for (const invalid of [{ ...pool, writer: 'external_system' }, { ...pool, mode: 'ExternalLedger' }, { ...pool, requestedLocation: { ...location, warehouseId: '999' } }, { ...pool, policyHash: 'bad' }]) assert.throws(() => parseStockReceiptPools({ location, items: [invalid], nextPoolId: null }));
  for (const changed of [{ ...preview, input: { ...input, lotReference: 'another-lot' } }, { ...preview, input: { ...input, expiresOn: null } }, { ...preview, input: { ...input, packages: 3 } }, { ...preview, pool: { ...pool, policyRevision: '2' } }, { ...preview, capacity: { ...capacity, inspectionHash: 'f'.repeat(64) } }]) assert.throws(() => prepareStockReceipt(actor, target, order, choice, 1, pool, input, changed));
  const unknownLine = { ...observedLine, expiryDate: null }, unknownInspection = { ...inspection, line: unknownLine }, unknownChoice = { ...choice, lines: [{ ...choice.lines[0], input: unknownLine }] }, unknownInput = { ...input, expiresOn: null };
  assert.equal(prepareStockReceipt(actor, target, order, unknownChoice, 1, pool, unknownInput, { ...preview, input: unknownInput, source: { order, inspection: unknownInspection } }).body.input.expiresOn, null);
});
test('immutable receipt records package counts fully held with no fabricated cost, release or second capacity claim', () => {
  assert.deepEqual(parseStockReceipt(receipt), receipt); assert.equal(parseStockReceipt(receipt).onHand, '2');
  for (const changed of [{ ...receipt, held: '1' }, { ...receipt, onHand: '24', held: '24' }, { ...receipt, available: '2' }, { ...receipt, reserved: '1' }, { ...receipt, valuation: '0.00' }, { ...receipt, after: { ...after, version: '3' } }, { ...receipt, after: { ...after, remainingPackages: '1' } }, { ...receipt, source: { ...source, privateVideo: 'forbidden' } }]) assert.throws(() => parseStockReceipt(changed), AccessFailure);
});
test('receipt transport keeps actor/CSRF and original key; stale or denied commands are never retried automatically', async () => {
  let calls = 0; const gateway = createProcurementStockReceiptGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++;
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(request.headers.get('Idempotency-Key'), key); assert.equal(request.redirect, 'error'); assert.equal(request.cache, 'no-store'); assert.deepEqual(await request.json(), command().body);
    return calls === 2 ? Response.json({ code: 'step_up_required' }, { status: 403 }) : Response.json(saved);
  } }));
  await assert.rejects(gateway.receive(command(), key, () => false), RequestNotSentError); assert.equal(calls, 0); assert.deepEqual(await gateway.receive(command(), key, () => true), saved);
  await assert.rejects(gateway.receive(command(), key, () => true), { code: 'step_up_required' }); let checks = 0; await assert.rejects(gateway.receive(command(), key, () => ++checks === 1), { code: 'commit_outcome_unknown' }); assert.equal(calls, 3);
});
test('unknown receipt recovers only by original actor/key and validates its actual inspection, lot, balance and movement', async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => key); let writes = 0;
  await assert.rejects(submitTrackedRequest(ledger, journal, actor, 'stock-receipt', stockReceiptOperation, command().body, async () => { writes++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
  const entry = journal.list(actor)[0]; assert.ok(entry); assert.equal(journal.list('99').length, 0); assert.deepEqual(Object.keys(JSON.parse([...storage.values.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  let wrong = true; const gateway = createProcurementStockReceiptGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); assert.equal(request.headers.get('Idempotency-Key'), null); assert.equal(request.headers.get('X-Expected-Actor-Id'), actor);
    if (request.url.endsWith('/operations/query')) { assert.deepEqual(await request.json(), { purpose: 'operation-recovery', operation: stockReceiptOperation, key }); return Response.json({ status: 'committed', result: saved }); }
    return Response.json(wrong ? { ...receipt, movementId: '999' } : receipt);
  } }));
  await assert.rejects(gateway.recover(actor, stockReceiptOperation, key)); wrong = false;
  const legacy = { lookup: async () => { throw new Error('No generic lookup'); }, close: async () => { throw new Error('No generic close'); } };
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'procurement.stockreceipt': { lookup: item => gateway.recover(item.actorId, item.operation, item.key) } }), { state: 'committed', result: { id: receiptId, version: '1' } }); assert.equal(journal.list(actor).length, 0); assert.equal(writes, 1);
  assert.throws(() => parseStockReceiptResult({ ...saved, state: 'available' }, key, stockReceiptOperation)); assert.throws(() => stockReceiptKeyOwner(key + '\n'));
});
test('receipt discovery requires both reading grants and exact fresh write return path', () => {
  const path = '/procurement/stock-receipts'; assert.equal(identityReturnPath(path), path);
  for (const actions of [[], ['stock.read'], ['procurement.read'], ['stock.receive']]) assert.equal(identityDestinations(actions).some(item => item.href === path), false);
  assert.equal(identityDestinations(['procurement.read', 'stock.read']).some(item => item.href === path), true);
  const session = parseIdentitySession({ ...sourceSession, stepUpOperations: ['stock.receive'] }); assert.deepEqual(stepUpFields(session, 'stock.receive', path), { expectedActorId: session.account!.userId, operation: 'stock.receive', returnPath: path });
  assert.equal(recoveryPage(stockReceiptOperation).href, path); assert.equal(operationLabel(stockReceiptOperation), 'Penerimaan stok ditahan'); assert.throws(() => loginFields(path + '?other=https://outside.test', '', '')); assert.throws(() => loginFields(path + '/other', '', ''));
});
