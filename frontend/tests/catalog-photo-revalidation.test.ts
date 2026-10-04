import {test} from 'node:test';
import assert from 'node:assert/strict';
import {RequestNotSentError} from '../src/api/client';
import {AccessFailure} from '../src/modules/administration/workflow';
import type {OperationReference} from '../src/modules/administration/operation-journal';
import {parseCatalogPhotoCurrent, type CatalogPhotoJournal, type CatalogPhotoTarget} from '../src/modules/catalog/catalog-photo-workflow';
import {catalogPhotoRevalidationKey, catalogPhotoRevalidationKeySku, catalogPhotoRevalidationOperation, catalogPhotoRevalidationPurpose,
  createCatalogPhotoRevalidationApiGateway, createCatalogPhotoRevalidationTransport, createCatalogPhotoRevalidationWorkflow,
  parseCatalogPhotoRevalidationCurrent, parseCatalogPhotoRevalidationResult, prepareCatalogPhotoRevalidation,
  type CatalogPhotoRevalidationRequest, type CatalogPhotoRevalidationTransport} from '../src/modules/catalog/catalog-photo-revalidation-api';

const actor = '9007199254740993', uuid = '11111111-2222-4333-8444-555555555555';
const target: CatalogPhotoTarget = {skuId: '9007199254740995', skuRevision: '7', definitionId: '9007199254740997', definitionVersion: '3'};
const reference = {id: '9007199254741001', version: '2', sha256: 'ab'.repeat(32)};
const metadata = parseCatalogPhotoCurrent({...target, photoRevision: '1', photo: {associationId: '9007199254741003', reservationId: '9007199254741005', reference,
  mime: 'image/png', recordedAt: '2026-10-04T05:00:00Z', matchesCurrentDefinition: true}}, target);
const proof = {skuId: target.skuId, skuRevision: target.skuRevision, versionRowId: '9007199254741007', proofRevision: '0',
  state: 'first_validation' as const, checkedAt: null, mime: null, pipelineId: null};
const pending = {...proof, proofRevision: '1', state: 'pending' as const};
const passed = {...proof, proofRevision: '1', state: 'passed' as const, checkedAt: '2026-10-04T06:00:00Z', mime: 'image/png' as const, pipelineId: 'catalog-local-v1'};
const result = {requestId: '9007199254741011', skuId: target.skuId, versionRowId: proof.versionRowId, state: 'pending' as const, recordedAt: '2026-10-04T05:30:00Z'};
const key = catalogPhotoRevalidationKey(target.skuId, uuid);
const command = () => prepareCatalogPhotoRevalidation(actor, target, metadata, proof, 'Periksa ulang foto asli');
const response = (value: unknown, status = 200) => Response.json(value, {status});
const hasCode = (code: string) => (error: unknown) => error instanceof AccessFailure && error.code === code;
function journal(rows = new Map<string, string>()): CatalogPhotoJournal {
  return {list: id => [...rows.values()].map(value => JSON.parse(value) as OperationReference).filter(value => value.actorId === id),
    remember: (actorId, operation, originalKey) => {
      const value = {actorId, operation, key: originalKey, createdAt: '2026-10-04T05:30:00Z'};
      rows.set(originalKey, JSON.stringify(value)); return value;
    }, confirmed: value => { rows.delete(value.key); }};
}

test('revalidation status preserves exact IDs and separates first validation, pending and terminal provenance', () => {
  for (const value of [proof, pending, passed, {...passed, state: 'rejected', mime: null}, {...passed, proofRevision: '256'}])
    assert.deepEqual(parseCatalogPhotoRevalidationCurrent(value, target), value);
  for (const change of [{skuId: Number(target.skuId)}, {skuRevision: '8'}, {versionRowId: '9223372036854775808'},
    {proofRevision: '00'}, {proofRevision: '257'}, {proofRevision: 1}, {checkedAt: '2026-02-30T06:00:00Z'},
    {state: 'passed'}, {proofRevision: '1'}, {mime: 'image/png'}, {pipelineId: 'unproved'}, {storagePath: '/original'}])
    assert.throws(() => parseCatalogPhotoRevalidationCurrent({...proof, ...change}, target), hasCode('catalog_photo_revalidation_invalid'));
  assert.throws(() => parseCatalogPhotoRevalidationCurrent({...passed, mime: null}, target));
  assert.throws(() => parseCatalogPhotoRevalidationCurrent({...pending, checkedAt: passed.checkedAt}, target));
  for (const change of [{skuId: '1'}, {requestId: 1}, {state: 'first_validation'}, {recordedAt: 'invalid'}, {reference}])
    assert.throws(() => parseCatalogPhotoRevalidationResult({...result, ...change}, target.skuId));
});

test('preparation freezes current association reference and finite proof revision without requiring a preview of an expired original', () => {
  const value = command();
  assert.deepEqual(value.body, {purpose: catalogPhotoRevalidationPurpose, expectedProofRevision: '0', reference, reason: 'Periksa ulang foto asli'});
  assert.ok(Object.isFrozen(value) && Object.isFrozen(value.target) && Object.isFrozen(value.body.reference));
  assert.equal(value.reservationId, metadata.photo!.reservationId);
  assert.throws(() => prepareCatalogPhotoRevalidation(actor, target, metadata, pending, 'Periksa ulang'), RequestNotSentError);
  assert.throws(() => prepareCatalogPhotoRevalidation(actor, target, metadata, {...passed, proofRevision: '256'}, 'Periksa ulang'), RequestNotSentError);
  assert.equal(prepareCatalogPhotoRevalidation(actor, target, metadata, {...passed, proofRevision: '255'}, 'Periksa ulang').body.expectedProofRevision, '255');
  for (const reason of ['', ' Alasan', 'Alasan\n', 'a'.repeat(1001), '\ud800'])
    assert.throws(() => prepareCatalogPhotoRevalidation(actor, target, metadata, proof, reason), RequestNotSentError);
  assert.throws(() => prepareCatalogPhotoRevalidation(actor, target, {...metadata, photo: null}, proof, 'Alasan'));
  assert.throws(() => prepareCatalogPhotoRevalidation(actor, target, {...metadata, photo: {...metadata.photo!, matchesCurrentDefinition: false}}, proof, 'Alasan'));
  for (const bad of [key + '\n', key.replace(target.skuId, '0' + target.skuId), key.replace('.revalidate.', '.upload.'), key.replace(uuid, '0'.repeat(36))])
    assert.throws(() => catalogPhotoRevalidationKeySku(bad));
});

test('the three exact requests bind actor, SKU revision and original bytes; lookup sends only original key and purpose', async () => {
  const requests: CatalogPhotoRevalidationRequest[] = [];
  const gateway = createCatalogPhotoRevalidationApiGateway({send: async request => {
    requests.push(request); assert.equal(request.headers['X-Expected-Actor-Id'], actor);
    return response(request.path.endsWith('/operations/query') ? {result} : request.path.endsWith('/query') ? proof : result);
  }});
  const controller = new AbortController();
  assert.deepEqual(await gateway.current(actor, target, metadata, () => true, controller.signal), proof);
  assert.deepEqual(requests[0].body, {purpose: catalogPhotoRevalidationPurpose, reference});
  assert.equal(requests[0].signal, controller.signal);
  assert.deepEqual(await gateway.request(command(), key, () => true), result);
  assert.equal(requests[1].path, `/api/v1/catalog/skus/${target.skuId}/photo-evidence/${metadata.photo!.reservationId}/revalidation`);
  assert.deepEqual(requests[1].headers, {'X-Expected-Actor-Id': actor, 'If-Match': '"7"', 'Idempotency-Key': key});
  const entry = {actorId: actor, operation: catalogPhotoRevalidationOperation, key, createdAt: result.recordedAt};
  assert.deepEqual(await gateway.recover(entry, () => true), result);
  assert.deepEqual(requests[2].body, {purpose: catalogPhotoRevalidationPurpose, key});
  assert.deepEqual(requests[2].headers, {'X-Expected-Actor-Id': actor});
  await assert.rejects(() => gateway.request({...command(), body: {...command().body, expectedProofRevision: '256'}}, key, () => true), RequestNotSentError);
  await assert.rejects(() => gateway.request(command(), catalogPhotoRevalidationKey('3', uuid), () => true), RequestNotSentError);
  assert.equal(requests.length, 3);
});

test('a lost request survives reload as a minimal original-key reference; absent or malformed recovery cannot unlock a new request', async () => {
  const rows = new Map<string, string>(); let sends = 0;
  const lost = createCatalogPhotoRevalidationWorkflow(createCatalogPhotoRevalidationApiGateway({send: async () => {
    sends++; assert.deepEqual(Object.keys(JSON.parse([...rows.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    throw new TypeError('Lost after actual server send');
  }}), journal(rows), () => uuid);
  await assert.rejects(() => lost.request(command(), () => true));
  assert.equal(sends, 1);
  let recovered: unknown = null, lookups = 0;
  const freshGateway = createCatalogPhotoRevalidationApiGateway({send: async request => {
    if (request.path.endsWith('/operations/query')) {lookups++; return response({result: recovered});}
    assert.ok(request.path.endsWith('/query')); return response({...pending, proofRevision: '2'});
  }});
  const fresh = createCatalogPhotoRevalidationWorkflow(freshGateway, journal(rows), () => uuid), entry = fresh.pending(actor)[0];
  await assert.rejects(() => fresh.recover('9007199254740994', entry, () => true), hasCode('actor_changed'));
  assert.equal(lookups, 0);
  assert.equal(await fresh.recover(actor, entry, () => true), null);
  await assert.rejects(() => fresh.request(command(), () => true), hasCode('resolve_previous_attempt'));
  recovered = {...result, skuId: '3'};
  await assert.rejects(() => fresh.recover(actor, entry, () => true), hasCode('catalog_photo_revalidation_invalid'));
  assert.equal(rows.size, 1);
  recovered = {...result, state: 'passed'};
  assert.equal((await fresh.recover(actor, entry, () => true))?.state, 'passed');
  assert.equal(rows.size, 0);
  // An older passed result is never projected as the status of a later current head.
  assert.equal((await freshGateway.current(actor, target, metadata, () => true)).state, 'pending');
  assert.equal(sends, 1); assert.equal(lookups, 3);
});

test('current configuration failure, cancellation and late writes fail closed; bad result identity remains recoverable', async () => {
  const disabled = createCatalogPhotoRevalidationApiGateway({send: async () => response({code: 'image_revalidation_unavailable'}, 503)});
  await assert.rejects(() => disabled.current(actor, target, metadata, () => true), hasCode('image_revalidation_unavailable'));
  const rows = new Map<string, string>(); let active = true, sends = 0;
  const late = createCatalogPhotoRevalidationWorkflow(createCatalogPhotoRevalidationApiGateway({send: async () => {sends++; active = false; return response(result);}}), journal(rows), () => uuid);
  await assert.rejects(() => late.request(command(), () => false), RequestNotSentError);
  assert.equal(sends, 0); assert.equal(rows.size, 0);
  await assert.rejects(() => late.request(command(), () => active), hasCode('commit_outcome_unknown'));
  assert.equal(rows.size, 1);
  const badRows = new Map<string, string>();
  const malformed = createCatalogPhotoRevalidationWorkflow(createCatalogPhotoRevalidationApiGateway({send: async () => response({...result, versionRowId: '1'})}), journal(badRows), () => uuid);
  await assert.rejects(() => malformed.request(command(), () => true), hasCode('catalog_photo_revalidation_invalid'));
  assert.equal(badRows.size, 1);
});

test('real JSON transport obtains fresh CSRF, retains private request headers, and never sends after the view changes during preflight', async () => {
  const requests: Request[] = []; let active = true;
  const fakeFetch: typeof fetch = async input => {
    const request = input instanceof Request ? input : new Request(input); requests.push(request);
    if (request.url.endsWith('/csrf')) return response({requestToken: 'synthetic-csrf'});
    assert.equal(request.method, 'POST'); assert.equal(request.cache, 'no-store'); assert.equal(request.credentials, 'same-origin'); assert.equal(request.redirect, 'error');
    assert.equal(request.headers.get('X-CSRF-Token'), 'synthetic-csrf'); assert.equal(request.headers.get('X-Expected-Actor-Id'), actor);
    assert.equal(request.headers.get('If-Match'), '"7"'); assert.equal(request.headers.get('Idempotency-Key'), key);
    assert.deepEqual(await request.json(), command().body); return response(result);
  };
  const transport = createCatalogPhotoRevalidationTransport({origin: 'https://unit-toko.test', fetch: fakeFetch});
  const gateway = createCatalogPhotoRevalidationApiGateway(transport);
  await gateway.request(command(), key, () => active);
  assert.equal(requests.length, 2);
  const stale: CatalogPhotoRevalidationTransport = createCatalogPhotoRevalidationTransport({origin: 'https://unit-toko.test', fetch: async input => {
    assert.ok(String(input).endsWith('/csrf')); active = false; return response({requestToken: 'synthetic-csrf'});
  }});
  await assert.rejects(() => createCatalogPhotoRevalidationApiGateway(stale).request(command(), key, () => active), RequestNotSentError);
  assert.equal(requests.length, 2);
});
