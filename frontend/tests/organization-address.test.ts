import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { createOrganizationAddressGateway, organizationAddressInput, organizationAddressKeyOwner, parseOrganizationAddressTargets,
  parseOrganizationAddressCurrent, parseOrganizationAddressProposal, parseOrganizationAddressResult, prepareOrganizationAddress,
  prepareOrganizationAddressDecision } from '../src/modules/organizations/organization-address-client';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { recoveryPage, operationLabel } from '../src/modules/administration/operation-recovery-navigation';
import { identityDestinations, identityReturnPath } from '../src/modules/identity/identity-workflow';

const actor = '9007199254740993', targetId = '9007199254740994', proposalId = '9007199254740995', addressId = '9007199254740996';
const target = { kind: 'branch' as const, id: targetId }, purpose = 'delivery' as const;
const address = { line1: 'Gedung Operasional Tebet', line2: null, city: 'Jakarta Selatan', region: 'DKI Jakarta', postalCode: '12810', countryCode: 'ID' };
const selected = { target, purpose, code: 'BNI-TEBET', name: 'Cabang Tebet', scopeVersion: '2', profileVersion: '3', addressVersion: '0', addressId: null, versionId: null, currentAddress: null };
const input = { target, purpose, expectedScopeVersion: '2', expectedProfileVersion: '3', expectedAddressVersion: '0', address };
const preview = { binding: 'a'.repeat(64), view: { target, purpose, code: selected.code, name: selected.name, scopeVersion: '2', profileVersion: '3', addressVersion: '0', address, sourceHash: 'b'.repeat(64) } };
const proposal = { proposalId, addressId, revision: '1' as const, target, purpose, code: selected.code, name: selected.name, address, baseVersion: '0', proposedAt: '2026-10-02T06:00:00Z', state: 'pending' as const, decision: null, addressVersion: null, versionId: null, bindingCurrent: true };
const decisionPreview = { binding: 'c'.repeat(64), view: { proposalId, revision: '1' as const, addressId, target, purpose, decision: 'approve' as const, address, code: selected.code, name: selected.name, baseVersion: '0', bindingCurrent: true } };
const uuid = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', proposeKey = `oa.${targetId}.${purpose}.${uuid}`, approveKey = `oad.${proposalId}.approve.${uuid}`;
const proposed = { addressId, proposalId, recordId: proposalId, operation: 'organization.address.propose' as const, addressVersion: null, versionId: null, recordedAt: proposal.proposedAt };
const approved = { ...proposed, recordId: '9007199254740997', operation: 'organization.address.approve' as const, addressVersion: '1', versionId: '9007199254740998', recordedAt: '2026-10-02T06:02:00Z' };
const command = () => prepareOrganizationAddress(actor, selected, input, preview, 'Alamat pengiriman dari sumber organisasi');
const decisionCommand = () => prepareOrganizationAddressDecision(actor, proposal, decisionPreview, 'Alamat telah diperiksa secara independen');
function storageFixture() {
  const values = new Map<string, string>(); const storage = { get length() { return values.size; }, key: (index: number) => [...values.keys()][index] ?? null,
    getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); }, removeItem: (key: string) => { values.delete(key); } };
  return { values, journal: new OperationJournal(() => storage) };
}
test('approved named targets preserve Int64 identities and distinct postal purposes without implying recipient contacts', () => {
  assert.deepEqual(parseOrganizationAddressTargets({ items: [selected], hasMore: false }, purpose).items[0], selected);
  for (const changed of [{ ...selected, purpose: 'dispatch' }, { ...selected, target: { kind: 'warehouse', id: targetId } }, { ...selected, name: '' },
    { ...selected, target: { ...target, id: 9007199254740994 } }, { ...selected, target: { ...target, id: '9223372036854775808' } },
    { ...selected, currentAddress: address }, { ...selected, recipient: 'Invented recipient' }]) assert.throws(() => parseOrganizationAddressTargets({ items: [changed], hasMore: false }, purpose));
  assert.throws(() => parseOrganizationAddressTargets({ items: [selected, selected], hasMore: false }, purpose));
  assert.throws(() => organizationAddressInput({ ...input, address: { ...address, contact: 'recipient@example.test' } } as never));
  for (const changed of [{ ...input, purpose: 'legal_billing' }, { ...input, address: { ...address, countryCode: '' } }, { ...input, address: { ...address, line1: 'x\u200by' } },
    { ...input, address: { ...address, line2: '' } }]) assert.throws(() => organizationAddressInput(changed as never), RequestNotSentError);
  assert.deepEqual(organizationAddressInput({ ...input, address: { ...address, line1: '  Ｇedung Operasional Tebet ', countryCode: 'id' } }).address, address);
  const empty = { addressId: null, target, purpose, version: '0', versionId: null, code: selected.code, name: selected.name, address: null, approvedAt: null, current: false };
  assert.deepEqual(parseOrganizationAddressCurrent(empty, target, purpose), empty);
  assert.throws(() => parseOrganizationAddressCurrent({ ...empty, address }, target, purpose));
  assert.throws(() => parseOrganizationAddressCurrent(empty, { ...target, id: '1' }, purpose));
  const historical = { ...empty, addressId, version: '1', versionId: '9', address, approvedAt: '2026-10-02T06:02:00Z', current: false };
  assert.deepEqual(parseOrganizationAddressCurrent(historical, target, purpose), historical);
  assert.equal(parseOrganizationAddressCurrent({ ...historical, version: '9223372036854775806' }, target, purpose).version, '9223372036854775806');
  assert.equal(organizationAddressInput({ ...input, expectedScopeVersion: '9223372036854775806', expectedProfileVersion: '9223372036854775806' }).expectedScopeVersion, '9223372036854775806');
  assert.throws(() => organizationAddressInput({ ...input, expectedAddressVersion: '9223372036854775806' }), RequestNotSentError);
  assert.deepEqual(parseOrganizationAddressTargets({ items: [{ ...selected, addressVersion: '1', addressId, versionId: '9', currentAddress: null }], hasMore: false }, purpose).items[0].currentAddress, null);
});
test('frozen proposal binds actual target, profile/address versions and exact explicit postal fields', () => {
  const frozen = command(); assert.ok(Object.isFrozen(frozen)); assert.ok(Object.isFrozen(frozen.body.input)); assert.ok(Object.isFrozen(frozen.body.input.address));
  assert.ok(Object.isFrozen(frozen.body.input.target)); assert.equal(frozen.targetId, targetId); assert.deepEqual(frozen.body.input, input);
  for (const changed of [{ ...preview, view: { ...preview.view, name: 'Different branch' } }, { ...preview, view: { ...preview.view, target: { ...target, id: '1' } } },
    { ...preview, view: { ...preview.view, purpose: 'dispatch' } }, { ...preview, view: { ...preview.view, scopeVersion: '3' } },
    { ...preview, view: { ...preview.view, profileVersion: '4' } }, { ...preview, view: { ...preview.view, addressVersion: '1' } },
    { ...preview, view: { ...preview.view, address: { ...address, postalCode: '12811' } } }]) assert.throws(() => prepareOrganizationAddress(actor, selected, input, changed as never, 'Periksa alamat'));
  for (const reason of ['', ' ', 'Alasan\nterpisah', 'x'.repeat(1001)]) assert.throws(() => prepareOrganizationAddress(actor, selected, input, preview, reason));
});
test('independent decision retains immutable proposal and permits exact stale rejection while denying stale approval', () => {
  assert.deepEqual(parseOrganizationAddressProposal(proposal, proposalId), proposal);
  assert.ok(Object.isFrozen(decisionCommand().body));
  assert.throws(() => prepareOrganizationAddressDecision(actor, proposal, { ...decisionPreview, view: { ...decisionPreview.view, bindingCurrent: false } }, 'Pemeriksaan'));
  const stale = { ...proposal, bindingCurrent: false }, rejected = { ...decisionPreview, view: { ...decisionPreview.view, decision: 'reject' as const, bindingCurrent: false } };
  assert.equal(prepareOrganizationAddressDecision(actor, stale, rejected, 'Tolak pengajuan yang sumbernya berubah').decision, 'reject');
  for (const changed of [{ ...decisionPreview, view: { ...decisionPreview.view, proposalId: '1' } }, { ...decisionPreview, view: { ...decisionPreview.view, addressId: '1' } },
    { ...decisionPreview, view: { ...decisionPreview.view, address: { ...address, line1: 'Changed address' } } }]) assert.throws(() => prepareOrganizationAddressDecision(actor, proposal, changed, 'Pemeriksaan'));
  assert.throws(() => prepareOrganizationAddressDecision(actor, { ...proposal, state: 'rejected', decision: 'reject' }, decisionPreview, 'Pemeriksaan'));
  assert.throws(() => parseOrganizationAddressResult({ ...approved, proposalId: '1' }, approveKey, approved.operation));
  assert.throws(() => parseOrganizationAddressResult({ ...proposed, versionId: '1' }, proposeKey, proposed.operation));
  assert.throws(() => organizationAddressKeyOwner(approveKey, 'organization.address.reject'));
});
test('address transport uses actual expected actor, CSRF, immutable revision and one command attempt', async () => {
  const sent: Request[] = []; const gateway = createOrganizationAddressGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    const actual = request as Request; if (actual.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf-memory-only' }); sent.push(actual);
    assert.equal(actual.headers.get('X-Expected-Actor-Id'), actor); assert.equal(actual.headers.get('X-CSRF-Token'), 'csrf-memory-only');
    assert.equal(actual.credentials, 'same-origin'); assert.equal(actual.cache, 'no-store'); assert.equal(actual.redirect, 'error');
    if (actual.url.endsWith('/targets/query')) return Response.json({ items: [selected], hasMore: false });
    if (actual.url.endsWith('/proposals')) { assert.equal(actual.headers.get('Idempotency-Key'), proposeKey); assert.deepEqual(await actual.json(), command().body); return Response.json(proposed); }
    assert.equal(actual.headers.get('If-Match'), '"1"'); assert.equal(actual.headers.get('Idempotency-Key'), approveKey); assert.deepEqual(await actual.json(), decisionCommand().body); return Response.json(approved);
  } }));
  assert.deepEqual(await gateway.targets(actor, purpose), { items: [selected], hasMore: false });
  assert.deepEqual(await gateway.propose(command(), proposeKey, () => true), proposed); assert.deepEqual(await gateway.decide(decisionCommand(), approveKey, () => true), approved);
  await assert.rejects(gateway.decide(decisionCommand(), approveKey, () => false), RequestNotSentError); assert.equal(sent.length, 3);
});
test('lost response retains only exact actor-operation-key metadata; missing result cannot retry or close', async () => {
  const { values, journal } = storageFixture(), ledger = new MutationLedger(() => approveKey); let attempts = 0;
  await assert.rejects(submitTrackedRequest(ledger, journal, actor, 'address-decision', approved.operation, decisionCommand().body, async () => { attempts++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
  const entry = journal.list(actor)[0], serialized = [...values.values()][0];
  assert.deepEqual(Object.keys(JSON.parse(serialized)).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  for (const secret of [address.line1, address.postalCode, decisionCommand().body.reason, decisionPreview.binding]) assert.equal(serialized.includes(secret), false);
  const legacy = { lookup: async () => { throw new Error('Generic lookup must not run'); }, close: async () => { throw new Error('Generic close must not run'); } };
  assert.deepEqual(operationRecoveryCapabilities(entry.operation), { canCheck: false, canClose: false, unavailable: 'adapter' });
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'organization.address': { lookup: async () => null } }), { state: 'unconfirmed' });
  assert.equal(journal.list(actor).length, 1);
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'organization.address': { lookup: async () => ({ state: 'committed', result: approved }) } }, 'Close'));
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'organization.address': { lookup: async () => ({ state: 'committed', result: { ...approved, proposalId: '1' } }) } }));
  assert.equal(journal.list(actor).length, 1);
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'organization.address': { lookup: async () => ({ state: 'committed', result: approved }) } }), { state: 'committed', result: { id: proposalId, version: '1' } });
  assert.equal(journal.list(actor).length, 0); assert.equal(attempts, 1);
});
test('proposal recovery verifies original scoped target from actual proposal and routes read-only users without financial grants', async () => {
  let mismatch = false; const requests: string[] = []; const gateway = createOrganizationAddressGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    const actual = request as Request; if (actual.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); requests.push(actual.url);
    if (actual.url.endsWith('/operations/query')) return Response.json({ status: 'committed', result: proposed });
    return Response.json(mismatch ? { ...proposal, target: { ...target, id: '1' } } : proposal);
  } }));
  assert.deepEqual(await gateway.recover(actor, proposed.operation, proposeKey), { state: 'committed', result: proposed }); mismatch = true;
  await assert.rejects(gateway.recover(actor, proposed.operation, proposeKey)); assert.equal(requests.length, 4);
  const addressPath = '/administration/organizations/addresses'; assert.equal(identityReturnPath(addressPath), addressPath);
  assert.equal(identityDestinations(['organization.address.read']).some(item => item.href === addressPath), true);
  assert.equal(identityDestinations(['technical.configure']).some(item => item.href === addressPath), true);
  assert.equal(identityDestinations(['organization.approve']).some(item => item.href === addressPath), true);
  assert.equal(identityDestinations(['organization.address.read']).some(item => item.href === '/billing/payment-reviews'), false);
  assert.equal(recoveryPage(proposed.operation).href, addressPath); assert.equal(operationLabel(approved.operation), 'Persetujuan alamat organisasi');
});
