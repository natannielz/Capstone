import type { components } from '../../api/generated/schema';
import { AccessFailure } from '../administration/workflow';
import { stockPageRequest } from './stock-count-workflow';
import { sourceActor, sourceNumber, sourceText, sourceTime, freezeSource } from './stock-source-workflow';
import { parsePolicyPreview } from './stock-source-policy-workflow';

type Wire = components['schemas'];
export type ActivationExpected = Wire['StockSourceActivationExpectedDto'];
export type ActivationHandoff = Wire['StockSourceActivationHandoffDto'];
export type ActivationPreview = Wire['StockSourceActivationPreviewDto'];
export type ActivationDetail = Wire['StockSourceActivationDetailDto'];
export type ActivationReview = Wire['StockSourceActivationReviewDto'];
export type ActivationResult = Wire['StockSourceActivationCommandDto'];
export type ActivationOperation = 'stock.source.activation.propose' | 'stock.source.activation.decide';
export type ActivationAction = 'approve' | 'reject';
export const activationDomains = ['Orders', 'Reservations', 'Fulfillment', 'Procurement', 'AfterSales', 'SourceReconciliation', 'InventoryOperations'] as const;
const maximum = BigInt('9223372036854775804');
const invalid = (): never => { throw new AccessFailure(502, 'stock_source_activation_response_invalid'); };
const inputInvalid = (): never => { throw new AccessFailure(400, 'stock_source_activation_input_invalid'); };
function row(value: unknown, keys: readonly string[]) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return invalid();
  const record = value as Record<string, unknown>;
  if (Object.keys(record).length !== keys.length || keys.some(key => !Object.hasOwn(record, key))) return invalid();
  return record;
}
const hash = (value: unknown) => typeof value === 'string' && /^[0-9a-f]{64}$(?![\s\S])/.test(value) ? value : invalid();
const optionalId = (value: unknown) => value === null ? null : sourceActor(value);
const state = (value: unknown) => value === 'proposed' || value === 'activated' || value === 'rejected' ? value : invalid();
export function activationTime(value: unknown): string {
  const time = sourceTime(value);
  if (!/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$(?![\s\S])/.test(time) || new Date(time).toISOString() !== time) return invalid();
  return time;
}
export function activationExpected(value: unknown): ActivationExpected {
  const record = row(value, ['poolRevision', 'poolFence', 'suspensionRevision', 'policyId', 'policyRevision', 'activationRevision', 'configurationHash']);
  return freezeSource({ poolRevision: sourceNumber(record.poolRevision, false, maximum), poolFence: sourceNumber(record.poolFence, true, maximum),
    suspensionRevision: sourceNumber(record.suspensionRevision, true, maximum), policyId: sourceActor(record.policyId),
    policyRevision: sourceNumber(record.policyRevision, false, maximum), activationRevision: sourceNumber(record.activationRevision, true, maximum),
    configurationHash: hash(record.configurationHash) });
}
export function activationHandoff(value: unknown): ActivationHandoff {
  const record = row(value, ['reference', 'observedAt', 'physicalPackages', 'carriedValueMinorUnits', 'emptyPoolConfirmed', 'previousWriterStopped', 'noUnrecordedStock']);
  if (record.physicalPackages !== '0' || record.carriedValueMinorUnits !== '0' || record.emptyPoolConfirmed !== true ||
    record.previousWriterStopped !== true || record.noUnrecordedStock !== true) return invalid();
  return freezeSource({ reference: sourceText(record.reference, 160), observedAt: activationTime(record.observedAt), physicalPackages: '0',
    carriedValueMinorUnits: '0', emptyPoolConfirmed: true, previousWriterStopped: true, noUnrecordedStock: true });
}
function configuration(value: unknown, poolId: string, expected: ActivationExpected) {
  const proved = parsePolicyPreview({ physicalPoolId: poolId, poolVersion: expected.poolRevision, suspensionVersion: expected.suspensionRevision,
    policyVersion: expected.policyRevision, configurationHash: expected.configurationHash, configuration: value });
  if (proved.configuration.settings.mode !== 'webLedger') return invalid(); return proved.configuration;
}
function obligations(value: unknown): Wire['StockSourceActivationObligationDto'][] {
  if (!Array.isArray(value) || value.length !== activationDomains.length) return invalid();
  const items = value.map(item => {
    const record = row(item, ['domain', 'producerRevision', 'complete', 'openCount']);
    if (!activationDomains.includes(record.domain as typeof activationDomains[number]) || record.complete !== true || record.openCount !== '0') return invalid();
    return { domain: record.domain as typeof activationDomains[number], producerRevision: sourceNumber(record.producerRevision), complete: true as const, openCount: '0' as const };
  });
  if (new Set(items.map(item => item.domain)).size !== activationDomains.length) return invalid(); return items;
}
export function parseActivationPreview(value: unknown): ActivationPreview {
  try {
    const record = row(value, ['physicalPoolId', 'expected', 'configuration', 'requiredHandoffReference', 'obligations', 'active']);
    const poolId = sourceActor(record.physicalPoolId), expected = activationExpected(record.expected), config = configuration(record.configuration, poolId, expected);
    const reference = sourceText(record.requiredHandoffReference, 160);
    if (record.active !== false || reference !== config.settings.handoffReference) return invalid();
    return freezeSource({ physicalPoolId: poolId, expected, configuration: config, requiredHandoffReference: reference, obligations: obligations(record.obligations), active: false });
  } catch { return invalid(); }
}
export function parseActivationDetail(value: unknown): ActivationDetail {
  try {
    const record = row(value, ['id', 'proposalVersion', 'status', 'physicalPoolId', 'expected', 'configuration', 'handoff', 'obligations', 'proposedAt', 'reason', 'decision', 'decisionId', 'activationId']);
    const id = sourceActor(record.id), poolId = sourceActor(record.physicalPoolId), expected = activationExpected(record.expected), status = state(record.status);
    const config = configuration(record.configuration, poolId, expected), handoff = activationHandoff(record.handoff), proposedAt = activationTime(record.proposedAt);
    const decisionId = optionalId(record.decisionId), activationId = optionalId(record.activationId);
    const decision = record.decision === null ? null : (() => {
      const item = row(record.decision, ['action', 'recordedAt', 'reason', 'handoff']);
      const action = activationAction(item.action), recordedAt = activationTime(item.recordedAt), checked = item.handoff === null ? null : activationHandoff(item.handoff);
      if ((action === 'approve') !== (checked !== null) || (action === 'approve' ? status !== 'activated' : status !== 'rejected') ||
        Date.parse(recordedAt) < Date.parse(proposedAt) || checked && (checked.reference !== handoff.reference ||
        Date.parse(checked.observedAt) < Date.parse(handoff.observedAt) || Date.parse(checked.observedAt) > Date.parse(recordedAt))) return invalid();
      return { action, recordedAt, reason: sourceText(item.reason, 1000), handoff: checked };
    })();
    if (record.proposalVersion !== '1' || handoff.reference !== config.settings.handoffReference || Date.parse(handoff.observedAt) > Date.parse(proposedAt) ||
      (status === 'proposed') !== (decision === null) || (decision === null) !== (decisionId === null) ||
      (status === 'activated') !== (activationId !== null) || activationId !== null && activationId !== id) return invalid();
    return freezeSource({ id, proposalVersion: '1', status, physicalPoolId: poolId, expected, configuration: config, handoff,
      obligations: obligations(record.obligations), proposedAt, reason: sourceText(record.reason, 1000), decision, decisionId, activationId });
  } catch { return invalid(); }
}
export function parseActivationReview(value: unknown): ActivationReview {
  try {
    const record = row(value, ['proposalId', 'physicalPoolId', 'action', 'expected', 'handoff', 'binding', 'proposal']);
    const action = activationAction(record.action), handoff = record.handoff === null ? null : activationHandoff(record.handoff), expected = activationExpected(record.expected);
    const proposal = parseActivationDetail(record.proposal), poolId = sourceActor(record.physicalPoolId), proposalId = sourceActor(record.proposalId);
    if (proposal.status !== 'proposed' || proposal.id !== proposalId || proposal.physicalPoolId !== poolId || (action === 'approve') !== (handoff !== null)) return invalid();
    if (action === 'approve' && (Object.keys(expected).some(key => key !== 'poolFence' && expected[key as keyof ActivationExpected] !== proposal.expected[key as keyof ActivationExpected]) ||
      handoff!.reference !== proposal.handoff.reference || Date.parse(handoff!.observedAt) < Date.parse(proposal.handoff.observedAt))) return invalid();
    return freezeSource({ proposalId, physicalPoolId: poolId, action, expected, handoff, binding: hash(record.binding), proposal });
  } catch { return invalid(); }
}
export function parseActivationHistory(value: unknown, poolId: string, offset = 0, limit = 25): Wire['StockSourceActivationPageDto'] {
  try {
    stockPageRequest(offset, limit); sourceActor(poolId); const record = row(value, ['total', 'items']), total = sourceNumber(record.total, true);
    const remaining = BigInt(total) - BigInt(offset), expected = remaining <= BigInt(0) ? 0 : remaining >= BigInt(limit) ? limit : Number(remaining);
    if (!Array.isArray(record.items) || record.items.length !== expected) return invalid();
    const items = record.items.map(parseActivationDetail);
    if (items.some((item, index) => item.physicalPoolId !== poolId || index > 0 && BigInt(items[index - 1].id) <= BigInt(item.id))) return invalid();
    return freezeSource({ total, items });
  } catch { return invalid(); }
}
export function activationAction(value: unknown): ActivationAction { return value === 'approve' || value === 'reject' ? value : inputInvalid(); }
export function activationOperation(value: unknown): ActivationOperation {
  return value === 'stock.source.activation.propose' || value === 'stock.source.activation.decide' ? value : inputInvalid();
}
export function prepareActivationProposal(actorId: string, preview: unknown, handoff: unknown, reason: string) {
  try {
    const selected = parseActivationPreview(preview), observed = activationHandoff(handoff);
    if (observed.reference !== selected.requiredHandoffReference) return inputInvalid();
    return freezeSource({ actorId: sourceActor(actorId), operation: 'stock.source.activation.propose' as const, preview: selected,
      poolId: selected.physicalPoolId, expectedVersion: selected.expected.poolRevision,
      body: { expected: selected.expected, handoff: observed, reason: sourceText(reason.trim(), 1000) } });
  } catch { return inputInvalid(); }
}
export function prepareActivationDecision(actorId: string, review: unknown, reason: string) {
  try {
    const selected = parseActivationReview(review);
    return freezeSource({ actorId: sourceActor(actorId), operation: 'stock.source.activation.decide' as const, review: selected,
      poolId: selected.physicalPoolId, proposalId: selected.proposalId, expectedVersion: '1' as const,
      body: { expected: selected.expected, handoff: selected.handoff, binding: selected.binding, reason: sourceText(reason.trim(), 1000) } });
  } catch { return inputInvalid(); }
}
export type ActivationCommand = ReturnType<typeof prepareActivationProposal> | ReturnType<typeof prepareActivationDecision>;
export function parseActivationResult(value: unknown): ActivationResult {
  try {
    const record = row(value, ['proposalId', 'proposalVersion', 'status', 'physicalPoolId', 'decisionId', 'activationId', 'recordedAt']);
    const proposalId = sourceActor(record.proposalId), status = state(record.status), decisionId = optionalId(record.decisionId), activationId = optionalId(record.activationId);
    if (record.proposalVersion !== '1' || (status === 'proposed') !== (decisionId === null) || (status === 'activated') !== (activationId !== null) ||
      activationId !== null && activationId !== proposalId) return invalid();
    return freezeSource({ proposalId, proposalVersion: '1', status, physicalPoolId: sourceActor(record.physicalPoolId), decisionId, activationId, recordedAt: activationTime(record.recordedAt) });
  } catch { return invalid(); }
}
export function activationOutcome(value: unknown, command: ActivationCommand) {
  const result = parseActivationResult(value), status = 'proposalId' in command ? command.review.action === 'approve' ? 'activated' : 'rejected' : 'proposed';
  if (result.physicalPoolId !== command.poolId || result.status !== status || 'proposalId' in command && result.proposalId !== command.proposalId) return invalid();
  return result;
}
export function activationReceipt(value: unknown, operation: ActivationOperation) {
  const record = row(value, ['state', 'result']); activationOperation(operation);
  if (record.state === 'not_confirmed') return record.result === null ? null : invalid();
  if (record.state !== 'committed') return invalid(); const result = parseActivationResult(record.result);
  if ((operation === 'stock.source.activation.propose') !== (result.status === 'proposed')) return invalid(); return result;
}
