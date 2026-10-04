import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {AccessFailure} from '../src/modules/administration/workflow';
import {RequestNotSentError} from '../src/api/client';
import {buyerPhotoIdentity, buyerPhotoMaximumBytes, createBuyerPhotoGateway} from '../src/modules/catalog/buyer-photo-client';

const actor = '9007199254740993';
const target = {publicId: '11111111-2222-4333-8444-555555555555', skuId: '9007199254740995', definitionId: '9007199254740997', definitionVersion: '2'};
const bytes = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aW0cAAAAASUVORK5CYII=', 'base64');
const hash = createHash('sha256').update(bytes).digest('hex');
const headers = {'Content-Type': 'image/png', 'Content-Length': String(bytes.length), 'X-Content-Sha256': hash};
const invalid = (error: unknown) => error instanceof AccessFailure && error.code === 'buyer_photo_response_invalid';
function client(response: () => Response | Promise<Response>) {
  return createBuyerPhotoGateway({origin: 'https://catalog.test', fetch: async input => {
    assert.ok(input instanceof Request);
    return input.url.endsWith('/identity/csrf') ? Response.json({requestToken: 'fresh'}) : response();
  }});
}

test('buyer photo is a scoped same-origin POST with fresh CSRF and a verified bounded original, never a Range or public GET', async () => {
  let tokens = 0, photos = 0;
  const gateway = createBuyerPhotoGateway({origin: 'https://catalog.test', fetch: async input => {
    assert.ok(input instanceof Request);
    assert.equal(input.credentials, 'same-origin'); assert.equal(input.cache, 'no-store'); assert.equal(input.redirect, 'error');
    if (input.url.endsWith('/identity/csrf')) return Response.json({requestToken: `token-${++tokens}`});
    photos++; assert.equal(input.url, `https://catalog.test/api/v1/catalog/variants/${target.publicId}/primary-photo/query`);
    assert.equal(input.method, 'POST'); assert.deepEqual(await input.json(), {purpose: 'catalog-photo'});
    assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('X-CSRF-Token'), `token-${tokens}`);
    assert.equal(input.headers.has('Range'), false); return new Response(bytes, {headers});
  }});
  for (let i = 0; i < 2; i++) {
    const result = await gateway.read(actor, target, () => true); assert.ok(result);
    assert.equal(result.type, 'image/png'); assert.deepEqual(Buffer.from(await result.arrayBuffer()), bytes);
  }
  assert.equal(tokens, 2); assert.equal(photos, 2);
  assert.notEqual(buyerPhotoIdentity(actor, target), buyerPhotoIdentity('1', target));
  assert.notEqual(buyerPhotoIdentity(actor, target), buyerPhotoIdentity(actor, {...target, definitionVersion: '3'}));
});

test('only genuine 204 means no photo; denied access and malformed or unverified bytes never become displayable', async () => {
  assert.equal(await client(() => new Response(null, {status: 204})).read(actor, target, () => true), null);
  await assert.rejects(() => client(() => new Response(null, {status: 403})).read(actor, target, () => true), error => error instanceof AccessFailure && error.status === 403);
  const missingHash = new Headers(headers); missingHash.delete('X-Content-Sha256');
  const missingLength = new Headers(headers); missingLength.delete('Content-Length');
  for (const response of [
    new Response(bytes, {headers: missingHash}), new Response(bytes, {headers: missingLength}),
    new Response(bytes, {headers: {...headers, 'X-Content-Sha256': hash.toUpperCase()}}),
    new Response(bytes, {headers: {...headers, 'Content-Type': 'application/pdf'}}),
    new Response(bytes, {headers: {...headers, 'Content-Length': String(buyerPhotoMaximumBytes + 1)}}),
    new Response(bytes, {headers: {...headers, 'Content-Length': '0' + bytes.length}}),
    new Response(bytes, {headers, status: 206}),
    new Response(bytes, {headers: {...headers, 'Content-Range': 'bytes 0-3/8'}}),
    new Response(new Uint8Array(bytes.length), {headers}),
    new Response(bytes.subarray(1), {headers}),
  ]) await assert.rejects(() => client(() => response).read(actor, target, () => true), invalid);
});

test('streaming overrun cancels the original before accumulating beyond the advertised bounded length', async () => {
  let cancelled = 0;
  const stream = new ReadableStream<Uint8Array>({start(controller) { controller.enqueue(new Uint8Array(2)); }, cancel() { cancelled++; }});
  await assert.rejects(() => client(() => new Response(stream, {headers: {...headers, 'Content-Length': '1'}})).read(actor, target, () => true), invalid);
  assert.equal(cancelled, 1);
});

test('session change during CSRF, stale responses and abort stop photo delivery without automatic retry', async () => {
  let active = true, calls = 0;
  const gateway = createBuyerPhotoGateway({origin: 'https://catalog.test', fetch: async () => { calls++; active = false; return Response.json({requestToken: 'fresh'}); }});
  await assert.rejects(() => gateway.read(actor, target, () => active), RequestNotSentError); assert.equal(calls, 1);
  let cancelled = 0;
  const late = client(() => {
    active = false;
    return new Response(new ReadableStream({cancel() { cancelled++; }}), {headers});
  });
  active = true; await assert.rejects(() => late.read(actor, target, () => active), RequestNotSentError); assert.equal(cancelled, 1);
  const abort = new AbortController(); let started!: () => void;
  const reading = new Promise<void>(resolve => { started = resolve; });
  const pending = client(() => new Response(new ReadableStream<Uint8Array>({pull() { started(); }, cancel() { cancelled++; }}), {headers}));
  const operation = pending.read(actor, target, () => true, abort.signal);
  await reading; abort.abort(); await assert.rejects(() => operation, RequestNotSentError); assert.equal(cancelled, 2);
});

test('malformed actor or source identity is rejected before even fetching CSRF', async () => {
  let calls = 0; const gateway = createBuyerPhotoGateway({origin: 'https://catalog.test', fetch: async () => { calls++; throw new Error('must not dispatch'); }});
  for (const [actorId, source] of [['01', target], [actor, {...target, publicId: target.publicId + '/other'}], [actor, {...target, definitionVersion: '0'}]] as const)
    await assert.rejects(() => gateway.read(actorId, source, () => true), RequestNotSentError);
  assert.equal(calls, 0);
});
