import { AccessFailure, stableContent } from '../administration/workflow';
import type { AccessScope } from '../organizations/access-scope';
import { parseStockLocation, stockLocationKey, type StockPackageChoice } from './stock-count-workflow';
import { parsePackPackages, parsePackRule, packBinding, type PackRule } from './pack-conversion-workflow';
import { sourceActor, sourceNumber, sourceText, sourceTime, freezeSource } from './stock-source-workflow';

export const conversionOperation = 'inventory.pack-conversion.execute' as const;
export const conversionValuePolicy = 'remaining-proportional-minor-unit-floor-v1' as const;
const bad = (): never => { throw new AccessFailure(502, 'pack_execution_response_invalid'); };
const invalid = (): never => { throw new AccessFailure(400, 'pack_execution_input_invalid'); };
function exact(value: unknown, fields: string[]) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return bad();
  const row = value as Record<string, unknown>;
  if (Object.keys(row).sort().join(',') !== [...fields].sort().join(',')) return bad();
  return row;
}
const number = (value: unknown, zero = false) => sourceNumber(value, zero);
function money(value: unknown): string {
  return typeof value === 'string' && /^(0|[1-9][0-9]{0,14})\.[0-9]{2}$(?![\s\S])/.test(value) ? value : bad();
}
function location(value: unknown) {
  exact(value, ['kind', 'organizationId', 'branchId', 'divisionId', 'shopId', 'warehouseId', 'channelId']);
  return parseStockLocation(value);
}
function packageChoice(value: unknown, role: 'source' | 'destination'): StockPackageChoice {
  return parsePackPackages({ total: '1', items: [value] }, role).items[0];
}
function policy(value: unknown) { return value === conversionValuePolicy ? conversionValuePolicy : bad(); }
function expiry(value: unknown): string | null {
  if (value === null) return null;
  if (typeof value !== 'string' || !/^[0-9]{4}-[0-9]{2}-[0-9]{2}$(?![\s\S])/.test(value) || value.startsWith('0000') ||
    !Number.isFinite(Date.parse(value)) || new Date(value).toISOString().slice(0, 10) !== value) return bad();
  return value;
}
export type ConversionInput = Readonly<{ location: AccessScope; ruleId: string; ruleVersion: string; sourceBalanceId: string; steps: number }>;
export function parseConversionInput(value: unknown): ConversionInput {
  const v = exact(value, ['location', 'ruleId', 'ruleVersion', 'sourceBalanceId', 'steps']);
  if (typeof v.steps !== 'number' || !Number.isInteger(v.steps) || v.steps < 1 || v.steps > 1000000) return bad();
  return freezeSource({ location: location(v.location), ruleId: number(v.ruleId), ruleVersion: number(v.ruleVersion),
    sourceBalanceId: number(v.sourceBalanceId), steps: v.steps });
}
function expected(value: unknown) {
  const v = exact(value, ['sourceBalanceRevision', 'poolRevision', 'poolFence', 'lotValueRevision']);
  return { sourceBalanceRevision: number(v.sourceBalanceRevision), poolRevision: number(v.poolRevision),
    poolFence: number(v.poolFence, true), lotValueRevision: number(v.lotValueRevision) };
}
function stock(value: unknown) {
  const v = exact(value, ['balanceId', 'poolId', 'lotId', 'onHand', 'reserved', 'held', 'expired', 'revision']);
  const result = { balanceId: number(v.balanceId), poolId: number(v.poolId), lotId: number(v.lotId), onHand: number(v.onHand, true),
    reserved: number(v.reserved, true), held: number(v.held, true), expired: number(v.expired, true), revision: number(v.revision) };
  if (BigInt(result.reserved) + BigInt(result.held) + BigInt(result.expired) > BigInt(result.onHand)) return bad();
  return result;
}
function packagesMatch(source: StockPackageChoice, destination: StockPackageChoice, rule?: PackRule) {
  if (source.baseUnitCode !== destination.baseUnitCode || source.skuId === destination.skuId || source.definitionId === destination.definitionId ||
    rule && (stableContent(source) !== stableContent(rule.source) || stableContent(destination) !== stableContent(rule.destination))) return bad();
}
function quantities(input: ConversionInput, source: StockPackageChoice, destination: StockPackageChoice, v: Record<string, unknown>, rule?: PackRule) {
  packagesMatch(source, destination, rule);
  const sourcePackages = number(v.sourcePackages), destinationPackages = number(v.destinationPackages), baseUnits = number(v.baseUnits), steps = BigInt(input.steps);
  if (BigInt(sourcePackages) % steps || BigInt(destinationPackages) % steps || BigInt(sourcePackages) / steps > BigInt("1000000") ||
    BigInt(destinationPackages) / steps > BigInt("1000000") || BigInt(baseUnits) !== BigInt(sourcePackages) * BigInt(source.baseUnitsPerPackage) ||
    BigInt(baseUnits) !== BigInt(destinationPackages) * BigInt(destination.baseUnitsPerPackage) || rule &&
    (BigInt(sourcePackages) !== steps * BigInt(rule.input.sourcePackagesPerStep) || BigInt(destinationPackages) !== steps * BigInt(rule.input.destinationPackagesPerStep))) return bad();
  return { sourcePackages, destinationPackages, baseUnits };
}
function stockTransition(input: ConversionInput, before: ReturnType<typeof stock>, after: ReturnType<typeof stock>, sourcePackages: string) {
  if (before.balanceId !== input.sourceBalanceId || after.balanceId !== before.balanceId || after.poolId !== before.poolId || after.lotId !== before.lotId ||
    after.reserved !== before.reserved || after.held !== before.held || after.expired !== before.expired ||
    BigInt(after.revision) !== BigInt(before.revision) + BigInt("1") || BigInt(before.onHand) - BigInt(after.onHand) !== BigInt(sourcePackages)) return bad();
}
export function parseConversionSource(value: unknown, rule: PackRule) {
  const v = exact(value, ['sourceBalanceId', 'sourceBalanceRevision', 'ruleId', 'ruleVersion', 'source', 'destination', 'receiptReference', 'lotReference',
    'lotId', 'expiresOn', 'availableSourcePackages', 'remainingPackages', 'remainingValue', 'maximumSteps', 'policy']);
  const source = packageChoice(v.source, 'source'), destination = packageChoice(v.destination, 'destination'); packagesMatch(source, destination, rule);
  const availableSourcePackages = number(v.availableSourcePackages, true), remainingPackages = number(v.remainingPackages, true), maximumSteps = number(v.maximumSteps, true);
  if (v.ruleId !== rule.id || v.ruleVersion !== rule.version || BigInt(availableSourcePackages) > BigInt(remainingPackages) ||
    BigInt(maximumSteps) !== BigInt(availableSourcePackages) / BigInt(rule.input.sourcePackagesPerStep)) return bad();
  return freezeSource({ sourceBalanceId: number(v.sourceBalanceId), sourceBalanceRevision: number(v.sourceBalanceRevision), ruleId: rule.id, ruleVersion: rule.version,
    source, destination, receiptReference: sourceText(v.receiptReference, 160), lotReference: sourceText(v.lotReference, 160), lotId: number(v.lotId), expiresOn: expiry(v.expiresOn),
    availableSourcePackages, remainingPackages, remainingValue: money(v.remainingValue), maximumSteps, policy: policy(v.policy) });
}
export type ConversionSource = ReturnType<typeof parseConversionSource>;
export function parseConversionSources(value: unknown, rule: PackRule, cursor: string | null = null) {
  try {
    const v = exact(value, ['items', 'unavailableCount', 'nextSourceBalanceId']), unavailableCount = number(v.unavailableCount, true);
    if (!Array.isArray(v.items) || BigInt(v.items.length) + BigInt(unavailableCount) > BigInt("25")) return bad();
    const items = v.items.map(item => parseConversionSource(item, rule)), next = v.nextSourceBalanceId === null ? null : number(v.nextSourceBalanceId);
    if (items.some((item, i) => cursor !== null && BigInt(item.sourceBalanceId) >= BigInt(cursor) || i > 0 && BigInt(items[i - 1].sourceBalanceId) <= BigInt(item.sourceBalanceId)) ||
      next !== null && (BigInt(items.length) + BigInt(unavailableCount) !== BigInt("25") || cursor !== null && BigInt(next) >= BigInt(cursor) ||
        items.some(item => BigInt(item.sourceBalanceId) < BigInt(next)))) return bad();
    return freezeSource({ items, unavailableCount, next });
  } catch { return bad(); }
}
export function prepareConversionInput(place: AccessScope, rule: PackRule, source: ConversionSource, steps: string): ConversionInput {
  try {
    const selectedRule = parsePackRule(rule), selectedSource = parseConversionSource(source, selectedRule);
    if (stockLocationKey(place) !== stockLocationKey(selectedRule.input.location) || !/^[1-9][0-9]{0,6}$(?![\s\S])/.test(steps) ||
      BigInt(steps) > BigInt(selectedSource.maximumSteps)) return invalid();
    return parseConversionInput({ location: place, ruleId: selectedRule.id, ruleVersion: selectedRule.version, sourceBalanceId: selectedSource.sourceBalanceId, steps: Number(steps) });
  } catch { return invalid(); }
}
export function parseConversionPreview(value: unknown, input?: ConversionInput, rule?: PackRule, selected?: ConversionSource) {
  try {
    const v = exact(value, ['input', 'expected', 'source', 'destination', 'stockBefore', 'stockAfter', 'remainingPackages', 'remainingValue', 'sourcePackages',
      'destinationPackages', 'baseUnits', 'policy', 'binding', 'observedAt']);
    const actual = parseConversionInput(v.input), expectation = expected(v.expected), source = packageChoice(v.source, 'source'), destination = packageChoice(v.destination, 'destination');
    const q = quantities(actual, source, destination, v, rule), stockBefore = stock(v.stockBefore), stockAfter = stock(v.stockAfter), remainingPackages = number(v.remainingPackages);
    stockTransition(actual, stockBefore, stockAfter, q.sourcePackages);
    if (input && stableContent(actual) !== stableContent(input) || rule && (actual.ruleId !== rule.id || actual.ruleVersion !== rule.version) ||
      expectation.sourceBalanceRevision !== stockBefore.revision || BigInt(remainingPackages) < BigInt(q.sourcePackages) ||
      BigInt(q.sourcePackages) > BigInt(stockBefore.onHand) - BigInt(stockBefore.reserved) - BigInt(stockBefore.held) - BigInt(stockBefore.expired) ||
      selected && (actual.sourceBalanceId !== selected.sourceBalanceId || stockBefore.lotId !== selected.lotId)) return bad();
    return freezeSource({ input: actual, expected: expectation, source, destination, stockBefore, stockAfter, remainingPackages, remainingValue: money(v.remainingValue),
      ...q, policy: policy(v.policy), binding: packBinding(v.binding), observedAt: sourceTime(v.observedAt) });
  } catch { return bad(); }
}
export type ConversionPreview = ReturnType<typeof parseConversionPreview>;
/** Exact minor units keep the server's proportional floor visible without floating point rounding. */
export function conversionTransferredValue(preview: ConversionPreview): string {
  const minor = BigInt(preview.remainingValue.replace('.', '')) * BigInt(preview.sourcePackages) / BigInt(preview.remainingPackages);
  return (minor / BigInt("100")).toString() + '.' + (minor % BigInt("100")).toString().padStart(2, '0');
}
export function prepareConversion(actorId: string, preview: ConversionPreview, reason: string) {
  try {
    const reviewed = parseConversionPreview(preview);
    return freezeSource({ actorId: sourceActor(actorId), operation: conversionOperation, preview: reviewed,
      body: { input: reviewed.input, expected: reviewed.expected, binding: reviewed.binding, reason: sourceText(reason.trim(), 1000) } });
  } catch { return invalid(); }
}
export type ConversionCommand = ReturnType<typeof prepareConversion>;
export function parseConversionResult(value: unknown) {
  try {
    const v = exact(value, ['conversionId', 'revision', 'status', 'sourcePackages', 'destinationPackages', 'baseUnits', 'destinationBalanceId', 'destinationLotId',
      'movementId', 'valueTransitionId', 'transferredValue']);
    if (v.revision !== '1' || v.status !== 'converted') return bad();
    return freezeSource({ conversionId: number(v.conversionId), revision: '1' as const, status: 'converted' as const, sourcePackages: number(v.sourcePackages),
      destinationPackages: number(v.destinationPackages), baseUnits: number(v.baseUnits), destinationBalanceId: number(v.destinationBalanceId), destinationLotId: number(v.destinationLotId),
      movementId: number(v.movementId), valueTransitionId: number(v.valueTransitionId), transferredValue: money(v.transferredValue) });
  } catch { return bad(); }
}
export type ConversionResult = ReturnType<typeof parseConversionResult>;
export function conversionOutcome(value: unknown, command: ConversionCommand) {
  const result = parseConversionResult(value), preview = command.preview;
  if (result.sourcePackages !== preview.sourcePackages || result.destinationPackages !== preview.destinationPackages || result.baseUnits !== preview.baseUnits ||
    result.transferredValue !== conversionTransferredValue(preview) || result.destinationBalanceId === preview.stockBefore.balanceId || result.destinationLotId === preview.stockBefore.lotId) return bad();
  return result;
}
export function conversionReceipt(value: unknown): Readonly<{ state: 'committed'; result: ConversionResult }> | null {
  const v = exact(value, ['state', 'result']);
  if (v.state === 'not_confirmed') return v.result === null ? null : bad();
  if (v.state !== 'committed') return bad();
  return freezeSource({ state: 'committed' as const, result: parseConversionResult(v.result) });
}
export function parseConversionDetail(value: unknown) {
  const v = exact(value, ['id', 'revision', 'status', 'input', 'source', 'destination', 'receiptReference', 'sourceLotReference', 'stockBefore', 'stockAfter', 'destinationAfter',
    'sourcePackages', 'destinationPackages', 'baseUnits', 'policy', 'transferredValue', 'recordedAt', 'reason']);
  if (v.revision !== '1' || v.status !== 'converted') return bad();
  const input = parseConversionInput(v.input), source = packageChoice(v.source, 'source'), destination = packageChoice(v.destination, 'destination'), q = quantities(input, source, destination, v);
  const stockBefore = stock(v.stockBefore), stockAfter = stock(v.stockAfter), destinationAfter = stock(v.destinationAfter); stockTransition(input, stockBefore, stockAfter, q.sourcePackages);
  if (destinationAfter.poolId !== stockBefore.poolId || destinationAfter.balanceId === stockBefore.balanceId || destinationAfter.lotId === stockBefore.lotId ||
    destinationAfter.onHand !== q.destinationPackages || destinationAfter.reserved !== '0' || destinationAfter.held !== '0' || destinationAfter.expired !== '0' || destinationAfter.revision !== '2') return bad();
  return freezeSource({ id: number(v.id), revision: '1' as const, status: 'converted' as const, input, source, destination, stockBefore, stockAfter, destinationAfter, ...q,
    receiptReference: sourceText(v.receiptReference, 160), sourceLotReference: sourceText(v.sourceLotReference, 160), policy: policy(v.policy),
    transferredValue: money(v.transferredValue), recordedAt: sourceTime(v.recordedAt), reason: sourceText(v.reason, 1000) });
}
export type ConversionDetail = ReturnType<typeof parseConversionDetail>;
export function parseConversionHistory(value: unknown, place: AccessScope, cursor: string | null = null) {
  try {
    const v = exact(value, ['items', 'nextConversionId']); if (!Array.isArray(v.items) || v.items.length > 25) return bad();
    const items = v.items.map(parseConversionDetail), next = v.nextConversionId === null ? null : number(v.nextConversionId);
    if (items.some((item, i) => stockLocationKey(item.input.location) !== stockLocationKey(place) || cursor !== null && BigInt(item.id) >= BigInt(cursor) ||
      i > 0 && BigInt(items[i - 1].id) <= BigInt(item.id)) || next !== null && (items.length !== 25 || next !== items[24].id)) return bad();
    return freezeSource({ items, next });
  } catch { return bad(); }
}
