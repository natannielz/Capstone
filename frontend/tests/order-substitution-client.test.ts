import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createOrderSubstitutionGateway, parseSubstitutionProposalReview, parseSubstitutionHistory, parseSubstitutionDecisionReview,
  parseSubstitutionResult, parseSubstitutionTarget } from '../src/modules/ordering/order-substitution-client';
import { OperationJournal, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';
import { initialPreparationValues, parseFulfillmentOrder, type Preparation } from '../src/modules/fulfillment/preparation-client';
import { actorId, family, sku, definition } from './catalog-fixtures';

test('after an accepted substitution the exhausted original line is never preselected for preparation', () => {
  const line = { skuId: sku.id, lineRevision: '0', publicId: sku.publicId, skuCode: sku.code, productName: 'Pena biru', definition, orderedPackages: 12, remainingPackages: 0 };
  const replaced = { ...line, skuId: '9007199254741099', publicId: '3f2b8c1e-4d5a-4b6c-9d7e-8f9a0b1c2d3e', skuCode: 'REPLACEMENT-12',
    definition: { ...definition, id: '9007199254741098', skuId: '9007199254741099' }, lineRevision: '5', orderedPackages: 1, remainingPackages: 1 };
  const order = parseFulfillmentOrder({ orderId: '9007199254741001', suborderId: '9007199254741002', revision: '6', review: 'a'.repeat(64), buyerName: 'Cabang fixture',
    delivery: { recipientName: 'Penerima fixture', phone: '081234567890', address: 'Alamat fixture', city: 'Jakarta', postalCode: '12810', instructions: null },
    lines: [line, replaced], canPrepare: true, orderedAt: definition.effectiveAt });
  const saved = { lines: [{ skuId: sku.id, lineRevision: '0', packages: 8 }] } as unknown as Preparation;
  const values = initialPreparationValues(order, saved);
  assert.deepEqual(values[sku.id + ':0'], { selected: false, packages: '8' });
  assert.deepEqual(values[replaced.skuId + ':5'], { selected: false, packages: '1' });
  const prepared = initialPreparationValues(order, { lines: [{ skuId: replaced.skuId, lineRevision: '5', packages: 1 }] } as unknown as Preparation);
  assert.deepEqual(prepared[replaced.skuId + ':5'], { selected: true, packages: '1' });
});

const original = { familyId: family.id, familyName: family.name, skuCode: sku.code, definition,
  price: { id: '9007199254740999', skuId: sku.id, version: '2', currency: 'IDR', unitPrice: '12000.00', effectiveAt: definition.effectiveAt }, packages: 4, baseUnits: '48', catalogSubtotal: '48000.00' };
const replacement = { ...original, skuCode: 'ALTERNATIVE-CARTON', definition: { ...definition, id: '9007199254741021', skuId: '9007199254741020', version: '1', packageLabel: 'Karton pengganti' },
  price: { ...original.price, id: '9007199254741022', skuId: '9007199254741020', version: '1', unitPrice: '30000.00' }, packages: 1, baseUnits: '12', catalogSubtotal: '30000.00' };
const input = { orderId: '9007199254741030', suborderId: '9007199254741031', expectedOrderRevision: '3', expectedDispatchSequence: '1', originalSkuId: sku.id, originalLineRevision: '0', originalPackages: 2,
  replacement: { skuId: replacement.definition.skuId, definitionId: replacement.definition.id, definitionVersion: '1', priceId: replacement.price.id, priceVersion: '1', packages: 1 } };
const proposal = { input, review: 'a'.repeat(64), buyerName: 'Cabang Selatan', original, replacement, remainingPackages: 2, calculatedAt: definition.effectiveAt };
const history = { proposalId: '9007199254741032', orderId: input.orderId, suborderId: input.suborderId, buyerName: proposal.buyerName, input, original, replacement,
  status: 'pending', decisionId: null, proposedAt: definition.effectiveAt, decidedAt: null };
const decision = { proposalId: history.proposalId, orderId: input.orderId, suborderId: input.suborderId, revision: '3', decision: 'Accept', review: 'b'.repeat(64), original, originalPackages: 2, replacement,
  currency: 'IDR', lines: [{ ...original, packages: 2, baseUnits: '24', catalogSubtotal: '24000.00' }, {...replacement, lineRevision: '4'}], catalogSubtotal: '54000.00', tax: '0.00', charges: '1500.00', total: '55500.00', spendingState: 'Pending', calculatedAt: definition.effectiveAt };
const accepted = { proposalId: history.proposalId, orderId: input.orderId, suborderId: input.suborderId, decisionId: '9007199254741033', revision: '4', status: 'accepted' as const, recordedAt: definition.effectiveAt };
function gateway(handle: (request: Request) => Promise<Response>) {
  return createOrderSubstitutionGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); return handle(request);
  } }));
}

test('proposal review binds exact original remainder and replacement package, quantity, approved price version and arithmetic', () => {
  assert.equal(parseSubstitutionProposalReview(proposal, input).replacement.price.unitPrice, '30000.00');
  for (const bad of [{ ...proposal, remainingPackages: 1 }, { ...proposal, input: { ...input, expectedDispatchSequence: '2' } },
    { ...proposal, replacement: { ...replacement, price: { ...replacement.price, version: '2' } } }, { ...proposal, replacement: { ...replacement, baseUnits: '1' } },
    { ...proposal, replacement: { ...replacement, catalogSubtotal: '29999.00' } }, { ...proposal, original: replacement }])
    assert.throws(() => parseSubstitutionProposalReview(bad, input));
});
test('consent validates all revised lines and total while preserving the original historical partial line', () => {
  const reviewed = parseSubstitutionDecisionReview(decision, parseSubstitutionHistory(history), 'Accept');
  assert.equal(reviewed.total, '55500.00'); assert.equal(reviewed.lines[0].packages, 2);
  for (const bad of [{ ...decision, total: '55500.01' }, { ...decision, originalPackages: 1 }, { ...decision, proposalId: '1' },
    { ...decision, lines: [replacement, replacement] }, { ...decision, decision: 'Decline' }, { ...decision, replacement: { ...replacement, packages: 2 } }])
    assert.throws(() => parseSubstitutionDecisionReview(bad, parseSubstitutionHistory(history), 'Accept'));
});
test('history cannot falsely mark consent pending or complete and targets cannot exceed the original quantity', () => {
  assert.equal(parseSubstitutionHistory(history).status, 'pending');
  for (const bad of [{ ...history, status: 'accepted' }, { ...history, decisionId: '10' }, { ...history, input: { ...input, orderId: '10' } }]) assert.throws(() => parseSubstitutionHistory(bad));
  assert.throws(() => parseSubstitutionTarget({ orderId: input.orderId, suborderId: input.suborderId, revision: '3', dispatchSequence: '1', buyerName: 'Cabang', canPropose: true, lines: [{ line: original, remainingPackages: 5 }] }));
  assert.throws(() => parseSubstitutionResult(accepted, 'decline'));
});

test('repeated SKU and same-SKU correction retain exact original and newly accepted line identities', () => {
  const newer = {...original, lineRevision: '4', price: {...original.price, id: '9007199254741100', version: '3', unitPrice: '13000.00'}, catalogSubtotal: '52000.00'};
  const target = {orderId: input.orderId, suborderId: input.suborderId, revision: '6', dispatchSequence: '1', buyerName: 'Cabang', canPropose: true,
    lines: [{line: original, remainingPackages: 2}, {line: newer, remainingPackages: 4}]};
  assert.equal(parseSubstitutionTarget(target).lines.length, 2);
  assert.throws(() => parseSubstitutionTarget({...target, lines: [{line: original, remainingPackages: 2}, {line: {...newer, lineRevision: '0'}, remainingPackages: 4}]}));
  const sameSkuInput = {...input, expectedOrderRevision: '6', originalLineRevision: '4', originalPackages: 4,
    replacement: {...input.replacement, skuId: sku.id, definitionId: definition.id, definitionVersion: definition.version, priceId: newer.price.id, priceVersion: newer.price.version, packages: 4}};
  const exact = {...proposal, input: sameSkuInput, original: newer, replacement: {...newer, lineRevision: '0'}, remainingPackages: 4};
  assert.equal(parseSubstitutionProposalReview(exact, sameSkuInput).original.lineRevision, '4');
  assert.throws(() => parseSubstitutionProposalReview({...exact, original: {...newer, lineRevision: '0'}}, sameSkuInput));
  assert.throws(() => parseSubstitutionDecisionReview({...decision, lines: [decision.lines[0], {...replacement, lineRevision: '0'}]}, parseSubstitutionHistory(history), 'Accept'));
});
test('command carries only reviewed consent and original idempotency key; uncertain reply is never retried', async () => {
  const review = parseSubstitutionDecisionReview(decision, parseSubstitutionHistory(history), 'Accept'); let writes = 0;
  const client = gateway(async request => {
    writes++; assert.equal(request.headers.get('If-Match'), '"3"'); assert.equal(request.headers.get('Idempotency-Key'), 'consent-original');
    assert.deepEqual(await request.json(), { decision: 'Accept', review: decision.review, reason: 'Setuju kemasan dan total' }); throw new Error('Reply lost after commit');
  });
  await assert.rejects(() => client.decide(actorId, review, 'Setuju kemasan dan total', 'consent-original', () => false)); assert.equal(writes, 0);
  await assert.rejects(() => client.decide(actorId, review, 'Setuju kemasan dan total', 'consent-original', () => true)); assert.equal(writes, 1);
  await assert.rejects(() => gateway(async () => Response.json({ ...accepted, revision: '5' })).decide(actorId, review, 'Setuju kemasan dan total', 'consent-original', () => true));
});
test('actor or page change after sending leaves an uncertain result for exact-key recovery', async () => {
  const review = parseSubstitutionProposalReview(proposal, input); let current = true, calls = 0;
  await assert.rejects(() => gateway(async () => { calls++; current = false; return Response.json({ ...accepted, decisionId: null, revision: null, status: 'proposed' }); })
    .propose(actorId, review, 'Ajukan pilihan pengganti', 'proposal-original', () => current), { code: 'commit_outcome_unknown' });
  assert.equal(calls, 1);
});
test('durable recovery retains unknown and wrong-action results and clears only a validated original receipt', async () => {
  const values = new Map<string, string>(), journal = new OperationJournal(() => ({ get length() { return values.size; }, key: index => [...values.keys()][index] ?? null,
    getItem: key => values.get(key) ?? null, setItem: (key, value) => { values.set(key, value); }, removeItem: key => { values.delete(key); } }));
  const entry = journal.remember(actorId, 'ordering.substitution.accept', 'consent-original');
  assert.deepEqual(Object.keys(JSON.parse([...values.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  const legacy = { lookup: async () => { throw new Error('Legacy lookup forbidden'); }, close: async () => { throw new Error('Legacy close forbidden'); } };
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'ordering.substitution': { lookup: async () => null } }), { state: 'unconfirmed' });
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'ordering.substitution': { lookup: async () => null } }, 'Close'));
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'ordering.substitution': { lookup: async () => ({ state: 'committed', result: { ...accepted, status: 'declined' } }) } }));
  assert.equal(journal.list(actorId).length, 1);
  const receipt = await gateway(async request => { assert.deepEqual(await request.json(), { purpose: 'operation-recovery', operation: 'accept', key: 'consent-original' }); return Response.json({ status: 'committed', result: accepted }); }).recover(actorId, 'accept', 'consent-original', () => true);
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'ordering.substitution': { lookup: async () => receipt } }), { state: 'committed', result: { id: history.proposalId, version: '4' } });
  assert.equal(journal.list(actorId).length, 0);
});


test('expired recovery is not dispatched and retains the original reference', async () => {
  let calls = 0;
  const client = gateway(async () => { calls++; return Response.json({ status: 'committed', result: accepted }); });
  await assert.rejects(() => client.recover(actorId, 'accept', 'consent-original', () => false), { code: 'recovery_request_stale' });
  assert.equal(calls, 0);
});

test('a deferred recovery response after session expiry cannot remove the journal original key', async () => {
  const values = new Map<string, string>(), journal = new OperationJournal(() => ({ get length() { return values.size; }, key: index => [...values.keys()][index] ?? null,
    getItem: key => values.get(key) ?? null, setItem: (key, value) => { values.set(key, value); }, removeItem: key => { values.delete(key); } }));
  const entry = journal.remember(actorId, 'ordering.substitution.accept', 'consent-original');
  const legacy = { lookup: async () => { throw new Error('Legacy lookup forbidden'); }, close: async () => { throw new Error('Legacy close forbidden'); } };
  let finish!: (value: Response) => void, dispatched!: () => void, current = true, calls = 0;
  const reply = new Promise<Response>(resolve => { finish = resolve; });
  const started = new Promise<void>(resolve => { dispatched = resolve; });
  const client = gateway(async request => {
    calls++; assert.deepEqual(await request.json(), { purpose: 'operation-recovery', operation: 'accept', key: entry.key }); dispatched(); return reply;
  });
  const pending = reconcileOperationRecovery(entry, journal, legacy, { 'ordering.substitution': {
    lookup: saved => client.recover(saved.actorId, 'accept', saved.key, () => current),
  } });
  await started; current = false; finish(Response.json({ status: 'committed', result: accepted }));
  await assert.rejects(pending, { code: 'recovery_request_stale' });
  assert.equal(calls, 1); assert.deepEqual(journal.list(actorId), [entry]);
  const fresh = gateway(async request => {
    assert.deepEqual(await request.json(), { purpose: 'operation-recovery', operation: 'accept', key: entry.key });
    return Response.json({ status: 'committed', result: accepted });
  });
  const result = await reconcileOperationRecovery(entry, journal, legacy, { 'ordering.substitution': {
    lookup: saved => fresh.recover(saved.actorId, 'accept', saved.key, () => true),
  } });
  assert.equal(result.state, 'committed'); assert.equal(journal.list(actorId).length, 0);
});
