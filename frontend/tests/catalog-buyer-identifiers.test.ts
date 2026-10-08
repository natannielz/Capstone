import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { AccessFailure } from '../src/modules/administration/workflow';
import { buyerCurrentCode, createBuyerCatalogGateway, parseBuyerCodeMatch, parseBuyerVariant } from '../src/modules/catalog/catalog-buyer-client';
import { createBuyerBulkGateway } from '../src/modules/catalog/catalog-bulk-client';
import { BulkInputError, parseBulkSkuInput, prepareBulkSkuSelection } from '../src/modules/ordering/order-bulk';
import { actorId, family, sku, definition } from './catalog-fixtures';

function item() {
  return { familyId: family.id, familyPublicId: family.publicId, familyName: family.name, description: family.description,
    categoryCode: family.categoryCode, skuId: sku.id, publicId: sku.publicId, code: sku.code, definition,
    currentPrice: { id: '71', skuId: sku.id, version: '2', currency: 'IDR', unitPrice: '100.00', effectiveAt: definition.effectiveAt },
    buyerIdentity: { skuId: sku.id, skuRevision: '5', definitionId: definition.id, definitionVersion: definition.version,
      currentPrimary: { identifierId: '81', identifierRevision: '1', code: 'CURRENT-PENA' } } };
}
function match(code = 'ACTIVE-ALIAS') {
  return { requestedCode: code, matchedIdentifier: { identifierId: code === 'CURRENT-PENA' ? '81' : '80', identifierRevision: code === 'CURRENT-PENA' ? '1' : '2',
    code, skuId: sku.id, skuRevision: '5', definitionId: definition.id, definitionVersion: definition.version, kind: 'skuCode', namespace: 'internal' }, variant: item() };
}
function transport(reply: () => unknown, status = 200) {
  return createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request);
    if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf-memory' });
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actorId);
    return Response.json(reply(), { status });
  } });
}

test('current buyer primary is separate from immutable SKU; private provenance and namespaces are not exposed', () => {
  const parsed = parseBuyerVariant({ ...item(), supplierCode: 'PRIVATE', provenanceReference: 'PRIVATE' });
  assert.equal(parsed.code, sku.code); assert.equal(buyerCurrentCode(parsed), 'CURRENT-PENA');
  assert.equal(parsed.buyerIdentity!.skuId, sku.id); assert.equal(parsed.buyerIdentity!.definitionId, definition.id);
  assert.equal('supplierCode' in parsed, false); assert.equal('provenanceReference' in parsed, false);
  const legacy = parseBuyerVariant({ ...item(), buyerIdentity: undefined });
  assert.equal(legacy.buyerIdentity, null, 'A legacy owner DTO is never relabeled as a current primary');
});

test('buyer gateway requires projection and refuses unrelated SKU or definition identity', async () => {
  const good = await createBuyerCatalogGateway(transport(() => ({ total: '1', items: [item()] }))).query(actorId);
  assert.equal(buyerCurrentCode(good.items[0]), 'CURRENT-PENA');
  for (const identity of [undefined, null, { ...item().buyerIdentity, skuId: '7' }, { ...item().buyerIdentity, definitionId: '7' },
    { ...item().buyerIdentity, definitionVersion: '2' }, { ...item().buyerIdentity, currentPrimary: { ...item().buyerIdentity.currentPrimary, code: 'SUPPLIER/001' } }]) {
    const row = { ...item(), buyerIdentity: identity };
    await assert.rejects(() => createBuyerCatalogGateway(transport(() => ({ total: '1', items: [row] }))).query(actorId), AccessFailure);
  }
});

test('exact alias envelope binds input to current package without changing stable code or implicit quantity', () => {
  const parsed = parseBuyerCodeMatch(match(), 'ACTIVE-ALIAS');
  assert.equal(parsed.variant.code, sku.code); assert.equal(parsed.matchedIdentifier.identifierId, '80');
  const selected = prepareBulkSkuSelection(parseBulkSkuInput('active-alias;4'), [parsed], []);
  assert.deepEqual(selected.selections, [{ skuId: sku.id, definitionId: definition.id, definitionVersion: definition.version,
    priceId: '71', priceVersion: '2', packages: 4 }]);
  assert.equal(parseBuyerCodeMatch(match('CURRENT-PENA'), 'CURRENT-PENA').matchedIdentifier.identifierId, '81');
});

test('wrong namespace, revision, selected definition or contradictory current primary cannot enter a draft', () => {
  for (const patch of [{ kind: 'gtin' }, { namespace: 'supplier:1' }, { skuId: '7' }, { skuRevision: '4' },
    { definitionId: '7' }, { definitionVersion: '2' }, { code: 'OTHER' }, { identifierId: '81' }, { identifierRevision: '0' }]) {
    const result = { ...match(), matchedIdentifier: { ...match().matchedIdentifier, ...patch } };
    assert.throws(() => parseBuyerCodeMatch(result, 'ACTIVE-ALIAS'), AccessFailure);
    assert.throws(() => prepareBulkSkuSelection(parseBulkSkuInput('ACTIVE-ALIAS;2'), [result as never], []), BulkInputError);
  }
});

test('two distinct active aliases to the same SKU are rejected atomically by client and draft preparation', async () => {
  const matches = [match(), match('CURRENT-PENA')];
  await assert.rejects(() => createBuyerBulkGateway(transport(() => matches)).exact(actorId, ['ACTIVE-ALIAS', 'CURRENT-PENA']), AccessFailure);
  const current = [{ skuId: '99', definitionId: '98', definitionVersion: '1', priceId: '97', priceVersion: '1', packages: 7 }];
  const before = structuredClone(current);
  assert.throws(() => prepareBulkSkuSelection(parseBulkSkuInput('ACTIVE-ALIAS;2\nCURRENT-PENA;3'), matches.map(row => parseBuyerCodeMatch(row, row.requestedCode)), current), BulkInputError);
  assert.deepEqual(current, before);
});

test('retired or unentitled batch refusal never returns a partial usable replacement', async () => {
  await assert.rejects(() => createBuyerBulkGateway(transport(() => ({ code: 'order_package_unavailable' }), 403))
    .exact(actorId, ['ACTIVE-ALIAS', 'RETIRED-ALIAS']), { code: 'order_package_unavailable' });
  await assert.rejects(() => createBuyerBulkGateway(transport(() => [match()])).exact(actorId, ['ACTIVE-ALIAS', 'OTHER-SKU']), AccessFailure);
  await assert.rejects(() => createBuyerBulkGateway(transport(() => [match()])).exact(actorId, ['CURRENT-PENA']), AccessFailure);
});

test('one permanent alias identifier cannot be reused by two distinct SKU envelopes', async () => {
  const first = match(), second = structuredClone(match('SECOND-ALIAS'));
  second.variant.skuId = '97'; second.variant.definition.skuId = '97'; second.variant.definition.id = '96';
  second.variant.currentPrice.skuId = '97'; second.variant.buyerIdentity.skuId = '97'; second.variant.buyerIdentity.definitionId = '96';
  second.variant.buyerIdentity.currentPrimary.identifierId = '95'; second.variant.buyerIdentity.currentPrimary.code = 'SECOND-PRIMARY';
  second.matchedIdentifier.skuId = '97'; second.matchedIdentifier.definitionId = '96'; second.matchedIdentifier.identifierId = '94';
  const gateway = createBuyerBulkGateway(transport(() => [first, second]));
  assert.equal((await gateway.exact(actorId, ['ACTIVE-ALIAS', 'SECOND-ALIAS'])).length, 2);
  const rows = parseBulkSkuInput('ACTIVE-ALIAS;2\nSECOND-ALIAS;3');
  assert.equal(prepareBulkSkuSelection(rows, [first, second].map(row => parseBuyerCodeMatch(row, row.requestedCode)), []).selections.length, 2);
  second.matchedIdentifier.identifierId = first.matchedIdentifier.identifierId;
  // Each envelope is internally consistent, but their shared permanent reservation is impossible.
  const parsed = [first, second].map(row => parseBuyerCodeMatch(row, row.requestedCode));
  await assert.rejects(() => gateway.exact(actorId, ['ACTIVE-ALIAS', 'SECOND-ALIAS']), AccessFailure);
  assert.throws(() => prepareBulkSkuSelection(rows, parsed, []), BulkInputError);
});
