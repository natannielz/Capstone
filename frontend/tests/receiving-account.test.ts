import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createReceivingAccountGateway, parseReceivingView, prepareReceivingProposal, prepareReceivingObservation, prepareReceivingDecision, validateReceivingReceipt } from '../src/modules/billing/receiving-account-client';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';

const maker = '9007199254740993', checker = '9007199254740994', time = '2026-09-30T03:00:00Z';
const details = { bankCode: 'FIXTUREBANK', accountNumber: '0000123456789', holderName: 'Synthetic cooperative', currency: 'IDR' as const };
const policy = { id: '11', revision: '1', scope: { fulfillmentOrganizationId: '100', shopId: '101', channelId: '103', warehouseId: null }, bankCode: 'FIXTUREBANK', bankName: 'Synthetic bank',
  method: 'authenticated-bank-record' as const, trustedChannelReference: 'synthetic portal', operatingOwnerReference: 'synthetic Finance', approvalReference: 'synthetic approval', active: true,
  approvedAt: '2026-09-29T03:00:00Z', expiresAt: '2026-10-01T03:00:00Z', maxObservationAgeSeconds: '3600' };
const initial = { proposal: { id: '51', accountId: '41', baseVersion: '0', actorId: maker, recordedAt: time, reason: 'Synthetic proposal', details, policy }, observation: null, decision: null, currentVersion: null };
const checked = { ...initial, observation: { id: '61', actorId: checker, checkedAt: time, sourceReference: 'synthetic bank confirmation', reason: 'Checked via approved channel', details } };
const result = { accountId: '41', proposalId: '51', recordId: '71', operation: 'billing.receiving-account.approve' as const, accountVersion: '1', recordedAt: time };
const key = 'ra.approve.51.11111111-1111-4111-8111-111111111111';
test('account comparison keeps exact bank observations, leading zeros and original maker exclusions', () => {
  assert.equal(parseReceivingView(initial, '51').proposal.actorId, maker);
  const view = parseReceivingView(checked, '51'); assert.equal(prepareReceivingDecision(checker, view, 'approve', 'Independent decision').owner, '51');
  assert.throws(() => prepareReceivingDecision(maker, view, 'approve', 'Self decision'));
  assert.throws(() => prepareReceivingObservation(maker, initial, details, 'source', 'self check', true));
  assert.throws(() => prepareReceivingObservation(checker, initial, details, 'source', 'missing attestation', false));
  assert.throws(() => prepareReceivingDecision(checker, initial, 'approve', 'No observed source'));
  assert.throws(() => prepareReceivingDecision(checker, { ...view, observation: { ...view.observation!, details: { ...details, accountNumber: '123456789' } } }, 'approve', 'Mismatch'));
  for (const accountNumber of [' 0000123456789', '0000123456789 ', '1e9', '123', '１２３４', '1234\n']) assert.throws(() => prepareReceivingProposal(maker, policy, { ...details, accountNumber }, 'invalid'));
  assert.equal(prepareReceivingProposal(maker, policy, details, 'Valid').body.details.accountNumber, '0000123456789');
  assert.throws(() => parseReceivingView({ ...initial, proposal: { ...initial.proposal, actorId: Number(maker) } }, '51'));
  assert.throws(() => parseReceivingView({ ...initial, paid: true }, '51'));
});
test('receiving-account client sends exact actor CSRF preconditions and recovers matching command only', async () => {
  let calls = 0, wrong = false;
  const gateway = createReceivingAccountGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); const path = new URL(request.url).pathname;
    if (path === '/api/v1/identity/csrf') return Response.json({ requestToken: 'fixture-csrf' });
    calls++; assert.equal(request.headers.get('X-Expected-Actor-Id'), checker); assert.equal(request.headers.get('X-CSRF-Token'), 'fixture-csrf'); assert.equal(request.cache, 'no-store');
    if (path.endsWith('/operations/query')) return Response.json({ status: 'committed', result: { ...result, proposalId: wrong ? '52' : '51' } });
    assert.equal(path, '/api/v1/billing/receiving-accounts/proposals/51/decisions'); assert.equal(request.headers.get('If-Match'), '"1"'); assert.equal(request.headers.get('Idempotency-Key'), key);
    assert.deepEqual(await request.json(), { decision: 'approve', observationId: '61', reason: 'Independent decision' }); return Response.json(result);
  } }));
  const command = prepareReceivingDecision(checker, checked, 'approve', 'Independent decision');
  await assert.rejects(() => gateway.send(command, key, () => false)); assert.equal(calls, 0);
  assert.deepEqual(await gateway.send(command, key, () => true), result); assert.deepEqual((await gateway.recover(checker, key, result.operation))?.result, result);
  wrong = true; await assert.rejects(() => gateway.recover(checker, key, result.operation));
  assert.throws(() => validateReceivingReceipt({ ...result, accountVersion: null }, key, result.operation));
  assert.throws(() => validateReceivingReceipt(result, key.replace('approve', 'reject'), result.operation));
});
test('account recovery journal contains no banking fields and cannot discard unknown or wrong receipts', async () => {
  const values = new Map<string, string>(); const journal = new OperationJournal(() => ({ get length() { return values.size; }, key: index => [...values.keys()][index] ?? null,
    getItem: key => values.get(key) ?? null, setItem: (key, value) => { values.set(key, value); }, removeItem: key => { values.delete(key); } }));
  const entry = journal.remember(checker, result.operation, key); assert.deepEqual(Object.keys(entry).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.equal(recoveryPage(entry.operation).href, '/billing/receiving-accounts'); assert.equal(operationRecoveryCapabilities(entry.operation).canClose, false);
  const legacy = { lookup: async () => { throw new Error('Wrong owner'); }, close: async () => { throw new Error('Cannot close'); } };
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, { 'billing.receiving-account': { lookup: async () => null } })).state, 'unconfirmed'); assert.equal(values.size, 1);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'billing.receiving-account': { lookup: async () => ({ state: 'committed', result: { ...result, proposalId: '52' } }) } })); assert.equal(values.size, 1);
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, { 'billing.receiving-account': { lookup: async () => ({ state: 'committed', result }) } })).state, 'committed'); assert.equal(values.size, 0);
});
