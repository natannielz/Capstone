import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import type { PaymentDraft } from './payment-client';
import type { PaymentVerificationDetail } from './payment-verification-client';

const id = z.string().refine(value => /^[1-9][0-9]{0,18}$(?![\s\S])/.test(value) && BigInt(value) <= BigInt('9223372036854775807'));
const revision = z.union([z.literal('0'), id]).refine(value => BigInt(value) < BigInt('9223372036854775806'));
const text = (length: number) => z.string().min(1).max(length).refine(value => value === value.trim() && !/[\p{Cc}\p{Cf}]/u.test(value));
const money = z.string().regex(/^(?:0|[1-9][0-9]{0,14})\.[0-9]{2}$(?![\s\S])/);
const positive = money.refine(value => value !== '0.00');
const day = z.string().regex(/^20[0-9]{2}-[0-9]{2}-[0-9]{2}$(?![\s\S])/).refine(value => Number.isFinite(Date.parse(value)) && new Date(value).toISOString().slice(0, 10) === value);
const hash = z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/);
const cents = (value: string) => BigInt(value.replace('.', ''));
const invalid = (): never => { throw new AccessFailure(502, 'payment_allocation_response_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const result = schema.safeParse(value); return result.success ? result.data : invalid(); }
const invoice = z.object({ invoiceId: id, numberEntity: id, numberChannel: id, numberYear: z.number().int().min(2000).max(2099), number: id,
  version: revision, total: positive, paid: money, credited: money, outstanding: money, paymentState: z.enum(['unpaid', 'partial', 'paid', 'credited']),
  dueDate: day, aging: z.enum(['settled', 'overdue', 'due', 'not_due']), daysOverdue: z.number().int().nonnegative() }).strict().refine(value =>
  cents(value.paid) + cents(value.credited) + cents(value.outstanding) === cents(value.total) &&
  value.paymentState === (value.outstanding === '0.00' ? value.credited === value.total ? 'credited' : 'paid' : value.paid !== '0.00' || value.credited !== '0.00' ? 'partial' : 'unpaid') &&
  (value.aging === 'overdue' ? value.daysOverdue > 0 && value.outstanding !== '0.00' : value.daysOverdue === 0) && (value.aging === 'settled') === (value.outstanding === '0.00'));
const view = z.object({ verificationId: id, declarationId: id, receiptId: id, buyingOrganizationId: id, buyerName: text(1000), transactionId: text(80), bookedDate: day,
  receiptVersion: revision, received: positive, allocated: money, reservedForRefund: money, available: money, invoices: z.array(invoice).min(1).max(100) }).strict().refine(value =>
  new Set(value.invoices.map(item => item.invoiceId)).size === value.invoices.length && cents(value.allocated) + cents(value.reservedForRefund) + cents(value.available) === cents(value.received));
const invoiceResult = z.object({ invoiceId: id, version: id, amount: positive, remaining: money }).strict();
const lines = z.array(invoiceResult).min(1).max(100).refine(value => new Set(value.map(item => item.invoiceId)).size === value.length);
const preview = z.object({ binding: hash, view, procedureReference: text(256), amount: positive, unapplied: money, invoices: lines }).strict().refine(value =>
  cents(value.amount) + cents(value.unapplied) === cents(value.view.available) && value.invoices.reduce((sum, line) => sum + cents(line.amount), BigInt(0)) === cents(value.amount) &&
  value.invoices.length === value.view.invoices.length && value.invoices.every(line => {
    const before = value.view.invoices.find(item => item.invoiceId === line.invoiceId);
    return before && BigInt(line.version) === BigInt(before.version) + BigInt(1) && cents(line.amount) + cents(line.remaining) === cents(before.outstanding);
  }));
const result = z.object({ allocationId: id, verificationId: id, receiptId: id, receiptVersion: id, amount: positive, unapplied: money,
  recordedAt: z.string().refine(value => Number.isFinite(Date.parse(value))), operation: z.literal('billing.payment.allocate'), invoices: lines }).strict().refine(value =>
  value.invoices.reduce((sum, line) => sum + cents(line.amount), BigInt(0)) === cents(value.amount));
const input = z.object({ expectedReceiptVersion: revision, lines: z.array(z.object({ invoiceId: id, expectedVersion: revision, amount: positive }).strict()).min(1).max(100)
  .refine(value => new Set(value.map(line => line.invoiceId)).size === value.length) }).strict();
export type PaymentAllocationView = z.infer<typeof view>;
export type PaymentAllocationPreview = z.infer<typeof preview>;
export type PaymentAllocationResult = z.infer<typeof result>;
export type PaymentAllocationInput = z.infer<typeof input>;
function ownerCheck(value: PaymentAllocationView, owner: PaymentDraft, verified: PaymentVerificationDetail) {
  if (value.verificationId !== verified.result.verificationId || value.receiptId !== verified.result.receiptId || value.declarationId !== owner.id ||
    value.received !== owner.input.declaredAmount || value.invoices.some(line => !owner.invoices.some(item => item.invoiceId === line.invoiceId && item.total === line.total))) return invalid();
  return value;
}
export function paymentAllocationKey(key: string) {
  const found = /^pa\.([1-9][0-9]{0,18})\.([1-9][0-9]{0,18})\.([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$(?![\s\S])/.exec(key);
  if (!found) return invalid(); return { verificationId: parse(id, found[1]), receiptId: parse(id, found[2]) };
}
export function parsePaymentAllocationResult(value: unknown, key: string) {
  const owner = paymentAllocationKey(key), saved = parse(result, value);
  if (saved.verificationId !== owner.verificationId || saved.receiptId !== owner.receiptId) return invalid(); return saved;
}
export function preparePaymentAllocation(actorId: string, shown: PaymentAllocationPreview, reason: string) {
  const value = parse(preview, shown), audit = text(1000).safeParse(reason);
  if (!audit.success) throw new AccessFailure(400, 'invalid_payment_allocation');
  const command = { actorId: parse(id, actorId), verificationId: value.view.verificationId, receiptId: value.view.receiptId,
    body: { binding: value.binding, input: { expectedReceiptVersion: value.view.receiptVersion,
      lines: value.invoices.map(line => ({ invoiceId: line.invoiceId, expectedVersion: value.view.invoices.find(item => item.invoiceId === line.invoiceId)!.version, amount: line.amount })) }, reason: audit.data } };
  command.body.input.lines.forEach(Object.freeze); Object.freeze(command.body.input.lines); Object.freeze(command.body.input); Object.freeze(command.body); return Object.freeze(command);
}
function data<T>(value: { data?: T; error?: unknown; response: Response }): T {
  if (!value.response.ok || value.data === undefined) throw new AccessFailure(value.response.status, (value.error as { code?: string })?.code ?? 'request_failed'); return value.data;
}
export function createPaymentAllocationGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  const header = (actorId: string) => ({ 'X-Expected-Actor-Id': parse(id, actorId) });
  return {
    async read(actorId: string, owner: PaymentDraft, verified: PaymentVerificationDetail) {
      const saved = ownerCheck(parse(view, data(await client.POST('/api/v1/billing/payment-verifications/{id}/allocations/query', {
        body: { purpose: 'payment-allocation', invoiceIds: owner.invoices.map(line => line.invoiceId) }, params: { path: { id: verified.result.verificationId }, header: header(actorId) }, cache: 'no-store' }))), owner, verified);
      if (saved.invoices.length !== owner.invoices.length) return invalid(); return saved;
    },
    async preview(actorId: string, owner: PaymentDraft, verified: PaymentVerificationDetail, values: PaymentAllocationInput) {
      const request = parse(input, values), saved = parse(preview, data(await client.POST('/api/v1/billing/payment-verifications/{id}/allocations/preview', {
        body: request, params: { path: { id: verified.result.verificationId }, header: header(actorId) }, cache: 'no-store' })));
      ownerCheck(saved.view, owner, verified);
      if (saved.view.receiptVersion !== request.expectedReceiptVersion || saved.invoices.length !== request.lines.length || request.lines.some(line => {
        const before = saved.view.invoices.find(item => item.invoiceId === line.invoiceId), after = saved.invoices.find(item => item.invoiceId === line.invoiceId);
        return !before || !after || before.version !== line.expectedVersion || after.amount !== line.amount;
      })) return invalid(); return saved;
    },
    async send(command: ReturnType<typeof preparePaymentAllocation>, key: string, current: () => boolean) {
      const owner = paymentAllocationKey(key);
      if (!current() || owner.verificationId !== command.verificationId || owner.receiptId !== command.receiptId) throw new RequestNotSentError(409, 'payment_allocation_view_stale');
      const saved = data(await client.POST('/api/v1/billing/payment-verifications/{id}/allocations', { body: command.body,
        params: { path: { id: command.verificationId }, header: { ...header(command.actorId), 'Idempotency-Key': key, 'If-Match': '"' + command.body.input.expectedReceiptVersion + '"' } }, cache: 'no-store' }));
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); const checked = parsePaymentAllocationResult(saved, key);
      if (BigInt(checked.receiptVersion) !== BigInt(command.body.input.expectedReceiptVersion) + BigInt(1) || checked.invoices.length !== command.body.input.lines.length || command.body.input.lines.some(line => {
        const after = checked.invoices.find(item => item.invoiceId === line.invoiceId); return !after || after.amount !== line.amount || BigInt(after.version) !== BigInt(line.expectedVersion) + BigInt(1);
      })) return invalid(); return checked;
    },
    async recover(actorId: string, key: string) {
      const owner = paymentAllocationKey(key), saved = parse(z.object({ status: z.enum(['committed', 'not_confirmed']), result: result.nullable() }).strict(), data(await client.POST('/api/v1/billing/payment-verifications/{id}/allocations/operations/query', {
        body: { purpose: 'operation-recovery', key }, params: { path: { id: owner.verificationId }, header: header(actorId) }, cache: 'no-store' })));
      if (saved.status === 'not_confirmed' && saved.result === null) return null;
      return saved.status === 'committed' && saved.result ? { state: 'committed' as const, result: parsePaymentAllocationResult(saved.result, key) } : invalid();
    },
  };
}
