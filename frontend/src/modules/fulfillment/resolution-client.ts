import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';
import type { AfterSalesCase } from './case-client';
import { parseInspectionView, parseInspectionAssessment, type InspectionView } from './inspection-client';

const id = z.string().regex(/^[1-9][0-9]{0,18}$(?![\s\S])/).refine(value => BigInt(value) <= BigInt('9223372036854775807'));
const action = z.enum(['replacement', 'credit', 'refund']), timestamp = z.string().datetime({ offset: true });
const text = z.string().min(1).max(1000).refine(value => value === value.trim() && !/[\p{Cc}\p{Cf}]/u.test(value));
const request = z.object({ requestedAt: timestamp, action, publicNote: text, state: z.literal('resolution_requested') }).strict();
const result = z.object({ returnId: id, caseId: id, invoiceId: id, revision: z.literal('1'), requestedAt: timestamp, action, state: z.literal('resolution_requested'), operation: z.literal('fulfillment.case.resolution_request') }).strict();
export type ResolutionAction = z.infer<typeof action>;
export type ResolutionResult = z.infer<typeof result>;
export type ResolutionView = { caseId: string; invoiceId: string; returnId: string; revision: '0' | '1'; binding: string; inspection: ReturnType<typeof parseInspectionAssessment>; request: z.infer<typeof request> | null };
export const resolutionActions = { replacement: 'Penggantian barang', credit: 'Kredit tagihan', refund: 'Pengembalian dana' };
function invalid(): never { throw new AccessFailure(502, 'resolution_response_invalid'); }
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const parsed = schema.safeParse(value); return parsed.success ? parsed.data : invalid(); }
export function resolutionOwner(key: string) {
  const matched = /^cq\.([1-9][0-9]{0,18})\.([1-9][0-9]{0,18})\.[a-zA-Z0-9_-]{1,68}$(?![\s\S])/.exec(key); if (!matched || key.length > 128) return invalid(); return { caseId: priceId(matched[1]), returnId: priceId(matched[2]) };
}
export function parseResolutionResult(value: unknown, key: string) { const parsed = parse(result, value), owner = resolutionOwner(key); if (parsed.caseId !== owner.caseId || parsed.returnId !== owner.returnId) return invalid(); return parsed; }
export function parseResolutionView(value: unknown, original: AfterSalesCase, originalInspection: InspectionView): ResolutionView {
  const checked = parseInspectionView(originalInspection, original, originalInspection.receipt.id); if (!checked.inspection) return invalid();
  const parsed = parse(z.object({ caseId: id, invoiceId: id, returnId: id, revision: z.enum(['0', '1']), binding: z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/), inspection: z.unknown(), request: request.nullable() }).strict(), value);
  const inspected = parseInspectionAssessment(parsed.inspection);
  if (parsed.caseId !== original.id || parsed.invoiceId !== original.source.invoiceId || parsed.returnId !== checked.receipt.id || (parsed.revision === '0') !== (parsed.request === null) ||
    inspected.inspectedAt !== checked.inspection.inspectedAt || inspected.publicNote !== checked.inspection.publicNote || JSON.stringify(inspected.lines) !== JSON.stringify(checked.inspection.lines) ||
    parsed.request && Date.parse(parsed.request.requestedAt) < Date.parse(inspected.inspectedAt)) return invalid(); return { ...parsed, inspection: inspected };
}
export function prepareResolution(actorId: string, original: AfterSalesCase, inspection: InspectionView, preview: ResolutionView, requestedAction: ResolutionAction, publicNote: string, reason: string) {
  const checked = parseResolutionView(preview, original, inspection), requested = action.safeParse(requestedAction), note = text.safeParse(publicNote), internal = text.safeParse(reason);
  if (checked.revision !== '0' || !requested.success || !note.success || !internal.success) throw new AccessFailure(400, 'invalid_return_resolution');
  return Object.freeze({ actorId: priceId(actorId), caseId: checked.caseId, returnId: checked.returnId, invoiceId: checked.invoiceId, revision: '0' as const,
    body: Object.freeze({ binding: checked.binding, action: requested.data, publicNote, reason }) });
}
function data<T>(response: { data?: T; error?: unknown; response: Response }): T {
  if (!response.response.ok || response.data === undefined) { const error = response.error && typeof response.error === 'object' ? response.error as Record<string, unknown> : {};
    throw new AccessFailure(response.response.status, typeof error.code === 'string' ? error.code : 'request_failed'); } return response.data;
}
export function createResolutionGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': priceId(actorId) });
  return {
    async read(actorId: string, original: AfterSalesCase, inspection: InspectionView, requestedAction: ResolutionAction | null = null) {
      const params = { path: { id: priceId(original.id), returnId: priceId(inspection.receipt.id) }, header: { ...headers(actorId), 'If-Match': '"2"' } };
      const response = requestedAction === null ? await client.POST('/api/v1/fulfillment/cases/{id}/returns/{returnId}/resolution/query', { body: { purpose: 'after-sales-resolution' }, params })
        : await client.POST('/api/v1/fulfillment/cases/{id}/returns/{returnId}/resolution/preview/query', { body: { purpose: 'after-sales-resolution', action: parse(action, requestedAction) }, params });
      return parseResolutionView(data(response), original, inspection);
    },
    async request(command: ReturnType<typeof prepareResolution>, key: string, current: () => boolean) {
      const owner = resolutionOwner(key); if (owner.caseId !== command.caseId || owner.returnId !== command.returnId) return invalid(); if (!current()) throw new RequestNotSentError(409, 'case_view_stale');
      const saved = parseResolutionResult(data(await client.POST('/api/v1/fulfillment/cases/{id}/returns/{returnId}/resolution', { body: command.body, params: { path: { id: command.caseId, returnId: command.returnId }, header: { ...headers(command.actorId), 'If-Match': '"0"', 'Idempotency-Key': key } } })), key);
      if (saved.invoiceId !== command.invoiceId || saved.action !== command.body.action) return invalid(); if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); return saved;
    },
    async recover(actorId: string, key: string) {
      const owner = resolutionOwner(key), parsed = parse(z.object({ status: z.enum(['not_confirmed', 'committed']), result: result.nullable() }).strict(), data(await client.POST('/api/v1/fulfillment/cases/{id}/returns/{returnId}/resolution/operations/query', { body: { purpose: 'operation-recovery', key }, params: { path: { id: owner.caseId, returnId: owner.returnId }, header: headers(actorId) } })));
      if (parsed.status === 'not_confirmed' && parsed.result === null) return null; if (parsed.status !== 'committed' || parsed.result === null) return invalid(); return { state: 'committed' as const, result: parseResolutionResult(parsed.result, key) };
    },
  };
}
