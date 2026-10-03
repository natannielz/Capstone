import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { freezeCatalogAttempt } from '../catalog/catalog-editor-state';
import { parseStockWarehouses, type StockWarehouse } from './stock-count-workflow';

const id = z.string().regex(/^[1-9][0-9]{0,18}$(?![\s\S])/).refine(value => BigInt(value) <= BigInt('9223372036854775807'));
const quantity = z.union([z.literal('0'), id]);
const hash = z.string().regex(/^[a-f0-9]{64}$(?![\s\S])/);
const text = (maximum: number) => z.string().min(1).max(maximum).refine(value => !/^\p{Z}|\p{Z}$|\p{Cc}/u.test(value) && value.isWellFormed());
const time = z.string().regex(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,3})?(?:Z|\+00:00)$(?![\s\S])/).refine(value => Number.isFinite(Date.parse(value)) && new Date(value).toISOString().slice(0, 19) === value.slice(0, 19) && !value.startsWith('0001-01-01T00:00:00'));
const entry = z.object({ balanceId: id, packages: quantity }).strict();
const observation = z.object({ observedAt: time, sourceReference: text(160), entries: z.array(entry).max(1000) }).strict().refine(value => value.entries.every((item, at) => at === 0 || BigInt(item.balanceId) > BigInt(value.entries[at - 1].balanceId)));
const input = z.object({ poolId: id, poolRevision: id, poolFence: quantity, policyId: id, policyRevision: id, configurationHash: hash, observation }).strict();
const location = z.object({ kind: z.literal('fulfillmentWarehouse'), organizationId: id, branchId: z.null(), divisionId: z.null(), shopId: id, warehouseId: id, channelId: id }).strict();
const day = z.string().regex(/^\d{4}-\d{2}-\d{2}$(?![\s\S])/).refine(value => Number.isFinite(Date.parse(value)) && new Date(value).toISOString().slice(0, 10) === value);
const balance = z.object({ balanceId: id, receiptId: id, lotId: id, location, skuId: id, skuCode: text(64), definitionId: id, definitionVersion: id,
  packageLabel: text(160), packageKind: z.enum(['loose', 'sealed', 'other']), baseUnitCode: text(64), baseUnitsPerPackage: z.number().int().min(1).max(2147483647),
  lotReference: text(128), expiresOn: day.nullable(), onHand: quantity, reserved: quantity, held: quantity, expired: quantity, version: id,
}).strict().refine(value => BigInt(value.reserved) + BigInt(value.held) + BigInt(value.expired) <= BigInt(value.onHand));
const baseline = z.object({ poolId: id, poolRevision: id, poolFence: quantity, poolLabel: text(160), physicalKey: text(160), ownerLocation: location,
  policyId: id, policyRevision: id, reconciliationProcedureReference: text(160), configurationHash: hash, baselineHash: hash,
  maximumAgeMilliseconds: id, latestSourceAt: time, balances: z.array(balance).max(1000), capturedAt: time, binding: hash,
}).strict().refine(value => Date.parse(value.latestSourceAt) <= Date.parse(value.capturedAt) &&
  value.balances.every((item, at) => item.location.organizationId === value.ownerLocation.organizationId && item.location.shopId === value.ownerLocation.shopId && item.location.warehouseId === value.ownerLocation.warehouseId && (at === 0 || BigInt(item.balanceId) > BigInt(value.balances[at - 1].balanceId))) &&
  new Set(value.balances.map(item => item.receiptId)).size === value.balances.length && new Set(value.balances.map(item => item.lotId)).size === value.balances.length);
const difference = z.object({ balanceId: id, baselinePackages: quantity, observedPackages: quantity }).strict().refine(value => value.baselinePackages !== value.observedPackages);
const result = z.object({ proposalId: id, decisionId: id.nullable(), poolId: id, revision: z.literal('1'), status: z.enum(['proposed', 'reconciled', 'rejected']), recordedAt: time }).strict().refine(value => (value.status === 'proposed') === (value.decisionId === null));
const purpose = 'stock-source-reconciliation-review';
const invalid = (): never => { throw new AccessFailure(502, 'stock_source_reconciliation_response_invalid'); };
function parse<T>(schema: z.ZodType<T, z.ZodTypeDef, unknown>, value: unknown): T { try { const checked = schema.safeParse(value); return checked.success ? checked.data : invalid(); } catch { return invalid(); } }
function entered<T>(schema: z.ZodType<T, z.ZodTypeDef, unknown>, value: unknown): T { try { const checked = schema.safeParse(value); if (checked.success) return checked.data; } catch { /* Exact malformed input is rejected before dispatch. */ } throw new RequestNotSentError(400, 'invalid_stock_source_reconciliation'); }
function data<T>(value: { data?: T; error?: unknown; response: Response }): T { if (!value.response.ok || value.data === undefined) throw new AccessFailure(value.response.status, (value.error as { code?: string })?.code ?? 'request_failed'); return value.data; }
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': parse(id, actorId) });
const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);
const differences = z.array(difference).max(1000);
const proposal = z.object({ id, version: z.literal('1'), poolId: id, baseline, observation, differences, recordedBy: id, recordedAt: time, reason: text(1000), status: z.literal('proposed') }).strict().refine(value =>
  value.poolId === value.baseline.poolId && Date.parse(value.recordedAt) >= Date.parse(value.observation.observedAt) && same(reconciliationDifferences(value.baseline, value.observation), value.differences));
const decision = z.object({ id, decision: z.enum(['confirm', 'reject']), observation: observation.nullable(), recordedBy: id, recordedAt: time, reason: text(1000) }).strict().refine(value => (value.decision === 'confirm') === (value.observation !== null));
const read = z.object({ proposal, decision: decision.nullable() }).strict().refine(value => !value.decision || value.decision.recordedBy !== value.proposal.recordedBy && Date.parse(value.decision.recordedAt) >= Date.parse(value.proposal.recordedAt) && (value.decision.decision !== 'confirm' || value.proposal.differences.length === 0 && value.decision.observation !== null && same(value.decision.observation.entries, value.proposal.observation.entries) && reconciliationObservationCurrent(value.proposal.baseline, value.proposal.observation, Date.parse(value.decision.recordedAt)) && reconciliationObservationCurrent(value.proposal.baseline, value.decision.observation, Date.parse(value.decision.recordedAt))));
const decisionPreview = z.object({ proposal, decision: z.enum(['confirm', 'reject']), baseline: baseline.nullable(), checkerDifferences: differences, canConfirm: z.boolean(), binding: hash, observedAt: time }).strict().refine(value => value.decision === 'confirm' ? value.baseline !== null && value.baseline.poolId === value.proposal.poolId : value.baseline === null && value.checkerDifferences.length === 0 && !value.canConfirm);
const history = z.object({ items: z.array(read).max(25), total: quantity }).strict().refine(value => value.items.every((item, at) => at === 0 || BigInt(item.proposal.id) > BigInt(value.items[at - 1].proposal.id)) && BigInt(value.items.length) <= BigInt(value.total));
const target = z.object({ poolId: id, label: text(160), physicalKey: text(160), ownerLocation: location, organizationName: text(160), shopName: text(160), warehouseName: text(160), channelNames: z.array(text(160)).min(1).max(256) }).strict();
const targets = z.object({ items: z.array(target).max(100), total: quantity }).strict().refine(value => new Set(value.items.map(item => item.poolId)).size === value.items.length && BigInt(value.items.length) <= BigInt(value.total));
const recovery = z.object({ state: z.enum(['committed', 'not_confirmed']), result: result.nullable() }).strict().refine(value => (value.state === 'committed') === (value.result !== null));

export type SourceReconciliationObservation = z.infer<typeof observation>;
export type SourceReconciliationInput = z.infer<typeof input>;
export type SourceReconciliationResult = z.infer<typeof result>;
export type SourceReconciliationBaseline = z.infer<typeof baseline>;
export type SourceReconciliationBalance = z.infer<typeof balance>;
export type SourceReconciliationDifference = z.infer<typeof difference>;
export type SourceReconciliationRead = z.infer<typeof read>;
export type SourceReconciliationDecisionPreview = z.infer<typeof decisionPreview>;
export type SourceReconciliationTarget = z.infer<typeof target>;
export const sourceReconciliationObservation = (value: unknown) => entered(observation, value);
export const sourceReconciliationInput = (value: unknown) => entered(input, value);
export const parseSourceReconciliationBaseline = (value: unknown) => parse(baseline, value);
export const parseSourceReconciliationRead = (value: unknown) => parse(read, value);

export function reconciliationDifferences(source: SourceReconciliationBaseline, observed: SourceReconciliationObservation): SourceReconciliationDifference[] {
  const actual = parse(baseline, source), checked = entered(observation, observed);
  if (actual.balances.length !== checked.entries.length || actual.balances.some((row, at) => row.balanceId !== checked.entries[at].balanceId)) throw new RequestNotSentError(409, 'stock_source_reconciliation_changed');
  return actual.balances.flatMap((row, at) => row.onHand === checked.entries[at].packages ? [] : [{ balanceId: row.balanceId, baselinePackages: row.onHand, observedPackages: checked.entries[at].packages }]);
}
export function reconciliationObservationCurrent(source: SourceReconciliationBaseline, observed: SourceReconciliationObservation, now: number) {
  const at = Date.parse(observed.observedAt);
  return Number.isFinite(now) && at >= Date.parse(source.latestSourceAt) && at <= now && BigInt(Math.floor(now - at)) < BigInt(source.maximumAgeMilliseconds);
}
export function prepareSourceReconciliation(actorId: string, source: SourceReconciliationBaseline, observed: SourceReconciliationObservation, reason: string, now: number) {
  const checked = parse(baseline, source), actual = entered(observation, observed);
  reconciliationDifferences(checked, actual);
  if (!reconciliationObservationCurrent(checked, actual, now)) throw new RequestNotSentError(409, 'stock_source_reconciliation_changed');
  const sent = entered(input, { poolId: checked.poolId, poolRevision: checked.poolRevision, poolFence: checked.poolFence, policyId: checked.policyId, policyRevision: checked.policyRevision, configurationHash: checked.configurationHash, observation: actual });
  return freezeCatalogAttempt({ actorId: parse(id, actorId), poolId: checked.poolId, body: { input: sent, binding: checked.binding, reason: entered(text(1000), reason) } });
}
export function parseReconciliationDecisionPreview(value: unknown, original: SourceReconciliationRead, action: 'confirm' | 'reject', observed: SourceReconciliationObservation | null) {
  const owner = parse(read, original), checked = parse(decisionPreview, value);
  if (owner.decision !== null || !same(checked.proposal, owner.proposal) || checked.decision !== action || (action === 'confirm') !== (observed !== null)) return invalid();
  if (action === 'confirm' && observed && checked.baseline) {
    if (!same(reconciliationDifferences(checked.baseline, observed), checked.checkerDifferences)) return invalid();
    const coherent = checked.baseline.baselineHash === owner.proposal.baseline.baselineHash && checked.baseline.configurationHash === owner.proposal.baseline.configurationHash && checked.baseline.policyId === owner.proposal.baseline.policyId && checked.baseline.policyRevision === owner.proposal.baseline.policyRevision && BigInt(checked.baseline.poolFence) === BigInt(owner.proposal.baseline.poolFence) + BigInt(1);
    const matched = coherent && checked.checkerDifferences.length === 0 && owner.proposal.differences.length === 0 && same(observed.entries, owner.proposal.observation.entries) && reconciliationObservationCurrent(checked.baseline, owner.proposal.observation, Date.parse(checked.observedAt)) && reconciliationObservationCurrent(checked.baseline, observed, Date.parse(checked.observedAt));
    if (checked.canConfirm !== matched) return invalid();
  }
  return checked;
}
export function prepareSourceReconciliationDecision(actorId: string, original: SourceReconciliationRead, shown: SourceReconciliationDecisionPreview, observed: SourceReconciliationObservation | null, reason: string, now: number) {
  const checked = parseReconciliationDecisionPreview(shown, original, shown.decision, observed);
  if (actorId === original.proposal.recordedBy || checked.decision === 'confirm' && (!checked.canConfirm || !checked.baseline || !observed || !reconciliationObservationCurrent(checked.baseline, observed, now) || !reconciliationObservationCurrent(checked.baseline, original.proposal.observation, now))) throw new RequestNotSentError(409, 'stock_source_reconciliation_changed');
  return freezeCatalogAttempt({ actorId: parse(id, actorId), proposalId: original.proposal.id, decision: checked.decision, body: { decision: checked.decision, observation: observed === null ? null : entered(observation, observed), binding: checked.binding, reason: entered(text(1000), reason) } });
}

/** An explicit Jakarta wall time is converted independently of the browser's local timezone. */
export function reconciliationObservationTime(value: string): string {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2})?$(?![\s\S])/.test(value)) throw new RequestNotSentError(400, 'invalid_stock_source_reconciliation');
  const stamp = value.length === 16 ? value + ':00' : value;
  const utc = new Date(stamp + '+07:00');
  if (!Number.isFinite(utc.getTime()) || new Date(utc.getTime() + 7 * 60 * 60 * 1000).toISOString().slice(0, 19) !== stamp) throw new RequestNotSentError(400, 'invalid_stock_source_reconciliation');
  return entered(time, utc.toISOString());
}

export function reconciliationKeyOwner(key: string, operation: string) {
  const parts = /^recon\.(propose|confirm|reject)\.([1-9][0-9]{0,18})\.([a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12})$(?![\s\S])/.exec(key);
  if (!parts || operation !== 'stock.source.reconciliation.' + (parts[1] === 'propose' ? 'propose' : 'decide')) return invalid();
  return { action: parts[1], id: parse(id, parts[2]) };
}
export function parseSourceReconciliationResult(value: unknown, key: string, operation: string): SourceReconciliationResult {
  const owner = reconciliationKeyOwner(key, operation), checked = parse(result, value);
  if (owner.action === 'propose' ? checked.poolId !== owner.id || checked.status !== 'proposed' : checked.proposalId !== owner.id || checked.status !== (owner.action === 'confirm' ? 'reconciled' : 'rejected')) return invalid();
  return checked;
}

export function createSourceReconciliationGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  const before = (current: () => boolean) => { if (!current()) throw new RequestNotSentError(409, 'stock_source_reconciliation_view_stale'); };
  const after = (current: () => boolean) => { if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); };
  return {
    async warehouses(actorId: string, signal?: AbortSignal) { return parseStockWarehouses(data(await client.POST('/api/v1/inventory/sources/warehouses/query', { body: { purpose }, params: { header: headers(actorId) }, signal }))); },
    async targets(actorId: string, warehouse: StockWarehouse, offset = 0, signal?: AbortSignal) {
      if (!Number.isInteger(offset) || offset < 0 || offset > 10000) throw new RequestNotSentError(400, 'invalid_stock_source_reconciliation');
      const checked = parse(targets, data(await client.POST('/api/v1/inventory/source-reconciliations/targets/query', { body: { purpose, location: warehouse.location, offset, limit: 25 }, params: { header: headers(actorId) }, signal })));
      return checked.items.every(item => item.ownerLocation.organizationId === warehouse.location.organizationId && item.ownerLocation.shopId === warehouse.location.shopId && item.ownerLocation.warehouseId === warehouse.location.warehouseId) && (checked.items.length === 0 || BigInt(offset + checked.items.length) <= BigInt(checked.total)) ? checked : invalid();
    },
    async baseline(actorId: string, poolId: string, signal?: AbortSignal) {
      const checked = parse(baseline, data(await client.POST('/api/v1/inventory/source-reconciliations/pools/{poolId}/baseline/query', { body: { purpose }, params: { path: { poolId: parse(id, poolId) }, header: headers(actorId) }, signal })));
      return checked.poolId === poolId ? checked : invalid();
    },
    async list(actorId: string, poolId: string, offset = 0, signal?: AbortSignal) {
      if (!Number.isInteger(offset) || offset < 0 || offset > 10000) throw new RequestNotSentError(400, 'invalid_stock_source_reconciliation');
      const checked = parse(history, data(await client.POST('/api/v1/inventory/source-reconciliations/pools/{poolId}/proposals/query', { body: { purpose, offset, limit: 25 }, params: { path: { poolId: parse(id, poolId) }, header: headers(actorId) }, signal })));
      return checked.items.every(item => item.proposal.poolId === poolId) && (checked.items.length === 0 || BigInt(offset + checked.items.length) <= BigInt(checked.total)) ? checked : invalid();
    },
    async read(actorId: string, proposalId: string, signal?: AbortSignal) {
      const checked = parse(read, data(await client.POST('/api/v1/inventory/source-reconciliations/proposals/{id}/query', { body: { purpose }, params: { path: { id: parse(id, proposalId) }, header: headers(actorId) }, signal })));
      return checked.proposal.id === proposalId ? checked : invalid();
    },
    async propose(command: ReturnType<typeof prepareSourceReconciliation>, key: string, current: () => boolean) {
      const operation = permissionOperation('propose'); if (reconciliationKeyOwner(key, operation).id !== command.poolId) return invalid();
      before(current); const checked = parseSourceReconciliationResult(data(await client.POST('/api/v1/inventory/source-reconciliations/proposals', { body: command.body, params: { header: { ...headers(command.actorId), 'Idempotency-Key': key } } })), key, operation); after(current); return checked;
    },
    async previewDecision(actorId: string, original: SourceReconciliationRead, action: 'confirm' | 'reject', observed: SourceReconciliationObservation | null, signal?: AbortSignal) {
      const owner = parse(read, original);
      return parseReconciliationDecisionPreview(data(await client.POST('/api/v1/inventory/source-reconciliations/proposals/{id}/review/query', { body: { purpose, decision: action, observation: observed === null ? null : entered(observation, observed) }, params: { path: { id: owner.proposal.id }, header: headers(actorId) }, signal })), owner, action, observed);
    },
    async decide(command: ReturnType<typeof prepareSourceReconciliationDecision>, key: string, current: () => boolean) {
      const operation = permissionOperation('decide'), owner = reconciliationKeyOwner(key, operation); if (owner.id !== command.proposalId || owner.action !== command.decision) return invalid();
      before(current); const checked = parseSourceReconciliationResult(data(await client.POST('/api/v1/inventory/source-reconciliations/proposals/{id}/decide', { body: command.body, params: { path: { id: command.proposalId }, header: { ...headers(command.actorId), 'If-Match': '"1"', 'Idempotency-Key': key } } })), key, operation); after(current); return checked;
    },
    async recover(actorId: string, operation: string, key: string, canReadDetail = true) {
      const owner = reconciliationKeyOwner(key, operation), checked = parse(recovery, data(await client.POST('/api/v1/inventory/source-reconciliations/operations/query', { body: { purpose: 'operation-recovery', operation, key }, params: { header: headers(actorId) } })));
      if (checked.result === null) return null;
      const outcome = parseSourceReconciliationResult(checked.result, key, operation);
      // The operation owner verifies the committed graph even when the original actor has no ordinary read grant.
      if (!canReadDetail) return { state: 'committed' as const, result: outcome };
      const actual = parse(read, data(await client.POST('/api/v1/inventory/source-reconciliations/proposals/{id}/query', { body: { purpose }, params: { path: { id: outcome.proposalId }, header: headers(actorId) } })));
      if (actual.proposal.id !== outcome.proposalId || actual.proposal.poolId !== outcome.poolId || (owner.action === 'propose' ? actual.proposal.poolId !== owner.id || actual.proposal.recordedBy !== actorId || actual.proposal.recordedAt !== outcome.recordedAt : !actual.decision || actual.decision.id !== outcome.decisionId || actual.decision.recordedBy !== actorId || actual.decision.recordedAt !== outcome.recordedAt || actual.decision.decision !== owner.action)) return invalid();
      return { state: 'committed' as const, result: outcome };
    },
  };
}
const permissionOperation = (action: 'propose' | 'decide') => 'stock.source.reconciliation.' + action;
