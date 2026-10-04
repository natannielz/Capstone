import {test} from 'node:test';
import assert from 'node:assert/strict';
import {RequestNotSentError} from '../src/api/client';
import {AccessFailure} from '../src/modules/administration/workflow';
import type {OperationReference} from '../src/modules/administration/operation-journal';
import {createCatalogPhotoGateway, type CatalogPhotoRequest, type CatalogPhotoTransport} from '../src/modules/catalog/catalog-photo-client';
import {catalogPhotoKey, catalogPhotoOperation, createCatalogPhotoAssociationWorkflow, parseCatalogPhotoCurrent,
  parseCatalogPhotoRecovery, parseCatalogPhotoRecoveryReference, parseCatalogPhotoResult, prepareCatalogPhotoAssociation,
  type CatalogPhotoJournal, type CatalogPhotoTarget} from '../src/modules/catalog/catalog-photo-workflow';

const actorId = '9007199254740993';
const target: CatalogPhotoTarget = {skuId: '9007199254740995', skuRevision: '7', definitionId: '9007199254740997', definitionVersion: '3'};
const at = '2026-10-04T05:30:00.123+00:00';
const reference = {id: '9007199254741001', version: '2', sha256: 'ab'.repeat(32)};
const uuid = '11111111-2222-4333-8444-555555555555';
const key = catalogPhotoKey(target.skuId, uuid);
const current = {...target, photoRevision: '0', photo: null};
const selected = {...target, photoRevision: '1', photo: {associationId: '9007199254741005', reservationId: '9007199254740999', reference,
  mime: 'image/png', recordedAt: at, matchesCurrentDefinition: true}};
const command = () => prepareCatalogPhotoAssociation(actorId, target, current, '9007199254740999', reference, 'Foto kemasan asli ditinjau');
const result = {associationId: '9007199254741005', ...target, photoRevision: '1', recordedAt: at};
const response = (value: unknown) => ({response: new Response(null, {status: 200}), data: value});
const port = (post: CatalogPhotoTransport['post']): CatalogPhotoTransport => ({post});
const hasCode = (code: string) => (error: unknown) => error instanceof AccessFailure && error.code === code;

/** A journal port that round-trips its exact serialized references, including across a fresh workflow instance. */
function journal(rows = new Map<string, string>()): CatalogPhotoJournal {
  return {
    list: id => [...rows.values()].map(value => JSON.parse(value) as OperationReference).filter(entry => entry.actorId === id),
    remember(id, operation, operationKey) {
      const entry = {actorId: id, operation, key: operationKey, createdAt: '2026-10-04T05:31:00.000Z'};
      rows.set(`${id}:${operation}:${operationKey}`, JSON.stringify(entry));
      return JSON.parse(rows.get(`${id}:${operation}:${operationKey}`)!) as OperationReference;
    },
    confirmed: entry => { rows.delete(`${entry.actorId}:${entry.operation}:${entry.key}`); },
  };
}

test('catalog photo metadata preserves lossless IDs, zero revision and a historical definition mismatch without inventing a current photo', () => {
  assert.deepEqual(parseCatalogPhotoCurrent(current, target), current);
  const priorDefinition = {...selected, photo: {...selected.photo, matchesCurrentDefinition: false}};
  assert.deepEqual(parseCatalogPhotoCurrent(priorDefinition, target), priorDefinition);
  for (const value of [
    {...current, skuId: Number(target.skuId)}, {...current, skuId: '09007199254740995'},
    {...current, definitionId: '9223372036854775808'}, {...current, skuRevision: '7\n'},
    {...current, photoRevision: 0}, {...current, photoRevision: '00'}, {...current, photoRevision: '1'},
    {...selected, photoRevision: '0'}, {...selected, photoRevision: '257'},
    {...selected, photo: {...selected.photo, mime: 'image/svg+xml'}},
    {...selected, photo: {...selected.photo, reference: {...reference, sha256: reference.sha256.toUpperCase()}}},
    {...selected, photo: {...selected.photo, reference: {...reference, version: 2}}},
    {...selected, photo: {...selected.photo, recordedAt: '2026-02-30T05:30:00Z'}},
    {...selected, photo: {...selected.photo, storagePath: '/private/original.png'}},
    {...current, imageAssetIds: []},
  ]) assert.throws(() => parseCatalogPhotoCurrent(value, target), hasCode('catalog_photo_response_invalid'));
  assert.throws(() => parseCatalogPhotoCurrent(current, {...target, definitionVersion: '4'}));
});

test('association freezes the exact source and reference; results must preserve the SKU/definition and advance only the photo revision', () => {
  const mutableReference = {...reference}, prepared = prepareCatalogPhotoAssociation(actorId, target, current, '9007199254740999', mutableReference, 'Ditinjau');
  mutableReference.sha256 = 'cd'.repeat(32);
  assert.equal(prepared.body.reference.sha256, reference.sha256);
  assert.ok(Object.isFrozen(prepared) && Object.isFrozen(prepared.target) && Object.isFrozen(prepared.body) && Object.isFrozen(prepared.body.reference));
  assert.deepEqual(parseCatalogPhotoResult(result, target.skuId, prepared), result);
  for (const change of [{skuId: '5'}, {skuRevision: '8'}, {definitionId: '6'}, {definitionVersion: '4'}, {photoRevision: '2'}, {associationId: 1}, {publicUrl: '/original'}])
    assert.throws(() => parseCatalogPhotoResult({...result, ...change}, target.skuId, prepared));
  const replacement = prepareCatalogPhotoAssociation(actorId, target, parseCatalogPhotoCurrent(selected, target), '9007199254741011', reference, 'Ganti foto');
  assert.throws(() => parseCatalogPhotoResult({...result, photoRevision: '2'}, target.skuId, replacement));
  assert.equal(parseCatalogPhotoResult({...result, associationId: '9007199254741013', photoRevision: '2'}, target.skuId, replacement).photoRevision, '2');
  for (const reason of ['', ' Ditinjau', 'Ditinjau\n', 'a'.repeat(1001), '\ud800'])
    assert.throws(() => prepareCatalogPhotoAssociation(actorId, target, current, '9007199254740999', reference, reason), hasCode('invalid_catalog_photo_request'));
  assert.throws(() => prepareCatalogPhotoAssociation(actorId, target, {...current, definitionId: '1'}, '9007199254740999', reference, 'Ditinjau'));
  assert.throws(() => prepareCatalogPhotoAssociation(actorId, target, {...parseCatalogPhotoCurrent(selected, target), photoRevision: '256'}, '9007199254740999', reference, 'Ditinjau'));
});

test('gateway shapes only the three agreed POST requests with exact actor, SKU If-Match and key; recovery sends no private source', async () => {
  const requests: CatalogPhotoRequest[] = [], original = command();
  const gateway = createCatalogPhotoGateway(port(async request => {
    requests.push(request);
    assert.equal(request.method, 'POST'); assert.equal(request.cache, 'no-store'); assert.equal(request.credentials, 'same-origin'); assert.equal(request.redirect, 'error');
    assert.equal(request.params.header['X-Expected-Actor-Id'], actorId);
    if (request.path === '/api/v1/catalog/skus/{id}/primary-photo/query') {
      assert.deepEqual(request.params.path, {id: target.skuId}); assert.deepEqual(request.body, {purpose: 'catalog-photo'}); return response(current);
    }
    if (request.path === '/api/v1/catalog/skus/{id}/primary-photo') {
      assert.deepEqual(request.params.path, {id: target.skuId});
      assert.deepEqual(request.params.header, {'X-Expected-Actor-Id': actorId, 'If-Match': '"7"', 'Idempotency-Key': key});
      assert.deepEqual(request.body, original.body); return response(result);
    }
    assert.deepEqual(request.params, {header: {'X-Expected-Actor-Id': actorId}});
    assert.deepEqual(request.body, {purpose: 'catalog-photo', key}); return response({result});
  }));
  const cancellation = new AbortController();
  assert.deepEqual(await gateway.current(actorId, target, () => true, cancellation.signal), current);
  assert.equal(requests[0].signal, cancellation.signal);
  assert.deepEqual(await gateway.associate(original, key, () => true), result);
  const entry = parseCatalogPhotoRecoveryReference({actorId, operation: catalogPhotoOperation, key, createdAt: at}, actorId);
  assert.deepEqual(await gateway.recover(entry, () => true), result);
  assert.equal(requests.length, 3);
  await assert.rejects(() => gateway.associate(original, catalogPhotoKey('9', uuid), () => true), RequestNotSentError);
  assert.equal(requests.length, 3);
});

test('stale or cancelled selections cannot send or accept a late read, and a late write remains uncertain', async () => {
  let calls = 0, active = true;
  const gateway = createCatalogPhotoGateway(port(async request => { calls++; active = false; return response(request.path.endsWith('/query') ? current : result); }));
  await assert.rejects(() => gateway.associate(command(), key, () => false), RequestNotSentError);
  const cancellation = new AbortController(); cancellation.abort();
  await assert.rejects(() => gateway.current(actorId, target, () => true, cancellation.signal), hasCode('catalog_photo_view_changed'));
  assert.equal(calls, 0);
  await assert.rejects(() => gateway.current(actorId, target, () => active), hasCode('catalog_photo_view_changed'));
  active = true;
  await assert.rejects(() => gateway.associate(command(), key, () => active), hasCode('commit_outcome_unknown'));
  assert.equal(calls, 2);
});

test('a lost commit survives reload with only actor/operation/key/time, recovers by original key, and never resends on null or wrong-source lookup', async () => {
  const rows = new Map<string, string>(); let writes = 0, lookups = 0;
  const lost = createCatalogPhotoGateway(port(async request => {
    assert.equal(request.path, '/api/v1/catalog/skus/{id}/primary-photo'); writes++;
    const stored = JSON.parse([...rows.values()][0]) as Record<string, unknown>;
    assert.deepEqual(Object.keys(stored).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.deepEqual(stored, {actorId, operation: catalogPhotoOperation, key, createdAt: '2026-10-04T05:31:00.000Z'});
    throw new TypeError('Connection lost after server commit');
  }));
  const first = createCatalogPhotoAssociationWorkflow(lost, journal(rows), () => uuid);
  await assert.rejects(() => first.associate(command(), () => true));
  assert.equal(first.pending(actorId).length, 1);
  await assert.rejects(() => first.associate(command(), () => true), hasCode('resolve_previous_attempt'));
  let found: unknown = null;
  const fresh = createCatalogPhotoAssociationWorkflow(createCatalogPhotoGateway(port(async request => {
    lookups++; assert.equal(request.path, '/api/v1/catalog/primary-photos/operations/query'); assert.deepEqual(request.body, {purpose: 'catalog-photo', key});
    return response({result: found});
  })), journal(rows), () => uuid);
  const entry = fresh.pending(actorId)[0];
  await assert.rejects(() => fresh.recover('9007199254740994', entry, () => true), hasCode('actor_changed'));
  assert.equal(lookups, 0);
  assert.equal(await fresh.recover(actorId, entry, () => true), null);
  await assert.rejects(() => fresh.associate(command(), () => true), hasCode('resolve_previous_attempt'));
  assert.equal(fresh.pending(actorId).length, 1);
  found = {...result, skuId: '9'};
  await assert.rejects(() => fresh.recover(actorId, entry, () => true), hasCode('catalog_photo_response_invalid'));
  assert.equal(fresh.pending(actorId).length, 1);
  found = result;
  assert.deepEqual(await fresh.recover(actorId, entry, () => true), result);
  assert.deepEqual(fresh.pending(actorId), []);
  assert.equal(writes, 1); assert.equal(lookups, 3);
});

test('recovery accepts only the nullable-result envelope and the SKU encoded in the canonical original key', () => {
  const entry = parseCatalogPhotoRecoveryReference({actorId, operation: catalogPhotoOperation, key, createdAt: at}, actorId);
  assert.deepEqual(parseCatalogPhotoRecovery({result}, entry), result);
  assert.equal(parseCatalogPhotoRecovery({result: null}, entry), null);
  for (const value of [{}, {result: undefined}, {state: 'committed', result}, {result: {...result, photoRevision: '0'}}, {result: {...result, skuId: '2'}}])
    assert.throws(() => parseCatalogPhotoRecovery(value, entry));
  for (const bad of [key + '\n', key.replace(target.skuId, '0' + target.skuId), key.replace('catalog.photo.', 'catalog.sku.'), key.replace(uuid, '0'.repeat(36))])
    assert.throws(() => parseCatalogPhotoRecoveryReference({...entry, key: bad}, actorId));
  assert.throws(() => parseCatalogPhotoRecoveryReference({...entry, reference}, actorId));
  assert.throws(() => parseCatalogPhotoRecoveryReference(entry, '1'));
});

test('journal failure prevents a write; malformed successes retain recovery and cleanup failure never causes a resend', async () => {
  let calls = 0;
  const rows = new Map<string, string>(), base = journal(rows);
  const gateway = createCatalogPhotoGateway(port(async () => { calls++; return response(result); }));
  const blocked = createCatalogPhotoAssociationWorkflow(gateway, {...base, remember: () => { throw new AccessFailure(400, 'recovery_storage_unavailable'); }}, () => uuid);
  await assert.rejects(() => blocked.associate(command(), () => true), hasCode('recovery_storage_unavailable')); assert.equal(calls, 0);
  const dirty = createCatalogPhotoAssociationWorkflow(gateway, {...base, confirmed: () => { throw new Error('Storage unavailable'); }}, () => uuid);
  assert.deepEqual(await dirty.associate(command(), () => true), result);
  await assert.rejects(() => dirty.associate(command(), () => true), hasCode('resolve_previous_attempt')); assert.equal(calls, 1);
  const failedRows = new Map<string, string>();
  const malformed = createCatalogPhotoAssociationWorkflow(createCatalogPhotoGateway(port(async () => response({...result, definitionId: '1'}))), journal(failedRows), () => uuid);
  await assert.rejects(() => malformed.associate(command(), () => true), hasCode('catalog_photo_response_invalid'));
  assert.equal(malformed.pending(actorId).length, 1);
  const deniedRows = new Map<string, string>();
  const denied = createCatalogPhotoAssociationWorkflow(createCatalogPhotoGateway(port(async () => ({response: new Response(null, {status: 403}), error: {code: 'access_denied'}}))), journal(deniedRows), () => uuid);
  await assert.rejects(() => denied.associate(command(), () => true), hasCode('access_denied'));
  assert.equal(denied.pending(actorId).length, 0);
});
