import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {AccessFailure} from '../src/modules/administration/workflow';
import {RequestNotSentError} from '../src/api/client';
import {buyerPhotoIdentity, buyerPhotoMaximumBytes, buyerPhotoMaximumPendingReads, createBuyerPhotoGateway} from '../src/modules/catalog/buyer-photo-client';

const actor = '9007199254740993';
const target = {publicId: '11111111-2222-4333-8444-555555555555', skuId: '9007199254740995', definitionId: '9007199254740997', definitionVersion: '2'};
const bytes = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aW0cAAAAASUVORK5CYII=', 'base64');
const hash = createHash('sha256').update(bytes).digest('hex');
const headers = {'Content-Type': 'image/png', 'Content-Length': String(bytes.length), 'X-Content-Sha256': hash};
const invalid = (error: unknown) => error instanceof AccessFailure && error.code === 'buyer_photo_response_invalid';
const notSent = (code: string) => (error: unknown) => error instanceof RequestNotSentError && error.code === code;
function latch() {
  let release!: () => void;
  return {wait: new Promise<void>(resolve => { release = resolve; }), release: () => release()};
}
function exclusiveLocks() {
  const queues = new Map<string, {run: () => void}[]>(), names: string[] = [];
  const request = (name: string, options: LockOptions, callback: (lock: Lock | null) => unknown) => new Promise<unknown>((resolve, reject) => {
    assert.equal(options.mode ?? 'exclusive', 'exclusive'); names.push(name);
    const signal = options.signal;
    if (signal?.aborted) { reject(signal.reason); return; }
    const queue = queues.get(name) ?? []; queues.set(name, queue);
    let granted = false;
    const remove = () => {
      const index = queue.indexOf(entry); queue.splice(index, 1);
      if (queue.length === 0) queues.delete(name);
      else if (index === 0) queue[0].run();
    };
    const abort = () => {
      if (granted) return;
      signal?.removeEventListener('abort', abort); reject(signal?.reason); remove();
    };
    const entry = {run() {
      granted = true; signal?.removeEventListener('abort', abort);
      void Promise.resolve().then(() => callback({name, mode: 'exclusive'})).then(
        value => { resolve(value); remove(); }, error => { reject(error); remove(); });
    }};
    queue.push(entry); signal?.addEventListener('abort', abort, {once: true});
    if (queue.length === 1) entry.run();
  });
  return {request: request as LockManager['request'], names};
}
function client(response: () => Response | Promise<Response>) {
  return createBuyerPhotoGateway({origin: 'https://catalog.test', locks: exclusiveLocks(), fetch: async input => {
    assert.ok(input instanceof Request);
    return input.url.endsWith('/identity/csrf') ? Response.json({requestToken: 'fresh'}) : response();
  }});
}

test('buyer photo is a scoped same-origin POST with fresh CSRF and a verified bounded original, never a Range or public GET', async () => {
  let tokens = 0, photos = 0;
  const gateway = createBuyerPhotoGateway({origin: 'https://catalog.test', locks: exclusiveLocks(), fetch: async input => {
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
  const gateway = createBuyerPhotoGateway({origin: 'https://catalog.test', locks: exclusiveLocks(), fetch: async () => { calls++; active = false; return Response.json({requestToken: 'fresh'}); }});
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
  let calls = 0; const gateway = createBuyerPhotoGateway({origin: 'https://catalog.test', locks: exclusiveLocks(), fetch: async () => { calls++; throw new Error('must not dispatch'); }});
  for (const [actorId, source] of [['01', target], [actor, {...target, publicId: target.publicId + '/other'}], [actor, {...target, definitionVersion: '0'}]] as const)
    await assert.rejects(() => gateway.read(actorId, source, () => true), RequestNotSentError);
  assert.equal(calls, 0);
});

test('many gateways for one actor serialize fresh CSRF, photo requests and body validation under one exclusive lock', {timeout: 5000}, async () => {
  const actorId = '9007199254741101', locks = exclusiveLocks();
  const csrfStarted = latch(), csrfContinue = latch(), photoStarted = latch(), photoContinue = latch();
  let tokens = 0, photos = 0, active = 0, maximumActive = 0;
  const calls: string[] = [];
  const fetcher: typeof fetch = async input => {
    assert.ok(input instanceof Request);
    if (input.url.endsWith('/identity/csrf')) {
      calls.push('csrf'); maximumActive = Math.max(maximumActive, ++active);
      if (++tokens === 1) { csrfStarted.release(); await csrfContinue.wait; }
      return Response.json({requestToken: `token-${tokens}`});
    }
    calls.push('photo'); assert.equal(input.headers.get('X-CSRF-Token'), `token-${tokens}`);
    assert.equal(input.headers.get('X-Expected-Actor-Id'), actorId);
    const first = ++photos === 1;
    return new Response(new ReadableStream<Uint8Array>({start(controller) {
      controller.enqueue(bytes);
      if (first) {
        photoStarted.release();
        void photoContinue.wait.then(() => { active--; controller.close(); });
      } else { active--; controller.close(); }
    }}), {headers});
  };
  const operations = Array.from({length: 12}, (_, index) => createBuyerPhotoGateway({origin: 'https://catalog.test', fetch: fetcher, locks})
    .read(actorId, {...target, definitionVersion: String(index + 1)}, () => true));
  await csrfStarted.wait; assert.deepEqual(calls, ['csrf']);
  csrfContinue.release(); await photoStarted.wait; assert.deepEqual(calls, ['csrf', 'photo']);
  photoContinue.release();
  for (const result of await Promise.all(operations)) {
    assert.ok(result); assert.deepEqual(Buffer.from(await result.arrayBuffer()), bytes);
  }
  assert.equal(maximumActive, 1); assert.equal(tokens, 12); assert.equal(photos, 12); assert.equal(active, 0);
  assert.deepEqual(calls, Array.from({length: 12}, () => ['csrf', 'photo']).flat());
  assert.deepEqual(locks.names, Array(12).fill(`unit-toko:buyer-photo:${actorId}`));
});

test('aborting a queued photo rejects without fetching its CSRF or delaying the next reader', {timeout: 5000}, async () => {
  const actorId = '9007199254741102', locks = exclusiveLocks(), started = latch(), proceed = latch();
  let calls = 0;
  const fetcher: typeof fetch = async input => {
    assert.ok(input instanceof Request);
    if (++calls === 1) { started.release(); await proceed.wait; }
    return input.url.endsWith('/identity/csrf') ? Response.json({requestToken: 'fresh'}) : new Response(bytes, {headers});
  };
  const gateway = () => createBuyerPhotoGateway({origin: 'https://catalog.test', fetch: fetcher, locks});
  const first = gateway().read(actorId, target, () => true); await started.wait;
  const abort = new AbortController(), queued = gateway().read(actorId, target, () => true, abort.signal);
  const rejected = assert.rejects(() => queued, notSent('buyer_photo_view_changed'));
  abort.abort(); await rejected; assert.equal(calls, 1);
  const next = gateway().read(actorId, target, () => true);
  proceed.release(); assert.ok(await first); assert.ok(await next); assert.equal(calls, 4);
});

test('a queued reader checks current actor context again before fetching CSRF', {timeout: 5000}, async () => {
  const actorId = '9007199254741103', locks = exclusiveLocks(), started = latch(), proceed = latch();
  let calls = 0, currentActor = true;
  const fetcher: typeof fetch = async input => {
    assert.ok(input instanceof Request);
    if (++calls === 1) { started.release(); await proceed.wait; }
    return input.url.endsWith('/identity/csrf') ? Response.json({requestToken: 'fresh'}) : new Response(bytes, {headers});
  };
  const gateway = () => createBuyerPhotoGateway({origin: 'https://catalog.test', fetch: fetcher, locks});
  const first = gateway().read(actorId, target, () => true); await started.wait;
  const queued = gateway().read(actorId, target, () => currentActor);
  const rejected = assert.rejects(() => queued, notSent('buyer_photo_view_changed'));
  currentActor = false; proceed.release(); assert.ok(await first); await rejected; assert.equal(calls, 2);
  assert.ok(await gateway().read(actorId, target, () => true)); assert.equal(calls, 4);
});

test('success, denied access, invalid bytes and an active stream abort each release the lock for the next photo', {timeout: 5000}, async () => {
  for (const [index, outcome] of ['success', 'denied', 'invalid', 'abort'].entries()) {
    const actorId = String(BigInt('9007199254741201') + BigInt(index)), locks = exclusiveLocks();
    const started = latch(), proceed = latch(), reading = latch(), abort = new AbortController();
    let tokens = 0, photos = 0, cancelled = 0;
    const calls: string[] = [];
    const fetcher: typeof fetch = async input => {
      assert.ok(input instanceof Request);
      if (input.url.endsWith('/identity/csrf')) {
        calls.push('csrf');
        if (++tokens === 1) { started.release(); await proceed.wait; }
        return Response.json({requestToken: `token-${tokens}`});
      }
      calls.push('photo');
      if (++photos === 1) {
        if (outcome === 'denied') return new Response(null, {status: 403});
        if (outcome === 'invalid') return new Response(new Uint8Array(bytes.length), {headers});
        if (outcome === 'abort') return new Response(new ReadableStream<Uint8Array>({
          pull() { reading.release(); }, cancel() { cancelled++; },
        }, {highWaterMark: 0}), {headers});
      }
      return new Response(bytes, {headers});
    };
    const gateway = () => createBuyerPhotoGateway({origin: 'https://catalog.test', fetch: fetcher, locks});
    const first = gateway().read(actorId, target, () => true, abort.signal); await started.wait;
    const settled = outcome === 'success' ? first.then(result => assert.ok(result)) : assert.rejects(() => first, error => outcome === 'denied'
      ? error instanceof AccessFailure && error.status === 403 : outcome === 'invalid' ? invalid(error) : notSent('buyer_photo_view_changed')(error));
    const next = gateway().read(actorId, target, () => true); assert.equal(tokens, 1);
    proceed.release();
    if (outcome === 'abort') { await reading.wait; abort.abort(); }
    await settled; const result = await next; assert.ok(result); assert.deepEqual(Buffer.from(await result.arrayBuffer()), bytes);
    assert.deepEqual(calls, ['csrf', 'photo', 'csrf', 'photo'], outcome);
    assert.equal(cancelled, outcome === 'abort' ? 1 : 0, outcome);
  }
});

test('the shared per-actor bound rejects reader 33 before fetching and abort restores all 32 slots', {timeout: 5000}, async () => {
  assert.equal(buyerPhotoMaximumPendingReads, 32);
  const actorId = '9007199254741104', locks = exclusiveLocks(), started = latch(), proceed = latch();
  let calls = 0;
  const fetcher: typeof fetch = async input => {
    assert.ok(input instanceof Request);
    if (++calls === 1) { started.release(); await proceed.wait; }
    return input.url.endsWith('/identity/csrf') ? Response.json({requestToken: 'fresh'}) : new Response(null, {status: 204});
  };
  const gateway = () => createBuyerPhotoGateway({origin: 'https://catalog.test', fetch: fetcher, locks});
  const controllers = Array.from({length: buyerPhotoMaximumPendingReads}, () => new AbortController());
  const operations = controllers.map(controller => gateway().read(actorId, target, () => true, controller.signal));
  const settled = Promise.allSettled(operations);
  await started.wait;
  await assert.rejects(() => gateway().read(actorId, target, () => true), notSent('buyer_photo_queue_full'));
  assert.equal(calls, 1);
  for (const controller of controllers) controller.abort();
  proceed.release();
  for (const result of await settled) { assert.equal(result.status, 'rejected'); if (result.status === 'rejected') assert.ok(notSent('buyer_photo_view_changed')(result.reason)); }
  assert.equal(calls, 1);
  const nextRound = Array.from({length: buyerPhotoMaximumPendingReads}, () => gateway().read(actorId, target, () => true));
  assert.deepEqual(await Promise.all(nextRound), Array(buyerPhotoMaximumPendingReads).fill(null));
  assert.equal(calls, 1 + buyerPhotoMaximumPendingReads * 2);
});

test('different actors can read independently while one actor is waiting on CSRF', {timeout: 5000}, async () => {
  const firstActor = '9007199254741105', secondActor = '9007199254741106', locks = exclusiveLocks(), started = latch(), proceed = latch();
  const calls: string[] = [];
  const gateway = (actorId: string) => createBuyerPhotoGateway({origin: 'https://catalog.test', locks, fetch: async input => {
    assert.ok(input instanceof Request);
    if (input.url.endsWith('/identity/csrf')) {
      calls.push(`${actorId}:csrf`);
      if (actorId === firstActor) { started.release(); await proceed.wait; }
      return Response.json({requestToken: 'fresh'});
    }
    calls.push(`${actorId}:photo`); assert.equal(input.headers.get('X-Expected-Actor-Id'), actorId);
    return new Response(bytes, {headers});
  }});
  const first = gateway(firstActor).read(firstActor, target, () => true); await started.wait;
  assert.ok(await gateway(secondActor).read(secondActor, target, () => true));
  assert.deepEqual(calls, [`${firstActor}:csrf`, `${secondActor}:csrf`, `${secondActor}:photo`]);
  proceed.release(); assert.ok(await first);
  assert.deepEqual(locks.names, [`unit-toko:buyer-photo:${firstActor}`, `unit-toko:buyer-photo:${secondActor}`]);
});

test('missing native locks refuses the photo before any fetch', async () => {
  const descriptor = Object.getOwnPropertyDescriptor(globalThis, 'navigator'); let calls = 0;
  Object.defineProperty(globalThis, 'navigator', {value: {}, configurable: true});
  try {
    const gateway = createBuyerPhotoGateway({origin: 'https://catalog.test', fetch: async () => { calls++; throw new Error('must not dispatch'); }});
    await assert.rejects(() => gateway.read(actor, target, () => true), notSent('buyer_photo_lock_unavailable'));
    assert.equal(calls, 0);
  } finally {
    if (descriptor) Object.defineProperty(globalThis, 'navigator', descriptor);
    else Reflect.deleteProperty(globalThis, 'navigator');
  }
});
