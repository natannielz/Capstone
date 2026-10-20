import {z} from 'zod';
import {fetchCsrfToken, RequestNotSentError} from '../../api/client';
import {AccessFailure} from '../administration/workflow';

const idPattern = /^[1-9][0-9]{0,18}$(?![\s\S])/;
const id = z.string().regex(idPattern).refine(value => idPattern.test(value) && BigInt(value) <= BigInt('9223372036854775807'));
const revision = id.refine(value => idPattern.test(value) && BigInt(value) <= BigInt(1001));
const name = z.string().min(1).max(256).refine(value => value === value.trim() && !/\p{Cc}/u.test(value));
const time = z.string().datetime({offset: true}).refine(value => /(?:Z|\+00:00)$/.test(value) && Number.isFinite(Date.parse(value)));
const reason = z.string().min(1).max(2000).refine(value => value.trim().length > 0);
const key = z.string().regex(/^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/);
const review = z.string().regex(/^[a-f0-9]{64}$(?![\s\S])/);
export const currentCourierSchema = z.object({revision, courierId: id, courierName: name, effectiveAt: time}).strict();
const changeSchema = z.object({id, shipmentId: id, revision: revision.refine(value => idPattern.test(value) && BigInt(value) >= BigInt(2)),
  previousCourierId: id, courierId: id, courierName: name, recordedAt: time, reason}).strict()
  .refine(row => row.previousCourierId !== row.courierId);
const taskSchema = z.object({shipmentId: id, orderId: id, suborderId: id, numberEntity: id, numberChannel: id,
  numberYear: z.string().regex(/^[1-9][0-9]{0,3}$(?![\s\S])/), number: id, departureCourierId: id, departureCourierName: name,
  currentAssignment: currentCourierSchema, received: z.boolean(), canHandover: z.boolean(), history: z.array(changeSchema).max(1000)}).strict();
const pendingSchema = z.object({actorId: id, shipmentId: id, expectedRevision: revision, previousCourierId: id, courierId: id,
  key, createdAt: time}).strict().refine(value => value.previousCourierId !== value.courierId);
export type CurrentCourier = z.infer<typeof currentCourierSchema>;
export type CourierChange = z.infer<typeof changeSchema>;
export type CourierTask = z.infer<typeof taskSchema>;
export type CourierPending = z.infer<typeof pendingSchema>;
export type CourierChoice = {userId: string; displayName: string};
export type CourierPreview = {task: CourierTask; courierId: string; courierName: string; review: string};
export type CourierContext = {actorId: string; isCurrent: () => boolean; signal?: AbortSignal};
const invalid = (): never => {throw new AccessFailure(502, 'courier_assignment_response_invalid');};
function parse<T>(schema: z.ZodType<T>, value: unknown): T {const checked = schema.safeParse(value); return checked.success ? checked.data : invalid();}
function input<T>(schema: z.ZodType<T>, value: unknown): T {
  const checked = schema.safeParse(value); if (!checked.success) throw new RequestNotSentError(400, 'courier_assignment_input_invalid'); return checked.data;
}
export function parseCurrentCourier(value: unknown): CurrentCourier {return parse(currentCourierSchema, value);}
export function currentCourierMatchesDeparture(value: CurrentCourier, courierId: string, courierName: string, dispatchedAt: string) {
  return Date.parse(value.effectiveAt) >= Date.parse(dispatchedAt) && (value.revision !== '1' ||
    value.courierId === courierId && value.courierName === courierName && Date.parse(value.effectiveAt) === Date.parse(dispatchedAt));
}
export function parseCourierTask(value: unknown): CourierTask {
  const row = parse(taskSchema, value);
  if (row.received && row.canHandover || BigInt(row.history.length) + BigInt(1) !== BigInt(row.currentAssignment.revision)) return invalid();
  let priorCourier = row.departureCourierId, priorTime = -Infinity; const eventIds = new Set<string>();
  for (const [index, event] of row.history.entries()) {
    if (event.shipmentId !== row.shipmentId || event.revision !== String(index + 2) || event.previousCourierId !== priorCourier ||
        eventIds.has(event.id) || Date.parse(event.recordedAt) < priorTime) return invalid();
    eventIds.add(event.id); priorCourier = event.courierId; priorTime = Date.parse(event.recordedAt);
  }
  const last = row.history.at(-1);
  if (last ? last.courierId !== row.currentAssignment.courierId || last.courierName !== row.currentAssignment.courierName ||
    Date.parse(last.recordedAt) !== Date.parse(row.currentAssignment.effectiveAt) :
    row.currentAssignment.courierId !== row.departureCourierId || row.currentAssignment.courierName !== row.departureCourierName) return invalid();
  return row;
}
export function parseCourierPending(value: unknown, actorId: string) {
  const ref = input(pendingSchema, value); if (ref.actorId !== input(id, actorId)) throw new RequestNotSentError(409, 'courier_assignment_actor_changed'); return ref;
}
export function matchCourierResult(value: unknown, ref: CourierPending): CourierChange {
  const row = parse(changeSchema, value);
  if (row.shipmentId !== ref.shipmentId || row.previousCourierId !== ref.previousCourierId || row.courierId !== ref.courierId ||
      BigInt(row.revision) !== BigInt(ref.expectedRevision) + BigInt(1)) return invalid();
  return row;
}
function parsePage<T>(value: unknown, beforeId: string | null, item: (row: unknown) => T, itemId: (row: T) => string) {
  const row = parse(z.object({items: z.array(z.unknown()).max(25), hasMore: z.boolean()}).strict(), value);
  if (row.hasMore && row.items.length !== 25) return invalid();
  let previous = beforeId === null ? null : BigInt(input(id, beforeId));
  const items = row.items.map(item);
  for (const item of items) {const current = BigInt(itemId(item)); if (previous !== null && current >= previous) return invalid(); previous = current;}
  return {items, hasMore: row.hasMore};
}
export function createCourierAssignmentGateway(fetcher: typeof fetch = globalThis.fetch, csrfUrl = '/api/v1/identity/csrf') {
  async function request(path: string, body: unknown, context: CourierContext, pending?: CourierPending) {
    const current = () => {if (!context.isCurrent() || context.signal?.aborted) throw new RequestNotSentError(409, 'courier_assignment_view_stale');};
    current(); input(id, context.actorId);
    const csrf = await fetchCsrfToken(csrfUrl, fetcher, context.signal); current();
    const headers: Record<string, string> = {'Content-Type': 'application/json', Accept: 'application/json', 'X-CSRF-Token': csrf, 'X-Expected-Actor-Id': context.actorId};
    if (pending) {headers['Idempotency-Key'] = pending.key; headers['If-Match'] = '"' + pending.expectedRevision + '"';}
    const response = await fetcher('/api/v1/fulfillment/courier-assignments' + path, {method: 'POST', credentials: 'same-origin', cache: 'no-store',
      redirect: 'error', signal: context.signal, headers, body: JSON.stringify(body)});
    const value: unknown = await response.json();
    if (!context.isCurrent() || context.signal?.aborted) throw new AccessFailure(503, 'courier_assignment_outcome_unknown');
    if (!response.ok) {
      const error = value && typeof value === 'object' ? value as {code?: unknown} : {};
      throw new AccessFailure(response.status, typeof error.code === 'string' ? error.code : 'courier_assignment_unavailable');
    }
    return value;
  }
  return {
    async list(beforeId: string | null, context: CourierContext) {
      const before = beforeId === null ? null : input(id, beforeId);
      return parsePage(await request('/query', {purpose: 'courier-assignment', beforeId: before, limit: 25}, context), before, parseCourierTask, row => row.shipmentId);
    },
    async read(shipmentId: string, context: CourierContext) {
      const selected = input(id, shipmentId), task = parseCourierTask(await request('/' + selected + '/query', {purpose: 'courier-assignment'}, context));
      if (task.shipmentId !== selected) return invalid(); return task;
    },
    async couriers(shipmentId: string, beforeId: string | null, context: CourierContext) {
      const selected = input(id, shipmentId), before = beforeId === null ? null : input(id, beforeId);
      return parsePage(await request('/' + selected + '/couriers/query', {purpose: 'courier-assignment', beforeId: before, limit: 25}, context), before,
        value => parse(z.object({userId: id, displayName: name}).strict(), value), row => row.userId);
    },
    async preview(task: CourierTask, courierId: string, context: CourierContext): Promise<CourierPreview> {
      const selected = parseCourierTask(task), target = input(id, courierId);
      if (!selected.canHandover || selected.received || target === selected.currentAssignment.courierId)
        throw new RequestNotSentError(409, 'courier_assignment_view_stale');
      const value = parse(z.object({task: z.unknown(), courierId: id, courierName: name, review}).strict(),
        await request('/' + selected.shipmentId + '/preview/query', {purpose: 'courier-assignment', expectedRevision: selected.currentAssignment.revision, courierId: target}, context));
      const fresh = parseCourierTask(value.task);
      if (JSON.stringify(fresh) !== JSON.stringify(selected) || value.courierId !== target || !fresh.canHandover || fresh.received) return invalid();
      return {...value, task: fresh};
    },
    async handover(shown: CourierPreview, note: string, original: CourierPending, context: CourierContext) {
      const ref = parseCourierPending(original, context.actorId), task = parseCourierTask(shown.task), reviewed = input(review, shown.review), auditReason = input(reason, note);
      if (!task.canHandover || task.received || ref.shipmentId !== task.shipmentId || ref.expectedRevision !== task.currentAssignment.revision ||
          ref.previousCourierId !== task.currentAssignment.courierId || ref.courierId !== shown.courierId || ref.courierId === ref.previousCourierId)
        throw new RequestNotSentError(409, 'courier_assignment_view_stale');
      const saved = matchCourierResult(await request('/' + ref.shipmentId + '/handover', {expectedRevision: ref.expectedRevision,
        courierId: ref.courierId, review: reviewed, reason: auditReason}, context, ref), ref);
      if (saved.reason !== auditReason || saved.courierName !== shown.courierName) return invalid(); return saved;
    },
    async recover(original: CourierPending, context: CourierContext) {
      const ref = parseCourierPending(original, context.actorId);
      const value = parse(z.object({status: z.enum(['not_confirmed', 'committed']), result: z.unknown().nullable()}).strict(),
        await request('/operations/query', {purpose: 'operation-recovery', key: ref.key}, context));
      if (value.status === 'not_confirmed') {if (value.result !== null) return invalid(); return null;}
      if (value.result === null) return invalid(); return matchCourierResult(value.result, ref);
    },
  };
}
