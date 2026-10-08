import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { paymentAllocationSchemas } from '../billing/payment-allocation-client';
import { freezeCatalogAttempt } from '../catalog/catalog-editor-state';
import { packageMatchingSchema, packageMatchesFit } from './package-matching';

const { id, hash } = paymentAllocationSchemas;
const zeroVersion = z.union([z.literal('0'), id]);
const userText = (max: number) => z.string().min(1).max(max).refine(value => !/^\p{Z}|\p{Z}$|\p{Cc}/u.test(value));
const labelText = (max: number) => userText(max).refine(value => value === value.normalize('NFKC') && !/[\p{Cf}\p{Co}\p{Cs}]/u.test(value));
const time = z.string().datetime({ offset: true });
const utc = time.refine(value => /(?:Z|\+00:00)$/.test(value) && !/\.\d{4,}/.test(value) && Date.parse(value) > Date.parse('0001-01-01T00:00:00Z'));
const owner = z.object({ organizationId: id, shopId: id, channelId: id }).strict().refine(value => new Set(Object.values(value)).size === 3);
const location = z.object({ organizationId: id, shopId: id, warehouseId: id, channelId: id }).strict().refine(value => new Set(Object.values(value)).size === 4);
const label = z.object({ id, code: labelText(64), name: labelText(160), scopeVersion: id, profileVersion: id }).strict();
const source = z.object({ organization: label, shop: label, channel: label }).strict();
const inOwner = (where: z.infer<typeof location>, scope: z.infer<typeof owner>) => where.organizationId === scope.organizationId && where.shopId === scope.shopId && where.channelId === scope.channelId;
export const supplierOwnerKey = (value: z.infer<typeof owner>) => [value.organizationId, value.shopId, value.channelId].join(':');
const sameOwner = (left: z.infer<typeof owner>, right: z.infer<typeof owner>) => supplierOwnerKey(left) === supplierOwnerKey(right);
const labelsMatch = (scope: z.infer<typeof owner>, names: z.infer<typeof source>) => scope.organizationId === names.organization.id && scope.shopId === names.shop.id && scope.channelId === names.channel.id;
const target = z.object({ owner, ...source.shape }).strict().refine(value => labelsMatch(value.owner, value));
const targets = z.object({ items: z.array(target).max(256) }).strict().refine(value => new Set(value.items.map(item => supplierOwnerKey(item.owner))).size === value.items.length);
const warehouse = z.object({ location, ...source.shape, warehouse: label }).strict().refine(value => labelsMatch(value.location, value) && value.location.warehouseId === value.warehouse.id);
const locations = z.object({ items: z.array(warehouse).max(256) }).strict().refine(value => new Set(value.items.map(item => item.location.warehouseId)).size === value.items.length);
const packageChoice = z.object({ skuId: id, skuRevision: id, definitionId: id, definitionVersion: id, skuCode: labelText(64), familyName: labelText(160), packageLabel: labelText(160),
  packageKind: z.enum(['loose', 'sealed', 'other']), baseUnitCode: labelText(64), baseUnitsPerPackage: z.number().int().min(1).max(2_147_483_647) }).strict();
const packagePage = z.object({ items: z.array(packageChoice).max(25), offset: z.number().int().min(0).max(10000), hasMore: z.boolean(), matching: packageMatchingSchema }).strict().refine(value => new Set(value.items.map(item => item.skuId)).size === value.items.length && (!value.hasMore || value.items.length > 0) && packageMatchesFit(value.items, value.matching));
const registration = { registrationNamespace: userText(64).nullable(), registrationValue: userText(128).nullable() };
const registrationMatches = (value: { registrationNamespace: string | null; registrationValue: string | null }) => (value.registrationNamespace === null) === (value.registrationValue === null);
const details = z.object({ subjectReference: userText(128), legalName: userText(160), displayName: userText(160), ...registration, address: userText(500), contactName: userText(160), contactMethod: z.enum(['email', 'phone', 'other']), contactValue: userText(256) }).strict().refine(registrationMatches);
const policy = z.object({ id, owner, revision: id, subjectReference: userText(128), legalName: userText(160), ...registration,
  method: z.enum(['established-contact', 'authenticated-record']), trustedChannelReference: userText(256), operatingOwnerReference: userText(256), approvalReference: userText(256),
  approvedAt: time, expiresAt: time, maxObservationAgeSeconds: id.refine(value => BigInt(value) <= BigInt(31536000)), allowReferenceOnly: z.boolean(), active: z.boolean() }).strict()
  .refine(registrationMatches).refine(value => Date.parse(value.expiresAt) > Date.parse(value.approvedAt));
const policies = z.object({ items: z.array(policy).max(256) }).strict().refine(value => new Set(value.items.map(item => item.id)).size === value.items.length);
const mapping = z.object({ location, sourceSystem: z.enum(['omi', 's_mart', 'supplier']), sourceAccountReference: userText(128), externalItemCode: userText(128),
  skuId: id, expectedSkuRevision: id, definitionId: id, definitionVersion: id, validFrom: utc, validUntil: utc }).strict().refine(value => Date.parse(value.validFrom) < Date.parse(value.validUntil));
const mappings = z.array(mapping).min(1).max(100).refine(values => values.every((value, index) => values.slice(index + 1).every(other =>
  value.sourceSystem !== other.sourceSystem || value.sourceAccountReference !== other.sourceAccountReference || value.externalItemCode !== other.externalItemCode ||
  Date.parse(value.validFrom) >= Date.parse(other.validUntil) || Date.parse(other.validFrom) >= Date.parse(value.validUntil))));
const input = z.object({ owner, supplierId: id.nullable(), baseVersion: zeroVersion.refine(value => BigInt(value) < BigInt('9223372036854775806')), policyId: id, policyRevision: id, details, mappings }).strict()
  .refine(value => (value.supplierId !== null || value.baseVersion === '0') && value.mappings.every(item => inOwner(item.location, value.owner)));
const mappingSource = z.object({ location, ...source.shape, warehouse: label, package: packageChoice }).strict().refine(value => labelsMatch(value.location, value) && value.warehouse.id === value.location.warehouseId);
const mappingSources = z.array(mappingSource).min(1).max(100);
const boundIdentity = (value: z.infer<typeof details>, approved: z.infer<typeof policy>) => value.subjectReference === approved.subjectReference && value.legalName === approved.legalName && value.registrationNamespace === approved.registrationNamespace && value.registrationValue === approved.registrationValue;
const facts = { input, source, mappingSources, policy };
const factsMatch = (value: { input: z.infer<typeof input>; source: z.infer<typeof source>; mappingSources: z.infer<typeof mappingSources>; policy: z.infer<typeof policy> }) =>
  labelsMatch(value.input.owner, value.source) && sameOwner(value.input.owner, value.policy.owner) && boundIdentity(value.input.details, value.policy) && value.policy.id === value.input.policyId && value.policy.revision === value.input.policyRevision &&
  value.mappingSources.length === value.input.mappings.length && value.mappingSources.every((shown, index) => { const sent = value.input.mappings[index]; return JSON.stringify(shown.location) === JSON.stringify(sent.location) &&
    shown.package.skuId === sent.skuId && shown.package.skuRevision === sent.expectedSkuRevision && shown.package.definitionId === sent.definitionId && shown.package.definitionVersion === sent.definitionVersion; });
const preview = z.object({ ...facts, binding: hash, observedAt: time }).strict().refine(factsMatch);
const observation = z.object({ id, observed: details, sourceReference: userText(256), trustedChannelReference: userText(256), reason: userText(1000), checkedAt: time }).strict();
const decision = z.enum(['approve', 'reject']);
const decisionRecord = z.object({ id, decision, reason: userText(1000), decidedAt: time, versionId: id.nullable(), version: id.nullable() }).strict();
const proposal = z.object({ id, supplierId: id, version: z.literal('1'), ...facts, reason: userText(1000), proposedAt: time, state: z.enum(['pending', 'approved', 'rejected']), observation: observation.nullable(), decision: decisionRecord.nullable() }).strict()
  .refine(factsMatch).refine(value => {
    if (value.input.supplierId !== null && value.input.supplierId !== value.supplierId || value.observation && (Date.parse(value.observation.checkedAt) < Date.parse(value.proposedAt) || value.observation.trustedChannelReference !== value.policy.trustedChannelReference)) return false;
    if (value.state === 'pending') return value.decision === null;
    if (!value.decision || Date.parse(value.decision.decidedAt) < Date.parse(value.proposedAt)) return false;
    if (value.state === 'rejected') return value.decision.decision === 'reject' && value.decision.versionId === null && value.decision.version === null;
    return value.decision.decision === 'approve' && value.decision.versionId !== null && value.decision.version !== null && BigInt(value.decision.version) === BigInt(value.input.baseVersion) + BigInt(1) &&
      value.observation !== null && JSON.stringify(value.observation.observed) === JSON.stringify(value.input.details) && Date.parse(value.decision.decidedAt) >= Date.parse(value.observation.checkedAt);
  });
const current = z.object({ supplierId: id, versionId: id.nullable(), version: zeroVersion, proposal: proposal.nullable() }).strict().refine(value => value.version === '0' ? value.versionId === null && value.proposal === null :
  value.versionId !== null && value.proposal?.supplierId === value.supplierId && value.proposal.state === 'approved' && value.proposal.decision?.version === value.version && value.proposal.decision.versionId === value.versionId);
const history = z.object({ owner, items: z.array(proposal).max(25), nextProposalId: id.nullable() }).strict().refine(value => value.items.every((item, index) => sameOwner(item.input.owner, value.owner) &&
  (index === 0 || BigInt(item.id) < BigInt(value.items[index - 1].id))) && (value.nextProposalId === null || value.items.length > 0 && value.nextProposalId === value.items.at(-1)!.id));
const decisionPreview = z.object({ proposal, decision, observationId: id.nullable(), binding: hash, observedAt: time }).strict().refine(value => value.proposal.state === 'pending' && Date.parse(value.observedAt) >= Date.parse(value.proposal.proposedAt) &&
  (value.decision === 'reject' ? value.observationId === null : value.proposal.observation !== null && value.observationId === value.proposal.observation.id));
const operation = z.enum(['procurement.supplier.propose', 'procurement.supplier.observe', 'procurement.supplier.approve', 'procurement.supplier.reject']);
const result = z.object({ supplierId: id, proposalId: id, proposalVersion: z.literal('1'), recordId: id, operation, versionId: id.nullable(), version: id.nullable() }).strict().refine(value =>
  (value.operation === 'procurement.supplier.approve' ? value.versionId !== null && value.version !== null : value.versionId === null && value.version === null) && (value.operation !== 'procurement.supplier.propose' || value.recordId === value.proposalId));
const recovery = z.object({ status: z.enum(['confirmed', 'not_confirmed']), result: result.nullable() }).strict().refine(value => (value.status === 'confirmed') === (value.result !== null));
const access = z.enum(['propose', 'approve']);
const invalid = (): never => { throw new AccessFailure(502, 'supplier_response_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const checked = schema.safeParse(value); return checked.success ? checked.data : invalid(); }
function entered<T>(schema: z.ZodType<T>, value: unknown): T { const checked = schema.safeParse(value); if (!checked.success) throw new RequestNotSentError(400, 'invalid_supplier_change'); return checked.data; }
function data<T>(response: { data?: T; error?: unknown; response: Response }): T { if (!response.response.ok || response.data === undefined) throw new AccessFailure(response.response.status, (response.error as { code?: string })?.code ?? 'request_failed'); return response.data; }
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': parse(id, actorId) });
const read = (value: SupplierAccess) => ({ purpose: 'supplier-review', access: parse(access, value) });
const ordinal = (left: string, right: string) => left < right ? -1 : left > right ? 1 : 0;
export type SupplierAccess = z.infer<typeof access>;
export type SupplierOwner = z.infer<typeof owner>;
export type SupplierTarget = z.infer<typeof target>;
export type SupplierLocation = z.infer<typeof warehouse>;
export type SupplierPackage = z.infer<typeof packageChoice>;
export type SupplierDetails = z.infer<typeof details>;
export type SupplierPolicy = z.infer<typeof policy>;
export type SupplierInput = z.infer<typeof input>;
export type SupplierMappingSource = z.infer<typeof mappingSource>;
export type SupplierPreview = z.infer<typeof preview>;
export type SupplierProposal = z.infer<typeof proposal>;
export type SupplierCurrent = z.infer<typeof current>;
export type SupplierDecision = z.infer<typeof decision>;
export type SupplierDecisionPreview = z.infer<typeof decisionPreview>;
export type SupplierResult = z.infer<typeof result>;
export function supplierTime(value: string) {
  if (!/^(?!0000)[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}$(?![\s\S])/.test(value)) throw new RequestNotSentError(400, 'invalid_supplier_change');
  const parsed = new Date(value + ':00+07:00'); if (!Number.isFinite(parsed.getTime()) || new Date(parsed.getTime() + 7 * 60 * 60 * 1000).toISOString().slice(0, 16) !== value) throw new RequestNotSentError(400, 'invalid_supplier_change'); return entered(utc, parsed.toISOString());
}
export function supplierInput(value: SupplierInput) {
  const checked = entered(input, value), cleanMappings = checked.mappings.map(item => ({ ...item, validFrom: new Date(item.validFrom).toISOString(), validUntil: new Date(item.validUntil).toISOString() }));
  return { ...checked, mappings: cleanMappings.sort((a, b) => ordinal(a.sourceSystem, b.sourceSystem) || ordinal(a.sourceAccountReference, b.sourceAccountReference) || ordinal(a.externalItemCode, b.externalItemCode) ||
    (BigInt(a.location.warehouseId) < BigInt(b.location.warehouseId) ? -1 : BigInt(a.location.warehouseId) > BigInt(b.location.warehouseId) ? 1 : 0) || (BigInt(a.skuId) < BigInt(b.skuId) ? -1 : BigInt(a.skuId) > BigInt(b.skuId) ? 1 : 0) || ordinal(a.validFrom, b.validFrom)) };
}
export function supplierPolicyCurrent(value: SupplierPolicy, now: number) { return value.active && Date.parse(value.approvedAt) <= now && now < Date.parse(value.expiresAt); }
export function parseSupplierProposal(value: unknown, expectedId?: string) { const checked = parse(proposal, value); return expectedId === undefined || checked.id === expectedId ? checked : invalid(); }
export function parseSupplierTargets(value: unknown) { return parse(targets, value); }
const uuid = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
export function supplierKeyOwner(value: string, expectedOperation: string) {
  parse(operation, expectedOperation);
  if (expectedOperation === 'procurement.supplier.propose') {
    const found = new RegExp(`^sup\\.([1-9][0-9]{0,18})\\.([1-9][0-9]{0,18})\\.([1-9][0-9]{0,18})\\.(0|[1-9][0-9]{0,18})\\.(${uuid})$(?![\\s\\S])`).exec(value);
    if (!found || found[5] === '00000000-0000-0000-0000-000000000000') return invalid();
    return { owner: parse(owner, { organizationId: found[1], shopId: found[2], channelId: found[3] }), supplierId: found[4] === '0' ? null : parse(id, found[4]), proposalId: null };
  }
  const found = new RegExp(`^sua\\.(observe|approve|reject)\\.([1-9][0-9]{0,18})\\.(${uuid})$(?![\\s\\S])`).exec(value);
  if (!found || expectedOperation !== 'procurement.supplier.' + found[1] || found[3] === '00000000-0000-0000-0000-000000000000') return invalid(); return { owner: null, supplierId: null, proposalId: parse(id, found[2]) };
}
export function parseSupplierResult(value: unknown, requestKey: string, expectedOperation: string) {
  const owner = supplierKeyOwner(requestKey, expectedOperation), checked = parse(result, value);
  if (checked.operation !== expectedOperation || owner.supplierId !== null && checked.supplierId !== owner.supplierId || owner.proposalId !== null && checked.proposalId !== owner.proposalId) return invalid(); return checked;
}
export function prepareSupplier(actorId: string, selected: SupplierTarget, selectedPolicy: SupplierPolicy, chosen: SupplierMappingSource[], sent: SupplierInput, shown: SupplierPreview, reason: string) {
  const scope = parse(target, selected), clean = supplierInput(sent), checked = parse(preview, shown), approved = parse(policy, selectedPolicy);
  const remaining = chosen.map(value => JSON.stringify(parse(mappingSource, value))), sourcesMatch = checked.mappingSources.every(value => { const index = remaining.indexOf(JSON.stringify(value)); if (index < 0) return false; remaining.splice(index, 1); return true; });
  if (!sameOwner(scope.owner, clean.owner) || JSON.stringify(clean) !== JSON.stringify(supplierInput(checked.input)) || JSON.stringify(approved) !== JSON.stringify(checked.policy) || !supplierPolicyCurrent(approved, Date.parse(checked.observedAt)) ||
    (['organization', 'shop', 'channel'] as const).some(field => JSON.stringify(scope[field]) !== JSON.stringify(checked.source[field])) || chosen.length !== clean.mappings.length || !sourcesMatch || remaining.length !== 0)
    throw new RequestNotSentError(409, 'supplier_view_stale');
  return freezeCatalogAttempt({ actorId: parse(id, actorId), owner: scope.owner, supplierId: clean.supplierId, body: { input: clean, binding: checked.binding, reason: entered(userText(1000), reason) } });
}
export function prepareSupplierObservation(actorId: string, current: SupplierProposal, activePolicy: SupplierPolicy, observed: SupplierDetails, sourceReference: string, reason: string, attested: boolean, now: number) {
  const saved = parse(proposal, current), approved = parse(policy, activePolicy);
  if (saved.state !== 'pending' || saved.observation !== null || !attested || !approved.allowReferenceOnly || !supplierPolicyCurrent(approved, now) || JSON.stringify(saved.policy) !== JSON.stringify(approved)) throw new RequestNotSentError(409, 'supplier_view_stale');
  return freezeCatalogAttempt({ actorId: parse(id, actorId), proposalId: saved.id, revision: saved.version, body: { observed: entered(details, observed), policyId: approved.id, policyRevision: approved.revision,
    trustedChannelReference: approved.trustedChannelReference, sourceReference: entered(userText(256), sourceReference), reason: entered(userText(1000), reason), attested: true as const } });
}
export function prepareSupplierDecision(actorId: string, current: SupplierProposal, shown: SupplierDecisionPreview, reason: string) {
  const saved = parse(proposal, current), checked = parse(decisionPreview, shown);
  if (saved.state !== 'pending' || JSON.stringify(saved) !== JSON.stringify(checked.proposal) || checked.decision === 'approve' && (!saved.observation || !supplierPolicyCurrent(saved.policy, Date.parse(checked.observedAt)) ||
    JSON.stringify(saved.observation.observed) !== JSON.stringify(saved.input.details) || Date.parse(checked.observedAt) >= Date.parse(saved.observation.checkedAt) + Number(saved.policy.maxObservationAgeSeconds) * 1000)) throw new RequestNotSentError(409, 'supplier_view_stale');
  return freezeCatalogAttempt({ actorId: parse(id, actorId), proposalId: saved.id, revision: saved.version, decision: checked.decision, body: { decision: checked.decision, observationId: checked.observationId, binding: checked.binding, reason: entered(userText(1000), reason) } });
}
export function createSupplierGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  const ensure = (isCurrent: () => boolean) => { if (!isCurrent()) throw new RequestNotSentError(409, 'supplier_view_stale'); };
  const after = (isCurrent: () => boolean) => { if (!isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown'); };
  return {
    async targets(actorId: string, permission: SupplierAccess, signal?: AbortSignal) { return parse(targets, data(await client.POST('/api/v1/procurement/suppliers/targets/query', { body: read(permission), params: { header: headers(actorId) }, signal }))); },
    async locations(actorId: string, permission: SupplierAccess, scope: SupplierOwner, signal?: AbortSignal) { const checked = parse(locations, data(await client.POST('/api/v1/procurement/suppliers/locations/query', { body: { ...read(permission), owner: parse(owner, scope) }, params: { header: headers(actorId) }, signal }))); return checked.items.every(item => inOwner(item.location, scope)) ? checked : invalid(); },
    async policies(actorId: string, permission: SupplierAccess, scope: SupplierOwner, signal?: AbortSignal) { const checked = parse(policies, data(await client.POST('/api/v1/procurement/suppliers/policies/query', { body: { ...read(permission), owner: parse(owner, scope) }, params: { header: headers(actorId) }, signal }))); return checked.items.every(item => sameOwner(item.owner, scope)) ? checked : invalid(); },
    async packages(actorId: string, permission: SupplierAccess, where: SupplierLocation['location'], search: string, offset = 0, signal?: AbortSignal) {
      if (!Number.isSafeInteger(offset) || offset < 0 || offset > 10000 || search.length > 128) throw new RequestNotSentError(400, 'invalid_supplier_change');
      const checked = parse(packagePage, data(await client.POST('/api/v1/procurement/suppliers/packages/query', { body: { ...read(permission), location: parse(location, where), search, offset, limit: 25 }, params: { header: headers(actorId) }, signal }))); return checked.offset === offset ? checked : invalid();
    },
    async list(actorId: string, permission: SupplierAccess, scope: SupplierOwner, beforeId: string | null = null, signal?: AbortSignal) { const checked = parse(history, data(await client.POST('/api/v1/procurement/suppliers/proposals/query', { body: { ...read(permission), owner: parse(owner, scope), beforeId }, params: { header: headers(actorId) }, signal })));
      return sameOwner(checked.owner, scope) && (beforeId === null || checked.items.every(item => BigInt(item.id) < BigInt(parse(id, beforeId)))) ? checked : invalid(); },
    async proposal(actorId: string, permission: SupplierAccess, proposalId: string, signal?: AbortSignal) { return parseSupplierProposal(data(await client.POST('/api/v1/procurement/suppliers/proposals/{id}/query', { body: read(permission), params: { path: { id: parse(id, proposalId) }, header: headers(actorId) }, signal })), proposalId); },
    async current(actorId: string, permission: SupplierAccess, supplierId: string, signal?: AbortSignal) { const checked = parse(current, data(await client.POST('/api/v1/procurement/suppliers/current/query', { body: { ...read(permission), supplierId: parse(id, supplierId) }, params: { header: headers(actorId) }, signal }))); return checked.supplierId === supplierId ? checked : invalid(); },
    async preview(actorId: string, value: SupplierInput, signal?: AbortSignal) { return parse(preview, data(await client.POST('/api/v1/procurement/suppliers/preview/query', { body: { purpose: 'supplier-review', input: supplierInput(value) }, params: { header: headers(actorId) }, signal }))); },
    async previewDecision(actorId: string, current: SupplierProposal, selected: SupplierDecision, signal?: AbortSignal) { const checked = parse(decisionPreview, data(await client.POST('/api/v1/procurement/suppliers/proposals/{id}/decision-preview/query', { body: { purpose: 'supplier-review', decision: selected, observationId: selected === 'approve' ? current.observation?.id ?? null : null }, params: { path: { id: parse(id, current.id) }, header: headers(actorId) }, signal })));
      return checked.proposal.id === current.id && checked.decision === selected ? checked : invalid(); },
    async propose(command: ReturnType<typeof prepareSupplier>, key: string, isCurrent: () => boolean) { const op = 'procurement.supplier.propose', expected = supplierKeyOwner(key, op); if (!expected.owner || !sameOwner(expected.owner, command.owner) || expected.supplierId !== command.supplierId) return invalid(); ensure(isCurrent);
      const saved = parseSupplierResult(data(await client.POST('/api/v1/procurement/suppliers/proposals', { body: command.body, params: { header: { ...headers(command.actorId), 'Idempotency-Key': key } } })), key, op); after(isCurrent); return saved; },
    async observe(command: ReturnType<typeof prepareSupplierObservation>, key: string, isCurrent: () => boolean) { const op = 'procurement.supplier.observe'; if (supplierKeyOwner(key, op).proposalId !== command.proposalId) return invalid(); ensure(isCurrent);
      const saved = parseSupplierResult(data(await client.POST('/api/v1/procurement/suppliers/proposals/{id}/observations', { body: command.body, params: { path: { id: command.proposalId }, header: { ...headers(command.actorId), 'Idempotency-Key': key, 'If-Match': '"1"' } } })), key, op); after(isCurrent); return saved; },
    async decide(command: ReturnType<typeof prepareSupplierDecision>, key: string, isCurrent: () => boolean) { const op = 'procurement.supplier.' + command.decision; if (supplierKeyOwner(key, op).proposalId !== command.proposalId) return invalid(); ensure(isCurrent);
      const saved = parseSupplierResult(data(await client.POST('/api/v1/procurement/suppliers/proposals/{id}/decisions', { body: command.body, params: { path: { id: command.proposalId }, header: { ...headers(command.actorId), 'Idempotency-Key': key, 'If-Match': '"1"' } } })), key, op); after(isCurrent); return saved; },
    async recover(actorId: string, requestedOperation: string, key: string) { const expected = supplierKeyOwner(key, requestedOperation), checked = parse(recovery, data(await client.POST('/api/v1/procurement/suppliers/operations/query', { body: { purpose: 'operation-recovery', operation: requestedOperation, key }, params: { header: headers(actorId) } })));
      if (checked.result === null) return null; const saved = parseSupplierResult(checked.result, key, requestedOperation);
      if (expected.owner) { const actual = parseSupplierProposal(data(await client.POST('/api/v1/procurement/suppliers/proposals/{id}/query', { body: read('propose'), params: { path: { id: saved.proposalId }, header: headers(actorId) } })), saved.proposalId); if (!sameOwner(actual.input.owner, expected.owner) || actual.supplierId !== saved.supplierId) return invalid(); }
      return { state: 'committed' as const, result: saved };
    },
  };
}
