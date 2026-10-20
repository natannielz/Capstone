import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createBuyerCatalogGateway, parseBuyerVariant } from '../src/modules/catalog/catalog-buyer-client';
import { actorId, family, sku, definition } from './catalog-fixtures';
import { identityDestinations, identityReturnPath, loginFields } from '../src/modules/identity/identity-workflow';
const item = { familyId: family.id, familyPublicId: family.publicId, familyName: family.name, description: family.description,
  categoryCode: family.categoryCode, skuId: sku.id, publicId: sku.publicId, code: sku.code, definition,
  buyerIdentity: { skuId: sku.id, skuRevision: '1', definitionId: definition.id, definitionVersion: definition.version,
    currentPrimary: { identifierId: '80', identifierRevision: '1', code: sku.code } },
  currentPrice: { id: '9007199254740999', skuId: sku.id, version: '2', currency: 'IDR', unitPrice: '0.00', effectiveAt: definition.effectiveAt } };
function gateway(handler: (request: Request) => Promise<Response>) {
  return createBuyerCatalogGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request);
    if (input.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf-memory' });
    assert.equal(input.method, 'POST'); assert.equal(input.credentials, 'same-origin');
    assert.equal(input.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(input.headers.get('X-CSRF-Token'), 'csrf-memory');
    return handler(input);
  } }));
}
test('entitled buyer client preserves missing versus zero and exact package/price identity without staff fields', () => {
  assert.equal(identityReturnPath('/catalog/browse'), '/catalog/browse');
  assert.equal(identityReturnPath('/shop'), '/shop');
  assert.deepEqual(loginFields('/shop', '', ''), { returnPath: '/shop' });
  assert.ok(identityDestinations(['catalog.read']).some(item => item.href === '/shop'));
  assert.ok(!identityDestinations(['order.submit']).some(item => item.href === '/shop'));
  for (const path of ['/shop/', '/shop?next=https://outside.co.id', '//shop', '/shop/' + sku.publicId]) {
    assert.equal(identityReturnPath(path), '/identity/account');
    assert.throws(() => loginFields(path, '', ''));
  }
  assert.equal(parseBuyerVariant({ ...item, currentPrice: null }).currentPrice, null);
  const parsed = parseBuyerVariant({ ...item, reason: 'private', proposedBy: '7' });
  assert.equal(parsed.currentPrice!.unitPrice, '0.00'); assert.equal(parsed.currentPrice!.id, '9007199254740999');
  assert.equal(parsed.definition.baseUnitsPerPackage, 12); assert.equal('reason' in parsed, false); assert.equal('proposedBy' in parsed, false);
  assert.throws(() => parseBuyerVariant({ ...item, currentPrice: undefined }));
  assert.throws(() => parseBuyerVariant({ ...item, currentPrice: { ...item.currentPrice, unitPrice: 0 } }));
  assert.throws(() => parseBuyerVariant({ ...item, currentPrice: { ...item.currentPrice, skuId: '7' } }));
  assert.throws(() => parseBuyerVariant({ ...item, definition: { ...definition, skuId: '7' } }));
  assert.throws(() => parseBuyerVariant({ ...item, publicId: sku.publicId + '\n' }));
});
test('buyer query and fresh detail use only the entitled endpoints and abortable same-origin transport', async () => {
  const calls: string[] = []; const client = gateway(async request => {
    calls.push(new URL(request.url).pathname); const body = await request.json();
    if (calls.length === 1) { assert.deepEqual(body, { search: 'PENA', offset: 0, limit: 25 }); return Response.json({ total: '1', items: [item] }); }
    assert.deepEqual(body, {}); return Response.json(item);
  });
  assert.equal((await client.query(actorId, ' PENA ')).items[0].skuId, sku.id);
  assert.equal((await client.variant(actorId, sku.publicId))!.currentPrice!.unitPrice, '0.00');
  assert.deepEqual(calls, ['/api/v1/catalog/variants/query', '/api/v1/catalog/variants/' + sku.publicId + '/query']);
});
test('revoked access, wrong detail and ambiguous pages never become a usable buyer selection', async () => {
  let calls = 0; const denied = gateway(async () => { calls++; return Response.json({ code: 'scope_denied' }, { status: 403 }); });
  await assert.rejects(() => denied.query(actorId), { code: 'scope_denied' }); assert.equal(calls, 1);
  await assert.rejects(() => gateway(async () => Response.json({ ...item, publicId: family.publicId })).variant(actorId, sku.publicId));
  await assert.rejects(() => gateway(async () => Response.json({ total: '2', items: [item, item] })).query(actorId));
  await assert.rejects(() => gateway(async () => Response.json({ total: '0', items: [item] })).query(actorId));
  assert.equal(await gateway(async () => Response.json(null)).variant(actorId, sku.publicId), null);
});
test('buyer query sends the category chip and order and reads per-category counts', async () => {
  const categories = [{ code: 'ATK', name: 'Alat tulis', count: '3' }, { code: 'PANTRY', name: 'Pantry kopi dan teh', count: '1' }];
  const client = gateway(async request => {
    assert.deepEqual(await request.json(), { search: '', offset: 0, limit: 25, category: 'PANTRY', sort: 'name' });
    return Response.json({ total: '1', items: [item], categories });
  });
  const page = await client.query(actorId, '', 0, undefined, { category: 'PANTRY', sort: 'name' });
  assert.deepEqual(page.categories, [{ code: 'ATK', name: 'Alat tulis', count: 3 }, { code: 'PANTRY', name: 'Pantry kopi dan teh', count: 1 }]);
  assert.deepEqual((await gateway(async () => Response.json({ total: '1', items: [item] })).query(actorId)).categories, [], 'an older server sends no chips');
  for (const bad of [[{ ...categories[0], count: '0' }], [categories[0], categories[0]], [{ ...categories[0], code: 'atk' }], [{ ...categories[0], name: '' }], 'ATK',
    Array.from({ length: 201 }, (_, index) => ({ code: 'C' + index, name: 'Kategori', count: '1' }))]) {
    await assert.rejects(() => gateway(async () => Response.json({ total: '1', items: [item], categories: bad })).query(actorId), JSON.stringify(bad).slice(0, 60));
  }
});
test('alphabetical pages may list packages in any id order; added order must stay ascending', async () => {
  const otherId = '41', other = { ...item, skuId: otherId, publicId: '12345678-1234-4123-8123-123456789abe', definition: { ...definition, skuId: otherId },
    buyerIdentity: { ...item.buyerIdentity, skuId: otherId }, currentPrice: { ...item.currentPrice, skuId: otherId } };
  const page = () => gateway(async () => Response.json({ total: '2', items: [item, other] }));
  assert.equal((await page().query(actorId, '', 0, undefined, { sort: 'name' })).items.length, 2);
  await assert.rejects(() => page().query(actorId));
  await assert.rejects(() => page().query(actorId, '', 0, undefined, { category: 'not a code' }));
});
