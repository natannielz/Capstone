import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { correctionFields } from './payment-correction-client';
import type { PaymentDraft } from './payment-client';

const id = z.string().refine(value => /^[1-9][0-9]{0,18}$(?![\s\S])/.test(value) && BigInt(value) <= BigInt('9223372036854775807'));
const revision = z.union([z.literal('0'), id]);
const text = (length: number) => z.string().min(1).max(length).refine(value => value === value.trim() && !/[\p{Cc}\p{Cf}]/u.test(value));
const hash = z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/);
const amount = z.string().regex(/^(?:0|[1-9][0-9]{0,14})\.[0-9]{2}$(?![\s\S])/).refine(value => value !== '0.00');
const timestamp = z.string().refine(value => Number.isFinite(Date.parse(value)));
const day = z.string().regex(/^20[0-9]{2}-[0-9]{2}-[0-9]{2}$(?![\s\S])/).refine(value => Number.isFinite(Date.parse(value)) && new Date(value).toISOString().slice(0, 10) === value);
const field = z.object({ field: z.enum(correctionFields), value: text(256).nullable() }).strict();
const resolution = z.object({ field: text(40), reason: text(1000) }).strict();
const discrepancy = z.object({ field: z.enum(['transfer_date', 'recipient', 'bank', 'reference', 'transfer_time']), declaration: text(256).nullable(), image: text(256).nullable(), bank: text(256).nullable() }).strict();
const result = z.object({ verificationId: id, declarationId: id, receiptId: id, reviewRevision: revision, recordedAt: timestamp, operation: z.literal('billing.payment.verify') }).strict();
const preview = z.object({ binding: hash, declarationId: id, declarationRevision: id, receiptId: id, reviewRevision: revision, buyingOrganizationId: id,
  buyerName: text(1000), declaredPayerName: text(160), amount, bookedDate: day, transactionId: text(80), accountNumber: z.string().regex(/^[0-9]{4,34}$(?![\s\S])/),
  accountHolder: text(160), bankCode: text(32), payerVerificationProcedureReference: text(256), fields: z.array(field).length(8).refine(values => values.every((item, index) => item.field === correctionFields[index])),
  discrepancies: z.array(discrepancy).max(5).refine(values => new Set(values.map(item => item.field)).size === values.length), reviewSource: z.enum(['ocr', 'manual']) }).strict();
const detail = z.object({ result, declarationRevision: id, evidence: z.object({ id, version: z.literal('1'), sha256: hash }).strict(), verifiedBy: id,
  payerVerificationReference: text(256), reason: text(1000), resolutions: z.array(resolution).max(8), reviewSource: z.enum(['ocr', 'manual']) }).strict();
export type PaymentVerificationPreview = z.infer<typeof preview>;
export type PaymentVerificationResult = z.infer<typeof result>;
export type PaymentVerificationDetail = z.infer<typeof detail>;
const invalid = (): never => { throw new AccessFailure(502, 'payment_verification_response_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const parsed = schema.safeParse(value); return parsed.success ? parsed.data : invalid(); }
export function paymentVerificationKey(key: string) {
  const parts = /^pv\.([1-9][0-9]{0,18})\.([1-9][0-9]{0,18})\.([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$(?![\s\S])/.exec(key);
  if (!parts) return invalid(); return { declarationId: parse(id, parts[1]), receiptId: parse(id, parts[2]) };
}
export function parsePaymentVerificationResult(value: unknown, key: string) {
  const owner = paymentVerificationKey(key), saved = parse(result, value);
  if (saved.declarationId !== owner.declarationId || saved.receiptId !== owner.receiptId) return invalid(); return saved;
}
export function preparePaymentVerification(actorId: string, shown: PaymentVerificationPreview, payerVerificationReference: string,
  imageReviewed: boolean, bankReviewed: boolean, payerVerified: boolean, resolutions: { field: string; reason: string }[], reason: string) {
  const view = parse(preview, shown), reviewed = z.array(resolution).max(8).safeParse(resolutions), audit = text(1000).safeParse(reason), payer = text(256).safeParse(payerVerificationReference);
  if (!reviewed.success || !audit.success || !payer.success || !imageReviewed || !bankReviewed || !payerVerified ||
    view.discrepancies.map(item => item.field).sort().join(',') !== reviewed.data.map(item => item.field).sort().join(',')) throw new AccessFailure(400, 'invalid_payment_verification');
  const command = { actorId: parse(id, actorId), declarationId: view.declarationId, body: { receiptId: view.receiptId, binding: view.binding, payerVerificationReference: payer.data,
    imageReviewed: true, bankReviewed: true, payerVerified: true, resolutions: reviewed.data.map(item => ({ ...item })), reason: audit.data } };
  command.body.resolutions.forEach(Object.freeze); Object.freeze(command.body.resolutions); Object.freeze(command.body); Object.freeze(command); return command;
}
function data<T>(value: { data?: T; error?: unknown; response: Response }): T {
  if (!value.response.ok || value.data === undefined) { const error = value.error as { code?: string }; throw new AccessFailure(value.response.status, error?.code ?? 'request_failed'); } return value.data;
}
export function createPaymentVerificationGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  const header = (actorId: string) => ({ 'X-Expected-Actor-Id': parse(id, actorId) });
  return {
    async read(actorId: string, owner: PaymentDraft) {
      const saved = parse(z.object({ state: z.enum(['not_verified', 'verified']), verification: detail.nullable() }).strict(), data(await client.POST('/api/v1/billing/payment-reviews/{id}/verification/query',
        { body: { purpose: 'payment-review' }, params: { path: { id: owner.id }, header: header(actorId) }, cache: 'no-store' })));
      if (saved.state === 'not_verified' && saved.verification === null) return null;
      const value = saved.verification;
      if (saved.state !== 'verified' || !value || value.result.declarationId !== owner.id || value.declarationRevision !== owner.revision || !owner.submission ||
        value.evidence.id !== owner.submission.evidence.id || value.evidence.version !== owner.submission.evidence.version ||
        value.evidence.sha256 !== owner.submission.evidence.sha256) return invalid(); return value;
    },
    async preview(actorId: string, owner: PaymentDraft, receiptId: string) {
      const saved = parse(preview, data(await client.POST('/api/v1/billing/payment-reviews/{id}/verification/preview', { body: { receiptId: parse(id, receiptId) },
        params: { path: { id: owner.id }, header: header(actorId) }, cache: 'no-store' })));
      if (saved.declarationId !== owner.id || saved.declarationRevision !== owner.revision || saved.receiptId !== receiptId || saved.amount !== owner.input.declaredAmount || saved.declaredPayerName !== owner.input.payerName) return invalid(); return saved;
    },
    async send(command: ReturnType<typeof preparePaymentVerification>, key: string, current: () => boolean) {
      if (!current()) throw new RequestNotSentError(409, 'payment_verification_view_stale'); const owner = paymentVerificationKey(key);
      if (owner.declarationId !== command.declarationId || owner.receiptId !== command.body.receiptId) throw new RequestNotSentError(409, 'payment_verification_view_stale');
      const saved = data(await client.POST('/api/v1/billing/payment-reviews/{id}/verification', { body: command.body, params: { path: { id: command.declarationId },
        header: { ...header(command.actorId), 'Idempotency-Key': key, 'If-Match': '"0"' } }, cache: 'no-store' }));
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); return parsePaymentVerificationResult(saved, key);
    },
    async recover(actorId: string, key: string) {
      const owner = paymentVerificationKey(key), saved = parse(z.object({ status: z.enum(['committed', 'not_confirmed']), result: result.nullable() }).strict(), data(await client.POST('/api/v1/billing/payment-reviews/{id}/verification/operations/query',
        { body: { purpose: 'operation-recovery', key }, params: { path: { id: owner.declarationId }, header: header(actorId) }, cache: 'no-store' })));
      if (saved.status === 'not_confirmed' && saved.result === null) return null;
      return saved.status === 'committed' && saved.result ? { state: 'committed' as const, result: parsePaymentVerificationResult(saved.result, key) } : invalid();
    },
  };
}
