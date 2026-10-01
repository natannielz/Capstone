import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';

const id = z.string().regex(/^[1-9][0-9]{0,18}$(?![\s\S])/).refine(value => BigInt(value) <= BigInt('9223372036854775807'));
const text = (maximum: number) => z.string().min(1).max(maximum).refine(value => value === value.trim() && !/[\p{Cc}\p{Cf}]/u.test(value));
const timestamp = z.string().datetime({ offset: true });
const issue = z.enum(['wrong_item', 'damaged', 'missing', 'other']);
const sourceLine = z.object({ orderId: id, shipmentId: id, receiptId: id, skuId: id, skuCode: text(160), productName: text(300),
  packageLabel: text(160), baseUnitsPerPackage: z.number().int().min(1).max(1000000), baseUnitCode: text(160), acceptedBaseUnits: id }).strict();
const unique = (lines: readonly { receiptId: string; skuId: string }[]) => new Set(lines.map(line => line.receiptId + ':' + line.skuId)).size === lines.length;
const source = z.object({ invoiceId: id, binding: z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/), number: text(160), buyerName: text(1000), sellerName: text(1000), issuedAt: timestamp,
  lines: z.array(sourceLine).min(1).max(100).refine(unique) }).strict();
const reportedLine = z.object({ receiptId: id, skuId: id, reportedBaseUnits: id.refine(value => BigInt(value) <= BigInt(1000000)) }).strict();
const reported = z.array(reportedLine).min(1).max(100).refine(unique);
const matches = (sale: CaseSource, lines: readonly CaseLine[]) => lines.every(line => {
  const item = sale.lines.find(item => item.receiptId === line.receiptId && item.skuId === line.skuId);
  return !!item && BigInt(line.reportedBaseUnits) <= BigInt(item.acceptedBaseUnits);
});
const result = z.object({ caseId: id, invoiceId: id, revision: z.literal('1'), requestedAt: timestamp, operation: z.literal('fulfillment.case.request') }).strict();
const state = z.enum(['requested', 'return_authorized', 'rejected']);
const decision = z.object({ caseId: id, invoiceId: id, revision: z.enum(['1', '2']), binding: z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/), state, decidedAt: timestamp.nullable(), publicExplanation: text(1000).nullable(), lines: z.array(reportedLine).max(100).refine(unique) }).strict()
  .refine(value => value.revision === '1' ? value.state === 'requested' && value.decidedAt === null && value.publicExplanation === null && value.lines.length === 0
    : value.state !== 'requested' && value.decidedAt !== null && value.publicExplanation !== null && (value.state === 'rejected' ? value.lines.length === 0 : value.lines.length > 0));
const view = z.object({ id, revision: z.enum(['1', '2']), requestedAt: timestamp, state, issue, reportedProblem: text(1000), source, lines: reported, decision: decision.nullable() }).strict()
  .refine(value => matches(value.source, value.lines) && Date.parse(value.requestedAt) >= Date.parse(value.source.issuedAt) && (value.revision === '1' ? value.state === 'requested' && value.decision === null
    : value.decision !== null && value.decision.caseId === value.id && value.decision.invoiceId === value.source.invoiceId && value.decision.revision === value.revision && value.decision.state === value.state && Date.parse(value.decision.decidedAt!) >= Date.parse(value.requestedAt) && value.decision.lines.every(line => {
      const original = value.lines.find(item => item.receiptId === line.receiptId && item.skuId === line.skuId); return !!original && BigInt(line.reportedBaseUnits) <= BigInt(original.reportedBaseUnits);
    })));
export type CaseSource = z.infer<typeof source>;
export type CaseLine = z.infer<typeof reportedLine>;
export type CaseResult = z.infer<typeof result>;
export type AfterSalesCase = z.infer<typeof view>;
export type CaseDecision = z.infer<typeof decision>;
const decisionResult = z.object({ caseId: id, invoiceId: id, revision: z.literal('2'), state: z.enum(['return_authorized', 'rejected']), decidedAt: timestamp, operation: z.literal('fulfillment.case.decide') }).strict();
export type CaseDecisionResult = z.infer<typeof decisionResult>;
function invalid(): never { throw new AccessFailure(502, 'case_response_invalid'); }
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const parsed = schema.safeParse(value); return parsed.success ? parsed.data : invalid(); }
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': priceId(actorId) });
export function caseOwner(key: string) {
  const matched = /^ca\.([1-9][0-9]{0,18})\.[a-zA-Z0-9_-]{1,90}$(?![\s\S])/.exec(key);
  if (!matched || key.length > 128) return invalid(); return priceId(matched[1]);
}
export function parseCaseResult(value: unknown, key: string) { const parsed = parse(result, value); if (parsed.invoiceId !== caseOwner(key)) return invalid(); return parsed; }
export function caseDecisionOwner(key: string) {
  const matched = /^cd\.([1-9][0-9]{0,18})\.[a-zA-Z0-9_-]{1,90}$(?![\s\S])/.exec(key);
  if (!matched || key.length > 128) return invalid(); return priceId(matched[1]);
}
export function parseCaseDecisionResult(value: unknown, key: string) { const parsed = parse(decisionResult, value); if (parsed.caseId !== caseDecisionOwner(key)) return invalid(); return parsed; }
export function prepareCaseDecision(actorId: string, owner: AfterSalesCase, review: CaseDecision, action: string, lines: readonly CaseLine[], publicExplanation: string, reason: string) {
  const original = view.safeParse(owner), preview = decision.safeParse(review), explanation = text(1000).safeParse(publicExplanation), note = text(1000).safeParse(reason);
  const values = z.array(reportedLine).max(100).refine(unique).safeParse(lines);
  if (!original.success || !preview.success || !explanation.success || !note.success || !values.success || original.data.revision !== '1' || preview.data.revision !== '1' || original.data.id !== preview.data.caseId || original.data.source.invoiceId !== preview.data.invoiceId || !['authorize_return', 'reject'].includes(action)
    || (action === 'reject' ? values.data.length !== 0 : values.data.length === 0) || !values.data.every(line => {
      const item = original.data.lines.find(item => item.receiptId === line.receiptId && item.skuId === line.skuId); return !!item && BigInt(line.reportedBaseUnits) <= BigInt(item.reportedBaseUnits);
    })) throw new AccessFailure(400, 'invalid_after_sales_decision');
  return Object.freeze({ actorId: priceId(actorId), caseId: original.data.id, invoiceId: original.data.source.invoiceId, revision: '1' as const,
    body: Object.freeze({ binding: preview.data.binding, action, publicExplanation, reason, lines: Object.freeze(values.data.map(line => Object.freeze(line))) }) });
}
function data<T>(response: { data?: T; error?: unknown; response: Response }): T {
  if (!response.response.ok || response.data === undefined) { const error = response.error && typeof response.error === 'object' ? response.error as Record<string, unknown> : {};
    throw new AccessFailure(response.response.status, typeof error.code === 'string' ? error.code : 'request_failed'); } return response.data;
}
export function prepareCase(actorId: string, sale: CaseSource, kind: string, values: readonly CaseLine[], reason: string) {
  const parsedSource = source.safeParse(sale), parsedLines = reported.safeParse(values), parsedIssue = issue.safeParse(kind), parsedReason = text(1000).safeParse(reason);
  if (!parsedSource.success || !parsedLines.success || !parsedIssue.success || !parsedReason.success || !matches(parsedSource.data, parsedLines.data)) throw new AccessFailure(400, 'invalid_after_sales_case');
  const lines = parsedLines.data.toSorted((a, b) => BigInt(a.receiptId) < BigInt(b.receiptId) ? -1 : BigInt(a.receiptId) > BigInt(b.receiptId) ? 1 : BigInt(a.skuId) < BigInt(b.skuId) ? -1 : 1);
  return Object.freeze({ actorId: priceId(actorId), source: Object.freeze({ ...parsedSource.data, lines: Object.freeze(parsedSource.data.lines.map(line => Object.freeze(line))) }),
    body: Object.freeze({ invoiceId: parsedSource.data.invoiceId, binding: parsedSource.data.binding, issue: parsedIssue.data, reason: parsedReason.data, lines: Object.freeze(lines.map(line => Object.freeze(line))) }) });
}
export function createCaseGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async source(actorId: string, invoiceId: string) {
      const parsed = parse(source, data(await client.POST('/api/v1/fulfillment/cases/sales/{invoiceId}/query', { body: { purpose: 'after-sales-case' }, params: { path: { invoiceId: priceId(invoiceId) }, header: { ...headers(actorId), 'If-Match': '"1"' } } })));
      if (parsed.invoiceId !== invoiceId) return invalid(); return parsed;
    },
    async request(command: ReturnType<typeof prepareCase>, key: string, current: () => boolean) {
      if (caseOwner(key) !== command.body.invoiceId) return invalid(); if (!current()) throw new RequestNotSentError(409, 'case_view_stale');
      const parsed = parseCaseResult(data(await client.POST('/api/v1/fulfillment/cases', { body: { ...command.body, lines: [...command.body.lines] }, params: { header: { ...headers(command.actorId), 'If-Match': '"0"', 'Idempotency-Key': key } } })), key);
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); return parsed;
    },
    async recover(actorId: string, key: string) {
      caseOwner(key); const parsed = parse(z.object({ status: z.enum(['not_confirmed', 'committed']), result: result.nullable() }).strict(), data(await client.POST('/api/v1/fulfillment/cases/operations/query', { body: { purpose: 'operation-recovery', key }, params: { header: headers(actorId) } })));
      if (parsed.status === 'not_confirmed' && parsed.result === null) return null;
      if (parsed.status !== 'committed' || parsed.result === null) return invalid(); return { state: 'committed' as const, result: parseCaseResult(parsed.result, key) };
    },
    async list(actorId: string, beforeId: string | null = null, signal?: AbortSignal) {
      const parsed = parse(z.object({ items: z.array(view).max(25), hasMore: z.boolean() }).strict(), data(await client.POST('/api/v1/fulfillment/cases/query', { body: { purpose: 'after-sales-case', beforeId }, params: { header: headers(actorId) }, signal })));
      if (new Set(parsed.items.map(item => item.id)).size !== parsed.items.length || parsed.hasMore && parsed.items.length !== 25 || beforeId !== null && parsed.items.some(item => BigInt(item.id) >= BigInt(priceId(beforeId)))) return invalid(); return parsed;
    },
    async read(actorId: string, caseId: string, revision: '1' | '2' = '1') {
      const parsed = parse(view, data(await client.POST('/api/v1/fulfillment/cases/{id}/query', { body: { purpose: 'after-sales-case' }, params: { path: { id: priceId(caseId) }, header: { ...headers(actorId), 'If-Match': '"' + revision + '"' } } })));
      if (parsed.id !== caseId) return invalid(); return parsed;
    },
    async decision(actorId: string, owner: AfterSalesCase) {
      const parsed = parse(decision, data(await client.POST('/api/v1/fulfillment/cases/{id}/decision/query', { body: { purpose: 'after-sales-decision' }, params: { path: { id: priceId(owner.id) }, header: { ...headers(actorId), 'If-Match': '"' + owner.revision + '"' } } })));
      if (parsed.caseId !== owner.id || parsed.invoiceId !== owner.source.invoiceId || parsed.revision !== owner.revision || parsed.state !== owner.state) return invalid(); return parsed;
    },
    async decide(command: ReturnType<typeof prepareCaseDecision>, key: string, current: () => boolean) {
      if (caseDecisionOwner(key) !== command.caseId) return invalid(); if (!current()) throw new RequestNotSentError(409, 'case_view_stale');
      const parsed = parseCaseDecisionResult(data(await client.POST('/api/v1/fulfillment/cases/{id}/decision', { body: { ...command.body, lines: [...command.body.lines] }, params: { path: { id: command.caseId }, header: { ...headers(command.actorId), 'If-Match': '"1"', 'Idempotency-Key': key } } })), key);
      if (parsed.invoiceId !== command.invoiceId) return invalid(); if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); return parsed;
    },
    async recoverDecision(actorId: string, key: string) {
      const caseId = caseDecisionOwner(key); const parsed = parse(z.object({ status: z.enum(['not_confirmed', 'committed']), result: decisionResult.nullable() }).strict(), data(await client.POST('/api/v1/fulfillment/cases/{id}/decision/operations/query', { body: { purpose: 'operation-recovery', key }, params: { path: { id: caseId }, header: headers(actorId) } })));
      if (parsed.status === 'not_confirmed' && parsed.result === null) return null;
      if (parsed.status !== 'committed' || parsed.result === null) return invalid(); return { state: 'committed' as const, result: parseCaseDecisionResult(parsed.result, key) };
    },
  };
}
