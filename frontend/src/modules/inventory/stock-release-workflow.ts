import { AccessFailure, stableContent } from '../administration/workflow';
import type { AccessScope } from '../organizations/access-scope';
import { parseStockLocation, stockLocationKey } from './stock-count-workflow';
import { parsePackWarehouses } from './pack-conversion-workflow';
import { priceMoney } from '../catalog/catalog-price-workflow';
import { sourceActor, sourceNumber, sourceText, sourceTime, freezeSource } from './stock-source-workflow';

export const releaseProposeOperation = 'inventory.stock-release.propose';
export const releaseDecideOperation = 'inventory.stock-release.decide';
export const releaseValuePolicy = 'remaining-proportional-minor-unit-floor-v1';
export type ReleaseOperation = typeof releaseProposeOperation | typeof releaseDecideOperation;
export type ReleaseAction = 'approve' | 'reject';
export type ReleaseInput = Readonly<{ location: AccessScope; receiptId: string; qualityAllocationId: string; valuationAllocationId: string; packages: number; policy: string }>;
const bad = (): never => { throw new AccessFailure(502, 'stock_release_response_invalid'); };
const invalid = (): never => { throw new AccessFailure(400, 'stock_release_input_invalid'); };
function exact(value: unknown, keys: readonly string[]): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return bad(); const row = value as Record<string, unknown>;
  if (Object.keys(row).sort().join(',') !== [...keys].sort().join(',')) return bad(); return row;
}
function place(value: unknown) {
  exact(value, ['kind', 'organizationId', 'branchId', 'divisionId', 'shopId', 'warehouseId', 'channelId']); return parseStockLocation(value);
}
function policy(value: unknown): string { return value === releaseValuePolicy ? value : bad(); }
function money(value: unknown): string {
  if (typeof value !== 'string' || !/^(?:0|[1-9][0-9]{0,14})\.[0-9]{2}$(?![\s\S])/u.test(value) || priceMoney(value) !== value) return bad(); return value;
}
const nullableId = (value: unknown) => value === null ? null : sourceActor(value);
function status(value: unknown): 'proposed' | 'approved' | 'rejected' {
  return value === 'proposed' || value === 'approved' || value === 'rejected' ? value : bad();
}
export function releaseOperation(value: unknown): ReleaseOperation {
  return value === releaseProposeOperation || value === releaseDecideOperation ? value : invalid();
}
export function releaseAction(value: unknown): ReleaseAction { return value === 'approve' || value === 'reject' ? value : invalid(); }
export function releaseBinding(value: unknown): string { return typeof value === 'string' && /^[0-9a-f]{64}$(?![\s\S])/u.test(value) ? value : bad(); }
export function releaseQuantity(value: string): number {
  if (!/^[1-9][0-9]{0,6}$(?![\s\S])/u.test(value) || Number(value) > 1000000) return invalid(); return Number(value);
}
export function parseReleaseInput(value: unknown): ReleaseInput {
  try {
    const v = exact(value, ['location', 'receiptId', 'qualityAllocationId', 'valuationAllocationId', 'packages', 'policy']);
    if (typeof v.packages !== 'number' || !Number.isInteger(v.packages) || v.packages < 1 || v.packages > 1000000) return bad();
    return freezeSource({ location: place(v.location), receiptId: sourceActor(v.receiptId), qualityAllocationId: sourceActor(v.qualityAllocationId),
      valuationAllocationId: sourceActor(v.valuationAllocationId), packages: v.packages, policy: policy(v.policy) });
  } catch { return bad(); }
}
export function parseReleaseExpected(value: unknown) {
  const v = exact(value, ['balanceRevision', 'poolRevision', 'poolFence', 'heldValueRevision', 'lotValueRevision']);
  return freezeSource({ balanceRevision: sourceNumber(v.balanceRevision), poolRevision: sourceNumber(v.poolRevision), poolFence: sourceNumber(v.poolFence, true),
    heldValueRevision: sourceNumber(v.heldValueRevision, true), lotValueRevision: sourceNumber(v.lotValueRevision, true) });
}
function selection(value: unknown) {
  const v = exact(value, ['receiptId', 'qualityAllocationId', 'valuationAllocationId', 'acceptedPackages', 'valuedPackages', 'capitalizedValue']);
  const result = { receiptId: sourceActor(v.receiptId), qualityAllocationId: sourceActor(v.qualityAllocationId), valuationAllocationId: sourceActor(v.valuationAllocationId),
    acceptedPackages: sourceNumber(v.acceptedPackages), valuedPackages: sourceNumber(v.valuedPackages), capitalizedValue: money(v.capitalizedValue) };
  return freezeSource(result);
}
function stock(value: unknown) {
  const v = exact(value, ['balanceId', 'poolId', 'skuId', 'definitionId', 'lotId', 'revision', 'onHandPackages', 'reservedPackages', 'heldPackages', 'expiredPackages', 'availablePackages']);
  const result = { balanceId: sourceActor(v.balanceId), poolId: sourceActor(v.poolId), skuId: sourceActor(v.skuId), definitionId: sourceActor(v.definitionId), lotId: sourceActor(v.lotId),
    revision: sourceNumber(v.revision), onHandPackages: sourceNumber(v.onHandPackages, true), reservedPackages: sourceNumber(v.reservedPackages, true),
    heldPackages: sourceNumber(v.heldPackages, true), expiredPackages: sourceNumber(v.expiredPackages, true), availablePackages: sourceNumber(v.availablePackages, true) };
  if (BigInt(result.onHandPackages) - BigInt(result.reservedPackages) - BigInt(result.heldPackages) - BigInt(result.expiredPackages) !== BigInt(result.availablePackages)) return bad();
  return freezeSource(result);
}
function facts(input: ReleaseInput, chosen: ReturnType<typeof selection>, before: ReturnType<typeof stock>, after: ReturnType<typeof stock>, apply = true) {
  if (chosen.receiptId !== input.receiptId || chosen.qualityAllocationId !== input.qualityAllocationId || chosen.valuationAllocationId !== input.valuationAllocationId ||
    BigInt(input.packages) > BigInt(chosen.valuedPackages) || BigInt(input.packages) > BigInt(chosen.acceptedPackages) || ['balanceId', 'poolId', 'skuId', 'definitionId', 'lotId', 'onHandPackages', 'reservedPackages', 'expiredPackages']
      .some(key => before[key as keyof typeof before] !== after[key as keyof typeof after])) return bad();
  if (apply ? BigInt(after.heldPackages) !== BigInt(before.heldPackages) - BigInt(input.packages) || BigInt(after.availablePackages) !== BigInt(before.availablePackages) + BigInt(input.packages) ||
    BigInt(after.revision) !== BigInt(before.revision) + BigInt(1) : stableContent(before) !== stableContent(after)) return bad();
}
export const parseReleaseWarehouses = parsePackWarehouses;
export function parseReleaseSources(value: unknown) {
  try {
    const v = exact(value, ['items', 'unavailableCount']); if (!Array.isArray(v.items) || v.items.length > 25) return bad();
    const items = v.items.map(value => {
      const row = exact(value, ['receiptId', 'qualityAllocationId', 'valuationAllocationId', 'receiptReference', 'productName', 'packagingLabel', 'lotReference',
        'expiresOn', 'acceptedPackages', 'valuedPackages', 'releaseablePackages', 'capitalizedValue', 'policy']);
      const expiresOn = row.expiresOn === null ? null : sourceText(row.expiresOn, 10);
      if (expiresOn !== null && !/^\d{4}-\d\d-\d\d$(?![\s\S])/u.test(expiresOn)) return bad();
      if (expiresOn !== null) sourceTime(expiresOn + 'T00:00:00Z');
      const chosen = selection({ receiptId: row.receiptId, qualityAllocationId: row.qualityAllocationId, valuationAllocationId: row.valuationAllocationId,
        acceptedPackages: row.acceptedPackages, valuedPackages: row.valuedPackages, capitalizedValue: row.capitalizedValue });
      const releaseablePackages = sourceNumber(row.releaseablePackages);
      if (BigInt(releaseablePackages) > BigInt(chosen.valuedPackages) || BigInt(releaseablePackages) > BigInt(chosen.acceptedPackages)) return bad();
      return { ...chosen, receiptReference: sourceText(row.receiptReference, 160), productName: sourceText(row.productName, 160), packagingLabel: sourceText(row.packagingLabel, 160),
        lotReference: sourceText(row.lotReference, 160), expiresOn, releaseablePackages, policy: policy(row.policy) };
    });
    if (new Set(items.map(releaseChoiceKey)).size !== items.length) return bad();
    return freezeSource({ items, unavailableCount: sourceNumber(v.unavailableCount, true) });
  } catch { return bad(); }
}
export type ReleaseChoice = ReturnType<typeof parseReleaseSources>['items'][number];
export function releaseChoiceKey(choice: Pick<ReleaseChoice, 'receiptId' | 'qualityAllocationId' | 'valuationAllocationId'>) {
  return choice.receiptId + ':' + choice.qualityAllocationId + ':' + choice.valuationAllocationId;
}
export function prepareReleaseInput(location: AccessScope, choice: ReleaseChoice, packages: string): ReleaseInput {
  try {
    const copied = parseReleaseSources({ items: [choice], unavailableCount: '0' }).items[0], quantity = releaseQuantity(packages);
    if (BigInt(quantity) > BigInt(copied.releaseablePackages)) return invalid();
    return parseReleaseInput({ location, receiptId: copied.receiptId, qualityAllocationId: copied.qualityAllocationId, valuationAllocationId: copied.valuationAllocationId,
      packages: quantity, policy: copied.policy });
  } catch { return invalid(); }
}
export function parseReleasePreview(value: unknown, requested?: ReleaseInput) {
  try {
    const v = exact(value, ['input', 'expected', 'selection', 'stockBefore', 'stockAfter', 'binding', 'observedAt']);
    const input = parseReleaseInput(v.input), expected = parseReleaseExpected(v.expected), chosen = selection(v.selection), before = stock(v.stockBefore), after = stock(v.stockAfter);
    facts(input, chosen, before, after);
    if (before.revision !== expected.balanceRevision || requested && stableContent(input) !== stableContent(requested)) return bad();
    return freezeSource({ input, expected, selection: chosen, stockBefore: before, stockAfter: after, binding: releaseBinding(v.binding), observedAt: sourceTime(v.observedAt) });
  } catch { return bad(); }
}
export type ReleasePreview = ReturnType<typeof parseReleasePreview>;
function proposal(value: unknown) {
  const v = exact(value, ['id', 'revision', 'input', 'expected', 'selection', 'stockBefore', 'stockAfter', 'proposedAt', 'reason']);
  const preview = parseReleasePreview({ input: v.input, expected: v.expected, selection: v.selection, stockBefore: v.stockBefore, stockAfter: v.stockAfter,
    binding: '0'.repeat(64), observedAt: v.proposedAt });
  if (v.revision !== '1') return bad();
  return freezeSource({ id: sourceActor(v.id), revision: '1' as const, input: preview.input, expected: preview.expected, selection: preview.selection,
    stockBefore: preview.stockBefore, stockAfter: preview.stockAfter, proposedAt: sourceTime(v.proposedAt), reason: sourceText(v.reason, 1000) });
}
export function parseReleaseDetail(value: unknown) {
  try {
    const v = exact(value, ['proposal', 'decision', 'status']), item = proposal(v.proposal), state = status(v.status);
    const decision = v.decision === null ? null : (() => {
      const d = exact(v.decision, ['id', 'action', 'decidedAt', 'reason', 'movementId', 'valueTransitionId', 'transferredValue']), action = releaseAction(d.action);
      const result = parseReleaseResult({ proposalId: item.id, revision: '1', status: action === 'approve' ? 'approved' : 'rejected', decisionId: d.id,
        movementId: d.movementId, valueTransitionId: d.valueTransitionId, transferredValue: d.transferredValue }, releaseDecideOperation);
      const decidedAt = sourceTime(d.decidedAt); if (Date.parse(decidedAt) < Date.parse(item.proposedAt)) return bad();
      return { id: result.decisionId!, action, decidedAt, reason: sourceText(d.reason, 1000), movementId: result.movementId,
        valueTransitionId: result.valueTransitionId, transferredValue: result.transferredValue };
    })();
    if ((state === 'proposed') !== (decision === null) || decision && state !== (decision.action === 'approve' ? 'approved' : 'rejected')) return bad();
    return freezeSource({ proposal: item, decision, status: state });
  } catch { return bad(); }
}
export type ReleaseDetail = ReturnType<typeof parseReleaseDetail>;
export function parseReleaseReview(value: unknown, detail: ReleaseDetail, requestedAction: ReleaseAction) {
  try {
    const v = exact(value, ['proposal', 'expected', 'selection', 'stockBefore', 'stockAfter', 'policy', 'action', 'binding', 'observedAt']);
    const item = proposal(v.proposal), expected = parseReleaseExpected(v.expected), chosen = selection(v.selection), before = stock(v.stockBefore), after = stock(v.stockAfter);
    const action = releaseAction(v.action);
    if (detail.status !== 'proposed' || action !== requestedAction || stableContent(item) !== stableContent(detail.proposal) ||
      policy(v.policy) !== item.input.policy || before.revision !== expected.balanceRevision) return bad();
    facts(item.input, chosen, before, after, action === 'approve');
    return freezeSource({ proposal: item, expected, selection: chosen, stockBefore: before, stockAfter: after, policy: item.input.policy,
      action, binding: releaseBinding(v.binding), observedAt: sourceTime(v.observedAt) });
  } catch { return bad(); }
}
export type ReleaseReview = ReturnType<typeof parseReleaseReview>;
export function parseReleaseHistory(value: unknown, cursor: string | null = null) {
  try {
    const v = exact(value, ['items', 'nextAfter']); if (!Array.isArray(v.items) || v.items.length > 25) return bad();
    const items = v.items.map(value => {
      const row = exact(value, ['proposalId', 'status', 'receiptReference', 'productName', 'packagingLabel', 'lotReference', 'packages', 'policy', 'recordedAt', 'decidedAt']);
      const state = status(row.status), recordedAt = sourceTime(row.recordedAt), decidedAt = row.decidedAt === null ? null : sourceTime(row.decidedAt);
      if ((state === 'proposed') !== (decidedAt === null) || decidedAt && Date.parse(decidedAt) < Date.parse(recordedAt)) return bad();
      return { proposalId: sourceActor(row.proposalId), status: state, receiptReference: sourceText(row.receiptReference, 160), productName: sourceText(row.productName, 160),
        packagingLabel: sourceText(row.packagingLabel, 160), lotReference: sourceText(row.lotReference, 160), packages: sourceNumber(row.packages), policy: policy(row.policy), recordedAt, decidedAt };
    });
    const nextAfter = nullableId(v.nextAfter), previous = cursor === null ? null : sourceActor(cursor);
    if (items.some((item, index) => previous !== null && BigInt(item.proposalId) >= BigInt(previous) || index > 0 && BigInt(items[index - 1].proposalId) <= BigInt(item.proposalId)) ||
      nextAfter !== null && (items.length !== 25 || nextAfter !== items[24].proposalId)) return bad();
    return freezeSource({ items, nextAfter });
  } catch { return bad(); }
}
export function prepareReleaseProposal(actorId: string, preview: ReleasePreview, reason: string) {
  try {
    const selected = parseReleasePreview(preview);
    return freezeSource({ actorId: sourceActor(actorId), operation: releaseProposeOperation as typeof releaseProposeOperation, preview: selected,
      revision: selected.expected.balanceRevision, body: { input: selected.input, expected: selected.expected, binding: selected.binding, reason: sourceText(reason.trim(), 1000) } });
  } catch { return invalid(); }
}
export function prepareReleaseDecision(actorId: string, preview: ReleaseReview, reason: string) {
  try {
    const selected = parseReleaseReview(preview, { proposal: preview.proposal, decision: null, status: 'proposed' }, preview.action);
    return freezeSource({ actorId: sourceActor(actorId), operation: releaseDecideOperation as typeof releaseDecideOperation, preview: selected, proposalId: selected.proposal.id,
      action: selected.action, revision: '1' as const, body: { policy: selected.policy, expected: selected.expected, binding: selected.binding, reason: sourceText(reason.trim(), 1000) } });
  } catch { return invalid(); }
}
export type ReleaseCommand = ReturnType<typeof prepareReleaseProposal> | ReturnType<typeof prepareReleaseDecision>;
export function parseReleaseResult(value: unknown, operation?: ReleaseOperation) {
  try {
    const v = exact(value, ['proposalId', 'revision', 'status', 'decisionId', 'movementId', 'valueTransitionId', 'transferredValue']);
    const state = status(v.status), decisionId = nullableId(v.decisionId), movementId = nullableId(v.movementId), valueTransitionId = nullableId(v.valueTransitionId);
    const transferredValue = v.transferredValue === null ? null : money(v.transferredValue);
    if (v.revision !== '1' || (state === 'proposed') !== (decisionId === null) || (state === 'approved') !== (movementId !== null) ||
      (state === 'approved') !== (valueTransitionId !== null) || (state === 'approved') !== (transferredValue !== null) ||
      operation !== undefined && (releaseOperation(operation) === releaseProposeOperation) !== (state === 'proposed')) return bad();
    return freezeSource({ proposalId: sourceActor(v.proposalId), revision: '1' as const, status: state, decisionId, movementId, valueTransitionId, transferredValue });
  } catch { return bad(); }
}
export type ReleaseResult = ReturnType<typeof parseReleaseResult>;
export function releaseOutcome(value: unknown, command: ReleaseCommand): ReleaseResult {
  const result = parseReleaseResult(value, command.operation);
  if ('proposalId' in command && (result.proposalId !== command.proposalId || result.status !== (command.action === 'approve' ? 'approved' : 'rejected'))) return bad(); return result;
}
export function releaseReceipt(value: unknown, operation: ReleaseOperation): { state: 'committed'; result: ReleaseResult } | null {
  const v = exact(value, ['state', 'result']); releaseOperation(operation);
  if (v.state === 'not_confirmed') return v.result === null ? null : bad();
  return v.state === 'committed' ? freezeSource({ state: 'committed' as const, result: parseReleaseResult(v.result, operation) }) : bad();
}
export const releaseLocationKey = stockLocationKey;
