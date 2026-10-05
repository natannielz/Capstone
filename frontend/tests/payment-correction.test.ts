import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { correctionFields, correctionBinding, createPaymentCorrectionGateway, parsePaymentCorrection, prepareCorrection, paymentCorrectionOwner,
  rememberPaymentCorrectionObservation, type PaymentCorrectionSupplementaryObservation } from '../src/modules/billing/payment-correction-client';
import {paymentSupplementOwner, supplementPdfOwner, type PaymentSupplement} from '../src/modules/billing/payment-pdf-supplement-client';
import type {PaymentSupplementObservation} from '../src/modules/billing/PaymentSupplementReviewPanel';
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

const pdfEvidence = {id: '9007199254740995', version: '1' as const, sha256: 'b'.repeat(64)};
const observation: PaymentCorrectionSupplementaryObservation = {field: 'amount', supplementId: '9007199254740997', supplementRevision: '2', originalReviewed: true,
  candidate: {resultId: '9007199254740999', resultHash: 'c'.repeat(64), evidence: pdfEvidence, pageIndex: 1, field: 'amount'}};
test('optional PDF provenance retains exact Int64 strings and old absent-field history while rejecting stale or ambiguous references', () => {
  const linked = {...saved, recent: [{...row, supplementaryObservations: [observation]}]};
  assert.deepEqual(parsePaymentCorrection(linked, owner, ocr).recent[0].supplementaryObservations, [observation]);
  assert.equal(Object.hasOwn(parsePaymentCorrection(saved, owner, ocr).recent[0], 'supplementaryObservations'), false);
  for (const refs of [null, [], [observation, observation],
    [{...observation, originalReviewed: false}], [{...observation, supplementRevision: '1'}], [{...observation, field: 'currency'}],
    [{...observation, supplementId: Number(observation.supplementId)}], [{...observation, supplementId: '9223372036854775806'}],
    [{...observation, candidate: {...observation.candidate, evidence}}],
    [{...observation, candidate: {...observation.candidate, pageIndex: 10}}], [{...observation, candidate: {...observation.candidate, resultHash: 'c'.repeat(64) + '\n'}}],
    [{...observation, candidate: {...observation.candidate, value: '2345678.90'}}],
    [{...observation, field: 'currency', candidate: {...observation.candidate, field: 'currency'}}, observation],
    [observation, {...observation, field: 'currency', candidate: {...observation.candidate, field: 'currency', resultHash: 'd'.repeat(64)}}],
    [observation, {...observation, supplementId: '109', field: 'currency', candidate: {...observation.candidate, field: 'currency'}}]]) {
    assert.throws(() => parsePaymentCorrection({...saved, recent: [{...row, supplementaryObservations: refs}]}, owner, ocr));
  }
  assert.deepEqual(prepareCorrection(actor, initial, changed, 'Foto telah ditinjau', [observation]).body.supplementaryObservations, [observation]);
  assert.equal(Object.hasOwn(prepareCorrection(actor, initial, changed, 'Foto telah ditinjau').body, 'supplementaryObservations'), false);
  assert.throws(() => prepareCorrection(actor, initial, original, 'Rujukan saja', [observation]), {code: 'correction_unchanged'});
  assert.throws(() => prepareCorrection(actor, initial, changed, 'Foto telah ditinjau', []), {code: 'invalid_payment_correction'});
});
test('separate image and PDF result sequences can share a numeric ID while exact evidence bindings remain distinct', () => {
  const sameResultNumber = {...observation, candidate: {...observation.candidate, resultId: initial.binding.extractionId}};
  const command = prepareCorrection(actor, initial, changed, 'Foto dan PDF yang berbeda diperiksa', [sameResultNumber]);
  assert.deepEqual(command.body.binding, initial.binding);
  assert.deepEqual(command.body.supplementaryObservations, [sameResultNumber]);
  assert.deepEqual(parsePaymentCorrection({...saved, recent: [{...row, supplementaryObservations: [sameResultNumber]}]}, owner, ocr)
    .recent[0].supplementaryObservations, [sameResultNumber]);
  for (const conflictingEvidence of [{...pdfEvidence, id: evidence.id}, {...pdfEvidence, sha256: evidence.sha256}]) {
    const wrongOriginal = {...sameResultNumber, candidate: {...sameResultNumber.candidate, evidence: conflictingEvidence}};
    assert.throws(() => prepareCorrection(actor, initial, changed, 'Bukti utama tidak menjadi lampiran', [wrongOriginal]), {code: 'invalid_payment_correction'});
    assert.throws(() => parsePaymentCorrection({...saved, recent: [{...row, supplementaryObservations: [wrongOriginal]}]}, owner, ocr));
  }
});
test('remembered PDF comparisons are bound to exact actor primary tuple and supplementary source without copying a value', () => {
  const primary = paymentSupplementOwner(actor, owner);
  const savedSupplement: PaymentSupplement = {declarationId: primary.declarationId, declarationRevision: primary.declarationRevision, submissionId: primary.submissionId,
    primaryEvidence: primary.primaryEvidence, supplementId: observation.supplementId, supplementRevision: '2',
    state: 'attached', reservationId: '101', pdfEvidence, attachedAt: time, reason: 'Dokumen tambahan untuk perbandingan'};
  const selection: PaymentSupplementObservation = {primary, supplement: savedSupplement,
    observation: {source: supplementPdfOwner(primary, savedSupplement), reference: observation.candidate,
      candidate: {field: 'amount', value: '2345678.90', confidence: '0.97', boundingBox: ['0.1', '0.2', '0.3', '0.1'],
        pdfCanvasCorners: [{x: 1, y: 2}, {x: 4, y: 2}, {x: 4, y: 3}, {x: 1, y: 3}], flags: []}}};
  const remembered = rememberPaymentCorrectionObservation(actor, owner, selection);
  assert.deepEqual(remembered.reference, observation); assert.equal(remembered.value, '2345678.90');
  const command = prepareCorrection(actor, initial, changed, 'Nominal foto diperiksa', [remembered.reference]);
  assert.equal(command.body.fields[0].value, '100.00'); assert.equal(command.body.binding.extractionId, '81');
  assert.deepEqual(command.body.binding.evidence, evidence); assert.equal('value' in command.body.supplementaryObservations![0].candidate, false);
  for (const foreign of [
    {...selection, primary: {...primary, actorId: '92'}}, {...selection, primary: {...primary, submissionId: '42'}},
    {...selection, supplement: {...savedSupplement, declarationId: '52'}},
    {...selection, observation: {...selection.observation, source: {...selection.observation.source, sourceKind: 'Payment' as const}}},
    {...selection, observation: {...selection.observation, source: {...selection.observation.source, sourceId: '99'}}},
    {...selection, observation: {...selection.observation, reference: {...observation.candidate, evidence}}}
  ]) assert.throws(() => rememberPaymentCorrectionObservation(actor, owner, foreign));
});
test('lost PDF-linked correction reply uses original actor and key lookup without resubmission or private journal payload', async () => {
  const storage = new Map<string, string>(), journal = new OperationJournal(() => ({get length() {return storage.size;}, key: index => [...storage.keys()][index] ?? null,
    getItem: key => storage.get(key) ?? null, setItem: (key, value) => {storage.set(key, value);}, removeItem: key => {storage.delete(key);}}));
  let writes = 0, queries = 0; const command = prepareCorrection(actor, initial, changed, 'Foto dan lampiran diperiksa', [observation]);
  const gateway = createPaymentCorrectionGateway(createUnitTokoClient({baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); const path = new URL(request.url).pathname;
    if (path === '/api/v1/identity/csrf') return Response.json({requestToken: 'fixture-csrf'});
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'fixture-csrf');
    if (path.endsWith('/operations/query')) {queries++; assert.deepEqual(await request.json(), {purpose: 'operation-recovery', key}); return Response.json({status: 'committed', result});}
    writes++; assert.equal(request.headers.get('If-Match'), '"0"'); assert.equal(request.headers.get('Idempotency-Key'), key);
    assert.deepEqual(await request.json(), command.body); throw new TypeError('Fixture lost committed reply');
  }}));
  const entry = journal.remember(actor, 'billing.payment.review.correct', key);
  await assert.rejects(() => gateway.save(command, key, () => true));
  assert.deepEqual(Object.keys(entry).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.equal([...storage.values()].some(value => value.includes(pdfEvidence.sha256) || value.includes(command.body.reason)), false);
  const legacy = {lookup: async () => assert.fail('Legacy lookup forbidden'), close: async () => assert.fail('Close forbidden')};
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, {'billing.payment.review.correct': {lookup: entry => gateway.recover(entry.actorId, entry.key)}})).state, 'committed');
  assert.equal(writes, 1); assert.equal(queries, 1); assert.equal(storage.size, 0);
});
