import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createOrderDraftGateway, discardConflictMessage, parseDiscardedDraft } from '../src/modules/ordering/order-draft-client';

const actor = '9007199254740993', id = '9007199254740999';
const input = { context: { organizationId: '10', branchId: '11', divisionId: null }, selections: [
  { skuId: '100', definitionId: '200', definitionVersion: '1', priceId: '300', priceVersion: '1', packages: 2 } ], delivery: null, purchaseReference: 'PO 7' };
const discardedAt = '2026-10-20T03:00:00Z', restorableUntil = '2026-11-19T03:00:00Z';
function gateway(handler: (request: Request) => Promise<Response>) { return createOrderDraftGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async value => {
  assert.ok(value instanceof Request); if (value.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
  assert.equal(value.headers.get('X-Expected-Actor-Id'), actor); return handler(value);
} })); }

test('discard sends the draft revision and key and accepts only an exact 30-day restore window', async () => {
  const result = await gateway(async request => {
    assert.match(request.url, new RegExp(`/api/v1/ordering/drafts/${id}/discard$`));
    assert.equal(request.headers.get('Idempotency-Key'), 'discard-1'); assert.equal(request.headers.get('If-Match'), '"3"');
    assert.deepEqual(await request.json(), { reason: 'Tidak jadi dipesan' });
    return Response.json({ draftId: id, revision: '3', discardedAt, restorableUntil });
  }).discard(actor, id, '3', 'Tidak jadi dipesan', 'discard-1');
  assert.equal(result.restorableUntil, restorableUntil);
  await assert.rejects(() => gateway(async () => Response.json({ draftId: id, revision: '3', discardedAt, restorableUntil: '2026-11-20T03:00:00Z' }))
    .discard(actor, id, '3', 'Tidak jadi dipesan', 'discard-2'), 'a window other than 30 days is refused');
  await assert.rejects(() => gateway(async () => Response.json({ draftId: '7', revision: '3', discardedAt, restorableUntil }))
    .discard(actor, id, '3', 'Tidak jadi dipesan', 'discard-3'), 'another draft id is refused');
});

test('restore and the discarded list bind the draft and branch', async () => {
  const restored = await gateway(async request => {
    assert.match(request.url, new RegExp(`/api/v1/ordering/drafts/${id}/restore$`)); assert.equal(request.headers.get('If-Match'), null);
    return Response.json({ draftId: id, revision: '3', restoredAt: '2026-10-21T03:00:00Z' });
  }).restore(actor, id, 'Dipakai lagi', 'restore-1');
  assert.equal(restored.revision, '3');
  const item = { id, revision: '3', discardedAt, restorableUntil, input };
  assert.equal((await gateway(async () => Response.json({ items: [item], hasMore: false })).listDiscarded(actor, input.context)).items[0].input.purchaseReference, 'PO 7');
  await assert.rejects(() => gateway(async () => Response.json({ items: [{ ...item, input: { ...input, context: { ...input.context, branchId: '12' } } }], hasMore: false }))
    .listDiscarded(actor, input.context));
  assert.throws(() => parseDiscardedDraft({ ...item, restorableUntil: discardedAt }));
});

test('discard conflicts read as plain Bahasa Indonesia', () => {
  assert.match(discardConflictMessage('order_draft_discarded')!, /sudah dibuang/);
  assert.match(discardConflictMessage('order_draft_restore_expired')!, /Masa pemulihan/);
  assert.equal(discardConflictMessage('something_else'), null);
});
