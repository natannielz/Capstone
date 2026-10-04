import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createPaymentAllocationGateway, parsePaymentAllocationResult, preparePaymentAllocation, type PaymentAllocationPreview } from '../src/modules/billing/payment-allocation-client';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
import { parsePaymentDraft } from '../src/modules/billing/payment-client';
import type { PaymentVerificationDetail } from '../src/modules/billing/payment-verification-client';

const actor = '9007199254740993', key = 'pa.9007199254740994.9007199254740995.11111111-1111-4111-8111-111111111111';
const preview: PaymentAllocationPreview = { binding: 'a'.repeat(64), procedureReference: 'Synthetic allocation procedure', amount: '1000.25', unapplied: '2999.75',
  view: { verificationId: '9007199254740994', receiptId: '9007199254740995', declarationId: '90', buyingOrganizationId: '200', buyerName: 'Cabang sintetis', transactionId: '0001', bookedDate: '2026-09-27',
    receiptVersion: '0', received: '4000.00', allocated: '0.00', reservedForRefund: '0.00', available: '4000.00', invoices: [{ invoiceId: '91', numberEntity: '100', numberChannel: '103', numberYear: 2026, number: '1',
      version: '0', total: '2000.25', paid: '0.00', credited: '0.00', outstanding: '2000.25', paymentState: 'unpaid', dueDate: '2026-10-27', aging: 'not_due', daysOverdue: 0 }] },
  invoices: [{ invoiceId: '91', version: '1', amount: '1000.25', remaining: '1000.00' }] };
const result = { allocationId: '92', verificationId: preview.view.verificationId, receiptId: preview.view.receiptId, receiptVersion: '1', amount: '1000.25', unapplied: '2999.75',
  recordedAt: '2026-09-28T00:00:00Z', operation: 'billing.payment.allocate' as const, invoices: preview.invoices };
const time = result.recordedAt;
const owner = parsePaymentDraft({ id: '90', revision: '1', createdAt: time, updatedAt: time, state: 'submitted',
  input: { invoiceIds: ['91'], declaredAmount: '4000.00', transferDate: '2026-09-27', payerName: 'Pembayar sintetis', bankReference: null, note: null },
  invoices: [{ invoiceId: '91', number: '100/103/2026/1', total: '2000.25', buyerName: 'Cabang sintetis', sellerName: 'Toko sintetis' }],
  submission: { id: '41', reviewRevision: '1', reservationId: '71', evidence: { id: '61', version: '1', sha256: 'b'.repeat(64) }, submittedAt: time } });
const verified: PaymentVerificationDetail = { result: { verificationId: preview.view.verificationId, declarationId: owner.id, receiptId: preview.view.receiptId, reviewRevision: '0', recordedAt: time, operation: 'billing.payment.verify' },
  declarationRevision: '1', evidence: { id: '61', version: '1', sha256: 'b'.repeat(64) }, verifiedBy: '81', payerVerificationReference: 'Synthetic independent source', reason: 'Reviewed', resolutions: [], reviewSource: 'ocr' };
const extra = { invoiceId: '93', number: '100/103/2026/2', version: '0', total: '3000.25', paid: '0.00', credited: '0.00', outstanding: '3000.25',
  buyerName: 'Cabang sintetis', sellerName: 'Toko sintetis', dueDate: '2026-10-27' };
function allocationGateway(handler: (request: Request) => Promise<Response>) {
  return createPaymentAllocationGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(request.cache, 'no-store');
    return handler(request);
  } }));
}

test('invoice choices use only the verified declaration route and preserve bounded descending cursors including empty pages', async () => {
  const cursor = '9007199254740998';
  let response: unknown = { items: [{ ...extra, invoiceId: '9007199254740997' }], nextBeforeInvoiceId: '9007199254740996' }, calls = 0;
  const gateway = allocationGateway(async request => {
    calls++; assert.equal(new URL(request.url).pathname, `/api/v1/billing/payment-verifications/${verified.result.verificationId}/allocations/invoices/query`);
    assert.deepEqual(await request.json(), { purpose: 'payment-allocation', beforeInvoiceId: cursor }); return Response.json(response);
  });
  assert.deepEqual(await gateway.choices(actor, owner, verified, cursor), response);
  response = { items: [], nextBeforeInvoiceId: '9007199254740996' };
  assert.deepEqual(await gateway.choices(actor, owner, verified, cursor), response);
  for (const changed of [
    { items: [extra, extra], nextBeforeInvoiceId: null },
    { items: [{ ...extra, invoiceId: cursor }], nextBeforeInvoiceId: null },
    { items: [], nextBeforeInvoiceId: cursor },
    { items: [extra], nextBeforeInvoiceId: '94' },
    { items: [{ ...extra, invoiceId: 93 }], nextBeforeInvoiceId: null },
    { items: [{ ...extra, paid: '0.01' }], nextBeforeInvoiceId: null },
    { items: [{ ...extra, invoiceId: '91' }], nextBeforeInvoiceId: null },
    { items: [{ ...extra, outstanding: '0.00', paid: extra.total }], nextBeforeInvoiceId: null },
    { items: [], nextBeforeInvoiceId: null, authorized: true },
  ]) { response = changed; await assert.rejects(() => gateway.choices(actor, owner, verified, cursor)); }
  const before = calls;
  await assert.rejects(() => gateway.choices(actor, owner, { ...verified, declarationRevision: '2' }, cursor));
  await assert.rejects(() => gateway.choices(actor, owner, verified, '9223372036854775808'));
  assert.equal(calls, before);
});

test('additional invoice balances and preview bind learned choices while default reads retain declaration membership', async () => {
  const additionalInvoice = { ...preview.view.invoices[0], invoiceId: extra.invoiceId, number: '2', total: extra.total, outstanding: extra.outstanding };
  const loaded = { ...preview.view, invoices: [...preview.view.invoices, additionalInvoice] };
  const additionalPreview: PaymentAllocationPreview = { ...preview, view: { ...preview.view, invoices: [additionalInvoice] },
    invoices: [{ ...preview.invoices[0], invoiceId: extra.invoiceId, remaining: '2000.00' }] };
  let mode: 'choices' | 'default' | 'read' | 'preview' = 'choices', wrong = false, calls = 0;
  const gateway = allocationGateway(async request => {
    calls++; const body = await request.json();
    if (mode === 'choices') return Response.json({ items: [extra], nextBeforeInvoiceId: null });
    if (mode === 'default') { assert.deepEqual(body, { purpose: 'payment-allocation', invoiceIds: ['91'] }); return Response.json(wrong ? additionalPreview.view : preview.view); }
    if (mode === 'read') { assert.deepEqual(body, { purpose: 'payment-allocation', invoiceIds: ['91', '93'] });
      return Response.json(wrong ? { ...loaded, invoices: [preview.view.invoices[0], { ...additionalInvoice, invoiceId: '94' }] } : loaded); }
    assert.equal(new URL(request.url).pathname, `/api/v1/billing/payment-verifications/${verified.result.verificationId}/allocations/preview`);
    assert.deepEqual(body, { expectedReceiptVersion: '0', lines: [{ invoiceId: '93', expectedVersion: '0', amount: '1000.25' }] });
    return Response.json(wrong ? { ...additionalPreview, view: { ...additionalPreview.view, receiptId: '94' } } : additionalPreview);
  });
  const choices = await gateway.choices(actor, owner, verified);
  mode = 'default'; assert.deepEqual(await gateway.read(actor, owner, verified), preview.view);
  wrong = true; await assert.rejects(() => gateway.read(actor, owner, verified)); wrong = false;
  mode = 'read'; const balances = await gateway.read(actor, owner, verified, choices.items); assert.deepEqual(balances, loaded);
  wrong = true; await assert.rejects(() => gateway.read(actor, owner, verified, choices.items)); wrong = false;
  mode = 'preview'; const input = { expectedReceiptVersion: '0', lines: [{ invoiceId: '93', expectedVersion: '0', amount: '1000.25' }] };
  assert.deepEqual(await gateway.preview(actor, owner, verified, input, balances), additionalPreview);
  wrong = true; await assert.rejects(() => gateway.preview(actor, owner, verified, input, balances)); wrong = false;
  const before = calls;
  await assert.rejects(() => gateway.preview(actor, owner, verified, input));
  await assert.rejects(() => gateway.preview(actor, owner, verified, { ...input, lines: [{ ...input.lines[0], invoiceId: '94' }] }, balances));
  await assert.rejects(() => gateway.preview(actor, owner, verified, { ...input, lines: [{ ...input.lines[0], expectedVersion: '1' }] }, balances));
  await assert.rejects(() => gateway.read(actor, owner, { ...verified, result: { ...verified.result, declarationId: '94' } }, choices.items));
  assert.equal(calls, before);
});
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
test('a fresh client recovers a non-declaration invoice by the original key without learning choices or resending', async () => {
  const committed = { ...result, invoices: [{ ...result.invoices[0], invoiceId: extra.invoiceId, remaining: '2000.00' }] };
  assert.equal(owner.invoices.some(item => item.invoiceId === extra.invoiceId), false);
  const rows = new Map<string, string>(), storage = { get length() { return rows.size; }, key: (index: number) => [...rows.keys()][index] ?? null,
    getItem: (key: string) => rows.get(key) ?? null, setItem: (key: string, value: string) => { rows.set(key, value); }, removeItem: (key: string) => { rows.delete(key); } };
  new OperationJournal(() => storage).remember(actor, result.operation, key);
  const journalAfterReload = new OperationJournal(() => storage), entry = journalAfterReload.list(actor)[0];
  assert.deepEqual(Object.keys(entry).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  let lookups = 0;
  const freshClient = allocationGateway(async request => {
    lookups++; assert.equal(new URL(request.url).pathname, `/api/v1/billing/payment-verifications/${result.verificationId}/allocations/operations/query`);
    assert.equal(request.headers.get('Idempotency-Key'), null); assert.deepEqual(await request.json(), { purpose: 'operation-recovery', key });
    return Response.json({ status: 'committed', result: committed });
  });
  const legacy = { lookup: async (): Promise<never> => { throw new Error('Wrong recovery owner'); }, close: async (): Promise<never> => { throw new Error('Cannot close'); } };
  assert.equal((await reconcileOperationRecovery(entry, journalAfterReload, legacy,
    { 'billing.payment.allocate': { lookup: reference => freshClient.recover(reference.actorId, reference.key) } })).state, 'committed');
  assert.equal(lookups, 1); assert.equal(rows.size, 0);
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
