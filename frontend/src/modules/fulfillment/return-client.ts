import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';
import { parseCaseView, type AfterSalesCase } from './case-client';

const count = z.string().regex(/^(?:0|[1-9][0-9]{0,18})$(?![\s\S])/).refine(value => BigInt(value) < BigInt('9223372036854775807'));
const id = z.string().regex(/^[1-9][0-9]{0,18}$(?![\s\S])/).refine(value => BigInt(value) <= BigInt('9223372036854775807')), quantity = id.refine(value => BigInt(value) <= BigInt(1000000));
const text = z.string().min(1).max(1000).refine(value => value === value.trim() && !/[\p{Cc}\p{Cf}]/u.test(value));
const timestamp = z.string().datetime({ offset: true });
const condition = z.enum(['uninspected', 'sealed', 'opened', 'damaged']);
const unique = (lines: readonly { receiptId: string; skuId: string }[]) => new Set(lines.map(line => line.receiptId + ':' + line.skuId)).size === lines.length;
const line = z.object({ receiptId: id, skuId: id, receivedBaseUnits: quantity, observedCondition: condition }).strict();
const lines = z.array(line).min(1).max(100).refine(unique);
const limit = z.object({ receiptId: id, skuId: id, warehouseId: id, authorizedBaseUnits: quantity, receivedBaseUnits: count }).strict().refine(value => BigInt(value.receivedBaseUnits) <= BigInt(value.authorizedBaseUnits));
const receipt = z.object({ id, revision: id, receivedAt: timestamp, publicNote: text, lines }).strict();
const view = z.object({ caseId: id, invoiceId: id, revision: count, binding: z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/), limits: z.array(limit).min(1).max(100).refine(unique), recent: z.array(receipt).max(25), hasEarlier: z.boolean() }).strict()
  .refine(value => {
    const revision = BigInt(value.revision), totals = new Map<string, bigint>();
    if (value.recent.length !== Number(revision > BigInt(25) ? BigInt(25) : revision) || value.hasEarlier !== (revision > BigInt(25)) || new Set(value.recent.map(row => row.id)).size !== value.recent.length) return false;
    for (let index = 0; index < value.recent.length; index++) {
      const row = value.recent[index]; if (BigInt(row.revision) !== revision - BigInt(index) || index > 0 && Date.parse(row.receivedAt) > Date.parse(value.recent[index - 1].receivedAt)) return false;
      for (const item of row.lines) { const key = item.receiptId + ':' + item.skuId; totals.set(key, (totals.get(key) ?? BigInt(0)) + BigInt(item.receivedBaseUnits)); }
    }
    if ([...totals].some(([key, total]) => { const item = value.limits.find(item => item.receiptId + ':' + item.skuId === key); return !item || total > BigInt(item.receivedBaseUnits); })) return false;
    return value.hasEarlier || value.limits.every(item => (totals.get(item.receiptId + ':' + item.skuId) ?? BigInt(0)) === BigInt(item.receivedBaseUnits));
  });
const result = z.object({ returnId: id, caseId: id, invoiceId: id, revision: id, receivedAt: timestamp, state: z.literal('quarantined'), operation: z.literal('fulfillment.case.return_receive') }).strict();
export type ReturnView = z.infer<typeof view>;
export type ReturnLine = z.infer<typeof line>;
export type ReturnResult = z.infer<typeof result>;
export const returnConditions = { uninspected: 'Belum diperiksa', sealed: 'Segel terlihat utuh', opened: 'Kemasan terbuka', damaged: 'Kerusakan terlihat' };
function invalid(): never { throw new AccessFailure(502, 'return_response_invalid'); }
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const parsed = schema.safeParse(value); return parsed.success ? parsed.data : invalid(); }
export function returnOwner(key: string) { const matched = /^cr\.([1-9][0-9]{0,18})\.[a-zA-Z0-9_-]{1,90}$(?![\s\S])/.exec(key); if (!matched || key.length > 128) return invalid(); return priceId(matched[1]); }
export function parseReturnResult(value: unknown, key: string) { const parsed = parse(result, value); if (parsed.caseId !== returnOwner(key)) return invalid(); return parsed; }
export function parseReturnView(value: unknown, original: AfterSalesCase) {
  const owner = parseCaseView(original), parsed = parse(view, value);
  if (owner.state !== 'return_authorized' || !owner.decision || parsed.caseId !== owner.id || parsed.invoiceId !== owner.source.invoiceId || parsed.limits.length !== owner.decision.lines.length || parsed.limits.some(item => {
    const allowed = owner.decision!.lines.find(line => line.receiptId === item.receiptId && line.skuId === item.skuId); return !allowed || allowed.reportedBaseUnits !== item.authorizedBaseUnits;
  }) || parsed.recent.some(row => Date.parse(row.receivedAt) < Date.parse(owner.decision!.decidedAt!))) return invalid(); return parsed;
}
export function prepareReturn(actorId: string, owner: AfterSalesCase, preview: ReturnView, quantities: readonly ReturnLine[], publicNote: string, reason: string) {
  const reviewed = parseReturnView(preview, owner), values = lines.safeParse(quantities), note = text.safeParse(publicNote), internal = text.safeParse(reason);
  if (!values.success || !note.success || !internal.success || !values.data.every(line => { const item = reviewed.limits.find(item => item.receiptId === line.receiptId && item.skuId === line.skuId); return !!item && BigInt(line.receivedBaseUnits) <= BigInt(item.authorizedBaseUnits) - BigInt(item.receivedBaseUnits); })) throw new AccessFailure(400, 'invalid_returned_stock');
  return Object.freeze({ actorId: priceId(actorId), caseId: owner.id, invoiceId: owner.source.invoiceId, revision: reviewed.revision,
    body: Object.freeze({ binding: reviewed.binding, publicNote, reason, lines: Object.freeze(values.data.toSorted((a, b) => BigInt(a.receiptId) < BigInt(b.receiptId) ? -1 : BigInt(a.receiptId) > BigInt(b.receiptId) ? 1 : BigInt(a.skuId) < BigInt(b.skuId) ? -1 : 1).map(line => Object.freeze(line))) }) });
}
function data<T>(response: { data?: T; error?: unknown; response: Response }): T {
  if (!response.response.ok || response.data === undefined) { const error = response.error && typeof response.error === 'object' ? response.error as Record<string, unknown> : {};
    throw new AccessFailure(response.response.status, typeof error.code === 'string' ? error.code : 'request_failed'); } return response.data;
}
export function createReturnGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': priceId(actorId) });
  return {
    async read(actorId: string, owner: AfterSalesCase, preview = false) {
      const options = { body: { purpose: 'after-sales-return' }, params: { path: { id: priceId(owner.id) }, header: { ...headers(actorId), 'If-Match': '"2"' } } };
      return parseReturnView(data(preview ? await client.POST('/api/v1/fulfillment/cases/{id}/returns/preview/query', options) : await client.POST('/api/v1/fulfillment/cases/{id}/returns/query', options)), owner);
    },
    async receive(command: ReturnType<typeof prepareReturn>, key: string, current: () => boolean) {
      if (returnOwner(key) !== command.caseId) return invalid(); if (!current()) throw new RequestNotSentError(409, 'case_view_stale');
      const parsed = parseReturnResult(data(await client.POST('/api/v1/fulfillment/cases/{id}/returns', { body: { ...command.body, lines: [...command.body.lines] }, params: { path: { id: command.caseId }, header: { ...headers(command.actorId), 'If-Match': '"' + command.revision + '"', 'Idempotency-Key': key } } })), key);
      if (parsed.invoiceId !== command.invoiceId || BigInt(parsed.revision) !== BigInt(command.revision) + BigInt(1)) return invalid(); if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); return parsed;
    },
    async recover(actorId: string, key: string) {
      const caseId = returnOwner(key); const parsed = parse(z.object({ status: z.enum(['not_confirmed', 'committed']), result: result.nullable() }).strict(), data(await client.POST('/api/v1/fulfillment/cases/{id}/returns/operations/query', { body: { purpose: 'operation-recovery', key }, params: { path: { id: caseId }, header: headers(actorId) } })));
      if (parsed.status === 'not_confirmed' && parsed.result === null) return null; if (parsed.status !== 'committed' || parsed.result === null) return invalid(); return { state: 'committed' as const, result: parseReturnResult(parsed.result, key) };
    },
  };
}
