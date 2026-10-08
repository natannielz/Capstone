import test from 'node:test';
import assert from 'node:assert/strict';
import {parseAnomalyEvaluation, parseAnomalyPolicyAttempt, retainAnomalyPolicyReference, createAnomalyGateway} from '../src/modules/operations/anomaly-client';
import {AccessFailure} from '../src/modules/administration/workflow';
import {RequestNotSentError} from '../src/api/client';

const source = {kind: 'order' as const, id: '9007199254741001', label: 'Bagian pesanan'};
const example = () => ({id: '9007199254741002', sourceKind: 'order', sourceId: source.id, sourceHash: 'a'.repeat(64), ruleVersion: 'operational-anomaly-v1',
  policyVersion: null, policyRevision: '1', recordedAt: '2026-10-08T10:00:00Z', current: true, disposition: null,
  signals: [{category: 'quantity', code: 'quantity_limit', state: 'policy_needed', reference: 'SKU/1', observed: '12', expected: null, policyVersion: null, explanation: 'Batas tinjauan belum tersedia.'}]});

test('policy absence is explicit, identifiers remain lossless and malformed explanations fail closed', () => {
  assert.equal(parseAnomalyEvaluation(example()).sourceId, source.id);
  for (const wrong of [{...example(), id: 9007199254741002}, {...example(), sourceHash: 'short'}, {...example(), extra: true},
    {...example(), signals: [{...example().signals[0], state: 'within_policy'}]},
    {...example(), signals: [{...example().signals[0], expected: '10'}]}])
    assert.throws(() => parseAnomalyEvaluation(wrong), (error: unknown) => error instanceof AccessFailure && error.code === 'anomaly_response_invalid');
});

test('refresh reads persisted latest result without sending a business mutation', async () => {
  const calls: {url: string; method: string; actor: string | null}[] = [];
  const gateway = createAnomalyGateway(async (input, init) => {
    calls.push({url: String(input), method: init?.method ?? 'GET', actor: new Headers(init?.headers).get('X-Expected-Actor-Id')});
    return new Response(JSON.stringify(example()), {headers: {'Content-Type': 'application/json'}});
  });
  const value = await gateway.latest(source, {actorId: '44', isCurrent: () => true});
  assert.equal(value?.id, example().id); assert.equal(calls.length, 1); assert.equal(calls[0].method, 'GET'); assert.equal(calls[0].actor, '44');
  assert.match(calls[0].url, /sourceId=9007199254741001$/);
});

test('stale views and stale evaluations never dispatch review commands', async () => {
  let count = 0; const gateway = createAnomalyGateway(async () => {count++; throw new Error('must not call');});
  await assert.rejects(gateway.latest(source, {actorId: '44', isCurrent: () => false}), (error: unknown) => error instanceof RequestNotSentError);
  await assert.rejects(gateway.review({...parseAnomalyEvaluation(example()), current: false}, 'requires_action', 'Periksa sumber', 'review-1', {actorId: '44', isCurrent: () => true}),
    (error: unknown) => error instanceof RequestNotSentError && error.code === 'anomaly_source_changed');
  assert.equal(count, 0);
});

test('a response for a different source is never shown in the current selection', async () => {
  const gateway = createAnomalyGateway(async () => new Response(JSON.stringify({...example(), sourceId: '99'})));
  await assert.rejects(gateway.latest(source, {actorId: '44', isCurrent: () => true}), (error: unknown) => error instanceof AccessFailure && error.code === 'anomaly_response_invalid');
});

test('lost command response is not retried automatically; explicit retry carries the same actor, CSRF and idempotency key', async () => {
  const sent: Headers[] = [];
  const gateway = createAnomalyGateway(async (input, init) => {
    if (input instanceof Request) return new Response(JSON.stringify({requestToken: 'test-csrf'}));
    sent.push(new Headers(init?.headers)); if (sent.length === 1) throw new TypeError('response lost');
    return new Response(JSON.stringify(example()));
  }, 'http://localhost/api/v1/identity/csrf');
  const context = {actorId: '44', isCurrent: () => true};
  await assert.rejects(gateway.evaluate(source, 'Periksa jumlah', 'stable-review-key', context)); assert.equal(sent.length, 1);
  await gateway.evaluate(source, 'Periksa jumlah', 'stable-review-key', context);
  assert.equal(sent.length, 2); for (const headers of sent) {
    assert.equal(headers.get('Idempotency-Key'), 'stable-review-key'); assert.equal(headers.get('X-Expected-Actor-Id'), '44'); assert.equal(headers.get('X-CSRF-Token'), 'test-csrf');
  }
});

test('session changes while CSRF is fetched stop the command before dispatch', async () => {
  let current = true, posts = 0;
  const gateway = createAnomalyGateway(async (input) => {
    if (input instanceof Request) {current = false; return new Response(JSON.stringify({requestToken: 'csrf'}));}
    posts++; throw new Error('unexpected command');
  }, 'http://localhost/api/v1/identity/csrf');
  await assert.rejects(gateway.evaluate(source, 'Periksa jumlah', 'stable-key', {actorId: '44', isCurrent: () => current}),
    (error: unknown) => error instanceof RequestNotSentError && error.code === 'anomaly_view_stale');
  assert.equal(posts, 0);
});

test('session change after command dispatch is an unknown outcome, never a not-sent refusal', async () => {
  let current = true, posts = 0;
  const gateway = createAnomalyGateway(async (input) => {
    if (input instanceof Request) return new Response(JSON.stringify({requestToken: 'csrf'}));
    posts++; current = false; return new Response(JSON.stringify(example()));
  }, 'http://localhost/api/v1/identity/csrf');
  await assert.rejects(gateway.evaluate(source, 'Periksa jumlah', 'original-key', {actorId: '44', isCurrent: () => current}),
    (error: unknown) => error instanceof AccessFailure && error.code === 'anomaly_outcome_unknown' && !(error instanceof RequestNotSentError));
  assert.equal(posts, 1);
});

const policyInput = () => ({sourceKind: 'order', sourceId: source.id, expectedRevision: '1', reason: 'Referensi penetapan telah diperiksa',
  policy: {version: 'synthetic-v1', approvalReference: 'SYNTHETIC-TEST-ONLY', maximumPackages: '3', maximumUnitPrice: null, currency: null, maximumRefund: null}});

test('policy inputs require explicit versions and valid exact quantities and money without supplying absent thresholds', () => {
  const parsed = parseAnomalyPolicyAttempt(policyInput());
  assert.equal(parsed.policy.maximumUnitPrice, null); assert.equal(parsed.policy.maximumRefund, null);
  for (const changes of [{version: ''}, {approvalReference: ''}, {maximumPackages: 'not-a-number'}, {maximumPackages: '0'},
    {maximumPackages: '1000001'}, {maximumUnitPrice: 'NaN', currency: 'IDR'}, {maximumUnitPrice: '1.001', currency: 'IDR'},
    {maximumUnitPrice: '1.00'}, {currency: 'IDR'}, {maximumRefund: '0', currency: 'IDR'}]) {
    assert.throws(() => parseAnomalyPolicyAttempt({...policyInput(), policy: {...policyInput().policy, ...changes}}),
      (error: unknown) => error instanceof RequestNotSentError && error.code === 'anomaly_policy_invalid');
  }
  assert.throws(() => parseAnomalyEvaluation({...example(), id: 'invalid'}), (error: unknown) => error instanceof AccessFailure);
});

test('configuration response loss retains one command and original-key receipt recovery performs only a scoped read', async () => {
  let posts = 0, reads = 0;
  const gateway = createAnomalyGateway(async (input, init) => {
    if (input instanceof Request) return new Response(JSON.stringify({requestToken: 'csrf'}));
    const headers = new Headers(init?.headers); assert.equal(headers.get('X-Expected-Actor-Id'), '44');
    if (init?.method === 'POST') {
      posts++; assert.equal(headers.get('Idempotency-Key'), 'original-policy-key'); assert.equal(headers.get('X-CSRF-Token'), 'csrf');
      assert.deepEqual(JSON.parse(String(init.body)), policyInput()); throw new TypeError('response lost after commit');
    }
    reads++; assert.equal(headers.get('Idempotency-Key'), null);
    assert.match(String(input), /policy\/receipt\?sourceKind=order&sourceId=9007199254741001&key=original-policy-key$/);
    return new Response(JSON.stringify({status: 'confirmed', policyVersion: 'synthetic-v1'}));
  }, 'http://localhost/api/v1/identity/csrf');
  const context = {actorId: '44', isCurrent: () => true};
  await assert.rejects(gateway.configure(parseAnomalyPolicyAttempt(policyInput()), 'original-policy-key', context));
  assert.equal(posts, 1);
  assert.deepEqual(await gateway.recoverPolicy(source, 'original-policy-key', context), {status: 'confirmed', policyVersion: 'synthetic-v1'});
  assert.equal(posts, 1); assert.equal(reads, 1);
});

test('policy recovery rejects contradictory receipts and stale context without a replacement write', async () => {
  let reads = 0;
  const gateway = createAnomalyGateway(async () => {reads++; return new Response(JSON.stringify({status: 'confirmed', policyVersion: null}));});
  await assert.rejects(gateway.recoverPolicy(source, 'original-policy-key', {actorId: '44', isCurrent: () => true}),
    (error: unknown) => error instanceof AccessFailure && error.code === 'anomaly_response_invalid');
  await assert.rejects(gateway.recoverPolicy(source, 'original-policy-key', {actorId: '44', isCurrent: () => false}),
    (error: unknown) => error instanceof RequestNotSentError);
  assert.equal(reads, 1);
});

test('a refusal after an uncertain earlier policy attempt never discards its original recovery reference', () => {
  for (const status of [400, 401, 403, 409]) {
    const refusal = new AccessFailure(status, 'scope_denied');
    assert.equal(retainAnomalyPolicyReference(refusal, false), false);
    assert.equal(retainAnomalyPolicyReference(refusal, true), true);
  }
  assert.equal(retainAnomalyPolicyReference(new AccessFailure(409, 'idempotency_key_reused'), false), true);
  assert.equal(retainAnomalyPolicyReference(new TypeError('response lost'), false), true);
});

test('51 sources remain reachable through bounded stable cursor pages and repeated or invented cursors fail closed', async () => {
  const items = Array.from({length: 51}, (_, index) => ({kind: 'order', id: String(151 - index), label: 'Bagian pesanan #' + (151 - index)}));
  const adjustment = {category: 'adjustment', code: 'adjustment_source_unavailable', state: 'unavailable', reference: 'adjustment',
    observed: null, expected: null, policyVersion: null, explanation: 'Sumber belum tersedia.'};
  const calls: string[] = [];
  const gateway = createAnomalyGateway(async input => {
    calls.push(String(input)); const second = String(input).includes('&before=102');
    return new Response(JSON.stringify({items: second ? items.slice(50) : items.slice(0, 50), nextCursor: second ? null : '102', adjustment}));
  });
  const context = {actorId: '44', isCurrent: () => true};
  const first = await gateway.choices('order', context), second = await gateway.choices('order', context, first.nextCursor);
  assert.equal(first.items.length, 50); assert.equal(second.items.length, 1); assert.equal(second.nextCursor, null);
  assert.equal(new Set([...first.items, ...second.items].map(item => item.id)).size, 51);
  assert.equal(calls[1], '/api/v1/operations/anomalies/sources?kind=order&before=102');
  for (const bad of [{items: items.slice(0, 50), nextCursor: '999', adjustment},
    {items: [items[0], items[0]], nextCursor: null, adjustment}, {items: [items[0]], nextCursor: null, adjustment}]) {
    const invalid = createAnomalyGateway(async () => new Response(JSON.stringify(bad)));
    await assert.rejects(invalid.choices('order', context, '102'), (error: unknown) => error instanceof AccessFailure);
  }
});
