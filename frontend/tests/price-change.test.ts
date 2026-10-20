import { test } from 'node:test';
import assert from 'node:assert/strict';
import { priceChange } from '../src/modules/catalog/catalog-price-workflow';

test('a proposed price is read against the current one as an exact amount and a rounded percentage', () => {
  assert.deepEqual(priceChange({ currency: 'IDR', unitPrice: '18000.00' }, { currency: 'IDR', unitPrice: '20000.00' }),
    { kind: 'up', difference: '2000.00', percent: '11,1', large: false });
  assert.deepEqual(priceChange({ currency: 'IDR', unitPrice: '20000.00' }, { currency: 'IDR', unitPrice: '15000.00' }),
    { kind: 'down', difference: '5000.00', percent: '25,0', large: true });
  assert.deepEqual(priceChange({ currency: 'IDR', unitPrice: '3500.00' }, { currency: 'IDR', unitPrice: '3500.00' }),
    { kind: 'same', difference: '0.00', percent: '0,0', large: false });
  assert.deepEqual(priceChange(null, { currency: 'IDR', unitPrice: '3500.00' }), { kind: 'first', difference: null, percent: null, large: false });
  assert.deepEqual(priceChange({ currency: 'IDR', unitPrice: '0.00' }, { currency: 'IDR', unitPrice: '1.00' }), { kind: 'up', difference: '1.00', percent: null, large: true });
  assert.equal(priceChange({ currency: 'USD', unitPrice: '1.00' }, { currency: 'IDR', unitPrice: '1.00' }).kind, 'currency');
  assert.equal(priceChange({ currency: 'IDR', unitPrice: '999999999999999.99' }, { currency: 'IDR', unitPrice: '0.01' }).percent, '100,0');
});
