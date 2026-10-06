import { test } from 'node:test';
import assert from 'node:assert/strict';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';
import { releaseProposeOperation, releaseDecideOperation } from '../src/modules/inventory/stock-release-workflow';

class MemoryStorage {
  values = new Map<string, string>();
  get length() { return this.values.size; }
  key(index: number) { return [...this.values.keys()][index] ?? null; }
  getItem(key: string) { return this.values.get(key) ?? null; }
  setItem(key: string, value: string) { this.values.set(key, value); }
  removeItem(key: string) { this.values.delete(key); }
}
const actor = '9007199254740993';
const result = { proposalId: '9007199254740997', revision: '1', status: 'proposed', decisionId: null,
  movementId: null, valueTransitionId: null, transferredValue: null };

test('release recovery survives reload, stays lookup-only and clears only an exact confirmed original actor receipt', async () => {
  const storage = new MemoryStorage(), journal = new OperationJournal(() => storage);
  const entry = journal.remember(actor, releaseProposeOperation, 'original-release-key');
  journal.remember('77', releaseDecideOperation, 'another-person-key');
  const reloaded = new OperationJournal(() => storage);
  let fallback = 0;
  const legacy = { lookup: async () => { fallback++; throw new Error('Wrong owner'); }, close: async () => { fallback++; throw new Error('Unsupported close'); } };
  assert.deepEqual(operationRecoveryCapabilities(entry.operation), { canCheck: false, canClose: false, unavailable: 'adapter' });
  await assert.rejects(reconcileOperationRecovery(entry, reloaded, legacy));
  const unknown = { 'inventory.stock-release': { lookup: async () => null } };
  assert.deepEqual(operationRecoveryCapabilities(entry.operation, unknown), { canCheck: true, canClose: false, unavailable: null });
  assert.deepEqual(await reconcileOperationRecovery(entry, reloaded, legacy, unknown), { state: 'unconfirmed' });
  await assert.rejects(reconcileOperationRecovery(entry, reloaded, legacy, unknown, 'Close without checking'));
  assert.equal(reloaded.list(actor).length, 1);
  const committed = { 'inventory.stock-release': { lookup: async () => ({ state: 'committed' as const, result: { ...result, status: 'proposed' as const, revision: '1' as const } }) } };
  let current = true;
  await assert.rejects(reconcileOperationRecovery(entry, reloaded, legacy, {
    'inventory.stock-release': { lookup: async () => { current = false; return committed['inventory.stock-release'].lookup(); } },
  }, undefined, () => current));
  assert.equal(reloaded.list(actor).length, 1);
  assert.deepEqual(await reconcileOperationRecovery(entry, reloaded, legacy, committed), {
    state: 'committed', result: { id: result.proposalId, version: '1' },
  });
  assert.deepEqual(reloaded.list(actor), []);
  assert.equal(reloaded.list('77').length, 1);
  assert.equal(fallback, 0);
});
