import {test} from 'node:test';
import assert from 'node:assert/strict';
import {RequestNotSentError} from '../src/api/client';
import {AccessFailure} from '../src/modules/administration/workflow';
import {createCourierAssignmentGateway, currentCourierMatchesDeparture, matchCourierResult, parseCourierPending,
  parseCourierTask, type CourierPending, type CourierTask} from '../src/modules/fulfillment/courier-assignment-client';
import {CourierAssignmentJournal, courierJournalPrefix} from '../src/modules/fulfillment/courier-assignment-journal';

const at = '2026-10-09T01:00:00Z', later = '2026-10-09T02:00:00Z', latest = '2026-10-09T03:00:00Z';
const initial: CourierTask = {shipmentId: '9007199254741200', orderId: '9007199254741100', suborderId: '9007199254741101', numberEntity: '100',
  numberChannel: '103', numberYear: '2026', number: '91', departureCourierId: '501', departureCourierName: 'Kurir A',
  currentAssignment: {revision: '1', courierId: '501', courierName: 'Kurir A', effectiveAt: at}, received: false, canHandover: true, history: []};
const first = {id: '9007199254741300', shipmentId: initial.shipmentId, revision: '2', previousCourierId: '501', courierId: '502', courierName: 'Kurir B',
  recordedAt: later, reason: 'Tugas diteruskan pada pergantian giliran.'};
const second = {...first, id: '9007199254741400', revision: '3', previousCourierId: '502', courierId: '501', courierName: 'Kurir A', recordedAt: latest, reason: 'Kembali kepada kurir A.'};
const current: CourierTask = {...initial, currentAssignment: {revision: '3', courierId: '501', courierName: 'Kurir A', effectiveAt: latest}, history: [first, second]};
const ref: CourierPending = {actorId: '701', shipmentId: initial.shipmentId, expectedRevision: '1', previousCourierId: '501', courierId: '502', key: 'handover-original-1', createdAt: at};
const shown = {task: initial, courierId: '502', courierName: 'Kurir B', review: 'a'.repeat(64)};
const context = {actorId: ref.actorId, isCurrent: () => true};
function memory() {
  const values = new Map<string, string>();
  return {values, get length() {return values.size;}, key: (i: number) => [...values.keys()][i] ?? null,
    getItem: (k: string) => values.get(k) ?? null, setItem: (k: string, v: string) => {values.set(k, v);}, removeItem: (k: string) => {values.delete(k);}};
}
type Call = {url: string; headers: Headers; body: Record<string, unknown>};
function transport(reply: (call: Call) => Promise<Response> | Response) {
  const calls: Call[] = [];
  const fetcher = async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = input instanceof Request ? input.url : input.toString();
    if (url.endsWith('/identity/csrf')) return Response.json({requestToken: 'csrf-test'});
    const call = {url, headers: new Headers(init?.headers), body: JSON.parse(String(init?.body)) as Record<string, unknown>};
    calls.push(call); return reply(call);
  };
  return {calls, gateway: createCourierAssignmentGateway(fetcher as typeof fetch, 'http://localhost/api/v1/identity/csrf')};
}
test('task parser retains departure provenance and exact complete A to B to A responsibility history', () => {
  assert.deepEqual(parseCourierTask(initial), initial); assert.deepEqual(parseCourierTask(current), current);
  assert.equal(currentCourierMatchesDeparture(initial.currentAssignment, '501', 'Kurir A', at), true);
  assert.equal(currentCourierMatchesDeparture(initial.currentAssignment, '502', 'Kurir A', at), false);
  assert.equal(currentCourierMatchesDeparture(current.currentAssignment, '501', 'Kurir A', at), true);
  assert.equal(currentCourierMatchesDeparture({...current.currentAssignment, effectiveAt: '2026-10-08T01:00:00Z'}, '501', 'Kurir A', at), false);
  for (const value of [
    {...current, history: [second]}, {...current, history: [first, {...second, previousCourierId: '503'}]},
    {...current, history: [first, {...second, id: first.id}]}, {...current, history: [first, {...second, shipmentId: '99'}]},
    {...current, history: [first, {...second, revision: '4'}]}, {...current, currentAssignment: {...current.currentAssignment, courierName: 'Other'}},
    {...current, history: [first, {...second, courierId: second.previousCourierId}]}, {...current, received: true, canHandover: true},
    {...initial, privateEvidence: 'must not enter task'}, {...initial, currentAssignment: {...initial.currentAssignment, revision: 'no'}}
  ]) assert.throws(() => parseCourierTask(value));
  assert.equal(parseCourierTask({...current, received: true, canHandover: false}).received, true);
});
test('current assignment is bounded by the actual owner contract while original outcome recovery stays immutable', () => {
  assert.deepEqual(matchCourierResult(first, ref), first);
  for (const patch of [{shipmentId: '98'}, {courierId: '503'}, {previousCourierId: '504'}, {revision: '3'}, {id: 42}, {privateBank: 'hidden'}])
    assert.throws(() => matchCourierResult({...first, ...patch}, ref));
  assert.throws(() => parseCourierTask({...initial, currentAssignment: {...initial.currentAssignment, revision: '1002'}}));
  assert.throws(() => parseCourierPending({...ref, actorId: '702'}, ref.actorId));
  assert.throws(() => parseCourierPending({...ref, key: 'key\n'}, ref.actorId));
});
test('list and courier pages preserve exclusive descending IDs and strict task disclosure', async () => {
  const good = transport(() => Response.json({items: [initial], hasMore: false}));
  assert.equal((await good.gateway.list(null, context)).items[0].shipmentId, initial.shipmentId);
  assert.deepEqual(good.calls[0].body, {purpose: 'courier-assignment', beforeId: null, limit: 25});
  assert.equal(good.calls[0].headers.get('X-Expected-Actor-Id'), ref.actorId);
  assert.equal(good.calls[0].headers.get('X-CSRF-Token'), 'csrf-test');
  await assert.rejects(() => good.gateway.list(initial.shipmentId, context));
  await assert.rejects(() => transport(() => Response.json({items: [initial, initial], hasMore: false})).gateway.list(null, context));
  await assert.rejects(() => transport(() => Response.json({items: [initial], hasMore: true})).gateway.list(null, context));
  const couriers = transport(() => Response.json({items: [{userId: '503', displayName: 'C'}, {userId: '502', displayName: 'B'}], hasMore: false}));
  assert.equal((await couriers.gateway.couriers(initial.shipmentId, null, context)).items.length, 2);
  await assert.rejects(() => couriers.gateway.couriers(initial.shipmentId, '503', context));
});
test('preview and commit bind exact task revision, courier, reason and original operation key', async () => {
  const server = transport(call => Response.json(call.url.endsWith('/preview/query') ? shown : first));
  assert.deepEqual(await server.gateway.preview(initial, '502', context), shown);
  assert.deepEqual(server.calls[0].body, {purpose: 'courier-assignment', expectedRevision: '1', courierId: '502'});
  assert.deepEqual(await server.gateway.handover(shown, first.reason, ref, context), first);
  const commit = server.calls[1]; assert.equal(commit.headers.get('If-Match'), '"1"'); assert.equal(commit.headers.get('Idempotency-Key'), ref.key);
  assert.deepEqual(commit.body, {expectedRevision: '1', courierId: '502', review: shown.review, reason: first.reason});
  await assert.rejects(() => server.gateway.handover(shown, first.reason, {...ref, previousCourierId: '99'}, context), RequestNotSentError);
  await assert.rejects(() => server.gateway.preview(initial, initial.currentAssignment.courierId, context), RequestNotSentError);
  await assert.rejects(() => transport(() => Response.json({...shown, task: {...initial, received: true, canHandover: false}})).gateway.preview(initial, '502', context));
  await assert.rejects(() => transport(() => Response.json({...shown, courierId: '503'})).gateway.preview(initial, '502', context));
  await assert.rejects(() => transport(() => Response.json({...first, reason: 'different'})).gateway.handover(shown, first.reason, ref, context));
});
test('stale preflight sends nothing, but an account change after transport preserves unknown outcome', async () => {
  const pre = transport(() => Response.json(first));
  await assert.rejects(() => pre.gateway.handover(shown, first.reason, ref, {...context, isCurrent: () => false}), RequestNotSentError);
  assert.equal(pre.calls.length, 0);
  let live = true; const post = transport(() => {live = false; return Response.json(first);});
  await assert.rejects(() => post.gateway.handover(shown, first.reason, ref, {...context, isCurrent: () => live}),
    error => error instanceof AccessFailure && error.code === 'courier_assignment_outcome_unknown');
  assert.equal(post.calls.length, 1);
});
test('separate durable references survive browser instances and cannot overwrite another tab or account', () => {
  const store = memory(), firstTab = new CourierAssignmentJournal(() => store), secondTab = new CourierAssignmentJournal(() => store);
  const other = {...ref, key: 'handover-other', shipmentId: '9007199254741500'};
  firstTab.remember(ref); secondTab.remember(other);
  assert.equal(new CourierAssignmentJournal(() => store).list(ref.actorId).length, 2);
  assert.deepEqual(firstTab.list('702'), []);
  assert.throws(() => secondTab.remember({...ref, courierId: '503'}));
  firstTab.confirmed(ref); assert.deepEqual(secondTab.list(ref.actorId), [other]);
  assert.equal([...store.values.values()].some(value => value.includes(first.reason)), false);
  store.setItem(courierJournalPrefix(ref.actorId) + 'wrong-key', JSON.stringify(ref));
  assert.throws(() => firstTab.list(ref.actorId));
});
test('denied, unavailable, not-confirmed and forged recovery results retain the exact pending reference', async () => {
  const store = memory(), journal = new CourierAssignmentJournal(() => store); journal.remember(ref);
  for (const status of [403, 409, 503]) {
    await assert.rejects(() => transport(() => Response.json({code: 'refused'}, {status})).gateway.recover(ref, context));
    assert.deepEqual(journal.list(ref.actorId), [ref]);
  }
  assert.equal(await transport(() => Response.json({status: 'not_confirmed', result: null})).gateway.recover(ref, context), null);
  assert.deepEqual(journal.list(ref.actorId), [ref]);
  await assert.rejects(() => transport(() => Response.json({status: 'committed', result: second})).gateway.recover(ref, context));
  const saved = await transport(() => Response.json({status: 'committed', result: first})).gateway.recover(ref, context);
  assert.deepEqual(saved, first); journal.confirmed(ref); assert.deepEqual(journal.list(ref.actorId), []);
});
test('storage refusal is detected before a recoverable reference can be considered retained', () => {
  const store = memory(), missing = new CourierAssignmentJournal(() => ({...store, setItem() {throw new Error('blocked');}}));
  assert.throws(() => missing.remember(ref));
  const ignored = new CourierAssignmentJournal(() => ({...store, setItem() {}}));
  assert.throws(() => ignored.remember(ref));
  assert.equal(store.values.size, 0);
});
