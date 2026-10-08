import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { paymentAllocationSchemas } from '../billing/payment-allocation-client';
import { freezeCatalogAttempt } from '../catalog/catalog-editor-state';
import { packageMatchingSchema, packageMatchesFit } from './package-matching';

const { id, hash } = paymentAllocationSchemas;
const version = id;
const text = (max: number) => z.string().min(1).max(max).refine(value => value === value.normalize('NFKC').trim() && !/[\p{Cc}\p{Cf}\p{Co}\p{Cs}]/u.test(value));
const userText = (max: number) => z.string().min(1).max(max).refine(value => !/^\p{Z}|\p{Z}$|\p{Cc}/u.test(value));
const time = z.string().datetime({ offset: true });
const date = z.string().regex(/^(?!0000)[0-9]{4}-[0-9]{2}-[0-9]{2}$(?![\s\S])/).refine(value => { const parsed = new Date(value + 'T00:00:00Z'); return value !== '0001-01-01' && Number.isFinite(parsed.getTime()) && parsed.toISOString().slice(0, 10) === value; });
const location = z.object({ organizationId: id, shopId: id, warehouseId: id, channelId: id }).strict().refine(value => new Set(Object.values(value)).size === 4);
const label = z.object({ id, code: text(64), name: text(160), scopeVersion: version, profileVersion: version }).strict();
const source = z.object({ organization: label, shop: label, warehouse: label, channel: label }).strict();
const sourceMatches = (where: z.infer<typeof location>, labels: z.infer<typeof source>) =>
  where.organizationId === labels.organization.id && where.shopId === labels.shop.id && where.warehouseId === labels.warehouse.id && where.channelId === labels.channel.id;
const target = z.object({ location, ...source.shape }).strict().refine(value => sourceMatches(value.location, value));
export const procurementLocationKey = (value: z.infer<typeof location>) => [value.organizationId, value.shopId, value.warehouseId, value.channelId].join(':');
const sameLocation = (left: z.infer<typeof location>, right: z.infer<typeof location>) => procurementLocationKey(left) === procurementLocationKey(right);
const targets = z.object({ items: z.array(target).max(256) }).strict().refine(value => new Set(value.items.map(item => procurementLocationKey(item.location))).size === value.items.length);
const route = z.enum(['omi_regular', 'ddo', 'smart', 'pasar_kering']);
const kind = z.enum(['stock_need', 'incoming_notice', 'purchase_request']);
const packages = z.number().int().min(1).max(1_000_000);
const selection = z.object({ skuId: id, expectedSkuRevision: version, definitionId: id, definitionVersion: version, packages, specification: userText(1000).nullable() }).strict();
const selections = z.array(selection).min(1).max(100).refine(values => new Set(values.map(value => value.skuId)).size === values.length);
const input = z.object({ location, route, kind, businessDate: date, externalReference: userText(128).nullable(), reason: userText(1000), lines: selections }).strict().refine(value =>
  value.route === 'omi_regular' ? value.kind === 'stock_need' || value.kind === 'incoming_notice' && value.externalReference !== null : value.kind === 'purchase_request');
const packageFields = { skuId: id, skuRevision: version, definitionId: id, definitionVersion: version, skuCode: text(64), familyName: text(160),
  packageLabel: text(160), packageKind: z.enum(['loose', 'sealed', 'other']), baseUnitCode: text(64), baseUnitsPerPackage: z.number().int().min(1).max(2_147_483_647) };
const packageChoice = z.object(packageFields).strict();
const packagePage = z.object({ items: z.array(packageChoice).max(25), offset: z.number().int().min(0).max(10000), hasMore: z.boolean(), matching: packageMatchingSchema }).strict().refine(value =>
  new Set(value.items.map(item => item.skuId)).size === value.items.length && (!value.hasMore || value.items.length > 0) && packageMatchesFit(value.items, value.matching));
const line = z.object({ ...packageFields, packages, specification: userText(1000).nullable() }).strict();
const lines = z.array(line).min(1).max(100);
const facts = { input, source, lines };
const factsMatch = (value: { input: z.infer<typeof input>; source: z.infer<typeof source>; lines: z.infer<typeof lines> }) =>
  sourceMatches(value.input.location, value.source) && value.lines.length === value.input.lines.length && value.lines.every((shown, index) => {
    const sent = value.input.lines[index]; return shown.skuId === sent.skuId && shown.skuRevision === sent.expectedSkuRevision && shown.definitionId === sent.definitionId &&
      shown.definitionVersion === sent.definitionVersion && shown.packages === sent.packages && shown.specification === sent.specification;
  });
const preview = z.object({ ...facts, observedAt: time, binding: hash }).strict().refine(factsMatch);
const decision = z.enum(['approve', 'reject']);
const state = z.enum(['submitted', 'approved', 'rejected']);
const decisionRecord = z.object({ id, decision, reason: userText(1000), decidedAt: time }).strict();
const request = z.object({ id, version: z.literal('1'), ...facts, submittedAt: time, state, decision: decisionRecord.nullable(),
  downstream: z.object({ supplier: z.literal('unresolved'), commercial: z.literal('unpriced'), payment: z.literal('not_recorded'), receipt: z.literal('not_recorded') }).strict() }).strict()
  .refine(factsMatch).refine(value => value.state === 'submitted' ? value.decision === null : value.decision !== null &&
    value.decision.decision === (value.state === 'approved' ? 'approve' : 'reject') && Date.parse(value.decision.decidedAt) >= Date.parse(value.submittedAt));
const history = z.object({ location, items: z.array(request).max(25), nextRequestId: id.nullable() }).strict().refine(value =>
  value.items.every((item, index) => sameLocation(item.input.location, value.location) && (index === 0 || BigInt(item.id) < BigInt(value.items[index - 1].id))) &&
  (value.nextRequestId === null || value.items.length > 0 && value.nextRequestId === value.items.at(-1)!.id));
const decisionPreview = z.object({ request, decision, binding: hash, observedAt: time }).strict().refine(value => value.request.state === 'submitted' && Date.parse(value.observedAt) >= Date.parse(value.request.submittedAt));
const operation = z.enum(['procurement.request.submit', 'procurement.request.approve', 'procurement.request.reject']);
const result = z.object({ requestId: id, requestVersion: z.literal('1'), decisionId: id.nullable(), status: state }).strict().refine(value => (value.status === 'submitted') === (value.decisionId === null));
const recovery = z.object({ status: z.enum(['confirmed', 'not_confirmed']), result: result.nullable() }).strict().refine(value => (value.status === 'confirmed') === (value.result !== null));
const invalid = (): never => { throw new AccessFailure(502, 'procurement_response_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const checked = schema.safeParse(value); return checked.success ? checked.data : invalid(); }
function data<T>(response: { data?: T; error?: unknown; response: Response }): T { if (!response.response.ok || response.data === undefined) throw new AccessFailure(response.response.status, (response.error as { code?: string })?.code ?? 'request_failed'); return response.data; }
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': parse(id, actorId) });
const requireCurrent = (current: () => boolean) => { if (!current()) throw new RequestNotSentError(409, 'procurement_view_stale'); };
export type ProcurementLocation = z.infer<typeof location>;
export type ProcurementTarget = z.infer<typeof target>;
export type ProcurementPackage = z.infer<typeof packageChoice>;
export type ProcurementInput = z.infer<typeof input>;
export type ProcurementPreview = z.infer<typeof preview>;
export type ProcurementRequest = z.infer<typeof request>;
export type ProcurementDecision = z.infer<typeof decision>;
export type ProcurementDecisionPreview = z.infer<typeof decisionPreview>;
export type ProcurementResult = z.infer<typeof result>;
export function procurementQuantity(value: string) { if (!/^[1-9][0-9]{0,6}$(?![\s\S])/.test(value) || !packages.safeParse(Number(value)).success) throw new RequestNotSentError(400, 'procurement_invalid'); return Number(value); }
export function procurementInput(value: ProcurementInput) { const checked = input.safeParse(value); if (!checked.success) throw new RequestNotSentError(400, 'procurement_invalid');
  return { ...checked.data, lines: [...checked.data.lines].sort((a, b) => BigInt(a.skuId) < BigInt(b.skuId) ? -1 : BigInt(a.skuId) > BigInt(b.skuId) ? 1 : 0) }; }
export function parseProcurementTargets(value: unknown) { return parse(targets, value); }
export function parseProcurementRequest(value: unknown, expectedId?: string) { const checked = parse(request, value); return expectedId === undefined || checked.id === expectedId ? checked : invalid(); }
const uuid = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
export function procurementKeyOwner(value: string, requestedOperation: string) {
  parse(operation, requestedOperation);
  if (requestedOperation === 'procurement.request.submit') {
    const found = new RegExp(`^pr\\.([1-9][0-9]{0,18})\\.([1-9][0-9]{0,18})\\.([1-9][0-9]{0,18})\\.([1-9][0-9]{0,18})\\.(${uuid})$(?![\\s\\S])`).exec(value);
    if (!found || found[5] === '00000000-0000-0000-0000-000000000000') return invalid();
    return { location: parse(location, { organizationId: found[1], shopId: found[2], warehouseId: found[3], channelId: found[4] }), requestId: null };
  }
  const found = new RegExp(`^prd\\.(approve|reject)\\.([1-9][0-9]{0,18})\\.(${uuid})$(?![\\s\\S])`).exec(value);
  if (!found || requestedOperation !== 'procurement.request.' + found[1] || found[3] === '00000000-0000-0000-0000-000000000000') return invalid();
  return { location: null, requestId: parse(id, found[2]) };
}
export function parseProcurementResult(value: unknown, requestKey: string, requestedOperation: string) {
  const owner = procurementKeyOwner(requestKey, requestedOperation), checked = parse(result, value);
  if (checked.status !== (requestedOperation === 'procurement.request.submit' ? 'submitted' : requestedOperation === 'procurement.request.approve' ? 'approved' : 'rejected') || owner.requestId !== null && checked.requestId !== owner.requestId) return invalid(); return checked;
}
export function prepareProcurement(actorId: string, selected: ProcurementTarget, chosen: ProcurementPackage[], sent: ProcurementInput, shown: ProcurementPreview) {
  const owner = parse(target, selected), clean = procurementInput(sent), checked = parse(preview, shown);
  if (!sameLocation(clean.location, owner.location) || JSON.stringify(clean) !== JSON.stringify(procurementInput(checked.input)) ||
    (['organization', 'shop', 'warehouse', 'channel'] as const).some(field => JSON.stringify(owner[field]) !== JSON.stringify(checked.source[field])) || chosen.length !== clean.lines.length ||
    new Set(chosen.map(item => item.skuId)).size !== chosen.length || checked.lines.some(shown => {
      const selected = chosen.find(item => item.skuId === shown.skuId); if (!selected) return true;
      const facts = parse(packageChoice, selected); return (Object.keys(packageFields) as (keyof ProcurementPackage)[]).some(field => facts[field] !== shown[field]);
    })) throw new RequestNotSentError(409, 'procurement_view_stale');
  return freezeCatalogAttempt({ actorId: parse(id, actorId), location: owner.location, body: { input: clean, binding: checked.binding } });
}
export function prepareProcurementDecision(actorId: string, original: ProcurementRequest, shown: ProcurementDecisionPreview, reason: string) {
  const owner = parse(request, original), checked = parse(decisionPreview, shown), cleanReason = userText(1000).safeParse(reason);
  if (!cleanReason.success) throw new RequestNotSentError(400, 'procurement_invalid');
  if (owner.state !== 'submitted' || JSON.stringify(owner) !== JSON.stringify(checked.request)) throw new RequestNotSentError(409, 'procurement_view_stale');
  return freezeCatalogAttempt({ actorId: parse(id, actorId), requestId: owner.id, revision: owner.version, decision: checked.decision, body: { decision: checked.decision, binding: checked.binding, reason: cleanReason.data } });
}
export function createProcurementGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async targets(actorId: string, signal?: AbortSignal) { return parseProcurementTargets(data(await client.POST('/api/v1/procurement/requests/targets/query', { body: { purpose: 'procurement-request-review' }, params: { header: headers(actorId) }, signal }))); },
    async packages(actorId: string, where: ProcurementLocation, search: string, offset = 0, signal?: AbortSignal) {
      if (!Number.isSafeInteger(offset) || offset < 0 || offset > 10000 || search.length > 128) throw new RequestNotSentError(400, 'procurement_invalid');
      const checked = parse(packagePage, data(await client.POST('/api/v1/procurement/requests/packages/query', { body: { purpose: 'procurement-request-review', location: parse(location, where), search, offset, limit: 25 }, params: { header: headers(actorId) }, signal })));
      return checked.offset === offset ? checked : invalid();
    },
    async list(actorId: string, where: ProcurementLocation, beforeId: string | null = null, signal?: AbortSignal) {
      const checked = parse(history, data(await client.POST('/api/v1/procurement/requests/query', { body: { purpose: 'procurement-request-review', location: parse(location, where), beforeId }, params: { header: headers(actorId) }, signal })));
      if (!sameLocation(checked.location, where) || beforeId !== null && checked.items.some(item => BigInt(item.id) >= BigInt(parse(id, beforeId)))) return invalid(); return checked;
    },
    async request(actorId: string, requestId: string, signal?: AbortSignal) { return parseProcurementRequest(data(await client.POST('/api/v1/procurement/requests/{id}/query', { body: { purpose: 'procurement-request-review' }, params: { path: { id: parse(id, requestId) }, header: headers(actorId) }, signal })), requestId); },
    async preview(actorId: string, value: ProcurementInput, signal?: AbortSignal) { return parse(preview, data(await client.POST('/api/v1/procurement/requests/preview/query', { body: { purpose: 'procurement-request-review', input: procurementInput(value) }, params: { header: headers(actorId) }, signal }))); },
    async previewDecision(actorId: string, original: ProcurementRequest, selected: ProcurementDecision, signal?: AbortSignal) {
      const checked = parse(decisionPreview, data(await client.POST('/api/v1/procurement/requests/{id}/decision-preview/query', { body: { purpose: 'procurement-request-review', decision: selected }, params: { path: { id: parse(id, original.id) }, header: headers(actorId) }, signal })));
      return checked.request.id === original.id && checked.decision === selected ? checked : invalid();
    },
    async submit(command: ReturnType<typeof prepareProcurement>, requestKey: string, isCurrent: () => boolean) {
      const op = 'procurement.request.submit', owner = procurementKeyOwner(requestKey, op); if (!owner.location || !sameLocation(owner.location, command.location)) return invalid(); requireCurrent(isCurrent);
      const saved = parseProcurementResult(data(await client.POST('/api/v1/procurement/requests/submissions', { body: command.body, params: { header: { ...headers(command.actorId), 'Idempotency-Key': requestKey } } })), requestKey, op);
      if (!isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown'); return saved;
    },
    async decide(command: ReturnType<typeof prepareProcurementDecision>, requestKey: string, isCurrent: () => boolean) {
      const op = 'procurement.request.' + command.decision, owner = procurementKeyOwner(requestKey, op); if (owner.requestId !== command.requestId) return invalid(); requireCurrent(isCurrent);
      const saved = parseProcurementResult(data(await client.POST('/api/v1/procurement/requests/{id}/decisions', { body: command.body,
        params: { path: { id: command.requestId }, header: { ...headers(command.actorId), 'If-Match': '"' + command.revision + '"', 'Idempotency-Key': requestKey } } })), requestKey, op);
      if (!isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown'); return saved;
    },
    async recover(actorId: string, requestedOperation: string, requestKey: string) {
      const owner = procurementKeyOwner(requestKey, requestedOperation), checked = parse(recovery, data(await client.POST('/api/v1/procurement/requests/operations/query', { body: { purpose: 'operation-recovery', operation: requestedOperation, key: requestKey }, params: { header: headers(actorId) } })));
      if (checked.result === null) return null; const saved = parseProcurementResult(checked.result, requestKey, requestedOperation);
      if (owner.location) { const actual = parseProcurementRequest(data(await client.POST('/api/v1/procurement/requests/{id}/query', { body: { purpose: 'procurement-request-review' }, params: { path: { id: saved.requestId }, header: headers(actorId) } })), saved.requestId); if (!sameLocation(actual.input.location, owner.location)) return invalid(); }
      return { state: 'committed' as const, result: saved };
    },
  };
}
