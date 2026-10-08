import { createUnitTokoClient, fetchCsrfToken, RequestNotSentError } from "../../api/client";
import type { OperationReference } from "../administration/operation-journal";
import { AccessFailure, stableContent } from "../administration/workflow";
import type { AccessScope } from "../organizations/access-scope";
import {
  parseStockLocation, parseStockWarehouses, parseStockPackages, parseStockHistory, parseStockObservation,
  prepareStockCount, stockCountOutcome, stockCountReceipt, stockCountOperation, stockCountActor, stockCountKey,
  stockCountSearch, stockPageRequest, type StockCountCommand, type StockCountPurpose,
  parseStockSources, parseStockPoolSelection,
} from "./stock-count-workflow";

type Client = ReturnType<typeof createUnitTokoClient>;
function value<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) {
    const problem = result.error && typeof result.error === "object" ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof problem.code === "string" ? problem.code : "request_failed",
      typeof problem.traceId === "string" ? problem.traceId : undefined);
  }
  return result.data;
}
const invalidInput = (): never => { throw new AccessFailure(400, "stock_count_input_invalid"); };
const invalidResponse = (): never => { throw new AccessFailure(502, "stock_count_response_invalid"); };
function locationInput(location: AccessScope) {
  try { return parseStockLocation(location); } catch { return invalidInput(); }
}
function commandInput(command: StockCountCommand) {
  if (command.operation !== stockCountOperation || typeof command.body.packages !== "number" ||
    !Number.isInteger(command.body.packages) || Object.is(command.body.packages, -0)) return invalidInput();
  const snapshot = prepareStockCount(command.actorId, command.body.location, command.choice, {
    packages: String(command.body.packages), reason: command.body.reason, observedLotLabel: command.body.observedLotLabel ?? "",
  }, command.source, command.body.scanSelections);
  if (stableContent(snapshot.body) !== stableContent(command.body)) return invalidInput();
  if ('poolSelection' in command.body) parseStockPoolSelection(command.body.poolSelection);
  return snapshot;
}
/** Authenticated, generated transport. Commands are sent once; callers own deliberate retries and their original key. */
export type StockSourceQuery = { actorId: string; location: AccessScope; offset: number; limit: number; signal?: AbortSignal; isCurrent: () => boolean };
export type StockSourceQueryTransport = { send(query: StockSourceQuery): Promise<Response> };
/** Maintained additive route pending root-generated OpenAPI. Same-origin CSRF only; no tokens in storage. */
export function createStockSourceQueryTransport(options: { origin?: string; fetch?: typeof globalThis.fetch } = {}): StockSourceQueryTransport {
  const fetcher = options.fetch ?? globalThis.fetch;
  return { async send(query) {
    const origin = options.origin ?? window.location.origin, url = new URL('/api/v1/inventory/stock-counts/sources/query', origin);
    if (typeof window !== 'undefined' && url.origin !== window.location.origin) throw new RequestNotSentError(400, 'invalid_origin');
    const current = () => { if (query.signal?.aborted || !query.isCurrent()) throw new RequestNotSentError(409, 'stock_count_view_stale'); };
    current(); const token = await fetchCsrfToken(new URL('/api/v1/identity/csrf', origin).href, fetcher, query.signal); current();
    return fetcher(new Request(url, { method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: query.signal,
      headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': token, 'X-Expected-Actor-Id': query.actorId },
      body: JSON.stringify({ purpose: 'inventory-stock-count', location: query.location, offset: query.offset, limit: query.limit }) }));
  } };
}
export function createStockCountGateway(client: Client = createUnitTokoClient(), sources: StockSourceQueryTransport = createStockSourceQueryTransport()) {
  const admissions = new Map<string, () => boolean>();
  // This middleware follows the shared CSRF middleware. A replaced/expired review cannot dispatch
  // after token acquisition; it never authenticates an actor or changes the protected transport.
  client.use({ onRequest({ request }) {
    if (!new URL(request.url).pathname.endsWith('/api/v1/inventory/stock-counts')) return;
    const identity = request.headers.get('X-Expected-Actor-Id') + ':' + request.headers.get('Idempotency-Key');
    const current = admissions.get(identity); if (current && !current()) throw new RequestNotSentError(409, 'stock_count_view_stale');
  } });
  return {
    async sources(actorId: string, location: AccessScope, offset = 0, signal?: AbortSignal, isCurrent: () => boolean = () => true) {
      const place = locationInput(location), paging = stockPageRequest(offset, 25), actor = stockCountActor(actorId);
      if (signal?.aborted || !isCurrent()) throw new RequestNotSentError(409, 'stock_count_view_stale');
      const response = await sources.send({ actorId: actor, location: place, ...paging, signal, isCurrent });
      const body: unknown = await response.json().catch(() => null);
      if (signal?.aborted || !isCurrent()) throw new AccessFailure(409, 'stock_count_view_stale');
      if (!response.ok) { const failure = body !== null && typeof body === 'object' ? body as Record<string, unknown> : {};
        throw new AccessFailure(response.status, typeof failure.code === 'string' ? failure.code : 'request_failed'); }
      return parseStockSources(body, place, offset, paging.limit);
    },
    async warehouses(actorId: string, purpose: StockCountPurpose, signal?: AbortSignal) {
      if (purpose !== "inventory-stock-count" && purpose !== "inventory-stock-history") return invalidInput();
      const header = { "X-Expected-Actor-Id": stockCountActor(actorId) };
      return parseStockWarehouses(value(await client.POST("/api/v1/inventory/stock-counts/warehouses/query",
        { body: { purpose }, params: { header }, signal })));
    },
    async packages(actorId: string, location: AccessScope, search = "", offset = 0, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": stockCountActor(actorId) };
      const paging = stockPageRequest(offset, 25);
      const body = { purpose: "inventory-stock-count", location: locationInput(location), search: stockCountSearch(search), ...paging };
      return parseStockPackages(value(await client.POST("/api/v1/inventory/stock-counts/packages/query",
        { body, params: { header }, signal })), offset, paging.limit);
    },
    async history(actorId: string, location: AccessScope, offset = 0, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": stockCountActor(actorId) };
      const paging = stockPageRequest(offset, 25), place = locationInput(location);
      return parseStockHistory(value(await client.POST("/api/v1/inventory/stock-counts/query",
        { body: { purpose: "inventory-stock-history", location: place, ...paging }, params: { header }, signal })), place, offset, paging.limit);
    },
    async observation(actorId: string, observationId: string, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": stockCountActor(actorId) }, id = stockCountActor(observationId);
      const response = value(await client.POST("/api/v1/inventory/stock-counts/{id}/query",
        { body: { purpose: "inventory-stock-history" }, params: { path: { id }, header }, signal }));
      if (response === null) return null;
      const observation = parseStockObservation(response);
      if (observation.id !== id) return invalidResponse();
      return observation;
    },
    async submit(command: StockCountCommand, key: string, isCurrent: () => boolean = () => true) {
      const snapshot = commandInput(command), header = {
        "X-Expected-Actor-Id": snapshot.actorId, "Idempotency-Key": stockCountKey(key),
      };
      if (!isCurrent()) throw new RequestNotSentError(409, "stock_count_view_stale");
      const identity = snapshot.actorId + ':' + key;
      if (admissions.has(identity)) throw new RequestNotSentError(409, 'request_in_progress');
      admissions.set(identity, isCurrent);
      try {
        const response = value(await client.POST("/api/v1/inventory/stock-counts", { body: snapshot.body, params: { header } }));
        if (!isCurrent()) throw new AccessFailure(503, "commit_outcome_unknown");
        return stockCountOutcome(response, snapshot);
      } finally { admissions.delete(identity); }
    },
    async receipt(entry: OperationReference, signal?: AbortSignal) {
      if (entry.operation !== stockCountOperation) return invalidInput();
      const header = { "X-Expected-Actor-Id": stockCountActor(entry.actorId) };
      const body = { purpose: "operation-recovery", operation: stockCountOperation, key: stockCountKey(entry.key) };
      return stockCountReceipt(value(await client.POST("/api/v1/inventory/stock-counts/operations/query",
        { body, params: { header }, signal })), { ...entry });
    },
  };
}
export type StockCountGateway = ReturnType<typeof createStockCountGateway>;
