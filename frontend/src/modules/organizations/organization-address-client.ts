import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { paymentAllocationSchemas } from '../billing/payment-allocation-client';

const { id, hash } = paymentAllocationSchemas;
const zeroVersion = z.string().regex(/^(0|[1-9][0-9]{0,18})$(?![\s\S])/).refine(value => BigInt(value) < BigInt('9223372036854775807'));
const expectedAddressVersion = zeroVersion.refine(value => BigInt(value) < BigInt('9223372036854775806'));
const version = zeroVersion.refine(value => value !== '0');
const text = (max: number) => z.string().min(1).max(max).refine(value => value === value.normalize('NFKC').trim() && !/[\p{Cc}\p{Cf}\p{Co}\p{Cs}]/u.test(value));
const purpose = z.enum(['legal_billing', 'delivery', 'dispatch']);
const target = z.object({ kind: z.enum(['buyingOrganization', 'fulfillmentOrganization', 'branch', 'division', 'warehouse']), id }).strict();
const postal = z.object({ line1: text(200), line2: text(200).nullable(), city: text(80), region: text(80), postalCode: text(20), countryCode: z.string().regex(/^[A-Z]{2}$(?![\s\S])/) }).strict();
const compatible = (value: { target: z.infer<typeof target>; purpose: z.infer<typeof purpose> }) => value.purpose === 'legal_billing'
  ? ['buyingOrganization', 'fulfillmentOrganization'].includes(value.target.kind) : value.purpose === 'delivery'
    ? ['branch', 'division'].includes(value.target.kind) : value.target.kind === 'warehouse';
const sameTarget = (left: z.infer<typeof target>, right: z.infer<typeof target>) => left.kind === right.kind && left.id === right.id;
const timestamp = z.string().datetime({ offset: true });
const choice = z.object({ target, purpose, code: text(64), name: text(160), scopeVersion: version, profileVersion: version,
  addressVersion: zeroVersion, addressId: id.nullable(), versionId: id.nullable(), currentAddress: postal.nullable() }).strict()
  .refine(compatible).refine(value => value.addressVersion === '0'
    ? value.addressId === null && value.versionId === null && value.currentAddress === null
    : value.addressId !== null && value.versionId !== null);
const targets = z.object({ items: z.array(choice).max(100), hasMore: z.literal(false) }).strict()
  .refine(value => new Set(value.items.map(item => item.target.kind + ':' + item.target.id)).size === value.items.length);
const input = z.object({ target, purpose, expectedScopeVersion: version, expectedProfileVersion: version, expectedAddressVersion, address: postal }).strict().refine(compatible);
const preview = z.object({ binding: hash, view: z.object({ target, purpose, code: text(64), name: text(160), scopeVersion: version,
  profileVersion: version, addressVersion: zeroVersion, address: postal, sourceHash: hash }).strict().refine(compatible) }).strict();
const currentAddress = z.object({ addressId: id.nullable(), target, purpose, version: zeroVersion, versionId: id.nullable(), code: text(64), name: text(160),
  address: postal.nullable(), approvedAt: timestamp.nullable(), current: z.boolean() }).strict().refine(compatible).refine(value => value.current
    ? value.version !== '0' && value.addressId !== null && value.versionId !== null && value.address !== null && value.approvedAt !== null
    : value.version === '0' ? value.addressId === null && value.versionId === null && value.address === null && value.approvedAt === null
      : value.addressId !== null && value.versionId !== null && value.address !== null && value.approvedAt !== null);
const proposal = z.object({ proposalId: id, addressId: id, revision: z.literal('1'), target, purpose, code: text(64), name: text(160), address: postal,
  baseVersion: expectedAddressVersion, proposedAt: timestamp, state: z.enum(['pending', 'approved', 'rejected']), decision: z.enum(['approve', 'reject']).nullable(),
  addressVersion: version.nullable(), versionId: id.nullable(), bindingCurrent: z.boolean() }).strict().refine(compatible).refine(value => value.state === 'approved'
    ? value.decision === 'approve' && value.addressVersion !== null && BigInt(value.addressVersion) === BigInt(value.baseVersion) + BigInt(1) && value.versionId !== null
    : value.decision === (value.state === 'pending' ? null : 'reject') && value.addressVersion === null && value.versionId === null);
const proposals = z.object({ items: z.array(proposal).max(100), nextProposalId: id.nullable() }).strict().refine(value =>
  value.items.every((item, index) => index === 0 || BigInt(item.proposalId) < BigInt(value.items[index - 1].proposalId)) &&
  (value.nextProposalId === null || value.items.length > 0 && value.nextProposalId === value.items[value.items.length - 1].proposalId));
const decision = z.enum(['approve', 'reject']);
const decisionPreview = z.object({ binding: hash, view: z.object({ proposalId: id, revision: z.literal('1'), addressId: id, target, purpose, decision,
  address: postal, code: text(64), name: text(160), baseVersion: expectedAddressVersion, bindingCurrent: z.boolean() }).strict().refine(compatible)
  .refine(value => value.decision === 'reject' || value.bindingCurrent) }).strict();
const operation = z.enum(['organization.address.propose', 'organization.address.approve', 'organization.address.reject']);
const result = z.object({ addressId: id, proposalId: id, recordId: id, operation, addressVersion: version.nullable(), versionId: id.nullable(), recordedAt: timestamp }).strict()
  .refine(value => value.operation === 'organization.address.approve' ? value.addressVersion !== null && value.versionId !== null : value.addressVersion === null && value.versionId === null);
const recovery = z.object({ status: z.enum(['not_confirmed', 'committed']), result: result.nullable() }).strict()
  .refine(value => (value.status === 'committed') === (value.result !== null));
export type PostalAddress = z.infer<typeof postal>;
export type OrganizationAddressPurpose = z.infer<typeof purpose>;
export type OrganizationAddressTarget = z.infer<typeof target>;
export type OrganizationAddressChoice = z.infer<typeof choice>;
export type OrganizationAddressInput = z.infer<typeof input>;
export type OrganizationAddressPreview = z.infer<typeof preview>;
export type OrganizationAddressCurrent = z.infer<typeof currentAddress>;
export type OrganizationAddressProposal = z.infer<typeof proposal>;
export type OrganizationAddressDecision = z.infer<typeof decision>;
export type OrganizationAddressDecisionPreview = z.infer<typeof decisionPreview>;
export type OrganizationAddressResult = z.infer<typeof result>;
export const addressPurposeLabels = { legal_billing: 'Penagihan legal', delivery: 'Pengiriman cabang / divisi', dispatch: 'Keberangkatan gudang' } as const;
const invalid = (): never => { throw new AccessFailure(502, 'organization_address_response_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const parsed = schema.safeParse(value); return parsed.success ? parsed.data : invalid(); }
function data<T>(response: { data?: T; error?: unknown; response: Response }): T {
  if (!response.response.ok || response.data === undefined) throw new AccessFailure(response.response.status, (response.error as { code?: string })?.code ?? 'request_failed');
  return response.data;
}
const requireCurrent = (current: () => boolean) => { if (!current()) throw new RequestNotSentError(409, 'organization_address_view_stale'); };
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': parse(id, actorId) });
export function organizationAddressInput(value: OrganizationAddressInput) {
  const normalized = { ...value, address: Object.fromEntries(Object.entries(value.address).map(([key, field]) => [key, field === null ? null :
    key === 'countryCode' ? field.normalize('NFKC').trim().toUpperCase() : field.normalize('NFKC').trim()])) };
  const parsed = input.safeParse(normalized); if (!parsed.success) throw new RequestNotSentError(400, 'organization_address_invalid'); return parsed.data;
}
export function parseOrganizationAddressTargets(value: unknown, selectedPurpose: OrganizationAddressPurpose) {
  const checked = parse(targets, value); if (checked.items.some(item => item.purpose !== selectedPurpose)) return invalid(); return checked;
}
export function parseOrganizationAddressCurrent(value: unknown, owner: OrganizationAddressTarget, selectedPurpose: OrganizationAddressPurpose) {
  const checked = parse(currentAddress, value); return sameTarget(checked.target, owner) && checked.purpose === selectedPurpose ? checked : invalid();
}
export function parseOrganizationAddressProposal(value: unknown, proposalId?: string) {
  const checked = parse(proposal, value); return proposalId === undefined || checked.proposalId === proposalId ? checked : invalid();
}
const uuid = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
export function organizationAddressKeyOwner(key: string, requestedOperation: string) {
  parse(operation, requestedOperation);
  const match = new RegExp(requestedOperation === 'organization.address.propose'
    ? `^oa\\.([1-9][0-9]{0,18})\\.(legal_billing|delivery|dispatch)\\.(${uuid})$(?![\\s\\S])`
    : `^oad\\.([1-9][0-9]{0,18})\\.(approve|reject)\\.(${uuid})$(?![\\s\\S])`).exec(key);
  if (!match || match[3] === '00000000-0000-0000-0000-000000000000' || requestedOperation !== 'organization.address.propose' && requestedOperation !== `organization.address.${match[2]}`) return invalid();
  return { id: parse(id, match[1]), action: match[2] };
}
export function parseOrganizationAddressResult(value: unknown, key: string, requestedOperation: string) {
  const owner = organizationAddressKeyOwner(key, requestedOperation), checked = parse(result, value);
  if (checked.operation !== requestedOperation || requestedOperation !== 'organization.address.propose' && checked.proposalId !== owner.id) return invalid(); return checked;
}
export function prepareOrganizationAddress(actorId: string, selected: OrganizationAddressChoice, sent: OrganizationAddressInput, shown: OrganizationAddressPreview, reason: string) {
  const checked = parse(preview, shown), owner = parse(choice, selected), clean = organizationAddressInput(sent);
  if (!sameTarget(clean.target, owner.target) || !sameTarget(checked.view.target, owner.target) || clean.purpose !== owner.purpose || checked.view.purpose !== owner.purpose ||
    checked.view.code !== owner.code || checked.view.name !== owner.name || checked.view.scopeVersion !== clean.expectedScopeVersion || clean.expectedScopeVersion !== owner.scopeVersion ||
    checked.view.profileVersion !== clean.expectedProfileVersion || clean.expectedProfileVersion !== owner.profileVersion || checked.view.addressVersion !== clean.expectedAddressVersion ||
    clean.expectedAddressVersion !== owner.addressVersion || JSON.stringify(checked.view.address) !== JSON.stringify(clean.address)) throw new RequestNotSentError(409, 'organization_address_view_stale');
  return Object.freeze({ actorId: parse(id, actorId), targetId: owner.target.id, purpose: owner.purpose,
    body: Object.freeze({ input: Object.freeze({ ...clean, target: Object.freeze({ ...clean.target }), address: Object.freeze({ ...clean.address }) }), binding: checked.binding, reason: parse(text(1000), reason) }) });
}
export function prepareOrganizationAddressDecision(actorId: string, source: OrganizationAddressProposal, shown: OrganizationAddressDecisionPreview, reason: string) {
  const owner = parse(proposal, source), checked = parse(decisionPreview, shown);
  if (owner.state !== 'pending' || checked.view.proposalId !== owner.proposalId || checked.view.addressId !== owner.addressId || !sameTarget(checked.view.target, owner.target) ||
    checked.view.purpose !== owner.purpose || checked.view.code !== owner.code || checked.view.name !== owner.name || checked.view.baseVersion !== owner.baseVersion ||
    checked.view.revision !== owner.revision || JSON.stringify(checked.view.address) !== JSON.stringify(owner.address)) throw new RequestNotSentError(409, 'organization_address_view_stale');
  return Object.freeze({ actorId: parse(id, actorId), proposalId: owner.proposalId, revision: owner.revision, decision: checked.view.decision,
    body: Object.freeze({ decision: checked.view.decision, binding: checked.binding, reason: parse(text(1000), reason) }) });
}
export function createOrganizationAddressGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async targets(actorId: string, selectedPurpose: OrganizationAddressPurpose) {
      return parseOrganizationAddressTargets(data(await client.POST('/api/v1/organizations/addresses/targets/query', { body: { purpose: parse(purpose, selectedPurpose) }, params: { header: headers(actorId) } })), selectedPurpose);
    },
    async read(actorId: string, owner: OrganizationAddressTarget, selectedPurpose: OrganizationAddressPurpose) {
      return parseOrganizationAddressCurrent(data(await client.POST('/api/v1/organizations/addresses/query', { body: { target: parse(target, owner), purpose: parse(purpose, selectedPurpose) }, params: { header: headers(actorId) } })), owner, selectedPurpose);
    },
    async proposals(actorId: string, owner: OrganizationAddressTarget, selectedPurpose: OrganizationAddressPurpose, beforeId: string | null = null) {
      const checked = parse(proposals, data(await client.POST('/api/v1/organizations/addresses/proposals/query', { body: { target: parse(target, owner), purpose: parse(purpose, selectedPurpose), beforeId: beforeId === null ? null : parse(id, beforeId) }, params: { header: headers(actorId) } })));
      if (checked.items.some(item => !sameTarget(item.target, owner) || item.purpose !== selectedPurpose || beforeId !== null && BigInt(item.proposalId) >= BigInt(beforeId))) return invalid(); return checked;
    },
    async proposal(actorId: string, proposalId: string) {
      return parseOrganizationAddressProposal(data(await client.POST('/api/v1/organizations/addresses/proposals/{id}/query', { body: { purpose: 'organization-address' }, params: { path: { id: parse(id, proposalId) }, header: headers(actorId) } })), proposalId);
    },
    async preview(actorId: string, sent: OrganizationAddressInput) {
      return parse(preview, data(await client.POST('/api/v1/organizations/addresses/preview', { body: organizationAddressInput(sent), params: { header: headers(actorId) } })));
    },
    async propose(command: ReturnType<typeof prepareOrganizationAddress>, key: string, current: () => boolean) {
      requireCurrent(current); const owner = organizationAddressKeyOwner(key, 'organization.address.propose');
      if (owner.id !== command.targetId || owner.action !== command.purpose) throw new RequestNotSentError(400, 'organization_address_invalid');
      return parseOrganizationAddressResult(data(await client.POST('/api/v1/organizations/addresses/proposals', { body: command.body, params: { header: { ...headers(command.actorId), 'Idempotency-Key': key } } })), key, 'organization.address.propose');
    },
    async previewDecision(actorId: string, owner: OrganizationAddressProposal, selectedDecision: OrganizationAddressDecision) {
      return parse(decisionPreview, data(await client.POST('/api/v1/organizations/addresses/proposals/{id}/preview-decision', { body: { decision: parse(decision, selectedDecision) }, params: { path: { id: parse(id, owner.proposalId) }, header: { ...headers(actorId), 'If-Match': '"1"' } } })));
    },
    async decide(command: ReturnType<typeof prepareOrganizationAddressDecision>, key: string, current: () => boolean) {
      requireCurrent(current); const requestedOperation = `organization.address.${command.decision}`, owner = organizationAddressKeyOwner(key, requestedOperation);
      if (owner.id !== command.proposalId) throw new RequestNotSentError(400, 'organization_address_invalid');
      return parseOrganizationAddressResult(data(await client.POST('/api/v1/organizations/addresses/proposals/{id}/decide', { body: command.body, params: { path: { id: command.proposalId }, header: { ...headers(command.actorId), 'Idempotency-Key': key, 'If-Match': `"${command.revision}"` } } })), key, requestedOperation);
    },
    async recover(actorId: string, requestedOperation: string, key: string) {
      const owner = organizationAddressKeyOwner(key, requestedOperation), checked = parse(recovery, data(await client.POST('/api/v1/organizations/addresses/operations/query', { body: { operation: parse(operation, requestedOperation), key, purpose: 'operation-recovery' }, params: { header: headers(actorId) } })));
      if (checked.result === null) return null;
      const saved = parseOrganizationAddressResult(checked.result, key, requestedOperation);
      if (requestedOperation === 'organization.address.propose') {
        const actual = parseOrganizationAddressProposal(data(await client.POST('/api/v1/organizations/addresses/proposals/{id}/query', { body: { purpose: 'organization-address' }, params: { path: { id: saved.proposalId }, header: headers(actorId) } })), saved.proposalId);
        if (actual.target.id !== owner.id || actual.purpose !== owner.action || actual.addressId !== saved.addressId || Date.parse(saved.recordedAt) !== Date.parse(actual.proposedAt)) return invalid();
      }
      return { state: 'committed' as const, result: saved };
    },
  };
}
