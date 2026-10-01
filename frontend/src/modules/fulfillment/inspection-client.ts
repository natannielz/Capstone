import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';
import { parseCaseView, type AfterSalesCase } from './case-client';
import { parseReturnReceipt, type ReturnReceipt } from './return-client';

const id = z.string().regex(/^[1-9][0-9]{0,18}$(?![\s\S])/).refine(value => BigInt(value) <= BigInt('9223372036854775807'));
const quantity = id.refine(value => BigInt(value) <= BigInt(1000000));
const text = (max: number) => z.string().min(1).max(max).refine(value => value === value.trim() && !/[\p{Cc}\p{Cf}]/u.test(value));
const identity = z.enum(['matched', 'different', 'unverified']), condition = z.enum(['sealed', 'opened', 'damaged', 'mixed']);
const publicLine = z.object({ receiptId: id, skuId: id, inspectedBaseUnits: quantity, identity, condition }).strict();
const line = publicLine.extend({ observedIdentity: text(160), observedLotLabel: text(160).nullable() }).strict();
const unique = (lines: readonly { receiptId: string; skuId: string }[]) => new Set(lines.map(line => line.receiptId + ':' + line.skuId)).size === lines.length;
const lines = z.array(line).min(1).max(100).refine(unique);
const assessment = z.object({ inspectedAt: z.string().datetime({ offset: true }), publicNote: text(1000), lines: z.array(publicLine).min(1).max(100).refine(unique) }).strict();
const result = z.object({ returnId: id, caseId: id, invoiceId: id, revision: z.literal('1'), inspectedAt: z.string().datetime({ offset: true }), state: z.literal('inspected_quarantine'), operation: z.literal('fulfillment.case.return_inspect') }).strict();
export type InspectionLine = z.infer<typeof line>;
export type InspectionResult = z.infer<typeof result>;
export function parseInspectionAssessment(value: unknown) { return parse(assessment, value); }
export type InspectionView = { caseId: string; invoiceId: string; revision: '0' | '1'; binding: string; receipt: ReturnReceipt; inspection: z.infer<typeof assessment> | null };
export const inspectionIdentities = { unverified: 'Identitas belum dapat dipastikan', matched: 'Sesuai produk pada invoice', different: 'Produk berbeda dari invoice' };
export const inspectionConditions = { sealed: 'Segel utuh', opened: 'Kemasan terbuka', damaged: 'Rusak', mixed: 'Kondisi campuran — tetap karantina' };
function invalid(): never { throw new AccessFailure(502, 'inspection_response_invalid'); }
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const parsed = schema.safeParse(value); return parsed.success ? parsed.data : invalid(); }
export function inspectionOwner(key: string) {
  const matched = /^ci\.([1-9][0-9]{0,18})\.([1-9][0-9]{0,18})\.[a-zA-Z0-9_-]{1,68}$(?![\s\S])/.exec(key);
  if (!matched || key.length > 128) return invalid(); return { caseId: priceId(matched[1]), returnId: priceId(matched[2]) };
}
export function parseInspectionResult(value: unknown, key: string) {
  const parsed = parse(result, value), owner = inspectionOwner(key); if (parsed.caseId !== owner.caseId || parsed.returnId !== owner.returnId) return invalid(); return parsed;
}
export function parseInspectionView(value: unknown, original: AfterSalesCase, expectedReturnId: string): InspectionView {
  const owner = parseCaseView(original), parsed = parse(z.object({ caseId: id, invoiceId: id, revision: z.enum(['0', '1']), binding: z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/), receipt: z.unknown(), inspection: assessment.nullable() }).strict(), value);
  const receipt = parseReturnReceipt(parsed.receipt, owner);
  if (parsed.caseId !== owner.id || parsed.invoiceId !== owner.source.invoiceId || receipt.id !== priceId(expectedReturnId) || (parsed.revision === '0') !== (parsed.inspection === null) || parsed.inspection &&
      (Date.parse(parsed.inspection.inspectedAt) < Date.parse(receipt.receivedAt) || parsed.inspection.lines.length !== receipt.lines.length || parsed.inspection.lines.some(line => !receipt.lines.some(item => item.receiptId === line.receiptId && item.skuId === line.skuId && item.receivedBaseUnits === line.inspectedBaseUnits)))) return invalid();
  return { ...parsed, receipt };
}
export function prepareInspection(actorId: string, original: AfterSalesCase, preview: InspectionView, values: readonly InspectionLine[], publicNote: string, reason: string) {
  const reviewed = parseInspectionView(preview, original, preview.receipt.id), items = lines.safeParse(values), note = text(1000).safeParse(publicNote), internal = text(1000).safeParse(reason);
  if (reviewed.revision !== '0' || !items.success || !note.success || !internal.success || items.data.length !== reviewed.receipt.lines.length || items.data.some(line => !reviewed.receipt.lines.some(item => item.receiptId === line.receiptId && item.skuId === line.skuId && item.receivedBaseUnits === line.inspectedBaseUnits))) throw new AccessFailure(400, 'invalid_return_inspection');
  return Object.freeze({ actorId: priceId(actorId), caseId: reviewed.caseId, returnId: reviewed.receipt.id, invoiceId: reviewed.invoiceId, revision: '0' as const,
    body: Object.freeze({ binding: reviewed.binding, publicNote, reason, lines: Object.freeze(items.data.toSorted((a, b) => BigInt(a.receiptId) < BigInt(b.receiptId) ? -1 : BigInt(a.receiptId) > BigInt(b.receiptId) ? 1 : BigInt(a.skuId) < BigInt(b.skuId) ? -1 : 1).map(line => Object.freeze(line))) }) });
}
function data<T>(response: { data?: T; error?: unknown; response: Response }): T {
  if (!response.response.ok || response.data === undefined) { const error = response.error && typeof response.error === 'object' ? response.error as Record<string, unknown> : {};
    throw new AccessFailure(response.response.status, typeof error.code === 'string' ? error.code : 'request_failed'); } return response.data;
}
export function createInspectionGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': priceId(actorId) });
  return {
    async read(actorId: string, original: AfterSalesCase, returnId: string, preview = false) {
      const options = { body: { purpose: 'after-sales-return-inspection' }, params: { path: { id: priceId(original.id), returnId: priceId(returnId) }, header: { ...headers(actorId), 'If-Match': '"2"' } } };
      return parseInspectionView(data(preview ? await client.POST('/api/v1/fulfillment/cases/{id}/returns/{returnId}/inspection/preview/query', options) : await client.POST('/api/v1/fulfillment/cases/{id}/returns/{returnId}/inspection/query', options)), original, returnId);
    },
    async inspect(command: ReturnType<typeof prepareInspection>, key: string, current: () => boolean) {
      const owner = inspectionOwner(key); if (owner.caseId !== command.caseId || owner.returnId !== command.returnId) return invalid(); if (!current()) throw new RequestNotSentError(409, 'case_view_stale');
      const parsed = parseInspectionResult(data(await client.POST('/api/v1/fulfillment/cases/{id}/returns/{returnId}/inspection', { body: { ...command.body, lines: [...command.body.lines] }, params: { path: { id: command.caseId, returnId: command.returnId }, header: { ...headers(command.actorId), 'If-Match': '"0"', 'Idempotency-Key': key } } })), key);
      if (parsed.invoiceId !== command.invoiceId) return invalid(); if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); return parsed;
    },
    async recover(actorId: string, key: string) {
      const owner = inspectionOwner(key); const parsed = parse(z.object({ status: z.enum(['not_confirmed', 'committed']), result: result.nullable() }).strict(), data(await client.POST('/api/v1/fulfillment/cases/{id}/returns/{returnId}/inspection/operations/query', { body: { purpose: 'operation-recovery', key }, params: { path: { id: owner.caseId, returnId: owner.returnId }, header: headers(actorId) } })));
      if (parsed.status === 'not_confirmed' && parsed.result === null) return null; if (parsed.status !== 'committed' || parsed.result === null) return invalid(); return { state: 'committed' as const, result: parseInspectionResult(parsed.result, key) };
    },
  };
}
