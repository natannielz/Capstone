import { test } from 'node:test';
import assert from 'node:assert/strict';
import { addToCart, cartLineState, pickCart, removeFromCart, setCartQuantity } from '../src/modules/ordering/cart-model';

const line = (skuId: string, packages: number, priceVersion = '1') => ({ skuId, definitionId: '7' + skuId, definitionVersion: '1', priceId: '9' + skuId, priceVersion, packages });
const draft = (id: string, updatedAt: string, skuIds: string[] = ['11']) => ({ id, revision: '3', createdAt: '2026-10-01T00:00:00Z', updatedAt,
  input: { context: { organizationId: '1', branchId: '2', divisionId: null }, selections: skuIds.map(sku => line(sku, 1)), delivery: null, purchaseReference: null } });

test('the cart is the branch draft changed most recently, so a buyer keeps one basket per branch', () => {
  assert.equal(pickCart([]), null);
  assert.equal(pickCart([draft('40', '2026-10-20T08:00:00Z'), draft('41', '2026-10-21T08:00:00Z'), draft('39', '2026-10-21T07:00:00Z')])?.id, '41');
  assert.equal(pickCart([draft('40', '2026-10-21T08:00:00Z'), draft('41', '2026-10-21T08:00:00Z')])?.id, '41');
});

test('adding a package already in the cart adds to its quantity and takes the newest price the buyer just saw', () => {
  const rule = { minimum: 2, increment: 2 };
  assert.deepEqual(addToCart([], line('11', 2), rule), [line('11', 2)]);
  assert.deepEqual(addToCart([line('11', 4), line('12', 1)], line('11', 2, '5'), rule), [line('11', 6, '5'), line('12', 1)]);
  assert.deepEqual(addToCart([line('12', 1)], line('11', 2), rule), [line('12', 1), line('11', 2)]);
});

test('a full cart or a quantity over the order limit is refused instead of silently trimmed', () => {
  const rule = { minimum: 1, increment: 1 };
  const full = Array.from({ length: 100 }, (_, index) => line(String(100 + index), 1));
  assert.throws(() => addToCart(full, line('99', 1), rule), (error: unknown) => (error as { code?: string }).code === 'cart_full');
  assert.equal(addToCart(full, line('100', 1), rule)[0].packages, 2);
  assert.throws(() => addToCart([line('11', 999_999)], line('11', 2), rule), (error: unknown) => (error as { code?: string }).code === 'order_package_quantity_invalid');
});

test('changing a quantity keeps the package rules, and removing the last line empties the cart', () => {
  const rule = { minimum: 10, increment: 5 };
  assert.deepEqual(setCartQuantity([line('11', 10), line('12', 1)], '11', 15, rule), [line('11', 15), line('12', 1)]);
  assert.throws(() => setCartQuantity([line('11', 10)], '11', 12, rule), (error: unknown) => (error as { code?: string }).code === 'order_package_quantity_invalid');
  assert.throws(() => setCartQuantity([line('11', 10)], '11', 5, rule), (error: unknown) => (error as { code?: string }).code === 'order_package_quantity_invalid');
  assert.deepEqual(removeFromCart([line('11', 10), line('12', 1)], '11'), [line('12', 1)]);
  assert.deepEqual(removeFromCart([line('11', 10)], '11'), []);
});

test('a line is ready only when its saved package and price are still the current ones', () => {
  const current = { skuId: '11', definitionId: '711', definitionVersion: '1', priceId: '911', priceVersion: '1' };
  assert.equal(cartLineState(line('11', 1), current), 'ready');
  assert.equal(cartLineState(line('11', 1, '2'), current), 'price-changed');
  assert.equal(cartLineState({ ...line('11', 1), definitionVersion: '2' }, current), 'package-changed');
  assert.equal(cartLineState(line('11', 1), { ...current, priceId: null, priceVersion: null }), 'unavailable');
  assert.equal(cartLineState(line('11', 1), null), 'unavailable');
});

test('line and cart amounts are exact decimal strings, never floating point', async () => {
  const { cartAmount, lineAmount } = await import('../src/modules/ordering/cart-model');
  assert.equal(lineAmount('96000.00', 3), '288000.00');
  assert.equal(lineAmount('0.10', 3), '0.30');
  assert.equal(lineAmount('999999999999999.99', 1), '999999999999999.99');
  assert.equal(cartAmount(['0.10', '0.20']), '0.30');
  assert.equal(cartAmount([]), '0.00');
});
