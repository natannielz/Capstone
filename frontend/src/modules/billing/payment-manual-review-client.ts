import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';
import { correctionValues, type PaymentCorrectionField } from './payment-correction-client';
import type { PaymentDraft } from './payment-client';

const id = z.string().refine(value => /^[1-9][0-9]{0,18}$(?![\s\S])/.test(value) && BigInt(value) <= BigInt('9223372036854775807'));
const revision = z.union([z.literal('0'), id]);
const hash = z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/);
const timestamp = z.string().refine(value => Number.isFinite(Date.parse(value)));
const text = (max: number) => z.string().min(1).max(max).refine(value => value === value.trim() && !/[\p{Cc}\p{Cf}]/u.test(value));
const view = z.object({ binding: hash, declarationId: id, declarationRevision: id, submissionId: id, evidence: z.object({ id, version: z.literal('1'), sha256: hash }).strict(),
  reviewRevision: revision, failureId: id, failureCode: z.enum(['ocr_unavailable', 'ocr_timeout', 'ocr_invalid_output']), failedAt: timestamp,
  procedureReference: text(256), fields: correctionValues, reviewedBy: id.nullable(), recordedAt: timestamp.nullable() }).strict();
const result = z.object({ declarationId: id, submissionId: id, reviewRevision: id, manualReviewId: id, recordedAt: timestamp }).strict();
export type PaymentManualReviewView = z.infer<typeof view>;
export type PaymentManualReviewResult = z.infer<typeof result>;
const invalid = (): never => { throw new AccessFailure(502, 'payment_manual_review_response_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const parsed = schema.safeParse(value); return parsed.success ? parsed.data : invalid(); }
export function manualReviewOwner(key: string) { const match = /^mr\.([1-9][0-9]{0,18})\.([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$(?![\s\S])/.exec(key); return match ? priceId(match[1]) : invalid(); }
export function parseManualReviewResult(value: unknown, key: string) { const saved = parse(result, value); if (saved.declarationId !== manualReviewOwner(key)) return invalid(); return saved; }
export function parseManualReview(value: unknown, owner: PaymentDraft) {
  const saved = parse(view, value), submission = owner.submission;
  if (!submission || saved.declarationId !== owner.id || saved.declarationRevision !== owner.revision || saved.submissionId !== submission.id ||
    saved.evidence.id !== submission.evidence.id || saved.evidence.version !== submission.evidence.version || saved.evidence.sha256 !== submission.evidence.sha256 ||
    (saved.reviewRevision === '0' ? saved.reviewedBy !== null || saved.recordedAt !== null || saved.fields.some(f => f.value !== null) : saved.reviewedBy === null || saved.recordedAt === null)) return invalid();
  return saved;
}
export function prepareManualReview(actorId: string, shown: PaymentManualReviewView, values: PaymentCorrectionField[], imageReviewed: boolean, reason: string) {
  const parsed = correctionValues.safeParse(values), audit = text(1000).safeParse(reason);
  if (!imageReviewed || !parsed.success || !audit.success) throw new AccessFailure(400, 'invalid_payment_manual_review');
  const command = { actorId: priceId(actorId), declarationId: priceId(shown.declarationId), submissionId: priceId(shown.submissionId), revision: parse(revision, shown.reviewRevision),
    body: { binding: parse(hash, shown.binding), imageReviewed: true, fields: parsed.data, reason: audit.data } };
  command.body.fields.forEach(Object.freeze); Object.freeze(command.body.fields); Object.freeze(command.body); return Object.freeze(command);
}
function data<T>(value: { data?: T; error?: unknown; response: Response }): T {
  if (!value.response.ok || value.data === undefined) { const error = value.error as { code?: string }; throw new AccessFailure(value.response.status, error?.code ?? 'request_failed'); } return value.data;
}
export function createPaymentManualReviewGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async read(actorId: string, owner: PaymentDraft) { return parseManualReview(data(await client.POST('/api/v1/billing/payment-reviews/{id}/manual-review/query', {
      body: { purpose: 'payment-review' }, params: { path: { id: owner.id }, header: { 'X-Expected-Actor-Id': priceId(actorId), 'If-Match': '"' + owner.revision + '"' } }, cache: 'no-store' })), owner); },
    async save(command: ReturnType<typeof prepareManualReview>, key: string, current: () => boolean) {
      if (!current()) throw new RequestNotSentError(409, 'payment_view_stale');
      if (manualReviewOwner(key) !== command.declarationId) return invalid();
      const saved = parseManualReviewResult(data(await client.POST('/api/v1/billing/payment-reviews/{id}/manual-review', { body: command.body,
        params: { path: { id: command.declarationId }, header: { 'X-Expected-Actor-Id': command.actorId, 'Idempotency-Key': key, 'If-Match': '"' + command.revision + '"' } }, cache: 'no-store' })), key);
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown');
      if (saved.submissionId !== command.submissionId || BigInt(saved.reviewRevision) !== BigInt(command.revision) + BigInt(1)) return invalid(); return saved;
    },
    async recover(actorId: string, key: string) {
      const saved = parse(z.object({ status: z.enum(['not_confirmed', 'committed']), result: result.nullable() }).strict(), data(await client.POST('/api/v1/billing/payment-reviews/{id}/manual-review/operations/query', {
        body: { purpose: 'operation-recovery', key }, params: { path: { id: manualReviewOwner(key) }, header: { 'X-Expected-Actor-Id': priceId(actorId) } }, cache: 'no-store' })));
      if (saved.status === 'not_confirmed' && saved.result === null) return null;
      return saved.status === 'committed' && saved.result !== null ? { state: 'committed' as const, result: parseManualReviewResult(saved.result, key) } : invalid();
    },
  };
}
