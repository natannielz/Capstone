import test from 'node:test';
import assert from 'node:assert/strict';
import {OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, submitTrackedRequest,
  type LegacyRecoveryAdapter, type OperationRecoveryAdapters} from '../src/modules/administration/operation-journal';
import {operationLabel, recoveryPage} from '../src/modules/administration/operation-recovery-navigation';
import {AccessFailure, MutationLedger} from '../src/modules/administration/workflow';
import {catalogPhotoKey, catalogPhotoOperation, type CatalogPhotoResult} from '../src/modules/catalog/catalog-photo-workflow';
import {catalogPhotoReservationKey, catalogPhotoReserveOperation, type CatalogPhotoReservation} from '../src/modules/catalog/catalog-photo-evidence-client';

class MemoryStorage {
  values = new Map<string, string>();
  get length() { return this.values.size; }
  key(index: number) { return [...this.values.keys()][index] ?? null; }
  getItem(key: string) { return this.values.get(key) ?? null; }
  setItem(key: string, value: string) { this.values.set(key, value); }
  removeItem(key: string) { this.values.delete(key); }
}
const actor = '9007199254740993', sku = '9007199254740995', uuid = 'd92ecbec-a1cf-4b68-a961-e47bd5e11527';
const at = '2026-10-04T12:00:00.000Z';
const associationKey = catalogPhotoKey(sku, uuid), uploadKey = catalogPhotoReservationKey(sku, uuid);
const photo: CatalogPhotoResult = {associationId: '41', skuId: sku, skuRevision: '2', definitionId: '39', definitionVersion: '1', photoRevision: '1', recordedAt: at};
const reservation: CatalogPhotoReservation = {reservationId: '52', revision: '1', expiresAt: '2026-10-04T12:10:00.000Z'};
const legacy: LegacyRecoveryAdapter = {
  async lookup() { assert.fail('Photo recovery must never call the access receipt adapter'); },
  async close() { assert.fail('Photo recovery has no close or resend operation'); },
};
function setup() {
  const storage = new MemoryStorage();
  return {storage, journal: new OperationJournal(() => storage, () => new Date(at))};
}

test('photo journal keeps only original actor, operation, canonical key and time across reload', async () => {
  for (const [operation, key] of [[catalogPhotoOperation, associationKey], [catalogPhotoReserveOperation, uploadKey]]) {
    const {storage, journal} = setup(); let writes = 0;
    await assert.rejects(submitTrackedRequest(new MutationLedger(() => key), journal, actor, 'photo', operation,
      {reason: 'Private reviewer note', reference: {id: '72', sha256: 'a'.repeat(64)}, fileName: 'private-photo.png'}, async original => {
        writes++; assert.equal(original, key); throw new AccessFailure(503, 'commit_outcome_unknown');
      }));
    const reloaded = new OperationJournal(() => storage), [entry] = reloaded.list(actor);
    assert.deepEqual(entry, {actorId: actor, operation, key, createdAt: at});
    assert.deepEqual(Object.keys(entry).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.deepEqual(reloaded.list('7'), []); assert.equal(writes, 1);
    assert.doesNotMatch([...storage.values.values()].join(''), /Private reviewer|sha256|reference|fileName|private-photo/);
  }
});

test('photo journal rejects malformed, cross-operation and oversized source keys before retention', () => {
  const {storage, journal} = setup();
  for (const [operation, key] of [
    [catalogPhotoOperation, uploadKey], [catalogPhotoReserveOperation, associationKey],
    [catalogPhotoOperation, associationKey.replace(sku, '0')], [catalogPhotoOperation, associationKey.replace(sku, '01')],
    [catalogPhotoOperation, associationKey.replace(sku, '9223372036854775808')],
    [catalogPhotoReserveOperation, uploadKey.replace(sku, '0')], [catalogPhotoReserveOperation, uploadKey.replace(sku, '01')],
    [catalogPhotoReserveOperation, uploadKey.replace(sku, '9223372036854775808')],
    [catalogPhotoOperation, associationKey + '\n'], [catalogPhotoReserveOperation, uploadKey + '\n'],
    [catalogPhotoOperation, associationKey.replace(uuid, uuid.toUpperCase())],
    [catalogPhotoReserveOperation, uploadKey.replace(uuid, uuid.replace('-4b68-', '-1b68-'))],
    ['catalog.photo.associate.extra', associationKey], ['evidence.catalog-image.complete', uploadKey],
  ]) assert.throws(() => journal.remember(actor, operation, key), /recovery_storage_unavailable/);
  assert.equal(storage.length, 0);
});

test('photo journal rejects injected private metadata and entries relabelled as another actor', () => {
  for (const mutation of ['privateMetadata', 'actor'] as const) {
    const {storage, journal} = setup(); const entry = journal.remember(actor, catalogPhotoOperation, associationKey);
    const storedKey = storage.key(0)!;
    storage.setItem(storedKey, JSON.stringify(mutation === 'actor' ? {...entry, actorId: '7'} : {...entry, reference: {id: '72'}}));
    assert.throws(() => journal.list(actor), /recovery_storage_unavailable/);
    assert.deepEqual(journal.list('7'), []);
  }
});

test('photo adapters are lookup-only and missing adapters navigate to the catalog without legacy fallback', async () => {
  const {journal} = setup();
  const adapters: OperationRecoveryAdapters = {
    'catalog.photo': {lookup: async () => null}, 'evidence.catalog-image': {lookup: async () => null},
  };
  for (const [operation, key] of [[catalogPhotoOperation, associationKey], [catalogPhotoReserveOperation, uploadKey]]) {
    assert.deepEqual(operationRecoveryCapabilities(operation), {canCheck: false, canClose: false, unavailable: 'adapter'});
    assert.deepEqual(operationRecoveryCapabilities(operation, adapters), {canCheck: true, canClose: false, unavailable: null});
    assert.deepEqual(recoveryPage(operation), {href: '/administration/catalog', label: 'halaman foto katalog'});
    assert.match(operationLabel(operation), /foto/);
    const entry = journal.remember(actor, operation, key);
    await assert.rejects(reconcileOperationRecovery(entry, journal, legacy), /recovery_adapter_unavailable/);
    await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters, 'Close uncertain upload'), /recovery_close_unsupported/);
  }
  assert.equal(journal.list(actor).length, 2);
});

test('null photo lookups retain uncertainty while genuine parsed receipts clear only their original references', async () => {
  const {journal} = setup();
  const association = journal.remember(actor, catalogPhotoOperation, associationKey);
  const upload = journal.remember(actor, catalogPhotoReserveOperation, uploadKey);
  let confirmed = false, lookups = 0;
  const adapters: OperationRecoveryAdapters = {
    'catalog.photo': {lookup: async entry => { lookups++; assert.deepEqual(entry, association); return confirmed ? {state: 'committed', result: photo} : null; }},
    'evidence.catalog-image': {lookup: async entry => { lookups++; assert.deepEqual(entry, upload); return confirmed ? {state: 'committed', result: reservation} : null; }},
  };
  for (const entry of [association, upload]) assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapters), {state: 'unconfirmed'});
  assert.equal(journal.list(actor).length, 2); confirmed = true;
  assert.deepEqual(await reconcileOperationRecovery(association, journal, legacy, adapters), {state: 'committed', result: {id: photo.associationId, version: photo.photoRevision}});
  assert.deepEqual(journal.list(actor), [upload]);
  assert.deepEqual(await reconcileOperationRecovery(upload, journal, legacy, adapters), {state: 'committed', result: {id: reservation.reservationId, version: '1'}});
  assert.deepEqual(journal.list(actor), []); assert.equal(lookups, 4);
});

test('foreign SKU, malformed results and unexpected metadata cannot resolve the pending photo reference', async () => {
  const invalidPhotoReceipts: unknown[] = [
    {state: 'committed', result: {...photo, skuId: '7'}},
    {state: 'committed', result: {...photo, associationId: 41}},
    {state: 'committed', result: {...photo, photoRevision: '0'}},
    {state: 'committed', result: {...photo, photoRevision: '257'}},
    {state: 'committed', result: {...photo, actorId: '7'}},
    {state: 'committed', result: photo, actorId: '7'}, {state: 'closed', result: null},
  ];
  for (const receipt of invalidPhotoReceipts) {
    const {journal} = setup(); const entry = journal.remember(actor, catalogPhotoOperation, associationKey);
    const adapters = {'catalog.photo': {lookup: async () => receipt}} as unknown as OperationRecoveryAdapters;
    await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters));
    assert.deepEqual(journal.list(actor), [entry]);
  }
  for (const invalid of [{...reservation, revision: '2'}, {...reservation, reservationId: 52},
    {...reservation, reservationId: '0'}, {...reservation, expiresAt: 'not-a-date'}, {...reservation, privatePath: 'private/photo.png'}]) {
    const {journal} = setup(); const entry = journal.remember(actor, catalogPhotoReserveOperation, uploadKey);
    const adapters = {'evidence.catalog-image': {lookup: async () => ({state: 'committed', result: invalid})}} as unknown as OperationRecoveryAdapters;
    await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters));
    assert.deepEqual(journal.list(actor), [entry]);
  }
});

test('changed actor or view rejects photo lookup before sending and after its reply without clearing another account', async () => {
  const {journal} = setup(); const entry = journal.remember(actor, catalogPhotoOperation, associationKey);
  const other = journal.remember('7', catalogPhotoOperation, catalogPhotoKey('8', uuid));
  let activeActor = '7', calls = 0;
  const current = () => activeActor === entry.actorId;
  const adapters: OperationRecoveryAdapters = {'catalog.photo': {lookup: async () => {
    calls++; activeActor = '7'; return {state: 'committed', result: photo};
  }}};
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters, undefined, current), /recovery_request_stale/);
  assert.equal(calls, 0); activeActor = actor;
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters, undefined, current), /recovery_request_stale/);
  assert.equal(calls, 1);
  const denied: OperationRecoveryAdapters = {'catalog.photo': {lookup: async () => { throw new AccessFailure(409, 'actor_changed'); }}};
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, denied), /actor_changed/);
  assert.deepEqual(journal.list(actor), [entry]); assert.deepEqual(journal.list('7'), [other]);
});
