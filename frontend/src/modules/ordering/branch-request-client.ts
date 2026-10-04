import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure, stableContent } from '../administration/workflow';
import { parseBuyerVariant, type BuyerVariant } from '../catalog/catalog-buyer-client';
import { branchRequestInput, requestContextKey, requestContextSchema, requestIdSchema, requestInputSchema, requestResult, requestSelectionSchema, requestText, requestTimeSchema,
  type BranchRequestAction, type BranchRequestContext, type BranchRequestInput, type BranchRequestOperation } from './branch-request-state';

const invalid = (): never => { throw new AccessFailure(502, 'branch_request_response_invalid'); };
const ownerSchema = z.object({ kind: z.enum(['fulfillmentShop', 'fulfillmentWarehouse']), organizationId: requestIdSchema, branchId: z.null(), divisionId: z.null(),
  shopId: requestIdSchema, warehouseId: requestIdSchema.nullable(), channelId: requestIdSchema }).strict().refine(owner => (owner.kind === 'fulfillmentWarehouse') === (owner.warehouseId !== null));
const labelsSchema = z.object({ buyingOrganization: requestText(200), branch: requestText(200), division: requestText(200).nullable(),
  fulfillmentOrganization: requestText(200), shop: requestText(200), warehouse: requestText(200).nullable(), channel: requestText(200) }).strict();
const lineSchema = requestSelectionSchema.extend({ skuCode: requestText(64), familyName: requestText(160), packageLabel: requestText(160), packageKind: z.enum(['sealed', 'loose', 'other']),
  baseUnitCode: requestText(64), baseUnitsPerPackage: z.number().int().min(1).max(2147483647), owner: ownerSchema }).strict();
const sourceSchema = z.object({ input: requestInputSchema, lines: z.array(lineSchema).min(1).max(100), owner: ownerSchema, labels: labelsSchema });
const hashSchema = z.string().regex(/^[a-f0-9]{64}$(?![\s\S])/u);
const previewSchema = sourceSchema.extend({ binding: hashSchema, observedAt: requestTimeSchema }).strict();
const viewSchema = sourceSchema.extend({ id: requestIdSchema, number: requestText(40), version: z.enum(['1', '2']), state: z.enum(['pending', 'accepted', 'rejected']),
  createdBy: requestIdSchema, recordedAt: requestTimeSchema, decision: z.object({ id: requestIdSchema, action: z.enum(['accept', 'reject']), reason: requestText(1000), recordedAt: requestTimeSchema }).strict().nullable() }).strict();
function parsed<T>(schema: z.ZodType<T>, value: unknown): T { const result = schema.safeParse(value); return result.success ? result.data : invalid(); }
function source<T extends z.infer<typeof sourceSchema>>(value: T): T {
  if (value.lines.length !== value.input.lines.length || value.lines.some((line, index) => stableContent(requestSelectionSchema.parse({ skuId: line.skuId, definitionId: line.definitionId, definitionVersion: line.definitionVersion, packages: line.packages })) !== stableContent(value.input.lines[index]) || stableContent(line.owner) !== stableContent(value.owner)) ||
    (value.input.context.divisionId === null) !== (value.labels.division === null) || (value.owner.warehouseId === null) !== (value.labels.warehouse === null)) return invalid();
  return value;
}
export function parseBranchRequestPreview(value: unknown, expected?: BranchRequestInput) {
  const result = source(parsed(previewSchema, value));
  if (expected && stableContent(result.input) !== stableContent(branchRequestInput(expected))) return invalid();
  return result;
}
export type BranchRequestPreview = ReturnType<typeof parseBranchRequestPreview>;
export function parseBranchRequest(value: unknown) {
  const row = source(parsed(viewSchema, value));
  if (row.number !== 'PT-' + row.id.padStart(8, '0') || (row.state === 'pending' ? row.version !== '1' || row.decision !== null : row.version !== '2' || row.decision === null || (row.state === 'accepted') !== (row.decision.action === 'accept')) ||
    row.decision && Date.parse(row.decision.recordedAt) < Date.parse(row.recordedAt)) return invalid();
  return row;
}
export type BranchRequest = ReturnType<typeof parseBranchRequest>;
export function parseBranchRequestDecision(value: unknown, id: string, action: BranchRequestAction) {
  const row = parsed(z.object({ request: z.unknown(), decision: z.enum(['accept', 'reject']), canDecide: z.boolean(), binding: hashSchema, observedAt: requestTimeSchema }).strict(), value), request = parseBranchRequest(row.request);
  if (request.id !== id || row.decision !== action || row.canDecide && request.state !== 'pending') return invalid();
  return { ...row, request };
}
export type BranchRequestDecisionPreview = ReturnType<typeof parseBranchRequestDecision>;
export function parseBranchRequestPage(value: unknown, before: string | null = null) {
  const row = parsed(z.object({ items: z.array(z.unknown()).max(25), nextRequestId: requestIdSchema.nullable() }).strict(), value), items = row.items.map(parseBranchRequest);
  let cursor = before === null ? null : BigInt(requestIdSchema.parse(before));
  for (const item of items) { if (cursor !== null && BigInt(item.id) >= cursor) return invalid(); cursor = BigInt(item.id); }
  if (row.nextRequestId !== null && (items.length !== 25 || row.nextRequestId !== items.at(-1)?.id)) return invalid();
  return { items, nextRequestId: row.nextRequestId };
}
const contextOptionSchema = z.object({ context: requestContextSchema, organizationName: requestText(200).nullable(), branchName: requestText(200).nullable(), divisionName: requestText(200).nullable() }).strict();
export type BranchRequestContextOption = z.infer<typeof contextOptionSchema>;
export function requestContextLabel(option: BranchRequestContextOption) { return [option.organizationName ?? 'Organisasi pemesan', option.branchName ?? 'Cabang pemesan', option.divisionName].filter(Boolean).join(' · '); }
export type BranchRequestPackagePage = { total: string; items: BuyerVariant[] };
function data<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) {
    const error = result.error && typeof result.error === 'object' ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof error.code === 'string' ? error.code : 'request_failed');
  }
  return result.data;
}
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': requestIdSchema.parse(actorId) });
function key(value: string) { if (!/^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/u.test(value)) throw new AccessFailure(400, 'invalid_branch_request_recovery'); return value; }
const purpose = 'branch-request-review';

export function createBranchRequestGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async contexts(actorId: string, signal?: AbortSignal) {
      const row = parsed(z.object({ items: z.array(contextOptionSchema).max(256) }).strict(), data(await client.POST('/api/v1/ordering/requests/contexts/query', { body: { purpose }, params: { header: headers(actorId) }, signal })));
      if (new Set(row.items.map(item => requestContextKey(item.context))).size !== row.items.length) return invalid(); return row.items;
    },
    async packages(actorId: string, context: BranchRequestContext, search = '', offset = 0, signal?: AbortSignal): Promise<BranchRequestPackagePage> {
      if (!Number.isSafeInteger(offset) || offset < 0 || offset > 10000 || search.length > 128) throw new AccessFailure(400, 'invalid_branch_order_request');
      const row = parsed(z.object({ total: z.string().regex(/^(0|[1-9][0-9]{0,18})$(?![\s\S])/u), items: z.array(z.unknown()).max(25) }).strict(), data(await client.POST('/api/v1/ordering/requests/packages/query', {
        body: { purpose, context: requestContextSchema.parse(context), search: search.trim(), offset, limit: 25 }, params: { header: headers(actorId) }, signal })));
      const items = row.items.map(parseBuyerVariant);
      if (BigInt(row.total) < BigInt(items.length) || new Set(items.map(item => item.skuId)).size !== items.length) return invalid(); return { total: row.total, items };
    },
    async preview(actorId: string, input: BranchRequestInput, signal?: AbortSignal) {
      const frozen = branchRequestInput(input);
      return parseBranchRequestPreview(data(await client.POST('/api/v1/ordering/requests/preview/query', { body: { purpose, input: frozen }, params: { header: headers(actorId) }, signal })), frozen);
    },
    async submit(actorId: string, preview: BranchRequestPreview, requestKey: string, current: () => boolean) {
      const frozen = parseBranchRequestPreview(preview);
      if (!current()) throw new RequestNotSentError(409, 'branch_request_view_stale');
      const result = requestResult(data(await client.POST('/api/v1/ordering/requests', { body: { input: frozen.input, binding: frozen.binding, reason: 'Pemesan mengonfirmasi cabang, kemasan, jumlah, dan toko tujuan' }, params: { header: { ...headers(actorId), 'Idempotency-Key': key(requestKey), 'If-Match': '"0"' } } })));
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown');
      if (result.state !== 'pending') return invalid(); return result;
    },
    async list(actorId: string, view: 'mine' | 'incoming', beforeRequestId: string | null = null, signal?: AbortSignal) {
      return parseBranchRequestPage(data(await client.POST('/api/v1/ordering/requests/query', { body: { purpose: 'branch-request-history', view, beforeRequestId }, params: { header: headers(actorId) }, signal })), beforeRequestId);
    },
    async read(actorId: string, id: string, signal?: AbortSignal) {
      const row = parseBranchRequest(data(await client.POST('/api/v1/ordering/requests/{id}/query', { body: { purpose: 'branch-request-history' }, params: { path: { id: requestIdSchema.parse(id) }, header: headers(actorId) }, signal })));
      if (row.id !== id) return invalid(); return row;
    },
    async previewDecision(actorId: string, id: string, action: BranchRequestAction, signal?: AbortSignal) {
      return parseBranchRequestDecision(data(await client.POST('/api/v1/ordering/requests/{id}/decisions/preview/query', { body: { purpose: 'branch-request-decision', decision: action }, params: { path: { id: requestIdSchema.parse(id) }, header: headers(actorId) }, signal })), id, action);
    },
    async decide(actorId: string, preview: BranchRequestDecisionPreview, reason: string, requestKey: string, current: () => boolean) {
      const frozen = parseBranchRequestDecision(preview, preview.request.id, preview.decision);
      if (!frozen.canDecide || !current()) throw new RequestNotSentError(409, 'branch_request_view_stale');
      const result = requestResult(data(await client.POST('/api/v1/ordering/requests/{id}/decisions', { body: { decision: frozen.decision, binding: frozen.binding, reason: requestText(1000).parse(reason) },
        params: { path: { id: frozen.request.id }, header: { ...headers(actorId), 'Idempotency-Key': key(requestKey), 'If-Match': '"' + frozen.request.version + '"' } } })));
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown');
      if (result.requestId !== frozen.request.id || result.state !== (frozen.decision === 'accept' ? 'accepted' : 'rejected')) return invalid(); return result;
    },
    async recover(actorId: string, operation: BranchRequestOperation, requestKey: string, signal?: AbortSignal) {
      const row = parsed(z.object({ state: z.enum(['confirmed', 'not_confirmed']), result: z.unknown() }).strict(), data(await client.POST('/api/v1/ordering/requests/operations/query', {
        body: { purpose: 'operation-recovery', operation, key: key(requestKey) }, params: { header: headers(actorId) }, signal })));
      if (row.state === 'not_confirmed') { if (row.result !== null) return invalid(); return null; }
      const result = requestResult(row.result);
      if ((operation === 'ordering.branchrequest.submit') !== (result.state === 'pending')) return invalid();
      return { state: 'committed' as const, result };
    },
  };
}
