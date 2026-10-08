import {z} from 'zod';
import {fetchCsrfToken, RequestNotSentError} from '../../api/client';
import {AccessFailure} from './workflow';
import {parseDelegationReference, type DelegationReference} from './delegation-journal';

const idPattern = /^[1-9][0-9]{0,18}$(?![\s\S])/;
const id = z.string().regex(idPattern).refine(v => idPattern.test(v) && BigInt(v) <= BigInt('9223372036854775807'));
const text = (max: number) => z.string().min(1).max(max).refine(v => v === v.trim() && !/\p{Cc}/u.test(v));
const at = z.string().datetime({offset: true}).refine(v => /(?:Z|\+00:00)$/.test(v));
const scope = z.object({kind: z.enum(['system', 'buyingOrganization', 'buyingBranch', 'buyingDivision', 'fulfillmentShop', 'fulfillmentWarehouse']),
  organizationId: id.nullable(), branchId: id.nullable(), divisionId: id.nullable(), shopId: id.nullable(), warehouseId: id.nullable(), channelId: id.nullable()}).strict();
const permissions = z.array(text(128)).min(1).max(256).refine(values => new Set(values).size === values.length);
const request = z.object({sourceGrantId: id, sourceVersion: id, beneficiaryUserId: id, beneficiaryMembershipId: id, scope, permissions,
  effectiveAt: at, expiresAt: at}).strict().refine(v => Date.parse(v.expiresAt) > Date.parse(v.effectiveAt));
const action = z.enum(['propose', 'approve', 'revoke', 'handover']);
const item = z.object({id, revision: id, sourceUserId: id, approvedBy: id.nullable(), request, status: z.enum(['pending', 'active', 'revoked']),
  sourceState: z.enum(['current', 'unavailable']), reason: text(1000), updatedAt: at, allowedActions: z.array(action).max(3), taskReassignmentState: z.enum(['unavailable', 'payment_review_only'])}).strict()
  .refine(v => !v.allowedActions.includes('propose') && new Set(v.allowedActions).size === v.allowedActions.length &&
    (v.status !== 'pending' || v.approvedBy === null) && (v.status !== 'active' || v.approvedBy !== null));
const source = z.object({id, version: id, scope, permissions, effectiveAt: at, expiresAt: at.nullable()}).strict();
const result = z.object({delegationId: id, eventId: id, revision: id, action, status: z.enum(['pending', 'active', 'revoked']), recordedAt: at}).strict()
  .refine(v => v.action !== 'propose' || v.status === 'pending' && v.revision === '1').refine(v => v.action !== 'approve' || v.status === 'active')
  .refine(v => v.action !== 'revoke' || v.status === 'revoked');
const command = z.object({action, id: id.nullable(), request: request.nullable(), reason: text(1000), revision: z.union([id, z.literal('0')])}).strict()
  .refine(v => v.action === 'propose' ? v.id === null && v.request !== null && v.revision === '0' : v.id !== null && v.request === null && v.revision !== '0');
export type DelegationRequest = z.infer<typeof request>;
export type DelegationItem = z.infer<typeof item>;
export type DelegationSource = z.infer<typeof source>;
export type DelegationCommand = z.infer<typeof command>;
export type DelegationResult = z.infer<typeof result>;
function parsed<T>(schema: z.ZodType<T>, value: unknown): T {
  const out = schema.safeParse(value); if (!out.success) throw new AccessFailure(502, 'delegation_response_invalid'); return out.data;
}
export function parseDelegationCommand(value: unknown): DelegationCommand {
  const out = command.safeParse(value); if (!out.success) throw new RequestNotSentError(400, 'delegation_input_invalid'); return out.data;
}
export function parseDelegationPage(value: unknown) {
  return parsed(z.object({items: z.array(item).max(25), nextId: id.nullable()}).strict().refine(v =>
    v.items.every((row, i) => i === 0 || BigInt(row.id) < BigInt(v.items[i - 1].id)) &&
    (v.nextId === null || v.items.length === 25 && v.nextId === v.items[24].id)), value);
}
export function parseDelegationResult(value: unknown, reference: DelegationReference): DelegationResult {
  const ref = parseDelegationReference(reference, reference.actorId), out = parsed(result, value);
  if (out.action !== ref.action || ref.id !== null && out.delegationId !== ref.id || BigInt(out.revision) !== BigInt(ref.revision) + BigInt(1))
    throw new AccessFailure(502, 'delegation_response_invalid'); return out;
}
type Context = {actorId: string; isCurrent: () => boolean; signal?: AbortSignal};
export function createDelegationGateway(fetcher: typeof globalThis.fetch = globalThis.fetch, csrfUrl = '/api/v1/identity/csrf') {
  async function call(path: string, body: unknown, context: Context, ref?: DelegationReference): Promise<unknown> {
    const current = (sent = false) => {if (!context.isCurrent() || context.signal?.aborted) {
      if (sent && ref) throw new AccessFailure(503, 'delegation_outcome_unknown');
      throw new RequestNotSentError(409, 'delegation_view_stale');
    }};
    current(); if (!id.safeParse(context.actorId).success) throw new RequestNotSentError(400, 'invalid_actor');
    const headers: Record<string, string> = {'Content-Type': 'application/json', 'Accept': 'application/json', 'X-Expected-Actor-Id': context.actorId};
    if (ref) {parseDelegationReference(ref, context.actorId); headers['Idempotency-Key'] = ref.key; headers['If-Match'] = `"${ref.revision}"`;}
    const timeout = new AbortController(), timer = setTimeout(() => timeout.abort(), 30_000);
    const signal = context.signal ? AbortSignal.any([context.signal, timeout.signal]) : timeout.signal;
    try {
      headers['X-CSRF-Token'] = await fetchCsrfToken(csrfUrl, fetcher, signal); current();
      const response = await fetcher('/api/v1/access/delegations' + path, {method: 'POST', body: JSON.stringify(body), headers,
        credentials: 'same-origin', redirect: 'error', cache: 'no-store', signal});
      const data: unknown = await response.json(); current(true);
      if (!response.ok) throw new AccessFailure(response.status, typeof (data as {code?: unknown})?.code === 'string' ? (data as {code: string}).code : 'delegation_unavailable');
      return data;
    } finally {clearTimeout(timer);}
  }
  return {
    async sources(context: Context) {return parsed(z.array(source).max(1000), await call('/sources/query', {purpose: 'delegation-review'}, context));},
    async list(context: Context, beforeId: string | null = null) {return parseDelegationPage(await call('/query', {purpose: 'delegation-review', beforeId}, context));},
    async detail(selected: string, context: Context) {
      parsed(id, selected);
      const detail = parsed(z.object({item, history: z.array(z.object({id, revision: id, actorId: id, action, reason: text(1000), recordedAt: at}).strict()).min(1)}).strict(),
        await call('/' + selected + '/query', {purpose: 'delegation-review'}, context));
      if (detail.item.id !== selected || detail.history.length !== Number(detail.item.revision) ||
        detail.history.some((row, i) => BigInt(row.revision) !== BigInt(i + 1))) throw new AccessFailure(502, 'delegation_response_invalid'); return detail;
    },
    async change(input: DelegationCommand, reference: DelegationReference, context: Context) {
      const value = parseDelegationCommand(input), ref = parseDelegationReference(reference, context.actorId);
      if (value.action !== ref.action || value.id !== ref.id || value.revision !== ref.revision) throw new RequestNotSentError(400, 'delegation_reference_mismatch');
      const {revision: _, ...body} = value; void _;
      return parseDelegationResult(await call('', body, context, ref), ref);
    },
    async recover(reference: DelegationReference, context: Context) {
      const ref = parseDelegationReference(reference, context.actorId);
      const value = parsed(z.object({state: z.enum(['committed', 'not_confirmed']), result: result.nullable()}).strict()
        .refine(v => (v.state === 'committed') === (v.result !== null)), await call('/operations/query', {purpose: 'operation-recovery', key: ref.key}, context));
      return value.result === null ? null : parseDelegationResult(value.result, ref);
    },
  };
}
