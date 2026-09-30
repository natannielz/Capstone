import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createPaymentGateway, preparePayment, preparePaymentSubmission, parsePaymentResult, parsePaymentSubmissionResult, paymentSubmissionOwner, paymentReservationOwner, parsePaymentEvidenceStatus, paymentEvidenceLabel, parsePaymentOcr, type PaymentDraft } from '../src/modules/billing/payment-client';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';

const actor = '9007199254740993', declarationId = '9007199254740994';
const input = { invoiceIds: ['9007199254740995'], declaredAmount: '4000.01', transferDate: '2026-09-29', payerName: 'Pembayar', bankReference: null, note: null };
const result = { declarationId, versionId: '9007199254740996', revision: '1', recordedAt: '2026-09-29T03:00:00Z' };
const declaration: PaymentDraft = { id: declarationId, revision: '2', createdAt: result.recordedAt, updatedAt: result.recordedAt, state: 'draft', input,
  invoices: [{ invoiceId: input.invoiceIds[0], number: '1/2/2026/10', total: '5000.00', buyerName: 'Cabang fixture', sellerName: 'Omi fixture' }], submission: null };
const checkedImage = parsePaymentEvidenceStatus({ reservationId: '51', revision: '3', state: 'quarantined', expiresAt: result.recordedAt, reference: { id: '52', version: '1', sha256: 'a'.repeat(64) }, scanStatus: 'clean', decoderStatus: 'decoded', safeFailure: null });
const submissionResult = { declarationId, submissionId: '9007199254740997', declarationRevision: '2', reviewRevision: '1' as const, state: 'submitted' as const, submittedAt: result.recordedAt };
const submissionKey = 's.' + declarationId + '.12345678-1234-1234-1234-123456789abc';
test('OCR suggestions are bound to the exact current submission and cannot become payment authority', () => {
  const owner: PaymentDraft = { ...declaration, state: 'submitted', submission: { id: submissionResult.submissionId, reviewRevision: '1', reservationId: '51', evidence: checkedImage.reference!, submittedAt: result.recordedAt } };
  const value = { declarationId, submissionId: owner.submission!.id, evidence: checkedImage.reference, state: 'succeeded', result: {
    id: '81', extractedAt: result.recordedAt, provenance: { engineVersion: 'synthetic', modelVersion: 'synthetic', modelSha256: 'b'.repeat(64), runtimeSha256: 'c'.repeat(64), preprocessingVersion: 'synthetic' },
    fields: ['amount', 'currency', 'transfer_date', 'transfer_time', 'recipient', 'account', 'bank', 'reference'].map(field => ({ field, value: null, confidence: null, boundingBox: null, flags: ['unknown'] })), qualityFlags: ['no_text', 'amount_unknown'],
  } };
  assert.equal(parsePaymentOcr(value, owner).result!.fields[0].value, null);
  for (const bad of [{ ...value, declarationId: '1' }, { ...value, submissionId: '1' }, { ...value, evidence: { ...value.evidence, sha256: 'f'.repeat(64) } },
    { ...value, state: 'verified' }, { ...value, verifiedAmount: '5000' }, { ...value, state: 'queued' }, { ...value, result: null },
    { ...value, result: { ...value.result, fields: [...value.result.fields.slice(1), value.result.fields[1]] } },
    { ...value, result: { ...value.result, fields: value.result.fields.map(field => ({ ...field, value: '5000' })) } }]) assert.throws(() => parsePaymentOcr(bad, owner));
  assert.throws(() => parsePaymentOcr(value, declaration));
});
test('submission freezes one explicit eligible image and rejects unchecked or already submitted declarations', () => {
  const command = preparePaymentSubmission(actor, declaration, checkedImage, 'Ajukan untuk diperiksa');
  assert.ok(Object.isFrozen(command)); assert.ok(Object.isFrozen(command.body)); assert.ok(Object.isFrozen(command.body.evidence));
  assert.equal(command.body.evidence.sha256, checkedImage.reference!.sha256); assert.equal(paymentSubmissionOwner(submissionKey), declarationId);
  for (const bad of [{ ...checkedImage, scanStatus: 'unavailable' as const, decoderStatus: 'unavailable' as const }, { ...checkedImage, safeFailure: 'scanner_unavailable' }])
    assert.throws(() => preparePaymentSubmission(actor, declaration, bad, 'Ajukan untuk diperiksa'));
  assert.throws(() => preparePaymentSubmission(actor, { ...declaration, state: 'submitted' }, checkedImage, 'Ajukan untuk diperiksa'));
  for (const bad of [{ ...submissionResult, reviewRevision: 1 }, { ...submissionResult, state: 'verified' }, { ...submissionResult, bankReceiptId: '5' }]) assert.throws(() => parsePaymentSubmissionResult(bad));
});
test('submission makes one exact actor and revision request and preserves lost response uncertainty', async () => {
  const command = preparePaymentSubmission(actor, declaration, checkedImage, 'Ajukan untuk diperiksa'); let calls = 0, lost = false;
  const gateway = createPaymentGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' }); calls++;
    assert.equal(request.url, `https://unit-toko.test/api/v1/billing/payment-declarations/${declarationId}/submit`);
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('If-Match'), '"2"'); assert.equal(request.headers.get('Idempotency-Key'), submissionKey);
    assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); assert.deepEqual(await request.json(), command.body);
    if (lost) throw new Error('lost committed response'); return Response.json(submissionResult);
  } }));
  await assert.rejects(() => gateway.submit(command, submissionKey, () => false)); assert.equal(calls, 0);
  assert.deepEqual(await gateway.submit(command, submissionKey, () => true), submissionResult); assert.equal(calls, 1);
  lost = true; await assert.rejects(() => gateway.submit(command, submissionKey, () => true)); assert.equal(calls, 2);
});
test('submission recovery cannot fall back to draft recovery or clear a different declaration receipt', async () => {
  const values = new Map<string, string>(), storage = { get length() { return values.size; }, key: (index: number) => [...values.keys()][index] ?? null,
    getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); }, removeItem: (key: string) => { values.delete(key); } };
  const journal = new OperationJournal(() => storage), entry = journal.remember(actor, 'billing.payment.submit', submissionKey);
  const legacy = { lookup: async () => { throw new Error('wrong owner'); }, close: async () => { throw new Error('must not close'); } };
  const draftOnly = { 'billing.payment': { lookup: async () => { throw new Error('draft fallback'); } } };
  assert.equal(operationRecoveryCapabilities(entry.operation, draftOnly).canCheck, false);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, draftOnly));
  const absent = { 'billing.payment.submit': { lookup: async () => null } };
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, absent, 'close'));
  await reconcileOperationRecovery(entry, journal, legacy, absent); assert.equal(journal.list(actor).length, 1);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'billing.payment.submit': { lookup: async () => ({ state: 'committed' as const, result: { ...submissionResult, declarationId: '6' } }) } }));
  assert.equal(journal.list(actor).length, 1);
  await reconcileOperationRecovery(entry, journal, legacy, { 'billing.payment.submit': { lookup: async () => ({ state: 'committed' as const, result: submissionResult }) } }); assert.equal(journal.list(actor).length, 0);
});
test('private evidence viewer binds actor, declaration revision and exact hash without retrying or accepting stale bytes', async () => {
  const bytes = new Uint8Array([137, 80, 78, 71]), hash = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)), byte => byte.toString(16).padStart(2, '0')).join('');
  const evidence = parsePaymentEvidenceStatus({ reservationId: '51', revision: '3', state: 'quarantined', expiresAt: result.recordedAt,
    reference: { id: '52', version: '1', sha256: hash }, scanStatus: 'clean', decoderStatus: 'decoded', safeFailure: null });
  let mode = 'ok', calls = 0, current = true;
  const gateway = createPaymentGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' }); calls++;
    assert.equal(request.url, `https://unit-toko.test/api/v1/billing/payment-declarations/${declarationId}/evidence/51/original/query`);
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('If-Match'), '"2"'); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(request.cache, 'no-store');
    assert.deepEqual(await request.json(), { purpose: 'payment-evidence-view', reference: evidence.reference });
    if (mode === 'lost') throw new Error('interrupted'); if (mode === 'stale') current = false;
    return new Response(mode === 'changed' ? new Uint8Array([1, 2, 3, 4]) : bytes, { headers: { 'content-type': mode === 'mime' ? 'text/html' : 'image/png', 'content-length': mode === 'length' ? '5' : '4' } });
  } }));
  const read = () => gateway.original(actor, declarationId, '2', evidence, () => current);
  const blob = await read(); assert.deepEqual(new Uint8Array(await blob.arrayBuffer()), bytes); assert.equal(blob.type, 'image/png');
  for (mode of ['changed', 'mime', 'length', 'lost', 'stale']) { current = true; const before = calls; await assert.rejects(read); assert.equal(calls, before + 1); }
  const before = calls; await assert.rejects(read); assert.equal(calls, before);
  await assert.rejects(() => gateway.original(actor, declarationId, '2', { ...evidence, scanStatus: 'unavailable', decoderStatus: 'unavailable' }, () => true)); assert.equal(calls, before);
});
test('evidence verdict stays separate from payment approval and rejects contradictory server responses', () => {
  const value = { reservationId: '51', revision: '3', state: 'quarantined', expiresAt: result.recordedAt, reference: { id: '52', version: '1', sha256: 'a'.repeat(64) }, scanStatus: 'clean', decoderStatus: 'decoded', safeFailure: null };
  assert.match(paymentEvidenceLabel(parsePaymentEvidenceStatus(value)), /pembayaran belum diverifikasi/);
  assert.match(paymentEvidenceLabel(parsePaymentEvidenceStatus({ ...value, scanStatus: 'infected', decoderStatus: 'unavailable', safeFailure: 'infected_media' })), /berkas ditolak/i);
  assert.match(paymentEvidenceLabel(parsePaymentEvidenceStatus({ ...value, scanStatus: 'unavailable', decoderStatus: 'unavailable', safeFailure: 'scanner_unavailable' })), /tetap ditahan/);
  for (const bad of [{ ...value, scanStatus: 'infected' }, { ...value, safeFailure: 'invalid_media' }, { ...value, state: 'fresh', reference: null }, { ...value, decoderStatus: 'invalid' }, { ...value, approved: true }]) assert.throws(() => parsePaymentEvidenceStatus(bad));
});
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
