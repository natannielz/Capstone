import {currentCourierSchema, currentCourierMatchesDeparture} from '../fulfillment/courier-assignment-client';
import {z} from 'zod';
import {fetchCsrfToken} from '../../api/client';
import {AccessFailure} from '../administration/workflow';

const id = z.string().regex(/^[1-9][0-9]{0,18}$(?![\s\S])/).refine(value => BigInt(value) <= BigInt('9223372036854775807'));
const time = z.string().datetime({offset: true}).refine(value => value.endsWith('Z') || value.endsWith('+00:00'));
const number = z.string().min(1).max(100);
const ids = z.array(id).min(1).max(20).refine(values => new Set(values).size === values.length);
const shipment = z.object({id, revision: id, suborderId: id, number, dispatchedAt: time, courierId: id, courierName: z.string().min(1).max(256), currentAssignment: currentCourierSchema}).strict()
  .refine(row => currentCourierMatchesDeparture(row.currentAssignment, row.courierId, row.courierName, row.dispatchedAt));
const invoice = z.object({id, number, issuedAt: time, dueDate: z.string().date(), orderIds: ids}).strict();
const payment = z.object({id, revision: id, createdAt: time, updatedAt: time, state: z.enum(['draft', 'submitted']), invoiceIds: ids, submittedAt: time.nullable()}).strict()
  .refine(row => Date.parse(row.createdAt) <= Date.parse(row.updatedAt) && (row.state === 'submitted') === (row.submittedAt !== null) && (row.submittedAt === null || Date.parse(row.createdAt) <= Date.parse(row.submittedAt)));
const caseItem = z.object({id, revision: id, requestedAt: time, state: z.enum(['requested', 'return_authorized', 'rejected']), invoiceId: id,
  issue: z.enum(['wrong_item', 'damaged', 'missing', 'other'])}).strict();
export type TimelineShipment = z.infer<typeof shipment>;
export type TimelineInvoice = z.infer<typeof invoice>;
export type TimelinePayment = z.infer<typeof payment>;
export type TimelineCase = z.infer<typeof caseItem>;
export type TimelinePage<T> = {orderId: string; items: T[]; hasMore: boolean};
const invalid = (): never => {throw new AccessFailure(502, 'order_timeline_response_invalid');};
function parse<T>(schema: z.ZodType<T>, value: unknown): T {const checked = schema.safeParse(value); return checked.success ? checked.data : invalid();}
function page<T extends {id: string}>(item: z.ZodType<T>, value: unknown, orderId: string, beforeId: string | null): TimelinePage<T> {
  const found = parse(z.object({orderId: id, items: z.array(item).max(25), hasMore: z.boolean()}).strict(), value);
  if (found.orderId !== parse(id, orderId) || found.hasMore && found.items.length !== 25) return invalid();
  let previous = beforeId === null ? null : BigInt(parse(id, beforeId));
  for (const row of found.items) {const current = BigInt(row.id); if (previous !== null && current >= previous) return invalid(); previous = current;}
  return found;
}
export const parseTimelineShipments = (value: unknown, orderId: string, beforeId: string | null = null) => page(shipment, value, orderId, beforeId);
export function parseTimelineInvoices(value: unknown, orderId: string, beforeId: string | null = null) {
  const found = page(invoice, value, orderId, beforeId);
  return found.items.every(row => row.orderIds.includes(orderId)) ? found : invalid();
}
export const parseTimelinePayments = (value: unknown, orderId: string, beforeId: string | null = null) => page(payment, value, orderId, beforeId);
export const parseTimelineCases = (value: unknown, orderId: string, beforeId: string | null = null) => page(caseItem, value, orderId, beforeId);

const invoiceEvent = z.object({invoiceId: id, eventId: id, kind: z.enum(['allocation', 'reversal']), recordedAt: time, originalAllocationId: id.nullable(), actorAccountId: id}).strict()
  .refine(row => (row.kind === 'reversal') === (row.originalAllocationId !== null));
const paymentOutcome = z.object({orderId: id, declarationId: id, declarationRevision: id, observedAt: time, declarationState: z.enum(['draft', 'submitted']),
  submissionId: id.nullable(), submittedAt: time.nullable(), reviewId: id.nullable(), reviewRevision: z.union([z.literal('0'), id]),
  reviewState: z.enum(['not_submitted', 'pending', 'assigned', 'rejected', 'replacement_requested']), reviewRecordedAt: time.nullable(), publicExplanation: z.string().min(1).max(1000).nullable(),
  verificationId: id.nullable(), verifiedAt: time.nullable(), currentAssigneeAccountId: id.nullable(), reviewActorAccountId: id.nullable(),
  verificationActorAccountId: id.nullable(), invoiceEvents: z.array(invoiceEvent)}).strict();
const caseEvent = z.object({eventId: id, kind: z.enum(['received', 'inspected', 'credit_requested', 'refund_requested', 'replacement_requested', 'entitlement_requested',
  'credit_approved', 'refund_approved', 'payout_recorded', 'refund_settled']), recordedAt: time, actorAccountId: id}).strict();
const caseOutcome = z.object({orderId: id, caseId: id, invoiceId: id, observedAt: time, caseState: z.enum(['requested', 'return_authorized', 'rejected']),
  decisionId: id.nullable(), decidedAt: time.nullable(), decisionActorAccountId: id.nullable(), publicExplanation: z.string().min(1).max(1000).nullable(),
  returns: z.array(z.object({returnId: id, events: z.array(caseEvent)}).strict())}).strict();
export type TimelinePaymentOutcome = z.infer<typeof paymentOutcome>;
export type TimelineCaseOutcome = z.infer<typeof caseOutcome>;
function chronological(values: {recordedAt: string}[], lower: string, upper: string) {
  let previous = Date.parse(lower); const until = Date.parse(upper);
  return values.every(row => {const at = Date.parse(row.recordedAt), valid = at >= previous && at <= until; previous = at; return valid;});
}
export function parseTimelinePaymentOutcome(value: unknown, orderId: string, declarationId: string) {
  const row = parse(paymentOutcome, value);
  if (row.orderId !== parse(id, orderId) || row.declarationId !== parse(id, declarationId) ||
      (row.submissionId === null) !== (row.submittedAt === null) || (row.reviewId === null) !== (row.reviewRecordedAt === null) ||
      (row.reviewRevision === '0') !== (row.reviewId === null) || (row.verificationId === null) !== (row.verifiedAt === null) ||
      (row.reviewId === null) !== (row.reviewActorAccountId === null) || (row.verificationId === null) !== (row.verificationActorAccountId === null) ||
      (row.reviewState === 'assigned') !== (row.currentAssigneeAccountId !== null)) return invalid();
  if (row.declarationState === 'draft') {
    if (row.submissionId !== null || row.reviewId !== null || row.reviewState !== 'not_submitted' || row.publicExplanation !== null || row.verificationId !== null || row.invoiceEvents.length > 0) return invalid();
  } else {
    if (row.submittedAt === null || row.reviewState === 'not_submitted' || row.reviewId === null && (row.reviewState !== 'pending' || row.publicExplanation !== null) ||
        !chronological([{recordedAt: row.submittedAt}, ...(row.reviewRecordedAt === null ? [] : [{recordedAt: row.reviewRecordedAt}])], row.submittedAt, row.observedAt) ||
        row.verifiedAt !== null && !chronological([{recordedAt: row.verifiedAt}], row.submittedAt, row.observedAt) ||
        row.invoiceEvents.length > 0 && row.verifiedAt === null || !chronological(row.invoiceEvents, row.submittedAt, row.observedAt)) return invalid();
  }
  if (new Set(row.invoiceEvents.map(event => [event.invoiceId, event.kind, event.eventId].join(':'))).size !== row.invoiceEvents.length) return invalid();
  return row;
}
export function parseTimelineCaseOutcome(value: unknown, orderId: string, caseId: string) {
  const row = parse(caseOutcome, value), requested = row.caseState === 'requested';
  if (row.orderId !== parse(id, orderId) || row.caseId !== parse(id, caseId) || requested !== (row.decisionId === null) ||
      requested !== (row.decidedAt === null) || requested !== (row.publicExplanation === null) || requested !== (row.decisionActorAccountId === null) ||
      row.decidedAt !== null && Date.parse(row.decidedAt) > Date.parse(row.observedAt) || row.caseState !== 'return_authorized' && row.returns.length > 0) return invalid();
  let previous = BigInt(0);
  for (const returned of row.returns) {
    if (BigInt(returned.returnId) <= previous || row.decidedAt === null || returned.events.length === 0 ||
        !chronological(returned.events, row.decidedAt, row.observedAt) || new Set(returned.events.map(event => event.kind + ':' + event.eventId)).size !== returned.events.length) return invalid();
    previous = BigInt(returned.returnId);
  }
  return row;
}

export function createOrderTimelineGateway(fetcher: typeof fetch = globalThis.fetch, csrfUrl = '/api/v1/identity/csrf') {
  async function query(actorId: string, orderId: string, path: string, body: unknown, signal?: AbortSignal) {
    const actor = parse(id, actorId), order = parse(id, orderId), controller = new AbortController();
    const cancel = () => controller.abort(); signal?.addEventListener('abort', cancel, {once: true}); if (signal?.aborted) cancel();
    const timeout = setTimeout(cancel, 30_000);
    try {
      const csrf = await fetchCsrfToken(csrfUrl, fetcher, controller.signal);
      const response = await fetcher('/api/v1/ordering/orders/' + order + '/timeline/' + path + '/query', {
        method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: controller.signal,
        headers: {'Content-Type': 'application/json', 'X-CSRF-Token': csrf, 'X-Expected-Actor-Id': actor}, body: JSON.stringify(body),
      });
      const value: unknown = await response.json();
      if (!response.ok) {const problem = value && typeof value === 'object' ? value as Record<string, unknown> : {}; throw new AccessFailure(response.status, typeof problem.code === 'string' ? problem.code : 'request_failed');}
      return value;
    } finally {clearTimeout(timeout); signal?.removeEventListener('abort', cancel);}
  }
  const request = (beforeId: string | null) => ({purpose: 'order-timeline', beforeId: beforeId === null ? null : parse(id, beforeId), limit: 25});
  return {
    async shipments(actor: string, order: string, before: string | null, signal?: AbortSignal) {
      return parseTimelineShipments(await query(actor, order, 'shipments', request(before), signal), order, before);
    },
    async invoices(actor: string, order: string, before: string | null, signal?: AbortSignal) {
      return parseTimelineInvoices(await query(actor, order, 'invoices', request(before), signal), order, before);
    },
    async payments(actor: string, order: string, before: string | null, signal?: AbortSignal) {
      return parseTimelinePayments(await query(actor, order, 'payments', request(before), signal), order, before);
    },
    async cases(actor: string, order: string, before: string | null, signal?: AbortSignal) {
      return parseTimelineCases(await query(actor, order, 'cases', request(before), signal), order, before);
    },
    async payment(actor: string, order: string, declarationId: string, signal?: AbortSignal) {
      const chosen = parse(id, declarationId);
      return parseTimelinePaymentOutcome(await query(actor, order, 'payments/' + chosen, {purpose: 'order-timeline'}, signal), order, chosen);
    },
    async case(actor: string, order: string, caseId: string, signal?: AbortSignal) {
      const chosen = parse(id, caseId);
      return parseTimelineCaseOutcome(await query(actor, order, 'cases/' + chosen, {purpose: 'order-timeline'}, signal), order, chosen);
    },
  };
}
