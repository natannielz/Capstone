import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { assertPaymentAllocationOwner, paymentAllocationSchemas as shared, type PaymentAllocationInput } from './payment-allocation-client';
import type { PaymentDraft } from './payment-client';
import type { PaymentVerificationDetail } from './payment-verification-client';

const { id, text, money, positive, hash, cents, lines, input, view } = shared;
const timestamp = z.string().refine(value => Number.isFinite(Date.parse(value)));
const capacities = z.array(z.object({ invoiceId: id, originallyAllocated: positive, previouslyReversed: money }).strict()
  .refine(value => cents(value.previouslyReversed) <= cents(value.originallyAllocated))).min(1).max(100)
  .refine(value => new Set(value.map(line => line.invoiceId)).size === value.length);
const correctionView = z.object({ originalAllocationId: id, originalRecordedAt: timestamp, view, capacities }).strict().refine(value =>
  value.capacities.length === value.view.invoices.length && value.capacities.every(capacity => {
    const invoice = value.view.invoices.find(line => line.invoiceId === capacity.invoiceId);
    return invoice && cents(capacity.originallyAllocated) <= cents(invoice.total);
  }) && value.capacities.reduce((sum, line) => sum + cents(line.originallyAllocated), BigInt(0)) <= cents(value.view.received));
const preview = z.object({ originalAllocationId: id, binding: hash, view, procedureReference: text(256), amount: positive, unapplied: money, invoices: lines, capacities }).strict().refine(value =>
  cents(value.unapplied) === cents(value.view.available) + cents(value.amount) && cents(value.amount) <= cents(value.view.allocated) &&
  value.invoices.reduce((sum, line) => sum + cents(line.amount), BigInt(0)) === cents(value.amount) &&
  value.capacities.reduce((sum, line) => sum + cents(line.originallyAllocated), BigInt(0)) <= cents(value.view.received) &&
  value.invoices.length === value.view.invoices.length && value.capacities.length === value.view.invoices.length && value.invoices.every(line => {
    const before = value.view.invoices.find(invoice => invoice.invoiceId === line.invoiceId), capacity = value.capacities.find(item => item.invoiceId === line.invoiceId);
    return before && capacity && BigInt(line.version) === BigInt(before.version) + BigInt(1) &&
      cents(line.remaining) === cents(before.outstanding) + cents(line.amount) && cents(line.amount) <= cents(before.paid) &&
      cents(capacity.originallyAllocated) <= cents(before.total) && cents(line.amount) <= cents(capacity.originallyAllocated) - cents(capacity.previouslyReversed);
  }));
const result = z.object({ correctionId: id, originalAllocationId: id, verificationId: id, receiptId: id, receiptVersion: id, amount: positive, unapplied: money,
  recordedAt: timestamp, operation: z.literal('billing.payment.allocation.correct'), invoices: lines }).strict().refine(value =>
  value.correctionId !== value.originalAllocationId && value.invoices.reduce((sum, line) => sum + cents(line.amount), BigInt(0)) === cents(value.amount));
const invalid = (): never => { throw new AccessFailure(502, 'payment_allocation_correction_response_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const parsed = schema.safeParse(value); return parsed.success ? parsed.data : invalid(); }
function data<T>(value: { data?: T; error?: unknown; response: Response }): T {
  if (!value.response.ok || value.data === undefined) throw new AccessFailure(value.response.status, (value.error as { code?: string })?.code ?? 'request_failed'); return value.data;
}
function verificationOwner(owner: PaymentDraft, verified: PaymentVerificationDetail) {
  if (verified.result.declarationId !== owner.id || verified.declarationRevision !== owner.revision) return invalid();
}
function ownerCheck(value: z.infer<typeof view>, owner: PaymentDraft, verified: PaymentVerificationDetail) {
  verificationOwner(owner, verified);
  return assertPaymentAllocationOwner(value, owner, verified);
}
export type PaymentAllocationCorrectionView = z.infer<typeof correctionView>;
export type PaymentAllocationCorrectionPreview = z.infer<typeof preview>;
export type PaymentAllocationCorrectionResult = z.infer<typeof result>;
export function paymentAllocationCorrectionKey(key: string) {
  const parts = /^pc\.([1-9][0-9]{0,18})\.([1-9][0-9]{0,18})\.([1-9][0-9]{0,18})\.([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$(?![\s\S])/.exec(key);
  if (!parts) return invalid(); return { verificationId: parse(id, parts[1]), receiptId: parse(id, parts[2]), originalAllocationId: parse(id, parts[3]) };
}
export function parsePaymentAllocationCorrectionResult(value: unknown, key: string) {
  const owner = paymentAllocationCorrectionKey(key), saved = parse(result, value);
  if (saved.verificationId !== owner.verificationId || saved.receiptId !== owner.receiptId || saved.originalAllocationId !== owner.originalAllocationId) return invalid(); return saved;
}
export function preparePaymentAllocationCorrection(actorId: string, shown: PaymentAllocationCorrectionPreview, reason: string) {
  const value = parse(preview, shown), audit = text(1000).safeParse(reason);
  if (!audit.success) throw new AccessFailure(400, 'invalid_payment_allocation_correction');
  const command = { actorId: parse(id, actorId), verificationId: value.view.verificationId, receiptId: value.view.receiptId, originalAllocationId: value.originalAllocationId,
    body: { binding: value.binding, input: { expectedReceiptVersion: value.view.receiptVersion, lines: value.invoices.map(line => ({ invoiceId: line.invoiceId,
      expectedVersion: value.view.invoices.find(invoice => invoice.invoiceId === line.invoiceId)!.version, amount: line.amount })) }, reason: audit.data },
    expectedResult: { amount: value.amount, unapplied: value.unapplied, invoices: value.invoices.map(line => ({ ...line })) } };
  command.body.input.lines.forEach(Object.freeze); Object.freeze(command.body.input.lines); Object.freeze(command.body.input); Object.freeze(command.body);
  command.expectedResult.invoices.forEach(Object.freeze); Object.freeze(command.expectedResult.invoices); Object.freeze(command.expectedResult); return Object.freeze(command);
}
export function createPaymentAllocationCorrectionGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  const header = (actorId: string) => ({ 'X-Expected-Actor-Id': parse(id, actorId) });
  return {
    async candidates(actorId: string, owner: PaymentDraft, verified: PaymentVerificationDetail, before: string | null = null) {
      verificationOwner(owner, verified); const cursor = before === null ? null : parse(id, before);
      const saved = parse(z.object({ items: z.array(correctionView).max(25), hasMore: z.boolean() }).strict().refine(value =>
        new Set(value.items.map(item => item.originalAllocationId)).size === value.items.length && (!value.hasMore || value.items.length === 25) &&
        value.items.every((item, index) => (cursor === null || BigInt(item.originalAllocationId) < BigInt(cursor)) &&
          (index === 0 || BigInt(item.originalAllocationId) < BigInt(value.items[index - 1].originalAllocationId)))),
      data(await client.POST('/api/v1/billing/payment-verifications/{id}/allocations/corrections/query', {
        body: { purpose: 'payment-allocation-correction', before: cursor }, params: { path: { id: parse(id, verified.result.verificationId) }, header: header(actorId) }, cache: 'no-store' })));
      saved.items.forEach(item => ownerCheck(item.view, owner, verified)); return saved;
    },
    async read(actorId: string, owner: PaymentDraft, verified: PaymentVerificationDetail, originalAllocationId: string) {
      verificationOwner(owner, verified);
      const original = parse(id, originalAllocationId), saved = parse(correctionView, data(await client.POST('/api/v1/billing/payment-allocations/{id}/corrections/query', {
        body: { purpose: 'payment-allocation-correction' }, params: { path: { id: original }, header: header(actorId) }, cache: 'no-store' })));
      ownerCheck(saved.view, owner, verified); if (saved.originalAllocationId !== original) return invalid(); return saved;
    },
    async preview(actorId: string, owner: PaymentDraft, verified: PaymentVerificationDetail, originalAllocationId: string, values: PaymentAllocationInput) {
      verificationOwner(owner, verified);
      const original = parse(id, originalAllocationId), request = parse(input, values), saved = parse(preview, data(await client.POST('/api/v1/billing/payment-allocations/{id}/corrections/preview', {
        body: request, params: { path: { id: original }, header: header(actorId) }, cache: 'no-store' })));
      ownerCheck(saved.view, owner, verified);
      if (saved.originalAllocationId !== original || saved.view.receiptVersion !== request.expectedReceiptVersion || saved.invoices.length !== request.lines.length || request.lines.some(line => {
        const before = saved.view.invoices.find(invoice => invoice.invoiceId === line.invoiceId), after = saved.invoices.find(invoice => invoice.invoiceId === line.invoiceId);
        return !before || !after || before.version !== line.expectedVersion || after.amount !== line.amount;
      })) return invalid(); return saved;
    },
    async correct(command: ReturnType<typeof preparePaymentAllocationCorrection>, key: string, current: () => boolean) {
      const owner = paymentAllocationCorrectionKey(key);
      if (!current() || owner.verificationId !== command.verificationId || owner.receiptId !== command.receiptId || owner.originalAllocationId !== command.originalAllocationId)
        throw new RequestNotSentError(409, 'payment_allocation_correction_view_stale');
      const saved = data(await client.POST('/api/v1/billing/payment-allocations/{id}/corrections', { body: command.body,
        params: { path: { id: command.originalAllocationId }, header: { ...header(command.actorId), 'Idempotency-Key': key, 'If-Match': '"' + command.body.input.expectedReceiptVersion + '"' } }, cache: 'no-store' }));
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); const checked = parsePaymentAllocationCorrectionResult(saved, key);
      if (BigInt(checked.receiptVersion) !== BigInt(command.body.input.expectedReceiptVersion) + BigInt(1) || checked.amount !== command.expectedResult.amount ||
        checked.unapplied !== command.expectedResult.unapplied || checked.invoices.length !== command.expectedResult.invoices.length || command.expectedResult.invoices.some(line => {
          const after = checked.invoices.find(invoice => invoice.invoiceId === line.invoiceId); return !after || after.version !== line.version || after.amount !== line.amount || after.remaining !== line.remaining;
        })) return invalid(); return checked;
    },
    async recover(actorId: string, key: string) {
      const owner = paymentAllocationCorrectionKey(key), saved = parse(z.object({ status: z.enum(['committed', 'not_confirmed']), result: result.nullable() }).strict(),
        data(await client.POST('/api/v1/billing/payment-allocations/{id}/corrections/operations/query', {
          body: { purpose: 'operation-recovery', key }, params: { path: { id: owner.originalAllocationId }, header: header(actorId) }, cache: 'no-store' })));
      if (saved.status === 'not_confirmed' && saved.result === null) return null;
      return saved.status === 'committed' && saved.result ? { state: 'committed' as const, result: parsePaymentAllocationCorrectionResult(saved.result, key) } : invalid();
    },
  };
}
