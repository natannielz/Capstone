import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { paymentAllocationSchemas } from '../billing/payment-allocation-client';
import { freezeCatalogAttempt } from '../catalog/catalog-editor-state';
import { priceMoney } from '../catalog/catalog-price-workflow';

const { id, hash, money } = paymentAllocationSchemas;
const zeroVersion = z.union([z.literal('0'), id]).refine(value => BigInt(value) < BigInt('9223372036854775807'));
const version = zeroVersion.refine(value => value !== '0');
const expectedVersion = zeroVersion.refine(value => BigInt(value) < BigInt('9223372036854775806'));
const text = (max: number) => z.string().min(1).max(max).refine(value => value === value.normalize('NFKC').trim() && !/[\p{Cc}\p{Cf}\p{Co}\p{Cs}]/u.test(value));
const time = z.string().datetime({ offset: true }).refine(value => !/\.[0-9]{3}[0-9]*[1-9][0-9]*(?:Z|[+-][0-9]{2}:[0-9]{2})$/.test(value));
const target = z.object({ kind: z.enum(['branch', 'division']), id }).strict();
const context = z.object({ organizationId: id, branchId: id, divisionId: id.nullable() }).strict();
const rule = z.object({ mode: z.enum(['every_order', 'above_threshold', 'not_required']), threshold: money.nullable() }).strict()
  .refine(value => (value.mode === 'above_threshold') === (value.threshold !== null));
const scoped = (value: { target: z.infer<typeof target>; context: z.infer<typeof context> }) => value.context.organizationId !== value.context.branchId &&
  (value.target.kind === 'branch' ? value.target.id === value.context.branchId && value.context.divisionId === null :
    value.target.id === value.context.divisionId && value.context.divisionId !== value.context.organizationId && value.context.divisionId !== value.context.branchId);
const sameTarget = (left: z.infer<typeof target>, right: z.infer<typeof target>) => left.kind === right.kind && left.id === right.id;
const sameContext = (left: z.infer<typeof context>, right: z.infer<typeof context>) => left.organizationId === right.organizationId && left.branchId === right.branchId && left.divisionId === right.divisionId;
const windowValid = (value: { validFrom: string; validUntil: string }) => Date.parse(value.validFrom) < Date.parse(value.validUntil);
const sameWindow = (left: { validFrom: string; validUntil: string }, right: { validFrom: string; validUntil: string }) => Date.parse(left.validFrom) === Date.parse(right.validFrom) && Date.parse(left.validUntil) === Date.parse(right.validUntil);
const sameRule = (left: z.infer<typeof rule>, right: z.infer<typeof rule>) => left.mode === right.mode && left.threshold === right.threshold;
const choice = z.object({ target, context, code: text(64), name: text(160), expectedScopeVersion: version, expectedProfileVersion: version,
  policyVersion: zeroVersion, policyId: id.nullable(), versionId: id.nullable() }).strict().refine(scoped).refine(value =>
    value.policyVersion === '0' ? value.versionId === null : value.policyId !== null && value.versionId !== null);
const targets = z.object({ items: z.array(choice).max(100), hasMore: z.boolean(), nextTargetId: id.nullable() }).strict().refine(value =>
  value.items.every((item, index) => index === 0 || BigInt(item.target.id) < BigInt(value.items[index - 1].target.id)) &&
  (value.hasMore ? value.items.length > 0 && value.nextTargetId === value.items.at(-1)!.target.id : value.nextTargetId === null));
const input = z.object({ target, expectedScopeVersion: version, expectedProfileVersion: version, expectedPolicyVersion: expectedVersion, rule, validFrom: time, validUntil: time }).strict().refine(windowValid);
const previewView = z.object({ target, context, code: text(64), name: text(160), scopeVersion: version, profileVersion: version, policyVersion: zeroVersion,
  rule, validFrom: time, validUntil: time, sourceHash: hash }).strict().refine(scoped).refine(windowValid);
const preview = z.object({ binding: hash, view: previewView }).strict();
const current = z.object({ target, context, code: text(64), name: text(160), policyId: id.nullable(), version: zeroVersion, versionId: id.nullable(),
  rule: rule.nullable(), validFrom: time.nullable(), validUntil: time.nullable(), approvedAt: time.nullable(), current: z.boolean(), active: z.boolean() }).strict().refine(scoped).refine(value =>
    value.version === '0' ? value.versionId === null && value.rule === null && value.validFrom === null && value.validUntil === null && value.approvedAt === null && !value.current && !value.active :
      value.policyId !== null && value.versionId !== null && value.rule !== null && value.validFrom !== null && value.validUntil !== null && value.approvedAt !== null &&
      Date.parse(value.validFrom) < Date.parse(value.validUntil) && (!value.active || value.current));
const decision = z.enum(['approve', 'reject']);
const proposalShape = { proposalId: id, policyId: id, revision: z.literal('1'), target, context, code: text(64), name: text(160), rule, validFrom: time, validUntil: time,
  baseVersion: expectedVersion, proposedAt: time, state: z.enum(['pending', 'approved', 'rejected']), decision: decision.nullable(), policyVersion: version.nullable(), versionId: id.nullable(), bindingCurrent: z.boolean() };
const proposal = z.object(proposalShape).strict().refine(scoped).refine(windowValid).refine(value => value.state === 'approved'
  ? value.decision === 'approve' && value.policyVersion !== null && BigInt(value.policyVersion) === BigInt(value.baseVersion) + BigInt(1) && value.versionId !== null
  : value.decision === (value.state === 'pending' ? null : 'reject') && value.policyVersion === null && value.versionId === null);
const proposals = z.object({ items: z.array(proposal).max(100), nextProposalId: id.nullable() }).strict().refine(value =>
  value.items.every((item, index) => index === 0 || BigInt(item.proposalId) < BigInt(value.items[index - 1].proposalId)) &&
  (value.nextProposalId === null || value.items.length > 0 && value.nextProposalId === value.items.at(-1)!.proposalId));
const decisionPreview = z.object({ binding: hash, view: z.object({ ...proposalShape, decision }).strict().refine(scoped).refine(windowValid).refine(value =>
  value.state === 'pending' && value.policyVersion === null && value.versionId === null && (value.decision === 'reject' || value.bindingCurrent)) }).strict();
const operation = z.enum(['ordering.spending.policy.propose', 'ordering.spending.policy.approve', 'ordering.spending.policy.reject']);
const result = z.object({ policyId: id, proposalId: id, recordId: id, operation, policyVersion: version.nullable(), versionId: id.nullable(), recordedAt: time }).strict()
  .refine(value => value.operation === 'ordering.spending.policy.approve' ? value.policyVersion !== null && value.versionId !== null : value.policyVersion === null && value.versionId === null);
const recovery = z.object({ status: z.enum(['confirmed', 'not_confirmed']), result: result.nullable() }).strict().refine(value => (value.status === 'confirmed') === (value.result !== null));
const invalid = (): never => { throw new AccessFailure(502, 'spending_policy_response_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const checked = schema.safeParse(value); return checked.success ? checked.data : invalid(); }
function data<T>(response: { data?: T; error?: unknown; response: Response }): T {
  if (!response.response.ok || response.data === undefined) throw new AccessFailure(response.response.status, (response.error as { code?: string })?.code ?? 'request_failed'); return response.data;
}
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': parse(id, actorId) });
const requireCurrent = (current: () => boolean) => { if (!current()) throw new RequestNotSentError(409, 'spending_policy_view_stale'); };
export type SpendingPolicyTarget = z.infer<typeof target>;
export type SpendingPolicyRule = z.infer<typeof rule>;
export type SpendingPolicyChoice = z.infer<typeof choice>;
export type SpendingPolicyInput = z.infer<typeof input>;
export type SpendingPolicyCurrent = z.infer<typeof current>;
export type SpendingPolicyProposal = z.infer<typeof proposal>;
export type SpendingPolicyPreview = z.infer<typeof preview>;
export type SpendingPolicyDecisionPreview = z.infer<typeof decisionPreview>;
export type SpendingPolicyDecision = z.infer<typeof decision>;
export type SpendingPolicyResult = z.infer<typeof result>;
export const spendingRuleLabels = { every_order: 'Setiap pesanan perlu persetujuan', above_threshold: 'Persetujuan di atas ambang belanja', not_required: 'Tidak memerlukan persetujuan belanja' } as const;
export function spendingPolicyInput(value: SpendingPolicyInput) {
  const checked = input.safeParse(value); if (!checked.success) throw new RequestNotSentError(400, 'spending_policy_invalid'); return checked.data;
}
/** Form dates explicitly use WIB, independently of the browser's local timezone. */
export function spendingPolicyTime(value: string) {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$(?![\s\S])/.test(value) || !time.safeParse(value + ':00+07:00').success) throw new RequestNotSentError(400, 'spending_policy_invalid');
  return new Date(value + ':00+07:00').toISOString();
}
export function spendingPolicyThreshold(value: string) { try { return priceMoney(value); } catch { throw new RequestNotSentError(400, 'spending_policy_invalid'); } }
export function parseSpendingPolicyTargets(value: unknown, beforeId: string | null = null) {
  const checked = parse(targets, value); if (beforeId !== null && checked.items.some(item => BigInt(item.target.id) >= BigInt(parse(id, beforeId)))) return invalid(); return checked;
}
export function parseSpendingPolicyCurrent(value: unknown, owner: SpendingPolicyChoice) {
  const checked = parse(current, value); return sameTarget(checked.target, owner.target) && sameContext(checked.context, owner.context) ? checked : invalid();
}
export function parseSpendingPolicyProposal(value: unknown, proposalId?: string) {
  const checked = parse(proposal, value); return proposalId === undefined || checked.proposalId === proposalId ? checked : invalid();
}
const uuid = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
export function spendingPolicyKeyOwner(key: string, requestedOperation: string) {
  parse(operation, requestedOperation);
  const found = new RegExp(requestedOperation === 'ordering.spending.policy.propose' ? `^osp\\.(branch|division)\\.([1-9][0-9]{0,18})\\.(${uuid})$(?![\\s\\S])`
    : `^ospd\\.(approve|reject)\\.([1-9][0-9]{0,18})\\.(${uuid})$(?![\\s\\S])`).exec(key);
  if (!found || found[3] === '00000000-0000-0000-0000-000000000000' || requestedOperation !== 'ordering.spending.policy.propose' && requestedOperation !== 'ordering.spending.policy.' + found[1]) return invalid();
  return { kind: found[1], id: parse(id, found[2]) };
}
export function parseSpendingPolicyResult(value: unknown, key: string, requestedOperation: string) {
  const owner = spendingPolicyKeyOwner(key, requestedOperation), checked = parse(result, value);
  if (checked.operation !== requestedOperation || requestedOperation !== 'ordering.spending.policy.propose' && checked.proposalId !== owner.id) return invalid(); return checked;
}
export function prepareSpendingPolicy(actorId: string, selected: SpendingPolicyChoice, sent: SpendingPolicyInput, shown: SpendingPolicyPreview, reason: string) {
  const owner = parse(choice, selected), clean = spendingPolicyInput(sent), checked = parse(preview, shown), view = checked.view;
  if (!sameTarget(clean.target, owner.target) || !sameTarget(view.target, owner.target) || !sameContext(view.context, owner.context) || view.code !== owner.code || view.name !== owner.name ||
    view.scopeVersion !== owner.expectedScopeVersion || clean.expectedScopeVersion !== view.scopeVersion || view.profileVersion !== owner.expectedProfileVersion || clean.expectedProfileVersion !== view.profileVersion ||
    view.policyVersion !== owner.policyVersion || clean.expectedPolicyVersion !== view.policyVersion || !sameRule(view.rule, clean.rule) || !sameWindow(view, clean)) throw new RequestNotSentError(409, 'spending_policy_view_stale');
  return freezeCatalogAttempt({ actorId: parse(id, actorId), target: owner.target, body: { input: clean, binding: checked.binding, reason: parse(text(1000), reason) } });
}
export function prepareSpendingPolicyDecision(actorId: string, source: SpendingPolicyProposal, shown: SpendingPolicyDecisionPreview, reason: string) {
  const owner = parse(proposal, source), checked = parse(decisionPreview, shown), view = checked.view;
  if (owner.state !== 'pending' || JSON.stringify({ ...owner, decision: view.decision, validFrom: new Date(owner.validFrom).toISOString(), validUntil: new Date(owner.validUntil).toISOString(), proposedAt: new Date(owner.proposedAt).toISOString() }) !==
    JSON.stringify({ ...view, validFrom: new Date(view.validFrom).toISOString(), validUntil: new Date(view.validUntil).toISOString(), proposedAt: new Date(view.proposedAt).toISOString() })) throw new RequestNotSentError(409, 'spending_policy_view_stale');
  return freezeCatalogAttempt({ actorId: parse(id, actorId), proposalId: owner.proposalId, policyId: owner.policyId, revision: owner.revision, baseVersion: owner.baseVersion, decision: view.decision,
    body: { decision: view.decision, binding: checked.binding, reason: parse(text(1000), reason) } });
}
export function createSpendingPolicyGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async targets(actorId: string, beforeId: string | null = null, signal?: AbortSignal) { return parseSpendingPolicyTargets(data(await client.POST('/api/v1/ordering/spending-policies/targets/query', {
      body: { purpose: 'spending-policy-review', beforeId }, params: { header: headers(actorId) }, signal })), beforeId); },
    async read(actorId: string, owner: SpendingPolicyChoice, signal?: AbortSignal) { return parseSpendingPolicyCurrent(data(await client.POST('/api/v1/ordering/spending-policies/current/query', {
      body: { purpose: 'spending-policy-review', target: owner.target }, params: { header: headers(actorId) }, signal })), owner); },
    async proposals(actorId: string, owner: SpendingPolicyChoice, beforeId: string | null = null, signal?: AbortSignal) {
      const checked = parse(proposals, data(await client.POST('/api/v1/ordering/spending-policies/proposals/query', { body: { purpose: 'spending-policy-review', target: owner.target, beforeId }, params: { header: headers(actorId) }, signal })));
      if (checked.items.some(item => !sameTarget(item.target, owner.target) || !sameContext(item.context, owner.context) || beforeId !== null && BigInt(item.proposalId) >= BigInt(parse(id, beforeId)))) return invalid(); return checked;
    },
    async proposal(actorId: string, proposalId: string, signal?: AbortSignal) { return parseSpendingPolicyProposal(data(await client.POST('/api/v1/ordering/spending-policies/proposals/{id}/query', {
      body: { purpose: 'spending-policy-review' }, params: { path: { id: parse(id, proposalId) }, header: headers(actorId) }, signal })), proposalId); },
    async preview(actorId: string, value: SpendingPolicyInput, signal?: AbortSignal) { return parse(preview, data(await client.POST('/api/v1/ordering/spending-policies/preview/query', {
      body: { purpose: 'spending-policy-review', input: spendingPolicyInput(value) }, params: { header: headers(actorId) }, signal }))); },
    async previewDecision(actorId: string, source: SpendingPolicyProposal, selected: SpendingPolicyDecision, signal?: AbortSignal) {
      const checked = parse(decisionPreview, data(await client.POST('/api/v1/ordering/spending-policies/proposals/{id}/decision-preview/query', {
        body: { purpose: 'spending-policy-review', decision: selected }, params: { path: { id: parse(id, source.proposalId) }, header: headers(actorId) }, signal })));
      if (checked.view.proposalId !== source.proposalId || checked.view.decision !== selected) return invalid(); return checked;
    },
    async propose(command: ReturnType<typeof prepareSpendingPolicy>, key: string, isCurrent: () => boolean) {
      const op = 'ordering.spending.policy.propose', owner = spendingPolicyKeyOwner(key, op); if (owner.kind !== command.target.kind || owner.id !== command.target.id) return invalid(); requireCurrent(isCurrent);
      const saved = parseSpendingPolicyResult(data(await client.POST('/api/v1/ordering/spending-policies/proposals', { body: command.body, params: { header: { ...headers(command.actorId), 'Idempotency-Key': key } } })), key, op);
      if (!isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown'); return saved;
    },
    async decide(command: ReturnType<typeof prepareSpendingPolicyDecision>, key: string, isCurrent: () => boolean) {
      const op = 'ordering.spending.policy.' + command.decision, owner = spendingPolicyKeyOwner(key, op); if (owner.id !== command.proposalId) return invalid(); requireCurrent(isCurrent);
      const saved = parseSpendingPolicyResult(data(await client.POST('/api/v1/ordering/spending-policies/proposals/{id}/decisions', { body: command.body,
        params: { path: { id: command.proposalId }, header: { ...headers(command.actorId), 'If-Match': '"' + command.revision + '"', 'Idempotency-Key': key } } })), key, op);
      if (!isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown');
      if (saved.policyId !== command.policyId || command.decision === 'approve' && BigInt(saved.policyVersion!) !== BigInt(command.baseVersion) + BigInt(1)) return invalid(); return saved;
    },
    async recover(actorId: string, requestedOperation: string, key: string) {
      const owner = spendingPolicyKeyOwner(key, requestedOperation), checked = parse(recovery, data(await client.POST('/api/v1/ordering/spending-policies/operations/query', {
        body: { purpose: 'operation-recovery', operation: requestedOperation, key }, params: { header: headers(actorId) } })));
      if (checked.result === null) return null; const saved = parseSpendingPolicyResult(checked.result, key, requestedOperation);
      if (requestedOperation === 'ordering.spending.policy.propose') {
        const actual = parseSpendingPolicyProposal(data(await client.POST('/api/v1/ordering/spending-policies/proposals/{id}/query', { body: { purpose: 'spending-policy-review' }, params: { path: { id: saved.proposalId }, header: headers(actorId) } })), saved.proposalId);
        if (actual.target.kind !== owner.kind || actual.target.id !== owner.id || actual.policyId !== saved.policyId || Date.parse(actual.proposedAt) !== Date.parse(saved.recordedAt)) return invalid();
      }
      return { state: 'committed' as const, result: saved };
    },
  };
}
