import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { paymentAllocationSchemas } from '../billing/payment-allocation-client';
import { parseCaseView, type AfterSalesCase } from './case-client';

const { id, money, positive, hash, text, cents } = paymentAllocationSchemas;
const version = z.union([z.literal('0'), id]).refine(value => BigInt(value) < BigInt('9223372036854775806'));
const action = z.enum(['credit', 'refund']), timestamp = z.string().datetime({ offset: true });
const unique = <T>(values: readonly T[], key: (value: T) => string) => new Set(values.map(key)).size === values.length;
const attributedLine = z.object({ receiptId: id, skuId: id, attributedBaseUnits: id.refine(value => BigInt(value) <= BigInt(1000000)), net: money, tax: money, charges: money }).strict();
const requestSchema = z.object({ returnId: id, caseId: id, invoiceId: id, action, version: version.refine(value => value !== '0'), requestedAt: timestamp,
  net: money, tax: money, charges: money, credit: positive, lines: z.array(attributedLine).min(1).max(100), state: z.literal('financial_review_requested'), operation: z.literal('billing.return.entitlement.request') }).strict().refine(value =>
  unique(value.lines, line => line.receiptId + ':' + line.skuId) && ['net', 'tax', 'charges'].every(component => value.lines.reduce((sum, line) => sum + cents(line[component as 'net' | 'tax' | 'charges']), BigInt(0)) === cents(value[component as 'net' | 'tax' | 'charges'])) &&
  cents(value.net) + cents(value.tax) + cents(value.charges) === cents(value.credit));
const receiptResult = z.object({ receiptId: id, version: version.refine(value => value !== '0'), released: positive, reservedForRefund: positive, available: money }).strict()
  .refine(value => cents(value.reservedForRefund) >= cents(value.released));
const releaseResult = z.object({ originalAllocationId: id, invoiceId: id, amount: positive }).strict();
const effect = { invoiceVersion: version.refine(value => value !== '0'), credit: positive, reservedRefund: money, remainingInvoice: money,
  receipts: z.array(receiptResult).max(100), releases: z.array(releaseResult).max(100) };
const conservation = (value: { credit: string; reservedRefund: string; receipts: readonly z.infer<typeof receiptResult>[]; releases: readonly z.infer<typeof releaseResult>[] }) =>
  unique(value.receipts, receipt => receipt.receiptId) && unique(value.releases, line => line.originalAllocationId) &&
  value.receipts.reduce((sum, receipt) => sum + cents(receipt.released), BigInt(0)) === cents(value.reservedRefund) &&
  value.releases.reduce((sum, line) => sum + cents(line.amount), BigInt(0)) === cents(value.reservedRefund) && cents(value.reservedRefund) <= cents(value.credit);
const resultSchema = z.object({ financialApprovalId: id, caseId: id, returnId: id, invoiceId: id, action, ...effect, recordedAt: timestamp,
  operation: z.literal('billing.return.financial.approve'), state: z.enum(['invoice_credited', 'refund_reserved']) }).strict().refine(value => conservation(value) &&
  value.releases.every(line => line.invoiceId === value.invoiceId) && (value.action === 'credit' ? value.state === 'invoice_credited' && value.reservedRefund === '0.00' && value.receipts.length === 0 && value.releases.length === 0
    : value.state === 'refund_reserved' && value.reservedRefund !== '0.00' && value.receipts.length > 0 && value.releases.length > 0));
const invoiceSchema = z.object({ invoiceId: id, version, issued: positive, paid: money, credited: money, outstanding: money,
  paymentState: z.enum(['unpaid', 'partial', 'paid', 'credited']), buyerName: text(1000) }).strict().refine(value => cents(value.paid) + cents(value.credited) + cents(value.outstanding) === cents(value.issued) &&
  value.paymentState === (value.outstanding === '0.00' ? value.credited === value.issued ? 'credited' : 'paid' : value.paid !== '0.00' || value.credited !== '0.00' ? 'partial' : 'unpaid'));
const receiptBalance = z.object({ receiptId: id, version, received: positive, allocated: money, reservedForRefund: money, available: money }).strict()
  .refine(value => cents(value.allocated) + cents(value.reservedForRefund) + cents(value.available) === cents(value.received));
const allocationSchema = z.object({ originalAllocationId: id, receiptId: id, receiptVersion: version, originallyAllocated: positive, previouslyReleased: money, available: money }).strict()
  .refine(value => cents(value.previouslyReleased) + cents(value.available) === cents(value.originallyAllocated));
const viewSchema = z.object({ request: requestSchema, invoice: invoiceSchema, receipts: z.array(receiptBalance).max(100), allocations: z.array(allocationSchema).max(100),
  hasMore: z.boolean(), beforeAllocationId: id.nullable(), approval: resultSchema.nullable() }).strict().refine(value => {
  if (value.request.invoiceId !== value.invoice.invoiceId || !unique(value.receipts, item => item.receiptId) || !unique(value.allocations, item => item.originalAllocationId) ||
    value.allocations.some((item, index) => index > 0 && BigInt(item.originalAllocationId) >= BigInt(value.allocations[index - 1].originalAllocationId)) ||
    value.beforeAllocationId !== (value.allocations.at(-1)?.originalAllocationId ?? null) || value.hasMore && value.allocations.length !== 100 ||
    value.request.action === 'credit' && (value.allocations.length || value.receipts.length || value.hasMore)) return false;
  if (value.allocations.some(item => !value.receipts.some(receipt => receipt.receiptId === item.receiptId && receipt.version === item.receiptVersion))) return false;
  if (value.receipts.some(receipt => !value.allocations.some(item => item.receiptId === receipt.receiptId) || value.allocations.filter(item => item.receiptId === receipt.receiptId).reduce((sum, item) => sum + cents(item.available), BigInt(0)) > cents(receipt.allocated)) ||
    value.allocations.reduce((sum, item) => sum + cents(item.available), BigInt(0)) > cents(value.invoice.paid)) return false;
  const saved = value.approval; return !saved || saved.caseId === value.request.caseId && saved.returnId === value.request.returnId && saved.invoiceId === value.invoice.invoiceId && saved.action === value.request.action &&
    saved.credit === value.request.credit && BigInt(saved.invoiceVersion) <= BigInt(value.invoice.version) && Date.parse(saved.recordedAt) >= Date.parse(value.request.requestedAt);
});
const releaseInput = z.object({ originalAllocationId: id, expectedReceiptVersion: version, amount: positive }).strict();
const inputSchema = z.object({ expectedInvoiceVersion: version, releases: z.array(releaseInput).max(100).refine(values => unique(values, value => value.originalAllocationId)) }).strict();
const previewSchema = z.object({ view: viewSchema, binding: hash, creditNoteProcedureReference: text(256), taxTreatmentReference: text(256), chargeTreatmentReference: text(256), refundReservationProcedureReference: text(256), ...effect }).strict()
  .refine(value => conservation(value) && value.view.approval === null && value.credit === value.view.request.credit && BigInt(value.invoiceVersion) === BigInt(value.view.invoice.version) + BigInt(1) &&
    cents(value.remainingInvoice) === cents(value.view.invoice.outstanding) + cents(value.reservedRefund) - cents(value.credit) && cents(value.reservedRefund) <= cents(value.view.invoice.paid) &&
    (value.view.request.action === 'credit' ? value.reservedRefund === '0.00' && value.receipts.length === 0 && value.releases.length === 0 : value.reservedRefund !== '0.00') &&
    value.releases.length === value.view.allocations.length && value.releases.every(line => line.invoiceId === value.view.invoice.invoiceId && value.view.allocations.some(item => item.originalAllocationId === line.originalAllocationId && cents(line.amount) <= cents(item.available))) &&
    value.receipts.length === value.view.receipts.length && value.receipts.every(receipt => {
      const before = value.view.receipts.find(item => item.receiptId === receipt.receiptId); if (!before) return false;
      const released = value.releases.filter(line => value.view.allocations.some(item => item.originalAllocationId === line.originalAllocationId && item.receiptId === receipt.receiptId)).reduce((sum, line) => sum + cents(line.amount), BigInt(0));
      return cents(receipt.released) === released && released <= cents(before.allocated) && BigInt(receipt.version) === BigInt(before.version) + BigInt(1) &&
        cents(receipt.reservedForRefund) === cents(before.reservedForRefund) + released && receipt.available === before.available;
    }));
export type ReturnFinancialAction = z.infer<typeof action>;
export type ReturnFinancialApprovalInput = z.infer<typeof inputSchema>;
export type ReturnFinancialApprovalResult = z.infer<typeof resultSchema>;
export type ReturnFinancialApprovalView = z.infer<typeof viewSchema>;
export type ReturnFinancialApprovalPreview = z.infer<typeof previewSchema>;
const invalid = (): never => { throw new AccessFailure(502, 'return_financial_response_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const parsed = schema.safeParse(value); return parsed.success ? parsed.data : invalid(); }
function data<T>(response: { data?: T; error?: unknown; response: Response }): T {
  if (!response.response.ok || response.data === undefined) throw new AccessFailure(response.response.status, (response.error as { code?: string })?.code ?? 'request_failed'); return response.data;
}
export function canApproveReturnFinancial(actions: readonly string[], value: ReturnFinancialAction) {
  return action.safeParse(value).success && ['case.read', 'invoice.read', 'credit.approve', ...(value === 'refund' ? ['payment.review.read', 'refund.approve'] : [])].every(permission => actions.includes(permission));
}
export function returnFinancialApprovalOwner(key: string) {
  const parts = /^fa\.([1-9][0-9]{0,18})\.([1-9][0-9]{0,18})\.(credit|refund)\.([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$(?![\s\S])/.exec(key);
  if (!parts) return invalid(); return { caseId: parse(id, parts[1]), returnId: parse(id, parts[2]), action: parse(action, parts[3]) };
}
export function parseReturnFinancialApprovalResult(value: unknown, key: string) {
  const owner = returnFinancialApprovalOwner(key), result = parse(resultSchema, value);
  if (result.caseId !== owner.caseId || result.returnId !== owner.returnId || result.action !== owner.action) return invalid(); return result;
}
export function parseReturnFinancialApprovalView(value: unknown, owner: AfterSalesCase, returnId: string, selectedAction: ReturnFinancialAction, beforeAllocationId: string | null = null) {
  const original = parseCaseView(owner), view = parse(viewSchema, value); parse(id, returnId); parse(action, selectedAction);
  if (original.state !== 'return_authorized' || !original.decision || view.request.caseId !== original.id || view.request.returnId !== returnId || view.request.action !== selectedAction ||
    view.invoice.invoiceId !== original.source.invoiceId || view.invoice.buyerName !== original.source.buyerName || Date.parse(view.request.requestedAt) < Date.parse(original.decision.decidedAt!) ||
    view.request.lines.some(line => !original.source.lines.some(item => item.receiptId === line.receiptId && item.skuId === line.skuId && BigInt(item.acceptedBaseUnits) >= BigInt(line.attributedBaseUnits)) ||
      !original.decision!.lines.some(item => item.receiptId === line.receiptId && item.skuId === line.skuId && BigInt(item.reportedBaseUnits) >= BigInt(line.attributedBaseUnits))) ||
    beforeAllocationId !== null && view.allocations.some(item => BigInt(item.originalAllocationId) >= BigInt(parse(id, beforeAllocationId)))) return invalid(); return view;
}
export function returnFinancialApprovalInput(view: ReturnFinancialApprovalView, values: ReturnFinancialApprovalInput) {
  const checked = parse(viewSchema, view), parsed = inputSchema.safeParse(values);
  if (!parsed.success || checked.approval || parsed.data.expectedInvoiceVersion !== checked.invoice.version ||
    (checked.request.action === 'credit' ? parsed.data.releases.length !== 0 : parsed.data.releases.length === 0)) throw new AccessFailure(400, 'invalid_return_financial_approval');
  const selected = parsed.data.releases;
  if (selected.some(line => !checked.allocations.some(item => item.originalAllocationId === line.originalAllocationId && item.receiptVersion === line.expectedReceiptVersion && cents(line.amount) <= cents(item.available))) ||
    checked.receipts.some(receipt => selected.filter(line => checked.allocations.some(item => item.originalAllocationId === line.originalAllocationId && item.receiptId === receipt.receiptId)).reduce((sum, line) => sum + cents(line.amount), BigInt(0)) > cents(receipt.allocated)))
    throw new AccessFailure(400, 'invalid_return_financial_approval');
  const released = selected.reduce((sum, line) => sum + cents(line.amount), BigInt(0));
  if (released > cents(checked.request.credit) || released > cents(checked.invoice.paid) || cents(checked.request.credit) > cents(checked.invoice.outstanding) + released) throw new AccessFailure(400, 'invalid_return_financial_approval');
  return { expectedInvoiceVersion: parsed.data.expectedInvoiceVersion, releases: selected.toSorted((left, right) => BigInt(left.originalAllocationId) < BigInt(right.originalAllocationId) ? -1 : 1) };
}
function previewInput(value: unknown, values: ReturnFinancialApprovalInput) {
  const preview = parse(previewSchema, value), input = returnFinancialApprovalInput(preview.view, values);
  if (preview.releases.length !== input.releases.length || input.releases.some(line => !preview.releases.some(item => item.originalAllocationId === line.originalAllocationId && item.amount === line.amount))) return invalid();
  return { preview, input };
}
export function prepareReturnFinancialApproval(actorId: string, owner: AfterSalesCase, returnId: string, selectedAction: ReturnFinancialAction, shown: ReturnFinancialApprovalPreview, values: ReturnFinancialApprovalInput, reason: string) {
  const { preview, input } = previewInput(shown, values); parseReturnFinancialApprovalView(preview.view, owner, returnId, selectedAction);
  const audit = text(1000).safeParse(reason); if (!audit.success) throw new AccessFailure(400, 'invalid_return_financial_approval');
  const command = { actorId: parse(id, actorId), caseId: owner.id, returnId: parse(id, returnId), invoiceId: preview.view.invoice.invoiceId,
    body: { action: selectedAction, binding: preview.binding, input, reason: audit.data }, expectedResult: { invoiceVersion: preview.invoiceVersion, credit: preview.credit,
      reservedRefund: preview.reservedRefund, remainingInvoice: preview.remainingInvoice, receipts: preview.receipts.map(receipt => ({ ...receipt })), releases: preview.releases.map(line => ({ ...line })) } };
  input.releases.forEach(Object.freeze); Object.freeze(input.releases); Object.freeze(input); Object.freeze(command.body);
  command.expectedResult.receipts.forEach(Object.freeze); Object.freeze(command.expectedResult.receipts); command.expectedResult.releases.forEach(Object.freeze); Object.freeze(command.expectedResult.releases); Object.freeze(command.expectedResult);
  return Object.freeze(command);
}
export function createReturnFinancialApprovalGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': parse(id, actorId) });
  return {
    async read(actorId: string, owner: AfterSalesCase, returnId: string, selectedAction: ReturnFinancialAction, beforeAllocationId: string | null = null) {
      parseCaseView(owner); parse(id, returnId); parse(action, selectedAction); if (beforeAllocationId !== null) parse(id, beforeAllocationId);
      return parseReturnFinancialApprovalView(data(await client.POST('/api/v1/billing/cases/{id}/returns/{returnId}/financial-approval/query', {
        body: { purpose: 'return-financial-approval', action: selectedAction, beforeAllocationId }, params: { path: { id: owner.id, returnId }, header: headers(actorId) }, cache: 'no-store' })), owner, returnId, selectedAction, beforeAllocationId);
    },
    async preview(actorId: string, owner: AfterSalesCase, view: ReturnFinancialApprovalView, values: ReturnFinancialApprovalInput) {
      const before = parseReturnFinancialApprovalView(view, owner, view.request.returnId, view.request.action), input = returnFinancialApprovalInput(before, values);
      const { preview } = previewInput(data(await client.POST('/api/v1/billing/cases/{id}/returns/{returnId}/financial-approval/preview', { body: { action: before.request.action, input },
        params: { path: { id: owner.id, returnId: before.request.returnId }, header: headers(actorId) }, cache: 'no-store' })), input);
      parseReturnFinancialApprovalView(preview.view, owner, before.request.returnId, before.request.action);
      if (JSON.stringify(preview.view.request) !== JSON.stringify(before.request) || JSON.stringify(preview.view.invoice) !== JSON.stringify(before.invoice) || preview.view.allocations.some(item => {
        const original = before.allocations.find(value => value.originalAllocationId === item.originalAllocationId); return !original || JSON.stringify(original) !== JSON.stringify(item);
      }) || preview.view.receipts.some(receipt => { const original = before.receipts.find(value => value.receiptId === receipt.receiptId); return !original || JSON.stringify(original) !== JSON.stringify(receipt); })) return invalid(); return preview;
    },
    async approve(command: ReturnType<typeof prepareReturnFinancialApproval>, key: string, current: () => boolean) {
      const owner = returnFinancialApprovalOwner(key);
      if (!current() || owner.caseId !== command.caseId || owner.returnId !== command.returnId || owner.action !== command.body.action) throw new RequestNotSentError(409, 'return_financial_view_stale');
      const value = data(await client.POST('/api/v1/billing/cases/{id}/returns/{returnId}/financial-approval', { body: command.body,
        params: { path: { id: command.caseId, returnId: command.returnId }, header: { ...headers(command.actorId), 'Idempotency-Key': key, 'If-Match': '"' + command.body.input.expectedInvoiceVersion + '"' } }, cache: 'no-store' }));
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); const saved = parseReturnFinancialApprovalResult(value, key), expected = command.expectedResult;
      if (saved.invoiceId !== command.invoiceId || ['invoiceVersion', 'credit', 'reservedRefund', 'remainingInvoice'].some(field => saved[field as keyof typeof expected] !== expected[field as keyof typeof expected]) ||
        saved.receipts.length !== expected.receipts.length || saved.releases.length !== expected.releases.length || expected.receipts.some(receipt => !saved.receipts.some(value => JSON.stringify(value) === JSON.stringify(receipt))) ||
        expected.releases.some(line => !saved.releases.some(value => JSON.stringify(value) === JSON.stringify(line)))) return invalid(); return saved;
    },
    async recover(actorId: string, key: string) {
      const owner = returnFinancialApprovalOwner(key), saved = parse(z.object({ status: z.enum(['not_confirmed', 'committed']), result: resultSchema.nullable() }).strict(),
        data(await client.POST('/api/v1/billing/cases/{id}/returns/{returnId}/financial-approval/operations/query', { body: { purpose: 'operation-recovery', action: owner.action, requestKey: key },
          params: { path: { id: owner.caseId, returnId: owner.returnId }, header: headers(actorId) }, cache: 'no-store' })));
      if (saved.status === 'not_confirmed' && saved.result === null) return null;
      return saved.status === 'committed' && saved.result ? { state: 'committed' as const, result: parseReturnFinancialApprovalResult(saved.result, key) } : invalid();
    },
  };
}
