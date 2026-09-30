import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createPaymentReviewGateway, parsePaymentReview, parsePaymentReviewPage } from '../src/modules/billing/payment-review-client';
import { identityDestinations, identityReturnPath } from '../src/modules/identity/identity-workflow';

const actor = '9007199254740993', id = '9007199254740994', time = '2026-09-30T03:00:00Z';
const owner = { id, revision: '2', state: 'submitted', createdAt: time, updatedAt: time,
  input: { invoiceIds: ['11'], declaredAmount: '1234.56', transferDate: '2026-09-30', payerName: 'Pembayar fixture', bankReference: null, note: null },
  invoices: [{ invoiceId: '11', number: '1/2/2026/3', total: '5000.00', buyerName: 'Cabang fixture', sellerName: 'Omi fixture' }],
  submission: { id: '41', reviewRevision: '1', reservationId: '51', evidence: { id: '61', version: '1', sha256: 'a'.repeat(64) }, submittedAt: time } };
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
