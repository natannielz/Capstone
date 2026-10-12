import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { parseCatalogDefinition } from '../catalog/catalog-workflow';
import { parsePrice, priceId } from '../catalog/catalog-price-workflow';
import type { OrderSelection } from './order-quote-client';
import { orderLineRevision } from './order-line-identity';

const invalid = (): never => { throw new AccessFailure(502, 'substitution_response_invalid'); };
const record = (value: unknown): Record<string, unknown> => value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : invalid();
const text = (value: unknown, max = 200) => typeof value === 'string' && value.trim() === value && value.length > 0 && value.length <= max && !/[\u0000-\u001f\u007f]/.test(value) ? value : invalid();
const number = (value: unknown, max = 1_000_000) => typeof value === 'number' && Number.isInteger(value) && value > 0 && value <= max ? value : invalid();
const money = (value: unknown) => typeof value === 'string' && /^(0|[1-9][0-9]{0,14})\.[0-9]{2}$(?![\s\S])/.test(value) ? value : invalid();
const cents = (value: string) => BigInt(value.replace('.', ''));
const time = (value: unknown) => typeof value === 'string' && value.length <= 64 && Number.isFinite(Date.parse(value)) ? value : invalid();
const hash = (value: unknown) => typeof value === 'string' && /^[a-f0-9]{64}$(?![\s\S])/.test(value) ? value : invalid();
const revision = (value: unknown) => { const result = priceId(value); return BigInt(result) <= BigInt(103) ? result : invalid(); };
const sequence = (value: unknown) => value === '0' ? value : priceId(value);
export type SubstitutionDecision = 'Accept' | 'Decline';
export type SubstitutionOperation = 'propose' | 'accept' | 'decline';
export function substitutionOperation(value: string): SubstitutionOperation {
  if (value === 'ordering.substitution.propose') return 'propose';
  if (value === 'ordering.substitution.accept') return 'accept';
  if (value === 'ordering.substitution.decline') return 'decline';
  return invalid();
}
function line(value: unknown) {
  const row = record(value), definition = parseCatalogDefinition(row.definition), price = parsePrice(row.price), packages = number(row.packages), baseUnits = priceId(row.baseUnits), subtotal = money(row.catalogSubtotal);
  if (definition.skuId !== price.skuId || BigInt(baseUnits) !== BigInt(packages) * BigInt(definition.baseUnitsPerPackage) || cents(subtotal) !== BigInt(packages) * cents(price.unitPrice)) return invalid();
  return { familyId: priceId(row.familyId), familyName: text(row.familyName, 160), skuCode: text(row.skuCode, 64), definition, price, packages, baseUnits, catalogSubtotal: subtotal, lineRevision: orderLineRevision(row.lineRevision) };
}
export type SubstitutionLine = ReturnType<typeof line>;
export const substitutionLineKey = (value: SubstitutionLine) => `${value.definition.skuId}:${value.lineRevision}`;
export function replacementSelection(value: SubstitutionLine, packages: number): OrderSelection {
  return { skuId: value.definition.skuId, definitionId: value.definition.id, definitionVersion: value.definition.version, priceId: value.price.id, priceVersion: value.price.version, packages: number(packages) };
}
export function parseSubstitutionInput(value: unknown) {
  const row = record(value), choice = record(row.replacement);
  const replacement: OrderSelection = { skuId: priceId(choice.skuId), definitionId: priceId(choice.definitionId), definitionVersion: priceId(choice.definitionVersion), priceId: priceId(choice.priceId), priceVersion: priceId(choice.priceVersion), packages: number(choice.packages) };
  const originalSkuId = priceId(row.originalSkuId);
  return { orderId: priceId(row.orderId), suborderId: priceId(row.suborderId), expectedOrderRevision: revision(row.expectedOrderRevision), expectedDispatchSequence: sequence(row.expectedDispatchSequence), originalSkuId, originalLineRevision: orderLineRevision(row.originalLineRevision), originalPackages: number(row.originalPackages), replacement };
}
export type SubstitutionInput = ReturnType<typeof parseSubstitutionInput>;
const equalInput = (left: SubstitutionInput, right: SubstitutionInput) => JSON.stringify(left) === JSON.stringify(right);
function selected(value: SubstitutionLine, expected: OrderSelection) {
  return value.lineRevision === '0' && value.definition.skuId === expected.skuId && value.definition.id === expected.definitionId && value.definition.version === expected.definitionVersion && value.price.id === expected.priceId && value.price.version === expected.priceVersion && value.packages === expected.packages;
}
export function parseSubstitutionTarget(value: unknown) {
  const row = record(value); if (!Array.isArray(row.lines) || row.lines.length < 1 || row.lines.length > 100 || typeof row.canPropose !== 'boolean') return invalid();
  const lines = row.lines.map(value => { const item = record(value), catalog = line(item.line); const remaining = item.remainingPackages === 0 ? 0 : number(item.remainingPackages);
    if (remaining > catalog.packages) return invalid(); return { line: catalog, remainingPackages: remaining }; });
  if (new Set(lines.map(value => substitutionLineKey(value.line))).size !== lines.length) return invalid();
  return { orderId: priceId(row.orderId), suborderId: priceId(row.suborderId), revision: revision(row.revision), dispatchSequence: sequence(row.dispatchSequence), buyerName: text(row.buyerName), lines, canPropose: row.canPropose };
}
export type SubstitutionTarget = ReturnType<typeof parseSubstitutionTarget>;
export function parseSubstitutionProposalReview(value: unknown, expected: SubstitutionInput) {
  const row = record(value), input = parseSubstitutionInput(row.input), original = line(row.original), replacement = line(row.replacement), remainingPackages = number(row.remainingPackages);
  if (!equalInput(input, parseSubstitutionInput(expected)) || original.definition.skuId !== input.originalSkuId || original.lineRevision !== input.originalLineRevision || !selected(replacement, input.replacement) || input.originalPackages > remainingPackages || remainingPackages > original.packages || original.price.currency !== replacement.price.currency) return invalid();
  return { input, review: hash(row.review), buyerName: text(row.buyerName), original, replacement, remainingPackages, calculatedAt: time(row.calculatedAt) };
}
export type SubstitutionProposalReview = ReturnType<typeof parseSubstitutionProposalReview>;
export function parseSubstitutionHistory(value: unknown) {
  const row = record(value), input = parseSubstitutionInput(row.input), original = line(row.original), replacement = line(row.replacement);
  const orderId = priceId(row.orderId), suborderId = priceId(row.suborderId), proposalId = priceId(row.proposalId);
  if (orderId !== input.orderId || suborderId !== input.suborderId || original.definition.skuId !== input.originalSkuId || original.lineRevision !== input.originalLineRevision || !selected(replacement, input.replacement) || !['pending', 'stale', 'accepted', 'declined'].includes(String(row.status))) return invalid();
  const status = row.status as 'pending' | 'stale' | 'accepted' | 'declined', decisionId = row.decisionId === null ? null : priceId(row.decisionId), decidedAt = row.decidedAt === null ? null : time(row.decidedAt), proposedAt = time(row.proposedAt);
  if ((status === 'accepted' || status === 'declined') !== (decisionId !== null && decidedAt !== null) || (decisionId === null) !== (decidedAt === null) || decidedAt !== null && Date.parse(decidedAt) < Date.parse(proposedAt)) return invalid();
  return { proposalId, orderId, suborderId, buyerName: text(row.buyerName), input, original, replacement, status, decisionId, proposedAt, decidedAt };
}
export type SubstitutionHistory = ReturnType<typeof parseSubstitutionHistory>;
export function parseSubstitutionDecisionReview(value: unknown, proposal: SubstitutionHistory, decision: SubstitutionDecision) {
  const row = record(value), original = line(row.original), replacement = line(row.replacement);
  if (priceId(row.proposalId) !== proposal.proposalId || priceId(row.orderId) !== proposal.orderId || priceId(row.suborderId) !== proposal.suborderId || row.decision !== decision || row.originalPackages !== proposal.input.originalPackages ||
    JSON.stringify(original) !== JSON.stringify(proposal.original) || JSON.stringify(replacement) !== JSON.stringify(proposal.replacement) || !Array.isArray(row.lines) || row.lines.length < 1 || row.lines.length > 100) return invalid();
  const currency = text(row.currency, 3), lines = row.lines.map(line), catalogSubtotal = money(row.catalogSubtotal), tax = money(row.tax), charges = money(row.charges), total = money(row.total);
  const currentRevision = revision(row.revision);
  if (!/^[A-Z]{3}$/.test(currency) || lines.some(value => value.price.currency !== currency) || new Set(lines.map(substitutionLineKey)).size !== lines.length ||
    lines.reduce((sum, value) => sum + cents(value.catalogSubtotal), BigInt(0)) !== cents(catalogSubtotal) || cents(total) !== cents(catalogSubtotal) + cents(tax) + cents(charges) || !['Pending', 'NotRequired', 'Approved', 'Rejected'].includes(String(row.spendingState))) return invalid();
  const retained = lines.find(value => substitutionLineKey(value) === substitutionLineKey(original));
  if (decision === 'Accept') {
    const introduced = lines.find(value => value.lineRevision === String(BigInt(currentRevision) + BigInt(1)));
    const remainder = original.packages - proposal.input.originalPackages;
    if (!introduced || !selected({ ...introduced, lineRevision: '0' }, proposal.input.replacement) ||
      (remainder === 0 ? retained !== undefined : !retained || retained.packages !== remainder || JSON.stringify(retained.definition) !== JSON.stringify(original.definition) || JSON.stringify(retained.price) !== JSON.stringify(original.price))) return invalid();
  } else if (!retained || JSON.stringify(retained) !== JSON.stringify(original)) return invalid();
  return { proposalId: proposal.proposalId, orderId: proposal.orderId, suborderId: proposal.suborderId, revision: currentRevision, decision, review: hash(row.review), original, originalPackages: proposal.input.originalPackages, replacement, currency, lines, catalogSubtotal, tax, charges, total, spendingState: row.spendingState as string, calculatedAt: time(row.calculatedAt) };
}
export type SubstitutionDecisionReview = ReturnType<typeof parseSubstitutionDecisionReview>;
export function parseSubstitutionResult(value: unknown, operation?: SubstitutionOperation) {
  const row = record(value), status = row.status;
  if (status !== 'proposed' && status !== 'accepted' && status !== 'declined' || operation && status !== ({ propose: 'proposed', accept: 'accepted', decline: 'declined' }[operation])) return invalid();
  const decisionId = row.decisionId === null ? null : priceId(row.decisionId), currentRevision = row.revision === null ? null : revision(row.revision);
  if ((status === 'proposed') !== (decisionId === null && currentRevision === null) || (decisionId === null) !== (currentRevision === null)) return invalid();
  return { proposalId: priceId(row.proposalId), orderId: priceId(row.orderId), suborderId: priceId(row.suborderId), decisionId, revision: currentRevision, status, recordedAt: time(row.recordedAt) };
}
export type SubstitutionResult = ReturnType<typeof parseSubstitutionResult>;
function page<T>(value: unknown, parse: (item: unknown) => T, id: (item: T) => string, cursor: string | null, ascending = false) {
  const row = record(value); if (!Array.isArray(row.items) || row.items.length > 25 || typeof row.hasMore !== 'boolean' || row.hasMore && row.items.length !== 25) return invalid();
  const items = row.items.map(parse); let previous = cursor === null ? null : BigInt(priceId(cursor));
  for (const item of items) { const current = BigInt(id(item)); if (previous !== null && (ascending ? current <= previous : current >= previous)) return invalid(); previous = current; }
  return { items, hasMore: row.hasMore };
}
function data<T>(value: { data?: T; error?: unknown; response: Response }): T {
  if (!value.response.ok || value.data === undefined) { const error = value.error && typeof value.error === 'object' ? value.error as Record<string, unknown> : {}; throw new AccessFailure(value.response.status, typeof error.code === 'string' ? error.code : 'request_failed'); } return value.data;
}
const headers = (actor: string) => ({ 'X-Expected-Actor-Id': priceId(actor) });
const key = (value: string) => /^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/.test(value) ? value : invalid();
function writeReady(reason: string, current: () => boolean) {
  if (reason.trim() !== reason || reason.length < 1 || reason.length > 1000 || /[\u0000-\u001f\u007f]/.test(reason)) throw new RequestNotSentError(400, 'invalid_reason');
  if (!current()) throw new RequestNotSentError(409, 'substitution_view_stale');
}
export function createOrderSubstitutionGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async targets(actor: string, beforeId: string | null = null, signal?: AbortSignal) {
      return page(data(await client.POST('/api/v1/ordering/substitutions/targets/query', { body: { purpose: 'order-substitution', beforeId }, params: { header: headers(actor) }, signal })), parseSubstitutionTarget, item => item.suborderId, beforeId);
    },
    async packages(actor: string, target: SubstitutionTarget, search: string, afterId: string | null = null, signal?: AbortSignal) {
      return page(data(await client.POST('/api/v1/ordering/substitutions/packages/query', { body: { purpose: 'order-substitution', orderId: target.orderId, suborderId: target.suborderId, search: search.trim() || null, afterId }, params: { header: headers(actor) }, signal })), line, item => item.definition.skuId, afterId, true);
    },
    async history(actor: string, forBuyer: boolean, orderId: string | null = null, beforeId: string | null = null, signal?: AbortSignal) {
      const result = page(data(await client.POST('/api/v1/ordering/substitutions/query', { body: { purpose: 'order-substitution', forBuyer, orderId, beforeId }, params: { header: headers(actor) }, signal })), parseSubstitutionHistory, item => item.proposalId, beforeId);
      if (orderId !== null && result.items.some(item => item.orderId !== orderId)) return invalid(); return result;
    },
    async preview(actor: string, input: SubstitutionInput, signal?: AbortSignal) {
      const checked = parseSubstitutionInput(input);
      return parseSubstitutionProposalReview(data(await client.POST('/api/v1/ordering/substitutions/preview/query', { body: { purpose: 'order-substitution', input: checked }, params: { header: headers(actor) }, signal })), checked);
    },
    async propose(actor: string, review: SubstitutionProposalReview, reason: string, requestKey: string, current: () => boolean) {
      writeReady(reason, current);
      const result = parseSubstitutionResult(data(await client.POST('/api/v1/ordering/substitutions', { body: { input: parseSubstitutionInput(review.input), review: hash(review.review), reason }, params: { header: { ...headers(actor), 'If-Match': '"0"', 'Idempotency-Key': key(requestKey) } } })), 'propose');
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown');
      if (result.orderId !== review.input.orderId || result.suborderId !== review.input.suborderId) return invalid(); return result;
    },
    async review(actor: string, proposal: SubstitutionHistory, decision: SubstitutionDecision, signal?: AbortSignal) {
      return parseSubstitutionDecisionReview(data(await client.POST('/api/v1/ordering/substitutions/{id}/preview/query', { body: { purpose: 'order-substitution', decision }, params: { path: { id: proposal.proposalId }, header: headers(actor) }, signal })), proposal, decision);
    },
    async decide(actor: string, review: SubstitutionDecisionReview, reason: string, requestKey: string, current: () => boolean) {
      writeReady(reason, current);
      const result = parseSubstitutionResult(data(await client.POST('/api/v1/ordering/substitutions/{id}/decisions', { body: { decision: review.decision, review: hash(review.review), reason }, params: { path: { id: review.proposalId }, header: { ...headers(actor), 'If-Match': '"' + revision(review.revision) + '"', 'Idempotency-Key': key(requestKey) } } })), review.decision === 'Accept' ? 'accept' : 'decline');
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown');
      if (result.proposalId !== review.proposalId || result.orderId !== review.orderId || result.suborderId !== review.suborderId || BigInt(result.revision!) !== BigInt(review.revision) + BigInt(1)) return invalid(); return result;
    },
    async recover(actor: string, operation: SubstitutionOperation, requestKey: string, current: () => boolean, signal?: AbortSignal) {
      if (!current()) throw new AccessFailure(409, 'recovery_request_stale');
      const row = record(data(await client.POST('/api/v1/ordering/substitutions/operations/query', { body: { purpose: 'operation-recovery', operation, key: key(requestKey) }, params: { header: headers(actor) }, signal })));
      if (!current()) throw new AccessFailure(409, 'recovery_request_stale');
      if (row.status === 'not_confirmed' && row.result === null) return null;
      if (row.status !== 'committed') return invalid(); return { state: 'committed' as const, result: parseSubstitutionResult(row.result, operation) };
    },
  };
}
