import { fetchCsrfToken, RequestNotSentError } from '../../api/client';
import { AccessFailure, stableContent } from '../administration/workflow';
import type { OperationReference } from '../administration/operation-journal';
import type { AccessScope } from '../organizations/access-scope';
import { parseStockLocation, stockLocationKey, stockPageRequest } from './stock-count-workflow';
import { parsePackInput, parsePackPreview, parsePackWarehouses, parsePackPackages, parsePackProposal, parsePackDecisionPreview, parsePackProposals, parsePackRules, packSearch,
  preparePackProposal, preparePackDecision, packOutcome, packReceipt, packOperation, packAction, packActor, packKey,
  type PackInput, type PackRole, type PackProposal, type PackAction, type PackCommand } from './pack-conversion-workflow';

export type PackRequest = Readonly<{ actorId: string; path: string; body: unknown; key?: string; revision?: string;
  signal?: AbortSignal; isCurrent: () => boolean }>;
export type PackTransport = { send(request: PackRequest): Promise<unknown> };
/** Maintained strict draft DTO transport. No provider credentials, request bodies or bindings enter persistent storage. */
export function createPackTransport(options: { origin?: string; fetch?: typeof globalThis.fetch } = {}): PackTransport {
  const fetcher = options.fetch ?? globalThis.fetch;
  return { async send(input) {
    const origin = options.origin ?? window.location.origin, url = new URL('/api/v1/inventory/pack-conversions' + input.path, origin);
    if (typeof window !== 'undefined' && url.origin !== window.location.origin) throw new RequestNotSentError(400, 'invalid_origin');
    const current = () => { if (input.signal?.aborted || !input.isCurrent()) throw new RequestNotSentError(409, 'pack_conversion_view_stale'); };
    current(); const token = await fetchCsrfToken(new URL('/api/v1/identity/csrf', origin).href, fetcher, input.signal); current();
    const headers: Record<string, string> = { 'Content-Type': 'application/json', 'X-CSRF-Token': token, 'X-Expected-Actor-Id': packActor(input.actorId) };
    if (input.key !== undefined) headers['Idempotency-Key'] = packKey(input.key);
    if (input.revision !== undefined) { if (input.revision !== '1') throw new RequestNotSentError(400, 'pack_conversion_input_invalid'); headers['If-Match'] = '"1"'; }
    const response = await fetcher(new Request(url, { method: 'POST', body: JSON.stringify(input.body), headers, signal: input.signal,
      credentials: 'same-origin', cache: 'no-store', redirect: 'error' }));
    const body: unknown = await response.json().catch(() => null);
    if (!input.isCurrent() || input.signal?.aborted) throw new AccessFailure(input.key === undefined ? 409 : 503, input.key === undefined ? 'pack_conversion_view_stale' : 'commit_outcome_unknown');
    if (!response.ok) {
      const error = body && typeof body === 'object' ? body as Record<string, unknown> : {};
      throw new AccessFailure(response.status, typeof error.code === 'string' ? error.code : 'request_failed', typeof error.traceId === 'string' ? error.traceId : undefined);
    }
    return body;
  } };
}
const invalid = (): never => { throw new AccessFailure(400, 'pack_conversion_input_invalid'); };
const bad = (): never => { throw new AccessFailure(502, 'pack_conversion_response_invalid'); };
function copyCommand(command: PackCommand): PackCommand {
  try {
    const copied = 'proposalId' in command ? preparePackDecision(command.actorId, command.preview, command.body.reason)
      : preparePackProposal(command.actorId, command.preview, command.body.reason);
    if (stableContent(command) !== stableContent(copied)) return invalid(); return copied;
  } catch { return invalid(); }
}
/** Every read is account-scoped; every mutation is a deliberate single original-key request. */
export function createPackConversionGateway(transport: PackTransport = createPackTransport()) {
  const inFlight = new Set<string>();
  const originalCommands = new Map<string, string>();
  const send = (actorId: string, path: string, body: unknown, signal?: AbortSignal, isCurrent: () => boolean = () => true) =>
    transport.send({ actorId: packActor(actorId), path, body, signal, isCurrent });
  return {
    async warehouses(actorId: string, signal?: AbortSignal, isCurrent?: () => boolean) {
      return parsePackWarehouses(await send(actorId, '/warehouses/query', { purpose: 'pack-conversion-rule-warehouses' }, signal, isCurrent));
    },
    async packages(actorId: string, location: AccessScope, role: PackRole, search = '', offset = 0, signal?: AbortSignal, isCurrent?: () => boolean) {
      if (role !== 'source' && role !== 'destination') return invalid();
      const paging = stockPageRequest(offset, 25), place = parseStockLocation(location);
      return parsePackPackages(await send(actorId, '/packages/query', { purpose: 'pack-conversion-rule-' + role, location: place, role,
        search: packSearch(search) || null, ...paging }, signal, isCurrent), role, offset, paging.limit);
    },
    async preview(actorId: string, input: PackInput, signal?: AbortSignal, isCurrent?: () => boolean) {
      const copied = parsePackInput(input);
      return parsePackPreview(await send(actorId, '/rules/preview/query', { purpose: 'pack-conversion-rule-preview', input: copied }, signal, isCurrent), copied);
    },
    async proposals(actorId: string, location: AccessScope, beforeId: string | null = null, signal?: AbortSignal, isCurrent?: () => boolean) {
      const place = parseStockLocation(location), cursor = beforeId === null ? null : packActor(beforeId);
      return parsePackProposals(await send(actorId, '/proposals/query', { purpose: 'pack-conversion-rule-history', location: place, beforeId: cursor }, signal, isCurrent), place, cursor);
    },
    async proposal(actorId: string, id: string, signal?: AbortSignal, isCurrent?: () => boolean) {
      const selected = packActor(id), value = await send(actorId, '/proposals/' + selected + '/query', { purpose: 'pack-conversion-rule-history' }, signal, isCurrent);
      const proposal = parsePackProposal(value); if (proposal.id !== selected) return bad(); return proposal;
    },
    async review(actorId: string, proposal: PackProposal, action: PackAction, signal?: AbortSignal, isCurrent?: () => boolean) {
      const selected = parsePackProposal(proposal), selectedAction = packAction(action);
      if (selected.status !== 'pending' || selected.canReview === false) throw new AccessFailure(403, 'independent_person_required');
      return parsePackDecisionPreview(await send(actorId, '/proposals/' + selected.id + '/review/query', {
        purpose: 'pack-conversion-rule-review', action: selectedAction }, signal, isCurrent), selected, selectedAction);
    },
    async rules(actorId: string, location: AccessScope, beforeId: string | null = null, signal?: AbortSignal, isCurrent?: () => boolean) {
      const place = parseStockLocation(location), cursor = beforeId === null ? null : packActor(beforeId);
      return parsePackRules(await send(actorId, '/rules/query', { purpose: 'pack-conversion-rule-history', location: place, beforeId: cursor }, signal, isCurrent), place, cursor);
    },
    async submit(command: PackCommand, key: string, isCurrent: () => boolean) {
      const snapshot = copyCommand(command), originalKey = packKey(key), identity = snapshot.actorId + ':' + snapshot.operation + ':' + originalKey;
      if (!isCurrent()) throw new RequestNotSentError(409, 'pack_conversion_view_stale');
      const content = stableContent(snapshot), original = originalCommands.get(identity);
      if (original !== undefined && original !== content) throw new RequestNotSentError(409, 'pack_conversion_input_invalid');
      originalCommands.set(identity, content);
      if (inFlight.has(identity)) throw new RequestNotSentError(409, 'request_in_progress');
      inFlight.add(identity);
      try {
        const path = 'proposalId' in snapshot ? '/proposals/' + snapshot.proposalId + '/' + snapshot.action : '/proposals';
        const response = await transport.send({ actorId: snapshot.actorId, path, body: snapshot.body, key: originalKey,
          ...('proposalId' in snapshot ? { revision: snapshot.expectedRevision } : {}), isCurrent });
        if (!isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown');
        try { return packOutcome(response, snapshot); }
        catch (error) {
          if (error instanceof AccessFailure && error.code === 'pack_conversion_response_invalid')
            throw new AccessFailure(503, 'commit_outcome_unknown');
          throw error;
        }
      } finally { inFlight.delete(identity); }
    },
    async receipt(entry: OperationReference, signal?: AbortSignal, isCurrent: () => boolean = () => true) {
      const actorId = packActor(entry.actorId), operation = packOperation(entry.operation), key = packKey(entry.key);
      return packReceipt(await send(actorId, '/operations/query', { purpose: 'operation-recovery', operation, key }, signal, isCurrent), operation);
    },
  };
}
export type PackConversionGateway = ReturnType<typeof createPackConversionGateway>;
export const packLocationKey = (location: AccessScope) => stockLocationKey(location);
