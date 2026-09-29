import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createPaymentGateway, preparePayment, parsePaymentResult, paymentReservationOwner } from '../src/modules/billing/payment-client';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';

const actor = '9007199254740993', declarationId = '9007199254740994';
const input = { invoiceIds: ['9007199254740995'], declaredAmount: '4000.01', transferDate: '2026-09-29', payerName: 'Pembayar', bankReference: null, note: null };
const result = { declarationId, versionId: '9007199254740996', revision: '1', recordedAt: '2026-09-29T03:00:00Z' };
test('payment freezes invoice intent and keeps IDs and money exact', () => {
  const command = preparePayment(actor, null, '0', input, 'Pembayaran cabang'); assert.ok(Object.isFrozen(command.body.input.invoiceIds));
  assert.equal(command.body.input.declaredAmount, '4000.01'); assert.deepEqual(parsePaymentResult(result), result);
  for (const bad of [{ ...input, invoiceIds: [input.invoiceIds[0], input.invoiceIds[0]] }, { ...input, declaredAmount: '4e3' }, { ...input, declaredAmount: '0.00' }, { ...input, transferDate: '2026-02-30' }]) assert.throws(() => preparePayment(actor, null, '0', bad, 'Catatan'));
  assert.throws(() => parsePaymentResult({ ...result, revision: 1 })); assert.throws(() => parsePaymentResult({ ...result, verified: true }));
});
test('payment writes once with exact actor/version and does not retry a lost response', async () => {
  let calls = 0; const command = preparePayment(actor, null, '0', input, 'Pembayaran cabang');
  const gateway = createPaymentGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' }); calls++;
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(request.headers.get('If-Match'), '"0"');
    assert.equal(request.headers.get('Idempotency-Key'), 'same-payment'); assert.deepEqual(await request.json(), command.body); throw new Error('lost response');
  } }));
  await assert.rejects(() => gateway.save(command, 'same-payment', () => false)); assert.equal(calls, 0);
  await assert.rejects(() => gateway.save(command, 'same-payment', () => true)); assert.equal(calls, 1);
});
test('payment image sends original binary bytes once with CSRF; scope is bound to owning declaration', async () => {
  const bytes = new Uint8Array([0, 255, 1, 128]), file = new File([bytes], 'original.png', { type: 'image/png' }); let calls = 0;
  const gateway = createPaymentGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' }); calls++;
    assert.equal(request.headers.get('Content-Type'), 'application/octet-stream'); assert.equal(request.headers.get('X-Expected-Actor-Id'), actor);
    assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(request.headers.get('If-Match'), '"1"'); assert.deepEqual(new Uint8Array(await request.arrayBuffer()), bytes);
    return Response.json({ state: 'quarantined', reference: { id: '55', version: '1', sha256: 'a'.repeat(64) } });
  } }));
  await assert.rejects(() => gateway.upload(actor, declarationId, '44', '1', new File(['bad'], 'file.pdf', { type: 'application/pdf' }), () => true)); assert.equal(calls, 0);
  assert.equal((await gateway.upload(actor, declarationId, '44', '1', file, () => true)).state, 'quarantined'); assert.equal(calls, 1);
  const key = 'p.' + declarationId + '.12345678-1234-1234-1234-123456789abc'; assert.equal(paymentReservationOwner(key), declarationId);
  assert.throws(() => paymentReservationOwner('p.0.12345678-1234-1234-1234-123456789abc'));
});
test('payment recovery is read-only and retains an unconfirmed operation without access-owner fallback', async () => {
  const values = new Map<string, string>(), storage = { get length() { return values.size; }, key: (index: number) => [...values.keys()][index] ?? null,
    getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); }, removeItem: (key: string) => { values.delete(key); } };
  const journal = new OperationJournal(() => storage), entry = journal.remember(actor, 'billing.payment.draft.save', 'same-payment');
  const legacy = { lookup: async () => { throw new Error('wrong owner'); }, close: async () => { throw new Error('must not close'); } };
  const absent = { 'billing.payment': { lookup: async () => null } }; assert.equal(operationRecoveryCapabilities(entry.operation, absent).canClose, false);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, absent, 'close'));
  await reconcileOperationRecovery(entry, journal, legacy, absent); assert.equal(journal.list(actor).length, 1);
  await reconcileOperationRecovery(entry, journal, legacy, { 'billing.payment': { lookup: async () => ({ state: 'committed' as const, result }) } }); assert.equal(journal.list(actor).length, 0);
  const upload = journal.remember(actor, 'evidence.payment-image.reserve', 'p.' + declarationId + '.12345678-1234-1234-1234-123456789abc');
  const adapter = { 'evidence.payment-image': { lookup: async () => ({ state: 'committed' as const, result: { reservationId: '55', revision: '1' as const, expiresAt: result.recordedAt } }) } };
  assert.equal(operationRecoveryCapabilities(upload.operation, adapter).canClose, false);
  await reconcileOperationRecovery(upload, journal, legacy, adapter); assert.equal(journal.list(actor).length, 0);
});
