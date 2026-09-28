import { createUnitTokoClient, RequestNotSentError } from "../../api/client";
import type { OperationReference } from "../administration/operation-journal";
import { AccessFailure, stableContent } from "../administration/workflow";
import type { AccessScope } from "../organizations/access-scope";
import {
  parseStockLocation, parseStockWarehouses, parseStockPackages, parseStockHistory, parseStockObservation,
  prepareStockCount, stockCountOutcome, stockCountReceipt, stockCountOperation, stockCountActor, stockCountKey,
  stockCountSearch, stockPageRequest, type StockCountCommand, type StockCountPurpose,
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
  });
  if (stableContent(snapshot.body) !== stableContent(command.body)) return invalidInput();
  return snapshot;
}
/** Authenticated, generated transport. Commands are sent once; callers own deliberate retries and their original key. */
export function createStockCountGateway(client: Client = createUnitTokoClient()) {
  return {
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
      const response = value(await client.POST("/api/v1/inventory/stock-counts", { body: snapshot.body, params: { header } }));
      if (!isCurrent()) throw new AccessFailure(503, "commit_outcome_unknown");
      return stockCountOutcome(response, snapshot);
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
