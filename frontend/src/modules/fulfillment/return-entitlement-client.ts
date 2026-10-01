import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { paymentAllocationSchemas } from '../billing/payment-allocation-client';
import { parseCaseView, type AfterSalesCase } from './case-client';
import { parseInspectionAssessment } from './inspection-client';
import type { ResolutionView } from './resolution-client';

const { id, money, positive, hash, text, cents } = paymentAllocationSchemas;
const version = z.union([z.literal('0'), id]).refine(value => BigInt(value) < BigInt('9223372036854775806'));
const action = z.enum(['credit', 'refund']), timestamp = z.string().datetime({ offset: true });
const quantity = id.refine(value => BigInt(value) <= BigInt(1000000));
const unique = (lines: readonly { receiptId: string; skuId: string }[]) => new Set(lines.map(line => line.receiptId + ':' + line.skuId)).size === lines.length;
const lineInput = z.object({ receiptId: id, skuId: id, tax: money, charges: money }).strict();
const inputSchema = z.object({ lines: z.array(lineInput).min(1).max(100).refine(unique) }).strict();
const attributedLine = z.object({ receiptId: id, skuId: id, attributedBaseUnits: quantity, net: money, tax: money, charges: money }).strict();
const amounts = { net: money, tax: money, charges: money, credit: positive, lines: z.array(attributedLine).min(1).max(100).refine(unique) };
const totals = (value: { net: string; tax: string; charges: string; credit: string; lines: readonly { net: string; tax: string; charges: string }[] }) =>
  ['net', 'tax', 'charges'].every(component => value.lines.reduce((sum, line) => sum + cents(line[component as 'net' | 'tax' | 'charges']), BigInt(0)) === cents(value[component as 'net' | 'tax' | 'charges'])) &&
  cents(value.net) + cents(value.tax) + cents(value.charges) === cents(value.credit);
const resultSchema = z.object({ returnId: id, caseId: id, invoiceId: id, action, version: version.refine(value => value !== '0'), requestedAt: timestamp,
  ...amounts, state: z.literal('financial_review_requested'), operation: z.literal('billing.return.entitlement.request') }).strict().refine(totals);
const sourceLine = z.object({ receiptId: id, skuId: id, inspectedBaseUnits: quantity, identity: z.enum(['matched', 'different', 'unverified']), condition: z.enum(['sealed', 'opened', 'damaged', 'mixed']),
  issuedBaseUnits: id, issuedNet: money, issuedTax: money, issuedCharges: money, previouslyAttributedBaseUnits: version,
  previouslyAttributedNet: money, previouslyAttributedTax: money, previouslyAttributedCharges: money }).strict().refine(value =>
  BigInt(value.inspectedBaseUnits) <= BigInt(value.issuedBaseUnits) && BigInt(value.previouslyAttributedBaseUnits) <= BigInt(value.issuedBaseUnits) &&
  cents(value.previouslyAttributedNet) <= cents(value.issuedNet) && cents(value.previouslyAttributedTax) <= cents(value.issuedTax) && cents(value.previouslyAttributedCharges) <= cents(value.issuedCharges) &&
  (value.previouslyAttributedBaseUnits !== '0' || [value.previouslyAttributedNet, value.previouslyAttributedTax, value.previouslyAttributedCharges].every(amount => amount === '0.00')));
const viewSchema = z.object({ returnId: id, caseId: id, invoiceId: id, action, version, buyerName: text(1000), lines: z.array(sourceLine).min(1).max(100).refine(unique), request: resultSchema.nullable() }).strict().refine(value => {
  const saved = value.request; return !saved || saved.caseId === value.caseId && saved.returnId === value.returnId && saved.invoiceId === value.invoiceId && saved.action === value.action &&
    BigInt(saved.version) <= BigInt(value.version) && saved.lines.length === value.lines.length && saved.lines.every(line => {
      const source = value.lines.find(item => item.receiptId === line.receiptId && item.skuId === line.skuId);
      return source && line.attributedBaseUnits === source.inspectedBaseUnits && BigInt(line.attributedBaseUnits) <= BigInt(source.previouslyAttributedBaseUnits) &&
        cents(line.net) <= cents(source.previouslyAttributedNet) && cents(line.tax) <= cents(source.previouslyAttributedTax) && cents(line.charges) <= cents(source.previouslyAttributedCharges);
    });
});
const previewSchema = z.object({ view: viewSchema, binding: hash, attributionProcedureReference: text(256), taxTreatmentReference: text(256),
  chargeTreatmentReference: text(256), creditNoteProcedureReference: text(256), ...amounts }).strict().refine(value => totals(value) && value.view.request === null &&
  value.lines.length === value.view.lines.length && value.lines.every(line => {
    const source = value.view.lines.find(item => item.receiptId === line.receiptId && item.skuId === line.skuId);
    return source && line.attributedBaseUnits === source.inspectedBaseUnits && BigInt(line.attributedBaseUnits) <= BigInt(source.issuedBaseUnits) - BigInt(source.previouslyAttributedBaseUnits) &&
      cents(line.net) <= cents(source.issuedNet) - cents(source.previouslyAttributedNet) && cents(line.tax) <= cents(source.issuedTax) - cents(source.previouslyAttributedTax) &&
      cents(line.charges) <= cents(source.issuedCharges) - cents(source.previouslyAttributedCharges);
  }));
export type ReturnEntitlementInput = z.infer<typeof inputSchema>;
export type ReturnEntitlementResult = z.infer<typeof resultSchema>;
export type ReturnEntitlementView = z.infer<typeof viewSchema>;
export type ReturnEntitlementPreview = z.infer<typeof previewSchema>;
const invalid = (): never => { throw new AccessFailure(502, 'return_entitlement_response_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const parsed = schema.safeParse(value); return parsed.success ? parsed.data : invalid(); }
function data<T>(response: { data?: T; error?: unknown; response: Response }): T {
  if (!response.response.ok || response.data === undefined) throw new AccessFailure(response.response.status, (response.error as { code?: string })?.code ?? 'request_failed'); return response.data;
}
function resolutionOwner(owner: AfterSalesCase, resolution: ResolutionView) {
  const original = parseCaseView(owner), inspected = parseInspectionAssessment(resolution.inspection);
  if (original.state !== 'return_authorized' || resolution.caseId !== original.id || resolution.invoiceId !== original.source.invoiceId || resolution.revision !== '1' || !resolution.request ||
    resolution.request.state !== 'resolution_requested' || !action.safeParse(resolution.request.action).success ||
    !timestamp.safeParse(resolution.request.requestedAt).success || Date.parse(resolution.request.requestedAt) < Date.parse(inspected.inspectedAt)) return invalid();
  return { original, inspected, caseId: original.id, invoiceId: original.source.invoiceId, returnId: parse(id, resolution.returnId), action: parse(action, resolution.request.action) };
}
export function returnEntitlementOwner(key: string) {
  const parts = /^er\.([1-9][0-9]{0,18})\.([1-9][0-9]{0,18})\.(credit|refund)\.([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$(?![\s\S])/.exec(key);
  if (!parts) return invalid(); return { caseId: parse(id, parts[1]), returnId: parse(id, parts[2]), action: parse(action, parts[3]) };
}
export function parseReturnEntitlementResult(value: unknown, key: string) {
  const owner = returnEntitlementOwner(key), result = parse(resultSchema, value);
  if (result.caseId !== owner.caseId || result.returnId !== owner.returnId || result.action !== owner.action) return invalid(); return result;
}
export function parseReturnEntitlementView(value: unknown, owner: AfterSalesCase, resolution: ResolutionView) {
  const source = resolutionOwner(owner, resolution), view = parse(viewSchema, value);
  if (view.caseId !== source.caseId || view.returnId !== source.returnId || view.invoiceId !== source.invoiceId || view.action !== source.action || view.buyerName !== source.original.source.buyerName ||
    view.lines.length !== source.inspected.lines.length || view.request && Date.parse(view.request.requestedAt) < Date.parse(resolution.request!.requestedAt) || view.lines.some(line => {
      const inspected = source.inspected.lines.find(item => item.receiptId === line.receiptId && item.skuId === line.skuId), issued = source.original.source.lines.find(item => item.receiptId === line.receiptId && item.skuId === line.skuId);
      return !inspected || !issued || line.inspectedBaseUnits !== inspected.inspectedBaseUnits || line.identity !== inspected.identity || line.condition !== inspected.condition || line.issuedBaseUnits !== issued.acceptedBaseUnits;
    })) return invalid(); return view;
}
export function returnEntitlementInput(view: ReturnEntitlementView, values: ReturnEntitlementInput): ReturnEntitlementInput {
  const checked = parse(viewSchema, view), input = inputSchema.safeParse(values);
  if (!input.success || checked.request || input.data.lines.length !== checked.lines.length || input.data.lines.some(line => {
    const source = checked.lines.find(item => item.receiptId === line.receiptId && item.skuId === line.skuId);
    return !source || BigInt(source.inspectedBaseUnits) > BigInt(source.issuedBaseUnits) - BigInt(source.previouslyAttributedBaseUnits) ||
      cents(line.tax) > cents(source.issuedTax) - cents(source.previouslyAttributedTax) || cents(line.charges) > cents(source.issuedCharges) - cents(source.previouslyAttributedCharges);
  })) throw new AccessFailure(400, 'invalid_return_entitlement');
  return { lines: input.data.lines.toSorted((left, right) => BigInt(left.receiptId) < BigInt(right.receiptId) ? -1 : BigInt(left.receiptId) > BigInt(right.receiptId) ? 1 : BigInt(left.skuId) < BigInt(right.skuId) ? -1 : 1) };
}
function previewInput(shown: ReturnEntitlementPreview, values: ReturnEntitlementInput) {
  const preview = parse(previewSchema, shown), input = returnEntitlementInput(preview.view, values);
  if (input.lines.some(line => { const attributed = preview.lines.find(item => item.receiptId === line.receiptId && item.skuId === line.skuId); return !attributed || attributed.tax !== line.tax || attributed.charges !== line.charges; })) return invalid();
  return { preview, input };
}
export function prepareReturnEntitlement(actorId: string, owner: AfterSalesCase, resolution: ResolutionView, shown: ReturnEntitlementPreview, values: ReturnEntitlementInput, reason: string) {
  const { preview, input } = previewInput(shown, values); parseReturnEntitlementView(preview.view, owner, resolution);
  const audit = text(1000).safeParse(reason); if (!audit.success) throw new AccessFailure(400, 'invalid_return_entitlement');
  const command = { actorId: parse(id, actorId), caseId: preview.view.caseId, returnId: preview.view.returnId, invoiceId: preview.view.invoiceId,
    body: { action: preview.view.action, expectedVersion: preview.view.version, binding: preview.binding, input, reason: audit.data },
    expectedResult: { net: preview.net, tax: preview.tax, charges: preview.charges, credit: preview.credit, lines: preview.lines.map(line => ({ ...line })) } };
  input.lines.forEach(Object.freeze); Object.freeze(input.lines); Object.freeze(input); Object.freeze(command.body);
  command.expectedResult.lines.forEach(Object.freeze); Object.freeze(command.expectedResult.lines); Object.freeze(command.expectedResult); return Object.freeze(command);
}
export function createReturnEntitlementGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': parse(id, actorId) });
  return {
    async read(actorId: string, owner: AfterSalesCase, resolution: ResolutionView) {
      const source = resolutionOwner(owner, resolution);
      return parseReturnEntitlementView(data(await client.POST('/api/v1/billing/cases/{id}/returns/{returnId}/entitlement/query', {
        body: { purpose: 'return-entitlement', action: source.action }, params: { path: { id: source.caseId, returnId: source.returnId }, header: headers(actorId) }, cache: 'no-store' })), owner, resolution);
    },
    async preview(actorId: string, owner: AfterSalesCase, resolution: ResolutionView, view: ReturnEntitlementView, values: ReturnEntitlementInput) {
      const before = parseReturnEntitlementView(view, owner, resolution), input = returnEntitlementInput(before, values);
      const preview = parse(previewSchema, data(await client.POST('/api/v1/billing/cases/{id}/returns/{returnId}/entitlement/preview', {
        body: { action: before.action, input }, params: { path: { id: before.caseId, returnId: before.returnId }, header: headers(actorId) }, cache: 'no-store' })));
      parseReturnEntitlementView(preview.view, owner, resolution); previewInput(preview, input);
      if (BigInt(preview.view.version) < BigInt(before.version) || before.lines.some(line => {
        const after = preview.view.lines.find(item => item.receiptId === line.receiptId && item.skuId === line.skuId);
        return !after || after.issuedNet !== line.issuedNet || after.issuedTax !== line.issuedTax || after.issuedCharges !== line.issuedCharges ||
          preview.view.version === before.version && (after.previouslyAttributedBaseUnits !== line.previouslyAttributedBaseUnits || after.previouslyAttributedNet !== line.previouslyAttributedNet ||
            after.previouslyAttributedTax !== line.previouslyAttributedTax || after.previouslyAttributedCharges !== line.previouslyAttributedCharges);
      })) return invalid(); return preview;
    },
    async request(command: ReturnType<typeof prepareReturnEntitlement>, key: string, current: () => boolean) {
      const owner = returnEntitlementOwner(key);
      if (!current() || owner.caseId !== command.caseId || owner.returnId !== command.returnId || owner.action !== command.body.action) throw new RequestNotSentError(409, 'return_entitlement_view_stale');
      const value = data(await client.POST('/api/v1/billing/cases/{id}/returns/{returnId}/entitlement', { body: command.body,
        params: { path: { id: command.caseId, returnId: command.returnId }, header: { ...headers(command.actorId), 'Idempotency-Key': key, 'If-Match': '"' + command.body.expectedVersion + '"' } }, cache: 'no-store' }));
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); const saved = parseReturnEntitlementResult(value, key), expected = command.expectedResult;
      if (saved.invoiceId !== command.invoiceId || BigInt(saved.version) !== BigInt(command.body.expectedVersion) + BigInt(1) ||
        ['net', 'tax', 'charges', 'credit'].some(component => saved[component as 'net' | 'tax' | 'charges' | 'credit'] !== expected[component as 'net' | 'tax' | 'charges' | 'credit']) ||
        saved.lines.length !== expected.lines.length || expected.lines.some(line => { const after = saved.lines.find(item => item.receiptId === line.receiptId && item.skuId === line.skuId);
          return !after || after.attributedBaseUnits !== line.attributedBaseUnits || after.net !== line.net || after.tax !== line.tax || after.charges !== line.charges;
        })) return invalid(); return saved;
    },
    async recover(actorId: string, key: string) {
      const owner = returnEntitlementOwner(key), saved = parse(z.object({ status: z.enum(['not_confirmed', 'committed']), result: resultSchema.nullable() }).strict(),
        data(await client.POST('/api/v1/billing/cases/{id}/returns/{returnId}/entitlement/operations/query', { body: { purpose: 'operation-recovery', action: owner.action, requestKey: key },
          params: { path: { id: owner.caseId, returnId: owner.returnId }, header: headers(actorId) }, cache: 'no-store' })));
      if (saved.status === 'not_confirmed' && saved.result === null) return null;
      return saved.status === 'committed' && saved.result ? { state: 'committed' as const, result: parseReturnEntitlementResult(saved.result, key) } : invalid();
    },
  };
}
