import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { recoveryPage, operationLabel } from '../src/modules/administration/operation-recovery-navigation';
import { SourceMemoryStorage } from './stock-source-fixtures';
import { commercialPolicyCharge, commercialPolicyInput, commercialPolicyKeyOwner, commercialPolicyTaxRate, commercialPolicyTime, createCommercialPolicyGateway,
  parseCommercialPolicyCurrent, parseCommercialPolicyProposal, parseCommercialPolicyResult, parseCommercialPolicySellers, parseCommercialPolicyTargets,
  prepareCommercialPolicy, prepareCommercialPolicyDecision } from '../src/modules/ordering/order-commercial-policy-client';

const actor = '9007199254740993', proposalId = '9007199254740994', policyId = '9007199254740995';
const book = { organizationId: '100', shopId: '101', channelId: '103', currency: 'IDR' as const };
const labels = { organizationName: 'Koperasi', shopName: 'Toko Tebet', channelName: 'Internal' };
const sellerVersions = { organizationScopeVersion: '2', organizationProfileVersion: '1', shopScopeVersion: '2', shopProfileVersion: '1', channelScopeVersion: '2', channelProfileVersion: '1' };
const seller = { seller: book, ...labels, sellerVersions };
const target = { kind: 'branch' as const, id: '11', seller: book }, context = { organizationId: '10', branchId: '11', divisionId: null };
const selected = { target, context, code: 'TEBET', name: 'Cabang Tebet', seller: labels, sellerVersions, expectedScopeVersion: '2', expectedProfileVersion: '3', policyVersion: '0', policyId: null, versionId: null };
const rule = { applicability: { budget: 'required' as const, costCenter: 'not_applicable' as const, spending: 'required' as const, creditHold: 'required' as const },
  charges: '1500.25', taxTreatment: 'taxable_exclusive' as const, taxRate: '12.3456', exemptionReference: null, taxBase: 'subtotal_and_charges' as const,
  taxRounding: 'line' as const, rounding: 'half_even' as const, terms: { grouping: 'receipt' as const, termOrigin: 'issue_date' as const, termDays: 14,
    dayRule: 'working_days' as const, workingDays: [1, 2, 3, 4, 5], holidays: ['2026-10-14'], numberingProcedure: 'Prosedur nomor disetujui',
    reissueProcedure: 'Koreksi tetap tertaut', taxDocumentProcess: 'Proses dokumen pajak disetujui' } };
const validFrom = '2026-10-02T00:00:00Z', validUntil = '2026-11-01T00:00:00Z';
const input = { target, expectedScopeVersion: '2', expectedProfileVersion: '3', sellerVersions, expectedPolicyVersion: '0', rule, validFrom, validUntil };
const preview = { binding: 'a'.repeat(64), view: { target, context, code: selected.code, name: selected.name, seller: labels, sellerVersions, scopeVersion: '2', profileVersion: '3',
  policyVersion: '0', rule, validFrom, validUntil, sourceHash: 'b'.repeat(64) } };
const proposal = { proposalId, policyId, revision: '1' as const, target, context, code: selected.code, name: selected.name, seller: labels, sellerVersions, rule,
  validFrom, validUntil, baseVersion: '0', proposedAt: '2026-10-01T09:00:00Z', state: 'pending' as const, decision: null, policyVersion: null, versionId: null, bindingCurrent: true };
const decisionPreview = { binding: 'c'.repeat(64), view: { ...proposal, decision: 'approve' as const } };
const uuid = 'a'.repeat(32), proposeKey = `ocp.b.11.100.101.103.${uuid}`, approveKey = `ocpd.approve.${proposalId}.${uuid}`;
const proposed = { policyId, proposalId, recordId: proposalId, operation: 'ordering.commercial.policy.propose' as const, policyVersion: null, versionId: null, recordedAt: proposal.proposedAt };
const approved = { ...proposed, recordId: '9007199254740996', operation: 'ordering.commercial.policy.approve' as const, policyVersion: '1', versionId: '9007199254740997', recordedAt: '2026-10-01T09:02:00Z' };
const command = () => prepareCommercialPolicy(actor, selected, input, preview, 'Aturan komersial telah ditinjau');
const decisionCommand = () => prepareCommercialPolicyDecision(actor, proposal, decisionPreview, 'Pemeriksaan independen sumber dan seluruh ketentuan');

test('exact tax strings, explicit applicability and existing invoice terms reject defaults, numeric money, malformed dates and excess precision', () => {
  assert.equal(commercialPolicyTaxRate('100.0000'), '100'); assert.equal(commercialPolicyTaxRate('0.0100'), '0.01'); assert.equal(commercialPolicyTaxRate('12.3456'), '12.3456');
  assert.equal(commercialPolicyCharge('0'), '0.00'); assert.equal(commercialPolicyTime('2026-10-02T07:30'), '2026-10-02T00:30:00.000Z');
  assert.deepEqual(commercialPolicyInput(input), input);
  for (const value of ['-1', '100.0001', '1e1', '1,5', '01', '1.00001', '10\n', 'NaN', '']) assert.throws(() => commercialPolicyTaxRate(value), RequestNotSentError);
  for (const changed of [{ ...rule, taxRate: 12.3456 }, { ...rule, taxRate: '12.34560' }, { ...rule, taxRate: '0' }, { ...rule, exemptionReference: 'Pengecualian' },
    { ...rule, charges: 1500.25 }, { ...rule, applicability: { ...rule.applicability, creditHold: undefined } }, { ...rule, applicability: { ...rule.applicability, spending: 'unlimited' } },
    { ...rule, terms: { ...rule.terms, workingDays: [] } }, { ...rule, terms: { ...rule.terms, workingDays: [1, 1] } },
    { ...rule, terms: { ...rule.terms, holidays: ['2026-02-30'] } }, { ...rule, terms: { ...rule.terms, holidays: ['2026-10-14', '2026-10-14'] } },
    { ...rule, terms: { ...rule.terms, termDays: 366 } }, { ...rule, terms: { ...rule.terms, privateCalendar: 'hidden' } }])
    assert.throws(() => commercialPolicyInput({ ...input, rule: changed } as never), RequestNotSentError);
  const exempt = { ...rule, taxTreatment: 'exempt' as const, taxRate: '0', exemptionReference: 'Keputusan pengecualian khusus' };
  assert.equal(commercialPolicyInput({ ...input, rule: exempt }).rule.taxRate, '0');
  assert.throws(() => commercialPolicyInput({ ...input, rule: { ...exempt, exemptionReference: null } }));
  for (const changed of [{ ...input, validUntil: validFrom }, { ...input, validFrom: '2026-10-02T00:00:00.0001Z' }, { ...input, sellerVersions: { ...sellerVersions, shopProfileVersion: 1 } },
    { ...input, expectedPolicyVersion: '9223372036854775806' }]) assert.throws(() => commercialPolicyInput(changed as never));
});
test('seller and target discovery retain canonical full seller keys, all six versions and raw-page cursors without adopting foreign choices', () => {
  assert.deepEqual(parseCommercialPolicySellers({ items: [seller], nextSeller: null }).items, [seller]);
  assert.deepEqual(parseCommercialPolicyTargets({ items: [selected], nextTargetId: null }, seller).items, [selected]);
  assert.equal(parseCommercialPolicyTargets({ items: [], nextTargetId: '8' }, seller, '9').nextTargetId, '8');
  assert.deepEqual(parseCommercialPolicySellers({ items: [], nextSeller: { shopId: '99', channelId: '98' } }, { shopId: '100', channelId: '103' }).items, []);
  for (const changed of [{ ...seller, seller: { ...book, currency: 'USD' } }, { ...seller, sellerVersions: { ...sellerVersions, channelScopeVersion: 2 } },
    { ...seller, source: { private: true } }]) assert.throws(() => parseCommercialPolicySellers({ items: [changed], nextSeller: null }));
  assert.throws(() => parseCommercialPolicySellers({ items: [seller, seller], nextSeller: null }));
  assert.throws(() => parseCommercialPolicySellers({ items: [seller], nextSeller: null }, { shopId: '101', channelId: '103' }));
  for (const changed of [{ ...selected, target: { ...target, seller: { ...book, channelId: '203' } } }, { ...selected, context: { ...context, divisionId: '12' } },
    { ...selected, sellerVersions: { ...sellerVersions, organizationProfileVersion: '2' } }, { ...selected, seller: { ...labels, shopName: 'Toko lain' } },
    { ...selected, policyVersion: '1' }, { ...selected, expectedScopeVersion: 2 }]) assert.throws(() => parseCommercialPolicyTargets({ items: [changed], nextTargetId: null }, seller));
  assert.throws(() => parseCommercialPolicyTargets({ items: [selected], nextTargetId: '12' }, seller));
  assert.throws(() => parseCommercialPolicyTargets({ items: [selected], nextTargetId: null }, seller, '11'));
});
test('frozen proposal and independent decision retain every financial, seller, profile and validity choice and refuse stale changes', () => {
  const frozen = command(); assert.ok(Object.isFrozen(frozen.body.input.rule.applicability)); assert.ok(Object.isFrozen(frozen.body.input.rule.terms.workingDays));
  assert.ok(Object.isFrozen(frozen.target.seller)); assert.deepEqual(parseCommercialPolicyProposal(proposal, proposalId), proposal);
  for (const changed of [{ ...preview, view: { ...preview.view, seller: { ...labels, channelName: 'Lain' } } },
    { ...preview, view: { ...preview.view, target: { ...target, seller: { ...book, shopId: '201' } } } },
    { ...preview, view: { ...preview.view, rule: { ...rule, taxRate: '12.3455' } } }, { ...preview, view: { ...preview.view, rule: { ...rule, charges: '1500.24' } } },
    { ...preview, view: { ...preview.view, validUntil: '2026-12-01T00:00:00Z' } }, { ...preview, binding: 'A'.repeat(64) },
    ...Object.keys(sellerVersions).map(key => ({ ...preview, view: { ...preview.view, sellerVersions: { ...sellerVersions, [key]: '4' } } }))])
    assert.throws(() => prepareCommercialPolicy(actor, selected, input, changed as never, 'Alasan'));
  assert.ok(Object.isFrozen(decisionCommand().body));
  assert.throws(() => prepareCommercialPolicyDecision(actor, proposal, { ...decisionPreview, view: { ...decisionPreview.view, bindingCurrent: false } }, 'Alasan'));
  assert.throws(() => prepareCommercialPolicyDecision(actor, proposal, { ...decisionPreview, view: { ...decisionPreview.view, sellerVersions: { ...sellerVersions, shopScopeVersion: '3' } } }, 'Alasan'));
  const stale = { ...proposal, bindingCurrent: false };
  assert.equal(prepareCommercialPolicyDecision(actor, stale, { ...decisionPreview, view: { ...stale, decision: 'reject' } }, 'Sumber telah berubah').decision, 'reject');
  const empty = { target, context, code: selected.code, name: selected.name, seller: labels, sellerVersions, expectedScopeVersion: '2', expectedProfileVersion: '3',
    policyId: null, version: '0', versionId: null, rule: null, validFrom: null, validUntil: null, approvedAt: null, current: false, active: false };
  assert.deepEqual(parseCommercialPolicyCurrent(empty, selected), empty); assert.throws(() => parseCommercialPolicyCurrent({ ...empty, active: true }, selected));
  assert.throws(() => parseCommercialPolicyCurrent({ ...empty, target: { ...target, seller: { ...book, channelId: '203' } } }, selected));
});
test('original proposal keys bind complete seller context while exact decision keys bind the original proposal and outcome', () => {
  assert.deepEqual(commercialPolicyKeyOwner(proposeKey, proposed.operation), { kind: 'branch', id: '11', seller: book });
  assert.deepEqual(parseCommercialPolicyResult(proposed, proposeKey, proposed.operation), proposed);
  assert.deepEqual(parseCommercialPolicyResult(approved, approveKey, approved.operation), approved);
  for (const key of [proposeKey + '\n', proposeKey.replace('100.101.103', '0100.101.103'), proposeKey.replace(uuid, '0'.repeat(32))])
    assert.throws(() => commercialPolicyKeyOwner(key, proposed.operation));
  assert.throws(() => commercialPolicyKeyOwner(approveKey, 'ordering.commercial.policy.reject'));
  assert.throws(() => parseCommercialPolicyResult({ ...proposed, recordId: '1' }, proposeKey, proposed.operation));
  assert.throws(() => parseCommercialPolicyResult({ ...approved, proposalId: '1' }, approveKey, approved.operation));
  assert.throws(() => parseCommercialPolicyResult({ ...approved, operation: proposed.operation }, approveKey, approved.operation));
});
test('commercial mutations retain CSRF, actor, immutable body, key and If-Match and never send stale or cross-seller attempts', async () => {
  let calls = 0; const gateway = createCommercialPolicyGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf-memory' }); calls++;
    assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('X-CSRF-Token'), 'csrf-memory');
    assert.equal(input.credentials, 'same-origin'); assert.equal(input.cache, 'no-store'); assert.equal(input.redirect, 'error');
    if (input.url.endsWith('/proposals')) { assert.equal(input.headers.get('Idempotency-Key'), proposeKey); assert.deepEqual(await input.json(), command().body); return Response.json(proposed); }
    assert.equal(input.headers.get('If-Match'), '"1"'); assert.equal(input.headers.get('Idempotency-Key'), approveKey); assert.deepEqual(await input.json(), decisionCommand().body);
    return calls === 2 ? Response.json(approved) : Response.json({ code: 'step_up_required' }, { status: 403 });
  } }));
  await assert.rejects(gateway.propose(command(), proposeKey, () => false), RequestNotSentError);
  await assert.rejects(gateway.propose(command(), proposeKey.replace('.103.', '.203.'), () => true), AccessFailure); assert.equal(calls, 0);
  assert.deepEqual(await gateway.propose(command(), proposeKey, () => true), proposed); assert.deepEqual(await gateway.decide(decisionCommand(), approveKey, () => true), approved);
  await assert.rejects(gateway.decide(decisionCommand(), approveKey, () => true), { code: 'step_up_required' }); assert.equal(calls, 3);
  const uncertain = createCommercialPolicyGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); return Response.json(proposed);
  } }));
  let current = true;
  await assert.rejects(uncertain.propose(command(), proposeKey, () => { const before = current; current = false; return before; }), { code: 'commit_outcome_unknown' });
});
test('lookup-only recovery authenticates full original proposal target/seller and current decision outcome without resending', async () => {
  let mismatch = '', calls = 0, decisionResult = false;
  const gateway = createCommercialPolicyGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++;
    assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('Idempotency-Key'), null);
    if (input.url.endsWith('/operations/query')) { const body = await input.json() as Record<string, unknown>; assert.equal(body.purpose, 'operation-recovery'); return Response.json({ status: 'confirmed', result: decisionResult ? approved : proposed }); }
    const actual = decisionResult ? { ...proposal, state: 'approved', decision: 'approve', policyVersion: approved.policyVersion, versionId: approved.versionId } : proposal;
    return Response.json(mismatch === 'seller' ? { ...actual, target: { ...target, seller: { ...book, channelId: '203' } } } :
      mismatch === 'policy' ? { ...actual, policyId: '1' } : mismatch === 'decision' ? { ...proposal, state: 'rejected', decision: 'reject' } : actual);
  } }));
  assert.deepEqual(await gateway.recover(actor, proposed.operation, proposeKey), { state: 'committed', result: proposed });
  mismatch = 'seller'; await assert.rejects(gateway.recover(actor, proposed.operation, proposeKey));
  mismatch = 'policy'; await assert.rejects(gateway.recover(actor, proposed.operation, proposeKey));
  mismatch = ''; decisionResult = true; assert.deepEqual(await gateway.recover(actor, approved.operation, approveKey), { state: 'committed', result: approved });
  mismatch = 'decision'; await assert.rejects(gateway.recover(actor, approved.operation, approveKey)); assert.equal(calls, 10);
});
test('bounded maximum Int64 keys and central metadata-only recovery preserve actor namespace and never close or resend commercial decisions', async () => {
  const largeKey = `ocp.d.9223372036854775807.9223372036854775806.9223372036854775805.9223372036854775804.${uuid}`;
  assert.ok(largeKey.length <= 120); assert.equal(commercialPolicyKeyOwner(largeKey, proposed.operation).kind, 'division');
  assert.equal(commercialPolicyKeyOwner(largeKey, proposed.operation).seller?.channelId, '9223372036854775804');
  assert.throws(() => commercialPolicyKeyOwner(largeKey.replace('9223372036854775807', '9223372036854775808'), proposed.operation));
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => approveKey); let sends = 0;
  await assert.rejects(submitTrackedRequest(ledger, journal, actor, 'commercial-policy-decision', approved.operation, decisionCommand().body,
    async () => { sends++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
  assert.equal(journal.list('99').length, 0); const entry = journal.list(actor)[0]; assert.ok(entry);
  const saved = [...storage.values.values()][0]; assert.deepEqual(Object.keys(JSON.parse(saved)).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  for (const privateValue of [decisionCommand().body.reason, decisionPreview.binding, rule.charges, rule.taxRate, rule.terms.taxDocumentProcess]) assert.equal(saved.includes(privateValue), false);
  const legacy = { lookup: async () => { throw new Error('No generic lookup'); }, close: async () => { throw new Error('No generic close'); } };
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'ordering.commercial.policy': { lookup: async () => ({ state: 'committed', result: approved }) } }, 'Tutup'));
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'ordering.commercial.policy': { lookup: async () => ({ state: 'committed', result: { ...approved, proposalId: '1' } }) } }));
  assert.equal(journal.list(actor).length, 1);
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'ordering.commercial.policy': { lookup: async () => ({ state: 'committed', result: approved }) } }),
    { state: 'committed', result: { id: proposalId, version: '1' } });
  assert.equal(journal.list(actor).length, 0); assert.equal(sends, 1);
  assert.equal(recoveryPage(proposed.operation).href, '/ordering/commercial-policies'); assert.equal(operationLabel(approved.operation), 'Persetujuan aturan komersial');
});
