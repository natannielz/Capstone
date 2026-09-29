import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure, stableContent } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';
import { parseDraftInput, type DraftInput } from './order-draft-client';
import { parseOrderQuote } from './order-quote-client';

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
export function parseSubmittedOrder(value: unknown) {
  const row = record(value), submission = parseOrderSubmitResult(row.submission), input = parseDraftInput(row.input);
  if (input.delivery === null) return invalid();
  const progress = record(row.progress), revision = priceId(progress.revision);
  if (!['Pending', 'NotRequired', 'Approved'].includes(String(progress.spendingState)) || !Array.isArray(progress.acceptedSuborders)) return invalid();
  const acceptedSuborders = progress.acceptedSuborders.map(priceId);
  if (new Set(acceptedSuborders).size !== acceptedSuborders.length || acceptedSuborders.some(id => !submission.suborderIds.includes(id)) ||
    progress.spendingState === 'NotRequired' && submission.spendingState !== 'NotRequired' || progress.spendingState === 'Approved' && submission.spendingState !== 'Pending' ||
    progress.spendingState === 'Pending' && (acceptedSuborders.length > 0 || submission.spendingState !== 'Pending') || BigInt(revision) !== BigInt(1) + BigInt(acceptedSuborders.length) + (progress.spendingState === 'Approved' ? BigInt(1) : BigInt(0))) return invalid();
  return { input, submission, progress: { revision, spendingState: progress.spendingState as 'Pending' | 'NotRequired' | 'Approved', acceptedSuborders }, ...amounts(row, input, submission.recordedAt) };
}
export type SubmittedOrder = ReturnType<typeof parseSubmittedOrder>;
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
