import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { createCatalogPriceGateway } from '../src/modules/catalog/catalog-price-client';
import { parsePricePreview, preparePriceProposal, preparePriceDecision, priceMoney, type PriceCommand } from '../src/modules/catalog/catalog-price-workflow';
import { actorId, sku, definition } from './catalog-fixtures';

const priceSku = { id: sku.id, familyId: sku.familyId, code: sku.code, publicId: sku.publicId, version: sku.version, active: true };
const preview = { sku: priceSku, definition, expected: { skuVersion: sku.version, definitionId: definition.id, definitionVersion: '1', priceVersion: '0' }, currentPrice: null };
const proposalId = '9007199254740994';
const proposed = { proposalId, skuId: sku.id, revision: '1', status: 'proposed', decisionId: null, priceId: null, priceVersion: null };
const proposal = { ...proposed, sku: priceSku, definition, expected: preview.expected, basePrice: null,
  input: { currency: 'IDR', unitPrice: '999999999999999.99' }, proposedAt: definition.effectiveAt, reason: 'Harga kemasan', decision: null, price: null, ownProposal: false };
const approved = { ...proposed, status: 'approve', decisionId: '901', priceId: '902', priceVersion: '1' };
const command = () => preparePriceProposal(actorId, parsePricePreview(preview), 'IDR', '999999999999999.99', 'Harga kemasan');
function gateway(handler: (r: Request) => Promise<Response>) {
  return createCatalogPriceGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request);
    if (input.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf-memory' });
    assert.equal(input.method, 'POST'); assert.equal(input.headers.get('X-Expected-Actor-Id'), actorId);
    assert.equal(input.headers.get('X-CSRF-Token'), 'csrf-memory'); assert.equal(input.credentials, 'same-origin');
    return handler(input);
  } }));
}
test('missing price remains null, exact maximum and explicit zero never use floating point', () => {
  assert.equal(parsePricePreview(preview).currentPrice, null);
  assert.equal(priceMoney('0'), '0.00'); assert.equal(priceMoney('999999999999999.99'), '999999999999999.99');
  for (const amount of ['', '1000000000000000', '1.001', '1e3', '-1', ' 1', '1,00', '1\n']) assert.throws(() => priceMoney(amount));
  assert.throws(() => parsePricePreview({ ...preview, sku: { ...priceSku, id: '1\n' } }));
  assert.throws(() => parsePricePreview({ ...preview, currentPrice: { id: '91', skuId: sku.id, version: '1', currency: 'IDR', unitPrice: 12.34, effectiveAt: definition.effectiveAt } }));
  assert.throws(() => parsePricePreview({ ...preview, expected: { ...preview.expected, definitionId: '999' } }));
});
test('reviewed proposal is deeply frozen and sends exact package/version/decimal strings once', async () => {
  const frozen = command(); assert.ok(Object.isFrozen(frozen.body.input)); assert.ok(Object.isFrozen(frozen.preview.definition.attributes));
  let calls = 0;
  const client = gateway(async request => { calls++; assert.equal(new URL(request.url).pathname, '/api/v1/catalog/prices/skus/' + sku.id + '/proposals');
    assert.equal(request.headers.get('If-Match'), '"' + sku.version + '"'); assert.equal(request.headers.get('Idempotency-Key'), 'original-key');
    const body = await request.json() as ReturnType<typeof preparePriceProposal>['body']; assert.equal(body.input.unitPrice, '999999999999999.99'); assert.equal(body.expectedPriceVersion, '0');
    return Response.json(proposed);
  });
  assert.deepEqual(await client.submit(frozen, 'original-key'), proposed); assert.equal(calls, 1);
});
test('decision binds the original proposal and refuses forged reviewed bodies before sending', async () => {
  const frozen = preparePriceDecision(actorId, proposal, 'approve', 'Tinjauan terpisah', 'Sesuai kemasan');
  let calls = 0; const client = gateway(async request => { calls++; assert.ok(request.url.endsWith('/proposals/' + proposalId + '/approve')); return Response.json(approved); });
  assert.deepEqual(await client.submit(frozen, 'decision-key'), approved);
  await assert.rejects(() => client.submit({ ...frozen, body: { ...frozen.body, reason: 'Diubah setelah tinjauan' } } as PriceCommand, 'decision-key'));
  assert.equal(calls, 1);
});
test('lost command response is not retried; action-bound receipt lookup sends no business command', async () => {
  let calls = 0; const client = gateway(async request => { calls++; if (request.url.endsWith('/operations/query')) {
    assert.deepEqual(await request.json(), { purpose: 'operation-recovery', action: 'propose', key: 'original-key' });
    return Response.json({ state: 'committed', result: proposed });
  } throw new TypeError('Lost response'); });
  await assert.rejects(() => client.submit(command(), 'original-key')); assert.equal(calls, 1);
  assert.deepEqual(await client.receipt({ actorId, operation: 'catalog.price.propose', key: 'original-key', createdAt: definition.effectiveAt }, sku.id), { state: 'committed', result: proposed });
  assert.equal(calls, 2);
});
test('stale session is checked before dispatch and after a possibly committed response', async () => {
  let calls = 0; const client = gateway(async () => { calls++; return Response.json(proposed); });
  await assert.rejects(() => client.submit(command(), 'original-key', () => false), RequestNotSentError); assert.equal(calls, 0);
  let checks = 0; await assert.rejects(() => client.submit(command(), 'original-key', () => ++checks === 1), /commit_outcome_unknown/);
  assert.equal(calls, 1);
});
test('unknown receipt does not confirm work and mismatched SKU/action is rejected', async () => {
  const entry = { actorId, operation: 'catalog.price.propose', key: 'original-key', createdAt: definition.effectiveAt };
  assert.equal(await gateway(async () => Response.json({ state: 'not_confirmed', result: null })).receipt(entry, sku.id), null);
  await assert.rejects(() => gateway(async () => Response.json({ state: 'committed', result: approved })).receipt(entry, sku.id));
  await assert.rejects(() => gateway(async () => Response.json({ state: 'committed', result: { ...proposed, skuId: '22' } })).receipt(entry, sku.id));
});
