import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { BuyBox } from '../src/modules/catalog/BuyBox';
import type { BuyerVariant } from '../src/modules/catalog/catalog-buyer-client';

const item = { skuId: '100', publicId: 'p-1', code: 'PEN-12', familyName: 'Pensil', categoryCode: 'ATK', description: null,
  definition: { id: '200', version: '1', packageLabel: 'Dus', packageKind: 'sealed', baseUnitsPerPackage: 12, baseUnitCode: 'piece',
    minimumOrderPackages: 1, orderIncrementPackages: 1, attributes: [] },
  currentPrice: { id: '300', version: '1', currency: 'IDR', unitPrice: '12000.00' } } as unknown as BuyerVariant;

test('a catalog-only account reads the product without a buy box or a branch lookup', () => {
  const html = renderToStaticMarkup(createElement(BuyBox, { item, actorId: '7', actions: ['catalog.read'], isCurrent: () => true }));
  assert.match(html, /Akun Anda dapat melihat katalog/);
  assert.doesNotMatch(html, /Pesan untuk|Tambah ke keranjang|Memeriksa cabang/);
});

test('an account that may order gets the buy box', () => {
  const html = renderToStaticMarkup(createElement(BuyBox, { item, actorId: '7', actions: ['catalog.read', 'order.submit'], isCurrent: () => true }));
  assert.match(html, /aria-label="Pemesanan"/);
  assert.doesNotMatch(html, /Akun Anda dapat melihat katalog/);
});
