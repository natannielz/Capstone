import test from 'node:test';
import assert from 'node:assert/strict';
import {createReplenishmentGateway, isReplenishmentRecoveryKey, parseReplenishmentEvaluation, parseReplenishmentInput, replenishmentRecoveryPrefix, replenishmentValues, ReplenishmentRecovery} from '../src/modules/inventory/replenishment-client';
import {AccessFailure} from '../src/modules/administration/workflow';
import {RequestNotSentError} from '../src/api/client';

const location = {kind: 'fulfillmentWarehouse' as const, organizationId: '100', shopId: '101', warehouseId: '102', channelId: '103', branchId: null, divisionId: null};
const inputs = {dailyDemandPerDay: '4', leadTimeDays: 2, safetyStockPackages: '3', reorderPointPackages: '2', purchaseMinimumPackages: '1', purchaseIncrementPackages: '2'};
const body = {location, poolId: '9007199254741001', expectedPoolRevision: '1', skuId: '99', expectedSkuRevision: '2', definitionId: '100', inputs, inputReference: 'Reviewed synthetic input reference', reason: 'Review purchase need'};
function browserStorage(map = new Map<string, string>()) {return {get length() {return map.size;}, key: (index: number) => [...map.keys()][index] ?? null,
  getItem: (key: string) => map.get(key) ?? null, setItem: (key: string, value: string) => {map.set(key, value);}, removeItem: (key: string) => {map.delete(key);}};}
function example() {return {id: '9007199254741002', location, poolId: body.poolId, poolRevision: '1', skuId: '99', skuRevision: '2', definitionId: '100', definitionVersion: '1',
  skuCode: 'PENCIL-12', packageLabel: 'Box of 12', packageKind: 'sealed', baseUnitCode: 'piece', baseUnitsPerPackage: 12, inputs, inputReference: body.inputReference,
  reason: body.reason, sourceHash: 'a'.repeat(64), ruleVersion: 'replenishment-lead-time-v1', observedAt: '2026-10-08T11:00:00Z', horizon: '2026-10-10T11:00:00Z',
  currentAuthority: true, usableBeforeReservations: '10', reservedPackages: '2', additionalUnreservedCommitments: '0', availableAfterCommitments: '8', thresholdAlert: false,
  eligibleSupplyPackages: '0', inventoryPosition: '8', requiredStockPackages: '11', suggestedPurchasePackages: '4', warnings: [], supplies: []};}

test('blank demand remains unknown, explicit zero remains zero, and purchase increments must be explicitly positive', () => {
  const draft = {dailyDemandPerDay: '', leadTimeDays: '2', safetyStockPackages: '0', reorderPointPackages: '', purchaseMinimumPackages: '1', purchaseIncrementPackages: '12'};
  assert.equal(replenishmentValues(draft).dailyDemandPerDay, null); assert.equal(replenishmentValues(draft).safetyStockPackages, '0');
  assert.equal(replenishmentValues({...draft, dailyDemandPerDay: '0'}).dailyDemandPerDay, '0');
  for (const changed of [{purchaseMinimumPackages: ''}, {purchaseIncrementPackages: '0'}, {leadTimeDays: '-1'}, {reorderPointPackages: '1.2'}, {safetyStockPackages: '9223372036854775808'}])
    assert.throws(() => replenishmentValues({...draft, ...changed}), RequestNotSentError);
  assert.equal(parseReplenishmentInput(body).poolId, body.poolId);
  assert.throws(() => parseReplenishmentInput({...body, verified: true}), RequestNotSentError);
});
test('response keeps long quantities exact and rejects double-counting or a suggestion from unknown source', () => {
  assert.equal(parseReplenishmentEvaluation(example()).id, example().id);
  const bad = [{...example(), availableAfterCommitments: '6'}, {...example(), currentAuthority: false}, {...example(), inventoryPosition: '10'},
    {...example(), thresholdAlert: true}, {...example(), inputs: {...inputs, dailyDemandPerDay: null}}, {...example(), usableBeforeReservations: 'bad'}, {...example(), extra: true}];
  for (const item of bad) assert.throws(() => parseReplenishmentEvaluation(item), (error: unknown) => error instanceof AccessFailure && error.code === 'replenishment_response_invalid');
  const unknown = {...example(), inputs: {...inputs, dailyDemandPerDay: null}, requiredStockPackages: null, suggestedPurchasePackages: null, warnings: ['daily_demand_missing']};
  assert.equal(parseReplenishmentEvaluation(unknown).thresholdAlert, false);
});
test('unreviewed, late or post-horizon incoming supply cannot be displayed as eligible', () => {
  for (const change of [{verified: false}, {expectedAt: '2026-10-08T10:00:00Z'}, {expectedAt: '2026-10-11T11:00:00Z'}]) {
    const supply = {reference: 'purchase-order:1', packages: '2', expectedAt: '2026-10-09T11:00:00Z', verified: true, state: 'eligible', ...change};
    assert.throws(() => parseReplenishmentEvaluation({...example(), supplies: [supply]}), AccessFailure);
  }
});
test('lost response is never automatically retried and durable recovery stores only account/key/time', async () => {
  let calls = 0; const gateway = createReplenishmentGateway(async input => {
    if (input instanceof Request) return new Response(JSON.stringify({requestToken: 'csrf-fixture'})); calls++; throw new TypeError('lost response');
  }, 'http://localhost/api/v1/identity/csrf');
  await assert.rejects(gateway.evaluate(body, 'review-key', {actorId: '44', isCurrent: () => true})); assert.equal(calls, 1);
  const map = new Map<string, string>(), storage = browserStorage(map);
  const first = new ReplenishmentRecovery(() => storage); first.save({actorId: '44', key: 'review-key', createdAt: '2026-10-08T11:00:00Z'});
  assert.equal(new ReplenishmentRecovery(() => storage).read('44')?.key, 'review-key'); assert.equal(first.read('45'), null);
  assert.deepEqual(Object.keys(JSON.parse([...map.values()][0])).sort(), ['actorId', 'createdAt', 'key']);
  assert.throws(() => first.save({actorId: '44', key: 'other-key', createdAt: '2026-10-08T11:00:00Z'}));
  first.clear('44', 'other-key'); assert.ok(first.read('44')); first.clear('44', 'review-key'); assert.equal(first.read('44'), null);
});

test('two tabs that both observe an empty journal retain distinct original keys and clear only their confirmed result', () => {
  const map = new Map<string, string>(), storage = browserStorage(map), first = new ReplenishmentRecovery(() => storage), second = new ReplenishmentRecovery(() => storage);
  const one = {actorId: '44', key: 'one', createdAt: '2026-10-08T11:00:00Z'}, two = {...one, key: 'two', createdAt: '2026-10-08T11:00:01Z'};
  assert.equal(first.read('44'), null); assert.equal(second.read('44'), null);
  const write = storage.setItem; let interleave = true;
  storage.setItem = (key, value) => {if (interleave) {interleave = false; second.save(two);} write(key, value);};
  first.save(one);
  assert.deepEqual(new ReplenishmentRecovery(() => browserStorage(map)).list('44'), [one, two]);
  first.clear('44', one.key); assert.deepEqual(second.read('44'), two); assert.equal(map.size, 1);
  assert.throws(() => first.save({...one, key: 'third'}));
  second.clear('44', two.key); assert.equal(first.read('44'), null); assert.equal(map.size, 0);
});

test('old single-key references remain recoverable alongside new keys without silently replacing either', () => {
  const map = new Map<string, string>(), storage = browserStorage(map), recovery = new ReplenishmentRecovery(() => storage);
  const old = {actorId: '44', key: 'old', createdAt: '2026-10-08T10:00:00Z'}, newer = {...old, key: 'new', createdAt: '2026-10-08T11:00:00Z'};
  storage.setItem('unit-toko:replenishment-pending:v1:44', JSON.stringify(old));
  storage.setItem(replenishmentRecoveryPrefix('44') + newer.key, JSON.stringify(newer));
  assert.deepEqual(recovery.list('44'), [old, newer]); assert.throws(() => recovery.save({...newer, key: 'third'}));
  recovery.clear('44', 'old'); assert.deepEqual(recovery.read('44'), newer);
  assert.equal(isReplenishmentRecoveryKey('44', 'unit-toko:replenishment-pending:v1:44'), true);
  assert.equal(isReplenishmentRecoveryKey('44', replenishmentRecoveryPrefix('44') + newer.key), true);
  assert.equal(isReplenishmentRecoveryKey('44', null), true);
  assert.equal(isReplenishmentRecoveryKey('44', replenishmentRecoveryPrefix('45') + newer.key), false);
});

test('malformed, overwritten or unavailable browser references cannot enable another command or clear pending evidence', () => {
  const map = new Map<string, string>(), storage = browserStorage(map), recovery = new ReplenishmentRecovery(() => storage);
  const entry = {actorId: '44', key: 'original', createdAt: '2026-10-08T11:00:00Z'}, key = replenishmentRecoveryPrefix('44') + entry.key;
  recovery.save(entry); assert.throws(() => recovery.save({...entry, createdAt: '2026-10-08T11:00:01Z'}));
  storage.setItem(key, '{bad'); assert.throws(() => recovery.read('44')); assert.throws(() => recovery.clear('44', entry.key)); assert.equal(storage.getItem(key), '{bad');
  storage.setItem(key, JSON.stringify({...entry, key: 'another'})); assert.throws(() => recovery.save(entry));
  const unavailable = new ReplenishmentRecovery(() => {throw new Error('Storage denied');}); assert.throws(() => unavailable.read('44'));
  map.clear(); storage.setItem = () => {}; assert.throws(() => recovery.save(entry));
});
test('changed actor/session during CSRF lookup prevents a command and wrong selected result is rejected', async () => {
  let current = true, posts = 0; const stale = createReplenishmentGateway(async input => {
    if (input instanceof Request) {current = false; return new Response(JSON.stringify({requestToken: 'csrf-fixture'}));} posts++; throw new Error('must not send');
  }, 'http://localhost/api/v1/identity/csrf');
  await assert.rejects(stale.evaluate(body, 'review-key', {actorId: '44', isCurrent: () => current}), RequestNotSentError); assert.equal(posts, 0);
  const wrong = createReplenishmentGateway(async input => input instanceof Request ? new Response(JSON.stringify({requestToken: 'csrf-fixture'})) : new Response(JSON.stringify({...example(), poolId: '22'})), 'http://localhost/api/v1/identity/csrf');
  await assert.rejects(wrong.evaluate(body, 'review-key', {actorId: '44', isCurrent: () => true}), AccessFailure);
});
test('refresh/recovery returns the saved observation without making a new evaluation', async () => {
  const calls: string[] = []; const gateway = createReplenishmentGateway(async input => {
    if (input instanceof Request) return new Response(JSON.stringify({requestToken: 'csrf-fixture'})); calls.push(String(input));
    return new Response(JSON.stringify(String(input).endsWith('/operations/query') ? {state: 'committed', evaluation: example()} : example()));
  }, 'http://localhost/api/v1/identity/csrf');
  const ctx = {actorId: '44', isCurrent: () => true}; assert.equal((await gateway.recover('review-key', ctx))?.id, example().id);
  assert.equal((await gateway.read(example().id, ctx))?.id, example().id); assert.equal(calls.length, 2); assert.ok(calls.every(path => path.endsWith('/query')));
});

test('a changed session after sending retains an uncertain outcome and a saved read must match its requested identity', async () => {
  let current = true, posts = 0;
  const gateway = createReplenishmentGateway(async input => {
    if (input instanceof Request) return new Response(JSON.stringify({requestToken: 'csrf-fixture'}));
    posts++; current = false; return new Response(JSON.stringify(example()));
  }, 'http://localhost/api/v1/identity/csrf');
  await assert.rejects(gateway.evaluate(body, 'original-review', {actorId: '44', isCurrent: () => current}),
    (failure: unknown) => failure instanceof AccessFailure && failure.status === 502 && !(failure instanceof RequestNotSentError));
  assert.equal(posts, 1);
  const reader = createReplenishmentGateway(async input => input instanceof Request ? new Response(JSON.stringify({requestToken: 'csrf-fixture'})) : new Response(JSON.stringify(example())), 'http://localhost/api/v1/identity/csrf');
  await assert.rejects(reader.read('21', {actorId: '44', isCurrent: () => true}), AccessFailure);
});

test('history keeps exact descending cursors and returns only recorded reviews from the chosen warehouse', async () => {
  const items = Array.from({length: 10}, (_, index) => ({...example(), id: (BigInt('9007199254742000') - BigInt(index)).toString()}));
  const calls: {path: string; body: unknown; key: string | null}[] = [];
  const gateway = createReplenishmentGateway(async (input, init) => {
    if (input instanceof Request) return new Response(JSON.stringify({requestToken: 'csrf-fixture'}));
    calls.push({path: String(input), body: JSON.parse(String(init?.body)), key: new Headers(init?.headers).get('Idempotency-Key')});
    return new Response(JSON.stringify({items, nextBefore: items.at(-1)!.id}));
  }, 'http://localhost/api/v1/identity/csrf');
  const page = await gateway.history(location, '9007199254743000', {actorId: '44', isCurrent: () => true});
  assert.deepEqual(page.items.map(item => item.id), items.map(item => item.id));
  assert.equal(page.nextBefore, items.at(-1)!.id);
  assert.equal(calls.length, 1); assert.ok(calls[0].path.endsWith('/history/query')); assert.equal(calls[0].key, null);
  assert.deepEqual(calls[0].body, {purpose: 'inventory-replenishment', location, before: '9007199254743000', limit: 10});
});

test('history refuses foreign location, duplicates, reverse ordering, invalid cursor and inconsistent page hints', async () => {
  const one = example(), two = {...example(), id: (BigInt(example().id) - BigInt(1)).toString()};
  const pages = [
    {items: [{...one, location: {...location, warehouseId: '999'}}], nextBefore: null},
    {items: [one, one], nextBefore: null}, {items: [two, one], nextBefore: null},
    {items: [one], nextBefore: one.id}, {items: [], nextBefore: one.id},
    {items: [one], nextBefore: null, total: 1}, {items: [{...one, inventoryPosition: '99'}], nextBefore: null},
  ];
  for (const page of pages) {
    const gateway = createReplenishmentGateway(async input => new Response(JSON.stringify(input instanceof Request ? {requestToken: 'csrf-fixture'} : page)), 'http://localhost/api/v1/identity/csrf');
    await assert.rejects(gateway.history(location, null, {actorId: '44', isCurrent: () => true}), AccessFailure);
  }
  const gateway = createReplenishmentGateway(async input => new Response(JSON.stringify(input instanceof Request ? {requestToken: 'csrf-fixture'} : {items: [one], nextBefore: null})), 'http://localhost/api/v1/identity/csrf');
  await assert.rejects(gateway.history(location, one.id, {actorId: '44', isCurrent: () => true}), AccessFailure);
  await assert.rejects(gateway.history(location, '0', {actorId: '44', isCurrent: () => true}), RequestNotSentError);
});
