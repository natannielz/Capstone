import { AccessFailure, stableContent } from '../administration/workflow';
import type { AccessScope } from '../organizations/access-scope';
import type { components } from '../../api/generated/schema';
import { parseStockLocation, parseStockWarehouses, parseStockPackages, stockLocationKey, stockPageRequest, stockCountSearch, type StockPackageChoice } from './stock-count-workflow';
import { sourceActor, sourceKey, sourceNumber, sourceText, sourceTime, freezeSource } from './stock-source-workflow';

export const packProposeOperation = 'inventory.pack-conversion.rule.propose';
export const packDecideOperation = 'inventory.pack-conversion.rule.decide';
export type PackOperation = typeof packProposeOperation | typeof packDecideOperation;
export type PackAction = 'approve' | 'reject';
export type PackRole = 'source' | 'destination';
export type PackInput = Readonly<Omit<components['schemas']['PackConversionRuleInputDto'], 'location'> & { location: AccessScope }>;
const bad = (): never => { throw new AccessFailure(502, 'pack_conversion_response_invalid'); };
const invalid = (): never => { throw new AccessFailure(400, 'pack_conversion_input_invalid'); };
function exact(value: unknown, keys: readonly string[]): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return bad();
  const row = value as Record<string, unknown>;
  if (Object.keys(row).sort().join(',') !== [...keys].sort().join(',')) return bad();
  return row;
}
function place(value: unknown): AccessScope {
  exact(value, ['kind', 'organizationId', 'branchId', 'divisionId', 'shopId', 'warehouseId', 'channelId']);
  return parseStockLocation(value);
}
export function parsePackWarehouses(value: unknown) {
  try {
    const v = exact(value, ['items']);
    if (!Array.isArray(v.items)) return bad();
    for (const item of v.items) {
      const row = exact(item, ['location', 'organizationName', 'shopName', 'warehouseName', 'channelName']); place(row.location);
    }
    return freezeSource(parseStockWarehouses(value));
  } catch { return bad(); }
}
export function packSearch(value: string): string {
  const search = stockCountSearch(value); return search.length <= 100 ? search : invalid();
}
export function packBinding(value: unknown): string {
  return typeof value === 'string' && /^[0-9a-f]{64}$(?![\s\S])/.test(value) ? value : bad();
}
export function packOperation(value: unknown): PackOperation {
  if (value !== packProposeOperation && value !== packDecideOperation) return invalid();
  return value;
}
export function packAction(value: unknown): PackAction {
  if (value !== 'approve' && value !== 'reject') return invalid();
  return value;
}
function ratioNumber(value: unknown): number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 1 && value <= 1000000 ? value : bad();
}
export function packRatio(value: string): number {
  if (!/^[1-9][0-9]{0,6}$(?![\s\S])/.test(value) || Number(value) > 1000000) return invalid();
  return Number(value);
}
export function parsePackInput(value: unknown): PackInput {
  try {
    const v = exact(value, ['location', 'sourceSkuId', 'sourceSkuRevision', 'sourceDefinitionId', 'destinationSkuId',
      'destinationSkuRevision', 'destinationDefinitionId', 'sourcePackagesPerStep', 'destinationPackagesPerStep']);
    const result = { location: place(v.location), sourceSkuId: sourceActor(v.sourceSkuId), sourceSkuRevision: sourceNumber(v.sourceSkuRevision),
      sourceDefinitionId: sourceActor(v.sourceDefinitionId), destinationSkuId: sourceActor(v.destinationSkuId),
      destinationSkuRevision: sourceNumber(v.destinationSkuRevision), destinationDefinitionId: sourceActor(v.destinationDefinitionId),
      sourcePackagesPerStep: ratioNumber(v.sourcePackagesPerStep), destinationPackagesPerStep: ratioNumber(v.destinationPackagesPerStep) };
    if (result.sourceSkuId === result.destinationSkuId || result.sourceDefinitionId === result.destinationDefinitionId) return bad();
    return freezeSource(result);
  } catch { return bad(); }
}
function choice(value: unknown, role: PackRole): StockPackageChoice {
  exact(value, ['skuId', 'skuCode', 'skuRevision', 'definitionId', 'definitionVersion', 'familyName', 'packageLabel',
    'packageKind', 'baseUnitCode', 'baseUnitsPerPackage']);
  const item = parseStockPackages({ total: '1', items: [value] }).items[0];
  if (item.packageKind !== (role === 'source' ? 'sealed' : 'loose')) return bad();
  return freezeSource(item);
}
export function parsePackPackages(value: unknown, role: PackRole, offset = 0, limit = 25) {
  try {
    if (role !== 'source' && role !== 'destination') return bad();
    const v = exact(value, ['total', 'items']); stockPageRequest(offset, limit);
    if (!Array.isArray(v.items)) return bad();
    const parsed = parseStockPackages(value, offset, limit);
    return freezeSource({ total: parsed.total, items: v.items.map(item => choice(item, role)) });
  } catch { return bad(); }
}
function facts(input: PackInput, source: StockPackageChoice, destination: StockPackageChoice) {
  if (source.skuId !== input.sourceSkuId || source.skuRevision !== input.sourceSkuRevision || source.definitionId !== input.sourceDefinitionId ||
    destination.skuId !== input.destinationSkuId || destination.skuRevision !== input.destinationSkuRevision || destination.definitionId !== input.destinationDefinitionId ||
    source.packageKind !== 'sealed' || destination.packageKind !== 'loose' || source.baseUnitCode !== destination.baseUnitCode ||
    BigInt(input.sourcePackagesPerStep) * BigInt(source.baseUnitsPerPackage) !== BigInt(input.destinationPackagesPerStep) * BigInt(destination.baseUnitsPerPackage)) return bad();
}
export function preparePackInput(location: AccessScope, source: StockPackageChoice, destination: StockPackageChoice, sourceRatio: string, destinationRatio: string): PackInput {
  try {
    const from = choice(source, 'source'), to = choice(destination, 'destination');
    const input = parsePackInput({ location, sourceSkuId: from.skuId, sourceSkuRevision: from.skuRevision, sourceDefinitionId: from.definitionId,
      destinationSkuId: to.skuId, destinationSkuRevision: to.skuRevision, destinationDefinitionId: to.definitionId,
      sourcePackagesPerStep: packRatio(sourceRatio), destinationPackagesPerStep: packRatio(destinationRatio) });
    facts(input, from, to); return input;
  } catch { return invalid(); }
}
export function parsePackPreview(value: unknown, expected?: PackInput) {
  try {
    const v = exact(value, ['input', 'source', 'destination', 'binding', 'sourcePackages', 'destinationPackages', 'baseUnits', 'physicalConversionAvailable', 'observedAt']);
    const input = parsePackInput(v.input), source = choice(v.source, 'source'), destination = choice(v.destination, 'destination'); facts(input, source, destination);
    const sourcePackages = sourceNumber(v.sourcePackages), destinationPackages = sourceNumber(v.destinationPackages), baseUnits = sourceNumber(v.baseUnits);
    if (v.physicalConversionAvailable !== false || sourcePackages !== String(input.sourcePackagesPerStep) || destinationPackages !== String(input.destinationPackagesPerStep) ||
      BigInt(baseUnits) !== BigInt(sourcePackages) * BigInt(source.baseUnitsPerPackage) || expected && stableContent(input) !== stableContent(expected)) return bad();
    return freezeSource({ input, source, destination, binding: packBinding(v.binding), sourcePackages, destinationPackages, baseUnits,
      physicalConversionAvailable: false as const, observedAt: sourceTime(v.observedAt) });
  } catch { return bad(); }
}
export type PackPreview = ReturnType<typeof parsePackPreview>;
export function parsePackProposal(value: unknown) {
  try {
    const v = exact(value, ['id', 'revision', 'status', 'input', 'source', 'destination', 'proposedAt', 'reason', 'decision', 'physicalConversionAvailable', 'canReview']);
    const input = parsePackInput(v.input), source = choice(v.source, 'source'), destination = choice(v.destination, 'destination'); facts(input, source, destination);
    const status = v.status;
    if (v.revision !== '1' || v.physicalConversionAvailable !== false || status !== 'pending' && status !== 'approved' && status !== 'rejected' ||
      typeof v.canReview !== 'boolean') return bad();
    const proposedAt = sourceTime(v.proposedAt);
    const decision = v.decision === null ? null : (() => {
      const d = exact(v.decision, ['id', 'action', 'decidedAt', 'reason']), action = packAction(d.action), decidedAt = sourceTime(d.decidedAt);
      if (status !== (action === 'approve' ? 'approved' : 'rejected') || Date.parse(decidedAt) < Date.parse(proposedAt)) return bad();
      return { id: sourceActor(d.id), action, decidedAt, reason: sourceText(d.reason, 1000) };
    })();
    if ((status === 'pending') !== (decision === null) || status !== 'pending' && v.canReview === true) return bad();
    return freezeSource({ id: sourceActor(v.id), revision: '1' as const, status: status as 'pending' | 'approved' | 'rejected', input, source, destination, proposedAt,
      reason: sourceText(v.reason, 1000), decision, physicalConversionAvailable: false as const,
      canReview: v.canReview });
  } catch { return bad(); }
}
export type PackProposal = ReturnType<typeof parsePackProposal>;
export function parsePackDecisionPreview(value: unknown, expected: PackProposal, action: PackAction) {
  try {
    const v = exact(value, ['proposal', 'action', 'binding', 'physicalConversionAvailable']), proposal = parsePackProposal(v.proposal);
    if (v.action !== packAction(action) || v.physicalConversionAvailable !== false || proposal.status !== 'pending' ||
      proposal.canReview === false || stableContent(proposal) !== stableContent(parsePackProposal(expected))) return bad();
    return freezeSource({ proposal, action, binding: packBinding(v.binding), physicalConversionAvailable: false as const });
  } catch { return bad(); }
}
export type PackDecisionPreview = ReturnType<typeof parsePackDecisionPreview>;
export function parsePackRule(value: unknown) {
  try {
    const v = exact(value, ['id', 'version', 'proposalId', 'decisionId', 'input', 'source', 'destination', 'approvedAt', 'physicalConversionAvailable']);
    const input = parsePackInput(v.input), source = choice(v.source, 'source'), destination = choice(v.destination, 'destination'); facts(input, source, destination);
    const id = sourceActor(v.id), proposalId = sourceActor(v.proposalId);
    if (id !== proposalId || v.version !== '1' || v.physicalConversionAvailable !== false) return bad();
    return freezeSource({ id, version: '1' as const, proposalId, decisionId: sourceActor(v.decisionId), input, source, destination,
      approvedAt: sourceTime(v.approvedAt), physicalConversionAvailable: false as const });
  } catch { return bad(); }
}
export type PackRule = ReturnType<typeof parsePackRule>;
function cursorPage<T extends { id: string; input: PackInput }>(value: unknown, location: AccessScope, cursor: string | null,
  cursorName: string, parser: (item: unknown) => T) {
  const v = exact(value, ['items', cursorName, 'physicalConversionAvailable']);
  if (!Array.isArray(v.items) || v.items.length > 25 || v.physicalConversionAvailable !== false) return bad();
  const previous = cursor === null ? null : sourceActor(cursor), next = v[cursorName] === null ? null : sourceActor(v[cursorName]);
  const items = v.items.map(parser);
  if (items.some((item, index) => stockLocationKey(item.input.location) !== stockLocationKey(location) || previous !== null && BigInt(item.id) >= BigInt(previous) ||
    index > 0 && BigInt(items[index - 1].id) <= BigInt(item.id)) || next !== null && (items.length !== 25 || next !== items[24].id)) return bad();
  return freezeSource({ items, next });
}
export const parsePackProposals = (value: unknown, location: AccessScope, cursor: string | null = null) => cursorPage(value, location, cursor, 'nextProposalId', parsePackProposal);
export const parsePackRules = (value: unknown, location: AccessScope, cursor: string | null = null) => cursorPage(value, location, cursor, 'nextRuleId', parsePackRule);
export function preparePackProposal(actorId: string, preview: PackPreview, reason: string) {
  try {
    const reviewed = parsePackPreview(preview);
    return freezeSource({ actorId: sourceActor(actorId), operation: packProposeOperation as typeof packProposeOperation, preview: reviewed,
      body: { input: reviewed.input, binding: reviewed.binding, reason: sourceText(reason.trim(), 1000) } });
  } catch { return invalid(); }
}
export function preparePackDecision(actorId: string, preview: PackDecisionPreview, reason: string) {
  try {
    const reviewed = parsePackDecisionPreview(preview, preview.proposal, preview.action);
    return freezeSource({ actorId: sourceActor(actorId), operation: packDecideOperation as typeof packDecideOperation, preview: reviewed, proposalId: reviewed.proposal.id,
      expectedRevision: reviewed.proposal.revision, action: reviewed.action, body: { binding: reviewed.binding, reason: sourceText(reason.trim(), 1000) } });
  } catch { return invalid(); }
}
export type PackCommand = ReturnType<typeof preparePackProposal> | ReturnType<typeof preparePackDecision>;
export function parsePackResult(value: unknown, operation?: PackOperation) {
  try {
    const v = exact(value, ['proposalId', 'revision', 'state', 'decisionId', 'ruleId', 'physicalConversionAvailable']);
    const state = v.state, proposalId = sourceActor(v.proposalId);
    const decisionId = v.decisionId === null ? null : sourceActor(v.decisionId), ruleId = v.ruleId === null ? null : sourceActor(v.ruleId);
    if (v.revision !== '1' || v.physicalConversionAvailable !== false || state !== 'pending' && state !== 'approve' && state !== 'reject' ||
      (state === 'pending') !== (decisionId === null) || (state === 'approve') !== (ruleId !== null) || ruleId !== null && ruleId !== proposalId ||
      operation !== undefined && ((packOperation(operation) === packProposeOperation) !== (state === 'pending'))) return bad();
    return freezeSource({ proposalId, revision: '1' as const, state, decisionId, ruleId, physicalConversionAvailable: false as const });
  } catch { return bad(); }
}
export type PackResult = ReturnType<typeof parsePackResult>;
export function packOutcome(value: unknown, command: PackCommand): PackResult {
  const result = parsePackResult(value, command.operation);
  if ('proposalId' in command && (result.proposalId !== command.proposalId || result.state !== command.action)) return bad();
  return result;
}
export function packReceipt(value: unknown, operation: PackOperation): { state: 'committed'; result: PackResult } | null {
  const v = exact(value, ['state', 'result']); packOperation(operation);
  if (v.state === 'not_confirmed') return v.result === null ? null : bad();
  if (v.state !== 'committed') return bad();
  return freezeSource({ state: 'committed' as const, result: parsePackResult(v.result, operation) });
}
export { sourceActor as packActor, sourceKey as packKey };
