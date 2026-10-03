import assert from 'node:assert/strict';
import test from 'node:test';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { recoveryPage, operationLabel } from '../src/modules/administration/operation-recovery-navigation';
import { identityDestinations, identityReturnPath, parseIdentitySession, stepUpFields } from '../src/modules/identity/identity-workflow';
import { createSupplierPayableGateway, supplierPayableInput, parseSupplierPayableResult, supplierPayableKeyOwner, parseSupplierPayablePreview, parseSupplierPayableRead, parseSupplierPayableChoice, parseSupplierPayableAdvance, supplierPayableCurrent, prepareSupplierPayable, prepareSupplierPayableDecision } from '../src/modules/procurement/procurement-supplier-payable-client';
import { SourceMemoryStorage, sourceSession } from './stock-source-fixtures';

const actor = '750', checker = '751', allocationId = '9007199254741001', proposalId = '9007199254741002';
const location = { organizationId: '100', shopId: '101', warehouseId: '102', channelId: '103' };
const original = { kind: 'recognize' as const, location, valuationAllocationId: allocationId, payableId: null, settlementId: null, amount: null, policyId: '810', policyRevision: '1', postingDate: '2026-10-03', reason: 'Pengakuan invoice yang sudah dinilai' };
const application = { ...original, kind: 'apply_advance' as const, valuationAllocationId: null, payableId: '820', settlementId: '830', amount: '4000.00' };
const proposed = { proposalId, version: '1' as const, status: 'proposed' as const, recordedAt: '2026-10-03T03:00:00Z' };
const decided = { proposalId, decisionId: '840', version: '1' as const, status: 'approved' as const, payableId: '820', applicationId: null, journalId: '850', recordedAt: '2026-10-03T03:00:01Z' };
const proposeOp = 'procurement.payable.propose', decideOp = 'procurement.payable.decide';
const proposeKey = `payable.recognize.${allocationId}.aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa`, decideKey = `payable.approve.${proposalId}.bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb`;
const label = (id: string, name: string) => ({ id, code: name, name, scopeVersion: '1', profileVersion: '1' });
const target = { location, organization: label('100', 'Koperasi'), shop: label('101', 'Toko'), warehouse: label('102', 'Gudang'), channel: label('103', 'Ritel') };
const accrual = { mappingKey: 'receipt', accountId: '901', accountCode: 'LACCRUAL' }, payableAccount = { mappingKey: 'payable', accountId: '902', accountCode: 'LPAYABLE' }, advanceAccount = { mappingKey: 'advance', accountId: '903', accountCode: 'AADVANCE' };
const policy = { id: '810', revision: '1', active: true, location, currency: 'IDR' as const, approvedAt: '2026-10-01T00:00:00Z', expiresAt: '2026-11-01T00:00:00Z', sourceTreatment: 'receipt_accrual_to_payable' as const, applicationTreatment: 'verified_advance_to_payable' as const, bookId: '904', chartVersionId: '905', receiptAccrual: accrual, supplierPayable: payableAccount, supplierAdvance: advanceAccount, operatingOwnerReference: 'FINANCE-001', approvalReference: 'APPROVED-001' };
const accounting = { bookId: '904', chartVersionId: '905', periodId: '906', periodMonth: '2026-10', receiptAccrual: accrual, supplierPayable: payableAccount, supplierAdvance: advanceAccount };
const source = { kind: 'recognize' as const, valuationAllocationId: allocationId, valuationAllocationHash: 'a'.repeat(64), valuationDecisionId: '910', valuationJournalId: '911', receiptId: '912', invoiceDocumentId: '913', invoiceMatchId: '914', matchLineNumber: 1, packages: 1, orderId: '915', commercialReference: '000PO', invoiceDocumentReference: '000INVOICE', supplierId: '916', supplierName: 'Pemasok', location, amount: '11250.00', netAmount: '10000.00', taxAmount: '1000.00', chargeAmount: '250.00', sourceAccount: accrual, sourceBookId: '904' };
const capacity = { valuationAllocationId: allocationId, amount: '11250.00', recognized: false, payableId: null };
const preview = { input: original, source, capacity, policy, accounting, amount: '11250.00', binding: 'b'.repeat(64), observedAt: '2026-10-03T02:59:59Z' };
const proposal = { id: proposalId, version: '1' as const, input: original, source, capacity, policy, accounting, amount: '11250.00', recordedAt: proposed.recordedAt, reason: original.reason };
const pending = { proposal, decision: null, decisionReason: null };
const decisionPreview = { proposal, decision: 'approve' as const, source, capacity, policy, accounting, binding: 'c'.repeat(64), observedAt: '2026-10-03T03:00:01Z' };
const currentPayable = { payable: { id: '820', valuationAllocationId: allocationId, orderId: '915', commercialReference: '000PO', invoiceDocumentReference: '000INVOICE', supplierId: '916', supplierName: 'Pemasok', location, amount: '11250.00', journalId: '850', recordedAt: decided.recordedAt }, capacity: { payableId: '820', version: '0', totalAmount: '11250.00', appliedAmount: '0.00', remainingAmount: '11250.00', lastApplicationId: null, lastHash: null } };
const currentAdvance = { advance: { settlementId: '830', disbursementId: '920', orderId: '915', commercialReference: '000PO', supplierId: '916', supplierName: 'Pemasok', location, amount: '6000.00', journalId: '921', recordedAt: '2026-10-03T02:00:00Z' }, capacity: { settlementId: '830', version: '0', totalAmount: '6000.00', appliedAmount: '0.00', remainingAmount: '6000.00', lastApplicationId: null, lastHash: null } };
const applySource = { kind: 'apply_advance' as const, payableId: '820', payableHash: 'd'.repeat(64), settlementId: '830', settlementHash: 'e'.repeat(64), disbursementId: '920', settlementJournalId: '921', valuationAllocationId: allocationId, orderId: '915', commercialReference: '000PO', invoiceDocumentReference: '000INVOICE', supplierId: '916', supplierName: 'Pemasok', location, amount: '4000.00', sourcePayableAccount: payableAccount, sourceAdvanceAccount: advanceAccount, sourceBookId: '904', advanceBookId: '904' };
const applyPreview = { ...preview, input: application, source: applySource, capacity: { payable: currentPayable.capacity, advance: currentAdvance.capacity }, amount: '4000.00' };
const command = () => prepareSupplierPayable(actor, target, supplierPayableInput(original), parseSupplierPayablePreview(preview), policy, { source, capacity }, null, null);
const decisionCommand = () => prepareSupplierPayableDecision(checker, parseSupplierPayableRead(pending), decisionPreview, 'Sumber dan jurnal ditinjau sendiri');

test('recognition derives its amount while advance application needs an explicit exact positive value', () => {
  assert.equal(supplierPayableInput(original).valuationAllocationId, allocationId);
  assert.equal(supplierPayableInput(application).amount, '4000.00');
  for (const value of [{ ...original, amount: '0.00' }, { ...original, payableId: '820' }, { ...original, valuationAllocationId: Number(allocationId) }, { ...application, amount: null }, { ...application, amount: '0.00' }, { ...application, amount: 4000 }, { ...application, amount: '4000.000' }, { ...application, settlementId: null }, { ...original, postingDate: '2026-02-30' }, { ...original, reason: ' incomplete ' }, { ...original, reason: 'bad\ntext' }]) assert.throws(() => supplierPayableInput(value), RequestNotSentError);
  assert.equal(supplierPayableInput({ ...original, reason: 'Exact\u200dtext' }).reason, 'Exact\u200dtext');
});

test('operation results preserve exact proposal identity and cannot claim rejection created financial effects', () => {
  assert.equal(parseSupplierPayableResult(proposed, proposeKey, proposeOp).proposalId, proposalId);
  assert.equal(parseSupplierPayableResult(decided, decideKey, decideOp).status, 'approved');
  for (const value of [{ ...decided, proposalId: '999' }, { ...decided, journalId: null }, { ...decided, status: 'rejected' }, { ...decided, fullAccountNumber: '0012345678' }, { ...decided, version: 1 }]) assert.throws(() => parseSupplierPayableResult(value, decideKey, decideOp), AccessFailure);
  assert.throws(() => supplierPayableKeyOwner(decideKey, proposeOp));
  assert.throws(() => supplierPayableKeyOwner(decideKey + '\n', decideOp));
});

test('review binds genuine valuation amount and exact accounting mappings without editable derived amounts', () => {
  assert.equal(command().body.input.amount, null); assert.ok(Object.isFrozen(command().body.input.location));
  assert.equal(parseSupplierPayablePreview(preview).amount, '11250.00');
  for (const value of [{ ...preview, amount: '11250.01' }, { ...preview, source: { ...source, netAmount: '10000.01' } }, { ...preview, source: { ...source, fullBankNumber: '0001234567' } }, { ...preview, capacity: { ...capacity, recognized: true, payableId: '820' } }, { ...preview, source: { ...source, location: { ...location, warehouseId: '999' } } }, { ...preview, accounting: { ...accounting, chartVersionId: '999' } }, { ...preview, policy: { ...policy, expiresAt: preview.observedAt } }, { ...preview, policy: { ...policy, supplierAdvance: payableAccount } }]) assert.throws(() => parseSupplierPayablePreview(value), AccessFailure);
  assert.throws(() => prepareSupplierPayable(actor, target, supplierPayableInput(original), parseSupplierPayablePreview(preview), { ...policy, revision: '2' }, { source, capacity }, null, null), RequestNotSentError);
});

test('partial application respects both exact remaining heads and current read cannot change source identity', () => {
  const checked = parseSupplierPayablePreview(applyPreview), proposedApplication = { ...proposal, ...checked, id: proposalId, version: '1' as const, recordedAt: proposed.recordedAt, reason: application.reason };
  assert.equal(prepareSupplierPayable(actor, target, supplierPayableInput(application), checked, policy, null, parseSupplierPayableChoice(currentPayable), parseSupplierPayableAdvance(currentAdvance)).body.input.amount, '4000.00');
  for (const value of [{ ...applyPreview, capacity: { ...applyPreview.capacity, advance: { ...currentAdvance.capacity, totalAmount: '3000.00', remainingAmount: '3000.00' } } }, { ...applyPreview, source: { ...applySource, settlementId: '999' } }, { ...applyPreview, capacity: { ...applyPreview.capacity, payable: { ...currentPayable.capacity, remainingAmount: '11000.00' } } }, { ...applyPreview, source: { ...applySource, advanceBookId: '999' } }]) assert.throws(() => parseSupplierPayablePreview(value));
  const { binding: _binding, observedAt: _observedAt, ...withoutPreview } = proposedApplication;
  assert.ok(_binding); assert.ok(_observedAt);
  const actual = parseSupplierPayableRead({ proposal: withoutPreview, decision: { ...decided, applicationId: '922' }, decisionReason: 'Diterapkan sebagian' });
  const afterPayable = { ...currentPayable, capacity: { ...currentPayable.capacity, version: '1', appliedAmount: '4000.00', remainingAmount: '7250.00', lastApplicationId: '922', lastHash: 'f'.repeat(64) } }, afterAdvance = { ...currentAdvance, capacity: { ...currentAdvance.capacity, version: '1', appliedAmount: '4000.00', remainingAmount: '2000.00', lastApplicationId: '922', lastHash: 'f'.repeat(64) } };
  assert.equal(supplierPayableCurrent(actual, afterPayable, afterAdvance).payable.capacity.remainingAmount, '7250.00');
  assert.equal(supplierPayableCurrent(actual, afterPayable, afterAdvance).advance?.capacity.remainingAmount, '2000.00');
  assert.throws(() => supplierPayableCurrent(actual, { ...afterPayable, payable: { ...afterPayable.payable, orderId: '999' } }, afterAdvance));
  assert.throws(() => supplierPayableCurrent(actual, afterPayable, { ...afterAdvance, advance: { ...afterAdvance.advance, supplierId: '999' } }));
});

test('independent decision freezes reviewed source and rejects stale, altered or already decided proposals', () => {
  assert.ok(Object.isFrozen(decisionCommand().body));
  assert.throws(() => prepareSupplierPayableDecision(checker, parseSupplierPayableRead({ ...pending, decision: decided, decisionReason: 'Disetujui' }), decisionPreview, 'again'));
  assert.throws(() => prepareSupplierPayableDecision(checker, parseSupplierPayableRead(pending), { ...decisionPreview, proposal: { ...proposal, reason: 'other' } }, 'reason'));
  assert.throws(() => prepareSupplierPayableDecision(checker, parseSupplierPayableRead(pending), { ...decisionPreview, capacity: { ...capacity, recognized: true, payableId: '820' } }, 'reason'));
  assert.equal(prepareSupplierPayableDecision(checker, parseSupplierPayableRead(pending), { ...decisionPreview, decision: 'reject', policy: { ...policy, active: false } }, 'Tidak dapat dibukukan').body.decision, 'reject');
});

test('decision transport binds actor, CSRF, If-Match and original key; stale views never dispatch', async () => {
  let calls = 0;
  const gateway = createSupplierPayableGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++;
    assert.equal(new URL(request.url).pathname, `/api/v1/procurement/supplier-payables/proposals/${proposalId}/decisions`); assert.equal(request.headers.get('X-Expected-Actor-Id'), checker); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(request.headers.get('If-Match'), '"1"'); assert.equal(request.headers.get('Idempotency-Key'), decideKey); assert.equal(request.cache, 'no-store'); assert.equal(request.redirect, 'error'); assert.deepEqual(await request.json(), decisionCommand().body); return Response.json(decided);
  } }));
  await assert.rejects(gateway.decide(decisionCommand(), decideKey, () => false), RequestNotSentError); assert.equal(calls, 0);
  assert.equal((await gateway.decide(decisionCommand(), decideKey, () => true)).status, 'approved');
  let active = 0; await assert.rejects(gateway.decide(decisionCommand(), decideKey, () => ++active === 1), { code: 'commit_outcome_unknown' }); assert.equal(calls, 2);
});

test('lost results recover once by original actor and key, never by resending private business input', async () => {
  for (const [who, key, operation, body, outcome, owner] of [[actor, proposeKey, proposeOp, command().body, proposed, pending], [checker, decideKey, decideOp, decisionCommand().body, decided, { ...pending, decision: decided, decisionReason: 'Disetujui' }]] as const) {
    const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => key); let attempts = 0, wrong = true, confirmed = true;
    await assert.rejects(submitTrackedRequest(ledger, journal, who, 'payable', operation, body, async () => { attempts++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
    const entry = journal.list(who)[0]; assert.ok(entry); assert.equal(journal.list('999').length, 0); assert.deepEqual(Object.keys(JSON.parse([...storage.values.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    const gateway = createSupplierPayableGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => { assert.ok(request instanceof Request); if (request.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); assert.equal(request.headers.get('Idempotency-Key'), null); assert.equal(request.headers.get('X-Expected-Actor-Id'), who); if (request.url.endsWith('/operations/query')) { assert.deepEqual(await request.json(), { purpose: 'operation-recovery', operation, key }); return Response.json({ state: confirmed ? 'confirmed' : 'not_confirmed', result: confirmed ? outcome : null }); } return Response.json(wrong ? { ...owner, proposal: { ...proposal, id: '999' } } : owner); } }));
    await assert.rejects(gateway.recover(who, operation, key)); confirmed = false; assert.equal(await gateway.recover(who, operation, key), null); assert.equal(journal.list(who).length, 1); confirmed = true; wrong = false;
    const legacy = { lookup: async () => { throw new Error('No generic lookup'); }, close: async () => { throw new Error('No generic close'); } };
    assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'procurement.payable': { lookup: item => gateway.recover(item.actorId, item.operation, item.key) } }), { state: 'committed', result: { id: proposalId, version: '1' } }); assert.equal(journal.list(who).length, 0); assert.equal(attempts, 1);
  }
});

test('command-only recovery does not require location discovery or ordinary read grants', async () => {
  let calls = 0;
  const gateway = createSupplierPayableGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => { assert.ok(request instanceof Request); if (request.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++; assert.ok(request.url.endsWith('/supplier-payables/operations/query')); assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); return Response.json({ state: 'confirmed', result: proposed }); } }));
  assert.deepEqual(await gateway.recover(actor, proposeOp, proposeKey, false), { state: 'committed', result: proposed }); assert.equal(calls, 1);
});

test('approved advance application recovery requires its original payable and verifies detail before clearing the pending key', async () => {
  const applicationProposal = { ...proposal, input: application, source: applySource, capacity: applyPreview.capacity, amount: '4000.00' };
  const applied = { ...decided, applicationId: '922' };
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => decideKey);
  let attempts = 0, detailReads = 0, recovered: unknown = { ...applied, payableId: null };
  await assert.rejects(submitTrackedRequest(ledger, journal, checker, 'apply-decision', decideOp, decisionCommand().body, async () => { attempts++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
  const entry = journal.list(checker)[0]; assert.ok(entry);
  const gateway = createSupplierPayableGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' });
    assert.equal(request.headers.get('Idempotency-Key'), null); assert.equal(request.headers.get('X-Expected-Actor-Id'), checker);
    if (request.url.endsWith('/operations/query')) { assert.deepEqual(await request.json(), { purpose: 'operation-recovery', operation: decideOp, key: decideKey }); return Response.json({ state: 'confirmed', result: recovered }); }
    assert.equal(new URL(request.url).pathname, `/api/v1/procurement/supplier-payables/proposals/${proposalId}/query`);
    detailReads++; return Response.json({ proposal: applicationProposal, decision: recovered, decisionReason: 'Diterapkan sebagian' });
  } }));
  const legacy = { lookup: async () => { throw new Error('No generic lookup'); }, close: async () => { throw new Error('No generic close'); } };
  const recover = () => reconcileOperationRecovery(entry, journal, legacy, { 'procurement.payable': { lookup: item => gateway.recover(item.actorId, item.operation, item.key) } });
  await assert.rejects(recover(), { code: 'supplier_payable_response_invalid' });
  assert.equal(detailReads, 0); assert.equal(journal.list(checker).length, 1);
  recovered = { ...applied, payableId: '999' };
  await assert.rejects(recover(), { code: 'supplier_payable_response_invalid' });
  assert.equal(detailReads, 1); assert.equal(journal.list(checker).length, 1);
  recovered = applied;
  assert.deepEqual(await recover(), { state: 'committed', result: { id: proposalId, version: '1' } });
  assert.equal(detailReads, 2); assert.equal(journal.list(checker).length, 0); assert.equal(attempts, 1);
});

test('scoped choices reject foreign sources and current balance arithmetic without inventing a zero advance', async () => {
  let wrong = false;
  const gateway = createSupplierPayableGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => { assert.ok(request instanceof Request); if (request.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); assert.equal(request.headers.get('Idempotency-Key'), null);
    if (request.url.endsWith('/valuation-allocations/query')) { assert.deepEqual(await request.json(), { purpose: 'supplier-payable-review', location, beforeValuationAllocationId: null }); return Response.json({ items: [{ source: wrong ? { ...source, location: { ...location, warehouseId: '999' } } : source, capacity }], nextValuationAllocationId: null }); }
    if (request.url.endsWith('/verified-advances/query')) { assert.deepEqual(await request.json(), { purpose: 'supplier-payable-review', location, payableId: '820', beforeSettlementId: null }); return Response.json({ items: [], nextSettlementId: null }); }
    return Response.json({ items: [target] });
  } }));
  assert.equal((await gateway.targets(actor)).items[0].warehouse.name, 'Gudang'); assert.equal((await gateway.valuations(actor, location)).items[0].source.amount, '11250.00'); assert.equal((await gateway.advances(actor, parseSupplierPayableChoice(currentPayable))).items.length, 0); wrong = true; await assert.rejects(gateway.valuations(actor, location));
  assert.throws(() => parseSupplierPayableChoice({ ...currentPayable, capacity: { ...currentPayable.capacity, remainingAmount: '11249.00' } }));
});

test('directory and recovery use own grants and preserve the exact MFA return path', () => {
  const path = '/procurement/supplier-payables'; assert.equal(identityReturnPath(path), path);
  for (const grants of [['procurement.payable.read'], [proposeOp], [decideOp]]) assert.ok(identityDestinations(grants).some(item => item.href === path));
  for (const grants of [[], ['procurement.read'], ['journal.post'], ['procurement.stockvaluation.read']]) assert.ok(!identityDestinations(grants).some(item => item.href === path));
  const session = parseIdentitySession({ ...sourceSession, stepUpOperations: [proposeOp, decideOp] }); assert.equal(stepUpFields(session, decideOp, path).returnPath, path);
  assert.equal(recoveryPage(decideOp).href, path); assert.match(operationLabel(proposeOp), /utang pemasok/);
});

test('workspace renewal links use the real account route and retain the payable action and return destination', () => {
  const workspaces: string[] = [];
  function visit(directory: string): void {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const file = join(directory, entry.name);
      if (entry.isDirectory()) visit(file);
      else if (entry.name.endsWith('Workspace.tsx')) workspaces.push(file);
    }
  }
  visit(resolve('src/modules'));
  assert.ok(workspaces.length > 0);
  assert.deepEqual(workspaces.filter(file => readFileSync(file, 'utf8').includes('/identity/step-up')), []);
  const source = readFileSync(resolve('src/modules/procurement/ProcurementSupplierPayableWorkspace.tsx'), 'utf8');
  const href = source.match(/href=\{`([^`]+operation=\$\{permission \+ action\}[^`]+)`\}/)?.[1];
  const returnPath = source.match(/const path = '([^']+)'/)?.[1];
  assert.ok(href); assert.equal(returnPath, '/procurement/supplier-payables');
  const session = parseIdentitySession({ ...sourceSession, stepUpOperations: [proposeOp, decideOp] });
  for (const operation of [proposeOp, decideOp]) {
    const url: URL = new URL(href.replace('${permission + action}', operation).replace('${encodeURIComponent(path)}', encodeURIComponent(returnPath!)), 'https://unit-toko.test');
    assert.equal(url.pathname, '/identity/account');
    assert.ok(existsSync(resolve('src/app/identity/account/page.tsx')));
    assert.deepEqual([...url.searchParams], [['operation', operation], ['returnPath', returnPath]]);
    assert.deepEqual(stepUpFields(session, url.searchParams.get('operation')!, url.searchParams.get('returnPath')!), { expectedActorId: session.account!.userId, operation, returnPath });
  }
});
