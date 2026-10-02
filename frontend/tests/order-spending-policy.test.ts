import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { recoveryPage, operationLabel } from '../src/modules/administration/operation-recovery-navigation';
import { identityDestinations, identityReturnPath, loginFields, parseIdentitySession, stepUpFields } from '../src/modules/identity/identity-workflow';
import { createSpendingPolicyGateway, parseSpendingPolicyCurrent, parseSpendingPolicyProposal, parseSpendingPolicyResult, parseSpendingPolicyTargets,
  prepareSpendingPolicy, prepareSpendingPolicyDecision, spendingPolicyInput, spendingPolicyThreshold, spendingPolicyTime, spendingPolicyKeyOwner } from '../src/modules/ordering/order-spending-policy-client';
import { SourceMemoryStorage, sourceSession } from './stock-source-fixtures';

const actor = '9007199254740993', proposalId = '9007199254740994', policyId = '9007199254740995';
const target = { kind: 'branch' as const, id: '11' }, context = { organizationId: '10', branchId: '11', divisionId: null };
const selected = { target, context, code: 'TEBET', name: 'Cabang Tebet', expectedScopeVersion: '2', expectedProfileVersion: '3', policyVersion: '0', policyId: null, versionId: null };
const rule = { mode: 'above_threshold' as const, threshold: '150000.25' }, validFrom = '2026-10-02T00:00:00Z', validUntil = '2026-11-01T00:00:00Z';
const input = { target, expectedScopeVersion: '2', expectedProfileVersion: '3', expectedPolicyVersion: '0', rule, validFrom, validUntil };
const preview = { binding: 'a'.repeat(64), view: { target, context, code: selected.code, name: selected.name, scopeVersion: '2', profileVersion: '3', policyVersion: '0', rule, validFrom, validUntil, sourceHash: 'b'.repeat(64) } };
const proposal = { proposalId, policyId, revision: '1' as const, target, context, code: selected.code, name: selected.name, rule, validFrom, validUntil, baseVersion: '0',
  proposedAt: '2026-10-01T09:00:00Z', state: 'pending' as const, decision: null, policyVersion: null, versionId: null, bindingCurrent: true };
const decisionPreview = { binding: 'c'.repeat(64), view: { ...proposal, decision: 'approve' as const } };
const uuid = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', proposeKey = `osp.branch.11.${uuid}`, approveKey = `ospd.approve.${proposalId}.${uuid}`;
const proposed = { policyId, proposalId, recordId: proposalId, operation: 'ordering.spending.policy.propose' as const, policyVersion: null, versionId: null, recordedAt: proposal.proposedAt };
const approved = { ...proposed, recordId: '9007199254740996', operation: 'ordering.spending.policy.approve' as const, policyVersion: '1', versionId: '9007199254740997', recordedAt: '2026-10-01T09:02:00Z' };
const command = () => prepareSpendingPolicy(actor, selected, input, preview, 'Aturan belanja cabang telah ditinjau');
const decisionCommand = () => prepareSpendingPolicyDecision(actor, proposal, decisionPreview, 'Pemeriksaan independen aturan dan masa berlaku');

test('spending rules retain explicit exact amounts, WIB dates and target versions without defaults or foreign hierarchy', () => {
  assert.deepEqual(parseSpendingPolicyTargets({ items: [selected], hasMore: false, nextTargetId: null }).items, [selected]);
  assert.equal(spendingPolicyThreshold('0'), '0.00'); assert.equal(spendingPolicyThreshold('999999999999999.99'), '999999999999999.99');
  assert.equal(spendingPolicyTime('2026-10-02T07:30'), '2026-10-02T00:30:00.000Z');
  for (const invalid of ['', '2026-02-30T07:30', '2026-10-02T07:30Z', '2026-10-02T25:30']) assert.throws(() => spendingPolicyTime(invalid));
  for (const mode of ['every_order', 'not_required'] as const) assert.equal(spendingPolicyInput({ ...input, rule: { mode, threshold: null } }).rule.mode, mode);
  for (const changed of [{ ...input, rule: { mode: 'every_order', threshold: '0.00' } }, { ...input, rule: { mode: 'above_threshold', threshold: null } },
    { ...input, rule: { mode: 'above_threshold', threshold: 150000 } }, { ...input, rule: { mode: 'above_threshold', threshold: '1e3' } },
    { ...input, validUntil: validFrom }, { ...input, validFrom: '2026-10-02T00:00:00.0001Z' }, { ...input, expectedPolicyVersion: '9223372036854775806' }])
    assert.throws(() => spendingPolicyInput(changed as never), RequestNotSentError);
  for (const changed of [{ ...selected, target: { kind: 'branch', id: '21' } }, { ...selected, context: { ...context, divisionId: '12' } },
    { ...selected, expectedScopeVersion: 2 }, { ...selected, policyVersion: '1' }, { ...selected, versionId: '9' }, { ...selected, privateApproval: 'hidden' }])
    assert.throws(() => parseSpendingPolicyTargets({ items: [changed], hasMore: false, nextTargetId: null }));
  assert.throws(() => parseSpendingPolicyTargets({ items: [selected, selected], hasMore: false, nextTargetId: null }));
  assert.throws(() => parseSpendingPolicyTargets({ items: [selected], hasMore: true, nextTargetId: '12' }));
  assert.throws(() => parseSpendingPolicyTargets({ items: [selected], hasMore: false, nextTargetId: null }, '11'));
  assert.equal(parseSpendingPolicyTargets({ items: [{ ...selected, policyId }], hasMore: false, nextTargetId: null }).items[0].policyVersion, '0');
});
test('reviewed proposal and independent decision bind complete rule, window and immutable source before freezing', () => {
  const frozen = command(); assert.ok(Object.isFrozen(frozen.body.input.rule)); assert.ok(Object.isFrozen(frozen.body.input.target));
  for (const changed of [{ ...preview, view: { ...preview.view, code: 'OTHER' } }, { ...preview, view: { ...preview.view, scopeVersion: '3' } },
    { ...preview, view: { ...preview.view, profileVersion: '4' } }, { ...preview, view: { ...preview.view, policyVersion: '1' } },
    { ...preview, view: { ...preview.view, rule: { ...rule, threshold: '150000.24' } } }, { ...preview, view: { ...preview.view, validUntil: '2026-12-01T00:00:00Z' } }])
    assert.throws(() => prepareSpendingPolicy(actor, selected, input, changed, 'Alasan'));
  assert.ok(Object.isFrozen(decisionCommand().body)); assert.deepEqual(parseSpendingPolicyProposal(proposal, proposalId), proposal);
  assert.throws(() => prepareSpendingPolicyDecision(actor, proposal, { ...decisionPreview, view: { ...decisionPreview.view, proposalId: '1' } }, 'Alasan'));
  assert.throws(() => prepareSpendingPolicyDecision(actor, proposal, { ...decisionPreview, view: { ...decisionPreview.view, bindingCurrent: false } }, 'Alasan'));
  const stale = { ...proposal, bindingCurrent: false };
  assert.equal(prepareSpendingPolicyDecision(actor, stale, { ...decisionPreview, view: { ...stale, decision: 'reject' } }, 'Sumber perlu diperbarui').decision, 'reject');
  assert.throws(() => parseSpendingPolicyResult({ ...approved, proposalId: '1' }, approveKey, approved.operation));
  assert.throws(() => spendingPolicyKeyOwner(approveKey, 'ordering.spending.policy.reject'));
  const missing = { target, context, code: selected.code, name: selected.name, policyId: null, version: '0', versionId: null, rule: null, validFrom: null, validUntil: null, approvedAt: null, current: false, active: false };
  assert.deepEqual(parseSpendingPolicyCurrent(missing, selected), missing);
  assert.deepEqual(parseSpendingPolicyCurrent({ ...missing, policyId }, selected), { ...missing, policyId });
  assert.throws(() => parseSpendingPolicyCurrent({ ...missing, active: true }, selected));
  assert.throws(() => parseSpendingPolicyCurrent({ ...missing, context: { ...context, organizationId: '20' } }, selected));
  const inactive = { ...missing, policyId, version: '1', versionId: '9007199254740997', rule, validFrom, validUntil, approvedAt: approved.recordedAt };
  assert.deepEqual(parseSpendingPolicyCurrent(inactive, selected), inactive);
});
test('spending transport sends exact CSRF, actor, body and immutable revision with no retries after denied or uncertain commands', async () => {
  let calls = 0; const gateway = createSpendingPolicyGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf-memory' }); calls++;
    assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('X-CSRF-Token'), 'csrf-memory');
    assert.equal(input.credentials, 'same-origin'); assert.equal(input.cache, 'no-store'); assert.equal(input.redirect, 'error');
    if (input.url.endsWith('/proposals')) { assert.equal(input.headers.get('Idempotency-Key'), proposeKey); assert.deepEqual(await input.json(), command().body); return Response.json(proposed); }
    assert.equal(input.headers.get('Idempotency-Key'), approveKey); assert.equal(input.headers.get('If-Match'), '"1"'); assert.deepEqual(await input.json(), decisionCommand().body);
    return calls === 2 ? Response.json(approved) : Response.json({ code: 'step_up_required' }, { status: 403 });
  } }));
  await assert.rejects(gateway.propose(command(), proposeKey, () => false), RequestNotSentError); assert.equal(calls, 0);
  assert.deepEqual(await gateway.propose(command(), proposeKey, () => true), proposed); assert.deepEqual(await gateway.decide(decisionCommand(), approveKey, () => true), approved);
  await assert.rejects(gateway.decide(decisionCommand(), approveKey, () => true), { code: 'step_up_required' }); assert.equal(calls, 3);
});
test('lookup-only spending recovery retains original actor metadata and never closes, resends or accepts a mismatched decision', async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => approveKey); let attempts = 0;
  await assert.rejects(submitTrackedRequest(ledger, journal, actor, 'spending-policy-decision', approved.operation, decisionCommand().body, async () => { attempts++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
  const entry = journal.list(actor)[0]; assert.equal(journal.list('99').length, 0);
  const serialized = [...storage.values.values()][0]; assert.deepEqual(Object.keys(JSON.parse(serialized)).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  for (const privateValue of [decisionCommand().body.reason, decisionPreview.binding, rule.threshold]) assert.equal(serialized.includes(privateValue), false);
  const legacy = { lookup: async () => { throw new Error('No generic lookup'); }, close: async () => { throw new Error('No generic close'); } };
  assert.deepEqual(operationRecoveryCapabilities(approved.operation), { canCheck: false, canClose: false, unavailable: 'adapter' });
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'ordering.spending.policy': { lookup: async () => null } }), { state: 'unconfirmed' });
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'ordering.spending.policy': { lookup: async () => ({ state: 'committed', result: approved }) } }, 'Close'));
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'ordering.spending.policy': { lookup: async () => ({ state: 'committed', result: { ...approved, proposalId: '1' } }) } }));
  assert.equal(journal.list(actor).length, 1);
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'ordering.spending.policy': { lookup: async () => ({ state: 'committed', result: approved }) } }), { state: 'committed', result: { id: proposalId, version: '1' } });
  assert.equal(journal.list(actor).length, 0); assert.equal(attempts, 1);
});
test('proposal recovery checks actual scoped owner and explicit policy navigation never grants order approval', async () => {
  let mismatch = false, calls = 0; const gateway = createSpendingPolicyGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++;
    assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('Idempotency-Key'), null);
    if (input.url.endsWith('/operations/query')) { assert.deepEqual(await input.json(), { purpose: 'operation-recovery', operation: proposed.operation, key: proposeKey }); return Response.json({ status: 'confirmed', result: proposed }); }
    return Response.json(mismatch ? { ...proposal, target: { kind: 'branch', id: '21' }, context: { organizationId: '20', branchId: '21', divisionId: null } } : proposal);
  } }));
  assert.deepEqual(await gateway.recover(actor, proposed.operation, proposeKey), { state: 'committed', result: proposed }); mismatch = true;
  await assert.rejects(gateway.recover(actor, proposed.operation, proposeKey)); assert.equal(calls, 4);
  const path = '/ordering/spending-policies', permission = 'order.spending.policy.approve';
  assert.equal(identityReturnPath(path), path); assert.deepEqual(loginFields(path, '', ''), { returnPath: path });
  for (const action of ['read', 'propose', 'approve']) assert.equal(identityDestinations(['order.spending.policy.' + action]).some(item => item.href === path), true);
  assert.equal(identityDestinations(['order.spend.approve']).some(item => item.href === path), false);
  assert.equal(identityDestinations([permission]).some(item => item.href === '/ordering/reviews'), false);
  assert.equal(recoveryPage(proposed.operation).href, path); assert.equal(operationLabel(approved.operation), 'Persetujuan aturan belanja');
  const session = parseIdentitySession({ ...sourceSession, stepUpOperations: [permission] });
  assert.deepEqual(stepUpFields(session, permission, path), { expectedActorId: session.account!.userId, operation: permission, returnPath: path });
  assert.throws(() => stepUpFields({ ...session, stepUpOperations: [] }, permission, path));
  for (const invalid of [path + '/', path + '?return=https://elsewhere.test', '//ordering/spending-policies']) assert.throws(() => loginFields(invalid, '', ''));
});
