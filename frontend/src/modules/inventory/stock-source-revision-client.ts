import { createUnitTokoClient, RequestNotSentError } from "../../api/client";
import { AccessFailure, stableContent } from "../administration/workflow";
import type { OperationReference } from "../administration/operation-journal";
import type { AccessScope } from "../organizations/access-scope";
import { sourceActor, sourceKey, sourceLocation, sourceLocationKey, type SourceDecision } from "./stock-source-workflow";
import {
  parseCurrentSource, parseCurrentSources, parseSourceRevision, parseSourceRevisions, prepareSourceRevision,
  prepareSourceRevisionDecision, sourceRevisionOperation, sourceRevisionOutcome, sourceRevisionReceipt, type SourceRevisionCommand,
} from "./stock-source-revision-workflow";

const invalid = (): never => { throw new AccessFailure(400, "stock_source_revision_input_invalid"); };
const badResponse = (): never => { throw new AccessFailure(502, "stock_source_revision_response_invalid"); };
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
function reviewed(command: SourceRevisionCommand): SourceRevisionCommand {
  try {
    const copy = "proposalId" in command
      ? prepareSourceRevisionDecision(command.actorId, command.proposal, command.operation.slice("stock.source.revision.".length) as SourceDecision,
        command.body.reviewReference, command.body.reason)
      : prepareSourceRevision(command.actorId, command.current, command.body);
    if (stableContent(copy) !== stableContent(command)) return invalid();
    return copy;
  } catch { return invalid(); }
}
/** Calls require the current account reference. Commands are sent once; uncertain outcomes retain their original key. */
export function createStockSourceRevisionGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async sources(actorId: string, location: AccessScope, offset = 0, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": sourceActor(actorId) }, place = sourceLocation(location), paging = page(offset);
      return parseCurrentSources(data(await client.POST("/api/v1/inventory/sources/pools/query",
        { body: { purpose: "inventory-source-current", location: place, ...paging }, params: { header }, signal })), place, offset, paging.limit);
    },
    async current(actorId: string, location: AccessScope, poolId: string, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": sourceActor(actorId) }, place = sourceLocation(location), id = sourceActor(poolId);
      const result = parseCurrentSource(data(await client.POST("/api/v1/inventory/sources/pools/{poolId}/query",
        { body: { purpose: "inventory-source-current", location: place, offset: 0, limit: 25 }, params: { path: { poolId: id }, header }, signal })));
      if (result.physicalPoolId !== id || sourceLocationKey(result.settings.location) !== sourceLocationKey(place)) return badResponse();
      return result;
    },
    async history(actorId: string, location: AccessScope, poolId: string | null = null, offset = 0, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": sourceActor(actorId) }, place = sourceLocation(location), paging = page(offset);
      const selected = poolId === null ? null : sourceActor(poolId);
      return parseSourceRevisions(data(await client.POST("/api/v1/inventory/sources/revisions/query",
        { body: { purpose: "inventory-source-revisions", location: place, physicalPoolId: selected, ...paging }, params: { header }, signal })),
        place, selected, offset, paging.limit);
    },
    async detail(actorId: string, location: AccessScope, proposalId: string, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": sourceActor(actorId) }, place = sourceLocation(location), id = sourceActor(proposalId);
      const value = data(await client.POST("/api/v1/inventory/sources/revisions/{id}/query",
        { body: { purpose: "inventory-source-revisions", location: place }, params: { path: { id }, header }, signal }));
      if (value === null) return null;
      const result = parseSourceRevision(value);
      if (result.id !== id || sourceLocationKey(result.proposedSettings.location) !== sourceLocationKey(place)) return badResponse();
      return result;
    },
    async submit(command: SourceRevisionCommand, key: string, isCurrent: () => boolean = () => true) {
      const snapshot = reviewed(command), header = { "X-Expected-Actor-Id": sourceActor(snapshot.actorId), "Idempotency-Key": sourceKey(key),
        "If-Match": '"' + snapshot.expectedVersion + '"' };
      if (!isCurrent()) throw new RequestNotSentError(409, "stock_source_revision_view_stale");
      let result: unknown;
      if ("proposalId" in snapshot) {
        const route = {
          "stock.source.revision.approve": "/api/v1/inventory/sources/revisions/{id}/approve",
          "stock.source.revision.reject": "/api/v1/inventory/sources/revisions/{id}/reject",
          "stock.source.revision.withdraw": "/api/v1/inventory/sources/revisions/{id}/withdraw",
        } as const;
        result = data(await client.POST(route[snapshot.operation], { body: snapshot.body, params: { path: { id: snapshot.proposalId }, header } }));
      } else result = data(await client.POST("/api/v1/inventory/sources/pools/{poolId}/revisions",
        { body: snapshot.body, params: { path: { poolId: snapshot.poolId }, header } }));
      if (!isCurrent()) throw new AccessFailure(503, "commit_outcome_unknown");
      return sourceRevisionOutcome(result, snapshot);
    },
    async receipt(entry: OperationReference, location: AccessScope, signal?: AbortSignal) {
      const operation = sourceRevisionOperation(entry.operation), header = { "X-Expected-Actor-Id": sourceActor(entry.actorId) };
      return sourceRevisionReceipt(data(await client.POST("/api/v1/inventory/sources/revision-operations/query", {
        body: { purpose: "operation-recovery", location: sourceLocation(location), operation, key: sourceKey(entry.key) }, params: { header }, signal,
      })), operation);
    },
  };
}
export type StockSourceRevisionGateway = ReturnType<typeof createStockSourceRevisionGateway>;
