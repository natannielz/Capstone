import { test } from 'node:test';
import assert from 'node:assert/strict';
import { nonBuyerCatalogHref } from '../src/modules/catalog/catalog-history';

const product = '00000065-0000-4000-8000-000000000201';

test('a signed-in account without catalog.read is sent to the sample catalog instead of a refusal on /shop', () => {
  // The same rule as G-029 in reverse: the organisation catalog is for catalog.read holders; others see the samples with a note.
  assert.equal(nonBuyerCatalogHref(['catalog.public.approve', 'pricing.approve'], undefined, '?q=kopi&kategori=PANTRY&urut=nama&halaman=3'),
    '/katalog?q=kopi&kategori=PANTRY&urut=nama');
  assert.equal(nonBuyerCatalogHref(['shipment.dispatch'], product, ''), '/katalog/' + product);
  assert.equal(nonBuyerCatalogHref([], undefined, ''), '/katalog');
});

test('a catalog reader stays on /shop', () => {
  assert.equal(nonBuyerCatalogHref(['catalog.read'], undefined, '?q=kopi'), null);
  assert.equal(nonBuyerCatalogHref(['catalog.read', 'order.submit'], product, ''), null);
});
