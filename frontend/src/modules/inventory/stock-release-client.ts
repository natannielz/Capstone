import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure, stableContent } from '../administration/workflow';
import type { OperationReference } from '../administration/operation-journal';
import type { AccessScope } from '../organizations/access-scope';
import { sourceActor, sourceKey } from './stock-source-workflow';
import { parseStockLocation } from './stock-count-workflow';
import { parseReleaseWarehouses, parseReleaseSources, parseReleaseHistory, parseReleaseDetail, parseReleasePreview, parseReleaseReview,
  parseReleaseInput, prepareReleaseProposal, prepareReleaseDecision, releaseOutcome, releaseReceipt, releaseOperation, releaseAction,
  type ReleaseInput, type ReleaseDetail, type ReleaseAction, type ReleaseCommand } from './stock-release-workflow';

function data<T>(result: { data?: T; error?: unknown; response: Response }, mutation = false): T {
  if (result.response.ok && result.data === undefined) throw new AccessFailure(mutation ? 503 : 502, mutation ? 'commit_outcome_unknown' : 'stock_release_response_invalid');
  if (!result.response.ok || result.data === undefined) {
    const problem = result.error && typeof result.error === 'object' ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof problem.code === 'string' ? problem.code : 'request_failed', typeof problem.traceId === 'string' ? problem.traceId : undefined);
  }
  return result.data;
}
function copy(command: ReleaseCommand): ReleaseCommand {
  try {
    const checked = 'proposalId' in command ? prepareReleaseDecision(command.actorId, command.preview, command.body.reason)
      : prepareReleaseProposal(command.actorId, command.preview, command.body.reason);
    if (stableContent(command) !== stableContent(checked)) throw new Error('Changed command'); return checked;
  } catch { throw new RequestNotSentError(400, 'stock_release_input_invalid'); }
}
/** Same maintained generated cookie/CSRF client, with a view guard at both actual fetch boundaries. */
export function createStockReleaseGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient(), fetcher: typeof globalThis.fetch = globalThis.fetch) {
  const originals = new Map<string, string>(), inFlight = new Set<string>();
  const options = (signal: AbortSignal | undefined, isCurrent: () => boolean = () => true) => ({ signal,
    fetch: (input: RequestInfo | URL, init?: RequestInit) => {
      if (signal?.aborted || !isCurrent()) throw new RequestNotSentError(409, 'stock_release_view_stale');
      return fetcher(input, init);
    } });
  const header = (actorId: string) => ({ 'X-Expected-Actor-Id': sourceActor(actorId) });
  return {
    async warehouses(actorId: string, signal?: AbortSignal, current?: () => boolean) {
      return parseReleaseWarehouses(data(await client.POST('/api/v1/inventory/stock-releases/warehouses/query', {
        body: { purpose: 'stock-release-warehouses' }, params: { header: header(actorId) }, ...options(signal, current),
      })));
    },
    async sources(actorId: string, location: AccessScope, signal?: AbortSignal, current?: () => boolean) {
      return parseReleaseSources(data(await client.POST('/api/v1/inventory/stock-releases/sources/query', {
        body: { purpose: 'stock-release-sources', location: parseStockLocation(location), limit: 25 }, params: { header: header(actorId) }, ...options(signal, current),
      })));
    },
    async history(actorId: string, location: AccessScope, cursor: string | null = null, signal?: AbortSignal, current?: () => boolean) {
      const afterProposalId = cursor === null ? null : sourceActor(cursor);
      return parseReleaseHistory(data(await client.POST('/api/v1/inventory/stock-releases/proposals/query', {
        body: { purpose: 'stock-release-history', location: parseStockLocation(location), afterProposalId, limit: 25 }, params: { header: header(actorId) }, ...options(signal, current),
      })), afterProposalId);
    },
    async detail(actorId: string, proposalId: string, signal?: AbortSignal, current?: () => boolean) {
      const id = sourceActor(proposalId); const result = parseReleaseDetail(data(await client.POST('/api/v1/inventory/stock-releases/proposals/{id}/query', {
        body: { purpose: 'stock-release-history' }, params: { path: { id }, header: header(actorId) }, ...options(signal, current),
      })));
      if (result.proposal.id !== id) throw new AccessFailure(502, 'stock_release_response_invalid'); return result;
    },
    async preview(actorId: string, input: ReleaseInput, signal?: AbortSignal, current?: () => boolean) {
      const selected = parseReleaseInput(input);
      return parseReleasePreview(data(await client.POST('/api/v1/inventory/stock-releases/preview/query', {
        body: { purpose: 'stock-release-preview', input: selected }, params: { header: header(actorId) }, ...options(signal, current),
      })), selected);
    },
    async review(actorId: string, detail: ReleaseDetail, action: ReleaseAction, signal?: AbortSignal, current?: () => boolean) {
      const selected = parseReleaseDetail(detail), checked = releaseAction(action);
      return parseReleaseReview(data(await client.POST('/api/v1/inventory/stock-releases/proposals/{id}/review/query', {
        body: { purpose: 'stock-release-review', action: checked, policy: selected.proposal.input.policy },
        params: { path: { id: selected.proposal.id }, header: header(actorId) }, ...options(signal, current),
      })), selected, checked);
    },
    async submit(command: ReleaseCommand, key: string, current: () => boolean) {
      const selected = copy(command), originalKey = sourceKey(key), identity = selected.actorId + ':' + selected.operation + ':' + originalKey;
      if (!current()) throw new RequestNotSentError(409, 'stock_release_view_stale');
      const content = stableContent(selected), original = originals.get(identity);
      if (original !== undefined && original !== content) throw new RequestNotSentError(409, 'idempotency_key_reused');
      if (inFlight.has(identity)) throw new RequestNotSentError(409, 'request_in_progress');
      originals.set(identity, content); inFlight.add(identity);
      try {
        const headers = { ...header(selected.actorId), 'Idempotency-Key': originalKey, 'If-Match': '"' + selected.revision + '"' };
        const response = 'proposalId' in selected ? selected.action === 'approve'
          ? await client.POST('/api/v1/inventory/stock-releases/proposals/{id}/approve', { body: selected.body, params: { path: { id: selected.proposalId }, header: headers }, ...options(undefined, current) })
          : await client.POST('/api/v1/inventory/stock-releases/proposals/{id}/reject', { body: selected.body, params: { path: { id: selected.proposalId }, header: headers }, ...options(undefined, current) })
          : await client.POST('/api/v1/inventory/stock-releases/proposals', { body: selected.body, params: { header: headers }, ...options(undefined, current) });
        if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown');
        const value = data(response, true);
        try { return releaseOutcome(value, selected); }
        catch { throw new AccessFailure(503, 'commit_outcome_unknown'); }
      } finally { inFlight.delete(identity); }
    },
    async receipt(entry: OperationReference, signal?: AbortSignal, current: () => boolean = () => true) {
      const operation = releaseOperation(entry.operation), key = sourceKey(entry.key);
      return releaseReceipt(data(await client.POST('/api/v1/inventory/stock-releases/operations/query', {
        body: { purpose: 'operation-recovery', operation, key }, params: { header: header(entry.actorId) }, ...options(signal, current),
      })), operation);
    },
  };
}
export type StockReleaseGateway = ReturnType<typeof createStockReleaseGateway>;
