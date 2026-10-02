import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { paymentAllocationSchemas } from '../billing/payment-allocation-client';
import { parseCaseView, type AfterSalesCase } from './case-client';

const { id, money, positive, hash, text, cents } = paymentAllocationSchemas;
const version = id.refine(value => BigInt(value) < BigInt('9223372036854775806'));
const day = z.string().regex(/^[2-9][0-9]{3}-[0-9]{2}-[0-9]{2}$(?![\s\S])/).refine(value => Number.isFinite(Date.parse(value)) && new Date(value).toISOString().slice(0, 10) === value);
const timestamp = z.string().datetime({ offset: true }), suffix = z.string().regex(/^[0-9]{4}$(?![\s\S])/);
const reference = z.object({ id, version: z.literal('1'), sha256: hash }).strict();
const payout = z.object({ payoutId: id, draftId: id, approvalId: id, state: z.literal('recorded_unverified'), amount: positive, recordedAt: timestamp }).strict();
const input = z.object({ bankImportId: id, bankRowNumber: z.number().int().min(2).max(501), beneficiaryVerificationReference: text(256), transferVerificationReference: text(256) }).strict();
const receipt = z.object({ receiptId: id, version, paid: positive, pending: money, reservedForRefund: positive, available: money }).strict()
  .refine(value => cents(value.paid) + cents(value.pending) === cents(value.reservedForRefund));
const uniqueSorted = (values: readonly { receiptId: string }[]) => values.every((value, index) => index === 0 || BigInt(value.receiptId) > BigInt(values[index - 1].receiptId));
const result = z.object({ settlementId: id, payoutId: id, financialApprovalId: id, caseId: id, returnId: id, invoiceId: id, amount: positive,
  operation: z.literal('billing.refund.payout.verify'), state: z.literal('bank_debit_verified'), recordedAt: timestamp, receipts: z.array(receipt).min(1).max(100) }).strict()
  .refine(value => uniqueSorted(value.receipts) && value.receipts.reduce((sum, row) => sum + cents(row.paid), BigInt(0)) >= cents(value.amount));
export const refundSettlementResultSchema = result;
const view = z.object({ payout, caseId: id, returnId: id, invoiceId: id, amount: positive, draftRevision: z.literal('1'), draftId: id, reservationId: id, evidence: reference,
  settlement: result.nullable(), candidateInstructions: text(1000), beneficiaryName: text(160), beneficiaryBankCode: text(80), beneficiaryAccountSuffix: suffix,
  payingBankCode: text(80), payingAccountSuffix: suffix, transferDate: day, transferReference: text(160) }).strict().refine(value =>
  value.payout.amount === value.amount && value.payout.draftId === value.draftId &&
  (value.settlement === null || value.settlement.payoutId === value.payout.payoutId && value.settlement.financialApprovalId === value.payout.approvalId &&
    value.settlement.caseId === value.caseId && value.settlement.returnId === value.returnId && value.settlement.invoiceId === value.invoiceId &&
    value.settlement.amount === value.amount && Date.parse(value.settlement.recordedAt) >= Date.parse(value.payout.recordedAt)));
const receiptPreview = z.object({ receiptId: id, versionBefore: version, versionAfter: version, paidBefore: money, paidAfter: positive, pending: money,
  reservedForRefund: positive, available: money, received: positive, allocated: money }).strict().refine(value =>
  BigInt(value.versionAfter) === BigInt(value.versionBefore) + BigInt(1) && cents(value.paidAfter) > cents(value.paidBefore) &&
  cents(value.paidAfter) + cents(value.pending) === cents(value.reservedForRefund) &&
  cents(value.allocated) + cents(value.reservedForRefund) + cents(value.available) === cents(value.received));
const preview = z.object({ binding: hash, view, input, debit: z.object({ bankImportId: id, bankRowNumber: z.number().int().min(2).max(501),
  transactionId: text(80), bookedDate: day, amount: positive, accountDisplay: suffix, importedBy: id }).strict(),
  policy: z.object({ bankDebitVerificationProcedureReference: text(256), beneficiaryVerificationProcedureReference: text(256) }).strict(),
  receipts: z.array(receiptPreview).min(1).max(100) }).strict().refine(value => value.view.settlement === null && uniqueSorted(value.receipts) &&
  value.debit.bankImportId === value.input.bankImportId && value.debit.bankRowNumber === value.input.bankRowNumber && value.debit.amount === value.view.amount &&
  value.debit.accountDisplay === value.view.payingAccountSuffix && value.debit.bookedDate >= value.view.transferDate &&
  value.receipts.reduce((sum, row) => sum + cents(row.paidAfter) - cents(row.paidBefore), BigInt(0)) === cents(value.view.amount));
export type RefundSettlementInput = z.infer<typeof input>;
export type RefundSettlementResult = z.infer<typeof result>;
export type RefundSettlementView = z.infer<typeof view>;
export type RefundSettlementPreview = z.infer<typeof preview>;
const invalid = (): never => { throw new AccessFailure(502, 'refund_settlement_response_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const checked = schema.safeParse(value); return checked.success ? checked.data : invalid(); }
function data<T>(response: { data?: T; error?: unknown; response: Response }): T {
  if (!response.response.ok || response.data === undefined) throw new AccessFailure(response.response.status, (response.error as { code?: string })?.code ?? 'request_failed');
  return response.data;
}
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': parse(id, actorId) });
const check = (current: () => boolean) => { if (!current()) throw new RequestNotSentError(409, 'refund_settlement_view_stale'); };
export function canVerifyRefundSettlement(actions: readonly string[]) { return ['disbursement.verify', 'case.read', 'invoice.read', 'payment.review.read'].every(action => actions.includes(action)); }
export function refundSettlementOwner(key: string) {
  const parts = /^rsv\.([1-9][0-9]{0,18})\.([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$(?![\s\S])/.exec(key);
  if (!parts || parts[2] === '00000000-0000-0000-0000-000000000000') return invalid(); return parse(id, parts[1]);
}
export function parseRefundSettlementResult(value: unknown, key: string) { const checked = parse(result, value); return checked.payoutId === refundSettlementOwner(key) ? checked : invalid(); }
export function parseRefundSettlementView(value: unknown, owner: AfterSalesCase, payoutId: string) {
  const original = parseCaseView(owner), checked = parse(view, value); parse(id, payoutId);
  if (original.state !== 'return_authorized' || checked.caseId !== original.id || checked.invoiceId !== original.source.invoiceId || checked.payout.payoutId !== payoutId ||
    Date.parse(checked.payout.recordedAt) < Date.parse(original.decision!.decidedAt!)) return invalid(); return checked;
}
export function refundSettlementInput(value: RefundSettlementInput) {
  const parsed = input.safeParse(value); if (!parsed.success) throw new RequestNotSentError(400, 'invalid_refund_settlement'); return parsed.data;
}
export function prepareRefundSettlement(actorId: string, owner: AfterSalesCase, before: RefundSettlementView, shown: RefundSettlementPreview,
  reason: string, reviewedEvidenceHash: string) {
  const checked = parse(preview, shown); parseRefundSettlementView(checked.view, owner, before.payout.payoutId);
  if (JSON.stringify(checked.view) !== JSON.stringify(parseRefundSettlementView(before, owner, before.payout.payoutId)) ||
    reviewedEvidenceHash !== checked.view.evidence.sha256) throw new RequestNotSentError(409, 'evidence_read_not_eligible');
  return Object.freeze({ actorId: parse(id, actorId), payoutId: checked.view.payout.payoutId, caseId: checked.view.caseId, returnId: checked.view.returnId, invoiceId: checked.view.invoiceId,
    financialApprovalId: checked.view.payout.approvalId, amount: checked.view.amount,
    expected: Object.freeze(checked.receipts.map(row => Object.freeze({ receiptId: row.receiptId, version: row.versionAfter, paid: row.paidAfter, pending: row.pending, reservedForRefund: row.reservedForRefund, available: row.available }))),
    body: Object.freeze({ input: Object.freeze({ ...checked.input }), binding: checked.binding, attested: true as const, reason: parse(text(1000), reason) }) });
}
export function createRefundSettlementGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async read(actorId: string, owner: AfterSalesCase, payoutId: string) {
      return parseRefundSettlementView(data(await client.POST('/api/v1/billing/refund-payouts/{id}/settlement/query', {
        body: { purpose: 'refund-settlement' }, params: { path: { id: parse(id, payoutId) }, header: headers(actorId) }, cache: 'no-store' })), owner, payoutId);
    },
    async preview(actorId: string, owner: AfterSalesCase, before: RefundSettlementView, values: RefundSettlementInput) {
      const selected = refundSettlementInput(values), initial = parseRefundSettlementView(before, owner, before.payout.payoutId);
      if (initial.settlement) throw new RequestNotSentError(409, 'refund_settlement_already_verified');
      const checked = parse(preview, data(await client.POST('/api/v1/billing/refund-payouts/{id}/settlement/preview', {
        body: selected, params: { path: { id: initial.payout.payoutId }, header: headers(actorId) }, cache: 'no-store' })));
      parseRefundSettlementView(checked.view, owner, initial.payout.payoutId);
      if (JSON.stringify(checked.view) !== JSON.stringify(initial) || JSON.stringify(checked.input) !== JSON.stringify(selected)) return invalid(); return checked;
    },
    async verify(command: ReturnType<typeof prepareRefundSettlement>, key: string, current: () => boolean) {
      check(current); if (refundSettlementOwner(key) !== command.payoutId) return invalid();
      const saved = parseRefundSettlementResult(data(await client.POST('/api/v1/billing/refund-payouts/{id}/settlement/verify', {
        body: command.body, params: { path: { id: command.payoutId }, header: { ...headers(command.actorId), 'Idempotency-Key': key } }, cache: 'no-store' })), key);
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown');
      return saved.caseId === command.caseId && saved.returnId === command.returnId && saved.invoiceId === command.invoiceId && saved.financialApprovalId === command.financialApprovalId &&
        saved.amount === command.amount && JSON.stringify(saved.receipts) === JSON.stringify(command.expected) ? saved : invalid();
    },
    async recover(actorId: string, key: string) {
      const recovered = parse(z.object({ status: z.enum(['committed', 'not_confirmed']), result: result.nullable() }).strict(), data(await client.POST('/api/v1/billing/refund-payouts/{id}/settlement/operations/query', {
        body: { purpose: 'operation-recovery', key }, params: { path: { id: refundSettlementOwner(key) }, header: headers(actorId) }, cache: 'no-store' })));
      if (recovered.status === 'not_confirmed' && recovered.result === null) return null;
      return recovered.status === 'committed' && recovered.result ? { state: 'committed' as const, result: parseRefundSettlementResult(recovered.result, key) } : invalid();
    },
    async original(actorId: string, before: RefundSettlementView, current: () => boolean) {
      const checked = parse(view, before); check(current);
      const response = await client.POST('/api/v1/billing/refund-payouts/{id}/settlement/evidence/{reservationId}/original/query', {
        body: { purpose: 'refund-settlement-evidence-view', reference: checked.evidence }, params: { path: { id: checked.payout.payoutId, reservationId: checked.reservationId }, header: headers(actorId) },
        parseAs: 'arrayBuffer', cache: 'no-store' });
      check(current); const bytes = data(response), mime = response.response.headers.get('content-type');
      if (!(bytes instanceof ArrayBuffer) || bytes.byteLength <= 0 || bytes.byteLength > 10 * 1024 * 1024 || response.response.headers.get('content-length') !== String(bytes.byteLength) || !['image/png', 'image/jpeg'].includes(mime ?? '')) return invalid();
      const digest = await crypto.subtle.digest('SHA-256', bytes); check(current);
      if (Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('') !== checked.evidence.sha256) return invalid();
      return new Blob([bytes], { type: mime! });
    },
  };
}
