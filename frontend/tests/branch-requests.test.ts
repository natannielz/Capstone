import assert from 'node:assert/strict';
import test from 'node:test';
import { createUnitTokoClient } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
import { SourceMemoryStorage } from './stock-source-fixtures';
import { branchRequestInput, branchRequestNumber, requestPackages, requestResult, requestStateLabel } from '../src/modules/ordering/branch-request-state';
import { createBranchRequestGateway, parseBranchRequest, parseBranchRequestDecision, parseBranchRequestPage, parseBranchRequestPreview } from '../src/modules/ordering/branch-request-client';

// Wire-only fixtures. Native Mongo/browser tests own the real producer graph.
const context = { organizationId: '10', branchId: '11', divisionId: null };
const owner = { kind: 'fulfillmentWarehouse' as const, organizationId: '100', branchId: null, divisionId: null, shopId: '101', warehouseId: '102', channelId: '103' };
const input = { context, lines: [{ skuId: '51', definitionId: '52', definitionVersion: '1', packages: 2 }], note: 'Kebutuhan rapat cabang' };
const labels = { buyingOrganization: 'Organisasi SINTETIS', branch: 'Cabang SINTETIS', division: null, fulfillmentOrganization: 'Koperasi SINTETIS', shop: 'Toko SINTETIS', warehouse: 'Gudang SINTETIS', channel: 'Omi SINTETIS' };
const lines = [{ ...input.lines[0], skuCode: 'SYN-BOX', familyName: 'ATK SINTETIS', packageLabel: 'Karton isi 12', packageKind: 'sealed', baseUnitCode: 'buah', baseUnitsPerPackage: 12, owner }];
const preview = { input, lines, owner, labels, binding: 'a'.repeat(64), observedAt: '2026-10-04T02:00:00Z' };
const view = { id: '88', number: 'PT-00000088', version: '1', state: 'pending', input, lines, owner, labels, createdBy: '1001', recordedAt: preview.observedAt, decision: null };
const submitted = { requestId: '88', decisionId: null, number: view.number, version: '1', state: 'pending', recordedAt: preview.observedAt };
const accepted = { ...submitted, version: '2', state: 'accepted', decisionId: '89' };
const malformed = (fn: () => unknown) => assert.throws(fn, (error: unknown) => error instanceof AccessFailure && error.code === 'branch_request_response_invalid');

test('request input cannot smuggle sale, price, address or duplicate lines', () => {
  assert.deepEqual(branchRequestInput(input), input);
  for (const value of [{ ...input, total: '100.00' }, { ...input, delivery: {} }, { ...input, lines: [{ ...input.lines[0], priceId: '9' }] },
    { ...input, lines: [...input.lines, ...input.lines] }, { ...input, context: { ...context, branchId: '11\n' } }, { ...input, note: 'line\nbreak' }]) assert.throws(() => branchRequestInput(value));
  assert.equal(requestPackages('6', 2, 2), 6);
  for (const value of ['0', '3', '2\n', '1.5', '1000001']) assert.throws(() => requestPackages(value, 2, 2));
});
test('physical preview binds exact selection, one owner and named context without financial extras', () => {
  const result = parseBranchRequestPreview(preview, input); assert.equal(result.lines[0].baseUnitsPerPackage, 12);
  malformed(() => parseBranchRequestPreview({ ...preview, total: '10.00' }));
  malformed(() => parseBranchRequestPreview({ ...preview, lines: [{ ...lines[0], packages: 3 }] }));
  malformed(() => parseBranchRequestPreview({ ...preview, lines: [{ ...lines[0], definitionVersion: '2' }] }));
  malformed(() => parseBranchRequestPreview({ ...preview, lines: [{ ...lines[0], owner: { ...owner, channelId: '104' } }] }));
  malformed(() => parseBranchRequestPreview({ ...preview, labels: { ...labels, division: 'Unexpected division' } }));
  malformed(() => parseBranchRequestPreview(preview, { ...input, lines: [{ ...input.lines[0], packages: 4 }] }));
});
test('F-026: a branch request is shown as PB- with six digits; the server PT- reference stays an internal key', () => {
  assert.equal(branchRequestNumber(requestResult(submitted).requestId), 'PB-000088');
  assert.equal(branchRequestNumber(parseBranchRequest(view).id), 'PB-000088');
  assert.throws(() => branchRequestNumber('0'));
});
test('human request number and immutable decision history must agree', () => {
  assert.equal(requestResult(submitted).number, 'PT-00000088'); assert.equal(requestResult(accepted).decisionId, '89');
  assert.equal(requestStateLabel('accepted'), 'Diterima untuk diproses');
  malformed(() => requestResult({ ...submitted, number: 'PT-88' }));
  malformed(() => requestResult({ ...accepted, decisionId: null }));
  malformed(() => requestResult({ ...submitted, decisionId: '89' }));
  assert.equal(parseBranchRequest(view).id, '88');
  malformed(() => parseBranchRequest({ ...view, state: 'accepted' }));
  malformed(() => parseBranchRequest({ ...view, state: 'rejected', version: '2', decision: { id: '89', action: 'accept', reason: 'Reviewed', recordedAt: view.recordedAt } }));
  assert.equal(parseBranchRequestDecision({ request: view, decision: 'accept', canDecide: true, binding: preview.binding, observedAt: preview.observedAt }, '88', 'accept').request.id, '88');
  malformed(() => parseBranchRequestDecision({ request: view, decision: 'reject', canDecide: true, binding: preview.binding, observedAt: preview.observedAt }, '88', 'accept'));
  malformed(() => parseBranchRequestPage({ items: [view, view], nextRequestId: null }));
});
test('same-origin request gateway sends current actor, CSRF, exact review and request-only path', async () => {
  const sent: Request[] = [], bodies: unknown[] = [];
  const client = createUnitTokoClient({ baseUrl: 'https://portal.unit-toko.co.id', fetch: async request => {
    const req = request as Request;
    if (new URL(req.url).pathname === '/api/v1/identity/csrf') return Response.json({ requestToken: 'csrf-fixture' });
    sent.push(req); bodies.push(await req.clone().json()); return Response.json(submitted);
  } });
  const gateway = createBranchRequestGateway(client);
  await gateway.submit('1001', parseBranchRequestPreview(preview), 'same-key', () => true);
  assert.equal(sent.length, 1); assert.equal(new URL(sent[0].url).pathname, '/api/v1/ordering/requests');
  assert.equal(sent[0].headers.get('X-Expected-Actor-Id'), '1001'); assert.equal(sent[0].headers.get('X-CSRF-Token'), 'csrf-fixture');
  assert.equal(sent[0].headers.get('Idempotency-Key'), 'same-key'); assert.equal(sent[0].headers.get('If-Match'), '"0"');
  assert.deepEqual((bodies[0] as { input: unknown }).input, input);
  await assert.rejects(() => gateway.submit('1001', parseBranchRequestPreview(preview), 'other-key', () => false)); assert.equal(sent.length, 1);
});
test('lost response keeps only original actor/key and recovery checks without resending', async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => 'original-request-key'); let writes = 0, reads = 0;
  await assert.rejects(() => submitTrackedRequest(ledger, journal, '1001', 'submit', 'ordering.branchrequest.submit', input, async () => { writes++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
  const entries = journal.list('1001'); assert.equal(entries.length, 1); assert.equal(journal.list('1002').length, 0);
  assert.deepEqual(Object.keys(entries[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  const legacy = { lookup: async () => { throw new Error('Unexpected legacy API'); }, close: async () => { throw new Error('Unexpected close'); } };
  const adapters = { 'ordering.branchrequest': { lookup: async () => { reads++; return { state: 'committed' as const, result: requestResult(submitted) }; } } };
  await assert.rejects(() => reconcileOperationRecovery(entries[0], journal, legacy, adapters, undefined, () => false)); assert.equal(reads, 0); assert.equal(journal.list('1001').length, 1);
  assert.equal((await reconcileOperationRecovery(entries[0], journal, legacy, adapters)).state, 'committed'); assert.equal(writes, 1); assert.equal(reads, 1); assert.equal(journal.list('1001').length, 0);
  assert.equal(recoveryPage('ordering.branchrequest.submit').href, '/ordering/requests'); assert.equal(recoveryPage('ordering.branchrequest.decide').href, '/ordering/requests/incoming');
});
