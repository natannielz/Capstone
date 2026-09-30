import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';
import { parsePaymentOcr, type PaymentDraft, type PaymentOcrView } from './payment-client';

export const correctionFields = ['amount', 'currency', 'transfer_date', 'transfer_time', 'recipient', 'account', 'bank', 'reference'] as const;
const id = z.string().refine(value => /^[1-9][0-9]{0,18}$(?![\s\S])/.test(value) && BigInt(value) <= BigInt('9223372036854775807'));
const revision = z.union([z.literal('0'), id]);
const text = (limit: number) => z.string().min(1).max(limit).refine(value => value === value.trim() && !/[\p{Cc}\p{Cf}]/u.test(value));
const timestamp = z.string().refine(value => Number.isFinite(Date.parse(value)));
const field = z.object({ field: z.enum(correctionFields), value: text(256).nullable() }).strict().refine(({ field, value }) => value === null ||
  (field === 'amount' ? /^(?:0|[1-9][0-9]{0,14})\.[0-9]{2}$(?![\s\S])/.test(value) : field === 'currency' ? value === 'IDR' :
    field === 'transfer_date' ? /^\d{4}-\d{2}-\d{2}$(?![\s\S])/.test(value) && !value.startsWith('0000') && Number.isFinite(Date.parse(value)) && new Date(value).toISOString().slice(0, 10) === value :
      field === 'transfer_time' ? /^(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]$(?![\s\S])/.test(value) : true));
const fields = z.array(field).length(8).refine(values => values.every((value, index) => value.field === correctionFields[index]));
const binding = z.object({ declarationRevision: id, submissionId: id, extractionId: id, evidence: z.object({ id, version: z.literal('1'), sha256: z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/) }).strict() }).strict();
const version = z.object({ id, revision: id, actorId: id, recordedAt: timestamp, reason: text(1000), beforeFields: fields, fields }).strict();
const view = z.object({ declarationId: id, binding, reviewRevision: revision, originalFields: fields, fields, recent: z.array(version).max(20) }).strict();
const result = z.object({ declarationId: id, submissionId: id, reviewRevision: id, correctionId: id, recordedAt: timestamp }).strict();
export type PaymentCorrectionView = z.infer<typeof view>;
export type PaymentCorrectionResult = z.infer<typeof result>;
export type PaymentCorrectionField = z.infer<typeof field>;
const invalid = (): never => { throw new AccessFailure(502, 'payment_response_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const parsed = schema.safeParse(value); return parsed.success ? parsed.data : invalid(); }
const equal = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);
export const parsePaymentCorrectionResult = (value: unknown) => parse(result, value);
export function paymentCorrectionOwner(key: string) { const match = /^c\.([1-9][0-9]{0,18})\.([0-9a-f-]{36})$(?![\s\S])/.exec(key); return match ? priceId(match[1]) : invalid(); }
export function correctionBinding(owner: PaymentDraft, ocr: PaymentOcrView) {
  const saved = parsePaymentOcr(ocr, owner); if (!saved.result) return invalid();
  return parse(binding, { declarationRevision: owner.revision, submissionId: saved.submissionId, evidence: saved.evidence, extractionId: saved.result.id });
}
export function parsePaymentCorrection(value: unknown, owner: PaymentDraft, ocr: PaymentOcrView): PaymentCorrectionView {
  const saved = parse(view, value), expected = correctionBinding(owner, ocr);
  if (saved.declarationId !== owner.id || !equal(saved.binding, expected) || !equal(saved.originalFields, ocr.result!.fields.map(({ field, value }) => ({ field, value })))) return invalid();
  if (saved.reviewRevision === '0') { if (saved.recent.length || !equal(saved.fields, saved.originalFields)) return invalid(); }
  else if (!saved.recent.length || saved.recent[0].revision !== saved.reviewRevision || !equal(saved.fields, saved.recent[0].fields) ||
    saved.recent.length !== Math.min(20, Number(BigInt(saved.reviewRevision) > BigInt(20) ? BigInt(20) : BigInt(saved.reviewRevision)))) return invalid();
  if (new Set(saved.recent.map(row => row.id)).size !== saved.recent.length || saved.recent.some((row, index) => equal(row.fields, row.beforeFields) ||
    row.revision === '1' && !equal(row.beforeFields, saved.originalFields) || index > 0 && (BigInt(row.revision) + BigInt(1) !== BigInt(saved.recent[index - 1].revision) ||
      Date.parse(row.recordedAt) > Date.parse(saved.recent[index - 1].recordedAt) || !equal(row.fields, saved.recent[index - 1].beforeFields)))) return invalid();
  return saved;
}
export function prepareCorrection(actorId: string, current: PaymentCorrectionView, values: PaymentCorrectionField[], reason: string) {
  const parsed = fields.safeParse(values), auditReason = text(1000).safeParse(reason);
  if (!parsed.success || !auditReason.success) throw new AccessFailure(400, 'invalid_payment_correction');
  const copied = parsed.data; if (equal(copied, current.fields)) throw new AccessFailure(400, 'correction_unchanged');
  return { actorId: priceId(actorId), declarationId: priceId(current.declarationId), revision: parse(revision, current.reviewRevision), body: { binding: parse(binding, current.binding), fields: copied, reason: auditReason.data } };
}
function data<T>(value: { data?: T; error?: unknown; response: Response }): T {
  if (!value.response.ok || value.data === undefined) { const error = value.error && typeof value.error === 'object' ? value.error as Record<string, unknown> : {};
    throw new AccessFailure(value.response.status, typeof error.code === 'string' ? error.code : 'request_failed'); } return value.data;
}
export function createPaymentCorrectionGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async read(actorId: string, owner: PaymentDraft, ocr: PaymentOcrView) {
      return parsePaymentCorrection(data(await client.POST('/api/v1/billing/payment-reviews/{id}/corrections/query', { body: { purpose: 'payment-ocr-view', binding: correctionBinding(owner, ocr) },
        params: { path: { id: priceId(owner.id) }, header: { 'X-Expected-Actor-Id': priceId(actorId) } }, cache: 'no-store' })), owner, ocr);
    },
    async save(command: ReturnType<typeof prepareCorrection>, key: string, current: () => boolean) {
      if (!current()) throw new RequestNotSentError(409, 'payment_view_stale');
      if (paymentCorrectionOwner(key) !== command.declarationId) return invalid();
      const saved = parsePaymentCorrectionResult(data(await client.POST('/api/v1/billing/payment-reviews/{id}/corrections', { body: command.body,
        params: { path: { id: command.declarationId }, header: { 'X-Expected-Actor-Id': command.actorId, 'Idempotency-Key': key, 'If-Match': '"' + command.revision + '"' } }, cache: 'no-store' })));
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown');
      if (saved.declarationId !== command.declarationId || saved.submissionId !== command.body.binding.submissionId || BigInt(saved.reviewRevision) !== BigInt(command.revision) + BigInt(1)) return invalid();
      return saved;
    },
    async recover(actorId: string, key: string) {
      const owner = paymentCorrectionOwner(key);
      const saved = parse(z.object({ status: z.enum(['not_confirmed', 'committed']), result: result.nullable() }).strict(), data(await client.POST('/api/v1/billing/payment-reviews/{id}/corrections/operations/query', {
        body: { purpose: 'operation-recovery', key }, params: { path: { id: owner }, header: { 'X-Expected-Actor-Id': priceId(actorId) } }, cache: 'no-store' })));
      if (saved.status === 'not_confirmed' && saved.result === null) return null;
      return saved.status === 'committed' && saved.result !== null && saved.result.declarationId === owner ? { state: 'committed' as const, result: saved.result } : invalid();
    },
  };
}
