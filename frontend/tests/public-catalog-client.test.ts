import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createPublicCatalogGateway, publicCatalogUrl} from '../src/modules/catalog/public-catalog-client';

const item = {publicId: '00000065-0000-4000-8000-000000000101', code: 'AIR-600-K24', name: 'Air mineral 600 ml', description: 'Air mineral dalam botol plastik.',
  categoryCode: 'AIR-MINUMAN', categoryName: 'Air dan minuman', packageLabel: 'Karton isi 24 botol', packageKind: 'sealed', baseUnitCode: 'botol',
  baseUnitsPerPackage: 24, attributes: [{key: 'Volume', value: '600 ml'}], minimumOrderPackages: 1, orderIncrementPackages: 1};

test('the public catalog is read anonymously with a plain GET, never with cookies, a CSRF token or an actor', async () => {
  const requests: Request[] = [];
  const gateway = createPublicCatalogGateway({origin: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); requests.push(input);
    return Response.json({total: '1', items: [item], categories: [{code: 'AIR-MINUMAN', name: 'Air dan minuman', count: '1'}]});
  }});
  const page = await gateway.query({search: ' air ', category: 'AIR-MINUMAN', sort: 'name', offset: 24});
  assert.equal(requests[0].method, 'GET'); assert.equal(requests[0].credentials, 'omit');
  assert.equal(requests[0].headers.has('X-CSRF-Token'), false); assert.equal(requests[0].headers.has('X-Expected-Actor-Id'), false);
  assert.equal(new URL(requests[0].url).search, '?q=air&kategori=AIR-MINUMAN&urut=nama&offset=24&limit=24');
  assert.equal(page.total, 1); assert.equal(page.items[0].name, 'Air mineral 600 ml'); assert.equal(page.categories[0].count, 1);
  assert.equal('price' in page.items[0] || 'currentPrice' in page.items[0], false);
});

test('a malformed public answer is refused instead of shown', async () => {
  for (const broken of [{...item, publicId: 'not-a-uuid'}, {...item, baseUnitsPerPackage: 0}, {...item, name: ''}]) {
    const gateway = createPublicCatalogGateway({origin: 'https://unit-toko.test', fetch: async () => Response.json({total: '1', items: [broken], categories: []})});
    await assert.rejects(() => gateway.query({}), (error: unknown) => (error as {code?: string}).code === 'public_catalog_response_invalid');
  }
});

test('a product that is not public answers as absent, not as an error', async () => {
  const gateway = createPublicCatalogGateway({origin: 'https://unit-toko.test', fetch: async () => new Response(null, {status: 404})});
  assert.equal(await gateway.item(item.publicId), null);
  await assert.rejects(() => gateway.item('../admin'), (error: unknown) => (error as {code?: string}).code === 'public_catalog_target_invalid');
});

test('public catalog addresses keep only search, category, order and page', () => {
  assert.equal(publicCatalogUrl({}), '/katalog');
  assert.equal(publicCatalogUrl({search: 'kopi', sort: 'name'}), '/katalog?q=kopi&urut=nama');
  assert.equal(publicCatalogUrl({category: 'PANTRY', offset: 48}), '/katalog?kategori=PANTRY&halaman=3');
});
