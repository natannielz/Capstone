import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';
import { parseDraftInput, parseOrderDelivery } from './order-draft-client';
import { parseOrderContext, parseOrderQuote } from './order-quote-client';
import { orderLineRevision } from './order-line-identity';

export type ReviewStage = 'Spending' | 'Store' | 'Cancellation';
export type ReviewAction = 'Approve' | 'Reject' | 'CancelRemainder';
const invalid = (): never => { throw new AccessFailure(502, 'order_review_response_invalid'); };
function record(value: unknown): Record<string, unknown> { return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : invalid(); }
function stage(value: unknown): ReviewStage { return value === 'Spending' || value === 'Store' || value === 'Cancellation' ? value : invalid(); }
function action(value: unknown): ReviewAction { return value === 'Approve' || value === 'Reject' || value === 'CancelRemainder' ? value : invalid(); }
function money(value: unknown) { return typeof value === 'string' && /^(0|[1-9][0-9]{0,14})\.[0-9]{2}$(?![\s\S])/.test(value) ? value : invalid(); }
function hash(value: unknown) { return typeof value === 'string' && /^[a-f0-9]{64}$(?![\s\S])/.test(value) ? value : invalid(); }
function time(value: unknown) { return typeof value === 'string' && value.length < 65 && Number.isFinite(Date.parse(value)) ? value : invalid(); }
const cents = (value: string) => BigInt(value.replace('.', ''));
export function orderDecisionOperation(value: ReviewStage, selected: ReviewAction = value === 'Cancellation' ? 'CancelRemainder' : 'Approve') {
  if (value === 'Cancellation') return selected === 'CancelRemainder' ? 'ordering.order.cancel' : invalid();
  if (selected === 'CancelRemainder') return invalid();
  return value === 'Spending' ? 'ordering.order.spending.' + (selected === 'Reject' ? 'reject' : 'approve') : 'ordering.order.store.' + (selected === 'Reject' ? 'reject' : 'accept');
}
export function orderDecisionSelection(operation: string): { stage: ReviewStage; action: ReviewAction } {
  const values: Readonly<Record<string, { stage: ReviewStage; action: ReviewAction }>> = {
    'ordering.order.spending.approve': { stage: 'Spending', action: 'Approve' }, 'ordering.order.spending.reject': { stage: 'Spending', action: 'Reject' },
    'ordering.order.store.accept': { stage: 'Store', action: 'Approve' }, 'ordering.order.store.reject': { stage: 'Store', action: 'Reject' },
    'ordering.order.cancel': { stage: 'Cancellation', action: 'CancelRemainder' },
  }; return Object.hasOwn(values, operation) ? values[operation] : invalid();
}
export function orderDecisionStage(operation: string): ReviewStage { return orderDecisionSelection(operation).stage; }
export function parseOrderReview(value: unknown, expectedStage: ReviewStage) {
  const row = record(value), reviewStage = stage(row.stage), orderId = priceId(row.orderId), suborderId = row.suborderId === null ? null : priceId(row.suborderId), revision = priceId(row.revision);
  if (reviewStage !== expectedStage || (reviewStage !== 'Store') !== (suborderId === null) || BigInt(revision) > BigInt(103) ||
    typeof row.buyerName !== 'string' || row.buyerName.length < 1 || row.buyerName.length > 200 || typeof row.canDecide !== 'boolean' ||
    typeof row.cancelled !== 'boolean' || !['Pending', 'NotRequired', 'Approved', 'Rejected'].includes(String(row.spendingState)) || !Array.isArray(row.lines)) return invalid();
  if (row.lines.length < 1 || row.lines.length > 100) return invalid();
  const context = parseOrderContext(row.context), delivery = parseOrderDelivery(row.delivery), orderedAt = time(row.orderedAt);
  if (!delivery) return invalid();
  // Each accepted line keeps its original package and price. Checkout still admits one line per SKU.
  const accepted = row.lines.map(item => {
    const line = record(item), definition = record(line.definition), price = record(line.price);
    const selected = parseDraftInput({ context, delivery, purchaseReference: null, selections: [{ skuId: definition.skuId,
      definitionId: definition.id, definitionVersion: definition.version, priceId: price.id, priceVersion: price.version, packages: line.packages }] }).selections;
    const checked = parseOrderQuote({ ...row, lines: [line], catalogSubtotal: line.catalogSubtotal, calculatedAt: orderedAt }, context, selected);
    const lineRevision = orderLineRevision(line.lineRevision);
    if (BigInt(lineRevision) > BigInt(revision)) return invalid();
    return { selected: selected[0], line: { ...checked.lines[0], lineRevision }, currency: checked.currency };
  });
  const lines = accepted.map(item => item.line), catalogSubtotal = money(row.catalogSubtotal);
  if (new Set(lines.map(line => `${line.definition.skuId}:${line.lineRevision}`)).size !== lines.length ||
    lines.reduce((sum, line) => sum + cents(line.catalogSubtotal), BigInt(0)) !== cents(catalogSubtotal)) return invalid();
  const input = { context, delivery, purchaseReference: null, selections: accepted.map(item => item.selected) };
  const quote = { context, calculatedAt: orderedAt, currency: accepted[0].currency, catalogSubtotal, lines };
  const spendingState = row.spendingState as 'Pending' | 'NotRequired' | 'Approved' | 'Rejected';
  const tax = row.tax === null ? null : money(row.tax), charges = row.charges === null ? null : money(row.charges), total = row.total === null ? null : money(row.total);
  if (reviewStage !== 'Store') {
    if (row.storeState !== null || tax === null || charges === null || total === null || cents(total) !== cents(quote.catalogSubtotal) + cents(tax) + cents(charges) ||
      reviewStage === 'Spending' && row.canDecide !== (!row.cancelled && spendingState === 'Pending') || row.cancelled && row.canDecide || spendingState === 'Rejected' && row.canDecide) return invalid();
  } else if (tax !== null || charges !== null || total !== null || !['awaitingSpending', 'awaitingAcceptance', 'accepted', 'rejected', 'cancelled', 'spendingRejected'].includes(String(row.storeState)) ||
    row.canDecide !== (row.storeState === 'awaitingAcceptance' && !row.cancelled) || row.storeState === 'awaitingSpending' && (spendingState !== 'Pending' || row.cancelled) ||
    ['awaitingAcceptance', 'accepted'].includes(String(row.storeState)) && (spendingState === 'Pending' || spendingState === 'Rejected' || row.cancelled) ||
    row.storeState === 'rejected' && ['Pending', 'Rejected'].includes(spendingState) ||
    row.storeState === 'spendingRejected' && (spendingState !== 'Rejected' || row.cancelled) || row.storeState === 'cancelled' && (!row.cancelled || spendingState === 'Rejected')) return invalid();
  return { orderId, suborderId, stage: reviewStage, revision, review: hash(row.review), buyerName: row.buyerName, input, quote, tax, charges, total,
    spendingState, storeState: row.storeState as string | null, canDecide: row.canDecide, cancelled: row.cancelled, orderedAt };
}
export type OrderReview = ReturnType<typeof parseOrderReview>;
export function parseOrderDecisionResult(value: unknown) {
  const row = record(value), selected = stage(row.stage), selectedAction = action(row.action), suborderId = row.suborderId === null ? null : priceId(row.suborderId), revision = priceId(row.revision);
  orderDecisionOperation(selected, selectedAction);
  if ((selected !== 'Store') !== (suborderId === null) || BigInt(revision) < BigInt(2) || BigInt(revision) > BigInt(103) ||
    row.state !== (selectedAction === 'Reject' ? 'rejected' : selectedAction === 'CancelRemainder' ? 'cancelled' : selected === 'Spending' ? 'approved' : 'accepted')) return invalid();
  return { decisionId: priceId(row.decisionId), orderId: priceId(row.orderId), suborderId, stage: selected, action: selectedAction, revision, state: row.state as 'approved' | 'accepted' | 'rejected' | 'cancelled', recordedAt: time(row.recordedAt) };
}
export type OrderDecisionResult = ReturnType<typeof parseOrderDecisionResult>;
function data<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) { const row = result.error && typeof result.error === 'object' ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof row.code === 'string' ? row.code : 'request_failed'); } return result.data;
}
const headers = (actor: string) => ({ 'X-Expected-Actor-Id': priceId(actor) });
function key(value: string) { if (!/^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/.test(value)) throw new AccessFailure(400, 'invalid_order_request_key'); return value; }
export function createOrderReviewGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async list(actor: string, selected: ReviewStage, beforeId: string | null = null, signal?: AbortSignal) {
      const row = record(data(await client.POST('/api/v1/ordering/reviews/query', { body: { purpose: 'order-review', stage: selected, beforeId }, params: { header: headers(actor) }, signal })));
      if (!Array.isArray(row.items) || row.items.length > 25 || typeof row.hasMore !== 'boolean') return invalid();
      const items = row.items.map(item => parseOrderReview(item, selected)); let previous = beforeId === null ? null : BigInt(priceId(beforeId));
      for (const item of items) { const cursor = BigInt(item.suborderId ?? item.orderId); if (previous !== null && cursor >= previous) return invalid(); previous = cursor; }
      return { items, hasMore: row.hasMore };
    },
    async read(actor: string, orderId: string, suborderId: string | null, selected: ReviewStage, signal?: AbortSignal) {
      const value = parseOrderReview(data(await client.POST('/api/v1/ordering/reviews/{id}/query', { body: { purpose: 'order-review', stage: selected, suborderId },
        params: { path: { id: priceId(orderId) }, header: headers(actor) }, signal })), selected);
      if (value.orderId !== orderId || value.suborderId !== suborderId) return invalid(); return value;
    },
    async decide(actor: string, review: OrderReview, reason: string, requestKey: string, isCurrent: () => boolean, selectedAction: ReviewAction = 'Approve') {
      orderDecisionOperation(review.stage, selectedAction);
      if (reason.trim() !== reason || reason.length < 1 || reason.length > 1000 || /[\u0000-\u001f\u007f]/.test(reason)) throw new RequestNotSentError(400, 'invalid_reason');
      if (!isCurrent()) throw new RequestNotSentError(409, 'order_view_stale');
      const result = parseOrderDecisionResult(data(await client.POST('/api/v1/ordering/reviews/{id}/decisions', {
        body: { stage: review.stage, suborderId: review.suborderId, action: selectedAction, review: hash(review.review), reason },
        params: { path: { id: priceId(review.orderId) }, header: { ...headers(actor), 'If-Match': '"' + priceId(review.revision) + '"', 'Idempotency-Key': key(requestKey) } } })));
      if (!isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown');
      if (result.orderId !== review.orderId || result.suborderId !== review.suborderId || result.stage !== review.stage || result.action !== selectedAction || BigInt(result.revision) !== BigInt(review.revision) + BigInt(1)) return invalid();
      return result;
    },
    async recover(actor: string, selected: ReviewStage, requestKey: string, signal?: AbortSignal, selectedAction: ReviewAction = 'Approve') {
      orderDecisionOperation(selected, selectedAction);
      const row = record(data(await client.POST('/api/v1/ordering/reviews/operations/query', { body: { purpose: 'operation-recovery', stage: selected, action: selectedAction, key: key(requestKey) }, params: { header: headers(actor) }, signal })));
      if (row.status === 'not_confirmed' && row.result === null) return null;
      if (row.status !== 'committed') return invalid(); const result = parseOrderDecisionResult(row.result);
      if (result.stage !== selected || result.action !== selectedAction) return invalid(); return { state: 'committed' as const, result };
    },
  };
}
