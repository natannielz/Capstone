import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createPaymentReviewGateway, parsePaymentDuplicates, parsePaymentReview, parsePaymentReviewPage, parsePaymentReviewQueue } from '../src/modules/billing/payment-review-client';
import { identityDestinations, identityReturnPath } from '../src/modules/identity/identity-workflow';

const actor = '9007199254740993', id = '9007199254740994', time = '2026-09-30T03:00:00Z';
const owner = { id, revision: '2', state: 'submitted', createdAt: time, updatedAt: time,
  input: { invoiceIds: ['11'], declaredAmount: '1234.56', transferDate: '2026-09-30', payerName: 'Pembayar fixture', bankReference: null, note: null },
  invoices: [{ invoiceId: '11', number: '1/2/2026/3', total: '5000.00', buyerName: 'Cabang fixture', sellerName: 'Omi fixture' }],
  submission: { id: '41', reviewRevision: '1', reservationId: '51', evidence: { id: '61', version: '1', sha256: 'a'.repeat(64) }, submittedAt: time } };
test('bulk queue binds current submitted rows, ownership and server time and accepts only explicit nonfinancial discrepancy flags', async () => {
  const row = { declaration: owner, reviewRevision: '1', state: 'assigned', assigneeId: actor, recordedAt: time, ocrState: 'succeeded', discrepancies: ['amount_differs', 'human_corrected'] };
  const page = { items: [row], hasMore: false, observedAt: '2026-09-30T04:00:00Z' };
  assert.equal(parsePaymentReviewQueue(page, null).items[0].declaration.id, id);
  for (const state of ['rejected', 'replacement_requested']) {
    const closed = { ...row, state }; assert.equal(parsePaymentReviewQueue({ ...page, items: [closed] }, null).items[0].assigneeId, actor);
    assert.throws(() => parsePaymentReviewQueue({ ...page, items: [{ ...closed, assigneeId: null }] }, null));
  }
  assert.equal(parsePaymentReviewQueue({ ...page, items: [{ ...row, state: 'pending', assigneeId: null }] }, null).items[0].state, 'pending');
  for (const changed of [{ ...row, assigneeId: null }, { ...row, recordedAt: null }, { ...row, ocrState: 'queued' }, { ...row, discrepancies: ['paid'] }, { ...row, discrepancies: ['amount_differs', 'amount_differs'] }, { ...row, reason: 'Private staff note' }])
    assert.throws(() => parsePaymentReviewQueue({ ...page, items: [changed] }, null));
  assert.throws(() => parsePaymentReviewQueue({ ...page, observedAt: '2026-09-29T03:00:00Z' }, null)); assert.throws(() => parsePaymentReviewQueue(page, id));
  assert.throws(() => parsePaymentReviewQueue({ ...page, items: [row, row] }, null));
  let reads = 0;
  const gateway = createPaymentReviewGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf-test' });
    reads++; assert.equal(new URL(request.url).pathname, '/api/v1/billing/payment-reviews/queue/query'); assert.equal(request.cache, 'no-store');
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf-test'); assert.equal(request.headers.get('Idempotency-Key'), null);
    assert.deepEqual(await request.json(), { purpose: 'payment-review', beforeId: null }); return Response.json(page);
  } }));
  assert.deepEqual(await gateway.queue(actor, null), parsePaymentReviewQueue(page, null)); assert.equal(reads, 1);
});

test('review responses retain exact IDs and submitted state, reject draft/private or invented cash state and invalid paging', () => {
  assert.equal(parsePaymentReview(owner).id, id);
  for (const value of [{ ...owner, state: 'draft', submission: null }, { ...owner, state: 'verified' }, { ...owner, cashReceived: '1234.56' }, { ...owner, id: Number(id) }]) assert.throws(() => parsePaymentReview(value));
  assert.equal(parsePaymentReviewPage({ items: [owner], hasMore: false }, null).items.length, 1);
  for (const value of [{ items: [owner, owner], hasMore: false }, { items: [owner], hasMore: true }, { items: [owner], hasMore: false, total: 100 }]) assert.throws(() => parsePaymentReviewPage(value, null));
  assert.throws(() => parsePaymentReviewPage({ items: [owner], hasMore: false }, id));
});
test('review gateway uses the separate actor-bound read routes and rejects a different returned declaration', async () => {
  const requests: Request[] = []; let wrong = false;
  const gateway = createPaymentReviewGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request);
    if (new URL(request.url).pathname === '/api/v1/identity/csrf') return Response.json({ requestToken: 'csrf-test' });
    requests.push(request); assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf-test');
    assert.equal(request.cache, 'no-store'); assert.equal(request.headers.get('Idempotency-Key'), null);
    if (new URL(request.url).pathname.endsWith('/ocr/query')) {
      assert.equal(request.headers.get('If-Match'), '"2"'); assert.deepEqual(await request.json(), { purpose: 'payment-ocr-view', reference: owner.submission.evidence });
      return Response.json({ declarationId: id, submissionId: '41', evidence: owner.submission.evidence, state: 'queued', result: null });
    }
    assert.equal(new URL(request.url).pathname, '/api/v1/billing/payment-reviews/' + id + '/query');
    assert.deepEqual(await request.json(), { purpose: 'payment-review' }); return Response.json({ ...owner, id: wrong ? '12' : id });
  } }));
  const row = await gateway.read(actor, id); assert.equal((await gateway.ocr(actor, row)).state, 'queued');
  wrong = true; await assert.rejects(() => gateway.read(actor, id)); assert.equal(requests.length, 3);
});
test('Finance template names and invoice access alone do not expose review navigation', () => {
  assert.equal(identityReturnPath('/billing/payment-reviews'), '/billing/payment-reviews');
  for (const actions of [[], ['invoice.read'], ['payment.verify'], ['payment.review.read']]) assert.ok(!identityDestinations(actions).some(item => item.href === '/billing/payment-reviews'));
  assert.ok(identityDestinations(['invoice.read', 'payment.review.read']).some(item => item.href === '/billing/payment-reviews'));
});

test('duplicate hints bind the submitted owner and preserve exact IDs, scoped paging and advisory signals', () => {
  const current = parsePaymentReview(owner), match = { declarationId: '9007199254740995', revision: '1', submissionId: '42', submittedAt: time, signals: ['same_image', 'same_reference'] };
  const page = { declarationId: id, revision: '2', submissionId: '41', items: [match], hasMore: false };
  assert.equal(parsePaymentDuplicates(page, current, null).items[0].declarationId, match.declarationId);
  for (const bad of [{ ...page, revision: '1' }, { ...page, submissionId: '44' }, { ...page, hasMore: true }, { ...page, total: 2 },
    ...[{ ...match, declarationId: id }, { ...match, declarationId: Number(match.declarationId) }, { ...match, signals: ['approved'] },
      { ...match, signals: ['same_image', 'same_image'] }, { ...match, account: 'private' }, { ...match, signals: [] }].map(item => ({ ...page, items: [item] }))])
    assert.throws(() => parsePaymentDuplicates(bad, current, null));
  assert.throws(() => parsePaymentDuplicates(page, current, match.declarationId));
  assert.throws(() => parsePaymentDuplicates({ ...page, items: [match, match] }, current, null));
});

test('duplicate lookup sends actor, exact original and revision with no mutation key or financial authority', async () => {
  let sent = 0;
  const gateway = createPaymentReviewGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request);
    if (new URL(request.url).pathname === '/api/v1/identity/csrf') return Response.json({ requestToken: 'csrf-test' });
    sent++; assert.equal(new URL(request.url).pathname, '/api/v1/billing/payment-reviews/' + id + '/duplicates/query');
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('If-Match'), '"2"');
    assert.equal(request.headers.get('X-CSRF-Token'), 'csrf-test'); assert.equal(request.headers.get('Idempotency-Key'), null); assert.equal(request.cache, 'no-store');
    assert.deepEqual(await request.json(), { purpose: 'payment-review', reference: owner.submission.evidence, beforeId: null });
    return Response.json({ declarationId: id, revision: '2', submissionId: '41', items: [], hasMore: false });
  } }));
  assert.deepEqual((await gateway.duplicates(actor, parsePaymentReview(owner), null)).items, []); assert.equal(sent, 1);
});
