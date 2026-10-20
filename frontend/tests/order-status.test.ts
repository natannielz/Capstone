import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buyerOrderStatus } from '../src/modules/ordering/order-status';
import { reorderChoices } from '../src/modules/ordering/cart-model';
import { statusTone } from '../src/lib/status/dictionary';
import type { BuyerVariant } from '../src/modules/catalog/catalog-buyer-client';

const order = (progress: Partial<{ spendingState: string; acceptedSuborders: string[]; rejectedSuborders: string[]; cancelled: boolean }>, suborders = ['51']) =>
  ({ submission: { suborderIds: suborders }, progress: { spendingState: 'NotRequired', acceptedSuborders: [], rejectedSuborders: [], cancelled: false, ...progress } }) as never;

test('each order gets one short status a buyer recognises, with a tone that matches it', () => {
  const cases: Array<[unknown, string, string]> = [
    [order({ spendingState: 'Pending' }), 'Menunggu persetujuan', 'warning'],
    [order({}), 'Menunggu toko', 'warning'],
    [order({ acceptedSuborders: ['51'] }), 'Diproses', 'info'],
    [order({ acceptedSuborders: ['51'] }, ['51', '52']), 'Menunggu toko', 'warning'],
    [order({ acceptedSuborders: ['51'], rejectedSuborders: ['52'] }, ['51', '52']), 'Sebagian ditolak', 'warning'],
    [order({ rejectedSuborders: ['51'] }), 'Ditolak', 'danger'],
    [order({ spendingState: 'Rejected' }), 'Ditolak', 'danger'],
    [order({ cancelled: true, acceptedSuborders: ['51'] }), 'Dibatalkan', 'danger'],
  ];
  for (const [value, label, tone] of cases) {
    const status = buyerOrderStatus(value as never);
    assert.equal(status.label, label); assert.equal(statusTone(status.label), tone); assert.ok(status.detail.length > 10);
  }
});

const saved = (skuId: string, packages: number) => ({ skuId, definitionId: '7' + skuId, definitionVersion: '1', priceId: '9' + skuId, priceVersion: '1', packages });
const variant = (skuId: string, minimum = 1, increment = 1, priced = true) => ({ skuId, definition: { id: '7' + skuId, version: '2', minimumOrderPackages: minimum, orderIncrementPackages: increment },
  currentPrice: priced ? { id: '9' + skuId, version: '4' } : null }) as unknown as BuyerVariant;

test('ordering again uses today\'s package and price, and names what can no longer be ordered', () => {
  const result = reorderChoices([saved('11', 3), saved('12', 2), saved('13', 1)], new Map([['11', variant('11', 2, 2)], ['13', variant('13', 1, 1, false)]]));
  assert.deepEqual(result.choices.map(item => [item.choice.skuId, item.choice.packages, item.choice.priceVersion, item.rule.increment]), [['11', 4, '4', 2]]);
  assert.deepEqual(result.unavailable, ['12', '13']);
});
