import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createBankReceiptGateway, parseBankReceiptResult, prepareBankReceipt } from '../src/modules/billing/bank-receipt-client';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';
import { recoveryPage, operationLabel } from '../src/modules/administration/operation-recovery-navigation';

const actorId = '9007199254740993', key = 'br.confirm.9007199254740994.2.11111111-1111-4111-8111-111111111111';
const shown = { importId: '9007199254740994', rowNumber: 2, binding: 'a'.repeat(64), row: { number: 2, transactionId: '0001', bookedDate: '2026-09-27', direction: 'credit' as const, amount: '1000.25', payerName: null, reference: 'SHARED', identityCandidate: 'b'.repeat(64) },
  account: { accountId: '41', versionId: '71', version: '1', details: { bankCode: 'FIXTUREBANK', accountNumber: '0000123456789', holderName: 'Synthetic store', currency: 'IDR' as const }, effectiveAt: '2026-09-26T00:00:00Z' },
  policy: { id: '920', revision: '1', trustedSourceReference: 'Synthetic bank portal', operatingOwnerReference: 'Synthetic Finance owner', approvalReference: 'Synthetic approval', cashMapping: 'bank_cash', suspenseMapping: 'unidentified_cash', expiresAt: '2026-09-29T00:00:00Z' }, existingReceiptId: null };
const observed = { accountNumber: '0000123456789', transactionId: '0001', bookedDate: '2026-09-27', amount: '1000.25' };
const saved = { confirmationId: '9100', receiptId: '9007199254740995', importId: shown.importId, rowNumber: 2, createdReceipt: true, recordedAt: '2026-09-28T00:00:00Z', operation: 'billing.bank-receipt.confirm' as const };
test('receipt decision freezes exact independent observation and rejects guessed money identity date or attestation', () => {
  const input = { ...observed }, command = prepareBankReceipt(actorId, shown, input, 'Independent source', true, 'Verified'); input.amount = '1.00';
  assert.equal(command.body.observed.amount, '1000.25');
  for (const bad of [{ ...observed, amount: '1.00' }, { ...observed, accountNumber: '123456789' }, { ...observed, transactionId: '1' }, { ...observed, bookedDate: '2026-02-30' }]) assert.throws(() => prepareBankReceipt(actorId, shown, bad, 'source', true, 'Review'));
  assert.throws(() => prepareBankReceipt(actorId, shown, observed, 'source', false, 'Review'));
  assert.throws(() => parseBankReceiptResult({ ...saved, receiptId: Number(saved.receiptId) }, key));
  assert.throws(() => parseBankReceiptResult({ ...saved, importId: '2' }, key));
  assert.throws(() => parseBankReceiptResult({ ...saved, paid: true }, key));
});
test('receipt gateway sends exact observed facts and actor CSRF headers and blocks stale or different-row sends', async () => {
  let writes = 0; const command = prepareBankReceipt(actorId, shown, observed, 'Independent source', true, 'Verified');
  const gateway = createBankReceiptGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); const path = new URL(request.url).pathname;
    if (path === '/api/v1/identity/csrf') return Response.json({ requestToken: 'fixture-csrf' });
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(request.headers.get('X-CSRF-Token'), 'fixture-csrf'); assert.equal(request.cache, 'no-store');
    if (path.endsWith('/operations/query')) return Response.json({ status: 'committed', result: saved });
    writes++; assert.equal(path, '/api/v1/billing/bank-receipts/confirmations'); assert.equal(request.headers.get('If-Match'), '"0"'); assert.equal(request.headers.get('Idempotency-Key'), key);
    assert.deepEqual(await request.json(), command.body); return Response.json(saved);
  } }));
  await assert.rejects(() => gateway.send(command, key, () => false)); await assert.rejects(() => gateway.send(command, key.replace('.2.', '.3.'), () => true)); assert.equal(writes, 0);
  assert.deepEqual(await gateway.send(command, key, () => true), saved); assert.equal(writes, 1);
  assert.deepEqual((await gateway.recover(actorId, key))?.result, saved); assert.equal(writes, 1);
});
test('financial recovery stores metadata only and cannot close an unknown confirmation or accept another row receipt', async () => {
  const values = new Map<string, string>(); const journal = new OperationJournal(() => ({ get length() { return values.size; }, key: index => [...values.keys()][index] ?? null,
    getItem: key => values.get(key) ?? null, setItem: (key, value) => { values.set(key, value); }, removeItem: key => { values.delete(key); } }));
  const entry = journal.remember(actorId, saved.operation, key); assert.deepEqual(Object.keys(entry).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.equal(recoveryPage(entry.operation).href, '/billing/bank-receipts'); assert.equal(operationLabel(entry.operation), 'Konfirmasi kredit bank'); assert.equal(operationRecoveryCapabilities(entry.operation).canClose, false);
  const legacy = { lookup: async () => { throw new Error('Wrong owner'); }, close: async () => { throw new Error('Cannot close'); } };
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, { 'billing.bank-receipt': { lookup: async () => null } })).state, 'unconfirmed'); assert.equal(values.size, 1);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'billing.bank-receipt': { lookup: async () => ({ state: 'committed', result: { ...saved, rowNumber: 3 } }) } })); assert.equal(values.size, 1);
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, { 'billing.bank-receipt': { lookup: async () => ({ state: 'committed', result: saved }) } })).state, 'committed'); assert.equal(values.size, 0);
});
