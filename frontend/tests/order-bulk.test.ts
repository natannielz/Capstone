import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { AccessFailure } from '../src/modules/administration/workflow';
import { createBuyerBulkGateway } from '../src/modules/catalog/catalog-bulk-client';
import { parseBuyerVariant } from '../src/modules/catalog/catalog-buyer-client';
import { BulkInputError, parseBulkSkuInput, prepareBulkSkuSelection } from '../src/modules/ordering/order-bulk';
import { actorId, family, sku, definition } from './catalog-fixtures';

function variant(code = sku.code, id = sku.id) {
  return parseBuyerVariant({ familyId: family.id, familyPublicId: family.publicId, familyName: family.name,
    description: family.description, categoryCode: family.categoryCode, skuId: id, publicId: sku.publicId, code,
    definition: { ...definition, skuId: id, minimumOrderPackages: 2, orderIncrementPackages: 2 },
    currentPrice: { id: '9007199254741003', skuId: id, version: '4', currency: 'IDR', unitPrice: '12345.67', effectiveAt: definition.effectiveAt } });
}

test('bulk parses explicit internal SKU and package counts from spreadsheet or semicolon rows', () => {
  assert.deepEqual(parseBulkSkuInput(' pena-12\t2\r\n\nPAPER_1;4\n'), [
    { line: 1, code: 'PENA-12', packages: 2 }, { line: 3, code: 'PAPER_1', packages: 4 }]);
  assert.equal(Object.isFrozen(parseBulkSkuInput('PENA-12;2')[0]), true);
});

test('bulk rejects duplicate/case aliases, confusable/control codes and implicit or malformed quantities', () => {
  for (const text of ['', 'PENA-12;2\npena-12;4', 'KODE;2', 'PENA-12\v;2', 'PENA-12', 'PENA-12;2;3',
    'PENA-12;0', 'PENA-12;01', 'PENA-12;2.5', 'PENA-12;1e3', 'PENA-12;-1', 'PENA-12;1000001'])
    assert.throws(() => parseBulkSkuInput(text), BulkInputError, text);
  assert.throws(() => parseBulkSkuInput(Array.from({ length: 101 }, (_, i) => 'SKU-' + i + ';2').join('\n')), BulkInputError);
});

test('bulk holds exact current definition and price versions, explicitly replaces counts and preserves other choices', () => {
  const old = { skuId: sku.id, definitionId: '49', definitionVersion: '1', priceId: '60', priceVersion: '1', packages: 8 };
  const retained = { ...old, skuId: '9007199254741007', packages: 10 };
  const existing = [old, retained], before = structuredClone(existing);
  const result = prepareBulkSkuSelection(parseBulkSkuInput('PENA-12;4'), [variant()], existing);
  assert.deepEqual(result.replacements, [sku.id]);
  assert.deepEqual(result.selections[0], { skuId: sku.id, definitionId: definition.id, definitionVersion: definition.version,
    priceId: '9007199254741003', priceVersion: '4', packages: 4 });
  assert.deepEqual(result.selections[1], retained); assert.notEqual(result.selections[1], retained);
  assert.deepEqual(existing, before);
});

test('bulk refuses reordered, partial, duplicate or unavailable catalog results and invalid package multiples', () => {
  const rows = parseBulkSkuInput('PENA-12;4\nPAPER_1;2'), first = variant(), second = variant('PAPER_1', '9007199254741011');
  for (const items of [[first], [second, first], [first, first], [first, { ...second, currentPrice: null }]])
    assert.throws(() => prepareBulkSkuSelection(rows, items, []), BulkInputError);
  assert.throws(() => prepareBulkSkuSelection(parseBulkSkuInput('PENA-12;1'), [first], []), BulkInputError);
  assert.throws(() => prepareBulkSkuSelection(parseBulkSkuInput('PENA-12;3'), [first], []), BulkInputError);
});

test('bulk never grows a draft beyond 100 distinct selections', () => {
  const existing = Array.from({ length: 100 }, (_, i) => ({ skuId: String(i + 1), definitionId: '51', definitionVersion: '1',
    priceId: '60', priceVersion: '1', packages: 2 }));
  assert.throws(() => prepareBulkSkuSelection(parseBulkSkuInput('PENA-12;2'), [variant()], existing), BulkInputError);
  assert.equal(existing.length, 100);
});

test('bulk gateway binds the exact actor and CSRF token and rejects mismatched server identity or partial results', async () => {
  let calls = 0, reply: unknown = [variant()];
  const gateway = createBuyerBulkGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request);
    if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'current-csrf' });
    calls++;
    assert.equal(new URL(request.url).pathname, '/api/v1/catalog/variants/exact-skus/query');
    assert.equal(request.method, 'POST'); assert.equal(request.headers.get('X-Expected-Actor-Id'), actorId);
    assert.equal(request.headers.get('X-CSRF-Token'), 'current-csrf');
    assert.equal(request.headers.get('Idempotency-Key'), null, 'Catalog lookup must not masquerade as a saved draft command');
    assert.deepEqual(await request.json(), { codes: [sku.code] });
    return Response.json(reply);
  } }));
  await assert.rejects(() => gateway.exact(actorId, ['pena-12']), AccessFailure); assert.equal(calls, 0);
  const result = await gateway.exact(actorId, [sku.code]);
  assert.equal(result[0].skuId, sku.id); assert.equal(result[0].currentPrice!.id, '9007199254741003');
  reply = []; await assert.rejects(() => gateway.exact(actorId, [sku.code]), AccessFailure);
  reply = [variant('DIFFERENT-CODE')]; await assert.rejects(() => gateway.exact(actorId, [sku.code]), AccessFailure);
});
