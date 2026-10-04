import assert from 'node:assert/strict';
import test from 'node:test';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
import { identityReturnPath } from '../src/modules/identity/identity-workflow';
import { SourceMemoryStorage } from './stock-source-fixtures';
import { advanceReturnInput, createAdvanceReturnGateway, parseAdvanceReturnPreview, parseAdvanceReturnRead, parseAdvanceReturnResult, prepareAdvanceReturn, prepareAdvanceReturnDecision } from '../src/modules/procurement/procurement-advance-return-client';
import { parseSupplierPayableAdvance } from '../src/modules/procurement/procurement-supplier-payable-client';

const location = { organizationId: '100', shopId: '101', warehouseId: '102', channelId: '103' };
const label = (id: string, name: string) => ({ id, code: name, name, scopeVersion: '1', profileVersion: '1' });
const target = { location, organization: label('100', 'Koperasi'), shop: label('101', 'Toko'), warehouse: label('102', 'Gudang'), channel: label('103', 'Ritel') };
const receipt = { receiptId: '810', receiptAmount: '3000.00', bookedDate: '2026-10-04', bankReference: null, bankAccountId: '811', bankAccountVersionId: '812', receiptHash: 'a'.repeat(64) };
const suspense = { mappingKey: 'suspense', accountId: '901', accountCode: 'LSUSPENSE' }, advanceAccount = { mappingKey: 'advance', accountId: '902', accountCode: 'AADVANCE' };
const advances = ['830', '840'].map((settlementId, at) => ({ advance: { settlementId, disbursementId: String(850 + at), orderId: '860', commercialReference: 'PO-RETURN', supplierId: '861', supplierName: 'Pemasok', location, amount: '6000.00', journalId: String(870 + at), recordedAt: '2026-10-03T00:00:00Z' }, capacity: { settlementId, version: '0', totalAmount: '6000.00', appliedAmount: '0.00', remainingAmount: '6000.00', lastApplicationId: null, lastHash: null } }));
const input = { location, receiptId: '810', policyId: '880', policyRevision: '1', postingDate: '2026-10-04', reason: 'Uang muka pesanan dikembalikan', lines: [{ settlementId: '830', amount: '1000.00' }, { settlementId: '840', amount: '2000.00' }] };
const source = { ...receipt, location, supplierId: '861', supplierName: 'Pemasok', orderId: '860', commercialReference: 'PO-RETURN', bookId: '904', suspenseAccount: suspense, lines: advances.map((item, at) => ({ settlementId: item.advance.settlementId, settlementHash: 'b'.repeat(64), disbursementId: item.advance.disbursementId, settlementAmount: item.advance.amount, returnAmount: input.lines[at].amount, sourceAdvanceAccount: advanceAccount })) };
const capacity = { lines: advances.map(item => ({ settlementId: item.advance.settlementId, advance: item.capacity })) };
const policy = { id: '880', revision: '1', active: true, location, currency: 'IDR' as const, approvedAt: '2026-10-01T00:00:00Z', expiresAt: '2026-11-01T00:00:00Z', treatment: 'whole_verified_supplier_advance_return' as const, bookId: '904', chartVersionId: '905', suspense, supplierAdvance: advanceAccount, operatingOwnerReference: 'FINANCE-001', approvalReference: 'APPROVAL-001' };
const accounting = { bookId: '904', chartVersionId: '905', periodId: '906', periodMonth: '2026-10', suspense, supplierAdvance: advanceAccount };
const preview = { input, source, capacity, policy, accounting, amount: '3000.00', binding: 'c'.repeat(64), observedAt: '2026-10-04T00:00:00Z' };
const proposalId = '9007199254741001', actor = '760', checker = '761';
const proposal = { id: proposalId, version: '1' as const, input, source, capacity, policy, accounting, amount: '3000.00', recordedAt: '2026-10-04T00:00:01Z', reason: input.reason };
const pending = { proposal, decision: null, decisionReason: null };
const shown = { proposal, decision: 'approve' as const, source, capacity, policy, accounting, binding: 'd'.repeat(64), observedAt: '2026-10-04T00:00:02Z' };
const returned = { proposalId, decisionId: '910', version: '1' as const, status: 'returned' as const, receiptId: '810', returnLineIds: ['911', '912'], journalId: '913', recordedAt: '2026-10-04T00:00:03Z' };
const proposeOp = 'procurement.advancereturn.propose', decideOp = 'procurement.advancereturn.decide';
const proposeKey = 'advance-return.propose.810.aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', decideKey = `advance-return.approve.${proposalId}.bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb`;
const command = () => prepareAdvanceReturn(actor, target, advanceReturnInput(input), parseAdvanceReturnPreview(preview), policy, receipt, advances);
const decisionCommand = () => prepareAdvanceReturnDecision(checker, parseAdvanceReturnRead(pending), shown, 'Kredit dan uang muka ditinjau secara independen');

test('whole bank credit accepts distinct payouts on one order and rejects duplicated or foreign source selections', () => {
  assert.deepEqual(advanceReturnInput({ ...input, lines: [...input.lines].reverse() }), input);
  assert.equal(command().body.input.lines.length, 2); assert.ok(Object.isFrozen(command().body.input.lines));
  for (const value of [{ ...input, lines: [input.lines[0], input.lines[0]] }, { ...input, lines: [] }, { ...input, lines: [{ settlementId: 830, amount: '1000.00' }] }, { ...input, lines: [{ settlementId: '830', amount: '0.00' }] }, { ...input, supplierId: '861' }, { ...input, postingDate: '2026-02-30' }]) assert.throws(() => advanceReturnInput(value), RequestNotSentError);
  for (const changes of [{ orderId: '999' }, { supplierId: '999' }, { disbursementId: '999' }, { commercialReference: 'OTHER' }, { amount: '5999.00' }]) assert.throws(() => prepareAdvanceReturn(actor, target, input, parseAdvanceReturnPreview(preview), policy, receipt, [{ ...advances[0], advance: { ...advances[0].advance, ...changes } }, advances[1]]));
});

test('preview binds exact credit, allocations, capacity and approved accounts', () => {
  for (const value of [{ ...preview, amount: '2999.99' }, { ...preview, source: { ...source, receiptAmount: '3000.01' } }, { ...preview, input: { ...input, receiptId: '999' } }, { ...preview, accounting: { ...accounting, bookId: '999' } }, { ...preview, policy: { ...policy, expiresAt: preview.observedAt } }, { ...preview, source: { ...source, lines: [...source.lines].reverse() } }, { ...preview, source: { ...source, lines: [{ ...source.lines[0], sourceAdvanceAccount: suspense }, source.lines[1]] } }, { ...preview, capacity: { lines: capacity.lines.map(item => ({ ...item, advance: { ...item.advance, version: '1', appliedAmount: '5500.00', remainingAmount: '500.00', lastApplicationId: '999', lastHash: 'e'.repeat(64) } })) } }]) assert.throws(() => parseAdvanceReturnPreview(value), AccessFailure);
  assert.throws(() => prepareAdvanceReturn(actor, target, input, parseAdvanceReturnPreview(preview), policy, { ...receipt, receiptHash: 'f'.repeat(64) }, advances), RequestNotSentError);
});

test('legacy and returned advance states preserve exact balances and cannot erase a return', () => {
  assert.deepEqual(parseSupplierPayableAdvance(advances[0]), advances[0]);
  const state = { stateVersion: 2 as const, settlementId: '830', version: '2', totalAmount: '6000.00', appliedAmount: '2000.00', returnedAmount: '1000.00', remainingAmount: '3000.00', lastEffectKind: 'return' as const, lastEffectId: '911', lastHash: 'a'.repeat(64) };
  assert.equal(parseSupplierPayableAdvance({ ...advances[0], capacity: state }).capacity.remainingAmount, '3000.00');
  for (const changed of [{ returnedAmount: '0.00' }, { remainingAmount: '4000.00' }, { lastApplicationId: '911' }, { stateVersion: '2' }, { lastEffectId: null }]) assert.throws(() => parseSupplierPayableAdvance({ ...advances[0], capacity: { ...state, ...changed } }));
});

test('decision binds exact owner, complete effects and safe transport', async () => {
  assert.equal(decisionCommand().body.decision, 'approve');
  for (const value of [{ ...returned, receiptId: null }, { ...returned, journalId: null }, { ...returned, returnLineIds: ['911', '911'] }, { ...returned, status: 'rejected' }, { ...returned, proposalId: '999' }]) assert.throws(() => parseAdvanceReturnResult(value, decideKey, decideOp));
  assert.throws(() => parseAdvanceReturnRead({ proposal, decision: { ...returned, receiptId: '999' }, decisionReason: 'Reviewed' }));
  assert.throws(() => parseAdvanceReturnRead({ proposal, decision: { ...returned, returnLineIds: ['911'] }, decisionReason: 'Reviewed' }));
  let calls = 0;
  const gateway = createAdvanceReturnGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++;
    assert.equal(request.headers.get('X-Expected-Actor-Id'), checker); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(request.headers.get('If-Match'), '"1"'); assert.equal(request.headers.get('Idempotency-Key'), decideKey); assert.equal(request.redirect, 'error'); assert.equal(request.cache, 'no-store'); assert.deepEqual(await request.json(), decisionCommand().body); return Response.json(returned);
  } }));
  await assert.rejects(gateway.decide(decisionCommand(), decideKey, () => false), RequestNotSentError); assert.equal(calls, 0);
  assert.equal((await gateway.decide(decisionCommand(), decideKey, () => true)).status, 'returned');
});

test('lost reply recovery retains pending reference until exact receipt ownership is confirmed', async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => decideKey); let attempts = 0, wrong = true;
  await assert.rejects(submitTrackedRequest(ledger, journal, checker, 'return', decideOp, decisionCommand().body, async () => { attempts++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
  const entry = journal.list(checker)[0]; assert.ok(entry); assert.equal(journal.list(actor).length, 0);
  assert.deepEqual(Object.keys(JSON.parse([...storage.values.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  const gateway = createAdvanceReturnGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); assert.equal(request.headers.get('Idempotency-Key'), null);
    if (request.url.endsWith('/operations/query')) { assert.deepEqual(await request.json(), { purpose: 'operation-recovery', operation: decideOp, key: decideKey }); return Response.json({ state: 'confirmed', result: returned }); }
    return Response.json({ proposal, decision: wrong ? { ...returned, receiptId: '999' } : returned, decisionReason: 'Reviewed' });
  } }));
  const legacy = { lookup: async () => { throw Error('No generic lookup'); }, close: async () => { throw Error('No generic close'); } };
  const recover = () => reconcileOperationRecovery(entry, journal, legacy, { 'procurement.advancereturn': { lookup: item => gateway.recover(item.actorId, item.operation, item.key) } });
  await assert.rejects(recover()); assert.equal(journal.list(checker).length, 1); wrong = false;
  assert.deepEqual(await recover(), { state: 'committed', result: { id: proposalId, version: '1' } }); assert.equal(journal.list(checker).length, 0); assert.equal(attempts, 1);
  assert.equal(identityReturnPath('/procurement/advance-returns'), '/procurement/advance-returns'); assert.equal(recoveryPage(proposeOp).href, '/procurement/advance-returns');
});

test('filtered receipt pages may be empty but must move forward inside the requested location', async () => {
  let next: unknown = { location, items: [], nextReceiptId: '800' };
  const gateway = createAdvanceReturnGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => { assert.ok(request instanceof Request); return request.url.endsWith('/csrf') ? Response.json({ requestToken: 'csrf' }) : Response.json(next); } }));
  assert.equal((await gateway.receipts(actor, location, '900')).nextReceiptId, '800');
  next = { location, items: [], nextReceiptId: '900' }; await assert.rejects(gateway.receipts(actor, location, '900'));
  next = { location: { ...location, warehouseId: '999' }, items: [], nextReceiptId: null }; await assert.rejects(gateway.receipts(actor, location));
  next = { location, items: [receipt], nextReceiptId: '820' }; await assert.rejects(gateway.receipts(actor, location));
  const proposed = { proposalId, version: '1', status: 'proposed', recordedAt: proposal.recordedAt };
  assert.equal(parseAdvanceReturnResult(proposed, proposeKey, proposeOp).status, 'proposed');
});
