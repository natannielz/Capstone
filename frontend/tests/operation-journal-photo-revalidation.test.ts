import test from 'node:test';
import assert from 'node:assert/strict';
import {OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, submitTrackedRequest,
  type LegacyRecoveryAdapter, type OperationRecoveryAdapters} from '../src/modules/administration/operation-journal';
import {operationLabel, recoveryPage} from '../src/modules/administration/operation-recovery-navigation';
import {AccessFailure, MutationLedger} from '../src/modules/administration/workflow';
import {catalogPhotoRevalidationOperation, catalogPhotoRevalidationKeySku,
  type CatalogPhotoRevalidationResult} from '../src/modules/catalog/catalog-photo-revalidation-api';

class MemoryStorage {
  values = new Map<string, string>();
  get length() { return this.values.size; }
  key(index: number) { return [...this.values.keys()][index] ?? null; }
  getItem(key: string) { return this.values.get(key) ?? null; }
  setItem(key: string, value: string) { this.values.set(key, value); }
  removeItem(key: string) { this.values.delete(key); }
}
const actor = '9007199254740993', sku = '9007199254740995', uuid = 'd92ecbec-a1cf-4b68-a961-e47bd5e11527';
const at = '2026-10-04T12:00:00.000Z', key = `catalog.photo.revalidate.${sku}.${uuid}`;
const operation = catalogPhotoRevalidationOperation;
const result: CatalogPhotoRevalidationResult = {requestId: '41', skuId: sku, versionRowId: '52', state: 'pending', recordedAt: at};
const legacy: LegacyRecoveryAdapter = {
  async lookup() { assert.fail('Revalidation must never use access receipt recovery'); },
  async close() { assert.fail('Revalidation has no close or resend command'); },
};
function setup() {
  const storage = new MemoryStorage();
  return {storage, journal: new OperationJournal(() => storage, () => new Date(at))};
}

test('uncertain revalidation retains only actor, operation, original key and time across reload', async () => {
  const {storage, journal} = setup(); let writes = 0;
  assert.equal(catalogPhotoRevalidationKeySku(key), sku);
  await assert.rejects(submitTrackedRequest(new MutationLedger(() => key), journal, actor, 'revalidate', operation,
    {reason: 'Private inspection note', reference: {id: '72', sha256: 'a'.repeat(64)}}, async original => {
      writes++; assert.equal(original, key); throw new AccessFailure(503, 'commit_outcome_unknown');
    }));
  const reloaded = new OperationJournal(() => storage);
  assert.deepEqual(reloaded.list(actor), [{actorId: actor, operation, key, createdAt: at}]);
  assert.deepEqual(reloaded.list('7'), []); assert.equal(writes, 1);
  assert.doesNotMatch([...storage.values.values()].join(''), /Private inspection|reason|sha256|reference/);
});

test('revalidation journal rejects malformed keys, cross-operation keys and injected private metadata', () => {
  const {storage, journal} = setup();
  for (const invalid of [key.replace(sku, '0'), key.replace(sku, '01'), key.replace(sku, '9223372036854775808'),
    key.replace(uuid, uuid.toUpperCase()), key.replace('-4b68-', '-1b68-'), key + '\n', key + '.extra',
    key.replace('revalidate.', ''), key.replace('revalidate.', 'upload.')]) {
    assert.throws(() => journal.remember(actor, operation, invalid), /recovery_storage_unavailable/);
  }
  assert.throws(() => journal.remember(actor, operation + '.extra', key), /recovery_storage_unavailable/);
  assert.equal(storage.length, 0);
  const entry = journal.remember(actor, operation, key), storedKey = storage.key(0)!;
  storage.setItem(storedKey, JSON.stringify({...entry, reference: {id: '72'}}));
  assert.throws(() => journal.list(actor), /recovery_storage_unavailable/);
  storage.setItem(storedKey, JSON.stringify({...entry, actorId: '7'}));
  assert.throws(() => journal.list(actor), /recovery_storage_unavailable/);
  assert.deepEqual(journal.list('7'), []);
});

test('revalidation needs the owning lookup adapter and directs missing-adapter users to catalog inspection', async () => {
  const {journal} = setup(); const entry = journal.remember(actor, operation, key);
  const adapters: OperationRecoveryAdapters = {'evidence.image-revalidation': {lookup: async () => null}};
  assert.deepEqual(operationRecoveryCapabilities(operation), {canCheck: false, canClose: false, unavailable: 'adapter'});
  assert.deepEqual(operationRecoveryCapabilities(operation, adapters), {canCheck: true, canClose: false, unavailable: null});
  assert.deepEqual(recoveryPage(operation), {href: '/administration/catalog', label: 'halaman pemeriksaan ulang foto katalog'});
  assert.equal(operationLabel(operation), 'Permintaan pemeriksaan ulang foto katalog');
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy), /recovery_adapter_unavailable/);
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters, 'Close uncertain request'), /recovery_close_unsupported/);
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapters), {state: 'unconfirmed'});
  assert.deepEqual(journal.list(actor), [entry]);
});

test('a committed revalidation request resolves its original key without treating pending as passed', async () => {
  for (const state of ['pending', 'passed', 'rejected'] as const) {
    const {journal} = setup(); const entry = journal.remember(actor, operation, key); let lookups = 0;
    const unrelated = journal.remember(actor, operation, key.replace(uuid, 'af590c38-8daa-4d26-b355-2a9e86a89ef7'));
    const adapters: OperationRecoveryAdapters = {'evidence.image-revalidation': {lookup: async original => {
      lookups++; assert.deepEqual(original, entry); return {state: 'committed', result: {...result, state}};
    }}};
    assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapters), {state: 'committed', result: {id: result.requestId, version: '1'}});
    assert.deepEqual(journal.list(actor), [unrelated]); assert.equal(lookups, 1);
  }
});

test('foreign source, malformed result and extra recovery metadata retain the unresolved revalidation', async () => {
  const receipts: unknown[] = [
    {state: 'committed', result: {...result, skuId: '7'}}, {state: 'committed', result: {...result, requestId: 41}},
    {state: 'committed', result: {...result, versionRowId: '0'}}, {state: 'committed', result: {...result, state: 'accepted'}},
    {state: 'committed', result: {...result, recordedAt: 'yesterday'}}, {state: 'committed', result: {...result, actorId: '7'}},
    {state: 'committed', result, actorId: '7'}, {state: 'closed', result: null}, {state: 'not_confirmed', result: null},
  ];
  for (const receipt of receipts) {
    const {journal} = setup(); const entry = journal.remember(actor, operation, key);
    const adapters = {'evidence.image-revalidation': {lookup: async () => receipt}} as unknown as OperationRecoveryAdapters;
    await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters));
    assert.deepEqual(journal.list(actor), [entry]);
  }
});

test('actor changes before lookup or during reply preserve both accounts recovery references', async () => {
  const {journal} = setup(); const entry = journal.remember(actor, operation, key);
  const other = journal.remember('7', operation, key.replace(sku, '8')); let activeActor = '7', lookups = 0;
  const current = () => activeActor === entry.actorId;
  const adapters: OperationRecoveryAdapters = {'evidence.image-revalidation': {lookup: async () => {
    lookups++; activeActor = '7'; return {state: 'committed', result};
  }}};
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters, undefined, current), /recovery_request_stale/);
  assert.equal(lookups, 0); activeActor = actor;
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters, undefined, current), /recovery_request_stale/);
  assert.equal(lookups, 1);
  const denied: OperationRecoveryAdapters = {'evidence.image-revalidation': {lookup: async () => { throw new AccessFailure(409, 'actor_changed'); }}};
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, denied), /actor_changed/);
  assert.deepEqual(journal.list(actor), [entry]); assert.deepEqual(journal.list('7'), [other]);
});
