import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { operationLabel, recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
import { identityDestinations, identityReturnPath, loginFields, parseIdentitySession, stepUpFields } from '../src/modules/identity/identity-workflow';
import { createSupplierBankGateway, supplierBankInput, supplierBankDetails, supplierBankPolicyCurrent, supplierBankKeyOwner, parseSupplierBankHistory, parseSupplierBankCurrent, parseSupplierBankProposal, parseSupplierBankResult, prepareSupplierBank, prepareSupplierBankObservation, prepareSupplierBankDecision } from '../src/modules/procurement/supplier-bank-client';
import { SourceMemoryStorage, sourceSession } from './stock-source-fixtures';

const actor = '9007199254740993', supplierId = '9007199254740994', proposalId = '9007199254740995', beneficiaryId = '9007199254740996';
const owner = { organizationId: '100', shopId: '101', channelId: '103' };
const organization = { id: '100', code: 'TOKO', name: 'Koperasi', scopeVersion: '1', profileVersion: '2' };
const source = { organization, shop: { ...organization, id: '101', code: 'SHOP', name: 'Toko' }, channel: { ...organization, id: '103', code: 'RETAIL', name: 'Ritel' } }, target = { owner, ...source };
const supplier = { id: supplierId, versionId: '601', version: '2', subjectReference: '000SUP-A', legalName: 'PT Sumber Kertas', displayName: 'Sumber Kertas' }, choice = { supplier, beneficiaryId: null, bankVersion: '0' };
const details = { bankCode: 'BNI', accountNumber: '0012345678901234567890123456789012', holderName: 'PT Sumber Kertas', currency: 'IDR' as const };
const policy = { id: '800', owner, supplierId, supplierVersionId: '601', supplierVersion: '2', subjectReference: '000SUP-A', revision: '1', bankCode: 'BNI', bankName: 'Bank BNI', currency: 'IDR' as const, method: 'bank-contact' as const,
  trustedChannelReference: 'Kanal bank terdaftar 001', authorizationReference: 'Mandat bank 001', operatingOwnerReference: 'Finance koperasi', approvalReference: 'Persetujuan bank 001', approvedAt: '2026-10-01T00:00:00.000Z', expiresAt: '2026-11-01T00:00:00.000Z', maxObservationAgeSeconds: '900', allowReferenceOnly: true, active: true };
const input = { owner, supplierId, supplierVersionId: '601', supplierVersion: '2', baseVersion: '0', policyId: '800', policyRevision: '1', details };
const facts = { input, source, supplier, policy }, preview = { ...facts, binding: 'a'.repeat(64), observedAt: '2026-10-03T01:00:00.000Z' };
const proposal = { ...facts, id: proposalId, beneficiaryId, version: '1' as const, reason: 'Rekening pemasok sesuai mandat', proposedAt: '2026-10-03T01:01:00.000Z', state: 'pending' as const, observation: null, decision: null };
const observation = { id: '801', observed: details, sourceReference: 'Konfirmasi bank 0001', reason: 'Diamati melalui kanal bank', checkedAt: '2026-10-03T01:02:00.000Z' }, observedProposal = { ...proposal, observation };
const observationPreview = { proposal, observed: details, sourceReference: observation.sourceReference, binding: 'b'.repeat(64), observedAt: observation.checkedAt };
const decisionPreview = { proposal: observedProposal, decision: 'approve' as const, observationId: '801', binding: 'c'.repeat(64), observedAt: '2026-10-03T01:03:00.000Z' };
const proposed = { beneficiaryId, proposalId, proposalVersion: '1' as const, recordId: proposalId, operation: 'procurement.supplierbank.propose' as const, versionId: null, version: null }, observedResult = { ...proposed, operation: 'procurement.supplierbank.observe' as const, recordId: '801' }, approved = { ...proposed, operation: 'procurement.supplierbank.approve' as const, recordId: '802', versionId: '803', version: '1' };
const approvedProposal = { ...observedProposal, state: 'approved' as const, decision: { id: '802', decision: 'approve' as const, reason: 'Rincian bank cocok', decidedAt: decisionPreview.observedAt, versionId: '803', version: '1' } };
const masked = { bankCode: 'BNI', maskedAccount: '*'.repeat(30) + '9012', currency: 'IDR' as const }, summary = { id: proposalId, beneficiaryId, supplier, baseVersion: '0', bankVersion: '0', details: masked, state: 'pending' as const, proposedAt: proposal.proposedAt };
const uuid = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', proposeKey = `sb.propose.${supplierId}.${uuid}`, observeKey = `sb.observe.${proposalId}.${uuid}`, approveKey = `sb.approve.${proposalId}.${uuid}`;
const command = () => prepareSupplierBank(actor, target, choice, policy, input, preview, proposal.reason);
const observeCommand = () => prepareSupplierBankObservation(actor, proposal, details, observation.sourceReference, observationPreview, observation.reason, true);
const decisionCommand = () => prepareSupplierBankDecision(actor, observedProposal, decisionPreview, 'Rincian bank cocok');

test('supplier bank account strings and IDs remain exact; malformed details are never normalized or coerced', () => {
  assert.deepEqual(supplierBankInput(input), input); assert.equal(command().body.input.details.accountNumber, details.accountNumber); assert.ok(Object.isFrozen(command().body.input.details));
  for (const changed of [{ ...details, bankCode: 'bni' }, { ...details, accountNumber: '012' }, { ...details, accountNumber: 1234 }, { ...details, accountNumber: details.accountNumber + '0' }, { ...details, accountNumber: '１２３４' }, { ...details, accountNumber: '1e10' }, { ...details, holderName: ' Nama ' }, { ...details, holderName: 'Nama\u200b' }, { ...details, currency: 'USD' }]) assert.throws(() => supplierBankDetails(changed), RequestNotSentError);
  assert.equal(supplierBankDetails({ ...details, accountNumber: '0001' }).accountNumber, '0001');
  assert.throws(() => supplierBankInput({ ...input, supplierVersion: '02' })); assert.throws(() => parseSupplierBankProposal({ ...proposal, input: { ...input, supplierId: Number(supplierId) } }));
});

test('bound proposal and observation use the exact approved supplier and separate bank policy with no inferred trust', () => {
  for (const changed of [{ ...policy, active: false }, { ...policy, allowReferenceOnly: false }, { ...policy, supplierVersion: '1' }, { ...policy, supplierId: '999' }, { ...policy, subjectReference: 'OTHER' }, { ...policy, owner: { ...owner, shopId: '999' } }]) assert.throws(() => prepareSupplierBank(actor, target, choice, changed, input, { ...preview, policy: changed }, proposal.reason));
  assert.throws(() => prepareSupplierBank(actor, target, choice, policy, input, { ...preview, input: { ...input, details: { ...details, accountNumber: '0002' } } }, proposal.reason));
  assert.throws(() => prepareSupplierBank(actor, target, { ...choice, bankVersion: '1', beneficiaryId }, policy, input, preview, proposal.reason));
  assert.equal(supplierBankPolicyCurrent(policy, Date.parse(policy.expiresAt)), false);
  assert.ok(Object.isFrozen(observeCommand().body.observed)); assert.throws(() => prepareSupplierBankObservation(actor, proposal, details, observation.sourceReference, observationPreview, observation.reason, false));
  assert.throws(() => prepareSupplierBankObservation(actor, proposal, { ...details, holderName: 'Different' }, observation.sourceReference, observationPreview, observation.reason, true));
  assert.throws(() => prepareSupplierBankObservation(actor, proposal, details, 'Different reference', observationPreview, observation.reason, true));
  assert.throws(() => prepareSupplierBankObservation(actor, proposal, details, observation.sourceReference, { ...observationPreview, observedAt: policy.expiresAt }, observation.reason, true));
});

test('approval requires exact fresh independent observation; rejection remains possible for mismatched or expired facts', () => {
  assert.deepEqual(parseSupplierBankProposal(approvedProposal, proposalId), approvedProposal); assert.ok(Object.isFrozen(decisionCommand().body));
  const mismatch = { ...observedProposal, observation: { ...observation, observed: { ...details, holderName: 'Different owner' } } };
  assert.throws(() => prepareSupplierBankDecision(actor, mismatch, { ...decisionPreview, proposal: mismatch }, 'Rincian berbeda'));
  assert.throws(() => prepareSupplierBankDecision(actor, observedProposal, { ...decisionPreview, observedAt: '2026-10-03T01:17:00.000Z' }, 'Kedaluwarsa'));
  assert.equal(prepareSupplierBankDecision(actor, mismatch, { ...decisionPreview, proposal: mismatch, decision: 'reject', observationId: null, observedAt: policy.expiresAt }, 'Tolak rincian berbeda').decision, 'reject');
  for (const changed of [{ ...approvedProposal, observation: null }, { ...approvedProposal, decision: { ...approvedProposal.decision, version: '2' } }, { ...proposal, decision: approvedProposal.decision }, { ...proposal, makerPersonId: actor }, { ...proposal, policy: { ...policy, contactValue: 'not a bank authority' } }]) assert.throws(() => parseSupplierBankProposal(changed));
});

test('masked history and current-account reads reject full account numbers, holder names and contradictory versions', () => {
  const history = { owner, items: [summary], nextProposalId: null }; assert.deepEqual(parseSupplierBankHistory(history), history);
  assert.equal(parseSupplierBankHistory({ ...history, items: [{ ...summary, details: { ...masked, maskedAccount: '****' } }] }).items[0].details.maskedAccount, '****');
  for (const item of [{ ...summary, details }, { ...summary, details: { ...masked, holderName: details.holderName } }, { ...summary, details: { ...masked, maskedAccount: details.accountNumber } }, { ...summary, policy }, { ...summary, state: 'approved' }, { ...summary, baseVersion: 'bad' }]) assert.throws(() => parseSupplierBankHistory({ ...history, items: [item] }));
  const head = { supplierId, beneficiaryId, versionId: null, version: '0', proposalId: null, details: null, eligible: false }; assert.deepEqual(parseSupplierBankCurrent(head), head);
  assert.throws(() => parseSupplierBankCurrent({ ...head, eligible: true })); assert.throws(() => parseSupplierBankCurrent({ ...head, details }));
});

test('supplier bank writes use actor-bound CSRF and exact If-Match; stale actors and failed responses never trigger retries', async () => {
  let calls = 0; const gateway = createSupplierBankGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++;
    assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(input.credentials, 'same-origin'); assert.equal(input.cache, 'no-store'); assert.equal(input.redirect, 'error');
    if (input.url.endsWith('/proposals')) { assert.equal(input.headers.get('Idempotency-Key'), proposeKey); assert.deepEqual(await input.json(), command().body); return Response.json(proposed); }
    assert.equal(input.headers.get('If-Match'), '"1"'); if (input.url.endsWith('/observations')) { assert.equal(input.headers.get('Idempotency-Key'), observeKey); assert.deepEqual(await input.json(), observeCommand().body); return Response.json(observedResult); }
    assert.equal(input.headers.get('Idempotency-Key'), approveKey); assert.deepEqual(await input.json(), decisionCommand().body); return calls === 4 ? Response.json({ code: 'step_up_required' }, { status: 403 }) : Response.json(approved);
  } }));
  await assert.rejects(gateway.propose(command(), proposeKey, () => false), RequestNotSentError); assert.equal(calls, 0);
  assert.deepEqual(await gateway.propose(command(), proposeKey, () => true), proposed); assert.deepEqual(await gateway.observe(observeCommand(), observeKey, () => true), observedResult); assert.deepEqual(await gateway.decide(decisionCommand(), approveKey, () => true), approved);
  await assert.rejects(gateway.decide(decisionCommand(), approveKey, () => true), { code: 'step_up_required' }); assert.equal(calls, 4);
  let checks = 0; await assert.rejects(gateway.decide(decisionCommand(), approveKey, () => ++checks === 1), { code: 'commit_outcome_unknown' }); assert.equal(calls, 5);
});

test('lost-response recovery retains only original actor references, checks the exact result, and cannot close or resend a bank observation', async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => observeKey); let attempts = 0;
  await assert.rejects(submitTrackedRequest(ledger, journal, actor, 'supplier-bank-observe', observedResult.operation, observeCommand().body, async () => { attempts++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
  const entry = journal.list(actor)[0], serialized = [...storage.values.values()][0]; assert.equal(journal.list('99').length, 0); assert.deepEqual(Object.keys(JSON.parse(serialized)).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  for (const secret of [details.accountNumber, details.holderName, policy.trustedChannelReference, observation.sourceReference, observation.reason]) assert.equal(serialized.includes(secret), false);
  const legacy = { lookup: async () => { throw new Error('No generic lookup'); }, close: async () => { throw new Error('No generic close'); } };
  assert.deepEqual(operationRecoveryCapabilities(entry.operation), { canCheck: false, canClose: false, unavailable: 'adapter' });
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'procurement.supplierbank': { lookup: async () => null } }), { state: 'unconfirmed' });
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'procurement.supplierbank': { lookup: async () => ({ state: 'committed', result: observedResult }) } }, 'Close'));
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'procurement.supplierbank': { lookup: async () => ({ state: 'committed', result: { ...observedResult, proposalId: '8' } }) } }));
  assert.equal(journal.list(actor).length, 1); assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'procurement.supplierbank': { lookup: async () => ({ state: 'committed', result: observedResult }) } }), { state: 'committed', result: { id: proposalId, version: '1' } }); assert.equal(journal.list(actor).length, 0); assert.equal(attempts, 1);
});

test('gateway recovery verifies the supplier/beneficiary and observation record without writing; bank task discovery never inherits unrelated financial grants', async () => {
  let foreign = false, calls = 0; const gateway = createSupplierBankGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++; assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('Idempotency-Key'), null);
    if (input.url.endsWith('/operations/query')) { assert.deepEqual(await input.json(), { purpose: 'operation-recovery', operation: observedResult.operation, key: observeKey }); return Response.json({ status: 'confirmed', result: observedResult }); }
    assert.deepEqual(await input.json(), { purpose: 'supplier-bank-review' }); return Response.json(foreign ? { ...observedProposal, observation: { ...observation, id: '899' } } : observedProposal);
  } }));
  assert.deepEqual(await gateway.recover(actor, observedResult.operation, observeKey), { state: 'committed', result: observedResult }); foreign = true; await assert.rejects(gateway.recover(actor, observedResult.operation, observeKey)); assert.equal(calls, 4);
  assert.throws(() => parseSupplierBankResult({ ...approved, version: null }, approveKey, approved.operation)); assert.throws(() => supplierBankKeyOwner(observeKey, approved.operation));
  const path = '/procurement/supplier-banks'; assert.equal(identityReturnPath(path), path); assert.deepEqual(loginFields(path, '', ''), { returnPath: path }); assert.equal(identityDestinations(['supplier.bank.read']).some(item => item.href === path), true);
  for (const permission of ['procurement.read', 'supplier.change.approve', 'receiving.account.approve', 'disbursement.record', 'supplier.bank.details.read']) assert.equal(identityDestinations([permission]).some(item => item.href === path), false);
  for (const permission of ['supplier.bank.propose', 'supplier.bank.approve']) { const session = parseIdentitySession({ ...sourceSession, stepUpOperations: [permission] }); assert.deepEqual(stepUpFields(session, permission, path), { expectedActorId: session.account!.userId, operation: permission, returnPath: path }); }
  assert.equal(recoveryPage(observedResult.operation).href, path); assert.equal(operationLabel(observedResult.operation), 'Pemeriksaan bank pemasok'); assert.equal(recoveryPage('procurement.supplier.observe').href, '/procurement/suppliers');
  for (const invalid of [path + '/', path + '?return=https://elsewhere.test', '//procurement/supplier-banks']) assert.throws(() => loginFields(invalid, '', ''));
});
