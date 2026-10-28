import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { keptTarget, refreshAfterConfirmedSave, restoreKeptLocation, takeKeptKey } from '../src/modules/procurement/procurement-desk';
import { purchaseOrderLabel } from '../src/modules/procurement/purchase-order-client';

const source = (file: string) => readFileSync(new URL('../src/modules/procurement/' + file, import.meta.url), 'utf8');
const pages = ['ProcurementRequestWorkspace.tsx', 'PurchaseOrderWorkspace.tsx', 'ProcurementReceivingWorkspace.tsx'];

function trace() {
  const calls: string[] = [];
  return {
    calls,
    after: (over: Partial<{ valid: () => boolean; list: () => Promise<void>; detail: () => Promise<void> }> = {}) => ({
      valid: over.valid ?? (() => true),
      confirmed: () => { calls.push('confirmed'); },
      list: over.list ?? (async () => { calls.push('list'); }),
      detail: over.detail ?? (async () => { calls.push('detail'); }),
    }),
  };
}

test('after a CONFIRMED save the list is read again, before the detail (F-022)', async () => {
  const t = trace();
  await refreshAfterConfirmedSave(async () => { t.calls.push('save'); return { id: '9' }; }, t.after());
  assert.deepEqual(t.calls, ['save', 'confirmed', 'list', 'detail']);
});

test('the list is NOT read when the save throws, is not sent or is uncertain (F-022)', async () => {
  for (const failure of [new Error('rejected'), Object.assign(new Error('not sent'), { status: 0, code: 'request_not_sent' }), Object.assign(new Error('uncertain'), { status: 504, code: 'outcome_unknown' })]) {
    const t = trace();
    await assert.rejects(refreshAfterConfirmedSave(async () => { t.calls.push('save'); throw failure; }, t.after()), failure);
    assert.deepEqual(t.calls, ['save'], 'no list, no detail, no notice');
  }
});

test('a view that is no longer current reads and announces nothing (F-022)', async () => {
  const t = trace();
  await refreshAfterConfirmedSave(async () => 'saved', t.after({ valid: () => false }));
  assert.deepEqual(t.calls, []);
});

test('a failed detail read still leaves the list fresh, and the failure is reported (F-022)', async () => {
  const t = trace(), failure = new Error('detail down');
  await assert.rejects(refreshAfterConfirmedSave(async () => 'saved', t.after({ detail: async () => { t.calls.push('detail'); throw failure; } })), failure);
  assert.deepEqual(t.calls, ['confirmed', 'list', 'detail']);
  const u = trace(), listFailure = new Error('list down');
  await assert.rejects(refreshAfterConfirmedSave(async () => 'saved', u.after({ list: async () => { throw listFailure; } })), listFailure);
  assert.deepEqual(u.calls, ['confirmed', 'detail'], 'the detail is still read when the list read fails');
});

test('"Muat data terbaru" keeps the chosen organisation/toko/gudang/kanal while it is still assigned (F-022)', async () => {
  const targets = [{ key: 'a' }, { key: 'b' }], read: string[] = [];
  const list = async (target: { key: string }) => { read.push(target.key); return ['rows of ' + target.key]; };
  assert.deepEqual(await restoreKeptLocation(targets, 'b', item => item.key, list), { target: { key: 'b' }, page: ['rows of b'] });
  assert.deepEqual(read, ['b']);
  assert.equal(await restoreKeptLocation(targets, 'c', item => item.key, list), null, 'a location no longer assigned is dropped');
  assert.equal(await restoreKeptLocation(targets, null, item => item.key, list), null);
  assert.deepEqual(read, ['b'], 'nothing is read for a dropped location');
  assert.deepEqual(keptTarget(targets, 'a', item => item.key), { key: 'a' });
});

test('the remembered location is used once, so an aborted read cannot restore it later (F-022)', () => {
  const keep = { current: 'a:b' as string | null };
  assert.equal(takeKeptKey(keep), 'a:b');
  assert.equal(keep.current, null);
  assert.equal(takeKeptKey(keep), null);
});

test('the three desks use these decisions (F-022)', () => {
  for (const page of pages) {
    const text = source(page);
    assert.match(text, /keep\.current = selected \? procurementLocationKey\(selected\.location\) : null; clearSelection\(\); setRevision\(value => value \+ 1\);/, page + ': the refresh remembers the location');
    assert.match(text, /restoreKeptLocation\(/, page + ': the reloaded targets restore it');
    assert.match(text, /takeKeptKey\(keep\)[\s\S]*gateway\.targets\(/, page + ': the key is taken before the targets are read');
    assert.doesNotMatch(text, /keep\.current = null;/, page + ': no late clearing after a successful read');
    assert.match(text, /refreshAfterConfirmedSave\(/, page + ': saves refresh the list');
  }
  assert.equal((source('ProcurementRequestWorkspace.tsx').match(/refreshAfterConfirmedSave\(/g) ?? []).length, 2, 'submit and decision');
});

test('a supplier order is named by its SP number once approved; a proposal before approval has no order number yet (F-021)', () => {
  const base = { id: '7', state: 'pending' as const, decision: null };
  assert.equal(purchaseOrderLabel(base), 'Usulan pesanan 7');
  assert.equal(purchaseOrderLabel({ ...base, state: 'approved' as const, decision: { orderId: '1' } }), 'SP-000001');
  assert.equal(purchaseOrderLabel({ ...base, state: 'rejected' as const, decision: { orderId: null } }), 'Usulan pesanan 7');
  const text = source('PurchaseOrderWorkspace.tsx');
  assert.match(text, /purchaseOrderLabel\(item\)/, 'list rows');
  assert.match(text, /purchaseOrderLabel\(proposal\)/, 'detail');
  assert.doesNotMatch(text, /' · Pesanan ' \+/, 'the SP number is shown once, not next to a bare order id');
});
