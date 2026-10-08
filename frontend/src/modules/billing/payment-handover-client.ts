import {z} from 'zod';
import {fetchCsrfToken, RequestNotSentError} from '../../api/client';
import {AccessFailure, accessMessage} from '../administration/workflow';
import {PaymentHandoverStorageError, parsePaymentHandoverReference, type PaymentHandoverReference} from './payment-handover-journal';
import type {PaymentDraft} from './payment-client';

/** Transport uncertainty is not a diagnosis about the durable operation reference. */
export function paymentHandoverMessage(error: unknown): string {
  if (error instanceof PaymentHandoverStorageError) return {
    read: 'Referensi pemulihan belum dapat dibaca. Periksa penyimpanan situs; jangan hapus referensi atau kirim perubahan baru sebelum hasilnya jelas.',
    write: 'Referensi pemulihan belum dapat disimpan. Permintaan baru belum dikirim. Periksa penyimpanan situs tanpa menghapus referensi yang belum terkonfirmasi.',
    remove: 'Referensi pemulihan belum dapat dibersihkan. Jangan kirim perubahan baru; periksa hasil memakai kunci asli setelah penyimpanan dapat digunakan.',
  }[error.operation];
  return accessMessage(error);
}

const id = z.string().regex(/^[1-9][0-9]{0,18}$(?![\s\S])/).refine(v => /^[1-9][0-9]{0,18}$(?![\s\S])/.test(v) && BigInt(v) <= BigInt('9223372036854775807'));
const revision = z.union([id, z.literal('0')]), at = z.string().datetime({offset: true}), hash = z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/);
const text = z.string().min(1).max(1000).refine(v => v === v.trim() && !/[\p{Cc}\p{Cf}]/u.test(v));
const selection = z.object({delegationId: id, delegationRevision: id, delegationEventId: id, originalAssigneeId: id, declarationRevision: id}).strict();
const choice = z.object({delegationId: id, delegationRevision: id, delegationEventId: id, state: z.enum(['active', 'expired', 'revoked', 'not_yet_effective']), expiresAt: at}).strict();
const page = z.object({declarationId: id, declarationRevision: id, submissionId: id, binding: hash, reviewRevision: revision,
  assigneeId: id.nullable(), open: z.boolean(), canClaim: z.boolean(), items: z.array(choice).max(25), nextId: id.nullable(), observedAt: at}).strict()
  .refine(v => (!v.open || v.assigneeId !== null && v.reviewRevision !== '0') && (!v.canClaim || v.open) && (v.open || v.items.length === 0))
  .refine(v => v.items.every((item, i) => i === 0 || BigInt(item.delegationId) < BigInt(v.items[i - 1].delegationId)) &&
    (v.nextId === null || v.items.length === 25 && v.nextId === v.items[24].delegationId));
const result = z.object({declarationId: id, submissionId: id, reviewRevision: id, actionId: id, action: z.literal('claim'), state: z.literal('assigned'),
  assigneeId: id, recordedAt: at, operation: z.literal('billing.payment.review.manage'), handover: selection}).strict();
export type PaymentHandoverPage = z.infer<typeof page>;
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const out = schema.safeParse(value); if (!out.success) throw new AccessFailure(502, 'payment_handover_response_invalid'); return out.data; }
export function parsePaymentHandoverPage(value: unknown, owner: Pick<PaymentDraft, 'id' | 'revision' | 'submission'>) {
  const out = parse(page, value);
  if (!owner.submission || out.declarationId !== owner.id || out.declarationRevision !== owner.revision || out.submissionId !== owner.submission.id) throw new AccessFailure(502, 'payment_handover_response_invalid'); return out;
}
export function preparePaymentHandover(actorId: string, shown: PaymentHandoverPage, selected: string, reason: string) {
  const current = parse(page, shown), item = current.items.find(item => item.delegationId === selected), audit = text.safeParse(reason);
  if (!id.safeParse(actorId).success || !current.canClaim || !item || current.assigneeId === actorId || !current.assigneeId || !audit.success) throw new RequestNotSentError(400, 'invalid_payment_handover');
  const handover = Object.freeze({delegationId: item.delegationId, delegationRevision: item.delegationRevision, delegationEventId: item.delegationEventId,
    originalAssigneeId: current.assigneeId, declarationRevision: current.declarationRevision});
  return Object.freeze({actorId, declarationId: current.declarationId, submissionId: current.submissionId, reviewRevision: current.reviewRevision,
    handover, body: Object.freeze({binding: current.binding, handover, reason: audit.data})});
}
export type PaymentHandoverCommand = ReturnType<typeof preparePaymentHandover>;
export function parsePaymentHandoverResult(value: unknown, reference: PaymentHandoverReference) {
  const ref = parsePaymentHandoverReference(reference, reference.actorId), out = parse(result, value);
  if (out.declarationId !== ref.declarationId || out.submissionId !== ref.submissionId || out.assigneeId !== ref.actorId ||
    BigInt(out.reviewRevision) !== BigInt(ref.reviewRevision) + BigInt(1) || Object.entries(out.handover).some(([name, value]) => value !== ref[name as keyof PaymentHandoverReference]))
    throw new AccessFailure(502, 'payment_handover_response_invalid'); return out;
}
export type HandoverContext = {actorId: string; isCurrent: () => boolean};
export function createPaymentHandoverGateway(fetcher: typeof globalThis.fetch = globalThis.fetch, csrfUrl = '/api/v1/identity/csrf') {
  async function call(declarationId: string, suffix: string, body: unknown, context: HandoverContext, ref?: PaymentHandoverReference) {
    const current = (sent = false) => { if (!context.isCurrent()) { if (sent && ref) throw new AccessFailure(503, 'payment_handover_outcome_unknown'); throw new RequestNotSentError(409, 'payment_handover_stale'); } };
    current(); if (!id.safeParse(context.actorId).success || !id.safeParse(declarationId).success) throw new RequestNotSentError(400, 'invalid_payment_handover');
    const headers: Record<string, string> = {'Content-Type': 'application/json', Accept: 'application/json', 'X-Expected-Actor-Id': context.actorId};
    if (ref) { parsePaymentHandoverReference(ref, context.actorId); headers['Idempotency-Key'] = ref.key; headers['If-Match'] = `"${ref.reviewRevision}"`; }
    const controller = new AbortController(), timer = setTimeout(() => controller.abort(), 30_000);
    try {
      headers['X-CSRF-Token'] = await fetchCsrfToken(csrfUrl, fetcher, controller.signal); current();
      const response = await fetcher('/api/v1/billing/payment-reviews/' + declarationId + '/handover' + suffix, {method: 'POST', headers, body: JSON.stringify(body),
        credentials: 'same-origin', redirect: 'error', cache: 'no-store', signal: controller.signal});
      const value: unknown = await response.json(); current(true);
      if (!response.ok) throw new AccessFailure(response.status, typeof (value as {code?: unknown})?.code === 'string' ? (value as {code: string}).code : 'payment_handover_unavailable'); return value;
    } finally {clearTimeout(timer);}
  }
  return {
    async read(owner: Pick<PaymentDraft, 'id' | 'revision' | 'submission'>, context: HandoverContext, beforeId: string | null = null) {
      if (beforeId !== null && !id.safeParse(beforeId).success) throw new RequestNotSentError(400, 'invalid_payment_handover');
      return parsePaymentHandoverPage(await call(owner.id, '/query', {purpose: 'payment-handover', beforeId}, context), owner);
    },
    async claim(command: PaymentHandoverCommand, reference: PaymentHandoverReference, context: HandoverContext) {
      const ref = parsePaymentHandoverReference(reference, context.actorId);
      if (command.actorId !== ref.actorId || command.declarationId !== ref.declarationId || command.submissionId !== ref.submissionId || command.reviewRevision !== ref.reviewRevision ||
        !hash.safeParse(command.body.binding).success || !text.safeParse(command.body.reason).success || !selection.safeParse(command.body.handover).success ||
        Object.entries(command.handover).some(([name, value]) => value !== ref[name as keyof PaymentHandoverReference] || value !== command.body.handover[name as keyof typeof command.handover])) throw new RequestNotSentError(400, 'invalid_payment_handover');
      return parsePaymentHandoverResult(await call(ref.declarationId, '', command.body, context, ref), ref);
    },
    async recover(reference: PaymentHandoverReference, context: HandoverContext) {
      const ref = parsePaymentHandoverReference(reference, context.actorId), recovered = parse(z.object({state: z.enum(['committed', 'not_confirmed']), result: result.nullable()}).strict()
        .refine(v => (v.state === 'committed') === (v.result !== null)), await call(ref.declarationId, '/operations/query', {purpose: 'operation-recovery', key: ref.key}, context));
      return recovered.result === null ? null : parsePaymentHandoverResult(recovered.result, ref);
    },
  };
}
