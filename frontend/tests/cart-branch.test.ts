import { test } from 'node:test';
import assert from 'node:assert/strict';
import { cartBranch, rememberCartBranch } from '../src/modules/ordering/cart-branch';

const option = (branchId: string) => ({ context: { organizationId: '1', branchId, divisionId: null }, organizationName: 'BNI', branchName: 'Cabang ' + branchId, divisionName: null });
function memory() { const values = new Map<string, string>(); return { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); } }; }

test('the buyer chooses the branch once, even when only one is allowed; the choice is kept for their own account only', () => {
  const storage = memory();
  assert.equal(cartBranch('7', [], storage), null);
  assert.equal(cartBranch('7', [option('2')], storage), null, 'The browser never chooses a branch silently');
  rememberCartBranch('7', option('2').context, storage);
  assert.equal(cartBranch('7', [option('2')], storage)?.context.branchId, '2');
  assert.equal(cartBranch('7', [option('2'), option('3')], storage)?.context.branchId, '2');
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

test('the header can read the remembered branch without fetching the branch list, and nothing before a first choice', async () => {
  const { rememberedCartContext } = await import('../src/modules/ordering/cart-branch');
  const storage = memory();
  assert.equal(rememberedCartContext('7', storage), null);
  rememberCartBranch('7', option('2').context, storage);
  assert.deepEqual(rememberedCartContext('7', storage), { organizationId: '1', branchId: '2', divisionId: null });
  rememberCartBranch('7', { organizationId: '1', branchId: '2', divisionId: '9' }, storage);
  assert.deepEqual(rememberedCartContext('7', storage), { organizationId: '1', branchId: '2', divisionId: '9' });
  for (const broken of ['1:2', 'x:2:', '1:0:', '1:2:3:4', '']) {
    const bad = { getItem: () => broken, setItem: () => undefined };
    assert.equal(rememberedCartContext('7', bad), null, broken);
  }
});
