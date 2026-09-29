import { createUnitTokoClient, RequestNotSentError } from "../../api/client";
import { AccessFailure, stableContent } from "../administration/workflow";
import type { OperationReference } from "../administration/operation-journal";
import type { AccessScope } from "../organizations/access-scope";
import { sourceActor, sourceKey, sourceLocation, sourceLocationKey, type SourceDecision } from "./stock-source-workflow";
import { parseCurrentAlias, parseSourceAlias, parseSourceAliases, prepareSourceAlias, prepareSourceAliasDecision,
  sourceAliasOperation, sourceAliasOutcome, sourceAliasReceipt, type SourceAliasCommand } from "./stock-source-alias-workflow";

const invalid = (): never => { throw new AccessFailure(400, "stock_source_alias_input_invalid"); };
const badResponse = (): never => { throw new AccessFailure(502, "stock_source_alias_response_invalid"); };
function data<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) {
    const problem = result.error && typeof result.error === "object" ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof problem.code === "string" ? problem.code : "request_failed",
      typeof problem.traceId === "string" ? problem.traceId : undefined);
  }
  return result.data;
}
function page(offset: number) {
  if (!Number.isInteger(offset) || offset < 0 || offset > 10000) return invalid();
  return { offset, limit: 25 };
}
function reviewed(command: SourceAliasCommand): SourceAliasCommand {
  try {
    const copy = "proposalId" in command
      ? prepareSourceAliasDecision(command.actorId, command.proposal, command.operation.slice("stock.source.alias.".length) as SourceDecision,
        command.body.reviewReference, command.body.reason)
      : prepareSourceAlias(command.actorId, command.current, command.body.enabled, command.body.reason);
    if (stableContent(copy) !== stableContent(command)) return invalid();
    return copy;
  } catch { return invalid(); }
}
/** Authenticated configuration only. Every command is sent once; unknown outcomes retain the original reference. */
export function createStockSourceAliasGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async current(actorId: string, poolId: string, requestedLocation: AccessScope, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": sourceActor(actorId) }, id = sourceActor(poolId), location = sourceLocation(requestedLocation);
      const result = parseCurrentAlias(data(await client.POST("/api/v1/inventory/sources/pools/{poolId}/aliases/query", {
        body: { purpose: "inventory-source-alias", requestedLocation: location }, params: { path: { poolId: id }, header }, signal,
      })));
      if (result.source.physicalPoolId !== id || sourceLocationKey(result.requestedLocation) !== sourceLocationKey(location)) return badResponse();
      return result;
    },
    async history(actorId: string, poolId: string, requestedLocation: AccessScope, offset = 0, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": sourceActor(actorId) }, id = sourceActor(poolId), location = sourceLocation(requestedLocation), paging = page(offset);
      return parseSourceAliases(data(await client.POST("/api/v1/inventory/sources/pools/{poolId}/alias-proposals/query", {
        body: { purpose: "inventory-source-alias-history", requestedLocation: location, ...paging }, params: { path: { poolId: id }, header }, signal,
      })), id, location, offset, paging.limit);
    },
    async detail(actorId: string, poolId: string, requestedLocation: AccessScope, proposalId: string, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": sourceActor(actorId) }, pool = sourceActor(poolId), id = sourceActor(proposalId), location = sourceLocation(requestedLocation);
      const value = data(await client.POST("/api/v1/inventory/sources/pools/{poolId}/alias-proposals/{id}/query", {
        body: { purpose: "inventory-source-alias-history", requestedLocation: location, offset: 0, limit: 25 }, params: { path: { poolId: pool, id }, header }, signal,
      }));
      if (value === null) return null;
      const result = parseSourceAlias(value);
      if (result.id !== id || result.physicalPoolId !== pool || sourceLocationKey(result.requestedLocation) !== sourceLocationKey(location)) return badResponse();
      return result;
    },
    async submit(command: SourceAliasCommand, key: string, isCurrent: () => boolean = () => true) {
      const snapshot = reviewed(command), header = { "X-Expected-Actor-Id": sourceActor(snapshot.actorId), "Idempotency-Key": sourceKey(key),
        "If-Match": '"' + snapshot.expectedVersion + '"' };
      if (!isCurrent()) throw new RequestNotSentError(409, "stock_source_alias_view_stale");
      let result: unknown;
      if ("proposalId" in snapshot) {
        const routes = {
          "stock.source.alias.approve": "/api/v1/inventory/sources/alias-proposals/{id}/approve",
          "stock.source.alias.reject": "/api/v1/inventory/sources/alias-proposals/{id}/reject",
          "stock.source.alias.withdraw": "/api/v1/inventory/sources/alias-proposals/{id}/withdraw",
        } as const;
        result = data(await client.POST(routes[snapshot.operation], { body: snapshot.body, params: { path: { id: snapshot.proposalId }, header } }));
      } else result = data(await client.POST("/api/v1/inventory/sources/pools/{poolId}/aliases",
        { body: snapshot.body, params: { path: { poolId: snapshot.poolId }, header } }));
      if (!isCurrent()) throw new AccessFailure(503, "commit_outcome_unknown");
      return sourceAliasOutcome(result, snapshot);
    },
    async receipt(entry: OperationReference, poolId: string, requestedLocation: AccessScope, signal?: AbortSignal) {
      const operation = sourceAliasOperation(entry.operation), header = { "X-Expected-Actor-Id": sourceActor(entry.actorId) }, pool = sourceActor(poolId);
      const result = sourceAliasReceipt(data(await client.POST("/api/v1/inventory/sources/alias-operations/query", {
        body: { purpose: "operation-recovery", physicalPoolId: pool, requestedLocation: sourceLocation(requestedLocation), operation, key: sourceKey(entry.key) },
        params: { header }, signal,
      })), operation);
      if (result !== null && result.physicalPoolId !== pool) return badResponse();
      return result;
    },
  };
}
export type StockSourceAliasGateway = ReturnType<typeof createStockSourceAliasGateway>;
