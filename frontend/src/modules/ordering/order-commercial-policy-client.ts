import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { paymentAllocationSchemas } from '../billing/payment-allocation-client';
import { freezeCatalogAttempt } from '../catalog/catalog-editor-state';
import { priceMoney } from '../catalog/catalog-price-workflow';
import { spendingPolicyTime } from './order-spending-policy-client';

const { id, hash, money } = paymentAllocationSchemas;
const zeroVersion = z.union([z.literal('0'), id]).refine(value => BigInt(value) < BigInt('9223372036854775807'));
const version = zeroVersion.refine(value => value !== '0');
const expectedVersion = zeroVersion.refine(value => BigInt(value) < BigInt('9223372036854775806'));
const text = (maximum: number) => z.string().min(1).max(maximum).refine(value => value === value.normalize('NFKC').trim() && !/[\p{Cc}\p{Cf}\p{Co}\p{Cs}]/u.test(value));
const time = z.string().datetime({ offset: true }).refine(value => !/\.[0-9]{3}[0-9]*[1-9][0-9]*(?:Z|[+-][0-9]{2}:[0-9]{2})$/.test(value));
const day = z.string().regex(/^(?!0000)[0-9]{4}-[0-9]{2}-[0-9]{2}$(?![\s\S])/).refine(value => value !== '0001-01-01' && Number.isFinite(Date.parse(value)) && new Date(value).toISOString().slice(0, 10) === value);
const book = z.object({ organizationId: id, shopId: id, channelId: id, currency: z.literal('IDR') }).strict();
const sellerVersions = z.object({ organizationScopeVersion: version, organizationProfileVersion: version, shopScopeVersion: version,
  shopProfileVersion: version, channelScopeVersion: version, channelProfileVersion: version }).strict();
const seller = z.object({ organizationName: text(160), shopName: text(160), channelName: text(160) }).strict();
const sellerChoice = z.object({ seller: book, ...seller.shape, sellerVersions }).strict();
const sellerCursor = z.object({ shopId: id, channelId: id }).strict();
const target = z.object({ kind: z.enum(['branch', 'division']), id, seller: book }).strict();
const context = z.object({ organizationId: id, branchId: id, divisionId: id.nullable() }).strict();
const applicability = z.object({ budget: z.enum(['required', 'not_applicable']), costCenter: z.enum(['required', 'not_applicable']),
  spending: z.enum(['required', 'not_applicable']), creditHold: z.enum(['required', 'not_applicable']) }).strict();
const inputInvalid = (): never => { throw new RequestNotSentError(400, 'commercial_policy_invalid'); };
export function commercialPolicyTaxRate(value: string) {
  if (typeof value !== 'string' || !/^(?:0|[1-9][0-9]{0,2})(?:\.[0-9]{1,4})?$(?![\s\S])/.test(value)) return inputInvalid();
  const [whole, fraction = ''] = value.split('.');
  if (BigInt(whole) * BigInt(10000) + BigInt(fraction.padEnd(4, '0')) > BigInt(1000000)) return inputInvalid();
  return value.includes('.') ? value.replace(/0+$/, '').replace(/\.$/, '') : value;
}
const taxRate = z.string().refine(value => { try { return commercialPolicyTaxRate(value) === value; } catch { return false; } });
const terms = z.object({ grouping: z.enum(['receipt', 'buying_branch_period_channel', 'buying_org_period_channel']),
  termOrigin: z.enum(['issue_date', 'earliest_receipt', 'latest_receipt']), termDays: z.number().int().min(0).max(365),
  dayRule: z.enum(['calendar_days', 'working_days']), workingDays: z.array(z.number().int().min(0).max(6)).max(7), holidays: z.array(day).max(730),
  numberingProcedure: text(1000), reissueProcedure: text(1000), taxDocumentProcess: text(1000) }).strict().refine(value =>
  (value.dayRule !== 'working_days' || value.workingDays.length > 0) && value.workingDays.every((item, index) => index === 0 || item > value.workingDays[index - 1]) &&
  value.holidays.every((item, index) => index === 0 || item > value.holidays[index - 1]));
const rule = z.object({ applicability, charges: money, taxTreatment: z.enum(['taxable_exclusive', 'exempt']), taxRate, exemptionReference: text(1000).nullable(),
  taxBase: z.enum(['subtotal', 'subtotal_and_charges']), taxRounding: z.enum(['line', 'document']), rounding: z.enum(['half_even', 'half_up']), terms }).strict().refine(value =>
  value.taxTreatment === 'exempt' ? value.taxRate === '0' && value.exemptionReference !== null : value.taxRate !== '0' && value.exemptionReference === null);
const sameBook = (left: z.infer<typeof book>, right: z.infer<typeof book>) => left.organizationId === right.organizationId && left.shopId === right.shopId && left.channelId === right.channelId && left.currency === right.currency;
const sameTarget = (left: z.infer<typeof target>, right: z.infer<typeof target>) => left.kind === right.kind && left.id === right.id && sameBook(left.seller, right.seller);
const sameContext = (left: z.infer<typeof context>, right: z.infer<typeof context>) => left.organizationId === right.organizationId && left.branchId === right.branchId && left.divisionId === right.divisionId;
const scoped = (value: { target: z.infer<typeof target>; context: z.infer<typeof context> }) => value.context.organizationId !== value.context.branchId &&
  (value.target.kind === 'branch' ? value.target.id === value.context.branchId && value.context.divisionId === null : value.target.id === value.context.divisionId &&
    value.context.divisionId !== value.context.organizationId && value.context.divisionId !== value.context.branchId);
const windowValid = (value: { validFrom: string; validUntil: string }) => Date.parse(value.validFrom) < Date.parse(value.validUntil);
const sameWindow = (left: { validFrom: string; validUntil: string }, right: { validFrom: string; validUntil: string }) => Date.parse(left.validFrom) === Date.parse(right.validFrom) && Date.parse(left.validUntil) === Date.parse(right.validUntil);
const same = (left: unknown, right: unknown) => JSON.stringify(left) === JSON.stringify(right);
const beforeSeller = (left: z.infer<typeof sellerCursor>, right: z.infer<typeof sellerCursor>) => BigInt(left.shopId) < BigInt(right.shopId) || left.shopId === right.shopId && BigInt(left.channelId) < BigInt(right.channelId);
const sellers = z.object({ items: z.array(sellerChoice).max(25), nextSeller: sellerCursor.nullable() }).strict().refine(value =>
  value.items.every((item, index) => index === 0 || beforeSeller(item.seller, value.items[index - 1].seller)) &&
  (value.nextSeller === null || value.items.length === 0 || !beforeSeller(value.items.at(-1)!.seller, value.nextSeller)));
const choiceShape = { target, context, code: text(64), name: text(160), seller, sellerVersions, expectedScopeVersion: version, expectedProfileVersion: version,
  policyVersion: zeroVersion, policyId: id.nullable(), versionId: id.nullable() };
const choice = z.object(choiceShape).strict().refine(scoped).refine(value => value.policyVersion === '0' ? value.versionId === null : value.policyId !== null && value.versionId !== null);
const targets = z.object({ items: z.array(choice).max(25), nextTargetId: id.nullable() }).strict().refine(value =>
  value.items.every((item, index) => index === 0 || BigInt(item.target.id) < BigInt(value.items[index - 1].target.id)) &&
  (value.nextTargetId === null || value.items.length === 0 || BigInt(value.nextTargetId) <= BigInt(value.items.at(-1)!.target.id)));
const input = z.object({ target, expectedScopeVersion: version, expectedProfileVersion: version, sellerVersions, expectedPolicyVersion: expectedVersion, rule, validFrom: time, validUntil: time }).strict().refine(windowValid);
const previewView = z.object({ target, context, code: text(64), name: text(160), seller, sellerVersions, scopeVersion: version, profileVersion: version,
  policyVersion: zeroVersion, rule, validFrom: time, validUntil: time, sourceHash: hash }).strict().refine(scoped).refine(windowValid);
const preview = z.object({ binding: hash, view: previewView }).strict();
const current = z.object({ target, context, code: text(64), name: text(160), seller, sellerVersions, expectedScopeVersion: version, expectedProfileVersion: version,
  policyId: id.nullable(), version: zeroVersion, versionId: id.nullable(), rule: rule.nullable(), validFrom: time.nullable(), validUntil: time.nullable(), approvedAt: time.nullable(),
  current: z.boolean(), active: z.boolean() }).strict().refine(scoped).refine(value => value.version === '0' ? value.versionId === null && value.rule === null &&
    value.validFrom === null && value.validUntil === null && value.approvedAt === null && !value.current && !value.active : value.policyId !== null && value.versionId !== null &&
    value.rule !== null && value.validFrom !== null && value.validUntil !== null && value.approvedAt !== null && Date.parse(value.validFrom) < Date.parse(value.validUntil) && (!value.active || value.current));
const decision = z.enum(['approve', 'reject']);
const proposalShape = { proposalId: id, policyId: id, revision: z.literal('1'), target, context, code: text(64), name: text(160), seller, sellerVersions,
  rule, validFrom: time, validUntil: time, baseVersion: expectedVersion, proposedAt: time, state: z.enum(['pending', 'approved', 'rejected']), decision: decision.nullable(),
  policyVersion: version.nullable(), versionId: id.nullable(), bindingCurrent: z.boolean() };
const proposal = z.object(proposalShape).strict().refine(scoped).refine(windowValid).refine(value => value.state === 'approved' ? value.decision === 'approve' &&
  value.policyVersion !== null && BigInt(value.policyVersion) === BigInt(value.baseVersion) + BigInt(1) && value.versionId !== null :
  value.decision === (value.state === 'pending' ? null : 'reject') && value.policyVersion === null && value.versionId === null);
const proposals = z.object({ items: z.array(proposal).max(25), nextProposalId: id.nullable() }).strict().refine(value => value.items.every((item, index) =>
  index === 0 || BigInt(item.proposalId) < BigInt(value.items[index - 1].proposalId)) && (value.nextProposalId === null || value.items.length > 0 && value.nextProposalId === value.items.at(-1)!.proposalId));
const decisionPreview = z.object({ binding: hash, view: z.object({ ...proposalShape, decision }).strict().refine(scoped).refine(windowValid).refine(value =>
  value.state === 'pending' && value.policyVersion === null && value.versionId === null && (value.decision === 'reject' || value.bindingCurrent)) }).strict();
const operation = z.enum(['ordering.commercial.policy.propose', 'ordering.commercial.policy.approve', 'ordering.commercial.policy.reject']);
const result = z.object({ policyId: id, proposalId: id, recordId: id, operation, policyVersion: version.nullable(), versionId: id.nullable(), recordedAt: time }).strict().refine(value =>
  value.operation === 'ordering.commercial.policy.approve' ? value.policyVersion !== null && value.versionId !== null : value.policyVersion === null && value.versionId === null);
const recovery = z.object({ status: z.enum(['confirmed', 'not_confirmed']), result: result.nullable() }).strict().refine(value => (value.status === 'confirmed') === (value.result !== null));
const invalid = (): never => { throw new AccessFailure(502, 'commercial_policy_response_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const checked = schema.safeParse(value); return checked.success ? checked.data : invalid(); }
function data<T>(response: { data?: T; error?: unknown; response: Response }): T {
  if (!response.response.ok || response.data === undefined) throw new AccessFailure(response.response.status, (response.error as { code?: string })?.code ?? 'request_failed'); return response.data;
}
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': parse(id, actorId) });
const requireCurrent = (isCurrent: () => boolean) => { if (!isCurrent()) throw new RequestNotSentError(409, 'commercial_policy_view_stale'); };
export type CommercialPolicySeller = z.infer<typeof sellerChoice>;
export type CommercialPolicySellerChoice = CommercialPolicySeller;
export type CommercialPolicySellerCursor = z.infer<typeof sellerCursor>;
export type CommercialPolicyTarget = z.infer<typeof target>;
export type CommercialPolicyRule = z.infer<typeof rule>;
export type CommercialPolicyChoice = z.infer<typeof choice>;
export type CommercialPolicyInput = z.infer<typeof input>;
export type CommercialPolicyCurrent = z.infer<typeof current>;
export type CommercialPolicyProposal = z.infer<typeof proposal>;
export type CommercialPolicyPreview = z.infer<typeof preview>;
export type CommercialPolicyDecisionPreview = z.infer<typeof decisionPreview>;
export type CommercialPolicyDecision = z.infer<typeof decision>;
export type CommercialPolicyResult = z.infer<typeof result>;
export function commercialPolicyInput(value: CommercialPolicyInput) { const checked = input.safeParse(value); return checked.success ? checked.data : inputInvalid(); }
export function commercialPolicyTime(value: string) { try { return spendingPolicyTime(value); } catch { return inputInvalid(); } }
export function commercialPolicyCharge(value: string) { try { return priceMoney(value); } catch { return inputInvalid(); } }
export function parseCommercialPolicySellers(value: unknown, cursor: CommercialPolicySellerCursor | null = null) {
  const checked = parse(sellers, value); const before = cursor === null ? null : parse(sellerCursor, cursor);
  if (before !== null && (checked.items.some(item => !beforeSeller(item.seller, before)) || checked.nextSeller !== null && !beforeSeller(checked.nextSeller, before))) return invalid(); return checked;
}
export function parseCommercialPolicyTargets(value: unknown, owner: CommercialPolicySeller, beforeId: string | null = null) {
  const checked = parse(targets, value), selected = parse(sellerChoice, owner), before = beforeId === null ? null : parse(id, beforeId);
  if (checked.items.some(item => !sameBook(item.target.seller, selected.seller) || !same(item.sellerVersions, selected.sellerVersions) ||
      !same(item.seller, { organizationName: selected.organizationName, shopName: selected.shopName, channelName: selected.channelName }) || before !== null && BigInt(item.target.id) >= BigInt(before)) ||
      before !== null && checked.nextTargetId !== null && BigInt(checked.nextTargetId) >= BigInt(before)) return invalid(); return checked;
}
export function parseCommercialPolicyCurrent(value: unknown, owner: CommercialPolicyChoice) {
  const checked = parse(current, value), selected = parse(choice, owner);
  return sameTarget(checked.target, selected.target) && sameContext(checked.context, selected.context) ? checked : invalid();
}
export function parseCommercialPolicyProposal(value: unknown, proposalId?: string) { const checked = parse(proposal, value); return proposalId === undefined || checked.proposalId === proposalId ? checked : invalid(); }
const uuid = '[0-9a-f]{32}';
export function commercialPolicyKeyOwner(key: string, requestedOperation: string) {
  parse(operation, requestedOperation); if (typeof key !== 'string' || key.length > 128) return invalid();
  if (requestedOperation === 'ordering.commercial.policy.propose') {
    const found = new RegExp(`^ocp\\.(b|d)\\.([1-9][0-9]{0,18})\\.([1-9][0-9]{0,18})\\.([1-9][0-9]{0,18})\\.([1-9][0-9]{0,18})\\.(${uuid})$(?![\\s\\S])`).exec(key);
    if (!found || found[6] === '0'.repeat(32)) return invalid();
    return { kind: found[1] === 'b' ? 'branch' : 'division', id: parse(id, found[2]), seller: parse(book, { organizationId: found[3], shopId: found[4], channelId: found[5], currency: 'IDR' }) };
  }
  const found = new RegExp(`^ocpd\\.(approve|reject)\\.([1-9][0-9]{0,18})\\.(${uuid})$(?![\\s\\S])`).exec(key);
  if (!found || found[3] === '0'.repeat(32) || requestedOperation !== 'ordering.commercial.policy.' + found[1]) return invalid();
  return { kind: found[1], id: parse(id, found[2]), seller: null };
}
export function parseCommercialPolicyResult(value: unknown, key: string, requestedOperation: string) {
  const owner = commercialPolicyKeyOwner(key, requestedOperation), checked = parse(result, value);
  if (checked.operation !== requestedOperation || requestedOperation !== 'ordering.commercial.policy.propose' && checked.proposalId !== owner.id ||
    requestedOperation === 'ordering.commercial.policy.propose' && checked.recordId !== checked.proposalId) return invalid(); return checked;
}
export function prepareCommercialPolicy(actorId: string, selected: CommercialPolicyChoice, sent: CommercialPolicyInput, shown: CommercialPolicyPreview, reason: string) {
  const owner = parse(choice, selected), clean = commercialPolicyInput(sent), checked = parse(preview, shown), view = checked.view;
  if (!sameTarget(clean.target, owner.target) || !sameTarget(view.target, owner.target) || !sameContext(view.context, owner.context) || view.code !== owner.code || view.name !== owner.name ||
    !same(view.seller, owner.seller) || !same(view.sellerVersions, owner.sellerVersions) || !same(clean.sellerVersions, view.sellerVersions) ||
    view.scopeVersion !== owner.expectedScopeVersion || clean.expectedScopeVersion !== view.scopeVersion || view.profileVersion !== owner.expectedProfileVersion || clean.expectedProfileVersion !== view.profileVersion ||
    view.policyVersion !== owner.policyVersion || clean.expectedPolicyVersion !== view.policyVersion || !same(view.rule, clean.rule) || !sameWindow(view, clean))
    throw new RequestNotSentError(409, 'commercial_policy_view_stale');
  return freezeCatalogAttempt({ actorId: parse(id, actorId), target: owner.target, body: { input: clean, binding: checked.binding, reason: parse(text(1000), reason) } });
}
export function prepareCommercialPolicyDecision(actorId: string, source: CommercialPolicyProposal, shown: CommercialPolicyDecisionPreview, reason: string) {
  const owner = parse(proposal, source), checked = parse(decisionPreview, shown), view = checked.view;
  const normalized = (value: CommercialPolicyProposal | CommercialPolicyDecisionPreview['view']) => ({ ...value, decision: view.decision,
    validFrom: new Date(value.validFrom).toISOString(), validUntil: new Date(value.validUntil).toISOString(), proposedAt: new Date(value.proposedAt).toISOString() });
  if (owner.state !== 'pending' || !same(normalized(owner), normalized(view))) throw new RequestNotSentError(409, 'commercial_policy_view_stale');
  return freezeCatalogAttempt({ actorId: parse(id, actorId), proposalId: owner.proposalId, policyId: owner.policyId, revision: owner.revision, baseVersion: owner.baseVersion,
    target: owner.target, decision: view.decision, body: { decision: view.decision, binding: checked.binding, reason: parse(text(1000), reason) } });
}
export function createCommercialPolicyGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async sellers(actorId: string, cursor: CommercialPolicySellerCursor | null = null, signal?: AbortSignal) {
      const before = cursor === null ? null : parse(sellerCursor, cursor);
      return parseCommercialPolicySellers(data(await client.POST('/api/v1/ordering/commercial-policies/sellers/query', { body: { purpose: 'commercial-policy-review',
        beforeShopId: before?.shopId ?? null, beforeChannelId: before?.channelId ?? null }, params: { header: headers(actorId) }, signal })), before);
    },
    async targets(actorId: string, owner: CommercialPolicySeller, beforeId: string | null = null, signal?: AbortSignal) {
      const selected = parse(sellerChoice, owner);
      return parseCommercialPolicyTargets(data(await client.POST('/api/v1/ordering/commercial-policies/targets/query', { body: { purpose: 'commercial-policy-review',
        seller: selected.seller, beforeId }, params: { header: headers(actorId) }, signal })), selected, beforeId);
    },
    async read(actorId: string, owner: CommercialPolicyChoice, signal?: AbortSignal) { return parseCommercialPolicyCurrent(data(await client.POST('/api/v1/ordering/commercial-policies/current/query', {
      body: { purpose: 'commercial-policy-review', target: parse(choice, owner).target }, params: { header: headers(actorId) }, signal })), owner); },
    async proposals(actorId: string, owner: CommercialPolicyChoice, beforeId: string | null = null, signal?: AbortSignal) {
      const selected = parse(choice, owner), checked = parse(proposals, data(await client.POST('/api/v1/ordering/commercial-policies/proposals/query', {
        body: { purpose: 'commercial-policy-review', target: selected.target, beforeId }, params: { header: headers(actorId) }, signal })));
      if (checked.items.some(item => !sameTarget(item.target, selected.target) || !sameContext(item.context, selected.context) ||
        beforeId !== null && BigInt(item.proposalId) >= BigInt(parse(id, beforeId)))) return invalid(); return checked;
    },
    async proposal(actorId: string, proposalId: string, signal?: AbortSignal) { return parseCommercialPolicyProposal(data(await client.POST('/api/v1/ordering/commercial-policies/proposals/{id}/query', {
      body: { purpose: 'commercial-policy-review' }, params: { path: { id: parse(id, proposalId) }, header: headers(actorId) }, signal })), proposalId); },
    async preview(actorId: string, value: CommercialPolicyInput, signal?: AbortSignal) { return parse(preview, data(await client.POST('/api/v1/ordering/commercial-policies/preview/query', {
      body: { purpose: 'commercial-policy-review', input: commercialPolicyInput(value) }, params: { header: headers(actorId) }, signal }))); },
    async previewDecision(actorId: string, source: CommercialPolicyProposal, selected: CommercialPolicyDecision, signal?: AbortSignal) {
      const checked = parse(decisionPreview, data(await client.POST('/api/v1/ordering/commercial-policies/proposals/{id}/decision-preview/query', {
        body: { purpose: 'commercial-policy-review', decision: parse(decision, selected) }, params: { path: { id: parse(id, source.proposalId) }, header: headers(actorId) }, signal })));
      if (checked.view.proposalId !== source.proposalId || checked.view.decision !== selected) return invalid(); return checked;
    },
    async propose(command: ReturnType<typeof prepareCommercialPolicy>, key: string, isCurrent: () => boolean) {
      const op = 'ordering.commercial.policy.propose', owner = commercialPolicyKeyOwner(key, op);
      if (owner.kind !== command.target.kind || owner.id !== command.target.id || owner.seller === null || !sameBook(owner.seller, command.target.seller)) return invalid(); requireCurrent(isCurrent);
      const saved = parseCommercialPolicyResult(data(await client.POST('/api/v1/ordering/commercial-policies/proposals', { body: command.body,
        params: { header: { ...headers(command.actorId), 'Idempotency-Key': key } } })), key, op);
      if (!isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown'); return saved;
    },
    async decide(command: ReturnType<typeof prepareCommercialPolicyDecision>, key: string, isCurrent: () => boolean) {
      const op = 'ordering.commercial.policy.' + command.decision, owner = commercialPolicyKeyOwner(key, op); if (owner.id !== command.proposalId) return invalid(); requireCurrent(isCurrent);
      const saved = parseCommercialPolicyResult(data(await client.POST('/api/v1/ordering/commercial-policies/proposals/{id}/decisions', { body: command.body,
        params: { path: { id: command.proposalId }, header: { ...headers(command.actorId), 'If-Match': '"' + command.revision + '"', 'Idempotency-Key': key } } })), key, op);
      if (!isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown');
      if (saved.policyId !== command.policyId || command.decision === 'approve' && BigInt(saved.policyVersion!) !== BigInt(command.baseVersion) + BigInt(1)) return invalid(); return saved;
    },
    async recover(actorId: string, requestedOperation: string, key: string) {
      const requested = parse(operation, requestedOperation);
      const owner = commercialPolicyKeyOwner(key, requestedOperation), checked = parse(recovery, data(await client.POST('/api/v1/ordering/commercial-policies/operations/query', {
        body: { purpose: 'operation-recovery', operation: requested, key }, params: { header: headers(actorId) } })));
      if (checked.result === null) return null; const saved = parseCommercialPolicyResult(checked.result, key, requestedOperation);
      const actual = parseCommercialPolicyProposal(data(await client.POST('/api/v1/ordering/commercial-policies/proposals/{id}/query', {
        body: { purpose: 'commercial-policy-review' }, params: { path: { id: saved.proposalId }, header: headers(actorId) } })), saved.proposalId);
      if (actual.policyId !== saved.policyId || (requestedOperation === 'ordering.commercial.policy.propose' ?
        owner.seller === null || actual.target.kind !== owner.kind || actual.target.id !== owner.id || !sameBook(actual.target.seller, owner.seller) || Date.parse(actual.proposedAt) !== Date.parse(saved.recordedAt) :
        actual.decision !== owner.kind || actual.state !== (owner.kind === 'approve' ? 'approved' : 'rejected') || actual.policyVersion !== saved.policyVersion || actual.versionId !== saved.versionId ||
          Date.parse(saved.recordedAt) < Date.parse(actual.proposedAt))) return invalid();
      return { state: 'committed' as const, result: saved };
    },
  };
}
