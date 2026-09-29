import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure, stableContent } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';
import { parseOrderContext, orderContextKey, type OrderSelection } from './order-quote-client';

const invalid = (): never => { throw new AccessFailure(502, 'order_draft_response_invalid'); };
function record(value: unknown): Record<string, unknown> { return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : invalid(); }
function text(value: unknown, limit: number, multiline = false): string {
  return typeof value === 'string' && value.length > 0 && value.length <= limit && value === value.trim() &&
    !(multiline ? /[\u0000-\u0009\u000b-\u001f\u007f]/ : /[\u0000-\u001f\u007f]/).test(value) ? value : invalid();
}
function optional(value: unknown, limit: number, multiline = false) { return value === null ? null : text(value, limit, multiline); }
function time(value: unknown) { const result = text(value, 64); return Number.isFinite(Date.parse(result)) ? result : invalid(); }
function line(value: unknown): OrderSelection {
  const row = record(value);
  if (typeof row.packages !== 'number' || !Number.isSafeInteger(row.packages) || row.packages < 1 || row.packages > 1_000_000) return invalid();
  return { skuId: priceId(row.skuId), definitionId: priceId(row.definitionId), definitionVersion: priceId(row.definitionVersion), priceId: priceId(row.priceId), priceVersion: priceId(row.priceVersion), packages: row.packages };
}
export function parseDraftInput(value: unknown) {
  const row = record(value), context = parseOrderContext(row.context);
  if (!Array.isArray(row.selections) || row.selections.length === 0 || row.selections.length > 100) return invalid();
  const selections = row.selections.map(line); if (new Set(selections.map(item => item.skuId)).size !== selections.length) return invalid();
  const supplied = row.delivery === null ? null : record(row.delivery);
  const delivery = supplied === null ? null : { recipientName: text(supplied.recipientName, 160), phone: text(supplied.phone, 32), address: text(supplied.address, 1000, true),
    city: text(supplied.city, 120), postalCode: optional(supplied.postalCode, 5), instructions: optional(supplied.instructions, 1000, true) };
  if (delivery && (!/^\+?[0-9][0-9 ()-]*$(?![\s\S])/.test(delivery.phone) || delivery.phone.replace(/[^0-9]/g, '').length < 7 ||
    delivery.phone.replace(/[^0-9]/g, '').length > 15 || delivery.postalCode !== null && !/^[0-9]{5}$(?![\s\S])/.test(delivery.postalCode))) return invalid();
  return { context, selections, delivery, purchaseReference: optional(row.purchaseReference, 160) };
}
export type DraftInput = ReturnType<typeof parseDraftInput>;
export function parseDraft(value: unknown) {
  const row = record(value), createdAt = time(row.createdAt), updatedAt = time(row.updatedAt);
  if (Date.parse(updatedAt) < Date.parse(createdAt)) return invalid();
  return { id: priceId(row.id), revision: priceId(row.revision), createdAt, updatedAt, input: parseDraftInput(row.input) };
}
export type OrderDraft = ReturnType<typeof parseDraft>;
export function parseDraftResult(value: unknown) {
  const row = record(value); return { draftId: priceId(row.draftId), versionId: priceId(row.versionId), revision: priceId(row.revision), recordedAt: time(row.recordedAt) };
}
export type DraftResult = ReturnType<typeof parseDraftResult>;
export function prepareDraftCommand(actorId: string, draftId: string | null, expectedRevision: string, input: DraftInput, reason: string) {
  try {
    if (draftId === null ? expectedRevision !== '0' : priceId(expectedRevision) === '9223372036854775807') throw new AccessFailure(400, 'invalid_order_draft_revision');
    return { actorId: priceId(actorId), draftId: draftId === null ? null : priceId(draftId), expectedRevision, input: parseDraftInput(input), reason: text(reason, 1000) };
  } catch { throw new AccessFailure(400, 'invalid_order_draft'); }
}
export type DraftCommand = ReturnType<typeof prepareDraftCommand>;
function data<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) {
    const error = result.error && typeof result.error === 'object' ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof error.code === 'string' ? error.code : 'request_failed', typeof error.traceId === 'string' ? error.traceId : undefined);
  } return result.data;
}
function key(value: string) { if (!/^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/.test(value)) throw new AccessFailure(400, 'invalid_order_draft_key'); return value; }
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': priceId(actorId) });
export function createOrderDraftGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async list(actorId: string, context: DraftInput['context'], beforeId: string | null = null, signal?: AbortSignal) {
      const row = record(data(await client.POST('/api/v1/ordering/drafts/query', { body: { purpose: 'order-preparation', context: parseOrderContext(context), beforeId: beforeId === null ? null : priceId(beforeId), limit: 25 },
        params: { header: headers(actorId) }, signal })));
      if (!Array.isArray(row.items) || row.items.length > 25 || typeof row.hasMore !== 'boolean') return invalid();
      const items = row.items.map(parseDraft);
      if (items.some((item, index) => orderContextKey(item.input.context) !== orderContextKey(context) ||
        (index > 0 || beforeId !== null) && BigInt(item.id) >= BigInt(index > 0 ? items[index - 1].id : beforeId!)) || row.hasMore && items.length !== 25) return invalid();
      return { items, hasMore: row.hasMore };
    },
    async read(actorId: string, id: string, signal?: AbortSignal) {
      const draft = parseDraft(data(await client.POST('/api/v1/ordering/drafts/{id}/query', { body: { purpose: 'order-preparation' },
        params: { path: { id: priceId(id) }, header: headers(actorId) }, signal })));
      return draft.id === id ? draft : invalid();
    },
    async submit(command: DraftCommand, requestKey: string, isCurrent: () => boolean = () => true) {
      const frozen = prepareDraftCommand(command.actorId, command.draftId, command.expectedRevision, command.input, command.reason);
      if (stableContent(frozen) !== stableContent(command)) throw new AccessFailure(400, 'invalid_order_draft');
      if (!isCurrent()) throw new RequestNotSentError(409, 'order_draft_view_stale');
      const result = parseDraftResult(data(await client.POST('/api/v1/ordering/drafts', { body: { draftId: frozen.draftId, input: frozen.input, reason: frozen.reason },
        params: { header: { ...headers(frozen.actorId), 'Idempotency-Key': key(requestKey), 'If-Match': '"' + frozen.expectedRevision + '"' } } })));
      if (!isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown');
      if (frozen.draftId !== null && result.draftId !== frozen.draftId || BigInt(result.revision) !== BigInt(frozen.expectedRevision) + BigInt(1)) return invalid();
      return result;
    },
    async recover(actorId: string, requestKey: string, signal?: AbortSignal) {
      const row = record(data(await client.POST('/api/v1/ordering/drafts/operations/query', { body: { purpose: 'operation-recovery', key: key(requestKey) }, params: { header: headers(actorId) }, signal })));
      if (row.status === 'not_confirmed' && row.result === null) return null;
      if (row.status !== 'committed') return invalid();
      return { state: 'committed' as const, result: parseDraftResult(row.result) };
    },
  };
}
