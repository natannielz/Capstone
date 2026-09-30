import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';

const id = z.string().refine(value => /^[1-9][0-9]{0,18}$(?![\s\S])/.test(value) && BigInt(value) <= BigInt('9223372036854775807'));
const revision = z.union([z.literal('0'), id]);
const text = (max: number) => z.string().min(1).max(max).refine(value => value === value.trim() && !/[\p{Cc}\p{Cf}]/u.test(value));
const date = z.string().refine(value => Number.isFinite(Date.parse(value)));
const details = z.object({ bankCode: text(32).refine(value => /^[A-Z0-9-]+$(?![\s\S])/.test(value)), accountNumber: z.string().regex(/^[0-9]{4,34}$(?![\s\S])/), holderName: text(160), currency: z.literal('IDR') }).strict();
const scope = z.object({ fulfillmentOrganizationId: id, shopId: id, channelId: id, warehouseId: id.nullable() }).strict();
const policy = z.object({ id, revision: id, scope, bankCode: text(32), bankName: text(160), method: z.enum(['bank-contact', 'authenticated-bank-record']), trustedChannelReference: text(256),
  operatingOwnerReference: text(256), approvalReference: text(256), active: z.boolean(), approvedAt: date, expiresAt: date, maxObservationAgeSeconds: id }).strict();
const proposal = z.object({ id, accountId: id, baseVersion: revision, actorId: id, recordedAt: date, reason: text(1000), details, policy }).strict();
const observation = z.object({ id, actorId: id, checkedAt: date, sourceReference: text(256), reason: text(1000), details }).strict();
const decision = z.object({ id, actorId: id, recordedAt: date, decision: z.enum(['approve', 'reject', 'withdraw']), reason: text(1000), observationId: id.nullable(), versionId: id.nullable(), accountVersion: id.nullable() }).strict();
const version = z.object({ id, version: id, details, effectiveAt: date, previousVersionId: id.nullable() }).strict();
const view = z.object({ proposal, observation: observation.nullable(), decision: decision.nullable(), currentVersion: version.nullable() }).strict();
const summary = z.object({ proposalId: id, accountId: id, scope, bankCode: text(32), maskedAccount: text(34), baseVersion: revision, currentVersion: revision, actorId: id, recordedAt: date, state: z.enum(['pending', 'approve', 'reject', 'withdraw']) }).strict();
export const receivingActions = ['propose', 'observe', 'approve', 'reject', 'withdraw'] as const;
export type ReceivingAction = typeof receivingActions[number];
const operation = z.enum(['billing.receiving-account.propose', 'billing.receiving-account.observe', 'billing.receiving-account.approve', 'billing.receiving-account.reject', 'billing.receiving-account.withdraw']);
const result = z.object({ accountId: id, proposalId: id, recordId: id, operation, accountVersion: id.nullable(), recordedAt: date }).strict();
export type ReceivingDetails = z.infer<typeof details>;
export type ReceivingPolicy = z.infer<typeof policy>;
export type ReceivingView = z.infer<typeof view>;
export type ReceivingSummary = z.infer<typeof summary>;
export type ReceivingResult = z.infer<typeof result>;
export type ReceivingCommand = { actorId: string; action: 'propose'; owner: string; revision: string; body: { accountId: string | null; policyId: string; policyRevision: string; details: ReceivingDetails; reason: string } } |
  { actorId: string; action: 'observe'; owner: string; revision: '1'; body: { details: ReceivingDetails; sourceReference: string; attested: true; reason: string } } |
  { actorId: string; action: 'approve' | 'reject' | 'withdraw'; owner: string; revision: '1'; body: { decision: 'approve' | 'reject' | 'withdraw'; observationId: string | null; reason: string } };
const invalid = (): never => { throw new AccessFailure(502, 'receiving_account_response_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown, input = false): T { const parsed = schema.safeParse(value); if (parsed.success) return parsed.data; if (input) throw new AccessFailure(400, 'invalid_receiving_account'); return invalid(); }
export function parseReceivingResult(value: unknown): ReceivingResult {
  const saved = parse(result, value);
  if ((saved.operation === 'billing.receiving-account.approve') !== (saved.accountVersion !== null) || saved.operation === 'billing.receiving-account.propose' && saved.recordId !== saved.proposalId) return invalid();
  return saved;
}
export function receivingKey(key: string) {
  const match = /^ra\.(propose|observe|approve|reject|withdraw)\.(0|[1-9][0-9]{0,18})\.([0-9a-f-]{36})$(?![\s\S])/.exec(key);
  if (!match || match[1] !== 'propose' && match[2] === '0') return invalid();
  return { action: match[1] as ReceivingAction, owner: parse(revision, match[2]) };
}
export function validateReceivingReceipt(value: unknown, key: string, expectedOperation: string) {
  const saved = parseReceivingResult(value), selected = receivingKey(key);
  if (saved.operation !== expectedOperation || expectedOperation !== 'billing.receiving-account.' + selected.action ||
    (selected.action === 'propose' ? selected.owner !== '0' && saved.accountId !== selected.owner : saved.proposalId !== selected.owner)) return invalid();
  return saved;
}
export function parseReceivingView(value: unknown, expectedId: string): ReceivingView {
  const saved = parse(view, value), p = saved.proposal, o = saved.observation, d = saved.decision;
  if (p.id !== expectedId || p.details.bankCode !== p.policy.bankCode || BigInt(p.baseVersion) > BigInt(saved.currentVersion?.version ?? '0') ||
    o && (o.actorId === p.actorId || Date.parse(o.checkedAt) < Date.parse(p.recordedAt)) ||
    d && ((d.decision === 'withdraw' ? d.actorId !== p.actorId : d.actorId === p.actorId) || Date.parse(d.recordedAt) < Date.parse(p.recordedAt))) return invalid();
  if (d?.decision === 'approve') {
    if (!o || d.observationId !== o.id || !d.versionId || !d.accountVersion || BigInt(d.accountVersion) !== BigInt(p.baseVersion) + BigInt(1) ||
      JSON.stringify(p.details) !== JSON.stringify(o.details) || !saved.currentVersion || BigInt(d.accountVersion) > BigInt(saved.currentVersion.version)) return invalid();
  } else if (d && (d.observationId !== null || d.versionId !== null || d.accountVersion !== null)) return invalid();
  return saved;
}
export function prepareReceivingProposal(actorId: string, selected: ReceivingPolicy, values: ReceivingDetails, reason: string, accountId: string | null = null, baseVersion = '0'): Extract<ReceivingCommand, { action: 'propose' }> {
  const p = parse(policy, selected), copied = parse(details, values, true);
  if (p.bankCode !== copied.bankCode || !p.active || accountId === null && baseVersion !== '0') throw new AccessFailure(400, 'invalid_receiving_account');
  return { actorId: parse(id, actorId), action: 'propose', owner: accountId === null ? '0' : parse(id, accountId), revision: parse(revision, baseVersion),
    body: { accountId, policyId: p.id, policyRevision: p.revision, details: copied, reason: parse(text(1000), reason, true) } };
}
export function prepareReceivingObservation(actorId: string, current: ReceivingView, values: ReceivingDetails, sourceReference: string, reason: string, attested: boolean): ReceivingCommand {
  if (!attested || current.decision || current.observation || actorId === current.proposal.actorId) throw new AccessFailure(400, 'invalid_receiving_account');
  return { actorId: parse(id, actorId), action: 'observe', owner: parse(id, current.proposal.id), revision: '1', body: { details: parse(details, values, true), sourceReference: parse(text(256), sourceReference, true), attested: true, reason: parse(text(1000), reason, true) } };
}
export function prepareReceivingDecision(actorId: string, current: ReceivingView, action: 'approve' | 'reject' | 'withdraw', reason: string): ReceivingCommand {
  if (current.decision || (action === 'withdraw' ? actorId !== current.proposal.actorId : actorId === current.proposal.actorId) || action === 'approve' &&
    (!current.observation || JSON.stringify(current.observation.details) !== JSON.stringify(current.proposal.details) || current.proposal.baseVersion !== (current.currentVersion?.version ?? '0'))) throw new AccessFailure(400, 'invalid_receiving_account');
  return { actorId: parse(id, actorId), action, owner: parse(id, current.proposal.id), revision: '1', body: { decision: action, observationId: action === 'approve' ? current.observation!.id : null, reason: parse(text(1000), reason, true) } };
}
function data<T>(value: { data?: T; error?: unknown; response: Response }): T {
  if (!value.response.ok || value.data === undefined) { const error = value.error && typeof value.error === 'object' ? value.error as Record<string, unknown> : {};
    throw new AccessFailure(value.response.status, typeof error.code === 'string' ? error.code : 'request_failed'); } return value.data;
}
export function createReceivingAccountGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': parse(id, actorId) });
  return {
    async policies(actorId: string, afterId: string | null = null) { return parse(z.object({ items: z.array(policy).max(25), hasMore: z.boolean() }).strict(), data(await client.POST('/api/v1/billing/receiving-accounts/policies/query', {
      body: { purpose: 'receiving-account-review', afterId }, params: { header: headers(actorId) }, cache: 'no-store' }))); },
    async list(actorId: string, afterId: string | null = null) { return parse(z.object({ items: z.array(summary).max(25), hasMore: z.boolean() }).strict(), data(await client.POST('/api/v1/billing/receiving-accounts/proposals/query', {
      body: { purpose: 'receiving-account-review', afterId }, params: { header: headers(actorId) }, cache: 'no-store' }))); },
    async read(actorId: string, proposalId: string) { return parseReceivingView(data(await client.POST('/api/v1/billing/receiving-accounts/proposals/{id}/query', {
      body: { purpose: 'receiving-account-review' }, params: { path: { id: parse(id, proposalId) }, header: headers(actorId) }, cache: 'no-store' })), proposalId); },
    async send(command: ReceivingCommand, key: string, current: () => boolean) {
      if (!current()) throw new RequestNotSentError(409, 'receiving_account_view_stale');
      const selected = receivingKey(key); if (selected.action !== command.action || selected.owner !== command.owner) return invalid();
      const header = { ...headers(command.actorId), 'Idempotency-Key': key, 'If-Match': '"' + command.revision + '"' };
      const saved = command.action === 'propose' ? data(await client.POST('/api/v1/billing/receiving-accounts/proposals', { body: command.body, params: { header }, cache: 'no-store' })) :
        command.action === 'observe' ? data(await client.POST('/api/v1/billing/receiving-accounts/proposals/{id}/observations', { body: command.body, params: { path: { id: command.owner }, header }, cache: 'no-store' })) :
          data(await client.POST('/api/v1/billing/receiving-accounts/proposals/{id}/decisions', { body: command.body, params: { path: { id: command.owner }, header }, cache: 'no-store' }));
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown');
      return validateReceivingReceipt(saved, key, 'billing.receiving-account.' + command.action);
    },
    async recover(actorId: string, key: string, expectedOperation: string) {
      const selected = receivingKey(key); if (expectedOperation !== 'billing.receiving-account.' + selected.action) return invalid();
      const saved = parse(z.object({ status: z.enum(['committed', 'not_confirmed']), result: result.nullable() }).strict(), data(await client.POST('/api/v1/billing/receiving-accounts/operations/query', {
        body: { purpose: 'operation-recovery', operation: expectedOperation, key }, params: { header: headers(actorId) }, cache: 'no-store' })));
      if (saved.status === 'not_confirmed' && saved.result === null) return null;
      return saved.status === 'committed' && saved.result ? { state: 'committed' as const, result: validateReceivingReceipt(saved.result, key, expectedOperation) } : invalid();
    },
  };
}
