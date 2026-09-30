import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createPaymentAllocationGateway, parsePaymentAllocationResult, preparePaymentAllocation, type PaymentAllocationPreview } from '../src/modules/billing/payment-allocation-client';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';

const actor = '9007199254740993', key = 'pa.9007199254740994.9007199254740995.11111111-1111-4111-8111-111111111111';
const preview: PaymentAllocationPreview = { binding: 'a'.repeat(64), procedureReference: 'Synthetic allocation procedure', amount: '1000.25', unapplied: '2999.75',
  view: { verificationId: '9007199254740994', receiptId: '9007199254740995', declarationId: '90', buyingOrganizationId: '200', buyerName: 'Cabang sintetis', transactionId: '0001', bookedDate: '2026-09-27',
    receiptVersion: '0', received: '4000.00', allocated: '0.00', reservedForRefund: '0.00', available: '4000.00', invoices: [{ invoiceId: '91', numberEntity: '100', numberChannel: '103', numberYear: 2026, number: '1',
      version: '0', total: '2000.25', paid: '0.00', credited: '0.00', outstanding: '2000.25', paymentState: 'unpaid', dueDate: '2026-10-27', aging: 'not_due', daysOverdue: 0 }] },
  invoices: [{ invoiceId: '91', version: '1', amount: '1000.25', remaining: '1000.00' }] };
const result = { allocationId: '92', verificationId: preview.view.verificationId, receiptId: preview.view.receiptId, receiptVersion: '1', amount: '1000.25', unapplied: '2999.75',
  recordedAt: '2026-09-28T00:00:00Z', operation: 'billing.payment.allocate' as const, invoices: preview.invoices };
test('allocation freezes exact amounts and rejects inconsistent receipt or invoice balances and mismatched results', () => {
  const copy = structuredClone(preview), command = preparePaymentAllocation(actor, copy, 'Reviewed invoice choice'); copy.invoices[0].amount = '999.00';
  assert.equal(command.body.input.lines[0].amount, '1000.25');
  assert.ok(Object.isFrozen(command) && Object.isFrozen(command.body) && Object.isFrozen(command.body.input) && Object.isFrozen(command.body.input.lines) && Object.isFrozen(command.body.input.lines[0]));
  for (const change of [
    { ...preview, unapplied: '3000.00' },
    { ...preview, invoices: [{ ...preview.invoices[0], remaining: '999.99' }] },
    { ...preview, view: { ...preview.view, reservedForRefund: '1.00' } },
    { ...preview, view: { ...preview.view, invoices: [{ ...preview.view.invoices[0], paid: '0.01' }] } },
  ]) assert.throws(() => preparePaymentAllocation(actor, change, 'Reviewed'));
  assert.throws(() => parsePaymentAllocationResult({ ...result, receiptId: Number(result.receiptId) }, key));
  assert.throws(() => parsePaymentAllocationResult({ ...result, receiptId: '3' }, key));
  assert.throws(() => parsePaymentAllocationResult({ ...result, amount: '1000.26' }, key));
  assert.throws(() => parsePaymentAllocationResult({ ...result, paid: true }, key));
});
test('allocation sends exact frozen source versions, CSRF and actor with no stale or off-owner send', async () => {
  let writes = 0; const command = preparePaymentAllocation(actor, preview, 'Reviewed');
  const gateway = createPaymentAllocationGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (new URL(request.url).pathname === '/api/v1/identity/csrf') return Response.json({ requestToken: 'csrf' });
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(request.cache, 'no-store');
    if (request.url.endsWith('/operations/query')) return Response.json({ status: 'committed', result });
    writes++; assert.equal(request.headers.get('If-Match'), '"0"'); assert.equal(request.headers.get('Idempotency-Key'), key); assert.deepEqual(await request.json(), command.body); return Response.json(result);
  } }));
  await assert.rejects(() => gateway.send(command, key, () => false)); await assert.rejects(() => gateway.send(command, key.replace('9007199254740995', '9'), () => true)); assert.equal(writes, 0);
  assert.deepEqual(await gateway.send(command, key, () => true), result); assert.equal(writes, 1);
  assert.deepEqual((await gateway.recover(actor, key))?.result, result); assert.equal(writes, 1);
});
test('allocation recovery stores only operation references and preserves uncertain or mismatched results', async () => {
  const rows = new Map<string, string>(), journal = new OperationJournal(() => ({ get length() { return rows.size; }, key: index => [...rows.keys()][index] ?? null,
    getItem: key => rows.get(key) ?? null, setItem: (key, value) => { rows.set(key, value); }, removeItem: key => { rows.delete(key); } }));
  const entry = journal.remember(actor, result.operation, key), legacy = { lookup: async () => { throw new Error('Wrong owner'); }, close: async () => { throw new Error('Cannot close'); } };
  assert.deepEqual(Object.keys(entry).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.equal(recoveryPage(entry.operation).href, '/billing/payment-reviews'); assert.equal(operationRecoveryCapabilities(entry.operation).canClose, false);
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, { 'billing.payment.allocate': { lookup: async () => null } })).state, 'unconfirmed'); assert.equal(rows.size, 1);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'billing.payment.allocate': { lookup: async () => ({ state: 'committed', result: { ...result, verificationId: '2' } }) } })); assert.equal(rows.size, 1);
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, { 'billing.payment.allocate': { lookup: async () => ({ state: 'committed', result }) } })).state, 'committed'); assert.equal(rows.size, 0);
});
