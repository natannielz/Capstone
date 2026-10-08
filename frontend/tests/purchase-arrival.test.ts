import {test} from 'node:test';
import assert from 'node:assert/strict';
import {arrivalUtc, createPurchaseArrivalGateway, parseArrivalPending, parsePurchaseArrival} from '../src/modules/procurement/purchase-arrival-client';
import {AccessFailure} from '../src/modules/administration/workflow';
import {RequestNotSentError} from '../src/api/client';
import {arrivalJournalPrefix, PurchaseArrivalJournal} from '../src/modules/procurement/purchase-arrival-journal';

const actorId = '9007199254740993', orderId = '9007199254740994', skuId = '500';
const input = {orderId, orderVersion: '1', approvalId: '90', skuId, definitionId: '501', expectedRevision: '1',
  expectedAt: '2026-10-11T03:00:00.000Z', confirmationReference: 'Supplier confirms 00017'};
const empty = {orderId, orderVersion: '1', approvalId: '90', skuId, definitionId: '501', revision: '1', orderedPackages: 3,
  state: 'unrecorded' as const, proposalId: null, proposedAt: null, expectedAt: null, confirmationReference: null, reason: null, recordedAt: null};
const shown = {current: empty, input, binding: 'a'.repeat(64), decision: null};
const saved = {eventId: '701', eventHash: 'b'.repeat(64), orderId, skuId, revision: '2', state: 'pending'};
const reference = {actorId, orderId, skuId, action: 'propose' as const, key: 'arrival.propose.00000000-0000-4000-8000-000000000001'};
const context = {actorId, isCurrent: () => true};
const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), {status, headers: {'Content-Type': 'application/json'}});
function referenceStorage() {
  const values = new Map<string, string>();
  return {get length() {return values.size;}, key: (index: number) => [...values.keys()][index] ?? null,
    getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => {values.set(key, value);},
    removeItem: (key: string) => {values.delete(key);}};
}

test('durable references survive a new journal and confirmation never erases another tab operation', () => {
  const storage = referenceStorage(), firstTab = new PurchaseArrivalJournal(() => storage);
  const other = {...reference, key: 'arrival.propose.00000000-0000-4000-8000-000000000002'};
  firstTab.remember(reference); new PurchaseArrivalJournal(() => storage).remember(other);
  const restarted = new PurchaseArrivalJournal(() => storage);
  assert.deepEqual(restarted.list(actorId), [reference, other]);
  assert.deepEqual(restarted.list('999'), []);
  restarted.confirmed(reference); assert.deepEqual(firstTab.list(actorId), [other]);
  assert.deepEqual(Object.keys(JSON.parse(storage.getItem(storage.key(0)!)!)).sort(), ['action', 'actorId', 'key', 'orderId', 'skuId']);
});

test('corrupt or mismatched recovery data is retained and cannot authorize overwriting or clearing it', () => {
  const storage = referenceStorage(), journal = new PurchaseArrivalJournal(() => storage);
  journal.remember(reference);
  const key = arrivalJournalPrefix(actorId) + reference.key;
  storage.setItem(key, JSON.stringify({...reference, orderId: '123'}));
  assert.throws(() => journal.remember(reference)); assert.throws(() => journal.confirmed(reference));
  assert.equal(JSON.parse(storage.getItem(key)!).orderId, '123');
  storage.setItem(key, JSON.stringify({...reference, actorId: '999'}));
  assert.throws(() => journal.list(actorId)); assert.throws(() => journal.confirmed(reference));
  assert.equal(storage.length, 1);
});

test('unavailable or non-retaining local storage refuses a new command reference', () => {
  assert.throws(() => new PurchaseArrivalJournal(() => {throw new Error('Storage denied');}).remember(reference));
  const storage = referenceStorage(); storage.setItem = () => {};
  assert.throws(() => new PurchaseArrivalJournal(() => storage).remember(reference));
});
function harness(answer: (path: string, init: RequestInit) => Response | Promise<Response>) {
  const calls: {path: string; init: RequestInit}[] = [];
  const fetcher: typeof fetch = async (url, init) => {
    const path = url instanceof Request ? new URL(url.url, 'https://test.invalid').pathname : String(url);
    if (path === '/csrf') return json({requestToken: 'csrf-private'});
    calls.push({path, init: init ?? {}}); return answer(path, init ?? {});
  };
  return {gateway: createPurchaseArrivalGateway(fetcher, 'https://test.invalid/csrf'), calls};
}
test('arrival times use explicit WIB and reject impossible calendar values', () => {
  assert.equal(arrivalUtc('2026-10-11T10:00'), input.expectedAt);
  for (const invalid of ['2026-02-30T10:00', '2026-10-11T25:00', '2026-10-11', '2026-10-11T10:00\n']) assert.throws(() => arrivalUtc(invalid), RequestNotSentError);
});
test('unreviewed or withdrawn dates cannot claim approved incoming stock and unknown fields are rejected', () => {
  assert.equal(parsePurchaseArrival(empty).expectedAt, null);
  assert.throws(() => parsePurchaseArrival({...empty, expectedAt: input.expectedAt}), AccessFailure);
  assert.throws(() => parsePurchaseArrival({...empty, paidAmount: '10000.00'}), AccessFailure);
  assert.throws(() => parsePurchaseArrival({...empty, state: 'pending'}), AccessFailure);
});
test('preview normalizes equivalent UTC formats and binds exact order package and revision', async () => {
  const h = harness(() => json({...shown, input: {...input, expectedAt: '2026-10-11T03:00:00+00:00'}}));
  const value = await h.gateway.preview(input, context);
  assert.equal(value.input.expectedAt, input.expectedAt); assert.equal(h.calls.length, 1);
  const headers = h.calls[0].init.headers as Record<string, string>;
  assert.equal(headers['X-Expected-Actor-Id'], actorId); assert.equal(headers['X-CSRF-Token'], 'csrf-private');
  assert.equal(h.calls[0].init.credentials, 'same-origin'); assert.equal(h.calls[0].init.cache, 'no-store');
  const bad = harness(() => json({...shown, input: {...input, definitionId: '999'}}));
  await assert.rejects(bad.gateway.preview(input, context), AccessFailure);
});
test('one immutable proposal is sent once and successful result must name the exact source', async () => {
  const h = harness(() => json(saved)); await h.gateway.submit(shown, 'Reviewed supplier confirmation', reference.key, context);
  assert.equal(h.calls.length, 1); assert.equal(h.calls[0].path, '/api/v1/procurement/arrivals/proposals');
  assert.equal((h.calls[0].init.headers as Record<string, string>)['Idempotency-Key'], reference.key);
  assert.deepEqual(JSON.parse(h.calls[0].init.body as string).input, input);
  const wrong = harness(() => json({...saved, orderId: '12'}));
  await assert.rejects(wrong.gateway.submit(shown, 'Reviewed supplier confirmation', reference.key, context), AccessFailure);
});
test('lost response has no retry and recovery uses original account key with lookup only', async () => {
  const h = harness(path => {if (path.endsWith('/proposals')) throw new TypeError('network response lost'); return json({status: 'confirmed', result: saved});});
  await assert.rejects(h.gateway.submit(shown, 'Reviewed supplier confirmation', reference.key, context), TypeError);
  assert.equal(h.calls.length, 1);
  assert.equal((await h.gateway.recover(reference, context)).result?.eventId, '701');
  assert.equal(h.calls.length, 2); assert.ok(h.calls[1].path.endsWith('/operations/query'));
  assert.deepEqual(JSON.parse(h.calls[1].init.body as string), {purpose: 'operation-recovery', action: 'propose', key: reference.key});
  assert.throws(() => parseArrivalPending(reference, '7'), RequestNotSentError);
  assert.throws(() => parseArrivalPending({...reference, confirmationReference: 'private'}, actorId), RequestNotSentError);
});
test('unknown recovery is retained and forged confirmed results are refused', async () => {
  const h = harness(() => json({status: 'not_confirmed', result: null}));
  assert.equal((await h.gateway.recover(reference, context)).status, 'not_confirmed');
  const wrong = harness(() => json({status: 'confirmed', result: {...saved, skuId: '501'}}));
  await assert.rejects(wrong.gateway.recover(reference, context), AccessFailure);
});
test('stale identity during CSRF acquisition prevents business request dispatch', async () => {
  let current = true, writes = 0;
  const gateway = createPurchaseArrivalGateway(async url => {
    if (url instanceof Request) {current = false; return json({requestToken: 'fresh'});} writes++; return json(saved);
  }, 'https://test.invalid/csrf');
  await assert.rejects(gateway.submit(shown, 'Reviewed supplier confirmation', reference.key, {actorId, isCurrent: () => current}), RequestNotSentError);
  assert.equal(writes, 0);
});
test('decision preserves exact source preview and sends expected revision without automatic retries', async () => {
  const pendingView = {...empty, state: 'pending', revision: '2', proposalId: '701', proposedAt: input.expectedAt,
    confirmationReference: input.confirmationReference, reason: 'Reviewed supplier confirmation', recordedAt: '2026-10-08T03:00:00.000Z'};
  const h = harness(path => path.endsWith('/query') ? json({current: pendingView, input, binding: 'a'.repeat(64), decision: 'approve'}) : json({...saved, eventId: '702', revision: '3', state: 'approved'}));
  const preview = await h.gateway.decision(parsePurchaseArrival(pendingView), 'approve', context);
  await h.gateway.submit(preview, 'Independently verified', 'arrival.approve.key', context);
  assert.equal((h.calls[1].init.headers as Record<string, string>)['If-Match'], '"2"');
  assert.equal(h.calls.length, 2);
});
test('identity change after dispatch preserves an uncertain outcome instead of claiming request was not sent', async () => {
  let current = true;
  const h = harness(() => {current = false; return json(saved);});
  await assert.rejects(h.gateway.submit(shown, 'Reviewed supplier confirmation', reference.key, {actorId, isCurrent: () => current}),
    error => error instanceof AccessFailure && error.status === 503 && error.code === 'purchase_arrival_outcome_unknown');
  assert.equal(h.calls.length, 1);
});
