import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {RequestNotSentError} from '../src/api/client';
import {AccessFailure} from '../src/modules/administration/workflow';
import {catalogPhotoEvidenceReady, catalogPhotoMaximumBytes, catalogPhotoReservationKey, createCatalogPhotoEvidenceGateway,
  createCatalogPhotoEvidenceTransport, parseCatalogPhotoEvidenceStatus, validateCatalogPhotoFile,
  type CatalogPhotoEvidenceStatus} from '../src/modules/catalog/catalog-photo-evidence-client';
import {parseCatalogPhotoCurrent} from '../src/modules/catalog/catalog-photo-workflow';

const actor = '9007199254740993', target = {skuId: '9007199254740995', skuRevision: '7', definitionId: '9007199254740997', definitionVersion: '3'};
const bytes = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aW0cAAAAASUVORK5CYII=', 'base64');
const image = new File([bytes], 'synthetic.png', {type: 'image/png'});
const reference = {id: '9007199254741011', version: '2', sha256: createHash('sha256').update(bytes).digest('hex')};
const reservation = {reservationId: '9007199254741001', revision: '1' as const, expiresAt: '2026-10-05T00:00:00Z'};
const ready: CatalogPhotoEvidenceStatus = {...reservation, revision: '3', state: 'quarantined', reference, scanStatus: 'clean', decoderStatus: 'decoded', safeFailure: null};
const reserveKey = catalogPhotoReservationKey(target.skuId, '11111111-2222-4333-8444-555555555555');
const metadata = parseCatalogPhotoCurrent({...target, photoRevision: '1', photo: {associationId: '9007199254741021', reservationId: reservation.reservationId,
  reference, mime: 'image/png', recordedAt: '2026-10-04T05:00:00Z', matchesCurrentDefinition: true}}, target);
const photoResponse = () => new Response(bytes, {headers: {'Content-Type': 'image/png', 'Content-Length': String(bytes.length)}});
const invalid = (error: unknown) => error instanceof AccessFailure && error.code === 'catalog_photo_evidence_invalid';

test('catalog image status keeps upload completion distinct from clean scanner and decoder proof', () => {
  assert.equal(catalogPhotoEvidenceReady(ready), true);
  assert.equal(catalogPhotoEvidenceReady({...ready, scanStatus: 'unavailable', decoderStatus: 'unavailable'}), false);
  assert.equal(catalogPhotoEvidenceReady({...ready, decoderStatus: 'invalid', safeFailure: 'invalid_media'}), false);
  assert.equal(catalogPhotoEvidenceReady({...ready, scanStatus: 'infected', decoderStatus: 'unavailable', safeFailure: 'infected_media'}), false);
  for (const value of [
    {...ready, reservationId: Number(reservation.reservationId)}, {...ready, reference: null}, {...ready, state: 'fresh'},
    {...ready, scanStatus: 'unavailable'}, {...ready, safeFailure: 'integrity_mismatch'},
    {...ready, reference: {...reference, version: '02'}}, {...ready, reference: {...reference, sha256: reference.sha256 + '\n'}},
    {...ready, originalPath: 'private/photo.png'},
  ]) assert.throws(() => parseCatalogPhotoEvidenceStatus(value), invalid);
  assert.throws(() => parseCatalogPhotoEvidenceStatus(ready, '1'), invalid);
});

test('same-origin gateway sends fresh CSRF, exact current SKU and raw bounded PNG bytes without implicit validation or retries', async () => {
  let csrf = 0, uploads = 0, reserves = 0;
  const gateway = createCatalogPhotoEvidenceGateway(createCatalogPhotoEvidenceTransport({origin: 'https://catalog.test', fetch: async value => {
    assert.ok(value instanceof Request);
    const url = new URL(value.url);
    if (url.pathname === '/api/v1/identity/csrf') return Response.json({requestToken: `fresh-${++csrf}`});
    assert.equal(value.method, 'POST'); assert.equal(value.credentials, 'same-origin'); assert.equal(value.cache, 'no-store'); assert.equal(value.redirect, 'error');
    assert.equal(value.headers.get('X-Expected-Actor-Id'), actor); assert.equal(value.headers.get('X-CSRF-Token'), `fresh-${csrf}`);
    if (url.pathname.endsWith('/photo-evidence')) {
      reserves++; assert.equal(value.headers.get('If-Match'), '"7"'); assert.equal(value.headers.get('Idempotency-Key'), reserveKey);
      assert.deepEqual(await value.json(), {reason: 'Foto kemasan ditinjau'}); return Response.json(reservation);
    }
    if (url.pathname.endsWith('/content')) {
      uploads++; assert.equal(value.headers.get('If-Match'), '"1"'); assert.equal(value.headers.get('Idempotency-Key'), `upload.${reservation.reservationId}`);
      assert.equal(value.headers.get('content-type'), 'application/octet-stream'); assert.deepEqual(Buffer.from(await value.arrayBuffer()), bytes);
      return Response.json({state: 'quarantined', reference});
    }
    if (url.pathname.endsWith('/recover')) {
      assert.deepEqual(await value.json(), {purpose: 'catalog-photo', reason: 'Periksa hasil unggah foto katalog'});
      assert.equal(value.headers.get('Idempotency-Key'), `upload.${reservation.reservationId}`); return Response.json({state: 'quarantined', reference});
    }
    assert.deepEqual(await value.json(), {purpose: 'catalog-photo'}); return Response.json(ready);
  }}));
  assert.deepEqual(await gateway.reserve(actor, target, reserveKey, 'Foto kemasan ditinjau', () => true), reservation);
  assert.deepEqual(await gateway.upload(actor, target.skuId, reservation, image, () => true), {state: 'quarantined', reference});
  assert.equal(catalogPhotoEvidenceReady(await gateway.status(actor, target.skuId, reservation.reservationId, () => true)), true);
  await gateway.recoverUpload(actor, target.skuId, reservation.reservationId, () => true);
  assert.equal(csrf, 4); assert.equal(uploads, 1); assert.equal(reserves, 1);
});

test('file and stale-session checks stop dispatch, including a session change while fetching CSRF', async () => {
  for (const file of [new File([], 'empty.png', {type: 'image/png'}), new File([bytes], 'document.pdf', {type: 'application/pdf'}),
    new File([new Uint8Array(catalogPhotoMaximumBytes + 1)], 'oversize.jpg', {type: 'image/jpeg'})])
    assert.throws(() => validateCatalogPhotoFile(file), RequestNotSentError);
  let active = true, calls = 0;
  const gateway = createCatalogPhotoEvidenceGateway(createCatalogPhotoEvidenceTransport({origin: 'https://catalog.test', fetch: async value => {
    assert.ok(value instanceof Request); assert.ok(value.url.endsWith('/identity/csrf')); calls++; active = false; return Response.json({requestToken: 'csrf'});
  }}));
  await assert.rejects(() => gateway.reserve(actor, target, reserveKey, 'Ditinjau', () => active), RequestNotSentError);
  assert.equal(calls, 1);
  await assert.rejects(() => gateway.upload(actor, target.skuId, reservation, new File([bytes], 'x.gif', {type: 'image/gif'}), () => true), RequestNotSentError);
  assert.equal(calls, 1);
});

test('original reads require validated evidence, exact hash/MIME/length and SKU revision; current-photo 204 stays absent', async () => {
  let requests = 0;
  const gateway = createCatalogPhotoEvidenceGateway({send: async request => {
    requests++;
    if (request.path.endsWith('/primary-photo/original/query')) {
      assert.deepEqual(request.body, {purpose: 'catalog-photo'}); return photoResponse();
    }
    assert.equal(request.path, `/api/v1/catalog/skus/${target.skuId}/photo-evidence/${reservation.reservationId}/original/query`);
    assert.equal(request.headers['If-Match'], '"7"'); assert.deepEqual(request.body, {purpose: 'catalog-photo-association', reference}); return photoResponse();
  }});
  await assert.rejects(() => gateway.original(actor, target, {...ready, decoderStatus: 'unavailable'}, () => true), RequestNotSentError);
  assert.equal(requests, 0);
  assert.deepEqual(Buffer.from(await (await gateway.original(actor, target, ready, () => true)).arrayBuffer()), bytes);
  assert.deepEqual(Buffer.from(await (await gateway.currentOriginal(actor, metadata, () => true))!.arrayBuffer()), bytes);
  assert.equal(await createCatalogPhotoEvidenceGateway({send: async () => new Response(null, {status: 204})}).currentOriginal(actor, metadata, () => true), null);
  for (const response of [
    new Response(bytes, {headers: {'content-type': 'image/jpeg', 'content-length': String(bytes.length)}}),
    new Response(bytes, {headers: {'content-type': 'image/png'}}),
    new Response(bytes, {headers: {'content-type': 'image/png', 'content-length': '99999999'}}),
    new Response(new Uint8Array(bytes.length), {headers: {'content-type': 'image/png', 'content-length': String(bytes.length)}}),
  ]) await assert.rejects(() => createCatalogPhotoEvidenceGateway({send: async () => response}).currentOriginal(actor, metadata, () => true), invalid);
});

test('overrun streaming is cancelled and late originals are discarded before a Blob can escape', async () => {
  let cancelled = 0;
  const stream = new ReadableStream<Uint8Array>({start(controller) { controller.enqueue(new Uint8Array(2)); }, cancel() { cancelled++; }});
  const overrun = createCatalogPhotoEvidenceGateway({send: async () => new Response(stream, {headers: {'content-type': 'image/png', 'content-length': '1'}})});
  await assert.rejects(() => overrun.original(actor, target, ready, () => true), invalid); assert.equal(cancelled, 1);
  let active = true;
  const late = createCatalogPhotoEvidenceGateway({send: async () => { active = false; return photoResponse(); }});
  await assert.rejects(() => late.original(actor, target, ready, () => active), error => error instanceof AccessFailure && error.code === 'catalog_photo_view_changed');
});

test('reservation recovery uses only the original key and rejects incoherent status or another owner', async () => {
  let value: unknown = {status: 'not_confirmed', reservation: null}, requests = 0;
  const gateway = createCatalogPhotoEvidenceGateway({send: async request => {
    requests++; assert.equal(request.path, `/api/v1/catalog/skus/${target.skuId}/photo-evidence/operations/query`);
    assert.deepEqual(request.body, {purpose: 'catalog-photo', key: reserveKey}); return Response.json(value);
  }});
  assert.equal(await gateway.recoverReservation(actor, reserveKey, () => true), null);
  value = {status: 'committed', reservation}; assert.deepEqual(await gateway.recoverReservation(actor, reserveKey, () => true), reservation);
  value = {status: 'committed', reservation: null}; await assert.rejects(() => gateway.recoverReservation(actor, reserveKey, () => true), invalid);
  await assert.rejects(() => gateway.reserve(actor, {...target, skuId: '1'}, reserveKey, 'Ditinjau', () => true), RequestNotSentError);
  assert.equal(requests, 3);
});
