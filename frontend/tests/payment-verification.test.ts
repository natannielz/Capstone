import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createPaymentVerificationGateway, preparePaymentVerification, parsePaymentVerificationResult, type PaymentVerificationPreview } from '../src/modules/billing/payment-verification-client';
import { correctionFields } from '../src/modules/billing/payment-correction-client';
import { parsePaymentDraft } from '../src/modules/billing/payment-client';
import { OperationJournal, reconcileOperationRecovery, operationRecoveryCapabilities } from '../src/modules/administration/operation-journal';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';

const actor = '9007199254740993', key = 'pv.9007199254740994.9007199254740995.11111111-1111-4111-8111-111111111111';
const preview: PaymentVerificationPreview = { binding: 'a'.repeat(64), declarationId: '9007199254740994', declarationRevision: '1', receiptId: '9007199254740995', reviewRevision: '0', buyingOrganizationId: '200',
  buyerName: 'Cabang sintetis', declaredPayerName: 'Pembayar sintetis', amount: '4000.00', bookedDate: '2026-09-27', transactionId: '0001', accountNumber: '0000123456789', accountHolder: 'Koperasi sintetis', bankCode: 'FIXTUREBANK',
  payerVerificationProcedureReference: 'Synthetic approved procedure', fields: correctionFields.map(field => ({ field, value: null })), discrepancies: [{ field: 'transfer_time', declaration: null, image: null, bank: null }], reviewSource: 'ocr' };
const result = { verificationId: '90', declarationId: preview.declarationId, receiptId: preview.receiptId, reviewRevision: '0', recordedAt: '2026-09-28T00:00:00Z', operation: 'billing.payment.verify' as const };
const prepare = () => preparePaymentVerification(actor, preview, 'Independent payer source', true, true, true, [{ field: 'transfer_time', reason: 'Confirmed booking date; time unknown' }], 'Reviewed');
test('verification requires each explicit attestation and discrepancy resolution and freezes the decision', () => {
  const resolutions = [{ field: 'transfer_time', reason: 'Confirmed booking date; time unknown' }];
  const command = preparePaymentVerification(actor, preview, 'Independent payer source', true, true, true, resolutions, 'Reviewed'); resolutions[0].reason = 'changed';
  assert.equal(command.body.resolutions[0].reason, 'Confirmed booking date; time unknown');
  assert.ok(Object.isFrozen(command) && Object.isFrozen(command.body) && Object.isFrozen(command.body.resolutions) && Object.isFrozen(command.body.resolutions[0]));
  for (const values of [[false, true, true], [true, false, true], [true, true, false]]) assert.throws(() => preparePaymentVerification(actor, preview, 'source', ...values as [boolean, boolean, boolean], resolutions, 'Reviewed'));
  assert.throws(() => preparePaymentVerification(actor, preview, 'source', true, true, true, [], 'Reviewed'));
  assert.throws(() => parsePaymentVerificationResult({ ...result, receiptId: Number(result.receiptId) }, key));
  assert.throws(() => parsePaymentVerificationResult({ ...result, receiptId: '3' }, key));
  assert.throws(() => parsePaymentVerificationResult({ ...result, paid: true }, key));
});
test('verification client sends actor CSRF immutable binding and exact identifiers with no stale or off-owner send', async () => {
  let writes = 0; const command = prepare();
  const gateway = createPaymentVerificationGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (new URL(request.url).pathname === '/api/v1/identity/csrf') return Response.json({ requestToken: 'csrf' });
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(request.cache, 'no-store');
    if (request.url.endsWith('/operations/query')) return Response.json({ status: 'committed', result });
    writes++; assert.equal(request.headers.get('If-Match'), '"0"'); assert.equal(request.headers.get('Idempotency-Key'), key); assert.deepEqual(await request.json(), command.body); return Response.json(result);
  } }));
  await assert.rejects(() => gateway.send(command, key, () => false)); await assert.rejects(() => gateway.send(command, key.replace('9007199254740995', '9'), () => true)); assert.equal(writes, 0);
  assert.deepEqual(await gateway.send(command, key, () => true), result); assert.equal(writes, 1); assert.deepEqual((await gateway.recover(actor, key))?.result, result); assert.equal(writes, 1);
});
test('explicit preview source is sent privately and a different returned source is rejected without changing write bodies', async () => {
  const time = '2026-09-30T03:00:00Z', owner = parsePaymentDraft({ id: preview.declarationId, revision: preview.declarationRevision, state: 'submitted', createdAt: time, updatedAt: time,
    input: { invoiceIds: ['11'], declaredAmount: preview.amount, transferDate: '2026-09-30', payerName: preview.declaredPayerName, bankReference: null, note: null },
    invoices: [{ invoiceId: '11', number: '1/2/2026/3', total: preview.amount, buyerName: 'Cabang', sellerName: 'Toko' }],
    submission: { id: '41', reviewRevision: '1', reservationId: '71', evidence: { id: '61', version: '1', sha256: 'b'.repeat(64) }, submittedAt: time } });
  let source: 'ocr' | 'manual' = 'ocr', calls = 0;
  const gateway = createPaymentVerificationGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
    calls++; assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(request.cache, 'no-store');
    assert.deepEqual(await request.json(), { receiptId: preview.receiptId, reviewSource: 'ocr' }); return Response.json({ ...preview, reviewSource: source });
  } }));
  assert.deepEqual(await gateway.preview(actor, owner, preview.receiptId, 'ocr'), preview); source = 'manual';
  await assert.rejects(() => gateway.preview(actor, owner, preview.receiptId, 'ocr'));
  assert.equal(calls, 2); assert.equal('reviewSource' in prepare().body, false);
});
test('verification recovery preserves metadata only, keeps unknown outcomes, and accepts an original decision with no corrections', async () => {
  const rows = new Map<string, string>(), journal = new OperationJournal(() => ({ get length() { return rows.size; }, key: index => [...rows.keys()][index] ?? null,
    getItem: key => rows.get(key) ?? null, setItem: (key, value) => { rows.set(key, value); }, removeItem: key => { rows.delete(key); } }));
  const entry = journal.remember(actor, result.operation, key), legacy = { lookup: async () => { throw new Error('Wrong owner'); }, close: async () => { throw new Error('Cannot close'); } };
  assert.deepEqual(Object.keys(entry).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.equal(recoveryPage(entry.operation).href, '/billing/payment-reviews'); assert.equal(operationRecoveryCapabilities(entry.operation).canClose, false);
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, { 'billing.payment.verify': { lookup: async () => null } })).state, 'unconfirmed'); assert.equal(rows.size, 1);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'billing.payment.verify': { lookup: async () => ({ state: 'committed', result: { ...result, declarationId: '2' } }) } })); assert.equal(rows.size, 1);
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, { 'billing.payment.verify': { lookup: async () => ({ state: 'committed', result }) } })).state, 'committed'); assert.equal(rows.size, 0);
});
