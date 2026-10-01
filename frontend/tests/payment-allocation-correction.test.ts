import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure } from '../src/modules/administration/workflow';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';
import { operationLabel, recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
import { parsePaymentDraft } from '../src/modules/billing/payment-client';
import type { PaymentVerificationDetail } from '../src/modules/billing/payment-verification-client';
import { createPaymentAllocationCorrectionGateway, paymentAllocationCorrectionKey, parsePaymentAllocationCorrectionResult,
  preparePaymentAllocationCorrection, type PaymentAllocationCorrectionPreview, type PaymentAllocationCorrectionView } from '../src/modules/billing/payment-allocation-correction-client';

const actor = '9007199254740993', time = '2026-10-02T00:00:00Z';
const key = 'pc.9007199254740994.9007199254740995.9007199254740996.11111111-1111-4111-8111-111111111111';
const preview: PaymentAllocationCorrectionPreview = { originalAllocationId: '9007199254740996', binding: 'a'.repeat(64), procedureReference: 'Synthetic approved correction procedure', amount: '400.25', unapplied: '2150.25',
  view: { verificationId: '9007199254740994', receiptId: '9007199254740995', declarationId: '90', buyingOrganizationId: '200', buyerName: 'Cabang sintetis', transactionId: '0001', bookedDate: '2026-09-27',
    receiptVersion: '9', received: '4000.00', allocated: '1750.00', reservedForRefund: '500.00', available: '1750.00', invoices: [{ invoiceId: '91', numberEntity: '100', numberChannel: '103', numberYear: 2026, number: '1',
      version: '7', total: '2000.25', paid: '1250.25', credited: '250.00', outstanding: '500.00', paymentState: 'partial', dueDate: '2026-10-27', aging: 'not_due', daysOverdue: 0 }] },
  capacities: [{ invoiceId: '91', originallyAllocated: '1000.25', previouslyReversed: '200.00' }], invoices: [{ invoiceId: '91', version: '8', amount: '400.25', remaining: '900.25' }] };
const correctionView: PaymentAllocationCorrectionView = { originalAllocationId: preview.originalAllocationId, originalRecordedAt: time, view: preview.view, capacities: preview.capacities };
const result = { correctionId: '9007199254740997', originalAllocationId: preview.originalAllocationId, verificationId: preview.view.verificationId, receiptId: preview.view.receiptId,
  receiptVersion: '10', amount: preview.amount, unapplied: preview.unapplied, recordedAt: time, operation: 'billing.payment.allocation.correct' as const, invoices: preview.invoices };
const owner = parsePaymentDraft({ id: '90', revision: '1', createdAt: time, updatedAt: time, state: 'submitted',
  input: { invoiceIds: ['91'], declaredAmount: '4000.00', transferDate: '2026-09-27', payerName: 'Pembayar sintetis', bankReference: null, note: null },
  invoices: [{ invoiceId: '91', number: '100/103/2026/1', total: '2000.25', buyerName: 'Cabang sintetis', sellerName: 'Toko' }],
  submission: { id: '41', reviewRevision: '1', reservationId: '71', evidence: { id: '61', version: '1', sha256: 'b'.repeat(64) }, submittedAt: time } });
const verified: PaymentVerificationDetail = { result: { verificationId: preview.view.verificationId, declarationId: owner.id, receiptId: preview.view.receiptId, reviewRevision: '0', recordedAt: time, operation: 'billing.payment.verify' },
  declarationRevision: '1', evidence: { id: '61', version: '1', sha256: 'b'.repeat(64) }, verifiedBy: '81', payerVerificationReference: 'Synthetic independent source', reason: 'Reviewed', resolutions: [], reviewSource: 'ocr' };
function gateway(handler: (request: Request) => Promise<Response>) {
  return createPaymentAllocationCorrectionGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(request.cache, 'no-store'); return handler(request);
  } }));
}

test('partial allocation correction freezes original capacities and released balances with exact large identifiers', () => {
  const copy = structuredClone(preview), command = preparePaymentAllocationCorrection(actor, copy, 'Independently checked correction');
  copy.invoices[0].amount = '999.00'; copy.invoices[0].remaining = '1499.00'; copy.capacities[0].originallyAllocated = '1999.00';
  assert.equal(command.body.input.lines[0].amount, '400.25'); assert.equal(command.expectedResult.invoices[0].remaining, '900.25'); assert.equal(command.expectedResult.unapplied, '2150.25');
  for (const value of [command, command.body, command.body.input, command.body.input.lines, command.body.input.lines[0], command.expectedResult, command.expectedResult.invoices, command.expectedResult.invoices[0]]) assert.ok(Object.isFrozen(value));
  assert.deepEqual(paymentAllocationCorrectionKey(key), { verificationId: preview.view.verificationId, receiptId: preview.view.receiptId, originalAllocationId: preview.originalAllocationId });
  assert.deepEqual(parsePaymentAllocationCorrectionResult(result, key), result);
});

test('correction rejects over-reversal, wrong direction, current paid exhaustion and malformed money or source identifiers', () => {
  const excess = { ...preview, amount: '801.00', unapplied: '2551.00', invoices: [{ ...preview.invoices[0], amount: '801.00', remaining: '1301.00' }] };
  for (const changed of [excess, { ...preview, unapplied: '1349.75' }, { ...preview, invoices: [{ ...preview.invoices[0], remaining: '99.75' }] },
    { ...preview, capacities: [{ ...preview.capacities[0], previouslyReversed: '1000.26' }] }, { ...preview, capacities: [...preview.capacities, preview.capacities[0]] },
    { ...preview, view: { ...preview.view, invoices: [{ ...preview.view.invoices[0], paid: '100.00', credited: '1400.25' }] } },
    { ...preview, view: { ...preview.view, allocated: '399.00', available: '3101.00' }, unapplied: '3501.25' },
    { ...preview, capacities: [{ ...preview.capacities[0], invoiceId: '92' }] }, { ...preview, amount: '4e2' }, { ...preview, originalAllocationId: '9223372036854775808' },
  ]) assert.throws(() => preparePaymentAllocationCorrection(actor, changed, 'Checked'));
  for (const changed of [{ ...result, originalAllocationId: '92' }, { ...result, receiptId: Number(result.receiptId) }, { ...result, amount: '400.26' },
    { ...result, operation: 'billing.payment.allocate' }, { ...result, correctionId: result.originalAllocationId }, { ...result, invoicePaid: true }])
    assert.throws(() => parsePaymentAllocationCorrectionResult(changed, key));
  for (const changed of [key.replace('pc.', 'pa.'), key + '\n', key.replace(preview.originalAllocationId, '9223372036854775808')]) assert.throws(() => paymentAllocationCorrectionKey(changed));
});

test('candidate, detail and preview reads stay on the owning verified declaration and original allocation', async () => {
  let mode = 'candidates', wrong = false;
  const client = gateway(async request => {
    const path = new URL(request.url).pathname, body = await request.json();
    if (mode === 'candidates') { assert.equal(path, `/api/v1/billing/payment-verifications/${preview.view.verificationId}/allocations/corrections/query`); assert.deepEqual(body, { purpose: 'payment-allocation-correction', before: null });
      return Response.json({ items: [wrong ? { ...correctionView, view: { ...preview.view, declarationId: '92' } } : correctionView], hasMore: false }); }
    if (mode === 'read') { assert.equal(path, `/api/v1/billing/payment-allocations/${preview.originalAllocationId}/corrections/query`); assert.deepEqual(body, { purpose: 'payment-allocation-correction' });
      return Response.json(wrong ? { ...correctionView, originalAllocationId: '92' } : correctionView); }
    assert.equal(path, `/api/v1/billing/payment-allocations/${preview.originalAllocationId}/corrections/preview`);
    assert.deepEqual(body, { expectedReceiptVersion: '9', lines: [{ invoiceId: '91', expectedVersion: '7', amount: '400.25' }] }); return Response.json(preview);
  });
  assert.deepEqual(await client.candidates(actor, owner, verified), { items: [correctionView], hasMore: false }); wrong = true; await assert.rejects(() => client.candidates(actor, owner, verified));
  wrong = false; mode = 'read'; assert.deepEqual(await client.read(actor, owner, verified, preview.originalAllocationId), correctionView); wrong = true; await assert.rejects(() => client.read(actor, owner, verified, preview.originalAllocationId));
  wrong = false; mode = 'preview'; const values = preparePaymentAllocationCorrection(actor, preview, 'Checked').body.input;
  assert.deepEqual(await client.preview(actor, owner, verified, preview.originalAllocationId, values), preview);
  await assert.rejects(() => client.preview(actor, owner, { ...verified, declarationRevision: '2' }, preview.originalAllocationId, values));
});

test('correction history pagination keeps exact descending original identifiers and refuses duplicate or crossed cursors', async () => {
  const cursor = '9007199254740997'; let items: PaymentAllocationCorrectionView[] = [correctionView];
  const client = gateway(async request => {
    assert.deepEqual(await request.json(), { purpose: 'payment-allocation-correction', before: cursor }); return Response.json({ items, hasMore: false });
  });
  assert.deepEqual((await client.candidates(actor, owner, verified, cursor)).items, items);
  for (const changed of [[correctionView, correctionView], [{ ...correctionView, originalAllocationId: cursor }],
    [{ ...correctionView, originalAllocationId: '90' }, correctionView]]) {
    items = changed; await assert.rejects(() => client.candidates(actor, owner, verified, cursor));
  }
  await assert.rejects(() => client.candidates(actor, owner, verified, '9223372036854775808'));
});

test('correction sends one exact frozen command and refuses stale/off-original writes and altered reviewed outcomes', async () => {
  let writes = 0, wrong = false, current = true; const command = preparePaymentAllocationCorrection(actor, preview, 'Checked');
  const client = gateway(async request => {
    writes++; assert.equal(new URL(request.url).pathname, `/api/v1/billing/payment-allocations/${preview.originalAllocationId}/corrections`);
    assert.equal(request.headers.get('If-Match'), '"9"'); assert.equal(request.headers.get('Idempotency-Key'), key); assert.deepEqual(await request.json(), command.body);
    return Response.json(wrong ? { ...result, invoices: [{ ...result.invoices[0], remaining: '900.26' }] } : result);
  });
  await assert.rejects(() => client.correct(command, key, () => false), RequestNotSentError);
  await assert.rejects(() => client.correct(command, key.replace(preview.originalAllocationId, '92'), () => true), RequestNotSentError); assert.equal(writes, 0);
  assert.deepEqual(await client.correct(command, key, () => true), result); assert.equal(writes, 1);
  wrong = true; await assert.rejects(() => client.correct(command, key, () => true)); wrong = false;
  await assert.rejects(() => client.correct(command, key, () => { const before = current; current = false; return before; }), (error: unknown) => error instanceof AccessFailure && error.code === 'commit_outcome_unknown');
  assert.equal(writes, 3);
});

test('original-key recovery never resends and preserves unknown, off-owner and malformed committed receipts', async () => {
  let response: unknown = { status: 'not_confirmed', result: null }, lookups = 0;
  const client = gateway(async request => {
    lookups++; assert.equal(new URL(request.url).pathname, `/api/v1/billing/payment-allocations/${preview.originalAllocationId}/corrections/operations/query`);
    assert.equal(request.headers.get('Idempotency-Key'), null); assert.deepEqual(await request.json(), { purpose: 'operation-recovery', key }); return Response.json(response);
  });
  assert.equal(await client.recover(actor, key), null); response = { status: 'committed', result }; assert.deepEqual((await client.recover(actor, key))?.result, result);
  for (const changed of [{ status: 'committed', result: null }, { status: 'not_confirmed', result }, { status: 'committed', result: { ...result, originalAllocationId: '92' } },
    { status: 'committed', result: { ...result, operation: 'billing.payment.allocate' } }, { status: 'committed', result, closed: true }]) {
    response = changed; await assert.rejects(() => client.recover(actor, key));
  }
  assert.equal(lookups, 7);
});

test('correction recovery keeps metadata and uncertain references and clears only its exact subtype after a current validated receipt', async () => {
  const rows = new Map<string, string>(), journal = new OperationJournal(() => ({ get length() { return rows.size; }, key: index => [...rows.keys()][index] ?? null,
    getItem: key => rows.get(key) ?? null, setItem: (key, value) => { rows.set(key, value); }, removeItem: key => { rows.delete(key); } }));
  const entry = journal.remember(actor, result.operation, key);
  const ordinary = journal.remember(actor, 'billing.payment.allocate', key.replace('pc.', 'pa.').replace('.' + preview.originalAllocationId, ''));
  const otherActor = '82'; journal.remember(otherActor, result.operation, key);
  const snapshot = [...rows.entries()]; let legacyCalls = 0, allocationCalls = 0;
  const legacy = { lookup: async () => { legacyCalls++; throw new Error('Wrong legacy recovery'); }, close: async () => { legacyCalls++; throw new Error('Wrong legacy close'); } };
  const ordinaryAdapter = { 'billing.payment.allocate': { lookup: async (): Promise<null> => { allocationCalls++; throw new Error('Wrong allocation recovery'); } } };
  const validAdapter = { 'billing.payment.allocation.correct': { lookup: async () => ({ state: 'committed' as const, result }) } };
  for (const serialized of rows.values()) assert.deepEqual(Object.keys(JSON.parse(serialized)).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.deepEqual(recoveryPage(entry.operation), { href: '/billing/payment-reviews', label: 'halaman koreksi alokasi pembayaran' });
  assert.equal(operationLabel(entry.operation), 'Koreksi alokasi pembayaran');
  assert.deepEqual(operationRecoveryCapabilities(entry.operation, ordinaryAdapter), { canCheck: false, canClose: false, unavailable: 'adapter' });
  assert.deepEqual(operationRecoveryCapabilities(entry.operation, validAdapter), { canCheck: true, canClose: false, unavailable: null });
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, ordinaryAdapter), { status: 503, code: 'recovery_adapter_unavailable' }); assert.deepEqual([...rows.entries()], snapshot);
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, { 'billing.payment.allocation.correct': { lookup: async () => null } })).state, 'unconfirmed'); assert.deepEqual([...rows.entries()], snapshot);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'billing.payment.allocation.correct': { lookup: async () => { throw new Error('Synthetic lost reply'); } } })); assert.deepEqual([...rows.entries()], snapshot);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, validAdapter, 'Close unknown correction'), { status: 400, code: 'recovery_close_unsupported' }); assert.deepEqual([...rows.entries()], snapshot);
  let current = true;
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'billing.payment.allocation.correct': { lookup: async () => { current = false; return { state: 'committed', result }; } } },
    undefined, () => current), { status: 409, code: 'recovery_request_stale' }); assert.deepEqual([...rows.entries()], snapshot);
  for (const changed of [{ ...result, originalAllocationId: '92' }, { ...result, operation: 'billing.payment.allocate' }]) {
    await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'billing.payment.allocation.correct': { lookup: async () => ({ state: 'committed', result: changed as never }) } })); assert.deepEqual([...rows.entries()], snapshot);
  }
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { ...ordinaryAdapter, ...validAdapter }), { state: 'committed', result: { id: result.correctionId, version: result.receiptVersion } });
  assert.deepEqual(journal.list(actor), [ordinary]); assert.equal(journal.list(otherActor).length, 1); assert.equal(rows.size, 2);
  assert.equal(legacyCalls, 0); assert.equal(allocationCalls, 0);
});
