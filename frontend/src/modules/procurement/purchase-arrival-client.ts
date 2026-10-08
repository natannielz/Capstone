import {z} from 'zod';
import {fetchCsrfToken, RequestNotSentError} from '../../api/client';
import {AccessFailure} from '../administration/workflow';

const idPattern = /^[1-9][0-9]{0,18}$(?![\s\S])/;
const id = z.string().regex(idPattern).refine(value => idPattern.test(value) && BigInt(value) <= BigInt('9223372036854775807'));
const text = (max: number) => z.string().min(1).max(max).refine(value => value === value.trim() && !/\p{Cc}/u.test(value));
const utc = z.string().datetime({offset: true}).refine(value => /(?:Z|\+00:00)$/.test(value) && !/\.\d{4,}/.test(value) && Number.isFinite(Date.parse(value)))
  .transform(value => new Date(value).toISOString());
const hash = z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/);
const input = z.object({orderId: id, orderVersion: id, approvalId: id, skuId: id, definitionId: id, expectedRevision: id,
  expectedAt: utc.nullable(), confirmationReference: text(256)}).strict();
const view = z.object({orderId: id, orderVersion: id, approvalId: id, skuId: id, definitionId: id, revision: id,
  orderedPackages: z.number().int().min(1).max(1_000_000), state: z.enum(['unrecorded', 'pending', 'approved', 'rejected']),
  proposalId: id.nullable(), proposedAt: utc.nullable(), expectedAt: utc.nullable(), confirmationReference: text(256).nullable(),
  reason: text(1000).nullable(), recordedAt: utc.nullable()}).strict()
  .refine(value => value.state === 'approved' || value.expectedAt === null)
  .refine(value => value.state === 'unrecorded'
    ? value.proposalId === null && value.recordedAt === null && value.confirmationReference === null && value.reason === null && value.proposedAt === null
    : value.proposalId !== null && value.recordedAt !== null && value.confirmationReference !== null && value.reason !== null)
  .refine(value => value.expectedAt === null || value.expectedAt === value.proposedAt);
const preview = z.object({current: view, input, binding: hash, decision: z.enum(['approve', 'reject']).nullable()}).strict()
  .refine(value => value.current.orderId === value.input.orderId && value.current.skuId === value.input.skuId &&
    value.current.definitionId === value.input.definitionId && value.current.approvalId === value.input.approvalId && value.current.orderVersion === value.input.orderVersion);
const result = z.object({eventId: id, eventHash: hash, orderId: id, skuId: id, revision: id, state: z.enum(['pending', 'approved', 'rejected'])}).strict();
const action = z.enum(['propose', 'approve', 'reject']);
const keyPattern = /^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/;
const pending = z.object({actorId: id, orderId: id, skuId: id, key: z.string().regex(keyPattern), action}).strict();
export type PurchaseArrivalInput = z.infer<typeof input>;
export type PurchaseArrivalView = z.infer<typeof view>;
export type PurchaseArrivalPreview = z.infer<typeof preview>;
export type PurchaseArrivalResult = z.infer<typeof result>;
export type PurchaseArrivalPending = z.infer<typeof pending>;
export function parsePurchaseArrival(value: unknown) { const parsed = view.safeParse(value); if (!parsed.success) throw new AccessFailure(502, 'purchase_arrival_response_invalid'); return parsed.data; }
export function arrivalUtc(value: string): string {
  if (!/^(?!0000)[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}$(?![\s\S])/.test(value)) throw new RequestNotSentError(400, 'purchase_arrival_invalid');
  const date = new Date(value + ':00+07:00');
  if (!Number.isFinite(date.getTime()) || new Date(date.getTime() + 7 * 3600_000).toISOString().slice(0, 16) !== value) throw new RequestNotSentError(400, 'purchase_arrival_invalid');
  return date.toISOString();
}
export function parseArrivalPending(value: unknown, actorId: string) { const parsed = pending.safeParse(value); if (!parsed.success || parsed.data.actorId !== actorId) throw new RequestNotSentError(409, 'purchase_arrival_pending_invalid'); return parsed.data; }
type Context = {actorId: string; isCurrent: () => boolean; signal?: AbortSignal};
export function createPurchaseArrivalGateway(fetcher: typeof globalThis.fetch = globalThis.fetch, csrfUrl = '/api/v1/identity/csrf') {
  async function request(path: string, body: unknown, context: Context, key?: string, revision?: string) {
    const current = () => { if (!context.isCurrent() || context.signal?.aborted) throw new RequestNotSentError(409, 'purchase_arrival_view_stale'); };
    current(); if (!id.safeParse(context.actorId).success || key !== undefined && !keyPattern.test(key)) throw new RequestNotSentError(400, 'purchase_arrival_invalid');
    const token = await fetchCsrfToken(csrfUrl, fetcher, context.signal); current();
    const headers: Record<string, string> = {'Accept': 'application/json', 'Content-Type': 'application/json', 'X-CSRF-Token': token, 'X-Expected-Actor-Id': context.actorId};
    if (key) headers['Idempotency-Key'] = key;
    if (revision) { if (!id.safeParse(revision).success) throw new RequestNotSentError(400, 'purchase_arrival_invalid'); headers['If-Match'] = '"' + revision + '"'; }
    const response = await fetcher('/api/v1/procurement/arrivals' + path, {method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: context.signal,
      headers, body: JSON.stringify(body)});
    const data: unknown = await response.json();
    if (!context.isCurrent() || context.signal?.aborted) throw new AccessFailure(503, 'purchase_arrival_outcome_unknown');
    if (!response.ok) throw new AccessFailure(response.status, (data as {code?: string})?.code ?? 'purchase_arrival_unavailable');
    return data;
  }
  function parse<T>(schema: z.ZodType<T>, value: unknown): T { const checked = schema.safeParse(value); if (!checked.success) throw new AccessFailure(502, 'purchase_arrival_response_invalid'); return checked.data; }
  function match(value: PurchaseArrivalView, orderId: string, skuId: string) { if (value.orderId !== orderId || value.skuId !== skuId) throw new AccessFailure(502, 'purchase_arrival_response_invalid'); return value; }
  return {
    async read(orderId: string, skuId: string, context: Context) {
      if (!id.safeParse(orderId).success || !id.safeParse(skuId).success) throw new RequestNotSentError(400, 'purchase_arrival_invalid');
      return match(parsePurchaseArrival(await request('/' + orderId + '/' + skuId + '/query', {purpose: 'purchase-arrival-review'}, context)), orderId, skuId);
    },
    async preview(value: PurchaseArrivalInput, context: Context) {
      const checked = input.safeParse(value); if (!checked.success) throw new RequestNotSentError(400, 'purchase_arrival_invalid');
      const shown = parse(preview, await request('/preview/query', {purpose: 'purchase-arrival-review', input: checked.data}, context));
      if (shown.decision !== null || JSON.stringify(shown.input) !== JSON.stringify(checked.data) || shown.current.revision !== value.expectedRevision) throw new AccessFailure(502, 'purchase_arrival_response_invalid');
      return shown;
    },
    async decision(current: PurchaseArrivalView, choice: 'approve' | 'reject', context: Context) {
      if (current.state !== 'pending' || !current.proposalId) throw new RequestNotSentError(409, 'purchase_arrival_view_stale');
      const shown = parse(preview, await request('/proposals/' + current.proposalId + '/decision-preview/query', {purpose: 'purchase-arrival-review', decision: choice}, context));
      if (shown.decision !== choice || JSON.stringify(shown.current) !== JSON.stringify(current)) throw new AccessFailure(502, 'purchase_arrival_response_invalid'); return shown;
    },
    async submit(shown: PurchaseArrivalPreview, reason: string, key: string, context: Context) {
      const checked = preview.safeParse(shown); if (!checked.success || !text(1000).safeParse(reason).success) throw new RequestNotSentError(400, 'purchase_arrival_invalid');
      const value = checked.data, choice = value.decision;
      const data = await request(choice ? '/proposals/' + value.current.proposalId + '/decisions' : '/proposals',
        choice ? {decision: choice, binding: value.binding, reason} : {input: value.input, binding: value.binding, reason}, context, key, choice ? value.current.revision : undefined);
      const saved = parse(result, data);
      if (saved.orderId !== value.input.orderId || saved.skuId !== value.input.skuId || saved.state !== (choice === null ? 'pending' : choice === 'approve' ? 'approved' : 'rejected') || BigInt(saved.revision) !== BigInt(value.current.revision) + BigInt(1)) throw new AccessFailure(502, 'purchase_arrival_response_invalid'); return saved;
    },
    async recover(saved: PurchaseArrivalPending, context: Context) {
      const ref = parseArrivalPending(saved, context.actorId);
      const value = parse(z.object({status: z.enum(['confirmed', 'not_confirmed']), result: result.nullable()}).strict(),
        await request('/operations/query', {purpose: 'operation-recovery', action: ref.action, key: ref.key}, context));
      if ((value.status === 'confirmed') !== (value.result !== null) || value.result && (value.result.orderId !== ref.orderId || value.result.skuId !== ref.skuId || value.result.state !== (ref.action === 'propose' ? 'pending' : ref.action === 'approve' ? 'approved' : 'rejected'))) throw new AccessFailure(502, 'purchase_arrival_response_invalid');
      return value;
    },
  };
}
