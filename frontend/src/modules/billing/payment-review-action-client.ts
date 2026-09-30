import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';
import type { PaymentDraft } from './payment-client';

const id = z.string().refine(value => /^[1-9][0-9]{0,18}$(?![\s\S])/.test(value) && BigInt(value) <= BigInt('9223372036854775807'));
const revision = z.union([z.literal('0'), id]), hash = z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/), timestamp = z.string().datetime({ offset: true });
const text = (max: number) => z.string().min(1).max(max).refine(value => value === value.trim() && !/[\p{Cc}\p{Cf}]/u.test(value));
const action = z.enum(['claim', 'release', 'reject', 'request_replacement']), state = z.enum(['pending', 'assigned', 'rejected', 'replacement_requested']);
const result = z.object({ declarationId: id, submissionId: id, reviewRevision: id, actionId: id, action, state, assigneeId: id.nullable(), recordedAt: timestamp, operation: z.literal('billing.payment.review.manage') }).strict()
  .refine(value => value.action === 'release' ? value.state === 'pending' && value.assigneeId === null : value.assigneeId !== null && value.state === ({ claim: 'assigned', reject: 'rejected', request_replacement: 'replacement_requested' } as const)[value.action]);
const view = z.object({ binding: hash, declarationId: id, declarationRevision: id, submissionId: id, evidence: z.object({ id, version: z.literal('1'), sha256: hash }).strict(),
  reviewRevision: revision, state, assigneeId: id.nullable(), recordedBy: id.nullable(), recordedAt: timestamp.nullable(), reason: text(1000).nullable(), verified: z.boolean(), submittedAt: timestamp, observedAt: timestamp }).strict()
  .refine(value => Date.parse(value.submittedAt) <= Date.parse(value.observedAt))
  .refine(value => (value.state === 'pending') === (value.assigneeId === null) && (value.reviewRevision === '0' ? value.state === 'pending' && value.recordedBy === null && value.recordedAt === null && value.reason === null : value.recordedBy !== null && value.recordedAt !== null && value.reason !== null));
export type PaymentReviewActionView = z.infer<typeof view>;
export type PaymentReviewActionResult = z.infer<typeof result>;
const invalid = (): never => { throw new AccessFailure(502, 'payment_review_action_response_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const parsed = schema.safeParse(value); return parsed.success ? parsed.data : invalid(); }
export function reviewActionOwner(key: string) { const match = /^ra\.([1-9][0-9]{0,18})\.([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$(?![\s\S])/.exec(key); return match ? priceId(match[1]) : invalid(); }
export function parseReviewActionResult(value: unknown, key: string) { const saved = parse(result, value); return saved.declarationId === reviewActionOwner(key) ? saved : invalid(); }
export function parseReviewActionView(value: unknown, owner: PaymentDraft) {
  const saved = parse(view, value), submission = owner.submission;
  if (!submission || saved.declarationId !== owner.id || saved.declarationRevision !== owner.revision || saved.submissionId !== submission.id || saved.evidence.id !== submission.evidence.id || saved.evidence.version !== submission.evidence.version || saved.evidence.sha256 !== submission.evidence.sha256 || Date.parse(saved.submittedAt) !== Date.parse(submission.submittedAt)) return invalid();
  return saved;
}
export function prepareReviewAction(actorId: string, shown: PaymentReviewActionView, selected: string, reason: string) {
  const source = parse(view, shown), choice = parse(action, selected), audit = text(1000).safeParse(reason); actorId = priceId(actorId);
  if (!audit.success || source.verified || !['pending', 'assigned'].includes(source.state) || choice === 'release' && source.state !== 'assigned' || choice === 'claim' && source.assigneeId === actorId) throw new AccessFailure(400, 'invalid_payment_review_action');
  const command = { actorId, declarationId: source.declarationId, submissionId: source.submissionId, revision: source.reviewRevision, body: { binding: source.binding, action: choice, reason: audit.data } };
  Object.freeze(command.body); return Object.freeze(command);
}
function data<T>(value: { data?: T; error?: unknown; response: Response }): T {
  if (!value.response.ok || value.data === undefined) { const error = value.error as { code?: string }; throw new AccessFailure(value.response.status, error?.code ?? 'request_failed'); } return value.data;
}
export function createPaymentReviewActionGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async read(actorId: string, owner: PaymentDraft) { return parseReviewActionView(data(await client.POST('/api/v1/billing/payment-reviews/{id}/workflow/query', {
      body: { purpose: 'payment-review' }, params: { path: { id: owner.id }, header: { 'X-Expected-Actor-Id': priceId(actorId), 'If-Match': '"' + owner.revision + '"' } }, cache: 'no-store' })), owner); },
    async save(command: ReturnType<typeof prepareReviewAction>, key: string, current: () => boolean) {
      if (!current()) throw new RequestNotSentError(409, 'payment_view_stale'); if (reviewActionOwner(key) !== command.declarationId) return invalid();
      const saved = parseReviewActionResult(data(await client.POST('/api/v1/billing/payment-reviews/{id}/workflow', { body: command.body,
        params: { path: { id: command.declarationId }, header: { 'X-Expected-Actor-Id': command.actorId, 'Idempotency-Key': key, 'If-Match': '"' + command.revision + '"' } }, cache: 'no-store' })), key);
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown');
      if (saved.submissionId !== command.submissionId || BigInt(saved.reviewRevision) !== BigInt(command.revision) + BigInt(1) || saved.action !== command.body.action || saved.assigneeId !== (saved.action === 'release' ? null : command.actorId)) return invalid(); return saved;
    },
    async recover(actorId: string, key: string) {
      const saved = parse(z.object({ status: z.enum(['not_confirmed', 'committed']), result: result.nullable() }).strict(), data(await client.POST('/api/v1/billing/payment-reviews/{id}/workflow/operations/query', {
        body: { purpose: 'operation-recovery', key }, params: { path: { id: reviewActionOwner(key) }, header: { 'X-Expected-Actor-Id': priceId(actorId) } }, cache: 'no-store' })));
      if (saved.status === 'not_confirmed' && saved.result === null) return null;
      return saved.status === 'committed' && saved.result !== null ? { state: 'committed' as const, result: parseReviewActionResult(saved.result, key) } : invalid();
    },
  };
}
