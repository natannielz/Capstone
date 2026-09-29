import { createUnitTokoClient, RequestNotSentError } from "../../api/client";
import { AccessFailure, stableContent } from "../administration/workflow";
import type { OperationReference } from "../administration/operation-journal";
import { sourceActor, sourceKey } from "./stock-source-workflow";
import { parseSuspensionPreview, parseSourceSuspension, parseSourceSuspensions, prepareSourceSuspension,
  suspensionOutcome, suspensionReceipt, type SourceSuspensionCommand } from "./stock-source-suspension-workflow";

const invalid = (): never => { throw new AccessFailure(400, "stock_source_suspension_input_invalid"); };
const badResponse = (): never => { throw new AccessFailure(502, "stock_source_suspension_response_invalid"); };
function data<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) {
    const problem = result.error && typeof result.error === "object" ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof problem.code === "string" ? problem.code : "request_failed",
      typeof problem.traceId === "string" ? problem.traceId : undefined);
  }
  return result.data;
}
function reviewed(command: SourceSuspensionCommand): SourceSuspensionCommand {
  try {
    const copy = prepareSourceSuspension(command.actorId, command.preview, command.body.reason);
    if (stableContent(copy) !== stableContent(command)) return invalid();
    return copy;
  } catch { return invalid(); }
}
/** Each stop is sent once; uncertain outcomes use original-actor receipt lookup only. */
export function createStockSourceSuspensionGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async preview(actorId: string, poolId: string, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": sourceActor(actorId) }, id = sourceActor(poolId);
      const result = parseSuspensionPreview(data(await client.POST("/api/v1/inventory/sources/pools/{poolId}/suspension-preview/query", {
        body: { purpose: "inventory-source-suspension-preview" }, params: { path: { poolId: id }, header }, signal,
      })));
      return result.physicalPoolId === id ? result : badResponse();
    },
    async latest(actorId: string, poolId: string, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": sourceActor(actorId) }, id = sourceActor(poolId);
      const value = data(await client.POST("/api/v1/inventory/sources/pools/{poolId}/suspension/query", {
        body: { purpose: "inventory-source-suspension-history" }, params: { path: { poolId: id }, header }, signal,
      }));
      if (value === null) return null;
      const result = parseSourceSuspension(value);
      return result.physicalPoolId === id ? result : badResponse();
    },
    async history(actorId: string, poolId: string, offset = 0, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": sourceActor(actorId) }, id = sourceActor(poolId);
      if (!Number.isInteger(offset) || offset < 0 || offset > 10000) return invalid();
      return parseSourceSuspensions(data(await client.POST("/api/v1/inventory/sources/pools/{poolId}/suspensions/query", {
        body: { purpose: "inventory-source-suspension-history", offset, limit: 25 }, params: { path: { poolId: id }, header }, signal,
      })), id, offset, 25);
    },
    async detail(actorId: string, poolId: string, suspensionId: string, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": sourceActor(actorId) }, pool = sourceActor(poolId), id = sourceActor(suspensionId);
      const value = data(await client.POST("/api/v1/inventory/sources/pools/{poolId}/suspensions/{id}/query", {
        body: { purpose: "inventory-source-suspension-history" }, params: { path: { poolId: pool, id }, header }, signal,
      }));
      if (value === null) return null;
      const result = parseSourceSuspension(value);
      return result.id === id && result.physicalPoolId === pool ? result : badResponse();
    },
    async submit(command: SourceSuspensionCommand, key: string, isCurrent: () => boolean = () => true) {
      const snapshot = reviewed(command), header = { "X-Expected-Actor-Id": sourceActor(snapshot.actorId), "Idempotency-Key": sourceKey(key),
        "If-Match": '"' + snapshot.expectedVersion + '"' };
      if (!isCurrent()) throw new RequestNotSentError(409, "stock_source_suspension_view_stale");
      const result = data(await client.POST("/api/v1/inventory/sources/pools/{poolId}/suspend", {
        body: snapshot.body, params: { path: { poolId: snapshot.poolId }, header },
      }));
      if (!isCurrent()) throw new AccessFailure(503, "commit_outcome_unknown");
      return suspensionOutcome(result, snapshot);
    },
    async receipt(entry: OperationReference, poolId: string, signal?: AbortSignal) {
      if (entry.operation !== "stock.source.suspend") return invalid();
      const header = { "X-Expected-Actor-Id": sourceActor(entry.actorId) }, pool = sourceActor(poolId);
      const result = suspensionReceipt(data(await client.POST("/api/v1/inventory/sources/suspension-operations/query", {
        body: { purpose: "operation-recovery", physicalPoolId: pool, operation: "stock.source.suspend", key: sourceKey(entry.key) },
        params: { header }, signal,
      })));
      return result === null || result.physicalPoolId === pool ? result : badResponse();
    },
  };
}
export type StockSourceSuspensionGateway = ReturnType<typeof createStockSourceSuspensionGateway>;
