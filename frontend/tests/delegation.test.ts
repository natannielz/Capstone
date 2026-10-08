import assert from 'node:assert/strict';
import test from 'node:test';
import {createDelegationGateway, parseDelegationCommand, parseDelegationPage, parseDelegationResult} from '../src/modules/administration/delegation-client';
import {DelegationJournal, type DelegationReference} from '../src/modules/administration/delegation-journal';
import {AccessFailure} from '../src/modules/administration/workflow';
import {RequestNotSentError} from '../src/api/client';

class Memory {
  private readonly entries = new Map<string, string>();
  get length() {return this.entries.size;}
  key(index: number) {return [...this.entries.keys()][index] ?? null;}
  getItem(key: string) {return this.entries.get(key) ?? null;}
  setItem(key: string, value: string) {this.entries.set(key, value);}
  removeItem(key: string) {this.entries.delete(key);}
}
const reference: DelegationReference = {actorId: '9007199254740993', action: 'approve', id: '9223372036854775800', revision: '2', key: 'original-operation', createdAt: '2026-10-08T00:00:00.000Z'};
const result = {delegationId: reference.id, eventId: '17', revision: '3', action: 'approve', status: 'active', recordedAt: reference.createdAt};
const scope = {kind: 'fulfillmentShop', organizationId: '100', shopId: '101', channelId: '103', branchId: null, divisionId: null, warehouseId: null};
const input = {action: 'propose', id: null, revision: '0', request: {sourceGrantId: '10', sourceVersion: '1', beneficiaryUserId: '20', beneficiaryMembershipId: '30', scope,
  permissions: ['stock.read'], effectiveAt: reference.createdAt, expiresAt: '2026-10-08T01:00:00.000Z'}, reason: 'Tugas sementara yang ditinjau'};

test('exact original result binding preserves Int64 IDs and rejects latest or unrelated outcomes', () => {
  assert.deepEqual(parseDelegationResult(result, reference), result);
  assert.equal(parseDelegationResult({...result, recordedAt: '2026-10-08T00:00:00+00:00'}, reference).recordedAt, '2026-10-08T00:00:00+00:00');
  assert.throws(() => parseDelegationResult({...result, recordedAt: '2026-10-08T07:00:00+07:00'}, reference), AccessFailure);
  for (const changed of [{...result, revision: '4'}, {...result, action: 'revoke'}, {...result, delegationId: '40'}, {...result, approved: true}])
    assert.throws(() => parseDelegationResult(changed, reference), AccessFailure);
});
test('proposal requires an explicit finite source selection and no owner-scoped fiction', () => {
  assert.deepEqual(parseDelegationCommand(input), input);
  for (const changed of [{...input, revision: '1'}, {...input, request: {...input.request, permissions: []}},
    {...input, request: {...input.request, permissions: ['stock.read', 'stock.read']}}, {...input, request: {...input.request, expiresAt: reference.createdAt}},
    {...input, request: {...input.request, scope: {...scope, kind: 'buyerOwned'}}}]) assert.throws(() => parseDelegationCommand(changed), RequestNotSentError);
});
test('actor-bound original references survive restart and simultaneous tabs without overwriting', () => {
  const memory = new Memory(), first = new DelegationJournal(() => memory), other = new DelegationJournal(() => memory);
  const separate = {...reference, key: 'separate-tab', id: '18'};
  first.remember(reference); other.remember(separate);
  assert.deepEqual(new DelegationJournal(() => memory).list(reference.actorId), [reference, separate]);
  assert.deepEqual(first.list('31'), []);
  assert.throws(() => first.remember({...reference, id: '19'}));
  first.confirmed(reference); assert.deepEqual(other.list(reference.actorId), [separate]);
  assert.equal(memory.length, 1);
  const stored = memory.getItem(memory.key(0)!)!;
  assert.equal(stored.includes('permissions'), false); assert.equal(stored.includes('reason'), false);
});
test('corrupt or unavailable storage prevents dispatch and cannot clear another original reference', () => {
  const memory = new Memory(), journal = new DelegationJournal(() => memory); journal.remember(reference);
  assert.throws(() => journal.confirmed({...reference, action: 'revoke'}));
  memory.setItem(memory.key(0)!, '{broken'); assert.throws(() => journal.list(reference.actorId));
  const refused = new DelegationJournal(() => {throw new Error('Storage refused');}); assert.throws(() => refused.remember(reference));
});
test('page validates stable descending order and unsupported task reassignment is explicit', () => {
  const row = {id: '2', revision: '1', sourceUserId: '1', approvedBy: null, request: input.request, status: 'pending', sourceState: 'current', reason: input.reason,
    updatedAt: reference.createdAt, allowedActions: ['approve'], taskReassignmentState: 'unavailable'};
  assert.equal(parseDelegationPage({items: [row], nextId: null}).items.length, 1);
  assert.throws(() => parseDelegationPage({items: [row, {...row, id: '3'}], nextId: null}));
  assert.throws(() => parseDelegationPage({items: [{...row, taskReassignmentState: 'assigned'}], nextId: null}));
});
test('actor changes after the actual command response retain an unknown outcome', async () => {
  let current = true, writes = 0;
  const fetcher: typeof fetch = async (url) => {
    if ((url instanceof Request ? url.url : String(url)).includes('/csrf')) return Response.json({requestToken: 'synthetic-csrf'});
    writes++; current = false; return Response.json(result);
  };
  const gateway = createDelegationGateway(fetcher, 'https://unit.test/api/v1/identity/csrf');
  const command = parseDelegationCommand({action: 'approve', id: reference.id, revision: reference.revision, request: null, reason: 'Reviewed approval'});
  await assert.rejects(() => gateway.change(command, reference, {actorId: reference.actorId, isCurrent: () => current}),
    error => error instanceof AccessFailure && error.code === 'delegation_outcome_unknown' && !(error instanceof RequestNotSentError));
  assert.equal(writes, 1);
});
test('repeated denied, conflict, and unconfirmed recovery retain the same original reference', async () => {
  const memory = new Memory(), journal = new DelegationJournal(() => memory); journal.remember(reference);
  for (const status of [403, 409, 503, 200]) {
    let reads = 0;
    const fetcher: typeof fetch = async (url) => (url instanceof Request ? url.url : String(url)).includes('/csrf') ? Response.json({requestToken: 'synthetic-csrf'}) :
      (reads++, Response.json(status === 200 ? {state: 'not_confirmed', result: null} : {code: 'synthetic_refusal'}, {status}));
    const gateway = createDelegationGateway(fetcher, 'https://unit.test/api/v1/identity/csrf');
    try {assert.equal(await gateway.recover(reference, {actorId: reference.actorId, isCurrent: () => true}), null);} catch (error) {assert.ok(error instanceof AccessFailure);}
    assert.deepEqual(journal.list(reference.actorId), [reference]); assert.equal(reads, 1);
  }
});
