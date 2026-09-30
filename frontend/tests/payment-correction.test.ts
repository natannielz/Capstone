import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { correctionFields, correctionBinding, createPaymentCorrectionGateway, parsePaymentCorrection, prepareCorrection, paymentCorrectionOwner } from '../src/modules/billing/payment-correction-client';
import { parsePaymentDraft, parsePaymentOcr } from '../src/modules/billing/payment-client';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';

const actor = '9007199254740993', time = '2026-09-30T03:00:00Z', key = 'c.51.11111111-1111-4111-8111-111111111111';
const evidence = { id: '61', version: '1', sha256: 'a'.repeat(64) };
const owner = parsePaymentDraft({ id: '51', revision: '2', state: 'submitted', createdAt: time, updatedAt: time,
  input: { invoiceIds: ['11'], declaredAmount: '100.00', transferDate: '2026-09-30', payerName: 'Fixture', bankReference: null, note: null },
  invoices: [{ invoiceId: '11', number: '1/2/2026/3', total: '500.00', buyerName: 'Cabang', sellerName: 'Toko' }],
  submission: { id: '41', reviewRevision: '1', reservationId: '71', evidence, submittedAt: time } });
const ocr = parsePaymentOcr({ declarationId: owner.id, submissionId: '41', evidence, state: 'succeeded', result: { id: '81', extractedAt: time,
  provenance: { engineVersion: 'fixture', modelVersion: 'fixture', modelSha256: 'a'.repeat(64), runtimeSha256: 'b'.repeat(64), preprocessingVersion: 'fixture' },
  fields: correctionFields.map(field => ({ field, value: null, confidence: null, boundingBox: null, flags: ['unknown'] })), qualityFlags: ['no_text'] } }, owner);
const original = correctionFields.map(field => ({ field, value: null }));
const initial = { declarationId: owner.id, binding: correctionBinding(owner, ocr), reviewRevision: '0', originalFields: original, fields: original, recent: [] };
const changed = original.map(field => field.field === 'amount' ? { ...field, value: '100.00' } : field);
const row = { id: '91', revision: '1', actorId: actor, recordedAt: time, reason: 'Cocokkan bukti', beforeFields: original, fields: changed };
const saved = { ...initial, reviewRevision: '1', fields: changed, recent: [row] };
const result = { declarationId: owner.id, submissionId: '41', reviewRevision: '1', correctionId: '91', recordedAt: time };

test('correction comparison binds exact source and complete latest chain without accepting invented money authority', () => {
  assert.equal(parsePaymentCorrection(initial, owner, ocr).reviewRevision, '0'); assert.equal(parsePaymentCorrection(saved, owner, ocr).recent[0].actorId, actor);
  for (const invalid of [{ ...saved, verified: true }, { ...saved, declarationId: '52' }, { ...saved, reviewRevision: '2' },
    { ...saved, binding: { ...saved.binding, extractionId: '82' } }, { ...saved, originalFields: changed }, { ...saved, recent: [{ ...row, beforeFields: changed }] },
    { ...saved, fields: changed.slice(1) }, { ...saved, recent: [{ ...row, actorId: Number(actor) }] }]) assert.throws(() => parsePaymentCorrection(invalid, owner, ocr));
  assert.throws(() => prepareCorrection(actor, initial, original, 'unchanged'));
  for (const value of ['-1.00', '1000000000000000.00', '2.001', 'NaN']) assert.throws(() => prepareCorrection(actor, initial, changed.map(field => field.field === 'amount' ? { ...field, value } : field), 'invalid'));
});
test('correction gateway sends current actor CSRF exact revision and binding and recovers only matching owner', async () => {
  let calls = 0, wrong = false;
  const gateway = createPaymentCorrectionGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); const path = new URL(request.url).pathname;
    if (path === '/api/v1/identity/csrf') return Response.json({ requestToken: 'fixture-csrf' });
    calls++; assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'fixture-csrf'); assert.equal(request.cache, 'no-store');
    if (path.endsWith('/operations/query')) return Response.json({ status: 'committed', result: { ...result, declarationId: wrong ? '52' : owner.id } });
    assert.equal(request.headers.get('If-Match'), '"0"'); assert.equal(request.headers.get('Idempotency-Key'), key);
    const body = await request.json(); assert.deepEqual(body, { binding: initial.binding, fields: changed, reason: 'Cocokkan bukti' }); return Response.json(result);
  } }));
  const command = prepareCorrection(actor, initial, changed, 'Cocokkan bukti');
  await assert.rejects(() => gateway.save(command, key, () => false)); assert.equal(calls, 0);
  assert.deepEqual(await gateway.save(command, key, () => true), result); assert.equal((await gateway.recover(actor, key))?.result.declarationId, owner.id);
  wrong = true; await assert.rejects(() => gateway.recover(actor, key)); assert.equal(paymentCorrectionOwner(key), owner.id);
});
test('correction recovery retains missing and wrong receipts and stores references without observed fields', async () => {
  const values = new Map<string, string>(); const journal = new OperationJournal(() => ({ get length() { return values.size; }, key: index => [...values.keys()][index] ?? null,
    getItem: key => values.get(key) ?? null, setItem: (key, value) => { values.set(key, value); }, removeItem: key => { values.delete(key); } }));
  const entry = journal.remember(actor, 'billing.payment.review.correct', key);
  assert.deepEqual(Object.keys(entry).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.equal(recoveryPage(entry.operation).href, '/billing/payment-reviews');
  assert.equal(operationRecoveryCapabilities(entry.operation).canClose, false);
  const legacy = { lookup: async () => { throw new Error('Wrong owner'); }, close: async () => { throw new Error('Must not close'); } };
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, { 'billing.payment.review.correct': { lookup: async () => null } })).state, 'unconfirmed'); assert.equal(values.size, 1);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'billing.payment.review.correct': { lookup: async () => ({ state: 'committed', result: { ...result, declarationId: '52' } }) } })); assert.equal(values.size, 1);
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, { 'billing.payment.review.correct': { lookup: async () => ({ state: 'committed', result }) } })).state, 'committed'); assert.equal(values.size, 0);
});
