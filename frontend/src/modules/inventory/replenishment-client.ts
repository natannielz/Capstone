import {z} from 'zod';
import {fetchCsrfToken, RequestNotSentError} from '../../api/client';
import {AccessFailure, stableContent} from '../administration/workflow';
import type {AccessScope} from '../organizations/access-scope';
import {parseStockLocation, parseStockPackages, parseStockWarehouses, stockLocationKey} from './stock-count-workflow';

const maximum = BigInt('9223372036854775807');
const unsignedPattern = /^(0|[1-9][0-9]{0,18})$(?![\s\S])/;
const quantity = z.string().regex(unsignedPattern).refine(value => unsignedPattern.test(value) && BigInt(value) <= maximum);
const id = quantity.refine(value => value !== '0');
const signedPattern = /^-?(0|[1-9][0-9]{0,18})$(?![\s\S])/;
const signed = z.string().regex(signedPattern).refine(value => signedPattern.test(value) && value !== '-0' && BigInt(value) <= maximum && BigInt(value) >= -maximum - BigInt(1));
const text = (maximum: number) => z.string().min(1).max(maximum).refine(value => value.trim() === value && !/\p{Cc}/u.test(value) && value.isWellFormed());
const decimal = z.string().max(60).regex(/^(0|[1-9][0-9]*)(\.[0-9]+)?$(?![\s\S])/);
const time = z.string().datetime({offset: true});
const location = z.object({kind: z.literal('fulfillmentWarehouse'), organizationId: id, shopId: id, warehouseId: id, channelId: id,
  branchId: z.null(), divisionId: z.null()}).strict();
const values = z.object({dailyDemandPerDay: decimal.nullable(), leadTimeDays: z.number().int().min(0).max(2147483647).nullable(),
  safetyStockPackages: quantity.nullable(), reorderPointPackages: quantity.nullable(), purchaseMinimumPackages: id, purchaseIncrementPackages: id}).strict();
const source = z.object({id, revision: id, label: text(160), physicalKey: text(160), active: z.boolean()}).strict();
const supply = z.object({reference: text(160), packages: id, expectedAt: time.nullable(), verified: z.boolean(),
  state: z.enum(['unverified', 'expected_arrival_missing', 'overdue', 'lead_time_missing', 'after_horizon', 'eligible'])}).strict();
const reviewInput = z.object({location, poolId: id, expectedPoolRevision: id, skuId: id, expectedSkuRevision: id, definitionId: id,
  inputs: values, inputReference: text(500), reason: text(1000)}).strict();
const evaluation = z.object({id, location, poolId: id, poolRevision: id, skuId: id, skuRevision: id, definitionId: id, definitionVersion: id,
  skuCode: text(64), packageLabel: text(160), packageKind: z.enum(['loose', 'sealed', 'other']), baseUnitCode: text(64),
  baseUnitsPerPackage: z.number().int().min(1).max(2147483647), inputs: values, inputReference: text(500), reason: text(1000),
  sourceHash: z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/), ruleVersion: z.literal('replenishment-lead-time-v1'), observedAt: time, horizon: time.nullable(),
  currentAuthority: z.boolean(), usableBeforeReservations: quantity.nullable(), reservedPackages: quantity.nullable(), additionalUnreservedCommitments: quantity.nullable(),
  availableAfterCommitments: signed.nullable(), thresholdAlert: z.boolean().nullable(), eligibleSupplyPackages: quantity.nullable(),
  inventoryPosition: signed.nullable(), requiredStockPackages: quantity.nullable(), suggestedPurchasePackages: quantity.nullable(),
  warnings: z.array(text(100)).max(64), supplies: z.array(supply).max(1000)}).strict().superRefine((value, context) => {
  const invalid = () => context.addIssue({code: 'custom', message: 'Inconsistent replenishment response'});
  if ([value.usableBeforeReservations, value.reservedPackages, value.additionalUnreservedCommitments, value.eligibleSupplyPackages,
    value.requiredStockPackages, value.suggestedPurchasePackages, value.inputs.reorderPointPackages].some(item => item !== null && !quantity.safeParse(item).success) ||
    [value.availableAfterCommitments, value.inventoryPosition].some(item => item !== null && !signed.safeParse(item).success)) return;
  if (new Set(value.warnings).size !== value.warnings.length || new Set(value.supplies.map(item => item.reference)).size !== value.supplies.length) invalid();
  if (!value.currentAuthority && [value.availableAfterCommitments, value.inventoryPosition, value.suggestedPurchasePackages].some(item => item !== null)) invalid();
  if (value.availableAfterCommitments !== null) {
    if (!value.currentAuthority || value.usableBeforeReservations === null || value.reservedPackages === null || value.additionalUnreservedCommitments === null) invalid();
    else if (BigInt(value.availableAfterCommitments) !== BigInt(value.usableBeforeReservations) - BigInt(value.reservedPackages) - BigInt(value.additionalUnreservedCommitments)) invalid();
  }
  if (value.thresholdAlert !== null && (value.availableAfterCommitments === null || value.inputs.reorderPointPackages === null ||
    value.thresholdAlert !== (BigInt(value.availableAfterCommitments) <= BigInt(value.inputs.reorderPointPackages)))) invalid();
  if (value.inventoryPosition !== null && (value.availableAfterCommitments === null || value.eligibleSupplyPackages === null ||
    BigInt(value.inventoryPosition) !== BigInt(value.availableAfterCommitments) + BigInt(value.eligibleSupplyPackages))) invalid();
  if (value.suggestedPurchasePackages !== null && (value.inventoryPosition === null || value.requiredStockPackages === null ||
    value.inputs.dailyDemandPerDay === null || value.inputs.leadTimeDays === null || value.inputs.safetyStockPackages === null)) invalid();
  if (value.supplies.some(item => item.state === 'eligible' && (!item.verified || item.expectedAt === null || value.horizon === null ||
    Date.parse(item.expectedAt) < Date.parse(value.observedAt) || Date.parse(item.expectedAt) > Date.parse(value.horizon)))) invalid();
});
export type ReplenishmentInput = z.infer<typeof reviewInput>;
export type ReplenishmentValues = z.infer<typeof values>;
export type ReplenishmentSource = z.infer<typeof source>;
export type ReplenishmentEvaluation = z.infer<typeof evaluation>;
export const replenishmentOperation = 'inventory.replenishment.evaluate';
export function parseReplenishmentInput(value: unknown): ReplenishmentInput {
  const parsed = reviewInput.safeParse(value); if (!parsed.success) throw new RequestNotSentError(400, 'invalid_replenishment_review'); return parsed.data;
}
export function parseReplenishmentEvaluation(value: unknown): ReplenishmentEvaluation {
  const parsed = evaluation.safeParse(value); if (!parsed.success) throw new AccessFailure(502, 'replenishment_response_invalid'); return parsed.data;
}
export function replenishmentValues(draft: Record<keyof ReplenishmentValues, string>): ReplenishmentValues {
  const optional = (value: string) => value.trim() === '' ? null : value.trim();
  const lead = optional(draft.leadTimeDays);
  if (lead !== null && !unsignedPattern.test(lead)) throw new RequestNotSentError(400, 'invalid_replenishment_review');
  const parsed = values.safeParse({dailyDemandPerDay: optional(draft.dailyDemandPerDay), leadTimeDays: lead === null ? null : Number(lead),
    safetyStockPackages: optional(draft.safetyStockPackages), reorderPointPackages: optional(draft.reorderPointPackages),
    purchaseMinimumPackages: draft.purchaseMinimumPackages.trim(), purchaseIncrementPackages: draft.purchaseIncrementPackages.trim()});
  if (!parsed.success) throw new RequestNotSentError(400, 'invalid_replenishment_review'); return parsed.data;
}
type Context = {actorId: string; signal?: AbortSignal; isCurrent: () => boolean};
export function createReplenishmentGateway(fetcher: typeof globalThis.fetch = globalThis.fetch, csrfUrl = '/api/v1/identity/csrf') {
  async function send(path: string, body: unknown, context: Context, key?: string): Promise<unknown> {
    const current = () => {if (context.signal?.aborted || !context.isCurrent()) throw new RequestNotSentError(409, 'replenishment_view_stale');};
    current(); if (!id.safeParse(context.actorId).success || key !== undefined && !/^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/.test(key)) throw new RequestNotSentError(400, 'invalid_replenishment_review');
    const csrf = await fetchCsrfToken(csrfUrl, fetcher, context.signal); current();
    const response = await fetcher('/api/v1/inventory/replenishment' + path, {method: 'POST', credentials: 'same-origin', redirect: 'error', cache: 'no-store', signal: context.signal,
      headers: {'Content-Type': 'application/json', 'X-CSRF-Token': csrf, 'X-Expected-Actor-Id': context.actorId, ...(key ? {'Idempotency-Key': key} : {})}, body: JSON.stringify(body)});
    const value: unknown = await response.json();
    // Once sent, a stale page cannot establish that the server did not commit.
    if (context.signal?.aborted || !context.isCurrent()) throw new AccessFailure(502, 'replenishment_view_stale');
    if (!response.ok) throw new AccessFailure(response.status, value !== null && typeof value === 'object' && 'code' in value && typeof value.code === 'string' ? value.code : 'replenishment_unavailable');
    return value;
  }
  return {
    async warehouses(context: Context) {return parseStockWarehouses(await send('/warehouses/query', {purpose: 'inventory-replenishment'}, context));},
    async sources(place: AccessScope, context: Context) {
      const result = z.object({items: z.array(source).max(256)}).strict().safeParse(await send('/sources/query', {purpose: 'inventory-replenishment', location: parseStockLocation(place)}, context));
      if (!result.success || new Set(result.data.items.map(item => item.id)).size !== result.data.items.length) throw new AccessFailure(502, 'replenishment_response_invalid');
      return result.data.items;
    },
    async packages(place: AccessScope, search: string, offset: number, context: Context) {
      return parseStockPackages(await send('/packages/query', {purpose: 'inventory-replenishment', location: parseStockLocation(place), search: search.trim() || null, offset, limit: 25}, context), offset, 25);
    },
    async evaluate(input: ReplenishmentInput, key: string, context: Context) {
      const body = parseReplenishmentInput(input); const result = parseReplenishmentEvaluation(await send('', body, context, key));
      if (stockLocationKey(result.location) !== stockLocationKey(body.location) || result.poolId !== body.poolId || result.poolRevision !== body.expectedPoolRevision ||
        result.skuId !== body.skuId || result.skuRevision !== body.expectedSkuRevision || result.definitionId !== body.definitionId ||
        stableContent(result.inputs) !== stableContent(body.inputs) || result.inputReference !== body.inputReference || result.reason !== body.reason)
        throw new AccessFailure(502, 'replenishment_response_invalid');
      return result;
    },
    async history(place: AccessScope, before: string | null, context: Context) {
      if (before !== null && !id.safeParse(before).success) throw new RequestNotSentError(400, 'invalid_replenishment_review');
      const response = await send('/history/query', {purpose: 'inventory-replenishment', location: parseStockLocation(place), before, limit: 10}, context);
      const parsed = z.object({items: z.array(evaluation).max(10), nextBefore: id.nullable()}).strict().safeParse(response);
      if (!parsed.success) throw new AccessFailure(502, 'replenishment_response_invalid');
      const page = parsed.data, ids = page.items.map(item => BigInt(item.id));
      if (page.items.some(item => stockLocationKey(item.location) !== stockLocationKey(place)) ||
          ids.some((value, index) => before !== null && value >= BigInt(before) || index > 0 && value >= ids[index - 1]) ||
          page.nextBefore !== null && (page.items.length !== 10 || page.nextBefore !== page.items.at(-1)?.id))
        throw new AccessFailure(502, 'replenishment_response_invalid');
      return page;
    },
    async read(reference: string, context: Context) {
      if (!id.safeParse(reference).success) throw new RequestNotSentError(400, 'invalid_replenishment_review');
      const result = await send('/' + reference + '/query', {purpose: 'inventory-replenishment'}, context);
      if (result === null) return null;
      const parsed = parseReplenishmentEvaluation(result);
      if (parsed.id !== reference) throw new AccessFailure(502, 'replenishment_response_invalid');
      return parsed;
    },
    async recover(key: string, context: Context) {
      const response = await send('/operations/query', {purpose: 'operation-recovery', operation: replenishmentOperation, key}, context);
      const parsed = z.object({state: z.enum(['not_confirmed', 'committed']), evaluation: z.unknown().nullable()}).strict().safeParse(response);
      if (!parsed.success || parsed.data.state === 'not_confirmed' && parsed.data.evaluation !== null) throw new AccessFailure(502, 'replenishment_response_invalid');
      return parsed.data.state === 'not_confirmed' ? null : parseReplenishmentEvaluation(parsed.data.evaluation);
    },
  };
}

const pending = z.object({actorId: id, key: z.string().regex(/^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/), createdAt: time}).strict();
export type ReplenishmentPending = z.infer<typeof pending>;
type RecoveryStorage = Pick<Storage, 'length' | 'key' | 'getItem' | 'setItem' | 'removeItem'>;
const legacyRecoveryKey = (actorId: string) => 'unit-toko:replenishment-pending:v1:' + actorId;
export function replenishmentRecoveryPrefix(actorId: string) {
  if (!id.safeParse(actorId).success) throw new Error('Invalid recovery owner');
  return 'unit-toko:replenishment-pending:v2:' + actorId + ':';
}
export function isReplenishmentRecoveryKey(actorId: string, key: string | null) {
  return key === null || key === legacyRecoveryKey(actorId) || key.startsWith(replenishmentRecoveryPrefix(actorId));
}
/** Recovery references only. Planning inputs, stock quantities and credentials never enter browser storage. */
export class ReplenishmentRecovery {
  constructor(private readonly storage: () => RecoveryStorage) {}
  private parse(value: string, actorId: string): ReplenishmentPending {
    const parsed = pending.safeParse(JSON.parse(value));
    if (!parsed.success || parsed.data.actorId !== actorId) throw new Error('Invalid recovery reference');
    return parsed.data;
  }
  list(actorId: string): ReplenishmentPending[] {
    const prefix = replenishmentRecoveryPrefix(actorId), storage = this.storage();
    const keys = Array.from({length: storage.length}, (_, index) => storage.key(index))
      .filter((key): key is string => key !== null && key.startsWith(prefix));
    keys.push(legacyRecoveryKey(actorId));
    const entries = new Map<string, ReplenishmentPending>();
    for (const key of keys) {
      // Another tab may have confirmed a reference after this key snapshot.
      const raw = storage.getItem(key); if (raw === null) continue;
      const entry = this.parse(raw, actorId);
      if (key !== legacyRecoveryKey(actorId) && key !== prefix + entry.key) throw new Error('Invalid recovery reference');
      const previous = entries.get(entry.key);
      if (previous && JSON.stringify(previous) !== JSON.stringify(entry)) throw new Error('Recovery reference changed');
      entries.set(entry.key, entry);
    }
    return [...entries.values()].sort((left, right) => Date.parse(left.createdAt) - Date.parse(right.createdAt) || left.key.localeCompare(right.key));
  }
  read(actorId: string): ReplenishmentPending | null {return this.list(actorId)[0] ?? null;}
  save(value: ReplenishmentPending) {
    const parsed = pending.parse(value), prefix = replenishmentRecoveryPrefix(parsed.actorId), storage = this.storage();
    if (this.list(parsed.actorId).some(entry => entry.key !== parsed.key)) throw new Error('Resolve the existing replenishment review first');
    const key = prefix + parsed.key, serialized = JSON.stringify(parsed), existing = storage.getItem(key);
    if (existing !== null && JSON.stringify(this.parse(existing, parsed.actorId)) !== serialized) throw new Error('Recovery reference changed');
    // Distinct operation keys remain distinct even when two tabs both observed an empty journal.
    storage.setItem(key, serialized);
    if (storage.getItem(key) !== serialized) throw new Error('Recovery reference was not retained');
  }
  clear(actorId: string, key: string) {
    const prefix = replenishmentRecoveryPrefix(actorId), storage = this.storage();
    const entry = this.list(actorId).find(value => value.key === key); if (!entry) return;
    for (const storedKey of [prefix + key, legacyRecoveryKey(actorId)]) {
      const raw = storage.getItem(storedKey); if (raw === null) continue;
      const saved = this.parse(raw, actorId); if (saved.key !== key) continue;
      if (JSON.stringify(saved) !== JSON.stringify(entry)) throw new Error('Recovery reference changed');
      storage.removeItem(storedKey);
      const remaining = storage.getItem(storedKey);
      if (remaining !== null && this.parse(remaining, actorId).key === key) throw new Error('Recovery reference could not be cleared');
    }
  }
}
