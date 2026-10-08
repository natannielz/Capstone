import { AccessFailure } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';
import { parseDraftInput } from './order-draft-client';
import type { SubmittedOrder } from './order-submit-client';

/** An editable intention only: neither a saved draft nor an accepted commercial snapshot. */
export function prepareReorderDraft(order: SubmittedOrder) {
  const sourceOrderId = priceId(order.submission.orderId);
  // Parse into new objects; editing the repeat must never mutate the historical order.
  const input = parseDraftInput({ ...order.input, purchaseReference: null });
  return { sourceOrderId, draft: { id: null, revision: '0', input }, historicalQuote: order.quote } as const;
}

export function reorderDraftReason(sourceOrderId: string | undefined) {
  if (sourceOrderId === undefined) throw new AccessFailure(400, 'invalid_order_draft');
  return 'Buat draf pemesanan ulang dari pesanan ' + priceId(sourceOrderId);
}
