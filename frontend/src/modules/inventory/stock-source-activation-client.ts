import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure, stableContent } from '../administration/workflow';
import type { OperationReference } from '../administration/operation-journal';
import { sourceActor, sourceKey } from './stock-source-workflow';
import { activationAction, activationHandoff, activationOperation, activationOutcome, activationReceipt, parseActivationPreview,
  parseActivationReview, parseActivationDetail, parseActivationHistory, prepareActivationProposal, prepareActivationDecision,
  type ActivationAction, type ActivationCommand, type ActivationHandoff } from './stock-source-activation-workflow';

const invalid = (): never => { throw new RequestNotSentError(400, 'stock_source_activation_input_invalid'); };
const bad = (): never => { throw new AccessFailure(502, 'stock_source_activation_response_invalid'); };
function data<T>(result: { data?: T; error?: unknown; response: Response }, mutation = false): T {
  if (result.response.ok && result.data === undefined) throw new AccessFailure(mutation ? 503 : 502,
    mutation ? 'commit_outcome_unknown' : 'stock_source_activation_response_invalid');
  if (!result.response.ok || result.data === undefined) {
    const problem = result.error && typeof result.error === 'object' ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof problem.code === 'string' ? problem.code : 'request_failed',
      typeof problem.traceId === 'string' ? problem.traceId : undefined);
  }
  return result.data;
}
function reviewed(command: ActivationCommand): ActivationCommand {
  try {
    const copy = 'proposalId' in command ? prepareActivationDecision(command.actorId, command.review, command.body.reason)
      : prepareActivationProposal(command.actorId, command.preview, command.body.handoff, command.body.reason);
    if (stableContent(copy) !== stableContent(command)) return invalid(); return copy;
  } catch { return invalid(); }
}
/** Reuses the maintained relative cookie/CSRF client. No automatic command retry. */
export function createStockSourceActivationGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  const originals = new Map<string, string>(), inFlight = new Set<string>();
  return {
    async preview(actorId: string, poolId: string, signal?: AbortSignal) {
      const pool = sourceActor(poolId), header = { 'X-Expected-Actor-Id': sourceActor(actorId) };
      const result = parseActivationPreview(data(await client.POST('/api/v1/inventory/sources/pools/{poolId}/activation-preview/query', {
        body: { purpose: 'inventory-source-activation-preview' }, params: { path: { poolId: pool }, header }, signal,
      })));
      return result.physicalPoolId === pool ? result : bad();
    },
    async review(actorId: string, poolId: string, proposalId: string, action: ActivationAction, handoff: ActivationHandoff | null, signal?: AbortSignal) {
      const pool = sourceActor(poolId), id = sourceActor(proposalId), selected = activationAction(action), header = { 'X-Expected-Actor-Id': sourceActor(actorId) };
      const facts = selected === 'approve' ? activationHandoff(handoff) : handoff === null ? null : invalid();
      const result = parseActivationReview(data(await client.POST('/api/v1/inventory/sources/activation-proposals/{id}/review/query', {
        body: { purpose: 'inventory-source-activation-review', physicalPoolId: pool, action: selected, handoff: facts }, params: { path: { id }, header }, signal,
      })));
      return result.physicalPoolId === pool && result.proposalId === id && result.action === selected && stableContent(result.handoff) === stableContent(facts) ? result : bad();
    },
    async history(actorId: string, poolId: string, offset = 0, signal?: AbortSignal) {
      if (!Number.isInteger(offset) || offset < 0 || offset > 10000) return invalid();
      const pool = sourceActor(poolId), header = { 'X-Expected-Actor-Id': sourceActor(actorId) };
      return parseActivationHistory(data(await client.POST('/api/v1/inventory/sources/pools/{poolId}/activation-proposals/query', {
        body: { purpose: 'inventory-source-activation-history', offset, limit: 25 }, params: { path: { poolId: pool }, header }, signal,
      })), pool, offset, 25);
    },
    async detail(actorId: string, poolId: string, proposalId: string, signal?: AbortSignal) {
      const pool = sourceActor(poolId), id = sourceActor(proposalId), header = { 'X-Expected-Actor-Id': sourceActor(actorId) };
      const raw = data(await client.POST('/api/v1/inventory/sources/pools/{poolId}/activation-proposals/{id}/query', {
        body: { purpose: 'inventory-source-activation-history' }, params: { path: { poolId: pool, id }, header }, signal,
      }));
      if (raw === null) return null; const result = parseActivationDetail(raw);
      return result.physicalPoolId === pool && result.id === id ? result : bad();
    },
    async submit(command: ActivationCommand, key: string, isCurrent: () => boolean = () => true) {
      const snapshot = reviewed(command), original = sourceKey(key), identity = snapshot.actorId + '\n' + snapshot.operation + '\n' + original;
      const fingerprint = stableContent(snapshot), previous = originals.get(identity);
      if (previous !== undefined && previous !== fingerprint) throw new RequestNotSentError(409, 'idempotency_key_reused');
      if (inFlight.has(identity)) throw new RequestNotSentError(409, 'request_in_flight');
      if (!isCurrent()) throw new RequestNotSentError(409, 'stock_source_activation_view_stale');
      originals.set(identity, fingerprint); inFlight.add(identity);
      try {
        const header = { 'X-Expected-Actor-Id': snapshot.actorId, 'Idempotency-Key': original, 'If-Match': '"' + snapshot.expectedVersion + '"' };
        let result: unknown;
        if ('proposalId' in snapshot) {
          const route = snapshot.review.action === 'approve' ? '/api/v1/inventory/sources/activation-proposals/{id}/approve'
            : '/api/v1/inventory/sources/activation-proposals/{id}/reject';
          result = data(await client.POST(route, { body: snapshot.body, params: { path: { id: snapshot.proposalId }, header } }), true);
        } else result = data(await client.POST('/api/v1/inventory/sources/pools/{poolId}/activation-proposals', {
          body: snapshot.body, params: { path: { poolId: snapshot.poolId }, header },
        }), true);
        if (!isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown');
        // A truncated/malformed successful mutation response may already have committed.
        try { return activationOutcome(result, snapshot); } catch { throw new AccessFailure(503, 'commit_outcome_unknown'); }
      } finally { inFlight.delete(identity); }
    },
    async receipt(entry: OperationReference, poolId: string, signal?: AbortSignal) {
      const pool = sourceActor(poolId), operation = activationOperation(entry.operation), header = { 'X-Expected-Actor-Id': sourceActor(entry.actorId) };
      const result = activationReceipt(data(await client.POST('/api/v1/inventory/sources/activation-operations/query', {
        body: { purpose: 'operation-recovery', physicalPoolId: pool, operation, key: sourceKey(entry.key) }, params: { header }, signal,
      })), operation);
      return result === null || result.physicalPoolId === pool ? result : bad();
    },
  };
}
export type StockSourceActivationGateway = ReturnType<typeof createStockSourceActivationGateway>;
