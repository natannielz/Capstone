import {z} from 'zod';
import {fetchCsrfToken, RequestNotSentError} from '../../api/client';
import {AccessFailure} from '../administration/workflow';
import {parseAnomalyReference, type AnomalyReference} from './anomaly-journal';

const idPattern = /^[1-9][0-9]{0,18}$(?![\s\S])/;
const id = z.string().regex(idPattern).refine(value => idPattern.test(value) && BigInt(value) <= BigInt('9223372036854775807'));
const text = (maximum: number) => z.string().min(1).max(maximum).refine(value => value === value.trim() && !/\p{Cc}/u.test(value));
const number = z.string().regex(/^(0|[1-9][0-9]*)(\.[0-9]+)?$(?![\s\S])/).max(80);
const kind = z.enum(['order', 'refund']);
const signal = z.object({category: z.enum(['price', 'quantity', 'adjustment', 'refund']), code: text(80),
  state: z.enum(['consistent', 'flagged', 'within_policy', 'policy_needed', 'unavailable']), reference: text(160),
  observed: number.nullable(), expected: number.nullable(), policyVersion: text(120).nullable(), explanation: text(1000)}).strict()
  .refine(value => value.state !== 'policy_needed' || value.expected === null)
  .refine(value => value.state !== 'within_policy' || value.policyVersion !== null && value.expected !== null);
const choice = z.object({kind, id, label: text(160)}).strict();
const choices = z.object({items: z.array(choice).max(50), nextCursor: id.nullable(), adjustment: signal}).strict()
  .refine(value => value.adjustment.category === 'adjustment' && value.adjustment.state === 'unavailable' &&
    value.items.every((item, index) => index === 0 || idPattern.test(item.id) && idPattern.test(value.items[index - 1].id) && BigInt(item.id) < BigInt(value.items[index - 1].id)) &&
    (value.nextCursor === null || value.items.length === 50 && value.nextCursor === value.items[49].id));
const disposition = z.object({id, reviewerId: id, recordedAt: z.string().datetime({offset: true}),
  outcome: z.enum(['acknowledged', 'requires_action', 'not_concerning']), reason: text(1000)}).strict();
const evaluation = z.object({id, sourceKind: kind, sourceId: id, sourceHash: z.string().length(64).regex(/^[0-9a-f]{64}$/),
  ruleVersion: z.literal('operational-anomaly-v1'), policyVersion: text(120).nullable(), policyRevision: id,
  recordedAt: z.string().datetime({offset: true}), signals: z.array(signal).min(1).max(301), current: z.boolean(), disposition: disposition.nullable()}).strict();
export type AnomalyChoice = z.infer<typeof choice>;
export type AnomalyEvaluation = z.infer<typeof evaluation>;
export type AnomalyOutcome = z.infer<typeof disposition>['outcome'];
export type AnomalyKind = z.infer<typeof kind>;
const moneyPattern = /^(0|[1-9][0-9]*)(\.[0-9]{1,2})?$(?![\s\S])/;
const money = z.string().max(18).regex(moneyPattern).refine(value => {
  if (!moneyPattern.test(value) || value.length > 18) return false;
  const [whole, fraction = ''] = value.split('.'); const cents = BigInt(whole) * BigInt(100) + BigInt(fraction.padEnd(2, '0'));
  return cents > BigInt(0) && cents <= BigInt('99999999999999999');
});
const policy = z.object({version: text(120), approvalReference: text(256), maximumPackages: id.nullable().refine(value => value === null || idPattern.test(value) && BigInt(value) <= BigInt(1000000)),
  maximumUnitPrice: money.nullable(), currency: z.string().length(3).regex(/^[A-Z]{3}$/).nullable(), maximumRefund: money.nullable()}).strict()
  .refine(value => (value.maximumUnitPrice !== null || value.maximumRefund !== null) === (value.currency !== null));
const policyAttempt = z.object({sourceKind: kind, sourceId: id, expectedRevision: id, policy, reason: text(1000)}).strict();
export type AnomalyPolicyAttempt = z.infer<typeof policyAttempt>;
export function retainAnomalyPolicyReference(failure: unknown, earlierAttemptExists: boolean): boolean {
  // A refusal now cannot prove that an earlier request with this key did not commit.
  return earlierAttemptExists || !(failure instanceof AccessFailure) || ![400, 401, 403, 409].includes(failure.status) || failure.code === 'idempotency_key_reused';
}
export function parseAnomalyPolicyAttempt(value: unknown) {
  const parsed = policyAttempt.safeParse(value); if (!parsed.success) throw new RequestNotSentError(400, 'anomaly_policy_invalid'); return parsed.data;
}
export function parseAnomalyEvaluation(value: unknown): AnomalyEvaluation {
  const result = evaluation.safeParse(value); if (!result.success) throw new AccessFailure(502, 'anomaly_response_invalid'); return result.data;
}
type Context = {actorId: string; isCurrent: () => boolean; signal?: AbortSignal};
export function createAnomalyGateway(fetcher: typeof globalThis.fetch = globalThis.fetch, csrfUrl = '/api/v1/identity/csrf') {
  async function request(path: string, context: Context, body?: unknown, key?: string): Promise<unknown> {
    const current = (sent = false) => {if (!context.isCurrent() || context.signal?.aborted) {
      if (sent) throw new AccessFailure(503, 'anomaly_outcome_unknown');
      throw new RequestNotSentError(409, 'anomaly_view_stale');
    }};
    current();
    if (!id.safeParse(context.actorId).success) throw new RequestNotSentError(400, 'invalid_actor');
    const headers: Record<string, string> = {'Accept': 'application/json', 'X-Expected-Actor-Id': context.actorId};
    if (body !== undefined) {
      if (!key || !/^[A-Za-z0-9_.:-]{1,128}$/.test(key)) throw new RequestNotSentError(400, 'invalid_idempotency_key');
      headers['Content-Type'] = 'application/json'; headers['Idempotency-Key'] = key;
      headers['X-CSRF-Token'] = await fetchCsrfToken(csrfUrl, fetcher, context.signal); current();
    }
    const response = await fetcher('/api/v1/operations/anomalies' + path, {method: body === undefined ? 'GET' : 'POST',
      headers, credentials: 'same-origin', redirect: 'error', cache: 'no-store', signal: context.signal,
      ...(body === undefined ? {} : {body: JSON.stringify(body)})});
    const data: unknown = await response.json(); current(body !== undefined);
    if (!response.ok) throw new AccessFailure(response.status, (data as {code?: string})?.code ?? 'anomaly_unavailable');
    return data;
  }
  const selected = (value: AnomalyEvaluation, source: AnomalyChoice) => {
    if (value.sourceKind !== source.kind || value.sourceId !== source.id) throw new AccessFailure(502, 'anomaly_response_invalid'); return value;
  };
  return {
    async configure(value: AnomalyPolicyAttempt, key: string, context: Context) {
      const input = parseAnomalyPolicyAttempt(value), result = await request('/policy', context, input, key);
      if (result !== input.policy.version) throw new AccessFailure(502, 'anomaly_response_invalid'); return input.policy.version;
    },
    async recoverPolicy(source: AnomalyChoice, key: string, context: Context) {
      if (!/^[A-Za-z0-9_.:-]{1,128}$/.test(key)) throw new RequestNotSentError(400, 'invalid_idempotency_key');
      const result = z.object({status: z.enum(['confirmed', 'not_confirmed']), policyVersion: text(120).nullable()}).strict()
        .refine(value => (value.status === 'confirmed') === (value.policyVersion !== null)).safeParse(
          await request('/policy/receipt?sourceKind=' + source.kind + '&sourceId=' + source.id + '&key=' + encodeURIComponent(key), context));
      if (!result.success) throw new AccessFailure(502, 'anomaly_response_invalid'); return result.data;
    },
    async recover(original: AnomalyReference, context: Context) {
      const ref = parseAnomalyReference(original, context.actorId);
      if (ref.operation === 'configure') throw new RequestNotSentError(400, 'anomaly_recovery_operation_invalid');
      const parsed = z.object({status: z.enum(['confirmed', 'not_confirmed']), evaluation: evaluation.nullable()}).strict()
        .refine(value => (value.status === 'confirmed') === (value.evaluation !== null)).safeParse(await request('/operations/receipt?sourceKind=' + ref.sourceKind +
          '&sourceId=' + ref.sourceId + '&operation=' + ref.operation + '&key=' + encodeURIComponent(ref.key) +
          (ref.evaluationId === null ? '' : '&evaluationId=' + ref.evaluationId), context));
      if (!parsed.success) throw new AccessFailure(502, 'anomaly_response_invalid');
      const result = parsed.data;
      if (result.evaluation !== null && (result.evaluation.sourceKind !== ref.sourceKind || result.evaluation.sourceId !== ref.sourceId ||
          ref.operation === 'review' && (result.evaluation.id !== ref.evaluationId || result.evaluation.disposition?.reviewerId !== ref.actorId)))
        throw new AccessFailure(502, 'anomaly_response_invalid');
      return result;
    },
    async choices(kind: AnomalyKind, context: Context, before: string | null = null) {
      if (before !== null && !id.safeParse(before).success) throw new RequestNotSentError(400, 'invalid_cursor');
      const parsed = choices.safeParse(await request('/sources?kind=' + kind + (before === null ? '' : '&before=' + before), context));
      if (!parsed.success || parsed.data.items.some(value => value.kind !== kind || before !== null && BigInt(value.id) >= BigInt(before)))
        throw new AccessFailure(502, 'anomaly_response_invalid'); return parsed.data;
    },
    async latest(source: AnomalyChoice, context: Context) {
      const data = await request('/latest?sourceKind=' + source.kind + '&sourceId=' + source.id, context);
      return data === null ? null : selected(parseAnomalyEvaluation(data), source);
    },
    async evaluate(source: AnomalyChoice, reason: string, key: string, context: Context) {
      if (!text(1000).safeParse(reason).success) throw new RequestNotSentError(400, 'anomaly_reason_required');
      return selected(parseAnomalyEvaluation(await request('/evaluate', context, {sourceKind: source.kind, sourceId: source.id, reason}, key)), source);
    },
    async review(value: AnomalyEvaluation, outcome: AnomalyOutcome, reason: string, key: string, context: Context) {
      if (!value.current || value.disposition !== null) throw new RequestNotSentError(409, 'anomaly_source_changed');
      if (!text(1000).safeParse(reason).success) throw new RequestNotSentError(400, 'anomaly_reason_required');
      const result = parseAnomalyEvaluation(await request('/' + value.id + '/disposition', context, {outcome, reason}, key));
      if (result.id !== value.id || result.sourceHash !== value.sourceHash || result.disposition?.outcome !== outcome || result.disposition.reason !== reason)
        throw new AccessFailure(502, 'anomaly_response_invalid'); return result;
    },
  };
}
