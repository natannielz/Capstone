import { createUnitTokoClient, RequestNotSentError } from "../../api/client";
import type { OperationReference } from "../administration/operation-journal";
import { AccessFailure, stableContent } from "../administration/workflow";
import type { AccessScope } from "../organizations/access-scope";
import {
  sourceActor, sourceKey, sourceOperation, sourcePurpose, sourceLocation, sourceLocationKey, sourceWarehouses, sourceAgeSeconds,
  parseSourceHistory, parseSourceDetail, prepareSourceProposal, prepareSourceDecision, sourceOutcome, sourceReceipt,
  type SourceCommand, type SourceDecision, type SourcePurpose,
} from "./stock-source-workflow";

const invalid = (): never => { throw new AccessFailure(400, "stock_source_input_invalid"); };
const badResponse = (): never => { throw new AccessFailure(502, "stock_source_response_invalid"); };
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
function reviewed(command: SourceCommand): SourceCommand {
  try {
    const copy = "proposalId" in command
      ? prepareSourceDecision(command.actorId, command.proposal, command.operation.slice("stock.source.".length) as SourceDecision,
        command.body.reviewReference, command.body.reason)
      : prepareSourceProposal(command.actorId, command.body.location, {
        ...command.body, maximumAgeSeconds: sourceAgeSeconds(command.body.maximumAgeMilliseconds),
        externalNamespace: command.body.externalNamespace ?? "", reservationProtocolReference: command.body.reservationProtocolReference ?? "",
      });
    if (stableContent(copy) !== stableContent(command)) return invalid();
    return copy;
  } catch { return invalid(); }
}
/** No automatic command retry. Every send keeps the reviewed snapshot and original key. */
export function createStockSourceGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async warehouses(actorId: string, purpose: SourcePurpose, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": sourceActor(actorId) };
      return sourceWarehouses(data(await client.POST("/api/v1/inventory/sources/warehouses/query",
        { body: { purpose: sourcePurpose(purpose) }, params: { header }, signal })));
    },
    async history(actorId: string, location: AccessScope, offset = 0, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": sourceActor(actorId) }, place = sourceLocation(location), paging = page(offset);
      return parseSourceHistory(data(await client.POST("/api/v1/inventory/sources/query",
        { body: { purpose: "inventory-source-setup", location: place, ...paging }, params: { header }, signal })), place, offset, paging.limit);
    },
    async detail(actorId: string, location: AccessScope, proposalId: string, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": sourceActor(actorId) }, id = sourceActor(proposalId), place = sourceLocation(location);
      const value = data(await client.POST("/api/v1/inventory/sources/{id}/query",
        { body: { purpose: "inventory-source-setup", location: place, offset: 0, limit: 25 }, params: { path: { id }, header }, signal }));
      if (value === null) return null;
      const result = parseSourceDetail(value);
      if (result.id !== id || sourceLocationKey(result.location) !== sourceLocationKey(place)) return badResponse();
      return result;
    },
    async submit(command: SourceCommand, key: string, isCurrent: () => boolean = () => true) {
      const snapshot = reviewed(command), header = { "X-Expected-Actor-Id": sourceActor(snapshot.actorId), "Idempotency-Key": sourceKey(key) };
      if (!isCurrent()) throw new RequestNotSentError(409, "stock_source_view_stale");
      let result: unknown;
      if ("proposalId" in snapshot) {
        if (snapshot.operation === "stock.source.propose") return invalid();
        const route = {
          "stock.source.approve": "/api/v1/inventory/sources/{id}/approve",
          "stock.source.reject": "/api/v1/inventory/sources/{id}/reject",
          "stock.source.withdraw": "/api/v1/inventory/sources/{id}/withdraw",
        } as const;
        result = data(await client.POST(route[snapshot.operation], { body: snapshot.body,
          params: { path: { id: snapshot.proposalId }, header: { ...header, "If-Match": '"' + snapshot.expectedVersion + '"' } } }));
      } else result = data(await client.POST("/api/v1/inventory/sources", { body: snapshot.body, params: { header } }));
      if (!isCurrent()) throw new AccessFailure(503, "commit_outcome_unknown");
      return sourceOutcome(result, snapshot);
    },
    async receipt(entry: OperationReference, location: AccessScope, signal?: AbortSignal) {
      const operation = sourceOperation(entry.operation);
      const header = { "X-Expected-Actor-Id": sourceActor(entry.actorId) };
      return sourceReceipt(data(await client.POST("/api/v1/inventory/sources/operations/query", {
        body: { purpose: "operation-recovery", location: sourceLocation(location), operation, key: sourceKey(entry.key) },
        params: { header }, signal,
      })), operation);
    },
  };
}
export type StockSourceGateway = ReturnType<typeof createStockSourceGateway>;
