import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure } from '../src/modules/administration/workflow';
import { OperationJournal, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';
import { StockCountEditor } from '../src/modules/inventory/stock-count-editor-state';
import { createStockCountGateway, createStockSourceQueryTransport, type StockCountGateway } from '../src/modules/inventory/stock-count-client';
import { parseStockSources, parseStockObservation, prepareStockCount, stockCountOutcome } from '../src/modules/inventory/stock-count-workflow';
import type { IdentitySession } from '../src/modules/identity/identity-workflow';
import { actorId, location, warehouse, choice, observation, reference } from './stock-count-fixtures';

// UNEXECUTED isolated native-domain tests; not browser/provider or database acceptance.
const pool = { poolId: '9007199254741011', poolRevision: '9007199254741013', setupDecisionId: '9007199254741015', configurationHash: 'a'.repeat(64),
  physicalKey: 'gudang-tebet-atk', label: 'Rak ATK gudang Tebet', active: false };
const source = { pool, requestedLocation: location }, page = { total: '1', items: [source] };
const bound = { ...observation, poolBinding: pool };
const draft = { packages: '0', reason: 'Physical count', observedLotLabel: '' };
const session: IdentitySession = { authenticated: true, account: { userId: actorId, displayName: 'Petugas', verifiedEmail: 'synthetic@example.test' },
  serverTime: '2026-09-28T01:00:00Z', factorCompletedAt: '2026-09-28T01:00:00Z', freshFactorUntil: '2026-09-28T01:05:00Z',
  idleExpiresAt: '2026-09-28T01:10:00Z', absoluteExpiresAt: '2026-09-28T02:00:00Z', stepUpOperations: [] };
const prepare = () => prepareStockCount(actorId, location, choice, draft, source);

test('source selection is scoped, lossless and immutable while omitted legacy bodies/projections remain exact', () => {
  assert.deepEqual(parseStockSources(page, location), page);
  const command = prepare(); assert.deepEqual(command.body.poolSelection, { poolId: pool.poolId, expectedPoolRevision: pool.poolRevision, configurationHash: pool.configurationHash });
  assert.ok(Object.isFrozen(command.source)); assert.ok(Object.isFrozen(command.source!.pool)); assert.ok(Object.isFrozen(command.source!.requestedLocation)); assert.ok(Object.isFrozen(command.body.poolSelection));
  assert.deepEqual(stockCountOutcome(bound, command), bound);
  const legacy = prepareStockCount(actorId, location, choice, draft);
  assert.equal('poolSelection' in legacy.body, false); assert.equal('source' in legacy, false);
  assert.deepEqual(parseStockObservation(observation), observation); assert.deepEqual(stockCountOutcome(observation, legacy), observation);
  assert.throws(() => stockCountOutcome(bound, legacy)); assert.throws(() => stockCountOutcome(observation, command));
  for (const bad of [null, { ...pool, poolId: 9007199254741011 }, { ...pool, poolRevision: '0' }, { ...pool, configurationHash: 'A'.repeat(64) },
    { ...pool, configurationHash: 'a'.repeat(64) + '\n' }, { ...pool, active: 'true' }, { ...pool, availablePackages: 20 }])
    assert.throws(() => parseStockObservation({ ...observation, poolBinding: bad }));
});

test('foreign requested warehouse, duplicate pool and changed historical binding cannot confirm reviewed source', () => {
  assert.throws(() => parseStockSources({ total: '1', items: [{ ...source, requestedLocation: { ...location, warehouseId: '109' } }] }, location));
  assert.throws(() => parseStockSources({ total: '2', items: [source, source] }, location));
  for (const changed of [{ poolId: '8' }, { poolRevision: '8' }, { configurationHash: 'b'.repeat(64) }, { setupDecisionId: '8' },
    { physicalKey: 'other' }, { label: 'Gudang lain' }, { active: true }]) assert.throws(() => stockCountOutcome({ ...bound, poolBinding: { ...pool, ...changed } }, prepare()));
  assert.throws(() => prepareStockCount(actorId, { ...location, warehouseId: '109' }, choice, draft, source));
  const labelled = prepareStockCount(actorId, location, choice, { ...draft, observedLotLabel: pool.physicalKey });
  assert.equal('poolSelection' in labelled.body, false, 'free-text physical label never binds a canonical pool');
});

test('maintained source query uses exact purpose/location/paging and current same-origin CSRF admission', async () => {
  const requests: Request[] = []; let current = true;
  const transport = createStockSourceQueryTransport({ origin: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); assert.equal(request.credentials, 'same-origin'); assert.equal(request.cache, 'no-store'); assert.equal(request.redirect, 'error');
    if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'fresh-source-csrf' }); requests.push(request);
    assert.equal(new URL(request.url).pathname, '/api/v1/inventory/stock-counts/sources/query');
    assert.equal(request.method, 'POST'); assert.equal(request.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(request.headers.get('X-CSRF-Token'), 'fresh-source-csrf');
    assert.deepEqual(await request.json(), { purpose: 'inventory-stock-count', location, offset: 0, limit: 25 }); return Response.json(page);
  } });
  const gateway = createStockCountGateway(createUnitTokoClient(), transport);
  assert.deepEqual(await gateway.sources(actorId, location, 0, undefined, () => current), page); assert.equal(requests.length, 1);
  current = false; await assert.rejects(() => gateway.sources(actorId, location, 0, undefined, () => current), error => error instanceof RequestNotSentError);
  assert.equal(requests.length, 1);
  const stale = createStockCountGateway(createUnitTokoClient(), createStockSourceQueryTransport({ origin: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) { current = false; return Response.json({ requestToken: 'source-csrf' }); }
    assert.fail('replaced source query must remain unsent');
  } })); current = true;
  await assert.rejects(() => stale.sources(actorId, location, 0, undefined, () => current), error => error instanceof RequestNotSentError);
});

test('count tuple is frozen before CSRF; a lost reply retries only original payload and key, changed source response remains unknown', async () => {
  let attempts = 0, current = true; const bodies: unknown[] = [], keys: string[] = [];
  const gateway = createStockCountGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'count-csrf' });
    attempts++; bodies.push(await request.clone().json()); keys.push(request.headers.get('Idempotency-Key')!);
    if (attempts === 1) throw new Error('reply lost'); return Response.json(bound);
  } }));
  const original = prepare(); await assert.rejects(() => gateway.submit(original, reference.key, () => current), /reply lost/);
  assert.deepEqual(await gateway.submit(original, reference.key, () => current), bound);
  assert.deepEqual(bodies, [original.body, original.body]); assert.deepEqual(keys, [reference.key, reference.key]);
  let writes = 0;
  const expired = createStockCountGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) { current = false; return Response.json({ requestToken: 'count-csrf' }); }
    writes++; return Response.json(bound);
  } })); current = true;
  await assert.rejects(() => expired.submit(original, reference.key, () => current), error => error instanceof RequestNotSentError);
  assert.equal(writes, 0);
  const wrong = createStockCountGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); return Response.json(request.url.endsWith('/identity/csrf') ? { requestToken: 'csrf' } : { ...bound, poolBinding: { ...pool, active: true } });
  } })); await assert.rejects(() => wrong.submit(original, reference.key), /stock_count_response_invalid/);
});

function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(r => { resolve = r; }); return { promise, resolve }; }
function editorFixture(overrides: Partial<StockCountGateway> = {}, actions = ['stock.count', 'stock.read'], accountSession = session) {
  const values = new Map<string, string>(); const storage = { get length() { return values.size; }, key: (i: number) => [...values.keys()][i] ?? null,
    getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); }, removeItem: (key: string) => { values.delete(key); } };
  const journal = new OperationJournal(() => storage); let now = 0;
  const gateway: StockCountGateway = { warehouses: async () => [warehouse, { ...warehouse, location: { ...location, warehouseId: '109' } }], sources: async () => page,
    packages: async () => ({ total: '1', items: [choice] }), history: async () => ({ total: '1', items: [observation] }), observation: async () => observation,
    submit: async () => bound, receipt: async () => null, ...overrides };
  const editor = new StockCountEditor({ actorId, actions, session: accountSession, observedAt: 0, now: () => now, gateway, journal, newKey: () => reference.key });
  return { editor, journal, values, advance: (elapsed: number) => { now = elapsed; } };
}
async function select(editor: StockCountEditor) { await editor.start(); await editor.chooseWarehouse(location); editor.chooseSource(pool.poolId);
  editor.choosePackage(choice.skuId); editor.changeDraft('packages', '0'); editor.changeDraft('reason', 'Physical count'); }

test('editor never selects first pool; warehouse/source changes clear dependent choices and stale source response is discarded', async () => {
  const data = editorFixture(); await data.editor.start(); await data.editor.chooseWarehouse(location);
  assert.equal(data.editor.snapshot().selectedSource, null); assert.equal(data.editor.snapshot().sourceMode, null);
  data.editor.choosePackage(choice.skuId); data.editor.changeDraft('packages', '0'); data.editor.changeDraft('reason', 'Physical count'); data.editor.reviewCount();
  assert.equal(data.editor.snapshot().review, null); assert.equal((data.editor.snapshot().error as AccessFailure).code, 'stock_count_source_selection_required');
  data.editor.chooseSource(pool.poolId); assert.equal(data.editor.snapshot().selectedPackage, null); assert.equal(data.editor.snapshot().draft.packages, '');
  data.editor.chooseSource('unbound'); assert.equal(data.editor.snapshot().selectedSource, null); assert.equal(data.editor.snapshot().sourceMode, 'unbound');
  await data.editor.chooseWarehouse(null); assert.equal(data.editor.snapshot().sourceMode, null); assert.deepEqual(data.editor.snapshot().sources.items, []);
  const old = deferred<typeof page>(); const replaced = editorFixture({ sources: async (_actor, where) => where.warehouseId === location.warehouseId ? old.promise : { total: '0', items: [] } });
  await replaced.editor.start(); const waiting = replaced.editor.chooseWarehouse(location);
  assert.equal(replaced.editor.snapshot().loadingSources, true);
  await replaced.editor.chooseWarehouse({ ...location, warehouseId: '109' }); old.resolve(page); await waiting;
  assert.deepEqual(replaced.editor.snapshot().sources.items, []); assert.equal(replaced.editor.snapshot().selectedSource, null);
});

test('bound uncertain attempt blocks source edits; permission denial does not dispatch replacement work', async () => {
  const sent: unknown[] = []; let calls = 0;
  const data = editorFixture({ submit: async (command, key) => { sent.push({ command, key }); calls++; if (calls === 1) throw new AccessFailure(503, 'commit_outcome_unknown'); return bound; } });
  await select(data.editor); data.editor.reviewCount(); await data.editor.submitReviewed(true);
  const frozen = data.editor.snapshot().attempt; assert.ok(frozen); data.editor.chooseSource('unbound'); await data.editor.loadSources();
  assert.equal(data.editor.snapshot().attempt, frozen); await data.editor.retry(); assert.equal(calls, 2); assert.deepEqual(sent[0], sent[1]);
  assert.equal(data.journal.list(actorId).length, 0); assert.equal(data.editor.snapshot().result!.poolBinding!.poolId, pool.poolId);
  assert.doesNotMatch([...data.values.values()].join(''), /poolId|configurationHash|physicalKey|packages|reason/);
  const denied = editorFixture({ sources: async () => { throw new AccessFailure(403, 'scope_denied'); } });
  await denied.editor.start(); await denied.editor.chooseWarehouse(location); assert.equal(denied.editor.snapshot().suspended, true); assert.equal(denied.editor.snapshot().selectedSource, null);
  const readOnly = editorFixture({ sources: async () => { assert.fail('history-only account must not query count sources'); } }, ['stock.read']);
  await readOnly.editor.start(); await readOnly.editor.chooseWarehouse(location); assert.deepEqual(readOnly.editor.snapshot().history.items, [observation]);
  await assert.rejects(() => readOnly.editor.receipt(reference), /access_denied/);
});

test('five-minute factor freshness expiration alone permits source choices, review, submission and original receipt lookup', async () => {
  let choices = 0, writes = 0, lookups = 0;
  const receipt = { state: 'committed' as const, result: { id: observation.id, version: '1' as const }, recordedAt: observation.recordedAt };
  const data = editorFixture({ sources: async () => { choices++; return page; }, submit: async () => { writes++; return bound; },
    receipt: async () => { lookups++; return receipt; } });
  data.advance(300000); // freshFactorUntil reached, but authenticated ordinary-MFA session remains valid.
  await select(data.editor); assert.equal(choices, 1); data.editor.reviewCount(); assert.ok(data.editor.snapshot().review);
  await data.editor.submitReviewed(true); assert.equal(writes, 1); assert.deepEqual(data.editor.snapshot().result, bound);
  assert.deepEqual(await data.editor.receipt(reference), receipt); assert.equal(lookups, 1); assert.equal(data.editor.snapshot().expired, false);
});

test('actual idle and absolute session expiry conceal source choices and deny reviewed submission or receipt lookup', async () => {
  for (const accountSession of [session, { ...session, idleExpiresAt: '2026-09-28T03:00:00Z', absoluteExpiresAt: '2026-09-28T01:10:00Z' }]) {
    let writes = 0, lookups = 0;
    const data = editorFixture({ submit: async () => { writes++; return bound; }, receipt: async () => { lookups++; return null; } }, ['stock.count', 'stock.read'], accountSession);
    await select(data.editor); data.editor.reviewCount(); assert.ok(data.editor.snapshot().review);
    data.advance(600000); data.editor.checkSession(); await data.editor.submitReviewed(true);
    assert.equal(data.editor.snapshot().expired, true); assert.equal(data.editor.snapshot().review, null); assert.deepEqual(data.editor.snapshot().sources.items, []);
    assert.equal(writes, 0); await assert.rejects(() => data.editor.receipt(reference), /recovery_request_stale/); assert.equal(lookups, 0);
  }
});

test('source-bound count recovery remains metadata-only and lookup-only after reload', async () => {
  const data = editorFixture(); const entry = data.journal.remember(actorId, reference.operation, reference.key);
  const stored = [...data.values.values()][0]; assert.deepEqual(Object.keys(JSON.parse(stored)).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  const legacy = { lookup: async () => { assert.fail('wrong recovery owner'); }, close: async () => { assert.fail('count observation cannot be closed'); } };
  const adapter = { 'inventory.stock-count': { lookup: async () => ({ state: 'committed' as const, result: { id: observation.id, version: '1' as const }, recordedAt: observation.recordedAt }) } };
  await assert.rejects(() => reconcileOperationRecovery(entry, data.journal, legacy, adapter, 'close'));
  assert.equal(data.journal.list(actorId).length, 1); await reconcileOperationRecovery(entry, data.journal, legacy, adapter);
  assert.equal(data.journal.list(actorId).length, 0);
});

test('unavailable legacy source heads and malformed source projection permit only a deliberate unbound observation', async () => {
  for (const error of [new AccessFailure(503, 'inventory_source_unavailable'), new AccessFailure(502, 'stock_count_response_invalid')]) {
    const data = editorFixture({ sources: async () => { throw error; }, submit: async command => {
      assert.equal('poolSelection' in command.body, false); assert.equal('source' in command, false); return observation;
    } });
    await data.editor.start(); await data.editor.chooseWarehouse(location);
    assert.equal(data.editor.snapshot().suspended, false); assert.equal(data.editor.snapshot().sourceMode, null); assert.equal(data.editor.snapshot().error, error);
    data.editor.chooseSource('unbound'); data.editor.choosePackage(choice.skuId); data.editor.changeDraft('packages', '0'); data.editor.changeDraft('reason', 'Physical count');
    data.editor.reviewCount(); assert.ok(data.editor.snapshot().review); await data.editor.submitReviewed(true);
    assert.deepEqual(data.editor.snapshot().result, observation);
  }
});
