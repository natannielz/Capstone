import { AccessFailure } from '../administration/workflow';
import { validReference } from '../organizations/access-scope';
import { catalogText } from './catalog-editor-state';
import { parseCatalogDefinition } from './catalog-workflow';

export const pricePath = '/catalog/prices';
export type PriceAccess = 'prepare' | 'review';
export type PriceAction = 'propose' | 'approve' | 'reject' | 'withdraw';
export const priceInvalid = (): never => { throw new AccessFailure(502, 'catalog_price_response_invalid'); };
const inputInvalid = (): never => { throw new AccessFailure(400, 'catalog_price_input_invalid'); };
export const priceRecord = (value: unknown): Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : priceInvalid();
export const priceId = (value: unknown): string => typeof value === 'string' && value === value.trim() && validReference(value) ? value : priceInvalid();
const version = (value: unknown): string => value === '0' ? '0' : priceId(value);
const text = (value: unknown, max: number): string => {
  if (typeof value !== 'string') return priceInvalid();
  try { return catalogText(value, max) === value ? value : priceInvalid(); } catch { return priceInvalid(); }
};
const time = (value: unknown): string => typeof value === 'string' && value.length <= 40 && /^\d{4}-\d\d-\d\dT/.test(value) && Number.isFinite(Date.parse(value)) ? value : priceInvalid();
const currency = (value: unknown): string => typeof value === 'string' && value.length === 3 && /^[A-Z]{3}$/.test(value) ? value : priceInvalid();
export function priceMoney(value: string): string {
  if (typeof value !== 'string' || !/^(0|[1-9][0-9]{0,14})(\.[0-9]{1,2})?$(?![\s\S])/.test(value)) return inputInvalid();
  const [whole, fraction = ''] = value.split('.'); return whole + '.' + fraction.padEnd(2, '0');
}
const money = (value: unknown): string => {
  if (typeof value !== 'string') return priceInvalid();
  try { return priceMoney(value) === value ? value : priceInvalid(); } catch { return priceInvalid(); }
};
function sku(value: unknown) {
  const v = priceRecord(value), code = text(v.code, 64), publicId = text(v.publicId, 36);
  if (!/^[A-Z0-9._-]+$/.test(code) || !/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(publicId) || typeof v.active !== 'boolean') return priceInvalid();
  return { id: priceId(v.id), familyId: priceId(v.familyId), code, publicId, version: priceId(v.version), active: v.active };
}
export function parsePrice(value: unknown) {
  const v = priceRecord(value); return { id: priceId(v.id), skuId: priceId(v.skuId), version: priceId(v.version), currency: currency(v.currency), unitPrice: money(v.unitPrice), effectiveAt: time(v.effectiveAt) };
}
export type CatalogPrice = ReturnType<typeof parsePrice>;
export function parsePricePreview(value: unknown) {
  const v = priceRecord(value), product = sku(v.sku), definition = parseCatalogDefinition(v.definition), e = priceRecord(v.expected);
  const expected = { skuVersion: priceId(e.skuVersion), definitionId: priceId(e.definitionId), definitionVersion: priceId(e.definitionVersion), priceVersion: version(e.priceVersion) };
  const currentPrice = v.currentPrice === null ? null : parsePrice(v.currentPrice);
  if (product.id !== definition.skuId || expected.skuVersion !== product.version || expected.definitionId !== definition.id || expected.definitionVersion !== definition.version ||
    (currentPrice ? currentPrice.skuId !== product.id || currentPrice.version !== expected.priceVersion : expected.priceVersion !== '0')) return priceInvalid();
  return { sku: product, definition, expected, currentPrice };
}
export type PricePreview = ReturnType<typeof parsePricePreview>;
export function parsePriceChoice(value: unknown) {
  const v = priceRecord(value), product = sku(v.sku), definition = parseCatalogDefinition(v.definition);
  if (definition.skuId !== product.id) return priceInvalid();
  return { sku: product, definition, familyName: text(v.familyName, 160) };
}
export type PriceChoice = ReturnType<typeof parsePriceChoice>;
export function priceItems<T>(value: unknown, parse: (row: unknown) => T, key: (row: T) => string, limit = 25): T[] {
  const rows = priceRecord(value).items; if (!Array.isArray(rows) || rows.length > limit) return priceInvalid();
  const items = rows.map(parse); if (new Set(items.map(key)).size !== items.length) return priceInvalid(); return items;
}
export function parsePriceProposal(value: unknown) {
  const v = priceRecord(value), base = parsePricePreview({ ...v, currentPrice: v.basePrice });
  const proposalId = priceId(v.proposalId), skuId = priceId(v.skuId), revision = priceId(v.revision), status = v.status;
  if (skuId !== base.sku.id || revision !== '1' || !['proposed', 'approve', 'reject', 'withdraw'].includes(status as string)) return priceInvalid();
  const i = priceRecord(v.input), input = { currency: currency(i.currency), unitPrice: money(i.unitPrice) };
  const d = v.decision === null ? null : priceRecord(v.decision);
  const decision = d === null ? null : { id: priceId(d.id), action: text(d.action, 16), decidedAt: time(d.decidedAt), reviewReference: text(d.reviewReference, 160), reason: text(d.reason, 1000) };
  const price = v.price === null ? null : parsePrice(v.price), proposedAt = time(v.proposedAt);
  if (status === 'proposed' ? decision !== null || price !== null : decision === null || decision.action !== status || Date.parse(decision.decidedAt) < Date.parse(proposedAt)) return priceInvalid();
  if (status === 'approve' ? price === null || price.skuId !== skuId || price.version !== String(BigInt(base.expected.priceVersion) + BigInt(1)) || price.unitPrice !== input.unitPrice || price.currency !== input.currency || Date.parse(price.effectiveAt) !== Date.parse(decision!.decidedAt) : price !== null) return priceInvalid();
  return { proposalId, skuId, revision, status: status as 'proposed' | 'approve' | 'reject' | 'withdraw', sku: base.sku, definition: base.definition, expected: base.expected,
    basePrice: base.currentPrice, input, proposedAt, ownProposal: typeof v.ownProposal === 'boolean' ? v.ownProposal : priceInvalid(), reason: text(v.reason, 1000), decision, price };
}
export type PriceProposal = ReturnType<typeof parsePriceProposal>;
export function priceOutcome(value: unknown, action: PriceAction, skuId: string, proposalId?: string) {
  const v = priceRecord(value), result = { proposalId: priceId(v.proposalId), skuId: priceId(v.skuId), revision: priceId(v.revision), status: v.status,
    decisionId: v.decisionId === null ? null : priceId(v.decisionId), priceId: v.priceId === null ? null : priceId(v.priceId), priceVersion: v.priceVersion === null ? null : priceId(v.priceVersion) };
  if (result.skuId !== skuId || proposalId !== undefined && result.proposalId !== proposalId || result.revision !== '1' || result.status !== (action === 'propose' ? 'proposed' : action) ||
    (action === 'propose' ? result.decisionId !== null : result.decisionId === null) || (action === 'approve' ? result.priceId === null || result.priceVersion === null : result.priceId !== null || result.priceVersion !== null)) return priceInvalid();
  return { ...result, status: result.status as 'proposed' | 'approve' | 'reject' | 'withdraw' };
}
export type PriceResult = ReturnType<typeof priceOutcome>;
function freeze<T>(value: T): T { if (value && typeof value === 'object') { Object.values(value).forEach(freeze); Object.freeze(value); } return value; }
export function preparePriceProposal(actorId: string, value: unknown, currencyValue: string, amount: string, reason: string) {
  const preview = parsePricePreview(value); if (!preview.sku.active) return inputInvalid();
  const review = { currency: currency(currencyValue), unitPrice: priceMoney(amount), reason: catalogText(reason, 1000) };
  const e = preview.expected;
  return freeze({ actorId: priceId(actorId), skuId: preview.sku.id, action: 'propose' as const, preview, review,
    body: { expectedDefinitionId: e.definitionId, expectedDefinitionVersion: e.definitionVersion, expectedPriceVersion: e.priceVersion, input: { currency: review.currency, unitPrice: review.unitPrice }, reason: review.reason } });
}
export function preparePriceDecision(actorId: string, value: unknown, action: Exclude<PriceAction, 'propose'>, reference: string, reason: string) {
  const proposal = parsePriceProposal(value); if (proposal.status !== 'proposed' || !['approve', 'reject', 'withdraw'].includes(action)) return inputInvalid();
  const review = { reference: catalogText(reference, 160), reason: catalogText(reason, 1000) };
  return freeze({ actorId: priceId(actorId), skuId: proposal.skuId, proposalId: proposal.proposalId, action, proposal, review,
    body: { reviewReference: review.reference, reason: review.reason } });
}
export type PriceCommand = ReturnType<typeof preparePriceProposal> | ReturnType<typeof preparePriceDecision>;
export function priceAction(operation: string): PriceAction {
  const match = /^catalog\.price\.(propose|approve|reject|withdraw)$(?![\s\S])/.exec(operation);
  return match ? match[1] as PriceAction : inputInvalid();
}

/** Moves of this size or more are flagged for the reviewer to check against the contract and the reference. */
export const LARGE_PRICE_CHANGE_PERCENT = 20;
type PriceAmount = { currency: string; unitPrice: string };
/** How a proposed price moves from the current one: the exact amount, a one-decimal percentage (rounded half up) and a flag on large moves. */
export function priceChange(base: PriceAmount | null, proposed: PriceAmount): { kind: 'first' | 'currency' | 'up' | 'down' | 'same'; difference: string | null; percent: string | null; large: boolean } {
  if (base === null) return { kind: 'first', difference: null, percent: null, large: false };
  if (base.currency !== proposed.currency) return { kind: 'currency', difference: null, percent: null, large: true };
  const zero = BigInt(0), from = BigInt(base.unitPrice.replace('.', '')), delta = BigInt(proposed.unitPrice.replace('.', '')) - from;
  const size = delta < zero ? -delta : delta, digits = size.toString().padStart(3, '0');
  const kind = delta > zero ? 'up' : delta < zero ? 'down' : 'same', difference = digits.slice(0, -2) + '.' + digits.slice(-2);
  if (from === zero) return { kind, difference, percent: kind === 'same' ? '0,0' : null, large: kind !== 'same' };
  const tenths = (size * BigInt(2000) + from) / (from * BigInt(2)), ten = BigInt(10);
  return { kind, difference, percent: (tenths / ten).toString() + ',' + (tenths % ten).toString(), large: tenths >= BigInt(LARGE_PRICE_CHANGE_PERCENT * 10) };
}
