import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createDeliveryAttemptGateway, jakartaVisitInput, jakartaVisitTime, parseDeliveryAttempt, prepareDeliveryAttempt } from '../src/modules/fulfillment/delivery-attempt-client';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';

const actor = '9007199254740993', shipmentId = '9007199254740997', time = '2026-09-30T01:00:00.123Z';
const preview = { shipmentId, sequence: '0', received: false, dispatchedAt: time, serverTime: time, lastOccurredAt: null };
const attempt = { id: '9007199254740995', shipmentId, sequence: '1', outcome: 'recipient_unavailable', reason: 'Cabang belum buka', occurredAt: time, recordedAt: time, revokedQrCount: 1 };
test('failed visit preserves exact IDs, explicit outcome, millisecond WIB and frozen command', () => {
  assert.deepEqual(parseDeliveryAttempt(attempt), attempt);
  assert.equal(jakartaVisitInput(time), '2026-09-30T08:00:00.123'); assert.equal(jakartaVisitTime(jakartaVisitInput(time)), time);
  const command = prepareDeliveryAttempt(actor, preview, attempt.outcome, time, attempt.reason);
  assert.equal(command.shipmentId, shipmentId); assert.equal(command.expectedSequence, '0'); assert.ok(Object.isFrozen(command)); assert.ok(Object.isFrozen(command.body));
  for (const changed of [{ ...attempt, shipmentId: 9007199254740997 }, { ...attempt, sequence: '0' }, { ...attempt, sequence: '1001' }, { ...attempt, outcome: 'delivered' }, { ...attempt, revokedQrCount: 21 }, { ...attempt, recordedAt: '2026-09-29T01:00:00Z' }, { ...attempt, reason: 'hello\nworld' }, { ...attempt, token: 'secret' }]) assert.throws(() => parseDeliveryAttempt(changed));
  for (const changed of [{ ...preview, received: true }, { ...preview, sequence: '1000', lastOccurredAt: time }]) assert.throws(() => prepareDeliveryAttempt(actor, changed, attempt.outcome, time, attempt.reason));
  assert.throws(() => prepareDeliveryAttempt(actor, preview, 'delivered', time, attempt.reason));
  assert.throws(() => prepareDeliveryAttempt(actor, preview, attempt.outcome, '2026-09-29T01:00:00Z', attempt.reason));
});
test('visit sends one current actor command with CSRF, version and original key; wrong result is uncertain', async () => {
  const requests: Request[] = []; let wrong = false;
  const gateway = createDeliveryAttemptGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
    requests.push(request); assert.equal(request.method, 'POST'); assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf');
    assert.equal(request.headers.get('If-Match'), '"0"'); assert.equal(request.headers.get('Idempotency-Key'), 'visit-key');
    return Response.json({ ...attempt, sequence: wrong ? '2' : '1' });
  } }));
  const command = prepareDeliveryAttempt(actor, preview, attempt.outcome, time, attempt.reason);
  await assert.rejects(() => gateway.record(command, 'visit-key', () => false)); assert.equal(requests.length, 0);
  assert.deepEqual(await gateway.record(command, 'visit-key', () => true), attempt); assert.equal(requests.length, 1);
  assert.deepEqual(await requests[0].clone().json(), command.body);
  wrong = true; await assert.rejects(() => gateway.record(command, 'visit-key', () => true)); assert.equal(requests.length, 2);
});
test('visit recovery cannot close, replay or erase unknown, malformed and stale outcomes', async () => {
  const values = new Map<string, string>(), storage = { get length() { return values.size; }, key: (index: number) => [...values.keys()][index] ?? null,
    getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); }, removeItem: (key: string) => { values.delete(key); } };
  const journal = new OperationJournal(() => storage), entry = journal.remember(actor, 'fulfillment.delivery-attempt.record', 'visit-key');
  const legacy = { lookup: async () => { throw new Error('wrong owner'); }, close: async () => { throw new Error('must not close'); } };
  const absent = { 'fulfillment.delivery-attempt': { lookup: async () => null } };
  assert.equal(recoveryPage(entry.operation).href, '/fulfillment/shipments');
  assert.deepEqual(operationRecoveryCapabilities(entry.operation, absent), { canCheck: true, canClose: false, unavailable: null });
  assert.deepEqual(Object.keys(JSON.parse([...values.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, absent)).state, 'unconfirmed'); assert.equal(values.size, 1);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, absent, 'close'));
  const malformed = { 'fulfillment.delivery-attempt': { lookup: async () => ({ state: 'committed' as const, result: { ...attempt, sequence: '0' } }) } };
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, malformed as never)); assert.equal(values.size, 1);
  let current = true; const late = { 'fulfillment.delivery-attempt': { lookup: async () => { current = false; return { state: 'committed' as const, result: parseDeliveryAttempt(attempt) }; } } };
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, late, undefined, () => current)); assert.equal(values.size, 1);
  const accepted = { 'fulfillment.delivery-attempt': { lookup: async () => ({ state: 'committed' as const, result: parseDeliveryAttempt(attempt) }) } };
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, accepted)).state, 'committed'); assert.equal(values.size, 0);
});
