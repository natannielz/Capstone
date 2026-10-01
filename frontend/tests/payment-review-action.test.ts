import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { parsePaymentDraft } from '../src/modules/billing/payment-client';
import { createPaymentReviewActionGateway, parseReviewActionView, parseReviewActionResult, prepareReviewAction } from '../src/modules/billing/payment-review-action-client';
import { OperationJournal, reconcileOperationRecovery, operationRecoveryCapabilities } from '../src/modules/administration/operation-journal';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';

const actor = '9007199254740993', time = '2026-10-01T03:00:00Z', key = 'ra.51.11111111-1111-4111-8111-111111111111';
const evidence = { id: '61', version: '1', sha256: 'a'.repeat(64) };
const owner = parsePaymentDraft({ id: '51', revision: '2', state: 'submitted', createdAt: time, updatedAt: time,
  input: { invoiceIds: ['11'], declaredAmount: '100.00', transferDate: '2026-10-01', payerName: 'Fixture', bankReference: null, note: null },
  invoices: [{ invoiceId: '11', number: '1/2/2026/3', total: '500.00', buyerName: 'Cabang', sellerName: 'Toko' }],
  submission: { id: '41', reviewRevision: '1', reservationId: '71', evidence, submittedAt: time } });
const raw = { binding: 'b'.repeat(64), declarationId: '51', declarationRevision: '2', submissionId: '41', evidence, reviewRevision: '0', state: 'pending',
  assigneeId: null, recordedBy: null, recordedAt: null, reason: null, publicExplanation: null, verified: false, submittedAt: time, observedAt: '2026-10-01T04:00:00Z' };
const view = parseReviewActionView(raw, owner), saved = { declarationId: '51', submissionId: '41', reviewRevision: '1', actionId: '91', action: 'claim', state: 'assigned',
  assigneeId: actor, recordedAt: time, operation: 'billing.payment.review.manage' } as const;
test('review actions bind exact submission, require a live transition and freeze the reason without financial assertions', () => {
  const command = prepareReviewAction(actor, view, 'claim', 'Reviewed owner'); assert.ok(Object.isFrozen(command) && Object.isFrozen(command.body));
  assert.throws(() => parseReviewActionView({ ...raw, evidence: { ...evidence, sha256: 'c'.repeat(64) } }, owner));
  assert.throws(() => parseReviewActionView({ ...raw, observedAt: '2026-09-30T03:00:00Z' }, owner));
  assert.throws(() => prepareReviewAction(actor, { ...view, verified: true }, 'reject', 'Reviewed'));
  assert.throws(() => prepareReviewAction(actor, { ...view, state: 'rejected', assigneeId: actor, recordedAt: time, recordedBy: actor, reason: 'Reviewed', reviewRevision: '1' }, 'claim', 'Reviewed'));
  assert.throws(() => prepareReviewAction(actor, view, 'release', 'Reviewed')); assert.throws(() => prepareReviewAction(actor, view, 'claim', ''));
  assert.throws(() => parseReviewActionResult({ ...saved, state: 'pending' }, key)); assert.throws(() => parseReviewActionResult({ ...saved, actionId: 91 }, key));
  assert.throws(() => parseReviewActionResult({ ...saved, paid: true }, key)); assert.throws(() => parseReviewActionResult({ ...saved, declarationId: '52' }, key));
  const closing = prepareReviewAction(actor, view, 'request_replacement', 'Internal check', 'Please upload a clear original.');
  assert.equal(closing.body.reason, 'Internal check'); assert.equal(closing.body.publicExplanation, 'Please upload a clear original.'); assert.ok(Object.isFrozen(closing.body));
  assert.throws(() => prepareReviewAction(actor, view, 'reject', 'Internal check'));
  assert.throws(() => prepareReviewAction(actor, view, 'claim', 'Internal check', 'Customer text'));
  for (const explanation of ['', ' hidden ', 'a'.repeat(1001), 'hidden\u200b']) assert.throws(() => prepareReviewAction(actor, view, 'reject', 'Internal check', explanation));
  assert.throws(() => parseReviewActionView({ ...raw, publicExplanation: 'Unexpected open text' }, owner));
});
test('review client sends actor CSRF and exact version/key, rejects stale ownership and never resends during recovery', async () => {
  const command = prepareReviewAction(actor, view, 'claim', 'Reviewed owner'); let writes = 0;
  const gateway = createPaymentReviewActionGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(request.cache, 'no-store');
    if (request.url.endsWith('/operations/query')) { assert.deepEqual(await request.json(), { purpose: 'operation-recovery', key }); return Response.json({ status: 'committed', result: saved }); }
    writes++; assert.equal(request.headers.get('If-Match'), '"0"'); assert.equal(request.headers.get('Idempotency-Key'), key); assert.deepEqual(await request.json(), command.body); return Response.json(saved);
  } }));
  await assert.rejects(() => gateway.save(command, key, () => false)); await assert.rejects(() => gateway.save(command, key.replace('51.', '52.'), () => true)); assert.equal(writes, 0);
  assert.deepEqual(await gateway.save(command, key, () => true), saved); assert.deepEqual((await gateway.recover(actor, key))?.result, saved); assert.equal(writes, 1);
});
test('review receipt recovery keeps metadata only and preserves unconfirmed/malformed results without close', async () => {
  const rows = new Map<string, string>(), journal = new OperationJournal(() => ({ get length() { return rows.size; }, key: index => [...rows.keys()][index] ?? null,
    getItem: key => rows.get(key) ?? null, setItem: (key, value) => { rows.set(key, value); }, removeItem: key => { rows.delete(key); } }));
  const reference = journal.remember(actor, saved.operation, key), legacy = { lookup: async () => { throw new Error('Wrong owner'); }, close: async () => { throw new Error('No close'); } };
  assert.deepEqual(Object.keys(reference).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.equal(recoveryPage(saved.operation).href, '/billing/payment-reviews');
  assert.equal(operationRecoveryCapabilities(saved.operation, { 'billing.payment.review.manage': { lookup: async () => null } }).canClose, false);
  assert.equal((await reconcileOperationRecovery(reference, journal, legacy, { 'billing.payment.review.manage': { lookup: async () => null } })).state, 'unconfirmed'); assert.equal(rows.size, 1);
  await assert.rejects(() => reconcileOperationRecovery(reference, journal, legacy, { 'billing.payment.review.manage': { lookup: async () => ({ state: 'committed', result: { ...saved, declarationId: '52' } }) } })); assert.equal(rows.size, 1);
  assert.equal((await reconcileOperationRecovery(reference, journal, legacy, { 'billing.payment.review.manage': { lookup: async () => ({ state: 'committed', result: saved }) } })).state, 'committed'); assert.equal(rows.size, 0);
});
