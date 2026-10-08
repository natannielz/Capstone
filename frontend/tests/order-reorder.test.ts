import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { prepareReorderDraft, reorderDraftReason } from '../src/modules/ordering/order-reorder';
import { createOrderDraftGateway, prepareDraftCommand } from '../src/modules/ordering/order-draft-client';
import { parseSubmittedOrder } from '../src/modules/ordering/order-submit-client';
import { actorId, family, sku, definition } from './catalog-fixtures';

const originalInput = { context: { organizationId: '10', branchId: '11', divisionId: null }, selections: [
  { skuId: sku.id, definitionId: definition.id, definitionVersion: definition.version, priceId: '9007199254740999', priceVersion: '2', packages: 2 } ],
  delivery: { recipientName: 'Penerima fixture', phone: '081234567890', address: 'Alamat fixture', city: 'Jakarta', postalCode: '12810', instructions: null }, purchaseReference: 'Referensi pesanan lama' };
function original() {
  return parseSubmittedOrder({ input: structuredClone(originalInput), currency: 'IDR', catalogSubtotal: '24000.50', tax: '0.00', charges: '1500.00', total: '25500.50',
    buyerName: 'Cabang fixture', submission: { orderId: '9007199254741001', revision: '1', suborderIds: ['9007199254741002'], recordedAt: definition.effectiveAt, spendingState: 'Pending' },
    progress: { revision: '1', spendingState: 'Pending', acceptedSuborders: [], rejectedSuborders: [], cancelled: false },
    lines: [{ familyId: family.id, familyName: family.name, publicId: sku.publicId, skuCode: sku.code, definition,
      price: { id: originalInput.selections[0].priceId, skuId: sku.id, version: '2', currency: 'IDR', unitPrice: '12000.25', effectiveAt: definition.effectiveAt },
      packages: 2, baseUnits: '24', catalogSubtotal: '24000.50' }] });
}

test('repeat order creates editable intent with exact historical package versions but no accepted price terms or saved identity', () => {
  const order = original(), before = structuredClone(order), repeat = prepareReorderDraft(order);
  assert.equal(repeat.sourceOrderId, '9007199254741001');
  assert.equal(repeat.draft.id, null); assert.equal(repeat.draft.revision, '0');
  assert.deepEqual(repeat.draft.input.context, order.input.context);
  assert.deepEqual(repeat.draft.input.selections, order.input.selections);
  assert.equal(repeat.draft.input.purchaseReference, null);
  assert.deepEqual(Object.keys(repeat.draft.input).sort(), ['context', 'delivery', 'purchaseReference', 'selections']);
  repeat.draft.input.selections[0].packages = 4;
  repeat.draft.input.delivery!.address = 'Tujuan baru yang perlu ditinjau';
  assert.deepEqual(order, before, 'Editing a repeat must preserve the complete accepted order snapshot');
  assert.throws(() => reorderDraftReason(undefined));
  assert.throws(() => reorderDraftReason('9007199254741001.0'));
});

test('repeat is saved only through the ordinary scoped draft command and retains source in immutable reason', async () => {
  const repeat = prepareReorderDraft(original());
  const command = prepareDraftCommand(actorId, repeat.draft.id, repeat.draft.revision, repeat.draft.input, reorderDraftReason(repeat.sourceOrderId));
  let writes = 0;
  const gateway = createOrderDraftGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request);
    if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
    writes++; assert.equal(new URL(request.url).pathname, '/api/v1/ordering/drafts');
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actorId);
    assert.equal(request.headers.get('If-Match'), '"0"'); assert.equal(request.headers.get('Idempotency-Key'), 'repeat-original');
    const body = await request.json() as Record<string, unknown>;
    assert.deepEqual(body, { draftId: null, input: repeat.draft.input, reason: 'Buat draf pemesanan ulang dari pesanan 9007199254741001' });
    return Response.json({ draftId: '9007199254741003', versionId: '9007199254741004', revision: '1', recordedAt: definition.effectiveAt });
  } }));
  await assert.rejects(() => gateway.submit(command, 'repeat-original', () => false)); assert.equal(writes, 0);
  assert.equal((await gateway.submit(command, 'repeat-original')).draftId, '9007199254741003'); assert.equal(writes, 1);
});
