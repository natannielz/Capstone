import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure, stableContent } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';
import { parseDraftInput, type DraftInput } from './order-draft-client';
import { parseOrderContext, parseOrderQuote } from './order-quote-client';
import type { OperationReference } from '../administration/operation-journal';

const invalid = (): never => { throw new AccessFailure(502, 'order_submit_response_invalid'); };
function record(value: unknown): Record<string, unknown> { return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : invalid(); }
function money(value: unknown) { return typeof value === 'string' && /^(0|[1-9][0-9]{0,14})\.[0-9]{2}$(?![\s\S])/.test(value) ? value : invalid(); }
const cents = (value: string) => BigInt(value.replace('.', ''));
function hash(value: unknown) { return typeof value === 'string' && /^[a-f0-9]{64}$(?![\s\S])/.test(value) ? value : invalid(); }
function state(value: unknown): 'Pending' | 'NotRequired' { return value === 'Pending' || value === 'NotRequired' ? value : invalid(); }
export function parseOrderSubmitResult(value: unknown) {
  const row = record(value), orderId = priceId(row.orderId), revision = priceId(row.revision);
  if (revision !== '1' || !Array.isArray(row.suborderIds) || row.suborderIds.length < 1 || row.suborderIds.length > 100 ||
    typeof row.recordedAt !== 'string' || row.recordedAt.length > 64 || !Number.isFinite(Date.parse(row.recordedAt))) return invalid();
  const suborderIds = row.suborderIds.map(priceId); if (new Set(suborderIds).size !== suborderIds.length) return invalid();
  return { orderId, revision, suborderIds, recordedAt: row.recordedAt, spendingState: state(row.spendingState) };
}
export type OrderSubmitResult = ReturnType<typeof parseOrderSubmitResult>;
function amounts(row: Record<string, unknown>, input: DraftInput, calculatedAt: unknown) {
  const quote = parseOrderQuote({ ...row, context: input.context, calculatedAt }, input.context, input.selections);
  const tax = money(row.tax), charges = money(row.charges), total = money(row.total);
  if (cents(quote.catalogSubtotal) + cents(tax) + cents(charges) !== cents(total)) return invalid();
  return { quote, tax, charges, total };
}
export function parseCheckoutReview(value: unknown, expected: DraftInput) {
  const row = record(value), input = parseDraftInput(row.input);
  if (stableContent(input) !== stableContent(expected) || input.delivery === null) return invalid();
  return { input, review: hash(row.review), spendingState: state(row.spendingState), ...amounts(row, input, row.calculatedAt) };
}
export type CheckoutReview = ReturnType<typeof parseCheckoutReview>;
function groupIdentity(value: unknown) {
  if (typeof value !== 'string') return invalid();
  const parts = value.split(':');
  if (parts.length !== 4 || !/^[A-Z]{3}$(?![\s\S])/.test(parts[3])) return invalid();
  const organizationId = priceId(parts[0]), shopId = priceId(parts[1]), channelId = priceId(parts[2]);
  if (value !== `${organizationId}:${shopId}:${channelId}:${parts[3]}`) return invalid();
  return { groupKey: value, organizationId, shopId, channelId, currency: parts[3] };
}
function groupName(value: unknown) {
  return value === null ? null : typeof value === 'string' && value.length > 0 && value.length <= 200 && value.trim() === value &&
    !/[\u0000-\u001f\u007f]/.test(value) ? value : invalid();
}
export function parseCheckoutGroups(value: unknown, expected: DraftInput) {
  const source = parseDraftInput(expected), row = record(value), context = parseOrderContext(row.context);
  if (stableContent(context) !== stableContent(source.context) || !Array.isArray(row.items) || row.items.length < 1 || row.items.length > 100) return invalid();
  const remaining = new Map(source.selections.map(line => [line.skuId, line]));
  const items = row.items.map(value => {
    const item = record(value), identity = groupIdentity(item.groupKey), input = parseDraftInput(item.input);
    if (item.organizationId !== identity.organizationId || item.shopId !== identity.shopId || item.channelId !== identity.channelId || item.currency !== identity.currency ||
      stableContent({ ...input, selections: source.selections }) !== stableContent(source)) return invalid();
    for (const line of input.selections) {
      if (stableContent(remaining.get(line.skuId)) !== stableContent(line)) return invalid();
      remaining.delete(line.skuId);
    }
    const quote = parseOrderQuote({ ...item, context, calculatedAt: row.calculatedAt }, context, input.selections);
    return { ...identity, organizationName: groupName(item.organizationName), shopName: groupName(item.shopName), channelName: groupName(item.channelName), input, quote };
  });
  if (remaining.size !== 0 || new Set(items.map(item => item.groupKey)).size !== items.length || items.some(item => item.currency !== items[0].currency)) return invalid();
  return { context, calculatedAt: items[0].quote.calculatedAt, items };
}
export type CheckoutGroup = ReturnType<typeof parseCheckoutGroups>['items'][number];
export const orderSubmitOperation = 'ordering.order.submit';
/** Stable for a saved draft and authenticated channel; edits require recovery of its original attempt. */
export async function checkoutGroupRequestKey(actorId: string, draftId: string, groupKey: string) {
  const actor = priceId(actorId), draft = priceId(draftId), identity = groupIdentity(groupKey);
  const bytes = new TextEncoder().encode(stableContent({ actorId: actor, draftId: draft, groupKey: identity.groupKey }));
  const digest = await crypto.subtle.digest('SHA-256', bytes);
  return `${orderSubmitOperation}:${actor}:${Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('')}`;
}
export async function readCheckoutGroupAttempt(actorId: string, draftId: string, groupKey: string,
  journal: { list: (actorId: string) => OperationReference[] }, lookup: (key: string) => Promise<{ state: 'committed'; result: OrderSubmitResult } | null>) {
  const requestKey = await checkoutGroupRequestKey(actorId, draftId, groupKey);
  const pending = journal.list(actorId).some(entry => entry.operation === orderSubmitOperation && entry.key === requestKey);
  const receipt = await lookup(requestKey);
  return { requestKey, result: receipt?.result ?? null, uncertain: pending && receipt === null };
}
export function parseSubmittedOrder(value: unknown) {
  const row = record(value), submission = parseOrderSubmitResult(row.submission), input = parseDraftInput(row.input);
  if (input.delivery === null || typeof row.buyerName !== 'string' || row.buyerName.trim().length === 0 || row.buyerName.length > 200) return invalid();
  const progress = record(row.progress), revision = priceId(progress.revision);
  if (!['Pending', 'NotRequired', 'Approved', 'Rejected'].includes(String(progress.spendingState)) || !Array.isArray(progress.acceptedSuborders) || !Array.isArray(progress.rejectedSuborders) || typeof progress.cancelled !== 'boolean') return invalid();
  const acceptedSuborders = progress.acceptedSuborders.map(priceId);
  const rejectedSuborders = progress.rejectedSuborders.map(priceId), decided = [...acceptedSuborders, ...rejectedSuborders];
  if (new Set(decided).size !== decided.length || decided.some(id => !submission.suborderIds.includes(id)) ||
    progress.spendingState === 'NotRequired' && submission.spendingState !== 'NotRequired' || ['Approved', 'Rejected'].includes(String(progress.spendingState)) && submission.spendingState !== 'Pending' ||
    progress.spendingState === 'Pending' && (decided.length > 0 || submission.spendingState !== 'Pending') || progress.spendingState === 'Rejected' && (decided.length > 0 || progress.cancelled) ||
    // Substitution decisions and renewed reviews add revisions this list does not enumerate.
    BigInt(revision) < BigInt(1 + decided.length + (['Approved', 'Rejected'].includes(String(progress.spendingState)) ? 1 : 0) + (progress.cancelled ? 1 : 0))) return invalid();
  return { input, submission, buyerName: row.buyerName, progress: { revision, spendingState: progress.spendingState as 'Pending' | 'NotRequired' | 'Approved' | 'Rejected', acceptedSuborders, rejectedSuborders, cancelled: progress.cancelled }, ...amounts(row, input, submission.recordedAt) };
}
export type SubmittedOrder = ReturnType<typeof parseSubmittedOrder>;
export function parseOrderPage(value: unknown, beforeId: string | null = null, sort: 'newest' | 'oldest' = 'newest') {
  if (sort !== 'newest' && sort !== 'oldest') return invalid();
  const row = record(value);
  if (!Array.isArray(row.items) || row.items.length > 25 || typeof row.hasMore !== 'boolean' || row.hasMore && row.items.length !== 25) return invalid();
  const items = row.items.map(parseSubmittedOrder); let cursor = beforeId === null ? null : BigInt(priceId(beforeId));
  for (const item of items) { const id = BigInt(item.submission.orderId); if (cursor !== null && (sort === 'newest' ? id >= cursor : id <= cursor)) return invalid(); cursor = id; }
  return { items, hasMore: row.hasMore };
}
function data<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) {
    const error = result.error && typeof result.error === 'object' ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof error.code === 'string' ? error.code : 'request_failed');
  } return result.data;
}
function key(value: string) { if (!/^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/.test(value)) throw new AccessFailure(400, 'invalid_order_request_key'); return value; }
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': priceId(actorId) });
export function createOrderSubmitGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async groups(actorId: string, input: DraftInput, signal?: AbortSignal) {
      const frozen = parseDraftInput(input);
      return parseCheckoutGroups(data(await client.POST('/api/v1/ordering/checkout/groups/query', { body: { purpose: 'order-preparation', input: frozen },
        params: { header: headers(actorId) }, signal })), frozen);
    },
    async list(actorId: string, beforeId: string | null = null, signal?: AbortSignal) {
      const cursor = beforeId === null ? null : priceId(beforeId);
      return parseOrderPage(data(await client.POST('/api/v1/ordering/orders/query', { body: { purpose: 'order-preparation', context: null, beforeId: cursor, limit: 25 }, params: { header: headers(actorId) }, signal })), cursor);
    },
    async preview(actorId: string, input: DraftInput, signal?: AbortSignal) {
      const frozen = parseDraftInput(input);
      return parseCheckoutReview(data(await client.POST('/api/v1/ordering/orders/preview/query', { body: { purpose: 'order-preparation', input: frozen }, params: { header: headers(actorId) }, signal })), frozen);
    },
    async submit(actorId: string, review: CheckoutReview, requestKey: string, isCurrent: () => boolean) {
      const input = parseDraftInput(review.input), expected = hash(review.review);
      if (!isCurrent()) throw new RequestNotSentError(409, 'order_view_stale');
      const result = parseOrderSubmitResult(data(await client.POST('/api/v1/ordering/orders', { body: { input, review: expected, reason: 'Pemesan mengonfirmasi kemasan, tujuan, dan jumlah biaya pesanan' },
        params: { header: { ...headers(actorId), 'Idempotency-Key': key(requestKey), 'If-Match': '"0"' } } })));
      if (!isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown');
      return result;
    },
    async read(actorId: string, orderId: string, signal?: AbortSignal) {
      const result = parseSubmittedOrder(data(await client.POST('/api/v1/ordering/orders/{id}/query', { body: { purpose: 'order-preparation' }, params: { path: { id: priceId(orderId) }, header: headers(actorId) }, signal })));
      return result.submission.orderId === orderId ? result : invalid();
    },
    async recover(actorId: string, requestKey: string, signal?: AbortSignal) {
      const row = record(data(await client.POST('/api/v1/ordering/orders/operations/query', { body: { purpose: 'operation-recovery', key: key(requestKey) }, params: { header: headers(actorId) }, signal })));
      if (row.status === 'not_confirmed' && row.result === null) return null;
      if (row.status !== 'committed') return invalid(); return { state: 'committed' as const, result: parseOrderSubmitResult(row.result) };
    },
  };
}
