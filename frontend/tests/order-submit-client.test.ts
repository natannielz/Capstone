import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { checkoutGroupRequestKey, createOrderSubmitGateway, orderSubmitOperation, parseCheckoutGroups, parseCheckoutReview, parseSubmittedOrder, parseOrderPage, readCheckoutGroupAttempt } from '../src/modules/ordering/order-submit-client';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
import { identityReturnPath } from '../src/modules/identity/identity-workflow';
import { actorId, family, sku, definition } from './catalog-fixtures';
const input = { context: { organizationId: '10', branchId: '11', divisionId: null }, selections: [
  { skuId: sku.id, definitionId: definition.id, definitionVersion: definition.version, priceId: '9007199254740999', priceVersion: '2', packages: 2 } ],
  delivery: { recipientName: 'Penerima fixture', phone: '081234567890', address: 'Alamat fixture', city: 'Jakarta', postalCode: '12810', instructions: null }, purchaseReference: null };
const review = { input, review: 'a'.repeat(64), calculatedAt: definition.effectiveAt, currency: 'IDR', catalogSubtotal: '24000.50', tax: '0.00', charges: '1500.00', total: '25500.50', spendingState: 'Pending',
  lines: [{ familyId: family.id, familyName: family.name, publicId: sku.publicId, skuCode: sku.code, definition,
    price: { id: input.selections[0].priceId, skuId: sku.id, version: '2', currency: 'IDR', unitPrice: '12000.25', effectiveAt: definition.effectiveAt },
    packages: 2, baseUnits: '24', catalogSubtotal: '24000.50' }] };
const result = { orderId: '9007199254741001', revision: '1', suborderIds: ['9007199254741002'], recordedAt: definition.effectiveAt, spendingState: 'Pending' as const };
function gateway(handler: (request: Request) => Promise<Response>) { return createOrderSubmitGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
  assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
  assert.equal(request.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); return handler(request);
} })); }
test('order history keeps exact accepted snapshots and rejects malformed, repeated or out-of-order pages', async () => {
  const row = { ...review, buyerName: 'Cabang fixture', submission: result, progress: { revision: '1', spendingState: 'Pending', acceptedSuborders: [], rejectedSuborders: [], cancelled: false } };
  const older = { ...row, submission: { ...result, orderId: '9007199254741000' } };
  assert.equal(identityReturnPath('/ordering/orders'), '/ordering/orders');
  assert.deepEqual(parseOrderPage({ items: [row, older], hasMore: false }).items.map(item => item.submission.orderId), [result.orderId, older.submission.orderId]);
  for (const page of [{ items: [row, row], hasMore: false }, { items: [older, row], hasMore: false }, { items: [row], hasMore: true },
    { items: [{ ...row, buyerName: '' }], hasMore: false }]) assert.throws(() => parseOrderPage(page));
  assert.throws(() => parseOrderPage({ items: [row], hasMore: false }, result.orderId));
  const saved = await gateway(async request => { assert.ok(request.url.endsWith('/orders/query'));
    assert.deepEqual(await request.json(), { purpose: 'order-preparation', context: null, beforeId: result.orderId, limit: 25 }); return Response.json({ items: [older], hasMore: false }); }).list(actorId, result.orderId);
  assert.equal(saved.items[0].total, '25500.50');
});
test('checkout review binds exact branch, delivery, packaging, approved prices and checked decimal total', () => {
  const parsed = parseCheckoutReview(review, input); assert.equal(parsed.total, '25500.50'); assert.equal(parsed.quote.lines[0].baseUnits, '24');
  for (const change of [ { ...review, input: { ...input, context: { ...input.context, branchId: '12' } } },
    { ...review, input: { ...input, delivery: { ...input.delivery, address: 'Another address' } } }, { ...review, total: '0.00' }, { ...review, tax: null },
    { ...review, review: 'a'.repeat(63) }, { ...review, spendingState: 'Approved' }, { ...review, lines: [{ ...review.lines[0], packages: 3 }] } ])
    assert.throws(() => parseCheckoutReview(change, input));
  assert.equal(parseSubmittedOrder({ ...review, buyerName: 'Cabang fixture', submission: result, progress: { revision: '1', spendingState: 'Pending', acceptedSuborders: [], rejectedSuborders: [], cancelled: false } }).submission.orderId, result.orderId);
  assert.equal(parseSubmittedOrder({ ...review, buyerName: 'Cabang fixture', submission: result, progress: { revision: '2', spendingState: 'Rejected', acceptedSuborders: [], rejectedSuborders: [], cancelled: false } }).progress.spendingState, 'Rejected');
  assert.throws(() => parseSubmittedOrder({ ...review, buyerName: 'Cabang fixture', submission: result, progress: { revision: '3', spendingState: 'Rejected', acceptedSuborders: [], rejectedSuborders: [], cancelled: true } }));
});
test('a substituted order keeps parsing after its renewed spending and store reviews raise the decision revision', () => {
  const accepted = (revision: string) => ({ ...review, buyerName: 'Cabang fixture', submission: result,
    progress: { revision, spendingState: 'Approved', acceptedSuborders: [result.suborderIds[0]], rejectedSuborders: [], cancelled: false } });
  assert.equal(parseSubmittedOrder(accepted('3')).progress.revision, '3');
  // Accepted substitution, renewed spending approval and renewed store acceptance each add a revision.
  assert.equal(parseSubmittedOrder(accepted('6')).progress.revision, '6');
  assert.throws(() => parseSubmittedOrder(accepted('2')));
});
test('order transport sends only accepted references and never retries a lost submission or sends from a stale view', async () => {
  let calls = 0, sentDraft: unknown; const checked = parseCheckoutReview(review, input);
  const client = gateway(async request => { calls++; assert.equal(request.headers.get('Idempotency-Key'), 'original'); assert.equal(request.headers.get('If-Match'), '"0"');
    const body = await request.json() as Record<string, unknown>; assert.deepEqual(body.input, input); assert.equal(body.review, review.review); assert.equal('total' in body, false);
    sentDraft = body.draftId; throw new Error('Lost response'); });
  await assert.rejects(() => client.submit(actorId, checked, 'original', () => false, '7')); assert.equal(calls, 0);
  await assert.rejects(() => client.submit(actorId, checked, 'original', () => true, '7')); assert.equal(calls, 1);
  // F-030: the server learns which saved draft became the order, so that draft is never the keranjang again.
  assert.equal(sentDraft, '7');
  await assert.rejects(() => client.submit(actorId, checked, 'original', () => true, 'draft-7')); assert.equal(calls, 1);
  await assert.rejects(() => gateway(async () => Response.json({ ...review, buyerName: 'Cabang fixture', submission: result })).read(actorId, '7'));
});
test('order recovery is lookup-only, preserves unknown outcomes and rejects an incomplete receipt', async () => {
  assert.equal(identityReturnPath('/ordering/checkout'), '/ordering/checkout'); assert.equal(recoveryPage('ordering.order.submit').href, '/ordering/checkout');
  const storage = new Map<string, string>(); const journal = new OperationJournal(() => ({ get length() { return storage.size; }, key: index => [...storage.keys()][index] ?? null,
    getItem: key => storage.get(key) ?? null, setItem: (key, value) => { storage.set(key, value); }, removeItem: key => { storage.delete(key); } }));
  const entry = journal.remember(actorId, 'ordering.order.submit', 'original'); assert.deepEqual(Object.keys(JSON.parse([...storage.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  const legacy = { lookup: async () => { throw new Error('Legacy lookup'); }, close: async () => { throw new Error('Legacy close'); } };
  assert.deepEqual(operationRecoveryCapabilities(entry.operation), { canCheck: false, canClose: false, unavailable: 'adapter' });
  const pending = { 'ordering.order': { lookup: async () => null } };
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, pending), { state: 'unconfirmed' }); assert.equal(journal.list(actorId).length, 1);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, pending, 'Close'), { code: 'recovery_close_unsupported' });
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'ordering.order': { lookup: async () => ({ state: 'committed', result: { ...result, suborderIds: [] } }) } }));
  assert.equal(journal.list(actorId).length, 1);
  const receipt = await gateway(async request => { assert.ok(request.url.endsWith('/operations/query')); assert.deepEqual(await request.json(), { purpose: 'operation-recovery', key: 'original' }); return Response.json({ status: 'committed', result }); }).recover(actorId, 'original');
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'ordering.order': { lookup: async () => receipt } }), { state: 'committed', result: { id: result.orderId, version: '1' } });
  assert.equal(journal.list(actorId).length, 0);
});

function groupedFixture() {
  const secondSelection = { ...input.selections[0], skuId: '9007199254740998', definitionId: '52', priceId: '9007199254741000', packages: 3 };
  const secondLine = { ...review.lines[0], skuCode: 'PENA-LEPAS', definition: { ...definition, id: '52', skuId: secondSelection.skuId },
    price: { ...review.lines[0].price, id: secondSelection.priceId, skuId: secondSelection.skuId }, packages: 3, baseUnits: '36', catalogSubtotal: '36000.75' };
  const selected = { ...input, selections: [...input.selections, secondSelection] };
  const item = { groupKey: '100:101:103:IDR', organizationId: '100', shopId: '101', channelId: '103', currency: 'IDR',
    organizationName: 'Penjual fixture', shopName: 'Omi fixture', channelName: 'Omi', input, catalogSubtotal: review.catalogSubtotal, lines: review.lines };
  const other = { ...item, groupKey: '100:201:203:IDR', shopId: '201', channelId: '203', shopName: 'S-Mart fixture', channelName: 'S-Mart',
    input: { ...input, selections: [secondSelection] }, catalogSubtotal: secondLine.catalogSubtotal, lines: [secondLine] };
  return { selected, value: { context: input.context, calculatedAt: review.calculatedAt, items: [item, other] } };
}
test('reviewed channel discovery preserves exact package subsets, buyer and delivery without aggregate commercial authority', async () => {
  const { selected, value } = groupedFixture(), parsed = parseCheckoutGroups(value, selected);
  assert.deepEqual(parsed.items.map(item => item.input.selections.map(line => line.skuId)), [[sku.id], ['9007199254740998']]);
  assert.deepEqual(parsed.items.map(item => item.quote.catalogSubtotal), ['24000.50', '36000.75']);
  const item = value.items[0], other = value.items[1];
  for (const malformed of [
    { ...value, items: [item] }, { ...value, items: [item, item] },
    { ...value, items: [{ ...item, groupKey: '100:101:999:IDR' }, other] },
    { ...value, items: [{ ...item, input: { ...item.input, delivery: { ...input.delivery, address: 'Changed' } } }, other] },
    { ...value, items: [{ ...item, input: { ...item.input, selections: [{ ...item.input.selections[0], packages: 1 }] } }, other] },
    { ...value, items: [{ ...item, lines: [{ ...item.lines[0], catalogSubtotal: '1.00' }] }, other] },
    { ...value, items: [{ ...item, shopName: 'Bad\nname' }, other] },
  ]) assert.throws(() => parseCheckoutGroups(malformed, selected));
  const loaded = await gateway(async request => {
    assert.ok(request.url.endsWith('/checkout/groups/query')); assert.deepEqual(await request.json(), { purpose: 'order-preparation', input: selected });
    return Response.json(value);
  }).groups(actorId, selected);
  assert.equal(loaded.items.length, 2); assert.equal('policy' in loaded.items[0], false);
});
test('saved draft channel keys separate actors and groups; partial commit and lost reply are recovered without resubmission', async () => {
  const { value } = groupedFixture(), a = value.items[0].groupKey, b = value.items[1].groupKey;
  const firstKey = await checkoutGroupRequestKey(actorId, '7', '1', a), secondKey = await checkoutGroupRequestKey(actorId, '7', '1', b);
  assert.equal(firstKey, await checkoutGroupRequestKey(actorId, '7', '1', a)); assert.notEqual(firstKey, secondKey);
  assert.notEqual(firstKey, await checkoutGroupRequestKey('17', '7', '1', a)); assert.notEqual(firstKey, await checkoutGroupRequestKey(actorId, '8', '1', a));
  assert.match(firstKey, new RegExp(`^ordering\\.order\\.submit:${actorId}:[a-f0-9]{64}$`));
  const storage = new Map<string, string>(), journal = new OperationJournal(() => ({ get length() { return storage.size; }, key: index => [...storage.keys()][index] ?? null,
    getItem: key => storage.get(key) ?? null, setItem: (key, value) => { storage.set(key, value); }, removeItem: key => { storage.delete(key); } }));
  const ledger = new MutationLedger(() => { throw new Error('A group must retain its original key'); }), receipts = new Map<string, typeof result>(); let writes = 0;
  assert.equal((await readCheckoutGroupAttempt(actorId, '7', '1', a, journal, async key => receipts.has(key) ? { state: 'committed', result: receipts.get(key)! } : null)).result, null);
  await submitTrackedRequest(ledger, journal, actorId, a, orderSubmitOperation, input, async key => { writes++; receipts.set(key, result); return result; }, firstKey);
  await assert.rejects(() => submitTrackedRequest(ledger, journal, actorId, b, orderSubmitOperation, input, async key => {
    writes++; receipts.set(key, { ...result, orderId: '9007199254741011' }); throw new AccessFailure(503, 'commit_outcome_unknown');
  }, secondKey));
  const lookup = async (key: string) => receipts.has(key) ? { state: 'committed' as const, result: receipts.get(key)! } : null;
  const committedA = await readCheckoutGroupAttempt(actorId, '7', '1', a, new OperationJournal(() => ({ get length() { return storage.size; }, key: index => [...storage.keys()][index] ?? null,
    getItem: key => storage.get(key) ?? null, setItem: (key, value) => { storage.set(key, value); }, removeItem: key => { storage.delete(key); } })), lookup);
  const committedB = await readCheckoutGroupAttempt(actorId, '7', '1', b, journal, lookup);
  assert.equal(committedA.result?.orderId, result.orderId); assert.equal(committedB.result?.orderId, '9007199254741011'); assert.equal(writes, 2);
  await assert.rejects(() => submitTrackedRequest(ledger, journal, actorId, a, orderSubmitOperation, input, async () => { writes++; return result; }, firstKey), { code: 'original_request_complete' });
  await assert.rejects(() => submitTrackedRequest(ledger, journal, actorId, b, orderSubmitOperation, input, async () => { writes++; return result; }, secondKey), { code: 'resolve_previous_attempt' });
  const unknown = await readCheckoutGroupAttempt(actorId, '7', '1', b, journal, async () => null);
  assert.equal(unknown.uncertain, true); assert.equal(unknown.result, null); assert.equal(journal.list(actorId)[0].key, secondKey); assert.equal(writes, 2);
  await assert.rejects(() => readCheckoutGroupAttempt(actorId, '7', '1', b, journal, async () => { throw new Error('Lookup failed'); })); assert.equal(writes, 2);
});
test('F-030: a checkout key belongs to one saved draft revision, so a new keranjang never replays an earlier order', async () => {
  const { value } = groupedFixture(), a = value.items[0].groupKey;
  const first = await checkoutGroupRequestKey(actorId, '7', '2', a);
  // The same checkout (same draft at the same saved revision) keeps its key: a lost answer is checked, never resubmitted.
  assert.equal(first, await checkoutGroupRequestKey(actorId, '7', '2', a));
  // A keranjang changed since, or a fresh draft, is a new checkout with its own key.
  assert.notEqual(first, await checkoutGroupRequestKey(actorId, '7', '3', a));
  assert.notEqual(first, await checkoutGroupRequestKey(actorId, '8', '1', a));
  assert.match(first, new RegExp(`^ordering\.order\.submit:${actorId}:[a-f0-9]{64}$`));
  await assert.rejects(() => checkoutGroupRequestKey(actorId, '7', '0', a)); await assert.rejects(() => checkoutGroupRequestKey(actorId, '7', 'x', a));
  const storage = new Map<string, string>(), journal = new OperationJournal(() => ({ get length() { return storage.size; }, key: index => [...storage.keys()][index] ?? null,
    getItem: key => storage.get(key) ?? null, setItem: (key, value) => { storage.set(key, value); }, removeItem: key => { storage.delete(key); } }));
  const receipts = new Map([[first, result]]);
  const lookup = async (key: string) => receipts.has(key) ? { state: 'committed' as const, result: receipts.get(key)! } : null;
  assert.equal((await readCheckoutGroupAttempt(actorId, '7', '2', a, journal, lookup)).result?.orderId, result.orderId);
  assert.equal((await readCheckoutGroupAttempt(actorId, '8', '1', a, journal, lookup)).result, null);
  assert.equal((await readCheckoutGroupAttempt(actorId, '7', '3', a, journal, lookup)).result, null);
});
