import { fetchCsrfToken, RequestNotSentError } from '../../api/client';
import { AccessFailure, stableContent } from '../administration/workflow';
import type { OperationReference } from '../administration/operation-journal';
import type { AccessScope } from '../organizations/access-scope';
import { parseStockLocation, stockLocationKey } from './stock-count-workflow';
import { createPackConversionGateway, type PackConversionGateway } from './pack-conversion-client';
import { parsePackRule, type PackRule } from './pack-conversion-workflow';
import { sourceActor, sourceKey, sourceNumber } from './stock-source-workflow';
import { conversionOperation, parseConversionInput, parseConversionSources, parseConversionPreview, parseConversionHistory, parseConversionDetail,
  prepareConversion, conversionOutcome, conversionReceipt, type ConversionInput, type ConversionSource, type ConversionCommand } from './pack-conversion-execution-workflow';

export type ConversionRequest = Readonly<{ actorId: string; path: string; body: unknown; key?: string; revision?: string;
  signal?: AbortSignal; isCurrent: () => boolean }>;
export type ConversionTransport = { send(request: ConversionRequest): Promise<unknown> };
/** Dedicated DTO transport uses the maintained CSRF helper; source revisions are lossless Int64 strings. */
export function createConversionTransport(options: { origin?: string; fetch?: typeof globalThis.fetch } = {}): ConversionTransport {
  const fetcher = options.fetch ?? globalThis.fetch;
  return { async send(input) {
    const origin = options.origin ?? window.location.origin, url = new URL('/api/v1/inventory/pack-conversions/executions' + input.path, origin);
    if (typeof window !== 'undefined' && url.origin !== window.location.origin) throw new RequestNotSentError(400, 'invalid_origin');
    const current = () => { if (input.signal?.aborted || !input.isCurrent()) throw new RequestNotSentError(409, 'pack_execution_view_stale'); };
    current();
    let actorId: string, key: string | undefined, revision: string | undefined, body: string;
    try { actorId = sourceActor(input.actorId); key = input.key === undefined ? undefined : sourceKey(input.key);
      revision = input.revision === undefined ? undefined : sourceNumber(input.revision); body = JSON.stringify(input.body);
      if (typeof body !== 'string') throw new Error('Missing command');
    } catch { throw new RequestNotSentError(400, 'pack_execution_input_invalid'); }
    const token = await fetchCsrfToken(new URL('/api/v1/identity/csrf', origin).href, fetcher, input.signal); current();
    const headers: Record<string, string> = { 'Content-Type': 'application/json', 'X-CSRF-Token': token, 'X-Expected-Actor-Id': actorId };
    if (key !== undefined) headers['Idempotency-Key'] = key;
    if (revision !== undefined) headers['If-Match'] = '"' + revision + '"';
    const response = await fetcher(new Request(url, { method: 'POST', body, headers, signal: input.signal,
      credentials: 'same-origin', cache: 'no-store', redirect: 'error' }));
    const result: unknown = await response.json().catch(() => null);
    if (input.signal?.aborted || !input.isCurrent()) throw new AccessFailure(key === undefined ? 409 : 503, key === undefined ? 'pack_execution_view_stale' : 'commit_outcome_unknown');
    if (!response.ok) {
      const problem = result && typeof result === 'object' && !Array.isArray(result) ? result as Record<string, unknown> : {};
      throw new AccessFailure(response.status, typeof problem.code === 'string' ? problem.code : 'request_failed', typeof problem.traceId === 'string' ? problem.traceId : undefined);
    }
    return result;
  } };
}
export function createPackExecutionGateway(transport: ConversionTransport = createConversionTransport(),
  rules: Pick<PackConversionGateway, 'warehouses' | 'rules'> = createPackConversionGateway()) {
  const originals = new Map<string, string>(), inFlight = new Set<string>();
  const send = (actorId: string, path: string, body: unknown, signal?: AbortSignal, isCurrent: () => boolean = () => true) =>
    transport.send({ actorId: sourceActor(actorId), path, body, signal, isCurrent });
  return {
    warehouses: rules.warehouses, rules: rules.rules,
    async sources(actorId: string, place: AccessScope, rule: PackRule, cursor: string | null = null, signal?: AbortSignal, current?: () => boolean) {
      const selected = parsePackRule(rule), location = parseStockLocation(place), beforeSourceBalanceId = cursor === null ? null : sourceNumber(cursor);
      if (stockLocationKey(location) !== stockLocationKey(selected.input.location)) throw new RequestNotSentError(400, 'pack_execution_input_invalid');
      return parseConversionSources(await send(actorId, '/sources/query', { purpose: 'pack-conversion-sources', location,
        ruleId: selected.id, ruleVersion: selected.version, beforeSourceBalanceId, limit: 25 }, signal, current), selected, beforeSourceBalanceId);
    },
    async preview(actorId: string, input: ConversionInput, rule: PackRule, source: ConversionSource, signal?: AbortSignal, current?: () => boolean) {
      const selected = parseConversionInput(input);
      return parseConversionPreview(await send(actorId, '/preview/query', { purpose: 'pack-conversion-preview', input: selected }, signal, current), selected, rule, source);
    },
    async history(actorId: string, place: AccessScope, cursor: string | null = null, signal?: AbortSignal, current?: () => boolean) {
      const location = parseStockLocation(place), beforeConversionId = cursor === null ? null : sourceNumber(cursor);
      return parseConversionHistory(await send(actorId, '/query', { purpose: 'pack-conversion-history', location, beforeConversionId, limit: 25 }, signal, current), location, beforeConversionId);
    },
    async detail(actorId: string, id: string, signal?: AbortSignal, current?: () => boolean) {
      const selected = sourceNumber(id), result = parseConversionDetail(await send(actorId, '/' + selected + '/query', { purpose: 'pack-conversion-history' }, signal, current));
      if (result.id !== selected) throw new AccessFailure(502, 'pack_execution_response_invalid'); return result;
    },
    async submit(command: ConversionCommand, key: string, isCurrent: () => boolean) {
      let selected: ConversionCommand, originalKey: string;
      try { selected = prepareConversion(command.actorId, command.preview, command.body.reason); originalKey = sourceKey(key);
        if (stableContent(command) !== stableContent(selected)) throw new Error('Changed command');
      } catch { throw new RequestNotSentError(400, 'pack_execution_input_invalid'); }
      if (!isCurrent()) throw new RequestNotSentError(409, 'pack_execution_view_stale');
      const identity = selected.actorId + ':' + originalKey, content = stableContent(selected), previous = originals.get(identity);
      if (previous !== undefined && previous !== content) throw new RequestNotSentError(409, 'idempotency_key_reused');
      if (inFlight.has(identity)) throw new RequestNotSentError(409, 'request_in_progress');
      originals.set(identity, content); inFlight.add(identity);
      try {
        const result = await transport.send({ actorId: selected.actorId, path: '', body: selected.body, key: originalKey,
          revision: selected.body.expected.sourceBalanceRevision, isCurrent });
        if (!isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown');
        try { return conversionOutcome(result, selected); } catch { throw new AccessFailure(503, 'commit_outcome_unknown'); }
      } finally { inFlight.delete(identity); }
    },
    async receipt(entry: OperationReference, currentActorId: string, signal?: AbortSignal, current: () => boolean = () => true) {
      const actorId = sourceActor(currentActorId);
      if (entry.actorId !== actorId || entry.operation !== conversionOperation || !current()) throw new AccessFailure(409, 'recovery_request_stale');
      return conversionReceipt(await send(actorId, '/operations/query', { purpose: 'operation-recovery', operation: conversionOperation,
        key: sourceKey(entry.key) }, signal, current));
    },
  };
}
export type PackExecutionGateway = ReturnType<typeof createPackExecutionGateway>;
