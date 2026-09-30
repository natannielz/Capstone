import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';
import { parsePaymentDraft, parsePaymentOcr, type PaymentDraft } from './payment-client';

const invalid = (): never => { throw new AccessFailure(502, 'payment_response_invalid'); };
function data<T>(value: { data?: T; error?: unknown; response: Response }): T {
  if (!value.response.ok || value.data === undefined) {
    const error = value.error && typeof value.error === 'object' ? value.error as Record<string, unknown> : {};
    throw new AccessFailure(value.response.status, typeof error.code === 'string' ? error.code : 'request_failed');
  }
  return value.data;
}
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': priceId(actorId) });
export function parsePaymentReview(value: unknown): PaymentDraft {
  const result = parsePaymentDraft(value); return result.state === 'submitted' && result.submission ? result : invalid();
}
export function parsePaymentReviewPage(value: unknown, beforeId: string | null) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return invalid();
  const page = value as Record<string, unknown>;
  if (Object.keys(page).sort().join(',') !== 'hasMore,items' || !Array.isArray(page.items) || page.items.length > 25 || typeof page.hasMore !== 'boolean') return invalid();
  const items = page.items.map(parsePaymentReview);
  if (page.hasMore && items.length !== 25 || items.some((item, index) =>
    beforeId !== null && BigInt(item.id) >= BigInt(priceId(beforeId)) || index > 0 && BigInt(item.id) >= BigInt(items[index - 1].id))) return invalid();
  return { items, hasMore: page.hasMore };
}
export function createPaymentReviewGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async list(actorId: string, beforeId: string | null, signal?: AbortSignal) {
      if (beforeId !== null) priceId(beforeId);
      return parsePaymentReviewPage(data(await client.POST('/api/v1/billing/payment-reviews/query', {
        body: { purpose: 'payment-review', beforeId }, params: { header: headers(actorId) }, signal, cache: 'no-store',
      })), beforeId);
    },
    async read(actorId: string, declarationId: string) {
      const value = parsePaymentReview(data(await client.POST('/api/v1/billing/payment-reviews/{id}/query', {
        body: { purpose: 'payment-review' }, params: { path: { id: priceId(declarationId) }, header: headers(actorId) }, cache: 'no-store',
      })));
      return value.id === declarationId ? value : invalid();
    },
    async ocr(actorId: string, owner: PaymentDraft) {
      const current = parsePaymentReview(owner);
      return parsePaymentOcr(data(await client.POST('/api/v1/billing/payment-reviews/{id}/ocr/query', {
        body: { purpose: 'payment-ocr-view', reference: current.submission!.evidence },
        params: { path: { id: current.id }, header: { ...headers(actorId), 'If-Match': '"' + current.revision + '"' } }, cache: 'no-store',
      })), current);
    },
    async original(actorId: string, owner: PaymentDraft, current: () => boolean) {
      const check = () => { if (!current()) throw new RequestNotSentError(409, 'payment_view_stale'); };
      check(); const saved = parsePaymentReview(owner), submitted = saved.submission!;
      const response = await client.POST('/api/v1/billing/payment-reviews/{id}/evidence/{reservationId}/original/query', {
        body: { purpose: 'payment-evidence-view', reference: submitted.evidence },
        params: { path: { id: saved.id, reservationId: submitted.reservationId }, header: { ...headers(actorId), 'If-Match': '"' + saved.revision + '"' } },
        parseAs: 'arrayBuffer', cache: 'no-store',
      });
      check(); const bytes = data(response), mime = response.response.headers.get('content-type');
      if (!(bytes instanceof ArrayBuffer) || bytes.byteLength <= 0 || bytes.byteLength > 10 * 1024 * 1024 ||
        response.response.headers.get('content-length') !== String(bytes.byteLength) || !['image/png', 'image/jpeg'].includes(mime ?? '')) return invalid();
      const digest = await crypto.subtle.digest('SHA-256', bytes); check();
      if (Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('') !== submitted.evidence.sha256) return invalid();
      return new Blob([bytes], { type: mime! });
    },
  };
}
