import { test } from 'node:test';
import assert from 'node:assert/strict';
import { cartBranch, rememberCartBranch } from '../src/modules/ordering/cart-branch';

const option = (branchId: string) => ({ context: { organizationId: '1', branchId, divisionId: null }, organizationName: 'BNI', branchName: 'Cabang ' + branchId, divisionName: null });
function memory() { const values = new Map<string, string>(); return { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); } }; }

test('one branch is chosen for the buyer; with several, the last one they used is kept for their own account only', () => {
  const storage = memory();
  assert.equal(cartBranch('7', [], storage), null);
  assert.equal(cartBranch('7', [option('2')], storage)?.context.branchId, '2');
  assert.equal(cartBranch('7', [option('2'), option('3')], storage), null);
  rememberCartBranch('7', option('3').context, storage);
  assert.equal(cartBranch('7', [option('2'), option('3')], storage)?.context.branchId, '3');
  assert.equal(cartBranch('8', [option('2'), option('3')], storage), null);
  assert.equal(cartBranch('7', [option('2'), option('4')], storage), null);
});

test('a blocked or broken browser store never stops the cart', () => {
  const broken = { getItem: () => { throw new Error('denied'); }, setItem: () => { throw new Error('denied'); } };
  assert.equal(cartBranch('7', [option('2'), option('3')], broken), null);
  assert.doesNotThrow(() => rememberCartBranch('7', option('3').context, broken));
});
