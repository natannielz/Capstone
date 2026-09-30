import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createPaymentManualReviewGateway, parseManualReview, prepareManualReview, parseManualReviewResult } from '../src/modules/billing/payment-manual-review-client';
import { correctionFields } from '../src/modules/billing/payment-correction-client';
import { parsePaymentDraft } from '../src/modules/billing/payment-client';
import { OperationJournal, reconcileOperationRecovery, operationRecoveryCapabilities } from '../src/modules/administration/operation-journal';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';

const actor = '9007199254740993', time = '2026-09-30T03:00:00Z', key = 'mr.51.11111111-1111-4111-8111-111111111111';
const evidence = { id: '61', version: '1', sha256: 'a'.repeat(64) };
const owner = parsePaymentDraft({ id: '51', revision: '2', state: 'submitted', createdAt: time, updatedAt: time,
  input: { invoiceIds: ['11'], declaredAmount: '100.00', transferDate: '2026-09-30', payerName: 'Fixture', bankReference: null, note: null },
  invoices: [{ invoiceId: '11', number: '1/2/2026/3', total: '500.00', buyerName: 'Cabang', sellerName: 'Toko' }],
  submission: { id: '41', reviewRevision: '1', reservationId: '71', evidence, submittedAt: time } });
const raw = { binding: 'b'.repeat(64), declarationId: owner.id, declarationRevision: owner.revision, submissionId: '41', evidence, reviewRevision: '0', failureId: '81',
  failureCode: 'ocr_unavailable', failedAt: time, procedureReference: 'Synthetic approved outage review', fields: correctionFields.map(field => ({ field, value: null })), reviewedBy: null, recordedAt: null };
const shown = parseManualReview(raw, owner), result = { declarationId: owner.id, submissionId: '41', reviewRevision: '1', manualReviewId: '91', recordedAt: time };
test('manual review keeps unknowns and exact IDs and requires original attestation without accepting a security failure', () => {
  const values = shown.fields.map(f => f.field === 'amount' ? { ...f, value: '100.00' } : f);
  assert.deepEqual(parseManualReview({ ...raw, evidence: { sha256: evidence.sha256, version: evidence.version, id: evidence.id } }, owner), shown);
  const prepared = prepareManualReview(actor, shown, values, true, 'Reviewed actual original'); values[0].value = '999.00';
  assert.equal(prepared.body.fields[0].value, '100.00'); assert.equal(prepared.body.fields[7].value, null); assert.ok(Object.isFrozen(prepared.body.fields[0]));
  assert.throws(() => prepareManualReview(actor, shown, shown.fields, false, 'Reviewed')); assert.throws(() => prepareManualReview(actor, shown, shown.fields, true, ''));
  for (const change of [{ failureCode: 'malware_detected' }, { declarationId: '52' }, { submissionId: '42' }, { evidence: { ...evidence, sha256: 'c'.repeat(64) } }, { reviewedBy: actor }, { fields: shown.fields.slice(0, 7) }])
    assert.throws(() => parseManualReview({ ...raw, ...change }, owner));
  assert.throws(() => parseManualReviewResult({ ...result, declarationId: 51 }, key)); assert.throws(() => parseManualReviewResult({ ...result, declarationId: '52' }, key));
});
test('manual gateway sends actor CSRF exact revision and does not dispatch a stale write', async () => {
  let writes = 0; const command = prepareManualReview(actor, shown, shown.fields, true, 'Unknown fields retained');
  const gateway = createPaymentManualReviewGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (new URL(request.url).pathname === '/api/v1/identity/csrf') return Response.json({ requestToken: 'csrf' });
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(request.cache, 'no-store');
    if (request.url.endsWith('/query')) { assert.equal(request.headers.get('If-Match'), '"2"'); return Response.json(raw); }
    writes++; assert.equal(request.headers.get('Idempotency-Key'), key); assert.equal(request.headers.get('If-Match'), '"0"'); assert.deepEqual(await request.json(), command.body); return Response.json(result);
  } }));
  assert.deepEqual(await gateway.read(actor, owner), shown); await assert.rejects(() => gateway.save(command, key, () => false)); assert.equal(writes, 0);
  assert.deepEqual(await gateway.save(command, key, () => true), result); assert.equal(writes, 1);
});
test('manual review recovery stores metadata only, retains unknown outcomes and never resends or closes a financial review', async () => {
  const rows = new Map<string, string>(), journal = new OperationJournal(() => ({ get length() { return rows.size; }, key: i => [...rows.keys()][i] ?? null,
    getItem: k => rows.get(k) ?? null, setItem: (k, value) => { rows.set(k, value); }, removeItem: k => { rows.delete(k); } }));
  const entry = journal.remember(actor, 'billing.payment.review.manual', key), legacy = { lookup: async () => { throw new Error('Wrong owner'); }, close: async () => { throw new Error('Cannot close'); } };
  assert.deepEqual(Object.keys(entry).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.equal(recoveryPage(entry.operation).href, '/billing/payment-reviews');
  assert.equal(operationRecoveryCapabilities(entry.operation).canCheck, false); assert.equal(operationRecoveryCapabilities(entry.operation).canClose, false);
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, { 'billing.payment.review.manual': { lookup: async () => null } })).state, 'unconfirmed'); assert.equal(rows.size, 1);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'billing.payment.review.manual': { lookup: async () => ({ state: 'committed', result: { ...result, declarationId: '52' } }) } }));
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, { 'billing.payment.review.manual': { lookup: async () => ({ state: 'committed', result }) } })).state, 'committed'); assert.equal(rows.size, 0);
});
