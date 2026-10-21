import { test } from 'node:test';
import assert from 'node:assert/strict';
import { OperationJournal, submitTrackedRequest, reconcileOperationRecovery, operationRecoveryCapabilities } from '../src/modules/administration/operation-journal';
import { MutationLedger } from '../src/modules/administration/workflow';
import { priceOperationKey, priceRecoverySku, PriceMutationLedger } from '../src/modules/catalog/catalog-price-recovery';
import { identityDestinations, identityReturnPath } from '../src/modules/identity/identity-workflow';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
class MemoryStorage {
  values = new Map<string, string>(); get length() { return this.values.size; }
  key(index: number) { return [...this.values.keys()][index] ?? null; }
  getItem(key: string) { return this.values.get(key) ?? null; }
  setItem(key: string, value: string) { this.values.set(key, value); }
  removeItem(key: string) { this.values.delete(key); }
}
const actor = '9007199254740993', sku = '9007199254740995';
const legacy = { lookup: async () => { throw new Error('Legacy must not own price receipts'); }, close: async () => { throw new Error('No close'); } };
test('price-only navigation and exact safe return path expose the assigned workspace', () => {
  assert.equal(identityReturnPath('/catalog/prices'), '/catalog/prices');
  assert.equal(identityReturnPath('/catalog/prices/../identity/account'), '/workspace');
  assert.ok(identityDestinations(['pricing.approve']).some(item => item.href === '/catalog/prices'));
  assert.ok(!identityDestinations(['catalog.manage']).some(item => item.href === '/catalog/prices'));
  assert.equal(recoveryPage('catalog.price.approve').href, '/catalog/prices');
});
for (const action of ['propose', 'approve', 'reject', 'withdraw'] as const) test(action + ' survives reload with only original scoped reference and cannot be closed or resubmitted', async () => {
  const storage = new MemoryStorage(), journal = new OperationJournal(() => storage), key = priceOperationKey(sku, 'original-nonce');
  assert.equal(priceRecoverySku(key), sku);
  const operation = 'catalog.price.' + action, ledger = new MutationLedger(() => key); let sends = 0;
  await assert.rejects(submitTrackedRequest(ledger, journal, actor, operation, operation, { reason: 'private-rationale', amount: '1.00' }, async () => { sends++; throw new TypeError('Lost response'); }));
  const reloaded = new OperationJournal(() => storage), entry = reloaded.list(actor)[0];
  assert.deepEqual(Object.keys(entry).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.doesNotMatch([...storage.values.values()].join(''), /private-rationale|amount|1\.00/);
  assert.equal(operationRecoveryCapabilities(operation).canCheck, false);
  assert.equal(operationRecoveryCapabilities(operation, { 'catalog.price': { lookup: async () => null } }).canClose, false);
  assert.deepEqual(await reconcileOperationRecovery(entry, reloaded, legacy, { 'catalog.price': { lookup: async () => null } }), { state: 'unconfirmed' });
  await assert.rejects(() => reconcileOperationRecovery(entry, reloaded, legacy, { 'catalog.price': { lookup: async () => null } }, 'Close it'));
  const result = { proposalId: '101', skuId: sku, revision: '1', status: action === 'propose' ? 'proposed' as const : action,
    decisionId: action === 'propose' ? null : '102', priceId: action === 'approve' ? '103' : null, priceVersion: action === 'approve' ? '1' : null };
  await assert.rejects(() => reconcileOperationRecovery(entry, reloaded, legacy, { 'catalog.price': { lookup: async () => ({ state: 'committed', result: { ...result, skuId: '9' } }) } }));
  assert.equal(reloaded.list(actor).length, 1);
  const confirmed = await reconcileOperationRecovery(entry, reloaded, legacy, { 'catalog.price': { lookup: async () => ({ state: 'committed', result }) } });
  assert.equal(confirmed.state, 'committed'); assert.equal(reloaded.list(actor).length, 0); assert.equal(sends, 1);
});
test('recovery keys reject unsafe identifiers and malformed persisted scopes', () => {
  for (const key of ['price:01:nonce', 'price:9223372036854775808:nonce', 'price:1:nonce\n', 'other:1:nonce', 'price:1:']) assert.throws(() => priceRecoverySku(key));
  assert.throws(() => priceOperationKey('1\n', 'nonce'));
  const ledger = new PriceMutationLedger(); ledger.useSku(sku);
  const key = ledger.begin('catalog.price.propose', { skuId: sku });
  assert.equal(priceRecoverySku(key), sku);
  ledger.fail('catalog.price.propose', new TypeError('Lost response')); ledger.useSku('7');
  assert.throws(() => ledger.begin('catalog.price.propose', { skuId: '7' }));
  assert.equal(ledger.begin('catalog.price.propose', { skuId: sku }), key);
});
