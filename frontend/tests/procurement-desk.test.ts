import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { keptTarget } from '../src/modules/procurement/procurement-desk';
import { purchaseOrderLabel } from '../src/modules/procurement/purchase-order-client';

const source = (file: string) => readFileSync(new URL('../src/modules/procurement/' + file, import.meta.url), 'utf8');
const pages = ['ProcurementRequestWorkspace.tsx', 'PurchaseOrderWorkspace.tsx', 'ProcurementReceivingWorkspace.tsx'];

test('"Muat data terbaru" keeps the chosen organisation/toko/gudang/kanal when it is still assigned (F-022)', () => {
  const targets = [{ key: 'a' }, { key: 'b' }];
  assert.deepEqual(keptTarget(targets, 'b', item => item.key), { key: 'b' });
  assert.equal(keptTarget(targets, 'c', item => item.key), null, 'a location no longer assigned is not kept');
  assert.equal(keptTarget(targets, null, item => item.key), null);
  for (const page of pages) {
    const text = source(page);
    assert.match(text, /keep\.current = selected \? procurementLocationKey\(selected\.location\) : null; clearSelection\(\); setRevision\(value => value \+ 1\);/, page + ': the refresh remembers the location');
    assert.match(text, /keptTarget\(/, page + ': the reloaded targets restore it');
  }
});

test('after a save the new record appears in the list without a reload (F-022)', () => {
  for (const page of pages) assert.match(source(page), /const list = await gateway\.list\(account\.actorId, [^,]+, null, signal\); if \(valid\(\)\) setPage\(list\);/, page);
});

test('a supplier order is named by its SP number once approved; a proposal before approval has no order number yet (F-021)', () => {
  const base = { id: '7', state: 'pending' as const, decision: null };
  assert.equal(purchaseOrderLabel(base), 'Usulan pesanan 7');
  assert.equal(purchaseOrderLabel({ ...base, state: 'approved' as const, decision: { orderId: '1' } }), 'SP-000001');
  assert.equal(purchaseOrderLabel({ ...base, state: 'rejected' as const, decision: { orderId: null } }), 'Usulan pesanan 7');
  const text = source('PurchaseOrderWorkspace.tsx');
  assert.match(text, /purchaseOrderLabel\(item\)/, 'list rows');
  assert.match(text, /purchaseOrderLabel\(proposal\)/, 'detail');
});
