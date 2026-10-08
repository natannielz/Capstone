import test from 'node:test';
import assert from 'node:assert/strict';
import {AnomalyJournal, parseAnomalyReference, type AnomalyReference} from '../src/modules/operations/anomaly-journal';
import {createAnomalyGateway, retainAnomalyPolicyReference} from '../src/modules/operations/anomaly-client';
import {AccessFailure} from '../src/modules/administration/workflow';

class StorageMemory {
  rows = new Map<string, string>();
  get length() {return this.rows.size;}
  key(index: number) {return [...this.rows.keys()][index] ?? null;}
  getItem(key: string) {return this.rows.get(key) ?? null;}
  setItem(key: string, value: string) {this.rows.set(key, value);}
  removeItem(key: string) {this.rows.delete(key);}
}
const reference = (operation: AnomalyReference['operation'], key: string = operation): AnomalyReference => ({actorId: '44', operation, sourceKind: 'order',
  sourceId: '9007199254741001', evaluationId: operation === 'review' ? '9007199254741002' : null,
  expectedRevision: operation === 'configure' ? '1' : null, key, createdAt: '2026-10-08T10:00:00.000Z'});
const evaluation = () => ({id: '9007199254741002', sourceKind: 'order', sourceId: '9007199254741001', sourceHash: 'a'.repeat(64), ruleVersion: 'operational-anomaly-v1',
  policyVersion: null, policyRevision: '1', recordedAt: '2026-10-08T10:00:00Z', current: true, disposition: null,
  signals: [{category: 'quantity', code: 'quantity_limit', state: 'policy_needed', reference: 'SKU/1', observed: '12', expected: null, policyVersion: null, explanation: 'Batas belum tersedia.'}]});

test('all three operation references survive reopening and simultaneous tabs cannot overwrite or clear another original', () => {
  const storage = new StorageMemory(), first = new AnomalyJournal(() => storage), second = new AnomalyJournal(() => storage);
  const refs = ['configure', 'evaluate', 'review'].map(operation => reference(operation as AnomalyReference['operation']));
  refs.forEach(ref => first.remember(ref)); second.remember(reference('evaluate', 'other-tab'));
  const reopened = new AnomalyJournal(() => storage); assert.equal(reopened.list('44').length, 4);
  second.confirmed(refs[0]); assert.deepEqual(reopened.list('44').map(ref => ref.key), ['evaluate', 'other-tab', 'review']);
  assert.throws(() => first.remember({...refs[1], sourceId: '2'}));
  assert.throws(() => first.confirmed({...refs[1], sourceId: '2'}));
  assert.equal(first.list('44').length, 3); assert.equal(first.list('45').length, 0);
  first.confirmed(refs[1]); assert.equal(second.list('44').some(ref => ref.key === 'other-tab'), true);
});

test('journal rejects business payloads, cross-actor references, invalid identities and corrupt persisted keys', () => {
  const storage = new StorageMemory(), journal = new AnomalyJournal(() => storage), ref = reference('review');
  for (const change of [{reason: 'Sensitive reason'}, {policy: {}}, {actorId: '45'}, {sourceId: '9223372036854775808'},
    {evaluationId: null}, {expectedRevision: '1'}, {key: 'key\n'}, {operation: 'other'}]) assert.throws(() => parseAnomalyReference({...ref, ...change}, '44'));
  journal.remember(ref); const savedKey = storage.key(0)!; storage.rows.set(savedKey + '.forged', storage.getItem(savedKey)!);
  assert.throws(() => journal.list('44'));
});

test('unavailable or silently dropping storage cannot permit an untracked command', () => {
  const unavailable = new AnomalyJournal(() => {throw new Error('blocked');}); assert.throws(() => unavailable.remember(reference('evaluate')));
  const storage = new StorageMemory(); storage.setItem = () => {}; const dropped = new AnomalyJournal(() => storage);
  assert.throws(() => dropped.remember(reference('configure')));
});

test('evaluation and disposition original-key recovery are actor-bound reads with no replacement command', async () => {
  const calls: string[] = [];
  const gateway = createAnomalyGateway(async (input, init) => {
    const url = String(input); calls.push(url); assert.equal(init?.method, 'GET'); assert.equal(new Headers(init?.headers).get('X-Expected-Actor-Id'), '44');
    const value = evaluation();
    return new Response(JSON.stringify({status: 'confirmed', evaluation: url.includes('operation=review') ? {...value,
      disposition: {id: '8', reviewerId: '44', recordedAt: value.recordedAt, outcome: 'requires_action', reason: 'Tinjauan asli'}} : value}));
  });
  for (const operation of ['evaluate', 'review'] as const) assert.equal((await gateway.recover(reference(operation), {actorId: '44', isCurrent: () => true})).status, 'confirmed');
  assert.match(calls[0], /operation=evaluate&key=evaluate$/); assert.match(calls[1], /operation=review&key=review&evaluationId=9007199254741002$/);
});

test('repeated denied, changed, unknown and not-confirmed recovery retains exact references', async () => {
  const storage = new StorageMemory(), journal = new AnomalyJournal(() => storage), ref = reference('review'); journal.remember(ref);
  for (const status of [403, 409, 503]) {
    const gateway = createAnomalyGateway(async () => new Response(JSON.stringify({code: 'scope_denied'}), {status}));
    await assert.rejects(gateway.recover(ref, {actorId: '44', isCurrent: () => true}));
    assert.equal(retainAnomalyPolicyReference(new AccessFailure(status, 'scope_denied'), true), true); assert.deepEqual(journal.list('44'), [ref]);
  }
  const gateway = createAnomalyGateway(async () => new Response(JSON.stringify({status: 'not_confirmed', evaluation: null})));
  assert.equal((await gateway.recover(ref, {actorId: '44', isCurrent: () => true})).status, 'not_confirmed'); assert.deepEqual(journal.list('44'), [ref]);
});

test('contradictory and other-source or other-reviewer recovery receipts are rejected', async () => {
  const value = evaluation();
  for (const body of [{status: 'confirmed', evaluation: null}, {status: 'not_confirmed', evaluation: value},
    {status: 'confirmed', evaluation: {...value, sourceId: '2'}}, {status: 'confirmed', evaluation: value},
    {status: 'confirmed', evaluation: {...value, disposition: {id: '8', reviewerId: '45', recordedAt: value.recordedAt, outcome: 'requires_action', reason: 'Other actor'}}}]) {
    const gateway = createAnomalyGateway(async () => new Response(JSON.stringify(body)));
    await assert.rejects(gateway.recover(reference('review'), {actorId: '44', isCurrent: () => true}), (error: unknown) => error instanceof AccessFailure);
  }
});
