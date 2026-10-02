import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { operationLabel, recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
import { identityDestinations, identityReturnPath, loginFields, parseIdentitySession, stepUpFields } from '../src/modules/identity/identity-workflow';
import { createSupplierGateway, parseSupplierProposal, parseSupplierResult, parseSupplierTargets, prepareSupplier, prepareSupplierDecision,
  prepareSupplierObservation, supplierInput, supplierKeyOwner, supplierPolicyCurrent, supplierTime } from '../src/modules/procurement/supplier-client';
import { SourceMemoryStorage, sourceSession } from './stock-source-fixtures';

const actor = '9007199254740993', proposalId = '9007199254740994', supplierId = '9007199254740995';
const owner = { organizationId: '100', shopId: '101', channelId: '103' }, location = { organizationId: '100', shopId: '101', warehouseId: '102', channelId: '103' };
const organization = { id: '100', code: 'TOKO', name: 'Koperasi', scopeVersion: '2', profileVersion: '3' };
const source = { organization, shop: { ...organization, id: '101', code: 'SHOP', name: 'Toko' }, channel: { ...organization, id: '103', code: 'RETAIL', name: 'Ritel' } };
const selected = { owner, ...source };
const details = { subjectReference: 'SUP-001', legalName: 'PT Sumber Kertas', displayName: 'Sumber Kertas', registrationNamespace: 'NIB', registrationValue: '000123', address: 'Jalan Contoh 1', contactName: 'Petugas pemasok', contactMethod: 'phone' as const, contactValue: '0012345678' };
const policy = { id: '700', owner, revision: '1', subjectReference: details.subjectReference, legalName: details.legalName, registrationNamespace: details.registrationNamespace, registrationValue: details.registrationValue,
  method: 'established-contact' as const, trustedChannelReference: 'Direktori pemasok disetujui / 001', operatingOwnerReference: 'Pengelola pemasok', approvalReference: 'Persetujuan 01',
  approvedAt: '2026-10-01T00:00:00.000Z', expiresAt: '2026-11-01T00:00:00.000Z', maxObservationAgeSeconds: '900', allowReferenceOnly: true, active: true };
const choice = { skuId: '500', skuRevision: '2', definitionId: '501', definitionVersion: '1', skuCode: 'PAPER-BOX', familyName: 'Kertas', packageLabel: 'Karton 12', packageKind: 'sealed' as const, baseUnitCode: 'RIM', baseUnitsPerPackage: 12 };
const mapping = { location, sourceSystem: 'supplier' as const, sourceAccountReference: '0007', externalItemCode: '00Ab-01', skuId: choice.skuId, expectedSkuRevision: choice.skuRevision, definitionId: choice.definitionId, definitionVersion: choice.definitionVersion, validFrom: '2026-10-03T00:00:00.000Z', validUntil: '2026-11-01T00:00:00.000Z' };
const mappingSource = { location, ...source, warehouse: { ...organization, id: '102', code: 'WARE', name: 'Gudang' }, package: choice };
const input = { owner, supplierId: null, baseVersion: '0', policyId: policy.id, policyRevision: policy.revision, details, mappings: [mapping] };
const preview = { input, source, mappingSources: [mappingSource], policy, binding: 'a'.repeat(64), observedAt: '2026-10-03T01:00:00.000Z' };
const proposal = { id: proposalId, supplierId, version: '1' as const, input, source, mappingSources: [mappingSource], policy, reason: 'Daftarkan pemasok dan kode yang ditinjau', proposedAt: '2026-10-03T01:01:00.000Z', state: 'pending' as const, observation: null, decision: null };
const observation = { id: '710', observed: details, sourceReference: 'Catatan panggilan 01', trustedChannelReference: policy.trustedChannelReference, reason: 'Pemeriksaan independen identitas dan kontak', checkedAt: '2026-10-03T01:02:00.000Z' };
const observedProposal = { ...proposal, observation }, decisionPreview = { proposal: observedProposal, decision: 'approve' as const, observationId: observation.id, binding: 'b'.repeat(64), observedAt: '2026-10-03T01:03:00.000Z' };
const proposed = { supplierId, proposalId, proposalVersion: '1' as const, recordId: proposalId, operation: 'procurement.supplier.propose' as const, versionId: null, version: null };
const observedResult = { ...proposed, recordId: observation.id, operation: 'procurement.supplier.observe' as const };
const approved = { ...proposed, recordId: '711', operation: 'procurement.supplier.approve' as const, versionId: '712', version: '1' };
const uuid = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', proposeKey = `sup.100.101.103.0.${uuid}`, observeKey = `sua.observe.${proposalId}.${uuid}`, approveKey = `sua.approve.${proposalId}.${uuid}`;
const command = () => prepareSupplier(actor, selected, policy, [mappingSource], input, preview, proposal.reason);
const observationCommand = () => prepareSupplierObservation(actor, proposal, policy, details, observation.sourceReference, observation.reason, true, Date.parse(observation.checkedAt));
const decisionCommand = () => prepareSupplierDecision(actor, observedProposal, decisionPreview, 'Setujui setelah pemeriksaan independen');

test('supplier input preserves exact leading-zero source identity and finite WIB intervals without overlapping aliases', () => {
  assert.deepEqual(supplierInput(input), input); assert.equal(supplierTime('2026-10-03T07:00'), '2026-10-03T00:00:00.000Z');
  const exact = '\uFEFFＲujukan\u200d'; assert.equal(supplierInput({ ...input, details: { ...details, contactValue: exact } }).details.contactValue, exact);
  for (const value of ['', '2026-02-30T07:00', '2026-10-03T25:00', '0001-01-01T07:00']) assert.throws(() => supplierTime(value));
  for (const changed of [{ ...input, details: { ...details, registrationValue: null } }, { ...input, details: { ...details, contactValue: ' 001' } },
    { ...input, details: { ...details, contactValue: 'line\nbreak' } }, { ...input, baseVersion: '1' }, { ...input, baseVersion: '9223372036854775806' },
    { ...input, mappings: [{ ...mapping, validUntil: mapping.validFrom }] }, { ...input, mappings: [{ ...mapping, validFrom: '2026-10-03T07:00:00+07:00' }] },
    { ...input, mappings: [{ ...mapping, validFrom: '2026-10-03T00:00:00.0001Z' }] }, { ...input, mappings: [mapping, { ...mapping, location: { ...location, warehouseId: '202' } }] },
    { ...input, mappings: [{ ...mapping, location: { ...location, shopId: '201' } }] }]) assert.throws(() => supplierInput(changed), RequestNotSentError);
  const adjacent = { ...mapping, validFrom: mapping.validUntil, validUntil: '2026-12-01T00:00:00.000Z' };
  assert.deepEqual(supplierInput({ ...input, mappings: [adjacent, mapping] }).mappings, [mapping, adjacent]);
  assert.equal(supplierInput({ ...input, mappings: [mapping, { ...mapping, externalItemCode: '00ab-01' }] }).mappings.length, 2);
});

test('supplier proposal freezes named owner, actual package and trusted policy with exact source multiset', () => {
  assert.deepEqual(parseSupplierTargets({ items: [selected] }).items, [selected]); assert.throws(() => parseSupplierTargets({ items: [{ ...selected, shop: { ...source.shop, id: '201' } }] }));
  assert.ok(Object.isFrozen(command().body.input.mappings[0])); assert.ok(Object.isFrozen(command().body.input.details));
  for (const changed of [{ ...preview, policy: { ...policy, trustedChannelReference: details.contactValue } }, { ...preview, policy: { ...policy, active: false } },
    { ...preview, source: { ...source, shop: { ...source.shop, profileVersion: '4' } } }, { ...preview, mappingSources: [{ ...mappingSource, package: { ...choice, baseUnitsPerPackage: 24 } }] },
    { ...preview, input: { ...input, details: { ...details, contactValue: '991234' } } }]) assert.throws(() => prepareSupplier(actor, selected, policy, [mappingSource], input, changed, proposal.reason));
  const twoInput = { ...input, mappings: [mapping, { ...mapping, externalItemCode: 'Another' }] }, twoPreview = { ...preview, input: twoInput, mappingSources: [mappingSource, mappingSource] };
  assert.throws(() => prepareSupplier(actor, selected, policy, [mappingSource, { ...mappingSource, package: { ...choice, familyName: 'Changed' } }], twoInput, twoPreview, proposal.reason));
});

test('observations are independent exact entries and require approved policy reference capability; mismatches and expiry block approval', () => {
  const different = { ...details, contactValue: '00999999' }, saved = prepareSupplierObservation(actor, proposal, policy, different, observation.sourceReference, observation.reason, true, Date.parse(observation.checkedAt));
  assert.equal(saved.body.observed.contactValue, different.contactValue); assert.equal(saved.body.trustedChannelReference, policy.trustedChannelReference); assert.notEqual(saved.body.trustedChannelReference, details.contactValue);
  assert.ok(Object.isFrozen(saved.body.observed));
  for (const changed of [{ ...policy, allowReferenceOnly: false }, { ...policy, active: false }, { ...policy, revision: '2' }, { ...policy, trustedChannelReference: details.contactValue }])
    assert.throws(() => prepareSupplierObservation(actor, proposal, changed, details, observation.sourceReference, observation.reason, true, Date.parse(observation.checkedAt)));
  assert.throws(() => prepareSupplierObservation(actor, proposal, policy, details, observation.sourceReference, observation.reason, false, Date.parse(observation.checkedAt)));
  assert.throws(() => prepareSupplierObservation(actor, proposal, policy, details, observation.sourceReference, observation.reason, true, Date.parse(policy.expiresAt)));
  assert.equal(supplierPolicyCurrent(policy, Date.parse(policy.expiresAt)), false); assert.ok(Object.isFrozen(decisionCommand().body));
  const mismatch = { ...observedProposal, observation: { ...observation, observed: different } };
  assert.throws(() => prepareSupplierDecision(actor, mismatch, { ...decisionPreview, proposal: mismatch }, 'Alasan'));
  assert.throws(() => prepareSupplierDecision(actor, observedProposal, { ...decisionPreview, observedAt: '2026-10-03T01:17:00.000Z' }, 'Alasan'));
  assert.equal(prepareSupplierDecision(actor, mismatch, { ...decisionPreview, proposal: mismatch, decision: 'reject', observationId: null }, 'Rincian berbeda').decision, 'reject');
});

test('strict supplier history rejects contradictory version decisions, wrong identities and leaked private authority records', () => {
  assert.deepEqual(parseSupplierProposal(observedProposal, proposalId), observedProposal);
  const approvedProposal = { ...observedProposal, state: 'approved' as const, decision: { id: approved.recordId, decision: 'approve' as const, reason: 'Sesuai hasil pemeriksaan', decidedAt: decisionPreview.observedAt, versionId: approved.versionId, version: '1' } };
  assert.deepEqual(parseSupplierProposal(approvedProposal), approvedProposal);
  for (const changed of [{ ...proposal, people: [actor] }, { ...proposal, decision: approvedProposal.decision }, { ...approvedProposal, observation: null },
    { ...approvedProposal, decision: { ...approvedProposal.decision, version: '2' } }, { ...proposal, input: { ...input, supplierId: '999' } },
    { ...observedProposal, observation: { ...observation, trustedChannelReference: details.contactValue } }]) assert.throws(() => parseSupplierProposal(changed));
  assert.throws(() => parseSupplierResult({ ...approved, proposalId: '8' }, approveKey, approved.operation)); assert.throws(() => supplierKeyOwner(observeKey, approved.operation));
});

test('supplier mutations use exact reviewed CSRF and actor-bound requests with immutable If-Match and no retries', async () => {
  let calls = 0; const gateway = createSupplierGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++;
    assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(input.credentials, 'same-origin'); assert.equal(input.cache, 'no-store'); assert.equal(input.redirect, 'error');
    if (input.url.endsWith('/proposals')) { assert.equal(input.headers.get('Idempotency-Key'), proposeKey); assert.deepEqual(await input.json(), command().body); return Response.json(proposed); }
    assert.equal(input.headers.get('If-Match'), '"1"');
    if (input.url.endsWith('/observations')) { assert.equal(input.headers.get('Idempotency-Key'), observeKey); assert.deepEqual(await input.json(), observationCommand().body); return Response.json(observedResult); }
    assert.equal(input.headers.get('Idempotency-Key'), approveKey); assert.deepEqual(await input.json(), decisionCommand().body);
    return calls === 4 ? Response.json({ code: 'step_up_required' }, { status: 403 }) : Response.json(approved);
  } }));
  await assert.rejects(gateway.propose(command(), proposeKey, () => false), RequestNotSentError); assert.equal(calls, 0);
  assert.deepEqual(await gateway.propose(command(), proposeKey, () => true), proposed); assert.deepEqual(await gateway.observe(observationCommand(), observeKey, () => true), observedResult); assert.deepEqual(await gateway.decide(decisionCommand(), approveKey, () => true), approved);
  await assert.rejects(gateway.decide(decisionCommand(), approveKey, () => true), { code: 'step_up_required' }); assert.equal(calls, 4);
  let actorChecks = 0; await assert.rejects(gateway.decide(decisionCommand(), approveKey, () => ++actorChecks === 1), { code: 'commit_outcome_unknown' }); assert.equal(calls, 5);
});

test('supplier operation recovery keeps only original actor references and never resends or closes uncertain observations', async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => observeKey); let attempts = 0;
  await assert.rejects(submitTrackedRequest(ledger, journal, actor, 'supplier-observe', observedResult.operation, observationCommand().body, async () => { attempts++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
  const entry = journal.list(actor)[0], serialized = [...storage.values.values()][0]; assert.equal(journal.list('99').length, 0); assert.deepEqual(Object.keys(JSON.parse(serialized)).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  for (const privateValue of [details.contactValue, policy.trustedChannelReference, observation.sourceReference, observation.reason]) assert.equal(serialized.includes(privateValue), false);
  const legacy = { lookup: async () => { throw new Error('No generic lookup'); }, close: async () => { throw new Error('No generic close'); } };
  assert.deepEqual(operationRecoveryCapabilities(observedResult.operation), { canCheck: false, canClose: false, unavailable: 'adapter' });
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'procurement.supplier': { lookup: async () => null } }), { state: 'unconfirmed' });
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'procurement.supplier': { lookup: async () => ({ state: 'committed', result: observedResult }) } }, 'Close'));
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'procurement.supplier': { lookup: async () => ({ state: 'committed', result: { ...observedResult, proposalId: '8' } }) } }));
  assert.equal(journal.list(actor).length, 1); assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'procurement.supplier': { lookup: async () => ({ state: 'committed', result: observedResult }) } }), { state: 'committed', result: { id: proposalId, version: '1' } });
  assert.equal(journal.list(actor).length, 0); assert.equal(attempts, 1);
});

test('recovery checks allocated supplier owner and ordinary procurement or bank permissions reveal no supplier governance task', async () => {
  let foreign = false, calls = 0; const gateway = createSupplierGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++; assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('Idempotency-Key'), null);
    if (input.url.endsWith('/operations/query')) { assert.deepEqual(await input.json(), { purpose: 'operation-recovery', operation: proposed.operation, key: proposeKey }); return Response.json({ status: 'confirmed', result: proposed }); }
    assert.deepEqual(await input.json(), { purpose: 'supplier-review', access: 'propose' }); return Response.json(foreign ? { ...proposal, supplierId: '999' } : proposal);
  } }));
  assert.deepEqual(await gateway.recover(actor, proposed.operation, proposeKey), { state: 'committed', result: proposed }); foreign = true; await assert.rejects(gateway.recover(actor, proposed.operation, proposeKey)); assert.equal(calls, 4);
  const path = '/procurement/suppliers'; assert.equal(identityReturnPath(path), path); assert.deepEqual(loginFields(path, '', ''), { returnPath: path });
  for (const permission of ['supplier.change.propose', 'supplier.change.approve']) { assert.equal(identityDestinations([permission]).some(value => value.href === path), true); const session = parseIdentitySession({ ...sourceSession, stepUpOperations: [permission] });
    assert.deepEqual(stepUpFields(session, permission, path), { expectedActorId: session.account!.userId, operation: permission, returnPath: path }); }
  for (const permission of ['procurement.read', 'procurement.approve', 'receiving.account.approve']) assert.equal(identityDestinations([permission]).some(value => value.href === path), false);
  assert.equal(recoveryPage(observedResult.operation).href, path); assert.equal(operationLabel(observedResult.operation), 'Pemeriksaan independen pemasok');
  for (const invalid of [path + '/', path + '?return=https://elsewhere.test', '//procurement/suppliers']) assert.throws(() => loginFields(invalid, '', ''));
});
