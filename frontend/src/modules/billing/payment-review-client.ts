import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { z } from 'zod';
import { AccessFailure } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';
import { parsePaymentDraft, parsePaymentOcr, paymentNotificationStatus, type PaymentDraft } from './payment-client';

const invalid = (): never => { throw new AccessFailure(502, 'payment_response_invalid'); };
function data<T>(value: { data?: T; error?: unknown; response: Response }): T {
  if (!value.response.ok || value.data === undefined) {
    const error = value.error && typeof value.error === 'object' ? value.error as Record<string, unknown> : {};
    throw new AccessFailure(value.response.status, typeof error.code === 'string' ? error.code : 'request_failed');
  }
  return value.data;
}
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': priceId(actorId) });
const queueId = z.string().regex(/^[1-9][0-9]{0,18}$(?![\s\S])/).refine(value => BigInt(value) <= BigInt('9223372036854775807'));
const queueTimestamp = z.string().datetime({ offset: true });
const queueEntry = z.object({ declaration: z.unknown(), reviewRevision: z.union([z.literal('0'), queueId]), state: z.enum(['pending', 'assigned', 'rejected', 'replacement_requested']),
  assigneeId: queueId.nullable(), recordedAt: queueTimestamp.nullable(), ocrState: z.enum(['queued', 'running', 'retry', 'dead', 'succeeded']),
  discrepancies: z.array(z.enum(['amount_unknown', 'amount_differs', 'date_unknown', 'date_differs', 'currency_unknown', 'ocr_quality', 'human_corrected'])).max(7), notification: paymentNotificationStatus,
}).strict().refine(row => (row.state === 'pending') === (row.assigneeId === null) && (row.reviewRevision === '0') === (row.recordedAt === null) &&
  (row.reviewRevision !== '0' || row.state === 'pending') && new Set(row.discrepancies).size === row.discrepancies.length && (row.ocrState === 'succeeded' || row.discrepancies.length === 0) &&
  ['rejected', 'replacement_requested'].includes(row.state) === (row.notification.state !== 'not_applicable'));
export type PaymentReviewQueueEntry = Omit<z.infer<typeof queueEntry>, 'declaration'> & { declaration: PaymentDraft };
export function parsePaymentReviewQueue(value: unknown, beforeId: string | null) {
  const parsed = z.object({ items: z.array(queueEntry).max(25), hasMore: z.boolean(), observedAt: queueTimestamp }).strict().safeParse(value); if (!parsed.success) return invalid();
  const { observedAt, hasMore } = parsed.data;
  const items: PaymentReviewQueueEntry[] = parsed.data.items.map(row => {
    const declaration = parsePaymentReview(row.declaration), submittedAt = Date.parse(declaration.submission!.submittedAt);
    if (submittedAt > Date.parse(observedAt) || row.recordedAt !== null && (Date.parse(row.recordedAt) < submittedAt || Date.parse(row.recordedAt) > Date.parse(observedAt)) ||
      row.notification.availableAt !== null && (row.recordedAt === null || Date.parse(row.notification.availableAt) < Date.parse(row.recordedAt) || Date.parse(row.notification.availableAt) > Date.parse(observedAt))) return invalid();
    return { ...row, declaration };
  });
  parsePaymentReviewPage({ items: items.map(row => row.declaration), hasMore }, beforeId);
  return { items, hasMore, observedAt };
}
export type PaymentDuplicatePage = { declarationId: string; revision: string; submissionId: string; items: { declarationId: string; revision: string; submissionId: string; submittedAt: string; signals: ('same_image' | 'same_reference')[] }[]; hasMore: boolean };
export function parsePaymentDuplicates(value: unknown, owner: PaymentDraft, beforeId: string | null): PaymentDuplicatePage {
  const current = parsePaymentReview(owner);
  if (!value || typeof value !== 'object' || Array.isArray(value)) return invalid();
  const page = value as Record<string, unknown>;
  if (Object.keys(page).sort().join(',') !== 'declarationId,hasMore,items,revision,submissionId' || page.declarationId !== current.id || page.revision !== current.revision ||
    page.submissionId !== current.submission!.id || !Array.isArray(page.items) || page.items.length > 25 || typeof page.hasMore !== 'boolean' || page.hasMore && page.items.length !== 25) return invalid();
  const sourceItems = page.items;
  const items = sourceItems.map((value, index) => {
    if (!value || typeof value !== 'object' || Array.isArray(value)) return invalid();
    const item = value as Record<string, unknown>;
    if (Object.keys(item).sort().join(',') !== 'declarationId,revision,signals,submissionId,submittedAt' || typeof item.declarationId !== 'string' || typeof item.revision !== 'string' || typeof item.submissionId !== 'string' ||
      typeof item.submittedAt !== 'string' || !/^\d{4}-\d{2}-\d{2}T/.test(item.submittedAt) || !Number.isFinite(Date.parse(item.submittedAt)) ||
      !Array.isArray(item.signals) || item.signals.length < 1 || item.signals.length > 2 || item.signals.some(signal => signal !== 'same_image' && signal !== 'same_reference') ||
      new Set(item.signals).size !== item.signals.length) return invalid();
    priceId(item.declarationId); priceId(item.revision); priceId(item.submissionId);
    if (item.declarationId === current.id || beforeId !== null && BigInt(item.declarationId) >= BigInt(priceId(beforeId)) || index > 0 && BigInt(item.declarationId) >= BigInt(sourceItems[index - 1].declarationId)) return invalid();
    return item as PaymentDuplicatePage['items'][number];
  });
  return { declarationId: current.id, revision: current.revision, submissionId: current.submission!.id, items, hasMore: page.hasMore };
}
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
    async duplicates(actorId: string, owner: PaymentDraft, beforeId: string | null) {
      const saved = parsePaymentReview(owner); if (beforeId !== null) priceId(beforeId);
      return parsePaymentDuplicates(data(await client.POST('/api/v1/billing/payment-reviews/{id}/duplicates/query', {
        body: { purpose: 'payment-review', reference: saved.submission!.evidence, beforeId },
        params: { path: { id: saved.id }, header: { ...headers(actorId), 'If-Match': '"' + saved.revision + '"' } }, cache: 'no-store',
      })), saved, beforeId);
    },
    async list(actorId: string, beforeId: string | null, signal?: AbortSignal) {
      if (beforeId !== null) priceId(beforeId);
      return parsePaymentReviewPage(data(await client.POST('/api/v1/billing/payment-reviews/query', {
        body: { purpose: 'payment-review', beforeId }, params: { header: headers(actorId) }, signal, cache: 'no-store',
      })), beforeId);
    },
    async queue(actorId: string, beforeId: string | null, signal?: AbortSignal) {
      if (beforeId !== null) priceId(beforeId);
      return parsePaymentReviewQueue(data(await client.POST('/api/v1/billing/payment-reviews/queue/query', {
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
