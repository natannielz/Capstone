import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AccessFailure } from '../src/modules/administration/workflow';
import { acceptCurrent, cartRows } from '../src/modules/ordering/CartWorkspace';
import { addToCartMessage } from '../src/modules/ordering/AddToCartPanel';
import { CartOutcomeUnknown } from '../src/modules/ordering/cart-client';
import type { BuyerVariant } from '../src/modules/catalog/catalog-buyer-client';

const saved = (skuId: string, packages: number, priceVersion = '1') => ({ skuId, definitionId: '7' + skuId, definitionVersion: '1', priceId: '9' + skuId, priceVersion, packages });
function variant(skuId: string, overrides: { priceVersion?: string; minimum?: number; increment?: number; definitionVersion?: string; price?: boolean } = {}) {
  return { skuId, publicId: '00000065-0000-4000-8000-000000000101', familyName: 'Air mineral 600 ml', code: 'AIR-600',
    definition: { id: '7' + skuId, version: overrides.definitionVersion ?? '1', packageLabel: 'Karton', baseUnitCode: 'botol', baseUnitsPerPackage: 24,
      minimumOrderPackages: overrides.minimum ?? 1, orderIncrementPackages: overrides.increment ?? 1 },
    currentPrice: overrides.price === false ? null : { id: '9' + skuId, version: overrides.priceVersion ?? '1', currency: 'IDR', unitPrice: '96000.00' } } as unknown as BuyerVariant;
}

test('each saved line is matched with what the buyer may see now', () => {
  const rows = cartRows([saved('11', 2), saved('12', 1), saved('13', 1), saved('14', 1)],
    new Map([['11', variant('11')], ['12', variant('12', { priceVersion: '2' })], ['14', variant('14', { price: false })]]));
  assert.deepEqual(rows.map(row => row.state), ['ready', 'price-changed', 'unavailable', 'unavailable']);
  assert.equal(rows[2].current, null);
});

test('accepting the newest price keeps the quantity, and a new package rule moves it to the nearest allowed amount', () => {
  assert.deepEqual(acceptCurrent(saved('12', 3), variant('12', { priceVersion: '2' })), saved('12', 3, '2'));
  assert.equal(acceptCurrent(saved('12', 3), variant('12', { definitionVersion: '2', minimum: 2, increment: 2 })).packages, 4);
  assert.equal(acceptCurrent(saved('12', 1), variant('12', { definitionVersion: '2', minimum: 6, increment: 6 })).packages, 6);
  assert.equal(acceptCurrent(saved('12', 12), variant('12', { definitionVersion: '2', minimum: 6, increment: 6 })).packages, 12);
});

test('cart messages tell the buyer the next step', () => {
  assert.match(addToCartMessage(new CartOutcomeUnknown({} as never, 'k')), /tidak akan masuk dua kali/);
  assert.match(addToCartMessage(new AccessFailure(409, 'order_selection_changed')), /Buka keranjang/);
  assert.match(addToCartMessage(new AccessFailure(400, 'cart_full')), /100 barang/);
  assert.match(addToCartMessage(new AccessFailure(401, 'unauthenticated')), /Masuk kembali/);
});
