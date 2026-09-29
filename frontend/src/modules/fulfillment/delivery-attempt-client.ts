import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';

export const deliveryOutcomes = { recipient_unavailable: 'Penerima belum tersedia', address_unreachable: 'Alamat tidak dapat dijangkau', delivery_refused: 'Kiriman ditolak saat kunjungan', transport_issue: 'Kendala perjalanan', other: 'Kendala lainnya' } as const;
const outcome = z.enum(['recipient_unavailable', 'address_unreachable', 'delivery_refused', 'transport_issue', 'other']);
const id = z.string().regex(/^[1-9][0-9]{0,18}$(?![\s\S])/).refine(value => BigInt(value) <= BigInt('9223372036854775807'));
const sequence = z.string().regex(/^(0|[1-9][0-9]{0,3})$(?![\s\S])/).refine(value => Number(value) <= 1000);
const date = z.string().max(64).refine(value => Number.isFinite(Date.parse(value)));
const reason = z.string().min(1).max(1000).refine(value => value === value.trim() && !/[\u0000-\u001f\u007f]/.test(value));
const attempt = z.object({ id, shipmentId: id, sequence: sequence.refine(value => value !== '0'), outcome, reason, occurredAt: date, recordedAt: date, revokedQrCount: z.number().int().min(0).max(20) }).strict()
  .refine(value => Date.parse(value.occurredAt) <= Date.parse(value.recordedAt));
const preview = z.object({ shipmentId: id, sequence, received: z.boolean(), dispatchedAt: date, serverTime: date, lastOccurredAt: date.nullable() }).strict()
  .refine(value => Date.parse(value.dispatchedAt) <= Date.parse(value.serverTime) && (value.lastOccurredAt === null ? value.sequence === '0' : value.sequence !== '0' && Date.parse(value.lastOccurredAt) >= Date.parse(value.dispatchedAt) && Date.parse(value.lastOccurredAt) <= Date.parse(value.serverTime)));
const page = z.object({ shipmentId: id, sequence, received: z.boolean(), items: z.array(attempt).max(25), hasMore: z.boolean() }).strict().refine(value =>
  value.items.every((item, index) => item.shipmentId === value.shipmentId && Number(item.sequence) <= Number(value.sequence) && (index === 0 || Number(item.sequence) < Number(value.items[index - 1].sequence))) && (!value.hasMore || value.items.length === 25));
const invalid = (): never => { throw new AccessFailure(502, 'delivery_attempt_response_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown): T { try { return schema.parse(value); } catch { return invalid(); } }
export const parseDeliveryAttempt = (value: unknown) => parse(attempt, value);
export type DeliveryAttempt = z.infer<typeof attempt>;
export type DeliveryPreview = z.infer<typeof preview>;
export type DeliveryHistory = z.infer<typeof page>;
export function prepareDeliveryAttempt(actorId: string, current: DeliveryPreview, result: string, occurredAt: string, note: string) {
  const source = parse(preview, current), details = outcome.safeParse(result), explanation = reason.safeParse(note), time = Date.parse(occurredAt);
  if (source.received || source.sequence === '1000' || !details.success || !explanation.success || !Number.isFinite(time) || time < Date.parse(source.lastOccurredAt ?? source.dispatchedAt)) throw new AccessFailure(400, 'invalid_delivery_attempt');
  return Object.freeze({ actorId: priceId(actorId), shipmentId: source.shipmentId, expectedSequence: source.sequence,
    body: Object.freeze({ outcome: details.data, occurredAt: new Date(time).toISOString(), reason: explanation.data }) });
}
type Command = ReturnType<typeof prepareDeliveryAttempt>;
export function jakartaVisitInput(value: string) { return new Date(Date.parse(value) + 7 * 60 * 60 * 1000).toISOString().slice(0, 23); }
export function jakartaVisitTime(value: string) { if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,3})?)?$(?![\s\S])/.test(value) || !Number.isFinite(Date.parse(value + '+07:00'))) throw new AccessFailure(400, 'invalid_delivery_attempt'); return new Date(value + '+07:00').toISOString(); }
function data<T>(result: { data?: T; error?: unknown; response: Response }): T { if (!result.response.ok || result.data === undefined) {
  const error = result.error && typeof result.error === 'object' ? result.error as Record<string, unknown> : {}; throw new AccessFailure(result.response.status, typeof error.code === 'string' ? error.code : 'request_failed'); } return result.data; }
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': priceId(actorId) });
const key = (value: string) => /^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/.test(value) ? value : invalid();
export function createDeliveryAttemptGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async read(actorId: string, shipmentId: string, beforeSequence: string | null = null, signal?: AbortSignal) {
      const result = parse(page, data(await client.POST('/api/v1/fulfillment/delivery-attempts/{shipmentId}/query', { body: { purpose: 'delivery-attempt', beforeSequence: beforeSequence === null ? null : priceId(beforeSequence) }, params: { path: { shipmentId: priceId(shipmentId) }, header: headers(actorId) }, signal })));
      if (result.shipmentId !== shipmentId || beforeSequence !== null && result.items.some(item => BigInt(item.sequence) >= BigInt(beforeSequence))) return invalid(); return result;
    },
    async preview(actorId: string, shipmentId: string, signal?: AbortSignal) {
      const result = parse(preview, data(await client.POST('/api/v1/fulfillment/delivery-attempts/{shipmentId}/preview', { body: { purpose: 'delivery-attempt' }, params: { path: { shipmentId: priceId(shipmentId) }, header: headers(actorId) }, signal })));
      if (result.shipmentId !== shipmentId) return invalid(); return result;
    },
    async record(command: Command, requestKey: string, current: () => boolean) {
      if (!current()) throw new RequestNotSentError(409, 'delivery_attempt_stale');
      const result = parseDeliveryAttempt(data(await client.POST('/api/v1/fulfillment/delivery-attempts/{shipmentId}', { body: command.body,
        params: { path: { shipmentId: command.shipmentId }, header: { ...headers(command.actorId), 'Idempotency-Key': key(requestKey), 'If-Match': `"${command.expectedSequence}"` } } })));
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown');
      if (result.shipmentId !== command.shipmentId || BigInt(result.sequence) !== BigInt(command.expectedSequence) + BigInt(1) || result.outcome !== command.body.outcome || result.reason !== command.body.reason || Date.parse(result.occurredAt) !== Date.parse(command.body.occurredAt)) return invalid(); return result;
    },
    async recover(actorId: string, requestKey: string) {
      const result = parse(z.object({ status: z.enum(['not_confirmed', 'committed']), attempt: attempt.nullable() }).strict(), data(await client.POST('/api/v1/fulfillment/delivery-attempts/operations/query', { body: { purpose: 'operation-recovery', key: key(requestKey) }, params: { header: headers(actorId) } })));
      if (result.status === 'not_confirmed' && result.attempt === null) return null;
      if (result.status !== 'committed' || result.attempt === null) return invalid(); return { state: 'committed' as const, result: result.attempt };
    },
  };
}
